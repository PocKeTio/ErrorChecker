using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ErrorChecker.Core
{
    // Codec sans perte pour les zones « interface » (peu de couleurs : texte, grilles, boîtes de dialogue) :
    // palette + un octet par pixel, compressés ensemble en Brotli (qualité 6).
    // Choix mesuré sur 16 captures réelles (Excel, éditeurs VBA/SQL, dialogues, Outlook, web) : ~20 % plus petit
    // que Deflate + filtre « ligne du dessus », décodage aussi rapide ; le filtre n'aide plus avec Brotli.
    // Format : [Magic][nb couleurs - 1] puis Brotli([palette B,G,R...][index par pixel])
    public static class PaletteCodec
    {
        public const byte Magic = 0x01;  // un JPEG commence par 0xFF, un PNG par 0x89
        public const int MaxColors = 256;
        private const int Quality = 6, Window = 22;

        // null si la zone a trop de couleurs (photo, texte ClearType riche...) : il faut un autre codec.
        public static byte[]? Encode(byte[] pixels, int stride, Area a)
        {
            var palette = new Dictionary<int, byte>();
            var colors = new List<int>();
            var raw = new byte[3 * MaxColors + a.W * a.H];   // palette réservée au maximum, recadrée ensuite
            int last = -1, pos = 3 * MaxColors;
            byte lastIndex = 0;
            for (int y = 0; y < a.H; y++)
            {
                var row = MemoryMarshal.Cast<byte, int>(pixels.AsSpan((a.Y + y) * stride + a.X * 4, a.W * 4));
                for (int x = 0; x < a.W; x++)
                {
                    int color = row[x] & 0xFFFFFF;
                    if (color != last)
                    {
                        if (!palette.TryGetValue(color, out lastIndex))
                        {
                            if (colors.Count == MaxColors) return null;
                            lastIndex = (byte)colors.Count;
                            palette.Add(color, lastIndex);
                            colors.Add(color);
                        }
                        last = color;
                    }
                    raw[pos++] = lastIndex;
                }
            }
            int start = 3 * (MaxColors - colors.Count);   // la palette se termine juste avant les index
            for (int i = 0; i < colors.Count; i++)
            {
                raw[start + 3 * i] = (byte)colors[i];
                raw[start + 3 * i + 1] = (byte)(colors[i] >> 8);
                raw[start + 3 * i + 2] = (byte)(colors[i] >> 16);
            }
            var source = raw.AsSpan(start);
            var output = new byte[2 + BrotliEncoder.GetMaxCompressedLength(source.Length)];
            output[0] = Magic;
            output[1] = (byte)(colors.Count - 1);
            if (!BrotliEncoder.TryCompress(source, output.AsSpan(2), out int written, Quality, Window))
                throw new InvalidOperationException("Compression Brotli impossible.");
            return output[..(2 + written)];
        }

        // Écrit la zone décodée (BGRA) dans target.
        public static void Decode(byte[] data, byte[] target, int stride, Area a)
        {
            if (data[0] != Magic) throw new InvalidDataException("Zone d'image illisible.");
            int count = data[1] + 1;
            var raw = new byte[3 * count + a.W * a.H];
            if (!BrotliDecoder.TryDecompress(data.AsSpan(2), raw, out int written) || written != raw.Length)
                throw new InvalidDataException("Zone d'image tronquée.");
            var colors = new int[count];
            for (int i = 0; i < count; i++)
                colors[i] = raw[3 * i] | raw[3 * i + 1] << 8 | raw[3 * i + 2] << 16 | unchecked((int)0xFF000000);

            int pos = 3 * count;
            for (int y = 0; y < a.H; y++)
            {
                var row = MemoryMarshal.Cast<byte, int>(target.AsSpan((a.Y + y) * stride + a.X * 4, a.W * 4));
                for (int x = 0; x < a.W; x++) row[x] = colors[raw[pos++]];
            }
        }
    }
}
