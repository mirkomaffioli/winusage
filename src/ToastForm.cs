using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Il messaggio "Stai usando il computer da ..." in pixel art: finestra senza bordi, animata a 10 fotogrammi
    /// al secondo, che compare in basso a destra senza rubare il focus e sparisce da sola (o con un clic).
    /// </summary>
    internal sealed class ToastForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const int LifeFrames = 110;      // 11 secondi
        private const int SlideFrames = 6;

        private static ToastForm current;
        private static readonly Random Rnd = new Random();

        private readonly ToastArt art;
        private readonly Timer timer = new Timer();
        private readonly int scale;
        private readonly Point target;
        private int frame;

        public static void ShowFor(double seconds)
        {
            if (current != null && !current.IsDisposed) current.Close();
            current = new ToastForm(seconds);
            current.Show();
            current.KeepOnTop();
        }

        private ToastForm(double seconds)
        {
            art = new ToastArt(seconds, Rnd.Next(ToastArt.TaglineCount));

            float dpi;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            scale = Math.Max(2, (int)Math.Round(2.0 * dpi / 96.0));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ToastArt.Key;
            TransparencyKey = ToastArt.Key;
            DoubleBuffered = true;
            ClientSize = new Size(ToastArt.W * scale, ToastArt.H * scale);

            // Sul monitor della finestra in primo piano (il gioco), non necessariamente quello principale.
            Rectangle wa = Probe.ForegroundScreen().WorkingArea;
            int margin = 16;
            target = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
            Location = SlidePosition(0);

            Click += delegate { Close(); };
            timer.Interval = 100;
            timer.Tick += OnTick;
            timer.Start();
        }

        /// <summary>Posizione durante l'ingresso/uscita: scorre dal basso a scatti, come nei giochi a pixel.</summary>
        private Point SlidePosition(int f)
        {
            double t = 1.0;
            if (f < SlideFrames) t = (double)f / SlideFrames;
            else if (f > LifeFrames - SlideFrames) t = (double)(LifeFrames - f) / SlideFrames;
            t = Math.Max(0, Math.Min(1, t));
            int offset = (int)(Height * (1 - t) * (1 - t));
            offset -= offset % scale;
            return new Point(target.X, target.Y + offset);
        }

        private void OnTick(object sender, EventArgs e)
        {
            frame++;
            if (frame >= LifeFrames) { Close(); return; }
            Location = SlidePosition(frame);
            if (frame % 5 == 0) KeepOnTop();
            Invalidate();
        }

        /// <summary>
        /// Rimette la finestra in cima a tutte le altre senza attivarla: giochi in finestra senza bordi e Steam
        /// Big Picture a volte si riportano sopra. Il gioco non perde mai il focus.
        /// </summary>
        private void KeepOnTop()
        {
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            PixelArt.DrawScaled(g, art.RenderFrame(frame), new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
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
                if (current == this) current = null;
            }
            base.Dispose(disposing);
        }
    }
}
