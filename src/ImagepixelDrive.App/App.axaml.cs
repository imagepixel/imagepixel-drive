using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ImagepixelDrive.App.ViewModels;
using ImagepixelDrive.App.Views;
using ImagepixelDrive.Core;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.App;

public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _exiting;

    /// <summary>Set by <see cref="Program"/> when the window should stay hidden at start.</summary>
    internal static bool StartMinimized { get; set; }

    /// <summary>Set by <see cref="Program"/>; tells when another start asks for the window.</summary>
    internal static SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The application keeps running in the notification area when its window
            // is closed, so that the drive stays and the offline folders are synced.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var manager = new DriveManager(
                new ConfigStore(),
                SecretStore.Create(),
                RcloneClient.FindRclone() ?? "rclone");

            _viewModel = new MainViewModel(manager);
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += (_, e) =>
            {
                if (!_exiting)
                {
                    e.Cancel = true;
                    _window.Hide();
                }
            };

            // "Quit" in the menu or the Dock of macOS, and logging off on either system,
            // end the application: the window must close then instead of hiding, or it
            // would keep the computer from shutting down. The drive is told to go;
            // that is not waited for.
            desktop.ShutdownRequested += (_, _) =>
            {
                if (!_exiting)
                {
                    _exiting = true;
                    _ = _viewModel.ShutdownAsync();
                }
            };

            // The main window of the lifetime is shown at start. Leaving it unset keeps
            // the application in the notification area until the user opens it.
            if (!StartMinimized)
            {
                desktop.MainWindow = _window;
            }

            CreateTrayIcon(desktop);
            Instance?.OnShowRequested(() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowWindow));

            // macOS: a click on the icon in the Dock brings the window back
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                    {
                        ShowWindow();
                    }
                };
            }

            // a drive that is set up is shown as soon as the application starts
            _viewModel.StartIfConnected();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Öffnen");
        open.Click += (_, _) => ShowWindow();

        var exit = new NativeMenuItem("Beenden");
        exit.Click += async (_, _) =>
        {
            _exiting = true;
            if (_viewModel is not null)
            {
                // remove the drive before leaving; what is not stored yet waits for the next start
                await _viewModel.ShutdownAsync();
            }

            desktop.Shutdown();
        };

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ImagepixelDrive/Assets/app.ico"))),
            ToolTipText = Texts.ProductName,
            Menu = new NativeMenu { open, new NativeMenuItemSeparator(), exit },
        };
        tray.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, [tray]);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }
}
