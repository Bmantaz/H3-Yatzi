using System.Collections.ObjectModel;

namespace Yatzi.Core;

/// <summary>
/// Spillets tilstand og regler i backendens kerne, uafhængigt af web og hardware.
/// Ejer én privat pointtavle og styrer kast, hold/slip, ét feltvalg pr. tur
/// og eksplicit start af næste tur uden at miste spillerens point.
/// Andre komponenter har kun læseadgang. Klassen har ingen egen lås;
/// fælles adgang fra HTTP og Oplà beskyttes af GameSession i API-laget.
/// </summary>
public class Game
{
    private const int DiceCount = 5;
    private const int MaxRolls = 3;

    private readonly int[] _dice = new int[DiceCount];
    private readonly bool[] _held = new bool[DiceCount];
    private readonly Random _random;
    private readonly Scorecard _scorecard = new();

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
    /// Løbende, skrivebeskyttet visning af spillerens 15 pointfelter.
    /// Null betyder ledigt; også et gemt nul låser feltet. API-svar skal kopiere visningen.
    /// </summary>
    public IReadOnlyDictionary<ScoreCategory, int?> Scores => _scorecard.Scores;

    /// <summary>
    /// True når denne spillers 15 felter er udfyldt, inklusive gemte nuller.
    /// Beskriver kun spillerens tavle; hele spillets afslutning hører til i GameSession.
    /// </summary>
    public bool IsScorecardComplete => _scorecard.IsComplete;

    /// <summary>
    /// Gemte point i de seks øvre felter, beregnet af pointtavlen ved læsning.
    /// </summary>
    public int UpperSum => _scorecard.UpperSum;

    /// <summary>
    /// Pointtavlens bonus: 50 ved en øvre sum på mindst 63, ellers 0.
    /// </summary>
    public int Bonus => _scorecard.Bonus;

    /// <summary>
    /// Alle gemte feltpoint plus pointtavlens bonus én gang.
    /// </summary>
    public int Total => _scorecard.Total;

    /// <summary>
    /// True efter ét accepteret feltvalg. Turens terninger, hold og slag bevares,
    /// mens kast, hold og feltvalg afvises. StartNextTurn åbner eksplicit næste tur,
    /// hvis pointtavlen stadig har ledige felter; Score og Strike starter den ikke selv.
    /// </summary>
    public bool TurnCompleted { get; private set; }

    /// <summary>
    /// Beregner mulige point, inklusive 0, for alle ledige felter fra de aktuelle terninger.
    /// Returnerer et selvstændigt, skrivebeskyttet øjebliksbillede uden sideeffekter.
    /// Før første kast og efter turafslutning er resultatet tomt.
    /// </summary>
    public IReadOnlyDictionary<ScoreCategory, int> PossibleScores
    {
        get
        {
            var scores = new Dictionary<ScoreCategory, int>();
            if (RollsUsed > 0 && !TurnCompleted)
            {
                foreach (var entry in _scorecard.Scores)
                {
                    if (!entry.Value.HasValue)
                    {
                        scores.Add(entry.Key, ScoreCalculator.Calculate(Dice, entry.Key));
                    }
                }
            }

            return new ReadOnlyDictionary<ScoreCategory, int>(scores);
        }
    }

    /// <summary>
    /// Opretter en starttilstand med den fælles tilfældighedskilde fra .NET.
    /// </summary>
    public Game() : this(Random.Shared)
    {
    }

    /// <summary>
    /// Opretter en åben tur med fem uslåede terninger, egne arrays og en tom pointtavle.
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
    /// Åbner næste tur efter et accepteret feltvalg, hvis tavlen ikke er fuld.
    /// Nulstiller terninger, hold, slag og turafslutning uden at kaste eller ændre point.
    /// Første tur er allerede åben fra konstruktøren. GameSession styrer,
    /// hvornår en afsluttet spiller genaktiveres, og beskytter fælles adgang med sin lås.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Den aktuelle tur er ikke afsluttet, eller pointtavlen er fuld. Hele tilstanden bevares.
    /// </exception>
    public void StartNextTurn()
    {
        if (!TurnCompleted)
        {
            throw new InvalidOperationException(
                "Den aktuelle tur skal afsluttes, før en ny tur kan starte.");
        }

        if (IsScorecardComplete)
        {
            throw new InvalidOperationException(
                "Pointtavlen er fuld. Der kan ikke startes en ny tur.");
        }

        // Bevar arrays og deres skrivebeskyttede visninger. Ingen tilfældighed
        // bruges før spillerens første Roll i den nye tur.
        Array.Clear(_dice);
        Array.Clear(_held);
        RollsUsed = 0;
        TurnCompleted = false;
    }

    /// <summary>
    /// Kaster de ikke-holdte terninger og øger antallet af brugte slag én gang.
    /// Terningeværdierne opdateres i de eksisterende arrays; hold-status bevares.
    /// Et kast bruger stadig ét slag, hvis alle fem terninger er holdt.
    /// </summary>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, eller dens tre slag er brugt.</exception>
    public void Roll()
    {
        EnsureTurnActive();

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
    /// <exception cref="InvalidOperationException">Turen er afsluttet, der er endnu ikke kastet, eller alle tre slag er brugt.</exception>
    public void ToggleHold(int index)
    {
        if (index < 0 || index >= DiceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "Terningens indeks skal være mellem 0 og 4.");
        }

        EnsureTurnActive();

        if (RollsUsed < 1 || RollsUsed >= MaxRolls)
        {
            throw new InvalidOperationException("Hold/slip er kun tilladt efter første eller andet kast.");
        }

        _held[index] = !_held[index];
    }

    /// <summary>
    /// Beregner og gemmer point fra spillets egne terninger og afslutter turen.
    /// Også et beregnet nul bruger feltet. Terninger, hold og brugte slag bevares.
    /// Afvisninger bevarer hele tilstanden og bruger ingen tilfældighed.
    /// </summary>
    /// <param name="category">Et kendt, ledigt pointfelt.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt.</exception>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, der er endnu ikke kastet, eller feltet er udfyldt.</exception>
    public void Score(ScoreCategory category)
    {
        EnsureCanScore(category);
        _scorecard.Record(Dice, category);

        // Lås først turen efter vellykket registrering, så en afvisning ikke bruger turen.
        TurnCompleted = true;
    }

    /// <summary>
    /// Gemmer 0 i et ledigt felt og afslutter turen efter mindst ét kast.
    /// Tillades også, når feltet kunne give point. Terninger, hold og slag bevares;
    /// afvisninger ændrer ingen tilstand og bruger ingen tilfældighed.
    /// </summary>
    /// <param name="category">Et kendt, ledigt pointfelt.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt.</exception>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, der er endnu ikke kastet, eller feltet er udfyldt.</exception>
    public void Strike(ScoreCategory category)
    {
        EnsureCanScore(category);
        _scorecard.Strike(category);
        TurnCompleted = true;
    }

    /// <summary>
    /// Kontrollerer input og tur før feltvalg. Inputfejl har forrang;
    /// derefter afvises en afsluttet tur før andre spilregelafvisninger.
    /// Pointtavlen kontrollerer efterfølgende, at feltet er ledigt.
    /// </summary>
    /// <param name="category">Feltet, som ønskes udfyldt.</param>
    /// <exception cref="ArgumentOutOfRangeException">Feltet er ukendt.</exception>
    /// <exception cref="InvalidOperationException">Turen er afsluttet, eller der er endnu ikke kastet.</exception>
    private void EnsureCanScore(ScoreCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Pointfeltet er ukendt.");
        }

        EnsureTurnActive();
        if (RollsUsed < 1)
        {
            throw new InvalidOperationException("Kast terningerne, før du vælger et pointfelt.");
        }
    }

    /// <summary>
    /// Fælles kontrol før kast, hold/slip og feltvalg, uden at ændre tilstanden.
    /// </summary>
    /// <exception cref="InvalidOperationException">Et feltvalg har allerede afsluttet turen.</exception>
    private void EnsureTurnActive()
    {
        if (TurnCompleted)
        {
            throw new InvalidOperationException(
                "Turen er afsluttet. Der kan ikke udføres flere handlinger i denne tur.");
        }
    }
}
