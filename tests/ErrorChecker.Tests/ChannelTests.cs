using System.Security.Cryptography;
using ErrorChecker.Core;
using Xunit;

public class ChannelTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public ChannelTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);

    private string Segment0(string folder) => Path.Combine(folder, $"{Channel.UserToHelper}.0000.log");

    private static IEnumerable<long> Ticks(IEnumerable<Msg> messages) => messages.Cast<Ping>().Select(p => p.Ticks);

    [Fact]
    public void Messages_arrive_in_send_order()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        Assert.Empty(reader.Poll());

        // Plus de 10 messages : l'ancienne version triait cmd_10 avant cmd_2.
        for (int i = 0; i < 20; i++) writer.Write(new Ping(i));
        writer.Write(new Ping(20), new Ping(21));

        Assert.Equal(Enumerable.Range(0, 22).Select(i => (long)i), Ticks(reader.Poll()));
        Assert.Empty(reader.Poll());
    }

    [Fact]
    public void Batch_is_invisible_until_its_header_is_written()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        writer.Write(new Ping(1));
        writer.Write(new Ping(2), new Ping(3));

        // On remet l'en-tête du second lot à zéro : c'est l'état entre l'écriture du lot et sa validation.
        var bytes = File.ReadAllBytes(Segment0(dir));
        int second = 4 + BitConverter.ToInt32(bytes, 0);
        var header = bytes[second..(second + 4)];
        Overwrite(Segment0(dir), second, new byte[4]);
        Assert.Equal(new[] { 1L }, Ticks(reader.Poll()));
        for (int i = 0; i < 20; i++) Assert.Empty(reader.Poll());  // l'écrivain peut être lent : attendre n'est pas une erreur

        Overwrite(Segment0(dir), second, header);
        Assert.Equal(new[] { 2L, 3L }, Ticks(reader.Poll()));
    }

    [Fact]
    public void Partially_transferred_record_is_delivered_once_complete()
    {
        var source = Path.Combine(dir, "source");
        var target = Path.Combine(dir, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var data = RandomNumberGenerator.GetBytes(10_000);
        using (var writer = new ChannelWriter(source, Channel.UserToHelper, key, create: true))
            writer.Write(new ScreenFrame(1, 100, 100, 1, 2, 0, Array.Empty<Move>(), new[] { new Patch(0, 0, 50, 50, data) }));
        var bytes = File.ReadAllBytes(Segment0(source));

        Channel.Create(target, Channel.UserToHelper);
        using var reader = new ChannelReader(target, Channel.UserToHelper, key);
        for (int done = 0; done < bytes.Length; done += 1000)
        {
            Assert.Empty(reader.Poll());
            using var fs = new FileStream(Segment0(target), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(bytes, done, Math.Min(1000, bytes.Length - done));
        }

        var frame = Assert.IsType<ScreenFrame>(Assert.Single(reader.Poll()));
        Assert.Equal(data, frame.Patches[0].Data);
    }

    [Fact]
    public void Record_larger_than_read_buffer_is_delivered()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        writer.Write(new ScreenFrame(1, 1920, 1080, 0, 0, 0, Array.Empty<Move>(), new[] { new Patch(0, 0, 1920, 1080, data) }), new Ping(7));

        var messages = reader.Poll();
        Assert.Equal(data, Assert.IsType<ScreenFrame>(messages[0]).Patches[0].Data);
        Assert.Equal(7, Assert.IsType<Ping>(messages[1]).Ticks);
    }

    [Fact]
    public void Segments_rotate_and_consumed_ones_are_deleted()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true, segmentSize: 4096);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        var sent = Enumerable.Range(0, 100).Select(i => i + new string('x', 200)).ToList();
        foreach (var text in sent) writer.Write(new TextInput(text));
        Assert.True(Directory.GetFiles(dir).Length > 5);

        Assert.Equal(sent, reader.Poll().Cast<TextInput>().Select(t => t.Text));
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public void Concurrent_writer_and_reader_never_lose_or_reorder_messages()
    {
        var random = new Random(42);
        var batches = Enumerable.Range(0, 600)
            .Select(_ => Enumerable.Range(0, random.Next(1, 6)).Select(_ => (Msg)new TextInput(new string('a', random.Next(0, 3000)) + random.Next())).ToArray())
            .ToList();
        var expected = batches.SelectMany(b => b).ToList();

        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true, segmentSize: 64 * 1024);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        var writing = Task.Run(() => { foreach (var batch in batches) writer.Write(batch); });

        var received = new List<Msg>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (received.Count < expected.Count && DateTime.UtcNow < deadline) received.AddRange(reader.Poll());
        writing.Wait();

        Assert.Equal(expected, received);
    }

    [Fact]
    public void Replayed_record_is_rejected()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, key);
        writer.Write(new TextInput("del *.*"));
        Assert.Single(reader.Poll());

        // Quelqu'un ayant accès au partage recopie l'enregistrement à la fin du fichier.
        var bytes = File.ReadAllBytes(Segment0(dir));
        using (var fs = new FileStream(Segment0(dir), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.Write(bytes);

        Assert.Throws<InvalidDataException>(() => { for (int i = 0; i < 50; i++) reader.Poll(); });
    }

    [Fact]
    public void Wrong_key_cannot_read()
    {
        using var writer = new ChannelWriter(dir, Channel.UserToHelper, key, create: true);
        using var reader = new ChannelReader(dir, Channel.UserToHelper, RandomNumberGenerator.GetBytes(32));
        writer.Write(new Ping(1));
        Assert.Throws<InvalidDataException>(() => { for (int i = 0; i < 50; i++) reader.Poll(); });
    }

    [Fact]
    public void Used_channel_cannot_be_reopened_by_a_second_helper()
    {
        Channel.Create(dir, Channel.HelperToUser);
        using (var first = new ChannelWriter(dir, Channel.HelperToUser, key, create: false))
            first.Write(new Join("dépanneur 1"));

        Assert.Throws<InvalidOperationException>(() => new ChannelWriter(dir, Channel.HelperToUser, key, create: false));
    }

    private static void Overwrite(string path, int offset, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = offset;
        fs.Write(data);
    }
}
