using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WinUsage
{
    internal enum NotifyKind { Screenshot, AppQuery, ChatMessage, ChatRequest, LockedRemote, Countdown, Cancelled, Security, Paired }

    /// <summary>
    /// Notifiche del controllo remoto in pixel art, con lo stesso fondale del messaggio di soglia (216x64 pixel logici,
    /// ingranditi senza interpolazione). Titoli nel font 5x7; il testo libero (messaggi, nomi) lo disegna la finestra
    /// a piena risoluzione dentro <see cref="TextArea"/>, perché il font 5x7 non ha tutti i caratteri.
    /// Dipende solo da System.Drawing: si può disegnare su PNG (tests/render-preview.sh).
    /// </summary>
    internal sealed class NotifyArt : IDisposable
    {
        public const int W = ToastArt.W;
        public const int H = ToastArt.H;
        private const int TextX = 52;
        private const int TextWidth = 156;

        public readonly NotifyKind Kind;
        public string Title = "";
        public string Big = "";
        public string Line3 = "";
        public string FreeText;              // testo libero (non null = disegnato dalla finestra in TextArea)
        public string Button;                // etichetta del pulsante, o null
        public string Button2;               // secondo pulsante (a sinistra del primo, arancione), o null
        public double Level = 1.0;           // bombola del conto alla rovescia: 1 piena, 0 vuota

        private static readonly int[] Bob = { 0, 1, 0, -1 };
        private static readonly int[] BubbleX = { 2, 44, 209, 210, 46, 5 };
        private static readonly int[] BubbleSpeed = { 1, 2, 1, 2, 1, 1 };
        private static readonly int[] BubbleOffset = { 0, 20, 33, 5, 48, 40 };
        private static readonly int[] WeedX = { 60, 98, 146, 188 };

        private static readonly Color TitleColor = Color.FromArgb(190, 235, 255);
        private static readonly Color BigColor = Color.FromArgb(255, 214, 90);
        private static readonly Color BigShadow = Color.FromArgb(138, 90, 26);
        private static readonly Color TagColor = Color.FromArgb(120, 232, 200);
        private static readonly Color Shadow = Color.FromArgb(8, 24, 52);
        private static readonly Color Navy = Color.FromArgb(11, 29, 58);
        private static readonly Color Alarm = Color.FromArgb(255, 120, 100);

        private readonly Bitmap sea = ToastArt.BuildSea(W, H);
        private readonly Bitmap frameBmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        private readonly Bitmap sprite;
        private readonly Bitmap fish = PixelArt.ToBitmap(Sprites.Fish);
        private readonly Bitmap bubble5 = PixelArt.ToBitmap(Sprites.Bubble5);
        private readonly Bitmap bubble3 = PixelArt.ToBitmap(Sprites.Bubble3);
        private readonly Bitmap weed0 = PixelArt.ToBitmap(Sprites.Seaweed0);
        private readonly Bitmap weed1 = PixelArt.ToBitmap(Sprites.Seaweed1);

        public NotifyArt(NotifyKind kind)
        {
            Kind = kind;
            string[] rows = null;
            switch (kind)
            {
                case NotifyKind.Screenshot: rows = RemoteSprites.Camera; break;
                case NotifyKind.AppQuery: rows = RemoteSprites.Periscope; break;
                case NotifyKind.ChatMessage:
                case NotifyKind.ChatRequest: rows = RemoteSprites.Shell; break;
                case NotifyKind.LockedRemote: rows = RemoteSprites.Padlock; break;
                case NotifyKind.Security: rows = RemoteSprites.Siren; break;
            }
            if (rows != null) sprite = PixelArt.ToBitmap(rows);
        }

        /// <summary>Area del testo libero, in pixel logici.</summary>
        public Rectangle TextArea
        {
            get { return new Rectangle(TextX, 19, TextWidth, Button != null ? 22 : 36); }
        }

        /// <summary>Pulsante, in pixel logici (vuoto se non c'è).</summary>
        public Rectangle ButtonRect
        {
            get
            {
                if (Button == null) return Rectangle.Empty;
                int w = PixelFont.Measure(Button, 1) + 10;
                return new Rectangle(W - 8 - w, H - 21, w, 13);
            }
        }

        /// <summary>Secondo pulsante, in pixel logici, a sinistra del primo (vuoto se non c'è).</summary>
        public Rectangle Button2Rect
        {
            get
            {
                if (Button2 == null || Button == null) return Rectangle.Empty;
                int w = PixelFont.Measure(Button2, 1) + 10;
                Rectangle b = ButtonRect;
                return new Rectangle(b.Left - 4 - w, b.Top, w, 13);
            }
        }

        public Bitmap RenderFrame(int frame)
        {
            using (Graphics g = Graphics.FromImage(frameBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(sea, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;

                for (int i = 0; i < WeedX.Length; i++)
                    g.DrawImageUnscaled((((frame / 4) + i) % 2 == 0) ? weed0 : weed1, WeedX[i], H - 13);

                for (int k = 0; k < BubbleX.Length; k++)
                {
                    int y = (H - 8) - ((frame * BubbleSpeed[k] + BubbleOffset[k]) % (H + 6));
                    int x = BubbleX[k] + ((((frame / 4) + k) % 2 == 0) ? 0 : 1);
                    g.DrawImageUnscaled(k < 2 ? bubble5 : bubble3, x, y);
                }

                DrawTexts(g);
                DrawIcon(g, frame);
                if (Button != null) DrawButton(g, ButtonRect, Button, Kind == NotifyKind.Countdown);
                if (!Button2Rect.IsEmpty) DrawButton(g, Button2Rect, Button2, true);
            }
            return frameBmp;
        }

        private void DrawTexts(Graphics g)
        {
            Color title = Kind == NotifyKind.Security ? Alarm : TitleColor;
            PixelFont.DrawShadow(g, Fit(Title, TextWidth), TextX, 8, title, Shadow, 1);
            if (FreeText != null)
            {
                // riquadro scuro dove la finestra scrive il testo libero
                Rectangle a = TextArea;
                using (Brush navy = new SolidBrush(Navy))
                using (Brush panel = new SolidBrush(Color.FromArgb(14, 40, 78)))
                {
                    g.FillRectangle(navy, a.X - 2, a.Y - 1, a.Width + 4, a.Height + 2);
                    g.FillRectangle(navy, a.X - 1, a.Y - 2, a.Width + 2, a.Height + 4);
                    g.FillRectangle(panel, a.X - 1, a.Y - 1, a.Width + 2, a.Height + 2);
                }
                return;
            }
            if (!string.IsNullOrEmpty(Big))
            {
                int scale = PixelFont.Measure(Big, 2) <= TextWidth ? 2 : 1;
                PixelFont.DrawShadow(g, Fit(Big, TextWidth), TextX, scale == 2 ? 20 : 24, BigColor, BigShadow, scale);
            }
            if (!string.IsNullOrEmpty(Line3))
            {
                int left = !Button2Rect.IsEmpty ? Button2Rect.Left : Button != null ? ButtonRect.Left : TextX + TextWidth + 4;
                int room = left - TextX - 4;
                PixelFont.DrawShadow(g, Fit(Line3, room), TextX, 43, TagColor, Shadow, 1);
            }
        }

        /// <summary>Accorcia il testo (con "..") finché non sta nella larghezza indicata.</summary>
        public static string Fit(string text, int width)
        {
            string t = PixelFont.Normalize(text);
            if (PixelFont.Measure(t, 1) <= width) return t;
            while (t.Length > 0 && PixelFont.Measure(t + "..", 1) > width) t = t.Substring(0, t.Length - 1);
            return t + "..";
        }

        private void DrawIcon(Graphics g, int frame)
        {
            int bob = Bob[(frame / 3) % Bob.Length];
            switch (Kind)
            {
                case NotifyKind.Countdown:
                case NotifyKind.Cancelled:
                    DrawTank(g, 16, 10 + bob, Kind == NotifyKind.Cancelled ? 1.0 : Level, frame, Kind == NotifyKind.Cancelled);
                    return;
                case NotifyKind.Paired:
                    g.DrawImageUnscaled(fish, 10 + (frame / 3) % 4, 22 + bob);
                    g.DrawImageUnscaled(fish, 22 + (frame / 3) % 4, 32 - bob);
                    return;
            }
            if (sprite == null) return;

            int y = 15 + bob;
            if (Kind == NotifyKind.AppQuery) y += Math.Max(0, 14 - frame * 2);   // il periscopio emerge
            g.DrawImage(sprite, new Rectangle(8, y, 32, 32), 0, 0, 16, 16, GraphicsUnit.Pixel);

            if (Kind == NotifyKind.Screenshot && (frame == 4 || frame == 5))
            {
                using (Brush b = new SolidBrush(Color.FromArgb(frame == 4 ? 230 : 120, 255, 255, 255)))
                    g.FillRectangle(b, 2, 2, W - 4, H - 4);   // flash
            }
            if (Kind == NotifyKind.Security && (frame / 3) % 2 == 0)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(255, 214, 90)))   // raggi della sirena
                {
                    g.FillRectangle(b, 6, y + 8, 4, 2);
                    g.FillRectangle(b, 38, y + 8, 4, 2);
                    g.FillRectangle(b, 23, y - 2, 2, 4);
                    g.FillRectangle(b, 10, y + 1, 2, 2);
                    g.FillRectangle(b, 36, y + 1, 2, 2);
                }
            }
        }

        /// <summary>Bombola d'ossigeno (14x36) che si svuota: il tema "ossigeno che finisce" del messaggio di soglia.</summary>
        public static void DrawTank(Graphics g, int x, int y, double level, int frame, bool refilling)
        {
            using (Brush navy = new SolidBrush(Navy))
            using (Brush steel = new SolidBrush(Color.FromArgb(150, 170, 190)))
            using (Brush body = new SolidBrush(Color.FromArgb(214, 64, 52)))
            using (Brush bodyLight = new SolidBrush(Color.FromArgb(240, 120, 96)))
            using (Brush empty = new SolidBrush(Color.FromArgb(40, 52, 72)))
            using (Brush air = new SolidBrush(Color.FromArgb(124, 200, 240)))
            using (Brush airLight = new SolidBrush(Color.FromArgb(191, 243, 255)))
            {
                // valvola
                g.FillRectangle(navy, x + 4, y, 6, 6);
                g.FillRectangle(steel, x + 5, y + 1, 4, 4);
                g.FillRectangle(navy, x + 9, y + 2, 4, 2);
                // corpo
                g.FillRectangle(navy, x + 1, y + 5, 12, 1);
                g.FillRectangle(navy, x, y + 6, 14, 30);
                g.FillRectangle(body, x + 1, y + 6, 12, 29);
                g.FillRectangle(bodyLight, x + 2, y + 7, 2, 26);
                // finestrella con il livello dell'aria
                int wx = x + 5, wy = y + 9, ww = 5, wh = 22;
                g.FillRectangle(navy, wx - 1, wy - 1, ww + 2, wh + 2);
                g.FillRectangle(empty, wx, wy, ww, wh);
                int fill = (int)Math.Round(wh * Math.Max(0, Math.Min(1, level)));
                if (fill > 0)
                {
                    g.FillRectangle(air, wx, wy + wh - fill, ww, fill);
                    g.FillRectangle(airLight, wx, wy + wh - fill, 1, fill);
                }
                // bolle che escono (si svuota) o entrano (annullato)
                int phase = frame % 12;
                if (!refilling && level > 0) g.FillRectangle(airLight, x + 12 + phase / 3, y - phase / 2, 2, 2);
                if (refilling) g.FillRectangle(airLight, wx + 2, wy + wh - 1 - (phase * 2) % wh, 1, 1);
            }
        }

        public static void DrawButton(Graphics g, Rectangle r, string label, bool danger)
        {
            Color face = danger ? Color.FromArgb(240, 138, 36) : Color.FromArgb(64, 196, 168);
            Color light = danger ? Color.FromArgb(255, 193, 94) : Color.FromArgb(150, 240, 216);
            Color dark = danger ? Color.FromArgb(160, 80, 16) : Color.FromArgb(22, 112, 124);
            using (Brush navy = new SolidBrush(Navy))
            using (Brush f = new SolidBrush(face))
            using (Brush l = new SolidBrush(light))
            using (Brush d = new SolidBrush(dark))
            {
                g.FillRectangle(navy, r.X + 1, r.Y, r.Width - 2, r.Height);
                g.FillRectangle(navy, r.X, r.Y + 1, r.Width, r.Height - 2);
                g.FillRectangle(f, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                g.FillRectangle(l, r.X + 1, r.Y + 1, r.Width - 2, 1);
                g.FillRectangle(d, r.X + 1, r.Y + r.Height - 2, r.Width - 2, 1);
            }
            int tx = r.X + (r.Width - PixelFont.Measure(label, 1)) / 2;
            PixelFont.DrawShadow(g, label, tx, r.Y + 3, Color.White, Navy, 1);
        }

        public void Dispose()
        {
            sea.Dispose(); frameBmp.Dispose(); fish.Dispose(); bubble5.Dispose(); bubble3.Dispose();
            weed0.Dispose(); weed1.Dispose();
            if (sprite != null) sprite.Dispose();
        }
    }

    /// <summary>Badge compatto del conto alla rovescia (120x22 pixel logici): bombola piccola, tempo e ANNULLA.</summary>
    internal sealed class BadgeArt : IDisposable
    {
        public const int W = 120;
        public const int H = 22;
        public static readonly Rectangle CancelRect = new Rectangle(70, 5, 46, 12);

        private readonly Bitmap sea = ToastArt.BuildSea(W, H);
        private readonly Bitmap frameBmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);

        public Bitmap Render(double remainingSeconds, double level, int frame)
        {
            using (Graphics g = Graphics.FromImage(frameBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(sea, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                using (Brush navy = new SolidBrush(Color.FromArgb(11, 29, 58)))
                using (Brush body = new SolidBrush(Color.FromArgb(214, 64, 52)))
                using (Brush empty = new SolidBrush(Color.FromArgb(40, 52, 72)))
                using (Brush air = new SolidBrush(Color.FromArgb(124, 200, 240)))
                {
                    g.FillRectangle(navy, 7, 3, 4, 2);
                    g.FillRectangle(navy, 5, 5, 8, 14);
                    g.FillRectangle(body, 6, 6, 6, 12);
                    g.FillRectangle(empty, 8, 7, 2, 10);
                    int fill = (int)Math.Round(10 * Math.Max(0, Math.Min(1, level)));
                    if (fill > 0) g.FillRectangle(air, 8, 17 - fill, 2, fill);
                }
                int s = (int)Math.Ceiling(remainingSeconds);
                string time = (s / 60) + ":" + (s % 60).ToString("00");
                Color c = s <= 60 && (frame / 5) % 2 == 0 ? Color.FromArgb(255, 120, 100) : Color.FromArgb(255, 214, 90);
                int scale = time.Length <= 4 ? 2 : 1;   // da 10 minuti in su il tempo non sta nel formato grande
                PixelFont.DrawShadow(g, time, 15, scale == 2 ? 4 : 8, c, Color.FromArgb(138, 90, 26), scale);
                NotifyArt.DrawButton(g, CancelRect, "ANNULLA", true);
            }
            return frameBmp;
        }

        public void Dispose()
        {
            sea.Dispose();
            frameBmp.Dispose();
        }
    }
}
