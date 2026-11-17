using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelDrive.Core;

/// <summary>
/// A single line of text that carries everything a customer needs to connect to their
/// Imagepixel Storage. It is meant to be copied and pasted, not to be secret on its
/// own terms: it contains the access key in readable form.
/// </summary>
/// <param name="Bucket">The bucket of the customer.</param>
/// <param name="AccessKeyId">The access key of the customer.</param>
/// <param name="SecretAccessKey">The secret that belongs to the access key.</param>
/// <param name="Region">The region the bucket is in.</param>
/// <param name="Prefix">The folder in the bucket that holds the drive, if it is not the usual one.</param>
/// <param name="ApiUrl">The address of the service that tells how much storage is booked and used, if there is one.</param>
/// <param name="ApiToken">The token to send to that service.</param>
public sealed record SetupCode(
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    string Region,
    string? Prefix = null,
    string? ApiUrl = null,
    string? ApiToken = null)
{
    /// <summary>What a setup code for Imagepixel Drive starts with.</summary>
    public const string DrivePrefix = "IPD1-";

    // A code made for Imagepixel Backup names a bucket and its key in the same way.
    // It is accepted, so that a storage handed out before can be used for a drive.
    private const string BackupPrefix = "IPB1-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(Bucket, AccessKeyId, SecretAccessKey, Region, Prefix, ApiUrl, ApiToken), JsonOptions);
        return DrivePrefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? text, out SetupCode? code)
    {
        code = null;
        var trimmed = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        var prefix = new[] { DrivePrefix, BackupPrefix }.FirstOrDefault(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        try
        {
            var base64 = trimmed[prefix.Length..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Bucket)
                || string.IsNullOrWhiteSpace(payload.AccessKeyId)
                || string.IsNullOrWhiteSpace(payload.SecretAccessKey))
            {
                return false;
            }

            code = new SetupCode(
                payload.Bucket,
                payload.AccessKeyId,
                payload.SecretAccessKey,
                string.IsNullOrWhiteSpace(payload.Region) ? ImagepixelStorage.DefaultRegion : payload.Region,
                string.IsNullOrWhiteSpace(payload.Prefix) ? null : payload.Prefix,
                string.IsNullOrWhiteSpace(payload.ApiUrl) ? null : payload.ApiUrl,
                string.IsNullOrWhiteSpace(payload.ApiToken) ? null : payload.ApiToken);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("b")] string Bucket,
        [property: JsonPropertyName("k")] string AccessKeyId,
        [property: JsonPropertyName("s")] string SecretAccessKey,
        [property: JsonPropertyName("r")] string? Region,
        [property: JsonPropertyName("p")] string? Prefix = null,
        [property: JsonPropertyName("a")] string? ApiUrl = null,
        [property: JsonPropertyName("t")] string? ApiToken = null);
}
