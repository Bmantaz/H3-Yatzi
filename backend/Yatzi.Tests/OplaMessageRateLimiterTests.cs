using Yatzi.Api;

namespace Yatzi.Tests;

/// <summary>Kontrollerer det rullende sekund med styret tid, uden netværk eller pauser.</summary>
public class OplaMessageRateLimiterTests
{
    [Fact]
    public void Constructor_RequiresTimeProvider() =>
        Assert.Throws<ArgumentNullException>(() => new OplaMessageRateLimiter(null!));

    [Fact]
    public void TwentyMessages_AreAllowedAndTwentyFirstIsRejected()
    {
        var limiter = new OplaMessageRateLimiter(new ManualTimestampTimeProvider());
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        Assert.False(limiter.TryRecordMessage());
    }

    [Theory]
    [InlineData(9_999_999, false)]
    [InlineData(10_000_000, true)]
    [InlineData(10_000_001, true)]
    public void Entries_ExpireAtExactlyOneSecond(long elapsedTicks, bool allowed)
    {
        var clock = new ManualTimestampTimeProvider();
        var limiter = new OplaMessageRateLimiter(clock);
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromTicks(elapsedTicks));
        Assert.Equal(allowed, limiter.TryRecordMessage());
    }

    [Fact]
    public void ClockSecondBoundary_DoesNotResetWindow()
    {
        var clock = new ManualTimestampTimeProvider();
        var limiter = new OplaMessageRateLimiter(clock);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(limiter.TryRecordMessage());
    }

    [Fact]
    public void PartialExpiry_KeepsYoungerMessagesInWindow()
    {
        var clock = new ManualTimestampTimeProvider();
        var limiter = new OplaMessageRateLimiter(clock);
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryRecordMessage());
        Assert.False(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryRecordMessage());
        Assert.False(limiter.TryRecordMessage());
    }

    [Fact]
    public void WallClockChanges_DoNotChangeMessageAge()
    {
        var clock = new ManualTimestampTimeProvider();
        var limiter = new OplaMessageRateLimiter(clock);
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.False(limiter.TryRecordMessage());
        clock.UtcNow = clock.UtcNow.AddDays(-2);
        Assert.False(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryRecordMessage());
    }

    [Fact]
    public void RejectedMessages_DoNotOccupyLaterWindow()
    {
        var clock = new ManualTimestampTimeProvider();
        var limiter = new OplaMessageRateLimiter(clock);
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 100; i++) Assert.False(limiter.TryRecordMessage());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 20; i++) Assert.True(limiter.TryRecordMessage());
        Assert.False(limiter.TryRecordMessage());
    }
}
