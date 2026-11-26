namespace MouseDebounceFix.Core;

/// <summary>
/// Supported physical mouse buttons monitored by the debounce filter.
/// </summary>
public enum MouseButton
{
    Left = 0,
    Right = 1
}

/// <summary>
/// Transition type for a mouse button event.
/// </summary>
public enum MouseButtonAction
{
    Down = 0,
    Up = 1
}

/// <summary>
/// Reason describing why an event was allowed or blocked.
/// </summary>
public enum DebounceReason
{
    Allowed = 0,
    Paused = 1,
    ButtonDisabled = 2,
    BounceDown = 3,
    SuppressedOrphanUp = 4
}

/// <summary>
/// Allocation-free result of evaluating a mouse button transition.
/// </summary>
/// <param name="ShouldAllow">True if the OS should receive this event; false to swallow it.</param>
/// <param name="Reason">Classification of the decision for metrics and diagnostics.</param>
/// <param name="ElapsedSinceLastUpMs">
/// Milliseconds elapsed since the last button release on the same button (populated on <see cref="MouseButtonAction.Down"/> when available).
/// Useful for real-hardware calibration without performing I/O inside the engine.
/// </param>
public readonly record struct DebounceDecision(
    bool ShouldAllow,
    DebounceReason Reason,
    double? ElapsedSinceLastUpMs)
{
    public bool ShouldBlock => !ShouldAllow;
}

/// <summary>
/// Pure eager-debouncing state machine for eliminating mechanical switch chatter
/// (phantom double/triple clicks) without adding latency to valid clicks or interrupting drag-and-drop.
/// </summary>
/// <remarks>
/// This class has zero Windows/OS dependencies and accepts monotonic timestamps in milliseconds
/// from the caller so it is 100% deterministic and unit-testable.
/// </remarks>
public sealed class DebounceEngine
{
    public const double DefaultThresholdMs = 65.0;

    private struct ButtonState
    {
        public bool HasLastUp;
        public double LastUpTimestampMs;
        public bool SuppressNextUp;

        public void Reset()
        {
            HasLastUp = false;
            LastUpTimestampMs = 0.0;
            SuppressNextUp = false;
        }
    }

    private ButtonState _leftState;
    private ButtonState _rightState;

    private long _thresholdMsBits;
    private int _isPaused;
    private int _filterLeftButton;
    private int _filterRightButton;

    private long _blockedLeftClicks;
    private long _blockedRightClicks;

    public DebounceEngine(
        double thresholdMs = DefaultThresholdMs,
        bool filterLeftButton = true,
        bool filterRightButton = true)
    {
        ValidateThreshold(thresholdMs);
        _thresholdMsBits = BitConverter.DoubleToInt64Bits(thresholdMs);
        _filterLeftButton = filterLeftButton ? 1 : 0;
        _filterRightButton = filterRightButton ? 1 : 0;
    }

    /// <summary>
    /// Minimum required interval in milliseconds between a button release (<see cref="MouseButtonAction.Up"/>)
    /// and the subsequent press (<see cref="MouseButtonAction.Down"/>) on the same button.
    /// </summary>
    public double ThresholdMs
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _thresholdMsBits));
        set
        {
            ValidateThreshold(value);
            Interlocked.Exchange(ref _thresholdMsBits, BitConverter.DoubleToInt64Bits(value));
        }
    }

    /// <summary>
    /// When true, all events pass through untouched and internal suppression state is cleared
    /// so pausing can never strand a button in a suppressed state.
    /// </summary>
    public bool IsPaused
    {
        get => Volatile.Read(ref _isPaused) != 0;
        set
        {
            int next = value ? 1 : 0;
            int previous = Interlocked.Exchange(ref _isPaused, next);
            if (previous != next)
            {
                ResetState();
            }
        }
    }

    /// <summary>
    /// Enables or disables chatter filtering for the left mouse button.
    /// </summary>
    public bool FilterLeftButton
    {
        get => Volatile.Read(ref _filterLeftButton) != 0;
        set
        {
            int next = value ? 1 : 0;
            int previous = Interlocked.Exchange(ref _filterLeftButton, next);
            if (previous != next)
            {
                _leftState.Reset();
            }
        }
    }

    /// <summary>
    /// Enables or disables chatter filtering for the right mouse button.
    /// </summary>
    public bool FilterRightButton
    {
        get => Volatile.Read(ref _filterRightButton) != 0;
        set
        {
            int next = value ? 1 : 0;
            int previous = Interlocked.Exchange(ref _filterRightButton, next);
            if (previous != next)
            {
                _rightState.Reset();
            }
        }
    }

    /// <summary>
    /// Number of phantom left-button clicks blocked in the current session.
    /// </summary>
    public long BlockedLeftClicks => Interlocked.Read(ref _blockedLeftClicks);

    /// <summary>
    /// Number of phantom right-button clicks blocked in the current session.
    /// </summary>
    public long BlockedRightClicks => Interlocked.Read(ref _blockedRightClicks);

    /// <summary>
    /// Total phantom clicks blocked across all monitored buttons in the current session.
    /// </summary>
    public long TotalBlockedClicks => BlockedLeftClicks + BlockedRightClicks;

    /// <summary>
    /// Evaluates a mouse button event using eager debouncing.
    /// </summary>
    /// <param name="button">Which button generated the event.</param>
    /// <param name="action">Whether the button was pressed or released.</param>
    /// <param name="timestampMs">Monotonic timestamp in milliseconds (e.g., from <c>Stopwatch.GetTimestamp()</c>).</param>
    public DebounceDecision ProcessEvent(MouseButton button, MouseButtonAction action, double timestampMs)
    {
        ValidateButton(button);
        ValidateAction(action);
        ValidateTimestamp(timestampMs);

        if (IsPaused)
        {
            return new DebounceDecision(true, DebounceReason.Paused, null);
        }

        if (!IsButtonEnabled(button))
        {
            return new DebounceDecision(true, DebounceReason.ButtonDisabled, null);
        }

        ref ButtonState state = ref GetButtonState(button);

        if (action == MouseButtonAction.Down)
        {
            double? elapsedMs = state.HasLastUp ? timestampMs - state.LastUpTimestampMs : null;
            double thresholdMs = ThresholdMs;

            if (elapsedMs.HasValue && elapsedMs.Value >= 0.0 && elapsedMs.Value < thresholdMs)
            {
                state.SuppressNextUp = true;
                IncrementBlockedCount(button);
                return new DebounceDecision(false, DebounceReason.BounceDown, elapsedMs);
            }

            // Clear any stale suppression flag (e.g., if an OS Up message was lost across a UAC boundary).
            state.SuppressNextUp = false;
            return new DebounceDecision(true, DebounceReason.Allowed, elapsedMs);
        }

        // MouseButtonAction.Up:
        // Always record the release timestamp—even for suppressed chatter—so multi-bounce bursts
        // (triple/quadruple switch chatter) extend the guard window until the contacts settle.
        state.LastUpTimestampMs = timestampMs;
        state.HasLastUp = true;

        if (state.SuppressNextUp)
        {
            state.SuppressNextUp = false;
            return new DebounceDecision(false, DebounceReason.SuppressedOrphanUp, null);
        }

        return new DebounceDecision(true, DebounceReason.Allowed, null);
    }

    /// <summary>
    /// Clears transient per-button timing and suppression state without resetting session counters.
    /// </summary>
    public void ResetState()
    {
        _leftState.Reset();
        _rightState.Reset();
    }

    /// <summary>
    /// Resets session blocked-click counters to zero.
    /// </summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _blockedLeftClicks, 0);
        Interlocked.Exchange(ref _blockedRightClicks, 0);
    }

    private bool IsButtonEnabled(MouseButton button) => button switch
    {
        MouseButton.Left => FilterLeftButton,
        MouseButton.Right => FilterRightButton,
        _ => false
    };

    private ref ButtonState GetButtonState(MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            return ref _leftState;
        }

        return ref _rightState;
    }

    private void IncrementBlockedCount(MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            Interlocked.Increment(ref _blockedLeftClicks);
        }
        else if (button == MouseButton.Right)
        {
            Interlocked.Increment(ref _blockedRightClicks);
        }
    }

    private static void ValidateThreshold(double thresholdMs)
    {
        if (double.IsNaN(thresholdMs) || double.IsInfinity(thresholdMs) || thresholdMs < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdMs),
                thresholdMs,
                "Threshold must be a finite value greater than or equal to 0 ms.");
        }
    }

    private static void ValidateTimestamp(double timestampMs)
    {
        if (double.IsNaN(timestampMs) || double.IsInfinity(timestampMs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMs),
                timestampMs,
                "Timestamp must be a finite value in milliseconds.");
        }
    }

    private static void ValidateButton(MouseButton button)
    {
        if (button is not (MouseButton.Left or MouseButton.Right))
        {
            throw new ArgumentOutOfRangeException(nameof(button), button, "Unsupported mouse button.");
        }
    }

    private static void ValidateAction(MouseButtonAction action)
    {
        if (action is not (MouseButtonAction.Down or MouseButtonAction.Up))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported mouse button action.");
        }
    }
}
