namespace Spirectl.Sts2.Live;

// Godot-free RID arithmetic and affine-fit primitives retained by the reusable geoclip producer.
//   * RID arithmetic. A Godot RID id packs a 32-bit VALIDATOR in the high word and a per-owner slot INDEX in
//     the low word. The validator comes from one process-global monotonically increasing counter shared by
//     every RID owner, so two RIDs created around an event BRACKET every RID created in between; the index is
//     a per-owner slot number that grows roughly monotonically. That is enough to enumerate a bounded
//     candidate set for a RID we cannot read directly — offline, where the probe can stand on both sides of
//     the skeleton build (recipe A), and reusable geoclip acquisition.
//   * Least-squares 2D affine fit + residual. The rigidity question: does a slot's mesh move as one rigid
//     piece between two poses (so a client could replay it with a transform) or does it genuinely deform
//     (so the vertices themselves must be streamed)?
internal static class Sts2SpineGeometryMath
{
    // A Godot RID id split into its two halves.
    internal readonly record struct RidParts(uint Validator, uint Index);

    internal static RidParts DecodeRid(ulong id)
        => new((uint)(id >> 32), (uint)(id & 0xFFFF_FFFFuL));

    internal static ulong ComposeRid(uint validator, uint index)
        => ((ulong)validator << 32) | index;

    // The bracketed candidate space between two RIDs taken before/after the event that created the RID we are
    // hunting. Validators are global and monotonic, so the target's validator is inside [low, high]; indices are
    // per-owner and only ROUGHLY ordered (a freed slot is reused), hence the symmetric slack. `Truncated` says
    // the full cross product exceeded the cap and the enumeration stops early — the caller reports that rather
    // than silently probing a subset.
    internal sealed record RidCandidatePlan(
        uint ValidatorLow,
        uint ValidatorHigh,
        uint IndexLow,
        uint IndexHigh,
        long TotalCandidates,
        int Cap,
        bool Truncated,
        long EmittedCount);

    internal static RidCandidatePlan PlanRidCandidates(ulong lowId, ulong highId, int indexSlack, int cap)
    {
        var a = DecodeRid(lowId);
        var b = DecodeRid(highId);
        var slack = (uint)Math.Max(0, indexSlack);

        var rawIndexLow = Math.Min(a.Index, b.Index);
        var rawIndexHigh = Math.Max(a.Index, b.Index);

        return BuildPlan(
            Math.Min(a.Validator, b.Validator),
            Math.Max(a.Validator, b.Validator),
            rawIndexLow > slack ? rawIndexLow - slack : 0u,
            rawIndexHigh > uint.MaxValue - slack ? uint.MaxValue : rawIndexHigh + slack,
            cap);
    }

    private static RidCandidatePlan BuildPlan(
        uint validatorLow,
        uint validatorHigh,
        uint indexLow,
        uint indexHigh,
        int cap)
    {
        var effectiveCap = Math.Max(0, cap);
        var validatorSpan = (long)validatorHigh - validatorLow + 1;
        var indexSpan = (long)indexHigh - indexLow + 1;
        var total = validatorSpan * indexSpan;
        var emitted = Math.Min(total, effectiveCap);

        return new RidCandidatePlan(
            validatorLow,
            validatorHigh,
            indexLow,
            indexHigh,
            total,
            effectiveCap,
            Truncated: total > effectiveCap,
            EmittedCount: emitted);
    }

    // Validator-major enumeration of a plan's candidate ids, stopping at the plan's emitted count. Lazy so a
    // caller can bail out as soon as it has found every RID it was looking for.
    internal static IEnumerable<ulong> EnumerateRidCandidates(RidCandidatePlan plan)
    {
        long emitted = 0;
        for (var validator = plan.ValidatorLow; validator <= plan.ValidatorHigh; validator += 1)
        {
            for (var index = plan.IndexLow; index <= plan.IndexHigh; index += 1)
            {
                if (emitted >= plan.EmittedCount)
                {
                    yield break;
                }

                emitted += 1;
                yield return ComposeRid(validator, index);

                if (index == uint.MaxValue)
                {
                    break;
                }
            }

            if (validator == uint.MaxValue)
            {
                yield break;
            }
        }
    }

    // Shared stride defaults and walk/choice primitives used by reusable geoclip candidate acquisition.
    // Callers may measure and choose a different stride at runtime.
    internal const uint DefaultLiveValidatorStride = 5;

    internal const uint DefaultLiveIndexStride = 1;

    /// <summary>A candidate RID as the walk thinks of it: a point on the (validator, index) lattice.</summary>
    internal readonly record struct RidPoint(uint Validator, uint Index)
    {
        internal ulong Id => ComposeRid(Validator, Index);
    }

    /// <summary>The measured progression, and whether it was measured, defaulted or dictated.</summary>
    internal sealed record StrideChoice(
        uint ValidatorStride,
        uint IndexStride,
        string Source,
        IReadOnlyList<uint> ConfirmedDeltas,
        IReadOnlyList<uint> DoubleConfirmedDeltas);

    // Given the validator deltas at which (anchor.validator + d, anchor.index + 1) validated, and those at
    // which (anchor.validator + 2d, anchor.index + 2) also validated, pick the stride. A single neighbour is
    // not evidence of a progression — any two members of ANY sequence are collinear — so the smallest delta
    // that produces an arithmetic progression of THREE points wins. Nothing double-confirmed ⇒ the caller's
    // fallback, flagged as such, because a defaulted stride that walks nothing is a much better outcome than
    // a guessed one that walks into a neighbouring rig.
    internal static StrideChoice ChooseValidatorStride(
        IReadOnlyCollection<uint> singleStepDeltas,
        IReadOnlyCollection<uint> doubleStepDeltas,
        uint fallback)
    {
        var singles = singleStepDeltas.Where(delta => delta > 0).Distinct().OrderBy(delta => delta).ToArray();
        var doubles = doubleStepDeltas.Where(delta => delta > 0).Distinct().OrderBy(delta => delta).ToArray();
        foreach (var delta in singles)
        {
            if (doubles.Contains(delta))
            {
                return new StrideChoice(delta, DefaultLiveIndexStride, "measured", singles, doubles);
            }
        }

        return new StrideChoice(Math.Max(1u, fallback), DefaultLiveIndexStride, "default", singles, doubles);
    }

    /// <summary>Where the walk is allowed to go, each bound carrying the stop reason it produces.</summary>
    internal sealed record StrideWalkLimits(
        uint ValidatorFloor,
        uint ValidatorCeiling,
        uint IndexLow,
        uint IndexHigh,
        int MaxRunLength,
        int MaxConsecutiveMisses);

    /// <summary>
    /// What the walk recovered and why it stopped in each direction. `GapSizes` counts only BRIDGED holes —
    /// misses that a later hit proved were holes rather than the end of the run — so a run reported with an
    /// empty gap list is a contiguous one.
    /// </summary>
    internal sealed record StrideWalk(
        IReadOnlyList<ulong> Found,
        long Probed,
        string StopReasonUp,
        string StopReasonDown,
        IReadOnlyList<int> GapSizes);

    // Step off the anchor in both directions along (+validatorStride, +indexStride). The anchor itself is
    // taken as already validated (the scan validated it) and is not re-probed. `validate` is injected so the
    // whole walk is testable without an engine; live it is a RenderingServer round trip per call, which is
    // why the miss budget is small.
    internal static StrideWalk WalkStride(
        RidPoint anchor,
        uint validatorStride,
        uint indexStride,
        Func<ulong, bool> validate,
        StrideWalkLimits limits)
    {
        var vStep = Math.Max(1u, validatorStride);
        var iStep = indexStride;
        var maxRun = Math.Max(1, limits.MaxRunLength);
        var missBudget = Math.Max(1, limits.MaxConsecutiveMisses);
        var gaps = new List<int>();
        long probed = 0;

        var up = new List<ulong>();
        var down = new List<ulong>();
        var found = 1;

        string stopUp = Walk(
            anchor,
            ascending: true,
            up);
        string stopDown = Walk(
            anchor,
            ascending: false,
            down);

        down.Reverse();
        var ordered = new List<ulong>(down.Count + 1 + up.Count);
        ordered.AddRange(down);
        ordered.Add(anchor.Id);
        ordered.AddRange(up);
        return new StrideWalk(ordered, probed, stopUp, stopDown, gaps);

        string Walk(RidPoint from, bool ascending, List<ulong> into)
        {
            var point = from;
            var misses = 0;
            var pendingGap = 0;
            while (true)
            {
                if (found >= maxRun)
                {
                    return "max-run-length";
                }

                uint nextValidator;
                uint nextIndex;
                if (ascending)
                {
                    // Every bound is tested as a SUBTRACTION on the limit rather than an addition on the
                    // point: the point plus a stride can overflow a uint and wrap back into the window.
                    if (limits.ValidatorCeiling < vStep || point.Validator > limits.ValidatorCeiling - vStep)
                    {
                        return "validator-ceiling";
                    }

                    nextValidator = point.Validator + vStep;
                    if (limits.IndexHigh < iStep || point.Index > limits.IndexHigh - iStep)
                    {
                        return "index-high";
                    }

                    nextIndex = point.Index + iStep;
                }
                else
                {
                    if (point.Validator < limits.ValidatorFloor + vStep)
                    {
                        return "validator-floor";
                    }

                    nextValidator = point.Validator - vStep;
                    if (point.Index < limits.IndexLow + iStep)
                    {
                        return "index-low";
                    }

                    nextIndex = point.Index - iStep;
                }

                point = new RidPoint(nextValidator, nextIndex);
                probed += 1;
                if (validate(point.Id))
                {
                    into.Add(point.Id);
                    found += 1;
                    if (pendingGap > 0)
                    {
                        gaps.Add(pendingGap);
                        pendingGap = 0;
                    }

                    misses = 0;
                    continue;
                }

                misses += 1;
                pendingGap += 1;
                if (misses >= missBudget)
                {
                    return "consecutive-misses";
                }
            }
        }
    }

    // ── Least-squares 2D affine fit ───────────────────────────────────────────────────────────────────
    //
    // Fit u = A·x + B·y + C, v = D·x + E·y + F over all point pairs and report how badly the best affine map
    // explains the motion. `Degenerate` means the normal equations were singular (fewer than three points, or
    // all source points collinear) and the fit fell back to a translation-only model, so the residual is a
    // weaker statement.
    internal sealed record AffineFit(
        double A,
        double B,
        double C,
        double D,
        double E,
        double F,
        double MaxResidual,
        double RmsResidual,
        bool Degenerate);

    internal static AffineFit FitAffine2D(
        IReadOnlyList<double> sourceX,
        IReadOnlyList<double> sourceY,
        IReadOnlyList<double> targetX,
        IReadOnlyList<double> targetY)
    {
        var count = Math.Min(Math.Min(sourceX.Count, sourceY.Count), Math.Min(targetX.Count, targetY.Count));
        if (count == 0)
        {
            return new AffineFit(1, 0, 0, 0, 1, 0, 0, 0, Degenerate: true);
        }

        // Symmetric 3x3 normal matrix S = Σ [x,y,1]ᵀ[x,y,1] with two right-hand sides (one per output axis).
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, s1 = 0;
        double bux = 0, buy = 0, bu = 0, bvx = 0, bvy = 0, bv = 0;
        for (var i = 0; i < count; i += 1)
        {
            double x = sourceX[i], y = sourceY[i], u = targetX[i], v = targetY[i];
            sxx += x * x;
            sxy += x * y;
            sx += x;
            syy += y * y;
            sy += y;
            s1 += 1;
            bux += x * u;
            buy += y * u;
            bu += u;
            bvx += x * v;
            bvy += y * v;
            bv += v;
        }

        var matrix = new[,]
        {
            { sxx, sxy, sx },
            { sxy, syy, sy },
            { sx, sy, s1 },
        };

        var degenerate = !TrySolve3x3(
            matrix,
            [bux, buy, bu],
            [bvx, bvy, bv],
            out var u3,
            out var v3);

        if (degenerate)
        {
            // Translation-only fallback: the mean offset. Keeps the residual meaningful for a 1- or 2-point
            // slot (a Spine region attachment has 4 vertices, so this is rare, but a clipping/point attachment
            // can be smaller).
            double meanDx = 0, meanDy = 0;
            for (var i = 0; i < count; i += 1)
            {
                meanDx += targetX[i] - sourceX[i];
                meanDy += targetY[i] - sourceY[i];
            }

            meanDx /= count;
            meanDy /= count;
            u3 = [1, 0, meanDx];
            v3 = [0, 1, meanDy];
        }

        double maxResidual = 0;
        double sumSquares = 0;
        for (var i = 0; i < count; i += 1)
        {
            double x = sourceX[i], y = sourceY[i];
            var predictedU = (u3[0] * x) + (u3[1] * y) + u3[2];
            var predictedV = (v3[0] * x) + (v3[1] * y) + v3[2];
            var dx = predictedU - targetX[i];
            var dy = predictedV - targetY[i];
            var residual = Math.Sqrt((dx * dx) + (dy * dy));
            maxResidual = Math.Max(maxResidual, residual);
            sumSquares += (dx * dx) + (dy * dy);
        }

        return new AffineFit(
            u3[0],
            u3[1],
            u3[2],
            v3[0],
            v3[1],
            v3[2],
            maxResidual,
            Math.Sqrt(sumSquares / count),
            degenerate);
    }

    // Gaussian elimination with partial pivoting over a 3x3 with two right-hand sides. False when the matrix is
    // singular to working precision, which for the normal equations above means the source points do not span
    // the plane.
    private static bool TrySolve3x3(
        double[,] matrix,
        double[] rhsU,
        double[] rhsV,
        out double[] solutionU,
        out double[] solutionV)
    {
        solutionU = [0, 0, 0];
        solutionV = [0, 0, 0];

        var m = (double[,])matrix.Clone();
        var u = (double[])rhsU.Clone();
        var v = (double[])rhsV.Clone();

        // Scale the singularity test to the matrix magnitude: the normal equations of pixel-space coordinates
        // carry entries in the 1e6..1e10 range, so a fixed absolute epsilon would call every real fit singular.
        double magnitude = 0;
        for (var r = 0; r < 3; r += 1)
        {
            for (var c = 0; c < 3; c += 1)
            {
                magnitude = Math.Max(magnitude, Math.Abs(m[r, c]));
            }
        }

        var epsilon = Math.Max(1e-12, magnitude * 1e-12);

        for (var col = 0; col < 3; col += 1)
        {
            var pivotRow = col;
            for (var row = col + 1; row < 3; row += 1)
            {
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivotRow, col]))
                {
                    pivotRow = row;
                }
            }

            if (Math.Abs(m[pivotRow, col]) <= epsilon)
            {
                return false;
            }

            if (pivotRow != col)
            {
                for (var c = 0; c < 3; c += 1)
                {
                    (m[col, c], m[pivotRow, c]) = (m[pivotRow, c], m[col, c]);
                }

                (u[col], u[pivotRow]) = (u[pivotRow], u[col]);
                (v[col], v[pivotRow]) = (v[pivotRow], v[col]);
            }

            for (var row = col + 1; row < 3; row += 1)
            {
                var factor = m[row, col] / m[col, col];
                if (factor == 0)
                {
                    continue;
                }

                for (var c = col; c < 3; c += 1)
                {
                    m[row, c] -= factor * m[col, c];
                }

                u[row] -= factor * u[col];
                v[row] -= factor * v[col];
            }
        }

        for (var row = 2; row >= 0; row -= 1)
        {
            var accU = u[row];
            var accV = v[row];
            for (var c = row + 1; c < 3; c += 1)
            {
                accU -= m[row, c] * solutionU[c];
                accV -= m[row, c] * solutionV[c];
            }

            solutionU[row] = accU / m[row, row];
            solutionV[row] = accV / m[row, row];
        }

        return true;
    }

    // Diagonal of the axis-aligned bounding box of a point set — the scale the residual is normalized against,
    // so "rigid" means the same thing for a 30px hand and a 900px torso.
    internal static double BoundingBoxDiagonal(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var count = Math.Min(xs.Count, ys.Count);
        if (count == 0)
        {
            return 0;
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (var i = 0; i < count; i += 1)
        {
            minX = Math.Min(minX, xs[i]);
            maxX = Math.Max(maxX, xs[i]);
            minY = Math.Min(minY, ys[i]);
            maxY = Math.Max(maxY, ys[i]);
        }

        var width = maxX - minX;
        var height = maxY - minY;
        return Math.Sqrt((width * width) + (height * height));
    }

    internal const double DefaultRigidThreshold = 0.01;

    internal readonly record struct RigidityVerdict(double NormalizedResidual, bool IsRigid);

    // Residual as a fraction of the slot's own size. A zero-size slot (degenerate bbox) is reported as rigid
    // only when its residual is zero too, so a collapsed attachment cannot masquerade as a clean rigid fit.
    internal static RigidityVerdict ClassifyRigidity(double maxResidual, double diagonal, double threshold)
    {
        if (diagonal <= 0)
        {
            return new RigidityVerdict(maxResidual <= 0 ? 0 : double.PositiveInfinity, maxResidual <= 0);
        }

        var normalized = maxResidual / diagonal;
        return new RigidityVerdict(normalized, normalized < threshold);
    }
}

// One parsed page of a text Spine atlas.
