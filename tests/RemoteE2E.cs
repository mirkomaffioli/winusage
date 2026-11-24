using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WinUsage
{
    // Program.cs (con il suo Main) non fa parte della prova: a RemoteService serve solo questo campo.
    internal static class Program { public static bool RestartRequested = false; }
}

namespace WinUsage.Tests
{
    /// <summary>
    /// Prova da capo a fondo del controllo remoto: il vero RemoteService (thread di lettura e di invio, coda, controller,
    /// database) contro il server Telegram finto, con la coda dei messaggi di Windows Forms. Riproduce i casi in cui la
    /// 1.6.0 perdeva i comandi o teneva ferme le risposte. Vedi run-remote-e2e.sh.
    /// </summary>
    internal static class RemoteE2E
    {
        private static int checks, failures;
        private static Control ui;

        private static void Check(bool ok, string what)
        {
            checks++;
            if (!ok) failures++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        }

        private static void UI(MethodInvoker action) { ui.Invoke(action); }

        private static bool WaitFor(Func<bool> condition, int ms) { return FakeTelegram.WaitFor(condition, ms); }

        /// <summary>PC con WinUsage appena avviato e un telefono abbinato, come dopo l'accesso a Windows.</summary>
        private sealed class Pc : IDisposable
        {
            public readonly FakeTelegram Tg = new FakeTelegram();
            public RemoteService Svc;
            private readonly Storage storage;
            private readonly string dir;

            public Pc(bool powerOnMessage, bool sendsDown)
            {
                Tg.FailSends = sendsDown;
                TelegramApi.BaseUrl = Tg.BaseUrl;
                dir = Path.Combine(Path.GetTempPath(), "winusage-e2e-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                Log.FilePath = Path.Combine(dir, "winusage.log");
                storage = new Storage(Path.Combine(dir, "usage.db"));

                RemoteSecrets s = new RemoteSecrets();
                s.Token = "123456:PROVA";
                PairedPhone p = new PairedPhone();
                p.Id = FakeTelegram.Phone;
                p.Name = "Mirko";
                s.Phones.Add(p);
                s.Save(storage);
                RemoteSettings rs = new RemoteSettings();
                rs.Enabled = true;
                rs.MsgPowerOn = powerOnMessage;
                rs.MsgCrash = false;
                rs.MsgPowerOff = false;
                rs.Sounds = false;
                rs.Save(storage);

                UI(delegate
                {
                    UsageTracker tracker = new UsageTracker(storage, AppSettings.Load(storage), DateTime.UtcNow);
                    Svc = new RemoteService(storage, tracker, ui, true, SystemIcons.Application);
                    Svc.Start();
                });
            }

            /// <summary>Quante volte il PC ha eseguito un comando, secondo il registro dei comandi.</summary>
            public int Executed(string command)
            {
                int n = 0;
                UI(delegate { foreach (RemoteLogEntry e in Svc.RecentLog(500)) if (e.Command == command) n++; });
                return n;
            }

            public int SecurityAlerts()
            {
                int n = 0;
                UI(delegate { foreach (RemoteLogEntry e in Svc.RecentLog(500)) if (e.Command == "(avviso di sicurezza)") n++; });
                return n;
            }

            /// <summary>Posizione del primo messaggio arrivato al telefono che comincia così (-1 se nessuno).</summary>
            public int Received(string prefix)
            {
                System.Collections.Generic.List<FakeTelegram.Sent> all = Tg.SentMessages;
                for (int i = 0; i < all.Count; i++) if (all[i].Text.StartsWith(prefix, StringComparison.Ordinal)) return i;
                return -1;
            }

            public void Dispose()
            {
                UI(delegate { Svc.Dispose(); });
                Tg.Dispose();
                storage.Dispose();
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// [E1] Salva (o Aggiungi telefono) nella finestra Controllo remoto subito dopo una risposta del PC. Nella 1.6.0 il
        /// thread di lettura vecchio restava vivo: i comandi letti da lui andavano persi e compariva un falso avviso.
        /// </summary>
        private static void SaveAfterReply()
        {
            Console.WriteLine("[E1] Salva nella finestra Controllo remoto subito dopo una risposta del PC");
            using (Pc pc = new Pc(false, false))
            {
                Check(WaitFor(delegate { return pc.Tg.PollPending; }, 5000), "il PC legge i messaggi");
                pc.Tg.Inject("/aiuto");
                Check(WaitFor(delegate { return pc.Tg.SentCount >= 1; }, 5000), "risposta a /aiuto");
                Thread.Sleep(300);
                UI(delegate { pc.Svc.ApplyConfig(pc.Svc.SecretsCopy, pc.Svc.SettingsCopy); });   // quello che fa Salva

                string[] cmds = { "/cmd01", "/cmd02", "/cmd03", "/cmd04", "/cmd05", "/cmd06" };
                foreach (string c in cmds)
                {
                    pc.Tg.Inject(c);
                    Thread.Sleep(700);
                }
                WaitFor(delegate
                {
                    foreach (string c in cmds) if (pc.Executed(c) == 0) return false;
                    return true;
                }, 8000);
                int once = 0;
                foreach (string c in cmds)
                {
                    int n = pc.Executed(c);
                    if (n == 1) once++;
                    Console.WriteLine("       " + c + ": eseguito " + n + (n == 1 ? " volta" : " volte"));
                }
                Check(once == cmds.Length, "ogni comando eseguito una volta sola: " + once + " su " + cmds.Length + " (la 1.6.0 ne perdeva circa metà)");
                Check(pc.SecurityAlerts() == 0 && pc.Received("⚠️") < 0, "nessun falso avviso «un altro programma sta leggendo»");
            }
        }

        /// <summary>
        /// [E2] Risveglio (o sblocco, o rete cambiata) subito dopo una risposta del PC. Nella 1.6.0 la lettura non ripartiva:
        /// restava sulla connessione di prima, che dopo uno standby vero è morta, fino al timeout di 40 secondi.
        /// </summary>
        private static void ResumeAfterReply()
        {
            Console.WriteLine("[E2] Risveglio subito dopo una risposta del PC");
            using (Pc pc = new Pc(false, false))
            {
                Check(WaitFor(delegate { return pc.Tg.PollPending; }, 5000), "il PC legge i messaggi");
                pc.Tg.Inject("/aiuto");
                Check(WaitFor(delegate { return pc.Tg.SentCount >= 1; }, 5000), "risposta a /aiuto");
                Thread.Sleep(300);
                int polls = pc.Tg.Polls, port = pc.Tg.LastPollPort;
                UI(delegate { pc.Svc.OnResume(); });
                Check(WaitFor(delegate { return pc.Tg.Polls > polls; }, 2000), "la lettura riparte subito (nella 1.6.0 no)");
                Check(pc.Tg.LastPollPort != port, "e su una connessione nuova");
                pc.Tg.Inject("/cmd07");
                Check(WaitFor(delegate { return pc.Executed("/cmd07") == 1; }, 3000), "il comando dopo il risveglio arriva subito");
            }
        }

        /// <summary>
        /// [E3] Rete giù all'accensione per gli invii. Nella 1.6.0 il messaggio «PC acceso» riprovava dopo 1, 20, 45, 80...
        /// secondi (fino a 5 minuti) e le risposte ai comandi, in coda dietro di lui, aspettavano altrettanto.
        /// </summary>
        private static void SendsAfterOutage()
        {
            Console.WriteLine("[E3] Invii che non partono all'accensione, poi la rete torna");
            using (Pc pc = new Pc(true, true))
            {
                Check(WaitFor(delegate { return pc.Tg.PollPending; }, 5000), "il PC legge i messaggi anche se gli invii falliscono");
                pc.Tg.Inject("/aiuto");
                Check(WaitFor(delegate { return pc.Executed("/aiuto") == 1; }, 5000), "il comando viene eseguito anche senza risposte");
                Thread.Sleep(6000);   // alcuni tentativi falliti
                pc.Tg.FailSends = false;
                UI(delegate { pc.Svc.OnUnlock(); });   // rete tornata, sblocco o risveglio: gli invii riprovano subito
                bool on = WaitFor(delegate { return pc.Received("🟢") >= 0; }, 3000);
                bool help = WaitFor(delegate { return pc.Received("WinUsage su ") >= 0; }, 3000);
                Check(on, "il messaggio di accensione parte appena torna la rete");
                Check(help, "la risposta a /aiuto, in coda dietro, parte subito dopo (nella 1.6.0 restava ferma)");
                Check(!on || !help || pc.Received("🟢") < pc.Received("WinUsage su "), "l'ordine dei messaggi resta quello della coda");
            }
        }

        [STAThread]
        private static int Main()
        {
            L.En = false;
            // Mono su Linux: se l'ultimo gestore di NetworkAddressChanged viene tolto e poi se ne aggiunge un altro (ogni
            // RemoteService lo fa nel costruttore e in Dispose), la sua lettura di netlink può chiudere il processo con
            // "Operation already in progress". Un gestore fisso tiene viva quella lettura. Solo per la prova: l'app crea un
            // RemoteService solo, e su Windows il problema non c'è.
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += delegate { };
            // configurazione cifrata: in prova basta una copia (niente DPAPI di Mono)
            RemoteSecrets.Protect = delegate(byte[] b) { return (byte[])b.Clone(); };
            RemoteSecrets.Unprotect = delegate(byte[] b) { return (byte[])b.Clone(); };
            ui = new Control();
            IntPtr handle = ui.Handle;   // crea l'handle nel thread dell'interfaccia
            int result = 1;
            Thread script = new Thread(delegate()
            {
                try
                {
                    SaveAfterReply();
                    ResumeAfterReply();
                    SendsAfterOutage();
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("  FAIL errore nella prova: " + ex);
                }
                Console.WriteLine();
                Console.WriteLine(failures == 0 ? "TUTTI I " + checks + " CONTROLLI SUPERATI" : failures + " CONTROLLI FALLITI su " + checks);
                result = failures == 0 ? 0 : 1;
                try { ui.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); } catch (Exception) { }
            });
            script.IsBackground = true;
            script.Start();
            Application.Run();
            GC.KeepAlive(handle);
            Environment.Exit(result);
            return result;
        }
    }
}
