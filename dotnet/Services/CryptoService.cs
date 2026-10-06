using System.Security.Cryptography;
using System.Text;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 加密工具：DPAPI / AES-256-GCM / PBKDF2。
/// 常量与算法参数必须与图形版 Services/CryptoService.cs 保持一致，否则两份数据无法互通。
/// </summary>
public static class CryptoService
{
    public const int Pbkdf2Iterations = 120_000;
    public const int KeySizeBytes = 32;
    public const int IvSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>PBKDF2-SHA256 派生 32 字节密钥（与图形版 DeriveKey 相同：120000 次）</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations = Pbkdf2Iterations)
    {
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeySizeBytes);
    }

    /// <summary>AES-256-GCM 加密，返回 base64(iv | tag | cipher)</summary>
    public static string EncryptAes(byte[] plain, byte[] key)
    {
        var iv = RandomBytes(IvSizeBytes);
        var tag = new byte[TagSizeBytes];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, TagSizeBytes))
            aes.Encrypt(iv, plain, cipher, tag);

        var buf = new byte[IvSizeBytes + TagSizeBytes + cipher.Length];
        Buffer.BlockCopy(iv, 0, buf, 0, IvSizeBytes);
        Buffer.BlockCopy(tag, 0, buf, IvSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipher, 0, buf, IvSizeBytes + TagSizeBytes, cipher.Length);
        return Convert.ToBase64String(buf);
    }

    /// <summary>解密 base64(iv | tag | cipher)</summary>
    public static byte[] DecryptAes(string base64, byte[] key)
    {
        var buf = Convert.FromBase64String(base64);
        if (buf.Length < IvSizeBytes + TagSizeBytes)
            throw new CryptographicException("密文长度不足，数据已损坏");
        var iv = buf.AsSpan(0, IvSizeBytes).ToArray();
        var tag = buf.AsSpan(IvSizeBytes, TagSizeBytes).ToArray();
        var cipher = buf.AsSpan(IvSizeBytes + TagSizeBytes).ToArray();
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSizeBytes))
            aes.Decrypt(iv, cipher, tag, plain);
        return plain;
    }

    /// <summary>同步载荷密钥：salt = UTF8("novaremote-sync:" + 用户名小写)。前缀是历史常量，不可更改。</summary>
    public static byte[] DeriveSyncKey(string username, string password)
    {
        var salt = Encoding.UTF8.GetBytes("novaremote-sync:" + username.Trim().ToLowerInvariant());
        return DeriveKey(password, salt);
    }

    // ---------------- DPAPI（仅当前 Windows 用户可解密） ----------------

    public static byte[] ProtectLocal(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public static byte[] UnprotectLocal(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
