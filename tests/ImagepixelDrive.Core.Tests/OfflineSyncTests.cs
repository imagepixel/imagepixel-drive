namespace ImagepixelDrive.Core.Tests;

public class OfflineSyncTests
{
    private static OfflineSync NewSync(TestDrive drive, string remote = "Projekte") =>
        new(drive.Client, new OfflineFolder(remote, drive.PathOf("offline", remote)), drive.PathOf("state", remote));

    [SkippableFact]
    public async Task First_run_brings_both_sides_together()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "aus dem Speicher");
        await drive.PutAsync("Projekte/Unter/Tief.txt", "tief im Speicher");
        await drive.PutAsync("Fotos/Bild.txt", "bleibt online");
        var sync = NewSync(drive);
        TestDrive.Write(Path.Combine(sync.Folder.LocalPath, "Lokal.txt"), "von diesem Geraet");

        Assert.False(sync.IsInitialized);
        var result = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.True(result.FirstRun);
        Assert.True(sync.IsInitialized);
        Assert.Equal(
            [OfflineSync.MarkerName, "Lokal.txt", "Plan.txt", "Unter/Tief.txt"],
            TestDrive.LocalFiles(sync.Folder.LocalPath));
        Assert.Equal(
            [OfflineSync.MarkerName, "Lokal.txt", "Plan.txt", "Unter/Tief.txt"],
            await drive.FilesAsync("Projekte"));

        // what is not kept offline stays in the storage only
        Assert.False(Directory.Exists(drive.PathOf("offline", "Fotos")));
        Assert.Equal("aus dem Speicher", File.ReadAllText(Path.Combine(sync.Folder.LocalPath, "Plan.txt")));
    }

    [SkippableFact]
    public async Task Changes_travel_in_both_directions()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "erste Fassung");
        var sync = NewSync(drive);
        TestDrive.AssertOutcome(SyncOutcome.Synced, await sync.RunAsync());
        var local = sync.Folder.LocalPath;

        // this computer: one new file, one changed file; another computer: one new file
        TestDrive.Write(Path.Combine(local, "Neu hier.txt"), "hier angelegt");
        TestDrive.Write(Path.Combine(local, "Plan.txt"), "zweite Fassung, hier geaendert");
        await drive.PutAsync("Projekte/Neu dort.txt", "woanders angelegt");

        var result = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.False(result.FirstRun);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(1, result.Downloaded);
        Assert.True(result.ChangedStorage);
        Assert.Equal("zweite Fassung, hier geaendert", await drive.Client.ReadTextAsync("Projekte/Plan.txt"));
        Assert.Equal("hier angelegt", await drive.Client.ReadTextAsync("Projekte/Neu hier.txt"));
        Assert.Equal("woanders angelegt", File.ReadAllText(Path.Combine(local, "Neu dort.txt")));

        // nothing left to do
        var again = await sync.RunAsync();
        TestDrive.AssertOutcome(SyncOutcome.Synced, again);
        Assert.False(again.ChangedAnything);
    }

    [SkippableFact]
    public async Task Deletions_travel_in_both_directions()
    {
        using var drive = await TestDrive.CreateAsync();
        foreach (var name in new[] { "A.txt", "B.txt", "C.txt", "D.txt", "E.txt" })
        {
            await drive.PutAsync("Projekte/" + name, "Inhalt " + name);
        }

        var sync = NewSync(drive);
        await sync.RunAsync();
        var local = sync.Folder.LocalPath;

        File.Delete(Path.Combine(local, "A.txt"));
        await drive.DeleteAsync("Projekte/B.txt");

        var result = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.Equal(1, result.DeletedInStorage);
        Assert.Equal(1, result.DeletedHere);
        Assert.Equal([OfflineSync.MarkerName, "C.txt", "D.txt", "E.txt"], TestDrive.LocalFiles(local));
        Assert.Equal([OfflineSync.MarkerName, "C.txt", "D.txt", "E.txt"], await drive.FilesAsync("Projekte"));
    }

    [SkippableFact]
    public async Task A_file_changed_on_both_sides_is_kept_twice()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "gemeinsamer Stand");
        var sync = NewSync(drive);
        await sync.RunAsync();
        var local = sync.Folder.LocalPath;

        TestDrive.Write(Path.Combine(local, "Plan.txt"), "hier geaendert");
        await Task.Delay(1500);
        await drive.PutAsync("Projekte/Plan.txt", "woanders geaendert, und zwar spaeter");

        var result = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.Equal(["Plan.Konflikt1.txt"], result.Conflicts);

        // the newer version keeps the name, the other one stays next to it, on both sides
        Assert.Equal("woanders geaendert, und zwar spaeter", File.ReadAllText(Path.Combine(local, "Plan.txt")));
        Assert.Equal("hier geaendert", File.ReadAllText(Path.Combine(local, "Plan.Konflikt1.txt")));
        Assert.Equal("woanders geaendert, und zwar spaeter", await drive.Client.ReadTextAsync("Projekte/Plan.txt"));
        Assert.Equal("hier geaendert", await drive.Client.ReadTextAsync("Projekte/Plan.Konflikt1.txt"));
    }

    [SkippableFact]
    public async Task Deleting_most_files_needs_a_confirmation()
    {
        using var drive = await TestDrive.CreateAsync();
        foreach (var name in new[] { "A.txt", "B.txt", "C.txt", "D.txt" })
        {
            await drive.PutAsync("Projekte/" + name, "Inhalt " + name);
        }

        var sync = NewSync(drive);
        await sync.RunAsync();
        var local = sync.Folder.LocalPath;

        foreach (var name in new[] { "A.txt", "B.txt", "C.txt", "D.txt" })
        {
            File.Delete(Path.Combine(local, name));
        }

        var stopped = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.TooManyDeletes, stopped);
        Assert.Equal(5, (await drive.FilesAsync("Projekte")).Length);

        var confirmed = await sync.RunAsync(SyncMode.AcceptAll);

        TestDrive.AssertOutcome(SyncOutcome.Synced, confirmed);
        Assert.Equal([OfflineSync.MarkerName], await drive.FilesAsync("Projekte"));
    }

    [SkippableFact]
    public async Task A_folder_that_vanished_on_this_computer_does_not_empty_the_storage()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "wichtig");
        await drive.PutAsync("Projekte/Liste.txt", "auch wichtig");
        var sync = NewSync(drive);
        await sync.RunAsync();

        // the folder is gone as a whole, for example with the disk it was on
        File.SetAttributes(Path.Combine(sync.Folder.LocalPath, OfflineSync.MarkerName), FileAttributes.Normal);
        Directory.Delete(sync.Folder.LocalPath, recursive: true);
        var gone = await sync.RunAsync();
        TestDrive.AssertOutcome(SyncOutcome.FolderMissing, gone);

        // ... or is there again, but empty
        Directory.CreateDirectory(sync.Folder.LocalPath);
        var empty = await sync.RunAsync();
        TestDrive.AssertOutcome(SyncOutcome.FolderMissing, empty);

        Assert.Equal([OfflineSync.MarkerName, "Liste.txt", "Plan.txt"], await drive.FilesAsync("Projekte"));
    }

    [SkippableFact]
    public async Task A_folder_that_vanished_in_the_storage_does_not_empty_this_computer()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "wichtig");
        await drive.PutAsync("Projekte/Liste.txt", "auch wichtig");
        await drive.PutAsync("Fotos/Bild.txt", "damit die Ablage nicht leer wird");
        var sync = NewSync(drive);
        await sync.RunAsync();

        var purge = await drive.Client.RunAsync(["purge", DriveAccess.Remote("Projekte")], null, null, CancellationToken.None);
        purge.ThrowIfFailed();

        var result = await sync.RunAsync();

        TestDrive.AssertOutcome(SyncOutcome.FolderMissing, result);
        Assert.Equal([OfflineSync.MarkerName, "Liste.txt", "Plan.txt"], TestDrive.LocalFiles(sync.Folder.LocalPath));
    }

    [SkippableFact]
    public async Task Repair_brings_both_sides_together_again_without_deleting()
    {
        using var drive = await TestDrive.CreateAsync();
        await drive.PutAsync("Projekte/Plan.txt", "Stand");
        var sync = NewSync(drive);
        await sync.RunAsync();
        TestDrive.Write(Path.Combine(sync.Folder.LocalPath, "Hier.txt"), "nur hier");
        await drive.PutAsync("Projekte/Dort.txt", "nur dort");

        var result = await sync.RunAsync(SyncMode.Repair);

        TestDrive.AssertOutcome(SyncOutcome.Synced, result);
        Assert.True(result.FirstRun);
        Assert.Equal([OfflineSync.MarkerName, "Dort.txt", "Hier.txt", "Plan.txt"], TestDrive.LocalFiles(sync.Folder.LocalPath));
        Assert.Equal([OfflineSync.MarkerName, "Dort.txt", "Hier.txt", "Plan.txt"], await drive.FilesAsync("Projekte"));
    }

    [SkippableFact]
    public async Task The_whole_drive_cannot_be_an_offline_folder()
    {
        using var drive = await TestDrive.CreateAsync();

        Assert.Throws<ArgumentException>(() => new OfflineSync(drive.Client, new OfflineFolder("/", drive.PathOf("alles")), drive.PathOf("state")));
    }

    [Fact]
    public void State_name_is_stable_and_differs_per_folder()
    {
        var first = new OfflineFolder("Projekte", Path.Combine(Path.GetTempPath(), "a"));
        var same = new OfflineFolder("/Projekte/", Path.Combine(Path.GetTempPath(), "a") + Path.DirectorySeparatorChar);
        var other = new OfflineFolder("Fotos", Path.Combine(Path.GetTempPath(), "a"));

        Assert.Equal(OfflineSync.StateName(first), OfflineSync.StateName(same));
        Assert.NotEqual(OfflineSync.StateName(first), OfflineSync.StateName(other));
        Assert.Matches("^[0-9a-f]{16}$", OfflineSync.StateName(first));
    }
}
