using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// Ejer serverens ene spil og spillernavn i hukommelsen. Registreres som singleton,
/// så HTTP-API'et og Oplà-handleren arbejder på samme spil; en ny serverinstans starter uden spil.
/// HTTP-validering ligger i GameEndpoints, og spillereglerne ligger i Game.
/// </summary>
public sealed class GameSession
{
    private readonly object _sync = new();
    private Game? _game;
    private string _playerName = string.Empty;

    /// <summary>
    /// Erstatter det aktuelle spil og navn samlet og returnerer en frisk starttilstand.
    /// </summary>
    /// <param name="name">Et allerede valideret og trimmet navn på 1–20 tegn.</param>
    /// <returns>Et selvstændigt øjebliksbillede med nul brugte slag.</returns>
    public GameStateResponse NewGame(string name)
    {
        lock (_sync)
        {
            _game = new Game();
            _playerName = name;
            return CreateSnapshot(_game, _playerName);
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
            return _game is null ? null : CreateSnapshot(_game, _playerName);
        }
    }

    /// <summary>
    /// Kaster terningerne gennem kernens regler og returnerer tilstanden efter kastet.
    /// Kast og kopiering sker under samme lås som oprettelse og læsning, så samtidige
    /// requests ikke kan bruge samme slagnummer eller overskride turens tre slag.
    /// </summary>
    /// <returns>Et selvstændigt øjebliksbillede eller null, hvis intet spil er oprettet.</returns>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, eller tre slag er brugt. Tilstanden bevares.</exception>
    public GameStateResponse? Roll()
    {
        lock (_sync)
        {
            if (_game is null)
            {
                return null;
            }

            _game.Roll();
            return CreateSnapshot(_game, _playerName);
        }
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
        lock (_sync)
        {
            if (_game is null)
            {
                return null;
            }

            _game.ToggleHold(index);
            return CreateSnapshot(_game, _playerName);
        }
    }

    /// <summary>
    /// Registrerer beregnede point fra spillets egne terninger og afslutter turen.
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
            if (_game is null)
            {
                return null;
            }

            _game.Score(category);
            return CreateSnapshot(_game, _playerName);
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
            if (_game is null)
            {
                return null;
            }

            _game.Strike(category);
            return CreateSnapshot(_game, _playerName);
        }
    }

    // Kopierne laves under låsen. Et svar deler derfor ingen arrays eller dictionaries med spillet
    // og kan serialiseres efter låsen er frigivet, uden at tilstanden flytter sig.
    private static GameStateResponse CreateSnapshot(Game game, string playerName) => new(
        new[]
        {
            new PlayerResponse(playerName, new ScorecardResponse(
                game.Scores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
                game.UpperSum,
                game.Bonus,
                game.Total))
        },
        0,
        game.Dice.ToArray(),
        game.Held.ToArray(),
        game.RollsUsed,
        game.PossibleScores.ToDictionary(entry => ScoreCategoryIds.ToId(entry.Key), entry => entry.Value, StringComparer.Ordinal),
        game.TurnCompleted);
}
