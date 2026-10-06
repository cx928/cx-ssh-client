using System.Text.Json;
using FluentFTP;
using CxSshClient.Models;

namespace CxSshClient.Services;

/// <summary>FTP / FTPS 文件会话 (FluentFTP)</summary>
public class FtpSession : IDisposable
{
    private FtpClient? _client;
    public SessionInfo Info { get; }
    public bool IsConnected => _client?.IsConnected ?? false;

    public FtpSession(SessionInfo info) => Info = info;

    private string ExtraString(string key, string def)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Info.Extra);
            if (d is not null && d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? def;
        }
        catch { }
        return def;
    }

    public void Connect()
    {
        var mode = ExtraString("encryption", "none");
        var passive = ExtraString("mode", "passive");
        _client = new FtpClient(Info.Host, Info.Username, Info.Password, Info.Port)
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
        _client.Connect();
    }

    public string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        return path.Replace('\\', '/');
    }

    public string WorkingDirectory
    {
        get
        {
            try { return _client?.GetWorkingDirectory() ?? "/"; }
            catch { return "/"; }
        }
    }

    public List<FileEntry> List(string dir)
    {
        var items = _client!.GetListing(Normalize(dir));
        var list = new List<FileEntry>();
        foreach (var it in items)
        {
            if (it.Name is "." or "..") continue;
            var isDir = it.Type == FtpObjectType.Directory;
            // 某些服务器对链接/未知类型不返回类型, 用名称兜底
            list.Add(new FileEntry
            {
                Name = it.Name,
                FullPath = it.FullName,
                IsDirectory = isDir,
                Size = isDir ? 0 : it.Size,
                Modified = it.Modified == DateTime.MinValue
                    ? (it.Created == DateTime.MinValue ? default : it.Created)
                    : it.Modified
            });
        }
        return list.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void CreateDirectory(string path) => _client!.CreateDirectory(path);

    public void Delete(string path, bool isDir)
    {
        if (isDir) _client!.DeleteDirectory(path);
        else _client!.DeleteFile(path);
    }

    public void Upload(string local, string remote, IProgress<double>? progress, CancellationToken ct)
    {
        Action<FtpProgress>? callback = null;
        if (progress is not null)
            callback = p => progress.Report(p.Progress / 100.0);
        _client!.UploadFile(local, remote, FtpRemoteExists.Overwrite, true, FtpVerify.None, callback);
    }

    public void Download(string remote, string local, IProgress<double>? progress, CancellationToken ct)
    {
        Action<FtpProgress>? callback = null;
        if (progress is not null)
            callback = p => progress.Report(p.Progress / 100.0);
        _client!.DownloadFile(local, remote, FtpLocalExists.Overwrite, FtpVerify.None, callback);
    }

    public void Dispose()
    {
        try
        {
            if (_client is { IsConnected: true }) _client.Disconnect();
            _client?.Dispose();
        }
        catch { }
    }
}
