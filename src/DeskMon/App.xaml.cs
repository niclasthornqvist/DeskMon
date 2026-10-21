using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DeskMon;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The shared styles at application level as well as in each window,
        // for the tooltip style in them: a tooltip opens in a popup of its
        // own, and the application's resources are the one place sure to be
        // searched from there. Done here because App.xaml's resources are
        // never loaded - see the note in it.
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DeskMon;component/Ui/PanelStyles.xaml", UriKind.Relative),
        });

        // The sensor dumps are checked before the single-instance lock on
        // purpose. They are diagnostics, not a second widget: taking the mutex
        // first meant that asking for a dump while the widget was running -
        // which, with autostart, is always - exited silently and wrote no file,
        // exactly when the dump was wanted. They never show a window, so two
        // copies of DeskMon are never on screen at once.
        if (HasFlag(e.Args, "--list-sensors"))
        {
            DumpSensorsAndExit(openInNotepad: true);
            return;
        }

        // Same dump plus the values the widget would show, written silently.
        // Useful when something reads "n/a" and you want to know why.
        if (HasFlag(e.Args, "--selftest"))
        {
            DumpSensorsAndExit(openInNotepad: false);
            return;
        }

        // The scheduled task and a manual double-click can easily both fire;
        // a second copy would just draw on top of the first.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\DeskMon.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // A widget that lives at the edge of the screen should never greet the
        // user with a crash dialog; record it and carry on - see OnUnhandledException.
        DispatcherUnhandledException += OnUnhandledException;

        // That hook only ever sees the UI thread, and plenty happens off it: a
        // sensor read, a notes save, the hardware query. Those used to leave
        // nothing behind at all - no dialog, no log, just a process that went
        // away or a feature that stopped - while the troubleshooting guide sent
        // you to error.log. Now they land there too.
        AppDomain.CurrentDomain.UnhandledException +=
            (_, args) => Log("background thread", args.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("unobserved task", args.Exception);
            args.SetObserved();   // logged, so don't let it tear the process down
        };

        new MainWindow().Show();
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "DeskMon.exe --list-sensors" writes every sensor on the machine to
    /// sensors.txt and opens it - the reference list for adding new metrics.
    /// </summary>
    private void DumpSensorsAndExit(bool openInNotepad)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sensors.txt");
        try
        {
            var settings = AppSettings.Load();
            // Reads every time: a one-shot dump wants fresh values, not reused ones.
            using var hub = new Sensors.SensorHub(settings.PreferredGpu, 0, allHardware: true);

            // Two reads: CPU load is a delta, so the first one is always null.
            hub.Read();
            Thread.Sleep(600);
            var snapshot = hub.Read();

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== What the widget reads ===");
            report.AppendLine($"  CpuLoad       {Show(snapshot.CpuLoad)} %");
            report.AppendLine($"  RamLoad       {Show(snapshot.RamLoad)} %   ({Show(snapshot.RamUsedGb)} / {Show(snapshot.RamTotalGb)} GB)");
            report.AppendLine($"  SwapLoad      {Show(snapshot.SwapLoad)} %   ({Show(snapshot.SwapUsedGb)} / {Show(snapshot.SwapTotalGb)} GB)");
            report.AppendLine($"  CpuTemp       {Show(snapshot.CpuTemp)} C");
            report.AppendLine($"  GpuTemp       {Show(snapshot.GpuTemp)} C");
            report.AppendLine($"  CpuPower      {Show(snapshot.CpuPower)} W");
            report.AppendLine($"  CpuClock      {Show(snapshot.CpuClock)} MHz");
            report.AppendLine($"  GpuLoad       {Show(snapshot.GpuLoad)} %");
            report.AppendLine($"  GpuMemoryLoad {Show(snapshot.GpuMemoryLoad)} %");
            report.AppendLine($"  GpuPower      {Show(snapshot.GpuPower)} W");
            report.AppendLine();
            report.AppendLine("=== Every sensor on this machine ===");
            report.AppendLine(hub.DumpSensors());

            File.WriteAllText(path, report.ToString());

            if (openInNotepad)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "DeskMon --list-sensors");
        }

        Shutdown();
    }

    private static string Show(double? value)
        => value?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";

    /// <summary>When the UI thread's recent errors happened - see <see cref="OnUnhandledException"/>.</summary>
    private readonly Queue<DateTime> _recentErrors = new();

    private const int ErrorsBeforeGivingUp = 5;
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Logs the error and carries on. This used to shut DeskMon down on the
    /// first one, and much of the UI runs in async void handlers - the tick,
    /// picture loading, the art lookups - so any one unexpected fault made the
    /// widget vanish mid-game with nothing to show for it but error.log. One
    /// feature failing once is better than the whole program going.
    ///
    /// Only an error repeating fast - five within a minute, which is a fault
    /// on the polling path that every tick hits again - still shuts down, so
    /// a broken state can't spin and fill the log for hours.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui thread", e.Exception);
        e.Handled = true;

        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (now - _recentErrors.Peek() > ErrorWindow)
            _recentErrors.Dequeue();

        if (_recentErrors.Count >= ErrorsBeforeGivingUp)
        {
            Log("ui thread", $"{ErrorsBeforeGivingUp} errors within a minute - shutting down.");
            Shutdown();
        }
    }

    /// <summary>How much error.log may grow before it is rotated.</summary>
    private const long MaxLogBytes = 256 * 1024;

    /// <summary>
    /// Appends to app\error.log. Best-effort and silent by design - this runs
    /// when something has already gone wrong, so it must not throw on top.
    ///
    /// The file is rotated rather than left to grow, because a fault that
    /// repeats on the polling path would otherwise write a line every second
    /// for as long as the widget is up.
    /// </summary>
    internal static void Log(string context, object? error)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "error.log");

            var existing = new FileInfo(path);
            if (existing.Exists && existing.Length > MaxLogBytes)
                File.Move(path, path + ".old", overwrite: true);

            File.AppendAllText(
                path,
                $"{DateTime.Now:u}  [{context}]  {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* nothing useful left to do */ }
    }

    /// <summary>
    /// Drops the single-instance lock early. Needed before restarting in place
    /// ("Reload settings"): the replacement process checks the lock immediately,
    /// which is long before this process has finished shutting down, and it
    /// would otherwise see an instance already running and exit - leaving no
    /// widget at all.
    /// </summary>
    public void ReleaseSingleInstanceLock()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }
}
