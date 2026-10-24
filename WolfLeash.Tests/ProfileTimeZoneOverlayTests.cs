using System.Text.Json;
using System.Text.Json.Nodes;
using WolfLeash.Configuration;
using Xunit;

namespace WolfLeash.Tests;

public sealed class ProfileTimeZoneOverlayTests
{
    private static JsonObject App(params string[] environment)
    {
        var app = ConfigurationTests.App("profile-app");
        app.Runner()["env"] = WolfJson.Array(environment);
        return app;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" UTC ", "UTC")]
    [InlineData("America/Chicago", "America/Chicago")]
    [InlineData("Etc/UTC", "Etc/UTC")]
    public void NormalizesInstalledIanaTimeZones(string? input, string? expected) =>
        Assert.Equal(expected, ProfileTimeZones.Normalize(input));

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("Central Standard Time")]
    [InlineData("UTC-11")]
    [InlineData("../America/Chicago")]
    [InlineData("/usr/share/zoneinfo/America/Chicago")]
    [InlineData("America/Chicago\nTZ=UTC")]
    public void RejectsInvalidOrNonIanaTimeZones(string input) =>
        Assert.Throws<InvalidDataException>(() => ProfileTimeZones.Normalize(input));

    [Fact]
    public void ReplacesDuplicateTimeZonesWithoutChangingOtherEnvironmentOrInput()
    {
        var original = App("TZ=UTC", "USER=someone", "TZ=Etc/GMT+3", "VALUE=one=two", "TZ", "TZDIR=/custom", "OTHER_TZ=keep", "tz=lowercase");
        var before = original.ToJsonString();
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        Assert.Equal(new[] { "USER=someone", "VALUE=one=two", "TZDIR=/custom", "OTHER_TZ=keep", "tz=lowercase", "TZ=America/Chicago" }, actual.Runner().Strings("env"));
        Assert.Equal(before, original.ToJsonString());
        Assert.Equal("America/Chicago", setting.TimeZone);
        Assert.True(JsonNode.DeepEquals(original, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresMissingOrNullEnvironment(bool explicitlyNull)
    {
        var original = App();
        if (explicitlyNull) original.Runner()["env"] = null;
        else original.Runner().Remove("env");
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "UTC", setting);
        Assert.Equal(new[] { "TZ=UTC" }, actual.Runner().Strings("env"));
        Assert.True(JsonNode.DeepEquals(original, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Fact]
    public void PreservesNonTimeZoneEditsMadeDuringOverride()
    {
        var original = App("USER=before", "TZ=UTC", "TOKEN=one=two");
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        actual.Runner()["env"] = new JsonArray("USER=after", "TOKEN=one=two", "EXTRA=yes", "TZ=America/Chicago");
        Assert.Equal(new[] { "USER=after", "TZ=UTC", "TOKEN=one=two", "EXTRA=yes" }, ProfileTimeZones.Unapply(actual, setting).Runner().Strings("env"));
    }

    [Theory]
    [InlineData("TZ=Europe/London")]
    [InlineData("TZ")]
    [InlineData("")]
    public void ClearingPreservesExternalTimeZoneChangeOrRemoval(string replacement)
    {
        var original = App("TZ=UTC", "USER=keep");
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        actual.Runner()["env"] = replacement.Length == 0 ? new JsonArray("USER=keep") : new JsonArray("USER=keep", replacement);
        Assert.True(JsonNode.DeepEquals(actual, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Fact]
    public void ClearingPreservesExternallyDuplicatedTimeZone()
    {
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(App("TZ=UTC"), "America/Chicago", setting);
        actual.Runner()["env"] = new JsonArray("TZ=America/Chicago", "TZ=Europe/London");
        Assert.True(JsonNode.DeepEquals(actual, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Fact]
    public void RemovesBaseJsonConflictsAndRestoresExactOriginalFormatting()
    {
        var original = App("TZ=UTC", "USER=keep");
        original.Runner()["base_create_json"] = "{ \"Env\": [\"FIRST=one=two\", \"TZ=Etc/GMT+3\", \"TZ\", \"LAST=keep\"], \"HostConfig\": { \"NetworkMode\": \"host\" } }";
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        var options = (JsonObject)JsonNode.Parse(actual.Runner().Text("base_create_json"))!;
        Assert.Equal(new[] { "FIRST=one=two", "LAST=keep" }, options.Strings("Env"));
        Assert.Equal("host", options["HostConfig"]!["NetworkMode"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Fact]
    public void LeavesBaseJsonFormattingUntouchedWhenThereIsNoTimeZone()
    {
        var original = App("USER=keep");
        original.Runner()["base_create_json"] = "{ \"Env\": [ \"USER=other\" ], \"HostConfig\": {} }";
        var actual = ProfileTimeZones.Apply(original, "UTC", new());
        Assert.Equal(original.Runner().Text("base_create_json"), actual.Runner().Text("base_create_json"));
    }

    [Fact]
    public void RestoresBaseTimeZoneAlongsideExternalNonTimeZoneJsonEdits()
    {
        var original = App("USER=keep");
        original.Runner()["base_create_json"] = """{"Env":["USER=before","TZ=UTC"],"HostConfig":{"NetworkMode":"host"}}""";
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        actual.Runner()["base_create_json"] = """{"Env":["USER=after","NEW=yes"],"HostConfig":{"NetworkMode":"bridge"}}""";
        var restored = ProfileTimeZones.Unapply(actual, setting);
        var options = (JsonObject)JsonNode.Parse(restored.Runner().Text("base_create_json"))!;
        Assert.Equal(new[] { "USER=after", "TZ=UTC", "NEW=yes" }, options.Strings("Env"));
        Assert.Equal("bridge", options["HostConfig"]!["NetworkMode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"Env\":[\"TZ=Europe/London\"]}")]
    [InlineData("{\"HostConfig\":{}}")]
    [InlineData("invalid external edit")]
    public void ClearingPreservesExplicitBaseJsonEdits(string raw)
    {
        var original = App("USER=keep");
        original.Runner()["base_create_json"] = """{"Env":["TZ=UTC"]}""";
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        actual.Runner()["base_create_json"] = raw;
        Assert.Equal(raw, ProfileTimeZones.Unapply(actual, setting).Runner().Text("base_create_json"));
    }

    [Fact]
    public void SettingSurvivesPersistenceAndCanChangeTimeZoneThenClear()
    {
        var original = App("USER=keep", "TZ=UTC");
        var first = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", first);
        first = JsonSerializer.Deserialize<ProfileTimeZoneSetting>(JsonSerializer.Serialize(first))!;
        var second = new ProfileTimeZoneSetting();
        var changed = ProfileTimeZones.Apply(ProfileTimeZones.Unapply(actual, first), "Europe/London", second);
        Assert.Equal(new[] { "USER=keep", "TZ=Europe/London" }, changed.Runner().Strings("env"));
        Assert.True(JsonNode.DeepEquals(original, ProfileTimeZones.Unapply(changed, second)));
    }

    [Fact]
    public void UndoBeforeRebasePropagatesNewDefaultEnvironmentThenRestoresIt()
    {
        var baseline = App("USER=keep", "TZ=UTC");
        var next = baseline.Copy(); next.Runner()["env"] = new JsonArray("USER=updated", "TZ=Europe/London", "NEW=yes");
        var first = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(baseline, "America/Chicago", first);
        var rebased = MountDefaults.RebaseApp(baseline, next, ProfileTimeZones.Unapply(actual, first));
        var second = new ProfileTimeZoneSetting();
        var overridden = ProfileTimeZones.Apply(rebased, "America/Chicago", second);
        Assert.Equal(new[] { "USER=updated", "NEW=yes", "TZ=America/Chicago" }, overridden.Runner().Strings("env"));
        Assert.True(JsonNode.DeepEquals(next, ProfileTimeZones.Unapply(overridden, second)));
    }

    [Fact]
    public void TracksEachAppIndependentlyAndLeavesUnknownAppUnchanged()
    {
        var first = App("TZ=UTC");
        var second = App("TZ=Europe/London"); second["id"] = "second";
        var untracked = App("TZ=Europe/Paris"); untracked["id"] = "untracked";
        var setting = new ProfileTimeZoneSetting();
        var a = ProfileTimeZones.Apply(first, "America/Chicago", setting);
        var b = ProfileTimeZones.Apply(second, "America/Chicago", setting);
        Assert.Equal(2, setting.Apps.Count);
        Assert.True(JsonNode.DeepEquals(first, ProfileTimeZones.Unapply(a, setting)));
        Assert.True(JsonNode.DeepEquals(second, ProfileTimeZones.Unapply(b, setting)));
        Assert.True(JsonNode.DeepEquals(untracked, ProfileTimeZones.Unapply(untracked, setting)));
        Assert.True(JsonNode.DeepEquals(first, ProfileTimeZones.Unapply(first, null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresTimeZoneAfterWolfRegeneratesAppIdIncludingChangedIcon(bool changeIcon)
    {
        var original = App("USER=keep", "TZ=Europe/London");
        original.Runner()["base_create_json"] = """{"Env":["OTHER=one=two","TZ=UTC"]}""";
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(original, "America/Chicago", setting);
        actual["id"] = "regenerated-id";
        if (changeIcon) actual["icon_png_path"] = "https://example.test/new-icon.png";
        actual.Runner()["image"] = "steam:updated";
        Assert.Equal("profile-app", ProfileTimeZones.ResolveTrackedAppId(actual, setting));
        var restored = ProfileTimeZones.Unapply(actual, setting);
        Assert.Equal(new[] { "USER=keep", "TZ=Europe/London" }, restored.Runner().Strings("env"));
        Assert.Equal(original.Runner().Text("base_create_json"), restored.Runner().Text("base_create_json"));
        Assert.Equal("regenerated-id", restored.Text("id"));
        Assert.Equal(actual.Text("icon_png_path"), restored.Text("icon_png_path"));
        Assert.Equal("steam:updated", restored.Runner().Text("image"));
        Assert.Equal(new[] { "USER=keep", "TZ=America/Chicago" }, actual.Runner().Strings("env"));
    }

    [Fact]
    public void ExternalTimeZoneEditSurvivesIdentityFallback()
    {
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(App("USER=keep", "TZ=UTC"), "America/Chicago", setting);
        actual["id"] = "regenerated-id";
        actual.Runner()["env"] = new JsonArray("USER=changed", "TZ=Europe/London");
        Assert.True(JsonNode.DeepEquals(actual, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Fact]
    public void AmbiguousIdentityRetainsRestorationDataInsteadOfGuessing()
    {
        var first = App("TZ=UTC");
        var second = App("TZ=Europe/London"); second["id"] = "second";
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(first, "America/Chicago", setting);
        ProfileTimeZones.Apply(second, "America/Chicago", setting);
        Assert.Equal("profile-app", ProfileTimeZones.ResolveTrackedAppId(actual, setting));
        Assert.Equal(new[] { "TZ=UTC" }, ProfileTimeZones.Unapply(actual, setting).Runner().Strings("env"));
        actual["id"] = "regenerated-id";
        var before = JsonSerializer.Serialize(setting);
        var error = Assert.Throws<InvalidDataException>(() => ProfileTimeZones.Unapply(actual, setting));
        Assert.Contains("could not be identified uniquely", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(setting));
        Assert.Equal(new[] { "TZ=America/Chicago" }, actual.Runner().Strings("env"));
    }

    [Fact]
    public void NewExternalAppMayAlreadyUseProfileTimeZoneWithoutTracking()
    {
        var setting = new ProfileTimeZoneSetting();
        ProfileTimeZones.Apply(App("TZ=UTC"), "America/Chicago", setting);
        var external = App("USER=external", "TZ=America/Chicago");
        external["id"] = "external"; external["title"] = "Other application";
        Assert.Null(ProfileTimeZones.ResolveTrackedAppId(external, setting));
        Assert.True(JsonNode.DeepEquals(external, ProfileTimeZones.Unapply(external, setting)));
    }

    [Fact]
    public void LegacySnapshotCanRestoreExactIdButFailsClosedAfterIdChanges()
    {
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(App("TZ=UTC"), "America/Chicago", setting);
        var legacy = setting.Apps["profile-app"];
        legacy.Title = null; legacy.RunnerName = null; legacy.RunnerType = null;
        Assert.Equal(new[] { "TZ=UTC" }, ProfileTimeZones.Unapply(actual, setting).Runner().Strings("env"));
        actual["id"] = "regenerated-id";
        Assert.Throws<InvalidDataException>(() => ProfileTimeZones.Unapply(actual, setting));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("custom")]
    public void RejectsUnsupportedLauncher(string type)
    {
        var original = App(); original.Runner()["type"] = type;
        var setting = new ProfileTimeZoneSetting();
        Assert.Throws<InvalidDataException>(() => ProfileTimeZones.Apply(original, "UTC", setting));
        Assert.Empty(setting.Apps);
    }

    [Fact]
    public void UndoLeavesNonDockerLaunchersUntouchedEvenWithPriorTracking()
    {
        var setting = new ProfileTimeZoneSetting();
        var actual = ProfileTimeZones.Apply(App("TZ=UTC"), "America/Chicago", setting);
        actual.Runner()["type"] = "process";
        actual.Runner()["env"] = new JsonObject { ["fork_field"] = true };
        Assert.True(JsonNode.DeepEquals(actual, ProfileTimeZones.Unapply(actual, setting)));
    }

    [Theory]
    [InlineData("runner", "{}")]
    [InlineData("runner", "[42]")]
    [InlineData("runner", "[null]")]
    [InlineData("base", "{\"Env\":{}}")]
    [InlineData("base", "{\"Env\":[42]}")]
    [InlineData("base", "[]")]
    [InlineData("base", "not json")]
    public void InvalidEnvironmentFailsWithoutChangingInputOrMetadata(string location, string raw)
    {
        var original = App("USER=keep");
        if (location == "runner") original.Runner()["env"] = JsonNode.Parse(raw);
        else original.Runner()["base_create_json"] = raw;
        var before = original.ToJsonString();
        var setting = new ProfileTimeZoneSetting();
        Assert.Throws<InvalidDataException>(() => ProfileTimeZones.Apply(original, "UTC", setting));
        Assert.Empty(setting.Apps);
        Assert.Equal("", setting.TimeZone);
        Assert.Equal(before, original.ToJsonString());
    }
}
