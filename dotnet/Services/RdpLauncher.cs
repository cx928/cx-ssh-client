using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// RDP 启动器：先 cmdkey 注入凭据，再调用系统 mstsc.exe。
/// 行为与图形版 Services/RdpService.cs 保持一致：
/// 凭据注册为 TERMSRV/&lt;host&gt;，mstsc 退出后删除；.rdp 文件写在可写的临时目录。
/// Extra 字段（键名与图形版一致）：fullscreen / admin / width / height。
/// </summary>
public static class RdpLauncher
{
    public static void Launch(SessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(session.Host))
            throw new CliException("会话缺少主机地址（Host），无法启动 RDP。");

        var host = SshConnectionFactory.NormalizeHost(session.Host);
        var port = session.Port <= 0 ? 3389 : session.Port;

        var extra = new Dictionary<string, JsonElement>();
        try { extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(session.Extra) ?? extra; }
        catch (JsonException) { /* Extra 非法 JSON 时使用默认值 */ }

        bool GetBool(string key, bool fallback)
        {
            if (!extra.TryGetValue(key, out var v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            };
        }
        int GetInt(string key, int fallback)
        {
            if (!extra.TryGetValue(key, out var v)) return fallback;
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
        }

        var fullscreen = GetBool("fullscreen", true);
        var admin = GetBool("admin", false);
        var width = GetInt("width", 1600);
        var height = GetInt("height", 900);

        // 1) 注入凭据（cmdkey 的密码需去掉双引号，避免命令行被截断）
        if (!string.IsNullOrWhiteSpace(session.Username))
        {
            var safePassword = (session.Password ?? "").Replace("\"", "");
            RunCmdkey($"/generic:TERMSRV/{host} /user:{session.Username} /pass:\"{safePassword}\"");
        }

        // 2) 生成 .rdp 文件
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{host}:{port}");
        sb.AppendLine($"username:s:{session.Username}");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("promptcredentialonce:i:0");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectclipboard:i:1");
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        sb.AppendLine("connection type:i:7");
        if (fullscreen)
        {
            sb.AppendLine("screen mode id:i:2");
        }
        else
        {
            sb.AppendLine("screen mode id:i:1");
            sb.AppendLine($"desktopwidth:i:{width}");
            sb.AppendLine($"desktopheight:i:{height}");
        }
        if (admin)
        {
            sb.AppendLine("administrative session:i:1");
            sb.AppendLine("shell working directory:s:");
        }

        var rdpFile = Path.Combine(ResolveTempDir(), $"cx-ssh-client_{session.Id}.rdp");
        File.WriteAllText(rdpFile, sb.ToString(), new UTF8Encoding(false));

        // 3) 启动 mstsc，并在其退出后清理凭据与临时文件
        try
        {
            var psi = new ProcessStartInfo("mstsc.exe", $"\"{rdpFile}\"") { UseShellExecute = true };
            var proc = Process.Start(psi);
            Console.WriteLine($"  mstsc 已启动：{host}:{port}（用户 {session.Username}）");
            Console.WriteLine($"  会话文件：{rdpFile}");
            Console.WriteLine("  提示：关闭远程桌面窗口后，凭据会自动从 Windows 凭据管理器删除。");

            if (proc is not null)
            {
                proc.WaitForExit();
                Cleanup(rdpFile, host);
            }
        }
        catch (Exception ex)
        {
            Cleanup(rdpFile, host);
            throw new CliException($"无法启动 mstsc.exe：{ex.Message}", ex);
        }
    }

    private static void Cleanup(string rdpFile, string host)
    {
        try { File.Delete(rdpFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        RunCmdkey($"/delete:TERMSRV/{host}");
    }

    /// <summary>优先 %TEMP%，其次数据目录，最后程序目录。</summary>
    private static string ResolveTempDir()
    {
        var candidates = new List<string>();
        try { candidates.Add(Path.GetTempPath()); } catch (IOException) { }
        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                VaultService.AppFolderName, "rdp"));
        }
        catch (IOException) { }
        try { candidates.Add(AppContext.BaseDirectory); } catch (IOException) { }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, "cx_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return dir;
            }
            catch (Exception) { /* 换下一个候选目录 */ }
        }

        throw new CliException("找不到可写的临时目录，已尝试：" + string.Join(" / ", candidates));
    }

    private static void RunCmdkey(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c cmdkey " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception) { /* cmdkey 不可用时仍允许 mstsc 弹出凭据输入框 */ }
    }
}
