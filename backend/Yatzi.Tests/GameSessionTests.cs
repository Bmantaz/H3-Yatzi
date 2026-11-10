using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerer sessionens indkapsling direkte, fordi et HTTP-svar allerede er
/// serialiseret og derfor ikke kan afsløre, om C#-objekter deler interne arrays.
/// </summary>
public class GameSessionTests
{
    [Fact]
    public void ToggleHold_PreservesPreviouslyReturnedSnapshots()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var rolled = session.Roll();
        Assert.NotNull(rolled);
        var expectedDice = rolled.Dice.ToArray();

        var held = session.ToggleHold(1);
        var released = session.ToggleHold(1);

        Assert.NotNull(held);
        Assert.NotNull(released);
        Assert.All(rolled.Held, value => Assert.False(value));
        Assert.Equal(new[] { false, true, false, false, false }, held.Held);
        Assert.All(released.Held, value => Assert.False(value));
        foreach (var snapshot in new[] { rolled, held, released })
        {
            Assert.Equal(expectedDice, snapshot.Dice);
            Assert.Equal(1, snapshot.RollsUsed);
            Assert.Equal("Andreas", Assert.Single(snapshot.Players).Name);
        }

        Assert.NotSame(rolled.Held, held.Held);
        Assert.NotSame(held.Held, released.Held);
    }

    [Fact]
    public void Roll_PreservesPreviouslyReturnedSnapshots()
    {
        var session = new GameSession();
        var start = session.NewGame("Andreas");
        var firstRoll = session.Roll();
        Assert.NotNull(firstRoll);
        var expectedFirstDice = firstRoll.Dice.ToArray();

        var secondRoll = session.Roll();

        Assert.NotNull(secondRoll);
        Assert.Equal(0, start.RollsUsed);
        Assert.All(start.Dice, value => Assert.Equal(0, value));
        Assert.Equal(1, firstRoll.RollsUsed);
        Assert.Equal(expectedFirstDice, firstRoll.Dice);
        Assert.Equal(2, secondRoll.RollsUsed);
        Assert.NotSame(start.Dice, firstRoll.Dice);
        Assert.NotSame(firstRoll.Dice, secondRoll.Dice);
        Assert.NotSame(firstRoll.Held, secondRoll.Held);
        Assert.NotSame(firstRoll.Players, secondRoll.Players);
    }

    [Fact]
    public void ReturnedSnapshots_CannotChangeSessionOrOtherSnapshots()
    {
        var session = new GameSession();
        var created = session.NewGame("Andreas");
        var firstRead = session.GetState();
        Assert.NotNull(firstRead);

        created.Dice[0] = 6;
        created.Held[0] = true;
        created.Players[0] = created.Players[0] with { Name = "Frederik" };
        firstRead.Dice[1] = 5;
        firstRead.Held[1] = true;
        firstRead.Players[0] = firstRead.Players[0] with { Name = "Bjarke" };

        var unchanged = session.GetState();

        Assert.NotNull(unchanged);
        Assert.All(unchanged.Dice, value => Assert.Equal(0, value));
        Assert.All(unchanged.Held, value => Assert.False(value));
        Assert.Equal("Andreas", Assert.Single(unchanged.Players).Name);
        Assert.Equal(0, unchanged.CurrentPlayer);
        Assert.Equal(0, unchanged.RollsUsed);
        Assert.Equal(0, firstRead.Dice[0]);
        Assert.False(firstRead.Held[0]);
        Assert.Equal(0, created.Dice[1]);
        Assert.False(created.Held[1]);
    }

    [Fact]
    public void FieldChoice_WithoutGameReturnsNull()
    {
        var session = new GameSession();

        Assert.Null(session.Score(ScoreCategory.Chance));
        Assert.Null(session.Strike(ScoreCategory.Chance));
        Assert.Null(session.GetState());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldChoiceAndNewGame_PreserveEarlierSnapshots(bool strike)
    {
        var session = new GameSession();
        var start = session.NewGame("Andreas");
        var first = session.Roll()!;
        var firstPossible = first.PossibleScores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var held = session.ToggleHold(1)!;
        var second = session.Roll()!;
        var secondPossible = second.PossibleScores.ToDictionary(entry => entry.Key, entry => entry.Value);

        var scored = strike ? session.Strike(ScoreCategory.Chance)! : session.Score(ScoreCategory.Chance)!;
        var expectedScored = session.GetState()!;
        var replacement = session.NewGame("Frederik");

        Assert.All(start.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
        Assert.Empty(start.PossibleScores);
        Assert.All(first.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
        Assert.All(second.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
        Assert.False(first.TurnCompleted);
        Assert.False(second.TurnCompleted);
        Assert.All(first.Held, value => Assert.False(value));
        Assert.True(held.Held[1]);
        Assert.Equal(firstPossible.OrderBy(entry => entry.Key), first.PossibleScores.OrderBy(entry => entry.Key));
        Assert.Equal(secondPossible.OrderBy(entry => entry.Key), second.PossibleScores.OrderBy(entry => entry.Key));
        GameStateAssertions.Equal(expectedScored, scored);
        Assert.Equal(strike ? 0 : second.Dice.Sum(), scored.Players[0].Scorecard.Scores["chance"]);
        Assert.False(scored.TurnCompleted);
        Assert.Equal(new int[5], scored.Dice);
        Assert.Equal(new bool[5], scored.Held);
        Assert.Equal(0, scored.RollsUsed);
        Assert.Empty(scored.PossibleScores);
        Assert.All(replacement.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
        Assert.False(replacement.TurnCompleted);
        Assert.NotSame(first.Players[0], second.Players[0]);
        Assert.NotSame(first.Players[0].Scorecard, second.Players[0].Scorecard);
        Assert.NotSame(first.Players[0].Scorecard.Scores, second.Players[0].Scorecard.Scores);
        Assert.NotSame(first.PossibleScores, second.PossibleScores);
    }

    [Fact]
    public void ReturnedDictionaries_CannotChangeGameOtherResponsesOrFutureFieldChoice()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var rolled = session.Roll()!;
        var other = session.GetState()!;
        var expected = session.GetState()!;

        rolled.Players[0].Scorecard.Scores["chance"] = 999;
        rolled.Players[0].Scorecard.Scores.Remove("ones");
        rolled.PossibleScores["chance"] = 999;
        rolled.PossibleScores.Clear();
        GameStateAssertions.Equal(expected, other);
        GameStateAssertions.Equal(expected, session.GetState()!);

        var scored = session.Score(ScoreCategory.Chance)!;
        var scoredCopy = session.GetState()!;
        scored.Players[0].Scorecard.Scores.Clear();
        scored.PossibleScores["chance"] = 999;

        Assert.Equal(expected.Dice.Sum(), scoredCopy.Players[0].Scorecard.Scores["chance"]);
        GameStateAssertions.Equal(scoredCopy, session.GetState()!);
        GameStateAssertions.Equal(expected, other);
    }
}
