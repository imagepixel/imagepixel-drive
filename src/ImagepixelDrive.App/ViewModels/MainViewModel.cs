using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImagepixelDrive.Core;

namespace ImagepixelDrive.App.ViewModels;

/// <summary>The window: shows the setup until a drive is connected, then the overview.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly DriveManager _manager;
    private DashboardViewModel? _dashboard;

    /// <param name="manager">The operations of the application.</param>
    /// <param name="cloudFolders">
    /// The cloud folders to offer as a place for a drive. Looked up on this computer when omitted.
    /// </param>
    public MainViewModel(DriveManager manager, IReadOnlyList<CloudFolder>? cloudFolders = null)
    {
        _manager = manager;
        CloudFolders = cloudFolders ?? CloudFolderDetector.Detect();

        // the notes on use and liability come first, once
        CurrentPage = !manager.HasAcceptedTerms(Texts.TermsVersion) ? new TermsViewModel(this)
            : manager.IsConnected ? Dashboard
            : new SetupViewModel(manager, this);
    }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    /// <summary>The folders of cloud storage services found on this computer.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders { get; }

    /// <summary>
    /// Lets the user choose a folder. Takes the title of the dialog and the folder it
    /// opens at, if any. Set by the window, which owns the dialog.
    /// </summary>
    public Func<string, string?, Task<string?>> PickFolder { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Opens a folder, a drive or a web page. Replaceable for tests.</summary>
    public Action<string> OpenInFileManager { get; set; } = path =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public DashboardViewModel Dashboard => _dashboard ??= new DashboardViewModel(_manager, this);

    /// <summary>Shows the drive right away if one is set up. Called once at the start of the application.</summary>
    public void StartIfConnected()
    {
        if (_manager.IsConnected)
        {
            _ = Dashboard.StartAsync();
        }
    }

    /// <summary>Records that the user confirmed the notes on use and liability, and goes on.</summary>
    public Task AcceptTermsAsync()
    {
        _manager.AcceptTerms(Texts.TermsVersion);
        if (_manager.IsConnected)
        {
            return ShowDashboardAsync();
        }

        CurrentPage = new SetupViewModel(_manager, this);
        return Task.CompletedTask;
    }

    /// <summary>Shows the overview and the drive.</summary>
    public Task ShowDashboardAsync()
    {
        CurrentPage = Dashboard;
        return Dashboard.StartAsync();
    }

    public void ShowAbout() => CurrentPage = new AboutViewModel(this, CurrentPage);

    /// <summary>Goes back to the setup, after the drive was disconnected.</summary>
    public void ShowSetup()
    {
        _dashboard?.Dispose();
        _dashboard = null;
        CurrentPage = new SetupViewModel(_manager, this);
    }

    /// <summary>Stops everything that runs in the background before the application exits.</summary>
    public async Task ShutdownAsync()
    {
        _dashboard?.Dispose();
        await _manager.StopAsync();
    }
}
