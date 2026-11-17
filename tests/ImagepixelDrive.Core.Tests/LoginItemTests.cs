namespace ImagepixelDrive.Core.Tests;

public class LoginItemTests
{
    private const string Program = "/Applications/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive";

    private static MacLoginItem NewItem() => new(Path.Combine(TestDrive.NewRoot(), "LaunchAgents"));

    [Fact]
    public void Login_item_is_a_launch_agent_that_starts_the_program_without_its_window()
    {
        var item = NewItem();
        Assert.False(item.IsEnabled);

        item.Enable(Program, "--minimized");

        Assert.True(item.IsEnabled);
        Assert.Equal("de.imagepixel.drive.plist", Path.GetFileName(item.FilePath));
        var agent = File.ReadAllText(item.FilePath);
        Assert.Contains("<string>de.imagepixel.drive</string>", agent);
        Assert.Contains($"<string>{Program}</string>", agent);
        Assert.Contains("<string>--minimized</string>", agent);
        Assert.Contains("<key>RunAtLoad</key>", agent);
        Assert.DoesNotContain("\r", agent);

        item.Disable();
        Assert.False(item.IsEnabled);
        item.Disable();
    }

    [Fact]
    public void Characters_with_a_meaning_in_the_file_are_escaped()
    {
        var item = NewItem();

        item.Enable("/Applications/Drive & <Co>.app/Contents/MacOS/ImagepixelDrive", "--minimized");

        Assert.Contains("<string>/Applications/Drive &amp; &lt;Co&gt;.app/Contents/MacOS/ImagepixelDrive</string>", File.ReadAllText(item.FilePath));
    }

    [Fact]
    public void A_program_that_was_moved_is_found_again()
    {
        var item = NewItem();

        // nothing to refresh while starting at login is turned off
        item.Refresh(Program, "--minimized");
        Assert.False(item.IsEnabled);

        item.Enable(Program, "--minimized");
        var moved = "/Users/kundin/Applications/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive";
        item.Refresh(moved, "--minimized");
        Assert.Contains($"<string>{moved}</string>", File.ReadAllText(item.FilePath));

        // a start from the disk image must not replace the place that works
        item.Refresh("/Volumes/Imagepixel Drive/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive", "--minimized");
        Assert.Contains($"<string>{moved}</string>", File.ReadAllText(item.FilePath));
    }

    [Theory]
    [InlineData(Program, true)]
    [InlineData("/Users/kundin/Desktop/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive", true)]
    [InlineData("/Volumes/Imagepixel Drive/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive", false)]
    [InlineData("/private/var/folders/ab/T/AppTranslocation/1234/d/Imagepixel Drive.app/Contents/MacOS/ImagepixelDrive", false)]
    public void Only_a_place_that_stays_can_be_started_at_login(string program, bool permanent)
    {
        Assert.Equal(permanent, NewItem().IsPermanentLocation(program));
    }
}
