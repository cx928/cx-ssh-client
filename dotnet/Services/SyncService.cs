using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>同步结果，用于打印与退出码判定。</summary>
public class SyncResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public long Rev { get; set; }

    /// <summary>若同步把远端数据合并了下来，这里是最新的密码库；调用方负责保存。</summary>
    public VaultData? Merged { get; set; }
}

/// <summary>
/// 账号同步客户端：与自建 RemoteHub 服务端推拉加密密码库。
/// 协议与图形版 Services/SyncService.cs 一致：
///   POST /api/v1/auth/login {username,password} -> {token,expires_at}
///   GET  /api/v1/vault -> {data,rev,updated_at}（404 = 云端无数据）
///   PUT  /api/v1/vault {data,updated_at,base_rev} -> {rev}（版本冲突 HTTP 409）
/// 鉴权：Authorization: Bearer &lt;token&gt;
/// data = base64(AES-256-GCM(iv|tag|cipher))，密钥 = PBKDF2-SHA256(同步密码, "novaremote-sync:"+用户名小写, 120000, 32)
/// </summary>
public class SyncService : IDisposable
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustSelfSigned { get; set; }

    public string Token { get; private set; } = "";
    public long TokenExpiresAt { get; private set; }

    private readonly HttpClient _http;
    private readonly Action<string>? _trace;

    public SyncService(bool trustSelfSigned, Action<string>? trace = null)
    {
        TrustSelfSigned = trustSelfSigned;
        _trace = trace;
        var handler = new HttpClientHandler
        {
            // 自签名证书：信任开关打开时接受任意服务端证书
            ServerCertificateCustomValidationCallback =
                (_, _, _, _) => TrustSelfSigned
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private byte[] PayloadKey() => CryptoService.DeriveSyncKey(Username, Password);

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
            throw new CliException(
                "云端数据无法解密：请确认同步密码与首次上传时一致" +
                "（若在服务端改过该账号密码，云端旧数据将无法解密，需要先清空服务端密码库）");
        }
        catch (FormatException)
        {
            throw new CliException("云端 data 字段不是合法 base64，可能并非本客户端上传的密文。");
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> CallAsync(
        HttpMethod method, string path, object? body = null, bool auth = true)
    {
        var url = ServerUrl.TrimEnd('/') + path;
        using var req = new HttpRequestMessage(method, url);
        if (auth && !string.IsNullOrEmpty(Token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json.Options), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        _trace?.Invoke($"{method.Method} {url} -> HTTP {(int)resp.StatusCode}");
        return (resp.StatusCode, text);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>登录并缓存 token（未过期则复用）。</summary>
    public async Task LoginAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(Token) && TokenExpiresAt > now + 60_000) return;

        var (status, text) = await CallAsync(HttpMethod.Post, "/api/v1/auth/login", new
        {
            username = Username,
            password = Password
        }, auth: false);

        if (status == HttpStatusCode.Unauthorized)
            throw new CliException("登录失败：用户名或密码错误（HTTP 401）。");

        if (status != HttpStatusCode.OK)
            throw new CliException($"登录失败：HTTP {(int)status} {Describe(text)}");

        try
        {
            using var doc = JsonDocument.Parse(text);
            Token = doc.RootElement.GetProperty("token").GetString() ?? "";
            TokenExpiresAt = doc.RootElement.TryGetProperty("expires_at", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64()
                : now + 3_600_000;
        }
        catch (Exception ex)
        {
            throw new CliException($"登录响应解析失败：{ex.Message}");
        }

        if (string.IsNullOrEmpty(Token))
            throw new CliException("登录响应中没有 token 字段。");
    }

    private async Task<(string Data, long Rev)?> GetVaultRawAsync()
    {
        var (status, text) = await CallAsync(HttpMethod.Get, "/api/v1/vault");
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new CliException($"拉取云端密码库失败：HTTP {(int)status} {Describe(text)}");

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            var rev = root.TryGetProperty("rev", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
            return (data, rev);
        }
        catch (Exception ex)
        {
            throw new CliException($"云端响应解析失败：{ex.Message}");
        }
    }

    /// <summary>推送；返回 false 表示版本冲突（HTTP 409）。</summary>
    private async Task<bool> PushAsync(VaultData v, long baseRev)
    {
        var (status, text) = await CallAsync(HttpMethod.Put, "/api/v1/vault", new
        {
            data = EncryptVault(v),
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            base_rev = baseRev
        });

        if (status == HttpStatusCode.Conflict) return false;
        if (status != HttpStatusCode.OK)
            throw new CliException($"推送云端失败：HTTP {(int)status} {Describe(text)}");
        return true;
    }

    private static string Describe(string body)
    {
        var t = (body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length == 0 ? "" : Truncate(t, 160);
    }

    /// <summary>与图形版一致的内容比较：忽略 updatedAt 抖动。</summary>
    private static string StableJson(VaultData v)
    {
        var clone = VaultData.FromJson(v.ToJson());
        clone.UpdatedAt = 0;
        return clone.ToJson();
    }

    /// <summary>执行一次完整同步（登录 → 拉取 → 合并 → 推送；409 时重拉重推一次）。</summary>
    public async Task<SyncResult> SyncAsync(VaultData local)
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)) throw new CliException("缺少 --server 参数（同步服务器地址）。");
        if (string.IsNullOrWhiteSpace(Username)) throw new CliException("缺少 --user 参数（同步账号）。");
        if (string.IsNullOrWhiteSpace(Password)) throw new CliException("缺少 --password 参数（同步密码）。");

        await LoginAsync();

        var remote = await GetVaultRawAsync();
        if (remote is null)
        {
            // 云端尚无数据：首次推送
            var ok = await PushAsync(local, 0);
            if (!ok)
            {
                // 极端情况：推送瞬间别人先建了库
                var again = await GetVaultRawAsync();
                if (again is not null)
                {
                    var remoteData0 = DecryptVault(again.Value.Data);
                    var merged0 = VaultData.Merge(local, remoteData0);
                    if (await PushAsync(merged0, again.Value.Rev))
                        return new SyncResult { Ok = true, Merged = merged0, Rev = again.Value.Rev + 1, Message = "首次推送时检测到云端新数据，已合并后推送" };
                }
                return new SyncResult { Ok = false, Message = "首次推送遇到版本冲突，请稍后重试" };
            }
            return new SyncResult { Ok = true, Merged = local, Rev = 1, Message = "首次推送完成" };
        }

        var remoteVault = DecryptVault(remote.Value.Data);
        var remoteRev = remote.Value.Rev;
        var merged = VaultData.Merge(local, remoteVault);

        if (StableJson(merged) == StableJson(remoteVault))
        {
            // 云端已包含本地全部内容
            return new SyncResult
            {
                Ok = true,
                Merged = merged,
                Rev = remoteRev,
                Message = StableJson(merged) == StableJson(local) ? "已与云端一致" : "已从云端拉取更新"
            };
        }

        if (await PushAsync(merged, remoteRev))
            return new SyncResult { Ok = true, Merged = merged, Rev = remoteRev + 1, Message = "推送更新完成" };

        // 409：重新拉取 → 再合并 → 重推一次
        var resp2 = await GetVaultRawAsync();
        if (resp2 is not null)
        {
            var remote2 = DecryptVault(resp2.Value.Data);
            var merged2 = VaultData.Merge(merged, remote2);
            if (await PushAsync(merged2, resp2.Value.Rev))
                return new SyncResult { Ok = true, Merged = merged2, Rev = resp2.Value.Rev + 1, Message = "检测到版本冲突，已重新合并并同步成功" };
        }

        return new SyncResult { Ok = false, Message = "同步冲突未能自动解决，请稍后重试" };
    }

    public void Dispose() => _http.Dispose();
}
