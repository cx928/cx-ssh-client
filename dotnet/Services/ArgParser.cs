namespace CxSshClient.Cli.Services;

/// <summary>
/// 极简命令行解析器（不依赖任何第三方库）。
/// 支持三种写法：
///   --name 值        （选项 + 独立值）
///   --name=值        （等号连写）
///   --flag           （布尔开关，值为 "true"）
/// 其余以 - 开头的未知选项会被保留在 Options 中，由命令自行判断是否需要报错。
/// </summary>
public class ArgParser
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>位置参数（未以 - 开头、且不是某个选项的值）</summary>
    public List<string> Positional { get; } = new();

    public string Command { get; private set; } = "";

    /// <summary>解析结果中是否出现过该选项</summary>
    public bool Has(string name) => _options.ContainsKey(Normalize(name));

    /// <summary>取选项值；未提供时返回 fallback</summary>
    public string Get(string name, string fallback = "") =>
        _options.TryGetValue(Normalize(name), out var v) ? v : fallback;

    public int GetInt(string name, int fallback)
    {
        var raw = Get(name);
        return int.TryParse(raw, out var n) ? n : fallback;
    }

    public bool GetBool(string name) => GetBool(name, false);

    public bool GetBool(string name, bool fallback)
    {
        if (!_options.TryGetValue(Normalize(name), out var v)) return fallback;
        if (string.IsNullOrEmpty(v)) return true;
        return v.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => true
        };
    }

    /// <summary>所有出现过的选项名（已去掉前缀 --）</summary>
    public IEnumerable<string> OptionNames => _options.Keys;

    /// <summary>未在已知列表中出现的选项名，用于「未知参数」提示。</summary>
    public List<string> UnknownOptions(params string[] known)
    {
        var set = new HashSet<string>(known.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return _options.Keys.Where(k => !set.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>移除一个选项（供全局选项在处理完后摘除，避免被命令判为未知参数）。</summary>
    public bool Remove(string name) => _options.Remove(Normalize(name));

    private static string Normalize(string name) => name.TrimStart('-').ToLowerInvariant();

    /// <summary>解析参数数组。args 不含程序名与命令名。</summary>
    public static ArgParser Parse(string command, string[] args)
    {
        var p = new ArgParser { Command = command };
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];

            // 单独的 "--" 之后全部按位置参数处理
            if (a == "--")
            {
                for (var j = i + 1; j < args.Length; j++) p.Positional.Add(args[j]);
                break;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var body = a[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    p._options[Normalize(body[..eq])] = body[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    // --key value 形式
                    p._options[Normalize(body)] = args[i + 1];
                    i++;
                }
                else
                {
                    // 布尔开关
                    p._options[Normalize(body)] = "true";
                }
            }
            else if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1 && !char.IsDigit(a[1]))
            {
                var body = a[1..];
                var eq = body.IndexOf('=');
                if (eq >= 0) p._options[Normalize(body[..eq])] = body[(eq + 1)..];
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    p._options[Normalize(body)] = args[i + 1];
                    i++;
                }
                else p._options[Normalize(body)] = "true";
            }
            else
            {
                p.Positional.Add(a);
            }
        }
        return p;
    }
}
