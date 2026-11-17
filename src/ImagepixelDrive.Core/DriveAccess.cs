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

    internal DriveAccess(DriveStorage storage, string obscuredPassword)
    {
        Storage = storage;
        ObscuredPassword = obscuredPassword;
    }

    public DriveStorage Storage { get; }

    /// <summary>
    /// The password in the reversible notation of rclone. It is as secret as the
    /// password itself.
    /// </summary>
    /// <remarks>
    /// rclone writes the same password differently every time it is asked, and it
    /// names the cache folder of a drive after everything it is told about the drive
    /// through its environment, this text included. A drive that is shown again must
    /// therefore be given the very text it was given before. Otherwise rclone starts
    /// with an empty cache, and files that were written but not stored yet when the
    /// drive was closed stay behind in the old one.
    /// </remarks>
    internal string ObscuredPassword { get; }

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
        environment[Key + "PASSWORD"] = ObscuredPassword;

        // give up quickly when the storage cannot be reached instead of leaving the
        // user waiting; whoever started rclone tries again later
        environment["RCLONE_CONTIMEOUT"] = "10s";
        environment["RCLONE_TIMEOUT"] = "1m";
        environment["RCLONE_RETRIES"] = "1";
        environment["RCLONE_LOW_LEVEL_RETRIES"] = "3";
        environment["RCLONE_COLOR"] = "NEVER";
    }
}
