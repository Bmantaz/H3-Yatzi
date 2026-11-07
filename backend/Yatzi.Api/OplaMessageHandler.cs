namespace Yatzi.Api;

/// <summary>
/// Behandler Oplàens ROLL- og HOLD-linjer i API-laget og returnerer STATE-svar.
/// Deler GameSession med HTTP-API'et og er uafhængig af netværk og hardware.
/// /ws/opla kalder HandleLine efter adgangskontrol og sender svarene til Oplàen.
/// </summary>
public sealed class OplaMessageHandler
{
    private readonly GameSession _session;
    private readonly ILogger<OplaMessageHandler> _logger;

    /// <summary>
    /// Modtager den fælles spilsession og logger gennem dependency injection.
    /// Handleren gemmer ingen spiller- eller forbindelsesspecifik tilstand.
    /// </summary>
    /// <param name="session">Samme spilsession som HTTP-API'et bruger.</param>
    /// <param name="logger">Logger til beskedhåndteringens afvisninger.</param>
    /// <exception cref="ArgumentNullException">Sessionen eller loggeren er null.</exception>
    public OplaMessageHandler(GameSession session, ILogger<OplaMessageHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Behandler ROLL og HOLD n gennem sessionens metoder og returnerer STATE.
    /// HOLD bruger terningenumre 1–5 og skifter mellem hold og slip.
    /// Et manglende spil eller en afvist spilhandling logges og giver intet svar.
    /// Debuglinjer med # først ignoreres uden logning. Ugyldigt input logges
    /// og ignoreres uden at ændre spiltilstanden.
    /// </summary>
    /// <param name="line">Én tekstlinje uden afsluttende linjeskift. Null behandles som ugyldigt input.</param>
    /// <returns>Én STATE-linje ved en accepteret handling; ellers en tom samling. Linjerne har intet afsluttende linjeskift.</returns>
    public IReadOnlyList<string> HandleLine(string? line)
    {
        // Debug kan indeholde kommandonavne, men skal aldrig udføre en spilhandling.
        if (line?.StartsWith('#') == true)
        {
            return Array.Empty<string>();
        }

        bool isRoll = line == "ROLL";
        int dieIndex = -1;
        if (!isRoll)
        {
            if (line is not { Length: 6 } || !line.StartsWith("HOLD ", StringComparison.Ordinal)
                || line[5] is < '1' or > '5')
            {
                // Længden giver kontekst uden at skrive ukendt tekst, mulige
                // hemmeligheder eller indlejrede linjeskift i loggen.
                _logger.LogWarning(
                    "Oplà-beskeden blev afvist: Ukendt kommando eller ugyldigt format. Længde: {Length}.",
                    line?.Length);
                return Array.Empty<string>();
            }

            // Det præcise format sikrer ét gyldigt ciffer. Numrene 1–5
            // oversættes til kernens indeks 0–4, før sessionen kaldes.
            dieIndex = line[5] - '1';
        }

        GameStateResponse? state;
        try
        {
            // Sessionen udfører handlingen og kopierer tilstanden under samme lås.
            // Svaret bygges fra denne kopi frem for et senere GetState-kald,
            // som kunne læse en anden klients efterfølgende handling.
            state = isRoll ? _session.Roll() : _session.ToggleHold(dieIndex);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning("Oplà-beskeden {Line} blev afvist: {Reason}", line, exception.Message);
            return Array.Empty<string>();
        }

        if (state is null)
        {
            _logger.LogWarning("Oplà-beskeden {Line} blev afvist: Der er ikke oprettet et spil.", line);
            return Array.Empty<string>();
        }

        return new[] { FormatState(state) };
    }

    /// <summary>
    /// Omsætter handlingens øjebliksbillede til STATE r hhhhh efter protokollen.
    /// Hold-markeringerne følger terning 1–5; terningeværdierne vises gennem HTTP-API'et.
    /// </summary>
    private static string FormatState(GameStateResponse state)
    {
        string held = string.Concat(state.Held.Select(value => value ? '1' : '0'));
        return $"STATE {state.RollsUsed} {held}";
    }
}
