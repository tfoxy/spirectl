using System.Text.Json;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The bake's CHEAP mesh-RID acquisitions, both of them, against the same recorded id spaces: the WALK arm
// (Sts2SpineGeoClipWalk), which finds a rig's slot meshes by column scan → stride ladder → walk, and the two dense
// TIERS behind it (Sts2SpineGeoClipSweep.AcquireTieredAsync), which sweep the core index band before the whole
// slacked plan. Between them they are about half a pose-only bake's wall time, so they are exactly the kind of
// change that is worth nothing if it is wrong: a silently incomplete acquisition is what Phase 1 shipped (29 of
// the merchant's 44 slots) and it took two rounds to find.
//
// THE DOUBLE IS REAL DATA. Every id space below is the actual (validator, index) set the geometry probe dumped
// out of the game — both rigs, in BOTH recorded sessions, because the two sessions disagree about the thing the
// acquisition depends on. In the later session each rig's meshes sit on an exact validator +5 / index +1
// progression; in the earlier one the same rigs' validator deltas run 12, 15, 24, 8, 8, 7, 6, 8, 5, 9, 7 … while
// the index still advances by exactly +1. An acquisition tested only against the clean session would pass here
// and lose a third of the merchant on a bad day.
public sealed class Sts2SpineGeoClipWalkTests
{
    private static ulong Rid(uint validator, uint index) => Sts2SpineGeoClipSweep.ComposeRidId(validator, index);

    // ── The four recorded id spaces ──────────────────────────────────────────────────────────────────
    //
    // Source: .sts2/research/data/spine-geoclip-phase{1,2}/probe-offline/offline-<rig>.json, `recipeA`. Each
    // window below is the same file's recorded recipe-A plan, so the candidate counts these tests quote are the
    // ones the dense sweep actually paid.

    // merchant, later session — one clean +5/+1 line with a THIRTEEN-point hole in it (indices 116..128).
    private static readonly (uint V, uint I)[] MerchantMeshesClean =
    [
        (16417, 88), (16422, 89), (16427, 90), (16432, 91), (16437, 92), (16442, 93), (16447, 94), (16452, 95),
        (16457, 96), (16462, 97), (16467, 98), (16472, 99), (16477, 100), (16482, 101), (16487, 102),
        (16492, 103), (16497, 104), (16502, 105), (16507, 106), (16512, 107), (16517, 108), (16522, 109),
        (16527, 110), (16532, 111), (16537, 112), (16542, 113), (16547, 114), (16552, 115), (16622, 129),
        (16627, 130),
    ];

    private static readonly ulong MerchantCleanBracketLow = Rid(16415, 87);
    private static readonly ulong MerchantCleanBracketMid = Rid(16637, 131);

    // merchant, earlier session — the SAME rig with an irregular head (+12, +11) before the line settles.
    private static readonly (uint V, uint I)[] MerchantMeshesIrregular =
    [
        (16212, 88), (16224, 89), (16235, 90), (16240, 91), (16245, 92), (16250, 93), (16255, 94), (16260, 95),
        (16265, 96), (16270, 97), (16275, 98), (16280, 99), (16285, 100), (16290, 101), (16295, 102),
        (16300, 103), (16305, 104), (16310, 105), (16315, 106), (16320, 107), (16325, 108), (16330, 109),
        (16335, 110), (16340, 111), (16345, 112), (16350, 113), (16355, 114), (16360, 115), (16430, 129),
        (16435, 130),
    ];

    private static readonly ulong MerchantIrregularBracketLow = Rid(16204, 87);
    private static readonly ulong MerchantIrregularBracketMid = Rid(16448, 131);

    private static readonly (uint V, uint I)[] ByrdonisMeshesClean =
    [
        (16779, 89), (16784, 90), (16789, 91), (16794, 92), (16799, 93), (16804, 94), (16809, 95), (16814, 96),
        (16819, 97), (16824, 98), (16829, 99), (16834, 100), (16839, 101), (16844, 102), (16849, 103),
        (16854, 104), (16859, 105), (16864, 106), (16879, 109), (16884, 110), (16889, 111), (16894, 112),
        (16899, 113), (16904, 114), (16909, 115), (16914, 116),
    ];

    private static readonly ulong ByrdonisCleanBracketLow = Rid(16777, 88);
    private static readonly ulong ByrdonisCleanBracketMid = Rid(16919, 117);

    // byrdonis, earlier session — eleven distinct validator lines. A constant-stride walk sees at most 8 of
    // these 26; this is the case the index walk exists for.
    private static readonly (uint V, uint I)[] ByrdonisMeshesIrregular =
    [
        (18737, 89), (18749, 90), (18764, 91), (18788, 92), (18796, 93), (18804, 94), (18811, 95), (18817, 96),
        (18825, 97), (18830, 98), (18839, 99), (18846, 100), (18851, 101), (18856, 102), (18861, 103),
        (18866, 104), (18871, 105), (18876, 106), (18891, 109), (18896, 110), (18901, 111), (18906, 112),
        (18911, 113), (18916, 114), (18921, 115), (18926, 116),
    ];

    private static readonly ulong ByrdonisIrregularBracketLow = Rid(18727, 88);
    private static readonly ulong ByrdonisIrregularBracketMid = Rid(18941, 117);

    // A probe mesh taken after the acquisition-frame seek, closing window B. Not a recorded measurement — the
    // baker never dumped one — so it is modelled on the same file's recipe-D probe mesh distance.
    private static readonly ulong MerchantBracketPost = Rid(16948, 160);

    // Window B's meshes: the ones the animation mints when it is SET, at indices the single-window bracket never
    // reached. The probe never dumped their ids, so this space is CALIBRATED rather than recorded — but it is
    // calibrated on the one thing about them that IS measured, and the thing the tier gate turns on: how they
    // split either side of the mid bracket's index.
    //
    // WHAT CHANGED, AND WHY IT MATTERS. This space used to be `indices 132..145` — every mesh ABOVE the mid
    // bracket index of 131 — which is not a measurement but a restatement of the claim the narrow tier's window-B
    // band was making. A fixture built out of the assumption it is testing cannot fail, and this one did not: it
    // passed while the shipped band lost 13 of the merchant's 14 window-B meshes on every live bake (band
    // 187..253 found 1, full axis 0..253 found 14, in 25 recorded arms; byrdonis 172..237 found 0 against
    // 0..237 finding 2, in 31 arms).
    //
    // So the split is now the live one: THIRTEEN below the mid index, ONE at or above it. The validator
    // progression is still the clean session's +5/+1 above the mid bracket's validator, which keeps them clear
    // of window A's validator range (…16637) and so genuinely window-B-only.
    private static readonly (uint V, uint I)[] MerchantWindowBMeshes =
    [
        .. Enumerable.Range(0, 13).Select(k => ((uint)(16700 + (5 * k)), (uint)(117 + k))),
        (16765, 140),
    ];

    // The mid bracket index the old window-B band floored itself at. Named because three tests below are about
    // what sits either side of it.
    private const uint MerchantMidBracketIndex = 131;

    /// <summary>An id space with a known member set, counting every probe the acquisition spends on it.</summary>
    private sealed class IdSpace(params IEnumerable<(uint V, uint I)>[] members)
    {
        private readonly HashSet<ulong> _members =
            [.. members.SelectMany(set => set).Select(point => Rid(point.V, point.I))];

        internal long Probes { get; private set; }

        internal List<ulong> Observed { get; } = [];

        internal bool Validate(ulong id)
        {
            Probes += 1;
            Observed.Add(id);
            return _members.Contains(id);
        }

        internal void Add(uint validator, uint index) => _members.Add(Rid(validator, index));
    }

    private static Sts2SpineGeoClipSweep.SweepWindow WindowA(ulong bracketLow, ulong bracketMid)
        => Sts2SpineGeoClipSweep.PlanWindowA(bracketLow, bracketMid, indexSlack: 64, cap: 50_000);

    private static IReadOnlyList<(uint V, uint I)> Points(IEnumerable<ulong> ids)
        => [.. ids.Select(Sts2SpineGeoClipSweep.DecodeRidId).OrderBy(p => p.Validator)];

    // ── The whole arm, against the real id spaces ────────────────────────────────────────────────────

    // The headline: the acquisition it replaces validated 38 579 candidates to find these 30. Both the
    // completeness and the cost are asserted, because either one alone is satisfiable by a useless arm — a walk
    // that finds everything by sweeping the window has bought nothing, and a walk that probes twelve candidates
    // and finds four has bought a fallback.
    [Fact]
    public void Acquire_FindsEveryMerchantWindowAMeshOfTheCleanSessionForUnderFourPercentOfTheDenseSweep()
    {
        var space = new IdSpace(MerchantMeshesClean);
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var plan = Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44);

        var result = Sts2SpineGeoClipWalk.Acquire(plan, space.Validate);

        Assert.Equal(38_579, window.Plan.TotalCandidates);
        Assert.Equal(MerchantMeshesClean, Points(result.Found));
        Assert.Equal(space.Probes, result.Probed);
        Assert.True(
            result.Probed < 2_000,
            $"the walk probed {result.Probed} of {window.Plan.TotalCandidates}, which is not a saving worth the risk");
    }

    // The same rig, the session whose validator deltas wander between 5 and 24. A constant-stride walk cannot see
    // the first two meshes at all; the index walk is what recovers them, and the line sweep is what reaches the
    // pair beyond the thirteen-point hole. Neither alone is enough — which is why both run off every anchor.
    [Fact]
    public void Acquire_StillFindsEveryMerchantMeshWhenTheValidatorStrideIsIrregular()
    {
        var space = new IdSpace(MerchantMeshesIrregular);
        var window = WindowA(MerchantIrregularBracketLow, MerchantIrregularBracketMid);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44), space.Validate);

        Assert.Equal(42_385, window.Plan.TotalCandidates);
        Assert.Equal(MerchantMeshesIrregular, Points(result.Found));
    }

    [Fact]
    public void Acquire_FindsEveryByrdonisWindowAMeshInBothRecordedSessions()
    {
        var clean = new IdSpace(ByrdonisMeshesClean);
        var cleanResult = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(ByrdonisCleanBracketLow, ByrdonisCleanBracketMid), 28),
            clean.Validate);

        var irregular = new IdSpace(ByrdonisMeshesIrregular);
        var irregularResult = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(ByrdonisIrregularBracketLow, ByrdonisIrregularBracketMid), 28),
            irregular.Validate);

        Assert.Equal(ByrdonisMeshesClean, Points(cleanResult.Found));
        Assert.Equal(ByrdonisMeshesIrregular, Points(irregularResult.Found));
    }

    // ── The stride is MEASURED, never assumed ────────────────────────────────────────────────────────

    // The seeded stride is +5/+1 because that is what the clean session shows. A rig on a different progression
    // must still be walked, so the ladder has to find the progression rather than confirm the seed.
    [Fact]
    public void Acquire_MeasuresAStrideTheAssumedOneWouldHaveMissedEntirely()
    {
        var sevens = Enumerable.Range(0, 26).Select(k => ((uint)(16420 + (7 * k)), (uint)(88 + k))).ToArray();
        var space = new IdSpace(sevens);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 26),
            space.Validate);

        Assert.Equal(Sts2SpineGeoClipWalk.StrideMeasured, result.StrideSource);
        Assert.Equal(7u, result.ValidatorStride);
        Assert.Equal(1u, result.IndexStride);
        Assert.Equal(sevens, Points(result.Found));
    }

    [Fact]
    public void Acquire_ReportsTheStrideItActuallyMeasuredOnTheRealMerchantSpace()
    {
        var space = new IdSpace(MerchantMeshesClean);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Equal(Sts2SpineGeoClipWalk.StrideMeasured, result.StrideSource);
        Assert.Equal(5u, result.ValidatorStride);
    }

    // Two points are collinear by definition, so a single neighbour is not evidence of a progression: the ladder
    // requires the three-point arithmetic progression before it calls a stride measured.
    [Fact]
    public void MeasureStride_RefusesToCallASingleNeighbourAProgression()
    {
        var space = new IdSpace([(16420u, 88u), (16429u, 89u)]);
        var plan = Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 26);

        var choice = Sts2SpineGeoClipWalk.MeasureStride(
            new Sts2SpineGeometryMath.RidPoint(16420, 88), space.Validate, plan, out var probed);

        Assert.Equal("default", choice.Source);
        Assert.Equal(Sts2SpineGeoClipWalk.AssumedValidatorStride, choice.ValidatorStride);
        Assert.Contains(9u, choice.ConfirmedDeltas);
        Assert.Empty(choice.DoubleConfirmedDeltas);
        Assert.True(probed > 0);
    }

    // ── The progression a walked run states about itself ─────────────────────────────────────────────

    // The merchant's own run votes 5 twenty-nine times, including across its thirteen-point hole: that pair's
    // Δ70 over 14 indices divides exactly, so it agrees with its neighbours instead of shouting 70.
    [Fact]
    public void ModalStride_ReadsFiveOffTheMerchantsRunIncludingAcrossItsHole()
        => Assert.Equal(
            5u,
            Sts2SpineGeoClipWalk.ModalStride(
                [.. MerchantMeshesClean.Select(point => Rid(point.V, point.I))], fallback: 99));

    // …and off the session whose head is irregular, where the ladder measures nothing at all.
    [Fact]
    public void ModalStride_StillReadsFiveWhenTheRunsHeadIsIrregular()
        => Assert.Equal(
            5u,
            Sts2SpineGeoClipWalk.ModalStride(
                [.. MerchantMeshesIrregular.Select(point => Rid(point.V, point.I))], fallback: 99));

    // A pair whose validator delta does NOT divide its index delta says nothing about the per-mesh stride — the
    // two members are simply not consecutive on one line. Counting it anyway means integer division inventing a
    // stride nothing is on, and on a short run that invention can outvote the truth.
    [Fact]
    public void ModalStride_IgnoresAPairWhoseValidatorDeltaDoesNotDivideItsIndexDelta()
    {
        var run = new[] { Rid(100, 10), Rid(107, 12), Rid(112, 13) };

        Assert.Equal(5u, Sts2SpineGeoClipWalk.ModalStride(run, fallback: 99));
    }

    [Fact]
    public void ModalStride_FallsBackWhenARunIsTooShortToStateAProgression()
        => Assert.Equal(99u, Sts2SpineGeoClipWalk.ModalStride([Rid(100, 10)], fallback: 99));

    // ── The two walks ────────────────────────────────────────────────────────────────────────────────

    // The LINE sweep must not stop on a hole. The merchant's own run has thirteen consecutive missing lattice
    // points in it, and the two meshes past that hole are real slots — a miss budget of any ordinary size drops
    // them, and `complete` then reads false for a reason nothing in the report explains.
    [Fact]
    public void Acquire_SpansTheThirteenPointHoleInTheMerchantsOwnRun()
    {
        var space = new IdSpace(MerchantMeshesClean);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Contains(Rid(16622, 129), result.Found);
        Assert.Contains(Rid(16627, 130), result.Found);
        Assert.Equal([13], result.GapSizes);
        Assert.Equal("validator-ceiling", result.StopUp);
        Assert.Equal("validator-floor", result.StopDown);
    }

    // The index walk is the half that CAN stop on consecutive misses, and must: bridging a hole by widening its
    // validator search is quadratic, so a long hole is the line sweep's job.
    [Fact]
    public void WalkByIndex_StopsOnConsecutiveMissesRatherThanWideningForever()
    {
        var space = new IdSpace([(16420u, 88u), (16425u, 89u), (16430u, 90u)]);
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(16_400, 16_600, 20, 200, 64, 64);

        var walk = Sts2SpineGeoClipWalk.WalkByIndex(
            new Sts2SpineGeometryMath.RidPoint(16420, 88), space.Validate, limits, reach: 48, missBudget: 2);

        Assert.Equal([(16425u, 89u), (16430u, 90u)], Points(walk.Found));
        Assert.Equal("consecutive-misses", walk.StopUp);
        Assert.Equal("consecutive-misses", walk.StopDown);
    }

    // A hole SHORTER than the miss budget is bridged rather than treated as the end of the run. Byrdonis has
    // exactly this shape (indices 107 and 108 mint meshes that never validate) in both recorded sessions, so the
    // SHIPPED budget is what this asserts — a literal here would let the default be lowered under it.
    [Fact]
    public void WalkByIndex_BridgesTheTwoIndexHoleByrdonisActuallyHasAtTheShippedMissBudget()
    {
        var space = new IdSpace(ByrdonisMeshesIrregular);
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(18_727, 18_941, 24, 181, 64, 64);

        var walk = Sts2SpineGeoClipWalk.WalkByIndex(
            new Sts2SpineGeometryMath.RidPoint(18876, 106),
            space.Validate,
            limits,
            Sts2SpineGeoClipWalk.DefaultIndexWalkReach,
            Sts2SpineGeoClipWalk.DefaultIndexWalkMissBudget);

        Assert.Contains(Rid(18891, 109), walk.Found);
        Assert.Equal(25, walk.Found.Count);
    }

    // The search WIDENS with each missed index rather than staying at one reach: a mesh that exists as an id but
    // fails the predicate still advanced the validator counter, so the member two indices out sits proportionally
    // further away. At the largest per-mesh delta the sessions recorded (24) a two-index hole puts the next
    // member 72 validators out — beyond a single reach of 48, and inside the third step's widened one.
    [Fact]
    public void WalkByIndex_WidensItsValidatorSearchWithEachMissedIndex()
    {
        var space = new IdSpace([(1000u, 50u), (1072u, 53u)]);
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(900, 1_200, 40, 60, 64, 64);

        var walk = Sts2SpineGeoClipWalk.WalkByIndex(
            new Sts2SpineGeometryMath.RidPoint(1000, 50),
            space.Validate,
            limits,
            Sts2SpineGeoClipWalk.DefaultIndexWalkReach,
            Sts2SpineGeoClipWalk.DefaultIndexWalkMissBudget);

        Assert.Equal([(1072u, 53u)], Points(walk.Found));
    }

    [Fact]
    public void WalkByIndex_NeverStepsOutsideTheWindowItWasGiven()
    {
        var space = new IdSpace(MerchantMeshesClean);
        space.Add(16700, 140);
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(16_415, 16_637, 23, 195, 64, 64);

        var walk = Sts2SpineGeoClipWalk.WalkByIndex(
            new Sts2SpineGeometryMath.RidPoint(16417, 88), space.Validate, limits, reach: 48, missBudget: 2);

        Assert.DoesNotContain(Rid(16700, 140), walk.Found);
        Assert.All(
            Points(walk.Found),
            point => Assert.True(point.V is >= 16_415 and <= 16_637 && point.I is >= 23 and <= 195));
    }

    // ── The lone plausible quad ──────────────────────────────────────────────────────────────────────

    // THE DOCUMENTED BLIND SPOT, and the one thing that closes it. The live battery found a 256×256 quad at
    // index 33 with four vertices, six indices, uvs exactly [0,1] and a box inside the skeleton's bounds — it
    // passes every clause of IsPlausibleSlotMesh and is excluded ONLY because nothing walks off it. If a change
    // to what "a run" means lets a run of one through, that quad is baked as a slot's art.
    [Fact]
    public void Acquire_RefusesALonePlausibleQuadNoWalkCanBeBuiltFrom()
    {
        var space = new IdSpace([(16_500u, 90u)]);
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44), space.Validate);

        Assert.Empty(result.Found);
        Assert.Equal(1, result.AnchorHypotheses);
        Assert.Equal(1, result.DeadAnchors);
        Assert.Equal(6, Sts2SpineGeoClipWalk.MinimumRunLength);
    }

    // …and the quad must not poison a real rig either: it is a dead anchor, and the scan carries on past it.
    [Fact]
    public void Acquire_WalksPastTheQuadAndStillRecoversTheWholeRig()
    {
        // Parked one column BELOW the rig's band, so the scan meets it before it meets the rig.
        var space = new IdSpace(MerchantMeshesClean, [(16_500u, 87u)]);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Equal(1, result.DeadAnchors);
        Assert.DoesNotContain(Rid(16_500, 87), result.Found);
        Assert.Equal(MerchantMeshesClean, Points(result.Found));
    }

    // A run of exactly the minimum length is kept; one short of it is not. The boundary is asserted from both
    // sides so a change to the constant cannot pass by moving in either direction.
    [Theory]
    [InlineData(5, 0)]
    [InlineData(6, 6)]
    public void Acquire_KeepsARunOnlyOnceItReachesTheMinimumRunLength(int runLength, int expectedFound)
    {
        var run = Enumerable.Range(0, runLength).Select(k => ((uint)(16_420 + (5 * k)), (uint)(88 + k))).ToArray();
        var space = new IdSpace(run);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Equal(expectedFound, result.Found.Count);
    }

    // ── Window B keeps its shape ─────────────────────────────────────────────────────────────────────

    // Window B's index axis is floored at ZERO because its meshes come out of a different owner's index pool
    // than the probe meshes that bound it. The walk arm must inherit that floor: an acquisition that starts its
    // coverage at the bracket's index is the Phase-1 bug with a new name.
    //
    // The seed is still the mid bracket index, but the scan now FANS OUT BOTH WAYS from it — 131, 130, 132,
    // 129, … — so the half of the axis below the seed is reached at the same rate as the half above it rather
    // than after a whole wrap. Both neighbours are asserted, because an order that fanned out one way only
    // still puts every index in the list and still passes coverage.
    [Fact]
    public void PlanColumnOrder_FansOutBothWaysFromTheMidBracketIndexAndStillCoversWindowBsFloorOfZero()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantCleanBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);
        var order = Sts2SpineGeoClipWalk.PlanColumnOrder(window.Plan, window.CoreIndexLow);

        Assert.Equal(0u, window.Plan.IndexLow);
        Assert.Equal(131u, order[0]);
        Assert.Equal(130u, order[1]);
        Assert.Equal(132u, order[2]);
        Assert.Contains(0u, order);
        Assert.Equal(224u, window.Plan.IndexHigh);
        Assert.Contains(224u, order);
        Assert.Equal(window.IndexSpan, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    // WHAT THE FAN-OUT BUYS ON WINDOW B, AND WHAT IT STILL DOES NOT.
    //
    // Buys: the 13 meshes below the seed. The scan now reaches index 129 on its fourth column (131, 130, 132,
    // 129) instead of after a whole wrap of the axis, anchors there, and walks the run down to 117.
    //
    // Does not buy: the ISOLATED mesh at index 140. Once a run is accepted the scan stops at the first column
    // outside that run's own index extent (StopBandClosed), and 140 is past it — so the arm still under-delivers
    // and the dense sweep behind it is still what completes the rig. That is the honest statement: the column
    // order was wrong about the SIDE and is now right about it; it was never sufficient on its own.
    //
    // HISTORY, kept because it is why this fixture reads the way it does. The scan used to run upward from the
    // seed and wrap to the floor only afterwards, which is the same containment claim the narrow tier's window-B
    // band was making — that these meshes sit at or above the mid bracket index. The fixture that "proved" it
    // safe was built on that claim (indices 132..145) and so could not fail. It was recalibrated on the live
    // split instead — 13 below the mid index, 1 at or above — and the walk arm then recovered NOTHING at all.
    // None of this was ever a live defect: the walk is off by default, measured a loss, and the dense tiers
    // sweep window B at full width. It is a PREREQUISITE for re-arming the walk.
    [Fact]
    public void Acquire_RecoversWindowBsSubSeedMeshesButStillComesUpShortOfTheIsolatedOne()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantCleanBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);
        var space = new IdSpace(MerchantWindowBMeshes);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44), space.Validate);

        Assert.Equal(MerchantMidBracketIndex, window.CoreIndexLow);

        var subSeed = MerchantWindowBMeshes.Where(mesh => mesh.I < MerchantMidBracketIndex).ToArray();
        Assert.Equal(13, subSeed.Length);
        Assert.Equal(13, result.Found.Count);
        Assert.Equal(subSeed, Points(result.Found));

        // The residual, asserted rather than glossed: the lone mesh above the seed is outside the accepted run's
        // extent, the scan closes there, and nothing reaches it.
        Assert.DoesNotContain(Rid(16_765, 140), result.Found);
        Assert.Equal("band-closed", result.StopReason);

        // And the thing that makes that survivable: coming up short arms the dense sweep, which has no seed.
        Assert.True(Sts2SpineGeoClipWalk.DenseSweepRequired(true, result.Found.Count, 44));

        var tiered = Tiered([window], expectedRunLength: 14, new IdSpace(MerchantWindowBMeshes));
        Assert.Equal(14, tiered.FoundAfterNarrow);
    }

    // Window A's column scan starts at the bracket's own index range, which is where every recorded window-A
    // mesh sits — but the scan may not STOP there, because that band is a hint and not a measurement.
    [Fact]
    public void PlanColumnOrder_StartsInsideTheBracketBandAndStillReachesTheWholeSlackedAxis()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var order = Sts2SpineGeoClipWalk.PlanColumnOrder(window.Plan, window.CoreIndexLow);

        Assert.Equal(87u, order[0]);
        Assert.Equal(23u, window.Plan.IndexLow);
        Assert.Equal(195u, window.Plan.IndexHigh);
        Assert.Equal(173, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
        Assert.Contains(23u, order);
        Assert.Contains(195u, order);
    }

    // THE ARM'S REAL LIMIT, asserted rather than glossed. A run parked far outside the hinted band IS covered by
    // the column order, but the probe budget runs out long before the scan walks there — an empty column costs
    // the window's whole validator span. So the honest statement is not "the walk always finds it"; it is "the
    // walk comes up short, says so, and the dense sweep is armed". Anything else here would be a test asserting
    // a property the code does not have.
    //
    // The fan-out did not change that and this is the arithmetic: window A's validator span is 223 rows, so the
    // 8 192-probe budget buys about 36 empty columns, and alternation reaches index 39 from the seed at 87 on
    // its ~96th. Halving the distance to a run on the wrong side of the seed is a real saving, and it is nowhere
    // near enough to make the walk sufficient by itself.
    [Fact]
    public void Acquire_CannotReachARunParkedFarOutsideTheHintedBandAndSaysSo()
    {
        var outside = Enumerable.Range(0, 10).Select(k => ((uint)(16_430 + (5 * k)), (uint)(30 + k))).ToArray();
        var space = new IdSpace(outside);
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var plan = Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44);

        var result = Sts2SpineGeoClipWalk.Acquire(plan, space.Validate);

        Assert.Contains(30u, plan.ColumnOrder);
        Assert.Empty(result.Found);
        Assert.True(result.BudgetExhausted);
        Assert.True(Sts2SpineGeoClipWalk.DenseSweepRequired(true, result.Found.Count, 44));
    }

    // The band hint does not shrink the walk once a run is anchored: a rig whose meshes straddle the bracket's
    // own index range is followed out of it, because the walks are bounded by the WINDOW, not by the hint.
    [Fact]
    public void Acquire_FollowsARunOutOfTheHintedBandOnceItHasAnchoredInside()
    {
        // Indices 80..99: ten below the merchant bracket's low index of 87, ten above it.
        var straddling = Enumerable.Range(0, 20).Select(k => ((uint)(16_430 + (5 * k)), (uint)(80 + k))).ToArray();
        var space = new IdSpace(straddling);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Equal(straddling, Points(result.Found));
        Assert.Equal(80u, result.IndexExtentLow);
        Assert.Equal(99u, result.IndexExtentHigh);
    }

    // ── When the arm refuses, and when the fallback fires ────────────────────────────────────────────

    [Fact]
    public void Plan_RefusesWhenTheKillSwitchForcesTheDenseSweep()
    {
        var plan = Sts2SpineGeoClipWalk.Plan(
            WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), expectedRunLength: 44, denseSweepOnly: true);

        Assert.False(plan.Viable);
        Assert.Contains("disarmed", plan.Reason);
        Assert.Empty(Sts2SpineGeoClipWalk.Acquire(plan, _ => true).Found);
        Assert.False(Sts2SpineGeoClipWalk.Acquire(plan, _ => true).Ran);
    }

    // A window small enough to sweep outright is not worth an acquisition that can miss. Byrdonis's window B is
    // 2 184 candidates and cost 0.10 s dense — less than the walk could save, and the place where a run too
    // short to clear the minimum length lives.
    [Fact]
    public void Plan_RefusesAWindowSmallEnoughToSweepDensely()
    {
        var small = Sts2SpineGeoClipSweep.PlanWindowB(
            Rid(16_500, 117), Rid(16_511, 130), indexSlack: 64, configuredCap: 50_000, envCap: null);

        var plan = Sts2SpineGeoClipWalk.Plan(small, expectedRunLength: 28);

        Assert.True(small.Plan.TotalCandidates <= Sts2SpineGeoClipWalk.DefaultDenseFloorCandidates);
        Assert.False(plan.Viable);
        Assert.Contains("dense sweep is already cheaper", plan.Reason);
    }

    [Fact]
    public void Plan_RefusesARigWithTooFewVisibleSlotsToTellARunFromAFalsePositive()
    {
        var plan = Sts2SpineGeoClipWalk.Plan(
            WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid),
            expectedRunLength: Sts2SpineGeoClipWalk.MinimumRunLength - 1);

        Assert.False(plan.Viable);
        Assert.Contains("below the", plan.Reason);
    }

    // THE CORRECTNESS RULE. A faster acquisition that loses the merchant's helmet is strictly worse than the
    // sweep it replaces, so the dense sweep runs unless the walk delivered one mesh per visible slot.
    [Theory]
    [InlineData(true, 44, 44, false)]
    [InlineData(true, 45, 44, false)]
    [InlineData(true, 43, 44, true)]
    [InlineData(true, 0, 44, true)]
    [InlineData(false, 44, 44, true)]
    public void DenseSweepRequired_FiresWheneverTheWalkDidNotCoverEveryVisibleSlot(
        bool anyWalkRan,
        int walkedMeshes,
        int expectedRunLength,
        bool expected)
        => Assert.Equal(
            expected, Sts2SpineGeoClipWalk.DenseSweepRequired(anyWalkRan, walkedMeshes, expectedRunLength));

    [Theory]
    [InlineData(true, false, "walk")]
    [InlineData(true, true, "walk-then-dense")]
    [InlineData(false, true, "dense")]
    [InlineData(false, false, "dense")]
    public void ArmName_NamesEveryCombinationTheReportCanCarry(bool walkRan, bool denseRan, string expected)
        => Assert.Equal(expected, Sts2SpineGeoClipWalk.ArmName(walkRan, denseRan));

    // The under-delivery the rule is written for, end to end on real data: a session in which only part of the
    // rig sits in window A leaves the total short, and that is what arms the sweep.
    [Fact]
    public void Acquire_UnderDeliversVisiblyWhenTheWindowHoldsOnlyPartOfTheRig()
    {
        var space = new IdSpace(MerchantMeshesClean);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44),
            space.Validate);

        Assert.Equal(30, result.Found.Count);
        Assert.True(Sts2SpineGeoClipWalk.DenseSweepRequired(true, result.Found.Count, 44));
    }

    // ── Bounds on what the arm may spend ─────────────────────────────────────────────────────────────

    // The arm runs synchronously between two awaited frames, so its worst case has to be bounded by something
    // the dense sweep already pays between two of its own yields.
    [Fact]
    public void Acquire_StopsAtItsProbeBudgetRatherThanDegeneratingIntoTheDenseSweep()
    {
        var space = new IdSpace();
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);

        var result = Sts2SpineGeoClipWalk.Acquire(
            Sts2SpineGeoClipWalk.Plan(window, expectedRunLength: 44), space.Validate);

        Assert.True(result.BudgetExhausted);
        Assert.Equal("probe-budget", result.StopReason);
        Assert.InRange(
            result.Probed,
            Sts2SpineGeoClipWalk.DefaultProbeBudget,
            Sts2SpineGeoClipWalk.DefaultProbeBudget + window.ValidatorSpan);
        Assert.True(result.Probed < window.Plan.TotalCandidates);
    }

    // Once a run is accepted the scan stops at the first column outside its index extent. That is what turns
    // window A from 31 889 candidates into a few hundred; without it the arm proves the empty half of the index
    // axis empty, column by column, and saves nothing.
    //
    // THE FAN-OUT MOVED WHICH SIDE IT CLOSES ON, and this is where that is recorded rather than glossed.
    // Ascending-then-wrap anchored at 88, then met 89..130 INSIDE the extent — 42 skipped columns, free, since
    // skipping is a `continue` — and closed at 131. Alternation reaches 88 on its third column (87, 86, 88) and
    // the next column in the order is 85, already outside the extent, so it closes there and skips nothing. Same
    // 30 meshes, same extent, same stop reason; the whole difference is the one extra empty column at 86, and
    // the arm's total probe budget is asserted separately above.
    [Fact]
    public void Acquire_StopsScanningColumnsAtTheFirstColumnOutsideTheRunsIndexBand()
    {
        var space = new IdSpace(MerchantMeshesClean);
        var plan = Sts2SpineGeoClipWalk.Plan(WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), 44);

        var result = Sts2SpineGeoClipWalk.Acquire(plan, space.Validate);

        Assert.Equal("band-closed", result.StopReason);
        Assert.Equal(88u, result.IndexExtentLow);
        Assert.Equal(130u, result.IndexExtentHigh);
        Assert.Equal(3, result.ColumnsScanned);
        Assert.Equal(0, result.ColumnsSkipped);
        Assert.Equal(173, plan.ColumnOrder.Count);
        Assert.True(
            result.ColumnsScanned + result.ColumnsSkipped < 10,
            $"the column scan visited {result.ColumnsScanned + result.ColumnsSkipped} of the axis's "
            + $"{plan.ColumnOrder.Count} columns to acquire a band it had already walked");
    }

    // ── The two dense TIERS ──────────────────────────────────────────────────────────────────────────
    //
    // The dense sweep is 70-76 % of a pose-only bake, and its index axis is mostly PADDING: window A widens the
    // bracket's own index band by 64 each way and window B floors its axis at 0. The narrow tier sweeps the
    // un-padded band first and the wide tier — the plan verbatim — only runs if that came up short.
    //
    // The claim the tier rests on is a property of the recorded data, so it is asserted as data first, then as
    // behaviour. Everything below drives the PRODUCTION driver (AcquireTieredAsync), not a re-implementation of
    // it: which plan each tier sweeps and whether the wide tier fires are the two things that could silently lose
    // a mesh, so a test that decided either of them itself would be grading its own arithmetic.

    /// <summary>
    /// The tiered acquisition against a simulated id space. No frame yield, so nothing incomplete is ever awaited
    /// and the driver runs to completion synchronously.
    /// </summary>
    private static Sts2SpineGeoClipSweep.TieredSweep Tiered(
        IReadOnlyList<Sts2SpineGeoClipSweep.SweepWindow> windows,
        int expectedRunLength,
        IdSpace space,
        IEnumerable<ulong>? foundBeforeTiers = null)
        => Sts2SpineGeoClipSweep
            .AcquireTieredAsync(windows, expectedRunLength, foundBeforeTiers ?? [], (_, _) => space.Validate)
            .GetAwaiter()
            .GetResult();

    private static Sts2SpineGeoClipSweep.SweepWindow MerchantWindowB()
        => Sts2SpineGeoClipSweep.PlanWindowB(
            MerchantCleanBracketMid, MerchantBracketPost, indexSlack: 64, configuredCap: 50_000, envCap: null);

    // THE CLAIM THE NARROW TIER RESTS ON, asserted against the data rather than quoted from a comment. In all
    // four recorded id spaces every mesh sits STRICTLY inside the bracket's own index range — never in the slack
    // ring the plan pads it with. If a fifth recorded session ever violates this, this test is where it says so,
    // and the wide tier behind the narrow one is what stops it from costing a mesh.
    [Fact]
    public void EveryRecordedMeshSitsStrictlyInsideItsBracketsOwnIndexBand()
    {
        (string Rig, (uint V, uint I)[] Meshes, ulong Low, ulong Mid)[] recorded =
        [
            ("merchant/clean", MerchantMeshesClean, MerchantCleanBracketLow, MerchantCleanBracketMid),
            ("merchant/irregular", MerchantMeshesIrregular, MerchantIrregularBracketLow, MerchantIrregularBracketMid),
            ("byrdonis/clean", ByrdonisMeshesClean, ByrdonisCleanBracketLow, ByrdonisCleanBracketMid),
            ("byrdonis/irregular", ByrdonisMeshesIrregular, ByrdonisIrregularBracketLow, ByrdonisIrregularBracketMid),
        ];

        foreach (var (rig, meshes, low, mid) in recorded)
        {
            var lowIndex = Sts2SpineGeoClipSweep.DecodeRidId(low).Index;
            var midIndex = Sts2SpineGeoClipSweep.DecodeRidId(mid).Index;
            foreach (var (validator, index) in meshes)
            {
                Assert.True(
                    index > lowIndex && index < midIndex,
                    $"{rig}: mesh ({validator},{index}) is not strictly inside the bracket band "
                    + $"{lowIndex}..{midIndex}, so the narrow tier would miss it");
            }
        }
    }

    // ── (a) The narrow tier ALONE, on all four recorded id spaces ────────────────────────────────────
    //
    // Both halves are asserted, because either alone is satisfiable by a useless tier: a narrow tier that finds
    // everything by sweeping the whole slacked plan has bought nothing, and one that probes a tenth of it and
    // finds a tenth of the rig has bought a fallback.

    [Fact]
    public void NarrowTierAlone_FindsEveryMerchantCleanMeshFor10035ProbesInsteadOf38579()
        => AssertNarrowTierCarriesTheWindowAlone(
            "merchant/clean", MerchantMeshesClean, MerchantCleanBracketLow, MerchantCleanBracketMid, 38_579, 10_035);

    [Fact]
    public void NarrowTierAlone_FindsEveryMerchantIrregularMeshFor11025ProbesInsteadOf42385()
        => AssertNarrowTierCarriesTheWindowAlone(
            "merchant/irregular",
            MerchantMeshesIrregular,
            MerchantIrregularBracketLow,
            MerchantIrregularBracketMid,
            42_385,
            11_025);

    [Fact]
    public void NarrowTierAlone_FindsEveryByrdonisCleanMeshFor4290ProbesInsteadOf22594()
        => AssertNarrowTierCarriesTheWindowAlone(
            "byrdonis/clean", ByrdonisMeshesClean, ByrdonisCleanBracketLow, ByrdonisCleanBracketMid, 22_594, 4_290);

    [Fact]
    public void NarrowTierAlone_FindsEveryByrdonisIrregularMeshFor6450ProbesInsteadOf33970()
        => AssertNarrowTierCarriesTheWindowAlone(
            "byrdonis/irregular",
            ByrdonisMeshesIrregular,
            ByrdonisIrregularBracketLow,
            ByrdonisIrregularBracketMid,
            33_970,
            6_450);

    private static void AssertNarrowTierCarriesTheWindowAlone(
        string rig,
        (uint V, uint I)[] meshes,
        ulong bracketLow,
        ulong bracketMid,
        long wideCandidates,
        long narrowCandidates)
    {
        var window = WindowA(bracketLow, bracketMid);
        var space = new IdSpace(meshes);

        var tiered = Tiered([window], expectedRunLength: meshes.Length, space);

        Assert.Equal(wideCandidates, window.Plan.TotalCandidates);
        var narrow = Assert.Single(tiered.Passes);
        Assert.Equal(Sts2SpineGeoClipSweep.TierNarrow, narrow.Tier);
        Assert.False(
            tiered.WideRan,
            $"{rig}: the narrow tier found every mesh and the wide sweep ran anyway, which is the whole cost this "
            + "change exists to avoid");
        Assert.Equal(meshes, Points(narrow.Found));
        Assert.Equal(narrowCandidates, narrow.Probed);
        Assert.Equal(narrow.Probed, space.Probes);
        Assert.True(
            narrow.Probed * 2 < wideCandidates,
            $"{rig}: the narrow tier paid {narrow.Probed} probes against the wide plan's {wideCandidates} "
            + $"({100 - (narrow.Probed * 100 / wideCandidates)} % fewer), which is not a saving worth a second "
            + "pass");
    }

    // The whole acquisition, both windows, on the merchant's recorded numbers: 30 meshes in window A and the 14
    // window B was added for. Tier one delivers all 44 — window A narrowed to its core band, window B at full
    // width — for 80 235 probes against the two plans' 108 779, and the wide tier never fires.
    //
    // 26 %, and the honest caveat with it. This fixture's window B is 70 200 of the 108 779 because its closing
    // bracket is a MODELLED id, which makes the one window that cannot be narrowed two thirds of the space and
    // flatters nothing. On the real recorded plans window B is much smaller relative to A (merchant live: A
    // 38 700, B 18 288), so tier one costs 9 900 + 18 288 = 28 188 against the wide plan's 56 988 — a 51 % cut,
    // and a 61 % cut against the 71 712 the shipped narrow+wide behaviour actually paid on every live bake.
    [Fact]
    public void NarrowTier_CarriesBothMerchantWindowsFor80235ProbesInsteadOf108779()
    {
        var windowA = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var windowB = MerchantWindowB();
        var space = new IdSpace(MerchantMeshesClean, MerchantWindowBMeshes);

        var tiered = Tiered([windowA, windowB], expectedRunLength: 44, space);

        Assert.False(
            tiered.WideRan,
            "tier one found every mesh and the wide sweep ran anyway — the failure this round exists to fix");
        Assert.Equal(44, tiered.FoundAfterNarrow);
        Assert.Equal(108_779, tiered.WideCandidates);
        Assert.Equal(80_235, tiered.NarrowProbed);
        Assert.Equal(2, tiered.Passes.Count);
        Assert.Equal(30, tiered.Passes[0].Found.Count);
        Assert.Equal(14, tiered.Passes[1].Found.Count);
        Assert.All(tiered.Passes, pass => Assert.Equal(Sts2SpineGeoClipSweep.TierNarrow, pass.Tier));

        // Window A is the whole saving, and it is a real one: 10 035 of its 38 579.
        Assert.Equal(10_035, tiered.Passes[0].Probed);
        Assert.Equal(70_200, tiered.Passes[1].Probed);
    }

    // THE REGRESSION THIS ROUND IS ABOUT, as a single number. Bounding window B too made tier one find 31 of 44,
    // fire the wide tier, and pay for all four passes: 108 779 wide candidates on top of 39 363 narrow probes.
    // Every one of the round's 20 live arms did exactly this, on both rigs.
    [Fact]
    public void BoundingWindowBToo_MakesTheWideTierUnavoidableAndTheWholeAcquisitionPureCost()
    {
        var windowA = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var windowB = MerchantWindowB() with { CoreBandBoundsSweep = true };
        var space = new IdSpace(MerchantMeshesClean, MerchantWindowBMeshes);

        var tiered = Tiered([windowA, windowB], expectedRunLength: 44, space);

        Assert.Equal(31, tiered.FoundAfterNarrow);
        Assert.True(tiered.WideRan);
        Assert.Equal(4, tiered.Passes.Count);

        // 148 142 probes to answer a question that 80 235 answers, and the extra 67 907 buy nothing but the 13
        // meshes tier one had just thrown away.
        Assert.Equal(39_363, tiered.NarrowProbed);
        Assert.Equal(148_142, tiered.Passes.Sum(pass => pass.Probed));
        Assert.Equal(44, tiered.Passes.Sum(pass => pass.Found.Count) - tiered.FoundAfterNarrow);
    }

    // ── (b) The slack ring, and the wide tier that covers it ─────────────────────────────────────────

    // THE CORRECTNESS RULE for the tiers. A mesh parked in the slack ring — outside the bracket band, inside the
    // slacked plan — is invisible to the narrow tier BY CONSTRUCTION. What makes narrowing safe is not that this
    // cannot happen; it is that the total comes up short, the wide tier fires, and the mesh is found.
    [Fact]
    public void WideTier_FiresOnASlackRingMeshTheNarrowTierCannotSeeAndFindsIt()
    {
        // Index 140: above the merchant bracket's own high index of 131, inside the slacked axis (23..195).
        var planted = (V: 16_632u, I: 140u);
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var space = new IdSpace(MerchantMeshesClean, [planted]);

        var tiered = Tiered([window], expectedRunLength: MerchantMeshesClean.Length + 1, space);

        Assert.True(tiered.WideRan);
        Assert.Equal(30, tiered.FoundAfterNarrow);
        Assert.Equal(2, tiered.Passes.Count);

        var narrow = tiered.Passes[0];
        var wide = tiered.Passes[1];
        Assert.Equal(Sts2SpineGeoClipSweep.TierNarrow, narrow.Tier);
        Assert.Equal(Sts2SpineGeoClipSweep.TierWide, wide.Tier);
        Assert.DoesNotContain(Rid(planted.V, planted.I), narrow.Found);
        Assert.Contains(Rid(planted.V, planted.I), wide.Found);
        Assert.Equal(31, wide.Found.Count);

        // The wide tier is the pre-tier sweep verbatim: the window's OWN plan, every candidate in it.
        Assert.Same(window.Plan, wide.Plan);
        Assert.Equal(window.Plan.TotalCandidates, wide.Probed);
        Assert.Equal(10_035 + 38_579, narrow.Probed + wide.Probed);
    }

    // The shortfall is graded on the TOTAL across windows, not per window — neither window knows how the rig's
    // meshes are split between them. A rig that is complete only once both windows are counted must therefore
    // NOT arm the wide tier, and one that is short by a single mesh must.
    [Theory]
    [InlineData(44, false)]
    [InlineData(43, false)]
    [InlineData(45, true)]
    public void WideTier_IsArmedOffTheTotalAcrossBothWindows(int expectedRunLength, bool wideExpected)
    {
        var space = new IdSpace(MerchantMeshesClean, MerchantWindowBMeshes);

        var tiered = Tiered(
            [WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid), MerchantWindowB()],
            expectedRunLength,
            space);

        Assert.Equal(wideExpected, tiered.WideRan);

        // THREE ORDINARY passes when the wide tier fires, not four: window B was already swept at full width in
        // tier one, so its wide pass would be a verbatim re-run of the same plan against the same predicate.
        // Window A, which WAS narrowed, is re-swept.
        //
        // Plus THREE index-recovery strips, and only in the `45` row — the one where the ordinary passes end
        // SHORT of the expectation and no strip can close it, so the driver runs every one it has. Window A
        // carries both halves of the recovery tier (its floor, [0, 23), and its ceiling, 196..341); window B
        // carries only a ceiling, because its ordinary plan is already floored at index 0. Its SENTINEL term
        // adds nothing on this bracket — the post endpoint (160) already sits above the mid endpoint's index
        // (131) — so its 225..329 strip is the budgeted reach and nothing else.
        var ordinary = tiered.Passes
            .Where(pass => pass.Tier != Sts2SpineGeoClipSweep.TierIndexRecovery)
            .ToList();
        var recovery = tiered.Passes
            .Where(pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery)
            .ToList();
        Assert.Equal(wideExpected ? 3 : 2, ordinary.Count);
        Assert.Equal(wideExpected ? 3 : 0, recovery.Count);
        if (wideExpected)
        {
            Assert.Equal(
                [("A", 0u, 22u), ("A", 196u, 341u), ("B", 225u, 329u)],
                recovery.Select(pass => (pass.WindowName, pass.Plan.IndexLow, pass.Plan.IndexHigh)).ToArray());
        }
        Assert.DoesNotContain(
            tiered.Passes,
            pass => pass.Tier == Sts2SpineGeoClipSweep.TierWide
                && pass.WindowName == Sts2SpineGeoClipSweep.WindowBName);
    }

    // The skip is an IDENTITY, not a guess, so it must not fire on a window the narrow tier really did narrow —
    // there the wide pass is the only thing that covers the slack ring.
    [Fact]
    public void WideTier_StillReSweepsAWindowTheNarrowTierActuallyNarrowed()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var space = new IdSpace(MerchantMeshesClean);

        var tiered = Tiered([window], expectedRunLength: 31, space);

        Assert.True(tiered.WideRan);
        var wide = Assert.Single(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierWide);
        Assert.Same(window.Plan, wide.Plan);
    }

    [Fact]
    public void IndexRecovery_RunsAfterEveryOrdinaryPassAndRecoversTheConstructedIndex114Target()
    {
        var windowA = Sts2SpineGeoClipSweep.PlanWindowA(Rid(132, 80), Rid(135, 125), 64, 50_000);
        var windowB = Sts2SpineGeoClipSweep.PlanWindowB(Rid(135, 125), Rid(140, 46), 64, 50_000, null);
        var originalA = Rid(134, 90);
        var originalB = Rid(137, 45);
        var target = Rid(136, 114);
        var beyondRecoveryEndpoint = Rid(136, 190);

        var tiered = Tiered(
            [windowA, windowB],
            expectedRunLength: 3,
            new IdSpace([(134u, 90u), (137u, 45u), (136u, 114u), (136u, 190u)]));

        // SIX passes: three ordinary, then the recovery tier's strips in window order — window A's FLOOR
        // (0 .. its hull's low edge, exclusive), window A's CEILING, then window B's ceiling. Window A's two find
        // nothing here (this space's ids sit above index 16 and outside window A's validator span above it),
        // which is the point of asserting them: they are added coverage, they do not displace or reorder B's.
        Assert.Equal([Sts2SpineGeoClipSweep.TierNarrow, Sts2SpineGeoClipSweep.TierNarrow,
            Sts2SpineGeoClipSweep.TierWide, Sts2SpineGeoClipSweep.TierIndexRecovery,
            Sts2SpineGeoClipSweep.TierIndexRecovery, Sts2SpineGeoClipSweep.TierIndexRecovery],
            tiered.Passes.Select(pass => pass.Tier));
        Assert.Equal(2, tiered.FoundAfterNarrow);
        Assert.DoesNotContain(target, tiered.Passes.Take(3).SelectMany(pass => pass.Found));

        var floor = tiered.Passes[3];
        Assert.Equal(Sts2SpineGeoClipSweep.WindowAName, floor.WindowName);
        Assert.Equal((0u, 15u), (floor.Plan.IndexLow, floor.Plan.IndexHigh));
        Assert.Equal(64, floor.Probed);
        Assert.Empty(floor.Found);

        // Window A's ceiling: four validators, so the budget affords the full 1 024-column reach above its hull
        // top of 189.
        var windowACeiling = tiered.Passes[4];
        Assert.Equal(Sts2SpineGeoClipSweep.WindowAName, windowACeiling.WindowName);
        Assert.Equal((190u, 1_213u), (windowACeiling.Plan.IndexLow, windowACeiling.Plan.IndexHigh));
        Assert.Equal(4 * 1_024, windowACeiling.Probed);
        Assert.Empty(windowACeiling.Found);

        // Window B's ceiling SUBSUMES the complement it replaces. The old strip stopped at the discarded middle
        // endpoint plus slack (189) and left (136, 190) — one index past it — unreachable; the budgeted reach
        // carries the same strip to 1 134 and picks it up. Both the constructed target at 114 and the id the old
        // boundary excluded are recovered by the same pass.
        var recovery = tiered.Passes[5];
        Assert.Equal(Sts2SpineGeoClipSweep.WindowBName, recovery.WindowName);
        Assert.Equal((111u, 1_134u), (recovery.Plan.IndexLow, recovery.Plan.IndexHigh));
        Assert.Equal(6 * 1_024, recovery.Probed);
        // 1 546 ordinary + 64 floor + 4 096 window-A ceiling + 6 144 window-B ceiling. The recorded pre-ceiling
        // total was 2 084 (1 546 + 64 + the old 474-candidate complement).
        Assert.Equal(1_546 + 64 + (4 * 1_024) + (6 * 1_024), tiered.Passes.Sum(pass => pass.Probed));
        Assert.Equal([target, beyondRecoveryEndpoint], recovery.Found);
        Assert.Contains(originalA, tiered.Passes.SelectMany(pass => pass.Found));
        Assert.Contains(originalB, tiered.Passes.SelectMany(pass => pass.Found));
    }

    [Fact]
    public void IndexRecovery_DoesNotRunWhenNarrowPassesAlreadyCoverTheExpectedCount()
    {
        var windowB = Sts2SpineGeoClipSweep.PlanWindowB(Rid(135, 125), Rid(140, 46), 64, 50_000, null);
        var tiered = Tiered([windowB], 1, new IdSpace([(137u, 45u)]));

        Assert.DoesNotContain(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery);
    }

    [Fact]
    public void IndexRecovery_DoesNotRunWhenAnOrdinaryWidePassCompletesTheResult()
    {
        var windowA = Sts2SpineGeoClipSweep.PlanWindowA(Rid(132, 80), Rid(135, 125), 64, 50_000);
        var windowB = Sts2SpineGeoClipSweep.PlanWindowB(Rid(135, 125), Rid(140, 46), 64, 50_000, null);
        var tiered = Tiered(
            [windowA, windowB],
            3,
            new IdSpace([(134u, 90u), (134u, 150u), (137u, 45u)]));

        Assert.Contains(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierWide);
        Assert.DoesNotContain(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery);
        Assert.Equal(2, tiered.FoundAfterNarrow);
    }

    // The SENTINEL half of the ceiling — the strip window B has carried since 6155552d, which reaches the
    // discarded middle endpoint when it sat above the post endpoint. It is kept OUTSIDE the ceiling's kill switch
    // so that disarming the ceiling restores exactly the window that shipped, and this is where that is pinned:
    // on a bracket whose post endpoint already covers the middle one, a disarmed ceiling plans nothing at all.
    [Fact]
    public void PlanWindowB_HasNoRecoveryStripWhenPostEndpointAlreadyCoversTheMiddleEndpointAndTheCeilingIsDown()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            Rid(10, 40), Rid(20, 80), 64, 50_000, null, indexCeilingRecovery: false);

        Assert.Null(window.IndexCeilingRecoveryPlan);
        Assert.Null(window.IndexFloorRecoveryPlan);
    }

    [Fact]
    public void PlanIndexCeilingRecovery_SaturatesEndpointAndDoesNotWrapItsStart()
    {
        var ordinary = Sts2SpineGeometryMath.PlanRidCandidates(
            Rid(10, uint.MaxValue - 3), Rid(11, uint.MaxValue - 2), 0, 50_000);

        var recovery = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(
            Sts2SpineGeoClipSweep.PlanIndexCeilingRecovery(ordinary, uint.MaxValue, int.MaxValue));

        Assert.Equal(uint.MaxValue - 1, recovery.IndexLow);
        Assert.Equal(uint.MaxValue, recovery.IndexHigh);
    }

    [Fact]
    public void PlanIndexCeilingRecovery_HasNoStripWhenTheOrdinaryPlanAlreadyEndsAtUintMax()
    {
        var ordinary = Sts2SpineGeometryMath.PlanRidCandidates(
            Rid(10, uint.MaxValue), Rid(11, uint.MaxValue), 0, 50_000);

        Assert.Null(Sts2SpineGeoClipSweep.PlanIndexCeilingRecovery(ordinary, uint.MaxValue, 64));
    }

    [Fact]
    public void PlanWindowB_LeavesTheOriginalPrefixExactAndCapsTheComplementWithTheChosenExplicitCap()
    {
        var mid = Rid(135, 125);
        var post = Rid(140, 46);
        var window = Sts2SpineGeoClipSweep.PlanWindowB(mid, post, 64, 5, envCap: 5);
        var original = Sts2SpineGeometryMath.PlanRidCandidates(Rid(135, 0), post, 64, 5);

        Assert.Equal(original, window.Plan);
        Assert.Equal(
            Sts2SpineGeometryMath.EnumerateRidCandidates(original),
            Sts2SpineGeometryMath.EnumerateRidCandidates(window.Plan));
        // The budgeted reach cannot afford even one column under a cap of 5, so what is left is the SENTINEL
        // term — the pre-fix complement, 111..189 — exactly as it was.
        var recovery = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(
            window.IndexCeilingRecoveryPlan);
        Assert.Equal(window.Plan.Cap, recovery.Cap);
        Assert.True(recovery.Truncated);
        Assert.Equal(5, recovery.EmittedCount);
        Assert.Equal(111u, recovery.IndexLow);
        Assert.Equal(189u, recovery.IndexHigh);
    }

    [Fact]
    public void IndexRecovery_DeduplicatesItsFindsForTheFallbackGradeWithoutChangingMergeOrder()
    {
        var windowB = Sts2SpineGeoClipSweep.PlanWindowB(Rid(135, 125), Rid(140, 46), 64, 50_000, null);
        var duplicate = Rid(136, 114);
        var tiered = Tiered([windowB], 2, new IdSpace([(136u, 114u)]), [duplicate]);

        var recovery = Assert.Single(
            tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery);
        Assert.Equal([duplicate], recovery.Found);
        var merged = Sts2SpineGeoClipSweep.MergeWindows([duplicate], recovery.Found, 10);
        Assert.Equal([duplicate], merged.Merged);
        Assert.Equal(0, merged.Dropped);
    }

    [Fact]
    public void IndexRecovery_UsesAnIndependentCappedPrefixWithoutDisplacingTheOrdinaryPrefix()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowB(
            Rid(135, 125), Rid(140, 46), 64, configuredCap: 50_000, envCap: 112);
        var original = Rid(136, 0);
        var recovered = Rid(136, 114);
        var beyondRecoveryCap = Rid(139, 114);
        var space = new IdSpace([(136u, 0u), (136u, 114u), (139u, 114u)]);
        var yields = 0;

        var tiered = Sts2SpineGeoClipSweep.AcquireTieredAsync(
                [window],
                expectedRunLength: 3,
                foundBeforeTiers: [],
                (_, _) => space.Validate,
                () =>
                {
                    yields += 1;
                    return Task.CompletedTask;
                },
                chunkSize: 64)
            .GetAwaiter()
            .GetResult();

        Assert.Equal(2, tiered.Passes.Count);
        var ordinary = tiered.Passes[0];
        var recovery = tiered.Passes[1];
        Assert.Equal(Sts2SpineGeoClipSweep.TierNarrow, ordinary.Tier);
        Assert.Equal(Sts2SpineGeoClipSweep.TierIndexRecovery, recovery.Tier);
        Assert.Equal(112, ordinary.Probed);
        Assert.Equal(
            Sts2SpineGeometryMath.EnumerateRidCandidates(window.Plan),
            space.Observed.Take(112));
        Assert.Equal([original], ordinary.Found);
        Assert.Equal(112, recovery.Plan.Cap);
        Assert.True(recovery.Plan.Truncated);
        Assert.Equal(112, recovery.Probed);
        Assert.Equal([recovered], recovery.Found);
        Assert.DoesNotContain(beyondRecoveryCap, tiered.Passes.SelectMany(pass => pass.Found));
        Assert.Equal(224, space.Probes);
        Assert.Equal(2, yields);
    }

    [Fact]
    public void TierName_AppendsTheDistinctRecoveryTierWithoutClaimingAnOrdinaryWidePassRan()
    {
        Assert.Equal(
            "narrow+index-recovery",
            Sts2SpineGeoClipSweep.TierName(narrowRan: true, wideRan: false, indexRecoveryRan: true));
    }

    // ── The walk arm's finds count ONCE ──────────────────────────────────────────────────────────────
    //
    // The two arms are graded together, and they SWEEP THE SAME IDS: the walk's column order starts at
    // `window.CoreIndexLow` (Sts2SpineGeoClipWalk.PlanColumnOrder) and the narrow tier sweeps exactly that core
    // band (NarrowToCoreBand). So the grade has to be on a UNION of RID ids. Grading it on a SUM of the two arms'
    // counts let one set of meshes satisfy the slot count twice, skip the wide sweep, and ship a silently
    // incomplete artifact — Phase 1's failure with a different cause. Both directions are asserted, because
    // either alone is satisfiable by a broken rule: a gate that ignores the walk entirely passes (a), and a gate
    // that adds the counts passes (b).

    // (a) OVERLAP. The walk carried 14 of the merchant's window-A meshes and the narrow tier re-finds all 30,
    // including those 14. The rig holds THIRTY distinct meshes against 44 slots, so the wide sweep must fire.
    [Fact]
    public void WideTier_FiresWhenTheWalksFindsAreTheSameMeshesTheNarrowTierReFinds()
    {
        var windows = new[] { WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid) };
        var walked = MerchantMeshesClean.Take(14).Select(mesh => Rid(mesh.V, mesh.I)).ToArray();

        // The control: with no walk arm at all, 30 against 44 slots arms the wide sweep.
        Assert.True(Tiered(windows, 44, new IdSpace(MerchantMeshesClean)).WideRan);

        var tiered = Tiered(windows, 44, new IdSpace(MerchantMeshesClean), walked);

        // The overlap is the premise, so it is asserted rather than assumed: every id the walk delivered is an id
        // the narrow pass found again by itself.
        var narrow = Assert.Single(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierNarrow);
        Assert.All(walked, id => Assert.Contains(id, narrow.Found));

        Assert.Equal(30, tiered.FoundAfterNarrow);
        Assert.True(
            tiered.WideRan,
            $"the acquisition holds 30 distinct meshes against 44 slots but graded itself at "
            + $"{tiered.FoundAfterNarrow} and skipped the wide sweep, which ships a silently incomplete rig");
    }

    // (b) DISJOINT. The walk carried window B's meshes, which the narrow tier's window-A pass cannot reach at all
    // (they sit above window A's validator range). Those 14 are real, additional coverage: 14 + 30 = 44 distinct
    // meshes against 44 slots, and the wide sweep must NOT be dragged in behind them. This is the case the gate
    // exists for — the shortfall is only observable in the total, because no window knows how the rig is split.
    [Fact]
    public void WideTier_CountsWalkFindsTheNarrowTierCannotReachTowardsTheSameTotal()
    {
        var windows = new[] { WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid) };
        var walked = MerchantWindowBMeshes.Select(mesh => Rid(mesh.V, mesh.I)).ToArray();

        var tiered = Tiered(windows, 44, new IdSpace(MerchantMeshesClean), walked);

        // Disjointness is the premise here, so it is asserted too.
        var narrow = Assert.Single(tiered.Passes, pass => pass.Tier == Sts2SpineGeoClipSweep.TierNarrow);
        Assert.All(walked, id => Assert.DoesNotContain(id, narrow.Found));

        Assert.Equal(14, walked.Length);
        Assert.Equal(44, tiered.FoundAfterNarrow);
        Assert.False(
            tiered.WideRan,
            $"the walk delivered 14 meshes the narrow tier cannot see and the narrow tier found 30 more, but the "
            + $"grade came to {tiered.FoundAfterNarrow} and swept the whole slacked plan again for nothing");
    }

    // A duplicate in the walk's own answer cannot inflate the grade either — the accumulator is a set, so the
    // caller is free to hand over an unordered sequence with repeats.
    [Fact]
    public void WideTier_IsNotFooledByRepeatsInWhatTheWalkArmHandsOver()
    {
        var windows = new[] { WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid) };
        var walked = MerchantWindowBMeshes
            .Concat(MerchantWindowBMeshes)
            .Select(mesh => Rid(mesh.V, mesh.I))
            .ToArray();

        var tiered = Tiered(windows, 45, new IdSpace(MerchantMeshesClean), walked);

        Assert.Equal(28, walked.Length);
        Assert.Equal(44, tiered.FoundAfterNarrow);
        Assert.True(tiered.WideRan);
    }

    // THE SEMANTICS THE BAKER'S CALL SITE RELIES ON, pinned in one place and in BOTH directions.
    //
    // RunDenseTiersAsync is handed the walk arm's merged ids as `walkedBeforeTiers`. Writing `[]` there compiles
    // and no offline lane catches it — validate.sh bridge-tests does not compile the baker, and the walk is only
    // ever armed by an explicit kill-switch override, never by default. What keeps that from being a
    // correctness bug is the direction of the error: the seed can only ever make the gate MORE conservative, so
    // dropping it costs a wide pass and never a mesh.
    //
    // Both halves are asserted because either alone is satisfiable by a broken driver: an acquisition that
    // ignores its seed outright passes the "widens without it" half, and one that always skips the wide tier
    // passes the other.
    [Fact]
    public void AcquireTiered_GradesItsSeedIntoTheTotalSoDroppingItCostsAWideTierAndNeverAMesh()
    {
        var windows = new[] { WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid) };

        // A seed sufficient to satisfy the target: 14 window-B ids the window-A narrow pass cannot reach, plus
        // the 30 it does find, is 44 against 44 slots.
        var seed = MerchantWindowBMeshes.Select(mesh => Rid(mesh.V, mesh.I)).ToArray();

        var seeded = Tiered(windows, 44, new IdSpace(MerchantMeshesClean), seed);
        Assert.Equal(44, seeded.FoundAfterNarrow);
        Assert.False(
            seeded.WideRan,
            "the seed satisfied the slot count and the wide tier widened anyway, which is a sweep of the whole "
            + "slacked plan bought for nothing");
        Assert.Single(seeded.Passes);

        // The same acquisition with the seed dropped — exactly what `walkedBeforeTiers: []` would do.
        var unseeded = Tiered(windows, 44, new IdSpace(MerchantMeshesClean), []);
        Assert.Equal(30, unseeded.FoundAfterNarrow);
        Assert.True(
            unseeded.WideRan,
            "the acquisition graded itself complete on 30 meshes against 44 slots without any seed to make up "
            + "the difference, which ships a silently incomplete rig");

        // …and the cost really is a pass and not a mesh: both runs end holding the same ids.
        Assert.Equal(
            seeded.Passes.SelectMany(pass => pass.Found).Distinct().OrderBy(id => id).ToArray(),
            unseeded.Passes.SelectMany(pass => pass.Found).Distinct().OrderBy(id => id).ToArray());
    }

    // ── (c) Window B's floor at zero ─────────────────────────────────────────────────────────────────

    // Window B floors its index axis at ZERO because its meshes come out of a different owner's index pool than
    // the probe meshes that bound it, and nothing recorded says where they land. The narrow tier used to raise
    // that floor back to the mid bracket's index. Live, the [0, mid) region it gave up is where almost all of
    // window B's meshes actually are, so window B is no longer narrowed at all.
    [Fact]
    public void NarrowTier_SweepsWindowBAtFullWidthBecauseItsCoreBandIsOnlyAnOrderingHint()
    {
        var window = MerchantWindowB();

        Assert.False(
            window.CoreBandBoundsSweep,
            "window B must not claim its core band bounds a dense pass — its index axis is floored at zero "
            + "precisely because nothing predicts where its meshes land");

        // The hint itself is unchanged: the walk arm still starts its column scan at the mid bracket index.
        Assert.Equal(MerchantMidBracketIndex, window.CoreIndexLow);
        Assert.Same(window, Sts2SpineGeoClipSweep.NarrowToCoreBand(window));

        var space = new IdSpace(MerchantWindowBMeshes);
        var tiered = Tiered([window], expectedRunLength: 14, space);

        var narrow = Assert.Single(tiered.Passes);
        Assert.Equal(Sts2SpineGeoClipSweep.TierNarrow, narrow.Tier);
        Assert.Equal(0u, narrow.Plan.IndexLow);
        Assert.Equal(14, narrow.Found.Count);
        Assert.Equal(70_200, narrow.Probed);
        Assert.False(
            tiered.WideRan,
            "window B swept at full width found all 14, so nothing is left for a second pass to discover");
    }

    // THE DEFECT ITSELF, pinned so it cannot come back. Bounding window B on its core band is not a lost saving,
    // it is a lost MESH: it throws away everything below the mid bracket index, which live is 13 of the
    // merchant's 14 and both of byrdonis's 2. Asserted against the band directly rather than through the tier
    // driver, so it still reads as a statement about the band once the driver stops using it.
    [Fact]
    public void WindowBsCoreBand_WouldLoseThirteenOfTheMerchantsFourteenMeshesIfItBoundedTheSweep()
    {
        var window = MerchantWindowB();
        var ifItBounded = window with { CoreBandBoundsSweep = true };

        var bounded = Sts2SpineGeoClipSweep.NarrowToCoreBand(ifItBounded).Plan;
        Assert.Equal(MerchantMidBracketIndex, bounded.IndexLow);

        var lost = MerchantWindowBMeshes.Where(mesh => mesh.I < bounded.IndexLow).ToArray();
        var kept = MerchantWindowBMeshes.Where(mesh => mesh.I >= bounded.IndexLow).ToArray();

        Assert.Equal(13, lost.Length);
        Assert.Single(kept);

        // And the tier driver would have graded itself on that 1 and swept everything twice — which is exactly
        // what 62 of the round's 65 recorded live bakes did.
        var space = new IdSpace(MerchantWindowBMeshes);
        var tiered = Tiered([ifItBounded], expectedRunLength: 14, space);

        Assert.Equal(1, tiered.FoundAfterNarrow);
        Assert.True(tiered.WideRan);
        Assert.Equal(2, tiered.Passes.Count);
    }

    // The saving the narrow tier is allowed to keep, on the window that earned it. Window A's band is a
    // containment claim backed by recorded ids, so it still bounds tier one.
    [Fact]
    public void WindowAsCoreBand_StillBoundsTheSweepBecauseItsContainmentClaimIsRecorded()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);

        Assert.True(window.CoreBandBoundsSweep);
        Assert.NotSame(window, Sts2SpineGeoClipSweep.NarrowToCoreBand(window));
        Assert.Equal(87u, Sts2SpineGeoClipSweep.NarrowToCoreBand(window).Plan.IndexLow);
    }

    // ── What the narrowing itself does, and does not, change ─────────────────────────────────────────

    [Fact]
    public void NarrowToCoreBand_KeepsTheWholeValidatorSpanAndOnlyNarrowsTheIndexAxis()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);

        var narrow = Sts2SpineGeoClipSweep.NarrowToCoreBand(window);

        Assert.Equal(window.Plan.ValidatorLow, narrow.Plan.ValidatorLow);
        Assert.Equal(window.Plan.ValidatorHigh, narrow.Plan.ValidatorHigh);
        Assert.Equal(87u, narrow.Plan.IndexLow);
        Assert.Equal(131u, narrow.Plan.IndexHigh);
        Assert.Equal(window.Name, narrow.Name);
        Assert.Equal(window.CoreIndexLow, narrow.CoreIndexLow);
        Assert.Equal(window.CoreIndexHigh, narrow.CoreIndexHigh);
        Assert.Equal(window.Plan.Cap, narrow.Plan.Cap);
        Assert.False(narrow.Plan.Truncated);
    }

    // A window whose core band already spans its whole axis has nothing to narrow, and must not be handed back a
    // differently-shaped copy of itself — the wide tier compares against the window's own plan.
    [Fact]
    public void NarrowToCoreBand_ReturnsTheWindowUnchangedWhenThereIsNoSlackToDrop()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(
            Rid(16_415, 87), Rid(16_637, 131), indexSlack: 0, cap: 50_000);

        Assert.Same(window, Sts2SpineGeoClipSweep.NarrowToCoreBand(window));
    }

    [Theory]
    [InlineData(true, false, "narrow")]
    [InlineData(true, true, "narrow-then-wide")]
    [InlineData(false, true, "wide")]
    [InlineData(false, false, "none")]
    public void TierName_NamesEveryCombinationTheReportCanCarry(bool narrowRan, bool wideRan, string expected)
        => Assert.Equal(expected, Sts2SpineGeoClipSweep.TierName(narrowRan, wideRan));

    // The tiers are graded by the SAME predicate the walk arm is, not by a second copy of the rule that could
    // drift away from it.
    [Theory]
    [InlineData(30, 30, false)]
    [InlineData(29, 30, true)]
    [InlineData(0, 30, true)]
    public void WideTier_UsesTheWalkArmsOwnShortfallRule(int meshesPresent, int expectedRunLength, bool expected)
    {
        var space = new IdSpace(MerchantMeshesClean.Take(meshesPresent));

        var tiered = Tiered(
            [WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid)], expectedRunLength, space);

        Assert.Equal(expected, tiered.WideRan);
        Assert.Equal(
            expected,
            Sts2SpineGeoClipWalk.DenseSweepRequired(true, tiered.FoundAfterNarrow, expectedRunLength));
    }

    // The frame yield is what keeps the sweep from freezing the host, so it has to fire on the tier driver's own
    // probe count — once per chunk, on both tiers.
    [Fact]
    public async Task AcquireTiered_YieldsAFrameEveryChunkOfProbesOnEveryTier()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);
        var space = new IdSpace(MerchantMeshesClean);
        var yields = 0;

        var tiered = await Sts2SpineGeoClipSweep.AcquireTieredAsync(
            [window],
            expectedRunLength: 44,
            foundBeforeTiers: [],
            (_, _) => space.Validate,
            () =>
            {
                yields += 1;
                return Task.CompletedTask;
            },
            chunkSize: 8_192);

        Assert.True(tiered.WideRan);

        // Every tier, INCLUDING both recovery strips — this space holds 30 of the 44 expected meshes, so the
        // recovery tier runs and its passes are exactly the ones long enough to freeze a host if they did not
        // yield. Window A's floor is 223 validators × 23 columns and its ceiling 223 × 146, the budgeted reach.
        Assert.Equal(
            (10_035 / 8_192) + (38_579 / 8_192) + (5_129 / 8_192) + (32_558 / 8_192),
            yields);
    }

    // ── (d) WINDOW A'S INDEX FLOOR: the post-death free-list defect, offline ──────────────────────────
    //
    // THE DEFECT. Window A's index axis is the two bracket sentinels' own index range padded by IndexSlack (64),
    // and that hull is an INFERENCE. The validator axis is not — a validator comes from one process-global
    // monotonic counter, so every RID minted inside the bracket has one inside the span. The index comes from the
    // per-type RID allocator, and the hull holds only while that allocator is BUMP-allocating. A creature death
    // anywhere earlier in the session frees mesh RIDs; later allocations are then served out of order, the rig's
    // own meshes land at recycled indices outside the hull, and they are NEVER PROBED. `meshesValidated` falls
    // below `slotsEverVisible`, `unassociated` is DEFINED as the difference, and the bake refuses with no
    // association fault in it at all.
    //
    // Round WS-I, `.sts2/research/data/geoclip-driven-combat-20260909T002811Z/`. Ten single-variable arms, a
    // virgin process each: idle, 180 s elapsed, a mirror client attached, eight concurrent /spines/ bakes and two
    // full rounds of card play with NO deaths all bake 4 of 4 rigs complete; two kills alone refuse 3 of 4.
    // Pausing the scene tree for the whole bake — which stops every foreign RID allocation inside the bracket —
    // changes nothing (40/23/17/10 paused against 40/23/17/10 unpaused), because the damage is in the allocator
    // before the bake starts.
    //
    // ── WHAT THE FIXTURES BELOW ARE, EXACTLY ────────────────────────────────────────────────────────
    //
    // The four id spaces are CALIBRATED, not dumped: the baker never dumped a post-death mesh id, and this test
    // may not run the game. What IS recorded, per rig, is the arm's own sweep-plan line — the validator span, the
    // core band, the slacked hull, and how many meshes each tier found — and every one of those bands is
    // reproduced here by the PRODUCTION planner from the recorded bracket sentinels, not restated. That is what
    // the `total=` assertions are for: PlanWindowA fed these sentinels reproduces 2 752 / 13 760 / 101 910
    // candidates for magi and 595 / 15 827 / 137 683 for spectral, which are the logged numbers to the unit, in
    // three independent bands each. A wrong reconstruction cannot hit twelve totals by accident.
    //
    // THE ONE THING THE FIXTURES ASSUME is WHERE the missing meshes sit: below the hull's floor rather than above
    // its top. That is the assumption the fix turns on, so it does not get to be assumed quietly — a window-B
    // fixture in this same file was once built out of the claim it was testing and passed while the shipped code
    // lost 13 of the merchant's 14 meshes. It is asserted here because it is MEASURED elsewhere, by four
    // independent controls in the same round: raising window B's index top from ~165–225 to ~1123–1185 across all
    // four rigs (arm SlackA against arm SlackB, same process state) found not ONE extra mesh, and extending
    // window A's own top from 196 to 288 on spectral (arm SlackA against arm Dq) found none either. The high side
    // is empty; the low side is where a free list hands slots back. `WindowAIndexFloor_DoesNotReachAboveTheHull`
    // below pins the limit that leaves in place, so the boundary is stated rather than hidden.

    /// <summary>A scattered post-death run: ascending validators, the recorded index distribution.</summary>
    private static (uint V, uint I)[] Scatter(uint validatorStart, uint validatorStride, params uint[] indices)
        => [.. indices.Select((index, k) => (validatorStart + ((uint)k * validatorStride), index))];

    // ironclad, arm SlackA — narrow 20, wide 30, window B 14, total 44 of 44. COMPLETE post-death: a free list
    // is active in this very process and this rig's hull still happens to cover its meshes. The control that
    // makes the fix's inertness gradeable.
    private static readonly ulong IroncladPostDeathLow = Rid(36_114, 103);
    private static readonly ulong IroncladPostDeathMid = Rid(36_334, 181);
    private static readonly ulong IroncladPostDeathPost = Rid(36_405, 105);

    private static readonly (uint V, uint I)[] IroncladPostDeathWindowA = Scatter(
        36_116, 7,
        // 20 inside the core band 103..181 — what the narrow tier found.
        104, 107, 111, 116, 120, 124, 129, 133, 138, 142, 147, 151, 156, 160, 165, 169, 173, 176, 179, 181,
        // 10 more inside the slacked hull 39..245 — what the wide tier added.
        41, 49, 57, 64, 72, 88, 196, 209, 223, 240);

    private static readonly (uint V, uint I)[] IroncladPostDeathWindowB = Scatter(
        36_336, 4, 3, 11, 19, 27, 35, 44, 52, 60, 68, 77, 85, 93, 101, 110);

    // flail_knight, arm SlackA — narrow 19, wide 37, window B 0, total 37 of 37. The second complete control.
    private static readonly ulong FlailPostDeathLow = Rid(36_492, 98);
    private static readonly ulong FlailPostDeathMid = Rid(36_682, 180);
    private static readonly ulong FlailPostDeathPost = Rid(36_683, 99);

    private static readonly (uint V, uint I)[] FlailPostDeathWindowA = Scatter(
        36_494, 5,
        99, 103, 107, 112, 116, 121, 125, 130, 134, 139, 143, 148, 152, 157, 161, 166, 170, 175, 180,
        36, 42, 48, 54, 60, 66, 72, 78, 84, 90, 190, 198, 206, 214, 222, 230, 238, 244);

    // spectral_knight, arm SlackA — narrow 3, wide 16, window B 1, total 17 of 22. REFUSES. Arm SlackB, the same
    // post-death state at IndexSlack 1024, found 21 on window A and admitted 22 of 22.
    private static readonly ulong SpectralPostDeathLow = Rid(36_756, 128);
    private static readonly ulong SpectralPostDeathMid = Rid(36_874, 132);
    private static readonly ulong SpectralPostDeathPost = Rid(36_881, 133);

    private static readonly (uint V, uint I)[] SpectralPostDeathWindowA = Scatter(
        36_758, 5,
        // 3 inside the collapsed core band 128..132.
        128, 130, 132,
        // 13 more inside the slacked hull 64..196.
        66, 71, 78, 85, 91, 99, 108, 115, 122, 140, 155, 168, 183,
        // 5 BELOW the hull's floor of 64 — recycled slots, never probed at IndexSlack 64.
        7, 19, 28, 41, 55);

    private static readonly (uint V, uint I)[] SpectralPostDeathWindowB = [(36_877, 144)];

    // magi_knight, arm SlackA — narrow 7, wide 10, window B 0, total 10 of 16. REFUSES, and reproduces to the
    // unit in SIX independent virgin processes (Dkill, Dq, Dq2, DqPause, DqElide0, DqLease0) plus this one: a
    // free list is a deterministic structure, so replaying the same frees gives the same answer.
    private static readonly ulong MagiPostDeathLow = Rid(36_980, 129);
    private static readonly ulong MagiPostDeathMid = Rid(37_065, 160);
    private static readonly ulong MagiPostDeathPost = Rid(37_066, 161);

    private static readonly (uint V, uint I)[] MagiPostDeathWindowA = Scatter(
        36_982, 5,
        // 7 inside the core band 129..160.
        131, 136, 141, 147, 152, 156, 160,
        // 3 more inside the slacked hull 65..224.
        96, 118, 205,
        // 6 BELOW the hull's floor of 65.
        12, 27, 39, 44, 58, 63);

    private static (
        Sts2SpineGeoClipSweep.SweepWindow A,
        Sts2SpineGeoClipSweep.SweepWindow B) PostDeathWindows(
            ulong low,
            ulong mid,
            ulong post,
            bool indexFloorRecovery = Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryDefault,
            int indexSlack = 64)
        => (
            Sts2SpineGeoClipSweep.PlanWindowA(
                low, mid, indexSlack, cap: 50_000, indexFloorRecovery: indexFloorRecovery),
            Sts2SpineGeoClipSweep.PlanWindowB(mid, post, indexSlack, configuredCap: 50_000, envCap: null));

    // FIRST: the reconstruction is the recorded one. Every band and every candidate total below is the
    // production planner's answer to the recorded bracket sentinels, checked against the arm's own log line.
    [Theory]
    // rig, low, mid, post, narrow band + total, wide band + total, window B band + total, slack-1024 total
    [InlineData("ironclad", 36_114u, 103u, 36_334u, 181u, 36_405u, 105u, 103u, 181u, 17_459, 39u, 245u, 45_747,
        0u, 169u, 12_240)]
    [InlineData("flail_knight", 36_492u, 98u, 36_682u, 180u, 36_683u, 99u, 98u, 180u, 15_853, 34u, 244u, 40_301,
        0u, 163u, 328)]
    [InlineData("spectral_knight", 36_756u, 128u, 36_874u, 132u, 36_881u, 133u, 128u, 132u, 595, 64u, 196u,
        15_827, 0u, 197u, 1_584)]
    [InlineData("magi_knight", 36_980u, 129u, 37_065u, 160u, 37_066u, 161u, 129u, 160u, 2_752, 65u, 224u, 13_760,
        0u, 225u, 452)]
    public void PostDeathBrackets_ReproduceEveryRecordedSweepPlanBandAndCandidateTotal(
        string rig,
        uint lowValidator,
        uint lowIndex,
        uint midValidator,
        uint midIndex,
        uint postValidator,
        uint postIndex,
        uint narrowLow,
        uint narrowHigh,
        long narrowTotal,
        uint wideLow,
        uint wideHigh,
        long wideTotal,
        uint windowBLow,
        uint windowBHigh,
        long windowBTotal)
    {
        var (windowA, windowB) = PostDeathWindows(
            Rid(lowValidator, lowIndex), Rid(midValidator, midIndex), Rid(postValidator, postIndex));
        var narrow = Sts2SpineGeoClipSweep.NarrowToCoreBand(windowA).Plan;

        Assert.Equal(
            (rig, narrowLow, narrowHigh, narrowTotal),
            (rig, narrow.IndexLow, narrow.IndexHigh, narrow.TotalCandidates));
        Assert.Equal(
            (rig, wideLow, wideHigh, wideTotal),
            (rig, windowA.Plan.IndexLow, windowA.Plan.IndexHigh, windowA.Plan.TotalCandidates));
        Assert.Equal(
            (rig, windowBLow, windowBHigh, windowBTotal),
            (rig, windowB.Plan.IndexLow, windowB.Plan.IndexHigh, windowB.Plan.TotalCandidates));
    }

    // ── The defect itself, and the fix, on the same four id spaces ───────────────────────────────────

    /// <summary>One post-death acquisition: what it ended up holding, and what the floor strip cost and found.</summary>
    private sealed record PostDeathAcquisition(int Found, int FloorProbes, int FloorFound);

    private static PostDeathAcquisition PostDeathAcquire(
        ulong low,
        ulong mid,
        ulong post,
        int expected,
        IdSpace space,
        bool indexFloorRecovery = Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryDefault)
    {
        var (windowA, windowB) = PostDeathWindows(low, mid, post, indexFloorRecovery);
        var tiered = Tiered([windowA, windowB], expected, space);
        var floor = tiered.Passes
            .Where(pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery
                && pass.WindowName == Sts2SpineGeoClipSweep.WindowAName)
            .ToList();
        return new PostDeathAcquisition(
            tiered.Passes.SelectMany(pass => pass.Found).Distinct().Count(),
            (int)floor.Sum(pass => pass.Probed),
            floor.Sum(pass => pass.Found.Count));
    }

    // THE DEFECT, REPRODUCED. Switch the strip off and the acquisition is the shipped one, byte for byte: the
    // two rigs the round measured refusing come up exactly as short as they did live — 17 of 22 and 10 of 16 —
    // and the two it measured passing still pass, in the SAME post-death allocator state.
    [Fact]
    public void WindowAIndexFloorOff_ReproducesTheRecordedPostDeathShortfallOnExactlyTwoOfFourRigs()
    {
        Assert.Equal(
            (44, 37, 17, 10),
            (
                PostDeathAcquire(
                    IroncladPostDeathLow, IroncladPostDeathMid, IroncladPostDeathPost, 44,
                    new IdSpace(IroncladPostDeathWindowA, IroncladPostDeathWindowB),
                    indexFloorRecovery: false).Found,
                PostDeathAcquire(
                    FlailPostDeathLow, FlailPostDeathMid, FlailPostDeathPost, 37,
                    new IdSpace(FlailPostDeathWindowA), indexFloorRecovery: false).Found,
                PostDeathAcquire(
                    SpectralPostDeathLow, SpectralPostDeathMid, SpectralPostDeathPost, 22,
                    new IdSpace(SpectralPostDeathWindowA, SpectralPostDeathWindowB),
                    indexFloorRecovery: false).Found,
                PostDeathAcquire(
                    MagiPostDeathLow, MagiPostDeathMid, MagiPostDeathPost, 16,
                    new IdSpace(MagiPostDeathWindowA), indexFloorRecovery: false).Found));
    }

    // THE FIX. The same four spaces with the strip armed: both shortfalls close, and the two rigs that were
    // already complete are UNTOUCHED — no floor pass ran on them at all, because the ordinary tiers had already
    // covered their slots and the strip is gated on the shortfall, not on the allocator's state.
    [Fact]
    public void WindowAIndexFloor_ClosesBothPostDeathShortfallsAndNeverRunsOnACompleteBake()
    {
        var ironclad = PostDeathAcquire(
            IroncladPostDeathLow, IroncladPostDeathMid, IroncladPostDeathPost, 44,
            new IdSpace(IroncladPostDeathWindowA, IroncladPostDeathWindowB));
        var flail = PostDeathAcquire(
            FlailPostDeathLow, FlailPostDeathMid, FlailPostDeathPost, 37,
            new IdSpace(FlailPostDeathWindowA));
        var spectral = PostDeathAcquire(
            SpectralPostDeathLow, SpectralPostDeathMid, SpectralPostDeathPost, 22,
            new IdSpace(SpectralPostDeathWindowA, SpectralPostDeathWindowB));
        var magi = PostDeathAcquire(
            MagiPostDeathLow, MagiPostDeathMid, MagiPostDeathPost, 16, new IdSpace(MagiPostDeathWindowA));

        Assert.Equal((44, 37, 22, 16), (ironclad.Found, flail.Found, spectral.Found, magi.Found));

        // Not one probe on the rigs that were already complete.
        Assert.Equal((0, 0), (ironclad.FloorProbes, flail.FloorProbes));

        // …and on the two that were short, the strip is what found the missing meshes.
        Assert.Equal((5, 6), (spectral.FloorFound, magi.FloorFound));
    }

    // THE COST, against the mitigation it replaces. Raising IndexSlack to 1024 recovers the same meshes — arm
    // SlackB measured exactly that, 22/22 and 16/16 — by widening the hull in BOTH directions, and nearly all of
    // the extra is spent proving the high side empty. The strip buys the same recovery for the low side alone.
    // The ratio is what matters and it is arithmetic, not a timing: at the round's measured ~1.3–2.0 µs per
    // rendering-server probe, 7 616 and 5 590 candidates are ~10–15 ms against 137 683 and 101 910 at 0.27 s and
    // 0.20 s. Asserted as an ORDER, not as a pair of magic numbers, so a later plan change cannot quietly invert
    // it.
    [Theory]
    [InlineData("spectral_knight", 36_756u, 128u, 36_874u, 132u, 7_616, 137_683)]
    [InlineData("magi_knight", 36_980u, 129u, 37_065u, 160u, 5_590, 101_910)]
    public void WindowAIndexFloor_CostsAFractionOfTheSlack1024WidenItReplaces(
        string rig,
        uint lowValidator,
        uint lowIndex,
        uint midValidator,
        uint midIndex,
        long floorCandidates,
        long slack1024Candidates)
    {
        var low = Rid(lowValidator, lowIndex);
        var mid = Rid(midValidator, midIndex);
        var floor = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(
            Sts2SpineGeoClipSweep.PlanWindowA(low, mid, 64, 50_000).IndexFloorRecoveryPlan);
        var widened = Sts2SpineGeoClipSweep.PlanWindowA(low, mid, 1_024, 50_000).Plan;

        Assert.Equal((rig, floorCandidates), (rig, floor.TotalCandidates));
        Assert.Equal((rig, slack1024Candidates), (rig, widened.TotalCandidates));
        Assert.True(
            floor.TotalCandidates * 15 < slack1024Candidates,
            $"{rig}: the index-floor strip plans {floor.TotalCandidates} candidates against the slack-1024 "
            + $"widen's {slack1024Candidates}, which is no longer the order-of-magnitude saving the strip was "
            + "chosen over a blanket widen for");

        // And the strip is never truncated by a cap the hull itself cleared: it is a strict subset of the
        // window's own validator span across a shorter index axis.
        Assert.False(floor.Truncated);
        Assert.Equal(0u, floor.IndexLow);
    }

    // THE LIMIT, STATED — and it MOVED. This test used to assert that the recovery tier reached below the hull
    // and never above it, on the strength of four controls that said the high side was empty. WS-N falsified
    // those controls on the request lane (see the ceiling's section below), so the boundary is now the ceiling's
    // budgeted reach, and it is asserted in both directions on the same magi bracket: the last column the reach
    // buys is swept, the first column past it is not.
    //
    // magi's window A is 86 validators wide with a hull of 65..224. The budget affords 32 768 / 86 = 381 columns,
    // which is under the 1 024-column ceiling, so the strip runs 225..605 — 86 × 381 = 32 766 candidates.
    [Fact]
    public void IndexRecovery_ReachesBelowTheHullAndAFixedBudgetedDistanceAboveItAndStopsThere()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(Rid(36_980, 129), Rid(37_065, 160), 64, 50_000);
        var ceiling = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(window.IndexCeilingRecoveryPlan);
        Assert.Equal((225u, 605u, 32_766L), (ceiling.IndexLow, ceiling.IndexHigh, ceiling.TotalCandidates));

        // A budgeted strip is never truncated, because the budget is chosen below the cap it is planned under.
        // That matters here more than anywhere: enumeration is validator-major, so a truncated recovery strip
        // would discard the TOP of the validator axis — the very place a late-minted mesh sits.
        Assert.False(ceiling.Truncated);

        var belowTheFloor = Rid(37_000, 12);
        var lastColumnTheReachBuys = Rid(37_000, 605);
        var oneColumnPastIt = Rid(37_000, 606);
        var space = new IdSpace([(37_000u, 12u), (37_000u, 605u), (37_000u, 606u)]);

        var tiered = Tiered([window], expectedRunLength: 4, space);
        var found = tiered.Passes.SelectMany(pass => pass.Found).Distinct().ToList();

        Assert.Contains(belowTheFloor, found);
        Assert.Contains(lastColumnTheReachBuys, found);
        Assert.DoesNotContain(oneColumnPastIt, found);
    }

    // The strip is a plan, not a special case: a hull that already reaches index 0 has nothing below it, and
    // must not be handed an empty or wrapped strip to sweep. (Window B is always in this shape — its ORDINARY
    // plan floors at 0 — which is why only window A grows one.)
    [Fact]
    public void PlanWindowAIndexFloorRecovery_HasNoStripWhenTheHullAlreadyReachesZero()
    {
        Assert.Null(Sts2SpineGeoClipSweep.PlanWindowA(Rid(10, 3), Rid(20, 40), 64, 50_000).IndexFloorRecoveryPlan);
        Assert.Null(
            Sts2SpineGeoClipSweep.PlanWindowAIndexFloorRecovery(
                Sts2SpineGeometryMath.PlanRidCandidates(Rid(10, 0), Rid(20, 40), 0, 50_000)));
    }

    // The kill switch restores the pre-fix plan EXACTLY — same bounds, same cap, same core band, no strip — so
    // "turn it off" is a real answer to a live surprise and not an approximation of one.
    [Fact]
    public void WindowAIndexFloorRecovery_KillSwitchRestoresTheExactPreFixWindow()
    {
        var armed = Sts2SpineGeoClipSweep.PlanWindowA(
            SpectralPostDeathLow, SpectralPostDeathMid, 64, 50_000, indexFloorRecovery: true);
        var disarmed = Sts2SpineGeoClipSweep.PlanWindowA(
            SpectralPostDeathLow, SpectralPostDeathMid, 64, 50_000, indexFloorRecovery: false);

        Assert.Null(disarmed.IndexFloorRecoveryPlan);
        Assert.NotNull(armed.IndexFloorRecoveryPlan);
        Assert.Equal(disarmed, armed with { IndexFloorRecoveryPlan = null });
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    public void ResolveWindowAIndexFloorRecovery_DefaultsArmedAndOnlyAnExplicitFalseDisarmsIt(
        string? raw,
        bool expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipSweep.ResolveWindowAIndexFloorRecovery(raw));
        Assert.True(Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryDefault);
    }

    // ── (e) THE HULL'S CEILING: the residual the floor strip cannot reach ─────────────────────────────
    //
    // WHAT THIS SECTION OVERTURNS, AND WHAT IT DOES NOT. Section (d)'s strip works: measured live on the request
    // lane in a driven fight (round WS-N,
    // `.sts2/research/data/geoclip-live-confirm-20260909T020500Z/`, read `results.txt` first), it took the lane
    // from 0 of 4 rigs admitted to 3 of 4, recovering spectral 17/22 -> 22/22 and magi 10/16 -> 16/16 at exactly
    // the 7 616 and 5 590 candidates section (d) predicted, reproduced to the unit in six independent processes,
    // with foreignMeshes=0 on all 27 admitted bakes. None of that is in question here.
    //
    // What is in question is the SCOPE section (d) was justified on. Its comment says the high side of the hull is
    // empty, on four controls: "raising window B's index top from ~165-225 to ~1123-1185 across all four rigs
    // found not ONE extra mesh". Those controls held in the processes WS-I measured them in. WS-N drove real
    // fights and every surviving residual sits ABOVE a hull top:
    //
    //   ironclad, arm H   window A covers 0..245 (wide 35..245 + the floor strip 0..34) and finds 30 of its 30 —
    //                     the SAME 30 the slack-1024 leg finds at 0..1206. Window B covers 0..165 and finds 10 of
    //                     its 14. Total 40 of 44. The four are in WINDOW B, above its top, and the floor strip
    //                     cannot reach them: it is window A's and it only ever grows DOWNWARD.
    //   flail, arm Hkills window A wide 26..166 finds 35, its floor strip 0..25 finds 0, window B finds 0 and its
    //                     complement 158..166 finds 0. Total 35 of 37. Those two sit above window A's OWN top.
    //
    // In every driven arm the floor strip RAN on these two rigs and found exactly zero. Raising the tops is the
    // only thing that recovers them.
    //
    // ── WHAT THE FIXTURES BELOW ARE, EXACTLY ────────────────────────────────────────────────────────
    //
    // Same discipline as section (d), on the DRIVEN arms instead of the env lane. Each rig's three bracket
    // sentinels are recovered from its recorded sweep-plan lines and then every band and every candidate total
    // those lines report is reproduced by the PRODUCTION planner from those sentinels rather than restated:
    // ironclad 18 343 / 46 631 / 11 952 / 7 735 and flail 2 483 / 26 931 / 316 / 4 966 / 18. Nine totals across
    // two rigs, in bands the planner derives independently. A wrong reconstruction cannot hit nine by accident.
    //
    // The sentinels are not free either — they are SOLVED from the lines, and the solution is over-determined.
    // ironclad's window A band 99..181 fixes {lowIndex, midIndex} = {99, 181} but not which is which; its window B
    // band 0..165 fixes postIndex = 101; and the ABSENCE of a window-B complement line in that arm forces
    // max(midIndex, postIndex) + 64 < 166, i.e. midIndex <= 101, i.e. midIndex = 99 and lowIndex = 181. So in that
    // process the FIRST sentinel came back at a HIGHER index than the second — the bracket's own endpoints are out
    // of order, which is the free list scattering slots, visible directly in the recorded numbers. flail's
    // complement line 158..166 is present and pins midIndex = 102 the same way.
    //
    // Then the per-tier FIND counts are reproduced too, before anything about the fix is asserted: ironclad
    // (narrow 18, wide 30, floor 0, window B 10) and flail (narrow 5, wide 35, floor 0, window B 0). The mesh
    // placements are constrained by those, not chosen to make a fix pass.
    //
    // THE ONE THING THE FIXTURES CANNOT PIN is WHERE above the top the missing meshes sit. The archive's own
    // "WHAT REMAINS UNPROVEN" says so: arm H's window B topped at 165 and found 10, the env leg's topped at 169
    // and found 14, and the exact indices were never read out. So the placement is a THEORY PARAMETER here and
    // the boundary is mapped rather than assumed — the rows below walk the missing meshes from one column above
    // the top to one column past the ceiling's reach, and the last row asserts the ceiling still comes up short
    // there. A live leg has to confirm the real indices are inside the reach; that is stated in the round report.

    private static (
        Sts2SpineGeoClipSweep.SweepWindow A,
        Sts2SpineGeoClipSweep.SweepWindow B) DrivenWindows(
            ulong low,
            ulong mid,
            ulong post,
            bool indexCeilingRecovery = Sts2SpineGeoClipSweep.IndexCeilingRecoveryDefault)
        => (
            Sts2SpineGeoClipSweep.PlanWindowA(
                low, mid, indexSlack: 64, cap: 50_000, indexCeilingRecovery: indexCeilingRecovery),
            Sts2SpineGeoClipSweep.PlanWindowB(
                mid, post, indexSlack: 64, configuredCap: 50_000, envCap: null,
                indexCeilingRecovery: indexCeilingRecovery));

    // ironclad, arm H (and H2, to the unit) — narrow 18, wide 30, floor 0, window B 10, total 40 of 44.
    // Solved sentinels: bracketLow index 181, bracketMid 99, bracketPost 101.
    private static readonly ulong IroncladDrivenLow = Rid(61_086, 181);
    private static readonly ulong IroncladDrivenMid = Rid(61_306, 99);
    private static readonly ulong IroncladDrivenPost = Rid(61_377, 101);

    // 30 meshes on window A's validator span: 18 inside the core band 99..181 (what the narrow tier found) and
    // 12 more inside the slacked hull 35..245 but outside that band (what the wide tier added), none below 35
    // (the floor strip found nothing in this arm, in six independent processes).
    private static readonly (uint V, uint I)[] IroncladDrivenWindowA = Scatter(
        61_088, 7,
        100, 104, 109, 113, 118, 122, 127, 131, 136, 140, 145, 149, 154, 158, 163, 170, 176, 181,
        38, 47, 56, 65, 74, 83, 92, 190, 205, 219, 232, 244);

    // 14 on window B's: 10 inside its axis 0..165, and 4 ABOVE its top at whichever indices the caller places
    // them. That last group is the parameter, not a fact.
    private static (uint V, uint I)[] IroncladDrivenWindowB(params uint[] aboveTheTop)
        => Scatter(61_308, 4, [5, 14, 23, 33, 42, 51, 61, 70, 88, 152, .. aboveTheTop]);

    // flail_knight, arm Hkills — narrow 5, wide 35, floor 0, window B 0 and its complement 0, total 35 of 37.
    // Solved sentinels: bracketLow index 90, bracketMid 102, bracketPost 93.
    private static readonly ulong FlailDrivenLow = Rid(52_103, 90);
    private static readonly ulong FlailDrivenMid = Rid(52_293, 102);
    private static readonly ulong FlailDrivenPost = Rid(52_294, 93);

    // All 37 sit on window A's validator span — window B's is two validators wide and found nothing in this arm.
    // 5 inside the core band 90..102, 30 more inside the hull 26..166, none below 26, and 2 above the top.
    private static (uint V, uint I)[] FlailDrivenWindowA(params uint[] aboveTheTop)
        => Scatter(
            52_105, 5,
            [90, 93, 96, 99, 102,
             27, 31, 35, 40, 44, 49, 53, 58, 62, 67, 71, 76, 80, 85, 89,
             105, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 159, 162, 165, 166,
             .. aboveTheTop]);

    /// <summary>One driven acquisition: what it held, and what each recovery strip cost and found.</summary>
    private sealed record DrivenAcquisition(
        int Found,
        int NarrowFound,
        int WideFound,
        int WindowBNarrowFound,
        long FloorProbes,
        int FloorFound,
        long CeilingProbes,
        int CeilingFound);

    private static DrivenAcquisition DrivenAcquire(
        ulong low,
        ulong mid,
        ulong post,
        int expected,
        IdSpace space,
        bool indexCeilingRecovery = Sts2SpineGeoClipSweep.IndexCeilingRecoveryDefault)
    {
        var (windowA, windowB) = DrivenWindows(low, mid, post, indexCeilingRecovery);
        var tiered = Tiered([windowA, windowB], expected, space);

        // The recovery passes are told apart by WHERE they sit relative to the window they belong to, which is
        // the only thing that distinguishes them — they share a tier name by design.
        var tops = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            [windowA.Name] = windowA.Plan.IndexHigh,
            [windowB.Name] = windowB.Plan.IndexHigh,
        };
        var recovery = tiered.Passes
            .Where(pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery)
            .ToList();
        var floors = recovery.Where(pass => pass.Plan.IndexHigh < tops[pass.WindowName]).ToList();
        var ceilings = recovery.Where(pass => pass.Plan.IndexLow > tops[pass.WindowName]).ToList();

        return new DrivenAcquisition(
            tiered.Passes.SelectMany(pass => pass.Found).Distinct().Count(),
            tiered.Passes.Single(pass =>
                pass.Tier == Sts2SpineGeoClipSweep.TierNarrow
                && pass.WindowName == Sts2SpineGeoClipSweep.WindowAName).Found.Count,
            tiered.Passes.Single(pass => pass.Tier == Sts2SpineGeoClipSweep.TierWide).Found.Count,
            tiered.Passes.Single(pass =>
                pass.Tier == Sts2SpineGeoClipSweep.TierNarrow
                && pass.WindowName == Sts2SpineGeoClipSweep.WindowBName).Found.Count,
            floors.Sum(pass => pass.Probed),
            floors.Sum(pass => pass.Found.Count),
            ceilings.Sum(pass => pass.Probed),
            ceilings.Sum(pass => pass.Found.Count));
    }

    // FIRST, AS IN SECTION (d): the reconstruction is the recorded one. Every band and every candidate total is
    // the production planner's answer to the SOLVED sentinels, checked against the driven arm's own log line.
    [Theory]
    // rig, low, mid, post, narrow band + total, wide band + total, window B band + total, floor band + total
    [InlineData("ironclad/H", 61_086u, 181u, 61_306u, 99u, 61_377u, 101u,
        99u, 181u, 18_343, 35u, 245u, 46_631, 0u, 165u, 11_952, 0u, 34u, 7_735)]
    [InlineData("flail_knight/Hkills", 52_103u, 90u, 52_293u, 102u, 52_294u, 93u,
        90u, 102u, 2_483, 26u, 166u, 26_931, 0u, 157u, 316, 0u, 25u, 4_966)]
    public void DrivenBrackets_ReproduceEveryRecordedSweepPlanBandAndCandidateTotal(
        string rig,
        uint lowValidator,
        uint lowIndex,
        uint midValidator,
        uint midIndex,
        uint postValidator,
        uint postIndex,
        uint narrowLow,
        uint narrowHigh,
        long narrowTotal,
        uint wideLow,
        uint wideHigh,
        long wideTotal,
        uint windowBLow,
        uint windowBHigh,
        long windowBTotal,
        uint floorLow,
        uint floorHigh,
        long floorTotal)
    {
        var (windowA, windowB) = DrivenWindows(
            Rid(lowValidator, lowIndex), Rid(midValidator, midIndex), Rid(postValidator, postIndex));
        var narrow = Sts2SpineGeoClipSweep.NarrowToCoreBand(windowA).Plan;
        var floor = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(windowA.IndexFloorRecoveryPlan);

        Assert.Equal(
            (rig, narrowLow, narrowHigh, narrowTotal),
            (rig, narrow.IndexLow, narrow.IndexHigh, narrow.TotalCandidates));
        Assert.Equal(
            (rig, wideLow, wideHigh, wideTotal),
            (rig, windowA.Plan.IndexLow, windowA.Plan.IndexHigh, windowA.Plan.TotalCandidates));
        Assert.Equal(
            (rig, windowBLow, windowBHigh, windowBTotal),
            (rig, windowB.Plan.IndexLow, windowB.Plan.IndexHigh, windowB.Plan.TotalCandidates));
        Assert.Equal(
            (rig, floorLow, floorHigh, floorTotal),
            (rig, floor.IndexLow, floor.IndexHigh, floor.TotalCandidates));
    }

    // …and window B's PRE-CEILING complement is reproduced too, in both of its recorded states: absent on
    // ironclad's arm H (which is what pins its bracketMid index at 99) and present at 158..166 for 18 candidates
    // on flail's arm Hkills (which is what pins flail's at 102).
    [Fact]
    public void DrivenBrackets_ReproduceWindowBsRecordedPreCeilingComplement()
    {
        var (_, ironclad) = DrivenWindows(
            IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost, indexCeilingRecovery: false);
        var (_, flail) = DrivenWindows(
            FlailDrivenLow, FlailDrivenMid, FlailDrivenPost, indexCeilingRecovery: false);

        Assert.Null(ironclad.IndexCeilingRecoveryPlan);
        var complement = Assert.IsType<Sts2SpineGeometryMath.RidCandidatePlan>(
            flail.IndexCeilingRecoveryPlan);
        Assert.Equal((158u, 166u, 18L), (complement.IndexLow, complement.IndexHigh, complement.TotalCandidates));
    }

    // THE DEFECT, REPRODUCED. Switch the ceiling off and both rigs come up exactly as short as they did on the
    // request lane in a driven fight — 40 of 44 and 35 of 37 — with every per-tier find count matching the arm's
    // own log line, and the FLOOR strip running and finding nothing on both, which is what the round measured in
    // six independent processes.
    [Theory]
    [InlineData("ironclad/H", 40, 18, 30, 10, 7_735, 0)]
    [InlineData("flail_knight/Hkills", 35, 5, 35, 0, 4_966, 0)]
    public void CeilingOff_ReproducesTheRecordedDrivenShortfallAndItsPerTierFindCounts(
        string rig,
        int found,
        int narrowFound,
        int wideFound,
        int windowBNarrowFound,
        long floorProbes,
        int floorFound)
    {
        var acquisition = rig.StartsWith("ironclad", StringComparison.Ordinal)
            ? DrivenAcquire(
                IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost, 44,
                new IdSpace(IroncladDrivenWindowA, IroncladDrivenWindowB(168, 182, 209, 247)),
                indexCeilingRecovery: false)
            : DrivenAcquire(
                FlailDrivenLow, FlailDrivenMid, FlailDrivenPost, 37,
                new IdSpace(FlailDrivenWindowA(174, 203)),
                indexCeilingRecovery: false);

        Assert.Equal(
            (rig, found, narrowFound, wideFound, windowBNarrowFound, floorProbes, floorFound),
            (rig, acquisition.Found, acquisition.NarrowFound, acquisition.WideFound,
                acquisition.WindowBNarrowFound, acquisition.FloorProbes, acquisition.FloorFound));
    }

    // THE FIX, AND ITS BOUNDARY, MAPPED RATHER THAN ASSUMED. The exact indices of the missing meshes were never
    // read out of the live process, so they are the parameter here and the rows walk them outward. The ceiling
    // reaches a distance the budget and the window's validator span decide between them — ironclad's window B is
    // 72 validators wide so it reaches 455 columns, to index 620; flail's window A is 191 wide so it reaches 171
    // columns, to index 337 — and the last row of each rig sits ONE COLUMN past that and is still short.
    [Theory]
    // ironclad: four consecutive meshes starting just above window B's top of 165, then mid-band, then landing
    // exactly on the last four columns the reach buys, then straddling the edge, then entirely past it.
    [InlineData("ironclad/just above the top", 166u, 44)]
    [InlineData("ironclad/mid band", 300u, 44)]
    [InlineData("ironclad/last four columns the reach buys", 617u, 44)]
    [InlineData("ironclad/straddling the reach's top column", 618u, 43)]
    [InlineData("ironclad/entirely past the reach", 621u, 40)]
    public void Ceiling_ClosesIroncladsWindowBShortfallForAnyPlacementInsideItsReach(
        string placement,
        uint firstMissingIndex,
        int expectedFound)
    {
        var acquisition = DrivenAcquire(
            IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost, 44,
            new IdSpace(
                IroncladDrivenWindowA,
                IroncladDrivenWindowB(
                    firstMissingIndex, firstMissingIndex + 1, firstMissingIndex + 2, firstMissingIndex + 3)));

        Assert.Equal((placement, expectedFound), (placement, acquisition.Found));
    }

    [Theory]
    [InlineData("flail/just above the top", 167u, 37)]
    [InlineData("flail/mid band", 250u, 37)]
    [InlineData("flail/last two columns the reach buys", 336u, 37)]
    [InlineData("flail/straddling the reach's top column", 337u, 36)]
    [InlineData("flail/entirely past the reach", 338u, 35)]
    public void Ceiling_ClosesFlailsWindowAShortfallForAnyPlacementInsideItsReach(
        string placement,
        uint firstMissingIndex,
        int expectedFound)
    {
        var acquisition = DrivenAcquire(
            FlailDrivenLow, FlailDrivenMid, FlailDrivenPost, 37,
            new IdSpace(FlailDrivenWindowA(firstMissingIndex, firstMissingIndex + 1)));

        Assert.Equal((placement, expectedFound), (placement, acquisition.Found));
    }

    // THE COST, ON THE SAME TWO BRACKETS, in candidates the planner computes rather than numbers a comment
    // quotes. Both rigs' ceilings and the probes they actually spend, against the blanket IndexSlack=1024 widen
    // this design exists to avoid. The floor strip is counted too, because a short bake pays for both.
    [Fact]
    public void Ceiling_CostsAFractionOfTheBlanketWidenAndSaysWhatEachRigSpends()
    {
        var (ironcladA, ironcladB) = DrivenWindows(
            IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost);
        var (flailA, flailB) = DrivenWindows(FlailDrivenLow, FlailDrivenMid, FlailDrivenPost);

        // Reach × validator span, per window. Window A is trimmed by the BUDGET (221 and 191 validators wide, so
        // 148 and 171 columns); ironclad's window B is 72 wide and gets 455; flail's window B is two wide, so it
        // reaches the full 1 024-column ceiling for 2 048 candidates.
        Assert.Equal(
            [(246u, 393u, 32_708L), (166u, 620u, 32_760L), (167u, 337u, 32_661L), (158u, 1_181u, 2_048L)],
            new[] { ironcladA, ironcladB, flailA, flailB }
                .Select(window => window.IndexCeilingRecoveryPlan!)
                .Select(plan => (plan.IndexLow, plan.IndexHigh, plan.TotalCandidates))
                .ToArray());

        // No ceiling strip is ever truncated: the budget is chosen below the request lane's own 50 000 cap, and
        // the reach is trimmed to whichever of the two is smaller. Enumeration is validator-major, so a truncated
        // recovery strip would throw away the TOP of the validator axis — where a late-minted mesh sits.
        Assert.All(
            new[] { ironcladA, ironcladB, flailA, flailB },
            window => Assert.False(window.IndexCeilingRecoveryPlan!.Truncated));

        // The blanket alternative, planned by the same production code on THESE sentinels. (The archive's own
        // slack-1024 leg planned 266 747 on its own bracket and spent 0.52 s on it.)
        var blanket = Sts2SpineGeoClipSweep.PlanWindowA(IroncladDrivenLow, IroncladDrivenMid, 1_024, 50_000).Plan;
        Assert.Equal(266_526L, blanket.TotalCandidates);

        // What each rig actually SPENDS on the recovery tier when it fires, floor included. ironclad pays both
        // ceilings because the meshes it is missing are behind the second one; flail's window A ceiling closes
        // its shortfall, so window B's never runs.
        var ironclad = DrivenAcquire(
            IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost, 44,
            new IdSpace(IroncladDrivenWindowA, IroncladDrivenWindowB(168, 182, 209, 247)));
        var flail = DrivenAcquire(
            FlailDrivenLow, FlailDrivenMid, FlailDrivenPost, 37, new IdSpace(FlailDrivenWindowA(174, 203)));

        Assert.Equal((7_735L, 32_708L + 32_760L), (ironclad.FloorProbes, ironclad.CeilingProbes));
        Assert.Equal((4_966L, 32_661L), (flail.FloorProbes, flail.CeilingProbes));
        Assert.Equal((4, 2), (ironclad.CeilingFound, flail.CeilingFound));
        Assert.True(
            ironclad.CeilingProbes + ironclad.FloorProbes < blanket.TotalCandidates,
            "the recovery tier now costs more than the blanket widen it was chosen over, on ironclad's own "
            + "bracket, which is the trade this whole design rests on");
    }

    // THE MEASUREMENT SECTION (d) BOUGHT MUST NOT REGRESS. spectral and magi are recovered by the FLOOR, at
    // 7 616 and 5 590 candidates, and the round measured exactly those numbers live in six processes. The
    // driver re-grades between strips, so a rig the floor rescues spends ZERO ceiling probes — the ceiling is
    // added coverage for the rigs the floor cannot reach, not a tax on the rigs it can.
    [Fact]
    public void Ceiling_CostsNothingOnARigTheFloorStripAlreadyRescues()
    {
        var spectral = DrivenAcquire(
            SpectralPostDeathLow, SpectralPostDeathMid, SpectralPostDeathPost, 22,
            new IdSpace(SpectralPostDeathWindowA, SpectralPostDeathWindowB));
        var magi = DrivenAcquire(
            MagiPostDeathLow, MagiPostDeathMid, MagiPostDeathPost, 16, new IdSpace(MagiPostDeathWindowA));

        Assert.Equal(
            (22, 7_616L, 5, 0L),
            (spectral.Found, spectral.FloorProbes, spectral.FloorFound, spectral.CeilingProbes));
        Assert.Equal((16, 5_590L, 6, 0L), (magi.Found, magi.FloorProbes, magi.FloorFound, magi.CeilingProbes));
    }

    // …AND NEITHER STRIP RUNS AT ALL ON A BAKE THAT REACHED ITS SLOT COUNT. This is the property the live round
    // measured — 7 complete bakes, 0 recovery passes, 0 probes, 0 allocations — and the one the ceiling is most
    // able to break, because unlike the floor it is planned on EVERY window of every bracket.
    [Theory]
    [InlineData("ironclad/complete", 44)]
    [InlineData("flail_knight/complete", 37)]
    public void Ceiling_NeverRunsOnABakeThatReachedItsSlotCount(string rig, int expected)
    {
        var acquisition = rig.StartsWith("ironclad", StringComparison.Ordinal)
            ? DrivenAcquire(
                IroncladPostDeathLow, IroncladPostDeathMid, IroncladPostDeathPost, expected,
                new IdSpace(IroncladPostDeathWindowA, IroncladPostDeathWindowB))
            : DrivenAcquire(
                FlailPostDeathLow, FlailPostDeathMid, FlailPostDeathPost, expected,
                new IdSpace(FlailPostDeathWindowA));

        Assert.Equal(
            (rig, expected, 0L, 0L),
            (rig, acquisition.Found, acquisition.FloorProbes, acquisition.CeilingProbes));
    }

    // THE FOREIGN-GEOMETRY EXPOSURE, BOUNDED AND ASSERTED. The ownership fence is the VALIDATOR span, not the
    // index axis: a validator comes from one process-global monotonic counter, so an id a strip can compose is by
    // construction an id minted inside the same MeshCreate bracket as the rig's own meshes. Widening the index
    // axis changes how much of THAT population the sweep sees; it can never reach a different one. So the thing
    // that has to be true — and the thing this pins — is that no recovery strip, in either direction, moves a
    // validator endpoint by so much as one.
    [Fact]
    public void NeitherRecoveryStripEverWidensAWindowsValidatorSpan()
    {
        (string Label, ulong Low, ulong Mid, ulong Post)[] brackets =
        [
            ("ironclad/H", IroncladDrivenLow, IroncladDrivenMid, IroncladDrivenPost),
            ("flail_knight/Hkills", FlailDrivenLow, FlailDrivenMid, FlailDrivenPost),
            ("spectral_knight/SlackA", SpectralPostDeathLow, SpectralPostDeathMid, SpectralPostDeathPost),
            ("magi_knight/SlackA", MagiPostDeathLow, MagiPostDeathMid, MagiPostDeathPost),
            ("merchant/clean", MerchantCleanBracketLow, MerchantCleanBracketMid, MerchantBracketPost),
        ];

        foreach (var (label, low, mid, post) in brackets)
        {
            var (windowA, windowB) = DrivenWindows(low, mid, post);
            foreach (var window in new[] { windowA, windowB })
            {
                foreach (var strip in window.IndexRecoveryPlans)
                {
                    Assert.Equal(
                        (label, window.Name, window.Plan.ValidatorLow, window.Plan.ValidatorHigh),
                        (label, window.Name, strip.ValidatorLow, strip.ValidatorHigh));
                }
            }
        }
    }

    // The ceiling's kill switch restores the pre-fix window EXACTLY — the ordinary plan, the floor strip, and
    // window B's own complement, which is deliberately OUTSIDE the switch because it shipped before this change.
    // Asserted by record equality, the same way the floor's switch is, so "turn it off" is a real answer to a
    // live surprise rather than an approximation of one.
    [Fact]
    public void IndexCeilingRecovery_KillSwitchRestoresTheExactPreFixWindows()
    {
        var (armedA, armedB) = DrivenWindows(FlailDrivenLow, FlailDrivenMid, FlailDrivenPost);
        var (disarmedA, disarmedB) = DrivenWindows(
            FlailDrivenLow, FlailDrivenMid, FlailDrivenPost, indexCeilingRecovery: false);

        // Window A had no ceiling at all before, and its floor strip is untouched by this switch.
        Assert.Null(disarmedA.IndexCeilingRecoveryPlan);
        Assert.NotNull(disarmedA.IndexFloorRecoveryPlan);
        Assert.Equal(disarmedA, armedA with { IndexCeilingRecoveryPlan = null });

        // Window B keeps the complement it has carried since 6155552d — 158..166 on this bracket — and loses only
        // the budgeted reach past it.
        Assert.Equal(
            (158u, 166u),
            (disarmedB.IndexCeilingRecoveryPlan!.IndexLow, disarmedB.IndexCeilingRecoveryPlan!.IndexHigh));
        Assert.Equal(
            disarmedB,
            armedB with { IndexCeilingRecoveryPlan = disarmedB.IndexCeilingRecoveryPlan });
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    public void ResolveIndexCeilingRecovery_DefaultsArmedAndOnlyAnExplicitFalseDisarmsIt(
        string? raw,
        bool expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipSweep.ResolveIndexCeilingRecovery(raw));
        Assert.True(Sts2SpineGeoClipSweep.IndexCeilingRecoveryDefault);
    }

    // The two bounds are a measurement and a budget, so they are pinned as values: a later edit that moves either
    // moves the live cost or the live reach and has to say which.
    [Fact]
    public void IndexCeilingRecovery_BoundsArePinnedToWhatTheRoundMeasured()
    {
        // The largest floor strip measured live (arm H2's flail, 31 706 candidates at 0.05 s), rounded up to the
        // next power of two — and below the request lane's hard-coded 50 000 cap, which is what makes a ceiling
        // strip unable to truncate.
        Assert.Equal(32_768, Sts2SpineGeoClipSweep.IndexCeilingRecoveryBudget);
        Assert.True(Sts2SpineGeoClipSweep.IndexCeilingRecoveryBudget < 50_000);

        // IndexSlack=1024, the blanket widen measured to recover every missing mesh — the furthest reach with any
        // evidence behind it, and the furthest this strip will go however cheap the window is.
        Assert.Equal(1_024, Sts2SpineGeoClipSweep.IndexCeilingRecoveryMaxColumns);
    }

    // ── The kill switch's ONE default ────────────────────────────────────────────────────────────────

    // TWO LANES, ONE DEFAULT. The env lane reads SPIRECTL_SPINE_GEOCLIP_DENSE_SWEEP inside the live-host-only
    // baker; the on-demand /geoclips/ lane plans its config here in the core. They each carried their own
    // literal and had drifted apart — the request lane still armed the walk arm long after it was measured a
    // loss and reverted on the env lane, and every one of those walks was wasted because the dense fallback
    // fired anyway. Both now resolve through ResolveDenseSweepOnly, and this is what stops them drifting again:
    // the request lane's plan must equal the env lane's unset answer.
    //
    // RETRACTED: this used to quote that loss as "+227 ms byrdonis, +485 ms merchant per cold pose". Those
    // milliseconds were measured against a dense fallback of 38 579 candidates for the merchant's window A, and
    // the narrow tier now carries that same window for 10 035
    // (NarrowTierAlone_FindsEveryMerchantCleanMeshFor10035ProbesInsteadOf38579). A wasted walk is charged
    // against a fallback roughly a quarter the size, so the figures do not transfer and are withdrawn. What
    // survives is the shape of the fact — the walk ran, the dense sweep ran anyway, the walk was pure cost —
    // and the probe counts, which the tests above re-measure rather than quote from a session.
    [Fact]
    public void RequestLane_PlansTheSameDenseSweepDefaultTheEnvLaneResolvesWhenItsVariableIsUnset()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://scenes/creature_visuals/byrdonis.tscn", "Visuals", "idle_loop", null, "/tmp/geoclips"),
            "Spirectl.Sts2",
            out _);

        Assert.NotNull(config);
        Assert.Equal(Sts2SpineGeoClipWalk.ResolveDenseSweepOnly(null), config!.DenseSweepOnly);
        Assert.Equal(Sts2SpineGeoClipWalk.ResolveDenseSweepOnly(string.Empty), config.DenseSweepOnly);
        Assert.Equal(Sts2SpineGeoClipWalk.ResolveDenseSweepOnly("   "), config.DenseSweepOnly);
    }

    // …and the SHIPPED value of that default is true, i.e. the walk arm is off. Pinned as a value because it is
    // a measurement, not a preference: re-arming the walk needs per-window grading and a live answer for why
    // window B seats no column, both recorded in the baker's DenseSweepEnv note.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void ResolveDenseSweepOnly_DefaultsToTheDenseSweepAndOnlyAnExplicitZeroArmsTheWalk(
        string? raw,
        bool expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipWalk.ResolveDenseSweepOnly(raw));
        Assert.True(Sts2SpineGeoClipWalk.DenseSweepOnlyDefault);
    }

    // ── Truncation: the cap can lose a mesh, so it must never look like an answer ─────────────────────
    //
    // Enumeration is validator-major (Sts2SpineGeometryMath.EnumerateRidCandidates), so a candidate cap
    // discards the TOP of the validator axis. Window A's span IS the bracket its meshes were minted inside, so
    // there is no far end known to be empty for a cap to throw away safely.
    //
    // RECORDED, not hypothetical. A live menu-background bake planned window A at 77 074 candidates against the
    // 50 000 cap, truncated, and returned 8 meshes — while the SAME window's core-band pass, same validator span
    // and a strict subset of the index axis, was small enough not to truncate and returned 21. A superset plan
    // returning 13 fewer meshes is only possible if the cap cut the sweep off before it reached them. The bake
    // shipped 25 meshes only because the narrow tier had already found them.

    // The mechanism, at the size the live bake hit it: a cap that stops a wide plan short makes it miss meshes a
    // narrower, uncapped plan over the same validator span finds.
    [Fact]
    public void ACappedWidePlan_MissesMeshesTheNarrowerUncappedPlanOverTheSameValidatorsFinds()
    {
        // 433 validators × 178 indices = 77 074, the live menu-background numbers.
        var low = Rid(22_820, 64);
        var high = Rid(23_252, 113);

        // Deep in the validator span, past where a 50 000 cap can reach: 50 000 / 178 candidates per row is 280
        // full rows of the 433, so the sweep stops partway through validator 23 100.
        var deep = Enumerable.Range(0, 13)
            .Select(k => (V: (uint)(23_150 + k), I: (uint)(100 + k)))
            .ToArray();

        var capped = Sts2SpineGeometryMath.PlanRidCandidates(low, high, indexSlack: 64, cap: 50_000);
        Assert.True(capped.Truncated);
        Assert.Equal(77_074, capped.TotalCandidates);

        var reached = Sts2SpineGeometryMath.EnumerateRidCandidates(capped).ToHashSet();
        Assert.All(deep, mesh => Assert.DoesNotContain(Rid(mesh.V, mesh.I), reached));

        // The narrower band over the same validators is 24 248 candidates, does not truncate, and reaches them.
        var narrow = Sts2SpineGeometryMath.PlanRidCandidates(
            Rid(22_820, 100), Rid(23_252, 112), indexSlack: 0, cap: 50_000);
        Assert.False(narrow.Truncated);
        var narrowReached = Sts2SpineGeometryMath.EnumerateRidCandidates(narrow).ToHashSet();
        Assert.Contains(Rid(deep[0].V, deep[0].I), narrowReached);
    }

    // So a truncated narrow pass may never be graded as sufficient, EVEN IF its find count clears the slot count.
    // Its count is a floor on a sweep that never finished; the ids it did not reach are exactly the ones a
    // validator-major cap discards. This is the asymmetry the acquisition is built on — widening costs a pass,
    // not widening ships a rig with meshes missing.
    [Fact]
    public void ATruncatedNarrowPass_ArmsTheWideTierEvenWhenItAlreadyFoundEveryExpectedMesh()
    {
        // A window A whose CORE BAND alone overflows the cap.
        var window = Sts2SpineGeoClipSweep.PlanWindowA(
            Rid(10_000, 87), Rid(19_000, 131), indexSlack: 64, cap: 2_000, capCeiling: 2_000);
        var narrow = Sts2SpineGeoClipSweep.NarrowToCoreBand(window);
        Assert.True(narrow.Plan.Truncated);

        // Two meshes, both inside the truncated prefix, so the narrow pass finds them all.
        var meshes = new[] { (V: 10_001u, I: 90u), (V: 10_002u, I: 91u) };
        var tiered = Tiered([window], expectedRunLength: meshes.Length, new IdSpace(meshes));

        Assert.Equal(2, tiered.FoundAfterNarrow);
        Assert.False(
            Sts2SpineGeoClipWalk.DenseSweepRequired(true, tiered.FoundAfterNarrow, meshes.Length),
            "the premise: on the COUNT alone this narrow pass looks complete");
        Assert.True(tiered.NarrowTruncated);
        Assert.True(
            tiered.WideRan,
            "a narrow pass that hit its cap swept a subset of its own plan, so its count is a floor and cannot "
            + "be allowed to skip the wide tier");
    }

    // …and an untruncated narrow pass that clears the count still skips it, so the rule above is not just
    // "always widen" wearing a hat.
    [Fact]
    public void AnUntruncatedNarrowPassThatClearsTheCount_StillSkipsTheWideTier()
    {
        var window = WindowA(MerchantCleanBracketLow, MerchantCleanBracketMid);

        var tiered = Tiered([window], expectedRunLength: 30, new IdSpace(MerchantMeshesClean));

        Assert.False(Sts2SpineGeoClipSweep.NarrowToCoreBand(window).Plan.Truncated);
        Assert.False(tiered.NarrowTruncated);
        Assert.False(tiered.WideRan);
    }

    // ── Window A's cap now covers window A ───────────────────────────────────────────────────────────

    // The fix for the truncation above: window A's cap is RAISED to its own plan's size, ceilinged. The live
    // window-A plans that truncated at 50 000 were 51 975 (merchant), 53 360 / 113 800 / 259 464 (ironclad) and
    // 77 074 (menu background); all five fit now.
    [Theory]
    [InlineData(38_579, 50_000, 50_000, "config")]
    [InlineData(51_975, 50_000, 51_975, "auto-raised")]
    [InlineData(77_074, 50_000, 77_074, "auto-raised")]
    [InlineData(259_464, 50_000, 259_464, "auto-raised")]
    public void ChooseWindowACap_RaisesToCoverTheWholeBracketAndNeverLowersTheConfiguredCap(
        long totalCandidates,
        int configuredCap,
        int expectedCap,
        string expectedSource)
    {
        var (cap, source, from, to) = Sts2SpineGeoClipSweep.ChooseWindowACap(totalCandidates, configuredCap);

        Assert.Equal(expectedCap, cap);
        Assert.Equal(expectedSource, source);
        Assert.Equal(configuredCap, from);
        Assert.Equal(expectedCap, to);
        Assert.True(cap >= configuredCap, "the raise must never lower a cap an operator set deliberately");
    }

    // The ceiling is what the cap was really for: a mis-detected bracket spanning a whole scene load must not
    // plan a multi-minute sweep. It still truncates there — and that truncation now arms the wide tier and lands
    // in the bake report rather than in a log line.
    [Fact]
    public void ChooseWindowACap_StopsAtTheCeilingSoAPathologicalBracketStillCannotPlanAnUnboundedSweep()
    {
        var (cap, source, _, _) = Sts2SpineGeoClipSweep.ChooseWindowACap(50_000_000, 50_000);

        Assert.Equal(Sts2SpineGeoClipSweep.WindowACapCeiling, cap);
        Assert.Equal("auto-raised", source);
    }

    // End to end on the plan that actually truncated live: the merchant's 51 975-candidate window A.
    [Fact]
    public void PlanWindowA_NoLongerTruncatesTheMerchantPlanThatTruncatedLive()
    {
        var window = Sts2SpineGeoClipSweep.PlanWindowA(
            Rid(40_346, 85), Rid(40_570, 187), indexSlack: 64, cap: 50_000);

        Assert.Equal(51_975, window.Plan.TotalCandidates);
        Assert.False(window.Plan.Truncated);
        Assert.Equal(51_975, window.Plan.Cap);
        Assert.Equal("auto-raised", window.CapSource);
        Assert.Equal(50_000, window.CapAutoRaisedFrom);
        Assert.Equal(51_975, window.CapAutoRaisedTo);
    }

    // ── Truncation reaches the bake report ───────────────────────────────────────────────────────────
    //
    // The old behaviour warned about truncation in a log line ("a slot mesh may be missing from the far end of
    // the window") and nowhere a consumer looks. Per-window `truncated` existed, but only for the WIDE plan, and
    // nothing rolled either up to the bake.

    [Fact]
    public void SummariseTruncation_SaysNothingWhenEveryWindowSweptItsWholePlan()
    {
        var (truncated, note) = Sts2SpineGeoClipSweep.SummariseTruncation(
            [Window("A", truncated: false, narrowTruncated: false)]);

        Assert.False(truncated);
        Assert.Equal(string.Empty, note);
    }

    [Theory]
    [InlineData(true, false, "wide tier")]
    [InlineData(false, true, "narrow tier")]
    [InlineData(true, true, "narrow tier")]
    public void SummariseTruncation_RollsUpEitherTiersTruncationAndNamesTheWindow(
        bool wide,
        bool narrow,
        string expectedFragment)
    {
        var (truncated, note) = Sts2SpineGeoClipSweep.SummariseTruncation(
            [Window("B", truncated: wide, narrowTruncated: narrow)]);

        Assert.True(truncated);
        Assert.Contains(expectedFragment, note);
        Assert.Contains("window B", note);
        Assert.Contains("meshesValidated as a floor", note);
    }

    // A truncation in EITHER window has to surface, not just the first one.
    [Fact]
    public void SummariseTruncation_ReportsAWindowBTruncationWhenWindowAWasFine()
    {
        var (truncated, note) = Sts2SpineGeoClipSweep.SummariseTruncation(
        [
            Window("A", truncated: false, narrowTruncated: false),
            Window("B", truncated: true, narrowTruncated: false),
        ]);

        Assert.True(truncated);
        Assert.Contains("window B", note);
        Assert.DoesNotContain("window A", note);
    }

    [Fact]
    public void SummariseTruncation_ReportsRecoveryOnlyTruncationWithTheRecoveryPlansExactNumbers()
    {
        var recoveryPlan = Sts2SpineGeometryMath.PlanRidCandidates(Rid(10, 111), Rid(12, 189), 0, 112);
        var window = Window("B", truncated: true, narrowTruncated: false, cap: 112) with
        {
            OrdinaryTruncated = false,
            IndexRecoveries = [new GeoClipBakeIndexRecovery(recoveryPlan, Probed: 112, Found: 1)],
        };

        var (truncated, note) = Sts2SpineGeoClipSweep.SummariseTruncation([window]);

        Assert.True(truncated);
        Assert.DoesNotContain("wide tier capped", note);
        Assert.Contains("index-recovery tier capped at 112 of 237 candidates", note);
        Assert.Contains("validators 10..12, indices 111..189, emitted 112, probed 112, found 1", note);
    }

    [Fact]
    public void SummariseTruncation_ReportsOrdinaryAndRecoveryTruncationAsSeparatePlans()
    {
        var recoveryPlan = Sts2SpineGeometryMath.PlanRidCandidates(Rid(10, 111), Rid(12, 189), 0, 3);
        var window = Window("B", truncated: true, narrowTruncated: false) with
        {
            OrdinaryTruncated = true,
            IndexRecoveries = [new GeoClipBakeIndexRecovery(recoveryPlan, Probed: 3, Found: 1)],
        };

        var (_, note) = Sts2SpineGeoClipSweep.SummariseTruncation([window]);

        Assert.Contains("window B wide tier capped at 3 of 4 candidates", note);
        Assert.Contains("window B index-recovery tier capped at 3 of 237 candidates", note);
        Assert.Contains("emitted 3, probed 3, found 1", note);
    }

    [Fact]
    public void RecoveryReportingMetadata_DoesNotChangeTheWindowJsonShape()
    {
        var baseline = Window("B", truncated: true, narrowTruncated: false);
        var recoveryPlan = Sts2SpineGeometryMath.PlanRidCandidates(Rid(10, 111), Rid(12, 189), 0, 112);
        var withMetadata = baseline with
        {
            OrdinaryTruncated = false,
            IndexRecoveries = [new GeoClipBakeIndexRecovery(recoveryPlan, Probed: 112, Found: 1)],
        };

        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(withMetadata));
        Assert.DoesNotContain("OrdinaryTruncated", JsonSerializer.Serialize(withMetadata));
        Assert.DoesNotContain("IndexRecover", JsonSerializer.Serialize(withMetadata));
    }

    private static GeoClipBakeWindow Window(
        string name,
        bool truncated,
        bool narrowTruncated,
        int cap = 3,
        long totalCandidates = 4)
        => new(
            name,
            ValidatorLow: 1,
            ValidatorHigh: 2,
            IndexLow: 0,
            IndexHigh: 1,
            ValidatorSpan: 2,
            TotalCandidates: totalCandidates,
            Cap: cap,
            Truncated: truncated,
            Probed: 3,
            Found: 1,
            SweepMs: 0,
            RecommendedCap: totalCandidates,
            CapSource: "config",
            CapAutoRaisedFrom: cap,
            CapAutoRaisedTo: cap,
            RejectedByReason: new Dictionary<string, int>(),
            NarrowTruncated: narrowTruncated);
}
