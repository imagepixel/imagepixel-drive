using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ImagepixelDrive.App.ViewModels;
using ImagepixelDrive.App.Views;
using ImagepixelDrive.Core;
using ImagepixelDrive.Core.Rclone;
using ImagepixelDrive.Core.Tests;

[assembly: AvaloniaTestApplication(typeof(ImagepixelDrive.App.Tests.TestAppBuilder))]

namespace ImagepixelDrive.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::ImagepixelDrive.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Walks through the application the way a customer would, without showing a window:
/// once with a drive in a cloud folder of the customer, and once with Imagepixel
/// Storage and a folder that is kept offline. The tests run the real rclone against
/// storages in the temp directory and save a picture of every screen to
/// artifacts/screenshots.
/// </summary>
public sealed class WalkthroughTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipd-ui-" + Guid.NewGuid().ToString("N")[..12]);

    private string OneDrive => Path.Combine(_root, "OneDrive");

    /// <summary>Stands in for the cloud storage a customer has set up on the computer.</summary>
    private CloudFolder[] CloudStorage =>
    [
        new(CloudService.OneDrive, "OneDrive", OneDrive),
        new(CloudService.Dropbox, "Dropbox", Path.Combine(_root, "Dropbox")),
    ];

    public WalkthroughTests()
    {
        foreach (var cloud in CloudStorage)
        {
            Directory.CreateDirectory(cloud.Path);
            File.WriteAllText(Path.Combine(cloud.Path, "Urlaub.txt"), "liegt schon in der Cloud");
        }
    }

    private DriveManager NewManager(ISecretStore secrets) => new(
        new ConfigStore(Path.Combine(_root, "Einstellungen")),
        secrets,
        RcloneClient.FindRclone()!,
        Path.Combine(_root, "Daten"),
        Path.Combine(_root, "Imagepixel Drive Offline"))
    {
        SyncInterval = TimeSpan.FromSeconds(3),

        // Windows picks a free drive letter; elsewhere the drive is a folder, which
        // must not be the one in the home folder of whoever runs the tests
        Mountpoint = OperatingSystem.IsWindows() ? null : Path.Combine(_root, "Laufwerk"),
    };

    [AvaloniaFact]
    public async Task Customer_puts_a_drive_into_a_cloud_folder_of_their_own()
    {
        Assert.SkipWhen(RcloneClient.FindRclone() is null, "rclone has not been fetched");
        var secrets = new MemorySecretStore();
        var manager = NewManager(secrets);
        // as on Windows, where the setup program takes care of starting with the computer
        var main = new MainViewModel(manager, CloudStorage) { LoginItem = null };
        var window = new MainWindow { DataContext = main };
        var opened = new List<string>();
        main.OpenInFileManager = opened.Add;
        window.Show();

        // --- once, before anything else: the notes on use and liability ----------------
        var terms = Assert.IsType<TermsViewModel>(main.CurrentPage);
        Assert.False(terms.ContinueCommand.CanExecute(null));
        Capture(window, "00-nutzungshinweise");

        terms.OpenTermsCommand.Execute(null);
        Assert.Equal([AboutViewModel.TermsUrl], opened);
        opened.Clear();

        terms.Confirmed = true;
        Assert.True(terms.ContinueCommand.CanExecute(null));
        await terms.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(Texts.TermsVersion, manager.Config.TermsAccepted);
        Assert.NotNull(manager.Config.TermsAcceptedAt);

        // --- where the files go: Imagepixel Storage is offered first ------------------
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.True(setup.IsImagepixel);
        Capture(window, "01-start");

        setup.ChooseKindCommand.Execute("LocalFolder");
        Assert.True(setup.IsOwnPlace);
        Assert.True(setup.HasCloudFolders);
        Capture(window, "02-eigener-speicherort");

        // one click on the cloud storage: the drive gets a folder of its own inside it
        setup.ChooseCloudFolderCommand.Execute(CloudStorage[0]);
        var driveFolder = Path.Combine(OneDrive, DriveManager.DriveFolderName);
        Assert.Equal(driveFolder, setup.DriveFolder);
        Assert.Contains(driveFolder, setup.DriveFolderHint);
        Assert.Contains("OneDrive", setup.CloudHint);
        Capture(window, "03-cloud-ordner-gewaehlt");

        // --- a new drive needs a password that is long enough and typed twice ---------
        setup.Password = "kurz";
        setup.PasswordRepeat = "kurz";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("mindestens 8 Zeichen", setup.ErrorMessage);

        setup.Password = Password;
        setup.PasswordRepeat = "vertippt-123";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("wiederhole das Passwort", setup.ErrorMessage);
        Assert.False(manager.IsConnected);

        setup.PasswordRepeat = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        // --- the overview ---------------------------------------------------------------
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.True(manager.IsConnected);
        Assert.True(dashboard.IsOwnPlace);
        Assert.False(dashboard.SupportsOfflineFolders);
        Assert.Equal(driveFolder, dashboard.OwnPlacePath);

        if (DriveMount.IsSupported)
        {
            Assert.True(dashboard.IsRunning, dashboard.DriveDetail);
            var drive = manager.Mount!.RootPath;

            // the customer works on the drive like on any disk
            Directory.CreateDirectory(Path.Combine(drive, "Steuer 2026"));
            File.WriteAllText(Path.Combine(drive, "Steuer 2026", "Belege.txt"), "Geheime Belege 4711");
            await Wait.UntilAsync(
                async () => await manager.PendingUploadsAsync() == 0 && Directory.EnumerateFiles(driveFolder, "*", SearchOption.AllDirectories).Count() >= 2,
                TimeSpan.FromSeconds(60),
                "the files arriving in the cloud folder");
            await dashboard.RefreshStatusAsync(force: true);
            Assert.Equal("Alles gespeichert.", dashboard.UploadStatus);
            Capture(window, "04-uebersicht-eigener-speicherort");

            dashboard.OpenDriveCommand.Execute(null);
            Assert.Equal([drive], opened);

            // what OneDrive gets to see: encrypted names and content, nothing else
            Assert.True(DriveManager.LooksLikeDrive(driveFolder));
            foreach (var file in Directory.EnumerateFiles(driveFolder, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("Belege", file, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Geheime", File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.True(dashboard.HasFailed);
            Capture(window, "04-uebersicht-eigener-speicherort");
        }

        // the other files of the cloud folder are left alone
        Assert.Equal("liegt schon in der Cloud", File.ReadAllText(Path.Combine(OneDrive, "Urlaub.txt")));

        // --- the info page leads back to the notes that were confirmed ---------------------
        dashboard.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Capture(window, "05-info");
        opened.Clear();
        about.OpenLinkCommand.Execute(AboutViewModel.TermsUrl);
        Assert.Equal([AboutViewModel.TermsUrl], opened);
        about.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);

        // --- disconnecting forgets the drive, but deletes nothing --------------------------
        await dashboard.AskDisconnectCommand.ExecuteAsync(null);
        Assert.True(dashboard.ConfirmDisconnect);

        // everything is stored, so there is nothing to warn about
        Assert.False(dashboard.HasDisconnectWarning);
        Capture(window, "06-trennen");
        await dashboard.DisconnectCommand.ExecuteAsync(null);
        var again = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.False(manager.IsConnected);
        Assert.Null(secrets.Get(SecretNames.DrivePassword));
        Assert.Null(secrets.Get(SecretNames.ObscuredDrivePassword));
        Assert.True(DriveManager.LooksLikeDrive(driveFolder));

        // --- opening it again, for example on another computer -----------------------------
        again.ChooseKindCommand.Execute("LocalFolder");
        again.LocalPath = OneDrive;
        again.Password = "falsches-Passwort";
        await again.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("das Passwort passt nicht", again.ErrorMessage);
        Capture(window, "07-falsches-passwort");

        again.Password = Password;
        await again.ConnectCommand.ExecuteAsync(null);
        Assert.Null(again.ErrorMessage);
        Assert.IsType<DashboardViewModel>(main.CurrentPage);
        if (DriveMount.IsSupported)
        {
            Assert.Equal("Geheime Belege 4711", File.ReadAllText(Path.Combine(manager.Mount!.RootPath, "Steuer 2026", "Belege.txt")));
        }

        await main.ShutdownAsync();
        Assert.Equal(DrivePhase.Stopped, manager.Phase);
        window.Close();

        // --- the next start does not ask for the notes again -------------------------------
        var next = new MainViewModel(manager, CloudStorage) { LoginItem = null };
        Assert.IsType<DashboardViewModel>(next.CurrentPage);
        next.Dashboard.Dispose();
    }

    [AvaloniaFact]
    public async Task Customer_uses_Imagepixel_Storage_and_keeps_a_folder_offline()
    {
        var rclone = RcloneClient.FindRclone();
        Assert.SkipWhen(rclone is null, "rclone has not been fetched");
        Assert.SkipUnless(DriveMount.IsSupported, "this computer cannot show a drive");

        // Imagepixel keeps a file in the bucket that tells how much storage is booked and used
        var statusFile = Path.Combine(_root, "s3", "kunde-drive", "imagepixel", "storage-status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statusFile)!);
        File.WriteAllText(
            statusFile,
            $$"""{ "version": 1, "bookedBytes": 107374182400, "usedBytes": 91268055040, "measuredAt": "{{DateTimeOffset.Now:O}}" }""");

        using var server = await S3TestServer.StartAsync(rclone!, Path.Combine(_root, "s3"), "kunde-drive");
        using var link = new Link(server.Port);
        var manager = NewManager(new MemorySecretStore());

        // the notes on use and liability were confirmed at an earlier start
        manager.AcceptTerms(Texts.TermsVersion);

        // as on a Mac: the application lies in the folder for programs and does not start at login yet
        var loginItem = new MacLoginItem(Path.Combine(_root, "LaunchAgents"));
        var program = "/Applications/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive";
        var main = new MainViewModel(manager, []) { LoginItem = loginItem, ExecutablePath = () => program };
        var window = new MainWindow { DataContext = main };
        string? picked = null;
        var opened = new List<string>();
        main.PickFolder = (_, _) => Task.FromResult(picked);
        main.OpenInFileManager = opened.Add;
        window.Show();

        // --- the setup code from Imagepixel fills in the access data -------------------
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.Endpoint = $"http://127.0.0.1:{link.Port}";
        setup.SetupCodeText = new SetupCode("kunde-drive", S3TestServer.AccessKey, S3TestServer.Secret, "us-east-1").Encode();
        Assert.Equal("kunde-drive", setup.Bucket);
        Assert.Equal(S3TestServer.AccessKey, setup.AccessKeyId);
        setup.Password = Password;
        setup.PasswordRepeat = Password;
        Capture(window, "10-imagepixel-storage");

        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.True(dashboard.IsRunning, dashboard.DriveDetail);
        Assert.True(dashboard.SupportsOfflineFolders);
        Assert.False(dashboard.IsOwnPlace);
        Assert.True(dashboard.IsReachable);
        var drive = manager.Mount!.RootPath;

        // --- the customer fills the drive -----------------------------------------------
        Directory.CreateDirectory(Path.Combine(drive, "Projekte"));
        Directory.CreateDirectory(Path.Combine(drive, "Fotos"));
        foreach (var name in new[] { "Angebot.txt", "Kalkulation.txt", "Plan.txt", "Notizen.txt" })
        {
            File.WriteAllText(Path.Combine(drive, "Projekte", name), "Inhalt von " + name);
        }

        File.WriteAllText(Path.Combine(drive, "Fotos", "Bild.txt"), "bleibt nur online");
        await Wait.UntilAsync(async () => await manager.PendingUploadsAsync() == 0, TimeSpan.FromSeconds(60), "the uploads");

        // --- the overview tells how much of the booked storage is used -------------------
        await dashboard.RefreshStorageStatusAsync();
        Assert.True(dashboard.HasStorageStatus);
        Assert.Equal("85 GB von 100 GB belegt (85 %)", dashboard.StorageText);
        Assert.StartsWith("Stand: heute", dashboard.StorageDetail);
        Assert.Contains("zu 85 % belegt", dashboard.StorageWarning);
        Assert.True(dashboard.ShowStartAtLoginNote);
        await dashboard.RefreshStatusAsync(force: true);
        Assert.Equal("Alles gespeichert.", dashboard.UploadStatus);
        Capture(window, "11-uebersicht");

        // --- one click makes the application start at every login ------------------------
        dashboard.StartAtLoginCommand.Execute(null);
        Assert.True(loginItem.IsEnabled);
        Assert.False(dashboard.ShowStartAtLoginNote);
        Assert.Contains("startet jetzt bei jeder Anmeldung", dashboard.Notice);
        var agent = File.ReadAllText(loginItem.FilePath);
        Assert.Contains($"<string>{program}</string>", agent);
        Assert.Contains("<string>--minimized</string>", agent);
        Capture(window, "11a-startet-bei-der-anmeldung");

        // --- choosing the whole drive is refused, a folder on it is taken ----------------
        picked = drive;
        await dashboard.AddOfflineFolderCommand.ExecuteAsync(null);
        Assert.Contains("ganze Laufwerk", dashboard.OfflineProblem);
        Assert.Empty(dashboard.OfflineFolders);

        picked = Path.Combine(drive, "Projekte");
        await dashboard.AddOfflineFolderCommand.ExecuteAsync(null);
        Assert.Null(dashboard.OfflineProblem);
        var folder = Assert.Single(dashboard.OfflineFolders);
        Assert.Equal("Projekte", folder.RemotePath);
        var local = folder.LocalPath;
        await Wait.UntilAsync(() => File.Exists(Path.Combine(local, "Plan.txt")), TimeSpan.FromSeconds(60), "the first sync");
        await SyncedAsync(manager, dashboard);
        Assert.StartsWith("Abgeglichen", folder.Status);
        Assert.False(folder.NeedsAttention);
        Assert.False(Directory.Exists(Path.Combine(_root, "Imagepixel Drive Offline", "Fotos")));
        Capture(window, "12-offline-ordner");

        folder.OpenCommand.Execute(null);
        Assert.Equal([local], opened);

        // --- the connection is lost: the folder stays usable ------------------------------
        link.Down();
        await dashboard.RefreshStatusAsync(force: true);
        Assert.False(dashboard.IsReachable);
        File.WriteAllText(Path.Combine(local, "Unterwegs.txt"), "ohne Verbindung geschrieben");
        File.WriteAllText(Path.Combine(local, "Plan.txt"), "unterwegs geaendert");
        await OutcomeAsync(manager, dashboard, SyncOutcome.Offline);
        Assert.Contains("nicht erreichbar", folder.Status);
        Assert.False(folder.NeedsAttention);
        Assert.Equal("unterwegs geaendert", File.ReadAllText(Path.Combine(local, "Plan.txt")));
        Capture(window, "13-ohne-verbindung");

        // --- back again: what changed on the way is carried over --------------------------
        link.Up();
        await dashboard.RefreshStatusAsync(force: true);
        Assert.True(dashboard.IsReachable);
        folder.SyncNowCommand.Execute(null);
        await Wait.UntilAsync(
            () => OnDrive(() => File.Exists(Path.Combine(drive, "Projekte", "Unterwegs.txt"))), TimeSpan.FromSeconds(90), "the file written without a connection");
        Assert.Equal("unterwegs geaendert", await OnDrive(() => File.ReadAllText(Path.Combine(drive, "Projekte", "Plan.txt"))));
        await SyncedAsync(manager, dashboard);

        // --- deleting nearly everything stops and asks -------------------------------------
        foreach (var file in Directory.EnumerateFiles(local).Where(f => Path.GetFileName(f) != OfflineSync.MarkerName))
        {
            File.Delete(file);
        }

        await OutcomeAsync(manager, dashboard, SyncOutcome.TooManyDeletes);
        Assert.True(folder.NeedsAttention);
        Assert.True(folder.CanConfirm);
        Assert.Equal("Löschungen übernehmen", folder.ConfirmLabel);
        Assert.True(await OnDrive(() => File.Exists(Path.Combine(drive, "Projekte", "Angebot.txt"))));
        Capture(window, "14-viele-dateien-geloescht");

        folder.ConfirmCommand.Execute(null);
        await Wait.UntilAsync(
            async () => !await OnDrive(() => File.Exists(Path.Combine(drive, "Projekte", "Angebot.txt"))), TimeSpan.FromSeconds(90), "the confirmed deletions");
        await SyncedAsync(manager, dashboard);
        Assert.False(folder.CanConfirm);

        // --- giving the folder up leaves it on the computer --------------------------------
        File.WriteAllText(Path.Combine(local, "Bleibt.txt"), "bleibt auf dem Rechner");
        await folder.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(dashboard.OfflineFolders);
        Assert.True(File.Exists(Path.Combine(local, "Bleibt.txt")));
        Capture(window, "15-nicht-mehr-offline");

        // --- disconnecting while a file is on its way: the question says what is at stake ---
        // (the drive takes a new file without a connection in a folder whose list it knows)
        await OnDrive(() => Directory.GetFileSystemEntries(drive));
        link.Down();
        await OnDrive(() =>
        {
            File.WriteAllText(Path.Combine(drive, "Spaet.txt"), "noch nicht gespeichert");
            return true;
        });
        await Wait.UntilAsync(async () => await manager.PendingUploadsAsync() == 1, TimeSpan.FromSeconds(30), "the file waiting for its upload");
        await dashboard.AskDisconnectCommand.ExecuteAsync(null);
        Assert.True(dashboard.ConfirmDisconnect);
        Assert.Equal(Texts.DisconnectLoses(1), dashboard.DisconnectWarning);
        Capture(window, "15a-trennen-mit-wartender-datei");
        dashboard.CancelDisconnectCommand.Execute(null);
        Assert.False(dashboard.ConfirmDisconnect);
        link.Up();
        await Wait.UntilAsync(
            async () => await manager.PendingUploadsAsync() == 0, TimeSpan.FromSeconds(120), "the upload after the connection came back");

        // --- the info page turns starting at login off and on ------------------------------
        dashboard.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.True(about.ShowStartAtLogin);
        Assert.True(about.StartAtLogin);
        about.StartAtLogin = false;
        Assert.False(loginItem.IsEnabled);

        // opened from its disk image, the application would not be found at the next login
        program = "/Volumes/Imagepixel Drive/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive";
        about.StartAtLogin = true;
        Assert.Contains("Programme", about.StartAtLoginProblem);
        Assert.False(loginItem.IsEnabled);
        Capture(window, "16-info-autostart");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(about.StartAtLogin);

        about.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.True(dashboard.ShowStartAtLoginNote);

        await main.ShutdownAsync();
        window.Close();
    }

    /// <summary>Waits until the last sync of the one offline folder ended a certain way and shows it.</summary>
    private static async Task OutcomeAsync(DriveManager manager, DashboardViewModel dashboard, SyncOutcome outcome)
    {
        await Wait.UntilAsync(
            () => manager.Keepers.Single() is { IsSyncing: false, LastResult: { } result } && result.Outcome == outcome,
            TimeSpan.FromSeconds(90),
            $"a sync that ends as {outcome}");
        dashboard.Refresh();
    }

    private static Task SyncedAsync(DriveManager manager, DashboardViewModel dashboard) =>
        OutcomeAsync(manager, dashboard, SyncOutcome.Synced);

    /// <summary>
    /// Looks at the drive from a thread of its own. The drive answers as fast as its
    /// storage does, and the thread of the user interface must never wait for that.
    /// </summary>
    private static Task<T> OnDrive<T>(Func<T> look) => Task.Run(look);

    private static void Capture(Window window, string name) => ScreenCapture.Save(window, "screenshots", name);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // still in use by a program that is ending; the temp folder is cleaned up by the system
        }
    }
}
