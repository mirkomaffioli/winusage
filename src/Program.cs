using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WinUsage
{
    internal static class Program
    {
        private const string QuitEventName = @"Local\WinUsage.Quit";

        [STAThread]
        private static void Main(string[] args)
        {
            // "WinUsage.exe --quit": chiede all'istanza in esecuzione di chiudersi salvando i dati (usato dall'installer).
            if (args.Length > 0 && string.Equals(args[0], "--quit", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(QuitEventName)) ev.Set();
                }
                catch (Exception) { /* nessuna istanza in esecuzione */ }
                return;
            }

            // Una sola istanza per sessione utente ("Local\" = per sessione, quindi più utenti possono avere la propria).
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\WinUsage.SingleInstance", out createdNew))
            {
                if (!createdNew) return;

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUsage");
                try { Directory.CreateDirectory(dir); } catch { }
                Log.FilePath = Path.Combine(dir, "winusage.log");

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    if (ex != null) Log.Write(ex);
                };

                using (EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
                {
                    TrayContext context = new TrayContext(Path.Combine(dir, "usage.db"));
                    RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(
                        quit, delegate(object state, bool timedOut) { context.RequestQuit(); }, null, Timeout.Infinite, true);
                    Application.Run(context);
                    wait.Unregister(null);
                }
            }
        }
    }
}
