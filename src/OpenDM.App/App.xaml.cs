using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OpenDM.App;

public partial class App : Application
{
    /// <summary>
    /// 出问题时的落盘日志（%APPDATA%\OpenDM\log.txt）。只记异常与接管失败这类
    /// 用户看不到原因的事，正常下载不写，避免日志膨胀。
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var path = Path.Combine(AppSettings.DataDir, "log.txt");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 被浏览器通过 native messaging 拉起时，先按协议应答一条指令。
        // 返回 true 表示这次只是来取路径 / 改路径的，回完就退出，不必开窗口。
        if (NativeHost.HandleBrowserLaunch())
        {
            Environment.Exit(0);
        }

        base.OnStartup(e);

        // 主程序被挪过位置的话，把 host 清单指回来，否则浏览器再想唤起就找不到人了
        NativeHost.Refresh();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowCrash(ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrash(e.Exception);
        e.Handled = true;
    }

    private static void ShowCrash(Exception ex)
    {
        Log("未处理异常: " + ex);
        try
        {
            MessageBox.Show(
                $"OpenDM 遇到一个未处理的错误：\n\n{ex.Message}\n\n{ex.GetType().Name}",
                "OpenDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}
