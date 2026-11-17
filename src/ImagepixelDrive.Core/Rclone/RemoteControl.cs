using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ImagepixelDrive.Core.Rclone;

/// <summary>
/// Talks to a running rclone through its remote control. rclone listens on this
/// computer only and asks for a name and password that are made up for each start.
/// </summary>
internal sealed class RemoteControl : IDisposable
{
    private readonly HttpClient _http;

    public RemoteControl(int port, string user, string password)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>Calls a command of rclone and returns its answer.</summary>
    public async Task<JsonElement> CallAsync(string command, object? parameters = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(command, parameters ?? new { }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RcloneException($"rclone answered {command} with {(int)response.StatusCode}: {body}", (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return document.RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}
