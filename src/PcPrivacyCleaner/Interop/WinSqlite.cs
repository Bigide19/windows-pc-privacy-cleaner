using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PcPrivacyCleaner.Interop
{
    /// <summary>
    /// Read-only helper over winsqlite3.dll, which ships with Windows 10 and later (System32).
    /// Browsers keep saved-password sites, cookies and history in SQLite files; this reads site names
    /// and counts from them without bundling a SQLite library.
    /// winsqlite3.dll is built with __stdcall (on x64 there is only one convention anyway).
    /// </summary>
    internal static class WinSqlite
    {
        private const string Dll = "winsqlite3.dll";
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_OPEN_READWRITE = 0x2;

        [DllImport(Dll, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Dll, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Close(IntPtr db);

        [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Prepare(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

        [DllImport(Dll, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.StdCall)]
        private static extern int Step(IntPtr stmt);

        [DllImport(Dll, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.StdCall)]
        private static extern int ColumnCount(IntPtr stmt);

        [DllImport(Dll, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr ColumnText(IntPtr stmt, int col);

        [DllImport(Dll, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.StdCall)]
        private static extern int ColumnBytes(IntPtr stmt, int col);

        [DllImport(Dll, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Finalize(IntPtr stmt);

        private static byte[] Utf8z(string s)
        {
            return Encoding.UTF8.GetBytes(s + "\0");
        }

        /// <summary>
        /// Runs several queries and returns at most maxRows rows for each. A query that fails
        /// (for example a table that an older browser version does not have) yields null; the rest still run.
        /// The file is opened read-write because it is always our own temp copy (WAL files need it).
        /// </summary>
        public static List<string[]>[] Run(string file, string[] sqls, int maxRows)
        {
            IntPtr db;
            if (Open(Utf8z(file), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
            {
                if (db != IntPtr.Zero) Close(db);
                throw new InvalidOperationException("SQLite open failed");
            }
            var results = new List<string[]>[sqls.Length];
            try
            {
                for (int q = 0; q < sqls.Length; q++)
                {
                    IntPtr stmt;
                    if (Prepare(db, Utf8z(sqls[q]), -1, out stmt, IntPtr.Zero) != SQLITE_OK || stmt == IntPtr.Zero)
                    {
                        results[q] = null;
                        continue;
                    }
                    var rows = new List<string[]>();
                    try
                    {
                        int cols = ColumnCount(stmt);
                        while (rows.Count < maxRows && Step(stmt) == SQLITE_ROW)
                        {
                            var row = new string[cols];
                            for (int c = 0; c < cols; c++)
                            {
                                IntPtr p = ColumnText(stmt, c);
                                int n = ColumnBytes(stmt, c);
                                if (p == IntPtr.Zero || n <= 0) { row[c] = ""; continue; }
                                var buf = new byte[n];
                                Marshal.Copy(p, buf, 0, n);
                                row[c] = Encoding.UTF8.GetString(buf);
                            }
                            rows.Add(row);
                        }
                    }
                    finally
                    {
                        Finalize(stmt);
                    }
                    results[q] = rows;
                }
            }
            finally
            {
                Close(db);
            }
            return results;
        }
    }
}
