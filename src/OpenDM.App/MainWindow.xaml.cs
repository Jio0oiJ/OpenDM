using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OpenDM.Core;
using OpenDM.Core.Engine;
using OpenDM.Core.Models;

namespace OpenDM.App;

public partial class MainWindow : Window
{
    private readonly TaskManager _mgr;
    private readonly ObservableCollection<DownloadTask> _tasks = new();
    private readonly ObservableCollection<FilterItem> _filters = new();
    private readonly Dictionary<string, TaskState> _lastState = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer;
    private readonly AppSettings _settings;
    private LocalBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();

        _settings = AppSettings.Load();

        _mgr = new TaskManager(_settings.Connections)
        {
            DefaultFolder = _settings.Folder,
            MaxConcurrentTasks = _settings.MaxConcurrent,
            SpeedLimitBps = _settings.SpeedLimitKbps * 1024
        };
        _mgr.SetProxy(_settings.Proxy);
        _mgr.TaskCreated += OnTaskCreated;

        foreach (var t in _mgr.LoadExisting())
        {
            _tasks.Add(t);
            _lastState[t.Id] = t.State;
        }

        _view = CollectionViewSource.GetDefaultView(_tasks);
        _view.Filter = FilterPredicate;
        TaskList.ItemsSource = _view;

        _filters.Add(new FilterItem("全部任务", _ => true));
        _filters.Add(new FilterItem("下载中", t => t.State is TaskState.Queued or TaskState.Probing or TaskState.Downloading));
        _filters.Add(new FilterItem("已完成", t => t.State == TaskState.Completed));
        _filters.Add(new FilterItem("已暂停", t => t.State == TaskState.Paused));
        _filters.Add(new FilterItem("出错", t => t.State == TaskState.Error));
        FilterList.ItemsSource = _filters;
        FilterList.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;

        DragOver += OnDragOver;
        Drop += OnDrop;

        // 命令行直接带链接（浏览器接管、右键"用 OpenDM 下载"都走这条路）
        Loaded += async (_, _) =>
        {
            var arg = CommandLineUrl();

            // 已经有一个 OpenDM 在跑？把活交给它，自己退出，免得两个窗口抢同一个接管端口
            if (_settings.BridgeEnabled)
            {
                if (arg is not null &&
                    await LocalBridge.TryForwardAsync(_settings.BridgePort, _settings.BridgeToken, arg))
                {
                    Close();
                    return;
                }

                if (arg is null && await LocalBridge.TryShowAsync(_settings.BridgePort, _settings.BridgeToken))
                {
                    Close();
                    return;
                }
            }

            StartBridge();

            if (arg is not null)
                await AddTaskAsync(arg, _settings.Folder, "", _settings.Connections);
        };

        UpdateTotals();
    }

    private static string? CommandLineUrl() =>
        Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a =>
            a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private FilterItem? Current => FilterList.SelectedItem as FilterItem;

    private string? _toast;
    private DateTime _toastAt;

    /// <summary>状态栏临时提示，6 秒后自动让位给常规状态。</summary>
    private void Toast(string message)
    {
        _toast = message;
        _toastAt = DateTime.Now;
        StatusText.Text = message;
    }

    private bool FilterPredicate(object obj) =>
        obj is DownloadTask t && (Current?.Match(t) ?? true);

    // ---------------------------------------------------------------- 刷新

    private void Tick()
    {
        bool stateChanged = false;

        foreach (var t in _tasks)
        {
            t.Refresh();
            if (!_lastState.TryGetValue(t.Id, out var prev) || prev != t.State)
            {
                _lastState[t.Id] = t.State;
                stateChanged = true;
            }
        }

        if (stateChanged) _view.Refresh();

        if (TaskList.SelectedItem is DownloadTask sel)
            foreach (var s in sel.Segments) s.NotifyProgress();

        UpdateTotals();
    }

    private void UpdateTotals()
    {
        double speed = 0;
        int active = 0;

        foreach (var t in _tasks)
        {
            if (t.State != TaskState.Downloading) continue;
            speed += t.SpeedBps;
            active++;
        }

        TotalSpeedText.Text = active > 0 ? Fmt.Speed(speed) : "—";
        TotalCountText.Text = active > 0
            ? $"{_tasks.Count} 个任务 · {active} 进行中"
            : $"{_tasks.Count} 个任务";

        foreach (var f in _filters) f.Count = _tasks.Count(f.Match);

        int queued = _tasks.Count(t => t.State == TaskState.Queued);
        var parts = new List<string> { active > 0 ? $"正在下载 {active}" : "就绪" };
        if (queued > 0) parts.Add($"{queued} 个排队");
        if (_mgr.SpeedLimitBps > 0) parts.Add($"限速 {Fmt.Size(_mgr.SpeedLimitBps)}/s");
        if (_bridge is not null) parts.Add($"接管 :{_settings.BridgePort}");

        var status = string.Join("  ·  ", parts);
        if (_toast is not null && (DateTime.Now - _toastAt).TotalSeconds < 6)
            status = _toast + "   |   " + status;

        StatusText.Text = status;

        bool hasSel = TaskList.SelectedItem is DownloadTask;
        BtnResume.IsEnabled = hasSel;
        BtnPause.IsEnabled = TaskList.SelectedItem is DownloadTask { State: TaskState.Downloading or TaskState.Queued };
        BtnFolder.IsEnabled = hasSel;
        BtnRemove.IsEnabled = hasSel;
        BtnHeaders.IsEnabled = hasSel;
    }

    private void OnTaskCreated(DownloadTask t)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tasks.Any(x => x.Id == t.Id)) return;
            _tasks.Insert(0, t);
            _lastState[t.Id] = t.State;
            FilterList.SelectedIndex = 0;
            _view.Refresh();
            TaskList.SelectedItem = t;
        }));
    }

    // ---------------------------------------------------------------- 操作

    private async void BtnNew_Click(object sender, RoutedEventArgs e) => await ShowNewTaskAsync(null);

    private async System.Threading.Tasks.Task ShowNewTaskAsync(string? prefillUrl)
    {
        var dlg = new NewTaskWindow(prefillUrl, _settings.Folder, _settings.Connections) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await AddTaskAsync(dlg.Url, dlg.Folder, dlg.FileName, dlg.Connections, dlg.Headers);
    }

    private async System.Threading.Tasks.Task AddTaskAsync(
        string url, string folder, string name, int connections,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        try
        {
            StatusText.Text = "正在探测下载地址…";
            var t = await _mgr.AddAsync(url, folder,
                string.IsNullOrWhiteSpace(name) ? null : name,
                connections, startNow: true, headers: headers);
            StatusText.Text = "已添加：" + t.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "添加失败";
            MessageBox.Show(this, $"无法开始下载：\n\n{ex.Message}", "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnResume_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not DownloadTask t) return;
        if (t.State == TaskState.Completed) return;
        _mgr.Start(t);
        Tick();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not DownloadTask t) return;
        _mgr.Pause(t);
        Tick();
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e) => RevealInExplorer(TaskList.SelectedItem as DownloadTask);

    /// <summary>列表行里的「打开所在文件夹」按钮（仅已完成任务显示）。</summary>
    private void RowOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not DownloadTask t) return;
        TaskList.SelectedItem = t;      // 顺手选中，方便接着做其它操作
        RevealInExplorer(t);
    }

    private void DetailOpenFolder_Click(object sender, RoutedEventArgs e) =>
        RevealInExplorer(TaskList.SelectedItem as DownloadTask);

    private void DetailOpenFile_Click(object sender, RoutedEventArgs e) =>
        OpenFile(TaskList.SelectedItem as DownloadTask);

    /// <summary>在资源管理器里定位文件；文件没了就退回打开它所在的目录。</summary>
    private void RevealInExplorer(DownloadTask? t)
    {
        if (t is null) return;

        try
        {
            if (File.Exists(t.SavePath))
            {
                Process.Start("explorer.exe", $"/select,\"{t.SavePath}\"");
                return;
            }

            if (Directory.Exists(t.Folder))
            {
                Process.Start("explorer.exe", $"\"{t.Folder}\"");
                Toast("文件已不在原位置，已打开保存目录");
                return;
            }

            Toast("目录不存在：" + t.Folder);
        }
        catch (Exception ex)
        {
            Toast("打开目录失败：" + ex.Message);
        }
    }

    /// <summary>用系统默认程序打开已下载的文件。</summary>
    private void OpenFile(DownloadTask? t)
    {
        if (t is null) return;

        if (!File.Exists(t.SavePath))
        {
            Toast("文件不存在，可能已被移动或删除");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(t.SavePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast("打开文件失败：" + ex.Message);
        }
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (TaskList.SelectedItem is not DownloadTask t) return;

        var r = MessageBox.Show(this,
            $"要删除任务「{t.FileName}」吗？\n\n" +
            "「是」= 删除任务并删除已下载的文件\n" +
            "「否」= 只从列表移除，保留文件\n" +
            "「取消」= 什么都不做",
            "删除任务", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return;

        _mgr.Remove(t, deleteFile: r == MessageBoxResult.Yes);
        _tasks.Remove(t);
        _lastState.Remove(t.Id);
        _view.Refresh();
        Tick();
    }

    private void BtnHeaders_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not DownloadTask t) return;

        var dlg = new HeaderWindow(t.FileName, t.Headers) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var parsed = dlg.Headers;
        _mgr.SetHeaders(t, parsed);

        StatusText.Text = parsed.Count == 0
            ? "已清除请求头"
            : $"已保存 {parsed.Count} 项请求头 · 下次开始 / 继续时生效";
        Tick();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        bool bridgeChanged = dlg.Result.BridgeEnabled != _settings.BridgeEnabled ||
                             dlg.Result.BridgePort != _settings.BridgePort ||
                             dlg.Result.BridgeToken != _settings.BridgeToken;

        _settings.Folder = dlg.Result.Folder;
        _settings.Connections = dlg.Result.Connections;
        _settings.MaxConcurrent = dlg.Result.MaxConcurrent;
        _settings.SpeedLimitKbps = dlg.Result.SpeedLimitKbps;
        _settings.Proxy = dlg.Result.Proxy;
        _settings.BridgeEnabled = dlg.Result.BridgeEnabled;
        _settings.BridgePort = dlg.Result.BridgePort;
        _settings.BridgeToken = dlg.Result.BridgeToken;
        _settings.Save();

        _mgr.DefaultFolder = _settings.Folder;
        _mgr.DefaultConnections = _settings.Connections;
        _mgr.MaxConcurrentTasks = _settings.MaxConcurrent;
        _mgr.SpeedLimitBps = _settings.SpeedLimitKbps * 1024;
        _mgr.SetProxy(_settings.Proxy);

        if (bridgeChanged) StartBridge();

        StatusText.Text = _settings.SpeedLimitKbps > 0
            ? $"设置已保存 · 全局限速 {Fmt.Size(_settings.SpeedLimitKbps * 1024)}/s"
            : "设置已保存";
        Tick();
    }

    // ---------------------------------------------------------------- 接管接口

    private void StartBridge()
    {
        StopBridge();

        if (!_settings.BridgeEnabled) return;

        var bridge = new LocalBridge(_mgr, _settings.BridgePort, _settings.BridgeToken);
        bridge.ShowRequested += () => Dispatcher.BeginInvoke(new Action(BringToFront));
        bridge.Log += msg => Dispatcher.BeginInvoke(new Action(() => Toast(msg)));

        if (!bridge.Start(out var err))
        {
            bridge.Dispose();
            App.Log("接管接口启动失败: " + err);
            StatusText.Text = err ?? "接管接口启动失败";
            return;
        }

        App.Log($"接管接口已监听 127.0.0.1:{_settings.BridgePort}");

        // 接管进来的任务要出现在列表里
        _bridge = bridge;
    }

    private void StopBridge()
    {
        _bridge?.Dispose();
        _bridge = null;
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTotals();

    private void FilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _view.Refresh();
        UpdateTotals();
    }

    private void TaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 点在行内按钮上（双击打开文件夹）时不要顺带打开文件
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;

        if (TaskList.SelectedItem is not DownloadTask t) return;

        if (t.State == TaskState.Completed)
        {
            OpenFile(t);
            return;
        }

        if (t.State == TaskState.Downloading) _mgr.Pause(t);
        else _mgr.Start(t);
        Tick();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void TaskList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = ShowNewTaskAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && TaskList.IsKeyboardFocusWithin)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- 拖拽

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.Text) is not string text) return;

        var url = text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;

        e.Handled = true;
        _ = ShowNewTaskAsync(url);
    }

    // ---------------------------------------------------------------- 收尾

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        StopBridge();
        try
        {
            _mgr.PauseAll();
            _mgr.Dispose();
        }
        catch { }
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>左侧分类项，带计数。</summary>
public sealed class FilterItem : INotifyPropertyChanged
{
    private int _count;

    public FilterItem(string name, Func<DownloadTask, bool> match)
    {
        Name = name;
        Match = match;
    }

    public string Name { get; }
    public Func<DownloadTask, bool> Match { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
