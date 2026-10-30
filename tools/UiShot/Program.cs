using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenDM.App;
using OpenDM.Core.Models;

namespace UiShot
{
    /// <summary>
    /// 离屏渲染主窗口为 PNG，用来肉眼检查排版（不显示窗口、不截屏、不干扰桌面）。
    /// 用法: UiShot.exe [输出路径] [--select=索引]
    /// </summary>
    public static class Program
    {
        static readonly double W = 1140;
        static readonly double H = 740;

        [STAThread]
        public static void Main(string[] args)
        {
            string outPath = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--")
                ? args[0]
                : "ui-shot.png");

            // 隔离数据目录，避免污染真实的下载任务列表
            string dataDir = Path.Combine(Path.GetTempPath(), "opendm-uishot-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("OPEN_DM_DATA_DIR", dataDir);
            File.WriteAllText(Path.Combine(dataDir, "settings.json"),
                "{\"Folder\":\"D:\\\\Downloads\",\"Connections\":8,\"MaxConcurrent\":3," +
                "\"SpeedLimitKbps\":0,\"Proxy\":\"\",\"BridgeEnabled\":false,\"BridgePort\":15100," +
                "\"BridgeToken\":\"uishot\"}");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/OpenDM;component/Themes/Theme.xaml", UriKind.Relative)
            });

            var win = new MainWindow
            {
                Width = W,
                Height = H,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false
            };
            var list = Inject(win);

            // WPF 的 Window 只有真正 Show 出来才会布局并产出可渲染的视觉树；
            // 挪到屏幕外 + 不显示在任务栏，既不影响桌面也能拿到真实排版。
            win.Show();
            DoEvents();
            DoEvents();

            if (list.SelectedItem is null && list.Items.Count > 0)
                list.SelectedItem = list.Items[0];

            DoEvents();
            win.UpdateLayout();
            DoEvents();

            double cw = win.ActualWidth > 0 ? win.ActualWidth : W;
            double ch = win.ActualHeight > 0 ? win.ActualHeight : H;

            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(cw * scale), (int)(ch * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(win);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) enc.Save(fs);

            Console.WriteLine("saved " + outPath + $" ({cw}x{ch})");

            win.Close();
            try { Directory.Delete(dataDir, true); } catch { }
            app.Shutdown();
        }

        static ListBox Inject(MainWindow win)
        {
            var list = (ListBox)win.FindName("TaskList");
            var cv = (ICollectionView)list.ItemsSource;
            var src = (ObservableCollection<DownloadTask>)cv.SourceCollection;

            src.Clear();
            var samples = Sample();
            foreach (var t in samples) src.Add(t);
            cv.Refresh();

            list.SelectedItem = samples[0];   // 默认选中「已完成」，好检查打开文件的按钮
            return list;
        }

        static List<DownloadTask> Sample()
        {
            var list = new List<DownloadTask>();

            // 1) 已完成
            var done = Make("ubuntu-24.04.1-desktop-amd64.iso",
                @"D:\Downloads\ubuntu-24.04.1-desktop-amd64.iso",
                6_000_000_000, 6_000_000_000, TaskState.Completed, 16);
            done.CompletedAt = DateTime.Now.AddMinutes(-12);
            list.Add(done);

            // 2) 下载中
            var run = Make("VisualStudioSetup.exe", @"D:\Downloads\VisualStudioSetup.exe",
                3_800_000_000, 2_400_000_000, TaskState.Downloading, 32);
            run.SpeedBps = 13_400_000;
            list.Add(run);

            // 3) 暂停
            var paused = Make("adobe-photoshop-2024.rar", @"D:\Downloads\adobe-photoshop-2024.rar",
                1_200_000_000, 480_000_000, TaskState.Paused, 8);
            list.Add(paused);

            // 4) 出错（带 Cookie 请求头）
            var err = Make("report-2026Q3.pdf", @"D:\Downloads\report-2026Q3.pdf",
                24_000_000, 3_100_000, TaskState.Error, 4);
            err.Error = "403 Forbidden";
            err.Headers["Cookie"] = "session=abc123";
            err.Headers["Referer"] = "https://example.com/";
            list.Add(err);

            // 5) 排队
            var queued = Make("movie-4k-bluray.mkv", @"D:\Downloads\movie-4k-bluray.mkv",
                18_000_000_000, 0, TaskState.Queued, 64);
            list.Add(queued);

            return list;
        }

        static DownloadTask Make(string name, string path, long total, long done, TaskState state, int segs)
        {
            var t = new DownloadTask
            {
                Url = "https://example.com/" + Uri.EscapeDataString(name),
                SavePath = path,
                TotalBytes = total,
                BytesDone = done,
                State = state,
                Connections = segs,
                IsPrepared = true,
                SupportsRange = true
            };

            long len = total / segs;
            for (int i = 0; i < segs; i++)
            {
                long start = i * len;
                long end = i == segs - 1 ? total - 1 : start + len - 1;
                long filled = (long)((end - start + 1) * Math.Min(1.0, done / (double)Math.Max(1, total)));
                t.Segments.Add(new Segment { Start = start, End = end, Position = start + filled });
            }
            return t;
        }

        static void DoEvents()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
