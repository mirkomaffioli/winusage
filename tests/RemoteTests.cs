using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Collaudo del controllo remoto (logica, senza rete né finestre): vedi run-tests.sh.
namespace WinUsage.Tests
{
    /// <summary>Host finto: registra tutto quello che il controller chiede.</summary>
    internal sealed class FakeHost : IRemoteHost
    {
        public readonly List<string> Sent = new List<string>();
        public readonly List<long> SentTo = new List<long>();
        public readonly List<string> Broadcasts = new List<string>();
        public readonly List<long> BroadcastExcept = new List<long>();
        public readonly List<long> ChatFrom = new List<long>();
        public long LastStepPhone, ChatRequestFrom;
        public readonly List<string> Audits = new List<string>();
        public readonly List<string> Alerts = new List<string>();
        public readonly List<string> Chats = new List<string>();
        public readonly List<string> Local = new List<string>();
        public int Screenshots, Locks, ChatOpened, Disabled, SecretsSaved;
        public long LastStep = -1;
        public bool ConfirmAnswer = true;
        public int ShutdownMinutes = -1;
        public bool Shutdown;
        public string ScreenshotError;
        public ForegroundInfo Foreground = new ForegroundInfo();

        public void SendText(long chatId, string text) { Sent.Add(text); SentTo.Add(chatId); }
        public void Broadcast(string text, long exceptId) { Broadcasts.Add(text); BroadcastExcept.Add(exceptId); }
        public void SendScreenshot(long chatId, ScreenshotResult shot) { Screenshots++; LastImage = shot; LastImageTo = chatId; }
        public ScreenshotResult LastImage;
        public long LastImageTo;
        public int Photos, PhotoCamera = -1;
        public string PhotoError;
        public bool PhotoDeferred;                     // true: la foto "arriva dopo" (CompletePhoto)
        public Action<ScreenshotResult> PendingPhoto;
        public void CapturePhoto(int camera, Action<ScreenshotResult> done)
        {
            Photos++;
            PhotoCamera = camera;
            if (PhotoDeferred) { PendingPhoto = done; return; }
            done(PhotoResult());
        }
        public ScreenshotResult PhotoResult()
        {
            ScreenshotResult r = new ScreenshotResult();
            r.Name = "foto";
            r.Error = PhotoError;
            if (r.Error == null) { r.Data = new byte[] { 0xFF, 0xD8, 0xFF }; r.Caption = "Integrated Camera"; }
            return r;
        }
        public void CompletePhoto() { Action<ScreenshotResult> d = PendingPhoto; PendingPhoto = null; d(PhotoResult()); }
        public ScreenshotResult CaptureScreenshot(int monitor, bool hd)
        {
            ScreenshotResult r = new ScreenshotResult();
            r.Error = ScreenshotError;
            if (r.Error == null) r.Data = new byte[] { 1, 2, 3 };
            return r;
        }
        public ForegroundInfo GetForegroundInfo() { return Foreground; }
        public LockResult Lock() { Locks++; return LockResult.Locked; }
        public DateTime StartShutdown(int minutes) { ShutdownMinutes = minutes; Shutdown = true; return DateTime.Now.AddMinutes(minutes); }
        public bool CancelShutdown(bool fromRemote) { bool was = Shutdown; Shutdown = false; return was; }
        public bool ShutdownActive { get { return Shutdown; } }
        public void OpenChat(bool requestedRemotely, long fromId) { ChatOpened++; ChatRequestFrom = fromId; }
        public bool Locked;
        public bool PcLocked { get { return Locked; } }
        public void DeliverChat(string text, DateTime sentUtc, long fromId) { Chats.Add(text); ChatFrom.Add(fromId); }
        public void DisableRemote() { Disabled++; }
        public void ConfirmPairing(long userId, string name, Action<bool> done) { done(ConfirmAnswer); }
        public void NotifyLocal(string kind) { Local.Add(kind); }
        public void SecurityAlert(string text) { Alerts.Add(text); }
        public void Audit(long senderId, string command, string result) { Audits.Add(command + " -> " + result); }
        public void SaveSecrets() { SecretsSaved++; }
        public void SaveTotpStep(long phoneId, long step) { LastStep = step; LastStepPhone = phoneId; }
        public string StatusText() { return "stato"; }

        public string Last { get { return Sent.Count > 0 ? Sent[Sent.Count - 1] : ""; } }
        public string LastBroadcast { get { return Broadcasts.Count > 0 ? Broadcasts[Broadcasts.Count - 1] : ""; } }
    }

    internal static class RemoteTests
    {
        private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("chiave-di-prova-dei-test");

        private static IncomingMessage Msg(long from, string text, DateTime date)
        {
            IncomingMessage m = new IncomingMessage();
            m.ChatId = from;
            m.FromId = from;
            m.ChatType = "private";
            m.FromName = "Mirko";
            m.Text = text;
            m.DateUtc = date;
            return m;
        }

        private static void Eq(object expected, object actual, string what) { Tests.Eq(expected, actual, what); }

        public static void Run(string dir)
        {
            Console.WriteLine("[R1] Base32 e TOTP (RFC 6238)");
            Eq("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")), "Base32 foobar");
            Eq("foobar", Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi")), "Base32 decodifica tollerante");
            Eq(null, Base32.Decode("ABC1"), "Base32 carattere non valido");
            byte[] rfc = Encoding.ASCII.GetBytes("12345678901234567890");
            long[] times = { 59, 1111111109, 1111111111, 1234567890, 2000000000, 20000000000 };
            string[] expected = { "94287082", "07081804", "14050471", "89005924", "69279037", "65353130" };
            for (int i = 0; i < times.Length; i++)
                Eq(expected[i], Totp.Code(rfc, times[i] / 30, 8), "RFC 6238 T=" + times[i]);
            DateTime t1 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1111111109);
            Eq("081804", Totp.Code(rfc, Totp.StepOf(t1)), "6 cifre");
            long step = Totp.StepOf(t1);
            Eq(step, Totp.Verify(rfc, "081804", t1, -1), "verifica codice corrente");
            Eq(step, Totp.Verify(rfc, "081804", t1.AddSeconds(30), -1), "accettato il passo precedente (orologio indietro)");
            Eq(-1L, Totp.Verify(rfc, "081804", t1.AddSeconds(90), -1), "rifiutato oltre ±1 passo");
            Eq(-1L, Totp.Verify(rfc, "081804", t1, step), "monouso: stesso passo rifiutato");
            Eq(-1L, Totp.Verify(rfc, "12345", t1, -1), "codice di 5 cifre rifiutato");
            string uri = Totp.ProvisioningUri(Totp.NewSecret(), "DESKTOP-ABC123XYZ");
            Eq(true, uri.StartsWith("otpauth://totp/WinUsage:DESKTOP-AB?secret="), "URI otpauth (" + uri.Length + " car.)");
            Eq(true, uri.Length <= QrCode.Capacity, "l'URI sta nel QR");

            Console.WriteLine("[R2] QR code");
            Eq(21, QrCode.Encode("A").GetLength(0), "versione 1 (21x21)");
            Eq(41, QrCode.Encode(uri).GetLength(0), "URI otpauth: versione 6 (41x41)");
            Eq(null, QrCode.Encode(new string('x', QrCode.Capacity + 1)), "troppo lungo: null");

            Console.WriteLine("[R3] Configurazione cifrata");
            RemoteSecrets.Protect = delegate(byte[] plain)
            {
                byte[] mac;
                using (HMACSHA256 hm = new HMACSHA256(TestKey)) mac = hm.ComputeHash(plain);
                byte[] r = new byte[32 + plain.Length];
                Array.Copy(mac, r, 32);
                Array.Copy(plain, 0, r, 32, plain.Length);
                return r;
            };
            RemoteSecrets.Unprotect = delegate(byte[] cipher)
            {
                byte[] plain = new byte[cipher.Length - 32];
                Array.Copy(cipher, 32, plain, 0, plain.Length);
                byte[] mac;
                using (HMACSHA256 hm = new HMACSHA256(TestKey)) mac = hm.ComputeHash(plain);
                for (int i = 0; i < 32; i++) if (mac[i] != cipher[i]) throw new CryptographicException("alterato");
                return plain;
            };
            Storage st = new Storage(Path.Combine(dir, "remote.db"));
            bool damaged;
            RemoteSecrets def = RemoteSecrets.Load(st, out damaged);
            Eq(false, damaged, "nessuna configurazione: non danneggiata");
            Eq(false, def.Paired, "predefinito: non abbinato");
            Eq(false, def.RequiresCode("screenshot"), "predefinito: screenshot senza codice");
            Eq(false, def.RequiresCode("app"), "predefinito: app senza codice");
            Eq(true, def.RequiresCode("blocca"), "predefinito: blocca con codice");
            Eq(true, def.RequiresCode("spegni"), "predefinito: spegni con codice");
            Eq(true, def.ExclusionSet().Contains("KeePass"), "esclusioni predefinite");
            def.Token = "123456:ABC=def\nzz";
            PairedPhone ph = new PairedPhone();
            ph.Id = 987654321;
            ph.Name = "Mirko (@mirko)";
            ph.TotpSecret = Base32.Encode(rfc);
            ph.TotpVerified = true;
            def.Phones.Add(ph);
            PairedPhone ph2 = new PairedPhone();
            ph2.Id = 55;
            ph2.Name = "Anna";
            def.Phones.Add(ph2);
            def.Save(st);
            Eq(true, st.GetSetting("remote_secure", "").IndexOf("987654321", StringComparison.Ordinal) < 0 &&
                     st.GetSetting("remote_secure", "").IndexOf("ABC", StringComparison.Ordinal) < 0, "nel database token e id non compaiono in chiaro");
            RemoteSecrets back = RemoteSecrets.Load(st, out damaged);
            Eq("123456:ABC=def\nzz", back.Token, "token riletto (anche con = e a capo)");
            Eq(2, back.Phones.Count, "due telefoni riletti");
            Eq(987654321L, back.Phones[0].Id, "primo telefono riletto");
            Eq(true, back.Phones[0].HasTotp && !back.Phones[1].HasTotp, "codice per telefono riletto (solo il primo ce l'ha)");
            Eq("Anna", back.NameOf(55), "nome del secondo telefono");
            Eq("Mirko (@mirko), Anna", back.Names(), "elenco dei nomi");
            RemoteSecrets cl = back.Clone();
            cl.Phones[0].Name = "cambiato"; cl.Phones.RemoveAt(1);
            Eq(true, back.Phones.Count == 2 && back.Phones[0].Name == "Mirko (@mirko)", "la copia non tocca l'originale");

            // formato della 1.4: un solo telefono con user_id, user_name, totp, totp_ok
            System.Text.StringBuilder old = new System.Text.StringBuilder();
            foreach (string[] kv in new[] { new[] { "token", "1:x" }, new[] { "user_id", "4242" }, new[] { "user_name", "Vecchio" },
                                             new[] { "totp", Base32.Encode(rfc) }, new[] { "totp_ok", "1" }, new[] { "code_lock", "1" } })
                old.Append(kv[0]).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(kv[1]))).Append('\n');
            RemoteSecrets legacy = RemoteSecrets.Parse(old.ToString());
            Eq(1, legacy.Phones.Count, "1.4: il telefono abbinato diventa il primo dell'elenco");
            Eq(true, legacy.Phones[0].Id == 4242 && legacy.Phones[0].Name == "Vecchio" && legacy.Phones[0].HasTotp,
               "1.4: id, nome e codice Authenticator passano al telefono");
            Eq("1:x", legacy.Token, "1.4: token riletto");
            RemoteSecrets noUser = RemoteSecrets.Parse("token=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("1:x")) + "\n");
            Eq(false, noUser.Paired, "1.4 senza telefono: nessun telefono");
            string blob = st.GetSetting("remote_secure", "");
            byte[] raw = Convert.FromBase64String(blob);
            raw[raw.Length - 3] ^= 0x01;   // qualcuno cambia un byte (ad esempio l'id utente)
            st.SetSetting("remote_secure", Convert.ToBase64String(raw));
            RemoteSecrets bad = RemoteSecrets.Load(st, out damaged);
            Eq(true, damaged, "blob alterato: configurazione danneggiata");
            Eq(false, bad.Paired, "blob alterato: nessun utente abbinato");

            RemoteSettings rs = RemoteSettings.Load(st);
            Eq(false, rs.Enabled, "remoto spento di default");
            Eq(5, rs.ShutdownDefaultMinutes, "spegnimento predefinito 5 minuti");
            Eq(true, rs.NotifyScreenshot && rs.NotifyCountdown && rs.MsgPowerOn && rs.MsgPowerOff, "notifiche e messaggi attivi di default");
            Eq(false, rs.MsgSleep || rs.MsgLock, "standby e blocco disattivati di default");
            rs.ShutdownDefaultMinutes = 500; rs.NotifyApp = false; rs.Save(st);
            Eq(RemoteSettings.MaxShutdownMinutes, RemoteSettings.Load(st).ShutdownDefaultMinutes, "minuti limitati a 120");
            Eq(false, RemoteSettings.Load(st).NotifyApp, "notifica app salvata");
            Eq(true, rs.ChatHistory, "cronologia della chat attiva di default");
            rs.ChatHistory = false; rs.Save(st);
            Eq(false, RemoteSettings.Load(st).ChatHistory, "cronologia della chat disattivata salvata");

            ChatHistory ch = new ChatHistory();
            DateTime ct = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
            ch.Add("ciao", false, ct);
            ch.Add("ciao a te", true, ct.AddSeconds(5));
            List<ChatEntry> snap = ch.Snapshot();
            Eq(2, snap.Count, "cronologia: due messaggi");
            Eq(true, !snap[0].Outgoing && snap[1].Outgoing && snap[1].Text == "ciao a te", "cronologia: ordine e direzione");
            ch.Add("dopo", false, ct.AddSeconds(9));
            Eq(2, snap.Count, "la copia non cambia con i messaggi nuovi");
            for (int i = 0; i < ChatHistory.Max + 20; i++) ch.Add("m" + i, i % 2 == 0, ct.AddSeconds(10 + i));
            Eq(ChatHistory.Max, ch.Count, "cronologia limitata a " + ChatHistory.Max + " messaggi");
            Eq("m" + (ChatHistory.Max + 19), ch.Snapshot()[ChatHistory.Max - 1].Text, "si tengono i più recenti");
            ch.Clear();
            Eq(0, ch.Count, "cronologia cancellata");

            string smile = char.ConvertFromUtf32(0x1F600);                     // 2 caratteri UTF-16
            string longText = new string('a', 1999) + smile + "fine";           // l'emoji cade a cavallo del 2000
            Eq(1999, TextCut.Cut(longText, 2000).Length, "taglio a 2000: l'emoji intera resta fuori");
            Eq(2001, TextCut.Cut(longText, 2001).Length, "taglio a 2001: l'emoji intera resta dentro");
            Eq("ab", TextCut.Cut("ab", 10), "testo corto invariato");
            Eq(1999, TextCut.SafeLength(longText, 0, 2000), "pezzo che non spezza l'emoji");
            Eq(6, TextCut.SafeLength(longText, 1999, 2000), "pezzo successivo: emoji e resto (6 caratteri)");
            string escaped = Uri.EscapeDataString(longText);
            Eq(true, escaped.Contains("%F0%9F%98%80") && escaped.EndsWith("fine"), "emoji codificata in UTF-8");
            Eq(true, escaped == TelegramApi.EscapeLong(longText), "codifica a pezzi con un'emoji sul confine: uguale a quella intera");
            // accorciare con "…": misura finta (ci stanno 20 caratteri UTF-16), perché sotto Mono misurare richiede uno schermo
            string row = "";
            for (int i = 0; i < 30; i++) row += "o" + smile;
            string fit = ColorText.FitWithEllipsis(row, delegate(string probe) { return probe.Length <= 20; });
            Eq(true, fit.EndsWith("\u2026") && fit.Length <= 20, "testo lungo accorciato con i puntini");
            bool lone = false;
            for (int i = 0; i < fit.Length; i++)
                if (char.IsHighSurrogate(fit[i]) && (i + 1 >= fit.Length || !char.IsLowSurrogate(fit[i + 1]))) lone = true;
            Eq(false, lone, "accorciando non resta mezza emoji");
            Eq(20, fit.Length, "si tiene il massimo che ci sta (6 coppie lettera+emoji, una lettera e i puntini)");
            Eq("ciao", ColorText.FitWithEllipsis("ciao", delegate(string probe) { return true; }), "testo che ci sta: invariato");
            string many = "";
            for (int i = 0; i < 1500; i++) many += smile;                       // 3000 caratteri, solo emoji
            Eq(true, Uri.EscapeDataString(many) == TelegramApi.EscapeLong(many), "codifica a pezzi di sole emoji");

            Console.WriteLine("[R4] Coda in uscita, registro, sessioni");
            RemoteStore store = new RemoteStore(st);
            DateTime now = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
            long o1 = store.AddOutbox("off", "PC spento", now.AddDays(-8), 42);
            long o2 = store.AddOutbox("on", "PC acceso", now.AddMinutes(-1), 43);
            List<OutboxItem> ob = store.LoadOutbox(now);
            Eq(1, ob.Count, "messaggi oltre 7 giorni scartati");
            Eq(o2, ob[0].Id, "resta il messaggio recente");
            Eq(43L, ob[0].ChatId, "la coda sa a quale telefono va il messaggio");
            store.RemoveOutbox(o2);
            Eq(0, store.LoadOutbox(now).Count, "coda vuota dopo l'invio");
            Eq(true, o1 > 0, "id della coda");
            store.AddLog(now, 42, "/spegni 5", "ok");
            Eq("/spegni 5", store.RecentLog(10)[0].Command, "registro dei comandi");
            store.AddPendingDelete(42, 100, now.AddMinutes(10));
            store.AddPendingDelete(43, 100, now.AddMinutes(10));   // stesso id di messaggio, chat diversa
            store.AddPendingDelete(42, 101, now.AddHours(-50));
            Eq(0, store.DueDeletes(now).Count, "screenshot da cancellare: non ancora");
            Eq(2, store.DueDeletes(now.AddMinutes(11)).Count, "due screenshot con lo stesso id in due chat (quello oltre 47 ore scartato)");
            store.RemovePendingDelete(43, 100);
            Eq(42L, store.DueDeletes(now.AddMinutes(11))[0].Key, "si toglie solo quello della chat giusta");
            store.Offset = 555;
            st.SetSetting("remote_totp_last_step", "500");   // valore della 1.4, senza telefono
            Eq(500L, store.TotpLastStep(42), "passo TOTP della 1.4 vale per il telefono di allora");
            store.SetTotpLastStep(42, 777);
            Eq(555L, store.Offset, "offset salvato");
            Eq(777L, store.TotpLastStep(42), "ultimo passo TOTP salvato per telefono");
            Eq(500L, store.TotpLastStep(43), "un altro telefono non eredita il passo del primo");

            // database della 1.4: tabelle senza chat_id, migrate all'apertura
            Storage oldDbSt = new Storage(Path.Combine(dir, "remote14.db"));
            oldDbSt.Execute("CREATE TABLE remote_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, " +
                            "kind TEXT NOT NULL, text TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0)");
            oldDbSt.Execute("CREATE TABLE remote_delete(message_id INTEGER PRIMARY KEY, due_utc TEXT NOT NULL)");
            oldDbSt.Execute("INSERT INTO remote_outbox(created_utc, kind, text) VALUES(?, 'off', 'PC spento')", RemoteStore.Iso(now));
            oldDbSt.Execute("INSERT INTO remote_delete(message_id, due_utc) VALUES(77, ?)", RemoteStore.Iso(now));
            RemoteStore migrated = new RemoteStore(oldDbSt);
            List<OutboxItem> mob = migrated.LoadOutbox(now);
            Eq(true, mob.Count == 1 && mob[0].ChatId == 0, "1.4: messaggio in coda conservato, per tutti i telefoni (chat 0)");
            List<KeyValuePair<long, long>> mdel = migrated.DueDeletes(now);
            Eq(true, mdel.Count == 1 && mdel[0].Key == 0 && mdel[0].Value == 77, "1.4: screenshot da cancellare conservato (chat 0)");
            migrated.AddPendingDelete(9, 77, now);
            Eq(2, migrated.DueDeletes(now).Count, "dopo la migrazione la chiave è (chat, messaggio)");
            oldDbSt.Dispose();

            long sA = st.StartSession(now.AddHours(-2));
            st.UpdateSession(sA, now.AddHours(-1), 3600);
            long sB = st.StartSession(now);
            DateTime lastSeen; bool clean;
            Eq(true, st.PreviousSession(sB, out lastSeen, out clean), "sessione precedente trovata");
            Eq(false, clean, "sessione precedente non chiusa: spegnimento improvviso");
            Eq(now.AddHours(-1), lastSeen, "ultimo segnale della sessione precedente");
            st.MarkSessionClean(sA);
            st.PreviousSession(sB, out lastSeen, out clean);
            Eq(true, clean, "sessione chiusa bene");
            st.UpdateSession(sA, now, 3601);
            st.PreviousSession(sB, out lastSeen, out clean);
            Eq(false, clean, "un salvataggio successivo (spegnimento annullato) la riapre");
            st.Dispose();

            string oldDb = Path.Combine(dir, "old.db");
            Db od = new Db(oldDb);
            od.Execute("CREATE TABLE sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, active_seconds INTEGER NOT NULL DEFAULT 0)");
            od.Execute("INSERT INTO sessions(started_utc, last_seen_utc) VALUES('2026-11-20T10:00:00Z', '2026-11-20T12:00:00Z')");
            od.Dispose();
            Storage up = new Storage(oldDb);
            long sC = up.StartSession(now);
            up.PreviousSession(sC, out lastSeen, out clean);
            Eq(true, clean, "aggiornamento dalla 1.2: nessun falso spegnimento improvviso");
            up.Dispose();

            Console.WriteLine("[R5] Lettura dei messaggi di Telegram");
            string json = "[" +
                "{\"update_id\":10,\"message\":{\"message_id\":1,\"date\":1795291200,\"chat\":{\"id\":42,\"type\":\"private\"}," +
                "\"from\":{\"id\":42,\"is_bot\":false,\"first_name\":\"Mirko\",\"username\":\"mirko\"},\"text\":\"/stato\"}}," +
                "{\"update_id\":11,\"message\":{\"message_id\":2,\"date\":1795291201,\"chat\":{\"id\":42,\"type\":\"private\"}," +
                "\"from\":{\"id\":42,\"is_bot\":false,\"first_name\":\"Mirko\"},\"forward_origin\":{\"type\":\"user\"},\"text\":\"/spegni 123456\"}}," +
                "{\"update_id\":12,\"edited_message\":{\"message_id\":3,\"date\":1795291202,\"chat\":{\"id\":42,\"type\":\"private\"},\"text\":\"/spegni\"}}," +
                "{\"update_id\":13,\"message\":{\"message_id\":4,\"date\":1795291203,\"chat\":{\"id\":-100,\"type\":\"group\"}," +
                "\"from\":{\"id\":42,\"is_bot\":false,\"first_name\":\"Mirko\"},\"photo\":[],\"caption\":\"/spegni 123456\"}}]";
            UpdateBatch ub = TelegramApi.ParseUpdates(json, 0);
            Eq(14L, ub.NextOffset, "offset oltre l'ultimo update (anche quelli ignorati)");
            Eq(3, ub.Messages.Count, "il messaggio modificato non diventa un messaggio");
            Eq("Mirko (@mirko)", ub.Messages[0].FromName, "nome del mittente");
            Eq(new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc), ub.Messages[0].DateUtc, "data del messaggio");
            Eq(true, ub.Messages[1].Forwarded, "inoltrato riconosciuto");
            Eq(null, ub.Messages[2].Text, "didascalia di una foto: nessun testo");
            Eq(null, RemoteController.Reject(ub.Messages[0]), "messaggio valido accettato");
            Eq("messaggio inoltrato", RemoteController.Reject(ub.Messages[1]), "inoltrato rifiutato");
            Eq("chat non privata", RemoteController.Reject(ub.Messages[2]), "gruppo rifiutato");
            Eq("Rete: https://api.telegram.org/bot<token>/getMe", TelegramApi.Redact("Rete: https://api.telegram.org/bot12345:SEGRETO/getMe", "12345:SEGRETO"),
               "token tolto dai messaggi di errore");
            Eq("x /bot<token>/y e il /bot", TelegramApi.Redact("x /bot999:ALTRO/y e il /bot", "12345:SEGRETO"), "anche un token diverso");

            string cmd; string[] args;
            RemoteController.ParseCommand("/Spegni@MioBot 123456 10", out cmd, out args);
            Eq("spegni", cmd, "comando con @bot e maiuscole");
            Eq(2, args.Length, "argomenti");
            RemoteController.ParseCommand("ciao!", out cmd, out args);
            Eq(null, cmd, "testo semplice: nessun comando");

            Console.WriteLine("[R6] Controller: abbinamento");
            FakeHost h = new FakeHost();
            RemoteSecrets sec = new RemoteSecrets();
            RemoteSettings set = new RemoteSettings();
            RemoteController c = new RemoteController(h, sec, set, delegate(long id) { return 0; });
            DateTime t = new DateTime(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc);
            c.Process(Msg(42, "/stato", t), t);
            Eq(0, h.Sent.Count, "non abbinato: nessuna risposta a nessuno");
            c.Process(Msg(42, "/abbina AAAA", t), t);
            Eq(0, h.Sent.Count, "abbinamento non aperto: silenzio");
            string code = c.StartPairing(t);
            Eq(RemoteController.PairingCodeLength, code.Length, "codice di abbinamento di 8 caratteri");
            for (int i = 0; i < RemoteController.PairingMaxAttempts; i++) c.Process(Msg(666, "/abbina XXXXXXXX", t), t);
            Eq(false, c.PairingOpen(t), "dopo 5 tentativi errati l'abbinamento si chiude");
            Eq(1, h.Alerts.Count, "avviso di sicurezza per i tentativi");
            code = c.StartPairing(t);
            c.Process(Msg(42, "/abbina " + code.ToLowerInvariant(), t.AddMinutes(6)), t.AddMinutes(6));
            Eq(false, sec.Paired, "codice scaduto dopo 5 minuti");
            code = c.StartPairing(t);
            h.ConfirmAnswer = false;
            c.Process(Msg(42, "/abbina " + code, t), t);
            Eq(false, sec.Paired, "conferma negata sul PC: non abbinato");
            code = c.StartPairing(t);
            h.ConfirmAnswer = true;
            c.Process(Msg(42, "/abbina " + code, t), t);
            Eq(true, sec.IsPaired(42) && sec.Phones.Count == 1, "abbinato dopo la conferma sul PC");
            Eq(0, h.Broadcasts.Count, "primo telefono: nessun altro da avvisare (nessun messaggio agli altri)");
            Eq(1, h.SecretsSaved, "configurazione salvata");

            Console.WriteLine("[R7] Controller: comandi");
            int before = h.Sent.Count;
            c.Process(Msg(666, "/screenshot", t), t);
            Eq(before, h.Sent.Count, "altro mittente: ignorato senza risposta");
            IncomingMessage fwd = Msg(42, "/annulla", t); fwd.Forwarded = true;
            c.Process(fwd, t);
            Eq(before, h.Sent.Count, "inoltrato dal mittente giusto: ignorato");

            c.Process(Msg(42, "/screenshot", t), t);
            Eq(1, h.Screenshots, "screenshot senza codice");
            Eq("screenshot", h.Local[0], "notifica sul PC per lo screenshot");
            c.Process(Msg(42, "/screenshot", t.AddSeconds(5)), t.AddSeconds(5));
            Eq(1, h.Screenshots, "un solo screenshot ogni 10 secondi");
            h.ScreenshotError = "PC bloccato";
            c.Process(Msg(42, "/screenshot", t.AddSeconds(20)), t.AddSeconds(20));
            Eq("PC bloccato", h.Last, "screenshot impossibile: motivo al telefono");
            h.ScreenshotError = null;

            c.Process(Msg(42, "/stato", t.AddMinutes(-10)), t);
            Eq("stato", h.Last, "/stato di 10 minuti fa: eseguito lo stesso (legge soltanto)");
            Eq(true, RemoteController.ExpiresWhenLate("spegni") && RemoteController.ExpiresWhenLate("blocca") &&
                     RemoteController.ExpiresWhenLate("sblocca"), "blocca, spegni e sblocca scadono");
            Eq(false, RemoteController.ExpiresWhenLate("screenshot") || RemoteController.ExpiresWhenLate("app") ||
                      RemoteController.ExpiresWhenLate("chat"), "screenshot, app e chat non scadono");
            c.Process(Msg(42, "/annulla", t.AddMinutes(-10)), t);
            Eq("Nessuno spegnimento in corso.", h.Last, "/annulla vale anche se vecchio");

            c.Process(Msg(42, "/blocca", t), t);
            Eq(true, h.Last.Contains("non è ancora configurato"), "codice non configurato: blocco rifiutato");
            sec.Find(42).TotpSecret = Base32.Encode(rfc);
            sec.Find(42).TotpVerified = true;
            c.Process(Msg(42, "/blocca", t), t);
            Eq(0, h.Locks, "senza codice non blocca");
            Eq(true, h.Last.StartsWith("Serve il codice"), "chiede il codice");
            string good = Totp.Code(rfc, Totp.StepOf(t));
            c.Process(Msg(42, "/blocca " + good, t), t);
            Eq(1, h.Locks, "codice giusto: PC bloccato");
            Eq(Totp.StepOf(t), h.LastStep, "passo del codice salvato");
            Eq(42L, h.LastStepPhone, "passo salvato per il telefono giusto");
            c.Process(Msg(42, "/blocca " + good, t.AddSeconds(5)), t.AddSeconds(5));
            Eq(1, h.Locks, "stesso codice riusato: rifiutato");
            c.Process(Msg(42, "/blocca 000000", t.AddSeconds(6)), t.AddSeconds(6));
            Eq(1, h.Alerts.Count, "due codici errati (il riuso conta): nessun blocco ancora");
            int alerts = h.Alerts.Count;
            DateTime t2 = t.AddMinutes(1);
            c.Process(Msg(42, "/blocca 000002", t2), t2);
            Eq(alerts + 1, h.Alerts.Count, "terzo errore consecutivo: avviso di sicurezza");
            string good2 = Totp.Code(rfc, Totp.StepOf(t2.AddSeconds(30)));
            c.Process(Msg(42, "/blocca " + good2, t2.AddSeconds(30)), t2.AddSeconds(30));
            Eq(1, h.Locks, "blocco di 15 minuti anche con il codice giusto");
            Eq(true, h.Last.Contains("bloccati fino"), "risposta: comandi bloccati");

            DateTime t3 = t2.AddMinutes(16);
            c.Process(Msg(42, "/sblocca " + Totp.Code(rfc, Totp.StepOf(t3)), t3), t3);
            Eq(true, h.Last.Contains("non chiedono il codice"), "sessione di 15 minuti aperta");
            c.Process(Msg(42, "/spegni", t3.AddMinutes(1)), t3.AddMinutes(1));
            Eq(5, h.ShutdownMinutes, "spegni senza codice in sessione, 5 minuti predefiniti");
            c.Process(Msg(42, "/spegni 10", t3.AddMinutes(2)), t3.AddMinutes(2));
            Eq(10, h.ShutdownMinutes, "spegni 10 minuti");
            Eq(true, h.Last.Contains("sostituisce"), "un nuovo /spegni sostituisce il precedente");
            Eq(false, h.Last.Contains("bloccato"), "PC sbloccato: nessun avviso sulle app chiuse");
            h.Locked = true;
            c.Process(Msg(42, "/spegni 10", t3.AddMinutes(2).AddSeconds(5)), t3.AddMinutes(2).AddSeconds(5));
            Eq(true, h.Last.Contains(RemoteController.LockedShutdownNote), "PC bloccato: avviso che le app verranno chiuse");
            h.Locked = false;
            c.Process(Msg(42, "/spegni 10", t3.AddMinutes(1).AddSeconds(-200)), t3.AddMinutes(2).AddSeconds(10));
            Eq(true, h.Last.Contains("scaduto"), "/spegni arrivato in ritardo: scaduto");
            h.ShutdownMinutes = -1;
            c.Process(Msg(42, "/spegni 0", t3.AddMinutes(3)), t3.AddMinutes(3));
            c.Process(Msg(42, "/spegni 121", t3.AddMinutes(3)), t3.AddMinutes(3));
            c.Process(Msg(42, "/spegni abc", t3.AddMinutes(3)), t3.AddMinutes(3));
            c.Process(Msg(42, "/spegni 5 6", t3.AddMinutes(3)), t3.AddMinutes(3));
            Eq(-1, h.ShutdownMinutes, "minuti non validi rifiutati (0, 121, testo, due numeri)");
            c.Process(Msg(42, "/annulla", t3.AddMinutes(4)), t3.AddMinutes(4));
            Eq("Spegnimento annullato.", h.Last, "/annulla");
            DateTime t4 = t3.AddMinutes(20);
            c.Process(Msg(42, "/spegni", t4), t4);
            Eq(-1, h.ShutdownMinutes, "sessione scaduta: di nuovo il codice");

            c.Process(Msg(42, "ciao dal telefono", t4), t4);
            Eq("ciao dal telefono", h.Chats[0], "testo semplice alla chat");
            c.Process(Msg(42, new string('a', 3000), t4), t4);
            Eq(RemoteController.MaxChatLength, h.Chats[1].Length, "messaggi troppo lunghi accorciati");
            c.Process(Msg(42, "/chat", t4), t4);
            Eq(1, h.ChatOpened, "/chat");
            Eq(true, h.Last.Contains("Ti avviso se la aprono o la rifiutano"), "/chat: il telefono sa che è una richiesta");
            c.Process(Msg(42, "/boh", t4), t4);
            Eq(true, h.Last.StartsWith("Comando sconosciuto"), "comando sconosciuto");
            c.Process(Msg(42, "/aiuto", t4), t4);
            Eq(true, h.Last.Contains("non ti chiederà mai codici"), "l'aiuto avverte dei finti messaggi");

            DateTime t5 = t4.AddMinutes(5);
            for (int i = 0; i < 25; i++) c.Process(Msg(42, "/stato", t5), t5);
            Eq(true, h.Audits[h.Audits.Count - 1].Contains("troppi comandi"), "più di 20 comandi al minuto ignorati");
            DateTime t6 = t5.AddMinutes(2);
            c.Process(Msg(42, "/stop", t6), t6);
            Eq(1, h.Disabled, "/stop disattiva il controllo remoto");

            Console.WriteLine("[R7b] Più telefoni");
            byte[] key99 = Encoding.ASCII.GetBytes("abcdefghijabcdefghij");
            DateTime m0 = t6.AddMinutes(30);
            code = c.StartPairing(m0);
            c.Process(Msg(99, "/abbina " + code, m0), m0);
            Eq(true, sec.IsPaired(99) && sec.Phones.Count == 2, "secondo telefono abbinato mentre il primo lo è già");
            Eq(true, h.LastBroadcast.StartsWith("📱 Nuovo telefono abbinato") && h.BroadcastExcept[h.BroadcastExcept.Count - 1] == 99,
               "il primo telefono viene avvisato del nuovo abbinamento");
            c.Process(Msg(99, "/abbina " + code, m0), m0);
            Eq("Questo telefono è già abbinato.", h.Last, "abbinamento già usato");
            c.Process(Msg(7, "/stato", m0), m0);
            Eq(true, h.SentTo[h.SentTo.Count - 1] != 7, "un terzo account non abbinato resta ignorato");

            DateTime m1 = m0.AddMinutes(1);
            c.Process(Msg(99, "/blocca " + Totp.Code(rfc, Totp.StepOf(m1)), m1), m1);
            Eq(true, h.Last.Contains("per questo telefono non è ancora"), "il codice del primo telefono non vale per il secondo");
            sec.Find(99).TotpSecret = Base32.Encode(key99);
            sec.Find(99).TotpVerified = true;
            int locks = h.Locks;
            c.Process(Msg(99, "/blocca " + Totp.Code(rfc, Totp.StepOf(m1)), m1), m1);
            Eq(locks, h.Locks, "codice dell'altro telefono rifiutato");
            c.Process(Msg(99, "/blocca " + Totp.Code(key99, Totp.StepOf(m1)), m1.AddSeconds(1)), m1.AddSeconds(1));
            Eq(locks + 1, h.Locks, "codice del proprio Authenticator accettato");
            Eq(99L, h.LastStepPhone, "passo salvato per il secondo telefono");
            Eq(true, h.LastBroadcast.Contains("Anna ha bloccato") || h.LastBroadcast.Contains("ha bloccato il PC"),
               "gli altri telefoni sanno chi ha bloccato il PC");
            Eq(99L, h.BroadcastExcept[h.BroadcastExcept.Count - 1], "...ma non chi l'ha fatto");
            c.Process(Msg(42, "/blocca " + Totp.Code(rfc, Totp.StepOf(m1)), m1.AddSeconds(2)), m1.AddSeconds(2));
            Eq(locks + 2, h.Locks, "lo stesso passo di tempo è usabile da entrambi (ognuno ha il suo segreto)");

            DateTime m2 = m1.AddMinutes(2);
            for (int i = 0; i < 3; i++) c.Process(Msg(99, "/blocca 00000" + i, m2.AddSeconds(i)), m2.AddSeconds(i));
            Eq(true, h.Last.Contains("bloccati per"), "tre errori dal secondo telefono: bloccato");
            Eq(true, h.Alerts[h.Alerts.Count - 1].Contains("telefono di"), "l'avviso di sicurezza dice quale telefono");
            c.Process(Msg(42, "/blocca " + Totp.Code(rfc, Totp.StepOf(m2.AddSeconds(31))), m2.AddSeconds(31)), m2.AddSeconds(31));
            Eq(locks + 3, h.Locks, "il blocco per codici errati di un telefono non ferma l'altro");

            DateTime m3 = m2.AddMinutes(1);
            c.Process(Msg(42, "/sblocca " + Totp.Code(rfc, Totp.StepOf(m3)), m3), m3);
            h.ShutdownMinutes = -1;
            c.Process(Msg(99, "/spegni", m3.AddSeconds(5)), m3.AddSeconds(5));
            Eq(-1, h.ShutdownMinutes, "la sessione /sblocca del primo telefono non vale per il secondo");
            c.Process(Msg(42, "/spegni", m3.AddSeconds(6)), m3.AddSeconds(6));
            Eq(5, h.ShutdownMinutes, "il primo telefono spegne nella sua sessione");
            Eq(true, h.LastBroadcast.Contains("ha avviato lo spegnimento") && h.BroadcastExcept[h.BroadcastExcept.Count - 1] == 42,
               "gli altri telefoni sanno dello spegnimento");
            c.Process(Msg(99, "/annulla", m3.AddSeconds(7)), m3.AddSeconds(7));
            Eq(true, h.LastBroadcast.StartsWith("Spegnimento annullato da"), "/annulla da un altro telefono: tutti avvisati");

            c.Process(Msg(99, "ciao a tutti", m3.AddSeconds(8)), m3.AddSeconds(8));
            Eq(99L, h.ChatFrom[h.ChatFrom.Count - 1], "la chat sa chi ha scritto");
            c.Process(Msg(99, "/chat", m3.AddSeconds(9)), m3.AddSeconds(9));
            Eq(99L, h.ChatRequestFrom, "la richiesta di chat sa chi la fa");
            c.Process(Msg(42, "/telefoni", m3.AddSeconds(10)), m3.AddSeconds(10));
            Eq(true, h.Last.Contains("(questo)") && h.Last.Split('\n').Length == 3, "/telefoni elenca i due telefoni");
            Eq(true, c.HelpText().Contains("tutti i telefoni"), "l'aiuto dice che la chat la leggono tutti");

            for (long id = 200; sec.Phones.Count < RemoteSecrets.MaxPhones; id++)
            {
                code = c.StartPairing(m3);
                c.Process(Msg(id, "/abbina " + code, m3), m3);
            }
            Eq(RemoteSecrets.MaxPhones, sec.Phones.Count, "fino a " + RemoteSecrets.MaxPhones + " telefoni");
            code = c.StartPairing(m3);
            c.Process(Msg(300, "/abbina " + code, m3), m3);
            Eq(true, !sec.IsPaired(300) && h.Last.Contains("già " + RemoteSecrets.MaxPhones + " telefoni"), "oltre il massimo: rifiutato");

            sec.Phones.Remove(sec.Find(99));
            c.Apply(sec, set);
            int sentBefore = h.Sent.Count;
            c.Process(Msg(99, "/stato", m3.AddMinutes(1)), m3.AddMinutes(1));
            Eq(sentBefore, h.Sent.Count, "telefono rimosso: ignorato");

            Console.WriteLine("[R8] App in uso, conto alla rovescia, messaggi");
            ForegroundInfo fi = new ForegroundInfo();
            fi.AppName = "Google Chrome"; fi.ProcessName = "chrome"; fi.SinceUtc = t; fi.IdleSeconds = 10; fi.SteamGame = true;
            string app = RemoteController.FormatForeground(fi, t.AddMinutes(25));
            Eq(true, app.Contains("Google Chrome (chrome.exe) da 25 minuti"), "app in uso: " + app.Split('\n')[0]);
            Eq(true, app.Contains("10 s fa") && app.Contains("Steam in corso: sì"), "ultimo input e Steam");
            fi.State = SessionState.Locked;
            Eq(true, RemoteController.FormatForeground(fi, t).StartsWith("🔒 PC bloccato"), "app in uso con PC bloccato");

            ShutdownCountdown cd = new ShutdownCountdown();
            cd.Start(5, t);
            Eq(300.0, cd.Remaining(t), "conto alla rovescia di 5 minuti");
            DateTime x = t;
            CountdownEvent ev = CountdownEvent.None;
            for (int i = 0; i < 299; i++) { x = x.AddSeconds(1); ev = cd.Tick(x); }
            Eq(CountdownEvent.None, ev, "a 299 secondi non è ancora scaduto");
            Eq(CountdownEvent.Expired, cd.Tick(x.AddSeconds(1)), "scade a 300 secondi");
            Eq(false, cd.Active, "non più attivo dopo la scadenza");
            cd.Start(5, t);
            cd.Tick(t.AddSeconds(1));
            Eq(CountdownEvent.None, cd.Tick(t.AddSeconds(120)), "timer fermo 2 minuti (PC bloccato): il conto continua");
            Eq(true, cd.Active, "ancora attivo dopo l'interruzione");
            Eq(CountdownEvent.Expired, cd.Tick(t.AddMinutes(30)), "scaduto mentre l'app era ferma: segnalato");
            Eq(25.0 * 60, cd.LateSeconds(t.AddMinutes(30)), "ritardo rispetto alla scadenza");
            cd.Start(5, t);
            Eq(true, cd.CancelForSleep(), "standby vero: annullato");
            Eq(CountdownEvent.None, cd.Tick(t.AddMinutes(30)), "dopo lo standby niente spegnimento");
            Eq(2, TelegramApi.NextBackoff(1), "attesa dopo un errore di rete: 1 -> 2 s");
            Eq(15, TelegramApi.NextBackoff(8), "attesa massima 15 s");
            Eq(15, TelegramApi.NextBackoff(15), "resta a 15 s");
            cd.Start(500, t);
            Eq(120 * 60, cd.TotalSeconds, "massimo 120 minuti");

            DateTime evening = new DateTime(2026, 11, 21, 22, 40, 0, DateTimeKind.Local).ToUniversalTime();
            string off = PowerMessages.Off(evening, false, 3 * 3600 + 12 * 60, new long[] { 4 * 3600 + 5 * 60, 80 * 60, 130 * 60 }, evening);
            Eq(true, off.Contains("alle 22:40") && off.Contains("sessione 3h 12m") && off.Contains("Steam 2h 10m"), "messaggio di spegnimento: " + off);
            Eq(true, PowerMessages.Crash(evening, evening.AddHours(10)).Contains("ieri alle 22:40") ||
                     PowerMessages.Crash(evening, evening.AddHours(10)).Contains("alle 22:40"), "spegnimento improvviso con l'ultimo segnale");
            Eq(true, PowerMessages.On(evening, true, evening).StartsWith("🟢 PC acceso"), "accensione con avvio automatico");
            Eq(true, PowerMessages.On(evening, false, evening).StartsWith("🟢 WinUsage avviato"), "avvio a mano");
            Eq(true, PowerMessages.Late("x", evening, evening.AddHours(9)).EndsWith("(notificato in ritardo)"), "notificato in ritardo");
            Eq("x", PowerMessages.Late("x", evening, evening.AddSeconds(30)), "non in ritardo se appena creato");

            Console.WriteLine("[R9] Font pixel");
            Eq("PERCHE' E' COSI'", PixelFont.Normalize("Perché è così"), "accenti in maiuscolo con apostrofo");
            Eq(true, PixelFont.CanDraw("Bloccato alle 15:02"), "testo disegnabile");
            Eq(false, PixelFont.CanDraw("ciao 😀"), "emoji non disegnabile");
            Eq(PixelFont.Measure("E'", 1), PixelFont.Measure("È", 1), "misura dopo la normalizzazione");

            Console.WriteLine("[R10] Lingua inglese");
            System.Globalization.CultureInfo itCulture = new System.Globalization.CultureInfo("it-IT");
            System.Globalization.CultureInfo enCulture = new System.Globalization.CultureInfo("en-US");
            System.Globalization.CultureInfo deCulture = new System.Globalization.CultureInfo("de-DE");
            Eq(false, L.ResolveEnglish(L.Auto, itCulture), "automatica con Windows in italiano: italiano");
            Eq(true, L.ResolveEnglish(L.Auto, enCulture), "automatica con Windows in inglese: inglese");
            Eq(true, L.ResolveEnglish(L.Auto, deCulture), "automatica con Windows in un'altra lingua: inglese");
            Eq(false, L.ResolveEnglish(L.Italian, enCulture), "scelta manuale italiano vince su Windows");
            Eq(true, L.ResolveEnglish(L.English, itCulture), "scelta manuale inglese vince su Windows");
            Eq(L.Auto, L.Choice(null), "senza impostazione: automatica");

            string ec; string[] ea;
            RemoteController.ParseCommand("/lock 123456", out ec, out ea);
            Eq("blocca", ec, "/lock = /blocca");
            RemoteController.ParseCommand("/shutdown@MioBot 10", out ec, out ea);
            Eq("spegni", ec, "/shutdown = /spegni (anche con il nome del bot)");
            RemoteController.ParseCommand("/Cancel", out ec, out ea);
            Eq("annulla", ec, "/cancel = /annulla");
            foreach (string[] pair in new[] { new[] { "status", "stato" }, new[] { "unlock", "sblocca" }, new[] { "phones", "telefoni" },
                                               new[] { "pair", "abbina" }, new[] { "help", "aiuto" } })
                Eq(pair[1], RemoteController.Canonical(pair[0]), "/" + pair[0] + " = /" + pair[1]);
            Eq("blocca", RemoteController.Canonical("blocca"), "i nomi italiani restano validi");
            Eq("/blocca", RemoteController.Cmd("blocca"), "nome mostrato in italiano");

            L.En = true;
            try
            {
                Eq("/lock", RemoteController.Cmd("blocca"), "nome mostrato in inglese");
                Eq("1 hour and 5 minutes", Fmt.Long(3900), "durata in inglese");
                Eq("2 HRS 30 MIN", Fmt.ToastTime(9000), "durata del toast in inglese");
                Eq("System", TimerIds.Labels[0], "nome del timer in inglese");
                Eq(true, RemoteController.When(evening, evening).StartsWith("at "), "ora in inglese");
                Eq(true, RemoteController.When(evening.AddDays(-1), evening).StartsWith("yesterday at "), "ieri in inglese");
                Eq(true, PowerMessages.On(evening, true, evening).StartsWith("🟢 PC on — "), "accensione in inglese");
                Eq(true, PowerMessages.Late("x", evening, evening.AddHours(9)).EndsWith("(sent late)"), "in ritardo in inglese");

                FakeHost eh = new FakeHost();
                RemoteSecrets es = new RemoteSecrets();
                PairedPhone ep = new PairedPhone(); ep.Id = 42; ep.Name = "Mirko"; es.Phones.Add(ep);
                RemoteController enc = new RemoteController(eh, es, new RemoteSettings(), delegate(long id) { return 0; });
                DateTime et = new DateTime(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);
                enc.Process(Msg(42, "/help", et), et);
                Eq(true, eh.Last.Contains("/shutdown") && eh.Last.Contains("/lock") && !eh.Last.Contains("/spegni"),
                   "l'aiuto in inglese mostra i comandi inglesi");
                enc.Process(Msg(42, "/lock", et), et);
                Eq(true, eh.Last.StartsWith("The Authenticator code for this phone") || eh.Last.Contains("not yet"),
                   "risposta in inglese: " + eh.Last.Split('\n')[0]);
                enc.Process(Msg(42, "/blocca", et), et);
                Eq(true, eh.Last.Contains("not yet"), "anche il comando italiano risponde in inglese");
                enc.Process(Msg(42, "/cancel", et), et);
                Eq("No shutdown in progress.", eh.Last, "/cancel in inglese");
                enc.Process(Msg(42, "/nope", et), et);
                Eq("Unknown command. /help for the list.", eh.Last, "comando sconosciuto in inglese");
                Eq(true, RemoteController.LockedShutdownNote.StartsWith("🔒 The PC is locked"), "avviso PC bloccato in inglese");
            }
            finally { L.En = false; }
            Eq("1 ora e 5 minuti", Fmt.Long(3900), "di nuovo in italiano");

            PhotoTests();
            NetworkTests();
        }

        /// <summary>[R12] /foto (1.7): foto dalla webcam con il codice dell'Authenticator, configurabile.</summary>
        private static void PhotoTests()
        {
            Console.WriteLine("[R12] /foto dalla webcam");
            RemoteSecrets def = new RemoteSecrets();
            Eq(true, def.RequiresCode("foto"), "predefinito: /foto con codice");
            RemoteSecrets off = new RemoteSecrets();
            off.CodePhoto = false;
            Eq(false, RemoteSecrets.Parse(off.Serialize()).CodePhoto, "codice di /foto disattivato: salvato e riletto");
            string old = def.Serialize().Replace("code_photo=", "x_old=");   // blob fino alla 1.6, senza la voce
            Eq(true, RemoteSecrets.Parse(old).CodePhoto, "configurazione della 1.6: /foto con codice");
            Eq(true, new RemoteSettings().NotifyPhoto, "predefinito: notifica della foto sul PC");
            Eq("foto", RemoteController.Canonical("photo"), "/photo = /foto");
            Eq("foto", RemoteController.Canonical("webcam"), "/webcam = /foto");
            Eq("/foto", RemoteController.Cmd("foto"), "nome mostrato in italiano");
            Eq(true, RemoteController.ExpiresWhenLate("foto"), "/foto in ritardo scade");

            byte[] key = Encoding.ASCII.GetBytes("12345678901234567890");
            FakeHost h = new FakeHost();
            RemoteSecrets sec = new RemoteSecrets();
            PairedPhone p = new PairedPhone(); p.Id = 42; p.Name = "Mirko"; p.TotpSecret = Base32.Encode(key); p.TotpVerified = true;
            PairedPhone p2 = new PairedPhone(); p2.Id = 77; p2.Name = "Anna";
            sec.Phones.Add(p); sec.Phones.Add(p2);
            RemoteController c = new RemoteController(h, sec, new RemoteSettings(), delegate(long id) { return 0; });
            DateTime t = new DateTime(2026, 11, 26, 17, 0, 0, DateTimeKind.Utc);

            c.Process(Msg(42, "/aiuto", t), t);
            Eq(true, h.Last.Contains("/foto CODICE [n]"), "l'aiuto mostra /foto con il codice");
            c.Process(Msg(42, "/foto", t), t);
            Eq(0, h.Photos, "senza codice nessuna foto");
            Eq(true, h.Last.StartsWith("Serve il codice") && h.Last.Contains("/foto 123456"), "chiede il codice: " + h.Last.Split('\n')[0]);
            c.Process(Msg(42, "/foto 000000", t), t);
            Eq(0, h.Photos, "codice errato: nessuna foto");
            c.Process(Msg(77, "/foto " + Totp.Code(key, Totp.StepOf(t)), t), t);
            Eq(0, h.Photos, "telefono senza codice configurato: nessuna foto");
            Eq(true, h.Last.Contains("non è ancora"), "risposta: codice non configurato per quel telefono");

            DateTime t1 = t.AddSeconds(30);
            c.Process(Msg(42, "/foto " + Totp.Code(key, Totp.StepOf(t1)), t1), t1);
            Eq(1, h.Photos, "codice giusto: foto scattata");
            Eq(0, h.PhotoCamera, "senza numero: la prima fotocamera");
            Eq(1, h.Screenshots, "foto inviata");
            Eq(42L, h.LastImageTo, "foto inviata solo a chi l'ha chiesta");
            Eq("foto", h.LastImage.Name, "nome del file: foto-...");
            Eq(true, h.LastImage.Caption.StartsWith("📷 Integrated Camera, "), "didascalia con fotocamera e ora: " + h.LastImage.Caption);
            Eq(true, h.Local.Contains("foto"), "notifica sul PC");
            Eq(0, h.Broadcasts.Count, "agli altri telefoni non si dice niente (come lo screenshot)");
            Eq(true, h.Audits[h.Audits.Count - 1].StartsWith("/foto -> "), "nel registro senza il codice: " + h.Audits[h.Audits.Count - 1]);

            DateTime t3 = t1.AddSeconds(60);
            c.Process(Msg(42, "/sblocca " + Totp.Code(key, Totp.StepOf(t3)), t3), t3);
            Eq(true, h.Last.Contains("/foto, /blocca e /spegni non chiedono il codice"), "sessione: /foto tra i comandi senza codice");
            int before = h.Photos;
            c.Process(Msg(42, "/foto 2", t3.AddSeconds(1)), t3.AddSeconds(1));
            Eq(before + 1, h.Photos, "in sessione senza codice");
            Eq(2, h.PhotoCamera, "/foto 2: seconda fotocamera");
            c.Process(Msg(42, "/foto", t3.AddSeconds(3)), t3.AddSeconds(3));
            Eq(before + 1, h.Photos, "una foto ogni 10 secondi");
            Eq(true, h.Last.StartsWith("Una foto ogni 10 secondi"), "risposta: troppo presto");
            foreach (string bad in new[] { "/foto 0", "/foto 10", "/foto abc", "/foto 1 2" })
                c.Process(Msg(42, bad, t3.AddSeconds(20)), t3.AddSeconds(20));
            Eq(before + 1, h.Photos, "numeri di fotocamera non validi rifiutati (0, 10, testo, due numeri)");
            Eq(true, h.Last.StartsWith("Uso: /foto 123456 [n]"), "uso spiegato: " + h.Last);

            h.PhotoDeferred = true;
            DateTime t4 = t3.AddSeconds(40);
            c.Process(Msg(42, "/photo", t4), t4);
            Eq(before + 2, h.Photos, "/photo in inglese");
            c.Process(Msg(42, "/foto", t4.AddSeconds(11)), t4.AddSeconds(11));
            Eq(before + 2, h.Photos, "una foto alla volta");
            Eq(true, h.Last.Contains("Sto già scattando"), "risposta: foto in corso");
            int shots = h.Screenshots;
            h.CompletePhoto();
            Eq(shots + 1, h.Screenshots, "la foto arriva dopo e viene inviata");
            c.Process(Msg(42, "/foto", t4.AddSeconds(30)), t4.AddSeconds(30));
            sec.Phones.Remove(p);   // telefono tolto dal PC mentre la fotocamera lavora
            h.CompletePhoto();
            Eq(shots + 1, h.Screenshots, "telefono rimosso nel frattempo: foto scartata");
            sec.Phones.Insert(0, p);
            h.PhotoDeferred = false;

            h.PhotoError = "📷 errore di prova";
            c.Process(Msg(42, "/foto", t4.AddSeconds(50)), t4.AddSeconds(50));
            Eq("📷 errore di prova", h.Last, "errore della fotocamera mandato al telefono");
            h.PhotoError = null;

            int late = h.Photos;
            c.Process(Msg(42, "/foto", t4.AddSeconds(70)), t4.AddSeconds(70 + 300));
            Eq(late, h.Photos, "/foto arrivato con 5 minuti di ritardo: nessuna foto");
            Eq(true, h.Last.Contains("scaduto"), "risposta: comando scaduto");

            sec.CodePhoto = false;
            DateTime t5 = t4.AddMinutes(30);   // sessione /sblocca finita
            c.Process(Msg(42, "/aiuto", t5), t5);
            Eq(true, h.Last.Contains("/foto [n]") && !h.Last.Contains("/foto CODICE"), "aiuto senza codice per /foto");
            c.Process(Msg(42, "/foto", t5), t5);
            Eq(late + 1, h.Photos, "codice disattivato: foto senza codice");
            c.Process(Msg(42, "/foto 123456 1", t5.AddSeconds(15)), t5.AddSeconds(15));
            Eq(1, h.PhotoCamera, "un codice scritto lo stesso viene ignorato");

            string dark = RemoteController.PhotoCaption("Cam", true, t);
            Eq(true, dark.StartsWith("📷 Cam, ") && dark.Contains("quasi nera"), "foto nera: avviso nella didascalia");
            Eq(true, RemoteController.PhotoCaption(null, false, t).StartsWith("📷 Webcam, "), "senza nome: Webcam");

            Console.WriteLine("[R12b] Webcam: formato, luminosità, messaggi");
            List<int[]> f = new List<int[]>();
            f.Add(new[] { 640, 480, 3000 }); f.Add(new[] { 1920, 1080, 500 }); f.Add(new[] { 1920, 1080, 3000 }); f.Add(new[] { 3840, 2160, 3000 });
            Eq(2, Webcam.ChooseFormat(f), "il più grande entro 1920x1080, a parità più fotogrammi al secondo");
            List<int[]> big = new List<int[]>();
            big.Add(new[] { 4096, 2160, 3000 }); big.Add(new[] { 2592, 1944, 1500 }); big.Add(new[] { 0, 0, 0 });
            Eq(1, Webcam.ChooseFormat(big), "tutti troppo grandi: il più piccolo");
            Eq(-1, Webcam.ChooseFormat(new List<int[]>()), "nessun formato");
            Eq(0, Webcam.MeanLuma(new byte[64 * 48 * 4], 64, 48), "immagine nera: luminosità 0");
            byte[] white = new byte[64 * 48 * 4];
            for (int i = 0; i < white.Length; i++) white[i] = 255;
            Eq(255, Webcam.MeanLuma(white, 64, 48), "immagine bianca: luminosità 255");
            Eq("📷 Fotocamera 3 inesistente: ce ne sono 2 (1 Integrated Camera, 2 USB Camera).",
               Webcam.CameraList(3, new List<string> { "Integrated Camera", "USB Camera" }), "fotocamera inesistente: elenco");
            Eq(true, Webcam.ErrorText(Webcam.E_ACCESSDENIED).Contains("Consenti alle app desktop"), "accesso negato: dove attivarlo");
            Eq(true, Webcam.ErrorText(unchecked((int)0xC00D3704)).EndsWith("0xC00D3704."), "altri errori con il codice");
            foreach (string title in new[] { "FOTO DALLA WEBCAM", "AL TELEFONO" })
                Eq(true, PixelFont.CanDraw(title) && PixelFont.Measure(title, 1) <= 156, "titolo nel font pixel: " + title);
            L.En = true;
            try
            {
                Eq("/photo", RemoteController.Cmd("foto"), "nome mostrato in inglese");
                Eq(true, Webcam.ErrorText(Webcam.E_ACCESSDENIED).Contains("Let desktop apps access your camera"), "accesso negato in inglese");
                foreach (string title in new[] { "WEBCAM PHOTO", "TO THE PHONE" })
                    Eq(true, PixelFont.CanDraw(title) && PixelFont.Measure(title, 1) <= 156, "titolo inglese nel font pixel: " + title);
            }
            finally { L.En = false; }
        }

        /// <summary>
        /// [R11] Client Telegram contro il server finto: quello che nella 1.6.0 faceva perdere i comandi (Abort che dopo un
        /// invio non fermava più la lettura) e i tempi dei nuovi tentativi di invio.
        /// </summary>
        private static void NetworkTests()
        {
            Console.WriteLine("[R11] Rete: lettura, invii e connessioni (server Telegram finto)");
            string savedBase = TelegramApi.BaseUrl;
            FakeTelegram tg = new FakeTelegram();
            TelegramApi.BaseUrl = tg.BaseUrl;
            try
            {
                // 1) Abort ferma la lettura in attesa anche se nel frattempo la stessa istanza ha mandato un messaggio
                TelegramApi api = new TelegramApi("1:TEST");
                Exception readError = null;
                Thread reader = new Thread(delegate()
                {
                    try { api.GetUpdates(0, 20); } catch (Exception ex) { readError = ex; }
                });
                reader.IsBackground = true;
                reader.Start();
                Eq(true, FakeTelegram.WaitFor(delegate { return tg.PollPending; }, 5000), "lettura in attesa sul server");
                Eq(true, api.SendMessage(FakeTelegram.Phone, "risposta", 10000) > 0, "invio con la stessa istanza durante la lettura");
                api.Abort();
                Eq(true, reader.Join(3000), "Abort ferma la lettura anche dopo un invio (nella 1.6.0 restava appesa)");
                Eq(true, readError is TelegramException, "la lettura interrotta finisce con un errore di rete");

                // 2) Abort non tocca un invio in corso (nella 1.6.0 interrompeva la risposta al posto della lettura)
                tg.SlowSendMs = 1200;
                Exception sendError = null;
                long slowId = 0;
                Thread sender = new Thread(delegate()
                {
                    try { slowId = api.SendMessage(FakeTelegram.Phone, "lento", 10000); } catch (Exception ex) { sendError = ex; }
                });
                sender.IsBackground = true;
                sender.Start();
                Thread.Sleep(300);
                api.Abort();
                sender.Join(5000);
                tg.SlowSendMs = 0;
                Eq("", sendError == null ? "" : sendError.Message, "Abort non interrompe un invio in corso");
                Eq(true, slowId > 0, "l'invio lento arriva");

                // 3) dopo Close nessuna lettura parte più (thread di lettura fermato), gli invii sì
                TelegramApi closing = new TelegramApi("1:TEST");
                closing.Close();
                int before = tg.Polls;
                bool refused = false;
                try { closing.GetUpdates(0, 1); } catch (TelegramException) { refused = true; }
                Eq(true, refused, "dopo Close la lettura è rifiutata");
                Eq(before, tg.Polls, "dopo Close nessuna lettura arriva al server");
                Eq(true, closing.SendMessage(FakeTelegram.Phone, "ok", 10000) > 0, "dopo Close gli invii funzionano ancora");

                // 4) connessioni: la lettura riusa la sua finché NewConnections non ne chiede una nuova; gli invii mai
                TelegramApi conn = new TelegramApi("1:TEST");
                tg.Inject("/a");
                UpdateBatch b1 = conn.GetUpdates(0, 5);
                int p1 = tg.LastPollPort;
                tg.Inject("/b");
                UpdateBatch b2 = conn.GetUpdates(b1.NextOffset, 5);
                int p2 = tg.LastPollPort;
                Eq(1, b2.Messages.Count, "seconda lettura: un messaggio");
                Eq(true, p1 == p2, "letture di seguito sulla stessa connessione");
                TelegramApi.NewConnections();
                tg.Inject("/c");
                UpdateBatch b3 = conn.GetUpdates(b2.NextOffset, 5);
                Eq(1, b3.Messages.Count, "lettura dopo NewConnections: un messaggio");
                Eq(true, tg.LastPollPort != p2, "dopo NewConnections (risveglio, rete) la lettura usa una connessione nuova");
                conn.SendMessage(FakeTelegram.Phone, "uno", 10000);
                conn.SendMessage(FakeTelegram.Phone, "due", 10000);
                List<FakeTelegram.Sent> s = tg.SentMessages;
                int sa = s[s.Count - 2].Port, sb = s[s.Count - 1].Port;
                Eq(true, sa != sb && sa != tg.LastPollPort && sb != tg.LastPollPort, "ogni invio su una connessione nuova");
            }
            finally
            {
                TelegramApi.BaseUrl = savedBase;
                tg.Dispose();
            }

            // 5) tempi dei nuovi tentativi di invio
            StringBuilder delays = new StringBuilder();
            for (int i = 1; i <= 7; i++) delays.Append(i > 1 ? "," : "").Append(SendRetry.DelaySeconds(i));
            Eq("1,2,4,8,15,15,15", delays.ToString(), "attese tra i tentativi di invio (secondi; nella 1.6.0 fino a 300)");
            DateTime t = new DateTime(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);
            Eq(false, SendRetry.GiveUp(true, t, t.AddDays(2)), "un messaggio della coda nel database non si abbandona");
            Eq(false, SendRetry.GiveUp(false, t, t.AddMinutes(2)), "una risposta si riprova per 2 minuti");
            Eq(true, SendRetry.GiveUp(false, t, t.AddMinutes(4)), "una risposta di 4 minuti si abbandona");
        }
    }
}
