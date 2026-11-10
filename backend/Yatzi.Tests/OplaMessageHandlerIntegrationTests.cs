using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>
/// Tester handleren fra applikationens rigtige servicecontainer sammen med HTTP-API'et.
/// Hver testcase får sin egen testvært og session. HTTP kører i hukommelsen,
/// så testene kræver hverken en åben netværksport eller fysisk Oplà.
/// </summary>
public class OplaMessageHandlerIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task HandleLine_CompleteTurn_IsVisibleThroughHttpAndPreservesHeldDice()
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");

        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var firstRoll = await GetStateAsync(client);
        AssertState(firstRoll, "Andreas", 1, "00000");

        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));
        var heldSecondDie = await GetStateAsync(client);
        AssertState(heldSecondDie, "Andreas", 1, "01000");
        Assert.Equal(firstRoll.Dice, heldSecondDie.Dice);

        Assert.Equal("STATE 1 01010", Assert.Single(handler.HandleLine("HOLD 4")));
        var heldFourthDie = await GetStateAsync(client);
        AssertState(heldFourthDie, "Andreas", 1, "01010");
        Assert.Equal(firstRoll.Dice, heldFourthDie.Dice);

        Assert.Equal("STATE 2 01010", Assert.Single(handler.HandleLine("ROLL")));
        var secondRoll = await GetStateAsync(client);
        AssertState(secondRoll, "Andreas", 2, "01010");
        Assert.Equal(firstRoll.Dice[1], secondRoll.Dice[1]);
        Assert.Equal(firstRoll.Dice[3], secondRoll.Dice[3]);

        Assert.Equal("STATE 2 00010", Assert.Single(handler.HandleLine("HOLD 2")));
        var releasedSecondDie = await GetStateAsync(client);
        AssertState(releasedSecondDie, "Andreas", 2, "00010");
        Assert.Equal(secondRoll.Dice, releasedSecondDie.Dice);

        Assert.Equal("STATE 3 00010", Assert.Single(handler.HandleLine("ROLL")));
        var thirdRoll = await GetStateAsync(client);
        AssertState(thirdRoll, "Andreas", 3, "00010");
        Assert.Equal(firstRoll.Dice[3], thirdRoll.Dice[3]);

        Assert.Empty(handler.HandleLine("ROLL"));
        AssertSameState(thirdRoll, await GetStateAsync(client));
        Assert.Empty(handler.HandleLine("HOLD 4"));
        AssertSameState(thirdRoll, await GetStateAsync(client));
    }

    [Fact]
    public async Task HttpAndHandlerActions_UpdateTheSameGameInBothDirections()
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");

        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var firstRoll = await GetStateAsync(client);
        AssertState(firstRoll, "Andreas", 1, "00000");

        using var heldResponse = await GameHttpActions.PostAsync(client, "/api/game/hold/2", _factory.Services.GetRequiredService<GameSession>().GetState());
        var held = await ReadStateAsync(heldResponse);
        AssertState(held, "Andreas", 1, "01000");
        Assert.Equal(firstRoll.Dice, held.Dice);

        Assert.Equal("STATE 2 01000", Assert.Single(handler.HandleLine("ROLL")));
        var secondRoll = await GetStateAsync(client);
        AssertState(secondRoll, "Andreas", 2, "01000");
        Assert.Equal(firstRoll.Dice[1], secondRoll.Dice[1]);

        using var releasedResponse = await GameHttpActions.PostAsync(client, "/api/game/hold/2", _factory.Services.GetRequiredService<GameSession>().GetState());
        var released = await ReadStateAsync(releasedResponse);
        AssertState(released, "Andreas", 2, "00000");
        Assert.Equal(secondRoll.Dice, released.Dice);
        AssertSameState(released, await GetStateAsync(client));

        // Et nyt handlerkald skal også se hold/slip, der kom gennem HTTP.
        Assert.Equal("STATE 3 00000", Assert.Single(handler.HandleLine("ROLL")));
        AssertState(await GetStateAsync(client), "Andreas", 3, "00000");
    }

    [Fact]
    public async Task NewGame_ExistingHandlerUsesTheReplacementGame()
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));
        Assert.Equal("STATE 2 01000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 3 01000", Assert.Single(handler.HandleLine("ROLL")));
        AssertState(await GetStateAsync(client), "Andreas", 3, "01000");

        await CreateGameAsync(client, "Frederik");

        // Samme handler skal følge sessionens nye spil frem for at beholde det gamle.
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        AssertState(await GetStateAsync(client), "Frederik", 1, "00000");
    }

    [Fact]
    public async Task ConcurrentRollsAcrossHttpAndHandler_OnlyAcceptThreeRolls()
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Handleren er synkron. Task.Run lader dens kald konkurrere med HTTP-kaldene
        // på arbejdstråde, når alle fem handlinger frigives fra samme startport.
        var handlerTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return handler.HandleLine("ROLL");
        })).ToArray();
        var contextState = _factory.Services.GetRequiredService<GameSession>().GetState();
        var httpTasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            return await GameHttpActions.PostAsync(client, "/api/game/roll", contextState);
        }).ToArray();

        start.SetResult(true);
        var handlerReplies = await Task.WhenAll(handlerTasks);
        var httpResponses = await Task.WhenAll(httpTasks);
        try
        {
            var acceptedRollNumbers = new List<int>();
            int rejected = 0;
            foreach (var replies in handlerReplies)
            {
                if (replies.Count == 0)
                {
                    rejected++;
                    continue;
                }

                string reply = Assert.Single(replies);
                Assert.Contains(reply, new[] { "STATE 1 00000", "STATE 2 00000", "STATE 3 00000" });
                acceptedRollNumbers.Add(reply[6] - '0');
            }

            foreach (var response in httpResponses)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var state = await ReadStateAsync(response);
                    Assert.InRange(state.RollsUsed, 1, 3);
                    AssertState(state, "Andreas", state.RollsUsed, "00000");
                    acceptedRollNumbers.Add(state.RollsUsed);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                    rejected++;
                }
            }

            // Hvilken indgang der vinder, er frit. Slagnumrene må hverken gentages
            // eller springes over, og begge indgange skal dele den samme grænse.
            Assert.Equal(new[] { 1, 2, 3 }, acceptedRollNumbers.OrderBy(value => value).ToArray());
            Assert.Equal(2, rejected);
            AssertState(await GetStateAsync(client), "Andreas", 3, "00000");
        }
        finally
        {
            foreach (var response in httpResponses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentHoldsAcrossHttpAndHandler_ToggleTwiceWithoutLosingAnAction()
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        var before = await GetStateAsync(client);
        AssertState(before, "Andreas", 1, "00000");
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handlerTask = Task.Run(async () =>
        {
            await start.Task;
            return handler.HandleLine("HOLD 2");
        });
        var httpTask = Task.Run(async () =>
        {
            await start.Task;
            return await GameHttpActions.PostAsync(client, "/api/game/hold/2", before);
        });

        start.SetResult(true);
        var handlerReplies = await handlerTask;
        using var httpResponse = await httpTask;
        var httpState = await ReadStateAsync(httpResponse);
        string reply = Assert.Single(handlerReplies);
        Assert.Contains(reply, new[] { "STATE 1 00000", "STATE 1 01000" });
        bool handlerHeld = reply == "STATE 1 01000";
        AssertState(httpState, "Andreas", 1, httpState.Held[1] ? "01000" : "00000");
        Assert.Equal(before.Dice, httpState.Dice);

        Assert.Equal(new[] { false, true }, new[] { handlerHeld, httpState.Held[1] }.OrderBy(value => value).ToArray());
        AssertSameState(before, await GetStateAsync(client));
    }

    [Theory]
    [InlineData("score")]
    [InlineData("strike")]
    public async Task HttpFieldChoice_OpensNextTurnForHandlerWithoutClearingPoints(string method)
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));

        using var response = await GameHttpActions.PostAsync(client, $"/api/game/{method}/chance", _factory.Services.GetRequiredService<GameSession>().GetState());
        var completed = await ReadStateAsync(response);

        Assert.False(completed.TurnCompleted);
        Assert.Equal(0, completed.RollsUsed);
        Assert.Empty(handler.HandleLine("HOLD 2"));
        AssertSameState(completed, await GetStateAsync(client));
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        Assert.Equal("STATE 1 01000", Assert.Single(handler.HandleLine("HOLD 2")));
        Assert.Equal(completed.Players[0].Scorecard.Scores["chance"], (await GetStateAsync(client)).Players[0].Scorecard.Scores["chance"]);

        await CreateGameAsync(client, "Frederik");
        Assert.Equal("STATE 1 00000", Assert.Single(handler.HandleLine("ROLL")));
        AssertState(await GetStateAsync(client), "Frederik", 1, "00000");
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("HOLD 2")]
    public async Task ConcurrentHttpScoreAndHandlerAction_FollowOneLegalOrder(string command)
    {
        using var client = _factory.CreateClient();
        var handler = _factory.Services.GetRequiredService<OplaMessageHandler>();
        await CreateGameAsync(client, "Andreas");
        handler.HandleLine("ROLL");
        // Hold alle gamle terninger, så et kast før feltvalget bevarer et kendt
        // pointgrundlag. Et kast efter feltvalget bruger derimod den nye tur.
        for (int i = 1; i <= 5; i++) handler.HandleLine($"HOLD {i}");
        var before = await GetStateAsync(client);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerTask = Task.Run(async () =>
        {
            await start.Task;
            return handler.HandleLine(command);
        });
        var httpTask = Task.Run(async () =>
        {
            await start.Task;
            return await GameHttpActions.PostAsync(client, "/api/game/score/chance", before);
        });
        start.SetResult(true);
        var replies = await handlerTask;
        using var response = await httpTask;
        var scored = await ReadStateAsync(response);

        Assert.False(scored.TurnCompleted);
        Assert.Equal(before.Dice.Sum(), scored.Players[0].Scorecard.Scores["chance"]);
        Assert.Single(scored.Players[0].Scorecard.Scores, entry => entry.Value.HasValue);
        Assert.Empty(scored.PossibleScores);
        Assert.Equal(new int[5], scored.Dice);
        Assert.Equal(new bool[5], scored.Held);
        Assert.Equal(0, scored.RollsUsed);
        var after = await GetStateAsync(client);
        if (replies.Count == 0)
        {
            Assert.Equal("HOLD 2", command);
            AssertSameState(scored, after);
        }
        else if (command == "ROLL")
        {
            string reply = Assert.Single(replies);
            if (reply == "STATE 2 11111")
            {
                AssertSameState(scored, after);
            }
            else
            {
                Assert.Equal("STATE 1 00000", reply);
                Assert.Equal(1, after.RollsUsed);
                Assert.Equal(scored.Players[0].Scorecard.Scores["chance"], after.Players[0].Scorecard.Scores["chance"]);
                Assert.All(after.Held, value => Assert.False(value));
                Assert.All(after.Dice, value => Assert.InRange(value, 1, 6));
            }
        }
        else
        {
            Assert.Equal("STATE 1 10111", Assert.Single(replies));
            AssertSameState(scored, after);
        }
    }

    private static async Task CreateGameAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/game/new", new { name });
        AssertState(await ReadStateAsync(response), name, 0, "00000");
    }

    private static async Task<GameStateResponse> GetStateAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/game");
        return await ReadStateAsync(response);
    }

    // HTTP-kontraktens feltnavne og casing kontrolleres i GameApiTests.
    // Her bruges DTO'en til at sammenligne indgangenes faktiske spiltilstand.
    private static async Task<GameStateResponse> ReadStateAsync(HttpResponseMessage response)
    {
        return await GameStateAssertions.ReadHttpStateAsync(response);
    }

    private static void AssertState(GameStateResponse state, string name, int rollsUsed, string heldPattern)
    {
        Assert.Equal(name, Assert.Single(state.Players).Name);
        Assert.Equal(0, state.CurrentPlayer);
        Assert.Equal(rollsUsed, state.RollsUsed);
        Assert.Equal(5, state.Dice.Length);
        int minValue = rollsUsed == 0 ? 0 : 1;
        int maxValue = rollsUsed == 0 ? 0 : 6;
        Assert.All(state.Dice, value => Assert.InRange(value, minValue, maxValue));
        Assert.Equal(heldPattern.Select(value => value == '1').ToArray(), state.Held);
    }

    private static void AssertSameState(GameStateResponse expected, GameStateResponse actual)
    {
        GameStateAssertions.Equal(expected, actual);
    }

    public void Dispose() => _factory.Dispose();
}
