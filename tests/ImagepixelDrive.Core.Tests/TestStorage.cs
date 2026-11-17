using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ImagepixelDrive.Core.Tests;

/// <summary>
/// An S3 compatible storage on this computer, served by rclone from a folder. It stands
/// in for the storage of a customer in tests that need a network connection to lose.
/// </summary>
internal sealed class S3TestServer : IDisposable
{
    public const string AccessKey = "testkey";
    public const string Secret = "testsecret";

    private readonly Process _process;

    private S3TestServer(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Serves a folder; every folder directly in it is a bucket.</summary>
    public static async Task<S3TestServer> StartAsync(string rclone, string folder, string bucket)
    {
        Directory.CreateDirectory(Path.Combine(folder, bucket));
        var port = Ports.Free();
        var startInfo = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "serve", "s3", folder, "--addr", $"127.0.0.1:{port}", "--auth-key", $"{AccessKey},{Secret}" },
        };
        foreach (var name in startInfo.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["RCLONE_CONFIG"] = "";

        var process = Process.Start(startInfo)!;
        ChildProcesses.EndWithThisProcess(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var server = new S3TestServer(process, port);
        var deadline = Stopwatch.StartNew();
        while (!await Ports.AnswersAsync(port))
        {
            if (process.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(30))
            {
                server.Dispose();
                throw new InvalidOperationException("The test storage did not start.");
            }

            await Task.Delay(100);
        }

        return server;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // ended already
        }

        _process.Dispose();
    }
}

/// <summary>
/// The connection between this computer and the test storage. Taking it down is what
/// losing the network looks like to rclone: nothing answers at the address any more.
/// </summary>
internal sealed class Link : IDisposable
{
    private readonly int _target;
    private readonly List<TcpClient> _open = [];
    private TcpListener? _listener;

    public Link(int targetPort)
    {
        _target = targetPort;
        Port = Ports.Free();
        Up();
    }

    /// <summary>The port to reach the storage through.</summary>
    public int Port { get; }

    public void Up()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        _listener = listener;

        // On a thread of its own: a test that waits for the drive on the thread of the
        // user interface must not keep the connection from carrying data.
        _ = Task.Run(() => AcceptAsync(listener));
    }

    public void Down()
    {
        _listener?.Stop();
        _listener = null;
        lock (_open)
        {
            foreach (var client in _open)
            {
                client.Close();
            }

            _open.Clear();
        }
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => ForwardAsync(client));
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // the link was taken down
        }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        var server = new TcpClient();
        lock (_open)
        {
            _open.Add(client);
            _open.Add(server);
        }

        try
        {
            await server.ConnectAsync(IPAddress.Loopback, _target).ConfigureAwait(false);
            var there = client.GetStream().CopyToAsync(server.GetStream());
            var back = server.GetStream().CopyToAsync(client.GetStream());
            await Task.WhenAny(there, back).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // one side hung up
        }
        finally
        {
            client.Close();
            server.Close();
            lock (_open)
            {
                _open.Remove(client);
                _open.Remove(server);
            }
        }
    }

    public void Dispose() => Down();
}

internal static class Ports
{
    public static int Free()
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

    public static async Task<bool> AnswersAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

internal static class Wait
{
    /// <summary>Waits until a condition holds and fails the test if it does not in time.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                // not there yet
            }

            Assert.True(watch.Elapsed < timeout, $"Not reached within {timeout.TotalSeconds:0} seconds: {what}");
            await Task.Delay(250);
        }
    }

    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, what);
}
