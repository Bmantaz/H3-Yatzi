using System.Text;
using System.Net.WebSockets;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Kontrollerer reservationer og præcis modtager uden netværk eller spiltilstand.</summary>
public class OplaConnectionRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrReservedIdentity_CannotBecomeReadyOrPaired(bool reserved)
    {
        var registry = new OplaConnectionRegistry();
        Guid id = Guid.NewGuid();
        if (reserved) Assert.True(registry.TryReserve(out id));
        Assert.False(registry.TryMarkReady(id));
        Assert.False(registry.TryBindPlayer(id, Guid.NewGuid()));
        Assert.False(registry.TryGetPlayerId(id, out var playerId));
        Assert.Equal(Guid.Empty, playerId);
    }

    [Fact]
    public void Binding_RequiresReadyAndNonemptyPlayerIdAndCannotReplaceAnotherPlayer()
    {
        var registry = new OplaConnectionRegistry();
        using var socket = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var id));
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Guid playerId = Guid.NewGuid();
        Assert.Equal(OplaConnectionState.Connected, connection.State);
        Assert.False(registry.TryBindPlayer(id, playerId));
        Assert.False(registry.TryGetPlayerId(id, out _));
        Assert.True(registry.TryMarkReady(id));
        Assert.True(registry.TryMarkReady(id));
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(registry.TryBindPlayer(id, Guid.Empty));
        Assert.False(registry.TryGetPlayerId(id, out _));
        Assert.True(registry.TryBindPlayer(id, playerId));
        Assert.True(registry.TryBindPlayer(id, playerId));
        Assert.False(registry.TryBindPlayer(id, Guid.NewGuid()));
        Assert.True(registry.TryMarkReady(id));
        Assert.Equal(OplaConnectionState.Paired, connection.State);
        Assert.True(registry.TryGetPlayerId(id, out var actual));
        Assert.Equal(playerId, actual);
    }

    [Fact]
    public async Task CompetingBindings_OnlyOnePlayerWins()
    {
        var registry = new OplaConnectionRegistry();
        using var socket = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var id));
        Assert.True(registry.TryAttach(new OplaConnection(id, socket)));
        Assert.True(registry.TryMarkReady(id));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            Guid playerId = Guid.NewGuid();
            await start.Task;
            return (PlayerId: playerId, Bound: registry.TryBindPlayer(id, playerId));
        }).ToArray();
        start.SetResult();
        var winner = Assert.Single(await Task.WhenAll(tasks), result => result.Bound);
        Assert.True(registry.TryGetPlayerId(id, out var actual));
        Assert.Equal(winner.PlayerId, actual);
    }

    [Fact]
    public async Task Closing_RevokesAccessBeforeWaitingForNetwork()
    {
        var registry = new OplaConnectionRegistry();
        using var socket = new ControlledWebSocket();
        var finishClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.CloseAction = token => finishClose.Task.WaitAsync(token);
        Assert.True(registry.TryReserve(out var id));
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        Assert.True(registry.TryBindPlayer(id, Guid.NewGuid()));
        var closing = connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OplaConnectionState.Closed, connection.State);
        Assert.False(registry.TryMarkReady(id));
        Assert.False(registry.TryBindPlayer(id, Guid.NewGuid()));
        Assert.False(registry.TryGetPlayerId(id, out var playerId));
        Assert.Equal(Guid.Empty, playerId);
        // Et andet registeropslag og en anden tilstandsændring kan stadig fuldføres.
        Assert.True(registry.TryReserve(out var otherId));
        using var otherSocket = new ControlledWebSocket();
        Assert.True(registry.TryAttach(new OplaConnection(otherId, otherSocket)));
        Assert.True(registry.TryMarkReady(otherId));
        finishClose.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(registry.Remove(id));
        Assert.False(registry.Remove(id));
    }

    [Fact]
    public async Task Removal_RevokesStaleReferenceWithoutNetworkAndDoesNotTouchNewBinding()
    {
        var registry = new OplaConnectionRegistry();
        using var oldSocket = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var oldId));
        var old = new OplaConnection(oldId, oldSocket);
        Assert.True(registry.TryAttach(old));
        Assert.True(registry.TryMarkReady(oldId));
        Guid playerId = Guid.NewGuid();
        Assert.True(registry.TryBindPlayer(oldId, playerId));
        Assert.True(registry.Remove(oldId));
        Assert.Equal(OplaConnectionState.Closed, old.State);
        Assert.False(await old.SendAsync("for sent"));
        Assert.False(registry.TryGetPlayerId(oldId, out _));
        Assert.Equal(0, oldSocket.CloseCount);
        Assert.Equal(0, oldSocket.DisposeCount);

        using var currentSocket = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var currentId));
        Assert.True(registry.TryAttach(new OplaConnection(currentId, currentSocket)));
        Assert.True(registry.TryMarkReady(currentId));
        Assert.True(registry.TryBindPlayer(currentId, playerId));
        Assert.False(registry.Remove(oldId));
        Assert.True(registry.TryGetPlayerId(currentId, out var actual));
        Assert.Equal(playerId, actual);
    }

    [Fact]
    public void Reserve_CountsPendingAcceptsAndRejectsThirteenth()
    {
        var registry = new OplaConnectionRegistry();
        var ids = new HashSet<Guid>();
        for (int i = 0; i < 12; i++)
        {
            Assert.True(registry.TryReserve(out var id));
            Assert.NotEqual(Guid.Empty, id);
            Assert.True(ids.Add(id));
            Assert.False(registry.TryGet(id, out _));
        }
        Assert.Equal(12, registry.Count);
        Assert.Empty(registry.GetConnections());
        Assert.False(registry.TryReserve(out var rejected));
        Assert.Equal(Guid.Empty, rejected);
    }

    [Fact]
    public async Task ParallelReservations_OnlyTwelveWin()
    {
        var registry = new OplaConnectionRegistry();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 48).Select(async _ =>
        {
            await start.Task;
            return registry.TryReserve(out var id) ? id : Guid.Empty;
        }).ToArray();
        start.SetResult();
        var accepted = (await Task.WhenAll(tasks)).Where(id => id != Guid.Empty).ToArray();
        Assert.Equal(12, accepted.Length);
        Assert.Equal(12, accepted.Distinct().Count());
        Assert.Equal(12, registry.Count);
    }

    [Fact]
    public void Attach_RequiresReservationAndCannotReplaceSession()
    {
        var registry = new OplaConnectionRegistry();
        using var socket = new ControlledWebSocket();
        Assert.False(registry.TryAttach(new OplaConnection(Guid.NewGuid(), socket)));
        Assert.True(registry.TryReserve(out var id));
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        var snapshot = registry.GetConnections();
        Assert.False(registry.TryAttach(new OplaConnection(id, socket)));
        Assert.True(registry.TryGet(id, out var found));
        Assert.Same(connection, found);
        Assert.True(registry.Remove(id));
        Assert.Same(connection, Assert.Single(snapshot));
        Assert.Empty(registry.GetConnections());
    }

    [Fact]
    public void RepeatedRemoval_DoesNotRemoveNewReservation()
    {
        var registry = new OplaConnectionRegistry();
        Assert.True(registry.TryReserve(out var oldId));
        Assert.True(registry.Remove(oldId));
        Assert.True(registry.TryReserve(out var newId));
        Assert.NotEqual(oldId, newId);
        Assert.False(registry.Remove(oldId));
        Assert.Equal(1, registry.Count);
        Assert.True(registry.Remove(newId));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Send_UsesExactSessionAndRejectsUnknownOrClosedRecipient()
    {
        var registry = new OplaConnectionRegistry();
        using var first = new ControlledWebSocket();
        using var second = new ControlledWebSocket();
        Assert.True(registry.TryReserve(out var firstId));
        Assert.True(registry.TryReserve(out var secondId));
        var firstConnection = new OplaConnection(firstId, first);
        Assert.True(registry.TryAttach(firstConnection));
        Assert.True(registry.TryAttach(new OplaConnection(secondId, second)));
        Assert.True(await registry.SendAsync(secondId, "anden"));
        Assert.Empty(first.Sent);
        Assert.Equal("anden", Encoding.UTF8.GetString(Assert.Single(second.Sent)));
        Assert.False(await registry.SendAsync(Guid.NewGuid(), "ukendt"));
        await firstConnection.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "test");
        Assert.False(await registry.SendAsync(firstId, "lukket"));
        Assert.Empty(first.Sent);
    }
}
