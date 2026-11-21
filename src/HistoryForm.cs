using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestra "Storico": grafico a barre e tabella dei tre timer, sugli ultimi 7 o 30 giorni.</summary>
    internal sealed class HistoryForm : Form
    {
        private readonly Storage storage;
        private readonly UsageTracker tracker;
        private readonly ChartPanel chart = new ChartPanel();
        private readonly ListView list = new ListView();
        private readonly RadioButton rb7 = new RadioButton();
        private readonly RadioButton rb30 = new RadioButton();
        private readonly Label summary = new Label();
        private readonly Timer refresh = new Timer();
        private readonly Font boldFont;
        private HistoryData data;
        private int days;

        private sealed class ChartPanel : Panel
        {
            public ChartPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }

        public HistoryForm(Storage storage, UsageTracker tracker, Icon icon, int initialDays)
        {
            this.storage = storage;
            this.tracker = tracker;
            days = initialDays == 30 ? 30 : 7;

            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            boldFont = new Font(Font, FontStyle.Bold);
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 620);
            MinimumSize = new Size(600, 500);
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(14);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            // intestazione: titolo + scelta del periodo
            TableLayoutPanel head = new TableLayoutPanel();
            head.ColumnCount = 2;
            head.RowCount = 1;
            head.Dock = DockStyle.Fill;
            head.AutoSize = true;
            head.Margin = new Padding(0, 0, 0, 8);
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label title = new Label();
            title.Text = "Tempo di utilizzo per giorno (standby e schermo bloccato esclusi)";
            title.Font = boldFont;
            title.AutoSize = true;
            title.Anchor = AnchorStyles.Left;
            head.Controls.Add(title, 0, 0);

            FlowLayoutPanel period = new FlowLayoutPanel();
            period.AutoSize = true;
            period.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            period.WrapContents = false;
            period.Anchor = AnchorStyles.Right;
            Label pl = new Label();
            pl.Text = "Periodo:";
            pl.AutoSize = true;
            pl.Margin = new Padding(0, 6, 6, 0);
            rb7.Text = "7 giorni";
            rb30.Text = "30 giorni";
            foreach (RadioButton rb in new RadioButton[] { rb7, rb30 })
            {
                rb.AutoSize = true;
                rb.Appearance = Appearance.Button;
                rb.TextAlign = ContentAlignment.MiddleCenter;
                rb.MinimumSize = new Size(80, 0);
                rb.Margin = new Padding(0, 0, 4, 0);
            }
            rb7.Checked = days == 7;
            rb30.Checked = days == 30;
            rb7.CheckedChanged += delegate { if (rb7.Checked) SetDays(7); };
            rb30.CheckedChanged += delegate { if (rb30.Checked) SetDays(30); };
            period.Controls.Add(pl);
            period.Controls.Add(rb7);
            period.Controls.Add(rb30);
            head.Controls.Add(period, 1, 0);
            root.Controls.Add(head, 0, 0);

            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(0, 0, 0, 6);
            chart.Paint += OnChartPaint;
            root.Controls.Add(chart, 0, 1);

            summary.AutoSize = true;
            summary.ForeColor = SystemColors.GrayText;
            summary.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(summary, 0, 2);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("Giorno", 230);
            for (int t = 0; t < TimerIds.Count; t++)
            {
                ColumnHeader h = list.Columns.Add(TimerIds.Labels[t], 100);
                h.TextAlign = HorizontalAlignment.Right;
            }
            root.Controls.Add(list, 0, 3);

            Controls.Add(root);

            refresh.Interval = 5000;
            refresh.Tick += delegate { LoadData(false); };
            refresh.Start();
            LoadData(true);
        }

        /// <summary>Cambia il periodo mostrato (7 o 30 giorni).</summary>
        public void SetDays(int value)
        {
            int d = value == 30 ? 30 : 7;
            if (rb7.Checked != (d == 7)) rb7.Checked = d == 7;
            if (rb30.Checked != (d == 30)) rb30.Checked = d == 30;
            if (d == days && data != null) return;
            days = d;
            LoadData(true);
        }

        /// <summary>Ricarica i dati (ad esempio dopo una pulizia del database).</summary>
        public void Reload()
        {
            LoadData(false);
        }

        private void LoadData(bool scrollToTop)
        {
            Text = "WinUsage - Storico degli ultimi " + days + " giorni";

            double[] live = new double[TimerIds.Count];
            for (int t = 0; t < TimerIds.Count; t++) live[t] = tracker.Today(t);
            data = HistoryData.Build(storage, DateTime.Now, live, days);

            int top = (!scrollToTop && list.TopItem != null) ? list.TopItem.Index : 0;

            list.BeginUpdate();
            list.Items.Clear();
            ListViewItem total = new ListViewItem("Totale " + days + " giorni");
            ListViewItem avg = new ListViewItem("Media al giorno (" + days + " giorni)");
            ListViewItem avgUsed = new ListViewItem("Media nei giorni di utilizzo");
            for (int t = 0; t < TimerIds.Count; t++)
            {
                total.SubItems.Add(Fmt.Compact(data.Total(t)));
                avg.SubItems.Add(Fmt.Compact(data.Total(t) / days));
                avgUsed.SubItems.Add(Fmt.Compact(data.AverageOverUsedDays(t)));
            }
            foreach (ListViewItem it in new ListViewItem[] { total, avg, avgUsed })
            {
                it.Font = boldFont;
                it.BackColor = SystemColors.ControlLight;
                list.Items.Add(it);
            }
            for (int i = days - 1; i >= 0; i--)   // dal più recente
            {
                ListViewItem item = new ListViewItem(data.LongLabel(i));
                for (int t = 0; t < TimerIds.Count; t++) item.SubItems.Add(Fmt.Compact(data.Seconds[i][t]));
                if (i == days - 1) item.Font = boldFont;
                list.Items.Add(item);
            }
            list.EndUpdate();
            if (top > 0 && top < list.Items.Count) list.TopItem = list.Items[top];

            int used = data.DaysWithUsage();
            summary.Text = "Giorni con utilizzo: " + used + " su " + days +
                ".  La linea tratteggiata è la media del timer Sistema nei giorni di utilizzo.";

            chart.Invalidate();
        }

        private void OnChartPaint(object sender, PaintEventArgs e)
        {
            if (data == null) return;
            HistoryChart.Draw(e.Graphics, chart.ClientRectangle, data, Font, boldFont, ForeColor, BackColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Stop();
                refresh.Dispose();
                boldFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
