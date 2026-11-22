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

        /// <summary>Jingle per tipo di notifica: 0 soglia, 1 notifica breve, 2 messaggio, 3 allarme, 4 tic, 5 annullato.</summary>
        private static readonly double[][] TuneFreqs =
        {
            new double[] { 523.25, 659.25, 783.99, 1046.50 },   // Do - Mi - Sol - Do
            new double[] { 880.00, 1318.51 },                   // La - Mi: "clic"
            new double[] { 659.25, 783.99, 659.25, 987.77 },    // Mi - Sol - Mi - Si
            new double[] { 987.77, 698.46, 987.77, 698.46 },    // Si - Fa ripetuti: allarme
            new double[] { 1567.98 },                           // tic del conto alla rovescia
            new double[] { 1046.50, 783.99, 523.25 },           // Do - Sol - Do discendente
        };

        private static readonly int[][] TuneMs =
        {
            new int[] { 90, 90, 90, 220 },
            new int[] { 50, 90 },
            new int[] { 80, 80, 80, 180 },
            new int[] { 120, 120, 120, 200 },
            new int[] { 35 },
            new int[] { 90, 90, 200 },
        };

        private static readonly SoundPlayer[] players = new SoundPlayer[TuneFreqs.Length];

        public static byte[] BuildWav()
        {
            return BuildWav(0);
        }

        public static byte[] BuildWav(int tune)
        {
            tune = Math.Max(0, Math.Min(TuneFreqs.Length - 1, tune));
            double[] freqs = TuneFreqs[tune];
            int[] ms = TuneMs[tune];

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
            Play(0);
        }

        public static void Play(int tune)
        {
            try
            {
                tune = Math.Max(0, Math.Min(TuneFreqs.Length - 1, tune));
                if (players[tune] == null) players[tune] = new SoundPlayer(new MemoryStream(BuildWav(tune)));
                players[tune].Play();   // asincrono: non blocca l'interfaccia
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        }
    }
}
