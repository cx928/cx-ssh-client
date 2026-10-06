using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CxSshClient.Models;

namespace CxSshClient.Services;

public class ExportFile
{
    public int V { get; set; } = 1;
    public string Salt { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>密码导入/导出: 加密 JSON(口令保护) 与 CSV(明文, 兼容常见工具)</summary>
public static class ImportExportService
{
    // ---------- 加密 JSON ----------
    public static void ExportEncryptedJson(VaultData data, string path, string passphrase)
    {
        var payload = Encoding.UTF8.GetBytes(data.ToJson());
        var salt = CryptoService.RandomBytes(16);
        var key = CryptoService.DeriveKey(passphrase, salt);
        var enc = CryptoService.EncryptAes(payload, key);
        var file = new ExportFile { Salt = Convert.ToBase64String(salt), Data = enc };
        File.WriteAllText(path, JsonSerializer.Serialize(file));
    }

    public static VaultData ImportEncryptedJson(string path, string passphrase)
    {
        var file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path))
            ?? throw new FormatException("文件格式不正确");
        var key = CryptoService.DeriveKey(passphrase, Convert.FromBase64String(file.Salt));
        var json = Encoding.UTF8.GetString(CryptoService.DecryptAes(file.Data, key));
        return VaultData.FromJson(json);
    }

    // ---------- CSV ----------
    public static void ExportCsv(VaultData data, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,group,protocol,host,port,username,password,note");
        foreach (var s in data.Sessions)
            sb.AppendLine(string.Join(",", new[]
            {
                CsvEscape(s.Name), CsvEscape(s.Group),
                ProtocolInfo.Label(s.Protocol), CsvEscape(s.Host),
                s.Port.ToString(), CsvEscape(s.Username), CsvEscape(s.Password), CsvEscape(s.Note)
            }));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static List<SessionInfo> ImportCsv(string path, out string warning)
    {
        warning = "";
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return new List<SessionInfo>();
        var header = ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));

        int cName = Col("name", "名称", "名字", "title", "标签");
        int cGroup = Col("group", "分组", "category", "folder", "目录");
        int cProto = Col("protocol", "协议", "type", "类型", "proto");
        int cHost = Col("host", "主机", "ip", "address", "server", "服务器", "hostname", "地址");
        int cPort = Col("port", "端口");
        int cUser = Col("username", "user", "用户名", "账号", "用户");
        int cPass = Col("password", "pass", "密码", "pwd", "passwd");
        int cNote = Col("note", "备注", "comment", "描述", "description");

        if (cHost < 0)
        {
            warning = "未找到主机列 (host)，无法导入";
            return new List<SessionInfo>();
        }

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
            var proto = protoText switch
            {
                "ssh" => SessionProtocol.Ssh,
                "sftp" => SessionProtocol.Sftp,
                "rdp" => SessionProtocol.Rdp,
                "ftp" => SessionProtocol.Ftp,
                "ftps" => SessionProtocol.Ftp,
                "vnc" => SessionProtocol.Vnc,
                _ => SessionProtocol.Ssh
            };
            if (cProto >= 0 && protoText is not ("" or "ssh" or "sftp" or "rdp" or "ftp" or "ftps" or "vnc"))
                skipped++;

            var session = new SessionInfo
            {
                Name = Get(cName),
                Group = Get(cGroup),
                Protocol = proto,
                Host = host,
                Port = int.TryParse(Get(cPort), out var p) && p > 0 && p < 65536 ? p : ProtocolInfo.DefaultPort(proto),
                Username = Get(cUser),
                Password = Get(cPass),
                Note = Get(cNote)
            };
            if (string.IsNullOrEmpty(session.Name)) session.Name = session.Host;
            result.Add(session);
        }
        if (skipped > 0) warning = $"跳过 {skipped} 行无效记录";
        return result;
    }

    private static string CsvEscape(string s)
    {
        if (s is null) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
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
}
