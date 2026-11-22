using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestra "Controllo remoto": bot Telegram, abbinamento, codice Authenticator, comandi, notifiche e registro.</summary>
    internal sealed class RemoteSettingsForm : Form
    {
        private readonly RemoteService service;
        private readonly RemoteSettings working;
        private string totpSecret;         // segreto in preparazione (salvato solo con Salva)
        private bool totpVerified;
        private bool totpChanged;

        private readonly Label lblStatus = new Label();
        private readonly CheckBox chkEnabled = new CheckBox();
        private readonly TextBox txtToken = new TextBox();
        private readonly Label lblToken = new Label();
        private readonly Label lblPairCode = new Label();
        private readonly Label lblPairInfo = new Label();
        private readonly Button btnPair = new Button();
        private readonly Button btnUnpair = new Button();

        private readonly Label lblTotp = new Label();
        private readonly PictureBox picQr = new PictureBox();
        private readonly Label lblSecret = new Label();
        private readonly TextBox txtVerify = new TextBox();

        private readonly CheckBox chkCodeScreenshot = new CheckBox();
        private readonly CheckBox chkCodeApp = new CheckBox();
        private readonly CheckBox chkCodeLock = new CheckBox();
        private readonly CheckBox chkCodeShutdown = new CheckBox();
        private readonly NumericUpDown numShutdown = new NumericUpDown();
        private readonly NumericUpDown numDelete = new NumericUpDown();
        private readonly NumericUpDown numMaxAge = new NumericUpDown();
        private readonly TextBox txtExclusions = new TextBox();

        private readonly CheckBox chkMsgOn = new CheckBox();
        private readonly CheckBox chkMsgOff = new CheckBox();
        private readonly CheckBox chkMsgCrash = new CheckBox();
        private readonly CheckBox chkMsgSleep = new CheckBox();
        private readonly CheckBox chkMsgLock = new CheckBox();
        private readonly CheckBox chkNTray = new CheckBox();
        private readonly CheckBox chkNScreenshot = new CheckBox();
        private readonly CheckBox chkNApp = new CheckBox();
        private readonly CheckBox chkNChat = new CheckBox();
        private readonly CheckBox chkNLocked = new CheckBox();
        private readonly CheckBox chkNCountdown = new CheckBox();
        private readonly CheckBox chkSounds = new CheckBox();

        private readonly ListView logView = new ListView();
        private readonly Timer pairTimer = new Timer();

        public RemoteSettingsForm(RemoteService service, Icon icon)
        {
            this.service = service;
            working = service.SettingsCopy;
            RemoteSecrets s = service.SecretsCopy;
            totpSecret = s.TotpSecret;
            totpVerified = s.TotpVerified;

            Text = "WinUsage - Controllo remoto (Telegram)";
            if (icon != null) Icon = icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            Size = new Size(640, 640);
            MinimumSize = new Size(560, 520);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildConnectionTab(s));
            tabs.TabPages.Add(BuildCodeTab());
            tabs.TabPages.Add(BuildCommandsTab(s));
            tabs.TabPages.Add(BuildNotifyTab());
            tabs.TabPages.Add(BuildLogTab());

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(8);
            Button close = new Button();
            close.Text = "Chiudi";
            close.AutoSize = true;
            close.Click += delegate { Close(); };
            Button save = new Button();
            save.Text = "Salva";
            save.AutoSize = true;
            save.Click += delegate { if (SaveAll()) Close(); };
            buttons.Controls.Add(close);
            buttons.Controls.Add(save);

            Controls.Add(tabs);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = close;

            pairTimer.Interval = 1000;
            pairTimer.Tick += delegate { RefreshState(); };
            pairTimer.Start();
            RefreshState();
            RefreshTotp();
        }

        // ---- schede ----

        private static FlowLayoutPanel Page(TabPage tab)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.FlowDirection = FlowDirection.TopDown;
            p.WrapContents = false;
            p.AutoScroll = true;
            p.Padding = new Padding(12);
            tab.Controls.Add(p);
            return p;
        }

        private static Label Title(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            l.Margin = new Padding(3, 10, 3, 4);
            return l;
        }

        private static Label Note(string text, int width)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(width, 0);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 2, 3, 6);
            return l;
        }

        private static CheckBox Check(CheckBox c, string text, bool value)
        {
            c.Text = text;
            c.AutoSize = true;
            c.Checked = value;
            return c;
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            FlowLayoutPanel r = new FlowLayoutPanel();
            r.AutoSize = true;
            r.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            r.WrapContents = false;
            r.Margin = new Padding(0);
            foreach (Control c in controls) r.Controls.Add(c);
            return r;
        }

        private static Label Inline(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 7, 3, 0);
            return l;
        }

        private static NumericUpDown Number(NumericUpDown n, int min, int max, int value)
        {
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = 70;
            return n;
        }

        private TabPage BuildConnectionTab(RemoteSecrets s)
        {
            TabPage tab = new TabPage("Collegamento");
            FlowLayoutPanel p = Page(tab);
            int w = 560;

            lblStatus.AutoSize = true;
            lblStatus.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11f, FontStyle.Bold);
            lblStatus.Margin = new Padding(3, 0, 3, 8);
            p.Controls.Add(lblStatus);
            p.Controls.Add(Check(chkEnabled, "Controllo remoto attivo", working.Enabled));

            p.Controls.Add(Title("1. Bot Telegram"));
            p.Controls.Add(Note("In Telegram apri @BotFather, invia /newbot e scegli un nome qualsiasi (meglio uno che non " +
                                "faccia capire di chi è). Copia qui il token. Consigliato: /setjoingroups → Disable. " +
                                "Usa un bot diverso per ogni PC e per ogni utente.", w));
            txtToken.Width = 360;
            txtToken.UseSystemPasswordChar = true;
            txtToken.Text = s.Token;
            CheckBox show = new CheckBox();
            show.Text = "Mostra";
            show.AutoSize = true;
            show.Margin = new Padding(6, 5, 3, 0);
            show.CheckedChanged += delegate { txtToken.UseSystemPasswordChar = !show.Checked; };
            Button verify = new Button();
            verify.Text = "Verifica";
            verify.AutoSize = true;
            verify.Click += delegate
            {
                lblToken.Text = "Verifica in corso...";
                lblToken.ForeColor = SystemColors.GrayText;
                service.TestToken(txtToken.Text.Trim(), delegate(string user, string err)
                {
                    if (IsDisposed) return;
                    lblToken.Text = user != null ? "✔ Bot @" + user : "✖ " + err;
                    lblToken.ForeColor = user != null ? Color.DarkGreen : Color.DarkRed;
                });
            };
            p.Controls.Add(Row(txtToken, show, verify));
            lblToken.AutoSize = true;
            lblToken.Margin = new Padding(3, 2, 3, 6);
            p.Controls.Add(lblToken);

            p.Controls.Add(Title("2. Abbinamento del telefono"));
            p.Controls.Add(Note("Premi Avvia abbinamento (salva e attiva il controllo remoto), poi scrivi al bot entro 5 minuti " +
                                "/abbina seguito dal codice. Il PC chiede conferma prima di accettare.", w));
            btnPair.Text = "Avvia abbinamento";
            btnPair.AutoSize = true;
            btnPair.Click += delegate { StartPairing(); };
            btnUnpair.Text = "Rimuovi abbinamento";
            btnUnpair.AutoSize = true;
            btnUnpair.Click += delegate
            {
                if (MessageBox.Show(this, "Rimuovere il telefono abbinato? Servirà un nuovo abbinamento e un nuovo codice Authenticator.",
                                    "WinUsage", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                service.Unpair();
                totpSecret = "";
                totpVerified = false;
                totpChanged = false;
                RefreshTotp();
                RefreshState();
            };
            p.Controls.Add(Row(btnPair, btnUnpair));
            lblPairCode.AutoSize = true;
            lblPairCode.Font = new Font(FontFamily.GenericMonospace, 20f, FontStyle.Bold);
            lblPairCode.Margin = new Padding(3, 8, 3, 0);
            p.Controls.Add(lblPairCode);
            lblPairInfo.AutoSize = true;
            lblPairInfo.MaximumSize = new Size(w, 0);
            p.Controls.Add(lblPairInfo);

            p.Controls.Add(Title("Privacy e sicurezza"));
            p.Controls.Add(Note("I messaggi del bot passano dai server di Telegram senza cifratura end-to-end: Telegram può " +
                                "leggere comandi, screenshot, app in uso e orari di accensione. Chi controlla il tuo account " +
                                "Telegram può vedere il tuo schermo: attiva la verifica in due passaggi di Telegram. " +
                                "Se perdi il telefono, revoca il token da @BotFather (/revoke). Se il PC lo usano anche altre " +
                                "persone, avvisale: catturarne lo schermo senza dirlo può violare la loro privacy.", w));
            return tab;
        }

        private TabPage BuildCodeTab()
        {
            TabPage tab = new TabPage("Codice");
            FlowLayoutPanel p = Page(tab);
            int w = 560;
            p.Controls.Add(Title("Codice dell'app Authenticator"));
            p.Controls.Add(Note("Serve per i comandi protetti (di solito /blocca e /spegni). Genera un segreto, inquadra il QR con " +
                                "Google Authenticator, Microsoft Authenticator o un'app simile, poi scrivi qui il codice che " +
                                "mostra per verificarlo. Il PC non chiede mai il codice in chat: lo scrivi tu nei comandi.", w));
            lblTotp.AutoSize = true;
            lblTotp.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            p.Controls.Add(lblTotp);
            Button gen = new Button();
            gen.Text = "Genera nuovo segreto";
            gen.AutoSize = true;
            gen.Click += delegate
            {
                if (totpVerified && MessageBox.Show(this, "Il codice attuale smetterà di funzionare. Continuare?", "WinUsage",
                                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                totpSecret = Base32.Encode(Totp.NewSecret());
                totpVerified = false;
                totpChanged = true;
                RefreshTotp();
            };
            p.Controls.Add(gen);
            picQr.SizeMode = PictureBoxSizeMode.AutoSize;
            picQr.Margin = new Padding(3, 8, 3, 4);
            p.Controls.Add(picQr);
            lblSecret.AutoSize = true;
            lblSecret.Font = new Font(FontFamily.GenericMonospace, 10f);
            p.Controls.Add(lblSecret);
            txtVerify.Width = 90;
            txtVerify.MaxLength = 6;
            Button check = new Button();
            check.Text = "Verifica codice";
            check.AutoSize = true;
            check.Click += delegate
            {
                byte[] secret = Base32.Decode(totpSecret);
                if (secret != null && Totp.Verify(secret, txtVerify.Text.Trim(), DateTime.UtcNow, -1) >= 0)
                {
                    totpVerified = true;
                    totpChanged = true;
                    txtVerify.Clear();
                    RefreshTotp();
                }
                else MessageBox.Show(this, "Codice non valido. Controlla che l'ora del PC e del telefono siano giuste e riprova.",
                                     "WinUsage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            p.Controls.Add(Row(Inline("Codice mostrato dall'app:"), txtVerify, check));
            p.Controls.Add(Note("Ricordati di premere Salva.", w));
            return tab;
        }

        private TabPage BuildCommandsTab(RemoteSecrets s)
        {
            TabPage tab = new TabPage("Comandi");
            FlowLayoutPanel p = Page(tab);
            int w = 560;
            p.Controls.Add(Title("Richiedono il codice dell'Authenticator"));
            p.Controls.Add(Check(chkCodeScreenshot, "/screenshot", s.CodeScreenshot));
            p.Controls.Add(Check(chkCodeApp, "/app (applicazione in uso)", s.CodeApp));
            p.Controls.Add(Check(chkCodeLock, "/blocca", s.CodeLock));
            p.Controls.Add(Check(chkCodeShutdown, "/spegni", s.CodeShutdown));

            p.Controls.Add(Title("Spegnimento"));
            p.Controls.Add(Row(Inline("Conto alla rovescia predefinito:"),
                               Number(numShutdown, RemoteSettings.MinShutdownMinutes, RemoteSettings.MaxShutdownMinutes, working.ShutdownDefaultMinutes),
                               Inline("minuti")));
            p.Controls.Add(Note("Lo spegnimento non è mai forzato: un documento non salvato lo ferma.", w));

            p.Controls.Add(Title("Screenshot"));
            p.Controls.Add(Row(Inline("Cancella lo screenshot dalla chat dopo"), Number(numDelete, 0, 2880, working.ScreenshotDeleteMinutes),
                               Inline("minuti (0 = mai)")));
            txtExclusions.Width = 260;
            txtExclusions.Text = s.ScreenshotExclusions;
            p.Controls.Add(Row(Inline("Non catturare con in primo piano:"), txtExclusions));
            p.Controls.Add(Note("Nomi dei processi separati da virgola (es. gestori di password).", w));

            p.Controls.Add(Title("Comandi in ritardo"));
            p.Controls.Add(Row(Inline("Scarta i comandi più vecchi di"), Number(numMaxAge, 30, 3600, working.MaxAgeSeconds), Inline("secondi")));
            p.Controls.Add(Note("Telegram conserva i messaggi a PC spento: un /spegni mandato ore prima non deve partire al risveglio.", w));
            return tab;
        }

        private TabPage BuildNotifyTab()
        {
            TabPage tab = new TabPage("Messaggi e notifiche");
            FlowLayoutPanel p = Page(tab);
            int w = 560;
            p.Controls.Add(Title("Messaggi al telefono"));
            p.Controls.Add(Check(chkMsgOn, "Accensione (accesso a Windows)", working.MsgPowerOn));
            p.Controls.Add(Check(chkMsgOff, "Spegnimento, riavvio, disconnessione e chiusura di WinUsage", working.MsgPowerOff));
            p.Controls.Add(Check(chkMsgCrash, "Spegnimento improvviso (corrente, blocco)", working.MsgCrash));
            p.Controls.Add(Check(chkMsgSleep, "Standby e ripresa", working.MsgSleep));
            p.Controls.Add(Check(chkMsgLock, "Blocco e sblocco dello schermo", working.MsgLock));

            p.Controls.Add(Title("Notifiche su questo PC (pixel art)"));
            p.Controls.Add(Check(chkNTray, "Indicatore sull'icona quando il controllo remoto è attivo", working.NotifyTray));
            p.Controls.Add(Check(chkNScreenshot, "Quando viene inviato uno screenshot", working.NotifyScreenshot));
            p.Controls.Add(Check(chkNApp, "Quando il telefono chiede l'app in uso", working.NotifyApp));
            p.Controls.Add(Check(chkNChat, "Messaggi arrivati con la chat chiusa (altrimenti la chat si apre da sola)", working.NotifyChat));
            p.Controls.Add(Check(chkNLocked, "\"Bloccato da remoto\" allo sblocco", working.NotifyLocked));
            p.Controls.Add(Check(chkNCountdown, "Conto alla rovescia dello spegnimento con Annulla", working.NotifyCountdown));
            p.Controls.Add(Check(chkSounds, "Suoni 8 bit", working.Sounds));
            p.Controls.Add(Note("Gli avvisi di sicurezza si vedono sempre. Il registro dei comandi è nella scheda Registro.", w));
            return tab;
        }

        private TabPage BuildLogTab()
        {
            TabPage tab = new TabPage("Registro");
            logView.Dock = DockStyle.Fill;
            logView.View = View.Details;
            logView.FullRowSelect = true;
            logView.Columns.Add("Ora", 120);
            logView.Columns.Add("Mittente", 90);
            logView.Columns.Add("Comando", 150);
            logView.Columns.Add("Esito", 260);
            Button refresh = new Button();
            refresh.Text = "Aggiorna";
            refresh.AutoSize = true;
            refresh.Dock = DockStyle.Bottom;
            refresh.Click += delegate { LoadLog(); };
            tab.Controls.Add(logView);
            tab.Controls.Add(refresh);
            tab.Enter += delegate { LoadLog(); };
            return tab;
        }

        private void LoadLog()
        {
            logView.BeginUpdate();
            logView.Items.Clear();
            foreach (RemoteLogEntry e in service.RecentLog(300))
            {
                ListViewItem it = new ListViewItem(e.TimeUtc.ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture));
                it.SubItems.Add(e.SenderId == 0 ? "PC" : e.SenderId.ToString(CultureInfo.InvariantCulture));
                it.SubItems.Add(e.Command);
                it.SubItems.Add(e.Result);
                logView.Items.Add(it);
            }
            logView.EndUpdate();
        }

        // ---- stato ----

        public void RefreshState()
        {
            if (IsDisposed) return;
            RemoteSecrets s = service.SecretsCopy;
            string status;
            Color color = Color.DarkGreen;
            if (service.Damaged) { status = "⚠ Configurazione danneggiata: reinserisci il token e salva"; color = Color.DarkRed; }
            else if (!s.HasToken) { status = "Non configurato"; color = SystemColors.GrayText; }
            else if (!service.Enabled) { status = "Disattivato"; color = SystemColors.GrayText; }
            else if (!s.Paired) { status = "In ascolto: nessun telefono abbinato"; color = Color.DarkOrange; }
            else status = "✔ Abbinato a " + s.UserName;
            lblStatus.Text = status;
            lblStatus.ForeColor = color;
            btnUnpair.Enabled = s.Paired;
            btnPair.Enabled = !s.Paired;
            if (chkEnabled.Checked != service.Enabled && !chkEnabled.Focused && !service.Enabled && working.Enabled)
            {
                chkEnabled.Checked = false;   // disattivato dal telefono con /stop
                working.Enabled = false;
            }

            if (s.Paired) { lblPairCode.Text = ""; lblPairInfo.Text = ""; }
            else if (service.PairingOpen)
            {
                int left = (int)Math.Max(0, (service.PairingUntilUtc - DateTime.UtcNow).TotalSeconds);
                lblPairInfo.Text = "Scrivi al bot:  /abbina " + lblPairCode.Text.Trim() + "   (scade tra " + (left / 60) + ":" +
                                   (left % 60).ToString("00", CultureInfo.InvariantCulture) + ")";
            }
            else if (lblPairCode.Text.Length > 0)
            {
                lblPairCode.Text = "";
                lblPairInfo.Text = "Abbinamento scaduto o chiuso: premi di nuovo Avvia abbinamento.";
            }
        }

        private void RefreshTotp()
        {
            byte[] secret = Base32.Decode(totpSecret);
            if (secret == null || secret.Length < 10)
            {
                lblTotp.Text = "Non configurato";
                lblTotp.ForeColor = SystemColors.GrayText;
                SetQr(null);
                lblSecret.Text = "";
                return;
            }
            lblTotp.Text = totpVerified ? "✔ Configurato e verificato" + (totpChanged ? " (premi Salva)" : "")
                                        : "Da verificare: inquadra il QR e scrivi il codice";
            lblTotp.ForeColor = totpVerified ? Color.DarkGreen : Color.DarkOrange;
            if (totpChanged && !totpVerified)
            {
                SetQr(QrCode.Encode(Totp.ProvisioningUri(secret, Environment.MachineName)));
                string b32 = totpSecret;
                System.Text.StringBuilder sb = new System.Text.StringBuilder("Chiave per l'inserimento manuale: ");
                for (int i = 0; i < b32.Length; i += 4) sb.Append(b32.Substring(i, Math.Min(4, b32.Length - i))).Append(' ');
                lblSecret.Text = sb.ToString().TrimEnd();
            }
            else
            {
                SetQr(null);   // il segreto già configurato non si mostra più
                lblSecret.Text = "";
            }
        }

        private void SetQr(bool[,] m)
        {
            if (picQr.Image != null) { picQr.Image.Dispose(); picQr.Image = null; }
            if (m == null) { picQr.Visible = false; return; }
            int n = m.GetLength(0), quiet = 4, px = 5;
            Bitmap b = new Bitmap((n + 2 * quiet) * px, (n + 2 * quiet) * px);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (m[y, x]) g.FillRectangle(Brushes.Black, (x + quiet) * px, (y + quiet) * px, px, px);
            }
            picQr.Image = b;
            picQr.Visible = true;
        }

        // ---- salvataggio ----

        private bool SaveAll()
        {
            string token = txtToken.Text.Trim();
            if (token.Length > 0 && token.IndexOf(':') < 0)
            {
                MessageBox.Show(this, "Il token non sembra valido (ha la forma 123456789:ABC...).", "WinUsage",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            RemoteSecrets s = service.SecretsCopy;   // l'abbinamento lo gestisce il servizio: si parte dalla sua copia
            s.Token = token;
            if (totpChanged && totpVerified)   // un segreto nuovo non verificato non sostituisce quello attuale
            {
                s.TotpSecret = totpSecret;
                s.TotpVerified = true;
            }
            s.CodeScreenshot = chkCodeScreenshot.Checked;
            s.CodeApp = chkCodeApp.Checked;
            s.CodeLock = chkCodeLock.Checked;
            s.CodeShutdown = chkCodeShutdown.Checked;
            s.ScreenshotExclusions = txtExclusions.Text.Trim();

            working.Enabled = chkEnabled.Checked && token.Length > 0;
            working.ShutdownDefaultMinutes = (int)numShutdown.Value;
            working.ScreenshotDeleteMinutes = (int)numDelete.Value;
            working.MaxAgeSeconds = (int)numMaxAge.Value;
            working.MsgPowerOn = chkMsgOn.Checked;
            working.MsgPowerOff = chkMsgOff.Checked;
            working.MsgCrash = chkMsgCrash.Checked;
            working.MsgSleep = chkMsgSleep.Checked;
            working.MsgLock = chkMsgLock.Checked;
            working.NotifyTray = chkNTray.Checked;
            working.NotifyScreenshot = chkNScreenshot.Checked;
            working.NotifyApp = chkNApp.Checked;
            working.NotifyChat = chkNChat.Checked;
            working.NotifyLocked = chkNLocked.Checked;
            working.NotifyCountdown = chkNCountdown.Checked;
            working.Sounds = chkSounds.Checked;

            if (totpChanged && !totpVerified && Base32.Decode(totpSecret) != null && totpSecret.Length > 0)
            {
                if (MessageBox.Show(this, "Il nuovo segreto dell'Authenticator non è stato verificato e non verrà salvato. Continuare?",
                                    "WinUsage", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            }

            try
            {
                service.ApplyConfig(s, working.Clone());
                if (totpVerified) totpChanged = false;
                RefreshTotp();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                MessageBox.Show(this, "Salvataggio non riuscito: " + ex.Message, "WinUsage", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void StartPairing()
        {
            if (txtToken.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Prima inserisci il token del bot.", "WinUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            chkEnabled.Checked = true;
            if (!SaveAll()) return;
            lblPairCode.Text = service.StartPairing();
            RefreshState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pairTimer.Stop();
                pairTimer.Dispose();
                if (picQr.Image != null) picQr.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
