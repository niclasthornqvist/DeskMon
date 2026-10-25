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

        // Before anything reads a setting or a note, the dumps below included:
        // the data moves out of app\ on the first start of 1.65 - see DataFolder.
        DataFolder.Prepare();

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

        // Started by a copy that was going down - see RestartAfterCrash. It
        // is waited for until it has really gone, not a fixed moment: Windows
        // Error Reporting can hold a crashed process for seconds while it
        // writes a dump, and one shutting down cleanly is still ending its
        // session. Started over it, this copy put up a second tray icon and
        // PresentMon, and could recover that session a second time.
        int afterCrash = Array.IndexOf(e.Args, AfterCrashFlag);
        StartedAfterCrash = afterCrash >= 0;
        if (StartedAfterCrash)
            WaitForExit(afterCrash + 1 < e.Args.Length ? e.Args[afterCrash + 1] : null);

        // The scheduled task and a manual double-click can easily both fire;
        // a second copy would just draw on top of the first.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirst);
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
        // Such an error ends the process - .NET leaves no way to carry on -
        // so a fresh copy is started on the way out, unless that would be a loop.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("background thread", args.ExceptionObject);
            if (args.IsTerminating)
                RestartAfterCrash();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("unobserved task", args.Exception);
            args.SetObserved();   // logged, so don't let it tear the process down
        };

        // A freeze throws nothing, so none of the hooks above ever see one -
        // the watchdog leaves a dump behind instead. See FreezeWatchdog.
        // Only once the widget is on screen: started before it, the window
        // being built counted as a freeze, and the dump written then paused
        // the whole start - the hardware loading too - for half a minute.
        Activity.SetUiThread();   // so the freeze log can say "the UI thread"
        var main = new MainWindow();
        main.ContentRendered += (_, _) => FreezeWatchdog.Start(Dispatcher);
        main.Show();
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "DeskMon.exe --list-sensors" writes every sensor on the machine to
    /// sensors.txt and opens it - the reference list for adding new metrics.
    /// </summary>
    private void DumpSensorsAndExit(bool openInNotepad)
    {
        // Beside settings.json in the user's folder: app\ may be locked (see DataFolder).
        string path = Path.Combine(DataFolder.Root, "sensors.txt");
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

            Directory.CreateDirectory(DataFolder.Root);   // a first run may not have it yet
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
            RestartAfterCrash();   // a fresh start often clears the broken state; the guard stops a loop
            Shutdown();
        }
    }

    private const string AfterCrashFlag = "--after-crash";

    /// <summary>
    /// Up to 15 s for the copy that started this one to be gone - by its
    /// process id, passed after --after-crash. Two seconds, as before, when
    /// there is no id to go by (a copy started by hand with the flag).
    /// </summary>
    private static void WaitForExit(string? pid)
    {
        if (!int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id))
        {
            Thread.Sleep(2000);
            return;
        }

        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(id);
            old.WaitForExit(15_000);
        }
        catch
        {
            // Already gone - nothing to wait for.
        }
    }

    /// <summary>This copy was started by one that crashed - MainWindow says so once the tray icon is up.</summary>
    public static bool StartedAfterCrash { get; private set; }

    /// <summary>When the last crash restart happened - kept outside app\, which may be what is broken.</summary>
    private static string CrashStampPath => Path.Combine(Path.GetTempPath(), "DeskMon-crash-restart.txt");

    private static readonly TimeSpan CrashRestartGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Starts a fresh copy of DeskMon as this one goes down from an error, so
    /// a crash mid-game brings the widget back within seconds rather than
    /// leaving nothing until the next logon. The open session is not lost:
    /// the new copy recovers it from its checkpoint, marked "cut short".
    ///
    /// At most once every 10 minutes: something broken at startup would
    /// otherwise crash and restart for ever. The single-instance lock is let
    /// go first, as Reload settings does, or the new copy would see this one
    /// and leave. Silent on failure - this runs while already failing.
    /// Can't help with DeskMon being killed or a native crash, which never
    /// reach .NET's handlers.
    /// </summary>
    private void RestartAfterCrash()
    {
        try
        {
            if (File.Exists(CrashStampPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CrashStampPath) < CrashRestartGap)
            {
                Log("restart after a crash", "Not restarting: DeskMon already restarted after a crash less than 10 minutes ago.");
                return;
            }

            if (Environment.ProcessPath is not { } exe)
                return;

            File.WriteAllText(CrashStampPath, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            // The new copy reads settings.json and the open session as it
            // starts; whatever this one still has queued goes down first.
            // Short: the process is already failing.
            AtomicFile.Flush(TimeSpan.FromSeconds(2));
            ReleaseSingleInstanceLock();
            // With this copy's id, so the new one can wait until it has gone - see WaitForExit.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"{AfterCrashFlag} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Log("restart after a crash", "Started a fresh copy of DeskMon.");
        }
        catch (Exception ex)
        {
            Log("restart after a crash", ex);
        }
    }

    /// <summary>How much error.log may grow before it is rotated.</summary>
    private const long MaxLogBytes = 256 * 1024;

    /// <summary>
    /// Appends to error.log in the user's DeskMon folder. Best-effort and silent by design - this runs
    /// when something has already gone wrong, so it must not throw on top.
    ///
    /// The file is rotated rather than left to grow, because a fault that
    /// repeats on the polling path would otherwise write a line every second
    /// for as long as the widget is up.
    /// </summary>
    internal static void Log(string context, object? error)
    {
        // One writer at a time: errors arrive from worker threads too - an
        // unobserved task, a background lookup - and two appends at once made
        // one of them fail, quietly losing the line that said what went wrong.
        lock (LogLock)
        {
            try
            {
                // With the rest of the user's data - see DataFolder - made if
                // need be, since this can be the first thing ever written there.
                Directory.CreateDirectory(DataFolder.Root);
                string path = Path.Combine(DataFolder.Root, "error.log");

                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length > MaxLogBytes)
                    File.Move(path, path + ".old", overwrite: true);

                File.AppendAllText(
                    path,
                    $"{DateTime.Now:u}  [{context}]  {error}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* nothing useful left to do */ }
        }
    }

    private static readonly object LogLock = new();

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

    /// <summary>
    /// Takes the lock back after a restart that never happened - Windows
    /// refused to start the replacement - so a later double-click still finds
    /// this copy running instead of starting a second one beside it.
    /// </summary>
    public void TakeSingleInstanceLock()
        => _singleInstance ??= new Mutex(initiallyOwned: true, SingleInstanceName, out _);

    private const string SingleInstanceName = @"Local\DeskMon.SingleInstance";

    protected override void OnExit(ExitEventArgs e)
    {
        // The writes queued off the UI thread - the position saved as the
        // widget closed, the last settings change - die with the process if
        // they haven't run. Bounded, so a dead drive can't hold the exit.
        AtomicFile.Flush(TimeSpan.FromSeconds(5));

        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }
}
