using System.Text;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

public class EncryptionTests
{
    [SkippableFact]
    public async Task Names_and_content_are_encrypted_in_the_storage()
    {
        using var drive = await TestDrive.CreateAsync();
        const string Secret = "Geheimer Plan 4711: niemand ausser mir liest das.";

        await drive.PutAsync("Projekte/Angebot Mueller.txt", Secret);

        // readable through the drive
        Assert.Equal(["Projekte/Angebot Mueller.txt"], await drive.FilesAsync());
        Assert.Equal(Secret, await drive.Client.ReadTextAsync("Projekte/Angebot Mueller.txt"));

        // the storage holds neither the names nor the text
        var stored = Directory.EnumerateFiles(drive.StorageFolder, "*", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(stored);
        var relative = Path.GetRelativePath(drive.StorageFolder, file);
        Assert.DoesNotContain("Projekte", relative, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Angebot", relative, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mueller", relative, StringComparison.OrdinalIgnoreCase);

        var bytes = File.ReadAllBytes(file);
        Assert.True(bytes.Length > Secret.Length);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Geheimer")));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("4711")));

        var encrypted = Assert.Single(await drive.Client.ListEncryptedAsync());
        Assert.DoesNotContain("Angebot", encrypted.Path, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Probe_tells_an_empty_place_a_drive_and_a_wrong_password_apart()
    {
        using var drive = await TestDrive.CreateAsync();

        Assert.Equal(DriveState.Empty, await drive.Client.ProbeAsync());

        await drive.Client.InitializeAsync();
        Assert.Equal(DriveState.Ready, await drive.Client.ProbeAsync());

        var other = await RcloneClient.CreateAsync(drive.Rclone, drive.Storage, "ein-anderes-Passwort");
        Assert.Equal(DriveState.WrongPassword, await other.ProbeAsync());
    }

    [SkippableFact]
    public async Task Probe_accepts_a_drive_that_was_filled_without_the_application()
    {
        using var drive = await TestDrive.CreateAsync();

        // no check file: only what plain rclone would have written
        await drive.PutAsync("Notiz.txt", "mit rclone allein geschrieben");

        Assert.Equal(DriveState.Ready, await drive.Client.ProbeAsync());

        var other = await RcloneClient.CreateAsync(drive.Rclone, drive.Storage, "ein-anderes-Passwort");
        Assert.Equal(DriveState.WrongPassword, await other.ProbeAsync());
    }

    [SkippableFact]
    public async Task Settings_of_an_rclone_of_the_user_do_not_reach_the_drive()
    {
        using var drive = await TestDrive.CreateAsync();

        var startInfo = drive.Client.CreateStartInfo(["version"]);

        Assert.Equal("", startInfo.Environment["RCLONE_CONFIG"]);
        Assert.Equal("crypt", startInfo.Environment["RCLONE_CONFIG_IPDRIVE_TYPE"]);

        // the password is handed over in the notation of rclone, never as typed
        Assert.NotEqual(TestDrive.Password, startInfo.Environment["RCLONE_CONFIG_IPDRIVE_PASSWORD"]);
        Assert.DoesNotContain(startInfo.ArgumentList, a => a.Contains(TestDrive.Password, StringComparison.Ordinal));

        var result = await drive.Client.RunAsync(["config", "file"], null, null, CancellationToken.None);
        Assert.Contains("memory", result.Output + string.Join('\n', result.Log), StringComparison.OrdinalIgnoreCase);
    }
}
