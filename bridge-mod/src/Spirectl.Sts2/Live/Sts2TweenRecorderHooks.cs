using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Dev-time recorder for STS2's imperative Godot <see cref="Tween"/> animations. Its animations are built
/// at run time — a <c>Tween</c> is created, property steps are appended to it, and it is then discarded —
/// and Godot's Tweener definitions are write-only (no getters), so the ONLY point at which a tween's shape
/// can be captured is creation time. We Harmony-postfix the tween-building surface, group the resulting
/// tweeners per <see cref="Tween"/> instance, attribute a trigger (the game method that built the tween),
/// and emit one JSONL record per completed tween. A later wave folds these records into generated
/// presentation-catalog <c>transitions</c> entries.
///
/// <para>This is the FIRST Harmony patch of a GodotSharp engine binding in this repo (every other hook
/// patches a MegaCrit game method). Engine-binding methods are thin NativeCalls stubs, so JIT inlining at
/// the call site could theoretically bypass the patch. A launch-time probe (a milestone counter logged to
/// <c>bridge.tween.probe</c>) verifies the postfix actually fires before any capture is trusted.</para>
///
/// <para>Discipline: hooks are installed unconditionally but INERT unless recording is enabled (a single
/// volatile-bool check with zero allocation on the disabled path). Recording is enabled via the
/// <c>SPIRECTL_BRIDGE_TWEEN_RECORD</c> env var (matching <see cref="Sts2AssetLoadGuardOptions"/>). When
/// enabled, completed records are handed to a bounded channel and drained by a background writer with
/// drop-on-full semantics (mirroring <c>Sts2VfxSpawnEventHooks</c>): telemetry must never block or
/// disrupt the game.</para>
/// </summary>
internal static class Sts2TweenRecorderHooks
{
    public const string EnvVar = "SPIRECTL_BRIDGE_TWEEN_RECORD";
    public const string OutputDirEnvVar = "SPIRECTL_BRIDGE_TWEEN_RECORD_DIR";

    // Independent, programmatically-toggled "lite hint" mode (env is a launch-time convenience for the
    // perf probe only; the real toggle is a facade subscription — see SetHintsEnabled). Enabled it does
    // strictly LESS than recording: it SKIPS the StackTrace trigger capture and all non-timing postfix
    // work, keeping only TweenProperty capture, address resolution, and per-tweener/tween-level Trans/Ease.
    public const string HintsEnvVar = "SPIRECTL_BRIDGE_TWEEN_HINTS";
    private const string LogTarget = "bridge.tween";
    private const string ProbeTarget = "bridge.tween.probe";
    private const string ScenePrefix = "res://scenes/";
    private const string SceneSuffix = ".tscn";
    private const string GameNamespaceRoot = "MegaCrit";

    // Compiler-generated async/coroutine state machine: NFoo+<AnimateIn>d__69.MoveNext -> (NFoo, AnimateIn).
    private static readonly Regex StateMachineName = new(@"^<(\w+)>d__\d+$", RegexOptions.Compiled);

    private static readonly object Sync = new();
    private static bool _installed;
    private static volatile bool _enabled;
    private static volatile bool _hintsEnabled;
    private static ILogStream? _log;

    // Part C: an optional resolver (registered by Sts2RuntimeFactory, backed by the scene watcher) that turns a
    // captured tween's target change into an END endpoint in the watcher's streamed space, so the mirror can
    // REPLAY the tween instead of only pre-arming timing. Null when no watcher is wired (recording-only / tests);
    // then hints stay timing-only (endpoint fields null) and consumers fall back to the streamed transforms.
    internal static Func<ulong, TweenTargetChange, double, TweenEndpoint?>? EndpointResolver { get => Sts2SceneAnimationCallbacks.TweenEndpointResolver; set => Sts2SceneAnimationCallbacks.TweenEndpointResolver = value; }

    // Fix 1: an optional canceller (registered by Sts2RuntimeFactory, backed by the scene watcher) invoked when a
    // recorded tween is KILLED/STOPPED. It tells the watcher to drop any open streaming-suppression window on the
    // tween's target nodes and force one settle re-emit, so a superseded/interrupted tween (e.g. the shared main-menu
    // focus reticle re-anchored onto a new button) stops freezing those nodes' live stream. Null in tests / when no
    // watcher is wired — then a kill only prevents the (not-yet-emitted) hint via record.Cancelled.
    // The bool is WS-REST's `forceOpacityResync`: when true the watcher forces a settled opacity re-emit even when
    // the node has NO open opacity window (a killed fade-IN never opened one), so a hint-less re-show still un-sticks.
    internal static Action<IReadOnlyCollection<ulong>, bool>? WindowCanceller { get => Sts2SceneAnimationCallbacks.TweenWindowCanceller; set => Sts2SceneAnimationCallbacks.TweenWindowCanceller = value; }

    // One record per live Tween; per-tweener step records so From/SetTrans/... postfixes attribute back.
    private static readonly ConditionalWeakTable<Tween, TweenRecord> TweenRecords = new();
    private static readonly ConditionalWeakTable<PropertyTweener, StepRecord> StepRecords = new();

    // Probe counters (always cheap; only touched on the enabled path).
    private static long _propertyCalls;
    private static long _recordsFlushed;
    private static long _hintsEmitted;

    // Bounded, drop-on-full JSONL sink drained by a single background writer thread.
    private static Channel<string>? _sink;
    private static string? _outputPath;

    public static bool IsEnabled => _enabled;

    public static bool IsHintsEnabled => _hintsEnabled;

    // Either mode drives the shared hook bodies (TweenProperty capture, address, Trans/Ease).
    private static bool Active => _enabled || _hintsEnabled;

    public static long PropertyCallCount => Interlocked.Read(ref _propertyCalls);

    public static long FlushedRecordCount => Interlocked.Read(ref _recordsFlushed);

    public static long HintEmitCount => Interlocked.Read(ref _hintsEmitted);

    public static string? OutputPath => _outputPath;

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

            var enable = ParseTruthy(System.Environment.GetEnvironmentVariable(EnvVar));

            // The `_installed` latch above is PER-ASSEMBLY-IDENTITY, and an embedder ships a second copy of this
            // runtime under its own assembly name, so `SPIRECTL_BRIDGE_TWEEN_RECORD=1` arms TWO JSONL writers: the
            // output path is a UTC timestamp to the second, so the two copies name the same file and both open it
            // `append: true`, interleaving two full recordings into one stream. The process-global claim gives the
            // recording to exactly one copy (see Sts2OneShotArmClaim); it is read only when the env var is truthy,
            // so an unrecorded host still pays exactly the one environment read it paid before.
            //
            // SCOPE: the RECORDING only. The Harmony hooks below stay per-copy on purpose — each copy publishes
            // hints into its OWN EmbeddableAnimationHintHub.Shared (that static is per-assembly-identity too,
            // despite its "process-wide" doc), so a copy that stood down here would deliver NO hints to the
            // embedder subscribed through it. Duplicate postfix work is a cost; a claim on Install would be a
            // product break.
            if (enable
                && !Sts2OneShotArmClaim.TryClaim("tween-record", Sts2OneShotArmClaim.SelfOwner, out var recordOwner))
            {
                logStream.Write(
                    BridgeLogLevel.Info,
                    LogTarget,
                    $"Tween JSONL recording ({EnvVar}) already armed by {recordOwner}; standing down.");
                enable = false;
            }

            try
            {
                var harmony = new Harmony("spirectl.tween-recorder");
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.TweenProperty), nameof(TweenPropertyPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.TweenInterval), nameof(TweenIntervalPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.TweenCallback), nameof(TweenCallbackPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.TweenMethod), nameof(TweenMethodPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.SetParallel), nameof(SetParallelPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.SetLoops), nameof(SetLoopsPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.Parallel), nameof(ParallelPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.Chain), nameof(ChainPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.SetTrans), nameof(TweenSetTransPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.SetEase), nameof(TweenSetEasePostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.From), nameof(FromPostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.FromCurrent), nameof(FromCurrentPostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.SetTrans), nameof(SetTransPostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.SetEase), nameof(SetEasePostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.SetDelay), nameof(SetDelayPostfix));
                PatchPostfix(harmony, typeof(PropertyTweener), nameof(PropertyTweener.AsRelative), nameof(AsRelativePostfix));
                // Fix 1: a killed/stopped tween must stop polluting the mirror (drop its hint, unfreeze its nodes).
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.Kill), nameof(KillPostfix));
                PatchPostfix(harmony, typeof(Tween), nameof(Tween.Stop), nameof(KillPostfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    LogTarget,
                    $"Skipping tween-recorder hooks because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;

            if (enable)
            {
                Enable();
            }

            // Lite hint mode is toggled by embedding hosts subscribing to the hint hub: the hub raises
            // ActiveChanged(true) on the first subscriber and ActiveChanged(false) on the last. The env var
            // is a launch-time convenience for the perf probe.
            Embedding.EmbeddableAnimationHintHub.Shared.ActiveChanged += SetHintsEnabled;
            if (Embedding.EmbeddableAnimationHintHub.Shared.HasSubscribers
                || ParseTruthy(System.Environment.GetEnvironmentVariable(HintsEnvVar)))
            {
                SetHintsEnabled(true);
            }

            logStream.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Installed tween-recorder hooks (18 postfixes). recording={_enabled} hints={_hintsEnabled}. "
                + $"Enable recording at launch with {EnvVar}=1; enable hints with {HintsEnvVar}=1.");
        }
    }

    /// <summary>
    /// Toggle the lite "animation hint" producer path. Called by <see cref="Embedding.EmbeddableAnimationHintHub"/>
    /// on the first subscriber (true) and last unsubscribe (false); also from the launch-time env probe. A plain
    /// volatile-bool flip — zero allocation, safe to call before/independently of <see cref="Install"/>.
    /// </summary>
    public static void SetHintsEnabled(bool value)
    {
        if (_hintsEnabled == value)
        {
            return;
        }

        _hintsEnabled = value;
        _log?.Write(
            BridgeLogLevel.Info,
            LogTarget,
            $"Tween animation-hints {(value ? "ENABLED" : "DISABLED")}. "
            + $"hintsEmitted={HintEmitCount} propertyCalls={PropertyCallCount}.");
    }

    public static void Enable()
    {
        lock (Sync)
        {
            if (_enabled)
            {
                return;
            }

            _sink = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity: 4096)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            _outputPath = ResolveOutputPath();
            var sink = _sink;
            var path = _outputPath;
            var writer = new Thread(() => DrainWriter(sink.Reader, path))
            {
                IsBackground = true,
                Name = "spirectl-tween-writer",
            };
            writer.Start();
            _enabled = true;
            _log?.Write(BridgeLogLevel.Info, LogTarget, $"Tween recording ENABLED. Writing JSONL to {path}.");
        }
    }

    public static void Disable()
    {
        lock (Sync)
        {
            if (!_enabled)
            {
                return;
            }

            _enabled = false;
            _sink?.Writer.TryComplete();
            _log?.Write(
                BridgeLogLevel.Info,
                LogTarget,
                $"Tween recording DISABLED. propertyCalls={PropertyCallCount} recordsFlushed={FlushedRecordCount} path={_outputPath}.");
        }
    }

    private static void PatchPostfix(Harmony harmony, Type declaring, string method, string postfix)
    {
        var target = AccessTools.Method(declaring, method)
            ?? throw new MissingMethodException(declaring.FullName, method);
        var patch = typeof(Sts2TweenRecorderHooks).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(Sts2TweenRecorderHooks), postfix);
        harmony.Patch(target, postfix: new HarmonyMethod(patch));
    }

    // ---- Tween.* postfixes ------------------------------------------------------------------------

    private static void TweenPropertyPostfix(
        Tween __instance,
        PropertyTweener __result,
        GodotObject @object,
        NodePath property,
        Variant finalVal,
        double duration)
    {
        if (!Active)
        {
            return;
        }

        var count = Interlocked.Increment(ref _propertyCalls);
        LogProbeMilestone(count);

        try
        {
            var record = GetOrCreateRecord(__instance);
            var step = record.BeginStep("property");
            step.Property = property?.ToString();
            step.To = VariantToJson(finalVal);
            // Keep the raw Variant too, so Part C endpoint resolution reads the exact value (Vector2/float) without
            // a JSON round-trip. Value-typed for the transform props we resolve, so it survives to the deferred emit.
            step.ToRaw = finalVal;
            step.HasToRaw = true;
            step.DurationMs = duration * 1000.0;
            // Implicit tween START: sample the property's CURRENT value NOW (registration time). The tween has not
            // stepped yet — the node still sits at its pre-tween value — so this is the true animation start. Explicit
            // `.From(...)` (FromPostfix, runs AFTER this) still wins via HasFromRaw. Best-effort; gated by the kill-switch.
            CaptureImplicitStart(step, @object, property);
            // Per-STEP target. A single Godot tween can animate DIFFERENT nodes in parallel (e.g. an event option
            // scales its ROOT while fading a highlight-glow CHILD's modulate). The endpoint hint for each step must be
            // attributed to that step's own node, or a child's fade endpoint would be fanned across the wrong subtree.
            step.TargetInstanceId = @object?.GetInstanceId() ?? 0;
            record.CaptureAddress(@object);
            if (__result is not null)
            {
                StepRecords.AddOrUpdate(__result, step);
            }
        }
        catch
        {
            // Telemetry must never disrupt the game.
        }
    }

    // Sample a property step's IMPLICIT start: the target's CURRENT value at TweenProperty registration time (pre-tween).
    // `GetIndexed` resolves sub-paths ("position:y", "modulate:a") to the same shape as the step's `to`, so MapTransform/
    // MapOpacity read it identically to a declared `.From(...)`. Gated by the kill-switch (skips the read entirely when
    // off) and best-effort (any read failure just leaves the step start-less → the client falls back to today's behavior).
    private static void CaptureImplicitStart(StepRecord step, GodotObject? @object, NodePath? property)
    {
        if (!Sts2SceneWatchRuntimeSettings.TweenImplicitStart || @object is null || property is null)
        {
            return;
        }

        try
        {
            step.ImplicitFromRaw = @object.GetIndexed(property);
            step.HasImplicitFromRaw = true;
        }
        catch
        {
            // A property with no getter / an exotic type: skip the implicit start, never disturb the game.
        }
    }

    private static void TweenIntervalPostfix(Tween __instance, double time)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var record = GetOrCreateRecord(__instance);
            var step = record.BeginStep("interval");
            step.DurationMs = time * 1000.0;
        }
        catch
        {
        }
    }

    private static void TweenCallbackPostfix(Tween __instance)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var record = GetOrCreateRecord(__instance);
            record.BeginStep("callback");
        }
        catch
        {
        }
    }

    private static void TweenMethodPostfix(Tween __instance, double duration)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var record = GetOrCreateRecord(__instance);
            var step = record.BeginStep("method");
            step.DurationMs = duration * 1000.0;
        }
        catch
        {
        }
    }

    private static void SetParallelPostfix(Tween __instance, bool parallel)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var record = GetOrCreateRecord(__instance);
            record.DefaultParallel = parallel;
            record.PendingParallel = parallel;
        }
        catch
        {
        }
    }

    private static void SetLoopsPostfix(Tween __instance, int loops)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            GetOrCreateRecord(__instance).Loops = loops;
        }
        catch
        {
        }
    }

    private static void ParallelPostfix(Tween __instance)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            GetOrCreateRecord(__instance).PendingParallel = true;
        }
        catch
        {
        }
    }

    private static void ChainPostfix(Tween __instance)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            GetOrCreateRecord(__instance).PendingParallel = false;
        }
        catch
        {
        }
    }

    // Tween-wide defaults (Tween.SetTrans/SetEase): apply to every Tweener appended to this Tween that
    // doesn't set its own Trans/Ease via PropertyTweener.SetTrans/SetEase. Steps whose per-tweener value
    // is absent fall back to these at fold-time (see tween-catalog-gen.ts), fixing the "?/?" gap where
    // game code sets the transition/easing once at the Tween level instead of per-property.
    private static void TweenSetTransPostfix(Tween __instance, Tween.TransitionType trans)
    {
        if (!Active)
        {
            return;
        }

        try
        {
            GetOrCreateRecord(__instance).DefaultTrans = trans.ToString();
        }
        catch
        {
        }
    }

    private static void TweenSetEasePostfix(Tween __instance, Tween.EaseType ease)
    {
        if (!Active)
        {
            return;
        }

        try
        {
            GetOrCreateRecord(__instance).DefaultEase = ease.ToString();
        }
        catch
        {
        }
    }

    // ---- PropertyTweener.* postfixes -------------------------------------------------------------

    private static void FromPostfix(PropertyTweener __instance, Variant value)
    {
        // Part C (Fix 2): the DECLARED start matters to the hints path too — a re-anchored/primed tween (the shared
        // main-menu reticle hard-set to x=0, then `.From(num3)`) must replay FROM num3, not the live transient. So
        // capture the raw Variant under Active (hints OR recording); the JSON `From` stays a recording-only concern.
        if (!Active)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.FromRaw = value;
            step.HasFromRaw = true;
            if (_enabled)
            {
                step.From = VariantToJson(value);
            }
        }
    }

    private static void FromCurrentPostfix(PropertyTweener __instance)
    {
        if (!Active)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.FromCurrent = true;
        }
    }

    private static void SetTransPostfix(PropertyTweener __instance, Tween.TransitionType trans)
    {
        if (!Active)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.Trans = trans.ToString();
        }
    }

    private static void SetEasePostfix(PropertyTweener __instance, Tween.EaseType ease)
    {
        if (!Active)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.Ease = ease.ToString();
        }
    }

    private static void SetDelayPostfix(PropertyTweener __instance, double delay)
    {
        if (!_enabled)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.DelayMs = delay * 1000.0;
        }
    }

    private static void AsRelativePostfix(PropertyTweener __instance)
    {
        if (!_enabled)
        {
            return;
        }

        if (StepRecords.TryGetValue(__instance, out var step))
        {
            step.AsRelative = true;
        }
    }

    // ---- Tween.Kill / Tween.Stop postfix (Fix 1) -------------------------------------------------

    // A killed/stopped tween is either (a) still pending its deferred Finalize — mark it Cancelled so no ghost hint
    // ships (a fade-OUT that was superseded a frame later would otherwise replay in reverse on the shared node); or
    // (b) already finalized and mid-replay — tell the watcher to drop its suppression window and re-sync the target
    // nodes' live values, so an interrupted move/fade no longer freezes those nodes' stream for the full window.
    private static void KillPostfix(Tween __instance)
    {
        if (!Active || !Sts2SceneWatchRuntimeSettings.CancelKilledTweens)
        {
            return;
        }

        try
        {
            if (!TweenRecords.TryGetValue(__instance, out var record))
            {
                return;
            }

            record.Cancelled = true;

            if (WindowCanceller is not { } cancel)
            {
                return;
            }

            // The distinct nodes this tween animated (per-step targets + the record's first-target address). Small
            // set — a tween touches one or a few nodes. The watcher zeroes each one's open suppression window.
            var ids = new HashSet<ulong>();
            if (record.TargetInstanceId != 0)
            {
                ids.Add(record.TargetInstanceId);
            }

            // WS-REST belt-and-braces: a killed fade-IN (a VISIBLE-target opacity step) never opened an opacity
            // window, so the plain "collapse an open window" path is a no-op for it. Detect that case here and ask the
            // watcher to force one settled opacity re-emit of the LIVE alpha even with no window open — so the client's
            // hide-latch always gets a fresh, un-suppressed alpha write to release against.
            var hasOpacityStep = false;
            var endAlphaVisible = false;
            foreach (var step in record.Steps)
            {
                if (step.TargetInstanceId != 0)
                {
                    ids.Add(step.TargetInstanceId);
                }

                if (TryOpacityChange(step) is { } endAlpha)
                {
                    hasOpacityStep = true;
                    if (endAlpha > Sts2CancelledTweenResync.VisibleEndAlphaEps)
                    {
                        endAlphaVisible = true;
                    }
                }
            }

            var forceOpacityResync = Sts2CancelledTweenResync.ShouldForceOpacityResync(
                Sts2SceneWatchRuntimeSettings.CancelledRiseResync, hasOpacityStep, endAlphaVisible);

            if (ids.Count > 0)
            {
                cancel(ids, forceOpacityResync);
            }
        }
        catch
        {
            // Telemetry must never disrupt the game.
        }
    }

    // ---- record lifecycle ------------------------------------------------------------------------

    private static TweenRecord GetOrCreateRecord(Tween tween)
    {
        if (TweenRecords.TryGetValue(tween, out var existing))
        {
            return existing;
        }

        var record = new TweenRecord();

        // The StackTrace trigger capture is the dominant per-tween cost and is a RECORDING concern only
        // (it names the game method that built the tween, folded into the catalog). Lite hint mode skips it.
        if (_enabled)
        {
            CaptureTrigger(record);
        }

        TweenRecords.Add(tween, record);

        // A tween is built synchronously within its creating method; by the next idle frame the record
        // is complete (every chained tweener has been appended). Finalize there — this captures looping and
        // killed tweens too, which the `finished` signal would miss.
        Callable.From(() =>
        {
            try
            {
                Finalize(record);
            }
            catch
            {
            }
        }).CallDeferred();

        return record;
    }

    private static void Finalize(TweenRecord record)
    {
        if (record.Flushed)
        {
            return;
        }

        record.Flushed = true;
        if (record.Steps.Count == 0)
        {
            return;
        }

        if (_enabled)
        {
            WriteRecord(record);
        }

        // A cancelled (killed/stopped) tween still records for JSONL/catalog fidelity, but must NOT emit a mirror
        // hint: it was superseded before it could play, so replaying its endpoint would animate the wrong thing
        // (e.g. the shared reticle's killed fade-OUT replaying 1→0 in reverse on the node already faded IN).
        if (_hintsEnabled && !record.Cancelled)
        {
            EmitHints(record);
        }
    }

    private static void WriteRecord(TweenRecord record)
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        var json = record.ToJson().ToJsonString();
        if (sink.Writer.TryWrite(json))
        {
            Interlocked.Increment(ref _recordsFlushed);
        }
    }

    // Lite hint fan-out: one timing hint per PROPERTY step (interval/callback/method steps are dropped),
    // and only for tweens whose target resolved to a res://scenes path (no scene => unusable for CSS
    // pre-arm). Raw Godot property strings + Trans/Ease names ship as-is; the consumer maps to CSS.
    private static void EmitHints(TweenRecord record)
    {
        if (record.Scene is not { } scene)
        {
            return;
        }

        var node = record.Node ?? ".";
        // Part C: attach declarative ENDPOINTS so a mirror consumer can REPLAY the tween (animate to the endpoint)
        // rather than only pre-arm timing. A single Godot tween may animate MULTIPLE nodes in parallel (an event option
        // scales its ROOT while fading a highlight-glow CHILD's modulate), so we GROUP property steps by their own
        // target node and resolve each group independently — otherwise a child's fade endpoint would be attributed to
        // (and fanned across) the wrong node's subtree. Per group we combine its steps into one end state and resolve
        // two channels: a TRANSFORM endpoint (Stage 2: parallel position + scale + rotation) attached to that group's
        // longest-duration transform step, and an OPACITY endpoint (Stage 4: modulate alpha fade) attached to its
        // longest-duration alpha step — each hint's duration is what the client uses for that channel's CSS transition.
        // Gated to Loops==0 (a one-shot CSS transition can't loop). Every other step — and every step when no resolver
        // is wired — stays a plain timing hint. Best-effort: any failure drops only the endpoints, never the timing
        // hint or the game.
        Dictionary<StepRecord, IReadOnlyList<double>>? endTransformByAnchor = null;
        Dictionary<StepRecord, double>? endOpacityByAnchor = null;
        Dictionary<StepRecord, IReadOnlyList<double>>? startTransformByAnchor = null;
        Dictionary<StepRecord, double>? startOpacityByAnchor = null;
        if (record.Loops == 0 && EndpointResolver is { } resolve)
        {
            var byTarget = new Dictionary<ulong, List<StepRecord>>();
            foreach (var step in record.Steps)
            {
                if (step.Kind != "property" || step.Property is null)
                {
                    continue;
                }

                var targetId = step.TargetInstanceId != 0 ? step.TargetInstanceId : record.TargetInstanceId;
                if (!byTarget.TryGetValue(targetId, out var list))
                {
                    byTarget[targetId] = list = new List<StepRecord>();
                }

                list.Add(step);
            }

            foreach (var (targetId, steps) in byTarget)
            {
                if (CombineTransformChange(steps, out var transformAnchor, out var opacityAnchor) is not { } change)
                {
                    continue;
                }

                try
                {
                    // Pass the transform anchor's duration so the resolver opens a matching streaming-suppression window
                    // on THIS target (the client uses that same duration for its transform transition); fall back to the
                    // opacity anchor for a fade-only group (no transform → no suppression window opened anyway).
                    var durationMs = transformAnchor?.DurationMs ?? opacityAnchor?.DurationMs ?? 0;
                    if (resolve(targetId, change, durationMs) is not { } endpoint)
                    {
                        continue;
                    }

                    if (endpoint.Transform is { } t && transformAnchor is not null)
                    {
                        (endTransformByAnchor ??= new Dictionary<StepRecord, IReadOnlyList<double>>())[transformAnchor] = t;
                    }

                    if (endpoint.Opacity is { } o && opacityAnchor is not null)
                    {
                        (endOpacityByAnchor ??= new Dictionary<StepRecord, double>())[opacityAnchor] = o;
                    }

                    // Fix 2: the declared START rides the SAME anchor as its endpoint channel (the client primes the
                    // element there before transitioning). Null unless the tween declared `.From(...)`.
                    if (endpoint.StartTransform is { } st && transformAnchor is not null)
                    {
                        (startTransformByAnchor ??= new Dictionary<StepRecord, IReadOnlyList<double>>())[transformAnchor] = st;
                    }

                    if (endpoint.StartOpacity is { } so && opacityAnchor is not null)
                    {
                        (startOpacityByAnchor ??= new Dictionary<StepRecord, double>())[opacityAnchor] = so;
                    }
                }
                catch
                {
                    // Endpoint resolution must never disrupt the game or drop the timing hint.
                }
            }
        }

        foreach (var step in record.Steps)
        {
            if (step.Kind != "property" || step.Property is null)
            {
                continue;
            }

            IReadOnlyList<double>? endTransform = null;
            endTransformByAnchor?.TryGetValue(step, out endTransform);
            double? endOpacity = null;
            if (endOpacityByAnchor is not null && endOpacityByAnchor.TryGetValue(step, out var o))
            {
                endOpacity = o;
            }

            IReadOnlyList<double>? startTransform = null;
            startTransformByAnchor?.TryGetValue(step, out startTransform);
            double? startOpacity = null;
            if (startOpacityByAnchor is not null && startOpacityByAnchor.TryGetValue(step, out var so))
            {
                startOpacity = so;
            }

            var hint = new Embedding.TweenAnimationHint(
                scene,
                node,
                step.Property,
                step.To?.ToJsonString(),
                step.DurationMs,
                step.Trans ?? record.DefaultTrans,
                step.Ease ?? record.DefaultEase,
                step.TargetInstanceId != 0 ? step.TargetInstanceId : record.TargetInstanceId,
                EndTransform: endTransform,
                EndOpacity: endOpacity,
                StartTransform: startTransform,
                StartOpacity: startOpacity);

            Embedding.EmbeddableAnimationHintHub.Shared.Publish(hint);
            var count = Interlocked.Increment(ref _hintsEmitted);
            LogHintMilestone(count);
        }
    }

    // Map a property step's raw target Variant to a TweenTargetChange the endpoint resolver understands. Stage 2
    // handles the full transform set (position/scale/rotation, whole-vector or per-axis); opacity is handled
    // separately by TryOpacityChange (Stage 4). Returns null for non-transform props.
    private static TweenTargetChange? TryTransformChange(StepRecord step) =>
        step.HasToRaw ? MapTransform(step.Property, step.ToRaw) : null;

    // The tween's START for a transform step, mapped to the same component. An explicit `.From(...)` (Fix 2) always
    // wins; otherwise the IMPLICIT start sampled at registration time is used when the kill-switch is on (WS-ANIM), so
    // an endpoint-only chrome tween still primes/replays from its real pre-tween value on a slow client. Null when the
    // step has neither — the client then falls back to the committed value (unchanged behavior).
    private static TweenTargetChange? TryTransformStart(StepRecord step)
    {
        if (step.HasFromRaw)
        {
            return MapTransform(step.Property, step.FromRaw);
        }

        if (step.HasImplicitFromRaw && Sts2SceneWatchRuntimeSettings.TweenImplicitStart)
        {
            return MapTransform(step.Property, step.ImplicitFromRaw);
        }

        return null;
    }

    private static TweenTargetChange? MapTransform(string? property, Variant raw)
    {
        if (property is null)
        {
            return null;
        }

        return property switch
        {
            "position" => new TweenTargetChange { Position = raw.AsVector2() },
            "position:x" => new TweenTargetChange { PositionX = raw.AsSingle() },
            "position:y" => new TweenTargetChange { PositionY = raw.AsSingle() },
            // Global-space position (the shared main-menu reticle slides via `global_position:x`). Resolved by a
            // dedicated global path in ResolveTweenEndpoint — distinct from the parent-relative `position` above.
            "global_position" => new TweenTargetChange { GlobalPosition = raw.AsVector2() },
            "global_position:x" => new TweenTargetChange { GlobalPositionX = raw.AsSingle() },
            "global_position:y" => new TweenTargetChange { GlobalPositionY = raw.AsSingle() },
            "scale" => new TweenTargetChange { Scale = raw.AsVector2() },
            "scale:x" => new TweenTargetChange { ScaleX = raw.AsSingle() },
            "scale:y" => new TweenTargetChange { ScaleY = raw.AsSingle() },
            "rotation" => new TweenTargetChange { Rotation = raw.AsSingle() },
            _ => null,
        };
    }

    // Stage 4: map a property step's raw target Variant to an ALPHA (opacity) endpoint, if it is one. Handles both
    // the whole-color tween (take .a) and the alpha-only sub-property, for BOTH the `modulate` (child-cascade) and
    // `self_modulate` (own-paint-only) channels. The alpha value is the same math for both channels; WHICH channel
    // this step drives is recovered from step.Property by CombineTransformChange (self_modulate → SelfModulateA).
    // `self_modulate:r/g/b` are intentionally NOT mapped: they tint the node's own paint, not its alpha. Returns
    // null for non-opacity props. `internal` (not private) so the Godot-free mapping is unit-testable.
    internal static float? TryOpacityChange(StepRecord step) =>
        step.HasToRaw ? MapOpacity(step.Property, step.ToRaw) : null;

    // The START alpha for an opacity step. An explicit `.From(...)` (Fix 2) wins; otherwise the IMPLICIT start sampled
    // at registration time is used when the kill-switch is on (WS-ANIM). Lets the resolver's opacity guard compare the
    // end alpha against the tween's real start rather than a stale live sample, and lets the client prime the fade from
    // its true start. Null when the step has neither — original live-sample behavior. `internal` for unit tests.
    internal static float? TryOpacityStart(StepRecord step)
    {
        if (step.HasFromRaw)
        {
            return MapOpacity(step.Property, step.FromRaw);
        }

        if (step.HasImplicitFromRaw && Sts2SceneWatchRuntimeSettings.TweenImplicitStart)
        {
            return MapOpacity(step.Property, step.ImplicitFromRaw);
        }

        return null;
    }

    private static float? MapOpacity(string? property, Variant raw)
    {
        if (property is null)
        {
            return null;
        }

        return property switch
        {
            "modulate:a" => raw.AsSingle(),
            "modulate" => raw.AsColor().A,
            "self_modulate:a" => raw.AsSingle(),
            "self_modulate" => raw.AsColor().A,
            _ => null,
        };
    }

    // Parallel-combine: fold ALL of a tween's animatable property steps into ONE end-state change (last-writer-wins
    // per component). Card play/focus tweens drive position + scale (± rotation) — and sometimes a modulate fade — in
    // parallel toward one final state; a single CSS transition per channel can only go to one endpoint, so we resolve
    // the combined end once. Transform and opacity are SEPARATE channels with independent timing: `transformAnchor`
    // gets the longest-duration transform step (the client's transform-transition duration + the producer's
    // suppression window) and `opacityAnchor` the longest-duration alpha step (the opacity-transition duration). A
    // fade can run parallel to a move with a different duration, so the anchors may differ. Returns a change that
    // satisfies HasAny (transform OR opacity), or null when no step touches either — so a fade-ONLY tween still emits.
    // `internal` (not private) so the Godot-free combine/channel-selection logic is unit-testable.
    internal static TweenTargetChange? CombineTransformChange(
        List<StepRecord> steps,
        out StepRecord? transformAnchor,
        out StepRecord? opacityAnchor)
    {
        transformAnchor = null;
        opacityAnchor = null;
        Vector2? position = null;
        float? positionX = null;
        float? positionY = null;
        Vector2? scale = null;
        float? scaleX = null;
        float? scaleY = null;
        float? rotation = null;
        Vector2? globalPosition = null;
        float? globalPositionX = null;
        float? globalPositionY = null;
        float? modulateA = null;
        // Fix 2: parallel accumulators for each step's DECLARED start (`.From(...)`), last-writer-wins like the end.
        Vector2? startPosition = null;
        float? startPositionX = null;
        float? startPositionY = null;
        Vector2? startScale = null;
        float? startScaleX = null;
        float? startScaleY = null;
        float? startRotation = null;
        Vector2? startGlobalPosition = null;
        float? startGlobalPositionX = null;
        float? startGlobalPositionY = null;
        float? startModulateA = null;
        var any = false;
        var bestTransformDuration = double.NegativeInfinity;
        var bestOpacityDuration = double.NegativeInfinity;

        foreach (var step in steps)
        {
            if (step.Kind != "property")
            {
                continue;
            }

            if (TryTransformChange(step) is { } change)
            {
                any = true;
                if (change.Position is { } p) position = p;
                if (change.PositionX is { } px) positionX = px;
                if (change.PositionY is { } py) positionY = py;
                if (change.Scale is { } s) scale = s;
                if (change.ScaleX is { } sx) scaleX = sx;
                if (change.ScaleY is { } sy) scaleY = sy;
                if (change.Rotation is { } r) rotation = r;
                if (change.GlobalPosition is { } gp) globalPosition = gp;
                if (change.GlobalPositionX is { } gpx) globalPositionX = gpx;
                if (change.GlobalPositionY is { } gpy) globalPositionY = gpy;

                if (TryTransformStart(step) is { } start)
                {
                    if (start.Position is { } sp) startPosition = sp;
                    if (start.PositionX is { } spx) startPositionX = spx;
                    if (start.PositionY is { } spy) startPositionY = spy;
                    if (start.Scale is { } ss) startScale = ss;
                    if (start.ScaleX is { } ssx) startScaleX = ssx;
                    if (start.ScaleY is { } ssy) startScaleY = ssy;
                    if (start.Rotation is { } sr) startRotation = sr;
                    if (start.GlobalPosition is { } sgp) startGlobalPosition = sgp;
                    if (start.GlobalPositionX is { } sgpx) startGlobalPositionX = sgpx;
                    if (start.GlobalPositionY is { } sgpy) startGlobalPositionY = sgpy;
                }

                if (step.DurationMs > bestTransformDuration)
                {
                    bestTransformDuration = step.DurationMs;
                    transformAnchor = step;
                }
            }
            else if (TryOpacityChange(step) is { } alpha)
            {
                any = true;
                modulateA = alpha;

                if (step.DurationMs > bestOpacityDuration)
                {
                    bestOpacityDuration = step.DurationMs;
                    opacityAnchor = step;
                    // Take the start alpha from the anchor step (the one whose channel wins below), so the guard and
                    // client prime use the declared start of the dominant fade. Null if the anchor had no `.From(...)`.
                    startModulateA = TryOpacityStart(step);
                }
            }
        }

        if (!any)
        {
            return null;
        }

        // The single longest-duration opacity anchor decides the channel: a `self_modulate`(:a) step maps to
        // SelfModulateA, everything else (`modulate`/`modulate:a`) to ModulateA. Exactly one is populated. In the
        // rare case a tween drives BOTH a modulate and a self_modulate step, the longer-duration one wins the anchor
        // and thus the channel — the client can only pin one alpha per node, so we resolve the dominant fade.
        var opacityIsSelf = opacityAnchor?.Property is { } anchorProp
            && anchorProp.StartsWith("self_modulate", StringComparison.Ordinal);

        return new TweenTargetChange
        {
            Position = position,
            PositionX = positionX,
            PositionY = positionY,
            Scale = scale,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Rotation = rotation,
            GlobalPosition = globalPosition,
            GlobalPositionX = globalPositionX,
            GlobalPositionY = globalPositionY,
            ModulateA = opacityIsSelf ? null : modulateA,
            SelfModulateA = opacityIsSelf ? modulateA : null,
            StartPosition = startPosition,
            StartPositionX = startPositionX,
            StartPositionY = startPositionY,
            StartScale = startScale,
            StartScaleX = startScaleX,
            StartScaleY = startScaleY,
            StartRotation = startRotation,
            StartGlobalPosition = startGlobalPosition,
            StartGlobalPositionX = startGlobalPositionX,
            StartGlobalPositionY = startGlobalPositionY,
            StartModulateA = opacityIsSelf ? null : startModulateA,
            StartSelfModulateA = opacityIsSelf ? startModulateA : null,
        };
    }

    private static void LogHintMilestone(long count)
    {
        if (count == 1 || count == 5 || count == 25 || count == 100 || count == 500
            || count == 2000 || count == 10000 || count == 50000)
        {
            _log?.Write(
                BridgeLogLevel.Info,
                ProbeTarget,
                $"Animation-hint emitted {count} time(s) (lite mode) — propertyCalls={PropertyCallCount}.");
        }
    }

    private static void CaptureTrigger(TweenRecord record)
    {
        // Stack walk only happens on the enabled path. Skip file info for speed.
        var trace = new StackTrace(2, fNeedFileInfo: false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            var declaring = method?.DeclaringType;
            if (declaring?.Namespace is not { } ns || !ns.StartsWith(GameNamespaceRoot, StringComparison.Ordinal))
            {
                continue;
            }

            // Coroutine/async state machine: NFoo+<AnimateIn>d__69.MoveNext -> class NFoo, method AnimateIn.
            var match = StateMachineName.Match(declaring.Name);
            if (match.Success && declaring.DeclaringType is { } outer)
            {
                record.TriggerClass = outer.Name;
                record.TriggerMethod = match.Groups[1].Value;
            }
            else
            {
                record.TriggerClass = declaring.Name;
                record.TriggerMethod = method!.Name;
            }

            return;
        }
    }

    private static void LogProbeMilestone(long count)
    {
        // Log at geometric milestones so the 500-entry log ring isn't flooded but the probe is observable.
        if (count == 1 || count == 5 || count == 25 || count == 100 || count == 500
            || count == 2000 || count == 10000)
        {
            _log?.Write(
                BridgeLogLevel.Info,
                ProbeTarget,
                $"Tween.TweenProperty postfix fired {count} time(s) — engine-binding Harmony hook is LIVE (not inlined away).");
        }
    }

    // ---- addressing / conversion -----------------------------------------------------------------

    private static string? NormalizeScenePath(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
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

        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static JsonNode? VariantToJson(Variant value)
    {
        try
        {
            switch (value.VariantType)
            {
                case Variant.Type.Nil:
                    return null;
                case Variant.Type.Bool:
                    return JsonValue.Create(value.AsBool());
                case Variant.Type.Int:
                    return JsonValue.Create(value.AsInt64());
                case Variant.Type.Float:
                    return JsonValue.Create(value.AsDouble());
                case Variant.Type.String:
                case Variant.Type.StringName:
                case Variant.Type.NodePath:
                    return JsonValue.Create(value.AsString());
                case Variant.Type.Vector2:
                {
                    var v = value.AsVector2();
                    return new JsonArray(v.X, v.Y);
                }
                case Variant.Type.Vector3:
                {
                    var v = value.AsVector3();
                    return new JsonArray(v.X, v.Y, v.Z);
                }
                case Variant.Type.Color:
                {
                    var c = value.AsColor();
                    return new JsonArray(c.R, c.G, c.B, c.A);
                }
                default:
                    return JsonValue.Create(value.ToString());
            }
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveOutputPath()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{stamp}.jsonl";
        var dirOverride = System.Environment.GetEnvironmentVariable(OutputDirEnvVar);
        string dir;
        if (!string.IsNullOrWhiteSpace(dirOverride))
        {
            dir = dirOverride;
        }
        else
        {
            try
            {
                dir = ProjectSettings.GlobalizePath("user://spirectl/tween-records");
            }
            catch
            {
                dir = Path.Combine(Path.GetTempPath(), "spirectl-tween-records");
            }
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
        }

        return Path.Combine(dir, fileName);
    }

    private static void DrainWriter(ChannelReader<string> reader, string path)
    {
        try
        {
            using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
            while (true)
            {
                if (!reader.TryRead(out var line))
                {
                    if (!SpinWaitForItem(reader))
                    {
                        break;
                    }

                    continue;
                }

                writer.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            _log?.Write(BridgeLogLevel.Warn, LogTarget, $"Tween JSONL writer stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool SpinWaitForItem(ChannelReader<string> reader)
    {
        var wait = reader.WaitToReadAsync();
        return wait.IsCompletedSuccessfully ? wait.Result : wait.AsTask().GetAwaiter().GetResult();
    }

    private static bool ParseTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }

    private sealed class TweenRecord
    {
        public string? Scene;
        public string? Node;
        public string? TargetType;
        public ulong TargetInstanceId;
        public string? TriggerClass;
        public string? TriggerMethod;
        public bool DefaultParallel;
        public bool PendingParallel;
        public string? DefaultTrans;
        public string? DefaultEase;
        public int Loops;
        public bool Flushed;
        // Set by KillPostfix when the tween is killed/stopped before its deferred Finalize runs: skip EmitHints so no
        // ghost/reverse hint ships (the JSONL WriteRecord still runs for catalog fidelity).
        public bool Cancelled;
        public bool AddressCaptured;
        public readonly List<StepRecord> Steps = new();

        public StepRecord BeginStep(string kind)
        {
            var step = new StepRecord
            {
                Order = Steps.Count,
                Kind = kind,
                // First tweener runs alone; every later one reads the running parallel state.
                Parallel = Steps.Count > 0 && PendingParallel,
            };
            Steps.Add(step);
            // parallel()/chain() are one-shot for the next tweener; set_parallel is the persistent default.
            PendingParallel = DefaultParallel;
            return step;
        }

        public void CaptureAddress(GodotObject? target)
        {
            if (AddressCaptured || target is null)
            {
                return;
            }

            AddressCaptured = true;
            TargetType = target.GetType().Name;
            TargetInstanceId = target.GetInstanceId();

            if (target is not Node node)
            {
                return;
            }

            for (var current = node; current is not null; current = current.GetParentOrNull<Node>())
            {
                var scene = NormalizeScenePath(current.SceneFilePath);
                if (scene is null)
                {
                    continue;
                }

                Scene = scene;
                Node = current == node ? "." : current.GetPathTo(node).ToString();
                return;
            }
        }

        public JsonObject ToJson()
        {
            var steps = new JsonArray();
            foreach (var step in Steps)
            {
                steps.Add(step.ToJson());
            }

            return new JsonObject
            {
                ["scene"] = Scene,
                ["node"] = Node,
                ["targetType"] = TargetType,
                ["targetInstanceId"] = TargetInstanceId,
                ["trigger"] = new JsonObject
                {
                    ["class"] = TriggerClass,
                    ["method"] = TriggerMethod,
                },
                ["defaultParallel"] = DefaultParallel,
                ["defaultTrans"] = DefaultTrans,
                ["defaultEase"] = DefaultEase,
                ["loops"] = Loops,
                ["steps"] = steps,
            };
        }
    }

    // `internal` (not private) so unit tests can build step lists for the Godot-free opacity/channel mapping.
    internal sealed class StepRecord
    {
        public int Order;
        public string Kind = "property";
        public string? Property;
        public JsonNode? From;
        public JsonNode? To;
        public bool FromCurrent;
        public bool AsRelative;
        public double DurationMs;
        public string? Trans;
        public string? Ease;
        public double? DelayMs;
        public bool Parallel;
        // Raw target Variant, retained so Part C endpoint resolution reads the exact value without a JSON
        // round-trip. Value-typed (Vector2/float) for the props we resolve.
        public Variant ToRaw;
        public bool HasToRaw;
        // Raw DECLARED start Variant (`.From(...)`), retained for Part C so a re-anchored/primed tween replays from
        // its declared start rather than a stale live sample. Only set when the game called `.From(...)` on this step.
        public Variant FromRaw;
        public bool HasFromRaw;
        // Raw IMPLICIT start Variant: the target property's CURRENT value sampled at TweenProperty registration time
        // (the node still sits at its pre-tween value there). Used as the tween's start when the game did NOT declare
        // an explicit `.From(...)`, so a slow mirror client that folds the node-create + settle + hint into one message
        // still replays from the real start instead of the near-final streamed value. Gated by the
        // SPIRECTL_TWEEN_IMPLICIT_START kill-switch (default ON); an explicit `.From(...)` always wins.
        public Variant ImplicitFromRaw;
        public bool HasImplicitFromRaw;
        // This STEP's own tween target (a single tween may animate multiple nodes in parallel). Part C attributes each
        // step's endpoint hint to this node; 0 when unresolved (falls back to the record's first-target address).
        public ulong TargetInstanceId;

        public JsonObject ToJson()
        {
            var obj = new JsonObject
            {
                ["order"] = Order,
                ["kind"] = Kind,
                ["durationMs"] = DurationMs,
                ["parallel"] = Parallel,
            };

            if (Property is not null)
            {
                obj["prop"] = Property;
            }

            if (FromCurrent)
            {
                obj["fromCurrent"] = true;
            }
            else if (From is not null)
            {
                obj["from"] = From;
            }

            if (To is not null)
            {
                obj["to"] = To;
            }

            if (Trans is not null)
            {
                obj["trans"] = Trans;
            }

            if (Ease is not null)
            {
                obj["ease"] = Ease;
            }

            if (DelayMs is { } delay)
            {
                obj["delayMs"] = delay;
            }

            if (AsRelative)
            {
                obj["asRelative"] = true;
            }

            return obj;
        }
    }
}
