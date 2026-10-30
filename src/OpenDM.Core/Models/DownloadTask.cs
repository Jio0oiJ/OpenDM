using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenDM.Core.Models;

/// <summary>
/// 一个下载任务。
/// 注意线程模型：引擎在后台线程直接写字段，不触发通知；
/// UI 用 400ms 定时器在 UI 线程调 Refresh() 批量通知，避免跨线程绑定异常与通知风暴。
/// </summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Url { get; set; } = "";
    public string SavePath { get; set; } = "";
    public string? ContentType { get; set; }
    public bool SupportsRange { get; set; }
    public int Connections { get; set; } = 8;
    public List<Segment> Segments { get; set; } = new();

    /// <summary>自定义请求头（Cookie / Referer / User-Agent …），空表示匿名请求。</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TaskState State { get; set; } = TaskState.Queued;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    /// <summary>总字节数；-1 表示服务器没给长度。</summary>
    public long TotalBytes { get; set; } = -1;

    /// <summary>已落盘字节数（原子累加，UI 只读）。</summary>
    public long BytesDone { get; set; }

    /// <summary>每秒字节，由测速器写入。</summary>
    public double SpeedBps { get; set; }

    /// <summary>探测已完成、分段已建好（用于区分"新建"与"恢复"）。</summary>
    public bool IsPrepared { get; set; }

    // ---------- 供绑定使用的只读视图 ----------

    public string FileName => Path.GetFileName(SavePath);

    public string Folder => Path.GetDirectoryName(SavePath) ?? "";

    public double Progress
    {
        get
        {
            if (State == TaskState.Completed) return 1;
            if (TotalBytes <= 0) return 0;
            return Math.Clamp((double)BytesDone / TotalBytes, 0, 1);
        }
    }

    public string ProgressText => TotalBytes <= 0
        ? Fmt.Size(BytesDone)
        : $"{Progress * 100:F0}%";

    public string SizeText => TotalBytes <= 0
        ? Fmt.Size(BytesDone)
        : $"{Fmt.Size(BytesDone)} / {Fmt.Size(TotalBytes)}";

    public string SpeedText => State switch
    {
        TaskState.Downloading => Fmt.Speed(SpeedBps),
        TaskState.Completed => "完成",
        TaskState.Paused => "已暂停",
        TaskState.Error => "已停止",
        _ => "—"
    };

    public string EtaText => State == TaskState.Downloading && TotalBytes > 0
        ? Fmt.Eta(TotalBytes - BytesDone, SpeedBps)
        : "";

    public string StatusText => Fmt.StateText(State);

    public string SegText
    {
        get
        {
            if (Segments.Count == 0) return "—";
            int done = Segments.Count(s => s.IsDone);
            return $"{Segments.Count} 段 · {done} 完成";
        }
    }

    public string DetailText
    {
        get
        {
            var parts = new List<string> { SizeText };
            if (State == TaskState.Downloading && SpeedBps > 1) parts.Add(Fmt.Speed(SpeedBps));
            if (State == TaskState.Error && !string.IsNullOrEmpty(Error)) parts.Add(Error!);
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>是否带了自定义请求头（界面上打个标记）。</summary>
    public bool HasHeaders => Headers.Count > 0;

    public string HeaderSummary => Headers.Count == 0
        ? ""
        : Headers.TryGetValue("Cookie", out var c) && !string.IsNullOrWhiteSpace(c)
            ? $"带 Cookie（{Headers.Count} 项）"
            : $"请求头 {Headers.Count} 项";

    // ---------- 通知 ----------

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        void N(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        N(nameof(Progress));
        N(nameof(ProgressText));
        N(nameof(SizeText));
        N(nameof(SpeedText));
        N(nameof(EtaText));
        N(nameof(StatusText));
        N(nameof(SegText));
        N(nameof(DetailText));
        N(nameof(HasHeaders));
        N(nameof(HeaderSummary));
        N(nameof(State));
    }

    /// <summary>按分段当前进度重算已下载量（恢复/暂停后调用）。</summary>
    public void RecomputeBytes()
    {
        long sum = 0;
        foreach (var s in Segments) sum += s.BytesDone;
        BytesDone = sum;
    }

    public override string ToString() => $"{FileName} [{State}]";
}
