using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxSshClient.Models;

/// <summary>密码库数据: 会话 + 删除墓碑。整体加密后落盘/上云。</summary>
public class VaultData
{
    [JsonPropertyName("sessions")]
    public List<SessionInfo> Sessions { get; set; } = new();

    /// <summary>已删除的会话 id + 删除时间(用于同步合并)</summary>
    [JsonPropertyName("tombstones")]
    public Dictionary<string, long> Tombstones { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    public static VaultData FromJson(string json) =>
        JsonSerializer.Deserialize<VaultData>(json) ?? new VaultData();

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>合并两个库: 按 id 取 updatedAt 较新者; 墓碑优先。返回合并结果。</summary>
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

        result.Sessions = map.Values.OrderBy(s => s.Name).ToList();
        result.Tombstones = tombs;
        result.UpdatedAt = Math.Max(a.UpdatedAt, b.UpdatedAt);
        return result;
    }
}
