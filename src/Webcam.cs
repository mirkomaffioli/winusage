using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;

namespace WinUsage
{
    /// <summary>
    /// Foto dalla webcam per /foto, con Media Foundation (incluso in Windows 10 e 11, nessuna DLL esterna). Media Foundation
    /// vede tutte le fotocamere che vede l'app Fotocamera di Windows, anche quelle integrate MIPI dei portatili recenti che
    /// le vecchie API Video for Windows (avicap32) non vedono. Le fotocamere a infrarossi di Windows Hello non sono nella
    /// categoria "acquisizione video" e quindi non compaiono.
    ///
    /// Passi: elenco delle fotocamere (MFEnumDeviceSources), source reader con l'elaborazione video attiva così che
    /// qualsiasi formato della fotocamera (NV12, YUY2, MJPG...) arrivi come RGB32, risoluzione nativa più grande entro
    /// 1920x1080, un secondo e mezzo di fotogrammi scartati perché esposizione e bilanciamento del bianco automatici si
    /// assestino (i primi fotogrammi sono scuri o verdi), poi l'ultimo fotogramma in JPEG.
    ///
    /// Come in ColorText, le interfacce COM si chiamano per indice nella vtable. Indici verificati su mfobjects.idl,
    /// mfidl.idl e mfreadwrite.idl: IUnknown QueryInterface 0, Release 2; IMFAttributes GetUINT32 7, GetUINT64 8,
    /// GetAllocatedString 13, SetUINT32 21, SetGUID 24 (33 metodi in tutto, ereditati da IMFActivate, IMFMediaType e
    /// IMFSample); IMFActivate ActivateObject 33, ShutdownObject 34; IMFSourceReader SetStreamSelection 4,
    /// GetNativeMediaType 5, GetCurrentMediaType 6, SetCurrentMediaType 7, ReadSample 9; IMFSample
    /// ConvertToContiguousBuffer 41; IMFMediaBuffer Lock 3, Unlock 4; IMF2DBuffer Lock2D 3, Unlock2D 4.
    ///
    /// Bloccante (da uno a qualche secondo): va chiamato da un thread di lavoro, mai da quello dell'interfaccia.
    /// </summary>
    internal static class Webcam
    {
        public const int MaxWidth = 1920;
        public const int MaxHeight = 1080;
        private const int WarmupMs = 1500;          // dal primo fotogramma
        private const int MinFrames = 5;
        private const int ReadLimitMs = 12000;      // fotocamera che non dà immagini

        // ---- costanti di Media Foundation ------------------------------------------------------------------------------

        private const int MF_VERSION = 0x00020070;
        private const int MFSTARTUP_FULL = 0;
        private const uint FIRST_VIDEO_STREAM = 0xFFFFFFFC;   // MF_SOURCE_READER_FIRST_VIDEO_STREAM
        private const uint ALL_STREAMS = 0xFFFFFFFE;          // MF_SOURCE_READER_ALL_STREAMS
        private const uint READERF_ERROR = 0x1;
        private const uint READERF_ENDOFSTREAM = 0x2;
        private const uint READERF_CURRENTMEDIATYPECHANGED = 0x20;
        public const int E_ACCESSDENIED = unchecked((int)0x80070005);
        private const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        private const int E_FAIL = unchecked((int)0x80004005);

        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        private static Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        private static Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static Guid IID_IMFMediaSource = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");
        private static Guid IID_IMF2DBuffer = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

        // ---- funzioni ----------------------------------------------------------------------------------------------------

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, int coinit);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
        [DllImport("mfplat.dll")]
        private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")]
        private static extern int MFShutdown();
        [DllImport("mfplat.dll")]
        private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
        [DllImport("mfplat.dll")]
        private static extern int MFCreateMediaType(out IntPtr mediaType);
        [DllImport("mf.dll")]
        private static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr activates, out uint count);
        [DllImport("mfreadwrite.dll")]
        private static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, out IntPtr reader);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUINT32Fn(IntPtr self, ref Guid key, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUINT64Fn(IntPtr self, ref Guid key, out ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAllocatedStringFn(IntPtr self, ref Guid key, out IntPtr value, out uint length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUINT32Fn(IntPtr self, ref Guid key, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetGUIDFn(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateObjectFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ShutdownObjectFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetStreamSelectionFn(IntPtr self, uint stream, int selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNativeMediaTypeFn(IntPtr self, uint stream, uint index, out IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCurrentMediaTypeFn(IntPtr self, uint stream, out IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetCurrentMediaTypeFn(IntPtr self, uint stream, IntPtr reserved, IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadSampleFn(IntPtr self, uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConvertToContiguousBufferFn(IntPtr self, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LockFn(IntPtr self, out IntPtr data, out uint maxLength, out uint currentLength);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnlockFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Lock2DFn(IntPtr self, out IntPtr scanline0, out int pitch);

        private static T Fn<T>(IntPtr self, int index) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(self);
            IntPtr fn = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        private static void Release(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            Fn<ReleaseFn>(p, 2)(p);
            p = IntPtr.Zero;
        }

        /// <summary>Errore di Media Foundation con il suo HRESULT, per scegliere il messaggio da mandare al telefono.</summary>
        internal sealed class MfException : Exception
        {
            public readonly int HResultCode;
            public MfException(int hr, string where) : base(where + " 0x" + hr.ToString("X8", CultureInfo.InvariantCulture)) { HResultCode = hr; }
        }

        private static void Check(int hr, string where)
        {
            if (hr < 0) throw new MfException(hr, where);
        }

        // ---- attributi ---------------------------------------------------------------------------------------------------

        private static void SetGuid(IntPtr attrs, Guid key, Guid value)
        {
            Check(Fn<SetGUIDFn>(attrs, 24)(attrs, ref key, ref value), "SetGUID");
        }

        private static void SetUInt32(IntPtr attrs, Guid key, uint value)
        {
            Check(Fn<SetUINT32Fn>(attrs, 21)(attrs, ref key, value), "SetUINT32");
        }

        private static bool TryGetUInt32(IntPtr attrs, Guid key, out uint value)
        {
            return Fn<GetUINT32Fn>(attrs, 7)(attrs, ref key, out value) >= 0;
        }

        private static bool TryGetUInt64(IntPtr attrs, Guid key, out ulong value)
        {
            return Fn<GetUINT64Fn>(attrs, 8)(attrs, ref key, out value) >= 0;
        }

        private static string GetString(IntPtr attrs, Guid key)
        {
            IntPtr s;
            uint len;
            if (Fn<GetAllocatedStringFn>(attrs, 13)(attrs, ref key, out s, out len) < 0 || s == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(s, (int)len); }
            finally { Marshal.FreeCoTaskMem(s); }
        }

        // ---- scelta della risoluzione (pura, provata nei test) ----------------------------------------------------------

        /// <summary>
        /// Indice del formato da usare: il più grande che sta in <see cref="MaxWidth"/> x <see cref="MaxHeight"/> (a parità,
        /// quello con più fotogrammi al secondo); se nessuno ci sta, il più piccolo. -1 se l'elenco è vuoto.
        /// </summary>
        public static int ChooseFormat(IList<int[]> formats)   // ogni elemento: { larghezza, altezza, fps * 100 }
        {
            int best = -1;
            for (int i = 0; i < formats.Count; i++)
            {
                int[] f = formats[i];
                if (f[0] <= 0 || f[1] <= 0) continue;
                if (best < 0) { best = i; continue; }
                int[] b = formats[best];
                bool fits = f[0] <= MaxWidth && f[1] <= MaxHeight, bestFits = b[0] <= MaxWidth && b[1] <= MaxHeight;
                long area = (long)f[0] * f[1], bestArea = (long)b[0] * b[1];
                if (fits != bestFits) { if (fits) best = i; continue; }
                if (area != bestArea) { if (fits ? area > bestArea : area < bestArea) best = i; continue; }
                if (f[2] > b[2]) best = i;
            }
            return best;
        }

        /// <summary>Luminosità media (0-255) di un'immagine RGB32, su una griglia di punti: quasi zero = obiettivo coperto.</summary>
        public static int MeanLuma(byte[] bgrx, int width, int height)
        {
            long sum = 0;
            int n = 0;
            int stepX = Math.Max(1, width / 32), stepY = Math.Max(1, height / 32);
            for (int y = 0; y < height; y += stepY)
                for (int x = 0; x < width; x += stepX)
                {
                    int i = (y * width + x) * 4;
                    if (i + 2 >= bgrx.Length) continue;
                    sum += (bgrx[i] * 29 + bgrx[i + 1] * 150 + bgrx[i + 2] * 77) >> 8;
                    n++;
                }
            return n == 0 ? 0 : (int)(sum / n);
        }

        // ---- cattura -----------------------------------------------------------------------------------------------------

        /// <param name="camera">0 = la prima fotocamera, n = l'n-esima (da 1).</param>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static ScreenshotResult Capture(int camera)
        {
            ScreenshotResult r = new ScreenshotResult();
            r.Name = "foto";
            try
            {
                CaptureCore(camera, r);
            }
            catch (MfException ex)
            {
                Log.Write("Webcam: " + ex.Message);
                r.Data = null;
                r.Error = ErrorText(ex.HResultCode);
            }
            catch (DllNotFoundException ex)
            {
                // Windows N/KN senza Media Feature Pack
                Log.Write("Webcam: " + ex.Message);
                r.Data = null;
                r.Error = L.T("📷 Su questo PC manca Media Foundation (Windows N senza il pacchetto Funzionalità multimediali): la foto non è possibile.",
                              "📷 Media Foundation is missing on this PC (Windows N without the Media Feature Pack): a photo is not possible.");
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                r.Data = null;
                r.Error = L.T("📷 Foto non riuscita.", "📷 Photo failed.");
            }
            return r;
        }

        /// <summary>Messaggio per il telefono da un HRESULT di Media Foundation.</summary>
        public static string ErrorText(int hr)
        {
            if (hr == E_ACCESSDENIED)
                return L.T("📷 Windows non permette a WinUsage di usare la fotocamera. Sul PC: Impostazioni > Privacy e sicurezza > Fotocamera > " +
                           "attiva «Accesso alla fotocamera» e «Consenti alle app desktop di accedere alla fotocamera».",
                           "📷 Windows does not let WinUsage use the camera. On the PC: Settings > Privacy & security > Camera > " +
                           "turn on \"Camera access\" and \"Let desktop apps access your camera\".");
            return L.T("📷 La fotocamera non ha dato un'immagine: forse la sta usando un'altra app, è spenta con il tasto della privacy " +
                       "o è disattivata. Errore 0x", "📷 The camera gave no image: another app may be using it, it may be off with the " +
                       "privacy switch, or disabled. Error 0x") + hr.ToString("X8", CultureInfo.InvariantCulture) + ".";
        }

        private static void CaptureCore(int camera, ScreenshotResult r)
        {
            int hrCo = CoInitializeEx(IntPtr.Zero, 0);   // COINIT_MULTITHREADED; S_FALSE se il thread era già MTA
            bool started = false;
            IntPtr attrs = IntPtr.Zero, array = IntPtr.Zero, source = IntPtr.Zero, readerAttrs = IntPtr.Zero, reader = IntPtr.Zero;
            IntPtr outType = IntPtr.Zero, sample = IntPtr.Zero;
            IntPtr[] devices = new IntPtr[0];
            int chosen = -1;
            try
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
                started = true;

                Check(MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
                SetGuid(attrs, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                uint count;
                Check(MFEnumDeviceSources(attrs, out array, out count), "MFEnumDeviceSources");
                devices = new IntPtr[count];
                List<string> names = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    devices[i] = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    string n = GetString(devices[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                    names.Add(string.IsNullOrEmpty(n) ? L.T("Fotocamera ", "Camera ") + (i + 1) : n);
                }

                if (count == 0)
                {
                    r.Error = L.T("📷 Nessuna fotocamera trovata su questo PC (o è disattivata in Gestione dispositivi).",
                                  "📷 No camera found on this PC (or it is disabled in Device Manager).");
                    return;
                }
                chosen = camera <= 0 ? 0 : camera - 1;
                if (chosen >= count)
                {
                    r.Error = CameraList(camera, names);
                    chosen = -1;
                    return;
                }
                r.Caption = names[chosen];

                Check(Fn<ActivateObjectFn>(devices[chosen], 33)(devices[chosen], ref IID_IMFMediaSource, out source), "ActivateObject");
                Check(MFCreateAttributes(out readerAttrs, 1), "MFCreateAttributes");
                SetUInt32(readerAttrs, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                Check(MFCreateSourceReaderFromMediaSource(source, readerAttrs, out reader), "MFCreateSourceReaderFromMediaSource");

                Fn<SetStreamSelectionFn>(reader, 4)(reader, ALL_STREAMS, 0);
                Check(Fn<SetStreamSelectionFn>(reader, 4)(reader, FIRST_VIDEO_STREAM, 1), "SetStreamSelection");
                SelectNativeFormat(reader);

                // uscita in RGB32: il source reader aggiunge decodificatore e conversione di colore che servono
                Check(MFCreateMediaType(out outType), "MFCreateMediaType");
                SetGuid(outType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(outType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                Check(Fn<SetCurrentMediaTypeFn>(reader, 7)(reader, FIRST_VIDEO_STREAM, IntPtr.Zero, outType), "SetCurrentMediaType RGB32");

                int width, height, stride;
                ReadFormat(reader, out width, out height, out stride);

                Stopwatch sw = Stopwatch.StartNew();
                long firstMs = -1;
                int frames = 0;
                ReadSampleFn read = Fn<ReadSampleFn>(reader, 9);
                while (sw.ElapsedMilliseconds < ReadLimitMs)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr s;
                    Check(read(reader, FIRST_VIDEO_STREAM, 0, out actual, out flags, out ts, out s), "ReadSample");
                    if ((flags & READERF_ERROR) != 0) { Release(ref s); throw new MfException(E_FAIL, "ReadSample flag errore"); }
                    if ((flags & READERF_CURRENTMEDIATYPECHANGED) != 0) ReadFormat(reader, out width, out height, out stride);
                    if (s == IntPtr.Zero)
                    {
                        if ((flags & READERF_ENDOFSTREAM) != 0) break;
                        continue;   // tick del flusso senza immagine
                    }
                    Release(ref sample);
                    sample = s;
                    frames++;
                    if (firstMs < 0) firstMs = sw.ElapsedMilliseconds;
                    if (frames >= MinFrames && sw.ElapsedMilliseconds - firstMs >= WarmupMs) break;
                    if ((flags & READERF_ENDOFSTREAM) != 0) break;
                }
                if (sample == IntPtr.Zero)
                {
                    r.Error = L.T("📷 La fotocamera non ha dato un'immagine in tempo: forse la sta usando un'altra app.",
                                  "📷 The camera gave no image in time: another app may be using it.");
                    return;
                }

                byte[] pixels = CopyPixels(sample, width, height, stride);
                r.Dark = MeanLuma(pixels, width, height) < 10;
                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                {
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(pixels, y * width * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), width * 4);
                    }
                    finally { bmp.UnlockBits(bd); }
                    r.Data = RemoteActions.Encode(bmp, ImageFormat.Jpeg, 88);
                }
                Log.Write("Webcam: foto " + width + "x" + height + " dopo " + frames + " fotogrammi, " + sw.ElapsedMilliseconds + " ms");
            }
            finally
            {
                Release(ref sample);
                Release(ref outType);
                Release(ref reader);            // chiude anche la sorgente (comportamento predefinito del source reader)
                Release(ref readerAttrs);
                Release(ref source);
                for (int i = 0; i < devices.Length; i++)
                {
                    IntPtr d = devices[i];
                    if (d == IntPtr.Zero) continue;
                    if (i == chosen) { try { Fn<ShutdownObjectFn>(d, 34)(d); } catch (Exception) { } }   // spegne la fotocamera
                    Release(ref d);
                }
                if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array);
                Release(ref attrs);
                if (started) MFShutdown();
                if (hrCo >= 0) CoUninitialize();
            }
        }

        /// <summary>"Fotocamera 3 inesistente: ce ne sono 2 (1 Integrated Camera, 2 USB Camera)."</summary>
        public static string CameraList(int camera, IList<string> names)
        {
            List<string> items = new List<string>();
            for (int i = 0; i < names.Count; i++) items.Add((i + 1).ToString(CultureInfo.InvariantCulture) + " " + names[i]);
            return L.T("📷 Fotocamera ", "📷 Camera ") + camera + L.T(" inesistente: ", " does not exist: ") +
                   (names.Count == 1 ? L.T("ce n'è una", "there is one") : L.T("ce ne sono ", "there are ") + names.Count) +
                   " (" + string.Join(", ", items.ToArray()) + ").";
        }

        /// <summary>Imposta in ingresso il formato nativo scelto con <see cref="ChooseFormat"/>. Se non riesce resta quello della fotocamera.</summary>
        private static void SelectNativeFormat(IntPtr reader)
        {
            GetNativeMediaTypeFn get = Fn<GetNativeMediaTypeFn>(reader, 5);
            List<int[]> formats = new List<int[]>();
            for (uint i = 0; i < 300; i++)
            {
                IntPtr t;
                int hr = get(reader, FIRST_VIDEO_STREAM, i, out t);
                if (hr == MF_E_NO_MORE_TYPES || hr < 0 || t == IntPtr.Zero) break;
                ulong size, rate;
                int w = 0, h = 0, fps = 0;
                if (TryGetUInt64(t, MF_MT_FRAME_SIZE, out size)) { w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF); }
                if (TryGetUInt64(t, MF_MT_FRAME_RATE, out rate))
                {
                    uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
                    if (den > 0) fps = (int)Math.Min(int.MaxValue, (long)num * 100 / den);
                }
                formats.Add(new int[] { w, h, fps });
                Release(ref t);
            }
            int best = ChooseFormat(formats);
            if (best < 0) return;
            IntPtr chosen;
            if (get(reader, FIRST_VIDEO_STREAM, (uint)best, out chosen) < 0 || chosen == IntPtr.Zero) return;
            try
            {
                int hr = Fn<SetCurrentMediaTypeFn>(reader, 7)(reader, FIRST_VIDEO_STREAM, IntPtr.Zero, chosen);
                if (hr < 0) Log.Write("Webcam: formato " + formats[best][0] + "x" + formats[best][1] + " rifiutato, 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
            }
            finally { Release(ref chosen); }
        }

        /// <summary>Dimensioni e passo (byte per riga, negativo = immagine dal basso) del formato RGB32 in uscita.</summary>
        private static void ReadFormat(IntPtr reader, out int width, out int height, out int stride)
        {
            IntPtr t;
            Check(Fn<GetCurrentMediaTypeFn>(reader, 6)(reader, FIRST_VIDEO_STREAM, out t), "GetCurrentMediaType");
            try
            {
                ulong size;
                if (!TryGetUInt64(t, MF_MT_FRAME_SIZE, out size)) throw new MfException(E_FAIL, "MF_MT_FRAME_SIZE");
                width = (int)(size >> 32);
                height = (int)(size & 0xFFFFFFFF);
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new MfException(E_FAIL, "dimensioni " + width + "x" + height);
                uint s;
                stride = TryGetUInt32(t, MF_MT_DEFAULT_STRIDE, out s) ? unchecked((int)s) : width * 4;
                if (Math.Abs(stride) < width * 4) stride = width * 4;
            }
            finally { Release(ref t); }
        }

        /// <summary>Pixel BGRX dall'alto in basso, righe di width * 4 byte.</summary>
        private static byte[] CopyPixels(IntPtr sample, int width, int height, int stride)
        {
            IntPtr buffer = IntPtr.Zero, buffer2d = IntPtr.Zero;
            byte[] pixels = new byte[width * height * 4];
            try
            {
                Check(Fn<ConvertToContiguousBufferFn>(sample, 41)(sample, out buffer), "ConvertToContiguousBuffer");

                // IMF2DBuffer dà la prima riga e il passo esatti (anche con righe allungate o immagine dal basso)
                if (Fn<QueryInterfaceFn>(buffer, 0)(buffer, ref IID_IMF2DBuffer, out buffer2d) >= 0 && buffer2d != IntPtr.Zero)
                {
                    IntPtr scan0;
                    int pitch;
                    if (Fn<Lock2DFn>(buffer2d, 3)(buffer2d, out scan0, out pitch) >= 0)
                    {
                        try
                        {
                            if (Math.Abs(pitch) < width * 4) throw new MfException(E_FAIL, "passo " + pitch);
                            for (int y = 0; y < height; y++)
                                Marshal.Copy(new IntPtr(scan0.ToInt64() + (long)y * pitch), pixels, y * width * 4, width * 4);
                        }
                        finally { Fn<UnlockFn>(buffer2d, 4)(buffer2d); }
                        return pixels;
                    }
                }

                IntPtr data;
                uint max, cur;
                Check(Fn<LockFn>(buffer, 3)(buffer, out data, out max, out cur), "Lock");
                try
                {
                    int abs = Math.Abs(stride);
                    if ((long)cur < (long)abs * (height - 1) + width * 4) throw new MfException(E_FAIL, "buffer corto " + cur);
                    long first = stride >= 0 ? data.ToInt64() : data.ToInt64() + (long)abs * (height - 1);
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(new IntPtr(first + (long)y * stride), pixels, y * width * 4, width * 4);
                }
                finally { Fn<UnlockFn>(buffer, 4)(buffer); }
                return pixels;
            }
            finally
            {
                Release(ref buffer2d);
                Release(ref buffer);
            }
        }
    }
}
