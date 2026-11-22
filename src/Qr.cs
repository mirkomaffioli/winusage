using System;
using System.Collections.Generic;
using System.Text;

namespace WinUsage
{
    /// <summary>
    /// Generatore di QR code minimo (modalità byte, correzione M, versioni 1-6) per mostrare il segreto TOTP
    /// all'app Authenticator senza librerie esterne. Segue lo standard ISO/IEC 18004.
    /// </summary>
    internal static class QrCode
    {
        // per versione 1..6, correzione M: codeword di correzione per blocco, numero di blocchi, dati per blocco
        private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
        private static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4 };
        private static readonly int[] DataPerBlock = { 0, 16, 28, 44, 32, 43, 27 };
        private const int MaxVersion = 6;

        /// <summary>Byte massimi codificabili.</summary>
        public static int Capacity { get { return Blocks[MaxVersion] * DataPerBlock[MaxVersion] - 2; } }

        /// <summary>Matrice dei moduli (true = scuro), senza margine. Null se il testo è troppo lungo.</summary>
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = 0;
            for (int v = 1; v <= MaxVersion; v++)
            {
                if (data.Length + 2 <= Blocks[v] * DataPerBlock[v]) { version = v; break; }   // 4+8 bit di intestazione + terminatore
            }
            if (version == 0) return null;

            byte[] codewords = AddEcc(BuildData(data, version), version);
            int size = 17 + 4 * version;
            bool[,] modules = new bool[size, size];      // [riga, colonna]
            bool[,] function = new bool[size, size];
            DrawFunctionPatterns(modules, function, version);
            DrawCodewords(modules, function, codewords);

            int bestMask = 0;
            long bestPenalty = long.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, function, mask);
                DrawFormat(modules, function, mask);
                long p = Penalty(modules);
                if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
                ApplyMask(modules, function, mask);   // XOR: annulla
            }
            ApplyMask(modules, function, bestMask);
            DrawFormat(modules, function, bestMask);
            return modules;
        }

        private static byte[] BuildData(byte[] data, int version)
        {
            int capacityBits = Blocks[version] * DataPerBlock[version] * 8;
            List<bool> bits = new List<bool>();
            AppendBits(bits, 0x4, 4);               // modalità byte
            AppendBits(bits, data.Length, 8);       // lunghezza (8 bit fino alla versione 9)
            foreach (byte b in data) AppendBits(bits, b, 8);
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));   // terminatore
            while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);

            byte[] result = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
            return result;
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        private static byte[] AddEcc(byte[] data, int version)
        {
            int nBlocks = Blocks[version], dataLen = DataPerBlock[version], ecLen = EcPerBlock[version];
            byte[] divisor = RsDivisor(ecLen);
            byte[][] dataBlocks = new byte[nBlocks][];
            byte[][] ecBlocks = new byte[nBlocks][];
            for (int b = 0; b < nBlocks; b++)
            {
                dataBlocks[b] = new byte[dataLen];
                Array.Copy(data, b * dataLen, dataBlocks[b], 0, dataLen);
                ecBlocks[b] = RsRemainder(dataBlocks[b], divisor);
            }
            List<byte> result = new List<byte>();
            for (int i = 0; i < dataLen; i++)
                for (int b = 0; b < nBlocks; b++) result.Add(dataBlocks[b][i]);
            for (int i = 0; i < ecLen; i++)
                for (int b = 0; b < nBlocks; b++) result.Add(ecBlocks[b][i]);
            return result.ToArray();
        }

        private static byte[] RsDivisor(int degree)
        {
            byte[] result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)GfMul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMul(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            byte[] result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= (byte)GfMul(divisor[i], factor);
            }
            return result;
        }

        private static int GfMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        private static void Set(bool[,] m, bool[,] f, int row, int col, bool dark)
        {
            m[row, col] = dark;
            f[row, col] = true;
        }

        private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version)
        {
            int size = m.GetLength(0);
            for (int i = 0; i < size; i++)
            {
                Set(m, f, 6, i, i % 2 == 0);   // timing
                Set(m, f, i, 6, i % 2 == 0);
            }
            DrawFinder(m, f, 3, 3);
            DrawFinder(m, f, 3, size - 4);
            DrawFinder(m, f, size - 4, 3);
            if (version >= 2)
            {
                int pos = size - 7;   // versioni 2-6: un solo allineamento, in basso a destra
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                        Set(m, f, pos + dr, pos + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
            DrawFormat(m, f, 0);   // riserva le aree del formato (riscritte dopo la scelta della maschera)
        }

        private static void DrawFinder(bool[,] m, bool[,] f, int row, int col)
        {
            int size = m.GetLength(0);
            for (int dr = -4; dr <= 4; dr++)
            {
                for (int dc = -4; dc <= 4; dc++)
                {
                    int r = row + dr, c = col + dc;
                    if (r < 0 || r >= size || c < 0 || c >= size) continue;
                    int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    Set(m, f, r, c, dist != 2 && dist != 4);
                }
            }
        }

        private static void DrawFormat(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            int data = (0 << 3) | mask;   // correzione M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            // prima copia, attorno al riquadro in alto a sinistra (x = colonna, y = riga)
            for (int i = 0; i <= 5; i++) Set(m, f, i, 8, Bit(bits, i));
            Set(m, f, 7, 8, Bit(bits, 6));
            Set(m, f, 8, 8, Bit(bits, 7));
            Set(m, f, 8, 7, Bit(bits, 8));
            for (int i = 9; i < 15; i++) Set(m, f, 8, 14 - i, Bit(bits, i));
            // seconda copia, divisa tra gli altri due riquadri
            for (int i = 0; i < 8; i++) Set(m, f, 8, size - 1 - i, Bit(bits, i));
            for (int i = 8; i < 15; i++) Set(m, f, size - 15 + i, 8, Bit(bits, i));
            Set(m, f, size - 8, 8, true);   // modulo sempre scuro
        }

        private static bool Bit(int value, int i)
        {
            return ((value >> i) & 1) != 0;
        }

        private static void DrawCodewords(bool[,] m, bool[,] f, byte[] data)
        {
            int size = m.GetLength(0);
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int col = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int row = upward ? size - 1 - vert : vert;
                        if (!f[row, col] && i < data.Length * 8)
                        {
                            m[row, col] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (f[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) m[y, x] = !m[y, x];
                }
            }
        }

        /// <summary>Penalità semplificata (sequenze, blocchi 2x2, equilibrio): basta per scegliere una maschera leggibile.</summary>
        private static long Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            long p = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        bool cur = pass == 0 ? m[a, b] : m[b, a];
                        bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                        if (cur == prev) run++;
                        else { if (run >= 5) p += run - 2; run = 1; }
                    }
                    if (run >= 5) p += run - 2;
                }
            }
            int dark = 0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (m[y, x]) dark++;
                    if (y + 1 < size && x + 1 < size && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1])
                        p += 3;
                }
            }
            int total = size * size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            p += Math.Max(0, k) * 10;
            return p;
        }
    }
}
