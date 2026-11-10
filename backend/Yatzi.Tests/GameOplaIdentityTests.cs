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
    [InlineData(2)]
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
        var next = _game.Strike(ScoreCategory.Chance)!;
        before = Snapshot();
        Assert.False(next.TurnCompleted);
        Assert.Equal(1 % count, next.CurrentPlayer);
        Assert.Equal(0, next.RollsUsed);
        Assert.Equal(new int[5], next.Dice);
        Assert.Equal(new bool[5], next.Held);
        if (count > 1)
        {
            Assert.Empty(_handler.HandleLine("ROLL", id));
            Assert.Empty(_handler.HandleLine("HOLD 1", id));
            Assert.Equal(before, Snapshot());
        }
        Guid nextId = players[next.CurrentPlayer].Id;
        Assert.Equal("STATE 1 00000", Assert.Single(_handler.HandleLine("ROLL", nextId)));
        Assert.Equal("STATE 1 10000", Assert.Single(_handler.HandleLine("HOLD 1", nextId)));
        Assert.All(next.Players.Skip(1), player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
        Assert.All(rolled.Players, player => Assert.All(player.Scorecard.Scores.Values, Assert.Null));
    }

    [Fact]
    public void IdentityValidation_FollowsRotationEvenWhenPlayersHaveEqualNames()
    {
        _game.NewLobbyGame(_players);
        for (int turn = 0; turn < 3; turn++)
        {
            int current = turn % 2;
            Guid id = _players[current].Id;
            string before = Snapshot();
            foreach (Guid? wrong in new Guid?[] { null, Guid.Empty, Guid.NewGuid(), _players[1 - current].Id })
            {
                Assert.Throws<InvalidOperationException>(() => _game.RollFromOpla(wrong));
                Assert.Throws<InvalidOperationException>(() => _game.ToggleHoldFromOpla(0, wrong));
                Assert.Equal(before, Snapshot());
            }
            Assert.Equal("STATE 1 00000", Assert.Single(_handler.HandleLine("ROLL", id)));
            Assert.Equal("STATE 1 01000", Assert.Single(_handler.HandleLine("HOLD 2", id)));
            _game.Strike(turn < 2 ? ScoreCategory.Chance : ScoreCategory.Ones);
            Assert.Equal(1 - current, _game.GetState()!.CurrentPlayer);
        }
        Assert.Equal(0, _game.GetState()!.Players[0].Scorecard.Scores["chance"]);
        Assert.Equal(0, _game.GetState()!.Players[1].Scorecard.Scores["chance"]);
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
    [InlineData("ROLL", false)]
    [InlineData("HOLD 1", false)]
    [InlineData("ROLL", true)]
    [InlineData("HOLD 1", true)]
    public void CancelledAction_PreservesStateWithoutWarning(string command, bool afterRotation)
    {
        _game.NewLobbyGame(_players);
        if (afterRotation)
        {
            _game.Roll();
            _game.Strike(ScoreCategory.Chance);
        }
        _game.Roll();
        string before = Snapshot();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _handler.HandleLine(command, _players[afterRotation ? 1 : 0].Id, stopped.Token));
        Assert.Equal(before, Snapshot());
        Assert.Empty(_logs.Entries);
    }

    private string Snapshot() => JsonSerializer.Serialize(_game.GetState());
    public void Dispose() => _logging.Dispose();
}
