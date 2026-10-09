using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ErrorChecker.Core
{
    // Codec sans perte pour les zones « interface » (peu de couleurs : texte, grilles, boîtes de dialogue) :
    // palette + un octet par pixel, compressés ensemble en Deflate (intégré à .NET Framework).
    // Mesuré sur 16 captures réelles : Brotli (absent de .NET Framework) ferait 11 % de moins sur une image
    // complète, mais pas mieux sur les petites zones (saisie), pour un encodage deux fois plus lent.
    // Format : [Magic][nb couleurs - 1] puis Deflate([palette B,G,R...][index par pixel])
    public static class PaletteCodec
    {
        public const byte Magic = 0x01;  // un JPEG commence par 0xFF, un PNG par 0x89
        public const int MaxColors = 256;

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
            using var ms = new MemoryStream();
            ms.WriteByte(Magic);
            ms.WriteByte((byte)(colors.Count - 1));
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, start, raw.Length - start);
            return ms.ToArray();
        }

        // Écrit la zone décodée (BGRA) dans target.
        public static void Decode(byte[] data, byte[] target, int stride, Area a)
        {
            if (data[0] != Magic) throw new InvalidDataException("Zone d'image illisible.");
            int count = data[1] + 1;
            var raw = new byte[3 * count + a.W * a.H];
            using (var z = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress))
            {
                int read = 0, n;
                while (read < raw.Length && (n = z.Read(raw, read, raw.Length - read)) > 0) read += n;
                if (read != raw.Length) throw new InvalidDataException("Zone d'image tronquée.");
            }
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
