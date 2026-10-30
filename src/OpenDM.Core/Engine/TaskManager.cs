using System.Net;
using OpenDM.Core.Models;

namespace OpenDM.Core.Engine;

/// <summary>
/// 任务的增删起停 + 队列调度 + 全局 HttpClient + 断点状态自动落盘。
/// 刻意不做 UI 依赖：它只抛事件，由界面层决定怎么 marshal 到 UI 线程。
///
/// 队列规则：同时最多跑 MaxConcurrentTasks 个任务，其余排队（State = Queued）。
/// 任何一个任务结束（完成 / 出错 / 暂停）都会腾出一个位子，队列里的下一个自动上场。
/// </summary>
public sealed class TaskManager : IDisposable
{
    /// <summary>单个任务允许的最大并发分段数。</summary>
    public const int MaxConnections = 64;

    /// <summary>同时下载的任务数上限。</summary>
    public const int MaxConcurrentLimit = 16;

    private readonly object _q = new();
    private readonly Dictionary<string, DownloadJob> _jobs = new();
    private readonly List<string> _queue = new();
    private readonly List<HttpClient> _retired = new();
    private readonly RateLimiter _limiter = new();
    private readonly System.Threading.Timer _autosave;

    private HttpClient _http;
    private bool _pumping;
    private int _maxConcurrent = 3;

    /// <summary>新任务创建后触发（可能在后台线程）。</summary>
    public event Action<DownloadTask>? TaskCreated;

    /// <summary>队列长度 / 并发位发生变化（可能在后台线程）。</summary>
    public event Action? QueueChanged;

    public string DefaultFolder { get; set; }
    public int DefaultConnections { get; set; } = 8;

    public TaskManager(int defaultConnections = 8)
    {
        _http = CreateClient(ProxySetting.System);

        DefaultConnections = Math.Clamp(defaultConnections, 1, MaxConnections);
        DefaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // 每 5 秒把进行中任务的进度刷到磁盘，断电也不丢
        _autosave = new System.Threading.Timer(_ => SaveActive(), null, 5000, 5000);
    }

    // ---------------------------------------------------------------- 网络

    private static HttpClient CreateClient(ProxySetting p)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            // 每个分段占一条连接，上限要跟着最大分段数走，否则多出来的段会排队等连接
            MaxConnectionsPerServer = MaxConnections * 2,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = p.UseProxy,
            Proxy = p.Proxy
        };

        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenDM/1.0");
        return http;
    }

    /// <summary>当前代理设置（跟随系统 / 直连 / 指定代理）。</summary>
    public ProxySetting Proxy { get; private set; } = ProxySetting.System;

    /// <summary>
    /// 换代理。旧客户端不立刻 Dispose —— 正在传输的请求还挂在上面，
    /// 立即释放会抛 ObjectDisposedException；等管理器退出时统一回收。
    /// 对进行中的任务，下一次分段请求就会走新代理。
    /// </summary>
    public void SetProxy(string? spec)
    {
        var p = ProxySetting.Parse(spec);
        Proxy = p;

        var old = _http;
        _http = CreateClient(p);
        lock (_retired) _retired.Add(old);
    }

    // ---------------------------------------------------------------- 队列

    /// <summary>同时下载的任务数。</summary>
    public int MaxConcurrentTasks
    {
        get { lock (_q) return _maxConcurrent; }
        set
        {
            lock (_q) _maxConcurrent = Math.Clamp(value, 1, MaxConcurrentLimit);
            Pump();
        }
    }

    /// <summary>全局速度上限（字节/秒），0 = 不限。</summary>
    public long SpeedLimitBps
    {
        get => _limiter.LimitBps;
        set => _limiter.LimitBps = value;
    }

    public RateLimiter Limiter => _limiter;

    public int ActiveCount
    {
        get { lock (_q) return _jobs.Values.Count(j => j.IsRunning); }
    }

    public int QueuedCount
    {
        get { lock (_q) return _queue.Count; }
    }

    /// <summary>管理器内所有任务对象的快照（诊断与测试用）。</summary>
    public IReadOnlyList<DownloadTask> Snapshot()
    {
        lock (_q) return _jobs.Values.Select(j => j.Task).ToList();
    }

    private bool HasSlotLocked() => _jobs.Values.Count(j => j.IsRunning) < _maxConcurrent;

    private void RemoveFromQueueLocked(string id) => _queue.RemoveAll(x => x == id);
    private bool InQueueLocked(string id) => _queue.Contains(id);

    /// <summary>把队列里能跑的尽量启动起来。结束的任务会回调这里。</summary>
    private void Pump()
    {
        lock (_q)
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (_queue.Count > 0 && HasSlotLocked())
                {
                    var id = _queue[0];
                    _queue.RemoveAt(0);

                    if (!_jobs.TryGetValue(id, out var job)) continue;
                    if (job.IsRunning) continue;
                    if (job.Task.State is TaskState.Completed or TaskState.Paused or TaskState.Error) continue;

                    job.Task.State = TaskState.Downloading;
                    job.Start();   // 同步把 IsRunning 置真，下一次 HasSlotLocked 立刻算上
                }
            }
            finally { _pumping = false; }
        }

        QueueChanged?.Invoke();
    }

    private void OnJobStopped(DownloadJob job)
    {
        // 任务结束后腾出并发位，队列里的下一个上场
        Pump();
    }

    // ---------------------------------------------------------------- 任务

    public IReadOnlyList<DownloadTask> LoadExisting() => ResumeStore.LoadAll();

    /// <summary>探测 → 建段 → 落盘 → 开始（或排队）。返回已创建的任务。</summary>
    public async Task<DownloadTask> AddAsync(
        string url,
        string? folder = null,
        string? preferredName = null,
        int? connections = null,
        bool startNow = true,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        url = url.Trim();
        folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder!;
        Directory.CreateDirectory(folder);

        var probe = await HttpProbe.ProbeAsync(_http, url, headers, ct).ConfigureAwait(false);

        var name = !string.IsNullOrWhiteSpace(preferredName)
            ? HttpProbe.Sanitize(preferredName!)
            : (probe.FileName ?? "download.bin");
        if (string.IsNullOrWhiteSpace(name)) name = "download.bin";

        var savePath = UniquePath(Path.Combine(folder, name));
        int conns = Math.Clamp(connections ?? DefaultConnections, 1, MaxConnections);

        var task = new DownloadTask
        {
            Url = probe.FinalUrl,
            SavePath = savePath,
            ContentType = probe.ContentType,
            SupportsRange = probe.SupportsRange,
            TotalBytes = probe.ContentLength,
            Connections = conns,
            Headers = headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            IsPrepared = true,
            State = TaskState.Queued,
            Segments = BuildSegments(probe.ContentLength, probe.SupportsRange, conns)
        };

        ResumeStore.Save(task);
        TaskCreated?.Invoke(task);

        if (startNow) Start(task);
        return task;
    }

    /// <summary>开始 / 继续。并发位满了就进队列。</summary>
    public void Start(DownloadTask task)
    {
        if (task.State == TaskState.Completed) return;

        DownloadJob job;
        lock (_q)
        {
            if (!_jobs.TryGetValue(task.Id, out job!))
            {
                job = new DownloadJob(task, () => _http, _limiter, OnJobStopped);
                _jobs[task.Id] = job;
            }
        }

        if (job.IsRunning) return;

        task.Error = null;
        task.RecomputeBytes();

        bool canGo;
        lock (_q)
        {
            canGo = HasSlotLocked();
            if (!canGo)
            {
                if (!InQueueLocked(task.Id)) _queue.Add(task.Id);
                task.State = TaskState.Queued;
            }
            else
            {
                RemoveFromQueueLocked(task.Id);
                task.State = TaskState.Downloading;
            }
        }

        if (!canGo)
        {
            ResumeStore.Save(task);
            QueueChanged?.Invoke();
            return;
        }

        job.Start();
        QueueChanged?.Invoke();
    }

    /// <summary>替换某个任务的请求头（Cookie 过期后在原任务上改，接着续传）。</summary>
    public void SetHeaders(DownloadTask task, IReadOnlyDictionary<string, string>? headers)
    {
        task.Headers = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        ResumeStore.Save(task);
        task.Refresh();
    }

    public void Pause(DownloadTask task)
    {
        lock (_q) RemoveFromQueueLocked(task.Id);

        if (_jobs.TryGetValue(task.Id, out var job)) job.Pause();

        if (task.State is TaskState.Downloading or TaskState.Queued or TaskState.Probing)
            task.State = TaskState.Paused;

        ResumeStore.Save(task);
        QueueChanged?.Invoke();
    }

    public void Remove(DownloadTask task, bool deleteFile)
    {
        lock (_q) RemoveFromQueueLocked(task.Id);

        if (_jobs.TryGetValue(task.Id, out var job))
        {
            job.Pause();
            job.Dispose();
            _jobs.Remove(task.Id);
        }

        ResumeStore.Delete(task.Id);

        if (deleteFile)
        {
            try { if (File.Exists(task.SavePath)) File.Delete(task.SavePath); } catch { }
        }

        Pump();
    }

    public void PauseAll()
    {
        List<DownloadTask> snapshot;
        lock (_q)
        {
            snapshot = _jobs.Values.Select(j => j.Task).ToList();
            _queue.Clear();
        }

        foreach (var task in snapshot)
        {
            if (task.State is TaskState.Downloading or TaskState.Queued) Pause(task);
        }
    }

    /// <summary>队列整体前移：把所有排队任务按加入顺序跑起来（受并发上限约束）。</summary>
    public void StartAllQueued() => Pump();

    private void SaveActive()
    {
        foreach (var job in _jobs.Values.ToArray())
        {
            var t = job.Task;
            if (t.State is TaskState.Downloading or TaskState.Queued) ResumeStore.Save(t);
        }
    }

    /// <summary>
    /// 分片策略：按连接数均分，但每段至少约 256KB，避免小文件开一堆连接反而更慢。
    /// 不支持 Range 或长度未知 → 单段顺序下载（不可续传）。
    /// </summary>
    private static List<Segment> BuildSegments(long total, bool rangeOk, int connections)
    {
        var list = new List<Segment>();

        if (!rangeOk || total <= 0)
        {
            list.Add(new Segment { Start = 0, End = Segment.UnknownEnd, Position = 0 });
            return list;
        }

        int n = Math.Clamp(connections, 1, MaxConnections);
        n = (int)Math.Min(n, Math.Max(1, total / (256 * 1024)));
        if (n <= 1)
        {
            list.Add(new Segment { Start = 0, End = total - 1, Position = 0 });
            return list;
        }

        long chunk = total / n;
        for (int i = 0; i < n; i++)
        {
            long start = i * chunk;
            long end = i == n - 1 ? total - 1 : start + chunk - 1;
            list.Add(new Segment { Start = start, End = end, Position = start });
        }
        return list;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !ResumeStore.FileTaken(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !ResumeStore.FileTaken(candidate)) return candidate;
        }
        return path;
    }

    public void Dispose()
    {
        _autosave.Dispose();
        foreach (var job in _jobs.Values)
        {
            try { job.Pause(); job.Dispose(); } catch { }
        }
        _jobs.Clear();
        _http.Dispose();

        lock (_retired)
        {
            foreach (var c in _retired) { try { c.Dispose(); } catch { } }
            _retired.Clear();
        }
    }
}
