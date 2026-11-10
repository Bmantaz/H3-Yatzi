using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Tester hele sessionens livscyklus med faste kast gennem de offentlige spilmetoder.
/// Ingen tests skriver direkte i tavlerne. Det gør bonus, vindere og slutkapløb reproducerbare.
/// </summary>
public class GameLifecycleSessionTests
{
    private const string Completed = "Spillet er afsluttet. Start et nyt spil for at fortsætte.";

    [Fact]
    public void Constructor_RejectsMissingRandomSource() =>
        Assert.Throws<ArgumentNullException>(() => new GameSession(null!));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void NewLobbyGame_ReplacesSinglePlayerIdentityAndStartsAtTurnOne(int count)
    {
        var random = Ones(10);
        var session = new GameSession(random);
        var original = session.NewGame("Første");
        session.Roll();
        var secondTurn = session.Score(ScoreCategory.Chance)!;
        Assert.Equal(2, secondTurn.TurnNumber);
        Assert.Equal(original.GameId, secondTurn.GameId);

        var lobby = session.NewLobbyGame(Players(count));

        Assert.NotEqual(Guid.Empty, lobby.GameId);
        Assert.NotEqual(original.GameId, lobby.GameId);
        AssertStart(lobby, count);
        Assert.Equal(5, random.CallCount);
        GameStateAssertions.Equal(lobby, session.GetState()!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void TurnNumber_OnlyChangesWhenAnAcceptedFieldOpensAnotherTurn(int count)
    {
        var random = Ones(2 * count * 5);
        var session = new GameSession(random);
        var start = count == 1 ? session.NewGame("Alex") : session.NewLobbyGame(Players(count));
        AssertStart(start, count);
        for (int turn = 1; turn <= 2 * count; turn++)
        {
            var before = session.GetState()!;
            Assert.Equal(turn, before.TurnNumber);
            Assert.Equal((turn - 1) % count, before.CurrentPlayer);
            Assert.Throws<InvalidOperationException>(() => session.Score(ScoreCategory.Chance));
            Assert.Throws<InvalidOperationException>(() => session.ToggleHold(0));
            GameStateAssertions.Equal(before, session.GetState()!);
            var rolled = session.Roll()!;
            var held = session.ToggleHold(0)!;
            Assert.Equal(turn, rolled.TurnNumber);
            Assert.Equal(turn, held.TurnNumber);
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Strike((ScoreCategory)999));
            GameStateAssertions.Equal(held, session.GetState()!);
            var next = turn <= count ? session.Score(ScoreCategory.Chance)! : session.Strike(ScoreCategory.Ones)!;
            Assert.Equal(turn + 1, next.TurnNumber);
            Assert.Equal(start.GameId, next.GameId);
            Assert.False(next.GameCompleted);
            Assert.Empty(next.WinnerIndices);
            Assert.Equal(turn, held.TurnNumber);
        }
        Assert.Equal(2 * count * 5, random.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Winners_UseIndicesForEqualNamesAndIncludeEveryHighestTotal(bool tie)
    {
        int[] expected = tie ? [1, 4] : [4];
        var random = new SequenceRandom(Enumerable.Repeat(2, 6 * 15 * 5));
        var session = new GameSession(random);
        var players = Players(6);
        session.NewLobbyGame(players);
        foreach (var (_, category) in GameStateAssertions.Categories)
        {
            for (int player = 0; player < 6; player++)
            {
                session.RollFromOpla(players[player].Id);
                var state = category == ScoreCategory.Chance && expected.Contains(player)
                    ? session.Score(category)! : session.Strike(category)!;
                if (!state.GameCompleted) Assert.Empty(state.WinnerIndices);
            }
        }

        var final = session.GetState()!;
        Assert.True(final.GameCompleted);
        Assert.Equal(expected, final.WinnerIndices);
        Assert.All(final.Players, player => Assert.Equal("Alex", player.Name));
        for (int i = 0; i < 6; i++) Assert.Equal(expected.Contains(i) ? 10 : 0, final.Players[i].Scorecard.Total);
        Assert.Equal(90, final.TurnNumber);
        Assert.Equal(450, random.CallCount);
    }

    [Fact]
    public void Bonus_DecidesWinnerEvenWhenOpponentHasMoreFieldPoints()
    {
        // Tre ens giver 3+6+9+12+15+18=63 i øvre del. Modspilleren får kun chance og yatzy.
        var dice = GameStateAssertions.Categories.SelectMany(entry =>
        {
            int value = (int)entry.Category;
            int[] first = value <= 6 ? [value, value, value, value == 1 ? 2 : 1, value == 1 ? 2 : 1] : [1, 1, 1, 1, 1];
            return first.Concat(Enumerable.Repeat(6, 5));
        });
        var random = new SequenceRandom(dice);
        var session = new GameSession(random);
        session.NewLobbyGame(Players(2));
        foreach (var (_, category) in GameStateAssertions.Categories)
        {
            session.Roll();
            if ((int)category <= 6) session.Score(category); else session.Strike(category);
            session.Roll();
            if (category is ScoreCategory.Chance or ScoreCategory.Yatzy) session.Score(category); else session.Strike(category);
        }

        var final = session.GetState()!;
        Assert.True(final.GameCompleted);
        Assert.Equal(63, final.Players[0].Scorecard.UpperSum);
        Assert.Equal(50, final.Players[0].Scorecard.Bonus);
        Assert.Equal(113, final.Players[0].Scorecard.Total);
        Assert.Equal(0, final.Players[1].Scorecard.Bonus);
        Assert.Equal(80, final.Players[1].Scorecard.Total);
        Assert.Equal(new[] { 0 }, final.WinnerIndices);
        Assert.Equal(150, random.CallCount);
        GameStateAssertions.Equal(final, session.GetState()!);
        Assert.Equal(113, session.GetState()!.Players[0].Scorecard.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedGame_RejectsAllActionsAndPreservesRandomCallsAndIdentity(bool lobby)
    {
        var random = Ones(75);
        var session = new GameSession(random);
        var players = Players(1);
        var start = lobby ? session.NewLobbyGame(players) : session.NewGame("Alex");
        PlayUntilLastField(session);
        session.Roll();
        session.ToggleHold(0);
        var final = session.Strike(ScoreCategory.Yatzy)!;
        Assert.True(final.GameCompleted);
        Assert.Equal(start.GameId, final.GameId);
        Assert.Equal(new[] { 0 }, final.WinnerIndices);
        Guid? id = lobby ? players[0].Id : null;
        Action[] actions =
        [
            () => session.Roll(), () => session.ToggleHold(0),
            () => session.Score(ScoreCategory.Ones), () => session.Strike(ScoreCategory.Yatzy),
            () => session.RollFromOpla(id), () => session.ToggleHoldFromOpla(0, id)
        ];
        foreach (var action in actions)
        {
            Assert.Equal(Completed, Assert.Throws<InvalidOperationException>(action).Message);
            GameStateAssertions.Equal(final, session.GetState()!);
            Assert.Equal(75, random.CallCount);
        }
        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);
        Assert.Empty(handler.HandleLine("ROLL", id));
        Assert.Empty(handler.HandleLine("HOLD 1", id));
        if (lobby)
        {
            Assert.Equal("Det er ikke denne spillers tur.", Assert.Throws<InvalidOperationException>(() => session.RollFromOpla(Guid.NewGuid())).Message);
            Assert.Throws<InvalidOperationException>(() => session.ToggleHoldFromOpla(0, null));
        }
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => handler.HandleLine("ROLL", id, stopped.Token));
        GameStateAssertions.Equal(final, session.GetState()!);
        Assert.Equal(75, random.CallCount);
    }

    [Fact]
    public void FinalSnapshots_AreIndependentAndNewSinglePlayerGameClearsResult()
    {
        var random = Ones(75);
        var session = new GameSession(random);
        var start = session.NewGame("Alex");
        PlayUntilLastField(session);
        session.Roll();
        var final = session.Score(ScoreCategory.Yatzy)!;
        var copy = session.GetState()!;
        Assert.NotSame(final.WinnerIndices, copy.WinnerIndices);
        final.WinnerIndices[0] = 99;
        final.Players[0].Scorecard.Scores["yatzy"] = 0;
        GameStateAssertions.Equal(copy, session.GetState()!);
        var replacement = session.NewGame("Næste");
        AssertStart(replacement, 1);
        Assert.NotEqual(start.GameId, replacement.GameId);
        Assert.Equal(new[] { 0 }, copy.WinnerIndices);
        Assert.Equal(50, copy.Players[0].Scorecard.Total);
        Assert.Empty(start.WinnerIndices);
        Assert.False(start.GameCompleted);
        Assert.Equal(75, random.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedAndCancelledCreation_PreservesCompletedResult(bool lobby)
    {
        var random = Ones(75);
        var session = new GameSession(random);
        if (lobby) session.NewLobbyGame(Players(1)); else session.NewGame("Alex");
        PlayUntilLastField(session);
        session.Roll();
        var final = session.Score(ScoreCategory.Yatzy)!;
        if (lobby)
        {
            Assert.Throws<InvalidOperationException>(() => session.NewGame("Nyt spil"));
            Assert.Throws<InvalidOperationException>(() => session.NewLobbyGame(Players(1)));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => session.NewLobbyGame(Players(0)));
            Assert.Throws<ArgumentException>(() => session.NewLobbyGame(Players(7)));
            Assert.Throws<ArgumentException>(() => session.NewLobbyGame([new(Guid.Empty, "Alex")]));
            Guid duplicate = Guid.NewGuid();
            Assert.Throws<ArgumentException>(() => session.NewLobbyGame([new(duplicate, "Alex"), new(duplicate, "Alex")]));
        }
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.NewLobbyGame(Players(2), stopped.Token));
        GameStateAssertions.Equal(final, session.GetState()!);
        Assert.Equal(75, random.CallCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task ConcurrentLastFieldChoices_OnlyOneIsAccepted(int count)
    {
        var random = Ones(75 * count);
        var session = new GameSession(random);
        session.NewLobbyGame(Players(count));
        PlayUntilLastField(session);
        session.Roll();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            await gate.Task;
            try { return (State: index == 0 ? session.Score(ScoreCategory.Yatzy) : session.Strike(ScoreCategory.Yatzy), Error: (string?)null); }
            catch (InvalidOperationException error) { return (State: (GameStateResponse?)null, Error: error.Message); }
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        var accepted = Assert.Single(results, result => result.State is not null).State!;
        Assert.Equal(Completed, Assert.Single(results, result => result.State is null).Error);
        Assert.True(accepted.GameCompleted);
        Assert.Equal(15 * count, accepted.TurnNumber);
        Assert.Equal(accepted.Players[count - 1].Scorecard.Scores["yatzy"] == 50 ? [count - 1] : Enumerable.Range(0, count).ToArray(), accepted.WinnerIndices);
        GameStateAssertions.Equal(accepted, session.GetState()!);
        Assert.Equal(75 * count, random.CallCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(6, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(6, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    public async Task RollAndLastField_RespectBothLegalOrdersAndFinalScoringDice(int count, int order)
    {
        var random = new SequenceRandom(Enumerable.Repeat(1, 75 * count).Concat(Enumerable.Repeat(6, 4)));
        var session = new GameSession(random);
        session.NewLobbyGame(Players(count));
        PlayUntilLastField(session, ScoreCategory.Chance);
        session.Roll();
        var before = session.ToggleHold(0)!;
        GameStateResponse? rolled = null;
        GameStateResponse final;
        string? error = null;
        void Roll()
        {
            try { rolled = session.Roll(); }
            catch (InvalidOperationException exception) { error = exception.Message; }
        }
        if (order == 0)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rollTask = Task.Run(async () => { await gate.Task; Roll(); });
            var scoreTask = Task.Run(async () => { await gate.Task; return session.Score(ScoreCategory.Chance)!; });
            gate.SetResult();
            await Task.WhenAll(rollTask, scoreTask).WaitAsync(TimeSpan.FromSeconds(10));
            final = await scoreTask;
        }
        else if (order == 1) { Roll(); final = session.Score(ScoreCategory.Chance)!; }
        else { final = session.Score(ScoreCategory.Chance)!; Roll(); }

        Assert.True(final.GameCompleted);
        Assert.Equal(15 * count, final.TurnNumber);
        Assert.Equal(count - 1, final.CurrentPlayer);
        Assert.Equal(before.Held, final.Held);
        var scoringTurn = rolled ?? before;
        Assert.Equal(scoringTurn.Dice.Sum(), final.Players[count - 1].Scorecard.Scores["chance"]);
        Assert.Equal(scoringTurn.Dice, final.Dice);
        Assert.Equal(scoringTurn.RollsUsed, final.RollsUsed);
        Assert.Equal(new[] { count - 1 }, final.WinnerIndices);
        Assert.Equal(75 * count + (rolled is null ? 0 : 4), random.CallCount);
        if (rolled is null) Assert.Equal(Completed, error); else Assert.Null(error);
        if (order == 1) Assert.NotNull(rolled);
        if (order == 2) Assert.Null(rolled);
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, before.Dice);
        GameStateAssertions.Equal(final, session.GetState()!);
    }

    // Lad kun sidste deltagers sidste felt være åbent; alle øvrige ture gennemføres gyldigt.
    private static void PlayUntilLastField(GameSession session, ScoreCategory last = ScoreCategory.Yatzy)
    {
        int count = session.GetState()!.Players.Length;
        var categories = GameStateAssertions.Categories.Select(entry => entry.Category).Where(category => category != last).Append(last).ToArray();
        for (int turn = 0; turn < 15 * count - 1; turn++)
        {
            session.Roll();
            session.Strike(categories[turn / count]);
        }
    }

    private static void AssertStart(GameStateResponse state, int count)
    {
        Assert.NotEqual(Guid.Empty, state.GameId);
        Assert.Equal(1, state.TurnNumber);
        Assert.False(state.GameCompleted);
        Assert.Empty(state.WinnerIndices);
        Assert.False(state.TurnCompleted);
        Assert.Equal(0, state.CurrentPlayer);
        Assert.Equal(count, state.Players.Length);
        Assert.Equal(new int[5], state.Dice);
        Assert.Equal(new bool[5], state.Held);
        Assert.Equal(0, state.RollsUsed);
        Assert.Empty(state.PossibleScores);
        Assert.All(state.Players, player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
    }

    private static LobbyGamePlayer[] Players(int count) => Enumerable.Range(0, count).Select(_ => new LobbyGamePlayer(Guid.NewGuid(), "Alex")).ToArray();
    private static SequenceRandom Ones(int values) => new(Enumerable.Repeat(1, values));

    private sealed class SequenceRandom(IEnumerable<int> values) : Random
    {
        private readonly Queue<int> _values = new(values);
        public int CallCount { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            Assert.Equal(1, minValue);
            Assert.Equal(7, maxValue);
            Assert.NotEmpty(_values);
            CallCount++;
            return _values.Dequeue();
        }
    }
}
