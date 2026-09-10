namespace Spirectl.Sts2.Live;

// Faithful loaded-`Resource` -> godot-scene-web `GodotResource` producer (the engine half;
// the Godot-free DTOs + serialization live in Sts2GodotSceneStateModels.cs). Sibling of
// Sts2GodotSceneStateProducer, one level down: where that serves `.tscn` scene structure,
// this serves the resource DOCUMENTS a node references and the renderer parses for
// properties — today specifically FONTS.
//
// Why fonts: a scene node sets `theme_override_fonts/font = ExtResource("res://themes/x.tres")`,
// a `FontVariation` whose `base_font` points at the actual `.ttf`. The renderer fetches that
// `.tres` as a document (resources cache -> parseResourceBody) to read `base_font`, then
// resolves the `.ttf` path to a `fontUrl` (and the .ttf BYTES are already served by the
// extractor's font path). Without this, the loaded `FontVariation` hits the extractor's
// `UnsupportedResourceTypeFailure` and text falls back to a default typeface.
//
// Why canvas-item materials: a node sets `material = ExtResource/SubResource(...)` (often a
// scene-embedded sub-resource the runtime scene producer emits as a `res://x.tscn::Sub`
// path ref). The renderer fetches the material as a document to read its TYPE, the
// `shader` ref (whose path identifies card_ripple/hsv/...) and the LIVE
// `shader_parameter/*` values, picking its render strategy (plus-lighter compositing, the
// SVG ripple approximation, the exact hsv color matrix, or the opt-in WebGL runtime).
// A sampled preview PNG is useless for that — and card_ripple at rest even renders fully
// transparent, failing extraction outright.
//
// Why AtlasTexture: an atlas sprite (`res://….sprites/x.tres`) is served as a document carrying its
// `region`/`margin` (Rect2) + `atlas` ExtResource ref, so the renderer resolves the UNDERLYING atlas image
// and crops the region itself via CSS (stable image URL). This replaces the old server-side per-sprite crop,
// which returned a distinct cropped PNG per frame and made atlas-ANIMATED sprites (intent icons) flicker as
// the browser re-fetched each frame's URL. An explicit raster format still renders a cropped PNG (the
// `sts2 assets extract` CLI), so only the default/auto document path changed.
//
// Deliberately NOT here: plain Texture2D/Theme/StyleBox — those already resolve via the extractor's PNG
// render + the renderer's url fallback, so serving them as documents would regress them. Encoding reuses
// Sts2GodotSceneStateProducer.EncodeVariant (raw scalars; `{type,args}` math; Resource -> path-first
// ExtResource ref), so `base_font`/`atlas` becomes the `{type:"ExtResource", path:"res://…"}` the renderer needs.
public static class Sts2GodotResourceProducer
{
    public const string RenderMode = Sts2GodotSceneStateEncoding.ResourceRenderMode;

    // True for resources the renderer consumes as documents (fonts, canvas-item materials).
    // Used by the extractor to route a loaded resource here instead of failing as
    // unsupported (fonts) or rendering a sampled preview (materials).
    public static bool IsSupported(Godot.Resource resource)
        => resource is Godot.Font or Godot.ShaderMaterial or Godot.CanvasItemMaterial or Godot.AtlasTexture;

    public static byte[] ProduceJson(Godot.Resource resource)
        => Sts2GodotSceneStateEncoding.Serialize(BuildDto(resource));

    public static GodotResourceDto BuildDto(Godot.Resource resource)
    {
        var diagnostics = new List<GodotParseDiagnosticDto>();
        var subResources = new Sts2GodotSceneStateProducer.SubResourceCollector();
        var type = resource.GetClass();
        var properties = new Dictionary<string, object?>();
        foreach (var name in PropertyAllowlist(resource))
        {
            var value = resource.Get(name);
            if (value.VariantType == Godot.Variant.Type.Nil)
            {
                continue;
            }

            properties[name] = Sts2GodotSceneStateProducer.EncodeVariant(value, diagnostics, subResources);
        }

        // The underlying atlas PAGE size (not a Godot property — derived from the loaded page
        // texture). The web client needs it to scale a cropped region into a layout box that
        // differs from the sprite's native size (CSS background-position/size offsets); without
        // it the renderer falls back to native-pixel scale. Emitted as the same `{type:"Vector2",
        // args:[w,h]}` shape as `region`/`margin`'s Rect2, so `asVector2` parses it client-side.
        if (resource is Godot.AtlasTexture { Atlas: { } atlasPage })
        {
            properties["atlas_size"] = Sts2GodotSceneStateProducer.EncodeVariant(
                Godot.Variant.From(atlasPage.GetSize()),
                diagnostics,
                subResources);
        }

        return new GodotResourceDto(
            Kind: "resource",
            Type: type,
            Header: new GodotResourceHeaderDto(
                "gd_resource",
                new Dictionary<string, object?> { ["type"] = type }),
            ExtResources: [],
            SubResources: subResources.Items,
            Properties: properties,
            Diagnostics: diagnostics);
    }

    // Curated per-class allowlist of the properties the renderer reads — NEVER a blind
    // GetPropertyList walk, which would dump FontFile.Data (the whole font byte array) and
    // every internal cache field into the JSON. Properties absent on the instance are skipped
    // (Get -> Nil) in BuildDto.
    private static IReadOnlyList<string> PropertyAllowlist(Godot.Resource resource) => resource switch
    {
        // gsw FontVariation branch reads base_font, variation_opentype (weight),
        // spacing_glyph, multichannel_signed_distance_field.
        Godot.FontVariation =>
        [
            "base_font",
            "variation_opentype",
            "spacing_glyph",
            "multichannel_signed_distance_field",
        ],
        // A leaf FontFile is rarely fetched as a document (a FontVariation's base_font ref is
        // the `.ttf` path, which resolves directly); emit only the MSDF flag for completeness.
        Godot.FontFile => ["multichannel_signed_distance_field"],
        // gsw imageResource (AtlasTexture branch) reads `atlas` (ExtResource -> underlying image),
        // `region` and `margin` (Rect2) to crop the sub-rect from the stable atlas via CSS.
        Godot.AtlasTexture => ["atlas", "region", "margin", "filter_clip"],
        // gsw assignMaterialAttributes reads `shader` (ext path -> card_ripple/hsv identity)
        // and `shader_parameter/*` (LIVE uniform values — e.g. NCardHighlight raises
        // card_ripple's `width` at runtime, which the static .tscn never carries).
        Godot.ShaderMaterial shaderMaterial => ["shader", .. ShaderParameterNames(shaderMaterial)],
        // gsw maps CanvasItemMaterial BLEND_MODE_ADD to plus-lighter compositing.
        Godot.CanvasItemMaterial => ["blend_mode", "light_mode"],
        _ => [],
    };

    // The material's declared uniforms as `shader_parameter/...` property names — the
    // narrow, shader-declared slice of GetPropertyList (NOT internal cache fields). Values
    // resolve through the same curated Get() in BuildDto; texture uniforms become path
    // refs via EncodeVariant.
    private static IReadOnlyList<string> ShaderParameterNames(Godot.ShaderMaterial material)
    {
        const string prefix = "shader_parameter/";
        var names = new List<string>();
        foreach (var property in material.GetPropertyList())
        {
            if (property.TryGetValue("name", out var name)
                && name.AsString() is { } propertyName
                && propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(propertyName);
            }
        }

        return names;
    }
}
