using System.Text;

namespace CxSshClient.Cli.Services;

/// <summary>
/// 控制台输出助手：统一中文提示、表格对齐（中文字符按 2 列宽计算）、口令遮罩。
/// 所有命令的可见输出都经过这里，便于保证风格一致。
/// </summary>
public static class Output
{
    public static ConsoleColor Default => ConsoleColor.Gray;

    public static void Info(string message) => WriteLine(message, ConsoleColor.Gray);

    public static void Ok(string message) => WriteLine("√ " + message, ConsoleColor.Green);

    public static void Warn(string message) => WriteLine("! " + message, ConsoleColor.Yellow);

    public static void Error(string message) => WriteLine("× " + message, ConsoleColor.Red);

    /// <summary>次要信息（灰色，便于和结果区分）</summary>
    public static void Detail(string message) => WriteLine(message, ConsoleColor.DarkGray);

    public static void Head(string message) => WriteLine(message, ConsoleColor.Cyan);

    private static void WriteLine(string message, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        catch (IOException)
        {
            // 输出被重定向/管道已关闭时忽略
        }
        finally
        {
            try { Console.ForegroundColor = old; } catch (IOException) { }
        }
    }

    /// <summary>把口令替换成固定长度掩码，避免终端/日志泄露。</summary>
    public static string Mask(string? secret) =>
        string.IsNullOrEmpty(secret) ? "(空)" : new string('*', Math.Min(8, Math.Max(3, secret.Length)));

    /// <summary>显示宽度：CJK 与全角字符按 2 计算。</summary>
    public static int DisplayWidth(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var w = 0;
        foreach (var ch in s)
            w += IsWide(ch) ? 2 : 1;
        return w;
    }

    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) ||   // 韩文字母
        (c >= 0x2E80 && c <= 0xA4CF) ||   // CJK 部首 ~ 彝文
        (c >= 0xAC00 && c <= 0xD7A3) ||   // 韩文音节
        (c >= 0xF900 && c <= 0xFAFF) ||   // CJK 兼容表意
        (c >= 0xFE30 && c <= 0xFE6F) ||   // CJK 兼容形式
        (c >= 0xFF00 && c <= 0xFF60) ||   // 全角 ASCII
        (c >= 0xFFE0 && c <= 0xFFE6);

    /// <summary>按显示宽度左侧补空格。</summary>
    public static string Pad(string? s, int width)
    {
        var text = s ?? "";
        var pad = width - DisplayWidth(text);
        return pad > 0 ? text + new string(' ', pad) : text;
    }

    /// <summary>截断到指定显示宽度，超出部分用 … 结尾。</summary>
    public static string Truncate(string? s, int width)
    {
        var text = s ?? "";
        if (DisplayWidth(text) <= width) return text;
        var sb = new StringBuilder();
        var w = 0;
        foreach (var ch in text)
        {
            var cw = IsWide(ch) ? 2 : 1;
            if (w + cw > width - 1) break;
            sb.Append(ch);
            w += cw;
        }
        return sb.Append('…').ToString();
    }

    /// <summary>以表格形式打印（第一行为表头）。</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var cols = headers.Count;
        var widths = new int[cols];
        for (var c = 0; c < cols; c++) widths[c] = DisplayWidth(headers[c]);
        foreach (var row in rows)
            for (var c = 0; c < cols; c++)
                widths[c] = Math.Max(widths[c], DisplayWidth(c < row.Count ? row[c] : ""));

        var head = string.Join("  ", headers.Select((h, c) => Pad(h, widths[c])));
        Head(head.TrimEnd());
        Head(new string('-', Math.Min(120, DisplayWidth(head.TrimEnd()))));

        foreach (var row in rows)
        {
            var line = string.Join("  ", Enumerable.Range(0, cols)
                .Select(c => Pad(c < row.Count ? row[c] : "", widths[c])));
            Info(line.TrimEnd());
        }
    }

    /// <summary>打印一行「键 : 值」。</summary>
    public static void Field(string key, string? value, ConsoleColor color = ConsoleColor.Gray)
    {
        var old = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(Pad(key, 14) + ": ");
            Console.ForegroundColor = color;
            Console.WriteLine(value ?? "");
        }
        catch (IOException) { }
        finally
        {
            try { Console.ForegroundColor = old; } catch (IOException) { }
        }
    }
}
