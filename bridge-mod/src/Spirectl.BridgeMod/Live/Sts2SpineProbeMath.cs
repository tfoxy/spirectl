using System.Globalization;

namespace Spirectl.Sts2.Live;

/// <summary>Godot-free planning and validation used only by the standalone Spine geometry probe.</summary>
internal static class Sts2SpineProbeMath
{
    // ── Recipe D: the LIVE candidate window ──────────────────────────────────────────────────────────
    //
    // On a live host nothing brackets the skeleton build: it happened before the probe was looking, so the
    // offline trick (own meshes on both sides of the event) is unavailable by construction. What IS readable
    // afterwards is each mesh node's CANVAS-ITEM rid — public API — and those were minted while the same
    // skeleton was being built, out of the same process-global validator counter. Measured offline, a
    // skeleton's mesh RIDs begin a few dozen validators PAST the highest canvas-item validator among its own
    // mesh children, so the canvas items pin the window's LOWER edge. The UPPER edge is a mesh the probe
    // creates itself, right now: its validator is above everything allocated so far, and its per-owner index
    // sits at (or near) the mesh owner's high-water mark, which is what bounds the index axis.
    //
    // Enumeration is validator-major ASCENDING from the canvas-item edge, so the neighbourhood the meshes
    // actually live in is swept FIRST. That is why truncation is tolerable here in a way it is not for the
    // offline bracket: a cap only discards the far end of the window, where the skeleton's meshes are known
    // not to be. `ValidatorsCovered` reports how far past the canvas-item edge the cap actually reached, and
    // `RecommendedCap` what it would take to reach `RecommendedValidatorDepth` — a sweep too shallow to clear
    // the measured offset must say so rather than report an honest-looking zero.
    internal const uint MeasuredMeshValidatorOffset = 28;

    internal const uint RecommendedValidatorDepth = 128;

    internal sealed record CanvasBracketPlan(
        bool Viable,
        string Reason,
        uint CanvasValidatorLow,
        uint CanvasValidatorHigh,
        uint ProbeValidator,
        uint ProbeIndex,
        long IndexSpan,
        long ValidatorsCovered,
        long RecommendedCap,
        Sts2SpineGeometryMath.RidCandidatePlan Candidates);

    internal static CanvasBracketPlan PlanCanvasItemBracket(
        IReadOnlyCollection<ulong> canvasItemRidIds,
        IReadOnlyCollection<ulong> probeMeshRidIds,
        int indexSlack,
        int cap)
    {
        var empty = new Sts2SpineGeometryMath.RidCandidatePlan(0, 0, 0, 0, 0, Math.Max(0, cap), Truncated: false, EmittedCount: 0);

        uint canvasLow = uint.MaxValue;
        uint canvasHigh = 0;
        foreach (var id in canvasItemRidIds)
        {
            var parts = Sts2SpineGeometryMath.DecodeRid(id);
            canvasLow = Math.Min(canvasLow, parts.Validator);
            canvasHigh = Math.Max(canvasHigh, parts.Validator);
        }

        uint probeValidator = 0;
        uint probeIndex = 0;
        foreach (var id in probeMeshRidIds)
        {
            var parts = Sts2SpineGeometryMath.DecodeRid(id);
            probeValidator = Math.Max(probeValidator, parts.Validator);
            probeIndex = Math.Max(probeIndex, parts.Index);
        }

        if (canvasItemRidIds.Count == 0)
        {
            return new CanvasBracketPlan(
                false,
                "no canvas-item RID was read off the sprite's mesh children, so the window has no lower edge",
                0,
                0,
                probeValidator,
                probeIndex,
                0,
                0,
                0,
                empty);
        }

        if (probeMeshRidIds.Count == 0)
        {
            return new CanvasBracketPlan(
                false,
                "no probe mesh RID was created, so the window has no upper edge",
                canvasLow,
                canvasHigh,
                0,
                0,
                0,
                0,
                0,
                empty);
        }

        if (probeValidator <= canvasHigh)
        {
            return new CanvasBracketPlan(
                false,
                "the probe mesh's validator is not above the highest canvas-item validator, so the two do not "
                + "bracket anything (the validator counter is not behaving as assumed)",
                canvasLow,
                canvasHigh,
                probeValidator,
                probeIndex,
                0,
                0,
                0,
                empty);
        }

        var slack = (uint)Math.Max(0, indexSlack);
        var indexHigh = probeIndex > uint.MaxValue - slack ? uint.MaxValue : probeIndex + slack;
        var candidates = Sts2SpineGeometryMath.PlanRidCandidates(
            Sts2SpineGeometryMath.ComposeRid(canvasHigh + 1, 0),
            Sts2SpineGeometryMath.ComposeRid(probeValidator, indexHigh),
            indexSlack: 0,
            cap: cap);
        var indexSpan = (long)indexHigh + 1;

        return new CanvasBracketPlan(
            true,
            "ok",
            canvasLow,
            canvasHigh,
            probeValidator,
            probeIndex,
            indexSpan,
            candidates.EmittedCount / indexSpan,
            indexSpan * RecommendedValidatorDepth,
            candidates);
    }

    // Whether a set of already-known mesh RIDs would have been reachable from a recipe-D window built out of
    // the same skeleton's canvas items. Offline, where both sets are in hand, this is the cheap dry run that
    // says whether the live recipe is sound BEFORE anybody spends a game session on it.
    internal static (int Contained, int Total, uint OffsetFromCanvasHigh) CheckCanvasBracketCoverage(
        CanvasBracketPlan plan,
        IReadOnlyCollection<ulong> knownMeshRidIds)
    {
        var contained = 0;
        var lowestValidator = uint.MaxValue;
        foreach (var id in knownMeshRidIds)
        {
            var parts = Sts2SpineGeometryMath.DecodeRid(id);
            lowestValidator = Math.Min(lowestValidator, parts.Validator);
            if (plan.Viable
                && parts.Validator >= plan.Candidates.ValidatorLow
                && parts.Validator <= plan.Candidates.ValidatorHigh
                && parts.Index >= plan.Candidates.IndexLow
                && parts.Index <= plan.Candidates.IndexHigh)
            {
                contained += 1;
            }
        }

        var offset = knownMeshRidIds.Count > 0 && lowestValidator > plan.CanvasValidatorHigh
            ? lowestValidator - plan.CanvasValidatorHigh
            : 0u;
        return (contained, knownMeshRidIds.Count, offset);
    }

    // A skeleton's own reported bounds are a superset of its drawn attachments, but a mesh can still poke a
    // little past them (Phase 1: one Fg mesh sits 68 px left of the reported box), so containment is judged
    // against the bounds inflated by this fraction of their larger side — the same 2% the readback-sanity
    // step uses, kept as one constant so the two cannot drift apart.
    internal const double DefaultSkeletonBoundsTolerance = 0.02;

    // A packed atlas region's outer edge lands exactly on 0 or 1 and float rounding can push it a few ULPs
    // past, so the unit-square test carries a hair of slack.
    internal const double UnitUvTolerance = 1e-4;

    /// <summary>
    /// One mesh surface reduced to the facts a spine-likeness decision needs, so the decision itself is
    /// Godot-free and unit-testable. <c>VertexVariantType</c> is the Godot variant type NAME as the readback
    /// reports it ("PackedVector2Array"), which is the cheapest honest way to carry the distinction without
    /// dragging the engine's enums into this file.
    /// </summary>
    internal sealed record SurfaceFacts(
        int SurfaceCount,
        string VertexVariantType,
        int VertexCount,
        int UvCount,
        IReadOnlyList<double> UvMin,
        IReadOnlyList<double> UvMax,
        int IndexCount,
        IReadOnlyList<double> Bbox);

    internal readonly record struct SpineLikeVerdict(bool Accepted, string Reason);

    // Is this surface plausibly one slot of the target skeleton? Every clause corresponds to something the
    // Phase-1 full sweep actually dragged in: three 230-vertex PackedVector3Array meshes belonging to some
    // other subsystem's 3D geometry (rejected on vertex type, and independently on their UV range, which is
    // what broke gate b), and one 256x256 quad at (4489,33) which passes EVERY clause here — Vector2, four
    // vertices, six indices, UVs exactly [0,1], bbox inside the skeleton's bounds. THAT ONE IS NOT
    // REJECTABLE by inspection, which is why an accepted candidate is only an anchor HYPOTHESIS: it is
    // confirmed by whether a stride run can be walked off it, not by this predicate.
    //
    // The reason strings are report keys (rejection histogram), so they are stable and lower-case-hyphenated.
    internal static SpineLikeVerdict IsSpineLikeSurface(
        SurfaceFacts facts,
        IReadOnlyList<double>? skeletonBounds,
        double boundsTolerance = DefaultSkeletonBoundsTolerance)
    {
        if (facts.SurfaceCount != 1)
        {
            return new SpineLikeVerdict(false, "surface-count");
        }

        if (!string.Equals(facts.VertexVariantType, "PackedVector2Array", StringComparison.Ordinal))
        {
            return new SpineLikeVerdict(false, "vertex-type");
        }

        if (facts.VertexCount <= 0)
        {
            return new SpineLikeVerdict(false, "no-vertices");
        }

        if (facts.UvCount <= 0 || facts.UvMin.Count < 2 || facts.UvMax.Count < 2)
        {
            return new SpineLikeVerdict(false, "uv-missing");
        }

        if (facts.UvCount != facts.VertexCount)
        {
            return new SpineLikeVerdict(false, "uv-count-mismatch");
        }

        if (facts.UvMin[0] < -UnitUvTolerance
            || facts.UvMin[1] < -UnitUvTolerance
            || facts.UvMax[0] > 1 + UnitUvTolerance
            || facts.UvMax[1] > 1 + UnitUvTolerance)
        {
            return new SpineLikeVerdict(false, "uv-out-of-unit-range");
        }

        if (facts.IndexCount <= 0 || facts.IndexCount % 3 != 0)
        {
            return new SpineLikeVerdict(false, "indices-not-triangles");
        }

        if (skeletonBounds is { Count: >= 4 } bounds && facts.Bbox.Count >= 4)
        {
            var tolerance = Math.Max(1d, Math.Max(bounds[2] - bounds[0], bounds[3] - bounds[1]))
                * Math.Max(0d, boundsTolerance);
            if (facts.Bbox[0] < bounds[0] - tolerance
                || facts.Bbox[1] < bounds[1] - tolerance
                || facts.Bbox[2] > bounds[2] + tolerance
                || facts.Bbox[3] > bounds[3] + tolerance)
            {
                return new SpineLikeVerdict(false, "bbox-outside-skeleton-bounds");
            }
        }

        return new SpineLikeVerdict(true, "ok");
    }

    // THE LOAD-BEARING PIECE OF ARITHMETIC IN THIS FILE.
    //
    // The anchor scan samples the validator axis every `s` rows and tests every index at each sample. The run
    // it is hunting is an arithmetic progression with validator step `stride`, so a sampled validator V hits a
    // member only when V ≡ v0 (mod stride) — and v0 is unknown. Pick `s` as a MULTIPLE of the stride (which is
    // exactly what the obvious "span / expected member count" choice gives: 14209/26 ≈ 546, or the 65 an
    // earlier draft used) and every sample lands in the SAME residue class as the first one. If that class is
    // not v0's, the scan provably never touches the run no matter how long it runs.
    //
    // With gcd(s, stride) = 1 the samples cycle through all `stride` residue classes, so any `stride`
    // consecutive samples cover v0's. Capping s at n-1 (n = expected members) guarantees at least that many
    // samples land inside the run's own span of stride*(n-1)+1 validators: span/s ≥ stride. Largest such s is
    // taken because the scan's cost is inversely proportional to it (26 children, stride 5 ⇒ 24, not 25).
    internal static uint ChooseAnchorValidatorStep(int expectedRunLength, uint stride)
    {
        var effectiveStride = Math.Max(1u, stride);
        var ceiling = (uint)Math.Max(1, expectedRunLength - 1);
        for (var step = ceiling; step >= 1; step -= 1)
        {
            if (GreatestCommonDivisor(step, effectiveStride) == 1)
            {
                return step;
            }
        }

        return 1;
    }

    private static uint GreatestCommonDivisor(uint a, uint b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    // The coarse pass over the canvas-item window: same validator range and index range as the full recipe-D
    // plan, but only every `ValidatorStep`-th validator row. `FullWindowCandidates` is carried alongside so a
    // report can state the saving rather than assert it.
    internal sealed record AnchorScanPlan(
        bool Viable,
        string Reason,
        uint ValidatorLow,
        uint ValidatorHigh,
        uint ValidatorStep,
        uint IndexLow,
        uint IndexHigh,
        long SampledValidators,
        long TotalCandidates,
        long FullWindowCandidates,
        int Cap,
        bool Truncated,
        long EmittedCount);

    internal static AnchorScanPlan PlanAnchorScan(
        CanvasBracketPlan bracket,
        int expectedRunLength,
        uint stride,
        uint stepOverride,
        int cap)
    {
        var effectiveCap = Math.Max(0, cap);
        if (!bracket.Viable)
        {
            return new AnchorScanPlan(
                false, bracket.Reason, 0, 0, 1, 0, 0, 0, 0, 0, effectiveCap, false, 0);
        }

        var window = bracket.Candidates;
        if (window.ValidatorHigh < window.ValidatorLow || window.IndexHigh < window.IndexLow)
        {
            return new AnchorScanPlan(
                false,
                "the canvas-item window is empty, so there is nothing to sample",
                window.ValidatorLow,
                window.ValidatorHigh,
                1,
                window.IndexLow,
                window.IndexHigh,
                0,
                0,
                window.TotalCandidates,
                effectiveCap,
                false,
                0);
        }

        var step = stepOverride > 0 ? stepOverride : ChooseAnchorValidatorStep(expectedRunLength, stride);
        var validatorSpan = (long)window.ValidatorHigh - window.ValidatorLow;
        var sampled = (validatorSpan / step) + 1;
        var indexSpan = (long)window.IndexHigh - window.IndexLow + 1;
        var total = sampled * indexSpan;
        var emitted = Math.Min(total, effectiveCap);

        return new AnchorScanPlan(
            true,
            "ok",
            window.ValidatorLow,
            window.ValidatorHigh,
            step,
            window.IndexLow,
            window.IndexHigh,
            sampled,
            total,
            window.TotalCandidates,
            effectiveCap,
            total > effectiveCap,
            emitted);
    }

    internal static IEnumerable<ulong> EnumerateAnchorScanCandidates(AnchorScanPlan plan)
    {
        if (!plan.Viable)
        {
            yield break;
        }

        long emitted = 0;
        for (var validator = plan.ValidatorLow; validator <= plan.ValidatorHigh; validator += plan.ValidatorStep)
        {
            for (var index = plan.IndexLow; index <= plan.IndexHigh; index += 1)
            {
                if (emitted >= plan.EmittedCount)
                {
                    yield break;
                }

                emitted += 1;
                yield return Sts2SpineGeometryMath.ComposeRid(validator, index);

                if (index == uint.MaxValue)
                {
                    break;
                }
            }

            // Both guards are for the arithmetic, not for a real window: stepping past uint.MaxValue would
            // wrap and re-sweep the bottom of the space forever.
            if (validator > uint.MaxValue - plan.ValidatorStep)
            {
                yield break;
            }
        }
    }

    // How short a walked run may be before its anchor is treated as a false positive rather than a member.
    // A quarter of the expected count is deliberately generous — the point is to reject the 256x256 quad
    // (a run of exactly 1) and its kind, not to insist the whole rig was recovered.
    internal static int MinimumAnchorRunLength(int expectedRunLength)
        => Math.Max(3, expectedRunLength / 4);

    // SPIRECTL_SPINE_GEOMETRY_PROBE_STRIDE="5,1" — validator step then index step. A bare "5" means "+5/+1".
    internal static bool TryParseStrideOverride(string? text, out uint validatorStride, out uint indexStride)
    {
        validatorStride = Sts2SpineGeometryMath.DefaultLiveValidatorStride;
        indexStride = Sts2SpineGeometryMath.DefaultLiveIndexStride;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValidator)
            || parsedValidator == 0)
        {
            return false;
        }

        validatorStride = parsedValidator;
        indexStride = parts.Length > 1
            && uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex)
                ? parsedIndex
                : Sts2SpineGeometryMath.DefaultLiveIndexStride;
        return true;
    }

    // ── Trimming a multi-rig run down to the target's own window ─────────────────────────────────────
    //
    // The walked run is one contiguous progression, but the rigs that were built back to back share it:
    // Phase 1's 33-member run is one rig's 7 meshes followed by the target's 26. Two independent signals can
    // say where the target's block starts inside the run, and they are computed BOTH WAYS ON PURPOSE — on the
    // Phase-1 data they disagree (build order says 7, bounds says {4,5,6}), and a lane that silently picked
    // one would have shipped a wrong answer with a passing gate.

    // Signal 1 — BUILD ORDER. If the run is the concatenation of whole rigs' blocks, in the order the rigs'
    // canvas items were minted, then only those contiguous groups of rigs whose child counts SUM to the run
    // length can explain it. Each such group implies one offset for the target's block.
    internal static IReadOnlyList<int> FeasibleTargetOffsets(
        int runLength,
        IReadOnlyList<int> childCounts,
        int targetOrdinal)
    {
        var offsets = new SortedSet<int>();
        if (runLength <= 0 || targetOrdinal < 0 || targetOrdinal >= childCounts.Count)
        {
            return [];
        }

        for (var first = 0; first <= targetOrdinal; first += 1)
        {
            var sum = 0;
            for (var last = first; last < childCounts.Count; last += 1)
            {
                sum += Math.Max(0, childCounts[last]);
                if (sum > runLength)
                {
                    break;
                }

                if (last < targetOrdinal || sum != runLength)
                {
                    continue;
                }

                var offset = 0;
                for (var earlier = first; earlier < targetOrdinal; earlier += 1)
                {
                    offset += Math.Max(0, childCounts[earlier]);
                }

                offsets.Add(offset);
            }
        }

        return [.. offsets];
    }

    internal sealed record OffsetBoundsScore(
        int Offset,
        double L1,
        int StrictlyOutside,
        IReadOnlyList<double> UnionBbox);

    internal sealed record BoundsOffsetChoice(
        int? Offset,
        bool Tied,
        IReadOnlyList<int> BestOffsets,
        IReadOnlyList<OffsetBoundsScore> Scores);

    // Signal 2 — BOUNDS. Slide a window of the target's own child count along the run and ask which position
    // reproduces the target skeleton's reported bounds: L1 distance between the window's union bbox and those
    // bounds, plus how many of the window's meshes poke strictly outside them. Ranked (outside, L1); a tie is
    // REPORTED as a tie rather than broken, because on the Phase-1 run three offsets score an exact 0.
    internal static BoundsOffsetChoice ChooseOffsetByBounds(
        IReadOnlyList<IReadOnlyList<double>> runBboxes,
        int windowLength,
        IReadOnlyList<double> skeletonBounds)
    {
        var scores = new List<OffsetBoundsScore>();
        if (windowLength <= 0 || runBboxes.Count < windowLength || skeletonBounds.Count < 4)
        {
            return new BoundsOffsetChoice(null, false, [], scores);
        }

        for (var offset = 0; offset + windowLength <= runBboxes.Count; offset += 1)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var outside = 0;
            for (var i = offset; i < offset + windowLength; i += 1)
            {
                var box = runBboxes[i];
                if (box.Count < 4)
                {
                    continue;
                }

                minX = Math.Min(minX, box[0]);
                minY = Math.Min(minY, box[1]);
                maxX = Math.Max(maxX, box[2]);
                maxY = Math.Max(maxY, box[3]);
                if (box[0] < skeletonBounds[0]
                    || box[1] < skeletonBounds[1]
                    || box[2] > skeletonBounds[2]
                    || box[3] > skeletonBounds[3])
                {
                    outside += 1;
                }
            }

            var union = minX <= maxX ? new[] { minX, minY, maxX, maxY } : [0d, 0d, 0d, 0d];
            var l1 = Math.Abs(union[0] - skeletonBounds[0])
                + Math.Abs(union[1] - skeletonBounds[1])
                + Math.Abs(union[2] - skeletonBounds[2])
                + Math.Abs(union[3] - skeletonBounds[3]);
            scores.Add(new OffsetBoundsScore(offset, l1, outside, union));
        }

        if (scores.Count == 0)
        {
            return new BoundsOffsetChoice(null, false, [], scores);
        }

        var bestOutside = scores.Min(score => score.StrictlyOutside);
        var bestL1 = scores.Where(score => score.StrictlyOutside == bestOutside).Min(score => score.L1);
        var best = scores
            .Where(score => score.StrictlyOutside == bestOutside && score.L1 <= bestL1 + 1e-6)
            .Select(score => score.Offset)
            .ToArray();

        return new BoundsOffsetChoice(best.Length == 1 ? best[0] : null, best.Length > 1, best, scores);
    }

    /// <summary>
    /// The trim verdict. `Agreed` is the only field a gate may act on: it is true exactly when the two
    /// signals single out the SAME offset. `Method` names which signal was the decisive one, or names the
    /// failure to agree — "disagreement" (both decided, differently) and "ambiguous" (nobody decided).
    /// </summary>
    internal sealed record TrimVerdict(
        string Method,
        bool Agreed,
        int? Offset,
        int WindowLength,
        IReadOnlyList<int> BuildOrderOffsets,
        BoundsOffsetChoice Bounds,
        IReadOnlyList<ulong> Trimmed,
        string Detail);

    internal static TrimVerdict TrimRunToTarget(
        IReadOnlyList<ulong> run,
        IReadOnlyList<IReadOnlyList<double>> runBboxes,
        IReadOnlyList<int> childCounts,
        int targetOrdinal,
        IReadOnlyList<double> skeletonBounds)
    {
        var windowLength = targetOrdinal >= 0 && targetOrdinal < childCounts.Count
            ? Math.Max(0, childCounts[targetOrdinal])
            : 0;
        var emptyBounds = new BoundsOffsetChoice(null, false, [], []);

        if (windowLength <= 0)
        {
            return new TrimVerdict(
                "ambiguous",
                false,
                null,
                windowLength,
                [],
                emptyBounds,
                [],
                "the target rig reported no mesh children, so there is no window to cut");
        }

        if (run.Count < windowLength)
        {
            return new TrimVerdict(
                "ambiguous",
                false,
                null,
                windowLength,
                [],
                emptyBounds,
                [],
                $"the walked run is {run.Count} mesh(es), shorter than the target's {windowLength} mesh children, "
                + "so no window of the target's size exists inside it");
        }

        var buildOrder = FeasibleTargetOffsets(run.Count, childCounts, targetOrdinal);
        var bounds = ChooseOffsetByBounds(runBboxes, windowLength, skeletonBounds);
        var agreedOffsets = buildOrder.Where(offset => bounds.BestOffsets.Contains(offset)).ToArray();

        if (agreedOffsets.Length == 1)
        {
            var offset = agreedOffsets[0];
            var method = buildOrder.Count == 1 ? "build-order" : "bounds";
            return new TrimVerdict(
                method,
                true,
                offset,
                windowLength,
                buildOrder,
                bounds,
                [.. run.Skip(offset).Take(windowLength)],
                $"both signals single out offset {offset} (build-order candidates [{string.Join(",", buildOrder)}], "
                + $"bounds best [{string.Join(",", bounds.BestOffsets)}])");
        }

        if (agreedOffsets.Length > 1)
        {
            return new TrimVerdict(
                "ambiguous",
                false,
                null,
                windowLength,
                buildOrder,
                bounds,
                [],
                $"the two signals agree on {agreedOffsets.Length} offsets ([{string.Join(",", agreedOffsets)}]) and "
                + "therefore on none of them");
        }

        if (buildOrder.Count > 0 && bounds.BestOffsets.Count > 0)
        {
            return new TrimVerdict(
                "disagreement",
                false,
                null,
                windowLength,
                buildOrder,
                bounds,
                [],
                $"build order says the target's block starts at [{string.Join(",", buildOrder)}] but the bounds "
                + $"score says [{string.Join(",", bounds.BestOffsets)}]; the run is reported untrimmed so the "
                + "question can be settled offline");
        }

        return new TrimVerdict(
            "ambiguous",
            false,
            null,
            windowLength,
            buildOrder,
            bounds,
            [],
            buildOrder.Count == 0
                ? "no contiguous group of rigs' mesh-child counts sums to the run length, so build order cannot "
                    + "explain the run at all"
                : "the bounds score singled out nothing to compare the build-order candidates against");
    }

}
