using System.Net;
using System.Text.Json;
using WolfLeash.Configuration;

namespace WolfLeash.Dashboard;

// One short-lived snapshot per Den instance, shared by all open dashboards. No GPU probes or Docker socket needed.
public sealed class DashboardService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private DashboardResult _last = new(null, null, null);
    private DateTimeOffset _nextRefresh;

    public DashboardService(IConfiguration configuration) : this(WolfConfigurationClient.CreateClient(configuration)) { }
    public DashboardService(HttpClient http) => _http = http;

    public async Task<DashboardResult> GetAsync(CancellationToken cancellationToken = default)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _nextRefresh) return _last;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await _http.GetAsync("api/v1/status", timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    _last = _last with { Error = "This Wolf version does not provide live dashboard data. Install the Wolf build with dashboard support." };
                else
                {
                    response.EnsureSuccessStatusCode();
                    var snapshot = await response.Content.ReadFromJsonAsync<DashboardSnapshot>(DashboardSnapshot.JsonOptions, timeout.Token);
                    if (snapshot is not { Success: true, Version: 1 } || snapshot.Gpus is null || snapshot.Sessions is null || snapshot.Containers is null || snapshot.Profiles is null)
                        throw new JsonException("Unsupported dashboard response.");
                    _last = new(snapshot, DateTimeOffset.UtcNow, null);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { _last = _last with { Error = "Wolf did not respond in time. Retrying automatically." }; }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
            { _last = _last with { Error = "Live status is unavailable. Check the Wolf connection. Retrying automatically." }; }
            _nextRefresh = DateTimeOffset.UtcNow.AddSeconds(4);
            return _last;
        }
        finally { _refresh.Release(); }
    }

    public void Dispose() { _http.Dispose(); _refresh.Dispose(); }
}
