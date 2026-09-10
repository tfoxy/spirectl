using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;


/// <summary>
/// ENV-GATED DIAGNOSTIC PROBE (prototype, no product behavior). Answers one question empirically: can a host
/// read a Spine skeleton's per-slot triangle geometry out of the running engine, cheaply enough and reliably
/// enough to stream it, instead of baking pre-rendered clips?
///
/// <para>The shape of the answer it goes looking for: the spine GDExtension draws each slot through a
/// <c>SpineMesh2D</c> child node that owns a RenderingServer mesh RID, and
/// <see cref="RenderingServer.MeshSurfaceGetArrays"/> is public API that reads such a mesh back
/// (vertices / UVs / colors / indices). The RID itself is not exposed on the node, so it has to be recovered
/// from RID arithmetic — a Godot RID id packs a process-globally monotonic validator over a per-owner slot
/// index — and which arithmetic is available depends on whether the probe was watching when the skeleton was
/// built:
/// <list type="bullet">
///   <item>RECIPE A (offline lane) — bracketing. Meshes the probe creates itself before and after the
///   skeleton builds bracket every RID it made in between; the bracketed range is enumerated and each
///   candidate validated by asking the server for it.</item>
///   <item>RECIPE D (live lane) — canvas-item bracketing. On a live host the skeleton was built before the
///   probe existed, so there is no lower bracket. But each mesh node's canvas-item RID IS public, and those
///   were minted from the same counter while the same skeleton was building, so they pin the window's lower
///   edge; one mesh created now pins the upper edge and bounds the index axis. See
///   <c>Sts2SpineProbeMath.PlanCanvasItemBracket</c>.</item>
/// </list></para>
///
/// <para>RECIPE B, RETIRED. A third route was tried and is dead: reading the GDExtension instance pointer at
/// a fixed offset behind the node's native object and scanning its leading 64-bit words for a value that
/// validates as a one-surface mesh RID. The offset itself is right, but the RID is not an inline word
/// anywhere in the first pages of the wrapper: the scan found ZERO hits on every node of both offline rigs at
/// 64 words, and widening it to 512 words only produced FALSE POSITIVES out of neighbouring heap allocations
/// (one node yielding two "RIDs", which silently corrupted a run). Recipe D replaces it; nothing here reads
/// native memory any more.</para>
///
/// <para>Everything else in the battery measures whether that readback is USABLE: does it track animation
/// updates with and without a forced draw pass, what coordinate space are the vertices in, which mesh belongs
/// to which slot, how long a whole-skeleton readback takes, how much of a slot's motion a single affine
/// transform explains (rigid vs deforming), and what the atlas says about the regions those UVs address.</para>
///
/// <para>SAFETY. With <c>SPIRECTL_SPINE_GEOMETRY_PROBE</c> unset, <see cref="Install"/> returns before doing
/// anything at all — no hook, no thread, no allocation — so an unarmed host is provably unchanged. When armed
/// the battery runs ONCE, well after the game has a scene, writes JSON, and stops; every native hop is
/// individually wrapped so a failure logs and the battery continues. Live mode is strictly read-only: it never
/// writes a slot color, never touches animation state, and never seeks time. Progress goes to
/// <c>GD.Print</c> (the game nulls stdio, so that is the reliable sink) prefixed <c>SPINE_GEOM_PROBE</c>.</para>
/// </summary>
internal static class Sts2SpineGeometryProbe
{
    internal const string ProbeVersion = "spine-geometry-probe/1";

    private const string LogPrefix = "SPINE_GEOM_PROBE";
    private const string LogTarget = "bridge.probe.spine-geometry";

    private const string ModeEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE";
    private const string OutEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_OUT";
    private const string ScenesEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_SCENES";
    private const string AnimEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_ANIM";
    private const string DelayEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_DELAY_SEC";
    private const string CandidateCapEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_CANDIDATE_CAP";
    private const string IndexSlackEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_INDEX_SLACK";
    private const string SamplesEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_SAMPLES";
    private const string TimingRepsEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_TIMING_REPS";
    private const string LiveWaitEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_LIVE_WAIT_SEC";

    // Live acquisition knobs (recipe D, anchor → stride → walk). All four default to "let the lane decide",
    // so an operator who sets none of them gets the measured recipe; each exists because a live rig can
    // surprise the arithmetic and the next run should not need a rebuild to answer back.
    private const string FullSweepEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_FULL_SWEEP";
    private const string AnchorStepEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_ANCHOR_STEP";
    private const string StrideEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_STRIDE";
    private const string WalkMissesEnv = "SPIRECTL_SPINE_GEOMETRY_PROBE_WALK_MISSES";

    // The two default offline targets: a merchant (a large, mostly-rigid humanoid rig authored as its own
    // scene) and a combat creature (a small rig with a runtime-scaled node transform, which is what makes the
    // coordinate-space question interesting). Both are addressed the same way the clip renderer addresses a
    // bake target — by scene res-path, resolving to the first SpineSprite in the scene.
    private static readonly string[] DefaultScenes =
    [
        "res://scenes/merchant/characters/ironclad_merchant.tscn",
        "res://scenes/creature_visuals/byrdonis.tscn",
    ];

    // Preference order for the animation the offline battery pins. `idle_loop` is the near-universal resting
    // clip; a rig that lacks it usually names its resting clip in the node's own `preview_animation`; anything
    // still unresolved falls back to the first clip the skeleton exposes, so every rig yields a battery.
    private const string PreferredAnimation = "idle_loop";

    // How many candidates a sweep validates before yielding a frame back to the game. A RenderingServer getter
    // synchronizes with the render thread, so an unbroken 50k-call sweep would visibly freeze the host; the
    // report records that the sweep was chunked so nobody reads it as one atomic instant.
    private const int CandidateChunkSize = 8192;

    // How many meshes the live lane creates to close its candidate window. One would do for the validator
    // edge, but the index edge is the mesh owner's high-water mark and a single create can land on a REUSED
    // low slot from the free list; a short burst makes the observed maximum index a sturdier ceiling.
    private const int LiveProbeMeshCount = 4;

    // Ceiling on the cap the live lane may raise itself to when the operator did not choose one. A sweep is
    // chunked and yields frames, so a large one does not freeze the host — it just takes minutes, which is a
    // worse outcome than a report that says the window was too wide to walk.
    private const int LiveAutoCandidateCapCeiling = 1_000_000;

    // Floor under the live index slack when the operator did not choose one. The probe mesh's own per-owner
    // index is the index axis's ceiling, and that index is NOT a high-water mark: Godot hands a freed slot
    // back to the next allocation, so a mesh created now can land well below meshes that are still alive.
    // Widening the axis costs one candidate per validator row swept; missing the target costs the run.
    private const int LiveIndexSlackFloor = 256;

    // Ceiling on the coarse anchor scan when the operator did not choose a cap. On the Phase-1 window
    // (14 209 validator rows x 347 indices) the scan plans ~205k candidates at step 24, so this leaves room
    // for a window twice that size before anything is truncated — and truncation is reported, not silent.
    private const int LiveAnchorScanCandidateCeiling = 500_000;

    // How far the stride ladder reaches when measuring the progression off the first anchor. 24 is the anchor
    // step for a 26-child rig, i.e. exactly the distance within which a member must exist if the run is dense
    // enough to have been anchored at all.
    private const int LiveStrideLadderMaxDelta = 24;

    // The dense fallback's half-width, in RUN WIDTHS, around each anchor that failed to walk. Three is enough
    // to contain the whole neighbouring-rig cluster the Phase-1 run sat in without approaching the cost of
    // the full window.
    private const int LiveDensePassRunWidths = 3;

    // How many failed anchors the dense fallback is willing to re-examine. Each one costs a window.
    private const int LiveDensePassAnchorCap = 4;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static readonly Lock Sync = new();
    private static bool _installed;

    /// <summary>
    /// Arm the probe if (and only if) <c>SPIRECTL_SPINE_GEOMETRY_PROBE</c> names a mode. Returns immediately
    /// otherwise, which is the unarmed host's entire cost.
    /// </summary>
    public static void Install(ILogStream logStream)
    {
        var mode = (System.Environment.GetEnvironmentVariable(ModeEnv) ?? string.Empty).Trim();
        if (mode.Length == 0)
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
        // own assembly name — so one env var arms two probes, which sweep and mint meshes in the same RID space and
        // write the same report directory. The claim is the outer, process-global guard; it sits BELOW the
        // empty-mode return so an unarmed host still pays exactly one environment read. See Sts2OneShotArmClaim.
        var owner = Sts2OneShotArmClaim.SelfOwner;
        if (!Sts2OneShotArmClaim.TryClaim(Sts2OneShotArmClaim.SpineGeometryProbeSubsystem, owner, out var existingOwner))
        {
            Log(logStream, $"already armed by {existingOwner}; standing down.");
            return;
        }

        try
        {
            var config = ReadConfig(mode, owner);
            if (config is null)
            {
                Log(logStream, $"armed with mode='{mode}' but {OutEnv} is unset; doing nothing.");
                return;
            }

            Log(
                logStream,
                $"armed owner={config.Owner} mode='{mode}' offline={config.Offline} live={config.Live} out='{config.OutDir}' "
                + $"scenes={config.Scenes.Count} anim='{config.Animation ?? "<auto>"}' delaySec={config.StartDelaySeconds}");

            // Detached: the battery waits for the game to settle and then parks on the main thread in bounded
            // hops. Install runs during bridge construction, long before there is a scene to probe.
            _ = Task.Run(() => RunAsync(config, logStream));
        }
        catch (Exception ex)
        {
            Log(logStream, $"failed to arm: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Configuration ────────────────────────────────────────────────────────────────────────────────

    private sealed record ProbeConfig(
        // Which copy of this runtime won the one-shot claim (see Sts2OneShotArmClaim). Echoed into every report so
        // an artifact from a two-mod loadout says which assembly identity produced it.
        string Owner,
        string RawMode,
        bool Offline,
        bool Live,
        string OutDir,
        IReadOnlyList<string> Scenes,
        string? Animation,
        int StartDelaySeconds,
        int CandidateCap,
        bool CandidateCapExplicit,
        int IndexSlack,
        bool IndexSlackExplicit,
        int SampleCount,
        int TimingReps,
        int LiveWaitSeconds,
        bool FullSweep,
        int AnchorStep,
        string? Stride,
        int WalkMisses);

    private static ProbeConfig? ReadConfig(string mode, string owner)
    {
        var outDir = (System.Environment.GetEnvironmentVariable(OutEnv) ?? string.Empty).Trim();
        if (outDir.Length == 0)
        {
            return null;
        }

        var tokens = mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var offline = tokens.Any(token => token.Equals("offline", StringComparison.OrdinalIgnoreCase));
        var live = tokens.Any(token => token.Equals("live", StringComparison.OrdinalIgnoreCase));
        if (!offline && !live)
        {
            // An unrecognized value still arms offline: a typo should produce a report saying so, not silence.
            offline = true;
        }

        var scenesRaw = (System.Environment.GetEnvironmentVariable(ScenesEnv) ?? string.Empty).Trim();
        var scenes = scenesRaw.Length == 0
            ? DefaultScenes
            : scenesRaw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var animation = (System.Environment.GetEnvironmentVariable(AnimEnv) ?? string.Empty).Trim();

        return new ProbeConfig(
            owner,
            mode,
            offline,
            live,
            outDir,
            scenes,
            animation.Length == 0 ? null : animation,
            ReadIntEnv(DelayEnv, 10, 0, 600),
            ReadIntEnv(CandidateCapEnv, 50_000, 0, 5_000_000),
            HasIntEnv(CandidateCapEnv),
            ReadIntEnv(IndexSlackEnv, 64, 0, 65_536),
            HasIntEnv(IndexSlackEnv),
            ReadIntEnv(SamplesEnv, 8, 2, 64),
            ReadIntEnv(TimingRepsEnv, 20, 1, 500),
            ReadIntEnv(LiveWaitEnv, 300, 5, 3600),
            ReadIntEnv(FullSweepEnv, 0, 0, 1) == 1,
            ReadIntEnv(AnchorStepEnv, 0, 0, 1_000_000),
            ReadStringEnv(StrideEnv),
            ReadIntEnv(WalkMissesEnv, 2, 1, 64));
    }

    private static string? ReadStringEnv(string name)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return raw.Length == 0 ? null : raw;
    }

    private static int ReadIntEnv(string name, int fallback, int min, int max)
    {
        var raw = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    // Whether the operator actually chose a value, as opposed to inheriting the default. The live lane needs
    // that distinction: its candidate space is not knowable until runtime, so an untouched default is a guess
    // it may improve on, while an explicit value is an instruction it must obey.
    private static bool HasIntEnv(string name)
        => int.TryParse(
            (System.Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out _);

    private static Dictionary<string, object?> EchoConfig(ProbeConfig config)
        => new(StringComparer.Ordinal)
        {
            // Not an env var, unlike every other key here: the assembly identity that won the process-global
            // one-shot claim. A loadout can hold two copies of this runtime and one env var reaches both, so a
            // report has to name the copy that actually ran (see Sts2OneShotArmClaim).
            ["owner"] = config.Owner,
            [ModeEnv] = config.RawMode,
            [OutEnv] = config.OutDir,
            [ScenesEnv] = config.Scenes.ToArray(),
            [AnimEnv] = config.Animation,
            [DelayEnv] = config.StartDelaySeconds,
            [CandidateCapEnv] = config.CandidateCap,
            [IndexSlackEnv] = config.IndexSlack,
            [SamplesEnv] = config.SampleCount,
            [TimingRepsEnv] = config.TimingReps,
            [LiveWaitEnv] = config.LiveWaitSeconds,
            [FullSweepEnv] = config.FullSweep,
            [AnchorStepEnv] = config.AnchorStep,
            [StrideEnv] = config.Stride,
            [WalkMissesEnv] = config.WalkMisses,
        };

    // ── Orchestration ────────────────────────────────────────────────────────────────────────────────

    private static async Task RunAsync(ProbeConfig config, ILogStream logStream)
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
            Log(logStream, "gave up waiting for a stable scene; nothing ran.");
            return;
        }

        if (config.Offline)
        {
            foreach (var scenePath in config.Scenes)
            {
                try
                {
                    await RunOfflineSceneAsync(config, scenePath, logStream).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log(logStream, $"offline battery for '{scenePath}' aborted: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (config.Live)
        {
            try
            {
                await RunLiveBatteryAsync(config, logStream).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(logStream, $"live battery aborted: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log(logStream, "battery complete; probe disarmed.");
    }

    // Readiness follows the same idiom the extraction path uses to decide a host is usable: a live main-thread
    // dispatcher plus a SceneTree that has actually mounted a scene. Then an extra settle delay, because the
    // first mounted scene is a splash/boot screen still streaming resources.
    private static async Task<bool> WaitForStableSceneAsync(ProbeConfig config, ILogStream logStream)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.LiveWaitSeconds);
        var announced = false;
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

            if (!announced)
            {
                announced = true;
                Log(logStream, $"scene is up; settling for {config.StartDelaySeconds}s before running.");
            }

            await Task.Delay(TimeSpan.FromSeconds(config.StartDelaySeconds)).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // ── Offline battery ──────────────────────────────────────────────────────────────────────────────

    private static async Task RunOfflineSceneAsync(ProbeConfig config, string scenePath, ILogStream logStream)
    {
        var sceneName = SanitizeName(Path.GetFileNameWithoutExtension(scenePath));
        Log(logStream, $"offline battery start scene='{scenePath}'");

        var report = await Sts2MainThreadDispatcher
            .InvokeAsync(() => RunOfflineSceneOnMainThreadAsync(config, scenePath, sceneName, logStream))
            .ConfigureAwait(false);

        WriteJson(config.OutDir, $"offline-{sceneName}.json", report, IndentedJson, logStream);
        Log(logStream, $"offline battery done scene='{scenePath}' -> offline-{sceneName}.json");
    }

    private static async Task<Dictionary<string, object?>> RunOfflineSceneOnMainThreadAsync(
        ProbeConfig config,
        string scenePath,
        string sceneName,
        ILogStream logStream)
    {
        var report = NewReport("offline", config);
        report["scenePath"] = scenePath;
        var notes = new List<string>();
        report["notes"] = notes;

        // Whether the single-ulong Rid layout assumption holds; every RID step below is meaningless without it.
        report["ridConstruction"] = DescribeRidConstruction();

        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            notes.Add("Engine.GetMainLoop() exposed no root viewport; nothing could be mounted.");
            return report;
        }

        if (!Sts2AssetExtractProvider.TryLoadSceneAndFindNode(scenePath, null, out var sceneRoot, out var spineNode, out var failNote))
        {
            notes.Add($"Scene load failed: {failNote}");
            return report;
        }

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
        try
        {
            rootViewport.AddChild(viewport);

            // RECIPE A, lower bracket: taken while the spine node is still parentless, so nothing of the
            // skeleton exists yet and every mesh it later creates carries a strictly larger validator.
            var bracketLow = RenderingServer.MeshCreate();
            probeRids.Add(bracketLow);

            var renderNode = Sts2AssetExtractProvider.DetachSpineForRender(sceneRoot, spineNode);
            viewport.AddChild(renderNode);

            // The skeleton (and therefore its per-slot mesh children) is built by the node's first process
            // frames, so the upper bracket must be taken after them.
            // MAIN-THREAD AFFINITY. Every await from here down is a bare await ON PURPOSE. This method body is
            // posted onto the game's captured SynchronizationContext, and the engine refuses tree mutation (and
            // is unsafe for much else) off that thread. `ConfigureAwait(false)` would opt the continuation out
            // of that context, and because the main thread carries a non-default SynchronizationContext the
            // runtime declines to inline such a continuation — so the rest of the battery would silently
            // resume on a thread-pool thread.
            await AwaitFramesAsync(rootViewport, 3, forceDraw: true);
            var bracketHigh = RenderingServer.MeshCreate();
            probeRids.Add(bracketHigh);

            report["bracket"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["lowId"] = bracketLow.Id,
                ["highId"] = bracketHigh.Id,
                ["low"] = DescribeRidParts(bracketLow.Id),
                ["high"] = DescribeRidParts(bracketHigh.Id),
            };

            // Pin the animation deterministically, exactly like a clip lane: one non-looping track, timescale
            // zero, explicit per-sample track time. That makes every later comparison reproducible.
            var lane = TryPrepareLane(spineNode, config.Animation, notes);
            report["lane"] = lane.Describe();
            if (lane.Entry is null || lane.Sprite is null)
            {
                return report;
            }

            await AwaitFramesAsync(rootViewport, 2, forceDraw: true);

            var skeletonObject = TryGet(() => lane.Sprite.GetSkeleton()?.BoundObject);
            var meshNodes = CollectMeshNodes(spineNode);
            report["structure"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spineMesh2dChildren"] = meshNodes.Count,
                ["spineSpriteChildren"] = TryGet(() => spineNode.GetChildCount()),
                ["slotCount"] = CountSlots(skeletonObject),
                ["skeletonObjectClass"] = TryGet(() => skeletonObject?.GetClass()),
            };

            // The mesh children's canvas-item RIDs. Public API, and therefore the ONE thing about a skeleton's
            // RID neighbourhood that a live host can also read — which is what recipe D is built out of. They
            // are collected here so the offline report can dry-run that recipe against a ground truth (recipe
            // A's answer) before a game session is spent on the live lane.
            var canvasItems = CollectCanvasItemRids(meshNodes);
            report["canvasItems"] = DescribeCanvasItems(canvasItems);

            // RECIPE A: enumerate the bracketed space and validate.
            var sweep = await RunBracketSweepAsync(rootViewport, bracketLow.Id, bracketHigh.Id, config, probeRids);
            report["recipeA"] = sweep.Report;

            // Recipe A only proves a RID exists somewhere in the bracketed id space: the set comes back in id
            // order rather than node order, and it can include meshes the rest of the game happened to create
            // inside the same window. Every step below has to be read with that in mind, which is why the
            // report names its source explicitly.
            var meshRids = sweep.Validated;
            report["meshRidSource"] = "recipeA-bracket-sweep";
            report["meshRidCount"] = meshRids.Count;

            // The live recipe's dry run: would the window recipe D builds out of those canvas items (plus a
            // mesh created now, standing in for the live probe mesh) have contained everything recipe A found?
            report["recipeDDryRun"] = DryRunCanvasBracket(canvasItems, meshRids, config, probeRids);

            if (meshRids.Count == 0)
            {
                notes.Add("No SpineMesh2D mesh RID was recovered, so every readback step below was skipped.");
                return report;
            }

            // Step 4: what does a readback actually hand back?
            report["readback"] = DescribeReadbackDump(meshRids);

            // Step 5: does the readback track a re-seek — with and without a forced draw?
            var noDrawArm = await RunFrameSteppingArmAsync(
                rootViewport, "no-force-draw", forceDraw: false, lane, meshRids, skeletonObject, config, captureFull: false);
            var drawArm = await RunFrameSteppingArmAsync(
                rootViewport, "with-force-draw", forceDraw: true, lane, meshRids, skeletonObject, config, captureFull: true);
            report["frameStepping"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["arms"] = new[] { noDrawArm.Summary, drawArm.Summary },
                ["note"] =
                    "The host's own main loop keeps rendering during the no-force-draw arm, so a positive result "
                    + "there means 'no EXTRA draw was needed', not 'no draw happened at all'.",
            };

            // Full geometry is kept for exactly one scene (the first that gets this far) so a later wireframe
            // overlay has something to draw, without every report carrying tens of MB.
            if (drawArm.FullSamples is { Count: > 0 } && TryClaimFullVertexSlot())
            {
                var verticesFile = $"offline-{sceneName}-vertices.json";
                WriteJson(
                    config.OutDir,
                    verticesFile,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["schema"] = ProbeVersion,
                        ["scenePath"] = scenePath,
                        ["animation"] = lane.AnimationName,
                        ["samples"] = drawArm.FullSamples,
                    },
                    CompactJson,
                    logStream);
                report["fullVertexFile"] = verticesFile;
            }

            // Step 6: which mesh is which slot? And how the per-slot columns beside it were resolved at all —
            // an empty name column reads as "this slot has no name" unless the report says otherwise.
            report["slotDiagnostics"] = ReadSlotSnapshot(skeletonObject).Diagnostics;
            report["slotMeshAssociation"] = await ProbeSlotMeshAssociationAsync(
                rootViewport, skeletonObject, meshRids, meshNodes);

            // Step 7: the atlas the UVs address.
            report["atlas"] = DumpAtlas(config, sceneName, spineNode, logStream);

            // Step 8: rigid or deforming, per slot.
            report["rigidity"] = SummarizeRigidity(drawArm.PerMeshVertices);

            // Step 9: what a whole-skeleton readback costs. Recorded next to the proof that the measurement is
            // on the thread it claims to be: a readback timed from a pool thread would be pricing a
            // cross-thread hop into the renderer, not the cost a host would actually pay.
            report["onMainThreadAtTiming"] = Sts2MainThreadDispatcher.DescribeStatus().IsOnCapturedThread;
            report["timing"] = MeasureReadbackTiming(meshRids, config.TimingReps);

            // Step 10: which space are those vertices in, and do they read back as this skeleton at all?
            report["coordinateSpace"] = DescribeGeometrySanity(lane.Sprite, spineNode, meshRids);

            return report;
        }
        catch (Exception ex)
        {
            notes.Add($"Offline battery threw: {ex.GetType().Name}: {ex.Message}");
            return report;
        }
        finally
        {
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

    // ── Lane preparation ─────────────────────────────────────────────────────────────────────────────

    private sealed class ProbeLane
    {
        public MegaSprite? Sprite { get; init; }

        public MegaTrackEntry? Entry { get; init; }

        public string? AnimationName { get; init; }

        public string? AnimationSource { get; init; }

        public float Duration { get; init; }

        public IReadOnlyList<string> Available { get; init; } = [];

        public string? Failure { get; init; }

        public Dictionary<string, object?> Describe()
            => new(StringComparer.Ordinal)
            {
                ["animation"] = AnimationName,
                ["animationSource"] = AnimationSource,
                ["durationSeconds"] = Duration,
                ["availableAnimations"] = Available,
                ["failure"] = Failure,
            };
    }

    private static ProbeLane TryPrepareLane(Node spineNode, string? requested, List<string> notes)
    {
        try
        {
            var sprite = new MegaSprite(spineNode);
            var skeleton = sprite.GetSkeleton();
            if (skeleton is null)
            {
                return new ProbeLane { Failure = "The SpineSprite exposed no skeleton." };
            }

            var data = skeleton.GetData();
            var available = data.GetAnimationNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            var (animation, source) = ChooseAnimation(spineNode, data, available, requested);
            if (animation is null)
            {
                return new ProbeLane
                {
                    Available = available,
                    Failure = "The skeleton exposed no playable animation.",
                };
            }

            var state = sprite.GetAnimationState();
            var entry = state.SetAnimation(animation, loop: false);
            if (entry is null)
            {
                return new ProbeLane
                {
                    Available = available,
                    AnimationName = animation,
                    AnimationSource = source,
                    Failure = "Setting the animation returned no track entry.",
                };
            }

            state.SetTimeScale(0f);
            entry.SetTrackTime(0f);
            return new ProbeLane
            {
                Sprite = sprite,
                Entry = entry,
                AnimationName = animation,
                AnimationSource = source,
                Duration = Math.Max(0f, entry.GetAnimationDuration()),
                Available = available,
            };
        }
        catch (Exception ex)
        {
            notes.Add($"Lane preparation threw: {ex.GetType().Name}: {ex.Message}");
            return new ProbeLane { Failure = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static (string? Animation, string Source) ChooseAnimation(
        Node spineNode,
        MegaSkeletonDataResource data,
        IReadOnlyList<string> available,
        string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && TryGet(() => data.HasAnimation(requested)))
        {
            return (requested, "env");
        }

        if (TryGet(() => data.HasAnimation(PreferredAnimation)))
        {
            return (PreferredAnimation, "preferred");
        }

        var preview = TryGet(() => spineNode.Get("preview_animation").AsString());
        if (!string.IsNullOrWhiteSpace(preview) && TryGet(() => data.HasAnimation(preview)))
        {
            return (preview, "preview_animation");
        }

        return available.Count > 0 ? (available[0], "first-available") : (null, "none");
    }

    // ── RID acquisition, recipe A (bracketing) ───────────────────────────────────────────────────────

    /// <summary>The sweep's report, plus the RIDs it validated so a caller can fall back to them.</summary>
    private sealed record BracketSweep(Dictionary<string, object?> Report, List<ulong> Validated);

    private static async Task<BracketSweep> RunBracketSweepAsync(
        Viewport rootViewport,
        ulong lowId,
        ulong highId,
        ProbeConfig config,
        List<Rid> probeRids)
    {
        var plan = Sts2SpineGeometryMath.PlanRidCandidates(lowId, highId, config.IndexSlack, config.CandidateCap);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["validatorLow"] = plan.ValidatorLow,
            ["validatorHigh"] = plan.ValidatorHigh,
            ["indexLow"] = plan.IndexLow,
            ["indexHigh"] = plan.IndexHigh,
            ["totalCandidates"] = plan.TotalCandidates,
            ["cap"] = plan.Cap,
            ["truncated"] = plan.Truncated,
            ["enumerated"] = plan.EmittedCount,
        };

        if (plan.EmittedCount == 0)
        {
            result["swept"] = false;
            result["note"] = "The candidate cap was zero, so no candidate was validated.";
            return new BracketSweep(result, []);
        }

        var outcome = await SweepCandidatesAsync(
            rootViewport,
            Sts2SpineGeometryMath.EnumerateRidCandidates(plan),
            [.. probeRids.Select(rid => rid.Id)]);

        result["swept"] = true;
        result["probed"] = outcome.Probed;
        result["sweptChunked"] = outcome.Chunked;
        result["sweepMs"] = Math.Round(outcome.ElapsedMs, 3);
        result["validatedCount"] = outcome.Validated.Count;
        result["validatedSample"] = outcome.Validated
            .Take(64)
            .Select(id => id.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        return new BracketSweep(result, outcome.Validated);
    }

    /// <summary>What a candidate sweep validated, and what it cost — shared by recipes A and D.</summary>
    private sealed record SweepOutcome(List<ulong> Validated, long Probed, bool Chunked, double ElapsedMs);

    // The candidate SOURCE is an enumerable rather than a plan, and the acceptance test is injectable, so the
    // one chunked, error-suppressed, frame-yielding sweep loop serves recipe A's bracket enumeration, recipe
    // D's full window, recipe D's coarse anchor scan and its dense fallback alike. `classifier` null means the
    // Phase-1 test verbatim (one surface, positive vertex count, triangles, matched UV channel) — recipe A
    // passes null and therefore still walks byte-identical ground.
    private static async Task<SweepOutcome> SweepCandidatesAsync(
        Viewport? rootViewport,
        IEnumerable<ulong> candidates,
        HashSet<ulong> excludeIds,
        Func<ulong, bool>? classifier = null)
    {
        var validated = new List<ulong>();
        long probed = 0;
        var chunked = false;
        var stopwatch = Stopwatch.StartNew();
        var accept = classifier ?? DefaultMeshClassifier;

        // An invalid RID makes the server log an error; tens of thousands of those would bury godot.log and
        // cost more than the lookups. Suppression is restored in the finally below.
        var previousPrintErrors = TryGet(() => Engine.PrintErrorMessages, true);
        try
        {
            TryRun(() => Engine.PrintErrorMessages = false);
            foreach (var candidate in candidates)
            {
                probed += 1;
                if (!excludeIds.Contains(candidate) && accept(candidate))
                {
                    validated.Add(candidate);
                }

                // Bare await: this runs inside a main-thread-posted body, and ConfigureAwait(false) would move
                // the rest of the sweep (and every RenderingServer call in it) onto the pool.
                if (probed % CandidateChunkSize == 0 && rootViewport is not null)
                {
                    chunked = true;
                    TryRun(() => Engine.PrintErrorMessages = previousPrintErrors);
                    await AwaitFramesAsync(rootViewport, 1, forceDraw: false);
                    TryRun(() => Engine.PrintErrorMessages = false);
                }
            }
        }
        finally
        {
            TryRun(() => Engine.PrintErrorMessages = previousPrintErrors);
            stopwatch.Stop();
        }

        return new SweepOutcome(validated, probed, chunked, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static bool DefaultMeshClassifier(ulong candidate)
        => TryValidateMeshRid(candidate, out _, out var vertexCount) && vertexCount > 0;

    // ── RID acquisition, recipe D (canvas-item bracketing) ───────────────────────────────────────────

    /// <summary>
    /// A mesh child's canvas-item RID, which unlike its mesh RID is public API. On a live host this is the
    /// only handle onto the id neighbourhood the skeleton's meshes were minted in.
    /// </summary>
    private sealed record CanvasItemRid(int ChildIndex, string? Name, ulong Id, bool Read);

    private static List<CanvasItemRid> CollectCanvasItemRids(IReadOnlyList<Node> meshNodes)
    {
        var found = new List<CanvasItemRid>(meshNodes.Count);
        for (var childIndex = 0; childIndex < meshNodes.Count; childIndex += 1)
        {
            var node = meshNodes[childIndex];
            var name = TryGet(() => node.Name.ToString());
            var id = TryGet(() => node is CanvasItem item ? item.GetCanvasItem().Id : 0uL, 0uL);
            found.Add(new CanvasItemRid(childIndex, name, id, id != 0));
        }

        return found;
    }

    private static Dictionary<string, object?> DescribeCanvasItems(IReadOnlyList<CanvasItemRid> canvasItems)
    {
        var readable = canvasItems.Where(item => item.Read).ToArray();
        var validators = readable
            .Select(item => Sts2SpineGeometryMath.DecodeRid(item.Id).Validator)
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nodeCount"] = canvasItems.Count,
            ["readableCount"] = readable.Length,
            ["validatorMin"] = validators.Length > 0 ? validators.Min() : (uint?)null,
            ["validatorMax"] = validators.Length > 0 ? validators.Max() : (uint?)null,
            ["perNode"] = canvasItems.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["childIndex"] = item.ChildIndex,
                ["name"] = item.Name,
                ["canvasItemRid"] = item.Id.ToString(CultureInfo.InvariantCulture),
                ["canvasItemRidParts"] = DescribeRidParts(item.Id),
                ["failure"] = item.Read ? null : "the node exposed no canvas item",
            }).ToArray(),
        };
    }

    // Offline only: build the window recipe D WOULD have used (canvas items below, a mesh created now above)
    // and measure it against recipe A's answer. A live run costs a game session, so this is where the recipe
    // gets to be wrong cheaply — `wouldContainAll` false here means the live lane cannot work as designed, and
    // `measuredValidatorOffset` is the number the window's shape is tuned from.
    private static Dictionary<string, object?> DryRunCanvasBracket(
        IReadOnlyList<CanvasItemRid> canvasItems,
        IReadOnlyList<ulong> knownMeshRids,
        ProbeConfig config,
        List<Rid> probeRids)
    {
        var canvasIds = canvasItems.Where(item => item.Read).Select(item => item.Id).ToArray();
        var standIn = TryGet(() => RenderingServer.MeshCreate(), default);
        if (standIn.Id != 0)
        {
            probeRids.Add(standIn);
        }

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(
            canvasIds,
            standIn.Id == 0 ? [] : [standIn.Id],
            config.IndexSlack,
            config.CandidateCap);

        var (contained, total, offset) = Sts2SpineProbeMath.CheckCanvasBracketCoverage(plan, knownMeshRids);
        var result = DescribeCanvasBracketPlan(plan);
        result["knownMeshRidCount"] = total;
        result["knownMeshRidsInWindow"] = contained;
        result["wouldContainAll"] = total > 0 && contained == total;
        result["measuredValidatorOffset"] = offset;
        result["capCoversMeasuredOffset"] = plan.Viable && plan.ValidatorsCovered >= offset;
        return result;
    }

    private static Dictionary<string, object?> DescribeCanvasBracketPlan(
        Sts2SpineProbeMath.CanvasBracketPlan plan)
        => new(StringComparer.Ordinal)
        {
            ["viable"] = plan.Viable,
            ["reason"] = plan.Reason,
            ["canvasValidatorLow"] = plan.CanvasValidatorLow,
            ["canvasValidatorHigh"] = plan.CanvasValidatorHigh,
            ["probeValidator"] = plan.ProbeValidator,
            ["probeIndex"] = plan.ProbeIndex,
            ["validatorLow"] = plan.Candidates.ValidatorLow,
            ["validatorHigh"] = plan.Candidates.ValidatorHigh,
            ["indexLow"] = plan.Candidates.IndexLow,
            ["indexHigh"] = plan.Candidates.IndexHigh,
            ["indexSpan"] = plan.IndexSpan,
            ["totalCandidates"] = plan.Candidates.TotalCandidates,
            ["cap"] = plan.Candidates.Cap,
            ["enumerated"] = plan.Candidates.EmittedCount,
            ["truncated"] = plan.Candidates.Truncated,
            ["validatorsCovered"] = plan.ValidatorsCovered,
            ["expectedValidatorOffset"] = Sts2SpineProbeMath.MeasuredMeshValidatorOffset,
            ["recommendedCap"] = plan.RecommendedCap,
            ["capIsDeepEnough"] =
                plan.Viable && plan.ValidatorsCovered >= Sts2SpineProbeMath.MeasuredMeshValidatorOffset,
        };

    // ── Readback ─────────────────────────────────────────────────────────────────────────────────────

    // One surface's arrays, flattened into plain CLR arrays so nothing Godot-owned survives the read.
    private sealed class MeshSurfaceRead
    {
        public int SurfaceCount { get; init; }

        public string VertexVariantType { get; init; } = "Nil";

        public double[] X { get; init; } = [];

        public double[] Y { get; init; } = [];

        public string UvVariantType { get; init; } = "Nil";

        public double[] U { get; init; } = [];

        public double[] V { get; init; } = [];

        public string ColorVariantType { get; init; } = "Nil";

        public float[][] Colors { get; init; } = [];

        public string IndexVariantType { get; init; } = "Nil";

        public int[] Indices { get; init; } = [];

        public IReadOnlyList<KeyValuePair<string, string>> PresentArrays { get; init; } = [];

        public double VertexChecksum => Checksum(X, Y);

        public double ColorChecksum
        {
            get
            {
                double sum = 0;
                for (var i = 0; i < Colors.Length; i += 1)
                {
                    var color = Colors[i];
                    for (var c = 0; c < color.Length; c += 1)
                    {
                        sum += (i + 1) * (c + 3) * color[c];
                    }
                }

                return sum;
            }
        }

        private static double Checksum(double[] xs, double[] ys)
        {
            double sum = 0;
            var count = Math.Min(xs.Length, ys.Length);
            for (var i = 0; i < count; i += 1)
            {
                sum += ((i + 1) * xs[i]) + ((i + 3) * ys[i]);
            }

            return sum;
        }
    }

    private static MeshSurfaceRead? TryReadSurface(ulong ridId)
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
                    var v2 = vertex.AsVector2Array();
                    xs = new double[v2.Length];
                    ys = new double[v2.Length];
                    for (var i = 0; i < v2.Length; i += 1)
                    {
                        xs[i] = v2[i].X;
                        ys[i] = v2[i].Y;
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

            var present = new List<KeyValuePair<string, string>>();
            for (var type = 0; type < (int)RenderingServer.ArrayType.Max; type += 1)
            {
                var slot = Slot((RenderingServer.ArrayType)type);
                if (slot.VariantType != Variant.Type.Nil)
                {
                    present.Add(new KeyValuePair<string, string>(
                        ((RenderingServer.ArrayType)type).ToString(),
                        slot.VariantType.ToString()));
                }
            }

            return new MeshSurfaceRead
            {
                SurfaceCount = surfaceCount,
                VertexVariantType = vertex.VariantType.ToString(),
                X = xs,
                Y = ys,
                UvVariantType = uv.VariantType.ToString(),
                U = us,
                V = vs,
                ColorVariantType = colorSlot.VariantType.ToString(),
                Colors = colors,
                IndexVariantType = indexSlot.VariantType.ToString(),
                Indices = indices,
                PresentArrays = present,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryValidateMeshRid(ulong ridId, out int surfaceCount, out int vertexCount)
    {
        surfaceCount = 0;
        vertexCount = 0;
        if (!TryComposeRid(ridId, out var rid))
        {
            return false;
        }

        try
        {
            surfaceCount = RenderingServer.MeshGetSurfaceCount(rid);
        }
        catch
        {
            return false;
        }

        if (surfaceCount != 1)
        {
            return false;
        }

        var read = TryReadSurface(ridId);
        if (read is null)
        {
            return false;
        }

        vertexCount = read.X.Length;

        // A drawable slot mesh is triangles over a positive vertex count with a matching UV channel; anything
        // else in the bracketed space is some other subsystem's mesh that happens to have one surface.
        return vertexCount > 0
            && read.Indices.Length % 3 == 0
            && (read.U.Length == 0 || read.U.Length == vertexCount);
    }

    // A STRICTER sibling of TryValidateMeshRid, never a replacement for it: the offline lane's numbers are a
    // regression anchor and must keep meaning exactly what they meant in Phase 1, so the loose test above is
    // left untouched and the live anchor scan gets its own. `reason` is a stable report key — "not-a-mesh"
    // for the overwhelming majority of candidates (the id simply does not name a mesh), and one of
    // Sts2SpineGeometryMath's rejection reasons for a real mesh that is not this skeleton's.
    private static bool TryValidateSpineLikeMeshRid(
        ulong ridId,
        IReadOnlyList<double>? skeletonBounds,
        out string reason)
    {
        reason = "not-a-mesh";
        if (!TryComposeRid(ridId, out var rid))
        {
            return false;
        }

        int surfaceCount;
        try
        {
            surfaceCount = RenderingServer.MeshGetSurfaceCount(rid);
        }
        catch
        {
            return false;
        }

        if (surfaceCount <= 0)
        {
            return false;
        }

        var read = TryReadSurface(ridId);
        if (read is null)
        {
            reason = "unreadable";
            return false;
        }

        var verdict = Sts2SpineProbeMath.IsSpineLikeSurface(
            DescribeSurfaceFacts(surfaceCount, read),
            skeletonBounds);
        reason = verdict.Reason;
        return verdict.Accepted;
    }

    private static Sts2SpineProbeMath.SurfaceFacts DescribeSurfaceFacts(int surfaceCount, MeshSurfaceRead read)
    {
        double[] uvMin = read.U.Length > 0 ? [read.U.Min(), read.V.Min()] : [0d, 0d];
        double[] uvMax = read.U.Length > 0 ? [read.U.Max(), read.V.Max()] : [0d, 0d];
        return new Sts2SpineProbeMath.SurfaceFacts(
            surfaceCount,
            read.VertexVariantType,
            read.X.Length,
            read.U.Length,
            uvMin,
            uvMax,
            read.Indices.Length,
            DescribeBbox(read));
    }

    private static Dictionary<string, object?> DescribeReadbackDump(IReadOnlyList<ulong> meshRids)
    {
        var entries = new List<Dictionary<string, object?>>();
        var vertexTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ridId in meshRids)
        {
            var read = TryReadSurface(ridId);
            if (read is null)
            {
                entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ridId"] = ridId.ToString(CultureInfo.InvariantCulture),
                    ["failure"] = "surface read returned nothing",
                });
                continue;
            }

            vertexTypes[read.VertexVariantType] = vertexTypes.GetValueOrDefault(read.VertexVariantType) + 1;
            entries.Add(SummarizeSurface(ridId, read));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["meshCount"] = meshRids.Count,
            ["vertexArrayVariantTypes"] = vertexTypes,
            ["meshes"] = entries,
        };
    }

    private static Dictionary<string, object?> SummarizeSurface(ulong ridId, MeshSurfaceRead read)
    {
        var firstVertices = new List<double[]>();
        for (var i = 0; i < Math.Min(8, read.X.Length); i += 1)
        {
            firstVertices.Add([Math.Round(read.X[i], 4), Math.Round(read.Y[i], 4)]);
        }

        double uMin = 0, uMax = 0, vMin = 0, vMax = 0;
        if (read.U.Length > 0)
        {
            uMin = read.U.Min();
            uMax = read.U.Max();
            vMin = read.V.Min();
            vMax = read.V.Max();
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ridId"] = ridId.ToString(CultureInfo.InvariantCulture),
            ["surfaceCount"] = read.SurfaceCount,
            ["vertexCount"] = read.X.Length,
            ["vertexVariantType"] = read.VertexVariantType,
            ["firstVertices"] = firstVertices,
            ["uvVariantType"] = read.UvVariantType,
            ["uvCount"] = read.U.Length,
            ["uvMin"] = new[] { Math.Round(uMin, 6), Math.Round(vMin, 6) },
            ["uvMax"] = new[] { Math.Round(uMax, 6), Math.Round(vMax, 6) },
            ["colorVariantType"] = read.ColorVariantType,
            ["colorCount"] = read.Colors.Length,
            ["firstColor"] = read.Colors.Length > 0 ? read.Colors[0] : null,
            ["indexVariantType"] = read.IndexVariantType,
            ["indexCount"] = read.Indices.Length,
            ["indexDivisibleByThree"] = read.Indices.Length % 3 == 0,
            ["presentArrays"] = read.PresentArrays
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            ["bbox"] = DescribeBbox(read),
        };
    }

    private static double[] DescribeBbox(MeshSurfaceRead read)
    {
        if (read.X.Length == 0)
        {
            return [0, 0, 0, 0];
        }

        return
        [
            Math.Round(read.X.Min(), 4),
            Math.Round(read.Y.Min(), 4),
            Math.Round(read.X.Max(), 4),
            Math.Round(read.Y.Max(), 4),
        ];
    }

    // ── Frame stepping ───────────────────────────────────────────────────────────────────────────────

    private sealed class FrameSteppingArm
    {
        public Dictionary<string, object?> Summary { get; init; } = new(StringComparer.Ordinal);

        public List<Dictionary<string, object?>>? FullSamples { get; init; }

        // Per mesh RID: the flattened vertex reads for every sample, feeding the rigidity fit.
        public Dictionary<ulong, List<MeshSurfaceRead>> PerMeshVertices { get; init; } = [];
    }

    private static async Task<FrameSteppingArm> RunFrameSteppingArmAsync(
        Viewport rootViewport,
        string armName,
        bool forceDraw,
        ProbeLane lane,
        IReadOnlyList<ulong> meshRids,
        GodotObject? skeletonObject,
        ProbeConfig config,
        bool captureFull)
    {
        var perMesh = new Dictionary<ulong, List<MeshSurfaceRead>>();
        foreach (var ridId in meshRids)
        {
            perMesh[ridId] = [];
        }

        var fullSamples = captureFull ? new List<Dictionary<string, object?>>() : null;
        var sampleTimes = new List<double>();
        var entry = lane.Entry;

        for (var sample = 0; sample < config.SampleCount; sample += 1)
        {
            var t = config.SampleCount <= 1
                ? 0f
                : (float)Math.Min(lane.Duration, sample * lane.Duration / (config.SampleCount - 1));
            sampleTimes.Add(Math.Round(t, 5));
            TryRun(() => entry?.SetTrackTime(t));
            await AwaitFramesAsync(rootViewport, 1, forceDraw);

            var sampleMeshes = captureFull ? new List<Dictionary<string, object?>>() : null;
            foreach (var ridId in meshRids)
            {
                var read = TryReadSurface(ridId);
                if (read is null)
                {
                    continue;
                }

                perMesh[ridId].Add(read);
                sampleMeshes?.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ridId"] = ridId.ToString(CultureInfo.InvariantCulture),
                    ["vertexCount"] = read.X.Length,
                    ["x"] = read.X.Select(value => Math.Round(value, 3)).ToArray(),
                    ["y"] = read.Y.Select(value => Math.Round(value, 3)).ToArray(),
                    ["u"] = read.U.Select(value => Math.Round(value, 6)).ToArray(),
                    ["v"] = read.V.Select(value => Math.Round(value, 6)).ToArray(),
                    ["indices"] = read.Indices,
                });
            }

            fullSamples?.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["trackTime"] = Math.Round(t, 5),
                ["meshes"] = sampleMeshes,
                ["slots"] = ReadSlotSnapshot(skeletonObject).Slots,
            });
        }

        // "Did anything move?" per mesh, as the count of consecutive sample pairs whose vertex checksum differs.
        var changedMeshes = 0;
        var totalTransitions = 0;
        var changedTransitions = 0;
        foreach (var (_, reads) in perMesh)
        {
            var meshChanged = false;
            for (var i = 1; i < reads.Count; i += 1)
            {
                totalTransitions += 1;
                if (Math.Abs(reads[i].VertexChecksum - reads[i - 1].VertexChecksum) > 1e-6)
                {
                    changedTransitions += 1;
                    meshChanged = true;
                }
            }

            if (meshChanged)
            {
                changedMeshes += 1;
            }
        }

        return new FrameSteppingArm
        {
            PerMeshVertices = perMesh,
            FullSamples = fullSamples,
            Summary = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["arm"] = armName,
                ["forceDraw"] = forceDraw,
                ["sampleCount"] = config.SampleCount,
                ["sampleTimes"] = sampleTimes,
                ["meshCount"] = meshRids.Count,
                ["meshesWhoseVerticesChanged"] = changedMeshes,
                ["sampleTransitions"] = totalTransitions,
                ["changedSampleTransitions"] = changedTransitions,
                ["verticesTrackedReseek"] = changedTransitions > 0,
            },
        };
    }

    // ── The L1 data loop: per-slot state alongside the geometry ──────────────────────────────────────

    /// <summary>The per-slot dump plus the accessor diagnostics that say how (or whether) it was resolved.</summary>
    private sealed record SlotSnapshot(
        List<Dictionary<string, object?>> Slots,
        Dictionary<string, object?> Diagnostics);

    // Which spelling a spine slot's data object exposes its name under is a fact about the GDExtension's hand
    // written binding, not something derivable, and the first battery guessed wrong: `slotName` came back
    // empty on every slot of both rigs while `get_index` / `get_blend_mode` on the SAME object answered fine,
    // which is the signature of a wrong method name rather than a missing hop. So the probe tries the
    // plausible spellings in order, falls back to the property table, and RECORDS which one answered — and
    // when none does, dumps the object's own name-ish method names so the next run can stop guessing.
    private static readonly string[] SlotNameAccessors = ["get_slot_name", "get_name", "get_slot_name_string"];

    private static readonly string[] SlotNameProperties = ["slot_name", "name"];

    private static readonly string[] AttachmentNameAccessors = ["get_attachment_name", "get_name"];

    private static readonly string[] AttachmentNameProperties = ["attachment_name", "name"];

    private static SlotSnapshot ReadSlotSnapshot(GodotObject? skeletonObject)
    {
        var slots = new List<Dictionary<string, object?>>();
        var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (skeletonObject is null || !GodotObject.IsInstanceValid(skeletonObject))
        {
            diagnostics["failure"] = "no live skeleton object to read slots from";
            return new SlotSnapshot(slots, diagnostics);
        }

        var slotArray = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        if (slotArray is null)
        {
            diagnostics["failure"] = "the skeleton exposed no slot list (get_slots)";
            return new SlotSnapshot(slots, diagnostics);
        }

        // Draw order is a permutation of the same slots. Identity is the natural key — but only if both
        // getters hand back the SAME wrapper objects, and a binding that mints a fresh wrapper per call would
        // make every lookup miss and report -1 for every slot, which is exactly what the first battery
        // produced. So identity is tried first, the slot's own data index second, and the diagnostics say
        // which one actually resolved the column.
        var drawOrderById = new Dictionary<ulong, int>();
        var drawOrderByDataIndex = new Dictionary<int, int>();
        var hasDrawOrder = TryGet(() => skeletonObject.HasMethod("get_draw_order"));
        diagnostics["drawOrderMethodPresent"] = hasDrawOrder;
        var drawOrder = hasDrawOrder
            ? TryGet(() => skeletonObject.Call("get_draw_order").AsGodotArray())
            : null;
        diagnostics["drawOrderCount"] = drawOrder?.Count ?? 0;
        if (drawOrder is not null)
        {
            for (var i = 0; i < drawOrder.Count; i += 1)
            {
                var candidate = TryGet(() => drawOrder[i].AsGodotObject());
                if (candidate is null)
                {
                    continue;
                }

                drawOrderById[candidate.GetInstanceId()] = i;
                var dataIndex = ReadSlotDataIndex(candidate);
                if (dataIndex is { } resolved)
                {
                    drawOrderByDataIndex[resolved] = i;
                }
            }
        }

        var matchedByInstanceId = 0;
        var matchedByDataIndex = 0;
        var unmatched = 0;
        string? slotNameSource = null;
        string? attachmentNameSource = null;
        var namelessDataClass = (string?)null;
        string[]? namelessDataMethods = null;

        for (var i = 0; i < slotArray.Count; i += 1)
        {
            var slot = TryGet(() => slotArray[i].AsGodotObject());
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = i };
            if (slot is null)
            {
                entry["failure"] = "slot entry was not an object";
                slots.Add(entry);
                continue;
            }

            int? slotIndex = null;
            var data = TryGet(() => Invoke(slot, "get_data")?.AsGodotObject());
            if (data is not null)
            {
                slotIndex = TryGet(() => Invoke(data, "get_index")?.AsInt32());
                entry["slotIndex"] = slotIndex;
                entry["blendMode"] = TryGet(() => Invoke(data, "get_blend_mode")?.AsInt32());

                var (name, source) = ResolveName(data, SlotNameAccessors, SlotNameProperties);
                entry["slotName"] = name;
                slotNameSource ??= source;
                if (source is null && namelessDataMethods is null)
                {
                    namelessDataClass = TryGet(() => data.GetClass());
                    namelessDataMethods = DescribeNameLikeMethods(data);
                }
            }
            else
            {
                entry["failure"] = "the slot exposed no data object (get_data)";
            }

            if (drawOrderById.TryGetValue(slot.GetInstanceId(), out var position))
            {
                entry["drawOrderPosition"] = position;
                entry["drawOrderMatchedBy"] = "instanceId";
                matchedByInstanceId += 1;
            }
            else if (slotIndex is { } key && drawOrderByDataIndex.TryGetValue(key, out var byIndex))
            {
                entry["drawOrderPosition"] = byIndex;
                entry["drawOrderMatchedBy"] = "slotDataIndex";
                matchedByDataIndex += 1;
            }
            else
            {
                entry["drawOrderPosition"] = -1;
                entry["drawOrderMatchedBy"] = null;
                unmatched += 1;
            }

            var color = TryGet(() => Invoke(slot, "get_color")?.AsColor());
            if (color is { } c)
            {
                entry["color"] = new[] { c.R, c.G, c.B, c.A };
            }

            var darkColor = TryGet(() => Invoke(slot, "get_dark_color")?.AsColor());
            if (darkColor is { } d)
            {
                entry["darkColor"] = new[] { d.R, d.G, d.B, d.A };
            }

            var attachment = TryGet(() => Invoke(slot, "get_attachment")?.AsGodotObject());
            if (attachment is null)
            {
                entry["attachmentName"] = null;
            }
            else
            {
                var (attachmentName, attachmentSource) =
                    ResolveName(attachment, AttachmentNameAccessors, AttachmentNameProperties);
                entry["attachmentName"] = attachmentName;
                attachmentNameSource ??= attachmentSource;
            }

            var deform = TryGet(() => Invoke(slot, "get_deform"));
            entry["deformLength"] = deform is { } deformVariant
                ? deformVariant.VariantType switch
                {
                    Variant.Type.PackedFloat32Array => deformVariant.AsFloat32Array().Length,
                    Variant.Type.PackedFloat64Array => deformVariant.AsFloat64Array().Length,
                    Variant.Type.Array => deformVariant.AsGodotArray().Count,
                    _ => 0,
                }
                : 0;

            var bone = TryGet(() => Invoke(slot, "get_bone")?.AsGodotObject());
            if (bone is not null)
            {
                entry["bone"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["a"] = TryGet(() => Invoke(bone, "get_a")?.AsSingle()),
                    ["b"] = TryGet(() => Invoke(bone, "get_b")?.AsSingle()),
                    ["c"] = TryGet(() => Invoke(bone, "get_c")?.AsSingle()),
                    ["d"] = TryGet(() => Invoke(bone, "get_d")?.AsSingle()),
                    ["worldX"] = TryGet(() => Invoke(bone, "get_world_x")?.AsSingle()),
                    ["worldY"] = TryGet(() => Invoke(bone, "get_world_y")?.AsSingle()),
                };
            }

            slots.Add(entry);
        }

        diagnostics["slotCount"] = slots.Count;
        diagnostics["slotNameAccessor"] = slotNameSource;
        diagnostics["attachmentNameAccessor"] = attachmentNameSource;
        diagnostics["drawOrderMatchedByInstanceId"] = matchedByInstanceId;
        diagnostics["drawOrderMatchedBySlotDataIndex"] = matchedByDataIndex;
        diagnostics["drawOrderUnmatched"] = unmatched;
        if (slotNameSource is null)
        {
            diagnostics["slotNameFailure"] =
                "no known accessor spelling answered; nameLikeMethodsOnSlotData lists what the object does expose";
            diagnostics["slotDataClass"] = namelessDataClass;
            diagnostics["nameLikeMethodsOnSlotData"] = namelessDataMethods ?? [];
        }

        if (unmatched > 0)
        {
            diagnostics["drawOrderFailure"] = drawOrder is null
                ? "the skeleton exposed no draw order, so no slot could be placed in it"
                : "some slots matched no draw-order entry by identity OR by slot-data index";
        }

        return new SlotSnapshot(slots, diagnostics);
    }

    private static int? ReadSlotDataIndex(GodotObject slot)
    {
        var data = TryGet(() => Invoke(slot, "get_data")?.AsGodotObject());
        return data is null ? null : TryGet(() => Invoke(data, "get_index")?.AsInt32());
    }

    // Try each method spelling, then each property spelling, and report WHICH one answered — a blank column is
    // only informative if the report can distinguish "the object has no name" from "the probe asked wrong".
    private static (string? Value, string? Source) ResolveName(
        GodotObject target,
        IReadOnlyList<string> methods,
        IReadOnlyList<string> properties)
    {
        foreach (var method in methods)
        {
            var value = TryGet(() => Invoke(target, method)?.AsString());
            if (!string.IsNullOrEmpty(value))
            {
                return (value, $"method:{method}");
            }
        }

        foreach (var property in properties)
        {
            // An unreadable property comes back as a Nil variant rather than throwing, so there is nothing to
            // distinguish here beyond the type.
            var variant = TryGet(() => target.Get(property));
            var value = variant.VariantType is Variant.Type.String or Variant.Type.StringName
                ? variant.AsString()
                : null;
            if (!string.IsNullOrEmpty(value))
            {
                return (value, $"property:{property}");
            }
        }

        return (null, null);
    }

    // The escape hatch for a wrong guess: the object's own method names that mention "name". Read once, only
    // when every spelling failed, so the next run can be told the right one instead of guessing again.
    private static string[] DescribeNameLikeMethods(GodotObject target)
    {
        var names = new List<string>();
        var list = TryGet(() => target.GetMethodList());
        if (list is null)
        {
            return [];
        }

        foreach (var entry in list)
        {
            var name = TryGet(() => entry.TryGetValue("name", out var value) ? value.AsString() : null);
            if (!string.IsNullOrEmpty(name) && name.Contains("name", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return [.. names];
    }

    // Call a method only when the object actually exposes it, so a missing binding costs one lookup instead of
    // an engine error line per slot per sample.
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

    // ── Slot ↔ mesh association ──────────────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, object?>> ProbeSlotMeshAssociationAsync(
        Viewport rootViewport,
        GodotObject? skeletonObject,
        IReadOnlyList<ulong> meshRids,
        IReadOnlyList<Node> meshNodes)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["meshNodeCount"] = meshNodes.Count,
            ["meshRidCount"] = meshRids.Count,
        };

        var slotArray = skeletonObject is null ? null : TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        if (slotArray is null || slotArray.Count == 0)
        {
            result["failure"] = "the skeleton exposed no slot list";
            return result;
        }

        result["slotCount"] = slotArray.Count;
        result["childOrderMatchesSlotCount"] = meshNodes.Count == slotArray.Count;

        var probes = new List<Dictionary<string, object?>>();
        // Two slots is enough to tell "child order == slot order" from "some other mapping": if both land on
        // their own ordinal, the orders agree.
        foreach (var slotIndex in new[] { 0, Math.Min(slotArray.Count - 1, slotArray.Count / 2) }.Distinct())
        {
            var probe = new Dictionary<string, object?>(StringComparer.Ordinal) { ["slotOrder"] = slotIndex };
            var slot = TryGet(() => slotArray[slotIndex].AsGodotObject());
            if (slot is null)
            {
                probe["failure"] = "slot entry was not an object";
                probes.Add(probe);
                continue;
            }

            var data = TryGet(() => Invoke(slot, "get_data")?.AsGodotObject());
            probe["slotName"] = data is null ? null : ResolveName(data, SlotNameAccessors, SlotNameProperties).Value;

            var original = TryGet(() => Invoke(slot, "get_color")?.AsColor());
            if (original is not { } baseColor)
            {
                probe["failure"] = "could not read the slot color";
                probes.Add(probe);
                continue;
            }

            var before = meshRids.ToDictionary(rid => rid, rid => TryReadSurface(rid)?.ColorChecksum ?? double.NaN);
            var probeColor = new Color(baseColor.R, baseColor.G, baseColor.B, 0.5f);
            TryRun(() => Invoke(slot, "set_color", probeColor));
            await AwaitFramesAsync(rootViewport, 1, forceDraw: true);

            var changed = new List<string>();
            var changedOrdinals = new List<int>();
            for (var i = 0; i < meshRids.Count; i += 1)
            {
                var rid = meshRids[i];
                var after = TryReadSurface(rid)?.ColorChecksum ?? double.NaN;
                var wasChanged = double.IsNaN(before[rid]) != double.IsNaN(after)
                    || (!double.IsNaN(after) && Math.Abs(after - before[rid]) > 1e-6);
                if (wasChanged)
                {
                    changed.Add(rid.ToString(CultureInfo.InvariantCulture));
                    changedOrdinals.Add(i);
                }
            }

            TryRun(() => Invoke(slot, "set_color", baseColor));
            await AwaitFramesAsync(rootViewport, 1, forceDraw: true);

            probe["changedMeshRids"] = changed;
            probe["changedMeshOrdinals"] = changedOrdinals;
            probe["mappedToOwnOrdinal"] = changedOrdinals.Count == 1 && changedOrdinals[0] == slotIndex;
            probes.Add(probe);
        }

        result["probes"] = probes;
        result["childOrderEqualsSlotOrder"] = probes.Count > 0
            && probes.All(probe => probe.TryGetValue("mappedToOwnOrdinal", out var value) && value is true);
        return result;
    }

    // ── Atlas ────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> DumpAtlas(
        ProbeConfig config,
        string sceneName,
        Node spineNode,
        ILogStream logStream)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            var skeletonData = spineNode.Get("skeleton_data_res");
            if (skeletonData.VariantType != Variant.Type.Object || skeletonData.AsGodotObject() is not Resource dataResource)
            {
                result["failure"] = "the SpineSprite carried no skeleton-data resource";
                return result;
            }

            result["skeletonDataPath"] = dataResource.ResourcePath;
            var atlasVariant = dataResource.Get("atlas_res");
            if (atlasVariant.VariantType != Variant.Type.Object || atlasVariant.AsGodotObject() is not Resource atlasResource)
            {
                result["failure"] = "the skeleton-data resource carried no atlas resource";
                return result;
            }

            result["atlasPath"] = atlasResource.ResourcePath;
            result["atlasClass"] = TryGet(() => atlasResource.GetClass());

            var attempts = new List<Dictionary<string, object?>>();
            var (atlasText, textSource) = ResolveAtlasText(atlasResource, attempts);
            result["textAttempts"] = attempts;
            result["atlasTextSource"] = textSource;
            if (string.IsNullOrWhiteSpace(atlasText))
            {
                // The whole investigation, dumped: which hops were tried, what each handed back, and every
                // text-shaped property the resource actually exposes. Empty here is a RESULT (the disk
                // `.spatlas` remains the accepted fallback), not a hole in the report.
                result["failure"] = "no hop produced atlas text";
                result["textLikeProperties"] = DescribeTextLikeProperties(atlasResource);
                return result;
            }

            var rawFile = $"atlas-{sceneName}.txt";
            TryRun(() => File.WriteAllText(Path.Combine(config.OutDir, rawFile), atlasText));
            result["rawFile"] = rawFile;
            result["rawLength"] = atlasText.Length;

            var parsed = Sts2SpineAtlasText.Parse(atlasText);
            var parsedFile = $"atlas-{sceneName}.json";
            WriteJson(
                config.OutDir,
                parsedFile,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["schema"] = ProbeVersion,
                    ["atlasPath"] = atlasResource.ResourcePath,
                    ["pages"] = parsed.Pages,
                    ["regions"] = parsed.Regions,
                },
                IndentedJson,
                logStream);

            result["parsedFile"] = parsedFile;
            result["pageCount"] = parsed.Pages.Count;
            result["regionCount"] = parsed.Regions.Count;
            result["firstRegions"] = parsed.Regions.Take(5).ToArray();
            return result;
        }
        catch (Exception ex)
        {
            result["failure"] = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }
    }

    // The runtime `atlas_data` read came back EMPTY on both live rigs while the static inspection said the
    // property carries the whole text atlas. Three things could produce that, and each is a different fix, so
    // the probe tries all of them and records which one answered instead of concluding: (1) the property is
    // there but not a String — a PackedByteArray reads as "" through AsString, which looks exactly like empty;
    // (2) the text is behind a method rather than the property; (3) the resource genuinely drops the source
    // text after the atlas is built, in which case only the file on disk still has it.
    private static readonly string[] AtlasTextProperties = ["atlas_data", "source_data", "atlas_text"];

    private static readonly string[] AtlasTextMethods = ["get_atlas_data", "get_source_data"];

    private static readonly string[] AtlasPathProperties = ["atlas_file", "atlas_path", "source_path"];

    private static readonly string[] AtlasDiskExtensions = [".spatlas", ".atlas"];

    private static (string? Text, string? Source) ResolveAtlasText(
        Resource atlas,
        List<Dictionary<string, object?>> attempts)
    {
        foreach (var property in AtlasTextProperties)
        {
            var variant = TryGet(() => atlas.Get(property));
            var text = VariantAsText(variant);
            attempts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hop"] = $"property:{property}",
                ["variantType"] = variant.VariantType.ToString(),
                ["length"] = text?.Length ?? 0,
            });

            if (!string.IsNullOrWhiteSpace(text))
            {
                return (text, $"property:{property}");
            }
        }

        foreach (var method in AtlasTextMethods)
        {
            var present = TryGet(() => atlas.HasMethod(method));
            var variant = present ? TryGet(() => Invoke(atlas, method)) ?? default : default;
            var text = VariantAsText(variant);
            attempts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hop"] = $"method:{method}",
                ["present"] = present,
                ["variantType"] = variant.VariantType.ToString(),
                ["length"] = text?.Length ?? 0,
            });

            if (!string.IsNullOrWhiteSpace(text))
            {
                return (text, $"method:{method}");
            }
        }

        foreach (var path in AtlasDiskCandidates(atlas))
        {
            var exists = TryGet(() => Godot.FileAccess.FileExists(path));
            var text = exists ? TryGet(() => Godot.FileAccess.GetFileAsString(path)) : null;
            attempts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hop"] = $"disk:{path}",
                ["exists"] = exists,
                ["length"] = text?.Length ?? 0,
            });

            if (!string.IsNullOrWhiteSpace(text))
            {
                return (text, $"disk:{path}");
            }
        }

        return (null, null);
    }

    private static string? VariantAsText(Variant variant)
        => variant.VariantType switch
        {
            Variant.Type.String or Variant.Type.StringName => variant.AsString(),
            // A byte array reads as the empty string through AsString, which is indistinguishable from an
            // absent property unless it is decoded explicitly.
            Variant.Type.PackedByteArray => TryGet(() => Encoding.UTF8.GetString(variant.AsByteArray())),
            _ => null,
        };

    private static IEnumerable<string> AtlasDiskCandidates(Resource atlas)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in AtlasPathProperties)
        {
            var variant = TryGet(() => atlas.Get(property));
            var path = variant.VariantType == Variant.Type.String ? variant.AsString() : null;
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                yield return path;
            }
        }

        var resourcePath = TryGet(() => atlas.ResourcePath) ?? string.Empty;
        if (resourcePath.Length == 0)
        {
            yield break;
        }

        if (seen.Add(resourcePath))
        {
            yield return resourcePath;
        }

        // An imported atlas is addressed by a resource path whose extension is the IMPORTER's, not the text
        // file's; the sibling with the atlas extension is the source the text actually lives in.
        var withoutExtension = resourcePath[..(resourcePath.LastIndexOf('.') is var dot && dot > 0 ? dot : resourcePath.Length)];
        foreach (var extension in AtlasDiskExtensions)
        {
            var candidate = withoutExtension + extension;
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static Dictionary<string, object?>[] DescribeTextLikeProperties(Resource atlas)
    {
        var described = new List<Dictionary<string, object?>>();
        var list = TryGet(() => atlas.GetPropertyList());
        if (list is null)
        {
            return [];
        }

        foreach (var entry in list)
        {
            var name = TryGet(() => entry.TryGetValue("name", out var value) ? value.AsString() : null);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var variant = TryGet(() => atlas.Get(name));
            if (variant.VariantType is not (Variant.Type.String or Variant.Type.StringName
                or Variant.Type.PackedByteArray or Variant.Type.PackedStringArray))
            {
                continue;
            }

            described.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["variantType"] = variant.VariantType.ToString(),
                ["length"] = VariantAsText(variant)?.Length ?? 0,
            });
        }

        return [.. described];
    }

    // ── Rigidity ─────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> SummarizeRigidity(
        IReadOnlyDictionary<ulong, List<MeshSurfaceRead>> perMesh)
    {
        var entries = new List<Dictionary<string, object?>>();
        var rigid = 0;
        var deforming = 0;
        var skipped = 0;

        foreach (var (ridId, reads) in perMesh)
        {
            if (reads.Count < 2 || reads[0].X.Length < 3)
            {
                skipped += 1;
                continue;
            }

            var baseRead = reads[0];
            var diagonal = Sts2SpineGeometryMath.BoundingBoxDiagonal(baseRead.X, baseRead.Y);
            double worstNormalized = 0;
            double worstResidual = 0;
            var degenerate = false;
            var comparable = 0;

            for (var i = 1; i < reads.Count; i += 1)
            {
                var target = reads[i];
                if (target.X.Length != baseRead.X.Length)
                {
                    // A vertex-count change means the attachment itself was swapped; there is no affine map
                    // between the two poses to fit, and the change is by definition not rigid.
                    degenerate = true;
                    continue;
                }

                comparable += 1;
                var fit = Sts2SpineGeometryMath.FitAffine2D(baseRead.X, baseRead.Y, target.X, target.Y);
                var verdict = Sts2SpineGeometryMath.ClassifyRigidity(
                    fit.MaxResidual,
                    diagonal,
                    Sts2SpineGeometryMath.DefaultRigidThreshold);
                worstResidual = Math.Max(worstResidual, fit.MaxResidual);
                worstNormalized = Math.Max(worstNormalized, double.IsInfinity(verdict.NormalizedResidual) ? 1e9 : verdict.NormalizedResidual);
                degenerate |= fit.Degenerate;
            }

            var isRigid = comparable > 0 && worstNormalized < Sts2SpineGeometryMath.DefaultRigidThreshold;
            if (comparable == 0)
            {
                skipped += 1;
            }
            else if (isRigid)
            {
                rigid += 1;
            }
            else
            {
                deforming += 1;
            }

            entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ridId"] = ridId.ToString(CultureInfo.InvariantCulture),
                ["vertexCount"] = baseRead.X.Length,
                ["bboxDiagonal"] = Math.Round(diagonal, 4),
                ["comparedSamples"] = comparable,
                ["maxResidual"] = Math.Round(worstResidual, 6),
                ["maxResidualNormalized"] = Math.Round(worstNormalized, 8),
                ["classification"] = comparable == 0 ? "unknown" : isRigid ? "rigid" : "deforming",
                ["degenerateFit"] = degenerate,
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["threshold"] = Sts2SpineGeometryMath.DefaultRigidThreshold,
            ["rigid"] = rigid,
            ["deforming"] = deforming,
            ["skipped"] = skipped,
            ["perMesh"] = entries,
        };
    }

    // ── Timing ───────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> MeasureReadbackTiming(IReadOnlyList<ulong> meshRids, int reps)
    {
        var samples = new List<double>(reps);
        // Per mesh as well as per skeleton: the whole-skeleton number is what a streaming host would pay per
        // frame, but only the per-mesh split says whether the cost is one pathological slot or all of them.
        var perMesh = meshRids.ToDictionary(rid => rid, _ => new List<double>(reps));
        var stopwatch = new Stopwatch();
        var perMeshWatch = new Stopwatch();
        for (var rep = 0; rep < reps; rep += 1)
        {
            stopwatch.Restart();
            foreach (var ridId in meshRids)
            {
                perMeshWatch.Restart();
                TryReadSurface(ridId);
                perMeshWatch.Stop();
                perMesh[ridId].Add(perMeshWatch.Elapsed.TotalMilliseconds);
            }

            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var sorted = samples.OrderBy(value => value).ToArray();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["meshCount"] = meshRids.Count,
            ["reps"] = reps,
            ["minMs"] = sorted.Length > 0 ? Math.Round(sorted[0], 4) : 0,
            ["medianMs"] = sorted.Length > 0 ? Math.Round(sorted[sorted.Length / 2], 4) : 0,
            ["maxMs"] = sorted.Length > 0 ? Math.Round(sorted[^1], 4) : 0,
            ["samplesMs"] = samples.Select(value => Math.Round(value, 4)).ToArray(),
            ["perMeshMedianMs"] = meshRids.Select(rid => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ridId"] = rid.ToString(CultureInfo.InvariantCulture),
                ["medianMs"] = Math.Round(Median(perMesh[rid]), 5),
                ["maxMs"] = Math.Round(perMesh[rid].Count > 0 ? perMesh[rid].Max() : 0, 5),
            }).ToArray(),
        };
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    // ── Coordinate space and readback sanity ─────────────────────────────────────────────────────────

    // Is what came back actually this skeleton's geometry? Two independent checks, both cheap: the union of
    // every mesh's vertices against the skeleton's OWN reported bounds (which also settles which space the
    // vertices are in), and the UV extents against the unit square a texture atlas addresses. A sweep that
    // collected some other subsystem's meshes fails both.
    private static Dictionary<string, object?> DescribeGeometrySanity(
        MegaSprite? sprite,
        Node spineNode,
        IReadOnlyList<ulong> meshRids)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
        var any = false;
        var meshesWithUvs = 0;
        var meshesRead = 0;
        foreach (var ridId in meshRids)
        {
            var read = TryReadSurface(ridId);
            if (read is null || read.X.Length == 0)
            {
                continue;
            }

            meshesRead += 1;
            any = true;
            minX = Math.Min(minX, read.X.Min());
            minY = Math.Min(minY, read.Y.Min());
            maxX = Math.Max(maxX, read.X.Max());
            maxY = Math.Max(maxY, read.Y.Max());

            if (read.U.Length > 0)
            {
                meshesWithUvs += 1;
                uMin = Math.Min(uMin, read.U.Min());
                uMax = Math.Max(uMax, read.U.Max());
                vMin = Math.Min(vMin, read.V.Min());
                vMax = Math.Max(vMax, read.V.Max());
            }
        }

        var union = any ? new[] { minX, minY, maxX, maxY } : [0d, 0d, 0d, 0d];
        result["meshesRead"] = meshesRead;
        result["meshVertexUnionBbox"] = union.Select(value => Math.Round(value, 4)).ToArray();
        result["meshesWithUvs"] = meshesWithUvs;
        result["uvMin"] = meshesWithUvs > 0 ? new[] { Math.Round(uMin, 6), Math.Round(vMin, 6) } : null;
        result["uvMax"] = meshesWithUvs > 0 ? new[] { Math.Round(uMax, 6), Math.Round(vMax, 6) } : null;
        // A hair of tolerance: a packed atlas region's outer edge lands exactly on 0 or 1 and float rounding
        // can push it a few ULPs past.
        result["uvsWithinUnitRange"] = meshesWithUvs > 0
            && uMin >= -1e-4 && vMin >= -1e-4 && uMax <= 1 + 1e-4 && vMax <= 1 + 1e-4;

        var bounds = TryGet(() => sprite?.GetSkeleton()?.GetBounds());
        result["skeletonBoundsRead"] = bounds is not null;
        if (bounds is { } rect)
        {
            // Containment is stated both ways: the strict "every vertex is inside the skeleton's own bounds"
            // and the area ratio, because a skeleton's reported bounds are a superset of the drawn attachments
            // (it includes slots whose attachment is currently null), so a ratio well under 1 is normal and a
            // ratio far ABOVE 1 is the real alarm.
            var boundsRect = new[] { (double)rect.Position.X, rect.Position.Y, rect.End.X, rect.End.Y };
            result["skeletonBounds"] = boundsRect.Select(value => Math.Round(value, 4)).ToArray();

            var tolerance = Math.Max(1d, Math.Max(boundsRect[2] - boundsRect[0], boundsRect[3] - boundsRect[1])) * 0.02;
            result["unionInsideSkeletonBounds"] = any
                && union[0] >= boundsRect[0] - tolerance
                && union[1] >= boundsRect[1] - tolerance
                && union[2] <= boundsRect[2] + tolerance
                && union[3] <= boundsRect[3] + tolerance;

            var boundsArea = Math.Max(0d, boundsRect[2] - boundsRect[0]) * Math.Max(0d, boundsRect[3] - boundsRect[1]);
            var unionArea = any ? Math.Max(0d, union[2] - union[0]) * Math.Max(0d, union[3] - union[1]) : 0d;
            result["unionAreaOverBoundsArea"] = boundsArea > 0 ? Math.Round(unionArea / boundsArea, 4) : (double?)null;
        }

        if (spineNode is Node2D node2d)
        {
            var local = TryGet(() => node2d.Transform);
            var global = TryGet(() => node2d.GlobalTransform);
            result["nodeTransform"] = DescribeTransform(local);
            result["nodeGlobalTransform"] = DescribeTransform(global);
        }

        // The verdict: mesh vertices that already agree with the skeleton's own reported bounds are in
        // skeleton-local space (so a consumer must apply the node transform itself); vertices that agree with
        // the node-transformed bounds are already in the node's parent space.
        string verdict = "unknown";
        if (any && bounds is { } b)
        {
            var localMatch = RectsAgree(union, [b.Position.X, b.Position.Y, b.End.X, b.End.Y]);
            verdict = localMatch
                ? "skeleton-local (mesh vertices match SpineSkeleton.get_bounds directly)"
                : "not-skeleton-local (mesh vertices disagree with SpineSkeleton.get_bounds; compare against the node transform)";
        }

        result["verdict"] = verdict;
        return result;
    }

    private static bool RectsAgree(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var scale = Math.Max(1d, Math.Max(Math.Abs(b[2] - b[0]), Math.Abs(b[3] - b[1])));
        for (var i = 0; i < 4; i += 1)
        {
            if (Math.Abs(a[i] - b[i]) > scale * 0.02)
            {
                return false;
            }
        }

        return true;
    }

    private static double[] DescribeTransform(Transform2D transform)
        => [transform.X.X, transform.X.Y, transform.Y.X, transform.Y.Y, transform.Origin.X, transform.Origin.Y];

    // ── Live battery (read-only) ─────────────────────────────────────────────────────────────────────
    //
    // Four gates, each of which must announce itself in the log whether it passes, fails, or never ran — a
    // gate that is merely ABSENT from the report is indistinguishable from a battery that crashed before
    // reaching it, and the operator running this cannot see the game's own screen:
    //   (a) acquisition — recipe D found as many meshes as the sprite has SpineMesh2D children;
    //   (b) readback sanity — those meshes read back as this skeleton (bounds + unit-square UVs);
    //   (c) liveness — the vertices move on their own between two reads, on the game's clock;
    //   (d) timing — what a whole-skeleton readback costs, measured provably on the main thread.
    //
    // Read-only throughout: no colour is written, no animation is set, no time is sought, no draw is forced.
    // The only thing created is the probe's own mesh, which the sweep excludes and the battery frees.

    private const int LiveMinimumMeshChildren = 10;

    /// <summary>A SpineSprite the poller considered, and the two facts that decide whether it qualifies.</summary>
    private sealed record LiveCandidate(
        ulong InstanceId,
        string? Name,
        string? Path,
        int MeshChildren,
        bool AnimationStateReady);

    private static async Task RunLiveBatteryAsync(ProbeConfig config, ILogStream logStream)
    {
        Log(
            logStream,
            $"live battery waiting up to {config.LiveWaitSeconds}s for a SpineSprite with at least "
            + $"{LiveMinimumMeshChildren} SpineMesh2D children; it takes the RICHEST one, not the first.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.LiveWaitSeconds);
        var survey = new List<LiveCandidate>();
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            survey = OnMainThread(SurveyLiveSpineCandidates, []);
            var target = PickLiveSpineCandidate(survey);
            if (target is not null)
            {
                Log(logStream, $"live candidates: {DescribeCandidates(survey)}");
                Log(
                    logStream,
                    $"live target chosen: '{target.Path}' meshChildren={target.MeshChildren} "
                    + $"(of {survey.Count} SpineSprite(s) in the tree)");

                var report = await Sts2MainThreadDispatcher
                    .InvokeAsync(() => RunLiveOnMainThreadAsync(config, target, survey, logStream))
                    .ConfigureAwait(false);
                WriteJson(config.OutDir, "live.json", report, IndentedJson, logStream);
                Log(logStream, "live battery done -> live.json");
                return;
            }

            // Every fifth poll (~10s), so a battery that is waiting says WHAT it is waiting past.
            if (attempt % 5 == 0)
            {
                Log(logStream, $"live battery still waiting; candidates: {DescribeCandidates(survey)}");
            }

            attempt += 1;
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        var reason = $"no SpineSprite qualified within {config.LiveWaitSeconds}s "
            + $"(needs at least {LiveMinimumMeshChildren} SpineMesh2D children and a ready animation state)";
        Log(logStream, $"live battery gave up: {reason}. Last survey: {DescribeCandidates(survey)}");

        // A give-up still writes the report. The runner gates on this file; an absent one would say "the probe
        // never ran" when what actually happened is "the probe ran and found nothing to run on".
        var abandoned = NewReport("live", config);
        abandoned["notes"] = new List<string> { reason };
        abandoned["candidates"] = survey.Select(DescribeCandidate).ToArray();
        var abandonedGates = new Dictionary<string, object?>(StringComparer.Ordinal);
        abandoned["gates"] = abandonedGates;
        SkipRemainingGates(abandonedGates, logStream, reason);
        WriteJson(config.OutDir, "live.json", abandoned, IndentedJson, logStream);
    }

    private static List<LiveCandidate> SurveyLiveSpineCandidates()
    {
        var found = new List<LiveCandidate>();
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root is null)
        {
            return found;
        }

        foreach (var node in EnumerateTree(root))
        {
            if (!TryGet(() => node.GetClass().Contains("SpineSprite", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The readiness idiom the live inspector uses: ask the sprite whether its animation state exists.
            // Never `get_current` — polling that is a known uncatchable crash, and nothing here needs it.
            found.Add(new LiveCandidate(
                node.GetInstanceId(),
                TryGet(() => node.Name.ToString()),
                TryGet(() => node.GetPath().ToString()),
                CollectMeshNodes(node).Count,
                TryGet(() => new MegaSprite(node).IsAnimationStateReady())));
        }

        return found;
    }

    // The RICHEST qualifying sprite, not the first. Taking the first is what bound the battery to a menu
    // backdrop rig on the previous run — the backdrop clears the ten-child bar too, and the tree walk reaches
    // it before the character. Slot count is the only cheap proxy for "this is the creature", so the sprite
    // with the most mesh children wins; the path tiebreak keeps the choice reproducible between runs.
    private static LiveCandidate? PickLiveSpineCandidate(IReadOnlyList<LiveCandidate> candidates)
        => candidates
            .Where(candidate => candidate.MeshChildren >= LiveMinimumMeshChildren && candidate.AnimationStateReady)
            .OrderByDescending(candidate => candidate.MeshChildren)
            .ThenBy(candidate => candidate.Path ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string DescribeCandidates(IReadOnlyList<LiveCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return "none (either no SpineSprite is in the tree, or the main-thread survey itself did not run)";
        }

        var listed = candidates
            .OrderByDescending(candidate => candidate.MeshChildren)
            .Take(12)
            .Select(candidate =>
                $"'{candidate.Path}' meshChildren={candidate.MeshChildren} ready={candidate.AnimationStateReady}");
        var suffix = candidates.Count > 12 ? $" (+{candidates.Count - 12} more)" : string.Empty;
        return string.Join("; ", listed) + suffix;
    }

    private static Dictionary<string, object?> DescribeCandidate(LiveCandidate candidate)
        => new(StringComparer.Ordinal)
        {
            ["instanceId"] = candidate.InstanceId.ToString(CultureInfo.InvariantCulture),
            ["name"] = candidate.Name,
            ["path"] = candidate.Path,
            ["spineMesh2dChildren"] = candidate.MeshChildren,
            ["animationStateReady"] = candidate.AnimationStateReady,
            ["qualifies"] = candidate.MeshChildren >= LiveMinimumMeshChildren && candidate.AnimationStateReady,
        };

    private static async Task<Dictionary<string, object?>> RunLiveOnMainThreadAsync(
        ProbeConfig config,
        LiveCandidate target,
        IReadOnlyList<LiveCandidate> survey,
        ILogStream logStream)
    {
        var report = NewReport("live", config);
        var notes = new List<string>();
        report["notes"] = notes;
        report["ridConstruction"] = DescribeRidConstruction();
        report["candidates"] = survey.Select(DescribeCandidate).ToArray();
        report["target"] = DescribeCandidate(target);
        var gates = new Dictionary<string, object?>(StringComparer.Ordinal);
        report["gates"] = gates;

        var probeRids = new List<Rid>();
        try
        {
            if (GodotObject.InstanceFromId(target.InstanceId) is not Node spineNode
                || !GodotObject.IsInstanceValid(spineNode))
            {
                const string reason = "the chosen SpineSprite went away between the survey and the battery";
                notes.Add(reason);
                SkipRemainingGates(gates, logStream, reason);
                return report;
            }

            report["node"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["instanceId"] = target.InstanceId.ToString(CultureInfo.InvariantCulture),
                ["name"] = TryGet(() => spineNode.Name.ToString()),
                ["path"] = TryGet(() => spineNode.GetPath().ToString()),
                ["sceneFilePath"] = TryGet(() => spineNode.SceneFilePath),
                ["class"] = TryGet(() => spineNode.GetClass()),
            };

            var sprite = TryGet(() => new MegaSprite(spineNode));
            var skeletonObject = TryGet(() => sprite?.GetSkeleton()?.BoundObject);
            var meshNodes = CollectMeshNodes(spineNode);
            report["structure"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spineMesh2dChildren"] = meshNodes.Count,
                ["slotCount"] = CountSlots(skeletonObject),
                ["skeletonObjectClass"] = TryGet(() => skeletonObject?.GetClass()),
            };

            // ── (a) ACQUISITION ──────────────────────────────────────────────────────────────────────
            // The rig neighbourhood is surveyed BEFORE the acquisition because the trim needs it: on a live
            // menu the walked run spans several rigs, and their mesh-child counts in build order are one of
            // the two signals that say which slice of it belongs to the target.
            var skeletonBounds = TryReadSkeletonBounds(spineNode);
            var neighbourhood = SurveyRigNeighbourhood(survey, target.InstanceId);
            var targetOrdinal = neighbourhood.FindIndex(rig => rig.IsTarget);
            report["rigNeighbourhood"] = neighbourhood.Select(DescribeRigNeighbour).ToArray();

            var acquisition = await AcquireLiveMeshRidsAsync(
                config, meshNodes, skeletonBounds, neighbourhood, targetOrdinal, probeRids, logStream);
            report["acquisition"] = acquisition.Report;
            var meshRids = acquisition.Found;
            if (acquisition.Failure is { } failure)
            {
                RecordGate(gates, logStream, "a", "acquisition", "fail", failure);
                SkipRemainingGates(gates, logStream, "acquisition recovered no mesh RID");
                return report;
            }

            // The trim decides gate (a), and it may honestly refuse to decide: when the build-order and
            // bounds signals disagree the run is REPORTED untrimmed and the gate fails, because a lane that
            // picked one signal would ship a wrong answer under a green gate. Gates (b)-(d) are measured on
            // the trimmed window when there is one and on the whole run when there is not — gate (b) passes
            // either way once the foreign-vertex-format meshes are rejected, which is the point of the
            // spine-likeness test in the walk.
            var gateMeshRids = acquisition.TrimAgreed && acquisition.Trimmed.Count > 0
                ? acquisition.Trimmed
                : meshRids;
            report["gateMeshSource"] = acquisition.TrimAgreed && acquisition.Trimmed.Count > 0
                ? "trimmed-to-target"
                : "untrimmed-run";

            // GetValueOrDefault, never the indexer: a Dictionary getter THROWS on a missing key, and a detail
            // string is the last place a battery should be able to die.
            var acquisitionDetail =
                $"found={meshRids.Count} expected={meshNodes.Count} trimmed={acquisition.Trimmed.Count} "
                + $"trim={acquisition.TrimSummary} "
                + $"method={acquisition.Report.GetValueOrDefault("method")} "
                + $"candidates={acquisition.Report.GetValueOrDefault("candidatesTested")} "
                + $"sweepMs={acquisition.Report.GetValueOrDefault("sweepMs")} "
                + $"validatorWindow={acquisition.Report.GetValueOrDefault("validatorWindow")}"
                + (acquisition.Report.GetValueOrDefault("shortfallHint") is string hint ? $" — {hint}" : string.Empty);
            var acquired = meshNodes.Count > 0
                && (acquisition.TrimAgreed
                    ? acquisition.Trimmed.Count == meshNodes.Count
                    : meshRids.Count == meshNodes.Count);
            RecordGate(gates, logStream, "a", "acquisition", acquired ? "pass" : "fail", acquisitionDetail);
            if (gateMeshRids.Count == 0)
            {
                SkipRemainingGates(gates, logStream, "the sweep validated no mesh RID, so nothing could be read");
                return report;
            }

            if (!acquired)
            {
                notes.Add(
                    "Gate (a) did not close: either the recovered mesh count does not match the sprite's "
                    + "SpineMesh2D child count, or the two trim signals did not agree on which slice of the run "
                    + "is this rig's. Every gate after (a) is measured anyway, on "
                    + (acquisition.TrimAgreed ? "the trimmed window" : "the whole run")
                    + ", and must be read with that in mind.");
            }

            report["readback"] = DescribeReadbackDump(gateMeshRids);

            // ── (b) READBACK SANITY ──────────────────────────────────────────────────────────────────
            var sanity = DescribeGeometrySanity(sprite, spineNode, gateMeshRids);
            report["readbackSanity"] = sanity;
            var uvsOk = sanity.GetValueOrDefault("uvsWithinUnitRange") is true;
            var boundsRead = sanity.GetValueOrDefault("skeletonBoundsRead") is true;
            var inside = sanity.GetValueOrDefault("unionInsideSkeletonBounds") is true;
            var sanityDetail =
                $"uvsInUnitSquare={uvsOk} uvMin={Join(sanity.GetValueOrDefault("uvMin"))} "
                + $"uvMax={Join(sanity.GetValueOrDefault("uvMax"))} "
                + $"unionBbox={Join(sanity.GetValueOrDefault("meshVertexUnionBbox"))} "
                + $"skeletonBounds={Join(sanity.GetValueOrDefault("skeletonBounds"))} "
                + $"insideBounds={inside} areaRatio={sanity.GetValueOrDefault("unionAreaOverBoundsArea")}";
            if (!boundsRead)
            {
                RecordGate(
                    gates,
                    logStream,
                    "b",
                    "readback-sanity",
                    "fail",
                    $"the skeleton reported no bounds to compare against; {sanityDetail}");
            }
            else
            {
                RecordGate(gates, logStream, "b", "readback-sanity", uvsOk && inside ? "pass" : "fail", sanityDetail);
            }

            // ── (c) LIVENESS ─────────────────────────────────────────────────────────────────────────
            // Two whole-skeleton readbacks ~200ms apart. The game is animating on its own clock; nothing here
            // seeks time or touches the animation state.
            var first = gateMeshRids.ToDictionary(rid => rid, rid => TryReadSurface(rid)?.VertexChecksum ?? double.NaN);
            // Bare await: same main-thread-affinity rule as the offline battery above.
            var elapsed = await AwaitWallClockAsync(spineNode, TimeSpan.FromMilliseconds(200));
            var second = gateMeshRids.ToDictionary(rid => rid, rid => TryReadSurface(rid)?.VertexChecksum ?? double.NaN);

            var changed = gateMeshRids.Count(rid =>
                double.IsNaN(first[rid]) != double.IsNaN(second[rid])
                || (!double.IsNaN(first[rid]) && Math.Abs(first[rid] - second[rid]) > 1e-6));

            report["liveMotion"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["gapMs"] = Math.Round(elapsed.TotalMilliseconds, 2),
                ["meshCount"] = gateMeshRids.Count,
                ["meshesChanged"] = changed,
                ["verticesTrackLiveAnimation"] = changed > 0,
            };
            RecordGate(
                gates,
                logStream,
                "c",
                "liveness",
                changed > 0 ? "pass" : "fail",
                $"meshesChanged={changed}/{gateMeshRids.Count} gapMs={Math.Round(elapsed.TotalMilliseconds, 2)} "
                + "(zero can also mean the rig is genuinely at rest — check the target is an animating one)");

            // ── (d) TIMING ───────────────────────────────────────────────────────────────────────────
            // Recorded next to the proof that the measurement is on the thread it claims to be: a readback
            // timed from a pool thread would be pricing a cross-thread hop into the renderer instead.
            var onMainThread = Sts2MainThreadDispatcher.DescribeStatus().IsOnCapturedThread;
            report["onMainThreadAtTiming"] = onMainThread;
            var timing = MeasureReadbackTiming(gateMeshRids, config.TimingReps);
            report["timing"] = timing;
            RecordGate(
                gates,
                logStream,
                "d",
                "timing",
                onMainThread ? "pass" : "fail",
                $"reps={config.TimingReps} medianMs={timing.GetValueOrDefault("medianMs")} "
                + $"maxMs={timing.GetValueOrDefault("maxMs")} meshes={gateMeshRids.Count} "
                + $"onMainThread={onMainThread}");

            var snapshot = ReadSlotSnapshot(skeletonObject);
            report["slots"] = snapshot.Slots;
            report["slotDiagnostics"] = snapshot.Diagnostics;
            report["atlas"] = DumpAtlas(config, $"live-{SanitizeName(target.Name)}", spineNode, logStream);
            return report;
        }
        catch (Exception ex)
        {
            var reason = $"live battery threw: {ex.GetType().Name}: {ex.Message}";
            notes.Add(reason);
            SkipRemainingGates(gates, logStream, reason);
            return report;
        }
        finally
        {
            foreach (var rid in probeRids)
            {
                TryRun(() => RenderingServer.FreeRid(rid));
            }
        }
    }

    /// <summary>
    /// What recipe D recovered. `Found` is the UNTRIMMED run — every mesh the walk stood on, which on a
    /// live menu spans several rigs — and `Trimmed` is the target's own window when, and only when, the two
    /// trim signals agreed on where it starts.
    /// </summary>
    private sealed record LiveAcquisition(
        Dictionary<string, object?> Report,
        List<ulong> Found,
        List<ulong> Trimmed,
        bool TrimAgreed,
        string TrimSummary,
        string? Failure);

    /// <summary>
    /// One SpineSprite in the live tree as the trim's build-order signal needs to see it: how many meshes it
    /// owns, where its canvas items sit on the validator axis (which is the order the rigs were built in), and
    /// what bounds it reports. Ordered by ascending canvas-validator low, this is the sequence the walked run
    /// is a contiguous window of.
    /// </summary>
    private sealed record RigNeighbour(
        ulong InstanceId,
        string? Path,
        int MeshChildren,
        bool CanvasRead,
        uint CanvasValidatorLow,
        uint CanvasValidatorHigh,
        double[]? SkeletonBounds,
        bool IsTarget);

    private static List<RigNeighbour> SurveyRigNeighbourhood(
        IReadOnlyList<LiveCandidate> survey,
        ulong targetInstanceId)
    {
        var rigs = new List<RigNeighbour>();
        foreach (var candidate in survey)
        {
            if (GodotObject.InstanceFromId(candidate.InstanceId) is not Node node
                || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            var meshNodes = CollectMeshNodes(node);
            var canvasIds = CollectCanvasItemRids(meshNodes)
                .Where(item => item.Read)
                .Select(item => Sts2SpineGeometryMath.DecodeRid(item.Id).Validator)
                .ToArray();

            rigs.Add(new RigNeighbour(
                candidate.InstanceId,
                candidate.Path,
                meshNodes.Count,
                canvasIds.Length > 0,
                canvasIds.Length > 0 ? canvasIds.Min() : 0u,
                canvasIds.Length > 0 ? canvasIds.Max() : 0u,
                TryReadSkeletonBounds(node),
                candidate.InstanceId == targetInstanceId));
        }

        // Ascending canvas-validator low IS build order: those RIDs came out of the same global counter while
        // each rig was being constructed. A rig whose canvas items could not be read is parked at the end
        // rather than dropped, so its mesh children still count against the run-length budget.
        return
        [
            .. rigs
                .OrderBy(rig => rig.CanvasRead ? rig.CanvasValidatorLow : uint.MaxValue)
                .ThenBy(rig => rig.Path ?? string.Empty, StringComparer.Ordinal),
        ];
    }

    private static double[]? TryReadSkeletonBounds(Node spineNode)
    {
        var rect = TryGet(() => new MegaSprite(spineNode).GetSkeleton()?.GetBounds());
        return rect is { } bounds
            ? [bounds.Position.X, bounds.Position.Y, bounds.End.X, bounds.End.Y]
            : null;
    }

    private static Dictionary<string, object?> DescribeRigNeighbour(RigNeighbour rig)
        => new(StringComparer.Ordinal)
        {
            ["instanceId"] = rig.InstanceId.ToString(CultureInfo.InvariantCulture),
            ["path"] = rig.Path,
            ["meshChildren"] = rig.MeshChildren,
            ["canvasValidatorLow"] = rig.CanvasRead ? rig.CanvasValidatorLow : (uint?)null,
            ["canvasValidatorHigh"] = rig.CanvasRead ? rig.CanvasValidatorHigh : (uint?)null,
            ["skeletonBounds"] = rig.SkeletonBounds?.Select(value => Math.Round(value, 4)).ToArray(),
            ["isTarget"] = rig.IsTarget,
        };

    private static async Task<LiveAcquisition> AcquireLiveMeshRidsAsync(
        ProbeConfig config,
        IReadOnlyList<Node> meshNodes,
        IReadOnlyList<double>? skeletonBounds,
        IReadOnlyList<RigNeighbour> neighbourhood,
        int targetOrdinal,
        List<Rid> probeRids,
        ILogStream logStream)
    {
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["recipe"] = "D-canvas-item-bracket" };
        report["rigNeighbourhood"] = neighbourhood.Select(DescribeRigNeighbour).ToArray();
        report["targetOrdinal"] = targetOrdinal;
        report["targetSkeletonBounds"] = skeletonBounds?.Select(value => Math.Round(value, 4)).ToArray();
        if (meshNodes.Count == 0)
        {
            report["failure"] = "the sprite has no SpineMesh2D children";
            return new LiveAcquisition(report, [], [], false, "not-applicable", "the sprite has no SpineMesh2D children");
        }

        var canvasItems = CollectCanvasItemRids(meshNodes);
        report["canvasItems"] = DescribeCanvasItems(canvasItems);
        var canvasIds = canvasItems.Where(item => item.Read).Select(item => item.Id).ToArray();

        // The window's upper edge, minted now. See LiveProbeMeshCount for why it is a burst rather than one.
        for (var i = 0; i < LiveProbeMeshCount; i += 1)
        {
            var rid = TryGet(() => RenderingServer.MeshCreate(), default);
            if (rid.Id != 0)
            {
                probeRids.Add(rid);
            }
        }

        var probeIds = probeRids.Select(rid => rid.Id).ToArray();
        report["probeMeshRids"] = probeIds
            .Select(id => id.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        // Both tunables were defaulted for the OFFLINE bracket, whose shape is known before the game runs.
        // The live window's is not, so where the operator left a default the lane picks a live-appropriate one
        // and says which it used; an explicitly set value is obeyed as given.
        var indexSlack = config.IndexSlackExplicit
            ? config.IndexSlack
            : Math.Max(config.IndexSlack, LiveIndexSlackFloor);
        report["indexSlackExplicit"] = config.IndexSlackExplicit;
        report["indexSlackUsed"] = indexSlack;

        var plan = Sts2SpineProbeMath.PlanCanvasItemBracket(
            canvasIds,
            probeIds,
            indexSlack,
            config.CandidateCap);

        // A cap too shallow to reach the ~28-validator neighbourhood the meshes live in would produce a
        // confident, wrong "found nothing", and how many candidates a validator row costs is exactly what is
        // not knowable in advance — so an unchosen cap is raised to one that provably clears that offset.
        report["candidateCapExplicit"] = config.CandidateCapExplicit;
        if (!config.CandidateCapExplicit
            && plan.Viable
            && plan.ValidatorsCovered < Sts2SpineProbeMath.RecommendedValidatorDepth)
        {
            var raised = (int)Math.Clamp(plan.RecommendedCap, config.CandidateCap, LiveAutoCandidateCapCeiling);
            if (raised > config.CandidateCap)
            {
                report["candidateCapAutoRaisedFrom"] = config.CandidateCap;
                report["candidateCapAutoRaisedTo"] = raised;
                report["candidateCapAutoRaiseClamped"] = plan.RecommendedCap > LiveAutoCandidateCapCeiling;
                plan = Sts2SpineProbeMath.PlanCanvasItemBracket(
                    canvasIds,
                    probeIds,
                    indexSlack,
                    raised);
            }
        }

        report["plan"] = DescribeCanvasBracketPlan(plan);
        report["validatorWindow"] =
            $"({plan.CanvasValidatorHigh},{plan.Candidates.ValidatorHigh}]x[0,{plan.Candidates.IndexHigh}]";
        if (!plan.Viable)
        {
            report["failure"] = plan.Reason;
            report["candidatesTested"] = 0L;
            report["sweepMs"] = 0d;
            return new LiveAcquisition(report, [], [], false, "not-applicable", plan.Reason);
        }

        var rootViewport = TryGet(() => (Engine.GetMainLoop() as SceneTree)?.Root);
        report["frameYieldsAvailable"] = rootViewport is not null;
        if (rootViewport is null)
        {
            Log(logStream, "live acquisition has no root viewport to yield frames to; the sweep runs unbroken.");
        }

        // Suppression spans the WHOLE acquisition rather than each individual sweep: the anchor scan, every
        // stride ladder, every walk step and the dense fallback all ask the server for ids that mostly do not
        // exist, and each miss logs an error. Restored in the finally whatever happens, including a throw.
        var previousPrintErrors = TryGet(() => Engine.PrintErrorMessages, true);
        try
        {
            TryRun(() => Engine.PrintErrorMessages = false);

            // Bare await: this body is posted onto the game's captured SynchronizationContext, and every
            // RenderingServer call underneath must stay on it.
            return await AcquireLiveMeshRidsInnerAsync(
                config,
                report,
                plan,
                rootViewport,
                skeletonBounds,
                neighbourhood,
                targetOrdinal,
                meshNodes.Count,
                probeIds,
                indexSlack,
                logStream);
        }
        finally
        {
            TryRun(() => Engine.PrintErrorMessages = previousPrintErrors);
        }
    }

    private static async Task<LiveAcquisition> AcquireLiveMeshRidsInnerAsync(
        ProbeConfig config,
        Dictionary<string, object?> report,
        Sts2SpineProbeMath.CanvasBracketPlan plan,
        Viewport? rootViewport,
        IReadOnlyList<double>? skeletonBounds,
        IReadOnlyList<RigNeighbour> neighbourhood,
        int targetOrdinal,
        int expectedCount,
        ulong[] probeIds,
        int indexSlack,
        ILogStream logStream)
    {
        var strideSource = "default";
        var validatorStride = Sts2SpineGeometryMath.DefaultLiveValidatorStride;
        var indexStride = Sts2SpineGeometryMath.DefaultLiveIndexStride;
        if (Sts2SpineProbeMath.TryParseStrideOverride(config.Stride, out var envValidator, out var envIndex))
        {
            validatorStride = envValidator;
            indexStride = envIndex;
            strideSource = "env";
        }

        // The scan's step is planned from the DEFAULT stride on purpose, never from the override or the
        // eventual measurement: the step it yields (24 for a 26-child rig) is coprime to 1, 5, 7 and 11 alike,
        // so being wrong about the stride here costs nothing — while a step derived from the measured stride
        // could not be computed before the scan that measures it has run.
        var anchorCap = config.CandidateCapExplicit ? config.CandidateCap : LiveAnchorScanCandidateCeiling;
        var anchorPlan = Sts2SpineProbeMath.PlanAnchorScan(
            plan,
            expectedCount,
            Sts2SpineGeometryMath.DefaultLiveValidatorStride,
            (uint)Math.Max(0, config.AnchorStep),
            anchorCap);
        report["anchorScan"] = DescribeAnchorScanPlan(anchorPlan, config);

        var fullSweepReason = config.FullSweep
            ? $"{FullSweepEnv}=1 asked for the Phase-1 full-window sweep"
            : anchorPlan.Viable
                ? null
                : $"the anchor scan is not viable ({anchorPlan.Reason}), so the full window is swept instead";
        report["method"] = fullSweepReason is null ? "anchor-stride-walk" : "full-window-sweep";
        report["fullSweepReason"] = fullSweepReason;

        return fullSweepReason is null
            ? await AcquireByAnchorWalkAsync(
                config,
                report,
                plan,
                anchorPlan,
                rootViewport,
                skeletonBounds,
                neighbourhood,
                targetOrdinal,
                expectedCount,
                probeIds,
                validatorStride,
                indexStride,
                strideSource,
                indexSlack,
                logStream)
            : await AcquireByFullWindowSweepAsync(
                report, plan, rootViewport, skeletonBounds, expectedCount, probeIds, indexSlack, logStream);
    }

    // The Phase-1 path, kept verbatim so `SPIRECTL_SPINE_GEOMETRY_PROBE_FULL_SWEEP=1` reproduces that run's
    // 37 finds exactly: the whole canvas-item window, validator-major, validated by the LOOSE mesh test. It is
    // also what an unusable anchor plan falls back to, which is why the fallback is named in the report.
    private static async Task<LiveAcquisition> AcquireByFullWindowSweepAsync(
        Dictionary<string, object?> report,
        Sts2SpineProbeMath.CanvasBracketPlan plan,
        Viewport? rootViewport,
        IReadOnlyList<double>? skeletonBounds,
        int expectedCount,
        ulong[] probeIds,
        int indexSlack,
        ILogStream logStream)
    {
        // Announced BEFORE the sweep: it can take minutes, and if it ends badly the operator needs to know
        // what window it walked, not just that it failed.
        Log(
            logStream,
            $"live acquisition plan: canvasValidators=[{plan.CanvasValidatorLow},{plan.CanvasValidatorHigh}] "
            + $"probeValidator={plan.ProbeValidator} probeIndex={plan.ProbeIndex} indexSpan={plan.IndexSpan} "
            + $"nominalCandidates={plan.Candidates.TotalCandidates} cap={plan.Candidates.Cap} "
            + $"enumerated={plan.Candidates.EmittedCount} validatorsCovered={plan.ValidatorsCovered} "
            + $"(meshes are expected ~{Sts2SpineProbeMath.MeasuredMeshValidatorOffset} validators past "
            + "the canvas edge)");

        var outcome = await SweepCandidatesAsync(
            rootViewport,
            Sts2SpineGeometryMath.EnumerateRidCandidates(plan.Candidates),
            [.. probeIds]);

        report["candidatesTested"] = outcome.Probed;
        report["sweepMs"] = Math.Round(outcome.ElapsedMs, 3);
        report["sweptChunked"] = outcome.Chunked;
        report["foundCount"] = outcome.Validated.Count;
        report["expectedCount"] = expectedCount;
        report["countsMatch"] = outcome.Validated.Count == expectedCount;
        report["found"] = DescribeFoundMeshes(outcome.Validated, plan);
        var fullSweepRun = DescribeRun(outcome.Validated, skeletonBounds);
        report["run"] = fullSweepRun;
        report["runSpineLikeness"] = SummarizeSpineLikeness(fullSweepRun);
        report["trim"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["method"] = "not-applicable",
            ["agreed"] = false,
            ["detail"] = "the full-window sweep does not produce a stride run, so there is nothing to trim; "
                + "gate (a) falls back to the Phase-1 rule (found == mesh children)",
        };

        var widenHint = DescribeWidenHint(plan, indexSlack);
        if (outcome.Validated.Count == 0)
        {
            var failure = plan.Candidates.Truncated
                ? $"the sweep validated no mesh in the {plan.ValidatorsCovered} validator row(s) the cap allowed, "
                    + $"against a measured offset of ~{Sts2SpineProbeMath.MeasuredMeshValidatorOffset}; "
                    + widenHint
                : $"the sweep validated no mesh anywhere in the full canvas-item window; {widenHint}";
            report["failure"] = failure;
            return new LiveAcquisition(report, [], [], false, "not-applicable", failure);
        }

        if (outcome.Validated.Count != expectedCount)
        {
            report["shortfallHint"] = outcome.Validated.Count < expectedCount
                ? $"fewer meshes than mesh children: some sit outside the window — {widenHint}"
                : "more meshes than mesh children: the window also caught meshes belonging to something else, "
                    + "so the set is a superset of this skeleton's";
        }

        return new LiveAcquisition(report, outcome.Validated, [], false, "not-applicable", null);
    }

    // The measured lane: sample the validator axis coarsely until something spine-like answers, measure the
    // progression off it, walk it, and trim what the walk returned down to this rig. An accepted candidate is
    // only a HYPOTHESIS — the 256x256 quad Phase 1 tripped over passes every plausibility clause there is —
    // so an anchor is confirmed by whether a run can be walked off it, and a short run sends the scan back to
    // looking rather than declaring victory.
    private static async Task<LiveAcquisition> AcquireByAnchorWalkAsync(
        ProbeConfig config,
        Dictionary<string, object?> report,
        Sts2SpineProbeMath.CanvasBracketPlan plan,
        Sts2SpineProbeMath.AnchorScanPlan anchorPlan,
        Viewport? rootViewport,
        IReadOnlyList<double>? skeletonBounds,
        IReadOnlyList<RigNeighbour> neighbourhood,
        int targetOrdinal,
        int expectedCount,
        ulong[] probeIds,
        uint validatorStride,
        uint indexStride,
        string strideSource,
        int indexSlack,
        ILogStream logStream)
    {
        var minimumRun = Sts2SpineProbeMath.MinimumAnchorRunLength(expectedCount);
        var runLengthBudget = neighbourhood.Sum(rig => rig.MeshChildren) + 16;
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(
            plan.CanvasValidatorHigh + 1,
            plan.Candidates.ValidatorHigh > 0 ? plan.Candidates.ValidatorHigh - 1 : 0,
            plan.Candidates.IndexLow,
            plan.Candidates.IndexHigh,
            Math.Max(expectedCount, runLengthBudget),
            config.WalkMisses);

        Log(
            logStream,
            $"live acquisition method=anchor-stride-walk window={report.GetValueOrDefault("validatorWindow")} "
            + $"step={anchorPlan.ValidatorStep} sampledValidators={anchorPlan.SampledValidators} "
            + $"candidates={anchorPlan.TotalCandidates} (full window {anchorPlan.FullWindowCandidates}) "
            + $"expecting a +{validatorStride}/+{indexStride} run, minimumRun={minimumRun} "
            + $"maxRun={limits.MaxRunLength} missBudget={limits.MaxConsecutiveMisses}");

        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var exclude = new HashSet<ulong>(probeIds);
        var meshesInspected = 0;

        bool Validate(ulong candidate)
        {
            if (exclude.Contains(candidate))
            {
                return false;
            }

            if (TryValidateSpineLikeMeshRid(candidate, skeletonBounds, out var reason))
            {
                meshesInspected += 1;
                return true;
            }

            // "not-a-mesh" is the id space itself answering, not a rejection worth counting: it is true of
            // essentially every candidate and would drown the histogram that matters.
            if (!string.Equals(reason, "not-a-mesh", StringComparison.Ordinal))
            {
                meshesInspected += 1;
                rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
            }

            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var anchorHits = new List<ulong>();
        var deadAnchors = new List<Dictionary<string, object?>>();
        Sts2SpineGeometryMath.StrideChoice? measured = null;
        Sts2SpineGeometryMath.StrideWalk? acceptedWalk = null;
        ulong acceptedAnchor = 0;
        long scanProbed = 0;
        long walkProbed = 0;
        long ladderProbed = 0;
        var chunked = false;

        foreach (var candidate in Sts2SpineProbeMath.EnumerateAnchorScanCandidates(anchorPlan))
        {
            scanProbed += 1;
            if (Validate(candidate))
            {
                anchorHits.Add(candidate);
                var parts = Sts2SpineGeometryMath.DecodeRid(candidate);
                var anchor = new Sts2SpineGeometryMath.RidPoint(parts.Validator, parts.Index);

                // The stride is measured once, off the FIRST anchor: a later anchor is a member of the same
                // progression or is not a member of anything, and re-measuring per candidate would cost a
                // ladder per false positive.
                if (measured is null && !string.Equals(strideSource, "env", StringComparison.Ordinal))
                {
                    measured = MeasureStrideLadder(anchor, Validate, limits, validatorStride, out var probes);
                    ladderProbed += probes;
                    if (string.Equals(measured.Source, "measured", StringComparison.Ordinal))
                    {
                        validatorStride = measured.ValidatorStride;
                        indexStride = measured.IndexStride;
                        strideSource = "measured";
                    }
                }

                var walk = Sts2SpineGeometryMath.WalkStride(
                    anchor, validatorStride, indexStride, Validate, limits);
                walkProbed += walk.Probed;
                if (walk.Found.Count >= minimumRun)
                {
                    acceptedWalk = walk;
                    acceptedAnchor = candidate;
                    Log(
                        logStream,
                        $"live anchor accepted validator={parts.Validator} index={parts.Index} "
                        + $"run={walk.Found.Count} stride={validatorStride}/{indexStride} ({strideSource}) "
                        + $"stopUp={walk.StopReasonUp} stopDown={walk.StopReasonDown} "
                        + $"gaps=[{string.Join(",", walk.GapSizes)}]");
                    break;
                }

                deadAnchors.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ridId"] = candidate.ToString(CultureInfo.InvariantCulture),
                    ["rid"] = DescribeRidParts(candidate),
                    ["runLength"] = walk.Found.Count,
                    ["minimumRunLength"] = minimumRun,
                    ["stopReasonUp"] = walk.StopReasonUp,
                    ["stopReasonDown"] = walk.StopReasonDown,
                    ["verdict"] = "a spine-like surface that no stride run could be walked off — a false "
                        + "positive of the plausibility test, not a member",
                });
            }

            // Bare await: the sweep is on the main thread and the game must keep drawing between chunks.
            if (scanProbed % CandidateChunkSize == 0 && rootViewport is not null)
            {
                chunked = true;
                await AwaitFramesAsync(rootViewport, 1, forceDraw: false);
            }
        }

        // The dense fallback: every anchor the scan found turned out to be a false positive, so the coarse
        // sampling is now suspect too (a run whose members are further apart than the step's coprimality
        // assumes would look exactly like this). Re-walk the neighbourhood of each dead anchor at full
        // density, and union whatever that turns up with the anchors themselves.
        var densePass = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ran"] = false };
        report["densePass"] = densePass;
        var hypotheses = anchorHits.Count;
        long denseProbedTotal = 0;
        if (acceptedWalk is null && deadAnchors.Count > 0)
        {
            densePass["ran"] = true;
            densePass["reason"] = "every anchor the coarse scan found failed to walk a run of at least "
                + $"{minimumRun}";
            var halfWidth = (long)Math.Max(1, expectedCount) * Math.Max(1u, validatorStride) * LiveDensePassRunWidths;
            var windows = new List<Dictionary<string, object?>>();
            var dense = new List<ulong>();
            long denseProbed = 0;
            var denseStopwatch = Stopwatch.StartNew();

            foreach (var anchorId in anchorHits.Take(LiveDensePassAnchorCap))
            {
                var parts = Sts2SpineGeometryMath.DecodeRid(anchorId);
                var low = (uint)Math.Max(plan.Candidates.ValidatorLow, (long)parts.Validator - halfWidth);
                var high = (uint)Math.Min(plan.Candidates.ValidatorHigh, (long)parts.Validator + halfWidth);
                var outcome = await SweepCandidatesAsync(
                    rootViewport,
                    EnumerateDenseWindow(low, high, plan.Candidates.IndexLow, plan.Candidates.IndexHigh),
                    exclude,
                    Validate);
                denseProbed += outcome.Probed;
                dense.AddRange(outcome.Validated);
                windows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["anchorRidId"] = anchorId.ToString(CultureInfo.InvariantCulture),
                    ["validatorLow"] = low,
                    ["validatorHigh"] = high,
                    ["probed"] = outcome.Probed,
                    ["found"] = outcome.Validated.Count,
                });
            }

            denseStopwatch.Stop();
            denseProbedTotal = denseProbed;
            densePass["windows"] = windows.ToArray();
            densePass["candidatesTested"] = denseProbed;
            densePass["found"] = dense.Count;
            densePass["ms"] = Math.Round(denseStopwatch.Elapsed.TotalMilliseconds, 3);

            // A dense find may be the member the coarse scan missed, so each gets one walk before the pass
            // gives up and reports the raw union.
            foreach (var candidate in dense)
            {
                var parts = Sts2SpineGeometryMath.DecodeRid(candidate);
                var walk = Sts2SpineGeometryMath.WalkStride(
                    new Sts2SpineGeometryMath.RidPoint(parts.Validator, parts.Index),
                    validatorStride,
                    indexStride,
                    Validate,
                    limits);
                walkProbed += walk.Probed;
                if (walk.Found.Count >= minimumRun)
                {
                    acceptedWalk = walk;
                    acceptedAnchor = candidate;
                    densePass["walkedRunFrom"] = candidate.ToString(CultureInfo.InvariantCulture);
                    break;
                }
            }

            if (acceptedWalk is null)
            {
                anchorHits = [.. anchorHits.Concat(dense).Distinct().Order()];
                densePass["union"] = anchorHits.Count;
            }
        }

        stopwatch.Stop();

        var found = acceptedWalk is not null
            ? new List<ulong>(acceptedWalk.Found)
            : [.. anchorHits.Distinct().Order()];

        report["candidatesTested"] = scanProbed + walkProbed + ladderProbed + denseProbedTotal;
        report["anchorCandidatesTested"] = scanProbed;
        report["walkCandidatesTested"] = walkProbed;
        report["strideLadderCandidatesTested"] = ladderProbed;
        report["denseCandidatesTested"] = denseProbedTotal;
        report["sweepMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
        report["sweptChunked"] = chunked;
        report["meshesInspected"] = meshesInspected;
        report["rejectedByReason"] = rejections;
        report["anchors"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hypotheses"] = hypotheses,
            ["accepted"] = acceptedAnchor == 0 ? null : DescribeRidParts(acceptedAnchor),
            ["acceptedRidId"] = acceptedAnchor == 0 ? null : acceptedAnchor.ToString(CultureInfo.InvariantCulture),
            ["dead"] = deadAnchors.ToArray(),
            ["minimumRunLength"] = minimumRun,
        };
        report["stride"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["validator"] = validatorStride,
            ["index"] = indexStride,
            ["source"] = strideSource,
            ["confirmedDeltas"] = measured?.ConfirmedDeltas.ToArray(),
            ["doubleConfirmedDeltas"] = measured?.DoubleConfirmedDeltas.ToArray(),
            ["ladderProbed"] = ladderProbed,
        };
        report["walk"] = acceptedWalk is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = acceptedWalk.Found.Count,
                ["probed"] = acceptedWalk.Probed,
                ["stopReasonUp"] = acceptedWalk.StopReasonUp,
                ["stopReasonDown"] = acceptedWalk.StopReasonDown,
                ["gapSizes"] = acceptedWalk.GapSizes.ToArray(),
                ["limits"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["validatorFloor"] = limits.ValidatorFloor,
                    ["validatorCeiling"] = limits.ValidatorCeiling,
                    ["indexLow"] = limits.IndexLow,
                    ["indexHigh"] = limits.IndexHigh,
                    ["maxRunLength"] = limits.MaxRunLength,
                    ["maxConsecutiveMisses"] = limits.MaxConsecutiveMisses,
                },
            };
        report["foundCount"] = found.Count;
        report["expectedCount"] = expectedCount;
        report["countsMatch"] = found.Count == expectedCount;
        report["found"] = DescribeFoundMeshes(found, plan);
        var describedRun = DescribeRun(found, skeletonBounds);
        report["run"] = describedRun;
        report["runSpineLikeness"] = SummarizeSpineLikeness(describedRun);

        if (found.Count == 0)
        {
            var failure = "the anchor scan found no spine-like surface anywhere in the sampled window; "
                + $"retry with {FullSweepEnv}=1 to sweep the whole window at full density, or widen it — "
                + DescribeWidenHint(plan, indexSlack);
            report["failure"] = failure;
            return new LiveAcquisition(report, [], [], false, "not-applicable", failure);
        }

        // The TRIM. Two independent signals, both computed, agreement required. Reported whichever way it
        // goes, next to the untrimmed run and the rig neighbourhood, so a disagreement is a question somebody
        // can answer offline rather than a run that has to be repeated.
        var runBboxes = found
            .Select(id => (IReadOnlyList<double>)(TryReadSurface(id) is { } read ? DescribeBbox(read) : [0d, 0d, 0d, 0d]))
            .ToArray();
        var trim = Sts2SpineProbeMath.TrimRunToTarget(
            found,
            runBboxes,
            [.. neighbourhood.Select(rig => rig.MeshChildren)],
            targetOrdinal,
            skeletonBounds ?? []);
        report["trim"] = DescribeTrim(trim);
        Log(
            logStream,
            $"live acquisition trim method={trim.Method} agreed={trim.Agreed} offset={trim.Offset?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} "
            + $"window={trim.WindowLength} buildOrder=[{string.Join(",", trim.BuildOrderOffsets)}] "
            + $"bounds=[{string.Join(",", trim.Bounds.BestOffsets)}] run={found.Count} — {trim.Detail}");

        if (found.Count != expectedCount)
        {
            report["shortfallHint"] = found.Count < expectedCount
                ? "the walked run is shorter than the sprite's mesh-child count: it stopped at "
                    + $"{acceptedWalk?.StopReasonDown ?? "n/a"} going down and {acceptedWalk?.StopReasonUp ?? "n/a"} "
                    + "going up"
                : "the walked run is longer than the sprite's mesh-child count, which is EXPECTED on a live "
                    + "menu: neighbouring rigs' meshes were minted in the same progression. See trim.";
        }

        return new LiveAcquisition(
            report,
            found,
            [.. trim.Trimmed],
            trim.Agreed,
            $"{trim.Method} agreed={trim.Agreed} buildOrder=[{string.Join(",", trim.BuildOrderOffsets)}] "
                + $"bounds=[{string.Join(",", trim.Bounds.BestOffsets)}]",
            null);
    }

    // The stride ladder: is there a delta d such that (anchor + d validators, +1 index) AND (anchor + 2d, +2)
    // both validate? Two points are collinear by definition, so only the three-point progression is evidence.
    private static Sts2SpineGeometryMath.StrideChoice MeasureStrideLadder(
        Sts2SpineGeometryMath.RidPoint anchor,
        Func<ulong, bool> validate,
        Sts2SpineGeometryMath.StrideWalkLimits limits,
        uint fallback,
        out long probed)
    {
        var singles = new List<uint>();
        var doubles = new List<uint>();
        probed = 0;

        for (uint delta = 1; delta <= LiveStrideLadderMaxDelta; delta += 1)
        {
            if (limits.ValidatorCeiling >= delta
                && anchor.Validator <= limits.ValidatorCeiling - delta
                && anchor.Index < limits.IndexHigh)
            {
                probed += 1;
                if (validate(Sts2SpineGeometryMath.ComposeRid(anchor.Validator + delta, anchor.Index + 1)))
                {
                    singles.Add(delta);
                }
            }

            if (limits.ValidatorCeiling >= 2 * delta
                && anchor.Validator <= limits.ValidatorCeiling - (2 * delta)
                && anchor.Index + 1 < limits.IndexHigh)
            {
                probed += 1;
                if (validate(Sts2SpineGeometryMath.ComposeRid(anchor.Validator + (2 * delta), anchor.Index + 2)))
                {
                    doubles.Add(delta);
                }
            }
        }

        return Sts2SpineGeometryMath.ChooseValidatorStride(singles, doubles, fallback);
    }

    private static IEnumerable<ulong> EnumerateDenseWindow(
        uint validatorLow,
        uint validatorHigh,
        uint indexLow,
        uint indexHigh)
    {
        for (var validator = validatorLow; validator <= validatorHigh; validator += 1)
        {
            for (var index = indexLow; index <= indexHigh; index += 1)
            {
                yield return Sts2SpineGeometryMath.ComposeRid(validator, index);
                if (index == uint.MaxValue)
                {
                    break;
                }
            }

            if (validator == uint.MaxValue)
            {
                yield break;
            }
        }
    }

    private static string DescribeWidenHint(Sts2SpineProbeMath.CanvasBracketPlan plan, int indexSlack)
        => $"widen the validator axis with {CandidateCapEnv} (>= {plan.RecommendedCap} covers "
            + $"{Sts2SpineProbeMath.RecommendedValidatorDepth} rows) or the index axis with "
            + $"{IndexSlackEnv} (used {indexSlack}, ceiling {plan.Candidates.IndexHigh})";

    private static Dictionary<string, object?>[] DescribeFoundMeshes(
        IReadOnlyList<ulong> found,
        Sts2SpineProbeMath.CanvasBracketPlan plan)
        =>
        [
            .. found.Select(id => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ridId"] = id.ToString(CultureInfo.InvariantCulture),
                ["rid"] = DescribeRidParts(id),
                ["validatorOffsetFromCanvasHigh"] =
                    Sts2SpineGeometryMath.DecodeRid(id).Validator - plan.CanvasValidatorHigh,
                ["vertexCount"] = TryReadSurface(id)?.X.Length ?? 0,
            }),
        ];

    // The untrimmed run, per mesh, in enough detail that next round can re-derive the trim offline without
    // another game session: which lattice point, how big the mesh is, where it sits, and what it addresses.
    // The spine-likeness verdict rides along on BOTH acquisition paths — the anchor lane already used it to
    // decide, and the full-window lane (which validates with the loose Phase-1 test, deliberately) gets to
    // show what the strict test WOULD have said about each of its finds, which is where the contaminants of a
    // full sweep name themselves.
    private static Dictionary<string, object?> DescribeRunMesh(ulong id, IReadOnlyList<double>? skeletonBounds)
    {
        var parts = Sts2SpineGeometryMath.DecodeRid(id);
        var read = TryReadSurface(id);
        var verdict = read is null
            ? new Sts2SpineProbeMath.SpineLikeVerdict(false, "unreadable")
            : Sts2SpineProbeMath.IsSpineLikeSurface(
                DescribeSurfaceFacts(read.SurfaceCount, read), skeletonBounds);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ridId"] = id.ToString(CultureInfo.InvariantCulture),
            ["validator"] = parts.Validator,
            ["index"] = parts.Index,
            ["vertexCount"] = read?.X.Length ?? 0,
            ["vertexVariantType"] = read?.VertexVariantType,
            ["indexCount"] = read?.Indices.Length ?? 0,
            ["bbox"] = read is null ? null : DescribeBbox(read),
            ["uvMin"] = read is { U.Length: > 0 }
                ? new[] { Math.Round(read.U.Min(), 6), Math.Round(read.V.Min(), 6) }
                : null,
            ["uvMax"] = read is { U.Length: > 0 }
                ? new[] { Math.Round(read.U.Max(), 6), Math.Round(read.V.Max(), 6) }
                : null,
            ["spineLike"] = verdict.Accepted,
            ["spineLikeReason"] = verdict.Reason,
        };
    }

    private static Dictionary<string, object?>[] DescribeRun(
        IReadOnlyList<ulong> run,
        IReadOnlyList<double>? skeletonBounds)
        => [.. run.Select(id => DescribeRunMesh(id, skeletonBounds))];

    // How the STRICT test classifies the found set, whichever lane found it. On a full-window sweep this is
    // the count that says the contaminants were seen and named.
    private static Dictionary<string, int> SummarizeSpineLikeness(IReadOnlyList<Dictionary<string, object?>> run)
    {
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mesh in run)
        {
            var reason = mesh.GetValueOrDefault("spineLikeReason") as string ?? "unknown";
            histogram[reason] = histogram.GetValueOrDefault(reason) + 1;
        }

        return histogram;
    }

    private static Dictionary<string, object?> DescribeAnchorScanPlan(
        Sts2SpineProbeMath.AnchorScanPlan plan,
        ProbeConfig config)
        => new(StringComparer.Ordinal)
        {
            ["viable"] = plan.Viable,
            ["reason"] = plan.Reason,
            ["validatorLow"] = plan.ValidatorLow,
            ["validatorHigh"] = plan.ValidatorHigh,
            ["validatorStep"] = plan.ValidatorStep,
            ["validatorStepSource"] = config.AnchorStep > 0 ? "env" : "coprime-to-stride",
            ["planningStride"] = Sts2SpineGeometryMath.DefaultLiveValidatorStride,
            ["indexLow"] = plan.IndexLow,
            ["indexHigh"] = plan.IndexHigh,
            ["sampledValidators"] = plan.SampledValidators,
            ["totalCandidates"] = plan.TotalCandidates,
            ["fullWindowCandidates"] = plan.FullWindowCandidates,
            ["savingVsFullWindow"] = plan.FullWindowCandidates > 0
                ? Math.Round(1d - ((double)plan.TotalCandidates / plan.FullWindowCandidates), 4)
                : (double?)null,
            ["cap"] = plan.Cap,
            ["truncated"] = plan.Truncated,
            ["enumerated"] = plan.EmittedCount,
        };

    private static Dictionary<string, object?> DescribeTrim(Sts2SpineProbeMath.TrimVerdict trim)
        => new(StringComparer.Ordinal)
        {
            ["method"] = trim.Method,
            ["agreed"] = trim.Agreed,
            ["offset"] = trim.Offset,
            ["windowLength"] = trim.WindowLength,
            ["buildOrderOffsets"] = trim.BuildOrderOffsets.ToArray(),
            ["boundsOffset"] = trim.Bounds.Offset,
            ["boundsTied"] = trim.Bounds.Tied,
            ["boundsBestOffsets"] = trim.Bounds.BestOffsets.ToArray(),
            ["boundsScores"] = trim.Bounds.Scores.Select(score => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["offset"] = score.Offset,
                ["l1"] = Math.Round(score.L1, 4),
                ["strictlyOutside"] = score.StrictlyOutside,
                ["unionBbox"] = score.UnionBbox.Select(value => Math.Round(value, 4)).ToArray(),
            }).ToArray(),
            ["trimmedCount"] = trim.Trimmed.Count,
            ["trimmed"] = trim.Trimmed.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            ["detail"] = trim.Detail,
        };

    // ── Gate bookkeeping ─────────────────────────────────────────────────────────────────────────────

    private static readonly (string Id, string Name)[] LiveGates =
    [
        ("a", "acquisition"),
        ("b", "readback-sanity"),
        ("c", "liveness"),
        ("d", "timing"),
    ];

    private static void RecordGate(
        Dictionary<string, object?> gates,
        ILogStream logStream,
        string id,
        string name,
        string status,
        string detail)
    {
        gates[id] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gate"] = id,
            ["name"] = name,
            ["status"] = status,
            ["detail"] = detail,
        };

        Log(logStream, $"live gate={id} {name} {status.ToUpperInvariant()} {detail}");
    }

    // Anything not yet recorded is recorded as SKIPPED with the reason. A gate that simply never appears
    // cannot be told apart from a battery that died on its way there.
    private static void SkipRemainingGates(Dictionary<string, object?> gates, ILogStream logStream, string reason)
    {
        foreach (var (id, name) in LiveGates)
        {
            if (!gates.ContainsKey(id))
            {
                RecordGate(gates, logStream, id, name, "skipped", reason);
            }
        }
    }

    private static string Join(object? value)
        => value switch
        {
            null => "n/a",
            double[] numbers => "[" + string.Join(",", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture))) + "]",
            _ => value.ToString() ?? "n/a",
        };

    private static async Task<TimeSpan> AwaitWallClockAsync(Node anchor, TimeSpan target)
    {
        var tree = anchor.GetTree();
        var started = Stopwatch.StartNew();
        if (tree is null)
        {
            await Task.Delay(target);
            return started.Elapsed;
        }

        for (var frame = 0; frame < 240 && started.Elapsed < target; frame += 1)
        {
            await anchor.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        return started.Elapsed;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────────────

    // Deliberately NOT the extraction path's AwaitRenderWarmupFramesAsync: that helper always closes a wait
    // with RenderingServer.ForceDraw, and whether a forced draw is REQUIRED for the readback to be current is
    // one of the questions this probe exists to answer, so the probe needs the no-draw variant too.
    private static async Task AwaitFramesAsync(Viewport rootViewport, int frames, bool forceDraw)
    {
        var tree = rootViewport.GetTree();
        for (var frame = 0; frame < frames; frame += 1)
        {
            if (tree is not null)
            {
                await rootViewport.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            if (forceDraw)
            {
                TryRun(() => RenderingServer.ForceDraw());
            }
        }
    }

    private static List<Node> CollectMeshNodes(Node spineNode)
    {
        var found = new List<Node>();
        try
        {
            foreach (var child in spineNode.GetChildren())
            {
                if (child is Node node && node.GetClass() == "SpineMesh2D")
                {
                    found.Add(node);
                }
            }
        }
        catch
        {
            // A partially-built skeleton can race the walk; an empty/short list is the reported result.
        }

        return found;
    }

    private static IEnumerable<Node> EnumerateTree(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            List<Node> children;
            try
            {
                children = [.. node.GetChildren().OfType<Node>()];
            }
            catch
            {
                continue;
            }

            for (var i = children.Count - 1; i >= 0; i -= 1)
            {
                stack.Push(children[i]);
            }
        }
    }

    private static int CountSlots(GodotObject? skeletonObject)
    {
        if (skeletonObject is null)
        {
            return 0;
        }

        var slots = TryGet(() => skeletonObject.Call("get_slots").AsGodotArray());
        return slots?.Count ?? 0;
    }

    // `Godot.Rid` wraps exactly one ulong and exposes no public constructor from a raw id, so the probe
    // reinterprets the bits. The round trip is asserted (and reported) rather than assumed; a layout change
    // would otherwise silently turn every RID step into noise.
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

    private static Dictionary<string, object?> DescribeRidConstruction()
    {
        const ulong probe = 0x1234_5678_9ABC_DEF0uL;
        var ok = TryComposeRid(probe, out var rid);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["roundTrips"] = ok && rid.Id == probe,
            ["probeId"] = probe.ToString(CultureInfo.InvariantCulture),
            ["observedId"] = rid.Id.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static Dictionary<string, object?> DescribeRidParts(ulong id)
    {
        var parts = Sts2SpineGeometryMath.DecodeRid(id);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["validator"] = parts.Validator,
            ["index"] = parts.Index,
        };
    }

    private static Dictionary<string, object?> NewReport(string mode, ProbeConfig config)
        => new(StringComparer.Ordinal)
        {
            ["schema"] = ProbeVersion,
            ["probeVersion"] = ProbeVersion,
            ["mode"] = mode,
            ["capturedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["env"] = EchoConfig(config),
        };

    // Full geometry is written for the first scene that produces any, and only that one.
    private static int _fullVertexSlotTaken;

    private static bool TryClaimFullVertexSlot()
        => Interlocked.CompareExchange(ref _fullVertexSlotTaken, 1, 0) == 0;

    private static void WriteJson(
        string dir,
        string fileName,
        object payload,
        JsonSerializerOptions options,
        ILogStream logStream)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(payload, options));
        }
        catch (Exception ex)
        {
            Log(logStream, $"could not write '{fileName}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
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
            // A probe must never take the host down; the missing datum is the reported result.
        }
    }

    private static void Log(ILogStream logStream, string message)
    {
        try
        {
            GD.Print($"{LogPrefix} {message}");
        }
        catch
        {
            // Printing is best-effort.
        }

        try
        {
            logStream.Write(BridgeLogLevel.Info, LogTarget, message);
        }
        catch
        {
            // Ditto.
        }
    }
}
