using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yatzi.Api;

/// <summary>
/// Repræsenterer én Oplà-forbindelse i transportlaget. Alle afsendelser bruger samme
/// lås, mens endpointet ejer modtagelse, lukning, disposal og fjernelse fra registeret.
/// READY og binding ændres gennem serverens register; lukning tilbagekalder adgang.
/// Klassen ejer ingen spillogik, parringskoder eller spillerdata fra lobbyen.
/// </summary>
public sealed class OplaConnection
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OplaConnection> _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _closeSync = new();
    private readonly object _stateSync = new();
    private OplaConnectionState _state = OplaConnectionState.Connected;
    private Guid _playerId;
    private Task? _closeTask;
    private int _closing;

    /// <summary>Serverens identitet for forbindelsen; den er ikke en parringskode.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Læsbar adgangstilstand; mutationer er serverinterne og trådsikre.</summary>
    public OplaConnectionState State
    {
        get { lock (_stateSync) return _state; }
    }

    /// <summary>True fra oprydningens start; nye afsendelser afvises derefter.</summary>
    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    /// <summary>Afsluttes først, når endpointet har disposed socketen og frigivet pladsen.</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Modtager en accepteret socket og dens reserverede identitet. TimeProvider
    /// gør lukkegrænsen testbar uden ventepauser; produktion bruger systemets tid.
    /// </summary>
    public OplaConnection(Guid connectionId, WebSocket socket,
        ILogger<OplaConnection>? logger = null, TimeProvider? timeProvider = null)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("Forbindelsen skal have en identitet.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(socket);

        ConnectionId = connectionId;
        _socket = socket;
        _logger = logger ?? NullLogger<OplaConnection>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Sender én UTF-8-tekstbesked uden at ændre teksten. Returnerer false, hvis
    /// forbindelsen lukker eller ikke længere er åben. Kalders annullering og
    /// netværksfejl under normal drift videresendes til endpointets fejlgrænse.
    /// </summary>
    public async Task<bool> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosing) return false;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (IsClosing || _socket.State != WebSocketState.Open) return false;
            byte[] payload = Utf8.GetBytes(text);
            await _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text,
                endOfMessage: true, cancellationToken);
            return true;
        }
        catch (WebSocketException) when (IsClosing)
        {
            return false;
        }
        catch (ObjectDisposedException) when (IsClosing)
        {
            return false;
        }
        catch (OperationCanceledException) when (IsClosing && !cancellationToken.IsCancellationRequested)
        {
            // Abort kan annullere socketens interne I/O uden at kalderen har annulleret.
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Endpointets fælles lukkevej. Nye afsendelser stoppes først. De fem sekunder
    /// omfatter både ventetid på en igangværende afsendelse og lukkehåndtrykket.
    /// Ved timeout eller netværksfejl afbrydes socketen; endpointet disposer den.
    /// </summary>
    public Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        // Adgang tilbagekaldes før afventning af en afsendelse eller modpartens
        // lukkehåndtryk. Tilstandslåsen holdes aldrig under netværksarbejdet.
        MarkClosed();
        // Flere oprydningskald afventer samme lukning frem for at disponere socketen
        // under et andet kalds igangværende lukkehåndtryk.
        lock (_closeSync) return _closeTask ??= CloseCoreAsync(status, description);
    }

    private async Task CloseCoreAsync(WebSocketCloseStatus status, string description)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
        bool lockTaken = false;
        try
        {
            await _sendLock.WaitAsync(deadline.Token);
            lockTaken = true;
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(status, description, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogWarning("Oplà-forbindelsen {ConnectionId} overskred lukketiden på fem sekunder.", ConnectionId);
            _socket.Abort();
        }
        catch (WebSocketException exception)
        {
            _logger.LogWarning("Oplà-forbindelsen {ConnectionId} kunne ikke lukkes normalt: {ErrorCode}.",
                ConnectionId, exception.WebSocketErrorCode);
            _socket.Abort();
        }
        catch (IOException)
        {
            _logger.LogWarning("Oplà-forbindelsen {ConnectionId} mistede transporten under lukning.", ConnectionId);
            _socket.Abort();
        }
        finally
        {
            if (lockTaken) _sendLock.Release();
        }
    }

    /// <summary>Registeret registrerer READY uden at nedgradere en eksisterende binding.</summary>
    internal bool TryMarkReady()
    {
        lock (_stateSync)
        {
            if (_state == OplaConnectionState.Closed) return false;
            if (_state == OplaConnectionState.Connected) _state = OplaConnectionState.Ready;
            return true;
        }
    }

    /// <summary>Servervalideret identitet bindes efter READY; en anden binding erstattes ikke.</summary>
    internal bool TryBindPlayer(Guid playerId)
    {
        lock (_stateSync)
        {
            if (playerId == Guid.Empty) return false;
            if (_state == OplaConnectionState.Paired) return _playerId == playerId;
            if (_state != OplaConnectionState.Ready) return false;
            _playerId = playerId;
            _state = OplaConnectionState.Paired;
            return true;
        }
    }

    /// <summary>Læser tilstand og identitet samlet, så lukning ikke efterlader adgang.</summary>
    internal bool TryGetPlayerId(out Guid playerId)
    {
        lock (_stateSync)
        {
            playerId = _state == OplaConnectionState.Paired ? _playerId : Guid.Empty;
            return playerId != Guid.Empty;
        }
    }

    /// <summary>Tilbagekalder adgang synkront, også når registeret fjerner sessionen.</summary>
    internal void MarkClosed()
    {
        lock (_stateSync)
        {
            _state = OplaConnectionState.Closed;
            _playerId = Guid.Empty;
            Volatile.Write(ref _closing, 1);
        }
    }

    // Låsen beholdes som et rent managed objekt: gamle SendAsync-kald kan stadig
    // vente på den under lukning. Den må derfor ikke disposes foran ventende kald.
    internal void MarkCompleted() => _completion.TrySetResult();
}
