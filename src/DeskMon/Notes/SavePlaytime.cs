using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeskMon.Notes;

/// <summary>
/// Where one game keeps its playtime in its save files. Set per game in
/// settings.json under PlaytimeFromSaves, keyed by the name the GAME heading
/// shows.
/// </summary>
public sealed class SaveFileSource
{
    /// <summary>
    /// The files to look in, with a wildcard in the file name and environment
    /// variables allowed: %LOCALAPPDATA%\Dawnwalker\Saved\SaveGames\*.meta.
    /// </summary>
    public string Files { get; set; } = string.Empty;

    /// <summary>The value's name in those files, e.g. PlayTime.</summary>
    public string Key { get; set; } = "PlayTime";

    /// <summary>What the number counts: seconds, minutes or hours.</summary>
    public string Unit { get; set; } = "seconds";
}

/// <summary>
/// A game's playtime as its own save files record it - the time a save menu
/// shows - for games that aren't on Steam, so the GAME heading can count from
/// the hours played before DeskMon was installed rather than from zero.
///
/// Games store this in any number of ways, which is why the where and what
/// come from settings.json rather than from code here; see
/// <see cref="SaveFileSource"/>. Only text is read - a "Key": 123 or Key=123
/// anywhere in the file - so a JSON or INI save summary works, a binary save
/// doesn't.
/// </summary>
public static class SavePlaytime
{
    /// <summary>
    /// Save files bigger than this are skipped: the playtime worth reading is
    /// in a small summary beside the save (Dawnwalker's .meta is under 1 KB),
    /// and reading the 400 KB saves themselves on every game start would be
    /// slow for nothing.
    /// </summary>
    private const long LargestFile = 256 * 1024;

    /// <summary>
    /// The highest playtime among the game's saves, or null when it has no
    /// entry, no saves, or none that say. Highest, because each save holds the
    /// time up to the moment it was made, and the newest is the furthest along.
    /// </summary>
    public static TimeSpan? For(string game, IReadOnlyDictionary<string, SaveFileSource>? sources)
    {
        if (sources is null)
            return null;

        // Matched without regard to case: the key is typed by hand.
        var source = sources.FirstOrDefault(s => string.Equals(s.Key, game, StringComparison.OrdinalIgnoreCase)).Value;
        if (source is null || string.IsNullOrWhiteSpace(source.Files) || string.IsNullOrWhiteSpace(source.Key))
            return null;

        try
        {
            string files = Environment.ExpandEnvironmentVariables(source.Files.Trim());
            string? folder = Path.GetDirectoryName(files);
            string pattern = Path.GetFileName(files);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(pattern) || !Directory.Exists(folder))
                return null;

            // "PlayTime": 33586, PlayTime=33586 or "PlayTime": "33586".
            var value = new Regex($@"""?{Regex.Escape(source.Key.Trim())}""?\s*[:=]\s*""?(\d+(?:\.\d+)?)",
                                  RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            double? highest = null;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles(pattern))
            {
                if (file.Length > LargestFile)
                    continue;

                if (value.Match(ReadShared(file.FullName)) is { Success: true } match
                    && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                    highest = Math.Max(highest ?? 0, n);
            }

            return highest is double amount ? ToTime(amount, source.Unit) : null;
        }
        catch
        {
            // A path that doesn't resolve, a save being written as we read:
            // DeskMon's own logged time is shown instead.
            return null;
        }
    }

    private static TimeSpan? ToTime(double amount, string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "seconds" or "second" or "s" => TimeSpan.FromSeconds(amount),
        "minutes" or "minute" or "min" or "m" => TimeSpan.FromMinutes(amount),
        "hours" or "hour" or "h" => TimeSpan.FromHours(amount),
        _ => null,   // a unit we can't read is no playtime, not a wrong one
    };

    /// <summary>Shared, since the game may be writing a save at this very moment.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
