using ErrorChecker.Core;
using Xunit;

public class OutboxTests
{
    [Fact]
    public void Only_the_last_pending_mouse_position_and_pong_are_kept()
    {
        var outbox = new Outbox();
        outbox.Post(new MouseInput(MouseKind.Move, 1, 1, 0, 0));
        outbox.Post(new MouseInput(MouseKind.Move, 2, 2, 0, 0));
        outbox.Post(new MouseInput(MouseKind.Down, 2, 2, 0, 0));
        outbox.Post(new MouseInput(MouseKind.Move, 3, 3, 0, 0));
        outbox.Post(new Pong(1));
        outbox.Post(new Pong(2));
        outbox.Post(new KeyStroke(0x74, 0));

        Assert.Equal(new Msg[]
        {
            new MouseInput(MouseKind.Move, 2, 2, 0, 0), new MouseInput(MouseKind.Down, 2, 2, 0, 0),
            new MouseInput(MouseKind.Move, 3, 3, 0, 0), new Pong(2), new KeyStroke(0x74, 0),
        }, outbox.Take());
        Assert.Empty(outbox.Take());
    }

    [Fact]
    public async Task Messages_are_written_in_order_through_the_channel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "errorchecker-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var key = TestData.RandomBytes(32);
            using var writer = new ChannelWriter(dir, Channel.HelperToUser, key, create: true);
            using var reader = new ChannelReader(dir, Channel.HelperToUser, key);
            var outbox = new Outbox();
            using var cts = new CancellationTokenSource();
            var run = outbox.RunAsync(writer, () => new Ping(0), cts.Token);
            for (int i = 0; i < 50; i++) outbox.Post(new KeyStroke(i, 0));

            var received = new List<KeyStroke>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (received.Count < 50 && DateTime.UtcNow < deadline)
            {
                received.AddRange(reader.Poll().OfType<KeyStroke>());
                await Task.Delay(5);
            }
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(Enumerable.Range(0, 50), received.Select(k => k.Vk));
        }
        finally { Directory.Delete(dir, true); }
    }
}
