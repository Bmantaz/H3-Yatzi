namespace Yatzi.Core;

/// <summary>
/// De 15 valgbare pointfelter i kernens spilleregler, i pointtavlens rækkefølge.
/// Ones–Sixes har samme værdi som terningernes øjental. Bonus og summer beregnes
/// særskilt; oversættelsen til HTTP-kontraktens tekst-id'er hører til i API-laget.
/// </summary>
public enum ScoreCategory
{
    Ones = 1,
    Twos = 2,
    Threes = 3,
    Fours = 4,
    Fives = 5,
    Sixes = 6,
    OnePair = 7,
    TwoPairs = 8,
    ThreeOfAKind = 9,
    FourOfAKind = 10,
    SmallStraight = 11,
    LargeStraight = 12,
    FullHouse = 13,
    Chance = 14,
    Yatzy = 15
}
