using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// Ejer serverens spil i hukommelsen: enten én selvstændig spiller eller lobbyens
/// 1–6 deltagere. Hver deltager har sin egen Game med tur og pointtavle. Feltvalg
/// flytter turen i deltagerrækkefølge. Sessionen ejer spil-id, turnummer og det
/// samlede slutresultat. HTTP og Oplà deler samme lås.
/// </summary>
public sealed class GameSession
{
    private readonly object _sync = new();
    private readonly Random _random;
    public const int MaxPlayers = 6;
    private SessionPlayer[] _players = [];
    private bool _lobbyGame;
    private int _currentPlayer;
    private Guid _gameId;
    private int _turnNumber;
    private Game? ActiveGame => _players.Length == 0 ? null : _players[_currentPlayer].Game;
    // Læses kun under sessionslåsen. Intet ekstra statusflag kan komme ud af takt med tavlerne.
    private bool IsGameCompleted => _players.Length > 0 && _players.All(player => player.Game.IsScorecardComplete);

    /// <summary>Opretter en session med .NETs fælles tilfældighedskilde som hidtil.</summary>
    public GameSession() : this(Random.Shared) { }

    /// <summary>
    /// Lader sessionstests styre kast gennem samme Game-metoder som produktionen.
    /// Kilden bevares ved nye spil og bruges kun af kernens accepterede kast.
    /// </summary>
    /// <param name="random">En ikke-null tilfældighedskilde til deltagernes kast.</param>
    /// <exception cref="ArgumentNullException">Kilden er null.</exception>
    internal GameSession(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

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
            _players = [new SessionPlayer(Guid.Empty, name, new Game(_random))];
            _currentPlayer = 0;
            _gameId = Guid.NewGuid();
            _turnNumber = 1;
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Opretter lobbyens deltagere samlet i accepteret rækkefølge. Navne er allerede
    /// valideret af lobby-API'et; id'er skal være ikke-tomme og unikke. Hver Game
    /// opretter sin egen pointtavle. Et tidligere enkeltspillerspil erstattes først,
    /// når hele deltagerlisten er klar. Omkamp bruger sin egen kontrollerede indgang.
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

            var participants = players.Select(player => new SessionPlayer(player.Id, player.Name, new Game(_random))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            _players = participants;
            _currentPlayer = 0;
            _gameId = Guid.NewGuid();
            _turnNumber = 1;
            _lobbyGame = true;
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Erstatter kun et afsluttet, aktuelt lobbyspil. Lobbyen holder sin lås og har
    /// kontrolleret forbindelserne; her kontrolleres spilrelation og nulstilling samlet.
    /// Afvisning eller annullering før accept bevarer også det tidligere slutresultat.
    /// </summary>
    /// <param name="context">Formatvalideret spil-id og initiativtagerens spiller-id.</param>
    /// <param name="allPlayersConnected">Lobbyens kontrol af alle aktuelle, åbne spillerbindinger.</param>
    /// <param name="cancellationToken">Kontrolleres også efter ventetid på låsen og lige før accept.</param>
    /// <returns>Typet afvisning eller omkampens selvstændige starttilstand.</returns>
    internal LobbyRestartResult RestartLobbyGame(LobbyRestartContext context, bool allPlayersConnected,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActiveGame is null) return new(LobbyRestartStatus.MissingGame);
            if (context.GameId != _gameId) return new(LobbyRestartStatus.StaleGame);
            if (!_lobbyGame) return new(LobbyRestartStatus.NotLobbyGame);
            if (!_players.Any(player => player.Id == context.PlayerId)) return new(LobbyRestartStatus.UnknownPlayer);
            if (!IsGameCompleted) return new(LobbyRestartStatus.NotCompleted);
            if (!allPlayersConnected) return new(LobbyRestartStatus.Disconnected);

            // Nye Games bruger samme tilfældighedskilde, men konstruktøren kaster ikke.
            // De gamle tavler røres først, når hele omkampen er klar til accept.
            var participants = _players.Select(player => new SessionPlayer(player.Id, player.Name, new Game(_random))).ToArray();
            Guid gameId = Guid.NewGuid();
            cancellationToken.ThrowIfCancellationRequested();
            _players = participants;
            _gameId = gameId;
            _currentPlayer = 0;
            _turnNumber = 1;
            return new(LobbyRestartStatus.Restarted, CreateSnapshot());
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
    /// Kaster den aktive deltagers terninger og returnerer tilstanden efter kastet.
    /// Kast og kopiering sker under samme lås som oprettelse og læsning, så samtidige
    /// requests ikke kan bruge samme slagnummer eller overskride turens tre slag.
    /// </summary>
    /// <returns>Et selvstændigt øjebliksbillede eller null, hvis intet spil er oprettet.</returns>
    /// <exception cref="InvalidOperationException">Spillet er afsluttet, eller tre slag er brugt. Tilstanden bevares.</exception>
    internal GameStateResponse? Roll()
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
    /// <exception cref="InvalidOperationException">Spillet er afsluttet, eller hold/slip afvises før første eller efter tredje kast.</exception>
    internal GameStateResponse? ToggleHold(int index)
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
    /// <exception cref="InvalidOperationException">Spilleren har ikke tur, spillet er afsluttet, eller kernen afviser kastet.</exception>
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
    /// som Oplà-kast. HTTP bruger samme kernekald med særskilt spil-/turkontekst.
    /// </summary>
    /// <param name="index">Kernens terningeindeks 0–4.</param>
    /// <param name="playerId">Servervalideret spillerbinding eller null ved selvstændig enkeltspillerbrug.</param>
    /// <param name="cancellationToken">Annullering før ændring, også efter ventetid på låsen.</param>
    /// <returns>Kopieret spiltilstand efter hold/slip eller null uden et oprettet spil.</returns>
    /// <exception cref="InvalidOperationException">Spilleren har ikke tur, spillet er afsluttet, eller kernen afviser hold/slip.</exception>
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
        if (_lobbyGame && (playerId is null || playerId == Guid.Empty || playerId != _players[_currentPlayer].Id))
            throw new InvalidOperationException("Det er ikke denne spillers tur.");
    }

    // Fælles kernekald for HTTP og Oplà. Kalderen holder altid sessionslåsen;
    // Oplà-indgangen har desuden kontrolleret identiteten under samme lås.
    private GameStateResponse? RollCore()
    {
        if (ActiveGame is null) return null;
        EnsureGameActive();
        ActiveGame.Roll();
        return CreateSnapshot();
    }

    private GameStateResponse? ToggleHoldCore(int index)
    {
        if (ActiveGame is null) return null;
        EnsureGameActive();
        ActiveGame.ToggleHold(index);
        return CreateSnapshot();
    }

    /// <summary>
    /// Registrerer point fra den aktive deltagers terninger og åbner næste tur,
    /// medmindre alle tavler dermed er fulde og spillet afsluttes.
    /// Kontrol, lagring, rotation og svarkopi sker under samme lås som kast og hold.
    /// Uden et mellemliggende kast kan et konkurrerende feltvalg ikke bruge den nye tur.
    /// </summary>
    /// <param name="category">Et felt, som HTTP-laget har oversat fra et gyldigt tekst-id.</param>
    /// <returns>Et selvstændigt øjebliksbillede eller null uden et oprettet spil.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt i et oprettet spil.</exception>
    /// <exception cref="InvalidOperationException">Spillet er afsluttet, eller kernen afviser feltvalget; tilstanden bevares.</exception>
    internal GameStateResponse? Score(ScoreCategory category)
    {
        lock (_sync) return ScoreCore(category, strike: false);
    }

    /// <summary>
    /// Gemmer 0 gennem kernens strygning og åbner næste tur eller afslutter spillet.
    /// Pointtavler, rotation og svarkopi er samlet, før andre handlinger får adgang.
    /// </summary>
    /// <param name="category">Et felt, som HTTP-laget har oversat fra et gyldigt tekst-id.</param>
    /// <returns>Et selvstændigt øjebliksbillede eller null uden et oprettet spil.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt i et oprettet spil.</exception>
    /// <exception cref="InvalidOperationException">Spillet er afsluttet, eller kernen afviser feltvalget; tilstanden bevares.</exception>
    internal GameStateResponse? Strike(ScoreCategory category)
    {
        lock (_sync) return ScoreCore(category, strike: true);
    }

    // Interne kontekstfri indgange ovenfor bruges af rene sessionstests. Produktionens
    // HTTP går gennem disse kontekstkrævende indgange eller lobbyens fælles koordinering.
    /// <summary>Kaster efter atomisk kontrol af HTTP-kontekst; se ExecuteHttpAction.</summary>
    public GameActionResult Roll(GameActionContext context, CancellationToken cancellationToken = default) =>
        ExecuteHttpAction(GameActionKind.Roll, context, cancellationToken);

    /// <summary>Skifter hold/slip for indeks 0–4 efter atomisk kontrol af HTTP-kontekst.</summary>
    public GameActionResult ToggleHold(int index, GameActionContext context, CancellationToken cancellationToken = default) =>
        ExecuteHttpAction(GameActionKind.ToggleHold, context, cancellationToken, index);

    /// <summary>Gemmer point og åbner næste tur eller afslutter spillet med valideret HTTP-kontekst.</summary>
    public GameActionResult Score(ScoreCategory category, GameActionContext context, CancellationToken cancellationToken = default) =>
        ExecuteHttpAction(GameActionKind.Score, context, cancellationToken, category: category);

    /// <summary>Gemmer 0 og åbner næste tur eller afslutter spillet med valideret HTTP-kontekst.</summary>
    public GameActionResult Strike(ScoreCategory category, GameActionContext context, CancellationToken cancellationToken = default) =>
        ExecuteHttpAction(GameActionKind.Strike, context, cancellationToken, category: category);

    /// <summary>
    /// Kontrollerer kontekst, regler, ændring og svarkopi som én operation. HTTP-laget har
    /// valideret felternes format og ruteinput. Annullering før ændring bevarer alt; ingen
    /// kontrol efter accept kan fortryde handlingen. Der kaldes aldrig ud til netværk/lobby.
    /// </summary>
    internal GameActionResult ExecuteHttpAction(GameActionKind action, GameActionContext context,
        CancellationToken cancellationToken, int index = 0, ScoreCategory category = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejection = ValidateHttpContext(context);
            if (rejection is not null) return rejection;
            try
            {
                var state = action switch
                {
                    GameActionKind.Roll => RollCore(),
                    GameActionKind.ToggleHold => ToggleHoldCore(index),
                    GameActionKind.Score => ScoreCore(category, strike: false),
                    GameActionKind.Strike => ScoreCore(category, strike: true),
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                };
                return new(GameActionStatus.Accepted, state);
            }
            catch (InvalidOperationException exception)
            {
                return new(GameActionStatus.Conflict, Message: exception.Message);
            }
        }
    }

    // Rækkefølgen følger docs/api.md. Formatkontrollen ligger ved HTTP-grænsen;
    // null-playerId i lobby kræver derimod viden om spillet og kontrolleres her.
    private GameActionResult? ValidateHttpContext(GameActionContext context)
    {
        if (ActiveGame is null)
            return new(GameActionStatus.MissingGame, Message: "Der er ikke oprettet et spil. Start et nyt spil først.");
        if (_lobbyGame && context.PlayerId is null)
            return new(GameActionStatus.InvalidLobbyPlayerId, Message: "playerId skal være et gyldigt, ikke-tomt GUID i et lobbyspil.");
        if (context.GameId != _gameId)
            return new(GameActionStatus.Conflict, Message: "Spillet er ikke længere aktuelt. Hent spiltilstanden igen.");
        if (!_lobbyGame && context.PlayerId is not null)
            return new(GameActionStatus.Conflict, Message: "Enkeltspillerhandlinger skal bruge playerId null.");
        if (_lobbyGame && !_players.Any(player => player.Id == context.PlayerId))
            return new(GameActionStatus.Conflict, Message: "Spilleren deltager ikke i dette lobbyspil.");
        if (IsGameCompleted)
            return new(GameActionStatus.Conflict, Message: "Spillet er afsluttet. Start et nyt spil for at fortsætte.");
        if (context.TurnNumber != _turnNumber)
            return new(GameActionStatus.Conflict, Message: "Turen er ikke længere aktuel. Hent spiltilstanden igen.");
        if (_lobbyGame && context.PlayerId != _players[_currentPlayer].Id)
            return new(GameActionStatus.Conflict, Message: "Det er ikke denne spillers tur.");
        return null;
    }

    // Kun under sessionslåsen. Fælles registrering bruges af begge HTTP-feltvalg og tests.
    private GameStateResponse? ScoreCore(ScoreCategory category, bool strike)
    {
        if (ActiveGame is null) return null;
        EnsureGameActive();
        if (strike) ActiveGame.Strike(category); else ActiveGame.Score(category);
        AdvanceTurn();
        return CreateSnapshot();
    }

    /// <summary>
    /// Flytter turen efter et accepteret feltvalg. Kalderen holder sessionslåsen.
    /// Ved sidste felt bevares slutkast og aktivt indeks, så et allerede gemt felt
    /// ikke efterfølges af fejl fra turstart på en fuld tavle. Kun en ny tur øger turnummeret.
    /// </summary>
    private void AdvanceTurn()
    {
        if (IsGameCompleted) return;

        int next = (_currentPlayer + 1) % _players.Length;
        var game = _players[next].Game;
        // Førstegangsturen er allerede åben fra konstruktøren. Kun en spiller,
        // som har afsluttet sin tidligere tur, skal nulstille sine turdata.
        if (game.TurnCompleted) game.StartNextTurn();
        _currentPlayer = next;
        _turnNumber++;
    }

    // Kaldes efter spilopslag og eventuel Oplà-identitetskontrol, altid under samme lås
    // som handlingen. Afvisningen må hverken bruge tilfældighed eller ændre slutresultatet.
    private void EnsureGameActive()
    {
        if (IsGameCompleted)
            throw new InvalidOperationException("Spillet er afsluttet. Start et nyt spil for at fortsætte.");
    }

    // Kopierne laves under låsen. Et svar deler derfor ingen arrays eller dictionaries med spillet
    // og kan serialiseres efter låsen er frigivet, uden at tilstanden flytter sig.
    private GameStateResponse CreateSnapshot()
    {
        var active = ActiveGame!;
        bool completed = IsGameCompleted;
        int highestTotal = completed ? _players.Max(player => player.Game.Total) : 0;
        int[] winners = completed
            ? Enumerable.Range(0, _players.Length).Where(index => _players[index].Game.Total == highestTotal).ToArray()
            : [];
        return new(
            _players.Select(player => new PlayerResponse(player.Name, new ScorecardResponse(
                player.Game.Scores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
                player.Game.UpperSum, player.Game.Bonus, player.Game.Total))).ToArray(),
            _currentPlayer,
            active.Dice.ToArray(),
            active.Held.ToArray(),
            active.RollsUsed,
            active.PossibleScores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
            active.TurnCompleted,
            _gameId,
            _turnNumber,
            completed,
            winners);
    }

    // Game indkapsler én deltagers tur og pointtavle. Identiteten bruges til
    // HTTP-/Oplà-spillerkontrollen og udstilles ikke som et nyt spil-JSON-felt.
    private sealed record SessionPlayer(Guid Id, string Name, Game Game);
}
