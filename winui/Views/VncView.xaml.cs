using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using CxSshClient.Models;
using CxSshClient.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using VirtualKey = Windows.System.VirtualKey;

namespace CxSshClient.Views;

public sealed partial class VncView : UserControl, ISessionContent
{
    private readonly DispatcherQueue _dq;
    private VncSession? _session;
    private SoftwareBitmap? _bitmap;
    private SoftwareBitmapSource? _source;
    private int _renderQueued;
    private int _buttons;
    private bool _clipboardEnabled = true;
    private bool _viewOnly;

    public SessionInfo Info { get; }
    public FrameworkElement View => this;
    public event Action<ISessionContent>? CloseRequested;
    public event Action<ISessionContent>? TitleChanged;

    public VncView(SessionInfo info)
    {
        Info = info;
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        TitleText.Text = info.DisplayTitle;

        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(info.Extra);
            if (d is not null)
            {
                if (d.TryGetValue("readonly", out var ro) && ro.ValueKind == JsonValueKind.True)
                    _viewOnly = true;
                if (d.TryGetValue("clipboard", out var cb) &&
                    (cb.ValueKind == JsonValueKind.True || cb.ValueKind == JsonValueKind.False))
                    _clipboardEnabled = cb.ValueKind == JsonValueKind.True;
            }
        }
        catch { }
    }

    public async Task StartAsync()
    {
        await Task.Yield();
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        Overlay.Visibility = Visibility.Visible;
        OverlayText.Text = $"正在连接 {Info.Host}:{Info.Port} …";
        SetState("正在连接…", "#E8912D");
        try { _session?.Dispose(); } catch { }

        var session = new VncSession(Info.Host, Info.Port, Info.Password) { ViewOnly = _viewOnly };
        _session = session;
        session.Status += msg => _dq.TryEnqueue(() => SetState(msg, "#E8912D"));
        session.BellReceived += () => _dq.TryEnqueue(() => ShowToast("远程主机响铃", InfoBarSeverity.Informational));
        session.ClipboardReceived += text => _dq.TryEnqueue(() => OnRemoteClipboard(text));
        session.FramebufferChanged += OnFramebufferChanged;
        session.Closed += () => _dq.TryEnqueue(() =>
        {
            Overlay.Visibility = Visibility.Visible;
            OverlayText.Text = "连接已断开";
            SetState("连接已断开", "#E5484D");
        });

        try
        {
            await session.ConnectAsync();
            _dq.TryEnqueue(() =>
            {
                Overlay.Visibility = Visibility.Collapsed;
                ScreenHost.Focus(FocusState.Programmatic);
                SetState($"已连接 · {Info.Host}:{Info.Port} · {session.Width}×{session.Height}", "#2EA96F");
            });
        }
        catch (Exception ex)
        {
            Overlay.Visibility = Visibility.Visible;
            OverlayText.Text = "连接失败：" + ex.Message;
            SetState("连接失败: " + ex.Message, "#E5484D");
        }
    }

    // ================= 渲染 =================

    private void OnFramebufferChanged()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        _dq.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            RenderFrame();
        });
    }

    private void RenderFrame()
    {
        if (_session is null) return;
        var (buffer, w, h) = _session.GetFramebuffer();
        if (w <= 0 || h <= 0 || buffer.Length < w * h * 4) return;

        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap?.Dispose();
                _bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                _source = new SoftwareBitmapSource();
                ScreenImage.Source = _source;
            }
            _bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(buffer));
            _ = _source!.SetBitmapAsync(_bitmap);
        }
        catch { }
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_bitmap is null) return;
        switch (ZoomBox.SelectedIndex)
        {
            case 0: // 适应窗口
                ScreenImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                ScreenImage.Width = double.NaN;
                ScreenImage.Height = double.NaN;
                break;
            case 1:
                SetFixedZoom(1.0);
                break;
            case 2:
                SetFixedZoom(0.75);
                break;
            default:
                SetFixedZoom(0.5);
                break;
        }
    }

    private void SetFixedZoom(double zoom)
    {
        if (_bitmap is null) return;
        ScreenImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill;
        ScreenImage.Width = _bitmap.PixelWidth * zoom;
        ScreenImage.Height = _bitmap.PixelHeight * zoom;
    }

    private void Zoom_Changed(object sender, SelectionChangedEventArgs e) => ApplyZoom();

    // ================= 键鼠 =================

    private (int X, int Y) MapPoint(Windows.Foundation.Point p)
    {
        if (_bitmap is null || _session is null) return (0, 0);
        var (_, w, h) = _session.GetFramebuffer();
        var origin = ScreenImage.TransformToVisual(ScreenHost).TransformPoint(new Windows.Foundation.Point(0, 0));
        var dispW = ScreenImage.ActualWidth;
        var scale = dispW <= 0 ? 1 : dispW / w;
        var x = (int)((p.X - origin.X) / scale);
        var y = (int)((p.Y - origin.Y) / scale);
        return (Math.Clamp(x, 0, Math.Max(0, w - 1)), Math.Clamp(y, 0, Math.Max(0, h - 1)));
    }

    private void Screen_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        var pt = e.GetCurrentPoint(ScreenHost);
        if (pt.Properties.IsLeftButtonPressed) _buttons |= 1;
        if (pt.Properties.IsMiddleButtonPressed) _buttons |= 2;
        if (pt.Properties.IsRightButtonPressed) _buttons |= 4;
        var (x, y) = MapPoint(pt.Position);
        _session?.SendPointer(x, y, _buttons);
    }

    private void Screen_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ScreenHost);
        if (!pt.Properties.IsLeftButtonPressed) _buttons &= ~1;
        if (!pt.Properties.IsMiddleButtonPressed) _buttons &= ~2;
        if (!pt.Properties.IsRightButtonPressed) _buttons &= ~4;
        var (x, y) = MapPoint(pt.Position);
        _session?.SendPointer(x, y, _buttons);
    }

    private void Screen_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ScreenHost);
        var (x, y) = MapPoint(pt.Position);
        _session?.SendPointer(x, y, _buttons);
    }

    private void Screen_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ScreenHost);
        var delta = pt.Properties.MouseWheelDelta;
        var (x, y) = MapPoint(pt.Position);
        if (delta > 0)
        {
            _session?.SendPointer(x, y, _buttons | 8);
            _session?.SendPointer(x, y, _buttons);
        }
        else
        {
            _session?.SendPointer(x, y, _buttons | 16);
            _session?.SendPointer(x, y, _buttons);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var keysym = VncKeys.FromVirtualKey(e.Key);
        if (keysym == 0) return;
        _session?.SendKey(keysym, true);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var keysym = VncKeys.FromVirtualKey(e.Key);
        if (keysym == 0) return;
        _session?.SendKey(keysym, false);
        e.Handled = true;
    }

    private void SendCad_Click(object sender, RoutedEventArgs e)
    {
        _session?.SendKey(0xFFE3, true);  // Ctrl
        _session?.SendKey(0xFFE9, true);  // Alt
        _session?.SendKey(0xFFFF, true);  // Delete
        _session?.SendKey(0xFFFF, false);
        _session?.SendKey(0xFFE9, false);
        _session?.SendKey(0xFFE3, false);
        ShowToast("已发送 Ctrl+Alt+Del", InfoBarSeverity.Success);
    }

    private async void SendClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                _session?.SendClipboard(text);
                ShowToast("已把本机剪贴板内容发送到远程", InfoBarSeverity.Success);
            }
            else ShowToast("剪贴板中没有文本内容", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowToast("读取剪贴板失败: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnRemoteClipboard(string text)
    {
        if (_clipboardEnabled && !string.IsNullOrEmpty(text))
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(text);
                Clipboard.SetContent(dp);
                ShowToast("已同步远程剪贴板到本机", InfoBarSeverity.Informational);
            }
            catch { }
        }
    }

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Severity = severity;
        Toast.Message = message;
        Toast.IsOpen = true;
    }

    private void SetState(string text, string colorHex)
    {
        StatusText.Text = text;
        var hex = colorHex.TrimStart('#');
        StateDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255,
            byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)));
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private void Disconnect_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        ScreenImage.Source = null;
        try { _bitmap?.Dispose(); } catch { }
        _bitmap = null;
        _source = null;
    }
}
