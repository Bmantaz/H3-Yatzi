using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Omkamp nulstiller hele spillet atomisk, mens identitet, tilfældighedskilde og gamle svar bevares.</summary>
public class GameRestartSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void Restart_ClearsBonusAndResultPreservesPlayersAndSupportsRepeatedGames(int count)
    {
        var random = new RestartRandom();
        var game = new GameSession(random);
        var players = Enumerable.Range(0, count).Select(_ => new LobbyGamePlayer(Guid.NewGuid(), "Alex")).ToArray();
        var originalStart = game.NewLobbyGame(players);
        for (int round = 0; round < 2; round++)
        {
            foreach (var (_, category) in GameStateAssertions.Categories)
            {
                random.Face = (int)category <= 6 ? (int)category : 1;
                foreach (var player in players)
                {
                    var state = game.GetState()!;
                    var rolled = game.Roll(GameHttpActions.Context(state, player.Id)).State!;
                    var context = GameHttpActions.Context(rolled, player.Id);
                    if (player == players[0]) game.Score(category, context); else game.Strike(category, context);
                }
            }
            var final = game.GetState()!;
            Assert.True(final.GameCompleted);
            Assert.Equal(105, final.Players[0].Scorecard.UpperSum);
            Assert.Equal(50, final.Players[0].Scorecard.Bonus);
            Assert.Equal(new[] { 0 }, final.WinnerIndices);
            int calls = random.Calls;
            var result = game.RestartLobbyGame(new(final.GameId, players[^1].Id), true);
            Assert.Equal(LobbyRestartStatus.Restarted, result.Status);
            var start = result.State!;
            AssertFreshRestart(start, count);
            Assert.NotEqual(final.GameId, start.GameId);
            Assert.Equal(calls, random.Calls);
            GameStateAssertions.Equal(start, game.GetState()!);
            Assert.All(start.Players, player => Assert.Equal("Alex", player.Name));
            Assert.Equal(105, final.Players[0].Scorecard.UpperSum);
            Assert.Equal(50, final.Players[0].Scorecard.Bonus);
            Assert.True(final.GameCompleted);
            AssertFreshRestart(originalStart, count);

            // Returnerede arrays/tavler må hverken ændre sessionen eller den gamle startkopi.
            start.Dice[0] = 6;
            start.Held[0] = true;
            start.PossibleScores["ones"] = 99;
            start.Players[0].Scorecard.Scores["ones"] = 99;
            final.WinnerIndices[0] = count;
            AssertFreshRestart(game.GetState()!, count);
        }
        Assert.Equal(150 * count, random.Calls);
    }

    [Fact]
    public void Restart_RejectionPriorityAndCancellationPreserveStateAndRandomCalls()
    {
        var random = new RestartRandom();
        var game = new GameSession(random);
        Guid id = Guid.NewGuid();
        Reject(new(Guid.NewGuid(), id), false, LobbyRestartStatus.MissingGame);
        var solo = game.NewGame("Solo");
        Reject(new(Guid.NewGuid(), id), false, LobbyRestartStatus.StaleGame);
        Reject(new(solo.GameId, id), false, LobbyRestartStatus.NotLobbyGame);
        var start = game.NewLobbyGame([new(id, "Alex")]);
        Reject(new(Guid.NewGuid(), Guid.NewGuid()), false, LobbyRestartStatus.StaleGame);
        Reject(new(start.GameId, Guid.NewGuid()), false, LobbyRestartStatus.UnknownPlayer);
        Reject(new(start.GameId, id), false, LobbyRestartStatus.NotCompleted);
        foreach (var (_, category) in GameStateAssertions.Categories) { game.Roll(); game.Strike(category); }
        var final = game.GetState()!;
        Reject(new(final.GameId, id), false, LobbyRestartStatus.Disconnected);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => game.RestartLobbyGame(new(final.GameId, id), true, cancelled.Token));
        GameStateAssertions.Equal(final, game.GetState()!);
        Assert.Equal(75, random.Calls);
        Assert.Throws<InvalidOperationException>(() => game.NewGame("Solo"));
        Assert.Throws<InvalidOperationException>(() => game.NewLobbyGame([new(id, "Alex")]));
        Assert.Equal(LobbyRestartStatus.Restarted, game.RestartLobbyGame(new(final.GameId, id), true).Status);
        Reject(new(final.GameId, id), true, LobbyRestartStatus.StaleGame);

        void Reject(LobbyRestartContext context, bool connected, LobbyRestartStatus expected)
        {
            var before = game.GetState();
            int calls = random.Calls;
            var result = game.RestartLobbyGame(context, connected);
            Assert.Equal(expected, result.Status);
            Assert.Null(result.State);
            if (before is null) Assert.Null(game.GetState()); else GameStateAssertions.Equal(before, game.GetState()!);
            Assert.Equal(calls, random.Calls);
        }
    }

    internal static void AssertFreshRestart(GameStateResponse state, int count)
    {
        Assert.NotEqual(Guid.Empty, state.GameId);
        Assert.Equal(count, state.Players.Length);
        Assert.Equal(1, state.TurnNumber);
        Assert.Equal(0, state.CurrentPlayer);
        Assert.False(state.GameCompleted);
        Assert.False(state.TurnCompleted);
        Assert.Empty(state.WinnerIndices);
        Assert.Empty(state.PossibleScores);
        Assert.Equal(new int[5], state.Dice);
        Assert.Equal(new bool[5], state.Held);
        Assert.Equal(0, state.RollsUsed);
        Assert.All(state.Players, player =>
        {
            Assert.Equal(15, player.Scorecard.Scores.Count);
            Assert.All(player.Scorecard.Scores.Values, Assert.Null);
            Assert.Equal(0, player.Scorecard.UpperSum);
            Assert.Equal(0, player.Scorecard.Bonus);
            Assert.Equal(0, player.Scorecard.Total);
        });
    }

    private sealed class RestartRandom : Random
    {
        public int Calls { get; private set; }
        public int Face { get; set; } = 1;
        public override int Next(int minValue, int maxValue)
        {
            Assert.Equal(1, minValue);
            Assert.Equal(7, maxValue);
            Calls++;
            return Face;
        }
    }
}
