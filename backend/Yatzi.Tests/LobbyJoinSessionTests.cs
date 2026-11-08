using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester atomisk tilmelding, bevarede spillere og ordnede beskedbatches gennem det
/// rigtige forbindelsesregister. Kontrollerede sockets/tasks styrer alle overlap.
/// </summary>
public class LobbyJoinSessionTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly GameSession _gameSession = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly List<ControlledWebSocket> _sockets = new();

    [Fact]
    public async Task Join_BindsStableIdentityAndSendsOnlyToCodeOwner()
    {
        var (first, firstSocket) = Connect();
        var (_, secondSocket) = Connect();
        var session = new LobbySession(_registry, _gameSession, () => 12);
        await session.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        var result = await session.JoinAsync("Andreas", "0012", _deadline.Token);

        Assert.Equal(LobbyJoinStatus.Joined, result.Status);
        var player = Assert.Single(result.State!.Players);
        Assert.NotEqual(Guid.Empty, player.Id);
        Assert.Equal("Andreas", player.Name);
        Assert.True(player.Connected);
        Assert.False(result.State.Started);
        Assert.True(_registry.TryGetPlayerId(first.ConnectionId, out var id));
        Assert.Equal(player.Id, id);
        Assert.Equal(id, Assert.Single(session.GetState().Players).Id);
        Assert.Equal(new[] { "PAIR 0012", "JOINED Andreas" }, Lines(firstSocket));
        Assert.Empty(secondSocket.Sent);
        Assert.Equal(LobbyJoinStatus.AlreadyJoined, (await session.JoinAsync("Andet navn", "0012")).Status);
        Assert.Equal(player, Assert.Single(session.GetState().Players));
    }

    [Fact]
    public async Task Join_PreservesOrderDuplicateNamesAndIndependentSnapshots()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        await ReadyAsync(session);
        await ReadyAsync(session);
        var first = (await session.JoinAsync("Andreas", "0001")).State!;
        var second = (await session.JoinAsync("Andreas", "0000")).State!;
        Assert.Equal(2, second.Players.Select(player => player.Id).Distinct().Count());
        Assert.Equal(first.Players[0], second.Players[0]);
        Assert.Single(first.Players);
        second.Players[0] = new(Guid.Empty, "Ændret kopi", false);
        Assert.Equal(first.Players[0], session.GetState().Players[0]);
    }

    [Fact]
    public async Task Join_UnknownOrAlreadyServerBoundConnectionDoesNotRegisterPlayer()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        Assert.Equal(LobbyJoinStatus.UnknownCode, (await session.JoinAsync("A", "0000")).Status);
        var (connection, socket) = await ReadyAsync(session);
        Guid existing = Guid.NewGuid();
        Assert.True(_registry.TryBindPlayer(connection.ConnectionId, existing));
        Assert.Equal(LobbyJoinStatus.ConnectionUnavailable, (await session.JoinAsync("A", "0000")).Status);
        Assert.Empty(session.GetState().Players);
        Assert.True(_registry.TryGetPlayerId(connection.ConnectionId, out var id));
        Assert.Equal(existing, id);
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "PAIR 0000" }, Lines(socket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_RetainsJoinedCodeAndFreesOnlyUnusedCode(bool remove)
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (joined, _) = await ReadyAsync(session);
        var accepted = await session.JoinAsync("A", "0000");
        var (unused, _) = await ReadyAsync(session);
        foreach (var connection in new[] { joined, unused })
            if (remove) Assert.True(_registry.Remove(connection.ConnectionId));
            else await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");

        Assert.Equal(accepted.State!.Players[0] with { Connected = false }, Assert.Single(session.GetState().Players));
        Assert.Equal(LobbyJoinStatus.AlreadyJoined, (await session.JoinAsync("A", "0000")).Status);
        Assert.Equal(LobbyJoinStatus.UnknownCode, (await session.JoinAsync("B", "0001")).Status);
        var (replacement, socket) = Connect();
        await session.HandleReadyAsync(replacement.ConnectionId, "0000", _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(accepted.State.Players[0], Assert.Single(session.GetState().Players));
    }

    [Fact]
    public async Task FullLobby_CountsDisconnectedPlayersAndDoesNotConsumeSeventhCode()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        for (int i = 0; i < LobbySession.MaxPlayers; i++)
        {
            var (connection, _) = await ReadyAsync(session);
            Assert.Equal(LobbyJoinStatus.Joined, (await session.JoinAsync("A", $"{i:D4}")).Status);
            _registry.Remove(connection.ConnectionId);
        }
        var (seventh, socket) = await ReadyAsync(session);
        Assert.Equal(LobbyJoinStatus.Full, (await session.JoinAsync("B", "0006")).Status);
        Assert.Equal(6, session.GetState().Players.Length);
        Assert.All(session.GetState().Players, player => Assert.False(player.Connected));
        Assert.False(_registry.TryGetPlayerId(seventh.ConnectionId, out _));
        Assert.Equal("PAIR 0006", Assert.Single(Lines(socket)));
    }

    [Fact]
    public async Task ConcurrentJoin_SameCodeCreatesExactlyOnePlayerAndJoinedMessage()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (_, socket) = await ReadyAsync(session);
        var start = Signal();
        var joins = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            await start.Task.WaitAsync(_deadline.Token);
            return await session.JoinAsync($"Spiller {i}", "0000", _deadline.Token);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(joins).WaitAsync(_deadline.Token);
        Assert.Single(results, result => result.Status == LobbyJoinStatus.Joined);
        Assert.Equal(15, results.Count(result => result.Status == LobbyJoinStatus.AlreadyJoined));
        Assert.Single(session.GetState().Players);
        Assert.Single(Lines(socket), line => line.StartsWith("JOINED "));
    }

    [Fact]
    public async Task ConcurrentJoin_LastPlaceAcceptsOnlyOneConnection()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        for (int i = 0; i < 7; i++) await ReadyAsync(session);
        for (int i = 0; i < 5; i++) await session.JoinAsync("A", $"{i:D4}");
        var start = Signal();
        var joins = new[] { "0005", "0006" }.Select(code => Task.Run(async () =>
        {
            await start.Task.WaitAsync(_deadline.Token);
            return await session.JoinAsync("B", code, _deadline.Token);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(joins).WaitAsync(_deadline.Token);
        Assert.Single(results, result => result.Status == LobbyJoinStatus.Joined);
        Assert.Single(results, result => result.Status == LobbyJoinStatus.Full);
        Assert.Equal(6, session.GetState().Players.Length);
        Assert.Equal(6, _registry.GetConnections().Count(connection => connection.State == OplaConnectionState.Paired));
    }

    [Fact]
    public async Task DisconnectDuringJoin_DoesNotLeaveHalfRegisteredPlayer()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, _) = await ReadyAsync(session);
        var start = Signal();
        var join = Task.Run(async () => { await start.Task; return await session.JoinAsync("A", "0000", _deadline.Token); });
        var close = Task.Run(async () => { await start.Task; _registry.Remove(connection.ConnectionId); });
        start.SetResult();
        await Task.WhenAll(join, close).WaitAsync(_deadline.Token);
        var result = await join;
        if (result.Status == LobbyJoinStatus.Joined)
            Assert.False(Assert.Single(session.GetState().Players).Connected);
        else
        {
            Assert.Contains(result.Status, new[] { LobbyJoinStatus.UnknownCode, LobbyJoinStatus.ConnectionUnavailable });
            Assert.Empty(session.GetState().Players);
        }
    }

    [Fact]
    public async Task BlockedJoined_WaitsForSendButAllowsReadsAndOtherConnections()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (_, firstSocket) = await ReadyAsync(session);
        var entered = Signal();
        var release = Signal();
        firstSocket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task<LobbyJoinResult> firstJoin = session.JoinAsync("A", "0000", _deadline.Token);
        try
        {
            await entered.Task.WaitAsync(_deadline.Token);
            Assert.Single(session.GetState().Players);
            await ReadyAsync(session);
            var secondJoin = await session.JoinAsync("B", "0001", _deadline.Token).WaitAsync(_deadline.Token);
            Assert.Equal(LobbyJoinStatus.Joined, secondJoin.Status);
            Assert.False(firstJoin.IsCompleted);
        }
        finally { release.TrySetResult(); await firstJoin.WaitAsync(_deadline.Token); }
        Assert.Equal(2, (await firstJoin).State!.Players.Length);
    }

    [Fact]
    public async Task OverlappingReadyAndJoin_PreserveBatchOrderAndStableIdentity()
    {
        var session = new LobbySession(_registry, _gameSession, () => 12);
        var (connection, socket) = Connect();
        var release = Signal();
        socket.SendAction = token => release.Task.WaitAsync(token);
        Task ready = session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await socket.SendStarted.Task.WaitAsync(_deadline.Token);
        Task<LobbyJoinResult> join = session.JoinAsync("A", "0012", _deadline.Token);
        Guid playerId = Assert.Single(session.GetState().Players).Id;
        Task repeated = session.HandleReadyAsync(connection.ConnectionId, "9999", _deadline.Token);
        try
        {
            Assert.False(join.IsCompleted);
            Assert.False(repeated.IsCompleted);
            Assert.Equal("PAIR 0012", Assert.Single(Lines(socket)));
        }
        finally { release.TrySetResult(); await Task.WhenAll(ready, join, repeated).WaitAsync(_deadline.Token); }
        Assert.Equal(new[] { "PAIR 0012", "JOINED A", "PAIR 0012", "JOINED A" }, Lines(socket));
        Assert.Equal(playerId, Assert.Single(session.GetState().Players).Id);
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("closed")]
    public async Task FailedJoined_PreservesPlayerReturnsSuccessAndClosesConnection(string failure)
    {
        var logs = new RecordingLoggerProvider(typeof(LobbySession).FullName!);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var session = new LobbySession(_registry, _gameSession, () => 0, loggerFactory.CreateLogger<LobbySession>());
        var (connection, socket) = await ReadyAsync(session);
        if (failure == "closed") await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        else socket.SendAction = _ => { if (failure == "io") throw new IOException(); throw new WebSocketException(); };
        var result = await session.JoinAsync("A", "0000", _deadline.Token);
        Assert.Equal(LobbyJoinStatus.Joined, result.Status);
        Assert.False(Assert.Single(result.State!.Players).Connected);
        Assert.True(connection.IsClosing);
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Entries).Level);
        Assert.Equal(failure == "closed" ? 1 : 2, socket.Sent.Count);
        Assert.Equal(LobbyJoinStatus.AlreadyJoined, (await session.JoinAsync("A", "0000")).Status);
        Assert.Single(session.GetState().Players);
    }

    [Fact]
    public async Task CancellationBeforeJoin_LeavesCodeAvailableWithoutBinding()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, socket) = await ReadyAsync(session);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.JoinAsync("A", "0000", stopped.Token));
        Assert.Empty(session.GetState().Players);
        Assert.False(_registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.Single(socket.Sent);
        Assert.Equal(LobbyJoinStatus.Joined, (await session.JoinAsync("A", "0000")).Status);
    }

    [Fact]
    public async Task CancellationDuringJoined_PreservesIdentityAndAllowsExplicitReadyReplay()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, socket) = await ReadyAsync(session);
        using var stopped = new CancellationTokenSource();
        socket.SendAction = token => { stopped.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.JoinAsync("A", "0000", stopped.Token));
        Guid playerId = Assert.Single(session.GetState().Players).Id;
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
        socket.SendAction = null;
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(playerId, Assert.Single(session.GetState().Players).Id);
    }

    [Fact]
    public async Task CancelledQueuedJoin_DoesNotLetLaterBatchOvertakePreviousReady()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, socket) = Connect();
        var release = Signal();
        socket.SendAction = token => release.Task.WaitAsync(token);
        Task ready = session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await socket.SendStarted.Task.WaitAsync(_deadline.Token);
        using var stopped = new CancellationTokenSource();
        Task<LobbyJoinResult> join = session.JoinAsync("A", "0000", stopped.Token);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join);
        Task replay = session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        try
        {
            Assert.False(replay.IsCompleted);
            Assert.Single(socket.Sent);
        }
        finally { release.TrySetResult(); await Task.WhenAll(ready, replay).WaitAsync(_deadline.Token); }
        Assert.Equal(new[] { "PAIR 0000", "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Single(session.GetState().Players);
    }

    [Fact]
    public async Task FailedPreviousBatch_DoesNotPoisonQueuedJoin()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, socket) = Connect();
        var release = Signal();
        socket.SendAction = async token => { await release.Task.WaitAsync(token); throw new WebSocketException(); };
        Task ready = session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await socket.SendStarted.Task.WaitAsync(_deadline.Token);
        Task<LobbyJoinResult> join = session.JoinAsync("A", "0000", _deadline.Token);
        socket.SendAction = null;
        release.SetResult();
        await Assert.ThrowsAsync<WebSocketException>(() => ready);
        Assert.Equal(LobbyJoinStatus.Joined, (await join.WaitAsync(_deadline.Token)).Status);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
    }

    [Fact]
    public async Task DisconnectAfterCommit_RetainsPlayerEvenWhenJoinedCannotFinishNormally()
    {
        var session = new LobbySession(_registry, _gameSession, () => 0);
        var (connection, socket) = await ReadyAsync(session);
        var entered = Signal();
        var release = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task<LobbyJoinResult> join = session.JoinAsync("A", "0000", _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        _registry.Remove(connection.ConnectionId);
        release.SetResult();
        var result = await join.WaitAsync(_deadline.Token);
        Assert.Equal(LobbyJoinStatus.Joined, result.Status);
        Assert.False(Assert.Single(result.State!.Players).Connected);
        Assert.Equal(LobbyJoinStatus.AlreadyJoined, (await session.JoinAsync("A", "0000")).Status);
    }

    private (OplaConnection Connection, ControlledWebSocket Socket) Connect()
    {
        Assert.True(_registry.TryReserve(out Guid id));
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        var connection = new OplaConnection(id, socket);
        Assert.True(_registry.TryAttach(connection));
        Assert.True(_registry.TryMarkReady(id));
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
