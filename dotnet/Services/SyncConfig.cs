using System.Text.Json;
using System.Text.Json.Serialization;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 同步参数持久化：%LOCALAPPDATA%\cx-ssh-client\sync.json
/// 口令用 DPAPI(CurrentUser) 单独加密后再 base64 存放，避免明文写盘。
/// 只影响 CLI；图形版有它自己的设置存储，两者互不干扰。
/// </summary>
public class SyncConfig
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    /// <summary>DPAPI 加密后的 base64（不是明文口令）</summary>
    [JsonPropertyName("passwordProtected")]
    public string PasswordProtected { get; set; } = "";

    [JsonPropertyName("trustSelfSigned")]
    public bool TrustSelfSigned { get; set; }

    private static string PathFor(string dataDirectory) =>
        Path.Combine(dataDirectory, "sync.json");

    /// <summary>读取；文件不存在或损坏时返回 null。</summary>
    public static SyncConfig? Load(string dataDirectory)
    {
        try
        {
            var path = PathFor(dataDirectory);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathFor(dataDirectory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public void SetPassword(string plain)
    {
        if (string.IsNullOrEmpty(plain)) { PasswordProtected = ""; return; }
        var enc = CryptoService.ProtectLocal(System.Text.Encoding.UTF8.GetBytes(plain));
        PasswordProtected = Convert.ToBase64String(enc);
    }

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(PasswordProtected)) return "";
        try
        {
            var raw = Convert.FromBase64String(PasswordProtected);
            return System.Text.Encoding.UTF8.GetString(CryptoService.UnprotectLocal(raw));
        }
        catch (Exception)
        {
            return "";
        }
    }
}
