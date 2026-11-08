using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Styrer overlap mellem spilinput, lobbybatches og overtagelse med tasks.
/// Alle tests bruger det rigtige register, den rigtige handler og samme GameSession.
/// </summary>
public class LobbyGameInputTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly GameSession _game = new();
    private readonly LobbySession _lobby;
    private readonly OplaMessageHandler _handler;
    private readonly RecordingLoggerProvider _logs = new(typeof(LobbySession).FullName!);
    private readonly ILoggerFactory _logging;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly List<ControlledWebSocket> _sockets = new();

    public LobbyGameInputTests()
    {
        _logging = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _lobby = new(_registry, _game, () => 0, _logging.CreateLogger<LobbySession>());
        _handler = new(_game, NullLogger<OplaMessageHandler>.Instance);
    }

    [Fact]
    public async Task JoinedBeforeStart_CannotChangeExistingStandaloneGame()
    {
        _game.NewGame("Gammelt spil");
        _game.Roll();
        var (connection, socket, id) = await JoinAsync("A");
        string before = Snapshot();
        Assert.True(await InputAsync(connection, id, "ROLL"));
        Assert.True(await InputAsync(connection, id, "HOLD 1"));
        Assert.Equal(before, Snapshot());
        Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, Lines(socket));
        Assert.Equal(2, _logs.Entries.Count);
    }

    [Fact]
    public async Task RecheckRejectsWrongIdentityRemovedAndUnboundConnections()
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        string before = Snapshot();
        Assert.True(await InputAsync(connection, Guid.NewGuid(), "ROLL"));
        var (unbound, _) = Connect();
        Assert.True(await InputAsync(unbound, id, "ROLL"));
        _registry.Remove(connection.ConnectionId);
        Assert.True(await InputAsync(connection, id, "ROLL"));
        Assert.Equal(before, Snapshot());
        Assert.Equal(3, Lines(socket).Length);
        Assert.Equal(3, _logs.Entries.Count);
    }

    [Fact]
    public async Task JoinedStartAndInputBatches_RemainOrderedWithoutBlockingOtherWork()
    {
        var (first, socket) = Connect();
        await _lobby.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        var entered = Signal();
        var release = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task<LobbyJoinResult> join = _lobby.JoinAsync("A", "0000", _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        var (second, waiting, _) = await JoinAsync("B");
        var waitingSent = Signal();
        waiting.SendAction = _ => { waitingSent.TrySetResult(); return Task.CompletedTask; };
        var starting = _lobby.StartAsync(_deadline.Token);
        await waitingSent.Task.WaitAsync(_deadline.Token);
        Assert.Equal("WAIT A", Lines(waiting).Last());
        Guid id = _lobby.GetState().Players[0].Id;
        var rolling = InputAsync(first, id, "ROLL");
        var ready = _lobby.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        Assert.True(_lobby.GetState().Started);
        Assert.False(starting.IsCompleted);
        Assert.False(rolling.IsCompleted);
        Assert.False(ready.IsCompleted);
        // HTTP kan ændre spillet, men de allerede reserverede svar beholder deres kopi.
        _game.ToggleHold(1);
        await _lobby.HandleReadyAsync(second.ConnectionId, null, _deadline.Token);
        Assert.Equal("WAIT A", Lines(waiting).Last());
        release.SetResult();
        await Task.WhenAll(join, starting, rolling, ready).WaitAsync(_deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000", "STATE 1 00000",
            "PAIR 0000", "JOINED A", "STATE 1 00000" }, Lines(socket));
        Assert.True(_game.GetState()!.Held[1]);
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task BlockedReadyBatch_PrecedesLaterActionState()
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        var entered = Signal();
        var release = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var ready = _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        var roll = InputAsync(connection, id, "ROLL");
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        release.SetResult();
        await Task.WhenAll(ready, roll).WaitAsync(_deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000", "STATE 1 00000" }, Lines(socket).TakeLast(4));
    }

    [Fact]
    public async Task TakeoverDuringOldSend_PreservesActionAndDoesNotWaitOrCloseNewOwner()
    {
        var (old, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        var entered = Signal();
        var release = Signal();
        socket.SendAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new WebSocketException(); };
        var oldRoll = InputAsync(old, id, "ROLL");
        await entered.Task.WaitAsync(_deadline.Token);
        var (current, replacement) = Connect();
        var takeover = _lobby.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 1 00000" }, Lines(replacement));
        Assert.False(takeover.IsCompleted);
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.True(await InputAsync(old, id, "ROLL"));
        Assert.True(await InputAsync(current, id, "ROLL"));
        Assert.Equal("STATE 2 00000", Lines(replacement).Last());
        release.SetResult();
        Assert.False(await oldRoll.WaitAsync(_deadline.Token));
        await takeover.WaitAsync(_deadline.Token);
        _registry.Remove(old.ConnectionId);
        Assert.True(_registry.TryGetPlayerId(current.ConnectionId, out Guid actual));
        Assert.Equal(id, actual);
        Assert.False(current.IsClosing);
        Assert.Equal(0, replacement.CloseCount);
        Assert.Equal(2, _game.GetState()!.RollsUsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSend_DoesNotRetryOrPoisonLaterBatches(bool ioFailure)
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        socket.SendAction = _ => Task.FromException(ioFailure ? new IOException() : new WebSocketException());
        if (ioFailure) await Assert.ThrowsAsync<IOException>(() => InputAsync(connection, id, "ROLL"));
        else await Assert.ThrowsAsync<WebSocketException>(() => InputAsync(connection, id, "ROLL"));
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        Assert.Equal(4, Lines(socket).Length);
        // Endpointet ejer lukning ved fejl. Her testes køen isoleret: næste eksplicitte
        // READY må fortsætte, men ingen ny handling eller genafsendelse sker automatisk.
        socket.SendAction = null;
        await _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 1 00000" }, Lines(socket).TakeLast(3));
        Assert.Equal(1, _game.GetState()!.RollsUsed);
    }

    [Fact]
    public async Task CancelledQueuedReply_KeepsAcceptedRollAndLaterBatchStillWaitsForEarlierReady()
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        var entered = Signal();
        var release = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var firstReady = _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var roll = _lobby.HandleGameInputAsync(connection.ConnectionId, id, "ROLL", _handler, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => roll);
        var nextReady = _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.False(nextReady.IsCompleted);
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        release.SetResult();
        await Task.WhenAll(firstReady, nextReady).WaitAsync(_deadline.Token);
        Assert.Equal(new[] { "PAIR 0000", "JOINED A", "STATE 0 00000", "PAIR 0000", "JOINED A", "STATE 1 00000" }, Lines(socket).TakeLast(6));
    }

    [Fact]
    public async Task CancellationDuringSend_KeepsAcceptedActionWithoutRetry()
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        var entered = Signal();
        socket.SendAction = token => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); };
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var roll = _lobby.HandleGameInputAsync(connection.ConnectionId, id, "ROLL", _handler, stopped.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => roll);
        Assert.Equal(1, _game.GetState()!.RollsUsed);
        Assert.Equal(4, Lines(socket).Length);
        socket.SendAction = null;
        await _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal("STATE 1 00000", Lines(socket).Last());
    }

    [Fact]
    public async Task CancellationBeforeInput_PreservesGameAndQueue()
    {
        var (connection, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _lobby.HandleGameInputAsync(connection.ConnectionId, id, "ROLL", _handler, stopped.Token));
        Assert.Equal(0, _game.GetState()!.RollsUsed);
        Assert.Equal(3, Lines(socket).Length);
        Assert.True(await InputAsync(connection, id, "ROLL"));
    }

    [Fact]
    public async Task ConcurrentStartAndInput_EitherRejectsBeforeStartOrAcceptsAfterStart()
    {
        var (connection, socket, id) = await JoinAsync("A");
        var gate = Signal();
        var start = Task.Run(async () => { await gate.Task; return await _lobby.StartAsync(_deadline.Token); });
        var roll = Task.Run(async () => { await gate.Task; return await InputAsync(connection, id, "ROLL"); });
        gate.SetResult();
        await Task.WhenAll(start, roll).WaitAsync(_deadline.Token);
        Assert.Equal(LobbyStartStatus.Started, (await start).Status);
        int rolls = _game.GetState()!.RollsUsed;
        Assert.InRange(rolls, 0, 1);
        Assert.Equal(rolls == 0 ? new[] { "STATE 0 00000" } : new[] { "STATE 0 00000", "STATE 1 00000" }, Lines(socket).Skip(2));
    }

    [Fact]
    public async Task ConcurrentTakeoverAndInput_OnlyCurrentBindingCanContinue()
    {
        var (old, socket, id) = await JoinAsync("A");
        await _lobby.StartAsync();
        var (current, replacement) = Connect();
        var gate = Signal();
        var takeover = Task.Run(async () => { await gate.Task; await _lobby.HandleReadyAsync(current.ConnectionId, "0000", _deadline.Token); });
        var roll = Task.Run(async () => { await gate.Task; return await InputAsync(old, id, "ROLL"); });
        gate.SetResult();
        await Task.WhenAll(takeover, roll).WaitAsync(_deadline.Token);
        int rolls = _game.GetState()!.RollsUsed;
        Assert.InRange(rolls, 0, 1);
        Assert.Equal($"STATE {rolls} 00000", Lines(replacement).Last());
        Assert.True(await InputAsync(old, id, "ROLL"));
        Assert.Equal(rolls, _game.GetState()!.RollsUsed);
        Assert.True(await InputAsync(current, id, "ROLL"));
        Assert.Equal(rolls + 1, _game.GetState()!.RollsUsed);
        Assert.False(_registry.TryGetPlayerId(old.ConnectionId, out _));
    }

    private Task<bool> InputAsync(OplaConnection connection, Guid id, string line) =>
        _lobby.HandleGameInputAsync(connection.ConnectionId, id, line, _handler, _deadline.Token);

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

    private async Task<(OplaConnection Connection, ControlledWebSocket Socket, Guid Id)> JoinAsync(string name)
    {
        var (connection, socket) = Connect();
        await _lobby.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        string code = Lines(socket)[0][5..];
        var joined = await _lobby.JoinAsync(name, code, _deadline.Token);
        return (connection, socket, joined.State!.Players.Last().Id);
    }

    private string Snapshot() => JsonSerializer.Serialize(_game.GetState());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string[] Lines(ControlledWebSocket socket) => socket.Sent.Select(Encoding.UTF8.GetString).ToArray();
    public void Dispose()
    {
        _deadline.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        _logging.Dispose();
        _deadline.Dispose();
    }
}
