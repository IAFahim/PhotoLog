using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PhotoLog.Avalonia;

/// One cell in the thumbnail grid. Thumb/Caption arrive later (async render), Selected toggles on click.
public class PhotoItem : INotifyPropertyChanged
{
    /// Inset when selected so the photo shrinks and the plate behind reads larger.
    const double SelectInset = 10;

    public required string Name { get; init; }
    public required string Path { get; init; }
    /// EXIF/mtime taken time — drives sort order and day-section headers.
    public required DateTime Taken { get; init; }

    Bitmap? _thumb;
    string _tip = "";
    string _caption = "";
    bool _selected;
    bool _inSelectionMode; // any photo in the library is selected → show empty circles
    bool _activePreview; // currently in the live preview (filmstrip ring)
    double _aspect = 1.0; // square placeholder until thumb decodes

    public Bitmap? Thumb
    {
        get => _thumb;
        set
        {
            if (!Set(ref _thumb, value) || value is null) return;
            Aspect = Math.Max(1.0, value.PixelSize.Width) / Math.Max(1.0, value.PixelSize.Height);
        }
    }

    /// Hover text: filename + the date being stamped.
    public string Tip { get => _tip; set => Set(ref _tip, value); }
    /// AI caption, stamped as the last line(s). Empty = no caption line. User-editable.
    public string Caption { get => _caption; set => Set(ref _caption, value); }

    /// Selection chip on the thumb. Raises companion bind props so XAML never needs `!`.
    public bool Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(NotSelected));
            Raise(nameof(ShowEmptyCheck));
            Raise(nameof(ThumbMargin));
        }
    }

    /// Library has any selection → show empty circles on unselected thumbs (GP selection mode).
    public bool InSelectionMode
    {
        get => _inSelectionMode;
        set
        {
            if (!Set(ref _inSelectionMode, value)) return;
            Raise(nameof(ShowEmptyCheck));
        }
    }

    /// Inverse of <see cref="Selected"/> for IsVisible.
    public bool NotSelected => !_selected;
    /// Empty circle: only while selecting, and only on unselected thumbs (browse mode = clean).
    public bool ShowEmptyCheck => _inSelectionMode && !_selected;
    /// Shrink inset when selected — full bleed when not (the plate behind fills the leftover).
    public Thickness ThumbMargin => _selected ? new Thickness(SelectInset) : default;

    /// True when this photo is the live preview focus (bottom filmstrip highlight).
    public bool IsActivePreview
    {
        get => _activePreview;
        set => Set(ref _activePreview, value);
    }

    /// Photo width ÷ height — the justified grid shapes the cell to match exactly.
    public double Aspect { get => _aspect; private set => Set(ref _aspect, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// One day bucket in the library view (header + photos for that calendar day).
/// Header checkbox mirrors Google Photos: hidden in browse mode; empty / partial (−) / all (✓) when selecting.
public sealed class DayGroup : INotifyPropertyChanged
{
    public required string Header { get; init; }
    public required DateOnly Day { get; init; }
    public ObservableCollection<PhotoItem> Photos { get; } = [];

    bool _allSelected;
    bool _anySelected;
    bool _partialSelected;
    bool _inSelectionMode;

    /// Library-wide selection mode (any photo selected somewhere).
    public bool InSelectionMode
    {
        get => _inSelectionMode;
        set
        {
            if (!Set(ref _inSelectionMode, value)) return;
            RaiseChrome();
        }
    }

    /// Every photo in this day is selected (filled day check).
    public bool AllSelected { get => _allSelected; private set => Set(ref _allSelected, value); }
    /// At least one photo this day is selected.
    public bool AnySelected { get => _anySelected; private set => Set(ref _anySelected, value); }
    /// Some but not all selected (minus day check).
    public bool PartialSelected { get => _partialSelected; private set => Set(ref _partialSelected, value); }
    /// No selection this day.
    public bool NoneSelected => !AnySelected;

    // Day-header chips only while the library is in selection mode (Image #2 = browse = clean header).
    public bool ShowEmptyDayCheck => _inSelectionMode && !AnySelected;
    public bool ShowPartialDayCheck => _inSelectionMode && PartialSelected;
    public bool ShowAllDayCheck => _inSelectionMode && AllSelected;

    /// Recompute checkbox state from <see cref="Photos"/>. Call after any select toggle.
    public void RefreshSelection()
    {
        var n = Photos.Count;
        var sel = 0;
        foreach (var p in Photos)
            if (p.Selected) sel++;
        AnySelected = sel > 0;
        AllSelected = n > 0 && sel == n;
        PartialSelected = sel > 0 && sel < n;
        Raise(nameof(NoneSelected));
        RaiseChrome();
    }

    void RaiseChrome()
    {
        Raise(nameof(ShowEmptyDayCheck));
        Raise(nameof(ShowPartialDayCheck));
        Raise(nameof(ShowAllDayCheck));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
