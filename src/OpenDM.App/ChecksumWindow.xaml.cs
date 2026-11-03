using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenDM.Core;
using OpenDM.Core.Models;

namespace OpenDM.App;

/// <summary>
/// 计算并展示某个已下载文件的 SHA-256。
/// 大文件可能几个 GB，所以整个计算放在后台线程分块读、边读边喂哈希，
/// 界面只管进度；关窗口就取消，不会留着线程空转。
/// </summary>
public partial class ChecksumWindow : Window
{
    private const int BufferSize = 1 << 20;   // 1 MB 一块：够大不至于频繁 syscall，又不至于吃内存

    private readonly string _path;
    private readonly string _fileName;
    private readonly CancellationTokenSource _cts = new();
    private string _hash = "";

    public ChecksumWindow(DownloadTask t)
    {
        InitializeComponent();

        _path = t.SavePath;
        _fileName = t.FileName;

        FileNameText.Text = _fileName;
        MetaText.Text = File.Exists(_path) ? SafeSize() : "文件不存在";

        Loaded += OnLoaded;
        Closed += (_, _) => _cts.Cancel();
    }

    private string SafeSize()
    {
        try { return Fmt.Size(new FileInfo(_path).Length); }
        catch { return ""; }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<(long Done, long Total, double Bps)>(p =>
        {
            Bar.Value = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : 0;
            StatusText.Text = p.Total > 0
                ? $"{Fmt.Size(p.Done)} / {Fmt.Size(p.Total)} · {Fmt.Size((long)p.Bps)}/s"
                : $"{Fmt.Size(p.Done)} · {Fmt.Size((long)p.Bps)}/s";
        });

        var sw = Stopwatch.StartNew();
        try
        {
            _hash = await Task.Run(() => Compute(progress, _cts.Token), _cts.Token);
            sw.Stop();

            HashBox.Text = _hash;
            Bar.Value = 1;
            Bar.Foreground = (Brush)FindResource("GreenBrush");
            StatusText.Text = $"用时 {sw.Elapsed.TotalSeconds:F1}s";
            StatusText.Foreground = (Brush)FindResource("GreenBrush");

            CopyBtn.IsEnabled = true;
            CopySumBtn.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // 用户关窗口了，什么都不用做
        }
        catch (Exception ex)
        {
            HashBox.Text = "计算失败";
            HashBox.Foreground = (Brush)FindResource("RedBrush");
            Bar.Value = 0;
            StatusText.Text = ex.Message;
            StatusText.Foreground = (Brush)FindResource("RedBrush");
        }
    }

    /// <summary>后台线程：分块读文件 → 喂给 SHA-256 → 每 120ms 报一次进度。</summary>
    private string Compute(IProgress<(long, long, double)> progress, CancellationToken ct)
    {
        // FileShare.ReadWrite：文件正被播放器或杀毒软件打开着也能算，不会直接报「被占用」
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);

        long total = fs.Length;
        long done = 0;
        var buf = new byte[BufferSize];
        var elapsed = Stopwatch.StartNew();

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.AppendData(buf, 0, n);
            done += n;

            if (elapsed.ElapsedMilliseconds >= 120)
            {
                progress.Report((done, total, done / Math.Max(0.001, elapsed.Elapsed.TotalSeconds)));
            }
        }

        progress.Report((done, total, done / Math.Max(0.001, elapsed.Elapsed.TotalSeconds)));
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (_hash.Length == 0) return;
        try
        {
            Clipboard.SetText(_hash);
            CopyBtn.Content = "已复制";
        }
        catch
        {
            CopyBtn.Content = "复制失败";
        }
    }

    private void CopySum_Click(object sender, RoutedEventArgs e)
    {
        if (_hash.Length == 0) return;
        try
        {
            // 和 sha256sum 命令的输出格式一致：哈希 + 两个空格 + 文件名
            Clipboard.SetText($"{_hash}  {_fileName}");
            CopySumBtn.Content = "已复制";
        }
        catch
        {
            CopySumBtn.Content = "复制失败";
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
