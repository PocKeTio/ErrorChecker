using ErrorChecker.Core;
using Xunit;

public class ProtocolTests
{
    public static TheoryData<Msg> SimpleMessages => new()
    {
        new Ping(long.MaxValue), new Pong(-1), new Bye("Fin à 12h"), new Join("Jean (PC-42)"), new Refuse(),
        new Settings(5, 0, 1, false), new Settings(2, 40, 0, true), new KeyStroke(0x77, 4), new Ack(42), new TextInput("é€😀\n\t"), new MouseInput(MouseKind.Wheel, 10, 20, -120, 2), new MouseInput(MouseKind.Down, 1, 2, 0, 4),
    };

    [Theory]
    [MemberData(nameof(SimpleMessages))]
    public void Message_survives_round_trip(Msg msg) => Assert.Equal(msg, Protocol.Decode(Protocol.Encode(msg)));

    [Fact]
    public void Accept_survives_round_trip()
    {
        var accept = Assert.IsType<Accept>(Protocol.Decode(Protocol.Encode(new Accept(1, new[] { "Écran 1", "Écran 2" }))));
        Assert.Equal(1, accept.Primary);
        Assert.Equal(new[] { "Écran 1", "Écran 2" }, accept.Screens);
    }

    [Fact]
    public void Frame_survives_round_trip()
    {
        var sent = new ScreenFrame(7, 1920, 1080, 5, 6, 250, new[] { new Move(0, 100, 1900, 800, -40) },
            new[] { new Patch(0, 64, 128, 64, new byte[] { 1, 2, 3 }), new Patch(1, 2, 3, 4, Array.Empty<byte>()) });
        var received = Assert.IsType<ScreenFrame>(Protocol.Decode(Protocol.Encode(sent)));
        Assert.Equal((7, 1920, 1080, 5, 6, 250), (received.Seq, received.Width, received.Height, received.CursorX, received.CursorY, received.LagMs));
        Assert.Equal(sent.Moves, received.Moves);
        Assert.Equal(sent.Patches.Select(r => (r.X, r.Y, r.W, r.H)), received.Patches.Select(r => (r.X, r.Y, r.W, r.H)));
        Assert.Equal(sent.Patches.Select(r => r.Data), received.Patches.Select(r => r.Data));
    }
}
