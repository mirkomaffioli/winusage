using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Una notifica pixel art da mostrare.</summary>
    internal sealed class NotifyRequest
    {
        public NotifyKind Kind;
        public string Title = "";
        public string Big = "";
        public string Line3 = "";
        public string FreeText;
        public string Button;
        public string Button2;               // secondo pulsante, a sinistra del primo (es. CHIUDI), o null
        public int Seconds = 6;              // 0 = finché non si clicca
        public int Priority;                 // 3 sicurezza, 2 conto alla rovescia, 1 messaggi, 0 il resto
        public int Tune = -1;                // jingle di ChipTune, -1 nessuno
        public Action OnClick;               // clic sulla notifica (che poi si chiude)
        public Action OnButton;              // clic sul pulsante
        public Action OnButton2;             // clic sul secondo pulsante
        public Action OnTimeout;             // la notifica sparisce da sola, senza clic
        public Func<NotifyArt, bool> Update; // a ogni fotogramma; false = chiudi
        public Func<string, bool> QuickReply;  // non null = barra "rispondi" sopra la notifica; false = non inviato
        public string ReplyDraft;            // testo scritto nella barra quando la notifica è stata cliccata
        public string QuickReplyCue = "Rispondi...";   // testo grigio nella barra (es. "Rispondi a tutti...")
    }

    /// <summary>
    /// Coda delle notifiche: una alla volta, in ordine di priorità. Non si mostrano sulla schermata di blocco (sparirebbero
    /// senza che nessuno le veda): aspettano lo sblocco.
    /// </summary>
    internal static class NotifyCenter
    {
        private static readonly List<NotifyRequest> queue = new List<NotifyRequest>();
        private static NotifyForm current;
        public static bool Sounds = true;

        public static void Show(NotifyRequest r)
        {
            int i = 0;
            while (i < queue.Count && queue[i].Priority >= r.Priority) i++;
            queue.Insert(i, r);
            if (current != null && !current.IsDisposed && r.Priority >= 3 && current.Request.Priority < 3)
                current.Close();   // un avviso di sicurezza passa davanti
            Pump();
        }

        /// <summary>Da chiamare a ogni secondo (e alla chiusura di una notifica).</summary>
        public static void Pump()
        {
            if (current != null && !current.IsDisposed) return;
            current = null;
            if (queue.Count == 0 || Probe.UserAway()) return;
            NotifyRequest r = queue[0];
            queue.RemoveAt(0);
            try
            {
                current = new NotifyForm(r);
                current.FormClosed += delegate
                {
                    current = null;
                    Timer t = new Timer();   // la prossima dopo un attimo, non nello stesso evento
                    t.Interval = 400;
                    t.Tick += delegate { t.Stop(); t.Dispose(); Pump(); };
                    t.Start();
                };
                current.Show();
                current.KeepOnTop();
                if (Sounds && r.Tune >= 0) ChipTune.Play(r.Tune);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                current = null;
            }
        }

        /// <summary>Toglie dalla coda (e chiude se visibile) le notifiche di un tipo, ad esempio il conto alla rovescia annullato.</summary>
        public static void Remove(NotifyKind kind)
        {
            queue.RemoveAll(delegate(NotifyRequest q) { return q.Kind == kind; });
            if (current != null && !current.IsDisposed && current.Request.Kind == kind) current.Close();
        }

        public static void CloseAll()
        {
            queue.Clear();
            if (current != null && !current.IsDisposed) current.Close();
        }

        public static int DpiScale()
        {
            float dpi;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            return Math.Max(2, (int)Math.Round(2.0 * dpi / 96.0));
        }
    }

    /// <summary>Finestra senza bordi e senza focus che mostra una <see cref="NotifyArt"/>, come il messaggio di soglia.</summary>
    internal sealed class NotifyForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SlideFrames = 6;

        public readonly NotifyRequest Request;
        private readonly NotifyArt art;
        private readonly Timer timer = new Timer();
        private readonly int scale;
        private int lifeFrames;
        private readonly Point target;
        private int frame;
        private bool closing;
        private int closeFrame;
        private QuickReplyBar reply;

        public NotifyForm(NotifyRequest r)
        {
            Request = r;
            art = new NotifyArt(r.Kind);
            art.Title = r.Title ?? "";
            art.Big = r.Big ?? "";
            art.Line3 = r.Line3 ?? "";
            art.FreeText = r.FreeText;
            art.Button = r.Button;
            art.Button2 = r.Button2;
            if (r.Update != null) r.Update(art);

            scale = NotifyCenter.DpiScale();
            lifeFrames = r.Seconds > 0 ? r.Seconds * 10 : int.MaxValue;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ToastArt.Key;
            TransparencyKey = ToastArt.Key;
            DoubleBuffered = true;
            ClientSize = new Size(NotifyArt.W * scale, NotifyArt.H * scale);

            Rectangle wa = Probe.ForegroundScreen().WorkingArea;
            target = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Location = SlidePosition(0);

            MouseClick += OnMouseClick;
            if (r.QuickReply != null)
            {
                reply = new QuickReplyBar(Width, scale, r.QuickReplyCue);
                reply.SendRequested += r.QuickReply;
                // chiusura subito dopo, non dentro il gestore del tasto della casella che sta per sparire
                reply.Sent += delegate { CloseLater(); };
                reply.CancelRequested += delegate { CloseLater(); };
            }
            timer.Interval = 100;
            timer.Tick += OnTick;
            timer.Start();
        }

        private Point SlidePosition(int f)
        {
            double t = f < SlideFrames ? (double)f / SlideFrames : 1.0;
            if (closing) t = Math.Max(0, 1.0 - (double)(frame - closeFrame) / SlideFrames);
            int offset = (int)(Height * (1 - t) * (1 - t));
            offset -= offset % scale;
            return new Point(target.X, target.Y + offset);
        }

        private void OnTick(object sender, EventArgs e)
        {
            frame++;
            // si sta scrivendo nella barra "rispondi": la notifica resta finché non si invia o si chiude
            if (!closing && reply != null && !reply.IsDisposed && reply.Busy) lifeFrames = Math.Max(lifeFrames, frame + SlideFrames + 20);
            if (!closing && Request.Update != null && !Request.Update(art)) StartClose();
            if (!closing && frame >= lifeFrames - SlideFrames)
            {
                StartClose();
                if (Request.OnTimeout != null) { try { Request.OnTimeout(); } catch (Exception ex) { Log.Write(ex); } }
            }
            if (closing && frame - closeFrame >= SlideFrames) { Close(); return; }
            Location = SlidePosition(frame);
            if (frame % 5 == 0) KeepOnTop();
            PlaceReply();
            Invalidate();
        }

        /// <summary>La barra "rispondi" compare sopra la notifica quando ha finito di salire e sparisce quando scende.</summary>
        private void PlaceReply()
        {
            if (reply == null || reply.IsDisposed) return;
            if (closing) { if (reply.Visible) reply.Hide(); return; }
            if (frame < SlideFrames) return;
            reply.Location = new Point(Left, Top - reply.Height - 2 * scale);
            if (!reply.Visible) reply.Show();
            if (frame % 5 == 0 && !reply.Busy) NativeWindowPos.KeepOnTop(reply.Handle);
        }

        private void CloseLater()
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { if (!IsDisposed) Close(); });
        }

        private void StartClose()
        {
            if (closing) return;
            closing = true;
            closeFrame = frame;
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (closing) return;
            if (reply != null && !reply.IsDisposed) Request.ReplyDraft = reply.Draft;
            try
            {
                if (Hit(art.ButtonRect, e.Location) && Request.OnButton != null) Request.OnButton();
                else if (Hit(art.Button2Rect, e.Location)) { if (Request.OnButton2 != null) Request.OnButton2(); }
                else if (Request.OnClick != null) Request.OnClick();
            }
            catch (Exception ex) { Log.Write(ex); }
            Close();
        }

        private bool Hit(Rectangle logical, Point p)
        {
            if (logical.IsEmpty) return false;
            return new Rectangle(logical.X * scale, logical.Y * scale, logical.Width * scale, logical.Height * scale).Contains(p);
        }

        public void KeepOnTop()
        {
            if (IsHandleCreated) NativeWindowPos.KeepOnTop(Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            PixelArt.DrawScaled(g, art.RenderFrame(frame), new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            g.CompositingMode = CompositingMode.SourceOver;
            if (art.FreeText != null)
            {
                Rectangle a = art.TextArea;
                Rectangle r = new Rectangle(a.X * scale + 2, a.Y * scale + 1, a.Width * scale - 4, a.Height * scale - 2);
                using (Font f = new Font(SystemFonts.MessageBoxFont.FontFamily, 6.5f * scale, GraphicsUnit.Pixel))
                {
                    // emoji a colori (DirectWrite) dove possibile
                    ColorText.Draw(g, art.FreeText, f, r, Color.FromArgb(234, 248, 255), NotifyArt.TextPanel, true);
                }
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;   // niente focus, niente scheda in Alt+Tab
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                timer.Dispose();
                art.Dispose();
                if (reply != null && !reply.IsDisposed) reply.Close();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Badge compatto e sempre visibile del conto alla rovescia: tempo rimasto e ANNULLA (cliccabile).</summary>
    internal sealed class BadgeForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly BadgeArt art = new BadgeArt();
        private readonly Timer timer = new Timer();
        private readonly int scale;
        private readonly Func<double> remaining;
        private readonly Func<double> level;
        private int frame;

        public event Action CancelClicked;

        public BadgeForm(Func<double> remaining, Func<double> level)
        {
            this.remaining = remaining;
            this.level = level;
            scale = NotifyCenter.DpiScale();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ToastArt.Key;
            TransparencyKey = ToastArt.Key;
            DoubleBuffered = true;
            ClientSize = new Size(BadgeArt.W * scale, BadgeArt.H * scale);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // sopra il posto delle notifiche grandi, così non si coprono
            Location = new Point(wa.Right - Width - 16, wa.Bottom - 16 - NotifyArt.H * scale - 8 - Height);
            MouseClick += delegate(object s, MouseEventArgs e)
            {
                Rectangle c = BadgeArt.CancelRect;
                if (new Rectangle(c.X * scale, c.Y * scale, c.Width * scale, c.Height * scale).Contains(e.Location))
                {
                    Action h = CancelClicked;
                    if (h != null) h();
                }
            };
            timer.Interval = 100;
            timer.Tick += delegate
            {
                frame++;
                if (frame % 5 == 0 && IsHandleCreated) NativeWindowPos.KeepOnTop(Handle);
                Invalidate();
            };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            PixelArt.DrawScaled(e.Graphics, art.Render(remaining(), level(), frame), new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                timer.Dispose();
                art.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class NativeWindowPos
    {
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>In cima a tutte le finestre senza attivarla: il gioco a schermo intero non perde il focus.</summary>
        public static void KeepOnTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
