using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WolfLeash.Configuration;
using Xunit;
using static WolfLeash.Tests.ConfigurationTests;

namespace WolfLeash.Tests;

public sealed class ProfileTimeZoneIntegrationTests
{
    private static JsonObject Docker(string id, params string[] env)
    {
        var app = App(id);
        app["title"] = id;
        app.Runner()["env"] = WolfJson.Array(env);
        return app;
    }

    private static async Task Save(TestContext test, JsonObject original, CatalogState state, ProfileEditor editor)
    {
        var (updated, links) = editor.Build();
        await test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting);
    }

    [Fact]
    public void EditorAppliesOnlyToEnabledDockerAppsWithoutChangingDefaultsOrProcessApps()
    {
        var enabled = new CatalogApp { Definition = Docker("linked", "TZ=Etc/UTC", "USER=retro") };
        var newlyEnabled = new CatalogApp { Definition = Docker("new", "OTHER=value") };
        var disabled = new CatalogApp { Definition = Docker("disabled", "TZ=Europe/London") };
        var actual = enabled.Definition.Copy(); actual["id"] = "actual";
        var process = new JsonObject { ["id"] = "process", ["title"] = "Process",
            ["runner"] = new JsonObject { ["type"] = "process", ["run_cmd"] = "echo test", ["env"] = new JsonArray("TZ=Etc/UTC") } };
        var original = Profile("one", actual, Docker("unlinked", "USER=second"), process);
        var state = new CatalogState
        {
            Apps = [enabled, newlyEnabled, disabled],
            Links = [new() { ProfileId = "one", AppId = "actual", CatalogId = enabled.Id, Baseline = enabled.Definition.Copy() }]
        };
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        editor.Apps.Single(a => a.CatalogApp?.Id == newlyEnabled.Id).Enabled = true;
        var (updated, _) = editor.Build();
        var dockerApps = updated.Objects("apps").Where(a => a.Runner().Text("type") == "docker").ToList();
        Assert.Equal(3, dockerApps.Count);
        Assert.All(dockerApps, a => Assert.Single(a.Runner().Strings("env"), v => v == "TZ=America/Chicago"));
        Assert.True(JsonNode.DeepEquals(process, updated.Objects("apps").Single(a => a.Text("id") == "process")));
        Assert.Equal(new[] { "TZ=Etc/UTC", "USER=retro" }, enabled.Definition.Runner().Strings("env"));
        Assert.Equal(new[] { "TZ=Europe/London" }, disabled.Definition.Runner().Strings("env"));
        Assert.Equal("TZ=Etc/UTC", original.Objects("apps")[0].Runner().Strings("env")[0]);
        Assert.Equal(3, editor.TimeZoneSetting!.Apps.Count);
    }

    [Fact]
    public async Task SaveReloadEnableLaterAndClearRestoreEachAppsOwnTimeZone()
    {
        await using var test = await TestContext.Create();
        var original = Profile("one", Docker("first", "USER=retro", "TZ=Europe/London"));
        test.Handler.Profiles = [original.Copy()];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var extra = new CatalogApp { Definition = Docker("later", "TZ=Asia/Tokyo", "EXTRA=one") };
        await test.Catalog.SaveAsync(extra);
        var state = await test.Catalog.GetAsync();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "America/Chicago" });

        var restarted = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client);
        state = await restarted.GetAsync();
        original = Assert.Single(test.Handler.Profiles).Copy();
        var editor = new ProfileEditor(original, state);
        Assert.Equal("America/Chicago", editor.TimeZone);
        editor.Apps.Single(a => a.CatalogApp?.Id == extra.Id).Enabled = true;
        await Save(test, original, state, editor);
        Assert.All(test.Handler.Profiles[0].Objects("apps"), a => Assert.Contains("TZ=America/Chicago", a.Runner().Strings("env")));

        state = await test.Catalog.GetAsync();
        original = test.Handler.Profiles[0].Copy();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "" });
        var apps = test.Handler.Profiles[0].Objects("apps");
        Assert.Equal(new[] { "USER=retro", "TZ=Europe/London" }, apps[0].Runner().Strings("env"));
        Assert.Equal(new[] { "TZ=Asia/Tokyo", "EXTRA=one" }, apps[1].Runner().Strings("env"));
        Assert.Empty((await test.Catalog.GetAsync()).ProfileTimeZones);
    }

    [Fact]
    public async Task DefaultUpdatesFlowThroughOverrideAndClearingRestoresUpdatedDefault()
    {
        await using var test = await TestContext.Create();
        var original = Profile("one", Docker("first", "TZ=Etc/UTC", "SETTING=old"));
        test.Handler.Profiles = [original.Copy()];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var state = await test.Catalog.GetAsync();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "America/Chicago" });
        var entry = (await test.Catalog.GetAsync()).Apps.Single();
        var previous = entry.Definition.Copy();
        entry.Definition.Runner()["env"] = new JsonArray("TZ=Asia/Tokyo", "SETTING=new", "ADDED=yes");
        await test.Catalog.SaveAsync(entry, previous);
        Assert.Equal(1, await test.Catalog.ApplyDefaultsAsync());
        Assert.Equal(new[] { "SETTING=new", "ADDED=yes", "TZ=America/Chicago" }, test.Handler.Profiles[0].Objects("apps")[0].Runner().Strings("env"));
        Assert.Equal(0, await test.Catalog.ApplyDefaultsAsync());

        state = await test.Catalog.GetAsync(); original = test.Handler.Profiles[0].Copy();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "" });
        Assert.Equal(new[] { "TZ=Asia/Tokyo", "SETTING=new", "ADDED=yes" }, test.Handler.Profiles[0].Objects("apps")[0].Runner().Strings("env"));
    }

    [Fact]
    public async Task ImportDoesNotPromotePersonalTimeZoneToSystemCatalog()
    {
        await using var test = await TestContext.Create();
        var original = Profile("one", Docker("first", "TZ=Etc/UTC", "USER=retro"));
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "America/Chicago" });
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var entry = Assert.Single((await test.Catalog.GetAsync()).Apps);
        Assert.Equal(new[] { "TZ=Etc/UTC", "USER=retro" }, entry.Definition.Runner().Strings("env"));
        Assert.Contains("TZ=America/Chicago", test.Handler.Profiles[0].Objects("apps")[0].Runner().Strings("env"));
    }

    [Fact]
    public async Task EmptyProfilesRememberPreferenceAndDeleteCleansMetadata()
    {
        await using var test = await TestContext.Create();
        var editor = new ProfileEditor(Profile("one"), new CatalogState()) { TimeZone = "America/Chicago" };
        var (created, _) = editor.Build();
        await test.Catalog.AddProfileAsync(created, editor.TimeZoneSetting);
        var extra = new CatalogApp { Definition = Docker("later", "USER=retro") };
        await test.Catalog.SaveAsync(extra);
        var state = await test.Catalog.GetAsync();
        Assert.Equal("America/Chicago", state.ProfileTimeZones["one"].TimeZone);
        editor = new ProfileEditor(created, state); editor.Apps.Single().Enabled = true;
        await Save(test, created, state, editor);
        Assert.Contains("TZ=America/Chicago", test.Handler.Profiles[0].Objects("apps")[0].Runner().Strings("env"));
        await test.Catalog.DeleteProfileAsync(test.Handler.Profiles[0].Copy());
        state = await test.Catalog.GetAsync();
        Assert.Empty(state.ProfileTimeZones); Assert.Empty(state.Links); Assert.Empty(test.Handler.Profiles);
    }

    [Fact]
    public async Task ConcurrentEmptyProfileTimeZoneEditCannotOverwriteNewPreference()
    {
        await using var test = await TestContext.Create();
        var original = Profile("one"); original["icon_png_path"] = "";
        test.Handler.Profiles = [original.Copy()];
        var oldState = await test.Catalog.GetAsync();
        var staleEditor = new ProfileEditor(original, oldState) { TimeZone = "Asia/Tokyo" };
        await Save(test, original, oldState, new ProfileEditor(original, oldState) { TimeZone = "America/Chicago" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(test, original, oldState, staleEditor));
        Assert.Equal("America/Chicago", (await test.Catalog.GetAsync()).ProfileTimeZones["one"].TimeZone);
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task WolfRestartRegeneratedIdsKeepDefaultLinksAndTimeZoneRestoration()
    {
        await using var test = await TestContext.Create();
        var original = Profile("one", Docker("first", "TZ=Etc/UTC", "SETTING=old"));
        test.Handler.Profiles = [original.Copy()];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var state = await test.Catalog.GetAsync();
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "America/Chicago" });
        // Wolf regenerates IDs from the title/icon when loading TOML at startup.
        test.Handler.Profiles[0].Objects("apps")[0]["id"] = "12345";
        test.Handler.Profiles[0].Objects("apps")[0]["icon_png_path"] = "external-icon.png";
        state = await test.Catalog.GetAsync(); original = test.Handler.Profiles[0].Copy();
        Assert.NotNull(new ProfileEditor(original, state).Apps.Single().CatalogApp);
        var entry = state.Apps.Single(); var previous = entry.Definition.Copy();
        entry.Definition.Runner()["env"] = new JsonArray("TZ=Asia/Tokyo", "SETTING=new");
        await test.Catalog.SaveAsync(entry, previous);
        Assert.Equal(1, await test.Catalog.ApplyDefaultsAsync());
        state = await test.Catalog.GetAsync();
        Assert.Equal("12345", Assert.Single(state.Links).AppId);
        Assert.True(state.ProfileTimeZones["one"].Apps.ContainsKey("12345"));
        Assert.Contains("SETTING=new", test.Handler.Profiles[0].Objects("apps")[0].Runner().Strings("env"));

        // Another restart followed by catalog import must not duplicate the link.
        test.Handler.Profiles[0].Objects("apps")[0]["id"] = "67890";
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        state = await test.Catalog.GetAsync(); original = test.Handler.Profiles[0].Copy();
        Assert.Equal("67890", Assert.Single(state.Links).AppId);
        Assert.Single(state.Apps);
        await Save(test, original, state, new ProfileEditor(original, state) { TimeZone = "" });
        var app = test.Handler.Profiles[0].Objects("apps")[0];
        Assert.Equal(new[] { "TZ=Asia/Tokyo", "SETTING=new" }, app.Runner().Strings("env"));
        Assert.Equal("external-icon.png", app.Text("icon_png_path"));
    }
}
