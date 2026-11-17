using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelDrive.Core;

namespace ImagepixelDrive.App.ViewModels;

/// <summary>
/// The overview: where the drive is and whether everything is stored, and the folders
/// that are kept on this computer as well.
/// </summary>
public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReachabilityInterval = TimeSpan.FromSeconds(30);

    private readonly DriveManager _manager;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _reachabilityChecked = DateTimeOffset.MinValue;
    private bool _polling;

    public DashboardViewModel(DriveManager manager, MainViewModel main)
    {
        _manager = manager;
        _main = main;
        _manager.Changed += OnManagerChanged;
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Refresh();
    }

    public string StorageName => _manager.Config.Storage is { } storage ? Texts.StorageName(storage) : "";

    /// <summary>Whether folders can be kept offline: not for a drive that lies on this computer anyway.</summary>
    public bool SupportsOfflineFolders => _manager.SupportsOfflineFolders;

    public bool IsOwnPlace => _manager.Config.Storage is { IsImagepixel: false };

    /// <summary>The folder a drive at a place of the customer lies in.</summary>
    public string OwnPlacePath => _manager.Config.Storage?.LocalPath ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsStarting), nameof(HasFailed), nameof(CanRetry))]
    public partial DrivePhase Phase { get; set; }

    public bool IsRunning => Phase == DrivePhase.Running;

    public bool IsStarting => Phase == DrivePhase.Starting;

    public bool HasFailed => Phase == DrivePhase.Failed;

    public bool CanRetry => Phase is DrivePhase.Failed or DrivePhase.Stopped;

    /// <summary>What the drive is doing, in a few words.</summary>
    [ObservableProperty]
    public partial string DriveStatus { get; set; } = "";

    /// <summary>Where to find the drive, or why it is not there.</summary>
    [ObservableProperty]
    public partial string DriveDetail { get; set; } = "";

    /// <summary>Whether everything written to the drive is stored.</summary>
    [ObservableProperty]
    public partial string UploadStatus { get; set; } = "";

    /// <summary>False while the storage does not answer. Only looked at for Imagepixel Storage.</summary>
    [ObservableProperty]
    public partial bool IsReachable { get; set; } = true;

    public ObservableCollection<OfflineFolderViewModel> OfflineFolders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOfflineProblem))]
    public partial string? OfflineProblem { get; set; }

    public bool HasOfflineProblem => !string.IsNullOrEmpty(OfflineProblem);

    /// <summary>Asks before the drive is disconnected from this computer.</summary>
    [ObservableProperty]
    public partial bool ConfirmDisconnect { get; set; }

    /// <summary>Shows the drive and starts looking after the offline folders.</summary>
    public async Task StartAsync()
    {
        _timer.Start();
        try
        {
            await _manager.StartAsync();
        }
        catch (Exception e)
        {
            DriveStatus = "Das Laufwerk konnte nicht geöffnet werden";
            DriveDetail = Texts.Error(e);
        }

        Refresh();
        await RefreshStatusAsync(force: true);
    }

    /// <summary>Looks up what changes without notice: files waiting for their upload, and whether the storage answers.</summary>
    public async Task RefreshStatusAsync(bool force = false)
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            var pending = await _manager.PendingUploadsAsync();
            UploadStatus = IsRunning ? Texts.PendingUploads(pending) : "";

            if (SupportsOfflineFolders && (force || DateTimeOffset.Now - _reachabilityChecked > ReachabilityInterval))
            {
                _reachabilityChecked = DateTimeOffset.Now;
                IsReachable = await _manager.IsReachableAsync();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or Core.Rclone.RcloneException)
        {
            // the drive is just going away; the next round tells
        }
        finally
        {
            _polling = false;
        }
    }

    [RelayCommand]
    private void OpenDrive()
    {
        if (_manager.Mount is { IsRunning: true } mount)
        {
            _main.OpenInFileManager(mount.RootPath);
        }
    }

    [RelayCommand]
    private Task RetryAsync() => StartAsync();

    [RelayCommand]
    private async Task AddOfflineFolderAsync()
    {
        OfflineProblem = null;
        if (_manager.Mount is not { IsRunning: true } mount)
        {
            OfflineProblem = "Das Laufwerk muss geöffnet sein, damit du einen Ordner darauf auswählen kannst.";
            return;
        }

        if (await _main.PickFolder("Ordner auf dem Laufwerk auswählen, der offline verfügbar sein soll", mount.RootPath) is not { } picked)
        {
            return;
        }

        try
        {
            await _manager.AddOfflineFolderAsync(_manager.ToRemotePath(picked));
        }
        catch (Exception e)
        {
            OfflineProblem = Texts.Error(e);
        }

        Refresh();
    }

    internal async Task RemoveOfflineFolderAsync(string remotePath)
    {
        await _manager.RemoveOfflineFolderAsync(remotePath);
        Refresh();
    }

    internal void SyncSoon(string remotePath, SyncMode mode) => _manager.SyncSoon(remotePath, mode);

    internal void Open(string path) => _main.OpenInFileManager(path);

    [RelayCommand]
    private void ShowAbout() => _main.ShowAbout();

    [RelayCommand]
    private void AskDisconnect() => ConfirmDisconnect = true;

    [RelayCommand]
    private void CancelDisconnect() => ConfirmDisconnect = false;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        ConfirmDisconnect = false;
        _timer.Stop();
        await _manager.DisconnectAsync();
        _main.ShowSetup();
    }

    private void OnManagerChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Brings the page up to date with the state of the drive and the offline folders.</summary>
    public void Refresh()
    {
        Phase = _manager.Phase;
        (DriveStatus, DriveDetail) = _manager.Phase switch
        {
            DrivePhase.Running when _manager.Mount is { } mount => (Texts.DriveReady(mount.Mountpoint), Texts.DriveWhere(mount.RootPath)),
            DrivePhase.Starting => ("Laufwerk wird geöffnet …", ""),
            DrivePhase.Failed => ("Das Laufwerk konnte nicht geöffnet werden", _manager.Problem is { } problem ? Texts.Error(problem) : ""),
            _ => ("Das Laufwerk ist geschlossen", ""),
        };
        if (!IsRunning)
        {
            UploadStatus = "";
        }

        // one entry per folder, kept across refreshes so that the list does not flicker
        var keepers = _manager.Keepers;
        foreach (var settings in _manager.Config.OfflineFolders)
        {
            var entry = OfflineFolders.FirstOrDefault(f => f.RemotePath == settings.RemotePath);
            if (entry is null)
            {
                entry = new OfflineFolderViewModel(this, settings.RemotePath, settings.LocalPath);
                OfflineFolders.Add(entry);
            }

            entry.Update(keepers.FirstOrDefault(k => k.Folder.RemotePath == settings.RemotePath));
        }

        foreach (var gone in OfflineFolders.Where(f => _manager.Config.OfflineFolders.All(s => s.RemotePath != f.RemotePath)).ToList())
        {
            OfflineFolders.Remove(gone);
        }

        OnPropertyChanged(nameof(HasOfflineFolders));
        OnPropertyChanged(nameof(StorageName));
        OnPropertyChanged(nameof(SupportsOfflineFolders));
        OnPropertyChanged(nameof(IsOwnPlace));
        OnPropertyChanged(nameof(OwnPlacePath));
    }

    public bool HasOfflineFolders => OfflineFolders.Count > 0;

    public void Dispose()
    {
        _timer.Stop();
        _manager.Changed -= OnManagerChanged;
    }
}

/// <summary>One folder that is kept on this computer, with the state of its sync.</summary>
public partial class OfflineFolderViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboard;
    private SyncMode _confirmMode;

    public OfflineFolderViewModel(DashboardViewModel dashboard, string remotePath, string localPath)
    {
        _dashboard = dashboard;
        RemotePath = remotePath;
        LocalPath = localPath;
    }

    /// <summary>The folder in the drive.</summary>
    public string RemotePath { get; }

    /// <summary>The folder on this computer.</summary>
    public string LocalPath { get; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>Whether the sync stopped and waits for the user.</summary>
    [ObservableProperty]
    public partial bool NeedsAttention { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    public partial string? Conflicts { get; set; }

    public bool HasConflicts => Conflicts is not null;

    /// <summary>What the button says that lets a stopped sync go ahead, if there is something to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string? ConfirmLabel { get; set; }

    public bool CanConfirm => ConfirmLabel is not null;

    internal void Update(OfflineKeeper? keeper)
    {
        var result = keeper?.LastResult;
        Status = Texts.SyncStatus(result, keeper?.LastRun, keeper?.IsSyncing ?? false, DateTimeOffset.Now);
        Conflicts = Texts.Conflicts(result);
        NeedsAttention = keeper is { IsSyncing: false } && result is { Outcome: not (SyncOutcome.Synced or SyncOutcome.Offline) };
        var confirmation = keeper is { IsSyncing: false } ? Texts.Confirmation(result) : null;
        ConfirmLabel = confirmation?.Label;
        _confirmMode = confirmation?.Mode ?? SyncMode.Normal;
    }

    [RelayCommand]
    private void Open() => _dashboard.Open(LocalPath);

    [RelayCommand]
    private void SyncNow() => _dashboard.SyncSoon(RemotePath, SyncMode.Normal);

    [RelayCommand]
    private void Confirm() => _dashboard.SyncSoon(RemotePath, _confirmMode);

    [RelayCommand]
    private Task RemoveAsync() => _dashboard.RemoveOfflineFolderAsync(RemotePath);
}
