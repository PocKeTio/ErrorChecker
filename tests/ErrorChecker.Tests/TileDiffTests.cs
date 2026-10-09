using ErrorChecker.Core;
using Xunit;

public class TileDiffTests
{
    private const int W = 200, H = 150, Stride = W * 4;

    private static byte[] Blank() => new byte[Stride * H];

    private static byte[] With(params (int X, int Y)[] pixels)
    {
        var frame = Blank();
        foreach (var (x, y) in pixels) frame[y * Stride + x * 4] = 255;
        return frame;
    }

    private static List<Area> Diff(byte[]? previous, byte[] current) => TileDiff.DirtyAreas(previous, current, W, H, Stride);

    [Fact]
    public void First_capture_is_sent_whole() => Assert.Equal(new[] { new Area(0, 0, W, H) }, Diff(null, Blank()));

    [Fact]
    public void Identical_capture_sends_nothing() => Assert.Empty(Diff(Blank(), Blank()));

    [Fact]
    public void One_pixel_change_sends_its_tile() =>
        Assert.Equal(new[] { new Area(64, 0, 64, 64) }, Diff(Blank(), With((70, 5))));

    [Fact]
    public void Edge_tiles_are_clipped_to_the_screen() =>
        Assert.Equal(new[] { new Area(192, 128, 8, 22) }, Diff(Blank(), With((199, 149))));

    [Fact]
    public void Adjacent_tiles_on_a_row_are_merged() =>
        Assert.Equal(new[] { new Area(0, 0, 128, 64) }, Diff(Blank(), With((10, 10), (70, 10))));

    [Fact]
    public void Same_band_on_consecutive_rows_is_merged() =>
        Assert.Equal(new[] { new Area(0, 0, 64, 128) }, Diff(Blank(), With((10, 10), (10, 70))));

    [Fact]
    public void Different_bands_stay_separate() =>
        Assert.Equal(new[] { new Area(0, 0, 128, 64), new Area(0, 64, 64, 64) }, Diff(Blank(), With((10, 10), (70, 10), (10, 70))));

    [Fact]
    public void Mostly_changed_screen_is_sent_whole()
    {
        // 4 x 3 = 12 tuiles, 7 modifiées
        var changed = With((0, 0), (64, 0), (128, 0), (192, 0), (0, 64), (64, 64), (128, 64));
        Assert.Equal(new[] { new Area(0, 0, W, H) }, Diff(Blank(), changed));
    }

    [Fact]
    public void Resolution_change_sends_whole_screen() =>
        Assert.Equal(new[] { new Area(0, 0, W, H) }, TileDiff.DirtyAreas(new byte[10], Blank(), W, H, Stride));
}
