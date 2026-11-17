using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

/// <summary>
/// A drive in the temp folder for one test: the encrypted files lie in a folder that
/// stands in for the storage, next to a folder for everything else a test needs.
/// </summary>
internal sealed class TestDrive : IDisposable
{
    public const string Password = "Nur-ein-Testpasswort-4711";

    private TestDrive(string root, string rclone, RcloneClient client, DriveStorage storage)
    {
        Root = root;
        Rclone = rclone;
        Client = client;
        Storage = storage;
    }

    /// <summary>The folder of this test.</summary>
    public string Root { get; }

    public string Rclone { get; }

    public RcloneClient Client { get; }

    public DriveStorage Storage { get; }

    /// <summary>The folder that stands in for the storage, if the drive is stored in a folder.</summary>
    public string StorageFolder => Path.Combine(Root, "storage");

    /// <summary>The rclone program, or a skipped test if it has not been fetched.</summary>
    public static string RequireRclone()
    {
        var rclone = RcloneClient.FindRclone();
        Skip.If(rclone is null, "rclone was not found. Run get-rclone.ps1 first.");
        return rclone!;
    }

    /// <summary>Creates a drive whose encrypted files lie in a folder.</summary>
    public static async Task<TestDrive> CreateAsync()
    {
        var rclone = RequireRclone();
        var root = NewRoot();
        var storage = new FolderStorage(Path.Combine(root, "storage"));
        Directory.CreateDirectory(storage.Path);
        var client = await RcloneClient.CreateAsync(rclone, storage, Password);
        return new TestDrive(root, rclone, client, storage);
    }

    /// <summary>Creates a drive at a storage the test provides.</summary>
    public static async Task<TestDrive> CreateAsync(string root, DriveStorage storage)
    {
        var rclone = RequireRclone();
        var client = await RcloneClient.CreateAsync(rclone, storage, Password);
        return new TestDrive(root, rclone, client, storage);
    }

    public static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipd-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A path below the folder of this test.</summary>
    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Writes a file on this computer, creating its folder.</summary>
    public static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>Puts a file with the given text into the drive, as another computer would.</summary>
    public async Task PutAsync(string path, string text)
    {
        var temporary = PathOf("upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, text);
        try
        {
            await Client.UploadAsync(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Deletes a file of the drive, as another computer would.</summary>
    public async Task DeleteAsync(string path)
    {
        var result = await Client.RunAsync(["deletefile", DriveAccess.Remote(path)], null, null, CancellationToken.None);
        result.ThrowIfFailed();
    }

    /// <summary>The paths of all files in the drive, sorted.</summary>
    public async Task<string[]> FilesAsync(string folder = "")
    {
        var entries = await Client.ListAsync(folder, recursive: true);
        return [.. entries.Where(e => !e.IsDirectory).Select(e => e.Path).Order(StringComparer.Ordinal)];
    }

    /// <summary>The paths of all files in a folder of this computer, sorted, with forward slashes.</summary>
    public static string[] LocalFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// A free drive letter of Windows, counted from the end of the order the
    /// application prefers them in; null if there are not that many. Tests that show a
    /// drive run at the same time, and the application itself takes the first letter,
    /// so every test class takes one of its own from the other end.
    /// </summary>
    public static string? DriveLetterFromEnd(int position)
    {
        var letters = DriveMount.FreeDriveLetters();
        return letters.Count >= position ? letters[^position] : null;
    }

    /// <summary>Runs a program of the system to its end and returns its exit code and what it printed.</summary>
    public static (int ExitCode, string Output) Run(string program, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + errors.GetAwaiter().GetResult());
    }

    /// <summary>Fails the test with what rclone reported if a sync did not end as expected.</summary>
    public static void AssertOutcome(SyncOutcome expected, SyncResult result) =>
        Assert.True(expected == result.Outcome, $"Expected {expected}, got {result.Outcome}:{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors)}");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // still in use by a program that is ending; the temp folder is cleaned up by the system
        }
    }
}
