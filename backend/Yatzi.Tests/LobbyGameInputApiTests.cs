using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Følger spilleridentiteten gennem rigtig HTTP-join/start og WebSocket-spilinput.</summary>
public partial class LobbyApiTests
{
    [Theory]
    [InlineData("Development", 1)]
    [InlineData("Development", 6)]
    [InlineData("Production", 1)]
    [InlineData("Production", 6)]
    [InlineData("Staging", 1)]
    [InlineData("Staging", 6)]
    [InlineData("Test", 1)]
    [InlineData("Test", 6)]
    public async Task LobbyGameInput_OnlyActiveCurrentOwnerCanPlayInEveryEnvironment(string environment, int count)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var players = new List<(WebSocket Socket, OplaConnection Connection, string Code)>();
        for (int i = 0; i < count; i++) players.Add(await JoinOnFactoryAsync(factory, client, "Åse Øster"));
        var identities = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players;
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(players[0].Socket));
        foreach (var player in players.Skip(1)) Assert.Equal("WAIT Åse Øster", await ReceiveAsync(player.Socket));
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        foreach (var player in players.Skip(1))
        {
            foreach (string command in new[] { "ROLL", "HOLD 1" })
            {
                await SendAsync(player.Socket, command);
                Assert.Contains("Det er ikke denne spillers tur", (await _handlerLogs.ReadAsync(_deadline.Token)).Message);
            }
        }
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        var first = players[0];
        await SendAsync(first.Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
        var rolled = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        await SendAsync(first.Socket, "HOLD 3");
        Assert.Equal("STATE 1 00100", await ReceiveAsync(first.Socket));
        await SendAsync(first.Socket, "ROLL");
        Assert.Equal("STATE 2 00100", await ReceiveAsync(first.Socket));
        var second = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        Assert.Equal(rolled.Dice[2], second.Dice[2]);
        await SendAsync(first.Socket, "HOLD 3");
        Assert.Equal("STATE 2 00000", await ReceiveAsync(first.Socket));

        // Ny socket overtager samme spiller, før den gamle har afsluttet sit håndtryk.
        var current = await ConnectAsync(factory);
        await SendAsync(current, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED Åse Øster", await ReceiveAsync(current));
        Assert.Equal("STATE 2 00000", await ReceiveAsync(current));
        Assert.False(registry.TryGetPlayerId(first.Connection.ConnectionId, out _));
        Assert.Equal(WebSocketMessageType.Close,
            (await first.Socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await first.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        await first.Connection.Completion.WaitAsync(_deadline.Token);
        var currentConnection = Assert.Single(registry.GetConnections(), connection =>
            !players.Skip(1).Any(player => player.Connection.ConnectionId == connection.ConnectionId));
        Assert.True(registry.TryGetPlayerId(currentConnection.ConnectionId, out Guid currentId));
        Assert.Equal(identities[0].Id, currentId);
        Assert.Equal(identities, (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players);
        await SendAsync(current, "ROLL");
        Assert.Equal("STATE 3 00000", await ReceiveAsync(current));
        await SendAsync(current, "HOLD 1");
        Assert.Contains("hold", (await _handlerLogs.ReadAsync(_deadline.Token)).Message, StringComparison.OrdinalIgnoreCase);
        using var scored = await client.PostAsync("/api/game/strike/chance", null, _deadline.Token);
        scored.EnsureSuccessStatusCode();
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(current, command);
            Assert.Contains("Turen er afsluttet", (await _handlerLogs.ReadAsync(_deadline.Token)).Message);
        }
        var completed = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        Assert.True(completed.TurnCompleted);
        Assert.Equal(0, completed.CurrentPlayer);
        Assert.Equal(0, completed.Players[0].Scorecard.Scores["chance"]);
        Assert.All(completed.Players.Skip(1), player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
        using var reset = await client.PostAsJsonAsync("/api/game/new", new { name = "Nulstilling" }, _deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
        await CloseAsync(current, currentConnection);
        // Første resterende frame skal være Close: inaktive spillere fik ingen spilsvar.
        foreach (var player in players.Skip(1)) await CloseAsync(player.Socket, player.Connection);
        Assert.Empty(_lobbyLogs.Entries);
    }

    [Fact]
    public async Task ConcurrentHttpAndRealOplaRolls_AcceptExactlyThreeAndKeepResponseOrder()
    {
        using var client = _factory.CreateClient();
        var first = await JoinOnFactoryAsync(_factory, client, "A");
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        var requests = Enumerable.Range(0, 3).Select(_ => client.PostAsync("/api/game/roll", null, _deadline.Token)).ToArray();
        for (int i = 0; i < 3; i++) await SendAsync(first.Socket, "ROLL");
        await SendAsync(first.Socket, "READY");
        var rolls = new List<int>();
        string line;
        while (!(line = await ReceiveAsync(first.Socket)).StartsWith("PAIR ", StringComparison.Ordinal))
        {
            Assert.StartsWith("STATE ", line);
            rolls.Add(line[6] - '0');
        }
        Assert.Equal("PAIR " + first.Code, line);
        Assert.Equal("JOINED A", await ReceiveAsync(first.Socket));
        Assert.Equal("STATE 3 00000", await ReceiveAsync(first.Socket));
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            Assert.Equal(3, responses.Count(response => response.IsSuccessStatusCode) + rolls.Count);
            Assert.Equal(rolls.Order(), rolls);
            Assert.Equal(3, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await CloseAsync(first.Socket, first.Connection);
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task LobbyInputDeliveryFailure_EndpointCleansOnlyCapturedConnectionAndKeepsRoll(string failure)
    {
        using var client = _factory.CreateClient();
        using var lifetime = new TestApplicationLifetime();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var readySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        int received = 0;
        socket.ReceiveAction = async (buffer, token) =>
        {
            int frame = received++;
            if (frame == 1) await allowRoll.Task.WaitAsync(token);
            if (frame > 1) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            byte[] bytes = Encoding.UTF8.GetBytes(frame == 0 ? "READY" : "ROLL");
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        };
        socket.SendAction = _ => { readySent.TrySetResult(); return Task.CompletedTask; };
        var context = new DefaultHttpContext { RequestAborted = request.Token };
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        var handling = OplaWebSocketEndpoints.HandleAsync(context, registry, lifetime,
            _factory.Services.GetRequiredService<ILoggerFactory>(), TimeProvider.System, lobby,
            _factory.Services.GetRequiredService<OplaMessageHandler>(), lobby);
        await readySent.Task.WaitAsync(_deadline.Token);
        var connection = Assert.Single(registry.GetConnections());
        string code = Encoding.UTF8.GetString(socket.Sent.Single())[5..];
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        socket.SendAction = async token =>
        {
            sending.TrySetResult();
            if (failure is "cancel" or "stop") await Task.Delay(Timeout.Infinite, token);
            if (failure == "takeover") await release.Task.WaitAsync(token);
            if (failure == "io") throw new IOException();
            throw new WebSocketException();
        };
        allowRoll.SetResult();
        await sending.Task.WaitAsync(_deadline.Token);
        if (failure == "cancel") request.Cancel();
        if (failure == "stop") lifetime.StopApplication();
        WebSocket? current = null;
        if (failure == "takeover")
        {
            current = await ConnectAsync(_factory);
            await SendAsync(current, "READY " + code);
            Assert.Equal("PAIR " + code, await ReceiveAsync(current));
            Assert.Equal("JOINED A", await ReceiveAsync(current));
            Assert.Equal("STATE 1 00000", await ReceiveAsync(current));
            Assert.True(connection.IsClosing);
            release.SetResult();
        }
        await handling.WaitAsync(_deadline.Token);
        Assert.True(connection.Completion.IsCompletedSuccessfully);
        Assert.False(registry.TryGet(connection.ConnectionId, out _));
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        Assert.Equal(4, socket.Sent.Count); // PAIR, JOINED, start og ét forsøgt spilsvar.
        if (current is null)
        {
            current = await ConnectAsync(_factory);
            await SendAsync(current, "READY " + code);
            Assert.Equal("PAIR " + code, await ReceiveAsync(current));
            Assert.Equal("JOINED A", await ReceiveAsync(current));
            Assert.Equal("STATE 1 00000", await ReceiveAsync(current));
        }
        await SendAsync(current, "HOLD 2");
        Assert.Equal("STATE 1 01000", await ReceiveAsync(current));
        await CloseAsync(current, Assert.Single(registry.GetConnections()));
    }

    [Fact]
    public async Task NormalReconnect_RestoresActivePermissionAndWaitingPlayerRemainsRejected()
    {
        using var client = _factory.CreateClient();
        var first = await JoinOnFactoryAsync(_factory, client, "A");
        var second = await JoinOnFactoryAsync(_factory, client, "B");
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        Assert.Equal("WAIT A", await ReceiveAsync(second.Socket));
        await SendAsync(first.Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
        await CloseAsync(first.Socket, first.Connection);
        await CloseAsync(second.Socket, second.Connection);
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Assert.Equal(0, registry.Count);
        var active = await ConnectAsync(_factory);
        await SendAsync(active, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(active));
        Assert.Equal("JOINED A", await ReceiveAsync(active));
        Assert.Equal("STATE 1 00000", await ReceiveAsync(active));
        var activeConnection = Assert.Single(registry.GetConnections());
        await SendAsync(active, "HOLD 2");
        Assert.Equal("STATE 1 01000", await ReceiveAsync(active));
        var waiting = await ConnectAsync(_factory);
        await SendAsync(waiting, "READY " + second.Code);
        Assert.Equal("PAIR " + second.Code, await ReceiveAsync(waiting));
        Assert.Equal("JOINED B", await ReceiveAsync(waiting));
        Assert.Equal("WAIT A", await ReceiveAsync(waiting));
        await SendAsync(waiting, "ROLL");
        Assert.Contains("Det er ikke denne spillers tur", (await _handlerLogs.ReadAsync(_deadline.Token)).Message);
        Assert.Equal(1, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        await CloseAsync(waiting, Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != activeConnection.ConnectionId));
        await CloseAsync(active, activeConnection);
    }
}
