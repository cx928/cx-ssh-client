using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using CxSshClient.Models;

namespace CxSshClient;

/// <summary>协议导航项</summary>
public class NavItem : INotifyPropertyChanged
{
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "";
    public SessionProtocol? Protocol { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
        }
    }

    public Brush Foreground => IsSelected
        ? new SolidColorBrush(Microsoft.UI.Colors.White)
        : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>会话内容视图的统一接口</summary>
public interface ISessionContent
{
    SessionInfo Info { get; }
    FrameworkElement View { get; }
    event Action<ISessionContent>? CloseRequested;
    event Action<ISessionContent>? TitleChanged;
    Task StartAsync();
    void Dispose();
}

/// <summary>打开的一个标签页</summary>
public class OpenTab : INotifyPropertyChanged
{
    public SessionInfo Session { get; set; } = new();
    public ISessionContent? Content { get; set; }

    private string _title = "";
    public string Title
    {
        get => _title;
        set { _title = value; Notify(nameof(Title)); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); Notify(nameof(HeaderBackground)); Notify(nameof(HeaderBorderBrush)); }
    }

    public string Glyph => ProtocolInfo.Glyph(Session.Protocol);

    public Brush AccentBrush
    {
        get
        {
            var hex = ProtocolInfo.Color(Session.Protocol).TrimStart('#');
            byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }
    }

    public Brush HeaderBackground => IsSelected
        ? ThemeBrush("AccentAcrylicBackgroundFillColorDefaultBrush", "AccentFillColorDefaultBrush")
        : ThemeBrush("SubtleFillColorSecondaryBrush", "CardBackgroundFillColorDefaultBrush");

    public Brush HeaderBorderBrush => IsSelected
        ? ThemeBrush("AccentFillColorDefaultBrush", "CardStrokeColorDefaultBrush")
        : ThemeBrush("CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush");

    private static Brush ThemeBrush(string key, string fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        if (Application.Current.Resources.TryGetValue(fallback, out var v2) && v2 is Brush b2) return b2;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
