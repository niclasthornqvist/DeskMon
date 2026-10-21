using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskMon.Notes;

/// <summary>
/// The pictures starred in the pictures window, one game at a time: a
/// favourites.txt in the game's screenshots folder, a file name per line.
///
/// Beside the pictures rather than in settings.json, so they go wherever the
/// pictures go - a notes folder moved or copied to another PC keeps its stars
/// - and a game's are gone with its folder. Plain text, so it can be read and
/// edited by hand like everything else DeskMon keeps.
/// </summary>
public static class PictureFavourites
{
    private const string FileName = "favourites.txt";

    /// <summary>The starred file names in <paramref name="folder"/>; empty when there are none or the file can't be read.</summary>
    public static HashSet<string> Load(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(folder, FileName);
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                    if (line.Trim() is { Length: > 0 } name)
                        names.Add(name);
        }
        catch
        {
            // Unreadable: no stars, rather than no pictures window.
        }

        return names;
    }

    /// <summary>
    /// Stars the picture, or takes its star away. True if it is a favourite
    /// now. Names of pictures no longer in the folder are dropped as it
    /// writes, so a deleted picture's star doesn't linger in the file.
    /// </summary>
    public static bool Toggle(string folder, string name)
    {
        var names = Load(folder);
        bool now = !names.Remove(name);
        if (now)
            names.Add(name);

        names.RemoveWhere(n => !File.Exists(Path.Combine(folder, n)));

        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(Path.Combine(folder, FileName),
                                string.Join(Environment.NewLine, names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
        return now;
    }
}
