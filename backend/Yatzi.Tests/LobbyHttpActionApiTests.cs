using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;
using Yatzi.Core;

namespace Yatzi.Tests;

/// <summary>HTTP-handlinger og virkelige/kontrollerede WebSocket-modtagere deler samme app og ordnede lobbykø.</summary>
public partial class LobbyApiTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task FullHttpGame_SendsExactlyStateWaitEndAndReadyRestoresFinalResult(int count)
    {
        using var client = _factory.CreateClient();
        var players = new List<(WebSocket Socket, OplaConnection Connection, string Code)>();
        for (int i = 0; i < count; i++) players.Add(await JoinOnFactoryAsync(_factory, client, "Alex"));
        var ids = _factory.Services.GetRequiredService<LobbySession>().GetState().Players;
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        var state = await GameStateAssertions.ReadHttpStateAsync(start, count);
        for (int i = 0; i < count; i++) Assert.Equal(i == 0 ? "STATE 0 00000" : "WAIT Alex", await ReceiveAsync(players[i].Socket));
        foreach (var (id, _) in GameStateAssertions.Categories)
        {
            for (int player = 0; player < count; player++)
            {
                using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", state, ids[player].Id, _deadline.Token);
                var rolled = await GameStateAssertions.ReadHttpStateAsync(roll, count);
                Assert.Equal("STATE 1 00000", await ReceiveAsync(players[player].Socket));
                using var hold = await GameHttpActions.PostAsync(client, "/api/game/hold/1", rolled, ids[player].Id, _deadline.Token);
                var held = await GameStateAssertions.ReadHttpStateAsync(hold, count);
                Assert.Equal("STATE 1 10000", await ReceiveAsync(players[player].Socket));
                using var chosen = await GameHttpActions.PostAsync(client, $"/api/game/strike/{id}", held, ids[player].Id, _deadline.Token);
                state = await GameStateAssertions.ReadHttpStateAsync(chosen, count);
                for (int recipient = 0; recipient < count; recipient++)
                    Assert.Equal(state.GameCompleted ? "END" : recipient == state.CurrentPlayer ? "STATE 0 00000" : "WAIT Alex",
                        await ReceiveAsync(players[recipient].Socket));
                if (state.TurnNumber == 2)
                {
                    await SendAsync(players[0].Socket, "READY");
                    Assert.Equal("PAIR " + players[0].Code, await ReceiveAsync(players[0].Socket));
                    Assert.Equal("JOINED Alex", await ReceiveAsync(players[0].Socket));
                    Assert.Equal(count == 1 ? "STATE 0 00000" : "WAIT Alex", await ReceiveAsync(players[0].Socket));
                }
            }
        }
        Assert.True(state.GameCompleted);
        Assert.Equal(Enumerable.Range(0, count), state.WinnerIndices);
        using var rejected = await GameHttpActions.PostAsync(client, "/api/game/roll", state, ids[^1].Id, _deadline.Token);
        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, "Spillet er afsluttet. Start et nyt spil for at fortsætte.");
        Assert.True(_factory.Services.GetRequiredService<LobbySession>().GetState().Started);
        GameStateAssertions.Equal(state, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        foreach (var player in players)
        {
            await SendAsync(player.Socket, "READY " + player.Code);
            Assert.Equal("PAIR " + player.Code, await ReceiveAsync(player.Socket));
            Assert.Equal("JOINED Alex", await ReceiveAsync(player.Socket));
            Assert.Equal("END", await ReceiveAsync(player.Socket));
            await CloseAsync(player.Socket, player.Connection);
        }
    }

    [Fact]
    public async Task DisconnectedActivePlayer_CanUseHttpReserveAndReconnectWithHeldState()
    {
        using var client = _factory.CreateClient();
        var first = await JoinOnFactoryAsync(_factory, client, "A");
        var second = await JoinOnFactoryAsync(_factory, client, "B");
        var ids = _factory.Services.GetRequiredService<LobbySession>().GetState().Players;
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        var state = await GameStateAssertions.ReadHttpStateAsync(start, 2);
        Assert.Equal("STATE 0 00000", await ReceiveAsync(first.Socket));
        Assert.Equal("WAIT A", await ReceiveAsync(second.Socket));
        await CloseAsync(first.Socket, first.Connection);
        using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", state, ids[0].Id, _deadline.Token);
        var rolled = await GameStateAssertions.ReadHttpStateAsync(roll, 2);
        using var hold = await GameHttpActions.PostAsync(client, "/api/game/hold/2", rolled, ids[0].Id, _deadline.Token);
        var held = await GameStateAssertions.ReadHttpStateAsync(hold, 2);
        Assert.Equal(0, held.CurrentPlayer);
        Assert.False(_factory.Services.GetRequiredService<LobbySession>().GetState().Players[0].Connected);
        var current = await ConnectAsync(_factory);
        await SendAsync(current, "READY " + first.Code);
        Assert.Equal("PAIR " + first.Code, await ReceiveAsync(current));
        Assert.Equal("JOINED A", await ReceiveAsync(current));
        Assert.Equal("STATE 1 01000", await ReceiveAsync(current));
        GameStateAssertions.Equal(held, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        using var score = await GameHttpActions.PostAsync(client, "/api/game/score/chance", held, ids[0].Id, _deadline.Token);
        var next = await GameStateAssertions.ReadHttpStateAsync(score, 2);
        Assert.Equal(held.Dice.Sum(), next.Players[0].Scorecard.Scores["chance"]);
        Assert.Equal("WAIT B", await ReceiveAsync(current));
        Assert.Equal("STATE 0 00000", await ReceiveAsync(second.Socket));
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        await CloseAsync(current, Assert.Single(registry.GetConnections(), connection => connection.ConnectionId != second.Connection.ConnectionId));
        await CloseAsync(second.Socket, second.Connection);
    }

    [Theory]
    [InlineData("roll", "websocket")]
    [InlineData("hold/1", "io")]
    [InlineData("score/chance", "websocket")]
    [InlineData("strike/chance", "io")]
    [InlineData("roll", "transport-cancel")]
    [InlineData("hold/1", "transport-cancel")]
    [InlineData("score/chance", "transport-cancel")]
    [InlineData("strike/chance", "transport-cancel")]
    [InlineData("roll", "closed")]
    [InlineData("hold/1", "closed")]
    [InlineData("score/chance", "closed")]
    [InlineData("strike/chance", "closed")]
    public async Task HttpDeliveryFailure_Preserves200AndOtherRecipients(string path, string failure)
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var second = await ControlledJoinedAsync(client, "B");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        await lobby.StartAsync(_deadline.Token);
        Guid id = lobby.GetState().Players[0].Id;
        if (path != "roll") Assert.Equal(GameActionStatus.Accepted, (await lobby.RollAsync(GameHttpActions.Context(game.GetState(), id), _deadline.Token)).Status);
        var before = game.GetState()!;
        int secondCount = second.Socket.Sent.Count;
        if (failure == "closed") await first.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "test");
        else first.Socket.SendAction = _ => failure switch
        {
            "io" => Task.FromException(new IOException()),
            "transport-cancel" => Task.FromException(new OperationCanceledException()),
            _ => Task.FromException(new WebSocketException())
        };
        using var response = await GameHttpActions.PostAsync(client, "/api/game/" + path, before, id, _deadline.Token);
        var accepted = await GameStateAssertions.ReadHttpStateAsync(response, 2);
        GameStateAssertions.Equal(accepted, game.GetState()!);
        bool field = path.StartsWith("score") || path.StartsWith("strike");
        Assert.Equal(field ? 1 : 0, accepted.CurrentPlayer);
        Assert.True(first.Connection.IsClosing);
        Assert.False(lobby.GetState().Players[0].Connected);
        Assert.Equal(secondCount + (field ? 1 : 0), second.Socket.Sent.Count);
        if (field) Assert.Equal("STATE 0 00000", SentLines(second.Socket)[^1]);
        Assert.Equal(1, first.Socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task BlockedHttpReply_OrdersOplaAndReadyAndRejectsOldChoiceAfterNextPlayerRolls()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var second = await ControlledJoinedAsync(client, "B");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        await lobby.StartAsync(_deadline.Token);
        var ids = lobby.GetState().Players;
        await lobby.RollAsync(GameHttpActions.Context(game.GetState(), ids[0].Id), _deadline.Token);
        var old = game.GetState()!;
        var entered = ActionSignal();
        var release = ActionSignal();
        var delivered = ActionSignal();
        second.Socket.SendAction = _ => { delivered.TrySetResult(); return Task.CompletedTask; };
        first.Socket.SendAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var score = GameHttpActions.PostAsync(client, "/api/game/score/chance", old, ids[0].Id, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        await delivered.Task.WaitAsync(_deadline.Token);
        Assert.Equal(1, game.GetState()!.CurrentPlayer);
        Assert.Equal("STATE 0 00000", SentLines(second.Socket)[^1]);
        Assert.True(await lobby.HandleGameInputAsync(second.Connection.ConnectionId, ids[1].Id, "ROLL",
            _factory.Services.GetRequiredService<OplaMessageHandler>(), _deadline.Token));
        Assert.Equal("STATE 1 00000", SentLines(second.Socket)[^1]);
        var current = game.GetState()!;
        using var stale = await GameHttpActions.PostAsync(client, "/api/game/strike/ones", old, ids[0].Id, _deadline.Token);
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "Turen er ikke længere aktuel. Hent spiltilstanden igen.");
        GameStateAssertions.Equal(current, game.GetState()!);
        var ready = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        Assert.False(ready.IsCompleted);
        release.TrySetResult();
        using var accepted = await score;
        var snapshot = await GameStateAssertions.ReadHttpStateAsync(accepted, 2);
        Assert.Equal(0, snapshot.RollsUsed);
        Assert.Equal(old.Dice.Sum(), snapshot.Players[0].Scorecard.Scores["chance"]);
        await ready;
        Assert.Equal(new[] { "WAIT B", "PAIR " + SentLines(first.Socket)[0][5..], "JOINED A", "WAIT B" }, SentLines(first.Socket).TakeLast(4));
        Assert.Equal(1, first.Socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task LateHttpSendFailureAfterTakeover_ClosesOnlyCapturedOldConnection()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var second = await ControlledJoinedAsync(client, "B");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        await lobby.StartAsync(_deadline.Token);
        var id = lobby.GetState().Players[0].Id;
        await lobby.RollAsync(GameHttpActions.Context(game.GetState(), id), _deadline.Token);
        var entered = ActionSignal();
        var release = ActionSignal();
        var delivered = ActionSignal();
        second.Socket.SendAction = _ => { delivered.TrySetResult(); return Task.CompletedTask; };
        first.Socket.SendAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new WebSocketException(); };
        var request = GameHttpActions.PostAsync(client, "/api/game/score/chance", game.GetState(), id, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        await delivered.Task.WaitAsync(_deadline.Token);
        var current = ActionConnection(registry);
        var reconnect = lobby.HandleReadyAsync(current.Connection.ConnectionId, SentLines(first.Socket)[0][5..], _deadline.Token);
        Assert.Equal(new[] { SentLines(first.Socket)[0], "JOINED A", "WAIT B" }, SentLines(current.Socket));
        Assert.Equal("STATE 0 00000", SentLines(second.Socket)[^1]);
        release.TrySetResult();
        await reconnect.WaitAsync(_deadline.Token);
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(current.Connection.IsClosing);
        Assert.Equal(0, current.Socket.CloseCount);
        Assert.True(registry.TryGetPlayerId(current.Connection.ConnectionId, out Guid currentId));
        Assert.Equal(id, currentId);
    }

    [Fact]
    public async Task CancelledHttpQueue_PreservesAcceptedTurnAndLaterReadyWaitsForEarlierBatch()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        await lobby.StartAsync(_deadline.Token);
        var entered = ActionSignal();
        var release = ActionSignal();
        first.Socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var ready = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var action = lobby.RollAsync(GameHttpActions.Context(game.GetState(), lobby.GetState().Players[0].Id), stopped.Token);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(1, game.GetState()!.RollsUsed);
        var later = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        Assert.False(later.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(ready, later).WaitAsync(_deadline.Token);
        Assert.Equal(new[] { SentLines(first.Socket)[0], "JOINED A", "STATE 1 00000" }, SentLines(first.Socket).TakeLast(3));
        Assert.False(first.Connection.IsClosing);
        using var cancelledBefore = new CancellationTokenSource();
        cancelledBefore.Cancel();
        int sent = first.Socket.Sent.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lobby.RollAsync(GameHttpActions.Context(game.GetState(), lobby.GetState().Players[0].Id), cancelledBefore.Token));
        Assert.Equal(sent, first.Socket.Sent.Count);
        Assert.Equal(1, game.GetState()!.RollsUsed);
    }

    [Fact]
    public async Task CancellationDuringHttpSend_PreservesAcceptedRollWithoutClosingOrReplaying()
    {
        using var client = _factory.CreateClient();
        var first = await ControlledJoinedAsync(client, "A");
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        await lobby.StartAsync(_deadline.Token);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var entered = ActionSignal();
        first.Socket.SendAction = token => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); };
        var action = lobby.RollAsync(GameHttpActions.Context(game.GetState(), lobby.GetState().Players[0].Id), stopped.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(1, game.GetState()!.RollsUsed);
        Assert.False(first.Connection.IsClosing);
        first.Socket.SendAction = null;
        await lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        Assert.Equal("STATE 1 00000", SentLines(first.Socket)[^1]);
        Assert.Equal(1, game.GetState()!.RollsUsed);
    }

    [Fact]
    public async Task DelayedJoin_ReturnsItsAcceptedIdentityAfterSameNameJoinAndStart()
    {
        using var client = _factory.CreateClient();
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var first = ActionConnection(registry);
        await lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        var entered = ActionSignal();
        var release = ActionSignal();
        first.Socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        var joining = client.PostAsJsonAsync("/api/lobby/join", new { name = "Alex", code = SentLines(first.Socket)[0][5..] }, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        Guid firstId = lobby.GetState().Players[0].Id;
        var second = await ControlledJoinedAsync(client, "Alex");
        Guid secondId = lobby.GetState().Players[1].Id;
        var starting = lobby.StartAsync(_deadline.Token);
        Assert.True(lobby.GetState().Started);
        Assert.NotEqual(firstId, secondId);
        Assert.False(joining.IsCompleted);
        release.TrySetResult();
        using var joined = await joining;
        var result = (await joined.Content.ReadFromJsonAsync<LobbyJoinResponse>(_deadline.Token))!;
        Assert.Equal(firstId, result.JoinedPlayerId);
        Assert.True(result.Started);
        Assert.Equal(new[] { firstId, secondId }, result.Players.Select(player => player.Id));
        await starting;
        Assert.Equal("WAIT Alex", SentLines(second.Socket)[^1]);
        using var get = await client.GetAsync("/api/lobby", _deadline.Token);
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync(_deadline.Token));
        Assert.Equal(new[] { "players", "started" }, json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private (ControlledWebSocket Socket, OplaConnection Connection) ActionConnection(OplaConnectionRegistry registry)
    {
        Assert.True(registry.TryReserve(out Guid id));
        var socket = new ControlledWebSocket();
        _sockets.Add(socket);
        var connection = new OplaConnection(id, socket);
        Assert.True(registry.TryAttach(connection));
        Assert.True(registry.TryMarkReady(id));
        return (socket, connection);
    }
    private static string[] SentLines(ControlledWebSocket socket) => socket.Sent.Select(Encoding.UTF8.GetString).ToArray();
    private static TaskCompletionSource ActionSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
