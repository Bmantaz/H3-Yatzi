namespace Yatzi.Core;

/// <summary>
/// Spillets tilstand og regler i backendens kerne, uafhængigt af web og hardware.
/// Andre komponenter kan læse tilstanden, mens ændringer styres af Game.
/// </summary>
public class Game
{
    private const int DiceCount = 5;
    private const int MaxRolls = 3;

    private readonly int[] _dice = new int[DiceCount];
    private readonly bool[] _held = new bool[DiceCount];
    private readonly Random _random;

    /// <summary>
    /// Terningernes værdier ved indeks 0–4. Værdien 0 betyder endnu ikke kastet;
    /// efter et kast viser en terning en værdi fra 1 til 6.
    /// </summary>
    public IReadOnlyList<int> Dice { get; }

    /// <summary>
    /// Hold-status ved indeks 0–4 i samme rækkefølge som Dice.
    /// Alle terninger starter som ikke holdte.
    /// </summary>
    public IReadOnlyList<bool> Held { get; }

    /// <summary>
    /// Antal brugte slag i den aktuelle tur, fra 0 til 3. Et nyt spil starter med 0 slag.
    /// </summary>
    public int RollsUsed { get; private set; }

    /// <summary>
    /// Opretter en starttilstand med den fælles tilfældighedskilde fra .NET.
    /// </summary>
    public Game() : this(Random.Shared)
    {
    }

    /// <summary>
    /// Opretter en starttilstand med fem uslåede terninger og egne arrays pr. spilinstans.
    /// </summary>
    /// <param name="random">Tilfældighedskilden, som bruges ved kast og kan styres i tests.</param>
    /// <exception cref="ArgumentNullException">Tilfældighedskilden er null.</exception>
    public Game(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;

        // Visningerne følger de private arrays, så spilmetoderne kan ændre
        // tilstanden, uden at andre komponenter får direkte skriveadgang.
        Dice = Array.AsReadOnly(_dice);
        Held = Array.AsReadOnly(_held);
    }

    /// <summary>
    /// Kaster de ikke-holdte terninger og øger antallet af brugte slag én gang.
    /// Terningeværdierne opdateres i de eksisterende arrays; hold-status bevares.
    /// Et kast bruger stadig ét slag, hvis alle fem terninger er holdt.
    /// </summary>
    /// <exception cref="InvalidOperationException">Turens tre slag er allerede brugt.</exception>
    public void Roll()
    {
        // Afvis før tilfældighedskilden bruges, så et ekstra kast ikke ændrer tilstanden.
        if (RollsUsed >= MaxRolls)
        {
            throw new InvalidOperationException("Der er allerede brugt tre slag i denne tur.");
        }

        for (int i = 0; i < DiceCount; i++)
        {
            if (!_held[i])
            {
                // Den øvre grænse er eksklusiv, så intervallet giver værdierne 1–6.
                _dice[i] = _random.Next(1, 7);
            }
        }

        RollsUsed++;
    }

    /// <summary>
    /// Skifter den valgte terning mellem holdt og sluppet efter første eller andet kast.
    /// Ændrer kun hold-status; terningeværdier, brugte slag og tilfældighedskilde bevares.
    /// </summary>
    /// <param name="index">Terningens indeks fra 0 til 4. Oplàens numre 1–5 omsættes i kommunikationslaget.</param>
    /// <exception cref="ArgumentOutOfRangeException">Indekset er uden for intervallet 0–4.</exception>
    /// <exception cref="InvalidOperationException">Der er endnu ikke kastet, eller alle tre slag er brugt.</exception>
    public void ToggleHold(int index)
    {
        if (index < 0 || index >= DiceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "Terningens indeks skal være mellem 0 og 4.");
        }

        if (RollsUsed < 1 || RollsUsed >= MaxRolls)
        {
            throw new InvalidOperationException("Hold/slip er kun tilladt efter første eller andet kast.");
        }

        _held[index] = !_held[index];
    }

    // TODO: spillere, pointberegning og spillets afslutning.
}
