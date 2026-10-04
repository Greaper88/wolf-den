using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WolfLeash.Components.Classes;
using WolfLeash.Configuration;
using WolfLeash.Database;
using Xunit;

namespace WolfLeash.Tests;

public sealed class ConfigurationTests
{
    internal static JsonObject App(string id = "steam", params string[] mounts) => new()
    {
        ["id"] = id, ["title"] = "Steam", ["icon_png_path"] = "", ["render_node"] = "/dev/dri/renderD129",
        ["start_audio_server"] = false, ["start_virtual_compositor"] = false, ["support_hdr"] = true,
        ["h264_gst_pipeline"] = "custom pipeline", ["gpu_auto_select"] = true,
        ["fork_field"] = new JsonObject { ["keep"] = true },
        ["runner"] = new JsonObject
        {
            ["type"] = "docker", ["name"] = "WolfSteam", ["image"] = "steam:old",
            ["mounts"] = WolfJson.Array(mounts), ["env"] = new JsonArray("USER=retro"),
            ["devices"] = new JsonArray(), ["ports"] = new JsonArray(), ["base_create_json"] = "{}",
            ["fork_runner_field"] = "keep"
        }
    };
    internal static JsonObject Profile(string id, params JsonObject[] apps) => new()
    {
        ["id"] = id, ["name"] = id, ["pin"] = new JsonArray(0, 1, 2, 3),
        ["apps"] = WolfJson.Array(apps), ["fork_profile_field"] = "preserve"
    };

    [Fact]
    public void MountRebasePreservesReplacementAdditionAndRemoval()
    {
        string[] previous = ["/shared:/games:ro", "/tools:/tools", "/old:/cache"];
        string[] next = ["/new-shared:/games:rw", "/tools2:/tools", "/new-cache:/cache", "/new:/new"];
        string[] actual = ["/private:/games:rw", "/old:/cache", "/user:/saves"];
        Assert.Equal(new[] { "/private:/games:rw", "/new-cache:/cache", "/new:/new", "/user:/saves" }, MountDefaults.Rebase(previous, next, actual));
    }

    [Fact]
    public void AppRebaseKeepsIdsForkFieldsAndProfileSpecificValues()
    {
        var original = App("catalog", "/shared:/games");
        var next = original.Copy(); next.Runner()["image"] = "steam:new";
        next.Runner()["mounts"] = new JsonArray("/new:/games", "/shared-tools:/tools");
        var actual = original.Copy(); actual["id"] = "profile-app"; actual["render_node"] = "/dev/dri/renderD130";
        actual.Runner()["env"] = new JsonArray("USER=custom");
        var result = MountDefaults.RebaseApp(original, next, actual);
        Assert.Equal("steam:new", result.Runner().Text("image"));
        Assert.Equal("profile-app", result.Text("id"));
        Assert.Equal("/dev/dri/renderD130", result.Text("render_node"));
        Assert.True(result["gpu_auto_select"]!.GetValue<bool>());
        Assert.Equal("keep", result.Runner().Text("fork_runner_field"));
        Assert.Equal(new[] { "USER=custom" }, result.Runner().Strings("env"));
        Assert.Equal(new[] { "/new:/games", "/shared-tools:/tools" }, result.Runner().Strings("mounts"));
    }

    [Theory]
    [InlineData("/src:relative")]
    [InlineData(":/games")]
    [InlineData("/src:/games:")]
    public void RejectsInvalidMounts(string mount) => Assert.Throws<InvalidDataException>(() => MountDefaults.Target(mount));

    [Fact]
    public void RejectsDuplicateContainerDestinations() => Assert.Throws<InvalidDataException>(() => MountDefaults.Index(["/one:/games", "/two:/games/"]));

    [Fact]
    public void WildlifeSteamTemplateParsesMultilineJsonAndEmptyArrays()
    {
        var apps = DefaultAppLoader.ParseApps(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/steam.toml")));
        var app = Assert.Single(apps);
        Assert.Equal("Steam", app.Text("title"));
        Assert.Empty(app.Runner().Strings("mounts"));
        Assert.IsType<JsonObject>(JsonNode.Parse(app.Runner().Text("base_create_json")));
    }

    [Fact]
    public void OptionalTemplateFieldsDefaultWithoutDroppingUnknownFields()
    {
        var app = Assert.Single(DefaultAppLoader.ParseApps("""
            [[apps]]
            title = 'Steam'
            gpu_auto_select = true
            [apps.video]
            source = 'custom'
            [apps.runner]
            type = 'Docker'
            name = 'Steam'
            image = 'steam:test'
            fork_option = 42
            """));
        Assert.Empty(app.Runner().Strings("mounts"));
        Assert.Equal("{}", app.Runner().Text("base_create_json"));
        Assert.True(app["gpu_auto_select"]!.GetValue<bool>());
        Assert.Equal(42, app.Runner()["fork_option"]!.GetValue<int>());
        Assert.Equal("custom", app["video"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public void AcceptsInlineArraysAndProcessProfileApps()
    {
        var apps = DefaultAppLoader.ParseApps("""
            [[profiles]]
            id = 'test'
            apps = [{ title = 'Test', runner = { type = 'process', run_cmd = 'echo test' } }]
            """);
        Assert.Equal("echo test", Assert.Single(apps).Runner().Text("run_cmd"));
    }

    [Fact]
    public void SyntaxFailureIncludesTheSource()
    {
        var error = Assert.Throws<InvalidDataException>(() => DefaultAppLoader.ParseApps("[[apps]\ntitle = 'broken'", "steam.toml"));
        Assert.Contains("steam.toml", error.Message);
    }

    [Fact]
    public void ProfileEditorClonesDefaultsAndPreservesExistingProcessApps()
    {
        var entry = new CatalogApp { Definition = App("template", "/shared:/games") };
        var process = new JsonObject { ["id"] = "process", ["title"] = "Process", ["runner"] = new JsonObject { ["type"] = "process", ["run_cmd"] = "echo yes" } };
        var original = Profile("test", process);
        var state = new CatalogState { Apps = [entry] };
        var editor = new ProfileEditor(original, state);
        var choice = editor.Apps.Single(a => a.CatalogApp is not null); choice.Enabled = true;
        choice.MountsText = "/user:/games";
        var (result, links) = editor.Build();
        Assert.Equal("preserve", result.Text("fork_profile_field"));
        Assert.True(JsonNode.DeepEquals(original.Objects("apps")[0], result.Objects("apps")[0]));
        Assert.Equal(new[] { "/shared:/games" }, entry.Definition.Runner().Strings("mounts"));
        Assert.NotEqual("template", choice.App.Text("id"));
        Assert.Single(links);
        Assert.Equal("0123", editor.Pin);
        Assert.Single(original.Objects("apps"));
    }

    [Fact]
    public async Task CatalogImportCollapsesMountVariantsAndSurvivesRestartOffline()
    {
        await using var test = await TestContext.Create();
        var first = Profile("one", App("a", "/shared:/games"));
        var second = Profile("two", App("b", "/user:/games"));
        await test.Catalog.ImportProfilesAsync([first, second]);
        await test.Catalog.ImportProfilesAsync([first, second]);
        var restarted = new AppCatalog(test.Storage, test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client);
        var state = await restarted.GetAsync();
        Assert.Single(state.Apps); Assert.Equal(2, state.Links.Count);
        var editor = new ProfileEditor(second, state);
        Assert.Single(MountDefaults.Differences(editor.Apps[0].Defaults, editor.Apps[0].Mounts));
        await restarted.DeleteAsync(state.Apps[0].Id);
        Assert.Empty((await restarted.GetAsync()).Apps);
        Assert.Single(second.Objects("apps"));
    }

    [Fact]
    public async Task ApplyDefaultsPreservesOverridesAndCanBeRetried()
    {
        await using var test = await TestContext.Create();
        var first = Profile("one", App("a", "/shared:/games"));
        var second = Profile("two", App("b", "/user:/games"));
        test.Handler.Profiles = [first, second];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var entry = (await test.Catalog.GetAsync()).Apps.Single();
        var previous = entry.Definition.Copy();
        entry.Definition.Runner()["mounts"] = new JsonArray("/new-shared:/games", "/tools:/tools");
        await test.Catalog.SaveAsync(entry, previous);
        Assert.Equal(2, await test.Catalog.ApplyDefaultsAsync());
        Assert.Equal(new[] { "/new-shared:/games", "/tools:/tools" }, test.Handler.Profiles.Single(p => p.Text("id") == "one").Objects("apps")[0].Runner().Strings("mounts"));
        Assert.Equal(new[] { "/user:/games", "/tools:/tools" }, test.Handler.Profiles.Single(p => p.Text("id") == "two").Objects("apps")[0].Runner().Strings("mounts"));
        Assert.Equal(0, await test.Catalog.ApplyDefaultsAsync());
        Assert.All(test.Handler.Profiles, p => Assert.Equal("preserve", p.Text("fork_profile_field")));
    }

    [Fact]
    public async Task ExternalProfileEditPreventsAnyWrites()
    {
        await using var test = await TestContext.Create();
        var expected = Profile("test", App()); test.Handler.Profiles = [expected.Copy()];
        test.Handler.Profiles[0]["name"] = "changed in Wolf UI";
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Client.ReplaceProfileAsync(expected, expected.Copy()));
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task PartialDefaultApplicationRetainsProgressAndResumesAfterFailedProfile()
    {
        await using var test = await TestContext.Create();
        test.Handler.Profiles = [Profile("one", App("a", "/shared:/games")), Profile("two", App("b", "/private:/games"))];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var entry = (await test.Catalog.GetAsync()).Apps.Single();
        var previous = entry.Definition.Copy();
        entry.Definition.Runner()["mounts"] = new JsonArray("/new-shared:/games", "/tools:/tools");
        await test.Catalog.SaveAsync(entry, previous);
        test.Handler.FailAddForProfile = "two";
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Catalog.ApplyDefaultsAsync());
        var partial = await test.Catalog.GetAsync();
        Assert.True(JsonNode.DeepEquals(entry.Definition, partial.Links.Single(l => l.ProfileId == "one").Baseline));
        Assert.True(JsonNode.DeepEquals(previous, partial.Links.Single(l => l.ProfileId == "two").Baseline));
        Assert.Equal(1, await test.Catalog.ApplyDefaultsAsync());
        Assert.Equal(new[] { "/private:/games", "/tools:/tools" }, test.Handler.Profiles.Single(p => p.Text("id") == "two").Objects("apps")[0].Runner().Strings("mounts"));
    }

    [Fact]
    public async Task StaleCatalogAndProfileEditorsCannotOverwriteNewDefaults()
    {
        await using var test = await TestContext.Create();
        var original = Profile("test", App()); test.Handler.Profiles = [original.Copy()];
        await test.Catalog.ImportProfilesAsync(test.Handler.Profiles);
        var state = await test.Catalog.GetAsync();
        var (updated, links) = new ProfileEditor(original, state).Build();
        var newer = (await test.Catalog.GetAsync()).Apps.Single();
        var previous = newer.Definition.Copy();
        newer.Definition.Runner()["image"] = "steam:new";
        await test.Catalog.SaveAsync(newer, previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Catalog.SaveAsync(state.Apps.Single(), previous));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Catalog.SaveProfileAsync(original, updated, links, state));
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task FailedAddRestoresProfileAndRetainsRecoveryCopy()
    {
        await using var test = await TestContext.Create();
        var expected = Profile("test", App()); test.Handler.Profiles = [expected.Copy()]; test.Handler.FailAdds = 1;
        var updated = expected.Copy(); updated["name"] = "new";
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Client.ReplaceProfileAsync(expected, updated));
        Assert.True(JsonNode.DeepEquals(expected, Assert.Single(test.Handler.Profiles)));
        Assert.Single(Directory.GetFiles(test.Storage.DirectoryPath, "profile-recovery-*.json"));
    }

    [Fact]
    public async Task LostSuccessResponseDoesNotDuplicateProfile()
    {
        await using var test = await TestContext.Create();
        var expected = Profile("test", App()); test.Handler.Profiles = [expected.Copy()]; test.Handler.LoseAddReply = true;
        var updated = expected.Copy(); updated["name"] = "new";
        await test.Client.ReplaceProfileAsync(expected, updated);
        Assert.True(JsonNode.DeepEquals(updated, Assert.Single(test.Handler.Profiles)));
    }

    [Fact]
    public async Task LegacyDatabaseUpgradePreservesAppsAndSharedRunner()
    {
        await using var test = await TestContext.Create(migrate: false);
        using var scope = test.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WolfLeashDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("20251118203127_ChangedClientsIconType");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Runners (Id,Name,Image,Type,Mounts,Env,Devices,Ports,BaseCreateJson)
            VALUES (1,'Steam','steam:old','docker','["/shared:/games"]','[]','[]','[]','{{}}');
            INSERT INTO Apps (Id,AppId,Title,RunnerId) VALUES (1,'one','Steam',1),(2,'two','Steam copy',1);
            """);
        await db.Database.MigrateAsync();
        var apps = await db.Apps.Include(a => a.Runner).ToListAsync();
        Assert.Equal(2, apps.Count);
        Assert.All(apps, a => Assert.Equal(new[] { "/shared:/games" }, a.Runner.Mounts));
        Assert.NotEqual(apps[0].Runner.Id, apps[1].Runner.Id);
        Assert.Equal(2, (await test.Catalog.GetAsync()).Apps.Count);
    }
}

internal sealed class TestContext : IAsyncDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wolf-den-tests-" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Services { get; }
    public DenStorage Storage { get; }
    public FakeWolf Handler { get; } = new();
    public WolfConfigurationClient Client { get; }
    public AppCatalog Catalog { get; }
    private TestContext()
    {
        Directory.CreateDirectory(Path);
        Storage = new DenStorage(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path }).Build());
        var services = new ServiceCollection();
        services.AddScoped(_ => new WolfLeashDbContext(new DbContextOptionsBuilder<WolfLeashDbContext>().UseSqlite($"Data Source={Path}/database.db")));
        Services = services.BuildServiceProvider();
        Client = new WolfConfigurationClient(new HttpClient(Handler) { BaseAddress = new Uri("http://wolf.test/") }, Storage);
        Catalog = new AppCatalog(Storage, Services.GetRequiredService<IServiceScopeFactory>(), Client);
    }
    public static async Task<TestContext> Create(bool migrate = true)
    {
        var test = new TestContext();
        if (migrate)
        {
            using var scope = test.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WolfLeashDbContext>().Database.MigrateAsync();
        }
        return test;
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Path, true);
    }
}

internal sealed class FakeWolf : HttpMessageHandler
{
    public List<JsonObject> Profiles { get; set; } = [];
    public int FailAdds { get; set; }
    public int Writes { get; private set; }
    public bool LoseAddReply { get; set; }
    public string? FailAddForProfile { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return Reply(new JsonObject { ["success"] = true, ["profiles"] = WolfJson.Array(Profiles) });
        Writes++;
        var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        if (request.RequestUri!.AbsolutePath.EndsWith("/remove")) Profiles.RemoveAll(p => p.Text("id") == body.Text("id"));
        else
        {
            if (FailAddForProfile == body.Text("id")) { FailAddForProfile = null; return Reply(new JsonObject { ["error"] = "simulated profile failure" }, HttpStatusCode.InternalServerError); }
            if (FailAdds > 0) { FailAdds--; return Reply(new JsonObject { ["error"] = "simulated failure" }, HttpStatusCode.InternalServerError); }
            Profiles.Add(body);
            if (LoseAddReply) { LoseAddReply = false; throw new HttpRequestException("lost response"); }
        }
        return Reply(new JsonObject { ["success"] = true });
    }
    private static HttpResponseMessage Reply(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
