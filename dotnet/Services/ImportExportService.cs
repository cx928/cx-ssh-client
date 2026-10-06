using System.Text;
using System.Text.Json;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>加密备份文件结构：{"V":1,"Salt":"base64","Data":"base64(iv|tag|cipher)"}</summary>
public class ExportFile
{
    public int V { get; set; } = 1;
    public string Salt { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>
/// 导入 / 导出：CSV 明文（兼容常见工具）与 AES-256-GCM + PBKDF2 加密备份。
/// 加密备份格式与图形版 Services/ImportExportService.cs 完全一致：盐 16 字节，密钥 PBKDF2-SHA256 12 万次。
/// </summary>
public static class ImportExportService
{
    // ---------------- 加密备份 ----------------

    public static void ExportEncryptedJson(VaultData data, string path, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new CliException("加密导出必须提供非空口令（--passphrase）。");

        var payload = Encoding.UTF8.GetBytes(data.ToJson());
        var salt = CryptoService.RandomBytes(16);
        var key = CryptoService.DeriveKey(passphrase, salt);
        var file = new ExportFile
        {
            V = 1,
            Salt = Convert.ToBase64String(salt),
            Data = CryptoService.EncryptAes(payload, key)
        };
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json.Options), new UTF8Encoding(false));
    }

    public static VaultData ImportEncryptedJson(string path, string passphrase)
    {
        if (!File.Exists(path)) throw new CliException($"备份文件不存在：{path}");
        ExportFile file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), Json.Options)
                   ?? throw new FormatException("内容为空");
        }
        catch (Exception ex)
        {
            throw new CliException($"备份文件格式不正确：{ex.Message}");
        }

        if (file.V != 1)
            throw new CliException($"不支持的备份版本 V={file.V}（本程序支持 V=1）。");

        byte[] salt;
        try { salt = Convert.FromBase64String(file.Salt); }
        catch { throw new CliException("备份文件中的 Salt 不是合法 base64。"); }

        var key = CryptoService.DeriveKey(passphrase, salt);
        try
        {
            var json = Encoding.UTF8.GetString(CryptoService.DecryptAes(file.Data, key));
            return VaultData.FromJson(json);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new CliException("解密失败：口令不正确，或备份文件已损坏。");
        }
        catch (FormatException)
        {
            throw new CliException("解密失败：密文不是合法 base64。");
        }
    }

    // ---------------- CSV ----------------

    public const string CsvHeader = "name,group,protocol,host,port,username,password,note";

    public static void ExportCsv(VaultData data, string path)
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (var s in data.Sessions.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            sb.Append(CsvEscape(s.Name)).Append(',')
              .Append(CsvEscape(s.Group)).Append(',')
              .Append(ProtocolInfo.Label(s.Protocol)).Append(',')
              .Append(CsvEscape(s.Host)).Append(',')
              .Append(s.Port.ToString()).Append(',')
              .Append(CsvEscape(s.Username)).Append(',')
              .Append(CsvEscape(s.Password)).Append(',')
              .Append(CsvEscape(s.Note)).Append('\n');
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // UTF-8 with BOM：便于 Excel 正确识别中文
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>解析 CSV。中英文表头均可识别；返回的会话尚未分配 Id。</summary>
    public static List<SessionInfo> ImportCsv(string path, out string warning)
    {
        warning = "";
        if (!File.Exists(path)) throw new CliException($"CSV 文件不存在：{path}");

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return new List<SessionInfo>();

        var header = ParseCsvLine(lines[0]).Select(h => h.Trim().Trim('\uFEFF').ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));

        int cName = Col("name", "名称", "名字", "title", "标签");
        int cGroup = Col("group", "分组", "组", "category", "folder", "目录");
        int cProto = Col("protocol", "协议", "type", "类型", "proto");
        int cHost = Col("host", "主机", "ip", "address", "server", "服务器", "hostname", "地址");
        int cPort = Col("port", "端口");
        int cUser = Col("username", "user", "用户名", "账号", "用户");
        int cPass = Col("password", "pass", "密码", "pwd", "passwd");
        int cNote = Col("note", "备注", "comment", "描述", "description", "说明");

        if (cHost < 0)
            throw new CliException(
                $"CSV 缺少主机列。至少需要一列名为 host / 主机 / ip / 地址；当前表头：{lines[0]}");

        var result = new List<SessionInfo>();
        var skipped = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = ParseCsvLine(lines[i]);
            string Get(int idx) => idx >= 0 && idx < cells.Count ? cells[idx].Trim() : "";

            var host = Get(cHost);
            if (string.IsNullOrEmpty(host)) { skipped++; continue; }

            var protoText = Get(cProto).ToLowerInvariant();
            var proto = ProtocolInfo.TryParse(protoText, out var parsed) ? parsed : SessionProtocol.Ssh;
            if (cProto >= 0 && protoText.Length > 0 && !ProtocolInfo.TryParse(protoText, out _))
                skipped++;

            var session = new SessionInfo
            {
                Name = Get(cName),
                Group = Get(cGroup),
                Protocol = proto,
                Host = host,
                Port = int.TryParse(Get(cPort), out var p) && p > 0 && p < 65536
                    ? p
                    : ProtocolInfo.DefaultPort(proto),
                Username = Get(cUser),
                Password = Get(cPass),
                Note = Get(cNote)
            };
            if (string.IsNullOrEmpty(session.Name)) session.Name = session.Host;
            result.Add(session);
        }

        if (skipped > 0) warning = $"已跳过 {skipped} 行无效记录（缺少主机或协议无法识别）";
        return result;
    }

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ---------------- 合并导入 ----------------

    /// <summary>合并导入：按 (协议, 主机, 端口, 用户名) 判定为同一会话则更新，否则新增。返回 (新增, 更新)。</summary>
    public static (int Added, int Updated) MergeImport(VaultService vault, IEnumerable<SessionInfo> incoming)
    {
        int added = 0, updated = 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var src in incoming)
        {
            var existing = vault.Data.Sessions.FirstOrDefault(x =>
                x.Protocol == src.Protocol &&
                string.Equals(x.Host, src.Host, StringComparison.OrdinalIgnoreCase) &&
                x.Port == src.Port &&
                string.Equals(x.Username, src.Username, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                src.Id = Guid.NewGuid().ToString("N");
                src.CreatedAt = now;
                src.UpdatedAt = now;
                vault.Data.Sessions.Add(src);
                added++;
            }
            else
            {
                existing.Name = string.IsNullOrEmpty(src.Name) ? existing.Name : src.Name;
                existing.Group = src.Group;
                existing.Password = src.Password;
                existing.Note = src.Note;
                existing.UpdatedAt = now;
                vault.Data.Tombstones.Remove(existing.Id);
                updated++;
            }
        }
        return (added, updated);
    }
}
