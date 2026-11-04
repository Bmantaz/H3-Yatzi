namespace Yatzi.Api;

/// <summary>
/// HTTP-svarets øjebliksbillede af spillet. API'et kopierer arrays fra den delte
/// GameSession, så senere handlinger ikke ændrer et svar, der er ved at blive sendt.
/// Felterne serialiseres som camelCase efter ASP.NET Cores almindelige webindstillinger.
/// </summary>
public sealed record GameStateResponse(
    PlayerResponse[] Players,
    int CurrentPlayer,
    int[] Dice,
    bool[] Held,
    int RollsUsed);

/// <summary>
/// Spilleroplysninger til frontenden. Name er det validerede, trimmede spillernavn.
/// Opgave #33 bruger én spiller; lobby og turskifte tilføjes senere.
/// </summary>
public sealed record PlayerResponse(string Name);

/// <summary>
/// Fælles HTTP-fejlformat. Message er en dansk forklaring, som frontenden kan vise.
/// </summary>
public sealed record ApiErrorResponse(string Message);
