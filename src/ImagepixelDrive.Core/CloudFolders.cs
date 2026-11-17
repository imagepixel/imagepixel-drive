namespace ImagepixelDrive.Core;

/// <summary>The cloud storage services whose local folders are recognised.</summary>
public enum CloudService
{
    OneDrive,
    GoogleDrive,
    ICloudDrive,
    Dropbox,
}

/// <summary>The folder a cloud storage service keeps in sync on this computer.</summary>
public sealed record CloudFolder(CloudService Service, string Name, string Path);

/// <summary>
/// Finds the folders of cloud storage services on this computer, so that they can be
/// offered as a place for a drive with one click. The encrypted files of the drive are
/// written into the local folder; the service of the user carries them into its cloud.
/// </summary>
public sealed class CloudFolderDetector
{
    private const string GoogleDriveVolumeLabel = "Google Drive";

    // the name of the personal folder inside the Google Drive volume depends on its language
    private static readonly string[] GoogleDriveRootFolders = ["Meine Ablage", "My Drive"];

    /// <summary>Reads an environment variable. Replaceable for tests.</summary>
    public Func<string, string?> EnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The profile folder of the current user. Replaceable for tests.</summary>
    public string UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The volumes of the computer with their labels. Replaceable for tests.</summary>
    public Func<IEnumerable<(string Root, string Label)>> Volumes { get; init; } = SystemVolumes;

    /// <summary>
    /// Whether cloud storage is offered on this system. Linux is left out: the services
    /// have no program there that keeps a folder in sync.
    /// </summary>
    public static bool IsSupported => !OperatingSystem.IsLinux();

    /// <summary>Finds the cloud folders of the current user. None where they are not offered.</summary>
    public static IReadOnlyList<CloudFolder> Detect() => IsSupported ? new CloudFolderDetector().Find() : [];

    public IReadOnlyList<CloudFolder> Find()
    {
        var found = new List<CloudFolder>();

        void Add(CloudService service, string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            var full = Normalize(path);
            if (!found.Any(f => string.Equals(f.Path, full, FolderRules.PathComparison)))
            {
                found.Add(new CloudFolder(service, name, full));
            }
        }

        // OneDrive sets these variables: the personal account, a work account, and
        // "OneDrive" for whichever was set up last.
        Add(CloudService.OneDrive, "OneDrive", EnvironmentVariable("OneDriveConsumer"));
        foreach (var variable in new[] { "OneDriveCommercial", "OneDrive" })
        {
            if (EnvironmentVariable(variable) is { Length: > 0 } path)
            {
                Add(CloudService.OneDrive, FolderName(path, "OneDrive"), path);
            }
        }

        // Google Drive for desktop shows the account as a volume of its own
        foreach (var (root, label) in Volumes())
        {
            if (!string.Equals(label, GoogleDriveVolumeLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var personal = GoogleDriveRootFolders
                .Select(name => Path.Combine(root, name))
                .FirstOrDefault(Directory.Exists);
            Add(CloudService.GoogleDrive, "Google Drive", personal ?? root);
        }

        // Google Drive can also mirror the files into the profile folder
        foreach (var name in GoogleDriveRootFolders.Append("Google Drive"))
        {
            Add(CloudService.GoogleDrive, "Google Drive", Path.Combine(UserProfile, name));
        }

        Add(CloudService.ICloudDrive, "iCloud Drive", Path.Combine(UserProfile, "iCloudDrive"));
        Add(CloudService.Dropbox, "Dropbox", Path.Combine(UserProfile, "Dropbox"));

        // macOS keeps cloud storage in fixed places below the home folder
        var cloudStorage = Path.Combine(UserProfile, "Library", "CloudStorage");
        if (Directory.Exists(cloudStorage))
        {
            // sorted, because the file system of macOS lists folders in no particular order
            foreach (var directory in Directory.GetDirectories(cloudStorage).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
                {
                    Add(CloudService.OneDrive, name, directory);
                }
                else if (name.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase))
                {
                    Add(CloudService.Dropbox, "Dropbox", directory);
                }
                else if (name.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase))
                {
                    // as on Windows: the personal folder, without the drives shared by others
                    var personal = GoogleDriveRootFolders
                        .Select(folder => Path.Combine(directory, folder))
                        .FirstOrDefault(Directory.Exists);
                    Add(CloudService.GoogleDrive, "Google Drive", personal ?? directory);
                }
            }
        }

        Add(
            CloudService.ICloudDrive,
            "iCloud Drive",
            Path.Combine(UserProfile, "Library", "Mobile Documents", "com~apple~CloudDocs"));

        return found;
    }

    /// <summary>Returns the cloud folder that is or contains the given path, if any.</summary>
    public static CloudFolder? Containing(string path, IEnumerable<CloudFolder> folders)
    {
        var full = Normalize(path);
        return folders.FirstOrDefault(folder =>
            string.Equals(full, folder.Path, FolderRules.PathComparison)
            || full.StartsWith(folder.Path + Path.DirectorySeparatorChar, FolderRules.PathComparison));
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);

        // the root of a volume keeps its separator: "G:\" must not become "G:"
        return trimmed.EndsWith(Path.VolumeSeparatorChar) ? full : trimmed;
    }

    private static string FolderName(string path, string fallback) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : fallback;

    private static IEnumerable<(string Root, string Label)> SystemVolumes()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string label;
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                label = drive.VolumeLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return (drive.RootDirectory.FullName, label);
        }
    }
}
