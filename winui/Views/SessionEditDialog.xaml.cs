using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CxSshClient.Helpers;
using CxSshClient.Models;

namespace CxSshClient.Views;

public sealed partial class SessionEditDialog : ContentDialog
{
    private readonly SessionInfo? _original;
    public SessionInfo? Result { get; private set; }

    public SessionEditDialog(SessionInfo? existing)
    {
        InitializeComponent();
        _original = existing;

        if (existing is null)
        {
            Title = "新建会话";
            HostBox.Text = "";
            PortBox.Text = "22";
        }
        else
        {
            Title = "编辑会话";
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            UserBox.Text = existing.Username;
            PassBox.Password = existing.Password;
            KeyBox.Text = existing.PrivateKeyPath;
            GroupBox.Text = existing.Group;
            NoteBox.Text = existing.Note;
        }

        var protocol = existing?.Protocol ?? SessionProtocol.Ssh;
        ProtocolBox.SelectedIndex = protocol switch
        {
            SessionProtocol.Ssh => 0,
            SessionProtocol.Sftp => 1,
            SessionProtocol.Ftp => 2,
            SessionProtocol.Rdp => 3,
            SessionProtocol.Vnc => 4,
            _ => 0
        };

        LoadExtra(existing);
        UpdatePanels(protocol);
    }

    private SessionProtocol SelectedProtocol =>
        (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "sftp" => SessionProtocol.Sftp,
            "ftp" => SessionProtocol.Ftp,
            "rdp" => SessionProtocol.Rdp,
            "vnc" => SessionProtocol.Vnc,
            _ => SessionProtocol.Ssh
        };

    private void LoadExtra(SessionInfo? existing)
    {
        var extra = new Dictionary<string, JsonElement>();
        if (existing is not null)
        {
            try { extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.Extra) ?? extra; }
            catch { }
        }

        bool GetBool(string k, bool def)
        {
            if (extra.TryGetValue(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return def;
        }
        int GetInt(string k, int def)
        {
            if (extra.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            return def;
        }
        string GetStr(string k, string def)
        {
            if (extra.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? def;
            return def;
        }

        FullscreenSwitch.IsOn = GetBool("fullscreen", true);
        AdminSwitch.IsOn = GetBool("admin", false);
        ReadOnlySwitch.IsOn = GetBool("readonly", false);
        ClipboardSwitch.IsOn = GetBool("clipboard", true);

        var w = GetInt("width", 1600);
        ResolutionBox.SelectedIndex = w switch
        {
            1280 => 0,
            1600 => 1,
            1920 => 2,
            2560 => 3,
            _ => 1
        };

        FtpEncryptionBox.SelectedIndex = GetStr("encryption", "none") switch
        {
            "explicit" => 1,
            "implicit" => 2,
            _ => 0
        };
        FtpModeBox.SelectedIndex = GetStr("mode", "passive") == "active" ? 1 : 0;
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is null) return;
        var proto = SelectedProtocol;
        UpdatePanels(proto);

        var current = int.TryParse(PortBox.Text, out var p) ? p : 0;
        var defaults = new[] { 22, 22, 21, 3389, 5900 };
        var isDefault = defaults.Contains(current);
        if (isDefault || current == 0)
            PortBox.Text = ProtocolInfo.DefaultPort(proto).ToString();
    }

    private void UpdatePanels(SessionProtocol proto)
    {
        if (DesktopPanel is null || FtpPanel is null || KeyPanel is null) return;
        var isDesktop = proto is SessionProtocol.Rdp or SessionProtocol.Vnc;
        DesktopPanel.Visibility = isDesktop ? Visibility.Visible : Visibility.Collapsed;
        FtpPanel.Visibility = proto == SessionProtocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = proto is SessionProtocol.Ssh or SessionProtocol.Sftp
            ? Visibility.Visible : Visibility.Collapsed;

        var isRdp = proto == SessionProtocol.Rdp;
        FullscreenSwitch.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
        AdminSwitch.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
        ResolutionBox.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlySwitch.Visibility = isRdp ? Visibility.Collapsed : Visibility.Visible;
        ClipboardSwitch.Visibility = isRdp ? Visibility.Collapsed : Visibility.Visible;
        DesktopTitle.Text = isRdp ? "RDP 桌面选项" : "VNC 桌面选项";
    }

    private void ShowPass_Changed(object sender, RoutedEventArgs e)
    {
        PassBox.PasswordRevealMode = ShowPassCheck.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Peek;
    }

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var file = await FilePickerHelper.PickOpenFileAsync(".pem", ".key", ".ppk", "*");
        if (file is not null) KeyBox.Text = file.Path;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ErrorBar.Message = "请填写主机地址 / IP";
            ErrorBar.IsOpen = true;
            args.Cancel = true;
            return;
        }
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port <= 0 || port > 65535)
        {
            ErrorBar.Message = "端口无效 (1-65535)";
            ErrorBar.IsOpen = true;
            args.Cancel = true;
            return;
        }

        var proto = SelectedProtocol;
        var session = _original?.Clone() ?? new SessionInfo();
        session.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text.Trim() : NameBox.Text.Trim();
        session.Protocol = proto;
        session.Host = HostBox.Text.Trim();
        session.Port = port;
        session.Username = UserBox.Text.Trim();
        session.Password = PassBox.Password;
        session.PrivateKeyPath = KeyBox.Text?.Trim() ?? "";
        session.Group = GroupBox.Text?.Trim() ?? "";
        session.Note = NoteBox.Text?.Trim() ?? "";

        var extra = new Dictionary<string, object>();
        if (proto == SessionProtocol.Rdp)
        {
            extra["fullscreen"] = FullscreenSwitch.IsOn;
            extra["admin"] = AdminSwitch.IsOn;
            var (w, h) = ResolutionBox.SelectedIndex switch
            {
                0 => (1280, 720),
                2 => (1920, 1080),
                3 => (2560, 1440),
                _ => (1600, 900)
            };
            extra["width"] = w;
            extra["height"] = h;
        }
        else if (proto == SessionProtocol.Vnc)
        {
            extra["readonly"] = ReadOnlySwitch.IsOn;
            extra["clipboard"] = ClipboardSwitch.IsOn;
        }
        else if (proto == SessionProtocol.Ftp)
        {
            extra["encryption"] = FtpEncryptionBox.SelectedIndex switch
            {
                1 => "explicit",
                2 => "implicit",
                _ => "none"
            };
            extra["mode"] = FtpModeBox.SelectedIndex == 1 ? "active" : "passive";
        }
        session.Extra = JsonSerializer.Serialize(extra);
        session.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_original is not null) session.CreatedAt = _original.CreatedAt;

        Result = session;
    }
}
