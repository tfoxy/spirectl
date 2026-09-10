using System.Diagnostics;
using System.Text;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Godot-free PHASE breakdown for one asset render (a combat-background PNG, a Spine clip/still), recorded by the
/// live render lanes and drained by whoever asked for the asset. It remains reusable, Godot-free code so the
/// accumulation, phase vocabulary, and fold are
/// unit-testable without the game assemblies; the Godot-typed lanes that stamp the phases
/// (<c>Sts2AssetExtractProvider.RenderAssets</c>) stay in the live-host glob.
/// <para>
/// WHY: an asset render is marshalled onto the Godot MAIN thread, so its synchronous segments — resource load,
/// GPU readback, the full-image alpha scan, the PNG encode — are a stall the person actually playing sees, while
/// its <c>await ProcessFrame</c> segments hand the frame back to the game and cost only the requesting client
/// some latency. A single wall-clock number for the whole render (what the embedder could measure before this)
/// cannot tell those two apart, so it cannot say whether a render is slow because the encoder is slow or merely
/// because it waited five frames. Every phase therefore carries <see cref="PhaseCost.Blocking"/>, and the
/// snapshot folds the two totals separately.
/// </para>
/// <para>
/// Report-only. Nothing here compares a number against a budget, and no lane changes behaviour based on what it
/// recorded — same doctrine as the producer-walk profiler.
/// </para>
/// </summary>
public static class Sts2RenderPhaseProfile
{
    /// <summary>
    /// Opt-OUT switch (<c>=0</c> disables recording). Default ON: an asset render is a seconds-scale, handful-of-
    /// times-per-session operation, so ~30 Stopwatch reads are free, and these numbers are only useful if they
    /// are already there when someone finally hits a slow room. Same reasoning as couch-coop's always-on
    /// static-background instrument.
    /// </summary>
    public const string ProfileEnvVar = "SPIRECTL_RENDER_PHASE_PROFILE";

    /// <summary>
    /// Completed snapshots retained for an embedder that has not drained them yet. A render's consumer takes its
    /// own snapshot immediately (<see cref="TryTake"/>), so this only has to survive the gap between
    /// <see cref="Complete"/> and that call; the cap keeps a consumer that never drains from leaking.
    /// </summary>
    public const int MaxRetainedSnapshots = 32;

    /// <summary>
    /// The phase vocabulary. Stable strings: an embedder's perf report keys on them, so renaming one silently
    /// re-labels a metric in a cross-round comparison. Grouped by lane, ordered as the render performs them
    /// (<see cref="PhaseOrder"/>).
    /// </summary>
    public static class Phase
    {
        // --- shared: getting onto the main thread -------------------------------------------------------
        /// <summary>Queued from the request thread until the Godot main loop picked the extraction up.</summary>
        public const string DispatchWait = "dispatchWait";

        // --- shared: building the node to render --------------------------------------------------------
        public const string SceneLoad = "sceneLoad";
        public const string SceneInstantiate = "sceneInstantiate";

        // --- combat-background lane ---------------------------------------------------------------------
        public const string LayerDiscover = "layerDiscover";
        public const string LayerLoad = "layerLoad";
        public const string LayerCompose = "layerCompose";
        public const string LayerStabilize = "layerStabilize";
        public const string LayerPrepare = "layerPrepare";
        public const string FrameResolve = "frameResolve";

        // --- spine lane ---------------------------------------------------------------------------------
        /// <summary>Lane 0's factory: scene load + node find + skeleton/atlas realization.</summary>
        public const string LaneBuild = "laneBuild";

        /// <summary>Extra side-by-side render lanes (clones), built before the still-vs-animate decision.</summary>
        public const string LaneExtra = "laneExtra";

        public const string LanePrepare = "lanePrepare";

        /// <summary>Frame(s) waited so a seeked track time rebuilds its meshes before the bounds read.</summary>
        public const string StillPoseWait = "stillPoseWait";

        public const string BoundsRead = "boundsRead";

        /// <summary>The per-frame bounds pre-pass an ANIMATED clip runs to size its cell.</summary>
        public const string UnionBounds = "unionBounds";

        /// <summary>The frame waited so a SubViewport resize lands on the render target.</summary>
        public const string ResizeWait = "resizeWait";

        /// <summary>Frame waited per capture batch.</summary>
        public const string BatchWait = "batchWait";

        /// <summary>Slicing one lane's cell out of the composite readback.</summary>
        public const string SliceRegion = "sliceRegion";

        /// <summary>Main thread parked on the background frame encoders finishing.</summary>
        public const string EncodeWait = "encodeWait";

        /// <summary>Per-frame crop + encode, on a WORKER thread (never blocks the game).</summary>
        public const string EncodeFrame = "encodeFrame";

        // --- shared: capture ----------------------------------------------------------------------------
        public const string ViewportAttach = "viewportAttach";

        /// <summary>Parked on <c>SceneTree.ProcessFrame</c> — the game runs during this.</summary>
        public const string WarmupWait = "warmupWait";

        /// <summary>The <c>ForceDraw</c> that closes each warmup frame.</summary>
        public const string WarmupDraw = "warmupDraw";

        public const string ForceDraw = "forceDraw";

        /// <summary>Pulling the rendered texture back off the GPU.</summary>
        public const string Readback = "readback";

        /// <summary>The full-image alpha scan that rejects an empty render.</summary>
        public const string VisibleScan = "visibleScan";

        public const string Trim = "trim";

        // --- shared: encode -----------------------------------------------------------------------------
        /// <summary>Format normalization before handing the image to the encoder.</summary>
        public const string EncodeNormalize = "encodeNormalize";

        /// <summary>The encoder itself, on the MAIN thread (the single-image lanes).</summary>
        public const string EncodeSave = "encodeSave";

        // --- the offline SPINE GEOCLIP bake (Sts2SpineGeoClipBaker) -------------------------------------
        //
        // Its own `bake`-prefixed names rather than the render lanes'. A bake is a different SHAPE of work —
        // it owns the main thread for seconds and yields dozens of frames — so folding its ForceDraws into a
        // background render's `forceDraw` would make both numbers uncomparable across rounds. `SceneLoad` and
        // `LanePrepare` ARE reused, because a bake performs literally the same two steps.
        //
        // The names are FINE-GRAINED on purpose: no coarse `bakeAssociate`-style wrapper spans them, because a
        // coarse span over fine ones double-counts into both folds and drives Snapshot.UnattributedMs — the
        // instrument's own honesty check — to zero.

        /// <summary>Parked: the settle frames awaited once the rig is mounted in its off-screen viewport.</summary>
        public const string BakeWarmupWait = "bakeWarmupWait";

        /// <summary>Parked: a frame awaited so a written track time rebuilds the skeleton's meshes (PASS 1 / PASS 3).</summary>
        public const string BakePoseWait = "bakePoseWait";

        /// <summary>Parked: the frames that settle the ACQUISITION pose before the RID bracket is closed.</summary>
        public const string BakeAcquireWait = "bakeAcquireWait";

        /// <summary>Parked: the frame the dense sweep hands back to the game once per candidate chunk.</summary>
        public const string BakeSweepWait = "bakeSweepWait";

        /// <summary>Parked: the frames awaited after seeking the rig to a probe GROUP's stop.</summary>
        public const string BakeProbeSeekWait = "bakeProbeSeekWait";

        /// <summary>Parked: one frame per probe FRAME, so the nudged slot colours reach the meshes.</summary>
        public const string BakeProbeFrameWait = "bakeProbeFrameWait";

        /// <summary>Parked: the frame awaited after a probe group's own colours are put back.</summary>
        public const string BakeProbeRestoreWait = "bakeProbeRestoreWait";

        /// <summary>Blocking: the <c>ForceDraw</c> that closes each awaited bake frame.</summary>
        public const string BakeForceDraw = "bakeForceDraw";

        /// <summary>Blocking: reading and parsing the imported atlas document off disk.</summary>
        public const string BakeAtlasRead = "bakeAtlasRead";

        /// <summary>Blocking: rendering-server validation of bracketed RID candidates (walk arm + dense tiers).</summary>
        public const string BakeSweepProbe = "bakeSweepProbe";

        /// <summary>Blocking: the association probe's colour-only mesh readbacks.</summary>
        public const string BakeColorRead = "bakeColorRead";

        /// <summary>Blocking: reading one sampled frame's geometry off every associated mesh.</summary>
        public const string BakeGeometryRead = "bakeGeometryRead";

        /// <summary>Blocking: decoding the rig's atlas pages, once for the whole rig.</summary>
        public const string BakePageCollect = "bakePageCollect";

        /// <summary>Blocking: writing the page bytes into one pose's artifact directory.</summary>
        public const string BakePageWrite = "bakePageWrite";

        /// <summary>Blocking: assembling and serializing one pose's manifest.</summary>
        public const string BakeManifest = "bakeManifest";
    }

    /// <summary>
    /// Canonical report order (render order, not alphabetical): a phase table is read top-to-bottom as the thing
    /// the render actually did. Phases absent from a given lane are simply missing from its snapshot.
    /// </summary>
    public static readonly IReadOnlyList<string> PhaseOrder =
    [
        Phase.DispatchWait,
        Phase.SceneLoad,
        Phase.SceneInstantiate,
        Phase.LayerDiscover,
        Phase.LayerLoad,
        Phase.LayerCompose,
        Phase.LayerStabilize,
        Phase.LayerPrepare,
        Phase.FrameResolve,
        Phase.LaneBuild,
        Phase.LaneExtra,
        Phase.LanePrepare,
        Phase.ViewportAttach,
        Phase.WarmupWait,
        Phase.WarmupDraw,
        Phase.StillPoseWait,
        Phase.BoundsRead,
        Phase.UnionBounds,
        Phase.ResizeWait,
        Phase.BatchWait,
        Phase.ForceDraw,
        Phase.Readback,
        Phase.SliceRegion,
        Phase.VisibleScan,
        Phase.Trim,
        Phase.EncodeNormalize,
        Phase.EncodeSave,
        Phase.EncodeWait,
        Phase.EncodeFrame,

        // The geoclip bake, in the order the bake performs them. Appended rather than interleaved with the
        // render lanes above: these names are stable report keys, and moving an existing one would silently
        // re-rank a column in a cross-round comparison.
        Phase.BakeWarmupWait,
        Phase.BakeForceDraw,
        Phase.BakeAtlasRead,
        Phase.BakePoseWait,
        Phase.BakeAcquireWait,
        Phase.BakeSweepWait,
        Phase.BakeSweepProbe,
        Phase.BakeProbeSeekWait,
        Phase.BakeProbeFrameWait,
        Phase.BakeProbeRestoreWait,
        Phase.BakeColorRead,
        Phase.BakePageCollect,
        Phase.BakeGeometryRead,
        Phase.BakePageWrite,
        Phase.BakeManifest,
    ];

    /// <summary>
    /// The scalar facts a phase duration alone cannot explain: how many layer scenes were loaded, how many render
    /// lanes were built vs retired, how many frames the render parked on, how big the output was. Same stability
    /// contract as the phase names.
    /// </summary>
    public static class Counter
    {
        public const string LayerLoads = "layerLoads";
        public const string LanesPrepared = "lanesPrepared";
        public const string LanesRetired = "lanesRetired";
        public const string FramesWaited = "framesWaited";
        public const string Frames = "frames";
        public const string OutputBytes = "outputBytes";
        public const string ViewportPixels = "viewportPixels";

        // --- the geoclip bake. `FramesWaited` above is REUSED rather than twinned: a bake's awaited frames are
        // the same fact the render lanes count, and a second name for it would split one metric in two.

        /// <summary>How many <c>ForceDraw</c>s the bake issued (one per awaited frame it asked to draw).</summary>
        public const string BakeForceDraws = "bakeForceDraws";

        /// <summary>
        /// How many awaited frames the DRAW BUDGET declined to close with a <c>ForceDraw</c> (GeoClipDrawBudget).
        /// Counted rather than left implicit: the whole lever is "issue fewer forced draws", and a bake that
        /// reports four draws is otherwise indistinguishable from one whose lever never armed and whose rig
        /// simply needed four. Together with <see cref="BakeForceDraws"/> this is the schedule the profile beside
        /// it was measured under. Frames the bake never asked to draw in the first place — the sweep's yields —
        /// are NOT counted here; only a draw the budget removed.
        /// </summary>
        public const string BakeDrawsElided = "bakeDrawsElided";

        /// <summary>Colour-only mesh readbacks the association probe took (the meter's own count).</summary>
        public const string BakeColorReads = "bakeColorReads";

        /// <summary>Bracketed RID candidates validated through the rendering server.</summary>
        public const string BakeSweepProbes = "bakeSweepProbes";
    }

    private static readonly bool ProfilingEnabled =
        Environment.GetEnvironmentVariable(ProfileEnvVar) != "0";

    /// <summary>Whether renders are recording. Read once at startup from <see cref="ProfileEnvVar"/>.</summary>
    public static bool Enabled => ProfilingEnabled;

    // The recorder is AMBIENT rather than threaded through every lane signature: the capture helpers that need to
    // stamp a phase (the warmup-frame await, the encoder) sit several frames deep in the call chain and are shared
    // by every lane. AsyncLocal (not a plain static) because a render is an async main-thread flow that yields on
    // Godot signals: two renders CAN interleave on the main thread, and a worker encode task must attribute its
    // time to the render that spawned it — both of which fall out of ExecutionContext flow for free.
    private static readonly AsyncLocal<Recorder?> Current = new();

    private static readonly object CompletedGate = new();
    private static readonly Dictionary<string, Snapshot> Completed = new(StringComparer.Ordinal);
    private static readonly Queue<string> CompletedOrder = new();

    /// <summary>One phase's cost within a render: summed duration, how many times it ran, and whether it held the
    /// Godot main thread (<see cref="Blocking"/> false = the game kept rendering its own frames meanwhile).</summary>
    public readonly record struct PhaseCost(string Phase, double Ms, int Calls, bool Blocking);

    /// <summary>
    /// One completed render's breakdown. <see cref="TotalMs"/> is measured end-to-end rather than summed, so the
    /// gap between it and <c>BlockingMs + ParkedMs</c> is visible: that residue is real un-attributed render time,
    /// and hiding it by defining the total as the sum would make the instrument self-confirming.
    /// </summary>
    public sealed record Snapshot(
        string RequestId,
        double TotalMs,
        double BlockingMs,
        double ParkedMs,
        IReadOnlyList<PhaseCost> Phases,
        IReadOnlyDictionary<string, long> Counters)
    {
        public static readonly Snapshot Empty = new(string.Empty, 0, 0, 0, [], new Dictionary<string, long>());

        /// <summary>Render time no phase claimed. Positive by construction unless phases overlap (worker encodes).</summary>
        public double UnattributedMs => Math.Max(0, TotalMs - BlockingMs - ParkedMs);

        /// <summary>The largest blocking phase — the one an optimization round should look at first.</summary>
        public PhaseCost? TopBlocking => Phases.Where(p => p.Blocking).OrderByDescending(p => p.Ms).Cast<PhaseCost?>().FirstOrDefault();

        public long Counter(string name) => Counters.TryGetValue(name, out var value) ? value : 0;
    }

    /// <summary>
    /// Start recording for <paramref name="requestId"/> and return the recorder. Every later stamp on this
    /// logical flow (including worker tasks it spawns) lands here until <see cref="Complete"/>. Null when
    /// profiling is off; a re-entrant Begin simply replaces the ambient recorder — a nested render is its own
    /// measurement, not a child of ours.
    /// </summary>
    public static Recorder? Begin(string? requestId)
    {
        if (!ProfilingEnabled)
        {
            return null;
        }

        var recorder = new Recorder(string.IsNullOrWhiteSpace(requestId) ? "anonymous" : requestId!);
        Current.Value = recorder;
        return recorder;
    }

    /// <summary>
    /// Make <paramref name="recorder"/> the ambient one for the calling flow, restoring the previous on dispose.
    /// </summary>
    /// <remarks>
    /// REQUIRED when a render hops threads through a raw <see cref="SynchronizationContext.Post"/> — which is how
    /// an extraction reaches the Godot main thread (<see cref="Sts2MainThreadDispatcher"/> queues the delegate and
    /// the main loop invokes it directly, so the posting thread's ExecutionContext, and with it the ambient
    /// recorder, does NOT travel). Without this the lane's stamps land nowhere and the render reports as one
    /// unattributed block, which is exactly the shape the first live run of this profiler produced.
    /// </remarks>
    public static Adoption Adopt(Recorder? recorder) => new(recorder);

    /// <summary>
    /// Time a phase. Dispose the returned scope to record it (<c>using var _ = Measure(...)</c>). Cheap and
    /// always safe: with profiling off, or outside any <see cref="Begin"/>, the scope holds no recorder and its
    /// disposal does nothing.
    /// </summary>
    /// <param name="blocking">
    /// False for a segment that does NOT hold the Godot main thread — an <c>await</c> on a frame signal, or work
    /// handed to a worker thread. This is the split the whole instrument exists for; getting it wrong turns a
    /// latency cost into a reported game stall.
    /// </param>
    public static Scope Measure(string phase, bool blocking = true)
        => Current.Value is { } recorder ? new Scope(recorder, phase, blocking) : default;

    /// <summary>
    /// Attribute an ALREADY-MEASURED duration to a phase, without wrapping the code that produced it.
    ///
    /// <para>WHY, given <see cref="Measure"/> exists: some of the costliest segments are already metered by the
    /// thing that performs them and that thing is deliberately Godot-free — the geoclip sweep returns each tier
    /// pass's own <c>Ms</c>, and the association's colour-read meter is the source of truth for what the probe
    /// cost. Wrapping those in a scope would either mean pushing the profiler into offline decision code or
    /// re-timing a number that has already been measured more precisely.</para>
    ///
    /// <para>The caller owns the arithmetic, which includes NOT double counting: an already-summed duration that
    /// spans its own awaited frames must have the parked half subtracted before it is recorded as blocking, or
    /// the fold over-attributes and <see cref="Snapshot.UnattributedMs"/> — the residual this instrument is
    /// graded on — silently clamps to zero.</para>
    ///
    /// <para>Outside a <see cref="Begin"/>, or with profiling off, this is a DROP and never a throw: a stamp is
    /// diagnostics, and diagnostics must not be able to fail a bake.</para>
    /// </summary>
    public static void Record(string phase, double elapsedMs, bool blocking = true)
        => Current.Value?.Add(phase, elapsedMs, blocking);

    /// <summary>
    /// The active render's breakdown SO FAR, without closing it. For a long flow that has to publish its own
    /// numbers mid-way — the geoclip bake writes a profile into every pose's manifest while the rig pass is
    /// still running — where waiting for <see cref="Complete"/> would mean the artifact never carries one.
    /// Null outside a render, exactly like <see cref="TryTake"/>: absent means "not measured".
    /// </summary>
    public static Snapshot? Peek() => Current.Value?.ToSnapshot();

    /// <summary>Add to a scalar counter (see <see cref="Counter"/>) on the active render.</summary>
    public static void Count(string counter, long delta = 1)
        => Current.Value?.Count(counter, delta);

    /// <summary>Set a scalar counter to an absolute value (a gauge — output bytes, viewport pixels).</summary>
    public static void Set(string counter, long value)
        => Current.Value?.Set(counter, value);

    /// <summary>
    /// Close the active render and retain its snapshot for <see cref="TryTake"/>. Clears the ambient recorder, so
    /// a stamp that escapes afterwards is dropped rather than attributed to the wrong render.
    /// </summary>
    public static Snapshot? Complete()
    {
        if (Current.Value is not { } recorder)
        {
            return null;
        }

        Current.Value = null;
        var snapshot = recorder.ToSnapshot();
        lock (CompletedGate)
        {
            if (!Completed.ContainsKey(snapshot.RequestId))
            {
                CompletedOrder.Enqueue(snapshot.RequestId);
            }

            Completed[snapshot.RequestId] = snapshot;
            while (CompletedOrder.Count > MaxRetainedSnapshots)
            {
                Completed.Remove(CompletedOrder.Dequeue());
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Take the completed snapshot for <paramref name="requestId"/>, removing it. Null when profiling is off, the
    /// render predates the profiler, or someone already took it — a caller must treat an absent breakdown as
    /// "not measured" and never as "measured zero".
    /// </summary>
    public static Snapshot? TryTake(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        lock (CompletedGate)
        {
            if (!Completed.Remove(requestId!, out var snapshot))
            {
                return null;
            }

            return snapshot;
        }
    }

    /// <summary>Drop all retained snapshots (test isolation; an embedder never needs this).</summary>
    public static void Reset()
    {
        Current.Value = null;
        lock (CompletedGate)
        {
            Completed.Clear();
            CompletedOrder.Clear();
        }
    }

    /// <summary>
    /// One-line human form: the totals then the phases in <see cref="PhaseOrder"/>, biggest-first within the line
    /// they already have. Used by a host log that prices each bake as it happens (couch-coop's spine prerender
    /// sweep), where a JSON blob per item would be unreadable.
    /// </summary>
    public static string FormatLogLine(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.Append($"total={snapshot.TotalMs:0.#}ms blocking={snapshot.BlockingMs:0.#}ms parked={snapshot.ParkedMs:0.#}ms");
        if (snapshot.UnattributedMs >= 1)
        {
            builder.Append($" other={snapshot.UnattributedMs:0.#}ms");
        }

        foreach (var phase in snapshot.Phases.OrderByDescending(p => p.Ms))
        {
            builder.Append($" {phase.Phase}={phase.Ms:0.#}");
            if (phase.Calls > 1)
            {
                builder.Append($"x{phase.Calls}");
            }
        }

        foreach (var counter in snapshot.Counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append($" {counter.Key}={counter.Value}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fold several renders into one phase table: durations and calls sum, and a phase keeps its blocking flag
    /// (which is a property of the CALL SITE, so it never differs between renders of the same phase). Used for a
    /// "where did a whole sweep go" summary over many bakes.
    /// </summary>
    public static IReadOnlyList<PhaseCost> Fold(IReadOnlyList<Snapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var totals = new Dictionary<string, PhaseCost>(StringComparer.Ordinal);
        foreach (var phase in snapshots.SelectMany(snapshot => snapshot.Phases))
        {
            totals[phase.Phase] = totals.TryGetValue(phase.Phase, out var running)
                ? running with { Ms = running.Ms + phase.Ms, Calls = running.Calls + phase.Calls }
                : phase;
        }

        return OrderPhases(totals.Values);
    }

    /// <summary>Phases in <see cref="PhaseOrder"/>; anything unknown sorts last, alphabetically, rather than being dropped.</summary>
    internal static IReadOnlyList<PhaseCost> OrderPhases(IEnumerable<PhaseCost> phases)
        => [.. phases
            .OrderBy(phase => PhaseRank.TryGetValue(phase.Phase, out var rank) ? rank : int.MaxValue)
            .ThenBy(phase => phase.Phase, StringComparer.Ordinal)];

    private static readonly Dictionary<string, int> PhaseRank =
        PhaseOrder.Select((phase, index) => (phase, index)).ToDictionary(pair => pair.phase, pair => pair.index, StringComparer.Ordinal);

    /// <summary>The scope returned by <see cref="Adopt"/>: ambient recorder in, previous one back on dispose.</summary>
    public readonly struct Adoption : IDisposable
    {
        private readonly Recorder? _previous;

        internal Adoption(Recorder? recorder)
        {
            _previous = Current.Value;
            if (recorder is not null)
            {
                Current.Value = recorder;
            }
        }

        public void Dispose() => Current.Value = _previous;
    }

    /// <summary>
    /// The disposable returned by <see cref="Measure"/>. A struct with a null recorder when profiling is off, so
    /// an unrecorded scope allocates nothing. Holds its recorder DIRECTLY rather than re-reading the ambient one
    /// on dispose: a phase measured across an <c>await</c> must land on the render that opened it.
    /// </summary>
    public readonly struct Scope(Recorder? recorder, string phase, bool blocking) : IDisposable
    {
        private readonly long _started = recorder is null ? 0 : Stopwatch.GetTimestamp();

        public void Dispose()
        {
            if (recorder is null)
            {
                return;
            }

            recorder.Add(phase, (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency, blocking);
        }
    }

    /// <summary>
    /// One render's accumulator. Locked because the worker encode tasks a clip bake spawns record into the same
    /// render while the main thread is still stamping its own phases.
    /// </summary>
    public sealed class Recorder(string requestId)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, PhaseCost> _phases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
        private readonly long _started = Stopwatch.GetTimestamp();

        public string RequestId { get; } = requestId;

        public void Add(string phase, double elapsedMs, bool blocking)
        {
            lock (_gate)
            {
                _phases[phase] = _phases.TryGetValue(phase, out var running)
                    ? running with { Ms = running.Ms + elapsedMs, Calls = running.Calls + 1 }
                    : new PhaseCost(phase, elapsedMs, 1, blocking);
            }
        }

        public void Count(string counter, long delta)
        {
            lock (_gate)
            {
                _counters[counter] = (_counters.TryGetValue(counter, out var running) ? running : 0) + delta;
            }
        }

        public void Set(string counter, long value)
        {
            lock (_gate)
            {
                _counters[counter] = value;
            }
        }

        public Snapshot ToSnapshot()
        {
            lock (_gate)
            {
                var ordered = OrderPhases(_phases.Values);
                return new Snapshot(
                    RequestId,
                    (Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency,
                    ordered.Where(phase => phase.Blocking).Sum(phase => phase.Ms),
                    ordered.Where(phase => !phase.Blocking).Sum(phase => phase.Ms),
                    ordered,
                    new Dictionary<string, long>(_counters, StringComparer.Ordinal));
            }
        }
    }
}
