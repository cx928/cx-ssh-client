using System.Text;
using CxSshClient.Models;

namespace CxSshClient.Services;

/// <summary>
/// 本地密码库。
/// 存储位置优先 %LOCALAPPDATA%\cx-ssh-client; 若该位置不可写(受限环境/权限问题),
/// 自动回退到程序目录下的 cx-ssh-client-data (便携模式), 可通过 DataDirectory 查看最终位置。
/// 写入采用"临时文件 + 替换"的原子方式, 避免中途失败损坏密码库。
/// </summary>
public class VaultService
{
    /// <summary>可覆盖的数据目录 (便携模式 / 自动化测试)</summary>
    public static string? OverrideDirectory { get; set; }

    private static string? _resolvedDir;

    /// <summary>写入密码库失败时触发 (UI 据此提示用户, 避免静默丢数据)</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>实际使用的数据目录 (自动选择可写位置)</summary>
    public static string DataDirectory
    {
        get
        {
            if (OverrideDirectory is not null) return OverrideDirectory;
            if (_resolvedDir is not null) return _resolvedDir;
            var candidates = Candidates();
            foreach (var candidate in candidates)
            {
                if (IsWritable(candidate))
                {
                    _resolvedDir = candidate;
                    return candidate;
                }
            }
            _resolvedDir = candidates[0];
            return _resolvedDir;
        }
    }

    public static string VaultFilePath => Path.Combine(DataDirectory, "vault.dat");

    private static List<string> Candidates()
    {
        var list = new List<string>();
        try
        {
            list.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cx-ssh-client"));
        }
        catch { }
        try { list.Add(Path.Combine(AppContext.BaseDirectory, "cx-ssh-client-data")); } catch { }
        return list;
    }

    /// <summary>探测目录是否可写 (真实写入一个探针文件)</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".nova-write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly object _lock = new();
    public VaultData Data { get; private set; } = new();

    public bool Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(VaultFilePath))
                {
                    var enc = File.ReadAllBytes(VaultFilePath);
                    var json = Encoding.UTF8.GetString(CryptoService.UnprotectLocal(enc));
                    Data = VaultData.FromJson(json);
                }
                else Data = new VaultData();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("VaultLoad", ex);
                Data = new VaultData();
                return false;
            }
        }
    }

    /// <summary>保存密码库; 返回 false 表示写入失败(已记录日志并触发 SaveFailed)</summary>
    public bool Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                Data.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var json = Data.ToJson();
                var enc = CryptoService.ProtectLocal(Encoding.UTF8.GetBytes(json));

                // 原子写入: 先写临时文件, 再替换目标文件
                var tmp = VaultFilePath + ".tmp";
                File.WriteAllBytes(tmp, enc);
                File.Move(tmp, VaultFilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("VaultSave", ex);
                SaveFailed?.Invoke($"密码库保存失败 ({DataDirectory}): {ex.Message}");
                return false;
            }
        }
    }

    public void UpsertSession(SessionInfo s)
    {
        lock (_lock)
        {
            var idx = Data.Sessions.FindIndex(x => x.Id == s.Id);
            if (idx >= 0) Data.Sessions[idx] = s;
            else Data.Sessions.Add(s);
            s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Data.Tombstones.Remove(s.Id);
        }
        Save();
    }

    public void DeleteSession(string id)
    {
        lock (_lock)
        {
            Data.Sessions.RemoveAll(x => x.Id == id);
            Data.Tombstones[id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        Save();
    }

    /// <summary>导入会话集合, 返回导入数量</summary>
    public int ImportSessions(IEnumerable<SessionInfo> sessions)
    {
        var count = 0;
        lock (_lock)
        {
            foreach (var s in sessions)
            {
                s.Id = Guid.NewGuid().ToString("N");
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                s.CreatedAt = now;
                s.UpdatedAt = now;
                Data.Sessions.Add(s);
                count++;
            }
        }
        Save();
        return count;
    }

    public void ReplaceAll(VaultData data)
    {
        lock (_lock) { Data = data; }
        Save();
    }
}
