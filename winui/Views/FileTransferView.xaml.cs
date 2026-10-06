using System.Collections.ObjectModel;
using CxSshClient.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CxSshClient.Models;
using CxSshClient.Services;

namespace CxSshClient.Views;

public sealed partial class FileTransferView : UserControl, ISessionContent
{
    private readonly DispatcherQueue _dq;
    private readonly ObservableCollection<FileEntry> _localItems = new();
    private readonly ObservableCollection<FileEntry> _remoteItems = new();
    private readonly ObservableCollection<TransferItem> _transfers = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private IFileSession? _remote;
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _remotePath = "/";

    public SessionInfo Info { get; }
    public FrameworkElement View => this;
    public event Action<ISessionContent>? CloseRequested;
    public event Action<ISessionContent>? TitleChanged;

    public FileTransferView(SessionInfo info)
    {
        Info = info;
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        TitleText.Text = info.DisplayTitle;
        RemoteTitleText.Text = info.Protocol == SessionProtocol.Ftp ? "远程 (FTP)" : "远程 (SFTP)";
        RemoteProtoIcon.Foreground = new SolidColorBrush(
            ParseHex(ProtocolInfo.Color(info.Protocol)));
        LocalList.ItemsSource = _localItems;
        RemoteList.ItemsSource = _remoteItems;
        TransferList.ItemsSource = _transfers;
        LocalPathBox.Text = _localPath;
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber));
    }

    public async Task StartAsync()
    {
        await Task.Yield();
        await ConnectAsync();
        LoadLocal(_localPath);
    }

    private async Task ConnectAsync()
    {
        SetState("正在连接…", "#E8912D");
        BottomStatus.Text = $"正在连接 {Info.Host}:{Info.Port} …";
        try { _remote?.Dispose(); } catch { }
        _remote = null;

        var info = Info;
        try
        {
            var session = await Task.Run<IFileSession>(() =>
            {
                IFileSession s = info.Protocol == SessionProtocol.Ftp
                    ? new FtpFileSession(info)
                    : new SftpFileSession(info);
                s.Connect();
                return s;
            });
            _remote = session;
            _remotePath = session.InitialPath;
            RemotePathBox.Text = _remotePath;
            SetState($"已连接 · {Info.Username}@{Info.Host}:{Info.Port}", "#2EA96F");
            BottomStatus.Text = "已连接，可以开始传输文件";
            await LoadRemoteAsync();
        }
        catch (Exception ex)
        {
            SetState("连接失败: " + ex.Message, "#E5484D");
            BottomStatus.Text = "连接失败: " + ex.Message;
        }
    }

    private void SetState(string text, string colorHex)
    {
        StatusText.Text = text;
        StateDot.Fill = new SolidColorBrush(ParseHex(colorHex));
    }

    // ================= 本地 =================

    private void LoadLocal(string path)
    {
        try
        {
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(path))
            {
                BottomStatus.Text = "本地目录不存在: " + path;
                return;
            }
            _localPath = path;
            LocalPathBox.Text = path;
            var entries = new List<FileEntry>();
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                entries.Add(new FileEntry
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    IsDirectory = true,
                    Modified = SafeTime(() => Directory.GetLastWriteTime(dir))
                });
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                entries.Add(new FileEntry
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false,
                    Size = SafeLength(file),
                    Modified = SafeTime(() => File.GetLastWriteTime(file))
                });
            }
            _localItems.Clear();
            foreach (var e in entries
                         .OrderByDescending(x => x.IsDirectory)
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _localItems.Add(e);
        }
        catch (Exception ex)
        {
            BottomStatus.Text = "读取本地目录失败: " + ex.Message;
        }
    }

    private static DateTime SafeTime(Func<DateTime> f)
    {
        try { return f(); } catch { return default; }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    private void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_localPath)?.FullName;
        if (parent is not null) LoadLocal(parent);
    }

    private void LocalRefresh_Click(object sender, RoutedEventArgs e) => LoadLocal(_localPath);

    private void LocalPath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        LoadLocal(LocalPathBox.Text?.Trim() ?? _localPath);
    }

    private void LocalList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LocalList.SelectedItem is FileEntry f && f.IsDirectory) LoadLocal(f.FullPath);
    }

    // ================= 远程 =================

    private async Task LoadRemoteAsync(string? path = null)
    {
        if (_remote is null) return;
        var target = path ?? _remotePath;
        try
        {
            var items = await Task.Run(() => _remote.List(target));
            _remotePath = target;
            RemotePathBox.Text = target;
            _remoteItems.Clear();
            foreach (var i in items) _remoteItems.Add(i);
            BottomStatus.Text = $"{items.Count} 个项目 · {target}";
        }
        catch (Exception ex)
        {
            BottomStatus.Text = "读取远程目录失败: " + ex.Message;
        }
    }

    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e) => await LoadRemoteAsync();

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null) return;
        var p = _remotePath.Replace('\\', '/').TrimEnd('/');
        var idx = p.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : p[..idx];
        await LoadRemoteAsync(parent);
    }

    private async void RemotePath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        await LoadRemoteAsync(RemotePathBox.Text?.Trim());
    }

    private async void RemoteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileEntry f) return;
        if (f.IsDirectory) await LoadRemoteAsync(f.FullPath);
        else await DownloadEntryAsync(f);
    }

    private async Task DownloadEntryAsync(FileEntry entry)
    {
        if (_remote is null) return;
        await RunTransferAsync(entry, false);
        LoadLocal(_localPath);
    }

    private async void RemoteMkdir_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null) return;
        var box = new TextBox { PlaceholderText = "新文件夹名称" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新建远程文件夹",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var name = box.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var full = JoinRemote(_remotePath, name);
            await Task.Run(() => _remote.CreateDirectory(full));
            await LoadRemoteAsync();
            BottomStatus.Text = "已创建目录: " + full;
        }
        catch (Exception ex)
        {
            BottomStatus.Text = "创建失败: " + ex.Message;
        }
    }

    private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null || RemoteList.SelectedItems.Count == 0) return;
        List<FileEntry> targets = RemoteList.SelectedItems.OfType<FileEntry>().ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除远程项目",
            Content = $"确定删除选中的 {targets.Count()} 个项目吗？此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await SafeUi.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        foreach (var t in targets)
        {
            try { await Task.Run(() => _remote.Delete(t.FullPath, t.IsDirectory)); }
            catch (Exception ex) { BottomStatus.Text = $"删除 {t.Name} 失败: {ex.Message}"; }
        }
        await LoadRemoteAsync();
    }

    // ================= 传输 =================

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null)
        {
            BottomStatus.Text = "尚未连接远程主机";
            return;
        }
        List<FileEntry> files = LocalList.SelectedItems.OfType<FileEntry>().Where(f => !f.IsDirectory).ToList();
        if (files.Count() == 0)
        {
            BottomStatus.Text = "请先在左侧选中要上传的文件（文件夹暂不支持递归上传）";
            return;
        }
        foreach (var f in files) await RunTransferAsync(f, true);
        await LoadRemoteAsync();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null)
        {
            BottomStatus.Text = "尚未连接远程主机";
            return;
        }
        List<FileEntry> files = RemoteList.SelectedItems.OfType<FileEntry>().Where(f => !f.IsDirectory).ToList();
        if (files.Count() == 0)
        {
            BottomStatus.Text = "请先在右侧选中要下载的文件";
            return;
        }
        foreach (var f in files) await RunTransferAsync(f, false);
        LoadLocal(_localPath);
    }

    private async Task RunTransferAsync(FileEntry entry, bool upload)
    {
        var item = new TransferItem
        {
            Name = entry.Name,
            IsUpload = upload,
            Detail = upload
                ? $"{entry.FullPath} → {JoinRemote(_remotePath, entry.Name)}"
                : $"{entry.FullPath} → {Path.Combine(_localPath, entry.Name)}",
            StatusText = "0%"
        };
        _transfers.Insert(0, item);
        TransferPanel.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p =>
        {
            var pct = Math.Clamp(p * 100, 0, 100);
            item.Progress = pct;
            item.StatusText = $"{pct:0}%";
        });

        try
        {
            await _ioLock.WaitAsync();
            await Task.Run(() =>
            {
                if (upload)
                    _remote!.Upload(entry.FullPath, JoinRemote(_remotePath, entry.Name), progress, _cts.Token);
                else
                    _remote!.Download(entry.FullPath, Path.Combine(_localPath, entry.Name), progress, _cts.Token);
            });
            item.Progress = 100;
            item.StatusText = "完成";
            BottomStatus.Text = (upload ? "上传完成: " : "下载完成: ") + entry.Name;
        }
        catch (Exception ex)
        {
            item.StatusText = "失败";
            item.Detail = ex.Message;
            BottomStatus.Text = "传输失败: " + ex.Message;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static string JoinRemote(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return name;
        dir = dir.Replace('\\', '/');
        if (dir.EndsWith('/')) return dir + name;
        return dir + "/" + name;
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _transfers.Where(t => t.StatusText is "完成" or "失败").ToList())
            _transfers.Remove(item);
        if (_transfers.Count == 0) TransferPanel.Visibility = Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLocal(_localPath);
        await LoadRemoteAsync();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
        LoadLocal(_localPath);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _remote?.Dispose(); } catch { }
        _remote = null;
    }
}
