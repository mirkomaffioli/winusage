using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinUsage
{
    /// <summary>
    /// Lingua dell'interfaccia (dalla 1.6): italiano o inglese. Ogni testo visibile si scrive accanto alla sua traduzione,
    /// <c>L.T("Italiano", "English")</c>, così le due lingue non si separano mai. La lingua si sceglie all'avvio:
    /// impostazione "language" = "auto" (segue Windows: italiano se Windows è in italiano, altrimenti inglese), "it" o "en".
    /// Cambiarla richiede il riavvio dell'app (menu e finestre sono già costruiti).
    /// </summary>
    internal static class L
    {
        public const string SettingKey = "language";
        public const string Auto = "auto", Italian = "it", English = "en";

        /// <summary>true = inglese. Predefinito italiano (anche nei test).</summary>
        public static bool En;

        public static string T(string it, string en)
        {
            return En ? en : it;
        }

        /// <summary>Lingua effettiva per una scelta salvata ("auto", "it", "en").</summary>
        public static bool ResolveEnglish(string choice, CultureInfo windows)
        {
            if (choice == Italian) return false;
            if (choice == English) return true;
            return windows == null || windows.TwoLetterISOLanguageName != "it";
        }

        public static string Choice(Storage st)
        {
            string v = st != null ? st.GetSetting(SettingKey, Auto) : Auto;
            return v == Italian || v == English ? v : Auto;
        }

        public static void Init(Storage st)
        {
            En = ResolveEnglish(Choice(st), CultureInfo.CurrentUICulture);
        }

        /// <summary>Cultura per numeri e date mostrati (virgola decimale in italiano, punto in inglese).</summary>
        public static CultureInfo Culture
        {
            get
            {
                try { return CultureInfo.GetCultureInfo(En ? "en-GB" : "it-IT"); }
                catch (Exception) { return CultureInfo.InvariantCulture; }
            }
        }

        /// <summary>Nome di un giorno della settimana nella lingua scelta.</summary>
        public static string DayName(DateTime d)
        {
            return Culture.DateTimeFormat.GetDayName(d.DayOfWeek);
        }
    }

    /// <summary>I tre timer tracciati.</summary>
    internal static class TimerIds
    {
        public const int System = 0;   // tempo di utilizzo del sistema dall'avvio
        public const int Browser = 1;  // browser in primo piano
        public const int Steam = 2;    // Steam (gioco avviato da Steam o client in primo piano)
        public const int Count = 3;

        public static readonly string[] Names = { "system", "browser", "steam" };
        public static string[] Labels { get { return new[] { L.T("Sistema", "System"), "Browser", "Steam" }; } }
    }

    /// <summary>Formattazione dei tempi nella lingua scelta.</summary>
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

        /// <summary>Per il toast pixel art (solo maiuscole): "2 ORE 30 MIN", "1 ORA", "45 MIN" (in inglese "2 HRS 30 MIN").</summary>
        public static string ToastTime(double seconds)
        {
            long totalMin = (long)Math.Round(Math.Max(0, seconds) / 60.0);
            long h = totalMin / 60, m = totalMin % 60;
            string hs = h == 1 ? L.T("1 ORA", "1 HR") : h + L.T(" ORE", " HRS");   // "2 HRS 30 MIN" sta nella riga grande
            if (h == 0) return m + " MIN";
            if (m == 0) return hs;
            return hs + " " + m + " MIN";
        }

        /// <summary>"1 ora e 30 minuti", "2 ore", "45 minuti" (arrotondato al minuto); in inglese "1 hour and 30 minutes".</summary>
        public static string Long(double seconds)
        {
            long totalMin = (long)Math.Round(Math.Max(0, seconds) / 60.0);
            long h = totalMin / 60, m = totalMin % 60;
            string hs = h == 1 ? L.T("1 ora", "1 hour") : h + L.T(" ore", " hours");
            string ms = m == 1 ? L.T("1 minuto", "1 minute") : m + L.T(" minuti", " minutes");
            if (h == 0) return ms;
            if (m == 0) return hs;
            return hs + L.T(" e ", " and ") + ms;
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
        public int RetentionDays = 0;          // pulizia automatica: elimina i giorni più vecchi di N (0 = conserva tutto)
        public string Language = L.Auto;       // "auto" (segue Windows), "it", "en": vale dal prossimo avvio

        public const int MinRetentionDays = 31;   // così lo storico a 30 giorni resta sempre completo
        public const int MaxRetentionDays = 3650;

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
            int keep;
            if (int.TryParse(st.GetSetting("retention_days", null), NumberStyles.Integer, CultureInfo.InvariantCulture, out keep))
                s.RetentionDays = keep <= 0 ? 0 : Math.Max(MinRetentionDays, Math.Min(MaxRetentionDays, keep));
            string br = st.GetSetting("browser_processes", null);
            if (!string.IsNullOrEmpty(br)) s.BrowserProcesses = br;
            s.Language = L.Choice(st);
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
            st.SetSetting("retention_days", RetentionDays.ToString(CultureInfo.InvariantCulture));
            st.SetSetting(L.SettingKey, Language == L.Italian || Language == L.English ? Language : L.Auto);
        }

        private static bool GetBool(Storage st, string key, bool def)
        {
            string v = st.GetSetting(key, null);
            if (v == null) return def;
            return v == "1";
        }
    }

    /// <summary>Informazioni sul database mostrate nella finestra di manutenzione.</summary>
    internal sealed class DbInfo
    {
        public string Path;
        public long FileBytes, WalBytes, PageSize, PageCount, FreePages, Days, Sessions;
        public string FirstDay, LastDay, SqliteVersion;

        public long FreeBytes { get { return FreePages * PageSize; } }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + L.T(" byte", " bytes");
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", L.Culture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##", L.Culture) + " MB";
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
                // 1.3: registro dei comandi del controllo remoto (la pulizia dello storico lo riguarda)
                db.Execute("CREATE TABLE IF NOT EXISTS remote_log(id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, " +
                           "sender_id INTEGER NOT NULL, command TEXT NOT NULL, result TEXT NOT NULL)");
                // 1.3: sessione chiusa in modo regolare (per riconoscere uno spegnimento improvviso).
                // Le sessioni già registrate valgono come chiuse bene, così l'aggiornamento non segnala falsi allarmi.
                bool hasClean = false;
                foreach (object[] col in db.Query("PRAGMA table_info(sessions)"))
                    if (string.Equals(col[1] as string, "ended_clean", StringComparison.OrdinalIgnoreCase)) hasClean = true;
                if (!hasClean)
                {
                    db.Execute("ALTER TABLE sessions ADD COLUMN ended_clean INTEGER NOT NULL DEFAULT 0");
                    db.Execute("UPDATE sessions SET ended_clean=1");
                }
                db.Execute("PRAGMA user_version=2");
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
                db.Execute("UPDATE sessions SET last_seen_utc=?, active_seconds=?, ended_clean=0 WHERE id=?", Iso(utcNow), activeSeconds, id);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>La sessione si è chiusa in modo regolare (uscita o spegnimento): da chiamare dopo l'ultimo salvataggio.</summary>
        public void MarkSessionClean(long id)
        {
            if (db == null || id < 0) return;
            try { db.Execute("UPDATE sessions SET ended_clean=1 WHERE id=?", id); }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>
        /// La sessione precedente a <paramref name="currentId"/>: ultimo segnale di vita e se si è chiusa bene.
        /// Falso se non esiste.
        /// </summary>
        public bool PreviousSession(long currentId, out DateTime lastSeenUtc, out bool endedClean)
        {
            lastSeenUtc = DateTime.MinValue;
            endedClean = true;
            if (db == null) return false;
            try
            {
                List<object[]> r = db.Query("SELECT last_seen_utc, ended_clean FROM sessions WHERE id < ? ORDER BY id DESC LIMIT 1", currentId);
                if (r.Count == 0) return false;
                DateTime d;
                if (DateTime.TryParseExact((string)r[0][0], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                           DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                    lastSeenUtc = d;
                endedClean = (long)r[0][1] != 0;
                return true;
            }
            catch (Exception ex) { Log.Write(ex); return false; }
        }

        public List<object[]> Query(string sql, params object[] args)
        {
            return db == null ? new List<object[]>() : db.Query(sql, args);
        }

        /// <summary>Esegue un'istruzione senza risultato (nessun effetto se il database non è disponibile).</summary>
        public void Execute(string sql, params object[] args)
        {
            if (db != null) db.Execute(sql, args);
        }

        // ---- manutenzione (questi metodi segnalano gli errori con un'eccezione: li mostra la finestra) ----

        private Db Open()
        {
            if (db == null) throw new InvalidOperationException(L.T("Database non disponibile.", "Database not available."));
            return db;
        }

        /// <summary>Dimensioni e contenuto del database, per la finestra di manutenzione.</summary>
        public DbInfo GetInfo()
        {
            Db d = Open();
            DbInfo i = new DbInfo();
            i.Path = DbPath;
            i.FileBytes = FileSize(DbPath);
            i.WalBytes = FileSize(DbPath + "-wal");
            i.PageSize = ScalarLong(d, "PRAGMA page_size");
            i.PageCount = ScalarLong(d, "PRAGMA page_count");
            i.FreePages = ScalarLong(d, "PRAGMA freelist_count");
            List<object[]> r = d.Query("SELECT COUNT(DISTINCT day), MIN(day), MAX(day) FROM usage_daily");
            i.Days = (long)r[0][0];
            i.FirstDay = r[0][1] as string;
            i.LastDay = r[0][2] as string;
            i.Sessions = ScalarLong(d, "SELECT COUNT(*) FROM sessions");
            i.SqliteVersion = d.Query("SELECT sqlite_version()")[0][0] as string;
            return i;
        }

        /// <summary>PRAGMA integrity_check: una sola riga "ok" se il database è sano.</summary>
        public List<string> IntegrityCheck()
        {
            List<string> result = new List<string>();
            foreach (object[] row in Open().Query("PRAGMA integrity_check")) result.Add(Convert.ToString(row[0]));
            return result;
        }

        /// <summary>Riscrive il database eliminando lo spazio inutilizzato. Restituisce i byte recuperati.</summary>
        public long Compact()
        {
            Db d = Open();
            long before = FileSize(DbPath) + FileSize(DbPath + "-wal");
            Checkpoint();
            d.Execute("VACUUM");
            Checkpoint();
            long after = FileSize(DbPath) + FileSize(DbPath + "-wal");
            return Math.Max(0, before - after);
        }

        /// <summary>Copia completa e coerente del database in <paramref name="target"/> (sovrascrive).</summary>
        public void Backup(string target)
        {
            Db d = Open();
            if (File.Exists(target)) File.Delete(target);
            try
            {
                d.Execute("VACUUM INTO ?", target);   // SQLite 3.27 o successivo (Windows 11 lo è)
            }
            catch (Exception ex)
            {
                // SQLite più vecchio: tutto il contenuto del WAL nel file principale, poi una copia del file.
                Log.Write("VACUUM INTO non disponibile, copia del file: " + ex.Message);
                if (File.Exists(target)) File.Delete(target);
                Checkpoint();
                File.Copy(DbPath, target, true);
            }
        }

        /// <summary>Elimina i totali giornalieri e le sessioni precedenti a <paramref name="firstDayToKeep"/> (yyyy-MM-dd).</summary>
        /// <returns>Numero di giorni eliminati.</returns>
        public int DeleteBefore(string firstDayToKeep)
        {
            Db d = Open();
            int days = CountDaysBefore(firstDayToKeep);
            d.Execute("BEGIN");
            try
            {
                d.Execute("DELETE FROM usage_daily WHERE day < ?", firstDayToKeep);
                d.Execute("DELETE FROM sessions WHERE started_utc < ?", firstDayToKeep);
                d.Execute("DELETE FROM remote_log WHERE ts_utc < ?", firstDayToKeep);
                d.Execute("COMMIT");
            }
            catch
            {
                try { d.Execute("ROLLBACK"); } catch { }
                throw;
            }
            return days;
        }

        /// <summary>Quanti giorni dello storico sono precedenti a <paramref name="dayKey"/>.</summary>
        public int CountDaysBefore(string dayKey)
        {
            return (int)(long)Open().Query("SELECT COUNT(DISTINCT day) FROM usage_daily WHERE day < ?", dayKey)[0][0];
        }

        /// <summary>Primo giorno conservato quando si eliminano i dati più vecchi di <paramref name="days"/> giorni.</summary>
        public static string FirstDayToKeep(DateTime todayLocal, int days)
        {
            return todayLocal.Date.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>Svuota lo storico (totali e sessioni), tranne la sessione in corso. La configurazione resta.</summary>
        public void DeleteAllUsage(long keepSessionId)
        {
            Db d = Open();
            d.Execute("BEGIN");
            try
            {
                d.Execute("DELETE FROM usage_daily");
                d.Execute("DELETE FROM sessions WHERE id <> ?", keepSessionId);
                d.Execute("COMMIT");
            }
            catch
            {
                try { d.Execute("ROLLBACK"); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Esporta i totali giornalieri in CSV (UTF-8 con BOM; separatore ";" come si aspetta Excel in italiano, "," in inglese).
        /// </summary>
        /// <returns>Numero di giorni esportati.</returns>
        public int ExportCsv(string target)
        {
            SortedDictionary<string, long[]> days = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
            foreach (object[] row in Open().Query("SELECT day, timer, seconds FROM usage_daily"))
            {
                int i = Array.IndexOf(TimerIds.Names, (string)row[1]);
                if (i < 0) continue;
                long[] arr;
                if (!days.TryGetValue((string)row[0], out arr)) { arr = new long[TimerIds.Count]; days[(string)row[0]] = arr; }
                arr[i] = (long)row[2];
            }

            StringBuilder sb = new StringBuilder();
            char sep = L.En ? ',' : ';';
            string[] labels = TimerIds.Labels;
            sb.Append(L.T("Giorno", "Day"));
            for (int t = 0; t < TimerIds.Count; t++) sb.Append(sep).Append(labels[t]).Append(" (hh:mm:ss)");
            for (int t = 0; t < TimerIds.Count; t++) sb.Append(sep).Append(labels[t]).Append(L.T(" (secondi)", " (seconds)"));
            sb.Append("\r\n");
            foreach (KeyValuePair<string, long[]> kv in days)
            {
                sb.Append(kv.Key);
                for (int t = 0; t < TimerIds.Count; t++) sb.Append(sep).Append(Fmt.Clock(kv.Value[t]));
                for (int t = 0; t < TimerIds.Count; t++) sb.Append(sep).Append(kv.Value[t].ToString(CultureInfo.InvariantCulture));
                sb.Append("\r\n");
            }
            File.WriteAllText(target, sb.ToString(), new UTF8Encoding(true));
            return days.Count;
        }

        private void Checkpoint()
        {
            Open().Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        private static long ScalarLong(Db d, string sql)
        {
            List<object[]> r = d.Query(sql);
            return r.Count > 0 && r[0][0] is long ? (long)r[0][0] : 0;
        }

        private static long FileSize(string path)
        {
            try { FileInfo fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
            catch { return 0; }
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
