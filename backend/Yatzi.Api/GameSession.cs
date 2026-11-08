using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// Ejer serverens spil i hukommelsen: enten én selvstændig spiller eller lobbyens
/// 1–6 deltagere. Hver deltager har sin egen Game med tur og pointtavle. Første
/// deltager er aktiv indtil #44 tilføjer turskifte. HTTP og Oplà deler samme lås.
/// </summary>
public sealed class GameSession
{
    private readonly object _sync = new();
    public const int MaxPlayers = 6;
    private SessionPlayer[] _players = [];
    private bool _lobbyGame;
    private Game? ActiveGame => _players.Length == 0 ? null : _players[0].Game;

    /// <summary>
    /// Erstatter et selvstændigt spil og navn samlet og returnerer en frisk starttilstand.
    /// Et lobbyspil kan ikke nulstilles gennem denne indgang.
    /// </summary>
    /// <param name="name">Et allerede valideret og trimmet navn på 1–20 tegn.</param>
    /// <returns>Et selvstændigt øjebliksbillede med nul brugte slag.</returns>
    /// <exception cref="InvalidOperationException">Et lobbyspil er allerede startet.</exception>
    public GameStateResponse NewGame(string name)
    {
        lock (_sync)
        {
            if (_lobbyGame) throw new InvalidOperationException("Et lobbyspil er allerede startet og kan ikke nulstilles her.");
            _players = [new SessionPlayer(Guid.Empty, name, new Game())];
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Opretter lobbyens deltagere samlet i accepteret rækkefølge. Navne er allerede
    /// valideret af lobby-API'et; id'er skal være ikke-tomme og unikke. Hver Game
    /// opretter sin egen pointtavle. Et tidligere enkeltspillerspil erstattes først,
    /// når hele deltagerlisten er klar. Et lobbyspil kan ikke startes igen.
    /// </summary>
    /// <remarks>
    /// Lobbyen kalder under sin lås; rækkefølgen er lobby → spilsession. Der kaldes
    /// aldrig tilbage til lobbyen eller netværket her. Annullering kontrolleres også
    /// efter ventetid på sessionslåsen og lige før den samlede tilstandsændring.
    /// </remarks>
    public GameStateResponse NewLobbyGame(IReadOnlyList<LobbyGamePlayer> players, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lobbyGame) throw new InvalidOperationException("Et lobbyspil er allerede startet.");
            if (players.Count is < 1 or > MaxPlayers)
                throw new ArgumentException("Et lobbyspil skal have mellem 1 og 6 spillere.", nameof(players));
            if (players.Any(player => player.Id == Guid.Empty) || players.Select(player => player.Id).Distinct().Count() != players.Count)
                throw new ArgumentException("Lobbyens spiller-id'er skal være ikke-tomme og unikke.", nameof(players));

            var participants = players.Select(player => new SessionPlayer(player.Id, player.Name, new Game())).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            _players = participants;
            _lobbyGame = true;
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Læser spillet uden at ændre det. Samme lås som ved oprettelse sikrer,
    /// at et svar aldrig blander spillernavnet fra ét spil med et andet spil.
    /// </summary>
    /// <returns>Et selvstændigt øjebliksbillede eller null, hvis intet spil er oprettet.</returns>
    public GameStateResponse? GetState()
    {
        lock (_sync)
        {
            return ActiveGame is null ? null : CreateSnapshot();
        }
    }

    /// <summary>
    /// Kaster den aktive første deltagers terninger og returnerer tilstanden efter kastet.
    /// Kast og kopiering sker under samme lås som oprettelse og læsning, så samtidige
    /// requests ikke kan bruge samme slagnummer eller overskride turens tre slag.
    /// </summary>
    /// <returns>Et selvstændigt øjebliksbillede eller null, hvis intet spil er oprettet.</returns>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, eller tre slag er brugt. Tilstanden bevares.</exception>
    public GameStateResponse? Roll()
    {
        lock (_sync) return RollCore();
    }

    /// <summary>
    /// Skifter én ternings hold-status gennem kernens regler og returnerer tilstanden.
    /// Samme lås som ved kast beskytter hele handlingen og kopieringen af svaret.
    /// </summary>
    /// <param name="index">Kernens indeks 0–4. API'et validerer og oversætter terningenummeret før kaldet.</param>
    /// <returns>Et selvstændigt øjebliksbillede eller null, hvis intet spil er oprettet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Indekset er uden for 0–4 i et oprettet spil.</exception>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, eller hold/slip afvises før første eller efter tredje kast.</exception>
    public GameStateResponse? ToggleHold(int index)
    {
        lock (_sync) return ToggleHoldCore(index);
    }

    /// <summary>
    /// Oplà-indgang med servervalideret identitet. Lobbyspil kræver den aktive
    /// deltagers id; null bevarer den selvstændige handlers enkeltspillerbrug.
    /// Identitet, annullering, kast og svarkopi kontrolleres under samme lås.
    /// </summary>
    /// <param name="playerId">Servervalideret spillerbinding eller null ved selvstændig enkeltspillerbrug.</param>
    /// <param name="cancellationToken">Annullering før ændring, også efter ventetid på låsen.</param>
    /// <returns>Kopieret spiltilstand efter kastet eller null uden et oprettet spil.</returns>
    /// <exception cref="InvalidOperationException">Spilleren har ikke tur, eller kernen afviser kastet.</exception>
    /// <exception cref="OperationCanceledException">Handlingen er annulleret før spilændringen.</exception>
    public GameStateResponse? RollFromOpla(Guid? playerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOplaPlayer(playerId);
            return RollCore();
        }
    }

    /// <summary>
    /// Skifter hold/slip for indeks 0–4 med samme identitets- og annulleringskontrol
    /// som Oplà-kast. HTTP bruger fortsat den særskilte ToggleHold-indgang.
    /// </summary>
    /// <param name="index">Kernens terningeindeks 0–4.</param>
    /// <param name="playerId">Servervalideret spillerbinding eller null ved selvstændig enkeltspillerbrug.</param>
    /// <param name="cancellationToken">Annullering før ændring, også efter ventetid på låsen.</param>
    /// <returns>Kopieret spiltilstand efter hold/slip eller null uden et oprettet spil.</returns>
    /// <exception cref="InvalidOperationException">Spilleren har ikke tur, eller kernen afviser hold/slip.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Indekset er uden for 0–4 i et oprettet spil.</exception>
    /// <exception cref="OperationCanceledException">Handlingen er annulleret før spilændringen.</exception>
    public GameStateResponse? ToggleHoldFromOpla(int index, Guid? playerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOplaPlayer(playerId);
            return ToggleHoldCore(index);
        }
    }

    // Kun under sessionslåsen. Et identitetsløst handlerkald må ikke omgå
    // lobbyens spillerkontrol. Før start afviser LobbySession sine tilmeldte
    // spillere; øvrig selvstændig enkeltspillerbrug bevarer den gamle kontrakt.
    private void ValidateOplaPlayer(Guid? playerId)
    {
        if (_lobbyGame && (playerId is null || playerId == Guid.Empty || playerId != _players[0].Id))
            throw new InvalidOperationException("Det er ikke denne spillers tur.");
    }

    // Fælles kernekald for HTTP og Oplà. Kalderen holder altid sessionslåsen;
    // Oplà-indgangen har desuden kontrolleret identiteten under samme lås.
    private GameStateResponse? RollCore()
    {
        if (ActiveGame is null) return null;
        ActiveGame.Roll();
        return CreateSnapshot();
    }

    private GameStateResponse? ToggleHoldCore(int index)
    {
        if (ActiveGame is null) return null;
        ActiveGame.ToggleHold(index);
        return CreateSnapshot();
    }

    /// <summary>
    /// Registrerer point fra første deltagers egne terninger og afslutter dennes tur.
    /// Turkontrol, lagring og kopiering sker under samme lås som kast og hold/slip,
    /// så to samtidige feltvalg tilsammen kun kan udfylde ét felt.
    /// </summary>
    /// <param name="category">Et felt, som HTTP-laget har oversat fra et gyldigt tekst-id.</param>
    /// <returns>Et selvstændigt øjebliksbillede eller null uden et oprettet spil.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt i et oprettet spil.</exception>
    /// <exception cref="InvalidOperationException">Kernen afviser feltvalget; tilstanden bevares.</exception>
    public GameStateResponse? Score(ScoreCategory category)
    {
        lock (_sync)
        {
            if (ActiveGame is null)
            {
                return null;
            }

            ActiveGame.Score(category);
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Gemmer 0 gennem kernens strygning og afslutter turen under den fælles lås.
    /// Pointtavlen og hele svaret kopieres, før andre handlinger kan få adgang.
    /// </summary>
    /// <param name="category">Et felt, som HTTP-laget har oversat fra et gyldigt tekst-id.</param>
    /// <returns>Et selvstændigt øjebliksbillede eller null uden et oprettet spil.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt i et oprettet spil.</exception>
    /// <exception cref="InvalidOperationException">Kernen afviser feltvalget; tilstanden bevares.</exception>
    public GameStateResponse? Strike(ScoreCategory category)
    {
        lock (_sync)
        {
            if (ActiveGame is null)
            {
                return null;
            }

            ActiveGame.Strike(category);
            return CreateSnapshot();
        }
    }

    // Kopierne laves under låsen. Et svar deler derfor ingen arrays eller dictionaries med spillet
    // og kan serialiseres efter låsen er frigivet, uden at tilstanden flytter sig.
    private GameStateResponse CreateSnapshot()
    {
        var active = ActiveGame!;
        return new(
            _players.Select(player => new PlayerResponse(player.Name, new ScorecardResponse(
                player.Game.Scores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
                player.Game.UpperSum, player.Game.Bonus, player.Game.Total))).ToArray(),
            0,
            active.Dice.ToArray(),
            active.Held.ToArray(),
            active.RollsUsed,
            active.PossibleScores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
            active.TurnCompleted);
    }

    // Game indkapsler én deltagers tur og pointtavle. Identiteten bruges til
    // Oplà-spillerkontrollen og udstilles ikke som et nyt spil-JSON-felt.
    private sealed record SessionPlayer(Guid Id, string Name, Game Game);
}
