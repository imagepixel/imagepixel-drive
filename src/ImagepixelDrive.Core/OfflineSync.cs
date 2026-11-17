using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core;

/// <summary>A folder of the drive that is kept on this computer as well.</summary>
/// <param name="RemotePath">The folder in the drive, with forward slashes, starting at the top of the drive.</param>
/// <param name="LocalPath">The folder on this computer that holds the same files.</param>
public sealed record OfflineFolder(string RemotePath, string LocalPath);

/// <summary>How a sync of an offline folder ended.</summary>
public enum SyncOutcome
{
    /// <summary>Both sides hold the same files now.</summary>
    Synced,

    /// <summary>The storage cannot be reached. Nothing was changed; the folder on this computer stays usable.</summary>
    Offline,

    /// <summary>
    /// More than half of the files were deleted on one side. Nothing was changed, so
    /// that a mistake does not empty the other side. Run again with
    /// <see cref="SyncMode.AcceptAll"/> if the deletion was meant.
    /// </summary>
    TooManyDeletes,

    /// <summary>Every file looks changed on one side. Nothing was changed; see <see cref="TooManyDeletes"/>.</summary>
    AllFilesChanged,

    /// <summary>
    /// The folder is missing on one side, or is no longer marked as an offline folder
    /// there. Nothing was changed.
    /// </summary>
    FolderMissing,

    /// <summary>The record of the last sync is unusable. <see cref="SyncMode.Repair"/> builds it anew.</summary>
    NeedsRepair,

    /// <summary>The sync failed for another reason; see the errors.</summary>
    Failed,
}

/// <summary>How a sync is run.</summary>
public enum SyncMode
{
    /// <summary>Carries the changes of each side to the other.</summary>
    Normal,

    /// <summary>Like <see cref="Normal"/>, but also when very many files were deleted or changed.</summary>
    AcceptAll,

    /// <summary>
    /// Brings both sides together anew: every file that exists on one side only is
    /// copied to the other, and of two differing files the newer one wins. Nothing is
    /// deleted, so files deleted since the last sync come back.
    /// </summary>
    Repair,
}

/// <summary>What a sync of an offline folder did.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="FirstRun">Whether both sides were brought together for the first time.</param>
/// <param name="Copied">The number of files copied in either direction.</param>
/// <param name="Uploaded">The number of files sent to the storage. Not counted on a first run.</param>
/// <param name="Downloaded">The number of files fetched from the storage. Not counted on a first run.</param>
/// <param name="DeletedHere">The number of files deleted on this computer because they were deleted in the storage.</param>
/// <param name="DeletedInStorage">The number of files deleted in the storage because they were deleted on this computer.</param>
/// <param name="Conflicts">
/// Files changed on both sides. The newer version kept the name; the other one was
/// kept under the names listed here.
/// </param>
/// <param name="Errors">What rclone reported when the sync did not succeed.</param>
/// <param name="Duration">How long the sync took.</param>
public sealed record SyncResult(
    SyncOutcome Outcome,
    bool FirstRun,
    int Copied,
    int Uploaded,
    int Downloaded,
    int DeletedHere,
    int DeletedInStorage,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Errors,
    TimeSpan Duration)
{
    /// <summary>Whether the sync changed anything on either side.</summary>
    public bool ChangedAnything => Copied + DeletedHere + DeletedInStorage + Conflicts.Count > 0;

    /// <summary>Whether the storage holds something else than before the sync.</summary>
    public bool ChangedStorage => FirstRun ? Copied > 0 : Uploaded + DeletedInStorage + Conflicts.Count > 0;
}

/// <summary>
/// Keeps one folder of the drive and a folder on this computer the same, in both
/// directions. The folder on this computer holds ordinary files: they can be read and
/// changed without a network connection, and the changes are carried over with the
/// next sync. The work is done by "rclone bisync".
/// </summary>
public sealed partial class OfflineSync
{
    /// <summary>
    /// A small file that marks a folder as an offline folder, on both sides. A sync
    /// only runs while both sides have it. If one side has lost the whole folder, it
    /// is missing there, and the sync stops instead of deleting the other side.
    /// </summary>
    public const string MarkerName = ".imagepixel-offline";

    /// <summary>What a file gets added to its name when it lost a conflict: "Plan.Konflikt1.txt".</summary>
    public const string ConflictSuffix = "Konflikt";

    private const string MarkerContent = "Imagepixel Drive\n";
    private const int ExitCritical = 7;

    private readonly RcloneClient _client;
    private readonly string _stateDirectory;

    /// <param name="client">Access to the drive.</param>
    /// <param name="folder">The pair of folders to keep the same.</param>
    /// <param name="stateDirectory">
    /// A folder of its own for the record of the last sync. Removing it makes the next
    /// sync a first run.
    /// </param>
    public OfflineSync(RcloneClient client, OfflineFolder folder, string stateDirectory)
    {
        _client = client;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        Folder = folder with
        {
            RemotePath = DriveAccess.Normalize(folder.RemotePath),
            LocalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.LocalPath)),
        };

        if (Folder.RemotePath.Length == 0)
        {
            throw new ArgumentException("The whole drive cannot be an offline folder; choose a folder in it.", nameof(folder));
        }
    }

    public OfflineFolder Folder { get; }

    /// <summary>Whether both sides have been brought together before.</summary>
    public bool IsInitialized =>
        Directory.Exists(_stateDirectory) && Directory.EnumerateFiles(_stateDirectory, "*.lst").Any();

    /// <summary>A name for the state directory of a pair of folders that stays the same between runs.</summary>
    public static string StateName(OfflineFolder folder)
    {
        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.LocalPath));
        if (!OperatingSystem.IsLinux())
        {
            local = local.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DriveAccess.Normalize(folder.RemotePath) + "\n" + local));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Runs one sync and reports what it did.</summary>
    public async Task<SyncResult> RunAsync(SyncMode mode = SyncMode.Normal, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var firstRun = !IsInitialized;
        var log = new List<string>();

        SyncResult Result(SyncOutcome outcome, IReadOnlyList<string>? errors = null)
        {
            var counts = Count(log);
            return new SyncResult(
                outcome,
                firstRun || mode == SyncMode.Repair,
                counts.Copied,
                counts.Uploaded,
                counts.Downloaded,
                counts.DeletedHere,
                counts.DeletedInStorage,
                counts.Conflicts,
                errors ?? [],
                watch.Elapsed);
        }

        if (firstRun)
        {
            // Bringing both sides together starts with marking them. A folder that is
            // new on one side is created on the other.
            try
            {
                Directory.CreateDirectory(Folder.LocalPath);
                EnsureLocalMarker();
                if (!await _client.FileExistsAsync(RemoteMarker, cancellationToken))
                {
                    await _client.UploadAsync(LocalMarker, RemoteMarker, cancellationToken);
                }
            }
            catch (RcloneException e)
            {
                return await _client.IsReachableAsync(cancellationToken)
                    ? Result(SyncOutcome.Failed, [e.Message])
                    : Result(SyncOutcome.Offline);
            }
        }
        else if (!Directory.Exists(Folder.LocalPath))
        {
            return Result(SyncOutcome.FolderMissing);
        }
        else if (Directory.EnumerateFileSystemEntries(Folder.LocalPath).Any(e => Path.GetFileName(e) != MarkerName))
        {
            // The marker alone may have been removed by a cleanup. With the files
            // still there the folder is clearly the same one. An emptied folder
            // without marker is left alone: the sync then stops as for a missing folder.
            EnsureLocalMarker();
        }

        Directory.CreateDirectory(_stateDirectory);
        List<string> arguments =
        [
            "bisync", Folder.LocalPath, DriveAccess.Remote(Folder.RemotePath),
            "--workdir", _stateDirectory,

            // both sides must still be marked, see MarkerName
            "--check-access", "--check-filename", MarkerName,

            // carry on by itself after an interruption instead of asking for a new start
            "--resilient", "--recover", "--max-lock", "2m",

            // changed on both sides: the newer file keeps its name, the other one is
            // kept next to it as "name.Konflikt1.ext"
            "--conflict-resolve", "newer", "--conflict-loser", "num",
            "--conflict-suffix", ConflictSuffix, "--suffix-keep-extension",

            "--create-empty-src-dirs",
            "--compare", "size,modtime",
            "-v",
        ];
        if (firstRun || mode == SyncMode.Repair)
        {
            arguments.AddRange(["--resync-mode", "newer"]);
        }
        else if (mode == SyncMode.AcceptAll)
        {
            arguments.Add("--force");
        }

        var result = await _client.RunAsync(
            arguments,
            null,
            line =>
            {
                lock (log)
                {
                    log.Add(line);
                }
            },
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return Result(SyncOutcome.Synced);
        }

        var errors = log.Where(l => IsLevel(l, "ERROR") || IsLevel(l, "NOTICE") || IsLevel(l, "CRITICAL")).TakeLast(8).ToList();

        // whatever rclone says about its failure, without the storage it is this
        if (!await _client.IsReachableAsync(cancellationToken))
        {
            return Result(SyncOutcome.Offline, errors);
        }

        bool Said(string text) => log.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (Said("too many deletes"))
        {
            return Result(SyncOutcome.TooManyDeletes, errors);
        }

        if (Said("all files were changed"))
        {
            return Result(SyncOutcome.AllFilesChanged, errors);
        }

        if (Said("Access test failed") || Said("check file check failed") || Said("Cannot sync to an empty directory") || Said("directory not found"))
        {
            return Result(SyncOutcome.FolderMissing, errors);
        }

        if (result.ExitCode == ExitCritical || Said("Must run --resync to recover"))
        {
            return Result(SyncOutcome.NeedsRepair, errors);
        }

        return Result(SyncOutcome.Failed, errors);
    }

    private string LocalMarker => Path.Combine(Folder.LocalPath, MarkerName);

    private string RemoteMarker => Folder.RemotePath + "/" + MarkerName;

    private void EnsureLocalMarker()
    {
        if (File.Exists(LocalMarker))
        {
            return;
        }

        File.WriteAllText(LocalMarker, MarkerContent);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(LocalMarker, File.GetAttributes(LocalMarker) | FileAttributes.Hidden);
        }
    }

    // "2026/11/16 23:02:06 INFO  : - Path1    Queue copy to Path2       - ipdrive:Projekte/Neu.txt"
    [GeneratedRegex(@"^\S+ \S+ (?<level>[A-Z]+)\s*: (?<message>.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^- (?<side>\S+)\s+(?<action>.+?)\s+- (?<target>.+)$")]
    private static partial Regex StepLine();

    private static bool IsLevel(string line, string level) =>
        LogLine().Match(line) is { Success: true } match && match.Groups["level"].Value == level;

    private static (int Copied, int Uploaded, int Downloaded, int DeletedHere, int DeletedInStorage, List<string> Conflicts) Count(List<string> log)
    {
        string[] lines;
        lock (log)
        {
            lines = [.. log];
        }

        int copied = 0, uploaded = 0, downloaded = 0, deletedHere = 0, deletedInStorage = 0;
        var conflicts = new List<string>();
        foreach (var line in lines)
        {
            if (LogLine().Match(line) is not { Success: true } entry)
            {
                continue;
            }

            var message = entry.Groups["message"].Value;
            if (message.EndsWith(": Copied (new)", StringComparison.Ordinal)
                || message.EndsWith(": Copied (replaced existing)", StringComparison.Ordinal))
            {
                copied++;
                continue;
            }

            if (StepLine().Match(message) is not { Success: true } step)
            {
                continue;
            }

            var side = step.Groups["side"].Value;
            var action = step.Groups["action"].Value;
            var target = step.Groups["target"].Value;
            if (action.StartsWith("Queue copy to Path2", StringComparison.Ordinal))
            {
                uploaded++;
            }
            else if (action.StartsWith("Queue copy to Path1", StringComparison.Ordinal))
            {
                downloaded++;
            }
            else if (action.StartsWith("Queue delete", StringComparison.Ordinal))
            {
                if (side == "Path1")
                {
                    deletedHere++;
                }
                else
                {
                    deletedInStorage++;
                }
            }
            else if (action.StartsWith("Renaming Path", StringComparison.Ordinal))
            {
                // the full path of the copy that keeps the losing version
                conflicts.Add(target.Replace('\\', '/').Split('/')[^1]);
            }
        }

        return (copied, uploaded, downloaded, deletedHere, deletedInStorage, conflicts);
    }
}
