using System;
using System.Runtime.CompilerServices;

namespace DeskMon;

/// <summary>
/// DeskMon's entry point, picked over the one WPF generates from App.xaml by
/// StartupObject in DeskMon.csproj.
///
/// It exists for the crash watcher (see App.StartWatcher). Through WPF's own
/// Main, the watcher made a whole Application - dispatcher, WPF's libraries
/// and all - just to sit and wait for one process to end: about 9.5 MB of
/// RAM, against under 5 for a bare .NET process. Here it is sent off first.
/// Everything else starts exactly as it did through the generated Main.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        int watch = Array.IndexOf(args, App.WatchFlag);
        if (watch >= 0)
        {
            App.WatchWidget(watch + 1 < args.Length ? args[watch + 1] : null);
            return;
        }

        RunWidget();
    }

    /// <summary>
    /// What the generated Main did. A method of its own, never inlined, so
    /// the JIT doesn't load WPF while compiling Main for the watcher too.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWidget()
    {
        var app = new App();
        app.Run();
    }
}
