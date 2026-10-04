using System.Collections;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using WolfLeash.Configuration;

namespace WolfLeash.Components.Classes;

public sealed record GowTemplate(string Source, JsonObject App);
public sealed record GowTemplates(List<GowTemplate> Apps, List<string> Errors);

public sealed class DefaultAppLoader : IDisposable
{
    public static readonly string[] DefaultApps =
        ["es-de", "firefox", "heroic-games-launcher", "kodi", "lutris", "pegasus", "plasma", "prismlauncher", "retroarch", "steam", "xfce"];
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _ref;
    private readonly IConfiguration _configuration;
    public DefaultAppLoader(IConfiguration configuration) : this(configuration, new HttpClient { Timeout = TimeSpan.FromSeconds(15) }) { }
    public DefaultAppLoader(IConfiguration configuration, HttpClient http)
    {
        _http = http;
        _configuration = configuration;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wolf-Den/1.0");
        _repository = configuration["GowCatalog:Repository"] ?? "games-on-whales/gow";
        _ref = configuration["GowCatalog:Ref"] ?? "master";
        if (!System.Text.RegularExpressions.Regex.IsMatch(_repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("GowCatalog:Repository must be owner/repository.");
    }

    public async Task<GowTemplates> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_repository}/git/trees/{Uri.EscapeDataString(_ref)}?recursive=1";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var tree = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new InvalidDataException("GOW returned an invalid repository tree.");
        if (tree["truncated"]?.GetValue<bool>() == true) throw new InvalidDataException("GOW repository listing was truncated.");
        var paths = tree.Objects("tree").Where(n => n.Text("type") == "blob")
            .Select(n => n.Text("path"))
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^apps/[^/]+/assets/wolf\.config\.toml$"))
            .Order().ToList();
        if (paths.Count == 0) throw new InvalidDataException("No GOW app definitions were found in the configured repository.");
        var sources = paths.Select(path => new TemplateSource(_repository, _ref, path)).ToList();
        sources.AddRange(AdditionalTemplates());
        var results = new System.Collections.Concurrent.ConcurrentBag<GowTemplate>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(sources.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (source, token) =>
        {
            try
            {
                var apps = await ReadPathAsync(source, token);
                for (var i = 0; i < apps.Count; i++) results.Add(new GowTemplate($"{source.Repository}@{source.Ref}:{source.Path}#{i}", apps[i]));
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { errors.Add($"{source.Repository}/{source.Path}: {e.Message}"); }
        });
        return new GowTemplates(results.OrderBy(a => a.Source).ToList(), errors.Order().ToList());
    }

    public async Task<List<JsonObject>> GetAppAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!DefaultApps.Contains(name)) throw new ArgumentException("Unknown GOW template.", nameof(name));
        var path = $"apps/{name}/assets/wolf.config.toml";
        var source = AdditionalTemplates().FirstOrDefault(s => s.Path == path) ?? new TemplateSource(_repository, _ref, path);
        return await ReadPathAsync(source, cancellationToken);
    }

    private sealed record TemplateSource(string Repository, string Ref, string Path);

    private IEnumerable<TemplateSource> AdditionalTemplates()
    {
        foreach (var entry in _configuration.GetSection("GowCatalog:AdditionalTemplates").GetChildren())
        {
            var repository = entry["Repository"] ?? "";
            var path = entry["Path"] ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(path, @"^apps/[A-Za-z0-9_-]+/assets/wolf\.config\.toml$"))
                throw new InvalidDataException("Additional GOW templates require a repository and apps/name/assets/wolf.config.toml path.");
            yield return new TemplateSource(repository, entry["Ref"] ?? "master", path);
        }
    }

    public string? GetDefaultIcon(JsonObject app)
    {
        var imageName = app.Runner().Text("image").Split('/').Last().Split(':')[0];
        var key = imageName.Length > 0 ? imageName : app.Text("title").Replace(" ", "").ToLowerInvariant();
        var file = _configuration[$"GowCatalog:Icons:{key}"];
        var baseUrl = _configuration["GowCatalog:IconBaseUrl"];
        return string.IsNullOrEmpty(file) || string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/{file}";
    }

    private async Task<List<JsonObject>> ReadPathAsync(TemplateSource source, CancellationToken cancellationToken)
    {
        var url = $"https://raw.githubusercontent.com/{source.Repository}/{Uri.EscapeDataString(source.Ref)}/{source.Path}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var apps = ParseApps(await response.Content.ReadAsStringAsync(cancellationToken), source.Path);
        if (_configuration["GowCatalog:IconBaseUrl"] is { Length: > 0 } iconBase)
        {
            var name = source.Path.Split('/')[1];
            var icon = _configuration[$"GowCatalog:Icons:{name}"];
            foreach (var app in apps) app["icon_png_path"] = string.IsNullOrEmpty(icon) ? "" : $"{iconBase.TrimEnd('/')}/{icon}";
        }
        return apps;
    }

    public static List<JsonObject> ParseApps(string toml, string source = "App template")
    {
        JsonObject root;
        try
        {
            var table = TomlSerializer.Deserialize<TomlTable>(toml)
                ?? throw new InvalidDataException("The document is empty.");
            root = (JsonObject)ToJson(table)!;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidDataException($"{source}: {e.Message}", e);
        }
        var apps = root.Objects("apps");
        apps.AddRange(root.Objects("profiles").SelectMany(p => p.Objects("apps")));
        if (apps.Count == 0) throw new InvalidDataException($"{source}: expected at least one [[apps]] or [[profiles.apps]] table.");
        foreach (var app in apps)
        {
            app["id"] ??= Guid.NewGuid().ToString("N");
            app["icon_png_path"] ??= "";
            app["start_virtual_compositor"] ??= true;
            app["start_audio_server"] ??= true;
            app["support_hdr"] ??= false;
            app["render_node"] ??= "";
            foreach (var key in new[] { "h264_gst_pipeline", "hevc_gst_pipeline", "av1_gst_pipeline", "opus_gst_pipeline" }) app[key] ??= "";
            var runner = app.Runner();
            if (runner.Text("type").Equals("docker", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in new[] { "mounts", "env", "devices", "ports" }) runner[key] ??= new JsonArray();
                runner["base_create_json"] ??= "{}";
            }
            AppCatalog.ValidateApp(app);
        }
        return apps.Select(a => a.Copy()).ToList();
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        TomlTable table => new JsonObject(table.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToJson(kv.Value)))),
        string text => JsonValue.Create(text),
        IEnumerable array => new JsonArray(array.Cast<object?>().Select(ToJson).ToArray()),
        _ => System.Text.Json.JsonSerializer.SerializeToNode(value)
    };

    public void Dispose() => _http.Dispose();
}
