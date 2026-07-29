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
        // headless style preview: src dest [light|heavy|off] [dx] [dy]
        if (args.Length >= 3 && args[0] == "--stamp-sample")
        {
            var drop = args.Length > 3 && Enum.TryParse<DropShadow>(args[3], true, out var d) ? d : DropShadow.Light;
            var (px, py) = Core.DropOffset(drop);
            var dx = args.Length > 4 && int.TryParse(args[4], out var x) ? x : px;
            var dy = args.Length > 5 && int.TryParse(args[5], out var y) ? y : py;
            System.IO.File.WriteAllBytes(args[2], Core.Thumb(args[1], null,
                "1521 Meander Rd\nTimmonsville SC 29161\nUnited States", 1280, drop,
                shadowX: dx, shadowY: dy).Jpeg);
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
