using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace PhotoLog.Avalonia;

/// Opt-in local vision captions: Gemma 4 E2B via llama.cpp (CPU).
/// E2B is the smallest Gemma 4 that still does image understanding; Q4_0 + Q8 mmproj
/// is the leanest practical pack (~3.4 GB). AI multi-select uses YoloPick (YOLO26n) instead —
/// Gemma yes/no is too slow for scanning a whole library.
/// Nothing is bundled — GGUFs download on first use into the user's data dir.
internal static class Caption
{
    // ggml-org mirror: Apache-2.0, no auth gate; Q8_0 mmproj is the smallest projector for E2B.
    const string Repo = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/";
    public static readonly (string File, long Bytes)[] Files =
    [
        ("gemma-4-E2B-it-Q4_0.gguf", 2_841_481_184),
        ("mmproj-gemma-4-E2B-it-Q8_0.gguf", 557_368_064),
    ];

    const string Prompt = "Describe this photo in one short caption of at most 12 words. "
                        + "Only output the caption, no punctuation at the end.";

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog", "models");

    public static string PathOf(string file) => Path.Combine(Dir, file);
    public static long TotalBytes { get { long t = 0; foreach (var f in Files) t += f.Bytes; return t; } }

    /// True once both files are present at (roughly) their full size.
    public static bool Ready
    {
        get
        {
            foreach (var (file, bytes) in Files)
            {
                var i = new FileInfo(PathOf(file));
                if (!i.Exists || i.Length < bytes * 0.99) return false;
            }
            return true;
        }
    }

    public static async Task Download(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        long done = 0, lastReport = 0;
        foreach (var (file, bytes) in Files)
        {
            var dest = PathOf(file);
            if (new FileInfo(dest) is { Exists: true } f && f.Length >= bytes * 0.99) { done += bytes; continue; }
            var tmp = dest + ".part";
            using (var src = await http.GetStreamAsync(Repo + file, ct))
            using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done - lastReport < 8 << 20) continue; // ~430 UI updates, not 3400
                    lastReport = done;
                    progress.Report(Math.Min(1, (double)done / TotalBytes));
                }
            }
            File.Move(tmp, dest, true);
        }
        progress.Report(1);
    }

    static LLamaWeights? _weights;
    static MtmdWeights? _clip;
    static readonly SemaphoreSlim Gate = new(1, 1); // ponytail: one caption at a time, a 3 GB model is not worth loading twice

    /// Caption one image. Throws if the model isn't downloaded — callers check Ready first.
    /// Always runs on the thread pool: llama.cpp inference is CPU-heavy and freezes Avalonia
    /// if it captures the UI sync context (even behind async/await).
    public static Task<(string Text, double TokensPerSecond)> Describe(string imagePath, CancellationToken ct) =>
        Task.Run(() => DescribeCore(imagePath, ct), ct);

    /// Yes/no match for AI select (e.g. "furniture", "outdoors"). Same model, short answer.
    public static Task<bool> Matches(string imagePath, string category, CancellationToken ct) =>
        Task.Run(() => MatchesCore(imagePath, category, ct), ct);

    static async Task<(string Text, double TokensPerSecond)> DescribeCore(string imagePath, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoaded(ct).ConfigureAwait(false);
            var raw = await Infer(imagePath, Prompt, maxTokens: 48, ct).ConfigureAwait(false);
            var seconds = raw.Seconds;
            return (Tidy(raw.Text), seconds > 0 ? raw.Tokens / seconds : 0);
        }
        finally { Gate.Release(); }
    }

    static async Task<bool> MatchesCore(string imagePath, string category, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoaded(ct).ConfigureAwait(false);
            var cat = (category ?? "").Trim();
            if (cat.Length == 0) return false;
            // Force a one-token-ish answer so we can parse yes/no reliably.
            var ask = $"Does this photo mainly show {cat}? Answer with only yes or no.";
            var raw = await Infer(imagePath, ask, maxTokens: 8, ct).ConfigureAwait(false);
            var s = Tidy(raw.Text).ToLowerInvariant();
            if (s.StartsWith("yes") || s.Contains(" yes")) return true;
            if (s.StartsWith("no") || s.Contains(" no")) return false;
            // ambiguous → no select (safer than false positives)
            return s.Contains("yes", StringComparison.Ordinal);
        }
        finally { Gate.Release(); }
    }

    static async Task EnsureLoaded(CancellationToken ct)
    {
        if (_weights is not null && _clip is not null) return;
        var p = new ModelParams(PathOf(Files[0].File)) { ContextSize = 4096, GpuLayerCount = 0 };
        _weights = await LLamaWeights.LoadFromFileAsync(p, ct).ConfigureAwait(false);
        var mp = MtmdContextParams.Default();
        mp.UseGpu = false;
        mp.NThreads = Environment.ProcessorCount;
        _clip = await MtmdWeights.LoadFromFileAsync(PathOf(Files[1].File), _weights, mp, ct).ConfigureAwait(false);
    }

    static async Task<(string Text, int Tokens, double Seconds)> Infer(
        string imagePath, string userPrompt, int maxTokens, CancellationToken ct)
    {
        var p = new ModelParams(PathOf(Files[0].File)) { ContextSize = 4096, GpuLayerCount = 0 };
        using var context = _weights!.CreateContext(p); // fresh context per image: no state bleed
        var exec = new InteractiveExecutor(context, _clip!);
        // LoadMedia (not SafeMtmdEmbed.FromMediaFile) is what registers the bitmap with the mtmd
        // context; the executor disposes the embed for us once the prompt is tokenized.
        exec.Embeds.Add(_clip!.LoadMedia(imagePath)
                        ?? throw new InvalidOperationException("the vision projector could not read " + imagePath));

        var text = new StringBuilder();
        var started = DateTime.UtcNow;
        var tokens = 0;
        var infer = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = ["<end_of_turn>"],
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f },
        };
        // "<image>" is the executor's placeholder — it swaps in the real media marker in place.
        var turn = $"<start_of_turn>user\n<image>\n{userPrompt}<end_of_turn>\n<start_of_turn>model\n";
        await foreach (var piece in exec.InferAsync(turn, infer, ct).ConfigureAwait(false))
        {
            text.Append(piece);
            tokens++;
        }
        exec.Embeds.Clear();
        return (text.ToString(), tokens, (DateTime.UtcNow - started).TotalSeconds);
    }

    /// One line, no chat-template debris, no trailing period.
    /// First real content line wins (role-only lines like "model" are skipped).
    public static string Tidy(string raw)
    {
        var s = (raw ?? "")
            .Replace("<end_of_turn>", "\n", StringComparison.Ordinal)
            .Replace("<start_of_turn>", "\n", StringComparison.Ordinal)
            .Replace("<image>", " ", StringComparison.Ordinal);

        foreach (var line in s.ReplaceLineEndings("\n").Split('\n'))
        {
            var t = line.Trim().Trim('"', '*', ' ').TrimEnd('.', ' ', '\t');
            if (t.Length == 0) continue;
            // gemma chat roles that sometimes leak out as their own line
            if (t is "model" or "user" or "system" or "assistant") continue;
            // strip a leading "model " / "user " if the model echoed the role
            if (t.StartsWith("model ", StringComparison.OrdinalIgnoreCase)) t = t[6..].TrimEnd('.', ' ');
            if (t.StartsWith("user ", StringComparison.OrdinalIgnoreCase)) t = t[5..].TrimEnd('.', ' ');
            t = t.TrimEnd('.', ' ');
            if (t.Length == 0) continue;
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length <= 14 ? t : string.Join(' ', words[..14]);
        }
        return "";
    }

    public static void Unload()
    {
        _clip?.Dispose();
        _weights?.Dispose();
        _clip = null;
        _weights = null;
    }
}
