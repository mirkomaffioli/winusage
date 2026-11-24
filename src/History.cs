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

        /// <summary>Cultura per i nomi di giorni e mesi (italiano o inglese, come l'interfaccia).</summary>
        public static CultureInfo Italian()
        {
            return L.Culture;
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
            if (i == DayKeys.Length - 1) return L.T("oggi", "today");
            return Dates[i].ToString("ddd dd/MM", Italian());
        }

        public string LongLabel(int i)
        {
            string s = Dates[i].ToString("dddd d MMMM", Italian());
            if (s.Length > 0) s = char.ToUpper(s[0], Italian()) + s.Substring(1);
            return i == DayKeys.Length - 1 ? s + L.T(" (oggi)", " (today)") : s;
        }

        public int Days { get { return DayKeys.Length; } }

        /// <summary>Etichetta corta per l'asse del grafico quando i giorni sono tanti: "08/11", "oggi".</summary>
        public string AxisLabel(int i)
        {
            if (i == DayKeys.Length - 1) return L.T("oggi", "today");
            return Dates[i].ToString("dd/MM", CultureInfo.InvariantCulture);
        }

        public double Total(int timer)
        {
            double t = 0;
            for (int i = 0; i < Seconds.Length; i++) t += Seconds[i][timer];
            return t;
        }

        /// <summary>Giorni del periodo in cui il computer è stato usato (timer Sistema maggiore di zero).</summary>
        public int DaysWithUsage()
        {
            int n = 0;
            for (int i = 0; i < Seconds.Length; i++) if (Seconds[i][TimerIds.System] > 0) n++;
            return n;
        }

        /// <summary>Media sui soli giorni di utilizzo (esclude i giorni a PC spento o prima dell'installazione).</summary>
        public double AverageOverUsedDays(int timer)
        {
            int n = DaysWithUsage();
            return n == 0 ? 0 : Total(timer) / n;
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
                bool dense = n > 14;   // storico a 30 giorni: barre sottili ed etichette diradate
                float gap = dense ? Math.Max(2f, groupW * 0.2f) : 14f;
                int barW = (int)Math.Max(dense ? 1 : 4, Math.Min(26, (groupW - gap) / TimerIds.Count));

                // un'etichetta ogni "every" giorni, contando a ritroso da oggi (che ha sempre la sua)
                int every = 1;
                if (dense)
                {
                    float labelW = g.MeasureString("00/00", font).Width + 10;
                    every = Math.Max(1, (int)Math.Ceiling(labelW / groupW));
                }

                for (int i = 0; i < n; i++)
                {
                    float gx = plot.Left + i * groupW + (groupW - barW * TimerIds.Count) / 2f;
                    // nei periodi lunghi una riga leggera separa le settimane (prima del lunedì)
                    if (dense && i > 0 && data.Dates[i].DayOfWeek == DayOfWeek.Monday)
                    {
                        using (Pen weekPen = new Pen(Color.FromArgb(35, fore)))
                        {
                            int wx = plot.Left + (int)Math.Round(i * groupW);
                            g.DrawLine(weekPen, wx, plot.Top, wx, plot.Bottom);
                        }
                    }
                    for (int t = 0; t < TimerIds.Count; t++)
                    {
                        int hgt = (int)Math.Round(data.Seconds[i][t] / 3600.0 / topH * plot.Height);
                        if (data.Seconds[i][t] > 0 && hgt < 1) hgt = 1;
                        using (Brush b = new SolidBrush(Colors[t]))
                            g.FillRectangle(b, (int)gx + t * barW, plot.Bottom - hgt, barW - 1, hgt);
                    }
                    bool today = i == n - 1;
                    if (!dense)
                    {
                        g.DrawString(data.ShortLabel(i), today ? bold : font, foreBrush,
                            new RectangleF(plot.Left + i * groupW, plot.Bottom + 5, groupW, 20), center);
                    }
                    else if ((n - 1 - i) % every == 0)
                    {
                        float cx = plot.Left + i * groupW + groupW / 2f;
                        float w = groupW * every;
                        g.DrawString(data.AxisLabel(i), today ? bold : font, foreBrush,
                            new RectangleF(cx - w / 2f, plot.Bottom + 5, w, 20), center);
                    }
                }

                // media giornaliera del timer Sistema (sui giorni di utilizzo), linea tratteggiata
                double avg = data.AverageOverUsedDays(TimerIds.System);
                if (avg > 0)
                {
                    int ay = plot.Bottom - (int)Math.Round(avg / 3600.0 / topH * plot.Height);
                    using (Pen avgPen = new Pen(Colors[TimerIds.System], 1f))
                    {
                        avgPen.DashStyle = DashStyle.Dash;
                        g.DrawLine(avgPen, plot.Left, ay, plot.Right, ay);
                    }
                    string avgText = L.T("media ", "average ") + Fmt.Compact(avg);
                    SizeF sz = g.MeasureString(avgText, font);
                    float ty = Math.Max(plot.Top, ay - sz.Height);
                    using (Brush backBrush = new SolidBrush(Color.FromArgb(220, back)))
                        g.FillRectangle(backBrush, plot.Right - sz.Width, ty, sz.Width, sz.Height);
                    using (Brush avgBrush = new SolidBrush(Colors[TimerIds.System]))
                        g.DrawString(avgText, font, avgBrush, plot.Right - sz.Width, ty);
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
