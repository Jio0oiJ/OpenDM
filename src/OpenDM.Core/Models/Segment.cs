using System.ComponentModel;

namespace OpenDM.Core.Models;

/// <summary>
/// 一个分段：文件的 [Start, End] 闭区间字节范围。
/// Position 是"下一个待写入的绝对偏移"，也是断点续传的唯一依据。
/// </summary>
public sealed class Segment : INotifyPropertyChanged
{
    public long Start { get; set; }

    /// <summary>闭区间终点；长度未知时取 UnknownEnd，EOF 后回填为 Position-1。</summary>
    public long End { get; set; }

    public long Position { get; set; }

    public const long UnknownEnd = long.MaxValue - 1;

    public long Length => End >= Start ? End - Start + 1 : 0;

    public long BytesDone => Math.Max(0, Math.Min(Position - Start, Length == 0 ? long.MaxValue : Length));

    public bool IsDone => Position > End;

    public double Progress => Length <= 0 ? 0 : Math.Clamp((double)(Position - Start) / Length, 0, 1);

    /// <summary>由 UI 线程的刷新定时器调用，让绑定重新读取进度。</summary>
    public void NotifyProgress()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => $"[{Start}-{End}] pos={Position}";
}
