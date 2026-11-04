using Yatzi.Api;

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
        created.Players[0] = new PlayerResponse("Frederik");
        firstRead.Dice[1] = 5;
        firstRead.Held[1] = true;
        firstRead.Players[0] = new PlayerResponse("Bjarke");

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
}
