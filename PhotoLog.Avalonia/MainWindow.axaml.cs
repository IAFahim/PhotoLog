using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PhotoLog.Avalonia;

public partial class MainWindow : Window
{
    readonly ObservableCollection<PhotoItem> _items = [];
    PhotoItem? _preview;
    CancellationTokenSource? _cts;
    (DateOnly? Date, string Addr, int Dx, int Dy) _rendered;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFolder)
    {
        InitializeComponent();
        PhotoGrid.ItemsSource = _items;
        OutBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PhotoLog-output");
        DownloadModelBtn.Content = $"Download AI model ({Caption.TotalBytes / 1e9:0.0} GB)";
        DownloadModelBtn.IsVisible = !Caption.Ready;
        // opaque window only — Mica/TransparentBackground made the whole UI see-through on
        // Wayland/X11 when the compositor didn't actually fill a solid backdrop
        if (string.IsNullOrWhiteSpace(initialFolder)) return;
        FolderBox.Text = initialFolder;
        Opened += async (_, _) => await Rescan(); // `photolog ~/pics` opens straight into the folder
    }

    // empty picker (the default) -> every photo keeps its own EXIF date; a picked date
    // replaces only y/m/d, never the photo's clock (Core.DateLine does the combining)
    DateOnly? OverrideDate => DatePart.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;

    // global rendering settings — unlike date/address they apply to every preview and export
    int ShadowX => (int)(ShadowXBox.Value ?? Core.DefaultShadowX);
    int ShadowY => (int)(ShadowYBox.Value ?? Core.DefaultShadowY);

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Pick photo folder", AllowMultiple = false });
        if (picked.Count == 0) return;
        FolderBox.Text = picked[0].TryGetLocalPath() ?? picked[0].Path.LocalPath;
        await Rescan();
    }

    async void Load_Click(object? sender, RoutedEventArgs e) => await Rescan();

    async void Folder_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await Rescan();
    }

    void ClearDate_Click(object? sender, RoutedEventArgs e) => DatePart.SelectedDate = null;

    // date/address/style changes re-render every preview but must never touch the selection
    async void DateChanged(object? sender, SelectionChangedEventArgs e) => await Refresh();
    async void StyleChanged(object? sender, NumericUpDownValueChangedEventArgs e) => await Refresh();
    async void Refresh_Click(object? sender, RoutedEventArgs e) => await Refresh();

    // leaving the address box only costs a re-render when the text actually changed
    async void Addr_LostFocus(object? sender, RoutedEventArgs e)
    {
        if ((OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY) != _rendered) await Refresh();
    }

    async void OutBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Pick output folder", AllowMultiple = false });
        if (picked.Count == 0) return;
        OutBox.Text = picked[0].TryGetLocalPath() ?? picked[0].Path.LocalPath;
    }

    async Task Refresh()
    {
        if (_items.Count == 0) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Status.Text = $"{_items.Count} image(s) loaded — rendering previews…";
        await RenderThumbs([.. _items], OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        _rendered = (OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY);
        Status.Text = $"{_items.Count} image(s) loaded.";
        if (_preview is not null) ShowPreview(_preview);
    }

    async Task Rescan()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var folder = Core.Expand(FolderBox.Text);
        var found = Core.Scan(folder);
        ClearPreview();
        if (found.Count == 0)
        {
            _items.Clear();
            UpdateCount();
            EmptyGrid.IsVisible = true;
            PhotoScroll.IsVisible = false;
            Status.Text = Directory.Exists(folder)
                ? "No photos in that folder — try another path."
                : "Choose a folder to get started.";
            return;
        }

        _items.Clear(); // a freshly loaded folder always starts with nothing selected
        EmptyGrid.IsVisible = false;
        PhotoScroll.IsVisible = true;
        foreach (var (name, path) in found)
            _items.Add(new PhotoItem { Name = name, Path = path, Tip = name });
        UpdateCount();
        Status.Text = $"Loading {_items.Count} photo(s)…";

        await RenderThumbs([.. _items], OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY, ct);
        if (ct.IsCancellationRequested) return;
        _rendered = (OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY);
        Status.Text = $"{_items.Count} photo(s) loaded. Select some, then Apply.";
    }

    static async Task RenderThumbs(IReadOnlyList<PhotoItem> items, DateOnly? date, string addr, int dx, int dy,
                                   CancellationToken ct)
    {
        try
        {
            await Task.Run(() => Parallel.ForEach(
                items,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                item =>
                {
                    try
                    {
                        var (d, a, cap) = Core.Fields(item.Selected, date, addr, item.Caption);
                        var (jpeg, line) = Core.Thumb(item.Path, d, a, shadowX: dx, shadowY: dy, caption: cap);
                        var bmp = new Bitmap(new MemoryStream(jpeg));
                        Dispatcher.UIThread.Post(() => { item.Thumb = bmp; item.Tip = $"{item.Name} — {line}"; });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => item.Tip = $"{item.Name} — cannot read: {ex.Message}");
                    }
                }), ct);
        }
        catch (OperationCanceledException) { /* superseded by a newer scan */ }
    }

    // an override only applies to checked photos, so toggling one changes what it should look like
    bool HasOverride => OverrideDate is not null || !string.IsNullOrWhiteSpace(AddrBox.Text);

    async void Cell_Click(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PhotoItem item) return;
        item.Selected = !item.Selected;
        UpdateCount();
        ShowPreview(item);
        // selection gates the override AND this photo's caption, so either one means a restamp
        if (HasOverride || item.Caption.Length > 0) await RestampOne(item);
    }

    void ShowPreview(PhotoItem item)
    {
        _preview = item;
        PreviewImage.Source = item.Thumb;
        PreviewName.Text = item.Name + (item.Selected ? "  [selected ✓]" : "  [not selected]");
        CaptionBox.Text = item.Caption;
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = true; // manual captions never need the model
        CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = Caption.Ready;
    }

    void ClearPreview()
    {
        _preview = null;
        PreviewImage.Source = null;
        PreviewName.Text = "Select a photo to preview and caption it";
        CaptionBox.Text = "";
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = false;
    }

    // ---- opt-in local captioning (Gemma 4 E2B via llama.cpp, downloaded on first use) ----

    async void Caption_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        // tidy so stamp + rename slug see the same one-line form as the model path
        var cleaned = Caption.Tidy(CaptionBox.Text ?? "");
        if (cleaned != (CaptionBox.Text ?? "")) CaptionBox.Text = cleaned;
        if (_preview.Caption == cleaned) return;
        _preview.Caption = cleaned;
        await RestampOne(_preview); // an edited or cleared caption changes what the stamp says
    }

    async Task RestampOne(PhotoItem item)
    {
        await RenderThumbs([item], OverrideDate, AddrBox.Text ?? "", ShadowX, ShadowY, CancellationToken.None);
        if (_preview == item) ShowPreview(item);
    }

    async void Caption_Click(object? sender, RoutedEventArgs e)
    {
        if (_preview is not null) await RunCaptions([_preview]);
    }

    async void CaptionAll_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToArray();
        if (selected.Length == 0) { AiStatus.Text = "Nothing selected to caption."; return; }
        await RunCaptions(selected);
    }

    async Task RunCaptions(IReadOnlyList<PhotoItem> items)
    {
        if (!await EnsureModel()) return;
        CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = false;
        AiProgress.IsVisible = true;
        AiProgress.IsIndeterminate = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                AiStatus.Text = $"Captioning {i + 1} of {items.Count} — {items[i].Name}…";
                var (text, tps) = await Caption.Describe(items[i].Path, CancellationToken.None);
                items[i].Caption = text;
                if (_preview == items[i]) CaptionBox.Text = text;
                await RestampOne(items[i]);
                AiStatus.Text = $"Captioned {i + 1} of {items.Count} ({tps:0.0} tok/s). Exports will use this name.";
            }
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Captioning failed: " + ex.Message;
        }
        finally
        {
            AiProgress.IsVisible = false;
            AiProgress.IsIndeterminate = false;
            CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = _preview is not null && Caption.Ready;
        }
    }

    /// The model only arrives via the explicit Download button; Caption itself never downloads.
    Task<bool> EnsureModel()
    {
        if (Caption.Ready) return Task.FromResult(true);
        AiStatus.Text = "Download the AI model first — captioning then runs offline on this machine.";
        return Task.FromResult(false);
    }

    async void DownloadModel_Click(object? sender, RoutedEventArgs e)
    {
        var gb = Caption.TotalBytes / 1e9;
        DownloadModelBtn.IsEnabled = false;
        AiProgress.IsVisible = true;
        AiProgress.IsIndeterminate = false;
        AiProgress.Value = 0;
        try
        {
            AiStatus.Text = $"Downloading model into {Caption.Dir}…";
            var progress = new Progress<double>(f =>
            {
                AiProgress.Value = f * 100;
                AiStatus.Text = $"Downloading model… {f * gb:0.00} / {gb:0.0} GB ({f:P0})";
            });
            await Caption.Download(progress, CancellationToken.None);
            AiStatus.Text = "Model ready — pick a photo and caption it.";
            DownloadModelBtn.IsVisible = false;
            if (_preview is not null) ShowPreview(_preview); // re-sync caption button states
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Download failed: " + ex.Message;
            DownloadModelBtn.IsEnabled = true;
        }
        finally
        {
            AiProgress.IsVisible = false;
        }
    }

    async void SelectAll_Click(object? sender, RoutedEventArgs e) => await SetAll(true);
    async void SelectNone_Click(object? sender, RoutedEventArgs e) => await SetAll(false);

    async Task SetAll(bool value)
    {
        foreach (var i in _items) i.Selected = value;
        UpdateCount();
        if (HasOverride) await Refresh();
        else if (_preview is not null) ShowPreview(_preview);
    }

    void UpdateCount() => CountText.Text = $"{_items.Count(i => i.Selected)} of {_items.Count} selected";

    async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToArray();
        if (selected.Length == 0)
        {
            Result.Text = "Select photos in the grid first, then apply.";
            return;
        }
        await Write(selected, "Exported");
    }

    async void SaveOne_Click(object? sender, RoutedEventArgs e)
    {
        // always stamp/rename this preview with current UI fields + its caption (even if unchecked)
        if (_preview is not null) await Write([_preview], "Saved", forceSelected: true);
    }

    async Task Write(IReadOnlyList<PhotoItem> items, string verb, bool forceSelected = false)
    {
        var outDir = Core.Expand(OutBox.Text);
        if (outDir.Length == 0)
        {
            Result.Text = "Choose an output folder first.";
            return;
        }
        var date = OverrideDate;
        var addr = AddrBox.Text ?? "";
        var (dx, dy) = (ShadowX, ShadowY);
        ApplyBtn.IsEnabled = SaveOneBtn.IsEnabled = false;
        Result.Text = $"Writing {items.Count} photo(s) to {outDir}…";
        try
        {
            // sequential so ExportFileName collision suffixes are stable and race-free
            var names = await Task.Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var written = new List<string>(items.Count);
                foreach (var i in items)
                {
                    var (d, a, cap) = Core.Fields(forceSelected || i.Selected, date, addr, i.Caption);
                    // caption (when present) becomes the export base name; empty keeps original
                    var name = Core.ExportFileName(i.Name, cap, used);
                    Core.Export(i.Path, name, outDir, d, a, dx, dy, cap);
                    written.Add(name);
                }
                return written;
            });
            var sample = names.Count == 1 ? names[0] : $"{names[0]} (+{names.Count - 1} more)";
            Result.Text = $"{verb} — {names.Count} photo(s) → {outDir}  ·  e.g. {sample}";
        }
        catch (Exception ex)
        {
            Result.Text = "Could not write files: " + ex.Message;
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
            SaveOneBtn.IsEnabled = _preview is not null;
        }
    }
}
