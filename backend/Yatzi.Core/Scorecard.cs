using System.Collections.ObjectModel;

namespace Yatzi.Core;

/// <summary>
/// Ejer én spillers 15 pointfelter og beregner summer og bonus fra de gemte point.
/// ScoreCalculator bestemmer pointene; Game kontrollerer kast og tur før feltvalg.
/// Klassen har ingen egen lås. GameSession beskytter den fælles adgang fra API-laget.
/// </summary>
public sealed class Scorecard
{
    private readonly Dictionary<ScoreCategory, int?> _scores;

    /// <summary>
    /// Løbende, skrivebeskyttet visning af alle 15 felter. Null betyder ledigt;
    /// ethvert gemt heltal, inklusive 0, betyder udfyldt og låst.
    /// Visningen følger tavlen; API-laget kopierer data til hvert HTTP-svar under sessionens lås.
    /// </summary>
    public IReadOnlyDictionary<ScoreCategory, int?> Scores { get; }

    /// <summary>
    /// Summen af gemte point i Ones–Sixes. Ledige felter bidrager med 0.
    /// </summary>
    public int UpperSum => _scores
        .Where(entry => (int)entry.Key <= (int)ScoreCategory.Sixes)
        .Sum(entry => entry.Value ?? 0);

    /// <summary>
    /// 50 point fra det øjeblik den øvre sum når mindst 63, ellers 0.
    /// De seks øvre felter behøver ikke alle være udfyldt.
    /// </summary>
    public int Bonus => UpperSum >= 63 ? 50 : 0;

    /// <summary>
    /// Alle gemte feltpoint plus bonus én gang. Beregnes ved læsning,
    /// så registreringer og gentagne læsninger ikke kan akkumulere bonus igen.
    /// </summary>
    public int Total => _scores.Values.Sum(score => score ?? 0) + Bonus;

    /// <summary>
    /// Opretter en selvstændig tavle med præcis ét ledigt felt for hver ScoreCategory.
    /// Kun denne klasses skrivemetoder får adgang til den underliggende dictionary.
    /// </summary>
    public Scorecard()
    {
        _scores = Enum.GetValues<ScoreCategory>()
            .ToDictionary(category => category, _ => (int?)null);
        Scores = new ReadOnlyDictionary<ScoreCategory, int?>(_scores);
    }

    /// <summary>
    /// Beregner og gemmer point fra et kast i et ledigt felt uden at ændre terningerne.
    /// Gemmer også 0 ved manglende kombination. Hele tavlen bevares ved afvisning.
    /// Metoden kender ingen turtilstand; Game skal kontrollere den før kaldet.
    /// </summary>
    /// <param name="dice">Præcis fem terningeværdier fra 1 til 6 til ScoreCalculator.</param>
    /// <param name="category">Det kendte og ledige felt, der skal udfyldes.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt, eller en terning er uden for 1–6.</exception>
    /// <exception cref="InvalidOperationException">Feltet er allerede udfyldt, også hvis værdien er 0.</exception>
    /// <exception cref="ArgumentNullException">Terningelisten til et ledigt felt er null.</exception>
    /// <exception cref="ArgumentException">Kastet til et ledigt felt indeholder ikke præcis fem terninger.</exception>
    public void Record(IReadOnlyList<int> dice, ScoreCategory category)
    {
        EnsureAvailable(category);

        // Gem først efter vellykket beregning. En inputfejl må ikke låse feltet,
        // og kun de beregnede heltalspoint gemmes, uden reference til terningerne.
        int score = ScoreCalculator.Calculate(dice, category);
        _scores[category] = score;
    }

    /// <summary>
    /// Gemmer 0 og låser et ledigt felt. Game skal kontrollere kast og tur før kaldet;
    /// selve tavlen kan stryge et felt uden terninger. Afvisninger bevarer hele tavlen.
    /// </summary>
    /// <param name="category">Det kendte og ledige felt, der skal stryges.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt.</exception>
    /// <exception cref="InvalidOperationException">Feltet er allerede udfyldt.</exception>
    public void Strike(ScoreCategory category)
    {
        EnsureAvailable(category);
        _scores[category] = 0;
    }

    /// <summary>
    /// Fælles kontrol før begge skrivemetoder. HasValue skelner mellem et ledigt
    /// felt og et låst felt med 0 point. Kontrollen ændrer ingen tilstand.
    /// </summary>
    /// <param name="category">Feltet, der ønskes udfyldt.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet findes ikke i tavlen.</exception>
    /// <exception cref="InvalidOperationException">Feltet har allerede et gemt resultat.</exception>
    private void EnsureAvailable(ScoreCategory category)
    {
        if (!_scores.TryGetValue(category, out int? score))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Pointfeltet er ukendt.");
        }

        if (score.HasValue)
        {
            throw new InvalidOperationException("Pointfeltet er allerede udfyldt.");
        }
    }
}
