using ErrorChecker.Core;
using Xunit;

public class ScrollDetectorTests
{
    private const int W = 640, H = 480, Stride = W * 4;

    // Écran factice : un bandeau fixe en haut, une zone de « texte » (lignes toutes différentes) qui défile,
    // un panneau fixe à gauche.
    private static byte[] Screen(int scroll)
    {
        var px = new byte[Stride * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * Stride + x * 4;
                int content = y < 64 ? 7 : x < 128 ? (y * 3) % 251 : (y + scroll) * 131 + x / 8 * 17;  // contenu défilant
                px[i] = (byte)content; px[i + 1] = (byte)(content >> 8); px[i + 2] = (byte)(content >> 3); px[i + 3] = 255;
            }
        return px;
    }

    private static Area DirtyBox(byte[] a, byte[] b) => TileDiff.Bounds(TileDiff.DirtyTiles(a, b, W, H, Stride)!, W, H)!.Value;

    [Theory]
    [InlineData(20)]
    [InlineData(-37)]
    [InlineData(120)]
    public void Scroll_is_detected_and_only_the_new_strip_remains(int scroll)
    {
        var before = Screen(0);
        var after = Screen(scroll);
        var move = ScrollDetector.Detect(before, after, Stride, DirtyBox(before, after));

        Assert.NotNull(move);
        Assert.Equal(-scroll, move!.Value.Dy);
        Assert.Equal(128, move.Value.X);   // le panneau fixe de gauche n'est pas recopié

        ScrollDetector.Apply(before, Stride, move.Value);
        var remaining = TileDiff.DirtyTiles(before, after, W, H, Stride)!;
        int dirtyRows = Enumerable.Range(0, remaining.GetLength(0)).Count(ty => Enumerable.Range(0, remaining.GetLength(1)).Any(tx => remaining[ty, tx]));
        Assert.True(dirtyRows <= (Math.Abs(scroll) + 63) / 64 + 1, $"{dirtyRows} rangées de tuiles encore à envoyer");
    }

    [Fact]
    public void Unrelated_change_is_not_taken_for_a_scroll()
    {
        var before = Screen(0);
        var after = Screen(0);
        var random = new Random(1);
        for (int i = 64 * Stride; i < after.Length; i++) if (i % 4 != 3) after[i] = (byte)random.Next(256);
        Assert.Null(ScrollDetector.Detect(before, after, Stride, DirtyBox(before, after)));
    }

    [Fact]
    public void Blank_or_repeated_rows_do_not_vote()
    {
        var before = new byte[Stride * H];
        var after = new byte[Stride * H];
        for (int y = 0; y < H; y++)   // grille vide : motif de 20 lignes répété
            for (int x = 0; x < W; x++)
            {
                before[y * Stride + x * 4] = (byte)(y % 20 == 0 || x % 64 == 0 ? 200 : 255);
                after[y * Stride + x * 4] = (byte)((y + 7) % 20 == 0 || x % 64 == 0 ? 200 : 255);
            }
        Assert.Null(ScrollDetector.Detect(before, after, Stride, new Area(0, 0, W, H)));
    }

    [Fact]
    public void Apply_handles_both_directions_in_place()
    {
        var px = Enumerable.Range(0, 4 * 10 * 4).Select(i => (byte)(i / 16)).ToArray();   // 4 px de large, 10 lignes ; octet = n° de ligne
        ScrollDetector.Apply(px, 16, new Move(0, 3, 4, 5, 2));    // lignes 1..5 -> 3..7
        Assert.Equal(new byte[] { 0, 1, 2, 1, 2, 3, 4, 5, 8, 9 }, Enumerable.Range(0, 10).Select(y => px[y * 16]));
        ScrollDetector.Apply(px, 16, new Move(0, 0, 4, 5, -3));   // lignes 3..7 -> 0..4
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 3, 4, 5, 8, 9 }, Enumerable.Range(0, 10).Select(y => px[y * 16]));
    }
}

public class ScrollBesidePanelTests
{
    private const int W = 640, H = 480, Stride = W * 4;

    // Éditeur VBA : explorateur de projet fixe (texte varié) sur 100 px, puis le code qui défile.
    // 100 n'est pas un multiple de 64 : la zone modifiée alignée sur les tuiles commence à 64, dans le panneau.
    private static byte[] Vbe(int scroll)
    {
        var px = new byte[Stride * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * Stride + x * 4;
                int c = x < 100 ? (y % 16 < 10 && (x * 31 + y * 17) % 7 < 2 ? 0x000080 : 0xFFFFFF)
                                : ((y + scroll) * 131 + x / 8 * 17) & 0xFFFFFF;
                px[i] = (byte)c; px[i + 1] = (byte)(c >> 8); px[i + 2] = (byte)(c >> 16); px[i + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void Scroll_next_to_a_static_panel_is_detected()
    {
        var before = Vbe(0);
        var after = Vbe(30);
        var box = TileDiff.Bounds(TileDiff.DirtyTiles(before, after, W, H, Stride)!, W, H)!.Value;
        Assert.Equal(64, box.X);   // la zone alignée déborde bien sur le panneau

        var move = ScrollDetector.Detect(before, after, Stride, box);
        Assert.NotNull(move);
        Assert.Equal(100, move!.Value.X);
        Assert.Equal(-30, move.Value.Dy);
    }
}
