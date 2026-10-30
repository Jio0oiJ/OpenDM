using System.IO;
using System.Windows;
using System.Windows.Input;
using OpenDM.Core.Engine;

namespace OpenDM.App;

public partial class NewTaskWindow : Window
{
    private const string HeaderHintDefault =
        "可直接粘贴 Cookie 串、浏览器里「Copy as cURL」的整条命令，或导出的 cookies.txt";

    private string _lastGuess = "";

    public string Url => UrlBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();
    public string FileName => NameBox.Text.Trim();
    public int Connections => (int)Math.Round(ConnSlider.Value);

    /// <summary>解析后的请求头（Cookie / Referer / User-Agent …）。</summary>
    public Dictionary<string, string> Headers => RequestHeaders.Parse(HeaderBox.Text);

    public NewTaskWindow(string? initialUrl = null, string? folder = null, int connections = 8)
    {
        InitializeComponent();

        FolderBox.Text = folder ?? "";
        ConnSlider.Value = Math.Clamp(connections, 1, TaskManager.MaxConnections);

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            UrlBox.Text = initialUrl.Trim();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        }

        UpdateHeaderHint();

        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            if (!string.IsNullOrEmpty(UrlBox.Text)) UrlBox.SelectAll();
        };
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var guess = GuessName(UrlBox.Text);
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _lastGuess)
        {
            NameBox.Text = guess;
            _lastGuess = guess;
        }
    }

    private static string GuessName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var u = new Uri(url.Trim());
            var seg = u.Segments.LastOrDefault()?.Trim('/');
            if (string.IsNullOrWhiteSpace(seg)) return "";
            var name = Uri.UnescapeDataString(seg);
            return HttpProbe.Sanitize(name);
        }
        catch
        {
            return "";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存位置",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : ""
        };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    // ---------------------------------------------------------------- 请求头

    private void HeaderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateHeaderHint();

    private void UpdateHeaderHint()
    {
        if (HeaderHint is null || HeaderBox is null) return;

        var parsed = RequestHeaders.Parse(HeaderBox.Text);
        if (parsed.Count == 0)
        {
            HeaderHint.Text = HeaderHintDefault;
            HeaderHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        var names = string.Join("、", parsed.Keys);
        var cookie = parsed.TryGetValue("Cookie", out var c) ? c : null;
        var extra = cookie is null ? "" : $"；Cookie {cookie.Length} 字符";

        HeaderHint.Text = $"已识别 {parsed.Count} 项：{names}{extra}";
        HeaderHint.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
    }

    private void ImportHeaders_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 Cookie / 请求头",
            Filter = "Cookie 文件|cookies.txt;*.txt;*.json;*.har|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var text = File.ReadAllText(dlg.FileName);

            // 太长的多半是整份 HAR，挑 cookie 字段出来会误导用户，直接提示
            if (text.Length > 400_000)
            {
                MessageBox.Show(this, "文件太大，请只粘贴 cookie 串或 cURL 命令。", "OpenDM",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            HeaderBox.Text = text.Trim();

            if (RequestHeaders.Parse(HeaderBox.Text).Count == 0)
            {
                MessageBox.Show(this, "这个文件里没解析出可用的 Cookie 或请求头。", "OpenDM",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取失败：{ex.Message}", "OpenDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearHeaders_Click(object sender, RoutedEventArgs e)
    {
        HeaderBox.Clear();
        HeaderBox.Focus();
    }

    // ---------------------------------------------------------------- 提交

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var url = Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("请填写下载地址");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("地址必须是 http:// 或 https:// 开头");
            return;
        }

        if (string.IsNullOrWhiteSpace(Folder))
        {
            ShowError("请选择保存位置");
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
