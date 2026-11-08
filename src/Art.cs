using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WinUsage
{
    /// <summary>Font bitmap 5x7 (solo maiuscole, cifre e qualche segno): il testo del toast è davvero pixel art.</summary>
    internal static class PixelFont
    {
        public const int Advance = 6;
        public const int Height = 7;

        private static readonly Dictionary<char, string[]> Glyphs = Build();

        private static void Add(Dictionary<char, string[]> d, char c, string rows)
        {
            d[c] = rows.Split('|');
        }

        private static Dictionary<char, string[]> Build()
        {
            Dictionary<char, string[]> d = new Dictionary<char, string[]>();
            Add(d, 'A', ".XXX.|X...X|X...X|XXXXX|X...X|X...X|X...X");
            Add(d, 'B', "XXXX.|X...X|X...X|XXXX.|X...X|X...X|XXXX.");
            Add(d, 'C', ".XXX.|X...X|X....|X....|X....|X...X|.XXX.");
            Add(d, 'D', "XXXX.|X...X|X...X|X...X|X...X|X...X|XXXX.");
            Add(d, 'E', "XXXXX|X....|X....|XXXX.|X....|X....|XXXXX");
            Add(d, 'F', "XXXXX|X....|X....|XXXX.|X....|X....|X....");
            Add(d, 'G', ".XXX.|X...X|X....|X.XXX|X...X|X...X|.XXXX");
            Add(d, 'H', "X...X|X...X|X...X|XXXXX|X...X|X...X|X...X");
            Add(d, 'I', ".XXX.|..X..|..X..|..X..|..X..|..X..|.XXX.");
            Add(d, 'J', "..XXX|...X.|...X.|...X.|...X.|X..X.|.XX..");
            Add(d, 'K', "X...X|X..X.|X.X..|XX...|X.X..|X..X.|X...X");
            Add(d, 'L', "X....|X....|X....|X....|X....|X....|XXXXX");
            Add(d, 'M', "X...X|XX.XX|X.X.X|X.X.X|X...X|X...X|X...X");
            Add(d, 'N', "X...X|XX..X|X.X.X|X..XX|X...X|X...X|X...X");
            Add(d, 'O', ".XXX.|X...X|X...X|X...X|X...X|X...X|.XXX.");
            Add(d, 'P', "XXXX.|X...X|X...X|XXXX.|X....|X....|X....");
            Add(d, 'Q', ".XXX.|X...X|X...X|X...X|X.X.X|X..X.|.XX.X");
            Add(d, 'R', "XXXX.|X...X|X...X|XXXX.|X.X..|X..X.|X...X");
            Add(d, 'S', ".XXXX|X....|X....|.XXX.|....X|....X|XXXX.");
            Add(d, 'T', "XXXXX|..X..|..X..|..X..|..X..|..X..|..X..");
            Add(d, 'U', "X...X|X...X|X...X|X...X|X...X|X...X|.XXX.");
            Add(d, 'V', "X...X|X...X|X...X|X...X|X...X|.X.X.|..X..");
            Add(d, 'W', "X...X|X...X|X...X|X.X.X|X.X.X|XX.XX|X...X");
            Add(d, 'X', "X...X|X...X|.X.X.|..X..|.X.X.|X...X|X...X");
            Add(d, 'Y', "X...X|X...X|.X.X.|..X..|..X..|..X..|..X..");
            Add(d, 'Z', "XXXXX|....X|...X.|..X..|.X...|X....|XXXXX");
            Add(d, '0', ".XXX.|X...X|X..XX|X.X.X|XX..X|X...X|.XXX.");
            Add(d, '1', "..X..|.XX..|..X..|..X..|..X..|..X..|.XXX.");
            Add(d, '2', ".XXX.|X...X|....X|...X.|..X..|.X...|XXXXX");
            Add(d, '3', "XXXX.|....X|....X|.XXX.|....X|....X|XXXX.");
            Add(d, '4', "...X.|..XX.|.X.X.|X..X.|XXXXX|...X.|...X.");
            Add(d, '5', "XXXXX|X....|XXXX.|....X|....X|X...X|.XXX.");
            Add(d, '6', ".XXX.|X....|X....|XXXX.|X...X|X...X|.XXX.");
            Add(d, '7', "XXXXX|....X|...X.|..X..|.X...|.X...|.X...");
            Add(d, '8', ".XXX.|X...X|X...X|.XXX.|X...X|X...X|.XXX.");
            Add(d, '9', ".XXX.|X...X|X...X|.XXXX|....X|....X|.XXX.");
            Add(d, '!', "..X..|..X..|..X..|..X..|..X..|.....|..X..");
            Add(d, '?', ".XXX.|X...X|....X|...X.|..X..|.....|..X..");
            Add(d, ':', ".....|..X..|..X..|.....|..X..|..X..|.....");
            Add(d, '.', ".....|.....|.....|.....|.....|.....|..X..");
            Add(d, ',', ".....|.....|.....|.....|..X..|..X..|.X...");
            Add(d, '-', ".....|.....|.....|.XXX.|.....|.....|.....");
            Add(d, '\'', "..X..|..X..|.X...|.....|.....|.....|.....");
            Add(d, '/', "....X|....X|...X.|..X..|.X...|X....|X....");
            Add(d, ' ', ".....|.....|.....|.....|.....|.....|.....");
            return d;
        }

        public static string Normalize(string text)
        {
            return (text ?? "").ToUpperInvariant();
        }

        public static int Measure(string text, int scale)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (text.Length * Advance - 1) * scale;
        }

        public static void Draw(Graphics g, string text, int x, int y, Color color, int scale)
        {
            text = Normalize(text);
            using (Brush brush = new SolidBrush(color))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    string[] glyph;
                    if (!Glyphs.TryGetValue(text[i], out glyph)) continue;
                    for (int r = 0; r < Height; r++)
                    {
                        string row = glyph[r];
                        for (int c = 0; c < 5; c++)
                        {
                            if (row[c] == 'X')
                                g.FillRectangle(brush, x + (i * Advance + c) * scale, y + r * scale, scale, scale);
                        }
                    }
                }
            }
        }

        /// <summary>Testo con ombra di un pixel (verso il basso a destra), come nei menu dei videogiochi.</summary>
        public static void DrawShadow(Graphics g, string text, int x, int y, Color color, Color shadow, int scale)
        {
            Draw(g, text, x + scale, y + scale, shadow, scale);
            Draw(g, text, x, y, color, scale);
        }
    }

    internal static class PixelArt
    {
        /// <summary>Converte uno sprite ASCII (vedi Sprites.cs) in bitmap 1:1 con sfondo trasparente.</summary>
        public static Bitmap ToBitmap(string[] rows)
        {
            int h = rows.Length, w = rows[0].Length;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c;
                    if (Sprites.Palette.TryGetValue(rows[y][x], out c)) bmp.SetPixel(x, y, c);
                }
            }
            return bmp;
        }

        /// <summary>Disegna <paramref name="source"/> ingrandita di un fattore intero, senza sfumature.</summary>
        public static void DrawScaled(Graphics g, Image source, Rectangle target)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(source, target, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
    }

    /// <summary>
    /// Disegna il messaggio di soglia raggiunta come scena pixel art animata (tema subacqueo):
    /// elmo da palombaro con orologio, bolle che salgono, un pesce che nuota, alghe che ondeggiano.
    /// Lavora su una tela logica di 216x64 pixel; chi la mostra la ingrandisce senza interpolazione.
    /// Dipende solo da System.Drawing, quindi si può disegnare anche su PNG (vedi tests).
    /// </summary>
    internal sealed class ToastArt : IDisposable
    {
        public const int W = 216;
        public const int H = 64;
        public static readonly Color Key = Color.FromArgb(255, 0, 255);   // colore trasparente per gli angoli smussati

        private const int TextX = 52;
        private const int TextWidth = 156;

        private static readonly string[] Taglines =
        {
            "RISALI A RESPIRARE UN PO'!",
            "OSSIGENO IN ESAURIMENTO!",
            "UNA PAUSA TI FA BENE!",
            "ALZATI E FAI DUE PASSI!",
            "STACCA GLI OCCHI, RESPIRA!"
        };

        private static readonly int[] Bob = { 0, 1, 0, -1 };
        private static readonly int[] BubbleX = { 2, 44, 209, 210, 46, 5 };
        private static readonly bool[] BubbleBig = { true, true, false, false, false, false };
        private static readonly int[] BubbleSpeed = { 1, 2, 1, 2, 1, 1 };
        private static readonly int[] BubbleOffset = { 0, 20, 33, 5, 48, 40 };
        private static readonly int[] WeedX = { 60, 98, 146, 188 };

        private readonly Bitmap baseLayer;
        private readonly Bitmap frameBmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        private readonly Bitmap helmet = PixelArt.ToBitmap(Sprites.Helmet32);
        private readonly Bitmap fish = PixelArt.ToBitmap(Sprites.Fish);
        private readonly Bitmap bubble5 = PixelArt.ToBitmap(Sprites.Bubble5);
        private readonly Bitmap bubble3 = PixelArt.ToBitmap(Sprites.Bubble3);
        private readonly Bitmap weed0 = PixelArt.ToBitmap(Sprites.Seaweed0);
        private readonly Bitmap weed1 = PixelArt.ToBitmap(Sprites.Seaweed1);

        public ToastArt(double seconds, int taglineIndex)
        {
            string tag = Taglines[((taglineIndex % Taglines.Length) + Taglines.Length) % Taglines.Length];
            baseLayer = BuildBase("Stai usando il computer da", Fmt.ToastTime(seconds), tag);
        }

        public static int TaglineCount { get { return Taglines.Length; } }

        private static Bitmap BuildBase(string line1, string big, string tag)
        {
            Color[] bands =
            {
                Color.FromArgb(30, 100, 170), Color.FromArgb(24, 82, 150), Color.FromArgb(20, 68, 132),
                Color.FromArgb(16, 56, 114), Color.FromArgb(13, 45, 96), Color.FromArgb(10, 36, 80)
            };
            Color sand = Color.FromArgb(230, 201, 140), sand2 = Color.FromArgb(196, 164, 104);
            Color navy = Color.FromArgb(11, 29, 58);
            Color light = Color.FromArgb(124, 200, 240), dark = Color.FromArgb(8, 26, 56);
            int seaBottom = 57;

            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color c;
                    if (y >= seaBottom)
                    {
                        c = ((x * 7 + y * 3) % 5 != 0) ? sand : sand2;
                        if (y == seaBottom) c = sand2;
                    }
                    else
                    {
                        double pos = (double)y / seaBottom * bands.Length;
                        int i = (int)pos;
                        double frac = pos - i;
                        int j = i;
                        // sfumatura a scacchiera sul bordo tra una banda e l'altra
                        if (i + 1 < bands.Length &&
                            ((((x + y) % 2) == 0 && frac > 0.75) || ((x % 2) == 0 && (y % 2) == 0 && frac > 0.5)))
                            j = i + 1;
                        c = bands[j];
                    }

                    // cornice: contorno scuro + bordo smussato chiaro (alto/sinistra) e scuro (basso/destra)
                    if (x == 0 || x == W - 1 || y == 0 || y == H - 1) c = navy;
                    else if (x == 1 || y == 1) c = light;
                    else if (x == W - 2 || y == H - 2) c = dark;
                    b.SetPixel(x, y, c);
                }
            }
            // angoli smussati
            int[][] cuts = { new int[] { 0, 0 }, new int[] { 1, 0 }, new int[] { 0, 1 } };
            foreach (int[] p in cuts)
            {
                b.SetPixel(p[0], p[1], Key);
                b.SetPixel(W - 1 - p[0], p[1], Key);
                b.SetPixel(p[0], H - 1 - p[1], Key);
                b.SetPixel(W - 1 - p[0], H - 1 - p[1], Key);
            }
            b.SetPixel(1, 1, navy); b.SetPixel(W - 2, 1, navy); b.SetPixel(1, H - 2, navy); b.SetPixel(W - 2, H - 2, navy);

            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Color shadow = Color.FromArgb(8, 24, 52);

                PixelFont.DrawShadow(g, line1, TextX, 9, Color.FromArgb(190, 235, 255), shadow, 1);

                int scale = PixelFont.Measure(big, 2) <= TextWidth ? 2 : 1;
                PixelFont.DrawShadow(g, big, TextX, 21, Color.FromArgb(255, 214, 90), Color.FromArgb(138, 90, 26), scale);

                PixelFont.DrawShadow(g, tag, TextX, 43, Color.FromArgb(120, 232, 200), shadow, 1);
            }
            return b;
        }

        /// <summary>Disegna il fotogramma <paramref name="frame"/> (10 al secondo). La bitmap restituita è riutilizzata.</summary>
        public Bitmap RenderFrame(int frame)
        {
            using (Graphics g = Graphics.FromImage(frameBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(baseLayer, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;

                // alghe che ondeggiano
                for (int i = 0; i < WeedX.Length; i++)
                {
                    Bitmap weed = (((frame / 4) + i) % 2 == 0) ? weed0 : weed1;
                    g.DrawImageUnscaled(weed, WeedX[i], 51);
                }

                // pesce che attraversa la scena
                int fx = (frame * 2) % (W + 40) - 20;
                g.DrawImageUnscaled(fish, fx, 49 + (((frame / 5) % 2 == 0) ? 0 : 1));

                // bolle che salgono
                for (int k = 0; k < BubbleX.Length; k++)
                {
                    int y = (H - 8) - ((frame * BubbleSpeed[k] + BubbleOffset[k]) % (H + 6));
                    int x = BubbleX[k] + ((((frame / 4) + k) % 2 == 0) ? 0 : 1);
                    g.DrawImageUnscaled(BubbleBig[k] ? bubble5 : bubble3, x, y);
                }

                // elmo che dondola
                g.DrawImageUnscaled(helmet, 8, 15 + Bob[(frame / 3) % Bob.Length]);
            }
            return frameBmp;
        }

        public void Dispose()
        {
            baseLayer.Dispose(); frameBmp.Dispose(); helmet.Dispose(); fish.Dispose();
            bubble5.Dispose(); bubble3.Dispose(); weed0.Dispose(); weed1.Dispose();
        }
    }
}
