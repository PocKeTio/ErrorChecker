using System.Diagnostics;

namespace ErrorChecker.Core
{
    // Compresse une zone trop riche en couleurs pour la palette (JPEG/PNG côté Windows).
    // quality : qualité JPEG, 0 = sans perte. Lossy : le rendu envoyé est approximatif.
    public delegate (byte[] Data, bool Lossy) AreaEncoder(byte[] pixels, int stride, Area area, int quality);

    // Transforme les captures successives en images différentielles :
    //  1. défilement détecté -> « recopier ces lignes » au lieu de renvoyer la zone ;
    //  2. seules les tuiles modifiées partent, découpées en bandes compressées en parallèle ;
    //  3. zones à peu de couleurs (texte, grilles, dialogues) -> palette sans perte, sinon AreaEncoder ;
    //  4. une zone envoyée avec perte puis restée immobile est renvoyée sans perte (texte net à l'arrêt).
    // Invariant : après chaque image transmise, l'écran du dépanneur = l'image de référence d'ici.
    public sealed class FrameEncoder
    {
        public const int BandHeight = 2 * Tile;
        private const int Tile = TileDiff.TileSize;
        private const int MaxRefineTilesPerFrame = 48;

        private readonly AreaEncoder encodeRichArea;
        private readonly Func<long> clock;
        private readonly long refineDelay;
        private byte[]? previous;
        private byte[]? current;
        private long[,]? lossySince; // par tuile : instant d'envoi d'un rendu avec perte non encore affiné (0 = net)
        private (int W, int H) size;
        private (int X, int Y) lastCursor = (int.MinValue, int.MinValue);

        public FrameEncoder(AreaEncoder encodeRichArea, Func<long>? clock = null, TimeSpan? refineDelay = null)
        {
            this.encodeRichArea = encodeRichArea;
            this.clock = clock ?? Stopwatch.GetTimestamp;
            this.refineDelay = (long)((refineDelay ?? TimeSpan.FromMilliseconds(700)).TotalSeconds * Stopwatch.Frequency);
        }

        // La prochaine image sera complète (dépanneur qui arrive, changement d'écran ou de qualité).
        public void Reset() => previous = null;

        // fill : copie la capture (BGRA, stride octets par ligne) dans le tampon fourni.
        // Renvoie null si rien n'a bougé depuis l'appel précédent.
        public ScreenFrame? Encode(int w, int h, int stride, Action<byte[]> fill, int cursorX, int cursorY, int quality, int seq, int lagMs)
        {
            if (size != (w, h)) { size = (w, h); previous = null; }
            if (current?.Length != stride * h) current = new byte[stride * h];
            fill(current);

            long now = clock();
            int rows = (h + Tile - 1) / Tile, cols = (w + Tile - 1) / Tile;
            var dirty = TileDiff.DirtyTiles(previous, current, w, h, stride);
            if (dirty == null || lossySince?.GetLength(0) != rows || lossySince.GetLength(1) != cols) lossySince = new long[rows, cols];

            var moves = new List<Move>();
            if (dirty != null && TileDiff.Bounds(dirty, w, h) is Area box && box.H >= BandHeight
                && ScrollDetector.Detect(previous!, current, stride, box) is Move move)
            {
                ScrollDetector.Apply(previous!, stride, move);
                moves.Add(move);
                ShiftLossyTiles(move, now);
                dirty = TileDiff.DirtyTiles(previous, current, w, h, stride);
            }
            (previous, current) = (current, previous);
            var pixels = previous;

            var pieces = new List<(Area Area, int Quality)>();
            foreach (var area in dirty == null ? new List<Area> { new(0, 0, w, h) } : TileDiff.Merge(dirty, w, h))
                pieces.AddRange(Bands(area).Select(b => (b, quality)));
            if (dirty != null)
                foreach (var area in RefineAreas(dirty, now, w, h))
                    pieces.AddRange(Bands(area).Select(b => (b, 0)));

            if (pieces.Count == 0 && moves.Count == 0 && (cursorX, cursorY) == lastCursor) return null;
            lastCursor = (cursorX, cursorY);

            var encoded = new List<(Patch Patch, bool Lossy)>[pieces.Count];
            Parallel.For(0, pieces.Count, i => encoded[i] = EncodePiece(pixels, stride, pieces[i].Area, pieces[i].Quality));
            var patches = encoded.SelectMany(e => e).ToList();

            foreach (var (patch, lossy) in patches)
                ForTiles(new Area(patch.X, patch.Y, patch.W, patch.H), (ty, tx) => lossySince![ty, tx] = lossy ? now : 0);
            return new ScreenFrame(seq, w, h, cursorX, cursorY, lagMs, moves.ToArray(), patches.Select(e => e.Patch).ToArray());
        }

        // Palette si possible ; sinon colonne de tuiles par colonne, pour que seule la partie riche
        // (photo, icônes) parte avec perte et que le texte autour reste net.
        private List<(Patch, bool)> EncodePiece(byte[] pixels, int stride, Area a, int quality)
        {
            var result = new List<(Patch, bool)>();
            if (PaletteCodec.Encode(pixels, stride, a) is byte[] whole)
            {
                result.Add((new Patch(a.X, a.Y, a.W, a.H, whole), false));
                return result;
            }
            int richStart = -1;
            for (int x = a.X; x < a.X + a.W; x += Tile)
            {
                var column = a with { X = x, W = Math.Min(Tile, a.X + a.W - x) };
                var palette = column.W == a.W ? null : PaletteCodec.Encode(pixels, stride, column);
                if (palette == null)
                {
                    if (richStart < 0) richStart = x;
                    continue;
                }
                AddRich(x);
                result.Add((new Patch(column.X, column.Y, column.W, column.H, palette), false));
            }
            AddRich(a.X + a.W);
            return result;

            void AddRich(int end)   // colonnes riches contiguës : une seule zone JPEG
            {
                if (richStart < 0) return;
                var rich = a with { X = richStart, W = end - richStart };
                var (data, lossy) = encodeRichArea(pixels, stride, rich, quality);
                result.Add((new Patch(rich.X, rich.Y, rich.W, rich.H, data), lossy));
                richStart = -1;
            }
        }

        // Grandes zones découpées en bandes : encodage parallèle ici, décodage parallèle chez le dépanneur.
        private static IEnumerable<Area> Bands(Area a)
        {
            for (int y = a.Y; y < a.Y + a.H; y += BandHeight)
                yield return a with { Y = y, H = Math.Min(BandHeight, a.Y + a.H - y) };
        }

        // Tuiles envoyées avec perte et immobiles depuis refineDelay, dans la limite d'un budget par image.
        private List<Area> RefineAreas(bool[,] dirty, long now, int w, int h)
        {
            var refine = new bool[dirty.GetLength(0), dirty.GetLength(1)];
            int count = 0;
            for (int ty = 0; ty < refine.GetLength(0) && count < MaxRefineTilesPerFrame; ty++)
                for (int tx = 0; tx < refine.GetLength(1) && count < MaxRefineTilesPerFrame; tx++)
                    if (!dirty[ty, tx] && lossySince![ty, tx] != 0 && now - lossySince[ty, tx] >= refineDelay)
                    {
                        refine[ty, tx] = true;
                        count++;
                    }
            return count == 0 ? new List<Area>() : TileDiff.Merge(refine, w, h);
        }

        // Le contenu recopié emporte son état « avec perte » : chaque tuile d'arrivée hérite des tuiles d'où
        // vient son contenu (à la tuile près, par excès). Délai d'affinage relancé : ça bouge encore.
        private void ShiftLossyTiles(Move move, long now)
        {
            int tx0 = move.X / Tile, tx1 = (move.X + move.W - 1) / Tile;
            int ty0 = move.Y / Tile, ty1 = (move.Y + move.H - 1) / Tile;
            var shifted = new bool[ty1 - ty0 + 1, tx1 - tx0 + 1];
            for (int ty = ty0; ty <= ty1; ty++)
            {
                int top = Math.Max(ty * Tile, move.Y), bottom = Math.Min(ty * Tile + Tile, move.Y + move.H) - 1;
                bool partial = top > ty * Tile || bottom < ty * Tile + Tile - 1;   // tuile en partie hors du déplacement
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    bool lossy = partial && lossySince![ty, tx] != 0;
                    for (int sy = (top - move.Dy) / Tile; sy <= (bottom - move.Dy) / Tile; sy++)
                        lossy |= lossySince![sy, tx] != 0;
                    shifted[ty - ty0, tx - tx0] = lossy;
                }
            }
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                    lossySince![ty, tx] = shifted[ty - ty0, tx - tx0] ? now : 0;
        }

        private static void ForTiles(Area a, Action<int, int> action)
        {
            for (int ty = a.Y / Tile; ty <= (a.Y + a.H - 1) / Tile; ty++)
                for (int tx = a.X / Tile; tx <= (a.X + a.W - 1) / Tile; tx++)
                    action(ty, tx);
        }
    }

    // Décode les zones JPEG/PNG (WPF côté Windows) dans l'écran local.
    public delegate void ImageDecoder(Patch patch, byte[] screen, int stride);

    // Côté dépanneur : tient à jour la copie locale de l'écran de l'utilisateur.
    public sealed class FrameDecoder
    {
        private readonly ImageDecoder decodeImage;

        public FrameDecoder(ImageDecoder decodeImage) => this.decodeImage = decodeImage;

        public byte[]? Screen { get; private set; }

        // Renvoie true si l'écran a été réalloué (tout est à redessiner).
        public bool Apply(ScreenFrame frame)
        {
            int stride = frame.Width * 4;
            bool resized = Screen == null || Screen.Length != stride * frame.Height;
            if (resized) Screen = new byte[stride * frame.Height];
            var screen = Screen!;
            foreach (var move in frame.Moves) ScrollDetector.Apply(screen, stride, move);
            Parallel.ForEach(frame.Patches, patch =>
            {
                if (patch.Data.Length > 0 && patch.Data[0] == PaletteCodec.Magic)
                    PaletteCodec.Decode(patch.Data, screen, stride, new Area(patch.X, patch.Y, patch.W, patch.H));
                else
                    decodeImage(patch, screen, stride);
            });
            return resized;
        }
    }
}
