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
            // storico a 30 giorni: valori di esempio deterministici, con qualche giorno a PC spento
            int n30 = 30;
            HistoryData h30 = new HistoryData();
            h30.DayKeys = new string[n30]; h30.Dates = new DateTime[n30]; h30.Seconds = new double[n30][];
            for (int i = 0; i < n30; i++)
            {
                h30.Dates[i] = new DateTime(2026, 11, 8).AddDays(i - (n30 - 1));
                h30.DayKeys[i] = h30.Dates[i].ToString("yyyy-MM-dd");
                double sys = (i % 9 == 4) ? 0 : 2 + ((i * 37) % 70) / 10.0;
                double br = sys * (0.2 + ((i * 13) % 5) / 10.0);
                double st = (h30.Dates[i].DayOfWeek == DayOfWeek.Saturday || h30.Dates[i].DayOfWeek == DayOfWeek.Sunday) ? sys * 0.5 : 0;
                h30.Seconds[i] = new double[] { sys * 3600, br * 3600, st * 3600 };
            }
            using (Bitmap cb = new Bitmap(790, 300))
            using (Graphics cg = Graphics.FromImage(cb))
            using (Font f = new Font("DejaVu Sans", 9f))
            using (Font fb = new Font("DejaVu Sans", 9f, FontStyle.Bold))
            {
                HistoryChart.Draw(cg, new Rectangle(0, 0, cb.Width, cb.Height), h30, f, fb, Color.Black, Color.White);
                cb.Save(System.IO.Path.Combine(outDir, "history30.png"), ImageFormat.Png);
            }
            RenderRemote(outDir);
            Console.WriteLine("toast.png, history.png, history30.png e remote-notify.png scritti");
            return 0;
        }

        /// <summary>Le notifiche pixel art del controllo remoto (testo libero disegnato come farebbe la finestra).</summary>
        private static void RenderRemote(string outDir)
        {
            int scale = 3, pad = 8;
            NotifyArt[] arts =
            {
                Make(NotifyKind.Screenshot, "SCREENSHOT INVIATO", "AL TELEFONO", "ALLE 15:02", null, null, 1),
                Make(NotifyKind.AppQuery, "IL TELEFONO HA CHIESTO", "L'APP IN USO", "ALLE 15:04", null, null, 1),
                Make(NotifyKind.ChatMessage, "MESSAGGIO DAL TELEFONO", "", "", "Ciao! A che ora finisci? Ricordati di staccare per cena", "APRI CHAT", 1, "CHIUDI"),
                Make(NotifyKind.ChatRequest, "IL TELEFONO VUOLE CHATTARE", "CHAT", "", null, "APRI CHAT", 1, "CHIUDI"),
                Make(NotifyKind.LockedRemote, "BLOCCATO DAL TELEFONO", "ALLE 15:02", "BENTORNATO!", null, null, 1),
                Make(NotifyKind.Countdown, "SPEGNIMENTO DA REMOTO", "TRA 4:59", "SALVA IL LAVORO!", null, "ANNULLA", 0.98),
                Make(NotifyKind.Countdown, "SPEGNIMENTO DA REMOTO", "TRA 0:42", "SALVA IL LAVORO!", null, "ANNULLA", 0.14),
                Make(NotifyKind.Cancelled, "SPEGNIMENTO ANNULLATO", "OSSIGENO OK", "DAL TELEFONO", null, null, 1),
                Make(NotifyKind.Security, "ATTENZIONE: SICUREZZA", "", "", "Codice dell'Authenticator sbagliato 3 volte dal telefono abbinato. Comandi con codice bloccati per 15 minuti.", null, 1),
                Make(NotifyKind.Paired, "TELEFONO ABBINATO", "", "", "Mirko (@mirko)", null, 1),
            };
            int[] frames = { 4, 9, 7, 7, 6, 6, 9, 6, 6, 6 };
            int cols = 2, rows = (arts.Length + 1) / cols;
            int cw = NotifyArt.W * scale, ch = NotifyArt.H * scale;
            using (Bitmap sheet = new Bitmap(cols * (cw + pad) + pad, rows * (ch + pad) + pad + BadgeArt.H * scale + pad))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(48, 48, 56));
                ImageAttributes ia = new ImageAttributes();
                ia.SetColorKey(ToastArt.Key, ToastArt.Key);
                for (int i = 0; i < arts.Length; i++)
                {
                    Rectangle dst = new Rectangle(pad + (i % cols) * (cw + pad), pad + (i / cols) * (ch + pad), cw, ch);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(arts[i].RenderFrame(frames[i]), dst, 0, 0, NotifyArt.W, NotifyArt.H, GraphicsUnit.Pixel, ia);
                    if (arts[i].FreeText != null)
                    {
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                        g.InterpolationMode = InterpolationMode.Default;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        Rectangle a = arts[i].TextArea;
                        RectangleF r = new RectangleF(dst.X + a.X * scale + 3, dst.Y + a.Y * scale + 2, a.Width * scale - 6, a.Height * scale - 4);
                        using (Font f = new Font("DejaVu Sans", 6.5f * scale * 0.75f, GraphicsUnit.Point))
                        using (Brush b = new SolidBrush(Color.FromArgb(234, 248, 255)))
                            g.DrawString(arts[i].FreeText, f, b, r);
                    }
                    arts[i].Dispose();
                }
                using (BadgeArt badge = new BadgeArt())
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    Rectangle dst = new Rectangle(pad, pad + rows * (ch + pad), BadgeArt.W * scale, BadgeArt.H * scale);
                    g.DrawImage(badge.Render(4 * 60 + 17, 0.86, 0), dst, 0, 0, BadgeArt.W, BadgeArt.H, GraphicsUnit.Pixel, ia);
                    dst.X += BadgeArt.W * scale + pad;
                    g.DrawImage(badge.Render(38, 0.12, 0), dst, 0, 0, BadgeArt.W, BadgeArt.H, GraphicsUnit.Pixel, ia);
                    dst.X += BadgeArt.W * scale + pad;
                    g.DrawImage(badge.Render(95 * 60, 1, 0), dst, 0, 0, BadgeArt.W, BadgeArt.H, GraphicsUnit.Pixel, ia);
                }
                sheet.Save(System.IO.Path.Combine(outDir, "remote-notify.png"), ImageFormat.Png);
            }
        }

        private static NotifyArt Make(NotifyKind kind, string title, string big, string line3, string free, string button, double level)
        {
            return Make(kind, title, big, line3, free, button, level, null);
        }

        private static NotifyArt Make(NotifyKind kind, string title, string big, string line3, string free, string button, double level, string button2)
        {
            NotifyArt a = new NotifyArt(kind);
            a.Title = title; a.Big = big; a.Line3 = line3; a.FreeText = free; a.Button = button; a.Level = level; a.Button2 = button2;
            return a;
        }
    }
}
