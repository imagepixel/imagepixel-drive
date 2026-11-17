namespace ImagepixelDrive.Core;

/// <summary>
/// Everything rclone needs to reach a drive: the place of the encrypted files and the
/// password that encrypts them. It is handed to rclone through the environment of its
/// process, so that nothing secret appears on a command line or in a file.
/// </summary>
public sealed class DriveAccess
{
    /// <summary>The name rclone knows the drive by: the storage with the encryption on top.</summary>
    internal const string DriveRemote = "ipdrive";

    private readonly string _obscuredPassword;

    internal DriveAccess(DriveStorage storage, string obscuredPassword)
    {
        Storage = storage;
        _obscuredPassword = obscuredPassword;
    }

    public DriveStorage Storage { get; }

    /// <summary>
    /// A path in the drive in the notation of rclone. Paths use forward slashes and
    /// start at the top of the drive; an empty path is the top itself.
    /// </summary>
    public static string Remote(string path = "") => $"{DriveRemote}:{Normalize(path)}";

    /// <summary>The place of the encrypted files as rclone sees it without the password.</summary>
    public string EncryptedLocation => Storage.Location;

    /// <summary>Brings a path of the drive into the notation of rclone.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    public void ApplyTo(IDictionary<string, string?> environment)
    {
        // settings of an rclone the user may have set up for themself must not leak in
        foreach (var name in environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(name);
        }

        // no configuration file: the description of the drive lives in memory only
        environment["RCLONE_CONFIG"] = "";

        Storage.ApplyTo(environment);

        // The encryption of rclone with its standard settings and one password. Anyone
        // who has the password and the access to the storage can read the drive with
        // rclone as it is released, without this application.
        const string Key = "RCLONE_CONFIG_IPDRIVE_";
        environment[Key + "TYPE"] = "crypt";
        environment[Key + "REMOTE"] = Storage.Location;
        environment[Key + "PASSWORD"] = _obscuredPassword;

        // give up quickly when the storage cannot be reached instead of leaving the
        // user waiting; whoever started rclone tries again later
        environment["RCLONE_CONTIMEOUT"] = "10s";
        environment["RCLONE_TIMEOUT"] = "1m";
        environment["RCLONE_RETRIES"] = "1";
        environment["RCLONE_LOW_LEVEL_RETRIES"] = "3";
        environment["RCLONE_COLOR"] = "NEVER";
    }
}
