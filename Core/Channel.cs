using System.Buffers.Binary;
using System.IO;

namespace ErrorChecker.Core
{
    // Canal à sens unique sur le partage réseau : un journal en ajout seul, un écrivain, un lecteur.
    //
    // Pourquoi ce format plutôt qu'un fichier réécrit à chaque image ou un fichier par commande :
    //  - aucune énumération de dossier ni File.Exists : le client SMB met ces informations en cache
    //    (10 s pour les dossiers et les métadonnées, 5 s pour « fichier introuvable » par défaut) ;
    //  - le lecteur garde le fichier ouvert et lit à la suite : une requête par sondage, rien n'est relu ;
    //  - jamais de lecture d'un fichier à moitié écrit : un gros lot est écrit avec son premier en-tête à 0,
    //    puis cet en-tête est écrit en dernier. En-tête nul = « pas encore prêt ». Un petit lot (touches,
    //    clics, acquittements) part en une seule requête : une lecture concurrente incomplète échoue au
    //    déchiffrement et est simplement relue au sondage suivant.
    //
    // Enregistrement : [longueur u32][message chiffré][tag 16 octets] (AES-CTR + HMAC-SHA256, RecordCipher).
    // Le nonce n'est pas stocké : il vaut (canal, numéro d'ordre). Un enregistrement rejoué,
    // déplacé ou modifié fait donc échouer le déchiffrement.
    // Un message vide marque le passage au segment suivant (fichiers limités en taille).
    public static class Channel
    {
        public const string UserToHelper = "u2h";
        public const string HelperToUser = "h2u";
        public const long DefaultSegmentSize = 32L * 1024 * 1024;
        internal const int TagSize = RecordCipher.TagSize;
        internal const int MaxRecordSize = 64 * 1024 * 1024;
        // Jusqu'à cette taille, le client SMB envoie l'écriture en une seule requête, appliquée d'un bloc.
        internal const int SingleWriteLimit = 64 * 1024;

        internal static string SegmentPath(string folder, string name, int index) =>
            Path.Combine(folder, $"{name}.{index:D4}.log");

        internal static byte[] Nonce(string name, long sequence)
        {
            var nonce = new byte[12];
            nonce[0] = name == UserToHelper ? (byte)1 : (byte)2;
            BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), sequence);
            return nonce;
        }

        // Crée le premier segment, vide, pour que le lecteur puisse l'ouvrir avant que l'écrivain n'arrive.
        public static void Create(string folder, string name) =>
            Open(SegmentPath(folder, name, 0), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();

        // Sans tampon (bufferSize 1) : chaque lecture ou écriture va directement au fichier, rien n'est gardé en mémoire.
        internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            new(path, mode, access, share, bufferSize: 1);
    }

    public sealed class ChannelWriter : IDisposable
    {
        private readonly object sync = new();
        private readonly string folder;
        private readonly string name;
        private readonly long segmentSize;
        private readonly RecordCipher cipher;
        private FileStream file;
        private int segment;
        private long offset;
        private long sequence;
        private bool faulted;

        // create = true : crée le canal ; false : reprend un canal créé par l'autre côté (Channel.Create).
        public ChannelWriter(string folder, string name, byte[] key, bool create, long segmentSize = Channel.DefaultSegmentSize)
        {
            this.folder = folder;
            this.name = name;
            this.segmentSize = segmentSize;
            // Pas de partage en écriture : un second écrivain (lien ouvert deux fois) est refusé.
            file = Channel.Open(Channel.SegmentPath(folder, name, 0), create ? FileMode.CreateNew : FileMode.Open,
                FileAccess.Write, FileShare.Read | FileShare.Delete);
            if (file.Length != 0)
            {
                file.Dispose();
                throw new InvalidOperationException("Cette session a déjà été utilisée. Il faut une nouvelle demande d'aide.");
            }
            cipher = new RecordCipher(key);
        }

        public void Write(params Msg[] messages) => Write((IReadOnlyList<Msg>)messages);

        public void Write(IReadOnlyList<Msg> messages)
        {
            if (messages.Count == 0) return;
            var plaintexts = messages.Select(Protocol.Encode).ToList();
            lock (sync)
            {
                // Après un échec d'écriture, position et numéro d'ordre ne sont plus sûrs : le canal est
                // abandonné (le pair verra un silence, pas des données « falsifiées »).
                if (faulted) throw new IOException("Canal interrompu par une erreur d'écriture précédente.");
                try
                {
                    WriteBatch(plaintexts);
                    if (offset >= segmentSize) NextSegment();
                }
                catch
                {
                    faulted = true;
                    throw;
                }
            }
        }

        private void WriteBatch(List<byte[]> plaintexts)
        {
            var batch = new byte[plaintexts.Sum(p => 4 + p.Length + Channel.TagSize)];
            int pos = 0;
            foreach (var p in plaintexts)
            {
                BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(pos), p.Length + Channel.TagSize);
                cipher.Encrypt(Channel.Nonce(name, sequence++), p, batch, pos + 4);
                pos += 4 + p.Length + Channel.TagSize;
            }
            if (batch.Length <= Channel.SingleWriteLimit)
                WriteAt(batch, 0, batch.Length, offset);   // un aller-retour réseau au lieu de deux
            else
            {
                var header = new byte[4];
                Buffer.BlockCopy(batch, 0, header, 0, 4);
                Array.Clear(batch, 0, 4);
                WriteAt(batch, 0, batch.Length, offset);   // le lot, invisible tant que son en-tête vaut 0...
                WriteAt(header, 0, 4, offset);             // ...puis l'en-tête : le lot apparaît d'un seul coup
            }
            offset += batch.Length;
        }

        private void WriteAt(byte[] data, int start, int count, long position)
        {
            file.Position = position;
            file.Write(data, start, count);
            file.Flush();
        }

        private void NextSegment()
        {
            var next = Channel.Open(Channel.SegmentPath(folder, name, segment + 1), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read | FileShare.Delete);
            WriteBatch(new List<byte[]> { Array.Empty<byte>() });
            file.Dispose();
            file = next;
            segment++;
            offset = 0;
        }

        public void Dispose()
        {
            lock (sync)
            {
                file.Dispose();
                cipher.Dispose();
            }
        }
    }

    public sealed class ChannelReader : IDisposable
    {
        private const int MaxRetries = 20;
        private readonly string folder;
        private readonly string name;
        private readonly RecordCipher cipher;
        private FileStream file;
        private int segment;
        private long offset;
        private long sequence;
        private int failures;
        private byte[] buffer = new byte[256 * 1024];

        // Octets visibles dans le fichier, messages incomplets compris : progresse pendant l'arrivée
        // d'une grosse image sur un réseau lent (signe de vie avant même qu'elle soit complète).
        public long Visible { get; private set; }

        public ChannelReader(string folder, string name, byte[] key)
        {
            this.folder = folder;
            this.name = name;
            file = Open(0);
            cipher = new RecordCipher(key);
        }

        private FileStream Open(int index) =>
            Channel.Open(Channel.SegmentPath(folder, name, index), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Renvoie les messages arrivés depuis le dernier appel (liste vide si rien de nouveau).
        public List<Msg> Poll()
        {
            var result = new List<Msg>();
            while (true)
            {
                file.Position = offset;
                int filled = file.Read(buffer, 0, buffer.Length);
                Visible = segment * Channel.DefaultSegmentSize + offset + filled;
                int pos = 0, needed = 0;
                bool nextSegment = false, retryLater = false;
                while (filled - pos >= 4)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos));
                    if (length == 0) break;                               // lot pas encore validé
                    bool valid = length is >= Channel.TagSize and <= Channel.MaxRecordSize;
                    if (valid && filled - pos < 4 + length)
                    {
                        needed = 4 + length;                              // pas encore entièrement lu
                        break;
                    }
                    if (!valid || !TryDecrypt(pos + 4, length, out var plain))
                    {
                        // Un échec isolé peut venir d'une lecture concurrente d'une écriture (cache réseau) :
                        // on relit plus tard. Des échecs répétés = données corrompues ou falsifiées.
                        if (++failures >= MaxRetries) throw new InvalidDataException("Données de session illisibles (corrompues ou falsifiées).");
                        retryLater = true;
                        break;
                    }
                    pos += 4 + length;
                    if (plain.Length == 0) { nextSegment = true; break; }
                    result.Add(Protocol.Decode(plain));
                }
                offset += pos;

                if (retryLater) return result;
                if (nextSegment)
                {
                    var old = Channel.SegmentPath(folder, name, segment);
                    var next = Open(segment + 1);
                    file.Dispose();
                    file = next;
                    segment++;
                    offset = 0;
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    continue;
                }
                if (needed > buffer.Length) { Array.Resize(ref buffer, needed); continue; }
                if (pos > 0 && filled == buffer.Length) continue;         // tampon plein : il reste peut-être des données
                return result;
            }
        }

        private bool TryDecrypt(int start, int length, out byte[] plain)
        {
            if (!cipher.TryDecrypt(Channel.Nonce(name, sequence), buffer, start, length, out plain)) return false;
            failures = 0;
            sequence++;
            return true;
        }

        public void Dispose()
        {
            file.Dispose();
            cipher.Dispose();
        }
    }
}
