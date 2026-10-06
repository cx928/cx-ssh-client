using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CxSshClient.Helpers;

public static class FilePickerHelper
{
    private static nint Hwnd => WindowNative.GetWindowHandle(App.MainAppWindow);

    public static async Task<StorageFile?> PickOpenFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        return await picker.PickSingleFileAsync();
    }

    public static async Task<StorageFile?> PickSaveFileAsync(string suggestedName, string extension, string label)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(label, new List<string> { extension });
        InitializeWithWindow.Initialize(picker, Hwnd);
        return await picker.PickSaveFileAsync();
    }

    public static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        return await picker.PickSingleFolderAsync();
    }
}
