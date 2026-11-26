using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestrella mostrata con il tasto sinistro sull'icona: tempo di utilizzo e totali dei tre timer.</summary>
    internal sealed class PopupForm : Form
    {
        private readonly UsageTracker tracker;
        private readonly Func<bool> chatAvailable;
        private readonly PixelButton chatButton;
        private readonly Label big;
        private readonly Label[] sessionCells = new Label[TimerIds.Count];
        private readonly Label[] todayCells = new Label[TimerIds.Count];
        private readonly Timer refresh;
        private readonly Timer watch;
        private DateTime shownAt = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public PopupForm(UsageTracker tracker, Func<bool> chatAvailable, Action openChat)
        {
            this.tracker = tracker;
            this.chatAvailable = chatAvailable;

            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            Padding = new Padding(18, 14, 18, 14);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            DoubleBuffered = true;

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 3;
            t.BackColor = Color.Transparent;

            Label title = new Label();
            title.Text = L.T("Stai usando il computer da", "You have been using the computer for");
            title.AutoSize = true;
            title.ForeColor = SystemColors.GrayText;
            t.Controls.Add(title, 0, 0);
            t.SetColumnSpan(title, 3);

            big = new Label();
            big.AutoSize = true;
            big.Font = new Font(Font.FontFamily, 24f, FontStyle.Bold);
            big.Margin = new Padding(0, 0, 0, 10);
            t.Controls.Add(big, 0, 1);
            t.SetColumnSpan(big, 3);

            t.Controls.Add(Header(""), 0, 2);
            t.Controls.Add(Header(L.T("Da avvio", "Since start")), 1, 2);
            t.Controls.Add(Header(L.T("Oggi", "Today")), 2, 2);

            for (int i = 0; i < TimerIds.Count; i++)
            {
                Label name = new Label();
                name.Text = TimerIds.Labels[i];
                name.AutoSize = true;
                name.Margin = new Padding(0, 3, 24, 3);
                t.Controls.Add(name, 0, 3 + i);

                sessionCells[i] = ValueCell();
                todayCells[i] = ValueCell();
                t.Controls.Add(sessionCells[i], 1, 3 + i);
                t.Controls.Add(todayCells[i], 2, 3 + i);
            }

            Label note = new Label();
            note.Text = L.T("Il tempo in standby non viene conteggiato.", "Time in standby is not counted.");
            note.AutoSize = true;
            note.ForeColor = SystemColors.GrayText;
            note.Margin = new Padding(0, 10, 0, 0);
            t.Controls.Add(note, 0, 3 + TimerIds.Count);
            t.SetColumnSpan(note, 3);

            // Chat con il telefono abbinato (solo se il controllo remoto è attivo)
            chatButton = new PixelButton("CHAT");
            chatButton.Size = new Size(120, 32);
            chatButton.Anchor = AnchorStyles.None;
            chatButton.Margin = new Padding(0, 12, 0, 0);
            chatButton.BackColor = BackColor;
            chatButton.Click += delegate
            {
                Hide();
                if (openChat != null) openChat();
            };
            t.Controls.Add(chatButton, 0, 4 + TimerIds.Count);
            t.SetColumnSpan(chatButton, 3);

            Controls.Add(t);

            refresh = new Timer();
            refresh.Interval = 1000;
            refresh.Tick += delegate { UpdateValues(); };

            // Chiusura quando si clicca altrove. Su Windows 11, se l'icona è nell'area delle icone nascoste (la
            // freccia ^ della tray), quel pannello si richiude subito dopo il clic e porta via il focus al popup:
            // nei primi istanti la perdita del focus si ignora e il popup si riprende il primo piano.
            Deactivate += delegate
            {
                if ((DateTime.UtcNow - shownAt).TotalMilliseconds < 800)
                {
                    BeginInvoke(new MethodInvoker(BringToForeground));
                    return;
                }
                Hide();
            };

            // Rete di sicurezza: se Windows non concede il primo piano, Deactivate non arriva mai.
            // Si chiude comunque al primo clic fuori dal popup.
            watch = new Timer();
            watch.Interval = 100;
            watch.Tick += delegate
            {
                if (!Visible || (DateTime.UtcNow - shownAt).TotalMilliseconds < 800) return;
                if (GetForegroundWindow() != Handle && Control.MouseButtons != MouseButtons.None &&
                    !Bounds.Contains(Cursor.Position))
                    Hide();
            };

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };

            VisibleChanged += delegate
            {
                if (Visible) { UpdateValues(); refresh.Start(); watch.Start(); }
                else { refresh.Stop(); watch.Stop(); }
            };
        }

        private static Label Header(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            l.Anchor = AnchorStyles.Right;
            l.Margin = new Padding(8, 0, 0, 0);
            return l;
        }

        private static Label ValueCell()
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Right;
            l.Margin = new Padding(8, 3, 0, 3);
            return l;
        }

        private void UpdateValues()
        {
            bool chat = chatAvailable != null && chatAvailable();
            if (chatButton.Visible != chat) chatButton.Visible = chat;
            big.Text = Fmt.Clock(tracker.Session(TimerIds.System));
            for (int i = 0; i < TimerIds.Count; i++)
            {
                sessionCells[i].Text = Fmt.Compact(tracker.Session(i));
                todayCells[i].Text = Fmt.Compact(tracker.Today(i));
            }
        }

        /// <summary>Mostra il popup nell'angolo in basso a destra dell'area di lavoro, vicino alla tray.</summary>
        public void ShowNearTray()
        {
            UpdateValues();
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle wa = screen.WorkingArea;
            shownAt = DateTime.UtcNow;
            Show();
            PerformLayout();
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            BringToForeground();
        }

        private void BringToForeground()
        {
            if (!Visible || IsDisposed) return;
            SetForegroundWindow(Handle);
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(SystemColors.ControlDark))
            {
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing && refresh != null) refresh.Dispose();
            if (disposing && watch != null) watch.Dispose();
            base.Dispose(disposing);
        }
    }
}
