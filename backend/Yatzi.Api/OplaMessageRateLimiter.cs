namespace Yatzi.Api;

/// <summary>
/// Begrænser komplette tekstbeskeder til 20 i et rullende sekund på én forbindelse.
/// Bruges kun af forbindelsens modtagelsesløkke, før indholdet behandles. Klassen
/// kender hverken WebSocket, parring eller spil og kræver ikke en fælles lås.
/// </summary>
public sealed class OplaMessageRateLimiter
{
    public const int MaxMessagesPerSecond = 20;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider;
    private readonly Queue<long> _timestamps = new(MaxMessagesPerSecond);

    /// <summary>Modtager samme tidskilde som transporten; tests kan styre tiden.</summary>
    public OplaMessageRateLimiter(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Registrerer én færdig tekstbesked og returnerer true, hvis der er plads i
    /// vinduet. Præcis ét sekund gamle poster udløber. False betyder, at endpointet
    /// skal lukke forbindelsen uden at behandle beskeden. Køen har højst 20 poster.
    /// Metoden må kun kaldes fra forbindelsens ene modtagelsesløkke.
    /// </summary>
    public bool TryRecordMessage()
    {
        long now = _timeProvider.GetTimestamp();
        // Monotone tidsstempler undgår nulstilling ved klokkesekundets skift eller
        // ved justering af systemets dato og klokkeslæt.
        while (_timestamps.TryPeek(out long oldest)
            && _timeProvider.GetElapsedTime(oldest, now) >= Window)
            _timestamps.Dequeue();

        if (_timestamps.Count == MaxMessagesPerSecond) return false;
        _timestamps.Enqueue(now);
        return true;
    }
}
