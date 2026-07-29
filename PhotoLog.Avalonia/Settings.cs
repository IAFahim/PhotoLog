using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoLog.Avalonia;

/// User prefs that survive restarts. JSON under LocalApplicationData/PhotoLog/settings.json
/// (same tree as the AI model). Pure file I/O — no UI types.
internal static class Settings
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// Selfcheck can point this at a temp file so it never touches real user prefs.
    internal static string? PathOverride;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog");

    public static string PathFile => PathOverride ?? Path.Combine(Dir, "settings.json");

    public sealed class Data
    {
        /// Stamp drop-shadow weight: Off / Light (single twin) / Heavy (multi-pass).
        public DropShadow DropShadow { get; set; } = DropShadow.Light;
        /// Drop direction in preview-scale pixels (+X right, −Y up).
        public int ShadowX { get; set; } = Core.DefaultShadowX;
        public int ShadowY { get; set; } = Core.DefaultShadowY;
        /// Last output folder (expanded path).
        public string? OutFolder { get; set; }
        /// Last loaded photo folder (restored into the path box; not auto-scanned).
        public string? LastFolder { get; set; }
        /// Optional multi-line address stamp text.
        public string? Address { get; set; }
    }

    public static Data Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new Data();
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(PathFile), JsonOpts) ?? new Data();
        }
        catch
        {
            return new Data(); // corrupt/partial → defaults; next Save rewrites cleanly
        }
    }

    public static void Save(Data data)
    {
        var dir = Path.GetDirectoryName(PathFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // atomic-ish: write temp then replace so a crash mid-write doesn't trash prefs
        var tmp = PathFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
        File.Move(tmp, PathFile, overwrite: true);
    }
}
