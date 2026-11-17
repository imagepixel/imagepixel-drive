using System;
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
    }

    public string Version { get; } =
        "Version " + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?") + " · Vorabversion";

    /// <summary>WinFsp is only part of the application where it shows the drive.</summary>
    public bool ShowWinFsp => OperatingSystem.IsWindows();

    [RelayCommand]
    private void Back() => _main.CurrentPage = _previous;

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
