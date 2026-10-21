using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DeskMon.Sensors;

/// <summary>
/// The two optional programs DeskMon leans on, and whether they are here.
///
/// Without either one DeskMon still runs - it just shows n/a where their
/// readings would go. That used to be all it did: a missing PawnIO left CPU
/// temperature at n/a with no reason given anywhere, since the sensor library
/// treats a driver it cannot load as a sensor that isn't there rather than an
/// error; and PresentMon's absence was only mentioned in a tooltip that
/// existed while a game ran. On a fresh machine that is two mysteries. This
/// names them, with the command that fixes each.
/// </summary>
public static class Prerequisites
{
    /// <param name="Name">The program, as you would search for it.</param>
    /// <param name="Effect">What doesn't work without it.</param>
    /// <param name="Install">The winget command that installs it.</param>
    public sealed record Missing(string Name, string Effect, string Install);

    /// <summary>
    /// What is missing, or an empty list. Touches the disk and the registry,
    /// so call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<Missing> Check(AppSettings settings)
    {
        var missing = new List<Missing>();

        if (!PawnIoInstalled())
            missing.Add(new Missing("PawnIO", "CPU temperature shows n/a",
                                    "winget install namazso.PawnIO"));

        // Only when FPS is wanted: with MeasureFps off, PresentMon not being
        // here is the expected state, not something to be told about.
        if (settings.MeasureFps && FpsMonitor.ResolvePresentMon(settings.PresentMonPath) is null)
            missing.Add(new Missing("PresentMon", "FPS, LOW, WORST and LAT show n/a",
                                    "winget install Intel.PresentMon.Console"));

        return missing;
    }

    /// <summary>
    /// The driver registers itself as a service, and a service is a key here.
    /// Read from the registry rather than through the service manager, which
    /// would need another package for one yes-or-no question.
    /// </summary>
    private static bool PawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch
        {
            // Can't tell. Saying nothing beats a false alarm on a machine where
            // temperatures are working.
            return true;
        }
    }

    /// <summary>The list as tooltip lines, or null when nothing is missing.</summary>
    public static string? Describe(IReadOnlyList<Missing> missing)
    {
        if (missing.Count == 0)
            return null;

        var lines = new List<string> { "Missing programs:" };
        foreach (var item in missing)
            lines.Add($"• {item.Name} - {item.Effect}.{Environment.NewLine}   Install: {item.Install}");

        return string.Join(Environment.NewLine, lines);
    }
}
