namespace Yatzi.Core;

/// <summary>
/// Beregner point fra et kast i backendens kerne uden at ændre input eller spiltilstand.
/// Scorecard og Game bruger beregningen ved registrering og visning af mulige point.
/// Alle 15 pointfelter beregnes ud fra en lokal optælling af terningernes øjental.
/// </summary>
public static class ScoreCalculator
{
    private const int DiceCount = 5;

    /// <summary>
    /// Validerer og tæller alle fem terninger og beregner det valgte pointfelt.
    /// Værdien 0 fra et uslået spil er ugyldig. Manglende kombination giver 0 point,
    /// mens chance altid summerer de fem gyldige terninger.
    /// Beregningen bevarer input og rækkefølge og gemmer ingen tilstand mellem kald.
    /// </summary>
    /// <param name="dice">Præcis fem terningeværdier fra 1 til 6, eksempelvis fra Game.Dice.</param>
    /// <param name="category">Et af de 15 definerede pointfelter.</param>
    /// <returns>Pointene for feltet, eller 0 hvis et gyldigt kast ikke opfylder kombinationen.</returns>
    /// <exception cref="ArgumentNullException">Terningelisten er null.</exception>
    /// <exception cref="ArgumentException">Listen indeholder ikke præcis fem terninger.</exception>
    /// <exception cref="ArgumentOutOfRangeException">En terning er uden for 1–6, eller feltet er ikke defineret.</exception>
    public static int Calculate(IReadOnlyList<int> dice, ScoreCategory category)
    {
        if (dice is null)
        {
            throw new ArgumentNullException(nameof(dice), "Terningerne må ikke være null.");
        }

        if (dice.Count != DiceCount)
        {
            throw new ArgumentException("Kastet skal indeholde præcis fem terninger.", nameof(dice));
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Pointfeltet er ukendt.");
        }

        // Indeks 1–6 svarer til øjentallene. Optællingen tilhører kun dette kald,
        // så input kan være skrivebeskyttet, og tidligere kast ikke påvirker pointene.
        int[] counts = new int[7];
        int total = 0;
        for (int i = 0; i < DiceCount; i++)
        {
            int value = dice[i];
            if (value is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(dice), value,
                    $"Terning {i + 1} skal have en værdi mellem 1 og 6.");
            }

            // Valider før opslaget, også når en brugbar kombination allerede er fundet.
            counts[value]++;
            total += value;
        }

        // Enum-værdierne for de seks øvre felter svarer til øjentallet.
        int faceValue = (int)category;
        if (faceValue <= (int)ScoreCategory.Sixes)
        {
            return counts[faceValue] * faceValue;
        }

        return category switch
        {
            ScoreCategory.OnePair => CalculateMatchingGroups(counts, 2, 1),
            ScoreCategory.TwoPairs => CalculateMatchingGroups(counts, 2, 2),
            ScoreCategory.ThreeOfAKind => CalculateMatchingGroups(counts, 3, 1),
            ScoreCategory.FourOfAKind => CalculateMatchingGroups(counts, 4, 1),
            ScoreCategory.SmallStraight => IsStraight(counts, 1) ? 15 : 0,
            ScoreCategory.LargeStraight => IsStraight(counts, 2) ? 20 : 0,
            // Præcis 2 og 3 forekomster kræver forskellige øjental og afviser fem ens.
            ScoreCategory.FullHouse => counts.Contains(2) && counts.Contains(3) ? total : 0,
            ScoreCategory.Chance => total,
            ScoreCategory.Yatzy => counts.Contains(DiceCount) ? 50 : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Pointfeltet er ukendt.")
        };
    }

    /// <summary>
    /// Kontrollerer en straight uden sortering ved at kræve præcis én af hver værdi.
    /// Et valideret kast har fem terninger, så der kan ikke være andre værdier samtidig.
    /// </summary>
    /// <param name="counts">Optælling af et valideret kast ved indeks 1–6.</param>
    /// <param name="firstValue">1 for lille straight eller 2 for stor straight.</param>
    /// <returns>True hvis alle fem krævede værdier forekommer præcis én gang.</returns>
    private static bool IsStraight(int[] counts, int firstValue)
    {
        for (int value = firstValue; value < firstValue + DiceCount; value++)
        {
            if (counts[value] != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finder de højeste forskellige øjental med mindst den krævede gruppestørrelse.
    /// Hver værdi bruges højst én gang, så fire ens ikke kan tælles som to par.
    /// </summary>
    /// <param name="counts">Optælling af et valideret kast ved indeks 1–6; indeks 0 bruges ikke.</param>
    /// <param name="groupSize">Antal terninger pr. gruppe: 2, 3 eller 4.</param>
    /// <param name="groupsRequired">Antal forskellige grupper: 1 eller 2.</param>
    /// <returns>Summen af de krævede grupper, eller 0 hvis der mangler en gruppe.</returns>
    private static int CalculateMatchingGroups(int[] counts, int groupSize, int groupsRequired)
    {
        int score = 0;
        int groupsFound = 0;
        for (int value = 6; value >= 1; value--)
        {
            if (counts[value] >= groupSize)
            {
                score += value * groupSize;
                groupsFound++;
                if (groupsFound == groupsRequired)
                {
                    return score;
                }
            }
        }

        // Ét fundet par giver ikke delpoint i feltet to par.
        return 0;
    }
}
