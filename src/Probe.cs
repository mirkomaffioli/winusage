using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinUsage
{
    /// <summary>Sonde sullo stato del sistema (finestra in primo piano, Steam).</summary>
    internal static class Probe
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        private static uint cachedPid;
        private static string cachedName = "";
        private static DateTime cachedAt = DateTime.MinValue;

        private static DateTime steamCheckedAt = DateTime.MinValue;
        private static bool steamRunning;

        /// <summary>Nome del processo (senza .exe, minuscolo) proprietario della finestra in primo piano.</summary>
        public static string ForegroundProcessName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";

                DateTime now = DateTime.UtcNow;
                if (pid == cachedPid && (now - cachedAt).TotalSeconds < 30) return cachedName;

                string name;
                using (Process p = Process.GetProcessById((int)pid))
                {
                    name = p.ProcessName.ToLowerInvariant();
                }
                cachedPid = pid;
                cachedName = name;
                cachedAt = now;
                return name;
            }
            catch
            {
                return "";   // processo terminato nel frattempo o accesso negato
            }
        }

        /// <summary>
        /// Stato di Windows per le notifiche (QUERY_USER_NOTIFICATION_STATE): 1 schermo bloccato/salvaschermo,
        /// 2 app a schermo intero, 3 gioco/app Direct3D a schermo intero, 4 modalita' presentazione,
        /// 5 normale, 6 ore silenziose, 7 app a schermo intero dello Store. In caso di errore restituisce 5.
        /// </summary>
        public static int NotificationState()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) == 0) return state;
            }
            catch (Exception) { }
            return 5;
        }

        public static bool IsFullscreen(int notificationState)
        {
            return notificationState == 2 || notificationState == 3 || notificationState == 4 || notificationState == 7;
        }

        /// <summary>Il monitor della finestra in primo piano (dove sta il gioco); altrimenti quello principale.</summary>
        public static Screen ForegroundScreen()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero) return Screen.FromHandle(hwnd);
            }
            catch (Exception) { }
            return Screen.PrimaryScreen;
        }

        /// <summary>
        /// Vero se Steam ha un gioco in esecuzione (valore RunningAppID nel registro dell'utente)
        /// oppure se la finestra in primo piano è il client Steam.
        /// </summary>
        public static bool SteamActive(string foregroundProcess)
        {
            if (foregroundProcess == "steam" || foregroundProcess == "steamwebhelper") return true;
            return SteamGameRunning();
        }

        private static bool SteamGameRunning()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("RunningAppID");
                    if (!(v is int) || (int)v == 0) return false;
                }
                // RunningAppID può restare valorizzato se Steam si chiude male: verifica che il client esista.
                DateTime now = DateTime.UtcNow;
                if ((now - steamCheckedAt).TotalSeconds >= 10)
                {
                    steamCheckedAt = now;
                    Process[] ps = Process.GetProcessesByName("steam");
                    steamRunning = ps.Length > 0;
                    foreach (Process p in ps) p.Dispose();
                }
                return steamRunning;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Avvio automatico con Windows (chiave Run dell'utente corrente, nessun permesso di amministratore).</summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WinUsage";

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                    {
                        string cmd = "\"" + System.Windows.Forms.Application.ExecutablePath + "\"";
                        object current = k.GetValue(ValueName);
                        if (current == null || !string.Equals(current.ToString(), cmd, StringComparison.OrdinalIgnoreCase))
                            k.SetValue(ValueName, cmd);
                    }
                    else
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
    }
}
