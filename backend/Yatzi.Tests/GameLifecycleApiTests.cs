using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerer alle 11 JSON-felter gennem HTTP ved hele spil med 1, 2 og 6 spillere.
/// Lobbytransportens start testes særskilt; her oprettes lobbydeltagerne direkte i sessionen.
/// HTTP kører i hukommelsen med eksplicit spil-/turkontekst og den aktive deltagers id.
/// </summary>
public class GameLifecycleApiTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    public async Task FullGame_ReturnsExtendedSnapshotsAndStableFinalResult(int count, bool strike)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Alex" });
        var standalone = await GameStateAssertions.ReadHttpStateAsync(created);
        Guid?[] actors = [null];
        if (count > 1)
        {
            var participants = Enumerable.Range(0, count).Select(_ => new LobbyGamePlayer(Guid.NewGuid(), "Alex")).ToArray();
            factory.Services.GetRequiredService<GameSession>().NewLobbyGame(participants);
            actors = participants.Select(player => (Guid?)player.Id).ToArray();
        }
        using var initial = await client.GetAsync("/api/game");
        var state = await GameStateAssertions.ReadHttpStateAsync(initial, count);
        var start = state;
        Assert.Equal(1, start.TurnNumber);
        if (count > 1) Assert.NotEqual(standalone.GameId, start.GameId);
        int turn = 1;
        foreach (var (id, category) in GameStateAssertions.Categories)
        {
            for (int player = 0; player < count; player++, turn++)
            {
                Assert.Equal(turn, state.TurnNumber);
                Assert.Equal(player, state.CurrentPlayer);
                Assert.False(state.GameCompleted);
                Assert.Empty(state.WinnerIndices);
                using var rollResponse = await GameHttpActions.PostAsync(client, "/api/game/roll", state, actors[player]);
                var rolled = await GameStateAssertions.ReadHttpStateAsync(rollResponse, count);
                using var holdResponse = await GameHttpActions.PostAsync(client, "/api/game/hold/1", rolled, actors[player]);
                var held = await GameStateAssertions.ReadHttpStateAsync(holdResponse, count);
                Assert.Equal(turn, held.TurnNumber);
                Assert.Equal(rolled.Dice, held.Dice);
                using var chosen = await GameHttpActions.PostAsync(client, $"/api/game/{(strike ? "strike" : "score")}/{id}", held, actors[player]);
                state = await GameStateAssertions.ReadHttpStateAsync(chosen, count);
                Assert.Equal(start.GameId, state.GameId);
                Assert.Equal(strike ? 0 : rolled.PossibleScores[id], state.Players[player].Scorecard.Scores[id]);
                bool last = turn == 15 * count;
                Assert.Equal(last, state.GameCompleted);
                Assert.Equal(last ? turn : turn + 1, state.TurnNumber);
                if (last)
                {
                    Assert.Equal(player, state.CurrentPlayer);
                    Assert.Equal(held.Dice, state.Dice);
                    Assert.Equal(held.Held, state.Held);
                    Assert.Equal(held.RollsUsed, state.RollsUsed);
                }
            }
        }
        Assert.Equal(15 * count, state.TurnNumber);
        if (strike) Assert.Equal(Enumerable.Range(0, count), state.WinnerIndices);
        Assert.Empty(start.WinnerIndices);
        Assert.False(start.GameCompleted);

        foreach (string path in new[] { "roll", "hold/1", "score/ones", "strike/chance" })
        {
            using var rejected = await GameHttpActions.PostAsync(client, $"/api/game/{path}", state, actors[state.CurrentPlayer]);
            await AssertError(rejected, HttpStatusCode.Conflict, "Spillet er afsluttet. Start et nyt spil for at fortsætte.");
            await AssertUnchanged();
        }
        foreach (var (path, message) in new[]
        {
            ("hold/0", "Terningens nummer skal være mellem 1 og 5."),
            ("hold/abc", "Terningens nummer skal være mellem 1 og 5."),
            ("score/unknown", "Pointfeltet er ukendt. Brug et af de 15 felt-id'er."),
            ("strike/CHANCE", "Pointfeltet er ukendt. Brug et af de 15 felt-id'er.")
        })
        {
            using var rejected = await client.PostAsync($"/api/game/{path}", null);
            await AssertError(rejected, HttpStatusCode.BadRequest, message);
            await AssertUnchanged();
        }
        using var invalidNew = await client.PostAsJsonAsync("/api/game/new", new { name = "" });
        await AssertError(invalidNew, HttpStatusCode.BadRequest, "Navnet skal indeholde mellem 1 og 20 tegn.");
        await AssertUnchanged();
        using var replacement = await client.PostAsJsonAsync("/api/game/new", new { name = "Næste" });
        if (count == 1)
        {
            var next = await GameStateAssertions.ReadHttpStateAsync(replacement);
            Assert.NotEqual(state.GameId, next.GameId);
            Assert.Equal(1, next.TurnNumber);
            Assert.False(next.GameCompleted);
            Assert.Empty(next.WinnerIndices);
        }
        else
        {
            await AssertError(replacement, HttpStatusCode.Conflict, "Et lobbyspil er allerede startet og kan ikke nulstilles her.");
            await AssertUnchanged();
        }

        async Task AssertUnchanged()
        {
            using var read = await client.GetAsync("/api/game");
            GameStateAssertions.Equal(state, await GameStateAssertions.ReadHttpStateAsync(read, count));
        }
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "message" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
    }
}
