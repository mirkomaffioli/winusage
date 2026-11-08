using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestra di configurazione (tasto destro sull'icona -> Configurazione).</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly AppSettings working;

        private readonly CheckBox chkAutoStart = new CheckBox();
        private readonly CheckBox chkNotify = new CheckBox();
        private readonly NumericUpDown numMinutes = new NumericUpDown();
        private readonly CheckBox chkFullscreen = new CheckBox();
        private readonly CheckBox chkBrowser = new CheckBox();
        private readonly TextBox txtBrowsers = new TextBox();
        private readonly CheckBox chkSteam = new CheckBox();

        public event Action<AppSettings> Saved;

        public SettingsForm(AppSettings current, string dbPath, Icon icon)
        {
            working = current;

            Text = "WinUsage - Configurazione";
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            if (icon != null) Icon = icon;

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // Generale
            root.Controls.Add(SectionTitle("Generale"));
            chkAutoStart.Text = "Avvia WinUsage con Windows";
            chkAutoStart.AutoSize = true;
            chkAutoStart.Checked = working.AutoStart;
            root.Controls.Add(chkAutoStart);

            // Notifiche
            root.Controls.Add(SectionTitle("Notifiche"));
            chkNotify.Text = "Avvisami a intervalli regolari: \"Stai usando il computer da ...\"";
            chkNotify.AutoSize = true;
            chkNotify.Checked = working.NotifyEnabled;
            chkNotify.CheckedChanged += delegate
            {
                numMinutes.Enabled = chkNotify.Checked;
                chkFullscreen.Enabled = chkNotify.Checked;
            };
            root.Controls.Add(chkNotify);

            FlowLayoutPanel rowMinutes = new FlowLayoutPanel();
            rowMinutes.AutoSize = true;
            rowMinutes.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            rowMinutes.Margin = new Padding(18, 0, 0, 0);
            Label l1 = new Label();
            l1.Text = "Ogni";
            l1.AutoSize = true;
            l1.Margin = new Padding(0, 6, 6, 0);
            numMinutes.Minimum = 1;
            numMinutes.Maximum = 1440;
            numMinutes.Width = 70;
            numMinutes.Value = Math.Max(1, Math.Min(1440, working.NotifyMinutes));
            numMinutes.Enabled = working.NotifyEnabled;
            Label l2 = new Label();
            l2.Text = "minuti di utilizzo";
            l2.AutoSize = true;
            l2.Margin = new Padding(6, 6, 0, 0);
            rowMinutes.Controls.Add(l1);
            rowMinutes.Controls.Add(numMinutes);
            rowMinutes.Controls.Add(l2);
            root.Controls.Add(rowMinutes);

            chkFullscreen.Text = "Mostra il messaggio anche sopra giochi e app a schermo intero (con un suono).\r\nSe disattivato, viene mostrato quando lo schermo intero finisce.";
            chkFullscreen.AutoSize = true;
            chkFullscreen.Checked = working.NotifyFullscreen;
            chkFullscreen.Enabled = working.NotifyEnabled;
            chkFullscreen.Margin = new Padding(18, 4, 3, 3);
            root.Controls.Add(chkFullscreen);

            // Timer
            root.Controls.Add(SectionTitle("Altri timer"));
            chkBrowser.Text = "Traccia l'utilizzo del browser (finestra in primo piano)";
            chkBrowser.AutoSize = true;
            chkBrowser.Checked = working.TrackBrowser;
            chkBrowser.CheckedChanged += delegate { txtBrowsers.Enabled = chkBrowser.Checked; };
            root.Controls.Add(chkBrowser);

            FlowLayoutPanel rowBrowsers = new FlowLayoutPanel();
            rowBrowsers.AutoSize = true;
            rowBrowsers.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            rowBrowsers.Margin = new Padding(18, 0, 0, 0);
            Label l3 = new Label();
            l3.Text = "Processi:";
            l3.AutoSize = true;
            l3.Margin = new Padding(0, 6, 6, 0);
            txtBrowsers.Width = 330;
            txtBrowsers.Text = working.BrowserProcesses;
            txtBrowsers.Enabled = working.TrackBrowser;
            rowBrowsers.Controls.Add(l3);
            rowBrowsers.Controls.Add(txtBrowsers);
            root.Controls.Add(rowBrowsers);

            chkSteam.Text = "Traccia l'utilizzo di Steam (giochi avviati da Steam e client in primo piano)";
            chkSteam.AutoSize = true;
            chkSteam.Checked = working.TrackSteam;
            root.Controls.Add(chkSteam);

            Label info = new Label();
            info.Text = "Dati e configurazione di questo utente:\r\n" + dbPath;
            info.AutoSize = true;
            info.ForeColor = SystemColors.GrayText;
            info.Margin = new Padding(3, 14, 3, 6);
            root.Controls.Add(info);

            // Pulsanti
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Button cancel = new Button();
            cancel.Text = "Annulla";
            cancel.AutoSize = true;
            cancel.DialogResult = DialogResult.Cancel;
            Button save = new Button();
            save.Text = "Salva";
            save.AutoSize = true;
            save.Click += OnSave;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
            cancel.Click += delegate { Close(); };
        }

        private static Label SectionTitle(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            l.Margin = new Padding(3, 10, 3, 4);
            return l;
        }

        private void OnSave(object sender, EventArgs e)
        {
            working.AutoStart = chkAutoStart.Checked;
            working.NotifyEnabled = chkNotify.Checked;
            working.NotifyMinutes = (int)numMinutes.Value;
            working.NotifyFullscreen = chkFullscreen.Checked;
            working.TrackBrowser = chkBrowser.Checked;
            string browsers = txtBrowsers.Text.Trim();
            working.BrowserProcesses = browsers.Length > 0 ? browsers : AppSettings.DefaultBrowsers;
            working.TrackSteam = chkSteam.Checked;

            Action<AppSettings> handler = Saved;
            if (handler != null) handler(working);
            Close();
        }
    }
}
