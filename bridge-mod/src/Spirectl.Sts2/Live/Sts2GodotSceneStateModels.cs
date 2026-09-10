using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spirectl.Sts2.Live;

// Godot-FREE encoding helpers + POCO DTOs for the GodotSceneState producer. Kept in a
// separate file from the engine walk (Sts2GodotSceneStateProducer) so the gsw-correct
// shape/serialization is offline-testable from the bridge test project, where Godot lives
// behind an `extern alias` and any public member exposing a `Godot.*` type would be
// invisible. The DTOs mirror godot-scene-web/packages/core/src/index.ts exactly.
public static class Sts2GodotSceneStateEncoding
{
    public const string RenderMode = "godot-scene-state";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Godot `GetNodePath(idx, forParent:true)` can hand back the engine NodePath form
    // (`.`, `./Host`); gsw wants the bare `.tscn` parent (`.` root sentinel kept, `./`
    // prefix stripped). Empty -> null (the scene root has no parent).
    public static string? NormalizeParent(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw == ".") return ".";
        return raw.StartsWith("./", StringComparison.Ordinal) ? raw[2..] : raw;
    }

    // `{ type, args }` engine-native Variant (the gsw `JSON.from_native` shape).
    public static Dictionary<string, object?> TypeArgs(string type, params object?[] args)
        => new() { ["type"] = type, ["args"] = args };

    // Path-first resource ref. gsw `GodotResourceRefValue` (a runtime producer holds loaded
    // `Resource` objects, so it emits `path`, not a scene-local `id`).
    public static Dictionary<string, object?> ExtResourceRef(string path)
        => new() { ["type"] = "ExtResource", ["path"] = path };

    // Scene-local SUB-resource ref (`{type:"SubResource", id}`) for an INLINE resource with no
    // `res://` path (an embedded `Gradient`/`Curve`/`CanvasItemMaterial`). The matching body
    // rides the doc's `subResources` table under this id; gsw's `asResourceRef` + `resolveResource`
    // (packages/project/src/fetch.ts) look it up by id. Without this, an inline resource would be
    // dropped to null (e.g. a particle `color_ramp` gradient -> white particles).
    public static Dictionary<string, object?> SubResourceRef(string id)
        => new() { ["type"] = "SubResource", ["id"] = id };

    // A resource is EXTERNAL (a fetchable `res://…` file gsw resolves by path) only when it has a
    // non-empty path with NO `::` scene-local sub-resource suffix. A runtime-loaded inline resource
    // is either anonymous (empty path) or carries a `res://scene.tscn::SubId` path — both are inline
    // sub-resources that must ride the doc's `subResources` table, NOT a bare ExtResource ref.
    public static bool IsExternalResourcePath(string? resourcePath)
        => resourcePath is { Length: > 0 } && !resourcePath.Contains("::", StringComparison.Ordinal);

    // The scene-local sub-resource id parsed from a `res://scene.tscn::Gradient_w0doc` path
    // ("Gradient_w0doc"), or null for an anonymous/external path (no `::`). Reusing Godot's own id
    // keeps a property's `::`-form ref and its table entry on the same id.
    public static string? SubResourceId(string? resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        var separator = resourcePath.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? resourcePath[(separator + 2)..] : null;
    }

    public static byte[] Serialize(GodotSceneStateDto dto)
        => JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);

    // Non-scene Resource documents (fonts/materials/styleboxes) the renderer parses for
    // properties. Mirrors godot-scene-web `GodotResource`; gsw sniffs `kind:"resource"`.
    public const string ResourceRenderMode = "godot-resource";

    public static byte[] Serialize(GodotResourceDto dto)
        => JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
}

// POCO DTOs mirroring godot-scene-web/packages/core/src/index.ts. Godot-free so the
// shape/serialization is offline-testable; the SceneState walk in
// Sts2GodotSceneStateProducer fills them. JSON keys are camelCased (record property names)
// except the hand-built `{type,args}`/ExtResource dictionaries, whose literal keys are
// intentional. Optionals are omitted when null (`JsonIgnoreCondition.WhenWritingNull`).
public sealed record GodotSceneStateDto(
    [property: JsonPropertyName("kind")] string Kind,
    IReadOnlyList<GodotSceneStateNodeDto> Nodes,
    IReadOnlyList<GodotSceneStateConnectionDto> Connections,
    IReadOnlyList<object> ExtResources,
    IReadOnlyList<object> SubResources,
    IReadOnlyList<string> EditableInstances,
    string? BasePath,
    IReadOnlyList<GodotParseDiagnosticDto> Diagnostics);

public sealed record GodotSceneStateNodeDto(
    int Index,
    int? SiblingIndex,
    string Name,
    string? Type,
    string? Parent,
    string? Owner,
    object? Instance,
    string? InstancePlaceholder,
    IReadOnlyList<string> Groups,
    IReadOnlyList<GodotOrderedPropertyDto> Properties);

public sealed record GodotOrderedPropertyDto(string Name, object? Value);

// Mirrors godot-scene-web `GodotSubResource` (core/src/index.ts): a scene-/doc-local inline
// resource body keyed by `id`, referenced from a node/resource property via
// `{type:"SubResource", id}`. `properties` are the same `{type,args}`/scalar-encoded values a
// node carries. Emitted for inline (empty-`ResourcePath`) resources so gsw can resolve them
// (e.g. a particle `color_ramp` Gradient's colors/offsets).
public sealed record GodotSubResourceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, object?> Attributes,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, object?> Properties);

public sealed record GodotSceneStateConnectionDto(
    string? Signal,
    string? From,
    string? To,
    string? Method,
    int? Flags,
    IReadOnlyList<object?>? Binds,
    int? Unbinds);

public sealed record GodotParseDiagnosticDto(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

// Mirrors godot-scene-web `GodotResource` (core/src/index.ts). `properties` is a keyed map
// (not the ordered array scene nodes use). `kind:"resource"` is the gsw JSON-sniff marker.
public sealed record GodotResourceDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("type")] string? Type,
    GodotResourceHeaderDto? Header,
    IReadOnlyList<object> ExtResources,
    IReadOnlyList<object> SubResources,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyList<GodotParseDiagnosticDto> Diagnostics);

public sealed record GodotResourceHeaderDto(
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, object?> Attributes);
