using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoLog.Avalonia;

/// Global stamp drop-shadow weight. Light = subtle 1px twin; Heavy = thicker multi-pass; Off = none.
public enum DropShadow { Off, Light, Heavy }

/// Port of main.py's pipeline: scan -> stamp -> export. No UI types here, so --selfcheck runs it headless.
internal static class Core
{
    static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff"];
    const string DateFmt = "MMM d, yyyy 'at' h:mm:ss tt"; // Jul 28, 2026 at 8:23:59 AM

    // ponytail: first system sans that exists; no font file shipped
    static readonly FontFamily Family = new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans", "Arial", "Helvetica" }
        .Select(n => SystemFonts.TryGet(n, out var f) ? f : (FontFamily?)null)
        .FirstOrDefault(f => f is not null) ?? SystemFonts.Families.First();

    public static string FmtDate(DateTime dt) => dt.ToString(DateFmt, CultureInfo.InvariantCulture);

    public static string Expand(string? p) =>
        string.IsNullOrWhiteSpace(p) ? "" :
        p.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                         p.TrimStart('~').TrimStart('/')) : p.Trim();

    /// One scan hit: display name (deduped), full path, and the photo's own taken time (EXIF → mtime).
    public readonly record struct Scanned(string Name, string Path, DateTime Taken);

    /// Recursive. Sorted newest-first by EXIF/mtime (Google Photos order). Duplicate filenames get _1/_2.
    public static List<Scanned> Scan(string? folder)
    {
        var root = Expand(folder);
        if (root.Length == 0 || !Directory.Exists(root)) return [];
        var used = new HashSet<string>();
        var list = new List<Scanned>();
        foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => Exts.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            var name = Path.GetFileName(p);
            for (var i = 1; !used.Add(name); i++)
                name = $"{Path.GetFileNameWithoutExtension(p)}_{i}{Path.GetExtension(p)}";
            list.Add(new Scanned(name, p, PhotoTime(p)));
        }
        // newest first; filename tie-break keeps the order stable
        list.Sort((a, b) =>
        {
            var c = b.Taken.CompareTo(a.Taken);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return list;
    }

    static string? Tag(ExifProfile e, ExifTag<string> t) =>
        e.TryGetValue(t, out var v) && !string.IsNullOrWhiteSpace(v.Value) ? v.Value : null;

    static DateTime FromExif(ExifProfile? exif, string path)
    {
        var raw = exif is not null ? Tag(exif, ExifTag.DateTimeOriginal) ?? Tag(exif, ExifTag.DateTime) : null;
        return raw is not null && DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                                                         DateTimeStyles.None, out var d)
            ? d : File.GetLastWriteTime(path);
    }

    /// The photo's own moment: EXIF DateTimeOriginal → EXIF DateTime → file mtime. No full pixel decode.
    public static DateTime PhotoTime(string path)
    {
        try
        {
            var info = Image.Identify(path);
            return FromExif(info.Metadata.ExifProfile, path);
        }
        catch
        {
            return File.GetLastWriteTime(path);
        }
    }

    /// Same as <see cref="PhotoTime(string)"/> when the image is already loaded (export/thumb path).
    public static DateTime PhotoTime(Image img, string path) => FromExif(img.Metadata.ExifProfile, path);

    /// Combine optional date/time overrides with the photo's own moment. Each part is independent:
    /// date-only keeps the photo's clock; time-only keeps the photo's day; both replace fully.
    public static DateTime EffectiveTime(Image img, string path, DateOnly? overrideDate, TimeOnly? overrideTime)
    {
        var t = PhotoTime(img, path);
        var d = overrideDate ?? DateOnly.FromDateTime(t);
        var clock = overrideTime ?? TimeOnly.FromDateTime(t);
        return d.ToDateTime(clock);
    }

    public static string DateLine(Image img, string path, DateOnly? overrideDate, TimeOnly? overrideTime = null) =>
        FmtDate(EffectiveTime(img, path, overrideDate, overrideTime));

    /// Google Photos-style day header: Today / Yesterday / weekday (this week) / "Sat, Jul 25" / with year if older.
    public static string DayHeader(DateOnly day, DateOnly? today = null)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Now);
        if (day == now) return "Today";
        if (day == now.AddDays(-1)) return "Yesterday";
        var age = now.DayNumber - day.DayNumber;
        if (age is >= 0 and < 7) return day.ToString("dddd", CultureInfo.InvariantCulture); // Monday
        if (day.Year == now.Year) return day.ToString("ddd, MMM d", CultureInfo.InvariantCulture); // Sat, Jul 25
        return day.ToString("ddd, MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// Override fields are scoped to checked images; unchecked photos keep their own EXIF date/time,
    /// no address and no caption. Every render/export path goes through here.
    public static (DateOnly? Date, TimeOnly? Time, string Addr, string Caption) Fields(
        bool selected, DateOnly? date, TimeOnly? time, string? addr, string? caption = null) =>
        selected ? (date, time, addr ?? "", caption ?? "") : (null, null, "", "");

    static string[] Lines(string? block) =>
        (block ?? "").Trim() is { Length: > 0 } s ? s.ReplaceLineEndings("\n").Split('\n') : [];

    /// Stamp is date + address only. Caption is never drawn on the image (rename / UI only).
    public static string[] StampLines(Image img, string path, DateOnly? overrideDate, string? addr,
                                      string? caption = null, TimeOnly? overrideTime = null) =>
        [DateLine(img, path, overrideDate, overrideTime), .. Lines(addr)];

    /// Write DateTimeOriginal / DateTime / DateTimeDigitized so embedded metadata matches the stamp.
    public static void WriteExif(Image img, DateTime when)
    {
        var s = when.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        var exif = img.Metadata.ExifProfile ??= new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, s);
        exif.SetValue(ExifTag.DateTime, s);
        exif.SetValue(ExifTag.DateTimeDigitized, s);
    }

    /// Align OS file timestamps with the stamp moment so File Created / Modified (and Access)
    /// match the photo — not "now when we exported".
    /// <list type="bullet">
    /// <item>Windows — Created + Modified + Access via SetFileTime (kernel), reliable.</item>
    /// <item>macOS — birth + mtime via .NET (setattrlist under the hood).</item>
    /// <item>Linux — Modified (mtime) always. Real FS *birth* is set at inode create and is not
    ///   writable by userspace, so Nautilus “File Created” often stays export-now; “File Modified”
    ///   and EXIF “Originally Created” carry the photo moment. .NET’s GetCreationTime may still
    ///   report the set value even when birth doesn’t change.</item>
    /// </list>
    public static void TouchTimes(string path, DateTime when)
    {
        // EXIF times are wall-clock without zone; treat as local so File.* matches what the UI shows.
        var t = when.Kind switch
        {
            DateTimeKind.Utc => when.ToLocalTime(),
            DateTimeKind.Local => when,
            _ => DateTime.SpecifyKind(when, DateTimeKind.Local),
        };
        // OS floors: Windows FILETIME starts 1601-01-01; keep a safe lower bound everywhere.
        if (t.Year < 1980) t = new DateTime(1980, 1, 1, t.Hour, t.Minute, t.Second, DateTimeKind.Local);

        if (OperatingSystem.IsWindows())
        {
            // Kernel SetFileTime is the only API file explorers consistently honor for Created.
            if (TryWindowsSetFileTime(path, t)) return;
        }

        // Portable path (macOS birth + mtime, Linux mtime, Windows fallback).
        try { File.SetLastWriteTime(path, t); } catch { /* mtime is the one every FS can do */ }
        try { File.SetLastAccessTime(path, t); } catch { /* optional */ }
        try { File.SetCreationTime(path, t); } catch { /* birth may be unsupported */ }

        // Second pass with Utc APIs — some hosts only honor one flavor.
        try
        {
            var u = t.ToUniversalTime();
            File.SetLastWriteTimeUtc(path, u);
            File.SetLastAccessTimeUtc(path, u);
            File.SetCreationTimeUtc(path, u);
        }
        catch { /* best-effort */ }
    }

    /// Windows FILETIME: 100ns ticks since 1601-01-01 UTC.
    static bool TryWindowsSetFileTime(string path, DateTime localWhen)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var utc = localWhen.ToUniversalTime();
            long fileTime = utc.ToFileTimeUtc();
            // Open with write-attributes so we don't need write-data access after save.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            long c = fileTime, a = fileTime, w = fileTime;
            return SetFileTime(fs.SafeFileHandle, ref c, ref a, ref w);
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileTime(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        ref long lpCreationTime,
        ref long lpLastAccessTime,
        ref long lpLastWriteTime);

    // default look: light drop shadow — dark twin ~1px top-right behind white text.
    // Unlike date/address these are global — they apply to every image, selected or not.
    public const DropShadow DefaultDrop = DropShadow.Light;
    public const int DefaultShadowX = 1, DefaultShadowY = -1;

    /// Suggested offset when the user picks a weight preset (they can then fine-tune X/Y freely).
    public static (int X, int Y) DropOffset(DropShadow drop) => drop switch
    {
        DropShadow.Off => (0, 0),
        DropShadow.Heavy => (3, -2),
        _ => (1, -1), // Light
    };

    /// <paramref name="shadowX"/>/<paramref name="shadowY"/> are the drop direction (preview-scale px).
    /// <paramref name="drop"/> is how thick: Off = none, Light = one twin, Heavy = multi-pass along that vector.
    public static void Stamp(Image img, string[] lines, DropShadow drop = DefaultDrop,
                             int shadowX = DefaultShadowX, int shadowY = DefaultShadowY)
    {
        var size = Math.Max(14, img.Width / 30);
        var font = Family.CreateFont(size);
        var text = string.Join('\n', lines);
        float x = img.Width - size / 2, y = size / 2;
        RichTextOptions At(float dx, float dy) => new(font)
        {
            Origin = new PointF(x + dx, y + dy),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.End,
            // ponytail: ImageSharp spaces lines as a multiple of font line height, Pillow adds size/3 px.
            // 1.33 is the closest single constant; exact only if you measure font metrics per size.
            LineSpacing = 4f / 3f,
        };
        // offsets are in preview-scale pixels; scaling with font size keeps exports matching the preview
        float S(int v) => (float)(v * size / 30.0);
        img.Mutate(c =>
        {
            if (drop != DropShadow.Off && (shadowX != 0 || shadowY != 0))
            {
                if (drop == DropShadow.Heavy)
                {
                    // multi-pass along the user vector: outer full offset → mid → near
                    c.DrawText(At(S(shadowX), S(shadowY)), text, Color.FromRgb(0, 0, 0));
                    c.DrawText(At(S(Mul(shadowX, 2, 3)), S(Mul(shadowY, 2, 3))), text, Color.FromRgb(25, 25, 25));
                    c.DrawText(At(S(Mul(shadowX, 1, 3)), S(Mul(shadowY, 1, 3))), text, Color.FromRgb(45, 45, 45));
                }
                else // Light
                    c.DrawText(At(S(shadowX), S(shadowY)), text, Color.FromRgb(40, 40, 40));
            }
            c.DrawText(At(0, 0), text, Color.White);
        });
    }

    static int Mul(int v, int num, int den) => (int)Math.Round(v * (double)num / den);

    /// Stamped preview, decoded small (JPEG scaled DCT) so a folder scan stays fast.
    /// Preserves aspect (ResizeMode.Max) — UI sizes the cell to match, no square crop.
    public static (byte[] Jpeg, string Date) Thumb(string path, DateOnly? overrideDate, string? addr,
                                                   int maxSide = 768,
                                                   DropShadow drop = DefaultDrop,
                                                   string? caption = null, TimeOnly? overrideTime = null,
                                                   int shadowX = DefaultShadowX, int shadowY = DefaultShadowY)
    {
        using var img = Image.Load(new DecoderOptions { TargetSize = new Size(maxSide, maxSide) }, path);
        img.Mutate(c => c.AutoOrient());
        if (img.Width > maxSide || img.Height > maxSide)
            img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(maxSide, maxSide), Mode = ResizeMode.Max }));
        var lines = StampLines(img, path, overrideDate, addr, caption, overrideTime);
        Stamp(img, lines, drop, shadowX, shadowY);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 88 });
        return (ms.ToArray(), lines[0]);
    }

    /// Full-resolution stamped copy into outDir. Originals are never touched.
    /// After write, EXIF + OS file times (Created / Modified / Access) all match the stamped moment
    /// (photo EXIF/mtime, or the date/time override when set) — on Windows, macOS, and Linux.
    /// <paramref name="name"/> is the final file name inside outDir (use <see cref="ExportFileName"/>).
    public static string Export(string src, string name, string outDir, DateOnly? overrideDate, string? addr,
                                DropShadow drop = DefaultDrop,
                                string? caption = null, TimeOnly? overrideTime = null,
                                int shadowX = DefaultShadowX, int shadowY = DefaultShadowY)
    {
        Directory.CreateDirectory(outDir);
        using var img = Image.Load(src);
        img.Mutate(c => c.AutoOrient());
        // caption is for ExportFileName only — never stamped onto pixels
        var when = EffectiveTime(img, src, overrideDate, overrideTime);
        Stamp(img, StampLines(img, src, overrideDate, addr, overrideTime: overrideTime), drop, shadowX, shadowY);
        WriteExif(img, when); // always — stamp, EXIF, and FS times share one clock
        var dest = Path.Combine(outDir, name);
        img.Save(dest);
        TouchTimes(dest, when); // after Save so the write itself doesn't stamp "now" over us
        return dest;
    }

    // ---- export naming: caption → filesystem-safe base name (pure, no model) ----

    /// Illegal on Windows + Unix path separators + control chars. Kept as one set so names are portable.
    static readonly char[] BadNameChars =
        Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

    /// Collapse model-ish caption text into a short filesystem-safe base name (no extension).
    /// Empty/whitespace after tidy → empty string (caller picks a fallback).
    public static string Slug(string? caption, int maxLen = 60)
    {
        var s = Caption.Tidy(caption ?? "");
        if (s.Length == 0) return "";

        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < 32 || BadNameChars.Contains(c)) chars[i] = ' ';
        }
        s = new string(chars);
        // collapse runs of whitespace to a single space; trim ends / stray dots (Windows forbids trailing '.')
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (s.Length == 0) return "";
        if (s.Length > maxLen)
        {
            s = s[..maxLen].TrimEnd(' ', '.');
            // don't cut mid-word when a space is nearby
            var sp = s.LastIndexOf(' ');
            if (sp >= maxLen / 2) s = s[..sp];
        }
        return s.Trim(' ', '.');
    }

    /// Build a unique export file name. Caption present → slug + original extension.
    /// Empty caption → original base name. Collisions get _1, _2, … (stable, order-dependent).
    /// <paramref name="used"/> is updated with the chosen name (OrdinalIgnoreCase).
    public static string ExportFileName(string originalName, string? caption, ISet<string> used)
    {
        var ext = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";

        var stem = Slug(caption);
        if (stem.Length == 0)
        {
            stem = Path.GetFileNameWithoutExtension(originalName);
            // original could still be empty or "." — never ship a blank filename
            if (string.IsNullOrWhiteSpace(stem) || stem is "." or "..")
                stem = "photo";
            // still sanitize the original stem (nested paths never, but defensive)
            foreach (var c in BadNameChars) stem = stem.Replace(c, '_');
            stem = stem.Trim(' ', '.');
            if (stem.Length == 0) stem = "photo";
        }

        var name = stem + ext;
        for (var i = 1; !used.Add(name); i++)
            name = $"{stem}_{i}{ext}";
        return name;
    }

    // ---- headless self-check (dotnet run -- --selfcheck) ----

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) throw new InvalidOperationException(what);
    }

    /// File times lose sub-second precision on many FS; allow 2s slack.
    static bool Near(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) < 2;

    static void MakeJpeg(string path, int w, int h, string? exifDate)
    {
        using var img = new Image<Rgb24>(w, h, new Rgb24(128, 128, 128));
        if (exifDate is not null)
        {
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, exifDate);
        }
        img.SaveAsJpeg(path);
    }

    static int MaxDiff(string a, string b, float x0, float y0, float x1, float y1)
    {
        using var ia = Image.Load<Rgb24>(a);
        using var ib = Image.Load<Rgb24>(b);
        int max = 0;
        for (int y = (int)(y0 * ia.Height); y < (int)(y1 * ia.Height); y++)
        for (int x = (int)(x0 * ia.Width); x < (int)(x1 * ia.Width); x++)
        {
            Rgb24 p = ia[x, y], q = ib[x, y];
            max = Math.Max(max, Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))));
        }
        return max;
    }

    /// A recognisable synthetic scene — a flat swatch would prove the model ran, not that it saw.
    static void MakeScene(string path)
    {
        using var img = new Image<Rgb24>(768, 512, new Rgb24(120, 180, 235));   // sky
        img.Mutate(c => c
            .Fill(Color.FromRgb(86, 150, 70), new SixLabors.ImageSharp.Drawing.RectangularPolygon(0, 330, 768, 182))     // grass
            .Fill(Color.FromRgb(252, 222, 80), new SixLabors.ImageSharp.Drawing.EllipsePolygon(665, 85, 45))             // sun
            .Fill(Color.FromRgb(214, 196, 170), new SixLabors.ImageSharp.Drawing.RectangularPolygon(250, 220, 220, 140)) // wall
            .FillPolygon(Color.FromRgb(150, 60, 50), new PointF(230, 225), new PointF(490, 225), new PointF(360, 140))
            .Fill(Color.FromRgb(90, 60, 40), new SixLabors.ImageSharp.Drawing.RectangularPolygon(330, 280, 60, 80))      // door
            .Fill(Color.FromRgb(150, 200, 235), new SixLabors.ImageSharp.Drawing.RectangularPolygon(275, 250, 35, 35))   // windows
            .Fill(Color.FromRgb(150, 200, 235), new SixLabors.ImageSharp.Drawing.RectangularPolygon(410, 250, 35, 35))
            .Fill(Color.FromRgb(95, 65, 45), new SixLabors.ImageSharp.Drawing.RectangularPolygon(540, 300, 18, 60))      // trunk
            .Fill(Color.FromRgb(60, 120, 55), new SixLabors.ImageSharp.Drawing.EllipsePolygon(550, 275, 50)));           // canopy
        img.SaveAsJpeg(path);
    }

    /// The captioner is opt-in: with no model on disk this must report, not throw.
    static void CaptionCheck(string dir)
    {
        if (!Caption.Ready)
        {
            Console.WriteLine($"  skip  caption model not downloaded ({Caption.TotalBytes / 1e9:0.0} GB) — pipeline unaffected");
            return;
        }
        var imagePath = Path.Combine(dir, "scene.jpg");
        MakeScene(imagePath);
        var t0 = DateTime.UtcNow;
        var (text, tps) = Caption.Describe(imagePath, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"  caption: \"{text}\"  ({tps:0.0} tok/s, {(DateTime.UtcNow - t0).TotalSeconds:0.0}s incl. model load)");
        Check(text.Length > 0 && !text.Contains("<start_of_turn>"), "caption model produced a clean one-line caption");
        Caption.Unload();
    }

    public static int SelfCheck()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "photolog-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        var outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(Path.Combine(tmp, "in", "nested"));
        try
        {
            Console.WriteLine($"font: {Family.Name}");
            Console.WriteLine($"workdir: {tmp}");

            MakeJpeg(Path.Combine(tmp, "in", "a.jpg"), 400, 300, "2026:07:17 19:35:49"); // Jul 17, 7:35:49 PM
            MakeJpeg(Path.Combine(tmp, "in", "nested", "a.jpg"), 400, 300, null);       // no EXIF -> mtime
            MakeJpeg(Path.Combine(tmp, "in", "b.png"), 400, 300, "2026:07:17 08:23:59"); // Jul 17, 8:23:59 AM

            Check(FmtDate(new DateTime(2026, 7, 28, 8, 23, 59)) == "Jul 28, 2026 at 8:23:59 AM", "date format matches reference");

            // Justified grid: every closed row's aspect-true widths + gaps must exactly fill the width.
            double[] mix = [1.5, 0.75, 1.33, 1.0, 3.0, 0.56, 1.5, 1.78, 0.75, 1.5];
            var rows = JustifiedPanel.Rows(mix, 620, 160, 4);
            Check(rows.Count > 1 && rows[^1].Start + rows[^1].Count == mix.Length, "justified rows cover all photos in order");
            var flush = true;
            for (var r = 0; r < rows.Count - 1; r++)
            {
                var (s, n, h) = rows[r];
                double w = (n - 1) * 4;
                for (var i = s; i < s + n; i++) w += mix[i] * h;
                flush &= Math.Abs(w - 620) < 0.01 && h > 80 && h < 320;
            }
            Check(flush, "each justified row exactly fills the width at a sane height");
            Check(JustifiedPanel.Rows([5.0], 620, 160, 4)[0].H < 160, "single ultra-wide photo shrinks to fit, never crops");

            var found = Scan(Path.Combine(tmp, "in"));
            var names = found.Select(f => f.Name).ToArray();
            Console.WriteLine("  scan: " + string.Join(", ", names));
            // newest-first by EXIF/mtime: nested a.jpg (mtime≈now) before Jul 17 evening before Jul 17 morning
            Check(names.Contains("a.jpg") && names.Contains("a_1.jpg") && names.Contains("b.png"), "scan finds all three (+ nested dedup name)");
            Check(names.Count(n => n is "a.jpg" or "a_1.jpg" or "b.png") == 3, "recursive scan + _1 dedup for nested same name");
            Check(found[0].Taken >= found[1].Taken && found[1].Taken >= found[2].Taken, "scan sorted newest-first by EXIF/mtime");

            var aPath = found.First(f => f.Name == "a.jpg").Path;
            var a1Path = found.First(f => f.Name == "a_1.jpg").Path;
            var bPath = found.First(f => f.Name == "b.png").Path;

            var jul28 = new DateOnly(2026, 7, 28);
            var noon = new TimeOnly(12, 0, 0);
            using (var pm = Image.Load(aPath))
            using (var am = Image.Load(bPath))
            {
                Check(DateLine(pm, aPath, null) == "Jul 17, 2026 at 7:35:49 PM", "empty picker -> photo's own EXIF date+time");
                Check(DateLine(am, bPath, null) == "Jul 17, 2026 at 8:23:59 AM", "empty picker -> photo's own EXIF date+time (2nd photo)");
                // custom date replaces y/m/d only; each photo keeps its own clock
                Check(DateLine(pm, aPath, jul28) == "Jul 28, 2026 at 7:35:49 PM", "custom date keeps photo A's EXIF time");
                Check(DateLine(am, bPath, jul28) == "Jul 28, 2026 at 8:23:59 AM", "custom date keeps photo B's EXIF time");
                // custom time replaces the clock only
                Check(DateLine(pm, aPath, null, noon) == "Jul 17, 2026 at 12:00:00 PM", "custom time keeps photo A's EXIF day");
                Check(DateLine(pm, aPath, jul28, noon) == "Jul 28, 2026 at 12:00:00 PM", "custom date+time replace both");
                Check(StampLines(pm, aPath, jul28, "A\nB").SequenceEqual(["Jul 28, 2026 at 7:35:49 PM", "A", "B"]), "address lines follow the date line");
                Check(StampLines(pm, aPath, null, "").Length == 1, "empty address -> date only");
            }
            using (var probe = Image.Load(a1Path))
            {
                var mtime = File.GetLastWriteTime(a1Path);
                Check(DateLine(probe, a1Path, null) == FmtDate(mtime), "no EXIF -> file mtime fallback");
                Check(DateLine(probe, a1Path, jul28) == FmtDate(new DateTime(2026, 7, 28) + mtime.TimeOfDay),
                      "custom date keeps mtime clock when there is no EXIF");
            }

            Check(DayHeader(jul28, jul28) == "Today", "day header: today");
            Check(DayHeader(jul28.AddDays(-1), jul28) == "Yesterday", "day header: yesterday");
            Check(DayHeader(new DateOnly(2026, 7, 25), jul28) == "Saturday", "day header: weekday within a week");
            Check(DayHeader(new DateOnly(2026, 6, 1), jul28) == "Mon, Jun 1", "day header: same year older");
            Check(DayHeader(new DateOnly(2025, 6, 1), jul28) == "Sun, Jun 1, 2025", "day header: other year");

            var dest = Export(aPath, "a.jpg", outDir, null, "1521 Meander Rd\nUnited States");
            Check(File.Exists(dest), "(a) export wrote " + dest);

            var topRight = MaxDiff(aPath, dest, 0.5f, 0f, 1f, 0.35f);
            var bottomLeft = MaxDiff(aPath, dest, 0f, 0.65f, 0.5f, 1f);
            Console.WriteLine($"  maxdiff top-right={topRight} bottom-left={bottomLeft}");
            Check(topRight > 60, "(b) top-right pixels changed (stamp drawn)");
            Check(bottomLeft < 30, "(b) bottom-left pixels unchanged (stamp is anchored, not global)");

            var second = Export(a1Path, "a_1.jpg", outDir, null, "");
            Check(Path.GetFileName(second) == "a_1.jpg" && File.Exists(second), "(c) deduped name exported alongside a.jpg");
            Check(new FileInfo(aPath).Length == new FileInfo(Path.Combine(tmp, "in", "a.jpg")).Length, "original untouched");

            // date override rewrites EXIF + FS times so the file's moment matches the stamp
            var stamped = Export(aPath, "override.jpg", outDir, jul28, "");
            var stampedWhen = new DateTime(2026, 7, 28, 19, 35, 49);
            using (var back = Image.Load(stamped))
            {
                Check(DateLine(back, stamped, null) == "Jul 28, 2026 at 7:35:49 PM",
                      "export EXIF rewritten to override date + original clock");
                Check(PhotoTime(back, stamped) == stampedWhen,
                      "PhotoTime reads the written EXIF DateTimeOriginal");
            }
            Check(MaxDiff(aPath, stamped, 0.5f, 0f, 1f, 0.35f) > 60, "override export drew its stamp");
            Check(Near(File.GetLastWriteTime(stamped), stampedWhen),
                  "export LastWriteTime (Modified) matches stamp moment");
            Check(Near(File.GetCreationTime(stamped), stampedWhen),
                  "export CreationTime (Created) matches stamp moment");

            var timeOnly = Export(aPath, "time-only.jpg", outDir, null, "", overrideTime: noon);
            var noonWhen = new DateTime(2026, 7, 17, 12, 0, 0);
            using (var back = Image.Load(timeOnly))
                Check(PhotoTime(back, timeOnly) == noonWhen,
                      "time-only override rewrites EXIF clock, keeps day");
            Check(Near(File.GetLastWriteTime(timeOnly), noonWhen) && Near(File.GetCreationTime(timeOnly), noonWhen),
                  "time-only override also sets FS Created/Modified");

            var (jpeg, line) = Thumb(aPath, jul28, "");
            Check(jpeg.Length > 0 && line == "Jul 28, 2026 at 7:35:49 PM", "thumbnail preview shows the same line the export stamps");
            Check(Thumb(aPath, null, "").Date == "Jul 17, 2026 at 7:35:49 PM", "thumbnail with empty picker uses the photo's own date");
            Check(Thumb(aPath, null, "", overrideTime: noon).Date == "Jul 17, 2026 at 12:00:00 PM", "thumbnail respects time override");

            // override fields are scoped to checked photos: a.jpg checked, b.png not
            var (selDate, selTime, selAddr, selCap) = Fields(true, jul28, noon, "1521 Meander Rd", "a green field at dusk");
            var (unsDate, unsTime, unsAddr, unsCap) = Fields(false, jul28, noon, "1521 Meander Rd", "a green field at dusk");
            var selExport = Export(aPath, "scoped-selected.jpg", outDir, selDate, selAddr, caption: selCap, overrideTime: selTime);
            var unsThumb = Thumb(bPath, unsDate, unsAddr, caption: unsCap, overrideTime: unsTime);
            Check(Thumb(aPath, selDate, selAddr, caption: selCap, overrideTime: selTime).Date == "Jul 28, 2026 at 12:00:00 PM",
                  "checked photo: override date + override time");
            Check(unsThumb.Date == "Jul 17, 2026 at 8:23:59 AM",
                  "unchecked photo re-rendered alongside it keeps its original EXIF date");
            using (var sel = Image.Load(aPath))
            using (var uns = Image.Load(bPath))
            {
                var selLines = StampLines(sel, aPath, selDate, selAddr, selCap, selTime);
                Check(selLines is ["Jul 28, 2026 at 12:00:00 PM", "1521 Meander Rd"],
                      "checked photo: date + address only (caption never stamped on image)");
                Check(StampLines(uns, bPath, unsDate, unsAddr, unsCap, unsTime).Length == 1,
                      "unchecked photo gets no address and no caption");
                Check(StampLines(sel, aPath, selDate, selAddr, "ignored caption text", selTime).Length == 2,
                      "caption argument is ignored for stamp lines");
                Check(StampLines(sel, aPath, selDate, "", selCap, selTime) is ["Jul 28, 2026 at 12:00:00 PM"],
                      "caption without address: date only, no caption on pixels");
            }
            Check(File.Exists(selExport) && MaxDiff(aPath, selExport, 0.5f, 0f, 1f, 0.35f) > 60,
                  "scoped export wrote the checked photo's stamp");
            Check(Fields(false, null, null, null, null) == Fields(false, jul28, noon, "x", "y"), "nothing selected -> override changes nothing");

            // no override: EXIF + FS times still match the photo's own moment (not "export now")
            var plain = Export(aPath, "plain.jpg", outDir, null, "");
            var plainWhen = new DateTime(2026, 7, 17, 19, 35, 49);
            using (var back = Image.Load(plain))
                Check(PhotoTime(back, plain) == plainWhen,
                      "no override: EXIF is the photo's own moment");
            Check(Near(File.GetLastWriteTime(plain), plainWhen) && Near(File.GetCreationTime(plain), plainWhen),
                  "no override: FS Created/Modified are the photo's own moment, not export-now");

            Check(Caption.Tidy("  A quiet street at sunset.\nExtra rambling here <end_of_turn>") == "A quiet street at sunset",
                  "caption tidy: one line, no template debris, no trailing period");
            Check(Caption.Tidy("<start_of_turn>model\nA red barn on a hill.<end_of_turn>") == "A red barn on a hill",
                  "caption tidy: strips chat-template wrappers");

            // tidy → slug → export file name (the rename path; pure, no model)
            Check(Slug("  A quiet street at sunset.\nExtra") == "A quiet street at sunset",
                  "slug uses tidy: one line, no trailing period");
            Check(Slug("foo/bar:baz*qux?") == "foo bar baz qux",
                  "slug strips path/illegal characters");
            Check(Slug("") == "" && ExportFileName("snap.jpg", "", new HashSet<string>()) == "snap.jpg",
                  "empty caption keeps original base name");
            Check(ExportFileName("", null, new HashSet<string>()) == "photo.jpg",
                  "empty stem falls back to photo.jpg (never blank)");
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var n1 = ExportFileName("a.jpg", "A quiet street at sunset", used);
                var n2 = ExportFileName("b.jpg", "A quiet street at sunset", used);
                var n3 = ExportFileName("c.jpg", null, used);
                Check(n1 == "A quiet street at sunset.jpg", "caption renames export base");
                Check(n2 == "A quiet street at sunset_1.jpg", "same caption gets _1 collision suffix");
                Check(n3 == "c.jpg", "no caption keeps original name");
                var destCap = Export(found[0].Path, n1, outDir, null, "", caption: "A quiet street at sunset.");
                Check(Path.GetFileName(destCap) == "A quiet street at sunset.jpg" && File.Exists(destCap),
                      "export wrote caption-derived file name");
            }

            CaptionCheck(tmp);

            // drop shadow: weight + freeform offset
            var lightJpeg = Thumb(found[0].Path, null, "", drop: DropShadow.Light, shadowX: 1, shadowY: -1).Jpeg;
            Check(Thumb(found[0].Path, null, "").Jpeg.SequenceEqual(lightJpeg),
                  "default drop shadow is Light at (1,-1)");
            Check(!lightJpeg.SequenceEqual(Thumb(found[0].Path, null, "", drop: DropShadow.Off).Jpeg),
                  "Off removes the dark twin");
            Check(!lightJpeg.SequenceEqual(Thumb(found[0].Path, null, "", drop: DropShadow.Heavy, shadowX: 3, shadowY: -2).Jpeg),
                  "Heavy renders thicker than Light");
            Check(!lightJpeg.SequenceEqual(Thumb(found[0].Path, null, "", drop: DropShadow.Light, shadowX: 6, shadowY: 4).Jpeg),
                  "custom Light offset changes the render");
            Check(!Thumb(found[0].Path, null, "", drop: DropShadow.Heavy, shadowX: 2, shadowY: -2).Jpeg
                    .SequenceEqual(Thumb(found[0].Path, null, "", drop: DropShadow.Heavy, shadowX: 5, shadowY: -3).Jpeg),
                  "Heavy offset is user-configurable");
            Check(DropOffset(DropShadow.Light) == (1, -1) && DropOffset(DropShadow.Heavy) == (3, -2),
                  "preset offset map: Light (1,-1) Heavy (3,-2)");

            // settings Save/Load against a temp file (never touches real user prefs)
            {
                var prev = Settings.PathOverride;
                Settings.PathOverride = Path.Combine(tmp, "settings.json");
                try
                {
                    Settings.Save(new Settings.Data
                    {
                        DropShadow = DropShadow.Heavy,
                        ShadowX = 4,
                        ShadowY = -3,
                        OutFolder = "/tmp/out",
                        LastFolder = "/tmp/in",
                        Address = "line1\nline2",
                        Theme = "Light",
                    });
                    var back = Settings.Load();
                    Check(back.DropShadow == DropShadow.Heavy && back.ShadowX == 4 && back.ShadowY == -3
                          && back.OutFolder == "/tmp/out" && back.LastFolder == "/tmp/in"
                          && back.Address == "line1\nline2" && back.Theme == "Light",
                          "settings Save/Load round-trips drop + offset + folders + address + theme");
                }
                finally { Settings.PathOverride = prev; }
            }

            // Google Photos day-header select state: none / partial / all
            {
                var day = new DayGroup { Header = "Monday", Day = new DateOnly(2026, 7, 27) };
                var a = new PhotoItem { Name = "a.jpg", Path = "/a", Taken = DateTime.Now };
                var b = new PhotoItem { Name = "b.jpg", Path = "/b", Taken = DateTime.Now };
                day.Photos.Add(a);
                day.Photos.Add(b);
                day.RefreshSelection();
                Check(day.NoneSelected && !day.PartialSelected && !day.AllSelected, "day select: none");
                a.Selected = true;
                day.RefreshSelection();
                Check(day.PartialSelected && !day.AllSelected && day.AnySelected, "day select: partial");
                b.Selected = true;
                day.RefreshSelection();
                Check(day.AllSelected && !day.PartialSelected && day.AnySelected, "day select: all");
                a.Selected = b.Selected = false;
                day.RefreshSelection();
                Check(day.NoneSelected, "day select: back to none");
            }

            Console.WriteLine("selfcheck OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selfcheck FAILED: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* leave it */ }
        }
    }
}
