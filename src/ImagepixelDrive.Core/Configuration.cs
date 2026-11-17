using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelDrive.Core;

/// <summary>The kinds of places a drive can be stored at.</summary>
public enum StorageKind
{
    /// <summary>A bucket provided by Imagepixel. The customer only enters the data they were given.</summary>
    ImagepixelStorage,

    /// <summary>
    /// A folder the customer chooses: on a disk, or inside the folder of a cloud
    /// service such as OneDrive, which then carries the encrypted files into its cloud.
    /// Imagepixel has no part in such a place.
    /// </summary>
    LocalFolder,
}

/// <summary>Where the drive is stored. Secrets are kept separately in an <see cref="ISecretStore"/>.</summary>
public sealed class StorageSettings
{
    public StorageKind Kind { get; set; }

    /// <summary>Bucket name, for <see cref="StorageKind.ImagepixelStorage"/>.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>Region of the bucket, for <see cref="StorageKind.ImagepixelStorage"/>.</summary>
    public string Region { get; set; } = ImagepixelStorage.DefaultRegion;

    /// <summary>The folder in the bucket that holds the drive.</summary>
    public string Prefix { get; set; } = ImagepixelStorage.DefaultPrefix;

    public string AccessKeyId { get; set; } = "";

    /// <summary>Folder that holds the encrypted files, for <see cref="StorageKind.LocalFolder"/>.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>
    /// Address of the service of Imagepixel that tells how much storage is booked and
    /// used. Empty as long as the application has not been told about one.
    /// </summary>
    public string ApiUrl { get; set; } = "";

    [JsonIgnore]
    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    /// <summary>
    /// The address of the storage service if it is not the one of Imagepixel Storage.
    /// Empty in normal use; the tests point it at a storage on this computer.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The place in the form the core works with.</summary>
    public DriveStorage ToStorage(string? secretAccessKey)
    {
        if (!IsImagepixel)
        {
            return new FolderStorage(LocalPath);
        }

        return string.IsNullOrWhiteSpace(Endpoint)
            ? ImagepixelStorage.Area(Bucket, Prefix, AccessKeyId, secretAccessKey ?? "", Region)
            : new S3Storage(Endpoint, Region, Bucket, Prefix, AccessKeyId, secretAccessKey ?? "", "Other");
    }
}

/// <summary>A folder of the drive the customer keeps on this computer as well.</summary>
public sealed class OfflineFolderSettings
{
    /// <summary>The folder in the drive, with forward slashes.</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>The folder on this computer.</summary>
    public string LocalPath { get; set; } = "";
}

/// <summary>The settings of the application. Secrets are not part of it.</summary>
public sealed class AppConfig
{
    public StorageSettings? Storage { get; set; }

    public List<OfflineFolderSettings> OfflineFolders { get; set; } = [];

    /// <summary>The drive letter used last, to show the drive at the same place again. Windows only.</summary>
    public string? DriveLetter { get; set; }

    /// <summary>
    /// The version of the notes on use and liability the user confirmed at the first
    /// start, or null if none was confirmed yet. A newer version is asked for once more.
    /// </summary>
    public string? TermsAccepted { get; set; }

    /// <summary>When <see cref="TermsAccepted"/> was confirmed.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }
}

/// <summary>Loads and saves <see cref="AppConfig"/> as a JSON file in the user's profile.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    /// <summary>The folder that holds the settings of the current user.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("IMAGEPIXEL_DRIVE_DATA") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImagepixelDrive");

    public string Directory { get; }

    private string FilePath => System.IO.Path.Combine(Directory, "config.json");

    public AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppConfig();
        }

        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Write to a temporary file first so that a crash never leaves a damaged file behind.
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>Rules about how the folders of a drive may relate to each other.</summary>
public static class FolderRules
{
    /// <summary>Returns whether one folder is the other or lies inside it.</summary>
    public static bool Overlap(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var a = WithSeparator(first);
        var b = WithSeparator(second);
        return a.StartsWith(b, PathComparison) || b.StartsWith(a, PathComparison);
    }

    /// <summary>Returns whether a path is the folder itself or lies inside it.</summary>
    public static bool IsInside(string path, string folder) =>
        !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(folder)
        && WithSeparator(path).StartsWith(WithSeparator(folder), PathComparison);

    /// <summary>
    /// How paths are compared on this computer: Linux tells "Daten" and "daten" apart,
    /// Windows and macOS take them for the same folder.
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Compares paths as <see cref="PathComparison"/> does.</summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // "C:\Daten" must not match "C:\Daten alt", so both paths end with a separator.
    // The top of a volume, "T:\", has one already.
    private static string WithSeparator(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
