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

            Text = L.T("WinUsage - Manutenzione database", "WinUsage - Database maintenance");
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
            root.Controls.Add(SectionTitle(L.T("Operazioni", "Operations")));
            FlowLayoutPanel ops = Row(0);
            ops.Controls.Add(MakeButton(L.T("Verifica integrità", "Check integrity"), OnCheck));
            ops.Controls.Add(MakeButton(L.T("Compatta", "Compact"), OnCompact));
            ops.Controls.Add(MakeButton("Backup...", OnBackup));
            ops.Controls.Add(MakeButton(L.T("Esporta CSV...", "Export CSV..."), OnExport));
            ops.Controls.Add(MakeButton(L.T("Apri cartella", "Open folder"), OnOpenFolder));
            root.Controls.Add(ops);
            root.Controls.Add(Hint(L.T("Compatta recupera lo spazio lasciato dai dati eliminati. Il backup è una copia completa " +
                                       "del database; il CSV contiene i totali di ogni giorno e si apre con Excel.",
                                       "Compact recovers the space left by deleted data. The backup is a full copy of the " +
                                       "database; the CSV contains the totals of each day and opens in Excel.")));

            // Pulizia
            root.Controls.Add(SectionTitle(L.T("Pulizia", "Clean-up")));
            FlowLayoutPanel older = Row(0);
            older.Controls.Add(InlineLabel(L.T("Elimina i dati più vecchi di", "Delete data older than")));
            numOlder.Minimum = 1;
            numOlder.Maximum = AppSettings.MaxRetentionDays;
            numOlder.Value = 365;
            numOlder.Width = 70;
            older.Controls.Add(numOlder);
            older.Controls.Add(InlineLabel(L.T("giorni", "days")));
            older.Controls.Add(MakeButton(L.T("Elimina...", "Delete..."), OnDeleteOlder));
            root.Controls.Add(older);

            FlowLayoutPanel all = Row(0);
            all.Controls.Add(MakeButton(L.T("Azzera tutto lo storico...", "Clear all history..."), OnDeleteAll));
            root.Controls.Add(all);
            root.Controls.Add(Hint(L.T("La configurazione non viene toccata. Prima di eliminare puoi fare un backup.", "Settings are not touched. You can make a backup before deleting.")));

            // Conservazione automatica
            root.Controls.Add(SectionTitle(L.T("Conservazione automatica", "Automatic retention")));
            FlowLayoutPanel keep = Row(0);
            chkRetention.Text = L.T("Elimina automaticamente i dati più vecchi di", "Automatically delete data older than");
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
            saveRetention = MakeButton(L.T("Salva", "Save"), OnSaveRetention);
            saveRetention.Enabled = false;
            chkRetention.CheckedChanged += delegate { numRetention.Enabled = chkRetention.Checked; UpdateSaveRetention(); };
            numRetention.ValueChanged += delegate { UpdateSaveRetention(); };
            keep.Controls.Add(chkRetention);
            keep.Controls.Add(numRetention);
            keep.Controls.Add(InlineLabel(L.T("giorni", "days")));
            keep.Controls.Add(saveRetention);
            root.Controls.Add(keep);
            root.Controls.Add(Hint(L.T("Controllata all'avvio e poi ogni 6 ore. Minimo " + AppSettings.MinRetentionDays +
                                       " giorni, così lo storico a 30 giorni resta completo.",
                                       "Checked at start-up and then every 6 hours. Minimum " + AppSettings.MinRetentionDays +
                                       " days, so the 30-day history stays complete.")));

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
            close.Text = L.T("Chiudi", "Close");
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
                    ? L.T("nessun giorno registrato", "no days recorded")
                    : i.Days + Days(i.Days) + L.T(", dal ", ", from ") + Pretty(i.FirstDay) + L.T(" al ", " to ") + Pretty(i.LastDay);
                info.Text =
                    "File: " + i.Path + "\r\n" +
                    L.T("Dimensione: ", "Size: ") + DbInfo.Size(i.FileBytes + i.WalBytes) +
                    L.T(" (spazio recuperabile con Compatta: ", " (space recoverable with Compact: ") + DbInfo.Size(i.FreeBytes) + ")\r\n" +
                    L.T("Storico: ", "History: ") + period + "\r\n" +
                    L.T("Sessioni registrate: ", "Recorded sessions: ") + i.Sessions + "    SQLite " + i.SqliteVersion;
            }
            catch (Exception ex)
            {
                info.Text = "File: " + storage.DbPath + "\r\n" + L.T("Database non disponibile: ", "Database not available: ") + ex.Message;
            }
        }

        /// <summary>" giorno" / " giorni" ( " day" / " days").</summary>
        private static string Days(long n)
        {
            return n == 1 ? L.T(" giorno", " day") : L.T(" giorni", " days");
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
                SetStatus(what + L.T(" non riuscito: ", " failed: ") + ex.Message, true);
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
            Run(L.T("Verifica", "Check"), delegate
            {
                List<string> r = storage.IntegrityCheck();
                if (r.Count == 1 && r[0] == "ok") return L.T("Verifica completata: il database è integro.", "Check complete: the database is intact.");
                string details = string.Join("\r\n", r.GetRange(0, Math.Min(10, r.Count)).ToArray());
                MessageBox.Show(this, L.T("SQLite ha trovato dei problemi:", "SQLite found some problems:") + "\r\n\r\n" + details +
                    "\r\n\r\n" + L.T("Fai un backup e, se lo storico appare sbagliato, azzeralo.",
                                     "Make a backup and, if the history looks wrong, clear it."),
                    L.T("Verifica integrità", "Integrity check"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return L.T("La verifica ha trovato " + r.Count + " problemi (dettagli nel messaggio).",
                           "The check found " + r.Count + " problems (details in the message).");
            });
        }

        private void OnCompact(object sender, EventArgs e)
        {
            Run(L.T("Compattazione", "Compaction"), delegate
            {
                long saved = storage.Compact();
                return L.T("Database compattato", "Database compacted") + (saved > 0 ? L.T(": recuperati ", ": recovered ") + DbInfo.Size(saved) + "." : ".");
            });
        }

        private void OnBackup(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = L.T("Salva una copia del database", "Save a copy of the database");
                dlg.Filter = L.T("Database SQLite (*.db)|*.db|Tutti i file (*.*)|*.*", "SQLite database (*.db)|*.db|All files (*.*)|*.*");
                dlg.FileName = "WinUsage-backup-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".db";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string target = dlg.FileName;
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(storage.DbPath), StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(L.T("Scegli un file diverso dal database in uso.", "Choose a file other than the database in use."), true);
                    return;
                }
                Run("Backup", delegate
                {
                    storage.Backup(target);
                    return L.T("Backup salvato in ", "Backup saved to ") + target;
                });
            }
        }

        private void OnExport(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = L.T("Esporta lo storico in CSV", "Export history to CSV");
                dlg.Filter = L.T("File CSV (*.csv)|*.csv", "CSV file (*.csv)|*.csv");
                dlg.FileName = "WinUsage-storico-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string target = dlg.FileName;
                Run(L.T("Esportazione", "Export"), delegate
                {
                    int n = storage.ExportCsv(target);
                    return L.T("Esportati ", "Exported ") + n + Days(n) + L.T(" in ", " to ") + target;
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
                SetStatus(L.T("Impossibile aprire la cartella: ", "Cannot open the folder: ") + ex.Message, true);
            }
        }

        private void OnDeleteOlder(object sender, EventArgs e)
        {
            int n = (int)numOlder.Value;
            string firstKept = Storage.FirstDayToKeep(DateTime.Now, n);
            if (MessageBox.Show(this,
                    L.T("Eliminare definitivamente i dati precedenti al " + Pretty(firstKept) + " (più vecchi di " + n +
                        " giorni)?\r\n\r\nL'operazione non si può annullare.",
                        "Permanently delete the data before " + Pretty(firstKept) + " (older than " + n +
                        " days)?\r\n\r\nThis cannot be undone."),
                    L.T("Elimina dati vecchi", "Delete old data"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Run(L.T("Pulizia", "Clean-up"), delegate
            {
                int days = storage.DeleteBefore(firstKept);
                RaiseDataChanged();
                return days == 0 ? L.T("Nessun dato più vecchio di " + n + " giorni.", "No data older than " + n + " days.")
                                 : L.T("Eliminati ", "Deleted ") + days + Days(days) + L.T(" di storico. ", " of history. ") +
                                   L.T("Usa Compatta per recuperare lo spazio.", "Use Compact to recover the space.");
            });
        }

        private void OnDeleteAll(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                    L.T("Eliminare definitivamente tutto lo storico (totali giornalieri e sessioni passate)?\r\n" +
                        "I totali di oggi ripartono da zero; la configurazione resta.\r\n\r\nL'operazione non si può annullare.",
                        "Permanently delete all history (daily totals and past sessions)?\r\n" +
                        "Today's totals start again from zero; settings stay.\r\n\r\nThis cannot be undone."),
                    L.T("Azzera storico", "Clear history"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Run(L.T("Azzeramento", "Clearing"), delegate
            {
                storage.DeleteAllUsage(tracker.SessionId);
                tracker.ResetToday();
                storage.Compact();
                RaiseDataChanged();
                return L.T("Storico azzerato.", "History cleared.");
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
                        L.T("Con questa regola vengono eliminati subito ", "With this rule ") + toDelete + Days(toDelete) +
                        L.T(" di storico (più vecchi di " + days + " giorni).\r\n\r\nContinuare?",
                            " of history (older than " + days + " days) are deleted right away.\r\n\r\nContinue?"),
                        L.T("Conservazione automatica", "Automatic retention"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            Run(L.T("Conservazione automatica", "Automatic retention"), delegate
            {
                savedRetention = days;
                Action<int> h = RetentionChanged;
                if (h != null) h(days);   // TrayContext salva e applica la regola
                UpdateSaveRetention();
                RaiseDataChanged();
                return days > 0
                    ? L.T("Salvato: i dati più vecchi di " + days + " giorni vengono eliminati automaticamente.",
                          "Saved: data older than " + days + " days is deleted automatically.")
                    : L.T("Salvato: lo storico viene conservato per sempre.", "Saved: history is kept forever.");
            });
        }
    }
}
