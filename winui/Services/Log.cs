namespace CxSshClient.Services;

/// <summary>轻量日志: 记录到数据目录下的 crash.log (目录不可写时忽略)</summary>
public static class Log
{
    private static readonly object _lock = new();

    public static string LogPath => Path.Combine(VaultService.DataDirectory, "crash.log");

    public static void Write(string source, Exception? ex)
    {
        Write(source, ex?.ToString() ?? "(null)");
    }

    public static void Write(string source, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(VaultService.DataDirectory);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {message}\r\n");
            }
        }
        catch
        {
            // 日志本身失败时静默忽略, 绝不影响主流程
        }
    }
}
