using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// The fonts installed on this machine, by name, for the font dropdowns in
/// the notepad and the settings window.
///
/// Listed once and kept: enumerating the system's fonts reads every font
/// file's names, and both windows would otherwise each pay for that - the
/// notepad on every start. Fonts installed while DeskMon runs show up after a
/// restart, which is when a newly installed font usually becomes usable
/// anyway.
/// </summary>
public static class InstalledFonts
{
    private static IReadOnlyList<string>? _names;

    public static IReadOnlyList<string> Names
        => _names ??= Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The list's own spelling of <paramref name="name"/>, or null if it isn't installed.</summary>
    public static string? Find(string? name)
        => name is null
            ? null
            : Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}
