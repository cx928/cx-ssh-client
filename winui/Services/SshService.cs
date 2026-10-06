using CxSshClient.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace CxSshClient.Services;

/// <summary>SSH 会话: 交互式 Shell 流 (xterm 终端)</summary>
public class SshSession : IDisposable
{
    private SshClient? _client;
    private ShellStream? _shell;

    public event Action<byte[]>? Output;
    public event Action<string>? Status;
    public event Action? Closed;

    public bool IsConnected => _client?.IsConnected ?? false;
    public SessionInfo Info { get; }

    public SshSession(SessionInfo info) => Info = info;

    public void Connect(uint cols = 100, uint rows = 30)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(Info.PrivateKeyPath) && File.Exists(Info.PrivateKeyPath))
        {
            try
            {
                var key = string.IsNullOrEmpty(Info.Password)
                    ? new PrivateKeyFile(Info.PrivateKeyPath)
                    : new PrivateKeyFile(Info.PrivateKeyPath, Info.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(Info.Username, key));
            }
            catch (Exception ex)
            {
                Status?.Invoke($"私钥加载失败: {ex.Message}");
            }
        }
        methods.Add(new PasswordAuthenticationMethod(Info.Username, Info.Password));

        var ci = new ConnectionInfo(Info.Host, Info.Port, Info.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _client = new SshClient(ci);
        _client.Connect();

        _shell = _client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 1 << 20);
        _shell.DataReceived += (_, e) => Output?.Invoke(e.Data);
        _shell.ErrorOccurred += (_, e) => Status?.Invoke(e.Exception?.Message ?? "终端错误");
        if (_shell is not null)
        {
            _shell.Closed += (_, _) =>
            {
                if (!IsConnected) Closed?.Invoke();
            };
        }
    }

    public void Write(byte[] data)
    {
        try
        {
            if (_shell is { CanWrite: true })
            {
                _shell.Write(data);
                _shell.Flush();
            }
        }
        catch { /* 会话已断开 */ }
    }

    /// <summary>
    /// 终端窗口大小变化。SSH.NET 的 ShellStream 未公开该能力,
    /// 这里通过反射调用内部 ChannelSession.SendWindowChangeRequest(Public)。
    /// </summary>
    public void Resize(uint cols, uint rows)
    {
        try
        {
            if (_shell is null) return;
            var field = typeof(ShellStream).GetField("_channel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var channel = field?.GetValue(_shell);
            if (channel is null) return;
            var method = channel.GetType().GetMethod("SendWindowChangeRequest",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) }, null);
            method?.Invoke(channel, new object[] { cols, rows, 0u, 0u });
        }
        catch { }
    }

    public void Dispose()
    {
        try { _shell?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}

/// <summary>SFTP 文件会话</summary>
public class SftpSession : IDisposable
{
    private SftpClient? _client;
    public SessionInfo Info { get; }
    public bool IsConnected => _client?.IsConnected ?? false;

    public SftpSession(SessionInfo info) => Info = info;

    public void Connect()
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(Info.PrivateKeyPath) && File.Exists(Info.PrivateKeyPath))
        {
            try
            {
                var key = string.IsNullOrEmpty(Info.Password)
                    ? new PrivateKeyFile(Info.PrivateKeyPath)
                    : new PrivateKeyFile(Info.PrivateKeyPath, Info.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(Info.Username, key));
            }
            catch { }
        }
        methods.Add(new PasswordAuthenticationMethod(Info.Username, Info.Password));
        var ci = new ConnectionInfo(Info.Host, Info.Port, Info.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _client = new SftpClient(ci);
        _client.Connect();
    }

    public string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ".";
        return path.Replace('\\', '/');
    }

    public List<ISftpFile> List(string dir)
    {
        var d = Normalize(dir);
        return _client!.ListDirectory(d).ToList();
    }

    public string WorkingDirectory
    {
        get
        {
            try { return _client?.WorkingDirectory ?? "."; }
            catch { return "."; }
        }
    }

    public bool IsDir(string path) => _client!.GetAttributes(path).IsDirectory;

    public void CreateDirectory(string path) => _client!.CreateDirectory(path);

    public void DeleteFile(string path) => _client!.DeleteFile(path);

    public void DeleteDirectory(string path) => _client!.DeleteDirectory(path);

    public void Upload(string local, string remote, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var src = File.OpenRead(local);
        using var dst = _client!.OpenWrite(remote);
        var total = src.Length;
        var sent = 0L;
        var buf = new byte[256 * 1024];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, n);
            sent += n;
            progress?.Report((double)sent / total);
        }
    }

    public void Download(string remote, string local, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var src = _client!.OpenRead(remote);
        using var dst = File.Create(local);
        var total = src.Length;
        var got = 0L;
        var buf = new byte[256 * 1024];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, n);
            got += n;
            progress?.Report(total > 0 ? (double)got / total : 0);
        }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
    }
}
