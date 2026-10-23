using System.Net;
using System.Text.Json;
using WolfLeash.Dashboard;
using Xunit;

namespace WolfLeash.Tests;

public sealed class DashboardTests
{
    [Fact]
    public void NumaStatusDeserializesSocketTopologyAndIndependentEffectivePlacements()
    {
        var snapshot = JsonSerializer.Deserialize<DashboardSnapshot>("""
            {"success":true,"version":1,
             "cpus":[{"id":0,"name":"CPU model","cpus":[0,1,4,5],"numa_nodes":[0,2]}],
             "gpus":[{"id":"gpu","cpu_ids":[0],"numa_node":2,"local_cpus":[4,5]}],
             "containers":[{"id":"app","placement":{"cpu_ids":[0],"numa_node":2,"cpus":[5]}}],
             "sessions":[{"id":"client","connected":true,"streaming":true,
                          "placement":{"cpu_ids":[1],"numa_node":1,"cpus":["8","9"]}}]}
            """, DashboardSnapshot.JsonOptions)!;
        var cpu = Assert.Single(snapshot.Cpus);
        Assert.Equal(new uint[] { 0, 2 }, cpu.NumaNodes);
        Assert.Equal(new uint[] { 0, 1, 4, 5 }, cpu.Cpus);
        Assert.Equal(2, snapshot.Gpus[0].NumaNode);
        Assert.Equal(new uint[] { 5 }, snapshot.Containers[0].Placement!.Cpus);
        Assert.Equal(new uint[] { 8, 9 }, snapshot.Sessions[0].Placement!.Cpus);
        Assert.Equal(new[] { 1 }, snapshot.Sessions[0].Placement!.CpuIds);
    }

    [Fact]
    public void GpusAreOrderedByPhysicalCpuAndUnknownLocalityComesLastWithoutDuplicates()
    {
        GpuStatus[] gpus = [new() { Id = "z", CpuIds = [8] }, new() { Id = "unknown" },
                            new() { Id = "b", CpuIds = [2] }, new() { Id = "a", CpuIds = [2] },
                            new() { Id = "shared", CpuIds = [8, 2, 8] }];
        var groups = DashboardTopology.GroupGpus(gpus);
        Assert.Equal(new[] { "a", "b", "shared", "z", "unknown" }, groups.SelectMany(g => g.Gpus).Select(g => g.Id));
        Assert.Equal(new[] { 2, 8 }, groups[1].CpuIds);
        Assert.Empty(groups[^1].CpuIds);
        Assert.Equal(5, groups.Sum(g => g.Gpus.Length));
    }

    [Fact]
    public void CpuUsageComesFromEffectiveMasksAndDeduplicatesUsersAcrossAppsAndStreams()
    {
        var snapshot = new DashboardSnapshot
        {
            Containers = [new() { Id = "app", ProfileId = "p", Placement = new() { CpuIds = [0] } },
                          new() { Id = "unknown", ProfileId = "u", GpuId = "gpu" }],
            Sessions = [new() { Id = "c", ProfileId = "p", Connected = true, Streaming = true,
                               Placement = new() { CpuIds = [0, 1] } },
                        new() { Id = "paused", ProfileId = "p", Placement = new() { CpuIds = [2] } }],
            Gpus = [new() { Id = "gpu", CpuIds = [2] }]
        };
        var users = snapshot.ActiveUsers(new Dictionary<string, string>());
        var user = Assert.Single(users, u => u.Key == "profile:p");
        Assert.True(user.UsesCpu(0));
        Assert.True(user.UsesCpu(1));
        Assert.False(user.UsesCpu(2));
        Assert.Equal(1, users.Count(u => u.UsesCpu(0)));
        Assert.False(Assert.Single(users, u => u.Key == "profile:u").UsesCpu(2));
    }

    [Theory]
    [InlineData(new uint[] { }, "")]
    [InlineData(new uint[] { 4, 1, 0, 4, 8, 3, 2, 9 }, "0–4, 8–9")]
    [InlineData(new uint[] { 0, uint.MaxValue }, "0, 4294967295")]
    public void LogicalCpuListsAreCompactAndSorted(uint[] cpus, string expected) =>
        Assert.Equal(expected, DashboardTopology.CpuList(cpus));

    [Fact]
    public void OlderStatusRemainsUsableWithoutInventingCpuAssignments()
    {
        var snapshot = JsonSerializer.Deserialize<DashboardSnapshot>("""
            {"success":true,"version":1,"gpus":[{"id":"gpu"}],"sessions":[{"id":"c"}],"containers":[{"id":"app"}]}
            """, DashboardSnapshot.JsonOptions)!;
        Assert.Empty(snapshot.Cpus);
        Assert.Empty(snapshot.Gpus[0].CpuIds);
        Assert.Null(snapshot.Sessions[0].Placement);
        Assert.Null(snapshot.Containers[0].Placement);
        Assert.Equal("CPU locality unavailable", DashboardTopology.CpuNames([]));
        Assert.Equal("CPUs 2, 10", DashboardTopology.CpuNames([10, 2, 2]));
    }

    [Fact]
    public void SharedAppOwnerAndViewerStaySeparateAndGpuCountsDoNotDuplicateUsers()
    {
        var snapshot = new DashboardSnapshot
        {
            Profiles = [new("owner", "Owner"), new("viewer", "Viewer")],
            Containers = [new() { Id = "desktop", ProfileId = "owner", GpuId = "amd" },
                          new() { Id = "launcher", ProfileId = "viewer", SessionId = "client", GpuId = "nvidia" }],
            Sessions = [new() { Id = "client", ProfileId = "viewer", Connected = true, Streaming = true, GpuId = "amd" },
                        new() { Id = "old-session", Connected = false }]
        };
        var users = snapshot.ActiveUsers(new Dictionary<string, string>());
        Assert.Equal(2, users.Count);
        var viewer = Assert.Single(users, u => u.Name == "Viewer");
        Assert.True(viewer.Connected);
        Assert.True(viewer.UsesGpu("amd"));
        Assert.True(viewer.UsesGpu("nvidia"));
        Assert.Single(viewer.Containers);
        var owner = Assert.Single(users, u => u.Name == "Owner");
        Assert.False(owner.Connected);
        Assert.Single(owner.Containers);
        Assert.Equal(2, users.Count(u => u.UsesGpu("amd")));
    }

    [Fact]
    public void RetainedLauncherUsesClientAliasAndDisconnectedEncoderIsNotCounted()
    {
        var snapshot = new DashboardSnapshot
        {
            Containers = [new() { Id = "launcher", SessionId = "c", GpuId = "amd" }],
            Sessions = [new() { Id = "c", Connected = false, GpuId = "nvidia" }]
        };
        var user = Assert.Single(snapshot.ActiveUsers(new Dictionary<string, string> { ["c"] = "Living room" }));
        Assert.Equal("Living room", user.Name);
        Assert.True(user.UsesGpu("amd"));
        Assert.False(user.UsesGpu("nvidia"));
    }

    [Fact]
    public void WolfNumericStringsAndUnavailableTelemetryArePreserved()
    {
        var snapshot = JsonSerializer.Deserialize<DashboardSnapshot>("""
            {"success":true,"version":1,"gpus":[{"id":"gpu","vram_bytes":"8589934592","gpu_percent":0}],
             "sessions":[{"id":"18446744073709551615","bitrate_kbps":"20500","width":1920}]}
            """, DashboardSnapshot.JsonOptions)!;
        Assert.Equal(8589934592UL, snapshot.Gpus[0].VramBytes);
        Assert.Equal(0d, snapshot.Gpus[0].GpuPercent);
        Assert.Null(snapshot.Gpus[0].EncoderPercent);
        Assert.Null(snapshot.Gpus[0].VramPercent);
        Assert.Equal("18446744073709551615", snapshot.Sessions[0].Id);
        Assert.Equal(20500, snapshot.Sessions[0].BitrateKbps);
    }

    [Fact]
    public async Task ConcurrentViewersShareOneSnapshotRequest()
    {
        var handler = new StatusHandler(HttpStatusCode.OK, """{"success":true,"version":1}""");
        using var service = new DashboardService(new HttpClient(handler) { BaseAddress = new("http://wolf/") });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetAsync()));
        Assert.Equal(1, handler.Requests);
        Assert.All(results, result => { Assert.NotNull(result.Snapshot); Assert.Null(result.Error); });
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", "does not provide")]
    [InlineData(HttpStatusCode.OK, "not json", "unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "unavailable")]
    [InlineData(HttpStatusCode.OK, "{\"success\":true,\"version\":2}", "unavailable")]
    public async Task UnsupportedOrOfflineWolfHasAnExplicitUnavailableState(HttpStatusCode code, string body, string expected)
    {
        using var service = new DashboardService(new HttpClient(new StatusHandler(code, body)) { BaseAddress = new("http://wolf/") });
        var result = await service.GetAsync();
        Assert.Null(result.Snapshot);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task DisconnectedBrowserCancelsPollingAndReconnectResumesIt()
    {
        using var connection = new DashboardConnection();
        using var stop = new CancellationTokenSource();
        var waiting = connection.WaitAsync(stop.Token);
        Assert.False(waiting.IsCompleted);
        connection.SetConnected(true);
        var first = await waiting;
        Assert.False(first.IsCancellationRequested);
        connection.SetConnected(false);
        Assert.True(first.IsCancellationRequested);
        var reconnect = connection.WaitAsync(stop.Token);
        Assert.False(reconnect.IsCompleted);
        connection.SetConnected(true);
        var second = await reconnect;
        Assert.False(second.IsCancellationRequested);
        connection.SetConnected(false);
        var closed = connection.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closed);
    }

    private sealed class StatusHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/status", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Interlocked.Increment(ref Requests);
            await Task.Delay(10, cancellationToken);
            return new(code) { Content = new StringContent(body) };
        }
    }
}
