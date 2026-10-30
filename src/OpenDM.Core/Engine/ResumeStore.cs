using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDM.Core.Models;

namespace OpenDM.Core.Engine;

/// <summary>
/// 断点续传状态的持久化。每个任务一个 json，存在 %APPDATA%\OpenDM\tasks\{id}.json。
/// 里面只有"下载到哪了"这种事实数据，不含任何不可重建的信息。
/// </summary>
public static class ResumeStore
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 数据根目录。默认 %APPDATA%\OpenDM；自测时用 OPEN_DM_DATA_DIR 指向临时目录，
    /// 免得测试任务混进用户真实的任务列表里。
    /// </summary>
    private static readonly string Base =
        Environment.GetEnvironmentVariable("OPEN_DM_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenDM");

    public static string Dir
    {
        get
        {
            var d = Path.Combine(Base, "tasks");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string PathFor(string id) => Path.Combine(Dir, id + ".json");

    public static void Save(DownloadTask t)
    {
        try
        {
            var dto = Dto.From(t);
            var json = JsonSerializer.Serialize(dto, Opt);
            var path = PathFor(t.Id);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 状态存不下来不该让下载挂掉，只记录
            System.Diagnostics.Debug.WriteLine($"[OpenDM] 保存状态失败: {ex.Message}");
        }
    }

    public static void Delete(string id)
    {
        try
        {
            var p = PathFor(id);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }

    /// <summary>某个保存路径是否已被别的任务占用（避免两个任务写同一个文件）。</summary>
    public static bool FileTaken(string savePath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), Opt);
                    if (dto is not null &&
                        string.Equals(dto.SavePath, savePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    public static List<DownloadTask> LoadAll()
    {
        var list = new List<DownloadTask>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), Opt);
                    if (dto is null || string.IsNullOrEmpty(dto.Url) || string.IsNullOrEmpty(dto.SavePath)) continue;
                    var t = dto.ToTask();
                    t.RecomputeBytes();
                    list.Add(t);
                }
                catch { /* 坏文件跳过 */ }
            }
        }
        catch { }
        return list.OrderByDescending(t => t.CreatedAt).ToList();
    }

    private sealed class Dto
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string SavePath { get; set; } = "";
        public string? ContentType { get; set; }
        public bool SupportsRange { get; set; }
        public int Connections { get; set; }
        public long TotalBytes { get; set; }
        public string State { get; set; } = "Paused";
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsPrepared { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public List<SegDto> Segments { get; set; } = new();

        public static Dto From(DownloadTask t) => new()
        {
            Id = t.Id,
            Url = t.Url,
            SavePath = t.SavePath,
            ContentType = t.ContentType,
            SupportsRange = t.SupportsRange,
            Connections = t.Connections,
            TotalBytes = t.TotalBytes,
            // 正在下载的，重启后一律变暂停
            State = (t.State is TaskState.Completed or TaskState.Error) ? t.State.ToString() : "Paused",
            Error = t.Error,
            CreatedAt = t.CreatedAt,
            CompletedAt = t.CompletedAt,
            IsPrepared = t.IsPrepared,
            Headers = t.Headers.Count == 0 ? null : new Dictionary<string, string>(t.Headers),
            Segments = t.Segments.Select(s => new SegDto { S = s.Start, E = s.End, P = s.Position }).ToList()
        };

        public DownloadTask ToTask()
        {
            var t = new DownloadTask
            {
                Id = string.IsNullOrEmpty(Id) ? Guid.NewGuid().ToString("n") : Id,
                Url = Url,
                SavePath = SavePath,
                ContentType = ContentType,
                SupportsRange = SupportsRange,
                Connections = Connections == 0 ? 8 : Connections,
                TotalBytes = TotalBytes,
                Error = Error,
                CreatedAt = CreatedAt == default ? DateTime.Now : CreatedAt,
                CompletedAt = CompletedAt,
                IsPrepared = IsPrepared,
                Headers = Headers is null || Headers.Count == 0
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
                Segments = Segments.Select(s => new Segment { Start = s.S, End = s.E, Position = s.P }).ToList()
            };
            t.State = Enum.TryParse<TaskState>(State, out var st) ? st : TaskState.Paused;
            return t;
        }
    }

    private sealed class SegDto
    {
        public long S { get; set; }
        public long E { get; set; }
        public long P { get; set; }
    }
}
