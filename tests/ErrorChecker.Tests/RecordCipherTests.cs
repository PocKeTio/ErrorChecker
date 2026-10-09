using System.Text;
using ErrorChecker.Core;
using Xunit;

// Vecteurs calculés indépendamment avec OpenSSL :
//   enc = HMAC-SHA256(clé, "ErrorChecker/enc"), mac = HMAC-SHA256(clé, "ErrorChecker/mac")
//   chiffré = openssl enc -aes-256-ctr -K enc -iv nonce||00000000
//   tag = 16 premiers octets de HMAC-SHA256(mac, nonce || chiffré)
public class RecordCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Hex("010000000500000000000000");
    private static readonly byte[] Plain = Encoding.ASCII.GetBytes("Bonjour, ceci est un test CTR+HMAC !!!!");
    private static readonly byte[] Expected = Hex("03cc2db3c7adf1eee6880f821253049acaeda43406c5440f47a3d8ee96d9382bf8aed658b2d608"
                                                + "ad84b114b26bfce54f2f4b5613515eb2");

    private static byte[] Hex(string hex) => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(2 * i, 2), 16)).ToArray();

    [Fact]
    public void Matches_reference_aes_ctr_and_hmac_sha256()
    {
        using var cipher = new RecordCipher(Key);
        var output = new byte[Plain.Length + RecordCipher.TagSize];
        cipher.Encrypt(Nonce, Plain, output, 0);
        Assert.Equal(Expected, output);
    }

    [Fact]
    public void Decrypts_reference_and_rejects_any_altered_byte()
    {
        using var cipher = new RecordCipher(Key);
        Assert.True(cipher.TryDecrypt(Nonce, Expected, 0, Expected.Length, out var plain));
        Assert.Equal(Plain, plain);

        for (int i = 0; i < Expected.Length; i++)
        {
            var altered = (byte[])Expected.Clone();
            altered[i] ^= 0x01;
            Assert.False(cipher.TryDecrypt(Nonce, altered, 0, altered.Length, out _));
        }
        var otherNonce = (byte[])Nonce.Clone();
        otherNonce[4] = 6;   // numéro d'ordre différent : rejeu détecté
        Assert.False(cipher.TryDecrypt(otherNonce, Expected, 0, Expected.Length, out _));
    }
}
