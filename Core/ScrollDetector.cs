using System.Runtime.InteropServices;

namespace ErrorChecker.Core
{
    // Détecte un défilement vertical (feuille Excel, code VBA...) : au lieu de renvoyer toute la zone,
    // on envoie « recopier ces lignes de Dy pixels » puis seulement la bande nouvellement apparue.
    public static class ScrollDetector
    {
        private const int MinRows = 16;

        // box : zone modifiée. Renvoie le défilement à appliquer à l'image précédente, ou null.
        public static Move? Detect(byte[] previous, byte[] current, int stride, Area box)
        {
            // Zone resserrée aux pixels modifiés : alignée sur les tuiles, elle déborde souvent sur un panneau
            // fixe voisin (explorateur de projet VBA, barre de défilement) qui empêcherait les lignes de correspondre.
            if (TileDiff.Shrink(previous, current, stride, box) is not Area changed || changed.H < 2 * MinRows) return null;
            box = changed;

            // Lignes de l'image précédente au contenu unique. Les lignes unies ou répétées
            // (fréquentes dans une grille vide) sont ambiguës et ne votent pas.
            var previousRows = new Dictionary<int, int>();
            for (int y = box.Y; y < box.Y + box.H; y++)
            {
                var row = Row(previous, stride, box, y);
                if (IsUniform(row)) continue;
                int hash = Hash(row);
                previousRows[hash] = previousRows.ContainsKey(hash) ? -1 : y;
            }

            var votes = new Dictionary<int, int>();
            int candidates = 0;
            for (int y = box.Y; y < box.Y + box.H; y++)
            {
                var row = Row(current, stride, box, y);
                if (IsUniform(row)) continue;
                bool known = previousRows.TryGetValue(Hash(row), out int from);
                if (known && from < 0) continue;   // ligne répétée (liste, grille) : ni voix, ni dans le seuil
                candidates++;
                if (known && from != y)
                    votes[y - from] = votes.GetValueOrDefault(y - from) + 1;
            }
            if (votes.Count == 0) return null;
            var (dy, count) = votes.MaxBy(v => v.Value);
            if (count < Math.Max(MinRows, candidates / 4)) return null;

            // Étendue : lignes identiques à la ligne précédente décalée de dy (source et destination dans la zone).
            int first = -1, last = -1, matched = 0;
            for (int y = Math.Max(box.Y, box.Y + dy); y < Math.Min(box.Y + box.H, box.Y + box.H + dy); y++)
            {
                if (!Row(current, stride, box, y).SequenceEqual(Row(previous, stride, box, y - dy))) continue;
                if (first < 0) first = y;
                last = y;
                matched++;
            }
            if (matched < MinRows || matched * 2 < last - first + 1) return null;
            return new Move(box.X, first, box.W, last - first + 1, dy);
        }

        // Applique le défilement en place (même opération des deux côtés : image de référence et affichage).
        public static void Apply(byte[] pixels, int stride, Move move)
        {
            int bytes = move.W * 4;
            if (move.Dy > 0)
                for (int y = move.Y + move.H - 1; y >= move.Y; y--) CopyRow(y);
            else
                for (int y = move.Y; y < move.Y + move.H; y++) CopyRow(y);

            void CopyRow(int y) =>
                pixels.AsSpan((y - move.Dy) * stride + move.X * 4, bytes).CopyTo(pixels.AsSpan(y * stride + move.X * 4, bytes));
        }

        private static ReadOnlySpan<byte> Row(byte[] pixels, int stride, Area box, int y) =>
            pixels.AsSpan(y * stride + box.X * 4, box.W * 4);

        private static bool IsUniform(ReadOnlySpan<byte> row)
        {
            var pixels = MemoryMarshal.Cast<byte, int>(row);
            foreach (int p in pixels)
                if (p != pixels[0]) return false;
            return true;
        }

        private static int Hash(ReadOnlySpan<byte> row)
        {
            var hash = new HashCode();
            hash.AddBytes(row);
            return hash.ToHashCode();
        }
    }
}
