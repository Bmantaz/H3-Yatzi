using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester lobbykontrakten gennem appens rigtige HTTP- og WebSocket-pipeline.
/// Styret kodekandidat gør fejlcases reproducerbare; parring sker via HTTP-join.
/// </summary>
public partial class LobbyApiTests : IDisposable
{
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
    private readonly RecordingLoggerProvider _logs = new(nameof(OplaWebSocketEndpoints));
    private readonly RecordingLoggerProvider _lobbyLogs = new(typeof(LobbySession).FullName!);
    private readonly RecordingLoggerProvider _handlerLogs = new(typeof(OplaMessageHandler).FullName!);
    private readonly List<WebSocket> _sockets = new();
    private readonly WebApplicationFactory<Program> _factory;

    public LobbyApiTests() => _factory = CreateFactory("Test");

    [Fact]
    public async Task Get_EmptyLobbyHasOnlyContractFieldsAndDoesNotCreateGame()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/lobby", _deadline.Token);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(new[] { "players", "started" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Empty(document.RootElement.GetProperty("players").EnumerateArray());
        Assert.False(document.RootElement.GetProperty("started").GetBoolean());
        using var game = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, game.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task ReadyJoinAndRepeatedReady_UseSameLobbyInEveryEnvironment(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Assert.Same(factory.Services.GetRequiredService<LobbySession>(), factory.Services.GetRequiredService<IOplaReadyHandler>());
        var socket = await ConnectAsync(factory);
        await SendAsync(socket, "READY");
        Assert.Equal("PAIR 0000", await ReceiveAsync(socket));
        var connection = Assert.Single(registry.GetConnections());
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "  Andreas  ", code = "0000", extra = true }, _deadline.Token);
        using var json = await ReadJsonAsync(joined);
        var player = Assert.Single(json.RootElement.GetProperty("players").EnumerateArray());
        Assert.Equal(new[] { "id", "name", "connected" }, player.EnumerateObject().Select(property => property.Name));
        Guid playerId = player.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, playerId);
        Assert.Equal("Andreas", player.GetProperty("name").GetString());
        Assert.True(player.GetProperty("connected").GetBoolean());
        Assert.Equal("JOINED Andreas", await ReceiveAsync(socket));
        Assert.True(registry.TryGetPlayerId(connection.ConnectionId, out var binding));
        Assert.Equal(playerId, binding);
        Assert.Equal(OplaConnectionState.Paired, connection.State);

        await SendAsync(socket, "READY 9999");
        Assert.Equal("PAIR 0000", await ReceiveAsync(socket));
        Assert.Equal("JOINED Andreas", await ReceiveAsync(socket));
        var state = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
        Assert.Equal(playerId, Assert.Single(state!.Players).Id);
        Assert.False(state.Started);
        using var game = await client.GetAsync("/api/game", _deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, game.StatusCode);

        await CloseAsync(socket, connection);
        Assert.False(Assert.Single((await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players).Connected);
        using var duplicate = await client.PostAsJsonAsync("/api/lobby/join", new { name = "Andreas", code = "0000" }, _deadline.Token);
        await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "Koden er allerede tilmeldt.");

        // Genforbindelse genfinder samme spiller uden et nyt HTTP-join.
        var replacement = await ConnectAsync(factory);
        await SendAsync(replacement, "READY 0000");
        Assert.Equal("PAIR 0000", await ReceiveAsync(replacement));
        Assert.Equal("JOINED Andreas", await ReceiveAsync(replacement));
        var newConnection = Assert.Single(registry.GetConnections());
        Assert.True(registry.TryGetPlayerId(newConnection.ConnectionId, out var reconnectedId));
        Assert.Equal(playerId, reconnectedId);
        Assert.Equal(playerId, Assert.Single((await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players).Id);
        await CloseAsync(replacement, newConnection);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task OpenConnectionTakeover_ClosesOldEndpointAndKeepsNewPlayerInEveryEnvironment(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Selvstændigt spil" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        using var rolled = await client.PostAsync("/api/game/roll", null, _deadline.Token);
        rolled.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        var oldSocket = await ConnectAsync(factory);
        await SendAsync(oldSocket, "READY");
        Assert.Equal("PAIR 0000", await ReceiveAsync(oldSocket));
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        Assert.Equal("JOINED A", await ReceiveAsync(oldSocket));
        var old = Assert.Single(registry.GetConnections());
        var beforeLobby = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);

        var currentSocket = await ConnectAsync(factory);
        await SendAsync(currentSocket, "READY 0000");
        // Den gamle klient har endnu ikke svaret på lukkehåndtrykket.
        Assert.Equal("PAIR 0000", await ReceiveAsync(currentSocket));
        Assert.Equal("JOINED A", await ReceiveAsync(currentSocket));
        var current = Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != old.ConnectionId);
        Assert.True(old.IsClosing);
        Assert.False(registry.TryGetPlayerId(old.ConnectionId, out _));
        Assert.True(registry.TryGetPlayerId(current.ConnectionId, out var id));
        Assert.Equal(beforeLobby!.Players[0].Id, id);
        Assert.Equal(WebSocketMessageType.Close,
            (await oldSocket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, oldSocket.CloseStatus);
        await oldSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        await old.Completion.WaitAsync(_deadline.Token);
        Assert.False(registry.Remove(old.ConnectionId));
        Assert.Single(registry.GetConnections());
        Assert.Equal(beforeLobby.Players, (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!.Players);

        await SendAsync(currentSocket, "READY 9999");
        Assert.Equal("PAIR 0000", await ReceiveAsync(currentSocket));
        Assert.Equal("JOINED A", await ReceiveAsync(currentSocket));
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(currentSocket, command);
            // Synkronisér på den nye afsenders id, så dens input faktisk er behandlet.
            RecordingLoggerProvider.Entry rejection;
            do { rejection = await _lobbyLogs.ReadAsync(_deadline.Token); }
            while (!Equals(rejection.Properties.GetValueOrDefault("ConnectionId"), current.ConnectionId));
            Assert.Contains("Lobbyspillet er ikke klar", rejection.Message);
        }
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        await CloseAsync(currentSocket, current);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task UnusedCodeTakeover_CanJoinThroughHttpOnNewConnection()
    {
        using var client = _factory.CreateClient();
        var (oldSocket, old, code) = await ReadyAsync();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var currentSocket = await ConnectAsync(_factory);
        await SendAsync(currentSocket, "READY " + code);
        Assert.Equal("PAIR " + code, await ReceiveAsync(currentSocket));
        var current = Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != old.ConnectionId);
        Assert.False(registry.TryGetPlayerId(current.ConnectionId, out _));
        Assert.Equal(WebSocketMessageType.Close,
            (await oldSocket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await oldSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        await old.Completion.WaitAsync(_deadline.Token);
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        Assert.Equal("JOINED A", await ReceiveAsync(currentSocket));
        Assert.True(registry.TryGetPlayerId(current.ConnectionId, out _));
        await CloseAsync(currentSocket, current);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (string json in new[] { "", "{", "null", "[]", "true", "12", "\"text\"" })
            yield return [json, "Requesten skal indeholde et gyldigt JSON-objekt."];
        // JSON kan parses, selv om en tekst indeholder en uparret UTF-16-surrogat.
        // GetString afviser først senere denne tekst; HTTP skal stadig give dansk 400.
        foreach (string json in new[]
        {
            """{"name":"\uD800","code":"0000"}""",
            """{"name":"\uDC00","code":"0000"}""",
            """{"name":"A","code":"\uD800"}""",
            """{"name":"A","code":"\uDC00"}"""
        }) yield return [json, "Requesten skal indeholde et gyldigt JSON-objekt."];
        foreach (string json in new[] { "{}", "{\"Name\":\"A\",\"code\":\"0000\"}", "{\"name\":null}", "{\"name\":3}", "{\"name\":[]}", "{\"name\":{}}" })
            yield return [json, "Navnet skal være tekst."];
        foreach (string name in new[] { "", "  ", new string('A', 21), "  " + new string('A', 21) + "  ", string.Concat(Enumerable.Repeat("😀", 11)) })
            yield return [JsonSerializer.Serialize(new { name, code = "0000" }), "Navnet skal indeholde mellem 1 og 20 tegn."];
        foreach (string name in new[] { "\nA", "A\r", "A\tB", "A\0B", "A\u007fB", "A\u0085" })
            yield return [JsonSerializer.Serialize(new { name, code = "0000" }), "Navnet må ikke indeholde kontroltegn."];
        foreach (string json in new[] { "{\"name\":\"A\"}", "{\"name\":\"A\",\"code\":null}", "{\"name\":\"A\",\"code\":12}", "{\"name\":\"A\",\"code\":[]}", "{\"name\":\"A\",\"code\":{}}" })
            yield return [json, "Koden skal være tekst."];
        foreach (string code in new[] { "", "123", "12345", " 0000", "0000 ", "00a0", "００００", "٠٠٠٠", "00\n0" })
            yield return [JsonSerializer.Serialize(new { name = "A", code }), "Koden skal bestå af præcis fire ASCII-cifre."];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Join_InvalidInputHasDanishJsonErrorAndLeavesCodeAvailable(string body, string message)
    {
        using var client = _factory.CreateClient();
        var (socket, connection, _) = await ReadyAsync();
        using var response = await client.PostAsync("/api/lobby/join", new StringContent(body, Encoding.UTF8, "application/json"), _deadline.Token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, message);
        Assert.Empty(_factory.Services.GetRequiredService<LobbySession>().GetState().Players);
        Assert.False(_factory.Services.GetRequiredService<OplaConnectionRegistry>().TryGetPlayerId(connection.ConnectionId, out _));
        using var accepted = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
        accepted.EnsureSuccessStatusCode();
        Assert.Equal("JOINED A", await ReceiveAsync(socket));
        await CloseAsync(socket, connection);
    }

    [Theory]
    [InlineData(0, "0000")]
    [InlineData(12, "0012")]
    public async Task Join_PreservesLeadingZeroCodeAsText(int candidate, string code)
    {
        using var factory = CreateFactory("Test", candidate);
        using var client = factory.CreateClient();
        var socket = await ConnectAsync(factory);
        await SendAsync(socket, "READY");
        Assert.Equal("PAIR " + code, await ReceiveAsync(socket));
        using var response = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("JOINED A", await ReceiveAsync(socket));
        await CloseAsync(socket, Assert.Single(factory.Services.GetRequiredService<OplaConnectionRegistry>().GetConnections()));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("  ABCDEFGHIJKLMNOPQRST  ")]
    [InlineData("😀😀😀😀😀😀😀😀😀😀")]
    public async Task Join_UsesTrimmedUtf16LengthAndAllowsUnicode(string name)
    {
        using var client = _factory.CreateClient();
        var (socket, connection, code) = await ReadyAsync();
        using var response = await client.PostAsJsonAsync("/api/lobby/join", new { name, code }, _deadline.Token);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(name.Trim(), Assert.Single(json.RootElement.GetProperty("players").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("JOINED " + name.Trim(), await ReceiveAsync(socket));
        await CloseAsync(socket, connection);
    }

    [Fact]
    public async Task Join_UnknownAndDisconnectedUnusedCodesReturn404()
    {
        using var client = _factory.CreateClient();
        using var unknown = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0012" }, _deadline.Token);
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "Koden er ukendt eller ikke længere gyldig.");
        var (socket, connection, code) = await ReadyAsync();
        await CloseAsync(socket, connection);
        using var expired = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        await AssertErrorAsync(expired, HttpStatusCode.NotFound, "Koden er ukendt eller ikke længere gyldig.");
        Assert.Empty(_factory.Services.GetRequiredService<LobbySession>().GetState().Players);
    }

    [Fact]
    public async Task ConcurrentHttpJoin_SameCodeReturnsOneSuccessAndOneConflict()
    {
        using var client = _factory.CreateClient();
        var (socket, connection, code) = await ReadyAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            client.PostAsJsonAsync("/api/lobby/join", new { name = $"Spiller {i}", code }, _deadline.Token)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await AssertErrorAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict, "Koden er allerede tilmeldt.");
            Assert.StartsWith("JOINED Spiller ", await ReceiveAsync(socket));
            Assert.Single(_factory.Services.GetRequiredService<LobbySession>().GetState().Players);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await CloseAsync(socket, connection);
    }

    [Fact]
    public async Task Join_SeventhPlayerReturns409EvenWhenOthersDisconnected()
    {
        using var client = _factory.CreateClient();
        for (int i = 0; i < 6; i++)
        {
            var (socket, connection, code) = await ReadyAsync();
            using var response = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
            response.EnsureSuccessStatusCode();
            Assert.Equal("JOINED A", await ReceiveAsync(socket));
            await CloseAsync(socket, connection);
        }
        var (seventh, seventhConnection, seventhCode) = await ReadyAsync();
        using var rejected = await client.PostAsJsonAsync("/api/lobby/join", new { name = "B", code = seventhCode }, _deadline.Token);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "Lobbyen er fuld. Der kan højst være seks spillere.");
        var state = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
        Assert.Equal(6, state!.Players.Length);
        Assert.Equal(6, state.Players.Select(player => player.Id).Distinct().Count());
        Assert.All(state.Players, player => Assert.False(player.Connected));
        await CloseAsync(seventh, seventhConnection);
    }

    [Fact]
    public async Task JoinedOpla_CannotChangeExistingSinglePlayerGameBeforeStart()
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Selvstændigt spil" }, _deadline.Token);
        created.EnsureSuccessStatusCode();
        using var rolled = await client.PostAsync("/api/game/roll", null, _deadline.Token);
        rolled.EnsureSuccessStatusCode();
        string before = await client.GetStringAsync("/api/game", _deadline.Token);
        var (socket, connection, code) = await ReadyAsync();
        using var joined = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        joined.EnsureSuccessStatusCode();
        Assert.Equal("JOINED A", await ReceiveAsync(socket));
        foreach (string command in new[] { "ROLL", "HOLD 1" })
        {
            await SendAsync(socket, command);
            Assert.Contains("Lobbyspillet er ikke klar", (await _lobbyLogs.ReadAsync(_deadline.Token)).Message);
        }
        Assert.Equal(before, await client.GetStringAsync("/api/game", _deadline.Token));
        await CloseAsync(socket, connection); // Første resterende frame skal være Close, aldrig STATE.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpJoin_SendFailureStillReturns200WithDisconnectedPlayer(bool ioFailure)
    {
        using var client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var session = _factory.Services.GetRequiredService<LobbySession>();
        Assert.True(registry.TryReserve(out Guid id));
        using var socket = new ControlledWebSocket();
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        await session.HandleReadyAsync(id, null, _deadline.Token);
        socket.SendAction = _ => { if (ioFailure) throw new IOException(); throw new WebSocketException(); };
        using var response = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
        using var json = await ReadJsonAsync(response);
        Assert.False(Assert.Single(json.RootElement.GetProperty("players").EnumerateArray()).GetProperty("connected").GetBoolean());
        Assert.Equal(2, socket.Sent.Count);
        Assert.True(connection.IsClosing);
        registry.Remove(id);
    }

    [Fact]
    public async Task HttpJoin_WaitsForJoinedWhileGetAndDuplicateJoinRemainAvailable()
    {
        using var client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var session = _factory.Services.GetRequiredService<LobbySession>();
        Assert.True(registry.TryReserve(out Guid id));
        using var socket = new ControlledWebSocket();
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        await session.HandleReadyAsync(id, null, _deadline.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var join = client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
        try
        {
            await entered.Task.WaitAsync(_deadline.Token);
            Assert.False(join.IsCompleted);
            var state = await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token);
            Assert.True(Assert.Single(state!.Players).Connected);
            using var duplicate = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
            await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "Koden er allerede tilmeldt.");
        }
        finally { release.TrySetResult(); }
        using var accepted = await join.WaitAsync(_deadline.Token);
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(2, socket.Sent.Count);
        registry.Remove(id);
    }

    [Fact]
    public async Task HttpJoin_FailedBindingReturns409WithoutChangingExistingBinding()
    {
        using var client = _factory.CreateClient();
        var (socket, connection, code) = await ReadyAsync();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Guid existingId = Guid.NewGuid();
        Assert.True(registry.TryBindPlayer(connection.ConnectionId, existingId));
        using var rejected = await client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code }, _deadline.Token);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "Oplà-forbindelsen er ikke længere tilgængelig.");
        Assert.Empty(_factory.Services.GetRequiredService<LobbySession>().GetState().Players);
        Assert.True(registry.TryGetPlayerId(connection.ConnectionId, out var binding));
        Assert.Equal(existingId, binding);
        await CloseAsync(socket, connection);
    }

    [Fact]
    public async Task DelayedHttpJoinFailure_AfterTakeoverCannotCloseNewConnection()
    {
        using var client = _factory.CreateClient();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var session = _factory.Services.GetRequiredService<LobbySession>();
        Assert.True(registry.TryReserve(out Guid oldId));
        using var oldSocket = new ControlledWebSocket();
        var old = new OplaConnection(oldId, oldSocket);
        Assert.True(registry.TryAttach(old));
        Assert.True(registry.TryMarkReady(oldId));
        await session.HandleReadyAsync(oldId, null, _deadline.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldSocket.SendAction = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new WebSocketException();
        };
        var join = client.PostAsJsonAsync("/api/lobby/join", new { name = "A", code = "0000" }, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        Guid playerId = Assert.Single(session.GetState().Players).Id;
        Assert.True(registry.TryReserve(out Guid currentId));
        using var currentSocket = new ControlledWebSocket();
        var current = new OplaConnection(currentId, currentSocket);
        Assert.True(registry.TryAttach(current));
        Assert.True(registry.TryMarkReady(currentId));
        Task reconnect = session.HandleReadyAsync(currentId, "0000", _deadline.Token);
        try
        {
            Assert.Equal(new[] { "PAIR 0000", "JOINED A" }, currentSocket.Sent.Select(Encoding.UTF8.GetString));
            Assert.True(registry.TryGetPlayerId(currentId, out var binding));
            Assert.Equal(playerId, binding);
            Assert.False(join.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await reconnect.WaitAsync(_deadline.Token);
        using var response = await join.WaitAsync(_deadline.Token);
        using var json = await ReadJsonAsync(response);
        var player = Assert.Single(json.RootElement.GetProperty("players").EnumerateArray());
        Assert.Equal(playerId, player.GetProperty("id").GetGuid());
        Assert.True(player.GetProperty("connected").GetBoolean());
        Assert.False(current.IsClosing);
        Assert.Equal(1, oldSocket.CloseCount);
        Assert.Equal(0, currentSocket.CloseCount);
        registry.Remove(oldId);
        registry.Remove(currentId);
    }

    private WebApplicationFactory<Program> CreateFactory(string environment, int candidate = 0) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment(environment)
            .ConfigureLogging(logging => logging.AddProvider(_logs).AddProvider(_lobbyLogs).AddProvider(_handlerLogs))
            .ConfigureServices(services => services.AddSingleton<LobbySession>(provider => new LobbySession(
                provider.GetRequiredService<OplaConnectionRegistry>(), provider.GetRequiredService<GameSession>(), () => candidate, provider.GetRequiredService<ILogger<LobbySession>>()))));

    private async Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/opla"), _deadline.Token);
        _sockets.Add(socket);
        return socket;
    }

    private async Task<(WebSocket Socket, OplaConnection Connection, string Code)> ReadyAsync()
    {
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var previousIds = registry.GetConnections().Select(connection => connection.ConnectionId).ToHashSet();
        var socket = await ConnectAsync(_factory);
        await SendAsync(socket, "READY");
        string pair = await ReceiveAsync(socket);
        Assert.Matches("^PAIR [0-9]{4}$", pair);
        return (socket, Assert.Single(registry.GetConnections(), connection => !previousIds.Contains(connection.ConnectionId)), pair[5..]);
    }

    private Task SendAsync(WebSocket socket, string text) => socket.SendAsync(
        new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, _deadline.Token);

    private async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private async Task CloseAsync(WebSocket socket, OplaConnection connection)
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close,
            (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token)).MessageType);
        await connection.Completion.WaitAsync(_deadline.Token);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string message)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("message", Assert.Single(json.RootElement.EnumerateObject()).Name);
        Assert.Equal(message, json.RootElement.GetProperty("message").GetString());
    }

    public void Dispose()
    {
        _deadline.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        _factory.Dispose();
        _deadline.Dispose();
    }
}
