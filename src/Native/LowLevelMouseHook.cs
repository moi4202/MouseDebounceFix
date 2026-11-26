using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MouseDebounceFix.Core;

namespace MouseDebounceFix.Native;

/// <summary>
/// Safe, low-overhead Win32 low-level mouse hook (WH_MOUSE_LL) wrapper.
/// Keeps the callback delegate rooted for GC safety, measures intervals with a monotonic Stopwatch clock,
/// ignores software-injected mouse events (LLMHF_INJECTED), and guarantees hook removal via IDisposable.
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;

    /// <summary>
    /// Event-injected flag in MSLLHOOKSTRUCT.flags (Remote Desktop, accessibility tools, automation).
    /// </summary>
    private const uint LLMHF_INJECTED = 0x00000001;

    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly DebounceEngine _engine;
    private readonly CalibrationTracker? _calibrationTracker;
    private readonly LowLevelMouseProc _hookCallback;

    private IntPtr _hookId = IntPtr.Zero;
    private int _ignoreInjectedEvents;
    private long _ignoredInjectedCount;
    private int _disposed;

    public LowLevelMouseHook(
        DebounceEngine engine,
        CalibrationTracker? calibrationTracker = null,
        bool ignoreInjectedEvents = true)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _calibrationTracker = calibrationTracker;
        _ignoreInjectedEvents = ignoreInjectedEvents ? 1 : 0;

        // Keep delegate in an instance field so the GC never collects the native callback pointer.
        _hookCallback = HookCallback;
    }

    public bool IsInstalled => Volatile.Read(ref _hookId) != IntPtr.Zero;

    public bool IgnoreInjectedEvents
    {
        get => Volatile.Read(ref _ignoreInjectedEvents) != 0;
        set => Volatile.Write(ref _ignoreInjectedEvents, value ? 1 : 0);
    }

    public long IgnoredInjectedEvents => Interlocked.Read(ref _ignoredInjectedCount);

    /// <summary>
    /// Installs the global WH_MOUSE_LL hook on the current UI message-pump thread.
    /// </summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (IsInstalled)
        {
            return;
        }

        IntPtr moduleHandle = GetModuleHandleW(null);
        IntPtr hookHandle = SetWindowsHookExW(WH_MOUSE_LL, _hookCallback, moduleHandle, 0);
        if (hookHandle == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(errorCode, $"Failed to install WH_MOUSE_LL hook (Win32 error {errorCode}).");
        }

        Interlocked.Exchange(ref _hookId, hookHandle);
    }

    /// <summary>
    /// Safely removes the Win32 mouse hook if installed.
    /// </summary>
    public void Uninstall()
    {
        IntPtr handle = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(handle);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Uninstall();
        GC.SuppressFinalize(this);
    }

    ~LowLevelMouseHook()
    {
        Uninstall();
    }

    /// <summary>
    /// Hot-path hook callback. Must stay ultra-fast (no I/O, no synchronous logging, no UI calls)
    /// so Windows never silently removes the hook due to LowLevelHooksTimeout.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Fast-path exit for high-frequency events like WM_MOUSEMOVE before any marshaling or clock read.
        int message = unchecked((int)wParam);
        MouseButton button;
        MouseButtonAction action;

        switch (message)
        {
            case WM_LBUTTONDOWN:
                button = MouseButton.Left;
                action = MouseButtonAction.Down;
                break;
            case WM_LBUTTONUP:
                button = MouseButton.Left;
                action = MouseButtonAction.Up;
                break;
            case WM_RBUTTONDOWN:
                button = MouseButton.Right;
                action = MouseButtonAction.Down;
                break;
            case WM_RBUTTONUP:
                button = MouseButton.Right;
                action = MouseButtonAction.Up;
                break;
            default:
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Fail-open immediately when paused.
        if (_engine.IsPaused)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        try
        {
            MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Ignore software-injected clicks (RDP, TeamViewer, automation scripts, on-screen keyboards).
            if (IgnoreInjectedEvents && (hookStruct.Flags & LLMHF_INJECTED) != 0)
            {
                Interlocked.Increment(ref _ignoredInjectedCount);
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            double timestampMs = Stopwatch.GetTimestamp() * TicksToMilliseconds;
            DebounceDecision decision = _engine.ProcessEvent(button, action, timestampMs);

            if (action == MouseButtonAction.Down
                && decision.ElapsedSinceLastUpMs.HasValue
                && _calibrationTracker is { IsActive: true })
            {
                var sample = new CalibrationSample(
                    button,
                    decision.ElapsedSinceLastUpMs.Value,
                    decision.ShouldBlock,
                    DateTime.UtcNow);
                _calibrationTracker.TryRecord(in sample);
            }

            if (decision.ShouldBlock)
            {
                return (IntPtr)1;
            }
        }
        catch
        {
            // Never let a managed exception cross a Win32 hook boundary; fail open so the user keeps clicking.
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
