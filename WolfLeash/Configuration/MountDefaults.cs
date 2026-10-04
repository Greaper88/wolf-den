using System.Text.Json.Nodes;

namespace WolfLeash.Configuration;

public record MountDifference(string Target, string? Default, string? Actual);

public static class MountDefaults
{
    public static string Target(string mount)
    {
        var parts = mount.Split(':');
        if (parts.Length is < 2 or > 3 || string.IsNullOrWhiteSpace(parts[0]) ||
            !parts[1].StartsWith('/') || (parts.Length == 3 && string.IsNullOrWhiteSpace(parts[2])))
            throw new InvalidDataException($"Invalid mount '{mount}'. Use source:/container/path[:options].");
        return parts[1].TrimEnd('/') is { Length: > 0 } target ? target : "/";
    }

    public static Dictionary<string, string> Index(IEnumerable<string> mounts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
            if (!result.TryAdd(Target(mount), mount))
                throw new InvalidDataException($"More than one mount uses container path '{Target(mount)}'.");
        return result;
    }

    public static List<MountDifference> Differences(IEnumerable<string> defaults, IEnumerable<string> actual)
    {
        var before = Index(defaults);
        var after = Index(actual);
        return before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => new MountDifference(k, before.GetValueOrDefault(k), after.GetValueOrDefault(k))).ToList();
    }

    // Container destination is the identity, including explicit removals of a default.
    public static List<string> Rebase(IEnumerable<string> previous, IEnumerable<string> next, IEnumerable<string> actual)
    {
        var newDefaults = Index(next);
        var current = Index(actual);
        var merged = new Dictionary<string, string>(newDefaults);
        foreach (var difference in Differences(previous, current.Values))
        {
            if (difference.Actual is null) merged.Remove(difference.Target);
            else merged[difference.Target] = difference.Actual;
        }
        return newDefaults.Keys.Concat(current.Keys).Distinct().Where(merged.ContainsKey).Select(k => merged[k]).ToList();
    }

    public static JsonObject RebaseApp(JsonObject previous, JsonObject next, JsonObject actual)
    {
        var result = Merge(previous, next, actual);
        result["id"] = actual["id"]?.DeepClone();
        if (actual.Runner().Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase) &&
            next.Runner().Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase))
            result.Runner()["mounts"] = WolfJson.Array(Rebase(previous.Runner().Strings("mounts"),
                next.Runner().Strings("mounts"), actual.Runner().Strings("mounts")));
        return result;
    }

    private static JsonObject Merge(JsonObject previous, JsonObject next, JsonObject actual)
    {
        var result = actual.Copy();
        foreach (var key in previous.Select(p => p.Key).Union(next.Select(p => p.Key)))
        {
            if (JsonNode.DeepEquals(previous[key], actual[key]))
            {
                if (next.ContainsKey(key)) result[key] = next[key]?.DeepClone();
                else result.Remove(key);
            }
            else if (previous[key] is JsonObject oldObject && next[key] is JsonObject newObject && actual[key] is JsonObject current)
                result[key] = Merge(oldObject, newObject, current);
        }
        return result;
    }
}
