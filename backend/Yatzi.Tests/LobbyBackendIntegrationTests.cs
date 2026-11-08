using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerer lobbyens samspil med transportgrænser, kapacitet og ny serverinstans.
/// Alle identiteter oprettes gennem HTTP-join; kun tid og kodekandidat styres i tests.
/// </summary>
public partial class LobbyApiTests
{
    [Theory]
    [InlineData("rate", WebSocketCloseStatus.PolicyViolation)]
    [InlineData("unicode", WebSocketCloseStatus.MessageTooBig)]
    [InlineData("bytes", WebSocketCloseStatus.MessageTooBig)]
    [InlineData("binary", WebSocketCloseStatus.InvalidMessageType)]
    [InlineData("utf8", WebSocketCloseStatus.InvalidPayloadData)]
    public async Task LobbyTransportViolation_PreservesGameAndAllowsSamePlayerToReconnect(string violation, WebSocketCloseStatus expected)
    {
        var clock = new ManualTimestampTimeProvider();
        using var baseFactory = CreateFactory("Test");
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var first = await JoinOnFactoryAsync(factory, client, "Andreas");
        var second = await JoinOnFactoryAsync(factory, client, "Bjarke");
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        start.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        Assert.Equal("WAIT Andreas", await ReceiveAsync(second.Socket));
        await SendAsync(first.Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
        await SendAsync(first.Socket, "HOLD 2");
        Assert.Equal("STATE 1 01000", await ReceiveAsync(first.Socket));
        string beforeGame = await client.GetStringAsync("/api/game", _deadline.Token);
        var beforeLobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;

        // Et nyt ratevindue undgår, at opsætningens READY/ROLL/HOLD tæller med.
        clock.Advance(TimeSpan.FromSeconds(1));
        if (violation == "rate")
        {
            for (int i = 0; i < 21; i++) await SendAsync(first.Socket, "# debug");
        }
        else if (violation == "unicode")
        {
            // 65 kodepunkter, men kun 130 bytes: denne case rammer tegngrænsen.
            await SendAsync(first.Socket, new string('ø', 65));
        }
        else if (violation == "bytes")
        {
            await first.Socket.SendAsync(new ArraySegment<byte>(new byte[256]), WebSocketMessageType.Text, false, _deadline.Token);
            await first.Socket.SendAsync(new ArraySegment<byte>(new byte[1]), WebSocketMessageType.Text, false, _deadline.Token);
        }
        else
        {
            byte[] payload = violation == "utf8" ? [0xc3, 0x28] : [1, 2, 3];
            await first.Socket.SendAsync(new ArraySegment<byte>(payload),
                violation == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _deadline.Token);
        }
        var closed = await first.Socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(expected, closed.CloseStatus);
        await first.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "modtaget", _deadline.Token);
        await first.Connection.Completion.WaitAsync(_deadline.Token);
        Assert.False(registry.TryGet(first.Connection.ConnectionId, out _));
        Assert.False(registry.TryGetPlayerId(first.Connection.ConnectionId, out _));
        Assert.Equal(beforeGame, await client.GetStringAsync("/api/game", _deadline.Token));
        var disconnected = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        Assert.True(disconnected.Started);
        Assert.Equal(beforeLobby.Players[0] with { Connected = false }, disconnected.Players[0]);
        Assert.Equal(beforeLobby.Players[1], disconnected.Players[1]);

        // READY er også en behandlingsmarkør: den anden socket fungerer stadig.
        await SendAsync(second.Socket, "READY");
        Assert.Equal("PAIR " + second.Code, await ReceiveAsync(second.Socket));
        Assert.Equal("JOINED Bjarke", await ReceiveAsync(second.Socket));
        Assert.Equal("WAIT Andreas", await ReceiveAsync(second.Socket));
        var current = await ConnectAsync(factory);
        await SendAsync(current, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED Andreas", await ReceiveAsync(current));
        Assert.Equal("STATE 1 01000", await ReceiveAsync(current));
        var currentConnection = Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != second.Connection.ConnectionId);
        Assert.NotEqual(first.Connection.ConnectionId, currentConnection.ConnectionId);
        Assert.True(registry.TryGetPlayerId(currentConnection.ConnectionId, out Guid currentId));
        Assert.Equal(beforeLobby.Players[0].Id, currentId);
        Assert.Equal(beforeLobby.Players, (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players);
        Assert.Equal(beforeGame, await client.GetStringAsync("/api/game", _deadline.Token));
        await SendAsync(current, "HOLD 2");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(current));
        await CloseAsync(current, currentConnection);
        await CloseAsync(second.Socket, second.Connection);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task FullLobbyRegistry_RejectsExtraConnectionThenAllowsTakeoverWithoutLosingBinding()
    {
        using var client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var first = await JoinOnFactoryAsync(_factory, client, "A");
        var second = await JoinOnFactoryAsync(_factory, client, "B");
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        start.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        Assert.Equal("WAIT A", await ReceiveAsync(second.Socket));
        await SendAsync(first.Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
        var unused = new List<(WebSocket Socket, OplaConnection Connection, string Code)>();
        for (int i = 2; i < OplaConnectionRegistry.MaxConnections; i++) unused.Add(await ReadyAsync());
        var beforeLobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        string beforeGame = await client.GetStringAsync("/api/game", _deadline.Token);
        Assert.Equal(12, registry.Count);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync(_factory));
        Assert.Contains("503", refused.Message);
        Assert.Equal(beforeLobby.Players, (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players);
        Assert.Equal(beforeGame, await client.GetStringAsync("/api/game", _deadline.Token));
        foreach (var player in new[] { first, second })
        {
            Assert.True(registry.TryGetPlayerId(player.Connection.ConnectionId, out Guid id));
            Assert.Contains(beforeLobby.Players, entry => entry.Id == id);
        }

        await CloseAsync(unused[0].Socket, unused[0].Connection);
        Assert.Equal(11, registry.Count);
        var current = await ConnectAsync(_factory);
        await SendAsync(current, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED A", await ReceiveAsync(current));
        Assert.Equal("STATE 1 00000", await ReceiveAsync(current));
        Assert.False(registry.TryGetPlayerId(first.Connection.ConnectionId, out _));
        Assert.Equal(WebSocketMessageType.Close,
            (await first.Socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await first.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        await first.Connection.Completion.WaitAsync(_deadline.Token);
        Assert.Equal(11, registry.Count);
        var originalIds = unused.Select(entry => entry.Connection.ConnectionId).Append(second.Connection.ConnectionId).ToHashSet();
        var currentConnection = Assert.Single(registry.GetConnections(), connection => !originalIds.Contains(connection.ConnectionId));
        Assert.True(registry.TryGetPlayerId(currentConnection.ConnectionId, out Guid currentId));
        Assert.Equal(beforeLobby.Players[0].Id, currentId);
        Assert.False(registry.Remove(first.Connection.ConnectionId));
        await SendAsync(current, "HOLD 2");
        Assert.Equal("STATE 1 01000", await ReceiveAsync(current));
        Assert.Equal(1, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        await CloseAsync(current, currentConnection);
        await CloseAsync(second.Socket, second.Connection);
        foreach (var entry in unused.Skip(1)) await CloseAsync(entry.Socket, entry.Connection);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task NewServerInstance_DoesNotRestoreOldPlayerOrGameFromPairCode(string environment)
    {
        Guid oldPlayerId;
        string oldCode;
        using (var previousFactory = CreateFactory(environment))
        using (var previousClient = previousFactory.CreateClient())
        {
            var first = await JoinOnFactoryAsync(previousFactory, previousClient, "Gammelt navn");
            oldCode = first.Code;
            oldPlayerId = (await previousClient.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players[0].Id;
            using var start = await previousClient.PostAsync("/api/lobby/start", null, _deadline.Token);
            start.EnsureSuccessStatusCode();
            Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
            await SendAsync(first.Socket, "ROLL");
            Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
            using var scored = await previousClient.PostAsync("/api/game/strike/chance", null, _deadline.Token);
            scored.EnsureSuccessStatusCode();
            await CloseAsync(first.Socket, first.Connection);
        }

        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var empty = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        Assert.Empty(empty.Players);
        Assert.False(empty.Started);
        using var missing = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var current = await ConnectAsync(factory);
        await SendAsync(current, "READY " + oldCode);
        string pair = await ReceiveAsync(current);
        Assert.Matches("^PAIR [0-9]{4}$", pair);
        // Testgeneratoren genbruger bevidst samme cifre. Det må ikke genskabe identiteten.
        Assert.Equal("PAIR " + oldCode, pair);
        var connection = Assert.Single(registry.GetConnections());
        Assert.False(registry.TryGetPlayerId(connection.ConnectionId, out _));
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(current, command);
            Assert.Contains("READY eller parring mangler", (await _logs.ReadAsync(_deadline.Token)).Message);
        }
        await SendAsync(current, "READY");
        Assert.Equal(pair, await ReceiveAsync(current)); // Ingen skjult JOINED eller STATE før markøren.
        Assert.Empty((await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players);
        using var stillMissing = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, stillMissing.StatusCode);
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "Ny spiller", code = pair[5..] }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        Assert.Equal("JOINED Ny spiller", await ReceiveAsync(current));
        var fresh = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        Assert.NotEqual(oldPlayerId, Assert.Single(fresh.Players).Id);
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        using var json = await ReadJsonAsync(started);
        AssertInitialLobbyGame(json.RootElement, ["Ny spiller"]);
        Assert.Equal("STATE 0 00000", await ReceiveAsync(current));
        await CloseAsync(current, connection);
        Assert.Equal(0, registry.Count);
    }
}
