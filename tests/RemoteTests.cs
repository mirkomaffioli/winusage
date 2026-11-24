using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Collaudo del controllo remoto (logica, senza rete né finestre): vedi run-tests.sh.
namespace WinUsage.Tests
{
    /// <summary>Host finto: registra tutto quello che il controller chiede.</summary>
    internal sealed class FakeHost : IRemoteHost
    {
        public readonly List<string> Sent = new List<string>();
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

        public void SendText(long chatId, string text) { Sent.Add(text); }
        public void SendScreenshot(long chatId, ScreenshotResult shot) { Screenshots++; }
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
        public void OpenChat(bool requestedRemotely) { ChatOpened++; }
        public bool Locked;
        public bool PcLocked { get { return Locked; } }
        public void DeliverChat(string text, DateTime sentUtc) { Chats.Add(text); }
        public void DisableRemote() { Disabled++; }
        public void ConfirmPairing(long userId, string name, Action<bool> done) { done(ConfirmAnswer); }
        public void NotifyLocal(string kind) { Local.Add(kind); }
        public void SecurityAlert(string text) { Alerts.Add(text); }
        public void Audit(long senderId, string command, string result) { Audits.Add(command + " -> " + result); }
        public void SaveSecrets() { SecretsSaved++; }
        public void SaveTotpStep(long step) { LastStep = step; }
        public string StatusText() { return "stato"; }

        public string Last { get { return Sent.Count > 0 ? Sent[Sent.Count - 1] : ""; } }
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
            def.UserId = 987654321;
            def.UserName = "Mirko (@mirko)";
            def.TotpSecret = Base32.Encode(rfc);
            def.TotpVerified = true;
            def.Save(st);
            Eq(true, st.GetSetting("remote_secure", "").IndexOf("987654321", StringComparison.Ordinal) < 0 &&
                     st.GetSetting("remote_secure", "").IndexOf("ABC", StringComparison.Ordinal) < 0, "nel database token e id non compaiono in chiaro");
            RemoteSecrets back = RemoteSecrets.Load(st, out damaged);
            Eq("123456:ABC=def\nzz", back.Token, "token riletto (anche con = e a capo)");
            Eq(987654321L, back.UserId, "utente riletto");
            Eq(true, back.HasTotp, "TOTP riletto");
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

            Console.WriteLine("[R4] Coda in uscita, registro, sessioni");
            RemoteStore store = new RemoteStore(st);
            DateTime now = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
            long o1 = store.AddOutbox("off", "PC spento", now.AddDays(-8));
            long o2 = store.AddOutbox("on", "PC acceso", now.AddMinutes(-1));
            List<OutboxItem> ob = store.LoadOutbox(now);
            Eq(1, ob.Count, "messaggi oltre 7 giorni scartati");
            Eq(o2, ob[0].Id, "resta il messaggio recente");
            store.RemoveOutbox(o2);
            Eq(0, store.LoadOutbox(now).Count, "coda vuota dopo l'invio");
            Eq(true, o1 > 0, "id della coda");
            store.AddLog(now, 42, "/spegni 5", "ok");
            Eq("/spegni 5", store.RecentLog(10)[0].Command, "registro dei comandi");
            store.AddPendingDelete(100, now.AddMinutes(10));
            store.AddPendingDelete(101, now.AddHours(-50));
            Eq(0, store.DueDeletes(now).Count, "screenshot da cancellare: non ancora");
            Eq(1, store.DueDeletes(now.AddMinutes(11)).Count, "screenshot da cancellare dopo 10 minuti (quello oltre 47 ore scartato)");
            store.Offset = 555; store.TotpLastStep = 777;
            Eq(555L, store.Offset, "offset salvato");
            Eq(777L, store.TotpLastStep, "ultimo passo TOTP salvato");

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
            RemoteController c = new RemoteController(h, sec, set, 0);
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
            Eq(42L, sec.UserId, "abbinato dopo la conferma sul PC");
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
            sec.TotpSecret = Base32.Encode(rfc);
            sec.TotpVerified = true;
            c.Process(Msg(42, "/blocca", t), t);
            Eq(0, h.Locks, "senza codice non blocca");
            Eq(true, h.Last.StartsWith("Serve il codice"), "chiede il codice");
            string good = Totp.Code(rfc, Totp.StepOf(t));
            c.Process(Msg(42, "/blocca " + good, t), t);
            Eq(1, h.Locks, "codice giusto: PC bloccato");
            Eq(Totp.StepOf(t), h.LastStep, "passo del codice salvato");
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
        }
    }
}
