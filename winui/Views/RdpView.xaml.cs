using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CxSshClient.Models;
using CxSshClient.Services;

namespace CxSshClient.Views;

public sealed partial class RdpView : UserControl, ISessionContent
{
    private int _width = 1600;
    private int _height = 900;

    public SessionInfo Info { get; }
    public FrameworkElement View => this;
    public event Action<ISessionContent>? CloseRequested;
    public event Action<ISessionContent>? TitleChanged;

    public RdpView(SessionInfo info)
    {
        Info = info;
        InitializeComponent();
        TitleText.Text = info.DisplayTitle;
        AddressText.Text = string.IsNullOrWhiteSpace(info.Username)
            ? $"{info.Host}:{info.Port}"
            : $"{info.Username} @ {info.Host}:{info.Port}";
        LoadExtra();
    }

    private void LoadExtra()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Info.Extra);
            if (d is null) return;
            if (d.TryGetValue("fullscreen", out var fs) && (fs.ValueKind == JsonValueKind.True || fs.ValueKind == JsonValueKind.False))
                FullscreenSwitch.IsOn = fs.ValueKind == JsonValueKind.True;
            if (d.TryGetValue("admin", out var ad) && (ad.ValueKind == JsonValueKind.True || ad.ValueKind == JsonValueKind.False))
                AdminSwitch.IsOn = ad.ValueKind == JsonValueKind.True;
            if (d.TryGetValue("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var wi))
            {
                _width = wi;
                ResolutionBox.SelectedIndex = wi switch { 1280 => 0, 1600 => 1, 1920 => 2, 2560 => 3, _ => 1 };
            }
            else ResolutionBox.SelectedIndex = 1;
        }
        catch { ResolutionBox.SelectedIndex = 1; }
    }

    public async Task StartAsync()
    {
        // 打开标签页即为「要连接」，直接启动系统远程桌面
        await Task.Yield();
        Launch();
    }

    private void Launch()
    {
        try
        {
            RdpService.Launch(Info);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"已启动系统远程桌面: {Info.Host}:{Info.Port}（凭据已注入凭据管理器）";
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = "启动失败: " + ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        SaveExtra();
        Launch();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveExtra();
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = "设置已保存。";
        StatusBar.IsOpen = true;
    }

    private void SaveExtra()
    {
        var (w, h) = ResolutionBox.SelectedIndex switch
        {
            0 => (1280, 720),
            2 => (1920, 1080),
            3 => (2560, 1440),
            _ => (1600, 900)
        };
        _width = w; _height = h;
        var extra = new Dictionary<string, object>
        {
            ["fullscreen"] = FullscreenSwitch.IsOn,
            ["admin"] = AdminSwitch.IsOn,
            ["width"] = w,
            ["height"] = h
        };
        Info.Extra = JsonSerializer.Serialize(extra);
        Info.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        App.SaveSessionRequested?.Invoke(Info);
    }

    public void Dispose() { }
}
