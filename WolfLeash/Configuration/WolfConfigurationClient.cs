using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WolfLeash.Configuration;

public sealed class WolfConfigurationClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly DenStorage _storage;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public WolfConfigurationClient(IConfiguration configuration, DenStorage storage)
        : this(CreateClient(configuration), storage) { }

    public WolfConfigurationClient(HttpClient http, DenStorage storage)
    {
        _http = http;
        _storage = storage;
    }

    internal static HttpClient CreateClient(IConfiguration configuration)
    {
        var path = configuration["SOCKET_PATH"] ?? "/var/run/wolf/wolf.sock";
        if (path.StartsWith("unix://")) path = path[7..];
        var handler = new SocketsHttpHandler();
        if (path.StartsWith('/'))
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration["BASE_ADDRESS"] ?? "http://localhost/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<List<JsonObject>> GetProfilesAsync()
    {
        var result = await SendAsync(HttpMethod.Get, "api/v1/profiles");
        if (result["profiles"] is not JsonArray) throw new InvalidDataException("Wolf returned no profile list.");
        return result.Objects("profiles");
    }

    public async Task<byte[]> GetIconAsync(string path)
    {
        using var response = await _http.GetAsync($"api/v1/utils/get-icon?icon_path={Uri.EscapeDataString(path)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task AddProfileAsync(JsonObject profile)
    {
        await _writes.WaitAsync();
        try
        {
            if ((await GetProfilesAsync()).Any(p => p.Text("id") == profile.Text("id")))
                throw new InvalidOperationException("A profile with this ID already exists.");
            await SendAsync(HttpMethod.Post, "api/v1/profiles/add", profile);
        }
        finally { _writes.Release(); }
    }

    public async Task DeleteProfileAsync(JsonObject expected)
    {
        await _writes.WaitAsync();
        try
        {
            await CheckCurrentAsync(expected);
            await _storage.WriteAsync($"deleted-profile-{Guid.NewGuid():N}.json", expected);
            await SendAsync(HttpMethod.Post, "api/v1/profiles/remove", new JsonObject { ["id"] = expected.Text("id") });
        }
        finally { _writes.Release(); }
    }

    public async Task ReplaceProfileAsync(JsonObject expected, JsonObject updated)
    {
        if (expected.Text("id") != updated.Text("id")) throw new InvalidOperationException("Profile IDs cannot change.");
        await _writes.WaitAsync();
        try
        {
            await CheckCurrentAsync(expected);
            // Wolf's API only has remove/add. Save a durable recovery copy before either request.
            var backup = $"profile-recovery-{Guid.NewGuid():N}.json";
            await _storage.WriteAsync(backup, expected);
            try
            {
                await SendAsync(HttpMethod.Post, "api/v1/profiles/remove", new JsonObject { ["id"] = expected.Text("id") });
                await SendAsync(HttpMethod.Post, "api/v1/profiles/add", updated);
            }
            catch (Exception failure)
            {
                try
                {
                    var current = (await GetProfilesAsync()).SingleOrDefault(p => p.Text("id") == expected.Text("id"));
                    if (JsonNode.DeepEquals(current, updated)) return; // The reply was lost after a successful add.
                    if (current is null) await SendAsync(HttpMethod.Post, "api/v1/profiles/add", expected);
                    else if (!JsonNode.DeepEquals(current, expected))
                        throw new InvalidOperationException("Wolf now contains a different profile; it was left untouched.");
                }
                catch (Exception recovery)
                {
                    throw new InvalidOperationException($"Profile save failed and recovery could not be confirmed. Recovery file: {backup}. {recovery.Message}", failure);
                }
                throw new InvalidOperationException($"Profile save failed; the previous configuration was retained or restored. {failure.Message}", failure);
            }
        }
        finally { _writes.Release(); }
    }

    private async Task CheckCurrentAsync(JsonObject expected)
    {
        var current = (await GetProfilesAsync()).SingleOrDefault(p => p.Text("id") == expected.Text("id"));
        if (!JsonNode.DeepEquals(expected, current))
            throw new InvalidOperationException("This profile changed in Wolf-UI or another browser. Reload before saving.");
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonObject? result;
        try { result = JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException($"Wolf returned an invalid JSON response ({(int)response.StatusCode})."); }
        if (!response.IsSuccessStatusCode || result?["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(result?.Text("error") is { Length: > 0 } error ? error : $"Wolf request failed ({(int)response.StatusCode}).");
        return result;
    }

    public void Dispose() { _http.Dispose(); _writes.Dispose(); }
}
