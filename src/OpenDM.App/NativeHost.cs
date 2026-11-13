using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenDM.App;

/// <summary>
/// 让浏览器能把 OpenDM 拉起来的「本地启动器」。
///
/// 为什么需要它：浏览器扩展没有执行本地程序的权限（file:// 指向 exe 只会被下载，
/// 自定义协议每次都要用户点一次确认）。唯一无感的方式是 Chrome 的 native messaging ——
/// 浏览器按注册表找到 host 清单，自己把进程拉起来。
///
/// 这里的 host 就是 OpenDM 本体：被浏览器以管道方式启动时，先按 native messaging 协议
/// 应答一条消息（start / setPath / getPath），然后该显示的窗口照常显示。
/// </summary>
internal static class NativeHost
{
    /// <summary>host 名，必须与注册表键名、host 清单的 name 完全一致。</summary>
    public const string HostName = "com.opendm.launcher";

    /// <summary>
    /// 固定扩展 ID —— 由 manifest 里的 "key" 决定（公钥 DER 的 SHA-256 前 16 字节映射）。
    /// 之所以要固定：浏览器只允许清单里列出的扩展调用 host，而「加载已解压的扩展」
    /// 默认 ID 跟目录路径绑定，换个目录就失效。
    /// </summary>
    public const string ExtensionId = "ndhkhjeaooenioieceapibfhpajkkflf";

    private static readonly string[] Browsers =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\",
        @"Software\Microsoft\Edge\NativeMessagingHosts\",
        @"Software\Chromium\NativeMessagingHosts\",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\"
    };

    private static string Dir => Path.Combine(AppSettings.DataDir, "native");
    private static string ManifestPath => Path.Combine(Dir, HostName + ".json");

    public static string ExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? "";

    // ---------------------------------------------------------------- 安装 / 卸载

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return false;
            foreach (var b in Browsers)
            {
                using var key = Registry.CurrentUser.OpenSubKey(b + HostName);
                if (key?.GetValue(null) is string p && p.Length > 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>已注册到哪些浏览器，用于设置页显示（Chrome / Edge / …）。</summary>
    public static List<string> InstalledBrowsers()
    {
        var list = new List<string>();
        string[] names = { "Chrome", "Edge", "Chromium", "Brave" };
        for (int i = 0; i < Browsers.Length; i++)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Browsers[i] + HostName);
                if (key?.GetValue(null) is string p && p.Length > 0) list.Add(names[i]);
            }
            catch { }
        }
        return list;
    }

    /// <summary>当前 host 清单里记录的主程序路径（扩展选项页显示的就是这个）。</summary>
    public static string RegisteredExePath()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>把 host 清单指向指定 exe，并写 HKCU 注册表（不需要管理员权限）。</summary>
    public static void Install(string? exePath = null)
    {
        string exe = string.IsNullOrWhiteSpace(exePath) ? ExePath : exePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("找不到主程序", exe);

        Directory.CreateDirectory(Dir);
        WriteManifest(exe);

        Exception? last = null;
        bool any = false;
        foreach (var b in Browsers)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(b + HostName);
                if (key is null) continue;
                // 默认值（名称 ""）必须是 host 清单的绝对路径，Chrome 只认这个
                key.SetValue(null, ManifestPath, RegistryValueKind.String);
                any = true;
            }
            catch (Exception ex) { last = ex; }
        }

        if (!any) throw last ?? new InvalidOperationException("写入注册表失败");
    }

    public static void Uninstall()
    {
        foreach (var b in Browsers)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(b + HostName, throwOnMissingSubKey: false); }
            catch { }
        }
        try { if (File.Exists(ManifestPath)) File.Delete(ManifestPath); }
        catch { }
    }

    /// <summary>
    /// 启动时自愈：清单里的 exe 被移走/删掉了，就重新指向当前运行的这份。
    /// 故意不覆盖「用户手动指定的另一个 OpenDM」—— 只要那个文件还在就尊重用户的选择。
    /// </summary>
    public static void Refresh()
    {
        try
        {
            if (!IsInstalled()) return;
            string registered = RegisteredExePath();
            if (registered.Length > 0 && File.Exists(registered)) return;
            if (ExePath.Length == 0) return;
            Install(ExePath);
        }
        catch { }
    }

    private static void WriteManifest(string exePath)
    {
        var payload = new
        {
            name = HostName,
            description = "OpenDM 浏览器自动唤起",
            path = exePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        };

        File.WriteAllText(ManifestPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------- 被浏览器拉起时的应答

    private const int STD_INPUT_HANDLE = -10;
    private const uint FILE_TYPE_PIPE = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    /// <summary>
    /// 判断这次启动是不是浏览器通过管道拉起来的。
    /// 用 GetFileType 而不是 Console.IsInputRedirected：没有控制台的 GUI 程序里，
    /// 后者的行为跟启动方式绑得很紧，容易在双击启动时误判、把程序卡在读管道上。
    /// </summary>
    private static bool LaunchedByBrowser()
    {
        try { return GetFileType(GetStdHandle(STD_INPUT_HANDLE)) == FILE_TYPE_PIPE; }
        catch { return false; }
    }

    /// <summary>
    /// 被浏览器拉起时处理一条指令。返回 true 表示已经处理完、本进程可以退出；
    /// 返回 false 表示继续正常启动图形界面（start 指令就是这种情况）。
    /// </summary>
    public static bool HandleBrowserLaunch()
    {
        if (!LaunchedByBrowser()) return false;

        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();

            // 万一管道里没有消息，别把启动卡死：最多等 3 秒
            JsonElement? msg = null;
            var read = Task.Run(() => msg = ReadMessage(input));
            if (!read.Wait(TimeSpan.FromSeconds(3)) || msg is null || msg.Value.ValueKind != JsonValueKind.Object)
                return false;

            string cmd = msg.Value.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            switch (cmd)
            {
                case "start":
                    WriteMessage(output, new { ok = true, exe = ExePath, pid = Environment.ProcessId });
                    return false;   // 应答完就正常启动窗口，用户能看见下载进度

                case "getPath":
                    WriteMessage(output, new { ok = true, path = RegisteredExePath(), exe = ExePath });
                    return true;

                case "setPath":
                {
                    string path = msg.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                        ? (p.GetString() ?? "").Trim().Trim('"')
                        : "";

                    if (path.Length == 0)
                    {
                        WriteMessage(output, new { ok = false, error = "路径为空" });
                        return true;
                    }
                    if (!File.Exists(path))
                    {
                        WriteMessage(output, new { ok = false, error = "这个路径上没有文件：" + path });
                        return true;
                    }
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMessage(output, new { ok = false, error = "请选择 OpenDM.exe" });
                        return true;
                    }

                    // 光看后缀不够：填成别的 exe 也能通过，最后只会让人困惑「为什么拉起来的不是下载器」。
                    // 有版本信息就核对一下产品名；读不到（非托管程序）就放行，不硬拦。
                    string product = "";
                    try { product = FileVersionInfo.GetVersionInfo(path).ProductName ?? ""; } catch { }
                    if (product.Length > 0 && !product.Contains("OpenDM", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMessage(output, new { ok = false, error = $"这个文件不是 OpenDM 主程序（产品名：{product}）" });
                        return true;
                    }

                    Install(path);
                    WriteMessage(output, new { ok = true, path });
                    return true;
                }

                default:
                    WriteMessage(output, new { ok = false, error = "未知指令：" + cmd });
                    return true;
            }
        }
        catch (Exception ex)
        {
            App.Log("native host 应答失败: " + ex.Message);
            return true;
        }
    }

    /// <summary>native messaging 的报文格式：4 字节小端长度 + UTF-8 JSON。</summary>
    private static JsonElement? ReadMessage(Stream s)
    {
        try
        {
            var lenBuf = new byte[4];
            if (!ReadExact(s, lenBuf, 4)) return null;
            uint len = BitConverter.ToUInt32(lenBuf, 0);
            if (len == 0 || len > 1024 * 1024) return null;

            var buf = new byte[len];
            if (!ReadExact(s, buf, (int)len)) return null;

            using var doc = JsonDocument.Parse(buf);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void WriteMessage(Stream s, object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        s.Write(BitConverter.GetBytes((uint)json.Length), 0, 4);
        s.Write(json, 0, json.Length);
        s.Flush();
    }
}
