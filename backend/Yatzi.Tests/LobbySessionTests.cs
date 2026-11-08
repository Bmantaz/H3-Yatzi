using System.Net.WebSockets;
using System.Text;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester kodeudstedelse gennem det rigtige register og kontrollerede sockets.
/// Styrede starttal og tasks gør kollisioner, afsendelsesfejl og samtidighed reproducerbare.
/// </summary>
public class LobbySessionTests : IDisposable
{
    private readonly OplaConnectionRegistry _registry = new();
    private readonly GameSession _gameSession = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly List<ControlledWebSocket> _sockets = new();

    [Theory]
    [InlineData(0, "0000")]
    [InlineData(12, "0012")]
    [InlineData(9999, "9999")]
    public async Task Ready_SendsFourDigitsWithoutBinding(int candidate, string code)
    {
        var (connection, socket) = Connect();
        var session = new LobbySession(_registry, _gameSession, () => candidate);

        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);

        Assert.Equal("PAIR " + code, Assert.Single(Lines(socket)));
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(_registry.TryGetPlayerId(connection.ConnectionId, out _));
    }

    [Fact]
    public async Task RepeatedReady_PreservesCodeAndExistingServerBinding()
    {
        var (connection, socket) = Connect();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () => { generated++; return 12; });
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Guid playerId = Guid.NewGuid();
        Assert.True(_registry.TryBindPlayer(connection.ConnectionId, playerId));

        foreach (string? supplied in new string?[] { null, "0012", "9999" })
            await session.HandleReadyAsync(connection.ConnectionId, supplied, _deadline.Token);

        Assert.Equal(1, generated);
        Assert.Equal(Enumerable.Repeat("PAIR 0012", 4), Lines(socket));
        Assert.True(_registry.TryGetPlayerId(connection.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
    }

    [Theory]
    [InlineData(12, "0012", "0013")]
    [InlineData(9999, "9999", "0000")]
    public async Task Collision_FindsNextCodeForNewUnrecognizedConnection(int candidate, string firstCode, string nextCode)
    {
        var (first, firstSocket) = Connect();
        var (second, secondSocket) = Connect();
        var session = new LobbySession(_registry, _gameSession, () => candidate);
        await session.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);

        // Uden en genkendelseskode skal en ny forbindelse have sin egen ledige kode.
        await session.HandleReadyAsync(second.ConnectionId, null, _deadline.Token);
        await session.HandleReadyAsync(first.ConnectionId, nextCode, _deadline.Token);

        Assert.Equal(new[] { "PAIR " + firstCode, "PAIR " + firstCode }, Lines(firstSocket));
        Assert.Equal("PAIR " + nextCode, Assert.Single(Lines(secondSocket)));
        Assert.False(_registry.TryGetPlayerId(first.ConnectionId, out _));
        Assert.False(_registry.TryGetPlayerId(second.ConnectionId, out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reserved")]
    [InlineData("connected")]
    [InlineData("closing")]
    [InlineData("removed")]
    public async Task InactiveConnection_DoesNotAllocateOrSend(string situation)
    {
        Guid id = Guid.NewGuid();
        ControlledWebSocket? socket = null;
        if (situation == "reserved") Assert.True(_registry.TryReserve(out id));
        else if (situation != "unknown")
        {
            var entry = Connect(ready: situation != "connected");
            id = entry.Connection.ConnectionId;
            socket = entry.Socket;
            if (situation == "closing")
                await entry.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
            if (situation == "removed") Assert.True(_registry.Remove(id));
        }
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () => { generated++; return 12; });

        await session.HandleReadyAsync(id, null, _deadline.Token);

        Assert.Equal(0, generated);
        if (socket is not null) Assert.Empty(socket.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_ReusesClosedCodeAndPreservesOtherConnection(bool remove)
    {
        var (first, _) = Connect();
        var (second, secondSocket) = Connect();
        var session = new LobbySession(_registry, _gameSession, () => 0);
        await session.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        await session.HandleReadyAsync(second.ConnectionId, null, _deadline.Token);
        if (remove) Assert.True(_registry.Remove(first.ConnectionId));
        else await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");

        var (third, thirdSocket) = Connect();
        await session.HandleReadyAsync(third.ConnectionId, null, _deadline.Token);
        await session.HandleReadyAsync(second.ConnectionId, null, _deadline.Token);

        Assert.Equal("PAIR 0000", Assert.Single(Lines(thirdSocket)));
        Assert.Equal(new[] { "PAIR 0001", "PAIR 0001" }, Lines(secondSocket));
    }

    [Fact]
    public async Task ConcurrentConnections_GetDistinctCodesEvenWithSameCandidate()
    {
        var entries = Enumerable.Range(0, OplaConnectionRegistry.MaxConnections).Select(_ => Connect()).ToArray();
        var session = new LobbySession(_registry, _gameSession, () => 9999);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = entries.Select(entry => Task.Run(async () =>
        {
            await start.Task.WaitAsync(_deadline.Token);
            await session.HandleReadyAsync(entry.Connection.ConnectionId, null, _deadline.Token);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(pending).WaitAsync(_deadline.Token);

        var lines = entries.Select(entry => Assert.Single(Lines(entry.Socket))).ToArray();
        Assert.Equal(entries.Length, lines.Distinct().Count());
        Assert.All(lines, line => Assert.Matches("^PAIR [0-9]{4}$", line));
    }

    [Fact]
    public async Task ConcurrentReadyOnSameConnection_AllocatesOnlyOnceAndSerializesSends()
    {
        var (connection, socket) = Connect();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () => { generated++; return 12; });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token)))).WaitAsync(_deadline.Token);

        Assert.Equal(1, generated);
        Assert.Equal(Enumerable.Repeat("PAIR 0012", 12), Lines(socket));
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task WaitingSend_DoesNotHoldLobbyLockOrBlockOtherConnection()
    {
        var (first, firstSocket) = Connect();
        var (second, secondSocket) = Connect();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstSocket.SendAction = token => release.Task.WaitAsync(token);
        var session = new LobbySession(_registry, _gameSession, () => 12);
        Task blocked = session.HandleReadyAsync(first.ConnectionId, null, _deadline.Token);
        try
        {
            await firstSocket.SendStarted.Task.WaitAsync(_deadline.Token);
            await session.HandleReadyAsync(second.ConnectionId, null, _deadline.Token).WaitAsync(_deadline.Token);
            Assert.Equal("PAIR 0013", Assert.Single(Lines(secondSocket)));
            Assert.False(blocked.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await blocked.WaitAsync(_deadline.Token);
        }
    }

    [Fact]
    public async Task CancellationBeforeHandling_DoesNotAllocateOrSend()
    {
        var (connection, socket) = Connect();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () => { generated++; return 12; });
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.HandleReadyAsync(connection.ConnectionId, null, stopped.Token));
        Assert.Equal(0, generated);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task CancellationDuringGeneration_DoesNotRegisterCode()
    {
        var (connection, socket) = Connect();
        using var stopped = new CancellationTokenSource();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () =>
        {
            if (++generated == 1) stopped.Cancel();
            return 12;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.HandleReadyAsync(connection.ConnectionId, null, stopped.Token));
        Assert.Empty(socket.Sent);
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(2, generated);
        Assert.Equal("PAIR 0012", Assert.Single(Lines(socket)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSend_PropagatesWithoutRetryAndKeepsAssignedCode(bool cancel)
    {
        var (connection, socket) = Connect();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () => { generated++; return 12; });
        using var stopped = new CancellationTokenSource();
        socket.SendAction = token =>
        {
            if (cancel)
            {
                stopped.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.HandleReadyAsync(connection.ConnectionId, null, stopped.Token));
        else
            await Assert.ThrowsAsync<WebSocketException>(() => session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token));
        Assert.Equal(1, generated);
        Assert.Single(socket.Sent);

        socket.SendAction = null;
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(1, generated);
        Assert.Equal(new[] { "PAIR 0012", "PAIR 0012" }, Lines(socket));
    }

    [Fact]
    public async Task ConnectionRemovedBeforeSend_DoesNotRetryOrChooseAnotherReceiver()
    {
        var (connection, socket) = Connect();
        var (_, otherSocket) = Connect();
        int generated = 0;
        var session = new LobbySession(_registry, _gameSession, () =>
        {
            generated++;
            Assert.True(_registry.Remove(connection.ConnectionId));
            return 12;
        });
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        await session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token);
        Assert.Equal(1, generated);
        Assert.Empty(socket.Sent);
        Assert.Empty(otherSocket.Sent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public async Task InvalidGeneratorValue_DoesNotSend(int candidate)
    {
        var (connection, socket) = Connect();
        var session = new LobbySession(_registry, _gameSession, () => candidate);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.HandleReadyAsync(connection.ConnectionId, null, _deadline.Token));
        Assert.Empty(socket.Sent);
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

    private static string[] Lines(ControlledWebSocket socket) => socket.Sent.Select(Encoding.UTF8.GetString).ToArray();

    public void Dispose()
    {
        _deadline.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        _deadline.Dispose();
    }
}
