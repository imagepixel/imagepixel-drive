using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

/// <summary>
/// Tests that show a drive: at a drive letter on Windows, which needs WinFsp, and at a
/// folder on macOS.
/// </summary>
public class DriveMountTests
{
    /// <summary>A place to show a drive at: a free drive letter on Windows, a folder of its own on macOS.</summary>
    private static string RequireDriveLetter()
    {
        Skip.IfNot(DriveMount.IsSupported, "This computer cannot show a drive.");
        if (!OperatingSystem.IsWindows())
        {
            return Path.Combine(TestDrive.NewRoot(), "Laufwerk");
        }

        var letters = DriveMount.FreeDriveLetters();
        Skip.If(letters.Count == 0, "No drive letter is free.");

        // The last one in the order of preference: the tests of the application run at
        // the same time and let the application choose, which takes the first.
        return letters[^1];
    }

    [SkippableFact]
    public async Task Drive_shows_the_storage_and_stores_what_is_written_encrypted()
    {
        var letter = RequireDriveLetter();
        using var drive = await TestDrive.CreateAsync();
        await drive.Client.InitializeAsync();
        await drive.PutAsync("Projekte/Plan.txt", "aus dem Speicher");

        await using (var mount = await DriveMount.StartAsync(drive.Client, letter, new DriveMountOptions(drive.PathOf("cache"))))
        {
            Assert.True(mount.IsRunning);
            Assert.Equal("aus dem Speicher", File.ReadAllText(Path.Combine(mount.RootPath, "Projekte", "Plan.txt")));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("Imagepixel Drive", new DriveInfo(letter).VolumeLabel);
            }

            // written like on any disk
            File.WriteAllText(Path.Combine(mount.RootPath, "Notiz.txt"), "ueber das Laufwerk geschrieben");
            Directory.CreateDirectory(Path.Combine(mount.RootPath, "Fotos"));
            File.WriteAllText(Path.Combine(mount.RootPath, "Fotos", "Bild.txt"), "ein Bild");

            await Wait.UntilAsync(
                async () => await mount.PendingUploadsAsync() == 0 && (await drive.FilesAsync()).Contains("Fotos/Bild.txt"),
                TimeSpan.FromSeconds(60),
                "the upload of what was written to the drive");
            Assert.Equal("ueber das Laufwerk geschrieben", await drive.Client.ReadTextAsync("Notiz.txt"));

            // the storage folder holds none of the names
            var stored = Directory.EnumerateFiles(drive.StorageFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(drive.StorageFolder, f))
                .ToList();
            Assert.Equal(4, stored.Count);
            Assert.DoesNotContain(stored, f => f.Contains("Notiz", StringComparison.OrdinalIgnoreCase) || f.Contains("Fotos", StringComparison.OrdinalIgnoreCase));

            // a change made behind the back of the drive shows up when it is told to look again
            await drive.PutAsync("Projekte/Neu.txt", "von einem anderen Geraet");
            await mount.RefreshAsync("Projekte");
            Assert.Equal("von einem anderen Geraet", File.ReadAllText(Path.Combine(mount.RootPath, "Projekte", "Neu.txt")));
        }

        Assert.False(Directory.Exists(letter + Path.DirectorySeparatorChar));
    }

    [SkippableFact]
    public async Task What_is_written_without_a_connection_is_stored_once_it_is_back()
    {
        var letter = RequireDriveLetter();
        var rclone = TestDrive.RequireRclone();
        var root = TestDrive.NewRoot();
        using var server = await S3TestServer.StartAsync(rclone, Path.Combine(root, "s3"), "bucket");
        using var link = new Link(server.Port);
        var storage = new S3Storage(
            $"http://127.0.0.1:{link.Port}", "us-east-1", "bucket", "drive", S3TestServer.AccessKey, S3TestServer.Secret, "Rclone");
        using var drive = await TestDrive.CreateAsync(root, storage);
        await drive.Client.InitializeAsync();

        await using var mount = await DriveMount.StartAsync(drive.Client, letter, new DriveMountOptions(drive.PathOf("cache")));

        // The drive takes new files without a connection only in folders whose list
        // it still knows; this one was just looked at.
        Assert.Equal([RcloneClient.CheckFileName], Directory.GetFileSystemEntries(mount.RootPath).Select(Path.GetFileName));

        link.Down();
        File.WriteAllText(Path.Combine(mount.RootPath, "Unterwegs.txt"), "ohne Verbindung geschrieben");
        await Wait.UntilAsync(async () => await mount.PendingUploadsAsync() == 1, TimeSpan.FromSeconds(30), "the file waiting for its upload");

        link.Up();
        await Wait.UntilAsync(
            async () => await mount.PendingUploadsAsync() == 0, TimeSpan.FromSeconds(120), "the upload after the connection came back");
        Assert.Equal("ohne Verbindung geschrieben", await drive.Client.ReadTextAsync("Unterwegs.txt"));
    }

    [SkippableFact]
    public async Task Drive_and_offline_folder_show_the_same_files()
    {
        var letter = RequireDriveLetter();
        using var drive = await TestDrive.CreateAsync();
        await drive.Client.InitializeAsync();
        var sync = new OfflineSync(drive.Client, new OfflineFolder("Projekte", drive.PathOf("offline", "Projekte")), drive.PathOf("state"));
        var local = sync.Folder.LocalPath;

        await using var mount = await DriveMount.StartAsync(drive.Client, letter, new DriveMountOptions(drive.PathOf("cache")));

        // written through the drive, found in the offline folder
        Directory.CreateDirectory(Path.Combine(mount.RootPath, "Projekte"));
        File.WriteAllText(Path.Combine(mount.RootPath, "Projekte", "Vom Laufwerk.txt"), "ueber das Laufwerk");
        await Wait.UntilAsync(
            async () => await mount.PendingUploadsAsync() == 0 && (await drive.FilesAsync()).Contains("Projekte/Vom Laufwerk.txt"),
            TimeSpan.FromSeconds(60),
            "the upload of what was written to the drive");
        TestDrive.AssertOutcome(SyncOutcome.Synced, await sync.RunAsync());
        Assert.Equal("ueber das Laufwerk", File.ReadAllText(Path.Combine(local, "Vom Laufwerk.txt")));

        // written in the offline folder, found on the drive
        TestDrive.Write(Path.Combine(local, "Vom Ordner.txt"), "im Offline-Ordner");
        var result = await sync.RunAsync();
        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.True(result.ChangedStorage);
        await mount.RefreshAsync("Projekte");
        Assert.Equal("im Offline-Ordner", File.ReadAllText(Path.Combine(mount.RootPath, "Projekte", "Vom Ordner.txt")));
    }
}
