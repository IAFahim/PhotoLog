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

/// Opt-in local image captioning: Gemma 4 E2B (vision) via llama.cpp, CPU only.
/// Nothing is bundled — the GGUF pair is downloaded on first use into the user's data dir.
internal static class Caption
{
    // ggml-org is the llama.cpp org's own mirror: Apache-2.0, no auth gate, and its Q8_0
    // mmproj is the smallest vision projector published for this model.
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
    public static async Task<(string Text, double TokensPerSecond)> Describe(string imagePath, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var p = new ModelParams(PathOf(Files[0].File)) { ContextSize = 4096, GpuLayerCount = 0 };
            if (_weights is null)
            {
                _weights = await LLamaWeights.LoadFromFileAsync(p, ct);
                var mp = MtmdContextParams.Default();
                mp.UseGpu = false;
                mp.NThreads = Environment.ProcessorCount;
                _clip = await MtmdWeights.LoadFromFileAsync(PathOf(Files[1].File), _weights, mp, ct);
            }

            using var context = _weights.CreateContext(p); // fresh context per image: no state bleed
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
                MaxTokens = 48,
                AntiPrompts = ["<end_of_turn>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f },
            };
            // "<image>" is the executor's placeholder — it swaps in the real media marker in place,
            // so the picture lands inside the user turn instead of being appended after it.
            var turn = $"<start_of_turn>user\n<image>\n{Prompt}<end_of_turn>\n<start_of_turn>model\n";
            await foreach (var piece in exec.InferAsync(turn, infer, ct))
            {
                text.Append(piece);
                tokens++;
            }
            exec.Embeds.Clear();
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            return (Tidy(text.ToString()), seconds > 0 ? tokens / seconds : 0);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// One line, no chat-template debris, no trailing period.
    public static string Tidy(string raw)
    {
        var s = raw.Replace("<end_of_turn>", " ").Replace("<start_of_turn>", " ").Trim();
        var nl = s.IndexOfAny(['\n', '\r']);
        if (nl >= 0) s = s[..nl];
        s = s.Trim().Trim('"', '*', ' ').TrimEnd('.', ' ');
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 14 ? s : string.Join(' ', words[..14]);
    }

    public static void Unload()
    {
        _clip?.Dispose();
        _weights?.Dispose();
        _clip = null;
        _weights = null;
    }
}
