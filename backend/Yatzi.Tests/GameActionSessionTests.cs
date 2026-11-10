using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Typede afvisninger, cancellation og svarkopier må ikke bruge tilfældighed eller ændre spillet.</summary>
public class GameActionSessionTests
{
    [Theory]
    [InlineData("roll")]
    [InlineData("hold")]
    [InlineData("score")]
    [InlineData("strike")]
    public void ContextRejectionAndCancellation_PreserveStateAndRandomCalls(string action)
    {
        var random = new CountingRandom();
        var game = new GameSession(random);
        var missing = Apply(new(Guid.NewGuid(), 1, null));
        Assert.Equal(GameActionStatus.MissingGame, missing.Status);
        Assert.Equal(0, random.Calls);
        game.NewGame("Alex");
        var before = game.Roll()!;
        var context = GameHttpActions.Context(before);
        foreach (var wrong in new[] { context with { GameId = Guid.NewGuid() }, context with { TurnNumber = 2 }, context with { PlayerId = Guid.NewGuid() } })
        {
            var rejected = Apply(wrong);
            Assert.Equal(GameActionStatus.Conflict, rejected.Status);
            Assert.Null(rejected.State);
            GameStateAssertions.Equal(before, game.GetState()!);
            Assert.Equal(5, random.Calls);
        }
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Apply(context, stopped.Token));
        GameStateAssertions.Equal(before, game.GetState()!);
        Assert.Equal(5, random.Calls);
        var accepted = Apply(context);
        Assert.Equal(GameActionStatus.Accepted, accepted.Status);
        Assert.Null(accepted.Message);
        GameStateAssertions.Equal(accepted.State!, game.GetState()!);
        Assert.Equal(action == "roll" ? 10 : 5, random.Calls);
        Assert.Equal(1, before.TurnNumber);
        Assert.Null(before.Players[0].Scorecard.Scores["chance"]);

        GameActionResult Apply(GameActionContext value, CancellationToken token = default) => action switch
        {
            "roll" => game.Roll(value, token),
            "hold" => game.ToggleHold(0, value, token),
            "score" => game.Score(ScoreCategory.Chance, value, token),
            _ => game.Strike(ScoreCategory.Chance, value, token)
        };
    }

    private sealed class CountingRandom : Random
    {
        public int Calls { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            Assert.Equal(1, minValue);
            Assert.Equal(7, maxValue);
            Calls++;
            return 1;
        }
    }
}
