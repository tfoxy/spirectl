using System.Collections.Concurrent;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Per-SpineSprite node inspector for the scene producer. Splits into:
//   * STATIC (probed once on add): the canonical clip address — (scene file, scene-relative node path)
//     — plus the available animation names. This is the node-addressed identity both the mirror and
//     structured views forward to fetch a rendered clip via `spine://<scene>?node=<rel>&anim=<name>`.
//     On add we also CONNECT to the node's `animation_started` signal (see below) so the live current
//     animation arrives event-driven instead of being polled.
//   * VOLATILE (read per tick): the animation the game is currently playing on track 0 and how far into
//     it (seconds). Sourced from the `animation_started` SIGNAL cache — NEVER by polling the native spine
//     state (polling get_current is an uncatchable crash; see PickDefaultAnimation). The client fetches
//     that clip and loops it on its own duration.
// Mirrors Sts2ParticleInspector (static spec on add, volatile state per tick).
internal static class Sts2SpineInspector
{
    // STATIC: canonical address + animation list. Null for non-Spine nodes or nodes that cannot be
    // canonicalized to a saved scene (so the client can't address an offline clip for them).
    public static RuntimeSceneSpineSnapshot? InspectStatic(Node node)
    {
        if (!LooksLikeSpine(node) || !TryCanonicalize(node, out var sceneResPath, out var relPath))
        {
            return null;
        }

        // Hook live anim sync for this Spine node (event-driven; safe). Failure here just drops live sync
        // for the node — it still renders its default clip — so it must not lose the static snapshot.
        AttachLiveTracking(node);

        var snapshot = new RuntimeSceneSpineSnapshot(sceneResPath, relPath, ReadAnimations(node), ReadSkeletonResPath(node));
        if (Sts2SpineDiagnostics.Current.Enabled && snapshot.Animations.Count == 0)
        {
            // #13 smoking-gun marker: a spine root with NO animations at add is one whose skeleton the game
            // injects later (`MegaSprite.SetSkeletonDataRes` from `_Ready` — chest / boss map point). Pair it with
            // the watcher's "re-probe RE-ARMED"/"re-probe SUCCEEDED" lines to see the recovery end to end.
            Sts2SpineDiagnostics.Current.Log(
                $"InspectStatic 0-animations-at-add node={node.GetInstanceId()} name={node.Name} "
                + $"scene={sceneResPath} rel={relPath ?? "<root>"} skel={snapshot.SkelResPath ?? "<unknown>"}");
        }

        return snapshot;
    }

    // The res:// path of a SpineSprite's skeleton-data resource (#8), for the client's `&skel=` fallback. Read
    // via the same defensive raw-Godot hops ReadAnimations uses; null on any failure or an in-memory resource
    // with no path (→ client omits `&skel=`, URL byte-identical). Runs only on add (InspectStatic), never a poll.
    private static string? ReadSkeletonResPath(Node spineNode)
    {
        try
        {
            var skeletonData = spineNode.Get("skeleton_data_res");
            if (skeletonData.VariantType != Variant.Type.Object
                || skeletonData.AsGodotObject() is not Resource resource)
            {
                return null;
            }

            var path = resource.ResourcePath;
            return string.IsNullOrWhiteSpace(path) || !path.StartsWith("res://", StringComparison.Ordinal)
                ? null
                : path;
        }
        catch
        {
            return null;
        }
    }

    // The Godot instance id of a SpineSprite's CURRENT skeleton-data resource, or 0 when it has none (#8/#13).
    // ONE native property Get, no MegaSpine hop — the cheap per-tick gate the watcher's late-static re-probe
    // uses to detect the RUNTIME skeleton injection (`MegaSprite.SetSkeletonDataRes`, called from _Ready by the
    // treasure chest and the boss map point) without paying for a full InspectStatic every tick.
    public static ulong ReadSkeletonDataInstanceId(Node spineNode)
    {
        try
        {
            var skeletonData = spineNode.Get("skeleton_data_res");
            if (skeletonData.VariantType != Variant.Type.Object
                || skeletonData.AsGodotObject() is not { } resource)
            {
                return 0;
            }

            return resource.GetInstanceId();
        }
        catch
        {
            return 0;
        }
    }

    // ── Live anim sync (event-driven via the `animation_started` signal) ──────────────────────────────
    //
    // The producer reacts to the SpineSprite's own `animation_started` signal instead of polling the live
    // animation state per tick. The signal fires from inside the game's spine update, so the track entry it
    // hands us is freshly started and VALID — this is exactly how the game itself reads anim state (e.g.
    // NQueenVfx / NBestiary connect this signal and read the entry's animation). We read the anim NAME off
    // the handed entry with a null-guard at every native hop, and NEVER call get_current (the crash path).
    //
    // State is keyed by Godot instance id (process-stable; ids aren't reused within a session). Two static
    // maps, written only on the main thread (signal callbacks + the watcher tick both run there) but kept
    // concurrent as belt-and-suspenders:
    //   * LiveByInstance — the last animation each tracked node started, with the engine-clock msec it began
    //     (so VOLATILE track time resets to ~0 at each anim change → a newly-started clip plays from frame 0).
    //   * Connected — the instance ids we have an active signal connection for, so cleanup touches Godot
    //     signal APIs ONLY for real spine nodes (never for the thousands of ordinary nodes the watcher evicts).
    private static readonly ConcurrentDictionary<ulong, LiveAnim> LiveByInstance = new();
    private static readonly ConcurrentDictionary<ulong, byte> Connected = new();

    // Live SKIN capture (#3). The runtime skin a spine node currently wears — the game applies it at runtime
    // (creatures like Fossil Stalker / Skulking Colony carry no authored skin in their .tscn, so an offline
    // scene instantiate for the bake would render skin-less = missing body pieces). Fed by RecordSkin from the
    // anim hooks (SetNextState postfix + animation_started) — both run on the main thread while the native
    // skeleton is alive — never a per-tick native poll. ReadLive returns it (volatile SpineSkin wire field);
    // the client appends `&skin=` ONLY when present, so an unknown skin yields today's byte-identical URL. Forget
    // drops it. Keyed by Godot instance id like LiveByInstance.
    private static readonly ConcurrentDictionary<ulong, string> SkinByInstance = new();

    // SPIRECTL_SPINE_DEBUG bookkeeping only: instance ids for which ReadLive has already logged the
    // PickDefaultAnimation fallback, so the "reached the guessed-idle fallback" marker fires ONCE per node
    // (else it repeats every tick). Populated only when Sts2SpineDiagnostics.Current.Enabled; cleared in Forget.
    private static readonly ConcurrentDictionary<ulong, byte> DefaultFallbackLogged = new();

    // ── Animation-state → owning SpineSprite map (round-8 items 10/11) ────────────────────────────────
    //
    // The game drives MOST spine nodes by calling MegaAnimationState.SetAnimation/AddAnimation DIRECTLY (the
    // bite/gaze/scratch one-shot VFX via NVfxSpine, the treasure chest, the boss map point, the merchant, the
    // rest-site character, …) — never through CreatureAnimator.SetNextState. Those calls carry the real clip
    // name AND the real loop flag, but a MegaAnimationState has no back-pointer to its sprite, so a postfix on
    // them cannot key the recording by node on its own.
    //
    // MegaSprite.TryGetAnimationState IS that back-pointer, and it is the sole choke point through which every
    // caller obtains a state (MegaSprite.GetAnimationState / IsAnimationStateReady both route through it, and
    // SpineNodeExtensions.RunWhenSpineReady polls it until the skeleton exists). Sts2SpineAnimationHooks
    // postfixes it and records the pairing here, so by the time any SetAnimation lands we already know which
    // SpineSprite node it drives. Both directions are kept so Forget can purge by node id.
    private static readonly ConcurrentDictionary<ulong, ulong> NodeByAnimState = new();
    private static readonly ConcurrentDictionary<ulong, ulong> AnimStateByNode = new();

    private const string AnimationStartedSignal = "animation_started";

    // One shared Callable bound to the static handler, reused for every node's connect/disconnect. It targets
    // a static method (null delegate target), so connecting it to a node does NOT root the node, and the same
    // instance disconnects cleanly. The handler resolves which node fired from the signal's first argument.
    private static readonly Callable AnimationStartedCallable =
        Callable.From<GodotObject, GodotObject, GodotObject>(OnAnimationStarted);

    private readonly record struct LiveAnim(string Anim, ulong StartedAtMsec, bool Looping);

    // ── Hook-fed SCHEDULED anim (survives a permanently-frozen ProcessMode.Disabled node) ─────────────
    //
    // The couch-coop headless CPU saver freezes spine nodes PERMANENTLY (ProcessMode.Disabled). That
    // silences the `animation_started` signal above (it fires from the SpineSprite's per-frame process) AND
    // halts the native track queue that auto-advances a one-shot (attack) to its pre-queued loop (idle_loop),
    // so the signal cache would latch on the last-seen anim. Instead, Sts2SpineAnimationHooks Harmony-hooks
    // CreatureAnimator.SetNextState — issued from GAME LOGIC at call time, independent of _process — and hands
    // us the sequence the game just queued (the one-shot + its pre-queued return loop) with the one-shot's
    // duration. ReadLive REPLAYS that off wall-clock so SpineCurrentAnim flips at the same moments the native
    // queue would have, with the node frozen. Preferred over the signal cache when present, so the non-frozen
    // A/B path (kill-switch off) is driven identically too — only CPU differs, never the reported anim.
    private static readonly ConcurrentDictionary<ulong, ScheduledAnim> ScheduledByInstance = new();

    // A one-shot head that hands off to a pre-queued loop, or a lone looping head (NextAnim == null).
    // CurrentDurMsec matters only for a one-shot head that has a queued NextAnim; a looping head plays until
    // the next SetNextState replaces the whole schedule (so it never needs a duration).
    private readonly record struct ScheduledAnim(
        string CurrentAnim,
        bool CurrentLooping,
        double CurrentDurMsec,
        string? NextAnim,
        bool NextLooping,
        ulong StartedAtMsec);

    // VOLATILE read (per tick): the animation currently playing on track 0 + seconds into it. Prefers the
    // hook-driven schedule (survives a frozen node), then the `animation_started` signal cache, then a stable
    // default — all NO native spine poll. Every clock advances in real time so a node re-sent each tick
    // (movement) never re-syncs to a frozen frame, and resets to ~0 at each anim change so an idle→attack (or
    // attack→idle) switch plays the new clip from the start.
    public static (string? Anim, double TrackTime, bool Looping, string? Skin, string? Mat, bool Paused) ReadLive(Node node, IReadOnlyList<string>? animations)
    {
        var nowMsec = Time.GetTicksMsec();
        var id = node.GetInstanceId();
        // Captured runtime skin (null when unknown) — independent of the anim/time/loop derivation below, rides
        // along on every read so the volatile SpineSkin wire field tracks it.
        SkinByInstance.TryGetValue(id, out var skin);
        // Shader-material discriminator (#8; null for every node with no normal_material ShaderMaterial, which is
        // almost all of them) — same deal: independent of the anim derivation, rides along on every read.
        var mat = ReadMaterialKey(node, id);

        // Hook-driven schedule (authoritative; survives a frozen node) — replay it off wall-clock (pure math in
        // Sts2SpineSchedule so it's offline-unit-testable).
        if (ScheduledByInstance.TryGetValue(id, out var sched))
        {
            // A PAUSED track (SetTimeScale(0) — the treasure chest sits frozen on frame 0 of "animation" until
            // it is opened) replays at its frozen elapsed time instead of the wall clock, so it never walks off
            // the end into its queued follow-up clip.
            var paused = PausedElapsedMsec.TryGetValue(id, out var pausedElapsed);
            var elapsedMsec = paused ? pausedElapsed : nowMsec - sched.StartedAtMsec;
            // `restingAtEnd` is the schedule's OWN pause: a lone one-shot that has run out (a Regent weapon after
            // its swing) rests on its last frame instead of free-running past the end forever. It reaches the wire
            // through the same SpinePaused field as SetTimeScale(0), which is the point — the paused-still path
            // already pins the sampled frame, so this needs nothing new on the client.
            var (anim, trackTime, looping, restingAtEnd) = Sts2SpineSchedule.ResolveScheduledAnim(
                sched.CurrentAnim, sched.CurrentLooping, sched.CurrentDurMsec,
                sched.NextAnim, sched.NextLooping, elapsedMsec, FinishedRestEnabled);
            return (anim, trackTime, looping, skin, mat, paused || restingAtEnd);
        }

        // Fallback: the `animation_started` signal cache (a spine node still processing that isn't
        // CreatureAnimator-driven). Under a permanent freeze this never populates, so the schedule above wins.
        if (LiveByInstance.TryGetValue(id, out var live))
        {
            return (live.Anim, (nowMsec - live.StartedAtMsec) / 1000.0, live.Looping, skin, mat, PausedElapsedMsec.ContainsKey(id));
        }

        // No live signal yet: GUESS a clip from the static list. The loop flag is derived from the guessed
        // NAME (Sts2SpineDefaults.DefaultAnimationLoops) rather than hardcoded true — a guess that landed on a
        // one-shot (attack/die/bite, the only thing some skeletons expose) used to be replayed forever by both
        // clients. Since the Regent round the guess is ABANDONED entirely when it lands on a one-shot (the pick
        // comes back null): a skeleton with nothing loop-shaped has no pose the game has played, and streaming
        // one anyway painted the Regent's daggers, frozen mid-swing, over the whole fight. A node leaves this
        // state the moment the game issues a real SetAnimation (hook) or fires its first animation_started, both
        // of which carry the real clip AND loop flag.
        var fallback = PickDefaultAnimation(animations);
        var fallbackLooping = DefaultLoopFixEnabled && Sts2SpineDefaults.DefaultAnimationLoops(fallback);
        if (Sts2SpineDiagnostics.Current.Enabled && DefaultFallbackLogged.TryAdd(id, 0))
        {
            Sts2SpineDiagnostics.Current.Log(
                $"PickDefaultAnimation fallback node={id} name={node.Name} picked='{fallback ?? "<null>"}' "
                + $"looping={fallbackLooping} animCount={animations?.Count ?? 0} (no live signal / schedule yet)");
        }
        return (fallback, nowMsec / 1000.0, fallbackLooping, skin, mat, PausedElapsedMsec.ContainsKey(id));
    }

    // SPIRECTL_SPINE_DEFAULT_LOOPFIX: escape hatch for the round-8 fallback change (smarter default pick + a
    // derived loop flag instead of a hardcoded `true`). Default ON; `0`/`false`/`off`/`no` restores the old
    // ordinal-first, always-looping guess for an A/B. Read once — env vars are process-stable.
    private static readonly bool DefaultLoopFixEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_DEFAULT_LOOPFIX") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // SPIRECTL_SPINE_DEFAULT_ONESHOT_BLANK: escape hatch for the Regent-weapon fix — a guess that lands on a
    // one-shot reports NO animation instead of the one-shot. Default ON; `0`/`false`/`off`/`no` restores the
    // pre-fix guess (the mid-frame of an attack, painted permanently) for an A/B. Read once, like the switch
    // above. Only consulted on the round-8 pick path: `SPIRECTL_SPINE_DEFAULT_LOOPFIX=0` selects the whole
    // pre-round-8 guess, blanking included.
    private static readonly bool DefaultOneShotBlankEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_DEFAULT_ONESHOT_BLANK") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // SPIRECTL_SPINE_FINISHED_REST: escape hatch for "a finished lone one-shot rests at its END" (see
    // Sts2SpineSchedule.ResolveScheduledAnim). Default ON; `0`/`false`/`off`/`no` goes back to reporting the
    // ever-growing wall-clock track time, i.e. the host keeps baking the clip's MID frame. Read once.
    private static readonly bool FinishedRestEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_FINISHED_REST") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // ── Shader-material discriminator (#8) ────────────────────────────────────────────────────────────
    //
    // SPIRECTL_SPINE_MATERIAL_KEY: escape hatch for the round-8 material discriminator. Default ON; `0`/`false`/
    // `off`/`no` reports a null key for every node, which restores byte-identical clip URLs/keys (the bake still
    // applies the material — only the cache discrimination is dropped). Read once — env vars are process-stable.
    private static readonly bool MaterialKeyEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_MATERIAL_KEY") ?? string.Empty)
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // Uniform NAMES per shader instance id: the uniform LIST is a property of the shader (static), only the
    // VALUES change, so the (allocating) GetShaderUniformList walk runs once per shader instead of per tick.
    private static readonly ConcurrentDictionary<ulong, string[]> UniformNamesByShader = new();

    // Last reported key + how many DISTINCT values this node has reported. Past MaxSignatureChanges the value
    // LATCHES (see Sts2SpineMaterialKey.MaxSignatureChanges): a hypothetical per-frame-animated uniform would
    // otherwise mint a fresh clip URL every tick and hammer the extraction gate forever.
    private static readonly ConcurrentDictionary<ulong, (string? Key, int Changes)> MaterialKeyByInstance = new();

    // The material signature for a spine node's `normal_material`, or null when it has none / it is not a
    // ShaderMaterial / the shader has no res:// path. Cost for the overwhelmingly common no-material case is ONE
    // native property Get per tick; a node WITH one additionally reads its (cached) uniform values. Never throws.
    private static string? ReadMaterialKey(Node node, ulong id)
    {
        if (!MaterialKeyEnabled)
        {
            return null;
        }

        string? key;
        string? shaderResPath;
        List<(string Name, Variant Value)>? liveUniforms;
        try
        {
            key = ComputeMaterialKey(node, out shaderResPath, out liveUniforms);
        }
        catch
        {
            return null; // an inaccessible material never costs us the anim read
        }

        // Latch: keep answering the last value once this node has churned through too many distinct signatures.
        var previous = MaterialKeyByInstance.TryGetValue(id, out var tracked) ? tracked : (Key: (string?)null, Changes: 0);
        if (string.Equals(previous.Key, key, StringComparison.Ordinal))
        {
            return key;
        }

        if (previous.Changes >= Sts2SpineMaterialKey.MaxSignatureChanges)
        {
            return previous.Key;
        }

        MaterialKeyByInstance[id] = (key, previous.Changes + 1);
        // R9 LIVE-UNIFORM CARRY: hand the bake the very values this signature was computed from, so the clip it
        // renders and the cache key it is stored under can never disagree (the boss map point's act tint used to be
        // read from an OFFLINE scene instance carrying the AUTHORED uniforms — see Sts2SpineLiveMaterialStore).
        // Recorded on the CHANGE path only: a repeat signature is already stored, and the latched case above answers
        // with a signature that was recorded when it was first minted.
        Sts2SpineLiveMaterials.Record(key, shaderResPath, liveUniforms);
        if (Sts2SpineDiagnostics.Current.Enabled)
        {
            Sts2SpineDiagnostics.Current.Log(
                $"spine material key node={id} name={node.Name} key={key ?? "<none>"} "
                + $"change#{previous.Changes + 1}/{Sts2SpineMaterialKey.MaxSignatureChanges}");
        }

        return key;
    }

    // Also hands back the RAW live uniform values (and the shader's res:// path) alongside the signature, so
    // ReadMaterialKey can hand them to the bake (Sts2SpineLiveMaterials) — the signature is a one-way hash, and the
    // bake needs the values themselves to render the tint the key promises. Null out-params whenever the key is null.
    private static string? ComputeMaterialKey(
        Node node,
        out string? shaderResPath,
        out List<(string Name, Variant Value)>? liveUniforms)
    {
        shaderResPath = null;
        liveUniforms = null;

        var materialVar = node.Get("normal_material");
        if (materialVar.VariantType != Variant.Type.Object
            || materialVar.AsGodotObject() is not ShaderMaterial material
            || material.Shader is not { } shader
            || string.IsNullOrWhiteSpace(shader.ResourcePath))
        {
            return null;
        }

        var names = UniformNamesByShader.GetOrAdd(shader.GetInstanceId(), _ => ReadUniformNames(shader));
        var uniforms = new List<(string Name, string Value)>(names.Length);
        var values = new List<(string Name, Variant Value)>(names.Length);
        foreach (var name in names)
        {
            var value = material.GetShaderParameter(name);
            uniforms.Add((name, DescribeVariant(value)));
            values.Add((name, value));
        }

        shaderResPath = shader.ResourcePath;
        liveUniforms = values;
        return Sts2SpineMaterialKey.Compute(shader.ResourcePath, uniforms);
    }

    private static string[] ReadUniformNames(Shader shader)
    {
        var names = new List<string>();
        try
        {
            foreach (var entry in shader.GetShaderUniformList())
            {
                if (entry.VariantType != Variant.Type.Dictionary)
                {
                    continue;
                }

                var dictionary = entry.AsGodotDictionary();
                if (dictionary.TryGetValue("name", out var nameVar) && nameVar.VariantType == Variant.Type.String)
                {
                    var name = nameVar.AsString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Unreadable uniform list — the shader path alone still discriminates shader-vs-shader.
        }

        return [.. names];
    }

    // CULTURE-STABLE text for a uniform value: the signature lands in an on-disk cache key, so a locale that
    // renders floats with a comma must not mint a different key than one that uses a dot. Godot's own
    // Color/Vector ToString goes through the current culture, hence the explicit formatting for the value types
    // a canvas_item shader actually carries; anything else falls back to the generic ToString.
    private static string DescribeVariant(Variant value) => value.VariantType switch
    {
        Variant.Type.Nil => "nil",
        Variant.Type.Bool => value.AsBool() ? "true" : "false",
        Variant.Type.Int => value.AsInt64().ToString(CultureInfo.InvariantCulture),
        Variant.Type.Float => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        Variant.Type.Color => FormatFloats(value.AsColor().R, value.AsColor().G, value.AsColor().B, value.AsColor().A),
        Variant.Type.Vector2 => FormatFloats(value.AsVector2().X, value.AsVector2().Y),
        Variant.Type.Vector3 => FormatFloats(value.AsVector3().X, value.AsVector3().Y, value.AsVector3().Z),
        Variant.Type.Vector4 => FormatFloats(value.AsVector4().X, value.AsVector4().Y, value.AsVector4().Z, value.AsVector4().W),
        Variant.Type.Object => value.AsGodotObject() is Resource resource && !string.IsNullOrWhiteSpace(resource.ResourcePath)
            ? resource.ResourcePath
            : "obj",
        _ => value.ToString(),
    };

    private static string FormatFloats(params float[] parts)
        => string.Join(',', parts.Select(part => part.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The same culture-stable uniform-value text the signature is hashed from, exposed for the bake's
    /// SPIRECTL_SPINE_DEBUG live-vs-offline A/B dump (Sts2SpineLiveMaterials.DescribeLive + the render lane).
    /// </summary>
    internal static string DescribeUniformValue(Variant value) => DescribeVariant(value);

    // Record which SpineSprite node a MegaAnimationState belongs to (see the NodeByAnimState comment above).
    // Called from the MegaSprite.TryGetAnimationState postfix on the game main thread. Idempotent; never throws.
    public static void RecordAnimationStateOwner(ulong animStateInstanceId, ulong nodeInstanceId)
    {
        if (animStateInstanceId == 0 || nodeInstanceId == 0)
        {
            return;
        }

        NodeByAnimState[animStateInstanceId] = nodeInstanceId;
        AnimStateByNode[nodeInstanceId] = animStateInstanceId;
    }

    /// <summary>The SpineSprite node instance id a MegaAnimationState drives, or 0 when unknown.</summary>
    public static ulong ResolveAnimationStateOwner(ulong animStateInstanceId)
        => NodeByAnimState.TryGetValue(animStateInstanceId, out var nodeId) ? nodeId : 0;

    // Record the runtime skin currently applied to a spine node (#3). Called by the anim hooks
    // (Sts2SpineAnimationHooks.SetNextStatePostfix + OnAnimationStarted) — both run on the game main thread while
    // the native skeleton is alive. Empty/whitespace CLEARS the entry (null skin = unknown → the client omits
    // `&skin=` and its URL is byte-identical to today). Idempotent; never throws.
    public static void RecordSkin(ulong instanceId, string? skin)
    {
        if (string.IsNullOrWhiteSpace(skin))
        {
            SkinByInstance.TryRemove(instanceId, out _);
            return;
        }

        SkinByInstance[instanceId] = skin;
    }

    // Read the runtime skin NAME off a live SpineSprite node's `skin` property (the native SpineSprite exposes
    // it in the Godot property table; the game sets it at runtime and the offline preview path sets it too — see
    // TryApplySpineSkinAndPose / TrySetDynamicValue "skin"). Defensive at every native hop and wrapped in
    // try/catch. MUST be called only from a context where the native skeleton is alive (the anim signal handler /
    // SetNextState postfix, main thread) — never a per-tick poll (the get_current crash class). Returns null when
    // the node exposes no skin, so the caller records "unknown" and the client keeps its default URL.
    public static string? TryReadCurrentSkinName(Node node)
    {
        try
        {
            var skinVar = node.Get("skin");
            var name = skinVar.VariantType switch
            {
                Variant.Type.String => skinVar.AsString(),
                Variant.Type.StringName => skinVar.AsStringName().ToString(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // ── Paused playback (MegaAnimationState.SetTimeScale, round-8 item 13) ────────────────────────────
    //
    // The treasure chest is set up as `SetAnimation("animation") + AddAnimation("shine_fade") +
    // SetTimeScale(0)`: the game FREEZES it on the first frame of "animation" (a closed chest) and only resumes
    // (SetTimeScale(1)) when the player opens it. The schedule replay below advances off WALL CLOCK, so without
    // knowing about the freeze it walked "animation" to its end and handed off to the queued "shine_fade" —
    // both clients then showed an OPEN, glowing chest sitting in a room whose chest is visibly shut.
    //
    // Keyed by node instance id: the elapsed schedule time at the moment the track was paused. Resuming shifts
    // the schedule's StartedAtMsec forward by the paused duration, so playback continues from where it stopped
    // instead of jumping. Absent = running (every other spine node), so this costs one dictionary miss per read.
    private static readonly ConcurrentDictionary<ulong, ulong> PausedElapsedMsec = new();

    /// <summary>
    /// Record the playback time scale the game just set on a spine node's track (0 = paused). Called from the
    /// MegaAnimationState.SetTimeScale postfix on the game main thread. Idempotent; never throws.
    /// </summary>
    public static void RecordTimeScale(ulong instanceId, double scale)
    {
        var nowMsec = Time.GetTicksMsec();
        if (scale == 0)
        {
            if (PausedElapsedMsec.ContainsKey(instanceId))
            {
                return; // already paused — keep the FIRST pause point (a re-issued SetTimeScale(0) is a no-op)
            }

            var elapsed = ScheduledByInstance.TryGetValue(instanceId, out var running)
                ? nowMsec - running.StartedAtMsec
                : 0UL;
            PausedElapsedMsec[instanceId] = elapsed;
            return;
        }

        if (!PausedElapsedMsec.TryRemove(instanceId, out var pausedElapsed))
        {
            return; // was not paused
        }

        // Resume where it stopped: re-anchor the schedule so `now - StartedAtMsec` == the paused elapsed.
        if (ScheduledByInstance.TryGetValue(instanceId, out var scheduled))
        {
            ScheduledByInstance[instanceId] = scheduled with { StartedAtMsec = nowMsec - pausedElapsed };
        }
    }

    // Record the animation sequence the game just issued for a spine node (called by Sts2SpineAnimationHooks
    // from the CreatureAnimator.SetNextState postfix). Survives a frozen node because it is driven by the
    // game's own call, not the node's process. `currentDurMsec` is the one-shot head's duration (0 when the
    // head is looping — a loop has no fixed end here). `startedAtMsec` is Time.GetTicksMsec() at the call.
    public static void RecordScheduledAnimation(
        ulong instanceId,
        string currentAnim,
        bool currentLooping,
        double currentDurMsec,
        string? nextAnim,
        bool nextLooping,
        ulong startedAtMsec)
    {
        if (string.IsNullOrWhiteSpace(currentAnim))
        {
            return;
        }

        ScheduledByInstance[instanceId] =
            new ScheduledAnim(currentAnim, currentLooping, currentDurMsec, nextAnim, nextLooping, startedAtMsec);
    }

    // Record a DIRECT MegaAnimationState.SetAnimation on track 0 (the path most spine nodes are driven by:
    // NVfxSpine one-shots, the treasure chest, the merchant, the rest-site character, the defeat pose). Same
    // effect as RecordScheduledAnimation with no queued return — a new set_animation clears the track's queue —
    // except for the RE-ASSERT guard: re-issuing the SAME looping clip must keep its free-running clock, or the
    // client re-seeds frame 0 on every call and the loop visibly restarts (the same guard OnAnimationStarted
    // applies to a re-fired looping animation_started). A one-shot re-issued with the same name DOES reseed, so
    // a second attack replays from the start.
    public static void RecordDirectAnimation(
        ulong instanceId,
        string anim,
        bool looping,
        double currentDurMsec,
        ulong startedAtMsec)
    {
        if (string.IsNullOrWhiteSpace(anim))
        {
            return;
        }

        if (looping
            && ScheduledByInstance.TryGetValue(instanceId, out var scheduled)
            && scheduled.CurrentLooping
            && scheduled.NextAnim is null
            && string.Equals(scheduled.CurrentAnim, anim, StringComparison.Ordinal))
        {
            return;
        }

        RecordScheduledAnimation(instanceId, anim, looping, currentDurMsec, nextAnim: null, nextLooping: false, startedAtMsec);
    }

    // Record a clip the game QUEUED behind whatever is already playing (MegaAnimationState.AddAnimation on
    // track 0, hooked by Sts2SpineAnimationHooks). Three cases:
    //   * no schedule yet (the game queues onto an empty track — NBossMapPoint does exactly this in _Ready):
    //     the queued clip IS what will play, so it becomes the head;
    //   * a ONE-SHOT head with no return yet: this is the return the head hands off to (NTreasureRoom queues
    //     "shine_fade" behind "animation"; CreatureAnimator queues the idle behind an attack);
    //   * anything else (a looping head — it plays until the next SetAnimation replaces it — or a head that
    //     already has a return): left alone, so a 3-deep queue keeps its FIRST return.
    // Never throws; a blank name is ignored.
    public static void RecordQueuedAnimation(ulong instanceId, string nextAnim, bool nextLooping)
    {
        if (string.IsNullOrWhiteSpace(nextAnim))
        {
            return;
        }

        if (!ScheduledByInstance.TryGetValue(instanceId, out var scheduled))
        {
            ScheduledByInstance[instanceId] =
                new ScheduledAnim(nextAnim, nextLooping, 0, null, false, Time.GetTicksMsec());
            return;
        }

        if (scheduled.CurrentLooping || scheduled.NextAnim is not null)
        {
            return;
        }

        ScheduledByInstance[instanceId] = scheduled with { NextAnim = nextAnim, NextLooping = nextLooping };
    }

    // Drop a node's live-anim tracking when the watcher evicts it (vanished/freed). Early-returns for any node
    // we never connected (the common case), so it never calls Godot signal APIs on non-spine nodes. A freed
    // node's connections are already gone, so we only disconnect while the node is still valid.
    public static void Forget(ulong instanceId, Node? node)
    {
        LiveByInstance.TryRemove(instanceId, out _);
        ScheduledByInstance.TryRemove(instanceId, out _);
        SkinByInstance.TryRemove(instanceId, out _);
        PausedElapsedMsec.TryRemove(instanceId, out _);
        MaterialKeyByInstance.TryRemove(instanceId, out _);
        DefaultFallbackLogged.TryRemove(instanceId, out _);
        if (AnimStateByNode.TryRemove(instanceId, out var animStateId))
        {
            NodeByAnimState.TryRemove(animStateId, out _);
        }

        if (!Connected.TryRemove(instanceId, out _))
        {
            return;
        }

        if (node is not null && GodotObject.IsInstanceValid(node))
        {
            node.Disconnect(AnimationStartedSignal, AnimationStartedCallable);
        }
    }

    private static void AttachLiveTracking(Node node)
    {
        var id = node.GetInstanceId();
        if (!Connected.TryAdd(id, 0))
        {
            return; // already tracking (a reused scene can re-inspect the same node)
        }

        try
        {
            node.Connect(AnimationStartedSignal, AnimationStartedCallable);
        }
        catch
        {
            // Connect failed — drop the claim so a later re-inspect can retry; the node still renders its
            // default clip, just without live anim sync.
            Connected.TryRemove(id, out _);
        }
    }

    // SIGNAL HANDLER for `animation_started(sprite, animationState, trackEntry)`. Fires SYNCHRONOUSLY on the main
    // thread from the game's spine update, so the animation state is alive + valid here. We read TRACK 0's current
    // animation (the body anim) — NOT the fired `trackEntry` — so an overlay-track event (embers/glow loops on a
    // higher track) does NOT change the reported body anim (which would flap the clip). This is the game's own
    // in-handler pattern (NQueenVfx/NBestiary read get_current(0) in this exact handler). Reading get_current HERE
    // is safe; the crash was POLLING it every TICK at arbitrary times (see PickDefaultAnimation), never in-handler.
    private static void OnAnimationStarted(GodotObject sprite, GodotObject animationState, GodotObject trackEntry)
    {
        if (sprite is not Node node || animationState is null)
        {
            return;
        }

        // Free owner-map entry: this signal hands us the sprite AND its animation state together, so it is a
        // second, independent way to learn the pairing the SetAnimation/AddAnimation postfixes key on (the
        // primary is the MegaSprite.TryGetAnimationState postfix). Costs two GetInstanceId reads.
        RecordAnimationStateOwner(animationState.GetInstanceId(), node.GetInstanceId());

        var current = animationState.Call("get_current", 0);
        if (current.VariantType == Variant.Type.Nil)
        {
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log($"OnAnimationStarted rejected node={node.GetInstanceId()} reason=track0-nil");
            }
            return; // track 0 has no current entry (only higher tracks active) — keep the last body anim
        }

        var entry = current.AsGodotObject();
        if (entry is null)
        {
            return;
        }

        var animationVar = entry.Call("get_animation");
        if (animationVar.VariantType == Variant.Type.Nil)
        {
            return;
        }

        var animation = animationVar.AsGodotObject();
        if (animation is null)
        {
            return;
        }

        var nameVar = animation.Call("get_name");
        if (nameVar.VariantType != Variant.Type.String)
        {
            return;
        }

        var name = nameVar.AsString();
        // Ignore empty names and Spine's "_ignore/" helper poses (rigging/cloth/glow sub-tracks) — they aren't
        // standalone playable clips. Keeping the previous anim is better than switching the mirror to a non-clip.
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("_ignore", StringComparison.OrdinalIgnoreCase))
        {
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log($"OnAnimationStarted rejected node={node.GetInstanceId()} reason=ignore-name name='{name}'");
            }
            return;
        }

        // The track entry's loop flag (the game's playback choice) tells the client whether to LOOP the clip or
        // FREEZE on its last frame when it runs past its end. Without it the client loops EVERYTHING, so a
        // one-shot anim (attack/cast/hurt/die — Regent's weapons only ever play "attack") replays forever =
        // the constant flicker. Read defensively: a binding without get_loop just leaves looping=false.
        var looping = false;
        var loopVar = entry.Call("get_loop");
        if (loopVar.VariantType == Variant.Type.Bool)
        {
            looping = loopVar.AsBool();
        }

        // (Re)seed the clock when the body anim changes. Additionally, skip the reseed ONLY for a re-asserted
        // LOOPING anim (same name): a looping anim that re-fires animation_started must keep its free-running
        // start or it rewinds to ~0 and the clip RESTARTS every tick (the flicker). A NON-looping anim re-firing
        // for the same name (e.g. a second attack) DOES reseed so the client replays it from frame 0.
        var id = node.GetInstanceId();
        if (looping
            && LiveByInstance.TryGetValue(id, out var prev)
            && string.Equals(prev.Anim, name, StringComparison.Ordinal))
        {
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log($"OnAnimationStarted rejected node={id} reason=reasserted-loop name='{name}'");
            }
            return;
        }

        LiveByInstance[id] = new LiveAnim(name, Time.GetTicksMsec(), looping);
        // Capture the runtime skin in the same alive-native window (#3) — this signal fires from inside the
        // game's spine update, so the node's skin property is valid here.
        RecordSkin(id, TryReadCurrentSkinName(node));
        if (Sts2SpineDiagnostics.Current.Enabled)
        {
            SkinByInstance.TryGetValue(id, out var capturedSkin);
            Sts2SpineDiagnostics.Current.Log($"OnAnimationStarted accepted node={id} name='{name}' looping={looping} skin='{capturedSkin ?? "<none>"}'");
        }
    }

    // A STABLE default animation to present for a Spine node, picked from the static animation list WITHOUT
    // touching the live (crash-prone) animation state. Prefers common idle/loop names. Returns null when the node
    // exposes no animations — or (SPIRECTL_SPINE_DEFAULT_ONESHOT_BLANK) when every candidate is a one-shot, which
    // means the game has to play a clip before the node has any pose to show. This is the FALLBACK used by
    // ReadLive until the node fires its first `animation_started` signal (and for nodes that never do).
    //
    // WHY NOT POLL the live current animation per tick: calling MegaAnimationState.get_current(0) on every tick
    // is an UNCATCHABLE crash. A SpineSprite's native MegaSpine state is uninitialised right after the node is
    // added (the runtime skeleton/track 0 is built a frame or more later) and is torn down during scene
    // transitions — both while the Godot node is still IsInstanceValid + IsInsideTree with a non-null
    // GetSkeleton(). In those windows get_current(0) dereferences freed/null native memory, and in the modded
    // game a native deref/NRE is an UNCATCHABLE SIGSEGV (the CLR's SIGSEGV→exception handler chain is broken,
    // so try/catch does NOT save us — the process just dies). This crashed the headless at the Neow "Continue"
    // → map transition (captured via core dump: ReadVolatile → get_current → MegaSpineBinding.Call → segfault),
    // and no external precondition (node valid/in-tree, skeleton present) reliably predicts it. Live anim sync
    // is therefore EVENT-DRIVEN (the `animation_started` signal above), which fires only when the native state
    // is alive — never a per-tick poll.
    public static string? PickDefaultAnimation(IReadOnlyList<string>? animations)
        => DefaultLoopFixEnabled
            ? Sts2SpineDefaults.PickDefaultAnimation(animations, DefaultOneShotBlankEnabled)
            : LegacyPickDefaultAnimation(animations);

    // Pre-round-8 guess, kept behind SPIRECTL_SPINE_DEFAULT_LOOPFIX=0 for an A/B: exact well-known names, else
    // animations[0]. ReadAnimations sorts ORDINALLY, so `animations[0]` picked "attack" for every creature
    // whose skeleton has no `idle*`/`animation`/`loop*` clip (sneaky_gremlin, whose idle is "awake_loop") —
    // reported as looping, which is why those enemies replayed their attack forever.
    private static string? LegacyPickDefaultAnimation(IReadOnlyList<string>? animations)
    {
        if (animations is null || animations.Count == 0)
        {
            return null;
        }

        foreach (var preferred in LegacyPreferredDefaultAnimations)
        {
            foreach (var candidate in animations)
            {
                if (string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return animations[0];
    }

    private static readonly string[] LegacyPreferredDefaultAnimations =
        ["idle_loop", "idle", "idle_anim", "animation", "loop", "loop_anim"];

    private static bool LooksLikeSpine(Node node)
        => node.GetClass().Contains("SpineSprite", StringComparison.OrdinalIgnoreCase)
            || node.GetType().Name.Contains("SpineSprite", StringComparison.OrdinalIgnoreCase);

    // Walk up to the nearest ancestor that is the root of a saved scene (non-empty SceneFilePath): the
    // smallest reusable scene enclosing this SpineSprite. Its file + the relative path to the node is the
    // canonical, reuse-stable identity (the same sub-scene mounted elsewhere resolves to the same key).
    private static bool TryCanonicalize(Node node, out string sceneResPath, out string? relPath)
    {
        sceneResPath = string.Empty;
        relPath = null;

        Node? sceneRoot = null;
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (!string.IsNullOrWhiteSpace(current.SceneFilePath))
            {
                sceneRoot = current;
                break;
            }
        }

        if (sceneRoot is null)
        {
            return false;
        }

        sceneResPath = sceneRoot.SceneFilePath;
        relPath = ReferenceEquals(sceneRoot, node) ? null : sceneRoot.GetPathTo(node).ToString();
        return true;
    }

    private static IReadOnlyList<string> ReadAnimations(Node spineNode)
    {
        try
        {
            var skeletonData = spineNode.Get("skeleton_data_res");
            return ReadAnimationNames(skeletonData);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ReadAnimationNames(Variant skeletonData)
    {
        if (skeletonData.VariantType == Variant.Type.Nil || skeletonData.AsGodotObject() is null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var name in new MegaSkeletonDataResource(skeletonData).GetAnimationNames())
        {
            // Skip Spine's "_ignore/" helper poses (rigging/setup, cloth/glow sub-track loops) — they
            // aren't standalone playable clips, matching the catalog enumeration.
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("_ignore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    internal static bool LooksLikeSpineNodeType(string? typeName)
        => !string.IsNullOrWhiteSpace(typeName)
            && typeName.Contains("SpineSprite", StringComparison.OrdinalIgnoreCase);
}
