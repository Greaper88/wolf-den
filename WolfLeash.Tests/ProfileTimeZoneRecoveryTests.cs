using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WolfLeash.Configuration;
using Xunit;

namespace WolfLeash.Tests;

public sealed class ProfileTimeZoneRecoveryTests
{
    private const string Journal = "pending-profile-update.json";
    private const string CatalogFile = "app-catalog.json";
    private static string JournalPath(TestContext test) => Path.Combine(test.Path, Journal);
    private static AppCatalog Restart(TestContext test) => new(test.Storage,
        test.Services.GetRequiredService<IServiceScopeFactory>(), test.Client);
    private static CatalogState Copy(CatalogState state) =>
        JsonSerializer.Deserialize<CatalogState>(JsonSerializer.Serialize(state))!;

    private static async Task Pending(TestContext test, CatalogState before, CatalogState after,
        JsonObject? expected, JsonObject? updated) => await test.Storage.WriteAsync(Journal,
        new { Version = 1, Before = before, After = after, Expected = expected, Updated = updated });

    [Fact]
    public async Task RecordsPrivateRecoveryDataBeforeWolfMutationAndCleansUpAfterSuccess()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        var observed = 0;
        test.Handler.AfterSuccessfulWrite = () =>
        {
            Assert.True(File.Exists(JournalPath(test)));
            var pending = (JsonObject)JsonNode.Parse(File.ReadAllText(JournalPath(test)))!;
            Assert.True(JsonNode.DeepEquals(original, pending["Expected"]));
            Assert.True(JsonNode.DeepEquals(updated, pending["Updated"]));
            Assert.Equal("America/Chicago", pending["After"]!["ProfileTimeZones"]!["profile"]!["TimeZone"]!.GetValue<string>());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(JournalPath(test)));
            observed++;
        };
        await test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting);
        Assert.Equal(2, observed);
        Assert.False(File.Exists(JournalPath(test)));
        Assert.Equal("America/Chicago", (await test.Catalog.GetAsync()).ProfileTimeZones["profile"].TimeZone);
    }

    [Fact]
    public async Task UnavailableJournalStoragePreventsAllWolfWrites()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        Directory.CreateDirectory(JournalPath(test));
        await Assert.ThrowsAsync<InvalidDataException>(() => test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting));
        Assert.Equal(0, test.Handler.Writes);
        Assert.True(JsonNode.DeepEquals(original, Assert.Single(test.Handler.Profiles)));
        Directory.Delete(JournalPath(test));
        Assert.Empty((await test.Catalog.GetAsync()).ProfileTimeZones);
    }

    [Fact]
    public async Task CatalogWriteFailureRetainsRestoreDataAndRecoversAfterRestart()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        original.Objects("apps")[0].Runner()["env"] = new JsonArray("USER=keep", "TZ=UTC");
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        var path = Path.Combine(test.Path, CatalogFile);
        var savedPath = path + ".saved";
        test.Handler.AfterSuccessfulWrite = () =>
        {
            if (test.Handler.Profiles.All(p => !JsonNode.DeepEquals(p, updated))) return;
            File.Move(path, savedPath);
            Directory.CreateDirectory(path);
            test.Handler.AfterSuccessfulWrite = null;
        };
        await Assert.ThrowsAnyAsync<IOException>(() => test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting));
        Assert.True(File.Exists(JournalPath(test)));
        Assert.True(JsonNode.DeepEquals(updated, Assert.Single(test.Handler.Profiles)));
        Directory.Delete(path);
        File.Move(savedPath, path);
        var writes = test.Handler.Writes;
        var recovered = await Restart(test).GetAsync();
        Assert.Equal(writes, test.Handler.Writes);
        Assert.False(File.Exists(JournalPath(test)));
        var clear = new ProfileEditor(updated, recovered) { TimeZone = "" };
        Assert.Equal(new[] { "USER=keep", "TZ=UTC" }, clear.Build().Profile.Objects("apps")[0].Runner().Strings("env"));
    }

    [Fact]
    public async Task FailedWolfReplacementRetainsOldMetadataAndResolvesJournal()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        test.Handler.FailAdds = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting));
        Assert.True(File.Exists(JournalPath(test)));
        Assert.True(JsonNode.DeepEquals(original, Assert.Single(test.Handler.Profiles)));
        var writes = test.Handler.Writes;
        Assert.Empty((await Restart(test).GetAsync()).ProfileTimeZones);
        Assert.Equal(writes, test.Handler.Writes);
        Assert.False(File.Exists(JournalPath(test)));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("replace")]
    public async Task RecoversSuccessfulWolfChangeWithoutMakingWolfWrites(string operation)
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        var updated = original.Copy(); updated["name"] = "updated";
        var before = await test.Catalog.GetAsync();
        if (operation == "delete") before.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        var after = Copy(before);
        if (operation == "delete") after.ProfileTimeZones.Remove("profile");
        else after.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        await test.Storage.WriteAsync(CatalogFile, before);
        JsonObject? expected = operation == "add" ? null : original;
        JsonObject? target = operation == "delete" ? null : updated;
        test.Handler.Profiles = target is null ? [] : [target.Copy()];
        await Pending(test, before, after, expected, target);
        var recovered = await Restart(test).GetAsync();
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(after), JsonSerializer.SerializeToNode(recovered)));
        Assert.Equal(0, test.Handler.Writes);
        Assert.False(File.Exists(JournalPath(test)));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("replace")]
    public async Task LeavesOldMetadataWhenWolfChangeDidNotHappen(string operation)
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        var updated = original.Copy(); updated["name"] = "updated";
        var before = await test.Catalog.GetAsync();
        var after = Copy(before); after.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        JsonObject? expected = operation == "add" ? null : original;
        JsonObject? target = operation == "delete" ? null : updated;
        test.Handler.Profiles = expected is null ? [] : [expected.Copy()];
        await Pending(test, before, after, expected, target);
        var recovered = await Restart(test).GetAsync();
        Assert.Empty(recovered.ProfileTimeZones);
        Assert.Equal(0, test.Handler.Writes);
        Assert.False(File.Exists(JournalPath(test)));
    }

    [Fact]
    public async Task CleansCommittedJournalWithoutConsultingChangedWolfProfile()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        var updated = original.Copy(); updated["name"] = "updated";
        var before = await test.Catalog.GetAsync();
        var after = Copy(before); after.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        await test.Storage.WriteAsync(CatalogFile, after);
        await Pending(test, before, after, original, updated);
        test.Handler.Profiles = [];
        Assert.Equal("America/Chicago", (await Restart(test).GetAsync()).ProfileTimeZones["profile"].TimeZone);
        Assert.False(File.Exists(JournalPath(test)));
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task ExternalWolfChangeKeepsJournalAndBlocksFurtherWrites()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        var updated = original.Copy(); updated["name"] = "updated";
        var external = original.Copy(); external["name"] = "external";
        var before = await test.Catalog.GetAsync();
        var after = Copy(before); after.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        test.Handler.Profiles = [external];
        await Pending(test, before, after, original, updated);
        var pending = File.ReadAllText(JournalPath(test));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Restart(test).GetAsync());
        Assert.Contains("no profile was changed", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Catalog.AddProfileAsync(ConfigurationTests.Profile("new")));
        Assert.Equal(pending, File.ReadAllText(JournalPath(test)));
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task ExternalCatalogChangeKeepsBothCatalogAndJournal()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        var updated = original.Copy(); updated["name"] = "updated";
        var before = await test.Catalog.GetAsync();
        var after = Copy(before); after.ProfileTimeZones["profile"] = new() { TimeZone = "America/Chicago" };
        var external = Copy(before); external.RemovedSources.Add("external");
        await test.Storage.WriteAsync(CatalogFile, external);
        await Pending(test, before, after, original, updated);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Restart(test).GetAsync());
        Assert.Contains("catalog changed", error.Message);
        Assert.True(File.Exists(JournalPath(test)));
        var actual = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(Path.Combine(test.Path, CatalogFile)))!;
        Assert.Contains("external", actual.RemovedSources);
        Assert.Equal(0, test.Handler.Writes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Expected\":{\"id\":\"profile\"}}")]
    public async Task InvalidJournalIsRetainedAndBlocksAllMutation(string raw)
    {
        await using var test = await TestContext.Create();
        await test.Catalog.GetAsync();
        await File.WriteAllTextAsync(JournalPath(test), raw);
        await Assert.ThrowsAsync<InvalidDataException>(() => test.Catalog.AddProfileAsync(ConfigurationTests.Profile("new")));
        Assert.Equal(raw, File.ReadAllText(JournalPath(test)));
        Assert.Equal(0, test.Handler.Writes);
    }

    [Fact]
    public async Task CleanupFailureBlocksNewUpdatesUntilOriginalJournalCanBeRemoved()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile", ConfigurationTests.App());
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        var saved = JournalPath(test) + ".saved";
        test.Handler.AfterSuccessfulWrite = () =>
        {
            if (test.Handler.Profiles.All(p => !JsonNode.DeepEquals(p, updated))) return;
            File.Move(JournalPath(test), saved);
            Directory.CreateDirectory(JournalPath(test));
            test.Handler.AfterSuccessfulWrite = null;
        };
        var failure = await Record.ExceptionAsync(() => test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        var writes = test.Handler.Writes;
        await Assert.ThrowsAsync<InvalidDataException>(() => test.Catalog.AddProfileAsync(ConfigurationTests.Profile("new")));
        Assert.True(Directory.Exists(JournalPath(test)));
        Assert.Equal(writes, test.Handler.Writes);
        Directory.Delete(JournalPath(test));
        File.Move(saved, JournalPath(test));
        Assert.Equal("America/Chicago", (await Restart(test).GetAsync()).ProfileTimeZones["profile"].TimeZone);
        Assert.False(File.Exists(JournalPath(test)));
        Assert.Equal(writes, test.Handler.Writes);
    }

    [Fact]
    public async Task UnchangedEmptyProfileSavesTimezoneWithoutJournalOrWolfMutation()
    {
        await using var test = await TestContext.Create();
        var original = ConfigurationTests.Profile("profile");
        original["icon_png_path"] = "";
        test.Handler.Profiles = [original.Copy()];
        var state = await test.Catalog.GetAsync();
        var editor = new ProfileEditor(original, state) { TimeZone = "America/Chicago" };
        var (updated, links) = editor.Build();
        await test.Catalog.SaveProfileAsync(original, updated, links, state, editor.TimeZoneSetting);
        Assert.Equal(0, test.Handler.Writes);
        Assert.False(File.Exists(JournalPath(test)));
        Assert.Equal("America/Chicago", (await Restart(test).GetAsync()).ProfileTimeZones["profile"].TimeZone);
    }
}
