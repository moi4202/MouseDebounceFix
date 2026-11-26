using System.Collections.Concurrent;

namespace MouseDebounceFix.Core;

/// <summary>
/// Allocation-free telemetry sample captured when live calibration mode is active.
/// </summary>
public readonly record struct CalibrationSample(
    MouseButton Button,
    double ElapsedSinceLastUpMs,
    bool WasBlocked,
    DateTime CapturedAtUtc);

/// <summary>
/// Lock-free bounded bridge between the high-frequency mouse hook callback and the UI calibration window.
/// Ensures zero I/O, zero locks, and O(1) cost inside WH_MOUSE_LL.
/// </summary>
public sealed class CalibrationTracker
{
    public const int DefaultMaxPendingSamples = 1024;
    public const double MaxSuspectedChatterMs = 120.0;
    public const double RecommendedSafetyMarginMs = 15.0;

    private readonly ConcurrentQueue<CalibrationSample> _queue = new();
    private readonly int _maxPendingSamples;
    private int _pendingCount;
    private int _isActive;

    public CalibrationTracker(int maxPendingSamples = DefaultMaxPendingSamples)
    {
        if (maxPendingSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingSamples), "Capacity must be positive.");
        }

        _maxPendingSamples = maxPendingSamples;
    }

    /// <summary>
    /// Controls whether samples are recorded. When false, <see cref="TryRecord"/> returns immediately.
    /// </summary>
    public bool IsActive
    {
        get => Volatile.Read(ref _isActive) != 0;
        set
        {
            int next = value ? 1 : 0;
            int previous = Interlocked.Exchange(ref _isActive, next);
            if (previous != next && next == 0)
            {
                Clear();
            }
        }
    }

    /// <summary>
    /// Enqueues a calibration sample in O(1) without blocking. Safe to invoke from WH_MOUSE_LL.
    /// </summary>
    public bool TryRecord(in CalibrationSample sample)
    {
        if (!IsActive)
        {
            return false;
        }

        if (Interlocked.Increment(ref _pendingCount) > _maxPendingSamples)
        {
            Interlocked.Decrement(ref _pendingCount);
            return false;
        }

        _queue.Enqueue(sample);
        return true;
    }

    /// <summary>
    /// Drains pending samples into <paramref name="destination"/> on the UI thread.
    /// </summary>
    public int DrainTo(List<CalibrationSample> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int drained = 0;
        while (_queue.TryDequeue(out CalibrationSample sample))
        {
            Interlocked.Decrement(ref _pendingCount);
            destination.Add(sample);
            drained++;
        }

        return drained;
    }

    /// <summary>
    /// Discards all buffered samples.
    /// </summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    /// <summary>
    /// Computes a recommended debounce threshold in milliseconds (rounded up to the nearest 5 ms)
    /// from observed suspected bounce intervals (events faster than <see cref="MaxSuspectedChatterMs"/>).
    /// Returns null if no suspected bounce intervals have been observed yet.
    /// </summary>
    public static double? SuggestThresholdMs(IEnumerable<double> observedIntervalsMs)
    {
        ArgumentNullException.ThrowIfNull(observedIntervalsMs);

        double? maxBounce = null;
        foreach (double interval in observedIntervalsMs)
        {
            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval < 0.0)
            {
                continue;
            }

            if (interval < MaxSuspectedChatterMs && (!maxBounce.HasValue || interval > maxBounce.Value))
            {
                maxBounce = interval;
            }
        }

        if (!maxBounce.HasValue)
        {
            return null;
        }

        double withMargin = maxBounce.Value + RecommendedSafetyMarginMs;
        double roundedUp = Math.Ceiling(withMargin / 5.0) * 5.0;
        return Math.Clamp(roundedUp, 20.0, 150.0);
    }
}
