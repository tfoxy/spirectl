using System.Globalization;
using System.Text;

namespace Spirectl.Sts2.Live;

// Godot-free core of the SPINE MATERIAL DISCRIMINATOR (round-8 item 8). Always compiled (no Godot dependency)
// so the signature is offline-unit-testable, like Sts2SpineSchedule / Sts2SpineDefaults. The Godot-typed reader
// (Sts2SpineInspector.ReadMaterialKey) collects the shader path + uniform values and delegates here.
//
// WHY: a SpineSprite can carry a `normal_material` ShaderMaterial the game re-parameterises at RUNTIME — the
// boss map point applies `res://shaders/boss_map_point.gdshader` (an R/G/B channel-remap MASK: red→map_color,
// green→white, blue→black_layer_color) and `NBossMapPoint.RefreshColorInstantly` pushes the current act's
// MapBgColor / MapTraveled|UntraveledColor into it. The bake now applies that material (else the raw colored
// mask renders instead of the game's near-grayscale boss), so the RENDERED PIXELS depend on values that are NOT
// part of the (scene, node, anim, skin, skel) clip address. Without a discriminator the first-baked variant is
// cached forever under that address: an unshaded pre-fix blob, or act 1's colors on act 2's map, or the
// untraveled tint after the node became travelable.
//
// The signature therefore rides the wire (`spineMat`) and the clip URL (`&mat=`) exactly like `&skin=` does for
// the runtime skin: absent (null) for every spine node with no shader material, so those URLs stay
// byte-identical and their caches stay valid.
internal static class Sts2SpineMaterialKey
{
    // How many DISTINCT signatures a single node may report before the value latches (round-8 safety valve).
    // A uniform animated per-frame would otherwise mint a fresh clip URL every tick — an unbounded re-bake
    // storm. No shipped SpineSprite normal_material does that (they are set on state changes), so the latch
    // should never engage; it exists so the worst case degrades to "one slightly stale tint" instead of
    // hammering the extraction gate. Exposed for the test + the debug log.
    internal const int MaxSignatureChanges = 16;

    /// <summary>
    /// A short, process-stable signature for a shader material: the shader's resource path plus every uniform
    /// name/value pair, hashed to 16 lowercase hex chars. Deterministic across runs (plain FNV-1a 64 over the
    /// canonical text), so it is safe as part of an on-disk clip cache key. Returns null when there is no
    /// shader path (an in-memory/unsaved shader is not addressable, so it cannot discriminate anything).
    /// </summary>
    /// <param name="shaderResPath">The `res://` path of the material's shader.</param>
    /// <param name="uniforms">Uniform (name, value-text) pairs; order-insensitive (sorted here).</param>
    public static string? Compute(string? shaderResPath, IReadOnlyList<(string Name, string Value)>? uniforms)
    {
        if (string.IsNullOrWhiteSpace(shaderResPath))
        {
            return null;
        }

        var text = new StringBuilder(shaderResPath.Trim());
        if (uniforms is { Count: > 0 })
        {
            // Sort so the signature never depends on the order Godot happens to enumerate the uniform list in.
            var ordered = uniforms
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal);
            foreach (var (name, value) in ordered)
            {
                text.Append('|').Append(name).Append('=').Append(value ?? string.Empty);
            }
        }

        return Fnv1a64Hex(text.ToString());
    }

    // FNV-1a 64 over the UTF-8 bytes, rendered as 16 lowercase hex chars. Chosen over a cryptographic hash for
    // being tiny + allocation-light and (unlike string.GetHashCode) STABLE across processes — the signature ends
    // up inside an on-disk cache key, so a per-process salt would defeat the cache entirely.
    private static string Fnv1a64Hex(string text)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
