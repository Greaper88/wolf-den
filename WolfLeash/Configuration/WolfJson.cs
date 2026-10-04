using System.Text.Json.Nodes;

namespace WolfLeash.Configuration;

// Keep the complete wire representation: forks may add fields unknown to Den.
public static class WolfJson
{
    public static JsonObject Copy(this JsonObject value) => (JsonObject)value.DeepClone();
    public static string Text(this JsonObject value, string key, string fallback = "") =>
        value[key]?.GetValue<string>() ?? fallback;
    public static List<JsonObject> Objects(this JsonObject value, string key) =>
        (value[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    public static List<string> Strings(this JsonObject value, string key) =>
        (value[key] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
    public static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(v => (JsonNode?)v.DeepClone()).ToArray());
    public static JsonObject Runner(this JsonObject app) => app["runner"] as JsonObject
        ?? throw new InvalidDataException($"App '{app.Text("title")}' has no runner configuration.");

    public static JsonObject FromLegacy(Database.Model.DbApp app) => new()
    {
        ["id"] = app.AppId, ["title"] = app.Title, ["icon_png_path"] = app.IconPngPath ?? "",
        ["start_virtual_compositor"] = app.StartVirtualCompositor ?? true,
        ["start_audio_server"] = app.StartAudioServer ?? true, ["support_hdr"] = app.SupportHdr ?? false,
        ["render_node"] = app.RenderNode ?? "", ["h264_gst_pipeline"] = app.H264_gst_pipeline ?? "",
        ["hevc_gst_pipeline"] = app.Hevc_gst_pipeline ?? "", ["av1_gst_pipeline"] = app.Av1_gst_pipeline ?? "",
        ["opus_gst_pipeline"] = app.Opus_gst_pipeline ?? "",
        ["runner"] = new JsonObject
        {
            ["type"] = app.Runner.Type, ["name"] = app.Runner.Name, ["image"] = app.Runner.Image,
            ["mounts"] = Array(app.Runner.Mounts), ["env"] = Array(app.Runner.Env),
            ["devices"] = Array(app.Runner.Devices), ["ports"] = Array(app.Runner.Ports),
            ["base_create_json"] = app.Runner.BaseCreateJson
        }
    };
}
