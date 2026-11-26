using MouseDebounceFix.Core;

namespace MouseDebounceFix.Tests;

public sealed class CalibrationTrackerTests
{
    [Fact]
    public void InactiveTracker_IgnoresIncomingSamples()
    {
        var tracker = new CalibrationTracker();
        var sample = new CalibrationSample(MouseButton.Left, 32.5, true, DateTime.UtcNow);

        Assert.False(tracker.TryRecord(in sample));

        var drained = new List<CalibrationSample>();
        Assert.Equal(0, tracker.DrainTo(drained));
        Assert.Empty(drained);
    }

    [Fact]
    public void ActiveTracker_RecordsAndDrainsSamplesInOrder_AndRespectsCapacity()
    {
        var tracker = new CalibrationTracker(maxPendingSamples: 2) { IsActive = true };

        var s1 = new CalibrationSample(MouseButton.Left, 25.0, true, DateTime.UtcNow);
        var s2 = new CalibrationSample(MouseButton.Left, 58.0, true, DateTime.UtcNow);
        var s3 = new CalibrationSample(MouseButton.Right, 180.0, false, DateTime.UtcNow);

        Assert.True(tracker.TryRecord(in s1));
        Assert.True(tracker.TryRecord(in s2));
        Assert.False(tracker.TryRecord(in s3)); // Exceeds bounded queue capacity

        var drained = new List<CalibrationSample>();
        Assert.Equal(2, tracker.DrainTo(drained));
        Assert.Equal(25.0, drained[0].ElapsedSinceLastUpMs);
        Assert.Equal(58.0, drained[1].ElapsedSinceLastUpMs);

        // Once drained, new samples are accepted again
        Assert.True(tracker.TryRecord(in s3));
    }

    [Fact]
    public void DeactivatingTracker_ClearsBufferedSamples()
    {
        var tracker = new CalibrationTracker { IsActive = true };
        var sample = new CalibrationSample(MouseButton.Left, 40.0, true, DateTime.UtcNow);
        Assert.True(tracker.TryRecord(in sample));

        tracker.IsActive = false;

        var drained = new List<CalibrationSample>();
        Assert.Equal(0, tracker.DrainTo(drained));
    }

    [Fact]
    public void SuggestThresholdMs_CalculatesSafeThresholdWithMargin()
    {
        // Example microswitch chatter at 35 ms and 58 ms, plus normal clicks at 185 ms and 420 ms
        double? suggested = CalibrationTracker.SuggestThresholdMs([35.0, 58.0, 185.0, 420.0]);

        // 58 + 15 = 73 -> rounded up to nearest 5 ms = 75 ms
        Assert.Equal(75.0, suggested);
    }

    [Fact]
    public void SuggestThresholdMs_WhenOnlyNormalClicksObserved_ReturnsNull()
    {
        double? suggested = CalibrationTracker.SuggestThresholdMs([160.0, 210.0, 500.0]);

        Assert.Null(suggested);
    }
}
