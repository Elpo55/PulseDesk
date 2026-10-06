using Sysora.Core.Monitoring;

namespace Sysora.Tests.Core;

public sealed class SustainedThresholdDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SustainedThresholdDetector CpuAbove90For30Seconds() =>
        new(threshold: 90, duration: TimeSpan.FromSeconds(30), hysteresis: 5);

    private static bool Feed(SustainedThresholdDetector detector, double value, int fromSecond, int toSecond)
    {
        var active = false;
        for (var s = fromSecond; s <= toSecond; s++)
        {
            active = detector.Update(value, T0.AddSeconds(s));
        }

        return active;
    }

    [Fact]
    public void ShortSpike_DoesNotTrigger()
    {
        var detector = CpuAbove90For30Seconds();

        Assert.False(Feed(detector, 99, 0, 10));
        Assert.False(Feed(detector, 20, 11, 60));
    }

    [Fact]
    public void ValueAboveThresholdForDuration_Triggers()
    {
        var detector = CpuAbove90For30Seconds();

        Assert.False(Feed(detector, 95, 0, 29));
        Assert.True(detector.Update(95, T0.AddSeconds(30)));
    }

    [Fact]
    public void DipBelowThresholdBeforeDuration_RestartsWait()
    {
        var detector = CpuAbove90For30Seconds();
        Feed(detector, 95, 0, 25);
        detector.Update(85, T0.AddSeconds(26));

        Assert.False(Feed(detector, 95, 27, 50));
        Assert.True(Feed(detector, 95, 51, 57));
    }

    [Fact]
    public void ActiveCondition_ClearsOnlyBelowHysteresis()
    {
        var detector = CpuAbove90For30Seconds();
        Feed(detector, 95, 0, 30);
        Assert.True(detector.IsActive);

        Assert.True(detector.Update(87, T0.AddSeconds(31)));  // between 85 and 90: still active
        Assert.False(detector.Update(84, T0.AddSeconds(32))); // below 90 - 5: cleared
    }

    [Fact]
    public void GapLongerThanDuration_RestartsWait()
    {
        var detector = CpuAbove90For30Seconds();
        Feed(detector, 95, 0, 20);

        // The machine slept for ten minutes: nothing is known about that period.
        Assert.False(detector.Update(95, T0.AddMinutes(10)));
        Assert.False(detector.Update(95, T0.AddMinutes(10).AddSeconds(29)));
        Assert.True(detector.Update(95, T0.AddMinutes(10).AddSeconds(30)));
    }

    [Fact]
    public void OutOfOrderSample_IsIgnored()
    {
        var detector = CpuAbove90For30Seconds();
        Feed(detector, 95, 0, 30);

        Assert.True(detector.Update(0, T0.AddSeconds(5)));
    }

    [Fact]
    public void ZeroDuration_TriggersImmediately()
    {
        var detector = new SustainedThresholdDetector(95, TimeSpan.Zero);

        Assert.False(detector.Update(94, T0));
        Assert.True(detector.Update(96, T0.AddSeconds(1)));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var detector = CpuAbove90For30Seconds();
        Feed(detector, 95, 0, 30);
        detector.Reset();

        Assert.False(detector.IsActive);
        Assert.Null(detector.AboveSince);
    }
}
