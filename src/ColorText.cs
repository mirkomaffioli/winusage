using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;

namespace WinUsage
{
    /// <summary>
    /// Testo con le emoji a colori. GDI (TextRenderer) disegna le emoji in bianco e nero, quindi il testo della chat e delle
    /// notifiche passa da DirectWrite + Direct2D (render target DC con D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT), che usa i
    /// font a colori di Windows (Segoe UI Emoji). Se DirectWrite non c'è o qualcosa va storto si torna a TextRenderer, che
    /// le mostra comunque, in bianco e nero.
    ///
    /// Le interfacce COM si chiamano per indice nella vtable, senza dichiarazioni [ComImport] (che dovrebbero ripetere tutti
    /// i metodi ereditati). Indici, firme e GUID verificati sugli IDL d2d1.idl e dwrite.idl:
    /// ID2D1Factory.CreateDCRenderTarget 16; ID2D1DCRenderTarget: CreateSolidColorBrush 8, DrawTextLayout 28,
    /// Clear 47, BeginDraw 48, EndDraw 49, BindDC 57; IDWriteFactory: CreateTextFormat 15,
    /// CreateTextLayout 18; IDWriteTextLayout.GetMetrics 60; IUnknown.Release 2.
    /// Solo dal thread dell'interfaccia (fabbrica Direct2D single-threaded).
    /// </summary>
    internal static class ColorText
    {
        /// <summary>Messo a false (ad esempio dai test) per usare sempre GDI.</summary>
        public static bool Enabled = true;

        private const TextFormatFlags GdiFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);
        private const string Ellipsis = "…";

        // ---- strutture ------------------------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D2D1_RENDER_TARGET_PROPERTIES
        {
            public int type;          // D2D1_RENDER_TARGET_TYPE_DEFAULT = 0
            public int format;        // DXGI_FORMAT_B8G8R8A8_UNORM = 87
            public int alphaMode;     // D2D1_ALPHA_MODE_IGNORE = 3
            public float dpiX;
            public float dpiY;
            public int usage;         // D2D1_RENDER_TARGET_USAGE_NONE = 0
            public int minLevel;      // D2D1_FEATURE_LEVEL_DEFAULT = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D2D1_COLOR_F { public float r, g, b, a; }

        [StructLayout(LayoutKind.Sequential)]
        private struct D2D1_POINT_2F { public float x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWRITE_TEXT_METRICS
        {
            public float left, top, width, widthIncludingTrailingWhitespace, height, layoutWidth, layoutHeight;
            public uint maxBidiReorderingDepth, lineCount;
        }

        // ---- funzioni e metodi --------------------------------------------------------------------------------------

        [DllImport("d2d1.dll")]
        private static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr options, out IntPtr factory);

        [DllImport("dwrite.dll")]
        private static extern int DWriteCreateFactory(int type, ref Guid iid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDCRenderTargetFn(IntPtr self, ref D2D1_RENDER_TARGET_PROPERTIES props, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSolidColorBrushFn(IntPtr self, ref D2D1_COLOR_F color, IntPtr props, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawTextLayoutFn(IntPtr self, D2D1_POINT_2F origin, IntPtr layout, IntPtr brush, int options);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClearFn(IntPtr self, ref D2D1_COLOR_F color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void BeginDrawFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EndDrawFn(IntPtr self, IntPtr tag1, IntPtr tag2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BindDCFn(IntPtr self, IntPtr hdc, ref RECT rect);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int CreateTextFormatFn(IntPtr self, string family, IntPtr collection, int weight, int style, int stretch,
                                                float size, string locale, out IntPtr format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int CreateTextLayoutFn(IntPtr self, string text, int length, IntPtr format, float maxWidth, float maxHeight,
                                                out IntPtr layout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMetricsFn(IntPtr self, out DWRITE_TEXT_METRICS metrics);

        private static readonly Dictionary<IntPtr, Delegate> delegates = new Dictionary<IntPtr, Delegate>();

        private static T Method<T>(IntPtr obj, int index) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            Delegate d;
            if (!delegates.TryGetValue(fn, out d))
            {
                d = Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
                delegates[fn] = d;
            }
            return d as T;
        }

        private static void Release(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { Method<ReleaseFn>(p, 2)(p); } catch (Exception) { }
            p = IntPtr.Zero;
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what, hr);
        }

        // ---- stato --------------------------------------------------------------------------------------------------

        private static bool tried, ok;
        private static IntPtr d2d, dwrite, target;
        private static readonly Dictionary<string, IntPtr> formats = new Dictionary<string, IntPtr>();
        private static float screenDpi;

        /// <summary>DirectWrite pronto (false su Windows vecchi, sotto Mono o dopo un errore).</summary>
        public static bool Available
        {
            // Anche un errore grave nelle chiamate native (accesso alla memoria) viene preso: si torna a GDI invece di chiudere l'app.
            [HandleProcessCorruptedStateExceptions, SecurityCritical]
            get
            {
                if (!Enabled) return false;
                if (tried) return ok;
                tried = true;
                try
                {
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version < new Version(6, 3))
                        return ok = false;   // i font a colori in Direct2D arrivano con Windows 8.1
                    Guid iidD2D = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
                    Check(D2D1CreateFactory(0, ref iidD2D, IntPtr.Zero, out d2d), "D2D1CreateFactory");
                    Guid iidDw = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
                    Check(DWriteCreateFactory(0, ref iidDw, out dwrite), "DWriteCreateFactory");
                    ok = true;
                }
                catch (Exception ex)
                {
                    Log.Write("Emoji a colori non disponibili (si usa GDI): " + ex.Message);
                    Release(ref dwrite);
                    Release(ref d2d);
                    ok = false;
                }
                return ok;
            }
        }

        /// <summary>Dopo un errore DirectWrite non si riprova: fino al riavvio si usa GDI.</summary>
        private static void Fail(Exception ex)
        {
            Log.Write("Emoji a colori disattivate per un errore (si usa GDI): " + ex.Message);
            ok = false;
            Release(ref target);
        }

        private static IntPtr Target()
        {
            if (target != IntPtr.Zero) return target;
            D2D1_RENDER_TARGET_PROPERTIES p = new D2D1_RENDER_TARGET_PROPERTIES();
            p.type = 0;
            p.format = 87;        // DXGI_FORMAT_B8G8R8A8_UNORM
            p.alphaMode = 3;      // D2D1_ALPHA_MODE_IGNORE: lo sfondo lo riempiamo noi con Clear
            p.dpiX = 96;          // 96 DPI: le unità di Direct2D coincidono con i pixel
            p.dpiY = 96;
            Check(Method<CreateDCRenderTargetFn>(d2d, 16)(d2d, ref p, out target), "CreateDCRenderTarget");
            return target;
        }

        private static float EmSize(Font font)
        {
            if (font.Unit == GraphicsUnit.Pixel) return font.Size;
            if (screenDpi <= 0)
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) screenDpi = g.DpiY;
            }
            return font.SizeInPoints * screenDpi / 72f;
        }

        private static IntPtr Format(Font font)
        {
            float size = EmSize(font);
            int weight = font.Bold ? 700 : 400;
            string key = font.FontFamily.Name + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + weight;
            IntPtr f;
            if (formats.TryGetValue(key, out f)) return f;
            Check(Method<CreateTextFormatFn>(dwrite, 15)(dwrite, font.FontFamily.Name, IntPtr.Zero, weight,
                                                         font.Italic ? 2 : 0, 5, size, Locale(), out f),
                  "CreateTextFormat");
            formats[key] = f;
            return f;
        }

        private static string Locale()
        {
            string name = CultureInfo.CurrentUICulture.Name;
            return string.IsNullOrEmpty(name) ? "it-IT" : name;
        }

        private static IntPtr Layout(string text, Font font, float maxWidth, float maxHeight)
        {
            IntPtr layout;
            Check(Method<CreateTextLayoutFn>(dwrite, 18)(dwrite, text, text.Length, Format(font), maxWidth, maxHeight, out layout),
                  "CreateTextLayout");
            return layout;
        }

        private static DWRITE_TEXT_METRICS Metrics(string text, Font font, float maxWidth)
        {
            IntPtr layout = Layout(text, font, maxWidth, 100000f);
            try
            {
                DWRITE_TEXT_METRICS m;
                Check(Method<GetMetricsFn>(layout, 60)(layout, out m), "GetMetrics");
                return m;
            }
            finally { Release(ref layout); }
        }

        // ---- API ----------------------------------------------------------------------------------------------------

        /// <summary>Dimensione del testo andando a capo entro <paramref name="maxWidth"/> pixel.</summary>
        // Anche un errore grave nelle chiamate native (accesso alla memoria) viene preso: si torna a GDI invece di chiudere l'app.
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static Size Measure(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) text = " ";
            if (Available)
            {
                try
                {
                    DWRITE_TEXT_METRICS m = Metrics(text, font, Math.Max(1, maxWidth));
                    return new Size((int)Math.Ceiling(m.widthIncludingTrailingWhitespace) + 1, (int)Math.Ceiling(m.height) + 1);
                }
                catch (Exception ex) { Fail(ex); }
            }
            return TextRenderer.MeasureText(text, font, new Size(maxWidth, 0), GdiFlags);
        }

        /// <summary>
        /// Scrive il testo nel rettangolo, andando a capo. <paramref name="back"/> è il colore (pieno) dello sfondo sotto il
        /// rettangolo: Direct2D lo ridipinge prima del testo. Con <paramref name="ellipsis"/> il testo che non ci sta finisce
        /// con "…" (senza spezzare un'emoji a metà).
        /// </summary>
        // Anche un errore grave nelle chiamate native (accesso alla memoria) viene preso: si torna a GDI invece di chiudere l'app.
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static void Draw(Graphics g, string text, Font font, Rectangle r, Color color, Color back, bool ellipsis)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 0 || r.Height <= 0) return;
            if (ellipsis) text = FitWithEllipsis(text, font, r.Size);
            if (Available)
            {
                try
                {
                    DrawD2D(g, text, font, r, color, back);
                    return;
                }
                catch (Exception ex) { Fail(ex); }
            }
            TextRenderer.DrawText(g, text, font, r, color, GdiFlags | (ellipsis ? TextFormatFlags.EndEllipsis : 0));
        }

        private static void DrawD2D(Graphics g, string text, Font font, Rectangle r, Color color, Color back)
        {
            // coordinate del DC: la Graphics può avere una traslazione (ad esempio nel doppio buffer)
            float dx = 0, dy = 0;
            using (System.Drawing.Drawing2D.Matrix m = g.Transform)
            {
                dx = m.OffsetX;
                dy = m.OffsetY;
            }
            RECT rc;
            rc.left = r.Left + (int)dx;
            rc.top = r.Top + (int)dy;
            rc.right = r.Right + (int)dx;
            rc.bottom = r.Bottom + (int)dy;

            IntPtr layout = Layout(text, font, r.Width, r.Height);
            IntPtr brush = IntPtr.Zero;
            IntPtr hdc = g.GetHdc();
            try
            {
                IntPtr t = Target();
                Check(Method<BindDCFn>(t, 57)(t, hdc, ref rc), "BindDC");
                Method<BeginDrawFn>(t, 48)(t);
                D2D1_COLOR_F bg = ToColorF(back);
                Method<ClearFn>(t, 47)(t, ref bg);
                D2D1_COLOR_F fg = ToColorF(color);
                int hr = Method<CreateSolidColorBrushFn>(t, 8)(t, ref fg, IntPtr.Zero, out brush);
                if (hr >= 0)
                {
                    D2D1_POINT_2F origin = new D2D1_POINT_2F();
                    Method<DrawTextLayoutFn>(t, 28)(t, origin, layout, brush, 2 | 4);   // CLIP | ENABLE_COLOR_FONT
                }
                int end = Method<EndDrawFn>(t, 49)(t, IntPtr.Zero, IntPtr.Zero);
                if (end == D2DERR_RECREATE_TARGET) Release(ref target);   // si ricrea al prossimo disegno
                else Check(end, "EndDraw");
                Check(hr, "CreateSolidColorBrush");
            }
            finally
            {
                g.ReleaseHdc(hdc);
                Release(ref brush);
                Release(ref layout);
            }
        }

        private static D2D1_COLOR_F ToColorF(Color c)
        {
            D2D1_COLOR_F f;
            f.r = c.R / 255f;
            f.g = c.G / 255f;
            f.b = c.B / 255f;
            f.a = 1f;
            return f;
        }

        /// <summary>Accorcia il testo (a confini di carattere visibile, emoji comprese) finché con "…" sta nell'area.</summary>
        public static string FitWithEllipsis(string text, Font font, Size area)
        {
            return FitWithEllipsis(text, delegate(string t) { return Fits(t, font, area); });
        }

        /// <summary>Come sopra, con la misura passata da fuori (nei test).</summary>
        internal static string FitWithEllipsis(string text, Func<string, bool> fits)
        {
            if (fits(text)) return text;
            int[] cuts = StringInfo.ParseCombiningCharacters(text);   // inizio di ogni carattere visibile
            int lo = 0, hi = cuts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (fits(text.Substring(0, cuts[mid]).TrimEnd() + Ellipsis)) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, cuts[lo]).TrimEnd() + Ellipsis;
        }

        private static bool Fits(string text, Font font, Size area)
        {
            return Measure(text, font, area.Width).Height <= area.Height + 1;
        }
    }
}
