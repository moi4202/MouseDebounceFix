using System.Globalization;
using MouseDebounceFix.Core;
using MouseDebounceFix.Native;

namespace MouseDebounceFix.UI;

/// <summary>
/// Headless WinForms ApplicationContext hosting the System Tray icon, context menu,
/// live session counters, calibration window launcher, and emergency shutdown hotkey.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private static readonly double[] PresetThresholdsMs = [50, 65, 70, 90, 120];

    private readonly DebounceEngine _engine;
    private readonly AppSettings _settings;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly CalibrationTracker _calibrationTracker;
    private readonly GlobalEmergencyHotkey _emergencyHotkey;

    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _statusRefreshTimer;

    private readonly ToolStripMenuItem _statsMenuItem;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _thresholdRootItem;
    private readonly ToolStripMenuItem _customThresholdItem;
    private readonly Dictionary<double, ToolStripMenuItem> _presetThresholdItems = new();
    private readonly ToolStripMenuItem _leftButtonMenuItem;
    private readonly ToolStripMenuItem _rightButtonMenuItem;
    private readonly ToolStripMenuItem _ignoreInjectedMenuItem;

    private CalibrationForm? _calibrationForm;
    private long _lastObservedBlockedCount = -1;
    private bool _disposed;

    public TrayAppContext(
        DebounceEngine engine,
        AppSettings settings,
        LowLevelMouseHook mouseHook,
        CalibrationTracker calibrationTracker,
        bool openCalibrationOnStart = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mouseHook = mouseHook ?? throw new ArgumentNullException(nameof(mouseHook));
        _calibrationTracker = calibrationTracker ?? throw new ArgumentNullException(nameof(calibrationTracker));

        _activeIcon = TrayIconFactory.CreateStatusIcon(isActive: true);
        _pausedIcon = TrayIconFactory.CreateStatusIcon(isActive: false);

        _statsMenuItem = new ToolStripMenuItem
        {
            Enabled = false
        };

        _pauseMenuItem = new ToolStripMenuItem();
        _pauseMenuItem.Click += (_, _) => TogglePause();

        _thresholdRootItem = new ToolStripMenuItem("Umbral de rebote");
        foreach (double preset in PresetThresholdsMs)
        {
            string label = preset == DebounceEngine.DefaultThresholdMs
                ? $"{preset:0} ms (recomendado)"
                : $"{preset:0} ms";

            var item = new ToolStripMenuItem(label) { Tag = preset };
            item.Click += (_, _) => SetThreshold(preset);
            _presetThresholdItems[preset] = item;
            _thresholdRootItem.DropDownItems.Add(item);
        }

        _customThresholdItem = new ToolStripMenuItem
        {
            Visible = false,
            Checked = true,
            Enabled = false
        };
        _thresholdRootItem.DropDownItems.Add(new ToolStripSeparator());
        _thresholdRootItem.DropDownItems.Add(_customThresholdItem);

        var buttonsRootItem = new ToolStripMenuItem("Botones protegidos");
        _leftButtonMenuItem = new ToolStripMenuItem("Botón izquierdo")
        {
            CheckOnClick = true,
            Checked = _engine.FilterLeftButton
        };
        _leftButtonMenuItem.Click += (_, _) =>
        {
            _engine.FilterLeftButton = _leftButtonMenuItem.Checked;
            _settings.FilterLeftButton = _leftButtonMenuItem.Checked;
            SaveSettingsSafe();
        };

        _rightButtonMenuItem = new ToolStripMenuItem("Botón derecho")
        {
            CheckOnClick = true,
            Checked = _engine.FilterRightButton
        };
        _rightButtonMenuItem.Click += (_, _) =>
        {
            _engine.FilterRightButton = _rightButtonMenuItem.Checked;
            _settings.FilterRightButton = _rightButtonMenuItem.Checked;
            SaveSettingsSafe();
        };

        buttonsRootItem.DropDownItems.Add(_leftButtonMenuItem);
        buttonsRootItem.DropDownItems.Add(_rightButtonMenuItem);

        _ignoreInjectedMenuItem = new ToolStripMenuItem("Ignorar eventos inyectados (RDP / scripts)")
        {
            CheckOnClick = true,
            Checked = _mouseHook.IgnoreInjectedEvents
        };
        _ignoreInjectedMenuItem.Click += (_, _) =>
        {
            _mouseHook.IgnoreInjectedEvents = _ignoreInjectedMenuItem.Checked;
            _settings.IgnoreInjectedEvents = _ignoreInjectedMenuItem.Checked;
            SaveSettingsSafe();
        };

        var calibrateMenuItem = new ToolStripMenuItem("Modo de calibración en vivo...");
        calibrateMenuItem.Click += (_, _) => OpenCalibrationWindow();

        var resetCounterMenuItem = new ToolStripMenuItem("Reiniciar contador");
        resetCounterMenuItem.Click += (_, _) =>
        {
            _engine.ResetCounters();
            RefreshUiState(force: true);
        };

        var exitMenuItem = new ToolStripMenuItem("Salir (Ctrl+Alt+Shift+X)");
        exitMenuItem.Click += (_, _) => ExitApplication();

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Opening += (_, _) => RefreshUiState(force: true);
        _contextMenu.Items.AddRange(
        [
            _statsMenuItem,
            new ToolStripSeparator(),
            _pauseMenuItem,
            _thresholdRootItem,
            buttonsRootItem,
            _ignoreInjectedMenuItem,
            new ToolStripSeparator(),
            calibrateMenuItem,
            resetCounterMenuItem,
            new ToolStripSeparator(),
            exitMenuItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenCalibrationWindow();

        _emergencyHotkey = new GlobalEmergencyHotkey(ExitApplication);

        _statusRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusRefreshTimer.Tick += (_, _) => RefreshUiState(force: false);
        _statusRefreshTimer.Start();

        RefreshUiState(force: true);

        if (openCalibrationOnStart)
        {
            OpenCalibrationWindow();
        }
    }

    public void OpenCalibrationWindow()
    {
        if (_calibrationForm is { IsDisposed: false })
        {
            if (_calibrationForm.WindowState == FormWindowState.Minimized)
            {
                _calibrationForm.WindowState = FormWindowState.Normal;
            }

            _calibrationForm.BringToFront();
            _calibrationForm.Activate();
            return;
        }

        _calibrationForm = new CalibrationForm(_engine, _calibrationTracker, SetThreshold);
        _calibrationForm.FormClosed += (_, _) => _calibrationForm = null;
        _calibrationForm.Show();
    }

    private void TogglePause()
    {
        _engine.IsPaused = !_engine.IsPaused;
        _settings.StartPaused = _engine.IsPaused;
        SaveSettingsSafe();
        RefreshUiState(force: true);
    }

    private void SetThreshold(double thresholdMs)
    {
        double clamped = Math.Clamp(thresholdMs, AppSettings.MinThresholdMs, AppSettings.MaxThresholdMs);
        _engine.ThresholdMs = clamped;
        _settings.ThresholdMs = clamped;
        SaveSettingsSafe();

        if (_calibrationForm is { IsDisposed: false })
        {
            _calibrationForm.SyncThresholdDisplay(clamped);
        }

        RefreshUiState(force: true);
    }

    private void RefreshUiState(bool force)
    {
        long totalBlocked = _engine.TotalBlockedClicks;
        if (!force && totalBlocked == _lastObservedBlockedCount)
        {
            return;
        }

        _lastObservedBlockedCount = totalBlocked;

        bool isPaused = _engine.IsPaused;
        double thresholdMs = _engine.ThresholdMs;

        _notifyIcon.Icon = isPaused ? _pausedIcon : _activeIcon;

        // NotifyIcon.Text has a hard 63-character limit in WinForms/Win32.
        string tooltip = isPaused
            ? $"MouseDebounceFix (PAUSADO) | Bloq: {totalBlocked}"
            : string.Format(
                CultureInfo.InvariantCulture,
                "MouseDebounceFix ({0:0} ms) | Bloq: {1}",
                thresholdMs,
                totalBlocked);
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];

        _statsMenuItem.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Bloqueados: {0}  (Izq: {1} | Der: {2})",
            totalBlocked,
            _engine.BlockedLeftClicks,
            _engine.BlockedRightClicks);

        _pauseMenuItem.Text = isPaused ? "Reanudar filtrado" : "Pausar filtrado";

        bool matchedPreset = false;
        foreach (KeyValuePair<double, ToolStripMenuItem> pair in _presetThresholdItems)
        {
            bool isSelected = Math.Abs(pair.Key - thresholdMs) < 0.01;
            pair.Value.Checked = isSelected;
            if (isSelected)
            {
                matchedPreset = true;
            }
        }

        _customThresholdItem.Visible = !matchedPreset;
        if (!matchedPreset)
        {
            _customThresholdItem.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Personalizado: {0:0} ms",
                thresholdMs);
        }
    }

    private void SaveSettingsSafe()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep filtering active even if settings storage is temporarily unavailable.
        }
    }

    private void ExitApplication()
    {
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        CleanupResources();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CleanupResources();
        }

        base.Dispose(disposing);
    }

    private void CleanupResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Dispose();

        _calibrationTracker.IsActive = false;

        if (_calibrationForm is { IsDisposed: false })
        {
            _calibrationForm.Close();
            _calibrationForm.Dispose();
            _calibrationForm = null;
        }

        _emergencyHotkey.Dispose();
        _mouseHook.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }
}
