using System.Reflection;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// WS-4: the HAND-LAYOUT LERP producer. Harmony postfixes on <c>NHandCardHolder</c>'s three target-setting entry
/// points coalesce a hand fan-out into ONE endpoint per holder per frame, resolve it through the scene watcher's
/// existing <see cref="Sts2RuntimeSceneWatcher.ResolveTweenEndpoint"/> (which also opens the streaming-suppression
/// window), and publish it as an ORDINARY <see cref="Embedding.TweenAnimationHint"/> on the same
/// <see cref="Embedding.EmbeddableAnimationHintHub"/> the tween recorder and the card-flight hook use.
///
/// <para>WHY THIS EXISTS AS A SEPARATE HOOK — the same reason <see cref="Sts2CardFlightHooks"/> does.
/// <see cref="Sts2TweenRecorderHooks"/> patches Godot <c>Tween</c> construction, and a hand card holder is not
/// animated by a tween at all: each of its three channels is a per-process-frame exponential approach with a fixed
/// rate (see <see cref="Sts2HandTweenMath"/>), so the recorder captures nothing and the mirror streamed every
/// intermediate frame of every holder. A device trace of a
/// start-of-turn draw measured <c>Hand/CardHolderContainer</c> at 369.3 KB / 594 upserts — 21.4% of a 3 s draw
/// window, peaking at 1.03 MB / 2477 upserts in one second — because EVERY newly drawn card re-targets EVERY
/// already-settled holder, so the whole hand re-animates once per card.</para>
///
/// <para>NO NEW WIRE TYPE. The motion is an exponential approach with a known rate and a known
/// settle test (see <see cref="Sts2HandTweenMath"/>), and an exponential approach and a CSS expo-out transition are
/// the same curve — so this ships as a plain tween hint with <c>trans = "Expo"</c>, <c>ease = "Out"</c> and a
/// computed duration. The consumer's Godot→CSS easing map already handles Expo/Out, its transform channel already
/// arms the TARGET element only (DOM-nested descendants ride rigidly, so the holder is the right suppression root
/// and its card subtree follows for free), and its <c>armTween</c> already handles being RE-ARMED mid-flight.</para>
///
/// <para>RE-TARGETING MID-FLIGHT IS THE NORMAL CASE, not an edge case: every drawn card re-fans the whole hand, so
/// each holder is re-targeted while its previous approach is still running. Three things make that correct.
/// (1) The endpoint and the duration are recomputed from the holder's LIVE pose at publish time, so the new hint
/// describes the REMAINING travel, not the original one. (2) The resolver OVERWRITES
/// <c>Tracked.SuppressTransformUntil</c> with the new deadline rather than extending it, so the window always
/// matches the newest hint. (3) A batch that ends up publishing NOTHING (unresolvable endpoint, untracked node)
/// CANCELS the node's open window through <see cref="WindowCanceller"/>, so the producer resumes streaming it.</para>
///
/// <para>A CANCEL IS NOT AN ABORT — this is the constraint the whole publish decision turns on. Cancelling un-pins
/// the PRODUCER, but a hint is one-way: a consumer that has already armed one keeps replaying that endpoint until
/// its own deadline, and there is no wire field that says "forget it". So "publish nothing and cancel" is only safe
/// for a holder nobody is pinned to; for a holder mid-approach it strands the card at a pose the game abandoned.
/// Two mechanisms follow. (a) A batch whose natural duration falls under the floor still publishes, at
/// <c>MinHintMs</c>, whenever <see cref="HasOpenWindow"/> says a window is open — see
/// <see cref="Sts2HandTweenMath.CorrectiveDurationMs"/>. (b) For the paths that genuinely cannot publish (the
/// endpoint does not resolve, the holder was dragged or cleared), the consumer carries the belt: it remembers the
/// streamed pose its pin overrode and applies it at the settle instead of discarding it.</para>
///
/// <para>SCOPE — why the publish is gated on a hand FAN-OUT batch. The same three setters are ALSO driven once per
/// process frame while a card is being dragged, to keep it under the cursor. Publishing there would be strictly
/// worse than streaming (one hint per frame) and, far worse, would PIN the dragged card client-side for the hint's
/// duration — the consumer ignores streamed transforms while a tween channel is armed. So the channel postfixes
/// only record while a hand fan-out is on the stack: <c>NPlayerHand.RefreshLayout</c> (the whole-hand re-layout,
/// run on gain/lose/focus/unfocus) and <c>NHandCardHolder.SetDefaultTargets</c> open that scope, and both are
/// event-driven, never per-frame. <c>BeginDrag</c> and <c>StopAnimations</c> additionally CANCEL any open window,
/// so a card that is picked up, cleared or removed from the tree resumes streaming immediately.</para>
///
/// <para>Discipline, matching every other telemetry hook here: installed unconditionally but INERT unless a hint
/// subscriber is attached (one volatile-bool read on the disabled path), never blocking, and never allowed to throw
/// into the game. The publish is DEFERRED to the next idle frame, both because that is where the three channels of
/// one batch have all landed (which is what makes ONE coalesced hint possible) and because the watcher's node
/// registry is read there on the same thread as the capture walk.</para>
/// </summary>
internal static class Sts2HandHolderHooks
{
    private const string LogTarget = "bridge.hand-tween";
    private const string HolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder";
    private const string HandTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand";

    // Only used to label the published hint (the mirror joins on TargetInstanceId, not on scene/node path). Read
    // from the live node when it has one; this is the fallback for an instance whose SceneFilePath is empty.
    private const string HolderSceneFallback = "cards/holders/hand_card_holder";
    private const string ScenePrefix = "res://scenes/";
    private const string SceneSuffix = ".tscn";

    // The hint's `property`. Not a Godot property tween, but position IS the channel that gates the endpoint (see
    // the HasPosition note in Publish), and the consumer only reads `property` to tell a modulate fade from a
    // self_modulate one — so an accurate transform-channel name is both honest and inert.
    private const string HintProperty = "position";

    // Godot's own trans/ease enum spellings, so the consumer's raw-name Godot→CSS map resolves them.
    private const string HintTrans = "Expo";
    private const string HintEase = "Out";

    // The game's own script-registered field names (NHandCardHolder.PropertyName.*), read through Godot's property
    // bridge exactly like Sts2CardFlightHooks does: the class's generated GetGodotClassPropertyValue exposes all
    // three, so `Get(name)` is a direct, allocation-light native read on the game thread. Reading the FIELD at
    // drain time (rather than the setter's argument) is what coalesces a batch: whatever the last setter of the
    // frame wrote is what we publish.
    private static readonly StringName TargetPositionProp = "_targetPosition";
    private static readonly StringName TargetAngleProp = "_targetAngle";
    private static readonly StringName TargetScaleProp = "_targetScale";

    // The LIVE pose the approach starts from. Raw Godot property names (not a generated `PropertyName` constant),
    // matching how Sts2RuntimeSceneWatcher.ResolveTweenEndpoint reads the very same three properties, so the
    // distances measured here and the endpoint reconstructed there can never disagree about which value they mean.
    // `rotation` is RADIANS (Control/Node2D alike); the game's target angle is DEGREES — see TryPublish.
    private static readonly StringName PositionProp = "position";
    private static readonly StringName RotationProp = "rotation";
    private static readonly StringName ScaleProp = "scale";

    // A hand is at most 10 holders (HandPosHelper's tables throw above that), and RefreshLayout touches each one
    // once per batch. The cap is a runaway guard, not a working limit.
    private const int PendingCap = 64;

    private static readonly object Sync = new();
    private static bool _installed;
    private static ILogStream? _log;

    // Every decline below is silent by design (this runs on the game main thread), so the counters are the only way
    // to answer "which gate ate the hint?" from outside. One interlocked add per coalesced batch, never per frame —
    // see the InFanBatch note in NoteChannel for the one gate that is deliberately NOT counted.
    private static ISts2AnimationHintCounter Counters => Sts2AnimationHintInstrumentation.Current.HandTween;

    // Registered by Sts2RuntimeFactory, backed by the live scene watcher. EndpointResolver maps the coalesced
    // target into the watcher's streamed space AND opens the suppression window; WindowCanceller collapses an open
    // window when a fresh batch produces no hint. Null when no watcher is wired (tests / recording-only) — then
    // nothing is published at all, because a hint without its window is pure wire cost for a client that would
    // still be receiving every streamed frame.
    internal static Func<ulong, TweenTargetChange, double, TweenEndpoint?>? EndpointResolver { get => Sts2SceneAnimationCallbacks.HandEndpointResolver; set => Sts2SceneAnimationCallbacks.HandEndpointResolver = value; }
    internal static Action<IReadOnlyCollection<ulong>, bool>? WindowCanceller { get => Sts2SceneAnimationCallbacks.HandWindowCanceller; set => Sts2SceneAnimationCallbacks.HandWindowCanceller = value; }

    // Does this holder still have an OPEN suppression window — i.e. is a consumer still pinned to a hint an earlier
    // batch published? A cancel is invisible to a client that has already armed one, so a sub-floor batch may only be
    // dropped for a holder nobody is pinned to; otherwise it publishes a short corrective hint instead (see
    // Sts2HandTweenMath.CorrectiveDurationMs). Null (no watcher wired) reads as "not pinned", which is correct: with
    // no resolver nothing was ever published in the first place.
    internal static Func<ulong, bool>? HasOpenWindow { get => Sts2SceneAnimationCallbacks.HasOpenWindow; set => Sts2SceneAnimationCallbacks.HasOpenWindow = value; }

    // ---- fan-out batch scope (game main thread only) ---------------------------------------------

    // Depth of the hand fan-out currently on the stack, plus the process frame it was entered on. The frame stamp
    // bounds the blast radius of a leaked depth: if a fan-out ever throws (its postfix would then not run), the
    // gate can still only be open for setter calls made in that SAME frame, never forever.
    private static int _fanDepth;
    private static ulong _fanFrame;

    // ---- coalescing state (game main thread only) ------------------------------------------------

    private static readonly Dictionary<ulong, PendingHolder> Pending = new();

    // Two separate scratch lists on purpose: the drain runs at IDLE and the abandon postfixes run mid-frame (an
    // _ExitTree can fire during a deferred flush), so sharing one buffer would risk them treading on each other.
    private static readonly List<ulong> CancelScratch = new();
    private static readonly List<ulong> AbandonScratch = new();

    // The process frame whose idle drain has already been queued. Keyed on the FRAME rather than on a "scheduled"
    // bool so that a deferred call which never arrives (Godot's message queue is allowed to drop on overflow) costs
    // one frame's batch instead of silently disabling the whole feature for the rest of the session.
    private static ulong _drainScheduledFrame = ulong.MaxValue;

    public static long HintEmitCount => Sts2AnimationHintInstrumentation.Current.HandTween.Emitted;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _log = logStream;
            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var holderType = AccessTools.TypeByName(HolderTypeName);
            var handType = AccessTools.TypeByName(HandTypeName);
            if (holderType is null || handType is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping hand-tween hook; {HolderTypeName} / {HandTypeName} were not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.hand-tween");

                // The fan-out SCOPE. Prefix opens it, postfix closes it; the channel postfixes below only record
                // while it is open, which is what keeps the per-frame drag driver out of the hint stream.
                PatchScope(harmony, handType, "RefreshLayout");
                PatchScope(harmony, holderType, "SetDefaultTargets");

                // The three channels. Each one only notes WHICH channel was re-targeted; the values are read from
                // the live fields at drain time so a batch that sets a channel twice still publishes once.
                PatchPostfix(harmony, holderType, "SetTargetPosition", nameof(PositionPostfix));
                PatchPostfix(harmony, holderType, "SetTargetAngle", nameof(AnglePostfix));
                PatchPostfix(harmony, holderType, "SetTargetScale", nameof(ScalePostfix));

                // The same two channels' INSTANT setters. The hand's focus layout does not APPROACH the focused pose
                // — it teleports the un-rotate and the un-scale and only then sets a position target — so a batch
                // containing one of these is the one case where the holder's live pose must be shipped as the hint's
                // START (see the InstantPose note in TryPublish). An instant write also SUPERSEDES an earlier
                // re-target of that same channel within the batch: it is the frame's last instruction for the
                // channel, and the channel's target field is not what it wrote — see Sts2HandTweenMath.Teleport.
                // Both are also reached OUTSIDE a fan-out (drag begin/cancel), where the scope gate already
                // discards them — considered and kept: widening the gate for them would buy nothing, because
                // BeginDrag CANCELS the window outright (AbandonPostfix) and the re-layout that ends a drag
                // re-states every channel from scratch.
                PatchPostfix(harmony, holderType, "SetAngleInstantly", nameof(InstantAnglePostfix));
                PatchPostfix(harmony, holderType, "SetScaleInstantly", nameof(InstantScalePostfix));

                // Two ways the game ABANDONS an in-flight approach without setting a new target. Both must collapse
                // the window or the holder freezes client-side for the rest of it: BeginDrag hands the holder to
                // NCardPlay (which then drives it per frame), StopAnimations cancels all three loops from _ExitTree
                // and Clear.
                PatchPostfix(harmony, holderType, "BeginDrag", nameof(AbandonPostfix));
                PatchPostfix(harmony, holderType, "StopAnimations", nameof(AbandonPostfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping hand-tween hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                LogTarget,
                "Installed hand-tween hook; hand fan-out lerps now ship as declarative tween hints "
                + "(SPIRECTL_SCENE_WATCH_HAND_TWEEN=0 restores per-frame streaming).");
        }
    }

    private static void PatchScope(Harmony harmony, Type declaring, string method)
    {
        var target = AccessTools.Method(declaring, method)
            ?? throw new MissingMethodException(declaring.FullName, method);
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(HookMethod(nameof(FanPrefix))),
            postfix: new HarmonyMethod(HookMethod(nameof(FanPostfix))));
    }

    private static void PatchPostfix(Harmony harmony, Type declaring, string method, string postfix)
    {
        var target = AccessTools.Method(declaring, method)
            ?? throw new MissingMethodException(declaring.FullName, method);
        harmony.Patch(target, postfix: new HarmonyMethod(HookMethod(postfix)));
    }

    private static MethodInfo HookMethod(string name) =>
        typeof(Sts2HandHolderHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(Sts2HandHolderHooks), name);

    // ---- fan-out scope postfixes -----------------------------------------------------------------

    // Deliberately a VOID prefix with no `__result` and no bool return: Harmony can only skip the original for a
    // bool-returning prefix, so this cannot alter the game's behaviour, it can only observe that a fan-out started.
    private static void FanPrefix()
    {
        _fanDepth++;
        try
        {
            _fanFrame = Engine.GetProcessFrames();
        }
        catch
        {
            _fanFrame = 0;
        }
    }

    private static void FanPostfix()
    {
        if (_fanDepth > 0)
        {
            _fanDepth--;
        }
    }

    private static bool InFanBatch
    {
        get
        {
            if (_fanDepth <= 0)
            {
                return false;
            }

            try
            {
                return _fanFrame == Engine.GetProcessFrames();
            }
            catch
            {
                return false;
            }
        }
    }

    // ---- channel postfixes -----------------------------------------------------------------------

    private static void PositionPostfix(Node __instance) => NoteChannel(__instance, HandChannels.Position);

    private static void AnglePostfix(Node __instance) => NoteChannel(__instance, HandChannels.Angle);

    private static void ScalePostfix(Node __instance) => NoteChannel(__instance, HandChannels.Scale);

    // The instant setters CLEAR their own channel from the batch, and only their own. They write the live pose and
    // leave that channel's TARGET field holding whatever the last approach was aiming at, so the field is not the
    // instruction they gave: a drain that read it would publish an endpoint rotating/scaling the card back to a pose
    // the game has abandoned. With the bit cleared the resolver instead falls back to the node's LIVE
    // rotation/scale, which right after an instant write is exactly the value the game wants.
    //
    // CLEARING, not merely "not setting". One layout of a hand can re-target a channel and then teleport it within
    // the SAME frame, and the two land in one coalesced batch — so an instant write has to supersede the earlier
    // re-target's bit rather than leave it standing (Sts2HandTweenMath.Teleport). A LATER re-target of the same
    // channel re-sets it, because that is a real instruction again. See
    // `.sts2/research/hand-holder-animation.md`.
    private static void InstantAnglePostfix(Node __instance)
        => NoteChannel(__instance, HandChannels.Angle, instant: true);

    private static void InstantScalePostfix(Node __instance)
        => NoteChannel(__instance, HandChannels.Scale, instant: true);

    private static void NoteChannel(Node holder, HandChannels channel, bool instant = false)
    {
        // Inert unless someone is listening AND the feature is on AND this is a hand fan-out. All plain
        // volatile/bool/int reads on the disabled path.
        //
        // The fan-out scope stays FIRST and stays UNCOUNTED: these same three setters are the per-process-frame drag
        // driver, so this branch is the designed common path and counting it would bury the buckets that matter
        // under a per-frame number nobody can interpret. The remaining gates are then checked one at a time so each
        // decline lands in its own bucket.
        if (!InFanBatch)
        {
            return;
        }

        if (!Embedding.EmbeddableAnimationHintHub.Shared.HasSubscribers)
        {
            Counters.RecordEarlyOutNoSubscribers();
            return;
        }

        if (!Sts2SceneWatchRuntimeSettings.HandTweenHints)
        {
            Counters.RecordEarlyOutLeverOff();
            return;
        }

        if (EndpointResolver is null)
        {
            Counters.RecordEarlyOutNoResolver();
            return;
        }

        try
        {
            if (holder is null || !GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            var id = holder.GetInstanceId();
            if (!Pending.TryGetValue(id, out var entry))
            {
                if (Pending.Count >= PendingCap)
                {
                    return; // fail open: this holder simply keeps streaming
                }

                Pending[id] = entry = new PendingHolder(holder);
            }

            // Last instruction wins, per channel: a re-target adds the channel to the batch, an instant write takes
            // it back out (Sts2HandTweenMath). InstantPose is NOT symmetric and stays sticky — it says the holder's
            // live pose is this hint's real START, and that remains true for the rest of the batch however the
            // channel bits end up.
            entry.Touched = instant
                ? Sts2HandTweenMath.Teleport(entry.Touched, channel)
                : Sts2HandTweenMath.Retarget(entry.Touched, channel);
            entry.InstantPose |= instant;

            if (_drainScheduledFrame != _fanFrame)
            {
                _drainScheduledFrame = _fanFrame;
                // DEFERRED to this frame's idle, exactly like Sts2TweenRecorderHooks' finalize and
                // Sts2CardFlightHooks' publish: by then every setter of this batch has run (SetDefaultTargets fires
                // all three back-to-back; RefreshLayout loops the whole hand), which is what lets one hint carry all
                // three channels. The callback runs on the game main thread, the same thread as the capture walk,
                // so the watcher reads below are legal.
                Callable.From(DrainSafely).CallDeferred();
            }
        }
        catch
        {
            // Telemetry must never disrupt the game — but a swallowed throw is exactly the failure that used to be
            // indistinguishable from "declined", so leave a count behind.
            Counters.RecordPublishFailed();
        }
    }

    // The game abandoned an in-flight approach on this holder (BeginDrag hands it to NCardPlay's per-frame driver;
    // StopAnimations cancels all three loops from _ExitTree/Clear). Drop any pending publish AND collapse any open
    // suppression window, so the node resumes streaming on the next tick instead of holding an endpoint the game
    // will never reach. Fail-open by construction: the worst case of an unnecessary cancel is today's streaming.
    private static void AbandonPostfix(Node __instance)
    {
        if (WindowCanceller is not { } cancel)
        {
            return;
        }

        try
        {
            if (__instance is null || !GodotObject.IsInstanceValid(__instance))
            {
                return;
            }

            var id = __instance.GetInstanceId();
            Pending.Remove(id);
            AbandonScratch.Clear();
            AbandonScratch.Add(id);
            cancel(AbandonScratch, false);
            AbandonScratch.Clear();
        }
        catch
        {
            // Telemetry must never disrupt the game.
        }
    }

    // ---- deferred publish ------------------------------------------------------------------------

    private static void DrainSafely()
    {
        try
        {
            Drain();
        }
        catch
        {
            // Telemetry must never disrupt the game (counted, same reason as NoteChannel's catch).
            Counters.RecordPublishFailed();
        }
        finally
        {
            Pending.Clear();
        }
    }

    private static void Drain()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        CancelScratch.Clear();
        foreach (var (id, entry) in Pending)
        {
            if (!TryPublish(id, entry))
            {
                // No hint for this holder this batch — so it must NOT stay pinned by a window an EARLIER batch
                // opened. This is the mid-flight re-target's failure branch and the reason the canceller is wired.
                CancelScratch.Add(id);
            }
        }

        if (CancelScratch.Count > 0 && WindowCanceller is { } cancel)
        {
            cancel(CancelScratch, false);
        }

        CancelScratch.Clear();
    }

    private static bool TryPublish(ulong id, PendingHolder entry)
    {
        // The same three gates re-checked at DRAIN time (a batch is noted mid-frame and published at idle, so any of
        // them can have flipped in between), split so a decline here is attributable exactly like one in NoteChannel.
        if (EndpointResolver is not { } resolve)
        {
            Counters.RecordEarlyOutNoResolver();
            return false;
        }

        if (!Sts2SceneWatchRuntimeSettings.HandTweenHints)
        {
            Counters.RecordEarlyOutLeverOff();
            return false;
        }

        if (!Embedding.EmbeddableAnimationHintHub.Shared.HasSubscribers)
        {
            Counters.RecordEarlyOutNoSubscribers();
            return false;
        }

        var holder = entry.Node;
        if (holder is null || !GodotObject.IsInstanceValid(holder))
        {
            return false; // freed between the setter and idle — nothing to publish and nothing to cancel
        }

        // The FINAL targets this frame, plus the LIVE pose they are being approached from. Both are read here (not
        // in the setter) so a batch that touched a channel twice publishes the last value, and so the distances
        // measure the travel that actually REMAINS — which is what makes a mid-flight re-target's duration right.
        var targetPosition = holder.Get(TargetPositionProp).AsVector2();
        var targetAngleDegrees = holder.Get(TargetAngleProp).AsSingle();
        var targetScale = holder.Get(TargetScaleProp).AsVector2();

        var livePosition = holder.Get(PositionProp).AsVector2();
        var liveRotation = holder.Get(RotationProp).AsSingle();
        var liveScale = holder.Get(ScaleProp).AsVector2();

        double frameSeconds;
        try
        {
            frameSeconds = holder.GetProcessDeltaTime();
        }
        catch
        {
            frameSeconds = 0.0; // Sts2HandTweenMath falls back to its 60 Hz mid-band value
        }

        // Only the channels still standing after the batch's last instruction per channel (see NoteChannel): a
        // channel a teleport took back out contributes neither a distance below nor an endpoint value further down.
        // A batch left with NO channels at all (every one teleported) therefore measures no travel, publishes
        // nothing, and returns false — which cancels the holder's window. That is the graceful outcome: the
        // consumer's pin catch-up applies the streamed pose it had been holding back, and streaming resumes.
        var touched = entry.Touched;
        var hasPosition = (touched & HandChannels.Position) != 0;
        var hasAngle = (touched & HandChannels.Angle) != 0;
        var hasScale = (touched & HandChannels.Scale) != 0;

        var positionMs = hasPosition
            ? Sts2HandTweenMath.PositionSettleMs(livePosition.DistanceTo(targetPosition), frameSeconds)
            : 0.0;
        // The angle loop lerps ROTATION DEGREES and snaps at 0.1 DEGREES, so the distance is measured in degrees
        // even though the endpoint below is built in Godot's radian `rotation` property.
        var angleMs = hasAngle
            ? Sts2HandTweenMath.AngleSettleMs(
                Math.Abs(Mathf.RadToDeg(liveRotation) - targetAngleDegrees), frameSeconds)
            : 0.0;
        // The scale loop's settle test compares the X component only; so does this.
        var scaleMs = hasScale
            ? Sts2HandTweenMath.ScaleSettleMs(Math.Abs(targetScale.X - liveScale.X), frameSeconds)
            : 0.0;

        // The floor is only safe to apply to a holder NOBODY is pinned to. With a window still open a consumer has
        // already armed an earlier hint and there is no wire-level way to abort it, so a dropped batch would strand
        // the card at an endpoint the game has abandoned; publish a short CORRECTIVE hint instead. See
        // Sts2HandTweenMath.CorrectiveDurationMs for the full argument and the two defects it fixes.
        var windowOpen = HasOpenWindow is { } isOpen && isOpen(id);
        var durationMs = Sts2HandTweenMath.CorrectiveDurationMs(positionMs, angleMs, scaleMs, windowOpen);
        if (durationMs <= 0)
        {
            // Every touched channel is inside the game's own snap threshold, and nothing is pinned. Deliberately
            // UNCOUNTED: a settled batch is normal, expected behaviour, not a hint going missing.
            return false;
        }

        // Only the channels this batch actually re-targeted are overridden; ResolveTweenEndpoint reconstructs the
        // end LOCAL transform from the node's LIVE position/rotation/scale for the rest (and applies the Control
        // pivot), then maps it through the emitted parent. `Position` also GATES that branch — the resolver only
        // resolves a transform endpoint for a change that moves the node (`HasPosition`), which is exactly right
        // here: both fan-out entry points always set the position, and a scale/angle-only change must not pin a
        // translation the game may still be driving.
        //
        // INSTANT POSE — the one case that DOES declare a start. `StartTransform: null` is the right default (see the
        // publish below): the common batch re-targets a holder that is already mid-approach, and priming would
        // teleport it backwards before easing forward. But a focus is mostly a TELEPORT, not an approach — the batch
        // arrives with the pose already written (that is what the instant setters this hook watches mean) and only a
        // small residue left to travel. Replaying that as an ease is wrong twice over: the snap stops being a snap,
        // and if the card is mid-UNFOCUS the consumer is still pinned to the unfocus endpoint, so the whole refocus
        // is invisible until that pin expires. Declaring the holder's LIVE (post-snap) pose as the start makes the
        // consumer prime there transition-lessly — the same teleport the game just did — and then ease only the
        // residue. The live values are the same ones the resolver reads for its untouched-channel base, so the start
        // it computes is the holder's current global pose by construction.
        var change = new TweenTargetChange
        {
            Position = hasPosition ? targetPosition : null,
            Rotation = hasAngle ? Mathf.DegToRad(targetAngleDegrees) : null,
            Scale = hasScale ? targetScale : null,
            StartPosition = entry.InstantPose ? livePosition : null,
            StartRotation = entry.InstantPose ? liveRotation : null,
            StartScale = entry.InstantPose ? liveScale : null,
        };

        var endpoint = resolve(id, change, durationMs);
        if (endpoint?.Transform is not { } transform)
        {
            // Unresolvable: the watcher has not tracked this holder yet (a card drawn THIS frame is added to the
            // tree after the last capture pass), or the endpoint cross-check failed. Keep streaming it — the next
            // drawn card re-fans the hand and this holder is hinted then. Counted as a resolve miss: this bucket
            // rising while the early-out buckets stay flat points at TARGETING, not at wiring.
            Counters.RecordResolveNull();
            return false;
        }

        Embedding.EmbeddableAnimationHintHub.Shared.Publish(new Embedding.TweenAnimationHint(
            Scene: ResolveSceneLabel(holder),
            NodePath: ".",
            Property: HintProperty,
            To: null,
            DurationMs: durationMs,
            Trans: HintTrans,
            Ease: HintEase,
            TargetInstanceId: id,
            EndTransform: transform,
            EndOpacity: null,
            Group: null,
            // NO start transform for an ordinary batch, deliberately. A start makes the consumer PRIME the element
            // there (transition-less) before transitioning — correct for a tween that declared `.From(...)`, and
            // exactly wrong for a re-target of a holder that is already mid-approach, which is the common case here:
            // priming would teleport it backwards before easing forward. With no start, the consumer transitions from
            // wherever the previous (possibly still-running) transition has reached, which is what the game's own
            // loop does. The resolver only produces one for the INSTANT-POSE batch above, where the teleport is real.
            StartTransform: endpoint?.StartTransform,
            StartOpacity: null));

        var count = Counters.RecordEmitted();
        if (count == 1 || count == 25 || count == 500 || count == 10000)
        {
            _log?.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Hand-tween hint emitted {count} time(s); per-frame hand-layout transforms are suppressed for "
                + "the approach's lifetime.");
        }

        return true;
    }

    // Cosmetic only (the mirror joins hints on TargetInstanceId): the holder's own scene path, normalized the same
    // way Sts2TweenRecorderHooks normalizes a tween record's.
    private static string ResolveSceneLabel(Node holder)
    {
        try
        {
            var raw = holder.SceneFilePath;
            if (string.IsNullOrEmpty(raw))
            {
                return HolderSceneFallback;
            }

            var path = raw;
            if (path.StartsWith(ScenePrefix, StringComparison.Ordinal))
            {
                path = path[ScenePrefix.Length..];
            }

            if (path.EndsWith(SceneSuffix, StringComparison.Ordinal))
            {
                path = path[..^SceneSuffix.Length];
            }

            return string.IsNullOrEmpty(path) ? HolderSceneFallback : path;
        }
        catch
        {
            return HolderSceneFallback;
        }
    }

    // One holder's coalesced batch: the node plus which channels the batch's last instruction per channel left
    // standing as re-targeted (HandChannels; see NoteChannel). The target VALUES are read from the live fields at
    // drain time (see TryPublish), so nothing here can go stale within the frame.
    private sealed class PendingHolder(Node node)
    {
        public Node Node { get; } = node;

        public HandChannels Touched { get; set; } = HandChannels.None;

        // Did this batch TELEPORT a channel rather than re-target it (SetAngleInstantly / SetScaleInstantly)? Then
        // the holder's live pose is the approach's real START and must be shipped as one — see TryPublish.
        public bool InstantPose { get; set; }
    }
}
