using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxSshClient.Cli.Models;

/// <summary>
/// 密码库数据：会话 + 删除墓碑。整体 DPAPI 加密后落盘 / 上云。
/// JSON 结构严格对齐图形版 Models/VaultData.cs：
/// {"sessions":[...],"tombstones":{},"updatedAt":0}
/// </summary>
public class VaultData
{
    [JsonPropertyName("sessions")]
    public List<SessionInfo> Sessions { get; set; } = new();

    /// <summary>已删除的会话 id → 删除时间（用于跨设备同步合并）</summary>
    [JsonPropertyName("tombstones")]
    public Dictionary<string, long> Tombstones { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    /// <summary>容错解析：字段缺失或为 null 时退化为默认值，不抛异常。</summary>
    public static VaultData FromJson(string json)
    {
        var data = JsonSerializer.Deserialize<VaultData>(json, Json.Options) ?? new VaultData();
        data.Sessions ??= new List<SessionInfo>();
        data.Tombstones ??= new Dictionary<string, long>();
        data.Sessions.RemoveAll(s => s is null);
        return data;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    /// <summary>合并两个库：按 id 取 updatedAt 较新者；墓碑优先于会话。算法与图形版一致。</summary>
    public static VaultData Merge(VaultData a, VaultData b)
    {
        var result = new VaultData();
        var map = new Dictionary<string, SessionInfo>();
        var tombs = new Dictionary<string, long>();

        void MergeOne(VaultData src, VaultData other)
        {
            foreach (var s in src.Sessions)
            {
                if (src.Tombstones.TryGetValue(s.Id, out var t1) &&
                    (!other.Tombstones.TryGetValue(s.Id, out var t2) || t1 >= t2))
                    continue;
                if (other.Tombstones.TryGetValue(s.Id, out _))
                    continue;
                if (map.TryGetValue(s.Id, out var existing))
                {
                    if (s.UpdatedAt > existing.UpdatedAt)
                        map[s.Id] = s;
                }
                else map[s.Id] = s;
            }
            foreach (var (id, t) in src.Tombstones)
            {
                if (!tombs.TryGetValue(id, out var old) || t > old)
                    tombs[id] = t;
            }
        }

        MergeOne(a, b);
        MergeOne(b, a);

        result.Sessions = map.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        result.Tombstones = tombs;
        result.UpdatedAt = Math.Max(a.UpdatedAt, b.UpdatedAt);
        return result;
    }
}

/// <summary>统一的 JSON 选项（与图形版 System.Text.Json 默认行为一致：不缩进、属性名原样）。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
