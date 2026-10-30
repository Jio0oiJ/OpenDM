using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using OpenDM.Core.Models;

namespace OpenDM.Core.Engine;

/// <summary>
/// 单个任务的执行体。一个 job 持有：目标文件的 FileStream、分段数组、CancellationTokenSource、测速器。
/// 暂停 = 取消令牌；续传 = 新建令牌，从各段 Position 继续发 Range 请求。文件就一份，多段并发写入。
/// </summary>
internal sealed class DownloadJob : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private const int MaxAttempts = 4;

    private readonly DownloadTask _t;
    private readonly Func<HttpClient> _http;
    private readonly RateLimiter _limiter;
    private readonly Action<DownloadJob>? _onStopped;
    private readonly object _fileLock = new();
    private readonly object _gate = new();
    private readonly SpeedMeter _speed = new();

    private FileStream? _fs;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _sampler;
    private long _done;
    private string? _fatal;
    private bool _running;
    private bool _disposed;

    public DownloadJob(DownloadTask task, Func<HttpClient> http, RateLimiter limiter,
                       Action<DownloadJob>? onStopped = null)
    {
        _t = task;
        _http = http;
        _limiter = limiter;
        _onStopped = onStopped;
        _done = task.BytesDone;
    }

    public DownloadTask Task => _t;

    /// <summary>是否正在跑（队列调度用它判断有没有空位）。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running || _disposed) return;
            _running = true;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _speed.Reset();
        _done = _t.BytesDone;

        _sampler?.Dispose();
        _sampler = new System.Threading.Timer(_ =>
        {
            _t.SpeedBps = _speed.Sample();
        }, null, 500, 500);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await RunAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _t.State = TaskState.Error; _t.Error = ex.Message; }
            finally
            {
                _sampler?.Dispose();
                _sampler = null;
                _t.SpeedBps = 0;
                lock (_gate) _running = false;
                ResumeStore.Save(_t);

                // 通知调度器腾出一个并发位（队列里的下一个该上场了）
                try { _onStopped?.Invoke(this); } catch { }
            }
        }, CancellationToken.None);
    }

    private async System.Threading.Tasks.Task RunAsync(CancellationToken ct)
    {
        _fatal = null;
        _t.Error = null;
        _t.State = TaskState.Downloading;

        EnsureFile();

        var pending = _t.Segments.Where(s => !s.IsDone).ToList();
        if (pending.Count == 0) { Complete(); return; }

        var workers = pending.Select(s => RunSegmentAsync(s, ct)).ToArray();
        try
        {
            await System.Threading.Tasks.Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        lock (_fileLock)
        {
            try { _fs?.Flush(); } catch { }
        }

        if (_t.Segments.All(s => s.IsDone))
        {
            Complete();
            return;
        }

        if (ct.IsCancellationRequested)
        {
            _t.State = TaskState.Paused;
            return;
        }

        _t.State = TaskState.Error;
        _t.Error ??= _fatal ?? "下载中断（连接被服务器关闭）";
    }

    private async System.Threading.Tasks.Task RunSegmentAsync(Segment seg, CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested && !seg.IsDone)
        {
            attempt++;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _t.Url);
                long? to = seg.End >= Segment.UnknownEnd ? null : seg.End;
                req.Headers.Range = new RangeHeaderValue(seg.Position, to);
                RequestHeaders.Apply(req, _t.Headers);

                using var resp = await _http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                            .ConfigureAwait(false);

                bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                if (!partial && seg.Position > seg.Start)
                    throw new InvalidOperationException("服务器忽略了 Range 请求，该资源无法断点续传");

                resp.EnsureSuccessStatusCode();

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                    while (!ct.IsCancellationRequested)
                    {
                        if (seg.IsDone) break;

                        int n = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                        if (n <= 0) break;

                        long writable = seg.End >= Segment.UnknownEnd
                            ? n
                            : Math.Min(n, seg.End - seg.Position + 1);
                        if (writable <= 0) break;

                        // 全局限速：所有分段共用一个令牌桶，先领配额再落盘
                        await _limiter.ConsumeAsync((int)writable, ct).ConfigureAwait(false);

                        WriteAt(seg.Position, buffer, (int)writable);
                        seg.Position += writable;

                        Interlocked.Add(ref _done, writable);
                        _t.BytesDone = Interlocked.Read(ref _done);
                        _speed.Add(writable);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (seg.IsDone) return;

                if (seg.End >= Segment.UnknownEnd)
                {
                    // 长度未知：读到 EOF 即该段完成
                    seg.End = Math.Max(seg.Start, seg.Position - 1);
                    return;
                }

                throw new IOException("连接提前结束");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;

                if (attempt >= MaxAttempts)
                {
                    Interlocked.CompareExchange(ref _fatal, ex.Message, null);
                    return;
                }

                try { await System.Threading.Tasks.Task.Delay(400 * attempt, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void EnsureFile()
    {
        lock (_fileLock)
        {
            if (_fs is not null) return;

            var dir = Path.GetDirectoryName(_t.SavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _fs = new FileStream(_t.SavePath, FileMode.OpenOrCreate, FileAccess.Write,
                                 FileShare.Read, 1, FileOptions.None);

            if (_t.TotalBytes > 0 && _fs.Length != _t.TotalBytes)
            {
                try { _fs.SetLength(_t.TotalBytes); } catch { }
            }
        }
    }

    private void WriteAt(long offset, byte[] buffer, int count)
    {
        lock (_fileLock)
        {
            if (_fs is null) throw new InvalidOperationException("文件句柄未就绪");
            _fs.Position = offset;
            _fs.Write(buffer, 0, count);
        }
    }

    private void Complete()
    {
        lock (_fileLock)
        {
            try
            {
                _fs?.Flush();
                if (_t.TotalBytes <= 0 && _fs is not null) _t.TotalBytes = _fs.Length;
                _fs?.Dispose();
            }
            catch { }
            _fs = null;
        }

        _t.BytesDone = _t.TotalBytes > 0 ? _t.TotalBytes : Interlocked.Read(ref _done);
        _t.State = TaskState.Completed;
        _t.CompletedAt = DateTime.Now;
        _t.SpeedBps = 0;
        _t.Error = null;
    }

    public void Pause()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { }
        }
    }

    public void Dispose()
    {
        Pause();
        _disposed = true;
        _sampler?.Dispose();
        _sampler = null;
        lock (_fileLock)
        {
            try { _fs?.Flush(); } catch { }
            try { _fs?.Dispose(); } catch { }
            _fs = null;
        }
        _cts?.Dispose();
        _cts = null;
    }
}
