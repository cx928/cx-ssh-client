using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 密码库相关命令：list / add / rm / show。
/// 每个方法返回进程退出码，参数错误会抛出 CliException（由 Program 统一转成中文错误）。
/// </summary>
public static class VaultCommands
{
    // ---------------- list ----------------

    public const string ListHelp = """
        用法：cx list [--protocol <ssh|sftp|rdp|ftp|vnc>] [--group <分组>] [--json]

        列出密码库中的全部会话（按分组、名称排序）。

        选项：
          --protocol <名称>  只显示指定协议的会话
          --group <分组>     只显示指定分组的会话
          --json             以 JSON 输出，便于脚本处理
          --help             显示本帮助
        """;

    public static int List(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(ListHelp); return 0; }

        var unknown = args.UnknownOptions("protocol", "group", "json", "help");
        if (unknown.Count > 0)
            throw new CliException($"list 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        IEnumerable<SessionInfo> items = vault.Data.Sessions;

        if (args.Has("protocol"))
        {
            var text = args.Get("protocol");
            if (!ProtocolInfo.TryParse(text, out var proto))
                throw new CliException($"无法识别的协议「{text}」，可用：{ProtocolInfo.AllNames}");
            items = items.Where(s => s.Protocol == proto);
        }

        if (args.Has("group"))
        {
            var group = args.Get("group");
            items = items.Where(s => string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase));
        }

        var list = items
            .OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (args.GetBool("json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(list, Json.Options));
            return 0;
        }

        if (list.Count == 0)
        {
            Output.Info(vault.Data.Sessions.Count == 0
                ? "密码库为空。用 `cx add --name 名称 --host 主机 [--user 用户] [--password 口令]` 添加第一条会话。"
                : "没有符合条件的会话。");
            return 0;
        }

        var rows = list.Select(s => (IReadOnlyList<string>)new[]
        {
            s.Id[..8],
            ProtocolInfo.Label(s.Protocol),
            Output.Truncate(s.Name, 24),
            Output.Truncate(s.Group, 12),
            $"{s.Host}:{s.Port}",
            Output.Truncate(s.Username, 16),
            string.IsNullOrEmpty(s.Password) && string.IsNullOrEmpty(s.PrivateKeyPath) ? "-" : "已保存"
        }).ToList();

        Output.Table(new[] { "ID", "协议", "名称", "分组", "地址", "用户名", "凭据" }, rows);
        Output.Detail($"共 {list.Count} 条会话（密码库：{vault.VaultFilePath}）");
        return 0;
    }

    // ---------------- add ----------------

    public const string AddHelp = """
        用法：cx add --name <名称> --host <主机> [选项]

        新增一条会话并写入密码库（DPAPI 加密后保存到
        %LOCALAPPDATA%\cx-ssh-client\vault.dat，与图形版共用同一份数据）。

        必填：
          --name <名称>        会话名称（唯一，供其他命令引用）
          --host <主机>        主机名或 IP

        选填：
          --protocol <名称>    协议 ssh（默认）/ sftp / rdp / ftp / vnc
          --port <端口>        端口，默认按协议取 22 / 22 / 3389 / 21 / 5900
          --user <用户名>      登录用户名（也可写作 --username）
          --password <口令>    登录口令（明文写入加密库，仅供本机当前用户读取）
          --group <分组>       分组名，便于归类
          --note <备注>        备注信息
          --key <私钥路径>     私钥文件（与 --password 同时存在时优先用私钥认证）
          --extra <JSON>       协议扩展设置，例如 RDP：{"fullscreen":true}
          --force              名称已存在时覆盖原会话
          --help               显示本帮助

        示例：
          cx add --name 生产Web --host 10.0.0.10 --user root --password 你的口令 --group 生产
        """;

    public static int Add(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(AddHelp); return 0; }

        var unknown = args.UnknownOptions("name", "host", "protocol", "port", "user", "username",
            "password", "pass", "group", "note", "key", "privatekey", "keypath", "extra", "force", "help");
        if (unknown.Count > 0)
            throw new CliException($"add 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        var name = args.Get("name").Trim();
        var host = args.Get("host").Trim();
        if (name.Length == 0) throw new CliException("缺少 --name 参数（会话名称）。参见 `cx add --help`。");
        if (host.Length == 0) throw new CliException("缺少 --host 参数（主机地址）。参见 `cx add --help`。");
        if (name.Length > 128) throw new CliException("会话名称过长（最多 128 个字符）。");

        var protocol = SessionProtocol.Ssh;
        if (args.Has("protocol") && !ProtocolInfo.TryParse(args.Get("protocol"), out protocol))
            throw new CliException($"无法识别的协议「{args.Get("protocol")}」，可用：{ProtocolInfo.AllNames}");

        var port = args.Has("port") ? args.GetInt("port", -1) : ProtocolInfo.DefaultPort(protocol);
        if (port <= 0 || port > 65535)
            throw new CliException($"端口不合法：{args.Get("port")}（应为 1-65535）。");

        var extra = args.Get("extra", "{}").Trim();
        if (extra.Length == 0) extra = "{}";
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(extra);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CliException($"--extra 不是合法 JSON：{ex.Message}");
        }

        var existing = vault.FindAllByName(name);
        SessionInfo session;
        if (existing.Count > 0)
        {
            if (!args.GetBool("force"))
                throw new CliException(
                    $"已存在同名会话「{name}」（ID {existing[0].Id[..8]}）。" +
                    "如需覆盖请加 --force，或换一个名称，或先用 `cx rm` 删除。");
            session = existing[0];
            session.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        else
        {
            session = new SessionInfo { Id = Guid.NewGuid().ToString("N") };
        }

        session.Name = name;
        session.Host = host;
        session.Protocol = protocol;
        session.Port = port;
        session.Group = args.Get("group");
        session.Note = args.Get("note");
        session.Username = args.Has("user") ? args.Get("user") : args.Get("username");
        session.Password = args.Has("password") ? args.Get("password") : args.Get("pass");
        session.PrivateKeyPath = args.Has("key") ? args.Get("key")
            : args.Has("privatekey") ? args.Get("privatekey")
            : args.Get("keypath");
        session.Extra = extra;

        vault.UpsertSession(session);
        vault.Save();

        Output.Ok($"已保存会话「{session.Name}」");
        Output.Field("ID", session.Id);
        Output.Field("协议/地址", $"{ProtocolInfo.Label(session.Protocol)}  {session.Host}:{session.Port}");
        Output.Field("用户名", string.IsNullOrEmpty(session.Username) ? "(未设置)" : session.Username);
        Output.Field("口令", Output.Mask(session.Password));
        Output.Field("密码库", vault.VaultFilePath);
        return 0;
    }

    // ---------------- rm ----------------

    public const string RmHelp = """
        用法：cx rm <名称|ID前缀> [--yes]

        删除一条会话，并写入删除标记（tombstone）以便同步时把删除动作传播到其他设备。

        选项：
          --yes, -y   跳过确认提示（脚本中使用）
          --help      显示本帮助
        """;

    public static int Remove(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(RmHelp); return 0; }

        var unknown = args.UnknownOptions("yes", "y", "help");
        if (unknown.Count > 0)
            throw new CliException($"rm 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少会话名称。用法：cx rm <名称|ID前缀> [--yes]");

        var key = string.Join(" ", args.Positional);
        var session = vault.Require(key);

        if (!args.GetBool("yes") && !args.GetBool("y"))
        {
            Output.Warn($"即将删除会话「{session.Name}」（{ProtocolInfo.Label(session.Protocol)} {session.Host}:{session.Port}）");
            if (!Prompt.Confirm("确认删除？"))
            {
                Output.Info("已取消，未做任何修改。");
                return 1;
            }
        }

        vault.DeleteSession(session.Id);
        vault.Save();

        Output.Ok($"已删除会话「{session.Name}」（ID {session.Id[..8]}）");
        Output.Detail("删除标记已记录，下次 `cx sync` 会同步到其他设备。");
        return 0;
    }

    // ---------------- show ----------------

    public const string ShowHelp = """
        用法：cx show <名称|ID前缀> [--show-password] [--json]

        显示一条会话的完整信息。默认隐藏口令，需显式加 --show-password 才会明文打印。

        选项：
          --show-password   明文显示口令（注意：终端内容可能被记录）
          --json            以 JSON 输出（含明文口令）
          --help            显示本帮助
        """;

    public static int Show(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(ShowHelp); return 0; }

        var unknown = args.UnknownOptions("show-password", "json", "help");
        if (unknown.Count > 0)
            throw new CliException($"show 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少会话名称。用法：cx show <名称|ID前缀> [--show-password]");

        var session = vault.Require(string.Join(" ", args.Positional));

        if (args.GetBool("json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(session, Json.Options));
            return 0;
        }

        var reveal = args.GetBool("show-password");
        Output.Head($"会话：{session.Name}");
        Output.Head(new string('-', 40));
        Output.Field("ID", session.Id);
        Output.Field("协议", ProtocolInfo.Label(session.Protocol));
        Output.Field("分组", string.IsNullOrEmpty(session.Group) ? "(未分组)" : session.Group);
        Output.Field("主机", session.Host);
        Output.Field("端口", session.Port.ToString());
        Output.Field("用户名", string.IsNullOrEmpty(session.Username) ? "(未设置)" : session.Username);
        Output.Field("口令", reveal ? session.Password ?? "" : Output.Mask(session.Password),
            reveal ? ConsoleColor.Yellow : ConsoleColor.Gray);
        Output.Field("私钥", string.IsNullOrEmpty(session.PrivateKeyPath) ? "(未设置)" : session.PrivateKeyPath);
        Output.Field("备注", string.IsNullOrEmpty(session.Note) ? "(无)" : session.Note);
        Output.Field("扩展设置", string.IsNullOrWhiteSpace(session.Extra) ? "{}" : session.Extra);
        Output.Field("创建时间", FormatTime(session.CreatedAt));
        Output.Field("更新时间", FormatTime(session.UpdatedAt));

        if (!reveal && !string.IsNullOrEmpty(session.Password))
            Output.Detail("口令已隐藏，如需查看请加 --show-password");
        return 0;
    }

    private static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "(未知)";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException) { return "(时间戳越界)"; }
    }
}

/// <summary>交互式确认（脚本中可用 --yes 跳过）。</summary>
public static class Prompt
{
    public static bool Confirm(string question, bool defaultValue = false)
    {
        if (Console.IsInputRedirected)
        {
            Output.Warn("当前为标准输入重定向环境，无法交互确认；已按「否」处理（如需继续请加 --yes）。");
            return defaultValue;
        }

        Console.Write($"{question} [y/N] ");
        try
        {
            var line = Console.ReadLine();
            if (line is null) return defaultValue;
            return line.Trim().ToLowerInvariant() is "y" or "yes" or "是";
        }
        catch (IOException)
        {
            return defaultValue;
        }
    }
}
