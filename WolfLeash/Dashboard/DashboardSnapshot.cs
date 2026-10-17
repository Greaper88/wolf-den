using System.Text.Json;
using System.Text.Json.Serialization;

namespace WolfLeash.Dashboard;

public sealed record DashboardSnapshot
{
    public bool Success { get; init; }
    public int Version { get; init; }
    public GpuStatus[] Gpus { get; init; } = [];
    public SessionStatus[] Sessions { get; init; } = [];
    public ContainerStatus[] Containers { get; init; } = [];
    public ProfileStatus[] Profiles { get; init; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public IReadOnlyList<ActiveUser> ActiveUsers(IReadOnlyDictionary<string, string> aliases)
    {
        string Key(string? profile, string? session, string fallback) =>
            !string.IsNullOrWhiteSpace(profile) ? "profile:" + profile : "client:" + (session ?? fallback);
        var groups = new Dictionary<string, ActiveUser>();
        ActiveUser Get(string? profile, string? session, string fallback)
        {
            var key = Key(profile, session, fallback);
            if (groups.TryGetValue(key, out var user)) return user;
            var name = !string.IsNullOrWhiteSpace(profile)
                ? Profiles.FirstOrDefault(p => p.Id == profile)?.Name ?? profile
                : session is not null && aliases.TryGetValue(session, out var alias) && !string.IsNullOrWhiteSpace(alias)
                    ? alias : session is not null ? "Client " + session : "Unassigned app";
            return groups[key] = new(key, name!, !string.IsNullOrWhiteSpace(profile));
        }
        foreach (var container in Containers)
            Get(container.ProfileId, container.SessionId, container.Id).Containers.Add(container);
        foreach (var session in Sessions)
        {
            // Paused sessions without a running app are not active users.
            var key = Key(session.ProfileId, session.Id, session.Id);
            if (session.Connected == true || session.Streaming || groups.ContainsKey(key))
                Get(session.ProfileId, session.Id, session.Id).Sessions.Add(session);
        }
        return groups.Values.OrderByDescending(u => u.Connected).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed record GpuStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string RenderNode { get; init; } = "";
    public bool Accessible { get; init; }
    public ulong? VramBytes { get; init; }
    public double? VramPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? EncoderPercent { get; init; }
    public string CapabilityStatus { get; init; } = "unknown";
    public string[] Codecs { get; init; } = [];
}
public sealed record SessionStatus
{
    public string Id { get; init; } = "";
    public string? ProfileId { get; init; }
    public string App { get; init; } = "";
    public bool? Connected { get; init; }
    public bool Streaming { get; init; }
    public string? GpuId { get; init; }
    public string? Codec { get; init; }
    public long? BitrateKbps { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Fps { get; init; }
}
public sealed record ContainerStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public string App { get; init; } = "";
    public string? ProfileId { get; init; }
    public string? SessionId { get; init; }
    public string? GpuId { get; init; }
    public string RenderNode { get; init; } = "";
}
public sealed record ProfileStatus(string Id, string Name);
public sealed record ActiveUser(string Key, string Name, bool IsProfile)
{
    public List<SessionStatus> Sessions { get; } = [];
    public List<ContainerStatus> Containers { get; } = [];
    public bool Connected => Sessions.Any(s => s.Connected == true);
    public bool UsesGpu(string id) => Containers.Any(c => c.GpuId == id) || Sessions.Any(s => (s.Connected == true || s.Streaming) && s.GpuId == id);
}
public sealed record DashboardResult(DashboardSnapshot? Snapshot, DateTimeOffset? UpdatedAt, string? Error);
