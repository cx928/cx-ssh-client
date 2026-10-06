using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CxSshClient.Helpers;
using CxSshClient.Models;
using CxSshClient.Services;

namespace CxSshClient.Views;

public sealed partial class SettingsView : UserControl, ISessionContent
{
    private readonly MainWindow _main;
    private bool _loading = true;

    public SessionInfo Info { get; } = new() { Name = "设置", Protocol = SessionProtocol.Ssh };
    public FrameworkElement View => this;
    public event Action<ISessionContent>? CloseRequested;
    public event Action<ISessionContent>? TitleChanged;

    public SettingsView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        LoadFromConfig();
        _loading = false;
    }

    private void LoadFromConfig()
    {
        var cfg = _main.Sync.Config;
        ServerBox.Text = string.IsNullOrWhiteSpace(cfg.ServerUrl) ? "https://156.239.3.215:8443" : cfg.ServerUrl;
        UserBox.Text = cfg.Username;
        PassBox.Password = cfg.Password;
        TrustSwitch.IsOn = cfg.TrustSelfSigned;
        AutoSyncBox.SelectedIndex = cfg.AutoSyncMinutes switch
        {
            <= 0 => 0,
            1 => 1,
            5 => 2,
            15 => 3,
            30 => 4,
            _ => 2
        };

        ThemeButtons.SelectedIndex = _main.Settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        FontSizeBox.Value = _main.Settings.TerminalFontSize;

        try
        {
            if (DataDirText is not null)
                DataDirText.Text = VaultService.DataDirectory
                    + (VaultService.IsWritable(VaultService.DataDirectory) ? "" : "  ⚠️ 该目录不可写");
        }
        catch { }

        var when = cfg.LastSyncAt == 0
            ? "从未同步"
            : DateTimeOffset.FromUnixTimeMilliseconds(cfg.LastSyncAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        SyncStatusText.Text = $"最近同步: {when}    {cfg.LastSyncResult}";
    }

    public Task StartAsync() => Task.CompletedTask;

    private void CollectConfig()
    {
        var cfg = _main.Sync.Config;
        cfg.ServerUrl = ServerBox.Text?.Trim() ?? "";
        cfg.Username = UserBox.Text?.Trim() ?? "";
        cfg.Password = PassBox.Password;
        cfg.TrustSelfSigned = TrustSwitch.IsOn;
        cfg.AutoSyncMinutes = AutoSyncBox.SelectedIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 5,
            3 => 15,
            4 => 30,
            _ => 5
        };
        _main.Sync.Save();
        _main.OnSyncConfigChanged();
    }

    private async void SaveTest_Click(object sender, RoutedEventArgs e)
    {
        CollectConfig();
        ShowSync("正在测试连接…", "连接中", InfoBarSeverity.Informational);
        try
        {
            await _main.Sync.TestConnectionAsync();
            ShowSync("✅ 服务器连接正常，可以开始同步。", "测试成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowSync("❌ 连接失败: " + ex.Message + "\n请确认服务器地址、端口(8443)与防火墙。",
                "测试失败", InfoBarSeverity.Error);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        CollectConfig();
        ShowSync("正在同步…", "同步中", InfoBarSeverity.Informational);
        var result = await _main.Sync.SyncNowAsync();
        _main.RefreshSessions();
        LoadFromConfig();
        var ok = !result.Contains("失败") && !result.Contains("冲突");
        ShowSync(result, ok ? "同步完成" : "同步异常", ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void ShowSync(string message, string title, InfoBarSeverity severity)
    {
        SyncInfo.Title = title;
        SyncInfo.Message = message;
        SyncInfo.Severity = severity;
        SyncInfo.IsOpen = true;
    }

    private void ShowData(string message, string title, InfoBarSeverity severity)
    {
        DataInfo.Title = title;
        DataInfo.Message = message;
        DataInfo.Severity = severity;
        DataInfo.IsOpen = true;
    }

    // ================= 导入 / 导出 =================

    private async Task<string?> AskPassphraseAsync(string title, string hint)
    {
        var box = new PasswordBox { PlaceholderText = hint };
        var confirm = new PasswordBox { PlaceholderText = "再次输入以确认" };
        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "请设置一个备份口令（至少 6 位）。导入该文件时必须输入相同口令。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        panel.Children.Add(box);
        panel.Children.Add(confirm);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        if (box.Password.Length < 6)
        {
            ShowData("备份口令至少需要 6 位字符。", "口令太短", InfoBarSeverity.Warning);
            return null;
        }
        if (box.Password != confirm.Password)
        {
            ShowData("两次输入的口令不一致。", "口令不一致", InfoBarSeverity.Warning);
            return null;
        }
        return box.Password;
    }

    private async void ExportEncrypted_Click(object sender, RoutedEventArgs e)
    {
        var pass = await AskPassphraseAsync("导出加密备份", "备份口令");
        if (pass is null) return;
        var file = await FilePickerHelper.PickSaveFileAsync(
            $"cx-ssh-client-backup-{DateTime.Now:yyyyMMdd-HHmm}", ".json", "程星SSH客户端 加密备份");
        if (file is null) return;
        try
        {
            ImportExportService.ExportEncryptedJson(_main.Vault.Data, file.Path, pass);
            ShowData($"已导出 {_main.Vault.Data.Sessions.Count} 个会话到:\n{file.Path}",
                "导出成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowData("导出失败: " + ex.Message, "导出失败", InfoBarSeverity.Error);
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出为 CSV 明文",
            Content = new TextBlock
            {
                Text = "CSV 文件中的密码是明文，任何拿到文件的人都能读取。\n确定继续导出吗？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "仍然导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var file = await FilePickerHelper.PickSaveFileAsync(
            $"cx-ssh-client-{DateTime.Now:yyyyMMdd-HHmm}", ".csv", "CSV 表格");
        if (file is null) return;
        try
        {
            ImportExportService.ExportCsv(_main.Vault.Data, file.Path);
            ShowData($"已导出 {_main.Vault.Data.Sessions.Count} 个会话到:\n{file.Path}",
                "导出成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowData("导出失败: " + ex.Message, "导出失败", InfoBarSeverity.Error);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        await _main.RunImportAsync();
        LoadFromConfig();
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cx-ssh-client");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowData("打开目录失败: " + ex.Message, "错误", InfoBarSeverity.Error);
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空全部会话",
            Content = new TextBlock
            {
                Text = $"这将删除本机保存的全部 {_main.Vault.Data.Sessions.Count} 个会话（含密码），" +
                       "并在下次同步时同步到云端。此操作不可撤销，确定继续吗？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "全部删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var empty = new VaultData();
        _main.Vault.ReplaceAll(empty);
        _main.RefreshSessions();
        ShowData("已清空本机会话。", "已清空", InfoBarSeverity.Success);
    }

    // ================= 外观 =================

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _main.Settings.Theme = ThemeButtons.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system"
        };
        _main.Settings.Save();
        _main.ApplyTheme();
    }

    private void FontSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        _main.Settings.TerminalFontSize = args.NewValue;
        _main.Settings.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    public void Dispose() { }
}
