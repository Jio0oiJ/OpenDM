using OpenDM.Core.Models;

namespace OpenDM.Core;

/// <summary>纯格式化工具，UI 直接绑字符串，省掉一堆转换器。</summary>
public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Size(long bytes)
    {
        if (bytes < 0) return "未知大小";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:F0} {Units[i]}" : $"{v:F1} {Units[i]}";
    }

    public static string Speed(double bps)
    {
        if (bps <= 1) return "—";
        return Size((long)bps) + "/s";
    }

    public static string Eta(long remainBytes, double bps)
    {
        if (remainBytes <= 0) return "即将完成";
        if (bps <= 1) return "—";
        var s = (long)(remainBytes / bps);
        if (s > 86_400) return "超过 1 天";
        var ts = TimeSpan.FromSeconds(s);
        return ts.Hours > 0
            ? $"剩余 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"剩余 {ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static string StateText(TaskState s) => s switch
    {
        TaskState.Queued => "排队中",
        TaskState.Probing => "正在连接",
        TaskState.Downloading => "下载中",
        TaskState.Paused => "已暂停",
        TaskState.Completed => "已完成",
        TaskState.Error => "出错",
        _ => ""
    };
}
