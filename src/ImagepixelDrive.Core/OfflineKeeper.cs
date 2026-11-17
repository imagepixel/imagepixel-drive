namespace ImagepixelDrive.Core;

/// <summary>
/// Keeps an offline folder in sync while the application runs: shortly after
/// something changes in the folder on this computer, and at a regular interval to
/// fetch what changed in the storage. Without a network connection the syncs end as
/// <see cref="SyncOutcome.Offline"/> and simply continue once the storage answers again.
/// </summary>
public sealed class OfflineKeeper : IAsyncDisposable
{
    private readonly OfflineSync _sync;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _settleTime;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _abort = new();
    private FileSystemWatcher? _watcher;
    private Task? _loop;
    private int _nextMode = (int)SyncMode.Normal;

    /// <param name="sync">The folder to keep in sync.</param>
    /// <param name="interval">The time between two syncs when nothing changes on this computer.</param>
    /// <param name="settleTime">
    /// The time to wait after a change on this computer, so that a program that
    /// writes several files is done before the sync starts.
    /// </param>
    public OfflineKeeper(OfflineSync sync, TimeSpan interval, TimeSpan? settleTime = null)
    {
        _sync = sync;
        _interval = interval;
        _settleTime = settleTime ?? TimeSpan.FromSeconds(3);
    }

    public OfflineFolder Folder => _sync.Folder;

    /// <summary>The result of the last sync, once there was one.</summary>
    public SyncResult? LastResult { get; private set; }

    /// <summary>When the last sync ended.</summary>
    public DateTimeOffset? LastRun { get; private set; }

    /// <summary>Raised after every sync, on a background thread.</summary>
    public event Action<OfflineKeeper, SyncResult>? Completed;

    /// <summary>Starts with a first sync and keeps going until disposed.</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Whether a sync is running at the moment.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>Raised when a sync starts, on a background thread.</summary>
    public event Action<OfflineKeeper>? Started;

    /// <summary>
    /// Asks for a sync now instead of at the end of the interval. A mode other than
    /// the normal one applies to that one sync only: it is how the user confirms a
    /// sync that stopped to ask.
    /// </summary>
    public void SyncSoon(SyncMode mode = SyncMode.Normal)
    {
        if (mode != SyncMode.Normal)
        {
            Interlocked.Exchange(ref _nextMode, (int)mode);
        }

        Signal();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SyncResult result;
                var mode = (SyncMode)Interlocked.Exchange(ref _nextMode, (int)SyncMode.Normal);
                IsSyncing = true;
                Started?.Invoke(this);
                try
                {
                    // a running sync is not cut off when the keeper is asked to stop
                    result = await _sync.RunAsync(mode, _abort.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    result = new SyncResult(SyncOutcome.Failed, false, 0, 0, 0, 0, 0, [], [e.Message], TimeSpan.Zero);
                }
                finally
                {
                    IsSyncing = false;
                }

                LastResult = result;
                LastRun = DateTimeOffset.Now;
                Watch();

                // What the sync itself wrote into the folder raised change events too.
                // They are dropped; a change of the user in the same moment is found by
                // the next sync at the latest.
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                Drain();
                Completed?.Invoke(this, result);

                if (await _changed.WaitAsync(_interval, cancellationToken))
                {
                    // wait until the folder has been quiet for a moment
                    do
                    {
                        await Task.Delay(_settleTime, cancellationToken);
                    }
                    while (Drain());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    /// <summary>Watches the folder on this computer once it exists.</summary>
    private void Watch()
    {
        if (_watcher is not null || !Directory.Exists(_sync.Folder.LocalPath))
        {
            return;
        }

        var watcher = new FileSystemWatcher(_sync.Folder.LocalPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Signal();
        watcher.Created += (_, _) => Signal();
        watcher.Deleted += (_, _) => Signal();
        watcher.Renamed += (_, _) => Signal();
        watcher.Error += (_, _) => Signal();
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void Signal()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // a sync is asked for already
        }
        catch (ObjectDisposedException)
        {
            // stopped
        }
    }

    private bool Drain() => _changed.Wait(0);

    /// <summary>
    /// Stops keeping the folder in sync. A sync that is running gets a minute to
    /// finish; after that it is ended and picked up again by the next run.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _watcher?.Dispose();
        if (_loop is not null)
        {
            if (await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromMinutes(1))) != _loop)
            {
                _abort.Cancel();
            }

            await _loop;
        }

        _stop.Dispose();
        _abort.Dispose();
        _changed.Dispose();
    }
}
