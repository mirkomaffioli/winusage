using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Chat con i telefoni abbinati, e solo con loro: il destinatario non si può cambiare. Con più telefoni è una chat di
    /// gruppo: quello che si scrive qui arriva a tutti, e i messaggi in arrivo mostrano il nome di chi li ha scritti. I messaggi stanno solo in memoria:
    /// con la cronologia attiva (predefinito) la finestra riparte da quelli già scambiati finché WinUsage resta aperto,
    /// senza cronologia spariscono alla chiusura della finestra. Stile pixel art come le notifiche.
    /// </summary>
    internal sealed class RemoteChatForm : Form
    {
        private static readonly Color Deep = Color.FromArgb(10, 36, 80);
        private static readonly Color Navy = Color.FromArgb(11, 29, 58);

        private readonly Panel scroller = new Panel();
        private readonly FlowLayoutPanel list = new FlowLayoutPanel();
        private readonly TextBox input = new TextBox();
        private readonly PixelButton send = new PixelButton(L.T("INVIA", "SEND"));
        private readonly PixelButton close = new PixelButton(L.T("CHIUDI", "CLOSE"));
        private readonly EmojiButton emoji;
        private string userName;
        private string groupNames;           // null = un telefono solo
        private readonly HeaderPanel header;
        private bool showInactive;

        /// <summary>Testo da inviare; il gestore restituisce false se non è stato possibile.</summary>
        public event Func<string, bool> SendRequested;

        /// <param name="groupNames">Nomi dei telefoni se sono più d'uno (chat di gruppo), altrimenti null.</param>
        public RemoteChatForm(string userName, Icon icon, bool keepHistory, string groupNames)
        {
            this.userName = string.IsNullOrEmpty(userName) ? L.T("telefono", "phone") : userName;
            this.groupNames = groupNames;
            Text = L.T("WinUsage - Chat con il telefono", "WinUsage - Chat with the phone");
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

            header = new HeaderPanel(this.userName, groupNames != null);
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
            // CHIUDI: per chi non vuole chattare. Il telefono viene avvisato (come chiudendo con la X).
            close.Dock = DockStyle.Left;
            close.BackColor = Navy;
            close.Width = 84;
            close.Danger = true;
            close.Click += delegate { Close(); };
            Panel gapLeft = new Panel();
            gapLeft.Dock = DockStyle.Left;
            gapLeft.Width = 8;
            emoji = new EmojiButton(input);
            emoji.Dock = DockStyle.Left;
            emoji.BackColor = Navy;
            bottom.Controls.Add(input);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(send);
            bottom.Controls.Add(emoji);
            bottom.Controls.Add(gapLeft);
            bottom.Controls.Add(close);

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

            AddNote((groupNames != null
                        ? L.T("Chat con tutti i telefoni abbinati (" + groupNames + "): quello che scrivi arriva a tutti. ",
                              "Chat with all paired phones (" + groupNames + "): what you write reaches everyone. ")
                        : L.T("Chat solo con " + this.userName + ". ", "Chat only with " + this.userName + ". ")) + (keepHistory
                ? L.T("I messaggi restano finché WinUsage è aperto (solo in memoria): spariscono al riavvio.",
                      "Messages stay while WinUsage is open (in memory only): they disappear on restart.")
                : L.T("Nessuna cronologia: i messaggi spariscono alla chiusura.", "No history: messages disappear when the window closes.")));
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

        /// <summary>Testo già scritto altrove (barra "rispondi" della notifica), da continuare qui.</summary>
        public void SetDraft(string text)
        {
            if (string.IsNullOrEmpty(text) || input.TextLength > 0) return;
            input.Text = text;
            input.SelectionStart = input.TextLength;
        }

        /// <param name="sender">Nome del telefono, mostrato sopra il messaggio (null con un telefono solo).</param>
        public void AddIncoming(string text, DateTime sentUtc, string sender)
        {
            AddBubble(new ChatBubble(text, false, sentUtc.ToLocalTime(), sender));
        }

        /// <summary>I telefoni abbinati sono cambiati mentre la chat è aperta.</summary>
        public void SetPhones(string userName, string groupNames)
        {
            this.userName = string.IsNullOrEmpty(userName) ? L.T("telefono", "phone") : userName;
            this.groupNames = groupNames;
            header.SetUser(this.userName, groupNames != null);
            AddNote(groupNames != null ? L.T("Telefoni nella chat: ", "Phones in the chat: ") + groupNames + "."
                                        : L.T("Ora la chat è solo con ", "The chat is now only with ") + this.userName + ".");
        }

        /// <summary>Messaggi già scambiati (cronologia), dal più vecchio.</summary>
        public void LoadHistory(System.Collections.Generic.List<ChatEntry> entries)
        {
            if (entries.Count == 0) return;
            list.SuspendLayout();
            ChatBubble last = null;
            foreach (ChatEntry e in entries)
            {
                last = new ChatBubble(e.Text, e.Outgoing, e.Utc.ToLocalTime(), groupNames != null ? e.Sender : null);
                list.Controls.Add(last);
            }
            list.ResumeLayout();
            Relayout();
            if (last != null)
            {
                ChatBubble target = last;
                Shown += delegate { scroller.ScrollControlIntoView(target); };
            }
        }

        private void DoSend()
        {
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            Func<string, bool> h = SendRequested;
            bool ok = h != null && h(text);
            if (ok)
            {
                AddBubble(new ChatBubble(text, true, DateTime.Now, null));
                input.Clear();
            }
            else AddNote(L.T("Messaggio non inviato: controllo remoto non attivo.", "Message not sent: remote control is off."));
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
            private string user;
            private bool group;
            private Bitmap sea;
            private readonly Bitmap shell = PixelArt.ToBitmap(RemoteSprites.Shell);

            public HeaderPanel(string user, bool group)
            {
                this.user = user;
                this.group = group;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            public void SetUser(string name, bool group)
            {
                user = name;
                this.group = group;
                if (sea != null) { sea.Dispose(); sea = null; }   // ridisegna il fondale con il nuovo nome
                Invalidate();
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
                        PixelFont.DrawShadow(g, group ? L.T("CHAT CON I TELEFONI", "CHAT WITH THE PHONES") : L.T("CHAT CON IL TELEFONO", "CHAT WITH THE PHONE"), 28, 7, Color.FromArgb(255, 214, 90), Color.FromArgb(138, 90, 26), 1);
                        string shown = user.ToUpperInvariant();
                        PixelFont.DrawShadow(g, NotifyArt.Fit(PixelFont.CanDraw(shown) ? shown : L.T("TELEFONO ABBINATO", "PAIRED PHONE"), lw - 34), 28, 17,
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
            private readonly string sender;     // nome sopra il testo (chat di gruppo), o null
            private const int Pad = 8;
            private const int SenderHeight = 15;

            public ChatBubble(string text, bool outgoing, DateTime local, string sender)
            {
                Text = text;
                this.outgoing = outgoing;
                this.sender = string.IsNullOrEmpty(sender) ? null : sender;
                // messaggi di un altro giorno (cronologia): anche la data
                time = local.ToString(local.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm", CultureInfo.InvariantCulture);
                DoubleBuffered = true;
                Font = SystemFonts.MessageBoxFont;
            }

            public void FitTo(int width)
            {
                int maxText = (int)(width * 0.78) - 2 * Pad;
                Size sz = ColorText.Measure(Text, Font, maxText);
                int top = sender != null ? SenderHeight : 0;
                int nameWidth = sender != null ? Math.Min(maxText, TextRenderer.MeasureText(sender, SenderFont()).Width) : 0;
                int w = Math.Max(Math.Max(sz.Width, nameWidth), 40) + 2 * Pad;
                int h = sz.Height + 2 * Pad + 12 + top;
                Size = new Size(w, h);
                Margin = new Padding(outgoing ? width - w : 0, 0, 0, 8);
            }

            private Font senderFont;

            private Font SenderFont()
            {
                if (senderFont == null) senderFont = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold);
                return senderFont;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && senderFont != null) senderFont.Dispose();
                base.Dispose(disposing);
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
                int top = 0;
                if (sender != null)
                {
                    top = SenderHeight;
                    TextRenderer.DrawText(g, sender, SenderFont(), new Rectangle(Pad, Pad - 2, Width - 2 * Pad, SenderHeight),
                                          Color.FromArgb(10, 70, 80), TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
                Rectangle tr = new Rectangle(Pad, Pad + top, Width - 2 * Pad, Height - 2 * Pad - 12 - top);
                ColorText.Draw(g, Text, Font, tr, text, fill, false);   // emoji a colori dove possibile
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
