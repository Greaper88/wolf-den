using System.Text.Json;
using System.Text.Json.Nodes;

namespace WolfLeash.Configuration;

public sealed class ProfileTimeZoneSetting
{
    public string TimeZone { get; set; } = "";
    public Dictionary<string, AppTimeZoneState> Apps { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AppTimeZoneState
{
    // Wolf regenerates IDs from title/icon after a config reload. Container
    // identity excludes the icon and image, which can change independently.
    public string? Title { get; set; }
    public string? RunnerType { get; set; }
    public string? RunnerName { get; set; }
    public bool RunnerEnvironmentPresent { get; set; }
    public bool RunnerEnvironmentWasNull { get; set; }
    public List<TimeZoneEnvironmentEntry> RunnerEntries { get; set; } = [];
    public string? BaseCreateJsonBefore { get; set; }
    public string? BaseCreateJsonAfter { get; set; }
    public List<TimeZoneEnvironmentEntry> BaseEntries { get; set; } = [];
}

public sealed class TimeZoneEnvironmentEntry
{
    public int Index { get; set; }
    public string Value { get; set; } = "";
}

// Wolf stores launcher environments as arrays. Keep the profile policy separate
// from the underlying app so clearing it and rebasing defaults remain reversible.
public static class ProfileTimeZones
{
    private static readonly Lazy<IReadOnlyList<string>> Zones = new(() =>
        TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Append("UTC").Append("Etc/UTC")
            .Distinct(StringComparer.Ordinal).OrderBy(z => z, StringComparer.Ordinal).ToArray());
    private static readonly Lazy<HashSet<string>> ZoneIds = new(() => AvailableIds.ToHashSet(StringComparer.Ordinal));
    public static IReadOnlyList<string> AvailableIds => Zones.Value;

    public static string? Normalize(string? value)
    {
        var zone = value?.Trim();
        if (string.IsNullOrEmpty(zone)) return null;
        if (!ZoneIds.Value.Contains(zone))
            throw new InvalidDataException("Select a valid IANA timezone, such as America/Chicago, or use the app/system default.");
        return zone;
    }

    public static JsonObject Apply(JsonObject app, string zone, ProfileTimeZoneSetting next)
    {
        zone = Normalize(zone) ?? throw new InvalidDataException("A timezone is required for the profile override.");
        var result = app.Copy();
        var runner = result.Runner();
        var type = runner.Text("type");
        if (!type.Equals("docker", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"App '{app.Text("title")}' uses a launcher that does not support timezone overrides.");
        var id = result.Text("id");
        if (string.IsNullOrEmpty(id)) throw new InvalidDataException("An app ID is required for the profile timezone override.");
        var values = ReadEnvironment(runner["env"], "launcher env");
        var state = new AppTimeZoneState
        {
            Title = result.Text("title"), RunnerType = type, RunnerName = runner.Text("name"),
            RunnerEnvironmentPresent = runner.ContainsKey("env"),
            RunnerEnvironmentWasNull = runner.ContainsKey("env") && runner["env"] is null,
            RunnerEntries = Entries(values)
        };
        runner["env"] = WolfJson.Array(values.Where(v => !IsTimeZone(v)).Append("TZ=" + zone));

        if (type.Equals("docker", StringComparison.OrdinalIgnoreCase) && runner["base_create_json"] is not null)
        {
            if (runner["base_create_json"] is not JsonValue value || !value.TryGetValue<string>(out var raw))
                throw new InvalidDataException("Base create JSON must be a JSON object encoded as text.");
            var options = ParseOptions(raw);
            var baseValues = ReadEnvironment(options["Env"], "base create JSON Env");
            state.BaseEntries = Entries(baseValues);
            if (state.BaseEntries.Count > 0)
            {
                state.BaseCreateJsonBefore = raw;
                options["Env"] = WolfJson.Array(baseValues.Where(v => !IsTimeZone(v)));
                state.BaseCreateJsonAfter = options.ToJsonString();
                runner["base_create_json"] = state.BaseCreateJsonAfter;
            }
        }

        next.TimeZone = zone;
        next.Apps[id] = state;
        return result;
    }

    public static JsonObject Unapply(JsonObject app, ProfileTimeZoneSetting? prior)
    {
        var result = app.Copy();
        if (prior is null) return result;
        var runner = result.Runner();
        if (!runner.Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase)) return result;
        var trackedId = ResolveTrackedAppId(result, prior);
        if (trackedId is null) return result;
        var state = prior.Apps[trackedId];
        var values = ReadEnvironment(runner["env"], "launcher env");
        var actual = values.Where(IsTimeZone).ToList();
        // A different, missing, or duplicated TZ is an external edit. It must
        // survive clearing this policy, as must all other environment entries.
        if (actual.Count != 1 || actual[0] != "TZ=" + prior.TimeZone) return result;

        var restored = Restore(values.Where(v => !IsTimeZone(v)).ToList(), state.RunnerEntries);
        if (restored.Count == 0 && !state.RunnerEnvironmentPresent) runner.Remove("env");
        else if (restored.Count == 0 && state.RunnerEnvironmentWasNull) runner["env"] = null;
        else runner["env"] = WolfJson.Array(restored);

        if (state.BaseCreateJsonBefore is null || runner["base_create_json"] is not JsonValue value ||
            !value.TryGetValue<string>(out var raw)) return result;
        if (raw == state.BaseCreateJsonAfter)
        {
            runner["base_create_json"] = state.BaseCreateJsonBefore;
            return result;
        }
        try
        {
            var options = ParseOptions(raw);
            // Preserve an externally removed Env or an explicitly changed TZ.
            if (options["Env"] is not JsonArray) return result;
            var baseValues = ReadEnvironment(options["Env"], "base create JSON Env");
            if (baseValues.Any(IsTimeZone)) return result;
            options["Env"] = WolfJson.Array(Restore(baseValues, state.BaseEntries));
            runner["base_create_json"] = options.ToJsonString();
        }
        catch (InvalidDataException) { /* An external JSON edit remains available for inspection. */ }
        return result;
    }

    public static string? ResolveTrackedAppId(JsonObject app, ProfileTimeZoneSetting? prior)
    {
        if (prior is null) return null;
        var id = app.Text("id");
        if (prior.Apps.ContainsKey(id)) return id;
        var runner = app.Runner();
        if (!runner.Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase)) return null;
        var matches = prior.Apps.Where(pair => SameIdentity(pair.Value, app, runner)).Take(2).ToList();
        if (matches.Count == 1) return matches[0].Key;
        // A genuinely new app may already use the same TZ as this profile.
        // Only ambiguity or old metadata without identity prevents safe restore.
        if ((matches.Count > 1 || prior.Apps.Values.Any(state => !HasIdentity(state))) &&
            ReadEnvironment(runner["env"], "launcher env").Contains("TZ=" + prior.TimeZone, StringComparer.Ordinal))
            throw new InvalidDataException($"The previous timezone for app '{app.Text("title")}' could not be identified uniquely after its ID changed. Its timezone restoration data was retained; reload or resolve the app identity before saving.");
        return null;
    }

    private static bool HasIdentity(AppTimeZoneState state) =>
        state.Title is not null && state.RunnerType is not null && state.RunnerName is not null;

    private static bool SameIdentity(AppTimeZoneState state, JsonObject app, JsonObject runner) =>
        HasIdentity(state) && state.Title == app.Text("title") && state.RunnerName == runner.Text("name") &&
        state.RunnerType!.Equals(runner.Text("type"), StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeZone(string value) => value == "TZ" || value.StartsWith("TZ=", StringComparison.Ordinal);

    private static List<string> ReadEnvironment(JsonNode? node, string label)
    {
        if (node is null) return [];
        if (node is not JsonArray array) throw new InvalidDataException($"The {label} must be an array of strings.");
        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new InvalidDataException($"The {label} must be an array of strings.");
            values.Add(text);
        }
        return values;
    }

    private static List<TimeZoneEnvironmentEntry> Entries(List<string> values) => values
        .Select((value, index) => new TimeZoneEnvironmentEntry { Index = index, Value = value })
        .Where(entry => IsTimeZone(entry.Value)).ToList();

    private static List<string> Restore(List<string> values, List<TimeZoneEnvironmentEntry> entries)
    {
        foreach (var entry in entries.OrderBy(e => e.Index))
            values.Insert(Math.Clamp(entry.Index, 0, values.Count), entry.Value);
        return values;
    }

    private static JsonObject ParseOptions(string raw)
    {
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("Base create JSON must be an object.");
        }
        catch (JsonException) { throw new InvalidDataException("Base create JSON must be a valid JSON object."); }
    }
}
