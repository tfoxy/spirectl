using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The GROUPED slot↔mesh association probe and the bake's frame budget — the two halves of "a pose-only
// geoclip bake costs awaited frames, not compute".
//
// The old probe nudged ONE slot's colour per awaited frame, so a 44-slot rig spent 88 frames (~1.5 s at
// 60 fps) deciding which mesh belongs to which slot. The probe here nudges a SUBSET per frame and reads a
// slot's identity out of WHICH frames its mesh moved in, which is O(log slots) frames for the same
// measurement. The decode is a pure function of "mesh -> set of frames it moved in", so all of it is
// provable here, against a game that cannot be run in CI.
public sealed class Sts2SpineGeoClipProbeTests
{
    // The scheme is an internal enum and an xunit theory has to be public, so arms name it by the same
    // string the environment knob takes.
    private static GeoClipProbeCodeScheme Scheme(string name) => GeoClipProbeSettings.Parse(name, null).Scheme;

    // ── The schedule: how many frames a group costs, and what the codes look like ─────────────────────

    // The whole point of the change. A group of one slot must not cost six frames, and a group of 44 must
    // not cost 44 — the old cost is quoted in the second column so a regression to it is loud.
    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(2, 2, 4)]
    [InlineData(3, 3, 6)]
    [InlineData(5, 4, 10)]
    [InlineData(28, 7, 56)]
    [InlineData(44, 8, 88)]
    [InlineData(200, 10, 400)]
    public void PlanProbe_UnionSafeCostsFarFewerFramesThanOneSlotPerFrame(
        int slots,
        int expectedFrames,
        int framesTheOldProbeWouldHaveCost)
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(slots, GeoClipProbeCodeScheme.UnionSafe);

        Assert.Equal(expectedFrames, plan.FrameCount);
        Assert.True(plan.FrameCount < framesTheOldProbeWouldHaveCost);
    }

    // The ordinal arm is the brief's original shape and the A/B escape hatch, so its frame count is pinned
    // too: ceil(log2(slots + 1)), the +1 because code 0 is reserved for "moved in no frame".
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(28, 5)]
    [InlineData(44, 6)]
    public void PlanProbe_OrdinalCostsCeilLog2OfTheGroupPlusOne(int slots, int expectedFrames)
        => Assert.Equal(expectedFrames, Sts2SpineGeoClipProbe.PlanProbe(slots, GeoClipProbeCodeScheme.Ordinal).FrameCount);

    // Codes are built from the GROUP's members, not from a rig-wide slot ordinal: a group of one is one
    // frame however many slots the rig has, which is why slots are probed at the frame they first become
    // visible rather than all together.
    [Fact]
    public void PlanProbe_SizesTheCodeSpaceFromTheGroupAndNotTheRig()
    {
        var singleton = Sts2SpineGeoClipProbe.PlanProbe(1, GeoClipProbeCodeScheme.UnionSafe);
        var wholeRig = Sts2SpineGeoClipProbe.PlanProbe(44, GeoClipProbeCodeScheme.UnionSafe);

        Assert.Equal(1, singleton.FrameCount);
        Assert.Single(singleton.Codes);
        Assert.Equal(8, wholeRig.FrameCount);

        // Three groups of one cost three frames between them, not three times the whole rig's schedule.
        var threeSingletons = Enumerable.Range(0, 3)
            .Sum(_ => Sts2SpineGeoClipProbe.PlanProbe(1, GeoClipProbeCodeScheme.UnionSafe).FrameCount);
        Assert.Equal(3, threeSingletons);
    }

    [Fact]
    public void PlanProbe_HandsBackNothingForAnEmptyGroup()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(0, GeoClipProbeCodeScheme.UnionSafe);

        Assert.Equal(0, plan.FrameCount);
        Assert.Empty(plan.Codes);
    }

    // Every code has to be distinct, non-zero (zero means "never moved") and inside the planned frame span,
    // under both schemes — a code outside the span could never be observed at all.
    [Theory]
    [InlineData("unionsafe")]
    [InlineData("ordinal")]
    public void PlanProbe_CodesAreDistinctNonZeroAndInsideTheFrameSpan(string schemeName)
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(44, Scheme(schemeName));
        var span = (1 << plan.FrameCount) - 1;

        Assert.Equal(44, plan.Codes.Count);
        Assert.Equal(44, plan.Codes.Distinct().Count());
        Assert.All(plan.Codes, code => Assert.InRange(code, 1, span));
    }

    // THE reason UnionSafe is the default: two distinct sets of the same size always union to a strictly
    // larger set, so a mesh that answers to two slots at once can never be read as some third slot's.
    [Fact]
    public void PlanProbe_UnionSafeCodesShareAPopCountSoNoUnionOfTwoIsAlsoACode()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(44, GeoClipProbeCodeScheme.UnionSafe);
        var weight = System.Numerics.BitOperations.PopCount((uint)plan.Codes[0]);

        Assert.All(plan.Codes, code => Assert.Equal(weight, System.Numerics.BitOperations.PopCount((uint)code)));

        var codes = plan.Codes.ToHashSet();
        for (var a = 0; a < plan.Codes.Count; a += 1)
        {
            for (var b = a + 1; b < plan.Codes.Count; b += 1)
            {
                Assert.DoesNotContain(plan.Codes[a] | plan.Codes[b], codes);
            }
        }
    }

    // The frame bound is load-bearing, not decorative: the caller loops `frame` from 0 to FrameCount, and a
    // bit set ABOVE that span would schedule a nudge in a frame the probe never runs — and `1 << frame` for a
    // large frame wraps (C# shifts by `frame & 31`) into a bit that IS in range. Slot 0's code deliberately
    // carries a bit past the plan's span here so a missing bound shows up.
    [Fact]
    public void PlanProbe_IsProbedAtReadsTheCodeBitAndRefusesAnythingOutsideThePlan()
    {
        var plan = new GeoClipProbePlan(GeoClipProbeCodeScheme.UnionSafe, 2, 2, [0b111, 0b010]);

        Assert.True(plan.IsProbedAt(0, 0));
        Assert.True(plan.IsProbedAt(0, 1));
        Assert.False(plan.IsProbedAt(0, 2));
        Assert.False(plan.IsProbedAt(0, 34));
        Assert.False(plan.IsProbedAt(1, 0));
        Assert.True(plan.IsProbedAt(1, 1));
        Assert.False(plan.IsProbedAt(2, 0));
        Assert.False(plan.IsProbedAt(-1, 0));
    }

    // ── The decode ───────────────────────────────────────────────────────────────────────────────────

    // The round trip that has to hold exactly: mesh i responded to slot i, so slot i gets mesh i and nothing
    // else, on every group size and under both schemes.
    [Theory]
    [InlineData(1, "unionsafe")]
    [InlineData(2, "unionsafe")]
    [InlineData(3, "unionsafe")]
    [InlineData(28, "unionsafe")]
    [InlineData(44, "unionsafe")]
    [InlineData(1, "ordinal")]
    [InlineData(2, "ordinal")]
    [InlineData(3, "ordinal")]
    [InlineData(44, "ordinal")]
    public void Decode_RecoversEverySlotWhenEachMeshAnswersToExactlyOne(int slots, string schemeName)
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(slots, Scheme(schemeName));

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [.. plan.Codes]);

        Assert.Equal(slots, decode.MeshesBySlot.Count);
        for (var slot = 0; slot < slots; slot += 1)
        {
            Assert.Equal([slot], decode.MeshesBySlot[slot]);
        }

        Assert.Empty(decode.MovedButUndecodable);
        Assert.Equal(0, decode.SilentMeshes);
    }

    // Meshes are NOT in slot order — the sweep hands them back in RID order — so the decode has to be by
    // code and not by position.
    [Fact]
    public void Decode_DoesNotAssumeMeshOrderMatchesSlotOrder()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(4, GeoClipProbeCodeScheme.UnionSafe);

        // mesh 0 answers to slot 3, mesh 1 to slot 0, mesh 2 to slot 2, mesh 3 to slot 1.
        var decode = Sts2SpineGeoClipProbe.Decode(
            plan,
            [plan.Codes[3], plan.Codes[0], plan.Codes[2], plan.Codes[1]]);

        Assert.Equal([1], decode.MeshesBySlot[0]);
        Assert.Equal([3], decode.MeshesBySlot[1]);
        Assert.Equal([2], decode.MeshesBySlot[2]);
        Assert.Equal([0], decode.MeshesBySlot[3]);
    }

    // A mesh nobody's colour reached: foreign geometry the bracket swept in, or a slot whose nudge the
    // animation's own colour timeline overwrote. It belongs to no slot and is not an anomaly either.
    [Fact]
    public void Decode_CountsAMeshThatMovedInNoFrameAsSilentRatherThanUndecodable()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(4, GeoClipProbeCodeScheme.UnionSafe);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[0], 0, plan.Codes[2]]);

        Assert.Equal([0], decode.MeshesBySlot[0]);
        Assert.Empty(decode.MeshesBySlot[1]);
        Assert.Equal([2], decode.MeshesBySlot[2]);
        Assert.Equal(1, decode.SilentMeshes);
        Assert.Empty(decode.MovedButUndecodable);
    }

    // A mesh that moved in EVERY probe frame is responding to something other than a slot colour. It is
    // reported, and it is NOT decoded to whatever slot happens to hold the all-bits code.
    [Fact]
    public void Decode_RefusesAMeshThatMovedInEveryFrame()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(44, GeoClipProbeCodeScheme.UnionSafe);
        var allFrames = (1 << plan.FrameCount) - 1;

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[7], allFrames]);

        Assert.Equal([0], decode.MeshesBySlot[7]);
        Assert.Equal([1], decode.MovedButUndecodable);
        Assert.Equal(1, decode.MeshesMovedInEveryFrame);
        Assert.All(decode.MeshesBySlot, meshes => Assert.DoesNotContain(1, meshes));
    }

    // …unless the all-frames code genuinely exists: a group of one is probed in one frame, and "moved in the
    // only frame there was" is exactly the right answer for it.
    [Fact]
    public void Decode_StillDecodesTheAllFramesCodeWhenTheGroupGenuinelyHoldsIt()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(1, GeoClipProbeCodeScheme.UnionSafe);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [0b1]);

        Assert.Equal([0], decode.MeshesBySlot[0]);
        Assert.Empty(decode.MovedButUndecodable);
        Assert.Equal(1, decode.MeshesMovedInEveryFrame);
    }

    // Two meshes on one slot's code is the "several meshes moved at once" case the atlas fallback already
    // exists to break. The decode must hand BOTH to that slot rather than silently picking one.
    [Fact]
    public void Decode_HandsEveryMeshOnOneCodeToThatSlotSoTheAtlasCanBreakTheTie()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(6, GeoClipProbeCodeScheme.UnionSafe);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[2], plan.Codes[2], plan.Codes[5]]);

        Assert.Equal([0, 1], decode.MeshesBySlot[2]);
        Assert.Equal([2], decode.MeshesBySlot[5]);
        Assert.Empty(decode.MovedButUndecodable);
    }

    // THE ambiguity the grouped probe introduces: one mesh driven by two slots moves in the UNION of their
    // frames. Under the union-safe code space that union is never a code, so the mesh is reported as
    // undecodable and its two slots are left for the repair probe — never handed to a third slot.
    [Fact]
    public void Decode_UnionSafeRefusesAMeshDrivenByTwoSlotsAtOnce()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(44, GeoClipProbeCodeScheme.UnionSafe);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[3] | plan.Codes[9]]);

        Assert.Equal([0], decode.MovedButUndecodable);
        Assert.Empty(decode.MeshesBySlot[3]);
        Assert.Empty(decode.MeshesBySlot[9]);
        Assert.All(decode.MeshesBySlot, meshes => Assert.Empty(meshes));
    }

    // …and the same input under the ordinal scheme lands on a THIRD slot, which is the whole reason the
    // ordinal arm is not the default. Codes 1 and 2 union to 3, which is slot 2's code.
    [Fact]
    public void Decode_OrdinalMisattributesAMeshDrivenByTwoSlots()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(44, GeoClipProbeCodeScheme.Ordinal);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[0] | plan.Codes[1]]);

        Assert.Empty(decode.MovedButUndecodable);
        Assert.Equal([0], decode.MeshesBySlot[2]);
    }

    // Bits above the plan's frame count cannot have been observed; a caller that supplies them must not be
    // able to conjure a code out of them.
    [Fact]
    public void Decode_IgnoresBitsAboveThePlannedFrameCount()
    {
        var plan = Sts2SpineGeoClipProbe.PlanProbe(3, GeoClipProbeCodeScheme.UnionSafe);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[1] | (1 << plan.FrameCount)]);

        Assert.Equal([0], decode.MeshesBySlot[1]);
        Assert.Empty(decode.MovedButUndecodable);
    }

    [Fact]
    public void Decode_ReportsNothingForAnEmptyGroup()
    {
        var decode = Sts2SpineGeoClipProbe.Decode(
            Sts2SpineGeoClipProbe.PlanProbe(0, GeoClipProbeCodeScheme.UnionSafe),
            [0, 3, 7]);

        Assert.Empty(decode.MeshesBySlot);
        Assert.Equal(3, decode.SilentMeshes);
        Assert.Empty(decode.MovedButUndecodable);
    }

    // A duplicated code would let two slots claim the same mesh, which is the one thing the `claimed` set
    // downstream cannot repair. The first slot wins and the second decodes to nothing.
    [Fact]
    public void Decode_RefusesToLetADuplicatedCodeClaimAMeshTwice()
    {
        var plan = new GeoClipProbePlan(GeoClipProbeCodeScheme.UnionSafe, 2, 2, [0b01, 0b01]);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [0b01]);

        Assert.Equal([0], decode.MeshesBySlot[0]);
        Assert.Empty(decode.MeshesBySlot[1]);
    }

    // ── The association's READ SET, and why it may never be narrowed ──────────────────────────────────

    // The other half of union-safety, and the half nothing used to cover. The tests above pin the CODE SPACE:
    // a mesh two slots drive lands on a union that is never a code, so it is refused instead of misattributed
    // (`PlanProbe_UnionSafeCodesShareAPopCountSoNoUnionOfTwoIsAlsoACode`,
    // `Decode_UnionSafeRefusesAMeshDrivenByTwoSlotsAtOnce`). But that refusal only ever happens if the mesh is
    // in the set the group READS BACK — and every group re-reads all of the swept meshes, including the ones
    // earlier groups already claimed, which looks like waste worth optimising away.
    //
    // It is not. This walks the exact rig that punishes the optimisation: mesh M is driven by a slot in an
    // earlier group (which claimed it) AND by two slots in this one. Read whole, M shows up as moved-but-
    // undecodable and buys the starved slots an exact repair. Filtered out, the group sees nothing move at all,
    // the repair predicate goes false, and the starved slots fall through to the atlas fallback with M no longer
    // even a candidate — a silent wrong pairing in place of a correct refusal.
    [Fact]
    public void Association_MustKeepReadingAMeshAnotherSlotAlreadyClaimed()
    {
        // The sweep's meshes, in RID order. M is the contended one; the other two answer to nothing here.
        const ulong meshA = 101, meshM = 202, meshC = 303;
        ulong[] swept = [meshA, meshM, meshC];

        // An earlier group's slot already paired with M, so M is claimed before this group is probed.
        var claimed = new HashSet<ulong> { meshM };

        // This group: two slots, first visible at the same frame, probed together.
        var plan = Sts2SpineGeoClipProbe.PlanProbe(2, GeoClipProbeCodeScheme.UnionSafe);
        Assert.Equal(2, plan.Codes.Count);

        // The rig itself, independent of what anyone chooses to read: M moves whenever EITHER of this group's
        // slots is nudged, so it moves in the union of their two codes.
        int MovedPositionsOf(ulong rid) => rid == meshM ? plan.Codes[0] | plan.Codes[1] : 0;
        int StarvedIn(GeoClipProbeDecode decoded)
            => Enumerable.Range(0, plan.SlotCount).Count(slot => decoded.MeshesBySlot[slot].Count == 0);

        // (a) The seam hands back the WHOLE swept list, claim or no claim.
        var readMeshes = Sts2SpineGeoClipProbe.MeshesToRead(swept, claimed);
        Assert.Contains(meshM, readMeshes);
        Assert.Equal(swept, readMeshes);

        // (b) Read whole: M's union is not a code, so it is refused — and that refusal is exactly what arms the
        // one-at-a-time repair for the two slots it starved.
        var decode = Sts2SpineGeoClipProbe.Decode(
            plan,
            [.. readMeshes.Select(MovedPositionsOf)]);

        Assert.Equal([1], decode.MovedButUndecodable);
        Assert.All(decode.MeshesBySlot, meshes => Assert.Empty(meshes));
        Assert.Equal(2, StarvedIn(decode));
        Assert.True(Sts2SpineGeoClipProbe.ShouldRepairOneAtATime(
            repairEnabled: true,
            movedButUndecodableMeshes: decode.MovedButUndecodable.Count,
            starvedSlots: StarvedIn(decode)));

        // (c) The same rig, read through the "don't re-read what's claimed" optimisation. Identical slots,
        // identical codes, identical physics — only the read set narrowed.
        var narrowed = readMeshes.Where(rid => !claimed.Contains(rid)).ToArray();
        var narrowedDecode = Sts2SpineGeoClipProbe.Decode(
            plan,
            [.. narrowed.Select(MovedPositionsOf)]);

        // Nothing moved, so there is no anomaly to report and nothing to repair…
        Assert.DoesNotContain(meshM, narrowed);
        Assert.Empty(narrowedDecode.MovedButUndecodable);
        Assert.Equal(2, StarvedIn(narrowedDecode));
        Assert.False(Sts2SpineGeoClipProbe.ShouldRepairOneAtATime(
            repairEnabled: true,
            movedButUndecodableMeshes: narrowedDecode.MovedButUndecodable.Count,
            starvedSlots: StarvedIn(narrowedDecode)));

        // …and the pool the atlas fallback would then pair those two starved slots against no longer contains
        // the mesh either of them actually drives. Whatever it picks is wrong, and nothing downstream says so.
        Assert.Equal([meshA, meshC], narrowed);
    }

    // ── The ARMED atlas-first read set, and the widen rule that is its only licence ──────────────────

    // The armed arm's read set is the optimisation the test above spends forty lines refusing, so the two must
    // be visibly different functions on the same input. If they ever became one — MeshesToRead delegating to the
    // armed spelling, say — the unarmed path would silently inherit a filter it has no widen rule behind, and
    // this is where that shows up (as does Association_MustKeepReadingAMeshAnotherSlotAlreadyClaimed above).
    [Fact]
    public void ArmedReadSet_FiltersWhatTheUnarmedSeamMustKeep()
    {
        const ulong meshA = 101, meshM = 202, meshC = 303;
        ulong[] swept = [meshA, meshM, meshC];
        var claimed = new HashSet<ulong> { meshM };

        Assert.Equal(swept, Sts2SpineGeoClipProbe.MeshesToRead(swept, claimed));
        Assert.Equal([meshA, meshC], Sts2SpineGeoClipProbe.MeshesToReadUnderAtlasFirst(swept, claimed));
    }

    // Order is load-bearing: a decode's mesh indices are indices into the list the read set returns, and the
    // caller maps them back through it. A filter that reordered would attribute meshes to the wrong slots.
    [Fact]
    public void ArmedReadSet_KeepsTheSweptOrderAndIsTheWholeListWhenNothingIsClaimed()
    {
        ulong[] swept = [909, 101, 505, 303];

        Assert.Same(swept, Sts2SpineGeoClipProbe.MeshesToReadUnderAtlasFirst(swept, new HashSet<ulong>()));
        Assert.Equal(
            [909, 505, 303],
            Sts2SpineGeoClipProbe.MeshesToReadUnderAtlasFirst(swept, new HashSet<ulong> { 101 }));
    }

    // THE WIDEN TRUTH TABLE. Each of the three anomalies is on its own, because each is a different way the
    // narrowed read set can have hidden the mesh a slot actually drives.
    [Theory]
    // Narrowed and clean: the fast path, and the only row that may skip the rerun.
    [InlineData(true, 0, 0, 0, null)]
    [InlineData(true, 1, 0, 0, Sts2SpineGeoClipProbe.WidenStarvedSlot)]
    [InlineData(true, 0, 1, 0, Sts2SpineGeoClipProbe.WidenUndecodableMesh)]
    [InlineData(true, 0, 0, 1, Sts2SpineGeoClipProbe.WidenAtlasFirstConflict)]
    // Starvation is reported first when several fire at once; it is the shape the hazard actually produces.
    [InlineData(true, 2, 3, 1, Sts2SpineGeoClipProbe.WidenStarvedSlot)]
    // NOT narrowed: the group already read every swept mesh, so a rerun would double its frames to re-derive an
    // identical answer. Every anomaly here is one the unarmed path also sees and already handles.
    [InlineData(false, 3, 3, 3, null)]
    public void WidenReason_FiresOnEachAnomalyAndOnlyWhenTheReadSetWasNarrowed(
        bool narrowed,
        int starved,
        int undecodable,
        int conflicts,
        string? expected)
        => Assert.Equal(
            expected,
            Sts2SpineGeoClipProbe.AtlasFirstWidenReason(narrowed, starved, undecodable, conflicts));

    // THE LICENCE, END TO END on the pure pieces. This is the WS-3 adversarial rig — mesh M driven by a slot
    // that already claimed it AND by two slots in this group — run the way the ARMED arm runs it. The narrowed
    // read hides M and starves both slots, which is precisely the silent-wrong-pairing setup; the widen rule
    // sees the starvation, the group is re-read whole, and the answer is the unarmed path's answer: M refused as
    // moved-but-undecodable, with the one-at-a-time repair armed behind it.
    [Fact]
    public void ArmedReadSet_StarvesTheContendedRigAndTheWidenRuleRestoresTheUnarmedAnswer()
    {
        const ulong meshA = 101, meshM = 202, meshC = 303;
        ulong[] swept = [meshA, meshM, meshC];
        var claimed = new HashSet<ulong> { meshM };

        var plan = Sts2SpineGeoClipProbe.PlanProbe(2, GeoClipProbeCodeScheme.UnionSafe);
        int MovedPositionsOf(ulong rid) => rid == meshM ? plan.Codes[0] | plan.Codes[1] : 0;
        int StarvedIn(GeoClipProbeDecode decoded)
            => Enumerable.Range(0, plan.SlotCount).Count(slot => decoded.MeshesBySlot[slot].Count == 0);

        // (a) The narrowed read: nothing moves, both slots starve, and NOTHING in the decode says why.
        var narrowedRead = Sts2SpineGeoClipProbe.MeshesToReadUnderAtlasFirst(swept, claimed);
        var narrowedDecode = Sts2SpineGeoClipProbe.Decode(plan, [.. narrowedRead.Select(MovedPositionsOf)]);
        Assert.DoesNotContain(meshM, narrowedRead);
        Assert.Empty(narrowedDecode.MovedButUndecodable);
        Assert.Equal(2, StarvedIn(narrowedDecode));

        // (b) …but the widen rule does. Starvation under a narrowed read set is never trusted.
        var reason = Sts2SpineGeoClipProbe.AtlasFirstWidenReason(
            readSetNarrowed: narrowedRead.Count < swept.Length,
            starvedSlots: StarvedIn(narrowedDecode),
            movedButUndecodableMeshes: narrowedDecode.MovedButUndecodable.Count,
            decodesConflictingWithAtlasFirst: 0);
        Assert.Equal(Sts2SpineGeoClipProbe.WidenStarvedSlot, reason);

        // (c) The widened rerun is the unarmed path, verbatim: whole read set, same plan, same physics — M's
        // union of two codes is not a code, so it is refused rather than handed to either slot, and the repair
        // that refusal arms is what recovers them exactly.
        var widenedRead = Sts2SpineGeoClipProbe.MeshesToRead(swept, claimed);
        var widenedDecode = Sts2SpineGeoClipProbe.Decode(plan, [.. widenedRead.Select(MovedPositionsOf)]);
        Assert.Equal(swept, widenedRead);
        Assert.Equal([1], widenedDecode.MovedButUndecodable);
        Assert.True(Sts2SpineGeoClipProbe.ShouldRepairOneAtATime(
            repairEnabled: true,
            movedButUndecodableMeshes: widenedDecode.MovedButUndecodable.Count,
            starvedSlots: StarvedIn(widenedDecode)));

        // …and a widened group is never widened again: it is already reading everything there is to read.
        Assert.Null(Sts2SpineGeoClipProbe.AtlasFirstWidenReason(
            readSetNarrowed: widenedRead.Count < swept.Length,
            starvedSlots: StarvedIn(widenedDecode),
            movedButUndecodableMeshes: widenedDecode.MovedButUndecodable.Count,
            decodesConflictingWithAtlasFirst: 0));
    }

    // ── The probe group's SEEK, and the one that is provably redundant ───────────────────────────────

    // A stop identifies (animation, track time), so the same stop is the same pose. The rig bake re-seeks to the
    // acquisition stop before sweeping and then probes the acquisition stop first — two awaited frames spent
    // writing the pose already on screen. Everything else pays.
    [Theory]
    // Already settled exactly where the group wants the rig: free.
    [InlineData(4, 4, 0)]
    [InlineData(0, 0, 0)]
    // A different stop is a different pose, whatever the numbers look like.
    [InlineData(4, 5, 2)]
    [InlineData(0, 7, 2)]
    // "Unknown" is the caller admitting it cannot promise where the rig is, and always pays. -1 == -1 must NOT
    // read as "settled at stop -1": that is the sentinel colliding with itself.
    [InlineData(-1, 3, 2)]
    [InlineData(-1, -1, 2)]
    public void ProbeSeekFramesNeeded_ElidesOnlyASeekToThePoseTheRigIsSettledIn(
        int settledStop,
        int groupStop,
        int expected)
        => Assert.Equal(expected, Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded(settledStop, groupStop));

    [Fact]
    public void ProbeSeekFramesNeeded_ChargesTheCallersOwnSeekCost()
    {
        Assert.Equal(2, Sts2SpineGeoClipProbe.DefaultProbeSeekFrames);
        Assert.Equal(5, Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded(1, 2, seekFrames: 5));
        Assert.Equal(0, Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded(2, 2, seekFrames: 5));
        Assert.Equal(0, Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded(1, 2, seekFrames: 0));
    }

    // ── The probe's environment knobs ────────────────────────────────────────────────────────────────

    [Fact]
    public void ProbeSettings_DefaultToTheUnionSafeSchemeWithTheRepairArmed()
    {
        var settings = GeoClipProbeSettings.Parse(null, null);

        Assert.Equal(GeoClipProbeCodeScheme.UnionSafe, settings.Scheme);
        Assert.True(settings.RepairEnabled);
        Assert.Equal(GeoClipProbeSettings.DefaultMaxRepairSlots, settings.MaxRepairSlots);
    }

    [Theory]
    [InlineData("ordinal", true)]
    [InlineData("BINARY", true)]
    [InlineData(" Ordinal ", true)]
    [InlineData("unionsafe", false)]
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void ProbeSettings_SelectTheSchemeByName(string raw, bool expectOrdinal)
        => Assert.Equal(
            expectOrdinal ? GeoClipProbeCodeScheme.Ordinal : GeoClipProbeCodeScheme.UnionSafe,
            GeoClipProbeSettings.Parse(raw, null).Scheme);

    [Theory]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("", true)]
    public void ProbeSettings_TurnTheRepairArmOffOnlyOnAnExplicitDenial(string raw, bool expected)
        => Assert.Equal(expected, GeoClipProbeSettings.Parse(null, raw).RepairEnabled);

    // The atlas-first knob now reads the SAME way round as the repair knob beside it: a kill switch, where only
    // an explicit off-word declines and absent, empty or misspelt arms. The affirmative spellings are kept as
    // spellings — they are what every recorded measurement arm passed — but they no longer carry the default.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("shadow", true)]
    [InlineData("yes please", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("Yes", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    public void ProbeSettings_TurnTheAtlasFirstAssociationOffOnlyOnAnExplicitDenial(string? raw, bool expected)
        => Assert.Equal(expected, GeoClipProbeSettings.Parse(null, null, null, raw).AtlasFirstArmed);

    [Fact]
    public void ProbeSettings_DefaultToTheArmedAtlasFirstArm()
    {
        Assert.True(GeoClipProbeSettings.Default.AtlasFirstArmed);
        Assert.True(GeoClipProbeSettings.Parse(null, null).AtlasFirstArmed);
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_ASSOC_ATLAS_FIRST", GeoClipProbeSettings.AtlasFirstEnv);
    }

    // ── The frame budget and its lease ───────────────────────────────────────────────────────────────

    [Fact]
    public void FrameBudget_DefaultsToUncappingTheEngine()
    {
        var budget = GeoClipFrameBudget.Parse(null, null);

        Assert.True(budget.RaiseMaxFps);
        Assert.Equal(GeoClipFrameBudget.UncappedMaxFps, budget.MaxFpsTarget);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("FALSE")]
    [InlineData("no")]
    public void FrameBudget_KillSwitchDisablesTheLeverEntirely(string raw)
        => Assert.False(GeoClipFrameBudget.Parse(raw, "240").RaiseMaxFps);

    [Theory]
    [InlineData("240", 240)]
    [InlineData("120", 120)]
    [InlineData("-5", GeoClipFrameBudget.UncappedMaxFps)]
    [InlineData("nonsense", GeoClipFrameBudget.UncappedMaxFps)]
    [InlineData("", GeoClipFrameBudget.UncappedMaxFps)]
    public void FrameBudget_TakesAnExplicitFiniteCapWhenOneIsNamed(string raw, int expected)
        => Assert.Equal(expected, GeoClipFrameBudget.Parse(null, raw).MaxFpsTarget);

    // The lease must put back what it OBSERVED, never a constant — the host's cap comes from the player's own
    // graphics setting, and a bake that "restored" 60 on a 144 Hz machine would be a visible regression.
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(30)]
    public void FrameRateLease_RestoresTheObservedPriorCapAndNotAConstant(int prior)
    {
        var current = prior;
        var writes = new List<int>();

        using (var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse(null, null),
            () => current,
            value =>
            {
                current = value;
                writes.Add(value);
            }))
        {
            Assert.True(lease.Raised);
            Assert.Equal(prior, lease.PriorMaxFps);
            Assert.Equal(GeoClipFrameBudget.UncappedMaxFps, current);
        }

        Assert.Equal(prior, current);
        Assert.Equal([GeoClipFrameBudget.UncappedMaxFps, prior], writes);
    }

    // The bake body throws on a rig it cannot read, and it must not leave the host uncapped when it does.
    [Fact]
    public void FrameRateLease_RestoresOnTheThrowPathToo()
    {
        var current = 60;

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = GeoClipFrameRateLease.Acquire(
                GeoClipFrameBudget.Parse(null, null),
                () => current,
                value => current = value);
            Assert.Equal(GeoClipFrameBudget.UncappedMaxFps, current);
            throw new InvalidOperationException("the bake threw");
        }));

        Assert.Equal(60, current);
    }

    [Fact]
    public void FrameRateLease_TouchesNothingWhenTheKillSwitchIsSet()
    {
        var reads = 0;
        var writes = 0;

        using var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse("0", null),
            () =>
            {
                reads += 1;
                return 60;
            },
            _ => writes += 1);
        lease.Dispose();

        Assert.False(lease.Raised);
        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void FrameRateLease_DoesNotWriteWhenTheCapIsAlreadyTheTarget()
    {
        var writes = 0;

        using var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse(null, "240"),
            () => 240,
            _ => writes += 1);
        lease.Dispose();

        Assert.False(lease.Raised);
        Assert.Equal(0, writes);
        Assert.Contains("already", lease.Note);
    }

    // The engine hop can refuse in either direction on a host mid-teardown, and neither refusal may take the
    // bake down or leave a raised cap behind.
    [Fact]
    public void FrameRateLease_SurvivesAReadThatThrows()
    {
        var writes = 0;

        using var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse(null, null),
            () => throw new InvalidOperationException("no engine"),
            _ => writes += 1);

        Assert.False(lease.Raised);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void FrameRateLease_PutsBackThePriorCapWhenTheRaisingWriteThrows()
    {
        var writes = new List<int>();

        using var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse(null, "240"),
            () => 60,
            value =>
            {
                writes.Add(value);
                if (value == 240)
                {
                    throw new InvalidOperationException("refused");
                }
            });

        Assert.False(lease.Raised);
        Assert.Equal([240, 60], writes);
    }

    [Fact]
    public void FrameRateLease_RestoresExactlyOnceHoweverOftenItIsDisposed()
    {
        var writes = new List<int>();
        var lease = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.Parse(null, null),
            () => 60,
            writes.Add);

        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        Assert.Equal([GeoClipFrameBudget.UncappedMaxFps, 60], writes);
    }
}
