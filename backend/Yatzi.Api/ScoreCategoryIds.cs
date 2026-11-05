using Yatzi.Core;

namespace Yatzi.Api;

/// <summary>
/// Oversætter mellem kernens felter og HTTP-kontraktens faste tekst-id'er.
/// Samme tabel styrer URL-validering og JSON-nøgler uden at ændre kernens enum.
/// </summary>
internal static class ScoreCategoryIds
{
    private static readonly Dictionary<ScoreCategory, string> Ids = new()
    {
        [ScoreCategory.Ones] = "ones",
        [ScoreCategory.Twos] = "twos",
        [ScoreCategory.Threes] = "threes",
        [ScoreCategory.Fours] = "fours",
        [ScoreCategory.Fives] = "fives",
        [ScoreCategory.Sixes] = "sixes",
        [ScoreCategory.OnePair] = "onePair",
        [ScoreCategory.TwoPairs] = "twoPairs",
        [ScoreCategory.ThreeOfAKind] = "threeOfAKind",
        [ScoreCategory.FourOfAKind] = "fourOfAKind",
        [ScoreCategory.SmallStraight] = "smallStraight",
        [ScoreCategory.LargeStraight] = "largeStraight",
        [ScoreCategory.FullHouse] = "fullHouse",
        [ScoreCategory.Chance] = "chance",
        [ScoreCategory.Yatzy] = "yatzy"
    };

    private static readonly Dictionary<string, ScoreCategory> Categories = Ids
        .ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);

    /// <summary>
    /// Finder et felt ved præcis stavning. Ukendt tekst giver false uden trimning
    /// eller accept af enum-tal, kombinerede navne og andre varianter.
    /// </summary>
    /// <param name="id">Det uændrede felt-id fra URL'en.</param>
    /// <param name="category">Det tilsvarende felt ved succes; ellers enum-værdien 0.</param>
    /// <returns>True for et præcist kendt id; opslaget ændrer ingen tilstand.</returns>
    public static bool TryParse(string id, out ScoreCategory category) => Categories.TryGetValue(id, out category);

    /// <summary>
    /// Finder JSON-nøglen for et kendt felt fra spillets tavle eller mulige point.
    /// </summary>
    /// <param name="category">Et kendt felt fra kernen.</param>
    /// <returns>Feltets faste tekst-id uden at ændre mapping eller spiltilstand.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Feltet findes ikke i kontraktens tabel.</exception>
    public static string ToId(ScoreCategory category) => Ids.TryGetValue(category, out string? id)
        ? id
        : throw new ArgumentOutOfRangeException(nameof(category), category, "Pointfeltet er ukendt.");
}
