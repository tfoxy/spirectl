using System.Numerics;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// CHANNEL-PARALLEL probe codes: the association's cost is readbacks and awaited frames, and both are divided
// by putting one probe bit on each colour COMPONENT instead of one bit per frame on alpha.
//
// The measurement is unchanged — a slot's colour is nudged and the mesh whose vertex colours move is its mesh.
// What changed is that one readback already yields a per-component sum, so a frame can carry three or four
// independent bits for exactly the device stall one bit used to cost. A 44-slot group falls from 8 awaited
// frames and 9 reads per mesh to 3 and 4.
//
// Everything here is a pure function of "plan" and "mesh -> set of positions it moved in", so all of it is
// provable against a game that cannot be run in CI. What is NOT provable here is whether the renderer puts a
// slot's R, G and B through to vertex colours at all — that is what the dead-channel guard and the alpha
// re-probe exist for, and both of THOSE are pinned below.
public sealed class Sts2SpineGeoClipProbeChannelTests
{
    // The channel set is an internal enum and an xunit theory has to be public, so arms name it by the same
    // string the environment knob takes — exactly the way the scheme arms already do.
    private static GeoClipProbeChannels Channels(string name) => GeoClipProbeSettings.Parse(null, null, name).Channels;

    private static GeoClipProbePlan Plan(int slots, GeoClipProbeChannels channels)
        => Sts2SpineGeoClipProbe.PlanProbe(slots, GeoClipProbeCodeScheme.UnionSafe, channels);

    private static GeoClipProbePlan Plan(int slots, string channels) => Plan(slots, Channels(channels));

    // ── The schedule ─────────────────────────────────────────────────────────────────────────────────

    // THE HEADLINE. Both real rigs' groups, and the sizes around them. The alpha column is what the bake
    // costs today, so a regression to it is loud.
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(6, 4, 2)]
    [InlineData(28, 7, 3)]
    [InlineData(44, 8, 3)]
    [InlineData(200, 10, 4)]
    public void PlanProbe_RgbCostsFewerAwaitedFramesThanTheAlphaSchedule(
        int slots,
        int alphaFrames,
        int expectedRgbFrames)
    {
        Assert.Equal(alphaFrames, Plan(slots, GeoClipProbeChannels.Alpha).FrameCount);
        Assert.Equal(expectedRgbFrames, Plan(slots, GeoClipProbeChannels.Rgb).FrameCount);
    }

    // The merchant's group, spelled out: this is the whole saving, in the two quantities that cost time.
    [Fact]
    public void PlanProbe_TheMerchantsGroupCostsThreeFramesAndFourReadsPerMeshInsteadOfEightAndNine()
    {
        var alpha = Plan(44, GeoClipProbeChannels.Alpha);
        var rgb = Plan(44, GeoClipProbeChannels.Rgb);

        Assert.Equal(8, alpha.FrameCount);
        Assert.Equal(3, rgb.FrameCount);

        // One baseline read per mesh plus one per probe frame.
        Assert.Equal(9, alpha.FrameCount + 1);
        Assert.Equal(4, rgb.FrameCount + 1);
    }

    // The alpha arm must be the OLD plan and not merely a plan with one channel: same frame count, same codes,
    // in the same order. Every recorded association pair was produced under exactly these codes.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(28)]
    [InlineData(44)]
    [InlineData(200)]
    public void PlanProbe_TheAlphaArmIsTheScheduleThatExistedBeforeChannels(int slots)
    {
        var legacy = Sts2SpineGeoClipProbe.PlanProbe(slots, GeoClipProbeCodeScheme.UnionSafe);
        var alpha = Plan(slots, GeoClipProbeChannels.Alpha);

        Assert.Equal(legacy.FrameCount, alpha.FrameCount);
        Assert.Equal(legacy.PositionCount, alpha.FrameCount);
        Assert.Equal(legacy.Codes, alpha.Codes);
    }

    // A position is (frame, channel), numbered frame * ChannelCount + channel, so the observable width is the
    // product and not the frame count.
    [Theory]
    [InlineData("alpha", 1)]
    [InlineData("rgb", 3)]
    [InlineData("rgba", 4)]
    public void PlanProbe_PositionsAreFramesTimesChannels(string channels, int expectedChannels)
    {
        var plan = Plan(44, channels);

        Assert.Equal(expectedChannels, plan.ChannelCount);
        Assert.Equal(plan.FrameCount * expectedChannels, plan.PositionCount);
    }

    // Codes have to fit inside the positions the probe will actually visit; a bit above the span could never
    // be observed, and `1 << position` for a large position wraps (C# shifts by `position & 31`) into a bit
    // that IS in range.
    [Theory]
    [InlineData("alpha")]
    [InlineData("rgb")]
    [InlineData("rgba")]
    public void PlanProbe_CodesAreDistinctNonZeroAndInsideThePositionSpan(string channels)
    {
        foreach (var slots in new[] { 1, 2, 7, 28, 44, 200 })
        {
            var plan = Plan(slots, channels);
            var span = (1 << plan.PositionCount) - 1;

            Assert.Equal(slots, plan.Codes.Count);
            Assert.Equal(slots, plan.Codes.Distinct().Count());
            Assert.All(plan.Codes, code => Assert.InRange(code, 1, span));
        }
    }

    // The union-safe property has to survive the wider space, because the repair pass depends on it: two
    // distinct sets of the same size always union to a strictly larger one, so a mesh two slots drive can
    // never be read as some third slot's.
    [Theory]
    [InlineData("rgb")]
    [InlineData("rgba")]
    public void PlanProbe_ChannelCodesStillShareAPopCountSoNoUnionOfTwoIsAlsoACode(string channels)
    {
        var plan = Plan(44, channels);
        var weight = BitOperations.PopCount((uint)plan.Codes[0]);

        Assert.All(plan.Codes, code => Assert.Equal(weight, BitOperations.PopCount((uint)code)));

        var codes = plan.Codes.ToHashSet();
        for (var a = 0; a < plan.Codes.Count; a += 1)
        {
            for (var b = a + 1; b < plan.Codes.Count; b += 1)
            {
                Assert.DoesNotContain(plan.Codes[a] | plan.Codes[b], codes);
            }
        }
    }

    // Codes are ints, and rounding the width up to a whole number of frames could push it past the shift
    // bound if nothing held it. The span must stay inside that bound, and every code inside the span, at any
    // group size a rig could present — a million slots is four orders of magnitude past the merchant's 44.
    // (The planner allocates one code per slot, so the array bound bites long before the shift bound could.)
    [Theory]
    [InlineData("alpha")]
    [InlineData("rgb")]
    [InlineData("rgba")]
    public void PlanProbe_NeverPlansMorePositionsThanACodeCanHold(string channels)
    {
        foreach (var scheme in new[] { GeoClipProbeCodeScheme.UnionSafe, GeoClipProbeCodeScheme.Ordinal })
        {
            foreach (var slots in new[] { 1, 44, 1_000, 100_000, 1_000_000 })
            {
                var plan = Sts2SpineGeoClipProbe.PlanProbe(slots, scheme, Channels(channels));

                Assert.InRange(plan.PositionCount, 1, Sts2SpineGeoClipProbe.MaxProbePositions);
                Assert.Equal(plan.FrameCount * plan.ChannelCount, plan.PositionCount);
                Assert.All(plan.Codes, code => Assert.InRange(code, 1, (1 << plan.PositionCount) - 1));
            }
        }
    }

    [Fact]
    public void PlanProbe_HandsBackNothingForAnEmptyGroupOnEveryChannelSet()
    {
        foreach (var channels in new[] { "alpha", "rgb", "rgba" })
        {
            var plan = Plan(0, channels);

            Assert.Equal(0, plan.FrameCount);
            Assert.Equal(0, plan.PositionCount);
            Assert.Empty(plan.Codes);
        }
    }

    // `IsProbedAt` indexes POSITIONS now. A three-channel three-frame plan has nine of them, and the tenth
    // must be refused however the code's bits fall.
    [Fact]
    public void PlanProbe_IsProbedAtIndexesPositionsAndRefusesAnythingOutsideThem()
    {
        var plan = new GeoClipProbePlan(
            GeoClipProbeCodeScheme.UnionSafe, 1, 3, [0b1_0000_0001], GeoClipProbeChannels.Rgb);

        Assert.Equal(9, plan.PositionCount);
        Assert.True(plan.IsProbedAt(0, 0));
        Assert.False(plan.IsProbedAt(0, 1));
        Assert.True(plan.IsProbedAt(0, 8));
        Assert.False(plan.IsProbedAt(0, 9));
        Assert.False(plan.IsProbedAt(0, 41));
        Assert.False(plan.IsProbedAt(0, -1));
        Assert.False(plan.IsProbedAt(1, 0));
    }

    // ── Which component carries which bit, and which sum reads it back ───────────────────────────────

    // The load-bearing detail of the alpha arm: it nudges the ALPHA component and reads the COMBINED
    // checksum, which is the predicate every recorded pair was produced under. Reading the A sum instead
    // would be a different measurement wearing the same name.
    [Fact]
    public void ChannelsOf_AlphaNudgesAlphaAndReadsTheCombinedChecksum()
    {
        var channels = Sts2SpineGeoClipProbe.ChannelsOf(GeoClipProbeChannels.Alpha);

        Assert.Single(channels);
        Assert.Equal(3, channels[0].Component);
        Assert.Equal(GeoClipColorSums.CombinedIndex, channels[0].Sum);
    }

    // The multi-channel arms read each component out of its OWN sum, so a nudge on one cannot be seen as a
    // nudge on another. A shared sum would make every channel report every channel's movement.
    [Theory]
    [InlineData("rgb", 3)]
    [InlineData("rgba", 4)]
    public void ChannelsOf_EachChannelDrivesItsOwnComponentAndReadsItsOwnSum(
        string channels,
        int expectedCount)
    {
        var mapped = Sts2SpineGeoClipProbe.ChannelsOf(Channels(channels));

        Assert.Equal(expectedCount, mapped.Count);
        Assert.Equal(expectedCount, mapped.Select(channel => channel.Component).Distinct().Count());
        Assert.Equal(expectedCount, mapped.Select(channel => channel.Sum).Distinct().Count());
        Assert.All(mapped, channel => Assert.NotEqual(GeoClipColorSums.CombinedIndex, channel.Sum));

        // R, G, B (and A) in that order, each reading the sum of the component it drives.
        for (var channel = 0; channel < mapped.Count; channel += 1)
        {
            Assert.Equal(channel, mapped[channel].Component);
            Assert.Equal(channel + 1, mapped[channel].Sum);
        }
    }

    // The nudge rule itself, which the alpha probe has always used: push the component to the far side of the
    // range, so the movement is at least 0.25 whatever the slot started at.
    [Theory]
    [InlineData(1f, 0.25f)]
    [InlineData(0.61960787f, 0.25f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(0f, 0.75f)]
    public void NudgeValue_PushesAComponentWellAwayFromWhereverItSits(float from, float expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipProbe.NudgeValue(from));
        Assert.True(Math.Abs(Sts2SpineGeoClipProbe.NudgeValue(from) - from) >= 0.25f);
    }

    // ── The colour a slot actually wears, frame by frame ─────────────────────────────────────────────
    //
    // This is the half of the probe that lives in the live baker and cannot be compiled here, so the DECISION
    // was pulled into the core and only a two-line Godot adapter was left behind. If the nudged colour and the
    // decode disagreed about which bit means which (frame, channel), every pairing would be wrong.

    // Frames run slowest, so one frame's channels are adjacent bits and the probe visits positions in order.
    [Fact]
    public void PositionOf_NumbersChannelsWithinAFrame()
    {
        Assert.Equal(0, Sts2SpineGeoClipProbe.PositionOf(0, 0, 3));
        Assert.Equal(2, Sts2SpineGeoClipProbe.PositionOf(0, 2, 3));
        Assert.Equal(3, Sts2SpineGeoClipProbe.PositionOf(1, 0, 3));
        Assert.Equal(8, Sts2SpineGeoClipProbe.PositionOf(2, 2, 3));

        // With one channel a position IS a frame, which is what makes the alpha arm the old schedule.
        Assert.Equal(7, Sts2SpineGeoClipProbe.PositionOf(7, 0, 1));
    }

    // THE ROUND TRIP that the whole change rests on. Nudge every slot exactly the way the plan says for each
    // frame, watch which components of which slot moved, rebuild the position set from that — and the decode
    // must hand every mesh back to the slot it came from. This is the live loop with the engine taken out.
    [Theory]
    [InlineData(1, "rgb")]
    [InlineData(6, "rgb")]
    [InlineData(28, "rgb")]
    [InlineData(44, "rgb")]
    [InlineData(44, "rgba")]
    [InlineData(44, "alpha")]
    public void ProbeColorFor_DrivesExactlyTheComponentsTheDecodeThenReadsBack(int slots, string channelName)
    {
        var plan = Plan(slots, channelName);
        var channels = Sts2SpineGeoClipProbe.ChannelsOf(Channels(channelName));

        // Every slot starts white and opaque, the way 67 of the merchant's 71 slots do.
        float[] baseColor = [1f, 1f, 1f, 1f];
        var observed = new int[slots];

        for (var frame = 0; frame < plan.FrameCount; frame += 1)
        {
            for (var slot = 0; slot < slots; slot += 1)
            {
                var probe = Sts2SpineGeoClipProbe.ProbeColorFor(
                    plan, slot, frame, baseColor[0], baseColor[1], baseColor[2], baseColor[3]);
                float[] after = [probe.R, probe.G, probe.B, probe.A];

                // The mesh this slot drives moves on exactly the components whose value changed.
                for (var channel = 0; channel < channels.Count; channel += 1)
                {
                    if (after[channels[channel].Component] != baseColor[channels[channel].Component])
                    {
                        observed[slot] |= 1
                            << Sts2SpineGeoClipProbe.PositionOf(frame, channel, channels.Count);
                    }
                }
            }
        }

        var decode = Sts2SpineGeoClipProbe.Decode(plan, observed);

        for (var slot = 0; slot < slots; slot += 1)
        {
            Assert.Equal([slot], decode.MeshesBySlot[slot]);
        }

        Assert.Empty(decode.MovedButUndecodable);
        Assert.Equal(0, decode.SilentMeshes);
        Assert.Equal(0, Sts2SpineGeoClipProbe.DeadChannels(plan, observed));
    }

    // A component the slot is NOT probed on at this frame must come back untouched — a probe that rewrote all
    // four every frame would make every mesh move in every position and decode to nothing.
    [Fact]
    public void ProbeColorFor_LeavesEveryComponentTheSlotIsNotProbedOnAlone()
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);
        var untouched = 0;

        for (var frame = 0; frame < plan.FrameCount; frame += 1)
        {
            for (var slot = 0; slot < plan.SlotCount; slot += 1)
            {
                var probe = Sts2SpineGeoClipProbe.ProbeColorFor(plan, slot, frame, 1f, 1f, 1f, 0.5f);

                // Alpha is not one of RGB's channels, so it is never written whatever the code says.
                Assert.Equal(0.5f, probe.A);

                float[] after = [probe.R, probe.G, probe.B];
                for (var channel = 0; channel < 3; channel += 1)
                {
                    var probed = plan.IsProbedAt(slot, Sts2SpineGeoClipProbe.PositionOf(frame, channel, 3));
                    Assert.Equal(probed ? 0.25f : 1f, after[channel]);
                    untouched += probed ? 0 : 1;
                }
            }
        }

        Assert.True(untouched > 0, "every component of every slot was probed, so the assertion is vacuous");
    }

    // The alpha arm has to produce the colour the one-at-a-time probe produced: RGB kept, alpha flipped.
    [Fact]
    public void ProbeColorFor_TheAlphaArmMovesAlphaAndNothingElse()
    {
        var plan = Plan(1, GeoClipProbeChannels.Alpha);

        var probe = Sts2SpineGeoClipProbe.ProbeColorFor(plan, 0, 0, 0.61960787f, 0.3f, 0.9f, 1f);

        Assert.Equal(0.61960787f, probe.R);
        Assert.Equal(0.3f, probe.G);
        Assert.Equal(0.9f, probe.B);
        Assert.Equal(0.25f, probe.A);
    }

    // A slot outside the plan, or a frame past its end, is never nudged — the loop bounds are the plan's, and
    // a colour written for a frame that is not run would be left on the rig.
    [Fact]
    public void ProbeColorFor_RefusesSlotsAndFramesOutsideThePlan()
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);

        Assert.Equal((1f, 1f, 1f, 1f), Sts2SpineGeoClipProbe.ProbeColorFor(plan, 44, 0, 1f, 1f, 1f, 1f));
        Assert.Equal((1f, 1f, 1f, 1f), Sts2SpineGeoClipProbe.ProbeColorFor(plan, -1, 0, 1f, 1f, 1f, 1f));
        Assert.Equal(
            (1f, 1f, 1f, 1f),
            Sts2SpineGeoClipProbe.ProbeColorFor(plan, 0, plan.FrameCount, 1f, 1f, 1f, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NudgeComponent_MovesOneComponentAndKeepsTheOtherThree(int component)
    {
        var nudged = Sts2SpineGeoClipProbe.NudgeComponent(0.1f, 0.2f, 0.3f, 0.4f, component);
        float[] before = [0.1f, 0.2f, 0.3f, 0.4f];
        float[] after = [nudged.R, nudged.G, nudged.B, nudged.A];

        for (var i = 0; i < 4; i += 1)
        {
            if (i == component)
            {
                Assert.Equal(Sts2SpineGeoClipProbe.NudgeValue(before[i]), after[i]);
                Assert.NotEqual(before[i], after[i]);
            }
            else
            {
                Assert.Equal(before[i], after[i]);
            }
        }
    }

    // ── The decode over the wider space ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "rgb")]
    [InlineData(2, "rgb")]
    [InlineData(28, "rgb")]
    [InlineData(44, "rgb")]
    [InlineData(200, "rgb")]
    [InlineData(44, "rgba")]
    public void Decode_RecoversEverySlotWhenEachMeshAnswersOnEveryChannelOfItsCode(
        int slots,
        string channels)
    {
        var plan = Plan(slots, channels);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [.. plan.Codes]);

        for (var slot = 0; slot < slots; slot += 1)
        {
            Assert.Equal([slot], decode.MeshesBySlot[slot]);
        }

        Assert.Empty(decode.MovedButUndecodable);
        Assert.Equal(0, decode.SilentMeshes);
    }

    // THE CORRECTNESS ARGUMENT of the whole change, exhaustively. A slot whose nudge fails to register on some
    // of its channels — what premultiplied alpha would do to a slot sitting at alpha zero — yields a mesh whose
    // observed positions are a STRICT SUBSET of its code. Every valid code has the same popcount, so a strict
    // subset is never any code: the mesh is reported undecodable and the slot starves into the repair pass. It
    // is never handed to a different slot, which is the outcome that would bake one slot's art onto another.
    [Theory]
    [InlineData("rgb")]
    [InlineData("rgba")]
    public void Decode_APartiallyAnsweredCodeIsUndecodableAndNeverAnotherSlots(string channels)
    {
        var plan = Plan(44, channels);
        var checkedSubsets = 0;

        for (var slot = 0; slot < plan.Codes.Count; slot += 1)
        {
            var code = plan.Codes[slot];
            var bits = Enumerable.Range(0, plan.PositionCount).Where(bit => (code & (1 << bit)) != 0).ToArray();

            // Every proper, non-empty subset of this slot's positions.
            for (var subset = 1; subset < (1 << bits.Length) - 1; subset += 1)
            {
                var observed = 0;
                for (var bit = 0; bit < bits.Length; bit += 1)
                {
                    if ((subset & (1 << bit)) != 0)
                    {
                        observed |= 1 << bits[bit];
                    }
                }

                var decode = Sts2SpineGeoClipProbe.Decode(plan, [observed]);

                Assert.Equal([0], decode.MovedButUndecodable);
                Assert.All(decode.MeshesBySlot, meshes => Assert.Empty(meshes));
                checkedSubsets += 1;
            }
        }

        // The loop has to have done work; a weight-1 code space would make every assertion above vacuous.
        Assert.True(checkedSubsets >= 44, $"only {checkedSubsets} partial answers were exercised");
    }

    [Fact]
    public void Decode_StillRefusesAMeshDrivenByTwoSlotsWhenTheCodesSpanChannels()
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[3] | plan.Codes[9]]);

        Assert.Equal([0], decode.MovedButUndecodable);
        Assert.All(decode.MeshesBySlot, meshes => Assert.Empty(meshes));
    }

    // Bits above the POSITION span (not merely above the frame count) cannot have been observed.
    [Fact]
    public void Decode_IgnoresBitsAboveThePositionSpan()
    {
        var plan = Plan(28, GeoClipProbeChannels.Rgb);

        var decode = Sts2SpineGeoClipProbe.Decode(plan, [plan.Codes[5] | (1 << plan.PositionCount)]);

        Assert.Equal([0], decode.MeshesBySlot[5]);
        Assert.Empty(decode.MovedButUndecodable);
    }

    // ── The dead-channel guard ───────────────────────────────────────────────────────────────────────

    // A whole component the renderer never puts through is the one failure union-safety does NOT cover: every
    // code loses the same positions, so a mesh two slots drive can lose enough of the union to land back on a
    // third slot's code and be PAIRED to it. So it is detected globally rather than waited on.
    [Fact]
    public void DeadChannels_CountsAChannelTheProbeDrivesThatNothingAnsweredOn()
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);
        var green = 0;
        var everything = 0;
        for (var position = 0; position < plan.PositionCount; position += 1)
        {
            everything |= 1 << position;
            if (position % 3 == 1)
            {
                green |= 1 << position;
            }
        }

        // Every mesh answered on R and B but never on G.
        var observed = plan.Codes.Select(code => code & ~green).ToArray();

        Assert.Equal(1, Sts2SpineGeoClipProbe.DeadChannels(plan, observed));
        Assert.Equal(0, Sts2SpineGeoClipProbe.DeadChannels(plan, [.. plan.Codes]));
        Assert.Equal(3, Sts2SpineGeoClipProbe.DeadChannels(plan, [0, 0, 0]));
        Assert.Equal(0, Sts2SpineGeoClipProbe.DeadChannels(plan, [everything]));
    }

    // A channel no CODE uses is unused, not dead — a small group's codes need not reach every position the
    // plan offers, and re-probing because an unused channel was quiet would fire on every such group.
    [Fact]
    public void DeadChannels_DoesNotCountAChannelNoCodeEvenDrives()
    {
        // One slot, three channels: its code is a single bit, so two of the three channels carry nothing.
        var plan = Plan(1, GeoClipProbeChannels.Rgb);

        Assert.Equal(3, plan.ChannelCount);
        Assert.Equal(1, BitOperations.PopCount((uint)plan.Codes[0]));
        Assert.Equal(0, Sts2SpineGeoClipProbe.DeadChannels(plan, [.. plan.Codes]));
    }

    [Fact]
    public void DeadChannels_ReportsNothingForAnEmptyPlan()
        => Assert.Equal(0, Sts2SpineGeoClipProbe.DeadChannels(Plan(0, GeoClipProbeChannels.Rgb), [1, 2, 3]));

    // ── When a group is re-probed on alpha ───────────────────────────────────────────────────────────

    // The alpha arm IS the fallback, so it can never ask for one — that would loop.
    [Fact]
    public void ShouldReprobeUnderAlpha_IsNeverTrueForAnAlphaPlan()
    {
        var plan = Plan(44, GeoClipProbeChannels.Alpha);

        Assert.False(Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(plan, [0, 0], 44, 16));
    }

    // A dead channel re-probes immediately, whatever the starved count says, because that is the correctness
    // case rather than the cost one.
    [Fact]
    public void ShouldReprobeUnderAlpha_FiresOnADeadChannelEvenWithNoSlotStarved()
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);
        var green = 0;
        for (var position = 1; position < plan.PositionCount; position += 3)
        {
            green |= 1 << position;
        }

        Assert.True(Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(
            plan,
            [.. plan.Codes.Select(code => code & ~green)],
            starvedSlots: 0,
            maxRepairSlots: 16));
    }

    // The cost crossover: the one-at-a-time repair is exact and costs two awaited frames per starved slot, so
    // it stays in charge while it is cheaper than re-running the 8-frame group probe plus its restore frame.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(20, true)]
    public void ShouldReprobeUnderAlpha_LetsTheRepairHandleWhatItIsTheCheaperAnswerFor(
        int starved,
        bool expected)
    {
        var plan = Plan(44, GeoClipProbeChannels.Rgb);

        Assert.Equal(9, Plan(44, GeoClipProbeChannels.Alpha).FrameCount + 1);
        Assert.Equal(
            expected,
            Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(plan, [.. plan.Codes], starved, maxRepairSlots: 16));
    }

    // Past the repair's own cap the surplus slots would fall through to the atlas fallback, where the PAIRS
    // could move — so the whole group goes back to alpha instead, however cheap the repair looked.
    //
    // At the SHIPPED cap of 16 this clause never gets to decide anything: an alpha plan spans at most
    // MaxProbePositions frames, so the cost clause has already fired by 16 starved slots. The cap is therefore
    // exercised at a cap the caller could set rather than at the one it does, because a test that fed it 17
    // starved slots at cap 16 would pass with the clause deleted — which is exactly how the mutation run caught
    // the first version of this test.
    [Fact]
    public void ShouldReprobeUnderAlpha_FiresPastTheRepairCapEvenWhenTheRepairWouldStillBeCheaper()
    {
        var plan = Plan(6, GeoClipProbeChannels.Rgb);

        // Two starved slots repair in four awaited frames against the alpha probe's five, so cost says wait.
        Assert.Equal(5, Plan(6, GeoClipProbeChannels.Alpha).FrameCount + 1);
        Assert.False(Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(plan, [.. plan.Codes], 2, maxRepairSlots: 16));

        // …but a repair that can only cover one of them would leave the other to the atlas fallback.
        Assert.True(Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(plan, [.. plan.Codes], 2, maxRepairSlots: 1));
    }

    // The clause above is dominated at the shipped constants, and that is a claim about the code rather than a
    // hope: an alpha plan cannot span more frames than a code has bits, so the cost clause fires first at every
    // reachable group size. If either constant moves, this is the test that says so.
    [Fact]
    public void ShouldReprobeUnderAlpha_CostFiresBeforeTheShippedRepairCapAtEveryReachableGroupSize()
    {
        foreach (var slots in new[] { 2, 6, 28, 44, 200, 100_000 })
        {
            var alphaFrames = Plan(slots, GeoClipProbeChannels.Alpha).FrameCount + 1;
            var costFiresAt = (alphaFrames / 2) + 1;

            Assert.True(
                costFiresAt <= GeoClipProbeSettings.DefaultMaxRepairSlots,
                $"{slots} slots: cost fires at {costFiresAt} starved, past the cap of "
                + $"{GeoClipProbeSettings.DefaultMaxRepairSlots}");
        }
    }

    // ── The environment knob ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProbeSettings_DefaultToRgbSoTheSavingIsOnWithoutAnEnvironmentVariable()
        => Assert.Equal(GeoClipProbeChannels.Rgb, GeoClipProbeSettings.Parse(null, null).Channels);

    [Theory]
    [InlineData("alpha", 1)]
    [InlineData(" LEGACY ", 1)]
    [InlineData("a", 1)]
    [InlineData("rgba", 4)]
    [InlineData("rgb", 3)]
    [InlineData("nonsense", 3)]
    [InlineData("", 3)]
    public void ProbeSettings_SelectTheChannelSetByName(string raw, int expectedChannelCount)
        => Assert.Equal(
            expectedChannelCount,
            Sts2SpineGeoClipProbe.ChannelsOf(GeoClipProbeSettings.Parse(null, null, raw).Channels).Count);
}
