using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WolfLeash.Components.Pages;
using WolfLeash.Dashboard;
using Xunit;

namespace WolfLeash.Tests;

public sealed class ContainerMemoryTests
{
    [Fact]
    public void ResidencyBytesAcceptWolfNumericStringsAndPreserveUnknownsAndZero()
    {
        var snapshot = JsonSerializer.Deserialize<DashboardSnapshot>("""
            {"success":true,"version":1,"containers":[
             {"id":"app","memory":{"local_nodes":["0",2],
              "anon":{"local_bytes":"4294967296","remote_bytes":"0"},
              "file":{"local_bytes":1048576,"remote_bytes":"106496"}}},
             {"id":"old"},
             {"id":"partial","memory":{"local_nodes":null,"anon":{"local_bytes":0}}}]}
            """, DashboardSnapshot.JsonOptions)!;
        var memory = snapshot.Containers[0].Memory!;
        Assert.Equal(new uint[] { 0, 2 }, memory.LocalNodes);
        Assert.Equal(4294967296UL, memory.Anon!.LocalBytes);
        Assert.Equal(0UL, memory.Anon.RemoteBytes);
        Assert.Equal(100d, memory.Anon.LocalPercent);
        Assert.Equal(106496UL, memory.File!.RemoteBytes);
        Assert.Null(snapshot.Containers[1].Memory);
        Assert.Null(snapshot.Containers[2].Memory!.LocalNodes);
        Assert.Null(snapshot.Containers[2].Memory!.Anon!.TotalBytes);
    }

    [Fact]
    public void LocalityPercentageRejectsMissingOrOverflowedTotalsAndPreservesTrueZero()
    {
        Assert.Equal(75d, new MemoryLocalityBytes { LocalBytes = 3, RemoteBytes = 1 }.LocalPercent);
        Assert.Equal(0d, new MemoryLocalityBytes { LocalBytes = 0, RemoteBytes = 1 }.LocalPercent);
        Assert.Equal(0UL, new MemoryLocalityBytes { LocalBytes = 0, RemoteBytes = 0 }.TotalBytes);
        Assert.Null(new MemoryLocalityBytes { LocalBytes = 0, RemoteBytes = 0 }.LocalPercent);
        Assert.Null(new MemoryLocalityBytes { LocalBytes = 1 }.LocalPercent);
        Assert.Null(new MemoryLocalityBytes { LocalBytes = ulong.MaxValue, RemoteBytes = 1 }.TotalBytes);
    }

    [Fact]
    public async Task LocalAndRemoteRamRenderSeparatelyFromCacheWithSmallRemoteValuesVisible()
    {
        var html = await Render(new()
        {
            LocalNodes = [2, 0, 2],
            Anon = new() { LocalBytes = 3UL << 30, RemoteBytes = 1UL << 30 },
            File = new() { LocalBytes = 1UL << 20, RemoteBytes = 106496 }
        });
        Assert.Contains("Anonymous RAM", html);
        Assert.Contains("75% local", html);
        Assert.Contains("3 GiB", html);
        Assert.Contains("1 GiB", html);
        Assert.Contains("File/cache:", html);
        Assert.Contains("104 KiB remote", html);
        Assert.Contains("Local = NUMA 0, 2", html);
        Assert.Contains("width: 75%", html);
        Assert.Contains("width: 25%", html);
    }

    [Fact]
    public async Task UnknownAndLegacyResidencyDoesNotBecomeFalseZeroOrLocality()
    {
        foreach (var memory in new ContainerMemoryStatus?[]
                 { null, new(), new() { LocalNodes = null! },
                   new() { LocalNodes = [0], Anon = new() { LocalBytes = 0 } },
                   new() { LocalNodes = [0], Anon = new() { LocalBytes = ulong.MaxValue, RemoteBytes = 1 } } })
        {
            var html = await Render(memory);
            Assert.Contains("Unavailable", html);
            Assert.DoesNotContain("0 B", html);
            Assert.DoesNotContain("100% local", html);
            Assert.DoesNotContain("locality-bar", html);
        }
    }

    [Fact]
    public async Task EmptyMemoryDisplaysKnownZeroWithoutClaimingLocalPercentage()
    {
        var html = await Render(new()
        {
            LocalNodes = [0], Anon = new() { LocalBytes = 0, RemoteBytes = 0 },
            File = new() { LocalBytes = 0, RemoteBytes = 0 }
        });
        Assert.Contains("No resident pages", html);
        Assert.Contains("0 B", html);
        Assert.DoesNotContain("100% local", html);
        Assert.DoesNotContain("locality-bar", html);
    }

    [Fact]
    public async Task TinyRemoteAnonymousAllocationNeverRoundsToExactlyAllLocal()
    {
        var html = await Render(new()
        {
            LocalNodes = [0], Anon = new() { LocalBytes = 1UL << 30, RemoteBytes = 1 }
        });
        Assert.DoesNotContain("100% local", html);
        Assert.Contains("99.99% local", html);
        Assert.Contains("1 B", html);
    }

    private static async Task<string> Render(ContainerMemoryStatus? memory)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ContainerMemoryView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ContainerMemoryView.Memory)] = memory }));
            return component.ToHtmlString();
        });
    }
}
