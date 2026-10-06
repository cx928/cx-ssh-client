using CxSshClient.Cli.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CxSshClient.Cli.Services;

/// <summary>
/// SSH.NET 连接参数构造：私钥优先、口令兜底，与图形版 Services/SshService.cs 的认证顺序一致。
/// 超时统一 15 秒，避免 CLI 在不可达主机上长时间挂起。
/// </summary>
public static class SshConnectionFactory
{
    /// <summary>握手 + 认证 + 通道的总超时（SSH.NET 的 ConnectionInfo.Timeout）。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>从会话记录构造 ConnectionInfo。私钥存在且可读时优先使用，口令方式始终追加在最后。</summary>
    public static ConnectionInfo Create(SessionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Host))
            throw new CliException("会话缺少主机地址（Host），请用 `cx add --host <地址>` 补全。");
        if (string.IsNullOrWhiteSpace(info.Username))
            throw new CliException($"会话「{info.DisplayTitle}」缺少用户名，请用 `cx add --user <用户名>` 补全。");

        var host = NormalizeHost(info.Host);
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(info.PrivateKeyPath))
        {
            if (!File.Exists(info.PrivateKeyPath))
            {
                throw new CliException($"私钥文件不存在：{info.PrivateKeyPath}");
            }
            try
            {
                var key = string.IsNullOrEmpty(info.Password)
                    ? new PrivateKeyFile(info.PrivateKeyPath)
                    : new PrivateKeyFile(info.PrivateKeyPath, info.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(info.Username, key));
            }
            catch (Exception ex)
            {
                throw new CliException($"私钥加载失败（{info.PrivateKeyPath}）：{ex.Message}");
            }
        }

        methods.Add(new PasswordAuthenticationMethod(info.Username, info.Password ?? ""));

        return new ConnectionInfo(host, info.Port <= 0 ? 22 : info.Port, info.Username, methods.ToArray())
        {
            Timeout = Timeout
        };
    }

    /// <summary>把常见异常翻译成中文的可操作提示。</summary>
    public static CliException Translate(Exception ex, SessionInfo info, string action)
    {
        var target = $"{NormalizeHost(info.Host)}:{info.Port}";
        // 逐层剥掉 AggregateException / TargetInvocationException 之类的包装
        var root = ex;
        while (root is AggregateException { InnerException: not null } agg)
            root = agg.InnerException!;

        var message = (root.Message ?? "").Trim();
        var denied = message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

        return root switch
        {
            SshAuthenticationException or _ when denied =>
                new CliException(
                    $"{action}失败：认证被拒绝（{target}，用户 {info.Username}）。" +
                    "请检查密码 / 私钥是否正确，以及服务端是否允许口令登录。", ex),
            SshOperationTimeoutException =>
                new CliException(
                    $"{action}超时（{target}，用户 {info.Username}）。可能原因：口令或私钥不正确、" +
                    "服务端不允许该认证方式、或网络在认证阶段丢包。请先确认凭据，再检查服务端 sshd 配置。", ex),
            SshConnectionException sce =>
                new CliException($"{action}失败：无法连接 {target}（{sce.Message}）。请确认主机可达且端口正确。", ex),
            System.Net.Sockets.SocketException se =>
                new CliException($"{action}失败：网络错误 {se.SocketErrorCode}（{target}）。", ex),
            TimeoutException =>
                new CliException($"{action}失败：连接 {target} 超时（{Timeout.TotalSeconds:0} 秒）。", ex),
            _ => new CliException($"{action}失败：{message}", ex)
        };
    }

    /// <summary>容忍用户把 "ssh://host"、"user@host" 或 "host:port" 直接填进 Host 字段。</summary>
    public static string NormalizeHost(string host)
    {
        var h = (host ?? "").Trim();
        if (h.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) h = h[6..];
        var at = h.LastIndexOf('@');
        if (at >= 0 && at < h.Length - 1) h = h[(at + 1)..];
        var colon = h.IndexOf(':');
        if (colon > 0) h = h[..colon];
        return h.Trim().Trim('/');
    }
}
