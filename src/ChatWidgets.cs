using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Emoji della tavolozza (code point; quelli con U+FE0F hanno anche il selettore della variante a colori).</summary>
    internal static class EmojiSet
    {
        private static readonly int[][] codes =
        {
            new[] { 0x1F600 }, new[] { 0x1F602 }, new[] { 0x1F923 }, new[] { 0x1F60A }, new[] { 0x1F607 }, new[] { 0x1F609 },
            new[] { 0x1F60D }, new[] { 0x1F618 }, new[] { 0x1F60E }, new[] { 0x1F914 }, new[] { 0x1F644 }, new[] { 0x1F634 },
            new[] { 0x1F622 }, new[] { 0x1F62D }, new[] { 0x1F621 }, new[] { 0x1F631 }, new[] { 0x1F917 }, new[] { 0x1F92A },
            new[] { 0x1F44D }, new[] { 0x1F44E }, new[] { 0x1F44C }, new[] { 0x1F64F }, new[] { 0x1F44F }, new[] { 0x1F44B },
            new[] { 0x1F4AA }, new[] { 0x270C, 0xFE0F }, new[] { 0x2764, 0xFE0F }, new[] { 0x1F494 }, new[] { 0x1F525 }, new[] { 0x1F389 },
            new[] { 0x2728 }, new[] { 0x2B50 }, new[] { 0x2705 }, new[] { 0x274C }, new[] { 0x26A0, 0xFE0F }, new[] { 0x23F0 },
            new[] { 0x2615 }, new[] { 0x1F355 }, new[] { 0x1F354 }, new[] { 0x1F35D }, new[] { 0x1F37A }, new[] { 0x1F382 },
            new[] { 0x1F3AE }, new[] { 0x1F4DA }, new[] { 0x1F4BB }, new[] { 0x1F4F1 }, new[] { 0x1F3E0 }, new[] { 0x1F697 },
        };

        public static int Count { get { return codes.Length; } }

        public static string Get(int i)
        {
            string s = "";
            foreach (int cp in codes[i]) s += char.ConvertFromUtf32(cp);
            return s;
        }

        /// <summary>Font per disegnare un'emoji grande (le emoji vengono da Segoe UI Emoji anche per fallback).</summary>
        public static Font NewFont(float pixels)
        {
            try { return new Font("Segoe UI Emoji", pixels, GraphicsUnit.Pixel); }
            catch (Exception) { return new Font(SystemFonts.MessageBoxFont.FontFamily, pixels, GraphicsUnit.Pixel); }
        }

        /// <summary>Disegna un'emoji centrata nel rettangolo.</summary>
        public static void DrawCentered(Graphics g, string emoji, Font font, Rectangle r, Color back)
        {
            Size sz = ColorText.Measure(emoji, font, r.Width + 20);
            Rectangle t = new Rectangle(r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2, sz.Width, sz.Height);
            t.Intersect(r);
            using (Brush b = new SolidBrush(back)) g.FillRectangle(b, r);
            ColorText.Draw(g, emoji, font, t, Color.FromArgb(11, 29, 58), back, false);
        }
    }

    /// <summary>Pulsante con una faccina: apre la tavolozza delle emoji e inserisce quella scelta nella casella di testo.</summary>
    internal sealed class EmojiButton : Control
    {
        private readonly TextBox target;
        private readonly Font font;
        private bool hover;
        private EmojiPicker picker;

        /// <summary>Tavolozza aperta (la notifica con la risposta rapida non deve sparire nel frattempo).</summary>
        public bool PickerOpen { get { return picker != null && !picker.IsDisposed; } }

        public EmojiButton(TextBox target)
        {
            this.target = target;
            font = EmojiSet.NewFont(18);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Width = 34;
            Text = "Emoji";
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
            Click += delegate { Toggle(); };
        }

        private void Toggle()
        {
            if (PickerOpen) { picker.Close(); return; }
            picker = new EmojiPicker();
            picker.Picked += Insert;
            picker.FormClosed += delegate { picker = null; };
            Point below = PointToScreen(new Point(0, 0));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(below.X, wa.Right - picker.Width));
            int y = below.Y - picker.Height - 4;
            if (y < wa.Top) y = below.Y + Height + 4;
            picker.Location = new Point(x, y);
            picker.Show(FindForm());
        }

        private void Insert(string emoji)
        {
            if (target.TextLength + emoji.Length > target.MaxLength) return;
            target.SelectedText = emoji;
            target.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color back = Parent != null ? Parent.BackColor : Color.FromArgb(11, 29, 58);
            Color face = hover ? Color.FromArgb(64, 196, 168) : back;
            e.Graphics.Clear(back);
            Rectangle r = new Rectangle(2, (Height - 30) / 2, Width - 4, 30);
            using (Brush b = new SolidBrush(face)) e.Graphics.FillRectangle(b, r);
            EmojiSet.DrawCentered(e.Graphics, char.ConvertFromUtf32(0x1F642), font, Rectangle.Inflate(r, -2, -2), face);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (PickerOpen) picker.Close();
                font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Tavolozza di emoji comuni, stile pixel art. Si chiude scegliendo un'emoji, con Esc o cliccando altrove.</summary>
    internal sealed class EmojiPicker : Form
    {
        private const int Cols = 8, Cell = 34, Pad = 6;
        private readonly Font font = EmojiSet.NewFont(20);
        private int hover = -1;

        public event Action<string> Picked;

        public EmojiPicker()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(11, 29, 58);
            int rows = (EmojiSet.Count + Cols - 1) / Cols;
            ClientSize = new Size(Cols * Cell + 2 * Pad, rows * Cell + 2 * Pad + 18);
            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            MouseMove += delegate(object s, MouseEventArgs e)
            {
                int h = HitTest(e.Location);
                if (h != hover) { hover = h; Invalidate(); }
            };
            MouseLeave += delegate { hover = -1; Invalidate(); };
            MouseClick += delegate(object s, MouseEventArgs e)
            {
                int i = HitTest(e.Location);
                if (i < 0) return;
                Action<string> h = Picked;
                Close();
                if (h != null) h(EmojiSet.Get(i));
            };
        }

        private Rectangle CellRect(int i)
        {
            return new Rectangle(Pad + (i % Cols) * Cell, Pad + 18 + (i / Cols) * Cell, Cell, Cell);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < EmojiSet.Count; i++) if (CellRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color panel = Color.FromArgb(14, 40, 78), teal = Color.FromArgb(64, 196, 168);
            g.Clear(BackColor);
            using (Pen border = new Pen(teal, 2)) g.DrawRectangle(border, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
            using (Bitmap title = new Bitmap(40, 9))
            {
                using (Graphics tg = Graphics.FromImage(title))
                {
                    tg.Clear(BackColor);
                    PixelFont.Draw(tg, "EMOJI", 3, 1, Color.FromArgb(255, 214, 90), 1);
                }
                PixelArt.DrawScaled(g, title, new Rectangle(0, 2, title.Width * 2, title.Height * 2));
            }
            using (Font small = new Font(SystemFonts.MessageBoxFont.FontFamily, 11, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "tutte le altre: Win + .", small, new Rectangle(80, 4, ClientSize.Width - 88, 16),
                                      Color.FromArgb(124, 200, 240), TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            for (int i = 0; i < EmojiSet.Count; i++)
            {
                Rectangle r = Rectangle.Inflate(CellRect(i), -1, -1);
                EmojiSet.DrawCentered(g, EmojiSet.Get(i), font, r, i == hover ? teal : panel);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: niente scheda in Alt+Tab
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) font.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Barra "rispondi" agganciata sopra la notifica di un messaggio dal telefono: casella di testo, emoji e INVIA, per
    /// rispondere senza aprire la chat. Non ruba il focus quando compare (un gioco a schermo intero resta in primo piano):
    /// si attiva solo cliccandoci. Finché si sta scrivendo la notifica non sparisce.
    /// </summary>
    internal sealed class QuickReplyBar : Form
    {
        private static readonly Color Navy = Color.FromArgb(11, 29, 58);
        private static readonly Color Teal = Color.FromArgb(64, 196, 168);

        private readonly TextBox input = new TextBox();
        private readonly EmojiButton emoji;
        private readonly PixelButton send = new PixelButton("INVIA");
        private readonly int scale;
        private readonly string cue;

        /// <summary>Testo da inviare; il gestore restituisce false se non è stato possibile.</summary>
        public event Func<string, bool> SendRequested;
        public event Action Sent;
        public event Action CancelRequested;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        public QuickReplyBar(int width, int scale, string cue)
        {
            this.scale = scale;
            this.cue = string.IsNullOrEmpty(cue) ? "Rispondi..." : cue;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Navy;
            TransparencyKey = ToastArt.Key;   // angoli smussati
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(width, 20 * scale);
            Padding = new Padding(3 * scale, 3 * scale, 3 * scale, 3 * scale);

            input.Dock = DockStyle.Fill;
            input.MaxLength = 2000;
            input.BorderStyle = BorderStyle.FixedSingle;
            input.BackColor = Color.FromArgb(234, 248, 255);
            input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSend(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Action h = CancelRequested; if (h != null) h(); }
            };
            input.HandleCreated += delegate { Cue(this.cue); };

            emoji = new EmojiButton(input);
            emoji.Dock = DockStyle.Left;
            emoji.BackColor = Navy;
            send.Dock = DockStyle.Right;
            send.BackColor = Navy;
            send.Width = 76;
            send.Click += delegate { DoSend(); };
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = 6;
            Panel gapLeft = new Panel();
            gapLeft.Dock = DockStyle.Left;
            gapLeft.Width = 4;

            Controls.Add(input);
            Controls.Add(gap);
            Controls.Add(send);
            Controls.Add(gapLeft);
            Controls.Add(emoji);
        }

        /// <summary>Si sta scrivendo (focus, testo o tavolozza aperta): la notifica deve restare.</summary>
        public bool Busy
        {
            get { return input.Focused || input.TextLength > 0 || emoji.PickerOpen || ContainsFocus; }
        }

        public string Draft { get { return input.Text; } }

        /// <summary>Testo grigio mostrato nella casella vuota (EM_SETCUEBANNER).</summary>
        private void Cue(string text)
        {
            try { SendMessage(input.Handle, 0x1501, new IntPtr(1), text); }
            catch (Exception) { /* non su Windows (test sotto Mono) */ }
        }

        private void DoSend()
        {
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            Func<string, bool> h = SendRequested;
            if (h != null && h(text))
            {
                input.Clear();
                Action s = Sent;
                if (s != null) s();
            }
            else
            {
                input.BackColor = Color.FromArgb(255, 214, 200);
                Cue("Non inviato: controllo remoto non attivo");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // cornice a gradini come le notifiche
            Graphics g = e.Graphics;
            int s = scale, w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(ToastArt.Key);
            using (Brush teal = new SolidBrush(Teal))
            using (Brush navy = new SolidBrush(Navy))
            {
                g.FillRectangle(teal, s, 0, w - 2 * s, h);
                g.FillRectangle(teal, 0, s, w, h - 2 * s);
                g.FillRectangle(navy, 2 * s, s, w - 4 * s, h - 2 * s);
                g.FillRectangle(navy, s, 2 * s, w - 2 * s, h - 4 * s);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }
}
