using System.Globalization;
using ImagepixelDrive.Core;
using ImagepixelDrive.Core.Rclone;

// The prototype of Imagepixel Drive: it shows an encrypted area of a storage as a
// drive and keeps chosen folders of it on this computer as well. It has no window;
// it reports what it does on the console until it is stopped.

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    var (command, options, offline) = Parse(args);
    return command switch
    {
        "run" => await RunAsync(options, offline),
        "show-storage" => await ShowStorageAsync(options),
        _ => Usage(),
    };
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
catch (RcloneException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Usage()
{
    Console.WriteLine(
        """
        Prototyp von Imagepixel Drive

          ipd-prototype run SPEICHER PASSWORT [--drive ORT] [--offline ORDNER]... [--offline-root ORDNER]
                            [--data ORDNER] [--interval SEKUNDEN] [--stop-file DATEI]
              Zeigt das Laufwerk an und hält die mit --offline genannten Ordner des Laufwerks
              zusätzlich auf diesem Gerät. Läuft, bis Strg+C gedrückt wird oder die Stopp-Datei erscheint.
              ORT ist unter Windows ein freier Laufwerksbuchstabe (T:), unter macOS ein leerer Ordner.

          ipd-prototype show-storage SPEICHER PASSWORT
              Listet, was im Speicher wirklich liegt: verschlüsselte Namen und Größen.

        SPEICHER ist entweder
          --bucket NAME --credentials DATEI.csv [--region REGION] [--prefix ORDNER]
              ein Bereich in Imagepixel Storage. DATEI.csv ist die Schlüsseldatei, die der Speicheranbieter
              beim Anlegen eines Zugangsschlüssels zum Herunterladen anbietet. Ohne --prefix: drive.
        oder
          --folder ORDNER
              ein Ordner, zum Ausprobieren ohne Netz.

        PASSWORT ist --password-file DATEI (erste Zeile der Datei) oder die Umgebungsvariable IPD_PASSWORD.
        Ohne beides wird das Passwort abgefragt.
        """);
    return 2;
}

static async Task<int> RunAsync(Dictionary<string, string> options, List<string> offlineFolders)
{
    var rclone = RcloneClient.FindRclone()
        ?? throw new ArgumentException("rclone wurde nicht gefunden. Bitte zuerst get-rclone.ps1 ausführen.");
    if (!DriveMount.IsSupported)
    {
        throw new ArgumentException(OperatingSystem.IsWindows()
            ? "WinFsp ist nicht installiert. Ohne WinFsp kann Windows das Laufwerk nicht anzeigen."
            : "Auf diesem System kann das Laufwerk nicht angezeigt werden.");
    }

    var data = options.GetValueOrDefault("data")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImagepixelDrive", "prototype");
    var offlineRoot = options.GetValueOrDefault("offline-root") ?? DriveManager.DefaultOfflineRoot;
    var interval = options.TryGetValue("interval", out var text)
        ? TimeSpan.FromSeconds(int.Parse(text, CultureInfo.InvariantCulture))
        : TimeSpan.FromSeconds(60);
    var stopFile = options.GetValueOrDefault("stop-file");
    Directory.CreateDirectory(data);

    var client = await RcloneClient.CreateAsync(rclone, Storage(options), Password(options));

    // a drive that was opened here before may start without the storage: its offline
    // folders are on this computer
    var known = Path.Combine(data, "drive-known");
    switch (await client.ProbeAsync())
    {
        case DriveState.Ready:
            Say("Laufwerk gefunden, das Passwort passt.");
            break;
        case DriveState.Empty:
            await client.InitializeAsync();
            Say("Der Bereich im Speicher war leer. Neues Laufwerk angelegt.");
            break;
        case DriveState.WrongPassword:
            Console.Error.WriteLine("Das Passwort passt nicht zu diesem Laufwerk.");
            return 1;
        case DriveState.Unreachable when File.Exists(known):
            Say("Der Speicher ist nicht erreichbar. Die Offline-Ordner bleiben benutzbar.");
            break;
        default:
            Console.Error.WriteLine("Der Speicher ist nicht erreichbar.");
            return 1;
    }

    File.WriteAllText(known, "");

    var place = options.GetValueOrDefault("drive") ?? DriveMount.DefaultMountpoint()
        ?? throw new ArgumentException("Es ist kein Laufwerksbuchstabe frei.");
    await using var mount = await DriveMount.StartAsync(
        client,
        place,
        DriveMountOptions.For(client.Access.Storage, Path.Combine(data, "cache"), Path.Combine(data, "logs", "drive.log")));
    Say($"Laufwerk {mount.RootPath} ist da.");

    var keepers = new List<OfflineKeeper>();
    foreach (var name in offlineFolders)
    {
        var folder = new OfflineFolder(name, Path.Combine(offlineRoot, DriveAccess.Normalize(name).Replace('/', Path.DirectorySeparatorChar)));
        var sync = new OfflineSync(client, folder, Path.Combine(data, "sync", OfflineSync.StateName(folder)));
        var keeper = new OfflineKeeper(sync, interval);
        keeper.Completed += async (k, result) =>
        {
            Report(k.Folder, result);
            if (result.ChangedStorage)
            {
                await mount.RefreshAsync(k.Folder.RemotePath);
            }
        };
        keeper.Start();
        keepers.Add(keeper);
        Say($"Offline-Ordner: {folder.RemotePath}  <->  {folder.LocalPath}");
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    mount.Exited += (_, _) => stop.Cancel();

    var waiting = -1;
    while (!stop.IsCancellationRequested && (stopFile is null || !File.Exists(stopFile)))
    {
        try
        {
            var now = await mount.PendingUploadsAsync(stop.Token);
            if (now != waiting)
            {
                waiting = now;
                Say(now == 0 ? "Laufwerk: alles gespeichert." : $"Laufwerk: {now} Datei(en) warten auf das Hochladen.");
            }

            await Task.Delay(1000, stop.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or RcloneException)
        {
            // stopping, or the drive has ended by itself
        }
    }

    Say("Wird beendet ...");
    foreach (var keeper in keepers)
    {
        await keeper.DisposeAsync();
    }

    await mount.StopAsync();
    Say("Laufwerk entfernt.");
    return 0;
}

static async Task<int> ShowStorageAsync(Dictionary<string, string> options)
{
    var rclone = RcloneClient.FindRclone()
        ?? throw new ArgumentException("rclone wurde nicht gefunden. Bitte zuerst get-rclone.ps1 ausführen.");
    var client = await RcloneClient.CreateAsync(rclone, Storage(options), Password(options));
    var entries = await client.ListEncryptedAsync();
    foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
    {
        Console.WriteLine($"{entry.Size,12}  {entry.Path}");
    }

    Console.WriteLine($"{entries.Count} Objekt(e), {entries.Sum(e => e.Size)} Bytes");
    return 0;
}

static void Report(OfflineFolder folder, SyncResult result)
{
    var text = result.Outcome switch
    {
        SyncOutcome.Synced when !result.ChangedAnything => null,
        SyncOutcome.Synced when result.FirstRun => $"zum ersten Mal abgeglichen, {result.Copied} Datei(en) kopiert",
        SyncOutcome.Synced =>
            $"abgeglichen: {result.Uploaded} hoch, {result.Downloaded} herunter, {result.DeletedInStorage} im Speicher gelöscht, {result.DeletedHere} hier gelöscht"
            + (result.Conflicts.Count > 0 ? $", auf beiden Seiten geändert und doppelt behalten: {string.Join(", ", result.Conflicts)}" : ""),
        SyncOutcome.Offline => "Speicher nicht erreichbar, der Ordner bleibt benutzbar",
        SyncOutcome.TooManyDeletes => "angehalten: mehr als die Hälfte der Dateien wurde gelöscht",
        SyncOutcome.AllFilesChanged => "angehalten: alle Dateien erscheinen geändert",
        SyncOutcome.FolderMissing => "angehalten: der Ordner fehlt auf einer Seite",
        SyncOutcome.NeedsRepair => "angehalten: der letzte Abgleich muss neu aufgesetzt werden",
        _ => "fehlgeschlagen: " + string.Join(" | ", result.Errors.TakeLast(2)),
    };
    if (text is not null)
    {
        Say($"Offline-Ordner {folder.RemotePath}: {text} ({result.Duration.TotalSeconds:0.0} s)");
    }
}

static void Say(string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");

static DriveStorage Storage(Dictionary<string, string> options)
{
    if (options.TryGetValue("folder", out var folder))
    {
        Directory.CreateDirectory(folder);
        return new FolderStorage(folder);
    }

    var bucket = options.GetValueOrDefault("bucket")
        ?? throw new ArgumentException("Bitte --bucket und --credentials angeben, oder --folder.");
    var credentials = options.GetValueOrDefault("credentials")
        ?? throw new ArgumentException("Bitte --credentials angeben.");
    var (accessKeyId, secretAccessKey) = ReadCredentials(credentials);
    return ImagepixelStorage.Area(
        bucket, options.GetValueOrDefault("prefix") ?? "drive", accessKeyId, secretAccessKey, options.GetValueOrDefault("region"));
}

// the file has a header line that names its columns, and one line with the key
static (string AccessKeyId, string SecretAccessKey) ReadCredentials(string path)
{
    var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
    if (lines.Length < 2)
    {
        throw new ArgumentException($"{path} enthält keinen Zugangsschlüssel.");
    }

    var header = lines[0].Split(',').Select(c => c.Trim().Trim('"').ToLowerInvariant()).ToList();
    var values = lines[1].Split(',').Select(c => c.Trim().Trim('"')).ToArray();
    var id = header.FindIndex(c => c.Contains("access key id"));
    var secret = header.FindIndex(c => c.Contains("secret"));
    if (id < 0 || secret < 0 || values.Length <= Math.Max(id, secret))
    {
        throw new ArgumentException($"{path} hat nicht die Spalten einer Schlüsseldatei.");
    }

    return (values[id], values[secret]);
}

static string Password(Dictionary<string, string> options)
{
    if (options.TryGetValue("password-file", out var file))
    {
        return File.ReadLines(file).FirstOrDefault()?.Trim() is { Length: > 0 } fromFile
            ? fromFile
            : throw new ArgumentException($"{file} enthält kein Passwort.");
    }

    if (Environment.GetEnvironmentVariable("IPD_PASSWORD") is { Length: > 0 } fromEnvironment)
    {
        return fromEnvironment;
    }

    Console.Write("Passwort des Laufwerks: ");
    var typed = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            typed.Length = Math.Max(0, typed.Length - 1);
        }
        else if (!char.IsControl(key.KeyChar))
        {
            typed.Append(key.KeyChar);
        }
    }

    Console.WriteLine();
    return typed.Length > 0 ? typed.ToString() : throw new ArgumentException("Ohne Passwort geht es nicht.");
}

static (string Command, Dictionary<string, string> Options, List<string> Offline) Parse(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var offline = new List<string>();
    var command = args.Length > 0 ? args[0] : "";
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            throw new ArgumentException($"Unerwartete Angabe: {args[i]}");
        }

        var name = args[i][2..];
        var value = args[++i];
        if (name.Equals("offline", StringComparison.OrdinalIgnoreCase))
        {
            offline.Add(value);
        }
        else
        {
            options[name] = value;
        }
    }

    return (command, options, offline);
}
