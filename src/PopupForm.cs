using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestrella mostrata con il tasto sinistro sull'icona: tempo di utilizzo e totali dei tre timer.</summary>
    internal sealed class PopupForm : Form
    {
        private readonly UsageTracker tracker;
        private readonly Label big;
        private readonly Label[] sessionCells = new Label[TimerIds.Count];
        private readonly Label[] todayCells = new Label[TimerIds.Count];
        private readonly Timer refresh;

        public PopupForm(UsageTracker tracker)
        {
            this.tracker = tracker;

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
            title.Text = "Stai usando il computer da";
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
            t.Controls.Add(Header("Da avvio"), 1, 2);
            t.Controls.Add(Header("Oggi"), 2, 2);

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
            note.Text = "Il tempo in standby non viene conteggiato.";
            note.AutoSize = true;
            note.ForeColor = SystemColors.GrayText;
            note.Margin = new Padding(0, 10, 0, 0);
            t.Controls.Add(note, 0, 3 + TimerIds.Count);
            t.SetColumnSpan(note, 3);

            Controls.Add(t);

            refresh = new Timer();
            refresh.Interval = 1000;
            refresh.Tick += delegate { UpdateValues(); };

            Deactivate += delegate { Hide(); };
            VisibleChanged += delegate
            {
                if (Visible) { UpdateValues(); refresh.Start(); }
                else refresh.Stop();
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
            Show();
            PerformLayout();
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
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
            base.Dispose(disposing);
        }
    }
}
