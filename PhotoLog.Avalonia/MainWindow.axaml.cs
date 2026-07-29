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
    readonly ObservableCollection<DayGroup> _days = [];
    PhotoItem? _preview;
    CancellationTokenSource? _cts;
    (DateOnly? Date, TimeOnly? Time, string Addr, DropShadow Drop, int Dx, int Dy) _rendered;
    bool _loadingSettings; // suppress Save/Refresh while applying prefs into the UI
    DropShadow _drop = DropShadow.Light; // source of truth (combo can fire before items exist)
    string _theme = "Dark";

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFolder)
    {
        InitializeComponent();
        PhotoGrid.ItemsSource = _days;
        DownloadModelBtn.Content = $"Download AI model ({Caption.TotalBytes / 1e9:0.0} GB)";
        DownloadModelBtn.IsVisible = !Caption.Ready;

        ApplySettings(Settings.Load());
        // CLI folder wins over the remembered last folder; always try to open one on launch
        if (!string.IsNullOrWhiteSpace(initialFolder))
            FolderBox.Text = initialFolder;
        if (!string.IsNullOrWhiteSpace(FolderBox.Text))
            Opened += async (_, _) => await Rescan();

        Closing += (_, _) => Persist(); // last-chance flush (also saved on each change)
    }

    void ApplySettings(Settings.Data s)
    {
        _loadingSettings = true;
        try
        {
            _drop = s.DropShadow;
            SelectDrop(s.DropShadow);
            if (ShadowXBox is not null) ShadowXBox.Value = s.ShadowX;
            if (ShadowYBox is not null) ShadowYBox.Value = s.ShadowY;
            SyncOffsetEnabled();
            OutBox.Text = string.IsNullOrWhiteSpace(s.OutFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PhotoLog-output")
                : s.OutFolder;
            if (!string.IsNullOrWhiteSpace(s.LastFolder)) FolderBox.Text = s.LastFolder;
            AddrBox.Text = s.Address ?? "";
            _theme = string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            Settings.ApplyTheme(_theme);
            SyncThemeButton();
        }
        finally { _loadingSettings = false; }
    }

    void SyncThemeButton()
    {
        if (ThemeBtn is null) return;
        // button shows the mode you switch *to*
        ThemeBtn.Content = _theme == "Dark" ? "Light mode" : "Dark mode";
    }

    void Theme_Click(object? sender, RoutedEventArgs e)
    {
        _theme = _theme == "Dark" ? "Light" : "Dark";
        Settings.ApplyTheme(_theme);
        SyncThemeButton();
        Persist();
    }

    void SelectDrop(DropShadow drop)
    {
        if (DropBox is null) return;
        for (var i = 0; i < DropBox.ItemCount; i++)
        {
            if (ReadDropTag(DropBox.Items[i]) is { } d && d == drop)
            {
                DropBox.SelectedIndex = i;
                return;
            }
        }
        DropBox.SelectedIndex = 1; // Light
    }

    static DropShadow? ReadDropTag(object? item)
    {
        if (item is ComboBoxItem cbi && cbi.Tag is string tag
            && Enum.TryParse<DropShadow>(tag, true, out var d))
            return d;
        if (item is string s && Enum.TryParse<DropShadow>(s, true, out var d2))
            return d2;
        return null;
    }

    DropShadow CurrentDrop => _drop;
    int ShadowX => (int)(ShadowXBox?.Value ?? Core.DefaultShadowX);
    int ShadowY => (int)(ShadowYBox?.Value ?? Core.DefaultShadowY);

    void SyncOffsetEnabled()
    {
        var on = _drop != DropShadow.Off;
        if (ShadowXBox is not null) ShadowXBox.IsEnabled = on;
        if (ShadowYBox is not null) ShadowYBox.IsEnabled = on;
    }

    void Persist()
    {
        if (_loadingSettings) return;
        try
        {
            Settings.Save(new Settings.Data
            {
                DropShadow = _drop,
                ShadowX = ShadowX,
                ShadowY = ShadowY,
                OutFolder = OutBox?.Text?.Trim(),
                LastFolder = FolderBox?.Text?.Trim(),
                Address = AddrBox?.Text,
                Theme = _theme,
            });
        }
        catch { /* prefs are best-effort; never crash the UI */ }
    }

    // empty pickers (the default) -> every photo keeps its own EXIF date/time; each field is independent
    DateOnly? OverrideDate => DatePart.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
    TimeOnly? OverrideTime => TimePart.SelectedTime is { } ts ? TimeOnly.FromTimeSpan(ts) : null;

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var opts = new FolderPickerOpenOptions { Title = "Open photo folder", AllowMultiple = false };
        if (await TryFolder(FolderBox.Text) is { } start)
            opts.SuggestedStartLocation = start;
        var picked = await StorageProvider.OpenFolderPickerAsync(opts);
        if (picked.Count == 0) return;
        FolderBox.Text = picked[0].TryGetLocalPath() ?? picked[0].Path.LocalPath;
        Persist();
        await Rescan();
    }

    /// Typed path: Enter loads (Browse/Open already loads after pick).
    async void Folder_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Persist(); await Rescan(); }
    }

    void ClearDate_Click(object? sender, RoutedEventArgs e) => DatePart.SelectedDate = null;
    void ClearTime_Click(object? sender, RoutedEventArgs e) => TimePart.SelectedTime = null;

    async void DateChanged(object? sender, SelectionChangedEventArgs e) => await Refresh();
    async void TimeChanged(object? sender, TimePickerSelectedValueChangedEventArgs e) => await Refresh();

    async void DropChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (ReadDropTag(DropBox?.SelectedItem) is not { } d) return;
        _drop = d;
        // picking a weight also snaps to its suggested offset (user can then fine-tune X/Y)
        if (d != DropShadow.Off)
        {
            var (px, py) = Core.DropOffset(d);
            _loadingSettings = true;
            try
            {
                if (ShadowXBox is not null) ShadowXBox.Value = px;
                if (ShadowYBox is not null) ShadowYBox.Value = py;
            }
            finally { _loadingSettings = false; }
        }
        SyncOffsetEnabled();
        Persist();
        await Refresh();
    }

    async void OffsetChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        Persist();
        await Refresh();
    }

    async void Refresh_Click(object? sender, RoutedEventArgs e) => await Refresh();

    async void Addr_LostFocus(object? sender, RoutedEventArgs e)
    {
        Persist();
        if ((OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop, ShadowX, ShadowY) != _rendered)
            await Refresh();
    }

    async void OutBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var opts = new FolderPickerOpenOptions { Title = "Pick output folder", AllowMultiple = false };
        // Open the picker at the path already in the box (e.g. ~/PhotoLog-output), not home.
        if (await TryFolder(OutBox.Text) is { } start)
            opts.SuggestedStartLocation = start;
        var picked = await StorageProvider.OpenFolderPickerAsync(opts);
        if (picked.Count == 0) return;
        OutBox.Text = picked[0].TryGetLocalPath() ?? picked[0].Path.LocalPath;
        Persist();
    }

    /// Resolve a typed/expanded path to an IStorageFolder for the picker start location.
    async Task<IStorageFolder?> TryFolder(string? path)
    {
        var p = Core.Expand(path);
        if (p.Length == 0 || !Directory.Exists(p)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(p); }
        catch { return null; }
    }

    void OutBox_LostFocus(object? sender, RoutedEventArgs e) => Persist();

    /// Rebuild day sections (Google Photos style). _items is already newest-first.
    void RebuildDays()
    {
        _days.Clear();
        foreach (var g in _items.GroupBy(i => DateOnly.FromDateTime(i.Taken)))
        {
            var day = new DayGroup { Header = Core.DayHeader(g.Key), Day = g.Key };
            foreach (var p in g) day.Photos.Add(p);
            _days.Add(day);
        }
    }

    async Task Refresh()
    {
        if (_items.Count == 0) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Status.Text = $"{_items.Count} image(s) loaded — rendering previews…";
        await RenderThumbs([.. _items], OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop,
                           ShadowX, ShadowY, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        _rendered = (OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop, ShadowX, ShadowY);
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
            _days.Clear();
            UpdateCount();
            EmptyGrid.IsVisible = true;
            PhotoScroll.IsVisible = false;
            LibToolbar.IsVisible = false;
            Status.Text = Directory.Exists(folder)
                ? "No photos in that folder — try another path."
                : "Open a folder of photos to get started.";
            return;
        }

        _items.Clear();
        EmptyGrid.IsVisible = false;
        PhotoScroll.IsVisible = true;
        LibToolbar.IsVisible = true;
        foreach (var s in found)
            _items.Add(new PhotoItem { Name = s.Name, Path = s.Path, Taken = s.Taken, Tip = s.Name });
        RebuildDays();
        UpdateCount();
        Status.Text = $"Loading {_items.Count} photo(s)…";

        await RenderThumbs([.. _items], OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop,
                           ShadowX, ShadowY, ct);
        if (ct.IsCancellationRequested) return;
        _rendered = (OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop, ShadowX, ShadowY);
        Status.Text = $"{_items.Count} photo(s) loaded. Select some, then Apply.";
        Persist(); // remember last folder once a scan succeeds
    }

    static async Task RenderThumbs(IReadOnlyList<PhotoItem> items, DateOnly? date, TimeOnly? time, string addr,
                                   DropShadow drop, int dx, int dy, CancellationToken ct)
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
                        var (d, t, a, cap) = Core.Fields(item.Selected, date, time, addr, item.Caption);
                        var (jpeg, line) = Core.Thumb(item.Path, d, a, drop: drop, caption: cap, overrideTime: t,
                                                      shadowX: dx, shadowY: dy);
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

    bool HasOverride => OverrideDate is not null || OverrideTime is not null || !string.IsNullOrWhiteSpace(AddrBox.Text);

    /// Address/Caption should not keep keyboard focus after you click the library.
    void ReleaseTextFocus()
    {
        // Move focus off TextBox so further keys don't keep typing into Address.
        if (TopLevel.GetTopLevel(this)?.FocusManager is { } fm)
            fm.Focus(null);
        else
            Focus();
    }

    async void Cell_Click(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PhotoItem item) return;
        ReleaseTextFocus();
        item.Selected = !item.Selected;
        UpdateCount();
        ShowPreview(item);
        // scroll ribbon: keep the tapped cell in view (selection “line” is the purple bar on the cell)
        if (sender is Control c)
            c.BringIntoView();
        if (HasOverride || item.Caption.Length > 0) await RestampOne(item);
    }

    /// Google Photos: day header check selects all in that day, or deselects when already all-on.
    async void DayHeader_Click(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DayGroup day) return;
        e.Handled = true;
        ReleaseTextFocus();
        var select = !day.AllSelected; // none/partial → all; all → none
        foreach (var p in day.Photos) p.Selected = select;
        UpdateCount();
        if (sender is Control c)
            c.BringIntoView();
        if (HasOverride)
        {
            // only restamp this day — overrides paint only on selected
            await RenderThumbs(day.Photos.ToList(), OverrideDate, OverrideTime, AddrBox.Text ?? "",
                               CurrentDrop, ShadowX, ShadowY, CancellationToken.None);
            if (_preview is not null) ShowPreview(_preview);
        }
        else if (_preview is not null) ShowPreview(_preview);
    }

    void ShowPreview(PhotoItem item)
    {
        _preview = item;
        PreviewImage.Source = item.Thumb;
        var empty = item.Thumb is null;
        PreviewEmpty.IsVisible = empty;
        // Name only when a real preview is up — avoids stacking under the empty-state card
        PreviewName.IsVisible = !empty;
        PreviewName.Text = item.Name + (item.Selected ? "  [selected ✓]" : "  [not selected]");
        CaptionBox.Text = item.Caption;
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = true;
        SyncCaptionButtons();
    }

    void ClearPreview()
    {
        _preview = null;
        PreviewImage.Source = null;
        PreviewEmpty.IsVisible = true;
        PreviewName.IsVisible = false;
        PreviewName.Text = "";
        CaptionBox.Text = "";
        SaveOneBtn.IsEnabled = CaptionBox.IsEnabled = false;
        SyncCaptionButtons();
    }

    /// Caption (one) needs a preview; Caption selected needs any selection + model ready.
    void SyncCaptionButtons()
    {
        var ready = Caption.Ready;
        CaptionBtn.IsEnabled = ready && _preview is not null;
        CaptionAllBtn.IsEnabled = ready && _items.Any(i => i.Selected);
        if (AiSelectBtn is not null)
            AiSelectBtn.IsEnabled = ready && _items.Count > 0;
    }

    string AiCategory
    {
        get
        {
            var custom = AiCustomBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(custom)) return custom;
            if (AiCategoryBox?.SelectedItem is ComboBoxItem { Content: string s }) return s;
            return "Furniture";
        }
    }

    async void AiSelect_Click(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        if (!await EnsureModel()) return;
        var cat = AiCategory;
        AiSelectBtn.IsEnabled = CaptionBtn.IsEnabled = CaptionAllBtn.IsEnabled = false;
        AiProgress.IsVisible = true;
        AiProgress.IsIndeterminate = false;
        AiProgress.Value = 0;
        var hit = 0;
        try
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                AiStatus.Text = $"AI select “{cat}”: {i + 1} of {_items.Count} — {item.Name}…";
                AiProgress.Value = 100.0 * i / _items.Count;
                // Matches runs on the thread pool (same as Caption) so the UI does not freeze.
                var yes = await Caption.Matches(item.Path, cat, CancellationToken.None);
                item.Selected = yes;
                if (yes) hit++;
            }
            AiProgress.Value = 100;
            UpdateCount();
            AiStatus.Text = hit == 0
                ? $"AI select “{cat}”: no matches."
                : $"AI select “{cat}”: selected {hit} of {_items.Count}.";
            if (HasOverride) await Refresh();
        }
        catch (Exception ex)
        {
            AiStatus.Text = "AI select failed: " + ex.Message;
        }
        finally
        {
            AiProgress.IsVisible = false;
            SyncCaptionButtons();
        }
    }

    async void Caption_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        var cleaned = Caption.Tidy(CaptionBox.Text ?? "");
        if (cleaned != (CaptionBox.Text ?? "")) CaptionBox.Text = cleaned;
        if (_preview.Caption == cleaned) return;
        _preview.Caption = cleaned;
        await RestampOne(_preview);
    }

    async Task RestampOne(PhotoItem item)
    {
        await RenderThumbs([item], OverrideDate, OverrideTime, AddrBox.Text ?? "", CurrentDrop,
                           ShadowX, ShadowY, CancellationToken.None);
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
                var item = items[i];
                AiStatus.Text = $"Captioning {i + 1} of {items.Count} — {item.Name}…";
                // Describe always runs on the thread pool; keep UI free for paint/input.
                var (text, tps) = await Caption.Describe(item.Path, CancellationToken.None)
                    .ConfigureAwait(true);
                item.Caption = text;
                if (_preview == item) CaptionBox.Text = text;
                await RestampOne(item);
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
            SyncCaptionButtons();
        }
    }

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
            AiStatus.Text = "Model ready — pick a photo, or multi-select and Caption selected.";
            DownloadModelBtn.IsVisible = false;
            SyncCaptionButtons();
            if (_preview is not null) ShowPreview(_preview);
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
        ReleaseTextFocus();
        foreach (var i in _items) i.Selected = value;
        UpdateCount();
        if (HasOverride) await Refresh();
        else if (_preview is not null) ShowPreview(_preview);
    }

    void UpdateCount()
    {
        var sel = _items.Count(i => i.Selected);
        CountText.Text = $"{sel} of {_items.Count} selected";
        // selection mode = any selected → empty circles + day checks; browse = clean (GP Image #2)
        var mode = sel > 0;
        foreach (var i in _items) i.InSelectionMode = mode;
        foreach (var d in _days)
        {
            d.InSelectionMode = mode;
            d.RefreshSelection();
        }
        SyncCaptionButtons();
    }

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
        Persist();
        var date = OverrideDate;
        var time = OverrideTime;
        var addr = AddrBox.Text ?? "";
        var drop = CurrentDrop;
        var (dx, dy) = (ShadowX, ShadowY);
        ApplyBtn.IsEnabled = SaveOneBtn.IsEnabled = false;
        Result.Text = $"Writing {items.Count} photo(s) to {outDir}…";
        try
        {
            var names = await Task.Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var written = new List<string>(items.Count);
                foreach (var i in items)
                {
                    var (d, t, a, cap) = Core.Fields(forceSelected || i.Selected, date, time, addr, i.Caption);
                    var name = Core.ExportFileName(i.Name, cap, used);
                    Core.Export(i.Path, name, outDir, d, a, drop, cap, overrideTime: t, shadowX: dx, shadowY: dy);
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
