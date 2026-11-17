using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ImagepixelDrive.App.Tests;

/// <summary>Saves pictures of windows that are rendered without being shown.</summary>
internal static class ScreenCapture
{
    /// <summary>Saves a picture of the window to artifacts/&lt;folder&gt;/&lt;name&gt;.png.</summary>
    public static void Save(Window window, string folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return;
        }

        var directory = Path.Combine(RepositoryRoot(), "artifacts", folder);
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ImagepixelDrive.slnx")))
            {
                return directory.FullName;
            }
        }

        return Path.GetTempPath();
    }
}
