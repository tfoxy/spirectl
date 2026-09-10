using Godot;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Shared unwrapping of Godot's two authored "ramp" resources into serializable snapshots:
//
//   Gradient / GradientTexture1D / GradientTexture2D -> a list of (offset, color) stops
//   Curve    / CurveTexture                          -> a list of (x, y) points
//
// Both shapes were originally private to Sts2ParticleInspector (particle `color_ramp` / `scale_curve` / ...),
// but the SAME resources also show up as ShaderMaterial SAMPLER uniforms — STS2's VFX shaders colour a particle
// per TEXEL through a `lut` GradientTexture1D (`COLOR = vec4(texture(lut, texture_color.rr).rgb, erosion) *
// vertex_color`), so a client that only receives the sampler's resource PATH cannot reproduce the colour at all.
// Promoted here so the particle inspector and the scene watcher's shader-parameter path share one implementation.
internal static class Sts2RampExtractor
{
    // The authored stops of a Gradient (directly, or wrapped in a GradientTexture1D/2D). Null when the object is
    // not a gradient or carries no points.
    public static IReadOnlyList<RuntimeSceneParticleGradientStopSnapshot>? ExtractGradientStops(GodotObject? obj)
    {
        var gradient = UnwrapGradient(obj);
        if (gradient is null)
        {
            return null;
        }

        var count = gradient.GetPointCount();
        if (count == 0)
        {
            return null;
        }

        var stops = new List<RuntimeSceneParticleGradientStopSnapshot>(count);
        for (var i = 0; i < count; i++)
        {
            stops.Add(new RuntimeSceneParticleGradientStopSnapshot(gradient.GetOffset(i), ToColor(gradient.GetColor(i))));
        }

        return stops;
    }

    // Godot `Gradient.interpolation_mode`: 0 = LINEAR (default), 1 = CONSTANT (nearest — hold each stop until the
    // next), 2 = CUBIC. Read through the snake_case `Get` (the inspector idiom) so we do not depend on the C#
    // binding enum name. Null when absent or the default, so the field stays off the wire for the common case.
    public static int? ExtractGradientInterpolation(GodotObject? obj)
    {
        var gradient = UnwrapGradient(obj);
        if (gradient is null)
        {
            return null;
        }

        try
        {
            var mode = gradient.Get("interpolation_mode").AsInt32();
            return mode == 0 ? null : mode;
        }
        catch
        {
            return null;
        }
    }

    // The authored points of a Curve (directly, or wrapped in a CurveTexture). Null when the object is not a curve
    // or carries no points.
    public static IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? ExtractCurvePoints(GodotObject? obj)
    {
        var curve = obj switch
        {
            Curve c => c,
            CurveTexture ct => ct.Curve,
            _ => null,
        };
        if (curve is null)
        {
            return null;
        }

        var count = curve.GetPointCount();
        if (count == 0)
        {
            return null;
        }

        var points = new List<RuntimeSceneParticleCurvePointSnapshot>(count);
        for (var i = 0; i < count; i++)
        {
            var p = curve.GetPointPosition(i);
            points.Add(new RuntimeSceneParticleCurvePointSnapshot(p.X, p.Y));
        }

        return points;
    }

    private static Gradient? UnwrapGradient(GodotObject? obj)
        => obj switch
        {
            Gradient g => g,
            GradientTexture1D gt1 => gt1.Gradient,
            GradientTexture2D gt2 => gt2.Gradient,
            _ => null,
        };

    private static RuntimeSceneColorSnapshot ToColor(Color c) => new(c.R, c.G, c.B, c.A, $"#{c.ToHtml(includeAlpha: true)}");
}
