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
        private PollWorker poll;                     // lettura attuale (null = ferma); usata solo nel thread dell'interfaccia
        private readonly BlockingCollection<OutItem> sendQueue = new BlockingCollection<OutItem>();
        private readonly Thread sendThread;
        private TelegramApi sendApi;                 // solo il thread di invio
        private readonly AutoResetEvent sendWake = new AutoResetEvent(false);   // rete tornata: gli invii riprovano subito
        private readonly ManualResetEvent disposing = new ManualResetEvent(false);
        private DateTime lastConflictAlertUtc = DateTime.MinValue;
        private DateTime lastTickUtc = DateTime.MinValue;
        // update già elaborati: durante un riavvio della lettura lo stesso messaggio può arrivare due volte
        private readonly HashSet<long> seenUpdates = new HashSet<long>();
        private readonly Queue<long> seenOrder = new Queue<long>();
        private const int SeenUpdatesMax = 1000;

        /// <summary>
        /// Un thread di lettura con il suo stato. Fermato, non legge più e non conferma nulla a Telegram anche se in quel
        /// momento non si è fatto interrompere: nella 1.6.0 il flag era uno solo per tutti i thread e il riavvio lo rimetteva
        /// a true, così un thread vecchio non uscito in tempo restava vivo accanto al nuovo e i messaggi letti da lui
        /// venivano confermati e poi scartati.
        /// </summary>
        private sealed class PollWorker
        {
            public readonly TelegramApi Api;
            public readonly string Token;
            public readonly AutoResetEvent Wake = new AutoResetEvent(false);
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            public volatile bool Stopped;
            public volatile bool Kicked;      // lettura interrotta apposta: si riparte subito, senza attesa né errore nel log
            public Thread Thread;

            public PollWorker(string token)
            {
                Token = token;
                Api = new TelegramApi(token);
            }
        }

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
            controller = new RemoteController(this, secrets, settings, store.TotpLastStep);   // passo TOTP per telefono

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
                SecurityAlert(L.T("La configurazione del controllo remoto non si può leggere (modificata o copiata da un altro ", "The remote control settings cannot be read (changed or copied from another ") +
                              L.T("utente o PC): il controllo remoto è disattivato. Reimpostalo da Controllo remoto.", "user or PC): remote control is off. Set it up again from Remote control."));
            RestartPolling();
            if (!Active) return;

            DateTime now = DateTime.UtcNow;
            foreach (OutboxItem it in store.LoadOutbox(now))
            {
                if (it.ChatId == 0)
                {
                    // riga della 1.4 (un telefono solo): diventa una riga per ogni telefono abbinato
                    store.RemoveOutbox(it.Id);
                    foreach (PairedPhone p in secrets.Phones)
                        EnqueuePersistentExisting(store.AddOutbox(it.Kind, it.Text, it.CreatedUtc, p.Id), p.Id,
                                                  PowerMessages.Late(it.Text, it.CreatedUtc, now));
                }
                else if (!secrets.IsPaired(it.ChatId)) store.RemoveOutbox(it.Id);   // telefono rimosso nel frattempo
                else EnqueuePersistentExisting(it.Id, it.ChatId, PowerMessages.Late(it.Text, it.CreatedUtc, now));
            }

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
            if (tokenChanged)
            {
                store.Offset = 0;
                seenUpdates.Clear();
                seenOrder.Clear();
            }
            RestartPolling();
            RaiseStateChanged();
        }

        /// <summary>Apre l'abbinamento (5 minuti) e restituisce il codice da scrivere al bot.</summary>
        public string StartPairing()
        {
            string code = controller.StartPairing(DateTime.UtcNow);
            if (poll == null) RestartPolling();
            return code;
        }

        public void StopPairing()
        {
            controller.StopPairing();
        }

        /// <summary>
        /// Toglie un telefono (con il suo codice Authenticator). Il telefono tolto e gli altri lo vengono a sapere. Con
        /// l'ultimo telefono si fermano anche lo spegnimento da remoto e la chat, e la cronologia si cancella.
        /// </summary>
        public void RemovePhone(long phoneId)
        {
            PairedPhone p = secrets.Find(phoneId);
            if (p == null) return;
            string name = p.Label;
            if (Listening) SendText(phoneId, L.T("Questo telefono non è più abbinato a ", "This phone is no longer paired with ") + PowerMessages.PcName + ".");
            secrets.Phones.Remove(p);
            secrets.Save(storage);
            controller.Apply(secrets, settings);
            Audit(0, L.T("(abbinamento)", "(pairing)"), L.T("telefono rimosso: ", "phone removed: ") + name);
            if (secrets.Paired)
            {
                if (Listening) Broadcast("📱 " + name + L.T(" non è più abbinato a ", " is no longer paired with ") + PowerMessages.PcName + ".", 0);
                if (chatForm != null && !chatForm.IsDisposed) chatForm.SetPhones(ChatTitle(), ChatGroupNames());
            }
            else
            {
                CancelShutdown(false);
                CloseChat(true);
            }
            RaiseStateChanged();
        }

        /// <summary>Verifica un token chiamando getMe in background; il risultato arriva nel thread dell'interfaccia.</summary>
        public void TestToken(string token, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string user = null, error = null;
                try { user = new TelegramApi(token).GetMeUsername(); }
                catch (TelegramException ex) { error = ex.Unauthorized ? L.T("Token non valido.", "Invalid token.") : ex.Message; }
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
            PollWorker w = new PollWorker(secrets.Token);
            long offset = store.Offset;
            w.Thread = new Thread(delegate() { PollLoop(w, offset); });
            w.Thread.IsBackground = true;
            w.Thread.Name = "WinUsage lettura Telegram";
            poll = w;
            w.Thread.Start();
        }

        private void StopPolling()
        {
            PollWorker w = poll;
            poll = null;
            if (w == null) return;
            w.Stopped = true;
            w.Api.Close();         // interrompe la lettura in corso e rifiuta le prossime
            w.Wake.Set();
            w.Thread.Join(2000);   // di solito esce subito; se no esce da solo, senza più leggere né confermare nulla
        }

        private void PollLoop(PollWorker w, long offset)
        {
            TelegramApi a = w.Api;
            int backoff = 1;
            bool webhookChecked = false;
            while (!w.Stopped)
            {
                try
                {
                    if (!webhookChecked)
                    {
                        string url = a.GetWebhookUrl();
                        webhookChecked = true;
                        if (w.Stopped) return;
                        if (url.Length > 0)
                        {
                            Post(delegate
                            {
                                SecurityAlert(L.T("Sul bot è impostato un webhook che WinUsage non usa: qualcuno potrebbe avere il token. ", "A webhook WinUsage does not use is set on the bot: someone may have the token. ") +
                                              L.T("Revoca il token da @BotFather (/revoke) e inserisci quello nuovo.", "Revoke the token from @BotFather (/revoke) and enter the new one."));
                            });
                            return;
                        }
                    }

                    // 25 secondi: molti router e operatori chiudono senza avvisare le connessioni ferme da più di 30-60 s,
                    // e una lettura "appesa" su una connessione morta faceva arrivare i comandi con minuti di ritardo.
                    UpdateBatch b = a.GetUpdates(offset, 25);
                    // Fermato durante l'attesa: niente elaborazione e soprattutto nessuna lettura con l'offset nuovo, che
                    // confermerebbe a Telegram messaggi mai elaborati. Li rilegge il thread che lo sostituisce.
                    if (w.Stopped) return;
                    w.Kicked = false;
                    backoff = 1;
                    if (b.NextOffset != offset)
                    {
                        long next = b.NextOffset;
                        List<IncomingMessage> msgs = b.Messages;
                        offset = next;
                        Post(delegate { Deliver(w, msgs, next); });
                    }
                }
                catch (TelegramException ex)
                {
                    if (w.Stopped) return;
                    if (w.Kicked) { w.Kicked = false; backoff = 1; continue; }
                    if (ex.Unauthorized)
                    {
                        Post(delegate { SecurityAlert(L.T("Telegram rifiuta il token del bot (revocato o sbagliato): il controllo remoto è fermo.", "Telegram rejects the bot token (revoked or wrong): remote control is stopped.")); });
                        return;
                    }
                    if (ex.Conflict)
                    {
                        // Appena dopo un riavvio della lettura il 409 può venire dalla lettura chiusa di questa stessa app, se la
                        // sua richiesta era già partita e non si è fatta interrompere: si riprova tra un secondo, senza allarme.
                        if ((DateTime.UtcNow - w.StartedUtc).TotalSeconds < 15)
                        {
                            Log.Write("Telegram (lettura): conflitto subito dopo il riavvio della lettura, si riprova");
                            Wait(w, 1000);
                            continue;
                        }
                        Post(delegate
                        {
                            if ((DateTime.UtcNow - lastConflictAlertUtc).TotalHours < 1) return;
                            lastConflictAlertUtc = DateTime.UtcNow;
                            string text = L.T("Un altro programma sta leggendo i messaggi del bot con lo stesso token: ", "Another program is reading the bot's messages with the same token: ") +
                                                L.T("possibile token compromesso, oppure WinUsage configurato con questo bot su un altro PC. ", "the token may be compromised, or WinUsage is set up with this bot on another PC. ") +
                                                L.T("Usa un bot per ogni PC; se non sei stato tu, revoca il token da @BotFather (/revoke).", "Use one bot per PC; if it was not you, revoke the token from @BotFather (/revoke).");
                            SecurityAlert(text);
                            Broadcast("⚠️ " + text, 0);
                        });
                        Wait(w, 30000);
                    }
                    else
                    {
                        Log.Write("Telegram (lettura): " + ex.Message);
                        Wait(w, backoff * 1000);
                        backoff = TelegramApi.NextBackoff(backoff);
                    }
                }
                catch (Exception ex)
                {
                    if (w.Stopped) return;
                    if (w.Kicked) { w.Kicked = false; backoff = 1; continue; }
                    Log.Write("Telegram (lettura): " + TelegramApi.Redact(ex.Message, w.Token));
                    Wait(w, backoff * 1000);
                    backoff = TelegramApi.NextBackoff(backoff);
                }
            }
        }

        private static void Wait(PollWorker w, int ms)
        {
            w.Wake.WaitOne(ms);
        }

        /// <summary>
        /// Messaggi letti da un thread di lettura, nel thread dell'interfaccia. Quelli letti dal thread appena sostituito non
        /// si scartano più (nella 1.6.0 andavano persi: quel thread li aveva già confermati a Telegram); un messaggio letto due
        /// volte durante il cambio si elabora una volta sola.
        /// </summary>
        private void Deliver(PollWorker w, List<IncomingMessage> msgs, long next)
        {
            if (w.Token != secrets.Token) return;      // letti con il token di prima: sono di un altro bot
            store.Offset = next;
            foreach (IncomingMessage m in msgs)
            {
                if (!Listening) break;                  // /stop o controllo remoto spento a metà del gruppo
                if (!FirstTime(m.UpdateId)) continue;   // già elaborato
                try { controller.Process(m, w.Api.ServerNowUtc()); }
                catch (Exception ex) { Log.Write(ex); }
            }
        }

        private bool FirstTime(long updateId)
        {
            if (!seenUpdates.Add(updateId)) return false;
            seenOrder.Enqueue(updateId);
            while (seenOrder.Count > SeenUpdatesMax) seenUpdates.Remove(seenOrder.Dequeue());
            return true;
        }

        /// <summary>
        /// Interrompe la lettura in corso e ne fa partire subito una nuova, su una connessione nuova: dopo un risveglio, uno
        /// sblocco, un cambio di rete o un periodo in cui l'app è rimasta ferma, la connessione aperta può essere morta senza
        /// che nessuno lo sappia. Anche gli invii in attesa di riprovare ripartono subito.
        /// </summary>
        private void KickPolling(string why)
        {
            sendWake.Set();
            PollWorker w = poll;
            if (!Listening || w == null) return;
            TelegramApi.NewConnections();
            w.Kicked = true;
            w.Api.Abort();
            w.Wake.Set();
        }

        // ---- invio ----

        private void Enqueue(OutItem it)
        {
            try { sendQueue.Add(it); } catch (Exception) { /* in chiusura */ }
        }

        /// <summary>Messaggio persistente: passa dalla coda nel database, così non si perde senza rete o allo spegnimento.</summary>
        /// <summary>Una riga per telefono: chi non lo riceve subito lo riceve dopo, senza doppioni per gli altri.</summary>
        private void EnqueuePersistent(string kind, string text)
        {
            DateTime now = DateTime.UtcNow;
            foreach (PairedPhone p in secrets.Phones)
                EnqueuePersistentExisting(store.AddOutbox(kind, text, now, p.Id), p.Id, text);
        }

        private void EnqueuePersistentExisting(long id, long chatId, string text)
        {
            OutItem it = new OutItem();
            it.Type = 0;
            it.ChatId = chatId;
            it.Text = text;
            it.OutboxId = id;
            Enqueue(it);
        }

        /// <summary>Client per gli invii, con il token attuale (solo il thread di invio). La lettura ha il suo.</summary>
        private TelegramApi SendApi()
        {
            string token = secrets.Token;
            if (string.IsNullOrEmpty(token)) return null;
            if (sendApi == null || sendApi.Token != token) sendApi = new TelegramApi(token);
            return sendApi;
        }

        private void SendLoop()
        {
            try
            {
                foreach (OutItem it in sendQueue.GetConsumingEnumerable())
                {
                    while (true)
                    {
                        TelegramApi a = SendApi();
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
                            if (SendRetry.GiveUp(it.OutboxId > 0, it.CreatedUtc, DateTime.UtcNow))
                            {
                                Log.Write("Telegram (invio) abbandonato dopo " + it.Attempts + " tentativi: " + msg);
                                break;
                            }
                            if (it.Attempts == 1) Log.Write("Telegram (invio) non riuscito, si riprova: " + msg);
                            // Attesa breve, interrotta da rete tornata, risveglio o sblocco (KickPolling): i messaggi in coda
                            // dietro a questo, comprese le risposte ai comandi, non restano fermi per minuti.
                            int waitMs = SendRetry.DelaySeconds(it.Attempts) * 1000;
                            if (WaitHandle.WaitAny(new WaitHandle[] { disposing, sendWake }, waitMs) == 0) return;
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
            // a tutti i telefoni in parallelo: il tempo che Windows concede allo spegnimento è uno solo
            string token = secrets.Token;
            DateTime now = DateTime.UtcNow;
            List<long> sentRows = new List<long>();
            List<ManualResetEvent> waits = new List<ManualResetEvent>();
            foreach (PairedPhone p in secrets.Phones)
            {
                long row = store.AddOutbox(kind, text, now, p.Id);
                long chat = p.Id;
                ManualResetEvent ev = new ManualResetEvent(false);
                waits.Add(ev);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        new TelegramApi(token).SendMessage(chat, text, timeoutMs);
                        lock (sentRows) sentRows.Add(row);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Messaggio \"" + kind + "\" rimandato al prossimo avvio: " + TelegramApi.Redact(ex.Message, token));
                    }
                    finally { ev.Set(); }
                });
            }
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs + 500);
            foreach (ManualResetEvent ev in waits)   // una attesa alla volta: WaitAll non è permesso nel thread dell'interfaccia
                ev.WaitOne(Math.Max(0, (int)(until - DateTime.UtcNow).TotalMilliseconds));
            lock (sentRows) foreach (long row in sentRows) store.RemoveOutbox(row);
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
                    Audit(0, L.T("(spegnimento)", "(shutdown)"), L.T("annullato: scaduto in ritardo con il PC in uso", "cancelled: expired late with the PC in use"));
                    if (Active) Broadcast(L.T("Spegnimento annullato: il conto alla rovescia è scaduto mentre il PC era sospeso e ora il PC è in uso.", "Shutdown cancelled: the countdown ran out while the PC was suspended and now the PC is in use."), 0);
                    ShowCancelled(L.T("IL PC ERA SOSPESO", "THE PC WAS SUSPENDED"));
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
                if (Active) Broadcast(L.T("⚠️ Spegnimento bloccato da un'applicazione (probabilmente un documento non salvato).", "⚠️ Shutdown blocked by an app (probably an unsaved document)."), 0);
            }

            if ((nowUtc - lastDeleteCheckUtc).TotalSeconds >= 30)
            {
                lastDeleteCheckUtc = nowUtc;
                if (secrets.Paired) foreach (KeyValuePair<long, long> d in store.DueDeletes(nowUtc)) EnqueueDelete(d.Key, d.Value);
            }
        }

        /// <param name="storedChat">Chat salvata con lo screenshot; 0 = screenshot della 1.4, mandato al primo telefono.</param>
        private void EnqueueDelete(long storedChat, long messageId)
        {
            store.RemovePendingDelete(storedChat, messageId);   // un tentativo solo
            long chat = storedChat != 0 ? storedChat : (secrets.Paired ? secrets.Phones[0].Id : 0);
            if (chat == 0) return;
            OutItem it = new OutItem();
            it.Type = 2;
            it.ChatId = chat;
            it.MessageId = messageId;
            Enqueue(it);
        }

        public void OnSuspend()
        {
            if (countdown.CancelForSleep())
            {
                HideCountdownUi();
                Audit(0, L.T("(spegnimento)", "(shutdown)"), L.T("annullato: PC in standby", "cancelled: PC in standby"));
                sleepCancelPending = true;
            }
            if (Active && settings.MsgSleep) SendNowOrLater("sleep", "💤 " + PowerMessages.PcName + " in standby " + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow), 2000);
            KickPolling("standby");   // la connessione aperta non sopravvive allo standby: al risveglio se ne apre una nuova
        }

        public void OnResume()
        {
            KickPolling("risveglio");
            if (sleepCancelPending)
            {
                sleepCancelPending = false;
                if (Active) Broadcast(L.T("Spegnimento annullato: il PC è andato in standby durante il conto alla rovescia.", "Shutdown cancelled: the PC went into standby during the countdown."), 0);
                ShowCancelled(L.T("IL PC ERA IN STANDBY", "THE PC WAS ASLEEP"));
            }
            if (Active && settings.MsgSleep) EnqueuePersistent("resume", "☀️ " + PowerMessages.PcName + L.T(" ripreso ", " resumed ") + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
        }

        public void OnLock()
        {
            if (!locked) lockedSinceUtc = DateTime.UtcNow;
            locked = true;
            if (Active && settings.MsgLock) EnqueuePersistent("lock", "🔒 " + PowerMessages.PcName + L.T(" bloccato ", " locked ") + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
        }

        public void OnUnlock()
        {
            locked = false;
            KickPolling("sblocco");
            if (Active && settings.MsgLock) EnqueuePersistent("unlock", "🔓 " + PowerMessages.PcName + L.T(" sbloccato ", " unlocked ") + RemoteController.When(DateTime.UtcNow, DateTime.UtcNow));
            if (lockedRemotelyUtc > DateTime.MinValue)
            {
                DateTime at = lockedRemotelyUtc;
                lockedRemotelyUtc = DateTime.MinValue;
                if (settings.NotifyLocked)
                {
                    NotifyRequest r = new NotifyRequest();
                    r.Kind = NotifyKind.LockedRemote;
                    r.Title = L.T("BLOCCATO DAL TELEFONO", "LOCKED BY THE PHONE");
                    r.Big = L.T("ALLE ", "AT ") + RemoteController.LocalTime(at);
                    r.Line3 = L.T("BENTORNATO!", "WELCOME BACK!");
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
            // riavvio per il cambio di lingua: torna subito, niente "WinUsage chiuso" ai telefoni
            if (sessionEnding || Program.RestartRequested || !Active || !settings.MsgPowerOff) return;
            DateTime now = DateTime.UtcNow;
            SendNowOrLater("exit", PowerMessages.AppClosed(now, now), 3000);
        }

        // ---- spegnimento ----

        private void OnCountdownExpired(DateTime nowUtc)
        {
            HideCountdownUi();
            Audit(0, L.T("(spegnimento)", "(shutdown)"), L.T("conto alla rovescia scaduto", "countdown expired"));
            tracker.FlushNow(nowUtc);
            // PC bloccato: nessuno può salvare o rispondere a "un'app impedisce l'arresto", che fermerebbe tutto. Si forza.
            bool force = PcLocked;
            if (force) Audit(0, L.T("(spegnimento)", "(shutdown)"), L.T("PC bloccato: spegnimento forzato", "PC locked: forced shutdown"));
            if (RemoteActions.PowerOff(force))
            {
                shutdownIssuedUtc = nowUtc;
                RemoteActions.KeepAwake(true);   // finché Windows non chiude, il PC non deve addormentarsi
            }
            else if (Active) Broadcast(L.T("⚠️ Windows non ha accettato lo spegnimento.", "⚠️ Windows did not accept the shutdown."), 0);
        }

        private void ShowCountdownNotice(int seconds)
        {
            if (!settings.NotifyCountdown) return;
            NotifyCenter.Remove(NotifyKind.Countdown);
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.Countdown;
            r.Title = L.T("SPEGNIMENTO DA REMOTO", "REMOTE SHUTDOWN");
            r.Button = L.T("ANNULLA", "CANCEL");
            r.Line3 = L.T("SALVA IL LAVORO!", "SAVE YOUR WORK!");
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
            Audit(0, L.T("(annullato dal PC)", "(cancelled on the PC)"), L.T("spegnimento annullato", "shutdown cancelled"));
            if (Active) Broadcast(L.T("Spegnimento annullato dal PC alle ", "Shutdown cancelled on the PC at ") + RemoteController.LocalTime(DateTime.UtcNow) + ".", 0);
            ShowCancelled(L.T("ANNULLATO DA QUI", "CANCELLED HERE"));
        }

        private void ShowCancelled(string line)
        {
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.Cancelled;
            r.Title = L.T("SPEGNIMENTO ANNULLATO", "SHUTDOWN CANCELLED");
            r.Big = L.T("OSSIGENO OK", "OXYGEN OK");
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
                chatForm = new RemoteChatForm(ChatTitle(), icon, settings.ChatHistory, ChatGroupNames());
                chatForm.SendRequested += SendChat;
                chatForm.LoadHistory(chatHistory.Snapshot());
                chatForm.FormClosed += delegate(object s, FormClosedEventArgs e)
                {
                    chatForm = null;
                    if (!settings.ChatHistory) chatHistory.Clear();
                    // Chiusa da chi sta al PC (CHIUDI o X): il telefono lo sa e non scrive a vuoto.
                    if (!chatClosingSilently && e.CloseReason == CloseReason.UserClosing && Active)
                    {
                        Broadcast(L.T("💬 Chat chiusa sul PC alle ", "💬 Chat closed on the PC at ") + RemoteController.LocalTime(DateTime.UtcNow) +
                                                 L.T(". I prossimi messaggi arriveranno come notifica.", ". Next messages will arrive as a notification."), 0);
                        Audit(0, L.T("(chat dal PC)", "(chat from the PC)"), L.T("chat chiusa", "chat closed"));
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
            Broadcast(L.T("💬 Chat rifiutata sul PC: chi è al computer non vuole chattare adesso.", "💬 Chat refused on the PC: whoever is at the computer does not want to chat now."), 0);
            Audit(0, L.T("(chat dal PC)", "(chat from the PC)"), L.T("chat rifiutata", "chat refused"));
        }

        /// <summary>Nome per l'intestazione della chat: il telefono, o "tutti i telefoni" se sono più d'uno.</summary>
        private string ChatTitle()
        {
            if (secrets.Phones.Count == 1) return secrets.Phones[0].Label;
            return L.T("TUTTI I ", "ALL ") + secrets.Phones.Count + L.T(" TELEFONI", " PHONES");   // niente parentesi: il font pixel non le ha
        }

        /// <summary>Nomi dei telefoni se la chat è di gruppo, altrimenti null.</summary>
        private string ChatGroupNames()
        {
            return secrets.Phones.Count > 1 ? secrets.Names() : null;
        }

        /// <summary>Testo scritto sul PC (chat o risposta rapida): va a tutti i telefoni abbinati.</summary>
        private bool SendChat(string text)
        {
            if (!Active || string.IsNullOrEmpty(text)) return false;
            Broadcast("💬 " + text, 0);
            chatHistory.Add(text, true, DateTime.UtcNow);
            Audit(0, L.T("(chat dal PC)", "(chat from the PC)"), text.Length + L.T(" caratteri", " characters"));
            return true;
        }

        // ---- IRemoteHost ----

        /// <summary>Lo stesso testo a tutti i telefoni abbinati, tranne <paramref name="exceptId"/> (0 = a tutti).</summary>
        public void Broadcast(string text, long exceptId)
        {
            foreach (PairedPhone p in secrets.Phones) if (p.Id != exceptId) SendText(p.Id, text);
        }

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
            if (minutes > 0) it.OnSent = delegate(long id) { store.AddPendingDelete(chatId, id, DateTime.UtcNow.AddMinutes(minutes)); };
            Enqueue(it);
        }

        public ScreenshotResult CaptureScreenshot(int monitor, bool hd)
        {
            if (disconnected)
            {
                ScreenshotResult r = new ScreenshotResult();
                r.Error = L.T("👥 Un altro utente sta usando il PC: lo screenshot non è possibile.", "👥 Another user is using the PC: a screenshot is not possible.");
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
            if (fromRemote) ShowCancelled(L.T("DAL TELEFONO", "FROM THE PHONE"));
            return true;
        }

        public bool ShutdownActive { get { return countdown.Active; } }

        public bool PcLocked { get { return locked || Probe.UserAway(); } }

        public void OpenChat(bool requestedRemotely, long fromId)
        {
            if (chatForm != null && !chatForm.IsDisposed)
            {
                chatForm.ShowChat(false);
                if (Active) SendText(fromId, L.T("💬 La chat è già aperta sul PC.", "💬 The chat is already open on the PC."));
                return;
            }
            // Non si apre di colpo una finestra mentre qualcuno lavora o gioca: notifica con APRI CHAT.
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.ChatRequest;
            r.Title = L.T("IL TELEFONO VUOLE CHATTARE", "THE PHONE WANTS TO CHAT");
            r.Big = "CHAT";
            if (secrets.Phones.Count > 1)
            {
                // con più telefoni si dice chi chiede (se il font pixel ha le lettere del nome)
                string who = secrets.NameOf(fromId).ToUpperInvariant();
                r.Line3 = PixelFont.CanDraw(who) ? L.T("DA ", "FROM ") + who : L.T("DA UN TELEFONO", "FROM A PHONE");
            }
            r.Button = L.T("APRI CHAT", "OPEN CHAT");
            r.Button2 = L.T("CHIUDI", "CLOSE");
            r.Seconds = 20;
            r.Priority = 1;
            r.Tune = 2;
            Action accept = delegate
            {
                bool wasOpen = chatForm != null && !chatForm.IsDisposed;
                ShowChat(true);
                if (!wasOpen && Active) Broadcast(L.T("💬 Chat aperta sul PC: puoi scrivere.", "💬 Chat open on the PC: you can write."), 0);
            };
            r.OnButton = accept;
            r.OnClick = accept;
            r.OnButton2 = RefuseChat;
            r.OnTimeout = delegate
            {
                if ((chatForm == null || chatForm.IsDisposed) && Active)
                    SendText(fromId, L.T("💬 Nessuno ha aperto la chat sul PC. I messaggi arriveranno come notifica.", "💬 Nobody opened the chat on the PC. Messages will arrive as a notification."));
            };
            NotifyCenter.Remove(NotifyKind.ChatRequest);   // una richiesta sola alla volta
            NotifyCenter.Show(r);
        }

        public void DeliverChat(string text, DateTime sentUtc, long fromId)
        {
            string sender = secrets.NameOf(fromId);
            bool group = secrets.Phones.Count > 1;
            chatHistory.Add(text, false, sentUtc, sender);
            // chat di gruppo: anche gli altri telefoni leggono quello che ha scritto
            if (group) Broadcast("💬 " + sender + ": " + text, fromId);
            if (chatForm != null && !chatForm.IsDisposed)
            {
                chatForm.AddIncoming(text, sentUtc, group ? sender : null);
                chatForm.ShowChat(false);
                return;
            }
            if (!settings.NotifyChat) { ShowChat(false); return; }
            NotifyRequest r = new NotifyRequest();
            r.Kind = NotifyKind.ChatMessage;
            r.Title = L.T("MESSAGGIO DAL TELEFONO", "MESSAGE FROM THE PHONE");
            r.FreeText = group ? sender + ": " + text : text;
            r.QuickReplyCue = group ? L.T("Rispondi a tutti...", "Reply to all...") : L.T("Rispondi...", "Reply...");
            r.Button = L.T("APRI CHAT", "OPEN CHAT");
            r.Button2 = L.T("CHIUDI", "CLOSE");
            r.OnButton2 = RefuseChat;
            r.Seconds = 15;   // il tempo di cliccare su "rispondi"; mentre si scrive non sparisce
            r.Priority = 1;
            r.Tune = 2;
            // risposta rapida dalla notifica, senza aprire la chat; se si apre la chat, quanto scritto passa lì
            r.QuickReply = SendChat;
            Action open = delegate
            {
                ShowChat(true);
                if (chatForm != null && !chatForm.IsDisposed) chatForm.SetDraft(r.ReplyDraft);
            };
            r.OnButton = open;
            r.OnClick = open;
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
                    string others = secrets.Paired
                        ? L.T("\r\n\r\nSi aggiunge ai telefoni già abbinati (", "\r\n\r\nIt joins the phones already paired (") + secrets.Names() + L.T("): potrà usare tutti i comandi ", "): it will be able to use all commands ") +
                          L.T("e leggerà la chat e gli avvisi. Gli altri telefoni riceveranno un avviso.", "and will read the chat and notices. The other phones will get a notice.")
                        : "";
                    ok = MessageBox.Show(owner,
                        L.T("Abbinare questo account Telegram al controllo remoto di WinUsage?\r\n\r\n", "Pair this Telegram account with WinUsage remote control?\r\n\r\n") + name + "\r\nid " +
                        userId.ToString(CultureInfo.InvariantCulture) + others + L.T("\r\n\r\nSe non sei stato tu a chiederlo, rispondi No.", "\r\n\r\nIf you did not ask for it, answer No."),
                        L.T("WinUsage - Abbinamento", "WinUsage - Pairing"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }
                done(ok);
                if (ok)
                {
                    NotifyRequest r = new NotifyRequest();
                    r.Kind = NotifyKind.Paired;
                    r.Title = L.T("TELEFONO ABBINATO", "PHONE PAIRED");
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
                r.Title = L.T("SCREENSHOT INVIATO", "SCREENSHOT SENT");
                r.Big = L.T("AL TELEFONO", "TO THE PHONE");
                r.Line3 = L.T("ALLE ", "AT ") + RemoteController.LocalTime(DateTime.UtcNow);
                r.Seconds = 5;
                r.Tune = 1;
            }
            else if (kind == "app")
            {
                if (!settings.NotifyApp) return;
                r.Kind = NotifyKind.AppQuery;
                r.Title = L.T("IL TELEFONO HA CHIESTO", "THE PHONE ASKED FOR");
                r.Big = L.T("L'APP IN USO", "APP IN USE");
                r.Line3 = L.T("ALLE ", "AT ") + RemoteController.LocalTime(DateTime.UtcNow);
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
            r.Title = L.T("ATTENZIONE: SICUREZZA", "WARNING: SECURITY");
            r.FreeText = text;
            r.Seconds = 0;   // resta finché non si clicca
            r.Priority = 3;
            r.Tune = 3;
            NotifyCenter.Show(r);
            Audit(0, L.T("(avviso di sicurezza)", "(security alert)"), text);
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

        public void SaveTotpStep(long phoneId, long step)
        {
            store.SetTotpLastStep(phoneId, step);
        }

        public string StatusText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("📊 ").Append(PowerMessages.PcName).Append('\n');
            sb.Append(L.T("In uso da ", "In use for ")).Append(Fmt.Long(tracker.Session(TimerIds.System)))
              .Append(L.T(" (acceso ", " (on ")).Append(RemoteController.When(tracker.SessionStartUtc, DateTime.UtcNow)).Append(")\n");
            sb.Append(L.T("Oggi: ", "Today: "));
            for (int i = 0; i < TimerIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TimerIds.Labels[i]).Append(' ').Append(Fmt.Compact(tracker.Today(i)));
            }
            if (locked || Probe.UserAway()) sb.Append(L.T("\n🔒 PC bloccato", "\n🔒 PC locked"));
            if (secrets.Phones.Count > 1) sb.Append(L.T("\n📱 Telefoni abbinati: ", "\n📱 Paired phones: ")).Append(secrets.Phones.Count);
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
