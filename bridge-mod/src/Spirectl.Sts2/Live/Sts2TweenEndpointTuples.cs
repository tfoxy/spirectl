namespace Spirectl.Sts2.Live;

// Godot-free tuple math for Part C tween-endpoint resolution — kept in its own class (no Godot-typed members) so
// it is unit-testable from an assembly that references GodotSharp only under an extern alias. The Godot-typed
// Sts2TweenEndpointMath delegates here.
public static class Sts2TweenEndpointTuples
{
    // Shift a streamed 6-tuple's origin by a local delta (dx, dy) mapped through a parent basis (a, b, c, d):
    // BasisXform(v) = (a·vx + c·vy, b·vx + d·vy). The basis (indices 0..3) is unchanged; only the origin (indices
    // 4, 5) moves. This is the core of a pure POSITION tween's end-global, in the watcher's streamed space.
    public static double[] PositionEndTuple(double[] gNow, double a, double b, double c, double d, double dx, double dy)
    {
        var shiftX = a * dx + c * dy;
        var shiftY = b * dx + d * dy;
        return new[] { gNow[0], gNow[1], gNow[2], gNow[3], gNow[4] + shiftX, gNow[5] + shiftY };
    }

    // Stage 2: the LOCAL transform of a Control/Node2D as a 6-tuple, from its position/rotation/scale/pivot, using
    // Godot's exact assembly `L = Translate(pos) · Translate(piv) · RotScale(rot,scale) · Translate(-piv)` where
    // `RotScale = Transform2D(rot,0).Scaled(scale)` ⇒ X=(cos·sx, sin·sx), Y=(−sin·sy, cos·sy). A Node2D is the
    // piv=(0,0) case (skew ignored — cards are Controls with no skew). Combining ALL of a tween's transform steps
    // into one end LOCAL transform (then mapping through the parent) is how parallel position+scale+rotation card
    // tweens replay as a single rigid motion.
    public static double[] LocalTransformTuple(
        double posX, double posY, double rot, double sx, double sy, double pivX, double pivY)
    {
        var cos = Math.Cos(rot);
        var sin = Math.Sin(rot);
        var a = cos * sx;
        var b = sin * sx;
        var c = -sin * sy;
        var d = cos * sy;
        // internal = Translate(piv)·RotScale·Translate(-piv): basis unchanged, origin = piv − RotScale·piv; then
        // the outer Translate(pos) adds pos to the origin.
        var tx = posX + pivX - (a * pivX + c * pivY);
        var ty = posY + pivY - (b * pivX + d * pivY);
        return new[] { a, b, c, d, tx, ty };
    }

    // Compose two 2D affines given as 6-tuples [a,b,c,d,tx,ty] (columns X=(a,b), Y=(c,d), origin=(tx,ty)):
    // (A·B) applies B first, then A — the same order as Godot's Transform2D `*`.
    public static double[] Affine2Mul(double[] a, double[] b)
    {
        return new[]
        {
            a[0] * b[0] + a[2] * b[1],
            a[1] * b[0] + a[3] * b[1],
            a[0] * b[2] + a[2] * b[3],
            a[1] * b[2] + a[3] * b[3],
            a[0] * b[4] + a[2] * b[5] + a[4],
            a[1] * b[4] + a[3] * b[5] + a[5],
        };
    }

    // A 6-tuple's basis determinant (columns X=(a,b), Y=(c,d)): a·d − b·c. Zero ⇒ the affine collapses the plane
    // (a scaled-to-nothing / degenerate parent) and has no inverse; the localize helpers below treat |det| at or
    // below this epsilon as singular. Kept slightly above exact-zero (unlike the watcher's raw `!= 0f` global-space
    // guard) because these tuples are float-derived doubles, so a truly collapsed parent can round to a tiny non-zero.
    private const double SingularDeterminantEpsilon = 1e-9;

    public static double Affine2Determinant(double[] t) => t[0] * t[3] - t[1] * t[2];

    // Inverse of a 2D affine 6-tuple [a,b,c,d,tx,ty] (columns X=(a,b), Y=(c,d), origin=(tx,ty)) — matches Godot's
    // Transform2D.AffineInverse(): swap-and-scale the 2×2 basis by 1/det, then re-map the negated origin through the
    // new basis. Singular (|det| ≤ epsilon) → identity (no inverse exists; callers that need identity-LOCAL for a
    // collapsed parent handle that themselves — see RebaseLocalTuple).
    public static double[] Affine2Inverse(double[] t)
    {
        var det = Affine2Determinant(t);
        if (Math.Abs(det) <= SingularDeterminantEpsilon)
        {
            return new double[] { 1, 0, 0, 1, 0, 0 };
        }

        var idet = 1.0 / det;
        // New basis columns (Godot: SWAP(X.x, Y.y) then X *= (idet,-idet), Y *= (-idet, idet)).
        var ia = t[3] * idet;   // X'.x =  d/det
        var ib = -t[1] * idet;  // X'.y = -b/det
        var ic = -t[2] * idet;  // Y'.x = -c/det
        var id = t[0] * idet;   // Y'.y =  a/det
        // origin' = newBasis · (-origin): BasisXform(v) = X'·v.x + Y'·v.y.
        var ox = -(ia * t[4] + ic * t[5]);
        var oy = -(ib * t[4] + id * t[5]);
        return new[] { ia, ib, ic, id, ox, oy };
    }

    // Re-base a child's streamed GLOBAL against its emitted parent's streamed global: L = parentGlobal⁻¹ · childGlobal,
    // the parent-relative transform the local-space mirror emits. The client recomposes `parentGlobal · L` down the
    // emitted parent chain to reproduce today's global exactly. A SINGULAR parent (|det| ≤ epsilon, i.e. visually
    // collapsed) has no inverse → return identity local; the collapsed parent already zeroes the subtree on screen,
    // so the child's own local is moot. Identity parent ⇒ L == childGlobal (roots keep their global unchanged).
    public static double[] RebaseLocalTuple(double[] parentGlobal, double[] childGlobal)
    {
        if (Math.Abs(Affine2Determinant(parentGlobal)) <= SingularDeterminantEpsilon)
        {
            return new double[] { 1, 0, 0, 1, 0, 0 };
        }

        return Affine2Mul(Affine2Inverse(parentGlobal), childGlobal);
    }

    // Localize a tween's END global against the emitted parent's streamed global — identical math to RebaseLocalTuple
    // (L = parentGlobal⁻¹ · gEnd), argument order (end, parent) matching the endpoint-resolution call site. So a
    // tween hint's End/Start transform lands in the SAME parent-relative space as the node's streamed Transform when
    // local mode is on, and the client composes both down the emitted parent chain identically.
    public static double[] LocalizeEndTuple(double[] gEnd, double[] parentGlobal) => RebaseLocalTuple(parentGlobal, gEnd);

    // Element-wise closeness of two 6-tuples, split into a tolerance for the linear basis (indices 0..3) and the
    // origin (indices 4,5). Used as a drop-to-stream safety cross-check: reconstructing the CURRENT local transform
    // and re-composing through the parent must reproduce the streamed global (it's an identity when the assembly
    // matches Godot for this node), so a mismatch flags an unsupported node (skew/exotic) → skip the endpoint.
    public static bool Approx(double[] a, double[] b, double originTol, double basisTol)
    {
        return Math.Abs(a[0] - b[0]) <= basisTol
            && Math.Abs(a[1] - b[1]) <= basisTol
            && Math.Abs(a[2] - b[2]) <= basisTol
            && Math.Abs(a[3] - b[3]) <= basisTol
            && Math.Abs(a[4] - b[4]) <= originTol
            && Math.Abs(a[5] - b[5]) <= originTol;
    }
}
