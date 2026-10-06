using System.Text;
using CxSshClient.Cli.Models;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 本地密码库。
/// 位置：%LOCALAPPDATA%\cx-ssh-client\vault.dat（可用环境变量 CX_DATA_DIR 覆盖，便于测试/便携使用）。
/// 内容：DPAPI(CurrentUser) 加密的 UTF-8 JSON；写入采用「临时文件 + 替换」的原子方式。
/// 文件不存在或解析失败时视为空库并给出提示，绝不崩溃。
/// </summary>
public class VaultService
{
    public const string AppFolderName = "cx-ssh-client";
    public const string VaultFileName = "vault.dat";

    /// <summary>数据目录覆盖（构造参数优先，其次环境变量 CX_DATA_DIR，最后 %LOCALAPPDATA%）</summary>
    public static string? OverrideDirectory { get; set; }

    public string DataDirectory { get; }
    public string VaultFilePath => Path.Combine(DataDirectory, VaultFileName);

    /// <summary>加载过程中的非致命提示（供命令打印）</summary>
    public List<string> Warnings { get; } = new();

    public VaultData Data { get; private set; } = new();

    public VaultService(string? dataDirectory = null)
    {
        DataDirectory = ResolveDirectory(dataDirectory);
    }

    private static string ResolveDirectory(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir!;
        if (!string.IsNullOrWhiteSpace(OverrideDirectory)) return OverrideDirectory!;

        var env = Environment.GetEnvironmentVariable("CX_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env!;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = AppContext.BaseDirectory;
        return Path.Combine(local, AppFolderName);
    }

    /// <summary>读取密码库。返回 false 表示文件存在但无法解密/解析（已按空库处理）。</summary>
    public bool Load()
    {
        Warnings.Clear();
        try
        {
            if (!File.Exists(VaultFilePath))
            {
                Data = new VaultData();
                return true;
            }

            var enc = File.ReadAllBytes(VaultFilePath);
            if (enc.Length == 0)
            {
                Warnings.Add($"密码库文件为空，已按空库处理：{VaultFilePath}");
                Data = new VaultData();
                return false;
            }

            string json;
            try
            {
                json = Encoding.UTF8.GetString(CryptoService.UnprotectLocal(enc));
            }
            catch (Exception ex)
            {
                Warnings.Add($"密码库无法解密（可能由其他 Windows 用户或图形版的不同参数创建）：{ex.Message}");
                Data = new VaultData();
                return false;
            }

            try
            {
                Data = VaultData.FromJson(json);
                return true;
            }
            catch (Exception ex)
            {
                Warnings.Add($"密码库内容不是合法 JSON，已按空库处理：{ex.Message}");
                Data = new VaultData();
                return false;
            }
        }
        catch (Exception ex)
        {
            Warnings.Add($"读取密码库失败：{ex.Message}");
            Data = new VaultData();
            return false;
        }
    }

    /// <summary>保存密码库（原子写入）。失败抛异常，由命令层转为友好的中文错误。</summary>
    public void Save()
    {
        Data.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Directory.CreateDirectory(DataDirectory);
        var json = Data.ToJson();
        var enc = CryptoService.ProtectLocal(Encoding.UTF8.GetBytes(json));

        var tmp = VaultFilePath + ".tmp";
        File.WriteAllBytes(tmp, enc);
        File.Move(tmp, VaultFilePath, overwrite: true);
    }

    /// <summary>写入或更新会话（按 Id 匹配），并清掉同 Id 的墓碑。</summary>
    public void UpsertSession(SessionInfo s)
    {
        s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var idx = Data.Sessions.FindIndex(x => x.Id == s.Id);
        if (idx >= 0) Data.Sessions[idx] = s;
        else Data.Sessions.Add(s);
        Data.Tombstones.Remove(s.Id);
    }

    /// <summary>删除会话并留下墓碑（供同步传播删除）。</summary>
    public void DeleteSession(string id)
    {
        Data.Sessions.RemoveAll(x => x.Id == id);
        Data.Tombstones[id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void ReplaceAll(VaultData data) => Data = data;

    /// <summary>导入会话集合（每条都分配新的 Id）。返回导入条数。</summary>
    public int ImportSessions(IEnumerable<SessionInfo> sessions)
    {
        var count = 0;
        foreach (var s in sessions)
        {
            s.Id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s.CreatedAt = now;
            s.UpdatedAt = now;
            Data.Sessions.Add(s);
            count++;
        }
        return count;
    }

    // ---------------- 查找 ----------------

    /// <summary>按名称精确匹配（忽略大小写），否则按 Id 前缀匹配。</summary>
    public SessionInfo? Find(string nameOrIdPrefix)
    {
        var key = (nameOrIdPrefix ?? "").Trim();
        if (key.Length == 0) return null;

        var exact = Data.Sessions.FirstOrDefault(s =>
            string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var idExact = Data.Sessions.FirstOrDefault(s =>
            string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase));
        if (idExact is not null) return idExact;

        var matches = Data.Sessions
            .Where(s => s.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>查找同名的所有会话（用于给出「名称不唯一」的提示）。</summary>
    public List<SessionInfo> FindAllByName(string name) =>
        Data.Sessions
            .Where(s => string.Equals(s.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>解析会话，失败时抛出带中文说明的异常。</summary>
    public SessionInfo Require(string nameOrIdPrefix)
    {
        var s = Find(nameOrIdPrefix);
        if (s is not null) return s;

        var sameName = FindAllByName(nameOrIdPrefix);
        if (sameName.Count > 1)
            throw new CliException(
                $"名称「{nameOrIdPrefix}」对应 {sameName.Count} 条会话，请改用会话 Id 前缀指定：" +
                string.Join(" / ", sameName.Select(x => x.Id[..8])));

        throw new CliException($"未找到会话「{nameOrIdPrefix}」。可用 `cx list` 查看全部会话。");
    }
}
