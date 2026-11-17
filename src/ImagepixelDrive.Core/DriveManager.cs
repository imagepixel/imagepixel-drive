using System.Text.RegularExpressions;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core;

/// <summary>Where the drive stands.</summary>
public enum DrivePhase
{
    /// <summary>The drive is not shown.</summary>
    Stopped,

    /// <summary>The drive is being shown.</summary>
    Starting,

    /// <summary>The drive is shown and can be used.</summary>
    Running,

    /// <summary>The drive could not be shown; see <see cref="DriveManager.Problem"/>.</summary>
    Failed,
}

/// <summary>A folder cannot be used for what the user chose it for; the reason tells why.</summary>
public sealed class FolderRuleException(FolderRuleException.Rule reason, string folder, string? other = null)
    : InvalidOperationException($"The folder {folder} cannot be used: {reason}.")
{
    public enum Rule
    {
        /// <summary>The whole drive was chosen, where a folder in it is needed.</summary>
        WholeDrive,

        /// <summary>The folder does not lie on the drive.</summary>
        NotOnDrive,

        /// <summary>The folder, or one around or inside it, is kept offline already.</summary>
        OfflineAlready,

        /// <summary>The folder lies inside or around one the application uses itself.</summary>
        UsedByApplication,
    }

    public Rule Reason { get; } = reason;

    public string Folder { get; } = folder;

    /// <summary>The folder the chosen one collides with, if there is one.</summary>
    public string? Other { get; } = other;
}

/// <summary>
/// The operations of the application: connecting to a drive, showing it, and keeping
/// folders of it on this computer. Settings and secrets are stored so that the next
/// start carries on where the last one ended.
/// </summary>
public sealed partial class DriveManager : IAsyncDisposable
{
    /// <summary>The name of the folder a drive gets inside a folder the user chose.</summary>
    public const string DriveFolderName = "Imagepixel Drive";

    private readonly ConfigStore _store;
    private readonly ISecretStore _secrets;
    private readonly string _rclonePath;
    private readonly string _dataDirectory;
    private readonly List<OfflineKeeper> _keepers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StorageStatusService _storageStatus;
    private RcloneClient? _client;

    /// <param name="store">Where the settings are kept.</param>
    /// <param name="secrets">Where the password and the access key are kept.</param>
    /// <param name="rclonePath">The rclone program.</param>
    /// <param name="dataDirectory">
    /// A folder on this computer for files on their way and the records of the syncs.
    /// </param>
    /// <param name="offlineRoot">The folder that receives the offline folders.</param>
    /// <param name="http">
    /// Reaches the service of Imagepixel that tells how much storage is booked and used.
    /// The tests pass one that answers by itself.
    /// </param>
    public DriveManager(
        ConfigStore store,
        ISecretStore secrets,
        string rclonePath,
        string? dataDirectory = null,
        string? offlineRoot = null,
        HttpClient? http = null)
    {
        _store = store;
        _secrets = secrets;
        _rclonePath = rclonePath;
        _dataDirectory = dataDirectory ?? DefaultDataDirectory;
        OfflineRoot = offlineRoot ?? DefaultOfflineRoot;
        _storageStatus = new StorageStatusService(http ?? SharedHttp.Value);
        Config = store.Load();
    }

    // one for the whole application, as its makers recommend
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient());

    /// <summary>The folder for what the application keeps on this computer besides its settings.</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImagepixelDrive");

    /// <summary>The folder in the profile of the user that receives the offline folders.</summary>
    public static string DefaultOfflineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Imagepixel Drive Offline");

    public AppConfig Config { get; private set; }

    public string OfflineRoot { get; }

    /// <summary>
    /// Where the drive is shown. Null in normal use, which means the usual place, see
    /// <see cref="DriveMount.DefaultMountpoint"/>; the tests name a place of their own.
    /// </summary>
    public string? Mountpoint { get; set; }

    /// <summary>The time between two syncs of an offline folder when nothing changes on this computer.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Whether a drive is set up and its password is known.</summary>
    public bool IsConnected => Config.Storage is not null && _secrets.Get(SecretNames.DrivePassword) is not null;

    /// <summary>
    /// Whether folders can be kept offline. A drive in a folder of this computer is
    /// there without a connection anyway.
    /// </summary>
    public bool SupportsOfflineFolders => Config.Storage is { IsImagepixel: true };

    public DrivePhase Phase { get; private set; } = DrivePhase.Stopped;

    /// <summary>Why the drive could not be shown, when <see cref="Phase"/> says it failed.</summary>
    public Exception? Problem { get; private set; }

    /// <summary>The drive while it is shown.</summary>
    public DriveMount? Mount { get; private set; }

    /// <summary>The offline folders that are being kept in sync.</summary>
    public IReadOnlyList<OfflineKeeper> Keepers
    {
        get
        {
            lock (_keepers)
            {
                return [.. _keepers];
            }
        }
    }

    /// <summary>Raised on a background thread whenever the drive or an offline folder changes its state.</summary>
    public event Action? Changed;

    /// <summary>
    /// The folder a drive lies in when the user chooses a place on this computer: the
    /// chosen folder itself if it is, or is meant to be, a drive, otherwise a folder
    /// of its own inside it. A drive never shares a folder with other files.
    /// </summary>
    public static string ResolveDriveFolder(string chosen)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen.Trim()));
        if (string.Equals(Path.GetFileName(full), DriveFolderName, FolderRules.PathComparison) || LooksLikeDrive(full))
        {
            return full;
        }

        return Path.Combine(full, DriveFolderName);
    }

    /// <summary>Whether a folder holds the encrypted files of a drive and nothing else.</summary>
    public static bool LooksLikeDrive(string folder)
    {
        try
        {
            var names = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList();
            return names.Count > 0 && names.All(name => EncryptedName().IsMatch(name!));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Connects to a drive, or creates one where there is none yet. Returns what was
    /// found; the settings are only stored when the drive is ready to use.
    /// </summary>
    /// <param name="storage">The place of the drive. A folder of this computer is taken as it is, see <see cref="ResolveDriveFolder"/>.</param>
    /// <param name="password">The password of the drive.</param>
    /// <param name="secretAccessKey">The secret of the access key, for Imagepixel Storage.</param>
    /// <param name="createIfMissing">Whether a drive is created if the place is empty.</param>
    /// <param name="apiToken">The token for the service that tells how much storage is used, if there is one.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    public async Task<DriveState> ConnectAsync(
        StorageSettings storage,
        string password,
        string? secretAccessKey,
        bool createIfMissing,
        string? apiToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!storage.IsImagepixel)
        {
            storage.LocalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage.LocalPath));
            RequireFreeFolder(storage.LocalPath);
            if (createIfMissing)
            {
                Directory.CreateDirectory(storage.LocalPath);
            }
            else if (!Directory.Exists(storage.LocalPath))
            {
                return DriveState.Empty;
            }
        }

        var client = await RcloneClient.CreateAsync(_rclonePath, storage.ToStorage(secretAccessKey), password, cancellationToken);
        var state = await client.ProbeAsync(cancellationToken);
        if (state == DriveState.Empty && createIfMissing)
        {
            await client.InitializeAsync(cancellationToken);
            state = DriveState.Ready;
        }

        if (state != DriveState.Ready)
        {
            return state;
        }

        await StopAsync();
        _client = client;
        Config.Storage = storage;
        Config.OfflineFolders = [];
        _store.Save(Config);
        _secrets.Set(SecretNames.DrivePassword, password);
        _secrets.Set(SecretNames.ObscuredDrivePassword, client.Access.ObscuredPassword);
        _secrets.Set(SecretNames.SecretAccessKey, storage.IsImagepixel ? secretAccessKey : null);
        _secrets.Set(SecretNames.ApiToken, storage.IsImagepixel ? apiToken : null);
        return state;
    }

    /// <summary>Whether the user confirmed this version of the notes on use and liability.</summary>
    public bool HasAcceptedTerms(string version) => Config.TermsAccepted == version;

    /// <summary>Records that the user confirmed the notes on use and liability in this version.</summary>
    public void AcceptTerms(string version)
    {
        Config.TermsAccepted = version;
        Config.TermsAcceptedAt = DateTimeOffset.Now;
        _store.Save(Config);
    }

    /// <summary>
    /// Forgets the drive on this computer. Nothing stored is deleted: the drive stays
    /// where it is stored, and the offline folders stay as ordinary folders. What the
    /// drive kept on this computer for files on their way is removed, because those
    /// are copies that are not encrypted; a file that has not reached the storage by
    /// then is lost.
    /// </summary>
    public async Task DisconnectAsync()
    {
        var storage = Config.Storage;
        await StopAsync();
        foreach (var folder in Config.OfflineFolders)
        {
            ForgetSync(folder);
        }

        if (storage is not null)
        {
            await ForgetCacheAsync(storage.ToStorage(null));
        }

        _client = null;
        Config.Storage = null;
        Config.OfflineFolders = [];
        _store.Save(Config);
        _secrets.Set(SecretNames.DrivePassword, null);
        _secrets.Set(SecretNames.ObscuredDrivePassword, null);
        _secrets.Set(SecretNames.SecretAccessKey, null);
        _secrets.Set(SecretNames.ApiToken, null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Shows the drive and starts keeping the offline folders in sync. If the drive
    /// cannot be shown, the offline folders are looked after all the same.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Phase is DrivePhase.Starting or DrivePhase.Running || Config.Storage is null)
            {
                return;
            }

            var client = await ClientAsync(cancellationToken);
            Phase = DrivePhase.Starting;
            Problem = null;
            Changed?.Invoke();

            foreach (var folder in Config.OfflineFolders)
            {
                StartKeeper(client, folder);
            }

            try
            {
                IReadOnlyList<string> places = Mountpoint is not null ? [Mountpoint] : DriveMount.Mountpoints(Config.DriveLetter);
                if (places.Count == 0)
                {
                    throw new IOException("No drive letter is free.");
                }

                var options = DriveMountOptions.For(
                    client.Access.Storage,
                    CacheDirectory(client.Access.Storage),
                    Path.Combine(_dataDirectory, "logs", "drive.log"));

                // a place that turns out to be taken is no reason to give up: the next one is tried
                DriveMount? mount = null;
                var mountpoint = places[0];
                for (var attempt = 0; mount is null; attempt++)
                {
                    mountpoint = places[attempt];
                    try
                    {
                        mount = await DriveMount.StartAsync(client, mountpoint, options, cancellationToken);
                    }
                    catch (RcloneException) when (attempt + 1 < Math.Min(places.Count, 4))
                    {
                        // tried again with the next place
                    }
                }

                mount.Exited += (_, _) => OnMountExited(mount);
                Mount = mount;
                Phase = DrivePhase.Running;
                if (OperatingSystem.IsWindows() && Mountpoint is null && Config.DriveLetter != mountpoint)
                {
                    Config.DriveLetter = mountpoint;
                    _store.Save(Config);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Problem = e;
                Phase = DrivePhase.Failed;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Removes the drive and stops the syncs. Running syncs are given time to finish.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            OfflineKeeper[] keepers;
            lock (_keepers)
            {
                keepers = [.. _keepers];
                _keepers.Clear();
            }

            foreach (var keeper in keepers)
            {
                await keeper.DisposeAsync();
            }

            if (Mount is { } mount)
            {
                Mount = null;
                await mount.DisposeAsync();
            }

            Phase = DrivePhase.Stopped;
            Problem = null;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>The number of files written to the drive that are not stored yet. Zero while the drive is not shown.</summary>
    public async Task<int> PendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Mount is { IsRunning: true } mount ? await mount.PendingUploadsAsync(cancellationToken) : 0;
        }
        catch (Exception e) when (e is HttpRequestException or RcloneException)
        {
            return 0;
        }
    }

    /// <summary>Whether the storage answers at the moment.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default) =>
        Config.Storage is not null && await (await ClientAsync(cancellationToken)).IsReachableAsync(cancellationToken);

    /// <summary>
    /// How much storage is booked at Imagepixel and how much of it is used. Null when
    /// there are no figures, which is always the case for a place of the customer's own.
    /// </summary>
    public async Task<StorageStatus?> GetStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Config.Storage is not { IsImagepixel: true } storage)
        {
            return null;
        }

        var client = await ClientAsync(cancellationToken);
        var result = await _storageStatus.GetAsync(
            storage.ApiUrl,
            _secrets.Get(SecretNames.ApiToken),
            token => client.ReadBucketTextAsync(StorageStatusDocument.ObjectKey, token),
            cancellationToken);

        // The status file can announce the service. From then on the service is asked first.
        if (result.ApiUrl is { } apiUrl && result.ApiToken is { } apiToken
            && ReferenceEquals(Config.Storage, storage)
            && (apiUrl != storage.ApiUrl || apiToken != _secrets.Get(SecretNames.ApiToken)))
        {
            storage.ApiUrl = apiUrl;
            _secrets.Set(SecretNames.ApiToken, apiToken);
            _store.Save(Config);
        }

        return result.Status;
    }

    /// <summary>
    /// Turns a folder the user picked on the drive into its path in the drive. Throws
    /// <see cref="FolderRuleException"/> if the folder is not on the drive or is the drive itself.
    /// </summary>
    public string ToRemotePath(string pickedFolder)
    {
        var root = Mount?.RootPath ?? throw new FolderRuleException(FolderRuleException.Rule.NotOnDrive, pickedFolder);
        var full = Path.GetFullPath(pickedFolder);
        if (!FolderRules.IsInside(full, root))
        {
            throw new FolderRuleException(FolderRuleException.Rule.NotOnDrive, pickedFolder);
        }

        var relative = DriveAccess.Normalize(Path.GetRelativePath(root, full));
        return relative is "" or "."
            ? throw new FolderRuleException(FolderRuleException.Rule.WholeDrive, pickedFolder)
            : relative;
    }

    /// <summary>
    /// Keeps a folder of the drive on this computer from now on. The folder on this
    /// computer is created below <see cref="OfflineRoot"/> and filled by the first sync.
    /// </summary>
    /// <param name="remotePath">The folder in the drive.</param>
    public async Task<OfflineFolderSettings> AddOfflineFolderAsync(string remotePath)
    {
        var remote = DriveAccess.Normalize(remotePath);
        if (remote.Length == 0)
        {
            throw new FolderRuleException(FolderRuleException.Rule.WholeDrive, remotePath);
        }

        // a folder inside an offline folder is offline already; one around it would sync the same files twice
        if (Config.OfflineFolders.FirstOrDefault(f => Nested(f.RemotePath, remote)) is { } existing)
        {
            throw new FolderRuleException(FolderRuleException.Rule.OfflineAlready, remote, existing.RemotePath);
        }

        var folder = new OfflineFolderSettings
        {
            RemotePath = remote,
            LocalPath = Path.Combine(OfflineRoot, remote.Replace('/', Path.DirectorySeparatorChar)),
        };
        Config.OfflineFolders.Add(folder);
        _store.Save(Config);

        if (Phase is DrivePhase.Running or DrivePhase.Failed or DrivePhase.Starting)
        {
            StartKeeper(await ClientAsync(CancellationToken.None), folder);
        }

        Changed?.Invoke();
        return folder;
    }

    /// <summary>
    /// Stops keeping a folder on this computer. Its files stay where they are, as an
    /// ordinary folder that is no longer synced; nothing changes in the drive.
    /// </summary>
    public async Task RemoveOfflineFolderAsync(string remotePath)
    {
        var remote = DriveAccess.Normalize(remotePath);
        if (Config.OfflineFolders.FirstOrDefault(f => f.RemotePath == remote) is not { } folder)
        {
            return;
        }

        OfflineKeeper? keeper;
        lock (_keepers)
        {
            keeper = _keepers.FirstOrDefault(k => k.Folder.RemotePath == remote);
            if (keeper is not null)
            {
                _keepers.Remove(keeper);
            }
        }

        if (keeper is not null)
        {
            await keeper.DisposeAsync();
        }

        Config.OfflineFolders.Remove(folder);
        _store.Save(Config);
        ForgetSync(folder);
        Changed?.Invoke();
    }

    /// <summary>Asks for a sync of an offline folder now, see <see cref="OfflineKeeper.SyncSoon"/>.</summary>
    public void SyncSoon(string remotePath, SyncMode mode = SyncMode.Normal)
    {
        var remote = DriveAccess.Normalize(remotePath);
        Keepers.FirstOrDefault(k => k.Folder.RemotePath == remote)?.SyncSoon(mode);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private async Task<RcloneClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        var settings = Config.Storage ?? throw new InvalidOperationException("No drive is set up.");
        var password = _secrets.Get(SecretNames.DrivePassword) ?? throw new InvalidOperationException("The password of the drive is not known.");
        var storage = settings.ToStorage(_secrets.Get(SecretNames.SecretAccessKey));

        // The drive is given the text rclone knows it by since it was set up, so that
        // rclone finds the files again that were on their way when it ended.
        if (_secrets.Get(SecretNames.ObscuredDrivePassword) is { Length: > 0 } obscured)
        {
            return _client = RcloneClient.Create(_rclonePath, storage, obscured);
        }

        var client = await RcloneClient.CreateAsync(_rclonePath, storage, password, cancellationToken);
        _secrets.Set(SecretNames.ObscuredDrivePassword, client.Access.ObscuredPassword);
        return _client = client;
    }

    /// <summary>
    /// The folder rclone keeps files of a drive in while they are on their way to the
    /// storage. They lie there as they are, not encrypted.
    /// </summary>
    private string CacheDirectory(DriveStorage storage) => Path.Combine(_dataDirectory, "cache", storage.Id);

    private async Task ForgetCacheAsync(DriveStorage storage)
    {
        var cache = CacheDirectory(storage);
        for (var attempt = 0; attempt < 5 && Directory.Exists(cache); attempt++)
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // rclone has just ended and the system is still letting go of a file
                await Task.Delay(300);
            }
        }
    }

    private void StartKeeper(RcloneClient client, OfflineFolderSettings settings)
    {
        var folder = new OfflineFolder(settings.RemotePath, settings.LocalPath);
        lock (_keepers)
        {
            if (_keepers.Any(k => k.Folder.RemotePath == folder.RemotePath))
            {
                return;
            }

            var keeper = new OfflineKeeper(new OfflineSync(client, folder, StateDirectory(settings)), SyncInterval);
            keeper.Started += _ => Changed?.Invoke();
            keeper.Completed += (sender, result) =>
            {
                // the drive learns at once what the sync stored behind its back
                if (result.ChangedStorage && Mount is { IsRunning: true } mount)
                {
                    mount.RefreshAsync(sender.Folder.RemotePath).ContinueWith(
                        t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }

                Changed?.Invoke();
            };
            _keepers.Add(keeper);
            keeper.Start();
        }
    }

    private void OnMountExited(DriveMount mount)
    {
        if (ReferenceEquals(Mount, mount))
        {
            // rclone ended by itself, for example because it was ended from outside
            Mount = null;
            Phase = DrivePhase.Failed;
            Problem = new IOException("The drive was closed unexpectedly.");
            Changed?.Invoke();
        }
    }

    private string StateDirectory(OfflineFolderSettings folder) =>
        Path.Combine(_dataDirectory, "sync", OfflineSync.StateName(new OfflineFolder(folder.RemotePath, folder.LocalPath)));

    /// <summary>Removes the record of the syncs of a folder and its marker on this computer.</summary>
    private void ForgetSync(OfflineFolderSettings folder)
    {
        try
        {
            var state = StateDirectory(folder);
            if (Directory.Exists(state))
            {
                Directory.Delete(state, recursive: true);
            }

            var marker = Path.Combine(folder.LocalPath, OfflineSync.MarkerName);
            if (File.Exists(marker))
            {
                File.SetAttributes(marker, FileAttributes.Normal);
                File.Delete(marker);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // leftovers do no harm: a folder that is added again starts with a first sync
        }
    }

    /// <summary>A folder for a drive must not collide with the folders the application works with.</summary>
    private void RequireFreeFolder(string folder)
    {
        string?[] own =
        [
            OfflineRoot,
            _dataDirectory,
            _store.Directory,
            OperatingSystem.IsWindows() ? null : Mountpoint ?? DriveMount.DefaultMountpoint(),
        ];
        if (own.FirstOrDefault(o => o is not null && FolderRules.Overlap(o, folder)) is { } collision)
        {
            throw new FolderRuleException(FolderRuleException.Rule.UsedByApplication, folder, collision);
        }
    }

    private static bool Nested(string first, string second) =>
        first == second
        || first.StartsWith(second + "/", StringComparison.Ordinal)
        || second.StartsWith(first + "/", StringComparison.Ordinal);

    // names as the encryption of rclone writes them: lower case letters a to v and digits
    [GeneratedRegex("^[0-9a-v]{16,}$")]
    private static partial Regex EncryptedName();
}
