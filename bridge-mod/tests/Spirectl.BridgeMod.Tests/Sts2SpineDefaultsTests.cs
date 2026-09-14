using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// A stand-in for one link of a queued animation chain.
/// <see cref="Sts2SpineDefaults.FlattenQueuedChain{TState}"/> is generic precisely so the walk can be exercised
/// with no game assembly present: the caller supplies the successor step and the projection.
/// </summary>
internal sealed class SpineQueuedStateDouble(string id, bool isLooping)
{
    public string Id { get; } = id;

    public bool IsLooping { get; } = isLooping;

    /// <summary>The successor a fixed-link build would hand back. Null models "nothing queued".</summary>
    public SpineQueuedStateDouble? Next { get; set; }

    /// <summary>How many times this link has been asked for its successor. Cycle/cap tests assert on it.</summary>
    public int SuccessorReads { get; private set; }

    public SpineQueuedStateDouble? ReadNext()
    {
        SuccessorReads++;
        return Next;
    }

    public (string Id, bool IsLooping) Describe() => (Id, IsLooping);
}

// Round-8 items 10/11 (spine animation semantics). Pure, Godot-free core of:
//   * which clip the producer presents for a spine node it has not yet observed a real animation for,
//   * whether that GUESS should be reported as looping (the wire's `spineLooping`, which both clients honour
//     by clamping on the last frame when false),
//   * and which links of a CreatureAnimator chain the game ACTUALLY queued (the MegaSprite.HasAnimation gate).
public sealed class Sts2SpineDefaultsTests
{
    // The real sneaky_gremlin clip set, in the ORDINAL order Sts2SpineInspector.ReadAnimations produces
    // (verified against animations/monsters/sneaky_gremlin/sneaky_gremlin.skel + its skel_data mix table).
    private static readonly string[] SneakyGremlinAnimations =
    [
        "attack", "awake_loop", "breathe", "die", "hurt_awake", "hurt_stunned", "spawn", "stunned_loop", "wake_up",
    ];

    // THE item-11 REGRESSION: the pre-fix guess was `animations[0]` after a handful of exact-name probes, and
    // the list is sorted ORDINALLY — so a gremlin (no `idle*`/`animation`/`loop*` clip) resolved to "attack",
    // reported as looping, and stood in combat replaying its attack forever. Its real idle is `awake_loop`.
    [Fact]
    public void PickDefaultAnimation_SneakyGremlin_NeverPicksAttack()
    {
        var picked = Sts2SpineDefaults.PickDefaultAnimation(SneakyGremlinAnimations);

        Assert.NotEqual("attack", picked);
        Assert.Equal("awake_loop", picked);
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops(picked));
    }

    // An exact well-known idle always wins, whatever else the skeleton exposes.
    [Fact]
    public void PickDefaultAnimation_PrefersExactIdleLoop()
    {
        Assert.Equal(
            "idle_loop",
            Sts2SpineDefaults.PickDefaultAnimation(["attack", "die", "idle_loop", "relaxed_loop"]));
        Assert.Equal("idle", Sts2SpineDefaults.PickDefaultAnimation(["attack", "idle"]));
    }

    // A single-clip decorative spine (treasure chest, boss map node, main-menu logo) exposes ["animation", …]
    // and no `*_loop` clip — it must still resolve to "animation", exactly as before the fix.
    [Fact]
    public void PickDefaultAnimation_SingleClipDecorativeSpine_PicksAnimation()
    {
        Assert.Equal("animation", Sts2SpineDefaults.PickDefaultAnimation(["animation", "shine_fade"]));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("animation"));
    }

    // Track-namespaced clips ("_tracks/…", "right/idle_loop", "tracks/writhe") are OVERLAY-track content the
    // game plays on track 1/2 — presenting one as the body animation would show a fragment of the creature.
    [Fact]
    public void PickDefaultAnimation_IgnoresTrackNamespacedLoops()
    {
        Assert.Equal(
            "breathe",
            Sts2SpineDefaults.PickDefaultAnimation(["_tracks/eyes_closed_loop", "attack", "breathe", "die"]));
        Assert.Equal(
            "body_loop",
            Sts2SpineDefaults.PickDefaultAnimation(["attack", "body_loop", "right/idle_loop"]));
    }

    // A `die_loop`/`dead_loop` is a corpse pose, never the resting state of a live creature: only picked when
    // there is no other loop to pick.
    [Fact]
    public void PickDefaultAnimation_DeprioritisesTerminalDeathLoops()
    {
        Assert.Equal("relaxed_loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "die_loop", "relaxed_loop"]));
        Assert.Equal("die_loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "die_loop"]));
    }

    // Regression guards taken from committed recordings (.sts2/bench), i.e. shapes the producer really streams:
    //   * the merchant's hand (merchant_inventory.tscn) exposes ONLY "default", played looping — the guess must
    //     stay "default" AND stay looping (audit-shop-open.ndjson);
    //   * a rest-site character exposes two track-1 lamp overlays FIRST in ordinal order, and the pre-fix guess
    //     streamed "_tracks/light_off" (a lamp, not the character) as the body animation — the plain `*_loop`
    //     body clip must win instead (audit-rest-refocus.ndjson).
    [Fact]
    public void PickDefaultAnimation_MatchesRecordedProducerCases()
    {
        Assert.Equal("default", Sts2SpineDefaults.PickDefaultAnimation(["default"]));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("default"));

        var restSite = new[] { "_tracks/light_off", "_tracks/light_on", "glory_loop", "hive_loop", "overgrowth_loop" };
        Assert.Equal("glory_loop", Sts2SpineDefaults.PickDefaultAnimation(restSite));
    }

    [Fact]
    public void PickDefaultAnimation_EmptyListYieldsNull()
    {
        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation(null));
        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation([]));
    }

    // ── A skeleton with NOTHING loop-shaped answers "no animation" (the Regent's permanent daggers) ────────────

    // The Regent's two weapon sprites (regent.tscn, Visuals/Weapons/WeaponAnim1+2, regent_weapon_skel_data.tres)
    // are authored `preview_animation = "-- Empty --"`: the game applies nothing to them until NRegentVfx.Attack()
    // fires a one-shot, and every clip the skeleton exposes is attack-shaped. So the pre-fix guess fell all the way
    // to `animations[0]`, the client's default still-mode sampled the MIDDLE of it, and light daggers hung frozen
    // mid-swing over the entire fight. There is no right clip to pick here — the right answer is NONE.
    //
    // Both the live-observed list (the running headless streams anim="attack" for these nodes) and the .skel's
    // other attack-shaped names are exercised: the property that matters is "every entry is a one-shot", not which
    // name happens to sort first.
    [Fact]
    public void PickDefaultAnimation_RegentWeapon_AllOneShots_YieldsNoAnimation()
    {
        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation(
            ["attack", "attack1", "attack2", "attack_end", "attack_test"]));
        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation(
            ["attack1", "attack2", "attack_end", "attack_test"]));
    }

    // The blanking is the LAST word only: every existing win is decided before it, so a skeleton that really does
    // expose a resting clip is picked exactly as it was — this is the guard against blanking the whole cast.
    [Fact]
    public void PickDefaultAnimation_BlankingNeverCostsALoopShapedPick()
    {
        Assert.Equal("awake_loop", Sts2SpineDefaults.PickDefaultAnimation(SneakyGremlinAnimations));
        Assert.Equal("idle_loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "die", "idle_loop"]));
        Assert.Equal("idle", Sts2SpineDefaults.PickDefaultAnimation(["attack", "idle"]));
        Assert.Equal("idle_anim", Sts2SpineDefaults.PickDefaultAnimation(["attack", "idle_anim"]));
        Assert.Equal("relaxed_loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "die_loop", "relaxed_loop"]));
        Assert.Equal("die_loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "die_loop"]));
        // The single-clip decoratives: the chest / boss map point / logo ("animation"), the merchant's hand
        // ("default"), and the secondary exact names — all read as looping, so none of them blank.
        Assert.Equal("animation", Sts2SpineDefaults.PickDefaultAnimation(["animation"]));
        Assert.Equal("default", Sts2SpineDefaults.PickDefaultAnimation(["default"]));
        Assert.Equal("breathe", Sts2SpineDefaults.PickDefaultAnimation(["breathe"]));
        Assert.Equal("loop", Sts2SpineDefaults.PickDefaultAnimation(["attack", "loop"]));
        Assert.Equal("loop_anim", Sts2SpineDefaults.PickDefaultAnimation(["attack", "loop_anim"]));
    }

    // SPIRECTL_SPINE_DEFAULT_ONESHOT_BLANK=0 (the producer passes the flag through): back to answering with the
    // one-shot, so an A/B puts the daggers straight back.
    [Fact]
    public void PickDefaultAnimation_BlankingKillSwitchOff_RestoresTheOneShotGuess()
    {
        var regentWeapon = new[] { "attack", "attack1", "attack2", "attack_end", "attack_test" };

        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation(regentWeapon, blankOneShotGuess: true));
        Assert.Equal("attack", Sts2SpineDefaults.PickDefaultAnimation(regentWeapon, blankOneShotGuess: false));
        // …and the loop-shaped picks are identical either way (the switch only governs the blanking).
        Assert.Equal("awake_loop", Sts2SpineDefaults.PickDefaultAnimation(SneakyGremlinAnimations, blankOneShotGuess: false));
    }

    // The loop flag is now DERIVED from the guessed name instead of hardcoded true: a guess that landed on a
    // one-shot is reported non-looping, so both clients clamp on its last frame instead of replaying it.
    [Fact]
    public void DefaultAnimationLoops_OnlyForLoopShapedNames()
    {
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("idle_loop"));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("idle"));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("awake_loop"));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("stunned_loop"));
        Assert.True(Sts2SpineDefaults.DefaultAnimationLoops("breathe"));

        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops("attack"));
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops("bite"));
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops("die"));
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops("shine_fade"));
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops(null));
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops("  "));
    }

    // ── HasAnimation gate on the CreatureAnimator.SetNextState postfix ────────────────────────────────────

    // The head clip is present: the chain walks to the first LOOPING link (the terminal idle the one-shot hands
    // back to), exactly as the game's AddNextState recursion queues it.
    [Fact]
    public void ResolveRecordableSequence_RecordsHeadAndFirstLoopingReturn()
    {
        var resolved = Sts2SpineDefaults.ResolveRecordableSequence(
            "attack", headLooping: false,
            [("recover", false), ("idle_loop", true), ("never_reached", true)],
            _ => true);

        Assert.Equal(("attack", false, "idle_loop", true), resolved);
    }

    // THE GATE: SetNextState logs "could not find '<id>' animation" and RETURNS without touching the track when
    // the clip is missing from the skeleton — the game keeps playing what it was playing. The postfix used to
    // record the missing clip anyway, switching the mirror to a clip that does not exist.
    [Fact]
    public void ResolveRecordableSequence_MissingHead_RecordsNothing()
    {
        Assert.Null(Sts2SpineDefaults.ResolveRecordableSequence(
            "attack", headLooping: false, [("idle_loop", true)], _ => false));
        Assert.Null(Sts2SpineDefaults.ResolveRecordableSequence(
            null, headLooping: true, [], _ => true));
        Assert.Null(Sts2SpineDefaults.ResolveRecordableSequence(
            "  ", headLooping: true, [], _ => true));
    }

    // AddNextState has the SAME gate, and its early return also ends the recursion — so a missing link
    // TRUNCATES the queue: nothing after it was queued either.
    [Fact]
    public void ResolveRecordableSequence_MissingQueuedLink_TruncatesTheQueue()
    {
        var resolved = Sts2SpineDefaults.ResolveRecordableSequence(
            "attack", headLooping: false,
            [("missing_transition", false), ("idle_loop", true)],
            name => name != "missing_transition");

        // The head still played; nothing was queued behind it, so the client clamps on the attack's last frame
        // rather than snapping to an idle the game never queued.
        Assert.Equal(("attack", false, (string?)null, false), resolved);
    }

    // ── Item 10: the bite overlay plays ONCE, carried entirely by the START message ────────────────────────

    // NVfxSpine (res://scenes/vfx/vfx_bite.tscn, _animation = "bite") calls
    // MegaAnimationState.SetAnimation("bite", loop: false) on its child SpineSprite and frees itself on
    // animation_completed. The hook records that as a LONE one-shot head, which resolves to a non-looping
    // "bite" for the whole life of the node — the client clamps on its last frame, so no end/"finished"
    // message is needed on the wire at all.
    [Fact]
    public void BiteOverlay_LoneOneShot_NeverBecomesALoop()
    {
        const double biteMsec = 733;
        foreach (var elapsed in new double[] { 0, 16, biteMsec - 1, biteMsec, biteMsec * 5 })
        {
            var (anim, _, looping, _) = Sts2SpineSchedule.ResolveScheduledAnim(
                "bite", currentLooping: false, currentDurMsec: biteMsec,
                nextAnim: null, nextLooping: false, elapsedMsec: elapsed);

            Assert.Equal("bite", anim);
            Assert.False(looping);
        }
    }

    // …and the fallback answers NOTHING when the hook has not fired yet: vfx_bite's skeleton exposes only "bite",
    // a one-shot. The pre-fix guess (animations[0], reported looping) is what made the overlay loop forever; the
    // round-8 fix stopped the looping but still streamed a mid-bite frame for a node the game had not yet played
    // anything on. NVfxSpine issues its SetAnimation("bite") from _Ready, so the correct picture for that gap is
    // an un-posed (invisible) overlay, not a bite caught halfway. The kill switch shows what it used to answer.
    [Fact]
    public void BiteOverlay_FallbackGuessIsNoAnimationUntilTheGamePlaysIt()
    {
        Assert.Null(Sts2SpineDefaults.PickDefaultAnimation(["bite"]));

        var legacy = Sts2SpineDefaults.PickDefaultAnimation(["bite"], blankOneShotGuess: false);
        Assert.Equal("bite", legacy);
        Assert.False(Sts2SpineDefaults.DefaultAnimationLoops(legacy));
    }

    // ── Flattening the queued chain (the walk the SetNextState postfix hands to the gate above) ───────────

    private static IReadOnlyList<(string Id, bool IsLooping)> Flatten(
        SpineQueuedStateDouble? head,
        int maxLinks = Sts2SpineDefaults.MaxQueuedChainLinks)
        => Sts2SpineDefaults.FlattenQueuedChain(
            head,
            static link => link.ReadNext(),
            static link => link.Describe(),
            maxLinks);

    // The ordinary shape: a one-shot's queued return, then whatever that queues, in queue order.
    [Fact]
    public void FlattenQueuedChain_WalksEveryLinkInQueueOrder()
    {
        var recover = new SpineQueuedStateDouble("recover", isLooping: false);
        var idle = new SpineQueuedStateDouble("idle_loop", isLooping: true);
        recover.Next = idle;

        Assert.Equal([("recover", false), ("idle_loop", true)], Flatten(recover));
    }

    // Nothing queued behind the head: an empty chain, which ResolveRecordableSequence turns into "no return".
    [Fact]
    public void FlattenQueuedChain_NoHead_IsEmpty()
    {
        Assert.Empty(Flatten(null));
        Assert.Empty(Flatten(new SpineQueuedStateDouble("idle_loop", isLooping: true), maxLinks: 0));
    }

    // THE POINT of routing the successor through the API lane rather than a field: a build may DECIDE its
    // queued return at call time, so the walk must ask, and must ask on every link. Here the same chain answers
    // with a different idle depending on a condition the step reads — the postfix runs one instruction after the
    // game asked the same question, so it gets the same answer.
    [Fact]
    public void FlattenQueuedChain_AsksTheSuccessorStepPerCall()
    {
        var attack = new SpineQueuedStateDouble("attack", isLooping: false);
        var idle = new SpineQueuedStateDouble("idle_loop", isLooping: true);
        var lowHealthIdle = new SpineQueuedStateDouble("low_health_loop", isLooping: true);

        var lowHealth = false;
        IReadOnlyList<(string Id, bool IsLooping)> WalkFrom(SpineQueuedStateDouble head) =>
            Sts2SpineDefaults.FlattenQueuedChain(
                head,
                link => ReferenceEquals(link, attack) ? (lowHealth ? lowHealthIdle : idle) : null,
                static link => link.Describe());

        Assert.Equal([("attack", false), ("idle_loop", true)], WalkFrom(attack));

        lowHealth = true;
        Assert.Equal([("attack", false), ("low_health_loop", true)], WalkFrom(attack));
    }

    // A chain that leads back to a link it already yielded must STOP. A per-call successor can produce one, and
    // this walk runs on the game's main thread inside a Harmony postfix, where a spin is a hang.
    [Fact]
    public void FlattenQueuedChain_StopsOnACycle()
    {
        var selfLink = new SpineQueuedStateDouble("idle_loop", isLooping: true);
        selfLink.Next = selfLink;
        Assert.Equal([("idle_loop", true)], Flatten(selfLink));

        var first = new SpineQueuedStateDouble("attack", isLooping: false);
        var second = new SpineQueuedStateDouble("recover", isLooping: false);
        first.Next = second;
        second.Next = first;
        Assert.Equal([("attack", false), ("recover", false)], Flatten(first));
    }

    // …and a chain that never repeats a link still stops at the cap, so an unbounded generator cannot spin it.
    [Fact]
    public void FlattenQueuedChain_StopsAtTheLinkCap()
    {
        var minted = 0;
        var endless = Sts2SpineDefaults.FlattenQueuedChain(
            new SpineQueuedStateDouble("attack", isLooping: false),
            _ => new SpineQueuedStateDouble($"filler{minted++}", isLooping: false),
            static link => link.Describe());

        Assert.Equal(Sts2SpineDefaults.MaxQueuedChainLinks, endless.Count);
        Assert.Equal("attack", endless[0].Id);

        var capped = Sts2SpineDefaults.FlattenQueuedChain(
            new SpineQueuedStateDouble("attack", isLooping: false),
            _ => new SpineQueuedStateDouble("filler", isLooping: false),
            static link => link.Describe(),
            maxLinks: 2);
        Assert.Equal(2, capped.Count);
    }

    // The composition the postfix actually performs: walk the chain, then gate it on the skeleton. A resolved
    // return is what lets a played one-shot END — without it the mirror reports the one-shot forever, because a
    // frozen spine node never advances its own track queue.
    [Fact]
    public void FlattenQueuedChain_FeedsTheRecordableSequence()
    {
        var attack = new SpineQueuedStateDouble("attack", isLooping: false);
        var idle = new SpineQueuedStateDouble("idle_loop", isLooping: true);
        attack.Next = idle;

        var resolved = Sts2SpineDefaults.ResolveRecordableSequence(
            "attack", headLooping: false, Flatten(attack.Next), _ => true);
        Assert.Equal(("attack", false, "idle_loop", true), resolved);

        // THE BETA DEFECT, in the only shape the core can see it: a successor step that answers "nothing"
        // leaves the one-shot with no return at all.
        var unqueued = Sts2SpineDefaults.ResolveRecordableSequence(
            "attack", headLooping: false, Flatten(null), _ => true);
        Assert.Equal(("attack", false, (string?)null, false), unqueued);
    }

}
