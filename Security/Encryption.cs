using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ErrorChecker.Security
{
    public class Encryption
    {
        private static readonly string KEY_FILE = "encryption.key";
        private static readonly int KEY_SIZE = 32; // 256 bits
        private static readonly int IV_SIZE = 16;  // 128 bits

        private byte[] key;
        private readonly string keyPath;

        public Encryption(string basePath)
        {
            keyPath = Path.Combine(basePath, KEY_FILE);
            InitializeKey();
        }

        private void InitializeKey()
        {
            if (File.Exists(keyPath))
            {
                key = File.ReadAllBytes(keyPath);
            }
            else
            {
                key = new byte[KEY_SIZE];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(key);
                }
                File.WriteAllBytes(keyPath, key);
            }
        }

        public byte[] Encrypt(byte[] data)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();

                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    // Écrire l'IV en premier
                    msEncrypt.Write(aes.IV, 0, aes.IV.Length);

                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        csEncrypt.Write(data, 0, data.Length);
                        csEncrypt.FlushFinalBlock();
                    }

                    return msEncrypt.ToArray();
                }
            }
        }

        public byte[] Decrypt(byte[] encryptedData)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;

                using (MemoryStream msDecrypt = new MemoryStream(encryptedData))
                {
                    // Lire l'IV
                    byte[] iv = new byte[IV_SIZE];
                    msDecrypt.Read(iv, 0, iv.Length);
                    aes.IV = iv;

                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    using (MemoryStream resultStream = new MemoryStream())
                    {
                        csDecrypt.CopyTo(resultStream);
                        return resultStream.ToArray();
                    }
                }
            }
        }
    }
}
