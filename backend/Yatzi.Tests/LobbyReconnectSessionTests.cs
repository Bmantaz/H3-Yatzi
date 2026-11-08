using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester bevarede spiller-/kodeposter på tværs af sockets. Tasks styrer forsinket
/// JOINED, konkurrerende overtagelser og lukning; styret tid udløser timeout.
/// </summary>
public class LobbyReconnectSessionTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly GameSession _gameSession = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly List<ControlledWebSocket> _sockets = new();

    [Theory]
    [InlineData("open")]
    [InlineData("closing")]
    [InlineData("removed")]
    public async Task Reconnect_PreservesIdentityNameAndOrderWithoutAnotherJoin(string situation)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, _) = await ReadyAsync(session);
        await session.JoinAsync("Andreas", "0000");
        await ReadyAsync(session);
        await session.JoinAsync("Bjarke", "0001");
        var before = session.GetState();
        if (situation == "closing") await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        if (situation == "removed") _registry.Remove(old.ConnectionId);
        var (current, socket) = Connect();

        await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);

        Assert.Equal(before.Players, session.GetState().Players);
        Assert.Equal(new[] { "PAIR 0000", "JOINED Andreas" }, Lines(socket));
        Assert.True(old.IsClosing);
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out var id));
        Assert.Equal(before.Players[0].Id, id);
        Assert.Equal(LobbyJoinStatus.AlreadyJoined, (await session.JoinAsync("Andreas", "0000")).Status);
    }

    [Fact]
    public async Task UnusedOpenCode_CanMoveBeforeJoinAndBindsOnlyNewConnection()
    {
        var session = new LobbySession(_registry, _gameSession, () => 12);
        var (old, oldSocket) = await ReadyAsync(session);
        var (current, socket) = Connect();
        await session.HandleReadyAsync(current.ConnectionId, "0012", _deadline.Token);
        Assert.Equal("PAIR 0012", Assert.Single(Lines(socket)));
        Assert.Empty(session.GetState().Players);
        Assert.Equal(OplaConnectionState.Ready, current.State);
        Assert.True(old.IsClosing);
        var joined = await session.JoinAsync("A", "0012", _deadline.Token);
        Assert.Equal(LobbyJoinStatus.Joined, joined.Status);
        Assert.Equal(new[] { "PAIR 0012", "JOINED A" }, Lines(socket));
        Assert.Equal("PAIR 0012", Assert.Single(Lines(oldSocket)));
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out var id));
        Assert.Equal(Assert.Single(joined.State!.Players).Id, id);
    }

    [Fact]
    public async Task LateOldCleanupAndReady_DoNotRemoveNewBindingOrIssueAnotherCode()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, oldSocket) = await ReadyAsync(session);
        await session.JoinAsync("A", "0000");
        var (current, socket) = Connect();
        await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        _registry.Remove(old.ConnectionId);
        await session.HandleReadyAsync(old.ConnectionId, "0000", _deadline.Token);
        await session.HandleReadyAsync(current.ConnectionId, "9999", _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(oldSocket));
        Assert.True(Assert.Single(session.GetState().Players).Connected);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownConnection_CannotSwitchToAnotherCodeOrPlayer(bool joined)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (first, socket) = await ReadyAsync(session);
        var (second, _) = await ReadyAsync(session);
        if (joined)
        {
            await session.JoinAsync("A", "0000");
            await session.JoinAsync("B", "0001");
        }
        var before = session.GetState();
        await session.HandleReadyAsync(first.ConnectionId, "0001", _deadline.Token);
        Assert.False(second.IsClosing);
        Assert.False(first.IsClosing);
        Assert.Equal(before.Players, session.GetState().Players);
        Assert.Equal(joined ? new[] { "PAIR 0000", "JOINED A", "PAIR 0000", "JOINED A" }
            : new[] { "PAIR 0000", "PAIR 0000" }, Lines(socket));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("closing")]
    [InlineData("removed")]
    public async Task UnknownOrExpiredUnusedCode_IssuesFreshPairWithoutPlayer(string situation)
    {
        int candidate = 0;
        var session = new LobbySession(_registry, _gameSession, () => candidate);
        var (old, _) = await ReadyAsync(session);
        if (situation == "closing") await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        if (situation == "removed") _registry.Remove(old.ConnectionId);
        candidate = 12;
        var (current, socket) = Connect();
        await session.HandleReadyAsync(current.ConnectionId, situation == "unknown" ? "9999" : "0000", _deadline.Token);
        Assert.Equal("PAIR 0012", Assert.Single(Lines(socket)));
        Assert.Empty(session.GetState().Players);
        Assert.False(_registry.TryGetPlayerId(current.ConnectionId, out _));
        if (situation == "unknown") Assert.False(old.IsClosing);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reserved")]
    [InlineData("connected")]
    [InlineData("closing")]
    [InlineData("removed")]
    [InlineData("paired")]
    public async Task InvalidRecipient_DoesNotStealExistingPlayer(string situation)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, _) = await ReadyAsync(session);
        await session.JoinAsync("A", "0000");
        var before = session.GetState();
        Guid targetId = Guid.NewGuid();
        ControlledWebSocket? socket = null;
        if (situation == "reserved") Assert.True(_registry.TryReserve(out targetId));
        else if (situation != "unknown")
        {
            var entry = Connect(ready: situation != "connected");
            targetId = entry.Connection.ConnectionId;
            socket = entry.Socket;
            if (situation == "closing") await entry.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
            if (situation == "removed") _registry.Remove(targetId);
            if (situation == "paired") Assert.True(_registry.TryBindPlayer(targetId, Guid.NewGuid()));
        }
        await session.HandleReadyAsync(targetId, "0000", _deadline.Token);
        Assert.Equal(before.Players, session.GetState().Players);
        Assert.False(old.IsClosing);
        if (socket is not null) Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task WaitingOldClose_DoesNotDelayNewMessagesOrOtherLobbyWork()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, oldSocket) = await ReadyAsync(session);
        await session.JoinAsync("A", "0000");
        var release = Signal();
        oldSocket.CloseAction = token => release.Task.WaitAsync(token);
        var (current, socket) = Connect();
        Task reconnect = session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        try
        {
            await oldSocket.CloseStarted.Task.WaitAsync(_deadline.Token);
            Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
            Assert.False(reconnect.IsCompleted);
            Assert.True(old.IsClosing);
            Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
            Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
            Assert.True(Assert.Single(session.GetState().Players).Connected);
            var (_, independent) = await ReadyAsync(session);
            Assert.Equal("PAIR 0001", Assert.Single(Lines(independent)));
        }
        finally { release.TrySetResult(); await reconnect.WaitAsync(_deadline.Token); }
    }

    [Fact]
    public async Task WaitingOldJoinedAndQueuedReady_DoNotDelayOrRedirectNewBatches()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, oldSocket) = await ReadyAsync(session);
        var entered = Signal();
        var release = Signal();
        oldSocket.SendAction = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new WebSocketException();
        };
        Task<LobbyJoinResult> join = session.JoinAsync("A", "0000", _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        Guid playerId = Assert.Single(session.GetState().Players).Id;
        Task oldReady = session.HandleReadyAsync(old.ConnectionId, null, _deadline.Token);
        var (current, socket) = Connect();
        Task reconnect = session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        try
        {
            Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
            Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out var actual));
            Assert.Equal(playerId, actual);
            Assert.False(reconnect.IsCompleted);
            Assert.False(join.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(join, oldReady, reconnect).WaitAsync(_deadline.Token); }
        Assert.Equal(LobbyJoinStatus.Joined, (await join).Status);
        Assert.True(Assert.Single((await join).State!.Players).Connected);
        Assert.False(current.IsClosing);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(oldSocket));
    }

    [Fact]
    public async Task ConcurrentTakeovers_LeaveOnlyLastAcceptedConnectionBound()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        await ReadyAsync(session);
        var joined = await session.JoinAsync("A", "0000");
        var replacements = new[] { Connect(), Connect() };
        var start = Signal();
        var tasks = replacements.Select(entry => Task.Run(async () =>
        {
            await start.Task.WaitAsync(_deadline.Token);
            await session.HandleReadyAsync(entry.Connection.ConnectionId, "0000", _deadline.Token);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(_deadline.Token);
        var winner = Assert.Single(replacements, entry => _registry.TryGetPlayerId(entry.Connection.ConnectionId, out _));
        Assert.Single(_registry.GetConnections(), connection => connection.State == OplaConnectionState.Paired);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(winner.Socket));
        Assert.Equal(joined.State!.Players, session.GetState().Players);
        foreach (var loser in _registry.GetConnections().Where(connection => connection.ConnectionId != winner.Connection.ConnectionId))
        {
            Assert.True(loser.IsClosing);
            _registry.Remove(loser.ConnectionId);
        }
        Assert.True(_registry.TryGetPlayerId(winner.Connection.ConnectionId, out _));
    }

    [Fact]
    public async Task ConcurrentJoinAndTakeover_UseOnePlayerOnNewConnection()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        await ReadyAsync(session);
        var (current, _) = Connect();
        var start = Signal();
        var join = Task.Run(async () => { await start.Task.WaitAsync(_deadline.Token); return await session.JoinAsync("A", "0000", _deadline.Token); });
        var reconnect = Task.Run(async () => { await start.Task.WaitAsync(_deadline.Token); await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token); });
        start.SetResult();
        await Task.WhenAll(join, reconnect).WaitAsync(_deadline.Token);
        Assert.Equal(LobbyJoinStatus.Joined, (await join).Status);
        var player = Assert.Single(session.GetState().Players);
        Assert.True(player.Connected);
        Assert.Equal("A", player.Name);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out var id));
        Assert.Equal(player.Id, id);
        Assert.Single(_registry.GetConnections(), connection => connection.State == OplaConnectionState.Paired);
    }

    [Fact]
    public async Task CancellationBeforeTakeover_PreservesOldConnectionAndAllowsLaterRetry()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, _) = await ReadyAsync(session);
        await session.JoinAsync("A", "0000");
        var (current, socket) = Connect();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.HandleReadyAsync(current.ConnectionId, "0000", stopped.Token));
        Assert.False(old.IsClosing);
        Assert.Empty(socket.Sent);
        Assert.True(_registry.TryGetPlayerId(old.ConnectionId, out _));
        await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        Assert.True(old.IsClosing);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
    }

    [Fact]
    public async Task CancellationAfterTakeover_DoesNotUndoTransferOrCancelOldClose()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, oldSocket) = await ReadyAsync(session);
        var joined = await session.JoinAsync("A", "0000");
        var release = Signal();
        oldSocket.CloseAction = token => release.Task.WaitAsync(token);
        var (current, socket) = Connect();
        using var stopped = new CancellationTokenSource();
        socket.SendAction = token => { stopped.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        Task reconnect = session.HandleReadyAsync(current.ConnectionId, "0000", stopped.Token);
        try
        {
            await oldSocket.CloseStarted.Task.WaitAsync(_deadline.Token);
            Assert.False(reconnect.IsCompleted);
            Assert.True(old.IsClosing);
            Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
            Assert.Equal(joined.State!.Players, session.GetState().Players);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect.WaitAsync(_deadline.Token));
        Assert.Equal(1, oldSocket.CloseCount);
        Assert.Equal("PAIR 0000", Assert.Single(Lines(socket)));
        socket.SendAction = null;
        await session.HandleReadyAsync(current.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "PAIR 0000", "JOINED A" }, Lines(socket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReconnectSend_DoesNotRestoreOldBindingOrAutomaticallyRetry(bool ioFailure)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, _) = await ReadyAsync(session);
        var joined = await session.JoinAsync("A", "0000");
        var (current, socket) = Connect();
        socket.SendAction = _ => { if (ioFailure) throw new IOException(); throw new WebSocketException(); };
        if (ioFailure)
            await Assert.ThrowsAsync<IOException>(() => session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token));
        else
            await Assert.ThrowsAsync<WebSocketException>(() => session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token));
        Assert.True(old.IsClosing);
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.Single(socket.Sent);
        Assert.Equal(joined.State!.Players, session.GetState().Players);
        socket.SendAction = null;
        await session.HandleReadyAsync(current.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "PAIR 0000", "JOINED A" }, Lines(socket));
    }

    [Fact]
    public async Task OldCloseTimeout_AbortsAfterFiveSecondsWithoutAffectingNewBinding()
    {
        var clock = new ControlledDeadlineTimeProvider();
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, oldSocket) = Connect(timeProvider: clock);
        await session.HandleReadyAsync(old.ConnectionId, null, _deadline.Token);
        await session.JoinAsync("A", "0000");
        oldSocket.CloseAction = token => Task.Delay(Timeout.Infinite, token);
        var (current, socket) = Connect();
        Task reconnect = session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        await oldSocket.CloseStarted.Task.WaitAsync(_deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.DueTime);
        clock.Expire();
        await reconnect.WaitAsync(_deadline.Token);
        Assert.Equal(1, oldSocket.AbortCount);
        Assert.Equal(0, oldSocket.DisposeCount);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.True(Assert.Single(session.GetState().Players).Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointSendFailure_CleansUpNewSocketButPreservesPlayerForLaterReconnect(bool ioFailure)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (old, _) = await ReadyAsync(session);
        Guid playerId = Assert.Single((await session.JoinAsync("A", "0000")).State!.Players).Id;
        using var socket = new ControlledWebSocket();
        OplaConnection? failedConnection = null;
        socket.ReceiveAction = (buffer, _) =>
        {
            failedConnection = Assert.Single(_registry.GetConnections(), connection => connection.ConnectionId != old.ConnectionId);
            byte[] ready = Encoding.UTF8.GetBytes("READY 0000");
            ready.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(ready.Length, WebSocketMessageType.Text, true));
        };
        socket.SendAction = _ => { if (ioFailure) throw new IOException(); throw new WebSocketException(); };
        var context = new DefaultHttpContext { RequestAborted = _deadline.Token };
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        using var lifetime = new TestApplicationLifetime();
        await OplaWebSocketEndpoints.HandleAsync(context, _registry, lifetime,
            NullLoggerFactory.Instance, TimeProvider.System, session,
            new OplaMessageHandler(_gameSession, NullLogger<OplaMessageHandler>.Instance), session).WaitAsync(_deadline.Token);

        Assert.NotNull(failedConnection);
        Assert.True(failedConnection.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, socket.DisposeCount);
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.False(Assert.Single(session.GetState().Players).Connected);
        var (current, currentSocket) = Connect();
        await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        Assert.Equal(playerId, Assert.Single(session.GetState().Players).Id);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(currentSocket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldReceiveInterruptedByTakeover_CompletesCleanupWithoutTouchingNewConnection(bool cancelled)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        using var oldSocket = new ControlledWebSocket();
        var receiving = Signal();
        int received = 0;
        oldSocket.ReceiveAction = async (buffer, token) =>
        {
            if (received++ == 0)
            {
                byte[] ready = Encoding.UTF8.GetBytes("READY");
                ready.CopyTo(buffer.Array!, buffer.Offset);
                return new WebSocketReceiveResult(ready.Length, WebSocketMessageType.Text, true);
            }
            receiving.TrySetResult();
            await oldSocket.CloseStarted.Task.WaitAsync(token);
            if (cancelled) throw new OperationCanceledException("Intern lukning");
            throw new ObjectDisposedException("Afsluttet modtagelse");
        };
        var context = new DefaultHttpContext { RequestAborted = _deadline.Token };
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(oldSocket)));
        using var lifetime = new TestApplicationLifetime();
        Task handling = OplaWebSocketEndpoints.HandleAsync(context, _registry, lifetime,
            NullLoggerFactory.Instance, TimeProvider.System, session,
            new OplaMessageHandler(_gameSession, NullLogger<OplaMessageHandler>.Instance), session);
        await receiving.Task.WaitAsync(_deadline.Token);
        var old = Assert.Single(_registry.GetConnections());
        await session.JoinAsync("A", "0000", _deadline.Token);
        var (current, socket) = Connect();
        await session.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        await handling.WaitAsync(_deadline.Token);
        Assert.True(old.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, oldSocket.DisposeCount);
        Assert.Single(_registry.GetConnections());
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.True(Assert.Single(session.GetState().Players).Connected);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
    }

    private (OplaConnection Connection, ControlledWebSocket Socket) Connect(bool ready = true, TimeProvider? timeProvider = null)
    {
        Assert.True(_registry.TryReserve(out Guid id));
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        var connection = new OplaConnection(id, socket, timeProvider: timeProvider);
        Assert.True(_registry.TryAttach(connection));
        if (ready) Assert.True(_registry.TryMarkReady(id));
        return (connection, socket);
    }

    private async Task<(OplaConnection Connection, ControlledWebSocket Socket)> ReadyAsync(LobbySession session)
    {
        var entry = Connect();
        await session.HandleReadyAsync(entry.Connection.ConnectionId, null, _deadline.Token);
        return entry;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string[] Lines(ControlledWebSocket socket) => socket.Sent.Select(Encoding.UTF8.GetString).ToArray();

    public void Dispose()
    {
        _deadline.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        _deadline.Dispose();
    }
}
