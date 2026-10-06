using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace CxSshClient.Models;

public enum SessionProtocol
{
    Ssh, Sftp, Rdp, Ftp, Vnc
}

public static class ProtocolInfo
{
    public static string Glyph(SessionProtocol p) => p switch
    {
        SessionProtocol.Ssh => "\uE8C8",
        SessionProtocol.Sftp => "\uE8B7",
        SessionProtocol.Rdp => "\uE7F8",
        SessionProtocol.Ftp => "\uE896",
        SessionProtocol.Vnc => "\uE7ED",
        _ => "\uE71D"
    };

    public static string Color(SessionProtocol p) => p switch
    {
        SessionProtocol.Ssh => "#2EA96F",
        SessionProtocol.Sftp => "#8B5CF6",
        SessionProtocol.Rdp => "#2E7BD6",
        SessionProtocol.Ftp => "#E8912D",
        SessionProtocol.Vnc => "#E5484D",
        _ => "#6B7280"
    };

    public static int DefaultPort(SessionProtocol p) => p switch
    {
        SessionProtocol.Ssh => 22,
        SessionProtocol.Sftp => 22,
        SessionProtocol.Rdp => 3389,
        SessionProtocol.Ftp => 21,
        SessionProtocol.Vnc => 5900,
        _ => 22
    };

    public static string Label(SessionProtocol p) => p switch
    {
        SessionProtocol.Ssh => "SSH",
        SessionProtocol.Sftp => "SFTP",
        SessionProtocol.Rdp => "RDP",
        SessionProtocol.Ftp => "FTP",
        SessionProtocol.Vnc => "VNC",
        _ => ""
    };
}

/// <summary>一个远程会话(账号)。所有字段随密码库整体加密存储。</summary>
public class SessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public SessionProtocol Protocol { get; set; } = SessionProtocol.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string Note { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>协议扩展设置(JSON 字符串)</summary>
    public string Extra { get; set; } = "{}";

    public SessionInfo Clone() => (SessionInfo)MemberwiseClone();

    // ---- 以下均为界面用的计算属性, 不参与序列化 ----
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    [JsonIgnore]
    public string ProtocolGlyph => ProtocolInfo.Glyph(Protocol);

    [JsonIgnore]
    public string ProtocolLabel => ProtocolInfo.Label(Protocol);

    [JsonIgnore]
    public Brush ProtocolBrush => MakeBrush(255);

    [JsonIgnore]
    public Brush ProtocolSoftBrush => MakeBrush(38);

    private Brush MakeBrush(byte alpha)
    {
        var hex = ProtocolInfo.Color(Protocol).TrimStart('#');
        byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, r, g, b));
    }

    [JsonIgnore]
    public string Address => $"{Host}:{Port}";

    [JsonIgnore]
    public string SubTitle =>
        string.IsNullOrWhiteSpace(Username) ? Address : $"{Username}@{Address}";
}
