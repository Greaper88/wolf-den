using System.Text.Json.Nodes;

namespace WolfLeash.Configuration;

public sealed class ProfileAppChoice
{
    public required JsonObject App { get; init; }
    public CatalogApp? CatalogApp { get; init; }
    public bool Enabled { get; set; }
    public bool PendingDefaults { get; init; }
    public string MountsText { get; set; } = "";
    public bool OverrideIcon { get; set; }
    public string Icon { get; set; } = "";
    public string EffectiveIcon => OverrideIcon || CatalogApp is null ? Icon : CatalogApp.Definition.Text("icon_png_path");
    public bool IsDocker => App.Runner().Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase);
    public List<string> Mounts => MountsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    public List<string> Defaults => CatalogApp?.Definition.Runner().Strings("mounts") ?? [];
    public void ResetMounts() => MountsText = string.Join('\n', Defaults);
}

public sealed class ProfileEditor
{
    public JsonObject Original { get; }
    public CatalogState Catalog { get; }
    public List<ProfileAppChoice> Apps { get; } = [];
    public string Name { get; set; }
    public string Icon { get; set; }
    public bool HasPin { get; set; }
    public string Pin { get; set; }
    public string TimeZone { get; set; }
    public ProfileTimeZoneSetting? TimeZoneSetting { get; private set; }

    public ProfileEditor(JsonObject profile, CatalogState catalog)
    {
        Original = profile.Copy(); Catalog = catalog;
        Name = profile.Text("name"); Icon = profile.Text("icon_png_path");
        var timezone = catalog.ProfileTimeZones.GetValueOrDefault(profile.Text("id"));
        TimeZone = timezone?.TimeZone ?? "";
        HasPin = profile["pin"] is JsonArray;
        Pin = string.Concat((profile["pin"] as JsonArray ?? []).Select(n => n!.GetValue<int>().ToString()));
        foreach (var actual in profile.Objects("apps"))
        {
            var underlying = ProfileTimeZones.Unapply(actual, timezone);
            var trackedId = ProfileTimeZones.ResolveTrackedAppId(actual, timezone);
            var profileLinks = catalog.Links.Where(l => l.ProfileId == profile.Text("id")).ToList();
            var link = profileLinks.SingleOrDefault(l => l.AppId == actual.Text("id"))
                ?? profileLinks.SingleOrDefault(l => l.AppId == trackedId);
            var entry = catalog.Apps.SingleOrDefault(a => a.Id == link?.CatalogId);
            var app = entry is not null && link is not null
                ? MountDefaults.RebaseApp(link.Baseline, entry.Definition, underlying) : underlying;
            var iconOverride = link is null ? null : AppCatalog.GetIconOverride(link, actual);
            if (iconOverride is not null) app["icon_png_path"] = iconOverride;
            Apps.Add(new ProfileAppChoice
            {
                App = app, CatalogApp = entry, Enabled = true,
                PendingDefaults = !JsonNode.DeepEquals(app, underlying),
                OverrideIcon = iconOverride is not null, Icon = app.Text("icon_png_path"),
                MountsText = string.Join('\n', app.Runner().Strings("mounts"))
            });
        }
        foreach (var entry in catalog.Apps.Where(a => Apps.All(c => c.CatalogApp?.Id != a.Id)))
        {
            var app = entry.Definition.Copy(); app["id"] = Guid.NewGuid().ToString("N");
            Apps.Add(new ProfileAppChoice { App = app, CatalogApp = entry, Icon = app.Text("icon_png_path"), MountsText = string.Join('\n', app.Runner().Strings("mounts")) });
        }
    }

    public (JsonObject Profile, List<ProfileAppLink> Links) Build()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A profile name is required.");
        if (HasPin && (Pin.Length == 0 || Pin.Any(c => c is < '0' or > '9')))
            throw new InvalidDataException("The PIN must contain digits only.");
        var timezone = ProfileTimeZones.Normalize(TimeZone);
        var timezoneSetting = timezone is null ? null : new ProfileTimeZoneSetting { TimeZone = timezone };
        var updated = Original.Copy();
        updated["name"] = Name; updated["icon_png_path"] = Icon;
        if (HasPin) updated["pin"] = new JsonArray(Pin.Select(c => (JsonNode?)JsonValue.Create(c - '0')).ToArray());
        else updated.Remove("pin");
        var apps = new List<JsonObject>(); var links = new List<ProfileAppLink>();
        foreach (var choice in Apps.Where(a => a.Enabled))
        {
            var app = choice.App.Copy();
            if (choice.CatalogApp is not null || choice.EffectiveIcon != app.Text("icon_png_path"))
                app["icon_png_path"] = choice.EffectiveIcon;
            if (choice.IsDocker)
            {
                MountDefaults.Index(choice.Mounts);
                app.Runner()["mounts"] = WolfJson.Array(choice.Mounts);
                if (timezoneSetting is not null) app = ProfileTimeZones.Apply(app, timezone!, timezoneSetting);
            }
            apps.Add(app);
            if (choice.CatalogApp is { } entry)
                links.Add(new ProfileAppLink { ProfileId = Original.Text("id"), AppId = app.Text("id"), CatalogId = entry.Id, Baseline = entry.Definition.Copy(), IconOverride = choice.OverrideIcon ? choice.Icon : null });
        }
        updated["apps"] = WolfJson.Array(apps);
        TimeZoneSetting = timezoneSetting;
        return (updated, links);
    }
}
