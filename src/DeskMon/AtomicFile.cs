using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
        // a copy is not atomic. A name of its own per write, not path + ".tmp":
        // two writes to one file at once - one queued, one direct on the way
        // out - would otherwise fight over the same temporary file, and the
        // loser's save was dropped without a word.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";

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

    // --- Off the UI thread ----------------------------------------------------
    //
    // The flush above waits until the disk has the data, which on a busy disk -
    // a game loading, Windows Update, a Defender scan - can take seconds. Done
    // on the UI thread at the end of every drag, every fold and every in-game
    // checkpoint, that froze every DeskMon window for as long. Work that has to
    // reach the disk but nobody waits on goes to one writer thread instead: one
    // thread, so the writes land in the order they were asked for.

    private static readonly Queue<Action> Pending = new();
    private static readonly object PendingLock = new();
    private static Thread? _writer;

    /// <summary>A job is being run, not only queued - <see cref="Flush"/> waits for both.</summary>
    private static bool _busy;

    /// <summary>
    /// Runs <paramref name="work"/> on the writer thread, after everything
    /// queued before it. For file work only; its errors are logged, not thrown,
    /// since whoever asked has moved on.
    /// </summary>
    public static void Later(Action work)
    {
        lock (PendingLock)
        {
            Pending.Enqueue(work);
            if (_writer is null)
            {
                // Background, so a hang on a dead network drive can't keep
                // the process alive after exit - Flush bounds the wait instead.
                _writer = new Thread(Drain) { IsBackground = true, Name = "DeskMon file writer" };
                _writer.Start();
            }
            Monitor.PulseAll(PendingLock);
        }
    }

    private static void Drain()
    {
        while (true)
        {
            Action work;
            lock (PendingLock)
            {
                _busy = false;
                Monitor.PulseAll(PendingLock);   // a Flush may be waiting on the queue going quiet
                while (Pending.Count == 0)
                    Monitor.Wait(PendingLock);
                work = Pending.Dequeue();
                _busy = true;
            }

            try
            {
                // Named for the freeze log - see Activity.
                using (Activity.Begin("writing a file in the background"))
                    work();
            }
            catch (Exception ex)
            {
                App.Log("writing a file", ex);
            }
        }
    }

    /// <summary>
    /// Waits until every queued write is done, up to <paramref name="timeout"/>.
    /// For the way out: the last position and settings must reach the disk
    /// before the process ends, and a writer thread is gone with it.
    /// </summary>
    public static void Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        lock (PendingLock)
        {
            while (Pending.Count > 0 || _busy)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(PendingLock, left))
                    return;
            }
        }
    }
}
