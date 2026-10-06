using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CxSshClient.Models;

namespace CxSshClient.Services;

/// <summary>RDP: 通过 cmdkey 注入凭据并调用 mstsc 连接 (系统原生, 体验最佳)</summary>
public static class RdpService
{
    public static void Launch(SessionInfo s)
    {
        var extra = new Dictionary<string, JsonElement>();
        try { extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Extra) ?? extra; } catch { }

        bool GetBool(string k, bool def = false)
        {
            if (!extra.TryGetValue(k, out var v)) return def;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => def
            };
        }
        int GetInt(string k, int def = 0)
        {
            if (!extra.TryGetValue(k, out var v)) return def;
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;
        }

        var fullscreen = GetBool("fullscreen", true);
        var admin = GetBool("admin", false);
        var width = GetInt("width", 1600);
        var height = GetInt("height", 900);

        // 1) 凭据注入 (mstsc 退出后清除)
        if (!string.IsNullOrWhiteSpace(s.Username))
        {
            RunCmdkey($"cmdkey /generic:TERMSRV/{s.Host} /user:{s.Username} /pass:\"{s.Password.Replace("\"", "")}\"");
        }

        // 2) 生成 .rdp 文件 (临时目录不可写时自动回退)
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{s.Host}:{s.Port}");
        sb.AppendLine($"username:s:{s.Username}");
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
            sb.AppendLine("screen mode id:i:2");
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

        var file = Path.Combine(ResolveTempDir(), $"cx-ssh-client_{s.Id}.rdp");
        File.WriteAllText(file, sb.ToString());

        // 3) 启动 mstsc
        try
        {
            var p = Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{file}\"") { UseShellExecute = true });
            _ = Task.Run(() =>
            {
                try
                {
                    p?.WaitForExit();
                    File.Delete(file);
                    RunCmdkey($"cmdkey /delete:TERMSRV/{s.Host}");
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException("无法启动 mstsc: " + ex.Message);
        }
    }

    /// <summary>当前用于存放 .rdp 文件的可写目录 (诊断/展示用)</summary>
    public static string CurrentTempDir
    {
        get
        {
            try { return ResolveTempDir(); }
            catch { return "(无可用目录)"; }
        }
    }

    /// <summary>找一个可写的临时目录: 优先 %TEMP%, 其次应用数据目录, 最后程序目录</summary>
    private static string ResolveTempDir()
    {
        var candidates = new List<string>();
        try { candidates.Add(Path.GetTempPath()); } catch { }
        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cx-ssh-client", "rdp"));
        }
        catch { }
        try { candidates.Add(AppContext.BaseDirectory); } catch { }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, "nova_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return dir;
            }
            catch { }
        }
        throw new InvalidOperationException(
            "找不到可写的临时目录, 已尝试: " + string.Join(" / ", candidates));
    }

    private static void RunCmdkey(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {args}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }
}
