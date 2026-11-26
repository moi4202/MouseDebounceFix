using System.Globalization;
using MouseDebounceFix.Core;

namespace MouseDebounceFix.UI;

/// <summary>
/// Real-time calibration window that drains lock-free samples from <see cref="CalibrationTracker"/>
/// on a UI timer so the WH_MOUSE_LL callback never touches UI controls or performs I/O.
/// </summary>
public sealed class CalibrationForm : Form
{
    private const int MaxDisplayedRows = 200;
    private const double FastClickFilterMs = 250.0;

    private readonly DebounceEngine _engine;
    private readonly CalibrationTracker _tracker;
    private readonly Action<double> _onApplyThreshold;

    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly List<CalibrationSample> _drainBuffer = new(64);
    private readonly List<double> _observedIntervalsMs = new(256);

    private readonly Label _lastClickLabel;
    private readonly Label _statsLabel;
    private readonly Label _suggestionLabel;
    private readonly NumericUpDown _thresholdInput;
    private readonly Button _applySuggestedButton;
    private readonly Button _applyCustomButton;
    private readonly Button _clearButton;
    private readonly CheckBox _onlyFastClicksCheckBox;
    private readonly ListView _eventsListView;
    private readonly ColumnHeader _timeColumn;
    private readonly ColumnHeader _buttonColumn;
    private readonly ColumnHeader _elapsedColumn;
    private readonly ColumnHeader _statusColumn;

    private double? _latestSuggestedThresholdMs;
    private double? _minBounceMs;
    private double? _maxBounceMs;
    private int _suspectedBounceCount;
    private bool _isResizingColumns;

    public CalibrationForm(
        DebounceEngine engine,
        CalibrationTracker tracker,
        Action<double> onApplyThreshold)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _onApplyThreshold = onApplyThreshold ?? throw new ArgumentNullException(nameof(onApplyThreshold));

        Text = "MouseDebounceFix — Modo de calibración en vivo";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);
        Size = new Size(720, 540);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var instructions = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = "Haz clics normales y dobles clics en cualquier ventana para medir los intervalos reales UP → DOWN de tu mouse.\n" +
                   "Los rebotes mecánicos suelen aparecer por debajo de 70-100 ms; los dobles clics humanos entre 130 y 250 ms."
        };

        _lastClickLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12.5f),
            Padding = new Padding(0, 8, 0, 4),
            Text = "Esperando clics..."
        };

        _statsLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = FormatStatsText()
        };

        _suggestionLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = Color.FromArgb(15, 118, 110),
            Padding = new Padding(0, 4, 0, 8),
            Text = "Umbral sugerido: aún no se detectan rebotes (< 120 ms)."
        };

        var controlsBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 8)
        };

        var thresholdLabel = new Label
        {
            AutoSize = true,
            Text = "Umbral (ms):",
            Padding = new Padding(0, 6, 4, 0)
        };

        _thresholdInput = new NumericUpDown
        {
            Minimum = (decimal)AppSettings.MinThresholdMs,
            Maximum = (decimal)AppSettings.MaxThresholdMs,
            DecimalPlaces = 0,
            Increment = 5,
            Width = 72,
            Value = Math.Clamp((decimal)Math.Round(_engine.ThresholdMs), (decimal)AppSettings.MinThresholdMs, (decimal)AppSettings.MaxThresholdMs)
        };

        _applyCustomButton = new Button
        {
            Text = "Aplicar umbral",
            AutoSize = true
        };
        _applyCustomButton.Click += (_, _) => ApplyThreshold((double)_thresholdInput.Value);

        _applySuggestedButton = new Button
        {
            Text = "Aplicar sugerido",
            AutoSize = true,
            Enabled = false
        };
        _applySuggestedButton.Click += (_, _) =>
        {
            if (_latestSuggestedThresholdMs.HasValue)
            {
                ApplyThreshold(_latestSuggestedThresholdMs.Value);
            }
        };

        _clearButton = new Button
        {
            Text = "Limpiar historial",
            AutoSize = true
        };
        _clearButton.Click += (_, _) => ClearHistory();

        _onlyFastClicksCheckBox = new CheckBox
        {
            Text = "Solo mostrar < 250 ms",
            AutoSize = true,
            Padding = new Padding(8, 4, 0, 0)
        };

        controlsBar.Controls.Add(thresholdLabel);
        controlsBar.Controls.Add(_thresholdInput);
        controlsBar.Controls.Add(_applyCustomButton);
        controlsBar.Controls.Add(_applySuggestedButton);
        controlsBar.Controls.Add(_clearButton);
        controlsBar.Controls.Add(_onlyFastClicksCheckBox);

        _eventsListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Consolas", 9.5f)
        };
        _timeColumn = _eventsListView.Columns.Add("Hora", 105);
        _buttonColumn = _eventsListView.Columns.Add("Botón", 90);
        _elapsedColumn = _eventsListView.Columns.Add("UP → DOWN (ms)", 135, HorizontalAlignment.Right);
        _statusColumn = _eventsListView.Columns.Add("Estado", 260);
        _eventsListView.SizeChanged += (_, _) => ResizeStatusColumn();
        _eventsListView.ColumnWidthChanged += (_, e) =>
        {
            if (!_isResizingColumns && e.ColumnIndex != _statusColumn.Index)
            {
                ResizeStatusColumn();
            }
        };

        var summaryPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true
        };
        summaryPanel.Controls.Add(_statsLabel);
        summaryPanel.Controls.Add(_suggestionLabel);

        root.Controls.Add(instructions, 0, 0);
        root.Controls.Add(_lastClickLabel, 0, 1);
        root.Controls.Add(summaryPanel, 0, 2);
        root.Controls.Add(controlsBar, 0, 3);
        root.Controls.Add(_eventsListView, 0, 4);

        Controls.Add(root);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _pollTimer.Tick += (_, _) => DrainSamples();
    }

    public void SyncThresholdDisplay(double thresholdMs)
    {
        if (IsDisposed)
        {
            return;
        }

        decimal clamped = Math.Clamp(
            (decimal)Math.Round(thresholdMs),
            _thresholdInput.Minimum,
            _thresholdInput.Maximum);
        _thresholdInput.Value = clamped;
        _statsLabel.Text = FormatStatsText();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyResponsiveColumnWidths();
        _tracker.Clear();
        _tracker.IsActive = true;
        _pollTimer.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyResponsiveColumnWidths();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _tracker.IsActive = false;
        _pollTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void DrainSamples()
    {
        _drainBuffer.Clear();
        int count = _tracker.DrainTo(_drainBuffer);
        if (count == 0)
        {
            return;
        }

        _eventsListView.BeginUpdate();
        try
        {
            foreach (CalibrationSample sample in _drainBuffer)
            {
                ProcessSample(sample);
            }
        }
        finally
        {
            _eventsListView.EndUpdate();
        }

        ResizeStatusColumn();
    }

    private void ProcessSample(CalibrationSample sample)
    {
        double elapsed = sample.ElapsedSinceLastUpMs;
        _observedIntervalsMs.Add(elapsed);

        if (elapsed < CalibrationTracker.MaxSuspectedChatterMs)
        {
            _suspectedBounceCount++;
            _minBounceMs = !_minBounceMs.HasValue ? elapsed : Math.Min(_minBounceMs.Value, elapsed);
            _maxBounceMs = !_maxBounceMs.HasValue ? elapsed : Math.Max(_maxBounceMs.Value, elapsed);
        }

        string buttonText = sample.Button == MouseButton.Left ? "Izquierdo" : "Derecho";
        string statusText = sample.WasBlocked
            ? "BLOQUEADO (rebote)"
            : elapsed < CalibrationTracker.MaxSuspectedChatterMs
                ? "PERMITIDO (posible rebote)"
                : "PERMITIDO";

        _lastClickLabel.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Último intervalo ({0}): {1:F1} ms — {2}",
            buttonText,
            elapsed,
            statusText);

        _lastClickLabel.ForeColor = sample.WasBlocked
            ? Color.FromArgb(185, 28, 28)
            : elapsed < CalibrationTracker.MaxSuspectedChatterMs
                ? Color.FromArgb(180, 83, 9)
                : Color.FromArgb(21, 128, 61);

        UpdateStatsAndSuggestion();

        if (_onlyFastClicksCheckBox.Checked && elapsed >= FastClickFilterMs)
        {
            return;
        }

        var item = new ListViewItem(sample.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        item.SubItems.Add(buttonText);
        item.SubItems.Add(elapsed.ToString("F2", CultureInfo.InvariantCulture));
        item.SubItems.Add(statusText);

        if (sample.WasBlocked)
        {
            item.BackColor = Color.FromArgb(254, 226, 226);
            item.ForeColor = Color.FromArgb(153, 27, 27);
        }
        else if (elapsed < CalibrationTracker.MaxSuspectedChatterMs)
        {
            item.BackColor = Color.FromArgb(254, 243, 199);
            item.ForeColor = Color.FromArgb(146, 64, 14);
        }

        _eventsListView.Items.Insert(0, item);
        while (_eventsListView.Items.Count > MaxDisplayedRows)
        {
            _eventsListView.Items.RemoveAt(_eventsListView.Items.Count - 1);
        }
    }

    private void UpdateStatsAndSuggestion()
    {
        _statsLabel.Text = FormatStatsText();

        _latestSuggestedThresholdMs = CalibrationTracker.SuggestThresholdMs(_observedIntervalsMs);
        if (_latestSuggestedThresholdMs.HasValue)
        {
            _suggestionLabel.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Umbral sugerido: {0:F0} ms (rebote máx. {1:F1} ms + {2:F0} ms de margen)",
                _latestSuggestedThresholdMs.Value,
                _maxBounceMs ?? 0.0,
                CalibrationTracker.RecommendedSafetyMarginMs);
            _applySuggestedButton.Enabled = true;
        }
        else
        {
            _suggestionLabel.Text = "Umbral sugerido: aún no se detectan rebotes (< 120 ms).";
            _applySuggestedButton.Enabled = false;
        }
    }

    private string FormatStatsText()
    {
        string bounceRange = _suspectedBounceCount > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} detectados (mín: {1:F1} ms | máx: {2:F1} ms)",
                _suspectedBounceCount,
                _minBounceMs ?? 0.0,
                _maxBounceMs ?? 0.0)
            : "0 detectados";

        return string.Format(
            CultureInfo.InvariantCulture,
            "Umbral activo: {0:F0} ms   |   Bloqueados en sesión: {1}   |   Rebotes < 120 ms: {2}",
            _engine.ThresholdMs,
            _engine.TotalBlockedClicks,
            bounceRange);
    }

    private void ApplyThreshold(double thresholdMs)
    {
        _onApplyThreshold(thresholdMs);
        SyncThresholdDisplay(thresholdMs);
    }

    private void ClearHistory()
    {
        _tracker.Clear();
        _drainBuffer.Clear();
        _observedIntervalsMs.Clear();
        _minBounceMs = null;
        _maxBounceMs = null;
        _suspectedBounceCount = 0;
        _latestSuggestedThresholdMs = null;
        _eventsListView.Items.Clear();
        ResizeStatusColumn();
        _lastClickLabel.ForeColor = SystemColors.ControlText;
        _lastClickLabel.Text = "Historial limpiado. Esperando clics...";
        UpdateStatsAndSuggestion();
    }

    private void ApplyResponsiveColumnWidths()
    {
        if (IsDisposed || _eventsListView.IsDisposed || _isResizingColumns)
        {
            return;
        }

        _isResizingColumns = true;
        try
        {
            int padding = LogicalToDeviceUnits(20);
            int timeWidth = Math.Max(
                LogicalToDeviceUnits(105),
                TextRenderer.MeasureText("00:00:00.000", _eventsListView.Font).Width + padding);
            int buttonWidth = Math.Max(
                LogicalToDeviceUnits(90),
                TextRenderer.MeasureText("Izquierdo", _eventsListView.Font).Width + padding);
            int elapsedWidth = Math.Max(
                LogicalToDeviceUnits(135),
                TextRenderer.MeasureText("UP → DOWN (ms)", _eventsListView.Font).Width + padding);

            _timeColumn.Width = timeWidth;
            _buttonColumn.Width = buttonWidth;
            _elapsedColumn.Width = elapsedWidth;
        }
        finally
        {
            _isResizingColumns = false;
        }

        ResizeStatusColumn();
    }

    private void ResizeStatusColumn()
    {
        if (IsDisposed || _eventsListView.IsDisposed || !_eventsListView.IsHandleCreated || _isResizingColumns)
        {
            return;
        }

        _isResizingColumns = true;
        try
        {
            int usedWidth = _timeColumn.Width + _buttonColumn.Width + _elapsedColumn.Width;
            int minStatusWidth = Math.Max(
                LogicalToDeviceUnits(220),
                TextRenderer.MeasureText("PERMITIDO (posible rebote)", _eventsListView.Font).Width + LogicalToDeviceUnits(24));

            int availableWidth = _eventsListView.ClientSize.Width - usedWidth - LogicalToDeviceUnits(2);
            int targetWidth = Math.Max(minStatusWidth, availableWidth);
            if (_statusColumn.Width != targetWidth)
            {
                _statusColumn.Width = targetWidth;
            }
        }
        finally
        {
            _isResizingColumns = false;
        }
    }
}
