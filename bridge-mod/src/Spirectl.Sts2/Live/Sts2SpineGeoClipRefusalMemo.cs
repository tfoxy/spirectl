using System.Globalization;
using System.Text;
using Spirectl.Sts2.Core.Artifacts;

namespace Spirectl.Sts2.Live;

// Godot-free REFUSAL MEMO for the on-demand geoclip lane: the set of bake identities this process has already
// tried and already refused, so it does not re-derive the same refusal from scratch.
//
// WHAT IT IS FOR. A bake that runs to completion and still fails to acquire the whole rig
// (Sts2SpineGeoClipRequestLane.IncompletenessReason) costs exactly as much as one that succeeds — an embedded
// host measured a median of 1.45 s and a p90 of 4.15 s per identity, on a catalog where roughly half of them
// land there. Nothing about that verdict changes between two identical requests in one process, so paying for
// it twice is paying for a computation whose answer is already known. A host with its own persistent store
// (couch-coop has one) sidesteps this; every other embedder re-pays it on every request, and every host re-pays
// it on the first request after a launch.
//
// SUCCESSES ARE NEVER RECORDED. The memo is a negative cache only. A successful bake leaves an artifact
// directory on disk, and deciding whether that directory is still the right answer is the caller's
// content-addressed store's job, not a lookaside table's — a producer-side memo of successes would be a second,
// weaker cache racing the real one.
//
// WHAT MAKES TWO REQUESTS "THE SAME". Everything the bake's answer can depend on has to be in the key, or the
// memo answers a question it was not asked:
//   * the IDENTITY — scene, node, the animation(s) asked for, pose-only, the sample time, the frame clock;
//   * the LEVERS — the association/probe and pause knobs are read from the environment per bake, and flipping
//     one is a deliberate "try this rig again the other way", which a memo that ignored them would refuse;
//   * the BRIDGE VERSION — a refusal is evidence about one build of the baker. A newer build is exactly where a
//     fix would have landed, so it must not inherit the old build's refusals.
// The memo is in-memory, so the version guard is belt-and-braces within one process; it earns its place by
// making the key mean the same thing if a receipt is ever written down, and by making the receipt legible.
//
// AND IT CAN STILL HIDE A FIX — a fix that lands somewhere other than the bridge version or a lever (game
// content, a resource pack) is invisible to the key. That is what SpineGeoClipBakeRequestSnapshot's
// IgnoreRefusalMemo is for: it re-runs the bake and re-records whatever it finds.

/// <summary>
/// The identity a refusal is remembered under. A record struct so equality is the whole tuple and nothing
/// about "same request" is left to a hand-written comparison.
/// </summary>
/// <param name="SceneResPath">The <c>res://…</c> scene the bake loads.</param>
/// <param name="NodePath">The spine node within it, or <see cref="AutoNodePath"/> when the loader picks.</param>
/// <param name="AnimationName">
/// The animation identity this request bakes: the primary animation, and — for a rig request that named several
/// — every animation it asked for, in request order. A rig request is NOT the same identity as the single-pose
/// request for its primary animation: it can fall back per pose and it is graded per pose, so memoing the two
/// as one would answer a question that was not asked.
/// </param>
/// <param name="SampleTimeSeconds">
/// The requested pose time, rounded to <see cref="SampleTimeDecimals"/> decimals, or
/// <see cref="AutoSampleTime"/> when the request left it to the sample-time heuristic. Rounded because a caller
/// that derives the time from a wall clock would otherwise mint a fresh identity on every request and never hit
/// the memo at all.
/// </param>
/// <param name="Fps">The frame clock, clamped exactly as the request lane clamps it.</param>
/// <param name="MaxFrames">The frame cap, clamped exactly as the request lane clamps it.</param>
/// <param name="LeverSignature">See <see cref="Sts2SpineGeoClipLevers"/>.</param>
/// <param name="BridgeVersion">The build of the baker that produced the refusal.</param>
internal readonly record struct Sts2SpineGeoClipRefusalKey(
    string SceneResPath,
    string NodePath,
    string AnimationName,
    bool PoseOnly,
    double SampleTimeSeconds,
    int Fps,
    int MaxFrames,
    string LeverSignature,
    string BridgeVersion)
{
    /// <summary>What <c>NodePath == null</c> ("let the loader find the single spine node") is spelled as.</summary>
    internal const string AutoNodePath = "<auto>";

    /// <summary>
    /// What "no explicit sample time" is spelled as. A negative is unambiguous — a real sample time is a
    /// position within a clip and can never be one — which keeps the key free of a NaN whose equality reads
    /// differently depending on how it is compared.
    /// </summary>
    internal const double AutoSampleTime = -1d;

    /// <summary>Matches the <c>0.#####</c> precision every geoclip surface already prints sample times at.</summary>
    internal const int SampleTimeDecimals = 5;

    /// <summary>
    /// Joins a rig request's animations into one identity. A control character rather than a comma or a plus, so
    /// no animation name a game could ship can forge a two-animation identity out of a one-animation request.
    /// </summary>
    internal const char AnimationSeparator = '\u001f';

    internal static double NormalizeSampleTime(double? seconds)
        => seconds is { } value && double.IsFinite(value) && value >= 0d
            ? Math.Round(value, SampleTimeDecimals, MidpointRounding.AwayFromZero)
            : AutoSampleTime;

    /// <summary>
    /// The animation identity of a request: the primary animation first, then any additional animation a rig
    /// request named, de-duplicated, in request order.
    /// </summary>
    internal static string AnimationIdentity(SpineGeoClipBakeRequestSnapshot request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var primary = request.AnimationName ?? string.Empty;
        if (request.AnimationNames is not { Count: > 0 } extras)
        {
            return primary;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { primary };
        var builder = new StringBuilder(primary);
        foreach (var name in extras)
        {
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                builder.Append(AnimationSeparator).Append(name);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Build the key for one request. Clamps <c>Fps</c>/<c>MaxFrames</c> the way
    /// <c>Sts2SpineGeoClipRequestLane.PlanConfig</c> does, so two requests that would plan the same bake share a
    /// key even when one of them spelled an out-of-range number.
    /// </summary>
    internal static Sts2SpineGeoClipRefusalKey From(
        SpineGeoClipBakeRequestSnapshot request,
        string leverSignature,
        string bridgeVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Sts2SpineGeoClipRefusalKey(
            (request.SceneResPath ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(request.NodePath) ? AutoNodePath : request.NodePath.Trim(),
            AnimationIdentity(request),
            request.PoseOnly,
            NormalizeSampleTime(request.SampleTimeSeconds),
            Math.Clamp(request.Fps, Sts2SpineGeoClipSpec.MinFps, Sts2SpineGeoClipSpec.MaxFps),
            Math.Clamp(request.MaxFrames ?? Sts2SpineGeoClipMath.DefaultMaxFrames, 1, 100_000),
            leverSignature ?? string.Empty,
            bridgeVersion ?? string.Empty);
    }

    /// <summary>A one-line form for a log or a structured detail; not parsed by anything.</summary>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{SceneResPath}?node={NodePath}&anim={AnimationName}&pose={(PoseOnly ? 1 : 0)}"
                + $"&t={SampleTimeSeconds.ToString("0.#####", CultureInfo.InvariantCulture)}"
                + $"&fps={Fps}&maxFrames={MaxFrames}&levers={LeverSignature}&bridge={BridgeVersion}");
}

/// <summary>
/// What was remembered about one refused identity: the verdict, the counters that produced it, and how often it
/// has been asked for since.
/// </summary>
/// <param name="Arm">A <c>Sts2SpineGeoClipRequestLane.RefusalArm*</c> token.</param>
/// <param name="Reason">The full detail from <c>IncompletenessReason</c>, counts and all.</param>
/// <param name="Attempts">
/// How many times this identity has been REFUSED, memo replays included. A high number on a fresh process is a
/// caller retrying something the memo is answering for free — which is the number that says the memo is
/// working, and the number that says a caller has a retry loop it does not know about.
/// </param>
/// <param name="ElapsedMsFirst">
/// What the refusal cost the FIRST time. Kept because it is the only figure that says what a memo hit saved,
/// and because it is gone forever once the bake it came from is not re-run.
/// </param>
internal sealed record Sts2SpineGeoClipRefusalReceipt(
    string Arm,
    string Reason,
    int Slots,
    int SlotsVisible,
    int Associated,
    int Unassociated,
    int ForeignMeshes,
    int StaleMeshFrames,
    int AttachmentDriftSlots,
    DateTimeOffset FirstRefusedAtUtc,
    DateTimeOffset LastRefusedAtUtc,
    int Attempts,
    double ElapsedMsFirst,
    // The ownership split behind the verdict — see Sts2SpineGeoClipOwnership. Carried so a memo replay hands
    // back the same counters the rule was derived from; 0/0 on a receipt means the bake recorded no provenance.
    int ClaimsProven = 0,
    int ClaimsUnproven = 0);

/// <summary>
/// The bounded in-memory store of refused identities, most-recently-used first.
/// </summary>
/// <remarks>
/// <para>SIZING. 512 identities is comfortably more than a full catalog sweep of a game's rigs asks for in one
/// launch (an embedded host's prerender pass walks the low hundreds), so in the case the memo exists for —
/// "this launch has already refused you" — it never evicts. The cap is not a tuning knob so much as a promise
/// that a caller minting fresh identities in a loop cannot grow this without bound; LRU is the right eviction
/// because the identity a host is asking for repeatedly is exactly the one worth keeping.</para>
/// <para>LOCKED, not concurrent-collection: every operation touches both the map and the recency list, the
/// critical sections are a handful of pointer moves, and the callers are bake requests on worker threads (at
/// most a few per second). A lock is the honest shape for that.</para>
/// </remarks>
internal sealed class Sts2SpineGeoClipRefusalMemo
{
    /// <summary>How many refused identities are remembered before the least-recently-used one is dropped.</summary>
    internal const int Capacity = 512;

    /// <summary>The process-wide memo the on-demand bake lane consults.</summary>
    internal static Sts2SpineGeoClipRefusalMemo Shared { get; } = new();

    private readonly Lock _gate = new();

    private readonly Dictionary<Sts2SpineGeoClipRefusalKey, LinkedListNode<Entry>> _entries = [];

    // Most-recently-used at the FRONT; the eviction victim is always the tail.
    private readonly LinkedList<Entry> _recency = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// The receipt for an identity this process has already refused, or null. A hit is a USE: it moves the entry
    /// to the front of the recency order and bumps <see cref="Sts2SpineGeoClipRefusalReceipt.Attempts"/>, so the
    /// count answers "how often was this asked for" rather than "how often did it cost a bake".
    /// </summary>
    internal Sts2SpineGeoClipRefusalReceipt? Lookup(Sts2SpineGeoClipRefusalKey key, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            var receipt = node.Value.Receipt with
            {
                Attempts = node.Value.Receipt.Attempts + 1,
                LastRefusedAtUtc = nowUtc,
            };
            node.Value = node.Value with { Receipt = receipt };
            _recency.Remove(node);
            _recency.AddFirst(node);
            return receipt;
        }
    }

    /// <summary>
    /// Remember a refusal, or refresh one already remembered. Returns the stored receipt — the one from the
    /// FIRST refusal, with its attempt count advanced, because the first bake is the one that measured what this
    /// identity costs.
    /// </summary>
    internal Sts2SpineGeoClipRefusalReceipt Record(
        Sts2SpineGeoClipRefusalKey key,
        string arm,
        string reason,
        int slots,
        int slotsVisible,
        int associated,
        int unassociated,
        int foreignMeshes,
        int staleMeshFrames,
        int attachmentDriftSlots,
        double elapsedMs,
        DateTimeOffset nowUtc,
        int claimsProven = 0,
        int claimsUnproven = 0)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                var refreshed = existing.Value.Receipt with
                {
                    ClaimsProven = claimsProven,
                    ClaimsUnproven = claimsUnproven,
                    Arm = arm,
                    Reason = reason,
                    Slots = slots,
                    SlotsVisible = slotsVisible,
                    Associated = associated,
                    Unassociated = unassociated,
                    ForeignMeshes = foreignMeshes,
                    StaleMeshFrames = staleMeshFrames,
                    AttachmentDriftSlots = attachmentDriftSlots,
                    LastRefusedAtUtc = nowUtc,
                    Attempts = existing.Value.Receipt.Attempts + 1,
                };
                existing.Value = existing.Value with { Receipt = refreshed };
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return refreshed;
            }

            var receipt = new Sts2SpineGeoClipRefusalReceipt(
                arm,
                reason,
                slots,
                slotsVisible,
                associated,
                unassociated,
                foreignMeshes,
                staleMeshFrames,
                attachmentDriftSlots,
                FirstRefusedAtUtc: nowUtc,
                LastRefusedAtUtc: nowUtc,
                Attempts: 1,
                ElapsedMsFirst: elapsedMs,
                claimsProven,
                claimsUnproven);

            var node = _recency.AddFirst(new Entry(key, receipt));
            _entries[key] = node;

            while (_entries.Count > Capacity && _recency.Last is { } victim)
            {
                _recency.RemoveLast();
                _entries.Remove(victim.Value.Key);
            }

            return receipt;
        }
    }

    /// <summary>Forget everything (test isolation; the host never needs this).</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
        }
    }

    private sealed record Entry(Sts2SpineGeoClipRefusalKey Key, Sts2SpineGeoClipRefusalReceipt Receipt);
}

/// <summary>
/// The bake levers that are read from the ENVIRONMENT per bake, folded into one string for the refusal memo's
/// key.
/// </summary>
/// <remarks>
/// It folds the RESOLVED settings, never the raw variables: a misspelt value and an absent one produce the same
/// bake, so they have to produce the same signature, or a typo would silently mint a fresh identity and re-pay
/// for a refusal the memo already holds.
/// </remarks>
internal static class Sts2SpineGeoClipLevers
{
    /// <summary>The signature of an explicitly supplied set of levers (the unit-tested seam).</summary>
    /// <param name="ownershipProofArmed">
    /// The admission lever, not a bake lever — but it belongs in this key for the same reason the others do. It
    /// decides the VERDICT on identical counters, so a memo that ignored it would answer a request made under one
    /// rule with the other rule's refusal, for the life of the process. Defaulted to the armed value so callers
    /// that predate it keep the signature they had.
    /// </param>
    /// <param name="indexFloorRecoveryArmed">
    /// Window A's index-floor recovery strip
    /// (<see cref="Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryEnv"/>). An ACQUISITION lever: with it down the
    /// sweep covers strictly less of the index axis, so the same rig in the same process can validate fewer
    /// meshes and refuse. A memo that ignored it would answer a request made with the strip armed using the
    /// refusal recorded without it — which is precisely the "I fixed it, why is it still refusing" trap the
    /// bridge-version clause in the key exists to avoid. Defaulted to the armed value so callers that predate it
    /// keep the signature they had.
    /// </param>
    /// <param name="indexCeilingRecoveryArmed">
    /// The index-CEILING recovery strip (<see cref="Sts2SpineGeoClipSweep.IndexCeilingRecoveryEnv"/>). Folded as
    /// its own term beside the floor's rather than combined with it, and that separation is the whole point: the
    /// two strips cover opposite halves of the index axis, so a single "recovery armed" bit would let a request
    /// made with the ceiling down be answered by the receipt written with the FLOOR down. Same reasoning as the
    /// floor's, same default so callers that predate it keep the signature they had.
    /// </param>
    internal static string Signature(
        GeoClipProbeSettings probe,
        GeoClipPauseBudget pause,
        bool denseSweepOnly,
        bool ownershipProofArmed = true,
        bool indexFloorRecoveryArmed = Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryDefault,
        bool indexCeilingRecoveryArmed = Sts2SpineGeoClipSweep.IndexCeilingRecoveryDefault)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(pause);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"probe={probe.Scheme}/{(probe.RepairEnabled ? 1 : 0)}/{probe.Channels}/{(probe.AtlasFirstArmed ? 1 : 0)}"
                + $";pause={(pause.PauseTree ? 1 : 0)}/{pause.Assert}"
                + $";dense={(denseSweepOnly ? 1 : 0)}"
                + $";own={(ownershipProofArmed ? 1 : 0)}"
                + $";floor={(indexFloorRecoveryArmed ? 1 : 0)}"
                + $";ceil={(indexCeilingRecoveryArmed ? 1 : 0)}");
    }

    /// <summary>
    /// The signature of the levers a bake started RIGHT NOW would run under.
    /// </summary>
    /// <remarks>
    /// The dense-sweep arm is folded from <see cref="Sts2SpineGeoClipWalk.DenseSweepOnlyDefault"/> rather than
    /// from its environment variable because that is what the ON-DEMAND lane plans with — the variable is the
    /// env-armed one-shot lane's, and this memo only ever answers on-demand requests. Folding the variable here
    /// would let a value the request lane ignores split the memo's keyspace.
    /// </remarks>
    internal static string CaptureSignature()
        => Signature(
            GeoClipProbeSettings.FromEnvironment(),
            GeoClipPauseBudget.FromEnvironment(),
            Sts2SpineGeoClipWalk.DenseSweepOnlyDefault,
            Sts2SpineGeoClipOwnership.ArmedFromEnvironment(),
            // FROM THE VARIABLE, unlike the dense-sweep arm above: the baker resolves this switch at its own plan
            // site, so it bites on the on-demand lane too and the memo must split its keyspace on it.
            Sts2SpineGeoClipSweep.WindowAIndexFloorRecoveryArmed(),
            Sts2SpineGeoClipSweep.IndexCeilingRecoveryArmed());
}
