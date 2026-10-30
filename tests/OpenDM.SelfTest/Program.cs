using System.Diagnostics;
using System.Security.Cryptography;
using OpenDM.Core;
using OpenDM.Core.Engine;
using OpenDM.Core.Models;

namespace OpenDM.SelfTest;

internal static class Program
{
    private static int _failures;

    private static async System.Threading.Tasks.Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 自测不写用户真实的数据目录，任务状态放临时目录里
        var sandbox = Path.Combine(Path.GetTempPath(), "opendm-selftest-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(sandbox);
        Environment.SetEnvironmentVariable("OPEN_DM_DATA_DIR", sandbox);

        // 供 GUI 端到端测试：dotnet run -- serve 8099 60
        if (args.Length >= 2 && args[0] == "serve")
        {
            int port = int.Parse(args[1]);
            int mb = args.Length > 2 ? int.Parse(args[2]) : 60;
            var payload = MakeData(mb * 1024 * 1024);
            var srcPath = Path.Combine(Path.GetTempPath(), "opendm-serve-src.bin");
            await File.WriteAllBytesAsync(srcPath, payload);
            Console.WriteLine($"SOURCE={srcPath}");
            Console.WriteLine($"SHA={Sha(payload)}");
            using var srv = new RangeServer(payload, chunkDelayMs: 12, port: port);
            srv.Run();
            Console.WriteLine($"SERVING http://127.0.0.1:{port}/file.bin  ({mb} MB)");
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(15));
            return 0;
        }

        Console.WriteLine("=== OpenDM 引擎自测 ===\n");

        await TestSegmentedWithResume();
        await TestNoRangeFallback();
        TestHeaderParsing();
        await Test64ConnectionsWithCookie();
        await TestQueueLimit();
        await TestGlobalRateLimit();
        await TestLocalBridge();
        TestProxyParsing();

        Console.WriteLine(_failures == 0 ? "\n全部通过 ✓" : $"\n{_failures} 项失败 ✗");

        try { Directory.Delete(sandbox, true); } catch { }
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 用例 1

    private static async System.Threading.Tasks.Task TestSegmentedWithResume()
    {
        Console.WriteLine("[用例 1] 8 段并发 + 40% 处暂停 + 重启续传");

        var data = MakeData(12 * 1024 * 1024 + 12345);
        var expected = Sha(data);
        using var server = new RangeServer(data);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        DownloadTask task;
        var m1 = new TaskManager(8) { DefaultFolder = dir };
        try
        {
            task = await m1.AddAsync($"http://127.0.0.1:{server.Port}/file.bin", dir, "resume-test.bin", 8);
            Console.WriteLine($"  探测: 大小={task.TotalBytes}  分段={task.Segments.Count}  支持Range={task.SupportsRange}");
            Check(task.Segments.Count == 8, $"应切成 8 段，实际 {task.Segments.Count}");
            Check(task.TotalBytes == data.Length, "Content-Length 不一致");

            // 等到 35% 以上
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30) && task.BytesDone < data.Length * 0.35)
                await System.Threading.Tasks.Task.Delay(50);

            m1.Pause(task);
            await System.Threading.Tasks.Task.Delay(700);

            long atPause = task.BytesDone;
            Console.WriteLine($"  暂停于 {atPause * 100.0 / data.Length:F1}%  状态={task.State}");
            Console.WriteLine($"  暂停时分段位置: {string.Join(" ", task.Segments.Take(4).Select(s => $"{s.Position - s.Start}/{s.Length}"))} ...");
            Check(task.State == TaskState.Paused, "暂停后状态应为 Paused");
            Check(atPause > 0 && atPause < data.Length, "暂停点应在中间");

            m1.Dispose();

            // 模拟进程重启：新 manager 从磁盘恢复
            var m2 = new TaskManager(8) { DefaultFolder = dir };
            var loaded = m2.LoadExisting().FirstOrDefault(t => t.SavePath == task.SavePath);
            Check(loaded is not null, "重启后应从磁盘恢复任务");
            if (loaded is null) { m2.Dispose(); return; }

            long recovered = 0;
            foreach (var s in loaded.Segments) recovered += s.BytesDone;
            Console.WriteLine($"  恢复读到: 已下载={recovered}  分段数={loaded.Segments.Count}  状态={loaded.State}");
            Check(recovered > atPause * 0.9, "恢复的字节数应接近暂停点");
            Check(loaded.Segments.Count == 8, "恢复后分段结构应保持");

            // 落盘允许比内存晚一块缓冲（取消瞬间还有一块在写），但不能更多
            Check(Math.Abs(recovered - atPause) <= 8 * 65536,
                  $"持久化进度与暂停点偏差过大：磁盘 {recovered} vs 内存 {atPause}");

            var persistedPositions = loaded.Segments.Select(s => s.Position).ToHashSet();
            var persistedSnapshot = string.Join(", ", loaded.Segments.Select(s => s.Position));
            int reqBefore = server.RangeStarts.Count;

            m2.Start(loaded);

            var sw2 = Stopwatch.StartNew();
            while (sw2.Elapsed < TimeSpan.FromSeconds(90) && loaded.State != TaskState.Completed)
                await System.Threading.Tasks.Task.Delay(100);

            Console.WriteLine($"  续传结束: 状态={loaded.State}  耗时={sw2.Elapsed.TotalSeconds:F1}s");
            Check(loaded.State == TaskState.Completed, $"续传后应完成，实际 {loaded.State} {loaded.Error}");

            var actual = Sha(await File.ReadAllBytesAsync(loaded.SavePath));
            Check(actual == expected, "续传后文件 SHA256 与原文件不一致（文件被写坏了）");
            Console.WriteLine($"  SHA256 {(actual == expected ? "一致 ✓" : "不一致 ✗")}  {actual[..16]}...");

            // 关键校验：续传时每段的 Range 起点必须等于持久化的断点位置，说明没有从 0 重下
            var resumedStarts = server.RangeStarts.Skip(reqBefore).ToList();
            Console.WriteLine($"  续传 Range 起点: {string.Join(", ", resumedStarts)}");
            Console.WriteLine($"  持久化断点  : {persistedSnapshot}");
            Check(resumedStarts.Count > 0, "续传应重新发起 Range 请求");
            Check(resumedStarts.All(p => persistedPositions.Contains(p)),
                  "存在续传请求没有从持久化断点开始（等于重下了已完成的字节）");
            Check(resumedStarts.All(p => p > 0), "续传请求不应出现 0 起点（说明整段重下）");

            m2.Dispose();
        }
        finally
        {
            try { m1.Dispose(); } catch { }
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 2

    private static async System.Threading.Tasks.Task TestNoRangeFallback()
    {
        Console.WriteLine("[用例 2] 服务器不支持 Range → 单流降级");

        var data = MakeData(3 * 1024 * 1024);
        var expected = Sha(data);
        using var server = new RangeServer(data, chunkDelayMs: 0);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        using var m = new TaskManager(8) { DefaultFolder = dir };
        var task = await m.AddAsync($"http://127.0.0.1:{server.Port}/norange", dir, "norange.bin", 8);

        Check(!task.SupportsRange, "应识别出不支持 Range");
        Check(task.Segments.Count == 1, $"降级后应为单段，实际 {task.Segments.Count}");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && task.State != TaskState.Completed)
            await System.Threading.Tasks.Task.Delay(50);

        Check(task.State == TaskState.Completed, $"应完成，实际 {task.State} {task.Error}");
        var actual = Sha(await File.ReadAllBytesAsync(task.SavePath));
        Check(actual == expected, "单流下载内容不一致");
        Console.WriteLine($"  完成: {Fmt.Size(task.BytesDone)}  SHA {(actual == expected ? "一致 ✓" : "不一致 ✗")}");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 3

    private static void TestHeaderParsing()
    {
        Console.WriteLine("[用例 3] Cookie / 请求头解析（裸串、头行、cURL、cookies.txt）");

        // 1) 裸 Cookie 串
        var a = RequestHeaders.Parse("a=1; b=2");
        Check(a.Count == 1 && a["Cookie"] == "a=1; b=2", $"裸 Cookie 串解析错：{Show(a)}");

        // 2) 请求头行
        var b = RequestHeaders.Parse("Cookie: sid=abc; uid=7\nReferer: https://x.com/p");
        Check(b.GetValueOrDefault("Cookie") == "sid=abc; uid=7", $"Cookie 头解析错：{Show(b)}");
        Check(b.GetValueOrDefault("Referer") == "https://x.com/p", $"Referer 头解析错：{Show(b)}");

        // 3) cURL（Chrome 的 Copy as cURL）
        var curl = "curl 'https://x.com/f.bin' -H 'Cookie: sid=abc' -H 'Referer: https://x.com/' " +
                   "-A 'Mozilla/5.0 Test' --header 'X-Token: t1' -b 'z=9' -e 'https://ref/' --compressed";
        var c = RequestHeaders.Parse(curl);
        Check(c.GetValueOrDefault("Cookie") == "sid=abc; z=9", $"cURL 的 Cookie 合并错：{Show(c)}");
        Check(c.GetValueOrDefault("User-Agent") == "Mozilla/5.0 Test", $"cURL 的 UA 错：{Show(c)}");
        Check(c.GetValueOrDefault("X-Token") == "t1", $"cURL 的 --header 错：{Show(c)}");
        Check(c.GetValueOrDefault("Referer") == "https://ref/", $"cURL 的 -e 错：{Show(c)}");

        // 4) Netscape cookies.txt
        var netscape = "# Netscape HTTP Cookie File\n.x.com\tTRUE\t/\tFALSE\t0\tn1\tv1\n.x.com\tTRUE\t/\tTRUE\t0\tn2\tv2\n";
        var d = RequestHeaders.Parse(netscape);
        Check(d.GetValueOrDefault("Cookie") == "n1=v1; n2=v2", $"cookies.txt 解析错：{Show(d)}");

        // 5) 混合 + 危险头必须被丢弃
        var e = RequestHeaders.Parse("Cookie: k=v\nRange: bytes=0-1\nHost: evil.com\nContent-Length: 10");
        Check(e.GetValueOrDefault("Cookie") == "k=v", $"混合解析错：{Show(e)}");
        Check(!e.ContainsKey("Range"), "用户写的 Range 必须被忽略（否则会下坏文件）");
        Check(!e.ContainsKey("Host"), "Host 不应被用户覆盖");
        Check(!e.ContainsKey("Content-Length"), "Content-Length 不应被用户覆盖");

        // 6) 换行注入要被压平
        var f = RequestHeaders.Parse("X-A: v1\r\nX-B: v2");
        Check(f.Count == 2, $"应识别 2 个头，实际 {Show(f)}");

        // 7) 空输入
        Check(RequestHeaders.Parse("   ").Count == 0, "空白输入应解析为空");

        Console.WriteLine();
    }

    private static string Show(Dictionary<string, string> h) =>
        h.Count == 0 ? "(空)" : string.Join(", ", h.Select(kv => $"{kv.Key}={kv.Value}"));

    // ---------------------------------------------------------------- 用例 4

    private static async System.Threading.Tasks.Task Test64ConnectionsWithCookie()
    {
        Console.WriteLine("[用例 4] 64 段并发 + 自定义 Cookie（无 Cookie 应 403）");

        var data = MakeData(24 * 1024 * 1024);
        var expected = Sha(data);
        using var server = new RangeServer(data, chunkDelayMs: 0);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        var url = $"http://127.0.0.1:{server.Port}/auth/file.bin";

        using (var m0 = new TaskManager(8) { DefaultFolder = dir })
        {
            bool denied = false;
            try { await m0.AddAsync(url, dir, "auth.bin", 4); }
            catch (HttpRequestException ex) when (ex.Message.Contains("403")) { denied = true; }
            Check(denied, "不带 Cookie 时应被 403 拒绝");
            Console.WriteLine($"  无 Cookie → 403 ✓");
        }

        var headers = RequestHeaders.Parse("Cookie: sid=ok; u=1");
        Check(headers.GetValueOrDefault("Cookie") == "sid=ok; u=1", "Cookie 准备失败");

        using var m = new TaskManager(8) { DefaultFolder = dir };
        var task = await m.AddAsync(url, dir, "auth.bin", 64, headers: headers);

        Console.WriteLine($"  探测: 大小={task.TotalBytes}  分段={task.Segments.Count}  连接数={task.Connections}");
        Check(task.Connections == 64, $"连接数应为 64，实际 {task.Connections}");
        Check(task.Segments.Count == 64, $"24MB 应切成 64 段，实际 {task.Segments.Count}");
        Check(task.HasHeaders, "任务应带上请求头");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(120) && task.State != TaskState.Completed)
            await System.Threading.Tasks.Task.Delay(50);

        Check(task.State == TaskState.Completed, $"应完成，实际 {task.State} {task.Error}");
        var actual = Sha(await File.ReadAllBytesAsync(task.SavePath));
        Check(actual == expected, "64 段并发下载内容不一致");
        Check(server.LastCookie.Contains("sid=ok"), $"服务器应收到 Cookie，实际「{server.LastCookie}」");

        // 重启后请求头要能跟着断点一起恢复
        var resumed = new TaskManager(8) { DefaultFolder = dir };
        var loaded = resumed.LoadExisting().FirstOrDefault(t => t.SavePath == task.SavePath);
        Check(loaded is not null && loaded.Headers.GetValueOrDefault("Cookie") == "sid=ok; u=1",
              "重启后 Cookie 应从磁盘恢复");
        resumed.Dispose();

        Console.WriteLine($"  完成: {Fmt.Size(task.BytesDone)}  耗时 {sw.Elapsed.TotalSeconds:F1}s  " +
                          $"SHA {(actual == expected ? "一致 ✓" : "不一致 ✗")}");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 5

    private static async System.Threading.Tasks.Task TestQueueLimit()
    {
        Console.WriteLine("[用例 5] 队列调度：5 个任务、并发上限 2");

        var data = MakeData(2 * 1024 * 1024);
        using var server = new RangeServer(data, chunkDelayMs: 12);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        using var m = new TaskManager(4) { DefaultFolder = dir };
        m.MaxConcurrentTasks = 2;

        var url = $"http://127.0.0.1:{server.Port}/file.bin";
        var tasks = new List<DownloadTask>();
        for (int i = 0; i < 5; i++)
            tasks.Add(await m.AddAsync(url, dir, $"q{i}.bin", 4));

        Check(tasks.Count(t => t.State == TaskState.Queued) == 3,
              $"并发上限 2 时，应只有 3 个排队，实际 {tasks.Count(t => t.State == TaskState.Queued)}");
        Console.WriteLine($"  建立后: 下载中 {tasks.Count(t => t.State == TaskState.Downloading)}  " +
                          $"排队 {tasks.Count(t => t.State == TaskState.Queued)}");

        int maxActive = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(120) && tasks.Any(t => t.State != TaskState.Completed))
        {
            maxActive = Math.Max(maxActive, tasks.Count(t => t.State == TaskState.Downloading));
            await System.Threading.Tasks.Task.Delay(40);
        }

        Check(maxActive <= 2, $"同时下载数超过上限：观察到 {maxActive}");
        Check(maxActive == 2, "应该真的并行跑满 2 个，而不是串行");
        Check(tasks.All(t => t.State == TaskState.Completed),
              "队列里的任务应全部完成，实际 " + string.Join(",", tasks.Select(t => t.State)));
        Check(m.QueuedCount == 0, $"队列应排空，剩余 {m.QueuedCount}");

        // 5 个文件内容都完整
        var expected = Sha(data);
        int bad = 0;
        foreach (var t in tasks)
            if (Sha(await File.ReadAllBytesAsync(t.SavePath)) != expected) bad++;
        Check(bad == 0, $"{bad} 个队列任务的文件内容不对");

        Console.WriteLine($"  峰值并发 {maxActive}  总耗时 {sw.Elapsed.TotalSeconds:F1}s  " +
                          $"文件全部一致 {(bad == 0 ? "✓" : "✗")}");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 6

    private static async System.Threading.Tasks.Task TestGlobalRateLimit()
    {
        Console.WriteLine("[用例 6] 全局限速：12 MB @ 4 MB/s（8 段共享一个令牌桶）");

        var data = MakeData(12 * 1024 * 1024);
        var expected = Sha(data);
        using var server = new RangeServer(data, chunkDelayMs: 0);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        using var m = new TaskManager(8) { DefaultFolder = dir };
        m.SpeedLimitBps = 4 * 1024 * 1024;

        var sw = Stopwatch.StartNew();
        var task = await m.AddAsync($"http://127.0.0.1:{server.Port}/file.bin", dir, "limited.bin", 8);

        while (sw.Elapsed < TimeSpan.FromSeconds(60) && task.State != TaskState.Completed)
            await System.Threading.Tasks.Task.Delay(50);

        var elapsed = sw.Elapsed.TotalSeconds;
        var speed = task.BytesDone / Math.Max(0.001, elapsed);

        Check(task.State == TaskState.Completed, $"应完成，实际 {task.State} {task.Error}");
        // 12MB / 4MBps = 3s。低于 2.4s 说明限速没生效；上限放宽到 8s，避免慢机器误判
        Check(elapsed >= 2.4, $"限速没生效：12MB 只用了 {elapsed:F1}s（应 ≥ 2.4s）");
        Check(elapsed <= 8, $"限速过度：12MB 用了 {elapsed:F1}s（应 ≤ 8s）");
        Check(Sha(await File.ReadAllBytesAsync(task.SavePath)) == expected, "限速后文件内容不一致");

        Console.WriteLine($"  耗时 {elapsed:F1}s  实测 {Fmt.Size((long)speed)}/s  目标 4.0 MB/s  " +
                          $"{(elapsed >= 2.4 && elapsed <= 8 ? "✓" : "✗")}");

        // 解除限速后应能全速跑（证明是「限速」不是「拖慢」）
        m.SpeedLimitBps = 0;
        Check(!m.Limiter.IsLimited, "限速应已解除");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 7

    private static async System.Threading.Tasks.Task TestLocalBridge()
    {
        Console.WriteLine("[用例 7] 浏览器接管接口（本地端口 + 令牌）");

        var data = MakeData(3 * 1024 * 1024);
        var expected = Sha(data);
        using var server = new RangeServer(data, chunkDelayMs: 0);
        server.Run();

        var dir = Path.Combine(Path.GetTempPath(), "opendm-test-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        using var m = new TaskManager(8) { DefaultFolder = dir };
        var port = FreePort();
        var token = Guid.NewGuid().ToString("n");

        using var bridge = new LocalBridge(m, port, token);
        if (!bridge.Start(out var err)) { Check(false, "接管接口启动失败: " + err); return; }

        var url = $"http://127.0.0.1:{server.Port}/file.bin";

        // 正确令牌 → 应真的把任务加进来
        bool ok = await LocalBridge.TryForwardAsync(port, token, url);
        Check(ok, "带正确令牌的 /add 应返回成功");

        // 错误令牌 → 必须拒绝（否则任意网页都能往你队列里塞文件）
        bool bad = await LocalBridge.TryForwardAsync(port, "wrong-token", url);
        Check(!bad, "错误令牌必须被拒绝");

        // 等它下完
        var sw = Stopwatch.StartNew();
        DownloadTask? task = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            task = m.Snapshot().FirstOrDefault();
            if (task is { State: TaskState.Completed }) break;
            task = null;
            await System.Threading.Tasks.Task.Delay(80);
        }

        Check(task is not null, "接管接口应真的创建并完成一个任务");
        if (task is not null)
        {
            Check(Sha(await File.ReadAllBytesAsync(task.SavePath)) == expected, "接管下载的文件内容不一致");
            Check(task.FileName.StartsWith("payload"), $"文件名应由服务器给出，实际 {task.FileName}");
        }

        Console.WriteLine($"  端口 {port}  令牌校验 {(bad ? "失效 ✗" : "生效 ✓")}  " +
                          $"落盘 {task?.FileName ?? "—"}");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 用例 8

    private static void TestProxyParsing()
    {
        Console.WriteLine("[用例 8] 代理串解析");

        Check(ProxySetting.Parse("").IsDirect == false && ProxySetting.Parse("").Proxy is null,
              "空值应回落到跟随系统");
        Check(ProxySetting.Parse(null).Display == "跟随系统", "null 应显示跟随系统");
        Check(ProxySetting.Parse("none").IsDirect, "none 应为直连");
        Check(ProxySetting.Parse("直连").IsDirect, "「直连」应被识别");

        var a = ProxySetting.Parse("127.0.0.1:7890");
        Check(!a.IsDirect && a.Proxy is not null, "省略协议时应默认 http");
        Check(a.Proxy!.GetProxy(new Uri("https://x.com"))?.ToString() == "http://127.0.0.1:7890/",
              $"省略协议解析错：{a.Proxy.GetProxy(new Uri("https://x.com"))}");

        var b = ProxySetting.Parse("socks5://u:p%40ss@10.0.0.2:1080");
        Check(b.Display == "socks5://10.0.0.2:1080", $"socks5 解析错：{b.Display}");
        var cred = (b.Proxy as System.Net.WebProxy)?.Credentials?.GetCredential(
            new Uri("socks5://10.0.0.2:1080"), "Proxy");
        Check(cred?.UserName == "u" && cred.Password == "p@ss",
              $"代理账号密码解析错：{cred?.UserName}/{cred?.Password}");

        var c = ProxySetting.Parse("ftp://x:21");
        Check(c.Error is not null, "不支持的协议应给出错误提示而不是静默生效");
        Check(c.Proxy is null, "解析失败应回落到跟随系统");

        Console.WriteLine();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ---------------------------------------------------------------- 工具

    private static byte[] MakeData(int size)
    {
        var b = new byte[size];
        var rnd = new Random(20261030);
        rnd.NextBytes(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static void Check(bool ok, string message)
    {
        if (ok) return;
        _failures++;
        Console.WriteLine($"  ✗ {message}");
    }
}
