using System.Diagnostics;
using ErrorChecker.Core;
using Xunit;

public class FrameEncoderOptionsTests
{
    private const int W = 256, H = 128, Stride = W * 4;
    private readonly List<int> richQualities = new();

    // Codec « riche » factice : très compact, marqué 0x03, avec perte si quality > 0.
    private (byte[], bool) TinyRich(byte[] pixels, int stride, Area a, int quality)
    {
        lock (richQualities) richQualities.Add(quality);
        return (new byte[] { 0x03, 0, 0, 0 }, quality > 0);
    }

    private static byte[] Image(Func<int, int, int> color)
    {
        var px = new byte[Stride * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c = color(x, y), i = y * Stride + x * 4;
                px[i] = (byte)c; px[i + 1] = (byte)(c >> 8); px[i + 2] = (byte)(c >> 16); px[i + 3] = 255;
            }
        return px;
    }

    private static readonly int[] Sky = Enumerable.Range(0, 256).Select(i => 0x4080C0 + i * 0x010101 % 0x3F3F3F).ToArray();

    [Fact]
    public void Photo_like_area_within_256_colors_goes_to_jpeg_when_far_cheaper()
    {
        var random = new Random(5);
        var photo = Image((x, y) => Sky[random.Next(256)]);   // ciel bruité : 256 couleurs, palette coûteuse
        var frame = new FrameEncoder(TinyRich).Encode(W, H, Stride, b => photo.CopyTo(b, 0), 0, 0, 60, false, 1, 0)!;
        Assert.All(frame.Patches, p => Assert.Equal(0x03, p.Data[0]));
    }

    [Fact]
    public void Text_area_stays_lossless_palette()
    {
        var ui = Image((x, y) => y % 16 < 10 && (x * 7 + y * 3) % 11 < 3 ? 0x202020 : 0xFFFFFF);
        var frame = new FrameEncoder(TinyRich).Encode(W, H, Stride, b => ui.CopyTo(b, 0), 0, 0, 60, false, 1, 0)!;
        Assert.All(frame.Patches, p => Assert.Equal(PaletteCodec.Magic, p.Data[0]));
    }

    [Fact]
    public void Lossy_areas_are_refined_once_in_high_quality_jpeg_rather_than_png()
    {
        long now = 1;
        var random = new Random(6);
        var photo = Image((x, y) => random.Next(0x1000000));   // trop de couleurs : JPEG
        var encoder = new FrameEncoder(TinyRich, () => now, TimeSpan.FromMilliseconds(700));
        encoder.Encode(W, H, Stride, b => photo.CopyTo(b, 0), 0, 0, 60, false, 1, 0);
        richQualities.Clear();
        now += Stopwatch.Frequency;
        Assert.NotNull(encoder.Encode(W, H, Stride, b => photo.CopyTo(b, 0), 0, 0, 60, false, 2, 0));
        Assert.All(richQualities, q => Assert.Equal(FrameEncoder.RefineQuality, q));
        now += Stopwatch.Frequency;
        Assert.Null(encoder.Encode(W, H, Stride, b => photo.CopyTo(b, 0), 0, 0, 60, false, 3, 0));   // une seule fois
    }

    [Fact]
    public void Economy_mode_reduces_colors_slightly_but_keeps_white_and_black_exact()
    {
        // Couleurs de base + infimes variations (lissage du texte) : 768 couleurs exactes, 3 après réduction.
        var bases = new[] { 0xF0F0F0, 0x202020, 0x285098 };
        var random = new Random(7);
        var ui = Image((x, y) => x < 64 ? 0xFFFFFF : x < 128 ? 0x000000
            : bases[(x / 8 + y / 8) % 3] + (random.Next(8) | random.Next(4) << 8 | random.Next(8) << 16));

        Assert.Throws<AggregateException>(() => new FrameEncoder((p, s, a, q) => throw new InvalidOperationException("JPEG"))
            .Encode(W, H, Stride, b => ui.CopyTo(b, 0), 0, 0, 40, false, 1, 0));   // sans réduction : il faut du JPEG

        var decoder = new FrameDecoder((p, s, st) => throw new InvalidOperationException("pas de JPEG attendu"));
        var frame = new FrameEncoder((p, s, a, q) => throw new InvalidOperationException("tout doit tenir en palette"))
            .Encode(W, H, Stride, b => ui.CopyTo(b, 0), 0, 0, 40, true, 1, 0);
        decoder.Apply((ScreenFrame)Protocol.Decode(Protocol.Encode(frame!)));
        var screen = decoder.Screen!;
        for (int i = 0; i < ui.Length; i++)
            if (i % 4 != 3) Assert.InRange(Math.Abs(screen[i] - ui[i]), 0, 7);
        Assert.Equal(255, screen[0]);                 // le blanc reste blanc
        Assert.Equal(0, screen[100 * 4]);             // le noir reste noir
    }
}
