using Godot;

namespace Spirectl.Sts2.Live;

// Godot-typed face of the LIVE SPINE-MATERIAL CARRY (round-9). The producer records the uniform snapshot each
// `&mat=` signature was computed from (Sts2SpineInspector.ReadMaterialKey, main thread, per live SpineSprite that
// carries a `normal_material` ShaderMaterial); the bake resolves that signature — which rides the clip URL verbatim
// — and renders through a material carrying the LIVE values instead of the offline scene's authored ones.
//
// See Sts2SpineLiveMaterialStore for the full diagnosis (boss_map_point.tscn's ShaderMaterial is
// `resource_local_to_scene = true`, so the offline `PackedScene.Instantiate()` the bake used to read is a private
// copy with the AUTHORED tint, never the act tint `NBossMapPoint.RefreshColorInstantly` pushed into the live one).
internal static class Sts2SpineLiveMaterials
{
    // SPIRECTL_SPINE_LIVE_MATERIAL: escape hatch for the round-9 live-uniform carry. Default ON; `0`/`false`/`off`/
    // `no` restores the round-8 behaviour (bake through the OFFLINE scene instance's material, authored uniforms and
    // all) for an A/B. Read once — env vars are process-stable.
    internal static readonly bool Enabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_LIVE_MATERIAL") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    private static readonly Sts2SpineLiveMaterialStore<Variant> Store = new();

    /// <summary>How many distinct live snapshots are currently resolvable (diagnostics only).</summary>
    public static int Count => Store.Count;

    /// <summary>
    /// Remember the (shader, uniform values) pair a `&amp;mat=` signature was computed from. Called from the
    /// inspector on the main thread whenever a node reports a NEW signature; a repeat of a known signature is a
    /// no-op (equal hash ⇒ equal content). Never throws into the capture path.
    /// </summary>
    public static void Record(
        string? signature,
        string? shaderResPath,
        IReadOnlyList<(string Name, Variant Value)>? uniforms)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            Store.Record(signature, shaderResPath, uniforms);
        }
        catch
        {
            // A diagnostic/caching side channel must never cost the producer its per-tick spine read.
        }
    }

    /// <summary>
    /// A FRESH ShaderMaterial carrying the live uniform values a `&amp;mat=` signature was captured from, or null
    /// when the carry is disabled, the signature is unknown/evicted, or the shader cannot be loaded (the bake then
    /// falls back to the offline scene material — round-8 behaviour).
    /// </summary>
    /// <remarks>
    /// Deliberately a NEW material rather than a re-parameterised capture of the scene's own: a `.tscn` sub-resource
    /// that is NOT `resource_local_to_scene` is shared by every instantiation of that PackedScene — including the
    /// live node's — so writing uniforms onto a captured material could re-tint the RUNNING GAME. Building our own
    /// makes that impossible by construction. One instance is shared by every render lane (materials are shared
    /// resources by design); it is released with the render nodes.
    /// </remarks>
    public static ShaderMaterial? TryBuildMaterial(string? signature, out string? provenance)
    {
        provenance = null;
        if (!Enabled)
        {
            return null;
        }

        try
        {
            if (Store.Resolve(signature) is not { } snapshot
                || ResourceLoader.Load(snapshot.ShaderResPath) is not Shader shader)
            {
                return null;
            }

            var material = new ShaderMaterial { Shader = shader };
            var applied = 0;
            foreach (var (name, value) in snapshot.Uniforms)
            {
                // A Nil value means the live material carried no override for that uniform, so the shader's own
                // default applies — exactly what a fresh material already does. Skipping keeps the two identical.
                if (value.VariantType == Variant.Type.Nil)
                {
                    continue;
                }

                material.SetShaderParameter(name, value);
                applied += 1;
            }

            provenance =
                $"Applied the LIVE normal_material uniforms (mat={signature}, shader '{snapshot.ShaderResPath}', "
                + $"{applied} uniform(s) carried from the addressed node) instead of the offline scene instance's "
                + "authored values.";
            return material;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SPIRECTL_SPINE_DEBUG A/B evidence: the live uniform values a signature carries rendered as text, or null when
    /// it is unknown. Paired at the call site with the same read off the OFFLINE material, so a bake logs exactly
    /// which values it would have used before this fix and which it uses now.
    /// </summary>
    public static string? DescribeLive(string? signature)
    {
        if (Store.Resolve(signature) is not { } snapshot)
        {
            return null;
        }

        var parts = snapshot.Uniforms
            .Select(pair => $"{pair.Name}={Sts2SpineInspector.DescribeUniformValue(pair.Value)}")
            .OrderBy(text => text, StringComparer.Ordinal);
        return $"{snapshot.ShaderResPath}[{string.Join(' ', parts)}]";
    }
}
