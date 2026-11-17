using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

public class ConnectionLossTests
{
    [SkippableFact]
    public async Task Offline_folder_stays_usable_without_the_storage_and_is_synced_afterwards()
    {
        var rclone = TestDrive.RequireRclone();
        var root = TestDrive.NewRoot();
        using var server = await S3TestServer.StartAsync(rclone, Path.Combine(root, "s3"), "bucket");
        using var link = new Link(server.Port);
        var storage = new S3Storage(
            $"http://127.0.0.1:{link.Port}", "us-east-1", "bucket", "drive", S3TestServer.AccessKey, S3TestServer.Secret, "Rclone");
        using var drive = await TestDrive.CreateAsync(root, storage);

        Assert.Equal(DriveState.Empty, await drive.Client.ProbeAsync());
        await drive.Client.InitializeAsync();
        await drive.PutAsync("Projekte/Plan.txt", "Stand vor der Reise");
        await drive.PutAsync("Fotos/Bild.txt", "nur online");

        var sync = new OfflineSync(drive.Client, new OfflineFolder("Projekte", drive.PathOf("offline", "Projekte")), drive.PathOf("state"));
        var local = sync.Folder.LocalPath;
        TestDrive.AssertOutcome(SyncOutcome.Synced, await sync.RunAsync());

        // the bucket holds encrypted names only
        var stored = await drive.Client.ListEncryptedAsync();
        Assert.NotEmpty(stored);
        Assert.DoesNotContain(stored, e => e.Path.Contains("Plan", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("Projekte", StringComparison.OrdinalIgnoreCase));

        // --- the connection is lost ---
        link.Down();
        Assert.False(await drive.Client.IsReachableAsync());
        Assert.Equal(DriveState.Unreachable, await drive.Client.ProbeAsync());

        // the offline folder holds ordinary files: reading and writing goes on
        Assert.Equal("Stand vor der Reise", File.ReadAllText(Path.Combine(local, "Plan.txt")));
        TestDrive.Write(Path.Combine(local, "Plan.txt"), "unterwegs geaendert");
        TestDrive.Write(Path.Combine(local, "Unterwegs.txt"), "unterwegs angelegt");

        // syncing fails without harm, as often as it is tried
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = await sync.RunAsync();
            TestDrive.AssertOutcome(SyncOutcome.Offline, failed);
            Assert.False(failed.ChangedAnything);
        }

        Assert.Equal("unterwegs geaendert", File.ReadAllText(Path.Combine(local, "Plan.txt")));

        // --- the connection is back; meanwhile another computer added a file ---
        link.Up();
        await drive.PutAsync("Projekte/Zuhause.txt", "inzwischen woanders angelegt");

        var back = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.Synced, back);
        Assert.Equal(2, back.Uploaded);
        Assert.Equal(1, back.Downloaded);
        Assert.Empty(back.Conflicts);
        Assert.Equal("unterwegs geaendert", await drive.Client.ReadTextAsync("Projekte/Plan.txt"));
        Assert.Equal("unterwegs angelegt", await drive.Client.ReadTextAsync("Projekte/Unterwegs.txt"));
        Assert.Equal("inzwischen woanders angelegt", File.ReadAllText(Path.Combine(local, "Zuhause.txt")));
        Assert.Equal(
            [OfflineSync.MarkerName, "Plan.txt", "Unterwegs.txt", "Zuhause.txt"],
            TestDrive.LocalFiles(local));
    }

    [SkippableFact]
    public async Task Keeper_syncs_after_a_change_and_at_its_interval()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "Stand");
        var sync = new OfflineSync(drive.Client, new OfflineFolder("Projekte", drive.PathOf("offline", "Projekte")), drive.PathOf("state"));
        var local = sync.Folder.LocalPath;
        var results = new List<SyncResult>();

        await using (var keeper = new OfflineKeeper(sync, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500)))
        {
            keeper.Completed += (_, result) =>
            {
                lock (results)
                {
                    results.Add(result);
                }
            };
            keeper.Start();

            await Wait.UntilAsync(() => File.Exists(Path.Combine(local, "Plan.txt")), TimeSpan.FromSeconds(60), "the first sync");

            // a change on this computer is carried over without anyone asking
            TestDrive.Write(Path.Combine(local, "Hier.txt"), "hier angelegt");
            await Wait.UntilAsync(
                async () => (await drive.FilesAsync("Projekte")).Contains("Hier.txt"), TimeSpan.FromSeconds(60), "the upload of a new file");

            // a change in the storage arrives with one of the regular syncs
            await drive.PutAsync("Projekte/Dort.txt", "woanders angelegt");
            await Wait.UntilAsync(() => File.Exists(Path.Combine(local, "Dort.txt")), TimeSpan.FromSeconds(60), "the download of a new file");
        }

        lock (results)
        {
            Assert.All(results, r => Assert.Equal(SyncOutcome.Synced, r.Outcome));
        }
    }
}
