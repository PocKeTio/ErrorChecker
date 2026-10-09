using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ErrorChecker.Core
{
    // Canal à sens unique sur le partage réseau : un journal en ajout seul, un écrivain, un lecteur.
    //
    // Pourquoi ce format plutôt qu'un fichier réécrit à chaque image ou un fichier par commande :
    //  - aucune énumération de dossier ni File.Exists : le client SMB met ces informations en cache
    //    (10 s pour les dossiers et les métadonnées, 5 s pour « fichier introuvable » par défaut) ;
    //  - le lecteur garde le fichier ouvert et lit à la suite : une requête par sondage, rien n'est relu ;
    //  - jamais de lecture d'un fichier à moitié écrit : un lot est écrit avec son premier en-tête à 0,
    //    puis cet en-tête est écrit en dernier. En-tête nul = « pas encore prêt ».
    //
    // Enregistrement : [longueur u32][message chiffré AES-GCM][tag 16 octets].
    // Le nonce n'est pas stocké : il vaut (canal, numéro d'ordre). Un enregistrement rejoué,
    // déplacé ou modifié fait donc échouer le déchiffrement.
    // Un message vide marque le passage au segment suivant (fichiers limités en taille).
    public static class Channel
    {
        public const string UserToHelper = "u2h";
        public const string HelperToUser = "h2u";
        public const long DefaultSegmentSize = 32L * 1024 * 1024;
        internal const int TagSize = 16;
        internal const int MaxRecordSize = 64 * 1024 * 1024;

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
            File.OpenHandle(SegmentPath(folder, name, 0), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
    }

    public sealed class ChannelWriter : IDisposable
    {
        private readonly object sync = new();
        private readonly string folder;
        private readonly string name;
        private readonly long segmentSize;
        private readonly AesGcm aes;
        private SafeFileHandle file;
        private int segment;
        private long offset;
        private long sequence;

        // create = true : crée le canal ; false : reprend un canal créé par l'autre côté (Channel.Create).
        public ChannelWriter(string folder, string name, byte[] key, bool create, long segmentSize = Channel.DefaultSegmentSize)
        {
            this.folder = folder;
            this.name = name;
            this.segmentSize = segmentSize;
            // Pas de partage en écriture : un second écrivain (lien ouvert deux fois) est refusé.
            file = File.OpenHandle(Channel.SegmentPath(folder, name, 0), create ? FileMode.CreateNew : FileMode.Open,
                FileAccess.Write, FileShare.Read | FileShare.Delete);
            if (RandomAccess.GetLength(file) != 0)
            {
                file.Dispose();
                throw new InvalidOperationException("Cette session a déjà été utilisée. Il faut une nouvelle demande d'aide.");
            }
            aes = new AesGcm(key);
        }

        public void Write(params Msg[] messages) => Write((IReadOnlyList<Msg>)messages);

        public void Write(IReadOnlyList<Msg> messages)
        {
            if (messages.Count == 0) return;
            var plaintexts = messages.Select(Protocol.Encode).ToList();
            lock (sync)
            {
                WriteBatch(plaintexts);
                if (offset >= segmentSize) NextSegment();
            }
        }

        private void WriteBatch(List<byte[]> plaintexts)
        {
            var batch = new byte[plaintexts.Sum(p => 4 + p.Length + Channel.TagSize)];
            int pos = 0;
            foreach (var p in plaintexts)
            {
                BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(pos), p.Length + Channel.TagSize);
                aes.Encrypt(Channel.Nonce(name, sequence++), p, batch.AsSpan(pos + 4, p.Length), batch.AsSpan(pos + 4 + p.Length, Channel.TagSize));
                pos += 4 + p.Length + Channel.TagSize;
            }
            var header = batch[..4];
            batch.AsSpan(0, 4).Clear();
            RandomAccess.Write(file, batch, offset);   // le lot, invisible tant que son en-tête vaut 0...
            RandomAccess.Write(file, header, offset);  // ...puis l'en-tête : le lot apparaît d'un seul coup
            offset += batch.Length;
        }

        private void NextSegment()
        {
            var next = File.OpenHandle(Channel.SegmentPath(folder, name, segment + 1), FileMode.CreateNew,
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
                aes.Dispose();
            }
        }
    }

    public sealed class ChannelReader : IDisposable
    {
        private const int MaxRetries = 5;
        private readonly string folder;
        private readonly string name;
        private readonly AesGcm aes;
        private SafeFileHandle file;
        private int segment;
        private long offset;
        private long sequence;
        private int failures;
        private byte[] buffer = new byte[256 * 1024];

        public ChannelReader(string folder, string name, byte[] key)
        {
            this.folder = folder;
            this.name = name;
            file = Open(0);
            aes = new AesGcm(key);
        }

        private SafeFileHandle Open(int index) =>
            File.OpenHandle(Channel.SegmentPath(folder, name, index), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Renvoie les messages arrivés depuis le dernier appel (liste vide si rien de nouveau).
        public List<Msg> Poll()
        {
            var result = new List<Msg>();
            while (true)
            {
                int filled = RandomAccess.Read(file, buffer, offset);
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
                    if (!valid || !TryDecrypt(buffer.AsSpan(pos + 4, length), out var plain))
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

        private bool TryDecrypt(ReadOnlySpan<byte> record, out byte[] plain)
        {
            plain = new byte[record.Length - Channel.TagSize];
            try
            {
                aes.Decrypt(Channel.Nonce(name, sequence), record[..plain.Length], record[plain.Length..], plain);
            }
            catch (CryptographicException)
            {
                return false;
            }
            failures = 0;
            sequence++;
            return true;
        }

        public void Dispose()
        {
            file.Dispose();
            aes.Dispose();
        }
    }
}
