using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>
/// Kontrollerer starttilstanden, kast, hold/slip, slaggrænsen og beskyttelsen af spillets data.
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

    // TODO: tests af pointberegning og spillets afslutning.
}
