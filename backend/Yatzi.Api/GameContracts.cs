namespace Yatzi.Api;

/// <summary>
/// HTTP-svarets øjebliksbillede af spillet. API'et kopierer arrays og dictionaries fra den delte
/// GameSession, så senere handlinger ikke ændrer et svar, der er ved at blive sendt.
/// Felterne serialiseres som camelCase efter ASP.NET Cores almindelige webindstillinger.
/// GameId identificerer spillet; TurnNumber tæller fælles ture fra 1. GameCompleted
/// beskriver alle tavler samlet. WinnerIndices er tomt indtil afslutning og indeholder
/// derefter alle vindernes indeks i deltagerrækkefølge, også ved ens navne.
/// </summary>
public sealed record GameStateResponse(
    PlayerResponse[] Players,
    int CurrentPlayer,
    int[] Dice,
    bool[] Held,
    int RollsUsed,
    Dictionary<string, int> PossibleScores,
    bool TurnCompleted,
    Guid GameId,
    int TurnNumber,
    bool GameCompleted,
    int[] WinnerIndices);

/// <summary>
/// Spilleroplysninger til frontenden. Name er det validerede, trimmede spillernavn.
/// Pointtavlen er en selvstændig kopi. Selvstændigt spil har én spiller; lobbyspil
/// har 1–6 i tilmeldingsrækkefølge. CurrentPlayer angiver den aktive deltagers indeks
/// og bevarer sidste spillers gyldige indeks efter spilafslutning.
/// </summary>
public sealed record PlayerResponse(string Name, ScorecardResponse Scorecard);

/// <summary>
/// Kopi af alle 15 feltpoint og de afledte summer. Null betyder ledigt, 0 udfyldt.
/// Scores bruger kontraktens faste tekst-id'er og deler ikke lagring med spillet.
/// </summary>
public sealed record ScorecardResponse(
    Dictionary<string, int?> Scores,
    int UpperSum,
    int Bonus,
    int Total);

/// <summary>
/// Fælles HTTP-fejlformat. Message er en dansk forklaring, som frontenden kan vise.
/// </summary>
public sealed record ApiErrorResponse(string Message);

/// <summary>
/// Formatvalideret HTTP-kontekst. PlayerId er null i enkeltspiller; id'et beskriver
/// en spilrelation og er ikke et login. Sessionen kontrollerer den mod sin aktuelle tilstand.
/// </summary>
public readonly record struct GameActionContext(Guid GameId, int TurnNumber, Guid? PlayerId);

/// <summary>HTTP-laget vælger statuskode ud fra denne status, aldrig ud fra fejlteksten.</summary>
public enum GameActionStatus { Accepted, MissingGame, InvalidLobbyPlayerId, Conflict }

/// <summary>Kun accept indeholder en kopieret spiltilstand; afvisning indeholder dansk fejltekst.</summary>
public sealed record GameActionResult(GameActionStatus Status, GameStateResponse? State = null, string? Message = null);

// Fælles valg mellem fire konkrete handlinger, så HTTP-koordineringen ikke behøver callbacks under lås.
internal enum GameActionKind { Roll, ToggleHold, Score, Strike }

/// <summary>Serverinternt input til lobbystart; spiller-id udstilles fortsat kun i lobby-JSON.</summary>
public sealed record LobbyGamePlayer(Guid Id, string Name);
