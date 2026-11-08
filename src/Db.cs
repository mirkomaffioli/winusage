using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinUsage
{
    /// <summary>
    /// Wrapper minimale su winsqlite3.dll, la libreria SQLite inclusa in Windows 10/11
    /// (C:\Windows\System32\winsqlite3.dll). Nessun pacchetto NuGet e nessuna DLL da distribuire.
    /// </summary>
    internal sealed class Db : IDisposable
    {
        private const string Lib = "winsqlite3.dll";
        private const CallingConvention Cc = CallingConvention.Cdecl;

        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT = 2;
        private const int SQLITE_TEXT = 3;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport(Lib, CallingConvention = Cc, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_open16(string filename, out IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_bind_null(IntPtr stmt, int idx);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long value);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_bind_double(IntPtr stmt, int idx, double value);
        [DllImport(Lib, CallingConvention = Cc, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_bind_text16(IntPtr stmt, int idx, string text, int n, IntPtr destructor);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_changes(IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        private IntPtr handle;

        public Db(string file)
        {
            int rc = sqlite3_open16(file, out handle);
            if (rc != 0)
            {
                string msg = handle != IntPtr.Zero ? ErrMsg() : "errore " + rc;
                if (handle != IntPtr.Zero) { sqlite3_close(handle); handle = IntPtr.Zero; }
                throw new InvalidOperationException("Impossibile aprire il database: " + msg);
            }
            sqlite3_busy_timeout(handle, 5000);
        }

        private string ErrMsg()
        {
            IntPtr p = sqlite3_errmsg16(handle);
            return p == IntPtr.Zero ? "errore sconosciuto" : Marshal.PtrToStringUni(p);
        }

        private IntPtr Prepare(string sql, object[] args)
        {
            IntPtr stmt;
            int rc = sqlite3_prepare16_v2(handle, sql, -1, out stmt, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException("SQL non valido (" + ErrMsg() + "): " + sql);
            try
            {
                for (int i = 0; args != null && i < args.Length; i++)
                {
                    object a = args[i];
                    int idx = i + 1;
                    int brc;
                    if (a == null) brc = sqlite3_bind_null(stmt, idx);
                    else if (a is long) brc = sqlite3_bind_int64(stmt, idx, (long)a);
                    else if (a is int) brc = sqlite3_bind_int64(stmt, idx, (int)a);
                    else if (a is bool) brc = sqlite3_bind_int64(stmt, idx, ((bool)a) ? 1 : 0);
                    else if (a is double) brc = sqlite3_bind_double(stmt, idx, (double)a);
                    else brc = sqlite3_bind_text16(stmt, idx, Convert.ToString(a), -1, SQLITE_TRANSIENT);
                    if (brc != 0) throw new InvalidOperationException("Bind fallito: " + ErrMsg());
                }
            }
            catch
            {
                sqlite3_finalize(stmt);
                throw;
            }
            return stmt;
        }

        /// <summary>Esegue un'istruzione che non restituisce righe.</summary>
        public void Execute(string sql, params object[] args)
        {
            IntPtr stmt = Prepare(sql, args);
            try
            {
                int rc;
                do { rc = sqlite3_step(stmt); } while (rc == SQLITE_ROW);
                if (rc != SQLITE_DONE) throw new InvalidOperationException("Esecuzione fallita: " + ErrMsg());
            }
            finally { sqlite3_finalize(stmt); }
        }

        /// <summary>Esegue una query e restituisce tutte le righe (long, double, string o null per colonna).</summary>
        public List<object[]> Query(string sql, params object[] args)
        {
            List<object[]> rows = new List<object[]>();
            IntPtr stmt = Prepare(sql, args);
            try
            {
                int cols = sqlite3_column_count(stmt);
                while (true)
                {
                    int rc = sqlite3_step(stmt);
                    if (rc == SQLITE_DONE) break;
                    if (rc != SQLITE_ROW) throw new InvalidOperationException("Lettura fallita: " + ErrMsg());
                    object[] row = new object[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        switch (sqlite3_column_type(stmt, c))
                        {
                            case SQLITE_INTEGER: row[c] = sqlite3_column_int64(stmt, c); break;
                            case SQLITE_FLOAT: row[c] = sqlite3_column_double(stmt, c); break;
                            case SQLITE_TEXT: row[c] = Marshal.PtrToStringUni(sqlite3_column_text16(stmt, c)); break;
                            default: row[c] = null; break;
                        }
                    }
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(stmt); }
            return rows;
        }

        /// <summary>Righe modificate dall'ultimo INSERT/UPDATE/DELETE.</summary>
        public int Changes { get { return sqlite3_changes(handle); } }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                sqlite3_close(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}
