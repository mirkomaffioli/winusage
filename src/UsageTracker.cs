using System;
using System.Globalization;

namespace WinUsage
{
    /// <summary>
    /// Motore di conteggio. Non dipende da WinForms né dall'orologio: riceve l'ora a ogni Tick,
    /// quindi è collaudabile con tempi simulati.
    ///
    /// Regola per lo standby: a ogni Tick (circa ogni secondo) si misura il tempo trascorso dal Tick
    /// precedente. Se supera MaxGapSeconds il computer era in standby/ibernazione (o l'orologio è stato
    /// cambiato) e quell'intervallo NON viene contato. In più TrayContext chiama NotifySuspend /
    /// NotifyResume sugli eventi di alimentazione per salvare i dati prima di dormire.
    /// </summary>
    internal sealed class UsageTracker
    {
        public const double MaxGapSeconds = 5.0;
        private const double FlushEverySeconds = 30.0;

        private readonly Storage storage;
        private AppSettings settings;

        private readonly double[] session = new double[TimerIds.Count];
        private readonly double[] today = new double[TimerIds.Count];
        private readonly double[] pending = new double[TimerIds.Count];

        private DateTime lastTickUtc;
        private readonly DateTime sessionStartUtc;
        private readonly long sessionId;
        private string dayKey;
        private double sinceFlush;
        private double nextThreshold;
        private bool paused;

        /// <summary>Sollevato quando il tempo di utilizzo supera la soglia; argomento = secondi di utilizzo.</summary>
        public event Action<double> ThresholdReached;

        public DateTime SessionStartUtc { get { return sessionStartUtc; } }

        public UsageTracker(Storage storage, AppSettings settings, DateTime nowUtc)
        {
            this.storage = storage;
            this.settings = settings;
            this.lastTickUtc = nowUtc;
            this.sessionStartUtc = nowUtc;
            this.dayKey = DayKey(nowUtc);
            LoadToday();
            this.sessionId = storage.StartSession(nowUtc);
            ResetThreshold();
        }

        public double Session(int timer) { return session[timer]; }
        public double Today(int timer) { return today[timer]; }

        public void ApplySettings(AppSettings newSettings)
        {
            settings = newSettings;
            ResetThreshold();
        }

        /// <summary>Da chiamare circa una volta al secondo.</summary>
        public void Tick(DateTime nowUtc, bool browserActive, bool steamActive)
        {
            double dt = (nowUtc - lastTickUtc).TotalSeconds;
            lastTickUtc = nowUtc;

            if (paused) return;
            // Intervallo anomalo: standby, ibernazione o cambio dell'orologio -> non si conta.
            if (dt <= 0 || dt > MaxGapSeconds) return;

            string key = DayKey(nowUtc);
            if (key != dayKey) RollDay(key);

            Add(TimerIds.System, dt);
            if (browserActive && settings.TrackBrowser) Add(TimerIds.Browser, dt);
            if (steamActive && settings.TrackSteam) Add(TimerIds.Steam, dt);

            sinceFlush += dt;
            if (sinceFlush >= FlushEverySeconds) Flush(nowUtc);

            CheckThreshold();
        }

        /// <summary>Il computer sta per andare in standby/ibernazione: salva tutto.</summary>
        public void NotifySuspend(DateTime nowUtc)
        {
            Flush(nowUtc);
        }

        /// <summary>Il computer è ripartito: il tempo dormito non va contato.</summary>
        public void NotifyResume(DateTime nowUtc)
        {
            lastTickUtc = nowUtc;
        }

        /// <summary>Sessione utente disconnessa (cambio utente rapido) o riconnessa.</summary>
        public void SetPaused(bool value, DateTime nowUtc)
        {
            if (value == paused) return;
            if (value) Flush(nowUtc);
            paused = value;
            lastTickUtc = nowUtc;
        }

        public void Shutdown(DateTime nowUtc)
        {
            Flush(nowUtc);
        }

        // ---- interni ----

        private void Add(int timer, double dt)
        {
            session[timer] += dt;
            today[timer] += dt;
            pending[timer] += dt;
        }

        private void ResetThreshold()
        {
            if (settings.NotifyEnabled && settings.NotifyMinutes > 0)
            {
                double interval = settings.NotifyMinutes * 60.0;
                nextThreshold = (Math.Floor(session[TimerIds.System] / interval) + 1) * interval;
            }
            else
            {
                nextThreshold = double.MaxValue;
            }
        }

        private void CheckThreshold()
        {
            double used = session[TimerIds.System];
            if (settings.NotifyEnabled && used >= nextThreshold)
            {
                ResetThreshold();
                Action<double> handler = ThresholdReached;
                if (handler != null) handler(used);
            }
        }

        private void WritePending()
        {
            for (int i = 0; i < TimerIds.Count; i++)
            {
                long whole = (long)Math.Floor(pending[i]);
                if (whole > 0)
                {
                    storage.AddUsage(dayKey, TimerIds.Names[i], whole);
                    pending[i] -= whole;
                }
            }
        }

        private void Flush(DateTime nowUtc)
        {
            WritePending();
            storage.UpdateSession(sessionId, nowUtc, (long)session[TimerIds.System]);
            sinceFlush = 0;
        }

        private void RollDay(string newKey)
        {
            WritePending();          // chiude il giorno precedente
            dayKey = newKey;
            LoadToday();
        }

        private void LoadToday()
        {
            long[] saved = storage.GetDay(dayKey);
            for (int i = 0; i < TimerIds.Count; i++) today[i] = saved[i];
            // pending[] resta invariato: sono frazioni di secondo ancora da scrivere
        }

        public static string DayKey(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
