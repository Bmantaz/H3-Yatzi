using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Fælles kontraktkontrol og indholdssammenligning af spillets kopierede svar.
/// Testtabellen er eksplicit, så en forkert produktionsmapping også opdages.
/// </summary>
internal static class GameStateAssertions
{
    internal static readonly (string Id, ScoreCategory Category)[] Categories =
    {
        ("ones", ScoreCategory.Ones), ("twos", ScoreCategory.Twos),
        ("threes", ScoreCategory.Threes), ("fours", ScoreCategory.Fours),
        ("fives", ScoreCategory.Fives), ("sixes", ScoreCategory.Sixes),
        ("onePair", ScoreCategory.OnePair), ("twoPairs", ScoreCategory.TwoPairs),
        ("threeOfAKind", ScoreCategory.ThreeOfAKind), ("fourOfAKind", ScoreCategory.FourOfAKind),
        ("smallStraight", ScoreCategory.SmallStraight), ("largeStraight", ScoreCategory.LargeStraight),
        ("fullHouse", ScoreCategory.FullHouse), ("chance", ScoreCategory.Chance), ("yatzy", ScoreCategory.Yatzy)
    };

    internal static void AssertJsonShape(JsonElement state)
    {
        AssertNames(state, "players", "currentPlayer", "dice", "held", "rollsUsed", "possibleScores", "turnCompleted");
        var players = state.GetProperty("players");
        Assert.Equal(1, players.GetArrayLength());
        var player = players[0];
        AssertNames(player, "name", "scorecard");
        Assert.InRange(player.GetProperty("name").GetString()!.Length, 1, 20);
        Assert.Equal(0, state.GetProperty("currentPlayer").GetInt32());
        int rolls = state.GetProperty("rollsUsed").GetInt32();
        Assert.InRange(rolls, 0, 3);
        var diceValues = state.GetProperty("dice");
        Assert.Equal(5, diceValues.GetArrayLength());
        Assert.All(diceValues.EnumerateArray(), die => Assert.InRange(die.GetInt32(), rolls == 0 ? 0 : 1, rolls == 0 ? 0 : 6));
        var held = state.GetProperty("held");
        Assert.Equal(5, held.GetArrayLength());
        Assert.All(held.EnumerateArray(), value => Assert.True(value.ValueKind is JsonValueKind.True or JsonValueKind.False));
        var card = player.GetProperty("scorecard");
        AssertNames(card, "scores", "upperSum", "bonus", "total");
        var scores = card.GetProperty("scores");
        AssertNames(scores, Categories.Select(entry => entry.Id).ToArray());
        int upperSum = 0;
        int sum = 0;
        foreach (var (id, category) in Categories)
        {
            var score = scores.GetProperty(id);
            Assert.True(score.ValueKind is JsonValueKind.Null or JsonValueKind.Number);
            if (score.ValueKind == JsonValueKind.Number)
            {
                int value = score.GetInt32();
                Assert.InRange(value, 0, 50);
                sum += value;
                if ((int)category <= 6)
                {
                    upperSum += value;
                }
            }
        }
        Assert.Equal(upperSum, card.GetProperty("upperSum").GetInt32());
        int bonus = upperSum >= 63 ? 50 : 0;
        Assert.Equal(bonus, card.GetProperty("bonus").GetInt32());
        Assert.Equal(sum + bonus, card.GetProperty("total").GetInt32());

        var possible = state.GetProperty("possibleScores");
        bool completed = state.GetProperty("turnCompleted").GetBoolean();
        var expectedIds = rolls == 0 || completed
            ? Array.Empty<string>()
            : Categories.Where(entry => scores.GetProperty(entry.Id).ValueKind == JsonValueKind.Null)
                .Select(entry => entry.Id).ToArray();
        AssertNames(possible, expectedIds);
        if (expectedIds.Length > 0)
        {
            int[] dice = state.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()).ToArray();
            foreach (var (id, category) in Categories.Where(entry => expectedIds.Contains(entry.Id)))
            {
                Assert.Equal(ScoreCalculator.Calculate(dice, category), possible.GetProperty(id).GetInt32());
            }
        }
    }

    internal static async Task<GameStateResponse> ReadHttpStateAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertJsonShape(document.RootElement);
        var state = await response.Content.ReadFromJsonAsync<GameStateResponse>();
        Assert.NotNull(state);
        return state;
    }

    internal static void Equal(GameStateResponse expected, GameStateResponse actual)
    {
        Assert.Equal(expected.CurrentPlayer, actual.CurrentPlayer);
        Assert.Equal(expected.Dice, actual.Dice);
        Assert.Equal(expected.Held, actual.Held);
        Assert.Equal(expected.RollsUsed, actual.RollsUsed);
        Assert.Equal(expected.TurnCompleted, actual.TurnCompleted);
        Assert.Equal(expected.PossibleScores.OrderBy(entry => entry.Key), actual.PossibleScores.OrderBy(entry => entry.Key));
        Assert.Equal(expected.Players.Length, actual.Players.Length);
        for (int i = 0; i < expected.Players.Length; i++)
        {
            Assert.Equal(expected.Players[i].Name, actual.Players[i].Name);
            var first = expected.Players[i].Scorecard;
            var second = actual.Players[i].Scorecard;
            Assert.Equal(first.Scores.OrderBy(entry => entry.Key), second.Scores.OrderBy(entry => entry.Key));
            Assert.Equal(first.UpperSum, second.UpperSum);
            Assert.Equal(first.Bonus, second.Bonus);
            Assert.Equal(first.Total, second.Total);
        }
    }

    private static void AssertNames(JsonElement element, params string[] names) =>
        Assert.Equal(names.OrderBy(name => name), element.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
}
