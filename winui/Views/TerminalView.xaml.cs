using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using CxSshClient.Models;
using CxSshClient.Services;

namespace CxSshClient.Views;

public sealed partial class TerminalView : UserControl, ISessionContent
{
    private readonly DispatcherQueue _dq;
    private SshSession? _ssh;
    private int _cols = 100;
    private int _rows = 30;
    private double _fontSize = 14;

    public SessionInfo Info { get; }
    public FrameworkElement View => this;
    public event Action<ISessionContent>? CloseRequested;
    public event Action<ISessionContent>? TitleChanged;

    public TerminalView(SessionInfo info)
    {
        Info = info;
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        TitleText.Text = info.DisplayTitle;
        Loaded += (_, _) => Web.Focus(FocusState.Programmatic);
    }

    public async Task StartAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            OverlayText.Text = "无法初始化 WebView2 运行时：\n" + ex.Message +
                               "\n\n请安装 Microsoft Edge WebView2 Runtime 后重试。";
            Overlay.Visibility = Visibility.Visible;
            return;
        }

        var core = Web.CoreWebView2;
        var www = Path.Combine(AppContext.BaseDirectory, "Assets", "www");
        core.SetVirtualHostNameToFolderMapping("nova.local", www, CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://nova.local/index.html");
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            switch (typeProp.GetString())
            {
                case "ready":
                    PostToTerminal("setfont", _fontSize);
                    ConnectSsh();
                    break;
                case "input":
                    var b64 = root.GetProperty("data").GetString() ?? "";
                    if (b64.Length > 0)
                    {
                        try { _ssh?.Write(Convert.FromBase64String(b64)); } catch { }
                    }
                    break;
                case "resize":
                    _cols = root.GetProperty("cols").GetInt32();
                    _rows = root.GetProperty("rows").GetInt32();
                    _ssh?.Resize((uint)Math.Max(20, _cols), (uint)Math.Max(5, _rows));
                    break;
            }
        }
        catch { }
    }

    private void ConnectSsh()
    {
        try { _ssh?.Dispose(); } catch { }
        SetState("正在连接…", "#E8912D");
        Overlay.Visibility = Visibility.Visible;
        OverlayText.Text = $"正在连接 {Info.Host}:{Info.Port} …";

        var session = new SshSession(Info);
        _ssh = session;
        session.Output += data => PostOutput(data);
        session.Status += msg => _dq.TryEnqueue(() => SetState(msg, "#E5484D"));
        session.Closed += () => _dq.TryEnqueue(() => SetState("连接已断开", "#E5484D"));

        Task.Run(() =>
        {
            try
            {
                session.Connect((uint)Math.Max(20, _cols), (uint)Math.Max(5, _rows));
                _dq.TryEnqueue(() =>
                {
                    Overlay.Visibility = Visibility.Collapsed;
                    SetState($"已连接 · {Info.Username}@{Info.Host}:{Info.Port}", "#2EA96F");
                    PostToTerminal("focus");
                    Web.Focus(FocusState.Programmatic);
                });
            }
            catch (Exception ex)
            {
                _dq.TryEnqueue(() =>
                {
                    Overlay.Visibility = Visibility.Collapsed;
                    SetState("连接失败: " + ex.Message, "#E5484D");
                    PostOutput(Encoding.UTF8.GetBytes($"\r\n\u001b[31m● 连接失败: {ex.Message}\u001b[0m\r\n"));
                });
            }
        });
    }

    private void SetState(string text, string colorHex)
    {
        StatusText.Text = text;
        var hex = colorHex.TrimStart('#');
        StateDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)));
    }

    private void PostOutput(byte[] data)
    {
        var json = JsonSerializer.Serialize(new { type = "output", data = Convert.ToBase64String(data) });
        _dq.TryEnqueue(() =>
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
        });
    }

    private void PostToTerminal(string type, double? size = null)
    {
        try
        {
            var json = size is null
                ? JsonSerializer.Serialize(new { type })
                : JsonSerializer.Serialize(new { type, size = (int)size.Value });
            Web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private void FontUp_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Min(28, _fontSize + 1);
        PostToTerminal("setfont", _fontSize);
        ApplyFont();
    }

    private void FontDown_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Max(9, _fontSize - 1);
        PostToTerminal("setfont", _fontSize);
        ApplyFont();
    }

    private void ApplyFont()
    {
        var settings = new AppSettings();
        settings.Load();
        settings.TerminalFontSize = _fontSize;
        settings.Save();
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        PostToTerminal("reset");
        ConnectSsh();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    public void Dispose()
    {
        try { _ssh?.Dispose(); } catch { }
        _ssh = null;
        try
        {
            if (Web.CoreWebView2 is not null)
            {
                Web.CoreWebView2.WebMessageReceived -= OnWebMessage;
                Web.CoreWebView2.Navigate("about:blank");
            }
        }
        catch { }
    }
}
