namespace Spirectl.Sts2.Live;

/// <summary>
/// Runtime-adjustable knobs for the SEMANTIC state watch hub (<c>EmbeddableStateSubscriptionHub</c>), the sibling
/// of <see cref="Sts2SceneWatchRuntimeSettings"/> for the scene-delta watcher. The hub is internal, but an
/// embedder needs to flip these at RUN TIME (a browser host that wants a cheaper idle host, or an A/B measurement
/// that has to compare backoff on against backoff off in one process), so they live on this public holder that
/// the hub consults per capture. Each seeds from an environment variable so launch-time behaviour is settable
/// without code, and can then be overwritten by an embedder.
/// <para>
/// Static because the hub reads them on a pool thread (and the tick gate reads
/// <see cref="RevisionWake"/> on the game main thread) while an embedder writes them from a background thread —
/// plain bool/long reads and writes are atomic and these are advisory pacing levers, so a one-capture stale read
/// is harmless and no locking is needed.
/// </para>
/// </summary>
public static class Sts2StateWatchRuntimeSettings
{
    /// <summary>Default idle ceiling: an unchanged subscription slows to one capture per 400 ms.</summary>
    public const int DefaultMaxIdleMs = 400;

    /// <summary>
    /// HARD ceiling on <see cref="MaxIdleInterval"/>, in milliseconds. Backoff is allowed to make an idle
    /// subscription cheap; it is NOT allowed to make a change arrive late. Semantic-revision coverage is partial
    /// by design (the hooks that bump it do not span gold, potions, map travel or remote seats), so polling
    /// remains the guarantee that a change is always discovered, and 400 ms is the budget that guarantee is
    /// written against. A larger value — from an env var or an embedder — is clamped to this.
    /// </summary>
    public const int MaxIdleCeilingMs = 400;

    private static long _maxIdleMs = ReadMs("SPIRECTL_STATE_WATCH_MAX_IDLE_MS", DefaultMaxIdleMs);

    /// <summary>
    /// Let an UNCHANGED subscription double its capture interval from its own
    /// <c>MinCaptureInterval</c> floor up to <see cref="MaxIdleInterval"/>, resetting to the floor the moment the
    /// state fingerprint changes, an action forces a refresh, or the semantic revision moves. Default ON;
    /// <c>SPIRECTL_STATE_WATCH_IDLE_BACKOFF=0</c> seeds it OFF, which restores the pre-backoff pacing exactly
    /// (every subscriber captures at its floor forever). A per-subscription
    /// <c>CurrentStateSubscriptionRequest.IdleBackoff=false</c> opts one subscriber out even while this is ON.
    /// </summary>
    public static bool IdleBackoff { get; set; } =
        ReadBool("SPIRECTL_STATE_WATCH_IDLE_BACKOFF", defaultOn: true);

    /// <summary>
    /// The ceiling an idle subscription's interval doubles toward. Seeded from
    /// <c>SPIRECTL_STATE_WATCH_MAX_IDLE_MS</c> (default <see cref="DefaultMaxIdleMs"/>), and always clamped into
    /// <c>[0, <see cref="MaxIdleCeilingMs"/>]</c> on both read and write — see <see cref="MaxIdleCeilingMs"/> for
    /// why the ceiling is not negotiable.
    /// </summary>
    public static TimeSpan MaxIdleInterval
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _maxIdleMs));
        set => Interlocked.Exchange(ref _maxIdleMs, ClampMaxIdleMs((long)value.TotalMilliseconds));
    }

    /// <summary>
    /// Let a semantic-revision bump (<see cref="Sts2SemanticStateRevision"/>, moved by the game-thread hooks the
    /// bridge already installs) collapse an idle subscription's backoff on the very next tick, so a change that a
    /// hook DOES cover is picked up at the floor interval rather than after the idle wait. Default ON;
    /// <c>SPIRECTL_STATE_WATCH_REVISION_WAKE=0</c> seeds it OFF.
    /// <para>
    /// It is an ACCELERATOR, never a gate: with this OFF (or with a change no hook covers) the hub still polls at
    /// <see cref="MaxIdleInterval"/> and still finds the change. Nothing anywhere waits for a revision bump.
    /// </para>
    /// </summary>
    public static bool RevisionWake { get; set; } =
        ReadBool("SPIRECTL_STATE_WATCH_REVISION_WAKE", defaultOn: true);

    /// <summary>
    /// Enables bridge-owned instrumentation for the state hub's capture, emission, backoff, revision-wake, and
    /// fingerprint counters. Embedded runtimes retain this reusable pacing setting but use the no-op seam.
    /// Default OFF; <c>SPIRECTL_STATE_WATCH_PROFILE=1</c> seeds it ON.
    /// </summary>
    public static bool Profile { get; set; } =
        ReadBool("SPIRECTL_STATE_WATCH_PROFILE", defaultOn: false);

    /// <summary>Clamp a requested idle ceiling into the never-miss budget. Negatives collapse to zero.</summary>
    public static long ClampMaxIdleMs(long milliseconds)
        => Math.Clamp(milliseconds, 0, MaxIdleCeilingMs);

    private static bool ReadBool(string name, bool defaultOn)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultOn;
        }

        return raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }

    private static long ReadMs(string name, int defaultMs)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)
            || !long.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return ClampMaxIdleMs(defaultMs);
        }

        return ClampMaxIdleMs(parsed);
    }
}
