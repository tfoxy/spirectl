using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Feeds the mirror's current-spine-animation off the game's ACTUAL <c>CreatureAnimator.SetNextState</c> call
/// rather than the <c>animation_started</c> Godot signal that <see cref="Sts2SpineInspector"/> normally reacts to.
///
/// <para>Why: the couch-coop headless CPU saver freezes spine nodes permanently (<c>ProcessMode.Disabled</c>).
/// A frozen SpineSprite no longer runs its per-frame process, so it never emits <c>animation_started</c> AND its
/// native track queue never auto-advances a one-shot (attack) to its pre-queued loop (idle_loop). The signal
/// cache would latch on the last-seen anim and the mirror would miss every idle→attack→idle change. Hooking the
/// game's own <c>SetNextState</c> — issued from combat logic (<c>CreatureCmd → NCreature.SetAnimationTrigger →
/// CreatureAnimator.SetTrigger</c>), independent of the node's process — captures the transition at call time.
/// It is the spine analog of <see cref="Sts2ParticleRestartHooks"/>.</para>
///
/// <para><c>SetNextState(AnimState)</c> is the sole track-0 choke point: it calls
/// <c>SetAnimation(state.Id, state.IsLooping)</c> and, for a one-shot, pre-queues the return through one of the
/// lane's queue entry points (<c>GameApiSpine.QueueAnimationTargets</c>). The postfix reads the driven node from
/// the private
/// <c>_spineController</c> (<c>MegaSprite.BoundObject</c>), the sequence from <c>state</c> + the queued chain
/// behind it (walked through the API lane, since the accessor differs by game build), and the one-shot's
/// duration from the just-created track entry
/// (<c>GetAnimationState().GetCurrent(0).GetAnimationEnd()</c> — the same call the game makes in this method),
/// falling back to the clip's STATIC skeleton-data duration when that live read comes back 0 on a frozen node (so a
/// one-shot with a queued return is never handed off IMMEDIATELY, which would drop the attack clip entirely),
/// then hands them to <see cref="Sts2SpineInspector.RecordScheduledAnimation"/>, which replays the sequence off
/// wall-clock. No wire/DTO/client change.</para>
///
/// <para>This observes MegaCrit's own animation-controller calls and reads animation names/durations
/// (game-produced data). It ships/patches no Spine Runtimes, consistent with the mod's spine-license posture.
/// Telemetry must never disrupt the game: install is best-effort and every postfix body is wrapped in try/catch.</para>
/// </summary>
internal static class Sts2SpineAnimationHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    // The private CreatureAnimator field holding the MegaSprite whose SpineSprite node this animator drives.
    // Resolved once at install; the postfix uses it to key the recorded schedule by the node's instance id.
    private static FieldInfo? _spineControllerField;

    // The private SpineAnimationAccess field holding the MegaSprite it wraps (#5). SpineAnimationAccess is a
    // readonly struct; the postfix reads _sprite off the (boxed) instance to reach the driven node.
    private static FieldInfo? _spineAccessSpriteField;

    // #5 (defeat death anim): opt-out escape hatch for the SpineAnimationAccess.SetAnimation hook. Default ON.
    // When the player dies, NGameOverScreen.MoveCreaturesToDifferentLayerAndDisableUi poses the corpse via a
    // DIRECT `nCreatureVisuals.SpineAnimation.SetAnimation("die", loop:false)` — SpineAnimationAccess.SetAnimation,
    // NOT CreatureAnimator.SetNextState. On a couch-frozen, freshly-created game-over creature that direct set
    // never reaches the mirror (no animation_started; no SetNextState), so ReadLive falls to
    // PickDefaultAnimation → idle_loop and the client shows the character standing idle instead of dead. Hooking
    // SpineAnimationAccess.SetAnimation funnels the pose into RecordScheduledAnimation (non-looping "die" → the
    // client freezes on the corpse frame). Combat is unaffected: CreatureAnimator.SetNextState drives the spine
    // via MegaAnimationState.SetAnimation directly, bypassing SpineAnimationAccess, so this hook never fires for
    // combat transitions.
    private static readonly bool SetAnimationHookEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_SETANIM_HOOK") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Round-8 items 10/11: opt-out for the DIRECT MegaAnimationState.SetAnimation/AddAnimation hooks (plus the
    // MegaSprite.TryGetAnimationState owner map they need, and the HasAnimation gate on the SetNextState
    // postfix). Default ON; `0`/`false`/`off`/`no` restores the pre-round-8 CreatureAnimator-only capture.
    private static readonly bool DirectAnimationHookEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_DIRECTANIM_HOOK") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            Harmony harmony;
            try
            {
                harmony = new Harmony("spirectl.spine-anim");
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.spine-anim",
                    $"Skipping spine-anim hook because Harmony init failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // CreatureAnimator.SetNextState is a private instance method (the track-0 choke point).
            var target = typeof(CreatureAnimator).GetMethod(
                "SetNextState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var postfix = typeof(Sts2SpineAnimationHooks).GetMethod(
                nameof(SetNextStatePostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _spineControllerField = typeof(CreatureAnimator).GetField(
                "_spineController",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (target is null || postfix is null || _spineControllerField is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.spine-anim",
                    "Skipping spine-anim hook; CreatureAnimator.SetNextState / _spineController was not found.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.spine-anim",
                    $"Skipping spine-anim hook; patching CreatureAnimator.SetNextState failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // #5: also hook SpineAnimationAccess.SetAnimation (the direct-set path the game-over screen uses to
            // pose the dead character). Best-effort and independent of the core SetNextState hook above — if it
            // cannot be patched (e.g. Harmony can't patch this readonly-struct instance method on some runtime),
            // the combat path still works; only the defeat-screen corpse pose is missed.
            TryInstallSetAnimationHook(harmony, logStream);

            // Round-8 items 10/11: the DIRECT-set path (bite/chest/boss/merchant/rest-site — everything not
            // driven by CreatureAnimator). Best-effort and independent of the hooks above.
            TryInstallDirectAnimationHooks(harmony, logStream);

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.mirror.spine-anim",
                "Installed spine-anim hook; mirror spine anim now tracks CreatureAnimator.SetNextState (survives a frozen ProcessMode).");
        }
    }

    // Install the #5 SpineAnimationAccess.SetAnimation postfix. Kept separate + fully guarded so any failure is
    // non-fatal to the primary SetNextState hook. No-op when the escape hatch disables it.
    private static void TryInstallSetAnimationHook(Harmony harmony, ILogStream logStream)
    {
        if (!SetAnimationHookEnabled)
        {
            return;
        }

        try
        {
            var accessType = typeof(SpineAnimationAccess);
            _spineAccessSpriteField = accessType.GetField("_sprite", BindingFlags.Instance | BindingFlags.NonPublic);
            var target = accessType.GetMethod(
                "SetAnimation",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string), typeof(bool), typeof(int) },
                modifiers: null);
            var postfix = typeof(Sts2SpineAnimationHooks).GetMethod(
                nameof(SetAnimationPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null || _spineAccessSpriteField is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.spine-anim",
                    "Skipping #5 SetAnimation hook; SpineAnimationAccess.SetAnimation / _sprite was not found.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.mirror.spine-anim",
                "Installed #5 SetAnimation hook; direct SpineAnimationAccess.SetAnimation sets (defeat-screen corpse pose) now reach the mirror.");
        }
        catch (Exception ex)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.mirror.spine-anim",
                $"Skipping #5 SetAnimation hook; patching SpineAnimationAccess.SetAnimation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Install the round-8 DIRECT-animation hooks. Three postfixes that only ever WRITE to spirectl-owned
    // dictionaries:
    //   * MegaSprite.TryGetAnimationState → the animation-state → SpineSprite owner map (see
    //     Sts2SpineInspector.NodeByAnimState). It is the sole choke point every caller goes through to obtain a
    //     state, so the pairing is always known before any SetAnimation on that state lands.
    //   * MegaAnimationState.SetAnimation(name, loop, track) → the clip + REAL loop flag the game just played.
    //     This is what makes the bite overlay's one-shot reach the wire as `spineLooping:false` inside its
    //     START message, so no end/"animation finished" message is needed at all.
    //   * every queue entry point the lane declares (GameApiSpine.QueueAnimationTargets) → the clip queued
    //     behind it. Which method a build queues through is build-specific, and a build that queues a LOOPING
    //     return through a different entry point than a one-shot needs both patched or the return is invisible.
    // Each is patched independently so one failure never costs the others. No-op when the escape hatch is off.
    private static void TryInstallDirectAnimationHooks(Harmony harmony, ILogStream logStream)
    {
        if (!DirectAnimationHookEnabled)
        {
            return;
        }

        var installed = 0;
        installed += TryPatch(
            harmony,
            logStream,
            typeof(MegaSprite).GetMethod(
                nameof(MegaSprite.TryGetAnimationState),
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null),
            nameof(AnimationStateOwnerPostfix),
            "MegaSprite.TryGetAnimationState")
            ? 1
            : 0;
        installed += TryPatch(
            harmony,
            logStream,
            typeof(MegaAnimationState).GetMethod(
                nameof(MegaAnimationState.SetAnimation),
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string), typeof(bool), typeof(int) },
                modifiers: null),
            nameof(DirectSetAnimationPostfix),
            "MegaAnimationState.SetAnimation")
            ? 1
            : 0;
        // Every queue entry point THIS game build routes a queued clip through (see
        // GameApiSpine.QueueAnimationTargets). One on v107; v111 splits tracked/untracked and sends a looping
        // return — the idle a one-shot hands back to — through the tracked one, so patching a single overload
        // there would miss exactly the transition this hook exists to observe.
        var queueTargets = GameApiSpine.QueueAnimationTargets();
        foreach (var (description, target) in queueTargets)
        {
            installed += TryPatch(harmony, logStream, target, nameof(DirectAddAnimationPostfix), description)
                ? 1
                : 0;
        }

        installed += TryPatch(
            harmony,
            logStream,
            typeof(MegaAnimationState).GetMethod(
                nameof(MegaAnimationState.SetTimeScale),
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(float) },
                modifiers: null),
            nameof(DirectSetTimeScalePostfix),
            "MegaAnimationState.SetTimeScale")
            ? 1
            : 0;

        // 3 fixed targets (owner map, SetAnimation, SetTimeScale) plus this lane's queue entry points.
        var expected = 3 + queueTargets.Count;
        if (installed > 0)
        {
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.mirror.spine-anim",
                $"Installed {installed}/{expected} direct spine-anim hook(s) on lane {GameApiLane.Name}; direct "
                + "MegaAnimationState sets (one-shot VFX overlays, chest, boss map node, merchant) now reach the "
                + "mirror with their real loop flag, every queued clip reaches it as a return, and a paused track "
                + "(SetTimeScale(0)) stops the replay walking off its clip.");
        }
    }

    // Patch one target with a named static postfix from this type. Fully guarded: logs and returns false on any
    // resolution/patch failure so a single missing method never blocks the rest of the install.
    private static bool TryPatch(
        Harmony harmony,
        ILogStream logStream,
        MethodInfo? target,
        string postfixName,
        string description)
    {
        try
        {
            var postfix = typeof(Sts2SpineAnimationHooks).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.mirror.spine-anim",
                    $"Skipping spine-anim hook; {description} was not found.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            return true;
        }
        catch (Exception ex)
        {
            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.mirror.spine-anim",
                $"Skipping spine-anim hook; patching {description} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Harmony postfix on MegaSprite.TryGetAnimationState(). Pure bookkeeping: pairs the returned animation
    // state with the SpineSprite node that owns it, so the SetAnimation/AddAnimation postfixes below can key
    // their recording by node. Both objects are already-materialised managed wrappers here, so this costs two
    // GetInstanceId reads and a dictionary write — no extra native call. Never throws.
    private static void AnimationStateOwnerPostfix(MegaSprite __instance, MegaAnimationState? __result)
    {
        try
        {
            if (__result?.BoundObject is not { } state
                || __instance.BoundObject is not Node node
                || !GodotObject.IsInstanceValid(node)
                || !GodotObject.IsInstanceValid(state))
            {
                return;
            }

            Sts2SpineInspector.RecordAnimationStateOwner(state.GetInstanceId(), node.GetInstanceId());
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Harmony postfix on MegaAnimationState.SetAnimation(string, bool, int). Runs on the game main thread right
    // after the game played a clip DIRECTLY on a spine node — the path CreatureAnimator never touches
    // (NVfxSpine one-shot overlays like the bite, NTreasureRoom's chest, NMerchantCharacter, NRestSiteCharacter,
    // NOrb, …). Records the clip WITH the game's own loop flag, so a one-shot arrives at the client as
    // `spineLooping:false` and is clamped on its last frame instead of replayed forever.
    private static void DirectSetAnimationPostfix(MegaAnimationState __instance, string animationName, bool loop, int trackId)
    {
        try
        {
            if (ResolveDirectTarget(__instance, animationName, trackId) is not { } node)
            {
                return;
            }

            // A one-shot's duration, so a clip queued behind it (DirectAddAnimationPostfix) knows when to hand
            // off. The STATIC skeleton duration is the right read here: the live track entry belongs to the
            // just-replaced animation on a frozen node. Irrelevant for a looping head (it plays until replaced).
            var currentDurMsec = loop
                ? 0
                : Sts2SpineSchedule.ResolveOneShotDurationMsec(
                    trackEndSeconds: 0,
                    skeletonDurationSeconds: TryReadSkeletonAnimationDurationSeconds(node, animationName));

            var capturedSkin = Sts2SpineInspector.TryReadCurrentSkinName(node);
            Sts2SpineInspector.RecordSkin(node.GetInstanceId(), capturedSkin);

            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"SetAnimation(state) node={node.GetInstanceId()} name={node.Name} anim='{animationName}' "
                    + $"looping={loop} durMs={currentDurMsec:0.#} skin='{capturedSkin ?? "<none>"}'");
            }

            Sts2SpineInspector.RecordDirectAnimation(
                node.GetInstanceId(), animationName, loop, currentDurMsec, Time.GetTicksMsec());
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Harmony postfix on every queue entry point the lane declares — MegaAnimationState.AddAnimation and, where
    // the build has one, its tracked sibling; the argument list is identical, so one postfix serves both. The
    // queued companion of the set above: a one-shot's return loop (CreatureAnimator, NTreasureRoom's
    // "shine_fade") or — when the game queues onto an empty track, which NBossMapPoint._Ready does — the clip
    // that will actually play. `delay` is deliberately ignored: the schedule replays a single head→return
    // handoff, and every track-0 call site in the game passes 0.
    private static void DirectAddAnimationPostfix(MegaAnimationState __instance, string animationName, float delay, bool loop, int trackId)
    {
        try
        {
            if (ResolveDirectTarget(__instance, animationName, trackId) is not { } node)
            {
                return;
            }

            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"AddAnimation(state) node={node.GetInstanceId()} name={node.Name} anim='{animationName}' "
                    + $"looping={loop} delay={delay:0.##}");
            }

            Sts2SpineInspector.RecordQueuedAnimation(node.GetInstanceId(), animationName, loop);
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Harmony postfix on MegaAnimationState.SetTimeScale(float) (round-8 item 13). The treasure chest is set up
    // as `SetAnimation("animation") + AddAnimation("shine_fade") + SetTimeScale(0)`: the game FREEZES it on the
    // closed-chest first frame and only resumes (SetTimeScale(1)) once the player opens it. The producer's
    // schedule replay runs off WALL CLOCK, so without this the chest walked "animation" to its end and handed off
    // to the queued "shine_fade" — both clients showed an OPEN, glowing chest in a room whose chest is visibly
    // shut. There is no track argument on this overload (it scales the whole state), so no track-0 gate here; the
    // owner map is the only thing needed. Never throws.
    private static void DirectSetTimeScalePostfix(MegaAnimationState __instance, float scale)
    {
        try
        {
            if (__instance.BoundObject is not { } bound || !GodotObject.IsInstanceValid(bound))
            {
                return;
            }

            var nodeId = Sts2SpineInspector.ResolveAnimationStateOwner(bound.GetInstanceId());
            if (nodeId == 0)
            {
                return;
            }

            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log($"SetTimeScale(state) node={nodeId} scale={scale:0.###}");
            }

            Sts2SpineInspector.RecordTimeScale(nodeId, scale);
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Shared gate for both direct postfixes: track 0 only (higher tracks are overlay content — embers, glowing
    // eyes, the architect's head — that must never become the reported BODY animation), no blank/`_ignore`
    // helper poses (matching OnAnimationStarted / ReadAnimations), and a live owning SpineSprite node.
    private static Node? ResolveDirectTarget(MegaAnimationState state, string animationName, int trackId)
    {
        if (trackId != 0
            || string.IsNullOrWhiteSpace(animationName)
            || animationName.StartsWith("_ignore", StringComparison.OrdinalIgnoreCase)
            || state.BoundObject is not { } bound
            || !GodotObject.IsInstanceValid(bound))
        {
            return null;
        }

        var nodeId = Sts2SpineInspector.ResolveAnimationStateOwner(bound.GetInstanceId());
        if (nodeId == 0)
        {
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log($"direct anim '{animationName}' dropped: no owner known for animation state {bound.GetInstanceId()}");
            }

            return null;
        }

        return GodotObject.InstanceFromId(nodeId) is Node node && GodotObject.IsInstanceValid(node) ? node : null;
    }

    // Harmony postfix on SpineAnimationAccess.SetAnimation(string name, bool loop, int track). Runs on the game
    // main thread right after a DIRECT (non-CreatureAnimator) spine set. Records the track-0 body anim into the
    // hook-driven schedule so a couch-frozen, freshly-created node (the game-over corpse) reports the real anim
    // instead of falling to PickDefaultAnimation(idle_loop). Non-looping "die" → the client freezes on the
    // corpse frame (currentDurMsec 0 + no queued next). Telemetry must never disrupt the game: fully try/caught.
    private static void SetAnimationPostfix(SpineAnimationAccess __instance, string name, bool loop, int track)
    {
        try
        {
            if (track != 0 || string.IsNullOrWhiteSpace(name) || _spineAccessSpriteField is null)
            {
                return; // only the body track (0) drives the reported anim; overlay tracks/glow must not.
            }

            if (_spineAccessSpriteField.GetValue(__instance) is not MegaSprite sprite
                || sprite.BoundObject is not Node node
                || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            // Skip Spine's "_ignore/" helper poses (rigging/cloth/glow), matching OnAnimationStarted.
            if (name.StartsWith("_ignore", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var capturedSkin = Sts2SpineInspector.TryReadCurrentSkinName(node);
            Sts2SpineInspector.RecordSkin(node.GetInstanceId(), capturedSkin);

            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"SetAnimation(direct) node={node.GetInstanceId()} name={node.Name} anim='{name}' "
                    + $"looping={loop} skin='{capturedSkin ?? "<none>"}'");
            }

            // A direct set has no pre-queued return chain (the game issues a single clip); record it as a lone
            // head. Looping → runs forever (an idle set directly); non-looping (die) → plays once then freezes.
            // (Since round 8 this is a subset of DirectSetAnimationPostfix — SpineAnimationAccess.SetAnimation
            // delegates to MegaAnimationState.SetAnimation — but it is kept as an independent safety net: it
            // has its own kill switch and needs no owner-map entry.)
            Sts2SpineInspector.RecordDirectAnimation(
                node.GetInstanceId(),
                name,
                looping: loop,
                currentDurMsec: 0,
                Time.GetTicksMsec());
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Harmony postfix on the private CreatureAnimator.SetNextState(AnimState state). Runs on the game main
    // thread synchronously right after the game issued SetAnimation (+ any pre-queued AddAnimation) on track 0.
    private static void SetNextStatePostfix(object __instance, AnimState state)
    {
        try
        {
            if (state is null || _spineControllerField is null)
            {
                return;
            }

            if (_spineControllerField.GetValue(__instance) is not MegaSprite sprite
                || sprite.BoundObject is not Node node
                || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            // The pre-queued return: walk the queued chain to the first looping state (the terminal idle_loop)
            // so a one-shot hands back to the loop the game queued. HOW the next link is reached differs by
            // game build and lives behind the API lane (GameApiSpine.QueuedNextState) — this postfix runs one
            // instruction after the animator resolved and queued the same link, on the same state object, so a
            // build that decides its return per call decides it identically here. Flattened through pure core
            // so both the walk and the HasAnimation gate below are offline-testable.
            var queuedChain = Sts2SpineDefaults.FlattenQueuedChain(
                GameApiSpine.QueuedNextState(state),
                GameApiSpine.QueuedNextState,
                static queued => (queued.Id, queued.IsLooping));

            // HasAnimation GATE: SetNextState/AddNextState log a warning and RETURN when the clip is absent from
            // the skeleton, leaving the track untouched — this postfix used to record it anyway, so a creature
            // whose skeleton lacks the state id had the mirror switch to a non-existent clip (or hand off to a
            // return that was never queued) while the game kept playing the previous animation. Reading
            // HasAnimation here is safe: SetNextState called it on this very sprite one instruction earlier.
            var sequence = DirectAnimationHookEnabled
                ? Sts2SpineDefaults.ResolveRecordableSequence(state.Id, state.IsLooping, queuedChain, sprite.HasAnimation)
                : Sts2SpineDefaults.ResolveRecordableSequence(state.Id, state.IsLooping, queuedChain, _ => true);
            if (sequence is not { } resolved)
            {
                if (Sts2SpineDiagnostics.Current.Enabled)
                {
                    Sts2SpineDiagnostics.Current.Log(
                        $"SetNextState skipped node={node.GetInstanceId()} name={node.Name} anim='{state.Id}' "
                        + "reason=missing-animation (the game played nothing)");
                }

                return;
            }

            var (currentAnim, currentLooping, nextAnim, nextLooping) = resolved;

            // The one-shot head's duration, so the schedule knows when to hand off to the queued return. Only a
            // non-looping head that has a queued next needs it; a looping head plays until the next SetNextState.
            // Prefer the LIVE track entry's end — the exact call the game itself makes in SetNextState
            // (GetCurrent(0).GetAnimationEnd() via OffsetLoopingAnimation) — but when that reads 0 (it can on a
            // couch-frozen SpineSprite whose track-0 current entry is not materialised the way the game's own
            // looping-branch read relies on), fall back to the clip's STATIC duration from the loaded skeleton data.
            // Without a non-zero duration the schedule would hand off to the queued idle_loop IMMEDIATELY and the
            // attack clip would never stream (the per-card attack visibly does not play). The clip is guaranteed
            // present in the skeleton (SetNextState only plays a clip it gated on HasAnimation), so the fallback
            // always yields the real duration for a real attack. The final decision is pure/offline-testable.
            double currentDurMsec = 0;
            if (!currentLooping && nextAnim is not null)
            {
                var endSeconds = TryReadAnimationEndSeconds(sprite);
                var skeletonSeconds = endSeconds > 0f ? 0f : TryReadSkeletonAnimationDurationSeconds(node, currentAnim);
                currentDurMsec = Sts2SpineSchedule.ResolveOneShotDurationMsec(endSeconds, skeletonSeconds);
            }

            // Capture the runtime skin (#3) in this alive-native, main-thread window — the offline bake instance
            // would otherwise render skin-less (missing body pieces). Defensive read; RecordSkin no-ops on null.
            var capturedSkin = Sts2SpineInspector.TryReadCurrentSkinName(node);
            Sts2SpineInspector.RecordSkin(node.GetInstanceId(), capturedSkin);

            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"SetNextState node={node.GetInstanceId()} name={node.Name} current='{currentAnim}' "
                    + $"looping={currentLooping} next='{nextAnim ?? "<none>"}' nextLooping={nextLooping} "
                    + $"currentDurMs={currentDurMsec:0.#} skin='{capturedSkin ?? "<none>"}'");
            }

            Sts2SpineInspector.RecordScheduledAnimation(
                node.GetInstanceId(),
                currentAnim,
                currentLooping,
                currentDurMsec,
                nextAnim,
                nextLooping,
                Time.GetTicksMsec());
        }
        catch
        {
            // Spine-anim telemetry must never disrupt the game.
        }
    }

    // Duration (seconds) of the animation the game just set on track 0, from the just-created track entry.
    // Best-effort: any failure returns 0, and the caller then falls back to the skeleton-data duration.
    private static float TryReadAnimationEndSeconds(MegaSprite sprite)
    {
        try
        {
            var animationState = sprite.TryGetAnimationState();
            var entry = animationState?.GetCurrent(0);
            return entry?.GetAnimationEnd() ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    // STATIC duration (seconds) of a clip by name, read from the SpineSprite's loaded skeleton data
    // (skeleton_data_res.find_animation(name).get_duration()). This is the fallback for a one-shot head when the
    // live track-entry read comes back 0 on a frozen node. Read via raw Godot Calls with a null-guard at every
    // native hop — the same GC-safe in-handler pattern OnAnimationStarted uses; this runs synchronously on the game
    // main thread inside SetNextState, so the native skeleton data is alive. Best-effort: any failure returns 0.
    private static float TryReadSkeletonAnimationDurationSeconds(Node node, string animName)
    {
        try
        {
            var skeletonDataVar = node.Get("skeleton_data_res");
            if (skeletonDataVar.VariantType != Variant.Type.Object
                || skeletonDataVar.AsGodotObject() is not { } skeletonData)
            {
                return 0f;
            }

            var animationVar = skeletonData.Call("find_animation", animName);
            if (animationVar.VariantType != Variant.Type.Object
                || animationVar.AsGodotObject() is not { } animation)
            {
                return 0f;
            }

            var durationVar = animation.Call("get_duration");
            return durationVar.VariantType == Variant.Type.Float ? durationVar.AsSingle() : 0f;
        }
        catch
        {
            return 0f;
        }
    }
}
