using System.Net;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.Core.Tests;

public class StorageStatusTests
{
    private const string Password = "Nur-ein-Testpasswort-4711";

    private const string Document = """
        { "version": 1, "bookedBytes": 107374182400, "usedBytes": 91268055040, "measuredAt": "2026-11-16T08:30:00+01:00" }
        """;

    /// <summary>Stands in for the service of Imagepixel and remembers what it was asked.</summary>
    private sealed class Service(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static Func<CancellationToken, Task<string?>> StatusFile(string? text) => _ => Task.FromResult(text);

    [Fact]
    public async Task The_service_is_asked_first()
    {
        var service = new Service(HttpStatusCode.OK, Document);
        var status = new StorageStatusService(new HttpClient(service));

        var result = await status.GetAsync("https://api.example.test/v1/", "ein-token", StatusFile(null));

        Assert.NotNull(result.Status);
        Assert.Equal(StorageStatusSource.Api, result.Status.Source);
        Assert.Equal(107374182400, result.Status.BookedBytes);
        Assert.Equal(0.85, result.Status.Fraction, 3);
        Assert.True(result.Status.IsWarning);
        Assert.False(result.Status.IsFull);

        var request = Assert.Single(service.Requests);
        Assert.Equal("https://api.example.test/v1/storage-status", request.RequestUri!.ToString());
        Assert.Equal("Bearer ein-token", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task The_status_file_stands_in_when_the_service_fails_and_can_announce_it()
    {
        var service = new Service(HttpStatusCode.InternalServerError, "kaputt");
        var status = new StorageStatusService(new HttpClient(service));
        var file = """
            { "bookedBytes": 1000, "usedBytes": 1000, "measuredAt": "2026-11-16T08:30:00Z",
              "apiUrl": "https://api.example.test/v2", "apiToken": "neues-token" }
            """;

        var result = await status.GetAsync("https://api.example.test/v1", "ein-token", StatusFile(file));

        Assert.Equal(StorageStatusSource.StatusFile, result.Status!.Source);
        Assert.True(result.Status.IsFull);
        Assert.Equal("https://api.example.test/v2", result.ApiUrl);
        Assert.Equal("neues-token", result.ApiToken);
    }

    [Fact]
    public async Task The_service_is_only_asked_over_an_encrypted_connection()
    {
        var service = new Service(HttpStatusCode.OK, Document);
        var status = new StorageStatusService(new HttpClient(service));

        var result = await status.GetAsync("http://api.example.test/v1", "ein-token", StatusFile(null));

        Assert.Null(result.Status);
        Assert.Empty(service.Requests);

        // and a status file cannot point the application to one that is not
        var file = """{ "bookedBytes": 1000, "usedBytes": 10, "apiUrl": "http://api.example.test", "apiToken": "x" }""";
        Assert.Null((await status.GetAsync(null, null, StatusFile(file))).ApiUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein JSON")]
    [InlineData("""{ "bookedBytes": 0, "usedBytes": 10 }""")]
    [InlineData("""{ "bookedBytes": 100, "usedBytes": -1 }""")]
    public async Task Figures_that_make_no_sense_are_not_shown(string file)
    {
        var status = new StorageStatusService(new HttpClient(new Service(HttpStatusCode.NotFound, "")));

        Assert.Null((await status.GetAsync(null, null, StatusFile(file))).Status);
    }

    [SkippableFact]
    public async Task Manager_reads_the_status_file_of_the_bucket_and_learns_about_the_service()
    {
        var rclone = TestDrive.RequireRclone();
        var root = TestDrive.NewRoot();
        using var server = await S3TestServer.StartAsync(rclone, Path.Combine(root, "s3"), "bucket");
        var service = new Service(HttpStatusCode.OK, Document);
        var secrets = new MemorySecretStore();
        await using var manager = new DriveManager(
            new ConfigStore(Path.Combine(root, "settings")),
            secrets,
            rclone,
            Path.Combine(root, "data"),
            Path.Combine(root, "offline"),
            new HttpClient(service));
        var storage = new StorageSettings
        {
            Kind = StorageKind.ImagepixelStorage,
            Endpoint = $"http://127.0.0.1:{server.Port}",
            Region = "us-east-1",
            Bucket = "bucket",
            AccessKeyId = S3TestServer.AccessKey,
        };
        Assert.Equal(DriveState.Ready, await manager.ConnectAsync(storage, Password, S3TestServer.Secret, createIfMissing: true));

        // no service known and no status file: no figures
        Assert.Null(await manager.GetStorageStatusAsync());
        Assert.Empty(service.Requests);

        // Imagepixel puts the status file into the bucket, next to the drive
        var client = await RcloneClient.CreateAsync(rclone, storage.ToStorage(S3TestServer.Secret), Password);
        var stored = await client.RunAsync(
            ["rcat", $"{DriveStorage.StoreRemote}:bucket/{StorageStatusDocument.ObjectKey}"],
            """
            { "bookedBytes": 2000, "usedBytes": 500, "measuredAt": "2026-11-16T08:30:00Z",
              "apiUrl": "https://api.example.test/v1", "apiToken": "ein-token" }
            """,
            null,
            CancellationToken.None);
        stored.ThrowIfFailed();

        var fromFile = await manager.GetStorageStatusAsync();
        Assert.Equal(StorageStatusSource.StatusFile, fromFile!.Source);
        Assert.Equal(0.25, fromFile.Fraction, 3);
        Assert.Equal("https://api.example.test/v1", manager.Config.Storage!.ApiUrl);
        Assert.Equal("ein-token", secrets.Get(SecretNames.ApiToken));

        // from then on the service is asked
        var fromService = await manager.GetStorageStatusAsync();
        Assert.Equal(StorageStatusSource.Api, fromService!.Source);
        Assert.Single(service.Requests);

        // the status file is not part of the drive
        Assert.Equal([RcloneClient.CheckFileName], (await client.ListAsync()).Select(e => e.Name));
    }

    [SkippableFact]
    public async Task A_place_of_the_customers_own_has_no_figures()
    {
        var rclone = TestDrive.RequireRclone();
        var root = TestDrive.NewRoot();
        var service = new Service(HttpStatusCode.OK, Document);
        await using var manager = new DriveManager(
            new ConfigStore(Path.Combine(root, "settings")),
            new MemorySecretStore(),
            rclone,
            Path.Combine(root, "data"),
            Path.Combine(root, "offline"),
            new HttpClient(service));
        var folder = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(root, "Tresor") };
        Assert.Equal(DriveState.Ready, await manager.ConnectAsync(folder, Password, null, createIfMissing: true));

        Assert.Null(await manager.GetStorageStatusAsync());
        Assert.Empty(service.Requests);
    }
}
