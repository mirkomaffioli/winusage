using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinUsage
{
    /// <summary>I tre timer tracciati.</summary>
    internal static class TimerIds
    {
        public const int System = 0;   // tempo di utilizzo del sistema dall'avvio
        public const int Browser = 1;  // browser in primo piano
        public const int Steam = 2;    // Steam (gioco avviato da Steam o client in primo piano)
        public const int Count = 3;

        public static readonly string[] Names = { "system", "browser", "steam" };
        public static readonly string[] Labels = { "Sistema", "Browser", "Steam" };
    }

    /// <summary>Formattazione dei tempi in italiano.</summary>
    internal static class Fmt
    {
        /// <summary>"01:30:05"</summary>
        public static string Clock(double seconds)
        {
            long s = (long)Math.Max(0, seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", s / 3600, (s / 60) % 60, s % 60);
        }

        /// <summary>"1h 05m", "12m", "30s"</summary>
        public static string Compact(double seconds)
        {
            long s = (long)Math.Max(0, seconds);
            long h = s / 3600, m = (s / 60) % 60;
            if (h > 0) return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", h, m);
            if (m > 0) return string.Format(CultureInfo.InvariantCulture, "{0}m", m);
            return string.Format(CultureInfo.InvariantCulture, "{0}s", s);
        }

        /// <summary>Per il toast pixel art (solo maiuscole): "2 ORE 30 MIN", "1 ORA", "45 MIN".</summary>
        public static string ToastTime(double seconds)
        {
            long totalMin = (long)Math.Round(Math.Max(0, seconds) / 60.0);
            long h = totalMin / 60, m = totalMin % 60;
            string hs = h == 1 ? "1 ORA" : h + " ORE";
            if (h == 0) return m + " MIN";
            if (m == 0) return hs;
            return hs + " " + m + " MIN";
        }

        /// <summary>"1 ora e 30 minuti", "2 ore", "45 minuti" (arrotondato al minuto).</summary>
        public static string Long(double seconds)
        {
            long totalMin = (long)Math.Round(Math.Max(0, seconds) / 60.0);
            long h = totalMin / 60, m = totalMin % 60;
            string hs = h == 1 ? "1 ora" : h + " ore";
            string ms = m == 1 ? "1 minuto" : m + " minuti";
            if (h == 0) return ms;
            if (m == 0) return hs;
            return hs + " e " + ms;
        }
    }

    /// <summary>Log molto semplice su file (%LOCALAPPDATA%\WinUsage\winusage.log).</summary>
    internal static class Log
    {
        public static string FilePath;

        public static void Write(string message)
        {
            try
            {
                if (FilePath == null) return;
                FileInfo fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { /* il log non deve mai causare errori */ }
        }

        public static void Write(Exception ex)
        {
            Write(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Configurazione dell'utente corrente (salvata nel suo database SQLite).</summary>
    internal sealed class AppSettings
    {
        public const string DefaultBrowsers =
            "chrome,msedge,firefox,brave,opera,opera_gx,vivaldi,iexplore,chromium,librewolf,waterfox,zen,arc";

        public bool AutoStart = true;
        public bool NotifyEnabled = true;
        public int NotifyMinutes = 60;
        public bool NotifyFullscreen = true;   // mostra il messaggio (con un suono) anche sopra giochi/app a schermo intero
        public bool TrackBrowser = true;
        public bool TrackSteam = true;
        public string BrowserProcesses = DefaultBrowsers;

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        public HashSet<string> BrowserSet()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (BrowserProcesses ?? "").Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
                if (p.Length > 0) set.Add(p);
            }
            return set;
        }

        public static AppSettings Load(Storage st)
        {
            AppSettings s = new AppSettings();
            s.AutoStart = GetBool(st, "autostart", s.AutoStart);
            s.NotifyEnabled = GetBool(st, "notify_enabled", s.NotifyEnabled);
            s.NotifyFullscreen = GetBool(st, "notify_fullscreen", s.NotifyFullscreen);
            s.TrackBrowser = GetBool(st, "track_browser", s.TrackBrowser);
            s.TrackSteam = GetBool(st, "track_steam", s.TrackSteam);
            int minutes;
            if (int.TryParse(st.GetSetting("notify_minutes", null), NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
                s.NotifyMinutes = Math.Max(1, Math.Min(1440, minutes));
            string br = st.GetSetting("browser_processes", null);
            if (!string.IsNullOrEmpty(br)) s.BrowserProcesses = br;
            return s;
        }

        public void Save(Storage st)
        {
            st.SetSetting("autostart", AutoStart ? "1" : "0");
            st.SetSetting("notify_enabled", NotifyEnabled ? "1" : "0");
            st.SetSetting("notify_minutes", NotifyMinutes.ToString(CultureInfo.InvariantCulture));
            st.SetSetting("notify_fullscreen", NotifyFullscreen ? "1" : "0");
            st.SetSetting("track_browser", TrackBrowser ? "1" : "0");
            st.SetSetting("track_steam", TrackSteam ? "1" : "0");
            st.SetSetting("browser_processes", BrowserProcesses ?? DefaultBrowsers);
        }

        private static bool GetBool(Storage st, string key, bool def)
        {
            string v = st.GetSetting(key, null);
            if (v == null) return def;
            return v == "1";
        }
    }

    /// <summary>
    /// Database SQLite dell'utente corrente: %LOCALAPPDATA%\WinUsage\usage.db.
    /// Essendo nel profilo di ogni utente Windows, storico e configurazione sono separati per utente.
    /// Se il database non è utilizzabile l'app continua a funzionare solo in memoria.
    /// </summary>
    internal sealed class Storage : IDisposable
    {
        private Db db;
        public readonly string DbPath;

        public bool Available { get { return db != null; } }

        public Storage(string dbPath)
        {
            DbPath = dbPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
                db = new Db(dbPath);
                db.Execute("PRAGMA journal_mode=WAL");
                db.Execute("PRAGMA synchronous=NORMAL");
                db.Execute("CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS usage_daily(day TEXT NOT NULL, timer TEXT NOT NULL, " +
                           "seconds INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(day, timer))");
                db.Execute("CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                           "started_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, active_seconds INTEGER NOT NULL DEFAULT 0)");
                db.Execute("PRAGMA user_version=1");
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                if (db != null) { db.Dispose(); db = null; }
            }
        }

        public string GetSetting(string key, string def)
        {
            if (db == null) return def;
            try
            {
                List<object[]> rows = db.Query("SELECT value FROM settings WHERE key=?", key);
                return rows.Count > 0 ? (string)rows[0][0] : def;
            }
            catch (Exception ex) { Log.Write(ex); return def; }
        }

        public void SetSetting(string key, string value)
        {
            if (db == null) return;
            try
            {
                db.Execute("UPDATE settings SET value=? WHERE key=?", value, key);
                if (db.Changes == 0) db.Execute("INSERT INTO settings(key, value) VALUES(?, ?)", key, value);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Somma <paramref name="seconds"/> al totale giornaliero del timer.</summary>
        public void AddUsage(string day, string timer, long seconds)
        {
            if (db == null || seconds <= 0) return;
            try
            {
                db.Execute("UPDATE usage_daily SET seconds = seconds + ? WHERE day=? AND timer=?", seconds, day, timer);
                if (db.Changes == 0)
                    db.Execute("INSERT INTO usage_daily(day, timer, seconds) VALUES(?, ?, ?)", day, timer, seconds);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Totali di un giorno, indicizzati con <see cref="TimerIds"/>.</summary>
        public long[] GetDay(string day)
        {
            long[] result = new long[TimerIds.Count];
            if (db == null) return result;
            try
            {
                foreach (object[] row in db.Query("SELECT timer, seconds FROM usage_daily WHERE day=?", day))
                {
                    int i = Array.IndexOf(TimerIds.Names, (string)row[0]);
                    if (i >= 0) result[i] = (long)row[1];
                }
            }
            catch (Exception ex) { Log.Write(ex); }
            return result;
        }

        /// <summary>Totali giornalieri nell'intervallo [firstDay, lastDay] (formato yyyy-MM-dd).</summary>
        public Dictionary<string, long[]> GetRange(string firstDay, string lastDay)
        {
            Dictionary<string, long[]> result = new Dictionary<string, long[]>();
            if (db == null) return result;
            try
            {
                foreach (object[] row in db.Query(
                    "SELECT day, timer, seconds FROM usage_daily WHERE day >= ? AND day <= ?", firstDay, lastDay))
                {
                    string day = (string)row[0];
                    int i = Array.IndexOf(TimerIds.Names, (string)row[1]);
                    if (i < 0) continue;
                    long[] arr;
                    if (!result.TryGetValue(day, out arr)) { arr = new long[TimerIds.Count]; result[day] = arr; }
                    arr[i] = (long)row[2];
                }
            }
            catch (Exception ex) { Log.Write(ex); }
            return result;
        }

        public long StartSession(DateTime utcNow)
        {
            if (db == null) return -1;
            try
            {
                string t = Iso(utcNow);
                db.Execute("INSERT INTO sessions(started_utc, last_seen_utc, active_seconds) VALUES(?, ?, 0)", t, t);
                List<object[]> rows = db.Query("SELECT last_insert_rowid()");
                return (long)rows[0][0];
            }
            catch (Exception ex) { Log.Write(ex); return -1; }
        }

        public void UpdateSession(long id, DateTime utcNow, long activeSeconds)
        {
            if (db == null || id < 0) return;
            try
            {
                db.Execute("UPDATE sessions SET last_seen_utc=?, active_seconds=? WHERE id=?", Iso(utcNow), activeSeconds, id);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        public List<object[]> Query(string sql, params object[] args)
        {
            return db == null ? new List<object[]>() : db.Query(sql, args);
        }

        private static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (db != null) { db.Dispose(); db = null; }
        }
    }
}
