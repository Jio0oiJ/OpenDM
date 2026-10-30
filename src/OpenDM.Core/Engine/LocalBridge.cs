using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenDM.Core.Models;

namespace OpenDM.Core.Engine;

/// <summary>
/// 本地接管接口：给浏览器扩展 / 其它程序一个「喂链接」的入口。
///
/// 为什么不用 HttpListener：Windows 上它绑定前缀通常要管理员权限或事先 netsh 注册 urlacl，
/// 而这个程序要能免安装直接双击跑。裸 TcpListener 绑 127.0.0.1 不需要任何权限，
/// HTTP 请求本身又足够简单，自己解析更省事。
///
/// 安全：只绑回环地址（外网连不上）+ 随机令牌（网页无法伪造，避免任意站点往你队列里塞文件）。
/// </summary>
public sealed class LocalBridge : IDisposable
{
    public const int DefaultPort = 65200;

    private readonly TaskManager _mgr;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public LocalBridge(TaskManager mgr, int port, string token)
    {
        _mgr = mgr;
        Port = port;
        Token = token;
    }

    public int Port { get; }
    public string Token { get; }
    public bool IsRunning => _listener is not null;

    /// <summary>接口日志（调试用）。</summary>
    public event Action<string>? Log;

    /// <summary>有其它实例请求「把窗口叫到前面来」（单实例运行时）。</summary>
    public event Action? ShowRequested;

    public bool Start(out string? error)
    {
        error = null;
        if (_listener is not null) return true;

        try
        {
            _listener = Bind(Port);
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            error = $"端口 {Port} 无法监听（可能已被其它程序占用）：{ex.Message}";
            _listener = null;
            return false;
        }
    }

    /// <summary>
    /// 先按独占方式绑；失败再带 SO_REUSEADDR 试一次。
    /// 原因：上一个 OpenDM 刚退出时端口会停在 TIME_WAIT，独占绑定会报"地址已在使用"，
    /// 用户看到的就是"重启后浏览器接管突然失效"。带 reuse 可以绑上 TIME_WAIT 的端口，
    /// 但如果真有别的程序在监听，两次都会失败 —— 不会误抢别人的端口。
    /// </summary>
    private static TcpListener Bind(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            return l;
        }
        catch (SocketException)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            l.Start();
            return l;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async System.Threading.Tasks.Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await HandleAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log?.Invoke("请求处理异常: " + ex.Message); }
                finally { try { client.Dispose(); } catch { } }
            }, CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- 协议

    private sealed class Request
    {
        public string Method = "GET";
        public string Path = "/";
        public readonly Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
    }

    private async System.Threading.Tasks.Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
        if (req is null) return;

        if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(stream, 204, "", "text/plain", timeout.Token).ConfigureAwait(false);
            return;
        }

        // 令牌校验：扩展在设置页里填同一个串；普通网页拿不到它
        if (!TokenOk(req))
        {
            await WriteJsonAsync(stream, 403,
                new { ok = false, error = "令牌不正确。请在 OpenDM 设置里复制令牌，填到扩展的选项页。" },
                timeout.Token).ConfigureAwait(false);
            return;
        }

        var path = req.Path.TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0) path = "/";

        switch (path)
        {
            case "/":
            case "/ping":
                await WriteJsonAsync(stream, 200, new
                {
                    ok = true,
                    app = "OpenDM",
                    version = "1.1",
                    active = _mgr.ActiveCount,
                    queued = _mgr.QueuedCount,
                    limit = _mgr.MaxConcurrentTasks
                }, timeout.Token).ConfigureAwait(false);
                return;

            case "/add":
                await HandleAddAsync(stream, req, timeout.Token).ConfigureAwait(false);
                return;

            case "/pauseall":
                _mgr.PauseAll();
                await WriteJsonAsync(stream, 200, new { ok = true }, timeout.Token).ConfigureAwait(false);
                return;

            case "/show":
                ShowRequested?.Invoke();
                await WriteJsonAsync(stream, 200, new { ok = true }, timeout.Token).ConfigureAwait(false);
                return;

            default:
                await WriteJsonAsync(stream, 404, new { ok = false, error = "未知接口 " + path },
                    timeout.Token).ConfigureAwait(false);
                return;
        }
    }

    private bool TokenOk(Request req)
    {
        if (string.IsNullOrEmpty(Token)) return true;

        if (req.Query.TryGetValue("token", out var q) && q == Token) return true;
        if (req.Headers.TryGetValue("X-OpenDM-Token", out var h) && h == Token) return true;

        // 允许免令牌访问的只有 ping，方便扩展探测端口
        return req.Path.TrimEnd('/').Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
               req.Path == "/";
    }

    private async System.Threading.Tasks.Task HandleAddAsync(NetworkStream stream, Request req,
                                                           CancellationToken ct)
    {
        var f = new Dictionary<string, string>(req.Query, StringComparer.OrdinalIgnoreCase);

        // POST 时允许用 JSON body 传参（字段名与 query 一致）
        if (req.Body.Length > 0 && req.Body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(req.Body);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        f[p.Name] = p.Value.GetString() ?? "";
                    else if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        // headers: { "Cookie": "...", "Referer": "..." }
                        foreach (var h in p.Value.EnumerateObject())
                            if (h.Value.ValueKind == JsonValueKind.String)
                                f["h:" + h.Name] = h.Value.GetString() ?? "";
                    }
                }
            }
            catch (JsonException) { /* 当作没有 body */ }
        }

        var url = Get(f, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            await WriteJsonAsync(stream, 400, new { ok = false, error = "缺少 url 参数" }, ct)
                .ConfigureAwait(false);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeader(headers, "Referer", Get(f, "referer"));
        AddHeader(headers, "Cookie", Get(f, "cookie"));
        AddHeader(headers, "User-Agent", Get(f, "ua") ?? Get(f, "useragent"));
        foreach (var kv in f)
            if (kv.Key.StartsWith("h:", StringComparison.OrdinalIgnoreCase))
                AddHeader(headers, kv.Key[2..], kv.Value);

        try
        {
            var task = await _mgr.AddAsync(
                url,
                folder: Get(f, "folder"),
                preferredName: Get(f, "filename"),
                connections: null,
                startNow: true,
                headers: headers.Count == 0 ? null : headers,
                ct: ct).ConfigureAwait(false);

            Log?.Invoke($"接管下载: {task.FileName}");
            await WriteJsonAsync(stream, 200, new
            {
                ok = true,
                id = task.Id,
                file = task.FileName,
                size = task.TotalBytes,
                state = task.State.ToString(),
                queued = task.State == TaskState.Queued
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, 200, new { ok = false, error = ex.Message }, ct)
                .ConfigureAwait(false);
        }
    }

    private static string? Get(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static void AddHeader(Dictionary<string, string> headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (RequestHeaders.IsForbidden(name)) return;
        headers[name] = value.Trim();
    }

    // ---------------------------------------------------------------- 读写

    private static readonly byte[] CrlfCrlf = { 13, 10, 13, 10 };

    private static async System.Threading.Tasks.Task<Request?> ReadRequestAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        int len = 0;
        int headEnd = -1;

        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            len += n;

            headEnd = IndexOf(buf, len, CrlfCrlf);
            if (headEnd >= 0) break;
        }

        if (headEnd < 0) return null;

        var head = Encoding.UTF8.GetString(buf, 0, headEnd);
        var lines = head.Split("\r\n");
        if (lines.Length == 0) return null;

        var req = new Request();
        var first = lines[0].Split(' ');
        if (first.Length >= 2)
        {
            req.Method = first[0].ToUpperInvariant();
            var target = first[1];

            var qIdx = target.IndexOf('?');
            req.Path = qIdx >= 0 ? target[..qIdx] : target;
            if (qIdx >= 0) ParseQuery(target[(qIdx + 1)..], req.Query);
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            req.Headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        // body：头部之后剩余的部分 + Content-Length 没读够时继续读
        int bodyStart = headEnd + 4;
        int have = len - bodyStart;

        int contentLength = 0;
        if (req.Headers.TryGetValue("Content-Length", out var cl))
            int.TryParse(cl, out contentLength);

        if (contentLength > 0 && contentLength < 1024 * 1024)
        {
            var bodyBuf = new byte[contentLength];
            int copied = Math.Min(have, contentLength);
            Buffer.BlockCopy(buf, bodyStart, bodyBuf, 0, copied);

            int got = copied;
            while (got < contentLength)
            {
                int n = await s.ReadAsync(bodyBuf.AsMemory(got, contentLength - got), ct).ConfigureAwait(false);
                if (n <= 0) break;
                got += n;
            }
            req.Body = Encoding.UTF8.GetString(bodyBuf, 0, got);
        }

        return req;
    }

    private static void ParseQuery(string query, Dictionary<string, string> into)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var k = idx >= 0 ? pair[..idx] : pair;
            var v = idx >= 0 ? pair[(idx + 1)..] : "";
            into[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
    }

    private static int IndexOf(byte[] buf, int len, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= len; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
                if (buf[i + j] != pattern[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static async System.Threading.Tasks.Task WriteJsonAsync(NetworkStream s, int code, object payload,
                                                                  CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await WriteAsync(s, code, json, "application/json; charset=utf-8", ct).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task WriteAsync(NetworkStream s, int code, string body,
                                                                string contentType, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {Reason(code)}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {bytes.Length}\r\n" +
                   "Access-Control-Allow-Origin: *\r\n" +
                   "Access-Control-Allow-Headers: Content-Type, X-OpenDM-Token\r\n" +
                   "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                   "Access-Control-Allow-Private-Network: true\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";

        await s.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), ct).ConfigureAwait(false);
        if (bytes.Length > 0)
            await s.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK"
    };

    // ---------------------------------------------------------------- 单实例转发

    /// <summary>
    /// 把链接交给已经打开的实例。返回 true 表示转交成功（调用方应直接退出）。
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> TryForwardAsync(int port, string token,
                                                                         string url,
                                                                         CancellationToken ct = default)
    {
        var path = "/add?url=" + Uri.EscapeDataString(url) + "&token=" + Uri.EscapeDataString(token);
        var text = await SendAsync(port, path, ct).ConfigureAwait(false);
        return text is not null && text.Contains("\"ok\":true");
    }

    /// <summary>请求已运行的实例把窗口显示出来。返回 true 表示那边确实在跑。</summary>
    public static async System.Threading.Tasks.Task<bool> TryShowAsync(int port, string token,
                                                                      CancellationToken ct = default)
    {
        var path = "/show?token=" + Uri.EscapeDataString(token);
        var text = await SendAsync(port, path, ct).ConfigureAwait(false);
        return text is not null && text.Contains("\"ok\":true");
    }

    private static async System.Threading.Tasks.Task<string?> SendAsync(int port, string path,
                                                                       CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(2));

            await tcp.ConnectAsync(IPAddress.Loopback, port, connectCts.Token).ConfigureAwait(false);

            var req = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";

            using var s = tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes(req).AsMemory(), connectCts.Token).ConfigureAwait(false);

            var buf = new byte[4096];
            var sb = new StringBuilder();
            int n;
            while ((n = await s.ReadAsync(buf.AsMemory(), connectCts.Token).ConfigureAwait(false)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

            var text = sb.ToString();
            return text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
