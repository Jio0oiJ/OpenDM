using System.Diagnostics;

namespace OpenDM.Core.Engine;

/// <summary>滑动窗口测速：每秒采样一次，取最近 3 秒的平均，避免数字乱跳。</summary>
public sealed class SpeedMeter
{
    private readonly long[] _samples = new long[4];
    private long _bytes;
    private long _lastBytes;
    private long _lastTicks;
    private readonly object _lock = new();

    public SpeedMeter()
    {
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public void Add(long n) => Interlocked.Add(ref _bytes, n);

    /// <summary>返回当前估算速度（字节/秒）。</summary>
    public double Sample()
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (elapsed < 0.35) return Current;

            var total = Interlocked.Read(ref _bytes);
            var delta = total - _lastBytes;
            _lastBytes = total;
            _lastTicks = now;

            for (int i = _samples.Length - 1; i > 0; i--) _samples[i] = _samples[i - 1];
            _samples[0] = delta;

            int n = _samples.Count(s => s > 0);
            if (n == 0) { Current = 0; return 0; }
            long sum = 0;
            for (int i = 0; i < n; i++) sum += _samples[i];
            Current = sum / (double)n / elapsed;
            return Current;
        }
    }

    public double Current { get; private set; }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_samples);
            _bytes = 0;
            _lastBytes = 0;
            Current = 0;
            _lastTicks = Stopwatch.GetTimestamp();
        }
    }
}
