using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Chat con il telefono abbinato, e solo con lui: il destinatario non si può cambiare. Nessuna cronologia: i messaggi
    /// restano solo in memoria e spariscono alla chiusura della finestra. Stile pixel art come le notifiche.
    /// </summary>
    internal sealed class RemoteChatForm : Form
    {
        private static readonly Color Deep = Color.FromArgb(10, 36, 80);
        private static readonly Color Navy = Color.FromArgb(11, 29, 58);

        private readonly Panel scroller = new Panel();
        private readonly FlowLayoutPanel list = new FlowLayoutPanel();
        private readonly TextBox input = new TextBox();
        private readonly PixelButton send = new PixelButton("INVIA");
        private readonly string userName;
        private bool showInactive;

        /// <summary>Testo da inviare; il gestore restituisce false se non è stato possibile.</summary>
        public event Func<string, bool> SendRequested;

        public RemoteChatForm(string userName, Icon icon)
        {
            this.userName = string.IsNullOrEmpty(userName) ? "telefono" : userName;
            Text = "WinUsage - Chat con il telefono";
            if (icon != null) Icon = icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(400, 540);
            MinimumSize = new Size(320, 360);
            BackColor = Deep;
            DoubleBuffered = true;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);

            Panel header = new HeaderPanel(this.userName);
            header.Dock = DockStyle.Top;
            header.Height = 64;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 44;
            bottom.Padding = new Padding(10, 7, 10, 7);
            bottom.BackColor = Navy;
            input.Dock = DockStyle.Fill;
            input.MaxLength = 2000;
            input.BorderStyle = BorderStyle.FixedSingle;
            input.BackColor = Color.FromArgb(234, 248, 255);
            input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSend(); }
            };
            send.Dock = DockStyle.Right;
            send.BackColor = Navy;
            send.Width = 84;
            send.Click += delegate { DoSend(); };
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = 8;
            bottom.Controls.Add(input);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(send);

            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.BackColor = Deep;
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.AutoSize = true;
            list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            list.Location = new Point(0, 0);
            list.Padding = new Padding(8, 8, 8, 8);
            list.BackColor = Deep;
            scroller.Controls.Add(list);
            scroller.Resize += delegate { Relayout(); };

            Controls.Add(scroller);
            Controls.Add(bottom);
            Controls.Add(header);

            AddNote("Chat solo con " + this.userName + ". Nessuna cronologia: i messaggi spariscono alla chiusura.");
            Shown += delegate { input.Focus(); };
        }

        public void ShowChat(bool activate)
        {
            if (!Visible)
            {
                showInactive = !activate;
                Show();
                showInactive = false;
            }
            else if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            if (activate) { Activate(); input.Focus(); }
        }

        protected override bool ShowWithoutActivation { get { return showInactive; } }

        public void AddIncoming(string text, DateTime sentUtc)
        {
            AddBubble(new ChatBubble(text, false, sentUtc.ToLocalTime()));
        }

        private void DoSend()
        {
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            Func<string, bool> h = SendRequested;
            bool ok = h != null && h(text);
            if (ok)
            {
                AddBubble(new ChatBubble(text, true, DateTime.Now));
                input.Clear();
            }
            else AddNote("Messaggio non inviato: controllo remoto non attivo.");
        }

        private void AddNote(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Color.FromArgb(124, 200, 240);
            l.AutoSize = false;
            l.Margin = new Padding(0, 0, 0, 8);
            l.Tag = "note";
            list.Controls.Add(l);
            Relayout();
        }

        private void AddBubble(ChatBubble b)
        {
            list.Controls.Add(b);
            Relayout();
            scroller.ScrollControlIntoView(b);
        }

        private void Relayout()
        {
            int width = Math.Max(200, scroller.ClientSize.Width - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            list.SuspendLayout();
            foreach (Control c in list.Controls)
            {
                ChatBubble b = c as ChatBubble;
                if (b != null) b.FitTo(width);
                else
                {
                    c.Width = width;
                    c.Height = TextRenderer.MeasureText(c.Text, c.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height + 2;
                }
            }
            list.ResumeLayout();
        }

        /// <summary>Intestazione disegnata in pixel art: fondale, conchiglia e titolo nel font 5x7.</summary>
        private sealed class HeaderPanel : Panel
        {
            private readonly string user;
            private Bitmap sea;
            private readonly Bitmap shell = PixelArt.ToBitmap(RemoteSprites.Shell);

            public HeaderPanel(string user)
            {
                this.user = user;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                int scale = 2;
                int lw = Math.Max(40, Width / scale), lh = Math.Max(16, Height / scale);
                if (sea == null || sea.Width != lw || sea.Height != lh)
                {
                    if (sea != null) sea.Dispose();
                    sea = ToastArt.BuildSea(lw, lh);
                    using (Graphics g = Graphics.FromImage(sea))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(shell, new Rectangle(6, 7, 16, 16), 0, 0, 16, 16, GraphicsUnit.Pixel);
                        PixelFont.DrawShadow(g, "CHAT CON IL TELEFONO", 28, 7, Color.FromArgb(255, 214, 90), Color.FromArgb(138, 90, 26), 1);
                        PixelFont.DrawShadow(g, NotifyArt.Fit(PixelFont.CanDraw(user) ? user : "TELEFONO ABBINATO", lw - 34), 28, 17,
                                             Color.FromArgb(190, 235, 255), Color.FromArgb(8, 24, 52), 1);
                    }
                }
                e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                PixelArt.DrawScaled(e.Graphics, sea, new Rectangle(0, 0, lw * scale, lh * scale));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { if (sea != null) sea.Dispose(); shell.Dispose(); }
                base.Dispose(disposing);
            }
        }

        /// <summary>Fumetto con angoli "a gradini" da pixel art: a destra i messaggi inviati, a sinistra quelli ricevuti.</summary>
        private sealed class ChatBubble : Control
        {
            private readonly bool outgoing;
            private readonly string time;
            private const int Pad = 8;

            public ChatBubble(string text, bool outgoing, DateTime local)
            {
                Text = text;
                this.outgoing = outgoing;
                time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
                DoubleBuffered = true;
                Font = SystemFonts.MessageBoxFont;
            }

            public void FitTo(int width)
            {
                int maxText = (int)(width * 0.78) - 2 * Pad;
                Size sz = TextRenderer.MeasureText(Text, Font, new Size(maxText, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                int w = Math.Max(sz.Width, 40) + 2 * Pad;
                int h = sz.Height + 2 * Pad + 12;
                Size = new Size(w, h);
                Margin = new Padding(outgoing ? width - w : 0, 0, 0, 8);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color fill = outgoing ? Color.FromArgb(255, 214, 90) : Color.FromArgb(64, 196, 168);
                Color light = outgoing ? Color.FromArgb(255, 244, 196) : Color.FromArgb(150, 240, 216);
                Color text = Color.FromArgb(11, 29, 58);
                g.Clear(Parent != null ? Parent.BackColor : Deep);
                int px = 3, w = Width, h = Height;
                using (Brush navy = new SolidBrush(Navy))
                using (Brush f = new SolidBrush(fill))
                using (Brush l = new SolidBrush(light))
                {
                    g.FillRectangle(navy, px, 0, w - 2 * px, h);
                    g.FillRectangle(navy, 0, px, w, h - 2 * px);
                    g.FillRectangle(f, px, px, w - 2 * px, h - 2 * px);
                    g.FillRectangle(f, 2 * px, px / 2 + 1, w - 4 * px, h - px - 2);
                    g.FillRectangle(l, 2 * px, px, w - 4 * px, px / 2 + 1);
                }
                Rectangle tr = new Rectangle(Pad, Pad, Width - 2 * Pad, Height - 2 * Pad - 12);
                TextRenderer.DrawText(g, Text, Font, tr, text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                using (Font small = new Font(Font.FontFamily, Font.Size * 0.8f))
                    TextRenderer.DrawText(g, time, small, new Rectangle(Pad, Height - Pad - 13, Width - 2 * Pad, 13),
                                          Color.FromArgb(60, 70, 90), TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>Pulsante disegnato in pixel art (stesso stile dei pulsanti delle notifiche).</summary>
    internal sealed class PixelButton : Control
    {
        private bool pressed;
        public bool Danger { get; set; }

        public PixelButton(string label)
        {
            Text = label;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg = BackColor.A == 255 ? BackColor : SystemColors.Window;
            e.Graphics.Clear(bg);
            int scale = Math.Max(1, Math.Min(Height / 14, 3));
            int lw = Math.Max(10, Width / scale), lh = Math.Max(10, Height / scale);
            using (Bitmap b = new Bitmap(lw, lh))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(bg);
                    int bw = Math.Min(lw, PixelFont.Measure(Text, 1) + 10);
                    Rectangle r = new Rectangle((lw - bw) / 2, (lh - 13) / 2 + (pressed ? 1 : 0), bw, 13);
                    NotifyArt.DrawButton(g, r, Text, Danger);
                }
                e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                PixelArt.DrawScaled(e.Graphics, b, new Rectangle(0, 0, lw * scale, lh * scale));
            }
        }
    }
}
