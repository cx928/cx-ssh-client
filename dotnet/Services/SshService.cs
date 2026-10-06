using System.Diagnostics;
using System.Text;
using CxSshClient.Cli.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CxSshClient.Cli.Services;

/// <summary>远程命令的执行结果。</summary>
public class CommandResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    /// <summary>为 true 表示服务端未返回退出码（例如会话被强制断开）。</summary>
    public bool ExitStatusUnknown { get; init; }
}

/// <summary>非交互式 SSH：执行单条命令并回收输出与退出码（SSH.NET SshClient.RunCommand）。</summary>
public static class SshCommandRunner
{
    /// <summary>执行命令。timeout 为空表示不限制（交由调用方 Ctrl+C 中断）。</summary>
    public static CommandResult Run(SessionInfo info, string command, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new CliException("命令内容为空。用法：cx exec <名称|user@host> \"命令\"");

        var sw = Stopwatch.StartNew();
        var client = new SshClient(SshConnectionFactory.Create(info));
        try
        {
            Connect(client);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw SshConnectionFactory.Translate(ex, info, "SSH 连接");
        }

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromSeconds(60);
        try
        {
            var stdout = cmd.Execute();
            sw.Stop();

            return new CommandResult
            {
                ExitCode = cmd.ExitStatus,
                StdOut = stdout ?? "",
                StdErr = cmd.Error ?? "",
                Elapsed = sw.Elapsed,
                // SSH.NET 在服务端未返回退出状态时取 -1
                ExitStatusUnknown = cmd.ExitStatus < 0
            };
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new CliException($"命令执行超时（{cmd.CommandTimeout.TotalSeconds:0} 秒）：{ex.Message}");
        }
        catch (SshException ex)
        {
            throw SshConnectionFactory.Translate(ex, info, "命令执行");
        }
        finally
        {
            try { if (client.IsConnected) client.Disconnect(); } catch { }
            client.Dispose();
        }
    }

    /// <summary>
    /// 建立连接。SSH.NET 的 ConnectionInfo.Timeout 已经限制了握手与认证的总时长
    /// （本程序设为 15 秒），因此这里直接调用 Connect，无需再套一层超时。
    /// </summary>
    internal static void Connect(BaseClient client) => client.Connect();

    /// <summary>
    /// 交互式 SSH：直接调用系统自带的 ssh.exe，把当前终端交给它。
    /// 这样键盘交互、ssh-agent、known_hosts、~/.ssh/config 等行为与原生一致。
    /// </summary>
    public static int RunInteractive(SessionInfo info, bool batchMode = false)
    {
        var host = SshConnectionFactory.NormalizeHost(info.Host);
        var port = info.Port <= 0 ? 22 : info.Port;
        var target = string.IsNullOrWhiteSpace(info.Username) ? host : $"{info.Username}@{host}";

        var args = new List<string>();
        if (port != 22) { args.Add("-p"); args.Add(port.ToString()); }
        if (batchMode) args.Add("-o BatchMode=yes");
        args.Add(target);

        var exe = ResolveSshExe();
        return RunChild(exe, args);
    }

    /// <summary>优先使用 System32 的 OpenSSH 客户端，避免 PATH 上有伪造的 ssh.exe。</summary>
    public static string ResolveSshExe()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system32, "OpenSSH", "ssh.exe");
        if (File.Exists(candidate)) return candidate;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "ssh.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { /* PATH 中含有非法字符的目录，跳过 */ }
        }

        throw new CliException(
            "未找到系统 ssh.exe。请安装「OpenSSH 客户端」可选功能：" +
            "设置 → 系统 → 可选功能 → 添加功能 → OpenSSH 客户端。");
    }

    /// <summary>启动子进程并把标准输入输出直接接到当前终端（支持交互）。</summary>
    internal static int RunChild(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) throw new CliException($"无法启动进程：{exe}");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CliException($"无法启动 {Path.GetFileName(exe)}：{ex.Message}");
        }
    }
}

/// <summary>SFTP 文件会话（SSH.NET SftpClient）：列出目录、下载、上传。</summary>
public sealed class SftpService : IDisposable
{
    private readonly SftpClient _client;
    private readonly SessionInfo _info;

    public SftpService(SessionInfo info)
    {
        _info = info;
        _client = new SftpClient(SshConnectionFactory.Create(info));
        try
        {
            SshCommandRunner.Connect(_client);
        }
        catch (Exception ex)
        {
            _client.Dispose();
            throw SshConnectionFactory.Translate(ex, info, "SFTP 连接");
        }
    }

    /// <summary>本地 Windows 路径习惯的反斜杠统一转成远端 POSIX 分隔符。</summary>
    public static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/');

    public string WorkingDirectory
    {
        get
        {
            try { return _client.WorkingDirectory ?? "."; }
            catch (SshException) { return "."; }
        }
    }

    public IReadOnlyList<SftpEntry> List(string directory)
    {
        var dir = Normalize(directory);
        try
        {
            return _client.ListDirectory(dir)
                .Where(f => f.Name is not ("." or ".."))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new SftpEntry(
                    f.Name,
                    f.FullName,
                    f.IsDirectory,
                    f.IsDirectory ? 0 : f.Length,
                    f.LastWriteTimeUtc))
                .ToList();
        }
        catch (SshException ex)
        {
            throw new CliException($"列目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>下载远端文件到本地。返回实际写入的字节数。</summary>
    public long Download(string remote, string local, Action<long, long>? progress = null)
    {
        var remotePath = Normalize(remote);
        var localPath = Path.GetFullPath(local);
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            using var src = _client.OpenRead(remotePath);
            var total = src.Length;
            using var dst = File.Create(localPath);
            var buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, n);
                done += n;
                progress?.Invoke(done, total);
            }
            return done;
        }
        catch (SshException ex)
        {
            throw new CliException($"下载失败（{remotePath} → {localPath}）：{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new CliException($"写入本地文件失败（{localPath}）：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CliException($"没有权限写入（{localPath}）：{ex.Message}");
        }
    }

    /// <summary>上传本地文件到远端。返回实际发送的字节数。</summary>
    public long Upload(string local, string remote, Action<long, long>? progress = null)
    {
        var localPath = Path.GetFullPath(local);
        if (!File.Exists(localPath)) throw new CliException($"本地文件不存在：{localPath}");
        var remotePath = Normalize(remote);

        try
        {
            using var src = File.OpenRead(localPath);
            var total = src.Length;
            using var dst = _client.OpenWrite(remotePath);
            var buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, n);
                done += n;
                progress?.Invoke(done, total);
            }
            return done;
        }
        catch (SshException ex)
        {
            throw new CliException($"上传失败（{localPath} → {remotePath}）：{ex.Message}");
        }
    }

    public void Dispose()
    {
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        _client.Dispose();
    }
}

/// <summary>SFTP 目录项（避免把 SSH.NET 的 ISftpFile 泄漏到命令层）。</summary>
public record SftpEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime ModifiedUtc)
{
    public string SizeText => IsDirectory ? "<DIR>" : FormatSize(Size);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }
}

/// <summary>把一行文本按指定编码写文件（供 exec 输出重定向使用，保留 UTF-8 无 BOM）。</summary>
public static class TextFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
}
