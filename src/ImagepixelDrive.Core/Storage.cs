namespace ImagepixelDrive.Core;

/// <summary>
/// Imagepixel Storage: S3 compatible buckets that Imagepixel sets up for its customers.
/// They are hosted by Wasabi, which is why the endpoints carry its name.
/// </summary>
public static class ImagepixelStorage
{
    /// <summary>The region buckets are created in unless told otherwise.</summary>
    public const string DefaultRegion = "eu-central-2";

    /// <summary>The folder of a bucket a drive lies in unless told otherwise.</summary>
    public const string DefaultPrefix = "drive";

    /// <summary>The host name of the storage service for a region.</summary>
    public static string Endpoint(string region) =>
        string.IsNullOrWhiteSpace(region) || region == "us-east-1"
            ? "s3.wasabisys.com"
            : $"s3.{region.Trim()}.wasabisys.com";

    /// <summary>The area of a bucket of Imagepixel Storage that holds a drive.</summary>
    public static S3Storage Area(string bucket, string prefix, string accessKeyId, string secretAccessKey, string? region = null)
    {
        var where = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        return new S3Storage(Endpoint(where), where, bucket, prefix, accessKeyId, secretAccessKey);
    }
}

/// <summary>
/// The place that holds the encrypted files of a drive. Everything stored there is
/// encrypted on this computer first.
/// </summary>
public abstract record DriveStorage
{
    /// <summary>The name rclone knows the place by, without encryption.</summary>
    internal const string StoreRemote = "ipstore";

    /// <summary>The location the encryption is put on top of, in the notation of rclone.</summary>
    internal abstract string Location { get; }

    /// <summary>Describes the place to rclone through its environment.</summary>
    internal abstract void ApplyTo(IDictionary<string, string?> environment);

    /// <summary>
    /// A short name for the place that stays the same between runs and differs from
    /// place to place. What the application keeps on this computer for a drive, such
    /// as files waiting for their upload, is filed under it, so that it never gets
    /// mixed up with another drive.
    /// </summary>
    public string Id
    {
        get
        {
            var text = this is S3Storage s3
                ? $"s3|{s3.Endpoint}|{s3.Bucket}|{s3.Prefix.Trim('/')}".ToLowerInvariant()
                : "folder|" + (OperatingSystem.IsLinux() ? Location : Location.ToUpperInvariant());
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }
}

/// <summary>An area of an S3 compatible bucket.</summary>
/// <param name="Endpoint">The host name of the service, or its address starting with http:// or https://.</param>
/// <param name="Region">The region of the bucket.</param>
/// <param name="Bucket">The bucket.</param>
/// <param name="Prefix">The folder in the bucket that belongs to the drive. Nothing outside of it is touched.</param>
/// <param name="AccessKeyId">The access key.</param>
/// <param name="SecretAccessKey">The secret that belongs to the access key.</param>
/// <param name="Provider">The kind of service, as rclone names it.</param>
public sealed record S3Storage(
    string Endpoint,
    string Region,
    string Bucket,
    string Prefix,
    string AccessKeyId,
    string SecretAccessKey,
    string Provider = "Wasabi") : DriveStorage
{
    internal override string Location
    {
        get
        {
            var prefix = Prefix.Trim('/');
            return prefix.Length == 0 ? $"{StoreRemote}:{Bucket}" : $"{StoreRemote}:{Bucket}/{prefix}";
        }
    }

    internal override void ApplyTo(IDictionary<string, string?> environment)
    {
        const string Key = "RCLONE_CONFIG_IPSTORE_";
        environment[Key + "TYPE"] = "s3";
        environment[Key + "PROVIDER"] = Provider;
        environment[Key + "ENDPOINT"] = Endpoint;
        environment[Key + "REGION"] = Region;
        environment[Key + "ACCESS_KEY_ID"] = AccessKeyId;
        environment[Key + "SECRET_ACCESS_KEY"] = SecretAccessKey;

        // the access key of a customer may not create buckets or ask about them
        environment[Key + "NO_CHECK_BUCKET"] = "true";
    }

    // keeps the secret out of logs and debugger views
    public override string ToString() => $"{Endpoint}/{Bucket}/{Prefix.Trim('/')}";
}

/// <summary>A folder, for example on a USB drive. Also used by the tests.</summary>
/// <param name="Path">The folder that receives the encrypted files.</param>
public sealed record FolderStorage(string Path) : DriveStorage
{
    internal override string Location => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));

    internal override void ApplyTo(IDictionary<string, string?> environment)
    {
        // a folder needs no description
    }
}
