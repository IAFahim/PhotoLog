using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Linq;

namespace PhotoLog.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply saved theme before the window paints (default Dark).
        Settings.ApplyTheme(Settings.Load().Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault(a => !a.StartsWith('-')));
        }

        base.OnFrameworkInitializationCompleted();
    }
}