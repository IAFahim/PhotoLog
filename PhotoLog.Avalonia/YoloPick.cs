using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

namespace PhotoLog.Avalonia;

/// Fast AI select via Ultralytics YOLO26 nano (COCO 80 classes, ~10 MB ONNX).
/// Gemma stays for captions only — yes/no vision LLM was too slow for multi-select scans.
internal static class YoloPick
{
    // Latest nano detection head from Ultralytics assets (YOLOv26, opset 18).
    const string FileName = "yolo26n.onnx";
    const long ExpectedBytes = 9_000_000; // ~9.5 MB; size gate only
    const string Url = "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.onnx";

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog", "models");

    public static string ModelPath => Path.Combine(Dir, FileName);

    public static bool Ready
    {
        get
        {
            var i = new FileInfo(ModelPath);
            return i.Exists && i.Length >= ExpectedBytes;
        }
    }

    public static long TotalBytes => 9_941_957; // published yolo26n.onnx size

    static Yolo? _yolo;
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// Download YOLO nano ONNX on first use (~10 MB). Progress is 0..1.
    public static async Task Download(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (Ready) { progress?.Report(1); return; }

        // Prefer a pre-fetched copy (dev / offline mirror) if present.
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

    /// True if any COCO detection for this category scores above confidence.
    public static Task<bool> Matches(string imagePath, string category, CancellationToken ct) =>
        Task.Run(() => MatchesCore(imagePath, category), ct);

    static bool MatchesCore(string imagePath, string category)
    {
        var labels = LabelsFor(category);
        if (labels.Count == 0) return false;

        Gate.Wait();
        try
        {
            EnsureLoaded();
            using var bmp = SKBitmap.Decode(imagePath);
            if (bmp is null) return false;

            // 0.25 is Ultralytics default; IoU ignored on YOLO26 (NMS internal).
            var hits = _yolo!.RunObjectDetection(bmp, confidence: 0.25, iou: 0.7);
            foreach (var d in hits)
            {
                var name = d.Label.Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (labels.Contains(name)) return true;
            }
            return false;
        }
        finally { Gate.Release(); }
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

    /// Preset categories → COCO label names. Custom text matches any label that contains it.
    static HashSet<string> LabelsFor(string category)
    {
        var cat = (category ?? "").Trim().ToLowerInvariant();
        if (cat.Length == 0) return [];

        if (Presets.TryGetValue(cat, out var set))
            return set;

        // Custom: substring against full COCO vocabulary (e.g. "dog", "chair", "cell").
        var hits = CocoAll.Where(n => n.Contains(cat, StringComparison.Ordinal) || cat.Contains(n, StringComparison.Ordinal)).ToArray();
        return hits.Length > 0 ? hits.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cat };
    }

    static readonly HashSet<string> CocoAll = new(StringComparer.OrdinalIgnoreCase)
    {
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
    };

    static readonly Dictionary<string, HashSet<string>> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["furniture"] = Set("chair", "couch", "bed", "dining table", "toilet", "potted plant", "vase", "clock"),
        ["outdoors"] = Set(
            "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
            "traffic light", "fire hydrant", "stop sign", "parking meter", "bench",
            "bird", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
            "backpack", "umbrella", "frisbee", "skis", "snowboard", "sports ball", "kite",
            "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
            "potted plant"),
        ["people"] = Set("person"),
        ["food"] = Set(
            "banana", "apple", "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza",
            "donut", "cake", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl"),
        ["vehicles"] = Set("bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat"),
        ["animals"] = Set("bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"),
        // Weak proxies — COCO has no "screenshot" / "document" class.
        ["screenshots"] = Set("tv", "laptop", "cell phone", "keyboard", "mouse", "remote"),
        ["indoor"] = Set(
            "chair", "couch", "bed", "dining table", "toilet", "tv", "laptop", "microwave",
            "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors",
            "teddy bear", "hair drier", "toothbrush", "bottle", "wine glass", "cup", "bowl",
            "potted plant", "remote", "keyboard", "mouse", "cell phone"),
        ["text / documents"] = Set("book"),
    };

    static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
