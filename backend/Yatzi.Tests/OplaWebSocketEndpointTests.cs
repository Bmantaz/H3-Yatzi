using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester routing, READY, adgang og transport i hukommelsen. READY- og logsignaler
/// synkroniserer modtagelsen; ingen ekkokommando, fysisk Oplà eller åben port bruges.
/// Klassens standardtestvært bruger RecordingReadyHandler til isoleret transport;
/// særskilte tests opretter en ny vært med den rigtige LobbySession.
/// </summary>
public class OplaWebSocketEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ManualTimestampTimeProvider _clock = new();
    private readonly RecordingLoggerProvider _logs = new(nameof(OplaWebSocketEndpoints));
    private readonly RecordingLoggerProvider _handlerLogs = new(typeof(OplaMessageHandler).FullName!);
    private readonly RecordingReadyHandler _ready = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private OplaConnectionRegistry Registry => _factory.Services.GetRequiredService<OplaConnectionRegistry>();

    public OplaWebSocketEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IOplaReadyHandler>(_ready);
            })
            .ConfigureLogging(logging => logging.AddProvider(_logs).AddProvider(_handlerLogs)));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task Http_WithoutUpgradeIsRejectedInEveryEnvironment(string environment)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/ws/opla", _deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<OplaConnectionRegistry>().Count);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task WebSocket_DefaultReadyAndAccessWorkInEveryEnvironment(string environment)
    {
        var logs = new RecordingLoggerProvider(nameof(OplaWebSocketEndpoints));
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment)
                .ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Assert.Same(factory.Services.GetRequiredService<LobbySession>(), factory.Services.GetRequiredService<IOplaReadyHandler>());
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/opla"), _deadline.Token);
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY 0012"));
        string pair = await ReceiveTextAsync(socket);
        Assert.Matches("^PAIR [0-9]{4}$", pair);
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY 9999"));
        Assert.Equal(pair, await ReceiveTextAsync(socket));
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(socket, Encoding.UTF8.GetBytes(command));
            Assert.Contains("READY eller parring mangler", (await logs.ReadAsync(_deadline.Token)).Message);
        }
        var connection = Assert.Single(registry.GetConnections());
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        // PAIR er læst. De afviste spilkommandoer må ikke have sendt STATE eller ekko.
        Assert.Equal(WebSocketMessageType.Close,
            (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await connection.Completion.WaitAsync(_deadline.Token);
        Assert.Equal(0, registry.Count);
        Assert.Equal(2, logs.Entries.Count);
    }

    [Fact]
    public async Task Production_ServerPairingCanReachRealHandler()
    {
        var ready = new RecordingReadyHandler();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .ConfigureServices(services => services.AddSingleton<IOplaReadyHandler>(ready)));
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        ready.HandleAction = call =>
        {
            Assert.True(registry.TryBindPlayer(call.ConnectionId, Guid.NewGuid()));
            return Task.CompletedTask;
        };
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/opla"), _deadline.Token);
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY"));
        var call = await ready.ReadAsync(_deadline.Token);
        Assert.True(registry.TryGet(call.ConnectionId, out var connection));
        await SendAsync(socket, Encoding.UTF8.GetBytes("ROLL"));
        Assert.Equal("STATE 1 00000", await ReceiveTextAsync(socket));
        Assert.Equal(1, (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!.RollsUsed);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close,
            (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await connection.Completion.WaitAsync(_deadline.Token);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Route_DoesNotAcceptAdditionalPathSegments()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/ws/opla/extra", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, Registry.Count);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("CONNECT")]
    public async Task Upgrade_IsNotLimitedToGet(string method)
    {
        using var socket = await ConnectAsync(method: method);
        var connection = RegistryConnection(await ReadyAsync(socket));
        await CloseAsync(socket, connection);
        Assert.Equal(0, Registry.Count);
    }

    [Theory]
    [InlineData("READY", null)]
    [InlineData("READY 0012", "0012")]
    [InlineData("READY 0000", "0000")]
    [InlineData("READY 4821", "4821")]
    public async Task Ready_ForwardsExactCodeWithoutPairingOrReply(string text, string? code)
    {
        using var socket = await ConnectAsync();
        var call = await ReadyAsync(socket, text);
        var connection = RegistryConnection(call);
        Assert.Equal(code, call.Code);
        Assert.True(call.Token.CanBeCanceled);
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(Registry.TryGetPlayerId(call.ConnectionId, out var playerId));
        Assert.Equal(Guid.Empty, playerId);
        await CloseAsync(socket, connection);
        Assert.Single(_ready.Calls);
        Assert.Empty(_logs.Entries);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("ready 4821")]
    [InlineData(" READY")]
    [InlineData("READY ")]
    [InlineData("READY 123")]
    [InlineData("READY 12345")]
    [InlineData("READY abcd")]
    [InlineData("READY １２３４")]
    [InlineData("READY ١٢٣٤")]
    [InlineData("READY\t4821")]
    [InlineData("READY  4821")]
    [InlineData("READY 4821 extra")]
    [InlineData("ROLL ")]
    [InlineData("HOLD 9")]
    [InlineData("PAIR 4821")]
    [InlineData("JOINED Andreas")]
    [InlineData(" \t ")]
    [InlineData(" # debug")]
    public async Task InvalidReadyAndClientClaims_DoNotChangeStateOrEcho(string text)
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        var entry = await _logs.ReadAsync(_deadline.Token);
        var connection = Assert.Single(Registry.GetConnections());
        Assert.Equal(OplaConnectionState.Connected, connection.State);
        Assert.False(Registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.Empty(_ready.Calls);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.All(entry.Properties.Values.OfType<string>(), value => Assert.DoesNotContain("4821", value));
        await CloseAsync(socket, connection);
        Assert.Single(_logs.Entries);
    }

    [Fact]
    public async Task FragmentedReady_IsProcessedOnceAfterFinalFragment()
    {
        using var socket = await ConnectAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("READY 0012");
        for (int i = 0; i < bytes.Length; i++)
            await socket.SendAsync(new ArraySegment<byte>(bytes, i, 1), WebSocketMessageType.Text,
                i == bytes.Length - 1, _deadline.Token);
        var call = await _ready.ReadAsync(_deadline.Token);
        Assert.Equal("0012", call.Code);
        Assert.Single(_ready.Calls);
        await CloseAsync(socket, RegistryConnection(call));
    }

    [Fact]
    public async Task RepeatedReady_PreservesBindingAndAlwaysReachesHandler()
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        Guid playerId = Guid.NewGuid();
        Assert.True(Registry.TryBindPlayer(connection.ConnectionId, playerId));
        foreach (string text in new[] { "READY", "READY 0012", "READY 4821" })
        {
            Assert.Equal(connection.ConnectionId, (await ReadyAsync(socket, text)).ConnectionId);
            Assert.Equal(OplaConnectionState.Paired, connection.State);
            Assert.True(Registry.TryGetPlayerId(connection.ConnectionId, out var actual));
            Assert.Equal(playerId, actual);
        }
        Assert.Equal(1, Registry.Count);
        Assert.Equal(4, _ready.Calls.Count);
        Assert.Empty(_logs.Entries);
        await CloseAsync(socket, connection);
    }

    public static IEnumerable<object[]> CodePointBoundaryCases()
    {
        yield return new object[] { new string('x', 64), 64, true };
        yield return new object[] { new string('x', 65), 65, false };
        yield return new object[] { new string('ø', 64), 64, true };
        yield return new object[] { new string('ø', 65), 65, false };
        yield return new object[] { string.Concat(Enumerable.Repeat("🎲", 64)), 64, true };
        yield return new object[] { string.Concat(Enumerable.Repeat("🎲", 63)) + "ab", 65, false };
        yield return new object[] { string.Concat(Enumerable.Repeat("e\u0301", 32)), 64, true };
        yield return new object[] { string.Concat(Enumerable.Repeat("e\u0301", 32)) + "e", 65, false };
        yield return new object[] { "#" + new string('x', 64), 65, false };
        yield return new object[] { "#" + new string('x', 63) + "\n", 65, false };
    }

    [Theory]
    [MemberData(nameof(CodePointBoundaryCases))]
    public async Task CodePointLimit_CountsUnicodeScalarsBeforeFormatAndAccess(string text, int count, bool allowed)
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        if (allowed)
        {
            var entry = await _logs.ReadAsync(_deadline.Token);
            Assert.Contains("READY eller parring mangler", entry.Message);
            await CloseAsync(socket, connection);
        }
        else
            await AssertClosedAsync(socket, connection, WebSocketCloseStatus.MessageTooBig);
        var warning = Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(count, warning.Properties["Length"]);
        if (!allowed) Assert.Equal(64, warning.Properties["MaxCodePoints"]);
        Assert.Equal(0, Registry.Count);
    }

    [Fact]
    public async Task FragmentedUtf8And256Bytes_PreserveCodePointsWithEmptyFinalFragment()
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("🎲", 64)));
        for (int i = 0; i < bytes.Length; i++)
            await socket.SendAsync(new ArraySegment<byte>(bytes, i, 1), WebSocketMessageType.Text,
                endOfMessage: false, _deadline.Token);
        await SendAsync(socket, Array.Empty<byte>());
        var entry = await _logs.ReadAsync(_deadline.Token);
        Assert.Equal(64, entry.Properties["Length"]);
        Assert.Contains("READY eller parring mangler", entry.Message);
        await CloseAsync(socket, connection);
        Assert.Single(_logs.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("ROLL\nHOLD 1")]
    [InlineData("ROLL\n")]
    [InlineData("ROLL\r")]
    [InlineData("# debug\nROLL")]
    [InlineData("# debug\r")]
    [InlineData("READY 4821\nHOLD 1")]
    public async Task InvalidLine_LogsOnceWithoutReplyAndNextReadyWorks(string text)
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        var entry = await _logs.ReadAsync(_deadline.Token);
        var connection = RegistryConnection(await ReadyAsync(socket));
        await CloseAsync(socket, connection);
        Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(connection.ConnectionId, entry.Properties["ConnectionId"]);
        Assert.Equal(text.Length == 0 ? "Tom tekst" : "Linjeskift i teksten", entry.Properties["Reason"]);
        Assert.DoesNotContain("READY 4821", entry.Message);
        Assert.All(entry.Properties.Values.OfType<string>(), value => Assert.DoesNotContain("4821", value));
    }

    [Theory]
    [InlineData("#")]
    [InlineData("# debug")]
    [InlineData("# ROLL")]
    [InlineData("# READY 4821")]
    public async Task Debug_IsIgnoredWithoutReplyOrWarning(string text)
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        await CloseAsync(socket, RegistryConnection(await ReadyAsync(socket)));
        Assert.Empty(_logs.Entries);
        Assert.Single(_ready.Calls);
    }

    [Theory]
    [InlineData("READY", true, 0)]
    [InlineData("ready", false, 19)]
    [InlineData("ukendt", false, 19)]
    [InlineData("# debug", false, 0)]
    [InlineData("", false, 19)]
    [InlineData("ROLL\nHOLD 1", false, 19)]
    public async Task AllCompleteTexts_CountTowardsRate(string text, bool recordsReady, int otherWarnings)
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        for (int i = 0; i < 19; i++)
        {
            if (recordsReady) await ReadyAsync(socket, text);
            else await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        }
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY"));
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.PolicyViolation);
        Assert.Equal(otherWarnings + 1, _logs.Entries.Count);
        var warning = _logs.Entries.Last();
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(connection.ConnectionId, warning.Properties["ConnectionId"]);
        Assert.Equal(21, warning.Properties["Count"]);
        Assert.Equal(recordsReady ? 20 : 1, _ready.Calls.Count);
        Assert.Equal(0, Registry.Count);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("length")]
    [InlineData("newline")]
    [InlineData("debug")]
    public async Task RateViolation_PrecedesUtf8LengthFormatAndDebug(string kind)
    {
        using var socket = await ConnectAsync();
        OplaConnection? connection = null;
        for (int i = 0; i < 20; i++) connection = RegistryConnection(await ReadyAsync(socket));
        byte[] bytes = kind switch
        {
            "utf8" => new byte[] { 0xc3, 0x28 },
            "length" => Encoding.UTF8.GetBytes(new string('x', 65)),
            "newline" => Encoding.UTF8.GetBytes("# READY 4821\nROLL"),
            _ => Encoding.UTF8.GetBytes("# debug")
        };
        await SendAsync(socket, bytes);
        await AssertClosedAsync(socket, connection!, WebSocketCloseStatus.PolicyViolation);
        Assert.Equal(21, Assert.Single(_logs.Entries).Properties["Count"]);
        Assert.Equal(20, _ready.Calls.Count);
    }

    [Fact]
    public async Task RateWindow_ExpiresAtExactlyOneSecond()
    {
        using var socket = await ConnectAsync();
        for (int i = 0; i < 20; i++) await ReadyAsync(socket);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await CloseAsync(socket, RegistryConnection(await ReadyAsync(socket, "READY 4821")));
        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task ManyFragments_CountOnlyOnceIncludingEmptyFinalFragment()
    {
        using var socket = await ConnectAsync();
        for (int i = 0; i < 19; i++) await ReadyAsync(socket);
        var connection = Assert.Single(Registry.GetConnections());
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 64));
        for (int i = 0; i < bytes.Length; i++)
            await socket.SendAsync(new ArraySegment<byte>(bytes, i, 1), WebSocketMessageType.Text,
                endOfMessage: false, _deadline.Token);
        await SendAsync(socket, Array.Empty<byte>());
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY"));
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.PolicyViolation);
        Assert.Equal(2, _logs.Entries.Count); // Besked 20 afvises på adgang; 21 afvises på rate.
        Assert.Equal(21, _logs.Entries.Last().Properties["Count"]);
        Assert.Equal(19, _ready.Calls.Count);
    }

    [Fact]
    public async Task OutboundMessages_DoNotConsumeReceiveQuota()
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(await Registry.SendAsync(connection.ConnectionId, "server", _deadline.Token));
            Assert.Equal("server", await ReceiveTextAsync(socket));
        }
        for (int i = 0; i < 19; i++) await ReadyAsync(socket);
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY"));
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.PolicyViolation);
        Assert.Single(_logs.Entries);
    }

    [Fact]
    public async Task RateOverflow_OnlyClosesSenderAndReplacementStartsFresh()
    {
        using var first = await ConnectAsync();
        var firstConnection = RegistryConnection(await ReadyAsync(first));
        using var second = await ConnectAsync();
        var secondConnection = RegistryConnection(await ReadyAsync(second));
        for (int i = 0; i < 19; i++) await ReadyAsync(first);
        await SendAsync(first, Encoding.UTF8.GetBytes("READY"));
        await AssertClosedAsync(first, firstConnection, WebSocketCloseStatus.PolicyViolation);
        Assert.Equal(1, Registry.Count);
        await ReadyAsync(second, "READY 0012");
        using var replacement = await ConnectAsync();
        for (int i = 0; i < 20; i++) await ReadyAsync(replacement, "READY 4821");
        var replacementConnection = Registry.GetConnections().Single(c => c.ConnectionId != secondConnection.ConnectionId);
        Assert.NotEqual(firstConnection.ConnectionId, replacementConnection.ConnectionId);
        Assert.False(Registry.Remove(firstConnection.ConnectionId));
        Assert.Equal(2, Registry.Count);
        await CloseAsync(replacement, replacementConnection);
        await CloseAsync(second, secondConnection);
        Assert.Equal(0, Registry.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PayloadByte257_ClosesEvenWithoutFinalFragment(bool complete)
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        await SendAsync(socket, Encoding.UTF8.GetBytes(new string('x', 256)), endOfMessage: false);
        await SendAsync(socket, new[] { (byte)'x' }, endOfMessage: complete);
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.MessageTooBig);
        Assert.Equal(0, Registry.Count);
    }

    [Fact]
    public async Task BinaryMessage_ClosesConnection()
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        await socket.SendAsync(new ArraySegment<byte>(new byte[] { 1, 2, 3 }), WebSocketMessageType.Binary, true, _deadline.Token);
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.InvalidMessageType);
    }

    [Fact]
    public async Task InvalidUtf8_ClosesInsteadOfReplacingCharacters()
    {
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        await SendAsync(socket, new byte[] { 0xc3, 0x28 });
        await AssertClosedAsync(socket, connection, WebSocketCloseStatus.InvalidPayloadData);
    }

    [Fact]
    public async Task TwoConnections_KeepTheirIdentityAndRecipientsSeparate()
    {
        using var first = await ConnectAsync();
        var firstConnection = RegistryConnection(await ReadyAsync(first));
        using var second = await ConnectAsync();
        var secondConnection = RegistryConnection(await ReadyAsync(second));
        Assert.NotEqual(firstConnection.ConnectionId, secondConnection.ConnectionId);
        await Task.WhenAll(Registry.SendAsync(firstConnection.ConnectionId, "første", _deadline.Token),
            Registry.SendAsync(secondConnection.ConnectionId, "anden", _deadline.Token));
        Assert.Equal("første", await ReceiveTextAsync(first));
        Assert.Equal("anden", await ReceiveTextAsync(second));
        await CloseAsync(first, firstConnection);
        Assert.True(await Registry.SendAsync(secondConnection.ConnectionId, "stadig anden", _deadline.Token));
        Assert.Equal("stadig anden", await ReceiveTextAsync(second));
        await CloseAsync(second, secondConnection);
        Assert.Equal(0, Registry.Count);
    }

    [Fact]
    public async Task TwelveConnections_RejectThirteenthAndReuseReleasedSlot()
    {
        var sockets = new List<WebSocket>();
        var connections = new List<OplaConnection>();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var socket = await ConnectAsync();
                sockets.Add(socket);
                connections.Add(RegistryConnection(await ReadyAsync(socket)));
            }
            Assert.Equal(12, Registry.Count);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync());
            Assert.Contains("503", rejected.Message);
            await CloseAsync(sockets[0], connections[0]);
            using var replacement = await ConnectAsync();
            var replacementConnection = RegistryConnection(await ReadyAsync(replacement));
            Assert.False(Registry.Remove(connections[0].ConnectionId));
            Assert.True(Registry.TryGet(replacementConnection.ConnectionId, out _));
            Assert.Equal(12, Registry.Count);
            await CloseAsync(replacement, replacementConnection);
            for (int i = 1; i < sockets.Count; i++) await CloseAsync(sockets[i], connections[i]);
            Assert.Equal(0, Registry.Count);
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    [Theory]
    [InlineData("ROLL", false)]
    [InlineData("HOLD 1", false)]
    [InlineData("ROLL", true)]
    [InlineData("HOLD 5", true)]
    [InlineData("PAIR 4821", true)]
    [InlineData("JOINED Andreas", true)]
    public async Task UnpairedCommand_LogsOnceAndDoesNotChangeGame(string command, bool sendReady)
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        using var rolled = await client.PostAsync("/api/game/roll", null, _deadline.Token);
        rolled.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        using var socket = await ConnectAsync();
        if (sendReady) await ReadyAsync(socket, "READY 4821");
        await SendAsync(socket, Encoding.UTF8.GetBytes(command));
        var warning = await _logs.ReadAsync(_deadline.Token);
        var connection = Assert.Single(Registry.GetConnections());
        Assert.Equal(sendReady ? OplaConnectionState.Ready : OplaConnectionState.Connected, connection.State);
        Assert.False(Registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.Contains("READY eller parring mangler", warning.Message);
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        await CloseAsync(socket, connection);
        Assert.Single(_logs.Entries);
    }

    [Fact]
    public async Task Ready_DefaultLobbyIssuesCodeWithoutCreatingGame()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        Assert.Same(factory.Services.GetRequiredService<LobbySession>(), factory.Services.GetRequiredService<IOplaReadyHandler>());
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/opla"), _deadline.Token);
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY 0012"));
        Assert.Matches("^PAIR [0-9]{4}$", await ReceiveTextAsync(socket));
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var connection = Assert.Single(registry.GetConnections());
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(registry.TryGetPlayerId(connection.ConnectionId, out _));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await connection.Completion.WaitAsync(_deadline.Token);
        using var missing = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("HOLD 1")]
    [InlineData("READY 123")]
    [InlineData("ukendt")]
    public async Task ServerPairedCommand_UsesHandlerWithoutTransportWarning(string command)
    {
        Guid playerId = Guid.NewGuid();
        _ready.HandleAction = call =>
        {
            Assert.True(Registry.TryBindPlayer(call.ConnectionId, playerId));
            return Task.CompletedTask;
        };
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        using var socket = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(socket));
        Assert.True(Registry.TryGetPlayerId(connection.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
        await SendAsync(socket, Encoding.UTF8.GetBytes(command));
        if (command == "ROLL")
        {
            Assert.Equal("STATE 1 00000", await ReceiveTextAsync(socket));
            Assert.Empty(_handlerLogs.Entries);
        }
        else
        {
            var warning = await _handlerLogs.ReadAsync(_deadline.Token);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
            Assert.Single(_handlerLogs.Entries);
        }
        await CloseAsync(socket, connection);
        Assert.Empty(_logs.Entries);
        Assert.False(Registry.TryGetPlayerId(connection.ConnectionId, out _));
    }

    [Fact]
    public async Task RecordingReadyHandler_DoesNotRecognizeCodeAndOldCleanupKeepsExplicitNewBinding()
    {
        Guid playerId = Guid.NewGuid();
        using var old = await ConnectAsync();
        var oldConnection = RegistryConnection(await ReadyAsync(old, "READY 0012"));
        Assert.True(Registry.TryBindPlayer(oldConnection.ConnectionId, playerId));
        using var current = await ConnectAsync();
        var connection = RegistryConnection(await ReadyAsync(current, "READY 0012"));
        Assert.NotEqual(oldConnection.ConnectionId, connection.ConnectionId);
        Assert.Equal(OplaConnectionState.Ready, connection.State);
        Assert.False(Registry.TryGetPlayerId(connection.ConnectionId, out _));
        Assert.True(Registry.TryBindPlayer(connection.ConnectionId, playerId));
        await CloseAsync(old, oldConnection);
        Assert.False(Registry.Remove(oldConnection.ConnectionId));
        Assert.True(Registry.TryGetPlayerId(connection.ConnectionId, out var actual));
        Assert.Equal(playerId, actual);
        await CloseAsync(current, connection);
    }

    [Fact]
    public async Task ExistingSpike_StillReturnsItsOwnPairTestCode()
    {
        using var socket = await ConnectAsync("/ws/spike");
        await SendAsync(socket, Encoding.UTF8.GetBytes("READY"));
        Assert.Equal("PAIR 1234", await ReceiveTextAsync(socket));
        Assert.Equal(0, Registry.Count);
        // Spikens eksisterende disposal har intet lukkehåndtryk.
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test afsluttet", _deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token));
    }

    private async Task<WebSocket> ConnectAsync(string path = "/ws/opla", string method = "GET")
    {
        using var client = _factory.CreateClient();
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Method = method;
        return await wsClient.ConnectAsync(new Uri("ws://localhost" + path), _deadline.Token);
    }

    private async Task<RecordingReadyHandler.Call> ReadyAsync(WebSocket socket, string text = "READY")
    {
        await SendAsync(socket, Encoding.UTF8.GetBytes(text));
        return await _ready.ReadAsync(_deadline.Token);
    }

    private OplaConnection RegistryConnection(RecordingReadyHandler.Call call)
    {
        Assert.True(Registry.TryGet(call.ConnectionId, out var connection));
        return connection;
    }

    private Task SendAsync(WebSocket socket, byte[] bytes, bool endOfMessage = true) =>
        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage, _deadline.Token);

    private async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        byte[] buffer = new byte[512];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private async Task CloseAsync(WebSocket socket, OplaConnection connection)
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test afsluttet", _deadline.Token);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[512]), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        await connection.Completion.WaitAsync(_deadline.Token);
        Assert.Equal(OplaConnectionState.Closed, connection.State);
        Assert.False(Registry.TryGet(connection.ConnectionId, out _));
    }

    private async Task AssertClosedAsync(WebSocket socket, OplaConnection connection, WebSocketCloseStatus expected)
    {
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[512]), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(expected, result.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "modtaget", _deadline.Token);
        await connection.Completion.WaitAsync(_deadline.Token);
        Assert.Equal(OplaConnectionState.Closed, connection.State);
        Assert.False(Registry.TryGetPlayerId(connection.ConnectionId, out _));
    }

    public void Dispose()
    {
        _deadline.Cancel();
        _factory.Dispose();
        _deadline.Dispose();
    }
}
