using System.Text.Json;
using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// HTTP-grænsen til spillet. Validerer rute/JSON og lader LobbySession koordinere
/// handling og controllerkø med GameSession. Returnerer svarkopi eller dansk JSON-fejl.
/// </summary>
public static class GameEndpoints
{
    private const string InvalidBodyMessage = "Requesten skal indeholde et gyldigt JSON-objekt.";
    private const string InvalidNameTypeMessage = "Navnet skal være tekst.";
    private const string InvalidNameLengthMessage = "Navnet skal indeholde mellem 1 og 20 tegn.";
    private const string MissingGameMessage = "Der er ikke oprettet et spil. Start et nyt spil først.";
    private const string InvalidDieNumberMessage = "Terningens nummer skal være mellem 1 og 5.";
    private const string InvalidCategoryMessage = "Pointfeltet er ukendt. Brug et af de 15 felt-id'er.";

    /// <summary>
    /// Tilføjer oprettelse, hentning, kast, hold/slip og feltvalg under /api/game. GameSession skal være
    /// registreret i dependency injection, før applikationen bygges.
    /// </summary>
    public static IEndpointRouteBuilder MapGameApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/game");
        group.MapPost("/new", CreateGameAsync);
        group.MapGet("", GetGame);
        group.MapPost("/roll", RollGame);
        group.MapPost("/hold/{n}", ToggleHold);
        group.MapPost("/score/{category}", ScoreGame);
        group.MapPost("/strike/{category}", StrikeGame);
        return endpoints;
    }

    /// <summary>
    /// Læser et JSON-objekt med et navn og opretter først spillet efter validering.
    /// Ugyldigt input giver 400 uden at ændre det eksisterende spil.
    /// Afbrudte requests videresender cancellation til JSON-læsningen.
    /// </summary>
    private static async Task<IResult> CreateGameAsync(
        HttpRequest request, GameSession session, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            // Automatisk modelbinding kan afvise JSON før handleren og dermed
            // omgå kontraktens message-felt. Derfor håndteres læsningen her.
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
            }

            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return Results.BadRequest(new ApiErrorResponse(InvalidNameTypeMessage));
            }

            // String-kontrollen ovenfor udelukker null. Længden vurderes efter
            // trimning, ligesom frontenden fjerner mellemrum omkring navnet.
            string name = nameElement.GetString()!.Trim();
            if (name.Length is < 1 or > 20)
            {
                return Results.BadRequest(new ApiErrorResponse(InvalidNameLengthMessage));
            }

            try { return Results.Ok(session.NewGame(name)); }
            catch (InvalidOperationException exception)
            {
                // Gyldigt input kan ikke overskrive et accepteret lobbyspil.
                return Results.Conflict(new ApiErrorResponse(exception.Message));
            }
        }
    }

    /// <summary>
    /// Returnerer det eksisterende spil som 200 eller kontraktens 404-fejl,
    /// hvis serveren endnu ikke har modtaget et gyldigt oprettelseskald.
    /// </summary>
    private static IResult GetGame(GameSession session)
    {
        var state = session.GetState();
        return state is null
            ? Results.NotFound(new ApiErrorResponse(MissingGameMessage))
            : Results.Ok(state);
    }

    /// <summary>Kaster med obligatorisk kontekst; lobbyen koordinerer STATE uden netværk under lås.</summary>
    private static Task<IResult> RollGame(HttpRequest request, LobbySession session, CancellationToken cancellationToken) =>
        HandleActionAsync(request, session, GameActionKind.Roll, cancellationToken);

    /// <summary>Ruteinput har forrang for JSON. HTTP bruger nummer 1–5, kernen indeks 0–4.</summary>
    private static Task<IResult> ToggleHold(string n, HttpRequest request, LobbySession session, CancellationToken cancellationToken)
    {
        if (!int.TryParse(n, out int number) || number is < 1 or > 5)
            return Task.FromResult<IResult>(Results.BadRequest(new ApiErrorResponse(InvalidDieNumberMessage)));
        return HandleActionAsync(request, session, GameActionKind.ToggleHold, cancellationToken, number - 1);
    }

    /// <summary>Feltvalg sender hverken point eller terninger, kun felt og spil-/tur-/spillerkontekst.</summary>
    private static Task<IResult> ScoreGame(string category, HttpRequest request, LobbySession session, CancellationToken cancellationToken) =>
        ChooseField(category, request, session, GameActionKind.Score, cancellationToken);

    /// <summary>Strygning bruger samme kontekst og koordinering som pointvalg.</summary>
    private static Task<IResult> StrikeGame(string category, HttpRequest request, LobbySession session, CancellationToken cancellationToken) =>
        ChooseField(category, request, session, GameActionKind.Strike, cancellationToken);

    private static Task<IResult> ChooseField(string category, HttpRequest request, LobbySession session,
        GameActionKind action, CancellationToken cancellationToken)
    {
        if (!ScoreCategoryIds.TryParse(category, out var scoreCategory))
            return Task.FromResult<IResult>(Results.BadRequest(new ApiErrorResponse(InvalidCategoryMessage)));
        return HandleActionAsync(request, session, action, cancellationToken, category: scoreCategory);
    }

    // Format læses før tilstandsopslag. Cancellation videresendes; et allerede accepteret
    // resultat må ikke erstattes af et senere GET eller genudføres efter en leveringsfejl.
    private static async Task<IResult> HandleActionAsync(HttpRequest request, LobbySession session,
        GameActionKind action, CancellationToken cancellationToken, int index = 0, ScoreCategory category = default)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
        catch (JsonException) { return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage)); }
        using (document)
        {
            if (!TryReadContext(document.RootElement, out var context, out string message))
                return Results.BadRequest(new ApiErrorResponse(message));
            var result = await (action switch
            {
                GameActionKind.Roll => session.RollAsync(context, cancellationToken),
                GameActionKind.ToggleHold => session.ToggleHoldAsync(index, context, cancellationToken),
                GameActionKind.Score => session.ScoreAsync(category, context, cancellationToken),
                GameActionKind.Strike => session.StrikeAsync(category, context, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            });
            return result.Status switch
            {
                GameActionStatus.Accepted => Results.Ok(result.State),
                GameActionStatus.MissingGame => Results.NotFound(new ApiErrorResponse(result.Message!)),
                GameActionStatus.InvalidLobbyPlayerId => Results.BadRequest(new ApiErrorResponse(result.Message!)),
                GameActionStatus.Conflict => Results.Conflict(new ApiErrorResponse(result.Message!)),
                _ => throw new InvalidOperationException("Ukendt resultat fra spilhandlingen.")
            };
        }
    }

    // Tilstedeværelse kontrolleres eksplicit: playerId:null er gyldigt format, et manglende
    // playerId er ikke. TryGetText afviser også ugyldige UTF-16-escapes uden en 500-fejl.
    private static bool TryReadContext(JsonElement root, out GameActionContext context, out string message)
    {
        context = default;
        message = InvalidBodyMessage;
        if (root.ValueKind != JsonValueKind.Object) return false;
        message = "gameId skal være et gyldigt, ikke-tomt GUID.";
        if (!root.TryGetProperty("gameId", out var game) || !TryGetText(game, out string gameText)
            || !Guid.TryParse(gameText, out Guid gameId) || gameId == Guid.Empty) return false;
        message = "turnNumber skal være et positivt heltal.";
        if (!root.TryGetProperty("turnNumber", out var turn) || turn.ValueKind != JsonValueKind.Number
            || !turn.TryGetInt32(out int turnNumber) || turnNumber < 1) return false;
        message = "playerId skal være null eller et gyldigt, ikke-tomt GUID.";
        if (!root.TryGetProperty("playerId", out var player)) return false;
        Guid? playerId = null;
        if (player.ValueKind != JsonValueKind.Null)
        {
            if (!TryGetText(player, out string playerText) || !Guid.TryParse(playerText, out Guid parsed) || parsed == Guid.Empty) return false;
            playerId = parsed;
        }
        context = new(gameId, turnNumber, playerId);
        message = string.Empty;
        return true;
    }

    private static bool TryGetText(JsonElement element, out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String) return false;
        try { text = element.GetString()!; return true; }
        catch (InvalidOperationException) { return false; }
    }
}
