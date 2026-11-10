using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerer kast, hold/slip, pointtavle, feltvalg og turafslutning med faste kast.
/// Sikrer også, at læsning og afvisninger bevarer spillets data og tilfældighedskilde.
/// </summary>
public class GameTests
{
    [Fact]
    public void Game_StartsWithFiveUnrolledDice()
    {
        var game = new Game();

        Assert.Equal(5, game.Dice.Count);
        Assert.All(game.Dice, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Game_StartsWithNoHeldDice()
    {
        var game = new Game();

        Assert.Equal(5, game.Held.Count);
        Assert.All(game.Held, value => Assert.False(value));
    }

    [Fact]
    public void Game_StartsWithZeroRollsUsed()
    {
        var game = new Game();

        Assert.Equal(0, game.RollsUsed);
    }

    [Fact]
    public void Game_DiceCannotBeChangedExternally()
    {
        var game = new Game();
        var dice = Assert.IsAssignableFrom<IList<int>>(game.Dice);

        Assert.Throws<NotSupportedException>(() => { dice[0] = 6; });
        Assert.Equal(0, game.Dice[0]);
    }

    [Fact]
    public void Game_HeldCannotBeChangedExternally()
    {
        var game = new Game();
        var held = Assert.IsAssignableFrom<IList<bool>>(game.Held);

        Assert.Throws<NotSupportedException>(() => { held[0] = true; });
        Assert.False(game.Held[0]);
    }

    [Fact]
    public void Roll_AssignsValuesBetweenOneAndSix()
    {
        var game = new Game();

        game.Roll();

        Assert.Equal(5, game.Dice.Count);
        Assert.All(game.Dice, value => Assert.InRange(value, 1, 6));
        Assert.All(game.Held, value => Assert.False(value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Roll_CountsEachAcceptedRoll(int rollCount)
    {
        var game = new Game();

        for (int i = 0; i < rollCount; i++)
        {
            game.Roll();
        }

        Assert.Equal(rollCount, game.RollsUsed);
    }

    [Fact]
    public void Roll_UpdatesExistingDiceViewWithExpectedValues()
    {
        var random = new SequenceRandom(1, 2, 3, 4, 5, 6, 5, 4, 3, 2);
        var game = new Game(random);
        var diceView = game.Dice;

        game.Roll();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, diceView);

        game.Roll();

        Assert.Equal(new[] { 6, 5, 4, 3, 2 }, diceView);
        Assert.Equal(diceView, game.Dice);
    }

    [Fact]
    public void Roll_AfterThreeRollsRejectsWithoutChangingState()
    {
        var random = new SequenceRandom(
            1, 2, 3, 4, 5,
            6, 5, 4, 3, 2,
            2, 3, 4, 5, 6,
            6, 6, 6, 6, 6);
        var game = new Game(random);
        game.Roll();
        game.ToggleHold(1);
        game.Roll();
        game.Roll();

        Assert.True(game.Held[1]);
        var expectedDice = game.Dice.ToArray();
        var expectedHeld = game.Held.ToArray();
        int expectedRollsUsed = game.RollsUsed;
        int expectedRandomCalls = random.CallCount;

        Assert.Throws<InvalidOperationException>(() => game.Roll());

        Assert.Equal(expectedDice, game.Dice);
        Assert.Equal(expectedHeld, game.Held);
        Assert.Equal(expectedRollsUsed, game.RollsUsed);
        Assert.Equal(expectedRandomCalls, random.CallCount);
    }

    [Fact]
    public void Game_ActionsDoNotAffectAnotherGame()
    {
        var firstGame = new Game(new SequenceRandom(1, 2, 3, 4, 5));
        var secondGame = new Game();

        firstGame.Roll();
        firstGame.ToggleHold(2);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, firstGame.Dice);
        Assert.True(firstGame.Held[2]);
        Assert.All(secondGame.Dice, value => Assert.Equal(0, value));
        Assert.All(secondGame.Held, value => Assert.False(value));
        Assert.Equal(0, secondGame.RollsUsed);
    }

    [Fact]
    public void Game_NullRandomThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new Game(null!));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    public void ToggleHold_TogglesOnlySelectedDieWithoutRolling(int index, int rollCount)
    {
        var random = new SequenceRandom(1, 2, 3, 4, 5, 6, 5, 4, 3, 2);
        var game = new Game(random);
        for (int i = 0; i < rollCount; i++)
        {
            game.Roll();
        }

        var heldView = game.Held;
        var expectedDice = game.Dice.ToArray();
        var expectedHeld = new bool[5];
        expectedHeld[index] = true;
        int expectedRandomCalls = random.CallCount;

        game.ToggleHold(index);

        Assert.Equal(expectedHeld, game.Held);
        Assert.Equal(expectedHeld, heldView);
        Assert.Equal(expectedDice, game.Dice);
        Assert.Equal(rollCount, game.RollsUsed);
        Assert.Equal(expectedRandomCalls, random.CallCount);

        game.ToggleHold(index);

        Assert.All(game.Held, value => Assert.False(value));
        Assert.All(heldView, value => Assert.False(value));
        Assert.Equal(expectedDice, game.Dice);
        Assert.Equal(rollCount, game.RollsUsed);
        Assert.Equal(expectedRandomCalls, random.CallCount);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(5, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(5, 0)]
    [InlineData(5, 3)]
    public void ToggleHold_InvalidIndexRejectsWithoutChangingState(int index, int rollCount)
    {
        var random = new SequenceRandom(
            1, 2, 3, 4, 5,
            6, 5, 4, 3, 2,
            2, 3, 4, 5, 6);
        var game = new Game(random);
        if (rollCount > 0)
        {
            game.Roll();
            game.ToggleHold(2);
            for (int i = 1; i < rollCount; i++)
            {
                game.Roll();
            }
        }

        var expectedDice = game.Dice.ToArray();
        var expectedHeld = game.Held.ToArray();
        int expectedRandomCalls = random.CallCount;

        // Indeksfejlen skal også have forrang før første og efter tredje kast.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => game.ToggleHold(index));

        Assert.Equal("index", exception.ParamName);
        Assert.Equal(expectedDice, game.Dice);
        Assert.Equal(expectedHeld, game.Held);
        Assert.Equal(rollCount, game.RollsUsed);
        Assert.Equal(expectedRandomCalls, random.CallCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(4, 3)]
    public void ToggleHold_OutsideAllowedRollsRejectsWithoutChangingState(int index, int rollCount)
    {
        var random = new SequenceRandom(
            1, 2, 3, 4, 5,
            6, 5, 4, 3, 2,
            2, 3, 4, 5, 6);
        var game = new Game(random);
        if (rollCount > 0)
        {
            game.Roll();
            game.ToggleHold(1);
            for (int i = 1; i < rollCount; i++)
            {
                game.Roll();
            }
        }

        var expectedDice = game.Dice.ToArray();
        var expectedHeld = game.Held.ToArray();
        int expectedRandomCalls = random.CallCount;

        Assert.Throws<InvalidOperationException>(() => game.ToggleHold(index));

        Assert.Equal(expectedDice, game.Dice);
        Assert.Equal(expectedHeld, game.Held);
        Assert.Equal(rollCount, game.RollsUsed);
        Assert.Equal(expectedRandomCalls, random.CallCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Roll_WithAllDiceHeldUsesRollsWithoutNewRandomValues(int rollCount)
    {
        var random = new SequenceRandom(1, 2, 3, 4, 5);
        var game = new Game(random);
        game.Roll();
        for (int i = 0; i < 5; i++)
        {
            game.ToggleHold(i);
        }

        for (int i = 1; i < rollCount; i++)
        {
            game.Roll();
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, game.Dice);
        Assert.All(game.Held, value => Assert.True(value));
        Assert.Equal(rollCount, game.RollsUsed);
        Assert.Equal(5, random.CallCount);
    }

    [Fact]
    public void Game_ThreeRollTurnPreservesHeldDiceAndRerollsReleasedDice()
    {
        var random = new SequenceRandom(
            1, 2, 3, 4, 5,
            6, 1, 2,
            3, 4, 5, 6);
        var game = new Game(random);

        game.Roll();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, game.Dice);
        Assert.All(game.Held, value => Assert.False(value));
        Assert.Equal(1, game.RollsUsed);
        Assert.Equal(5, random.CallCount);

        // Hold terning 2 og 4; indekserne i kernen starter ved 0.
        game.ToggleHold(1);
        game.ToggleHold(3);

        Assert.Equal(new[] { false, true, false, true, false }, game.Held);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, game.Dice);
        Assert.Equal(1, game.RollsUsed);
        Assert.Equal(5, random.CallCount);

        game.Roll();

        Assert.Equal(new[] { 6, 2, 1, 4, 2 }, game.Dice);
        Assert.Equal(new[] { false, true, false, true, false }, game.Held);
        Assert.Equal(2, game.RollsUsed);
        Assert.Equal(8, random.CallCount);

        game.ToggleHold(1);

        Assert.Equal(new[] { false, false, false, true, false }, game.Held);
        Assert.Equal(new[] { 6, 2, 1, 4, 2 }, game.Dice);
        Assert.Equal(2, game.RollsUsed);
        Assert.Equal(8, random.CallCount);

        game.Roll();

        Assert.Equal(new[] { 3, 4, 5, 4, 6 }, game.Dice);
        Assert.Equal(new[] { false, false, false, true, false }, game.Held);
        Assert.Equal(3, game.RollsUsed);
        Assert.Equal(12, random.CallCount);
    }

    [Fact]
    public void Game_StartsWithEmptyScorecardAndOpenTurn()
    {
        var random = new SequenceRandom();
        var game = new Game(random);

        Assert.Equal(15, game.Scores.Count);
        Assert.Equal(Enum.GetValues<ScoreCategory>(), game.Scores.Keys.OrderBy(category => category));
        Assert.All(game.Scores.Values, score => Assert.Null(score));
        Assert.Equal(0, game.UpperSum);
        Assert.Equal(0, game.Bonus);
        Assert.Equal(0, game.Total);
        Assert.False(game.TurnCompleted);
        Assert.False(game.IsScorecardComplete);
        Assert.Empty(game.PossibleScores);
        Assert.Equal(0, random.CallCount);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void FieldChoice_AfterAllowedRollsStoresResultAndPreservesDiceState(int rollCount, bool strike)
    {
        var game = CreateRolledGame(rollCount, out var random);
        var expected = CaptureState(game, random);
        var scoresView = game.Scores;

        if (strike)
        {
            game.Strike(ScoreCategory.Fives);
        }
        else
        {
            game.Score(ScoreCategory.Fives);
        }

        int expectedScore = strike ? 0 : 15;
        Assert.Equal(expectedScore, scoresView[ScoreCategory.Fives]);
        Assert.All(game.Scores.Where(entry => entry.Key != ScoreCategory.Fives), entry => Assert.Null(entry.Value));
        Assert.Equal(expectedScore, game.UpperSum);
        Assert.Equal(0, game.Bonus);
        Assert.Equal(expectedScore, game.Total);
        Assert.True(game.TurnCompleted);
        Assert.Empty(game.PossibleScores);
        AssertDiceStateUnchanged(game, random, expected);
    }

    [Theory]
    [InlineData(ScoreCategory.Ones, 0)]
    [InlineData(ScoreCategory.Twos, 4)]
    [InlineData(ScoreCategory.Threes, 0)]
    [InlineData(ScoreCategory.Fours, 0)]
    [InlineData(ScoreCategory.Fives, 15)]
    [InlineData(ScoreCategory.Sixes, 0)]
    [InlineData(ScoreCategory.OnePair, 10)]
    [InlineData(ScoreCategory.TwoPairs, 14)]
    [InlineData(ScoreCategory.ThreeOfAKind, 15)]
    [InlineData(ScoreCategory.FourOfAKind, 0)]
    [InlineData(ScoreCategory.SmallStraight, 0)]
    [InlineData(ScoreCategory.LargeStraight, 0)]
    [InlineData(ScoreCategory.FullHouse, 19)]
    [InlineData(ScoreCategory.Chance, 19)]
    [InlineData(ScoreCategory.Yatzy, 0)]
    public void Score_ReferenceRollStoresExpectedPointsAndCompletesTurn(ScoreCategory category, int expectedScore)
    {
        var game = CreateRolledGame(1, out var random);
        var expected = CaptureState(game, random);

        game.Score(category);

        Assert.Equal(expectedScore, game.Scores[category]);
        Assert.All(game.Scores.Where(entry => entry.Key != category), entry => Assert.Null(entry.Value));
        Assert.Equal((int)category <= 6 ? expectedScore : 0, game.UpperSum);
        Assert.Equal(0, game.Bonus);
        Assert.Equal(expectedScore, game.Total);
        Assert.True(game.TurnCompleted);
        Assert.Empty(game.PossibleScores);
        AssertDiceStateUnchanged(game, random, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldChoice_BeforeFirstRollRejectsAndLeavesTurnUsable(bool strike)
    {
        var random = new SequenceRandom(5, 5, 5, 2, 2);
        var game = new Game(random);
        var expected = CaptureState(game, random);
        Action chooseField = strike
            ? () => game.Strike(ScoreCategory.Chance)
            : () => game.Score(ScoreCategory.Chance);

        var exception = Assert.Throws<InvalidOperationException>(chooseField);

        Assert.Equal("Kast terningerne, før du vælger et pointfelt.", exception.Message);
        AssertStateUnchanged(game, random, expected);

        game.Roll();
        chooseField();

        Assert.Equal(strike ? 0 : 19, game.Scores[ScoreCategory.Chance]);
        Assert.True(game.TurnCompleted);
        Assert.Equal(5, random.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void FieldChoice_UnknownCategoryAtAnyTurnStagePreservesState(int value)
    {
        for (int stage = 0; stage < 3; stage++)
        {
            var random = new SequenceRandom(5, 5, 5, 2, 2);
            var game = new Game(random);
            if (stage > 0)
            {
                game.Roll();
                game.ToggleHold(2);
            }
            if (stage == 2)
            {
                game.Score(ScoreCategory.Fives);
            }
            var expected = CaptureState(game, random);

            foreach (var action in new Action<ScoreCategory>[] { game.Score, game.Strike })
            {
                var exception = Assert.Throws<ArgumentOutOfRangeException>(() => action((ScoreCategory)value));

                Assert.Equal("category", exception.ParamName);
                Assert.Equal((ScoreCategory)value, exception.ActualValue);
                Assert.StartsWith("Pointfeltet er ukendt.", exception.Message);
                AssertStateUnchanged(game, random, expected);
            }

            if (stage < 2)
            {
                if (stage == 0)
                {
                    game.Roll();
                }
                game.Score(ScoreCategory.Chance);

                Assert.Equal(19, game.Scores[ScoreCategory.Chance]);
                Assert.True(game.TurnCompleted);
            }
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void CompletedTurn_AllValidActionsRejectWithoutChangingState(int rollCount, bool strike)
    {
        var game = CreateRolledGame(rollCount, out var random);
        if (strike)
        {
            game.Strike(ScoreCategory.Fives);
        }
        else
        {
            game.Score(ScoreCategory.Fives);
        }
        var expected = CaptureState(game, random);
        Action[] actions =
        {
            game.Roll,
            () => game.ToggleHold(0),
            () => game.ToggleHold(2),
            () => game.ToggleHold(4),
            () => game.Score(ScoreCategory.Fives),
            () => game.Score(ScoreCategory.Chance),
            () => game.Strike(ScoreCategory.Fives),
            () => game.Strike(ScoreCategory.Chance)
        };

        foreach (var action in actions)
        {
            var exception = Assert.Throws<InvalidOperationException>(action);

            Assert.Equal("Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur.", exception.Message);
            AssertStateUnchanged(game, random, expected);
        }
    }

    [Fact]
    public void Score_ZeroPointsStillPreventsAnotherFieldChoice()
    {
        var game = CreateRolledGame(1, out var random);
        game.Score(ScoreCategory.Yatzy);
        var expected = CaptureState(game, random);

        Assert.Equal(0, game.Scores[ScoreCategory.Yatzy]);
        Assert.True(game.TurnCompleted);
        var exception = Assert.Throws<InvalidOperationException>(() => game.Score(ScoreCategory.Chance));

        Assert.Equal("Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur.", exception.Message);
        AssertStateUnchanged(game, random, expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ToggleHold_InvalidIndexAfterCompletionKeepsInputError(int index)
    {
        var game = CreateRolledGame(3, out var random);
        game.Score(ScoreCategory.FullHouse);
        var expected = CaptureState(game, random);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => game.ToggleHold(index));

        Assert.Equal("index", exception.ParamName);
        Assert.Equal(index, exception.ActualValue);
        AssertStateUnchanged(game, random, expected);
    }

    [Fact]
    public void PossibleScores_ReferenceRollIncludesAllFieldsAndDoesNotChangeState()
    {
        var game = CreateRolledGame(1, out var random);
        var expected = CaptureState(game, random);
        var expectedScores = new Dictionary<ScoreCategory, int>
        {
            [ScoreCategory.Ones] = 0,
            [ScoreCategory.Twos] = 4,
            [ScoreCategory.Threes] = 0,
            [ScoreCategory.Fours] = 0,
            [ScoreCategory.Fives] = 15,
            [ScoreCategory.Sixes] = 0,
            [ScoreCategory.OnePair] = 10,
            [ScoreCategory.TwoPairs] = 14,
            [ScoreCategory.ThreeOfAKind] = 15,
            [ScoreCategory.FourOfAKind] = 0,
            [ScoreCategory.SmallStraight] = 0,
            [ScoreCategory.LargeStraight] = 0,
            [ScoreCategory.FullHouse] = 19,
            [ScoreCategory.Chance] = 19,
            [ScoreCategory.Yatzy] = 0
        };

        for (int read = 0; read < 3; read++)
        {
            Assert.Equal(expectedScores.OrderBy(entry => entry.Key), game.PossibleScores.OrderBy(entry => entry.Key));
            AssertStateUnchanged(game, random, expected);
        }

        game.ToggleHold(2);

        Assert.Equal(expectedScores.OrderBy(entry => entry.Key), game.PossibleScores.OrderBy(entry => entry.Key));
        Assert.Equal(5, random.CallCount);
    }

    [Fact]
    public void PossibleScores_NewRollsAndCompletionPreserveEarlierSnapshots()
    {
        var random = new SequenceRandom(
            5, 5, 5, 2, 2,
            1, 2, 3, 4, 5,
            2, 3, 4, 5, 6);
        var game = new Game(random);
        var beforeRoll = game.PossibleScores;
        game.Roll();
        var firstRoll = game.PossibleScores;
        var expectedFirst = firstRoll.ToDictionary(entry => entry.Key, entry => entry.Value);
        game.Roll();
        var secondRoll = game.PossibleScores;
        var expectedSecond = secondRoll.ToDictionary(entry => entry.Key, entry => entry.Value);
        game.Roll();
        var thirdRoll = game.PossibleScores;
        var expectedThird = thirdRoll.ToDictionary(entry => entry.Key, entry => entry.Value);

        Assert.Empty(beforeRoll);
        Assert.Equal(19, firstRoll[ScoreCategory.Chance]);
        Assert.Equal(19, firstRoll[ScoreCategory.FullHouse]);
        Assert.Equal(15, secondRoll[ScoreCategory.SmallStraight]);
        Assert.Equal(0, secondRoll[ScoreCategory.LargeStraight]);
        Assert.Equal(15, secondRoll[ScoreCategory.Chance]);
        Assert.Equal(0, thirdRoll[ScoreCategory.SmallStraight]);
        Assert.Equal(20, thirdRoll[ScoreCategory.LargeStraight]);
        Assert.Equal(20, thirdRoll[ScoreCategory.Chance]);
        Assert.All(game.Scores.Values, score => Assert.Null(score));
        Assert.False(game.TurnCompleted);
        Assert.Equal(15, random.CallCount);

        game.Score(ScoreCategory.Chance);

        Assert.Empty(game.PossibleScores);
        Assert.Equal(20, game.Total);
        Assert.Equal(expectedFirst.OrderBy(entry => entry.Key), firstRoll.OrderBy(entry => entry.Key));
        Assert.Equal(expectedSecond.OrderBy(entry => entry.Key), secondRoll.OrderBy(entry => entry.Key));
        Assert.Equal(expectedThird.OrderBy(entry => entry.Key), thirdRoll.OrderBy(entry => entry.Key));
        Assert.Equal(15, random.CallCount);
    }

    [Fact]
    public void Scores_LiveViewRejectsExternalChanges()
    {
        var game = CreateRolledGame(1, out var random);
        var scoresView = game.Scores;
        var mutable = Assert.IsAssignableFrom<IDictionary<ScoreCategory, int?>>(scoresView);
        game.Score(ScoreCategory.Fives);
        var expected = CaptureState(game, random);

        Assert.Equal(15, scoresView[ScoreCategory.Fives]);
        Assert.Throws<NotSupportedException>(() => mutable[ScoreCategory.Fives] = 30);
        Assert.Throws<NotSupportedException>(() => mutable.Add(ScoreCategory.Chance, 30));
        Assert.Throws<NotSupportedException>(() => mutable.Remove(ScoreCategory.Fives));
        Assert.Throws<NotSupportedException>(() => mutable.Clear());
        AssertStateUnchanged(game, random, expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PossibleScores_RejectsExternalChangesAtEveryTurnStage(int stage)
    {
        var random = new SequenceRandom(5, 5, 5, 2, 2);
        var game = new Game(random);
        if (stage > 0)
        {
            game.Roll();
        }
        if (stage == 2)
        {
            game.Strike(ScoreCategory.Fives);
        }
        var expected = CaptureState(game, random);
        var mutable = Assert.IsAssignableFrom<IDictionary<ScoreCategory, int>>(game.PossibleScores);

        Assert.Throws<NotSupportedException>(() => mutable[ScoreCategory.Fives] = 30);
        Assert.Throws<NotSupportedException>(() => mutable.Add(ScoreCategory.Chance, 30));
        Assert.Throws<NotSupportedException>(() => mutable.Remove(ScoreCategory.Fives));
        Assert.Throws<NotSupportedException>(() => mutable.Clear());
        AssertStateUnchanged(game, random, expected);
    }

    [Fact]
    public void FieldChoice_SeparateGamesKeepIndependentScorecards()
    {
        var firstGame = CreateRolledGame(1, out var firstRandom);
        var secondGame = CreateRolledGame(1, out _);
        firstGame.Score(ScoreCategory.Fives);
        var expectedFirst = CaptureState(firstGame, firstRandom);

        secondGame.Strike(ScoreCategory.Chance);

        AssertStateUnchanged(firstGame, firstRandom, expectedFirst);
        Assert.Equal(15, firstGame.Scores[ScoreCategory.Fives]);
        Assert.Null(firstGame.Scores[ScoreCategory.Chance]);
        Assert.Null(secondGame.Scores[ScoreCategory.Fives]);
        Assert.Equal(0, secondGame.Scores[ScoreCategory.Chance]);
        Assert.Equal(0, secondGame.Total);
        Assert.True(secondGame.TurnCompleted);

        var freshGame = new Game();

        Assert.All(freshGame.Scores.Values, score => Assert.Null(score));
        Assert.False(freshGame.TurnCompleted);
        Assert.Equal(0, freshGame.Total);
        Assert.Empty(freshGame.PossibleScores);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void StartNextTurn_AfterFieldChoiceResetsDiceButPreservesPointsAndViews(int rollCount, bool strike)
    {
        var game = CreateRolledGame(rollCount, out var random);
        var diceView = game.Dice;
        var heldView = game.Held;
        var scoresView = game.Scores;
        var possibleScores = game.PossibleScores;
        if (strike) game.Strike(ScoreCategory.Fives);
        else game.Score(ScoreCategory.Fives);
        var completed = CaptureState(game, random);

        game.StartNextTurn();

        Assert.Same(diceView, game.Dice);
        Assert.Same(heldView, game.Held);
        Assert.Same(scoresView, game.Scores);
        Assert.All(diceView, die => Assert.Equal(0, die));
        Assert.All(heldView, held => Assert.False(held));
        Assert.Equal(0, game.RollsUsed);
        Assert.False(game.TurnCompleted);
        Assert.False(game.IsScorecardComplete);
        Assert.Empty(game.PossibleScores);
        Assert.Equal(completed.Scores.OrderBy(entry => entry.Key), game.Scores.OrderBy(entry => entry.Key));
        Assert.Equal((completed.UpperSum, completed.Bonus, completed.Total), (game.UpperSum, game.Bonus, game.Total));
        Assert.Equal(completed.RandomCalls, random.CallCount);
        Assert.Equal(15, possibleScores[ScoreCategory.Fives]);
        Assert.Equal(19, possibleScores[ScoreCategory.Chance]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StartNextTurn_DuringOpenTurnRejectsWithoutChangingState(int rollCount)
    {
        var random = new SequenceRandom(Enumerable.Repeat(2, 15).ToArray());
        var game = new Game(random);
        for (int i = 0; i < rollCount; i++) game.Roll();
        var expected = CaptureState(game, random);

        var exception = Assert.Throws<InvalidOperationException>(game.StartNextTurn);

        Assert.Equal("Den aktuelle tur skal afsluttes, før en ny tur kan starte.", exception.Message);
        AssertStateUnchanged(game, random, expected);
    }

    [Fact]
    public void StartNextTurn_RepeatedBeforeNewFieldChoiceRejectsWithoutChangingState()
    {
        var game = CreateRolledGame(1, out var random);
        game.Strike(ScoreCategory.Chance);
        game.StartNextTurn();
        var expected = CaptureState(game, random);

        var exception = Assert.Throws<InvalidOperationException>(game.StartNextTurn);

        Assert.Equal("Den aktuelle tur skal afsluttes, før en ny tur kan starte.", exception.Message);
        AssertStateUnchanged(game, random, expected);
    }

    [Fact]
    public void NextTurn_RequiresRollPreservesLockedFieldsAndAllowsThreeFreshRolls()
    {
        var random = new SequenceRandom(
            1, 2, 3, 4, 5,
            6, 5, 4, 3, 2,
            1, 2, 3, 4, 5,
            2, 3, 4, 5, 6);
        var game = new Game(random);
        game.Roll();
        for (int i = 0; i < 5; i++) game.ToggleHold(i);
        game.Score(ScoreCategory.Chance);
        game.StartNextTurn();
        var fresh = CaptureState(game, random);

        Action[] beforeRoll =
        {
            () => game.ToggleHold(0),
            () => game.Score(ScoreCategory.Ones),
            () => game.Strike(ScoreCategory.Ones)
        };
        foreach (var action in beforeRoll)
        {
            Assert.Throws<InvalidOperationException>(action);
            AssertStateUnchanged(game, random, fresh);
        }

        // Alle terninger var holdt i den gamle tur. Den nye tur skal alligevel
        // bruge fem nye værdier fra samme, uændrede tilfældighedskilde.
        game.Roll();
        Assert.Equal(new[] { 6, 5, 4, 3, 2 }, game.Dice);
        Assert.Equal(1, game.RollsUsed);
        Assert.Equal(10, random.CallCount);
        Assert.Equal(15, game.Scores[ScoreCategory.Chance]);
        Assert.Equal(14, game.PossibleScores.Count);
        Assert.False(game.PossibleScores.ContainsKey(ScoreCategory.Chance));
        var rolled = CaptureState(game, random);
        foreach (var action in new Action[] { () => game.Score(ScoreCategory.Chance), () => game.Strike(ScoreCategory.Chance) })
        {
            var exception = Assert.Throws<InvalidOperationException>(action);
            Assert.Equal("Pointfeltet er allerede udfyldt.", exception.Message);
            AssertStateUnchanged(game, random, rolled);
        }

        game.ToggleHold(1);
        Assert.True(game.Held[1]);
        game.ToggleHold(1);
        Assert.False(game.Held[1]);
        game.Roll();
        game.Roll();
        Assert.Equal(3, game.RollsUsed);
        Assert.Equal(20, random.CallCount);
        var lastRoll = CaptureState(game, random);
        Assert.Throws<InvalidOperationException>(game.Roll);
        Assert.Throws<InvalidOperationException>(() => game.ToggleHold(0));
        AssertStateUnchanged(game, random, lastRoll);

        game.Score(ScoreCategory.Ones);
        Assert.Equal(0, game.Scores[ScoreCategory.Ones]);
        Assert.Equal(15, game.Scores[ScoreCategory.Chance]);
        Assert.True(game.TurnCompleted);
    }

    [Fact]
    public void StartNextTurn_PreservesEarnedBonusWithoutAccumulatingItAgain()
    {
        var random = new SequenceRandom(
            5, 5, 5, 5, 5,
            6, 6, 6, 6, 6,
            4, 4, 1, 1, 1);
        var game = new Game(random);
        game.Roll();
        game.Score(ScoreCategory.Fives);
        game.StartNextTurn();
        game.Roll();
        game.Score(ScoreCategory.Sixes);
        game.StartNextTurn();
        game.Roll();
        game.Score(ScoreCategory.Fours);
        var scores = game.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);

        game.StartNextTurn();

        Assert.Equal((63, 50, 113), (game.UpperSum, game.Bonus, game.Total));
        Assert.Equal(scores.OrderBy(entry => entry.Key), game.Scores.OrderBy(entry => entry.Key));
        Assert.False(game.IsScorecardComplete);
        Assert.Equal(15, random.CallCount);
        var expected = CaptureState(game, random);
        for (int read = 0; read < 3; read++) AssertStateUnchanged(game, random, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Game_FifteenTurnsFillScorecardAndRejectFurtherTurnWithoutChangingState(bool strike)
    {
        var random = new SequenceRandom(Enumerable.Range(0, 75).Select(i => i % 6 + 1).ToArray());
        var game = new Game(random);
        var categories = Enum.GetValues<ScoreCategory>();
        Assert.False(game.IsScorecardComplete);
        for (int i = 0; i < categories.Length; i++)
        {
            game.Roll();
            if (strike) game.Strike(categories[i]);
            else game.Score(categories[i]);
            Assert.True(game.TurnCompleted);
            Assert.Equal(i + 1, game.Scores.Values.Count(score => score.HasValue));
            Assert.Equal(i == categories.Length - 1, game.IsScorecardComplete);
            if (i < categories.Length - 1)
            {
                game.StartNextTurn();
                Assert.False(game.TurnCompleted);
                Assert.Empty(game.PossibleScores);
            }
        }

        Assert.True(game.IsScorecardComplete);
        Assert.Equal(75, random.CallCount);
        if (strike) Assert.All(game.Scores.Values, score => Assert.Equal(0, score));
        var completed = CaptureState(game, random);
        var exception = Assert.Throws<InvalidOperationException>(game.StartNextTurn);
        Assert.Equal("Pointtavlen er fuld. Der kan ikke startes en ny tur.", exception.Message);
        AssertStateUnchanged(game, random, completed);
        Action[] afterCompletion =
        {
            game.Roll,
            () => game.ToggleHold(0),
            () => game.Score(ScoreCategory.Chance),
            () => game.Strike(ScoreCategory.Chance)
        };
        foreach (var action in afterCompletion)
        {
            Assert.Throws<InvalidOperationException>(action);
            AssertStateUnchanged(game, random, completed);
        }
    }

    [Fact]
    public void NextTurns_PreserveEarlierPossibleScoresAndDoNotAffectAnotherGame()
    {
        var random = new SequenceRandom(1, 2, 3, 4, 5, 6, 6, 6, 6, 6);
        var game = new Game(random);
        var other = CreateRolledGame(1, out var otherRandom);
        other.Strike(ScoreCategory.Ones);
        var expectedOther = CaptureState(other, otherRandom);
        var scoresView = game.Scores;
        game.Roll();
        var firstPossible = game.PossibleScores;
        game.Score(ScoreCategory.Chance);
        game.StartNextTurn();
        game.Roll();
        var secondPossible = game.PossibleScores;
        game.Score(ScoreCategory.Yatzy);
        game.StartNextTurn();

        Assert.Same(scoresView, game.Scores);
        Assert.Equal(15, scoresView[ScoreCategory.Chance]);
        Assert.Equal(50, scoresView[ScoreCategory.Yatzy]);
        Assert.Equal(65, game.Total);
        Assert.Equal(15, firstPossible[ScoreCategory.Chance]);
        Assert.Equal(0, firstPossible[ScoreCategory.Yatzy]);
        Assert.False(secondPossible.ContainsKey(ScoreCategory.Chance));
        Assert.Equal(50, secondPossible[ScoreCategory.Yatzy]);
        Assert.Empty(game.PossibleScores);
        Assert.Equal(10, random.CallCount);
        AssertStateUnchanged(other, otherRandom, expectedOther);
    }

    /// <summary>
    /// Opretter referencekastet 5,5,5,2,2 med en holdt terning efter 1–3 kast.
    /// Faste værdier for de øvrige terninger gør turens slutresultat ens i alle tre tilfælde.
    /// </summary>
    private static Game CreateRolledGame(int rollCount, out SequenceRandom random)
    {
        random = new SequenceRandom(5, 5, 5, 2, 2, 5, 5, 2, 2, 5, 5, 2, 2);
        var game = new Game(random);
        game.Roll();
        game.ToggleHold(2);
        for (int roll = 1; roll < rollCount; roll++)
        {
            game.Roll();
        }

        return game;
    }

    // Kopier alle observerbare værdier, så tests også opdager utilsigtede sideeffekter
    // i andre dele af spillet end den handling, der forventes at blive afvist.
    private static GameSnapshot CaptureState(Game game, SequenceRandom random) => new(
        game.Dice.ToArray(),
        game.Held.ToArray(),
        game.RollsUsed,
        game.Scores.ToDictionary(entry => entry.Key, entry => entry.Value),
        game.UpperSum,
        game.Bonus,
        game.Total,
        game.TurnCompleted,
        game.IsScorecardComplete,
        game.PossibleScores.ToDictionary(entry => entry.Key, entry => entry.Value),
        random.CallCount);

    private static void AssertDiceStateUnchanged(Game game, SequenceRandom random, GameSnapshot expected)
    {
        Assert.Equal(expected.Dice, game.Dice);
        Assert.Equal(expected.Held, game.Held);
        Assert.Equal(expected.RollsUsed, game.RollsUsed);
        Assert.Equal(expected.RandomCalls, random.CallCount);
    }

    private static void AssertStateUnchanged(Game game, SequenceRandom random, GameSnapshot expected)
    {
        AssertDiceStateUnchanged(game, random, expected);
        Assert.Equal(expected.Scores.OrderBy(entry => entry.Key), game.Scores.OrderBy(entry => entry.Key));
        Assert.Equal(expected.UpperSum, game.UpperSum);
        Assert.Equal(expected.Bonus, game.Bonus);
        Assert.Equal(expected.Total, game.Total);
        Assert.Equal(expected.TurnCompleted, game.TurnCompleted);
        Assert.Equal(expected.IsScorecardComplete, game.IsScorecardComplete);
        Assert.Equal(expected.PossibleScores.OrderBy(entry => entry.Key), game.PossibleScores.OrderBy(entry => entry.Key));
        Assert.Equal(expected.RandomCalls, random.CallCount);
    }

    private sealed record GameSnapshot(
        int[] Dice,
        bool[] Held,
        int RollsUsed,
        Dictionary<ScoreCategory, int?> Scores,
        int UpperSum,
        int Bonus,
        int Total,
        bool TurnCompleted,
        bool IsScorecardComplete,
        Dictionary<ScoreCategory, int> PossibleScores,
        int RandomCalls);

    /// <summary>
    /// Leverer forudbestemte værdier, så kast kan testes uden tilfældige testresultater.
    /// </summary>
    private sealed class SequenceRandom : Random
    {
        private readonly Queue<int> _values;

        public int CallCount { get; private set; }

        public SequenceRandom(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        /// <summary>
        /// Registrerer kaldet og returnerer næste værdi, hvis terningeintervallet er korrekt.
        /// </summary>
        public override int Next(int minValue, int maxValue)
        {
            CallCount++;

            // Faste svar alene ville ikke afsløre en forkert øvre grænse i Game.Roll().
            Assert.Equal(1, minValue);
            Assert.Equal(7, maxValue);
            Assert.NotEmpty(_values);

            return _values.Dequeue();
        }
    }
}
