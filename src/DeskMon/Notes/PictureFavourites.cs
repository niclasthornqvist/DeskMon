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
        // Unreadable: no stars, rather than no pictures window.
        TryLoad(folder, out var names);
        return names;
    }

    /// <summary>False when the file is there but couldn't be read - not the same as having no stars.</summary>
    private static bool TryLoad(string folder, out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(folder, FileName);
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                    if (line.Trim() is { Length: > 0 } name)
                        names.Add(name);
            return true;
        }
        catch
        {
            names.Clear();
            return false;
        }
    }

    /// <summary>
    /// Stars the picture, or takes its star away. Null when done, else why
    /// not - shown to the user, who clicked for it. Names of pictures no
    /// longer in the folder are dropped as it writes, so a deleted picture's
    /// star doesn't linger in the file.
    ///
    /// Never throws: it runs from a click, and a folder Windows wouldn't
    /// write to was an unhandled error. Nor does it write over a file it
    /// couldn't read - the stars in it would all have been replaced by the
    /// one just clicked.
    /// </summary>
    public static string? Toggle(string folder, string name)
    {
        try
        {
            if (!TryLoad(folder, out var names))
                return $"{FileName} couldn't be read, so it was left as it is";

            if (!names.Remove(name))
                names.Add(name);

            names.RemoveWhere(n => !File.Exists(Path.Combine(folder, n)));

            Directory.CreateDirectory(folder);
            AtomicFile.WriteAllText(Path.Combine(folder, FileName),
                                    string.Join(Environment.NewLine, names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
