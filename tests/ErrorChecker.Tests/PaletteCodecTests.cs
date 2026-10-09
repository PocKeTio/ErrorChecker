using ErrorChecker.Core;
using Xunit;

public class PaletteCodecTests
{
    private const int W = 300, H = 200, Stride = W * 4;

    private static byte[] Ui()
    {
        // Fond, quadrillage et « texte » en quelques teintes : typique d'une feuille Excel.
        var px = new byte[Stride * H];
        var random = new Random(3);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c = x % 60 == 0 || y % 20 == 0 ? 0xD4D4D4 : 0xFFFFFF;
                if (y % 20 is > 5 and < 15 && x % 60 is > 5 and < 45 && random.Next(3) == 0) c = 0x202020 + random.Next(8) * 0x101010;
                int i = y * Stride + x * 4;
                px[i] = (byte)c; px[i + 1] = (byte)(c >> 8); px[i + 2] = (byte)(c >> 16); px[i + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void Ui_area_round_trips_losslessly_and_compresses()
    {
        var source = Ui();
        var area = new Area(17, 9, 250, 170);
        var data = PaletteCodec.Encode(source, Stride, area);

        Assert.NotNull(data);
        Assert.True(data!.Length < area.W * area.H / 4, $"{data.Length} octets");
        var target = new byte[source.Length];
        PaletteCodec.Decode(data, target, Stride, area);
        for (int y = area.Y; y < area.Y + area.H; y++)
            Assert.True(source.AsSpan(y * Stride + area.X * 4, area.W * 4).SequenceEqual(target.AsSpan(y * Stride + area.X * 4, area.W * 4)), $"ligne {y}");
        Assert.Equal(0, target[0]);   // rien n'est écrit hors de la zone
    }

    [Fact]
    public void Too_many_colors_falls_back_to_another_codec()
    {
        var px = new byte[Stride * H];
        new Random(4).NextBytes(px);
        Assert.Null(PaletteCodec.Encode(px, Stride, new Area(0, 0, W, H)));
    }

    [Fact]
    public void Exactly_256_colors_is_accepted()
    {
        var px = new byte[Stride * H];
        for (int i = 0; i < W * H; i++) px[i * 4] = (byte)i;   // 256 nuances de bleu
        var data = PaletteCodec.Encode(px, Stride, new Area(0, 0, W, H));
        Assert.NotNull(data);
        Assert.Equal(255, data![1]);
    }
}
