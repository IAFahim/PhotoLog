using Avalonia;
using System;
using System.Linq;

namespace PhotoLog.Avalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selfcheck")) return Core.SelfCheck(); // headless: no window needed
        if (args.Length >= 3 && args[0] == "--stamp-sample") // headless style preview: src dest [dx dy]
        {
            System.IO.File.WriteAllBytes(args[2], Core.Thumb(args[1], null,
                "1521 Meander Rd\nTimmonsville SC 29161\nUnited States", 1280,
                args.Length > 4 ? int.Parse(args[3]) : Core.DefaultShadowX,
                args.Length > 4 ? int.Parse(args[4]) : Core.DefaultShadowY).Jpeg);
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
