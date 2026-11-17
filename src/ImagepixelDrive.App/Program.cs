using System;
using System.Linq;
using Avalonia;

namespace ImagepixelDrive.App;

sealed class Program
{
    /// <summary>Starts without showing the window, as an autostart entry does.</summary>
    public const string MinimizedArgument = "--minimized";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        App.Instance = instance;
        App.StartMinimized = args.Contains(MinimizedArgument, StringComparer.OrdinalIgnoreCase);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
