using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace WinUsage
{
    /// <summary>Dati dello storico degli ultimi N giorni (dal più vecchio a oggi).</summary>
    internal sealed class HistoryData
    {
        public string[] DayKeys;
        public DateTime[] Dates;
        public double[][] Seconds;   // [giorno][timer]

        public static CultureInfo Italian()
        {
            try { return new CultureInfo("it-IT"); }
            catch (Exception) { return CultureInfo.InvariantCulture; }
        }

        /// <param name="liveToday">Valori di oggi ancora non salvati (se null si usa solo il database).</param>
        public static HistoryData Build(Storage storage, DateTime todayLocal, double[] liveToday, int days)
        {
            HistoryData d = new HistoryData();
            d.DayKeys = new string[days];
            d.Dates = new DateTime[days];
            d.Seconds = new double[days][];
            for (int i = 0; i < days; i++)
            {
                DateTime date = todayLocal.Date.AddDays(i - (days - 1));
                d.Dates[i] = date;
                d.DayKeys[i] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                d.Seconds[i] = new double[TimerIds.Count];
            }
            Dictionary<string, long[]> stored = storage.GetRange(d.DayKeys[0], d.DayKeys[days - 1]);
            for (int i = 0; i < days; i++)
            {
                long[] row;
                if (stored.TryGetValue(d.DayKeys[i], out row))
                    for (int t = 0; t < TimerIds.Count; t++) d.Seconds[i][t] = row[t];
            }
            if (liveToday != null)
            {
                for (int t = 0; t < TimerIds.Count; t++)
                    d.Seconds[days - 1][t] = Math.Max(d.Seconds[days - 1][t], liveToday[t]);
            }
            return d;
        }

        public string ShortLabel(int i)
        {
            if (i == DayKeys.Length - 1) return "oggi";
            return Dates[i].ToString("ddd dd/MM", Italian());
        }

        public string LongLabel(int i)
        {
            string s = Dates[i].ToString("dddd d MMMM", Italian());
            if (s.Length > 0) s = char.ToUpper(s[0], Italian()) + s.Substring(1);
            return i == DayKeys.Length - 1 ? s + " (oggi)" : s;
        }

        public double Total(int timer)
        {
            double t = 0;
            for (int i = 0; i < Seconds.Length; i++) t += Seconds[i][timer];
            return t;
        }
    }

    /// <summary>Grafico a barre raggruppate (Sistema / Browser / Steam per giorno). Solo System.Drawing.</summary>
    internal static class HistoryChart
    {
        public static readonly Color[] Colors =
        {
            Color.FromArgb(37, 99, 235), Color.FromArgb(245, 158, 11), Color.FromArgb(16, 185, 129)
        };

        private static string AxisLabel(double hours)
        {
            if (hours <= 0) return "0";
            if (Math.Abs(hours - Math.Round(hours)) < 0.001) return ((int)Math.Round(hours)) + "h";
            return ((int)Math.Round(hours * 60)) + "m";
        }

        public static void Draw(Graphics g, Rectangle r, HistoryData data, Font font, Font bold, Color fore, Color back)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(back);

            Color grid = Color.FromArgb(60, fore);
            int left = 44, right = 12, top = 30, bottom = 26;
            Rectangle plot = new Rectangle(r.X + left, r.Y + top, Math.Max(10, r.Width - left - right), Math.Max(10, r.Height - top - bottom));

            // scala verticale con passo "tondo"
            double max = 0;
            for (int i = 0; i < data.Seconds.Length; i++)
                for (int t = 0; t < TimerIds.Count; t++) max = Math.Max(max, data.Seconds[i][t]);
            double maxH = max / 3600.0;
            double[] steps = { 0.25, 0.5, 1, 2, 3, 4, 6, 8, 12, 24 };
            double step = steps[steps.Length - 1];
            foreach (double s in steps) { if (maxH / s <= 5) { step = s; break; } }
            double topH = Math.Max(step * 2, Math.Ceiling(maxH / step) * step);

            using (Pen gridPen = new Pen(grid))
            using (Brush foreBrush = new SolidBrush(fore))
            using (StringFormat right_ = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
            {
                for (double h = 0; h <= topH + 0.0001; h += step)
                {
                    int y = plot.Bottom - (int)Math.Round(h / topH * plot.Height);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(AxisLabel(h), font, foreBrush, new RectangleF(r.X, y - 10, left - 6, 20), right_);
                }

                int n = data.Seconds.Length;
                float groupW = (float)plot.Width / n;
                int barW = (int)Math.Max(4, Math.Min(26, (groupW - 14) / TimerIds.Count));
                for (int i = 0; i < n; i++)
                {
                    float gx = plot.Left + i * groupW + (groupW - barW * TimerIds.Count) / 2f;
                    for (int t = 0; t < TimerIds.Count; t++)
                    {
                        int hgt = (int)Math.Round(data.Seconds[i][t] / 3600.0 / topH * plot.Height);
                        if (data.Seconds[i][t] > 0 && hgt < 1) hgt = 1;
                        using (Brush b = new SolidBrush(Colors[t]))
                            g.FillRectangle(b, (int)gx + t * barW, plot.Bottom - hgt, barW - 1, hgt);
                    }
                    bool today = i == n - 1;
                    g.DrawString(data.ShortLabel(i), today ? bold : font, foreBrush,
                        new RectangleF(plot.Left + i * groupW, plot.Bottom + 5, groupW, 20), center);
                }

                // legenda
                int lx = plot.Left;
                for (int t = 0; t < TimerIds.Count; t++)
                {
                    using (Brush b = new SolidBrush(Colors[t])) g.FillRectangle(b, lx, r.Y + 9, 10, 10);
                    g.DrawString(TimerIds.Labels[t], font, foreBrush, lx + 14, r.Y + 5);
                    lx += 14 + (int)g.MeasureString(TimerIds.Labels[t], font).Width + 14;
                }
            }
        }
    }
}
