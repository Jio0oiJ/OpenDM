using System.Diagnostics;

namespace OpenDM.Core.Engine;

/// <summary>
/// 全局令牌桶限速。所有任务、所有分段共用同一个桶，所以「限速 1MB/s」指的是总带宽，不是每段 1MB/s。
///
/// 实现要点：不做「等够令牌再发」的忙等，而是允许桶透支成负数，然后按欠下的量睡相应时间。
/// 这样即使单次读取的块（64KB）比设定速率还大（比如限 20KB/s），也不会陷入永远等不到令牌的死循环。
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private double _tokens;
    private long _lastTicks;
    private long _limitBps;

    public RateLimiter(long limitBps = 0)
    {
        _limitBps = Math.Max(0, limitBps);
        _lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>每秒允许的字节数；0 表示不限速。</summary>
    public long LimitBps
    {
        get => Interlocked.Read(ref _limitBps);
        set
        {
            var v = Math.Max(0, value);
            lock (_gate)
            {
                // 换档时重新起算，避免改小后忽然冲出一大股
                _lastTicks = Stopwatch.GetTimestamp();
                _tokens = 0;
                Interlocked.Exchange(ref _limitBps, v);
            }
        }
    }

    public bool IsLimited => LimitBps > 0;

    /// <summary>领取 <paramref name="bytes"/> 字节的配额，必要时异步等待。</summary>
    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        double waitSeconds;

        lock (_gate)
        {
            long limit = Interlocked.Read(ref _limitBps);
            if (limit <= 0) return;

            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;

            // 桶容量 = 1 秒的量，允许一点点突发，但不会积累成大爆发
            _tokens = Math.Min(_tokens + elapsed * limit, limit);
            _tokens -= bytes;

            waitSeconds = _tokens >= 0 ? 0 : -_tokens / limit;
        }

        if (waitSeconds <= 0) return;
        if (waitSeconds > 5) waitSeconds = 5; // 防御：单次最多睡 5 秒，下一轮重算

        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
    }
}
