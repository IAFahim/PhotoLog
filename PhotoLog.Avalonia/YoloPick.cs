using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

namespace PhotoLog.Avalonia;

/// Fast AI select via Ultralytics YOLO26 nano (fixed COCO-80 classes, ~10 MB ONNX).
/// Detect once per photo → cache labels (RAM + disk) → every later category filter is free.
/// Gemma stays for captions only.
internal static class YoloPick
{
    const string FileName = "yolo26n.onnx";
    const long ExpectedBytes = 9_000_000;
    const string Url = "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.onnx";
    const double Confidence = 0.25;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog", "models");

    public static string ModelPath => Path.Combine(Dir, FileName);
    static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog", "yolo-cache.json");

    public static bool Ready
    {
        get
        {
            var i = new FileInfo(ModelPath);
            return i.Exists && i.Length >= ExpectedBytes;
        }
    }

    public static long TotalBytes => 9_941_957;

    /// Dropdown groups — every COCO class appears in at least one (no fake "screenshot" proxies).
    public static readonly (string Name, string[] Labels)[] Groups =
    [
        ("People", ["person"]),
        ("Animals", ["bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"]),
        ("Vehicles", ["bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat"]),
        ("Street / outdoor", ["traffic light", "fire hydrant", "stop sign", "parking meter", "bench"]),
        ("Sports", ["frisbee", "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket"]),
        ("Accessories", ["backpack", "umbrella", "handbag", "tie", "suitcase"]),
        ("Food", ["banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake"]),
        ("Tableware", ["bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl"]),
        ("Furniture", ["chair", "couch", "potted plant", "bed", "dining table", "toilet"]),
        ("Electronics", ["tv", "laptop", "mouse", "remote", "keyboard", "cell phone"]),
        ("Appliances", ["microwave", "oven", "toaster", "sink", "refrigerator"]),
        ("Indoor bits", ["book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"]),
    ];

    static Yolo? _yolo;
    static readonly SemaphoreSlim Gate = new(1, 1);
    // key = absolute path; value invalidated when mtime/size change
    static readonly ConcurrentDictionary<string, CacheEntry> Mem = new(StringComparer.Ordinal);
    static int _cacheDirty;
    static int _cacheLoaded;

    public static async Task Download(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (Ready) { progress?.Report(1); return; }

        foreach (var seed in new[] { Path.Combine("/tmp", FileName), Path.Combine(Dir, FileName + ".seed") })
        {
            if (new FileInfo(seed) is { Exists: true } s && s.Length >= ExpectedBytes)
            {
                File.Copy(seed, ModelPath, true);
                progress?.Report(1);
                return;
            }
        }

        var tmp = ModelPath + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var src = await http.GetStreamAsync(Url, ct).ConfigureAwait(false);
        await using var dst = File.Create(tmp);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        var total = TotalBytes;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            progress?.Report(Math.Min(1, (double)done / total));
        }
        await dst.DisposeAsync().ConfigureAwait(false);
        File.Move(tmp, ModelPath, true);
        progress?.Report(1);
    }

    /// Match against cached labels when possible. FromCache=true means no ONNX this call.
    public static Task<(bool Match, bool FromCache)> Matches(string imagePath, string category, CancellationToken ct) =>
        Task.Run(() => MatchesCore(imagePath, category), ct);

    /// Force-detect (or read cache) and return sorted unique COCO labels for one image.
    public static Task<(IReadOnlyList<string> Labels, bool FromCache)> Detect(string imagePath, CancellationToken ct) =>
        Task.Run(() => DetectCore(imagePath), ct);

    /// Flush disk cache after a batch so next session is also free.
    public static void SaveCache()
    {
        if (Interlocked.Exchange(ref _cacheDirty, 0) == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(dir);
            var map = Mem.ToDictionary(
                kv => kv.Key,
                kv => new DiskRow(kv.Value.Mtime, kv.Value.Size, kv.Value.Labels),
                StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(CachePath, json);
        }
        catch { /* cache is best-effort */ Interlocked.Exchange(ref _cacheDirty, 1); }
    }

    static (bool Match, bool FromCache) MatchesCore(string imagePath, string category)
    {
        var want = LabelsFor(category);
        if (want.Count == 0) return (false, true);

        var (labels, fromCache) = DetectCore(imagePath);
        foreach (var lab in labels)
            if (want.Contains(lab)) return (true, fromCache);
        return (false, fromCache);
    }

    static (IReadOnlyList<string> Labels, bool FromCache) DetectCore(string imagePath)
    {
        EnsureDiskCacheLoaded();
        var info = new FileInfo(imagePath);
        if (!info.Exists) return (Array.Empty<string>(), true);

        var path = info.FullName;
        var mtime = info.LastWriteTimeUtc.Ticks;
        var size = info.Length;

        if (Mem.TryGetValue(path, out var hit) && hit.Mtime == mtime && hit.Size == size)
            return (hit.Labels, true);

        Gate.Wait();
        try
        {
            // re-check under lock (another thread may have filled it)
            if (Mem.TryGetValue(path, out hit) && hit.Mtime == mtime && hit.Size == size)
                return (hit.Labels, true);

            EnsureLoaded();
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null)
            {
                var empty = new CacheEntry(mtime, size, Array.Empty<string>());
                Mem[path] = empty;
                Interlocked.Exchange(ref _cacheDirty, 1);
                return (empty.Labels, false);
            }

            var results = _yolo!.RunObjectDetection(bmp, confidence: Confidence, iou: 0.7);
            // unique labels, keep first-seen order
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in results)
            {
                var n = d.Label.Name;
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                names.Add(n);
            }
            var entry = new CacheEntry(mtime, size, names.ToArray());
            Mem[path] = entry;
            Interlocked.Exchange(ref _cacheDirty, 1);
            return (entry.Labels, false);
        }
        finally { Gate.Release(); }
    }

    static void EnsureDiskCacheLoaded()
    {
        if (Interlocked.Exchange(ref _cacheLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            var map = JsonSerializer.Deserialize<Dictionary<string, DiskRow>>(json);
            if (map is null) return;
            foreach (var (path, row) in map)
            {
                if (row.Labels is null) continue;
                Mem[path] = new CacheEntry(row.Mtime, row.Size, row.Labels);
            }
        }
        catch { /* ignore corrupt cache */ }
    }

    static void EnsureLoaded()
    {
        if (_yolo is not null) return;
        if (!Ready) throw new InvalidOperationException("YOLO model not downloaded.");
        _yolo = new Yolo(new YoloOptions
        {
            ExecutionProvider = new CpuExecutionProvider(ModelPath),
        });
    }

    /// Preset group name or free text → COCO label set to match against.
    public static HashSet<string> LabelsFor(string category)
    {
        var cat = (category ?? "").Trim();
        if (cat.Length == 0) return [];

        foreach (var (name, labels) in Groups)
            if (string.Equals(name, cat, StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);

        // legacy aliases from the first YOLO UI
        if (string.Equals(cat, "outdoors", StringComparison.OrdinalIgnoreCase))
            return LabelsFor("Street / outdoor").Concat(LabelsFor("Vehicles")).Concat(LabelsFor("Sports")).Concat(LabelsFor("Animals")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(cat, "indoor", StringComparison.OrdinalIgnoreCase))
            return LabelsFor("Furniture").Concat(LabelsFor("Electronics")).Concat(LabelsFor("Appliances")).Concat(LabelsFor("Indoor bits")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var q = cat.ToLowerInvariant();
        // custom: any COCO class whose name contains the query (or vice versa)
        var hits = CocoAll.Where(n => n.Contains(q, StringComparison.Ordinal) || q.Contains(n, StringComparison.Ordinal)).ToArray();
        return hits.Length > 0
            ? hits.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { q };
    }

    static readonly string[] CocoAll =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog",
        "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella",
        "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball", "kite",
        "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle",
        "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich",
        "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote",
        "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator", "book",
        "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
    ];

    readonly record struct CacheEntry(long Mtime, long Size, string[] Labels);
    sealed record DiskRow(long Mtime, long Size, string[] Labels);
}
