using System.Security.Cryptography;
using System.Text;

namespace ErrorChecker.Core
{
    // Chiffrement authentifié des enregistrements : AES-CTR + HMAC-SHA256 (« encrypt-then-MAC »).
    // .NET Framework 4.8 n'a pas AES-GCM ; cette construction standard joue le même rôle avec ce qu'il fournit.
    // Sortie : [chiffré, même longueur que le clair][tag 16 octets]. Le nonce (12 octets) n'est jamais réutilisé
    // avec la même clé : il vaut (canal, numéro d'ordre).
    internal sealed class RecordCipher : IDisposable
    {
        public const int TagSize = 16;
        private readonly Aes aes;
        private readonly ICryptoTransform blocks;
        private readonly HMACSHA256 mac;

        public RecordCipher(byte[] key)
        {
            // Deux clés indépendantes dérivées de la clé de session : une pour chiffrer, une pour authentifier.
            using var derive = new HMACSHA256(key);
            aes = Aes.Create();
            aes.Mode = CipherMode.ECB;   // sert seulement à chiffrer les blocs compteur (mode CTR)
            aes.Padding = PaddingMode.None;
            aes.Key = derive.ComputeHash(Encoding.ASCII.GetBytes("ErrorChecker/enc"));
            blocks = aes.CreateEncryptor();
            mac = new HMACSHA256(derive.ComputeHash(Encoding.ASCII.GetBytes("ErrorChecker/mac")));
        }

        public void Encrypt(byte[] nonce, byte[] plain, byte[] output, int offset)
        {
            Xor(nonce, plain, 0, plain.Length, output, offset);
            Buffer.BlockCopy(Tag(nonce, output, offset, plain.Length), 0, output, offset + plain.Length, TagSize);
        }

        // record[offset..] : chiffré puis tag. Renvoie false si le tag ne correspond pas (rien n'est déchiffré).
        public bool TryDecrypt(byte[] nonce, byte[] record, int offset, int length, out byte[] plain)
        {
            plain = new byte[length - TagSize];
            var expected = Tag(nonce, record, offset, plain.Length);
            int diff = 0;   // comparaison en temps constant
            for (int i = 0; i < TagSize; i++) diff |= expected[i] ^ record[offset + plain.Length + i];
            if (diff != 0) return false;
            Xor(nonce, record, offset, plain.Length, plain, 0);
            return true;
        }

        private byte[] Tag(byte[] nonce, byte[] data, int offset, int length)
        {
            mac.Initialize();
            mac.TransformBlock(nonce, 0, nonce.Length, null, 0);
            mac.TransformFinalBlock(data, offset, length);
            return mac.Hash;   // les 16 premiers octets servent de tag
        }

        // CTR : blocs (nonce || compteur 32 bits) chiffrés d'un seul appel, puis XOR avec les données.
        private void Xor(byte[] nonce, byte[] source, int sourceOffset, int length, byte[] target, int targetOffset)
        {
            int count = (length + 15) / 16;
            var stream = new byte[count * 16];
            for (int b = 0; b < count; b++)
            {
                Buffer.BlockCopy(nonce, 0, stream, b * 16, 12);
                stream[b * 16 + 12] = (byte)(b >> 24);
                stream[b * 16 + 13] = (byte)(b >> 16);
                stream[b * 16 + 14] = (byte)(b >> 8);
                stream[b * 16 + 15] = (byte)b;
            }
            var keystream = new byte[stream.Length];   // tampons distincts : le chiffrement en place n'est pas garanti
            if (count > 0) blocks.TransformBlock(stream, 0, stream.Length, keystream, 0);
            for (int i = 0; i < length; i++) target[targetOffset + i] = (byte)(source[sourceOffset + i] ^ keystream[i]);
        }

        public void Dispose()
        {
            blocks.Dispose();
            aes.Dispose();
            mac.Dispose();
        }
    }
}
