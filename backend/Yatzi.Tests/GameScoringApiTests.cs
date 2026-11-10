using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Tester pointvalg gennem den rigtige HTTP-pipeline, inklusive låsning og samtidighed.
/// Hver testcase har sin egen vært; kast vurderes fra svarenes faktiske terninger.
/// </summary>
public class GameScoringApiTests : IDisposable
{
    private const string InvalidCategory = "Pointfeltet er ukendt. Brug et af de 15 felt-id'er.";
    private const string MissingGame = "Der er ikke oprettet et spil. Start et nyt spil først.";
    private const string BeforeRoll = "Kast terningerne, før du vælger et pointfelt.";
    private const string Completed = "Spillet er afsluttet. Start et nyt spil for at fortsætte.";
    private readonly WebApplicationFactory<Program> _factory = new();

    public static IEnumerable<object[]> Categories() => GameStateAssertions.Categories
        .Select(entry => new object[] { entry.Id, entry.Category });

    [Theory]
    [MemberData(nameof(Categories))]
    public async Task FieldChoice_AllIdsMapToExpectedCoreCategory(string id, ScoreCategory category)
    {
        using var client = _factory.CreateClient();
        foreach (string method in new[] { "score", "strike" })
        {
            await NewGameAsync(client);
            using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
            var before = await GameStateAssertions.ReadHttpStateAsync(roll);

            using var response = await GameHttpActions.PostAsync(client, $"/api/game/{method}/{id}", _factory.Services.GetRequiredService<GameSession>().GetState());
            var after = await GameStateAssertions.ReadHttpStateAsync(response);

            int expected = method == "strike" ? 0 : ScoreCalculator.Calculate(before.Dice, category);
            AssertChosenField(before, after, id, expected);
        }
    }

    [Theory]
    [InlineData("score", 1)]
    [InlineData("score", 2)]
    [InlineData("score", 3)]
    [InlineData("strike", 1)]
    [InlineData("strike", 2)]
    [InlineData("strike", 3)]
    public async Task FieldChoice_AfterAllowedRollsOpensNextTurnForAllClients(string method, int rolls)
    {
        using var client = _factory.CreateClient();
        using var other = _factory.CreateClient();
        await NewGameAsync(client);
        for (int i = 0; i < rolls; i++)
        {
            using var rolled = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
            await GameStateAssertions.ReadHttpStateAsync(rolled);
            if (i == 0)
            {
                using var held = await GameHttpActions.PostAsync(client, "/api/game/hold/2", _factory.Services.GetRequiredService<GameSession>().GetState());
                await GameStateAssertions.ReadHttpStateAsync(held);
            }
        }
        var before = await GetAsync(client);
        Assert.True(before.PossibleScores["chance"] > 0);

        using var response = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        var after = await GameStateAssertions.ReadHttpStateAsync(response);
        AssertChosenField(before, after, "chance", method == "strike" ? 0 : before.Dice.Sum());

        foreach (string path in new[] { "hold/1", "hold/2", "score/chance", "score/ones", "strike/chance", "strike/yatzy" })
        {
            using var rejected = await GameHttpActions.PostAsync(other, $"/api/game/{path}", _factory.Services.GetRequiredService<GameSession>().GetState());
            await AssertErrorAsync(rejected, HttpStatusCode.Conflict, path.StartsWith("hold/")
                ? "Hold/slip er kun tilladt efter første eller andet kast." : BeforeRoll);
            GameStateAssertions.Equal(after, await GetAsync(client));
        }

        using var invalidHold = await GameHttpActions.PostAsync(client, "/api/game/hold/6", _factory.Services.GetRequiredService<GameSession>().GetState());
        await AssertErrorAsync(invalidHold, HttpStatusCode.BadRequest, "Terningens nummer skal være mellem 1 og 5.");
        GameStateAssertions.Equal(after, await GetAsync(other));
        GameStateAssertions.Equal(after, await GetAsync(other));
        using var nextRoll = await GameHttpActions.PostAsync(other, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        var next = await GameStateAssertions.ReadHttpStateAsync(nextRoll);
        Assert.Equal(1, next.RollsUsed);
        Assert.Equal(after.Players[0].Scorecard.Scores["chance"], next.Players[0].Scorecard.Scores["chance"]);
        Assert.False(next.PossibleScores.ContainsKey("chance"));
        using var used = await GameHttpActions.PostAsync(client, "/api/game/score/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        await AssertErrorAsync(used, HttpStatusCode.Conflict, "Pointfeltet er allerede udfyldt.");
        GameStateAssertions.Equal(next, await GetAsync(client));
    }

    [Fact]
    public async Task Score_MissingFaceStoresZeroAndOpensNextTurn()
    {
        using var client = _factory.CreateClient();
        await NewGameAsync(client);
        using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        var before = await GameStateAssertions.ReadHttpStateAsync(roll);
        // Fem terninger kan ikke indeholde alle seks øjental. Nullet er derfor garanteret.
        var missing = GameStateAssertions.Categories.Take(6).First(entry => !before.Dice.Contains((int)entry.Category));
        Assert.Equal(0, before.PossibleScores[missing.Id]);

        using var response = await GameHttpActions.PostAsync(client, $"/api/game/score/{missing.Id}", _factory.Services.GetRequiredService<GameSession>().GetState());

        AssertChosenField(before, await GameStateAssertions.ReadHttpStateAsync(response), missing.Id, 0);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("strike")]
    public async Task FieldChoice_MissingGameAndBeforeRollReturnErrorsWithoutChanges(string method)
    {
        using var client = _factory.CreateClient();
        using var missing = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, MissingGame);
        using var stillMissing = await client.GetAsync("/api/game");
        await AssertErrorAsync(stillMissing, HttpStatusCode.NotFound, MissingGame);

        var before = await NewGameAsync(client);
        using var rejected = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, BeforeRoll);
        GameStateAssertions.Equal(before, await GetAsync(client));

        using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        await GameStateAssertions.ReadHttpStateAsync(roll);
        using var accepted = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        Assert.False((await GameStateAssertions.ReadHttpStateAsync(accepted)).TurnCompleted);
    }

    public static IEnumerable<object[]> InvalidCategories()
    {
        foreach (string method in new[] { "score", "strike" })
        {
            foreach (string id in new[] { "OnePair", "ONES", "fullhouse", "0", "1", "15", "-1", "Ones,Twos", "ones,twos", "bonus", "total", " onePair", "onePair ", "unknown" })
            {
                yield return new object[] { method, id };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCategories))]
    public async Task FieldChoice_InvalidIdHasPriorityAtEveryStage(string method, string id)
    {
        using var client = _factory.CreateClient();
        for (int stage = 0; stage < 3; stage++)
        {
            if (stage == 1)
            {
                await NewGameAsync(client);
                using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
                await GameStateAssertions.ReadHttpStateAsync(roll);
            }
            if (stage == 2)
            {
                using var score = await GameHttpActions.PostAsync(client, "/api/game/score/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
                await GameStateAssertions.ReadHttpStateAsync(score);
            }
            var before = stage == 0 ? null : await GetAsync(client);

            using var rejected = await GameHttpActions.PostAsync(client, $"/api/game/{method}/{Uri.EscapeDataString(id)}", _factory.Services.GetRequiredService<GameSession>().GetState());

            await AssertErrorAsync(rejected, HttpStatusCode.BadRequest, InvalidCategory);
            if (before is null)
            {
                using var missing = await client.GetAsync("/api/game");
                await AssertErrorAsync(missing, HttpStatusCode.NotFound, MissingGame);
            }
            else
            {
                GameStateAssertions.Equal(before, await GetAsync(client));
            }
        }
    }

    [Theory]
    [InlineData("score")]
    [InlineData("strike")]
    public async Task NewGame_AfterFieldChoiceClearsCardAndTurn(string method)
    {
        using var client = _factory.CreateClient();
        await NewGameAsync(client);
        using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        await GameStateAssertions.ReadHttpStateAsync(roll);
        using var held = await GameHttpActions.PostAsync(client, "/api/game/hold/2", _factory.Services.GetRequiredService<GameSession>().GetState());
        await GameStateAssertions.ReadHttpStateAsync(held);
        using var score = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        var completed = await GameStateAssertions.ReadHttpStateAsync(score);

        var start = await NewGameAsync(client, "Frederik");

        Assert.False(start.TurnCompleted);
        Assert.All(start.Dice, die => Assert.Equal(0, die));
        Assert.All(start.Held, value => Assert.False(value));
        Assert.Equal(0, start.RollsUsed);
        Assert.Empty(start.PossibleScores);
        Assert.All(start.Players[0].Scorecard.Scores.Values, value => Assert.Null(value));
        Assert.Equal(0, start.Players[0].Scorecard.Total);
        GameStateAssertions.Equal(start, await GetAsync(client));
        Assert.False(completed.TurnCompleted);
    }

    [Theory]
    [InlineData("score/chance", "score/chance")]
    [InlineData("score/chance", "score/fives")]
    [InlineData("strike/chance", "strike/chance")]
    [InlineData("strike/chance", "strike/fives")]
    [InlineData("score/chance", "strike/chance")]
    [InlineData("score/chance", "strike/fives")]
    public async Task ConcurrentFieldChoices_OnlyWinnerCanFillAField(string firstPath, string secondPath)
    {
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        await NewGameAsync(firstClient);
        using var roll = await GameHttpActions.PostAsync(firstClient, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        var before = await GameStateAssertions.ReadHttpStateAsync(roll);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] paths = { firstPath, secondPath };
        HttpClient[] clients = { firstClient, secondClient };
        var tasks = clients.Select(async (client, i) =>
        {
            await start.Task;
            return await GameHttpActions.PostAsync(client, $"/api/game/{paths[i]}", before);
        }).ToArray();
        start.SetResult(true);
        var responses = await Task.WhenAll(tasks);
        try
        {
            int winner = Assert.Single(Enumerable.Range(0, 2), i => responses[i].StatusCode == HttpStatusCode.OK);
            await AssertErrorAsync(responses[1 - winner], HttpStatusCode.Conflict, "Turen er ikke længere aktuel. Hent spiltilstanden igen.");
            var after = await GameStateAssertions.ReadHttpStateAsync(responses[winner]);
            string id = paths[winner].Split('/')[1];
            int expected = paths[winner].StartsWith("strike/") ? 0 : before.PossibleScores[id];
            AssertChosenField(before, after, id, expected);
            GameStateAssertions.Equal(after, await GetAsync(firstClient));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentRollAndScore_UseOldTurnOrRejectStaleRoll()
    {
        using var client = _factory.CreateClient();
        using var other = _factory.CreateClient();
        await NewGameAsync(client);
        using var firstRoll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
        var before = await GameStateAssertions.ReadHttpStateAsync(firstRoll);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> SendAsync(HttpClient sender, string path)
        {
            await start.Task;
            return await GameHttpActions.PostAsync(sender, path, before);
        }
        var rollTask = SendAsync(client, "/api/game/roll");
        var scoreTask = SendAsync(other, "/api/game/score/chance");
        start.SetResult(true);
        using var roll = await rollTask;
        using var score = await scoreTask;
        var after = await GameStateAssertions.ReadHttpStateAsync(score);

        Assert.False(after.TurnCompleted);
        if (roll.IsSuccessStatusCode)
        {
            var rolled = await GameStateAssertions.ReadHttpStateAsync(roll);
            Assert.Equal(2, rolled.RollsUsed);
            AssertChosenField(rolled, after, "chance", rolled.Dice.Sum());
            Assert.Null(rolled.Players[0].Scorecard.Scores["chance"]);
            GameStateAssertions.Equal(after, await GetAsync(client));
        }
        else
        {
            await AssertErrorAsync(roll, HttpStatusCode.Conflict, "Turen er ikke længere aktuel. Hent spiltilstanden igen.");
            AssertChosenField(before, after, "chance", before.Dice.Sum());
            GameStateAssertions.Equal(after, await GetAsync(client));
        }
    }

    private static void AssertChosenField(GameStateResponse before, GameStateResponse after, string id, int expected)
    {
        Assert.Equal(new int[5], after.Dice);
        Assert.Equal(new bool[5], after.Held);
        Assert.Equal(0, after.RollsUsed);
        Assert.Equal(before.CurrentPlayer, after.CurrentPlayer);
        Assert.False(after.TurnCompleted);
        Assert.Empty(after.PossibleScores);
        var filled = Assert.Single(after.Players[0].Scorecard.Scores, entry => entry.Value.HasValue);
        Assert.Equal(id, filled.Key);
        Assert.Equal(expected, filled.Value);
        Assert.All(before.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
    }

    [Theory]
    [InlineData("score")]
    [InlineData("strike")]
    public async Task FullScorecard_LastFieldReturnsSuccessAndPreservesFinalDice(string method)
    {
        using var client = _factory.CreateClient();
        await NewGameAsync(client);
        foreach (var (id, category) in GameStateAssertions.Categories)
        {
            using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState());
            var before = await GameStateAssertions.ReadHttpStateAsync(roll);
            using var chosen = await GameHttpActions.PostAsync(client, $"/api/game/{method}/{id}", _factory.Services.GetRequiredService<GameSession>().GetState());
            var state = await GameStateAssertions.ReadHttpStateAsync(chosen);
            Assert.Equal(method == "strike" ? 0 : ScoreCalculator.Calculate(before.Dice, category), state.Players[0].Scorecard.Scores[id]);
            bool last = id == "yatzy";
            Assert.Equal(last, state.TurnCompleted);
            Assert.Equal(last, state.GameCompleted);
            Assert.Equal(before.GameId, state.GameId);
            Assert.Equal(last ? before.TurnNumber : before.TurnNumber + 1, state.TurnNumber);
            Assert.Equal(last ? new[] { 0 } : [], state.WinnerIndices);
            Assert.Equal(last ? before.RollsUsed : 0, state.RollsUsed);
            Assert.Equal(last ? before.Dice : new int[5], state.Dice);
        }
        var completed = await GetAsync(client);
        Assert.All(completed.Players[0].Scorecard.Scores.Values, value => Assert.True(value.HasValue));
        foreach (string path in new[] { "roll", "hold/1", "score/ones", "strike/chance" })
        {
            using var rejected = await GameHttpActions.PostAsync(client, $"/api/game/{path}", _factory.Services.GetRequiredService<GameSession>().GetState());
            await AssertErrorAsync(rejected, HttpStatusCode.Conflict, Completed);
            GameStateAssertions.Equal(completed, await GetAsync(client));
        }
    }

    private static async Task<GameStateResponse> NewGameAsync(HttpClient client, string name = "Andreas")
    {
        using var response = await client.PostAsJsonAsync("/api/game/new", new { name });
        var state = await GameStateAssertions.ReadHttpStateAsync(response);
        Assert.Equal(name, state.Players[0].Name);
        return state;
    }

    private static async Task<GameStateResponse> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/game");
        return await GameStateAssertions.ReadHttpStateAsync(response);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("message", Assert.Single(document.RootElement.EnumerateObject()).Name);
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
    }

    public void Dispose() => _factory.Dispose();
}
