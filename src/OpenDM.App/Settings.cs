using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenDM.Core.Engine;

namespace OpenDM.App;

/// <summary>应用设置，存在 %APPDATA%\OpenDM\settings.json。</summary>
public sealed class AppSettings
{
    public string Folder { get; set; } = "";
    public int Connections { get; set; } = 8;

    /// <summary>同时下载的任务数（其余排队）。</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>全局速度上限，单位 KB/s；0 = 不限速。</summary>
    public long SpeedLimitKbps { get; set; }

    /// <summary>代理。空 = 跟随系统；none = 直连；或 http://host:port / socks5://host:port。</summary>
    public string Proxy { get; set; } = "";

    // ---------- 浏览器接管 ----------

    public bool BridgeEnabled { get; set; } = true;
    public int BridgePort { get; set; } = LocalBridge.DefaultPort;

    /// <summary>本地接口令牌：网页拿不到它，就无法往队列里塞任务。</summary>
    public string BridgeToken { get; set; } = "";

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    internal static string DataDir =>
        Environment.GetEnvironmentVariable("OPEN_DM_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenDM");

    public static AppSettings Load()
    {
        var s = new AppSettings();
        bool isNew = !File.Exists(FilePath);

        try
        {
            if (!isNew)
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(s.Folder))
            s.Folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        s.Connections = Math.Clamp(s.Connections, 1, TaskManager.MaxConnections);
        s.MaxConcurrent = Math.Clamp(s.MaxConcurrent, 1, TaskManager.MaxConcurrentLimit);
        if (s.SpeedLimitKbps < 0) s.SpeedLimitKbps = 0;
        if (s.BridgePort is < 1024 or > 65535) s.BridgePort = LocalBridge.DefaultPort;

        // 令牌第一次生成后必须落盘：否则每次启动都是新串，扩展里填的旧令牌立刻失效
        bool needSave = isNew || string.IsNullOrWhiteSpace(s.BridgeToken);
        if (needSave) s.BridgeToken = NewToken();

        if (needSave) s.Save();
        return s;
    }

    /// <summary>令牌用 16 位十六进制 —— 够随机，又能手抄进扩展设置页。</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
