namespace ErrorChecker.Core
{
    // Messages en attente d'envoi : un seul envoyeur écrit dans le canal, sans jamais bloquer
    // l'interface ni la boucle de réception. Ce qui arrive pendant une écriture part groupé dans la suivante.
    public sealed class Outbox
    {
        private readonly object sync = new();
        private readonly SemaphoreSlim signal = new(0);
        private List<Msg> pending = new();

        public void Post(Msg msg)
        {
            lock (sync)
            {
                // Seule la dernière position de souris et la dernière réponse à un ping comptent.
                bool replaces = pending.Count > 0 && (msg, pending[^1]) switch
                {
                    (MouseInput { Action: MouseKind.Move }, MouseInput { Action: MouseKind.Move }) => true,
                    (Pong, Pong) => true,
                    _ => false
                };
                if (replaces) pending[^1] = msg;
                else pending.Add(msg);
            }
            if (signal.CurrentCount == 0) signal.Release();
        }

        public List<Msg> Take()
        {
            lock (sync)
            {
                var batch = pending;
                pending = new List<Msg>();
                return batch;
            }
        }

        // Envoie au fil de l'eau ; keepAlive est posté toutes les 2 s (signe de vie, mesure de l'aller-retour).
        public async Task RunAsync(ChannelWriter writer, Func<Msg> keepAlive, CancellationToken token)
        {
            var lastKeepAlive = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastKeepAlive > TimeSpan.FromSeconds(2))
                {
                    Post(keepAlive());
                    lastKeepAlive = DateTime.UtcNow;
                }
                writer.Write(Take());
                await signal.WaitAsync(TimeSpan.FromSeconds(1), token);
            }
        }
    }
}
