using System.Net;
using System.Text;

namespace OpenDM.SelfTest;

/// <summary>
/// 本地 HTTP 测试服务器：支持 Range（206/Content-Range），带限速，用来验证分段并发与断点续传。
/// /file.bin 支持 Range；/norange 永远返回 200 全量（模拟不支持分段的服务器）；
/// /auth 要求带上 Cookie: sid=ok，否则 403（用来验证自定义请求头真的发出去了）。
/// </summary>
internal sealed class RangeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _data;
    private readonly int _chunkDelayMs;
    private CancellationTokenSource? _cts;

    public int Port { get; }

    /// <summary>服务器收到的最后一个 Cookie 请求头。</summary>
    public string LastCookie
    {
        get { lock (_reqLock) return _lastCookie; }
    }
    private string _lastCookie = "";

    /// <summary>服务器累计送出的字节数，用来判断续传有没有重复下载。</summary>
    public long BytesServed => Interlocked.Read(ref _served);
    private long _served;

    /// <summary>记录每个 Range 请求的起点，用来验证续传是不是精确接在暂停点上。</summary>
    public List<long> RangeStarts
    {
        get { lock (_reqLock) return _rangeStarts.ToList(); }
    }
    private readonly List<long> _rangeStarts = new();
    private readonly object _reqLock = new();

    public RangeServer(byte[] data, int chunkDelayMs = 4, int port = 0)
    {
        _data = data;
        _chunkDelayMs = chunkDelayMs;

        Port = port > 0 ? port : GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Run()
    {
        _cts = new CancellationTokenSource();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = System.Threading.Tasks.Task.Run(() => HandleAsync(ctx));
            }
        });
    }

    private async System.Threading.Tasks.Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // 需要 Cookie 的路径：没带对 Cookie 直接 403
            if (req.Url!.AbsolutePath.Contains("auth"))
            {
                var cookie = req.Headers["Cookie"] ?? "";
                lock (_reqLock) _lastCookie = cookie;
                if (!cookie.Contains("sid=ok"))
                {
                    res.StatusCode = 403;
                    res.Close();
                    return;
                }
            }

            if (!req.Url!.AbsolutePath.Contains("norange"))
                res.AddHeader("Accept-Ranges", "bytes");

            long start = 0, end = _data.Length - 1;
            bool wantsRange = false;

            var rangeHeader = req.Headers["Range"];
            bool rangeAllowed = !req.Url.AbsolutePath.Contains("norange");

            if (rangeAllowed && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var spec = rangeHeader.Substring(6).Split(',')[0];
                var parts = spec.Split('-');
                if (long.TryParse(parts[0], out var s)) start = s;
                if (parts.Length > 1 && long.TryParse(parts[1], out var e)) end = Math.Min(e, _data.Length - 1);
                wantsRange = true;
                lock (_reqLock) _rangeStarts.Add(start);
            }

            if (start > _data.Length - 1) { res.StatusCode = 416; res.Close(); return; }

            long len = end - start + 1;
            res.StatusCode = wantsRange ? 206 : 200;
            if (wantsRange) res.AddHeader("Content-Range", $"bytes {start}-{end}/{_data.Length}");
            res.ContentLength64 = len;
            res.ContentType = "application/octet-stream";
            res.AddHeader("Content-Disposition", "attachment; filename=\"payload.bin\"");

            if (req.HttpMethod == "HEAD") { res.Close(); return; }

            var buffer = new byte[32 * 1024];
            long pos = start;
            while (pos <= end)
            {
                int n = (int)Math.Min(buffer.Length, end - pos + 1);
                await res.OutputStream.WriteAsync(_data.AsMemory((int)pos, n));
                pos += n;
                Interlocked.Add(ref _served, n);
                if (_chunkDelayMs > 0) await System.Threading.Tasks.Task.Delay(_chunkDelayMs);
            }
            res.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
