using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Kontrollerer obligatorisk HTTP-kontekst, fejlprioritet og forsinkede requests uden automatisk kontekstfornyelse.</summary>
public class GameActionApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private static readonly string[] Paths = ["roll", "hold/1", "score/chance", "strike/chance"];
    private const string StaleTurn = "Turen er ikke længere aktuel. Hent spiltilstanden igen.";

    public static IEnumerable<object?[]> InvalidBodies()
    {
        string game = "\"gameId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\"";
        var cases = new List<(string? Body, string Message)>();
        foreach (string? body in new[] { null, "", "{", "null", "[]", "1", "true" })
            cases.Add((body, "Requesten skal indeholde et gyldigt JSON-objekt."));
        cases.Add(("{}", "gameId skal være et gyldigt, ikke-tomt GUID."));
        foreach (string value in new[] { "null", "false", "42", "[]", "{}", "\"\"", "\"bad\"", "\"00000000-0000-0000-0000-000000000000\"", "\"\\uD800\"" })
            cases.Add(($"{{\"gameId\":{value}}}", "gameId skal være et gyldigt, ikke-tomt GUID."));
        foreach (string value in new[] { "null", "false", "0", "-1", "2147483648", "1.0", "1.5", "\"1\"", "[]", "{}" })
            cases.Add(($"{{{game},\"turnNumber\":{value}}}", "turnNumber skal være et positivt heltal."));
        cases.Add(($"{{{game}}}", "turnNumber skal være et positivt heltal."));
        cases.Add(($"{{{game},\"turnNumber\":1}}", "playerId skal være null eller et gyldigt, ikke-tomt GUID."));
        foreach (string value in new[] { "false", "1", "[]", "{}", "\"bad\"", "\"00000000-0000-0000-0000-000000000000\"", "\"\\uD800\"" })
            cases.Add(($"{{{game},\"turnNumber\":1,\"playerId\":{value}}}", "playerId skal være null eller et gyldigt, ikke-tomt GUID."));
        foreach (string path in Paths)
            foreach (var (body, message) in cases) yield return [path, body, message];
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidContext_Is400BeforeGameLookupAndPreservesExistingGame(string path, string? body, string message)
    {
        using var client = _factory.CreateClient();
        for (int stage = 0; stage < 2; stage++)
        {
            GameStateResponse? before = null;
            if (stage == 1)
            {
                _factory.Services.GetRequiredService<GameSession>().NewGame("Alex");
                before = _factory.Services.GetRequiredService<GameSession>().Roll();
            }
            using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/api/game/{path}", content);
            await Error(response, HttpStatusCode.BadRequest, message);
            if (before is null) Assert.Null(_factory.Services.GetRequiredService<GameSession>().GetState());
            else GameStateAssertions.Equal(before, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        }
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("hold/1")]
    [InlineData("score/chance")]
    [InlineData("strike/chance")]
    public async Task ValidContext_WithoutGameIs404(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await GameHttpActions.PostAsync(client, $"/api/game/{path}", null);
        await Error(response, HttpStatusCode.NotFound, "Der er ikke oprettet et spil. Start et nyt spil først.");
    }

    [Fact]
    public async Task RouteErrors_PrecedeMissingBodyAndUnknownFieldsAreIgnored()
    {
        using var client = _factory.CreateClient();
        foreach (string path in new[] { "hold/0", "hold/abc", "score/unknown", "strike/CHANCE" })
        {
            using var rejected = await client.PostAsync($"/api/game/{path}", null);
            await Error(rejected, HttpStatusCode.BadRequest, path.StartsWith("hold")
                ? "Terningens nummer skal være mellem 1 og 5." : "Pointfeltet er ukendt. Brug et af de 15 felt-id'er.");
        }
        var state = _factory.Services.GetRequiredService<GameSession>().NewGame("Alex");
        using var response = await client.PostAsJsonAsync("/api/game/roll", new { gameId = state.GameId, turnNumber = 1, playerId = (Guid?)null, dice = new[] { 6, 6 }, points = 999 });
        Assert.Equal(1, (await GameStateAssertions.ReadHttpStateAsync(response)).RollsUsed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task LobbyIdentityAndOldTurns_AreRejectedEvenAfterNextPlayerRollsOrOriginalPlayerReturns(int count)
    {
        using var client = _factory.CreateClient();
        var session = _factory.Services.GetRequiredService<GameSession>();
        var players = Enumerable.Range(0, count).Select(_ => new LobbyGamePlayer(Guid.NewGuid(), "Alex")).ToArray();
        var first = session.NewLobbyGame(players);
        foreach (string path in Paths)
        {
            using var missingId = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first);
            await Error(missingId, HttpStatusCode.BadRequest, "playerId skal være et gyldigt, ikke-tomt GUID i et lobbyspil.");
            using var unknown = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first, Guid.NewGuid());
            await Error(unknown, HttpStatusCode.Conflict, "Spilleren deltager ikke i dette lobbyspil.");
            if (count > 1)
            {
                using var waiting = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first, players[1].Id);
                await Error(waiting, HttpStatusCode.Conflict, "Det er ikke denne spillers tur.");
            }
            GameStateAssertions.Equal(first, session.GetState()!);
        }
        session.RollFromOpla(players[0].Id);
        using var chosen = await GameHttpActions.PostAsync(client, "/api/game/score/chance", first, players[0].Id);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        session.RollFromOpla(players[1 % count].Id);
        var next = session.GetState()!;
        await RejectOld(next);
        for (int player = 1; player < count; player++)
        {
            var state = session.GetState()!;
            using var strike = await GameHttpActions.PostAsync(client, "/api/game/strike/chance", state, players[player].Id);
            Assert.Equal(HttpStatusCode.OK, strike.StatusCode);
            if (player < count - 1) session.RollFromOpla(players[player + 1].Id);
        }
        if (count > 1) session.RollFromOpla(players[0].Id);
        Assert.Equal(0, session.GetState()!.CurrentPlayer);
        await RejectOld(session.GetState()!);

        async Task RejectOld(GameStateResponse expected)
        {
            foreach (string path in Paths)
            {
                using var response = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first, players[0].Id);
                await Error(response, HttpStatusCode.Conflict, StaleTurn);
                GameStateAssertions.Equal(expected, session.GetState()!);
            }
        }
    }

    [Fact]
    public async Task PreviousGameAndWrongSoloIdentity_CannotChangeReplacement()
    {
        using var client = _factory.CreateClient();
        var session = _factory.Services.GetRequiredService<GameSession>();
        var old = session.NewGame("Alex");
        var current = session.NewGame("Alex");
        foreach (string path in Paths)
        {
            using var stale = await GameHttpActions.PostAsync(client, $"/api/game/{path}", old, Guid.NewGuid());
            await Error(stale, HttpStatusCode.Conflict, "Spillet er ikke længere aktuelt. Hent spiltilstanden igen.");
            using var wrongPlayer = await GameHttpActions.PostAsync(client, $"/api/game/{path}", current, Guid.NewGuid());
            await Error(wrongPlayer, HttpStatusCode.Conflict, "Enkeltspillerhandlinger skal bruge playerId null.");
            GameStateAssertions.Equal(current, session.GetState()!);
        }
    }

    [Fact]
    public async Task FinalStatePriority_IsGameThenRelationThenCompletionBeforeTurnAndActivePlayer()
    {
        using var client = _factory.CreateClient();
        var session = _factory.Services.GetRequiredService<GameSession>();
        var players = new[] { new LobbyGamePlayer(Guid.NewGuid(), "Alex"), new LobbyGamePlayer(Guid.NewGuid(), "Alex") };
        var first = session.NewLobbyGame(players);
        foreach (var (_, category) in GameStateAssertions.Categories)
            foreach (var _ in players) { session.Roll(); session.Strike(category); }
        var final = session.GetState()!;
        foreach (string path in Paths)
        {
            using var noPlayer = await client.PostAsJsonAsync($"/api/game/{path}", new GameActionContext(Guid.NewGuid(), 1, null));
            await Error(noPlayer, HttpStatusCode.BadRequest, "playerId skal være et gyldigt, ikke-tomt GUID i et lobbyspil.");
            using var wrongGame = await client.PostAsJsonAsync($"/api/game/{path}", new GameActionContext(Guid.NewGuid(), 1, Guid.NewGuid()));
            await Error(wrongGame, HttpStatusCode.Conflict, "Spillet er ikke længere aktuelt. Hent spiltilstanden igen.");
            using var unknown = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first, Guid.NewGuid());
            await Error(unknown, HttpStatusCode.Conflict, "Spilleren deltager ikke i dette lobbyspil.");
            using var completed = await GameHttpActions.PostAsync(client, $"/api/game/{path}", first, players[0].Id);
            await Error(completed, HttpStatusCode.Conflict, "Spillet er afsluttet. Start et nyt spil for at fortsætte.");
            GameStateAssertions.Equal(final, session.GetState()!);
        }
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "message" }, json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(message, json.RootElement.GetProperty("message").GetString());
    }
    public void Dispose() => _factory.Dispose();
}
