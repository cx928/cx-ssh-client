using System.Text;
using CxSshClient.Cli.Services;

namespace CxSshClient.Cli;

/// <summary>
/// 程星SSH客户端（cx-ssh-client）命令行版入口。
///
/// 设计要点：
///  * 密码库与图形版完全互通：%LOCALAPPDATA%\cx-ssh-client\vault.dat（DPAPI + UTF-8 JSON）
///  * 同步协议与图形版一致，云端密文可互相解密
///  * 不使用任何命令行解析库，参数解析见 Services/ArgParser.cs
///
/// 退出码约定：
///   0  成功
///   1  运行期失败（连接失败、文件缺失、同步失败…）或用户取消
///   2  用法错误（参数不合法 / 未知命令）
///   N  `cx exec` 直接返回远端命令的退出码
/// </summary>
public static class Program
{
    public const string Version = "1.0.0";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintBanner();
            PrintGlobalHelp();
            return 0;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        // 全局开关：cx --help / cx --version / cx -v
        if (command is "--help" or "-h" or "help" or "/?")
        {
            PrintBanner();
            PrintGlobalHelp();
            return 0;
        }
        if (command is "--version" or "-v" or "version")
        {
            Console.WriteLine($"cx-ssh {Version}");
            Console.WriteLine("程星SSH客户端 命令行版 (MPL-2.0)");
            return 0;
        }

        var args2 = ArgParser.Parse(command, rest);

        // --data-dir 是唯一在所有命令之前生效的全局选项，便于测试与便携使用
        if (args2.Has("data-dir") || args2.Has("data"))
        {
            var dir = args2.Has("data-dir") ? args2.Get("data-dir") : args2.Get("data");
            if (dir.Length > 0) VaultService.OverrideDirectory = Path.GetFullPath(dir);
            args2.Remove("data-dir");
            args2.Remove("data");
        }

        var vault = new VaultService();
        vault.Load();
        foreach (var w in vault.Warnings)
            Output.Warn(w);

        try
        {
            return command switch
            {
                "list" or "ls" => VaultCommands.List(vault, args2),
                "add" => VaultCommands.Add(vault, args2),
                "rm" or "remove" or "del" or "delete" => VaultCommands.Remove(vault, args2),
                "show" or "info" => VaultCommands.Show(vault, args2),
                "exec" or "run" => ConnectionCommands.Exec(vault, args2),
                "ssh" => ConnectionCommands.Ssh(vault, args2),
                "sftp" => ConnectionCommands.Sftp(vault, args2),
                "ftp" => ConnectionCommands.Ftp(vault, args2),
                "rdp" => ConnectionCommands.Rdp(vault, args2),
                "import" => DataCommands.Import(vault, args2),
                "export" => DataCommands.Export(vault, args2),
                "sync" => DataCommands.Sync(vault, args2),
                _ => UnknownCommand(command)
            };
        }
        catch (CliException ex)
        {
            Output.Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Output.Error("操作已取消。");
            return 1;
        }
        catch (Exception ex)
        {
            // 兜底：不把堆栈直接甩给用户，但保留类型与消息便于排查
            Output.Error($"发生未预期的错误（{ex.GetType().Name}）：{ex.Message}");
            Output.Detail("如反复出现，请用 `cx list`、`cx show <名称>` 确认密码库状态后反馈。");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Output.Error($"未知命令「{command}」。");
        Console.WriteLine();
        PrintGlobalHelp();
        return 2;
    }

    private static void PrintBanner()
    {
        Console.WriteLine($"程星SSH客户端 命令行版  cx-ssh {Version}");
        Console.WriteLine($"密码库：{new VaultService().VaultFilePath}");
        Console.WriteLine("许可协议：MPL-2.0");
        Console.WriteLine();
    }

    private static void PrintGlobalHelp()
    {
        Console.WriteLine("""
            用法：cx-ssh <命令> [参数]

            会话管理
              list    列出全部会话（可 --protocol 过滤）
              add     新增会话（--name --host 必填）
              rm      删除会话（按名称或 ID 前缀）
              show    查看会话详情（--show-password 才显示口令）
              import  从 CSV / 加密备份导入
              export  导出为 CSV / 加密备份

            远程连接
              exec    执行一条远程命令并打印输出与退出码
              ssh     调用系统 ssh.exe 打开交互式会话
              sftp    SFTP 列目录 / 下载 / 上传
              ftp     FTP 列目录 / 下载 / 上传
              rdp     通过 mstsc 打开远程桌面（cmdkey 注入凭据）
              sync    与自建服务端同步加密密码库

            全局选项
              --data-dir <目录>   覆盖数据目录（默认 %LOCALAPPDATA%\cx-ssh-client）
              --help              显示本帮助
              --version           显示版本号

            查看某个命令的详细用法：
              cx-ssh <命令> --help

            示例
              cx-ssh add --name web-1 --host 203.0.113.10 --user root --password 你的口令
              cx-ssh list
              cx-ssh exec web-1 "uname -a"
              cx-ssh sftp ls web-1 /root
              cx-ssh export D:\backup\sessions.csv
              cx-ssh sync --server https://sync.example.com:8443 --user 你的账号 --password 你的口令 --trust-self-signed
            """);
    }
}
