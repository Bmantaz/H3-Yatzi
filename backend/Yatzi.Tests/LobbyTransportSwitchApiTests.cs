using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Modellerer USB → WiFi → USB med tre successive forbindelser på samme endpoint.
/// Hver overtagelse går gennem appens HTTP-/WebSocket-pipeline og bevarer hele
/// spiløjebliksbilledet. Testene bruger hverken fysisk hardware eller rigtig TCP/WSS.
/// </summary>
public partial class LobbyApiTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task TransportSwitch_ActivePlayerKeepsStateAndCanRollOnceAfterTwoTakeovers(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var (before, players) = await StartScoredTransportSwitchGameAsync(factory, client);
        var lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        var current = players[1];

        for (int change = 0; change < 2; change++)
            current = await TakeOverTransportSwitchAsync(factory, client, current, before, lobby, "STATE 2 01000");

        await SendAsync(current.Socket, "ROLL");
        Assert.Equal("STATE 3 01000", await ReceiveAsync(current.Socket));
        var after = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        Assert.Equal(before.RollsUsed + 1, after.RollsUsed);
        Assert.Equal(before.Dice[1], after.Dice[1]);
        // Kun ét nyt kast er sendt efter snapshot-kontrollen. Terningernes øvrige
        // værdier og mulige point må ændres; identitet, tur og gemte point skal bestå.
        GameStateAssertions.Equal(before with
        {
            RollsUsed = after.RollsUsed,
            Dice = after.Dice,
            PossibleScores = after.PossibleScores
        }, after);
        Assert.Empty(_handlerLogs.Entries);

        await CloseAsync(players[0].Socket, players[0].Connection);
        await CloseAsync(current.Socket, current.Connection);
        Assert.Equal(0, factory.Services.GetRequiredService<OplaConnectionRegistry>().Count);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task TransportSwitch_WaitingPlayerKeepsIdentityAndCannotRollOrHoldAfterTwoTakeovers(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var (before, players) = await StartScoredTransportSwitchGameAsync(factory, client);
        var lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        var current = players[0];

        for (int change = 0; change < 2; change++)
            current = await TakeOverTransportSwitchAsync(factory, client, current, before, lobby, "WAIT B");

        await AssertTransportSwitchInputRejectedAsync(client, current.Socket, before, "Det er ikke denne spillers tur");
        // READY er en behandlingsmarkør: afviste input må ikke have lagt et
        // STATE-svar i kø foran dette PAIR/JOINED/WAIT-batch.
        await SendAsync(current.Socket, "READY " + current.Code);
        Assert.Equal("PAIR " + current.Code, await ReceiveAsync(current.Socket));
        Assert.Equal("JOINED A", await ReceiveAsync(current.Socket));
        Assert.Equal("WAIT B", await ReceiveAsync(current.Socket));
        GameStateAssertions.Equal(before,
            (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!);

        await CloseAsync(current.Socket, current.Connection);
        await CloseAsync(players[1].Socket, players[1].Connection);
        Assert.Equal(0, factory.Services.GetRequiredService<OplaConnectionRegistry>().Count);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task TransportSwitch_CompletedGameKeepsResultAndCannotReopenAfterTwoTakeovers(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();
        var players = new[]
        {
            await JoinOnFactoryAsync(factory, client, "A"),
            await JoinOnFactoryAsync(factory, client, "B")
        };
        var lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        var state = await GameStateAssertions.ReadHttpStateAsync(start, 2);
        await ReadStatusAsync();

        // Alle felter udfyldes gennem gyldige HTTP-handlinger. De rigtige sockets
        // modtager og forbruger også hvert spilsvar; intet skrives direkte i sessionen.
        foreach (var (category, _) in GameStateAssertions.Categories)
        {
            for (int player = 0; player < players.Length; player++)
            {
                using var roll = await GameHttpActions.PostAsync(client, "/api/game/roll", state,
                    lobby.Players[player].Id, _deadline.Token);
                state = await GameStateAssertions.ReadHttpStateAsync(roll, 2);
                Assert.Equal("STATE 1 00000", await ReceiveAsync(players[player].Socket));
                using var score = await GameHttpActions.PostAsync(client, "/api/game/score/" + category, state,
                    lobby.Players[player].Id, _deadline.Token);
                state = await GameStateAssertions.ReadHttpStateAsync(score, 2);
                await ReadStatusAsync();
            }
        }
        Assert.True(state.GameCompleted);
        Assert.NotEmpty(state.WinnerIndices);
        Assert.All(state.Players, player =>
        {
            Assert.All(player.Scorecard.Scores.Values, score => Assert.NotNull(score));
            Assert.True(player.Scorecard.Total > 0);
        });
        var before = state;
        lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        // B er sidste aktive spiller og forbliver det også i det afsluttede snapshot.
        // Dermed skyldes afvisningen slutstatus og ikke en anden spillers tur.
        Assert.Equal(1, before.CurrentPlayer);
        var current = players[1];
        for (int change = 0; change < 2; change++)
            current = await TakeOverTransportSwitchAsync(factory, client, current, before, lobby, "END");

        await AssertTransportSwitchInputRejectedAsync(client, current.Socket, before, "Spillet er afsluttet");
        await SendAsync(current.Socket, "READY " + current.Code);
        Assert.Equal("PAIR " + current.Code, await ReceiveAsync(current.Socket));
        Assert.Equal("JOINED B", await ReceiveAsync(current.Socket));
        Assert.Equal("END", await ReceiveAsync(current.Socket));
        GameStateAssertions.Equal(before,
            (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!);

        await CloseAsync(players[0].Socket, players[0].Connection);
        await CloseAsync(current.Socket, current.Connection);
        Assert.Equal(0, factory.Services.GetRequiredService<OplaConnectionRegistry>().Count);

        async Task ReadStatusAsync()
        {
            for (int player = 0; player < players.Length; player++)
                Assert.Equal(state.GameCompleted ? "END" : player == state.CurrentPlayer
                        ? "STATE 0 00000" : "WAIT " + state.Players[state.CurrentPlayer].Name,
                    await ReceiveAsync(players[player].Socket));
        }
    }

    /// <summary>
    /// Bygger et ikke-tomt spil gennem join/start, Oplà-input og HTTP-score:
    /// A har positive chancepoint, B har tur med to slag og terning 2 holdt.
    /// Returnerer det friske HTTP-snapshot og begge parrede forbindelser.
    /// </summary>
    private async Task<(GameStateResponse State,
        (WebSocket Socket, OplaConnection Connection, string Code)[] Players)> StartScoredTransportSwitchGameAsync(
        WebApplicationFactory<Program> factory, HttpClient client)
    {
        var players = new[]
        {
            await JoinOnFactoryAsync(factory, client, "A"),
            await JoinOnFactoryAsync(factory, client, "B")
        };
        var lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
        using var start = await client.PostAsync("/api/lobby/start", null, _deadline.Token);
        await GameStateAssertions.ReadHttpStateAsync(start, 2);
        Assert.Equal("STATE 0 00000", await ReceiveAsync(players[0].Socket));
        Assert.Equal("WAIT A", await ReceiveAsync(players[1].Socket));
        await SendAsync(players[0].Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(players[0].Socket));
        var rolled = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        using var score = await GameHttpActions.PostAsync(client, "/api/game/score/chance", rolled,
            lobby.Players[0].Id, _deadline.Token);
        var scored = await GameStateAssertions.ReadHttpStateAsync(score, 2);
        Assert.InRange(scored.Players[0].Scorecard.Scores["chance"].GetValueOrDefault(), 5, 30);
        Assert.Equal("WAIT B", await ReceiveAsync(players[0].Socket));
        Assert.Equal("STATE 0 00000", await ReceiveAsync(players[1].Socket));
        await SendAsync(players[1].Socket, "ROLL");
        Assert.Equal("STATE 1 00000", await ReceiveAsync(players[1].Socket));
        await SendAsync(players[1].Socket, "HOLD 2");
        Assert.Equal("STATE 1 01000", await ReceiveAsync(players[1].Socket));
        await SendAsync(players[1].Socket, "ROLL");
        Assert.Equal("STATE 2 01000", await ReceiveAsync(players[1].Socket));
        var before = (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!;
        Assert.Equal(1, before.CurrentPlayer);
        Assert.Equal(2, before.RollsUsed);
        Assert.True(before.Held[1]);
        return (before, players);
    }

    /// <summary>
    /// Sender bevaret READY-kode på en ny socket og kontrollerer svar, identitet
    /// og hele spillet før/efter den gamle sockets forsinkede lukkehåndtryk.
    /// Afslutter gammel forbindelse og returnerer den nye med samme kode.
    /// </summary>
    private async Task<(WebSocket Socket, OplaConnection Connection, string Code)> TakeOverTransportSwitchAsync(
        WebApplicationFactory<Program> factory, HttpClient client,
        (WebSocket Socket, OplaConnection Connection, string Code) previous,
        GameStateResponse before, LobbyStateResponse beforeLobby, string expectedStatus)
    {
        var registry = factory.Services.GetRequiredService<OplaConnectionRegistry>();
        Assert.True(registry.TryGetPlayerId(previous.Connection.ConnectionId, out Guid playerId));
        var identity = Assert.Single(beforeLobby.Players, player => player.Id == playerId);
        var knownConnections = registry.GetConnections().Select(connection => connection.ConnectionId).ToHashSet();
        var socket = await ConnectAsync(factory);
        await SendAsync(socket, "READY " + previous.Code);
        Assert.Equal("PAIR " + previous.Code, await ReceiveAsync(socket));
        Assert.Equal("JOINED " + identity.Name, await ReceiveAsync(socket));
        Assert.Equal(expectedStatus, await ReceiveAsync(socket));
        var current = Assert.Single(registry.GetConnections(), connection => !knownConnections.Contains(connection.ConnectionId));
        Assert.NotEqual(previous.Connection.ConnectionId, current.ConnectionId);
        Assert.True(previous.Connection.IsClosing);
        Assert.False(previous.Connection.Completion.IsCompleted);
        Assert.False(registry.TryGetPlayerId(previous.Connection.ConnectionId, out _));
        await AssertPreservedAsync();

        // Ny PAIR/JOINED/status er modtaget, mens gammel lukning stadig venter.
        // Afslut først nu håndtrykket, og kontrollér at oprydningen kun fjerner den gamle.
        var close = await previous.Socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), _deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
        await previous.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "overtagelse modtaget", _deadline.Token);
        await previous.Connection.Completion.WaitAsync(_deadline.Token);
        Assert.False(registry.TryGet(previous.Connection.ConnectionId, out _));
        Assert.False(registry.Remove(previous.Connection.ConnectionId));
        await AssertPreservedAsync();
        Assert.Equal(2, registry.Count);
        return (socket, current, previous.Code);

        async Task AssertPreservedAsync()
        {
            Assert.True(registry.TryGetPlayerId(current.ConnectionId, out Guid actualId));
            Assert.Equal(playerId, actualId);
            Assert.False(current.IsClosing);
            GameStateAssertions.Equal(before,
                (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!);
            var lobby = (await client.GetFromJsonAsync<LobbyStateResponse>("/api/lobby", _deadline.Token))!;
            Assert.Equal(beforeLobby.Started, lobby.Started);
            Assert.Equal(beforeLobby.Players, lobby.Players);
        }
    }

    /// <summary>Afventer afvisningslogs som behandlingsmarkører og bekræfter uændret spil efter hvert input.</summary>
    private async Task AssertTransportSwitchInputRejectedAsync(HttpClient client, WebSocket socket,
        GameStateResponse before, string expectedReason)
    {
        foreach (string command in new[] { "ROLL", "HOLD 2" })
        {
            await SendAsync(socket, command);
            var rejection = await _handlerLogs.ReadAsync(_deadline.Token);
            Assert.Equal(LogLevel.Warning, rejection.Level);
            Assert.Contains(expectedReason, rejection.Message);
            GameStateAssertions.Equal(before,
                (await client.GetFromJsonAsync<GameStateResponse>("/api/game", _deadline.Token))!);
        }
    }
}
