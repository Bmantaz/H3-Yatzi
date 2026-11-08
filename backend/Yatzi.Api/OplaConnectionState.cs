namespace Yatzi.Api;

/// <summary>
/// Transportens adgangstilstand for én forbindelse. READY alene giver ingen
/// spillerbinding. Closed gælder fra oprydningens start, før lukkehåndtrykket.
/// </summary>
public enum OplaConnectionState
{
    Connected,
    Ready,
    Paired,
    Closed
}
