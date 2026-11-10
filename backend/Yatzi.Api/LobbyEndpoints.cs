using System.Text.Json;

namespace Yatzi.Api;

/// <summary>
/// HTTP-grænsen til lobbyen. Validerer browserens navn/kode og oversætter sessionens
/// regelresultater til danske JSON-fejl. LobbySession ejer tilmelding, start og omkamp.
/// </summary>
public static class LobbyEndpoints
{
    private const string InvalidBodyMessage = "Requesten skal indeholde et gyldigt JSON-objekt.";

    /// <summary>Registrerer lobbylæsning, tilmelding, første spillestart og omkamp.</summary>
    public static IEndpointRouteBuilder MapLobbyApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/lobby");
        group.MapGet("", (LobbySession session) => Results.Ok(session.GetState()));
        group.MapPost("/join", JoinAsync);
        group.MapPost("/start", StartAsync);
        group.MapPost("/restart", RestartAsync);
        return endpoints;
    }

    // Manuel JSON-læsning bevarer det fælles message-format også ved forkert body-type.
    // Al inputvalidering sker før sessionen, så en afvist request ikke forbruger en kode.
    private static async Task<IResult> JoinAsync(
        HttpRequest request, LobbySession session, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
        catch (JsonException)
        {
            return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new ApiErrorResponse("Navnet skal være tekst."));

            if (!TryGetText(nameElement, out string originalName))
                return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
            // Kontrollér før Trim: også et afsluttende linjeskift skal afvises.
            if (originalName.Any(char.IsControl))
                return Results.BadRequest(new ApiErrorResponse("Navnet må ikke indeholde kontroltegn."));
            string name = originalName.Trim();
            if (name.Length is < 1 or > 20)
                return Results.BadRequest(new ApiErrorResponse("Navnet skal indeholde mellem 1 og 20 tegn."));
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new ApiErrorResponse("Koden skal være tekst."));

            if (!TryGetText(codeElement, out string code))
                return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
            if (code.Length != 4 || code.Any(character => character is < '0' or > '9'))
                return Results.BadRequest(new ApiErrorResponse("Koden skal bestå af præcis fire ASCII-cifre."));

            var result = await session.JoinAsync(name, code, cancellationToken);
            return result.Status switch
            {
                LobbyJoinStatus.Joined => Results.Ok(new LobbyJoinResponse(result.State!.Players, result.State.Started, result.JoinedPlayerId!.Value)),
                LobbyJoinStatus.UnknownCode => Results.NotFound(new ApiErrorResponse("Koden er ukendt eller ikke længere gyldig.")),
                LobbyJoinStatus.AlreadyJoined => Results.Conflict(new ApiErrorResponse("Koden er allerede tilmeldt.")),
                LobbyJoinStatus.Full => Results.Conflict(new ApiErrorResponse("Lobbyen er fuld. Der kan højst være seks spillere.")),
                LobbyJoinStatus.ConnectionUnavailable => Results.Conflict(new ApiErrorResponse("Oplà-forbindelsen er ikke længere tilgængelig.")),
                LobbyJoinStatus.Started => Results.Conflict(new ApiErrorResponse("Lobbyen er allerede startet.")),
                _ => throw new InvalidOperationException("Ukendt resultat fra lobbytilmelding.")
            };
        }
    }

    // JsonDocument kan parse et escape med
    // uparret UTF-16-surrogat, som først afvises ved GetString. Fang kun tekstlæsningen,
    // så fejl fra lobbyens tilstandsændringer ikke omklassificeres til klientfejl.
    private static bool TryGetText(JsonElement element, out string text)
    {
        if (element.ValueKind != JsonValueKind.String) { text = string.Empty; return false; }
        try { text = element.GetString()!; return true; }
        catch (InvalidOperationException) { text = string.Empty; return false; }
    }

    // Første start kræver fortsat ingen body. Sessionen ejer
    // den atomiske accept og afventer STATE/WAIT uden at holde tilstandslåsen.
    private static async Task<IResult> StartAsync(LobbySession session, CancellationToken cancellationToken)
    {
        var result = await session.StartAsync(cancellationToken);
        return result.Status switch
        {
            LobbyStartStatus.Started => Results.Ok(result.State),
            LobbyStartStatus.AlreadyStarted => Results.Conflict(new ApiErrorResponse("Lobbyen er allerede startet.")),
            LobbyStartStatus.NoPlayers => Results.Conflict(new ApiErrorResponse("Lobbyen skal have mellem 1 og 6 tilmeldte spillere før start.")),
            LobbyStartStatus.Disconnected => Results.Conflict(new ApiErrorResponse("Alle tilmeldte spillere skal være forbundet før start.")),
            _ => throw new InvalidOperationException("Ukendt resultat fra lobbystart.")
        };
    }

    // Manuel formatkontrol før sessionsopslag bevarer fejlprioriteten og dansk JSON.
    // Dette er en ny spilrelation, så turnNumber fra spilhandlinger er ikke påkrævet.
    private static async Task<IResult> RestartAsync(HttpRequest request, LobbySession session, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
        catch (JsonException) { return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage)); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new ApiErrorResponse(InvalidBodyMessage));
            if (!root.TryGetProperty("gameId", out var game) || !TryGetText(game, out string gameText)
                || !Guid.TryParse(gameText, out Guid gameId) || gameId == Guid.Empty)
                return Results.BadRequest(new ApiErrorResponse("gameId skal være et gyldigt, ikke-tomt GUID."));
            if (!root.TryGetProperty("playerId", out var player) || !TryGetText(player, out string playerText)
                || !Guid.TryParse(playerText, out Guid playerId) || playerId == Guid.Empty)
                return Results.BadRequest(new ApiErrorResponse("playerId skal være et gyldigt, ikke-tomt GUID."));

            var result = await session.RestartAsync(new(gameId, playerId), cancellationToken);
            return result.Status switch
            {
                LobbyRestartStatus.Restarted => Results.Ok(result.State),
                LobbyRestartStatus.MissingGame => Results.NotFound(new ApiErrorResponse("Der er ikke oprettet et spil. Start et nyt spil først.")),
                LobbyRestartStatus.StaleGame => Results.Conflict(new ApiErrorResponse("Spillet er ikke længere aktuelt. Hent spiltilstanden igen.")),
                LobbyRestartStatus.NotLobbyGame => Results.Conflict(new ApiErrorResponse("Der er ikke et startet lobbyspil at genstarte.")),
                LobbyRestartStatus.UnknownPlayer => Results.Conflict(new ApiErrorResponse("Spilleren deltager ikke i dette lobbyspil.")),
                LobbyRestartStatus.NotCompleted => Results.Conflict(new ApiErrorResponse("Spillet skal være afsluttet før omkamp.")),
                LobbyRestartStatus.Disconnected => Results.Conflict(new ApiErrorResponse("Alle tilmeldte spillere skal være forbundet før omkamp.")),
                _ => throw new InvalidOperationException("Ukendt resultat fra omkamp.")
            };
        }
    }
}
