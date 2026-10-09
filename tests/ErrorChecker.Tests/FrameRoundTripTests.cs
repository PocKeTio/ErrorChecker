using System.Diagnostics;
using ErrorChecker.Core;
using Xunit;

// Simule une session : captures successives -> FrameEncoder -> protocole -> FrameDecoder.
// Les zones « riches » passent par un codec brut sans perte (le JPEG n'existe que sous Windows),
// signalé « avec perte » quand quality > 0 pour exercer l'affinage.
public class FrameRoundTripTests
{
    private const int W = 640, H = 480, Stride = W * 4;
    private long now = 1;

    private static (byte[], bool) RawEncode(byte[] pixels, int stride, Area a, int quality)
    {
        var data = new byte[1 + a.W * a.H * 4];
        data[0] = 0x02;
        for (int y = 0; y < a.H; y++) Buffer.BlockCopy(pixels, (a.Y + y) * stride + a.X * 4, data, 1 + y * a.W * 4, a.W * 4);
        return (data, quality > 0);
    }

    private static void RawDecode(Patch p, byte[] screen, int stride)
    {
        Assert.Equal(0x02, p.Data[0]);
        for (int y = 0; y < p.H; y++) Buffer.BlockCopy(p.Data, 1 + y * p.W * 4, screen, (p.Y + y) * stride + p.X * 4, p.W * 4);
    }

    // Bandeau fixe, panneau fixe à gauche, zone de texte qui défile, et un encart « photo » (couleurs variées).
    private static byte[] Screen(int scroll, int edit = -1)
    {
        var px = new byte[Stride * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int line = y + scroll;   // lignes de « texte » de 12 px séparées de 6 px, glyphes pseudo-aléatoires
                int c = y < 64 ? 0x2B579A : x < 128 ? 0xF3F3F3 : (line % 18 < 12 && Noise(x / 2, line) < 300 ? 0x202020 : 0xFFFFFF);
                if (x >= 448 && x < 600 && y >= 300 && y < 420) c = (x * 2654435761u ^ (uint)(y * 40503)).GetHashCode() & 0xFFFFFF; // « photo »
                if (edit >= 0 && x >= 200 && x < 300 && y >= 100 && y < 120) c = (x * edit + y) & 0xFFFFFF;
                int i = y * Stride + x * 4;
                px[i] = (byte)c; px[i + 1] = (byte)(c >> 8); px[i + 2] = (byte)(c >> 16); px[i + 3] = 255;
            }
        return px;
    }

    private static int Noise(int a, int b) => (int)((uint)(a * 73856093 ^ b * 19349663) % 1000);

    private ScreenFrame? Send(FrameEncoder encoder, FrameDecoder decoder, byte[] capture, int quality, ref int seq)
    {
        var frame = encoder.Encode(W, H, Stride, b => capture.CopyTo(b, 0), 10, 10, quality, false, seq + 1, 0);
        if (frame == null) return null;
        seq++;
        frame = (ScreenFrame)Protocol.Decode(Protocol.Encode(frame));   // passe par le vrai format
        decoder.Apply(frame);
        Assert.True(capture.AsSpan().SequenceEqual(decoder.Screen), $"écran du dépanneur différent après l'image {frame.Seq}");
        return frame;
    }

    [Theory]
    [InlineData(0)]    // sans perte
    [InlineData(60)]   // zones riches « avec perte » -> affinées plus tard
    public void Helper_screen_always_matches_user_screen(int quality)
    {
        var encoder = new FrameEncoder(RawEncode, () => now, TimeSpan.FromMilliseconds(700));
        var decoder = new FrameDecoder(RawDecode);
        int seq = 0;

        var first = Send(encoder, decoder, Screen(0), quality, ref seq)!;
        Assert.Equal(W * H, first.Patches.Sum(p => p.W * p.H));                              // première image complète
        Assert.All(first.Patches, p => Assert.True(p.H <= FrameEncoder.BandHeight));       // découpé en bandes

        Assert.Null(Send(encoder, decoder, Screen(0), quality, ref seq));                   // rien n'a bougé

        var edit = Send(encoder, decoder, Screen(0, edit: 3), quality, ref seq)!;            // saisie dans une cellule
        Assert.Empty(edit.Moves);
        Assert.True(edit.Patches.Sum(p => p.W * p.H) <= 100 * 20, "seuls les pixels modifiés (100x20) sont envoyés, pas les tuiles entières");

        var down = Send(encoder, decoder, Screen(37, edit: 3), quality, ref seq)!;            // défilement
        Assert.Single(down.Moves);
        Send(encoder, decoder, Screen(5, edit: 3), quality, ref seq);                        // défilement inverse

        // Affinage : 0,7 s plus tard sans changement, les zones envoyées avec perte repartent sans perte.
        now += Stopwatch.Frequency;
        var refine = Send(encoder, decoder, Screen(5, edit: 3), quality, ref seq);
        if (quality == 0) Assert.Null(refine);
        else
        {
            Assert.NotNull(refine);
            Assert.All(refine!.Patches, p => Assert.InRange(p.X, 448 - 64, 600));           // seulement l'encart « photo »
            now += Stopwatch.Frequency;
            Assert.Null(Send(encoder, decoder, Screen(5, edit: 3), quality, ref seq));        // une seule fois
        }
    }

    // Zones d'une même image décodées en parallèle : elles ne doivent jamais se chevaucher,
    // sinon l'ordre de décodage décide du résultat (ex. JPEG flou par-dessus la version nette).
    [Fact]
    public void Patches_of_a_frame_never_overlap_even_when_most_of_the_screen_changes()
    {
        var encoder = new FrameEncoder(RawEncode, () => now, TimeSpan.FromMilliseconds(700));
        var decoder = new FrameDecoder(RawDecode);
        int seq = 0;
        Send(encoder, decoder, Screen(0), 60, ref seq);          // l'encart « photo » part avec perte
        now += Stopwatch.Frequency;                               // ... et devient à affiner
        var busy = Screen(0);
        for (int y = 0; y < 300; y++)                             // plus de la moitié de l'écran change, pas la photo
            for (int x = 0; x < W; x++) busy[y * Stride + x * 4] ^= 0x55;
        var frame = Send(encoder, decoder, busy, 60, ref seq)!;
        for (int i = 0; i < frame.Patches.Length; i++)
            for (int j = i + 1; j < frame.Patches.Length; j++)
            {
                Patch a = frame.Patches[i], b = frame.Patches[j];
                bool overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                Assert.False(overlap, $"({a.X},{a.Y},{a.W}x{a.H}) chevauche ({b.X},{b.Y},{b.W}x{b.H})");
            }
    }

    [Fact]
    public void Scroll_sends_far_less_than_repainting()
    {
        var encoder = new FrameEncoder(RawEncode);
        var decoder = new FrameDecoder(RawDecode);
        int seq = 0;
        Send(encoder, decoder, Screen(0), 0, ref seq);
        var scrolled = Send(encoder, decoder, Screen(40), 0, ref seq)!;

        var repaint = new FrameEncoder(RawEncode);
        repaint.Encode(W, H, Stride, b => Screen(0).CopyTo(b, 0), 0, 0, 0, false, 1, 0);
        int movedArea = scrolled.Patches.Sum(p => p.W * p.H);
        Assert.True(movedArea < W * H / 4, $"{movedArea} pixels renvoyés malgré le défilement");
    }

    [Fact]
    public void Resolution_change_restarts_with_a_full_frame()
    {
        var encoder = new FrameEncoder(RawEncode);
        encoder.Encode(W, H, Stride, b => Screen(0).CopyTo(b, 0), 0, 0, 0, false, 1, 0);
        var small = new byte[320 * 4 * 200];
        Array.Fill(small, (byte)255);
        var frame = encoder.Encode(320, 200, 320 * 4, b => small.CopyTo(b, 0), 0, 0, 0, false, 2, 0)!;
        Assert.Equal(320 * 200, frame.Patches.Sum(p => p.W * p.H));
    }
}
