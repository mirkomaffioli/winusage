using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Disegna su PNG il toast pixel art (per controllarlo senza Windows): vedi run-tests.sh.
namespace WinUsage.Tests
{
    internal static class RenderPreview
    {
        private static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            int scale = 3;
            double[] secs = { 3600, 5400 + 30, 45 * 60, 12 * 3600 + 30 * 60 };
            int[] frames = { 0, 17 };
            int rows = secs.Length, cols = frames.Length;
            int pad = 8;
            using (Bitmap sheet = new Bitmap(cols * (ToastArt.W * scale + pad) + pad, rows * (ToastArt.H * scale + pad) + pad))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(48, 48, 56));
                for (int r = 0; r < rows; r++)
                {
                    using (ToastArt art = new ToastArt(secs[r], r))
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            Bitmap f = art.RenderFrame(frames[c]);
                            // il colore magenta e' trasparente nella finestra reale: qui lo sostituiamo con lo sfondo
                            ImageAttributes ia = new ImageAttributes();
                            ia.SetColorKey(ToastArt.Key, ToastArt.Key);
                            Rectangle dst = new Rectangle(pad + c * (ToastArt.W * scale + pad), pad + r * (ToastArt.H * scale + pad),
                                                          ToastArt.W * scale, ToastArt.H * scale);
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(f, dst, 0, 0, ToastArt.W, ToastArt.H, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
                sheet.Save(System.IO.Path.Combine(outDir, "toast.png"), ImageFormat.Png);
            }
            // grafico dello storico con dati di esempio
            HistoryData hd = new HistoryData();
            int n = 7;
            hd.DayKeys = new string[n]; hd.Dates = new DateTime[n]; hd.Seconds = new double[n][];
            double[][] sample =
            {
                new double[] { 5.2, 1.5, 0 }, new double[] { 7.8, 3.1, 1.2 }, new double[] { 3.4, 0.8, 0 },
                new double[] { 9.1, 2.6, 3.5 }, new double[] { 6.0, 4.4, 0.4 }, new double[] { 2.2, 0.3, 0 },
                new double[] { 4.1, 1.9, 1.0 }
            };
            for (int i = 0; i < n; i++)
            {
                hd.Dates[i] = new DateTime(2026, 11, 2).AddDays(i);
                hd.DayKeys[i] = hd.Dates[i].ToString("yyyy-MM-dd");
                hd.Seconds[i] = new double[] { sample[i][0] * 3600, sample[i][1] * 3600, sample[i][2] * 3600 };
            }
            using (Bitmap cb = new Bitmap(680, 300))
            using (Graphics cg = Graphics.FromImage(cb))
            using (Font f = new Font("DejaVu Sans", 9f))
            using (Font fb = new Font("DejaVu Sans", 9f, FontStyle.Bold))
            {
                HistoryChart.Draw(cg, new Rectangle(0, 0, cb.Width, cb.Height), hd, f, fb, Color.Black, Color.White);
                cb.Save(System.IO.Path.Combine(outDir, "history.png"), ImageFormat.Png);
            }
            Console.WriteLine("toast.png e history.png scritti");
            return 0;
        }
    }
}
