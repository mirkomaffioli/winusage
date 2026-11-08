using System;
using System.IO;
using System.Media;

namespace WinUsage
{
    /// <summary>
    /// Piccolo jingle "8 bit" (onda quadra, 4 note) generato in memoria: nessun file audio da distribuire.
    /// Si usa quando il messaggio compare sopra un gioco a schermo intero, dove potrebbe non essere visibile.
    /// </summary>
    internal static class ChipTune
    {
        private const int SampleRate = 22050;
        private static SoundPlayer player;

        public static byte[] BuildWav()
        {
            double[] freqs = { 523.25, 659.25, 783.99, 1046.50 };   // Do - Mi - Sol - Do
            int[] ms = { 90, 90, 90, 220 };

            MemoryStream pcm = new MemoryStream();
            for (int n = 0; n < freqs.Length; n++)
            {
                int samples = SampleRate * ms[n] / 1000;
                for (int i = 0; i < samples; i++)
                {
                    double t = (double)i / SampleRate;
                    double square = Math.Sin(2 * Math.PI * freqs[n] * t) >= 0 ? 1.0 : -1.0;
                    double envelope = 1.0 - 0.6 * i / samples;          // decadimento dolce
                    short v = (short)(square * envelope * 0.22 * short.MaxValue);
                    pcm.WriteByte((byte)(v & 0xFF));
                    pcm.WriteByte((byte)((v >> 8) & 0xFF));
                }
            }

            byte[] data = pcm.ToArray();
            MemoryStream wav = new MemoryStream();
            BinaryWriter w = new BinaryWriter(wav);
            w.Write(new char[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + data.Length);
            w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);        // byte al secondo
            w.Write((short)2);              // allineamento
            w.Write((short)16);             // bit per campione
            w.Write(new char[] { 'd', 'a', 't', 'a' });
            w.Write(data.Length);
            w.Write(data);
            w.Flush();
            return wav.ToArray();
        }

        public static void Play()
        {
            try
            {
                if (player == null) player = new SoundPlayer(new MemoryStream(BuildWav()));
                player.Play();   // asincrono: non blocca l'interfaccia
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
    }
}
