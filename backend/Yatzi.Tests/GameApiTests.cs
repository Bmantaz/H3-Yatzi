using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Yatzi.Tests;

/// <summary>
/// Tester HTTP-kontrakten gennem den rigtige request-pipeline og GameSession.
/// xUnit opretter en ny testklasse pr. testcase, så hver test får sin egen server
/// og delte spiltilstand. Der åbnes ingen rigtig netværksport.
/// </summary>
public class GameApiTests : IDisposable
{
    private const string InvalidBodyMessage = "Requesten skal indeholde et gyldigt JSON-objekt.";
    private const string InvalidNameTypeMessage = "Navnet skal være tekst.";
    private const string InvalidNameLengthMessage = "Navnet skal indeholde mellem 1 og 20 tegn.";
    private const string MissingGameMessage = "Der er ikke oprettet et spil. Start et nyt spil først.";
    private const string MaxRollsMessage = "Der er allerede brugt tre slag i denne tur.";
    private const string InvalidDieNumberMessage = "Terningens nummer skal være mellem 1 og 5.";
    private const string HoldNotAllowedMessage = "Hold/slip er kun tilladt efter første eller andet kast.";

    private readonly WebApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Get_BeforeNewGame_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/game");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("Andreas", "Andreas")]
    [InlineData("  Andreas  ", "Andreas")]
    [InlineData("ABCDEFGHIJKLMNOPQRST", "ABCDEFGHIJKLMNOPQRST")]
    [InlineData("  ABCDEFGHIJKLMNOPQRST  ", "ABCDEFGHIJKLMNOPQRST")]
    [InlineData("\tAndreas\r\n", "Andreas")]
    public async Task NewGame_ValidName_ReturnsTrimmedNameAndStartState(string name, string expectedName)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/game/new", new { name });

        await AssertStartStateAsync(response, expectedName);
    }

    /// <summary>
    /// Dækker JSON-, type- og længdefejl hver for sig. Null betyder helt manglende
    /// body; en tom streng sender derimod en tom body med application/json.
    /// </summary>
    public static IEnumerable<object?[]> InvalidNewGameRequests()
    {
        yield return new object?[] { null, InvalidBodyMessage };
        yield return new object?[] { "", InvalidBodyMessage };
        yield return new object?[] { "{", InvalidBodyMessage };
        yield return new object?[] { "null", InvalidBodyMessage };
        yield return new object?[] { "[]", InvalidBodyMessage };
        yield return new object?[] { "\"Andreas\"", InvalidBodyMessage };
        yield return new object?[] { "42", InvalidBodyMessage };
        yield return new object?[] { "true", InvalidBodyMessage };
        yield return new object?[] { "{}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":null}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":42}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":true}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":[]}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":{}}", InvalidNameTypeMessage };
        yield return new object?[] { "{\"name\":\"\"}", InvalidNameLengthMessage };
        yield return new object?[] { "{\"name\":\"   \"}", InvalidNameLengthMessage };
        yield return new object?[] { "{\"name\":\"ABCDEFGHIJKLMNOPQRSTU\"}", InvalidNameLengthMessage };
        yield return new object?[] { "{\"name\":\"  ABCDEFGHIJKLMNOPQRSTU  \"}", InvalidNameLengthMessage };
    }

    [Theory]
    [MemberData(nameof(InvalidNewGameRequests))]
    public async Task NewGame_InvalidInput_DoesNotCreateGame(string? body, string message)
    {
        using var client = _factory.CreateClient();

        using var response = await PostNewGameBodyAsync(client, body);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, message);
        using var stateResponse = await client.GetAsync("/api/game");
        await AssertErrorAsync(stateResponse, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Theory]
    [MemberData(nameof(InvalidNewGameRequests))]
    public async Task NewGame_InvalidInput_PreservesExistingGame(string? body, string message)
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        using var rolled = await client.PostAsync("/api/game/roll", null);
        await AssertGameStateAsync(rolled, "Andreas", 1);
        bool[] held = { false, true, false, false, false };
        using var heldResponse = await client.PostAsync("/api/game/hold/2", null);
        string heldJson = await AssertGameStateAsync(heldResponse, "Andreas", 1, held);

        using var response = await PostNewGameBodyAsync(client, body);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, message);
        using var stateResponse = await client.GetAsync("/api/game");
        Assert.Equal(heldJson, await AssertGameStateAsync(stateResponse, "Andreas", 1, held));
    }

    [Fact]
    public async Task Roll_BeforeNewGame_ReturnsNotFoundAndDoesNotCreateGame()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/game/roll", null);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, MissingGameMessage);
        using var stateResponse = await client.GetAsync("/api/game");
        await AssertErrorAsync(stateResponse, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Fact]
    public async Task Roll_ThreeAcceptedRolls_ReturnUpdatedStateToAllClients()
    {
        using var client = _factory.CreateClient();
        using var otherClient = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");

        for (int roll = 1; roll <= 3; roll++)
        {
            using var response = await client.PostAsync("/api/game/roll", null);
            string rolledJson = await AssertGameStateAsync(response, "Andreas", roll);

            foreach (var reader in new[] { client, otherClient })
            {
                using var stateResponse = await reader.GetAsync("/api/game");
                Assert.Equal(rolledJson, await AssertGameStateAsync(stateResponse, "Andreas", roll));
            }
        }
    }

    [Fact]
    public async Task Roll_AfterThreeRolls_ReturnsConflictAndPreservesState()
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        string lastAcceptedJson = string.Empty;
        for (int roll = 1; roll <= 3; roll++)
        {
            using var accepted = await client.PostAsync("/api/game/roll", null);
            lastAcceptedJson = await AssertGameStateAsync(accepted, "Andreas", roll);
        }

        using var response = await client.PostAsync("/api/game/roll", null);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, MaxRollsMessage);
        using var stateResponse = await client.GetAsync("/api/game");
        Assert.Equal(lastAcceptedJson, await AssertGameStateAsync(stateResponse, "Andreas", 3));
    }

    [Fact]
    public async Task NewGame_AfterThreeRolls_ResetsStateAndAllowsAnotherRoll()
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        var held = new bool[5];
        for (int roll = 1; roll <= 3; roll++)
        {
            using var response = await client.PostAsync("/api/game/roll", null);
            await AssertGameStateAsync(response, "Andreas", roll, held);
            if (roll == 1)
            {
                held[1] = true;
                using var heldResponse = await client.PostAsync("/api/game/hold/2", null);
                await AssertGameStateAsync(heldResponse, "Andreas", roll, held);
            }
        }

        using var replacement = await client.PostAsJsonAsync("/api/game/new", new { name = "Frederik" });

        string freshJson = await AssertStartStateAsync(replacement, "Frederik");
        using var freshState = await client.GetAsync("/api/game");
        Assert.Equal(freshJson, await AssertStartStateAsync(freshState, "Frederik"));
        using var nextRoll = await client.PostAsync("/api/game/roll", null);
        string rolledJson = await AssertGameStateAsync(nextRoll, "Frederik", 1);
        using var rolledState = await client.GetAsync("/api/game");
        Assert.Equal(rolledJson, await AssertGameStateAsync(rolledState, "Frederik", 1));
    }

    [Fact]
    public async Task Roll_ConcurrentRequests_OnlyAcceptThreeRollsWithConsistentSnapshots()
    {
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        using var created = await firstClient.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");

        var clients = new[] { firstClient, secondClient };
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 5).Select(async index =>
        {
            // Alle fem opgaver venter på samme start, så vi sender konkurrerende
            // requests frem for at vente på ét kast ad gangen.
            await start.Task;
            return await clients[index % clients.Length].PostAsync("/api/game/roll", null);
        }).ToArray();

        start.SetResult(true);
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var acceptedRollNumbers = new List<int>();
            string? thirdRollJson = null;
            foreach (var response in responses)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    int rollNumber = document.RootElement.GetProperty("rollsUsed").GetInt32();
                    Assert.InRange(rollNumber, 1, 3);
                    string json = await AssertGameStateAsync(response, "Andreas", rollNumber);
                    acceptedRollNumbers.Add(rollNumber);
                    if (rollNumber == 3)
                    {
                        thirdRollJson = json;
                    }
                }
                else
                {
                    await AssertErrorAsync(response, HttpStatusCode.Conflict, MaxRollsMessage);
                }
            }

            Assert.Equal(new[] { 1, 2, 3 }, acceptedRollNumbers.OrderBy(number => number).ToArray());
            Assert.NotNull(thirdRollJson);
            using var stateResponse = await firstClient.GetAsync("/api/game");
            Assert.Equal(thirdRollJson, await AssertGameStateAsync(stateResponse, "Andreas", 3));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    public async Task Hold_ValidNumber_TogglesOnlySelectedDie(int number, int rollsUsed)
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        for (int roll = 1; roll <= rollsUsed; roll++)
        {
            using var response = await client.PostAsync("/api/game/roll", null);
            await AssertGameStateAsync(response, "Andreas", roll);
        }

        // En anden terning holdes i forvejen, så testen også opdager, hvis
        // handlingen fejlagtigt nulstiller andre terningers hold-markeringer.
        int otherNumber = number % 5 + 1;
        var held = new bool[5];
        held[otherNumber - 1] = true;
        using var otherHeld = await client.PostAsync($"/api/game/hold/{otherNumber}", null);
        string beforeJson = await AssertGameStateAsync(otherHeld, "Andreas", rollsUsed, held);

        held[number - 1] = true;
        using var heldResponse = await client.PostAsync($"/api/game/hold/{number}", null);
        string heldJson = await AssertGameStateAsync(heldResponse, "Andreas", rollsUsed, held);
        Assert.Equal(ReadDice(beforeJson), ReadDice(heldJson));

        held[number - 1] = false;
        using var released = await client.PostAsync($"/api/game/hold/{number}", null);
        string releasedJson = await AssertGameStateAsync(released, "Andreas", rollsUsed, held);
        Assert.Equal(beforeJson, releasedJson);
        using var stateResponse = await client.GetAsync("/api/game");
        Assert.Equal(releasedJson, await AssertGameStateAsync(stateResponse, "Andreas", rollsUsed, held));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task Hold_InvalidNumberBeforeNewGame_ReturnsBadRequestAndDoesNotCreateGame(string number)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/game/hold/{number}", null);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, InvalidDieNumberMessage);
        using var stateResponse = await client.GetAsync("/api/game");
        await AssertErrorAsync(stateResponse, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task Hold_InvalidNumber_PreservesExistingGame(string number)
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        using var rolled = await client.PostAsync("/api/game/roll", null);
        await AssertGameStateAsync(rolled, "Andreas", 1);
        bool[] held = { false, false, false, false, true };
        using var heldResponse = await client.PostAsync("/api/game/hold/5", null);
        string beforeJson = await AssertGameStateAsync(heldResponse, "Andreas", 1, held);

        using var response = await client.PostAsync($"/api/game/hold/{number}", null);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, InvalidDieNumberMessage);
        using var stateResponse = await client.GetAsync("/api/game");
        Assert.Equal(beforeJson, await AssertGameStateAsync(stateResponse, "Andreas", 1, held));
    }

    [Fact]
    public async Task Hold_ValidNumberBeforeNewGame_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/game/hold/2", null);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, MissingGameMessage);
        using var stateResponse = await client.GetAsync("/api/game");
        await AssertErrorAsync(stateResponse, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Hold_OutsideAllowedRolls_ReturnsConflictAndPreservesState(int rollsUsed)
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        var held = new bool[5];
        for (int roll = 1; roll <= rollsUsed; roll++)
        {
            using var response = await client.PostAsync("/api/game/roll", null);
            await AssertGameStateAsync(response, "Andreas", roll, held);
            if (roll == 1)
            {
                held[4] = true;
                using var heldResponse = await client.PostAsync("/api/game/hold/5", null);
                await AssertGameStateAsync(heldResponse, "Andreas", roll, held);
            }
        }

        using var beforeResponse = await client.GetAsync("/api/game");
        string beforeJson = await AssertGameStateAsync(beforeResponse, "Andreas", rollsUsed, held);

        using var rejected = await client.PostAsync("/api/game/hold/2", null);

        await AssertErrorAsync(rejected, HttpStatusCode.Conflict, HoldNotAllowedMessage);
        using var afterResponse = await client.GetAsync("/api/game");
        Assert.Equal(beforeJson, await AssertGameStateAsync(afterResponse, "Andreas", rollsUsed, held));
    }

    [Fact]
    public async Task Hold_CompleteTurn_PreservesHeldDieAndSupportsRelease()
    {
        using var client = _factory.CreateClient();
        using var otherClient = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        using var firstRoll = await client.PostAsync("/api/game/roll", null);
        string firstJson = await AssertGameStateAsync(firstRoll, "Andreas", 1);

        bool[] held = { false, true, false, false, false };
        using var heldResponse = await client.PostAsync("/api/game/hold/2", null);
        string heldJson = await AssertGameStateAsync(heldResponse, "Andreas", 1, held);
        Assert.Equal(ReadDice(firstJson), ReadDice(heldJson));
        using var otherHeldState = await otherClient.GetAsync("/api/game");
        Assert.Equal(heldJson, await AssertGameStateAsync(otherHeldState, "Andreas", 1, held));

        using var secondRoll = await client.PostAsync("/api/game/roll", null);
        string secondJson = await AssertGameStateAsync(secondRoll, "Andreas", 2, held);
        Assert.Equal(ReadDice(firstJson)[1], ReadDice(secondJson)[1]);

        using var released = await client.PostAsync("/api/game/hold/2", null);
        string releasedJson = await AssertGameStateAsync(released, "Andreas", 2);
        Assert.Equal(ReadDice(secondJson), ReadDice(releasedJson));

        // En sluppet terning kastes igen, men må gerne få den samme værdi.
        using var thirdRoll = await client.PostAsync("/api/game/roll", null);
        string thirdJson = await AssertGameStateAsync(thirdRoll, "Andreas", 3);
        using var otherFinalState = await otherClient.GetAsync("/api/game");
        Assert.Equal(thirdJson, await AssertGameStateAsync(otherFinalState, "Andreas", 3));
    }

    [Fact]
    public async Task Roll_AllDiceHeld_UsesRollsWithoutChangingDiceAndNewGameClearsHolds()
    {
        using var client = _factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        using var firstRoll = await client.PostAsync("/api/game/roll", null);
        string firstJson = await AssertGameStateAsync(firstRoll, "Andreas", 1);
        int[] firstDice = ReadDice(firstJson);

        var held = new bool[5];
        for (int number = 1; number <= 5; number++)
        {
            held[number - 1] = true;
            using var response = await client.PostAsync($"/api/game/hold/{number}", null);
            string heldJson = await AssertGameStateAsync(response, "Andreas", 1, held);
            Assert.Equal(firstDice, ReadDice(heldJson));
        }

        for (int roll = 2; roll <= 3; roll++)
        {
            using var response = await client.PostAsync("/api/game/roll", null);
            string rolledJson = await AssertGameStateAsync(response, "Andreas", roll, held);
            Assert.Equal(firstDice, ReadDice(rolledJson));
        }

        using var replacement = await client.PostAsJsonAsync("/api/game/new", new { name = "Frederik" });
        await AssertStartStateAsync(replacement, "Frederik");
    }

    [Fact]
    public async Task Hold_ConcurrentRequests_ToggleTwiceWithoutLosingAnAction()
    {
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        using var created = await firstClient.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");
        using var rolled = await firstClient.PostAsync("/api/game/roll", null);
        string rolledJson = await AssertGameStateAsync(rolled, "Andreas", 1);

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new[] { firstClient, secondClient }.Select(async client =>
        {
            await start.Task;
            return await client.PostAsync("/api/game/hold/2", null);
        }).ToArray();

        start.SetResult(true);
        var responses = await Task.WhenAll(requests);
        try
        {
            var returnedHolds = new List<bool>();
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                bool isHeld = document.RootElement.GetProperty("held")[1].GetBoolean();
                bool[] expectedHeld = { false, isHeld, false, false, false };
                string json = await AssertGameStateAsync(response, "Andreas", 1, expectedHeld);
                Assert.Equal(ReadDice(rolledJson), ReadDice(json));
                returnedHolds.Add(isHeld);
            }

            // Rækkefølgen af netværkssvar er fri, men de to handlinger skal
            // give ét hold og ét slip og ende i samme tilstand som før dem.
            Assert.Equal(new[] { false, true }, returnedHolds.OrderBy(value => value).ToArray());
            using var stateResponse = await firstClient.GetAsync("/api/game");
            Assert.Equal(rolledJson, await AssertGameStateAsync(stateResponse, "Andreas", 1));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Get_RepeatedRequestsAndOtherClient_ReturnSameState()
    {
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        using var created = await firstClient.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        string initialJson = await AssertStartStateAsync(created, "Andreas");

        // Flere requests og en anden klient skal stadig bruge serverens ene session.
        foreach (var client in new[] { firstClient, firstClient, secondClient })
        {
            using var response = await client.GetAsync("/api/game");
            Assert.Equal(initialJson, await AssertStartStateAsync(response, "Andreas"));
        }
    }

    [Fact]
    public async Task NewGame_ReplacesExistingPlayerAndReturnsFreshStartState()
    {
        using var client = _factory.CreateClient();
        using var firstGame = await client.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(firstGame, "Andreas");

        using var replacement = await client.PostAsJsonAsync("/api/game/new", new { name = "Frederik" });

        string replacementJson = await AssertStartStateAsync(replacement, "Frederik");
        using var stateResponse = await client.GetAsync("/api/game");
        Assert.Equal(replacementJson, await AssertStartStateAsync(stateResponse, "Frederik"));
    }

    [Fact]
    public async Task Get_NewServerInstance_StartsWithoutGame()
    {
        using var firstClient = _factory.CreateClient();
        using var created = await firstClient.PostAsJsonAsync("/api/game/new", new { name = "Andreas" });
        await AssertStartStateAsync(created, "Andreas");

        // En ny host har sin egen DI-container og arver intet fra den første host.
        using var otherFactory = new WebApplicationFactory<Program>();
        using var otherClient = otherFactory.CreateClient();
        using var response = await otherClient.GetAsync("/api/game");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, MissingGameMessage);
    }

    [Fact]
    public async Task Health_StillReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Frontend_StillServesStartPage()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id=\"start-screen\"", await response.Content.ReadAsStringAsync());
    }

    // Sender også syntaktisk ugyldig JSON, som PostAsJsonAsync ellers ville rette
    // ved at serialisere strengen. Request og content frigives efter afsendelse.
    private static async Task<HttpResponseMessage> PostNewGameBodyAsync(HttpClient client, string? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/game/new");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static Task<string> AssertStartStateAsync(HttpResponseMessage response, string expectedName) =>
        AssertGameStateAsync(response, expectedName, 0);

    // Læser de faktiske JSON-felter frem for at deserialisere til API'ets egne
    // records, så forkert casing eller et ændret format også bliver opdaget.
    // Tilfældige kast må gerne gentage værdier; derfor kontrolleres intervallet.
    private static async Task<string> AssertGameStateAsync(
        HttpResponseMessage response, string expectedName, int expectedRollsUsed, bool[]? expectedHeld = null)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var state = document.RootElement;
        Assert.Equal(
            new[] { "currentPlayer", "dice", "held", "players", "rollsUsed" },
            state.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());

        var players = state.GetProperty("players");
        Assert.Equal(1, players.GetArrayLength());
        Assert.Equal("name", Assert.Single(players[0].EnumerateObject()).Name);
        Assert.Equal(expectedName, players[0].GetProperty("name").GetString());
        Assert.Equal(0, state.GetProperty("currentPlayer").GetInt32());
        Assert.Equal(expectedRollsUsed, state.GetProperty("rollsUsed").GetInt32());

        var dice = state.GetProperty("dice");
        Assert.Equal(5, dice.GetArrayLength());
        int minValue = expectedRollsUsed == 0 ? 0 : 1;
        int maxValue = expectedRollsUsed == 0 ? 0 : 6;
        Assert.All(dice.EnumerateArray(), die => Assert.InRange(die.GetInt32(), minValue, maxValue));
        var held = state.GetProperty("held");
        Assert.Equal(5, held.GetArrayLength());
        Assert.Equal(expectedHeld ?? new bool[5], held.EnumerateArray().Select(die => die.GetBoolean()).ToArray());
        return json;
    }

    private static int[] ReadDice(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("dice").EnumerateArray().Select(die => die.GetInt32()).ToArray();
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, string expectedMessage)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("message", Assert.Single(document.RootElement.EnumerateObject()).Name);
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("message").GetString());
    }

    public void Dispose() => _factory.Dispose();
}
