namespace Spirectl.Sts2.Live;

// Faithful `PackedScene.GetState()` -> godot-scene-web `GodotSceneState` producer (the
// engine-walking half; the Godot-free encoding helpers + DTOs live in
// Sts2GodotSceneStateModels.cs / Sts2GodotSceneStateEncoding).
//
// This is the spirectl-owned replacement for a downstream host's hand-rolled
// `LiveSceneStateSerializer`: it walks a loaded scene's `SceneState` and emits the exact
// JSON shape godot-scene-web's renderer consumes
// (godot-scene-web/packages/core/src/index.ts `GodotSceneState`), so a browser can fetch
// `res://…/foo.tscn` through the asset seam and get scene structure instead of a PNG
// render. It is deliberately DISTINCT from the curated, semantically-typed
// `RuntimeSceneNodeSnapshot` model that the live runtime-scene RPCs expose for a different
// consumer — that contract is left untouched.
//
// Encoding contract (must match gsw exactly; do NOT lean on `Json.from_native`'s
// type-tagged scalar strings):
//   - plain scalars are RAW (`3`, `1.0`, `true`, `"hi"`) — never `"i:3"`/`"f:1.0"`;
//   - math/engine Variants are engine-native `{ type, args }` (mirrors gsw's
//     `JSON.from_native` shape) — `{type:"Vector2",args:[x,y]}`, `Color`, `Rect2`,
//     `StringName`, `NodePath`, `Transform2D`, …;
//   - resource refs are path-first `{ type:"ExtResource", path:"res://…" }`;
//   - parent paths are BARE (`Host`, `Host/Label`), never Godot's `"./A/B"` NodePath
//     form; the scene root's parent is omitted.
// (Two of these — raw-scalar and bare-parent — are also being raised against gsw so it
//  tolerates the other forms; we emit the correct shape regardless and don't depend on
//  those landing.)
public static class Sts2GodotSceneStateProducer
{
    public const string RenderMode = Sts2GodotSceneStateEncoding.RenderMode;

    // Walk a loaded scene's SceneState and serialize it to gsw `GodotSceneState` JSON bytes.
    // MUST run on the Godot main thread (the caller marshals via Sts2MainThreadDispatcher).
    // `resourcePath` is the scene's own res:// path, used as `basePath`.
    public static byte[] ProduceJson(Godot.PackedScene scene, string resourcePath)
        => Sts2GodotSceneStateEncoding.Serialize(BuildDto(scene, resourcePath));

    public static GodotSceneStateDto BuildDto(Godot.PackedScene scene, string resourcePath)
    {
        var state = scene.GetState();
        var diagnostics = new List<GodotParseDiagnosticDto>();
        var subResources = new SubResourceCollector();
        if (state is null)
        {
            return new GodotSceneStateDto(
                Kind: "scene",
                Nodes: [],
                Connections: [],
                ExtResources: [],
                SubResources: [],
                EditableInstances: [],
                BasePath: string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath,
                Diagnostics:
                [
                    new GodotParseDiagnosticDto("error", "scene-state-unavailable",
                        "PackedScene.GetState() returned null."),
                ]);
        }

        var nodes = new List<GodotSceneStateNodeDto>(state.GetNodeCount());
        for (var i = 0; i < state.GetNodeCount(); i++)
        {
            var ownPath = state.GetNodePath(i).ToString();
            var isRoot = ownPath is "." or "";
            var type = state.GetNodeType(i).ToString();
            var instance = state.GetNodeInstance(i);
            var placeholder = state.IsNodeInstancePlaceholder(i)
                ? state.GetNodeInstancePlaceholder(i)
                : null;

            var properties = new List<GodotOrderedPropertyDto>(state.GetNodePropertyCount(i));
            for (var p = 0; p < state.GetNodePropertyCount(i); p++)
            {
                properties.Add(new GodotOrderedPropertyDto(
                    state.GetNodePropertyName(i, p).ToString(),
                    EncodeVariant(state.GetNodePropertyValue(i, p), diagnostics, subResources)));
            }

            nodes.Add(new GodotSceneStateNodeDto(
                Index: i,
                SiblingIndex: state.GetNodeIndex(i),
                Name: state.GetNodeName(i).ToString(),
                Type: string.IsNullOrEmpty(type) ? null : type,
                Parent: isRoot ? null : Sts2GodotSceneStateEncoding.NormalizeParent(state.GetNodePath(i, true).ToString()),
                Owner: null,
                Instance: instance is { ResourcePath: { Length: > 0 } refPath }
                    ? Sts2GodotSceneStateEncoding.ExtResourceRef(refPath)
                    : null,
                InstancePlaceholder: string.IsNullOrEmpty(placeholder) ? null : placeholder,
                Groups: [.. state.GetNodeGroups(i)],
                Properties: properties));
        }

        var connections = new List<GodotSceneStateConnectionDto>(state.GetConnectionCount());
        for (var c = 0; c < state.GetConnectionCount(); c++)
        {
            var binds = state.GetConnectionBinds(c);
            connections.Add(new GodotSceneStateConnectionDto(
                Signal: state.GetConnectionSignal(c).ToString(),
                From: state.GetConnectionSource(c).ToString(),
                To: state.GetConnectionTarget(c).ToString(),
                Method: state.GetConnectionMethod(c).ToString(),
                Flags: state.GetConnectionFlags(c),
                Binds: binds.Count == 0 ? null : [.. binds.Select(b => EncodeVariant(b, diagnostics, subResources))],
                Unbinds: state.GetConnectionUnbinds(c)));
        }

        return new GodotSceneStateDto(
            Kind: "scene",
            Nodes: nodes,
            Connections: connections,
            // A runtime producer holds loaded `Resource` objects, so disk-backed refs are emitted
            // path-first inline (no ExtResource TABLE). INLINE resources (no res:// path) DO need a
            // table — they're collected here as `subResources` and referenced by id.
            ExtResources: [],
            SubResources: subResources.Items,
            EditableInstances: [],
            BasePath: string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath,
            Diagnostics: diagnostics);
    }

    // Variant -> gsw `GodotVariant`. Scalars raw; engine types `{type,args}`; a Resource with a
    // `res://` path -> path-first ExtResource ref; an INLINE resource (no path — an embedded
    // Gradient/Curve/CanvasItemMaterial) -> `{type:"SubResource", id}` with its body registered in
    // `subResources` (so a particle `color_ramp` gradient survives instead of dropping to null and
    // rendering white); containers recurse. Unhandled constructor-style Variants degrade to
    // `{type, args:[]}` plus a diagnostic (keeps the shape valid without fabricating values).
    internal static object? EncodeVariant(
        Godot.Variant value,
        List<GodotParseDiagnosticDto> diagnostics,
        SubResourceCollector subResources)
    {
        switch (value.VariantType)
        {
            case Godot.Variant.Type.Nil:
                return null;
            case Godot.Variant.Type.Bool:
                return value.AsBool();
            case Godot.Variant.Type.Int:
                return value.AsInt64();
            case Godot.Variant.Type.Float:
                return value.AsDouble();
            case Godot.Variant.Type.String:
                return value.AsString();
            case Godot.Variant.Type.StringName:
                return Sts2GodotSceneStateEncoding.TypeArgs("StringName", value.AsString());
            case Godot.Variant.Type.NodePath:
                return Sts2GodotSceneStateEncoding.TypeArgs("NodePath", value.AsString());
            case Godot.Variant.Type.Vector2:
            {
                var v = value.AsVector2();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector2", (double)v.X, (double)v.Y);
            }
            case Godot.Variant.Type.Vector2I:
            {
                var v = value.AsVector2I();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector2i", v.X, v.Y);
            }
            case Godot.Variant.Type.Vector3:
            {
                var v = value.AsVector3();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector3", (double)v.X, (double)v.Y, (double)v.Z);
            }
            case Godot.Variant.Type.Vector3I:
            {
                var v = value.AsVector3I();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector3i", v.X, v.Y, v.Z);
            }
            case Godot.Variant.Type.Vector4:
            {
                var v = value.AsVector4();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector4", (double)v.X, (double)v.Y, (double)v.Z, (double)v.W);
            }
            case Godot.Variant.Type.Vector4I:
            {
                var v = value.AsVector4I();
                return Sts2GodotSceneStateEncoding.TypeArgs("Vector4i", v.X, v.Y, v.Z, v.W);
            }
            case Godot.Variant.Type.Rect2:
            {
                var r = value.AsRect2();
                return Sts2GodotSceneStateEncoding.TypeArgs("Rect2", (double)r.Position.X, (double)r.Position.Y, (double)r.Size.X, (double)r.Size.Y);
            }
            case Godot.Variant.Type.Rect2I:
            {
                var r = value.AsRect2I();
                return Sts2GodotSceneStateEncoding.TypeArgs("Rect2i", r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
            }
            case Godot.Variant.Type.Color:
            {
                var c = value.AsColor();
                return Sts2GodotSceneStateEncoding.TypeArgs("Color", (double)c.R, (double)c.G, (double)c.B, (double)c.A);
            }
            case Godot.Variant.Type.Transform2D:
            {
                var t = value.AsTransform2D();
                return Sts2GodotSceneStateEncoding.TypeArgs("Transform2D",
                    (double)t.X.X, (double)t.X.Y,
                    (double)t.Y.X, (double)t.Y.Y,
                    (double)t.Origin.X, (double)t.Origin.Y);
            }
            case Godot.Variant.Type.Object:
            {
                var obj = value.AsGodotObject();
                if (obj is Godot.Resource resource)
                {
                    // An EXTERNAL resource (a real `res://…` file) -> path-first ExtResource ref.
                    // A scene-LOCAL resource is inline: at runtime it either has no path (anonymous)
                    // or a `res://scene.tscn::SubId` path (a `::` sub-resource id, NOT a fetchable
                    // file). Both must be serialized into `subResources` so gsw resolves them — an
                    // inline Gradient dropped as a bare `::` ExtResource ref renders particles white.
                    return Sts2GodotSceneStateEncoding.IsExternalResourcePath(resource.ResourcePath)
                        ? Sts2GodotSceneStateEncoding.ExtResourceRef(resource.ResourcePath)
                        : Sts2GodotSceneStateEncoding.SubResourceRef(
                            subResources.Register(resource, diagnostics));
                }
                diagnostics.Add(new GodotParseDiagnosticDto("warning", "object-not-resource",
                    $"A non-Resource Object value was dropped ({obj?.GetType().Name ?? "null"})."));
                return null;
            }
            // Packed arrays gsw's particle/gradient readers consume as `{type,args}` call-values
            // (packedNumberArray / packedColorValues in godot-scene-web visual-2d.ts): floats flat,
            // colors flattened to r,g,b,a quads. Without these a Gradient's colors/offsets hit the
            // `default` empty-args branch and every ramp stop is lost.
            case Godot.Variant.Type.PackedFloat32Array:
                return Sts2GodotSceneStateEncoding.TypeArgs("PackedFloat32Array",
                    value.AsFloat32Array().Select(f => (object?)(double)f).ToArray());
            case Godot.Variant.Type.PackedFloat64Array:
                return Sts2GodotSceneStateEncoding.TypeArgs("PackedFloat64Array",
                    value.AsFloat64Array().Select(f => (object?)f).ToArray());
            case Godot.Variant.Type.PackedInt32Array:
                return Sts2GodotSceneStateEncoding.TypeArgs("PackedInt32Array",
                    value.AsInt32Array().Select(i => (object?)(long)i).ToArray());
            case Godot.Variant.Type.PackedInt64Array:
                return Sts2GodotSceneStateEncoding.TypeArgs("PackedInt64Array",
                    value.AsInt64Array().Select(i => (object?)i).ToArray());
            case Godot.Variant.Type.PackedColorArray:
            {
                var colors = value.AsColorArray();
                var args = new List<object?>(colors.Length * 4);
                foreach (var c in colors)
                {
                    args.Add((double)c.R);
                    args.Add((double)c.G);
                    args.Add((double)c.B);
                    args.Add((double)c.A);
                }

                return Sts2GodotSceneStateEncoding.TypeArgs("PackedColorArray", args.ToArray());
            }
            case Godot.Variant.Type.Array:
                return value.AsGodotArray().Select(item => EncodeVariant(item, diagnostics, subResources)).ToList();
            case Godot.Variant.Type.Dictionary:
            {
                var result = new Dictionary<string, object?>();
                var dict = value.AsGodotDictionary();
                foreach (var key in dict.Keys)
                {
                    result[key.AsString()] = EncodeVariant(dict[key], diagnostics, subResources);
                }

                return result;
            }
            default:
                diagnostics.Add(new GodotParseDiagnosticDto("warning", "unhandled-variant-type",
                    $"Variant type '{value.VariantType}' has no faithful encoder; emitted empty args."));
                return Sts2GodotSceneStateEncoding.TypeArgs(value.VariantType.ToString());
        }
    }

    // Serialize an inline resource's STORAGE properties into a gsw `{type,args}`/scalar map (the
    // same shape a node's properties carry), so gsw's resolver reads them by their Godot names
    // (`colors`/`offsets` for a Gradient, `blend_mode` for a CanvasItemMaterial, `_data` for a
    // Curve). Meta/identity fields gsw never reads are skipped. Nested inline resources recurse
    // through `EncodeVariant` and land in the same collector.
    internal static Dictionary<string, object?> EncodeResourceProperties(
        Godot.Resource resource,
        List<GodotParseDiagnosticDto> diagnostics,
        SubResourceCollector subResources)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var info in resource.GetPropertyList())
        {
            if (!info.TryGetValue("usage", out var usageValue)
                || ((Godot.PropertyUsageFlags)usageValue.AsInt64() & Godot.PropertyUsageFlags.Storage) == 0)
            {
                continue;
            }

            if (!info.TryGetValue("name", out var nameValue) || nameValue.AsString() is not { Length: > 0 } name)
            {
                continue;
            }

            if (name is "resource_local_to_scene" or "resource_name" or "resource_path" or "script")
            {
                continue;
            }

            properties[name] = EncodeVariant(resource.Get(name), diagnostics, subResources);
        }

        return properties;
    }

    // Collects INLINE (scene-/doc-local) resources into a `subResources` table, minting a stable id
    // per distinct instance. Deduped by instance id so a resource shared across properties is
    // emitted once; the id is registered BEFORE recursing so a self/cyclic reference terminates.
    internal sealed class SubResourceCollector
    {
        private readonly Dictionary<ulong, string> _idByInstance = [];

        public List<GodotSubResourceDto> Items { get; } = [];

        public string Register(Godot.Resource resource, List<GodotParseDiagnosticDto> diagnostics)
        {
            var instanceId = resource.GetInstanceId();
            if (_idByInstance.TryGetValue(instanceId, out var existing))
            {
                return existing;
            }

            // Prefer the resource's own scene-local sub-resource id (`…tscn::Gradient_w0doc`) so a
            // property ref that was already resolved to that `::` path matches the same id; fall back
            // to a minted id for a truly anonymous resource.
            var id = Sts2GodotSceneStateEncoding.SubResourceId(resource.ResourcePath)
                ?? $"SubResource_{_idByInstance.Count + 1}";
            _idByInstance[instanceId] = id;
            var properties = EncodeResourceProperties(resource, diagnostics, this);
            Items.Add(new GodotSubResourceDto(id, resource.GetClass(), new Dictionary<string, object?>(), properties));
            return id;
        }
    }
}
