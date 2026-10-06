namespace CxSshClient.Models;

/// <summary>统一的远程/本地文件条目(用于 SFTP / FTP 文件管理器)</summary>
public class FileEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";

    public string SizeText
    {
        get
        {
            if (IsDirectory) return "文件夹";
            double s = Size;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
            return i == 0 ? $"{Size} B" : $"{s:0.##} {units[i]}";
        }
    }

    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
}
