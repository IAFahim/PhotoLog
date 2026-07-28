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
    (DateOnly? Date, string Addr, string Style) _rendered;
    bool _sizeWarned;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFolder)
    {
        InitializeComponent();
        PhotoGrid.ItemsSource = _items;
        StyleCombo.ItemsSource = Core.Styles;
        StyleCombo.SelectedIndex = 0; // outlined + drop shadow is the default look
        OutBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PhotoLog-output");
        // Mica/acrylic needs a transparent window; where the compositor can't do it (most X11),
        // drop back to the opaque theme background so the window isn't see-through.
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.None) ClearValue(BackgroundProperty);
        };
        if (string.IsNullOrWhiteSpace(initialFolder)) return;
        FolderBox.Text = initialFolder;
        Opened += async (_, _) => await Rescan(); // `photolog ~/pics` opens straight into the folder
    }

    // empty picker (the default) -> every photo keeps its own EXIF date; a picked date
    // replaces only y/m/d, never the photo's clock (Core.DateLine does the combining)
    DateOnly? OverrideDate => DatePart.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;

    // global rendering setting — unlike date/address it applies to every preview and export
    string StampStyle => StyleCombo.SelectedItem as string ?? Core.Styles[0];

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
    async void StyleChanged(object? sender, SelectionChangedEventArgs e) => await Refresh();
    async void Refresh_Click(object? sender, RoutedEventArgs e) => await Refresh();

    // leaving the address box only costs a re-render when the text actually changed
    async void Addr_LostFocus(object? sender, RoutedEventArgs e)
    {
        if ((OverrideDate, AddrBox.Text ?? "", StampStyle) != _rendered) await Refresh();
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
        await RenderThumbs([.. _items], OverrideDate, AddrBox.Text ?? "", StampStyle, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        _rendered = (OverrideDate, AddrBox.Text ?? "", StampStyle);
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
            Status.Text = Directory.Exists(folder)
                ? "No images in that folder."
                : "Pick a folder with Browse, or type its path and press Enter.";
            return;
        }

        _items.Clear(); // a freshly loaded folder always starts with nothing selected
        foreach (var (name, path) in found)
            _items.Add(new PhotoItem { Name = name, Path = path, Tip = name });
        UpdateCount();
        Status.Text = $"{_items.Count} image(s) loaded — rendering previews…";

        await RenderThumbs([.. _items], OverrideDate, AddrBox.Text ?? "", StampStyle, ct);
        if (ct.IsCancellationRequested) return;
        _rendered = (OverrideDate, AddrBox.Text ?? "", StampStyle);
        Status.Text = $"{_items.Count} image(s) loaded.";
    }

    static async Task RenderThumbs(IReadOnlyList<PhotoItem> items, DateOnly? date, string addr, string style,
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
                        var (jpeg, line) = Core.Thumb(item.Path, d, a, cap, style);
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
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = CaptionBtn.IsEnabled = true;
        CaptionAllBtn.IsEnabled = true;
    }

    void ClearPreview()
    {
        _preview = null;
        PreviewImage.Source = null;
        PreviewName.Text = "Click an image to preview it and toggle selection";
        CaptionBox.Text = "";
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = false;
    }

    // ---- opt-in local captioning (Gemma 4 E2B via llama.cpp, downloaded on first use) ----

    async void Caption_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_preview is null || _preview.Caption == (CaptionBox.Text ?? "")) return;
        _preview.Caption = CaptionBox.Text ?? "";
        await RestampOne(_preview); // an edited or cleared caption changes what the stamp says
    }

    async Task RestampOne(PhotoItem item)
    {
        await RenderThumbs([item], OverrideDate, AddrBox.Text ?? "", StampStyle, CancellationToken.None);
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
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                AiStatus.Text = $"Captioning {i + 1}/{items.Count}: {items[i].Name}…";
                var (text, tps) = await Caption.Describe(items[i].Path, CancellationToken.None);
                items[i].Caption = text;
                if (_preview == items[i]) CaptionBox.Text = text;
                await RestampOne(items[i]);
                AiStatus.Text = $"Captioned {i + 1}/{items.Count} ({tps:0.0} tok/s)";
            }
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Captioning failed: " + ex.Message;
        }
        finally
        {
            CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = _preview is not null;
        }
    }

    /// Dormant until the user says yes: the model is only fetched after an explicit confirmation.
    async Task<bool> EnsureModel()
    {
        if (Caption.Ready) return true;
        var gb = Caption.TotalBytes / 1e9;
        if (!_sizeWarned)
        {
            AiStatus.Text = $"Captioning needs a one-time {gb:0.0} GB download (Gemma 4 E2B, runs offline on CPU) "
                          + "into " + Caption.Dir + ". Click Caption again to start.";
            _sizeWarned = true;
            return false;
        }
        CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(f => AiStatus.Text = $"Downloading model… {f * gb:0.00} / {gb:0.0} GB ({f:P0})");
            await Caption.Download(progress, CancellationToken.None);
            AiStatus.Text = "Model ready.";
            return true;
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Download failed: " + ex.Message;
            return false;
        }
        finally
        {
            CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = _preview is not null;
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

    void UpdateCount() => CountText.Text = $"{_items.Count(i => i.Selected)}/{_items.Count} selected";

    async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToArray();
        if (selected.Length == 0)
        {
            Result.Text = "No images selected — click some previews first.";
            return;
        }
        await Write(selected, "Done");
    }

    async void SaveOne_Click(object? sender, RoutedEventArgs e)
    {
        if (_preview is not null) await Write([_preview], "Saved");
    }

    async Task Write(IReadOnlyList<PhotoItem> items, string verb)
    {
        var outDir = Core.Expand(OutBox.Text);
        if (outDir.Length == 0) { Result.Text = "Set an output folder first."; return; }
        var date = OverrideDate;
        var addr = AddrBox.Text ?? "";
        var style = StampStyle;
        ApplyBtn.IsEnabled = SaveOneBtn.IsEnabled = false;
        Result.Text = $"Writing {items.Count} image(s) to {outDir}…";
        try
        {
            await Task.Run(() => Parallel.ForEach(items, i =>
            {
                var (d, a, cap) = Core.Fields(i.Selected, date, addr, i.Caption);
                Core.Export(i.Path, i.Name, outDir, d, a, cap, style);
            }));
            Result.Text = $"{verb} — {items.Count} image(s) written to {outDir}";
        }
        catch (Exception ex)
        {
            Result.Text = "Error: " + ex.Message;
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
            SaveOneBtn.IsEnabled = _preview is not null;
        }
    }
}
