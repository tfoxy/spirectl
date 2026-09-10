using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

/// <summary>
/// ENV-GATED OFFLINE BAKER (no product behavior). Turns one (scene, node, animation) into a
/// <c>geoclip/0</c> artifact: a manifest of per-slot triangle geometry sampled at a fixed frame rate, plus the
/// raw atlas page PNGs those triangles sample. A client that has the artifact can draw the skeleton itself,
/// instead of being served pre-rendered frames.
///
/// <para>WHY THIS IS OFFLINE. Nothing here reads or touches live game state. The target scene is loaded fresh,
/// its SpineSprite is detached from its scene ancestors and mounted alone in a throwaway SubViewport, and the
/// animation is pinned (timescale zero + explicit per-frame track time) so the sample sequence is
/// reproducible. Slot colors ARE written during the association pass — on that throwaway instance only, and
/// restored immediately — which is exactly why the baker refuses to work on anything mounted in the live
/// tree.</para>
///
/// <para>THE THREE THINGS THAT ARE NOT FREE, and how each is solved:
/// <list type="bullet">
///   <item>The per-slot mesh RID is not exposed on the node. Recovered by BRACKETING: a probe mesh created
///   before the skeleton exists and another after it has built bound every RID it made in between, and the
///   bounded id space is enumerated and validated through the rendering server. Costly (tens of thousands of
///   lookups) and therefore done exactly once per target.</item>
///   <item>Child order is NOT slot order, so which mesh belongs to which slot has to be measured. Measured by
///   flipping one slot's colour alpha and seeing which mesh's vertex colours moved.</item>
///   <item>The runtime atlas resource reports empty atlas text, so the region table is parsed from the
///   IMPORTED atlas on disk (a JSON envelope carrying the text atlas), reached through the resource's
///   `.import` sidecar.</item>
/// </list></para>
///
/// <para>SAFETY. With <c>SPIRECTL_SPINE_GEOCLIP_BAKE</c> unset, <see cref="Install"/> returns before doing
/// anything, so an unarmed host pays one environment read. When armed the bake runs ONCE, well after the game
/// has a scene, writes its artifacts, and stops. Every native hop is individually wrapped so a failure is a
/// logged reason rather than a dead host — and because a later agent has to run this against a game that
/// cannot be run here, EVERY skipped slot, part or frame logs why it was skipped.</para>
/// </summary>
internal static class Sts2SpineGeoClipBaker
{
    // "/2" is the SPLIT-BRACKET baker: two swept RID windows instead of one, a tightened mesh filter, and an
    // atlas-region association fallback. It names the ACQUISITION contract, not the artifact contract — the
    // manifest schema is still `geoclip/0` and a current player needs no change.
    internal const string BakerVersion = "spine-geoclip-baker/0";

    private const string LogPrefix = "SPINE_GEOCLIP";
    private const string LogTarget = "bridge.bake.spine-geoclip";

    // The baker narrates every slot, sweep window and skip — by design, so a bake that cannot be re-run
    // here is still debuggable from a log. That narration ALWAYS goes to the bridge log stream (below, and
    // viewable at the DebugLogViewer endpoint). It additionally goes to GD.Print — i.e. the game's
    // godot.log — ONLY when this is set, because on-demand geoclip bakes are on by default for a normal
    // CouchCoop player and ~100+ untagged SPINE_GEOCLIP lines per baked creature is log spam for them.
    // Set SPIRECTL_SPINE_GEOCLIP_LOG_GODOT=1 for a dev session that wants the lines in godot.log.
    private const string GodotLogEnv = "SPIRECTL_SPINE_GEOCLIP_LOG_GODOT";
    private static readonly bool LogToGodot = ReadBoolEnv(GodotLogEnv, false);

    private const string BakeEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE";
    private const string OutEnv = "SPIRECTL_SPINE_GEOCLIP_OUT";
    private const string FpsEnv = "SPIRECTL_SPINE_GEOCLIP_FPS";
    private const string DelayEnv = "SPIRECTL_SPINE_GEOCLIP_DELAY_SEC";
    private const string CandidateCapEnv = "SPIRECTL_SPINE_GEOCLIP_CANDIDATE_CAP";
    private const string IndexSlackEnv = "SPIRECTL_SPINE_GEOCLIP_INDEX_SLACK";
    private const string MaxFramesEnv = "SPIRECTL_SPINE_GEOCLIP_MAX_FRAMES";
    private const string WaitEnv = "SPIRECTL_SPINE_GEOCLIP_WAIT_SEC";
    private const string WindowBCapEnv = "SPIRECTL_SPINE_GEOCLIP_WINDOW_B_CAP";
    private const string StrictMeshFilterEnv = "SPIRECTL_SPINE_GEOCLIP_STRICT_MESH_FILTER";

    // The acquisition arm selector. DENSE IS THE DEFAULT, and the walk is opt-IN via
    // SPIRECTL_SPINE_GEOCLIP_DENSE_SWEEP=0 — the reverse of how this shipped, because measuring it live turned
    // the walk from a win into a LOSS.
    //
    // What was measured (WS-F2, 6 walk-armed bakes, both rigs, every session): the dense fallback fired EVERY
    // time, so the walk's probes were pure added cost — byrdonis 22 311 probes against dense's 20 929 (+6.6 %),
    // merchant 59 198 against 49 200 (+20.3 %). Two independent causes, and the first is a design interaction
    // rather than a tuning value:
    //
    //   1. `Plan` refuses any window at or below its candidate floor, while `DenseSweepRequired` grades the
    //      walk's TOTAL across windows against `slotsVisibleAtAcquisition`. byrdonis window B holds 2 196
    //      candidates and TWO real slot meshes, so the walk is never allowed to look for them, can never reach
    //      28, and the fallback is guaranteed BEFORE A SINGLE PROBE RUNS. Any rig with one mesh in a small
    //      window has this shape.
    //   2. The merchant's window-B walk finds 0 of 14 (8 456 probes, budget exhausted, no run seated, the
    //      stride ladder never measuring a stride).
    //
    // The arm is kept, not deleted: its offline reasoning is sound on recorded id spaces (92-96 % savings) and
    // its ~40 tests still pass. What the offline fixtures could not falsify is a walk that never seats a
    // starting column on a LIVE window. Re-arming it wants (1) per-window grading so a small window's meshes
    // are not charged to the walk's total, and (2) a live answer for why window B seats no column.
    private const string DenseSweepEnv = "SPIRECTL_SPINE_GEOCLIP_DENSE_SWEEP";

    // Fuse consecutive ENV-LANE targets that share a (scene, node) into ONE rig bake. Off by default so a spec's
    // recorded per-target numbers keep meaning what they meant; see GroupTargets.
    private const string BatchRigsEnv = "SPIRECTL_SPINE_GEOCLIP_BATCH_RIGS";

    // Accept a rig bake whose sweep validated meshes no probed slot answered for, instead of sending the rig to
    // per-target bakes. OFF by default — see Sts2SpineGeoClipBatch.BatchRefusal for why the safe direction is
    // the default and why this is a lever rather than a decision.
    private const string BatchForeignOkEnv = "SPIRECTL_SPINE_GEOCLIP_BATCH_FOREIGN_OK";

    // Opt-in only, capped at Sts2SpineGeoClipForeignCandidateDiagnostics.MaximumLimit. This diagnoses meshes
    // that passed the existing filter but were left unclaimed; it is log-only and never changes the artifact,
    // association, batch fallback, or refusal verdict. Read by BOTH lanes — the env-armed one-shot in Install
    // and the on-demand request lane a host's /geoclips/ route runs — because they share one bake and a
    // diagnostic only the env lane could arm left the lane that serves users unable to explain itself. It is
    // deliberately NOT in Sts2SpineGeoClipLevers.Signature: being log-only it decides no verdict, and folding it
    // in would split the refusal memo's keyspace over a switch that changes nothing the memo answers.
    private const string ForeignCandidateDiagnosticsEnv = "SPIRECTL_SPINE_GEOCLIP_FOREIGN_DIAGNOSTICS";

    // Run-wide POSE-ONLY defaults. Per-target `&pose=1` / `&t=` in the spec are the finer-grained lever; these
    // exist so a whole sweep can be flipped to single-pose without rewriting every chunk of the spec.
    private const string PoseOnlyEnv = "SPIRECTL_SPINE_GEOCLIP_POSE_ONLY";
    private const string SampleTimeEnv = "SPIRECTL_SPINE_GEOCLIP_SAMPLE_SEC";

    // How many bracketed candidates are validated before a frame is yielded back to the game. A rendering-server
    // getter synchronizes with the render thread, so an unbroken sweep would visibly freeze the host.
    private const int CandidateChunkSize = 8192;

    // A sweep that comes back with more one-surface meshes than this is picking up somebody else's geometry;
    // the excess is dropped (and reported) rather than paying for it on every sampled frame.
    private const int MaxRetainedMeshRids = 2048;

    private static readonly Lock Sync = new();
    private static bool _installed;

    /// <summary>
    /// Arm the baker if (and only if) <c>SPIRECTL_SPINE_GEOCLIP_BAKE</c> names at least one target. Returns
    /// immediately otherwise, which is the unarmed host's entire cost.
    /// </summary>
    public static void Install(ILogStream logStream)
    {
        var spec = (System.Environment.GetEnvironmentVariable(BakeEnv) ?? string.Empty).Trim();
        if (spec.Length == 0)
        {
            return;
        }

        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
        }

        // The latch above is PER-ASSEMBLY-IDENTITY, and an embedder ships a second copy of this runtime under its
        // own assembly name — so one env var arms two bakers, which mint meshes into the same RID validator space
        // and write the same output directory. The claim is the outer, process-global guard; it sits BELOW the
        // empty-spec return so an unarmed host still pays exactly one environment read. See Sts2OneShotArmClaim.
        var owner = Sts2OneShotArmClaim.SelfOwner;
        if (!Sts2OneShotArmClaim.TryClaim(Sts2OneShotArmClaim.SpineGeoClipBakeSubsystem, owner, out var existingOwner))
        {
            Log(logStream, $"already armed by {existingOwner}; standing down.");
            return;
        }

        try
        {
            var outDir = (System.Environment.GetEnvironmentVariable(OutEnv) ?? string.Empty).Trim();
            if (outDir.Length == 0)
            {
                Log(logStream, $"armed with {BakeEnv}='{spec}' but {OutEnv} is unset; doing nothing.");
                return;
            }

            var targets = Sts2SpineGeoClipSpec.ParseTargets(spec);
            if (targets.Count == 0)
            {
                Log(logStream, $"armed with {BakeEnv}='{spec}' but it named no target; doing nothing.");
                return;
            }

            var config = new GeoClipConfig(
                owner,
                spec,
                outDir,
                targets,
                Sts2SpineGeoClipSpec.ParseFps(System.Environment.GetEnvironmentVariable(FpsEnv)),
                ReadIntEnv(DelayEnv, 10, 0, 600),
                ReadIntEnv(CandidateCapEnv, 50_000, 0, 5_000_000),
                ReadIntEnv(IndexSlackEnv, 64, 0, 65_536),
                ReadIntEnv(MaxFramesEnv, Sts2SpineGeoClipMath.DefaultMaxFrames, 1, 100_000),
                ReadIntEnv(WaitEnv, 300, 5, 3600),
                // Deliberately allowed above the AUTO-RAISE ceiling: the ceiling exists so the baker never plans a
                // multi-minute sweep by itself, not to stop an operator who has decided to pay for one.
                ReadOptionalIntEnv(WindowBCapEnv, 1, 5_000_000),
                ReadBoolEnv(StrictMeshFilterEnv, true),
                ReadBoolEnv(PoseOnlyEnv, false),
                ReadOptionalDoubleEnv(SampleTimeEnv),
                // Default TRUE: dense is the shipped acquisition. See DenseSweepEnv for the live measurement that
                // inverted this, and for what re-arming the walk would need. Resolved through the Godot-free core
                // rather than a literal here, so this lane and the on-demand request lane cannot disagree about
                // what "nothing said otherwise" means — they had, and the request lane still paid for a walk that
                // had been reverted here.
                Sts2SpineGeoClipWalk.ResolveDenseSweepOnly(
                    System.Environment.GetEnvironmentVariable(DenseSweepEnv)),
                ForeignCandidateDiagnosticsLimit: Sts2SpineGeoClipForeignCandidateDiagnostics.ParseLimit(
                    System.Environment.GetEnvironmentVariable(ForeignCandidateDiagnosticsEnv)));

            Log(
                logStream,
                $"armed owner={config.Owner} out='{config.OutDir}' targets={config.Targets.Count} fps={config.Fps} "
                + $"maxFrames={config.MaxFrames} delaySec={config.StartDelaySeconds} candidateCap={config.CandidateCap} "
                + $"windowBCap={config.WindowBCap?.ToString(CultureInfo.InvariantCulture) ?? "<auto>"} "
                + $"strictMeshFilter={(config.StrictMeshFilter ? 1 : 0)} "
                + $"denseSweepOnly={(config.DenseSweepOnly ? 1 : 0)} "
                + $"poseOnly={(config.PoseOnly ? 1 : 0)} "
                + $"sampleSec={config.SampleTimeSeconds?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "<auto>"}"
                + (config.ForeignCandidateDiagnosticsLimit > 0
                    ? $" foreignCandidateDiagnostics={config.ForeignCandidateDiagnosticsLimit}"
                    : string.Empty));
            foreach (var target in config.Targets)
            {
                Log(
                    logStream,
                    $"target scene='{target.ScenePath}' node='{target.NodePath ?? "<auto>"}' "
                    + $"anim='{target.Animation ?? "<missing>"}' poseOnly={(config.IsPoseOnly(target) ? 1 : 0)} "
                    + $"t={config.RequestedSampleTime(target)?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "<auto>"}");
            }

            // Detached: Install runs during bridge construction, long before there is a scene to bake against.
            _ = Task.Run(() => RunAsync(config, logStream));
        }
        catch (Exception ex)
        {
            Log(logStream, $"failed to arm: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── The on-demand REQUEST lane ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bake ONE target on demand and return what it produced. The lane a host uses to serve a geoclip it does
    /// not have yet, as opposed to <see cref="Install"/>'s env-armed one-shot sweep.
    ///
    /// <para>IT DOES NOT CLAIM. <c>Sts2OneShotArmClaim</c> guards <see cref="Install"/>, where one environment
    /// variable reaches two copies of this runtime in one process. A request carries its own output directory and
    /// arrives on a caller's thread, so it has no such ambiguity — and if it claimed, an env-armed host would
    /// refuse every request for the life of the process. Both lanes converge on
    /// <see cref="BakeTargetOnMainThreadAsync"/>, which is the only implementation of a bake.</para>
    ///
    /// <para>BLOCKING, and it must not be called from the game's main thread — it posts the bake onto that thread
    /// and waits for it, so calling it from there is a deadlock. That is checked rather than documented away: an
    /// on-thread call returns a structured failure. Callers run it on a worker (a host's extraction gate already
    /// does).</para>
    /// </summary>
    internal static SpineGeoClipBakeResultSnapshot BakeOnDemand(
        SpineGeoClipBakeRequestSnapshot request,
        ILogStream? logStream = null)
        // THE REFUSAL MEMO WRAPS THE WHOLE LANE, so an identity this process has already baked and refused is
        // answered for the cost of a dictionary lookup — before planning, before the output directory, before
        // the hop onto the game's main thread. A refusal costs what a success costs (seconds of scene load,
        // bracket sweep and association) and re-deriving one changes nothing about it. The protocol itself lives
        // in the Godot-free lane so it is unit-testable without an engine; Sts2SpineGeoClipRefusalMemo carries
        // the argument for what is in the key and what the memo can still hide.
        => Sts2SpineGeoClipRequestLane.BakeWithRefusalMemo(
            request,
            Sts2SpineGeoClipRefusalMemo.Shared,
            Sts2SpineGeoClipLevers.CaptureSignature(),
            BridgeBuildInfo.BridgeVersion,
            () => BakeOnDemandUncached(request, logStream),
            log: note => Log(logStream, note));

    /// <summary>
    /// <see cref="BakeOnDemand"/> with the refusal memo taken off: plan, validate, and run the bake.
    /// </summary>
    private static SpineGeoClipBakeResultSnapshot BakeOnDemandUncached(
        SpineGeoClipBakeRequestSnapshot request,
        ILogStream? logStream)
    {
        // The SAME variable, parse and cap the env lane arms with (see the ForeignCandidateDiagnosticsEnv read in
        // Install). It is resolved here rather than inside PlanConfig so that method stays pure — and it is
        // resolved AT ALL because both lanes share BakeRigOnMainThreadAsync, which only ever sees the limit the
        // config carries: leaving it at the record default made the lane the product's /geoclips/ route runs the
        // one lane that could not report why association left a validated mesh unclaimed.
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            request,
            Sts2OneShotArmClaim.SelfOwner,
            out var rejected,
            Sts2SpineGeoClipForeignCandidateDiagnostics.ParseLimit(
                System.Environment.GetEnvironmentVariable(ForeignCandidateDiagnosticsEnv)));
        if (config is null)
        {
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                $"the geoclip bake request is missing a required field: {rejected}.",
                [new AssetExtractDetail(rejected ?? "request", "<missing>", "This field is required.")]);
        }

        var status = Sts2MainThreadDispatcher.DescribeStatus();
        if (!status.HasCapturedContext)
        {
            return SpineGeoClipBakeResultSnapshot.NotSupported(
                "No STS2 main thread has been captured, so there is no engine to load the scene into.");
        }

        if (status.IsOnCapturedThread)
        {
            // Failing loudly beats deadlocking the game: the bake awaits engine frames, which cannot advance
            // while the main thread is blocked inside this call.
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                "a geoclip bake cannot be requested from the STS2 main thread; it would deadlock.",
                [
                    new AssetExtractDetail(
                        Field: "thread",
                        Value: "main",
                        Note: "Call BakeSpineGeoClip from a worker thread; the bake marshals itself onto the "
                            + "main thread and awaits engine frames."),
                ]);
        }

        try
        {
            Directory.CreateDirectory(config.OutDir);
        }
        catch (Exception ex)
        {
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                $"cannot create the geoclip output directory '{config.OutDir}': {ex.Message}",
                [new AssetExtractDetail("outputDirectory", config.OutDir, ex.GetType().Name)]);
        }

        try
        {
            // The phase recorder opens HERE, on the requesting thread, not inside the main-thread delegate: the
            // hop itself is real time a caller waits for (the main loop only drains the dispatcher between
            // frames) and it is the one segment the bake cannot see. Completed in a finally so a FAILED bake
            // still leaves a breakdown — which is exactly when one is wanted. Same shape as the raster lane's
            // (Sts2AssetExtractProvider.ExtractAsync), deliberately.
            var recorder = Sts2RenderPhaseProfile.Begin(MintProfileRequestId(config.Targets));
            try
            {
                var dispatchWait = Sts2RenderPhaseProfile.Measure(
                    Sts2RenderPhaseProfile.Phase.DispatchWait, blocking: false);

                // EVERY target in one dispatch. A request that names N animations of one rig is ONE bake of that
                // rig — the config's targets already carry them — and posting them separately would pay the
                // per-scene sweep and association N times, which is the whole thing this lane is trying not to
                // do.
                async Task<GeoClipRigBakeOutcome> RunOnMainThreadAsync()
                {
                    dispatchWait.Dispose();

                    // MANDATORY. The dispatcher POSTS a raw delegate onto the captured SynchronizationContext,
                    // so none of this thread's ExecutionContext — and therefore none of the ambient recorder —
                    // travels with it. Without this adoption every phase the bake stamps is dropped on the floor
                    // and the whole bake reports as one unattributed block.
                    using var adoption = Sts2RenderPhaseProfile.Adopt(recorder);

                    // THE FRAME-PLAN GUARD, here because this is the FIRST point on the main thread and the
                    // engine reads it needs are main-thread reads. A whole-clip bake on the dummy backend is
                    // refused before a scene is loaded: the re-mint below corrects the ACQUISITION pose only, so
                    // every other frame would come back as that pose while the bake still reported complete —
                    // and both admission rules are frame-blind, so the store would take it. See
                    // Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal for the measurement.
                    var wholeClipRefusal = Sts2SpineGeoClipRequestLane.RefuseWholeClipOnDummyBackend(
                        config,
                        ReadsAsDummyRenderer());
                    if (wholeClipRefusal is not null)
                    {
                        Log(logStream, $"REFUSED before baking: {wholeClipRefusal.Poses[0].FailureReason}");
                        return wholeClipRefusal;
                    }

                    return await BakeRigOnMainThreadAsync(config, config.Targets, logStream);
                }

                var outcome = Sts2MainThreadDispatcher
                    .InvokeAsync(RunOnMainThreadAsync)
                    .GetAwaiter()
                    .GetResult();
                return Sts2SpineGeoClipRequestLane.ToSnapshot(outcome);
            }
            finally
            {
                Sts2RenderPhaseProfile.Complete();
            }
        }
        catch (Exception ex)
        {
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                $"the geoclip bake threw {ex.GetType().Name}: {ex.Message}",
                [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message)]);
        }
    }

    // Neither lane's dispatch carries a request id — the env lane has no request at all, and the on-demand
    // snapshot never had one — so the phase recorder is given a minted one. It has to be UNIQUE per bake
    // (Sts2RenderPhaseProfile retains completed snapshots by id, so a repeated id silently overwrites) and
    // legible in a log, hence the rig's own artifact directory name plus a process-wide sequence.
    /// <summary>
    /// The three backend names the re-mint arm switch and the whole-clip guard both key on. ONE reader, so the
    /// two decisions can never be taken against different answers, and so a future signal is added in one place.
    /// </summary>
    /// <remarks>
    /// MAIN THREAD ONLY, like every other engine read in this file, and each read is individually guarded: a
    /// diagnostic accessor that throws must not fail a bake, and a name that could not be obtained arrives
    /// downstream blank — which <see cref="Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer"/> reads as "not known
    /// to be dummy", i.e. the pre-detection behaviour on both call sites.
    /// </remarks>
    private static (string Driver, string Method, string Display) BackendNames()
        => (TryGet(() => RenderingServer.GetCurrentRenderingDriverName(), string.Empty),
            TryGet(() => RenderingServer.GetCurrentRenderingMethod(), string.Empty),
            TryGet(() => DisplayServer.GetName(), string.Empty));

    /// <summary>Whether this process's renderer is one whose mesh-update entry points are no-ops.</summary>
    private static bool ReadsAsDummyRenderer()
    {
        var backend = BackendNames();
        return Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(backend.Driver, backend.Method, backend.Display);
    }

    private static int _profileSequence;

    private static string MintProfileRequestId(IReadOnlyList<GeoClipTarget> targets)
    {
        var head = targets.Count > 0 ? targets[0] : null;
        var name = head is null
            ? "empty"
            : Sts2SpineGeoClipSpec.DirectoryName(
                head.ScenePath, head.NodePath ?? ".", head.Animation ?? string.Empty);
        return $"geoclip:{name}#{Interlocked.Increment(ref _profileSequence).ToString(CultureInfo.InvariantCulture)}";
    }

    private static int ReadIntEnv(string name, int fallback, int min, int max)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    // Null means "nobody said", which is NOT the same as a default value: window B's cap is auto-sized from the
    // window's own shape unless an operator overrides it, and a fallback constant would erase that distinction.
    private static int? ReadOptionalIntEnv(string name, int min, int max)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : null;
    }

    // Null means "nobody said", exactly as ReadOptionalIntEnv: an absent `&t=` falls back to the mid/terminal
    // heuristic, which is NOT the same as asking for t=0.
    private static double? ReadOptionalDoubleEnv(string name)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0d
            && !double.IsNaN(parsed)
            && !double.IsInfinity(parsed)
                ? parsed
                : null;
    }

    private static bool ReadBoolEnv(string name, bool fallback)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return raw.Length == 0
            ? fallback
            : raw is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    // ── Orchestration ────────────────────────────────────────────────────────────────────────────────

    private static async Task RunAsync(GeoClipConfig config, ILogStream logStream)
    {
        try
        {
            Directory.CreateDirectory(config.OutDir);
        }
        catch (Exception ex)
        {
            Log(logStream, $"cannot create output dir '{config.OutDir}': {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!await WaitForStableSceneAsync(config, logStream).ConfigureAwait(false))
        {
            Log(logStream, "gave up waiting for a stable scene; nothing was baked.");
            return;
        }

        // ONE TARGET AT A TIME BY DEFAULT, because the env lane is the MEASUREMENT lane: a spec that names eight
        // targets is usually somebody comparing per-target costs, and silently fusing them would change what
        // every recorded number means. `SPIRECTL_SPINE_GEOCLIP_BATCH_RIGS=1` groups consecutive targets that
        // share a (scene, node) into one rig bake, which is what the request lane does unconditionally — so the
        // amortisation can be A/B'd from a launch line, on one build, against the same rig.
        foreach (var group in Sts2SpineGeoClipBatch.GroupTargets(config.Targets, ReadBoolEnv(BatchRigsEnv, false)))
        {
            try
            {
                // One recorder PER RIG BAKE, not one for the whole sweep: the env lane is the measurement lane,
                // and a spec naming eight targets is somebody comparing per-target costs. Same Begin/dispatch-
                // wait/Adopt/Complete shape as the request lane above; the adoption is what makes the stamps land.
                var recorder = Sts2RenderPhaseProfile.Begin(MintProfileRequestId(group));
                try
                {
                    var dispatchWait = Sts2RenderPhaseProfile.Measure(
                        Sts2RenderPhaseProfile.Phase.DispatchWait, blocking: false);

                    // Same implementation the on-demand request lane drives (BakeOnDemand). The env lane discards
                    // the outcome because every one of its fields has already been logged by the bake itself; the
                    // request lane is the one that has to hand it back to a caller.
                    async Task<GeoClipRigBakeOutcome> RunOnMainThreadAsync()
                    {
                        dispatchWait.Dispose();
                        using var adoption = Sts2RenderPhaseProfile.Adopt(recorder);
                        return await BakeRigOnMainThreadAsync(config, group, logStream);
                    }

                    _ = await Sts2MainThreadDispatcher
                        .InvokeAsync(RunOnMainThreadAsync)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Sts2RenderPhaseProfile.Complete();
                }
            }
            catch (Exception ex)
            {
                Log(
                    logStream,
                    $"target '{string.Join(" ", group.Select(target => target.Raw))}' aborted: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        Log(logStream, "bake complete; baker disarmed.");
    }


    // Same readiness the geometry probe waits for: a live main-thread dispatcher plus a SceneTree that has
    // actually mounted a scene, then a settle delay because the first mounted scene is still streaming.
    private static async Task<bool> WaitForStableSceneAsync(GeoClipConfig config, ILogStream logStream)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (!Sts2MainThreadDispatcher.DescribeStatus().HasCapturedContext)
            {
                continue;
            }

            if (!OnMainThread(() => Engine.GetMainLoop() is SceneTree { CurrentScene: not null }, false))
            {
                continue;
            }

            Log(logStream, $"scene is up; settling for {config.StartDelaySeconds}s before baking.");
            await Task.Delay(TimeSpan.FromSeconds(config.StartDelaySeconds)).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // ── One rig: N animations, ONE scene load ────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE implementation of a bake, shared by the env-armed one-shot lane (<see cref="RunAsync"/>) and the
    /// on-demand request lane (<see cref="BakeOnDemand"/>). Runs on the game's main thread.
    ///
    /// <para>ONE TARGET IS THE DEGENERATE CASE OF N. Every per-scene step — the scene load, the bracket RID
    /// sweep, the slot↔mesh association, the atlas page export — is a property of the RIG rather than of one
    /// animation, and a pose-only bake is 90-96 % those steps. So the bake takes a LIST of targets that share a
    /// (scene, node) and pays for them once. With one target it does exactly what it did before: the probe
    /// schedule keeps the natural frame order (see <c>Sts2SpineGeoClipBatch.ProbeOrder</c>), nothing is compared
    /// for attachment drift, and the association pairs are the same pairs.</para>
    ///
    /// <para>AND IT CHECKS ITSELF. Sharing an association across poses is only sound while a slot keeps drawing
    /// the same attachment and the rig does not re-mint mesh RIDs when an animation is set. Both are measured
    /// rather than assumed, and any pose that fails either is re-baked in its own scene load — which costs the
    /// status quo, where a silently wrong association would bake one slot's art onto another and the store would
    /// content-address it for ever.</para>
    /// </summary>
    private static async Task<GeoClipRigBakeOutcome> BakeRigOnMainThreadAsync(
        GeoClipConfig config,
        IReadOnlyList<GeoClipTarget> targets,
        ILogStream? logStream)
    {
        if (targets.Count == 0)
        {
            return new GeoClipRigBakeOutcome([], 0, "empty");
        }

        var pass = await BakeRigPassAsync(config, targets, logStream);
        if (pass.Retry.Count == 0)
        {
            return pass.Outcome;
        }

        // THE FALLBACK IS THE OLD PATH, not a repair of the new one: each retried animation is re-baked through
        // this same function with ONE target, which is the single-target bake verbatim — its own scene load, its
        // own sweep, its own association. Whatever the batch measured about the rig is therefore irrelevant to
        // the artifact that ends up on disk.
        var poses = pass.Outcome.Poses.ToList();
        var scenesLoaded = pass.Outcome.ScenesLoaded;
        var elapsedMs = pass.Outcome.ElapsedMs;
        foreach (var animation in pass.Retry)
        {
            var target = targets.FirstOrDefault(
                candidate => string.Equals(candidate.Animation, animation, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            Log(
                logStream,
                $"scene='{target.ScenePath}' anim='{animation}': the rig-wide association does not hold for this "
                + $"pose ({pass.RetryReason}); re-baking it in its OWN scene load.");
            var redone = await BakeRigPassAsync(config, [target], logStream);
            scenesLoaded += redone.Outcome.ScenesLoaded;
            elapsedMs += redone.Outcome.ElapsedMs;
            if (redone.Outcome.Poses.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < poses.Count; i += 1)
            {
                if (string.Equals(poses[i].AnimationName, animation, StringComparison.Ordinal))
                {
                    poses[i] = redone.Outcome.Poses[0] with { Batched = false };
                }
            }
        }

        return new GeoClipRigBakeOutcome(
            poses, scenesLoaded, $"batched+fallback:{pass.RetryReason}", Math.Round(elapsedMs, 3));
    }

    /// <summary>The single-target entry the env lane still drives one target at a time.</summary>
    private static async Task<GeoClipBakeOutcome> BakeTargetOnMainThreadAsync(
        GeoClipConfig config,
        GeoClipTarget target,
        ILogStream? logStream)
    {
        var rig = await BakeRigOnMainThreadAsync(config, [target], logStream);
        return rig.Poses.Count > 0
            ? rig.Poses[0]
            : GeoClipBakeOutcome.Failed("the bake produced no outcome.", animationName: target.Animation ?? string.Empty);
    }

    /// <summary>One pass over one loaded scene, plus the animations it declined to answer for.</summary>
    private sealed record RigPass(GeoClipRigBakeOutcome Outcome, IReadOnlyList<string> Retry, string RetryReason);

    /// <summary>Everything one animation of a rig bake accumulates before its artifact is written.</summary>
    private sealed class AnimationBake(GeoClipTarget target)
    {
        internal GeoClipTarget Target { get; } = target;

        internal string Animation { get; } = target.Animation ?? string.Empty;

        internal double Duration { get; set; }

        internal double[] Times { get; set; } = [];

        internal string SampleTimeSource { get; set; } = string.Empty;

        internal List<string> Notes { get; } = [];

        internal Rect2 RestBounds { get; set; }

        internal List<PoseFrame> Poses { get; } = [];

        /// <summary>Slots that draw a DIFFERENT attachment here than at the pose association was measured at.</summary>
        internal int AttachmentDriftSlots { get; set; }

        internal int StaleMeshFrames { get; set; }

        internal string? Failure { get; set; }

        internal GeoClipBakeOutcome? Outcome { get; set; }
    }

    private static async Task<RigPass> BakeRigPassAsync(
        GeoClipConfig config,
        IReadOnlyList<GeoClipTarget> targets,
        ILogStream? logStream)
    {
        var head = targets[0];
        var batched = targets.Count > 1;
        var rigLabel = batched
            ? $"scene='{head.ScenePath}' rig anims={targets.Count} [{string.Join(",", targets.Select(t => t.Animation))}]"
            : $"scene='{head.ScenePath}' anim='{head.Animation ?? "<missing>"}'";
        var bakes = targets.Select(target => new AnimationBake(target)).ToList();

        // Started before the first refusal path, so an aborted pass still reports what it cost — a rig whose
        // scene will not load is a cost the sweep pays too.
        var stopwatch = Stopwatch.StartNew();

        string AnimLabel(AnimationBake bake) => $"scene='{bake.Target.ScenePath}' anim='{bake.Animation}'";

        RigPass Abort(string reason, int scenesLoaded)
            => new(
                new GeoClipRigBakeOutcome(
                    [.. bakes.Select(bake => GeoClipBakeOutcome.Failed(reason, animationName: bake.Animation))],
                    scenesLoaded,
                    batched ? "batched" : "single",
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                [],
                string.Empty);

        if (targets.Any(target => string.IsNullOrWhiteSpace(target.Animation)))
        {
            const string Reason = "the target named no `anim=`; the spec is `<scene>?anim=<name>`.";
            Log(logStream, $"skip {rigLabel}: {Reason}");
            return Abort(Reason, 0);
        }

        // The tree itself is held (not just its root) because the scene-tree pause lease below writes
        // `SceneTree.Paused`; a root viewport with no reachable tree is the same refusal it always was.
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        var rootViewport = sceneTree?.Root;
        if (sceneTree is null || rootViewport is null)
        {
            const string Reason = "the main loop exposed no root viewport.";
            Log(logStream, $"skip {rigLabel}: {Reason}");
            return Abort(Reason, 0);
        }

        // `sceneLoad` rather than a `bakeSceneLoad` twin: this is literally the call the render lanes stamp
        // under that name, and minting a second name for one operation would split one metric in two.
        var sceneLoadScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.SceneLoad);
        var sceneLoaded = Sts2AssetExtractProvider.TryLoadSceneAndFindNode(
            head.ScenePath, head.NodePath, out var sceneRoot, out var spineNode, out var failNote);
        sceneLoadScope.Dispose();
        if (!sceneLoaded)
        {
            Log(logStream, $"skip {rigLabel}: {failNote}");
            return Abort(failNote, 0);
        }

        // Must be read BEFORE the detach, which frees the scene root.
        var nodePath = ReferenceEquals(sceneRoot, spineNode)
            ? "."
            : TryGet(() => sceneRoot.GetPathTo(spineNode).ToString(), spineNode.Name.ToString());

        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = new Vector2I(1024, 1024),
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        var probeRids = new List<Rid>();

        // A bake's cost is dominated by AWAITED ENGINE FRAMES, not by compute: a dozen-plus frames at the
        // host's cap of one per 16.7 ms. The main thread is blocked for the whole bake either way, so the
        // frame cap is lifted for its duration and put back — the OBSERVED value, in a `using` that runs
        // after the cleanup below on every path including a throw. See GeoClipFrameRateLease.
        //
        // NOT touched: vertical sync. Sts2BackgroundThrottleHooks pins it ON deliberately, because with it
        // off the engine keeps drawing into a surface a compositor will not release and the whole main
        // thread parks inside the swapchain acquire — exactly the hang this bake would have to survive if it
        // ran while the window was hidden. If vsync rather than Engine.MaxFps turns out to be what holds the
        // loop at 60 Hz, this lever shows no win and the kill-switch below makes that an A/B, not a rebuild.
        using var frameRate = GeoClipFrameRateLease.Acquire(
            GeoClipFrameBudget.FromEnvironment(),
            () => Engine.MaxFps,
            maxFps => Engine.MaxFps = maxFps);
        Log(
            logStream,
            $"{rigLabel}: frame budget maxFps {frameRate.Note} (vsync "
            + $"{TryGet(() => DisplayServer.WindowGetVsyncMode().ToString(), "<unknown>")}, left alone).");

        // WHICH awaited frames get a ForceDraw. Read once per rig pass so every site below decides from the same
        // budget, and logged beside the frame budget because the two together are the frame schedule that any
        // phase profile from this bake has to be read against. See GeoClipDrawBudget for why a settle frame and
        // a pose read do not need the draw and a bracket does.
        var drawBudget = GeoClipDrawBudget.FromEnvironment();
        Log(logStream, $"{rigLabel}: draw budget {drawBudget.Note}.");

        // ── The SCENE-TREE PAUSE, DEFAULT OFF (see GeoClipPauseBudget) ───────────────────────────────────
        //
        // A paused tree stops running the GAME between the bake's awaited frames, which is where the bake's
        // parked milliseconds go. It is acquired AFTER the frame-rate lease so that `using` disposal — which
        // runs in reverse declaration order — gives the pause back FIRST and puts the frame cap back last.
        //
        // TEARDOWN ORDERING, chosen rather than inherited: the rig teardown (`finally` at the bottom of this
        // method) runs BEFORE either `using` disposes, so left alone it would free the offscreen viewport on a
        // still-paused tree. That is why the `finally` opens with an explicit `pause.Release()`. Godot flushes
        // its delete queue on a paused tree, so the teardown would in fact survive the pause — but "the tree is
        // paused exactly while the bake is measuring, and not one statement longer" is a much easier property to
        // reason about than "and also during teardown, which happens to be safe", and the pause is the one thing
        // here that is visible to the person playing. The `using` stays as the guarantee for the paths a `finally`
        // cannot cover.
        var pauseBudget = GeoClipPauseBudget.FromEnvironment();
        using var pause = GeoClipScenePauseLease.Acquire(
            pauseBudget,
            () => sceneTree.Paused,
            paused => sceneTree.Paused = paused,
            // The bake's own subtree has to be exempt from the pause it just took, or the pause freezes the very
            // rig being posed. Set on the SubViewport ONLY on the path that actually paused, so the unpaused
            // path constructs and attaches exactly the node it always did.
            () => viewport.ProcessMode = Node.ProcessModeEnum.Always);

        // The lease's own view of `Paused` is fixed at acquisition; this is the LIVE one, cleared when the probe
        // or the liveness assertion below hands the pause back mid-bake.
        var treePaused = pause.Paused;
        var pauseNote = pause.Note;
        var pauseAlwaysLogged = false;

        // Gated so the DEFAULT-OFF path emits byte-identical logs: an unarmed lever says nothing.
        if (pauseBudget.PauseTree)
        {
            Log(
                logStream,
                $"{rigLabel}: scene-tree pause {pauseNote} "
                + $"(assert={pauseBudget.Assert.ToString().ToLowerInvariant()}).");
        }

        try
        {
            rootViewport.AddChild(viewport);

            // Bracket LOW, taken while the spine node is still parentless: nothing of the skeleton exists yet, so
            // every mesh it later creates carries a strictly larger validator.
            var bracketLow = RenderingServer.MeshCreate();
            probeRids.Add(bracketLow);

            var renderNode = Sts2AssetExtractProvider.DetachSpineForRender(sceneRoot, spineNode);
            viewport.AddChild(renderNode);

            // MAIN-THREAD AFFINITY. Every await from here down is a BARE await on purpose. This body is posted
            // onto the game's captured SynchronizationContext, and the engine refuses tree mutation off that
            // thread; `ConfigureAwait(false)` would opt the continuation out of that context, and because the
            // main thread carries a non-default SynchronizationContext the runtime declines to inline such a
            // continuation — so the rest of the bake would silently resume on a thread-pool thread.
            if (treePaused)
            {
                // THE DEADLOCK PROBE, on the FIRST frame this bake awaits under a pause it took.
                //
                // Everything below here waits on `SceneTree.ProcessFrame`. If a paused tree did not emit that
                // signal, the very next await would never return and the bake would hang holding the game
                // paused — the worst failure this repo can ship. So the first one is raced against a deadline
                // instead of trusted.
                //
                // WHAT IS EXPECTED TO HAPPEN: the signal fires. `ProcessFrame` is emitted by the SceneTree once
                // per main-loop iteration; `paused` gates whether individual NODES process, not whether the loop
                // runs. The SubViewport keeps drawing for the same reason — its update mode drives it and
                // `ForceDraw` is unconditional. This probe exists because "expected" is not "measured", and it
                // has not been measured: the arming gate that would have put this lever in front of a live game
                // is closed on every profile taken so far.
                //
                // The race is safe because the continuation comes back through the bridge's main-thread
                // dispatcher, whose Timer is ProcessMode.Always (Sts2GodotSynchronizationContext) — it drains on
                // a paused tree, so the `Task.Delay` leg can still complete even if the frame leg never does.
                if (!await AwaitFrameWithDeadlineAsync(
                        rootViewport,
                        PauseProbeDeadline,
                        Sts2RenderPhaseProfile.Phase.BakeWarmupWait,
                        // Frame 0 of a 3-frame settle whose LAST frame closes `bracketMid`: this one only
                        // settles, so the budget elides its draw exactly as it elides the settle frames below.
                        forceDraw: drawBudget.ForceDrawAt(GeoClipDrawSite.BracketSettle, 0, 3)))
                {
                    treePaused = false;
                    pause.Release();
                    pauseNote = $"released (no frame within {PauseProbeDeadline.TotalMilliseconds:0}ms of pausing)";
                    GeoClipPauseDisarm.Latch("a paused tree emitted no ProcessFrame within the probe deadline");
                    Log(
                        logStream,
                        $"{rigLabel}: SCENE-TREE PAUSE REFUSED — no ProcessFrame arrived within "
                        + $"{PauseProbeDeadline.TotalMilliseconds:0}ms of pausing the tree, so awaiting frames "
                        + "under pause would have hung the game. The pause has been handed back, this bake "
                        + "continues unpaused, and the lever is DISARMED for the rest of this process: one bake "
                        + "pays the deadline, never a whole sweep.");
                }

                // The probe consumed the first of the three warmup frames on the paused path, so the frame COUNT
                // is the same either way — and so is the DRAW count, because the probe above took frame 0's
                // decision from the same budget and these two carry frames 1 and 2 of the same settle.
                await AwaitBudgetedFramesAsync(
                    rootViewport,
                    2,
                    drawBudget,
                    GeoClipDrawSite.BracketSettle,
                    Sts2RenderPhaseProfile.Phase.BakeWarmupWait);
            }
            else
            {
                await AwaitBudgetedFramesAsync(
                    rootViewport,
                    3,
                    drawBudget,
                    GeoClipDrawSite.BracketSettle,
                    Sts2RenderPhaseProfile.Phase.BakeWarmupWait);
            }

            // Bracket MID (Phase 1 called this one `bracketHigh`, and it is still the upper edge of window A —
            // the window whose answer the recorded Phase-1 numbers grade). It is NOT the end of mesh creation:
            // setting an animation is where the skeleton mints the slot meshes it had not needed yet, and a rig
            // bake sets EVERY requested animation below (14 of the merchant's 44 slots and 2 of byrdonis's 28
            // are minted exactly there). Window B, below, covers everything above this edge.
            var bracketMid = RenderingServer.MeshCreate();
            probeRids.Add(bracketMid);

            // The lane is re-prepared on every animation switch and never reused across one: setting an animation
            // returns a NEW track entry and the old one must not be touched again (re-reading a stale entry is a
            // known uncatchable crash). `current` is the only entry any code below is allowed to hold.
            GeoClipLane? current = null;
            var currentAnimation = string.Empty;

            // The measured slot↔mesh association, once PASS 2 has produced one. Declared HERE, above `Use`,
            // purely so the local function can read it: a local function cannot capture a variable declared
            // after it, and the census below has to run inside `Use` because the animation SET is the event it
            // is measuring. Null until PASS 2 finishes, which is what makes the census skip itself.
            IReadOnlyCollection<ulong>? associatedMeshes = null;

            GeoClipLane? Use(string animation, string label)
            {
                if (current is not null && string.Equals(currentAnimation, animation, StringComparison.Ordinal))
                {
                    return current;
                }

                // RIDER 2: the LIVENESS CENSUS, on the one event that can invalidate an association. Setting an
                // animation is where the skeleton mints (and could recycle) slot meshes, and `staleMeshFrames`
                // only notices a recycled RID that reads back EMPTY — a recycled RID that reads back POPULATED
                // is the same fault with no symptom. Counting how many ASSOCIATED mesh RIDs still resolve, either
                // side of the switch, is the direct measurement. Skipped silently before an association exists
                // (PASS 1 has nothing to count), costs no awaited frame, and is a surface-count hop per mesh
                // (~0.019 µs live); the probes that fail are covered by the armed backtrace suppressor.
                var liveBefore = CountLiveMeshes(associatedMeshes);

                // `lanePrepare`, not a bake twin: the render lanes stamp the same "realize a spine lane" step
                // under that name. Stamped at the SWITCH rather than per call, because a cached hit above did
                // no work to price.
                GeoClipLane? prepared;
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LanePrepare))
                {
                    prepared = TryPrepareLane(spineNode, animation, logStream, label);
                }

                if (prepared is null)
                {
                    return null;
                }

                if (associatedMeshes is { Count: > 0 })
                {
                    Log(
                        logStream,
                        $"{label}: anim='{animation}' set; liveMeshes={CountLiveMeshes(associatedMeshes)}/"
                        + $"{associatedMeshes.Count} (was {liveBefore}/{associatedMeshes.Count} before the "
                        + "switch). A drop here means the rig re-minted or recycled an ASSOCIATED mesh RID, "
                        + "which invalidates the shared association for every pose after it.");
                }

                // BELT AND BRACES for the paused path. Setting an animation is where the skeleton MINTS the slot
                // meshes that animation needs (the same event the census above measures), and a node minted after
                // the pause was taken never saw the `Always` that was set on the SubViewport. Inheritance covers
                // the common case — a child left on `Inherit` resolves through its ancestors to `Always` — so
                // this only has to catch a node that names its own mode, and on a paused tree naming `Pausable`
                // means "frozen".
                //
                // WHAT IT REWRITES, and what it leaves alone: `Pausable` becomes `Always` (it would have run on
                // an unpaused bake and must run here), and `WhenPaused` becomes `Disabled` (it would NOT have
                // run on an unpaused bake and must not start now). `Disabled` and `Always` are left exactly as
                // authored. The rule is "the pause changes NOTHING about what gets baked", which is why this is
                // not simply "force everything to Always". Nothing is restored, because this whole subtree is
                // detached and queue-freed at teardown.
                if (treePaused)
                {
                    var forcedAlways = ForceRigProcessingUnderPause(viewport);
                    if (forcedAlways > 0 && !pauseAlwaysLogged)
                    {
                        pauseAlwaysLogged = true;
                        Log(
                            logStream,
                            $"{rigLabel}: scene-tree pause re-armed {forcedAlways} rig node(s) that named their "
                            + "own process mode; a lazily-minted mesh node would otherwise have been frozen by "
                            + "the pause and baked as a still frame. Logged once per rig.");
                    }
                }

                current = prepared;
                currentAnimation = animation;
                return prepared;
            }

            var firstLane = Use(bakes[0].Animation, AnimLabel(bakes[0]));
            if (firstLane is null)
            {
                return Abort("the animation lane could not be prepared; see the log above.", 1);
            }

            var skeletonObject = TryGet(() => firstLane.Sprite.GetSkeleton()?.BoundObject);
            if (skeletonObject is null)
            {
                const string Reason = "the SpineSprite exposed no skeleton object.";
                Log(logStream, $"skip {rigLabel}: {Reason}");
                return Abort(Reason, 1);
            }

            var slotTable = ReadSlotTable(skeletonObject, logStream, rigLabel);
            if (slotTable.Count == 0)
            {
                const string Reason = "the skeleton exposed no slots.";
                Log(logStream, $"skip {rigLabel}: {Reason}");
                return Abort(Reason, 1);
            }

            var pathConstraintTargetSlots = ReadPathConstraintTargetSlots(skeletonObject, logStream, rigLabel);

            // The atlas is read HERE rather than after association, because association now uses it: an
            // attachment names a region, and a region names a box on a page, so the atlas is independent evidence
            // about which mesh belongs to which slot. Reading it early costs nothing (it is disk + parse) and the
            // page export below still consumes the same document.
            SpineAtlasDocument atlas;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeAtlasRead))
            {
                atlas = ReadAtlasDocument(spineNode, logStream, rigLabel);
            }

            var atlasPageSizes = atlas.Pages.Select(page => (page.Width, page.Height)).ToArray();
            var atlasFallbackAvailable = atlas.Regions.Count > 0
                && atlasPageSizes.Any(size => size.Width > 0 && size.Height > 0);
            if (!atlasFallbackAvailable)
            {
                Log(
                    logStream,
                    $"{rigLabel}: the atlas offers no usable region/page table (regions={atlas.Regions.Count} "
                    + $"pages={atlasPageSizes.Length}), so association has no atlas fallback on this rig.");
            }

            // ── THE PRE-PASS: every animation SET, and PASS 1 captured, BEFORE bracketPost ────────────────
            //
            // Both halves have to happen up here. Setting an animation mints the slot meshes that animation
            // needs, and PASS 1's track seeks can mint more; window B only spans RIDs below bracketPost, so
            // anything minted after it is invisible to the sweep and its slot is lost.
            //
            // PASS 1 reads pose state only — attachment / colour / draw order / bounds all come off the
            // skeleton, no mesh RID is known yet and none is needed.
            //
            // A1 — the FREE half of the paused bake's liveness assertion — rides along here: PASS 1 already reads
            // a skeleton bounds per pose, so comparing two of them costs nothing but the comparison. `Passed` is
            // sticky across the rig's animations; `Inconclusive` is not a failure and never becomes one on its
            // own (see GeoClipPauseLivenessCheck), it is the trigger for the two-frame check after association.
            var boundsVerdict = GeoClipPauseLiveness.Inconclusive;
            foreach (var bake in bakes)
            {
                var animLabel = AnimLabel(bake);
                var lane = Use(bake.Animation, animLabel);
                if (lane is null)
                {
                    bake.Failure = "the animation lane could not be prepared; see the log above.";
                    continue;
                }

                bake.Duration = lane.Duration;

                // THE FRAME CLOCK. Either the whole animation at `fps`, or — POSE-ONLY — exactly one time, chosen
                // by the same function the raster still renderer uses (Sts2SpineStillFrame.ChooseSample: an
                // explicit `&t=` wins, else the LAST frame of a terminal die/defeat animation, else the mid
                // point). Calling that function rather than restating its rule is the entire reason a pose-only
                // geoclip and the still for the same identity show the same pose.
                //
                // With one time, PASS 1 and PASS 3 visit one pose and acquisition happens AT it. Correct — a slot
                // with no attachment at the sampled pose has no surface to validate and contributes nothing — but
                // it narrows `slotsEverVisible` to "visible at THIS pose", which the report and the manifest both
                // say.
                var poseOnlyHere = config.IsPoseOnly(bake.Target);
                var requestedSeconds = config.RequestedSampleTime(bake.Target);
                if (poseOnlyHere)
                {
                    var sample = Sts2SpineStillFrame.ChooseSample(
                        bake.Animation,
                        (float)lane.Duration,
                        requestedSeconds is { } seconds ? (float)seconds : null);
                    bake.Times = [sample.Seconds];
                    bake.SampleTimeSource = sample.Source;
                    bake.Notes.Add(
                        $"POSE-ONLY bake: one frame at t={sample.Seconds:0.#####}s, chosen by '{sample.Source}'. "
                        + "`bake.slotsEverVisible` therefore counts slots visible AT THIS POSE, not anywhere in the "
                        + "animation, and `bake.complete` is scoped the same way.");
                    Log(
                        logStream,
                        $"{animLabel}: POSE-ONLY t={sample.Seconds:0.#####}s source={sample.Source} "
                        + $"(duration={lane.Duration:0.###}s, requested="
                        + $"{requestedSeconds?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "<auto>"})");
                }
                else
                {
                    bake.SampleTimeSource = Sts2SpineGeoClipMath.SampleSourceWholeClip;
                    if (Sts2SpineGeoClipMath.FrameTimesWereTruncated(lane.Duration, config.Fps, config.MaxFrames))
                    {
                        Log(
                            logStream,
                            $"{animLabel}: {lane.Duration:0.###}s at {config.Fps}fps exceeds the "
                            + $"{config.MaxFrames}-frame cap; the tail of the animation is NOT baked.");
                    }

                    bake.Times = Sts2SpineGeoClipMath.FrameTimes(lane.Duration, config.Fps, config.MaxFrames);
                }

                Log(
                    logStream,
                    $"{animLabel}: slots={slotTable.Count} duration={lane.Duration:0.###}s "
                    + $"frames={bake.Times.Length} @ {config.Fps}fps");

                // REST BOUNDS, for the placement fallback only. Read at t=0 before PASS 1 seeks anywhere, and
                // only for a pose-only bake, mirroring the raster still lane: a posed read that comes back
                // degenerate must not size the canvas, but a whole-clip bake unions many frames and has no single
                // pose to fall back FROM.
                if (poseOnlyHere)
                {
                    TryRun(() => lane.Entry.SetTrackTime(0f));
                    await AwaitBudgetedFramesAsync(
                        rootViewport,
                        1,
                        drawBudget,
                        GeoClipDrawSite.PoseRead,
                        Sts2RenderPhaseProfile.Phase.BakePoseWait);
                    bake.RestBounds = TryGet(() => lane.Skeleton.GetBounds(), default);
                }

                foreach (var t in bake.Times)
                {
                    TryRun(() => lane.Entry.SetTrackTime((float)t));

                    // PASS 1 reads SKELETON state only — attachment, colour, draw order, bounds — every one of
                    // which is written in `_process` and is therefore current the moment `ProcessFrame` has
                    // fired. No mesh RID is known here and none is read, so there is nothing for a forced draw
                    // to make current.
                    await AwaitBudgetedFramesAsync(
                        rootViewport,
                        1,
                        drawBudget,
                        GeoClipDrawSite.PoseRead,
                        Sts2RenderPhaseProfile.Phase.BakePoseWait);
                    bake.Poses.Add(ReadPose(skeletonObject, lane, slotTable, pathConstraintTargetSlots, t));
                }

                // A1, on bounds this loop has already read. A pose-only bake compares its rest bounds (t=0)
                // against its one posed bounds; a whole-clip bake compares its first sampled pose against its
                // last, which is the same question asked of the reads it happens to have.
                if (treePaused && boundsVerdict != GeoClipPauseLiveness.Passed && bake.Poses.Count > 0)
                {
                    boundsVerdict = GeoClipPauseLivenessCheck.FromBounds(
                        poseOnlyHere ? BoundsArray(bake.RestBounds) : bake.Poses[0].Bounds,
                        bake.Poses[^1].Bounds);
                }
            }

            var live = bakes.Where(bake => bake.Failure is null && bake.Poses.Count > 0).ToList();
            if (live.Count == 0)
            {
                const string Reason = "no requested animation produced a pose.";
                Log(logStream, $"skip {rigLabel}: {Reason}");
                return Abort(Reason, 1);
            }

            // ── ACQUISITION: the pose showing the most slots, across every animation in the pass ──────────
            var stops = new List<(AnimationBake Bake, int Frame)>();
            foreach (var bake in live)
            {
                for (var frame = 0; frame < bake.Poses.Count; frame += 1)
                {
                    stops.Add((bake, frame));
                }
            }

            LogAttachmentClassificationObservations(live.SelectMany(bake => bake.Poses), logStream, rigLabel);

            var visibleAt = stops
                .Select(stop => stop.Bake.Poses[stop.Frame].Slots.Count(slot => slot.RequiresDrawing))
                .ToArray();
            var acquisitionStop = Sts2SpineGeoClipBatch.ChooseAcquisitionStop(visibleAt);
            var bestVisible = visibleAt.Length > 0 ? visibleAt[acquisitionStop] : 0;
            var acquisition = stops[acquisitionStop];

            Log(
                logStream,
                $"{rigLabel}: acquiring mesh RIDs at anim='{acquisition.Bake.Animation}' frame "
                + $"{acquisition.Frame} (t={acquisition.Bake.Times[acquisition.Frame]:0.####}s), where "
                + $"{bestVisible}/{slotTable.Count} slots show a drawable attachment.");

            // ── ATTACHMENT DRIFT, from PASS 1 data alone, before a single mesh has been swept ─────────────
            //
            // A slot that draws attachment A where the association is measured and attachment B somewhere else
            // may be drawn by a different mesh there, and `staleMeshFrames` only catches that when the old mesh
            // reads back EMPTY rather than stale-but-populated. Only a BATCH is checked: a single-target bake
            // associates at a frame of its own animation, which is the behaviour every recorded association pair
            // was graded against, and applying a new refusal to it would change answers this round must not
            // change.
            var acquisitionAttachments = AttachmentsAt(acquisition.Bake.Poses[acquisition.Frame]);
            if (batched)
            {
                foreach (var bake in live)
                {
                    var drifted = new HashSet<int>();
                    foreach (var pose in bake.Poses)
                    {
                        foreach (var ordinal in Sts2SpineGeoClipBatch.AttachmentDrift(
                                     acquisitionAttachments, AttachmentsAt(pose)))
                        {
                            drifted.Add(ordinal);
                        }
                    }

                    bake.AttachmentDriftSlots = drifted.Count;
                    if (drifted.Count > 0)
                    {
                        Log(
                            logStream,
                            $"{AnimLabel(bake)}: {drifted.Count} slot(s) draw a different attachment here than at "
                            + $"anim='{acquisition.Bake.Animation}', where the rig-wide association is measured "
                            + $"(ordinals {string.Join(",", drifted.OrderBy(o => o).Take(12))}"
                            + (drifted.Count > 12 ? ",…" : string.Empty)
                            + "); this pose is left to a per-target bake.");
                    }
                }
            }

            var kept = live.Where(bake => bake.AttachmentDriftSlots == 0).ToList();
            if (kept.Count == 0)
            {
                // Nothing to associate for, but the drifting poses still have to be answered — the caller gets
                // them from the retry list below.
                Log(logStream, $"{rigLabel}: every requested pose drifts; the whole rig falls back to per-target.");
                return new RigPass(
                    new GeoClipRigBakeOutcome(
                        [.. bakes.Select(bake => bake.Outcome ?? GeoClipBakeOutcome.Failed(
                            "attachment drift; awaiting a per-target bake",
                            animationName: bake.Animation))],
                        1,
                        "batched",
                        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                    [.. live.Select(bake => bake.Animation)],
                    $"attachmentDrift={live.Sum(bake => bake.AttachmentDriftSlots)}");
            }

            var acquisitionTime = (float)acquisition.Bake.Times[acquisition.Frame];
            TryRun(() => Use(acquisition.Bake.Animation, AnimLabel(acquisition.Bake))?.Entry
                .SetTrackTime(acquisitionTime));

            // ── THE HEADLESS MESH-POOL RE-MINT, env-armed and OFF by default ──────────────────────────────
            //
            // Sts2SpineGeoClipHeadlessRemint carries WHY this exists. The mechanics, which live here because
            // only the baker knows the ordering they depend on:
            //
            //   1. A probe RID is taken FIRST. Every mesh the rebuilt pool creates is allocated after it, so
            //      window B can be planned from here instead of from `bracketMid` and then covers the new pool
            //      exactly — without which the sweep would be hunting a pool that no longer exists.
            //   2. Re-setting the sprite's own skeleton data resource frees every slot mesh AND replaces the
            //      skeleton and animation state. Everything the baker holds across that call is dangling
            //      afterwards, so the lane cache is dropped, the lane is re-prepared, and `skeletonObject` is
            //      re-read from the NEW skeleton. Reading a stale spine object is a known uncatchable crash.
            //   3. The rebuild leaves the skeleton in its SETUP pose. Steps 2-4 are deliberately synchronous:
            //      the first draw after a rebuild is the CREATE that materialises every mesh, and if a queued
            //      redraw ran before the acquisition pose were re-applied the whole rig would be baked at the
            //      setup pose with nothing able to correct it afterwards.
            //   4. An explicit INTERNAL_PROCESS notification runs the sprite's own skeleton update at the
            //      re-applied pose, so the mesh instances hold acquisition-pose vertices BEFORE any draw. Both
            //      the idle and the physics notification are sent; the sprite acts on whichever matches its
            //      update mode and ignores the other.
            //
            // THE DEFAULT IS THE RENDERER (Sts2SpineGeoClipHeadlessRemint.Decide): unset, the lever arms itself
            // exactly on the backend whose two mesh-update entry points are no-ops, and nowhere else. So the
            // decision is LOGGED on both paths — an armed bake used to be the only one that said anything, which
            // was fine while arming took an explicit variable and is not fine now that it does not.
            Rid? remintBracket = null;
            // ordinal -> the colour this bake WROTE to that slot for the duration of the re-mint's create pass,
            // and the colour it was holding beforehand. Both stay empty unless the mint mark is armed AND the
            // acquisition pose actually has two slots drawing one attachment; see Sts2SpineGeoClipHeadlessMintMark.
            var mintMarks = new Dictionary<int, double[]>();
            var mintOriginals = new Dictionary<int, Color>();
            // The three backend names the arm switch keys on, read HERE because the switch itself is compiled into
            // the Godot-free build. Each read is guarded: a bake must not fail because a diagnostic accessor
            // threw, and a name that could not be obtained reads downstream as "not known to be dummy", which is
            // the pre-detection behaviour.
            var backend = BackendNames();
            var remint = Sts2SpineGeoClipHeadlessRemint.Decide(
                backend.Driver,
                backend.Method,
                backend.Display);
            Log(logStream, $"{rigLabel}: {remint.Description}.");
            if (remint.Armed)
            {
                var bracket = RenderingServer.MeshCreate();
                probeRids.Add(bracket);
                remintBracket = bracket;

                var poolBefore = CountSpineMeshChildren(spineNode);
                var rebuilt = TryGet(
                    () =>
                    {
                        var res = spineNode.Call("get_skeleton_data_res");
                        spineNode.Call("set_skeleton_data_res", res);
                        return true;
                    },
                    false);

                if (!rebuilt)
                {
                    Log(
                        logStream,
                        $"{rigLabel}: HEADLESS RE-MINT REFUSED — re-setting the sprite's skeleton data resource "
                        + "threw, so the mesh pool was NOT rebuilt and this bake reads whatever the meshes "
                        + "already held. Nothing else was changed.");
                    remintBracket = null;
                }
                else
                {
                    current = null;
                    currentAnimation = string.Empty;
                    var remintLane = Use(acquisition.Bake.Animation, AnimLabel(acquisition.Bake));
                    if (remintLane is null)
                    {
                        return Abort(
                            "the animation lane could not be re-prepared after the headless mesh-pool re-mint.",
                            1);
                    }

                    TryRun(() => remintLane.Entry.SetTrackTime(acquisitionTime));

                    // ── THE MINT MARK, written INSIDE the re-mint bracket and nowhere else ────────────────
                    //
                    // The re-mint's create pass is the one moment a slot's colour is written into a surface
                    // headless, so a distinguishing colour has to be standing here — after the pose is
                    // re-applied (the animation's own colour timeline would otherwise overwrite it during the
                    // notification below) and before the notification that recomputes the mesh instances. It is
                    // restored the moment the create pass has been drawn, which is why the arm is inert on a
                    // real renderer: there the restore lands and the mark never reaches an association read.
                    if (Sts2SpineGeoClipHeadlessMintMark.Armed(true))
                    {
                        ApplyMintMarks(
                            TryGet(() => remintLane.Sprite.GetSkeleton()?.BoundObject),
                            acquisition.Bake.Poses[acquisition.Frame],
                            mintMarks,
                            mintOriginals,
                            logStream,
                            rigLabel);
                    }

                    TryRun(() => spineNode.Notification((int)Node.NotificationInternalProcess));
                    TryRun(() => spineNode.Notification((int)Node.NotificationInternalPhysicsProcess));

                    var reskeleton = TryGet(() => remintLane.Sprite.GetSkeleton()?.BoundObject);
                    if (reskeleton is null)
                    {
                        return Abort(
                            "the rebuilt SpineSprite exposed no skeleton object after the headless re-mint.",
                            1);
                    }

                    skeletonObject = reskeleton;
                    Log(
                        logStream,
                        $"{rigLabel}: HEADLESS RE-MINT — the sprite's skeleton data resource was re-set, freeing "
                        + $"the slot mesh pool ({poolBefore} -> {CountSpineMeshChildren(spineNode)} mesh child "
                        + $"node(s)) and re-posing at t={acquisitionTime:0.####}s before any draw, so the next "
                        + "draw creates every slot surface afresh instead of patching a vertex region the dummy "
                        + "renderer discards. Window B is planned from this bracket. NOTE: only the ACQUISITION "
                        + "pose is corrected; a whole-clip bake still reads later frames off meshes nothing "
                        + "re-created.");
                }
            }

            // The LAST of these two closes `bracketPost` below, so it keeps its draw whatever the budget says:
            // a mesh this re-seek minted and that has not been drawn yet could be allocated above the bracket
            // and would then be invisible to the sweep. The first frame only settles.
            await AwaitBudgetedFramesAsync(
                rootViewport,
                2,
                drawBudget,
                GeoClipDrawSite.BracketSettle,
                Sts2RenderPhaseProfile.Phase.BakeAcquireWait);

            // THE RESTORE, and its placement is the whole reason the mark is safe. It is AFTER the settle
            // bracket, because the create pass that has to carry the mark happens on the first draw the bracket
            // closes, not on the notification that filled the mesh instances. It is BEFORE the pose-parity
            // re-read below, so that check still compares the rig's own colours against PASS 1 and would report
            // a mark this bake failed to take back. On a real renderer the next draw patches the restored colour
            // into the attribute region and the mark is gone before anything reads a mesh colour; headless that
            // patch is the write the dummy backend discards, which is exactly what leaves the mark readable.
            if (mintOriginals.Count > 0)
            {
                RestoreMintMarks(skeletonObject, mintOriginals, logStream, rigLabel);
            }

            if (remintBracket is not null)
            {
                // The rebuild is only sound if the rig came back POSED THE SAME. Re-read the pose the ordinary
                // path already recorded in PASS 1 and say plainly how far the rebuilt skeleton is from it — a
                // skin, a slot colour or a draw order the scene had set and the rebuild reset would show up
                // here rather than as an unexplained geometry diff later.
                var remintLane = Use(acquisition.Bake.Animation, AnimLabel(acquisition.Bake));
                var reread = remintLane is null
                    ? null
                    : TryGet(() => ReadPose(
                        skeletonObject,
                        remintLane,
                        slotTable,
                        pathConstraintTargetSlots,
                        acquisition.Bake.Times[acquisition.Frame]));
                Log(
                    logStream,
                    $"{rigLabel}: HEADLESS RE-MINT pose parity — "
                    + (reread is null
                        ? "the re-posed skeleton could not be re-read, so the rebuild is UNVERIFIED."
                        : DescribePoseParity(acquisition.Bake.Poses[acquisition.Frame], reread)));
            }

            // Bracket POST, taken once the skeleton has been animated and posed: every mesh the rig was ever
            // going to mint exists by now. Nothing after this point SHOULD mint another — the association pass
            // only WRITES slot colours (which cannot create an attachment) and PASS 3 revisits track times PASS 1
            // has already visited — but the `staleMeshFrames` counter below checks that claim rather than
            // assuming it, and on a rig bake acting on it is what the per-target fallback is for.
            var bracketPost = RenderingServer.MeshCreate();
            probeRids.Add(bracketPost);

            var skeletonBox = Sts2SpineGeoClipMath.SkeletonBox.FromBoundsArray(
                acquisition.Bake.Poses[acquisition.Frame].Bounds);
            var sweep = await SweepMeshRidsAsync(
                rootViewport,
                [
                    // BOTH index-recovery strips are resolved HERE rather than from the config, so the two kill
                    // switches bite on BOTH lanes: the on-demand /geoclips/ lane hard-codes its PlanConfig
                    // (IndexSlack included), which is exactly why the index axis could not be A/B'd live and the
                    // whole WS-I diagnosis had to run on the env-armed one-shot lane instead.
                    Sts2SpineGeoClipSweep.PlanWindowA(
                        bracketLow.Id,
                        bracketMid.Id,
                        config.IndexSlack,
                        config.CandidateCap,
                        indexFloorRecovery: Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryArmed(),
                        indexCeilingRecovery: Sts2SpineGeoClipSweep.IndexCeilingRecoveryArmed()),
                    Sts2SpineGeoClipSweep.PlanWindowB(
                        // The re-mint's own bracket when it ran, `bracketMid` otherwise. After a pool rebuild
                        // every live slot mesh is younger than that probe, and window B's cap covers a bounded
                        // number of validator rows measured UP from its low edge — so planning from the older
                        // bracket would spend the whole budget below the pool it is looking for.
                        remintBracket?.Id ?? bracketMid.Id,
                        bracketPost.Id,
                        config.IndexSlack,
                        config.CandidateCap,
                        config.WindowBCap,
                        indexCeilingRecovery: Sts2SpineGeoClipSweep.IndexCeilingRecoveryArmed()),
                ],
                skeletonBox,
                config,
                probeRids,
                // What the cheap acquisition arm has to deliver before the dense sweep is allowed to stay
                // unarmed: one mesh per slot showing an attachment at the acquisition frame. At or ABOVE the
                // number `complete` is graded on — equal in general, and larger when the single-pose
                // transparency arm drops a slot from the expectation — so the arm can never be satisfied by less
                // than a complete bake. Deliberately NOT narrowed by that arm: this is a coverage floor for the
                // sweep, and keeping it high only makes the sweep look harder, never admits more.
                bestVisible,
                logStream,
                rigLabel);
            var meshRids = sweep.Meshes;
            if (meshRids.Count == 0)
            {
                const string Reason = "the bracket sweep validated no slot mesh, so nothing could be read back.";
                Log(logStream, $"skip {rigLabel}: {Reason}");
                return new RigPass(
                    new GeoClipRigBakeOutcome(
                        [.. bakes.Select(bake => GeoClipBakeOutcome.Failed(
                            Reason,
                            bake.Times.Length > 0 ? bake.Times[0] : 0d,
                            bake.SampleTimeSource,
                            bake.Animation))],
                        1,
                        batched ? "batched" : "single",
                        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                    [],
                    string.Empty);
            }

            // ── PASS 2 — which mesh is which slot, ONCE for the rig ───────────────────────────────────────
            //
            // The probe stops are the KEPT animations' sampled poses, flattened. A slot is probed at the first
            // stop in `probeOrder` that shows it, so with the acquisition stop first a rig whose widest pose
            // covers every slot pays exactly one probe group for the whole batch. A single-target bake keeps the
            // natural frame order, which is what makes its pairs the same pairs as before.
            var probeStops = new List<(AnimationBake Bake, int Frame)>();
            foreach (var bake in kept)
            {
                for (var frame = 0; frame < bake.Poses.Count; frame += 1)
                {
                    probeStops.Add((bake, frame));
                }
            }

            var acquisitionProbeStop = probeStops.FindIndex(
                stop => ReferenceEquals(stop.Bake, acquisition.Bake) && stop.Frame == acquisition.Frame);
            var probeOrder = Sts2SpineGeoClipBatch.ProbeOrder(
                probeStops.Count, Math.Max(0, acquisitionProbeStop), batched);
            var unionPoses = probeStops.Select(stop => stop.Bake.Poses[stop.Frame]).ToList();

            var association = await AssociateSlotMeshesAsync(
                rootViewport,
                stop =>
                {
                    var (bake, frame) = probeStops[stop];
                    var lane = Use(bake.Animation, AnimLabel(bake));
                    TryRun(() => lane?.Entry.SetTrackTime((float)bake.Times[frame]));
                },
                probeOrder,
                unionPoses,
                skeletonObject,
                slotTable,
                meshRids,
                atlas,
                atlasPageSizes,
                logStream,
                rigLabel,
                config.ForeignCandidateDiagnosticsLimit,
                drawBudget,
                // The rig is posed at the acquisition stop and has been settled there since the re-seek above:
                // the sweep between only reads meshes, and nothing has written a track time or switched an
                // animation since. That is the claim ProbeSeekFramesNeeded elides the first group's seek on.
                settledStop: acquisitionProbeStop,
                mintMarks: mintMarks);
            // From here on the liveness census inside `Use` has something to count (PASS 3 re-sets an animation
            // per pose, and that is the one event that can invalidate what was just measured).
            associatedMeshes = association.BySlotOrdinal.Values.Distinct().ToList();
            if (association.BySlotOrdinal.Count == 0)
            {
                const string Reason = "no slot could be associated with a mesh.";
                Log(logStream, $"skip {rigLabel}: {Reason}");
                return new RigPass(
                    new GeoClipRigBakeOutcome(
                        [.. bakes.Select(bake => GeoClipBakeOutcome.Failed(
                            Reason,
                            bake.Times.Length > 0 ? bake.Times[0] : 0d,
                            bake.SampleTimeSource,
                            bake.Animation))],
                        1,
                        batched ? "batched" : "single",
                        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                    [],
                    string.Empty);
            }

            // ── A3 — the PAID half of the paused bake's liveness assertion ───────────────────────────────
            //
            // TWO FRAMES, and only when the free check could not confirm the rig moved (or when ASSERT=strict
            // buys the confirmation anyway). It runs HERE because it needs the association: the meshes it reads
            // are the ones the artifact is actually built from, which is the difference between "something in the
            // scene changed" and "the geometry this bake is about to write down changed".
            //
            // WHY THE ASSERTION EXISTS AT ALL, and why the atlas-first arm made it mandatory: association used to
            // depend on the colour probe, which writes a slot colour and reads the mesh back — a frozen rig would
            // fail that outright and the bake would collapse loudly. With the atlas claiming associations before
            // any probe frame, that implicit liveness net is gone, and a frozen rig can reach a complete-looking
            // artifact in which every frame is the same pose.
            var checksumVerdict = (GeoClipPauseLiveness?)null;
            if (treePaused && GeoClipPauseLivenessCheck.NeedsChecksumCheck(boundsVerdict, pauseBudget.Assert))
            {
                checksumVerdict = await CheckPausedRigMovesAsync(
                    rootViewport,
                    Use(acquisition.Bake.Animation, AnimLabel(acquisition.Bake)),
                    acquisition.Bake.Times.Length > acquisition.Frame
                        ? acquisition.Bake.Times[acquisition.Frame]
                        : 0d,
                    acquisition.Bake.Duration,
                    associatedMeshes);
                Log(
                    logStream,
                    $"{rigLabel}: scene-tree pause liveness bounds={boundsVerdict.ToString().ToLowerInvariant()} "
                    + $"checksums={checksumVerdict.ToString()?.ToLowerInvariant()} "
                    + $"(assert={pauseBudget.Assert.ToString().ToLowerInvariant()}).");
            }

            if (GeoClipPauseLivenessCheck.PauseIsRefuted(boundsVerdict, checksumVerdict))
            {
                // REFUTED. Everything this pass measured — every pose read, and the association built on those
                // poses — was measured on a rig that did not move, so none of it can be repaired in place and
                // none of it may be written. The pause goes back, the lever disarms itself process-wide, and the
                // whole rig is handed to the per-target fallback, which is the single-target bake verbatim and
                // which the latch now guarantees runs UNPAUSED.
                treePaused = false;
                pause.Release();
                pauseNote = "released (liveness assertion refuted the pause)";
                GeoClipPauseDisarm.RecordAssertFailure("a paused tree froze the rig (liveness assertion A3)");
                const string Reason = "the paused scene tree froze the rig; awaiting a re-bake with the pause off";
                Log(
                    logStream,
                    $"{rigLabel}: SCENE-TREE PAUSE REFUTED — the associated meshes read back identical positions "
                    + "at two different track times, so the paused tree was not posing this rig and every pose "
                    + "this pass read is the same frame. NOTHING is written. The pause has been handed back, the "
                    + "lever is DISARMED for the rest of this process, and every requested animation is re-baked "
                    + "per target with the tree running.");
                return new RigPass(
                    new GeoClipRigBakeOutcome(
                        [.. bakes.Select(bake => GeoClipBakeOutcome.Failed(
                            Reason,
                            bake.Times.Length > 0 ? bake.Times[0] : 0d,
                            bake.SampleTimeSource,
                            bake.Animation))],
                        1,
                        batched ? "batched" : "single",
                        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                    [.. live.Select(bake => bake.Animation)],
                    "pauseAssertFailed");
            }

            // ── PAGES, ONCE FOR THE RIG ──────────────────────────────────────────────────────────────────
            //
            // A rig's atlas pages are identical across its poses — which is exactly why the store
            // content-addresses and dedupes them — so the decode-and-re-encode is paid once and the bytes are
            // written into each pose's directory. The artifact directory keeps the shape it always had: nothing
            // downstream learns that a page was shared upstream.
            List<CollectedPage> collected;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakePageCollect))
            {
                collected = CollectPages(spineNode, atlas, config.KnownPageContentIds, logStream, rigLabel);
            }

            // ── PASS 3 — geometry, per animation, on the associated meshes only ───────────────────────────
            foreach (var bake in kept)
            {
                var animLabel = AnimLabel(bake);
                var lane = Use(bake.Animation, animLabel);
                if (lane is null)
                {
                    bake.Failure = "the animation lane could not be re-prepared for the geometry pass.";
                    continue;
                }

                var stale = new StaleMeshTally();
                var frames = new List<GeoClipFrameSample>(bake.Times.Length);
                for (var i = 0; i < bake.Times.Length; i += 1)
                {
                    TryRun(() => lane.Entry.SetTrackTime((float)bake.Times[i]));

                    // The seek-await-read loop the offline geometry probe measured BOTH WAYS: its
                    // `no-force-draw` and `with-force-draw` arms report identical per-mesh tracking on both
                    // recorded rigs, so the awaited frame alone is what makes the readback below current.
                    await AwaitBudgetedFramesAsync(
                        rootViewport,
                        1,
                        drawBudget,
                        GeoClipDrawSite.PoseRead,
                        Sts2RenderPhaseProfile.Phase.BakePoseWait);

                    // ONCE PER FRAME SAMPLE, never per mesh. A per-mesh scope would be tens of thousands of
                    // Stopwatch reads on a whole-clip bake and would price the instrument instead of the bake.
                    using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeGeometryRead))
                    {
                        frames.Add(BuildFrameSample(
                            bake.Poses[i], association.BySlotOrdinal, stale, logStream, animLabel, i));
                    }
                }

                bake.StaleMeshFrames = stale.Frames;
                if (stale.Frames > 0)
                {
                    Log(
                        logStream,
                        $"{animLabel}: staleMeshFrames={stale.Frames} across {stale.Slots.Count} slot(s) — an "
                        + "ASSOCIATED mesh read back empty during the geometry pass, so those frames are baked as "
                        + "hidden. That means a mesh RID was recycled or re-minted after acquisition, not that the "
                        + "slot is hidden.");
                }

                // WHERE THE CLIP SITS, measured rather than left to be inferred (see GeoClipPlacement).
                var placement = BuildPlacement(bake.Poses, bake.RestBounds, logStream, animLabel);

                var pages = collected.Select(page => page.Page).ToList();

                Sts2SpineGeoClipBuilder.BuildResult build;
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeManifest))
                {
                    build = Sts2SpineGeoClipBuilder.Build(new GeoClipBuildRequest(
                        bake.Target.ScenePath,
                        nodePath,
                        bake.Animation,
                        config.Fps,
                        bake.Duration,
                        frames,
                        pages,
                        atlas,
                        Placement: placement,
                        Notes: bake.Notes));
                }

                foreach (var diagnostic in build.Diagnostics)
                {
                    Log(logStream, $"{animLabel}: {diagnostic}");
                }

                // COUNTERS ARE SCOPED TO THIS POSE, not to the rig. `complete` is graded on the slots THIS
                // animation shows, so a rig bake cannot pass a pose off as complete on the strength of another
                // pose's coverage — nor fail one because a sibling showed a slot it does not.
                var drawableHere = VisibleOrdinals(bake.Poses);
                // …and of those, the ones this bake is EXPECTED to produce geometry for. The two sets differ
                // only for a slot read fully transparent at a SINGLE sampled pose, which draws nothing and is
                // baked hidden either way; Sts2SpineGeoClipPoseTransparency carries the whole argument and the
                // multi-frame boundary that keeps it legal.
                var expectation = Sts2SpineGeoClipPoseTransparency.Expect(PoseTransparencyView(bake.Poses));
                var visibleHere = expectation.Ordinals;
                if (expectation.TransparentOrdinals.Count > 0)
                {
                    Log(
                        logStream,
                        $"{animLabel}: {expectation.TransparentOrdinals.Count} slot(s) show a drawable attachment "
                        + "but their colour READ BACK fully transparent (alpha 0) at the one sampled pose "
                        + $"(ordinals {string.Join(",", expectation.TransparentOrdinals)}); a slot at alpha 0 puts "
                        + "no pixel on screen, so baking it hidden is the same picture and it is NOT expected to "
                        + $"produce geometry. Scoped to single-pose bakes ({Sts2SpineGeoClipPoseTransparency.ArmEnv}"
                        + "=0 expects them again); a multi-frame bake never drops a slot this way.");
                }

                var associatedHere = visibleHere.Count(association.BySlotOrdinal.ContainsKey);
                var truncation = Sts2SpineGeoClipSweep.SummariseTruncation(sweep.Windows);
                // Scoped to THIS pose's claims for the same reason `associatedHere` is: a rig-wide proven count
                // would credit a pose with evidence gathered for a slot it does not show. Graded over the whole
                // DRAWABLE set rather than the expectation above, deliberately: a claim that was actually made
                // stays graded even for a slot this bake no longer expects, so the transparency arm can only ever
                // make the ownership arm stricter, never wave a weak claim through.
                var claimsHere = drawableHere
                    .Where(association.BySlotOrdinal.ContainsKey)
                    .Select(ordinal => association.ClaimByOrdinal.TryGetValue(ordinal, out var claim)
                        ? claim
                        : Sts2SpineGeoClipOwnership.ClaimUnknown)
                    .ToArray();
                var claimsProvenHere = claimsHere.Count(Sts2SpineGeoClipOwnership.IsPositiveProof);
                var claimProofNote = Sts2SpineGeoClipOwnership.Summarise(claimsHere);
                var report = new GeoClipBakeReport(
                    BakerVersion,
                    config.Owner,
                    config.IsPoseOnly(bake.Target),
                    bake.Times.Length > 0 ? bake.Times[0] : 0d,
                    bake.SampleTimeSource,
                    slotTable.Count,
                    visibleHere.Count,
                    bestVisible,
                    acquisition.Frame,
                    sweep.Windows,
                    meshRids.Count,
                    config.StrictMeshFilter,
                    associatedHere,
                    Math.Max(0, visibleHere.Count - associatedHere),
                    association.ByColorFlip,
                    association.ByColorFlipPlusAtlas,
                    association.ByElimination,
                    association.ByAtlasRegion,
                    association.AmbiguousColorFlip,
                    association.AmbiguousAtlasRegion,
                    association.ForeignMeshes,
                    stale.Frames,
                    associatedHere >= visibleHere.Count,
                    atlasFallbackAvailable,
                    BuildAssociationPairs(slotTable, meshRids, association.BySlotOrdinal),
                    acquisition.Bake.Animation,
                    batched,
                    truncation.Truncated,
                    truncation.Note,
                    AtlasFirstResolved: association.AtlasFirstResolved,
                    AtlasFirstAgreed: association.AtlasFirstAgreed,
                    AtlasFirstDisagreed: association.AtlasFirstDisagreed,
                    AtlasFirstDisagreements: association.AtlasFirstDisagreements.Count > 0
                        ? association.AtlasFirstDisagreements
                        : null,
                    AssocAtlasFirstArmed: association.AtlasFirstArmed,
                    AtlasFirstWidened: association.AtlasFirstWidened,
                    // PEEKED, not drained: the rig pass is still running (later poses of a batch have not been
                    // written yet), and the recorder is only closed by the lane that opened it. So this pose's
                    // `bake.profile` is the bake SO FAR — every per-rig cost plus this pose's own — which is the
                    // honest thing a mid-flight artifact can carry, and is stated here rather than left to be
                    // discovered from a batch whose second manifest reports a larger total than its first.
                    Profile: GeoClipBakeProfile.From(Sts2RenderPhaseProfile.Peek()),
                    // The pause the profile above has to be read against: `parkedMs` is the game's own time only
                    // while this is false.
                    BakePaused: treePaused,
                    PauseNote: pauseNote,
                    PauseAssertFailed: GeoClipPauseDisarm.AssertFailures,
                    ClaimsProven: claimsProvenHere,
                    ClaimsUnproven: claimsHere.Length - claimsProvenHere,
                    ClaimProof: claimProofNote,
                    ByAtlasRegionSlotColor: association.ByAtlasRegionSlotColor,
                    SlotsTransparentAtPose: expectation.TransparentOrdinals.Count);

                Log(
                    logStream,
                    $"{animLabel}: completeness slots={report.Slots} everVisible={report.SlotsEverVisible} "
                    + $"transparentAtPose={report.SlotsTransparentAtPose} "
                    + $"meshesValidated={report.MeshesValidated} associated={report.Associated} "
                    + $"unassociated={report.Unassociated} complete={report.Complete} "
                    + $"truncated={report.Truncated}");
                if (report.Truncated)
                {
                    Log(logStream, $"{animLabel}: TRUNCATED — {report.TruncatedNote}");
                }
                Log(
                    logStream,
                    $"{animLabel}: association byColorFlip={report.ByColorFlip} "
                    + $"byColorFlipPlusAtlas={report.ByColorFlipPlusAtlas} byElimination={report.ByElimination} "
                    + $"byAtlasRegion={report.ByAtlasRegion} "
                    + $"byAtlasRegionSlotColor={report.ByAtlasRegionSlotColor} "
                    + $"ambiguousColorFlip={report.AmbiguousColorFlip} "
                    + $"ambiguousAtlasRegion={report.AmbiguousAtlasRegion} foreign={report.ForeignMeshes} "
                    + $"staleMeshFrames={report.StaleMeshFrames}");
                Log(
                    logStream,
                    $"{animLabel}: ownership claimsProven={report.ClaimsProven} "
                    + $"claimsUnproven={report.ClaimsUnproven} [{report.ClaimProof}] — a claim is PROVEN by a "
                    + "colour-flip response or by an atlas match whose corners coincide with the region the "
                    + "slot's attachment names; a bare containment match and elimination are not. Unclaimed "
                    + $"leftovers ({report.ForeignMeshes}) refuse this bake only alongside an unproven claim.");
                // SHADOW ONLY — nothing below reads this. The STRICT reading of the ownership rule refuses any
                // unproven claim whether or not the bracket holds leftovers. It is not what is enforced, because
                // with no leftovers the claims exhaust the validated pool and there is nothing an unproven claim
                // could have taken instead; this line is here so the next live leg can MEASURE how many bakes
                // the strict form would cost before anyone argues for it.
                if (report.ClaimsUnproven > 0 && report.ForeignMeshes <= 0)
                {
                    Log(
                        logStream,
                        $"{animLabel}: ownership SHADOW — the strict form of the arm (refuse on any unproven "
                        + $"claim, ignoring leftovers) would have refused this bake on {report.ClaimsUnproven} "
                        + $"of {report.ClaimsProven + report.ClaimsUnproven} claim(s). Report only; this bake "
                        + "was NOT refused on that basis.");
                }
                Log(
                    logStream,
                    $"{animLabel}: poseOnly={(report.PoseOnly ? 1 : 0)} sampleTime={report.SampleTimeSeconds:0.#####}s "
                    + $"source={report.SampleTimeSource} placement="
                    + (placement is null
                        ? "<none>"
                        : $"canvas {placement.CanvasWidth}x{placement.CanvasHeight} local "
                          + $"({placement.LocalX:0.###},{placement.LocalY:0.###}) "
                          + $"{placement.LocalWidth:0.###}x{placement.LocalHeight:0.###} fitScale {placement.FitScale:0.#####}"));

                var manifestText = Sts2SpineGeoClipBuilder.Serialize(build.Document with { Bake = report });
                // Include per-file allocation slack and publication metadata in the pre-write budget.
                static long AllocatedBytes(long bytes) => checked((bytes + 4095) / 4096 * 4096);
                var outputBytes = collected.Sum(page => AllocatedBytes(page.Bytes?.LongLength ?? 0L))
                    + AllocatedBytes(System.Text.Encoding.UTF8.GetByteCount(manifestText)) + 8192;
                if (!config.OutputBudget.TryConsume(outputBytes))
                {
                    bake.Failure = $"output-limit-exceeded: required={outputBytes} used={config.OutputBudget.UsedBytes} "
                        + $"maximum={config.OutputBudget.MaximumBytes}";
                    Log(logStream, $"{animLabel}: {bake.Failure}; no artifact files were written.");
                    continue;
                }

                var directory = Path.Combine(
                    config.OutDir,
                    Sts2SpineGeoClipSpec.DirectoryName(bake.Target.ScenePath, nodePath, bake.Animation));
                Directory.CreateDirectory(directory);
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakePageWrite))
                {
                    WritePages(collected, directory, logStream, animLabel);
                }

                var manifestPath = Path.Combine(directory, "manifest.json");
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeManifest))
                {
                    File.WriteAllText(manifestPath, manifestText);
                }

                Log(
                    logStream,
                    $"baked {animLabel} -> '{manifestPath}': parts={build.Document.Parts.Count} "
                    + $"rigid={build.Document.Parts.Count(part => part.Rigid)} frames={build.Document.Frames.Count} "
                    + $"pages={pages.Count} diagnostics={build.Diagnostics.Count} in "
                    + $"{stopwatch.Elapsed.TotalSeconds:0.##}s");
                bake.Outcome = new GeoClipBakeOutcome(
                    Success: true,
                    manifestPath,
                    [.. pages.Select(page => page.File)],
                    build.Document.Parts.Count,
                    build.Document.Frames.Count,
                    report.SampleTimeSeconds,
                    report.SampleTimeSource,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                    report.Slots,
                    report.SlotsEverVisible,
                    report.Associated,
                    report.Unassociated,
                    report.ForeignMeshes,
                    report.Complete,
                    FailureReason: null,
                    bake.Animation,
                    stale.Frames,
                    bake.AttachmentDriftSlots,
                    batched,
                    report.ClaimsProven,
                    report.ClaimsUnproven,
                    report.ClaimProof,
                    report.MeshesValidated,
                    report.Truncated);
            }

            stopwatch.Stop();

            // ── THE GATE ─────────────────────────────────────────────────────────────────────────────────
            //
            // A stale mesh is a verdict on the RIG, not on one pose: it says this skeleton re-mints or recycles
            // mesh RIDs when an animation is set, which makes every batched pose's association suspect — the
            // counter only sees the recycles that read back EMPTY, and a recycled RID that reads back POPULATED
            // is the same fault with no symptom. So one stale frame sends the whole rig to per-target bakes.
            var staleTotal = kept.Sum(bake => bake.StaleMeshFrames);
            var driftTotal = live.Sum(bake => bake.AttachmentDriftSlots);
            var retry = new List<string>();
            var retryReason = batched
                ? Sts2SpineGeoClipBatch.BatchRefusal(
                    staleTotal,
                    driftTotal,
                    association.ForeignMeshes,
                    !ReadBoolEnv(BatchForeignOkEnv, false))
                : null;
            if (retryReason is not null)
            {
                if (driftTotal > 0 && staleTotal == 0 && retryReason.StartsWith("attachmentDrift", StringComparison.Ordinal))
                {
                    // Drift is a property of ONE pose against the pose association was measured at, so only the
                    // poses that drift go back.
                    retry.AddRange(live.Where(bake => bake.AttachmentDriftSlots > 0).Select(bake => bake.Animation));
                }
                else
                {
                    // A stale mesh and a foreign mesh are both verdicts on the RIG's mesh set, not on one pose,
                    // so the whole rig goes back and the batched artifacts are overwritten.
                    retry.AddRange(live.Select(bake => bake.Animation));
                    Log(
                        logStream,
                        $"{rigLabel}: the batch reported {retryReason}, which is a verdict on this rig's mesh set "
                        + "rather than on one pose; EVERY pose is re-baked per target so its counters are the ones "
                        + "a single-animation bake would have produced.");
                }
            }

            retryReason ??= string.Empty;

            var outcomes = bakes
                .Select(bake => bake.Outcome ?? GeoClipBakeOutcome.Failed(
                    bake.Failure
                        ?? (bake.AttachmentDriftSlots > 0
                            ? "attachment drift; awaiting a per-target bake"
                            : "the bake produced no artifact."),
                    bake.Times.Length > 0 ? bake.Times[0] : 0d,
                    bake.SampleTimeSource,
                    bake.Animation))
                .ToList();
            Log(
                logStream,
                $"{rigLabel}: rig pass baked {outcomes.Count(outcome => outcome.Success)}/{bakes.Count} pose(s) in "
                + $"ONE scene load, {stopwatch.Elapsed.TotalSeconds:0.##}s"
                + (retry.Count > 0 ? $"; {retry.Count} to re-bake per target ({retryReason})" : string.Empty));
            LogPhaseProfile(logStream, rigLabel);
            return new RigPass(
                new GeoClipRigBakeOutcome(
                    outcomes,
                    1,
                    batched ? "batched" : "single",
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                retry,
                retryReason);
        }
        catch (Exception ex)
        {
            var reason = $"bake threw {ex.GetType().Name}: {ex.Message}";
            Log(logStream, $"skip {rigLabel}: {reason}");
            return new RigPass(
                new GeoClipRigBakeOutcome(
                    [.. bakes.Select(bake => bake.Outcome ?? GeoClipBakeOutcome.Failed(
                        reason,
                        animationName: bake.Animation))],
                    1,
                    batched ? "batched" : "single",
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3)),
                [],
                string.Empty);
        }
        finally
        {
            // UNPAUSE FIRST. See the ordering note at the lease: `using` disposal runs after this block, so
            // without this line the rig teardown below would free the offscreen viewport on a paused tree. The
            // call is idempotent and a no-op on the default-off path.
            pause.Release();

            foreach (var rid in probeRids)
            {
                TryRun(() => RenderingServer.FreeRid(rid));
            }

            TryRun(() =>
            {
                if (viewport.GetParent() is not null)
                {
                    rootViewport.RemoveChild(viewport);
                }

                viewport.QueueFree();
            });
        }
    }

    /// <summary>
    /// The rig pass's own phase breakdown, on the log line every other bake number arrives on.
    ///
    /// <para>Through the bake's own logger, so it carries the <c>SPINE_GEOCLIP</c> prefix a research stamp-tailer
    /// already greps for — a breakdown that only reached the manifest would be invisible during the run, and a
    /// run that aborts before writing a manifest would leave no numbers at all.</para>
    ///
    /// <para>PEEKED, so a mid-flight pass reports what it has spent so far; the recorder is closed by the lane
    /// that opened it, one level up. Silent when the profiler is off — an absent line reads as "not measured",
    /// which a zeroed one would not.</para>
    /// </summary>
    private static void LogPhaseProfile(ILogStream? logStream, string rigLabel)
    {
        if (Sts2RenderPhaseProfile.Peek() is not { } snapshot)
        {
            return;
        }

        // The ANTI-VACUITY line. A phase table is only worth reading if each phase is on the right side of the
        // blocking/parked split, and a call site that stamped the wrong side produces a table that looks
        // completely normal. Checked against the doctrine table rather than trusted.
        var violations = GeoClipPhaseDoctrine.Violations(GeoClipBakeProfile.From(snapshot)!);
        Log(
            logStream,
            $"{rigLabel}: phase profile {Sts2RenderPhaseProfile.FormatLogLine(snapshot)}"
            + (violations.Count > 0
                ? $" doctrineViolations=[{string.Join(",", violations)}] — a bake phase was stamped on the wrong "
                  + "side of the blocking/parked split, so the shares above are wrong by whatever it cost"
                : string.Empty));
    }

    /// <summary>Ordinal → drawable attachment name, for slots requiring a mesh at this pose.</summary>
    private static Dictionary<int, string> AttachmentsAt(PoseFrame pose)
    {
        var map = new Dictionary<int, string>();
        foreach (var slot in pose.Slots)
        {
            if (slot.AttachmentName is { } attachment && slot.RequiresDrawing)
            {
                map[slot.Ordinal] = attachment;
            }
        }

        return map;
    }

    /// <summary>
    /// The captured poses as <see cref="Sts2SpineGeoClipPoseTransparency"/>'s pure input: per pose, per slot,
    /// the drawing requirement and the alpha the capture READ (with whether it read one at all).
    ///
    /// <para>A colour shorter than four components is carried as alpha 1 — i.e. as opaque, i.e. as still
    /// expected. Every branch that cannot answer the question leaves the slot expected, which is the direction
    /// that cannot admit a bake on a capture failure.</para>
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>> PoseTransparencyView(
        IReadOnlyList<PoseFrame> poses)
    {
        var view = new List<IReadOnlyList<Sts2SpineGeoClipPoseTransparency.SlotPose>>(poses.Count);
        foreach (var pose in poses)
        {
            var slots = new List<Sts2SpineGeoClipPoseTransparency.SlotPose>(pose.Slots.Count);
            foreach (var slot in pose.Slots)
            {
                slots.Add(new Sts2SpineGeoClipPoseTransparency.SlotPose(
                    slot.Ordinal,
                    slot.RequiresDrawing,
                    slot.ColorReadSucceeded,
                    slot.Color.Length >= 4 ? slot.Color[3] : 1d));
            }

            view.Add(slots);
        }

        return view;
    }

    /// <summary>Slot ordinals that show a drawable attachment at any of these poses.</summary>
    private static HashSet<int> VisibleOrdinals(IReadOnlyList<PoseFrame> poses)
    {
        var visible = new HashSet<int>();
        foreach (var pose in poses)
        {
            foreach (var slot in pose.Slots)
            {
                if (slot.RequiresDrawing)
                {
                    visible.Add(slot.Ordinal);
                }
            }
        }

        return visible;
    }

    // ── Placement ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The clip's canvas and its node-LOCAL rect, from the skeleton bounds PASS 1 already read at every sampled
    /// time. No extra engine work: the bounds are a by-product of the pose capture.
    ///
    /// <para>The bounds fed to the fit are the UNION over the sampled frames — for a pose-only bake that union is
    /// the single sampled pose, which is exactly the raster still lane's rule (it measures at the still time
    /// rather than at rest so a mid-animation lunge is not cropped) and, for a whole-clip bake, exactly its
    /// union-over-frames rule. A degenerate union falls back to the REST bounds, and a degenerate rest read falls
    /// back to the default square — the same two-step the still lane takes.</para>
    ///
    /// <para>ONE CAVEAT, recorded rather than papered over: for a WHOLE-CLIP bake the two unions are taken over
    /// different sample sets (the geoclip samples at its own fps, the raster clip at its own), so the two
    /// placements can differ by the bounds of the frames one visited and the other did not. For a POSE-ONLY bake
    /// both sample the one time <c>ChooseSample</c> returns, so they are equal.</para>
    /// </summary>
    private static GeoClipPlacement? BuildPlacement(
        IReadOnlyList<PoseFrame> poses,
        Rect2 restBounds,
        ILogStream? logStream,
        string label)
    {
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var any = false;
        foreach (var pose in poses)
        {
            if (pose.Bounds.Length < 4 || !(pose.Bounds[2] > 0d) || !(pose.Bounds[3] > 0d))
            {
                continue;
            }

            var x0 = pose.Bounds[0];
            var y0 = pose.Bounds[1];
            var x1 = x0 + pose.Bounds[2];
            var y1 = y0 + pose.Bounds[3];
            if (!any)
            {
                (minX, minY, maxX, maxY, any) = (x0, y0, x1, y1, true);
                continue;
            }

            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);
        }

        var source = "sampled-pose-union";
        if (!any || maxX - minX <= 1d || maxY - minY <= 1d)
        {
            if (restBounds.Size.X > 1f && restBounds.Size.Y > 1f)
            {
                (minX, minY) = (restBounds.Position.X, restBounds.Position.Y);
                (maxX, maxY) = (minX + restBounds.Size.X, minY + restBounds.Size.Y);
                source = "rest-bounds";
            }
            else
            {
                Log(
                    logStream,
                    $"{label}: neither the sampled poses nor the rest pose reported usable skeleton bounds, so "
                    + "`meta.placement` falls back to the default canvas; a client should keep inverting the "
                    + "raster clip's placement for this rig.");
                return GeoClipPlacement.From(Sts2SceneFitFrame.Place(Sts2SceneFitFrame.Fallback()));
            }
        }

        // THE SAME FUNCTION the raster still/clip lane sizes its render cell with — called, not restated. That is
        // what makes "the geoclip's placement and the still's clipPlacement agree" a property of the code rather
        // than a coincidence two implementations have to keep.
        var frame = Sts2AssetExtractProvider.FrameFromBoundsFitted(
            new Rect2((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY)));
        Log(
            logStream,
            $"{label}: placement bounds ({source}) "
            + $"{maxX - minX:0.###}x{maxY - minY:0.###}@({minX:0.###},{minY:0.###}) "
            + $"-> canvas {frame.ViewportSize.X}x{frame.ViewportSize.Y} fitScale {frame.NodeScale.X:0.#####}");
        return GeoClipPlacement.From(Sts2SceneFitFrame.Place(
            frame.ViewportSize.X, frame.ViewportSize.Y, frame.NodePosition.X, frame.NodePosition.Y, frame.NodeScale.X));
    }

    // ── The animation lane ───────────────────────────────────────────────────────────────────────────

    private sealed record GeoClipLane(MegaSprite Sprite, MegaSkeleton Skeleton, MegaTrackEntry Entry, double Duration);

    private static GeoClipLane? TryPrepareLane(Node spineNode, string animation, ILogStream? logStream, string label)
    {
        try
        {
            var sprite = new MegaSprite(spineNode);
            var skeleton = sprite.GetSkeleton();
            if (skeleton is null)
            {
                Log(logStream, $"skip {label}: the SpineSprite exposed no skeleton.");
                return null;
            }

            var data = skeleton.GetData();
            if (!TryGet(() => data.HasAnimation(animation), false))
            {
                var available = string.Join(", ", TryGet(() => data.GetAnimationNames(), []) ?? []);
                Log(logStream, $"skip {label}: the skeleton has no such animation. Available: [{available}]");
                return null;
            }

            var state = sprite.GetAnimationState();
            var entry = state.SetAnimation(animation, loop: false, trackId: 0);
            if (entry is null)
            {
                Log(logStream, $"skip {label}: setting the animation returned no track entry.");
                return null;
            }

            // Timescale zero + an explicit per-frame track time is the whole determinism story: the pose at a
            // given t is the same on every run, so the bake is reproducible. The entry is HELD — never re-read
            // through `get_current`, which is a known uncatchable crash.
            state.SetTimeScale(0f);
            entry.SetTrackTime(0f);
            var duration = Math.Max(0f, TryGet(() => entry.GetAnimationEnd(), 0f));
            if (duration <= 0)
            {
                Log(logStream, $"{label}: the track entry reported a zero-length animation; baking a single pose at t=0.");
            }

            return new GeoClipLane(sprite, skeleton, entry, duration);
        }
        catch (Exception ex)
        {
            Log(logStream, $"skip {label}: preparing the animation lane threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── Pose capture (no geometry) ───────────────────────────────────────────────────────────────────

    // Per-slot facts that do not change over an animation, read once.
    private sealed record SlotInfo(int Ordinal, int SlotIndex, string SlotName, int BlendMode);

    private sealed record PoseSlot(
        int Ordinal,
        int SlotIndex,
        bool AttachmentReadSucceeded,
        bool HasAttachment,
        string? AttachmentName,
        double[] Color,
        int BlendMode,
        bool RequiresDrawing,
        string? AttachmentWrapperClass,
        string AttachmentClassificationSource,
        // Whether `Color` was READ or is the white fallback. The manifest projection does not care — an
        // unreadable slot colour is written as white either way — but the slot-colour tie-break does: a failed
        // read that looks like white is indistinguishable from a slot that IS white, and pairing on it would be
        // a guess dressed as a measurement. Defaulted true so the field cannot silently disarm the arm on a
        // construction path that forgets it; the one real construction site sets it from the read.
        bool ColorReadSucceeded = true);

    private sealed record PoseFrame(double T, int[] DrawOrder, double[] Bounds, IReadOnlyList<PoseSlot> Slots);

    /// <summary>
    /// How many child nodes of the sprite are its per-slot mesh nodes. Used only to show, in the log, that the
    /// headless re-mint really did tear the pool down and build another one — a rebuild that silently did
    /// nothing would otherwise be indistinguishable from one that worked.
    /// </summary>
    private static int CountSpineMeshChildren(Node spineNode)
        => TryGet(
            () => spineNode.GetChildren().Count(child => child.GetClass().Contains("SpineMesh")),
            -1);

    /// <summary>
    /// Write the mint mark: give every slot that shares an attachment name with another drawable slot at the
    /// acquisition pose its own red channel, and remember what it was holding.
    ///
    /// <para>Records into <paramref name="mintMarks"/> the colour that was ACTUALLY WRITTEN — read back off the
    /// Godot <c>Color</c> after construction rather than recomputed from the ladder — because that is the value
    /// the tie-break has to compare a surface against, and a channel that quantised or clamped on the way in
    /// must compare as what it became. A slot whose colour cannot be read or written is simply not marked: it
    /// keeps whatever it had and the tie-break, seeing an unmarked member, declines the whole group.</para>
    /// </summary>
    private static void ApplyMintMarks(
        GodotObject? skeleton,
        PoseFrame pose,
        Dictionary<int, double[]> mintMarks,
        Dictionary<int, Color> mintOriginals,
        ILogStream? logStream,
        string label)
    {
        var plan = Sts2SpineGeoClipHeadlessMintMark.For(
            pose.Slots.Select(slot => (slot.Ordinal, slot.AttachmentName, slot.RequiresDrawing)));
        foreach (var skipped in plan.Oversized)
        {
            Log(
                logStream,
                $"{label}: HEADLESS MINT MARK skipped '{skipped.AttachmentName}' — {skipped.Count} slots draw it "
                + $"and the ladder separates at most {Sts2SpineGeoClipHeadlessMintMark.MaxGroupSize}. The group is "
                + "left entirely unmarked, so its tie refuses exactly as it did before this arm existed.");
        }

        if (plan.Marks.Count == 0)
        {
            Log(
                logStream,
                $"{label}: HEADLESS MINT MARK — no two drawable slots share an attachment name at the acquisition "
                + "pose, so nothing is marked and the re-mint runs exactly as it did unmarked.");
            return;
        }

        var slots = skeleton is null ? null : TryGet(() => skeleton.Call("get_slots").AsGodotArray());
        if (slots is null)
        {
            Log(
                logStream,
                $"{label}: HEADLESS MINT MARK REFUSED — the rebuilt skeleton exposed no slot array, so the "
                + $"{plan.Marks.Count} slot(s) that share an attachment name were not marked and their tie stays "
                + "unbroken. Nothing else was changed.");
            return;
        }

        foreach (var mark in plan.Marks)
        {
            var slot = mark.Ordinal >= 0 && mark.Ordinal < slots.Count
                ? TryGet(() => slots[mark.Ordinal].AsGodotObject())
                : null;
            if (slot is null || TryGet(() => Invoke(slot, "get_color")?.AsColor()) is not { } baseColor)
            {
                Log(
                    logStream,
                    $"{label}: HEADLESS MINT MARK could not read ordinal {mark.Ordinal}'s colour, so it is left "
                    + "unmarked and its whole tie group will be declined.");
                continue;
            }

            var minted = new Color((float)mark.Red, baseColor.G, baseColor.B, baseColor.A);
            TryRun(() => Invoke(slot, "set_color", minted));
            mintOriginals[mark.Ordinal] = baseColor;
            mintMarks[mark.Ordinal] = [minted.R, minted.G, minted.B, minted.A];
        }

        Log(
            logStream,
            $"{label}: HEADLESS MINT MARK — {mintMarks.Count} of {plan.Marks.Count} slot(s) across "
            + $"{plan.GroupCount} same-attachment group(s) marked on the red channel before the create pass "
            + $"[{plan.Describe()}]. The mark is restored once the bracket has drawn; it is the SLOT side of the "
            + "tie-break, and a mark that does not survive to the surface refuses instead of guessing.");
    }

    /// <summary>Put every marked slot's own colour back, from the CURRENT skeleton rather than a held reference.</summary>
    private static void RestoreMintMarks(
        GodotObject skeletonObject,
        IReadOnlyDictionary<int, Color> mintOriginals,
        ILogStream? logStream,
        string label)
    {
        var slots = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        var restored = 0;
        foreach (var (ordinal, original) in mintOriginals.OrderBy(entry => entry.Key))
        {
            var slot = slots is not null && ordinal >= 0 && ordinal < slots.Count
                ? TryGet(() => slots[ordinal].AsGodotObject())
                : null;
            if (slot is null)
            {
                continue;
            }

            TryRun(() => Invoke(slot, "set_color", original));
            restored += 1;
        }

        Log(
            logStream,
            $"{label}: HEADLESS MINT MARK restored on {restored} of {mintOriginals.Count} marked slot(s). The "
            + "pose-parity line below re-reads the same colours, so a restore that did not land shows up there as "
            + "a colour difference rather than silently.");
    }

    /// <summary>
    /// One line comparing a pose the bake already recorded against the same pose re-read from a rebuilt
    /// skeleton: the attachment set, the slot colours and the draw order. Everything the manifest's geometry is
    /// keyed on comes off those, so if they match, the rebuild is invisible to the artifact.
    /// </summary>
    private static string DescribePoseParity(PoseFrame expected, PoseFrame actual)
    {
        var byOrdinal = actual.Slots.ToDictionary(slot => slot.Ordinal);
        var attachmentDiffs = 0;
        var colorDiffs = 0;
        var missing = 0;
        var worstColor = 0d;
        foreach (var slot in expected.Slots)
        {
            if (!byOrdinal.TryGetValue(slot.Ordinal, out var now))
            {
                missing += 1;
                continue;
            }

            if (now.HasAttachment != slot.HasAttachment
                || !string.Equals(now.AttachmentName, slot.AttachmentName, StringComparison.Ordinal))
            {
                attachmentDiffs += 1;
            }

            var channels = Math.Min(slot.Color.Length, now.Color.Length);
            var worstHere = 0d;
            for (var channel = 0; channel < channels; channel += 1)
            {
                worstHere = Math.Max(worstHere, Math.Abs(slot.Color[channel] - now.Color[channel]));
            }

            if (worstHere > 0)
            {
                colorDiffs += 1;
                worstColor = Math.Max(worstColor, worstHere);
            }
        }

        var drawOrderSame = expected.DrawOrder.SequenceEqual(actual.DrawOrder);
        var boundsDelta = 0d;
        for (var i = 0; i < Math.Min(expected.Bounds.Length, actual.Bounds.Length); i += 1)
        {
            boundsDelta = Math.Max(boundsDelta, Math.Abs(expected.Bounds[i] - actual.Bounds[i]));
        }

        return $"slots={expected.Slots.Count} missing={missing} attachmentDiffs={attachmentDiffs} "
            + $"colourDiffs={colorDiffs} (worst {worstColor:0.####}) drawOrderIdentical="
            + $"{drawOrderSame.ToString().ToLowerInvariant()} worstBoundsDelta={boundsDelta:0.####}";
    }

    private static List<SlotInfo> ReadSlotTable(GodotObject skeletonObject, ILogStream? logStream, string label)
    {
        var table = new List<SlotInfo>();
        var slots = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        if (slots is null)
        {
            return table;
        }

        for (var ordinal = 0; ordinal < slots.Count; ordinal += 1)
        {
            var slot = TryGet(() => slots[ordinal].AsGodotObject());
            if (slot is null)
            {
                Log(logStream, $"{label}: slot ordinal {ordinal} is not an object; dropped.");
                continue;
            }

            var data = TryGet(() => Invoke(slot, "get_data")?.AsGodotObject());
            if (data is null)
            {
                Log(logStream, $"{label}: slot ordinal {ordinal} exposed no slot data; dropped (no stable slot index).");
                continue;
            }

            var slotIndex = TryGet(() => Invoke(data, "get_index")?.AsInt32() ?? -1, -1);
            if (slotIndex < 0)
            {
                Log(logStream, $"{label}: slot ordinal {ordinal} reported no slot index; dropped.");
                continue;
            }

            table.Add(new SlotInfo(
                ordinal,
                slotIndex,
                TryGet(() => Invoke(data, "get_slot_name")?.AsString(), string.Empty) ?? string.Empty,
                TryGet(() => Invoke(data, "get_blend_mode")?.AsInt32() ?? 0, 0)));
        }

        return table;
    }

    /// <summary>
    /// Reads only the stable target slot indices exposed by supported path constraints. A constraint that cannot
    /// complete the full target→data→index chain is deliberately ignored: the corresponding attachment remains
    /// drawable, so an acquisition/read/association failure cannot masquerade as a harmless path attachment.
    /// </summary>
    private static HashSet<int> ReadPathConstraintTargetSlots(
        GodotObject skeletonObject,
        ILogStream? logStream,
        string label)
    {
        var targetSlots = new HashSet<int>();
        var constraints = TryGet(() => skeletonObject.Call("get_path_constraints").AsGodotArray());
        if (constraints is null)
        {
            Log(logStream, $"{label}: pathConstraintTargets=0 source=get_path_constraints-unavailable; unknown attachments remain drawable.");
            return targetSlots;
        }

        var resolved = 0;
        for (var ordinal = 0; ordinal < constraints.Count; ordinal += 1)
        {
            var constraint = TryGet(() => constraints[ordinal].AsGodotObject());
            var target = constraint is null ? null : TryGet(() => Invoke(constraint, "get_target")?.AsGodotObject());
            var data = target is null ? null : TryGet(() => Invoke(target, "get_data")?.AsGodotObject());
            var slotIndex = data is null ? -1 : TryGet(() => Invoke(data, "get_index")?.AsInt32() ?? -1, -1);
            if (slotIndex < 0)
            {
                Log(logStream, $"{label}: path constraint ordinal {ordinal} did not resolve a stable target slot index; unknown attachments remain drawable.");
                continue;
            }

            targetSlots.Add(slotIndex);
            resolved += 1;
        }

        Log(
            logStream,
            $"{label}: pathConstraintTargets={targetSlots.Count} resolvedConstraints={resolved}/{constraints.Count} "
            + "source=target-slot-data-index.");
        return targetSlots;
    }

    private static PoseFrame ReadPose(
        GodotObject skeletonObject,
        GeoClipLane lane,
        IReadOnlyList<SlotInfo> slotTable,
        IReadOnlySet<int> pathConstraintTargetSlots,
        double t)
    {
        var slots = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        var poseSlots = new List<PoseSlot>(slotTable.Count);
        foreach (var info in slotTable)
        {
            string? attachmentName = null;
            string? attachmentWrapperClass = null;
            var attachmentReadSucceeded = false;
            var hasAttachment = false;
            var classification = Sts2SpineGeoClipAttachmentClassification.Classify(
                attachmentReadSucceeded: false,
                hasAttachment: false,
                attachmentWrapperClass: null,
                isPathConstraintTarget: false);
            double[] color = [1, 1, 1, 1];
            var colorReadSucceeded = false;
            if (slots is not null && info.Ordinal < slots.Count && TryGet(() => slots[info.Ordinal].AsGodotObject()) is { } slot)
            {
                attachmentReadSucceeded = TryReadAttachment(slot, out var attachment);
                hasAttachment = attachmentReadSucceeded && attachment is not null;
                attachmentName = attachment is null
                    ? null
                    : TryGet(() => Invoke(attachment, "get_attachment_name")?.AsString());
                attachmentWrapperClass = attachment is null ? null : TryGet(() => attachment.GetClass().ToString());
                classification = Sts2SpineGeoClipAttachmentClassification.Classify(
                    attachmentReadSucceeded: attachmentReadSucceeded,
                    hasAttachment: hasAttachment,
                    attachmentWrapperClass: attachmentWrapperClass,
                    isPathConstraintTarget: pathConstraintTargetSlots.Contains(info.SlotIndex));
                if (TryGet(() => Invoke(slot, "get_color")?.AsColor()) is { } c)
                {
                    color = [c.R, c.G, c.B, c.A];
                    colorReadSucceeded = true;
                }
            }

            poseSlots.Add(new PoseSlot(
                info.Ordinal,
                info.SlotIndex,
                attachmentReadSucceeded,
                hasAttachment,
                attachmentName,
                color,
                info.BlendMode,
                classification.RequiresDrawing,
                attachmentWrapperClass,
                classification.Source,
                colorReadSucceeded));
        }

        return new PoseFrame(t, ReadDrawOrder(skeletonObject, slotTable), ReadBounds(lane), poseSlots);
    }

    private static void LogAttachmentClassificationObservations(
        IEnumerable<PoseFrame> poses,
        ILogStream? logStream,
        string label)
    {
        var captured = poses.ToArray();
        foreach (var slot in captured
                     .SelectMany(pose => pose.Slots)
                     .Where(slot => slot.HasAttachment && !slot.RequiresDrawing)
                     .DistinctBy(slot => new
                     {
                         slot.Ordinal,
                         slot.SlotIndex,
                         slot.AttachmentName,
                         slot.AttachmentClassificationSource,
                         slot.AttachmentWrapperClass,
                     })
                     .OrderBy(slot => slot.Ordinal))
        {
            Log(
                logStream,
                $"{label}: pathAttachmentClassification ordinal={slot.Ordinal} slotIndex={slot.SlotIndex} "
                + $"attachment='{slot.AttachmentName ?? "<unavailable>"}' present=1 requiresDrawing=0 "
                + $"source={slot.AttachmentClassificationSource} wrapperClass="
                + $"{slot.AttachmentWrapperClass ?? "<unavailable>"}.");
        }

        var observations = captured
            .SelectMany(pose => pose.Slots)
            .GroupBy(slot => new
            {
                slot.AttachmentReadSucceeded,
                slot.HasAttachment,
                slot.RequiresDrawing,
                slot.AttachmentClassificationSource,
                slot.AttachmentWrapperClass,
            })
            .OrderBy(group => group.Key.RequiresDrawing ? 1 : 0)
            .ThenBy(group => group.Key.AttachmentClassificationSource, StringComparer.Ordinal)
            .ThenBy(group => group.Key.AttachmentWrapperClass, StringComparer.Ordinal)
            .Select(group => $"count={group.Count()} present="
                + $"{(group.Key.AttachmentReadSucceeded ? group.Key.HasAttachment ? "1" : "0" : "unknown")} "
                + $"requiresDrawing={(group.Key.RequiresDrawing ? 1 : 0)} "
                + $"source={group.Key.AttachmentClassificationSource} wrapperClass="
                + $"{group.Key.AttachmentWrapperClass ?? "<unavailable>"}")
            .ToArray();

        Log(
            logStream,
            observations.Length == 0
                ? $"{label}: attachmentClassification observations=0 (no slots)."
                : $"{label}: attachmentClassification observations={observations.Length} [{string.Join("; ", observations)}].");
    }

    private static int[] ReadDrawOrder(GodotObject skeletonObject, IReadOnlyList<SlotInfo> slotTable)
    {
        var order = TryGet(() => skeletonObject.Call("get_draw_order").AsGodotArray());
        if (order is null || order.Count == 0)
        {
            // Setup order is the honest fallback: it is what the skeleton draws when nothing reorders it.
            return [.. slotTable.Select(info => info.SlotIndex)];
        }

        var indices = new List<int>(order.Count);
        for (var i = 0; i < order.Count; i += 1)
        {
            var slot = TryGet(() => order[i].AsGodotObject());
            var data = slot is null ? null : TryGet(() => Invoke(slot, "get_data")?.AsGodotObject());
            var slotIndex = data is null ? -1 : TryGet(() => Invoke(data, "get_index")?.AsInt32() ?? -1, -1);
            if (slotIndex >= 0)
            {
                indices.Add(slotIndex);
            }
        }

        return indices.Count > 0 ? [.. indices] : [.. slotTable.Select(info => info.SlotIndex)];
    }

    private static double[] ReadBounds(GeoClipLane lane)
    {
        var rect = TryGet(() => lane.Skeleton.GetBounds(), default);
        return [rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y];
    }

    // ── Recipe A: bracketed mesh-RID acquisition, in TWO windows ─────────────────────────────────────

    private sealed record SweepOutcome(List<ulong> Meshes, IReadOnlyList<GeoClipBakeWindow> Windows);

    // One window's accumulating state while both arms have their turn at it.
    private sealed class WindowAcquisition(Sts2SpineGeoClipSweep.SweepWindow window)
    {
        internal Sts2SpineGeoClipSweep.SweepWindow Window { get; } = window;

        internal List<ulong> Found { get; } = [];

        internal HashSet<ulong> Seen { get; } = [];

        internal Dictionary<string, int> Rejected { get; } = new(StringComparer.Ordinal);

        internal long Probed { get; set; }

        internal double Ms { get; set; }

        internal string Arm { get; set; } = Sts2SpineGeoClipWalk.ArmDense;

        internal string ArmNote { get; set; } = string.Empty;

        internal GeoClipBakeWalk? Walk { get; set; }

        // Which dense TIERS ran here, and what the narrow one cost and returned. Kept per window because the
        // two windows narrow by very different amounts, so a bake-wide total could not attribute the saving.
        internal string Tier { get; set; } = Sts2SpineGeoClipSweep.TierNone;

        internal bool NarrowRan { get; set; }

        internal bool WideRan { get; set; }

        internal bool IndexRecoveryRan { get; set; }

        internal bool IndexRecoveryTruncated { get; set; }

        // EVERY recovery strip this window swept, not just the last one: the recovery tier covers the hull in
        // both directions now, so a window can run two, and the truncation roll-up has to see both.
        internal List<Sts2SpineGeoClipSweep.TierPass> IndexRecoveryPasses { get; } = [];

        internal long NarrowCandidates { get; set; }

        internal long NarrowProbed { get; set; }

        internal int NarrowFound { get; set; }

        internal bool NarrowTruncated { get; set; }

        internal void Add(IEnumerable<ulong> ids)
        {
            foreach (var id in ids)
            {
                if (Seen.Add(id))
                {
                    Found.Add(id);
                }
            }
        }
    }

    /// <summary>
    /// Acquire every planned window's meshes and merge the finds. The windows are reported SEPARATELY because
    /// that is the only way to tell a fix from a coincidence: window A is Phase 1's window verbatim, so its found
    /// count has a recorded value to be graded against (30 on the merchant, 26 on byrdonis), and window B's count
    /// is the yield of the split. A single merged total would hide a regression in either.
    ///
    /// <para>THREE PASSES, ONE ANSWER, each cheaper than the next and each armed only by the one before it
    /// coming up short. Every stage is graded on the same number — the size of the UNION of distinct mesh RID ids
    /// held so far, across every window and every stage, against <paramref name="expectedRunLength"/>, the count
    /// of slots showing an attachment at the acquisition frame, which is exactly what <c>complete</c> is graded
    /// on — by the same predicate (<see cref="Sts2SpineGeoClipWalk.DenseSweepRequired"/>), and every stage's
    /// finds are UNIONED in rather than replaced:</para>
    ///
    /// <para>A UNION, never a sum of the stages' counts. The stages deliberately overlap — the walk arm's column
    /// order starts at the core index band and the narrow tier sweeps that same band — so a sum lets one set of
    /// meshes satisfy the slot count twice over and skips the sweep that would have found the rest. That is the
    /// Phase-1 failure mode with a different cause, so the grade is on ids.</para>
    /// <list type="number">
    ///   <item>The WALK arm (<see cref="Sts2SpineGeoClipWalk"/>), off by default since it was measured a loss
    ///   live — a few hundred probes instead of tens of thousands when it is armed and works.</item>
    ///   <item>The NARROW dense tier: each window's core index band only (see
    ///   <see cref="Sts2SpineGeoClipSweep.NarrowToCoreBand"/>), which on the recorded plans is 19-42 % of the
    ///   candidates and holds every mesh either recorded session produced.</item>
    ///   <item>The WIDE dense tier: the full slacked plan, unchanged, on every window.</item>
    /// </list>
    ///
    /// <para>Each fallback is deliberately blunt: it re-runs ALL windows rather than guessing which one is
    /// deficient, because the shortfall is only observable in the total. The cost of being wrong is therefore one
    /// wasted pass on top of the status quo, never a missing mesh. That asymmetry is the whole design: Phase 1
    /// shipped an acquisition that lost 15 of the merchant's 44 slots silently, and it took two rounds to
    /// notice.</para>
    /// </summary>
    private static async Task<SweepOutcome> SweepMeshRidsAsync(
        Viewport rootViewport,
        IReadOnlyList<Sts2SpineGeoClipSweep.SweepWindow> windows,
        Sts2SpineGeoClipMath.SkeletonBox skeletonBounds,
        GeoClipConfig config,
        List<Rid> probeRids,
        int expectedRunLength,
        ILogStream? logStream,
        string label)
    {
        var self = probeRids.Select(rid => rid.Id).ToHashSet();
        var states = windows.Select(window => new WindowAcquisition(window)).ToList();
        var rejectedTotals = new Dictionary<string, int>(StringComparer.Ordinal);

        // Suppressed around the WHOLE acquisition, not just one window: an invalid RID makes the rendering server
        // log an error, and tens of thousands of those cost more than the lookups they annotate. Restored in the
        // finally — including the brief restorations each window makes while it yields a frame.
        var previousPrintErrors = TryGet(() => Engine.PrintErrorMessages, true);

        // Deliberately INSIDE the acquisition rather than beside it: the bench measures a cost that is a
        // function of the managed stack reaching the probe, so it runs on the main thread from the acquisition's
        // own async continuation and not from a worker. It does NOT stand at the exact depth the dense arm
        // probes from — the tiered arm now probes from inside
        // `Sts2SpineGeoClipSweep.SweepOnceAsync`'s continuation, a few frames deeper than here — which is
        // precisely why the bench carries a DEPTH LADDER arm and reports microseconds per added frame rather
        // than a single deep-vs-shallow number. Unarmed, this is one environment read. `probeRids` are the
        // baker's own minted meshes, which is where the bench gets a RID it knows resolves.
        if (Sts2GeoClipDiagnostics.Current.Armed)
        {
            await Sts2GeoClipDiagnostics.Current.RunAsync(
                rootViewport,
                probeRids.Count > 0 ? probeRids[0].Id : null,
                message => Log(logStream, $"{label}: {message}"),
                frames => AwaitFramesAsync(rootViewport, frames, forceDraw: false, Sts2RenderPhaseProfile.Phase.BakeSweepWait));
        }

        // Armed around the WHOLE acquisition for the same reason the print flag is: patching is far too
        // expensive to do per window. Which stretches actually suppress is decided by SuppressProbeErrors /
        // RestoreProbeErrors below, not by the lifetime of this scope — the scope only makes the patch
        // available, and disarms it on every exit path including a throw.
        //
        // RIDER 1: `armMs=`. Arming is a Harmony patch plus a verification backtrace capture, both one-off and
        // both on the main thread, and until now the only thing the log said about it was whether it bound. It
        // is timed HERE rather than threaded into the suppressor's own "ARMED and verified" line, so the
        // suppressor keeps one `Action<string>` and no knowledge of who is pricing it; the two lines sit
        // adjacent in the log and carry the same `SPINE_GEOCLIP` prefix.
        var armStopwatch = Stopwatch.StartNew();
        using var backtraceSuppression = Sts2GeoClipDiagnostics.Current.Arm(message => Log(logStream, $"{label}: {message}"));
        armStopwatch.Stop();
        Log(
            logStream,
            $"{label}: backtrace suppression arming took armMs="
            + armStopwatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)
            + ". A patch that binds pays this ONCE per rig bake; compare it against the sweep's own cost before "
            + "treating arming as free.");

        try
        {
            foreach (var state in states)
            {
                RunWalkArm(state, skeletonBounds, self, config, expectedRunLength, logStream, label);
            }

            // The UNION of what the walk arm delivered, not the sum of the windows' counts — the ids themselves,
            // because the dense tiers behind this re-sweep the very band the walk starts in and have to union
            // their finds onto these before either can be graded. Merged exactly as the artifact will be, so the
            // grade is on meshes that will actually be retained.
            var walkedIds = MergeAllWindows(states).Merged;
            var walked = walkedIds.Count;
            var walkRan = states.Any(state => state.Walk is not null);
            var denseNeeded = Sts2SpineGeoClipWalk.DenseSweepRequired(walkRan, walked, expectedRunLength);
            if (walkRan && !denseNeeded)
            {
                Log(
                    logStream,
                    $"{label}: walk arm acquired {walked}/{expectedRunLength} drawable slots' meshes for "
                    + $"{states.Sum(state => state.Probed)} probes against "
                    + $"{states.Sum(state => state.Window.Plan.TotalCandidates)} dense candidates; the dense "
                    + "sweep was not needed.");
            }
            else if (walkRan)
            {
                Log(
                    logStream,
                    $"{label}: walk arm acquired only {walked} mesh(es) against {expectedRunLength} slot(s) "
                    + "showing an attachment, so the DENSE sweep is armed behind it. The walk's finds are kept "
                    + "and unioned with the sweep's; nothing is lost, only time.");
            }

            if (denseNeeded)
            {
                await RunDenseTiersAsync(
                    states,
                    rootViewport,
                    skeletonBounds,
                    self,
                    previousPrintErrors,
                    config,
                    expectedRunLength,
                    // THE UNTESTED EDGE, named because nothing else names it: writing `[]` here compiles, and no
                    // offline lane catches it — `validate.sh bridge-tests` does not compile this file, and the
                    // walk is only ever armed by an explicit kill-switch override, never by default. The
                    // consequence is COST ONLY: dropping the seed can only make the tier gate more conservative,
                    // so the wide tier fires when it need not have and the artifact is the same one. Pinned from
                    // the core side by
                    // AcquireTiered_GradesItsSeedIntoTheTotalSoDroppingItCostsAWideTierAndNeverAMesh.
                    walkedIds,
                    logStream,
                    label);
            }

            foreach (var state in states)
            {
                foreach (var (reason, count) in state.Rejected)
                {
                    rejectedTotals[reason] = rejectedTotals.GetValueOrDefault(reason) + count;
                }
            }
        }
        finally
        {
            RestoreProbeErrors(previousPrintErrors);
        }

        var reports = states.Select(BuildWindowReport).ToList();
        Log(
            logStream,
            $"{label}: mesh filter strict={(config.StrictMeshFilter ? 1 : 0)} rejected "
            + (rejectedTotals.Count == 0
                ? "nothing"
                : string.Join(
                    " ",
                    rejectedTotals.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"))));

        var (merged, dropped) = MergeAllWindows(states);
        if (dropped > 0)
        {
            Log(
                logStream,
                $"{label}: the windows validated {merged.Count + dropped} meshes between them, far more than a "
                + $"skeleton has; keeping the first {MaxRetainedMeshRids} and dropping {dropped}.");
        }

        Log(
            logStream,
            $"{label}: bracket sweep validated {merged.Count} slot meshes across {windows.Count} window(s) "
            + $"({string.Join(" + ", reports.Select(report => $"{report.Name}={report.Found}[{report.Arm}/{report.Tier}]"))}).");
        return new SweepOutcome(merged, reports);
    }

    /// <summary>
    /// Every window's finds as ONE deduped, capped list — window A first, so the retention cap keeps Phase 1's
    /// window ahead of the split's yield. Used both to grade the acquisition mid-flight and to build the artifact
    /// at the end, so the number the tier gate believes and the meshes the bake ships can never disagree.
    /// </summary>
    private static (List<ulong> Merged, int Dropped) MergeAllWindows(IReadOnlyList<WindowAcquisition> states)
        => Sts2SpineGeoClipSweep.MergeWindows(
            states.Count > 0 ? states[0].Found : [],
            states.Count > 1 ? states.Skip(1).SelectMany(state => state.Found) : [],
            MaxRetainedMeshRids);

    private static GeoClipBakeWindow BuildWindowReport(WindowAcquisition state)
    {
        var plan = state.Window.Plan;
        return new GeoClipBakeWindow(
            state.Window.Name,
            plan.ValidatorLow,
            plan.ValidatorHigh,
            plan.IndexLow,
            plan.IndexHigh,
            state.Window.ValidatorSpan,
            plan.TotalCandidates,
            plan.Cap,
            plan.Truncated || state.IndexRecoveryTruncated,
            state.Probed,
            state.Found.Count,
            Math.Round(state.Ms, 3),
            plan.TotalCandidates,
            state.Window.CapSource,
            state.Window.CapAutoRaisedFrom,
            state.Window.CapAutoRaisedTo,
            state.Rejected,
            state.Arm,
            state.ArmNote,
            state.Walk,
            state.Tier,
            state.Window.CoreIndexLow,
            state.Window.CoreIndexHigh,
            state.NarrowCandidates,
            state.NarrowProbed,
            state.NarrowFound,
            state.NarrowTruncated)
        {
            OrdinaryTruncated = plan.Truncated,
            IndexRecoveries = [.. state.IndexRecoveryPasses.Select(
                recovery => new GeoClipBakeIndexRecovery(recovery.Plan, recovery.Probed, recovery.Found.Count))],
        };
    }


    /// <summary>
    /// The WALK arm on one window. Synchronous: its probe budget is one dense chunk, so it can never hold the
    /// main thread longer than the sweep it is replacing already does between two of its own yields.
    /// </summary>
    private static void RunWalkArm(
        WindowAcquisition state,
        Sts2SpineGeoClipMath.SkeletonBox skeletonBounds,
        IReadOnlySet<ulong> self,
        GeoClipConfig config,
        int expectedRunLength,
        ILogStream? logStream,
        string label)
    {
        var plan = Sts2SpineGeoClipWalk.Plan(state.Window, expectedRunLength, config.DenseSweepOnly);
        if (!plan.Viable)
        {
            state.Arm = Sts2SpineGeoClipWalk.ArmDense;
            state.ArmNote = plan.Reason;
            Log(logStream, $"{label}: window {state.Window.Name} walk arm not planned — {plan.Reason}.");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Sts2SpineGeoClipWalk.WalkResult result;
        try
        {
            // Suppressed here, restored by the CALLER's finally rather than a local one — which is safe only
            // because this arm is synchronous and nothing between it and that finally yields a frame. Both
            // halves still go on together and come off together; if this ever gains an await, it needs its own
            // RestoreProbeErrors around the yield the way the dense tiers' callback has.
            SuppressProbeErrors();
            result = Sts2SpineGeoClipWalk.Acquire(
                plan,
                candidate => !self.Contains(candidate)
                    && Classify(candidate, skeletonBounds, config, state.Rejected) is null);
        }
        finally
        {
            stopwatch.Stop();
        }

        state.Add(result.Found);
        state.Probed += result.Probed;
        state.Ms += stopwatch.Elapsed.TotalMilliseconds;

        // BLOCKING in full, unlike the dense tiers below: this arm is synchronous by design (its budget is one
        // dense chunk), so nothing inside its stopwatch handed a frame back to the game.
        Sts2RenderPhaseProfile.Record(
            Sts2RenderPhaseProfile.Phase.BakeSweepProbe, stopwatch.Elapsed.TotalMilliseconds);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeSweepProbes, result.Probed);
        state.Arm = Sts2SpineGeoClipWalk.ArmName(walkRan: true, denseRan: false);
        state.ArmNote = result.Reason;
        state.Walk = new GeoClipBakeWalk(
            result.ColumnsPlanned,
            result.ColumnsScanned,
            result.ColumnsSkipped,
            result.AnchorProbed,
            result.AnchorHypotheses,
            result.DeadAnchors,
            result.Runs,
            result.ValidatorStride,
            result.IndexStride,
            result.StrideSource,
            result.StrideProbed,
            result.LineProbed,
            result.LineFound,
            result.IndexWalkProbed,
            result.IndexWalkFound,
            result.StopUp,
            result.StopDown,
            result.GapSizes,
            result.IndexExtentLow,
            result.IndexExtentHigh,
            result.StopReason,
            result.BudgetExhausted,
            result.Probed,
            result.Found.Count,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3));

        Log(
            logStream,
            $"{label}: window {state.Window.Name} walk found={result.Found.Count} probed={result.Probed} "
            + $"(of {plan.Window.TotalCandidates} dense) columns={result.ColumnsScanned}/{result.ColumnsPlanned} "
            + $"stride={result.ValidatorStride}/{result.IndexStride} ({result.StrideSource}) runs={result.Runs} "
            + $"dead={result.DeadAnchors} indices={result.IndexExtentLow}..{result.IndexExtentHigh} "
            + $"stop={result.StopReason} up={result.StopUp} down={result.StopDown} "
            + $"gaps=[{string.Join(",", result.GapSizes)}] in {stopwatch.Elapsed.TotalSeconds:0.###}s");
    }

    /// <summary>
    /// Both halves of the sweep's error suppression, turned on together so they cannot drift apart.
    ///
    /// <para>The print flag and the backtrace gate answer different questions and are NOT interchangeable.
    /// Turning printing off saves ~135 us per failed probe (the logger, the formatter, the log write); it does
    /// nothing about the ~27 us the engine spends CAPTURING a C# stack trace before it ever looks at the flag,
    /// because that capture is an argument to the print call. Turning the gate on saves that 27 us. A stretch
    /// that wants one wants the other, and a site that set only one would look correct and cost 15x.</para>
    ///
    /// <para>The tiered dense arm made that trap concrete: the toggles that used to sit around ONE window's
    /// candidate loop now sit around <see cref="Sts2SpineGeoClipSweep.AcquireTieredAsync"/>, which runs BOTH
    /// tiers of BOTH windows inside a single span, so one missed pairing here would silently cost 15x on the
    /// whole acquisition rather than on one window.</para>
    /// </summary>
    private static void SuppressProbeErrors()
    {
        TryRun(() => Engine.PrintErrorMessages = false);
        Sts2GeoClipDiagnostics.Current.Suppressed = true;
    }

    private static void RestoreProbeErrors(bool previousPrintErrors)
    {
        TryRun(() => Engine.PrintErrorMessages = previousPrintErrors);
        Sts2GeoClipDiagnostics.Current.Suppressed = false;
    }

    /// <summary>
    /// The DENSE arm, in TWO TIERS: the core index band of every window first, then — only if the meshes held
    /// between them are still short of the slots showing an attachment — Phase 2's full slacked sweep, verbatim,
    /// on every window. Both tiers' finds are unioned onto whatever the walk arm found, and
    /// <paramref name="walkedBeforeTiers"/> carries the walk's IDS rather than its count for exactly that reason:
    /// the narrow tier re-sweeps the band the walk arm starts in, so counting both would grade the rig at twice
    /// its real coverage and skip the wide sweep.
    ///
    /// <para>The tier decision itself lives in <see cref="Sts2SpineGeoClipSweep.AcquireTieredAsync"/> rather than
    /// here, because a rule whose failure mode is a silently incomplete artifact has to be gradeable against a
    /// recorded id space without a game. This half is the engine adapter: the rendering-server predicate, the
    /// error suppression around each yielded frame, and the log.</para>
    ///
    /// <para>The suppression span therefore covers the ENTIRE tiered call — narrow tier and, if it is armed,
    /// wide tier, across every window — because the core drives both from inside one await and never hands the
    /// toggle back except through <c>yieldFrame</c>. That callback is the only stretch inside the span where the
    /// game gets frames, and it restores full-fidelity error reporting for exactly that stretch.</para>
    /// </summary>
    private static async Task RunDenseTiersAsync(
        IReadOnlyList<WindowAcquisition> states,
        Viewport rootViewport,
        Sts2SpineGeoClipMath.SkeletonBox skeletonBounds,
        IReadOnlySet<ulong> self,
        bool previousPrintErrors,
        GeoClipConfig config,
        int expectedRunLength,
        IReadOnlyList<ulong> walkedBeforeTiers,
        ILogStream? logStream,
        string label)
    {
        var byName = new Dictionary<string, WindowAcquisition>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            byName[state.Window.Name] = state;
        }

        // What the walk arm left here, captured before any tier unions onto it, for the arm note below.
        var walkOnly = states.ToDictionary(state => state.Window.Name, state => state.Found.Count, StringComparer.Ordinal);

        // Read before the span opens, so the summary below can report how many captures the suppressor actually
        // skipped across BOTH tiers. It is the anti-vacuity number: a suppressor that failed to bind reports
        // zero here while every other line in this log looks exactly as it does when it worked. It is a total
        // rather than a per-pass figure because every pass runs inside the one `AcquireTieredAsync` await and
        // the counter is process-wide — a per-pass split would have to be inferred, and an inferred number is
        // the wrong thing to grade a "did it bind at all" check on.
        var skippedBefore = Sts2GeoClipDiagnostics.Current.Skipped;

        // Every millisecond the tiers spent handing a frame BACK to the game. A tier pass's own `Ms` is a
        // stopwatch around its whole candidate loop, awaited yields included — so recording that number as
        // blocking would double-count against `bakeSweepWait` (which is parked), push blocking+parked past the
        // measured total, and clamp `unattributedMs` to zero. The residual is the instrument's honesty check, so
        // the parked half is subtracted here instead.
        var yieldedMs = 0d;

        Sts2SpineGeoClipSweep.TieredSweep tiered;
        try
        {
            SuppressProbeErrors();
            tiered = await Sts2SpineGeoClipSweep.AcquireTieredAsync(
                [.. states.Select(state => state.Window)],
                expectedRunLength,
                walkedBeforeTiers,
                // One predicate and one rejection histogram per WINDOW, shared by both tiers: a mesh must not be
                // believed by the narrow pass and refused by the wide one.
                (window, _) => candidate => !self.Contains(candidate)
                    && Classify(candidate, skeletonBounds, config, byName[window.Name].Rejected) is null,
                async () =>
                {
                    var yieldStarted = Stopwatch.GetTimestamp();
                    RestoreProbeErrors(previousPrintErrors);
                    await AwaitFramesAsync(rootViewport, 1, forceDraw: false, Sts2RenderPhaseProfile.Phase.BakeSweepWait);
                    SuppressProbeErrors();
                    yieldedMs += (Stopwatch.GetTimestamp() - yieldStarted) * 1000.0 / Stopwatch.Frequency;
                },
                CandidateChunkSize);
        }
        finally
        {
            RestoreProbeErrors(previousPrintErrors);
        }

        // Attributed from the passes' OWN sums rather than re-timed here: the sweep measures itself, and it is
        // Godot-free decision code the profiler has no business reaching into.
        Sts2RenderPhaseProfile.Record(
            Sts2RenderPhaseProfile.Phase.BakeSweepProbe,
            Math.Max(0d, tiered.Passes.Sum(pass => pass.Ms) - yieldedMs));
        Sts2RenderPhaseProfile.Count(
            Sts2RenderPhaseProfile.Counter.BakeSweepProbes, tiered.Passes.Sum(pass => pass.Probed));

        foreach (var pass in tiered.Passes)
        {
            var state = byName[pass.WindowName];
            var plan = pass.Plan;
            state.Add(pass.Found);
            state.Probed += pass.Probed;
            state.Ms += pass.Ms;
            if (pass.Tier == Sts2SpineGeoClipSweep.TierNarrow)
            {
                state.NarrowRan = true;
                state.NarrowCandidates = plan.TotalCandidates;
                state.NarrowProbed += pass.Probed;
                state.NarrowFound = pass.Found.Count;
                state.NarrowTruncated = plan.Truncated;
            }
            else if (pass.Tier == Sts2SpineGeoClipSweep.TierWide)
            {
                state.WideRan = true;
            }
            else if (pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery)
            {
                state.IndexRecoveryRan = true;
                state.IndexRecoveryTruncated |= plan.Truncated;
                state.IndexRecoveryPasses.Add(pass);
            }

            state.Tier = Sts2SpineGeoClipSweep.TierName(
                state.NarrowRan, state.WideRan, state.IndexRecoveryRan);
            state.Arm = Sts2SpineGeoClipWalk.ArmName(state.Walk is not null, denseRan: true);
            if (state.Walk is not null)
            {
                state.ArmNote = $"the walk arm delivered {walkOnly[pass.WindowName]} mesh(es) here and the total "
                    + "came up short of the slots showing an attachment, so this window was swept densely as well";
            }

            if (pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery)
            {
                state.ArmNote = (string.IsNullOrEmpty(state.ArmNote) ? string.Empty : state.ArmNote + "; ")
                    + $"index recovery validators {plan.ValidatorLow}..{plan.ValidatorHigh}, indices "
                    + $"{plan.IndexLow}..{plan.IndexHigh}, cap {plan.Cap}, emitted {plan.EmittedCount}, "
                    + $"truncated={plan.Truncated}, probed {pass.Probed}, found {pass.Found.Count}";
            }

            Log(
                logStream,
                $"{label}: window {pass.WindowName} tier={pass.Tier} "
                + $"validators={plan.ValidatorLow}..{plan.ValidatorHigh} "
                + $"indices={plan.IndexLow}..{plan.IndexHigh} total={plan.TotalCandidates} cap={plan.Cap} "
                + $"({state.Window.CapSource}) truncated={plan.Truncated} emitted={plan.EmittedCount} "
                + $"probed={pass.Probed} "
                + $"found={pass.Found.Count} in {pass.Ms / 1000d:0.##}s");
            if (plan.Truncated)
            {
                Log(
                    logStream,
                    $"{label}: window {pass.WindowName} tier={pass.Tier} holds {plan.TotalCandidates} candidates "
                    + $"but only {plan.EmittedCount} were validated, so a slot mesh may be missing from the far "
                    + "end of the window. Raise "
                    + $"{(pass.WindowName == Sts2SpineGeoClipSweep.WindowBName ? WindowBCapEnv : CandidateCapEnv)} "
                    + $"to {plan.TotalCandidates}.");
            }
        }

        var recoveryPasses = tiered.Passes
            .Where(pass => pass.Tier == Sts2SpineGeoClipSweep.TierIndexRecovery)
            .ToList();
        Log(
            logStream,
            tiered.WideRan
                ? $"{label}: narrow tier acquired {tiered.FoundAfterNarrow} mesh(es) against "
                    + $"{expectedRunLength} slot(s) showing an attachment"
                    + (tiered.NarrowTruncated
                        ? " AND hit its candidate cap, so it swept a subset of its own plan and its count is a "
                            + "floor rather than an answer"
                        : string.Empty)
                    + $", so the ordinary WIDE sweep is armed behind it "
                    + $"({tiered.NarrowProbed} narrow probes against {tiered.WideCandidates} wide candidates). "
                    + "The narrow tier's finds are kept and unioned with the wide sweep's; nothing is lost, only "
                    + "time."
                : $"{label}: narrow tier acquired {tiered.FoundAfterNarrow}/{expectedRunLength} drawable slots' "
                    + $"meshes for {tiered.NarrowProbed} probes against {tiered.WideCandidates} wide candidates; "
                    + "the ordinary wide sweep was not needed.");

        if (recoveryPasses.Count > 0)
        {
            Log(
                logStream,
                $"{label}: index recovery ran after the ordinary tiers remained short: "
                + $"passes={recoveryPasses.Count} candidates={recoveryPasses.Sum(pass => pass.Plan.TotalCandidates)} "
                + $"emitted={recoveryPasses.Sum(pass => pass.Plan.EmittedCount)} "
                + $"probed={recoveryPasses.Sum(pass => pass.Probed)} "
                + $"found={recoveryPasses.Sum(pass => pass.Found.Count)} "
                + $"truncated={recoveryPasses.Any(pass => pass.Plan.Truncated)}.");
        }

        Log(
            logStream,
            $"{label}: dense tiers probed {tiered.Passes.Sum(pass => pass.Probed)} candidate(s) and skipped "
            + $"{Sts2GeoClipDiagnostics.Current.Skipped - skippedBefore} backtrace capture(s). A zero there with a "
            + "non-zero probe count means the suppressor never bound and the sweep paid the engine's stock error "
            + "path.");
    }

    // Both arms share one predicate and one rejection histogram, so a mesh cannot be believed by one and refused
    // by the other. A surface-count rejection is overwhelmingly "this id is not a mesh at all", which is the
    // expected answer for almost every candidate in the space; counting it would drown every reason that
    // actually says something about a mesh we looked at.
    private static string? Classify(
        ulong candidate,
        Sts2SpineGeoClipMath.SkeletonBox skeletonBounds,
        GeoClipConfig config,
        Dictionary<string, int> rejected)
    {
        var reason = ClassifySlotMeshRid(candidate, skeletonBounds, config.StrictMeshFilter);
        if (reason is not null && reason != Sts2SpineGeoClipMath.RejectSurfaceCount)
        {
            rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
        }

        return reason;
    }

    /// <summary>
    /// Whether a bracketed RID is one of this skeleton's slot meshes; returns the rejection reason, or null to
    /// accept. The DECISION is pure (<see cref="Sts2SpineGeoClipMath.IsPlausibleSlotMesh"/>); this half only
    /// reads the surface out of the rendering server.
    /// </summary>
    private static string? ClassifySlotMeshRid(
        ulong ridId,
        Sts2SpineGeoClipMath.SkeletonBox skeletonBounds,
        bool strict)
    {
        // Almost every candidate in the space is not a mesh at all, and this is the cheap answer for those: the
        // reader bails on the surface-count hop before touching any array.
        var read = TryReadSurface(ridId);
        if (read is null)
        {
            return Sts2SpineGeoClipMath.RejectSurfaceCount;
        }

        return Sts2SpineGeoClipMath.IsPlausibleSlotMesh(
            Sts2SpineGeoClipMath.DescribeSurface(
                read.SurfaceCount,
                read.VerticesAreVector2,
                read.X,
                read.Y,
                read.HasUvChannel,
                read.U,
                read.V,
                read.Indices.Length),
            skeletonBounds,
            strict);
    }

    // ── Slot ↔ mesh association ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which mesh RID draws which slot. Measured, not assumed: child order is NOT slot order (proven on the
    /// merchant rig), and the bracketed RID set is in id order rather than node order. One slot's colour alpha
    /// is nudged and the mesh whose vertex colours move is its mesh.
    ///
    /// <para>The probe is done at the FIRST frame in which the slot shows an attachment, because a hidden slot
    /// has no vertex colours to move. Slots are grouped by that frame so the track is seeked once per group.
    /// A slot whose colour is driven by the animation's own colour timeline has its nudge overwritten before
    /// the next frame and cannot be measured this way — those fall through to the single-candidate elimination
    /// below, and anything still unresolved is named in the log.</para>
    ///
    /// <para>A slot can also move no mesh because its mesh was never swept in: the windows only span RIDs minted
    /// between the probe meshes, so a mesh minted outside them is invisible to this step and no nudge can reach
    /// it. That is a sweep-coverage failure, not an association one, and the log says which of the two it is by
    /// comparing the swept mesh count against the number of slots showing an attachment.</para>
    ///
    /// <para>TWO ATLAS FALLBACKS sit behind the colour nudge, because the nudge is measurement and the atlas is
    /// evidence of a different kind: (a) when several meshes move at once, the set is narrowed to those whose uv
    /// box lands in the region the slot's attachment NAMES, and a unique survivor is taken; (b) after the
    /// single-leftover elimination, every still-unassociated visible slot is matched against the atlas directly.
    /// Both refuse to pick when the evidence is ambiguous — a wrong pairing bakes another slot's art onto this
    /// slot for the whole clip, which is worse than a missing part.</para>
    /// </summary>
    private sealed record AssociationOutcome(
        Dictionary<int, ulong> BySlotOrdinal,
        int SlotsEverVisible,
        int ByColorFlip,
        int ByColorFlipPlusAtlas,
        int ByElimination,
        int ByAtlasRegion,
        int AmbiguousColorFlip,
        int AmbiguousAtlasRegion,
        int ForeignMeshes,
        // The atlas-first SHADOW arm's verdict on the same inputs. Reported, never acted on — see
        // RunAtlasFirstShadow. Defaulted so the record still constructs on the paths that never reach it.
        int AtlasFirstResolved = 0,
        int AtlasFirstAgreed = 0,
        int AtlasFirstDisagreed = 0,
        IReadOnlyList<string>? AtlasFirstDisagreementsOrNull = null,
        // Whether the atlas-first arm DISCOVERED this association rather than shadowing it, and how many probe
        // groups had to be widened back onto the whole read set when it did.
        bool AtlasFirstArmed = false,
        int AtlasFirstWidened = 0,
        // Slot ordinal → HOW its claim was made, in Sts2SpineGeoClipOwnership's vocabulary. The `by*` counters
        // above are a rig-wide roll-up of the same facts; this is the per-ordinal form, and it is what lets the
        // admission rule be graded PER POSE (a pose shows its own subset of the rig's slots, so a rig-wide proven
        // count would credit one pose with another's evidence).
        IReadOnlyDictionary<int, string>? ClaimByOrdinalOrNull = null,
        // Claims made by the slot-colour tie-break behind the atlas fallback. Counted apart from
        // `ByAtlasRegion` because it resolves what the plain atlas match cannot, so the two numbers together say
        // which arm a rig's bake actually rests on.
        int ByAtlasRegionSlotColor = 0)
    {
        internal IReadOnlyList<string> AtlasFirstDisagreements => AtlasFirstDisagreementsOrNull ?? [];

        internal IReadOnlyDictionary<int, string> ClaimByOrdinal
            => ClaimByOrdinalOrNull ?? new Dictionary<int, string>();
    }

    /// <param name="seekToStop">
    /// Put the rig into the pose at stop <c>i</c> of <paramref name="poses"/>. A stop is a sampled pose, which on
    /// a single-animation bake is a frame of that animation and on a RIG bake may belong to any of its
    /// animations — which is why this is a delegate rather than a track-time write: only the caller knows whether
    /// getting there also means switching animation (and re-taking the track entry, which must never be held
    /// across a switch).
    /// </param>
    /// <param name="probeOrder">
    /// The order stops are VISITED in, which decides where each slot is probed (a slot is probed at the first
    /// stop in this order that shows it). Natural order for a single animation — the schedule every recorded
    /// association pair was produced under — and acquisition-first for a rig bake, so the widest pose claims what
    /// it can in one group.
    /// </param>
    /// <param name="settledStop">
    /// The stop the rig is ALREADY posed at and settled on when this is called, or -1 when the caller cannot
    /// promise one. Only ever used to elide a seek that would write the pose the rig is holding, and only on the
    /// armed atlas-first path — see Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded.
    /// </param>
    /// <param name="drawBudget">
    /// Whether this pass's own awaited frames are closed with a forced draw. DEFAULT ON here and elided only by
    /// its own switch, because unlike the settle frames elsewhere in the bake, a stale read on this path does not
    /// make the bake slow — it drops or mis-assigns a slot's claim. See GeoClipDrawBudget.
    /// </param>
    private static async Task<AssociationOutcome> AssociateSlotMeshesAsync(
        Viewport rootViewport,
        Action<int> seekToStop,
        IReadOnlyList<int> probeOrder,
        IReadOnlyList<PoseFrame> poses,
        GodotObject skeletonObject,
        IReadOnlyList<SlotInfo> slotTable,
        IReadOnlyList<ulong> meshRids,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> atlasPageSizes,
        ILogStream? logStream,
        string label,
        int foreignCandidateDiagnosticsLimit,
        GeoClipDrawBudget drawBudget,
        int settledStop = -1,
        IReadOnlyDictionary<int, double[]>? mintMarks = null)
    {
        var association = new Dictionary<int, ulong>();
        // Written at EVERY site that adds to `association`, so the two never disagree about which ordinals are
        // claimed. An ordinal in `association` with no entry here would grade as `unknown`, which is not a proof.
        var claimProvenance = new Dictionary<int, string>();
        var claimed = new HashSet<ulong>();
        var uvBoxes = new Dictionary<ulong, Sts2SpineGeoClipAtlas.MeshUvBox?>();
        // Filled from the SAME surface reads that fill `uvBoxes`, so the slot-colour tie-break below pays nothing
        // for its evidence. A rid absent from this map was never read; a rid mapped to null was read and has no
        // usable colour (see UniformSurfaceColor). The tie-break refuses on either.
        var meshColors = new Dictionary<ulong, Sts2SpineGeoClipSlotColorIdentity.MeshColor?>();
        var ridOrder = BuildRidOrder(meshRids);
        int byColorFlip = 0, byColorFlipPlusAtlas = 0, byElimination = 0, byAtlasRegion = 0, ambiguousColorFlip = 0;
        var byAtlasRegionSlotColor = 0;

        // ordinal -> the first stop, IN PROBE ORDER, at which it shows an attachment. Walking `probeOrder`
        // rather than 0..N is the whole difference between a single-animation bake and a rig one: with the
        // natural order this is "the first frame of this animation", exactly as before.
        var firstVisible = new Dictionary<int, int>();
        var probeRank = new Dictionary<int, int>();
        for (var position = 0; position < probeOrder.Count; position += 1)
        {
            var stop = probeOrder[position];
            probeRank[stop] = position;
            if (stop < 0 || stop >= poses.Count)
            {
                continue;
            }

            foreach (var slot in poses[stop].Slots)
            {
                if (slot.AttachmentName is not null && slot.RequiresDrawing && !firstVisible.ContainsKey(slot.Ordinal))
                {
                    firstVisible[slot.Ordinal] = stop;
                }
            }
        }

        foreach (var info in slotTable)
        {
            if (!firstVisible.ContainsKey(info.Ordinal))
            {
                Log(
                    logStream,
                    $"{label}: slot {info.SlotIndex} ('{info.SlotName}') never shows a drawable attachment in this animation; "
                    + "no mesh association attempted, and it contributes no part.");
            }
        }

        var probeSettings = GeoClipProbeSettings.FromEnvironment();

        // ── THE ARMED ATLAS-FIRST PASS: discovery from data already in hand, at zero awaited frames ───────
        //
        // Default ARMED; the kill switch on GeoClipProbeSettings.AtlasFirstEnv takes the colour-probe arm below
        // back unchanged. Armed, the atlas resolves what it can BEFORE anything is probed, on inputs the bake has
        // already paid for (uv boxes are surface reads, and every mesh's is read here once and cached for the
        // fallbacks below). Each unique resolution claims its mesh immediately and is attributed to the atlas —
        // so the method mix flips, `byColorFlip` → `byAtlasRegion`, on a rig where the atlas can carry it. The
        // RESIDUE, and only the residue, reaches the colour probe: a group whose slots are all claimed here
        // simply does not exist below, and pays no seek, no probe frame and no readback.
        //
        // It resolves and refuses exactly as the shadow arm did (Sts2SpineGeoClipAtlas.AssociateAll — a tie is
        // REFUSED, never handed out in list order), which is what keeps the pairs it claims the pairs the probe
        // would have measured.
        var atlasFirstArmed = probeSettings.AtlasFirstArmed;
        var atlasFirstOrdinalByOrder = new Dictionary<int, int>();
        var atlasFirstWidened = 0;
        if (atlasFirstArmed)
        {
            var attachmentByOrdinal = firstVisible.Keys.ToDictionary(
                ordinal => ordinal,
                ordinal => AttachmentNameAt(poses, firstVisible[ordinal], ordinal));
            var upfront = Sts2SpineGeoClipAtlas.AssociateAll(
                attachmentByOrdinal,
                ReadUvBoxes(meshRids, ridOrder, uvBoxes, meshColors),
                atlas,
                atlasPageSizes);

            foreach (var match in upfront.Resolved)
            {
                if (match.Order < 0 || match.Order >= meshRids.Count)
                {
                    continue;
                }

                var rid = meshRids[match.Order];
                if (!claimed.Add(rid))
                {
                    continue;
                }

                association[match.Ordinal] = rid;
                // The METHOD, not just "the atlas did it": an exact corner agreement with the region this slot's
                // attachment names is a positive identification, and a bare containment is not.
                claimProvenance[match.Ordinal] = Sts2SpineGeoClipOwnership.AtlasClaim(match.Method);
                atlasFirstOrdinalByOrder[match.Order] = match.Ordinal;

                // Attributed to the ATLAS REGION, which is what actually decided it. The counter a reader
                // compares against a shadow-arm bake is `byAtlasRegion`, and `assocAtlasFirstArmed` in the same
                // report says why it moved.
                byAtlasRegion += 1;
            }

            Log(
                logStream,
                $"{label}: assocAtlasFirstArmed=1 — the atlas claimed {association.Count}/{firstVisible.Count} "
                + $"visible slot(s) before any probe frame, at zero awaited frames; "
                + $"{firstVisible.Count - association.Count} left to the colour probe."
                + (upfront.Refused.Count > 0
                    ? " Refused: "
                      + string.Join(
                          " ",
                          upfront.Refused
                              .Take(MaxAtlasFirstDisagreements)
                              .Select(refusal => $"{refusal.Ordinal}={refusal.Reason}"))
                      + (upfront.Refused.Count > MaxAtlasFirstDisagreements ? " …" : string.Empty)
                    : string.Empty));
        }

        ResetColorReadMeter();

        // Unarmed, nothing is associated yet and this filter removes nothing — the groups, their order and their
        // members are the ones the pre-atlas-first pass built. Armed, a slot the atlas already claimed takes no
        // code and no readback.
        foreach (var group in firstVisible
                     .Where(pair => !association.ContainsKey(pair.Key))
                     .GroupBy(pair => pair.Value)
                     .OrderBy(group => probeRank.TryGetValue(group.Key, out var rank) ? rank : group.Key))
        {
            // THE SEEK, elided when it would write the pose the rig is already holding. Armed only: the unarmed
            // schedule stays frame-for-frame what every recorded association pair was produced under.
            var seekFrames = Sts2SpineGeoClipProbe.ProbeSeekFramesNeeded(
                atlasFirstArmed ? settledStop : -1, group.Key);
            if (seekFrames > 0)
            {
                TryRun(() => seekToStop(group.Key));
                await AwaitBudgetedFramesAsync(
                    rootViewport,
                    seekFrames,
                    drawBudget,
                    GeoClipDrawSite.ProbeFrame,
                    Sts2RenderPhaseProfile.Phase.BakeProbeSeekWait);
            }
            else
            {
                Log(
                    logStream,
                    $"{label}: probe group at stop {group.Key} needs no seek — the rig is already posed and "
                    + $"settled there — so {Sts2SpineGeoClipProbe.DefaultProbeSeekFrames} awaited frame(s) are "
                    + "elided.");
            }

            // The probe restores every colour it nudged and awaits a frame, so the rig is still settled HERE
            // when the next group is considered.
            settledStop = group.Key;

            // Every member of the group is resolved ONCE, up front, in the same ordinal order the
            // one-at-a-time probe used to visit them — so a slot that cannot be read still gets exactly the
            // message it used to get, and simply takes no code in the schedule below.
            var members = new List<ProbeMember>();
            foreach (var ordinal in group.Select(pair => pair.Key).OrderBy(value => value))
            {
                var memberInfo = slotTable.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                var memberLabel = memberInfo is null
                    ? $"ordinal {ordinal}"
                    : $"slot {memberInfo.SlotIndex} ('{memberInfo.SlotName}')";

                var slots = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
                var slot = slots is not null && ordinal < slots.Count ? TryGet(() => slots[ordinal].AsGodotObject()) : null;
                if (slot is null)
                {
                    Log(logStream, $"{label}: {memberLabel} could not be re-read for the association probe; unassociated.");
                    continue;
                }

                if (TryGet(() => Invoke(slot, "get_color")?.AsColor()) is not { } baseColor)
                {
                    Log(logStream, $"{label}: {memberLabel} exposed no colour, so the association probe cannot run; unassociated.");
                    continue;
                }

                members.Add(new ProbeMember(ordinal, memberLabel, slot, baseColor));
            }

            if (members.Count == 0)
            {
                continue;
            }

            // THE SCHEDULE. One slot per frame costs one awaited engine frame per slot; a code per slot and a
            // SUBSET per frame costs O(log slots) frames for the same moved/not-moved measurement, and
            // spreading each frame's bits across COLOUR COMPONENTS divides that again by the channel count —
            // one readback already yields a per-component sum, so three bits per frame cost exactly what one
            // did. The predicate is still a checksum delta against a baseline read with every slot at its own
            // colour; only WHICH slots are nudged together, and on which component, changed.
            var plan = Sts2SpineGeoClipProbe.PlanProbe(members.Count, probeSettings.Scheme, probeSettings.Channels);

            // Every swept mesh, INCLUDING the ones earlier groups already claimed. Skipping those looks free and
            // turns a correct refusal into a silent wrong pairing; the whole argument is on MeshesToRead, and the
            // decode's mesh indices are into THIS list, so nothing below may reach past it for the sweep's own.
            //
            // ARMED, the read set is narrowed to the UNCLAIMED meshes — the merchant's ~176 metered reads per
            // probe frame become the handful its residue can still be. That is the optimisation MeshesToRead
            // refuses, and it is only sound because of the WIDEN below: every anomaly the narrowing could have
            // manufactured re-runs this group once on the whole set, at exactly the unarmed cost.
            var readMeshes = atlasFirstArmed
                ? Sts2SpineGeoClipProbe.MeshesToReadUnderAtlasFirst(meshRids, claimed)
                : Sts2SpineGeoClipProbe.MeshesToRead(meshRids, claimed);
            var frameSets = await RunGroupProbeAsync(rootViewport, plan, members, readMeshes, drawBudget);
            var decode = Sts2SpineGeoClipProbe.Decode(plan, frameSets);

            // THE WIDEN RULE. Null unless the read set was actually narrowed AND the narrowed answer shows one
            // of the three shapes a filtered-away mesh can produce; see AtlasFirstWidenReason.
            var widenReason = Sts2SpineGeoClipProbe.AtlasFirstWidenReason(
                readSetNarrowed: readMeshes.Count < meshRids.Count,
                starvedSlots: StarvedSlots(decode, members.Count),
                movedButUndecodableMeshes: decode.MovedButUndecodable.Count,
                decodesConflictingWithAtlasFirst: ConflictingAtlasFirstDecodes(
                    decode, readMeshes, ridOrder, atlasFirstOrdinalByOrder, members));

            if (widenReason is not null)
            {
                atlasFirstWidened += 1;
                Log(
                    logStream,
                    $"{label}: atlas-first widen: {widenReason} under the narrowed read set "
                    + $"({readMeshes.Count} of {meshRids.Count} meshes), so this group is re-probed on the WHOLE "
                    + "swept set — the same read set, frames and answer the unarmed path would have produced. "
                    + "The narrowed result is discarded, not merged.");
                readMeshes = Sts2SpineGeoClipProbe.MeshesToRead(meshRids, claimed);
                frameSets = await RunGroupProbeAsync(rootViewport, plan, members, readMeshes, drawBudget);
                decode = Sts2SpineGeoClipProbe.Decode(plan, frameSets);
            }

            // THE FALLBACK. A slot whose nudge does not register on one of the components — the case
            // premultiplied alpha would produce on a slot sitting at alpha zero — yields a mesh whose observed
            // positions are a STRICT SUBSET of its code, which under the union-safe space has too few bits to
            // be any code and so is undecodable rather than another slot's. Correctness is therefore never at
            // stake; only cost is, and past the point where the one-at-a-time repair is the cheaper exact
            // answer the whole group is re-probed on alpha, which costs and answers what it did before.
            if (Sts2SpineGeoClipProbe.ShouldReprobeUnderAlpha(
                    plan,
                    frameSets,
                    StarvedSlots(decode, members.Count),
                    probeSettings.MaxRepairSlots))
            {
                Log(
                    logStream,
                    $"{label}: {StarvedSlots(decode, members.Count)} of {members.Count} slot(s) decoded to no "
                    + $"mesh under the {plan.Channels} probe and "
                    + $"{Sts2SpineGeoClipProbe.DeadChannels(plan, frameSets)} of its {plan.ChannelCount} "
                    + "channel(s) went unanswered entirely; re-probing the whole group on alpha, the way the "
                    + "pre-channel pass did.");
                plan = Sts2SpineGeoClipProbe.PlanProbe(
                    members.Count, probeSettings.Scheme, GeoClipProbeChannels.Alpha);
                frameSets = await RunGroupProbeAsync(rootViewport, plan, members, readMeshes, drawBudget);
                decode = Sts2SpineGeoClipProbe.Decode(plan, frameSets);
            }

            var movedByMember = new List<ulong>[members.Count];
            for (var member = 0; member < members.Count; member += 1)
            {
                movedByMember[member] = [.. decode.MeshesBySlot[member].Select(mesh => readMeshes[mesh])];
            }

            Log(
                logStream,
                $"{label}: probed {members.Count} slot(s) first visible at frame {group.Key} in "
                + $"{plan.FrameCount} frame(s) ({plan.Scheme}, {plan.Channels} × {plan.ChannelCount} "
                + $"channel(s) = {plan.PositionCount} code positions); {decode.SilentMeshes} of "
                + $"{readMeshes.Count} meshes never moved, {decode.MovedButUndecodable.Count} moved on no valid "
                + "code"
                + (decode.MeshesMovedInEveryFrame > 0
                    ? $" (of which {decode.MeshesMovedInEveryFrame} moved in EVERY probe position, so they are "
                      + "responding to something other than a slot colour)"
                    : string.Empty)
                + ".");

            // THE REPAIR. A mesh that moved on a frame set matching no code is the one thing the grouped
            // probe can see and the one-at-a-time probe could not: it is a mesh several slots drive at once,
            // or one whose response is not stable frame to frame. Under the union-safe code space that is the
            // ONLY way an anomaly can present (a union of two codes always has too many bits to be a code),
            // so when no such mesh exists there is nothing a slower probe would find. When one does exist,
            // the slots it starved are re-measured exactly the way they used to be — which is what keeps the
            // association PAIRS identical to the old algorithm's on a rig where this happens.
            var starved = Enumerable.Range(0, members.Count).Where(member => movedByMember[member].Count == 0).ToArray();
            if (Sts2SpineGeoClipProbe.ShouldRepairOneAtATime(
                    probeSettings.RepairEnabled, decode.MovedButUndecodable.Count, starved.Length))
            {
                var repairing = Math.Min(starved.Length, probeSettings.MaxRepairSlots);
                Log(
                    logStream,
                    $"{label}: {decode.MovedButUndecodable.Count} mesh(es) moved on no valid code and "
                    + $"{starved.Length} slot(s) decoded to no mesh; re-probing {repairing} of them one at a "
                    + "time, the way the pre-grouping pass did."
                    + (repairing < starved.Length
                        ? $" The remaining {starved.Length - repairing} are left to the atlas fallback "
                          + $"(cap {probeSettings.MaxRepairSlots})."
                        : string.Empty));
                for (var i = 0; i < repairing; i += 1)
                {
                    movedByMember[starved[i]] = await ProbeOneSlotAsync(
                        rootViewport, members[starved[i]], readMeshes, drawBudget);
                }
            }

            for (var member = 0; member < members.Count; member += 1)
            {
                var ordinal = members[member].Ordinal;
                var slotLabel = members[member].Label;
                var moved = movedByMember[member];
                var attachmentName = AttachmentNameAt(poses, group.Key, ordinal);
                switch (moved.Count)
                {
                    case 1 when claimed.Add(moved[0]):
                        association[ordinal] = moved[0];
                        claimProvenance[ordinal] = Sts2SpineGeoClipOwnership.ClaimColorFlip;
                        byColorFlip += 1;
                        break;
                    case 1:
                        Log(
                            logStream,
                            $"{label}: {slotLabel} moved a mesh already claimed by another slot; unassociated (the "
                            + "colour nudge is not discriminating on this rig).");
                        break;
                    case 0:
                        // Two very different causes look identical here, and naming the wrong one sends the next
                        // reader after the wrong bug. The window is only to blame when the sweep came back with
                        // FEWER meshes than there are slots showing an attachment: then no nudge could ever have
                        // been seen for the surplus slots, because their mesh is not in the set being watched.
                        // Once the sweep covers the visible slots, that story is dead and saying it anyway sends
                        // the reader back to a window that is already wide enough.
                        Log(
                            logStream,
                            $"{label}: {slotLabel} moved no mesh — "
                            + (meshRids.Count < firstVisible.Count
                                ? $"the sweep validated only {meshRids.Count} meshes for {firstVisible.Count} slots "
                                  + "that show a drawable attachment, so this slot's mesh is most likely outside the swept "
                                  + "RID windows rather than unmeasurable"
                                : $"the sweep validated {meshRids.Count} meshes for {firstVisible.Count} slots that "
                                  + "show a drawable attachment, so coverage is NOT the explanation; most likely the "
                                  + "animation's own colour timeline overwrote the nudge before the next frame")
                            + "; falling through to the atlas fallback.");
                        break;
                    default:
                        // Several meshes moved together. The atlas can break the tie without touching the
                        // skeleton: only one of them samples the region this slot's attachment names.
                        var narrowed = MatchByAtlasRegion(
                            attachmentName, moved, claimed, meshRids, ridOrder, uvBoxes, atlas, atlasPageSizes,
                            meshColors);
                        if (narrowed.Rid is { } narrowedRid && claimed.Add(narrowedRid))
                        {
                            association[ordinal] = narrowedRid;
                            // The co-moving SET is provably ours — foreign geometry cannot answer our nudge —
                            // but which member of it this is rests on the atlas, so the claim is graded on the
                            // atlas half.
                            claimProvenance[ordinal] =
                                Sts2SpineGeoClipOwnership.ColorFlipAtlasClaim(narrowed.Match.Method);
                            byColorFlipPlusAtlas += 1;
                            Log(
                                logStream,
                                $"{label}: {slotLabel} moved {moved.Count} meshes at once; the atlas region "
                                + $"'{narrowed.Match.RegionName}' picks exactly one of them "
                                + $"({narrowed.Match.Method}), so it is associated by colour-flip+atlas.");
                        }
                        else
                        {
                            ambiguousColorFlip += 1;
                            Log(
                                logStream,
                                $"{label}: {slotLabel} moved {moved.Count} meshes at once and the atlas did not "
                                + $"break the tie ({narrowed.Match.Method}); unassociated.");
                        }

                        break;
                }
            }
        }

        // Elimination: when exactly one visible slot and exactly one mesh are left over, the pairing is forced.
        // Repeated because resolving one can expose the next.
        while (true)
        {
            var pendingOrdinals = firstVisible.Keys.Where(ordinal => !association.ContainsKey(ordinal)).ToArray();
            var pendingRids = meshRids.Where(rid => !claimed.Contains(rid)).ToArray();
            if (pendingOrdinals.Length != 1 || pendingRids.Length != 1)
            {
                break;
            }

            association[pendingOrdinals[0]] = pendingRids[0];
            claimProvenance[pendingOrdinals[0]] = Sts2SpineGeoClipOwnership.ClaimElimination;
            claimed.Add(pendingRids[0]);
            byElimination += 1;
            var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == pendingOrdinals[0]);
            Log(
                logStream,
                $"{label}: slot {info?.SlotIndex ?? pendingOrdinals[0]} ('{info?.SlotName}') associated by elimination "
                + "(it was the only unmatched slot left and one mesh was unmatched).");
        }

        // The atlas-region fallback, run to a FIXPOINT: every slot resolved here removes a mesh from the unclaimed
        // pool, which can turn the next slot's ambiguous three-way tie into a unique containment. Bounded by the
        // slot count, because each pass that changes anything associates at least one slot.
        var unresolved = new Dictionary<int, Sts2SpineGeoClipAtlas.RegionMatch>();
        for (var pass = 0; pass <= firstVisible.Count; pass += 1)
        {
            var progressed = false;
            foreach (var ordinal in firstVisible.Keys.Where(o => !association.ContainsKey(o)).OrderBy(o => o))
            {
                var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                var slotLabel = info is null ? $"ordinal {ordinal}" : $"slot {info.SlotIndex} ('{info.SlotName}')";
                var resolved = MatchByAtlasRegion(
                    AttachmentNameAt(poses, firstVisible[ordinal], ordinal),
                    meshRids,
                    claimed,
                    meshRids,
                    ridOrder,
                    uvBoxes,
                    atlas,
                    atlasPageSizes,
                    meshColors);
                if (resolved.Rid is { } rid && claimed.Add(rid))
                {
                    association[ordinal] = rid;
                    claimProvenance[ordinal] = Sts2SpineGeoClipOwnership.AtlasClaim(resolved.Match.Method);
                    unresolved.Remove(ordinal);
                    byAtlasRegion += 1;
                    progressed = true;
                    Log(
                        logStream,
                        $"{label}: {slotLabel} associated by atlas region '{resolved.Match.RegionName}' "
                        + $"({resolved.Match.Method}; score {resolved.Match.Score:0.##} vs runner-up "
                        + $"{resolved.Match.RunnerUpScore:0.##}, {resolved.Match.ContainedCount} contained).");
                }
                else
                {
                    // Kept, not logged: an earlier pass's verdict on this slot is stale as soon as any other slot
                    // claims a mesh, so only the LAST one is worth printing.
                    unresolved[ordinal] = resolved.Match;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        // The SLOT-COLOUR tie-break, behind the atlas fallback: the only slots that reach it are the ones the
        // atlas narrowed to a tie it refused to resolve, and the only candidates offered are that tie's own
        // members. See Sts2SpineGeoClipSlotColorIdentity for why the colour is available and why a tie that does
        // not separate has to stay a tie.
        var tieBreak = ResolveTiesBySlotColor(
            unresolved, association, claimProvenance, claimed, poses, firstVisible, slotTable, meshRids, ridOrder,
            meshColors, mintMarks ?? EmptyMintMarks, logStream, label);
        // Both arms answer the same question — which of OUR slots owns a mesh the atlas already narrowed to our
        // own regions — and are counted together, because a reader comparing two bakes wants "the tie-break
        // carried this rig", not which of its two evidence sources was available. Which one it was is in the
        // claim provenance (`atlas+slot-color:` vs `atlas+mint-mark:`) and in the log line each emits.
        byAtlasRegionSlotColor += tieBreak.SlotColorClaims + tieBreak.MintMarkClaims;

        var ambiguousAtlasRegion = 0;
        foreach (var (ordinal, match) in unresolved.OrderBy(pair => pair.Key))
        {
            var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
            var slotLabel = info is null ? $"ordinal {ordinal}" : $"slot {info.SlotIndex} ('{info.SlotName}')";
            if (match.Method == Sts2SpineGeoClipAtlas.MatchAmbiguous)
            {
                ambiguousAtlasRegion += 1;
            }

            Log(
                logStream,
                $"{label}: {slotLabel} (attachment "
                + $"'{AttachmentNameAt(poses, firstVisible[ordinal], ordinal) ?? "<none>"}') could not be resolved "
                + $"against the atlas either ({match.Method}); unassociated.");
        }

        var unmatchedMeshes = meshRids.Count(rid => !claimed.Contains(rid));
        if (atlasFirstArmed)
        {
            Log(
                logStream,
                $"{label}: assocAtlasFirstArmed=1 atlasFirstWidened={atlasFirstWidened} — the atlas DISCOVERED "
                + $"{byAtlasRegion} of the {association.Count} association(s) above and the colour probe only saw "
                + "the residue. `atlasFirstWidened` counts probe groups that had to be re-run on the whole swept "
                + "read set; each one cost its probe frames twice.");
        }

        Log(
            logStream,
            $"{label}: associated {association.Count}/{firstVisible.Count} drawable slots to meshes; "
            + $"{unmatchedMeshes} of {meshRids.Count} validated meshes belong to no slot (foreign geometry in the "
            + $"bracket). The probe took {ColorReadMeter()} — the association's cost that is NOT awaited frames.");
        LogForeignCandidateDiagnostics(
            meshRids,
            claimed,
            foreignCandidateDiagnosticsLimit,
            unmatchedMeshes,
            atlas,
            atlasPageSizes,
            logStream);

        // The meter is the SOURCE OF TRUTH for what the colour probe cost — it times the individual readbacks,
        // which is finer than any scope this method could open — so the profiler is a consumer of it here rather
        // than a second, coarser measurement of the same thing.
        Sts2RenderPhaseProfile.Record(
            Sts2RenderPhaseProfile.Phase.BakeColorRead, _colorReadTicks * 1000.0 / Stopwatch.Frequency);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeColorReads, _colorReads);

        var atlasFirst = RunAtlasFirstShadow(
            association, firstVisible, poses, slotTable, meshRids, ridOrder, uvBoxes, atlas, atlasPageSizes,
            logStream, label);

        return new AssociationOutcome(
            association,
            firstVisible.Count,
            byColorFlip,
            byColorFlipPlusAtlas,
            byElimination,
            byAtlasRegion,
            ambiguousColorFlip,
            ambiguousAtlasRegion,
            unmatchedMeshes,
            atlasFirst.Resolved,
            atlasFirst.Agreed,
            atlasFirst.Disagreed,
            atlasFirst.Disagreements,
            atlasFirstArmed,
            atlasFirstWidened,
            claimProvenance,
            byAtlasRegionSlotColor);
    }

    /// <summary>
    /// The live half of the slot-colour tie-break: gather the evidence the pure rule needs, run it per TIE
    /// GROUP, and write back only what it accepted.
    ///
    /// <para>A tie group is a set of slots the atlas refused over the SAME candidates — three slots drawing one
    /// <c>circ_rotator</c> over one uv box, say. Grouping matters: the rule's mutual-best test needs every slot
    /// that could claim a candidate to be in front of it at once, and asking one slot at a time would let the
    /// first-asked take a mesh that a later one owns.</para>
    ///
    /// <para>IT REFUSES A WHOLE GROUP ON ANY MISSING EVIDENCE. An unreadable slot colour (which the pose records
    /// as white, indistinguishable from a slot that IS white), a candidate with no usable mesh colour, or a tie
    /// entry some other slot has claimed since the verdict was cached — each one shrinks or distorts the set the
    /// rule reasons over, and a rule that has been handed a smaller set finds it EASIER to declare a unique
    /// winner. That is the direction a conservative arm must never fail in, so the group is dropped instead.</para>
    /// </summary>
    /// <param name="mintMarks">
    /// Ordinal → the colour the headless re-mint wrote to that slot before its mesh was created (empty on every
    /// other path). A group whose members are ALL marked reads its slot side from here instead of from the pose:
    /// same rule, same refusals, but the evidence is a value this process WROTE, which is what makes the tie
    /// breakable at all under a renderer that cannot answer a colour probe. See
    /// <see cref="Sts2SpineGeoClipHeadlessMintMark"/>.
    /// </param>
    /// <returns>How many claims each arm of the tie-break made.</returns>
    private static (int SlotColorClaims, int MintMarkClaims) ResolveTiesBySlotColor(
        Dictionary<int, Sts2SpineGeoClipAtlas.RegionMatch> unresolved,
        Dictionary<int, ulong> association,
        Dictionary<int, string> claimProvenance,
        HashSet<ulong> claimed,
        IReadOnlyList<PoseFrame> poses,
        IReadOnlyDictionary<int, int> firstVisible,
        IReadOnlyList<SlotInfo> slotTable,
        IReadOnlyList<ulong> meshRids,
        IReadOnlyDictionary<ulong, int> ridOrder,
        IReadOnlyDictionary<ulong, Sts2SpineGeoClipSlotColorIdentity.MeshColor?> meshColors,
        IReadOnlyDictionary<int, double[]> mintMarks,
        ILogStream? logStream,
        string label)
    {
        if (unresolved.Count == 0)
        {
            return (0, 0);
        }

        // The two arms are gated independently. The passive one has shipped since Sep-08 and keeps its kill
        // switch; the mint mark's own gate is upstream — no mark was written, so no group can be marked — and it
        // must stay reachable when someone disarms the passive arm to isolate it.
        var slotColorArmed = Sts2SpineGeoClipSlotColorIdentity.ArmedFromEnvironment();
        if (!slotColorArmed && mintMarks.Count == 0)
        {
            Log(
                logStream,
                $"{label}: slot-colour tie-break DISARMED ({Sts2SpineGeoClipSlotColorIdentity.ArmEnv}); "
                + $"{unresolved.Count} slot(s) left to the atlas verdict alone.");
            return (0, 0);
        }

        var claims = 0;
        var mintClaims = 0;
        var groups = unresolved
            .Where(pair => pair.Value.Method == Sts2SpineGeoClipAtlas.MatchAmbiguous
                && pair.Value.TiedCandidates.Count > 1)
            .OrderBy(pair => pair.Key)
            .GroupBy(pair => string.Join(",", pair.Value.TiedCandidates.Select(tied => tied.Order).Order()))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in groups)
        {
            var ordinals = group.Select(pair => pair.Key).ToArray();
            // Every member of a group tied over the SAME candidate orders — that is the grouping key — so any
            // member's list names the whole candidate set.
            var tiedByOrder = group.First().Value.TiedCandidates
                .ToDictionary(tied => tied.Order, tied => tied.Method);

            // ALL of them, or none. A partly marked group would put a written value and a read one on the same
            // distance scale, and the unmarked member would sit at its pose colour — which is precisely the
            // colour the marked ones were moved AWAY from, so the rule would find the mark decisive for a reason
            // that is an artefact of the marking rather than evidence about the mesh.
            var minted = mintMarks.Count > 0
                && ordinals.All(ordinal => mintMarks.TryGetValue(ordinal, out var mark) && mark.Length >= 4);
            if (!minted && !slotColorArmed)
            {
                Log(
                    logStream,
                    $"{label}: slot-colour tie-break DISARMED ({Sts2SpineGeoClipSlotColorIdentity.ArmEnv}) and "
                    + $"this group of {ordinals.Length} slot(s) carries no mint mark; left to the atlas verdict "
                    + "alone.");
                continue;
            }

            var slots = new List<Sts2SpineGeoClipSlotColorIdentity.SlotColor>(ordinals.Length);
            var unreadableSlot = -1;
            foreach (var ordinal in ordinals)
            {
                if (minted)
                {
                    var mark = mintMarks[ordinal];
                    slots.Add(new Sts2SpineGeoClipSlotColorIdentity.SlotColor(
                        ordinal, mark[0], mark[1], mark[2], mark[3]));
                    continue;
                }

                var pose = firstVisible[ordinal];
                var slot = pose >= 0 && pose < poses.Count
                    ? poses[pose].Slots.FirstOrDefault(entry => entry.Ordinal == ordinal)
                    : null;
                if (slot is null || !slot.ColorReadSucceeded || slot.Color.Length < 4)
                {
                    unreadableSlot = ordinal;
                    break;
                }

                slots.Add(new Sts2SpineGeoClipSlotColorIdentity.SlotColor(
                    ordinal, slot.Color[0], slot.Color[1], slot.Color[2], slot.Color[3]));
            }

            if (unreadableSlot >= 0)
            {
                Log(
                    logStream,
                    $"{label}: slot-colour tie-break skipped a group of {ordinals.Length} slot(s) — ordinal "
                    + $"{unreadableSlot} has no READ colour at its pose, and the white fallback it carries "
                    + "instead cannot be told from a slot that is genuinely white.");
                continue;
            }

            var candidates = new List<Sts2SpineGeoClipSlotColorIdentity.MeshColor>(tiedByOrder.Count);
            var dropped = 0;
            foreach (var order in tiedByOrder.Keys.Order())
            {
                if (order < 0 || order >= meshRids.Count || claimed.Contains(meshRids[order])
                    || !meshColors.TryGetValue(meshRids[order], out var color) || color is not { } usable)
                {
                    dropped += 1;
                    continue;
                }

                candidates.Add(usable);
            }

            if (dropped > 0)
            {
                Log(
                    logStream,
                    $"{label}: slot-colour tie-break skipped a group of {ordinals.Length} slot(s) — {dropped} of "
                    + $"{tiedByOrder.Count} tied candidate(s) carry no usable mesh colour or have been claimed "
                    + "since the tie was recorded, and deciding over the remainder would be easier than the "
                    + "evidence allows.");
                continue;
            }

            if (minted)
            {
                // THE EVIDENCE, printed whether or not the rule then accepts it: what was written into each
                // slot, and what came back off each candidate surface. On a renderer that discards the restore
                // these agree pairwise and the tie breaks; on one that applies it they are all back at the pose
                // colour and the rule refuses above tolerance. Either way this line says which happened, so a
                // reader never has to infer the mechanism from the verdict.
                Log(
                    logStream,
                    $"{label}: mint-mark tie-break over {slots.Count} slot(s) and {candidates.Count} tied "
                    + $"candidate(s) — marks [{string.Join(" ", slots.Select(DescribeSlotColor))}] vs surfaces "
                    + $"[{string.Join(" ", candidates.Select(DescribeMeshColor))}].");
            }

            var verdict = Sts2SpineGeoClipSlotColorIdentity.Disambiguate(slots, candidates);
            foreach (var (ordinal, order) in verdict.OrderByOrdinal.OrderBy(pair => pair.Key))
            {
                var rid = meshRids[order];
                if (!claimed.Add(rid))
                {
                    continue;
                }

                var method = tiedByOrder.GetValueOrDefault(order, Sts2SpineGeoClipAtlas.MatchContainment);
                association[ordinal] = rid;
                claimProvenance[ordinal] = minted
                    ? Sts2SpineGeoClipOwnership.AtlasMintMarkClaim(method)
                    : Sts2SpineGeoClipOwnership.AtlasSlotColorClaim(method);
                unresolved.Remove(ordinal);
                if (minted)
                {
                    mintClaims += 1;
                }
                else
                {
                    claims += 1;
                }

                var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                Log(
                    logStream,
                    $"{label}: slot {info?.SlotIndex ?? ordinal} ('{info?.SlotName}') associated by "
                    + (minted
                        ? $"atlas+mint-mark ({method}) — the atlas tied {tiedByOrder.Count} candidate(s) over one "
                          + "region and exactly one of them came back carrying the colour this bake wrote to this "
                          + "slot before its surface was created."
                        : $"atlas+slot-colour ({method}) — the atlas tied {tiedByOrder.Count} candidate(s) over "
                          + "one region and this slot's own colour at the sampled pose matches exactly one of "
                          + "them."));
            }

            foreach (var refusal in verdict.Refused)
            {
                var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == refusal.Ordinal);
                Log(
                    logStream,
                    $"{label}: slot {info?.SlotIndex ?? refusal.Ordinal} ('{info?.SlotName}') NOT resolved by "
                    + $"{(minted ? "mint mark" : "slot colour")} ({refusal.Reason}); it stays unassociated rather "
                    + $"than being handed one of the {tiedByOrder.Count} tied candidate(s).");
            }
        }

        return (claims, mintClaims);
    }

    /// <summary>No mint mark was written — the shape every non-re-mint path hands the tie-break.</summary>
    private static readonly Dictionary<int, double[]> EmptyMintMarks = [];

    private static string DescribeSlotColor(Sts2SpineGeoClipSlotColorIdentity.SlotColor slot)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"ordinal {slot.Ordinal}=({slot.R:0.####},{slot.G:0.####},{slot.B:0.####},{slot.A:0.####})");

    private static string DescribeMeshColor(Sts2SpineGeoClipSlotColorIdentity.MeshColor mesh)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"order {mesh.Order}=({mesh.R:0.####},{mesh.G:0.####},{mesh.B:0.####},{mesh.A:0.####})");

    // This is deliberately AFTER association: these are VALIDATED candidates with no claimant, not rejected
    // sweep probes. It reads at most the configured cap and emits only generic geometry plus sweep-relative
    // provenance — never raw RIDs, slot names, resource paths, or anything copied into a served manifest.
    private static void LogForeignCandidateDiagnostics(
        IReadOnlyList<ulong> meshRids,
        IReadOnlySet<ulong> claimed,
        int limit,
        int foreignCount,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> atlasPageSizes,
        ILogStream? logStream)
    {
        if (limit <= 0 || foreignCount <= 0)
        {
            return;
        }

        var summaries = new List<Sts2SpineGeoClipForeignCandidateDiagnostics.Summary>(limit);
        for (var order = 0; order < meshRids.Count && summaries.Count < limit; order += 1)
        {
            if (claimed.Contains(meshRids[order]) || TryReadSurface(meshRids[order]) is not { } read)
            {
                continue;
            }

            summaries.Add(new Sts2SpineGeoClipForeignCandidateDiagnostics.Summary(
                order,
                read.X.Length,
                read.Indices.Length,
                Sts2SpineGeoClipAtlas.ClassifyUvProvenance(
                    RangeMin(read.U), RangeMin(read.V), RangeMax(read.U), RangeMax(read.V), atlas, atlasPageSizes),
                RangeMin(read.X), RangeMin(read.Y), RangeMax(read.X), RangeMax(read.Y),
                RangeMin(read.U), RangeMin(read.V), RangeMax(read.U), RangeMax(read.V)));
        }

        var lines = Sts2SpineGeoClipForeignCandidateDiagnostics.Format(summaries, limit);
        if (lines.Count == 0)
        {
            return;
        }

        Log(logStream, $"foreign-candidate diagnostics {lines.Count}/{foreignCount} (cap={limit});");
        foreach (var line in lines)
        {
            Log(logStream, $"foreign-candidate {line}");
        }
    }

    private static double RangeMin(IReadOnlyList<double> values) => values.Count == 0 ? 0d : values.Min();

    private static double RangeMax(IReadOnlyList<double> values) => values.Count == 0 ? 0d : values.Max();

    /// <summary>What the atlas-first SHADOW arm found, beside the real association it is compared against.</summary>
    private sealed record AtlasFirstShadow(
        int Resolved,
        int Agreed,
        int Disagreed,
        IReadOnlyList<string> Disagreements);

    /// <summary>The most disagreeing ordinals a manifest will carry; the rest are counted, not named.</summary>
    private const int MaxAtlasFirstDisagreements = 12;

    /// <summary>
    /// THE SHADOW ARM. Re-run association from the ATLAS ALONE — no colour probe, no elimination — and compare
    /// it, ordinal by ordinal, against the association the bake actually shipped.
    ///
    /// <para>WHY IT IS WORTH THE MICROSECONDS. The colour probe is the expensive half of a bake: an awaited frame
    /// per probe position plus a readback per mesh per frame, and it is what `bakeColorRead` above now prices.
    /// The atlas is free by comparison — the uv boxes it needs are already read and cached in the dictionary
    /// this is handed. Whether the cheap evidence could have done DISCOVERY, rather than only tie-breaking behind
    /// the expensive one, decides whether a later round may narrow the probe. That question cannot be answered
    /// by argument, and this is the measurement that replaces one.</para>
    ///
    /// <para>IT ACTS ON NOTHING. The real association is already final when this runs; the return value reaches
    /// three report fields and one log line, and no code reads it back. A shadow arm that could change an
    /// artifact would be a second association algorithm shipping untested, which is the opposite of the point.</para>
    /// </summary>
    private static AtlasFirstShadow RunAtlasFirstShadow(
        IReadOnlyDictionary<int, ulong> association,
        IReadOnlyDictionary<int, int> firstVisible,
        IReadOnlyList<PoseFrame> poses,
        IReadOnlyList<SlotInfo> slotTable,
        IReadOnlyList<ulong> meshRids,
        IReadOnlyDictionary<ulong, int> ridOrder,
        Dictionary<ulong, Sts2SpineGeoClipAtlas.MeshUvBox?> uvBoxes,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> atlasPageSizes,
        ILogStream? logStream,
        string label)
    {
        // Every visible slot, at the pose its attachment was read at — the same input the real arm's atlas
        // fallback used, so a disagreement is about the ALGORITHM and never about which pose was looked at.
        var attachmentByOrdinal = firstVisible.Keys.ToDictionary(
            ordinal => ordinal,
            ordinal => AttachmentNameAt(poses, firstVisible[ordinal], ordinal));

        // The uv boxes the real association already read and cached. Meshes whose surface could not be read at
        // all are absent from the candidate set exactly as they are for the real arm; a mesh that was never
        // looked at (nothing ever considered it) is read once here, which is the only cost this arm adds. Armed,
        // every box is already in the cache — the atlas-first pass read them all before the first probe frame.
        var boxes = ReadUvBoxes(meshRids, ridOrder, uvBoxes);

        var shadow = Sts2SpineGeoClipAtlas.AssociateAll(attachmentByOrdinal, boxes, atlas, atlasPageSizes);

        var agreed = 0;
        var disagreed = 0;
        var notes = new List<string>();
        var logLines = new List<string>();
        foreach (var match in shadow.Resolved)
        {
            // The real arm's answer for this ordinal, in the same `Order` handle the shadow speaks in.
            var realOrder = association.TryGetValue(match.Ordinal, out var rid)
                ? ridOrder.GetValueOrDefault(rid, -1)
                : -1;
            if (realOrder == match.Order)
            {
                agreed += 1;
                continue;
            }

            disagreed += 1;
            if (notes.Count >= MaxAtlasFirstDisagreements)
            {
                continue;
            }

            var info = slotTable.FirstOrDefault(candidate => candidate.Ordinal == match.Ordinal);

            // TWO SPELLINGS of the same disagreement, and the difference is deliberate. The MANIFEST entry
            // carries indices only — slot index, mesh orders, match method — because a geoclip ships computed
            // outputs and never rig or authoring identifiers, and a slot name and an atlas region name are both.
            // The LOG may name them: it is a developer's console, not an artifact.
            notes.Add(
                $"{info?.SlotIndex ?? match.Ordinal}:{match.Order}vs"
                + (realOrder >= 0 ? realOrder.ToString(CultureInfo.InvariantCulture) : "none")
                + $":{match.Method}");
            logLines.Add(
                $"slot {info?.SlotIndex ?? match.Ordinal} ('{info?.SlotName}') atlas-first mesh #{match.Order} "
                + $"({match.Method}, region '{match.RegionName}') vs measured mesh "
                + (realOrder >= 0 ? $"#{realOrder}" : "<none>"));
        }

        Log(
            logStream,
            $"{label}: atlasFirstResolved={shadow.Resolved.Count}/{firstVisible.Count} "
            + $"atlasFirstAgreed={agreed} atlasFirstDisagreed={disagreed} "
            + $"atlasFirstRefused={shadow.Refused.Count} — SHADOW ARM ONLY: the association above is unchanged, "
            + "and 'disagreed' counts ordinals where the atlas alone picked a DIFFERENT mesh than the colour "
            + "probe did, which is the number that decides whether the probe can ever be narrowed."
            + (shadow.Refused.Count > 0
                ? " Refusals: "
                  + string.Join(
                      " ",
                      shadow.Refused
                          .Take(MaxAtlasFirstDisagreements)
                          .Select(refusal => $"{refusal.Ordinal}={refusal.Reason}"))
                  + (shadow.Refused.Count > MaxAtlasFirstDisagreements ? " …" : string.Empty)
                : string.Empty));
        foreach (var line in logLines)
        {
            Log(logStream, $"{label}: atlas-first disagreement — {line}");
        }

        return new AtlasFirstShadow(shadow.Resolved.Count, agreed, disagreed, notes);
    }

    /// <summary>How many of a group's slots the decode left with no mesh at all.</summary>
    private static int StarvedSlots(GeoClipProbeDecode decode, int memberCount)
        => Enumerable.Range(0, memberCount).Count(member => decode.MeshesBySlot[member].Count == 0);

    /// <summary>
    /// How many decoded (slot, mesh) pairs land on a mesh the ARMED atlas-first pass had already given to a
    /// different ordinal — the third widen shape.
    /// </summary>
    /// <remarks>
    /// Zero by construction while the armed read set is exactly "the unclaimed meshes": an atlas-first claim IS a
    /// claim, so its mesh is not in the set being decoded. It is COUNTED rather than assumed away because it is
    /// the first thing a looser read-set filter would get wrong, and the cost of asking is a walk over one
    /// group's decode.
    /// </remarks>
    private static int ConflictingAtlasFirstDecodes(
        GeoClipProbeDecode decode,
        IReadOnlyList<ulong> readMeshes,
        IReadOnlyDictionary<ulong, int> ridOrder,
        IReadOnlyDictionary<int, int> atlasFirstOrdinalByOrder,
        IReadOnlyList<ProbeMember> members)
    {
        if (atlasFirstOrdinalByOrder.Count == 0)
        {
            return 0;
        }

        var conflicts = 0;
        for (var member = 0; member < members.Count; member += 1)
        {
            foreach (var mesh in decode.MeshesBySlot[member])
            {
                if (mesh < 0 || mesh >= readMeshes.Count)
                {
                    continue;
                }

                var order = ridOrder.GetValueOrDefault(readMeshes[mesh], -1);
                if (atlasFirstOrdinalByOrder.TryGetValue(order, out var owner)
                    && owner != members[member].Ordinal)
                {
                    conflicts += 1;
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Every swept mesh's uv box, read once and cached. A mesh's uvs address the same atlas region for the whole
    /// clip, so the cache is what lets the armed pass, the tie-breaker and the fallback all ask without paying
    /// three times; a mesh whose surface cannot be read at all is absent from the result, exactly as it is from
    /// every other candidate set.
    /// </summary>
    private static List<Sts2SpineGeoClipAtlas.MeshUvBox> ReadUvBoxes(
        IReadOnlyList<ulong> meshRids,
        IReadOnlyDictionary<ulong, int> ridOrder,
        Dictionary<ulong, Sts2SpineGeoClipAtlas.MeshUvBox?> uvBoxes,
        Dictionary<ulong, Sts2SpineGeoClipSlotColorIdentity.MeshColor?>? meshColors = null)
    {
        var boxes = new List<Sts2SpineGeoClipAtlas.MeshUvBox>(meshRids.Count);
        foreach (var rid in meshRids)
        {
            if (!uvBoxes.TryGetValue(rid, out var cached))
            {
                var read = TryReadSurface(rid);
                cached = read is null || read.U.Length == 0
                    ? null
                    : new Sts2SpineGeoClipAtlas.MeshUvBox(
                        ridOrder.GetValueOrDefault(rid, -1), read.U.Min(), read.V.Min(), read.U.Max(), read.V.Max());
                uvBoxes[rid] = cached;
                CacheMeshColor(meshColors, rid, ridOrder, read);
            }

            if (cached is { } box)
            {
                boxes.Add(box);
            }
        }

        return boxes;
    }

    /// <summary>
    /// The single colour a surface's vertices carry, for the slot-colour tie-break — recorded from a read that
    /// was going to happen anyway, so the arm costs no extra device round trip.
    ///
    /// <para>NULL means "no usable colour", and there are three ways to get there: the surface did not read, it
    /// carries no colour array, or its vertices do NOT agree on one colour. The last is the interesting one. The
    /// tie-break compares a mesh against a slot's single pose colour, so a mesh whose vertices disagree has no
    /// colour to compare — collapsing it to a mean would invent one, and a mean is exactly the kind of number
    /// that would then land near some slot by accident. The spread gate is the tie-break's own tolerance,
    /// because agreeing to within the tolerance is what the comparison downstream means by "the same colour".
    /// </para>
    /// </summary>
    private static void CacheMeshColor(
        Dictionary<ulong, Sts2SpineGeoClipSlotColorIdentity.MeshColor?>? meshColors,
        ulong rid,
        IReadOnlyDictionary<ulong, int> ridOrder,
        MeshRead? read)
    {
        if (meshColors is null)
        {
            return;
        }

        meshColors[rid] = UniformSurfaceColor(ridOrder.GetValueOrDefault(rid, -1), read);
    }

    private static Sts2SpineGeoClipSlotColorIdentity.MeshColor? UniformSurfaceColor(int order, MeshRead? read)
    {
        if (order < 0 || read is null || read.Colors.Length == 0)
        {
            return null;
        }

        Span<double> low = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        Span<double> high = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        foreach (var vertex in read.Colors)
        {
            if (vertex.Length < 4)
            {
                return null;
            }

            for (var channel = 0; channel < 4; channel += 1)
            {
                low[channel] = Math.Min(low[channel], vertex[channel]);
                high[channel] = Math.Max(high[channel], vertex[channel]);
            }
        }

        for (var channel = 0; channel < 4; channel += 1)
        {
            if (high[channel] - low[channel] > Sts2SpineGeoClipSlotColorIdentity.DefaultTolerance)
            {
                return null;
            }
        }

        return new Sts2SpineGeoClipSlotColorIdentity.MeshColor(
            order,
            (low[0] + high[0]) / 2,
            (low[1] + high[1]) / 2,
            (low[2] + high[2]) / 2,
            (low[3] + high[3]) / 2);
    }

    /// <summary>
    /// ONE run of a group's probe schedule: baseline, one awaited frame per probe frame with every member set
    /// to its nudged colour for that frame, a readback per mesh per frame, then restore and one more frame.
    /// </summary>
    /// <remarks>
    /// Extracted so a group can be run twice — once on the plan's own channels and, if too many slots come back
    /// starved, once more on alpha, which is the schedule every recorded association pair was produced under.
    /// Restoring INSIDE the run is what makes the second call a clean repeat rather than a probe of a rig still
    /// wearing the first one's colours.
    /// </remarks>
    private static async Task<int[]> RunGroupProbeAsync(
        Viewport rootViewport,
        GeoClipProbePlan plan,
        IReadOnlyList<ProbeMember> members,
        IReadOnlyList<ulong> meshRids,
        GeoClipDrawBudget drawBudget)
    {
        var channels = Sts2SpineGeoClipProbe.ChannelsOf(plan.Channels);
        var baseline = ReadColorSums(meshRids);
        var observed = new int[meshRids.Count];

        for (var probeFrame = 0; probeFrame < plan.FrameCount; probeFrame += 1)
        {
            for (var member = 0; member < members.Count; member += 1)
            {
                var probed = members[member];
                var color = ProbeColorAt(plan, member, probeFrame, probed.BaseColor);
                TryRun(() => Invoke(probed.Slot, "set_color", color));
            }

            await AwaitBudgetedFramesAsync(
                rootViewport,
                1,
                drawBudget,
                GeoClipDrawSite.ProbeFrame,
                Sts2RenderPhaseProfile.Phase.BakeProbeFrameWait);

            for (var mesh = 0; mesh < meshRids.Count; mesh += 1)
            {
                // ONE readback, every sum. The device stall is per CALL, so the extra components are free.
                var now = MeteredColorSums(meshRids[mesh]);
                for (var channel = 0; channel < channels.Count; channel += 1)
                {
                    var sum = channels[channel].Sum;
                    if (MeshColorMoved(baseline[mesh][sum], now[sum]))
                    {
                        observed[mesh] |= 1
                            << Sts2SpineGeoClipProbe.PositionOf(probeFrame, channel, channels.Count);
                    }
                }
            }
        }

        // Restore before anything else looks at this rig. One frame for the whole group, where the old pass
        // paid one per slot.
        foreach (var member in members)
        {
            TryRun(() => Invoke(member.Slot, "set_color", member.BaseColor));
        }

        await AwaitBudgetedFramesAsync(
            rootViewport,
            1,
            drawBudget,
            GeoClipDrawSite.ProbeFrame,
            Sts2RenderPhaseProfile.Phase.BakeProbeRestoreWait);
        return observed;
    }

    /// <summary>The Godot adapter over the core's probe-colour decision; the decision itself is offline.</summary>
    private static Color ProbeColorAt(GeoClipProbePlan plan, int slot, int frame, Color baseColor)
    {
        var (r, g, b, a) = Sts2SpineGeoClipProbe.ProbeColorFor(
            plan, slot, frame, baseColor.R, baseColor.G, baseColor.B, baseColor.A);
        return new Color(r, g, b, a);
    }

    /// <summary>One component of a colour pushed well away from wherever it sits; the others left alone.</summary>
    private static Color NudgeComponent(Color color, int component)
    {
        var (r, g, b, a) = Sts2SpineGeoClipProbe.NudgeComponent(
            color.R, color.G, color.B, color.A, component);
        return new Color(r, g, b, a);
    }

    /// <summary>
    /// One member of a probe GROUP: a slot that was resolvable at the group's frame, carrying the colour to
    /// restore and the nudged colour to probe with.
    ///
    /// <para>The slot object is HELD for the length of the group's probe rather than re-read per frame. The
    /// skeleton owns its slots for its whole life and the pre-grouping pass already held the same reference
    /// across two awaited frames; if that ever stopped being true the symptom is loud — no mesh moves, every
    /// slot in the group falls through to the atlas fallback — rather than a wrong pairing.</para>
    /// </summary>
    private sealed record ProbeMember(int Ordinal, string Label, GodotObject Slot, Color BaseColor)
    {
        /// <summary>
        /// The nudge, unchanged from the one-at-a-time pass: push the alpha well away from wherever it sits,
        /// so the vertex colours the renderer writes for this slot's mesh have to move.
        /// </summary>
        public Color ProbeColor => NudgeComponent(BaseColor, component: 3);
    }

    /// <summary>
    /// How many colour-only reads the association has taken, and how long they took, since
    /// <see cref="ResetColorReadMeter"/>. Reported in the group-probe log line so the phase split does not have
    /// to be INFERRED from a frame-rate A/B: the association's non-frame cost is exactly this number.
    /// </summary>
    private static long _colorReads;

    private static long _colorReadTicks;

    private static void ResetColorReadMeter()
    {
        _colorReads = 0;
        _colorReadTicks = 0;
    }

    private static string ColorReadMeter()
        => $"{_colorReads} colour read(s) in "
            + (_colorReadTicks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture)
            + " ms";

    /// <summary>One metered colour-only read, in the NaN spelling the probe's predicate expects.</summary>
    private static double MeteredColorChecksum(ulong ridId)
    {
        var start = Stopwatch.GetTimestamp();
        var value = TryReadColorChecksum(ridId) ?? double.NaN;
        _colorReadTicks += Stopwatch.GetTimestamp() - start;
        _colorReads += 1;
        return value;
    }

    private static double[] ReadColorChecksums(IReadOnlyList<ulong> meshRids)
    {
        var checksums = new double[meshRids.Count];
        for (var i = 0; i < meshRids.Count; i += 1)
        {
            checksums[i] = MeteredColorChecksum(meshRids[i]);
        }

        return checksums;
    }

    /// <summary>One metered colour-only read, kept as every sum it can be read as.</summary>
    private static GeoClipColorSums MeteredColorSums(ulong ridId)
    {
        var start = Stopwatch.GetTimestamp();
        var value = TryReadColorSums(ridId) ?? GeoClipColorSums.NotRead;
        _colorReadTicks += Stopwatch.GetTimestamp() - start;
        _colorReads += 1;
        return value;
    }

    private static GeoClipColorSums[] ReadColorSums(IReadOnlyList<ulong> meshRids)
    {
        var sums = new GeoClipColorSums[meshRids.Count];
        for (var i = 0; i < meshRids.Count; i += 1)
        {
            sums[i] = MeteredColorSums(meshRids[i]);
        }

        return sums;
    }

    /// <summary>
    /// The moved/not-moved predicate, unchanged. A surface that could not be read at all reads back NaN, and
    /// a mesh that appeared or disappeared between the two reads counts as having moved — which is why the
    /// NaN comparison is asymmetric rather than a plain delta.
    /// </summary>
    private static bool MeshColorMoved(double before, double after)
        => double.IsNaN(before) != double.IsNaN(after)
            || (!double.IsNaN(after) && Math.Abs(after - before) > 1e-6);

    /// <summary>
    /// The pre-grouping probe for ONE slot — baseline, nudge, a frame, diff, restore, a frame — kept as the
    /// repair arm the grouped probe falls back to when it finds a mesh that answers to no single slot.
    /// </summary>
    private static async Task<List<ulong>> ProbeOneSlotAsync(
        Viewport rootViewport,
        ProbeMember member,
        IReadOnlyList<ulong> meshRids,
        GeoClipDrawBudget drawBudget)
    {
        var before = ReadColorChecksums(meshRids);
        TryRun(() => Invoke(member.Slot, "set_color", member.ProbeColor));
        await AwaitBudgetedFramesAsync(
            rootViewport,
            1,
            drawBudget,
            GeoClipDrawSite.ProbeFrame,
            Sts2RenderPhaseProfile.Phase.BakeProbeFrameWait);

        var moved = new List<ulong>();
        for (var i = 0; i < meshRids.Count; i += 1)
        {
            if (MeshColorMoved(before[i], MeteredColorChecksum(meshRids[i])))
            {
                moved.Add(meshRids[i]);
            }
        }

        TryRun(() => Invoke(member.Slot, "set_color", member.BaseColor));
        await AwaitBudgetedFramesAsync(
            rootViewport,
            1,
            drawBudget,
            GeoClipDrawSite.ProbeFrame,
            Sts2RenderPhaseProfile.Phase.BakeProbeRestoreWait);
        return moved;
    }

    private static string? AttachmentNameAt(IReadOnlyList<PoseFrame> poses, int frame, int ordinal)
        => frame >= 0 && frame < poses.Count
            ? poses[frame].Slots.FirstOrDefault(slot => slot.Ordinal == ordinal)?.AttachmentName
            : null;

    /// <summary>
    /// The live half of the atlas fallback: read each candidate mesh's uv box (cached — a mesh's uvs address the
    /// same region for the whole clip) and hand the pure matcher a set of UNCLAIMED candidates.
    /// </summary>
    private static (ulong? Rid, Sts2SpineGeoClipAtlas.RegionMatch Match) MatchByAtlasRegion(
        string? attachmentName,
        IReadOnlyList<ulong> candidateRids,
        IReadOnlySet<ulong> claimed,
        IReadOnlyList<ulong> allMeshRids,
        IReadOnlyDictionary<ulong, int> ridOrder,
        Dictionary<ulong, Sts2SpineGeoClipAtlas.MeshUvBox?> uvBoxes,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> atlasPageSizes,
        Dictionary<ulong, Sts2SpineGeoClipSlotColorIdentity.MeshColor?>? meshColors = null)
    {
        var boxes = new List<Sts2SpineGeoClipAtlas.MeshUvBox>();
        foreach (var rid in candidateRids)
        {
            if (claimed.Contains(rid))
            {
                continue;
            }

            if (!uvBoxes.TryGetValue(rid, out var cached))
            {
                // `Order` is the mesh's position in the merged sweep result, so a verdict can be reported (and
                // recorded in `associationPairs`) without carrying raw RID ids into the artifact.
                var read = TryReadSurface(rid);
                cached = read is null || read.U.Length == 0
                    ? null
                    : new Sts2SpineGeoClipAtlas.MeshUvBox(
                        ridOrder.GetValueOrDefault(rid, -1), read.U.Min(), read.V.Min(), read.U.Max(), read.V.Max());
                uvBoxes[rid] = cached;
                CacheMeshColor(meshColors, rid, ridOrder, read);
            }

            if (cached is { } box)
            {
                boxes.Add(box);
            }
        }

        var match = Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion(
            attachmentName, atlas, atlasPageSizes, boxes);
        return match.Order >= 0 && match.Order < allMeshRids.Count
            ? (allMeshRids[match.Order], match)
            : (null, match);
    }

    private static List<int[]> BuildAssociationPairs(
        IReadOnlyList<SlotInfo> slotTable,
        IReadOnlyList<ulong> meshRids,
        IReadOnlyDictionary<int, ulong> association)
    {
        var ridOrder = BuildRidOrder(meshRids);
        var pairs = new List<int[]>(association.Count);
        foreach (var info in slotTable)
        {
            if (association.TryGetValue(info.Ordinal, out var rid))
            {
                pairs.Add([info.SlotIndex, ridOrder.GetValueOrDefault(rid, -1)]);
            }
        }

        return [.. pairs.OrderBy(pair => pair[0])];
    }

    // A mesh's ORDER in the merged sweep result is how the artifact refers to it: raw RID ids are per-session
    // handles that mean nothing to a later reader, and the order is stable within one bake.
    private static Dictionary<ulong, int> BuildRidOrder(IReadOnlyList<ulong> meshRids)
    {
        var order = new Dictionary<ulong, int>(meshRids.Count);
        for (var i = 0; i < meshRids.Count; i += 1)
        {
            order.TryAdd(meshRids[i], i);
        }

        return order;
    }

    // ── Frame assembly ───────────────────────────────────────────────────────────────────────────────

    // A slot that WAS associated and then read back empty during the geometry pass is a different animal from a
    // hidden slot, and Phase 1 emitted both as "hidden" with no way to tell them apart. Counting it is how the
    // "nothing mints a mesh after bracketPost" claim gets checked instead of assumed.
    private sealed class StaleMeshTally
    {
        public int Frames { get; set; }

        public HashSet<int> Slots { get; } = [];
    }

    private static GeoClipFrameSample BuildFrameSample(
        PoseFrame pose,
        IReadOnlyDictionary<int, ulong> association,
        StaleMeshTally stale,
        ILogStream? logStream,
        string label,
        int frameIndex)
    {
        var slots = new List<GeoClipSlotSample>(pose.Slots.Count);
        foreach (var slot in pose.Slots)
        {
            if (!slot.RequiresDrawing)
            {
                slots.Add(GeoClipSlotSample.Hidden(
                    slot.SlotIndex,
                    slot.Color,
                    slot.BlendMode,
                    slot.AttachmentName,
                    requiresDrawing: false));
                continue;
            }

            if (slot.AttachmentName is null)
            {
                slots.Add(GeoClipSlotSample.Hidden(
                    slot.SlotIndex,
                    slot.Color,
                    slot.BlendMode,
                    attachmentName: null,
                    requiresDrawing: true));
                continue;
            }

            if (!association.TryGetValue(slot.Ordinal, out var rid))
            {
                if (frameIndex == 0)
                {
                    Log(
                        logStream,
                        $"{label}: slot {slot.SlotIndex} shows attachment '{slot.AttachmentName}' but has no associated "
                        + "mesh, so it is baked as hidden for every frame.");
                }

                slots.Add(GeoClipSlotSample.Hidden(slot.SlotIndex, slot.Color, slot.BlendMode, slot.AttachmentName));
                continue;
            }

            var read = TryReadSurface(rid);
            if (read is null || read.X.Length == 0)
            {
                stale.Frames += 1;
                if (stale.Slots.Add(slot.SlotIndex))
                {
                    Log(
                        logStream,
                        $"{label}: slot {slot.SlotIndex} ('{slot.AttachmentName}') has an ASSOCIATED mesh that read "
                        + $"back empty at frame {frameIndex}; baked as hidden there. The mesh RID was valid at "
                        + "acquisition, so the rig re-minted or recycled it mid-bake.");
                }

                slots.Add(GeoClipSlotSample.Hidden(slot.SlotIndex, slot.Color, slot.BlendMode, slot.AttachmentName));
                continue;
            }

            slots.Add(new GeoClipSlotSample(
                slot.SlotIndex,
                slot.AttachmentName,
                slot.Color,
                slot.BlendMode,
                HasGeometry: true,
                read.X,
                read.Y,
                read.U,
                read.V,
                read.Indices));
        }

        return new GeoClipFrameSample(pose.T, pose.DrawOrder, pose.Bounds, slots);
    }

    // ── Atlas + pages ────────────────────────────────────────────────────────────────────────────────

    private static Resource? TryGetAtlasResource(Node spineNode)
    {
        var skeletonData = TryGet(() => spineNode.Get("skeleton_data_res"), default);
        if (skeletonData.VariantType != Variant.Type.Object
            || TryGet(() => skeletonData.AsGodotObject()) is not Resource dataResource)
        {
            return null;
        }

        var atlasVariant = TryGet(() => dataResource.Get("atlas_res"), default);
        return atlasVariant.VariantType == Variant.Type.Object
            && TryGet(() => atlasVariant.AsGodotObject()) is Resource atlasResource
            ? atlasResource
            : null;
    }

    /// <summary>
    /// The region table. The runtime `atlas_data` property reads back EMPTY on this build, so the primary
    /// source is the IMPORTED atlas on disk: a `.spatlas` JSON envelope carrying the text atlas verbatim,
    /// reached through the atlas resource's `.import` sidecar. The runtime property is still tried as a
    /// fallback, so a build that starts populating it needs no change here.
    /// </summary>
    private static SpineAtlasDocument ReadAtlasDocument(Node spineNode, ILogStream? logStream, string label)
    {
        var empty = new SpineAtlasDocument([], []);
        var atlasResource = TryGetAtlasResource(spineNode);
        if (atlasResource is null)
        {
            Log(logStream, $"{label}: no atlas resource on the SpineSprite; every part falls back to page 0.");
            return empty;
        }

        var atlasPath = TryGet(() => atlasResource.ResourcePath, string.Empty) ?? string.Empty;
        if (atlasPath.Length > 0)
        {
            var importText = TryGet(
                () => System.Text.Encoding.UTF8.GetString(Godot.FileAccess.GetFileAsBytes($"{atlasPath}.import")),
                string.Empty) ?? string.Empty;
            var importedPath = Sts2SpineGeoClipAtlas.ExtractImportedResourcePath(importText, ".spatlas");
            if (importedPath.Length > 0)
            {
                var envelope = TryGet(() => Godot.FileAccess.GetFileAsBytes(importedPath), []) ?? [];
                var text = Sts2SpineGeoClipAtlas.ExtractAtlasTextFromEnvelope(envelope);
                if (text.Length > 0)
                {
                    var parsed = Sts2SpineAtlasText.Parse(text);
                    Log(
                        logStream,
                        $"{label}: atlas from '{importedPath}' — pages={parsed.Pages.Count} regions={parsed.Regions.Count}.");
                    return parsed;
                }

                Log(logStream, $"{label}: '{importedPath}' carried no `atlas_data` text.");
            }
            else
            {
                Log(logStream, $"{label}: '{atlasPath}.import' named no imported .spatlas.");
            }
        }

        var runtimeText = TryGet(() => atlasResource.Get("atlas_data").AsString(), string.Empty) ?? string.Empty;
        if (runtimeText.Length > 0)
        {
            var parsed = Sts2SpineAtlasText.Parse(runtimeText);
            Log(
                logStream,
                $"{label}: atlas from the runtime resource property — pages={parsed.Pages.Count} regions={parsed.Regions.Count}.");
            return parsed;
        }

        Log(
            logStream,
            $"{label}: could not read the atlas text from '{atlasPath}' by either route; page association falls back "
            + "to page 0 for every part.");
        return empty;
    }

    /// <summary>
    /// One atlas page, resolved to bytes ONCE for the whole rig. <see cref="Bytes"/> is null when the caller
    /// already holds this page (it is described in the manifest and not written).
    /// </summary>
    private sealed record CollectedPage(GeoClipPage Page, byte[]? Bytes, string Method);

    /// <summary>
    /// Resolve the RAW atlas page images. current does not repack: a part addresses a page-pixel rect of the page it
    /// came from, so the page has to ship as-is.
    ///
    /// <para>THREE WAYS TO GET THE BYTES, and the cheap ones are tried first.
    /// <c>Texture2D.GetImage()</c> → <c>Decompress()</c> → <c>SavePngToBuffer()</c> measured 161 ms for the
    /// merchant's four pages, on every bake, against a 271 ms budget, so a byte copy is worth reaching for twice:
    /// <list type="number">
    ///   <item>the SOURCE image, when it is a PNG whose own header agrees with the runtime texture and the atlas
    ///   text (<see cref="Sts2SpineGeoClipPageSource"/>) — which an EXPORTED build never ships, so this one only
    ///   ever fires in the editor or against a loose-file install;</item>
    ///   <item>the IMPORTED <c>.ctex</c> the source's <c>.import</c> sidecar names, whose payload is already a
    ///   lossless WebP (<see cref="Sts2SpineGeoClipCtexSource"/>) — the one that fires on a shipped build;</item>
    ///   <item>the re-encode, which always works and is the reason both of the above may decline freely.</item>
    /// </list>
    /// Every clause that declines is named in the log, so a run that shows no saving says which one.</para>
    ///
    /// <para>Called ONCE per rig bake and the bytes reused across its poses, which is what the store's
    /// content-addressed page folder already assumes.</para>
    /// </summary>
    private static List<CollectedPage> CollectPages(
        Node spineNode,
        SpineAtlasDocument atlas,
        IReadOnlySet<string>? knownContentIds,
        ILogStream? logStream,
        string label)
    {
        var pages = new List<CollectedPage>();
        var atlasResource = TryGetAtlasResource(spineNode);
        var textures = atlasResource is null
            ? null
            : TryGet(() => atlasResource.Call("get_textures").AsGodotArray());
        if (textures is null || textures.Count == 0)
        {
            Log(logStream, $"{label}: the atlas resource handed back no page textures; NO page PNG was written.");
            return pages;
        }

        if (atlas.Pages.Count > 0 && atlas.Pages.Count != textures.Count)
        {
            Log(
                logStream,
                $"{label}: the atlas text declares {atlas.Pages.Count} pages but the resource handed back "
                + $"{textures.Count} textures; page ids follow the TEXTURE order.");
        }

        var mode = Sts2SpineGeoClipPageSource.ModeFromEnvironment();
        var ctexMode = Sts2SpineGeoClipCtexSource.ModeFromEnvironment();
        var atlasPath = TryGet(() => atlasResource!.ResourcePath, string.Empty) ?? string.Empty;
        for (var index = 0; index < textures.Count; index += 1)
        {
            var fileName = $"page-{index.ToString(CultureInfo.InvariantCulture)}.png";
            if (TryGet(() => textures[index].AsGodotObject()) is not Texture2D texture)
            {
                Log(logStream, $"{label}: page {index} is not a Texture2D; skipped (parts on it will crop nothing).");
                continue;
            }

            var declared = index < atlas.Pages.Count ? atlas.Pages[index] : null;
            var sourcePath = TryGet(() => texture.ResourcePath, string.Empty) ?? string.Empty;
            var width = TryGet(() => texture.GetWidth(), 0);
            var height = TryGet(() => texture.GetHeight(), 0);

            byte[] bytes = [];
            var method = "encode";
            if (mode != Sts2SpineGeoClipPageSource.Mode.Disabled
                || ctexMode != Sts2SpineGeoClipCtexSource.Mode.Disabled)
            {
                // TWO PLACES A PAGE'S FILE CAN BE. The texture's own resource path when the importer handed back a
                // standalone image, and — when it did not, which a sub-resource of the imported atlas would not —
                // the atlas's own directory plus the page name the atlas TEXT declares, which is where every
                // Spine runtime looks. Both candidates go through the same checks; only the path guessing differs.
                var refusals = new List<string>();
                var tried = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidatePath in new[]
                         {
                             sourcePath,
                             Sts2SpineGeoClipPageSource.SiblingPath(atlasPath, declared?.Name),
                         })
                {
                    if (candidatePath.Length == 0 || !tried.Add(candidatePath))
                    {
                        continue;
                    }

                    var candidate = TryGet(() => Godot.FileAccess.GetFileAsBytes(candidatePath), []) ?? [];
                    var sidecar = TryGet(
                        () => System.Text.Encoding.UTF8.GetString(
                            Godot.FileAccess.GetFileAsBytes($"{candidatePath}.import")),
                        string.Empty) ?? string.Empty;
                    if (mode != Sts2SpineGeoClipPageSource.Mode.Disabled)
                    {
                        var decision = Sts2SpineGeoClipPageSource.Decide(
                            mode,
                            candidatePath,
                            candidate,
                            width,
                            height,
                            declared?.Name,
                            declared?.Width ?? 0,
                            declared?.Height ?? 0,
                            sidecar);
                        if (decision.Accept)
                        {
                            bytes = candidate;
                            method = "source";
                            Log(
                                logStream,
                                $"{label}: page {index} copied verbatim from its source image '{candidatePath}' "
                                + $"({bytes.Length} B, {width}x{height}); no decode, no re-encode.");
                            break;
                        }

                        refusals.Add($"'{candidatePath}' → {decision.Reason}");
                    }

                    // THE SECOND SOURCE, and on an EXPORTED build the only one: the imported `.ctex` the sidecar
                    // names. Tried per candidate rather than once, so the atlas-sibling path gets the same shot
                    // the texture's own resource path does, and refuses through the same list.
                    if (ctexMode == Sts2SpineGeoClipCtexSource.Mode.Disabled)
                    {
                        continue;
                    }

                    var ctexPath = Sts2SpineGeoClipCtexSource.CtexPathFromSidecar(sidecar);
                    if (ctexPath.Length == 0)
                    {
                        refusals.Add($"'{candidatePath}.import' → {(sidecar.Length == 0 ? "no-import-sidecar" : "no-ctex-path")}");
                        continue;
                    }

                    if (!Sts2SpineGeoClipCtexSource.CtexNamesSource(ctexPath, candidatePath))
                    {
                        refusals.Add(
                            $"'{ctexPath}' → ctex-name-mismatch:{Sts2SpineGeoClipPageSource.FileName(ctexPath)}"
                            + $"-vs-{Sts2SpineGeoClipPageSource.FileName(candidatePath)}");
                        continue;
                    }

                    var ctexBytes = TryGet(() => Godot.FileAccess.GetFileAsBytes(ctexPath), []) ?? [];
                    var ctex = Sts2SpineGeoClipCtexSource.Extract(
                        ctexBytes,
                        width,
                        height,
                        declared?.Width ?? 0,
                        declared?.Height ?? 0);
                    if (ctex.Payload is { } payload)
                    {
                        bytes = payload.Bytes;
                        method = "ctex";
                        fileName =
                            $"page-{index.ToString(CultureInfo.InvariantCulture)}."
                            + Sts2SpineGeoClipCtexSource.ExtensionFor(ctexMode);
                        width = payload.Width;
                        height = payload.Height;
                        Log(
                            logStream,
                            $"{label}: page {index} copied verbatim out of the imported texture '{ctexPath}' "
                            + $"({bytes.Length} B of embedded WebP, {width}x{height}, written as '{fileName}'); "
                            + "no decode, no re-encode.");
                        break;
                    }

                    refusals.Add($"'{ctexPath}' → {ctex.Reason}");
                }

                if (bytes.Length == 0)
                {
                    Log(
                        logStream,
                        $"{label}: page {index} is re-encoded from the runtime texture — every passthrough "
                        + $"candidate declined: {string.Join("; ", refusals)}.");
                }
            }

            if (bytes.Length == 0)
            {
                var image = TryGet(() => texture.GetImage());
                if (image is null)
                {
                    Log(logStream, $"{label}: page {index} exposed no image; skipped.");
                    continue;
                }

                // A compressed import cannot be PNG-encoded straight; decompress in place first.
                if (TryGet(() => image.IsCompressed(), false))
                {
                    var error = TryGet(() => image.Decompress(), Error.Failed);
                    if (error != Error.Ok)
                    {
                        Log(logStream, $"{label}: page {index} is compressed and would not decompress ({error}); skipped.");
                        continue;
                    }
                }

                bytes = TryGet(() => image.SavePngToBuffer(), []) ?? [];
                if (bytes.Length == 0)
                {
                    Log(logStream, $"{label}: page {index} encoded to zero PNG bytes; skipped.");
                    continue;
                }

                width = TryGet(() => image.GetWidth(), width);
                height = TryGet(() => image.GetHeight(), height);
            }

            var contentId = Sts2SpineGeoClipPageId.ContentId(bytes);
            var known = knownContentIds is not null && knownContentIds.Contains(contentId);
            if (known)
            {
                Log(
                    logStream,
                    $"{label}: page {index} (sha256 {contentId[..16]}…) is already held by the caller; it is "
                    + "described in the manifest and NOT written.");
            }

            pages.Add(new CollectedPage(
                new GeoClipPage(index, fileName, width, height, contentId),
                known ? null : bytes,
                method));
        }

        return pages;
    }

    /// <summary>
    /// Write the collected pages into one pose's artifact directory and return the manifest entries.
    /// </summary>
    /// <remarks>
    /// The directory keeps the shape it always had — a page PNG beside the manifest — even when a rig bake
    /// resolved the bytes once and shares them across poses, so nothing downstream learns that pages are shared
    /// upstream. A page the caller already holds is described and not written; the caller is the one that has to
    /// resolve it, and it says so by having named the content id in the first place.
    /// </remarks>
    private static List<GeoClipPage> WritePages(
        IReadOnlyList<CollectedPage> collected,
        string directory,
        ILogStream? logStream,
        string label)
    {
        var pages = new List<GeoClipPage>(collected.Count);
        foreach (var page in collected)
        {
            if (page.Bytes is { } bytes)
            {
                try
                {
                    File.WriteAllBytes(Path.Combine(directory, page.Page.File), bytes);
                }
                catch (Exception ex)
                {
                    Log(
                        logStream,
                        $"{label}: could not write page {page.Page.Id} ('{page.Page.File}'): "
                        + $"{ex.GetType().Name}: {ex.Message}");
                    continue;
                }
            }

            pages.Add(page.Page);
        }

        return pages;
    }

    // ── Mesh readback ────────────────────────────────────────────────────────────────────────────────

    // One surface's arrays, flattened into plain CLR arrays so nothing Godot-owned survives the read. Kept
    // separate from the probe's own reader on purpose: the probe is owned by another work stream, and a bake
    // must not be able to break a diagnostic (or vice versa).
    private sealed class MeshRead
    {
        public int SurfaceCount { get; init; }

        // Spine draws 2D: a PackedVector3Array under the vertex key is some other subsystem's geometry that
        // happened to land in the bracketed id space. Recorded rather than inferred from the array lengths,
        // because the reader flattens both spellings into the same X/Y arrays.
        public bool VerticesAreVector2 { get; init; }

        public bool HasUvChannel { get; init; }

        public double[] X { get; init; } = [];

        public double[] Y { get; init; } = [];

        public double[] U { get; init; } = [];

        public double[] V { get; init; } = [];

        public int[] Indices { get; init; } = [];

        public float[][] Colors { get; init; } = [];

        // Position-weighted so a permutation of the same colours is not mistaken for no change. The arithmetic
        // moved to the Godot-free core so the colour-only read below can be PROVED to compute the same double
        // offline; the expression itself is the one this property always had.
        public double ColorChecksum => Sts2SpineGeoClipColorChecksum.FromJagged(Colors);
    }

    /// <summary>
    /// The association probe's read: the surface's COLOUR checksum and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Same answers as <c>TryReadSurface(rid)?.ColorChecksum</c>, clause for clause — an unreadable rid,
    /// a mesh with no surface and a surface with no arrays all answer null (which the caller turns into NaN, and
    /// <see cref="MeshColorMoved"/> treats a NaN/not-NaN transition as movement); a surface whose colour slot is
    /// absent or is not a <c>PackedColorArray</c> answers 0, exactly as an empty <c>Colors</c> did.</para>
    /// <para>WHAT IT SAVES, and what it does not. It skips marshalling the vertex, UV and index arrays the
    /// association throws away, and it does not allocate a <c>float[4]</c> per vertex to hold colours it sums
    /// once. It does NOT skip <c>MeshSurfaceGetArrays</c> itself, because there is no rendering-server call that
    /// returns one array of a surface — and that call, not the marshalling, is where a read's time goes: it is a
    /// buffer readback per vertex/attribute/index buffer, each of which flushes and stalls the device. Measured
    /// (spine-geometry-phase0): a whole-skeleton readback is 7.7 ms for the merchant's 30 meshes = ~0.26 ms per
    /// mesh, on meshes 19 of which have FOUR vertices. Nothing size-proportional can be 0.26 ms on four
    /// vertices, so the marshalling this removes is single-digit microseconds of it.</para>
    /// </remarks>
    private static double? TryReadColorChecksum(ulong ridId) => TryReadColorSums(ridId)?.Combined;

    /// <summary>
    /// The same single read, kept as the combined checksum AND a per-component sum.
    /// </summary>
    /// <remarks>
    /// The extra sums are four multiply-adds per vertex against a call that stalls the device for ~0.26 ms, so
    /// they are free — and they are what lets one awaited frame carry one probe bit per colour component
    /// instead of one in total. <see cref="GeoClipColorSums.Combined"/> is accumulated by the same expression
    /// the single-sum path always used, so the alpha arm's answers are bit-for-bit the recorded ones.
    /// </remarks>
    private static GeoClipColorSums? TryReadColorSums(ulong ridId)
    {
        if (!TryComposeRid(ridId, out var rid))
        {
            return null;
        }

        try
        {
            if (RenderingServer.MeshGetSurfaceCount(rid) <= 0)
            {
                return null;
            }

            var arrays = RenderingServer.MeshSurfaceGetArrays(rid, 0);
            if (arrays is null || arrays.Count == 0)
            {
                return null;
            }

            var slot = (int)RenderingServer.ArrayType.Color;
            var colorSlot = slot >= 0 && slot < arrays.Count ? arrays[slot] : default;
            if (colorSlot.VariantType != Variant.Type.PackedColorArray)
            {
                return GeoClipColorSums.Zero;
            }

            var colors = colorSlot.AsColorArray();
            var sums = GeoClipColorSums.Zero;
            for (var i = 0; i < colors.Length; i += 1)
            {
                var color = colors[i];
                sums = Sts2SpineGeoClipColorChecksum.AccumulateSums(sums, i, color.R, color.G, color.B, color.A);
            }

            return sums;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How many of <paramref name="meshRids"/> still resolve to a live mesh with at least one surface.
    ///
    /// <para>The CHEAP half of a surface read: <c>MeshGetSurfaceCount</c> only, with no array marshalling behind
    /// it, which is the same hop the sweep's own classifier bails on first. Zero when nothing has been associated
    /// yet, so a caller can invoke it unconditionally.</para>
    /// </summary>
    private static int CountLiveMeshes(IReadOnlyCollection<ulong>? meshRids)
    {
        if (meshRids is null || meshRids.Count == 0)
        {
            return 0;
        }

        var live = 0;
        foreach (var ridId in meshRids)
        {
            if (!TryComposeRid(ridId, out var rid))
            {
                continue;
            }

            if (TryGet(() => RenderingServer.MeshGetSurfaceCount(rid), 0) > 0)
            {
                live += 1;
            }
        }

        return live;
    }

    private static MeshRead? TryReadSurface(ulong ridId)
    {
        if (!TryComposeRid(ridId, out var rid))
        {
            return null;
        }

        try
        {
            var surfaceCount = RenderingServer.MeshGetSurfaceCount(rid);
            if (surfaceCount <= 0)
            {
                return null;
            }

            var arrays = RenderingServer.MeshSurfaceGetArrays(rid, 0);
            if (arrays is null || arrays.Count == 0)
            {
                return null;
            }

            Variant Slot(RenderingServer.ArrayType type)
            {
                var index = (int)type;
                return index >= 0 && index < arrays.Count ? arrays[index] : default;
            }

            var vertex = Slot(RenderingServer.ArrayType.Vertex);
            double[] xs = [];
            double[] ys = [];
            switch (vertex.VariantType)
            {
                case Variant.Type.PackedVector2Array:
                    var current = vertex.AsVector2Array();
                    xs = new double[current.Length];
                    ys = new double[current.Length];
                    for (var i = 0; i < current.Length; i += 1)
                    {
                        xs[i] = current[i].X;
                        ys[i] = current[i].Y;
                    }

                    break;
                case Variant.Type.PackedVector3Array:
                    var v3 = vertex.AsVector3Array();
                    xs = new double[v3.Length];
                    ys = new double[v3.Length];
                    for (var i = 0; i < v3.Length; i += 1)
                    {
                        xs[i] = v3[i].X;
                        ys[i] = v3[i].Y;
                    }

                    break;
            }

            var uv = Slot(RenderingServer.ArrayType.TexUV);
            double[] us = [];
            double[] vs = [];
            if (uv.VariantType == Variant.Type.PackedVector2Array)
            {
                var packed = uv.AsVector2Array();
                us = new double[packed.Length];
                vs = new double[packed.Length];
                for (var i = 0; i < packed.Length; i += 1)
                {
                    us[i] = packed[i].X;
                    vs[i] = packed[i].Y;
                }
            }

            var colorSlot = Slot(RenderingServer.ArrayType.Color);
            var colors = colorSlot.VariantType == Variant.Type.PackedColorArray
                ? colorSlot.AsColorArray().Select(color => new[] { color.R, color.G, color.B, color.A }).ToArray()
                : [];

            var indexSlot = Slot(RenderingServer.ArrayType.Index);
            var indices = indexSlot.VariantType == Variant.Type.PackedInt32Array
                ? indexSlot.AsInt32Array()
                : [];

            return new MeshRead
            {
                SurfaceCount = surfaceCount,
                VerticesAreVector2 = vertex.VariantType == Variant.Type.PackedVector2Array,
                HasUvChannel = uv.VariantType == Variant.Type.PackedVector2Array,
                X = xs,
                Y = ys,
                U = us,
                V = vs,
                Indices = indices,
                Colors = colors,
            };
        }
        catch
        {
            return null;
        }
    }

    // `Godot.Rid` wraps exactly one ulong and exposes no public constructor from a raw id, so the bits are
    // reinterpreted; the round trip is checked rather than assumed.
    private static bool TryComposeRid(ulong id, out Rid rid)
    {
        var value = id;
        rid = Unsafe.As<ulong, Rid>(ref value);
        if (rid.Id == id)
        {
            return true;
        }

        try
        {
            object boxed = default(Rid);
            var field = typeof(Rid)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.FieldType == typeof(ulong));
            if (field is null)
            {
                return false;
            }

            field.SetValue(boxed, id);
            rid = (Rid)boxed;
            return rid.Id == id;
        }
        catch
        {
            return false;
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Yield <paramref name="frames"/> engine frames, optionally closing each with a <c>ForceDraw</c>.
    /// </summary>
    /// <param name="phase">
    /// WHICH PART OF THE BAKE is waiting (a <c>Sts2RenderPhaseProfile.Phase.Bake*Wait</c> name). REQUIRED, with no
    /// default, because the whole reason to profile this bake is to find out where its dozens of awaited frames
    /// go — and a default would quietly file every un-named call site under one bucket, which is the answer we
    /// already had. Every call site therefore has to say what it is waiting FOR.
    /// </param>
    /// <remarks>
    /// The two halves are stamped DIFFERENTLY on purpose: the <c>ProcessFrame</c> await hands the main thread
    /// back to the game and is PARKED, while the <c>ForceDraw</c> that closes the frame holds it and is BLOCKING.
    /// That is the same doctrine as the render lanes' warmup helper, and getting it backwards would report the
    /// bake's politeness as a freeze.
    /// </remarks>
    /// <summary>How long the first frame awaited under a bake-taken pause is given before the pause is refused.
    /// One bake pays this once; the process-wide latch means no other bake ever pays it.</summary>
    private static readonly TimeSpan PauseProbeDeadline = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Await ONE engine frame, but give up after <paramref name="deadline"/> instead of waiting forever.
    /// </summary>
    /// <returns>
    /// True when the frame arrived (and was closed with the same <c>ForceDraw</c> the unbounded helper does, so
    /// this is a drop-in replacement for one of its iterations); false when the deadline won.
    /// </returns>
    /// <remarks>
    /// SAFE ONLY BECAUSE OF THE DISPATCHER. The `Task.Delay` leg completes on the timer wheel and its
    /// continuation is posted back to the game's captured SynchronizationContext, whose draining Timer is
    /// <c>ProcessMode.Always</c> (see Sts2GodotSynchronizationContext) — so the deadline can still fire on a
    /// paused tree, which is exactly the situation this exists to escape. Take that Timer off `Always` and this
    /// method becomes an unbounded wait wearing a deadline.
    ///
    /// <para>When the deadline wins, the signal leg is simply abandoned: a `SignalAwaiter` that never completes
    /// leaves one incomplete Task behind, which is a far smaller cost than the alternative of blocking to tidy
    /// it up on a frame that by definition is not coming.</para>
    /// </remarks>
    private static async Task<bool> AwaitFrameWithDeadlineAsync(
        Viewport rootViewport,
        TimeSpan deadline,
        string phase,
        bool forceDraw)
    {
        var tree = rootViewport.GetTree();
        if (tree is null)
        {
            // No tree to await, and therefore no tree that could have been paused. Not a deadlock.
            return true;
        }

        bool fired;
        using (Sts2RenderPhaseProfile.Measure(phase, blocking: false))
        {
            var frame = AwaitProcessFrameAsync(rootViewport, tree);
            fired = ReferenceEquals(await Task.WhenAny(frame, Task.Delay(deadline)), frame);
        }

        if (!fired)
        {
            return false;
        }

        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.FramesWaited);
        if (!forceDraw)
        {
            // Still a drop-in replacement for one iteration of the budgeted helper: it counts the same declined
            // draw, so a paused bake's schedule reads exactly like an unpaused one's.
            Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeDrawsElided);
            return true;
        }

        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeForceDraws);
        using var drawScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeForceDraw);
        TryRun(() => RenderingServer.ForceDraw());
        return true;
    }

    // A `SignalAwaiter` is awaitable but not a Task, so it cannot be handed to Task.WhenAny directly.
    private static async Task AwaitProcessFrameAsync(Viewport rootViewport, SceneTree tree)
        => await rootViewport.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

    /// <summary>
    /// A3: seek the rig to two different track times and check that the meshes the artifact is built from
    /// actually moved between them. Two awaited frames, no readback beyond the vertex positions.
    /// </summary>
    private static async Task<GeoClipPauseLiveness> CheckPausedRigMovesAsync(
        Viewport rootViewport,
        GeoClipLane? lane,
        double poseSeconds,
        double durationSeconds,
        IReadOnlyCollection<ulong>? associatedMeshes)
    {
        var times = GeoClipPauseLivenessCheck.ChooseChecksumTimes(poseSeconds, durationSeconds);
        if (lane is null || times is null || associatedMeshes is null || associatedMeshes.Count == 0)
        {
            // Nothing to ask the question WITH. Inconclusive, never a failure: an unanswerable check must not
            // convict the pause.
            return GeoClipPauseLiveness.Inconclusive;
        }

        // FIRST read covers every associated mesh, because it is also the SELECTION pass — the largest meshes
        // are the ones most likely to move measurably, and they are not known until something has been read.
        TryRun(() => lane.Entry.SetTrackTime((float)times.Value.First));
        await AwaitFramesAsync(rootViewport, 1, forceDraw: true, Sts2RenderPhaseProfile.Phase.BakePoseWait);
        var first = ReadPositionSums(associatedMeshes);
        var selected = GeoClipPauseLivenessCheck.LargestMeshes(
            first.ToDictionary(entry => entry.Key, entry => entry.Value.Vertices),
            GeoClipPauseLivenessCheck.ChecksumMeshCount);
        if (selected.Count == 0)
        {
            return GeoClipPauseLiveness.Inconclusive;
        }

        // SECOND read is only those few.
        TryRun(() => lane.Entry.SetTrackTime((float)times.Value.Second));
        await AwaitFramesAsync(rootViewport, 1, forceDraw: true, Sts2RenderPhaseProfile.Phase.BakePoseWait);
        var second = ReadPositionSums(selected);

        // A mesh that answered the first time and not the second makes the two lists different LENGTHS, which
        // FromChecksums reads as inconclusive — a broken read rather than a frozen rig.
        return GeoClipPauseLivenessCheck.FromChecksums(
            [.. selected.Where(second.ContainsKey).Select(rid => first[rid].Checksum)],
            [.. selected.Where(second.ContainsKey).Select(rid => second[rid].Checksum)]);
    }

    /// <summary>
    /// Vertex count and an index-weighted position sum per mesh that reads back. WEIGHTED by index so a rig that
    /// merely re-orders or mirrors its vertices does not produce the same number as one that did not move.
    /// </summary>
    private static Dictionary<ulong, (int Vertices, double Checksum)> ReadPositionSums(IEnumerable<ulong> meshRids)
    {
        var sums = new Dictionary<ulong, (int Vertices, double Checksum)>();
        using var scope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeGeometryRead);
        foreach (var ridId in meshRids)
        {
            var read = TryReadSurface(ridId);
            if (read is null || read.X.Length == 0)
            {
                continue;
            }

            var checksum = 0d;
            for (var i = 0; i < read.X.Length; i += 1)
            {
                checksum += (read.X[i] * (i + 1)) + (read.Y[i] * (i + 2));
            }

            sums[ridId] = (read.X.Length, checksum);
        }

        return sums;
    }

    /// <summary>
    /// Make every node under the bake's own detached subtree behave, on a PAUSED tree, exactly as it would have
    /// on a running one — see the call site in <c>Use</c> for the rule and why it is not "force everything to
    /// Always".
    /// </summary>
    /// <returns>How many nodes had to be rewritten. Zero on a rig whose nodes all inherit, which is the expected
    /// answer; a non-zero one is the reason this sweep exists.</returns>
    private static int ForceRigProcessingUnderPause(Node root)
    {
        var changed = 0;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            switch (TryGet(() => node.ProcessMode, Node.ProcessModeEnum.Inherit))
            {
                case Node.ProcessModeEnum.Pausable:
                    TryRun(() => node.ProcessMode = Node.ProcessModeEnum.Always);
                    changed += 1;
                    break;
                case Node.ProcessModeEnum.WhenPaused:
                    TryRun(() => node.ProcessMode = Node.ProcessModeEnum.Disabled);
                    changed += 1;
                    break;
            }

            var children = TryGet(() => node.GetChildCount(), 0);
            for (var i = 0; i < children; i += 1)
            {
                var child = TryGet(() => node.GetChild(i));
                if (child is not null)
                {
                    pending.Push(child);
                }
            }
        }

        return changed;
    }

    /// <summary>A Godot <c>Rect2</c> in the same 4-double shape <c>PoseFrame.Bounds</c> carries, so the two can
    /// be compared by the Godot-free liveness check.</summary>
    private static double[] BoundsArray(Rect2 rect)
        => [rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y];

    /// <summary>
    /// Yield <paramref name="frames"/> engine frames, letting the DRAW BUDGET decide which of them are closed
    /// with a <c>ForceDraw</c>.
    /// </summary>
    /// <remarks>
    /// The one entry point for every awaited frame the elision lever governs; the raw
    /// <see cref="AwaitFramesAsync"/> stays for the two callers that are not governed by it (the dense sweep's
    /// yields, which never drew, and the paused-tree liveness check, which is default-off and always draws).
    /// Frames are awaited ONE AT A TIME rather than in a batch because the budget's answer depends on the frame's
    /// position within the group — the last frame of a bracket settle draws and the ones before it do not.
    ///
    /// <para>A declined draw is COUNTED, not merely skipped: the profile beside it reports how many draws the
    /// bake issued, and without the elided count a bake whose lever armed is indistinguishable from one whose
    /// rig simply needed fewer frames.</para>
    /// </remarks>
    private static async Task AwaitBudgetedFramesAsync(
        Viewport rootViewport,
        int frames,
        GeoClipDrawBudget budget,
        GeoClipDrawSite site,
        string phase)
    {
        for (var frame = 0; frame < frames; frame += 1)
        {
            var forceDraw = budget.ForceDrawAt(site, frame, frames);
            if (!forceDraw)
            {
                Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeDrawsElided);
            }

            await AwaitFramesAsync(rootViewport, 1, forceDraw, phase);
        }
    }

    private static async Task AwaitFramesAsync(Viewport rootViewport, int frames, bool forceDraw, string phase)
    {
        var tree = rootViewport.GetTree();
        for (var frame = 0; frame < frames; frame += 1)
        {
            if (tree is not null)
            {
                using (Sts2RenderPhaseProfile.Measure(phase, blocking: false))
                {
                    await rootViewport.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }

                Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.FramesWaited);
            }

            if (forceDraw)
            {
                Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.BakeForceDraws);
                using var drawScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BakeForceDraw);
                TryRun(() => RenderingServer.ForceDraw());
            }
        }
    }

    // Call a method only when the object actually exposes it, so a missing binding costs one lookup instead of
    // an engine error line per slot per frame.
    private static bool TryReadAttachment(GodotObject slot, out GodotObject? attachment)
    {
        attachment = null;
        try
        {
            if (!slot.HasMethod("get_attachment"))
            {
                return false;
            }

            attachment = slot.Call("get_attachment").AsGodotObject();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Variant? Invoke(GodotObject target, string method, params Variant[] args)
    {
        try
        {
            return target.HasMethod(method) ? target.Call(method, args) : (Variant?)null;
        }
        catch
        {
            return null;
        }
    }

    private static T OnMainThread<T>(Func<T> action, T fallback)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(action, TimeSpan.FromSeconds(20));
        }
        catch
        {
            return fallback;
        }
    }

    private static T? TryGet<T>(Func<T?> action)
    {
        try
        {
            return action();
        }
        catch
        {
            return default;
        }
    }

    private static T TryGet<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch
        {
            return fallback;
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A bake must never take the host down; the missing datum is a logged reason instead.
        }
    }

    private static void Log(ILogStream? logStream, string message)
    {
        if (LogToGodot)
        {
            try
            {
                GD.Print($"{LogPrefix} {message}");
            }
            catch
            {
                // Printing is best-effort.
            }
        }

        try
        {
            logStream?.Write(BridgeLogLevel.Info, LogTarget, message);
        }
        catch
        {
            // Ditto.
        }
    }
}
