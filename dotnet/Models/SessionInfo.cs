using System.Text.Json.Serialization;

namespace CxSshClient.Cli.Models;

/// <summary>
/// 协议类型。数值必须与图形版 (NovaRemote) 的 SessionProtocol 完全一致，
/// 因为该枚举数值会直接写入 vault.dat / 同步载荷的 JSON。
/// </summary>
public enum SessionProtocol
{
    Ssh = 0,
    Sftp = 1,
    Rdp = 2,
    Ftp = 3,
    Vnc = 4
}

/// <summary>协议元数据：标签 / 默认端口 / 解析。与图形版 ProtocolInfo 保持一致。</summary>
public static class ProtocolInfo
{
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
        _ => "?"
    };

    /// <summary>解析协议名（大小写不敏感，容忍 ftps/终端 等别名）。</summary>
    public static bool TryParse(string? text, out SessionProtocol protocol)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "ssh": protocol = SessionProtocol.Ssh; return true;
            case "sftp": protocol = SessionProtocol.Sftp; return true;
            case "rdp": protocol = SessionProtocol.Rdp; return true;
            case "ftp":
            case "ftps": protocol = SessionProtocol.Ftp; return true;
            case "vnc": protocol = SessionProtocol.Vnc; return true;
            default: protocol = SessionProtocol.Ssh; return false;
        }
    }

    public static string AllNames => "ssh | sftp | rdp | ftp | vnc";
}

/// <summary>
/// 一个远程会话（账号）。字段名与顺序严格对齐图形版 Models/SessionInfo.cs，
/// 保证同一个 vault.dat 可被 CLI 与图形版互相读写。
/// </summary>
public class SessionInfo
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Group")]
    public string Group { get; set; } = "";

    [JsonPropertyName("Protocol")]
    public SessionProtocol Protocol { get; set; } = SessionProtocol.Ssh;

    [JsonPropertyName("Host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("Username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("Password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("PrivateKeyPath")]
    public string PrivateKeyPath { get; set; } = "";

    [JsonPropertyName("Note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("CreatedAt")]
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("UpdatedAt")]
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>协议扩展设置（JSON 字符串）</summary>
    [JsonPropertyName("Extra")]
    public string Extra { get; set; } = "{}";

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    [JsonIgnore]
    public string Address => $"{Host}:{Port}";

    public SessionInfo Clone() => (SessionInfo)MemberwiseClone();
}
