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
            var full = new List<Area> { new(0, 0, width, height) };
            if (previous == null || previous.Length != current.Length) return full;

            int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;
            var dirty = new bool[rows, cols];
            int count = 0;
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++)
                {
                    int x = tx * tile, y = ty * tile;
                    if (TileChanged(previous, current, x, y, Math.Min(tile, width - x), Math.Min(tile, height - y), stride))
                    {
                        dirty[ty, tx] = true;
                        count++;
                    }
                }

            if (count == 0) return new List<Area>();
            if (count * 2 > rows * cols) return full;  // plus de la moitié a changé : une seule image compresse mieux

            // Fusion des tuiles contiguës d'une ligne, puis des bandes identiques de lignes successives.
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
