using System.Security.Cryptography;
using System.Text;

namespace CxSshClient.Services;

/// <summary>AES-256-GCM / PBKDF2 / DPAPI 加密工具</summary>
public static class CryptoService
{
    public static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>PBKDF2 派生 32 字节密钥</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations = 120_000)
    {
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    /// <summary>AES-256-GCM 加密, 返回 base64(iv|tag|cipher)</summary>
    public static string EncryptAes(byte[] plain, byte[] key)
    {
        var iv = RandomBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, plain, cipher, tag);
        var buf = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(iv, 0, buf, 0, 12);
        Buffer.BlockCopy(tag, 0, buf, 12, 16);
        Buffer.BlockCopy(cipher, 0, buf, 28, cipher.Length);
        return Convert.ToBase64String(buf);
    }

    public static byte[] DecryptAes(string base64, byte[] key)
    {
        var buf = Convert.FromBase64String(base64);
        if (buf.Length < 28) throw new CryptographicException("数据损坏");
        var iv = buf.AsSpan(0, 12).ToArray();
        var tag = buf.AsSpan(12, 16).ToArray();
        var cipher = buf.AsSpan(28).ToArray();
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(iv, cipher, tag, plain);
        return plain;
    }

    // ---------- DPAPI (当前用户本机保护) ----------
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CxSshClient.Local.2025");

    public static byte[] ProtectLocal(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] UnprotectLocal(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
