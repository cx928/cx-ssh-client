using System.Diagnostics;
using System.Text;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 远程连接类命令：exec / ssh / sftp / ftp / rdp。
/// 这些命令都需要先解析出目标会话：既支持密码库里的「名称 / ID 前缀」，
/// 也支持直接写成 user@host（临时连接，不写入密码库）。
/// </summary>
public static class ConnectionCommands
{
    /// <summary>
    /// 解析目标。返回 (会话, 是否来自密码库)。
    /// 若写法是 user@host 且密码库中没有同名记录，则构造一个临时会话。
    /// </summary>
    public static (SessionInfo Session, bool FromVault) ResolveTarget(
        VaultService vault, string target, int defaultPort = 22, string? passwordOverride = null,
        SessionProtocol defaultProtocol = SessionProtocol.Ssh)
    {
        var text = (target ?? "").Trim();
        if (text.Length == 0)
            throw new CliException("缺少目标会话。可填密码库中的会话名称 / ID 前缀，或直接写 user@host。");

        var known = vault.Find(text);
        if (known is not null) return (known, true);

        // user@host 形式：临时会话
        if (text.Contains('@'))
        {
            var at = text.LastIndexOf('@');
            var user = text[..at];
            var hostPart = text[(at + 1)..];
            var port = defaultPort;

            var colon = hostPart.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPart[(colon + 1)..], out var p) && p > 0 && p <= 65535)
            {
                port = p;
                hostPart = hostPart[..colon];
            }

            if (hostPart.Trim().Length == 0)
                throw new CliException($"无法解析目标「{target}」：@ 后面没有主机地址。");

            return (new SessionInfo
            {
                Name = text,
                Host = hostPart.Trim(),
                Port = port,
                Username = user.Trim(),
                Password = passwordOverride ?? "",
                Protocol = defaultProtocol
            }, false);
        }

        // 看着像主机名/IP 但缺用户名：临时连接必须带用户名
        if (text.Contains('.') || text.Contains(':') || text.Contains('/'))
        {
            var host = text;
            var port = defaultPort;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p2) && p2 > 0 && p2 <= 65535)
            {
                port = p2;
                host = text[..colon];
            }
            throw new CliException(
                $"「{target}」不在密码库中，且没有写用户名，无法作为临时连接。" +
                $"请改用 user@host 形式（例如 你的用户名@{host}:{port}），或先用 `cx add --name <名称> --host {host}` 保存为会话。");
        }

        // 最后借用 vault.Require 抛出「未找到 / 名称不唯一」的友好提示
        vault.Require(text);
        throw new CliException($"无法解析目标「{target}」。");
    }

    // ---------------- exec ----------------

    public static readonly string ExecHelp = $"""
        用法：cx exec <名称|user@host> "<命令>" [--password <口令>] [--timeout <秒>] [--out <文件>]

        通过 SSH 在远端执行一条命令，打印标准输出、标准错误与远端退出码。
        本命令的退出码 = 远端命令的退出码（便于脚本串联）。

        参数：
          <名称>             密码库中的会话名称或 ID 前缀
          user@host          临时连接（口令用 --password 提供）
          "<命令>"           要执行的命令，建议用引号包住整条命令

        选项：
          --password <口令>  临时口令（user@host 形式或覆盖库中口令时使用）
          --timeout <秒>     命令超时，默认 60 秒
          --out <文件>       同时把标准输出写入本地文件
          --help             显示本帮助

        示例：
          cx exec web-1 "uname -a"
          cx exec web-1 "df -h" --out D:\tmp\df.txt
        """;

    public static int Exec(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(ExecHelp); return 0; }

        var unknown = args.UnknownOptions("password", "pass", "timeout", "out", "help");
        if (unknown.Count > 0)
            throw new CliException($"exec 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count < 2)
            throw new CliException("用法：cx exec <名称|user@host> \"命令\"。参见 `cx exec --help`。");

        var target = args.Positional[0];
        var command = string.Join(" ", args.Positional.Skip(1));
        var password = args.Has("password") ? args.Get("password") : args.Get("pass");

        var (session, fromVault) = ResolveTarget(vault, target, 22, password);
        if (!fromVault) Output.Detail($"（临时连接 {session.Username}@{session.Host}:{session.Port}，未使用密码库）");

        var timeoutSeconds = args.Has("timeout") ? args.GetInt("timeout", 60) : 60;
        if (timeoutSeconds <= 0) timeoutSeconds = 60;

        Output.Detail($"$ ssh {session.Username}@{session.Host}:{session.Port} -- {command}");
        var result = SshCommandRunner.Run(session, command, TimeSpan.FromSeconds(timeoutSeconds));

        if (result.StdOut.Length > 0)
        {
            Console.Out.Write(result.StdOut);
            if (!result.StdOut.EndsWith('\n')) Console.Out.WriteLine();
        }
        if (result.StdErr.Length > 0)
        {
            var old = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Error.Write(result.StdErr);
                if (!result.StdErr.EndsWith('\n')) Console.Error.WriteLine();
            }
            finally { Console.ForegroundColor = old; }
        }

        if (args.Has("out"))
        {
            TextFileWriter.WriteAllText(args.Get("out"), result.StdOut);
            Output.Detail($"标准输出已写入：{Path.GetFullPath(args.Get("out"))}");
        }

        Console.WriteLine($"CX_EXIT_CODE={result.ExitCode}");
        if (result.ExitStatusUnknown)
            Output.Warn("服务端未返回退出码（连接可能被中途断开），已按 0 处理。");
        Output.Detail($"耗时 {result.Elapsed.TotalSeconds:0.00} 秒");
        return result.ExitCode;
    }

    // ---------------- ssh（交互式） ----------------

    public const string SshHelp = """
        用法：cx ssh <名称|user@host> [--port <端口>] [--batch]

        调用系统自带的 ssh.exe 打开交互式会话，把当前终端交给它。
        键盘交互、ssh-agent、known_hosts 与 ~/.ssh/config 的行为与手动执行 ssh 完全一致。

        说明：ssh.exe 不会自动读取密码库里的口令。若该会话只有口令没有私钥，
              请先按提示手动输入，或为该会话配置私钥（cx add --key <私钥路径>）。

        选项：
          --port <端口>   临时覆盖端口（默认用会话里的端口，22 时不额外传参）
          --batch         加 -o BatchMode=yes，禁止任何交互式提问（用于脚本探测）
          --help          显示本帮助
        """;

    public static int Ssh(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(SshHelp); return 0; }

        var unknown = args.UnknownOptions("port", "batch", "help");
        if (unknown.Count > 0)
            throw new CliException($"ssh 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少会话名称。用法：cx ssh <名称|user@host>");

        var (session, _) = ResolveTarget(vault, string.Join(" ", args.Positional));
        if (args.Has("port")) session.Port = args.GetInt("port", session.Port);

        if (string.IsNullOrWhiteSpace(session.Username))
            throw new CliException($"会话「{session.Name}」没有用户名，交互式 SSH 无法自动补全。请用 `cx add --user` 补全，或改用 user@host 形式。");

        Output.Detail($"正在调用 {SshCommandRunner.ResolveSshExe()} …");
        var code = SshCommandRunner.RunInteractive(session, args.GetBool("batch"));
        if (code != 0) Output.Warn($"ssh.exe 退出码：{code}");
        return code;
    }

    // ---------------- sftp ----------------

    public static readonly string SftpHelp = $"""
        用法：cx sftp <子命令> [参数]

        子命令：
          ls  <名称> [远端目录]                 列出远端目录（默认当前工作目录）
          get <名称> <远端文件> <本地路径>       下载文件
          put <名称> <本地文件> <远端路径>       上传文件

        选项（各子命令通用）：
          --password <口令>   临时口令
          --quiet             不显示进度
          --help              显示本帮助

        示例：
          cx sftp ls  web-1 /root
          cx sftp get web-1 /etc/os-release D:\tmp\os-release.txt
          cx sftp put web-1 D:\tmp\a.txt /root/a.txt
        """;

    public static int Sftp(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help") || args.Positional.Count == 0)
        {
            Console.WriteLine(SftpHelp);
            return args.GetBool("help") ? 0 : 1;
        }

        var sub = args.Positional[0].ToLowerInvariant();
        var rest = args.Positional.Skip(1).ToList();

        if (sub is not ("ls" or "get" or "put"))
            throw new CliException($"sftp 不认识的子命令「{sub}」，可用：ls / get / put。参见 `cx sftp --help`。");

        var unknown = args.UnknownOptions("password", "pass", "quiet", "help");
        if (unknown.Count > 0)
            throw new CliException($"sftp {sub} 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        var expect = sub == "ls" ? 1 : 3;
        if (rest.Count < expect)
            throw new CliException($"sftp {sub} 参数不足。参见 `cx sftp --help`。");

        var (session, _) = ResolveTarget(vault, rest[0], 22,
            args.Has("password") ? args.Get("password") : args.Get("pass"));

        using var sftp = new SftpService(session);

        switch (sub)
        {
            case "ls":
            {
                var dir = rest.Count > 1 ? rest[1] : sftp.WorkingDirectory;
                var entries = sftp.List(dir);
                Output.Head($"目录 {SftpService.Normalize(dir)}（{session.Username}@{session.Host}:{session.Port}）");
                Output.Head(new string('-', 60));
                Output.Table(
                    new[] { "类型", "大小", "修改时间", "名称" },
                    entries.Select(e => (IReadOnlyList<string>)new[]
                    {
                        e.IsDirectory ? "DIR" : "FILE",
                        e.SizeText,
                        e.ModifiedUtc == DateTime.MinValue
                            ? "-"
                            : e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        e.Name
                    }).ToList());
                Output.Detail($"共 {entries.Count} 项");
                return 0;
            }

            case "get":
            {
                var remote = rest[1];
                var local = rest[2];
                var quiet = args.GetBool("quiet");
                var sw = Stopwatch.StartNew();
                var written = sftp.Download(remote, local, quiet ? null : MakeProgress());
                sw.Stop();
                if (!quiet) Console.WriteLine();
                Output.Ok($"已下载 {SftpService.Normalize(remote)} → {Path.GetFullPath(local)}");
                Output.Detail($"共 {written} 字节，耗时 {sw.Elapsed.TotalSeconds:0.00} 秒");
                return 0;
            }

            default:
            {
                var local = rest[1];
                var remote = rest[2];
                var quiet = args.GetBool("quiet");
                var sw = Stopwatch.StartNew();
                var sent = sftp.Upload(local, remote, quiet ? null : MakeProgress());
                sw.Stop();
                if (!quiet) Console.WriteLine();
                Output.Ok($"已上传 {Path.GetFullPath(local)} → {SftpService.Normalize(remote)}");
                Output.Detail($"共 {sent} 字节，耗时 {sw.Elapsed.TotalSeconds:0.00} 秒");
                return 0;
            }
        }
    }

    // ---------------- ftp ----------------

    public static readonly string FtpHelp = """
        用法：cx ftp <子命令> [参数]

        子命令：
          ls  <名称> [远端目录]                 列出远端目录（默认 /）
          get <名称> <远端文件> <本地路径>       下载文件
          put <名称> <本地文件> <远端路径>       上传文件

        FTP/FTPS 的加密方式与数据连接模式取自会话的 Extra 字段：
          {"encryption":"none|explicit|implicit","mode":"passive|active"}

        选项：
          --password <口令>   临时口令
          --quiet             不显示进度
          --help              显示本帮助
        """;

    public static int Ftp(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help") || args.Positional.Count == 0)
        {
            Console.WriteLine(FtpHelp);
            return args.GetBool("help") ? 0 : 1;
        }

        var sub = args.Positional[0].ToLowerInvariant();
        var rest = args.Positional.Skip(1).ToList();

        if (sub is not ("ls" or "get" or "put"))
            throw new CliException($"ftp 不认识的子命令「{sub}」，可用：ls / get / put。参见 `cx ftp --help`。");

        var unknown = args.UnknownOptions("password", "pass", "quiet", "help");
        if (unknown.Count > 0)
            throw new CliException($"ftp {sub} 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        var expect = sub == "ls" ? 1 : 3;
        if (rest.Count < expect)
            throw new CliException($"ftp {sub} 参数不足。参见 `cx ftp --help`。");

        var (session, _) = ResolveTarget(vault, rest[0], 21,
            args.Has("password") ? args.Get("password") : args.Get("pass"), SessionProtocol.Ftp);

        using var ftp = new FtpService(session);
        var quiet = args.GetBool("quiet");

        switch (sub)
        {
            case "ls":
            {
                var dir = rest.Count > 1 ? rest[1] : ftp.WorkingDirectory;
                var entries = ftp.List(dir);
                Output.Head($"目录 {FtpService.Normalize(dir)}（ftp://{session.Username}@{session.Host}:{session.Port}）");
                Output.Head(new string('-', 60));
                Output.Table(
                    new[] { "类型", "大小", "修改时间", "名称" },
                    entries.Select(e => (IReadOnlyList<string>)new[]
                    {
                        e.IsDirectory ? "DIR" : "FILE",
                        e.SizeText,
                        e.Modified == DateTime.MinValue || e.Modified.Year < 1900
                            ? "-"
                            : e.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        e.Name
                    }).ToList());
                Output.Detail($"共 {entries.Count} 项");
                return 0;
            }

            case "get":
            {
                var sw = Stopwatch.StartNew();
                var size = ftp.Download(rest[1], rest[2]);
                sw.Stop();
                Output.Ok($"已下载 {FtpService.Normalize(rest[1])} → {Path.GetFullPath(rest[2])}");
                Output.Detail($"共 {size} 字节，耗时 {sw.Elapsed.TotalSeconds:0.00} 秒");
                return 0;
            }

            default:
            {
                var sw = Stopwatch.StartNew();
                var size = ftp.Upload(rest[1], rest[2]);
                sw.Stop();
                Output.Ok($"已上传 {Path.GetFullPath(rest[1])} → {FtpService.Normalize(rest[2])}");
                Output.Detail($"共 {size} 字节，耗时 {sw.Elapsed.TotalSeconds:0.00} 秒");
                return 0;
            }
        }
    }

    // ---------------- rdp ----------------

    public const string RdpHelp = """
        用法：cx rdp <名称|user@host>

        通过 Windows 凭据管理器（cmdkey）注入凭据，再调用系统 mstsc.exe 打开远程桌面。
        关闭远程桌面窗口后会自动删除凭据与临时 .rdp 文件。

        选项：
          --help   显示本帮助

        说明：会话的 Extra 字段可控制显示方式，例如：
          {"fullscreen":true,"admin":false,"width":1600,"height":900}
        """;

    public static int Rdp(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(RdpHelp); return 0; }

        var unknown = args.UnknownOptions("help");
        if (unknown.Count > 0)
            throw new CliException($"rdp 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少会话名称。用法：cx rdp <名称>");

        var (session, _) = ResolveTarget(vault, string.Join(" ", args.Positional), 3389,
            null, SessionProtocol.Rdp);

        if (!OperatingSystem.IsWindows())
            throw new CliException("当前系统不支持 mstsc.exe，RDP 功能仅在 Windows 上可用。");

        RdpLauncher.Launch(session);
        return 0;
    }

    // ---------------- 进度条 ----------------

    /// <summary>单行进度回调；控制台被重定向时自动降级为静默。</summary>
    private static Action<long, long> MakeProgress()
    {
        var interactive = !Console.IsOutputRedirected;
        if (!interactive) return (_, _) => { };

        return (done, total) =>
        {
            try
            {
                var width = 28;
                var ratio = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
                var filled = (int)Math.Round(ratio * width);
                var bar = new string('#', filled) + new string('.', width - filled);
                Console.Write($"\r  [{bar}] {ratio * 100,5:0.0}%  {done}/{total} 字节");
            }
            catch (IOException) { }
        };
    }
}
