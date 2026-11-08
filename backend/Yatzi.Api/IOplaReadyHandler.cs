namespace Yatzi.Api;

/// <summary>
/// Serverintern grænse fra WebSocket-transporten til lobby/parring i #59.
/// Kaldes efter formatkontrol og registreret READY, også ved gentagelser.
/// Implementeringen skal respektere annullering og bruge registeret til binding
/// og afsendelse. Koden er en anmodning om genkendelse, ikke bevis for adgang.
/// </summary>
public interface IOplaReadyHandler
{
    /// <summary>
    /// Modtager forbindelsens id og en eventuel firecifret kode med bevarede nuller.
    /// Endpointet afventer arbejdet uden register- eller tilstandslås. Fejl går
    /// gennem endpointets fejlgrænse og oprydning; kommandoen genkøres ikke.
    /// </summary>
    Task HandleReadyAsync(Guid connectionId, string? code, CancellationToken cancellationToken);
}
