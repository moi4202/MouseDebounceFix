using System.Runtime.InteropServices;

namespace MouseDebounceFix.Native;

/// <summary>
/// Registers a global Windows emergency hotkey (Ctrl + Alt + Shift + X) without installing a keyboard hook,
/// ensuring the user can always shut down MouseDebounceFix instantly even if hardware fails.
/// </summary>
public sealed class GlobalEmergencyHotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4D44; // 'MD'

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_X = 0x58;

    private readonly Action _onTriggered;
    private bool _registered;
    private bool _disposed;

    public GlobalEmergencyHotkey(Action onTriggered)
    {
        _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));

        CreateHandle(new CreateParams());
        _registered = RegisterHotKey(
            Handle,
            HotkeyId,
            MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT,
            VK_X);
    }

    public bool IsRegistered => _registered;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)HotkeyId)
        {
            _onTriggered();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered && Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
