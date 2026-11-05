using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Tester alle 15 pointfelter og inputgrænsen med faste kast og forventninger.
/// Input og rækkefølge bevares, og ugyldige terninger afvises før pointberegningen.
/// </summary>
public class ScoreCalculatorTests
{
    [Theory]
    [InlineData(ScoreCategory.Ones, new int[] { 1, 1, 2, 3, 6 }, 2)]
    [InlineData(ScoreCategory.Twos, new int[] { 1, 1, 2, 3, 6 }, 2)]
    [InlineData(ScoreCategory.Threes, new int[] { 1, 1, 2, 3, 6 }, 3)]
    [InlineData(ScoreCategory.Fours, new int[] { 1, 1, 2, 3, 6 }, 0)]
    [InlineData(ScoreCategory.Fives, new int[] { 1, 1, 2, 3, 6 }, 0)]
    [InlineData(ScoreCategory.Sixes, new int[] { 1, 1, 2, 3, 6 }, 6)]
    [InlineData(ScoreCategory.Fours, new int[] { 4, 1, 4, 6, 4 }, 12)]
    [InlineData(ScoreCategory.Fives, new int[] { 5, 1, 5, 4, 5 }, 15)]
    public void Calculate_MixedDice_ReturnsOnlyMatchingPoints(
        ScoreCategory category, int[] dice, int expectedScore)
    {
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
    }

    [Theory]
    [InlineData(ScoreCategory.Ones, new int[] { 2, 3, 4, 5, 6 })]
    [InlineData(ScoreCategory.Twos, new int[] { 1, 3, 4, 5, 6 })]
    [InlineData(ScoreCategory.Threes, new int[] { 1, 2, 4, 5, 6 })]
    [InlineData(ScoreCategory.Fours, new int[] { 1, 2, 3, 5, 6 })]
    [InlineData(ScoreCategory.Fives, new int[] { 1, 2, 3, 4, 6 })]
    [InlineData(ScoreCategory.Sixes, new int[] { 1, 2, 3, 4, 5 })]
    public void Calculate_NoMatchingDice_ReturnsZero(ScoreCategory category, int[] dice)
    {
        Assert.Equal(0, ScoreCalculator.Calculate(dice, category));
    }

    [Theory]
    [InlineData(ScoreCategory.Ones, 1, 5)]
    [InlineData(ScoreCategory.Twos, 2, 10)]
    [InlineData(ScoreCategory.Threes, 3, 15)]
    [InlineData(ScoreCategory.Fours, 4, 20)]
    [InlineData(ScoreCategory.Fives, 5, 25)]
    [InlineData(ScoreCategory.Sixes, 6, 30)]
    public void Calculate_FiveMatchingDice_ReturnsCategoryMaximum(
        ScoreCategory category, int value, int expectedScore)
    {
        int[] dice = { value, value, value, value, value };

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
    }

    [Theory]
    [InlineData(new int[] { 1, 2, 3, 4, 6 }, 0)]
    [InlineData(new int[] { 5, 1, 2, 3, 4 }, 5)]
    [InlineData(new int[] { 5, 1, 5, 2, 3 }, 10)]
    [InlineData(new int[] { 5, 5, 5, 2, 2 }, 15)]
    [InlineData(new int[] { 5, 5, 5, 5, 2 }, 20)]
    [InlineData(new int[] { 5, 5, 5, 5, 5 }, 25)]
    public void Calculate_FivesWithZeroToFiveMatches_ReturnsExpectedScore(int[] dice, int expectedScore)
    {
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, ScoreCategory.Fives));
    }

    [Theory]
    [InlineData(new int[] { 1, 2, 3, 4, 6 }, 0, 0, 0, 0)]
    [InlineData(new int[] { 1, 1, 2, 2, 3 }, 4, 6, 0, 0)]
    [InlineData(new int[] { 5, 5, 4, 4, 2 }, 10, 18, 0, 0)]
    [InlineData(new int[] { 5, 5, 5, 2, 2 }, 10, 14, 15, 0)]
    [InlineData(new int[] { 6, 6, 5, 5, 5 }, 12, 22, 15, 0)]
    [InlineData(new int[] { 4, 4, 4, 4, 3 }, 8, 0, 12, 16)]
    [InlineData(new int[] { 1, 1, 1, 1, 1 }, 2, 0, 3, 4)]
    [InlineData(new int[] { 6, 6, 6, 6, 6 }, 12, 0, 18, 24)]
    [InlineData(new int[] { 1, 1, 2, 3, 4 }, 2, 0, 0, 0)]
    [InlineData(new int[] { 6, 6, 1, 2, 3 }, 12, 0, 0, 0)]
    [InlineData(new int[] { 3, 3, 3, 1, 2 }, 6, 0, 9, 0)]
    [InlineData(new int[] { 6, 6, 6, 1, 2 }, 12, 0, 18, 0)]
    [InlineData(new int[] { 6, 6, 6, 6, 1 }, 12, 0, 18, 24)]
    [InlineData(new int[] { 6, 6, 5, 5, 1 }, 12, 22, 0, 0)]
    public void Calculate_MatchingGroups_ReturnsExpectedScoresAndPreservesInput(
        int[] dice, int expectedPair, int expectedTwoPairs, int expectedThreeOfAKind, int expectedFourOfAKind)
    {
        var expectedDice = dice.ToArray();

        Assert.Equal(expectedPair, ScoreCalculator.Calculate(dice, ScoreCategory.OnePair));
        Assert.Equal(expectedTwoPairs, ScoreCalculator.Calculate(dice, ScoreCategory.TwoPairs));
        Assert.Equal(expectedThreeOfAKind, ScoreCalculator.Calculate(dice, ScoreCategory.ThreeOfAKind));
        Assert.Equal(expectedFourOfAKind, ScoreCalculator.Calculate(dice, ScoreCategory.FourOfAKind));

        Assert.Equal(expectedDice, dice);
    }

    [Theory]
    [InlineData(ScoreCategory.OnePair, new int[] { 6, 5, 5, 5, 6 }, 12)]
    [InlineData(ScoreCategory.TwoPairs, new int[] { 2, 5, 2, 5, 5 }, 14)]
    [InlineData(ScoreCategory.ThreeOfAKind, new int[] { 6, 5, 5, 5, 6 }, 15)]
    [InlineData(ScoreCategory.FourOfAKind, new int[] { 4, 1, 4, 4, 4 }, 16)]
    public void Calculate_MatchingGroupsWithReorderedReadOnlyDice_ReturnsSameScoreAndPreservesInputs(
        ScoreCategory category, int[] dice, int expectedScore)
    {
        var expectedDice = dice.ToArray();
        var reorderedDice = dice.Reverse().ToArray();
        var expectedReorderedDice = reorderedDice.ToArray();

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(Array.AsReadOnly(reorderedDice), category));

        Assert.Equal(expectedDice, dice);
        Assert.Equal(expectedReorderedDice, reorderedDice);
    }

    [Theory]
    [InlineData(new int[] { 5, 1, 4, 2, 3 }, 15, 0, 0, 15, 0)]
    [InlineData(new int[] { 6, 2, 5, 3, 4 }, 0, 20, 0, 20, 0)]
    [InlineData(new int[] { 1, 2, 3, 4, 4 }, 0, 0, 0, 14, 0)]
    [InlineData(new int[] { 5, 5, 5, 2, 2 }, 0, 0, 19, 19, 0)]
    [InlineData(new int[] { 6, 6, 6, 5, 5 }, 0, 0, 28, 28, 0)]
    [InlineData(new int[] { 4, 4, 4, 4, 3 }, 0, 0, 0, 19, 0)]
    [InlineData(new int[] { 1, 1, 1, 1, 1 }, 0, 0, 0, 5, 50)]
    [InlineData(new int[] { 6, 6, 6, 6, 6 }, 0, 0, 0, 30, 50)]
    [InlineData(new int[] { 1, 2, 3, 4, 6 }, 0, 0, 0, 16, 0)]
    [InlineData(new int[] { 2, 3, 4, 5, 5 }, 0, 0, 0, 19, 0)]
    [InlineData(new int[] { 2, 2, 3, 4, 5 }, 0, 0, 0, 16, 0)]
    [InlineData(new int[] { 2, 3, 4, 4, 6 }, 0, 0, 0, 19, 0)]
    [InlineData(new int[] { 1, 1, 2, 2, 3 }, 0, 0, 0, 9, 0)]
    [InlineData(new int[] { 3, 3, 3, 1, 2 }, 0, 0, 0, 12, 0)]
    [InlineData(new int[] { 1, 1, 1, 2, 2 }, 0, 0, 7, 7, 0)]
    [InlineData(new int[] { 2, 2, 2, 1, 1 }, 0, 0, 8, 8, 0)]
    [InlineData(new int[] { 6, 6, 5, 5, 5 }, 0, 0, 27, 27, 0)]
    public void Calculate_FinalCategories_ReturnsExpectedScoresAndPreservesInput(
        int[] dice, int expectedSmallStraight, int expectedLargeStraight,
        int expectedFullHouse, int expectedChance, int expectedYatzy)
    {
        var expectedDice = dice.ToArray();

        Assert.Equal(expectedSmallStraight, ScoreCalculator.Calculate(dice, ScoreCategory.SmallStraight));
        Assert.Equal(expectedLargeStraight, ScoreCalculator.Calculate(dice, ScoreCategory.LargeStraight));
        Assert.Equal(expectedFullHouse, ScoreCalculator.Calculate(dice, ScoreCategory.FullHouse));
        Assert.Equal(expectedChance, ScoreCalculator.Calculate(dice, ScoreCategory.Chance));
        Assert.Equal(expectedYatzy, ScoreCalculator.Calculate(dice, ScoreCategory.Yatzy));

        Assert.Equal(expectedDice, dice);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Calculate_YatzyWithAnyFace_ReturnsFifty(int value)
    {
        int[] dice = { value, value, value, value, value };

        Assert.Equal(50, ScoreCalculator.Calculate(dice, ScoreCategory.Yatzy));
    }

    [Theory]
    [InlineData(ScoreCategory.SmallStraight, new int[] { 5, 1, 4, 2, 3 }, 15)]
    [InlineData(ScoreCategory.LargeStraight, new int[] { 6, 2, 5, 3, 4 }, 20)]
    [InlineData(ScoreCategory.FullHouse, new int[] { 2, 5, 2, 5, 5 }, 19)]
    [InlineData(ScoreCategory.Chance, new int[] { 2, 5, 2, 5, 5 }, 19)]
    [InlineData(ScoreCategory.Yatzy, new int[] { 3, 3, 3, 3, 3 }, 50)]
    public void Calculate_FinalCategoriesWithReorderedReadOnlyDice_ReturnsSameScoreAndPreservesInputs(
        ScoreCategory category, int[] dice, int expectedScore)
    {
        var expectedDice = dice.ToArray();
        var reorderedDice = dice.Reverse().ToArray();
        var expectedReorderedDice = reorderedDice.ToArray();

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(Array.AsReadOnly(reorderedDice), category));

        Assert.Equal(expectedDice, dice);
        Assert.Equal(expectedReorderedDice, reorderedDice);
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
    public void Calculate_ReferenceRoll_MatchesAllFifteenScoresInSpecification(
        ScoreCategory category, int expectedScore)
    {
        int[] dice = { 5, 5, 5, 2, 2 };

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
    }

    [Theory]
    [InlineData(ScoreCategory.Ones, 2)]
    [InlineData(ScoreCategory.Twos, 2)]
    [InlineData(ScoreCategory.Threes, 0)]
    [InlineData(ScoreCategory.Fours, 4)]
    [InlineData(ScoreCategory.Fives, 0)]
    [InlineData(ScoreCategory.Sixes, 6)]
    public void Calculate_ReorderedDice_ReturnsSameScoreAndPreservesBothInputs(
        ScoreCategory category, int expectedScore)
    {
        int[] firstDice = { 6, 1, 4, 1, 2 };
        int[] secondDice = { 1, 2, 1, 6, 4 };
        var expectedFirst = firstDice.ToArray();
        var expectedSecond = secondDice.ToArray();

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(firstDice, category));
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(secondDice, category));

        Assert.Equal(expectedFirst, firstDice);
        Assert.Equal(expectedSecond, secondDice);
    }

    [Theory]
    [InlineData(ScoreCategory.Ones, 2)]
    [InlineData(ScoreCategory.Twos, 2)]
    [InlineData(ScoreCategory.Threes, 3)]
    [InlineData(ScoreCategory.Fours, 0)]
    [InlineData(ScoreCategory.Fives, 0)]
    [InlineData(ScoreCategory.Sixes, 6)]
    public void Calculate_ReadOnlyList_ReturnsExpectedScoreAndPreservesInput(
        ScoreCategory category, int expectedScore)
    {
        int[] dice = { 1, 1, 2, 3, 6 };
        var expectedDice = dice.ToArray();

        int score = ScoreCalculator.Calculate(Array.AsReadOnly(dice), category);

        Assert.Equal(expectedScore, score);
        Assert.Equal(expectedDice, dice);
    }

    [Fact]
    public void Calculate_NullDice_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ScoreCalculator.Calculate(null!, ScoreCategory.Ones));

        Assert.Equal("dice", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Calculate_WrongDiceCount_RejectsWithoutChangingInput(int count)
    {
        var dice = Enumerable.Repeat(1, count).ToArray();
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentException>(
            () => ScoreCalculator.Calculate(dice, ScoreCategory.Ones));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(expectedDice, dice);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(-1, 0)]
    [InlineData(-1, 1)]
    [InlineData(-1, 2)]
    [InlineData(-1, 3)]
    [InlineData(-1, 4)]
    [InlineData(7, 0)]
    [InlineData(7, 1)]
    [InlineData(7, 2)]
    [InlineData(7, 3)]
    [InlineData(7, 4)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MinValue, 2)]
    [InlineData(int.MinValue, 3)]
    [InlineData(int.MinValue, 4)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MaxValue, 3)]
    [InlineData(int.MaxValue, 4)]
    public void Calculate_InvalidDieAtAnyIndex_RejectsWithoutChangingInput(int value, int index)
    {
        // Også værdier, der ikke matcher Fives, skal valideres. Placeringen
        // sidst i listen afslører et eventuelt for tidligt retur fra beregningen.
        int[] dice = { 5, 1, 5, 2, 5 };
        dice[index] = value;
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScoreCalculator.Calculate(dice, ScoreCategory.Fives));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(value, exception.ActualValue);
        Assert.Equal(expectedDice, dice);
    }

    [Fact]
    public void Calculate_UnrolledGameDice_RejectsWithoutChangingGame()
    {
        var game = new Game();
        var expectedDice = game.Dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScoreCalculator.Calculate(game.Dice, ScoreCategory.Ones));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(expectedDice, game.Dice);
        Assert.All(game.Held, value => Assert.False(value));
        Assert.Equal(0, game.RollsUsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Calculate_UndefinedCategory_RejectsWithoutChangingInput(int value)
    {
        int[] dice = { 6, 1, 4, 1, 2 };
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScoreCalculator.Calculate(dice, (ScoreCategory)value));

        Assert.Equal("category", exception.ParamName);
        Assert.Equal((ScoreCategory)value, exception.ActualValue);
        Assert.Equal(expectedDice, dice);
    }

    [Theory]
    [InlineData(ScoreCategory.OnePair)]
    [InlineData(ScoreCategory.TwoPairs)]
    [InlineData(ScoreCategory.ThreeOfAKind)]
    [InlineData(ScoreCategory.FourOfAKind)]
    [InlineData(ScoreCategory.SmallStraight)]
    [InlineData(ScoreCategory.LargeStraight)]
    [InlineData(ScoreCategory.FullHouse)]
    [InlineData(ScoreCategory.Chance)]
    [InlineData(ScoreCategory.Yatzy)]
    public void Calculate_LowerCategoriesWithNullDice_ThrowsArgumentNullException(ScoreCategory category)
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ScoreCalculator.Calculate(null!, category));

        Assert.Equal("dice", exception.ParamName);
    }

    [Theory]
    [InlineData(ScoreCategory.OnePair)]
    [InlineData(ScoreCategory.TwoPairs)]
    [InlineData(ScoreCategory.ThreeOfAKind)]
    [InlineData(ScoreCategory.FourOfAKind)]
    [InlineData(ScoreCategory.SmallStraight)]
    [InlineData(ScoreCategory.LargeStraight)]
    [InlineData(ScoreCategory.FullHouse)]
    [InlineData(ScoreCategory.Chance)]
    [InlineData(ScoreCategory.Yatzy)]
    public void Calculate_LowerCategoriesWithWrongDiceCount_RejectsWithoutChangingInput(ScoreCategory category)
    {
        foreach (int count in new[] { 0, 4, 6 })
        {
            var dice = Enumerable.Repeat(6, count).ToArray();
            var expectedDice = dice.ToArray();

            var exception = Assert.Throws<ArgumentException>(() => ScoreCalculator.Calculate(dice, category));

            Assert.Equal("dice", exception.ParamName);
            Assert.Equal(expectedDice, dice);
        }
    }

    [Theory]
    [InlineData(ScoreCategory.OnePair, 0)]
    [InlineData(ScoreCategory.OnePair, -1)]
    [InlineData(ScoreCategory.OnePair, 7)]
    [InlineData(ScoreCategory.OnePair, int.MinValue)]
    [InlineData(ScoreCategory.OnePair, int.MaxValue)]
    [InlineData(ScoreCategory.TwoPairs, 0)]
    [InlineData(ScoreCategory.TwoPairs, -1)]
    [InlineData(ScoreCategory.TwoPairs, 7)]
    [InlineData(ScoreCategory.TwoPairs, int.MinValue)]
    [InlineData(ScoreCategory.TwoPairs, int.MaxValue)]
    [InlineData(ScoreCategory.ThreeOfAKind, 0)]
    [InlineData(ScoreCategory.ThreeOfAKind, -1)]
    [InlineData(ScoreCategory.ThreeOfAKind, 7)]
    [InlineData(ScoreCategory.ThreeOfAKind, int.MinValue)]
    [InlineData(ScoreCategory.ThreeOfAKind, int.MaxValue)]
    [InlineData(ScoreCategory.FourOfAKind, 0)]
    [InlineData(ScoreCategory.FourOfAKind, -1)]
    [InlineData(ScoreCategory.FourOfAKind, 7)]
    [InlineData(ScoreCategory.FourOfAKind, int.MinValue)]
    [InlineData(ScoreCategory.FourOfAKind, int.MaxValue)]
    [InlineData(ScoreCategory.SmallStraight, 0)]
    [InlineData(ScoreCategory.SmallStraight, -1)]
    [InlineData(ScoreCategory.SmallStraight, 7)]
    [InlineData(ScoreCategory.SmallStraight, int.MinValue)]
    [InlineData(ScoreCategory.SmallStraight, int.MaxValue)]
    [InlineData(ScoreCategory.LargeStraight, 0)]
    [InlineData(ScoreCategory.LargeStraight, -1)]
    [InlineData(ScoreCategory.LargeStraight, 7)]
    [InlineData(ScoreCategory.LargeStraight, int.MinValue)]
    [InlineData(ScoreCategory.LargeStraight, int.MaxValue)]
    [InlineData(ScoreCategory.FullHouse, 0)]
    [InlineData(ScoreCategory.FullHouse, -1)]
    [InlineData(ScoreCategory.FullHouse, 7)]
    [InlineData(ScoreCategory.FullHouse, int.MinValue)]
    [InlineData(ScoreCategory.FullHouse, int.MaxValue)]
    [InlineData(ScoreCategory.Chance, 0)]
    [InlineData(ScoreCategory.Chance, -1)]
    [InlineData(ScoreCategory.Chance, 7)]
    [InlineData(ScoreCategory.Chance, int.MinValue)]
    [InlineData(ScoreCategory.Chance, int.MaxValue)]
    [InlineData(ScoreCategory.Yatzy, 0)]
    [InlineData(ScoreCategory.Yatzy, -1)]
    [InlineData(ScoreCategory.Yatzy, 7)]
    [InlineData(ScoreCategory.Yatzy, int.MinValue)]
    [InlineData(ScoreCategory.Yatzy, int.MaxValue)]
    public void Calculate_LowerCategoriesWithInvalidLastDie_RejectsBeforeScoringAndPreservesInput(
        ScoreCategory category, int value)
    {
        // Også den sidste terning valideres før beregning og opslag, selv om
        // de første værdier ligner en brugbar kombination.
        int[] dice = category switch
        {
            ScoreCategory.TwoPairs => new[] { 6, 6, 5, 5, value },
            ScoreCategory.SmallStraight => new[] { 1, 2, 3, 4, value },
            ScoreCategory.LargeStraight => new[] { 2, 3, 4, 5, value },
            ScoreCategory.FullHouse => new[] { 3, 3, 3, 2, value },
            _ => new[] { 6, 6, 6, 6, value }
        };
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ScoreCalculator.Calculate(dice, category));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(value, exception.ActualValue);
        Assert.Equal(expectedDice, dice);
    }

    [Fact]
    public void Calculate_InvalidDiceWithLowerCategory_ThrowsInputErrorAndPreservesInput()
    {
        int[] dice = { 1, 1, 1, 1, 7 };
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScoreCalculator.Calculate(dice, ScoreCategory.Yatzy));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(expectedDice, dice);
    }

    [Fact]
    public void Calculate_RepeatedCallsWithDifferentInputs_DoNotShareState()
    {
        int[] firstDice = { 1, 1, 1, 2, 3 };
        int[] secondDice = { 6, 6, 6, 6, 6 };

        Assert.Equal(3, ScoreCalculator.Calculate(firstDice, ScoreCategory.Ones));
        Assert.Equal(30, ScoreCalculator.Calculate(secondDice, ScoreCategory.Sixes));
        Assert.Equal(3, ScoreCalculator.Calculate(firstDice, ScoreCategory.Ones));
    }

    [Theory]
    [InlineData(ScoreCategory.OnePair, new int[] { 6, 6, 1, 2, 3 }, 12)]
    [InlineData(ScoreCategory.TwoPairs, new int[] { 6, 6, 5, 5, 1 }, 22)]
    [InlineData(ScoreCategory.ThreeOfAKind, new int[] { 6, 6, 6, 1, 2 }, 18)]
    [InlineData(ScoreCategory.FourOfAKind, new int[] { 6, 6, 6, 6, 1 }, 24)]
    public void Calculate_MatchingGroupsOnRepeatedCalls_DoNotReuseCounts(
        ScoreCategory category, int[] dice, int expectedScore)
    {
        int[] unmatchedDice = { 1, 2, 3, 4, 5 };

        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
        Assert.Equal(0, ScoreCalculator.Calculate(unmatchedDice, category));
        Assert.Equal(expectedScore, ScoreCalculator.Calculate(dice, category));
    }

    [Theory]
    [InlineData(ScoreCategory.SmallStraight, new int[] { 5, 1, 4, 2, 3 }, 15, new int[] { 1, 1, 1, 1, 1 }, 0)]
    [InlineData(ScoreCategory.LargeStraight, new int[] { 6, 2, 5, 3, 4 }, 20, new int[] { 1, 1, 1, 1, 1 }, 0)]
    [InlineData(ScoreCategory.FullHouse, new int[] { 5, 5, 5, 2, 2 }, 19, new int[] { 6, 6, 6, 6, 6 }, 0)]
    [InlineData(ScoreCategory.Chance, new int[] { 6, 6, 6, 6, 6 }, 30, new int[] { 1, 1, 1, 1, 1 }, 5)]
    [InlineData(ScoreCategory.Yatzy, new int[] { 2, 2, 2, 2, 2 }, 50, new int[] { 2, 2, 2, 2, 1 }, 0)]
    public void Calculate_FinalCategoriesOnRepeatedCalls_DoNotReuseCountsOrTotal(
        ScoreCategory category, int[] firstDice, int expectedFirstScore, int[] secondDice, int expectedSecondScore)
    {
        Assert.Equal(expectedFirstScore, ScoreCalculator.Calculate(firstDice, category));
        Assert.Equal(expectedSecondScore, ScoreCalculator.Calculate(secondDice, category));
        Assert.Equal(expectedFirstScore, ScoreCalculator.Calculate(firstDice, category));
    }
}
