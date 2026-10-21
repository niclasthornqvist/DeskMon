using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace DeskMon;

/// <summary>
/// ===========================================================================
///  ADD A THEME HERE.
/// ===========================================================================
/// The looks the Theme setting offers. A theme is nothing but values for
/// settings that already exist - every colour, the accent, the two fonts, the
/// corners and the glow - so picking one is the same as typing all of those
/// into the settings window, and anything a theme sets can still be changed
/// by hand afterwards. Doing so makes the theme Custom.
///
/// Custom is your own look. The first time a preset is picked over it, the
/// values it had are kept in AppSettings.CustomTheme, and picking Custom again
/// puts them back - so trying the themes out never costs the colours you had.
///
/// Each theme must set every key in <see cref="Keys"/>: a theme that left one
/// out would keep whatever the theme before it set, and the same theme would
/// look different depending on where you came from.
/// </summary>
public static class ThemeCatalogue
{
    public const string Custom = "Custom";

    /// <summary>Everything a theme decides - setting names on <see cref="AppSettings"/>.</summary>
    public static readonly string[] Keys =
    {
        "BackgroundColor", "BorderColor", "LabelColor", "HeaderColor", "SectionColor", "SeparatorColor",
        "BarColor", "BarTrackColor", "BarGradient", "BarGradientStartColor", "BarGradientEndColor",
        "NormalColor", "WarnColor", "CritColor", "UnavailableColor",
        "NotesBackgroundColor", "NotesTextColor", "AccentColor",
        "FontFamily", "WindowFontFamily", "CornerRadius", "Glow", "GlowColor",
    };

    /// <param name="Description">One line for the settings window, saying what it looks like.</param>
    public sealed record Theme(string Name, string Description, IReadOnlyDictionary<string, string> Values);

    private static Theme Make(string name, string description,
                              string background, string border, string label, string header, string section,
                              string separator, string track, string gradientStart, string gradientEnd,
                              string normal, string warn, string crit, string unavailable,
                              string windowBackground, string text, string accent,
                              string widgetFont, string windowFont, double corner, bool glow,
                              string? bar = null)
        => new(name, description, new Dictionary<string, string>
        {
            ["BackgroundColor"] = background,
            ["BorderColor"] = border,
            ["LabelColor"] = label,
            ["HeaderColor"] = header,
            ["SectionColor"] = section,
            ["SeparatorColor"] = separator,
            // A bar colour turns the gradient off; none leaves bars to the gradient.
            ["BarColor"] = bar ?? string.Empty,
            ["BarTrackColor"] = track,
            ["BarGradient"] = bar is null ? "true" : "false",
            ["BarGradientStartColor"] = gradientStart,
            ["BarGradientEndColor"] = gradientEnd,
            ["NormalColor"] = normal,
            ["WarnColor"] = warn,
            ["CritColor"] = crit,
            ["UnavailableColor"] = unavailable,
            ["NotesBackgroundColor"] = windowBackground,
            ["NotesTextColor"] = text,
            ["AccentColor"] = accent,
            ["FontFamily"] = widgetFont,
            ["WindowFontFamily"] = windowFont,
            ["CornerRadius"] = corner.ToString(CultureInfo.InvariantCulture),
            ["Glow"] = glow ? "true" : "false",
            // Every theme glows in its accent; a colour of your own makes it Custom.
            ["GlowColor"] = string.Empty,
        });

    // The widget's font is a fixed-width one wherever the theme allows - its
    // numbers change every second, and in a proportional font they jump about.
    // Only fonts every Windows 10 and 11 PC has, so no theme falls back to
    // something it wasn't designed with.
    public static readonly Theme[] Themes =
    {
        Make("Dark", "DeskMon's own: charcoal glass, soft grey labels and a blue accent.",
             background: "#E6121216", border: "#33FFFFFF", label: "#FF9AA4B2", header: "#FF7F8A9B", section: "#FFE2E8F0",
             separator: "#2EFFFFFF", track: "#26FFFFFF", gradientStart: "#FF22D3EE", gradientEnd: "#FFA855F7",
             normal: "#FF4ADE80", warn: "#FFFBBF24", crit: "#FFF87171", unavailable: "#FF6B7280",
             windowBackground: "#F2121216", text: "#FFE6EAF0", accent: "#FF60A5FA",
             widgetFont: "Consolas", windowFont: "Calibri", corner: 10, glow: false),

        Make("Light", "White panels with dark text, for a bright desktop or daytime.",
             background: "#EBF8FAFC", border: "#26000000", label: "#FF4B5563", header: "#FF6B7280", section: "#FF111827",
             separator: "#1F000000", track: "#1A000000", gradientStart: "#FF3B82F6", gradientEnd: "#FF8B5CF6",
             normal: "#FF16A34A", warn: "#FFD97706", crit: "#FFDC2626", unavailable: "#FF9CA3AF",
             windowBackground: "#FAF8FAFC", text: "#FF1F2937", accent: "#FF2563EB",
             widgetFont: "Consolas", windowFont: "Segoe UI", corner: 10, glow: false),

        Make("Cyberpunk", "Night City: hot pink and electric yellow on deep violet, with a glow.",
             background: "#E80D0221", border: "#99FF2A6D", label: "#FF05D9E8", header: "#FFFF2A6D", section: "#FFFCEE0A",
             separator: "#4DFF2A6D", track: "#2605D9E8", gradientStart: "#FFFF2A6D", gradientEnd: "#FFFCEE0A",
             normal: "#FF00FF9F", warn: "#FFFCEE0A", crit: "#FFFF2A6D", unavailable: "#FF5C4B7A",
             windowBackground: "#F50D0221", text: "#FFE0F7FF", accent: "#FFFCEE0A",
             widgetFont: "Lucida Console", windowFont: "Bahnschrift", corner: 2, glow: true),

        Make("Neon", "Black glass lit by cyan and magenta tubes.",
             background: "#EB05050A", border: "#9900FFFF", label: "#FF7DF9FF", header: "#FFB967FF", section: "#FFFFFFFF",
             separator: "#4000FFFF", track: "#2600FFFF", gradientStart: "#FF00FFFF", gradientEnd: "#FFFF00E5",
             normal: "#FF39FF14", warn: "#FFFFF01F", crit: "#FFFF3131", unavailable: "#FF4B4B6B",
             windowBackground: "#F505050A", text: "#FFF0FAFF", accent: "#FFFF00E5",
             widgetFont: "Consolas", windowFont: "Segoe UI", corner: 14, glow: true),

        Make("Fantasy", "An old tome: dark leather, parchment text and gold leaf.",
             background: "#EB1E140C", border: "#99C9A227", label: "#FFD8C3A5", header: "#FFA88B5E", section: "#FFF2D492",
             separator: "#4DC9A227", track: "#26F2D492", gradientStart: "#FF8B1E3F", gradientEnd: "#FFE0B84F",
             normal: "#FF8FBF6A", warn: "#FFE0A040", crit: "#FFD0473A", unavailable: "#FF6E5A44",
             windowBackground: "#F5241810", text: "#FFF0E2C8", accent: "#FFC9A227",
             widgetFont: "Palatino Linotype", windowFont: "Palatino Linotype", corner: 12, glow: false),

        Make("Futuristic", "A starship console: navy glass, ice-blue lines, a faint glow.",
             background: "#E00A1420", border: "#8033D1FF", label: "#FF9FD8FF", header: "#FF5E8BA8", section: "#FFE6F7FF",
             separator: "#4033D1FF", track: "#2633D1FF", gradientStart: "#FF33D1FF", gradientEnd: "#FF7C4DFF",
             normal: "#FF2EF2B4", warn: "#FFFFC857", crit: "#FFFF5C7A", unavailable: "#FF3E5870",
             windowBackground: "#F00A1420", text: "#FFE6F7FF", accent: "#FF33D1FF",
             widgetFont: "Consolas", windowFont: "Bahnschrift", corner: 4, glow: true),

        Make("Retro terminal", "A green phosphor screen from 1983, square corners and all.",
             background: "#F5000A02", border: "#8033FF33", label: "#FF33FF33", header: "#FF1FAF1F", section: "#FF8CFF8C",
             separator: "#4033FF33", track: "#2633FF33", gradientStart: "#FF1F8F1F", gradientEnd: "#FF66FF66",
             normal: "#FF33FF33", warn: "#FFFFB000", crit: "#FFFF4040", unavailable: "#FF1F6F1F",
             windowBackground: "#FA000A02", text: "#FF33FF33", accent: "#FF33FF33",
             widgetFont: "Consolas", windowFont: "Lucida Console", corner: 0, glow: true),

        Make("Minimalist", "Quiet greys and one colour of bar: nothing that isn't needed.",
             background: "#F2161616", border: "#14FFFFFF", label: "#FFA3A3A3", header: "#FF737373", section: "#FFF5F5F5",
             separator: "#14FFFFFF", track: "#1AFFFFFF", gradientStart: "#FFD4D4D4", gradientEnd: "#FFFFFFFF",
             normal: "#FFD4D4D4", warn: "#FFFACC15", crit: "#FFF87171", unavailable: "#FF525252",
             windowBackground: "#FA161616", text: "#FFF5F5F5", accent: "#FFE5E5E5",
             widgetFont: "Segoe UI", windowFont: "Segoe UI", corner: 4, glow: false, bar: "#FFD4D4D4"),

        Make("Ocean", "Deep sea blues with teal and coral.",
             background: "#E6061A26", border: "#4D2DD4BF", label: "#FF94C7D6", header: "#FF5E8FA3", section: "#FFE0F7FA",
             separator: "#332DD4BF", track: "#262DD4BF", gradientStart: "#FF0EA5E9", gradientEnd: "#FF2DD4BF",
             normal: "#FF2DD4BF", warn: "#FFFBBF24", crit: "#FFFB7185", unavailable: "#FF3F6474",
             windowBackground: "#F2061A26", text: "#FFE0F7FA", accent: "#FF2DD4BF",
             widgetFont: "Consolas", windowFont: "Segoe UI", corner: 12, glow: false),

        Make("Sunset", "Warm plum dusk, orange and pink.",
             background: "#E61F0F1C", border: "#4DFB923C", label: "#FFF5C6B8", header: "#FFB98A8A", section: "#FFFFE4D6",
             separator: "#33FB923C", track: "#26FB923C", gradientStart: "#FFF43F5E", gradientEnd: "#FFFBBF24",
             normal: "#FFA3E635", warn: "#FFFBBF24", crit: "#FFF43F5E", unavailable: "#FF6B4A5E",
             windowBackground: "#F21F0F1C", text: "#FFFFF1EA", accent: "#FFFB923C",
             widgetFont: "Consolas", windowFont: "Candara", corner: 12, glow: false),

        Make("Forest", "Moss and pine, with the warmth of a lantern.",
             background: "#E60E1A12", border: "#4D84CC16", label: "#FFB5CDA3", header: "#FF7C9470", section: "#FFECF5E4",
             separator: "#3384CC16", track: "#2684CC16", gradientStart: "#FF166534", gradientEnd: "#FFA3E635",
             normal: "#FF84CC16", warn: "#FFF59E0B", crit: "#FFEF4444", unavailable: "#FF4A5E44",
             windowBackground: "#F20E1A12", text: "#FFECF5E4", accent: "#FF84CC16",
             widgetFont: "Consolas", windowFont: "Constantia", corner: 10, glow: false),

        Make("Dracula", "The well-loved coder's palette: purple, pink and green on grey.",
             background: "#EB282A36", border: "#4DBD93F9", label: "#FFBFC4E0", header: "#FF6272A4", section: "#FFF8F8F2",
             separator: "#3344475A", track: "#2644475A", gradientStart: "#FFBD93F9", gradientEnd: "#FFFF79C6",
             normal: "#FF50FA7B", warn: "#FFF1FA8C", crit: "#FFFF5555", unavailable: "#FF6272A4",
             windowBackground: "#F5282A36", text: "#FFF8F8F2", accent: "#FFBD93F9",
             widgetFont: "Consolas", windowFont: "Segoe UI", corner: 8, glow: false),

        Make("Nord", "Arctic calm: frosted blues on slate.",
             background: "#EB2E3440", border: "#4D88C0D0", label: "#FFD8DEE9", header: "#FF81A1C1", section: "#FFECEFF4",
             separator: "#334C566A", track: "#264C566A", gradientStart: "#FF5E81AC", gradientEnd: "#FF88C0D0",
             normal: "#FFA3BE8C", warn: "#FFEBCB8B", crit: "#FFBF616A", unavailable: "#FF4C566A",
             windowBackground: "#F52E3440", text: "#FFECEFF4", accent: "#FF88C0D0",
             widgetFont: "Consolas", windowFont: "Segoe UI", corner: 8, glow: false),
    };

    /// <summary>Custom first, then the presets - what the Theme dropdown lists.</summary>
    public static string[] Names => Themes.Select(t => t.Name).Prepend(Custom).ToArray();

    public static Theme? Find(string name)
        => Themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the dropdown says about a theme, for the line under it.</summary>
    public static string Describe(string name)
        => Find(name)?.Description
           ?? "Your own colours and fonts, as set below. Pick a theme to try it - Custom brings yours back.";

    /// <summary>
    /// Switches to <paramref name="name"/>: a preset's values written into the
    /// settings, or Custom's saved ones put back. Leaving Custom first saves it,
    /// so it can be returned to. Every key changed is marked edited, so the
    /// save writes it - see AppSettings.MarkEdited.
    /// </summary>
    public static void Apply(AppSettings settings, string name)
    {
        var theme = Find(name);
        string target = theme?.Name ?? Custom;

        if (settings.Theme == Custom && target != Custom)
        {
            settings.CustomTheme = Snapshot(settings);
            settings.MarkEdited(nameof(AppSettings.CustomTheme));
        }

        var values = theme?.Values ?? settings.CustomTheme;
        foreach (var (key, value) in values)
            Set(settings, key, value);

        settings.Theme = target;
        settings.MarkEdited(nameof(AppSettings.Theme));
    }

    /// <summary>
    /// For a setting changed by hand: one a theme decides makes the theme Custom,
    /// and Custom remembers it, so the edit is what Custom brings back later.
    /// </summary>
    public static void Edited(AppSettings settings, string key)
    {
        if (!Keys.Contains(key))
            return;

        settings.CustomTheme = Snapshot(settings);
        settings.MarkEdited(nameof(AppSettings.CustomTheme));

        if (settings.Theme != Custom)
        {
            settings.Theme = Custom;
            settings.MarkEdited(nameof(AppSettings.Theme));
        }
    }

    /// <summary>The theme's settings as they are now, as strings.</summary>
    private static Dictionary<string, string> Snapshot(AppSettings settings)
    {
        var values = new Dictionary<string, string>();
        foreach (string key in Keys)
            if (typeof(AppSettings).GetProperty(key)?.GetValue(settings) is { } value)
                values[key] = value switch
                {
                    bool b => b ? "true" : "false",
                    double d => d.ToString(CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty,
                };
        return values;
    }

    private static void Set(AppSettings settings, string key, string value)
    {
        if (typeof(AppSettings).GetProperty(key) is not { CanWrite: true } property)
            return;

        object? typed = property.PropertyType switch
        {
            var t when t == typeof(bool) => bool.TryParse(value, out bool b) ? b : null,
            var t when t == typeof(double) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null,
            var t when t == typeof(string) => value,
            _ => null,
        };
        if (typed is null)
            return;

        property.SetValue(settings, typed);
        settings.MarkEdited(key);
    }
}
