using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WolfLeash.Database;
using WolfLeash.Components.Classes;

namespace WolfLeash.Configuration;

public sealed class CatalogApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public JsonObject Definition { get; set; } = new();
    public string? Source { get; set; }
    public string Title => Definition.Text("title");
}

public sealed class ProfileAppLink
{
    public string ProfileId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public JsonObject Baseline { get; set; } = new();
    // null inherits the default; empty string explicitly selects the placeholder.
    public string? IconOverride { get; set; }
}

public sealed class CatalogState
{
    public int Version { get; set; } = 1;
    public bool LegacyImported { get; set; }
    public bool GowSeedComplete { get; set; }
    public List<string> RemovedSources { get; set; } = [];
    public List<CatalogApp> Apps { get; set; } = [];
    public List<ProfileAppLink> Links { get; set; } = [];
}

public sealed class AppCatalog(DenStorage storage, IServiceScopeFactory scopes, WolfConfigurationClient wolf,
    DefaultAppLoader? gow = null, IConfiguration? configuration = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string FileName = "app-catalog.json";
    private bool _seedAttempted;
    public string? SourceStatus { get; private set; }

    private async Task<CatalogState> ReadAsync()
    {
        var path = Path.Combine(storage.DirectoryPath, FileName);
        CatalogState state;
        if (File.Exists(path))
        {
            await using var file = File.OpenRead(path);
            state = await JsonSerializer.DeserializeAsync<CatalogState>(file)
                ?? throw new InvalidDataException("The app catalog is empty or invalid.");
            if (state.Version != 1) throw new InvalidDataException($"Unsupported app catalog version {state.Version}.");
        }
        else state = new();

        if (!state.LegacyImported)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WolfLeashDbContext>();
            foreach (var app in await db.Apps.Include(a => a.Runner).AsNoTracking().ToListAsync())
                if (app.Runner is not null) state.Apps.Add(new CatalogApp { Definition = WolfJson.FromLegacy(app) });
            state.LegacyImported = true;
            state.GowSeedComplete = state.Apps.Count > 0;
            await storage.WriteAsync(FileName, state);
        }
        return state;
    }

    public async Task<CatalogState> GetAsync()
    {
        await SeedAsync();
        await _gate.WaitAsync();
        try { return await ReadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SeedAsync()
    {
        if (gow is null || configuration?["GowCatalog:AutoPopulate"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true) return;
        await _gate.WaitAsync();
        try
        {
            if (_seedAttempted) return;
            _seedAttempted = true;
            var state = await ReadAsync();
            if (!state.GowSeedComplete) await AddGowAsync(state);
        }
        catch (Exception e) { SourceStatus = $"GOW auto-population could not finish: {e.Message} Use Add new apps from GOW to retry."; }
        finally { _gate.Release(); }
    }

    public async Task<int> RefreshGowAsync()
    {
        await _gate.WaitAsync();
        try { return await AddGowAsync(await ReadAsync()); }
        finally { _gate.Release(); }
    }

    private async Task<int> AddGowAsync(CatalogState state)
    {
        if (gow is null) return 0;
        var result = await gow.DiscoverAsync();
        var added = 0;
        foreach (var template in result.Apps)
        {
            if (state.Apps.Any(a => a.Source == template.Source) || state.RemovedSources.Contains(template.Source)) continue;
            var existing = FindEntry(state, template.App);
            if (existing is not null) existing.Source = template.Source;
            else { state.Apps.Add(new CatalogApp { Source = template.Source, Definition = template.App }); added++; }
        }
        state.GowSeedComplete = result.Errors.Count == 0;
        await storage.WriteAsync(FileName, state);
        SourceStatus = result.Errors.Count == 0 ? null : $"Some GOW templates could not load: {string.Join("; ", result.Errors)}. Use Add new apps from GOW to retry.";
        return added;
    }

    public async Task SaveAsync(CatalogApp app, JsonObject? expected = null)
    {
        ValidateApp(app.Definition);
        await _gate.WaitAsync();
        try
        {
            var state = await ReadAsync();
            var existing = state.Apps.SingleOrDefault(a => a.Id == app.Id);
            if (existing is not null && !JsonNode.DeepEquals(existing.Definition, expected))
                throw new InvalidOperationException("This app's defaults changed in another browser. Reload before saving.");
            if (existing is null && expected is not null) throw new InvalidOperationException("This catalog app was deleted. Reload before saving.");
            if (existing is null) state.Apps.Add(new CatalogApp { Id = app.Id, Definition = app.Definition.Copy() });
            else existing.Definition = app.Definition.Copy();
            await storage.WriteAsync(FileName, state);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await ReadAsync();
            if (state.Apps.SingleOrDefault(a => a.Id == id)?.Source is { } source) state.RemovedSources.Add(source);
            state.Apps.RemoveAll(a => a.Id == id);
            state.Links.RemoveAll(a => a.CatalogId == id);
            await storage.WriteAsync(FileName, state);
        }
        finally { _gate.Release(); }
    }

    // Link the same named app/image to shared defaults. Existing launch differences
    // remain in the profile and are preserved by the three-way default merge.
    public async Task ImportProfilesAsync(IReadOnlyList<JsonObject> profiles)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await ReadAsync();
            foreach (var profile in profiles)
            foreach (var app in profile.Objects("apps"))
            {
                if (state.Links.Any(l => l.ProfileId == profile.Text("id") && l.AppId == app.Text("id"))) continue;
                var entry = FindEntry(state, app);
                if (entry is null)
                {
                    entry = new CatalogApp { Definition = app.Copy() };
                    if (string.IsNullOrWhiteSpace(entry.Definition.Text("icon_png_path")) && gow?.GetDefaultIcon(app) is { } defaultIcon)
                        entry.Definition["icon_png_path"] = defaultIcon;
                    state.Apps.Add(entry);
                }
                var baseline = entry.Definition.Copy();
                // A previously unspecified icon starts inheriting the catalog default.
                if (string.IsNullOrWhiteSpace(app.Text("icon_png_path"))) baseline["icon_png_path"] = "";
                state.Links.Add(new ProfileAppLink
                {
                    ProfileId = profile.Text("id"), AppId = app.Text("id"), CatalogId = entry.Id,
                    Baseline = baseline,
                    IconOverride = app.Text("icon_png_path") == baseline.Text("icon_png_path") ? null : app.Text("icon_png_path")
                });
            }
            await storage.WriteAsync(FileName, state);
        }
        finally { _gate.Release(); }
    }

    private static CatalogApp? FindEntry(CatalogState state, JsonObject app)
    {
        var exact = state.Apps.FirstOrDefault(a => SameTemplate(a.Definition, app));
        if (exact is not null) return exact;
        var candidates = state.Apps.Where(a => SameApplication(a.Definition, app)).Take(2).ToList();
        // Several intentionally saved variants require an exact match.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool SameApplication(JsonObject first, JsonObject second)
    {
        static string Name(JsonObject app) => string.Concat(app.Text("title").Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        if (Name(first) != Name(second)) return false;
        var a = first.Runner(); var b = second.Runner();
        if (!a.Text("type").Equals(b.Text("type"), StringComparison.OrdinalIgnoreCase)) return false;
        return a.Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase)
            ? a.Text("image") == b.Text("image")
            : a.Text("run_cmd") == b.Text("run_cmd");
    }

    public static bool SameTemplate(JsonObject first, JsonObject second)
    {
        var a = first.Copy(); var b = second.Copy();
        foreach (var key in new[] { "id", "icon_png_path", "render_node", "gpu_auto_select", "support_hdr", "h264_gst_pipeline", "hevc_gst_pipeline", "av1_gst_pipeline", "opus_gst_pipeline" })
        { a.Remove(key); b.Remove(key); }
        a.Runner().Remove("mounts"); b.Runner().Remove("mounts");
        foreach (var app in new[] { a, b })
            if (app.Runner()["base_create_json"] is JsonValue value && value.TryGetValue<string>(out var json))
            {
                try { app.Runner()["base_create_json"] = JsonNode.Parse(json); }
                catch (JsonException) { /* Preserve invalid existing data for inspection. */ }
            }
        return JsonNode.DeepEquals(a, b);
    }

    public async Task<int> ApplyDefaultsAsync(string? catalogId = null)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await ReadAsync();
            var profiles = await wolf.GetProfilesAsync();
            var count = 0;
            foreach (var profile in profiles)
            {
                var updated = profile.Copy();
                var links = state.Links.Where(l => l.ProfileId == profile.Text("id") &&
                    (catalogId is null || l.CatalogId == catalogId)).ToList();
                foreach (var link in links)
                {
                    var entry = state.Apps.SingleOrDefault(a => a.Id == link.CatalogId);
                    var apps = (JsonArray?)updated["apps"];
                    var actual = updated.Objects("apps").SingleOrDefault(a => a.Text("id") == link.AppId);
                    if (entry is null || actual is null || apps is null) continue;
                    var rebased = MountDefaults.RebaseApp(link.Baseline, entry.Definition, actual);
                    link.IconOverride = GetIconOverride(link, actual);
                    if (link.IconOverride is not null) rebased["icon_png_path"] = link.IconOverride;
                    apps[apps.IndexOf(actual)] = rebased;
                }
                if (!JsonNode.DeepEquals(profile, updated))
                {
                    await wolf.ReplaceProfileAsync(profile, updated);
                    count++;
                }
                foreach (var link in links)
                    if (state.Apps.SingleOrDefault(a => a.Id == link.CatalogId) is { } entry)
                        link.Baseline = entry.Definition.Copy();
                // Record each successful profile independently; retry can resume after a connection failure.
                await storage.WriteAsync(FileName, state);
            }
            return count;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveProfileAsync(JsonObject expected, JsonObject updated, List<ProfileAppLink> links, CatalogState displayed)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await ReadAsync();
            foreach (var link in links)
            {
                var entry = state.Apps.SingleOrDefault(a => a.Id == link.CatalogId);
                var shown = displayed.Apps.SingleOrDefault(a => a.Id == link.CatalogId);
                if (entry is null || shown is null || !JsonNode.DeepEquals(entry.Definition, shown.Definition))
                    throw new InvalidOperationException("App defaults changed while this profile was open. Reload before saving.");
            }
            await wolf.ReplaceProfileAsync(expected, updated);
            state.Links.RemoveAll(l => l.ProfileId == expected.Text("id"));
            state.Links.AddRange(links);
            await storage.WriteAsync(FileName, state);
        }
        finally { _gate.Release(); }
    }

    public static void ValidateApp(JsonObject app)
    {
        if (string.IsNullOrWhiteSpace(app.Text("title"))) throw new InvalidDataException("An app title is required.");
        var runner = app.Runner();
        if (runner.Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(runner.Text("image")) || string.IsNullOrWhiteSpace(runner.Text("name")))
                throw new InvalidDataException("Docker image and runner name are required.");
            MountDefaults.Index(runner.Strings("mounts"));
            if (JsonNode.Parse(runner.Text("base_create_json", "{}")) is not JsonObject)
                throw new InvalidDataException("Base create JSON must be an object.");
        }
        else if (runner.Text("type").Equals("process", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(runner.Text("run_cmd"))) throw new InvalidDataException("A process command is required.");
        }
        else throw new InvalidDataException($"Unsupported runner type '{runner.Text("type")}'.");
    }

    public static string? GetIconOverride(ProfileAppLink link, JsonObject actual)
    {
        var icon = actual.Text("icon_png_path");
        if (link.IconOverride is not null && icon == link.IconOverride) return link.IconOverride;
        return icon == link.Baseline.Text("icon_png_path") ? null : icon;
    }
}
