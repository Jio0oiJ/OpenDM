using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenDM.Core.Engine;

namespace OpenDM.App;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; }

    private readonly AppSettings _origin;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        _origin = current;
        Result = new AppSettings
        {
            Folder = current.Folder,
            Connections = current.Connections,
            MaxConcurrent = current.MaxConcurrent,
            SpeedLimitKbps = current.SpeedLimitKbps,
            Proxy = current.Proxy,
            BridgeEnabled = current.BridgeEnabled,
            BridgePort = current.BridgePort,
            BridgeToken = current.BridgeToken
        };

        FolderBox.Text = current.Folder;
        ConnSlider.Value = Math.Clamp(current.Connections, 1, TaskManager.MaxConnections);
        QueueSlider.Value = Math.Clamp(current.MaxConcurrent, 1, TaskManager.MaxConcurrentLimit);
        SpeedBox.Text = current.SpeedLimitKbps.ToString();
        ProxyBox.Text = current.Proxy;
        BridgeCheck.IsChecked = current.BridgeEnabled;
        PortBox.Text = current.BridgePort.ToString();
        TokenBox.Text = current.BridgeToken;

        BridgeCheck_Changed(null, null!);
        RefreshLauncherState();
    }

    // ---------------------------------------------------------------- 浏览器自动唤起

    private void RefreshLauncherState()
    {
        bool on = NativeHost.IsInstalled();
        var browsers = on ? NativeHost.InstalledBrowsers() : new List<string>();

        if (on && browsers.Count > 0)
        {
            LauncherStateText.Text = "已启用 · 已注册到 " + string.Join("、", browsers);
            LauncherStateText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
        }
        else
        {
            LauncherStateText.Text = "未安装 · 浏览器不会自动启动 OpenDM";
            LauncherStateText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }

        string path = on ? NativeHost.RegisteredExePath() : "";
        if (path.Length == 0) path = NativeHost.ExePath;

        LauncherPathText.Text = path;
        LauncherPathText.ToolTip = path;

        LauncherInstallBtn.Content = on ? "重新安装" : "安装";
        LauncherRemoveBtn.IsEnabled = on;
        LauncherRemoveBtn.Opacity = on ? 1 : 0.4;
    }

    private void LauncherInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeHost.Install();
            RefreshLauncherState();
            MessageBox.Show(this,
                "已安装。浏览器（Chrome / Edge）在连不上 OpenDM 时会自动把它启动起来。\n\n" +
                "如果浏览器已经打开着，需要重启一次浏览器才会生效 —— 浏览器只在启动时读这份注册表。",
                "OpenDM", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "安装失败：" + ex.Message, "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LauncherRemove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeHost.Uninstall();
            RefreshLauncherState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "移除失败：" + ex.Message, "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BridgeCheck_Changed(object? sender, RoutedEventArgs e)
    {
        bool on = BridgeCheck.IsChecked == true;
        PortBox.IsEnabled = on;
        TokenBox.IsEnabled = on;
        PortBox.Opacity = on ? 1 : 0.45;
        TokenBox.Opacity = on ? 1 : 0.45;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认保存目录",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : ""
        };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TokenBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is not System.Windows.Controls.Button btn) return;
        var old = btn.Content;
        btn.Content = "已复制";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            btn.Content = old;
        };
        timer.Start();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = Result.Folder;

        // 限速：留空或 0 都当不限
        long speedKb = 0;
        var speedText = SpeedBox.Text.Trim();
        if (speedText.Length > 0 && !long.TryParse(speedText, out speedKb))
        {
            MessageBox.Show(this, "限速只能填数字（单位 KB/s），0 表示不限速。", "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SpeedBox.Focus();
            return;
        }
        if (speedKb < 0) speedKb = 0;

        // 代理：写错了当场拦住，否则下载会静默走系统代理，很难查
        var proxy = ProxySetting.Parse(ProxyBox.Text.Trim());
        if (proxy.Error is not null)
        {
            MessageBox.Show(this, proxy.Error + "\n\n留空可改为跟随系统代理。", "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ProxyBox.Focus();
            return;
        }

        int port = _origin.BridgePort;
        if (BridgeCheck.IsChecked == true)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1024 or > 65535)
            {
                MessageBox.Show(this, "端口需要是 1024–65535 之间的数字。", "OpenDM",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PortBox.Focus();
                return;
            }
        }

        Result.Folder = folder;
        Result.Connections = (int)Math.Round(ConnSlider.Value);
        Result.MaxConcurrent = (int)Math.Round(QueueSlider.Value);
        Result.SpeedLimitKbps = speedKb;
        Result.Proxy = ProxyBox.Text.Trim();
        Result.BridgeEnabled = BridgeCheck.IsChecked == true;
        Result.BridgePort = port;
        Result.BridgeToken = string.IsNullOrWhiteSpace(TokenBox.Text)
            ? AppSettings.NewToken()
            : TokenBox.Text.Trim();

        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
