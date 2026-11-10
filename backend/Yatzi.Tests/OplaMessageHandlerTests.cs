using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester håndskrevne Oplà-linjer direkte mod handleren og den fælles spilsession.
/// Der bruges hverken netværk, seriel-port eller fysisk hardware.
/// </summary>
public class OplaMessageHandlerTests
{
    [Fact]
    public void Roll_ThreeAcceptedRolls_ReturnStateAndUpdateSharedSession()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);

        for (int roll = 1; roll <= 3; roll++)
        {
            var replies = handler.HandleLine("ROLL");

            Assert.Equal($"STATE {roll} 00000", Assert.Single(replies));
            var state = session.GetState();
            Assert.NotNull(state);
            Assert.Equal(roll, state.RollsUsed);
            Assert.Equal(5, state.Dice.Length);
            Assert.All(state.Dice, value => Assert.InRange(value, 1, 6));
            Assert.All(state.Held, value => Assert.False(value));
        }
    }

    [Fact]
    public void Roll_AfterThreeRolls_LogsWarningAndPreservesState()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        session.Roll();
        session.ToggleHold(1);
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);
        Assert.Equal("STATE 2 01000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 3 01000", Assert.Single(handler.HandleLine("ROLL")));
        var before = session.GetState();
        Assert.NotNull(before);

        var replies = handler.HandleLine("ROLL");

        Assert.Empty(replies);
        AssertStateUnchanged(before, session.GetState());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ROLL", entry.Message);
        Assert.Contains("tre slag", entry.Message);
    }

    [Fact]
    public void Roll_WithoutGame_LogsWarningAndDoesNotCreateGame()
    {
        var session = new GameSession();
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        var replies = handler.HandleLine("ROLL");

        Assert.Empty(replies);
        Assert.Null(session.GetState());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ROLL", entry.Message);
        Assert.Contains("ikke oprettet et spil", entry.Message);
    }

    [Theory]
    [InlineData("10000")]
    [InlineData("01000")]
    [InlineData("00100")]
    [InlineData("00010")]
    [InlineData("00001")]
    [InlineData("10100")]
    [InlineData("11111")]
    public void Roll_WithHeldDice_ReturnsPatternInDieOrderAndPreservesHeldValues(string heldPattern)
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var firstRoll = session.Roll();
        Assert.NotNull(firstRoll);

        // Hold sættes direkte for at isolere ROLL-testen fra HOLD-fortolkningen.
        var expectedHeld = heldPattern.Select(value => value == '1').ToArray();
        for (int i = 0; i < expectedHeld.Length; i++)
        {
            if (expectedHeld[i])
            {
                session.ToggleHold(i);
            }
        }

        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);

        var replies = handler.HandleLine("ROLL");

        Assert.Equal($"STATE 2 {heldPattern}", Assert.Single(replies));
        var state = session.GetState();
        Assert.NotNull(state);
        Assert.Equal(2, state.RollsUsed);
        Assert.Equal(expectedHeld, state.Held);
        Assert.All(state.Dice, value => Assert.InRange(value, 1, 6));
        for (int i = 0; i < expectedHeld.Length; i++)
        {
            if (expectedHeld[i])
            {
                Assert.Equal(firstRoll.Dice[i], state.Dice[i]);
            }
        }
    }

    [Theory]
    [InlineData(1, 1, "10000")]
    [InlineData(2, 1, "01000")]
    [InlineData(3, 1, "00100")]
    [InlineData(4, 1, "00010")]
    [InlineData(5, 1, "00001")]
    [InlineData(1, 2, "10000")]
    [InlineData(2, 2, "01000")]
    [InlineData(3, 2, "00100")]
    [InlineData(4, 2, "00010")]
    [InlineData(5, 2, "00001")]
    public void Hold_AfterAllowedRolls_TogglesSelectedDieAndReturnsState(
        int number, int rollsUsed, string heldPattern)
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);
        for (int roll = 1; roll <= rollsUsed; roll++)
        {
            Assert.Equal($"STATE {roll} 00000", Assert.Single(handler.HandleLine("ROLL")));
        }

        var before = session.GetState();
        Assert.NotNull(before);
        string line = $"HOLD {number}";

        var replies = handler.HandleLine(line);

        Assert.Equal($"STATE {rollsUsed} {heldPattern}", Assert.Single(replies));
        var held = session.GetState();
        Assert.NotNull(held);
        Assert.Equal(heldPattern.Select(value => value == '1').ToArray(), held.Held);
        Assert.Equal(before.Dice, held.Dice);
        Assert.Equal(rollsUsed, held.RollsUsed);

        var released = handler.HandleLine(line);

        Assert.Equal($"STATE {rollsUsed} 00000", Assert.Single(released));
        AssertStateUnchanged(before, session.GetState());
    }

    [Fact]
    public void Hold_MultipleDice_ChangingOnePreservesOtherHolds()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var before = session.GetState();
        Assert.NotNull(before);

        Assert.Equal("STATE 1 10000", Assert.Single(handler.HandleLine("HOLD 1")));
        Assert.Equal("STATE 1 10100", Assert.Single(handler.HandleLine("HOLD 3")));
        Assert.Equal("STATE 1 00100", Assert.Single(handler.HandleLine("HOLD 1")));

        var state = session.GetState();
        Assert.NotNull(state);
        Assert.Equal(new[] { false, false, true, false, false }, state.Held);
        Assert.Equal(before.Dice, state.Dice);
        Assert.Equal(1, state.RollsUsed);
    }

    [Fact]
    public void Hold_FollowedByRoll_PreservesHeldValueAndUsesOneRoll()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var handler = new OplaMessageHandler(session, NullLogger<OplaMessageHandler>.Instance);
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var firstRoll = session.GetState();
        Assert.NotNull(firstRoll);
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));

        var replies = handler.HandleLine("ROLL");

        Assert.Equal("STATE 2 01000", Assert.Single(replies));
        var state = session.GetState();
        Assert.NotNull(state);
        Assert.Equal(firstRoll.Dice[1], state.Dice[1]);
        Assert.Equal(new[] { false, true, false, false, false }, state.Held);
        Assert.Equal(2, state.RollsUsed);
        Assert.All(state.Dice, value => Assert.InRange(value, 1, 6));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public void Hold_OutsideAllowedRolls_LogsWarningAndPreservesState(int number, int rollsUsed)
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);
        for (int roll = 1; roll <= rollsUsed; roll++)
        {
            Assert.Single(handler.HandleLine("ROLL"));
            if (roll == 1)
            {
                // Før første kast afvises hold; efter tredje kast afvises slip.
                session.ToggleHold(number - 1);
            }
        }

        var before = session.GetState();
        Assert.NotNull(before);
        string line = $"HOLD {number}";

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        AssertStateUnchanged(before, session.GetState());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(line, entry.Message);
        Assert.Contains("første eller andet kast", entry.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Hold_WithoutGame_LogsWarningAndDoesNotCreateGame(int number)
    {
        var session = new GameSession();
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);
        string line = $"HOLD {number}";

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        Assert.Null(session.GetState());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(line, entry.Message);
        Assert.Contains("ikke oprettet et spil", entry.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("roll")]
    [InlineData("ROLL ")]
    [InlineData("ROLL 1")]
    [InlineData("HOLD")]
    [InlineData("HOLD ")]
    [InlineData("HOLD 0")]
    [InlineData("HOLD 6")]
    [InlineData("HOLD a")]
    [InlineData("HOLD 02")]
    [InlineData("HOLD +1")]
    [InlineData("HOLD  1")]
    [InlineData("HOLD 1 ")]
    [InlineData("HOLD 1 2")]
    [InlineData("hold 1")]
    [InlineData(" HOLD 1")]
    [InlineData("HOLD\t1")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("BOGUS")]
    [InlineData("ROLL\n")]
    [InlineData("ROLL\r\n")]
    [InlineData("ROLL\nHOLD 1")]
    [InlineData(" # debug")]
    [InlineData("HOLD 2\r\n")]
    [InlineData("ROLL\r")]
    public void HandleLine_UnrecognizedInput_LogsWarningAndPreservesState(string? line)
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        session.Roll();
        var before = session.ToggleHold(2);
        Assert.NotNull(before);
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        AssertStateUnchanged(before, session.GetState());
        AssertInvalidInputWarning(logger, line?.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BOGUS")]
    [InlineData("HOLD 0")]
    [InlineData("ROLL\nHOLD 1")]
    public void HandleLine_UnrecognizedInputWithoutGame_LogsWarningAndDoesNotCreateGame(string? line)
    {
        var session = new GameSession();
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        Assert.Null(session.GetState());
        AssertInvalidInputWarning(logger, line?.Length);
    }

    [Theory]
    [InlineData("#", false)]
    [InlineData("#", true)]
    [InlineData("# debug", false)]
    [InlineData("# debug", true)]
    [InlineData("# ROLL", false)]
    [InlineData("# ROLL", true)]
    [InlineData("# HOLD 2", false)]
    [InlineData("# HOLD 2", true)]
    public void HandleLine_DebugInput_DoesNotReplyLogOrChangeState(string line, bool hasGame)
    {
        var session = new GameSession();
        GameStateResponse? before = null;
        if (hasGame)
        {
            session.NewGame("Andreas");
            session.Roll();
            before = session.ToggleHold(1);
            Assert.NotNull(before);
        }

        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        Assert.Empty(logger.Entries);
        if (before is null)
        {
            Assert.Null(session.GetState());
        }
        else
        {
            AssertStateUnchanged(before, session.GetState());
        }
    }

    [Fact]
    public void HandleLine_ValidCommandsAfterInvalidAndDebugInput_ContinueNormally()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var firstRoll = session.GetState();
        Assert.NotNull(firstRoll);

        Assert.Empty(handler.HandleLine("HOLD 0"));
        Assert.Empty(handler.HandleLine("# HOLD 2"));
        AssertStateUnchanged(firstRoll, session.GetState());
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));
        Assert.Equal("STATE 2 01000", Assert.Single(handler.HandleLine("ROLL")));

        var state = session.GetState();
        Assert.NotNull(state);
        Assert.Equal(2, state.RollsUsed);
        Assert.Equal(new[] { false, true, false, false, false }, state.Held);
        Assert.Equal(firstRoll.Dice[1], state.Dice[1]);
        AssertInvalidInputWarning(logger, "HOLD 0".Length);
    }

    [Fact]
    public void HandleLine_ValidRollAndHoldSequence_DoesNotLog()
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("HOLD 2")));
        Assert.Equal("STATE 2 00000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 3 00000", Assert.Single(handler.HandleLine("ROLL")));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void HandleLine_UnrecognizedInput_LogsLengthWithoutInputText()
    {
        const string line = "BOGUS private-value\r\nROLL";
        var session = new GameSession();
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        var replies = handler.HandleLine(line);

        Assert.Empty(replies);
        Assert.Null(session.GetState());
        AssertInvalidInputWarning(logger, line.Length);
        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain("private-value", entry.Message);
        Assert.DoesNotContain('\r', entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        foreach (var value in entry.Properties.Values)
        {
            Assert.DoesNotContain("private-value", value?.ToString() ?? string.Empty);
        }
    }

    [Theory]
    [InlineData(false, "ROLL")]
    [InlineData(false, "HOLD 1")]
    [InlineData(false, "HOLD 2")]
    [InlineData(false, "HOLD 3")]
    [InlineData(false, "HOLD 4")]
    [InlineData(false, "HOLD 5")]
    [InlineData(true, "ROLL")]
    [InlineData(true, "HOLD 1")]
    [InlineData(true, "HOLD 2")]
    [InlineData(true, "HOLD 3")]
    [InlineData(true, "HOLD 4")]
    [InlineData(true, "HOLD 5")]
    public void NextTurn_AcceptsRollRejectsHoldBeforeRollAndNewGameReusesHandler(bool strike, string line)
    {
        var session = new GameSession();
        session.NewGame("Andreas");
        session.Roll();
        session.ToggleHold(1);
        var before = strike
            ? session.Strike(Yatzi.Core.ScoreCategory.Chance)!
            : session.Score(Yatzi.Core.ScoreCategory.Chance)!;
        var logger = new RecordingLogger();
        var handler = new OplaMessageHandler(session, logger);

        Assert.False(before.TurnCompleted);
        if (line == "ROLL")
        {
            Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine(line)));
            Assert.Equal(before.Players[0].Scorecard.Scores["chance"], session.GetState()!.Players[0].Scorecard.Scores["chance"]);
            Assert.Empty(logger.Entries);
        }
        else
        {
            Assert.Empty(handler.HandleLine(line));
            GameStateAssertions.Equal(before, session.GetState()!);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(line, entry.Message);
            Assert.Contains("Hold/slip er kun tilladt efter første eller andet kast.", entry.Message);
        }

        session.NewGame("Frederik");

        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var replacement = session.GetState()!;
        Assert.Equal("Frederik", replacement.Players[0].Name);
        Assert.All(replacement.Players[0].Scorecard.Scores.Values, score => Assert.Null(score));
        Assert.False(replacement.TurnCompleted);
        Assert.Equal(line == "ROLL" ? 0 : 1, logger.Entries.Count);
    }

    private static void AssertInvalidInputWarning(RecordingLogger logger, int? expectedLength)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Ukendt kommando eller ugyldigt format", entry.Message);
        Assert.True(entry.Properties.TryGetValue("Length", out var length));
        Assert.Equal((object?)expectedLength, length);
    }

    private static void AssertStateUnchanged(GameStateResponse expected, GameStateResponse? actual)
    {
        Assert.NotNull(actual);
        GameStateAssertions.Equal(expected, actual);
    }

    /// <summary>
    /// Gemmer logniveau, besked og strukturerede felter, så afvisningernes årsag
    /// og inputlængde kan kontrolleres uden at gemme den ukendte tekst i loggen.
    /// </summary>
    private sealed class RecordingLogger : ILogger<OplaMessageHandler>
    {
        public List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            Entries.Add((logLevel, formatter(state, exception), properties));
        }
    }
}
