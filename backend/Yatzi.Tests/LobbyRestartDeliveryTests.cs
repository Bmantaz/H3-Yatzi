using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Accepteret omkamp og ordnede køer bevares ved blokeret I/O, overtagelse og annullering.</summary>
public partial class LobbyApiTests
{
    [Fact]
    public async Task Restart_ConcurrentRequestsAcceptExactlyOneAndSendOnlyOneStatusPerPlayer()
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var ids = lobby.GetState().Players;
        int[] sent = controllers.Select(player => player.Socket.Sent.Count).ToArray();
        var gate = ActionSignal();
        var requests = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await gate.Task.WaitAsync(_deadline.Token);
            return await RestartRequestAsync(client, new(final.GameId, ids[index % 2].Id));
        })).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(requests).WaitAsync(_deadline.Token);
        try
        {
            var accepted = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var state = await GameStateAssertions.ReadHttpStateAsync(accepted, 2);
            GameRestartSessionTests.AssertFreshRestart(state, 2);
            foreach (var response in responses.Where(response => response != accepted))
                await AssertErrorAsync(response, HttpStatusCode.Conflict, StaleRestartMessage);
            Assert.Equal(sent.Select(count => count + 1), controllers.Select(player => player.Socket.Sent.Count));
            Assert.Equal("STATE 0 00000", SentLines(controllers[0].Socket)[^1]);
            Assert.Equal("WAIT Alex", SentLines(controllers[1].Socket)[^1]);
            GameStateAssertions.Equal(state, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("websocket")]
    [InlineData("io")]
    [InlineData("transport-cancel")]
    [InlineData("closed-after-accept")]
    public async Task Restart_DeliveryFailurePreserves200AndOtherRecipients(string failure)
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        Guid id = lobby.GetState().Players[1].Id;
        if (failure == "closed-after-accept")
            controllers[0].Socket.SendAction = _ => { controllers[1].Connection.MarkClosed(); return Task.CompletedTask; };
        else controllers[0].Socket.SendAction = _ => failure switch
        {
            "io" => Task.FromException(new IOException()),
            "transport-cancel" => Task.FromException(new OperationCanceledException()),
            _ => Task.FromException(new WebSocketException())
        };
        using var response = await RestartRequestAsync(client, new(final.GameId, id));
        var start = await GameStateAssertions.ReadHttpStateAsync(response, 2);
        GameRestartSessionTests.AssertFreshRestart(start, 2);
        GameStateAssertions.Equal(start, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        Assert.Equal(failure == "closed-after-accept" ? "STATE 0 00000" : "WAIT Alex",
            SentLines(controllers[failure == "closed-after-accept" ? 0 : 1].Socket)[^1]);
        Assert.True(controllers[failure == "closed-after-accept" ? 1 : 0].Connection.IsClosing);
        using var retry = await RestartRequestAsync(client, new(final.GameId, id));
        await AssertErrorAsync(retry, HttpStatusCode.Conflict, StaleRestartMessage);
        GameStateAssertions.Equal(start, _factory.Services.GetRequiredService<GameSession>().GetState()!);
    }

    [Fact]
    public async Task Restart_PendingEndOrdersRestartAndOplaWhileCompetingHttpRequestsReject()
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        var ids = lobby.GetState().Players;
        var first = controllers[0];
        var entered = ActionSignal();
        var release = ActionSignal();
        var otherDelivered = ActionSignal();
        int sent = first.Socket.Sent.Count;
        // READY bygger END før omkampen. Dets levering skal blive foran den nye STATE.
        first.Socket.SendAction = token =>
        {
            if (SentLines(first.Socket)[^1] != "END") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var oldReady = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        controllers[1].Socket.SendAction = _ => { otherDelivered.TrySetResult(); return Task.CompletedTask; };
        var request = RestartRequestAsync(client, new(final.GameId, ids[1].Id));
        await otherDelivered.Task.WaitAsync(_deadline.Token);
        Assert.False(request.IsCompleted);
        Assert.Equal("WAIT Alex", SentLines(controllers[1].Socket)[^1]);
        var start = game.GetState()!;
        GameRestartSessionTests.AssertFreshRestart(start, 2);

        var gate = ActionSignal();
        var competitors = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await gate.Task.WaitAsync(_deadline.Token);
            using var competing = await RestartRequestAsync(client, new(final.GameId, ids[index % 2].Id));
            await AssertErrorAsync(competing, HttpStatusCode.Conflict, StaleRestartMessage);
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(competitors).WaitAsync(_deadline.Token);
        Assert.Equal(sent + 3, first.Socket.Sent.Count);

        var roll = lobby.HandleGameInputAsync(first.Connection.ConnectionId, ids[0].Id, "ROLL",
            _factory.Services.GetRequiredService<OplaMessageHandler>(), _deadline.Token);
        Assert.Equal(1, game.GetState()!.RollsUsed);
        Assert.False(roll.IsCompleted);
        var laterReady = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        Assert.False(laterReady.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(oldReady, roll, laterReady).WaitAsync(_deadline.Token);
        using var accepted = await request;
        GameStateAssertions.Equal(start, await GameStateAssertions.ReadHttpStateAsync(accepted, 2));
        Assert.Equal(new[] { SentLines(first.Socket)[0], "JOINED Alex", "END", "STATE 0 00000", "STATE 1 00000",
            SentLines(first.Socket)[0], "JOINED Alex", "STATE 1 00000" }, SentLines(first.Socket).Skip(sent));
        Assert.Equal(1, first.Socket.MaxConcurrentSends);
        Assert.True(final.GameCompleted);
    }

    [Fact]
    public async Task Restart_LateSendFailureAfterTakeoverClosesOnlyCapturedOldConnection()
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var registry = _factory.Services.GetRequiredService<OplaConnectionRegistry>();
        var first = controllers[0];
        var ids = lobby.GetState().Players;
        var entered = ActionSignal();
        var release = ActionSignal();
        var otherDelivered = ActionSignal();
        first.Socket.SendAction = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new IOException(); };
        controllers[1].Socket.SendAction = _ => { otherDelivered.TrySetResult(); return Task.CompletedTask; };
        var restarting = RestartRequestAsync(client, new(final.GameId, ids[1].Id));
        await entered.Task.WaitAsync(_deadline.Token);
        await otherDelivered.Task.WaitAsync(_deadline.Token);
        var current = ActionConnection(registry);
        var takeover = lobby.HandleReadyAsync(current.Connection.ConnectionId, SentLines(first.Socket)[0][5..], _deadline.Token);
        Assert.Equal(new[] { SentLines(first.Socket)[0], "JOINED Alex", "STATE 0 00000" }, SentLines(current.Socket));
        Assert.False(restarting.IsCompleted);
        release.TrySetResult();
        await takeover.WaitAsync(_deadline.Token);
        using var response = await restarting;
        GameRestartSessionTests.AssertFreshRestart(await GameStateAssertions.ReadHttpStateAsync(response, 2), 2);
        Assert.False(current.Connection.IsClosing);
        Assert.Equal(0, current.Socket.CloseCount);
        Assert.Equal(3, current.Socket.Sent.Count);
        Assert.True(registry.TryGetPlayerId(current.Connection.ConnectionId, out Guid bound));
        Assert.Equal(ids[0].Id, bound);
        Assert.Equal(ids, lobby.GetState().Players);
    }

    [Fact]
    public async Task Restart_CancellationBeforeAcceptPreservesResultAndAllowsExplicitRetry()
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var before = lobby.GetState();
        int[] sent = controllers.Select(player => player.Socket.Sent.Count).ToArray();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lobby.RestartAsync(new(final.GameId, before.Players[1].Id), stopped.Token));
        GameStateAssertions.Equal(final, _factory.Services.GetRequiredService<GameSession>().GetState()!);
        Assert.Equal(before.Players, lobby.GetState().Players);
        Assert.Equal(sent, controllers.Select(player => player.Socket.Sent.Count));
        using var response = await RestartRequestAsync(client, new(final.GameId, before.Players[1].Id));
        GameRestartSessionTests.AssertFreshRestart(await GameStateAssertions.ReadHttpStateAsync(response, 2), 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_CancelledQueueOrSendPreservesAcceptedGameAndDoesNotPoisonReady(bool queued)
    {
        using var client = _factory.CreateClient();
        var (final, controllers) = await FinishedRestartAsync(client);
        var lobby = _factory.Services.GetRequiredService<LobbySession>();
        var game = _factory.Services.GetRequiredService<GameSession>();
        var first = controllers[0];
        var ids = lobby.GetState().Players;
        var entered = ActionSignal();
        var release = ActionSignal();
        var otherDelivered = ActionSignal();
        first.Socket.SendAction = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task older = Task.CompletedTask;
        if (queued)
        {
            older = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
            await entered.Task.WaitAsync(_deadline.Token);
        }
        controllers[1].Socket.SendAction = _ => { otherDelivered.TrySetResult(); return Task.CompletedTask; };
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        var restarting = client.PostAsJsonAsync("/api/lobby/restart", new LobbyRestartContext(final.GameId, ids[1].Id), stopped.Token);
        await entered.Task.WaitAsync(_deadline.Token);
        await otherDelivered.Task.WaitAsync(_deadline.Token);
        var accepted = game.GetState()!;
        GameRestartSessionTests.AssertFreshRestart(accepted, 2);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarting);
        Assert.False(first.Connection.IsClosing);
        var ready = lobby.HandleReadyAsync(first.Connection.ConnectionId, null, _deadline.Token);
        if (queued) Assert.False(ready.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(older, ready).WaitAsync(_deadline.Token);
        Assert.Equal(new[] { SentLines(first.Socket)[0], "JOINED Alex", "STATE 0 00000" }, SentLines(first.Socket).TakeLast(3));
        GameStateAssertions.Equal(accepted, game.GetState()!);
        using var retry = await RestartRequestAsync(client, new(final.GameId, ids[1].Id));
        await AssertErrorAsync(retry, HttpStatusCode.Conflict, StaleRestartMessage);
        Assert.False(first.Connection.IsClosing);
        Assert.Equal(1, first.Socket.MaxConcurrentSends);
    }
}
