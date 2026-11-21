using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Finestra "Manutenzione database": informazioni, verifica, compattazione, backup, esportazione CSV,
    /// pulizia dei dati vecchi e conservazione automatica.
    /// </summary>
    internal sealed class MaintenanceForm : Form
    {
        private readonly Storage storage;
        private readonly UsageTracker tracker;

        private readonly Label info = new Label();
        private readonly Label status = new Label();
        private readonly NumericUpDown numOlder = new NumericUpDown();
        private readonly CheckBox chkRetention = new CheckBox();
        private readonly NumericUpDown numRetention = new NumericUpDown();
        private readonly Button saveRetention;
        private int savedRetention;

        /// <summary>Nuovo valore della conservazione automatica (0 = conserva tutto).</summary>
        public event Action<int> RetentionChanged;

        /// <summary>Lo storico è cambiato (pulizia o azzeramento): chi lo mostra deve ricaricarlo.</summary>
        public event Action DataChanged;

        public MaintenanceForm(Storage storage, UsageTracker tracker, int retentionDays, Icon icon)
        {
            this.storage = storage;
            this.tracker = tracker;

            Text = "WinUsage - Manutenzione database";
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            if (icon != null) Icon = icon;

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // Informazioni
            root.Controls.Add(SectionTitle("Database"));
            info.AutoSize = true;
            info.MaximumSize = new Size(560, 0);
            info.Margin = new Padding(3, 0, 3, 6);
            root.Controls.Add(info);

            // Operazioni
            root.Controls.Add(SectionTitle("Operazioni"));
            FlowLayoutPanel ops = Row(0);
            ops.Controls.Add(MakeButton("Verifica integrità", OnCheck));
            ops.Controls.Add(MakeButton("Compatta", OnCompact));
            ops.Controls.Add(MakeButton("Backup...", OnBackup));
            ops.Controls.Add(MakeButton("Esporta CSV...", OnExport));
            ops.Controls.Add(MakeButton("Apri cartella", OnOpenFolder));
            root.Controls.Add(ops);
            root.Controls.Add(Hint("Compatta recupera lo spazio lasciato dai dati eliminati. Il backup è una copia completa " +
                                   "del database; il CSV contiene i totali di ogni giorno e si apre con Excel."));

            // Pulizia
            root.Controls.Add(SectionTitle("Pulizia"));
            FlowLayoutPanel older = Row(0);
            older.Controls.Add(InlineLabel("Elimina i dati più vecchi di"));
            numOlder.Minimum = 1;
            numOlder.Maximum = AppSettings.MaxRetentionDays;
            numOlder.Value = 365;
            numOlder.Width = 70;
            older.Controls.Add(numOlder);
            older.Controls.Add(InlineLabel("giorni"));
            older.Controls.Add(MakeButton("Elimina...", OnDeleteOlder));
            root.Controls.Add(older);

            FlowLayoutPanel all = Row(0);
            all.Controls.Add(MakeButton("Azzera tutto lo storico...", OnDeleteAll));
            root.Controls.Add(all);
            root.Controls.Add(Hint("La configurazione non viene toccata. Prima di eliminare puoi fare un backup."));

            // Conservazione automatica
            root.Controls.Add(SectionTitle("Conservazione automatica"));
            FlowLayoutPanel keep = Row(0);
            chkRetention.Text = "Elimina automaticamente i dati più vecchi di";
            chkRetention.AutoSize = true;
            chkRetention.Margin = new Padding(3, 5, 6, 0);
            numRetention.Minimum = AppSettings.MinRetentionDays;
            numRetention.Maximum = AppSettings.MaxRetentionDays;
            numRetention.Width = 70;
            savedRetention = retentionDays;
            chkRetention.Checked = retentionDays > 0;
            numRetention.Value = retentionDays > 0
                ? Math.Max(numRetention.Minimum, Math.Min(numRetention.Maximum, retentionDays)) : 365;
            numRetention.Enabled = chkRetention.Checked;
            saveRetention = MakeButton("Salva", OnSaveRetention);
            saveRetention.Enabled = false;
            chkRetention.CheckedChanged += delegate { numRetention.Enabled = chkRetention.Checked; UpdateSaveRetention(); };
            numRetention.ValueChanged += delegate { UpdateSaveRetention(); };
            keep.Controls.Add(chkRetention);
            keep.Controls.Add(numRetention);
            keep.Controls.Add(InlineLabel("giorni"));
            keep.Controls.Add(saveRetention);
            root.Controls.Add(keep);
            root.Controls.Add(Hint("Controllata all'avvio e poi ogni 6 ore. Minimo " + AppSettings.MinRetentionDays +
                                   " giorni, così lo storico a 30 giorni resta completo."));

            // Stato e chiusura
            status.AutoSize = true;
            status.MaximumSize = new Size(560, 0);
            status.Margin = new Padding(3, 12, 3, 6);
            root.Controls.Add(status);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Dock = DockStyle.Right;
            Button close = new Button();
            close.Text = "Chiudi";
            close.AutoSize = true;
            close.Click += delegate { Close(); };
            buttons.Controls.Add(close);
            root.Controls.Add(buttons);

            Controls.Add(root);
            CancelButton = close;

            RefreshInfo();
        }

        // ---- interfaccia ----

        private static Label SectionTitle(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            l.Margin = new Padding(3, 10, 3, 4);
            return l;
        }

        private static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(560, 0);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 2, 3, 2);
            return l;
        }

        private static Label InlineLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 6, 6, 0);
            return l;
        }

        private static FlowLayoutPanel Row(int indent)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.WrapContents = false;
            p.Margin = new Padding(indent, 0, 0, 2);
            return p;
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(3, 2, 6, 2);
            b.Click += click;
            return b;
        }

        private void RefreshInfo()
        {
            try
            {
                DbInfo i = storage.GetInfo();
                string period = i.Days == 0
                    ? "nessun giorno registrato"
                    : i.Days + (i.Days == 1 ? " giorno" : " giorni") + ", dal " + Pretty(i.FirstDay) + " al " + Pretty(i.LastDay);
                info.Text =
                    "File: " + i.Path + "\r\n" +
                    "Dimensione: " + DbInfo.Size(i.FileBytes + i.WalBytes) +
                    " (spazio recuperabile con Compatta: " + DbInfo.Size(i.FreeBytes) + ")\r\n" +
                    "Storico: " + period + "\r\n" +
                    "Sessioni registrate: " + i.Sessions + "    SQLite " + i.SqliteVersion;
            }
            catch (Exception ex)
            {
                info.Text = "File: " + storage.DbPath + "\r\nDatabase non disponibile: " + ex.Message;
            }
        }

        private static string Pretty(string dayKey)
        {
            DateTime d;
            if (DateTime.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.ToString("d MMMM yyyy", HistoryData.Italian());
            return dayKey;
        }

        private void SetStatus(string text, bool error)
        {
            status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
            status.Text = text;
        }

        /// <summary>Esegue un'operazione con la clessidra, mostrando l'esito o l'errore.</summary>
        private void Run(string what, Func<string> op)
        {
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                tracker.FlushNow(DateTime.UtcNow);   // i secondi in memoria finiscono nel database prima dell'operazione
                SetStatus(op(), false);
            }
            catch (Exception ex)
            {
                Log.Write(what + ": " + ex.Message);
                SetStatus(what + " non riuscito: " + ex.Message, true);
            }
            finally
            {
                Cursor.Current = old;
                RefreshInfo();
            }
        }

        private void RaiseDataChanged()
        {
            Action h = DataChanged;
            if (h != null) h();
        }

        // ---- operazioni ----

        private void OnCheck(object sender, EventArgs e)
        {
            Run("Verifica", delegate
            {
                List<string> r = storage.IntegrityCheck();
                if (r.Count == 1 && r[0] == "ok") return "Verifica completata: il database è integro.";
                string details = string.Join("\r\n", r.GetRange(0, Math.Min(10, r.Count)).ToArray());
                MessageBox.Show(this, "SQLite ha trovato dei problemi:\r\n\r\n" + details +
                    "\r\n\r\nFai un backup e, se lo storico appare sbagliato, azzeralo.",
                    "Verifica integrità", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return "La verifica ha trovato " + r.Count + " problemi (dettagli nel messaggio).";
            });
        }

        private void OnCompact(object sender, EventArgs e)
        {
            Run("Compattazione", delegate
            {
                long saved = storage.Compact();
                return "Database compattato" + (saved > 0 ? ": recuperati " + DbInfo.Size(saved) + "." : ".");
            });
        }

        private void OnBackup(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Salva una copia del database";
                dlg.Filter = "Database SQLite (*.db)|*.db|Tutti i file (*.*)|*.*";
                dlg.FileName = "WinUsage-backup-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".db";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string target = dlg.FileName;
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(storage.DbPath), StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Scegli un file diverso dal database in uso.", true);
                    return;
                }
                Run("Backup", delegate
                {
                    storage.Backup(target);
                    return "Backup salvato in " + target;
                });
            }
        }

        private void OnExport(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Esporta lo storico in CSV";
                dlg.Filter = "File CSV (*.csv)|*.csv";
                dlg.FileName = "WinUsage-storico-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string target = dlg.FileName;
                Run("Esportazione", delegate
                {
                    int n = storage.ExportCsv(target);
                    return "Esportati " + n + (n == 1 ? " giorno" : " giorni") + " in " + target;
                });
            }
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", "/select,\"" + storage.DbPath + "\"");
            }
            catch (Exception ex)
            {
                SetStatus("Impossibile aprire la cartella: " + ex.Message, true);
            }
        }

        private void OnDeleteOlder(object sender, EventArgs e)
        {
            int n = (int)numOlder.Value;
            string firstKept = Storage.FirstDayToKeep(DateTime.Now, n);
            if (MessageBox.Show(this,
                    "Eliminare definitivamente i dati precedenti al " + Pretty(firstKept) + " (più vecchi di " + n +
                    " giorni)?\r\n\r\nL'operazione non si può annullare.",
                    "Elimina dati vecchi", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Run("Pulizia", delegate
            {
                int days = storage.DeleteBefore(firstKept);
                RaiseDataChanged();
                return days == 0 ? "Nessun dato più vecchio di " + n + " giorni."
                                 : "Eliminati " + days + (days == 1 ? " giorno" : " giorni") + " di storico. " +
                                   "Usa Compatta per recuperare lo spazio.";
            });
        }

        private void OnDeleteAll(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                    "Eliminare definitivamente tutto lo storico (totali giornalieri e sessioni passate)?\r\n" +
                    "I totali di oggi ripartono da zero; la configurazione resta.\r\n\r\nL'operazione non si può annullare.",
                    "Azzera storico", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Run("Azzeramento", delegate
            {
                storage.DeleteAllUsage(tracker.SessionId);
                tracker.ResetToday();
                storage.Compact();
                RaiseDataChanged();
                return "Storico azzerato.";
            });
        }

        private int EditedRetention()
        {
            return chkRetention.Checked ? (int)numRetention.Value : 0;
        }

        private void UpdateSaveRetention()
        {
            saveRetention.Enabled = EditedRetention() != savedRetention;
        }

        private void OnSaveRetention(object sender, EventArgs e)
        {
            int days = EditedRetention();
            if (days > 0)
            {
                // la regola si applica subito: se elimina qualcosa, chiedi conferma mostrando quanto
                int toDelete = 0;
                try { toDelete = storage.CountDaysBefore(Storage.FirstDayToKeep(DateTime.Now, days)); }
                catch (Exception) { }
                if (toDelete > 0 && MessageBox.Show(this,
                        "Con questa regola vengono eliminati subito " + toDelete + (toDelete == 1 ? " giorno" : " giorni") +
                        " di storico (più vecchi di " + days + " giorni).\r\n\r\nContinuare?",
                        "Conservazione automatica", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            Run("Conservazione automatica", delegate
            {
                savedRetention = days;
                Action<int> h = RetentionChanged;
                if (h != null) h(days);   // TrayContext salva e applica la regola
                UpdateSaveRetention();
                RaiseDataChanged();
                return days > 0
                    ? "Salvato: i dati più vecchi di " + days + " giorni vengono eliminati automaticamente."
                    : "Salvato: lo storico viene conservato per sempre.";
            });
        }
    }
}
