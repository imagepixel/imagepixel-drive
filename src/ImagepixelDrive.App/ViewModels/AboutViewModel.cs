using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImagepixelDrive.App.ViewModels;

/// <summary>Version, what the customer has to know, licence and the components the application is built on.</summary>
public partial class AboutViewModel : ViewModelBase
{
    public const string SourceUrl = "https://github.com/imagepixel/imagepixel-drive";
    public const string EmergencyUrl = "https://github.com/imagepixel/imagepixel-drive#reading-a-drive-without-this-application";
    public const string TermsUrl = "https://github.com/imagepixel/imagepixel-drive/blob/main/NUTZUNGSHINWEISE.md";
    public const string RcloneUrl = "https://rclone.org";
    public const string WinFspUrl = "https://github.com/winfsp/winfsp";

    private readonly MainViewModel _main;
    private readonly ViewModelBase _previous;

    public AboutViewModel(MainViewModel main, ViewModelBase previous)
    {
        _main = main;
        _previous = previous;
        StartAtLogin = main.LoginItem is { IsEnabled: true };
    }

    public string Version { get; } =
        "Version " + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?") + " · Vorabversion";

    /// <summary>WinFsp is only part of the application where it shows the drive.</summary>
    public bool ShowWinFsp => OperatingSystem.IsWindows();

    /// <summary>
    /// On macOS the application itself registers to start at login. On Windows the
    /// setup program offers this, so there is nothing to choose here.
    /// </summary>
    public bool ShowStartAtLogin => _main.LoginItem is not null;

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    /// <summary>What keeps the application from starting at login, if anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartAtLoginProblem))]
    public partial string? StartAtLoginProblem { get; set; }

    public bool HasStartAtLoginProblem => StartAtLoginProblem is not null;

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_main.LoginItem is not { } loginItem || value == loginItem.IsEnabled)
        {
            return;
        }

        if (value)
        {
            StartAtLoginProblem = _main.EnableStartAtLogin();
            if (StartAtLoginProblem is not null)
            {
                // once the box has taken the click, it is cleared again
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartAtLogin = false);
            }
        }
        else
        {
            loginItem.Disable();
            StartAtLoginProblem = null;
        }
    }

    [RelayCommand]
    private void Back()
    {
        // the overview tells whether the application starts at login, which may have changed here
        (_previous as DashboardViewModel)?.RefreshStartAtLogin();
        _main.CurrentPage = _previous;
    }

    [RelayCommand]
    private void OpenLink(string url) => _main.OpenInFileManager(url);
}

/// <summary>Converters used by the views.</summary>
public static class Converters
{
    /// <summary>Dims a section that cannot be used yet.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> EnabledOpacity =
        new(enabled => enabled ? 1.0 : 0.5);
}
