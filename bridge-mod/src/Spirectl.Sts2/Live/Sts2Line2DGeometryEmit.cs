using System;
using System.Collections.Generic;
using System.Globalization;

namespace Spirectl.Sts2.Live;

// WS-2 producer rules for streaming `Line2D` STROKE GEOMETRY (the map quill annotations) — PURE and Godot-free (like
// Sts2RegionEmitCap / Sts2RichRoleFontEmit) so the change signature and the wire rounding are unit-testable without
// the Godot-coupled Sts2RuntimeSceneWatcher.
//
// Background. A map annotation is a `Line2D` instanced from `res://scenes/screens/map/map_line_draw.tscn` (pen) or
// `..._erase.tscn` (eraser) and appended LIVE as a direct child of `…/MapDrawing/DrawViewport`, a 960x1620
// SubViewport. Points are pushed with `AddPoint(pos * 0.5)` at >= 2px spacing while the finger/mouse drags. The
// watcher already streams those nodes (with the correct viewport-prefix x2 fit transform), but a `Line2D` carries NO
// texture and NO rect the generic walk understands — its whole appearance IS `points` + `width` + `default_color` —
// so every client rendered nothing. This adds those three as `linePoints` / `lineWidth` / `lineColor`.
//
// SHAPE ON THE WIRE. `linePoints` is FLATTENED (`[x0,y0,x1,y1,…]`) in NODE-LOCAL coordinates — the same space as
// `localRect`, i.e. the client transforms them by the node's streamed `transform` like every other box. Each value is
// rounded to 2 dp, matching the quantization the watcher already applies to transforms/rects (sub-2dp jitter is
// invisible on screen and would otherwise churn the wire).
//
// SKIPPED BY DESIGN:
//   * joint_mode / begin_cap_mode / end_cap_mode — constant `round` (2) on BOTH authored stroke scenes, so a client
//     hard-codes round joins/caps; a wire field would be dead weight on every stroke.
//   * texture / texture_mode — constant `tile` (1) over `trail2.png` / `trail3.png`; the texture already streams
//     through the generic primary-texture probe (`Line2D` is in ResolveTextureProbe's `texture` set).
//   * ERASER DETECTION needs no field of its own: an eraser stroke is exactly the one whose `material` shader is
//     `res://shaders/map_drawing/line_erase.gdshader` (blend_sub), and the SHADER PATH already streams as
//     `RuntimeSceneNodeDelta.Shader`. A consumer discriminates pen vs eraser off that.
//
// EMISSION POLICY (the intentFrames pattern, NOT the Text pattern): the three fields ship as ONE unit and are
// STICKY — emitted only on `includeStatic` (add/keyframe) or when this node's cheap per-tick SIGNATURE changed, and
// carried forward by the client's MergeVolatile in between. Per tick the watcher computes ONLY <see cref="Signature"/>
// (point COUNT + last point + width + color); the full `Points` array is marshalled exactly once, at EMIT time, by
// <see cref="FlattenRounded"/>. That is what keeps an idle map (dozens of finished strokes) at zero cost.
//
// PERF ENVELOPE. An ACTIVELY-DRAWN stroke changes its signature on most ticks, so it re-ships its WHOLE point array
// each time: ~4-6 KB of JSON at 300 points, for the ~seconds one drag lasts, for the ONE stroke being drawn. At rest
// (finger up, or merely viewing the map) it is exactly zero — the signature is stable, so nothing is read or sent.
// The documented drop-in follow-up if that burst ever matters is a `LineEmitCapMs` throttle in the
// <see cref="Sts2RegionEmitCap"/> shape (pin the last-emitted signature inside the window, let one array through per
// window, always let a COUNT DROP — clear/undo — through immediately). Deliberately NOT implemented now: it trades
// stroke-tip latency for bandwidth and nothing has yet shown the trade is needed.
//
// SCOPE (WS4, Aug-10). The first cut gated on TYPE ALONE (`node.IsClass("Line2D")`), which is wrong: a Line2D is a
// generic Godot primitive and the game uses it for VFX too — most visibly `card_trail_<character>.tscn`, whose
// `Trails/OuterTrail` (width 96) + `Trails/InnerTrail` (width 64) ride EVERY flying card. Those trails get their
// entire look from a `width_curve` taper, an alpha `gradient`, a stretched `trail.png` and an ADDITIVE material —
// all four of which this emit deliberately drops (see SKIPPED BY DESIGN above) — so streaming their points made a
// client paint a thick solid bar behind every card instead of the comet the game draws. Worse, they are the exact
// worst case for the "re-ship the whole array on every changed tick" policy: a discard→draw reshuffle spawns 2
// GROWING strokes PER CARD at once.
//
// So the stream is scoped to the nodes the feature was written for — the MAP QUILL strokes — by scene identity
// (`map_line_draw.tscn` / `map_line_erase.tscn`), with a structural fallback for a stroke built in code (direct
// child of `DrawViewport`). Every other Line2D is back to its pre-Aug-7 wire, and card trails are reconstructed
// CLIENT-SIDE from the trail node's own streamed motion (its local transform is the exact inverse of the flying
// card's global one — see the couch-coop mirror's cardTrail module), which needs no geometry on the wire at all.
//
// DEFAULT `map` (map strokes only); SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY=0 seeds it OFF, which restores the
// pre-feature wire BYTE-IDENTICALLY (all three fields stay null ⇒ omitted by WhenWritingNull, and no signature is
// ever computed); `=all` restores the Aug-7 type-only behaviour, kept as the A/B lever for measuring what the
// unscoped stream costs. See Sts2SceneWatchRuntimeSettings.Line2DGeometryScope.
internal static class Sts2Line2DGeometryEmit
{
    // The authored map-annotation stroke scenes are `res://scenes/screens/map/map_line_draw.tscn` (pen, width 4) and
    // `…/map_line_erase.tscn` (eraser, width 12). Matched on the FILE NAME PREFIX rather than the full res:// path so
    // a directory move (or a future `map_line_highlight.tscn`) keeps working; `card_trail_*.tscn` and every other VFX
    // scene are excluded by construction.
    private const string MapStrokeScenePrefix = "map_line_";

    // The live parent a stroke is appended under: `…/MapDrawing/DrawViewport`, a 960x1620 SubViewport. Used ONLY as a
    // fallback for a stroke that carries no SceneFilePath (i.e. one built with `new Line2D()` instead of instanced
    // from the authored scene) — a card trail's Line2D parent is `Trails`, so this cannot re-admit one.
    private const string MapStrokeParentName = "DrawViewport";

    // Which Line2D nodes stream stroke geometry. Read from SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY per capture pass, so
    // it is also flippable at runtime through Sts2SceneWatchRuntimeSettings for an A/B.
    internal enum Scope
    {
        // No Line2D streams geometry — the pre-feature wire, byte-identically.
        Off = 0,

        // DEFAULT: only the map quill strokes (the nodes this feature exists for).
        MapStrokes = 1,

        // Every Line2D in the game (the Aug-7 type-only behaviour). Kept as a measurement lever, not a product mode:
        // it re-introduces the solid-bar card trails and the reshuffle marshal storm.
        All = 2,
    }

    // Env → Scope. Unset/blank keeps the default; the historical boolean spellings still mean what they meant
    // (`0/false/off/no` ⇒ Off), and anything else that is not `all` falls back to the default rather than throwing —
    // a typo'd env must not silently disable the map strokes.
    internal static Scope ParseScope(string? raw, Scope fallback = Scope.MapStrokes)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" => Scope.Off,
            "all" or "every" or "2" => Scope.All,
            _ => fallback,
        };
    }

    // Is this node one of the map quill strokes? `sceneFilePath` is Godot's `Node.SceneFilePath`, non-empty only on an
    // instanced-scene ROOT — which the stroke IS (the authored scenes' root node is the Line2D itself), while a card
    // trail's Line2D is a CHILD of the `card_trail_*.tscn` root and therefore carries none. `parentName` is the
    // fallback for a code-built stroke.
    internal static bool IsMapStroke(string? sceneFilePath, string? parentName)
    {
        if (sceneFilePath is { Length: > 0 })
        {
            var slash = sceneFilePath.LastIndexOf('/');
            var file = slash >= 0 ? sceneFilePath.AsSpan(slash + 1) : sceneFilePath.AsSpan();
            if (file.StartsWith(MapStrokeScenePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return string.Equals(parentName, MapStrokeParentName, StringComparison.Ordinal);
    }

    // The per-tick admission test, split from the per-add structural facts so the walk pays two bool reads and the env
    // stays live-flippable. `isLine2D` is the native class test; `isMapStroke` is IsMapStroke resolved once on add.
    internal static bool ShouldStream(Scope scope, bool isLine2D, bool isMapStroke)
        => isLine2D && scope switch
        {
            Scope.Off => false,
            Scope.All => true,
            _ => isMapStroke,
        };

    // The canonical "no points" payload. Deliberately an EMPTY LIST rather than null: a stroke whose points were
    // CLEARED (undo / clear-all before the node itself is freed) must ship `linePoints: []` so the client erases what
    // it drew. Null means "unchanged, keep what you have" — the opposite instruction.
    internal static readonly IReadOnlyList<double> NoPoints = Array.Empty<double>();

    // The watcher's house quantization for anything positional (transforms, rects, and now stroke points): 2 dp,
    // banker's rounding, with negative zero normalised to +0 so the same coordinate never produces two different
    // signature strings ("-0" vs "0").
    internal static double Round2(double value)
    {
        var rounded = Math.Round(value, 2);
        return rounded == 0 ? 0 : rounded;
    }

    // The CHEAP per-tick change signature for one Line2D. Deliberately does NOT look at the interior of the point
    // array — marshalling `Points` every tick for every stroke is precisely the cost this exists to avoid.
    //
    // Format: "{count}|{width}|{#rrggbbaa}|{lastX},{lastY}" (2-dp numbers, invariant culture). An empty stroke has an
    // empty trailing point section: "0|{width}|{#rrggbbaa}|".
    //
    // What each term catches:
    //   count      — an APPEND (the normal drawing case: `AddPoint` at >= 2px spacing) and a CLEAR/UNDO (count drops).
    //   last point — a same-count edit, i.e. an undo-then-redraw that lands back on the same length with a different
    //                tip. Without it such a stroke would be invisible to the detector.
    //   width      — pen (4) vs eraser (12), and any runtime width change.
    //   color      — `default_color`, which the map palette sets per stroke.
    // A mid-array edit at unchanged count AND unchanged tip is not expressible by the game's own stroke API
    // (append-only until the whole node is rebuilt), so it is out of scope by construction.
    internal static string Signature(int pointCount, double width, string? colorHtml, double lastX, double lastY)
    {
        var head = string.Concat(
            (pointCount > 0 ? pointCount : 0).ToString(CultureInfo.InvariantCulture),
            "|",
            Format(width),
            "|",
            colorHtml ?? string.Empty,
            "|");

        return pointCount > 0
            ? string.Concat(head, Format(lastX), ",", Format(lastY))
            : head;
    }

    // Flatten an interleaved `[x0,y0,x1,y1,…]` coordinate sequence to the wire payload, rounding every value to 2 dp.
    // Called ONCE per emitted stroke delta (never per tick).
    //
    // Tolerates the two degenerate inputs the live tree can hand back: EMPTY (a stroke node that exists but has no
    // points yet — the frame between `_Ready` and the first `AddPoint`) and ODD length (a torn read of the marshalled
    // array); an unpaired trailing coordinate is not a point and is dropped rather than shipped as `(x, undefined)`.
    internal static IReadOnlyList<double> FlattenRounded(IReadOnlyList<double>? interleavedXy)
    {
        if (interleavedXy is null)
        {
            return NoPoints;
        }

        var values = (interleavedXy.Count / 2) * 2; // drop an unpaired trailing coordinate
        if (values <= 0)
        {
            return NoPoints;
        }

        var flattened = new double[values];
        for (var i = 0; i < values; i++)
        {
            flattened[i] = Round2(interleavedXy[i]);
        }

        return flattened;
    }

    private static string Format(double value)
        => Round2(value).ToString("0.##", CultureInfo.InvariantCulture);
}
