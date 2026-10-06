using CxSshClient.Models;

namespace CxSshClient.Services;

/// <summary>文件会话统一接口 (SFTP / FTP)</summary>
public interface IFileSession : IDisposable
{
    void Connect();
    string InitialPath { get; }
    List<FileEntry> List(string path);
    void CreateDirectory(string path);
    void Delete(string path, bool isDir);
    void Upload(string local, string remote, IProgress<double>? progress, CancellationToken ct);
    void Download(string remote, string local, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>SFTP 适配器</summary>
public class SftpFileSession : IFileSession
{
    private readonly SftpSession _inner;

    public SftpFileSession(SessionInfo info) => _inner = new SftpSession(info);

    public void Connect() => _inner.Connect();

    public string InitialPath => _inner.WorkingDirectory;

    public List<FileEntry> List(string path)
    {
        var list = new List<FileEntry>();
        foreach (var f in _inner.List(path))
        {
            var name = f.Name;
            if (name is "." or "..") continue;
            list.Add(new FileEntry
            {
                Name = name,
                FullPath = f.FullName,
                IsDirectory = f.IsDirectory,
                Size = f.IsDirectory ? 0 : f.Length,
                Modified = f.LastWriteTime
            });
        }
        return list
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void Delete(string path, bool isDir)
    {
        if (isDir) _inner.DeleteDirectory(path);
        else _inner.DeleteFile(path);
    }

    public void Upload(string local, string remote, IProgress<double>? progress, CancellationToken ct) =>
        _inner.Upload(local, remote, progress, ct);

    public void Download(string remote, string local, IProgress<double>? progress, CancellationToken ct) =>
        _inner.Download(remote, local, progress, ct);

    public void Dispose() => _inner.Dispose();
}

/// <summary>FTP / FTPS 适配器</summary>
public class FtpFileSession : IFileSession
{
    private readonly FtpSession _inner;

    public FtpFileSession(SessionInfo info) => _inner = new FtpSession(info);

    public void Connect() => _inner.Connect();

    public string InitialPath => _inner.WorkingDirectory;

    public List<FileEntry> List(string path) => _inner.List(path);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void Delete(string path, bool isDir) => _inner.Delete(path, isDir);

    public void Upload(string local, string remote, IProgress<double>? progress, CancellationToken ct) =>
        _inner.Upload(local, remote, progress, ct);

    public void Download(string remote, string local, IProgress<double>? progress, CancellationToken ct) =>
        _inner.Download(remote, local, progress, ct);

    public void Dispose() => _inner.Dispose();
}
