using System.Diagnostics.CodeAnalysis;

namespace Yatzi.Api;

/// <summary>
/// Singleton-register for Oplà-transporten. En null-værdi reserverer plads før
/// WebSocket-accept; derefter tilknyttes sessionen. Låsen beskytter registeret og
/// korte kald til sessionens tilstandslås. Ingen af låsene holdes under netværksarbejde.
/// Registeret ejer ikke socketernes disposal.
/// </summary>
public sealed class OplaConnectionRegistry
{
    public const int MaxConnections = 12;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, OplaConnection?> _connections = new();

    /// <summary>Antal accepterede forbindelser og igangværende reservationer samlet.</summary>
    public int Count
    {
        get { lock (_sync) return _connections.Count; }
    }

    /// <summary>Reserverer atomisk en plads og identitet; false ved fuldt register.</summary>
    public bool TryReserve(out Guid connectionId)
    {
        lock (_sync)
        {
            connectionId = Guid.Empty;
            if (_connections.Count >= MaxConnections) return false;
            do { connectionId = Guid.NewGuid(); } while (_connections.ContainsKey(connectionId));
            _connections.Add(connectionId, null);
            return true;
        }
    }

    /// <summary>Tilknytter én session til dens eksisterende reservation, højst én gang.</summary>
    public bool TryAttach(OplaConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_sync)
        {
            if (!_connections.TryGetValue(connection.ConnectionId, out var existing)
                || existing is not null || connection.IsClosing)
                return false;
            _connections[connection.ConnectionId] = connection;
            return true;
        }
    }

    /// <summary>Finder en accepteret session. En reservation alene giver ingen modtager.</summary>
    public bool TryGet(Guid connectionId, [NotNullWhen(true)] out OplaConnection? connection)
    {
        lock (_sync)
            return _connections.TryGetValue(connectionId, out connection) && connection is not null;
    }

    /// <summary>Returnerer en selvstændig liste over accepterede sessioner, uden reservationer.</summary>
    public IReadOnlyList<OplaConnection> GetConnections()
    {
        lock (_sync) return _connections.Values.OfType<OplaConnection>().ToArray();
    }

    /// <summary>Registrerer READY for en accepteret, åben session; giver ingen adgang alene.</summary>
    public bool TryMarkReady(Guid connectionId)
    {
        lock (_sync)
            return _connections.TryGetValue(connectionId, out var connection)
                && connection is not null && connection.TryMarkReady();
    }

    /// <summary>
    /// #59 leverer en servervalideret identitet efter READY. Tomt id, manglende
    /// session, lukning eller forsøg på at erstatte en anden binding giver false.
    /// Dette er en serveroperation, aldrig en klientkommando eller HTTP-rute.
    /// </summary>
    public bool TryBindPlayer(Guid connectionId, Guid playerId)
    {
        lock (_sync)
            return _connections.TryGetValue(connectionId, out var connection)
                && connection is not null && connection.TryBindPlayer(playerId);
    }

    /// <summary>Læsbar adgangskontrol: kun en registreret, åben, parret session giver et spiller-id.</summary>
    public bool TryGetPlayerId(Guid connectionId, out Guid playerId)
    {
        lock (_sync)
        {
            playerId = Guid.Empty;
            return _connections.TryGetValue(connectionId, out var connection)
                && connection is not null && connection.TryGetPlayerId(out playerId);
        }
    }

    /// <summary>
    /// Finder den præcise modtager under låsen og sender derefter uden låsen.
    /// En ukendt eller endnu ikke accepteret identitet giver false, aldrig en anden modtager.
    /// </summary>
    public Task<bool> SendAsync(Guid connectionId, string text, CancellationToken cancellationToken = default) =>
        TryGet(connectionId, out var connection)
            ? connection.SendAsync(text, cancellationToken)
            : Task.FromResult(false);

    /// <summary>
    /// Endpointet frigiver reservationen efter acceptfejl eller afsluttet oprydning.
    /// Identiteten genbruges ikke, så et gentaget kald ikke kan fjerne en ny forbindelse.
    /// </summary>
    public bool Remove(Guid connectionId)
    {
        lock (_sync)
        {
            if (!_connections.Remove(connectionId, out var connection)) return false;
            // Kun synkron tilbagekaldelse under låsen. Endpointet ejer fortsat
            // netværkslukning og disposal, også hvis andre har en gammel reference.
            connection?.MarkClosed();
            return true;
        }
    }
}
