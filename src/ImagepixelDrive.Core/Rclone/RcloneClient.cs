using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelDrive.Core.Rclone;

/// <summary>What the place of a drive looks like to someone who holds a password.</summary>
public enum DriveState
{
    /// <summary>The drive is there and the password opens it.</summary>
    Ready,

    /// <summary>Nothing is stored at the place yet. A drive can be created there.</summary>
    Empty,

    /// <summary>Files are stored at the place, but the password does not open them.</summary>
    WrongPassword,

    /// <summary>The place cannot be reached, for example without a network connection.</summary>
    Unreachable,
}

/// <summary>A file or folder of the drive.</summary>
public sealed record RemoteEntry(
    [property: JsonPropertyName("Path")] string Path,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("ModTime")] DateTimeOffset ModTime,
    [property: JsonPropertyName("IsDir")] bool IsDirectory);

/// <summary>Runs rclone for one drive and translates its output.</summary>
public sealed class RcloneClient
{
    /// <summary>
    /// A small file at the top of every drive. It tells that the place holds a drive
    /// and, because its name is encrypted like every other, whether a password fits.
    /// </summary>
    public const string CheckFileName = ".imagepixel-drive";

    private const string CheckFileContent = "Imagepixel Drive 1\n";

    // exit codes of rclone
    private const int ExitDirectoryNotFound = 3;
    private const int ExitFileNotFound = 4;

    private RcloneClient(string rclonePath, DriveAccess access)
    {
        RclonePath = rclonePath;
        Access = access;
    }

    public string RclonePath { get; }

    public DriveAccess Access { get; }

    /// <summary>
    /// Finds the rclone program: next to the application, or the one fetched by
    /// get-rclone.ps1 or get-rclone.sh when running from the source tree.
    /// </summary>
    public static string? FindRclone()
    {
        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

        // in the source tree the programs for macOS carry the chip in their name
        var fetched = OperatingSystem.IsMacOS()
            ? "rclone-darwin-" + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64")
            : name;

        if (Environment.GetEnvironmentVariable("IMAGEPIXEL_DRIVE_RCLONE") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(directory.FullName, name), Path.Combine(directory.FullName, "bin", fetched) })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Prepares access to a drive. Nothing is contacted yet.</summary>
    /// <param name="rclonePath">The rclone program.</param>
    /// <param name="storage">The place of the encrypted files.</param>
    /// <param name="password">The password that encrypts the drive.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    public static async Task<RcloneClient> CreateAsync(
        string rclonePath,
        DriveStorage storage,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("A drive needs a password.", nameof(password));
        }

        if (password.Contains('\n') || password.Contains('\r'))
        {
            throw new ArgumentException("A password cannot contain line breaks.", nameof(password));
        }

        // rclone expects passwords in its own reversible notation; it is asked to
        // produce it so that the password is passed on exactly as rclone would do it
        var startInfo = NewStartInfo(rclonePath, ["obscure", "-"]);
        ClearSettings(startInfo.Environment);
        var result = await RunAsync(startInfo, password + "\n", null, cancellationToken);
        result.ThrowIfFailed();

        return new RcloneClient(rclonePath, new DriveAccess(storage, result.Output.Trim()));
    }

    /// <summary>Tells whether there is a drive at the place and whether the password opens it.</summary>
    public async Task<DriveState> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var encrypted = await RunAsync(["lsjson", Access.EncryptedLocation, "--max-depth", "1"], null, null, cancellationToken);
        if (encrypted.ExitCode == ExitDirectoryNotFound)
        {
            return DriveState.Empty;
        }

        if (encrypted.ExitCode != 0)
        {
            return DriveState.Unreachable;
        }

        if (ParseEntries(encrypted.Output).Count == 0)
        {
            return DriveState.Empty;
        }

        var check = await RunAsync(["cat", DriveAccess.Remote(CheckFileName)], null, null, cancellationToken);
        if (check.ExitCode == 0)
        {
            return DriveState.Ready;
        }

        // A drive filled by rclone alone has no check file. Names that the password
        // does not decrypt are left out of a listing, so anything listed proves it right.
        var listed = await RunAsync(["lsjson", DriveAccess.Remote(), "--max-depth", "1"], null, null, cancellationToken);
        if (listed.ExitCode != 0)
        {
            return DriveState.Unreachable;
        }

        return ParseEntries(listed.Output).Count > 0 ? DriveState.Ready : DriveState.WrongPassword;
    }

    /// <summary>Marks the place as a drive that this password opens. Safe to repeat.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["rcat", DriveAccess.Remote(CheckFileName)], CheckFileContent, null, cancellationToken);
        result.ThrowIfFailed();
    }

    /// <summary>Whether the storage answers at the moment.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["lsjson", Access.EncryptedLocation, "--max-depth", "1", "--contimeout", "5s", "--timeout", "15s", "--low-level-retries", "1"],
            null,
            null,
            cancellationToken);
        return result.ExitCode is 0 or ExitDirectoryNotFound;
    }

    /// <summary>Lists a folder of the drive with the names as the user sees them.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path = "", bool recursive = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["lsjson", DriveAccess.Remote(path)];
        if (recursive)
        {
            arguments.Add("--recursive");
        }

        var result = await RunAsync(arguments, null, null, cancellationToken);
        if (result.ExitCode == ExitDirectoryNotFound)
        {
            return [];
        }

        result.ThrowIfFailed();
        return ParseEntries(result.Output);
    }

    /// <summary>Lists what the storage really holds: encrypted names and sizes.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListEncryptedAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["lsjson", Access.EncryptedLocation, "--recursive", "--files-only"], null, null, cancellationToken);
        if (result.ExitCode == ExitDirectoryNotFound)
        {
            return [];
        }

        result.ThrowIfFailed();
        return ParseEntries(result.Output);
    }

    /// <summary>Whether a file exists in the drive.</summary>
    public async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["lsjson", DriveAccess.Remote(path), "--stat"], null, null, cancellationToken);
        if (result.ExitCode is ExitDirectoryNotFound or ExitFileNotFound)
        {
            return false;
        }

        result.ThrowIfFailed();

        // A storage made of buckets has no folders of its own: it answers for any
        // path that is not a file as if it were an empty folder.
        return !string.IsNullOrWhiteSpace(result.Output)
            && JsonSerializer.Deserialize<RemoteEntry>(result.Output) is { IsDirectory: false };
    }

    /// <summary>Copies a file of this computer into the drive, replacing what is there.</summary>
    public async Task UploadAsync(string localFile, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["copyto", Path.GetFullPath(localFile), DriveAccess.Remote(path)], null, null, cancellationToken);
        result.ThrowIfFailed();
    }

    /// <summary>Creates a folder in the drive, with the folders that lead to it.</summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["mkdir", DriveAccess.Remote(path)], null, null, cancellationToken);
        result.ThrowIfFailed();
    }

    /// <summary>Reads a file of the drive as text.</summary>
    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["cat", DriveAccess.Remote(path)], null, null, cancellationToken);
        result.ThrowIfFailed();
        return result.Output;
    }

    /// <summary>
    /// Reads a small file that lies in the bucket next to the drive, not in it, as
    /// text. Null if the drive is not stored in a bucket, the file is missing or the
    /// storage does not answer.
    /// </summary>
    /// <param name="key">The key of the file in the bucket.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    public async Task<string?> ReadBucketTextAsync(string key, CancellationToken cancellationToken = default)
    {
        if (Access.Storage is not S3Storage storage)
        {
            return null;
        }

        var result = await RunAsync(
            [
                "cat", $"{DriveStorage.StoreRemote}:{storage.Bucket}/{key.TrimStart('/')}",
                "--contimeout", "5s", "--timeout", "15s", "--low-level-retries", "1",
            ],
            null,
            null,
            cancellationToken);
        return result.ExitCode == 0 && result.Output.Length > 0 ? result.Output : null;
    }

    internal ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments, string? program = null)
    {
        var startInfo = NewStartInfo(program ?? RclonePath, arguments);
        Access.ApplyTo(startInfo.Environment);
        return startInfo;
    }

    internal Task<RcloneResult> RunAsync(
        IEnumerable<string> arguments,
        string? input,
        Action<string>? onLogLine,
        CancellationToken cancellationToken) =>
        RunAsync(CreateStartInfo(arguments), input, onLogLine, cancellationToken);

    private static ProcessStartInfo NewStartInfo(string rclonePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(rclonePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ClearSettings(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(name);
        }

        environment["RCLONE_CONFIG"] = "";
    }

    /// <summary>
    /// Runs rclone to its end. rclone writes results to its output and everything it
    /// has to say about its work to its error stream.
    /// </summary>
    private static async Task<RcloneResult> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        Action<string>? onLogLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        var log = new List<string>();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (log)
                {
                    log.Add(e.Data);
                }

                onLogLine?.Invoke(e.Data);
            }
        };

        process.Start();
        ChildProcesses.EndWithThisProcess(process);
        process.BeginErrorReadLine();

        // the output is taken as it is: it can be the content of a file
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // ended in the meantime
            }

            throw;
        }

        // makes sure the error stream is read to its end
        process.WaitForExit();
        return new RcloneResult(process.ExitCode, await output, log);
    }

    private static IReadOnlyList<RemoteEntry> ParseEntries(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<RemoteEntry>>(json) ?? [];
}

/// <summary>How a run of rclone ended.</summary>
/// <param name="ExitCode">The exit code of rclone. 0 means success.</param>
/// <param name="Output">What rclone wrote to its output.</param>
/// <param name="Log">What rclone reported about its work, line by line.</param>
public sealed record RcloneResult(int ExitCode, string Output, IReadOnlyList<string> Log)
{
    public void ThrowIfFailed()
    {
        if (ExitCode != 0)
        {
            throw new RcloneException(RcloneException.Describe(Log, ExitCode), ExitCode);
        }
    }
}

/// <summary>rclone ended with an error.</summary>
public sealed class RcloneException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    /// <summary>The last lines rclone reported, which name the reason of a failure.</summary>
    internal static string Describe(IReadOnlyList<string> log, int exitCode)
    {
        var lines = log.Where(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("CRITICAL", StringComparison.Ordinal) || l.Contains("NOTICE", StringComparison.Ordinal))
            .TakeLast(3)
            .ToList();
        if (lines.Count == 0)
        {
            lines = [.. log.TakeLast(3)];
        }

        return lines.Count == 0 ? $"rclone ended with exit code {exitCode}." : string.Join(Environment.NewLine, lines);
    }
}
