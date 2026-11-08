using System.Text.Json;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Tester sessionens lobbydeltagere, egne pointtavler, kopier og nulstillingsværn.</summary>
public class GameLobbySessionTests
{
    [Theory]
    [InlineData(1)]
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
    public void FieldChoice_ChangesOnlyFirstScorecardAndLeavesTurnCompleted(bool strike)
    {
        var session = new GameSession();
        var start = session.NewLobbyGame(Players(6));
        var rolled = session.Roll()!;
        session.ToggleHold(1);
        var chosen = (strike ? session.Strike(ScoreCategory.Chance) : session.Score(ScoreCategory.Chance))!;
        Assert.Equal(strike ? 0 : rolled.Dice.Sum(), chosen.Players[0].Scorecard.Scores["chance"]);
        Assert.All(chosen.Players.Skip(1), player => Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score)));
        Assert.All(start.Players, player => Assert.All(player.Scorecard.Scores.Values, score => Assert.Null(score)));
        Assert.True(chosen.TurnCompleted);
        Assert.Equal(0, chosen.CurrentPlayer);
        Assert.Equal(rolled.Dice, chosen.Dice);
        Assert.Equal(new[] { false, true, false, false, false }, chosen.Held);
        string before = JsonSerializer.Serialize(chosen);
        Assert.Throws<InvalidOperationException>(() => session.Roll());
        Assert.Throws<InvalidOperationException>(() => session.ToggleHold(0));
        Assert.Throws<InvalidOperationException>(() => session.Score(ScoreCategory.Ones));
        Assert.Throws<InvalidOperationException>(() => session.Strike(ScoreCategory.Ones));
        Assert.Equal(before, JsonSerializer.Serialize(session.GetState()));
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
        string before = JsonSerializer.Serialize(session.GetState());
        Assert.Throws<InvalidOperationException>(() => session.NewGame("Overskrivning"));
        Assert.Throws<InvalidOperationException>(() => session.NewLobbyGame(Players(1)));
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

    private static LobbyGamePlayer[] Players(int count) => Enumerable.Range(0, count)
        .Select(index => new LobbyGamePlayer(Guid.NewGuid(), $"Spiller {index}")).ToArray();
}
