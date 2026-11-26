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

    /// <summary>Un telefono abbinato: account Telegram e segreto del suo Authenticator (ogni telefono ha il suo).</summary>
    internal sealed class PairedPhone
    {
        public long Id;                      // id dell'account Telegram (= chat privata con il bot)
        public string Name = "";
        public string TotpSecret = "";       // base32; vuoto = codice non configurato per questo telefono
        public bool TotpVerified;

        public bool HasTotp { get { return TotpVerified && SecretBytes() != null; } }

        public byte[] SecretBytes()
        {
            byte[] b = Base32.Decode(TotpSecret);
            return b != null && b.Length >= 10 ? b : null;
        }

        public PairedPhone Clone()
        {
            return (PairedPhone)MemberwiseClone();
        }

        /// <summary>Nome da mostrare (quello di Telegram, o l'id se manca).</summary>
        public string Label
        {
            get { return string.IsNullOrEmpty(Name) || Name == "?" ? L.T("telefono ", "phone ") + Id.ToString(CultureInfo.InvariantCulture) : Name; }
        }
    }

    /// <summary>
    /// Valori di sicurezza del controllo remoto, salvati in un unico blob cifrato con DPAPI (utente corrente).
    /// Chi modifica il database non può cambiare i telefoni abbinati: un blob alterato non si decifra.
    /// Dalla 1.5 più telefoni, ognuno con il suo codice Authenticator; il formato della 1.4 (un telefono) si legge ancora.
    /// </summary>
    internal sealed class RemoteSecrets
    {
        public const int MaxPhones = 5;

        public string Token = "";
        public List<PairedPhone> Phones = new List<PairedPhone>();
        public bool CodeScreenshot;          // protezione dei comandi (decisioni di design: screenshot e app senza codice)
        public bool CodeApp;
        public bool CodeLock = true;
        public bool CodeShutdown = true;
        public bool CodePhoto = true;        // 1.7: /foto con la webcam, con il codice di default
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

        public bool Paired { get { return Phones.Count > 0; } }
        public bool HasToken { get { return !string.IsNullOrEmpty(Token); } }
        public bool CanAddPhone { get { return Phones.Count < MaxPhones; } }

        public PairedPhone Find(long id)
        {
            foreach (PairedPhone p in Phones) if (p.Id == id) return p;
            return null;
        }

        public bool IsPaired(long id) { return id != 0 && Find(id) != null; }

        public string NameOf(long id)
        {
            PairedPhone p = Find(id);
            return p != null ? p.Label : id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Nomi dei telefoni separati da virgola ("Mirko, Anna").</summary>
        public string Names()
        {
            List<string> n = new List<string>();
            foreach (PairedPhone p in Phones) n.Add(p.Label);
            return string.Join(", ", n.ToArray());
        }

        public RemoteSecrets Clone()
        {
            RemoteSecrets c = (RemoteSecrets)MemberwiseClone();
            c.Phones = new List<PairedPhone>();
            foreach (PairedPhone p in Phones) c.Phones.Add(p.Clone());
            return c;
        }

        public bool RequiresCode(string command)
        {
            switch (command)
            {
                case "screenshot": return CodeScreenshot;
                case "app": return CodeApp;
                case "blocca": return CodeLock;
                case "spegni": return CodeShutdown;
                case "foto": return CodePhoto;
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
            Line(sb, "phones", Phones.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < Phones.Count; i++)
            {
                string k = "phone" + i.ToString(CultureInfo.InvariantCulture) + "_";
                Line(sb, k + "id", Phones[i].Id.ToString(CultureInfo.InvariantCulture));
                Line(sb, k + "name", Phones[i].Name);
                Line(sb, k + "totp", Phones[i].TotpSecret);
                Line(sb, k + "totp_ok", Phones[i].TotpVerified ? "1" : "0");
            }
            Line(sb, "code_screenshot", CodeScreenshot ? "1" : "0");
            Line(sb, "code_app", CodeApp ? "1" : "0");
            Line(sb, "code_lock", CodeLock ? "1" : "0");
            Line(sb, "code_shutdown", CodeShutdown ? "1" : "0");
            Line(sb, "code_photo", CodePhoto ? "1" : "0");
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
            Dictionary<string, string> kv = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                kv[line.Substring(0, eq)] = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(eq + 1)));
            }

            string count;
            if (kv.TryGetValue("phones", out count))
            {
                int n;
                int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                for (int i = 0; i < n && i < MaxPhones; i++)
                {
                    string k = "phone" + i.ToString(CultureInfo.InvariantCulture) + "_";
                    PairedPhone p = new PairedPhone();
                    string v;
                    if (kv.TryGetValue(k + "id", out v)) long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out p.Id);
                    if (kv.TryGetValue(k + "name", out v)) p.Name = v;
                    if (kv.TryGetValue(k + "totp", out v)) p.TotpSecret = v;
                    if (kv.TryGetValue(k + "totp_ok", out v)) p.TotpVerified = v == "1";
                    if (p.Id != 0 && s.Find(p.Id) == null) s.Phones.Add(p);
                }
            }
            else
            {
                // formato della 1.4: un solo telefono, con il codice Authenticator unico che diventa il suo
                long id = 0;
                string v;
                if (kv.TryGetValue("user_id", out v)) long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                if (id != 0)
                {
                    PairedPhone p = new PairedPhone();
                    p.Id = id;
                    if (kv.TryGetValue("user_name", out v)) p.Name = v;
                    if (kv.TryGetValue("totp", out v)) p.TotpSecret = v;
                    if (kv.TryGetValue("totp_ok", out v)) p.TotpVerified = v == "1";
                    s.Phones.Add(p);
                }
            }

            foreach (KeyValuePair<string, string> e in kv)
            {
                string key = e.Key, v = e.Value;
                switch (key)
                {
                    case "token": s.Token = v; break;
                    case "code_screenshot": s.CodeScreenshot = v == "1"; break;
                    case "code_app": s.CodeApp = v == "1"; break;
                    case "code_lock": s.CodeLock = v == "1"; break;
                    case "code_shutdown": s.CodeShutdown = v == "1"; break;
                    case "code_photo": s.CodePhoto = v == "1"; break;   // assente (blob fino alla 1.6): resta con il codice
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
        public bool NotifyPhoto = true;
        public bool NotifyApp = true;
        public bool NotifyChat = true;
        public bool NotifyLocked = true;
        public bool NotifyCountdown = true;
        public bool Sounds = true;

        // chat
        public bool ChatHistory = true;            // cronologia in memoria fino al riavvio; false = sparisce alla chiusura

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
            s.NotifyPhoto = B(st, "remote_notify_photo", s.NotifyPhoto);
            s.NotifyApp = B(st, "remote_notify_app", s.NotifyApp);
            s.NotifyChat = B(st, "remote_notify_chat", s.NotifyChat);
            s.NotifyLocked = B(st, "remote_notify_locked", s.NotifyLocked);
            s.NotifyCountdown = B(st, "remote_notify_countdown", s.NotifyCountdown);
            s.Sounds = B(st, "remote_sounds", s.Sounds);
            s.ChatHistory = B(st, "remote_chat_history", s.ChatHistory);
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
            st.SetSetting("remote_notify_photo", NotifyPhoto ? "1" : "0");
            st.SetSetting("remote_notify_app", NotifyApp ? "1" : "0");
            st.SetSetting("remote_notify_chat", NotifyChat ? "1" : "0");
            st.SetSetting("remote_notify_locked", NotifyLocked ? "1" : "0");
            st.SetSetting("remote_notify_countdown", NotifyCountdown ? "1" : "0");
            st.SetSetting("remote_sounds", Sounds ? "1" : "0");
            st.SetSetting("remote_chat_history", ChatHistory ? "1" : "0");
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
        public long ChatId;                  // 0 = riga della 1.4, per tutti i telefoni
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
                MigrateChatColumns();
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>
        /// Dalla 1.5 coda e screenshot da cancellare sanno a quale chat (telefono) appartengono. Le righe della 1.4 hanno
        /// chat_id 0 = "il telefono di allora": il servizio le assegna (coda: a tutti i telefoni; screenshot: al primo).
        /// remote_delete si ricrea perché la chiave diventa (chat_id, message_id): gli id dei messaggi sono per chat.
        /// </summary>
        private void MigrateChatColumns()
        {
            if (!HasColumn("remote_outbox", "chat_id"))
                st.Execute("ALTER TABLE remote_outbox ADD COLUMN chat_id INTEGER NOT NULL DEFAULT 0");
            if (!HasColumn("remote_delete", "chat_id"))
            {
                st.Execute("CREATE TABLE IF NOT EXISTS remote_delete2(chat_id INTEGER NOT NULL, message_id INTEGER NOT NULL, " +
                           "due_utc TEXT NOT NULL, PRIMARY KEY(chat_id, message_id))");
                st.Execute("INSERT OR IGNORE INTO remote_delete2(chat_id, message_id, due_utc) SELECT 0, message_id, due_utc FROM remote_delete");
                st.Execute("DROP TABLE remote_delete");
                st.Execute("ALTER TABLE remote_delete2 RENAME TO remote_delete");
            }
        }

        private bool HasColumn(string table, string column)
        {
            foreach (object[] row in st.Query("PRAGMA table_info(" + table + ")"))
                if (row.Length > 1 && string.Equals(row[1] as string, column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public long AddOutbox(string kind, string text, DateTime utc, long chatId)
        {
            try
            {
                st.Execute("INSERT INTO remote_outbox(created_utc, kind, text, chat_id) VALUES(?, ?, ?, ?)", Iso(utc), kind, text, chatId);
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
                foreach (object[] row in st.Query("SELECT id, created_utc, kind, text, chat_id FROM remote_outbox ORDER BY id"))
                {
                    OutboxItem it = new OutboxItem();
                    it.Id = (long)row[0];
                    it.CreatedUtc = ParseIso((string)row[1]);
                    it.Kind = (string)row[2];
                    it.Text = (string)row[3];
                    it.ChatId = (long)row[4];
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

        public void AddPendingDelete(long chatId, long messageId, DateTime dueUtc)
        {
            try
            {
                st.Execute("INSERT OR REPLACE INTO remote_delete(chat_id, message_id, due_utc) VALUES(?, ?, ?)", chatId, messageId, Iso(dueUtc));
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Screenshot da cancellare ora (chat, messaggio). Oltre le 47 ore Telegram non lo permette più: si lasciano perdere.</summary>
        public List<KeyValuePair<long, long>> DueDeletes(DateTime nowUtc)
        {
            List<KeyValuePair<long, long>> ids = new List<KeyValuePair<long, long>>();
            try
            {
                st.Execute("DELETE FROM remote_delete WHERE due_utc < ?", Iso(nowUtc.AddHours(-47)));
                foreach (object[] row in st.Query("SELECT chat_id, message_id FROM remote_delete WHERE due_utc <= ?", Iso(nowUtc)))
                    ids.Add(new KeyValuePair<long, long>((long)row[0], (long)row[1]));
            }
            catch (Exception ex) { Log.Write(ex); }
            return ids;
        }

        public void RemovePendingDelete(long chatId, long messageId)
        {
            try { st.Execute("DELETE FROM remote_delete WHERE chat_id=? AND message_id=?", chatId, messageId); }
            catch (Exception ex) { Log.Write(ex); }
        }

        public long Offset
        {
            get { return GetLong("remote_offset"); }
            set { st.SetSetting("remote_offset", value.ToString(CultureInfo.InvariantCulture)); }
        }

        /// <summary>
        /// Ultimo passo TOTP accettato per un telefono (un codice non si riusa). Il valore della 1.4 (unico, senza telefono)
        /// vale per il telefono di allora finché quel telefono non ne salva uno suo.
        /// </summary>
        public long TotpLastStep(long phoneId)
        {
            long own = GetLong("remote_totp_last_step_" + phoneId.ToString(CultureInfo.InvariantCulture));
            return Math.Max(own, GetLong("remote_totp_last_step"));
        }

        public void SetTotpLastStep(long phoneId, long step)
        {
            st.SetSetting("remote_totp_last_step_" + phoneId.ToString(CultureInfo.InvariantCulture), step.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>Tagli di testo che non spezzano le emoji (coppie surrogate UTF-16).</summary>
    internal static class TextCut
    {
        /// <summary>Quanti caratteri prendere da <paramref name="start"/>, al massimo <paramref name="max"/>, senza dividere una coppia surrogata.</summary>
        public static int SafeLength(string s, int start, int max)
        {
            int len = Math.Min(max, s.Length - start);
            if (len > 1 && start + len < s.Length && char.IsHighSurrogate(s[start + len - 1]) && char.IsLowSurrogate(s[start + len]))
                len--;
            return len;
        }

        /// <summary>Al massimo <paramref name="max"/> caratteri, senza lasciare mezza emoji in fondo.</summary>
        public static string Cut(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, SafeLength(s, 0, max));
        }
    }

    /// <summary>Un messaggio della chat: testo, direzione e ora (UTC).</summary>
    internal sealed class ChatEntry
    {
        public readonly string Text;
        public readonly bool Outgoing;     // true = scritto sul PC
        public readonly DateTime Utc;
        public readonly string Sender;     // nome del telefono che l'ha scritto (null per i messaggi del PC)

        public ChatEntry(string text, bool outgoing, DateTime utc) : this(text, outgoing, utc, null) { }

        public ChatEntry(string text, bool outgoing, DateTime utc, string sender)
        {
            Text = text;
            Outgoing = outgoing;
            Utc = utc;
            Sender = sender;
        }
    }

    /// <summary>
    /// Cronologia della chat, solo in memoria: non va mai nel database, quindi sparisce quando WinUsage si chiude (riavvio
    /// del PC compreso). Tiene gli ultimi <see cref="Max"/> messaggi. Con la cronologia disattivata contiene solo i
    /// messaggi arrivati a chat chiusa, che si vedono all'apertura e poi si cancellano.
    /// </summary>
    internal sealed class ChatHistory
    {
        public const int Max = 500;
        private readonly List<ChatEntry> entries = new List<ChatEntry>();

        public int Count { get { return entries.Count; } }

        public void Add(string text, bool outgoing, DateTime utc)
        {
            Add(text, outgoing, utc, null);
        }

        public void Add(string text, bool outgoing, DateTime utc, string sender)
        {
            entries.Add(new ChatEntry(text, outgoing, utc, sender));
            if (entries.Count > Max) entries.RemoveRange(0, entries.Count - Max);
        }

        public List<ChatEntry> Snapshot()
        {
            return new List<ChatEntry>(entries);
        }

        public void Clear()
        {
            entries.Clear();
        }
    }

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

    /// <summary>Immagine da mandare al telefono: screenshot o, dalla 1.7, foto della webcam.</summary>
    internal sealed class ScreenshotResult
    {
        public byte[] Data;
        public bool Png;
        public string Error;           // non null: cattura non possibile, con il motivo per il telefono
        public string Name = "screenshot";   // inizio del nome del file su Telegram ("foto" per la webcam)
        public string Caption;         // didascalia (foto: nome della fotocamera, poi il controller aggiunge l'ora)
        public bool Dark;              // foto quasi nera: obiettivo coperto o fotocamera spenta con il tasto della privacy
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
        /// <summary>Foto dalla webcam (0 = la prima). Può durare qualche secondo: <paramref name="done"/> arriva dopo, sul
        /// thread dell'interfaccia (nei test anche subito).</summary>
        void CapturePhoto(int camera, Action<ScreenshotResult> done);
        ForegroundInfo GetForegroundInfo();
        LockResult Lock();
        DateTime StartShutdown(int minutes);           // ora locale prevista
        bool CancelShutdown(bool fromRemote);
        bool ShutdownActive { get; }
        bool PcLocked { get; }                         // schermo bloccato (nessuno può salvare i documenti)
        void Broadcast(string text, long exceptId);    // a tutti i telefoni abbinati tranne exceptId (0 = a tutti)
        void OpenChat(bool requestedRemotely, long fromId);
        void DeliverChat(string text, DateTime sentUtc, long fromId);
        void DisableRemote();
        void ConfirmPairing(long userId, string name, Action<bool> done);
        void NotifyLocal(string kind);                 // "screenshot", "app", "foto"
        void SecurityAlert(string text);
        void Audit(long senderId, string command, string result);
        void SaveSecrets();
        void SaveTotpStep(long phoneId, long step);
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

        /// <summary>Stato dei codici di un telefono: ognuno ha il suo segreto, la sua sessione /sblocca e il suo blocco.</summary>
        private sealed class CodeState
        {
            public DateTime SessionUntilUtc = DateTime.MinValue;
            public int Failures;
            public DateTime LockoutUntilUtc = DateTime.MinValue;
            public long LastStep;
        }

        private readonly Dictionary<long, CodeState> codeStates = new Dictionary<long, CodeState>();
        private readonly Func<long, long> loadStep;
        private DateTime lastScreenshotUtc = DateTime.MinValue;
        private DateTime lastAppUtc = DateTime.MinValue;
        private DateTime lastPhotoUtc = DateTime.MinValue;
        private bool photoBusy;
        public const int MaxCameras = 9;
        private readonly Queue<DateTime> recentCommands = new Queue<DateTime>();
        private DateTime floodNoticeUtc = DateTime.MinValue;

        /// <param name="loadStep">Ultimo passo TOTP salvato per un telefono (per non accettare due volte lo stesso codice).</param>
        public RemoteController(IRemoteHost host, RemoteSecrets secrets, RemoteSettings settings, Func<long, long> loadStep)
        {
            this.host = host;
            this.secrets = secrets;
            this.settings = settings;
            this.loadStep = loadStep;
        }

        public void Apply(RemoteSecrets newSecrets, RemoteSettings newSettings)
        {
            secrets = newSecrets;
            settings = newSettings;
            // telefoni tolti: via anche il loro stato (un telefono riabbinato riparte da capo)
            List<long> gone = new List<long>();
            foreach (long id in codeStates.Keys) if (!secrets.IsPaired(id)) gone.Add(id);
            foreach (long id in gone) codeStates.Remove(id);
        }

        private CodeState State(long phoneId)
        {
            CodeState st;
            if (!codeStates.TryGetValue(phoneId, out st))
            {
                st = new CodeState();
                st.LastStep = loadStep != null ? loadStep(phoneId) : 0;
                codeStates[phoneId] = st;
            }
            return st;
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
            if (m == null) return L.T("vuoto", "empty");
            if (m.ChatType != "private") return L.T("chat non privata", "not a private chat");
            if (m.FromIsBot) return L.T("mittente bot", "sender is a bot");
            if (m.ChatId != m.FromId) return L.T("chat diversa dal mittente", "chat differs from sender");
            if (m.Forwarded) return L.T("messaggio inoltrato", "forwarded message");
            if (m.ViaBot) return L.T("messaggio via bot", "message via bot");
            if (m.Text == null) return L.T("non testuale", "not text");
            return null;
        }

        /// <param name="serverNowUtc">Ora del server Telegram (header Date), non dell'orologio del PC.</param>
        public void Process(IncomingMessage m, DateTime serverNowUtc)
        {
            string reject = Reject(m);
            if (reject != null)
            {
                if (m != null && secrets.IsPaired(m.FromId))
                    host.Audit(m.FromId, L.T("(messaggio ignorato)", "(message ignored)"), reject);
                return;
            }

            string text = m.Text.Trim();
            string cmd;
            string[] args;
            ParseCommand(text, out cmd, out args);

            if (!secrets.IsPaired(m.FromId))
            {
                // Un telefono nuovo (il primo o uno in più) si abbina solo con il codice mostrato sul PC. Agli altri non si
                // risponde mai: il bot non si fa riconoscere.
                if (cmd == "abbina" && PairingOpen(serverNowUtc) && !pairingConfirmPending) HandlePairing(m, args, serverNowUtc);
                else if (secrets.Paired) host.Audit(m.FromId, cmd != null ? Cmd(cmd) : L.T("(testo)", "(text)"), L.T("mittente non autorizzato, ignorato", "unauthorised sender, ignored"));
                return;
            }

            if (cmd == null)
            {
                string t = TextCut.Cut(text, MaxChatLength);
                host.DeliverChat(t, m.DateUtc, m.FromId);
                return;
            }

            if (Flooding(serverNowUtc))
            {
                if ((serverNowUtc - floodNoticeUtc).TotalSeconds > 60)
                {
                    floodNoticeUtc = serverNowUtc;
                    host.SendText(m.ChatId, L.T("Troppi comandi in poco tempo: riprova tra un minuto.", "Too many commands in a short time: try again in a minute."));
                }
                host.Audit(m.FromId, Cmd(cmd), L.T("troppi comandi, ignorato", "too many commands, ignored"));
                return;
            }

            // Solo i comandi che agiscono sul PC con il codice scadono: un /blocca o /spegni arrivato molto dopo non deve
            // partire a sorpresa. Quelli che leggono soltanto (/stato, /app, /screenshot, /chat...) si eseguono anche in
            // ritardo (dopo uno standby o una rete caduta), invece di costringere a ripeterli.
            double age = (serverNowUtc - m.DateUtc).TotalSeconds;
            if (age > settings.MaxAgeSeconds && ExpiresWhenLate(cmd))
            {
                host.SendText(m.ChatId, L.T("⌛ Comando scaduto (inviato alle ", "⌛ Command expired (sent at ") + LocalTime(m.DateUtc) + L.T("): rimandalo se serve ancora.", "): send it again if you still need it."));
                host.Audit(m.FromId, Cmd(cmd), L.T("scaduto", "expired"));
                return;
            }

            Execute(m, cmd, args, serverNowUtc);
        }

        public static string LockedShutdownNote
        {
            get { return L.T("🔒 Il PC è bloccato: se lo è ancora alla scadenza, le app aperte verranno chiuse senza salvare.", "🔒 The PC is locked: if it still is when time runs out, open apps will be closed without saving."); }
        }

        /// <summary>Comandi che non si eseguono se arrivano oltre il tempo massimo (quelli che agiscono sul PC o sulla webcam).</summary>
        public static bool ExpiresWhenLate(string cmd)
        {
            // /foto: una foto della stanza scattata a sorpresa ore dopo la richiesta (al risveglio del PC) non serve a nessuno
            return cmd == "blocca" || cmd == "spegni" || cmd == "sblocca" || cmd == "foto";
        }

        private void HandlePairing(IncomingMessage m, string[] args, DateTime nowUtc)
        {
            pairingAttempts++;
            string given = args.Length > 0 ? args[0].ToUpperInvariant() : "";
            if (given == pairingCode)
            {
                long userId = m.FromId;
                long chatId = m.ChatId;
                if (!secrets.CanAddPhone)
                {
                    pairingCode = null;
                    host.SendText(chatId, L.T("Ci sono già ", "There are already ") + RemoteSecrets.MaxPhones + L.T(" telefoni abbinati: rimuovine uno dal PC e riprova.", " paired phones: remove one from the PC and try again."));
                    host.Audit(userId, L.T("/abbina", "/pair"), L.T("troppi telefoni", "too many phones"));
                    return;
                }
                pairingConfirmPending = true;
                string name = string.IsNullOrEmpty(m.FromName) ? "?" : m.FromName;
                host.SendText(chatId, L.T("Codice corretto. Conferma l'abbinamento sul PC.", "Correct code. Confirm the pairing on the PC."));
                host.ConfirmPairing(userId, name, delegate(bool ok)
                {
                    pairingConfirmPending = false;
                    pairingCode = null;
                    if (ok && secrets.Find(userId) == null && secrets.CanAddPhone)
                    {
                        PairedPhone phone = new PairedPhone();
                        phone.Id = userId;
                        phone.Name = name;
                        secrets.Phones.Add(phone);
                        codeStates.Remove(userId);
                        host.SaveSecrets();
                        host.Audit(userId, L.T("/abbina", "/pair"), L.T("abbinato", "paired"));
                        host.SendText(chatId, L.T("✅ Telefono abbinato a ", "✅ Phone paired with ") + Environment.MachineName + ".\n\n" + HelpText());
                        // gli altri telefoni lo sanno: un abbinamento che nessuno aspettava salta all'occhio
                        if (secrets.Phones.Count > 1)
                            host.Broadcast(L.T("📱 Nuovo telefono abbinato a ", "📱 New phone paired with ") + Environment.MachineName + ": " + phone.Label + ".", userId);
                    }
                    else
                    {
                        host.Audit(userId, L.T("/abbina", "/pair"), L.T("rifiutato dal PC", "refused on the PC"));
                        host.SendText(chatId, L.T("Abbinamento rifiutato dal PC.", "Pairing refused on the PC."));
                    }
                });
                return;
            }

            host.Audit(m.FromId, L.T("/abbina", "/pair"), L.T("codice errato (", "wrong code (") + pairingAttempts + "/" + PairingMaxAttempts + ")");
            if (pairingAttempts >= PairingMaxAttempts)
            {
                pairingCode = null;
                host.SendText(m.ChatId, L.T("Codice errato. Troppi tentativi: abbinamento chiuso.", "Wrong code. Too many attempts: pairing closed."));
                host.SecurityAlert(L.T("Troppi tentativi di abbinamento con un codice errato (account id ", "Too many pairing attempts with a wrong code (account id ") + m.FromId +
                                   L.T("). L'abbinamento è stato chiuso.", "). Pairing has been closed."));
            }
            else
            {
                host.SendText(m.ChatId, L.T("Codice errato.", "Wrong code."));
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
            cmd = Canonical(head.ToLowerInvariant());
            args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
        }

        // Comandi in tutte e due le lingue: dentro si usa sempre il nome italiano, il messaggio può usare l'uno o l'altro.
        private static readonly string[][] CommandNames =
        {
            new[] { "aiuto", "help" }, new[] { "stato", "status" }, new[] { "screenshot", "screenshot" },
            new[] { "app", "app" }, new[] { "chat", "chat" }, new[] { "blocca", "lock" }, new[] { "spegni", "shutdown" },
            new[] { "annulla", "cancel" }, new[] { "sblocca", "unlock" }, new[] { "telefoni", "phones" },
            new[] { "stop", "stop" }, new[] { "abbina", "pair" }, new[] { "foto", "photo" }
        };

        /// <summary>Nome interno (italiano) di un comando scritto in italiano o in inglese.</summary>
        public static string Canonical(string name)
        {
            foreach (string[] n in CommandNames) if (n[1] == name) return n[0];
            if (name == "poweroff" || name == "off") return "spegni";
            if (name == "webcam") return "foto";
            return name;
        }

        /// <summary>Nome del comando da mostrare, con la barra, nella lingua scelta ("/blocca" o "/lock").</summary>
        public static string Cmd(string canonical)
        {
            foreach (string[] n in CommandNames) if (n[0] == canonical) return "/" + (L.En ? n[1] : n[0]);
            return "/" + canonical;
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

            PairedPhone phone = secrets.Find(m.FromId);
            CodeState st = State(m.FromId);
            if (phone == null || !phone.HasTotp)
            {
                host.SendText(m.ChatId, L.T("Questo comando richiede il codice dell'Authenticator, che per questo telefono non è ancora ", "This command needs the Authenticator code, which for this phone is not yet ") +
                                        L.T("configurato sul PC (Controllo remoto > Codice).", "set up on the PC (Remote control > Code)."));
                host.Audit(m.FromId, Cmd(cmd), L.T("codice non configurato", "code not set up"));
                return false;
            }

            if (nowUtc < st.LockoutUntilUtc)
            {
                host.SendText(m.ChatId, L.T("⛔ Troppi codici errati: i comandi con codice sono bloccati fino alle ", "⛔ Too many wrong codes: commands with a code are blocked until ") + LocalTime(st.LockoutUntilUtc) + ".");
                host.Audit(m.FromId, Cmd(cmd), L.T("bloccato per codici errati", "blocked for wrong codes"));
                return false;
            }

            if (code == null)
            {
                if (nowUtc < st.SessionUntilUtc) return true;
                host.SendText(m.ChatId, L.T("Serve il codice dell'Authenticator: ", "The Authenticator code is needed: ") + Cmd(cmd) + " 123456\n" +
                                        L.T("Oppure /sblocca 123456 per non chiederlo per ", "Or /unlock 123456 so it is not asked for ") + SessionMinutes + L.T(" minuti.", " minutes."));
                host.Audit(m.FromId, Cmd(cmd), L.T("codice mancante", "code missing"));
                return false;
            }

            return VerifyCode(m, Cmd(cmd), code, nowUtc);
        }

        /// <summary>Verifica il codice con il segreto del telefono che scrive (ogni telefono ha il suo).</summary>
        private bool VerifyCode(IncomingMessage m, string what, string code, DateTime nowUtc)
        {
            PairedPhone phone = secrets.Find(m.FromId);
            CodeState st = State(m.FromId);
            long step = phone != null ? Totp.Verify(phone.SecretBytes(), code, nowUtc, st.LastStep) : -1;
            if (step >= 0)
            {
                st.LastStep = step;
                host.SaveTotpStep(m.FromId, step);
                st.Failures = 0;
                return true;
            }

            st.Failures++;
            if (st.Failures >= MaxCodeFailures)
            {
                st.Failures = 0;
                st.LockoutUntilUtc = nowUtc.AddMinutes(LockoutMinutes);
                st.SessionUntilUtc = DateTime.MinValue;
                host.SendText(m.ChatId, L.T("⛔ Codice errato per ", "⛔ Wrong code ") + MaxCodeFailures + L.T(" volte: comandi con codice bloccati per ", " times: commands with a code blocked for ") +
                                        LockoutMinutes + L.T(" minuti.", " minutes."));
                host.SecurityAlert(L.T("Codice dell'Authenticator sbagliato ", "Wrong Authenticator code ") + MaxCodeFailures + L.T(" volte dal telefono di ", " times from the phone of ") +
                                   secrets.NameOf(m.FromId) + L.T(". Per quel telefono i comandi con codice sono bloccati per ", ". For that phone, commands with a code are blocked for ") +
                                   LockoutMinutes + L.T(" minuti.", " minutes."));
                host.Audit(m.FromId, what, L.T("codice errato, blocco di ", "wrong code, blocked for ") + LockoutMinutes + L.T(" minuti", " minutes"));
            }
            else
            {
                host.SendText(m.ChatId, L.T("Codice errato o già usato. Aspetta il prossimo codice e riprova.", "Wrong or already used code. Wait for the next code and try again."));
                host.Audit(m.FromId, what, L.T("codice errato", "wrong code"));
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
                    host.Audit(m.FromId, Cmd(cmd), "ok");
                    break;

                case "stato":
                    host.SendText(chat, host.StatusText());
                    host.Audit(m.FromId, L.T("/stato", "/status"), "ok");
                    break;

                case "sblocca":
                {
                    string code = args.Count > 0 ? args[0] : null;
                    PairedPhone phone = secrets.Find(m.FromId);
                    CodeState st = State(m.FromId);
                    if (phone == null || !phone.HasTotp)
                    {
                        host.SendText(chat, L.T("Il codice dell'Authenticator per questo telefono non è configurato sul PC.", "The Authenticator code for this phone is not set up on the PC."));
                        host.Audit(m.FromId, L.T("/sblocca", "/unlock"), L.T("codice non configurato", "code not set up"));
                        break;
                    }
                    if (nowUtc < st.LockoutUntilUtc)
                    {
                        host.SendText(chat, L.T("⛔ Comandi con codice bloccati fino alle ", "⛔ Commands with a code blocked until ") + LocalTime(st.LockoutUntilUtc) + ".");
                        host.Audit(m.FromId, L.T("/sblocca", "/unlock"), L.T("bloccato per codici errati", "blocked for wrong codes"));
                        break;
                    }
                    if (!IsCode(code))
                    {
                        host.SendText(chat, L.T("Uso: /sblocca 123456", "Usage: /unlock 123456"));
                        break;
                    }
                    if (VerifyCode(m, L.T("/sblocca", "/unlock"), code, nowUtc))
                    {
                        st.SessionUntilUtc = nowUtc.AddMinutes(SessionMinutes);
                        host.SendText(chat, L.T("🔓 Per ", "🔓 For ") + SessionMinutes + L.T(" minuti (fino alle ", " minutes (until ") + LocalTime(st.SessionUntilUtc) +
                                            ") " + CodeCommandsText() + ".");
                        host.Audit(m.FromId, L.T("/sblocca", "/unlock"), L.T("sessione di ", "session of ") + SessionMinutes + L.T(" minuti", " minutes"));
                    }
                    break;
                }

                case "screenshot":
                {
                    if (!CheckCode(m, "screenshot", args, nowUtc)) break;
                    if ((nowUtc - lastScreenshotUtc).TotalSeconds < ScreenshotIntervalSeconds)
                    {
                        host.SendText(chat, L.T("Uno screenshot ogni ", "One screenshot every ") + ScreenshotIntervalSeconds + L.T(" secondi: riprova tra poco.", " seconds: try again shortly."));
                        host.Audit(m.FromId, "/screenshot", L.T("troppo presto", "too soon"));
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
                        host.Audit(m.FromId, "/screenshot" + (monitor > 0 ? " " + monitor : "") + (hd ? " hd" : ""), L.T("inviato", "sent"));
                    }
                    break;
                }

                case "foto":
                    TakePhoto(m, args, nowUtc);
                    break;

                case "app":
                {
                    if (!CheckCode(m, "app", args, nowUtc)) break;
                    if ((nowUtc - lastAppUtc).TotalSeconds < ScreenshotIntervalSeconds)
                    {
                        host.SendText(chat, L.T("Riprova tra qualche secondo.", "Try again in a few seconds."));
                        break;
                    }
                    lastAppUtc = nowUtc;
                    host.SendText(chat, FormatForeground(host.GetForegroundInfo(), DateTime.UtcNow));
                    host.NotifyLocal("app");
                    host.Audit(m.FromId, "/app", "ok");
                    break;
                }

                case "chat":
                    host.OpenChat(true, m.FromId);
                    host.SendText(chat, L.T("💬 Richiesta di chat mostrata sul PC: scrivi qui i messaggi (senza /). Ti avviso se la aprono o la rifiutano.", "💬 Chat request shown on the PC: write your messages here (without /). I will tell you if it is opened or refused."));
                    host.Audit(m.FromId, "/chat", "ok");
                    break;

                case "blocca":
                {
                    if (!CheckCode(m, "blocca", args, nowUtc)) break;
                    LockResult r = host.Lock();
                    string reply = r == LockResult.Locked ? L.T("🔒 PC bloccato alle ", "🔒 PC locked at ") + LocalTime(DateTime.UtcNow) + "."
                                 : r == LockResult.AlreadyLocked ? L.T("Il PC era già bloccato.", "The PC was already locked.")
                                 : L.T("Non è stato possibile bloccare il PC.", "The PC could not be locked.");
                    host.SendText(chat, reply);
                    if (r == LockResult.Locked)
                        host.Broadcast("🔒 " + secrets.NameOf(m.FromId) + L.T(" ha bloccato il PC alle ", " locked the PC at ") + LocalTime(DateTime.UtcNow) + ".", m.FromId);
                    host.Audit(m.FromId, L.T("/blocca", "/lock"), r.ToString());
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
                            host.SendText(chat, L.T("Uso: /spegni 123456 [minuti], minuti da ", "Usage: /shutdown 123456 [minutes], minutes from ") + RemoteSettings.MinShutdownMinutes +
                                                L.T(" a ", " to ") + RemoteSettings.MaxShutdownMinutes + L.T(" (predefinito ", " (default ") + settings.ShutdownDefaultMinutes + ").");
                            host.Audit(m.FromId, L.T("/spegni", "/shutdown"), L.T("argomenti non validi", "invalid arguments"));
                            break;
                        }
                        minutes = n;
                    }
                    bool replacing = host.ShutdownActive;
                    DateTime due = host.StartShutdown(minutes);
                    host.SendText(chat, L.T("⏻ Spegnimento alle ", "⏻ Shutdown at ") + due.ToString("HH:mm", CultureInfo.InvariantCulture) +
                                        (replacing ? L.T(" (sostituisce il precedente)", " (replaces the previous one)") : "") + L.T(". /annulla per fermarlo.", ". /cancel to stop it.") +
                                        (host.PcLocked ? "\n" + LockedShutdownNote : ""));
                    host.Broadcast("⏻ " + secrets.NameOf(m.FromId) + L.T(" ha avviato lo spegnimento: alle ", " started the shutdown: at ") +
                                   due.ToString("HH:mm", CultureInfo.InvariantCulture) + L.T(". /annulla per fermarlo.", ". /cancel to stop it."), m.FromId);
                    host.Audit(m.FromId, L.T("/spegni ", "/shutdown ") + minutes, L.T("conto alla rovescia avviato", "countdown started"));
                    break;
                }

                case "annulla":
                {
                    bool was = host.CancelShutdown(true);
                    host.SendText(chat, was ? L.T("Spegnimento annullato.", "Shutdown cancelled.") : L.T("Nessuno spegnimento in corso.", "No shutdown in progress."));
                    if (was) host.Broadcast(L.T("Spegnimento annullato da ", "Shutdown cancelled by ") + secrets.NameOf(m.FromId) + ".", m.FromId);
                    host.Audit(m.FromId, L.T("/annulla", "/cancel"), was ? L.T("annullato", "cancelled") : L.T("niente da annullare", "nothing to cancel"));
                    break;
                }

                case "stop":
                    host.Audit(m.FromId, "/stop", L.T("controllo remoto disattivato", "remote control turned off"));
                    host.SendText(chat, L.T("Controllo remoto disattivato. Si riattiva solo dal PC.", "Remote control turned off. It can only be turned back on from the PC."));
                    host.Broadcast(L.T("Controllo remoto disattivato da ", "Remote control turned off by ") + secrets.NameOf(m.FromId) + L.T(". Si riattiva solo dal PC.", ". It can only be turned back on from the PC."), m.FromId);
                    host.DisableRemote();
                    break;

                case "abbina":
                    host.SendText(chat, L.T("Questo telefono è già abbinato.", "This phone is already paired."));
                    break;

                case "telefoni":
                {
                    StringBuilder sb = new StringBuilder(L.T("📱 Telefoni abbinati a ", "📱 Phones paired with ") + Environment.MachineName + ":");
                    foreach (PairedPhone p in secrets.Phones)
                        sb.Append("\n• ").Append(p.Label).Append(p.Id == m.FromId ? L.T(" (questo)", " (this one)") : "")
                          .Append(p.HasTotp ? "" : L.T(" – codice non configurato", " – code not set up"));
                    host.SendText(chat, sb.ToString());
                    host.Audit(m.FromId, L.T("/telefoni", "/phones"), "ok");
                    break;
                }

                default:
                    host.SendText(chat, L.T("Comando sconosciuto. /aiuto per l'elenco.", "Unknown command. /help for the list."));
                    host.Audit(m.FromId, Cmd(cmd), L.T("sconosciuto", "unknown"));
                    break;
            }
        }

        /// <summary>
        /// /foto [n]: foto dalla webcam (n = numero della fotocamera, se ce n'è più di una). Codice dell'Authenticator se
        /// richiesto (predefinito sì). Una alla volta e al massimo una ogni <see cref="ScreenshotIntervalSeconds"/> secondi.
        /// </summary>
        private void TakePhoto(IncomingMessage m, List<string> args, DateTime nowUtc)
        {
            long chat = m.ChatId;
            long from = m.FromId;
            string usage = L.T("Uso: /foto", "Usage: /photo") + (secrets.CodePhoto ? " 123456" : "") +
                           L.T(" [n], n = numero della fotocamera (da 1 a ", " [n], n = camera number (1 to ") + MaxCameras + ").";
            if (!CheckCode(m, "foto", args, nowUtc)) return;   // toglie il codice dagli argomenti
            int camera = 0;
            if (args.Count > 1)
            {
                host.SendText(chat, usage);
                host.Audit(from, Cmd("foto"), L.T("argomenti non validi", "invalid arguments"));
                return;
            }
            if (args.Count == 1)
            {
                int n;
                if (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > MaxCameras)
                {
                    host.SendText(chat, usage);
                    host.Audit(from, Cmd("foto"), L.T("argomenti non validi", "invalid arguments"));
                    return;
                }
                camera = n;
            }
            if (photoBusy)
            {
                host.SendText(chat, L.T("📷 Sto già scattando una foto: arriva tra poco.", "📷 Already taking a photo: it is on its way."));
                host.Audit(from, Cmd("foto"), L.T("foto già in corso", "photo already in progress"));
                return;
            }
            if ((nowUtc - lastPhotoUtc).TotalSeconds < ScreenshotIntervalSeconds)
            {
                host.SendText(chat, L.T("Una foto ogni ", "One photo every ") + ScreenshotIntervalSeconds + L.T(" secondi: riprova tra poco.", " seconds: try again shortly."));
                host.Audit(from, Cmd("foto"), L.T("troppo presto", "too soon"));
                return;
            }
            lastPhotoUtc = nowUtc;
            photoBusy = true;
            string what = Cmd("foto") + (camera > 0 ? " " + camera : "");
            host.CapturePhoto(camera, delegate(ScreenshotResult shot)
            {
                photoBusy = false;
                if (!secrets.IsPaired(from)) { host.Audit(from, what, L.T("telefono rimosso nel frattempo, foto scartata", "phone removed meanwhile, photo discarded")); return; }
                if (shot == null || shot.Error != null || shot.Data == null)
                {
                    string err = shot != null && shot.Error != null ? shot.Error : L.T("📷 Foto non riuscita.", "📷 Photo failed.");
                    host.SendText(chat, err);
                    host.Audit(from, what, err);
                    return;
                }
                shot.Caption = PhotoCaption(shot.Caption, shot.Dark, DateTime.UtcNow);
                host.SendScreenshot(chat, shot);
                host.NotifyLocal("foto");
                host.Audit(from, what, L.T("inviata", "sent"));
            });
        }

        /// <summary>Didascalia della foto: "📷 Integrated Camera, 15:02" più un avviso se è quasi nera.</summary>
        public static string PhotoCaption(string camera, bool dark, DateTime nowUtc)
        {
            string c = "📷 " + (string.IsNullOrEmpty(camera) ? L.T("Webcam", "Webcam") : camera) + ", " + LocalTime(nowUtc);
            if (dark) c += "\n" + L.T("L'immagine è quasi nera: l'obiettivo è coperto o la fotocamera è spenta con il tasto della privacy?",
                                      "The image is almost black: is the lens covered or the camera off with the privacy switch?");
            return c;
        }

        /// <summary>Per la risposta a /sblocca: "/foto, /blocca e /spegni non chiedono il codice".</summary>
        private string CodeCommandsText()
        {
            List<string> c = new List<string>();
            foreach (string n in new[] { "screenshot", "app", "foto", "blocca", "spegni" })
                if (secrets.RequiresCode(n)) c.Add(Cmd(n));
            if (c.Count == 0) return L.T("i comandi con codice non lo chiedono", "commands with a code do not ask for it");
            if (c.Count == 1) return c[0] + L.T(" non chiede il codice", " does not ask for the code");
            return string.Join(", ", c.GetRange(0, c.Count - 1).ToArray()) + L.T(" e ", " and ") + c[c.Count - 1] +
                   L.T(" non chiedono il codice", " do not ask for the code");
        }

        public string HelpText()
        {
            // nomi dei comandi nella lingua scelta; funzionano comunque anche quelli dell'altra lingua
            string code = L.T(" CODICE", " CODE"), codeTag = L.T(" (codice)", " (code)");
            StringBuilder sb = new StringBuilder();
            sb.Append(L.T("WinUsage su ", "WinUsage on ")).Append(Environment.MachineName).Append("\n\n");
            sb.Append(Cmd("stato")).Append(L.T(" – tempi di utilizzo", " – usage times")).Append('\n');
            sb.Append(Cmd("app")).Append(L.T(" – applicazione in uso", " – app in use")).Append(secrets.CodeApp ? codeTag : "").Append('\n');
            sb.Append(Cmd("screenshot")).Append(L.T(" [n] [hd] – cattura dello schermo", " [n] [hd] – screen capture"))
              .Append(secrets.CodeScreenshot ? codeTag : "").Append('\n');
            sb.Append(Cmd("foto")).Append(secrets.CodePhoto ? code : "").Append(L.T(" [n] – foto dalla webcam", " [n] – webcam photo")).Append('\n');
            sb.Append(Cmd("chat")).Append(L.T(" – apre la chat sul PC", " – opens the chat on the PC"))
              .Append(secrets.Phones.Count > 1 ? L.T(" (la leggono tutti i telefoni abbinati)", " (all paired phones read it)") : "").Append('\n');
            sb.Append(Cmd("blocca")).Append(secrets.CodeLock ? code : "").Append(L.T(" – blocca il PC", " – locks the PC")).Append('\n');
            sb.Append(Cmd("spegni")).Append(secrets.CodeShutdown ? code : "")
              .Append(L.T(" [minuti] – spegne dopo un conto alla rovescia (", " [minutes] – shuts down after a countdown ("))
              .Append(settings.ShutdownDefaultMinutes).Append(" min)\n");
            sb.Append(Cmd("annulla")).Append(L.T(" – ferma lo spegnimento", " – stops the shutdown")).Append('\n');
            sb.Append(Cmd("sblocca")).Append(code).Append(L.T(" – niente codice per ", " – no code needed for ")).Append(SessionMinutes)
              .Append(L.T(" minuti", " minutes")).Append('\n');
            sb.Append(Cmd("telefoni")).Append(L.T(" – telefoni abbinati", " – paired phones")).Append('\n');
            sb.Append(Cmd("stop")).Append(L.T(" – disattiva il controllo remoto per tutti (si riattiva dal PC)",
                                              " – turns remote control off for everyone (turn it back on from the PC)")).Append("\n\n");
            sb.Append(L.T("Il testo senza / va nella chat del PC.", "Text without / goes to the PC chat.")).Append('\n');
            sb.Append(L.T("Il PC non ti chiederà mai codici o password in chat: se succede, non è lui.",
                          "The PC will never ask you for codes or passwords in chat: if that happens, it is not the PC."));
            return sb.ToString();
        }

        /// <summary>Testo di /app. Non contiene mai il titolo della finestra (decisione di design).</summary>
        public static string FormatForeground(ForegroundInfo fi, DateTime nowUtc)
        {
            if (fi.State == SessionState.Locked)
                return L.T("🔒 PC bloccato", "🔒 PC locked") +
                       (fi.LockedSinceUtc > DateTime.MinValue ? L.T(" dalle ", " since ") + LocalTime(fi.LockedSinceUtc) : "") + ".";
            if (fi.State == SessionState.Disconnected)
                return L.T("👥 Un altro utente sta usando il PC (sessione disconnessa).", "👥 Another user is using the PC (session disconnected).");

            StringBuilder sb = new StringBuilder();
            string name = string.IsNullOrEmpty(fi.AppName) ? fi.ProcessName : fi.AppName;
            if (string.IsNullOrEmpty(name)) sb.Append(L.T("🖥️ Nessuna applicazione in primo piano", "🖥️ No app in the foreground"));
            else
            {
                sb.Append(L.T("🖥️ In uso: ", "🖥️ In use: ")).Append(name);
                if (!string.IsNullOrEmpty(fi.ProcessName) && !string.Equals(name, fi.ProcessName, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(fi.ProcessName).Append(".exe)");
                if (fi.SinceUtc > DateTime.MinValue) sb.Append(L.T(" da ", " for ")).Append(Fmt.Long((nowUtc - fi.SinceUtc).TotalSeconds));
            }
            string yes = L.T("sì", "yes"), no = "no";
            sb.Append(L.T("\n⌨️ Ultimo input: ", "\n⌨️ Last input: ")).Append(Ago(fi.IdleSeconds));
            sb.Append(L.T("\n🎮 Gioco Steam in corso: ", "\n🎮 Steam game running: ")).Append(fi.SteamGame ? yes : no);
            sb.Append(L.T("\n🖵 Schermo intero: ", "\n🖵 Full screen: ")).Append(fi.Fullscreen ? yes : no);
            return sb.ToString();
        }

        public static string Ago(double seconds)
        {
            if (seconds < 60) return Math.Max(0, (int)seconds) + L.T(" s fa", " s ago");
            return Fmt.Long(seconds) + L.T(" fa", " ago");
        }

        public static string LocalTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>"alle 23:40", "ieri alle 23:40", "il 12/11 alle 23:40" rispetto a <paramref name="nowUtc"/> (in inglese "at 23:40"...).</summary>
        public static string When(DateTime utc, DateTime nowUtc)
        {
            DateTime local = utc.ToLocalTime(), now = nowUtc.ToLocalTime();
            string hm = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (local.Date == now.Date) return L.T("alle ", "at ") + hm;
            if (local.Date == now.Date.AddDays(-1)) return L.T("ieri alle ", "yesterday at ") + hm;
            return L.T("il ", "on ") + local.ToString("dd/MM", CultureInfo.InvariantCulture) + L.T(" alle ", " at ") + hm;
        }
    }

    /// <summary>Testi dei messaggi di accensione e spegnimento.</summary>
    internal static class PowerMessages
    {
        public static string PcName { get { return Environment.MachineName; } }

        public static string On(DateTime utc, bool autostart, DateTime nowUtc)
        {
            return autostart
                ? L.T("🟢 PC acceso — ", "🟢 PC on — ") + PcName + L.T(", accesso ", ", signed in ") + RemoteController.When(utc, nowUtc)
                : L.T("🟢 WinUsage avviato — ", "🟢 WinUsage started — ") + PcName + " " + RemoteController.When(utc, nowUtc);
        }

        public static string Off(DateTime utc, bool logoff, double sessionSeconds, long[] today, DateTime nowUtc)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(logoff ? L.T("🔴 Disconnessione — ", "🔴 Signed out — ") : L.T("🔴 PC spento o riavviato — ", "🔴 PC shut down or restarted — "))
              .Append(PcName).Append(' ').Append(RemoteController.When(utc, nowUtc));
            sb.Append(L.T(" · sessione ", " · session ")).Append(Fmt.Compact(sessionSeconds));
            sb.Append(L.T(" · oggi: ", " · today: "));
            string[] labels = TimerIds.Labels;
            for (int i = 0; i < TimerIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(labels[i]).Append(' ').Append(Fmt.Compact(today[i]));
            }
            return sb.ToString();
        }

        public static string AppClosed(DateTime utc, DateTime nowUtc)
        {
            return L.T("⚪ WinUsage chiuso — ", "⚪ WinUsage closed — ") + PcName + " " + RemoteController.When(utc, nowUtc) +
                   L.T(" (uscita dall'app o aggiornamento): il PC resta acceso senza controllo remoto.",
                       " (app exited or updated): the PC stays on without remote control.");
        }

        public static string Crash(DateTime lastSeenUtc, DateTime nowUtc)
        {
            return L.T("⚠️ Spegnimento improvviso — ", "⚠️ Sudden shutdown — ") + PcName + L.T(": ultimo segnale ", ": last signal ") +
                   RemoteController.When(lastSeenUtc, nowUtc) +
                   L.T(" (±30 s). Mancanza di corrente, blocco o spegnimento forzato.", " (±30 s). Power cut, freeze or forced shutdown.");
        }

        public static string Late(string text, DateTime createdUtc, DateTime nowUtc)
        {
            return (nowUtc - createdUtc).TotalMinutes >= 2 ? text + L.T(" (notificato in ritardo)", " (sent late)") : text;
        }
    }
}
