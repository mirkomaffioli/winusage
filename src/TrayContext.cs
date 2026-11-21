using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinUsage
{
    /// <summary>L'applicazione vera e propria: icona nella tray, timer a 1 secondo, eventi di alimentazione.</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Storage storage;
        private AppSettings settings;
        private HashSet<string> browserSet;
        private readonly UsageTracker tracker;

        private readonly NotifyIcon notifyIcon;
        private readonly Icon icon;
        private readonly Timer timer;
        private PopupForm popup;
        private SettingsForm settingsForm;
        private HistoryForm historyForm;
        private MaintenanceForm maintenanceForm;
        private DateTime lastRetentionRun = DateTime.MinValue;
        private DateTime popupHiddenAt = DateTime.MinValue;
        private DateTime lastTooltipUpdate = DateTime.MinValue;
        private bool exiting;
        private bool toastPending;   // soglia raggiunta mentre il messaggio non si poteva vedere: rimandato
        private int presentTicks;    // secondi consecutivi in cui le sonde dicono che lo schermo è sbloccato
        private readonly Control invoker = new Control();   // per rientrare nel thread dell'interfaccia

        public TrayContext(string dbPath)
        {
            storage = new Storage(dbPath);

            // Prima esecuzione: salva i valori predefiniti (avvio automatico attivo, notifica ogni ora).
            bool firstRun = storage.GetSetting("initialized", null) == null;
            settings = AppSettings.Load(storage);
            if (firstRun)
            {
                settings.Save(storage);
                storage.SetSetting("initialized", "1");
            }
            browserSet = settings.BrowserSet();
            AutoStart.Apply(settings.AutoStart);   // ad ogni avvio: riallinea anche il percorso dell'exe

            IntPtr ignored = invoker.Handle;   // crea l'handle nel thread dell'interfaccia
            tracker = new UsageTracker(storage, settings, DateTime.UtcNow);
            tracker.ThresholdReached += OnThresholdReached;

            icon = LoadIcon();
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miHistory = new ToolStripMenuItem("Storico 7 giorni...");
            miHistory.Click += delegate { ShowHistory(7); };
            ToolStripMenuItem miHistory30 = new ToolStripMenuItem("Storico 30 giorni...");
            miHistory30.Click += delegate { ShowHistory(30); };
            ToolStripMenuItem miMaintenance = new ToolStripMenuItem("Manutenzione database...");
            miMaintenance.Click += delegate { ShowMaintenance(); };
            ToolStripMenuItem miSettings = new ToolStripMenuItem("Configurazione...");
            miSettings.Click += delegate { ShowSettings(); };
            ToolStripMenuItem miTest = new ToolStripMenuItem("Mostra messaggio di prova");
            miTest.Click += delegate { ShowToast(tracker.Session(TimerIds.System), false); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Esci");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miHistory);
            menu.Items.Add(miHistory30);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miSettings);
            menu.Items.Add(miMaintenance);
            menu.Items.Add(miTest);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = icon;
            notifyIcon.ContextMenuStrip = menu;      // tasto destro
            notifyIcon.MouseClick += OnTrayMouseClick;   // tasto sinistro
            notifyIcon.MouseMove += delegate { UpdateTooltip(); };
            UpdateTooltip(true);
            notifyIcon.Visible = true;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;

            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += OnTimerTick;
            timer.Start();

            ApplyRetention();   // conservazione automatica: all'avvio e poi ogni 6 ore

            if (!storage.Available)
            {
                notifyIcon.ShowBalloonTip(8000, "WinUsage",
                    "Database non disponibile: lo storico non verrà salvato.", ToolTipIcon.Warning);
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinUsage.app.ico"))
                {
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
            return SystemIcons.Application;
        }

        // ---- tick ----

        private void OnTimerTick(object sender, EventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;

                // Schermo bloccato o salvaschermo: controllo a ogni secondo, in aggiunta agli eventi di sessione
                // (che arrivano subito ma che, se persi, lascerebbero il conteggio acceso o spento per sempre).
                bool away = Probe.UserAway();
                tracker.SetPauseReason(UsageTracker.PauseAway, away, now);
                presentTicks = away ? 0 : presentTicks + 1;
                if (presentTicks == 5 && tracker.Paused && !away)
                {
                    // Lo schermo risulta sbloccato da 5 secondi ma non è arrivato l'evento di sblocco/riconnessione.
                    Log.Write("Evento di sblocco non ricevuto: il conteggio riprende");
                    tracker.SetPauseReason(UsageTracker.PauseLocked | UsageTracker.PauseDisconnected, false, now);
                }

                string fg = (settings.TrackBrowser || settings.TrackSteam) ? Probe.ForegroundProcessName() : "";
                bool browser = settings.TrackBrowser && browserSet.Contains(fg);
                bool steam = settings.TrackSteam && Probe.SteamActive(fg);
                tracker.Tick(now, browser, steam);

                // Messaggio rimandato (schermo bloccato, oppure schermo intero con l'opzione disattivata).
                if (toastPending) TryShowPendingToast();

                if ((now - lastRetentionRun).TotalHours >= 6) ApplyRetention();
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        // ---- notifiche ----

        private void OnThresholdReached(double seconds)
        {
            toastPending = true;
            TryShowPendingToast();
        }

        /// <summary>
        /// Mostra il messaggio rimandato appena si può vedere: non sulla schermata di blocco (sparirebbe senza che
        /// nessuno lo veda) e, se l'opzione è disattivata, non sopra uno schermo intero.
        /// </summary>
        private void TryShowPendingToast()
        {
            if (tracker.Paused || Probe.UserAway()) return;
            bool fullscreen = Probe.IsFullscreen(Probe.NotificationState());
            if (fullscreen && !settings.NotifyFullscreen) return;
            toastPending = false;
            ShowToast(tracker.Session(TimerIds.System), fullscreen);
        }

        private void ShowToast(double seconds, bool fullscreen)
        {
            try
            {
                ToastForm.ShowFor(seconds);   // messaggio in pixel art, sempre in primo piano e senza rubare il focus
                // Su un gioco a schermo intero "esclusivo" Windows non permette di disegnare altre finestre:
                // il suono avvisa comunque.
                if (fullscreen) ChipTune.Play();
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }

            // Notifica di Windows (area notifiche) in aggiunta al messaggio in pixel art. Con un gioco a schermo
            // intero esclusivo Windows la trattiene e la mostra quando si esce dal gioco.
            try
            {
                notifyIcon.ShowBalloonTip(10000, "WinUsage",
                    "Stai usando il computer da " + Fmt.Long(seconds), ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        private void UpdateTooltip()
        {
            UpdateTooltip(false);
        }

        private void UpdateTooltip(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - lastTooltipUpdate).TotalSeconds < 1) return;
            lastTooltipUpdate = now;
            string text = "WinUsage - in uso da " + Fmt.Long(tracker.Session(TimerIds.System));
            if (text.Length > 63) text = text.Substring(0, 63);   // limite di NotifyIcon.Text
            notifyIcon.Text = text;
        }

        // ---- tasto sinistro: popup con il tempo di utilizzo ----

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // Il click sull'icona toglie il focus al popup (che si chiude): non riaprirlo subito.
            if ((DateTime.UtcNow - popupHiddenAt).TotalMilliseconds < 400) return;

            if (popup == null || popup.IsDisposed)
            {
                popup = new PopupForm(tracker);
                popup.VisibleChanged += delegate { if (!popup.Visible) popupHiddenAt = DateTime.UtcNow; };
            }
            popup.ShowNearTray();
        }

        // ---- tasto destro: storico e configurazione ----

        private void ShowHistory(int days)
        {
            if (historyForm != null && !historyForm.IsDisposed)
            {
                historyForm.SetDays(days);
                if (historyForm.WindowState == FormWindowState.Minimized) historyForm.WindowState = FormWindowState.Normal;
                historyForm.Activate();
                return;
            }
            historyForm = new HistoryForm(storage, tracker, icon, days);
            historyForm.Show();
        }

        private void ShowMaintenance()
        {
            if (maintenanceForm != null && !maintenanceForm.IsDisposed)
            {
                maintenanceForm.Activate();
                return;
            }
            maintenanceForm = new MaintenanceForm(storage, tracker, settings.RetentionDays, icon);
            maintenanceForm.RetentionChanged += OnRetentionChanged;
            maintenanceForm.DataChanged += delegate
            {
                if (historyForm != null && !historyForm.IsDisposed) historyForm.Reload();
            };
            maintenanceForm.Show();
        }

        private void OnRetentionChanged(int days)
        {
            settings.RetentionDays = days;
            settings.Save(storage);
            ApplyRetention();
        }

        /// <summary>Conservazione automatica: elimina i giorni più vecchi del limite scelto (se attivo).</summary>
        private void ApplyRetention()
        {
            lastRetentionRun = DateTime.UtcNow;
            if (settings.RetentionDays <= 0 || !storage.Available) return;
            try
            {
                tracker.FlushNow(DateTime.UtcNow);
                int deleted = storage.DeleteBefore(Storage.FirstDayToKeep(DateTime.Now, settings.RetentionDays));
                if (deleted > 0)
                    Log.Write("Conservazione automatica: eliminati " + deleted + " giorni più vecchi di " +
                              settings.RetentionDays + " giorni");
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        private void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(settings.Clone(), storage.DbPath, icon);
            settingsForm.Saved += OnSettingsSaved;
            settingsForm.Show();
        }

        private void OnSettingsSaved(AppSettings newSettings)
        {
            newSettings.RetentionDays = settings.RetentionDays;   // si modifica dalla finestra di manutenzione
            settings = newSettings;
            browserSet = settings.BrowserSet();
            settings.Save(storage);
            AutoStart.Apply(settings.AutoStart);
            tracker.ApplySettings(settings);
        }

        // ---- alimentazione e sessione ----

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend) tracker.NotifySuspend(DateTime.UtcNow);
                else if (e.Mode == PowerModes.Resume) tracker.NotifyResume(DateTime.UtcNow);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // Schermo bloccato: l'utente non sta usando il PC, il conteggio si ferma fino allo sblocco.
            // Cambio utente rapido: quando questa sessione viene disconnessa il PC lo sta usando un altro utente.
            try
            {
                DateTime now = DateTime.UtcNow;
                switch (e.Reason)
                {
                    case SessionSwitchReason.SessionLock:
                        presentTicks = 0;
                        tracker.SetPauseReason(UsageTracker.PauseLocked, true, now);
                        break;
                    case SessionSwitchReason.SessionUnlock:
                        tracker.SetPauseReason(UsageTracker.PauseLocked, false, now);
                        break;
                    case SessionSwitchReason.ConsoleDisconnect:
                    case SessionSwitchReason.RemoteDisconnect:
                        presentTicks = 0;
                        tracker.SetPauseReason(UsageTracker.PauseDisconnected, true, now);
                        break;
                    case SessionSwitchReason.ConsoleConnect:
                    case SessionSwitchReason.RemoteConnect:
                        tracker.SetPauseReason(UsageTracker.PauseDisconnected, false, now);
                        break;
                }
                Log.Write("Sessione: " + e.Reason + (tracker.Paused ? " (conteggio in pausa)" : " (conteggio attivo)"));
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try { tracker.Shutdown(DateTime.UtcNow); } catch (Exception ex) { Log.Write(ex); }
        }

        // ---- uscita ----

        /// <summary>Chiamato da un altro thread (WinUsage.exe --quit): chiude l'app salvando i dati.</summary>
        public void RequestQuit()
        {
            try
            {
                if (invoker.IsHandleCreated && !invoker.IsDisposed)
                    invoker.BeginInvoke(new Action(ExitApp));
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        private void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.SessionEnding -= OnSessionEnding;
                timer.Stop();
                tracker.Shutdown(DateTime.UtcNow);
                notifyIcon.Visible = false;   // evita l'icona "fantasma" nella tray
                notifyIcon.Dispose();
                if (popup != null && !popup.IsDisposed) popup.Dispose();
                invoker.Dispose();
                if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Dispose();
                if (historyForm != null && !historyForm.IsDisposed) historyForm.Dispose();
                if (maintenanceForm != null && !maintenanceForm.IsDisposed) maintenanceForm.Dispose();
                storage.Dispose();
            }
            catch (Exception ex) { Log.Write(ex); }
            base.ExitThreadCore();
        }
    }
}
