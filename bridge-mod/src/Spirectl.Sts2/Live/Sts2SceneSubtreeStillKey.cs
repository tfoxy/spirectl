using System.Globalization;

using Spirectl.Sts2.Core.Artifacts;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Godot-free parser for the <c>scene-subtree://</c> asset key.
///
/// <para>The key addresses ONE node of a packed scene and renders only that subtree:
/// <c>scene-subtree://&lt;res-scene&gt;?node=&lt;sceneRelativePath&gt;</c>. Everything past <c>node</c> is an
/// optional POSING knob, because the node an effect still wants is very often not renderable exactly as it was
/// authored — the authored value is the resting state the game tweens AWAY from at runtime. The two cases that
/// created these knobs:</para>
/// <list type="bullet">
/// <item><description>Every authored <c>card_ripple</c> material ships <c>shader_parameter/width = 0.0</c>; the
/// card's highlight script tweens it up when the card becomes playable. Rendered as authored the shader's
/// <c>smoothstep(1.0, 1.0, …)</c> is degenerate and the capture is fully transparent, which the extract provider
/// (correctly) refuses. <c>shaderParam.width=0.075</c> is what makes the still the state a player actually
/// sees.</description></item>
/// <item><description>The card rarity glows tween <c>modulate:a</c> 1.0 → 0.9 on tree entry, and a consumer that
/// composites the still under the node's own live modulate would then apply that fade twice.
/// <c>modulate=1,1,1,1</c> pins the still to the authored value and leaves the fade to the consumer.</description></item>
/// </list>
///
/// <para>WHY <c>backdrop=black</c> EXISTS, and why it is not a cosmetic preference. STS2's glow art is authored
/// for ADDITIVE blending: the sprites are fully opaque with BLACK where nothing should show, because black adds
/// nothing. Captured over TRANSPARENCY that encoding falls apart — the black areas become opaque black quads
/// with real alpha, and whether the result can be re-composited at all then depends on whether the engine's
/// readback happened to un-premultiply, which is an inference about the renderer rather than a property of the
/// artifact. Over an opaque black backdrop there is nothing to infer: the capture IS
/// <c>black + Σ(contribution)</c>, which is exactly the quantity an additive compositor (Godot's
/// <c>blend_add</c>, CSS <c>plus-lighter</c>) adds, and the alpha channel stops carrying meaning at all.</para>
///
/// <para>WHY THE KNOBS RIDE THE KEY STRING. The key is already a string that travels end to end (CLI query →
/// IPC → bridge). Expressing the overrides inside it keeps the wire contract and the generated protocol
/// untouched: only this parser and the render branch that consumes <see cref="SceneSubtreeStillOptions"/> know
/// they exist.</para>
///
/// <para>WHY IT IS ITS OWN GODOT-FREE FILE (the <see cref="Sts2EventBackgroundFrameMath"/> precedent): the parse
/// is pure string/number work with a lot of malformation cases, and the provider that consumes it is
/// live-host-only, so inline it could not be unit-tested without the game.</para>
/// </summary>
internal static class Sts2SceneSubtreeStillKey
{
    internal const string Prefix = "scene-subtree://";

    /// <summary>The scene-relative node path that addresses the scene's OWN ROOT.</summary>
    internal const string RootNodePath = ".";

    /// <summary>The <c>particles=</c> value that keeps emitters simulating through the capture.</summary>
    internal const string LiveParticlesToken = "live";

    /// <summary>The <c>backdrop=</c> value that captures over opaque black instead of transparency.</summary>
    internal const string BlackBackdropToken = "black";

    /// <summary>The <c>shaderParam.&lt;name&gt;=</c> query-key prefix.</summary>
    private const string ShaderParamPrefix = "shaderParam.";

    internal readonly record struct ParsedKey(string ScenePath, string NodePath, SceneSubtreeStillOptions Options);

    /// <summary>
    /// Parse a <c>scene-subtree://</c> key, or null when it is not one / is malformed.
    ///
    /// <para>Malformation is deliberately ALL-OR-NOTHING for the addressing half (a missing scheme, a scene path
    /// without <c>res://</c>, a missing or empty <c>node</c>) — the caller then falls through to the ordinary
    /// resource path rather than rendering something the key did not ask for. A malformed POSING knob is dropped
    /// individually instead, for the same reason <see cref="Sts2EventBackgroundFrameMath.TryParseFrameSpec"/>
    /// returns null on a half-parsed frame: a still that silently rendered at a wrong-but-plausible rect would be
    /// harder to notice than one that rendered at the default.</para>
    /// </summary>
    internal static ParsedKey? TryParse(string? key)
    {
        var raw = key?.Replace('\\', '/').Trim() ?? string.Empty;
        if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = raw[Prefix.Length..];
        var queryStart = rest.IndexOf('?');
        if (queryStart <= 0)
        {
            return null;
        }

        var scenePath = rest[..queryStart];
        if (!scenePath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? nodePath = null;
        SceneSubtreeRect? rect = null;
        Dictionary<string, float>? shaderParameters = null;
        IReadOnlyList<float>? modulate = null;
        var liveParticles = false;
        var blackBackdrop = false;

        foreach (var pair in rest[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var name = pair[..equalsIndex];
            var value = pair[(equalsIndex + 1)..];
            switch (name)
            {
                case "node":
                    nodePath = Uri.UnescapeDataString(value);
                    break;
                case "rect":
                    rect = TryParseRect(value);
                    break;
                case "modulate":
                    modulate = TryParseColor(value);
                    break;
                case "particles":
                    liveParticles = string.Equals(value, LiveParticlesToken, StringComparison.OrdinalIgnoreCase);
                    break;
                case "backdrop":
                    blackBackdrop = string.Equals(value, BlackBackdropToken, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    if (name.StartsWith(ShaderParamPrefix, StringComparison.Ordinal)
                        && name.Length > ShaderParamPrefix.Length
                        && TryParseFloat(value, out var uniform))
                    {
                        shaderParameters ??= new Dictionary<string, float>(StringComparer.Ordinal);
                        // Case-preserving: a Godot uniform name is case-sensitive.
                        shaderParameters[Uri.UnescapeDataString(name[ShaderParamPrefix.Length..])] = uniform;
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        return new ParsedKey(
            scenePath,
            nodePath,
            new SceneSubtreeStillOptions
            {
                NodeLocalRect = rect,
                ShaderParameters = shaderParameters,
                Modulate = modulate,
                LiveParticles = liveParticles,
                BlackBackdrop = blackBackdrop,
            });
    }

    /// <summary>True when this node path addresses the scene's own root rather than a descendant.</summary>
    internal static bool AddressesSceneRoot(string? nodePath)
        => string.Equals(nodePath?.Trim(), RootNodePath, StringComparison.Ordinal);

    /// <summary>
    /// The render notes describing what was posed, so a response says which of these knobs actually took effect
    /// rather than leaving a caller to infer it from the pixels. Uniform names are sorted for a stable note.
    /// </summary>
    internal static IReadOnlyList<string> Describe(SceneSubtreeStillOptions options)
    {
        var notes = new List<string>(2);
        if (options.ShaderParameters is { Count: > 0 } parameters)
        {
            var formatted = parameters
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value.ToString(CultureInfo.InvariantCulture)}");
            notes.Add($"Overrode shader parameter(s) {string.Join(", ", formatted)} on the addressed node.");
        }

        if (options.Modulate is { Count: 4 } modulate)
        {
            var channels = modulate.Select(channel => channel.ToString(CultureInfo.InvariantCulture));
            notes.Add($"Pinned the addressed node's modulate to ({string.Join(", ", channels)}).");
        }

        if (options.BlackBackdrop)
        {
            notes.Add(
                "Captured over an opaque black backdrop, so an additive effect's pixels are its contribution and the alpha channel carries no meaning.");
        }

        return notes;
    }

    /// <summary>
    /// <c>rect=x,y,w,h</c> in NODE-LOCAL units, one unit per output pixel. Null on any malformation or a
    /// non-positive size — a zero-area capture is a blank image, not a smaller one.
    /// </summary>
    private static SceneSubtreeRect? TryParseRect(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 4
            || !TryParseFloat(parts[0], out var x)
            || !TryParseFloat(parts[1], out var y)
            || !TryParseFloat(parts[2], out var width)
            || !TryParseFloat(parts[3], out var height)
            || width < 1f
            || height < 1f)
        {
            return null;
        }

        return new SceneSubtreeRect(x, y, width, height);
    }

    /// <summary>
    /// <c>modulate=r,g,b,a</c>, four finite floats. NOT clamped to 0..1: Godot's own modulate is unclamped and an
    /// HDR-ish over-1 tint is a thing an authored scene can carry, so refusing it here would be this parser
    /// inventing a rule the engine does not have.
    /// </summary>
    private static IReadOnlyList<float>? TryParseColor(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 4)
        {
            return null;
        }

        var channels = new float[4];
        for (var index = 0; index < 4; index++)
        {
            if (!TryParseFloat(parts[index], out channels[index]))
            {
                return null;
            }
        }

        return channels;
    }

    private static bool TryParseFloat(string value, out float parsed)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && float.IsFinite(parsed);
}
