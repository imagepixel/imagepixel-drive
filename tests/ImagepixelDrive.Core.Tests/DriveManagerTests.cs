using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

public class DriveManagerTests
{
    private const string Password = "Nur-ein-Testpasswort-4711";

    private static DriveManager NewManager(string root, ISecretStore? secrets = null) => new(
        new ConfigStore(Path.Combine(root, "settings")),
        secrets ?? new MemorySecretStore(),
        TestDrive.RequireRclone(),
        Path.Combine(root, "data"),
        Path.Combine(root, "offline"))
    {
        // Windows picks a free drive letter; elsewhere the drive is a folder, which
        // must not be the one in the home folder of whoever runs the tests
        Mountpoint = OperatingSystem.IsWindows() ? null : Path.Combine(root, "Laufwerk"),
    };

    private static StorageSettings InFolder(string folder) => new() { Kind = StorageKind.LocalFolder, LocalPath = folder };

    [SkippableFact]
    public async Task A_drive_in_a_folder_is_created_remembered_and_guarded_by_its_password()
    {
        var root = TestDrive.NewRoot();
        var folder = Path.Combine(root, "OneDrive", DriveManager.DriveFolderName);
        var secrets = new MemorySecretStore();
        await using var manager = NewManager(root, secrets);
        Assert.False(manager.IsConnected);

        // nothing is created unless it is asked for
        Assert.Equal(DriveState.Empty, await manager.ConnectAsync(InFolder(folder), Password, null, createIfMissing: false));
        Assert.False(Directory.Exists(folder));
        Assert.False(manager.IsConnected);

        Assert.Equal(DriveState.Ready, await manager.ConnectAsync(InFolder(folder), Password, null, createIfMissing: true));
        Assert.True(manager.IsConnected);
        Assert.False(manager.SupportsOfflineFolders);
        Assert.True(DriveManager.LooksLikeDrive(folder));

        // the next start finds the drive again
        await using var next = NewManager(root, secrets);
        Assert.True(next.IsConnected);
        Assert.Equal(folder, next.Config.Storage!.LocalPath);

        // another password does not open it, and does not replace what is set up
        Assert.Equal(DriveState.WrongPassword, await next.ConnectAsync(InFolder(folder), "ein-anderes-Passwort", null, createIfMissing: true));
        Assert.Equal(Password, secrets.Get(SecretNames.DrivePassword));

        await next.DisconnectAsync();
        Assert.False(next.IsConnected);
        Assert.Null(secrets.Get(SecretNames.DrivePassword));
        Assert.True(DriveManager.LooksLikeDrive(folder));
    }

    [Fact]
    public void A_drive_gets_a_folder_of_its_own()
    {
        var root = TestDrive.NewRoot();
        var cloud = Path.Combine(root, "OneDrive");
        TestDrive.Write(Path.Combine(cloud, "Urlaub.jpg"), "ein Bild");

        // inside a folder with other files: a folder of its own
        Assert.Equal(Path.Combine(cloud, DriveManager.DriveFolderName), DriveManager.ResolveDriveFolder(cloud));

        // an empty folder with another name as well, so that the name tells what it is
        var empty = Path.Combine(root, "Leer");
        Directory.CreateDirectory(empty);
        Assert.Equal(Path.Combine(empty, DriveManager.DriveFolderName), DriveManager.ResolveDriveFolder(empty));

        // the folder of a drive is taken as it is, whatever it is called
        var named = Path.Combine(cloud, DriveManager.DriveFolderName);
        Assert.Equal(named, DriveManager.ResolveDriveFolder(named + Path.DirectorySeparatorChar));
        var existing = Path.Combine(root, "Tresor");
        TestDrive.Write(Path.Combine(existing, "s3up63dlbtmgtjh5ds08k279a8"), "verschluesselt");
        TestDrive.Write(Path.Combine(existing, "tjrlguecp2jpjdtkicd8oqjmu4", "c4pmmt3jjvegh14qhnl27n9okc"), "verschluesselt");
        Assert.True(DriveManager.LooksLikeDrive(existing));
        Assert.Equal(existing, DriveManager.ResolveDriveFolder(existing));
        Assert.False(DriveManager.LooksLikeDrive(cloud));
    }

    [SkippableFact]
    public async Task A_drive_cannot_lie_in_a_folder_the_application_uses()
    {
        var root = TestDrive.NewRoot();
        await using var manager = NewManager(root);

        var error = await Assert.ThrowsAsync<FolderRuleException>(() =>
            manager.ConnectAsync(InFolder(Path.Combine(root, "offline", "Tresor")), Password, null, createIfMissing: true));

        Assert.Equal(FolderRuleException.Rule.UsedByApplication, error.Reason);
        Assert.False(manager.IsConnected);
    }

    [SkippableFact]
    public async Task Manager_shows_the_drive_and_keeps_a_chosen_folder_offline()
    {
        Skip.IfNot(DriveMount.IsSupported, "This computer cannot show a drive.");
        var rclone = TestDrive.RequireRclone();
        var root = TestDrive.NewRoot();
        using var server = await S3TestServer.StartAsync(rclone, Path.Combine(root, "s3"), "bucket");
        var storage = new StorageSettings
        {
            Kind = StorageKind.ImagepixelStorage,
            Endpoint = $"http://127.0.0.1:{server.Port}",
            Region = "us-east-1",
            Bucket = "bucket",
            AccessKeyId = S3TestServer.AccessKey,
        };
        var secrets = new MemorySecretStore();
        await using var manager = NewManager(root, secrets);
        manager.SyncInterval = TimeSpan.FromSeconds(3);
        var changes = 0;
        manager.Changed += () => Interlocked.Increment(ref changes);

        Assert.Equal(DriveState.Ready, await manager.ConnectAsync(storage, Password, S3TestServer.Secret, createIfMissing: true));
        Assert.True(manager.SupportsOfflineFolders);
        Assert.Equal(S3TestServer.Secret, secrets.Get(SecretNames.SecretAccessKey));

        await manager.StartAsync();
        Assert.True(manager.Phase == DrivePhase.Running, manager.Problem?.Message);
        var drive = manager.Mount!.RootPath;

        // the user works on the drive ...
        Directory.CreateDirectory(Path.Combine(drive, "Projekte", "2026"));
        File.WriteAllText(Path.Combine(drive, "Projekte", "2026", "Plan.txt"), "ueber das Laufwerk");
        Directory.CreateDirectory(Path.Combine(drive, "Fotos"));
        File.WriteAllText(Path.Combine(drive, "Fotos", "Bild.txt"), "bleibt online");
        await Wait.UntilAsync(async () => await manager.PendingUploadsAsync() == 0, TimeSpan.FromSeconds(60), "the uploads");

        // ... and picks a folder of it to keep on the computer
        Assert.Equal("Projekte/2026", manager.ToRemotePath(Path.Combine(drive, "Projekte", "2026")));
        Assert.Equal(FolderRuleException.Rule.WholeDrive, Assert.Throws<FolderRuleException>(() => manager.ToRemotePath(drive)).Reason);
        Assert.Equal(FolderRuleException.Rule.NotOnDrive, Assert.Throws<FolderRuleException>(() => manager.ToRemotePath(root)).Reason);

        var folder = await manager.AddOfflineFolderAsync(manager.ToRemotePath(Path.Combine(drive, "Projekte")));
        Assert.Equal(Path.Combine(root, "offline", "Projekte"), folder.LocalPath);
        var local = Path.Combine(folder.LocalPath, "2026", "Plan.txt");
        await Wait.UntilAsync(() => File.Exists(local), TimeSpan.FromSeconds(60), "the first sync of the offline folder");
        Assert.Equal("ueber das Laufwerk", File.ReadAllText(local));
        Assert.False(Directory.Exists(Path.Combine(root, "offline", "Fotos")));

        // a folder inside or around an offline folder cannot be added on top
        Assert.Equal(
            FolderRuleException.Rule.OfflineAlready,
            (await Assert.ThrowsAsync<FolderRuleException>(() => manager.AddOfflineFolderAsync("Projekte/2026"))).Reason);

        // a change in the offline folder shows up on the drive
        File.WriteAllText(Path.Combine(folder.LocalPath, "Neu.txt"), "im Offline-Ordner angelegt");
        await Wait.UntilAsync(
            () => File.Exists(Path.Combine(drive, "Projekte", "Neu.txt")), TimeSpan.FromSeconds(90), "the new file on the drive");
        Assert.True(changes > 0);

        // giving the folder up leaves its files on the computer and in the drive
        await manager.RemoveOfflineFolderAsync("Projekte");
        Assert.Empty(manager.Config.OfflineFolders);
        Assert.True(File.Exists(local));
        Assert.False(File.Exists(Path.Combine(folder.LocalPath, OfflineSync.MarkerName)));
        Assert.True(File.Exists(Path.Combine(drive, "Projekte", "2026", "Plan.txt")));

        await manager.StopAsync();
        Assert.Equal(DrivePhase.Stopped, manager.Phase);
        Assert.False(Directory.Exists(drive));
    }
}

public class SetupCodeTests
{
    [Fact]
    public void A_code_carries_the_access_to_a_storage()
    {
        var code = new SetupCode("kunde-drive", "KEYID", "geheim", "eu-central-2", "drive", "https://portal.example.test/api/v1", "token");

        var text = code.Encode();

        Assert.StartsWith(SetupCode.DrivePrefix, text);
        Assert.True(SetupCode.TryDecode("  " + text[..20] + " \n" + text[20..] + "  ", out var decoded));
        Assert.Equal(code, decoded);
    }

    [Fact]
    public void A_code_of_the_backup_application_is_accepted()
    {
        // {"b":"kunde","k":"KEYID","s":"geheim","r":"eu-central-2"}
        const string Code = "IPB1-eyJiIjoia3VuZGUiLCJrIjoiS0VZSUQiLCJzIjoiZ2VoZWltIiwiciI6ImV1LWNlbnRyYWwtMiJ9";

        Assert.True(SetupCode.TryDecode(Code, out var decoded));

        Assert.Equal("kunde", decoded!.Bucket);
        Assert.Equal("KEYID", decoded.AccessKeyId);
        Assert.Equal("geheim", decoded.SecretAccessKey);
        Assert.Null(decoded.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("irgendein Text")]
    [InlineData("IPD1-kein-base64!!")]
    [InlineData("IPD1-e30")]
    public void Anything_else_is_not_a_code(string text)
    {
        Assert.False(SetupCode.TryDecode(text, out var decoded));
        Assert.Null(decoded);
    }
}
