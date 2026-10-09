using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ErrorChecker.Core
{
    // Codec sans perte pour les zones « interface » (peu de couleurs : texte, grilles, boîtes de dialogue) :
    // palette + un octet par pixel, compressé. Plus net que le JPEG, et plus petit sur ce type de contenu.
    // Format : [Magic][nb couleurs - 1][palette B,G,R...][indices filtrés, compressés]
    public static class PaletteCodec
    {
        public const byte Magic = 0x01;  // un JPEG commence par 0xFF, un PNG par 0x89
        public const int MaxColors = 256;

        // null si la zone a trop de couleurs (photo, texte ClearType riche...) : il faut un autre codec.
        public static byte[]? Encode(byte[] pixels, int stride, Area a)
        {
            var palette = new Dictionary<int, byte>();
            var colors = new List<int>();
            var indices = new byte[a.W * a.H];
            int last = -1;
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
                    indices[y * a.W + x] = lastIndex;
                }
            }
            // Filtre « ligne du dessus » : ce qui se répète verticalement devient des zéros.
            for (int i = indices.Length - 1; i >= a.W; i--) indices[i] ^= indices[i - a.W];

            using var ms = new MemoryStream();
            ms.WriteByte(Magic);
            ms.WriteByte((byte)(colors.Count - 1));
            foreach (int color in colors)
            {
                ms.WriteByte((byte)color);
                ms.WriteByte((byte)(color >> 8));
                ms.WriteByte((byte)(color >> 16));
            }
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(indices);
            return ms.ToArray();
        }

        // Écrit la zone décodée (BGRA) dans target.
        public static void Decode(byte[] data, byte[] target, int stride, Area a)
        {
            if (data[0] != Magic) throw new InvalidDataException("Zone d'image illisible.");
            int count = data[1] + 1, start = 2 + count * 3;
            var colors = new int[count];
            for (int i = 0; i < count; i++)
                colors[i] = data[2 + i * 3] | data[3 + i * 3] << 8 | data[4 + i * 3] << 16 | unchecked((int)0xFF000000);

            var indices = new byte[a.W * a.H];
            using (var z = new DeflateStream(new MemoryStream(data, start, data.Length - start), CompressionMode.Decompress))
            {
                int read = 0, n;
                while (read < indices.Length && (n = z.Read(indices, read, indices.Length - read)) > 0) read += n;
                if (read != indices.Length) throw new InvalidDataException("Zone d'image tronquée.");
            }
            for (int i = a.W; i < indices.Length; i++) indices[i] ^= indices[i - a.W];

            for (int y = 0; y < a.H; y++)
            {
                var row = MemoryMarshal.Cast<byte, int>(target.AsSpan((a.Y + y) * stride + a.X * 4, a.W * 4));
                for (int x = 0; x < a.W; x++) row[x] = colors[indices[y * a.W + x]];
            }
        }
    }
}
