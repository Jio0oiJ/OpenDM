using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenDM.Core;
using OpenDM.Core.Models;

namespace OpenDM.App;

/// <summary>
/// 单个任务的「属性」窗口：把列表和详情面板里没有的字段（完整链接、任务 ID、
/// 磁盘上文件的真实大小与修改时间、请求头清单）一次列全，长文本可以复制走。
/// </summary>
public partial class PropertiesWindow : Window
{
    private readonly List<(string Label, string Value)> _rows = new();

    public PropertiesWindow(DownloadTask t)
    {
        InitializeComponent();

        TitleText.Text = t.FileName;
        Build(t);
    }

    private void Build(DownloadTask t)
    {
        AddRow("状态", t.StatusText, (Brush)FindResource(StateBrushKey(t.State)));

        AddRow("进度", t.TotalBytes > 0 ? $"{t.Progress * 100:F1}%" : "未知（服务器未给长度）");
        AddRow("大小", t.TotalBytes > 0
            ? $"{Fmt.Size(t.BytesDone)} / {Fmt.Size(t.TotalBytes)}（共 {t.TotalBytes:N0} 字节）"
            : Fmt.Size(t.BytesDone));

        AddRow("保存到", t.SavePath, copy: t.SavePath);
        AddRow("下载链接", t.Url, copy: t.Url);

        AddRow("分段", t.SegText + (t.SupportsRange ? "" : "（服务器不支持 Range，单流）"));
        AddRow("连接数", t.Connections.ToString());

        AddRow("创建时间", t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow("完成时间", t.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—");

        AddRow("请求头", t.Headers.Count == 0
            ? "无（匿名请求）"
            : $"{t.Headers.Count} 项：{string.Join("、", t.Headers.Keys)}");

        AddRow("磁盘文件", DiskInfo(t.SavePath));

        if (!string.IsNullOrWhiteSpace(t.Error))
            AddRow("错误", t.Error!, (Brush)FindResource("RedBrush"));

        AddRow("任务 ID", t.Id, copy: t.Id);
    }

    /// <summary>磁盘上的真实情况：文件可能被移走、改名，或被别的程序占用改过大小。</summary>
    private static string DiskInfo(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "文件不在原位置（可能已移动或删除）";
            return $"{Fmt.Size(fi.Length)} · 最后修改 {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            return "无法读取：" + ex.Message;
        }
    }

    private static string StateBrushKey(TaskState state) => state switch
    {
        TaskState.Completed => "GreenBrush",
        TaskState.Downloading => "AccentBrush",
        TaskState.Error => "RedBrush",
        TaskState.Queued or TaskState.Probing => "AmberBrush",
        _ => "MutedBrush"
    };

    private void AddRow(string label, string value, Brush? color = null, string? copy = null)
    {
        _rows.Add((label, value));

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(lab, 0);

        var val = new TextBlock
        {
            Text = value,
            Foreground = color ?? (Brush)FindResource("TextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(val, 1);

        grid.Children.Add(lab);
        grid.Children.Add(val);

        if (copy is not null)
        {
            var btn = new Button
            {
                Content = "复制",
                Style = (Style)FindResource("LinkBtn"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            btn.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(copy);
                    btn.Content = "已复制";
                }
                catch
                {
                    btn.Content = "复制失败";
                }
            };
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        Rows.Children.Add(grid);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _rows.Select(r => $"{r.Label}：{r.Value}"));
        try
        {
            Clipboard.SetText(text);
            TitleText.Text = "已复制全部信息";
        }
        catch
        {
            TitleText.Text = "复制失败，剪贴板被其它程序占用";
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
