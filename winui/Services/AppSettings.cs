using System.Text.Json;
using Microsoft.UI.Xaml;

namespace CxSshClient.Services;

/// <summary>应用设置 (主题、终端字号、窗口位置等) — 与密码库放在同一数据目录</summary>
public class AppSettings
{
    private static string FilePath => Path.Combine(VaultService.DataDirectory, "settings.json");

    public string Theme { get; set; } = "system";
    public double TerminalFontSize { get; set; } = 14;
    public bool MinimizeToTrayOnClose { get; set; }

    // 窗口位置与大小 (下次启动恢复)
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    public ElementTheme ElementTheme => Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (s is null) return;
            Theme = s.Theme;
            TerminalFontSize = s.TerminalFontSize;
            MinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
            WindowX = s.WindowX;
            WindowY = s.WindowY;
            WindowWidth = s.WindowWidth;
            WindowHeight = s.WindowHeight;
        }
        catch (Exception ex)
        {
            Log.Write("SettingsLoad", ex);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(VaultService.DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("SettingsSave", ex);
        }
    }
}
