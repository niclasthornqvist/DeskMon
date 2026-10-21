using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskMon.Notes;

/// <summary>
/// Just enough SQLite to read another program's database - GOG Galaxy's - and
/// nothing more: open, run a query, read the rows as text or numbers, close.
///
/// Through winsqlite3.dll, the SQLite that ships inside Windows 10 and 11,
/// rather than a NuGet package: that would bring a native library of its own
/// to bundle into DeskMon.exe and unpack on first start, for a few queries.
/// </summary>
internal static class WinSqlite
{
    /// <summary>One row of a result, by column number.</summary>
    public readonly struct Row
    {
        private readonly IntPtr _statement;
        internal Row(IntPtr statement) => _statement = statement;

        public string? Text(int column)
            => sqlite3_column_text(_statement, column) is var text && text != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(text)
                : null;

        public long Long(int column) => sqlite3_column_int64(_statement, column);
    }

    /// <summary>
    /// Every row <paramref name="sql"/> returns, each turned into a
    /// <typeparamref name="T"/> by <paramref name="read"/>. Throws when the
    /// file can't be opened or the query is wrong - the caller decides what
    /// that means.
    /// </summary>
    /// <param name="readOnly">
    /// True for a database another program owns: never written, never locked
    /// for writing. False only for a copy of our own.
    /// </param>
    public static List<T> Query<T>(string path, string sql, Func<Row, T> read, bool readOnly = true)
    {
        int flags = readOnly ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE;
        if (sqlite3_open_v2(Utf8(path), out IntPtr db, flags, IntPtr.Zero) != SQLITE_OK)
        {
            string error = db != IntPtr.Zero ? Message(db) : "could not open";
            sqlite3_close_v2(db);
            throw new InvalidOperationException(error);
        }

        try
        {
            // The owner may be writing at that moment; wait for it briefly
            // rather than failing straight away.
            sqlite3_busy_timeout(db, 2000);

            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr statement, IntPtr.Zero) != SQLITE_OK)
                throw new InvalidOperationException(Message(db));

            try
            {
                var rows = new List<T>();
                int step;
                while ((step = sqlite3_step(statement)) == SQLITE_ROW)
                    rows.Add(read(new Row(statement)));

                if (step != SQLITE_DONE)
                    throw new InvalidOperationException(Message(db));

                return rows;
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }
        finally
        {
            sqlite3_close_v2(db);
        }
    }

    private static string Message(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "SQLite error";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x01, SQLITE_OPEN_READWRITE = 0x02;

    private const string Library = "winsqlite3.dll";

    [DllImport(Library)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Library)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Library)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(Library)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Library)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Library)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport(Library)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
}
