using Yatzi.Core;

namespace Yatzi.Tests;

public class GameTests
{
    [Fact]
    public void Game_CanBeCreated()
    {
        var game = new Game();
        Assert.NotNull(game);
    }

    // TODO: tests af pointberegning, max 3 slag, hold/slip, slut på spillet
}
