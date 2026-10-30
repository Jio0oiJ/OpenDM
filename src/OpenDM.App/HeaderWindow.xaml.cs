using System.IO;
using System.Windows;
using System.Windows.Input;
using OpenDM.Core.Engine;

namespace OpenDM.App;

/// <summary>
/// 编辑某个任务的自定义请求头。典型用途：Cookie 过期后下载变 403，改一遍 Cookie 接着续传，
/// 不用把已经下了一半的文件重新建任务。
/// </summary>
public partial class HeaderWindow : Window
{
    private const string HintDefault =
        "可直接粘贴 Cookie 串、浏览器里「Copy as cURL」的整条命令，或导出的 cookies.txt";

    public Dictionary<string, string> Headers => RequestHeaders.Parse(HeaderBox.Text);

    public HeaderWindow(string fileName, IReadOnlyDictionary<string, string>? existing)
    {
        InitializeComponent();

        FileNameText.Text = fileName;
        HeaderBox.Text = RequestHeaders.ToDisplayText(existing);
        UpdateHint();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void HeaderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateHint();

    private void UpdateHint()
    {
        if (HintText is null || HeaderBox is null) return;

        var parsed = RequestHeaders.Parse(HeaderBox.Text);
        if (parsed.Count == 0)
        {
            HintText.Text = HintDefault;
            HintText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        var names = string.Join("、", parsed.Keys);
        HintText.Text = $"已识别 {parsed.Count} 项：{names}";
        HintText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 Cookie / 请求头",
            Filter = "Cookie 文件|cookies.txt;*.txt;*.json|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var text = File.ReadAllText(dlg.FileName);
            if (text.Length > 400_000)
            {
                MessageBox.Show(this, "文件太大，请只粘贴 cookie 串或 cURL 命令。", "OpenDM",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            HeaderBox.Text = text.Trim();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取失败：{ex.Message}", "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        HeaderBox.Clear();
        HeaderBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
