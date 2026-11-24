using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Controllo remoto via Telegram: collega il controller (regole di sicurezza) a rete, finestre e azioni di Windows.
    /// Due thread di rete: uno legge i messaggi (long polling), uno invia. Tutto il resto (database compreso) gira nel
    /// thread dell'interfaccia: i thread di rete ci rientrano con <see cref="Post"/>.
    /// </summary>
    internal sealed class RemoteService : IRemoteHost, IDisposable
    {
        private readonly Storage storage;
        private readonly RemoteStore store;
        private readonly UsageTracker tracker;
        private readonly Control invoker;
        private readonly bool autostart;
        private readonly Icon icon;

        private RemoteSecrets secrets;
        private RemoteSettings settings;
        private bool damaged;
        private readonly RemoteController controller;

        // rete
        private TelegramApi api;
        private Thread pollThread;
        private volatile bool pollRunning;
        private readonly AutoResetEvent pollWake = new AutoResetEvent(false);
        private readonly BlockingCollection<OutItem> sendQueue = new BlockingCollection<OutItem>();
        private readonly Thread sendThread;
        private readonly ManualResetEvent disposing = new ManualResetEvent(false);
        private DateTime lastConflictAlertUtc = DateTime.MinValue;
        private volatile bool pollKicked;            // richiesta interrotta apposta: si riparte subito, senza attesa
        private DateTime lastTickUtc = DateTime.MinValue;

        // stato del PC
        private bool locked, disconnected;
        private DateTime lockedSinceUtc = DateTime.MinValue;
        private DateTime lockedRemotelyUtc = DateTime.MinValue;
        private bool sessionEnding;

        // spegnimento
        private readonly ShutdownCountdown countdown = new ShutdownCountdown();
        private BadgeForm badge;
        private bool lastMinuteShown;
        private DateTime shutdownIssuedUtc = DateTime.MinValue;
        private bool sleepCancelPending;              // conto alla rovescia annullato dallo standby, da segnalare al risveglio
        private DateTime lastDeleteCheckUtc = DateTime.MinValue;

        // chat
        private RemoteChatForm chatForm;
        private bool chatClosingSilently;             // chiusura decisa dall'app (abbinamento rimosso, /stop): niente avviso
        private readonly ChatHistory chatHistory = new ChatHistory();   // solo in memoria: sparisce alla chiusura dell'app

        /// <summary>Stato cambiato (attivo, abbinato...): aggiornare icona, popup e finestra di configurazione.</summary>
        public event Action StateChanged;

        private sealed class OutItem
        {
            public int Type;                 // 0 testo, 1 immagine, 2 cancellazione
            public long ChatId;
            public string Text;
            public byte[] Data;
            public bool AsDocument;
            public long MessageId;
            public long OutboxId = -1;       // > 0: messaggio persistente (riprovato finché non parte)
            public DateTime CreatedUtc = DateTime.UtcNow;
            public int Attempts;
            public Action<long> OnSent;
        }

        public RemoteService(Storage storage, UsageTracker tracker, Control invoker, bool autostart, Icon icon)
        {
            this.storage = storage;
            this.tracker = tracker;
            this.invoker = invoker;
            this.autostart = autostart;
            this.icon = icon;
            store = new RemoteStore(storage);
            secrets = RemoteSecrets.Load(storage, out damaged);
            settings = RemoteSettings.Load(storage);
            NotifyCenter.Sounds = settings.Sounds;
            controller = new RemoteController(this, secrets, settings, store.TotpLastStep);

            sendThread = new Thread(SendLoop);
            sendThread.IsBackground = true;
            sendThread.Name = "WinUsage invio Telegram";
            sendThread.Start();

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }

        /// <summary>Rete cambiata (Wi-Fi ricollegato, VPN...): la connessione della lettura è quasi certamente morta.</summary>
        private void OnNetworkChanged(object sender, EventArgs e)
        {
            Post(delegate { KickPolling("rete cambiata"); });
        }

        // ---- stato ----

        public bool Damaged { get { return damaged; } }
        public bool Paired { get { return secrets.Paired; } }
        public bool Enabled { get { return settings.Enabled; } }
        /// <summary>Riceve comandi: attivo, con token e configurazione integra (l'abbinamento può mancare).</summary>
        public bool Listening { get { return settings.Enabled && secrets.HasToken && !damaged; } }
        /// <summary>Tutto pronto: anche un telefono abbinato.</summary>
        public bool Active { get { return Listening && secrets.Paired; } }
        public bool ShowIndicator { get { return Active && settings.NotifyTray; } }
        public bool ChatAvailable { get { return Active; } }
        public RemoteSecrets SecretsCopy { get { return secrets.Clone(); } }
        public RemoteSettings SettingsCopy { get { return settings.Clone(); } }
        public List<RemoteLogEntry> RecentLog(int max) { return store.RecentLog(max); }
        public bool PairingOpen { get { return controller.PairingOpen(DateTime.UtcNow); } }
        public DateTime PairingUntilUtc { get { return controller.PairingUntilUtc; } }

        /// <summary>All'avvio dell'app: messaggi rimasti in coda, spegnimento improvviso, accensione.</summary>
        public void Start()
        {
            if (damaged)
                SecurityAlert("La configurazione del controllo remoto non si può leggere (modificata o copiata da un altro " +
                              "utente o PC): il controllo remoto è disattivato. Reimpostalo da Controllo remoto.");
            RestartPolling();
            if (!Active) return;

            DateTime now = DateTime.UtcNow;
            foreach (OutboxItem it in store.LoadOutbox(now))
                EnqueuePersistentExisting(it.Id, PowerMessages.Late(it.Text, it.CreatedUtc, now));

            DateTime lastSeen;
            bool clean;
            if (settings.MsgCrash && storage.PreviousSession(tracker.SessionId, out lastSeen, out clean) && !clean && lastSeen > DateTime.MinValue)
                EnqueuePersistent("crash", PowerMessages.Crash(lastSeen, now));
            if (settings.MsgPowerOn)
                EnqueuePersistent("on", PowerMessages.On(tracker.SessionStartUtc, autostart, now));
        }

        private void RaiseStateChanged()
        {
            Action h = StateChanged;
            if (h != null) { try { h(); } catch (Exception ex) { Log.Write(ex); } }
        }

        // ---- configurazione (finestra Controllo remoto) ----

        public void ApplyConfig(RemoteSecrets newSecrets, RemoteSettings newSettings)
        {
            bool tokenChanged = newSecrets.Token != secrets.Token;
            secrets = newSecrets;
            settings = newSettings;
            damaged = false;
            secrets.Save(storage);
            settings.Save(storage);
            NotifyCenter.Sounds = settings.Sounds;
            controller.Apply(secrets, settings);
            if (!settings.ChatHistory && (chatForm == null || chatForm.IsDisposed)) chatHistory.Clear();
            if (tokenChanged) store.Offset = 0;
            RestartPolling();
            RaiseStateChanged();
        }

        /// <summary>Apre l'abbinamento (5 minuti) e restituisce il codice da scrivere al bot.</summary>
        public string StartPairing()
        {
            string code = controller.StartPairing(DateTime.UtcNow);
            if (pollThread == null) RestartPolling();
            return code;
        }

        public void StopPairing()
        {
            controller.StopPairing();
        }

        public void Unpair()
        {
            secrets.UserId = 0;
            secrets.UserName = "";
            secrets.TotpSecret = "";
            secrets.TotpVerified = false;
            secrets.Save(storage);
            controller.Apply(secrets, settings);
            CancelShutdown(false);
            CloseChat(true);   // un altro telefono non deve vedere la chat del precedente
            RaiseStateChanged();
        }

        /// <summary>Verifica un token chiamando getMe in background; il risultato arriva nel thread dell'interfaccia.</summary>
        public void TestToken(string token, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string user = null, error = null;
                try { user = new TelegramApi(token).GetMeUsername(); }
                catch (TelegramException ex) { error = ex.Unauthorized ? "Token non valido." : ex.Message; }
                catch (Exception ex) { error = TelegramApi.Redact(ex.Message, token); }
                if (error != null) Log.Write("Verifica del token non riuscita: " + error);
                Post(delegate { done(user, error); });
            });
        }

        // ---- lettura dei messaggi ----

        private void RestartPolling()
        {
            StopPolling();
            if (!Listening) return;
            api = new TelegramApi(secrets.Token);
            pollRunning = true;
            pollThread = new Thread(PollLoop);
            pollThread.IsBackground = true;
            pollThread.Name = "WinUsage lettura Telegram";
            pollThread.Start(new object[] { api, store.Offset });
        }

        private void StopPolling()
        {
            pollRunning = false;
            pollWake.Set();
            if (api != null) api.Abort();
            if (pollThread != null) { pollThread.Join(2000); pollThread = null; }
            api = null;   // gli invii usano il token attuale; le letture in ritardo vengono ignorate
        }

        private void PollLoop(object state)
        {
            object[] st = (object[])state;
            TelegramApi a = (TelegramApi)st[0];
            long offset = (long)st[1];
            int backoff = 1;
            bool webhookChecked = false;
            while (pollRunning)
            {
                try
                {
                    if (!webhookChecked)
                    {
                        string url = a.GetWebhookUrl();
                        webhookChecked = true;
                        if (url.Length > 0)
                        {
                            Post(delegate
                            {
                                SecurityAlert("Sul bot è impostato un webhook che WinUsage non usa: qualcuno potrebbe avere il token. " +
                                              "Revoca il token da @BotFather (/revoke) e inserisci quello nuovo.");
                            });
                            return;
                        }
                    }

                    // 25 secondi: molti router e operatori chiudono senza avvisare le connessioni ferme da più di 30-60 s,
                    // e una lettura "appesa" su una connessione morta faceva arrivare i comandi con minuti di ritardo.
                    UpdateBatch b = a.GetUpdates(offset, 25);
                    backoff = 1;
                    if (b.NextOffset != offset)
                    {
                        long next = b.NextOffset;
                        List<IncomingMessage> msgs = b.Messages;
                        offset = next;
                        Post(delegate
                        {
                            if (a != api) return;   // configurazione cambiata nel frattempo
                            store.Offset = next;
                            foreach (IncomingMessage m in msgs)
                            {
                                try { controller.Process(m, a.ServerNowUtc()); }
                                catch (Exception ex) { Log.Write(ex); }
                            }
                        });
                    }
                }
                catch (TelegramException ex)
                {
                    if (!pollRunning) return;
                    if (pollKicked) { pollKicked = false; backoff = 1; continue; }
                    if (ex.Unauthorized)
                    {
                        Post(delegate { SecurityAlert("Telegram rifiuta il token del bot (revocato o sbagliato): il controllo remoto è fermo."); });
                        return;
                    }
                    if (ex.Conflict)
                    {
                        Post(delegate
                        {
                            if ((DateTime.UtcNow - lastConflictAlertUtc).TotalHours < 1) return;
                            lastConflictAlertUtc = DateTime.UtcNow;
                            const string text = "Un altro programma sta leggendo i messaggi del bot con lo stesso token: " +
                                                "possibile token compromesso, oppure WinUsage configurato con questo bot su un altro PC. " +
                                                "Usa un bot per ogni PC; se non sei stato tu, revoca il token da @BotFather (/revoke).";
                            SecurityAlert(text);
                            if (secrets.Paired) SendText(secrets.UserId, "⚠️ " + text);
                        });
                        Wait(30000);
                    }
                    else
                    {
                        Log.Write("Telegram (lettura): " + ex.Message);
                        Wait(backoff * 1000);
                        backoff = TelegramApi.NextBackoff(backoff);
                    }
                }
                catch (Exception ex)
                {
                    if (!pollRunning) return;
                    if (pollKicked) { pollKicked = false; backoff = 1; continue; }
                    Log.Write("Telegram (lettura): " + TelegramApi.Redact(ex.Message, secrets.Token));
                    Wait(backoff * 1000);
                    backoff = TelegramApi.NextBackoff(backoff);
                }
            }
        }

        private void Wait(int ms)
        {
            pollWake.WaitOne(ms);
        }

        /// <summary>
        /// Interrompe la lettura in corso e ne fa partire subito una nuova: dopo un risveglio, uno sblocco, un cambio di rete o
        /// un periodo in cui l'app è rimasta ferma, la connessione aperta può essere morta senza che nessuno lo sappia.
        /// </summary>
        private void KickPolling(string why)
        {
            if (!Listening || pollThread == null || api == null) return;
            pollKicked = true;
            api.Abort();
            pollWake.Set();
        }

        // ---- invio ----

        private void Enqueue(OutItem it)
        {
            try { sendQueue.Add(it); } catch (Exception) { /* in chiusura */ }
        }

        /// <summary>Messaggio persistente: passa dalla coda nel database, così non si perde senza rete o allo spegnimento.</summary>
        private void EnqueuePersistent(string kind, string text)
        {
            if (!secrets.Paired) return;
            long id = store.AddOutbox(kind, text, DateTime.UtcNow);
            EnqueuePersistentExisting(id, text);
        }

        private void EnqueuePersistentExisting(long id, string text)
        {
            OutItem it = new OutItem();
            it.Type = 0;
            it.ChatId = secrets.UserId;
            it.Text = text;
            it.OutboxId = id;
            Enqueue(it);
        }

        private void SendLoop()
        {
            try
            {
                foreach (OutItem it in sendQueue.GetConsumingEnumerable())
                {
                    while (true)
                    {
                        TelegramApi a = api ?? (secrets.HasToken ? new TelegramApi(secrets.Token) : null);
                        if (a == null || it.ChatId == 0) break;
                        try
                        {
                            long id = 0;
                            if (it.Type == 0) id = a.SendMessage(it.ChatId, it.Text, 30000);
                            else if (it.Type == 1) id = a.SendImage(it.ChatId, it.Data, it.AsDocument, null);
                            else a.DeleteMessage(it.ChatId, it.MessageId);
                            OutItem done = it;
                            long sentId = id;
                            Post(delegate
                            {
                                if (done.OutboxId > 0) store.RemoveOutbox(done.OutboxId);
                                if (done.OnSent != null) done.OnSent(sentId);
                            });
                            break;
                        }
                        catch (Exception ex)
                        {
                            TelegramException te = ex as TelegramException;
                            string msg = TelegramApi.Redact(ex.Message, secrets.Token);
                            if (te != null && (te.Permanent || te.Unauthorized))
                            {
                                Log.Write("Telegram (invio) non riuscito: " + msg);
                                OutItem failed = it;
                                bool drop = !te.Unauthorized;   // con un token nuovo si riprova al prossimo avvio
                                Post(delegate { if (failed.OutboxId > 0 && drop) store.RemoveOutbox(failed.OutboxId); });
                                break;
                            }
                            it.Attempts++;
                            bool persistent = it.OutboxId > 0;
                            if (!persistent && (it.Attempts >= 4 || (DateTime.UtcNow - it.CreatedUtc).TotalMinutes > 3))
                            {
                                Log.Write("Telegram (invio) abbandonato dopo " + it.Attempts + " tentativi: " + msg);
                                break;
                            }
                            // primo nuovo tentativo dopo 1 s (di solito era solo una connessione vecchia chiusa dal server)
                            int waitS = it.Attempts == 1 ? 1 : Math.Min(300, 5 * it.Attempts * it.Attempts);
                            if (disposing.WaitOne(waitS * 1000)) return;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>
        /// Invio immediato con attesa breve (allo spegnimento o all'uscita, quando non c'è tempo per la coda). Se non riesce
        /// il messaggio resta nel database e parte al prossimo avvio.
        /// </summary>
        private void SendNowOrLater(string kind, string text, int timeoutMs)
        {
            if (!secrets.Paired) return;
            long id = store.AddOutbox(kind, text, DateTime.UtcNow);
            try
            {
                new TelegramApi(secrets.Token).SendMessage(secrets.UserId, text, timeoutMs);
                store.RemoveOutbox(id);
            }
            catch (Exception ex)
            {
                Log.Write("Messaggio \"" + kind + "\" rimandato al prossimo avvio: " + TelegramApi.Redact(ex.Message, secrets.Token));
            }
        }

        private void Post(Action action)
        {
            try
            {
                if (invoker.IsHandleCreated && !invoker.IsDisposed)
                {
                    invoker.BeginInvoke((MethodInvoker)delegate
                    {
                        try { action(); } catch (Exception ex) { Log.Write(ex); }
                    });
                }
            }
            catch (Exception) { /* app in chiusura */ }
        }

        // ---- eventi del PC (chiamati da TrayContext nel thread dell'interfaccia) ----

        public void Tick(DateTime nowUtc)
        {
            RemoteActions.TrackForeground(nowUtc);

            // L'app è rimasta ferma (standby moderno, PC bloccato con lo schermo spento, sistema sotto sforzo): la
            // connessione della lettura potrebbe essere morta, meglio aprirne subito una nuova.
            if (lastTickUtc > DateTime.MinValue && (nowUtc - lastTickUtc).TotalSeconds > 10) KickPolling("app ferma");
            lastTickUtc = nowUtc;

            CountdownEvent ev = countdown.Tick(nowUtc);
            if (ev == CountdownEvent.Expired)
            {
                // Scaduto mentre l'app era ferma e adesso qualcuno sta usando il PC: niente spegnimento a sorpresa.
                if (countdown.LateSeconds(nowUtc) > 120 && !PcLocked)
                {
                    HideCountdownUi();
                    Audit(secrets.UserId, "(spegnimento)", "annullato: scaduto in ritardo con il PC in uso");
                    if (Active) SendText(secrets.UserId, "Spegnimento annullato: il conto alla rovescia è scaduto mentre il PC era sospeso e ora il PC è in uso.");
                    ShowCancelled("IL PC ERA SOSPESO");
                }
                else OnCountdownExpired(nowUtc);
            }
            else if (countdown.Active)
            {
                double rem = countdown.Remaining(nowUtc);
                if (rem <= 60 && !lastMinuteShown)
                {
                    lastMinuteShown = true;
                    ShowCountdownNotice(0);
                }
                if (rem <= 10 && settings.NotifyCountdown && settings.Sounds) ChipTune.Play(4);
            }

            if (shutdownIssuedUtc > DateTime.MinValue && !sessionEnding && (nowUtc - shutdownIssuedUtc).TotalMinutes >= 3)
            {
                shutdownIssuedUtc = DateTime.MinValue;
                RemoteActions.KeepAwake(false);
                if (Active) SendText(secrets.UserId, "⚠️ Spegnimento bloccato da un'applicazione (probabilmente un documento non salvato).");
            }

            if ((nowUtc - lastDeleteCheckUtc).TotalSeconds >= 30)
            {
                lastDeleteCheckUtc = nowUtc;
                if (secrets.Paired) foreach (long id in store.DueDeletes(nowUtc)) EnqueueDelete(id);
            }
        }

        private void EnqueueDelete(long messageId)
        {
            store.RemovePendingDelete(messageId);   // un tentativo solo: se non riesce si riprova al prossimo screenshot
            OutItem it = new OutItem();
            it.Type = 2;
            it.ChatId = secrets.UserId;
            it.MessageId = messageId;
            Enqueue(it);
        }

        public void OnSuspend()
        {
            if (countdown.CancelForSleep())
            {
                HideCountdownUi();
                Audit(secrets.UserId, "(spegnimento)", "annullato: PC in standby");
                sleepCancelPending = true;
            }
            if (Active && settings.MsgSleep) SendNowOrLater("sleep", "💤 " + PowerMessages.PcName + " in standby " + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow), 2000);
            if (api != null) api.Abort();
        }

        public void OnResume()
        {
            KickPolling("risveglio");
            pollWake.Set();
            if (sleepCancelPending)
            {
                sleepCancelPending = false;
                if (Active) SendText(secrets.UserId, "Spegnimento annullato: il PC è andato in standby durante il conto alla rovescia.");
                ShowCancelled("IL PC ERA IN STANDBY");
            }
            if (Active && settings.MsgSleep) EnqueuePersistent("resume", "☀️ " + PowerMessages.PcName + " ripreso " + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
        }

        public void OnLock()
        {
            if (!locked) lockedSinceUtc = DateTime.UtcNow;
            locked = true;
            if (Active && settings.MsgLock) EnqueuePersistent("lock", "🔒 " + PowerMessages.PcName + " bloccato " + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
        }

        public void OnUnlock()
        {
            locked = false;
            KickPolling("sblocco");
            if (Active && settings.MsgLock) EnqueuePersistent("unlock", "🔓 " + PowerMessages.PcName + " sbloccato " + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
            if (lockedRemotelyUtc > DateTime.MinValue)
            {
                DateTime at = lockedRemotelyUtc;
                lockedRemotelyUtc = DateTime.MinValue;
                if (settings.NotifyLocked)
                {
                    NotifyRequest r = new NotifyRequest();
                    r.Kind = NotifyKind.LockedRemote;
                    r.Title = "BLOCCATO DAL TELEFONO";
                    r.Big = "ALLE " + RemoteController.LocalTime(at);
                    r.Line3 = "BENTORNATO!";
                    r.Seconds = 8;
                    r.Tune = 1;
                    NotifyCenter.Show(r);
                }
            }
        }

        public void OnDisconnect() { disconnected = true; }
        public void OnConnect() { disconnected = false; }

        /// <summary>Windows si sta spegnendo o l'utente si disconnette: ultimo messaggio, con invio immediato.</summary>
        public void OnSessionEnding(bool logoff)
        {
            sessionEnding = true;
            if (!Active || !settings.MsgPowerOff) return;
            DateTime now = DateTime.UtcNow;
            long[] today = new long[TimerIds.Count];
            for (int i = 0; i < TimerIds.Count; i++) today[i] = (long)tracker.Today(i);
            SendNowOrLater("off", PowerMessages.Off(now, logoff, tracker.Session(TimerIds.System), today, now), 3000);
        }

        /// <summary>L'utente chiude WinUsage (o l'installer lo aggiorna): il PC resta acceso senza controllo.</summary>
        public void OnAppExit()
        {
            if (sessionEnding || !Active || !settings.MsgPowerOff) return;
            DateTime now = DateTime.UtcNow;
            SendNowOrLater("exit", PowerMessages.AppClosed(now, now), 3000);
        }

        // ---- spegnimento ----

        private void OnCountdownExpired(DateTime nowUtc)
        {
            HideCountdownUi();
            Audit(secrets.UserId, "(spegnimento)", "conto alla rovescia scaduto");
            tracker.FlushNow(nowUtc);
            // PC bloccato: nessuno può salvare o rispondere a "un'app impedisce l'arresto", che fermerebbe tutto. Si forza.
            bool force = PcLocked;
            if (force) Audit(secrets.UserId, "(spegnimento)", "PC bloccato: spegnimento forzato");
            if (RemoteActions.PowerOff(force))
            {
                shutdownIssuedUtc = nowUtc;
                RemoteActions.KeepAwake(true);   // finché Windows non chiude, il PC non deve addormentarsi
            }
            else if (Active) SendText(secrets.UserId, "⚠️ Windows non ha accettato lo spegnimento.");
        }

        private void ShowCountdownNotice(int seconds)
        {
            if (!settings.NotifyCountdown) return;
            NotifyCenter.Remove(NotifyKind.Countdown);
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.Countdown;
            r.Title = "SPEGNIMENTO DA REMOTO";
            r.Button = "ANNULLA";
            r.Line3 = "SALVA IL LAVORO!";
            r.Seconds = seconds;
            r.Priority = 2;
            r.Tune = 3;
            r.OnButton = CancelFromPc;
            r.Update = delegate(NotifyArt art)
            {
                if (!countdown.Active) return false;
                double rem = countdown.Remaining(DateTime.UtcNow);
                int s = (int)Math.Ceiling(rem);
                art.Big = "TRA " + (s / 60) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
                art.Level = countdown.TotalSeconds > 0 ? rem / countdown.TotalSeconds : 0;
                return true;
            };
            NotifyCenter.Show(r);
        }

        private void HideCountdownUi()
        {
            lastMinuteShown = false;
            RemoteActions.KeepAwake(false);
            NotifyCenter.Remove(NotifyKind.Countdown);
            if (badge != null && !badge.IsDisposed) badge.Close();
            badge = null;
        }

        private void CancelFromPc()
        {
            if (!countdown.Cancel()) return;
            HideCountdownUi();
            Audit(0, "(annullato dal PC)", "spegnimento annullato");
            if (Active) SendText(secrets.UserId, "Spegnimento annullato dal PC alle " + RemoteController.LocalTime(DateTime.UtcNow) + ".");
            ShowCancelled("ANNULLATO DA QUI");
        }

        private void ShowCancelled(string line)
        {
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.Cancelled;
            r.Title = "SPEGNIMENTO ANNULLATO";
            r.Big = "OSSIGENO OK";
            r.Line3 = line;
            r.Seconds = 6;
            r.Tune = 5;
            NotifyCenter.Show(r);
        }

        // ---- chat ----

        /// <summary>Tasto CHAT del popup.</summary>
        public void OpenChatFromPc()
        {
            if (!Active) return;
            ShowChat(true);
        }

        private void ShowChat(bool activate)
        {
            if (chatForm == null || chatForm.IsDisposed)
            {
                chatForm = new RemoteChatForm(secrets.UserName, icon, settings.ChatHistory);
                chatForm.SendRequested += SendChat;
                chatForm.LoadHistory(chatHistory.Snapshot());
                chatForm.FormClosed += delegate(object s, FormClosedEventArgs e)
                {
                    chatForm = null;
                    if (!settings.ChatHistory) chatHistory.Clear();
                    // Chiusa da chi sta al PC (CHIUDI o X): il telefono lo sa e non scrive a vuoto.
                    if (!chatClosingSilently && e.CloseReason == CloseReason.UserClosing && Active)
                    {
                        SendText(secrets.UserId, "💬 Chat chiusa sul PC alle " + RemoteController.LocalTime(DateTime.UtcNow) +
                                                 ". I prossimi messaggi arriveranno come notifica.");
                        Audit(0, "(chat dal PC)", "chat chiusa");
                    }
                };
            }
            chatForm.ShowChat(activate);
        }

        /// <summary>
        /// Chiusura decisa dall'app (abbinamento rimosso, /stop, uscita): nessun avviso al telefono. La cronologia si
        /// cancella se richiesto (abbinamento rimosso) o se è disattivata.
        /// </summary>
        private void CloseChat(bool clearHistory)
        {
            NotifyCenter.Remove(NotifyKind.ChatRequest);
            NotifyCenter.Remove(NotifyKind.ChatMessage);
            chatClosingSilently = true;
            try { if (chatForm != null && !chatForm.IsDisposed) chatForm.Close(); }
            finally { chatClosingSilently = false; }
            chatForm = null;
            if (clearHistory || !settings.ChatHistory) chatHistory.Clear();
        }

        /// <summary>CHIUDI su una notifica della chat: chi sta al PC non vuole chattare adesso.</summary>
        private void RefuseChat()
        {
            if (!settings.ChatHistory) chatHistory.Clear();
            NotifyCenter.Remove(NotifyKind.ChatMessage);
            if (!Active) return;
            SendText(secrets.UserId, "💬 Chat rifiutata sul PC: chi è al computer non vuole chattare adesso.");
            Audit(0, "(chat dal PC)", "chat rifiutata");
        }

        /// <summary>Testo scritto nella chat del PC: va solo al telefono abbinato.</summary>
        private bool SendChat(string text)
        {
            if (!Active || string.IsNullOrEmpty(text)) return false;
            OutItem it = new OutItem();
            it.Type = 0;
            it.ChatId = secrets.UserId;
            it.Text = "💬 " + text;
            Enqueue(it);
            chatHistory.Add(text, true, DateTime.UtcNow);
            Audit(0, "(chat dal PC)", text.Length + " caratteri");
            return true;
        }

        // ---- IRemoteHost ----

        public void SendText(long chatId, string text)
        {
            OutItem it = new OutItem();
            it.Type = 0;
            it.ChatId = chatId;
            it.Text = text;
            Enqueue(it);
        }

        public void SendScreenshot(long chatId, ScreenshotResult shot)
        {
            OutItem it = new OutItem();
            it.Type = 1;
            it.ChatId = chatId;
            it.Data = shot.Data;
            it.AsDocument = shot.Png;
            int minutes = settings.ScreenshotDeleteMinutes;
            if (minutes > 0) it.OnSent = delegate(long id) { store.AddPendingDelete(id, DateTime.UtcNow.AddMinutes(minutes)); };
            Enqueue(it);
        }

        public ScreenshotResult CaptureScreenshot(int monitor, bool hd)
        {
            if (disconnected)
            {
                ScreenshotResult r = new ScreenshotResult();
                r.Error = "👥 Un altro utente sta usando il PC: lo screenshot non è possibile.";
                return r;
            }
            return RemoteActions.Capture(monitor, hd, secrets.ExclusionSet());
        }

        public ForegroundInfo GetForegroundInfo()
        {
            bool away = locked || Probe.UserAway();
            return RemoteActions.GetForeground(away, disconnected, lockedSinceUtc);
        }

        public LockResult Lock()
        {
            LockResult r = RemoteActions.Lock();
            if (r == LockResult.Locked)
            {
                lockedRemotelyUtc = DateTime.UtcNow;
                if (!locked) lockedSinceUtc = DateTime.UtcNow;
            }
            return r;
        }

        public DateTime StartShutdown(int minutes)
        {
            countdown.Start(minutes, DateTime.UtcNow);
            lastMinuteShown = countdown.TotalSeconds <= 60;
            shutdownIssuedUtc = DateTime.MinValue;
            sleepCancelPending = false;
            RemoteActions.KeepAwake(true);   // niente standby né schermo spento finché il conto è in corso
            if (settings.NotifyCountdown)
            {
                if (badge == null || badge.IsDisposed)
                {
                    badge = new BadgeForm(
                        delegate { return countdown.Remaining(DateTime.UtcNow); },
                        delegate { return countdown.TotalSeconds > 0 ? countdown.Remaining(DateTime.UtcNow) / countdown.TotalSeconds : 0; });
                    badge.CancelClicked += CancelFromPc;
                    badge.Show();
                    badge.BringToFront();
                }
                ShowCountdownNotice(countdown.TotalSeconds <= 60 ? 0 : 11);
            }
            return countdown.DueUtc.ToLocalTime();
        }

        public bool CancelShutdown(bool fromRemote)
        {
            if (!countdown.Cancel()) return false;
            HideCountdownUi();
            if (fromRemote) ShowCancelled("DAL TELEFONO");
            return true;
        }

        public bool ShutdownActive { get { return countdown.Active; } }

        public bool PcLocked { get { return locked || Probe.UserAway(); } }

        public void OpenChat(bool requestedRemotely)
        {
            if (chatForm != null && !chatForm.IsDisposed)
            {
                chatForm.ShowChat(false);
                if (Active) SendText(secrets.UserId, "💬 La chat è già aperta sul PC.");
                return;
            }
            // Non si apre di colpo una finestra mentre qualcuno lavora o gioca: notifica con APRI CHAT.
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.ChatRequest;
            r.Title = "IL TELEFONO VUOLE CHATTARE";
            r.Big = "CHAT";
            r.Button = "APRI CHAT";
            r.Button2 = "CHIUDI";
            r.Seconds = 20;
            r.Priority = 1;
            r.Tune = 2;
            Action accept = delegate
            {
                bool wasOpen = chatForm != null && !chatForm.IsDisposed;
                ShowChat(true);
                if (!wasOpen && Active) SendText(secrets.UserId, "💬 Chat aperta sul PC: puoi scrivere.");
            };
            r.OnButton = accept;
            r.OnClick = accept;
            r.OnButton2 = RefuseChat;
            r.OnTimeout = delegate
            {
                if ((chatForm == null || chatForm.IsDisposed) && Active)
                    SendText(secrets.UserId, "💬 Nessuno ha aperto la chat sul PC. I messaggi arriveranno come notifica.");
            };
            NotifyCenter.Remove(NotifyKind.ChatRequest);   // una richiesta sola alla volta
            NotifyCenter.Show(r);
        }

        public void DeliverChat(string text, DateTime sentUtc)
        {
            chatHistory.Add(text, false, sentUtc);
            if (chatForm != null && !chatForm.IsDisposed)
            {
                chatForm.AddIncoming(text, sentUtc);
                chatForm.ShowChat(false);
                return;
            }
            if (!settings.NotifyChat) { ShowChat(false); return; }
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.ChatMessage;
            r.Title = "MESSAGGIO DAL TELEFONO";
            r.FreeText = text;
            r.Button = "APRI CHAT";
            r.Button2 = "CHIUDI";
            r.OnButton2 = RefuseChat;
            r.Seconds = 11;
            r.Priority = 1;
            r.Tune = 2;
            r.OnButton = delegate { ShowChat(true); };
            r.OnClick = delegate { ShowChat(true); };
            NotifyCenter.Show(r);
        }

        public void DisableRemote()
        {
            settings.Enabled = false;
            settings.Save(storage);
            controller.Apply(secrets, settings);
            StopPolling();
            CloseChat(false);
            RaiseStateChanged();
        }

        public void ConfirmPairing(long userId, string name, Action<bool> done)
        {
            // Dopo il codice giusto serve comunque il clic di chi sta al PC.
            Post(delegate
            {
                bool ok;
                using (Form owner = new Form())
                {
                    owner.TopMost = true;
                    owner.ShowInTaskbar = false;
                    owner.StartPosition = FormStartPosition.CenterScreen;
                    owner.Size = new Size(1, 1);
                    owner.Show();
                    ok = MessageBox.Show(owner,
                        "Abbinare questo account Telegram al controllo remoto di WinUsage?\r\n\r\n" + name + "\r\nid " +
                        userId.ToString(CultureInfo.InvariantCulture) + "\r\n\r\nSe non sei stato tu a chiederlo, rispondi No.",
                        "WinUsage - Abbinamento", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }
                done(ok);
                if (ok)
                {
                    NotifyRequest r = new NotifyRequest();
                    r.Kind = NotifyKind.Paired;
                    r.Title = "TELEFONO ABBINATO";
                    r.FreeText = name;
                    r.Seconds = 8;
                    r.Tune = 0;
                    NotifyCenter.Show(r);
                }
                RaiseStateChanged();
            });
        }

        public void NotifyLocal(string kind)
        {
            NotifyRequest r = new NotifyRequest();
            if (kind == "screenshot")
            {
                if (!settings.NotifyScreenshot) return;
                r.Kind = NotifyKind.Screenshot;
                r.Title = "SCREENSHOT INVIATO";
                r.Big = "AL TELEFONO";
                r.Line3 = "ALLE " + RemoteController.LocalTime(DateTime.UtcNow);
                r.Seconds = 5;
                r.Tune = 1;
            }
            else if (kind == "app")
            {
                if (!settings.NotifyApp) return;
                r.Kind = NotifyKind.AppQuery;
                r.Title = "IL TELEFONO HA CHIESTO";
                r.Big = "L'APP IN USO";
                r.Line3 = "ALLE " + RemoteController.LocalTime(DateTime.UtcNow);
                r.Seconds = 4;
                r.Tune = 1;
            }
            else return;
            NotifyCenter.Show(r);
        }

        public void SecurityAlert(string text)
        {
            Log.Write("Sicurezza: " + text);
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.Security;
            r.Title = "ATTENZIONE: SICUREZZA";
            r.FreeText = text;
            r.Seconds = 0;   // resta finché non si clicca
            r.Priority = 3;
            r.Tune = 3;
            NotifyCenter.Show(r);
            Audit(0, "(avviso di sicurezza)", text);
        }

        public void Audit(long senderId, string command, string result)
        {
            store.AddLog(DateTime.UtcNow, senderId, command, result);
        }

        public void SaveSecrets()
        {
            secrets.Save(storage);
            RaiseStateChanged();
        }

        public void SaveTotpStep(long step)
        {
            store.TotpLastStep = step;
        }

        public string StatusText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("📊 ").Append(PowerMessages.PcName).Append('\n');
            sb.Append("In uso da ").Append(Fmt.Long(tracker.Session(TimerIds.System)))
              .Append(" (acceso ").Append(RemoteController.When(tracker.SessionStartUtc, DateTime.UtcNow)).Append(")\n");
            sb.Append("Oggi: ");
            for (int i = 0; i < TimerIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TimerIds.Labels[i]).Append(' ').Append(Fmt.Compact(tracker.Today(i)));
            }
            if (locked || Probe.UserAway()) sb.Append("\n🔒 PC bloccato");
            if (countdown.Active) sb.Append("\n⏻ Spegnimento alle ").Append(countdown.DueUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            StopPolling();
            disposing.Set();
            sendQueue.CompleteAdding();
            HideCountdownUi();
            CloseChat(true);
        }
    }
}
