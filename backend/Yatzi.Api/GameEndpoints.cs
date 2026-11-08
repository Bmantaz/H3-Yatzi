using System.Text.Json;
using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// HTTP-grænsen mellem frontenden og GameSession. Validerer input og oversætter
/// resultatet til spiltilstand eller danske JSON-fejl efter docs/api.md.
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

    /// <summary>
    /// Kaster uden request-body og returnerer 200 med tilstanden eller 404 uden spil.
    /// Kernens afvisning efter tredje slag eller afsluttet tur bliver 409 med dens danske besked.
    /// </summary>
    private static IResult RollGame(GameSession session)
    {
        GameStateResponse? state;
        try
        {
            // Fang kun regelafvisningen omkring selve kastet. Andre fejltyper
            // håndteres af ASP.NET Core og omklassificeres ikke til spilregelbrud.
            state = session.Roll();
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new ApiErrorResponse(exception.Message));
        }

        return state is null
            ? Results.NotFound(new ApiErrorResponse(MissingGameMessage))
            : Results.Ok(state);
    }

    /// <summary>
    /// Validerer terningenummer 1–5 og skifter hold/slip uden request-body.
    /// Returnerer 400 for ugyldigt nummer, 404 uden spil eller 409 ved regelafvisning.
    /// Et vellykket kald giver 200 med hele spiltilstanden.
    /// </summary>
    private static IResult ToggleHold(string n, GameSession session)
    {
        // Tekstparameteren gør, at også fx abc og heltalsoverløb får vores JSON-fejl.
        // Nummeret kontrolleres først, så ugyldigt input altid giver 400, også uden spil.
        if (!int.TryParse(n, out int number) || number is < 1 or > 5)
        {
            return Results.BadRequest(new ApiErrorResponse(InvalidDieNumberMessage));
        }

        GameStateResponse? state;
        try
        {
            // Kun kommunikationslaget kender numrene 1–5; kernen bruger indeks 0–4.
            state = session.ToggleHold(number - 1);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new ApiErrorResponse(exception.Message));
        }

        return state is null
            ? Results.NotFound(new ApiErrorResponse(MissingGameMessage))
            : Results.Ok(state);
    }

    /// <summary>
    /// Registrerer point fra serverens terninger uden request-body. Klienten vælger kun feltet.
    /// </summary>
    private static IResult ScoreGame(string category, GameSession session) =>
        ChooseField(category, session.Score);

    /// <summary>
    /// Stryger et felt uden request-body. Kernen kontrollerer kast og tur og gemmer altid 0.
    /// </summary>
    private static IResult StrikeGame(string category, GameSession session) =>
        ChooseField(category, session.Strike);

    /// <summary>
    /// Fælles HTTP-grænse for feltvalg: præcist tekst-id før spilopslag, derefter
    /// 200 med hele tilstanden, 404 uden spil eller 409 ved kernens regelafvisning.
    /// Andre fejltyper videresendes til ASP.NET Core.
    /// </summary>
    private static IResult ChooseField(string category, Func<ScoreCategory, GameStateResponse?> action)
    {
        if (!ScoreCategoryIds.TryParse(category, out var scoreCategory))
        {
            return Results.BadRequest(new ApiErrorResponse(InvalidCategoryMessage));
        }

        GameStateResponse? state;
        try
        {
            state = action(scoreCategory);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new ApiErrorResponse(exception.Message));
        }

        return state is null
            ? Results.NotFound(new ApiErrorResponse(MissingGameMessage))
            : Results.Ok(state);
    }
}
