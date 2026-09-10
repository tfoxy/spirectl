using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-2 map quill strokes render as nothing in the mirror — the pure Line2D stroke-geometry emit rules
// (Sts2Line2DGeometryEmit). A map annotation is a `Line2D` appended live under `…/MapDrawing/DrawViewport` whose
// ENTIRE appearance is points + width + default_color; the watcher streamed such a node with a correct transform but
// no geometry at all. These are the two decisions that make the fix affordable: the CHEAP per-tick change signature
// (which must never marshal the point array) and the 2-dp flatten that builds the wire payload at emit time.
public sealed class Sts2Line2DGeometryEmitTests
{
    private const string Red = "#ff0000ff";
    private const string Blue = "#0000ffff";

    private const double PenWidth = 4;     // map_line_draw.tscn
    private const double EraserWidth = 12; // map_line_erase.tscn

    [Fact]
    public void Signature_HasTheDocumentedFormat()
    {
        // "{count}|{width}|{#rrggbbaa}|{lastX},{lastY}" — the exact string a downstream reader may be diffed against.
        Assert.Equal(
            "3|4|#ff0000ff|10.5,20.25",
            Sts2Line2DGeometryEmit.Signature(3, PenWidth, Red, 10.5, 20.25));
    }

    [Fact]
    public void Signature_OfAnEmptyStroke_HasAnEmptyPointSection()
    {
        // A Line2D that exists but has no points yet (the frame between _Ready and the first AddPoint), and the
        // degenerate negative count a torn read could produce — both collapse to the same well-defined string.
        Assert.Equal("0|4|#ff0000ff|", Sts2Line2DGeometryEmit.Signature(0, PenWidth, Red, 0, 0));
        Assert.Equal("0|4|#ff0000ff|", Sts2Line2DGeometryEmit.Signature(-1, PenWidth, Red, 99, 99));
    }

    [Fact]
    public void Signature_IsStableForAnUnchangedStroke()
    {
        // The load-bearing property: a finished stroke sitting on the map must re-signature IDENTICALLY every tick,
        // or an idle map would re-ship every stroke's whole point array forever.
        Assert.Equal(
            Sts2Line2DGeometryEmit.Signature(120, PenWidth, Red, 480.5, 810.25),
            Sts2Line2DGeometryEmit.Signature(120, PenWidth, Red, 480.5, 810.25));
    }

    [Fact]
    public void Signature_DetectsAnAppend()
    {
        // The normal drawing case: AddPoint(pos * 0.5) at >= 2px spacing grows the count and moves the tip.
        var before = Sts2Line2DGeometryEmit.Signature(41, PenWidth, Red, 100, 200);
        var after = Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103, 204);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Signature_DetectsAClearOrUndo_ByCountDrop()
    {
        // Clear-all / undo shrinks the stroke. Both the count and the tip move, so the geometry re-ships and the
        // client can erase what it drew (an emptied stroke ships `linePoints: []`).
        var drawn = Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103, 204);

        Assert.NotEqual(drawn, Sts2Line2DGeometryEmit.Signature(20, PenWidth, Red, 60, 120));
        Assert.NotEqual(drawn, Sts2Line2DGeometryEmit.Signature(0, PenWidth, Red, 0, 0));
    }

    [Fact]
    public void Signature_DetectsASameCountEdit_ViaTheLastPoint()
    {
        // Why the tip is in the signature at all: an undo-then-redraw can land back on the SAME point count with a
        // different stroke. Count alone would call that unchanged and the client would keep the stale polyline.
        Assert.NotEqual(
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103, 204),
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 51, 88));
    }

    [Fact]
    public void Signature_DetectsAWidthChange()
    {
        // Pen (4) vs eraser (12) — and any runtime width change on the same node.
        Assert.NotEqual(
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103, 204),
            Sts2Line2DGeometryEmit.Signature(42, EraserWidth, Red, 103, 204));
    }

    [Fact]
    public void Signature_DetectsAColorChange()
    {
        // default_color is what the map palette sets per stroke; a recolour must re-ship the unit.
        Assert.NotEqual(
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103, 204),
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Blue, 103, 204));
    }

    [Fact]
    public void Signature_IgnoresSubQuantumJitter()
    {
        // The signature quantizes to the SAME 2 dp the wire does, so a coordinate that cannot change a single
        // emitted byte cannot trigger an emit either.
        Assert.Equal(
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103.001, 204.002),
            Sts2Line2DGeometryEmit.Signature(42, PenWidth, Red, 103.004, 204.001));
    }

    [Fact]
    public void Signature_ToleratesAnUnknownColor()
    {
        // A colour that could not be read leaves the section empty rather than the word "null"; still a stable,
        // comparable string.
        Assert.Equal("2|4||1,2", Sts2Line2DGeometryEmit.Signature(2, PenWidth, null, 1, 2));
    }

    [Fact]
    public void FlattenRounded_InterleavesAndRoundsToTwoDecimals()
    {
        var flattened = Sts2Line2DGeometryEmit.FlattenRounded([1.234, 5.678, -3.14159, 0.005001]);

        Assert.Equal([1.23, 5.68, -3.14, 0.01], flattened);
    }

    [Fact]
    public void FlattenRounded_PassesAlreadyQuantizedValuesThrough()
    {
        // Points arrive from AddPoint(pos * 0.5), so exact halves are the common case and must not be perturbed.
        Assert.Equal([0.5, 480.0, -12.25, 810.75], Sts2Line2DGeometryEmit.FlattenRounded([0.5, 480.0, -12.25, 810.75]));
    }

    [Fact]
    public void FlattenRounded_NormalisesNegativeZero()
    {
        // -0 and 0 are the same coordinate; letting both through would produce two different signatures (and two
        // different JSON spellings) for one stroke.
        var flattened = Sts2Line2DGeometryEmit.FlattenRounded([-0.001, -0.0]);

        Assert.False(double.IsNegative(flattened[0]));
        Assert.False(double.IsNegative(flattened[1]));
    }

    [Fact]
    public void FlattenRounded_OfNullOrEmpty_IsEmptyNotNull()
    {
        // An emptied stroke must ship `linePoints: []` (erase what you drew), never null (which means "unchanged").
        Assert.Empty(Sts2Line2DGeometryEmit.FlattenRounded(null));
        Assert.Empty(Sts2Line2DGeometryEmit.FlattenRounded([]));
    }

    [Fact]
    public void FlattenRounded_DropsAnUnpairedTrailingCoordinate()
    {
        // A torn read of the marshalled array: an x with no y is not a point, and shipping it would desync every
        // subsequent coordinate pair on the client.
        Assert.Equal([1.0, 2.0], Sts2Line2DGeometryEmit.FlattenRounded([1, 2, 3]));
        Assert.Empty(Sts2Line2DGeometryEmit.FlattenRounded([1]));
    }

    [Fact]
    public void Round2_MatchesTheWatchersHouseQuantization()
    {
        Assert.Equal(103.0, Sts2Line2DGeometryEmit.Round2(103.004));
        Assert.Equal(103.01, Sts2Line2DGeometryEmit.Round2(103.0051));
        Assert.Equal(-3.14, Sts2Line2DGeometryEmit.Round2(-3.14159));
        Assert.False(double.IsNegative(Sts2Line2DGeometryEmit.Round2(-0.004)));
    }

    // ---- WS4 SCOPE ---------------------------------------------------------------------------------------------
    // The first cut gated on TYPE ALONE, so it fired for every Line2D in the game. The card trails behind a flying
    // card (`card_trail_<character>.tscn` → `Trails/OuterTrail` width 96 + `Trails/InnerTrail` width 64) are the
    // regression that forced this scope: their whole look is a width_curve + gradient + stretched texture +
    // additive material, none of which this unit carries, so a client drew a thick solid bar; and a discard→draw
    // reshuffle re-marshalled TWO growing point arrays per card per tick.

    private const string PenScene = "res://scenes/screens/map/map_line_draw.tscn";
    private const string EraserScene = "res://scenes/screens/map/map_line_erase.tscn";

    [Fact]
    public void IsMapStroke_AcceptsBothAuthoredStrokeScenes()
    {
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke(PenScene, parentName: null));
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke(EraserScene, parentName: null));
    }

    [Fact]
    public void IsMapStroke_MatchesOnTheFileNameNotTheFullPath()
    {
        // Prefix-on-file-name, so a directory move (or a future `map_line_highlight.tscn`) keeps streaming rather
        // than silently rendering nothing on the map.
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke("res://scenes/map/map_line_draw.tscn", null));
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke("map_line_erase.tscn", null));
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke("res://x/map_line_highlight.tscn", null));
    }

    [Fact]
    public void IsMapStroke_RejectsEveryCardTrailScene()
    {
        // A card trail's Line2D is a CHILD of the trail scene root, so it carries no SceneFilePath at all — but even
        // if it somehow did, the trail scenes must never match.
        foreach (var character in new[] { "ironclad", "silent", "defect", "regent", "necrobinder" })
        {
            Assert.False(Sts2Line2DGeometryEmit.IsMapStroke($"res://scenes/vfx/card_trail_{character}.tscn", null));
        }
    }

    [Fact]
    public void IsMapStroke_RejectsTheOtherLine2DVfxScenes()
    {
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke("res://scenes/vfx/vfx_hyperbeam.tscn", null));
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke("res://scenes/vfx/cards/bolas_vfx.tscn", null));
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke("res://scenes/combat/remote_targeting_indicator.tscn", null));
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke("res://scenes/creature_visuals/architect.tscn", null));
    }

    [Fact]
    public void IsMapStroke_FallsBackToTheDrawViewportParent()
    {
        // A stroke built with `new Line2D()` instead of instanced carries no SceneFilePath; it is still a map
        // annotation, identified by the SubViewport it is appended to (…/MapDrawing/DrawViewport).
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke(null, "DrawViewport"));
        Assert.True(Sts2Line2DGeometryEmit.IsMapStroke(string.Empty, "DrawViewport"));
    }

    [Fact]
    public void IsMapStroke_ParentFallbackCannotReadmitATrail()
    {
        // The trail Line2Ds' parent is `Trails` — the one thing that must not slip through the fallback.
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke(null, "Trails"));
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke(null, null));
        Assert.False(Sts2Line2DGeometryEmit.IsMapStroke(null, "DrawViewportContainer"));
    }

    [Fact]
    public void ParseScope_DefaultsToMapStrokes()
    {
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope(null));
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope("   "));
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope("1"));
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope("on"));
    }

    [Fact]
    public void ParseScope_KeepsTheHistoricalBooleanOffSpellings()
    {
        // SPIRECTL_SCENE_WATCH_LINE2D_GEOMETRY=0 was the shipped kill-switch; it must still mean "no geometry at
        // all" (the byte-identical pre-feature wire), not "default".
        foreach (var raw in new[] { "0", "false", "FALSE", "off", "no", " off " })
        {
            Assert.Equal(Sts2Line2DGeometryEmit.Scope.Off, Sts2Line2DGeometryEmit.ParseScope(raw));
        }
    }

    [Fact]
    public void ParseScope_UnderstandsTheUnscopedAbLever()
    {
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.All, Sts2Line2DGeometryEmit.ParseScope("all"));
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.All, Sts2Line2DGeometryEmit.ParseScope("ALL"));
    }

    [Fact]
    public void ParseScope_FallsBackRatherThanDisablingOnATypo()
    {
        // A misspelled value must not silently blank the map annotations.
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope("mpa"));
        Assert.Equal(Sts2Line2DGeometryEmit.Scope.MapStrokes, Sts2Line2DGeometryEmit.ParseScope("yes"));
    }

    [Fact]
    public void ShouldStream_AdmitsOnlyMapStrokesByDefault()
    {
        const Sts2Line2DGeometryEmit.Scope scope = Sts2Line2DGeometryEmit.Scope.MapStrokes;

        Assert.True(Sts2Line2DGeometryEmit.ShouldStream(scope, isLine2D: true, isMapStroke: true));
        Assert.False(Sts2Line2DGeometryEmit.ShouldStream(scope, isLine2D: true, isMapStroke: false));
    }

    [Fact]
    public void ShouldStream_NeverAdmitsANonLine2D()
    {
        // The type test is the outer gate in every scope — `all` must not start signaturing arbitrary nodes.
        foreach (var scope in new[]
                 {
                     Sts2Line2DGeometryEmit.Scope.Off,
                     Sts2Line2DGeometryEmit.Scope.MapStrokes,
                     Sts2Line2DGeometryEmit.Scope.All,
                 })
        {
            Assert.False(Sts2Line2DGeometryEmit.ShouldStream(scope, isLine2D: false, isMapStroke: true));
        }
    }

    [Fact]
    public void ShouldStream_OffAdmitsNothing()
    {
        Assert.False(Sts2Line2DGeometryEmit.ShouldStream(
            Sts2Line2DGeometryEmit.Scope.Off, isLine2D: true, isMapStroke: true));
    }

    [Fact]
    public void ShouldStream_AllAdmitsEveryLine2D()
    {
        Assert.True(Sts2Line2DGeometryEmit.ShouldStream(
            Sts2Line2DGeometryEmit.Scope.All, isLine2D: true, isMapStroke: false));
    }
}
