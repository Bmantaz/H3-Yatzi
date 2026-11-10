namespace Yatzi.Api;

/// <summary>
/// Selvstændigt HTTP-øjebliksbillede af lobbyen. Koder og transport-id'er udstilles
/// ikke. Started skifter først ved accepteret spillestart og nulstilles ved servergenstart.
/// </summary>
public sealed record LobbyStateResponse(LobbyPlayerResponse[] Players, bool Started);

/// <summary>Stabil spilleridentitet, valideret navn og aktuel serverbinding.</summary>
public sealed record LobbyPlayerResponse(Guid Id, string Name, bool Connected);

/// <summary>Sessionens regelresultat; HTTP-laget vælger statuskode og dansk fejltekst.</summary>
public enum LobbyJoinStatus { Joined, UnknownCode, AlreadyJoined, Full, ConnectionUnavailable, Started }

/// <summary>Kun accepteret tilmelding indeholder et lobbyøjebliksbillede.</summary>
public sealed record LobbyJoinResult(LobbyJoinStatus Status, LobbyStateResponse? State = null, Guid? JoinedPlayerId = null);

/// <summary>Join-svaret fastholder netop requestens accepterede id, også ved samtidige navne.</summary>
public sealed record LobbyJoinResponse(LobbyPlayerResponse[] Players, bool Started, Guid JoinedPlayerId);

/// <summary>Start afvises før ændringer eller accepteres med et kopieret spiløjebliksbillede.</summary>
public enum LobbyStartStatus { Started, AlreadyStarted, NoPlayers, Disconnected }

/// <summary>En accepteret start bevares også ved senere afsendelsesfejl.</summary>
public sealed record LobbyStartResult(LobbyStartStatus Status, GameStateResponse? State = null);

/// <summary>Omkamp vedrører et afsluttet spil og en tilmeldt initiativtager, ikke en bestemt tur.</summary>
public readonly record struct LobbyRestartContext(Guid GameId, Guid PlayerId);

/// <summary>Omkampens afvisninger følger kontraktens fejlprioritet uden fortolkning af fejltekst.</summary>
public enum LobbyRestartStatus { Restarted, MissingGame, StaleGame, NotLobbyGame, UnknownPlayer, NotCompleted, Disconnected }

/// <summary>Kun accepteret omkamp indeholder et kopieret startøjebliksbillede.</summary>
public sealed record LobbyRestartResult(LobbyRestartStatus Status, GameStateResponse? State = null);
