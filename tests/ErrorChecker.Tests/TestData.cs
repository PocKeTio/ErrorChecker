using System.Security.Cryptography;

// Aides communes aux deux cibles (net6.0 et net48, qui n'a pas les raccourcis récents).
internal static class TestData
{
    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }

    public static void Write(this Stream stream, byte[] data) => stream.Write(data, 0, data.Length);
}
