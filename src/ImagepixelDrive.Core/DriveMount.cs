using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core;

/// <summary>How a drive is shown on this computer.</summary>
/// <param name="CacheDirectory">
/// The folder for files on their way: what was written waits here until it is stored,
/// and what was read is kept for a while. It must be on a disk that supports sparse files.
/// </param>
/// <param name="VolumeName">The name the drive carries in the file manager.</param>
/// <param name="LogFile">A file rclone writes its log to, if wanted.</param>
public sealed record DriveMountOptions(string CacheDirectory, string VolumeName = "Imagepixel Drive", string? LogFile = null)
{
    /// <summary>
    /// How long the list of a folder is trusted before the storage is asked again. The
    /// storage does not announce changes made by other computers, so they show up
    /// after this time at the latest.
    /// </summary>
    public TimeSpan DirectoryCacheTime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether files that are read pass through the cache folder as well. That is what
    /// a storage far away needs. For a storage on this computer only written files do,
    /// or every file that is opened would lie on the disk twice.
    /// </summary>
    public bool CacheReads { get; init; } = true;

    /// <summary>The options that fit the place a drive is stored at.</summary>
    public static DriveMountOptions For(DriveStorage storage, string cacheDirectory, string? logFile = null) =>
        storage is FolderStorage
            ? new DriveMountOptions(cacheDirectory, LogFile: logFile) { CacheReads = false, DirectoryCacheTime = TimeSpan.FromSeconds(5) }
            : new DriveMountOptions(cacheDirectory, LogFile: logFile);
}

/// <summary>
/// A drive shown as a volume of this computer. Files are encrypted before they leave
/// it. The volume exists as long as the session is running; rclone ends with this
/// process if it ends for any reason. What was written but not stored yet stays in
/// the cache folder and is stored the next time the drive is shown with the same folder.
/// </summary>
/// <remarks>
/// Windows shows the drive at a drive letter through WinFsp. macOS shows it at a folder
/// through its own NFS client, which rclone serves on this computer only; nothing has to
/// be installed for that and no kernel extension is involved.
/// </remarks>
public sealed class DriveMount : IAsyncDisposable
{
    // macOS: rclone does not watch its standard input, so a shell does. It runs rclone
    // and ends it once the input is closed, which happens when this process ends for
    // any reason; rclone then removes the volume. The exit code of rclone becomes the
    // one of the shell. The input is kept as number 3 first, because a shell gives
    // what it runs in the background an empty input of its own.
    private static readonly string StopOnInputClose = """
        exec 3<&0
        "$@" 3<&- &
        mount=$!
        ( cat <&3 > /dev/null; kill -TERM "$mount" 2> /dev/null ) > /dev/null 2>&1 &
        exec 3<&-
        wait "$mount"
        """.ReplaceLineEndings("\n");

    // macOS: what its NFS client is told about the drive. Without them it keeps too
    // much to itself, because rclone serves the drive without the services a file
    // server usually has next to it.
    private static readonly string[] MacMountOptions =
    [
        // Locks are kept on this computer; rclone has no lock service to ask. macOS
        // locks the companion file ("._name") it stores extended attributes in, and
        // without this option that fails: a file that carries attributes, as every
        // download does, could then not be copied to the drive.
        "locallocks",

        // A name that was asked for before the file existed is asked for again.
        // Folders of a bucket never change their time, so macOS would never learn
        // that a file has appeared in one.
        "nonegnamecache",

        // For the same reason a folder list is trusted for three seconds, not for a minute.
        "actimeo=3",
    ];

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly RemoteControl _control;
    private readonly List<string> _log = [];

    private DriveMount(Process process, RemoteControl control, string mountpoint)
    {
        _process = process;
        _control = control;
        Mountpoint = mountpoint;
    }

    /// <summary>Where the drive is shown: a drive letter such as "T:" on Windows, a folder on macOS.</summary>
    public string Mountpoint { get; }

    /// <summary>The path to open in a file manager.</summary>
    public string RootPath => IsDriveLetter(Mountpoint) ? Mountpoint + Path.DirectorySeparatorChar : Mountpoint;

    public bool IsRunning => !_process.HasExited;

    /// <summary>Raised when the volume disappears, whether it was stopped or failed.</summary>
    public event EventHandler? Exited;

    /// <summary>Whether this computer can show a drive at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() ? IsWinFspInstalled() : OperatingSystem.IsMacOS();

    /// <summary>Returns the drive letters that are not in use, in the order they should be tried.</summary>
    public static IReadOnlyList<string> FreeDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return [.. "IJKLMNOPQRSTUVWXYZHGFED".Where(c => !used.Contains(c)).Select(c => $"{c}:")];
    }

    /// <summary>
    /// The place to show the drive at: on Windows the letter used last if it is still
    /// free, otherwise the first free one; on macOS a folder in the home folder.
    /// Null if Windows has no letter left.
    /// </summary>
    public static string? DefaultMountpoint(string? lastDriveLetter = null) => Mountpoints(lastDriveLetter).FirstOrDefault();

    /// <summary>
    /// The places to show the drive at, in the order they should be tried. Windows has
    /// several: a letter that looks free can be taken a moment later, or be reserved
    /// for a network drive that is not connected.
    /// </summary>
    public static IReadOnlyList<string> Mountpoints(string? lastDriveLetter = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Imagepixel Drive")];
        }

        var free = FreeDriveLetters();
        var last = lastDriveLetter?.ToUpperInvariant();
        return last is not null && free.Contains(last) ? [last, .. free.Where(letter => letter != last)] : free;
    }

    /// <summary>Whether WinFsp is installed, which Windows needs to show the drive.</summary>
    public static bool IsWinFspInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(folder), "WinFsp", "bin", "winfsp-x64.dll")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a volume is shown at a folder. macOS only; Windows uses drive letters.</summary>
    public static bool IsMounted(string folder)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        if (parent is null || !Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            // A folder a volume is shown at lies on another device than the folder
            // around it. "stat" prints the number of the device of each.
            var (exitCode, output) = Run("/usr/bin/stat", ["-f", "%d", folder, parent]);
            var devices = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return exitCode == 0 && devices.Length == 2 && devices[0] != devices[1];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a volume that was left behind at a folder by an rclone that could not
    /// clean up. macOS only; does nothing if there is none.
    /// </summary>
    public static void ReleaseStale(string folder)
    {
        if (!OperatingSystem.IsMacOS() || !IsMounted(folder))
        {
            return;
        }

        try
        {
            Run("/sbin/umount", ["-f", folder]);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nothing more to try
        }
    }

    /// <summary>Shows the drive and waits until it can be used.</summary>
    /// <param name="client">Access to the drive.</param>
    /// <param name="mountpoint">A free drive letter on Windows, an empty folder on macOS; see <see cref="DefaultMountpoint"/>.</param>
    /// <param name="options">How the drive is shown.</param>
    /// <param name="cancellationToken">Cancels waiting for the drive.</param>
    public static async Task<DriveMount> StartAsync(
        RcloneClient client,
        string mountpoint,
        DriveMountOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Showing the drive is only implemented for Windows and macOS.");
        }

        Directory.CreateDirectory(options.CacheDirectory);
        var port = FreePort();
        var user = "ipd";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        List<string> arguments =
        [
            OperatingSystem.IsWindows() ? "mount" : "nfsmount", DriveAccess.Remote(), mountpoint,
            "--volname", options.VolumeName,

            // files pass through the cache folder: programs can then read and write as
            // on a disk, and a failed upload is tried again
            "--vfs-cache-mode", options.CacheReads ? "full" : "writes",
            "--cache-dir", options.CacheDirectory,
            "--dir-cache-time", Duration(options.DirectoryCacheTime),

            "--rc", "--rc-addr", $"127.0.0.1:{port}",
            "-v",
        ];
        if (OperatingSystem.IsMacOS())
        {
            arguments.AddRange(MacMountOptions.SelectMany(option => new[] { "--option", option }));
        }

        if (options.LogFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogFile))!);
            arguments.AddRange(["--log-file", options.LogFile]);
        }

        ProcessStartInfo startInfo;
        Func<bool> isReady;
        if (OperatingSystem.IsWindows())
        {
            startInfo = client.CreateStartInfo(arguments);
            var root = mountpoint + Path.DirectorySeparatorChar;
            isReady = () => Directory.Exists(root);
        }
        else
        {
            ReleaseStale(mountpoint);
            Directory.CreateDirectory(mountpoint);
            if (Directory.EnumerateFileSystemEntries(mountpoint).Any())
            {
                throw new IOException($"The folder {mountpoint} is not empty, so the drive cannot be shown there.");
            }

            startInfo = client.CreateStartInfo(["-c", StopOnInputClose, "sh", client.RclonePath, .. arguments], "/bin/sh");
            isReady = () => IsMounted(mountpoint);
        }

        startInfo.Environment["RCLONE_RC_USER"] = user;
        startInfo.Environment["RCLONE_RC_PASS"] = password;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var session = new DriveMount(process, new RemoteControl(port, user, password), mountpoint);
        process.Exited += (_, _) => session.Exited?.Invoke(session, EventArgs.Empty);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (session._log)
                {
                    session._log.Add(e.Data);
                    if (session._log.Count > 200)
                    {
                        session._log.RemoveAt(0);
                    }
                }
            }
        };

        process.Start();
        ChildProcesses.EndWithThisProcess(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        if (OperatingSystem.IsWindows())
        {
            // on macOS the input stays open: closing it is what ends rclone
            process.StandardInput.Close();
        }

        try
        {
            var deadline = Stopwatch.StartNew();
            while (!isReady())
            {
                if (process.HasExited)
                {
                    process.WaitForExit();
                    throw new RcloneException(session.ErrorText(), process.ExitCode);
                }

                if (deadline.Elapsed > StartTimeout)
                {
                    throw new TimeoutException($"The drive did not appear at {mountpoint} in time.");
                }

                await Task.Delay(150, cancellationToken);
            }

            // A volume that another program put at the same place in the same moment
            // looks like ours. rclone then gives up shortly after it started.
            await Task.Delay(500, cancellationToken);
            if (process.HasExited)
            {
                process.WaitForExit();
                throw new RcloneException(session.ErrorText(), process.ExitCode);
            }
        }
        catch
        {
            await session.StopAsync(TimeSpan.Zero);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Makes the drive forget what it knows about a folder, so that changes made
    /// behind its back, such as by the sync of an offline folder, show up at once.
    /// </summary>
    public async Task RefreshAsync(string folder = "", CancellationToken cancellationToken = default)
    {
        var path = DriveAccess.Normalize(folder);
        try
        {
            await _control.CallAsync("vfs/forget", path.Length == 0 ? null : new { dir = path }, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or RcloneException)
        {
            // the drive is gone or does not know the folder: nothing to forget
        }
    }

    /// <summary>The number of files that were written to the drive but are not stored yet.</summary>
    public async Task<int> PendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        var answer = await _control.CallAsync("vfs/queue", null, cancellationToken);
        return answer.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Array
            ? queue.GetArrayLength()
            : 0;
    }

    /// <summary>
    /// Removes the volume. Files that are not stored yet get the given time to reach
    /// the storage; what is still waiting after that stays in the cache folder.
    /// </summary>
    public async Task StopAsync(TimeSpan? waitForUploads = null)
    {
        var wait = waitForUploads ?? TimeSpan.FromSeconds(30);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!_process.HasExited && deadline.Elapsed < wait && await PendingUploadsAsync() > 0)
            {
                await Task.Delay(500);
            }

            if (!_process.HasExited)
            {
                // rclone unmounts the volume before it ends
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _control.CallAsync("core/quit", null, timeout.Token);
            }
        }
        catch (Exception e) when (e is HttpRequestException or RcloneException or OperationCanceledException or InvalidOperationException)
        {
            // rclone does not answer any more; it is ended below
        }

        try
        {
            // macOS: the shell that watches over rclone ends it when its input closes
            _process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // closed before, or the process is gone
        }

        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // the process has already exited
        }

        if (!IsDriveLetter(Mountpoint))
        {
            ReleaseStale(Mountpoint);
            try
            {
                // the empty folder the volume was shown at
                Directory.Delete(Mountpoint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // still in use, or gone already
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _control.Dispose();
        _process.Dispose();
    }

    private string ErrorText()
    {
        lock (_log)
        {
            return RcloneException.Describe(_log, _process.ExitCode);
        }
    }

    private static bool IsDriveLetter(string mountpoint) =>
        mountpoint.Length == 2 && mountpoint[1] == ':' && char.IsAsciiLetter(mountpoint[0]);

    private static string Duration(TimeSpan time) => $"{Math.Max(1, (long)time.TotalSeconds)}s";

    private static (int ExitCode, string Output) Run(string program, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        _ = error.GetAwaiter().GetResult();
        return (process.HasExited ? process.ExitCode : -1, output);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
