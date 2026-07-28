using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoLog.Avalonia;

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

    /// Recursive, sorted by filename; duplicate filenames from nested folders get _1/_2 suffixes.
    public static List<(string Name, string Path)> Scan(string? folder)
    {
        var root = Expand(folder);
        if (root.Length == 0 || !Directory.Exists(root)) return [];
        var used = new HashSet<string>();
        var list = new List<(string, string)>();
        foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => Exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(p);
            for (var i = 1; !used.Add(name); i++)
                name = $"{Path.GetFileNameWithoutExtension(p)}_{i}{Path.GetExtension(p)}";
            list.Add((name, p));
        }
        return list;
    }

    static string? Tag(ExifProfile e, ExifTag<string> t) =>
        e.TryGetValue(t, out var v) && !string.IsNullOrWhiteSpace(v.Value) ? v.Value : null;

    /// The photo's own moment: EXIF DateTimeOriginal (0x9003) -> EXIF DateTime (0x0132) -> file mtime.
    public static DateTime PhotoTime(Image img, string path)
    {
        var raw = img.Metadata.ExifProfile is { } e
            ? Tag(e, ExifTag.DateTimeOriginal) ?? Tag(e, ExifTag.DateTime) : null;
        return raw is not null && DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                                                         DateTimeStyles.None, out var d)
            ? d : File.GetLastWriteTime(path);
    }

    /// A custom date replaces only y/m/d — the clock always stays the photo's own.
    public static string DateLine(Image img, string path, DateOnly? overrideDate)
    {
        var t = PhotoTime(img, path);
        return FmtDate(overrideDate is { } d ? d.ToDateTime(TimeOnly.FromDateTime(t)) : t);
    }

    /// The override fields are scoped to checked images; an unchecked photo always
    /// stamps its own EXIF date and no address. Every render/export path goes through here.
    public static (DateOnly? Date, string Addr) Fields(bool selected, DateOnly? date, string? addr) =>
        selected ? (date, addr ?? "") : (null, "");

    public static string[] StampLines(Image img, string path, DateOnly? overrideDate, string? addr)
    {
        var date = DateLine(img, path, overrideDate);
        var a = (addr ?? "").Trim();
        return a.Length == 0 ? [date] : [date, .. a.ReplaceLineEndings("\n").Split('\n')];
    }

    public static void Stamp(Image img, string[] lines)
    {
        var size = Math.Max(14, img.Width / 30);
        var font = Family.CreateFont(size);
        var text = string.Join('\n', lines);
        var off = Math.Max(1, size / 12);
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
        img.Mutate(c => c.DrawText(At(off, off), text, Color.Black)   // soft shadow
                         .DrawText(At(0, 0), text, Color.White));
    }

    /// Stamped preview, decoded small (JPEG scaled DCT) so a folder scan stays fast.
    public static (byte[] Jpeg, string Date) Thumb(string path, DateOnly? overrideDate, string? addr, int maxSide = 768)
    {
        using var img = Image.Load(new DecoderOptions { TargetSize = new Size(maxSide, maxSide) }, path);
        img.Mutate(c => c.AutoOrient());
        if (img.Width > maxSide || img.Height > maxSide)
            img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(maxSide, maxSide), Mode = ResizeMode.Max }));
        var lines = StampLines(img, path, overrideDate, addr);
        Stamp(img, lines);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 88 });
        return (ms.ToArray(), lines[0]);
    }

    /// Full-resolution stamped copy into outDir. Originals are never touched.
    public static string Export(string src, string name, string outDir, DateOnly? overrideDate, string? addr)
    {
        Directory.CreateDirectory(outDir);
        using var img = Image.Load(src);
        img.Mutate(c => c.AutoOrient());
        Stamp(img, StampLines(img, src, overrideDate, addr));
        var dest = Path.Combine(outDir, name);
        img.Save(dest); // encoder from extension; ImageSharp re-writes the EXIF profile it read
        return dest;
    }

    // ---- headless self-check (dotnet run -- --selfcheck) ----

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) throw new InvalidOperationException(what);
    }

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

            var found = Scan(Path.Combine(tmp, "in"));
            var names = found.Select(f => f.Name).ToArray();
            Console.WriteLine("  scan: " + string.Join(", ", names));
            Check(names.SequenceEqual(["a.jpg", "a_1.jpg", "b.png"]), "recursive scan sorted by filename + _1 dedup");

            var jul28 = new DateOnly(2026, 7, 28);
            using (var pm = Image.Load(found[0].Path))
            using (var am = Image.Load(found[2].Path))
            {
                Check(DateLine(pm, found[0].Path, null) == "Jul 17, 2026 at 7:35:49 PM", "empty picker -> photo's own EXIF date+time");
                Check(DateLine(am, found[2].Path, null) == "Jul 17, 2026 at 8:23:59 AM", "empty picker -> photo's own EXIF date+time (2nd photo)");
                // custom date replaces y/m/d only; each photo keeps its own clock
                Check(DateLine(pm, found[0].Path, jul28) == "Jul 28, 2026 at 7:35:49 PM", "custom date keeps photo A's EXIF time");
                Check(DateLine(am, found[2].Path, jul28) == "Jul 28, 2026 at 8:23:59 AM", "custom date keeps photo B's EXIF time");
                Check(StampLines(pm, found[0].Path, jul28, "A\nB").SequenceEqual(["Jul 28, 2026 at 7:35:49 PM", "A", "B"]), "address lines follow the date line");
                Check(StampLines(pm, found[0].Path, null, "").Length == 1, "empty address -> date only");
            }
            using (var probe = Image.Load(found[1].Path))
            {
                var mtime = File.GetLastWriteTime(found[1].Path);
                Check(DateLine(probe, found[1].Path, null) == FmtDate(mtime), "no EXIF -> file mtime fallback");
                Check(DateLine(probe, found[1].Path, jul28) == FmtDate(new DateTime(2026, 7, 28) + mtime.TimeOfDay),
                      "custom date keeps mtime clock when there is no EXIF");
            }

            var dest = Export(found[0].Path, found[0].Name, outDir, null, "1521 Meander Rd\nUnited States");
            Check(File.Exists(dest), "(a) export wrote " + dest);

            var topRight = MaxDiff(found[0].Path, dest, 0.5f, 0f, 1f, 0.35f);
            var bottomLeft = MaxDiff(found[0].Path, dest, 0f, 0.65f, 0.5f, 1f);
            Console.WriteLine($"  maxdiff top-right={topRight} bottom-left={bottomLeft}");
            Check(topRight > 60, "(b) top-right pixels changed (stamp drawn)");
            Check(bottomLeft < 30, "(b) bottom-left pixels unchanged (stamp is anchored, not global)");

            var second = Export(found[1].Path, found[1].Name, outDir, null, "");
            Check(Path.GetFileName(second) == "a_1.jpg" && File.Exists(second), "(c) deduped name exported alongside a.jpg");
            Check(new FileInfo(found[0].Path).Length == new FileInfo(Path.Combine(tmp, "in", "a.jpg")).Length, "original untouched");

            // the stamp actually written to disk keeps the photo's clock under a custom date
            var stamped = Export(found[0].Path, "override.jpg", outDir, jul28, "");
            using (var back = Image.Load(stamped))
                Check(DateLine(back, stamped, null) == "Jul 17, 2026 at 7:35:49 PM", "export preserves the source EXIF (date override is pixels only)");
            Check(MaxDiff(found[0].Path, stamped, 0.5f, 0f, 1f, 0.35f) > 60, "override export drew its stamp");

            var (jpeg, line) = Thumb(found[0].Path, jul28, "");
            Check(jpeg.Length > 0 && line == "Jul 28, 2026 at 7:35:49 PM", "thumbnail preview shows the same line the export stamps");
            Check(Thumb(found[0].Path, null, "").Date == "Jul 17, 2026 at 7:35:49 PM", "thumbnail with empty picker uses the photo's own date");

            // override fields are scoped to checked photos: a.jpg checked, b.png not
            var (selDate, selAddr) = Fields(true, jul28, "1521 Meander Rd");
            var (unsDate, unsAddr) = Fields(false, jul28, "1521 Meander Rd");
            var selExport = Export(found[0].Path, "scoped-selected.jpg", outDir, selDate, selAddr);
            var unsThumb = Thumb(found[2].Path, unsDate, unsAddr);
            Check(Thumb(found[0].Path, selDate, selAddr).Date == "Jul 28, 2026 at 7:35:49 PM",
                  "checked photo: override date + its own time");
            Check(unsThumb.Date == "Jul 17, 2026 at 8:23:59 AM",
                  "unchecked photo re-rendered alongside it keeps its original EXIF date");
            using (var sel = Image.Load(found[0].Path))
            using (var uns = Image.Load(found[2].Path))
            {
                Check(StampLines(sel, found[0].Path, selDate, selAddr).Length == 2, "checked photo gets the address line");
                Check(StampLines(uns, found[2].Path, unsDate, unsAddr).Length == 1, "unchecked photo gets no address line");
            }
            Check(File.Exists(selExport) && MaxDiff(found[0].Path, selExport, 0.5f, 0f, 1f, 0.35f) > 60,
                  "scoped export wrote the checked photo's stamp");
            Check(Fields(false, null, null) == Fields(false, jul28, "x"), "nothing selected -> override changes nothing");

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
