using Microsoft.UI.Xaml;
using CxSshClient.Models;
using CxSshClient.Services;

namespace CxSshClient;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    /// <summary>视图请求保存会话(由主窗口注入)</summary>
    public static Action<SessionInfo>? SaveSessionRequested;

    public App()
    {
        InitializeComponent();

        // 兜底: 任一异常都不再让整个程序崩溃, 并写入数据目录下的 crash.log
        UnhandledException += (_, e) =>
        {
            Log.Write("XAML", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("Task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>兼容旧调用点</summary>
    public static void LogCrash(string source, Exception? ex) => Log.Write(source, ex);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();
    }
}
