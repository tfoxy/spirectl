using System;
using System.Collections.Generic;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The on-demand geoclip lane's REFUSAL MEMO: the completeness rule that decides a bake was refused, the identity
// a refusal is remembered under, and the protocol that turns "already refused" into an O(1) answer.
//
// What is actually being protected here is a cost. A bake that runs and still fails to acquire the whole rig is
// not cheaper than one that succeeds — an embedded host measured a median 1.45s and a p90 4.15s per identity —
// so a lane that re-derives the same refusal is spending seconds to reach a conclusion it already reached. The
// tests below pin the two halves that make that safe: the key has to include everything the verdict depends on
// (or the memo answers a question it was not asked), and nothing but a refusal may ever be recorded.
public sealed class Sts2SpineGeoClipRefusalMemoTests
{
    private const string Levers = "probe=UnionSafe/1/Rgb/1;pause=0/Bounds;dense=1";

    private const string Bridge = "spirectl-bridge/0.1.0";

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    // ---- The completeness rule ------------------------------------------------------------------------------

    // The arm table. Ordered by how much each arm explains: an incomplete bake's association numbers are not
    // evidence about anything, and an unassociated slot makes a foreign-mesh count unreadable, so the first arm
    // that fires is the one reported.
    [Theory]
    [InlineData(false, 40, 44, 0, "complete=false", "incomplete")]
    [InlineData(false, 44, 44, 0, "complete=false", "incomplete")]
    [InlineData(true, 40, 44, 0, "associated=40 of slotsEverVisible=44", "unassociated")]
    [InlineData(true, 0, 1, 3, "associated=0 of slotsEverVisible=1", "unassociated")]
    [InlineData(true, 44, 44, 2, "foreignMeshes=2", "foreign")]
    public void IncompletenessReason_NamesTheFirstArmThatFires(
        bool complete,
        int associated,
        int slotsVisible,
        int foreignMeshes,
        string expectedReason,
        string expectedArm)
    {
        var reason = Sts2SpineGeoClipRequestLane.IncompletenessReason(
            complete, associated, slotsVisible, foreignMeshes);

        Assert.Equal(expectedReason, reason);
        Assert.Equal(expectedArm, Sts2SpineGeoClipRequestLane.ClassifyRefusal(reason));
    }

    // A whole rig, every slot associated, no foreign mesh: adoptable, and therefore never memoised.
    [Fact]
    public void IncompletenessReason_IsNullForAWholeBake()
    {
        Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(true, 44, 44, 0));
        // More associated than were ever visible is not a refusal either — the guard is a floor, not an equality.
        Assert.Null(Sts2SpineGeoClipRequestLane.IncompletenessReason(true, 45, 44, 0));
    }

    // The arm is the token BEFORE the first '='; the reason carries counts and is unbucketable on its own.
    [Fact]
    public void ClassifyRefusal_BucketsUnknownAndEmptyReasonsAsOther()
    {
        Assert.Equal("other", Sts2SpineGeoClipRequestLane.ClassifyRefusal(null));
        Assert.Equal("other", Sts2SpineGeoClipRequestLane.ClassifyRefusal("   "));
        Assert.Equal("other", Sts2SpineGeoClipRequestLane.ClassifyRefusal("somethingNew=3"));
        Assert.Equal("incomplete", Sts2SpineGeoClipRequestLane.ClassifyRefusal("complete=false"));
    }

    // A rig result is refused only when NO pose in it is adoptable: one good pose makes the whole result worth
    // returning, and memoising it would throw away work that succeeded.
    [Fact]
    public void RefusalReasonFor_KeepsARigResultThatHoldsOneAdoptablePose()
    {
        var refusedOnly = ResultWith(
            success: true,
            poses: [Pose("attack", complete: false), Pose("idle", complete: false)]);
        var mixed = ResultWith(
            success: true,
            poses: [Pose("attack", complete: false), Pose("idle", complete: true, associated: 44)]);

        Assert.Equal("complete=false", Sts2SpineGeoClipRequestLane.RefusalReasonFor(refusedOnly));
        Assert.Null(Sts2SpineGeoClipRequestLane.RefusalReasonFor(mixed));
    }

    // A HARD failure (no scene, no spine node, a throw) is not a completeness refusal. Those are the failures
    // most likely to be transient, and a memo that pinned one would keep answering with it after the cause was
    // gone.
    [Fact]
    public void RefusalReasonFor_IgnoresAFailedBake()
    {
        var failed = SpineGeoClipBakeResultSnapshot.Failure(
            AssetExtractFailureCode.RuntimeFailure,
            "the scene could not be loaded.",
            [new AssetExtractDetail("scene", "res://missing.tscn", "not found")]);

        Assert.Null(Sts2SpineGeoClipRequestLane.RefusalReasonFor(failed));
    }

    // ---- The key --------------------------------------------------------------------------------------------

    // Every identity field splits the keyspace. If one of them did not, the memo would answer a request with
    // another request's verdict.
    [Fact]
    public void Key_DiffersOnEveryIdentityField()
    {
        var baseline = Key(Request());

        Assert.Equal(baseline, Key(Request()));
        Assert.NotEqual(baseline, Key(Request(scene: "res://other.tscn")));
        Assert.NotEqual(baseline, Key(Request(node: "Rig/Other")));
        Assert.NotEqual(baseline, Key(Request(animation: "idle")));
        Assert.NotEqual(baseline, Key(Request(poseOnly: false)));
        Assert.NotEqual(baseline, Key(Request(sampleTime: 0.5d)));
        Assert.NotEqual(baseline, Key(Request(fps: 45)));
        Assert.NotEqual(baseline, Key(Request(maxFrames: 120)));
        // A rig request that names more animations is a DIFFERENT bake from the single-pose request for its
        // primary: it batches, it can fall back per pose, and it is graded per pose.
        Assert.NotEqual(baseline, Key(Request(animationNames: ["attack", "idle"])));
    }

    // The levers and the bridge build are in the key for two different reasons: flipping a lever is a deliberate
    // "try this rig the other way", and a newer build of the baker is exactly where a fix would have landed.
    [Fact]
    public void Key_DiffersOnLeversAndBridgeVersion()
    {
        var baseline = Key(Request());

        Assert.NotEqual(baseline, Sts2SpineGeoClipRefusalKey.From(Request(), "probe=Ordinal/1/Rgb/1", Bridge));
        Assert.NotEqual(baseline, Sts2SpineGeoClipRefusalKey.From(Request(), Levers, "spirectl-bridge/0.4.0"));
    }

    // An absent node path and an explicit one must not collide, and an absent sample time must not collide with
    // a real one.
    [Fact]
    public void Key_SpellsTheAbsentFieldsUnambiguously()
    {
        var auto = Key(Request(node: null, sampleTime: null));

        Assert.Equal(Sts2SpineGeoClipRefusalKey.AutoNodePath, auto.NodePath);
        Assert.Equal(Sts2SpineGeoClipRefusalKey.AutoSampleTime, auto.SampleTimeSeconds);
        // Blank and absent are the same request, so they are the same identity.
        Assert.Equal(auto, Key(Request(node: "   ", sampleTime: null)));
        // An explicit node path is not the same identity as "find the single spine node".
        Assert.NotEqual(auto, Key(Request(node: "Rig/Spine", sampleTime: null)));
        // Nor is an explicit sample time of 0 the same as "let the heuristic pick" — for a one-shot the two pick
        // very different frames.
        Assert.NotEqual(auto, Key(Request(node: null, sampleTime: 0d)));
    }

    // A caller deriving a sample time from a wall clock would otherwise mint a fresh identity on every request
    // and never hit the memo at all.
    [Fact]
    public void Key_RoundsTheSampleTimeAndClampsTheFrameClock()
    {
        Assert.Equal(Key(Request(sampleTime: 1.2345678d)), Key(Request(sampleTime: 1.23456789d)));
        Assert.NotEqual(Key(Request(sampleTime: 1.23456d)), Key(Request(sampleTime: 1.23457d)));
        // Two requests that would plan the SAME bake share a key even when one spelled an out-of-range number.
        Assert.Equal(Key(Request(fps: 1)), Key(Request(fps: 0)));
        Assert.Equal(Key(Request(maxFrames: 1)), Key(Request(maxFrames: 0)));
    }

    // The lever fold reads the RESOLVED settings, so a misspelt value and an absent one — which produce the same
    // bake — produce the same signature rather than splitting the keyspace on a typo.
    [Fact]
    public void LeverSignature_FoldsResolvedSettingsNotRawText()
    {
        var absent = Sts2SpineGeoClipLevers.Signature(
            GeoClipProbeSettings.Parse(null, null, null, null),
            GeoClipPauseBudget.Parse(null, null),
            denseSweepOnly: true);
        var misspelt = Sts2SpineGeoClipLevers.Signature(
            GeoClipProbeSettings.Parse("nonsense", "yes-please", "purple", "affirmative"),
            GeoClipPauseBudget.Parse("perhaps", "loudly"),
            denseSweepOnly: true);
        var killSwitched = Sts2SpineGeoClipLevers.Signature(
            GeoClipProbeSettings.Parse(null, null, null, "0"),
            GeoClipPauseBudget.Parse(null, null),
            denseSweepOnly: true);

        Assert.Equal(absent, misspelt);
        Assert.NotEqual(absent, killSwitched);
        Assert.False(string.IsNullOrWhiteSpace(Sts2SpineGeoClipLevers.CaptureSignature()));
    }

    // THE TWO INDEX-RECOVERY STRIPS ARE SEPARATE TERMS, and this is the failure they exist to prevent: an
    // operator who takes the CEILING down to reproduce something must not be handed the receipt written with the
    // FLOOR down. They cover opposite halves of the index axis and produce genuinely different acquisitions, so a
    // single "recovery armed" bit would collide two states that refuse for different reasons — the "I changed the
    // lever, why is it still refusing" trap the bridge-version clause in this key already exists to avoid.
    //
    // Asserted as four distinct signatures rather than as a substring, so a later fold that happens to spell the
    // two bits into one token still fails here.
    [Fact]
    public void LeverSignature_SplitsTheIndexFloorAndIndexCeilingStripsIntoIndependentTerms()
    {
        string Signature(bool floor, bool ceiling) => Sts2SpineGeoClipLevers.Signature(
            GeoClipProbeSettings.Parse(null, null, null, null),
            GeoClipPauseBudget.Parse(null, null),
            denseSweepOnly: true,
            ownershipProofArmed: true,
            indexFloorRecoveryArmed: floor,
            indexCeilingRecoveryArmed: ceiling);

        var signatures = new HashSet<string>(
            [Signature(true, true), Signature(true, false), Signature(false, true), Signature(false, false)],
            StringComparer.Ordinal);

        Assert.Equal(4, signatures.Count);

        // And the default — nothing said either way — is both strips armed, which is what a bake started right
        // now runs, so the memo's own capture cannot mint a different identity than the bake it remembers.
        Assert.Equal(Signature(true, true), Sts2SpineGeoClipLevers.Signature(
            GeoClipProbeSettings.Parse(null, null, null, null),
            GeoClipPauseBudget.Parse(null, null),
            denseSweepOnly: true));
    }

    // ---- The protocol ---------------------------------------------------------------------------------------

    // THE POINT OF THE WHOLE FEATURE: the second identical request does not reach the bake at all.
    [Fact]
    public void SecondIdenticalRequestNeverInvokesTheBake()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        var first = Bake(memo, Request(), ref bakes, RefusedResult());
        var second = Bake(memo, Request(), ref bakes, RefusedResult());

        Assert.Equal(1, bakes);
        // The first answer is the bake's own — an incomplete bake reports Success (it ran, it wrote a manifest)
        // — now carrying the verdict the completeness rule reached.
        Assert.True(first.Success);
        Assert.False(first.RefusedFromMemo);
        Assert.Equal("unassociated", first.RefusalArm);
        Assert.Equal("associated=40 of slotsEverVisible=44", first.RefusalReason);
        // The second is the refusal itself, and says so rather than pretending to be a bake.
        Assert.False(second.Success);
        Assert.True(second.RefusedFromMemo);
        Assert.Equal("refused-memo", second.BatchNote);
        Assert.Equal("unassociated", second.RefusalArm);
        Assert.Equal(first.RefusalReason, second.RefusalReason);
        // Zero, because this call cost nothing — which is the entire point.
        Assert.Equal(0d, second.ElapsedMs);
        // The counters the refusal was derived from travel with it, so a caller can re-derive its own verdict.
        Assert.Equal(40, second.Associated);
        Assert.Equal(44, second.SlotsVisible);
    }

    // A DIFFERENT identity is not answered from another identity's refusal.
    [Fact]
    public void ADifferentIdentityStillBakes()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        Bake(memo, Request(), ref bakes, RefusedResult());
        Bake(memo, Request(animation: "idle"), ref bakes, RefusedResult());

        Assert.Equal(2, bakes);
    }

    // The escape hatch: a fix can land somewhere the key cannot see, so the memo must be bypassable — and the
    // bypassed run re-records whatever it now finds rather than leaving the old receipt in place.
    [Fact]
    public void IgnoreRefusalMemoReRunsTheBakeAndRefreshesTheReceipt()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        Bake(memo, Request(), ref bakes, RefusedResult());
        var forced = Bake(memo, Request(ignoreMemo: true), ref bakes, RefusedResult(complete: false));

        Assert.Equal(2, bakes);
        Assert.False(forced.RefusedFromMemo);
        Assert.Equal("incomplete", forced.RefusalArm);

        var replay = Bake(memo, Request(), ref bakes, RefusedResult());
        Assert.Equal(2, bakes);
        Assert.Equal("incomplete", replay.RefusalArm); // the refreshed verdict, not the original one
    }

    // A bypassed run that now SUCCEEDS clears nothing by itself — but the identity it was refused under is no
    // longer what a caller asks about, and the success is returned unchanged. Successes are never recorded.
    [Fact]
    public void SuccessIsNeverRecorded()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        var result = Bake(memo, Request(), ref bakes, WholeResult());

        Assert.Equal(1, bakes);
        Assert.Equal(0, memo.Count);
        Assert.True(result.Success);
        Assert.Null(result.RefusalArm);
        Assert.Null(result.RefusalReason);
        Assert.False(result.RefusedFromMemo);

        // And the next identical request still bakes, because nothing was remembered about it.
        Bake(memo, Request(), ref bakes, WholeResult());
        Assert.Equal(2, bakes);
    }

    // A hard failure is not recorded either — see RefusalReasonFor_IgnoresAFailedBake for why.
    [Fact]
    public void AFailedBakeIsNeverRecorded()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;
        var failure = SpineGeoClipBakeResultSnapshot.Failure(
            AssetExtractFailureCode.RuntimeFailure,
            "the scene could not be loaded.",
            []);

        Bake(memo, Request(), ref bakes, failure);
        Bake(memo, Request(), ref bakes, failure);

        Assert.Equal(2, bakes);
        Assert.Equal(0, memo.Count);
    }

    // ---- RETRYABLE ACQUISITION SHORTFALLS -------------------------------------------------------------------
    //
    // A refusal is normally evidence ABOUT THE RIG and re-running it reaches the same verdict, which is what
    // makes the memo (and a host's durable receipt behind it) worth having. ONE SHAPE IS NOT LIKE THAT: a sweep
    // that validated fewer meshes than there are slots, with no window truncated, has diagnosed itself — its
    // index plan did not cover the rig's own meshes and it completed that plan. Window A's index axis is
    // inferred from two bracket sentinels and holds only while Godot's RID allocator is bump-allocating; one
    // creature death earlier in the session breaks it (round WS-I: the same four rigs bake 4/4 on a quiet room
    // and 1/4 after two kills, in virgin processes). Pinning that is pinning noise.

    // The rule, on raw counters. BOTH clauses are load-bearing, and zero is missing evidence rather than a
    // measured shortfall.
    [Theory]
    // an untruncated shortfall — the whole point
    [InlineData(17, 22, false, true)]
    [InlineData(1, 44, false, true)]
    // TRUNCATED: the sweep never finished its own plan, so the shortfall says nothing about whether the plan was
    // right, and re-running it under the same cap reproduces it exactly. A configuration verdict; it sticks.
    [InlineData(17, 22, true, false)]
    // no shortfall at all — this refusal came from association, leftovers or ownership, and is about the rig
    [InlineData(22, 22, false, false)]
    [InlineData(44, 22, false, false)]
    // no counters carried (an older producer, a memo replay): missing evidence, not clean evidence
    [InlineData(0, 22, false, false)]
    public void IsRetryableAcquisitionShortfall_NeedsAnUntruncatedMeasuredShortfall(
        int meshesValidated,
        int slotsVisible,
        bool truncated,
        bool expected)
        => Assert.Equal(
            expected,
            Sts2SpineGeoClipRequestLane.IsRetryableAcquisitionShortfall(
                meshesValidated, slotsVisible, truncated));

    // THE BEHAVIOUR THAT MATTERS. One unlucky bake must not refuse the identity for the life of the process.
    [Fact]
    public void ARetryableAcquisitionShortfallIsNotMemoisedAndTheNextRequestBakesAgain()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        var first = Bake(memo, Request(), ref bakes, ShortfallResult());

        Assert.Equal(1, bakes);
        Assert.Equal(0, memo.Count);
        Assert.True(first.RefusalRetryable);
        // Still a refusal, still stamped with its arm and reason — it is not adoptable, only not sticky.
        Assert.Equal("incomplete", first.RefusalArm);
        Assert.Equal("complete=false", first.RefusalReason);
        Assert.False(first.RefusedFromMemo);

        // …and the SAME identity re-bakes rather than being answered from a receipt. That second bake succeeding
        // is the whole reason not to pin the first: the index-floor recovery strip, a drained free list, or any
        // later allocation can make it whole.
        var second = Bake(memo, Request(), ref bakes, WholeResult());

        Assert.Equal(2, bakes);
        Assert.True(second.Success);
        Assert.False(second.RefusalRetryable);
        Assert.Equal(0, memo.Count);
    }

    // The narrowness of the exemption, asserted from the other side: every OTHER refusal is still memoised, so
    // this is not a quiet retirement of the memo.
    [Fact]
    public void EveryOtherRefusalIsStillMemoisedAndStillNotRetryable()
    {
        foreach (var refused in new[]
        {
            // an ordinary unassociated/incomplete refusal with no sweep counters
            RefusedResult(),
            // a shortfall the CAP caused: the sweep did not finish, so re-running it changes nothing
            ShortfallResult(truncated: true),
        })
        {
            var memo = new Sts2SpineGeoClipRefusalMemo();
            var bakes = 0;

            var first = Bake(memo, Request(), ref bakes, refused);
            var second = Bake(memo, Request(), ref bakes, refused);

            Assert.Equal(1, bakes);
            Assert.Equal(1, memo.Count);
            Assert.False(first.RefusalRetryable);
            Assert.True(second.RefusedFromMemo);
            Assert.False(second.RefusalRetryable);
        }
    }

    // KILL SWITCH: with it down the shortfall is memoised exactly as it was before, and reports itself as not
    // retryable — so a consumer reading the flag to decide whether to write a durable receipt sees one answer,
    // not two.
    [Fact]
    public void TheRetryableShortfallKillSwitchRestoresTheStickyRefusal()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        var first = Bake(memo, Request(), ref bakes, ShortfallResult(), retryableArmed: false);
        var second = Bake(memo, Request(), ref bakes, ShortfallResult(), retryableArmed: false);

        Assert.Equal(1, bakes);
        Assert.Equal(1, memo.Count);
        Assert.False(first.RefusalRetryable);
        Assert.True(second.RefusedFromMemo);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void ResolveRetryableShortfall_DefaultsArmed(string? raw, bool expected)
    {
        Assert.Equal(expected, Sts2SpineGeoClipRequestLane.ResolveRetryableShortfall(raw));
        Assert.True(Sts2SpineGeoClipRequestLane.RetryableShortfallDefault);
    }

    // The verdict and the retryability flag come from ONE walk of the poses, so the flag can never qualify a
    // reason taken from a different pose. A rig whose FIRST refused pose is an ordinary refusal is not made
    // retryable by a later pose that happens to be short.
    [Fact]
    public void RefusalVerdictFor_TakesItsRetryabilityFromThePoseItTookTheReasonFrom()
    {
        var result = ResultWith(
            success: true,
            poses:
            [
                Pose("attack", complete: true, associated: 40, slotsVisible: 44),
                Pose(
                    "idle",
                    complete: false,
                    associated: 17,
                    slotsVisible: 22,
                    meshesValidated: 17),
            ],
            complete: true);

        var verdict = Sts2SpineGeoClipRequestLane.RefusalVerdictFor(result);

        Assert.Equal("associated=40 of slotsEverVisible=44", verdict.Reason);
        Assert.False(verdict.Retryable);
        // …and the counters travel with the reason, from the SAME pose: the first refused pose validated no
        // meshes worth reporting, not the short second one's 17 of 22.
        Assert.Equal((0, 44), (verdict.MeshesValidated, verdict.SlotsVisible));
        Assert.Equal(verdict.Reason, Sts2SpineGeoClipRequestLane.RefusalReasonFor(result));
    }

    // Attempts counts how often the identity was REFUSED, memo answers included: a high number on a fresh
    // process is a caller retrying something the memo is answering for free.
    [Fact]
    public void AttemptsCountMemoAnswersToo()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        var bakes = 0;

        Bake(memo, Request(), ref bakes, RefusedResult());
        Bake(memo, Request(), ref bakes, RefusedResult());
        Bake(memo, Request(), ref bakes, RefusedResult());

        var receipt = memo.Lookup(Key(Request()), Now);
        Assert.NotNull(receipt);
        Assert.Equal(4, receipt!.Attempts); // 1 bake + 2 memo answers + this lookup
        Assert.Equal(12.5d, receipt.ElapsedMsFirst); // what the FIRST refusal cost, kept for good
    }

    // ---- The bound ------------------------------------------------------------------------------------------

    // Past the cap the LEAST-RECENTLY-USED identity goes, not the oldest: the one a host keeps asking for is
    // exactly the one worth keeping.
    [Fact]
    public void EvictsTheLeastRecentlyUsedPastTheCap()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        for (var i = 0; i < Sts2SpineGeoClipRefusalMemo.Capacity; i++)
        {
            Record(memo, Request(animation: $"anim{i}"));
        }

        Assert.Equal(Sts2SpineGeoClipRefusalMemo.Capacity, memo.Count);

        // Touch the oldest entry so it is no longer the eviction victim, then overflow by one.
        Assert.NotNull(memo.Lookup(Key(Request(animation: "anim0")), Now));
        Record(memo, Request(animation: "overflow"));

        Assert.Equal(Sts2SpineGeoClipRefusalMemo.Capacity, memo.Count);
        Assert.NotNull(memo.Lookup(Key(Request(animation: "anim0")), Now));
        Assert.NotNull(memo.Lookup(Key(Request(animation: "overflow")), Now));
        Assert.Null(memo.Lookup(Key(Request(animation: "anim1")), Now)); // the LRU victim
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var memo = new Sts2SpineGeoClipRefusalMemo();
        Record(memo, Request());
        Assert.Equal(1, memo.Count);

        memo.Clear();

        Assert.Equal(0, memo.Count);
        Assert.Null(memo.Lookup(Key(Request()), Now));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static SpineGeoClipBakeRequestSnapshot Request(
        string scene = "res://rig/merchant.tscn",
        string? node = "Rig/Spine",
        string animation = "attack",
        double? sampleTime = null,
        bool poseOnly = true,
        int fps = 30,
        int? maxFrames = null,
        IReadOnlyList<string>? animationNames = null,
        bool ignoreMemo = false)
        => new(
            scene,
            node,
            animation,
            sampleTime,
            OutputDirectory: "/tmp/geoclip",
            Fps: fps,
            MaxFrames: maxFrames,
            PoseOnly: poseOnly,
            AnimationNames: animationNames,
            KnownPageContentIds: null,
            IgnoreRefusalMemo: ignoreMemo);

    private static Sts2SpineGeoClipRefusalKey Key(SpineGeoClipBakeRequestSnapshot request)
        => Sts2SpineGeoClipRefusalKey.From(request, Levers, Bridge);

    private static SpineGeoClipBakeResultSnapshot Bake(
        Sts2SpineGeoClipRefusalMemo memo,
        SpineGeoClipBakeRequestSnapshot request,
        ref int bakes,
        SpineGeoClipBakeResultSnapshot result,
        bool retryableArmed = true)
    {
        var counted = 0;
        var answer = Sts2SpineGeoClipRequestLane.BakeWithRefusalMemo(
            request,
            memo,
            Levers,
            Bridge,
            () =>
            {
                counted++;
                return result;
            },
            () => Now,
            // Explicit rather than read from the environment: the protocol is the unit under test and a suite
            // whose verdict depends on the shell it was launched from is not a test.
            retryableShortfallArmed: () => retryableArmed);
        bakes += counted;
        return answer;
    }

    private static void Record(Sts2SpineGeoClipRefusalMemo memo, SpineGeoClipBakeRequestSnapshot request)
    {
        var bakes = 0;
        Bake(memo, request, ref bakes, RefusedResult());
    }

    /// <summary>A bake that RAN and wrote a manifest, but did not acquire the whole rig.</summary>
    private static SpineGeoClipBakeResultSnapshot RefusedResult(bool complete = true)
        => ResultWith(
            success: true,
            poses: [Pose("attack", complete)],
            complete: complete,
            associated: 40,
            slotsVisible: 44);

    private static SpineGeoClipBakeResultSnapshot WholeResult()
        => ResultWith(
            success: true,
            poses: [Pose("attack", complete: true, associated: 44)],
            complete: true,
            associated: 44,
            slotsVisible: 44);

    /// <summary>
    /// A bake that RAN and whose SWEEP came up short: fewer meshes validated than there are slots to fill, with
    /// no window truncated. See <see cref="Sts2SpineGeoClipRequestLane.IsRetryableAcquisitionShortfall"/>.
    /// </summary>
    private static SpineGeoClipBakeResultSnapshot ShortfallResult(
        int meshesValidated = 17,
        int slotsVisible = 22,
        bool truncated = false)
        => ResultWith(
            success: true,
            poses:
            [
                Pose(
                    "attack",
                    complete: false,
                    associated: meshesValidated,
                    slotsVisible: slotsVisible,
                    meshesValidated: meshesValidated,
                    sweepTruncated: truncated),
            ],
            complete: false,
            associated: meshesValidated,
            slotsVisible: slotsVisible,
            meshesValidated: meshesValidated,
            sweepTruncated: truncated);

    private static SpineGeoClipBakeResultSnapshot ResultWith(
        bool success,
        IReadOnlyList<SpineGeoClipBakePoseSnapshot> poses,
        bool complete = false,
        int associated = 40,
        int slotsVisible = 44,
        int meshesValidated = 0,
        bool sweepTruncated = false)
        => new(
            Success: success,
            ManifestPath: "/tmp/geoclip/merchant--Rig-Spine--attack/manifest.json",
            PageFileNames: ["page0.png"],
            PartCount: 12,
            FrameCount: 1,
            SampleTimeSeconds: 0.8d,
            SampleTimeSource: "mid",
            ElapsedMs: 12.5d,
            Slots: 48,
            SlotsVisible: slotsVisible,
            Associated: associated,
            Unassociated: slotsVisible - associated,
            ForeignMeshes: 0,
            Complete: complete,
            Error: null,
            Poses: poses,
            MeshesValidated: meshesValidated,
            SweepTruncated: sweepTruncated);

    private static SpineGeoClipBakePoseSnapshot Pose(
        string animation,
        bool complete,
        int associated = 40,
        int slotsVisible = 44,
        int meshesValidated = 0,
        bool sweepTruncated = false)
        => new(
            animation,
            Success: true,
            ManifestPath: "/tmp/geoclip/merchant--Rig-Spine--attack/manifest.json",
            PageFileNames: ["page0.png"],
            PartCount: 12,
            FrameCount: 1,
            SampleTimeSeconds: 0.8d,
            SampleTimeSource: "mid",
            Slots: 48,
            SlotsVisible: slotsVisible,
            Associated: associated,
            Unassociated: slotsVisible - associated,
            ForeignMeshes: 0,
            StaleMeshFrames: 0,
            AttachmentDriftSlots: 0,
            Complete: complete,
            Batched: false,
            FailureReason: null,
            MeshesValidated: meshesValidated,
            SweepTruncated: sweepTruncated);
}
