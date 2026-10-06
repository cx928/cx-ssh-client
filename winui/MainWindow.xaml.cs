using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CxSshClient.Helpers;
using CxSshClient.Models;
using CxSshClient.Services;
using CxSshClient.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace CxSshClient;

public sealed partial class MainWindow : Window
{
    private readonly VaultService _vault = new();
    private readonly SyncService _sync;
    private readonly ObservableCollection<SessionInfo> _viewSessions = new();
    private readonly ObservableCollection<NavItem> _navItems = new();
    private readonly ObservableCollection<OpenTab> _tabs = new();
    private readonly DispatcherQueue _dq;
    private DispatcherQueueTimer? _syncTimer;
    private AppWindow? _appWindow;
    private bool _positioned;
    private SessionProtocol? _filter;
    private string _search = "";

    public AppSettings Settings { get; } = new();
    public VaultService Vault => _vault;
    public SyncService Sync => _sync;

    public MainWindow()
    {
        InitializeComponent();
        Title = "程星SSH客户端";
        _dq = DispatcherQueue.GetForCurrentThread();

        Settings.Load();
        ApplyTheme();
        LoadAppIcon();

        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        var hwnd = WindowNative.GetWindowHandle(this);
        var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(wid);
        try
        {
            var tb = _appWindow.TitleBar;
            tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }
        catch { }
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) _appWindow.SetIcon(ico);
        }
        catch (Exception ex) { Log.Write("SetIcon", ex); }
        try { PositionWindow(wid); } catch { }

        // 窗口显示后再定位一次 (部分环境首次 Move 会被系统覆盖)
        Activated += (_, _) =>
        {
            if (_positioned || _appWindow is null) return;
            _positioned = true;
            try { PositionWindow(_appWindow.Id); } catch { }
        };

        _sync = new SyncService(_vault);
        _vault.Load();
        _sync.Load();
        _sync.StatusChanged += msg => _dq.TryEnqueue(() => UpdateSyncStatus(msg));

        // 密码库写入失败(如目录不可写)时明确提示, 避免用户以为已保存
        VaultService.SaveFailed += msg => _dq.TryEnqueue(() =>
        {
            SetStatus(msg, false);
            WelcomeSyncText.Text = "⚠️ " + msg;
        });

        App.SaveSessionRequested = session =>
        {
            _vault.UpsertSession(session);
            RefreshSessions();
        };

        NavList.ItemsSource = _navItems;
        SessionsList.ItemsSource = _viewSessions;
        TabsControl.ItemsSource = _tabs;

        BuildNav();
        RefreshSessions();
        SetupSyncTimer();
        UpdateSyncStatus(_sync.Config.LastSyncResult);
        UpdateTabUi();

        Closed += (_, _) => Cleanup();
    }

    // ================= 初始化辅助 =================

    /// <summary>把应用图标载入标题栏与欢迎页 (unpackaged 下用 file:// 最稳)</summary>
    private void LoadAppIcon()
    {
        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            if (!File.Exists(png)) return;
            var uri = new Uri("file:///" + png.Replace('\\', '/'));
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            if (TitleBarIcon is not null) TitleBarIcon.Source = bmp;
            if (WelcomeIcon is not null) WelcomeIcon.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Write("LoadAppIcon", ex);
        }
    }

    /// <summary>恢复上次窗口位置与大小; 首次启动则居中显示</summary>
    private void PositionWindow(Microsoft.UI.WindowId wid)
    {
        if (_appWindow is null) return;
        var area = DisplayArea.GetFromWindowId(wid, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        var w = Settings.WindowWidth >= 880 ? Math.Min(Settings.WindowWidth, work.Width) : Math.Min(1260, Math.Max(880, work.Width - 60));
        var h = Settings.WindowHeight >= 600 ? Math.Min(Settings.WindowHeight, work.Height) : Math.Min(860, Math.Max(600, work.Height - 60));

        var x = Settings.WindowX != int.MinValue
            ? Settings.WindowX
            : work.X + Math.Max(0, (work.Width - w) / 2);
        var y = Settings.WindowY != int.MinValue
            ? Settings.WindowY
            : work.Y + Math.Max(0, (work.Height - h) / 2);

        // 保证窗口标题栏始终可见 (防止上次关闭后显示器变化导致窗口跑到屏幕外)
        x = Math.Clamp(x, work.X - 40, work.X + Math.Max(0, work.Width - 240));
        y = Math.Clamp(y, work.Y - 10, work.Y + Math.Max(0, work.Height - 120));

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public void ApplyTheme()
    {
        if (RootGrid is not null)
            RootGrid.RequestedTheme = Settings.ElementTheme;
    }

    private void BuildNav()
    {
        _navItems.Clear();
        _navItems.Add(new NavItem { Title = "全部", Glyph = "\uE71D", Protocol = null });
        _navItems.Add(new NavItem { Title = "SSH", Glyph = "\uE756", Protocol = SessionProtocol.Ssh });
        _navItems.Add(new NavItem { Title = "SFTP", Glyph = "\uE8B7", Protocol = SessionProtocol.Sftp });
        _navItems.Add(new NavItem { Title = "FTP", Glyph = "\uE896", Protocol = SessionProtocol.Ftp });
        _navItems.Add(new NavItem { Title = "RDP", Glyph = "\uE7F8", Protocol = SessionProtocol.Rdp });
        _navItems.Add(new NavItem { Title = "VNC", Glyph = "\uE7F4", Protocol = SessionProtocol.Vnc });
        NavList.SelectedIndex = 0;
    }

    private void SetupSyncTimer()
    {
        _syncTimer?.Stop();
        var minutes = _sync.Config.AutoSyncMinutes;
        if (minutes <= 0) return;
        _syncTimer = _dq.CreateTimer();
        _syncTimer.Interval = TimeSpan.FromMinutes(minutes);
        _syncTimer.Tick += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_sync.Config.Username) ||
                string.IsNullOrWhiteSpace(_sync.Config.Password)) return;
            await _sync.SyncNowAsync();
            _dq.TryEnqueue(RefreshSessions);
        };
        _syncTimer.Start();
    }

    // ================= 会话列表 =================

    public void RefreshSessions()
    {
        var all = _vault.Data.Sessions
            .Where(s => _filter is null || s.Protocol == _filter)
            .Where(s =>
                string.IsNullOrWhiteSpace(_search) ||
                s.DisplayTitle.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                s.Host.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                s.Username.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                s.Group.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Protocol)
            .ThenBy(s => s.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _viewSessions.Clear();
        foreach (var s in all) _viewSessions.Add(s);

        var label = _filter is null ? "全部会话" : $"{ProtocolInfo.Label(_filter.Value)} 会话";
        ListHeaderText.Text = label;
        ListSubText.Text = $"{all.Count} 个连接";
        VaultCountText.Text = $"库: {_vault.Data.Sessions.Count}";
        if (EmptyHint is not null)
            EmptyHint.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item) return;
        foreach (var n in _navItems) n.IsSelected = ReferenceEquals(n, item);
        _filter = item.Protocol;
        RefreshSessions();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? "";
        RefreshSessions();
    }

    // ================= 标签页 =================

    private async void OpenSession(SessionInfo s)
    {
        var existing = _tabs.FirstOrDefault(t => t.Session.Id == s.Id);
        if (existing is not null && existing.Content is not null)
        {
            SelectTab(existing);
            return;
        }

        ISessionContent content = s.Protocol switch
        {
            SessionProtocol.Ssh => new TerminalView(s),
            SessionProtocol.Sftp => new FileTransferView(s),
            SessionProtocol.Ftp => new FileTransferView(s),
            SessionProtocol.Rdp => new RdpView(s),
            SessionProtocol.Vnc => new VncView(s),
            _ => new TerminalView(s)
        };

        content.CloseRequested += c =>
        {
            var t = _tabs.FirstOrDefault(x => ReferenceEquals(x.Content, c));
            if (t is not null) CloseTab(t);
        };
        content.TitleChanged += c =>
        {
            var t = _tabs.FirstOrDefault(x => ReferenceEquals(x.Content, c));
            if (t is not null) t.Title = c.Info.DisplayTitle;
        };

        var tab = new OpenTab { Session = s, Content = content, Title = s.DisplayTitle };
        content.View.Visibility = Visibility.Collapsed;
        ContentHost.Children.Add(content.View);
        _tabs.Add(tab);
        SelectTab(tab);
        UpdateTabUi();

        try
        {
            await content.StartAsync();
        }
        catch (Exception ex)
        {
            SetStatus("连接失败: " + ex.Message, false);
            await ShowInfoAsync("连接失败", ex.Message);
        }
    }

    private void SelectTab(OpenTab? tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = ReferenceEquals(t, tab);
            if (t.Content is not null)
                t.Content.View.Visibility = t.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        }
        WelcomePanel.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseTab(OpenTab? tab)
    {
        if (tab is null) return;
        var idx = _tabs.IndexOf(tab);
        try { tab.Content?.Dispose(); } catch { }
        if (tab.Content is not null) ContentHost.Children.Remove(tab.Content.View);
        _tabs.Remove(tab);
        if (_tabs.Count == 0) SelectTab(null);
        else SelectTab(_tabs[Math.Clamp(idx, 0, _tabs.Count - 1)]);
        UpdateTabUi();
    }

    private void UpdateTabUi()
    {
        TabCountText.Text = $"{_tabs.Count} 个会话打开";
        WelcomePanel.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabChip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OpenTab tab) SelectTab(tab);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is OpenTab tab) CloseTab(tab);
    }

    // ================= 会话增删改 =================

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SessionEditDialog(null) { XamlRoot = RootGrid.XamlRoot };
        if (await SafeUi.ShowAsync(dialog) == ContentDialogResult.Primary && dialog.Result is not null)
        {
            _vault.UpsertSession(dialog.Result);
            RefreshSessions();
            SetStatus($"已保存会话: {dialog.Result.DisplayTitle}", true);
        }
    }

    private async void CardEdit_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not SessionInfo s) return;
        var dialog = new SessionEditDialog(s) { XamlRoot = RootGrid.XamlRoot };
        if (await SafeUi.ShowAsync(dialog) == ContentDialogResult.Primary && dialog.Result is not null)
        {
            _vault.UpsertSession(dialog.Result);
            RefreshSessions();
            foreach (var t in _tabs.Where(t => t.Session.Id == s.Id))
            {
                t.Session = dialog.Result;
                t.Title = dialog.Result.DisplayTitle;
            }
        }
    }

    private void CardDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not SessionInfo s) return;
        var copy = s.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = s.DisplayTitle + " 副本";
        _vault.UpsertSession(copy);
        RefreshSessions();
    }

    private async void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not SessionInfo s) return;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "删除会话",
            Content = $"确定删除「{s.DisplayTitle}」吗？该操作会同步到云端。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await SafeUi.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            foreach (var t in _tabs.Where(t => t.Session.Id == s.Id).ToList()) CloseTab(t);
            _vault.DeleteSession(s.Id);
            RefreshSessions();
            SetStatus($"已删除: {s.DisplayTitle}", true);
        }
    }

    private void CardConnect_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is SessionInfo s) OpenSession(s);
    }

    private void SessionsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is SessionInfo s) OpenSession(s);
    }

    private static SessionInfo? TagOf(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.Tag is SessionInfo s1) return s1;
            if (fe.DataContext is SessionInfo s2) return s2;
        }
        return null;
    }

    // ================= 导入 / 导出 =================

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunImportAsync();

    public async Task RunImportAsync()
    {
        var dialog = new ImportDialog { XamlRoot = RootGrid.XamlRoot };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            if (dialog.Mode == ImportMode.Csv)
            {
                var file = await FilePickerHelper.PickOpenFileAsync(".csv", ".txt");
                if (file is null) return;
                var tmp = Path.Combine(Path.GetTempPath(), "nova_import_" + Guid.NewGuid().ToString("N") + ".csv");
                var text = await File.ReadAllTextAsync(file.Path);
                await File.WriteAllTextAsync(tmp, text);
                var list = ImportExportService.ImportCsv(tmp, out var warning);
                File.Delete(tmp);
                var n = _vault.ImportSessions(list);
                RefreshSessions();
                await ShowInfoAsync("导入完成", $"成功导入 {n} 个会话。{(string.IsNullOrEmpty(warning) ? "" : "\n" + warning)}");
            }
            else
            {
                var file = await FilePickerHelper.PickOpenFileAsync(".json", ".novakey");
                if (file is null) return;
                var data = ImportExportService.ImportEncryptedJson(file.Path, dialog.Passphrase);
                var n = _vault.ImportSessions(data.Sessions);
                RefreshSessions();
                await ShowInfoAsync("导入完成", $"成功导入 {n} 个会话（加密文件）。");
            }
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("导入失败", ex.Message);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sync.Config.Username) || string.IsNullOrWhiteSpace(_sync.Config.Password))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "尚未配置账号同步",
                Content = "是否现在前往设置？",
                PrimaryButtonText = "去设置",
                CloseButtonText = "取消"
            };
            if (await SafeUi.ShowAsync(dialog) == ContentDialogResult.Primary) OpenSettings();
            return;
        }

        SetStatus("正在同步…", true);
        var result = await _sync.SyncNowAsync();
        RefreshSessions();
        SetStatus(result, !result.Contains("失败") && !result.Contains("冲突"));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var existing = _tabs.FirstOrDefault(t => t.Content is SettingsView);
        if (existing is not null) { SelectTab(existing); return; }

        var view = new SettingsView(this);
        view.CloseRequested += c =>
        {
            var t = _tabs.FirstOrDefault(x => ReferenceEquals(x.Content, c));
            if (t is not null) CloseTab(t);
        };
        var tab = new OpenTab
        {
            Session = new SessionInfo { Name = "设置", Protocol = SessionProtocol.Ssh },
            Content = view,
            Title = "设置"
        };
        view.View.Visibility = Visibility.Collapsed;
        ContentHost.Children.Add(view.View);
        _tabs.Add(tab);
        SelectTab(tab);
        UpdateTabUi();
    }

    // ================= 状态 / 提示 =================

    public void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(ok
            ? Windows.UI.Color.FromArgb(255, 46, 169, 111)
            : Windows.UI.Color.FromArgb(255, 229, 72, 77));
    }

    private void UpdateSyncStatus(string message)
    {
        var cfg = _sync.Config;
        var configured = !string.IsNullOrWhiteSpace(cfg.Username) && !string.IsNullOrWhiteSpace(cfg.Password);
        SyncDot.Fill = new SolidColorBrush(configured
            ? Windows.UI.Color.FromArgb(255, 46, 169, 111)
            : Windows.UI.Color.FromArgb(255, 150, 150, 150));
        var when = cfg.LastSyncAt == 0
            ? "未同步"
            : DateTimeOffset.FromUnixTimeMilliseconds(cfg.LastSyncAt).ToLocalTime().ToString("MM-dd HH:mm");
        VaultSyncText.Text = configured ? $"同步: {when}" : "未配置同步";
        WelcomeSyncText.Text = configured
            ? $"账号同步: {cfg.Username} @ {cfg.ServerUrl}  ·  最近 {when}"
            : "账号同步: 未配置 (点击左侧同步图标设置)";
        if (!string.IsNullOrEmpty(message)) StatusText.Text = message;
    }

    public async Task ShowInfoAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了"
        };
        await SafeUi.ShowAsync(dialog);
    }

    public async Task<bool> ConfirmAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await SafeUi.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public void OnSyncConfigChanged()
    {
        SetupSyncTimer();
        UpdateSyncStatus(_sync.Config.LastSyncResult);
    }

    private void Cleanup()
    {
        _syncTimer?.Stop();
        try
        {
            if (_appWindow is not null)
            {
                Settings.WindowX = _appWindow.Position.X;
                Settings.WindowY = _appWindow.Position.Y;
                Settings.WindowWidth = _appWindow.Size.Width;
                Settings.WindowHeight = _appWindow.Size.Height;
                Settings.Save();
            }
        }
        catch { }
        foreach (var t in _tabs.ToList())
        {
            try { t.Content?.Dispose(); } catch { }
        }
    }
}
