using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>Tester identitet, turregler og kopier ved Oplà-indgangen uden transport.</summary>
public class GameOplaIdentityTests : IDisposable
{
    private readonly GameSession _game = new();
    private readonly RecordingLoggerProvider _logs = new(typeof(OplaMessageHandler).FullName!);
    private readonly ILoggerFactory _logging;
    private readonly OplaMessageHandler _handler;
    private readonly LobbyGamePlayer[] _players = [new(Guid.NewGuid(), "A"), new(Guid.NewGuid(), "A")];

    public GameOplaIdentityTests()
    {
        _logging = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _handler = new(_game, _logging.CreateLogger<OplaMessageHandler>());
    }

    [Theory]
    [InlineData(0, "ROLL")]
    [InlineData(1, "ROLL")]
    [InlineData(2, "ROLL")]
    [InlineData(3, "ROLL")]
    [InlineData(0, "HOLD 1")]
    [InlineData(1, "HOLD 1")]
    [InlineData(2, "HOLD 1")]
    [InlineData(3, "HOLD 1")]
    public void WrongOrMissingIdentity_DoesNotChangeLobbyGame(int identity, string command)
    {
        _game.NewLobbyGame(_players);
        _game.Roll();
        string before = Snapshot();
        Guid? id = identity switch { 0 => null, 1 => Guid.Empty, 2 => _players[1].Id, _ => Guid.NewGuid() };
        Assert.Empty(_handler.HandleLine(command, id));
        Assert.Equal(before, Snapshot());
        Assert.Equal(LogLevel.Warning, Assert.Single(_logs.Entries).Level);
        // Den offentlige sessionindgang håndhæver kontrollen uden handlerens hjælp.
        Assert.Throws<InvalidOperationException>(() => _game.RollFromOpla(id));
        Assert.Throws<InvalidOperationException>(() => _game.ToggleHoldFromOpla(0, id));
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void ActiveIdentity_UsesCoreRulesAndKeepsOtherScorecards(int count)
    {
        var players = Enumerable.Range(0, count).Select(i => new LobbyGamePlayer(Guid.NewGuid(), $"Spiller {i}")).ToArray();
        _game.NewLobbyGame(players);
        Guid id = players[0].Id;
        Assert.Empty(_handler.HandleLine("HOLD 1", id));
        Assert.Equal("STATE 1 00000", Assert.Single(_handler.HandleLine("ROLL", id)));
        var rolled = _game.GetState()!;
        Assert.Equal("STATE 1 01000", Assert.Single(_handler.HandleLine("HOLD 2", id)));
        Assert.Equal("STATE 2 01000", Assert.Single(_handler.HandleLine("ROLL", id)));
        Assert.Equal(rolled.Dice[1], _game.GetState()!.Dice[1]);
        Assert.Equal("STATE 2 00000", Assert.Single(_handler.HandleLine("HOLD 2", id)));
        Assert.Equal("STATE 3 00000", Assert.Single(_handler.HandleLine("ROLL", id)));
        string before = Snapshot();
        Assert.Empty(_handler.HandleLine("ROLL", id));
        Assert.Empty(_handler.HandleLine("HOLD 1", id));
        Assert.Equal(before, Snapshot());
        _game.Strike(ScoreCategory.Chance);
        before = Snapshot();
        Assert.Empty(_handler.HandleLine("ROLL", id));
        Assert.Empty(_handler.HandleLine("HOLD 1", id));
        Assert.Equal(before, Snapshot());
        var state = _game.GetState()!;
        Assert.True(state.TurnCompleted);
        Assert.Equal(0, state.CurrentPlayer);
        Assert.All(state.Players.Skip(1), player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
        Assert.All(rolled.Players, player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandaloneGame_PreservesHandlerWithAndWithoutServerIdentity(bool withIdentity)
    {
        _game.NewGame("A");
        Guid? id = withIdentity ? Guid.NewGuid() : null;
        Assert.Equal("STATE 1 00000", Assert.Single(_handler.HandleLine("ROLL", id)));
        Assert.Equal("STATE 1 10000", Assert.Single(_handler.HandleLine("HOLD 1", id)));
        _game.NewGame("B");
        Assert.Equal("STATE 1 00000", Assert.Single(_handler.HandleLine("ROLL", id)));
        Assert.Empty(_logs.Entries);
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("HOLD 1")]
    public void MissingGame_LogsOnceAndCreatesNothing(string command)
    {
        Assert.Empty(_handler.HandleLine(command, Guid.NewGuid()));
        Assert.Null(_game.GetState());
        Assert.Single(_logs.Entries);
    }

    [Fact]
    public void DebugAndInvalidInput_PreserveExistingLoggingContract()
    {
        _game.NewLobbyGame(_players);
        string before = Snapshot();
        Assert.Empty(_handler.HandleLine("# ROLL", _players[0].Id));
        Assert.Empty(_logs.Entries);
        Assert.Empty(_handler.HandleLine("HOLD 01", _players[0].Id));
        var warning = Assert.Single(_logs.Entries);
        Assert.Equal(7, warning.Properties["Length"]);
        Assert.DoesNotContain("HOLD 01", warning.Message);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ConcurrentHttpAndOplaRolls_UseOneLimitAndCopiedSnapshots()
    {
        _game.NewLobbyGame(_players);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await start.Task;
            try { return i % 2 == 0 ? _game.Roll() : _game.RollFromOpla(_players[0].Id); }
            catch (InvalidOperationException) { return null; }
        })).ToArray();
        start.SetResult();
        var accepted = (await Task.WhenAll(tasks)).OfType<GameStateResponse>().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, accepted.Select(state => state.RollsUsed).Order());
        Assert.Equal(3, _game.GetState()!.RollsUsed);
        accepted[0].Dice[0] = 99;
        accepted[0].Players[1].Scorecard.Scores["ones"] = 30;
        Assert.DoesNotContain(99, _game.GetState()!.Dice);
        Assert.Null(_game.GetState()!.Players[1].Scorecard.Scores["ones"]);
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("HOLD 1")]
    public void CancelledAction_PreservesStateWithoutWarning(string command)
    {
        _game.NewLobbyGame(_players);
        _game.Roll();
        string before = Snapshot();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _handler.HandleLine(command, _players[0].Id, stopped.Token));
        Assert.Equal(before, Snapshot());
        Assert.Empty(_logs.Entries);
    }

    private string Snapshot() => JsonSerializer.Serialize(_game.GetState());
    public void Dispose() => _logging.Dispose();
}
