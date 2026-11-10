using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Tester tavlens lagring og beskyttelse samt bonus og summer gennem rigtige kast.
/// Turregler og HTTP kontrolleres særskilt i spil- og API-testene.
/// </summary>
public class ScorecardTests
{
    public static IEnumerable<object[]> AllCategories => Enum.GetValues<ScoreCategory>()
        .Select(category => new object[] { category });

    [Fact]
    public void Scorecard_StartsWithFifteenEmptyFieldsAndZeroTotals()
    {
        var card = new Scorecard();

        Assert.Equal(15, card.Scores.Count);
        Assert.Equal(Enum.GetValues<ScoreCategory>(), card.Scores.Keys.OrderBy(category => (int)category));
        Assert.All(card.Scores.Values, score => Assert.Null(score));
        Assert.False(card.IsComplete);
        Assert.Equal((0, 0, 0), (card.UpperSum, card.Bonus, card.Total));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsComplete_OnlyBecomesTrueAfterLastFieldIncludingZeros(bool strike)
    {
        var card = new Scorecard();
        var categories = Enum.GetValues<ScoreCategory>();

        for (int i = 0; i < categories.Length; i++)
        {
            Assert.False(card.IsComplete);
            if (strike)
            {
                card.Strike(categories[i]);
            }
            else
            {
                // Kastet giver både positive point og beregnede nuller på tavlen.
                card.Record(new[] { 5, 5, 5, 2, 2 }, categories[i]);
            }

            var expected = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
            var totals = (card.UpperSum, card.Bonus, card.Total);
            Assert.Equal(i == categories.Length - 1, card.IsComplete);
            AssertCardUnchanged(card, expected, totals);
        }

        Assert.True(card.IsComplete);
        Assert.All(card.Scores.Values, score => Assert.True(score.HasValue));
        if (strike) Assert.All(card.Scores.Values, score => Assert.Equal(0, score));
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
    public void Record_ReferenceRoll_StoresCalculatedPointsOnlyInSelectedField(
        ScoreCategory category, int expectedScore)
    {
        var card = new Scorecard();
        int[] dice = { 5, 5, 5, 2, 2 };
        var expectedDice = dice.ToArray();

        card.Record(dice, category);

        Assert.Equal(expectedScore, card.Scores[category]);
        Assert.All(card.Scores.Where(entry => entry.Key != category), entry => Assert.Null(entry.Value));
        Assert.Equal(expectedScore, card.Total);
        Assert.Equal(0, card.Bonus);
        Assert.Equal(expectedDice, dice);
    }

    [Theory]
    [MemberData(nameof(AllCategories))]
    public void Strike_AnyCategory_StoresZeroOnlyInSelectedField(ScoreCategory category)
    {
        var card = new Scorecard();

        card.Strike(category);

        Assert.Equal(0, card.Scores[category]);
        Assert.All(card.Scores.Where(entry => entry.Key != category), entry => Assert.Null(entry.Value));
        Assert.Equal((0, 0, 0), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Record_PositiveScore_LocksFieldAgainstRecordAndStrike()
    {
        var card = new Scorecard();
        card.Record(new[] { 5, 5, 5, 5, 5 }, ScoreCategory.Fives);

        Assert.Equal(25, card.Scores[ScoreCategory.Fives]);
        AssertFieldLocked(card, ScoreCategory.Fives);
    }

    [Fact]
    public void Record_ZeroScore_LocksFieldAgainstRecordAndStrike()
    {
        var card = new Scorecard();
        card.Record(new[] { 1, 1, 1, 1, 1 }, ScoreCategory.Fives);

        Assert.Equal(0, card.Scores[ScoreCategory.Fives]);
        AssertFieldLocked(card, ScoreCategory.Fives);
    }

    [Fact]
    public void Strike_LocksFieldAgainstRecordAndStrike()
    {
        var card = new Scorecard();
        card.Strike(ScoreCategory.Fives);

        AssertFieldLocked(card, ScoreCategory.Fives);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnknownCategory_BothWritesRejectAndPreserveCard(int value)
    {
        var card = CreateCardWithBonus();
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);
        var category = (ScoreCategory)value;

        var recordException = Assert.Throws<ArgumentOutOfRangeException>(
            () => card.Record(new[] { 6, 6, 6, 6, 6 }, category));
        Assert.Equal("category", recordException.ParamName);
        Assert.Equal(category, recordException.ActualValue);
        AssertCardUnchanged(card, expectedScores, expectedTotals);

        var strikeException = Assert.Throws<ArgumentOutOfRangeException>(() => card.Strike(category));
        Assert.Equal("category", strikeException.ParamName);
        Assert.Equal(category, strikeException.ActualValue);
        AssertCardUnchanged(card, expectedScores, expectedTotals);
    }

    [Fact]
    public void Record_NullDice_PreservesCardAndLeavesFieldAvailable()
    {
        var card = CreateCardWithBonus();
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);

        var exception = Assert.Throws<ArgumentNullException>(() => card.Record(null!, ScoreCategory.Chance));

        Assert.Equal("dice", exception.ParamName);
        AssertCardUnchanged(card, expectedScores, expectedTotals);
        card.Record(new[] { 5, 5, 5, 2, 2 }, ScoreCategory.Chance);
        Assert.Equal(19, card.Scores[ScoreCategory.Chance]);
        Assert.Equal(132, card.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void Record_WrongDiceCount_PreservesCardAndLeavesFieldAvailable(int count)
    {
        var card = CreateCardWithBonus();
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);
        var dice = Enumerable.Repeat(1, count).ToArray();
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentException>(() => card.Record(dice, ScoreCategory.Chance));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(expectedDice, dice);
        AssertCardUnchanged(card, expectedScores, expectedTotals);
        card.Record(new[] { 5, 5, 5, 2, 2 }, ScoreCategory.Chance);
        Assert.Equal(19, card.Scores[ScoreCategory.Chance]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 2)]
    [InlineData(7, 4)]
    [InlineData(int.MinValue, 3)]
    [InlineData(int.MaxValue, 1)]
    public void Record_InvalidDie_PreservesCardAndLeavesFieldAvailable(int value, int index)
    {
        var card = CreateCardWithBonus();
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);
        int[] dice = { 5, 5, 5, 2, 2 };
        dice[index] = value;
        var expectedDice = dice.ToArray();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => card.Record(dice, ScoreCategory.Chance));

        Assert.Equal("dice", exception.ParamName);
        Assert.Equal(value, exception.ActualValue);
        Assert.Equal(expectedDice, dice);
        AssertCardUnchanged(card, expectedScores, expectedTotals);
        card.Record(new[] { 5, 5, 5, 2, 2 }, ScoreCategory.Chance);
        Assert.Equal(19, card.Scores[ScoreCategory.Chance]);
    }

    [Fact]
    public void Scores_ViewReflectsLaterRecordsAndStrikes()
    {
        var card = new Scorecard();
        var view = card.Scores;

        card.Record(Array.AsReadOnly(new[] { 5, 5, 5, 5, 5 }), ScoreCategory.Fives);
        card.Strike(ScoreCategory.Ones);

        Assert.Same(view, card.Scores);
        Assert.Equal(25, view[ScoreCategory.Fives]);
        Assert.Equal(0, view[ScoreCategory.Ones]);
        Assert.Null(view[ScoreCategory.Sixes]);
        Assert.Equal((25, 0, 25), (card.UpperSum, card.Bonus, card.Total));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scores_DictionaryInterfacesRejectMutation(bool useNonGenericDictionary)
    {
        var card = CreateCardWithBonus();
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);
        Action[] attempts;
        if (useNonGenericDictionary)
        {
            var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(card.Scores);
            attempts = new Action[]
            {
                () => dictionary[ScoreCategory.Chance] = 999,
                () => dictionary.Add((ScoreCategory)16, 0),
                () => dictionary.Remove(ScoreCategory.Fives),
                () => dictionary.Clear()
            };
        }
        else
        {
            var dictionary = Assert.IsAssignableFrom<IDictionary<ScoreCategory, int?>>(card.Scores);
            attempts = new Action[]
            {
                () => dictionary[ScoreCategory.Chance] = 999,
                () => dictionary.Add((ScoreCategory)16, 0),
                () => dictionary.Remove(ScoreCategory.Fives),
                () => dictionary.Clear()
            };
        }

        foreach (var attempt in attempts)
        {
            Assert.Throws<NotSupportedException>(attempt);
            AssertCardUnchanged(card, expectedScores, expectedTotals);
        }
    }

    [Fact]
    public void SeparateScorecards_DoNotShareChanges()
    {
        var firstCard = new Scorecard();
        var secondCard = new Scorecard();

        firstCard.Record(new[] { 5, 5, 5, 5, 5 }, ScoreCategory.Fives);
        firstCard.Strike(ScoreCategory.Chance);
        secondCard.Record(new[] { 5, 5, 5, 2, 2 }, ScoreCategory.Chance);

        Assert.NotSame(firstCard.Scores, secondCard.Scores);
        Assert.Equal(25, firstCard.Scores[ScoreCategory.Fives]);
        Assert.Equal(0, firstCard.Scores[ScoreCategory.Chance]);
        Assert.Equal((25, 0, 25), (firstCard.UpperSum, firstCard.Bonus, firstCard.Total));
        Assert.Null(secondCard.Scores[ScoreCategory.Fives]);
        Assert.Equal(19, secondCard.Scores[ScoreCategory.Chance]);
        Assert.Equal((0, 0, 19), (secondCard.UpperSum, secondCard.Bonus, secondCard.Total));
    }

    [Fact]
    public void Record_DoesNotRetainReferenceToOriginalDice()
    {
        var firstCard = new Scorecard();
        var secondCard = new Scorecard();
        int[] dice = { 5, 5, 5, 2, 2 };
        firstCard.Record(dice, ScoreCategory.Chance);

        Array.Fill(dice, 6);
        secondCard.Record(dice, ScoreCategory.Chance);

        Assert.Equal(19, firstCard.Scores[ScoreCategory.Chance]);
        Assert.Equal(19, firstCard.Total);
        Assert.Equal(30, secondCard.Scores[ScoreCategory.Chance]);
        Assert.Equal(30, secondCard.Total);
    }

    [Theory]
    [InlineData(62, 0, 62)]
    [InlineData(63, 50, 113)]
    [InlineData(64, 50, 114)]
    public void Bonus_UpperSumAroundThreshold_ReturnsExpectedTotals(
        int expectedUpperSum, int expectedBonus, int expectedTotal)
    {
        var card = new Scorecard();
        card.Record(new[] { 5, 5, 5, 5, 5 }, ScoreCategory.Fives);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Sixes);

        // Alle grænser opnås med rigtige kast. De øvre felter Ones og Twos
        // forbliver ledige, så testen også kontrollerer bonus før hele sektionen er udfyldt.
        switch (expectedUpperSum)
        {
            case 62:
                card.Record(new[] { 3, 1, 1, 1, 1 }, ScoreCategory.Threes);
                card.Record(new[] { 4, 1, 1, 1, 1 }, ScoreCategory.Fours);
                break;
            case 63:
                card.Record(new[] { 4, 4, 1, 1, 1 }, ScoreCategory.Fours);
                break;
            case 64:
                card.Record(new[] { 3, 3, 3, 1, 1 }, ScoreCategory.Threes);
                break;
        }

        Assert.Equal(expectedUpperSum, card.UpperSum);
        Assert.Equal(expectedBonus, card.Bonus);
        Assert.Equal(expectedTotal, card.Total);
        Assert.Null(card.Scores[ScoreCategory.Ones]);
        Assert.Null(card.Scores[ScoreCategory.Twos]);
    }

    [Fact]
    public void Bonus_LowerFieldsCannotTriggerBonus()
    {
        var card = new Scorecard();
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Chance);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Yatzy);

        Assert.Equal((0, 0, 80), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Bonus_UpperSumBelowThresholdWithLowerPoints_DoesNotAwardBonus()
    {
        var card = new Scorecard();
        card.Record(new[] { 5, 5, 5, 5, 5 }, ScoreCategory.Fives);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Sixes);
        card.Record(new[] { 3, 1, 1, 1, 1 }, ScoreCategory.Threes);
        card.Record(new[] { 4, 1, 1, 1, 1 }, ScoreCategory.Fours);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Yatzy);

        Assert.Equal((62, 0, 112), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Bonus_StruckUpperFieldDoesNotPreventBonusFromOtherUpperFields()
    {
        var card = CreateCardWithBonus();

        card.Strike(ScoreCategory.Ones);

        Assert.Equal(0, card.Scores[ScoreCategory.Ones]);
        Assert.Equal((63, 50, 113), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Totals_RepeatedReadsAndLaterWrites_CountBonusOnce()
    {
        var card = CreateCardWithBonus();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((63, 50, 113), (card.UpperSum, card.Bonus, card.Total));
        }

        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Chance);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((63, 50, 143), (card.UpperSum, card.Bonus, card.Total));
        }

        card.Strike(ScoreCategory.OnePair);
        Assert.Equal((63, 50, 143), (card.UpperSum, card.Bonus, card.Total));
        card.Record(new[] { 1, 1, 1, 1, 1 }, ScoreCategory.Ones);
        Assert.Equal((68, 50, 148), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Totals_MaximumInEveryCategory_Returns374()
    {
        var card = new Scorecard();
        for (int value = 1; value <= 6; value++)
        {
            card.Record(Enumerable.Repeat(value, 5).ToArray(), (ScoreCategory)value);
        }

        card.Record(new[] { 6, 6, 1, 2, 3 }, ScoreCategory.OnePair);
        card.Record(new[] { 6, 6, 5, 5, 1 }, ScoreCategory.TwoPairs);
        card.Record(new[] { 6, 6, 6, 1, 2 }, ScoreCategory.ThreeOfAKind);
        card.Record(new[] { 6, 6, 6, 6, 1 }, ScoreCategory.FourOfAKind);
        card.Record(new[] { 1, 2, 3, 4, 5 }, ScoreCategory.SmallStraight);
        card.Record(new[] { 2, 3, 4, 5, 6 }, ScoreCategory.LargeStraight);
        card.Record(new[] { 6, 6, 6, 5, 5 }, ScoreCategory.FullHouse);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Chance);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Yatzy);

        Assert.All(card.Scores.Values, score => Assert.NotNull(score));
        Assert.Equal(219, card.Scores.Where(entry => (int)entry.Key > 6).Sum(entry => entry.Value ?? 0));
        Assert.Equal((105, 50, 374), (card.UpperSum, card.Bonus, card.Total));
    }

    [Fact]
    public void Strike_AllFifteenFields_LeavesFilledZerosAndZeroTotals()
    {
        var card = new Scorecard();
        foreach (var category in Enum.GetValues<ScoreCategory>())
        {
            card.Strike(category);
        }

        Assert.Equal(15, card.Scores.Count);
        Assert.True(card.IsComplete);
        Assert.All(card.Scores.Values, score => Assert.Equal(0, score));
        Assert.Equal((0, 0, 0), (card.UpperSum, card.Bonus, card.Total));
        foreach (var category in Enum.GetValues<ScoreCategory>())
        {
            AssertFieldLocked(card, category);
        }
    }

    // 25 i femmere + 30 i seksere + 8 i firere giver bonus med tre øvre felter stadig ledige.
    private static Scorecard CreateCardWithBonus()
    {
        var card = new Scorecard();
        card.Record(new[] { 5, 5, 5, 5, 5 }, ScoreCategory.Fives);
        card.Record(new[] { 6, 6, 6, 6, 6 }, ScoreCategory.Sixes);
        card.Record(new[] { 4, 4, 1, 1, 1 }, ScoreCategory.Fours);
        return card;
    }

    private static void AssertFieldLocked(Scorecard card, ScoreCategory category)
    {
        var expectedScores = card.Scores.ToDictionary(entry => entry.Key, entry => entry.Value);
        var expectedTotals = (card.UpperSum, card.Bonus, card.Total);

        var recordException = Assert.Throws<InvalidOperationException>(
            () => card.Record(new[] { 6, 6, 6, 6, 6 }, category));
        Assert.Equal("Pointfeltet er allerede udfyldt.", recordException.Message);
        AssertCardUnchanged(card, expectedScores, expectedTotals);

        var strikeException = Assert.Throws<InvalidOperationException>(() => card.Strike(category));
        Assert.Equal("Pointfeltet er allerede udfyldt.", strikeException.Message);
        AssertCardUnchanged(card, expectedScores, expectedTotals);
    }

    private static void AssertCardUnchanged(
        Scorecard card, IReadOnlyDictionary<ScoreCategory, int?> expectedScores,
        (int UpperSum, int Bonus, int Total) expectedTotals)
    {
        Assert.Equal(expectedScores.Count, card.Scores.Count);
        foreach (var entry in expectedScores)
        {
            Assert.True(card.Scores.TryGetValue(entry.Key, out int? actualScore));
            Assert.Equal(entry.Value, actualScore);
        }

        Assert.Equal(expectedTotals, (card.UpperSum, card.Bonus, card.Total));
    }
}
