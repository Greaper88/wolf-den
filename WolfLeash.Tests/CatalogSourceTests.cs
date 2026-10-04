using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WolfLeash.Components.Classes;
using WolfLeash.Configuration;
using Xunit;

namespace WolfLeash.Tests;

public sealed class CatalogSourceTests
{
    [Fact]
    public async Task AdditionalPlasmaTemplateUsesPublishedImageAndForkArtwork()
    {
        await using var test = await TestContext.Create();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GowCatalog:AdditionalTemplates:0:Repository"] = "Greaper88/gow",
            ["GowCatalog:AdditionalTemplates:0:Ref"] = "plasma-container",
            ["GowCatalog:AdditionalTemplates:0:Path"] = "apps/plasma/assets/wolf.config.toml",
            ["GowCatalog:IconBaseUrl"] = "https://raw.githubusercontent.com/Greaper88/wolf-logos/main/itself",
            ["GowCatalog:Icons:plasma"] = "wolf-itself-plasma.png"
        }).Build();
        using var loader = new DefaultAppLoader(config, new HttpClient(new FakeGow()));
        var catalog = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client, loader, config);
        var state = await catalog.GetAsync();
        Assert.Equal(3, state.Apps.Count);
        var plasma = state.Apps.Single(a => a.Title == "Plasma");
        Assert.StartsWith("Greaper88/gow@plasma-container:", plasma.Source);
        Assert.Equal("ghcr.io/greaper88/plasma:fedora", plasma.Definition.Runner().Text("image"));
        Assert.EndsWith("/wolf-itself-plasma.png", plasma.Definition.Text("icon_png_path"));
        var existing = plasma.Definition.Copy(); existing["id"] = "existing-plasma"; existing["icon_png_path"] = "";
        test.Handler.Profiles = [ConfigurationTests.Profile("player", existing)];
        await catalog.ImportProfilesAsync(test.Handler.Profiles);
        Assert.Equal(3, (await catalog.GetAsync()).Apps.Count);
        await catalog.ApplyDefaultsAsync();
        Assert.EndsWith("/wolf-itself-plasma.png", test.Handler.Profiles[0].Objects("apps")[0].Text("icon_png_path"));
    }

    [Fact]
    public async Task FreshCatalogDiscoversRepositoryTemplatesWithForkIconsAndPreservesEdits()
    {
        await using var test = await TestContext.Create();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GowCatalog:IconBaseUrl"] = "https://raw.githubusercontent.com/Greaper88/wolf-logos/main/itself",
            ["GowCatalog:Icons:steam"] = "wolf-itself-steam.png"
        }).Build();
        var handler = new FakeGow();
        using var loader = new DefaultAppLoader(config, new HttpClient(handler));
        var catalog = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client, loader, config);
        var initial = await catalog.GetAsync();
        Assert.Equal(2, initial.Apps.Count);
        Assert.True(initial.GowSeedComplete);
        Assert.Equal("", initial.Apps.Single(a => a.Title == "Unknown").Definition.Text("icon_png_path"));
        var steam = initial.Apps.Single(a => a.Title == "Steam");
        Assert.EndsWith("/itself/wolf-itself-steam.png", steam.Definition.Text("icon_png_path"));
        var previous = steam.Definition.Copy();
        steam.Definition.Runner()["mounts"] = new JsonArray("/shared:/games");
        steam.Definition["icon_png_path"] = "https://example.test/custom.png";
        await catalog.SaveAsync(steam, previous);
        await catalog.RefreshGowAsync();
        var current = (await catalog.GetAsync()).Apps.Single(a => a.Id == steam.Id);
        Assert.Equal("https://example.test/custom.png", current.Definition.Text("icon_png_path"));
        Assert.Equal(new[] { "/shared:/games" }, current.Definition.Runner().Strings("mounts"));
        await catalog.DeleteAsync(steam.Id);
        await catalog.RefreshGowAsync();
        Assert.DoesNotContain((await catalog.GetAsync()).Apps, a => a.Title == "Steam");
        handler.FailAll = true;
        var restarted = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client, loader, config);
        Assert.Single((await restarted.GetAsync()).Apps);
        Assert.Null(restarted.SourceStatus);
    }

    [Fact]
    public async Task PartialTemplateFailureKeepsSuccessfulAppsAndRetriesOnlyWithoutOverwriting()
    {
        await using var test = await TestContext.Create();
        var config = new ConfigurationBuilder().Build();
        var handler = new FakeGow { FailUnknown = true };
        using var loader = new DefaultAppLoader(config, new HttpClient(handler));
        var catalog = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client, loader, config);
        Assert.Single((await catalog.GetAsync()).Apps);
        Assert.Contains("unknown", catalog.SourceStatus);
        handler.FailUnknown = false;
        Assert.Equal(1, await catalog.RefreshGowAsync());
        Assert.Equal(2, (await catalog.GetAsync()).Apps.Count);
        Assert.Null(catalog.SourceStatus);
    }

    [Fact]
    public async Task OfflineFreshInstallStillAllowsManualAppsAndLaterSeeding()
    {
        await using var test = await TestContext.Create();
        var config = new ConfigurationBuilder().Build();
        var handler = new FakeGow { FailAll = true };
        using var loader = new DefaultAppLoader(config, new HttpClient(handler));
        var catalog = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client, loader, config);
        Assert.Empty((await catalog.GetAsync()).Apps);
        Assert.NotNull(catalog.SourceStatus);
        await catalog.SaveAsync(new CatalogApp { Definition = ConfigurationTests.App() });
        Assert.Single((await catalog.GetAsync()).Apps);
        handler.FailAll = false;
        await catalog.RefreshGowAsync();
        Assert.Equal(3, (await catalog.GetAsync()).Apps.Count);
    }

    [Fact]
    public async Task ExplicitBlankIconOverrideSurvivesDefaultIconChanges()
    {
        await using var test = await TestContext.Create();
        var app = ConfigurationTests.App();
        var original = ConfigurationTests.Profile("test", app);
        test.Handler.Profiles = [original.Copy()];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state);
        editor.Apps[0].OverrideIcon = true;
        editor.Apps[0].Icon = "";
        var (updated, links) = editor.Build();
        await test.Catalog.SaveProfileAsync(original, updated, links, state);
        var entry = (await test.Catalog.GetAsync()).Apps.Single();
        var previous = entry.Definition.Copy();
        entry.Definition["icon_png_path"] = "https://example.test/new-default.png";
        await test.Catalog.SaveAsync(entry, previous);
        await test.Catalog.ApplyDefaultsAsync();
        Assert.Equal("", test.Handler.Profiles[0].Objects("apps")[0].Text("icon_png_path"));
        var reloaded = new ProfileEditor(test.Handler.Profiles[0], await test.Catalog.GetAsync());
        Assert.True(reloaded.Apps[0].OverrideIcon);
        reloaded.Apps[0].OverrideIcon = false;
        Assert.Equal("https://example.test/new-default.png", reloaded.Build().Profile.Objects("apps")[0].Text("icon_png_path"));
    }

    [Fact]
    public async Task IconVariantsImportAsOneAppAndKeepProfileOverride()
    {
        await using var test = await TestContext.Create();
        var first = ConfigurationTests.App("one"); first["icon_png_path"] = "https://example.test/default.png";
        var second = first.Copy(); second["id"] = "two"; second["icon_png_path"] = "https://example.test/user.png";
        await test.Catalog.ImportProfilesAsync([ConfigurationTests.Profile("one", first), ConfigurationTests.Profile("two", second)]);
        var state = await test.Catalog.GetAsync();
        Assert.Single(state.Apps);
        Assert.Equal("https://example.test/user.png", state.Links.Single(l => l.ProfileId == "two").IconOverride);
    }

    [Fact]
    public async Task OlderRunnerSettingsLinkToGowDefaultsWithoutChangingProfileLaunchSettings()
    {
        await using var test = await TestContext.Create();
        var standard = ConfigurationTests.App("catalog"); standard["title"] = "Prism Launcher";
        await test.Catalog.SaveAsync(new CatalogApp { Definition = standard });
        var old = standard.Copy(); old["id"] = "existing"; old["title"] = "Prismlauncher";
        old.Runner()["env"] = new JsonArray("USER=legacy");
        old.Runner()["name"] = "OldPrismRunner";
        old.Runner()["base_create_json"] = "{\"HostConfig\":{\"CapAdd\":[\"SYS_ADMIN\"]}}";
        test.Handler.Profiles = [ConfigurationTests.Profile("player", old)];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var state = await test.Catalog.GetAsync();
        Assert.Single(state.Apps);
        var updated = new ProfileEditor(test.Handler.Profiles[0], state).Build().Profile.Objects("apps")[0];
        Assert.True(JsonNode.DeepEquals(old, updated));
        var entry = state.Apps.Single(); var previous = entry.Definition.Copy();
        entry.Definition.Runner()["mounts"] = new JsonArray("/shared:/games");
        await test.Catalog.SaveAsync(entry, previous);
        await test.Catalog.ApplyDefaultsAsync();
        var result = test.Handler.Profiles[0].Objects("apps")[0];
        Assert.Equal(old.Runner().Text("base_create_json"), result.Runner().Text("base_create_json"));
        Assert.Equal(old.Runner().Strings("env"), result.Runner().Strings("env"));
        Assert.Equal("OldPrismRunner", result.Runner().Text("name"));
        Assert.Equal(new[] { "/shared:/games" }, result.Runner().Strings("mounts"));
    }
}

internal sealed class FakeGow : HttpMessageHandler
{
    public bool FailAll { get; set; }
    public bool FailUnknown { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailAll || (FailUnknown && request.RequestUri!.AbsolutePath.Contains("unknown")))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 Not Found") });
        if (request.RequestUri!.Host == "api.github.com")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"truncated":false,"tree":[
                  {"type":"blob","path":"apps/steam/assets/wolf.config.toml"},
                  {"type":"blob","path":"apps/unknown/assets/wolf.config.toml"},
                  {"type":"blob","path":"images/base/Dockerfile"}]}
                """, Encoding.UTF8, "application/json") });
        var title = request.RequestUri.AbsolutePath.Contains("/steam/") ? "Steam" :
            request.RequestUri.AbsolutePath.Contains("/plasma/") ? "Plasma" : "Unknown";
        var image = title == "Plasma" ? "ghcr.io/greaper88/plasma:fedora" : $"gow/{title}:edge";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"""
            [[apps]]
            title = '{title}'
            icon_png_path = 'https://example.test/upstream.png'
            [apps.runner]
            type = 'docker'
            name = '{title}'
            image = '{image}'
            """) });
    }
}
