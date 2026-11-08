using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Tester startens acceptpunkt og beskedbatches med rigtige sessionsobjekter.
/// Kontrollerede sockets/tasks styrer overlap uden faste ventepauser.
/// </summary>
public class LobbyStartSessionTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly GameSession _game = new();
    private readonly LobbySession _lobby;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly List<ControlledWebSocket> _sockets = new();

    public LobbyStartSessionTests() => _lobby = new(_registry, _game, () => 0);

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Start_InitializesAllPlayersAndSendsStateOrWaitInJoinOrder(int count)
    {
        for (int i = 0; i < count; i++) await JoinAsync($"Spiller {i}");
        var result = await _lobby.StartAsync(_deadline.Token);
        Assert.Equal(LobbyStartStatus.Started, result.Status);
        Assert.Equal(Enumerable.Range(0, count).Select(i => $"Spiller {i}"), result.State!.Players.Select(player => player.Name));
        Assert.Equal(JsonSerializer.Serialize(result.State), JsonSerializer.Serialize(_game.GetState()));
        Assert.True(_lobby.GetState().Started);
        for (int i = 0; i < count; i++)
            Assert.Equal(new[] { $"PAIR {i:D4}", $"JOINED Spiller {i}", i == 0 ? "STATE 0 00000" : "WAIT Spiller 0" }, Lines(_sockets[i]));
    }

    [Fact]
    public async Task EmptyLobby_DoesNotReplaceExistingGame()
    {
        var before = _game.NewGame("Selvstændigt");
        Assert.Equal(LobbyStartStatus.NoPlayers, (await _lobby.StartAsync()).Status);
        Assert.False(_lobby.GetState().Started);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(_game.GetState()));
    }

    [Fact]
    public async Task DuplicateDisplayNames_DoNotMergePlayersOrGiveBothActiveState()
    {
        await JoinAsync("Andreas");
        await JoinAsync("Andreas");
        var result = await _lobby.StartAsync(_deadline.Token);
        Assert.Equal(2, result.State!.Players.Length);
        Assert.Equal(2, _lobby.GetState().Players.Select(player => player.Id).Distinct().Count());
        Assert.Equal("STATE 0 00000", Lines(_sockets[0])[^1]);
        Assert.Equal("WAIT Andreas", Lines(_sockets[1])[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectedPlayer_RejectsStartWithoutChangingLobbyOrGame(bool remove)
    {
        var before = _game.NewGame("Selvstændigt");
        await JoinAsync("A");
        var (connection, _) = await JoinAsync("B");
        if (remove) _registry.Remove(connection.ConnectionId);
        else await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        Assert.Equal(LobbyStartStatus.Disconnected, (await _lobby.StartAsync()).Status);
        Assert.False(_lobby.GetState().Started);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(_game.GetState()));
        Assert.All(_sockets, socket => Assert.Equal(2, socket.Sent.Count));
    }

    [Fact]
    public async Task RepeatedStartAndJoinAfterStart_DoNotResetGameOrSendAgain()
    {
        await JoinAsync("A");
        await _lobby.StartAsync();
        _game.Roll();
        var before = JsonSerializer.Serialize(_game.GetState());
        Assert.Equal(LobbyStartStatus.AlreadyStarted, (await _lobby.StartAsync()).Status);
        Assert.Equal(LobbyJoinStatus.Started, (await _lobby.JoinAsync("B", "9999")).Status);
        Assert.Equal(before, JsonSerializer.Serialize(_game.GetState()));
        Assert.Equal(3, _sockets[0].Sent.Count);
    }

    [Fact]
    public async Task PendingJoined_PrecedesStateWhileOtherPlayersReceiveWaitAndHttpStateCanAdvance()
    {
        var (first, socket) = await ReadyAsync();
        var entered = Signal();
        var release = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task<LobbyJoinResult> join = _lobby.JoinAsync("A", "0000", _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        var (_, second) = await JoinAsync("B");
        var waitingSent = Signal();
        second.SendAction = _ => { waitingSent.TrySetResult(); return Task.CompletedTask; };
        Task<LobbyStartResult> start = _lobby.StartAsync(_deadline.Token);
        try
        {
            await waitingSent.Task.WaitAsync(_deadline.Token);
            Assert.True(_lobby.GetState().Started);
            Assert.False(start.IsCompleted);
            Assert.Equal("WAIT A", Lines(second)[^1]);
            Assert.Equal(2, _game.GetState()!.Players.Length);
            Assert.Throws<InvalidOperationException>(() => _game.NewGame("Overskrivning"));
            _game.Roll();
        }
        finally { release.TrySetResult(); await Task.WhenAll(join, start).WaitAsync(_deadline.Token); }
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000" }, Lines(socket));
        Assert.Equal(0, (await start).State!.RollsUsed);
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        await _lobby.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        Assert.Equal("STATE 1 00000", Lines(socket)[^1]);
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("closed")]
    public async Task FailedStartDelivery_PreservesStartAndStillSendsOtherPlayers(string failure)
    {
        var (first, socket) = await JoinAsync("A");
        var (_, second) = await JoinAsync("B");
        if (failure == "closed") await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        else socket.SendAction = _ => { if (failure == "io") throw new IOException(); throw new WebSocketException(); };
        var result = await _lobby.StartAsync(_deadline.Token);
        Assert.Equal(LobbyStartStatus.Started, result.Status);
        Assert.True(_lobby.GetState().Started);
        Assert.False(_lobby.GetState().Players[0].Connected);
        Assert.True(first.IsClosing);
        Assert.Equal("WAIT A", Lines(second)[^1]);
        Assert.Equal(2, result.State!.Players.Length);
        Assert.Equal(LobbyStartStatus.AlreadyStarted, (await _lobby.StartAsync()).Status);
        Assert.Equal(failure == "closed" ? 2 : 3, socket.Sent.Count);
    }

    [Fact]
    public async Task CancellationBeforeStart_PreservesLobbyAndAllowsLaterStart()
    {
        await JoinAsync("A");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _lobby.StartAsync(stopped.Token));
        Assert.False(_lobby.GetState().Started);
        Assert.Null(_game.GetState());
        Assert.Equal(2, _sockets[0].Sent.Count);
        Assert.Equal(LobbyStartStatus.Started, (await _lobby.StartAsync()).Status);
    }

    [Fact]
    public async Task CancellationDuringDelivery_PreservesGameAndOnlyExplicitReadyReplaysState()
    {
        var (first, socket) = await JoinAsync("A");
        await JoinAsync("B");
        using var stopped = new CancellationTokenSource();
        socket.SendAction = token => { stopped.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _lobby.StartAsync(stopped.Token));
        Assert.True(_lobby.GetState().Started);
        Assert.Equal(2, _game.GetState()!.Players.Length);
        Assert.Equal(3, socket.Sent.Count);
        Assert.Equal(2, _sockets[1].Sent.Count);
        Assert.Equal(LobbyStartStatus.AlreadyStarted, (await _lobby.StartAsync()).Status);
        socket.SendAction = null;
        await _lobby.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000", "PAIR 0000", "JOINED A", "STATE 0 00000" }, Lines(socket));
    }

    [Fact]
    public async Task LateStartSendFailure_ClosesOnlyOldConnectionAfterTakeover()
    {
        var (old, oldSocket) = await JoinAsync("A");
        var entered = Signal();
        var release = Signal();
        oldSocket.SendAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new WebSocketException(); };
        Task<LobbyStartResult> start = _lobby.StartAsync(_deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        var (current, socket) = Connect();
        Task reconnect = _lobby.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        try
        {
            Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000" }, Lines(socket));
            Assert.True(old.IsClosing);
            Assert.False(start.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(start, reconnect).WaitAsync(_deadline.Token); }
        Assert.Equal(LobbyStartStatus.Started, (await start).Status);
        Assert.False(current.IsClosing);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.True(_lobby.GetState().Players[0].Connected);
        Assert.Equal(0, socket.CloseCount);
        Assert.Equal(3, socket.Sent.Count);
    }

    [Fact]
    public async Task ConcurrentStarts_AcceptOnlyOneAndSendOneInitialBatch()
    {
        await JoinAsync("A");
        var gate = Signal();
        var starts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task.WaitAsync(_deadline.Token);
            return await _lobby.StartAsync(_deadline.Token);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(starts).WaitAsync(_deadline.Token);
        Assert.Single(results, result => result.Status == LobbyStartStatus.Started);
        Assert.Equal(7, results.Count(result => result.Status == LobbyStartStatus.AlreadyStarted));
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000" }, Lines(_sockets[0]));
    }

    [Fact]
    public async Task ConcurrentJoinAndStart_EitherIncludesPlayerOrRejectsJoin()
    {
        await JoinAsync("A");
        await ReadyAsync();
        var gate = Signal();
        var join = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); return await _lobby.JoinAsync("B", "0001", _deadline.Token); });
        var start = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); return await _lobby.StartAsync(_deadline.Token); });
        gate.SetResult();
        await Task.WhenAll(join, start).WaitAsync(_deadline.Token);
        Assert.Equal(LobbyStartStatus.Started, (await start).Status);
        bool included = (await join).Status == LobbyJoinStatus.Joined;
        Assert.Contains((await join).Status, new[] { LobbyJoinStatus.Joined, LobbyJoinStatus.Started });
        Assert.Equal(included ? 2 : 1, _game.GetState()!.Players.Length);
        Assert.Equal(included ? new[] { "PAIR 0001", "JOINED B", "WAIT A" } : new[] { "PAIR 0001" }, Lines(_sockets[1]));
    }

    [Fact]
    public async Task ConcurrentReconnectAndStart_InitializesOnePlayerOnCurrentConnection()
    {
        await JoinAsync("A");
        var (current, socket) = Connect();
        var gate = Signal();
        var reconnect = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); await _lobby.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token); });
        var start = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); return await _lobby.StartAsync(_deadline.Token); });
        gate.SetResult();
        await Task.WhenAll(reconnect, start).WaitAsync(_deadline.Token);
        Assert.Equal(LobbyStartStatus.Started, (await start).Status);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000" }, Lines(socket));
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.Single(_game.GetState()!.Players);
    }

    [Fact]
    public async Task ConcurrentDisconnectAndStart_EitherRejectsOrPreservesAcceptedStart()
    {
        var (connection, _) = await JoinAsync("A");
        var gate = Signal();
        var close = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); _registry.Remove(connection.ConnectionId); });
        var start = Task.Run(async () => { await gate.Task.WaitAsync(_deadline.Token); return await _lobby.StartAsync(_deadline.Token); });
        gate.SetResult();
        await Task.WhenAll(close, start).WaitAsync(_deadline.Token);
        var result = await start;
        Assert.Contains(result.Status, new[] { LobbyStartStatus.Started, LobbyStartStatus.Disconnected });
        Assert.Equal(result.Status == LobbyStartStatus.Started, _lobby.GetState().Started);
        Assert.Equal(result.Status == LobbyStartStatus.Started, _game.GetState() is not null);
        Assert.False(_lobby.GetState().Players[0].Connected);
    }

    [Fact]
    public async Task ReadyAfterHttpActions_RestoresActiveStateAndWaitWithoutRestartingCompletedTurn()
    {
        var (first, socket) = await JoinAsync("A");
        var (second, waiting) = await JoinAsync("B");
        await _lobby.StartAsync();
        _game.Roll();
        _game.ToggleHold(2);
        _game.Strike(ScoreCategory.Chance);
        string before = JsonSerializer.Serialize(_game.GetState());
        await _lobby.HandleReadyAsync(first.ConnectionId, "9999", _deadline.Token);
        await _lobby.HandleReadyAsync(second.ConnectionId, "0000", _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 1 00100" }, Lines(socket).TakeLast(3));
        Assert.Equal(new[] { "PAIR 0001", "JOINED B", "WAIT A" }, Lines(waiting).TakeLast(3));
        var (current, replacement) = Connect();
        await _lobby.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 1 00100" }, Lines(replacement));
        Assert.Equal(before, JsonSerializer.Serialize(_game.GetState()));
        Assert.Throws<InvalidOperationException>(() => _game.Roll());
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

    private async Task<(OplaConnection Connection, ControlledWebSocket Socket)> ReadyAsync()
    {
        var entry = Connect();
        await _lobby.HandleReadyAsync(entry.Connection.ConnectionId, null, _deadline.Token);
        return entry;
    }

    private async Task<(OplaConnection Connection, ControlledWebSocket Socket)> JoinAsync(string name)
    {
        var entry = await ReadyAsync();
        string code = Lines(entry.Socket)[0][5..];
        Assert.Equal(LobbyJoinStatus.Joined, (await _lobby.JoinAsync(name, code, _deadline.Token)).Status);
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
