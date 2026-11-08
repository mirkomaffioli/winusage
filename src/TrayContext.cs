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
        private DateTime popupHiddenAt = DateTime.MinValue;
        private DateTime lastTooltipUpdate = DateTime.MinValue;
        private bool exiting;
        private bool toastPending;   // soglia raggiunta durante lo schermo intero, messaggio rimandato
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
            miHistory.Click += delegate { ShowHistory(); };
            ToolStripMenuItem miSettings = new ToolStripMenuItem("Configurazione...");
            miSettings.Click += delegate { ShowSettings(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Esci");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miHistory);
            menu.Items.Add(miSettings);
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
                string fg = (settings.TrackBrowser || settings.TrackSteam) ? Probe.ForegroundProcessName() : "";
                bool browser = settings.TrackBrowser && browserSet.Contains(fg);
                bool steam = settings.TrackSteam && Probe.SteamActive(fg);
                tracker.Tick(DateTime.UtcNow, browser, steam);

                // Messaggio rimandato (opzione "schermo intero" disattivata): mostralo appena finisce.
                if (toastPending && !Probe.IsFullscreen(Probe.NotificationState()))
                {
                    toastPending = false;
                    ShowToast(tracker.Session(TimerIds.System), false);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }

        // ---- notifiche ----

        private void OnThresholdReached(double seconds)
        {
            bool fullscreen = Probe.IsFullscreen(Probe.NotificationState());
            if (fullscreen && !settings.NotifyFullscreen)
            {
                toastPending = true;
                return;
            }
            ShowToast(seconds, fullscreen);
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
                notifyIcon.ShowBalloonTip(10000, "WinUsage",
                    "Stai usando il computer da " + Fmt.Long(seconds), ToolTipIcon.Info);
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
                popup.Deactivate += delegate { popupHiddenAt = DateTime.UtcNow; };
            }
            popup.ShowNearTray();
        }

        // ---- tasto destro: storico e configurazione ----

        private void ShowHistory()
        {
            if (historyForm != null && !historyForm.IsDisposed)
            {
                historyForm.Activate();
                return;
            }
            historyForm = new HistoryForm(storage, tracker, icon);
            historyForm.Show();
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
            // Cambio utente rapido: quando questa sessione viene disconnessa l'utente non sta usando il PC.
            try
            {
                if (e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                    tracker.SetPaused(true, DateTime.UtcNow);
                else if (e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                    tracker.SetPaused(false, DateTime.UtcNow);
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
                storage.Dispose();
            }
            catch (Exception ex) { Log.Write(ex); }
            base.ExitThreadCore();
        }
    }
}
