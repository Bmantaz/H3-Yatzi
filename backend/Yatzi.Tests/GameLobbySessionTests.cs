using System.Text.Json;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Tester sessionens lobbydeltagere, egne pointtavler, kopier og nulstillingsværn.</summary>
public class GameLobbySessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void NewLobbyGame_CopiesParticipantsAndInitializesIndependentScorecards(int count)
    {
        var session = new GameSession();
        var players = Players(count);
        string[] names = players.Select(player => player.Name).ToArray();
        var state = session.NewLobbyGame(players);
        players[0] = new(Guid.NewGuid(), "Ændret input");
        Assert.Equal(names, state.Players.Select(player => player.Name));
        Assert.Equal(names, session.GetState()!.Players.Select(player => player.Name));
        Assert.Equal(0, state.CurrentPlayer);
        Assert.Equal(new int[5], state.Dice);
        Assert.Equal(new bool[5], state.Held);
        Assert.Equal(0, state.RollsUsed);
        Assert.Empty(state.PossibleScores);
        Assert.False(state.TurnCompleted);
        Assert.NotEqual(Guid.Empty, state.GameId);
        Assert.Equal(1, state.TurnNumber);
        Assert.False(state.GameCompleted);
        Assert.Empty(state.WinnerIndices);
        Assert.All(state.Players, player =>
        {
            Assert.Equal(15, player.Scorecard.Scores.Count);
            Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score));
            Assert.Equal(0, player.Scorecard.UpperSum);
            Assert.Equal(0, player.Scorecard.Bonus);
            Assert.Equal(0, player.Scorecard.Total);
        });
        Assert.Equal(count, state.Players.Select(player => player.Scorecard.Scores).Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void InvalidParticipantCount_DoesNotReplaceSinglePlayerGame(int count)
    {
        var session = new GameSession();
        session.NewGame("Gammelt spil");
        session.Roll();
        string before = JsonSerializer.Serialize(session.GetState());
        Assert.Throws<ArgumentException>(() => session.NewLobbyGame(Players(count)));
        Assert.Equal(before, JsonSerializer.Serialize(session.GetState()));
        Assert.Equal("Nyt selvstændigt spil", session.NewGame("Nyt selvstændigt spil").Players[0].Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPlayerIds_DoNotCreateLobbyGame(bool duplicate)
    {
        var session = new GameSession();
        var players = Players(2);
        players[1] = players[1] with { Id = duplicate ? players[0].Id : Guid.Empty };
        Assert.Throws<ArgumentException>(() => session.NewLobbyGame(players));
        Assert.Null(session.GetState());
        session.NewGame("A");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldChoice_ChangesOnlyActiveScorecardAndOpensNextTurn(bool strike)
    {
        var session = new GameSession();
        var start = session.NewLobbyGame(Players(6));
        var rolled = session.Roll()!;
        session.ToggleHold(1);
        var chosen = (strike ? session.Strike(ScoreCategory.Chance) : session.Score(ScoreCategory.Chance))!;
        Assert.Equal(strike ? 0 : rolled.Dice.Sum(), chosen.Players[0].Scorecard.Scores["chance"]);
        Assert.All(chosen.Players.Skip(1), player => Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score)));
        Assert.All(start.Players, player => Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score)));
        AssertFreshTurn(chosen, 1);
        string before = JsonSerializer.Serialize(chosen);
        Assert.Throws<InvalidOperationException>(() => session.ToggleHold(0));
        Assert.Throws<InvalidOperationException>(() => session.Score(ScoreCategory.Ones));
        Assert.Throws<InvalidOperationException>(() => session.Strike(ScoreCategory.Ones));
        Assert.Equal(before, JsonSerializer.Serialize(session.GetState()));
        Assert.Equal(1, session.Roll()!.RollsUsed);
    }

    [Fact]
    public void Responses_DoNotShareAnyPlayersCardsDiceHeldOrPossibleScores()
    {
        var session = new GameSession();
        session.NewLobbyGame(Players(6));
        var state = session.Roll()!;
        string before = JsonSerializer.Serialize(session.GetState());
        foreach (var player in state.Players) player.Scorecard.Scores["ones"] = 30;
        state.Players[0] = new("Ændret", state.Players[0].Scorecard);
        state.Dice[0] = 99;
        state.Held[0] = true;
        state.PossibleScores.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(session.GetState()));
    }

    [Fact]
    public void RepeatedCreation_CannotResetOrReplaceLobbyGame()
    {
        var session = new GameSession();
        session.NewGame("Før lobby");
        session.Roll();
        var lobby = session.NewLobbyGame(Players(2));
        Assert.Equal(0, lobby.RollsUsed);
        session.Roll();
        session.Score(ScoreCategory.Chance);
        Assert.Equal(1, session.GetState()!.CurrentPlayer);
        session.Roll();
        string before = JsonSerializer.Serialize(session.GetState());
        Assert.Throws<InvalidOperationException>(() => session.NewGame("Overskrivning"));
        Assert.Throws<InvalidOperationException>(() => session.NewLobbyGame(Players(1)));
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.NewLobbyGame(Players(2), stopped.Token));
        Assert.Equal(before, JsonSerializer.Serialize(session.GetState()));
    }

    [Fact]
    public void CancellationBeforeCreation_PreservesExistingGame()
    {
        var session = new GameSession();
        var before = session.NewGame("A");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.NewLobbyGame(Players(2), stopped.Token));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(session.GetState()));
        session.NewGame("B");
    }

    [Fact]
    public async Task ConcurrentSinglePlayerCreationAndLobbyStart_EndWithProtectedLobbyGame()
    {
        var session = new GameSession();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var standalone = Task.Run(async () =>
        {
            await start.Task;
            try { session.NewGame("Selvstændigt"); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var lobby = Task.Run(async () => { await start.Task; return session.NewLobbyGame(Players(2)); });
        start.SetResult();
        await Task.WhenAll(standalone, lobby).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "Spiller 0", "Spiller 1" }, session.GetState()!.Players.Select(player => player.Name));
        Assert.Throws<InvalidOperationException>(() => session.NewGame("Sen oprettelse"));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    public void Rotation_ReusesEachPlayersScorecardAcrossSeveralRounds(int count, bool strike)
    {
        var session = new GameSession();
        var players = Players(count);
        session.NewLobbyGame(players);
        var snapshots = new List<(GameStateResponse State, string Json)>();
        foreach (var (id, category) in GameStateAssertions.Categories.Take(3))
        {
            for (int i = 0; i < count; i++)
            {
                AssertFreshTurn(session.GetState()!, i);
                var rolled = session.RollFromOpla(players[i].Id)!;
                var before = session.ToggleHoldFromOpla(1, players[i].Id)!;
                snapshots.Add((before, JsonSerializer.Serialize(before)));
                var chosen = (strike ? session.Strike(category) : session.Score(category))!;
                AssertFreshTurn(chosen, (i + 1) % count);
                int expected = strike ? 0 : ScoreCalculator.Calculate(rolled.Dice, category);
                Assert.Equal(expected, chosen.Players[i].Scorecard.Scores[id]);
                Assert.Equal(before.Players[i].Scorecard.Total + expected, chosen.Players[i].Scorecard.Total);
                for (int other = 0; other < count; other++)
                    if (other != i)
                        Assert.Equal(JsonSerializer.Serialize(before.Players[other]), JsonSerializer.Serialize(chosen.Players[other]));
            }
        }

        Assert.All(session.GetState()!.Players, player => Assert.Equal(3, player.Scorecard.Scores.Values.Count(score => score.HasValue)));
        foreach (var snapshot in snapshots) Assert.Equal(snapshot.Json, JsonSerializer.Serialize(snapshot.State));
        var current = session.GetState()!;
        string currentJson = JsonSerializer.Serialize(current);
        snapshots[0].State.Players[0].Scorecard.Scores["ones"] = 999;
        snapshots[0].State.Dice[0] = 99;
        Assert.Equal(currentJson, JsonSerializer.Serialize(session.GetState()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedFieldChoices_NeverAdvanceOrChangeScorecards(bool strike)
    {
        var session = new GameSession();
        session.NewLobbyGame(Players(2));
        for (int i = 0; i < 2; i++)
        {
            session.Roll();
            session.Strike(ScoreCategory.Chance);
        }
        string fresh = JsonSerializer.Serialize(session.GetState());
        Assert.Throws<InvalidOperationException>(() => Choose(ScoreCategory.Ones));
        Assert.Equal(fresh, JsonSerializer.Serialize(session.GetState()));
        session.Roll();
        string rolled = JsonSerializer.Serialize(session.GetState());
        Assert.Throws<InvalidOperationException>(() => Choose(ScoreCategory.Chance));
        Assert.Throws<ArgumentOutOfRangeException>(() => Choose((ScoreCategory)0));
        Assert.Equal(rolled, JsonSerializer.Serialize(session.GetState()));

        GameStateResponse? Choose(ScoreCategory category) => strike ? session.Strike(category) : session.Score(category);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    public void FullGame_LastFieldPreservesFinalTurnAndEarlierFullPlayersDoNotStopRotation(int count, bool strike)
    {
        var session = new GameSession();
        session.NewLobbyGame(Players(count));
        GameStateResponse? finalRoll = null;
        GameStateResponse? result = null;
        var categories = GameStateAssertions.Categories;
        for (int field = 0; field < categories.Length; field++)
        {
            for (int player = 0; player < count; player++)
            {
                AssertFreshTurn(session.GetState()!, player);
                session.Roll();
                finalRoll = session.ToggleHold(0)!;
                var (id, category) = categories[field];
                result = (strike ? session.Strike(category) : session.Score(category))!;
                Assert.Equal(strike ? 0 : ScoreCalculator.Calculate(finalRoll.Dice, category), result.Players[player].Scorecard.Scores[id]);
                if (field < categories.Length - 1 || player < count - 1)
                {
                    AssertFreshTurn(result, (player + 1) % count);
                    Assert.False(result.GameCompleted);
                    Assert.Empty(result.WinnerIndices);
                    Assert.Equal(field * count + player + 2, result.TurnNumber);
                }
                if (field == categories.Length - 1)
                    Assert.All(result.Players[player].Scorecard.Scores.Values, score => Assert.True(score.HasValue));
            }
        }

        Assert.NotNull(result);
        Assert.NotNull(finalRoll);
        Assert.True(result.TurnCompleted);
        Assert.True(result.GameCompleted);
        Assert.Equal(15 * count, result.TurnNumber);
        int maximum = result.Players.Max(player => player.Scorecard.Total);
        Assert.Equal(Enumerable.Range(0, count).Where(index => result.Players[index].Scorecard.Total == maximum), result.WinnerIndices);
        if (strike) Assert.Equal(Enumerable.Range(0, count), result.WinnerIndices);
        Assert.Equal(count - 1, result.CurrentPlayer);
        Assert.Equal(finalRoll.Dice, result.Dice);
        Assert.Equal(finalRoll.Held, result.Held);
        Assert.Equal(finalRoll.RollsUsed, result.RollsUsed);
        Assert.Empty(result.PossibleScores);
        Assert.All(result.Players, player =>
        {
            Assert.All(player.Scorecard.Scores.Values, score => Assert.True(score.HasValue));
            Assert.Equal(player.Scorecard.Scores.Values.Sum(score => score!.Value) + player.Scorecard.Bonus, player.Scorecard.Total);
        });
        string completed = JsonSerializer.Serialize(result);
        Action[] actions = [() => session.Roll(), () => session.ToggleHold(0), () => session.Score(ScoreCategory.Ones), () => session.Strike(ScoreCategory.Chance)];
        foreach (var action in actions)
        {
            Assert.Equal("Spillet er afsluttet. Start et nyt spil for at fortsætte.", Assert.Throws<InvalidOperationException>(action).Message);
            Assert.Equal(completed, JsonSerializer.Serialize(session.GetState()));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task ConcurrentFieldChoices_WithoutNewRollOnlyFillOneField(int count)
    {
        var session = new GameSession();
        session.NewLobbyGame(Players(count));
        var rolled = session.Roll()!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try { return (Index: index, State: index == 0 ? session.Score(ScoreCategory.Chance) : session.Strike(ScoreCategory.Ones)); }
            catch (InvalidOperationException exception)
            {
                Assert.Equal("Kast terningerne, før du vælger et pointfelt.", exception.Message);
                return (Index: index, State: (GameStateResponse?)null);
            }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        var accepted = Assert.Single(results, result => result.State is not null);
        var state = session.GetState()!;
        AssertFreshTurn(state, 1 % count);
        var filled = Assert.Single(state.Players.SelectMany(player => player.Scorecard.Scores), entry => entry.Value.HasValue);
        Assert.Equal(accepted.Index == 0 ? "chance" : "ones", filled.Key);
        Assert.Equal(accepted.Index == 0 ? rolled.Dice.Sum() : 0, filled.Value);
        Assert.Equal(JsonSerializer.Serialize(accepted.State), JsonSerializer.Serialize(state));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    public void RollAndFieldChoice_BothOrdersPreserveScoringDiceAndResponseCopies(int count, bool rollFirst)
    {
        var session = new GameSession();
        var players = Players(count);
        session.NewLobbyGame(players);
        var before = session.RollFromOpla(players[0].Id)!;
        GameStateResponse rolled;
        GameStateResponse scored;
        if (rollFirst)
        {
            rolled = session.RollFromOpla(players[0].Id)!;
            scored = session.Score(ScoreCategory.Chance)!;
            Assert.Equal(2, rolled.RollsUsed);
            Assert.Null(rolled.Players[0].Scorecard.Scores["chance"]);
            Assert.Equal(rolled.Dice.Sum(), scored.Players[0].Scorecard.Scores["chance"]);
            Assert.Equal(JsonSerializer.Serialize(scored), JsonSerializer.Serialize(session.GetState()));
        }
        else
        {
            scored = session.Score(ScoreCategory.Chance)!;
            rolled = session.RollFromOpla(players[1 % count].Id)!;
            Assert.Equal(1, rolled.RollsUsed);
            Assert.Equal(before.Dice.Sum(), scored.Players[0].Scorecard.Scores["chance"]);
            Assert.Equal(scored.Players[0].Scorecard.Scores["chance"], rolled.Players[0].Scorecard.Scores["chance"]);
            Assert.Equal(JsonSerializer.Serialize(rolled), JsonSerializer.Serialize(session.GetState()));
        }
        AssertFreshTurn(scored, 1 % count);
        Assert.All(before.Players, player => Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score)));
    }

    private static void AssertFreshTurn(GameStateResponse state, int player)
    {
        Assert.Equal(player, state.CurrentPlayer);
        Assert.False(state.TurnCompleted);
        Assert.Equal(new int[5], state.Dice);
        Assert.Equal(new bool[5], state.Held);
        Assert.Equal(0, state.RollsUsed);
        Assert.Empty(state.PossibleScores);
    }

    private static LobbyGamePlayer[] Players(int count) => Enumerable.Range(0, count)
        .Select(index => new LobbyGamePlayer(Guid.NewGuid(), $"Spiller {index}")).ToArray();
}
