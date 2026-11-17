using System.Security;

namespace ImagepixelDrive.Core;

/// <summary>
/// Starts the application when the user logs in, so that the drive is there without
/// the user opening the application. macOS has this; on Windows the setup program
/// takes care of it instead.
/// </summary>
public interface ILoginItem
{
    /// <summary>The file that tells the system to start the application.</summary>
    string FilePath { get; }

    bool IsEnabled { get; }

    /// <summary>Whether a program lies where it will still be found at the next login.</summary>
    bool IsPermanentLocation(string executablePath);

    /// <summary>Makes the system start the given program, without its window, at every login.</summary>
    void Enable(string executablePath, string hiddenArgument);

    /// <summary>
    /// Points the system to the program again if the application was moved since
    /// starting at login was turned on. Does nothing while it is turned off.
    /// </summary>
    void Refresh(string executablePath, string hiddenArgument);

    void Disable();
}

public static class LoginItem
{
    /// <summary>Returns how this system starts programs at login, or null if the application leaves that to its setup program.</summary>
    public static ILoginItem? Create() => OperatingSystem.IsMacOS() ? new MacLoginItem() : null;
}

/// <summary>macOS: a launch agent in the user's Library folder.</summary>
public sealed class MacLoginItem(string? launchAgentsDirectory = null) : ILoginItem
{
    private const string Label = "de.imagepixel.drive";

    private readonly string _directory = launchAgentsDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string FilePath => Path.Combine(_directory, Label + ".plist");

    public bool IsEnabled => File.Exists(FilePath);

    /// <summary>
    /// Not while the application is opened from its disk image, or from the temporary
    /// copy macOS runs an application from that was not moved after its download.
    /// </summary>
    public bool IsPermanentLocation(string executablePath) =>
        !executablePath.StartsWith("/Volumes/", StringComparison.Ordinal)
        && !executablePath.Contains("/AppTranslocation/", StringComparison.Ordinal);

    public void Enable(string executablePath, string hiddenArgument)
    {
        var content = Content(executablePath, hiddenArgument).ReplaceLineEndings("\n");
        if (!IsEnabled || File.ReadAllText(FilePath) != content)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, content);
        }
    }

    public void Refresh(string executablePath, string hiddenArgument)
    {
        if (IsEnabled && IsPermanentLocation(executablePath))
        {
            Enable(executablePath, hiddenArgument);
        }
    }

    public void Disable()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    private static string Content(string executablePath, string hiddenArgument) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executablePath)}</string>
                <string>{SecurityElement.Escape(hiddenArgument)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;
}
