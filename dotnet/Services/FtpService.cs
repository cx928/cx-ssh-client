using System.Text.Json;
using CxSshClient.Cli.Models;
using FluentFTP;

namespace CxSshClient.Cli.Services;

/// <summary>FTP 目录项（CLI 侧的精简表示）。</summary>
public record FtpEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)
{
    public string SizeText => IsDirectory ? "<DIR>" : SftpEntry.FormatSize(Size);
}

/// <summary>
/// FTP / FTPS 会话（FluentFTP）。
/// 加密方式与被动模式从会话的 Extra JSON 读取，键名与图形版 Services/FtpService.cs 完全一致：
///   {"encryption":"none|explicit|implicit","mode":"passive|active"}
/// 注意 FluentFTP 50.x 的进度回调类型是 Action&lt;FtpProgress&gt;。
/// </summary>
public sealed class FtpService : IDisposable
{
    private readonly FtpClient _client;
    private readonly SessionInfo _info;

    public FtpService(SessionInfo info)
    {
        _info = info;
        if (string.IsNullOrWhiteSpace(info.Host))
            throw new CliException("会话缺少主机地址（Host）。");

        var mode = ExtraString("encryption", "none");
        var passive = ExtraString("mode", "passive");

        _client = new FtpClient(SshConnectionFactory.NormalizeHost(info.Host), info.Username, info.Password,
            info.Port <= 0 ? 21 : info.Port)
        {
            Config =
            {
                EncryptionMode = mode switch
                {
                    "explicit" => FtpEncryptionMode.Explicit,
                    "implicit" => FtpEncryptionMode.Implicit,
                    _ => FtpEncryptionMode.None
                },
                DataConnectionType = passive == "active"
                    ? FtpDataConnectionType.AutoActive
                    : FtpDataConnectionType.AutoPassive,
                ValidateAnyCertificate = true,
                ConnectTimeout = 15000,
                ReadTimeout = 30000,
                DataConnectionConnectTimeout = 15000,
                ListingParser = FtpParser.Auto,
                LogToConsole = false
            }
        };

        try
        {
            _client.Connect();
        }
        catch (Exception ex)
        {
            _client.Dispose();
            throw new CliException(
                $"FTP 连接失败（{_info.Host}:{_info.Port}，加密 {mode}）：{ex.Message}", ex);
        }
    }

    private string ExtraString(string key, string fallback)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_info.Extra);
            if (dict is not null && dict.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? fallback;
        }
        catch (JsonException) { /* Extra 不是合法 JSON 时按默认配置处理 */ }
        return fallback;
    }

    public static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');

    public string WorkingDirectory
    {
        get
        {
            try { return _client.GetWorkingDirectory() ?? "/"; }
            catch (Exception) { return "/"; }
        }
    }

    public IReadOnlyList<FtpEntry> List(string directory)
    {
        var dir = Normalize(directory);
        try
        {
            return _client.GetListing(dir)
                .Where(i => i.Name is not ("." or ".."))
                .Select(i =>
                {
                    var isDir = i.Type == FtpObjectType.Directory;
                    return new FtpEntry(
                        i.Name,
                        i.FullName,
                        isDir,
                        isDir ? 0 : i.Size,
                        i.Modified == DateTime.MinValue ? i.Created : i.Modified);
                })
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new CliException($"FTP 列目录失败（{dir}）：{ex.Message}", ex);
        }
    }

    /// <summary>下载远端文件到本地。progress 回调形如 (已传输字节, 总字节)。</summary>
    public long Download(string remote, string local, Action<long, long>? progress = null)
    {
        var localPath = Path.GetFullPath(local);
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FluentFTP 50.x：进度回调类型是 Action<FtpProgress>（Progress 为 0~100 百分比）
        Action<FtpProgress>? callback = null;
        if (progress is not null)
            callback = p => progress(p.TransferredBytes, TotalFrom(p));

        var status = _client.DownloadFile(localPath, Normalize(remote), FtpLocalExists.Overwrite, FtpVerify.None, callback);
        if (status is FtpStatus.Failed)
            throw new CliException($"FTP 下载失败（{Normalize(remote)} → {localPath}）。");
        return new FileInfo(localPath).Length;
    }

    /// <summary>上传本地文件到远端。</summary>
    public long Upload(string local, string remote, Action<long, long>? progress = null)
    {
        var localPath = Path.GetFullPath(local);
        if (!File.Exists(localPath)) throw new CliException($"本地文件不存在：{localPath}");

        Action<FtpProgress>? callback = null;
        if (progress is not null)
            callback = p => progress(p.TransferredBytes, TotalFrom(p));

        var status = _client.UploadFile(localPath, Normalize(remote), FtpRemoteExists.Overwrite, true, FtpVerify.None, callback);
        if (status is FtpStatus.Failed)
            throw new CliException($"FTP 上传失败（{localPath} → {Normalize(remote)}）。");
        return new FileInfo(localPath).Length;
    }

    /// <summary>
    /// FtpProgress 不直接暴露总字节数，这里用「已传输 / 百分比」反推；
    /// 百分比为 0 时退化为已传输字节（调用方通常只用于显示进度条）。
    /// </summary>
    private static long TotalFrom(FtpProgress p)
    {
        if (p.Progress > 0.001 && p.TransferredBytes >= 0)
            return (long)Math.Round(p.TransferredBytes / (p.Progress / 100.0));
        return p.TransferredBytes < 0 ? 0 : p.TransferredBytes;
    }

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception) { /* 断开失败不影响退出 */ }
        _client.Dispose();
    }
}
