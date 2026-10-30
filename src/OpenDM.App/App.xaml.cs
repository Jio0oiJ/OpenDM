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
        base.OnStartup(e);

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
