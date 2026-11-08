using System.Net.WebSockets;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester registerets atomiske overtagelse uden netværksarbejde. Kun endpointet
/// må frigive plads og disponere sockets efter den synkrone tilbagekaldelse.
/// </summary>
public class OplaConnectionTakeoverTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly List<ControlledWebSocket> _sockets = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TakeOver_RevokesOldConnectionWithoutClosingDisposingOrRemovingIt(bool joined)
    {
        var (old, oldSocket) = Connect();
        var (current, _) = Connect();
        Guid playerId = joined ? Guid.NewGuid() : Guid.Empty;
        if (joined) Assert.True(_registry.TryBindPlayer(old.ConnectionId, playerId));

        Assert.Equal(OplaTakeoverStatus.TakenOver,
            _registry.TryTakeOver(old.ConnectionId, current.ConnectionId, playerId, out var replaced));
        Assert.Same(old, replaced);
        Assert.True(old.IsClosing);
        Assert.Equal(OplaConnectionState.Closed, old.State);
        Assert.False(_registry.TryMarkReady(old.ConnectionId));
        Assert.False(_registry.TryBindPlayer(old.ConnectionId, Guid.NewGuid()));
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.False(await old.SendAsync("for sent"));
        Assert.True(_registry.TryGet(old.ConnectionId, out var stillRegistered));
        Assert.Same(old, stillRegistered);
        Assert.Equal(2, _registry.Count);
        Assert.Equal(0, oldSocket.CloseCount);
        Assert.Equal(0, oldSocket.DisposeCount);
        Assert.False(old.Completion.IsCompleted);
        Assert.Equal(joined, _registry.TryGetPlayerId(current.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
        Assert.Equal(joined ? OplaConnectionState.Paired : OplaConnectionState.Ready, current.State);
    }

    [Theory]
    [InlineData("closing")]
    [InlineData("removed")]
    [InlineData("unknown")]
    public async Task JoinedPlayer_CanBeRestoredWithoutOpenSource(string situation)
    {
        var (old, _) = Connect();
        Guid playerId = Guid.NewGuid();
        Assert.True(_registry.TryBindPlayer(old.ConnectionId, playerId));
        if (situation == "closing") await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        else _registry.Remove(old.ConnectionId);
        Guid oldId = situation == "unknown" ? Guid.NewGuid() : old.ConnectionId;
        var (current, _) = Connect();
        Assert.Equal(OplaTakeoverStatus.TakenOver, _registry.TryTakeOver(oldId, current.ConnectionId, playerId, out _));
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reserved")]
    [InlineData("connected")]
    [InlineData("closing")]
    [InlineData("removed")]
    [InlineData("paired")]
    public async Task UnusedCode_RequiresOpenReadySource(string situation)
    {
        Guid oldId = Guid.NewGuid();
        OplaConnection? old = null;
        Guid externalId = Guid.NewGuid();
        if (situation == "reserved") Assert.True(_registry.TryReserve(out oldId));
        else if (situation != "unknown")
        {
            (old, _) = Connect(ready: situation != "connected");
            oldId = old.ConnectionId;
            if (situation == "closing") await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
            if (situation == "removed") _registry.Remove(oldId);
            if (situation == "paired") Assert.True(_registry.TryBindPlayer(oldId, externalId));
        }
        var (current, socket) = Connect();
        Assert.Equal(OplaTakeoverStatus.SourceUnavailable,
            _registry.TryTakeOver(oldId, current.ConnectionId, Guid.Empty, out var replaced));
        Assert.Null(replaced);
        Assert.Equal(OplaConnectionState.Ready, current.State);
        Assert.Empty(socket.Sent);
        if (situation == "paired")
        {
            Assert.False(old!.IsClosing);
            Assert.True(_registry.TryGetPlayerId(oldId, out var binding));
            Assert.Equal(externalId, binding);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reserved")]
    [InlineData("connected")]
    [InlineData("closing")]
    [InlineData("removed")]
    [InlineData("same-player")]
    [InlineData("other-player")]
    [InlineData("same-connection")]
    public async Task InvalidTarget_DoesNotRevokeOrChangeSource(string situation)
    {
        var (old, oldSocket) = Connect();
        Guid playerId = Guid.NewGuid();
        Assert.True(_registry.TryBindPlayer(old.ConnectionId, playerId));
        Guid targetId = Guid.NewGuid();
        if (situation == "same-connection") targetId = old.ConnectionId;
        else if (situation == "reserved") Assert.True(_registry.TryReserve(out targetId));
        else if (situation != "unknown")
        {
            var (target, _) = Connect(ready: situation != "connected");
            targetId = target.ConnectionId;
            if (situation == "closing") await target.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
            if (situation == "removed") _registry.Remove(targetId);
            if (situation is "same-player" or "other-player")
                Assert.True(_registry.TryBindPlayer(targetId, situation == "same-player" ? playerId : Guid.NewGuid()));
        }

        Assert.Equal(OplaTakeoverStatus.TargetUnavailable,
            _registry.TryTakeOver(old.ConnectionId, targetId, playerId, out var replaced));
        Assert.Null(replaced);
        Assert.False(old.IsClosing);
        Assert.True(_registry.TryGetPlayerId(old.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
        Assert.Equal(0, oldSocket.CloseCount);
    }

    [Fact]
    public void TakenOverSource_StillCountsTowardsConnectionLimitUntilRemoved()
    {
        var (old, _) = Connect();
        var (current, _) = Connect();
        for (int i = 2; i < OplaConnectionRegistry.MaxConnections; i++) Connect();
        Assert.Equal(OplaTakeoverStatus.TakenOver,
            _registry.TryTakeOver(old.ConnectionId, current.ConnectionId, Guid.Empty, out _));
        Assert.Equal(12, _registry.Count);
        Assert.False(_registry.TryReserve(out _));
        Assert.True(_registry.Remove(old.ConnectionId));
        Assert.True(_registry.TryReserve(out _));
    }

    private (OplaConnection Connection, ControlledWebSocket Socket) Connect(bool ready = true)
    {
        Assert.True(_registry.TryReserve(out Guid id));
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        var connection = new OplaConnection(id, socket);
        Assert.True(_registry.TryAttach(connection));
        if (ready) Assert.True(_registry.TryMarkReady(id));
        return (connection, socket);
    }

    public void Dispose()
    {
        foreach (var socket in _sockets) socket.Dispose();
    }
}
