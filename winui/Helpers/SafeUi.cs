using Microsoft.UI.Xaml.Controls;

namespace CxSshClient.Helpers;

/// <summary>
/// 对话框安全显示: WinUI 同时只允许一个 ContentDialog,
/// 重复显示会抛异常, 这里统一吞掉并记录, 避免 async void 事件导致进程崩溃。
/// </summary>
public static class SafeUi
{
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.LogCrash("Dialog", ex);
            return ContentDialogResult.None;
        }
    }
}
