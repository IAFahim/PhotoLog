using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;

namespace PhotoLog.Avalonia;

/// User prefs that survive restarts. JSON under LocalApplicationData/PhotoLog/settings.json
internal static class Settings
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string? PathOverride;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLog");

    public static string PathFile => PathOverride ?? Path.Combine(Dir, "settings.json");

    public sealed class Data
    {
        public DropShadow DropShadow { get; set; } = DropShadow.Light;
        public int ShadowX { get; set; } = Core.DefaultShadowX;
        public int ShadowY { get; set; } = Core.DefaultShadowY;
        public string? OutFolder { get; set; }
        public string? LastFolder { get; set; }
        public string? Address { get; set; }
        /// "Dark" or "Light". Default Dark (user preference).
        public string Theme { get; set; } = "Dark";
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
            return new Data();
        }
    }

    public static void Save(Data data)
    {
        var dir = Path.GetDirectoryName(PathFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = PathFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
        File.Move(tmp, PathFile, overwrite: true);
    }

    public static void ApplyTheme(string? theme)
    {
        if (Application.Current is null) return;
        var dark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
