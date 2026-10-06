using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>数据交换类命令：import / export / sync。</summary>
public static class DataCommands
{
    // ---------------- import ----------------

    public const string ImportHelp = """
        用法：cx import <文件> [选项]

        从 CSV 或加密备份导入会话到本地密码库。

        选项：
          --encrypted        按加密备份解析（{"V":1,"Salt":"...","Data":"..."}）
          --passphrase <口令>  加密备份的口令（与 --encrypted 搭配）
          --replace          先用导入内容替换整个密码库（不保留原有会话）
          --yes              跳过确认提示
          --help             显示本帮助

        合并规则（默认）：按 (协议, 主机, 端口, 用户名) 判断是否同一条会话，
        相同则更新名称/分组/口令/备注，不同则新增。新会话会分配新的 ID。

        CSV 表头（中英文均可识别，至少要有主机列）：
          name,group,protocol,host,port,username,password,note

        示例：
          cx import D:\backup\sessions.csv
          cx import D:\backup\vault.cxbak --encrypted --passphrase 你的口令
        """;

    public static int Import(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(ImportHelp); return 0; }

        var unknown = args.UnknownOptions("encrypted", "passphrase", "replace", "yes", "y", "merge", "help");
        if (unknown.Count > 0)
            throw new CliException($"import 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少导入文件路径。用法：cx import <文件> [--encrypted --passphrase 口令]");

        var path = Path.GetFullPath(string.Join(" ", args.Positional));
        if (!File.Exists(path)) throw new CliException($"文件不存在：{path}");

        var encrypted = args.GetBool("encrypted") || args.GetBool("passphrase");
        var replace = args.GetBool("replace");
        List<SessionInfo> incoming;
        var warning = "";

        if (encrypted)
        {
            var passphrase = args.Get("passphrase");
            if (string.IsNullOrEmpty(passphrase))
                throw new CliException("导入加密备份必须提供 --passphrase <口令>。");

            var backup = ImportExportService.ImportEncryptedJson(path, passphrase);
            incoming = backup.Sessions;
            Output.Info($"已解密备份：{incoming.Count} 条会话，{backup.Tombstones.Count} 条删除标记");

            if (replace)
            {
                if (!args.GetBool("yes") && !args.GetBool("y") &&
                    !Prompt.Confirm($"确认用备份替换当前密码库（现有 {vault.Data.Sessions.Count} 条会话将被清除）？"))
                {
                    Output.Info("已取消，未做任何修改。");
                    return 1;
                }
                vault.ReplaceAll(backup);
                vault.Save();
                Output.Ok($"已用备份替换密码库：{vault.Data.Sessions.Count} 条会话");
                Output.Field("密码库", vault.VaultFilePath);
                return 0;
            }
        }
        else
        {
            incoming = ImportExportService.ImportCsv(path, out warning);
            if (warning.Length > 0) Output.Warn(warning);
        }

        if (incoming.Count == 0)
        {
            Output.Warn("没有可导入的记录。");
            Output.Detail("请检查文件是否为 CSV（表头需含 host/主机 列），或加密备份是否解密成功。");
            return 1;
        }

        var (added, updated) = ImportExportService.MergeImport(vault, incoming);
        vault.Save();

        Output.Ok($"导入完成：新增 {added} 条，更新 {updated} 条，当前共 {vault.Data.Sessions.Count} 条会话");
        Output.Field("来源文件", path);
        Output.Field("密码库", vault.VaultFilePath);
        return 0;
    }

    // ---------------- export ----------------

    public const string ExportHelp = """
        用法：cx export <文件> [选项]

        把密码库导出为 CSV（明文）或加密备份。

        选项：
          --encrypted            导出为加密备份（{"V":1,"Salt":"...","Data":"..."}）
          --passphrase <口令>    加密备份口令（PBKDF2-SHA256，12 万次迭代，AES-256-GCM）
          --help                 显示本帮助

        警告：CSV 是明文格式（含口令），请妥善保管，不要随意外发。

        示例：
          cx export D:\backup\sessions.csv
          cx export D:\backup\vault.cxbak --encrypted --passphrase 你的口令
        """;

    public static int Export(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(ExportHelp); return 0; }

        var unknown = args.UnknownOptions("encrypted", "passphrase", "help");
        if (unknown.Count > 0)
            throw new CliException($"export 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        if (args.Positional.Count == 0)
            throw new CliException("缺少导出文件路径。用法：cx export <文件> [--encrypted --passphrase 口令]");

        var path = Path.GetFullPath(string.Join(" ", args.Positional));
        var encrypted = args.GetBool("encrypted") || args.GetBool("passphrase");

        if (vault.Data.Sessions.Count == 0)
        {
            Output.Warn("密码库为空，已取消导出（避免生成空文件误导后续导入）。");
            return 1;
        }

        if (encrypted)
        {
            var passphrase = args.Get("passphrase");
            if (string.IsNullOrEmpty(passphrase))
                throw new CliException("加密导出必须提供非空 --passphrase <口令>。");

            ImportExportService.ExportEncryptedJson(vault.Data, path, passphrase);
            Output.Ok($"已导出加密备份：{vault.Data.Sessions.Count} 条会话");
            Output.Field("文件", path);
            Output.Detail("格式 {\"V\":1,\"Salt\":\"base64\",\"Data\":\"base64(iv|tag|cipher)\"}，PBKDF2-SHA256 12 万次");
            return 0;
        }

        ImportExportService.ExportCsv(vault.Data, path);
        Output.Ok($"已导出 CSV：{vault.Data.Sessions.Count} 条会话");
        Output.Field("文件", path);
        Output.Warn("CSV 为明文（包含口令），请妥善保管。需要加密请加 --encrypted --passphrase <口令>。");
        return 0;
    }

    // ---------------- sync ----------------

    public const string SyncHelp = """
        用法：cx sync [--server <URL> --user <账号> --password <口令>] [选项]

        与自建同步服务端推拉加密密码库（协议与图形版一致）：
          POST /api/v1/auth/login {username,password} -> {token}
          GET  /api/v1/vault -> {data,rev,updated_at}    （404 表示云端尚无数据）
          PUT  /api/v1/vault {data,updated_at,base_rev} -> {rev}（版本冲突返回 409，会自动重拉合并后再推一次）
        鉴权：Authorization: Bearer <token>
        data = base64(AES-256-GCM(iv|tag|cipher))，
        密钥 = PBKDF2-SHA256(同步口令, "novaremote-sync:" + 用户名小写, 120000, 32)

        选项：
          --server <URL>        服务端地址，例如 https://example.com:8443
          --user <账号>         同步账号
          --password <口令>     同步口令
          --trust-self-signed   接受自签名证书（仅在内网/自建服务时使用）
          --save                把本次参数保存到本地（口令经 DPAPI 加密），下次可直接 `cx sync`
          --dry-run             只拉取并显示差异，不写回本地、不推送
          --help                显示本帮助

        合并规则：按会话 ID 取 UpdatedAt 较新者；删除标记（tombstone）优先。
        """;

    public static int Sync(VaultService vault, ArgParser args)
    {
        if (args.GetBool("help")) { Console.WriteLine(SyncHelp); return 0; }

        var unknown = args.UnknownOptions("server", "user", "username", "password", "pass",
            "trust-self-signed", "save", "dry-run", "help");
        if (unknown.Count > 0)
            throw new CliException($"sync 不认识的参数：{string.Join(", ", unknown.Select(u => "--" + u))}");

        var saved = SyncConfig.Load(vault.DataDirectory);

        var server = args.Has("server") ? args.Get("server") : saved?.Server ?? "";
        var user = args.Has("user") ? args.Get("user")
            : args.Has("username") ? args.Get("username")
            : saved?.User ?? "";
        var password = args.Has("password") ? args.Get("password")
            : args.Has("pass") ? args.Get("pass")
            : saved?.GetPassword() ?? "";
        var trust = args.GetBool("trust-self-signed") || (saved?.TrustSelfSigned ?? false);

        if (string.IsNullOrWhiteSpace(server))
            throw new CliException("缺少 --server 参数（同步服务端地址）。参见 `cx sync --help`。");
        if (string.IsNullOrWhiteSpace(user))
            throw new CliException("缺少 --user 参数（同步账号）。参见 `cx sync --help`。");
        if (string.IsNullOrWhiteSpace(password))
            throw new CliException("缺少 --password 参数（同步口令）。参见 `cx sync --help`。");

        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            server = "https://" + server;

        Output.Info($"同步服务端：{server}");
        Output.Field("账号", user);
        Output.Field("本地会话数", vault.Data.Sessions.Count.ToString());
        if (trust) Output.Warn("已启用 --trust-self-signed：将接受任意服务端证书（仅建议在内网使用）");
        if (args.GetBool("dry-run")) Output.Warn("--dry-run：只拉取比较，不写回本地、不推送");

        using var sync = new SyncService(trust, msg => Output.Detail("  " + msg));
        sync.ServerUrl = server;
        sync.Username = user;
        sync.Password = password;

        SyncResult result;
        try
        {
            result = sync.SyncAsync(vault.Data).GetAwaiter().GetResult();
        }
        catch (CliException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new CliException(
                $"无法连接同步服务端 {server}：{ex.Message}" +
                (trust ? "" : "。若服务端使用自签名证书，请加 --trust-self-signed。"), ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new CliException($"同步请求超时（{server}）：{ex.Message}", ex);
        }

        if (args.GetBool("dry-run"))
        {
            Output.Info("--dry-run 结束：未修改本地密码库，未推送云端。");
            Output.Field("云端版本", result.Rev.ToString());
            return result.Ok ? 0 : 1;
        }

        if (!result.Ok)
        {
            Output.Error("同步失败：" + result.Message);
            return 1;
        }

        if (result.Merged is not null)
        {
            var before = vault.Data.Sessions.Count;
            vault.ReplaceAll(result.Merged);
            vault.Save();
            Output.Detail($"本地密码库已更新（{before} → {vault.Data.Sessions.Count} 条会话）");
        }

        if (args.GetBool("save"))
        {
            saved ??= new SyncConfig();
            saved.Server = server;
            saved.User = user;
            saved.TrustSelfSigned = trust;
            saved.SetPassword(password);
            saved.Save(vault.DataDirectory);
            Output.Detail($"已保存同步参数：{Path.Combine(vault.DataDirectory, "sync.json")}（口令经 DPAPI 加密）");
        }

        Output.Ok($"同步成功：{result.Message}");
        Output.Field("云端版本", result.Rev.ToString());
        Output.Field("会话总数", vault.Data.Sessions.Count.ToString());
        Output.Field("删除标记", vault.Data.Tombstones.Count.ToString());
        return 0;
    }
}
