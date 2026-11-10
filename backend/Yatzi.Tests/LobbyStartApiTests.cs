using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

// Startcases til samme testklasse: genbruger fabrikkens HTTP/WebSocket-klienter,
// fejlformat og afsluttede lukninger fra LobbyApiTests uden en ny testværtstype.
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
    public async Task Start_UsesSharedSessionAndPreservesContractInEveryEnvironment(string environment, int count)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var players = new List<(WebSocket Socket, OplaConnection Connection, string Code)>();
        for (int i = 0; i < count; i++) players.Add(await JoinOnFactoryAsync(factory, client, $"Spiller {i}"));
        var before = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        using var json = await ReadJsonAsync(started);
        AssertInitialLobbyGame(json.RootElement, Enumerable.Range(0, count).Select(i => $"Spiller {i}").ToArray());
        Assert.Equal(json.RootElement.GetRawText(), await client.GetStringAsync("/api/game", _deadline.Token));
        var lobby = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
        Assert.True(lobby!.Started);
        Assert.Equal(before!.Players, lobby.Players);
        for (int i = 0; i < count; i++)
            Assert.Equal(i == 0 ? "STATE 0 00000" : "WAIT Spiller 0", await ReceiveAsync(players[i].Socket));

        using var repeated = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        await AssertErrorAsync(repeated, HttpStatusCode.Conflict, "Lobbyen er allerede startet.");
        using var reset = await client.PostAsJsonAsync("/api/game/new", new { name = "Overskrivning" }, _deadline.Token);
        await AssertErrorAsync(reset, HttpStatusCode.Conflict, "Et lobbyspil er allerede startet og kan ikke nulstilles her.");
        using var invalidReset = await client.PostAsJsonAsync("/api/game/new", new { name = "" }, _deadline.Token);
        await AssertErrorAsync(invalidReset, HttpStatusCode.BadRequest, "Navnet skal indeholde mellem 1 og 20 tegn.");
        using var join = await client.PostAsJsonAsync("/api/lobby/join", new { name = "Ny", code = "9999" }, _deadline.Token);
        await AssertErrorAsync(join, HttpStatusCode.Conflict, "Lobbyen er allerede startet.");
        using var invalidJoin = await client.PostAsJsonAsync("/api/lobby/join", new { name = "Ny", code = 12 }, _deadline.Token);
        await AssertErrorAsync(invalidJoin, HttpStatusCode.BadRequest, "Koden skal være tekst.");

        await SendAsync(players[0].Socket, "READY 9999");
        Assert.Equal("PAIR " + players[0].Code, await ReceiveAsync(players[0].Socket));
        Assert.Equal("JOINED Spiller 0", await ReceiveAsync(players[0].Socket));
        Assert.Equal("STATE 0 00000", await ReceiveAsync(players[0].Socket));
        foreach (var player in players) await CloseAsync(player.Socket, player.Connection);
        Assert.Equal(json.RootElement.GetRawText(), await client.GetStringAsync("/api/game", _deadline.Token));
    }

    [Fact]
    public async Task Start_RejectsEmptyLobbyAndIgnoresBodyWithoutReplacingExistingGame()
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Selvstændigt" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        using var rejected = await client.PostAsync("/api/lobby/start", new StringContent("{", Encoding.UTF8, "application/json"), _deadline.Token);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "Lobbyen skal have mellem 1 og 6 tilmeldte spillere før start.");
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        var player = await JoinOnFactoryAsync(_factory, client, "A");
        using var started = await client.PostAsJsonAsync("/api/lobby/start", new { players = Array.Empty<object>(), started = false }, _deadline.Token);
        using var json = await ReadJsonAsync(started);
        AssertInitialLobbyGame(json.RootElement, ["A"]);
        Assert.Equal("STATE 0 00000", await ReceiveAsync(player.Socket));
        await CloseAsync(player.Socket, player.Connection);
    }

    [Fact]
    public async Task Start_DisconnectedPlayerReturns409AndCanStartAfterReconnect()
    {
        using var client = _factory.CreateClient();
        var player = await JoinOnFactoryAsync(_factory, client, "A");
        await CloseAsync(player.Socket, player.Connection);
        using var rejected = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "Alle tilmeldte spillere skal være forbundet før start.");
        Assert.False((await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Started);
        using var missingGame = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missingGame.StatusCode);
        var current = await ConnectAsync(_factory);
        await SendAsync(current, "READY " + player.Code);
        Assert.Equal("PAIR " + player.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED A", await ReceiveAsync(current));
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(current));
        await CloseAsync(current, Assert.Single(_factory.Services.GetRequiredService<OplaConnectionRegistry>().GetConnections()));
    }

    [Fact]
    public async Task StartedLobby_HttpActionsRotatePlayersAndReconnectRestoresCurrentStateOrWait()
    {
        using var client = _factory.CreateClient();
        var first = await JoinOnFactoryAsync(_factory, client, "Åse Øster");
        var second = await JoinOnFactoryAsync(_factory, client, "Bjarke");
        using var started = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        started.EnsureSuccessStatusCode();
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        Assert.Equal("WAIT Åse Øster", await ReceiveAsync(second.Socket));
        var initialState = (await started.Content.ReadFromJsonAsync<GameStateResponse>(_deadline.Token))!;
        var ids = _factory.Services.GetRequiredService<LobbySession>().GetState().Players;
        using var rolled = await GameHttpActions.PostAsync(client, "/api/game/roll", initialState, ids[0].Id, _deadline.Token);
        rolled.EnsureSuccessStatusCode();
        Assert.Equal("STATE 1 00000", await ReceiveAsync(first.Socket));
        using var held = await GameHttpActions.PostAsync(client, "/api/game/hold/3", initialState, ids[0].Id, _deadline.Token);
        held.EnsureSuccessStatusCode();
        Assert.Equal("STATE 1 00100", await ReceiveAsync(first.Socket));
        using var scored = await GameHttpActions.PostAsync(client, "/api/game/strike/chance", initialState, ids[0].Id, _deadline.Token);
        Assert.Equal("WAIT Bjarke", await ReceiveAsync(first.Socket));
        Assert.Equal("STATE 0 00000", await ReceiveAsync(second.Socket));
        using var scoredJson = await ReadJsonAsync(scored);
        var state = scoredJson.RootElement;
        Assert.False(state.GetProperty("turnCompleted").GetBoolean());
        Assert.Equal(1, state.GetProperty("currentPlayer").GetInt32());
        Assert.Equal(0, state.GetProperty("rollsUsed").GetInt32());
        Assert.Equal(0, state.GetProperty("players")[0].GetProperty("scorecard").GetProperty("scores").GetProperty("chance").GetInt32());
        Assert.All(state.GetProperty("players")[1].GetProperty("scorecard").GetProperty("scores").EnumerateObject(),
            score => Assert.Equal(JsonValueKind.Null, score.Value.ValueKind));
        foreach (var player in new[] { first, second })
        {
            await SendAsync(player.Socket, "READY " + player.Code);
            Assert.Equal("PAIR " + player.Code, await ReceiveAsync(player.Socket));
            Assert.Equal(player.Code == first.Code ? "JOINED Åse Øster" : "JOINED Bjarke", await ReceiveAsync(player.Socket));
            Assert.Equal(player.Code == first.Code ? "WAIT Bjarke" : "STATE 0 00000", await ReceiveAsync(player.Socket));
        }
        // Første spiller er stadig parret, men har ikke længere tur.
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(first.Socket, command);
            Assert.Contains("Det er ikke denne spillers tur", (await _handlerLogs.ReadAsync(_deadline.Token)).Message);
        }
        await CloseAsync(first.Socket, first.Connection);
        var current = await ConnectAsync(_factory);
        await SendAsync(current, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED Åse Øster", await ReceiveAsync(current));
        Assert.Equal("WAIT Bjarke", await ReceiveAsync(current));
        Assert.Equal(state.GetRawText(), await client.GetStringAsync("/api/game", _deadline.Token));
        // HTTP bruger den nye spillers eksplicitte kontekst, også som reserve efter overtagelse.
        using var nextRoll = await GameHttpActions.PostAsync(client, "/api/game/roll",
            _factory.Services.GetRequiredService<GameSession>().GetState(), ids[1].Id, _deadline.Token);
        nextRoll.EnsureSuccessStatusCode();
        Assert.Equal("STATE 1 00000", await ReceiveAsync(second.Socket));
        var next = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        Assert.Equal(1, next.CurrentPlayer);
        Assert.Equal(1, next.RollsUsed);
        Assert.Equal(0, next.Players[0].Scorecard.Scores["chance"]);
        await SendAsync(second.Socket, "READY " + second.Code);
        Assert.Equal("PAIR " + second.Code, await ReceiveAsync(second.Socket));
        Assert.Equal("JOINED Bjarke", await ReceiveAsync(second.Socket));
        Assert.Equal("STATE 1 00000", await ReceiveAsync(second.Socket));
        Assert.Equal(1, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var currentConnection = Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != second.Connection.ConnectionId);
        await CloseAsync(current, currentConnection);
        await CloseAsync(second.Socket, second.Connection);
    }

    [Fact]
    public async Task ConcurrentHttpStarts_ReturnOneSuccessOneConflictAndOneStateMessage()
    {
        using var client = _factory.CreateClient();
        var player = await JoinOnFactoryAsync(_factory, client, "A");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsync("/api/lobby/start", null, _deadline.Token)));
        try
        {
            Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
            await AssertErrorAsync(Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict, "Lobbyen er allerede startet.");
            Assert.Equal("STATE 0 00000", await ReceiveAsync(player.Socket));
        }
        finally { foreach (var result in results) result.Dispose(); }
        await CloseAsync(player.Socket, player.Connection);
    }

    [Fact]
    public async Task ConcurrentHttpNewGameAndStart_EndWithLobbyGameAndRejectFurtherReset()
    {
        using var client = _factory.CreateClient();
        var player = await JoinOnFactoryAsync(_factory, client, "Lobbyspiller");
        var resetTask = client.PostAsJsonAsync("/api/game/new", new { name = "Selvstændigt" }, _deadline.Token);
        var startTask = client.PostAsync("/api/lobby/start", null, _deadline.Token);
        await Task.WhenAll(resetTask, startTask);
        using var reset = await resetTask;
        using var started = await startTask;
        Assert.Contains(reset.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        started.EnsureSuccessStatusCode();
        var state = await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token);
        Assert.Equal("Lobbyspiller", Assert.Single(state!.Players).Name);
        Assert.Equal("STATE 0 00000", await ReceiveAsync(player.Socket));
        using var repeated = await client.PostAsJsonAsync("/api/game/new", new { name = "Overskrivning" }, _deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        await CloseAsync(player.Socket, player.Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpStart_TransportFailureReturns200AndOtherPlayerStillReceivesWait(bool ioFailure)
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var second = await ControlledJoinedAsync(client, "B");
        first.Socket.SendAction = _ => { if (ioFailure) throw new IOException(); throw new WebSocketException(); };
        using var response = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        using var json = await ReadJsonAsync(response);
        AssertInitialLobbyGame(json.RootElement, ["A", "B"]);
        Assert.Equal("WAIT A", Encoding.UTF8.GetString(second.Socket.Sent.Last()));
        Assert.Equal(3, first.Socket.Sent.Count);
        var lobby = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
        Assert.True(lobby!.Started);
        Assert.False(lobby.Players[0].Connected);
        Assert.True(lobby.Players[1].Connected);
    }

    [Fact]
    public async Task HttpStart_WaitsForDeliveryButGetAndOtherRecipientsRemainAvailable()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var second = await ControlledJoinedAsync(client, "B");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        second.Socket.SendAction = _ => { waitingSent.TrySetResult(); return Task.CompletedTask; };
        var start = client.PostAsync("/api/lobby/start", null, _deadline.Token);
        try
        {
            await entered.Task.WaitAsync(_deadline.Token);
            await waitingSent.Task.WaitAsync(_deadline.Token);
            Assert.False(start.IsCompleted);
            Assert.Equal("WAIT A", Encoding.UTF8.GetString(second.Socket.Sent.Last()));
            Assert.True((await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Started);
            Assert.Equal(2, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.Players.Length);
        }
        finally { release.TrySetResult(); }
        using var response = await start.WaitAsync(_deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<(WebSocket Socket, OplaConnection Connection, string Code)> JoinOnFactoryAsync(
        WebApplicationFactory<Program> factory, HttpClient client, string name)
    {
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var previous = registry.GetConnections().Select(connection => connection.ConnectionId).ToHashSet();
        var socket = await ConnectAsync(factory);
        await SendAsync(socket, "READY");
        string pair = await ReceiveAsync(socket);
        Assert.Matches("^PAIR [0-9]{4}$", pair);
        string code = pair[5..];
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name, code }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        var result = (await joined.Content.ReadFromJsonAsync<LobbyJoinResponse>(_deadline.Token))!;
        var connection = Assert.Single(registry.GetConnections(), connection => !previous.Contains(connection.ConnectionId));
        Assert.True(registry.TryGetPlayerId(connection.ConnectionId, out Guid boundId));
        Assert.Equal(boundId, result.JoinedPlayerId);
        Assert.Equal("JOINED " + name, await ReceiveAsync(socket));
        return (socket, connection, code);
    }

    private async Task<(ControlledWebSocket Socket, OplaConnection Connection)> ControlledJoinedAsync(HttpClient client, string name)
    {
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Assert.True(registry.TryReserve(out Guid id));
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        await _factory.Services.GetRequiredService<LobbySession>().HandleReadyAsync(id, null, _deadline.Token);
        string code = Encoding.UTF8.GetString(socket.Sent.Single())[5..];
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name, code }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        var joinedState = (await joined.Content.ReadFromJsonAsync<LobbyJoinResponse>(_deadline.Token))!;
        Assert.True(registry.TryGetPlayerId(connection.ConnectionId, out Guid boundId));
        Assert.Equal(boundId, joinedState.JoinedPlayerId);
        return (socket, connection);
    }

    private static void AssertInitialLobbyGame(JsonElement state, string[] names)
    {
        GameStateAssertions.AssertJsonShape(state, names.Length);
        Assert.Equal(1, state.GetProperty("turnNumber").GetInt32());
        Assert.False(state.GetProperty("gameCompleted").GetBoolean());
        Assert.Empty(state.GetProperty("winnerIndices").EnumerateArray());
        Assert.Equal(names, state.GetProperty("players").EnumerateArray().Select(player => player.GetProperty("name").GetString()));
        Assert.Equal(0, state.GetProperty("currentPlayer").GetInt32());
        Assert.Equal(new int[5], state.GetProperty("dice").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(new bool[5], state.GetProperty("held").EnumerateArray().Select(value => value.GetBoolean()));
        Assert.Equal(0, state.GetProperty("rollsUsed").GetInt32());
        Assert.Empty(state.GetProperty("possibleScores").EnumerateObject());
        Assert.False(state.GetProperty("turnCompleted").GetBoolean());
        foreach (var player in state.GetProperty("players").EnumerateArray())
        {
            Assert.Equal(new[] { "name", "scorecard" }, player.EnumerateObject().Select(property => property.Name));
            var card = player.GetProperty("scorecard");
            Assert.Equal(new[] { "scores", "upperSum", "bonus", "total" }, card.EnumerateObject().Select(property => property.Name));
            Assert.Equal(GameStateAssertions.Categories.Select(category => category.Id), card.GetProperty("scores").EnumerateObject().Select(property => property.Name));
            Assert.All(card.GetProperty("scores").EnumerateObject(), score => Assert.Equal(JsonValueKind.Null, score.Value.ValueKind));
            Assert.Equal(0, card.GetProperty("upperSum").GetInt32());
            Assert.Equal(0, card.GetProperty("bonus").GetInt32());
            Assert.Equal(0, card.GetProperty("total").GetInt32());
        }
    }
}
