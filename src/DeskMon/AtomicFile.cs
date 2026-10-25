using System.IO;

namespace DeskMon;

/// <summary>
/// Writes a file in a way that cannot leave a half-written one behind.
///
/// File.WriteAllText and friends truncate the file and then write it, so a
/// crash, a full disk or a power cut in between leaves it empty or cut short.
/// That costs something both times it happens here: a truncated settings.json
/// no longer parses and loads as defaults, silently discarding every
/// customisation, and a truncated notes file is notes gone - which is the one
/// thing these notes are promised not to do.
///
/// The content goes to a temporary file in the same folder, which is then
/// renamed over the target. A rename within one volume is atomic, so a reader
/// sees either the whole old file or the whole new one, never a partial one.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] contents)
        => Write(path, contents);

    /// <summary>UTF-8 without a BOM - what File.WriteAllText wrote before this.</summary>
    public static void WriteAllText(string path, string contents)
        => Write(path, new System.Text.UTF8Encoding(false).GetBytes(contents));

    private static void Write(string path, byte[] contents)
    {
        // Beside the target on purpose: a rename across volumes is a copy, and
        // a copy is not atomic.
        string temp = path + ".tmp";

        // The folder too, if it isn't there: a Windows user starting DeskMon
        // for the first time has no %APPDATA%\DeskMon yet, and the first thing
        // written - settings.json, on the first drag - would fail without it.
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);

        // Flushed through to the disk before the rename. Without it the rename
        // is atomic but the data may not be: Windows can commit the rename
        // before it writes the file's contents out, and a power cut in that
        // window leaves the real name pointing at an empty file - the exact
        // outcome this class exists to prevent.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Don't leave a stray .tmp next to the real file.
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
