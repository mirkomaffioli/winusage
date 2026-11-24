using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Controllo remoto via Telegram: la parte che non dipende da WinForms né dalla rete (collaudabile con Mono).
// Design completo: claude/WinUsage-remote-design.md nel progetto.
namespace WinUsage
{
    /// <summary>Base32 (RFC 4648, senza padding) per il segreto TOTP: è il formato delle app Authenticator.</summary>
    internal static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        /// <summary>Decodifica ignorando spazi, trattini, padding e maiuscole/minuscole. Null se non valido.</summary>
        public static byte[] Decode(string text)
        {
            if (text == null) return null;
            List<byte> result = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char ch in text.ToUpperInvariant())
            {
                if (ch == ' ' || ch == '-' || ch == '=') continue;
                int v = Alphabet.IndexOf(ch);
                if (v < 0) return null;
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return result.ToArray();
        }
    }

    /// <summary>Codici a tempo (RFC 6238, HMAC-SHA1, 30 secondi, 6 cifre) compatibili con le app Authenticator.</summary>
    internal static class Totp
    {
        public const int StepSeconds = 30;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long StepOf(DateTime utc)
        {
            return (long)Math.Floor((utc - Epoch).TotalSeconds / StepSeconds);
        }

        public static string Code(byte[] secret, long step, int digits)
        {
            byte[] counter = new byte[8];
            for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
            byte[] hash;
            using (HMACSHA1 h = new HMACSHA1(secret)) hash = h.ComputeHash(counter);
            int offset = hash[hash.Length - 1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            int mod = 1;
            for (int i = 0; i < digits; i++) mod *= 10;
            return (binary % mod).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }

        public static string Code(byte[] secret, long step)
        {
            return Code(secret, step, 6);
        }

        /// <summary>
        /// Verifica un codice nella finestra di ±1 passo attorno a <paramref name="utc"/>. Un codice vale una volta sola:
        /// sono accettati solo passi successivi a <paramref name="lastStep"/>. Restituisce il passo accettato o -1.
        /// </summary>
        public static long Verify(byte[] secret, string code, DateTime utc, long lastStep)
        {
            if (secret == null || secret.Length == 0 || code == null || code.Length != 6) return -1;
            long now = StepOf(utc);
            long[] candidates = { now, now - 1, now + 1 };
            foreach (long step in candidates)
            {
                if (step <= lastStep) continue;
                if (SameText(Code(secret, step), code)) return step;
            }
            return -1;
        }

        /// <summary>Confronto a tempo costante: non rivela quante cifre sono giuste.</summary>
        private static bool SameText(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] NewSecret()
        {
            byte[] s = new byte[20];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(s);
            return s;
        }

        /// <summary>Indirizzo otpauth:// da mostrare come QR code. Breve, per stare in un QR versione 6.</summary>
        public static string ProvisioningUri(byte[] secret, string account)
        {
            StringBuilder acc = new StringBuilder();
            foreach (char c in account ?? "")
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') acc.Append(c);
                if (acc.Length >= 10) break;
            }
            if (acc.Length == 0) acc.Append("PC");
            return "otpauth://totp/WinUsage:" + acc + "?secret=" + Base32.Encode(secret) + "&issuer=WinUsage";
        }
    }

    /// <summary>
    /// Valori di sicurezza del controllo remoto, salvati in un unico blob cifrato con DPAPI (utente corrente).
    /// Chi modifica il database non può cambiare l'utente abbinato: un blob alterato non si decifra.
    /// </summary>
    internal sealed class RemoteSecrets
    {
        public string Token = "";
        public long UserId;                  // 0 = nessun telefono abbinato
        public string UserName = "";
        public string TotpSecret = "";       // base32; vuoto = non configurato
        public bool TotpVerified;
        public bool CodeScreenshot;          // protezione dei comandi (decisioni di design: screenshot e app senza codice)
        public bool CodeApp;
        public bool CodeLock = true;
        public bool CodeShutdown = true;
        public string ScreenshotExclusions = "keepass,keepassxc,1password,bitwarden,enpass";

        private const string SettingKey = "remote_secure";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinUsage.Remote.v1");

        /// <summary>Cifratura sostituibile nei test. Predefinita: DPAPI con ambito utente corrente.</summary>
        public static Func<byte[], byte[]> Protect = delegate(byte[] plain)
        {
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        };

        public static Func<byte[], byte[]> Unprotect = delegate(byte[] cipher)
        {
            return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        };

        public bool Paired { get { return UserId != 0; } }
        public bool HasToken { get { return !string.IsNullOrEmpty(Token); } }
        public bool HasTotp { get { return TotpVerified && SecretBytes() != null; } }

        public byte[] SecretBytes()
        {
            byte[] b = Base32.Decode(TotpSecret);
            return b != null && b.Length >= 10 ? b : null;
        }

        public RemoteSecrets Clone()
        {
            return (RemoteSecrets)MemberwiseClone();
        }

        public bool RequiresCode(string command)
        {
            switch (command)
            {
                case "screenshot": return CodeScreenshot;
                case "app": return CodeApp;
                case "blocca": return CodeLock;
                case "spegni": return CodeShutdown;
                default: return false;
            }
        }

        public HashSet<string> ExclusionSet()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (ScreenshotExclusions ?? "").Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
                if (p.Length > 0) set.Add(p);
            }
            return set;
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            Line(sb, "token", Token);
            Line(sb, "user_id", UserId.ToString(CultureInfo.InvariantCulture));
            Line(sb, "user_name", UserName);
            Line(sb, "totp", TotpSecret);
            Line(sb, "totp_ok", TotpVerified ? "1" : "0");
            Line(sb, "code_screenshot", CodeScreenshot ? "1" : "0");
            Line(sb, "code_app", CodeApp ? "1" : "0");
            Line(sb, "code_lock", CodeLock ? "1" : "0");
            Line(sb, "code_shutdown", CodeShutdown ? "1" : "0");
            Line(sb, "exclusions", ScreenshotExclusions);
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""))).Append('\n');
        }

        public static RemoteSecrets Parse(string text)
        {
            RemoteSecrets s = new RemoteSecrets();
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string v = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(eq + 1)));
                switch (key)
                {
                    case "token": s.Token = v; break;
                    case "user_id": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.UserId); break;
                    case "user_name": s.UserName = v; break;
                    case "totp": s.TotpSecret = v; break;
                    case "totp_ok": s.TotpVerified = v == "1"; break;
                    case "code_screenshot": s.CodeScreenshot = v == "1"; break;
                    case "code_app": s.CodeApp = v == "1"; break;
                    case "code_lock": s.CodeLock = v == "1"; break;
                    case "code_shutdown": s.CodeShutdown = v == "1"; break;
                    case "exclusions": s.ScreenshotExclusions = v; break;
                }
            }
            return s;
        }

        /// <summary>
        /// Legge il blob. Se manca restituisce i valori predefiniti; se non si decifra (alterato o copiato da un altro
        /// utente/PC) <paramref name="damaged"/> è vero e il controllo remoto va considerato disattivato.
        /// </summary>
        public static RemoteSecrets Load(Storage st, out bool damaged)
        {
            damaged = false;
            string blob = st.GetSetting(SettingKey, null);
            if (string.IsNullOrEmpty(blob)) return new RemoteSecrets();
            try
            {
                byte[] plain = Unprotect(Convert.FromBase64String(blob));
                return Parse(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex)
            {
                Log.Write("Configurazione remota non leggibile: " + ex.GetType().Name);
                damaged = true;
                return new RemoteSecrets();
            }
        }

        public void Save(Storage st)
        {
            byte[] cipher = Protect(Encoding.UTF8.GetBytes(Serialize()));
            st.SetSetting(SettingKey, Convert.ToBase64String(cipher));
        }
    }

    /// <summary>Impostazioni non segrete del controllo remoto (tabella settings, prefisso remote_).</summary>
    internal sealed class RemoteSettings
    {
        public bool Enabled;
        public int MaxAgeSeconds = 120;            // i comandi più vecchi vengono scartati
        public int ScreenshotDeleteMinutes = 10;   // 0 = non cancellare dalla chat
        public int ShutdownDefaultMinutes = 5;

        // messaggi al telefono
        public bool MsgPowerOn = true;
        public bool MsgPowerOff = true;
        public bool MsgCrash = true;
        public bool MsgSleep;
        public bool MsgLock;

        // notifiche sul PC (pixel art)
        public bool NotifyTray = true;
        public bool NotifyScreenshot = true;
        public bool NotifyApp = true;
        public bool NotifyChat = true;
        public bool NotifyLocked = true;
        public bool NotifyCountdown = true;
        public bool Sounds = true;

        public const int MinShutdownMinutes = 1;
        public const int MaxShutdownMinutes = 120;

        public RemoteSettings Clone()
        {
            return (RemoteSettings)MemberwiseClone();
        }

        public static RemoteSettings Load(Storage st)
        {
            RemoteSettings s = new RemoteSettings();
            s.Enabled = B(st, "remote_enabled", s.Enabled);
            s.MaxAgeSeconds = I(st, "remote_max_age_s", s.MaxAgeSeconds, 30, 3600);
            s.ScreenshotDeleteMinutes = I(st, "remote_screenshot_delete_min", s.ScreenshotDeleteMinutes, 0, 2880);
            s.ShutdownDefaultMinutes = I(st, "remote_shutdown_default_min", s.ShutdownDefaultMinutes, MinShutdownMinutes, MaxShutdownMinutes);
            s.MsgPowerOn = B(st, "remote_msg_power_on", s.MsgPowerOn);
            s.MsgPowerOff = B(st, "remote_msg_power_off", s.MsgPowerOff);
            s.MsgCrash = B(st, "remote_msg_crash", s.MsgCrash);
            s.MsgSleep = B(st, "remote_msg_sleep", s.MsgSleep);
            s.MsgLock = B(st, "remote_msg_lock", s.MsgLock);
            s.NotifyTray = B(st, "remote_notify_tray", s.NotifyTray);
            s.NotifyScreenshot = B(st, "remote_notify_screenshot", s.NotifyScreenshot);
            s.NotifyApp = B(st, "remote_notify_app", s.NotifyApp);
            s.NotifyChat = B(st, "remote_notify_chat", s.NotifyChat);
            s.NotifyLocked = B(st, "remote_notify_locked", s.NotifyLocked);
            s.NotifyCountdown = B(st, "remote_notify_countdown", s.NotifyCountdown);
            s.Sounds = B(st, "remote_sounds", s.Sounds);
            return s;
        }

        public void Save(Storage st)
        {
            st.SetSetting("remote_enabled", Enabled ? "1" : "0");
            st.SetSetting("remote_max_age_s", MaxAgeSeconds.ToString(CultureInfo.InvariantCulture));
            st.SetSetting("remote_screenshot_delete_min", ScreenshotDeleteMinutes.ToString(CultureInfo.InvariantCulture));
            st.SetSetting("remote_shutdown_default_min", ShutdownDefaultMinutes.ToString(CultureInfo.InvariantCulture));
            st.SetSetting("remote_msg_power_on", MsgPowerOn ? "1" : "0");
            st.SetSetting("remote_msg_power_off", MsgPowerOff ? "1" : "0");
            st.SetSetting("remote_msg_crash", MsgCrash ? "1" : "0");
            st.SetSetting("remote_msg_sleep", MsgSleep ? "1" : "0");
            st.SetSetting("remote_msg_lock", MsgLock ? "1" : "0");
            st.SetSetting("remote_notify_tray", NotifyTray ? "1" : "0");
            st.SetSetting("remote_notify_screenshot", NotifyScreenshot ? "1" : "0");
            st.SetSetting("remote_notify_app", NotifyApp ? "1" : "0");
            st.SetSetting("remote_notify_chat", NotifyChat ? "1" : "0");
            st.SetSetting("remote_notify_locked", NotifyLocked ? "1" : "0");
            st.SetSetting("remote_notify_countdown", NotifyCountdown ? "1" : "0");
            st.SetSetting("remote_sounds", Sounds ? "1" : "0");
        }

        private static bool B(Storage st, string key, bool def)
        {
            string v = st.GetSetting(key, null);
            return v == null ? def : v == "1";
        }

        private static int I(Storage st, string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(st.GetSetting(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }
    }

    internal sealed class OutboxItem
    {
        public long Id;
        public DateTime CreatedUtc;
        public string Kind;
        public string Text;
    }

    internal sealed class RemoteLogEntry
    {
        public DateTime TimeUtc;
        public long SenderId;
        public string Command;
        public string Result;
    }

    /// <summary>Tabelle del controllo remoto: coda in uscita, registro dei comandi, screenshot da cancellare.</summary>
    internal sealed class RemoteStore
    {
        private readonly Storage st;

        public RemoteStore(Storage storage)
        {
            st = storage;
            try
            {
                st.Execute("CREATE TABLE IF NOT EXISTS remote_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, " +
                           "kind TEXT NOT NULL, text TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0)");
                st.Execute("CREATE TABLE IF NOT EXISTS remote_log(id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, " +
                           "sender_id INTEGER NOT NULL, command TEXT NOT NULL, result TEXT NOT NULL)");
                st.Execute("CREATE TABLE IF NOT EXISTS remote_delete(message_id INTEGER PRIMARY KEY, due_utc TEXT NOT NULL)");
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        public long AddOutbox(string kind, string text, DateTime utc)
        {
            try
            {
                st.Execute("INSERT INTO remote_outbox(created_utc, kind, text) VALUES(?, ?, ?)", Iso(utc), kind, text);
                List<object[]> r = st.Query("SELECT last_insert_rowid()");
                return r.Count > 0 ? (long)r[0][0] : -1;
            }
            catch (Exception ex) { Log.Write(ex); return -1; }
        }

        public void RemoveOutbox(long id)
        {
            try { st.Execute("DELETE FROM remote_outbox WHERE id=?", id); } catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Messaggi in attesa, dal più vecchio. Quelli più vecchi di 7 giorni vengono scartati.</summary>
        public List<OutboxItem> LoadOutbox(DateTime nowUtc)
        {
            List<OutboxItem> list = new List<OutboxItem>();
            try
            {
                st.Execute("DELETE FROM remote_outbox WHERE created_utc < ?", Iso(nowUtc.AddDays(-7)));
                foreach (object[] row in st.Query("SELECT id, created_utc, kind, text FROM remote_outbox ORDER BY id"))
                {
                    OutboxItem it = new OutboxItem();
                    it.Id = (long)row[0];
                    it.CreatedUtc = ParseIso((string)row[1]);
                    it.Kind = (string)row[2];
                    it.Text = (string)row[3];
                    list.Add(it);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
            return list;
        }

        public void AddLog(DateTime utc, long sender, string command, string result)
        {
            try
            {
                st.Execute("INSERT INTO remote_log(ts_utc, sender_id, command, result) VALUES(?, ?, ?, ?)",
                           Iso(utc), sender, command ?? "", result ?? "");
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        public List<RemoteLogEntry> RecentLog(int max)
        {
            List<RemoteLogEntry> list = new List<RemoteLogEntry>();
            try
            {
                foreach (object[] row in st.Query("SELECT ts_utc, sender_id, command, result FROM remote_log ORDER BY id DESC LIMIT ?", max))
                {
                    RemoteLogEntry e = new RemoteLogEntry();
                    e.TimeUtc = ParseIso((string)row[0]);
                    e.SenderId = (long)row[1];
                    e.Command = (string)row[2];
                    e.Result = (string)row[3];
                    list.Add(e);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
            return list;
        }

        public void AddPendingDelete(long messageId, DateTime dueUtc)
        {
            try { st.Execute("INSERT OR REPLACE INTO remote_delete(message_id, due_utc) VALUES(?, ?)", messageId, Iso(dueUtc)); }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Screenshot da cancellare ora. Oltre le 47 ore Telegram non lo permette più: si lasciano perdere.</summary>
        public List<long> DueDeletes(DateTime nowUtc)
        {
            List<long> ids = new List<long>();
            try
            {
                st.Execute("DELETE FROM remote_delete WHERE due_utc < ?", Iso(nowUtc.AddHours(-47)));
                foreach (object[] row in st.Query("SELECT message_id FROM remote_delete WHERE due_utc <= ?", Iso(nowUtc)))
                    ids.Add((long)row[0]);
            }
            catch (Exception ex) { Log.Write(ex); }
            return ids;
        }

        public void RemovePendingDelete(long messageId)
        {
            try { st.Execute("DELETE FROM remote_delete WHERE message_id=?", messageId); } catch (Exception ex) { Log.Write(ex); }
        }

        public long Offset
        {
            get { return GetLong("remote_offset"); }
            set { st.SetSetting("remote_offset", value.ToString(CultureInfo.InvariantCulture)); }
        }

        public long TotpLastStep
        {
            get { return GetLong("remote_totp_last_step"); }
            set { st.SetSetting("remote_totp_last_step", value.ToString(CultureInfo.InvariantCulture)); }
        }

        private long GetLong(string key)
        {
            long v;
            return long.TryParse(st.GetSetting(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        public static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseIso(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d;
            return DateTime.MinValue;
        }
    }

    internal enum CountdownEvent { None, Expired }

    /// <summary>
    /// Conto alla rovescia dello spegnimento. Lo standby vero (evento di sospensione di Windows) lo annulla, così il PC
    /// non si spegne di colpo al risveglio. Un'interruzione del timer senza standby (PC bloccato con lo schermo spento,
    /// app rallentata) invece non lo annulla più: se nel frattempo è scaduto, Tick lo segnala e chi lo usa guarda il ritardo.
    /// </summary>
    internal sealed class ShutdownCountdown
    {
        public bool Active { get; private set; }
        public DateTime DueUtc { get; private set; }
        public int TotalSeconds { get; private set; }

        public void Start(int minutes, DateTime nowUtc)
        {
            minutes = Math.Max(RemoteSettings.MinShutdownMinutes, Math.Min(RemoteSettings.MaxShutdownMinutes, minutes));
            TotalSeconds = minutes * 60;
            DueUtc = nowUtc.AddSeconds(TotalSeconds);
            Active = true;
        }

        public bool Cancel()
        {
            bool was = Active;
            Active = false;
            return was;
        }

        public double Remaining(DateTime nowUtc)
        {
            return Active ? Math.Max(0, (DueUtc - nowUtc).TotalSeconds) : 0;
        }

        /// <summary>Secondi passati dalla scadenza (0 se non ancora scaduto).</summary>
        public double LateSeconds(DateTime nowUtc)
        {
            return Math.Max(0, (nowUtc - DueUtc).TotalSeconds);
        }

        public CountdownEvent Tick(DateTime nowUtc)
        {
            if (!Active) return CountdownEvent.None;
            if (nowUtc >= DueUtc)
            {
                Active = false;
                return CountdownEvent.Expired;
            }
            return CountdownEvent.None;
        }

        /// <summary>Windows sta andando in standby: il conto alla rovescia si annulla. True se era attivo.</summary>
        public bool CancelForSleep()
        {
            return Cancel();
        }
    }

    /// <summary>Un messaggio arrivato da Telegram, con solo i campi che servono ai controlli.</summary>
    internal sealed class IncomingMessage
    {
        public long UpdateId;
        public long MessageId;
        public long ChatId;
        public string ChatType = "";
        public long FromId;
        public bool FromIsBot;
        public string FromName = "";
        public string Text;            // null se non è un messaggio di testo (foto, file, adesivo...)
        public DateTime DateUtc;
        public bool Forwarded;
        public bool ViaBot;
    }

    internal sealed class ScreenshotResult
    {
        public byte[] Data;
        public bool Png;
        public string Error;           // non null: cattura non possibile, con il motivo per il telefono
    }

    internal enum SessionState { Active, Locked, Disconnected }

    internal sealed class ForegroundInfo
    {
        public SessionState State;
        public DateTime LockedSinceUtc;
        public string AppName = "";
        public string ProcessName = "";
        public DateTime SinceUtc;
        public double IdleSeconds;
        public bool SteamGame;
        public bool Fullscreen;
    }

    internal enum LockResult { Locked, AlreadyLocked, Failed }

    /// <summary>Quello che il controller chiede al resto dell'app. Implementato da RemoteService (e da un finto host nei test).</summary>
    internal interface IRemoteHost
    {
        void SendText(long chatId, string text);
        void SendScreenshot(long chatId, ScreenshotResult shot);
        ScreenshotResult CaptureScreenshot(int monitor, bool hd);
        ForegroundInfo GetForegroundInfo();
        LockResult Lock();
        DateTime StartShutdown(int minutes);           // ora locale prevista
        bool CancelShutdown(bool fromRemote);
        bool ShutdownActive { get; }
        bool PcLocked { get; }                         // schermo bloccato (nessuno può salvare i documenti)
        void OpenChat(bool requestedRemotely);
        void DeliverChat(string text, DateTime sentUtc);
        void DisableRemote();
        void ConfirmPairing(long userId, string name, Action<bool> done);
        void NotifyLocal(string kind);                 // "screenshot", "app"
        void SecurityAlert(string text);
        void Audit(long senderId, string command, string result);
        void SaveSecrets();
        void SaveTotpStep(long step);
        string StatusText();
    }

    /// <summary>
    /// Il cuore del controllo remoto: decide chi può fare cosa. Non fa rete né UI: riceve i messaggi già letti e
    /// chiede le azioni a <see cref="IRemoteHost"/>. Tutte le chiamate arrivano dal thread dell'interfaccia.
    /// </summary>
    internal sealed class RemoteController
    {
        public const int PairingCodeLength = 8;
        public const int PairingMinutes = 5;
        public const int PairingMaxAttempts = 5;
        public const int MaxCodeFailures = 3;
        public const int LockoutMinutes = 15;
        public const int SessionMinutes = 15;
        public const int ScreenshotIntervalSeconds = 10;
        public const int MaxCommandsPerMinute = 20;
        public const int MaxChatLength = 2000;
        private const string PairingAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // niente 0/O, 1/I/L

        private readonly IRemoteHost host;
        private RemoteSecrets secrets;
        private RemoteSettings settings;

        private string pairingCode;
        private DateTime pairingUntilUtc;
        private int pairingAttempts;
        private bool pairingConfirmPending;

        private DateTime sessionUntilUtc = DateTime.MinValue;
        private int codeFailures;
        private DateTime lockoutUntilUtc = DateTime.MinValue;
        private long totpLastStep;
        private DateTime lastScreenshotUtc = DateTime.MinValue;
        private DateTime lastAppUtc = DateTime.MinValue;
        private readonly Queue<DateTime> recentCommands = new Queue<DateTime>();
        private DateTime floodNoticeUtc = DateTime.MinValue;

        public RemoteController(IRemoteHost host, RemoteSecrets secrets, RemoteSettings settings, long totpLastStep)
        {
            this.host = host;
            this.secrets = secrets;
            this.settings = settings;
            this.totpLastStep = totpLastStep;
        }

        public void Apply(RemoteSecrets newSecrets, RemoteSettings newSettings)
        {
            secrets = newSecrets;
            settings = newSettings;
        }

        public RemoteSecrets Secrets { get { return secrets; } }

        // ---- abbinamento ----

        /// <summary>Apre la finestra di abbinamento e restituisce il codice da mostrare sul PC.</summary>
        public string StartPairing(DateTime nowUtc)
        {
            byte[] rnd = new byte[PairingCodeLength];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in rnd) sb.Append(PairingAlphabet[b % PairingAlphabet.Length]);
            pairingCode = sb.ToString();
            pairingUntilUtc = nowUtc.AddMinutes(PairingMinutes);
            pairingAttempts = 0;
            pairingConfirmPending = false;
            return pairingCode;
        }

        public void StopPairing()
        {
            pairingCode = null;
        }

        public bool PairingOpen(DateTime nowUtc)
        {
            return pairingCode != null && nowUtc < pairingUntilUtc;
        }

        public DateTime PairingUntilUtc { get { return pairingUntilUtc; } }

        // ---- filtro dei messaggi ----

        /// <summary>
        /// Controlli comuni a ogni messaggio. Restituisce null se il messaggio può essere considerato, altrimenti il motivo
        /// (solo per il registro: a chi non è autorizzato non si risponde mai).
        /// </summary>
        public static string Reject(IncomingMessage m)
        {
            if (m == null) return "vuoto";
            if (m.ChatType != "private") return "chat non privata";
            if (m.FromIsBot) return "mittente bot";
            if (m.ChatId != m.FromId) return "chat diversa dal mittente";
            if (m.Forwarded) return "messaggio inoltrato";
            if (m.ViaBot) return "messaggio via bot";
            if (m.Text == null) return "non testuale";
            return null;
        }

        /// <param name="serverNowUtc">Ora del server Telegram (header Date), non dell'orologio del PC.</param>
        public void Process(IncomingMessage m, DateTime serverNowUtc)
        {
            string reject = Reject(m);
            if (reject != null)
            {
                if (m != null && secrets.Paired && m.FromId == secrets.UserId)
                    host.Audit(m.FromId, "(messaggio ignorato)", reject);
                return;
            }

            string text = m.Text.Trim();
            string cmd;
            string[] args;
            ParseCommand(text, out cmd, out args);

            if (!secrets.Paired)
            {
                if (cmd == "abbina" && PairingOpen(serverNowUtc) && !pairingConfirmPending) HandlePairing(m, args, serverNowUtc);
                return;   // nessuna risposta: il bot non si fa riconoscere
            }

            if (m.FromId != secrets.UserId)
            {
                host.Audit(m.FromId, cmd != null ? "/" + cmd : "(testo)", "mittente non autorizzato, ignorato");
                return;
            }

            if (cmd == null)
            {
                string t = text.Length > MaxChatLength ? text.Substring(0, MaxChatLength) : text;
                host.DeliverChat(t, m.DateUtc);
                return;
            }

            if (Flooding(serverNowUtc))
            {
                if ((serverNowUtc - floodNoticeUtc).TotalSeconds > 60)
                {
                    floodNoticeUtc = serverNowUtc;
                    host.SendText(m.ChatId, "Troppi comandi in poco tempo: riprova tra un minuto.");
                }
                host.Audit(m.FromId, "/" + cmd, "troppi comandi, ignorato");
                return;
            }

            // Solo i comandi che agiscono sul PC con il codice scadono: un /blocca o /spegni arrivato molto dopo non deve
            // partire a sorpresa. Quelli che leggono soltanto (/stato, /app, /screenshot, /chat...) si eseguono anche in
            // ritardo (dopo uno standby o una rete caduta), invece di costringere a ripeterli.
            double age = (serverNowUtc - m.DateUtc).TotalSeconds;
            if (age > settings.MaxAgeSeconds && ExpiresWhenLate(cmd))
            {
                host.SendText(m.ChatId, "⌛ Comando scaduto (inviato alle " + LocalTime(m.DateUtc) + "): rimandalo se serve ancora.");
                host.Audit(m.FromId, "/" + cmd, "scaduto");
                return;
            }

            Execute(m, cmd, args, serverNowUtc);
        }

        public const string LockedShutdownNote =
            "🔒 Il PC è bloccato: se lo è ancora alla scadenza, le app aperte verranno chiuse senza salvare.";

        /// <summary>Comandi che non si eseguono se arrivano oltre il tempo massimo (quelli protetti dal codice).</summary>
        public static bool ExpiresWhenLate(string cmd)
        {
            return cmd == "blocca" || cmd == "spegni" || cmd == "sblocca";
        }

        private void HandlePairing(IncomingMessage m, string[] args, DateTime nowUtc)
        {
            pairingAttempts++;
            string given = args.Length > 0 ? args[0].ToUpperInvariant() : "";
            if (given == pairingCode)
            {
                pairingConfirmPending = true;
                long userId = m.FromId;
                long chatId = m.ChatId;
                string name = string.IsNullOrEmpty(m.FromName) ? "?" : m.FromName;
                host.SendText(chatId, "Codice corretto. Conferma l'abbinamento sul PC.");
                host.ConfirmPairing(userId, name, delegate(bool ok)
                {
                    pairingConfirmPending = false;
                    pairingCode = null;
                    if (ok)
                    {
                        secrets.UserId = userId;
                        secrets.UserName = name;
                        host.SaveSecrets();
                        host.Audit(userId, "/abbina", "abbinato");
                        host.SendText(chatId, "✅ Telefono abbinato a " + Environment.MachineName + ".\n\n" + HelpText());
                    }
                    else
                    {
                        host.Audit(userId, "/abbina", "rifiutato dal PC");
                        host.SendText(chatId, "Abbinamento rifiutato dal PC.");
                    }
                });
                return;
            }

            host.Audit(m.FromId, "/abbina", "codice errato (" + pairingAttempts + "/" + PairingMaxAttempts + ")");
            if (pairingAttempts >= PairingMaxAttempts)
            {
                pairingCode = null;
                host.SendText(m.ChatId, "Codice errato. Troppi tentativi: abbinamento chiuso.");
                host.SecurityAlert("Troppi tentativi di abbinamento con un codice errato (account id " + m.FromId +
                                   "). L'abbinamento è stato chiuso.");
            }
            else
            {
                host.SendText(m.ChatId, "Codice errato.");
            }
        }

        private bool Flooding(DateTime nowUtc)
        {
            while (recentCommands.Count > 0 && (nowUtc - recentCommands.Peek()).TotalSeconds > 60) recentCommands.Dequeue();
            if (recentCommands.Count >= MaxCommandsPerMinute) return true;
            recentCommands.Enqueue(nowUtc);
            return false;
        }

        /// <summary>"/spegni@MioBot 123456 10" → cmd "spegni", args {"123456", "10"}. Testo semplice → cmd null.</summary>
        public static void ParseCommand(string text, out string cmd, out string[] args)
        {
            cmd = null;
            args = new string[0];
            if (string.IsNullOrEmpty(text) || text[0] != '/') return;
            string[] parts = text.Split(new char[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            string head = parts[0].Substring(1);
            int at = head.IndexOf('@');
            if (at >= 0) head = head.Substring(0, at);
            cmd = head.ToLowerInvariant();
            args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
        }

        private static bool IsCode(string s)
        {
            if (s == null || s.Length != 6) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>
        /// Controllo del codice per i comandi protetti. Accetta il codice negli argomenti (che viene tolto) oppure una
        /// sessione aperta con /sblocca. Se non va bene risponde al telefono e restituisce false.
        /// </summary>
        private bool CheckCode(IncomingMessage m, string cmd, List<string> args, DateTime nowUtc)
        {
            string code = null;
            for (int i = 0; i < args.Count; i++)
            {
                if (IsCode(args[i])) { code = args[i]; args.RemoveAt(i); break; }
            }

            if (!secrets.RequiresCode(cmd)) return true;

            if (!secrets.HasTotp)
            {
                host.SendText(m.ChatId, "Questo comando richiede il codice dell'Authenticator, che non è ancora configurato sul PC " +
                                        "(Controllo remoto > Codice).");
                host.Audit(m.FromId, "/" + cmd, "codice non configurato");
                return false;
            }

            if (nowUtc < lockoutUntilUtc)
            {
                host.SendText(m.ChatId, "⛔ Troppi codici errati: i comandi con codice sono bloccati fino alle " + LocalTime(lockoutUntilUtc) + ".");
                host.Audit(m.FromId, "/" + cmd, "bloccato per codici errati");
                return false;
            }

            if (code == null)
            {
                if (nowUtc < sessionUntilUtc) return true;
                host.SendText(m.ChatId, "Serve il codice dell'Authenticator: /" + cmd + " 123456\n" +
                                        "Oppure /sblocca 123456 per non chiederlo per " + SessionMinutes + " minuti.");
                host.Audit(m.FromId, "/" + cmd, "codice mancante");
                return false;
            }

            return VerifyCode(m, "/" + cmd, code, nowUtc);
        }

        private bool VerifyCode(IncomingMessage m, string what, string code, DateTime nowUtc)
        {
            long step = Totp.Verify(secrets.SecretBytes(), code, nowUtc, totpLastStep);
            if (step >= 0)
            {
                totpLastStep = step;
                host.SaveTotpStep(step);
                codeFailures = 0;
                return true;
            }

            codeFailures++;
            if (codeFailures >= MaxCodeFailures)
            {
                codeFailures = 0;
                lockoutUntilUtc = nowUtc.AddMinutes(LockoutMinutes);
                sessionUntilUtc = DateTime.MinValue;
                host.SendText(m.ChatId, "⛔ Codice errato per " + MaxCodeFailures + " volte: comandi con codice bloccati per " +
                                        LockoutMinutes + " minuti.");
                host.SecurityAlert("Codice dell'Authenticator sbagliato " + MaxCodeFailures + " volte dal telefono abbinato. " +
                                   "I comandi con codice sono bloccati per " + LockoutMinutes + " minuti.");
                host.Audit(m.FromId, what, "codice errato, blocco di " + LockoutMinutes + " minuti");
            }
            else
            {
                host.SendText(m.ChatId, "Codice errato o già usato. Aspetta il prossimo codice e riprova.");
                host.Audit(m.FromId, what, "codice errato");
            }
            return false;
        }

        private void Execute(IncomingMessage m, string cmd, string[] rawArgs, DateTime nowUtc)
        {
            List<string> args = new List<string>(rawArgs);
            long chat = m.ChatId;
            switch (cmd)
            {
                case "start":
                case "aiuto":
                case "help":
                    host.SendText(chat, HelpText());
                    host.Audit(m.FromId, "/" + cmd, "ok");
                    break;

                case "stato":
                    host.SendText(chat, host.StatusText());
                    host.Audit(m.FromId, "/stato", "ok");
                    break;

                case "sblocca":
                {
                    string code = args.Count > 0 ? args[0] : null;
                    if (!secrets.HasTotp)
                    {
                        host.SendText(chat, "Il codice dell'Authenticator non è configurato sul PC.");
                        host.Audit(m.FromId, "/sblocca", "codice non configurato");
                        break;
                    }
                    if (nowUtc < lockoutUntilUtc)
                    {
                        host.SendText(chat, "⛔ Comandi con codice bloccati fino alle " + LocalTime(lockoutUntilUtc) + ".");
                        host.Audit(m.FromId, "/sblocca", "bloccato per codici errati");
                        break;
                    }
                    if (!IsCode(code))
                    {
                        host.SendText(chat, "Uso: /sblocca 123456");
                        break;
                    }
                    if (VerifyCode(m, "/sblocca", code, nowUtc))
                    {
                        sessionUntilUtc = nowUtc.AddMinutes(SessionMinutes);
                        host.SendText(chat, "🔓 Per " + SessionMinutes + " minuti (fino alle " + LocalTime(sessionUntilUtc) +
                                            ") /blocca e /spegni non chiedono il codice.");
                        host.Audit(m.FromId, "/sblocca", "sessione di " + SessionMinutes + " minuti");
                    }
                    break;
                }

                case "screenshot":
                {
                    if (!CheckCode(m, "screenshot", args, nowUtc)) break;
                    if ((nowUtc - lastScreenshotUtc).TotalSeconds < ScreenshotIntervalSeconds)
                    {
                        host.SendText(chat, "Uno screenshot ogni " + ScreenshotIntervalSeconds + " secondi: riprova tra poco.");
                        host.Audit(m.FromId, "/screenshot", "troppo presto");
                        break;
                    }
                    lastScreenshotUtc = nowUtc;
                    int monitor = 0;
                    bool hd = false;
                    foreach (string a in args)
                    {
                        int n;
                        if (a.Equals("hd", StringComparison.OrdinalIgnoreCase)) hd = true;
                        else if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 16) monitor = n;
                    }
                    ScreenshotResult shot = host.CaptureScreenshot(monitor, hd);
                    if (shot.Error != null)
                    {
                        host.SendText(chat, shot.Error);
                        host.Audit(m.FromId, "/screenshot", shot.Error);
                    }
                    else
                    {
                        host.SendScreenshot(chat, shot);
                        host.NotifyLocal("screenshot");
                        host.Audit(m.FromId, "/screenshot" + (monitor > 0 ? " " + monitor : "") + (hd ? " hd" : ""), "inviato");
                    }
                    break;
                }

                case "app":
                {
                    if (!CheckCode(m, "app", args, nowUtc)) break;
                    if ((nowUtc - lastAppUtc).TotalSeconds < ScreenshotIntervalSeconds)
                    {
                        host.SendText(chat, "Riprova tra qualche secondo.");
                        break;
                    }
                    lastAppUtc = nowUtc;
                    host.SendText(chat, FormatForeground(host.GetForegroundInfo(), DateTime.UtcNow));
                    host.NotifyLocal("app");
                    host.Audit(m.FromId, "/app", "ok");
                    break;
                }

                case "chat":
                    host.OpenChat(true);
                    host.SendText(chat, "💬 Richiesta di chat mostrata sul PC: scrivi qui i messaggi (senza /). Ti avviso se la aprono o la rifiutano.");
                    host.Audit(m.FromId, "/chat", "ok");
                    break;

                case "blocca":
                {
                    if (!CheckCode(m, "blocca", args, nowUtc)) break;
                    LockResult r = host.Lock();
                    string reply = r == LockResult.Locked ? "🔒 PC bloccato alle " + LocalTime(DateTime.UtcNow) + "."
                                 : r == LockResult.AlreadyLocked ? "Il PC era già bloccato."
                                 : "Non è stato possibile bloccare il PC.";
                    host.SendText(chat, reply);
                    host.Audit(m.FromId, "/blocca", r.ToString());
                    break;
                }

                case "spegni":
                {
                    if (!CheckCode(m, "spegni", args, nowUtc)) break;
                    int minutes = settings.ShutdownDefaultMinutes;
                    if (args.Count > 0)
                    {
                        int n;
                        if (args.Count > 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out n) ||
                            n < RemoteSettings.MinShutdownMinutes || n > RemoteSettings.MaxShutdownMinutes)
                        {
                            host.SendText(chat, "Uso: /spegni 123456 [minuti], minuti da " + RemoteSettings.MinShutdownMinutes +
                                                " a " + RemoteSettings.MaxShutdownMinutes + " (predefinito " + settings.ShutdownDefaultMinutes + ").");
                            host.Audit(m.FromId, "/spegni", "argomenti non validi");
                            break;
                        }
                        minutes = n;
                    }
                    bool replacing = host.ShutdownActive;
                    DateTime due = host.StartShutdown(minutes);
                    host.SendText(chat, "⏻ Spegnimento alle " + due.ToString("HH:mm", CultureInfo.InvariantCulture) +
                                        (replacing ? " (sostituisce il precedente)" : "") + ". /annulla per fermarlo." +
                                        (host.PcLocked ? "\n" + LockedShutdownNote : ""));
                    host.Audit(m.FromId, "/spegni " + minutes, "conto alla rovescia avviato");
                    break;
                }

                case "annulla":
                {
                    bool was = host.CancelShutdown(true);
                    host.SendText(chat, was ? "Spegnimento annullato." : "Nessuno spegnimento in corso.");
                    host.Audit(m.FromId, "/annulla", was ? "annullato" : "niente da annullare");
                    break;
                }

                case "stop":
                    host.Audit(m.FromId, "/stop", "controllo remoto disattivato");
                    host.SendText(chat, "Controllo remoto disattivato. Si riattiva solo dal PC.");
                    host.DisableRemote();
                    break;

                case "abbina":
                    host.SendText(chat, "Questo telefono è già abbinato.");
                    break;

                default:
                    host.SendText(chat, "Comando sconosciuto. /aiuto per l'elenco.");
                    host.Audit(m.FromId, "/" + cmd, "sconosciuto");
                    break;
            }
        }

        public string HelpText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("WinUsage su ").Append(Environment.MachineName).Append("\n\n");
            sb.Append("/stato – tempi di utilizzo\n");
            sb.Append("/app – applicazione in uso").Append(secrets.CodeApp ? " (codice)" : "").Append('\n');
            sb.Append("/screenshot [n] [hd] – cattura dello schermo").Append(secrets.CodeScreenshot ? " (codice)" : "").Append('\n');
            sb.Append("/chat – apre la chat sul PC\n");
            sb.Append("/blocca").Append(secrets.CodeLock ? " CODICE" : "").Append(" – blocca il PC\n");
            sb.Append("/spegni").Append(secrets.CodeShutdown ? " CODICE" : "").Append(" [minuti] – spegne dopo un conto alla rovescia (")
              .Append(settings.ShutdownDefaultMinutes).Append(" min)\n");
            sb.Append("/annulla – ferma lo spegnimento\n");
            sb.Append("/sblocca CODICE – niente codice per ").Append(SessionMinutes).Append(" minuti\n");
            sb.Append("/stop – disattiva il controllo remoto (si riattiva dal PC)\n\n");
            sb.Append("Il testo senza / va nella chat del PC.\n");
            sb.Append("Il PC non ti chiederà mai codici o password in chat: se succede, non è lui.");
            return sb.ToString();
        }

        /// <summary>Testo di /app. Non contiene mai il titolo della finestra (decisione di design).</summary>
        public static string FormatForeground(ForegroundInfo fi, DateTime nowUtc)
        {
            if (fi.State == SessionState.Locked)
                return "🔒 PC bloccato" + (fi.LockedSinceUtc > DateTime.MinValue ? " dalle " + LocalTime(fi.LockedSinceUtc) : "") + ".";
            if (fi.State == SessionState.Disconnected)
                return "👥 Un altro utente sta usando il PC (sessione disconnessa).";

            StringBuilder sb = new StringBuilder();
            string name = string.IsNullOrEmpty(fi.AppName) ? fi.ProcessName : fi.AppName;
            if (string.IsNullOrEmpty(name)) sb.Append("🖥️ Nessuna applicazione in primo piano");
            else
            {
                sb.Append("🖥️ In uso: ").Append(name);
                if (!string.IsNullOrEmpty(fi.ProcessName) && !string.Equals(name, fi.ProcessName, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(fi.ProcessName).Append(".exe)");
                if (fi.SinceUtc > DateTime.MinValue) sb.Append(" da ").Append(Fmt.Long((nowUtc - fi.SinceUtc).TotalSeconds));
            }
            sb.Append("\n⌨️ Ultimo input: ").Append(Ago(fi.IdleSeconds));
            sb.Append("\n🎮 Gioco Steam in corso: ").Append(fi.SteamGame ? "sì" : "no");
            sb.Append("\n🖵 Schermo intero: ").Append(fi.Fullscreen ? "sì" : "no");
            return sb.ToString();
        }

        public static string Ago(double seconds)
        {
            if (seconds < 60) return Math.Max(0, (int)seconds) + " s fa";
            return Fmt.Long(seconds) + " fa";
        }

        public static string LocalTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>"alle 23:40", "ieri alle 23:40", "il 12/11 alle 23:40" rispetto a <paramref name="nowUtc"/>.</summary>
        public static string When(DateTime utc, DateTime nowUtc)
        {
            DateTime local = utc.ToLocalTime(), now = nowUtc.ToLocalTime();
            string hm = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (local.Date == now.Date) return "alle " + hm;
            if (local.Date == now.Date.AddDays(-1)) return "ieri alle " + hm;
            return "il " + local.ToString("dd/MM", CultureInfo.InvariantCulture) + " alle " + hm;
        }
    }

    /// <summary>Testi dei messaggi di accensione e spegnimento.</summary>
    internal static class PowerMessages
    {
        public static string PcName { get { return Environment.MachineName; } }

        public static string On(DateTime utc, bool autostart, DateTime nowUtc)
        {
            return autostart
                ? "🟢 PC acceso — " + PcName + ", accesso " + RemoteController.When(utc, nowUtc)
                : "🟢 WinUsage avviato — " + PcName + " " + RemoteController.When(utc, nowUtc);
        }

        public static string Off(DateTime utc, bool logoff, double sessionSeconds, long[] today, DateTime nowUtc)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(logoff ? "🔴 Disconnessione — " : "🔴 PC spento o riavviato — ").Append(PcName).Append(' ')
              .Append(RemoteController.When(utc, nowUtc));
            sb.Append(" · sessione ").Append(Fmt.Compact(sessionSeconds));
            sb.Append(" · oggi: ");
            for (int i = 0; i < TimerIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TimerIds.Labels[i]).Append(' ').Append(Fmt.Compact(today[i]));
            }
            return sb.ToString();
        }

        public static string AppClosed(DateTime utc, DateTime nowUtc)
        {
            return "⚪ WinUsage chiuso — " + PcName + " " + RemoteController.When(utc, nowUtc) +
                   " (uscita dall'app o aggiornamento): il PC resta acceso senza controllo remoto.";
        }

        public static string Crash(DateTime lastSeenUtc, DateTime nowUtc)
        {
            return "⚠️ Spegnimento improvviso — " + PcName + ": ultimo segnale " + RemoteController.When(lastSeenUtc, nowUtc) +
                   " (±30 s). Mancanza di corrente, blocco o spegnimento forzato.";
        }

        public static string Late(string text, DateTime createdUtc, DateTime nowUtc)
        {
            return (nowUtc - createdUtc).TotalMinutes >= 2 ? text + " (notificato in ritardo)" : text;
        }
    }
}
