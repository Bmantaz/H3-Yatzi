using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Følger WebSocket-input gennem den rigtige handler og fælles GameSession til
/// STATE og HTTP. Kun testværtens READY-behandler parrer; der er ingen testrute.
/// Kontrollerede sockets fremkalder sendesvigt uden porte eller ventepauser.
/// </summary>
public class OplaWebSocketGameIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ManualTimestampTimeProvider _clock = new();
    private readonly RecordingReadyHandler _ready = new();
    private readonly RecordingLoggerProvider _transportLogs = new(nameof(OplaWebSocketEndpoints));
    private readonly RecordingLoggerProvider _handlerLogs = new(typeof(OplaMessageHandler).FullName!);
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private OplaConnectionRegistry Registry => _factory.Services.GetRequiredService<OplaConnectionRegistry>();

    public OplaWebSocketGameIntegrationTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IOplaReadyHandler>(_ready);
            })
            .ConfigureLogging(logging => logging.AddProvider(_transportLogs).AddProvider(_handlerLogs)));
        _client = _factory.CreateClient();
        _ready.HandleAction = call =>
        {
            if (!Registry.TryGetPlayerId(call.ConnectionId, out _))
                Assert.True(Registry.TryBindPlayer(call.ConnectionId, Guid.NewGuid()));
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task CompleteTurn_UpdatesHttpAndPreservesHeldDice()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            await CommandAsync(socket, "ROLL", "STATE 1 00000");
            var first = await StateAsync();
            Assert.Equal(1, first.RollsUsed);
            await CommandAsync(socket, "HOLD 2", "STATE 1 01000");
            var held = await StateAsync();
            Assert.Equal(first.Dice, held.Dice);
            Assert.Equal(new[] { false, true, false, false, false }, held.Held);
            await CommandAsync(socket, "ROLL", "STATE 2 01000");
            var second = await StateAsync();
            Assert.Equal(first.Dice[1], second.Dice[1]);
            await CommandAsync(socket, "HOLD 2", "STATE 2 00000");
            Assert.Equal(second.Dice, (await StateAsync()).Dice);
            await CommandAsync(socket, "ROLL", "STATE 3 00000");
            Assert.Equal(3, (await StateAsync()).RollsUsed);
            await CloseAsync(socket, connection);
        }
        Assert.Empty(_transportLogs.Entries);
        Assert.Empty(_handlerLogs.Entries);
    }

    [Fact]
    public async Task HttpAndWebSocket_UseSameSessionAndReplacementGame()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            using var rolled = await GameHttpActions.PostAsync(_client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState(), cancellationToken: _deadline.Token);
            rolled.EnsureSuccessStatusCode();
            await CommandAsync(socket, "HOLD 2", "STATE 1 01000");
            using var held = await GameHttpActions.PostAsync(_client, "/api/game/hold/4", _factory.Services.GetRequiredService<GameSession>().GetState(), cancellationToken: _deadline.Token);
            held.EnsureSuccessStatusCode();
            await CommandAsync(socket, "ROLL", "STATE 2 01010");
            var previous = await StateAsync();
            Assert.Equal(2, previous.RollsUsed);
            await CreateGameAsync("Frederik");
            Assert.Equal(0, (await StateAsync()).RollsUsed);
            await CommandAsync(socket, "ROLL", "STATE 1 00000");
            var current = await StateAsync();
            Assert.Equal("Frederik", Assert.Single(current.Players).Name);
            Assert.Equal(1, current.RollsUsed);
            Assert.Equal(OplaConnectionState.Paired, connection.State);
            await CloseAsync(socket, connection);
        }
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("ROLL ")]
    [InlineData("HOLD 02")]
    [InlineData("HOLD 6")]
    [InlineData("READY 123")]
    [InlineData("PAIR 4821")]
    [InlineData("JOINED Andreas")]
    [InlineData("ukendt")]
    public async Task InvalidCommand_LogsOnlyHandlerWarningAndPreservesState(string text)
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            var before = await StateAsync();
            await RejectAsync(socket, text);
            GameStateAssertions.Equal(before, await StateAsync());
            var entry = Assert.Single(_handlerLogs.Entries);
            Assert.Contains("Ukendt kommando eller ugyldigt format", entry.Message);
            Assert.Equal(text.Length, entry.Properties["Length"]);
            Assert.All(entry.Properties.Values.OfType<string>(), value => Assert.DoesNotContain("4821", value));
            await CloseAsync(socket, connection);
        }
        Assert.Empty(_transportLogs.Entries);
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("HOLD 1")]
    public async Task MissingGame_DoesNotCreateGameOrReply(string command)
    {
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            await RejectAsync(socket, command);
            using var missing = await _client.GetAsync("/api/game", _deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Single(_handlerLogs.Entries);
            Assert.Empty(_transportLogs.Entries);
            await CloseAsync(socket, connection);
        }
    }

    [Theory]
    [InlineData("beforeRoll", "HOLD 1")]
    [InlineData("threeRolls", "ROLL")]
    [InlineData("threeRolls", "HOLD 2")]
    [InlineData("score", "ROLL")]
    [InlineData("score", "HOLD 2")]
    [InlineData("strike", "ROLL")]
    [InlineData("strike", "HOLD 2")]
    public async Task RuleChecks_RejectIllegalActionsAndAcceptRollInNextTurn(string situation, string command)
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            if (situation != "beforeRoll") await CommandAsync(socket, "ROLL", "STATE 1 00000");
            if (situation == "threeRolls")
            {
                await CommandAsync(socket, "ROLL", "STATE 2 00000");
                await CommandAsync(socket, "ROLL", "STATE 3 00000");
            }
            if (situation is "score" or "strike")
            {
                using var scored = await GameHttpActions.PostAsync(_client, $"/api/game/{situation}/chance", _factory.Services.GetRequiredService<GameSession>().GetState(), cancellationToken: _deadline.Token);
                scored.EnsureSuccessStatusCode();
            }
            var before = await StateAsync();
            if ((situation is "score" or "strike") && command == "ROLL")
            {
                Assert.False(before.TurnCompleted);
                await CommandAsync(socket, command, "STATE 1 00000");
                var next = await StateAsync();
                Assert.Equal(1, next.RollsUsed);
                Assert.Equal(before.Players[0].Scorecard.Scores["chance"], next.Players[0].Scorecard.Scores["chance"]);
                Assert.Empty(_handlerLogs.Entries);
            }
            else
            {
                await RejectAsync(socket, command);
                GameStateAssertions.Equal(before, await StateAsync());
                Assert.Single(_handlerLogs.Entries);
            }
            Assert.Empty(_transportLogs.Entries);
            await CloseAsync(socket, connection);
        }
    }

    [Fact]
    public async Task ReadyDebugAndInvalidTransport_DoNotReachGameHandler()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            var before = await StateAsync();
            await SendAsync(socket, "# ROLL");
            await SendAsync(socket, "READY 0012");
            Assert.Equal(connection.ConnectionId, (await _ready.ReadAsync(_deadline.Token)).ConnectionId);
            Assert.Empty(_handlerLogs.Entries);
            Assert.Empty(_transportLogs.Entries);
            foreach (string text in new[] { "", "ROLL\nHOLD 1", "# debug\rROLL" })
            {
                await SendAsync(socket, text);
                Assert.Equal(LogLevel.Warning, (await _transportLogs.ReadAsync(_deadline.Token)).Level);
            }
            GameStateAssertions.Equal(before, await StateAsync());
            Assert.Empty(_handlerLogs.Entries);
            await CloseAsync(socket, connection);
        }
    }

    [Fact]
    public async Task TwoPairedConnections_OnlyReceiveOwnActionReplies()
    {
        await CreateGameAsync();
        var (first, firstConnection) = await ConnectPairedAsync();
        var (second, secondConnection) = await ConnectPairedAsync();
        using (first)
        using (second)
        {
            await CommandAsync(first, "ROLL", "STATE 1 00000");
            await CommandAsync(second, "HOLD 2", "STATE 1 01000");
            await CommandAsync(first, "HOLD 4", "STATE 1 01010");
            var state = await StateAsync();
            Assert.Equal(1, state.RollsUsed);
            Assert.Equal(new[] { false, true, false, true, false }, state.Held);
            // Close skal være næste besked hos begge. Ekstra broadcast-svar opdages her.
            await CloseAsync(first, firstConnection);
            await CloseAsync(second, secondConnection);
        }
    }

    [Fact]
    public async Task FragmentedRollAndHold_ExecuteOnceAfterCompletion()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            foreach (string command in new[] { "ROLL", "HOLD 2" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(command);
                for (int i = 0; i < bytes.Length; i++)
                    await socket.SendAsync(new ArraySegment<byte>(bytes, i, 1), WebSocketMessageType.Text,
                        i == bytes.Length - 1, _deadline.Token);
                Assert.Equal(command == "ROLL" ? "STATE 1 00000" : "STATE 1 01000", await ReceiveTextAsync(socket));
            }
            Assert.Equal(1, (await StateAsync()).RollsUsed);
            Assert.Empty(_handlerLogs.Entries);
            await CloseAsync(socket, connection);
        }
    }

    [Fact]
    public async Task InterruptedCommand_DoesNotExecutePartialInput()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            var before = await StateAsync();
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("ROL")),
                WebSocketMessageType.Text, false, _deadline.Token);
            await CloseAsync(socket, connection);
            GameStateAssertions.Equal(before, await StateAsync());
            Assert.Empty(_handlerLogs.Entries);
        }
    }

    [Fact]
    public async Task ConcurrentHttpAndWebSocketRolls_OnlyUseThreeRolls()
    {
        await CreateGameAsync();
        var (socket, connection) = await ConnectPairedAsync();
        using (socket)
        {
            var contextState = await StateAsync();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var httpTasks = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;
                using var response = await GameHttpActions.PostAsync(_client, "/api/game/roll", contextState, cancellationToken: _deadline.Token);
                if (response.IsSuccessStatusCode)
                    return (int?)(await response.Content.ReadFromJsonAsync<GameStateResponse>(_deadline.Token))!.RollsUsed;
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                return null;
            }).ToArray();
            var webSocketWork = SendRollsAsync();
            start.SetResult();
            var httpRolls = await Task.WhenAll(httpTasks);
            await webSocketWork;
            // READY markerer, at begge kommandoer er behandlet. Læs de eventuelle
            // STATE-svar frem til Close; en regelafvisning sender netop intet svar.
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
            var rollNumbers = httpRolls.Where(value => value.HasValue).Select(value => value!.Value).ToList();
            while (true)
            {
                byte[] buffer = new byte[256];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                Assert.Equal(WebSocketMessageType.Text, result.MessageType);
                Assert.True(result.EndOfMessage);
                string reply = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Assert.Matches("^STATE [1-3] 00000$", reply);
                rollNumbers.Add(reply[6] - '0');
            }
            await connection.Completion.WaitAsync(_deadline.Token);
            Assert.Equal(new[] { 1, 2, 3 }, rollNumbers.OrderBy(number => number));
            Assert.Equal(3, (await StateAsync()).RollsUsed);
            Assert.Equal(httpRolls.Count(value => value.HasValue) - 1, _handlerLogs.Entries.Count);
            Assert.Empty(_transportLogs.Entries);

            async Task SendRollsAsync()
            {
                await start.Task;
                await SendAsync(socket, "ROLL");
                await SendAsync(socket, "ROLL");
                await SendAsync(socket, "READY");
                await _ready.ReadAsync(_deadline.Token);
            }
        }
    }

    [Fact]
    public async Task SlowReply_PreservesSnapshotAndDoesNotBlockHttpOrAnotherConnection()
    {
        await CreateGameAsync();
        using var lifetime = new TestApplicationLifetime();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = CreateRollSocket();
        socket.SendAction = token => release.Task.WaitAsync(token);
        var handling = StartControlled(socket, lifetime);
        await socket.SendStarted.Task.WaitAsync(_deadline.Token);
        await _ready.ReadAsync(_deadline.Token);
        Assert.Equal(1, (await StateAsync()).RollsUsed);
        using var rolled = await GameHttpActions.PostAsync(_client, "/api/game/roll", _factory.Services.GetRequiredService<GameSession>().GetState(), cancellationToken: _deadline.Token);
        rolled.EnsureSuccessStatusCode();
        var (other, otherConnection) = await ConnectPairedAsync();
        using (other)
        {
            await CommandAsync(other, "HOLD 1", "STATE 2 10000");
            Assert.False(handling.IsCompleted);
            Assert.Equal("STATE 1 00000", Encoding.UTF8.GetString(Assert.Single(socket.Sent)));
            release.SetResult();
            await handling.WaitAsync(_deadline.Token);
            await CloseAsync(other, otherConnection);
        }
        Assert.Equal(2, (await StateAsync()).RollsUsed);
        Assert.Equal(0, Registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("unexpected")]
    public async Task SendFailure_KeepsAcceptedActionWithoutRetryOrRerouting(string kind)
    {
        await CreateGameAsync();
        using var lifetime = new TestApplicationLifetime();
        var socket = CreateRollSocket();
        socket.SendAction = _ => Task.FromException(kind switch
        {
            "websocket" => new WebSocketException(WebSocketError.ConnectionClosedPrematurely),
            "io" => new IOException("testfejl"),
            _ => new InvalidOperationException("testfejl")
        });
        var handling = StartControlled(socket, lifetime);
        if (kind == "unexpected") await Assert.ThrowsAsync<InvalidOperationException>(() => handling);
        else await handling.WaitAsync(_deadline.Token);
        await _ready.ReadAsync(_deadline.Token);
        Assert.Equal(1, (await StateAsync()).RollsUsed);
        Assert.Single(socket.Sent); // Ét afsendelsesforsøg, ikke en bekræftet levering.
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(0, Registry.Count);
        // En ny forbindelse får kun sit eget svar, og det gamle kast gentages ikke.
        var (replacement, connection) = await ConnectPairedAsync();
        using (replacement)
        {
            await CommandAsync(replacement, "HOLD 2", "STATE 1 01000");
            await CloseAsync(replacement, connection);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSend_CleansUpWithoutUndoingAcceptedRoll(bool stopServer)
    {
        await CreateGameAsync();
        using var lifetime = new TestApplicationLifetime();
        using var request = new CancellationTokenSource();
        var socket = CreateRollSocket();
        socket.SendAction = token => Task.Delay(Timeout.Infinite, token);
        var handling = StartControlled(socket, lifetime, request.Token);
        await socket.SendStarted.Task.WaitAsync(_deadline.Token);
        if (stopServer) lifetime.StopApplication();
        else request.Cancel();
        await handling.WaitAsync(_deadline.Token);
        Assert.Equal(1, (await StateAsync()).RollsUsed);
        Assert.Single(socket.Sent);
        Assert.Equal(0, Registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task SendReturnsFalse_EndsLifetimeWithoutRetryingAction()
    {
        await CreateGameAsync();
        using var lifetime = new TestApplicationLifetime();
        // Transporten bryder sammen efter en komplet kommando, før svarafsendelse.
        var socket = CreateRollSocket(current => current.Abort());
        await StartControlled(socket, lifetime).WaitAsync(_deadline.Token);
        Assert.Equal(1, (await StateAsync()).RollsUsed);
        Assert.Empty(socket.Sent);
        Assert.Equal(0, Registry.Count);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task ClosureBeforeAccessCheck_DoesNotCallGameHandler()
    {
        await CreateGameAsync();
        var before = await StateAsync();
        using var lifetime = new TestApplicationLifetime();
        var socket = CreateRollSocket(_ => Assert.True(Registry.Remove(Assert.Single(Registry.GetConnections()).ConnectionId)));
        await StartControlled(socket, lifetime).WaitAsync(_deadline.Token);
        GameStateAssertions.Equal(before, await StateAsync());
        Assert.Empty(socket.Sent);
        Assert.Empty(_handlerLogs.Entries);
        Assert.Single(_transportLogs.Entries);
        Assert.Equal(0, Registry.Count);
    }

    private async Task CreateGameAsync(string name = "Andreas")
    {
        using var response = await _client.PostAsJsonAsync("/api/game/new", new { name }, _deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<GameStateResponse> StateAsync()
    {
        var state = await _client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token);
        Assert.NotNull(state);
        return state;
    }

    private async Task<(WebSocket Socket, OplaConnection Connection)> ConnectPairedAsync()
    {
        var socket = await _factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/opla"), _deadline.Token);
        await SendAsync(socket, "READY");
        var call = await _ready.ReadAsync(_deadline.Token);
        Assert.True(Registry.TryGet(call.ConnectionId, out var connection));
        Assert.Equal(OplaConnectionState.Paired, connection.State);
        return (socket, connection);
    }

    private Task SendAsync(WebSocket socket, string text) => socket.SendAsync(
        new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, _deadline.Token);

    private async Task CommandAsync(WebSocket socket, string command, string expected)
    {
        await SendAsync(socket, command);
        Assert.Equal(expected, await ReceiveTextAsync(socket));
    }

    private async Task RejectAsync(WebSocket socket, string command)
    {
        await SendAsync(socket, command);
        Assert.Equal(LogLevel.Warning, (await _handlerLogs.ReadAsync(_deadline.Token)).Level);
    }

    private async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        byte[] buffer = new byte[256];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private async Task CloseAsync(WebSocket socket, OplaConnection connection)
    {
        // Første ulæste besked skal være Close. Det opdager også svar ved afvisning
        // og ekstra svar, uden at bruge en ventepause som bevis for tavshed.
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        await connection.Completion.WaitAsync(_deadline.Token);
        Assert.False(Registry.TryGet(connection.ConnectionId, out _));
    }

    private static ControlledWebSocket CreateRollSocket(Action<ControlledWebSocket>? beforeRoll = null)
    {
        var socket = new ControlledWebSocket();
        int received = 0;
        socket.ReceiveAction = (buffer, _) =>
        {
            int frame = received++;
            if (frame == 2) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            byte[] bytes = Encoding.UTF8.GetBytes(frame == 0 ? "READY" : "ROLL");
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            if (frame == 1) beforeRoll?.Invoke(socket);
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        };
        return socket;
    }

    private Task StartControlled(ControlledWebSocket socket, TestApplicationLifetime lifetime,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };
        context.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(() => Task.FromResult<WebSocket>(socket)));
        return OplaWebSocketEndpoints.HandleAsync(context, Registry, lifetime,
            _factory.Services.GetRequiredService<ILoggerFactory>(), _clock, _ready,
            _factory.Services.GetRequiredService<OplaMessageHandler>(), _factory.Services.GetRequiredService<LobbySession>());
    }

    public void Dispose()
    {
        _deadline.Cancel();
        _client.Dispose();
        _factory.Dispose();
        _deadline.Dispose();
    }
}
