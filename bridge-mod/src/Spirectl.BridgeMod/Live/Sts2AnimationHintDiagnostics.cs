namespace Spirectl.Sts2.Live;

/// <summary>
/// One animation-hint producer family's emission tally: how many hints it published, and — one bucket per gate —
/// how many times it decided NOT to.
///
/// <para>WHY THE EARLY-OUTS ARE COUNTED AT ALL. Every hint hook here is deliberately silent when it declines: it is
/// installed unconditionally, runs on the game main thread, and must never log per spawn or throw into the game. The
/// cost of that discipline is that "the producer emitted no hints" and "the producer was never reached" look
/// identical from outside, so diagnosing a dead hint stream meant proving it by elimination — an expensive way to
/// discover that one bool lever was off. These bridge diagnostics make the question directly answerable by showing
/// WHICH gate ate the hint.</para>
///
/// <para>ALWAYS ON, no lever. Each bucket is a single <see cref="Interlocked"/> add on a path that already runs once
/// per VFX spawn / once per coalesced batch — never once per frame, and never inside the producer's capture walk —
/// so the ceiling is a few hundred adds per combat. A kill-switch would cost more than it saved and would reintroduce
/// exactly the "is it off, or is it broken?" ambiguity this exists to remove.</para>
///
/// <para>MONOTONIC AND PROCESS-GLOBAL: counters only ever rise and are never reset, so a reader that wants a rate
/// samples twice and takes the delta rather than expecting a zero baseline.</para>
/// </summary>
public sealed class Sts2AnimationHintFamilyCounters : ISts2AnimationHintCounter
{
    private long _emitted;
    private long _earlyOutNoSubscribers;
    private long _earlyOutLeverOff;
    private long _earlyOutNoResolver;
    private long _resolveNull;
    private long _publishFailed;
    private long _resolveParked;
    private long _resolveRetryHit;
    private long _resolveRetryDropped;
    private long _resolveNotReplayable;
    private long _resolveSingular;

    /// <summary>Hints this family actually published on the hub.</summary>
    public long Emitted => Interlocked.Read(ref _emitted);

    /// <summary>Declined because nothing was subscribed to the hint hub — i.e. no mirror consumer is attached.</summary>
    public long EarlyOutNoSubscribers => Interlocked.Read(ref _earlyOutNoSubscribers);

    /// <summary>Declined because this family's runtime kill-switch is OFF (per-frame streaming was asked for).</summary>
    public long EarlyOutLeverOff => Interlocked.Read(ref _earlyOutLeverOff);

    /// <summary>
    /// Declined because no resolver delegate is wired — the hook is installed but no live scene watcher registered
    /// itself, so a hint could be built but its streaming-suppression window could not be opened.
    /// </summary>
    public long EarlyOutNoResolver => Interlocked.Read(ref _earlyOutNoResolver);

    /// <summary>
    /// Reached the resolver and got nothing back: the node is not tracked yet, or the endpoint cross-check failed.
    /// Distinct from the gates above because the hook DID run — this is a targeting problem, not a wiring one.
    /// </summary>
    public long ResolveNull => Interlocked.Read(ref _resolveNull);

    /// <summary>
    /// A swallowed exception on a publish path. These catches exist so telemetry can never disrupt the game; the
    /// counter is the only trace they leave, so a nonzero value here means the hook is failing rather than declining.
    /// </summary>
    public long PublishFailed => Interlocked.Read(ref _publishFailed);

    /// <summary>
    /// R14 ATTRIBUTION FOR <see cref="ResolveNull"/>. A resolve that found NO tracked node for the mover and was
    /// therefore parked for a retry after the next reconcile. The hint was still declined on this pass (the hook
    /// records its <see cref="ResolveNull"/> as before), so this bucket does not replace that one — it says WHY the
    /// resolve came back empty, and pairs with <see cref="ResolveRetryHit"/> / <see cref="ResolveRetryDropped"/> to
    /// say how the retry ended. Parked ≈ RetryHit + RetryDropped in steady state, ignoring flights still in flight.
    /// </summary>
    public long ResolveParked => Interlocked.Read(ref _resolveParked);

    /// <summary>
    /// A parked resolve that succeeded on retry: the mover was tracked by a later reconcile, the hint was rebuilt
    /// against its LIVE pose and published. Every one of these also increments <see cref="Emitted"/>, so
    /// <c>Emitted</c> stays the single honest count of hints on the wire regardless of which path produced them.
    /// </summary>
    public long ResolveRetryHit => Interlocked.Read(ref _resolveRetryHit);

    /// <summary>
    /// A parked resolve that never made it: its TTL expired before the mover was tracked, the park was at its cap
    /// when the request arrived, or the retry resolved but had no publisher to hand the hint to. That flight kept
    /// streaming per-frame — correct, just not cheap.
    /// </summary>
    public long ResolveRetryDropped => Interlocked.Read(ref _resolveRetryDropped);

    /// <summary>
    /// R14 ATTRIBUTION FOR <see cref="ResolveNull"/>: the drawn scalars are not integrable (a non-finite value, a
    /// non-positive duration or speed, a degenerate scale), so no client could replay the animation. Nothing to do
    /// with tracking — a nonzero value here points at the CAPTURE, not at the walk's timing.
    /// </summary>
    public long ResolveNotReplayable => Interlocked.Read(ref _resolveNotReplayable);

    /// <summary>
    /// R14 ATTRIBUTION FOR <see cref="ResolveNull"/>: the mover WAS tracked, but its global transform is singular
    /// (visually collapsed), so there is no invertible basis to express the flight in and the hint would be
    /// meaningless.
    /// </summary>
    public long ResolveSingular => Interlocked.Read(ref _resolveSingular);

    /// <summary>
    /// Records one published hint and returns the new total, so a caller can keep its own emit-milestone logging
    /// (1 / 25 / 500 / 10000) off a single atomic increment instead of a second counter.
    /// </summary>
    public long RecordEmitted() => Interlocked.Increment(ref _emitted);

    /// <summary>Records one <see cref="EarlyOutNoSubscribers"/> decline.</summary>
    public void RecordEarlyOutNoSubscribers() => Interlocked.Increment(ref _earlyOutNoSubscribers);

    /// <summary>Records one <see cref="EarlyOutLeverOff"/> decline.</summary>
    public void RecordEarlyOutLeverOff() => Interlocked.Increment(ref _earlyOutLeverOff);

    /// <summary>Records one <see cref="EarlyOutNoResolver"/> decline.</summary>
    public void RecordEarlyOutNoResolver() => Interlocked.Increment(ref _earlyOutNoResolver);

    /// <summary>Records one <see cref="ResolveNull"/> outcome.</summary>
    public void RecordResolveNull() => Interlocked.Increment(ref _resolveNull);

    /// <summary>Records one <see cref="PublishFailed"/> outcome (a swallowed exception on a publish path).</summary>
    public void RecordPublishFailed() => Interlocked.Increment(ref _publishFailed);

    /// <summary>Records one <see cref="ResolveParked"/> outcome (mover untracked ⇒ parked for a retry).</summary>
    public void RecordResolveParked() => Interlocked.Increment(ref _resolveParked);

    /// <summary>Records one <see cref="ResolveRetryHit"/> outcome (a parked resolve published after a reconcile).</summary>
    public void RecordResolveRetryHit() => Interlocked.Increment(ref _resolveRetryHit);

    /// <summary>Records one <see cref="ResolveRetryDropped"/> outcome (expired, refused at the cap, or unpublishable).</summary>
    public void RecordResolveRetryDropped() => Interlocked.Increment(ref _resolveRetryDropped);

    /// <summary>Records one <see cref="ResolveNotReplayable"/> outcome (the drawn scalars are not integrable).</summary>
    public void RecordResolveNotReplayable() => Interlocked.Increment(ref _resolveNotReplayable);

    /// <summary>Records one <see cref="ResolveSingular"/> outcome (the tracked mover's transform is collapsed).</summary>
    public void RecordResolveSingular() => Interlocked.Increment(ref _resolveSingular);
}

/// <summary>
/// The always-on, public read surface for the animation-hint producers' emission counters, one
/// <see cref="Sts2AnimationHintFamilyCounters"/> per hint family.
///
/// <para>Public on purpose even though the hooks themselves are internal: a standalone bridge diagnostics endpoint
/// can answer "why is the mirror not receiving card-flight hints?" without a debugger or restart. Kept engine-free
/// so it compiles in every configuration and is unit-testable offline.</para>
///
/// <para>Families are independent: a stalled one is diagnosed by comparing its buckets against a family that is
/// still emitting.</para>
/// </summary>
public static class Sts2AnimationHintDiagnostics
{
    /// <summary>The discard→draw shuffle card-flight producer (<c>Sts2CardFlightHooks</c>).</summary>
    public static Sts2AnimationHintFamilyCounters CardFlight { get; } = new();

    /// <summary>
    /// The card-DISCARD flight producer. The family exists ahead of its hook so the two land independently; until
    /// that hook is wired every bucket here stays at zero, which is itself the honest reading.
    /// </summary>
    public static Sts2AnimationHintFamilyCounters CardDiscard { get; } = new();

    /// <summary>The hand-layout approach producer (<c>Sts2HandHolderHooks</c>).</summary>
    public static Sts2AnimationHintFamilyCounters HandTween { get; } = new();
}
