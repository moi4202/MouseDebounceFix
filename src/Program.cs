using MouseDebounceFix.Core;
using MouseDebounceFix.Native;
using MouseDebounceFix.UI;

namespace MouseDebounceFix;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\MouseDebounceFix_SingleInstance_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "MouseDebounceFix ya se encuentra en ejecución en la bandeja del sistema.",
                "MouseDebounceFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        bool openCalibrationOnStart = args.Any(arg =>
            string.Equals(arg, "--calibrate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-c", StringComparison.OrdinalIgnoreCase));

        AppSettings settings = AppSettings.Load();
        var engine = new DebounceEngine(
            thresholdMs: settings.ThresholdMs,
            filterLeftButton: settings.FilterLeftButton,
            filterRightButton: settings.FilterRightButton)
        {
            IsPaused = settings.StartPaused
        };

        var calibrationTracker = new CalibrationTracker();
        using var mouseHook = new LowLevelMouseHook(
            engine,
            calibrationTracker,
            ignoreInjectedEvents: settings.IgnoreInjectedEvents);

        // Guarantee the global Win32 mouse hook is always removed on abnormal termination.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => mouseHook.Dispose();
        Application.ThreadException += (_, e) =>
        {
            mouseHook.Dispose();
            MessageBox.Show(
                $"Ocurrió un error inesperado y el hook del mouse se desactivó por seguridad:\n\n{e.Exception.Message}",
                "MouseDebounceFix — Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Application.Exit();
        };

        try
        {
            mouseHook.Install();

            using var trayContext = new TrayAppContext(
                engine,
                settings,
                mouseHook,
                calibrationTracker,
                openCalibrationOnStart);

            Application.Run(trayContext);
        }
        catch (Exception ex)
        {
            mouseHook.Dispose();
            MessageBox.Show(
                $"No se pudo iniciar MouseDebounceFix:\n\n{ex.Message}",
                "MouseDebounceFix — Error de inicio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            mouseHook.Dispose();
        }
    }
}
