using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Finestra "Storico 7 giorni": grafico a barre e tabella dei tre timer.</summary>
    internal sealed class HistoryForm : Form
    {
        private const int Days = 7;

        private readonly Storage storage;
        private readonly UsageTracker tracker;
        private readonly ChartPanel chart = new ChartPanel();
        private readonly ListView list = new ListView();
        private readonly Timer refresh = new Timer();
        private readonly Font boldFont;
        private HistoryData data;

        private sealed class ChartPanel : Panel
        {
            public ChartPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }

        public HistoryForm(Storage storage, UsageTracker tracker, Icon icon)
        {
            this.storage = storage;
            this.tracker = tracker;

            Text = "WinUsage - Storico degli ultimi 7 giorni";
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            boldFont = new Font(Font, FontStyle.Bold);
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 560);
            MinimumSize = new Size(560, 480);
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(14);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            Label title = new Label();
            title.Text = "Tempo di utilizzo per giorno (standby escluso)";
            title.Font = boldFont;
            title.AutoSize = true;
            title.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(title, 0, 0);

            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(0, 0, 0, 10);
            chart.Paint += OnChartPaint;
            root.Controls.Add(chart, 0, 1);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("Giorno", 190);
            for (int t = 0; t < TimerIds.Count; t++)
            {
                ColumnHeader h = list.Columns.Add(TimerIds.Labels[t], 100);
                h.TextAlign = HorizontalAlignment.Right;
            }
            root.Controls.Add(list, 0, 2);

            Controls.Add(root);

            refresh.Interval = 5000;
            refresh.Tick += delegate { LoadData(); };
            refresh.Start();
            LoadData();
        }

        private void LoadData()
        {
            double[] live = new double[TimerIds.Count];
            for (int t = 0; t < TimerIds.Count; t++) live[t] = tracker.Today(t);
            data = HistoryData.Build(storage, DateTime.Now, live, Days);

            list.BeginUpdate();
            list.Items.Clear();
            for (int i = Days - 1; i >= 0; i--)   // dal più recente
            {
                ListViewItem item = new ListViewItem(data.LongLabel(i));
                for (int t = 0; t < TimerIds.Count; t++) item.SubItems.Add(Fmt.Compact(data.Seconds[i][t]));
                if (i == Days - 1) item.Font = boldFont;
                list.Items.Add(item);
            }
            ListViewItem total = new ListViewItem("Totale 7 giorni");
            ListViewItem avg = new ListViewItem("Media al giorno");
            for (int t = 0; t < TimerIds.Count; t++)
            {
                total.SubItems.Add(Fmt.Compact(data.Total(t)));
                avg.SubItems.Add(Fmt.Compact(data.Total(t) / Days));
            }
            total.Font = boldFont;
            avg.Font = boldFont;
            total.BackColor = SystemColors.ControlLight;
            avg.BackColor = SystemColors.ControlLight;
            list.Items.Add(total);
            list.Items.Add(avg);
            list.EndUpdate();

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
