namespace Yatzi.Api;

/// <summary>
/// HTTP-svarets øjebliksbillede af spillet. API'et kopierer arrays og dictionaries fra den delte
/// GameSession, så senere handlinger ikke ændrer et svar, der er ved at blive sendt.
/// Felterne serialiseres som camelCase efter ASP.NET Cores almindelige webindstillinger.
/// </summary>
public sealed record GameStateResponse(
    PlayerResponse[] Players,
    int CurrentPlayer,
    int[] Dice,
    bool[] Held,
    int RollsUsed,
    Dictionary<string, int> PossibleScores,
    bool TurnCompleted);

/// <summary>
/// Spilleroplysninger til frontenden. Name er det validerede, trimmede spillernavn.
/// Pointtavlen er en selvstændig kopi. Selvstændigt spil har én spiller; lobbyspil
/// har 1–6 i tilmeldingsrækkefølge. Turskifte følger i #44.
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

/// <summary>Serverinternt input til lobbystart; spiller-id udstilles fortsat kun i lobby-JSON.</summary>
public sealed record LobbyGamePlayer(Guid Id, string Name);
