#if ENABLE_STS2_LIVE_HOST
extern alias GodotLive;
#endif
using Spirectl.Sts2.Live;
using Xunit;
#if ENABLE_STS2_LIVE_HOST
using GVariant = GodotLive::Godot.Variant;
using GColor = GodotLive::Godot.Color;
using GVector2 = GodotLive::Godot.Vector2;
#endif

namespace Spirectl.BridgeMod.Tests;

// Pure, Godot-free core of Part C tween-endpoint resolution: Stage 1 shifts a streamed 6-tuple's origin by a local
// position delta mapped through the parent's basis; Stage 2 reconstructs the full end LOCAL transform (position +
// scale + rotation, pivot-correct) and composes it through the parent. (The Godot-typed wrappers PositionEndGlobal /
// TransformEndGlobal delegate here; their Transform2D reads are exercised by the live card verification.)
public sealed class Sts2TweenEndpointMathTests
{
    [Fact]
    public void PositionEndTuple_IdentityBasis_ShiftsOriginByLocalDelta()
    {
        // gNow origin (50,60), identity basis; delta (20,5) → origin (70,65); basis unchanged.
        var end = Sts2TweenEndpointTuples.PositionEndTuple(
            new double[] { 1, 0, 0, 1, 50, 60 }, a: 1, b: 0, c: 0, d: 1, dx: 20, dy: 5);
        Assert.Equal(new double[] { 1, 0, 0, 1, 70, 65 }, end);
    }

    [Fact]
    public void PositionEndTuple_MapsDeltaThroughParentScaleBasis()
    {
        // Parent scale 2 → delta (20,5) maps to (40,10); origin (50,60) → (90,70).
        var end = Sts2TweenEndpointTuples.PositionEndTuple(
            new double[] { 1, 0, 0, 1, 50, 60 }, a: 2, b: 0, c: 0, d: 2, dx: 20, dy: 5);
        Assert.Equal(new double[] { 1, 0, 0, 1, 90, 70 }, end);
    }

    [Fact]
    public void PositionEndTuple_MapsDeltaThroughParentRotationBasis()
    {
        // Parent basis = 90° rotation: X=(0,1), Y=(-1,0). BasisXform((10,0)) = (0,10).
        var end = Sts2TweenEndpointTuples.PositionEndTuple(
            new double[] { 1, 0, 0, 1, 0, 0 }, a: 0, b: 1, c: -1, d: 0, dx: 10, dy: 0);
        Assert.Equal(0, end[4], 6);
        Assert.Equal(10, end[5], 6);
    }

    [Fact]
    public void PositionEndTuple_NoMove_ReturnsInputOrigin()
    {
        var end = Sts2TweenEndpointTuples.PositionEndTuple(
            new double[] { 1, 0, 0, 1, 50, 60 }, a: 1, b: 0, c: 0, d: 1, dx: 0, dy: 0);
        Assert.Equal(new double[] { 1, 0, 0, 1, 50, 60 }, end);
    }

    // ---- Stage 2: LOCAL transform reconstruction (scale + rotation + pivot) --------------------------

    [Fact]
    public void LocalTransformTuple_PureScale_ScalesBasisKeepsPosition()
    {
        // rot=0, pivot=0: L = [sx,0,0,sy, posX,posY].
        var l = Sts2TweenEndpointTuples.LocalTransformTuple(posX: 10, posY: 20, rot: 0, sx: 2, sy: 3, pivX: 0, pivY: 0);
        Assert.Equal(new double[] { 2, 0, 0, 3, 10, 20 }, l);
    }

    [Fact]
    public void LocalTransformTuple_PureRotation90_BuildsRotationBasis()
    {
        // 90°: X=(cos,sin)=(0,1), Y=(-sin,cos)=(-1,0); pivot 0, pos 0 → origin 0.
        var l = Sts2TweenEndpointTuples.LocalTransformTuple(
            posX: 0, posY: 0, rot: Math.PI / 2, sx: 1, sy: 1, pivX: 0, pivY: 0);
        Assert.Equal(0, l[0], 6);
        Assert.Equal(1, l[1], 6);
        Assert.Equal(-1, l[2], 6);
        Assert.Equal(0, l[3], 6);
        Assert.Equal(0, l[4], 6);
        Assert.Equal(0, l[5], 6);
    }

    [Fact]
    public void LocalTransformTuple_RotationAboutPivot_ShiftsOrigin()
    {
        // 90° about pivot (100,0): the pivot must map to itself, so the origin absorbs (piv − RotScale·piv).
        // RotScale·(100,0) = (0,100) → origin = (100,0) − (0,100) = (100,−100).
        var l = Sts2TweenEndpointTuples.LocalTransformTuple(
            posX: 0, posY: 0, rot: Math.PI / 2, sx: 1, sy: 1, pivX: 100, pivY: 0);
        Assert.Equal(100, l[4], 6);
        Assert.Equal(-100, l[5], 6);
    }

    [Fact]
    public void Affine2Mul_IdentityLeftFactor_ReturnsRight()
    {
        var identity = new double[] { 1, 0, 0, 1, 0, 0 };
        var b = new double[] { 2, 3, 4, 5, 6, 7 };
        Assert.Equal(b, Sts2TweenEndpointTuples.Affine2Mul(identity, b));
    }

    [Fact]
    public void Affine2Mul_TranslateComposesScale_KeepsScaleAddsTranslation()
    {
        // translate(10,20) · scale(2) = scale-2 basis with origin (10,20) (the scaled origin is 0).
        var translate = new double[] { 1, 0, 0, 1, 10, 20 };
        var scale = new double[] { 2, 0, 0, 2, 0, 0 };
        Assert.Equal(new double[] { 2, 0, 0, 2, 10, 20 }, Sts2TweenEndpointTuples.Affine2Mul(translate, scale));
    }

    [Fact]
    public void Approx_SplitsBasisAndOriginTolerance()
    {
        var a = new double[] { 1, 0, 0, 1, 100, 100 };
        Assert.True(Sts2TweenEndpointTuples.Approx(a, new double[] { 1, 0, 0, 1, 100.4, 100 }, originTol: 0.5, basisTol: 1e-3));
        Assert.False(Sts2TweenEndpointTuples.Approx(a, new double[] { 1, 0, 0, 1, 101, 100 }, originTol: 0.5, basisTol: 1e-3));
        Assert.False(Sts2TweenEndpointTuples.Approx(a, new double[] { 1.01, 0, 0, 1, 100, 100 }, originTol: 0.5, basisTol: 1e-3));
    }

    [Fact]
    public void ReconstructEndGlobal_ScaleUnderScaledParent_ScalesGlobalBasisAboutNodeOrigin()
    {
        // The end-to-end Stage 2 replay math (as TransformEndGlobal composes it, but purely in tuples): a Control at
        // local pos (100,50), scale 1, under a parent that scales 2 and translates (200,100). Tween scales it to 1.5.
        var pp = new double[] { 2, 0, 0, 2, 200, 100 };
        var lNow = Sts2TweenEndpointTuples.LocalTransformTuple(100, 50, rot: 0, sx: 1, sy: 1, pivX: 0, pivY: 0);
        var gNow = Sts2TweenEndpointTuples.Affine2Mul(pp, lNow);
        // Cross-check identity holds (this is what TransformEndGlobal asserts before trusting the endpoint).
        Assert.True(Sts2TweenEndpointTuples.Approx(gNow, new double[] { 2, 0, 0, 2, 400, 200 }, 0.5, 1e-3));

        var lEnd = Sts2TweenEndpointTuples.LocalTransformTuple(100, 50, rot: 0, sx: 1.5, sy: 1.5, pivX: 0, pivY: 0);
        var gEnd = Sts2TweenEndpointTuples.Affine2Mul(pp, lEnd);
        // Global basis scaled by 1.5 (2 → 3); the node origin (pivot at local origin = its position) is unchanged.
        Assert.Equal(new double[] { 3, 0, 0, 3, 400, 200 }, gEnd);
    }

    [Fact]
    public void ReconstructEndGlobal_ParallelPositionAndScale_CombinesBoth()
    {
        // Parallel card tween: position (100,50)→(140,50) AND scale 1→1.25, identity parent. The combined endpoint
        // must reflect BOTH — global basis 1.25 and origin shifted by the position delta.
        var pp = new double[] { 1, 0, 0, 1, 0, 0 };
        var lEnd = Sts2TweenEndpointTuples.LocalTransformTuple(140, 50, rot: 0, sx: 1.25, sy: 1.25, pivX: 0, pivY: 0);
        var gEnd = Sts2TweenEndpointTuples.Affine2Mul(pp, lEnd);
        Assert.Equal(new double[] { 1.25, 0, 0, 1.25, 140, 50 }, gEnd);
    }

    // ---- Local-transform re-basing (Workstream A): parent⁻¹ · global -------------------------------

    private static void AssertTupleApprox(double[] expected, double[] actual, int precision = 6)
    {
        Assert.Equal(6, actual.Length);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i], actual[i], precision);
        }
    }

    [Fact]
    public void Affine2Inverse_ComposedWithOriginal_YieldsIdentity()
    {
        // A general affine (scale + shear + rotation-ish basis + translation); inv·t and t·inv are both identity.
        var t = new double[] { 1.5, 0.5, -0.4, 1.2, 320, 180 };
        var inv = Sts2TweenEndpointTuples.Affine2Inverse(t);
        var identity = new double[] { 1, 0, 0, 1, 0, 0 };
        AssertTupleApprox(identity, Sts2TweenEndpointTuples.Affine2Mul(inv, t));
        AssertTupleApprox(identity, Sts2TweenEndpointTuples.Affine2Mul(t, inv));
    }

    [Fact]
    public void Affine2Inverse_SingularMatrix_ReturnsIdentity()
    {
        // Columns X=(1,0), Y=(2,0) collapse the plane onto a line (det = 1·0 − 0·2 = 0) → no inverse → identity.
        var collapsed = new double[] { 1, 0, 2, 0, 10, 20 };
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0 }, Sts2TweenEndpointTuples.Affine2Inverse(collapsed));
    }

    [Fact]
    public void LocalizeEndTuple_ComposedBackThroughParent_ReproducesEndGlobal()
    {
        // An emitted parent with scale + shear + translation and an arbitrary END global: localizing then
        // re-composing `parent · local` must return the END global (the client reproduces today's globals exactly).
        var parentGlobal = new double[] { 1.5, 0.5, -0.4, 1.2, 320, 180 };
        var gEnd = new double[] { 0.9, 0.1, -0.2, 1.1, 512, 300 };

        var local = Sts2TweenEndpointTuples.LocalizeEndTuple(gEnd, parentGlobal);
        AssertTupleApprox(gEnd, Sts2TweenEndpointTuples.Affine2Mul(parentGlobal, local));
    }

    [Fact]
    public void RebaseLocalTuple_ScaledParent_ProducesParentRelativeLocal_AndRoundTrips()
    {
        // Parent scale 2, translate (100,50); a global child at (260,130) sits at local ((260−100)/2,(130−50)/2)
        // = (80,40) with scale 1 — and re-composing that local through the parent returns the child global.
        var parentGlobal = new double[] { 2, 0, 0, 2, 100, 50 };
        var childGlobal = new double[] { 2, 0, 0, 2, 260, 130 };

        var local = Sts2TweenEndpointTuples.RebaseLocalTuple(parentGlobal, childGlobal);
        Assert.Equal(new double[] { 1, 0, 0, 1, 80, 40 }, local);
        AssertTupleApprox(childGlobal, Sts2TweenEndpointTuples.Affine2Mul(parentGlobal, local));
    }

    [Fact]
    public void RebaseLocalTuple_IdentityParent_ReturnsChildUnchanged()
    {
        // A root re-bases against identity → its local IS its global (client composition is a no-op at the root).
        var child = new double[] { 0.8, 0.2, -0.3, 1.1, 42, 17 };
        AssertTupleApprox(child, Sts2TweenEndpointTuples.RebaseLocalTuple(new double[] { 1, 0, 0, 1, 0, 0 }, child), precision: 9);
    }

    [Fact]
    public void RebaseAndLocalize_SingularParent_ReturnIdentityLocal()
    {
        // A parent scaled to nothing on X (det 0) visually collapses its subtree and has no inverse → identity local.
        var collapsed = new double[] { 0, 0, 0, 1, 200, 100 };
        var child = new double[] { 1, 0, 0, 1, 300, 300 };
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0 }, Sts2TweenEndpointTuples.RebaseLocalTuple(collapsed, child));
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0 }, Sts2TweenEndpointTuples.LocalizeEndTuple(child, collapsed));
    }

    [Fact]
    public void LocalizeEndTuple_ChainBreak_ReBasesAgainstEmittedParentNotActualParentItem()
    {
        // Across a skipped non-CanvasItem (a chain-break), the EMITTED parent's global differs from the child's
        // actual Godot parent-item global. The endpoint must localize against the EMITTED parent: composing the
        // result back through the EMITTED parent reproduces the end global, while composing through the (different)
        // actual parent-item misplaces it — proving we must NOT shortcut via the internal parent-item local.
        var emittedParent = new double[] { 1, 0, 0, 1, 100, 40 };     // e.g. a CanvasLayer-rooted container
        var actualParentItem = new double[] { 2, 0, 0, 2, 100, 40 };  // the skipped-through Godot parent (scaled 2)
        var gEnd = new double[] { 1, 0, 0, 1, 260, 130 };

        var localVsEmitted = Sts2TweenEndpointTuples.LocalizeEndTuple(gEnd, emittedParent);
        AssertTupleApprox(gEnd, Sts2TweenEndpointTuples.Affine2Mul(emittedParent, localVsEmitted));

        // Composing the emitted-parent-relative local through the WRONG (actual) parent misplaces the origin.
        var backThroughActual = Sts2TweenEndpointTuples.Affine2Mul(actualParentItem, localVsEmitted);
        Assert.Equal(420, backThroughActual[4], 6);
        Assert.Equal(220, backThroughActual[5], 6);
    }

#if ENABLE_STS2_LIVE_HOST
    // ---- Stage 4: opacity channel mapping (modulate vs self_modulate) --------------------------------
    // The recorder folds a captured tween's property steps into one TweenTargetChange and picks the opacity
    // channel from the anchor step's PROPERTY string. This needs GodotSharp (a step's raw end value is a Variant),
    // so it compiles/runs only in the live-host build (bridge-live-host-tests) — but NO live game is required:
    // Variant construction + AsSingle/AsColor/AsVector2 are pure managed reads. The Godot-coupled endpoint resolve
    // + streaming-suppression window (ResolveTweenEndpoint) are verified live, not here.

    private static Sts2TweenRecorderHooks.StepRecord PropStep(string property, GVariant toRaw, double durationMs = 100)
        => new()
        {
            Kind = "property",
            Property = property,
            ToRaw = toRaw,
            HasToRaw = true,
            DurationMs = durationMs,
        };

    [Fact]
    public void TryOpacityChange_MapsModulateAndSelfModulateAlpha_RejectsTintAndTransform()
    {
        // modulate channel: alpha sub-prop + whole-color .a.
        Assert.Equal(0.25f, Sts2TweenRecorderHooks.TryOpacityChange(PropStep("modulate:a", 0.25f)));
        Assert.Equal(0.5f, Sts2TweenRecorderHooks.TryOpacityChange(PropStep("modulate", new GColor(1f, 1f, 1f, 0.5f))));
        // self channel: same alpha math (the channel is recovered later from the property string).
        Assert.Equal(0.75f, Sts2TweenRecorderHooks.TryOpacityChange(PropStep("self_modulate:a", 0.75f)));
        Assert.Equal(0.125f, Sts2TweenRecorderHooks.TryOpacityChange(PropStep("self_modulate", new GColor(1f, 1f, 1f, 0.125f))));
        // NOT opacity: a self_modulate tint (r) sub-prop and a transform prop both map to null.
        Assert.Null(Sts2TweenRecorderHooks.TryOpacityChange(PropStep("self_modulate:r", 0.9f)));
        Assert.Null(Sts2TweenRecorderHooks.TryOpacityChange(PropStep("position:x", 10f)));
    }

    [Fact]
    public void CombineTransformChange_RoutesModulateToModulateA_AndSelfModulateToSelfModulateA()
    {
        var mod = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("modulate:a", 0.5f) },
            out var modTransformAnchor, out var modOpacityAnchor);
        Assert.NotNull(mod);
        Assert.Equal(0.5f, mod!.Value.ModulateA);
        Assert.Null(mod.Value.SelfModulateA);
        Assert.Null(modTransformAnchor);
        Assert.NotNull(modOpacityAnchor);

        var self = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("self_modulate", new GColor(1f, 1f, 1f, 0.25f)) },
            out _, out var selfOpacityAnchor);
        Assert.NotNull(self);
        Assert.Equal(0.25f, self!.Value.SelfModulateA);
        Assert.Null(self.Value.ModulateA);
        Assert.Equal("self_modulate", selfOpacityAnchor!.Property);
    }

    [Fact]
    public void CombineTransformChange_ParallelMoveAndSelfFade_HasTransformAnchorAndSelfChannelOpacityAnchor()
    {
        var steps = new List<Sts2TweenRecorderHooks.StepRecord>
        {
            PropStep("position", new GVector2(140f, 50f), durationMs: 200),
            PropStep("self_modulate:a", 0f, durationMs: 150),
        };

        var change = Sts2TweenRecorderHooks.CombineTransformChange(steps, out var transformAnchor, out var opacityAnchor);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasTransform);
        Assert.NotNull(transformAnchor);
        Assert.Equal("position", transformAnchor!.Property);
        Assert.NotNull(opacityAnchor);
        Assert.Equal("self_modulate:a", opacityAnchor!.Property);
        // The fade lands on the SELF channel; the child-cascade modulate channel stays untouched.
        Assert.Equal(0f, change.Value.SelfModulateA);
        Assert.Null(change.Value.ModulateA);
    }

    [Fact]
    public void CombineTransformChange_ScaleOnly_HasTransformButNotHasPosition()
    {
        // A scale-only tween (the targeting arrow head's elastic pop) is a transform change but drives NO
        // translation. The endpoint resolver gates translation pinning on HasPosition, so a scale-only tween must
        // NOT report HasPosition — otherwise its endpoint would bake (and freeze) the node's cursor-tracked origin.
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("scale", new GVector2(1.3f, 1.3f)) },
            out var transformAnchor, out _);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasTransform);
        Assert.False(change.Value.HasPosition);
        Assert.NotNull(transformAnchor);
    }

    [Fact]
    public void CombineTransformChange_RotationOnly_HasTransformButNotHasPosition()
    {
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("rotation", 1.5f) },
            out _, out _);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasTransform);
        Assert.False(change.Value.HasPosition);
    }

    [Fact]
    public void CombineTransformChange_PositionAndScale_HasPosition()
    {
        // A parallel move+scale (card lift) DOES translate → HasPosition true → still pinned/suppressed as before.
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord>
            {
                PropStep("position", new GVector2(140f, 50f)),
                PropStep("scale", new GVector2(1.25f, 1.25f)),
            },
            out _, out _);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasTransform);
        Assert.True(change.Value.HasPosition);
    }

    [Fact]
    public void CombineTransformChange_PositionXOnly_HasPosition()
    {
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("position:x", 10f) },
            out _, out _);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasPosition);
    }

    [Fact]
    public void CombineTransformChange_PureSelfFade_SatisfiesHasAnyWithNoTransform()
    {
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("self_modulate", new GColor(1f, 1f, 1f, 0f), durationMs: 120) },
            out var transformAnchor, out var opacityAnchor);

        Assert.NotNull(change);
        Assert.True(change!.Value.HasAny);
        Assert.False(change.Value.HasTransform);
        Assert.Null(transformAnchor);
        Assert.NotNull(opacityAnchor);
        Assert.Equal(0f, change.Value.SelfModulateA);
    }

    // ---- Fix 2: global-position mapping + declared-start (`.From(...)`) folding ---------------------

    private static Sts2TweenRecorderHooks.StepRecord PropStepFromTo(
        string property, GVariant fromRaw, GVariant toRaw, double durationMs = 100)
        => new()
        {
            Kind = "property",
            Property = property,
            ToRaw = toRaw,
            HasToRaw = true,
            FromRaw = fromRaw,
            HasFromRaw = true,
            DurationMs = durationMs,
        };

    [Fact]
    public void CombineTransformChange_GlobalPosition_MapsToGlobalAxes_AndReportsHasGlobalPosition()
    {
        // Whole-vector global_position.
        var whole = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("global_position", new GVector2(300f, 80f)) },
            out var wholeAnchor, out _);
        Assert.NotNull(whole);
        Assert.Equal(new GVector2(300f, 80f), whole!.Value.GlobalPosition);
        Assert.True(whole.Value.HasGlobalPosition);
        Assert.True(whole.Value.HasPosition);
        Assert.True(whole.Value.HasTransform);
        Assert.Equal("global_position", wholeAnchor!.Property);

        // Per-axis global_position:x (the shared main-menu reticle slide).
        var axis = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("global_position:x", 272f) },
            out _, out _);
        Assert.NotNull(axis);
        Assert.Equal(272f, axis!.Value.GlobalPositionX);
        Assert.True(axis.Value.HasGlobalPosition);
        Assert.True(axis.Value.HasPosition);
    }

    [Fact]
    public void CombineTransformChange_FoldsDeclaredStart_ForOpacityAndGlobalPosition()
    {
        // The shared main-menu focus reticle: it slides `global_position:x` From(num3)→num3-28 AND fades `modulate`
        // From(transparentWhite a=0)→gold(a=1) in parallel. Both declared starts must fold into Start* so the resolver
        // guards the fade against 0 (not the stale live ~1) and the client primes the slide to num3 (not far-left 0).
        const float num3 = 300f;
        var steps = new List<Sts2TweenRecorderHooks.StepRecord>
        {
            PropStepFromTo("global_position:x", num3, num3 - 28f, durationMs: 250),
            PropStepFromTo("modulate", new GColor(1f, 1f, 1f, 0f), new GColor(1f, 0.84f, 0f, 1f), durationMs: 50),
        };

        var change = Sts2TweenRecorderHooks.CombineTransformChange(steps, out var transformAnchor, out var opacityAnchor);

        Assert.NotNull(change);
        // End state.
        Assert.Equal(num3 - 28f, change!.Value.GlobalPositionX);
        Assert.Equal(1f, change.Value.ModulateA);
        // Declared starts folded.
        Assert.Equal(num3, change.Value.StartGlobalPositionX);
        Assert.Equal(0f, change.Value.StartModulateA);
        Assert.Null(change.Value.StartSelfModulateA);
        // Anchors: the longer slide anchors transform; the modulate fade anchors opacity.
        Assert.Equal("global_position:x", transformAnchor!.Property);
        Assert.Equal("modulate", opacityAnchor!.Property);
    }

    [Fact]
    public void CombineTransformChange_SelfFadeStart_LandsOnSelfChannel()
    {
        // A self_modulate fade with a declared start routes the START alpha to the SELF channel (mirroring the end).
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord>
            {
                PropStepFromTo("self_modulate:a", 0f, 1f, durationMs: 120),
            },
            out _, out var opacityAnchor);

        Assert.NotNull(change);
        Assert.Equal(1f, change!.Value.SelfModulateA);
        Assert.Equal(0f, change.Value.StartSelfModulateA);
        Assert.Null(change.Value.StartModulateA);
        Assert.Equal("self_modulate:a", opacityAnchor!.Property);
    }

    [Fact]
    public void CombineTransformChange_NoDeclaredStart_LeavesStartFieldsNull()
    {
        // Without `.From(...)` AND without an implicit sample the Start* fields stay null → client falls back to the
        // committed value (unchanged behavior). PropStep sets neither HasFromRaw nor HasImplicitFromRaw.
        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { PropStep("modulate:a", 1f) },
            out _, out _);

        Assert.NotNull(change);
        Assert.Equal(1f, change!.Value.ModulateA);
        Assert.Null(change.Value.StartModulateA);
        Assert.Null(change.Value.StartSelfModulateA);
        Assert.Null(change.Value.StartGlobalPositionX);
    }

    // ---- WS-ANIM: IMPLICIT start (sampled at tween creation, no `.From(...)`) -----------------------

    // A step carrying ONLY an implicit start (the value GetIndexed sampled at TweenProperty registration time).
    private static Sts2TweenRecorderHooks.StepRecord PropStepImplicit(
        string property, GVariant implicitFromRaw, GVariant toRaw, double durationMs = 100)
        => new()
        {
            Kind = "property",
            Property = property,
            ToRaw = toRaw,
            HasToRaw = true,
            ImplicitFromRaw = implicitFromRaw,
            HasImplicitFromRaw = true,
            DurationMs = durationMs,
        };

    [Fact]
    public void CombineTransformChange_ImplicitStart_FoldsIntoStartForTransformAndOpacity()
    {
        // The endpoint-only chrome case (proceed-button slide + a fade), no `.From(...)`: the implicit pre-tween sample
        // must fold into the Start* channels exactly like a declared start, so the client can prime the replay.
        Assert.True(Sts2SceneWatchRuntimeSettings.TweenImplicitStart); // default ON
        var steps = new List<Sts2TweenRecorderHooks.StepRecord>
        {
            PropStepImplicit("position", new GVector2(1583f, 764f), new GVector2(1983f, 764f), durationMs: 800),
            PropStepImplicit("modulate:a", 0f, 1f, durationMs: 200),
        };

        var change = Sts2TweenRecorderHooks.CombineTransformChange(steps, out var transformAnchor, out var opacityAnchor);

        Assert.NotNull(change);
        // End state.
        Assert.Equal(new GVector2(1983f, 764f), change!.Value.Position);
        Assert.Equal(1f, change.Value.ModulateA);
        // Implicit starts folded (same channels a `.From(...)` would populate).
        Assert.Equal(new GVector2(1583f, 764f), change.Value.StartPosition);
        Assert.Equal(0f, change.Value.StartModulateA);
        Assert.Equal("position", transformAnchor!.Property);
        Assert.Equal("modulate:a", opacityAnchor!.Property);
    }

    [Fact]
    public void CombineTransformChange_ExplicitFromWinsOverImplicit()
    {
        // A step with BOTH an explicit `.From(...)` AND an implicit sample: the DECLARED start must win (the game may
        // hard-prime a re-anchored node, so its declared From is authoritative over the live pre-tween sample).
        var step = new Sts2TweenRecorderHooks.StepRecord
        {
            Kind = "property",
            Property = "position:y",
            ToRaw = 236f,
            HasToRaw = true,
            FromRaw = 336f,          // declared .From(336)
            HasFromRaw = true,
            ImplicitFromRaw = 999f,  // stale live sample — must be ignored
            HasImplicitFromRaw = true,
            DurationMs = 100,
        };

        var change = Sts2TweenRecorderHooks.CombineTransformChange(
            new List<Sts2TweenRecorderHooks.StepRecord> { step }, out _, out _);

        Assert.NotNull(change);
        Assert.Equal(236f, change!.Value.PositionY);
        Assert.Equal(336f, change.Value.StartPositionY); // declared wins, NOT 999
    }

    [Fact]
    public void CombineTransformChange_KillSwitchOff_IgnoresImplicitStart()
    {
        // With the kill-switch OFF the implicit sample is ignored entirely → today's declared-start-only behavior
        // (Start* null unless the game called `.From(...)`). Restore the default in finally (it is a process-wide static).
        var prior = Sts2SceneWatchRuntimeSettings.TweenImplicitStart;
        try
        {
            Sts2SceneWatchRuntimeSettings.TweenImplicitStart = false;
            var change = Sts2TweenRecorderHooks.CombineTransformChange(
                new List<Sts2TweenRecorderHooks.StepRecord>
                {
                    PropStepImplicit("position", new GVector2(1583f, 764f), new GVector2(1983f, 764f)),
                    PropStepImplicit("modulate:a", 0f, 1f),
                },
                out _, out _);

            Assert.NotNull(change);
            // End state still resolves.
            Assert.Equal(new GVector2(1983f, 764f), change!.Value.Position);
            Assert.Equal(1f, change.Value.ModulateA);
            // But NO implicit start folds through with the switch off.
            Assert.Null(change.Value.StartPosition);
            Assert.Null(change.Value.StartModulateA);
        }
        finally
        {
            Sts2SceneWatchRuntimeSettings.TweenImplicitStart = prior;
        }
    }
#endif
}
