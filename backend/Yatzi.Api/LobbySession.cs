using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yatzi.Api;

/// <summary>
/// Ejer parringskoder, spillere og tilmeldingsrækkefølge i hukommelsen. READY udsteder
/// PAIR; HTTP-join binder serverens spiller-id og sender JOINED. Registeret ejer
/// transporten. READY med en kendt kode overtager samme tilknytning på en ny
/// forbindelse. Start opretter deltagernes spil og køer STATE/WAIT gennem registeret.
/// Spilinput koordineres med start/overtagelse og bruger samme ordnede beskedkø.
/// </summary>
public sealed class LobbySession : IOplaReadyHandler
{
    private const int CodeCount = 10000;
    public const int MaxPlayers = GameSession.MaxPlayers;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, LobbyEntry> _entries = new();
    private readonly List<LobbyEntry> _players = new();
    private readonly OplaConnectionRegistry _registry;
    private readonly GameSession _gameSession;
    private bool _started;
    private readonly Func<int> _nextCandidate;
    private readonly ILogger<LobbySession> _logger;

    /// <summary>Produktion bruger .NETs tilfældighedskilde uden en ny pakke eller indstilling.</summary>
    public LobbySession(OplaConnectionRegistry registry, GameSession gameSession, ILogger<LobbySession>? logger = null)
        : this(registry, gameSession, () => RandomNumberGenerator.GetInt32(CodeCount), logger)
    {
    }

    /// <summary>Gør kodekollisioner og foranstillede nuller reproducerbare i tests.</summary>
    /// <param name="registry">Samme transportregister som endpointet bruger.</param>
    /// <param name="gameSession">Samme spilsession som spil-API'et og handleren bruger.</param>
    /// <param name="nextCandidate">Leverer et starttal fra 0 til 9999 ved hver ny kode.</param>
    /// <param name="logger">Logger lobbyens afvisninger og leveringsfejl; tests kan udelade den.</param>
    /// <exception cref="ArgumentNullException">En afhængighed mangler.</exception>
    public LobbySession(OplaConnectionRegistry registry, GameSession gameSession, Func<int> nextCandidate, ILogger<LobbySession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gameSession);
        ArgumentNullException.ThrowIfNull(nextCandidate);
        _registry = registry;
        _gameSession = gameSession;
        _nextCandidate = nextCandidate;
        _logger = logger ?? NullLogger<LobbySession>.Instance;
    }

    /// <summary>
    /// Genbruger eller tildeler en kode og sender PAIR til præcis den READY-forbindelse.
    /// Transporten har allerede valideret READY-formatet. En ny forbindelse kan
    /// genfinde en kode; en allerede kendt forbindelse beholder altid sin tilknytning.
    /// </summary>
    /// <param name="connectionId">Transportens servergenererede forbindelses-id.</param>
    /// <param name="code">En eventuel klientkode; kan ikke ændre en eksisterende kodepost.</param>
    /// <param name="cancellationToken">Transportens kombinerede request-/stop-token.</param>
    /// <remarks>
    /// READY opretter ingen spiller eller spil. En tilmeldt forbindelse får også JOINED.
    /// Afsendelse afventes uden lobbylåsen.
    /// Sendesvigt genkører ikke kodeudstedelsen; undtagelser går til transportens fejlgrænse.
    /// </remarks>
    public async Task HandleReadyAsync(Guid connectionId, string? code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutgoingBatch batch;
        OplaConnection? replaced = null;
        lock (_sync)
        {
            // En annulleret operation må heller ikke ændre tilstanden efter ventetid på låsen.
            cancellationToken.ThrowIfCancellationRequested();
            RemoveClosedEntries();
            if (!_registry.TryGet(connectionId, out var connection) || connection.IsClosing
                || connection.State is not (OplaConnectionState.Ready or OplaConnectionState.Paired))
                return;

            if (!_entries.TryGetValue(connectionId, out var entry))
            {
                entry = code is null ? null : _entries.Values.FirstOrDefault(candidate => candidate.Code == code);
                if (entry is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = _registry.TryTakeOver(entry.ConnectionId, connectionId, entry.PlayerId, out replaced);
                    if (result == OplaTakeoverStatus.TargetUnavailable) return;

                    _entries.Remove(entry.ConnectionId);
                    if (result == OplaTakeoverStatus.SourceUnavailable)
                    {
                        // Den ubrugte kilde kan være lukket siden oprydningen.
                        // Koden er bortfaldet; den nye forbindelse får en ledig kode.
                        entry = null;
                    }
                    else
                    {
                        entry.ConnectionId = connectionId;
                        // Gamle batches beholder deres id og tasks. Den nye socket
                        // må ikke vente på den gamle sockets JOINED eller lukning.
                        entry.SendTail = Task.CompletedTask;
                        _entries.Add(connectionId, entry);
                    }
                }
                if (entry is null)
                {
                    string assignedCode = CreateCode();
                    cancellationToken.ThrowIfCancellationRequested();
                    entry = new LobbyEntry(connectionId, assignedCode);
                    _entries.Add(connectionId, entry);
                }
            }
            var lines = new List<string> { $"PAIR {entry.Code}" };
            if (entry.IsJoined)
            {
                lines.Add($"JOINED {entry.Name}");
                if (_started) lines.Add(GameReply(entry.PlayerId, _gameSession.GetState()!));
            }
            batch = QueueBatch(entry, lines.ToArray());
        }

        // Registeret finder kun denne modtager. En lukning efter kodeudstedelsen kan
        // give false; det udsteder ingen ny kode og flytter aldrig svaret til en anden socket.
        // Begge opgaver startes før afventning. PAIR/JOINED på den nye socket må
        // ikke vente på gammelt I/O. Lukning bruger sin egen femsekundersfrist,
        // også hvis requesten annulleres efter accepteret overtagelse.
        Task closing = replaced?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Opla-forbindelsen er overtaget.")
            ?? Task.CompletedTask;
        Task<bool> sending = SendBatchAsync(batch, cancellationToken);
        await Task.WhenAll(closing, sending);
    }

    /// <summary>Returnerer en kopi i tilmeldingsrækkefølge med aktuelle forbindelsesopslag.</summary>
    public LobbyStateResponse GetState()
    {
        lock (_sync)
            return new LobbyStateResponse(_players.Select(entry => new LobbyPlayerResponse(
                entry.PlayerId, entry.Name,
                _registry.TryGetPlayerId(entry.ConnectionId, out var id) && id == entry.PlayerId)).ToArray(), _started);
    }

    /// <summary>
    /// Koordinerer transportens spilinput med start, READY og overtagelse. Bindingen
    /// genkontrolleres under lobbylåsen, før handleren udfører handlingen under
    /// spilsessionslåsen. Et accepteret svar reserverer sin plads i lobbykøen før
    /// låsen slippes; netværket afventes bagefter. False afslutter modtagelsesløkken.
    /// </summary>
    /// <remarks>
    /// En allerede accepteret handling fortrydes eller genafspilles aldrig. Gamle
    /// batches beholder deres modtager, også hvis spilleren overtages under afsendelse.
    /// Serverbundne testforbindelser uden lobbytilknytning bevarer enkeltspillerforløbet.
    /// </remarks>
    /// <returns>True efter behandling eller afvisning; false når en svarmodtager ikke længere kan bruges.</returns>
    public async Task<bool> HandleGameInputAsync(Guid connectionId, Guid playerId, string line,
        OplaMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        OutgoingBatch? batch = null;
        IReadOnlyList<string> replies;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_registry.TryGetPlayerId(connectionId, out Guid currentId) || currentId != playerId)
            {
                _logger.LogWarning("Oplà-forbindelsen {ConnectionId} afviste beskeden: Spillerbindingen er ikke længere gyldig.", connectionId);
                return true;
            }

            var player = _players.FirstOrDefault(entry => entry.PlayerId == playerId);
            if (player is not null && (player.ConnectionId != connectionId || !_started))
            {
                _logger.LogWarning("Oplà-forbindelsen {ConnectionId} afviste beskeden: Lobbyspillet er ikke klar til spilinput.", connectionId);
                return true;
            }

            // Overtagelse kan ikke ligge mellem genkontrollen og handlingen.
            // Registerlåsen er frigivet før spilsessionslåsen tages af handleren.
            replies = handler.HandleLine(line, playerId, cancellationToken);
            if (replies.Count == 0) return true;
            if (player is not null) batch = QueueBatch(player, replies.ToArray());
        }

        if (batch is not null) return await SendBatchAsync(batch, cancellationToken);
        foreach (string reply in replies)
            if (!await _registry.SendAsync(connectionId, reply, cancellationToken)) return false;
        return true;
    }

    /// <summary>
    /// Tilmeld et valideret navn og en firecifret kode atomisk. En vellykket serverbinding
    /// er acceptpunktet; senere afbrydelse eller sendesvigt fjerner ikke spilleren.
    /// HTTP afventer JOINED-forsøget og får derefter et friskt lobbyøjebliksbillede.
    /// </summary>
    /// <remarks>Annullering før binding ændrer ingen deltager; senere annullering videresendes.</remarks>
    public async Task<LobbyJoinResult> JoinAsync(string name, string code, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutgoingBatch batch;
        OplaConnection connection;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_started) return new(LobbyJoinStatus.Started);
            RemoveClosedEntries();
            var entry = _entries.Values.FirstOrDefault(candidate => candidate.Code == code);
            if (entry is null) return new(LobbyJoinStatus.UnknownCode);
            if (entry.IsJoined) return new(LobbyJoinStatus.AlreadyJoined);
            if (_players.Count >= MaxPlayers) return new(LobbyJoinStatus.Full);

            Guid playerId = Guid.NewGuid();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_registry.TryGet(entry.ConnectionId, out var recipient)
                || !_registry.TryBindPlayer(entry.ConnectionId, playerId))
                return new(LobbyJoinStatus.ConnectionUnavailable);

            connection = recipient;
            entry.PlayerId = playerId;
            entry.Name = name;
            _players.Add(entry);
            batch = QueueBatch(entry, [$"JOINED {name}"]);
        }

        await SendCommittedBatchAsync(batch, connection, "JOINED", cancellationToken);
        return new(LobbyJoinStatus.Joined, GetState());
    }

    /// <summary>
    /// Starter 1–6 forbundne deltagere samlet. Lobbyen fryser rækkefølgen og kalder
    /// GameSession under låserækkefølgen lobby → spilsession. Batches reserveres
    /// før låsen slippes og sendes parallelt bagefter; HTTP får startøjebliksbilledet.
    /// Afvisning bevarer tidligere spil, og sendefejl fortryder ikke accepteret start.
    /// </summary>
    public async Task<LobbyStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameStateResponse state;
        var deliveries = new List<(OutgoingBatch Batch, OplaConnection Connection)>();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_started) return new(LobbyStartStatus.AlreadyStarted);
            if (_players.Count == 0) return new(LobbyStartStatus.NoPlayers);
            var connections = new List<OplaConnection>();
            foreach (var player in _players)
            {
                if (!_registry.TryGet(player.ConnectionId, out var connection)
                    || !_registry.TryGetPlayerId(player.ConnectionId, out var id) || id != player.PlayerId)
                    return new(LobbyStartStatus.Disconnected);
                connections.Add(connection);
            }

            state = _gameSession.NewLobbyGame(_players.Select(player => new LobbyGamePlayer(player.PlayerId, player.Name)).ToArray(), cancellationToken);
            _started = true;
            for (int i = 0; i < _players.Count; i++)
                deliveries.Add((QueueBatch(_players[i], [GameReply(_players[i].PlayerId, state)]), connections[i]));
        }

        // Alle opgaver startes, selv om en tidligere modtager venter eller fejler.
        // Connection er fanget før await; en senere overtagelse må ikke blive lukket.
        await Task.WhenAll(deliveries.Select(delivery =>
            SendCommittedBatchAsync(delivery.Batch, delivery.Connection, "Spillestart", cancellationToken)));
        return new(LobbyStartStatus.Started, state);
    }

    // Kun under lobbylåsen. STATE bruger samme formattering som spilhandleren.
    // Første deltager forbliver aktiv, også efter turafslutning, frem til #44.
    private string GameReply(Guid playerId, GameStateResponse state) =>
        _players[state.CurrentPlayer].PlayerId == playerId
            ? OplaMessageHandler.FormatState(state)
            : $"WAIT {state.Players[state.CurrentPlayer].Name}";

    private async Task SendCommittedBatchAsync(OutgoingBatch batch, OplaConnection connection,
        string operation, CancellationToken cancellationToken)
    {
        try
        {
            if (await SendBatchAsync(batch, cancellationToken)) return;
        }
        catch (WebSocketException) { }
        catch (IOException) { }
        await CloseFailedDeliveryAsync(connection, operation);
    }

    // Samme lukkevej som endpointet: adgang tilbagekaldes før netværksafventning.
    // Endpointet ejer stadig disposal og fjernelse fra registeret.
    private Task CloseFailedDeliveryAsync(OplaConnection connection, string operation)
    {
        _logger.LogWarning("{Operation} kunne ikke leveres til Oplà-forbindelsen {ConnectionId}. Den accepterede tilstand bevares.",
            operation, connection.ConnectionId);
        return connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, $"{operation} kunne ikke leveres.");
    }

    // Kun under lobbylåsen. Hver operation reserverer sin plads, men starter ingen I/O.
    // WhenAll bevarer rækkefølgen, også hvis en ventende operation annulleres før sin tur.
    private static OutgoingBatch QueueBatch(LobbyEntry entry, string[] lines)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new OutgoingBatch(entry.ConnectionId, lines, entry.SendTail, completed);
        entry.SendTail = Task.WhenAll(entry.SendTail, completed.Task);
        return batch;
    }

    private async Task<bool> SendBatchAsync(OutgoingBatch batch, CancellationToken cancellationToken)
    {
        try
        {
            await batch.Previous.WaitAsync(cancellationToken);
            foreach (string line in batch.Lines)
                if (!await _registry.SendAsync(batch.ConnectionId, line, cancellationToken)) return false;
            return true;
        }
        finally
        {
            // Fejl videresendes til kalderen, men må ikke forgifte afsendelseskøen.
            batch.Completed.TrySetResult();
        }
    }

    // Oprydning sker før hvert opslag, uden timer eller baggrundsopgave. En lukket
    // forbindelse er straks ugyldig i registeret; dens ubrugte kode frigives her.
    private void RemoveClosedEntries()
    {
        foreach (var entry in _entries.Values.ToArray())
        {
            if (!entry.IsJoined && (!_registry.TryGet(entry.ConnectionId, out var connection) || connection.IsClosing))
                _entries.Remove(entry.ConnectionId);
        }
    }

    // Kun kald under lobbylåsen: både kollisionstjek og den efterfølgende registrering
    // skal være atomiske. Lineær søgning undgår en uendelig løkke ved gentagne tilfældige tal.
    private string CreateCode()
    {
        int start = _nextCandidate();
        if (start is < 0 or >= CodeCount)
            throw new ArgumentOutOfRangeException(nameof(_nextCandidate), "Kodens starttal skal være mellem 0 og 9999.");

        for (int offset = 0; offset < CodeCount; offset++)
        {
            string candidate = ((start + offset) % CodeCount).ToString("D4", CultureInfo.InvariantCulture);
            if (!_entries.Values.Any(entry => entry.Code == candidate)) return candidate;
        }

        // 12 forbindelser og højst seks bevarede spillere opbruger ikke kodeområdet.
        // Søgningen har også en entydig afslutning, hvis grænserne senere ændres.
        throw new InvalidOperationException("Der er ingen ledige parringskoder.");
    }

    // Samme post bevares efter lukning, når en spiller er tilmeldt. Listen over
    // spillere refererer til posterne, men HTTP får kun selvstændige response-records.
    private sealed class LobbyEntry(Guid connectionId, string code)
    {
        public Guid ConnectionId { get; set; } = connectionId;
        public string Code { get; } = code;
        public Guid PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsJoined => PlayerId != Guid.Empty;
        public Task SendTail { get; set; } = Task.CompletedTask;
    }

    private sealed record OutgoingBatch(Guid ConnectionId, string[] Lines, Task Previous, TaskCompletionSource Completed);
}
