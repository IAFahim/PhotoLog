using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace PhotoLog.Avalonia;

/// One cell in the thumbnail grid. Thumb/Caption arrive later (async render), Selected toggles on click.
public class PhotoItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    Bitmap? _thumb;
    string _caption = "";
    bool _selected;

    public Bitmap? Thumb { get => _thumb; set => Set(ref _thumb, value); }
    public string Caption { get => _caption; set => Set(ref _caption, value); }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
