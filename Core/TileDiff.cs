using System.Runtime.InteropServices;

namespace ErrorChecker.Core
{
    public readonly record struct Area(int X, int Y, int W, int H);

    // Repère les zones modifiées entre deux captures (pixels 32 bits) : seules celles-ci sont envoyées.
    // Une saisie dans une cellule Excel = une ou deux tuiles de 64x64 au lieu de l'écran entier.
    public static class TileDiff
    {
        public const int TileSize = 64;

        public static List<Area> DirtyAreas(byte[]? previous, byte[] current, int width, int height, int stride, int tile = TileSize)
        {
            var dirty = DirtyTiles(previous, current, width, height, stride, tile);
            return dirty == null ? new List<Area> { new(0, 0, width, height) } : Merge(dirty, width, height, tile);
        }

        // null = pas de référence comparable : tout l'écran est à envoyer.
        public static bool[,]? DirtyTiles(byte[]? previous, byte[] current, int width, int height, int stride, int tile = TileSize)
        {
            if (previous == null || previous.Length != current.Length) return null;
            int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;
            var dirty = new bool[rows, cols];
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++)
                {
                    int x = tx * tile, y = ty * tile;
                    dirty[ty, tx] = TileChanged(previous, current, x, y, Math.Min(tile, width - x), Math.Min(tile, height - y), stride);
                }
            return dirty;
        }

        // Rectangle englobant les tuiles modifiées, ou null si rien n'a changé.
        public static Area? Bounds(bool[,] dirty, int width, int height, int tile = TileSize)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int ty = 0; ty < dirty.GetLength(0); ty++)
                for (int tx = 0; tx < dirty.GetLength(1); tx++)
                    if (dirty[ty, tx])
                    {
                        x0 = Math.Min(x0, tx); y0 = Math.Min(y0, ty);
                        x1 = Math.Max(x1, tx); y1 = Math.Max(y1, ty);
                    }
            if (x1 < 0) return null;
            int x = x0 * tile, y = y0 * tile;
            return new Area(x, y, Math.Min((x1 + 1) * tile, width) - x, Math.Min((y1 + 1) * tile, height) - y);
        }

        // Fusion des tuiles contiguës d'une ligne, puis des bandes identiques de lignes successives.
        // Les zones obtenues ne couvrent que des tuiles modifiées et ne se chevauchent jamais.
        public static List<Area> Merge(bool[,] dirty, int width, int height, int tile = TileSize)
        {
            int rows = dirty.GetLength(0), cols = dirty.GetLength(1);
            var result = new List<Area>();
            var open = new Dictionary<(int X, int W), int>();  // bande -> index dans result, sur la ligne précédente
            for (int ty = 0; ty < rows; ty++)
            {
                var next = new Dictionary<(int X, int W), int>();
                for (int tx = 0; tx < cols; tx++)
                {
                    if (!dirty[ty, tx]) continue;
                    int start = tx;
                    while (tx + 1 < cols && dirty[ty, tx + 1]) tx++;
                    int x = start * tile, w = Math.Min((tx + 1) * tile, width) - x;
                    int y = ty * tile, h = Math.Min(tile, height - y);
                    if (open.TryGetValue((x, w), out int i))
                        result[i] = result[i] with { H = result[i].H + h };
                    else
                    {
                        i = result.Count;
                        result.Add(new Area(x, y, w, h));
                    }
                    next[(x, w)] = i;
                }
                open = next;
            }
            return result;
        }

        // Rectangle serré autour des pixels qui diffèrent dans r (null si aucun) : une saisie dans une cellule
        // ne touche que quelques dizaines de pixels d'une tuile de 64x64.
        public static Area? Shrink(byte[] previous, byte[] current, int stride, Area r)
        {
            int left = int.MaxValue, right = -1, top = -1, bottom = -1;
            for (int y = r.Y; y < r.Y + r.H; y++)
            {
                var a = MemoryMarshal.Cast<byte, int>(previous.AsSpan(y * stride + r.X * 4, r.W * 4));
                var b = MemoryMarshal.Cast<byte, int>(current.AsSpan(y * stride + r.X * 4, r.W * 4));
                if (a.SequenceEqual(b)) continue;
                int l = 0, rr = a.Length - 1;
                while (a[l] == b[l]) l++;
                while (a[rr] == b[rr]) rr--;
                left = Math.Min(left, l);
                right = Math.Max(right, rr);
                if (top < 0) top = y;
                bottom = y;
            }
            return right < 0 ? null : new Area(r.X + left, top, right - left + 1, bottom - top + 1);
        }

        private static bool TileChanged(byte[] a, byte[] b, int x, int y, int w, int h, int stride)
        {
            for (int row = y; row < y + h; row++)
            {
                int offset = row * stride + x * 4;
                if (!a.AsSpan(offset, w * 4).SequenceEqual(b.AsSpan(offset, w * 4))) return true;
            }
            return false;
        }
    }
}
