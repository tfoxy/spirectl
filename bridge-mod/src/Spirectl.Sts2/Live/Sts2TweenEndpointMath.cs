using Godot;

namespace Spirectl.Sts2.Live;

// The aggregated end-state of one Godot property tween on a node, in the node's LOCAL property space (the values
// the tween's TweenProperty steps drive toward). Only the properties a tween actually touches are set; the rest
// are null. Stage 1 populates Position/PositionX/PositionY; Scale/Rotation/GlobalPosition/ModulateA are wired in
// later stages.
internal readonly struct TweenTargetChange
{
    public Vector2? Position { get; init; }
    public float? PositionX { get; init; }
    public float? PositionY { get; init; }
    public Vector2? Scale { get; init; }
    public float? ScaleX { get; init; }
    public float? ScaleY { get; init; }
    public float? Rotation { get; init; }
    public Vector2? GlobalPosition { get; init; }
    public float? GlobalPositionX { get; init; }
    public float? GlobalPositionY { get; init; }
    public float? ModulateA { get; init; }

    // Stage 4 self-fade: the end alpha of a `self_modulate`(:a) tween. Distinct from ModulateA because
    // self_modulate modulates ONLY the node's own paint (not the child cascade), so the client fans it
    // differently — but the producer endpoint math is identical (map to an end alpha, guard vs the live value).
    // At most one of ModulateA / SelfModulateA is set per resolve (the opacity anchor's channel wins).
    public float? SelfModulateA { get; init; }

    // Fix 2: the tween's DECLARED start (`.From(...)`), folded per component. Only set for axes the game explicitly
    // primed with `.From(...)`. Used to (a) let the client PRIME the element to the real start (a re-anchored node's
    // live value is a stale transient), and (b) make the opacity guard compare against the declared start alpha
    // rather than the stale live alpha (so a `From(0)→1` reveal on a node still latched at 1 is not rejected).
    public Vector2? StartPosition { get; init; }
    public float? StartPositionX { get; init; }
    public float? StartPositionY { get; init; }
    public Vector2? StartScale { get; init; }
    public float? StartScaleX { get; init; }
    public float? StartScaleY { get; init; }
    public float? StartRotation { get; init; }
    public Vector2? StartGlobalPosition { get; init; }
    public float? StartGlobalPositionX { get; init; }
    public float? StartGlobalPositionY { get; init; }
    public float? StartModulateA { get; init; }
    public float? StartSelfModulateA { get; init; }

    public bool HasTransform =>
        Position.HasValue || PositionX.HasValue || PositionY.HasValue
        || Scale.HasValue || ScaleX.HasValue || ScaleY.HasValue
        || Rotation.HasValue
        || GlobalPosition.HasValue || GlobalPositionX.HasValue || GlobalPositionY.HasValue;

    // Whether the tween drives TRANSLATION specifically (as opposed to scale/rotation only). A transform endpoint
    // bakes the node's CURRENT global origin into the pinned 6-tuple; for a node whose position is ALSO driven
    // per-frame by game code (e.g. the targeting arrow head tracking the cursor), pinning a scale-only tween would
    // freeze that live translation. So translation is only pinned/suppressed when the tween itself moves it.
    public bool HasPosition =>
        Position.HasValue || PositionX.HasValue || PositionY.HasValue
        || GlobalPosition.HasValue || GlobalPositionX.HasValue || GlobalPositionY.HasValue;

    // Whether the tween moves the node in GLOBAL space (`global_position(:x/:y)`). Such a tween resolves via a
    // dedicated global-origin path (the value is already in global space); a mix of local + global position on one
    // node doesn't occur in practice, so the global path takes precedence when this is true.
    public bool HasGlobalPosition =>
        GlobalPosition.HasValue || GlobalPositionX.HasValue || GlobalPositionY.HasValue;

    public bool HasAny => HasTransform || ModulateA.HasValue || SelfModulateA.HasValue;
}

// The resolved END state a tween reaches, in the scene watcher's STREAMED space. `Transform` is the target node's
// end GLOBAL Transform2D as a 6-tuple [a,b,c,d,tx,ty] (the client rigidly propagates it across the subtree);
// `Opacity` is the end modulate.a. Either may be null.
internal readonly struct TweenEndpoint
{
    public double[]? Transform { get; init; }
    public double? Opacity { get; init; }

    // Fix 2: the tween's declared START, in the same streamed spaces as Transform/Opacity. Non-null only when the
    // game primed the tween with `.From(...)`; the client sets the element to this (transition-less) before starting
    // the transition to the endpoint, so a re-anchored/primed node replays from its real start (no 1-frame transient).
    public double[]? StartTransform { get; init; }
    public double? StartOpacity { get; init; }
}

// Pure Transform2D endpoint math, unit-testable with primitive inputs (no Godot Node access). The scene watcher
// streams each node's global as `G = viewportPrefix * GetGlobalTransformWithCanvas()`; endpoints are produced in
// that same space so they line up with streamed transforms.
internal static class Sts2TweenEndpointMath
{
    // Serialize a Transform2D the way the watcher serializes streamed transforms: [a,b,c,d,tx,ty] =
    // (X.X, X.Y, Y.X, Y.Y, Origin.X, Origin.Y).
    public static double[] ToTuple(Transform2D t) =>
        new double[] { t.X.X, t.X.Y, t.Y.X, t.Y.Y, t.Origin.X, t.Origin.Y };

    // The END global for a POSITION change: keep the current global basis, shift the origin by the local-position
    // delta mapped through the PARENT's screen-space basis. Derivation: G' = Pg·L' differs from G = Pg·L only in
    // origin by Pg.Basis·(pos' − pos0); left-multiplying by the viewport prefix folds that into
    // (vp·Pg).Basis·Δ = Pp.Basis·Δ. So `gEnd = gNow` with `Origin += Pp.BasisXform(pos' − pos0)`, exact for a pure
    // position tween. `gNow` = the node's streamed global; `pp` = viewportPrefix·parent.GetGlobalTransformWithCanvas()
    // (its Origin is unused — BasisXform applies only the linear part); `pos0`/`posNew` = local position now/target.
    // Returns the end global as a streamed-space 6-tuple.
    public static double[] PositionEndGlobal(Transform2D gNow, Transform2D pp, Vector2 pos0, Vector2 posNew)
    {
        var d = posNew - pos0;
        // Godot Transform2D basis columns: X = (a, b), Y = (c, d); BasisXform(v) = X·v.x + Y·v.y.
        return Sts2TweenEndpointTuples.PositionEndTuple(ToTuple(gNow), pp.X.X, pp.X.Y, pp.Y.X, pp.Y.Y, d.X, d.Y);
    }

    // Stage 2: the END global for a COMBINED transform tween (any of position/scale/rotation, possibly parallel).
    // Reconstruct the end LOCAL transform from the node's current components with the tweened ones overridden, then
    // map to global via the parent: `G_end = Pp · L_end`. `gNow`/`pp` are the node's and its parent's streamed
    // globals; `pos0`/`rot0`/`scale0`/`pivot` are the node's CURRENT live components (pivot = Control.PivotOffset,
    // else zero). Returns the end global as a streamed-space 6-tuple, or null if the safety cross-check fails
    // (`Pp · L_now` must reproduce `gNow`; a mismatch means an unsupported node — Node2D skew / exotic type — so we
    // skip the endpoint and let it stream). Only pure math + reads; never mutates the node.
    public static double[]? TransformEndGlobal(
        Transform2D gNow,
        Transform2D pp,
        Vector2 pos0,
        float rot0,
        Vector2 scale0,
        Vector2 pivot,
        TweenTargetChange change)
    {
        var posNew = change.Position
            ?? new Vector2(change.PositionX ?? pos0.X, change.PositionY ?? pos0.Y);
        var scaleNew = change.Scale
            ?? new Vector2(change.ScaleX ?? scale0.X, change.ScaleY ?? scale0.Y);
        var rotNew = change.Rotation ?? rot0;

        var ppTuple = ToTuple(pp);

        // Safety cross-check: the reconstructed CURRENT local, re-composed through the parent, must reproduce the
        // streamed global (an identity when the assembly matches Godot for this node).
        var lNow = Sts2TweenEndpointTuples.LocalTransformTuple(
            pos0.X, pos0.Y, rot0, scale0.X, scale0.Y, pivot.X, pivot.Y);
        var gCheck = Sts2TweenEndpointTuples.Affine2Mul(ppTuple, lNow);
        if (!Sts2TweenEndpointTuples.Approx(gCheck, ToTuple(gNow), originTol: 0.5, basisTol: 1e-3))
        {
            return null;
        }

        var lEnd = Sts2TweenEndpointTuples.LocalTransformTuple(
            posNew.X, posNew.Y, rotNew, scaleNew.X, scaleNew.Y, pivot.X, pivot.Y);
        return Sts2TweenEndpointTuples.Affine2Mul(ppTuple, lEnd);
    }
}
