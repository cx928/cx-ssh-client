using System.Net.Http;
using System.Text;
using System.Text.Json;
using CxSshClient.Models;

namespace CxSshClient.Services;

public class SyncConfig
{
    public string ServerUrl { get; set; } = "https://156.239.3.215:8443";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustSelfSigned { get; set; } = true;
    public int AutoSyncMinutes { get; set; } = 5;
    public string Token { get; set; } = "";
    public long TokenExpiresAt { get; set; }
    public long LastSyncAt { get; set; }
    public string LastSyncResult { get; set; } = "尚未同步";
}

/// <summary>账号同步客户端: 与 RemoteHub 服务端推拉加密密码库</summary>
public class SyncService
{
    private static string Dir => VaultService.OverrideDirectory ?? VaultService.DataDirectory;
    private static string FilePath => Path.Combine(Dir, "sync.dat");

    public SyncConfig Config { get; private set; } = new();
    public event Action<string>? StatusChanged;
    private readonly VaultService _vault;
    private readonly HttpClient _http;

    public SyncService(VaultService vault)
    {
        _vault = vault;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => Config.TrustSelfSigned
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = Encoding.UTF8.GetString(CryptoService.UnprotectLocal(File.ReadAllBytes(FilePath)));
                Config = JsonSerializer.Deserialize<SyncConfig>(json) ?? new SyncConfig();
            }
        }
        catch { Config = new SyncConfig(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(Config);
            var enc = CryptoService.ProtectLocal(Encoding.UTF8.GetBytes(json));
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, enc);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("SyncConfigSave", ex);
        }
    }

    private byte[] PayloadKey()
    {
        var salt = Encoding.UTF8.GetBytes("cx-ssh-client-sync:" + Config.Username.Trim().ToLowerInvariant());
        return CryptoService.DeriveKey(Config.Password, salt);
    }

    private string EncryptVault(VaultData v) =>
        CryptoService.EncryptAes(Encoding.UTF8.GetBytes(v.ToJson()), PayloadKey());

    private VaultData DecryptVault(string b64)
    {
        try
        {
            return VaultData.FromJson(Encoding.UTF8.GetString(CryptoService.DecryptAes(b64, PayloadKey())));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                "云端数据无法解密：请确认同步密码与首次上传时一致（若在服务端改过密码，云端旧数据将无法解密，需先清空服务端密码库）");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("云端数据格式无效，可能并非本客户端上传的密文");
        }
    }

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, object? body = null, bool auth = true)
    {
        var url = Config.ServerUrl.TrimEnd('/') + path;
        using var req = new HttpRequestMessage(method, url);
        if (auth && !string.IsNullOrEmpty(Config.Token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Config.Token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {Truncate(text, 120)}");
        return JsonDocument.Parse(text);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public async Task<bool> TestConnectionAsync()
    {
        var doc = await CallAsync(HttpMethod.Get, "/health", auth: false);
        return doc.RootElement.GetProperty("status").GetString() == "ok";
    }

    private async Task LoginAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(Config.Token) && Config.TokenExpiresAt > now + 60_000) return;
        var doc = await CallAsync(HttpMethod.Post, "/api/v1/auth/login", new
        {
            username = Config.Username,
            password = Config.Password
        }, auth: false);
        Config.Token = doc.RootElement.GetProperty("token").GetString() ?? "";
        Config.TokenExpiresAt = doc.RootElement.GetProperty("expires_at").GetInt64();
        Save();
    }

    public async Task<string> SyncNowAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Config.ServerUrl) ||
                string.IsNullOrWhiteSpace(Config.Username) || string.IsNullOrWhiteSpace(Config.Password))
                return "请先完成同步设置";

            await LoginAsync();

            VaultData remote;
            long remoteRev;
            var resp = await GetVaultRawAsync();
            if (resp is null) // 404: 服务端尚无数据
            {
                var firstPush = await PushAsync(_vault.Data, 0);
                var msg = firstPush ? "首次推送完成 ✅" : "首次推送失败";
                Finish(msg);
                return msg;
            }
            remote = DecryptVault(resp.Value.Data);
            remoteRev = resp.Value.Rev;

            var merged = VaultData.Merge(_vault.Data, remote);

            if (VaultDataJson(merged) == VaultDataJson(remote))
            {
                if (VaultDataJson(merged) != VaultDataJson(_vault.Data))
                    _vault.ReplaceAll(merged);
                Finish("已与云端一致 ✅");
                return "已与云端一致 ✅";
            }

            if (!await PushAsync(merged, remoteRev))
            {
                // 冲突: 重拉再合并重试一次
                var resp2 = await GetVaultRawAsync();
                if (resp2 is not null)
                {
                    var remote2 = DecryptVault(resp2.Value.Data);
                    var merged2 = VaultData.Merge(merged, remote2);
                    if (await PushAsync(merged2, resp2.Value.Rev))
                    {
                        _vault.ReplaceAll(merged2);
                        Finish("冲突已解决并同步 ✅");
                        return "冲突已解决并同步 ✅";
                    }
                }
                Finish("同步冲突，请稍后重试");
                return "同步冲突，请稍后重试";
            }

            _vault.ReplaceAll(merged);
            Finish("推送更新完成 ✅");
            return "推送更新完成 ✅";
        }
        catch (Exception ex)
        {
            var msg = "同步失败: " + ex.Message;
            Finish(msg);
            return msg;
        }
    }

    private async Task<(string Data, long Rev)?> GetVaultRawAsync()
    {
        try
        {
            var doc = await CallAsync(HttpMethod.Get, "/api/v1/vault");
            var root = doc.RootElement;
            var data = root.GetProperty("data").GetString() ?? "";
            var rev = root.GetProperty("rev").GetInt64();
            return (data, rev);
        }
        catch (Exception ex) when (ex.Message.Contains("HTTP 404"))
        {
            return null;
        }
    }

    private async Task<bool> PushAsync(VaultData v, long baseRev)
    {
        try
        {
            await CallAsync(HttpMethod.Put, "/api/v1/vault", new
            {
                data = EncryptVault(v),
                updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                base_rev = baseRev
            });
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("HTTP 409"))
        {
            return false;
        }
    }

    private static string VaultDataJson(VaultData v)
    {
        // 稳定化比较: 只比较内容, 忽略 UpdatedAt 抖动
        var clone = VaultData.FromJson(v.ToJson());
        clone.UpdatedAt = 0;
        return clone.ToJson();
    }

    private void Finish(string message)
    {
        Config.LastSyncAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Config.LastSyncResult = message;
        Save();
        StatusChanged?.Invoke(message);
    }
}
