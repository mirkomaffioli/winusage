using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(IntPtr hObj, int index, StringBuilder info, int length, out int needed);

        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const int UOI_NAME = 2;

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

        /// <summary>
        /// Vero se l'utente non sta usando la sessione: schermo bloccato, salvaschermo o sessione non attiva
        /// (cambio utente rapido). Due controlli indipendenti: lo stato delle notifiche di Windows e il desktop che
        /// riceve l'input, che con lo schermo bloccato è quello protetto di Winlogon e non è accessibile.
        /// </summary>
        public static bool UserAway()
        {
            if (NotificationState() == 1) return true;
            try
            {
                IntPtr desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                // Accesso negato: l'input va al desktop protetto (schermata di blocco). Altri errori non dicono nulla.
                if (desk == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
                try
                {
                    StringBuilder name = new StringBuilder(256);
                    int needed;
                    if (GetUserObjectInformation(desk, UOI_NAME, name, name.Capacity * 2, out needed))
                        return !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
                }
                finally { CloseDesktop(desk); }
            }
            catch (Exception) { }
            return false;
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

        /// <summary>Argomento aggiunto all'avvio automatico: così il messaggio al telefono distingue l'accensione dall'avvio a mano.</summary>
        public const string Argument = "--autostart";

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                    {
                        string cmd = "\"" + System.Windows.Forms.Application.ExecutablePath + "\" " + Argument;
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
