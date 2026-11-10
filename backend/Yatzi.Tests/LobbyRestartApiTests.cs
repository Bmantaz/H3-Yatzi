using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Omkampsruten valideres gennem HTTP, eksisterende parringer og appens rigtige WebSocket-pipeline.</summary>
public partial class LobbyApiTests
{
    private const string RestartExampleId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string StaleRestartMessage = "Spillet er ikke længere aktuelt. Hent spiltilstanden igen.";
    private const string DisconnectedRestartMessage = "Alle tilmeldte spillere skal være forbundet før omkamp.";

    public static IEnumerable<object[]> InvalidRestartBodies()
    {
        foreach (string body in new[] { "", "{", "null", "[]", "true", "1", "\"tekst\"", "{\"gameId\":\"x\",}" })
            yield return [body, "Requesten skal indeholde et gyldigt JSON-objekt."];
        yield return ["{}", "gameId skal være et gyldigt, ikke-tomt GUID."];
        foreach (string value in new[] { "null", "true", "1", "[]", "{}", "\"\"", "\"ugyldig\"", "\"00000000-0000-0000-0000-000000000000\"", "\"\\uD800\"", "\"\\uDC00\"" })
        {
            yield return [$"{{\"gameId\":{value},\"playerId\":null}}", "gameId skal være et gyldigt, ikke-tomt GUID."];
            yield return [$"{{\"gameId\":\"{RestartExampleId}\",\"playerId\":{value}}}", "playerId skal være et gyldigt, ikke-tomt GUID."];
        }
        yield return [$"{{\"gameId\":\"{RestartExampleId}\"}}", "playerId skal være et gyldigt, ikke-tomt GUID."];
    }

    [Theory]
    [MemberData(nameof(InvalidRestartBodies))]
    public async Task Restart_InvalidBodyPrecedesMissingGameAndPreservesFinishedGame(string body, string message)
    {
        using var client = _factory.CreateClient();
        await RejectBody();
        Assert.Null(_factory.Services.GetRequiredService<GameSession>().GetState());
        var (final, controllers) = await FinishedRestartAsync(client, 1);
        int sent = controllers[0].Socket.Sent.Count;
        await RejectBody();
        GameStateAssertions.Equal(final, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        Assert.Equal(sent, controllers[0].Socket.Sent.Count);

        async Task RejectBody()
        {
            using var response = await client.PostAsync("/api/lobby/restart", new StringContent(body, Encoding.UTF8, "application/json"), _deadline.Token);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, message);
        }
    }

    [Theory]
    [InlineData("Development", 1)]
    [InlineData("Development", 2)]
    [InlineData("Development", 6)]
    [InlineData("Production", 1)]
    [InlineData("Production", 2)]
    [InlineData("Production", 6)]
    [InlineData("Staging", 1)]
    [InlineData("Staging", 2)]
    [InlineData("Staging", 6)]
    [InlineData("Test", 1)]
    [InlineData("Test", 2)]
    [InlineData("Test", 6)]
    public async Task Restart_TwoFullGamesKeepPairingsAndContractInEveryEnvironment(string environment, int count)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var players = new List<(WebSocket Socket, OplaConnection Connection, string Code)>();
        for (int i = 0; i < count; i++) players.Add(await JoinOnFactoryAsync(factory, client, "Alex"));
        var lobby = factory.Services.GetRequiredService<LobbySession>();
        var identities = lobby.GetState().Players;
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        var state = await GameStateAssertions.ReadHttpStateAsync(start, count);
        await ReadStatus(state);
        var gameIds = new HashSet<Guid> { state.GameId };
        for (int round = 0; round < 2; round++)
        {
            foreach (var (category, _) in GameStateAssertions.Categories)
            {
                for (int player = 0; player < count; player++)
                {
                    using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", state, identities[player].Id, _deadline.Token);
                    state = await GameStateAssertions.ReadHttpStateAsync(roll, count);
                    Assert.Equal("STATE 1 00000", await ReceiveAsync(players[player].Socket));
                    using var strike = await GameHttpActions.PostAsync(client, "/api/game/strike/" + category, state, identities[player].Id, _deadline.Token);
                    state = await GameStateAssertions.ReadHttpStateAsync(strike, count);
                    await ReadStatus(state);
                }
            }
            var final = state;
            Assert.True(final.GameCompleted);
            Assert.Equal(Enumerable.Range(0, count), final.WinnerIndices);
            using var restart = await client.PostAsJsonAsync("/api/lobby/restart",
                new { gameId = final.GameId, playerId = identities[^1].Id, extra = true, turnNumber = "ignoreres" }, _deadline.Token);
            state = await GameStateAssertions.ReadHttpStateAsync(restart, count);
            Assert.True(gameIds.Add(state.GameId));
            GameRestartSessionTests.AssertFreshRestart(state, count);
            await ReadStatus(state);
            var after = lobby.GetState();
            Assert.True(after.Started);
            Assert.Equal(identities, after.Players);
            for (int i = 0; i < count; i++)
            {
                await SendAsync(players[i].Socket, "READY " + players[i].Code);
                Assert.Equal("PAIR " + players[i].Code, await ReceiveAsync(players[i].Socket));
                Assert.Equal("JOINED Alex", await ReceiveAsync(players[i].Socket));
                Assert.Equal(i == 0 ? "STATE 0 00000" : "WAIT Alex", await ReceiveAsync(players[i].Socket));
            }
            Assert.True(final.GameCompleted);
            Assert.Equal(Enumerable.Range(0, count), final.WinnerIndices);
        }
        // Rå Oplà-input bruger den bevarede binding og tilstanden ved behandling.
        await SendAsync(players[0].Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(players[0].Socket));
        await SendAsync(players[0].Socket, "HOLD 1");
        Assert.Equal("STATE 1 10000", await ReceiveAsync(players[0].Socket));
        if (count > 1)
        {
            await SendAsync(players[1].Socket, "ROLL");
            await SendAsync(players[1].Socket, "READY");
            Assert.Equal("PAIR " + players[1].Code, await ReceiveAsync(players[1].Socket));
            Assert.Equal("JOINED Alex", await ReceiveAsync(players[1].Socket));
            Assert.Equal("WAIT Alex", await ReceiveAsync(players[1].Socket));
        }
        Assert.Equal(1, factory.Services.GetRequiredService<GameSession>().GetState()!.RollsUsed);
        using var duplicateStart = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        await AssertErrorAsync(duplicateStart, HttpStatusCode.Conflict, "Lobbyen er allerede startet.");
        using var solo = await client.PostAsJsonAsync("/api/game/new", new { name = "Solo" }, _deadline.Token);
        await AssertErrorAsync(solo, HttpStatusCode.Conflict, "Et lobbyspil er allerede startet og kan ikke nulstilles her.");
        using var join = await client.PostAsJsonAsync("/api/lobby/join", new { name = "Ny", code = players[0].Code }, _deadline.Token);
        await AssertErrorAsync(join, HttpStatusCode.Conflict, "Lobbyen er allerede startet.");
        foreach (var player in players) await CloseAsync(player.Socket, player.Connection);

        async Task ReadStatus(GameStateResponse snapshot)
        {
            for (int i = 0; i < count; i++)
                Assert.Equal(snapshot.GameCompleted ? "END" : i == snapshot.CurrentPlayer ? "STATE 0 00000" : "WAIT Alex",
                    await ReceiveAsync(players[i].Socket));
        }
    }

    [Fact]
    public async Task Restart_MissingAndSoloGameHaveExactPriorityAndSoloCanStillBeReplaced()
    {
        using var client = _factory.CreateClient();
        using var missing = await RestartRequestAsync(client, new(Guid.NewGuid(), Guid.NewGuid()));
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, "Der er ikke oprettet et spil. Start et nyt spil først.");
        var game = _factory.Services.GetRequiredService<GameSession>();
        var solo = game.NewGame("Solo");
        using var stale = await RestartRequestAsync(client, new(Guid.NewGuid(), Guid.NewGuid()));
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, StaleRestartMessage);
        using var mode = await RestartRequestAsync(client, new(solo.GameId, Guid.NewGuid()));
        await AssertErrorAsync(mode, HttpStatusCode.Conflict, "Der er ikke et startet lobbyspil at genstarte.");
        GameStateAssertions.Equal(solo, game.GetState()!);
        foreach (var (_, category) in GameStateAssertions.Categories) { game.Roll(); game.Strike(category); }
        using var finishedSolo = await RestartRequestAsync(client, new(solo.GameId, Guid.NewGuid()));
        await AssertErrorAsync(finishedSolo, HttpStatusCode.Conflict, "Der er ikke et startet lobbyspil at genstarte.");
        using var next = await client.PostAsJsonAsync("/api/game/new", new { name = "Næste" }, _deadline.Token);
        var state = await GameStateAssertions.ReadHttpStateAsync(next);
        GameRestartSessionTests.AssertFreshRestart(state, 1);
        Assert.NotEqual(solo.GameId, state.GameId);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("closed")]
    [InlineData("unbound")]
    [InlineData("wrong-binding")]
    public async Task Restart_RejectsUnavailablePlayerWithoutLosingResultThenSameCodeReconnects(string failure)
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var ids = lobby.GetState().Players;
        var first = controllers[0];
        string code = SentLines(first.Socket)[0][5..];
        if (failure == "removed") registry.Remove(first.Connection.ConnectionId);
        else if (failure == "closed") await first.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        else if (failure == "unbound") first.Connection.MarkClosed();
        else
        {
            // Den offentlige transport kan ikke ombinde en parret forbindelse.
            // Injicér derfor kun denne interne fejl for at kontrollere id-sammenligningen.
            typeof(OplaConnection).GetField("_playerId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(first.Connection, Guid.NewGuid());
            Assert.True(registry.TryGetPlayerId(first.Connection.ConnectionId, out Guid wrongId));
            Assert.NotEqual(ids[0].Id, wrongId);
            Assert.False(first.Connection.IsClosing);
        }
        int[] counts = controllers.Select(player => player.Socket.Sent.Count).ToArray();
        foreach (var (context, message) in new[]
        {
            (new LobbyRestartContext(Guid.NewGuid(), Guid.NewGuid()), StaleRestartMessage),
            (new LobbyRestartContext(final.GameId, Guid.NewGuid()), "Spilleren deltager ikke i dette lobbyspil."),
            (new LobbyRestartContext(final.GameId, ids[1].Id), DisconnectedRestartMessage)
        })
        {
            using var response = await RestartRequestAsync(client, context);
            await AssertErrorAsync(response, HttpStatusCode.Conflict, message);
            GameStateAssertions.Equal(final, _factory.Services.GetRequiredService<GameSession>().GetState()!);
            Assert.Equal(counts, controllers.Select(player => player.Socket.Sent.Count));
        }
        var current = ActionConnection(registry);
        await lobby.HandleReadyAsync(current.Connection.ConnectionId, code, _deadline.Token);
        Assert.Equal(new[] { "PAIR " + code, "JOINED Alex", "END" }, SentLines(current.Socket));
        using var accepted = await RestartRequestAsync(client, new(final.GameId, ids[1].Id));
        GameRestartSessionTests.AssertFreshRestart(await GameStateAssertions.ReadHttpStateAsync(accepted, 2), 2);
        Assert.Equal("STATE 0 00000", SentLines(current.Socket)[^1]);
        Assert.Equal("WAIT Alex", SentLines(controllers[1].Socket)[^1]);
        Assert.Equal(ids, lobby.GetState().Players);
    }

    [Fact]
    public async Task Restart_ActiveGamePrecedesDisconnectedAndUnknownPlayerPrecedesActiveGame()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "Alex");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var state = (await lobby.StartAsync(_deadline.Token)).State!;
        Guid id = lobby.GetState().Players[0].Id;
        first.Connection.MarkClosed();
        using var unknown = await RestartRequestAsync(client, new(state.GameId, Guid.NewGuid()));
        await AssertErrorAsync(unknown, HttpStatusCode.Conflict, "Spilleren deltager ikke i dette lobbyspil.");
        using var active = await RestartRequestAsync(client, new(state.GameId, id));
        await AssertErrorAsync(active, HttpStatusCode.Conflict, "Spillet skal være afsluttet før omkamp.");
        GameStateAssertions.Equal(state, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        Assert.Equal(3, first.Socket.Sent.Count);
    }

    [Fact]
    public async Task Restart_OldGameActionsRejectEvenWhenTurnAndPlayerMatchAndNewPlayerHasRolled()
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var game = _factory.Services.GetRequiredService<GameSession>();
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        Guid id = lobby.GetState().Players[0].Id;
        using var restart = await RestartRequestAsync(client, new(final.GameId, id));
        var state = await GameStateAssertions.ReadHttpStateAsync(restart, 2);
        using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", state, id, _deadline.Token);
        var current = await GameStateAssertions.ReadHttpStateAsync(roll, 2);
        int[] counts = controllers.Select(player => player.Socket.Sent.Count).ToArray();
        foreach (string action in new[] { "roll", "hold/1", "score/chance", "strike/chance" })
        {
            using var response = await client.PostAsJsonAsync("/api/game/" + action, new GameActionContext(final.GameId, 1, id), _deadline.Token);
            await AssertErrorAsync(response, HttpStatusCode.Conflict, StaleRestartMessage);
            GameStateAssertions.Equal(current, game.GetState()!);
            Assert.Equal(counts, controllers.Select(player => player.Socket.Sent.Count));
        }
        using var staleRestart = await RestartRequestAsync(client, new(final.GameId, id));
        await AssertErrorAsync(staleRestart, HttpStatusCode.Conflict, StaleRestartMessage);
        Assert.True(final.GameCompleted);
    }

    private Task<HttpResponseMessage> RestartRequestAsync(HttpClient client, LobbyRestartContext context) =>
        client.PostAsJsonAsync("/api/lobby/restart", context, _deadline.Token);

    // Gyldige lobbyhandlinger udfylder tavlerne; ingen direkte adgang til pointlagringen.
    private async Task<(GameStateResponse Final, (ControlledWebSocket Socket, OplaConnection Connection)[] Controllers)>
        FinishedRestartAsync(HttpClient client, int count = 2)
    {
        var controllers = new List<(ControlledWebSocket Socket, OplaConnection Connection)>();
        for (int i = 0; i < count; i++) controllers.Add(await ControlledJoinedAsync(client, "Alex"));
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var state = (await lobby.StartAsync(_deadline.Token)).State!;
        var ids = lobby.GetState().Players;
        foreach (var (_, category) in GameStateAssertions.Categories)
        {
            foreach (var player in ids)
            {
                var rolled = await lobby.RollAsync(GameHttpActions.Context(state, player.Id), _deadline.Token);
                Assert.Equal(GameActionStatus.Accepted, rolled.Status);
                var chosen = await lobby.StrikeAsync(category, GameHttpActions.Context(rolled.State, player.Id), _deadline.Token);
                Assert.Equal(GameActionStatus.Accepted, chosen.Status);
                state = chosen.State!;
            }
        }
        Assert.True(state.GameCompleted);
        Assert.All(controllers, player => Assert.Equal("END", SentLines(player.Socket)[^1]));
        return (state, controllers.ToArray());
    }
}
