using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using ImagepixelDrive.Core;

namespace ImagepixelDrive.App;

/// <summary>
/// Makes sure the application runs only once per user. A second start asks the
/// running instance to show its window and exits.
/// </summary>
/// <remarks>
/// Windows: a named mutex, which the setup program also uses to notice that the
/// application is running, and a named event to ask for the window. macOS and Linux:
/// a locked file in the folder of the settings, because their named mutexes do not
/// reach from one way of starting a program to another, and a pipe to ask for the window.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>The installer uses this name to notice that the application is running.</summary>
    public const string MutexName = "ImagepixelDrive.SingleInstance";

    private const string ShowEventName = "ImagepixelDrive.ShowWindow";

    private readonly IDisposable _claim;
    private readonly EventWaitHandle? _showEvent;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(IDisposable claim)
    {
        _claim = claim;
        if (OperatingSystem.IsWindows())
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
    }

    /// <summary>Returns null if another instance is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(false, MutexName, out var createdNew);
            if (createdNew)
            {
                return new SingleInstance(mutex);
            }

            mutex.Dispose();
            return null;
        }

        try
        {
            // the lock is gone as soon as the process that holds it ends, however it ends
            Directory.CreateDirectory(ConfigStore.DefaultDirectory);
            var lockFile = new FileStream(
                Path.Combine(ConfigStore.DefaultDirectory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(lockFile);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalExisting()
    {
        if (OperatingSystem.IsWindows())
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
            {
                using (showEvent)
                {
                    showEvent.Set();
                }
            }

            return;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            pipe.WriteByte(1);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // the running instance is just starting or ending; nothing to show then
        }
    }

    /// <summary>Calls the handler, on a background thread, whenever another start asks for the window.</summary>
    public void OnShowRequested(Action handler)
    {
        var thread = new Thread(() =>
        {
            if (_showEvent is { } showEvent)
            {
                while (WaitHandle.WaitAny([showEvent, _stop.Token.WaitHandle]) == 0)
                {
                    handler();
                }
            }
            else
            {
                ListenOnPipe(handler);
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstance",
        };
        thread.Start();
    }

    private void ListenOnPipe(Action handler)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                if (pipe.ReadByte() >= 0)
                {
                    handler();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // the other side went away; wait for the next one
            }
        }
    }

    // One pipe per user and per folder of settings, so that neither another user nor
    // a test with settings of its own reaches this instance. The name only has to be
    // the same at every start, so a simple checksum of the two will do.
    private static string PipeName
    {
        get
        {
            var hash = 14695981039346656037UL;
            foreach (var c in Environment.UserName + "\n" + ConfigStore.DefaultDirectory)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }

            return "ImagepixelDrive." + hash.ToString("x16");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _showEvent?.Dispose();
        _claim.Dispose();
    }
}
