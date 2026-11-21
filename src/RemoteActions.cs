using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>Le azioni che il telefono può chiedere: app in uso, screenshot, blocco, spegnimento. Solo API di Windows.</summary>
    internal static class RemoteActions
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // ---- applicazione in primo piano ----

        private static uint lastPid;
        private static DateTime lastPidSinceUtc = DateTime.MinValue;
        private static string lastName = "", lastFriendly = "";

        /// <summary>Da chiamare a ogni secondo: tiene l'ora dell'ultimo cambio di applicazione in primo piano.</summary>
        public static void TrackForeground(DateTime nowUtc)
        {
            uint pid = ForegroundPid();
            if (pid != lastPid)
            {
                lastPid = pid;
                lastPidSinceUtc = nowUtc;
                lastName = "";
                lastFriendly = "";
            }
        }

        /// <summary>
        /// Processo della finestra in primo piano. Le app dello Store vivono dentro ApplicationFrameHost: si cerca la
        /// finestra figlia di un altro processo, che è l'app vera.
        /// </summary>
        private static uint ForegroundPid()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return 0;
                if (!string.Equals(ProcessNameOf(pid), "applicationframehost", StringComparison.OrdinalIgnoreCase)) return pid;
                uint inner = 0;
                EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr l)
                {
                    uint cp;
                    GetWindowThreadProcessId(child, out cp);
                    if (cp != 0 && cp != pid) { inner = cp; return false; }
                    return true;
                }, IntPtr.Zero);
                return inner != 0 ? inner : pid;
            }
            catch (Exception) { return 0; }
        }

        private static string ProcessNameOf(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>Nome leggibile (descrizione del file, es. "Google Chrome"). Funziona anche con processi elevati.</summary>
        private static string FriendlyNameOf(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(sb.ToString());
                string d = (vi.FileDescription ?? "").Trim();
                if (d.Length == 0) d = (vi.ProductName ?? "").Trim();
                return d;
            }
            catch (Exception) { return ""; }
            finally { CloseHandle(h); }
        }

        public static double IdleSeconds()
        {
            try
            {
                LASTINPUTINFO li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref li)) return 0;
                uint now = (uint)Environment.TickCount;
                return (now - li.dwTime) / 1000.0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Dati per /app. Il titolo della finestra non viene mai letto.</summary>
        public static ForegroundInfo GetForeground(bool locked, bool disconnected, DateTime lockedSinceUtc)
        {
            ForegroundInfo fi = new ForegroundInfo();
            if (disconnected) { fi.State = SessionState.Disconnected; return fi; }
            if (locked) { fi.State = SessionState.Locked; fi.LockedSinceUtc = lockedSinceUtc; return fi; }
            fi.State = SessionState.Active;
            TrackForeground(DateTime.UtcNow);
            if (lastPid != 0)
            {
                if (lastName.Length == 0) lastName = ProcessNameOf(lastPid);
                if (lastFriendly.Length == 0) lastFriendly = FriendlyNameOf(lastPid);
                fi.ProcessName = lastName;
                fi.AppName = lastFriendly.Length > 0 ? lastFriendly : lastName;
                fi.SinceUtc = lastPidSinceUtc;
            }
            fi.IdleSeconds = IdleSeconds();
            fi.SteamGame = Probe.SteamActive("");
            fi.Fullscreen = Probe.IsFullscreen(Probe.NotificationState());
            return fi;
        }

        public static string ForegroundProcessName()
        {
            uint pid = ForegroundPid();
            return pid == 0 ? "" : ProcessNameOf(pid);
        }

        // ---- screenshot ----

        /// <param name="monitor">0 = tutti i monitor, n = n-esimo monitor.</param>
        public static ScreenshotResult Capture(int monitor, bool hd, HashSet<string> exclusions)
        {
            ScreenshotResult r = new ScreenshotResult();
            if (Probe.UserAway()) { r.Error = "🔒 PC bloccato: lo screenshot non è possibile."; return r; }

            string fg = ForegroundProcessName();
            if (fg.Length > 0 && exclusions.Contains(fg))
            {
                r.Error = "🙈 Screenshot non consentito ora: in primo piano c'è un'app esclusa.";
                return r;
            }

            Rectangle area;
            Screen[] screens = Screen.AllScreens;
            if (monitor <= 0) area = SystemInformation.VirtualScreen;
            else if (monitor <= screens.Length) area = screens[monitor - 1].Bounds;
            else
            {
                r.Error = "Monitor " + monitor + " inesistente: ce ne sono " + screens.Length + ".";
                return r;
            }

            try
            {
                using (Bitmap bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

                    if (hd)
                    {
                        r.Png = true;
                        r.Data = Encode(bmp, ImageFormat.Png, 0);
                        if (r.Data.Length > 45 * 1024 * 1024) r.Error = "Screenshot troppo grande per Telegram: prova senza hd.";
                        return r;
                    }

                    // sendPhoto: larghezza + altezza al massimo 10000 pixel e file sotto i 10 MB
                    double k = Math.Min(1.0, 9000.0 / (area.Width + area.Height));
                    if (k < 1.0)
                    {
                        using (Bitmap small = new Bitmap(Math.Max(1, (int)(area.Width * k)), Math.Max(1, (int)(area.Height * k)), PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(small))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(bmp, 0, 0, small.Width, small.Height);
                            }
                            r.Data = Encode(small, ImageFormat.Jpeg, 85);
                        }
                    }
                    else r.Data = Encode(bmp, ImageFormat.Jpeg, 85);
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                r.Error = "Screenshot non riuscito.";
            }
            return r;
        }

        private static byte[] Encode(Bitmap bmp, ImageFormat format, long quality)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                if (format.Guid == ImageFormat.Jpeg.Guid)
                {
                    ImageCodecInfo codec = null;
                    foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                        if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
                    if (codec != null)
                    {
                        using (EncoderParameters ps = new EncoderParameters(1))
                        {
                            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                            bmp.Save(ms, codec, ps);
                            return ms.ToArray();
                        }
                    }
                }
                bmp.Save(ms, format);
                return ms.ToArray();
            }
        }

        // ---- blocco e spegnimento ----

        public static LockResult Lock()
        {
            if (Probe.UserAway()) return LockResult.AlreadyLocked;
            return LockWorkStation() ? LockResult.Locked : LockResult.Failed;
        }

        /// <summary>
        /// Spegne il PC senza forzare: un'app con un documento non salvato può fermare lo spegnimento (solo le app
        /// bloccate vengono chiuse). Il privilegio di spegnimento è già dell'utente: va solo attivato.
        /// </summary>
        public static bool PowerOff()
        {
            try
            {
                IntPtr token;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out token))   // ADJUST_PRIVILEGES | QUERY
                {
                    try
                    {
                        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                        tp.Count = 1;
                        tp.Attributes = 0x2;   // SE_PRIVILEGE_ENABLED
                        if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                    finally { CloseHandle(token); }
                }
                const uint EWX_POWEROFF = 0x00000008, EWX_FORCEIFHUNG = 0x00000010;
                const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000, SHTDN_REASON_FLAG_PLANNED = 0x80000000;
                bool ok = ExitWindowsEx(EWX_POWEROFF | EWX_FORCEIFHUNG, SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_FLAG_PLANNED);
                if (!ok) Log.Write("ExitWindowsEx non riuscito, errore " + Marshal.GetLastWin32Error());
                return ok;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                return false;
            }
        }
    }
}
