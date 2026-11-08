using System;
using System.Collections.Generic;
using System.IO;

// Collaudo della logica (database, tracker, formattazione) con tempo simulato.
// Eseguito con Mono su Linux: vedi run-tests.sh (winsqlite3.dll viene mappata su libsqlite3).
namespace WinUsage.Tests
{
    internal static class Tests
    {
        private static int failures;
        private static int checks;

        private static void Eq(object expected, object actual, string what)
        {
            checks++;
            if (!object.Equals(expected, actual))
            {
                failures++;
                Console.WriteLine("  FAIL " + what + ": atteso " + expected + ", ottenuto " + actual);
            }
            else Console.WriteLine("  ok   " + what + " = " + actual);
        }

        private static DateTime t0 = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);

        // Fa avanzare il tempo di "seconds" secondi con un tick al secondo.
        private static DateTime Run(UsageTracker tr, DateTime from, int seconds, bool browser, bool steam)
        {
            DateTime t = from;
            for (int i = 0; i < seconds; i++)
            {
                t = t.AddSeconds(1);
                tr.Tick(t, browser, steam);
            }
            return t;
        }

        private static int Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "winusage-test-" + Guid.NewGuid().ToString("N"));
            string dbPath = Path.Combine(dir, "usage.db");
            Console.WriteLine("TZ locale: " + TimeZoneInfo.Local.Id + "  | db: " + dbPath);

            Console.WriteLine("[1] Formattazione");
            Eq("1 ora", Fmt.Long(3600), "Long(3600)");
            Eq("2 ore", Fmt.Long(7200), "Long(7200)");
            Eq("1 ora e 30 minuti", Fmt.Long(5400), "Long(5400)");
            Eq("1 minuto", Fmt.Long(60), "Long(60)");
            Eq("45 minuti", Fmt.Long(45 * 60), "Long(2700)");
            Eq("01:30:05", Fmt.Clock(5405), "Clock(5405)");
            Eq("1h 05m", Fmt.Compact(3900), "Compact(3900)");

            Console.WriteLine("[2] Database e impostazioni");
            Storage st = new Storage(dbPath);
            Eq(true, st.Available, "db disponibile");
            AppSettings s = AppSettings.Load(st);
            Eq(60, s.NotifyMinutes, "default NotifyMinutes");
            s.NotifyMinutes = 60; s.NotifyEnabled = true; s.AutoStart = false; s.TrackSteam = true;
            s.BrowserProcesses = "chrome, firefox.exe;Edge";
            s.Save(st);
            AppSettings s2 = AppSettings.Load(st);
            Eq(false, s2.AutoStart, "AutoStart salvato");
            Eq(true, s2.NotifyFullscreen, "NotifyFullscreen predefinito = attivo");
            s.NotifyFullscreen = false; s.Save(st);
            Eq(false, AppSettings.Load(st).NotifyFullscreen, "NotifyFullscreen salvato");
            s.NotifyFullscreen = true; s.Save(st);
            Eq(true, s2.BrowserSet().Contains("firefox"), "BrowserSet normalizza .exe");
            Eq(true, s2.BrowserSet().Contains("edge"), "BrowserSet case-insensitive");
            s.BrowserProcesses = AppSettings.DefaultBrowsers;

            Console.WriteLine("[3] Tracker: 1h attivo, standby 40 min (con eventi), 1h30 attivo oltre la mezzanotte locale");
            UsageTracker tr = new UsageTracker(st, s, t0);
            List<double> fired = new List<double>();
            tr.ThresholdReached += delegate(double sec) { fired.Add(sec); };

            DateTime t = Run(tr, t0, 1800, true, false);    // 30 min con browser
            t = Run(tr, t, 1800, false, false);              // 30 min senza
            Eq(3600.0, tr.Session(TimerIds.System), "sessione dopo 1h");
            Eq(1, fired.Count, "una notifica alla soglia di 1h");
            Eq(1800.0, tr.Session(TimerIds.Browser), "browser 30 min");

            tr.NotifySuspend(t);                             // standby
            t = t.AddMinutes(40);                            // 40 minuti dormiti
            tr.NotifyResume(t);
            tr.Tick(t.AddSeconds(1), false, false);
            t = t.AddSeconds(1);
            Eq(3601.0, tr.Session(TimerIds.System), "i 40 min di standby non sono contati (3600s + 1s dopo il resume)");

            t = Run(tr, t, 5399, false, true);               // fino a 1h30 totali dopo il resume, con Steam
            double expected = 3600 + 1 + 5399;
            Eq(expected, tr.Session(TimerIds.System), "sessione totale 2.5h");
            Eq(2, fired.Count, "seconda notifica a 2h");
            Eq(5399.0, tr.Session(TimerIds.Steam), "steam");

            Console.WriteLine("[4] Standby SENZA eventi (solo salto dell'orologio)");
            double before = tr.Session(TimerIds.System);
            t = t.AddMinutes(30);
            tr.Tick(t, false, false);                        // primo tick dopo il salto: scartato
            Eq(before, tr.Session(TimerIds.System), "salto di 30 min scartato");
            t = Run(tr, t, 10, false, false);
            Eq(before + 10, tr.Session(TimerIds.System), "poi riprende a contare");

            Console.WriteLine("[5] Cambio utente rapido (sessione disconnessa)");
            before = tr.Session(TimerIds.System);
            tr.SetPaused(true, t);
            t = Run(tr, t, 600, true, true);
            Eq(before, tr.Session(TimerIds.System), "in pausa: nulla viene contato");
            tr.SetPaused(false, t);
            t = Run(tr, t, 5, false, false);
            Eq(before + 5, tr.Session(TimerIds.System), "dopo la riconnessione conta di nuovo");

            Console.WriteLine("[6] Soglia disattivata / cambiata");
            AppSettings off = s.Clone(); off.NotifyEnabled = false;
            tr.ApplySettings(off);
            int n = fired.Count;
            t = Run(tr, t, 4000, false, false);
            Eq(n, fired.Count, "nessuna notifica se disattivata");
            AppSettings every10 = s.Clone(); every10.NotifyMinutes = 10;
            tr.ApplySettings(every10);
            t = Run(tr, t, 1200, false, false);
            Eq(n + 2, fired.Count, "ogni 10 minuti: 2 notifiche in 20 min");

            tr.Shutdown(t);
            double totalSession = tr.Session(TimerIds.System);
            double totalBrowser = tr.Session(TimerIds.Browser);
            double totalSteam = tr.Session(TimerIds.Steam);
            st.Dispose();

            Console.WriteLine("[7] Persistenza: riapro il database");
            Storage st2 = new Storage(dbPath);
            long sumSys = 0, sumBr = 0, sumSteam = 0;
            int days = 0;
            foreach (object[] row in st2.Query("SELECT day, timer, seconds FROM usage_daily ORDER BY day, timer"))
            {
                Console.WriteLine("       " + row[0] + "  " + row[1] + "  " + row[2]);
                long v = (long)row[2];
                if ((string)row[1] == "system") { sumSys += v; days++; }
                else if ((string)row[1] == "browser") sumBr += v;
                else if ((string)row[1] == "steam") sumSteam += v;
            }
            Eq(true, Math.Abs(sumSys - totalSession) < 1.0, "somma giornaliera sistema = sessione (" + sumSys + " vs " + totalSession + ")");
            Eq(true, Math.Abs(sumBr - totalBrowser) < 1.0, "somma giornaliera browser");
            Eq(true, Math.Abs(sumSteam - totalSteam) < 1.0, "somma giornaliera steam");
            Eq(2, days, "il tempo e' diviso su 2 giorni (mezzanotte locale)");

            List<object[]> sess = st2.Query("SELECT active_seconds FROM sessions");
            Eq(1, sess.Count, "una riga in sessions");
            Eq(true, Math.Abs((long)sess[0][0] - totalSession) < 1.0, "active_seconds sessione salvato");

            UsageTracker tr2 = new UsageTracker(st2, s, t.AddMinutes(5));
            Eq(0.0, tr2.Session(TimerIds.System), "nuova sessione riparte da zero");
            Eq(true, tr2.Today(TimerIds.System) > 0 || DateTime.UtcNow.Date != t.Date, "il totale di oggi viene ricaricato dal db");
            Eq(2, st2.Query("SELECT id FROM sessions").Count, "seconda riga in sessions");
            st2.Dispose();

            Console.WriteLine("[9] Jingle 8 bit (WAV)");
            byte[] wav = ChipTune.BuildWav();
            Eq("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4), "intestazione RIFF");
            Eq("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4), "formato WAVE");
            Eq(wav.Length - 8, BitConverter.ToInt32(wav, 4), "dimensione RIFF coerente");
            Eq(wav.Length - 44, BitConverter.ToInt32(wav, 40), "dimensione dati coerente");
            double secsWav = (wav.Length - 44) / 2.0 / 22050;
            Eq(true, secsWav > 0.45 && secsWav < 0.6, "durata ~0,5 s (" + secsWav.ToString("0.00") + ")");

            Console.WriteLine("[8] Storico 7 giorni");
            string dbPath2 = Path.Combine(dir, "hist.db");
            Storage sh = new Storage(dbPath2);
            DateTime today = new DateTime(2026, 11, 8);
            for (int i = 0; i < 10; i++)      // 10 giorni di dati: ne devono comparire solo 7
            {
                string day = today.AddDays(-i).ToString("yyyy-MM-dd");
                sh.AddUsage(day, "system", 3600 * (i + 1));
                if (i % 2 == 0) sh.AddUsage(day, "browser", 1800);
                if (i == 3) sh.AddUsage(day, "steam", 7200);
            }
            double[] live = new double[TimerIds.Count];
            live[TimerIds.System] = 3600 + 90;    // oggi: valore in memoria piu' recente di quello salvato
            HistoryData hd = HistoryData.Build(sh, today, live, 7);
            Eq(7, hd.DayKeys.Length, "7 giorni");
            Eq("2026-11-02", hd.DayKeys[0], "primo giorno");
            Eq("2026-11-08", hd.DayKeys[6], "ultimo giorno = oggi");
            Eq(3690.0, hd.Seconds[6][TimerIds.System], "oggi usa il valore live");
            Eq(3600.0 * 7, hd.Seconds[0][TimerIds.System], "giorno piu' vecchio (i=6)");
            Eq(7200.0, hd.Seconds[3][TimerIds.Steam], "steam 5 nov (i=3)");
            Eq(0.0, hd.Seconds[1][TimerIds.Browser], "giorno senza browser = 0");
            Eq("oggi", hd.ShortLabel(6), "etichetta di oggi");
            Eq("lun 02/11", hd.ShortLabel(0), "etichetta in italiano");
            Eq("Domenica 8 novembre (oggi)", hd.LongLabel(6), "etichetta lunga");
            HistoryData empty = HistoryData.Build(sh, today.AddDays(100), null, 7);
            Eq(0.0, empty.Total(TimerIds.System), "nessun dato: totale 0");
            sh.Dispose();

            try { Directory.Delete(dir, true); } catch { }
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TUTTI I " + checks + " CONTROLLI SUPERATI" : failures + " CONTROLLI FALLITI su " + checks);
            return failures == 0 ? 0 : 1;
        }
    }
}
