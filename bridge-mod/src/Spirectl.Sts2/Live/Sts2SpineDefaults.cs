namespace Spirectl.Sts2.Live;

// Godot-free core of the mirror's spine-animation SEMANTICS: which clip to present for a node the producer has
// not yet observed a real animation for, whether that guess should LOOP, and which links of a
// CreatureAnimator chain the game actually queued. Always compiled (no Godot dependency) so all three are
// offline-unit-testable, like Sts2SpineSchedule. The Godot-typed callers (Sts2SpineInspector.ReadLive,
// Sts2SpineAnimationHooks.SetNextStatePostfix) delegate here.
//
// WHY (round-8 items 10/11 — "enemy spines loop the wrong animation"): the producer used to answer an
// unobserved node with `animations[0]` reported as LOOPING. `Sts2SpineInspector.ReadAnimations` sorts the
// clip names ORDINALLY, so for sneaky_gremlin — [attack, awake_loop, breathe, die, hurt_awake, hurt_stunned,
// spawn, stunned_loop, wake_up] — `animations[0]` is "attack": the gremlin stood in combat replaying its
// attack forever. Two independent defects: the ORDINAL guess (fixed by PickDefaultAnimation below) and the
// hardcoded loop flag (fixed by DefaultAnimationLoops — a guessed one-shot now clamps on its last frame in
// both clients instead of replaying).
//
// WHY (the Regent's permanent daggers): clamping a guessed one-shot on its last frame was still a guess at a pose
// the game never struck. The Regent's two weapon sprites (regent.tscn, Visuals/Weapons/WeaponAnim*) are authored
// `preview_animation = "-- Empty --"` on a skeleton whose ENTIRE clip list is attack-shaped (attack/attack1/
// attack2/attack_end/attack_test): the game applies nothing to them until NRegentVfx.Attack() fires a one-shot on
// the body's `attack1` spine event, so between swings they are meant to render their SETUP pose — no weapon at
// all. Nothing loop-shaped exists to pick, so the guess fell through to `animations[0]` = "attack", the client's
// default still-mode sampled the MIDDLE of it, and every browser viewer saw light daggers frozen mid-swing from
// the moment combat opened. PickDefaultAnimation now reports NO animation rather than a one-shot: a spine node the
// game has applied nothing to draws its setup pose, and the client already treats a null current anim as "not a
// clip node" (isSpineClipNode → false) and tears the spine layer down. Drawing nothing is strictly closer to the
// setup pose than inventing a frame out of the middle of an attack.
internal static class Sts2SpineDefaults
{
    // Exact names that are unambiguously THE idle loop. Checked first, in this order.
    private static readonly string[] PreferredIdleAnimations = ["idle_loop", "idle", "idle_anim"];

    // Exact names checked only after the `*_loop` suffix scan below, so a creature that has a real
    // `<state>_loop` idle (gremlins: awake_loop / stunned_loop) is never answered with a base pose
    // ("breathe" exists on the gremlin skeleton but no AnimState ever plays it) and a single-clip decorative
    // spine still resolves to its one clip: the chest / boss map point / logo name it "animation", the
    // merchant's hand (merchant_inventory.tscn, observed in .sts2/bench/audit-shop-open.ndjson) names it
    // "default". These all read as looping in DefaultAnimationLoops too.
    private static readonly string[] SecondaryDefaultAnimations =
        ["breathe", "animation", "default", "loop", "loop_anim"];

    // A `*_loop` clip whose name starts with one of these is a TERMINAL pose (a corpse lying still), never the
    // resting state of a live creature — deprioritised so `die_loop`/`dead_loop` only win when nothing else does.
    private static readonly string[] TerminalLoopPrefixes = ["die", "dead", "death"];

    private const string LoopSuffix = "_loop";

    /// <summary>
    /// A STABLE default animation to present for a Spine node the producer has not yet seen a real animation
    /// for, picked from the static animation list. Preference order:
    /// <list type="number">
    /// <item>an exact well-known idle name (<c>idle_loop</c>/<c>idle</c>/<c>idle_anim</c>);</item>
    /// <item>a plain (non track-namespaced) <c>*_loop</c> clip that is not a terminal death pose;</item>
    /// <item>any plain <c>*_loop</c> clip;</item>
    /// <item>an exact secondary default (<c>breathe</c>/<c>animation</c>/<c>loop</c>/<c>loop_anim</c>);</item>
    /// <item>the first listed clip (the pre-fix behavior, now only reached when nothing above matched).</item>
    /// </list>
    /// Returns null when the node exposes no animations — and, when <paramref name="blankOneShotGuess"/> is set
    /// (the default; the producer passes <c>SPIRECTL_SPINE_DEFAULT_ONESHOT_BLANK</c> here), ALSO when every step
    /// above landed on something that is not loop-shaped. That last case is a skeleton with no resting clip at all
    /// — the Regent's weapons — where any answer is a pose the game never played, so the honest answer is "none":
    /// the client drops the spine layer and the node renders as the setup pose the .tscn authored it with.
    /// </summary>
    public static string? PickDefaultAnimation(IReadOnlyList<string>? animations, bool blankOneShotGuess = true)
    {
        var picked = PickAnimationName(animations);
        if (picked is null || !blankOneShotGuess || DefaultAnimationLoops(picked))
        {
            return picked;
        }

        // Every candidate above read as a ONE-SHOT (attack/die/bite): the skeleton exposes no resting state, so
        // the game must apply a clip before this node has any pose to show. Answer "no animation" instead of a
        // fabricated mid-swing frame.
        return null;
    }

    // The raw preference walk — the pick BEFORE the one-shot blanking above. Kept separate so the blanking is one
    // visible decision rather than five `return null`s threaded through the preference order.
    private static string? PickAnimationName(IReadOnlyList<string>? animations)
    {
        if (animations is null || animations.Count == 0)
        {
            return null;
        }

        foreach (var preferred in PreferredIdleAnimations)
        {
            if (FindExact(animations, preferred) is { } exact)
            {
                return exact;
            }
        }

        if (FindLoopSuffixed(animations, allowTerminal: false) is { } loop)
        {
            return loop;
        }

        if (FindLoopSuffixed(animations, allowTerminal: true) is { } terminalLoop)
        {
            return terminalLoop;
        }

        foreach (var secondary in SecondaryDefaultAnimations)
        {
            if (FindExact(animations, secondary) is { } exact)
            {
                return exact;
            }
        }

        return animations[0];
    }

    /// <summary>
    /// Whether a GUESSED default animation should be reported as looping. Only clips that read as an idle/loop
    /// get the loop flag; anything else (a one-shot like <c>attack</c>/<c>die</c>/<c>bite</c> that only got
    /// picked because the skeleton has nothing loop-shaped) is reported non-looping, so both clients clamp on
    /// its last frame instead of replaying it forever.
    ///
    /// <c>animation</c> stays LOOPING: it is the whole content of the single-clip decorative spines (chest,
    /// boss map point, logo) and reporting it non-looping would change their pre-fix appearance in the window
    /// before the game's own SetAnimation/AddAnimation reaches the hook — which then overrides this guess with
    /// the real flag anyway.
    /// </summary>
    public static bool DefaultAnimationLoops(string? animation)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return false;
        }

        if (animation.EndsWith(LoopSuffix, StringComparison.OrdinalIgnoreCase)
            || animation.Contains("idle", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var name in SecondaryDefaultAnimations)
        {
            if (string.Equals(animation, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The sequence the game ACTUALLY queued for a <c>CreatureAnimator.SetNextState(state)</c> call, given the
    /// state's id/loop flag, its <c>NextState</c> chain flattened in order, and the sprite's
    /// <c>MegaSprite.HasAnimation</c> predicate.
    ///
    /// <para>This mirrors the game's own gates: <c>SetNextState</c> logs a warning and RETURNS without touching
    /// the track when the head clip is missing from the skeleton, and <c>AddNextState</c> does the same, which
    /// also ends its recursion — so a missing link truncates the queue. The postfix used to record the head +
    /// walked chain unconditionally, so a creature whose skeleton lacks the state id had the mirror switch to
    /// a clip that does not exist (or hand off to a queued idle that was never queued) while the game kept
    /// playing the previous animation. Returning null means "the game played nothing — leave the recorded
    /// schedule alone".</para>
    /// </summary>
    public static (string Head, bool HeadLooping, string? NextAnim, bool NextLooping)? ResolveRecordableSequence(
        string? headAnim,
        bool headLooping,
        IReadOnlyList<(string Id, bool IsLooping)> queuedChain,
        Func<string, bool> hasAnimation)
    {
        if (string.IsNullOrWhiteSpace(headAnim) || !hasAnimation(headAnim))
        {
            return null;
        }

        string? nextAnim = null;
        var nextLooping = false;
        foreach (var (id, isLooping) in queuedChain)
        {
            if (string.IsNullOrWhiteSpace(id) || !hasAnimation(id))
            {
                break; // AddNextState bails here and stops recursing — nothing further was queued.
            }

            nextAnim = id;
            nextLooping = isLooping;
            if (isLooping)
            {
                break; // the terminal idle loop the one-shot hands back to
            }
        }

        return (headAnim, headLooping, nextAnim, nextLooping);
    }

    private static string? FindExact(IReadOnlyList<string> animations, string wanted)
    {
        foreach (var candidate in animations)
        {
            if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    // A `*_loop` clip usable as a body-track default: plain-named only. Track-namespaced clips ("_tracks/…",
    // "right/idle_loop", "tracks/writhe") are overlay-track content the game plays on track 1/2 — presenting one
    // as the body animation would show a fragment of the creature instead of the creature.
    private static string? FindLoopSuffixed(IReadOnlyList<string> animations, bool allowTerminal)
    {
        foreach (var candidate in animations)
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || !candidate.EndsWith(LoopSuffix, StringComparison.OrdinalIgnoreCase)
                || candidate.Contains('/')
                || candidate.StartsWith('_'))
            {
                continue;
            }

            if (!allowTerminal && IsTerminalLoop(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsTerminalLoop(string candidate)
    {
        foreach (var prefix in TerminalLoopPrefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
