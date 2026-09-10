namespace Spirectl.Sts2.Live;

/// <summary>
/// A process-global monotonic counter that the bridge's EXISTING game-thread hooks bump when something they
/// already observe has changed the semantic state. The semantic state watch hub reads it on the main-thread tick
/// and, when it has moved, collapses an idle subscriber's backoff so the change is captured at the floor interval
/// instead of after the idle wait.
/// <para>
/// <b>Accelerator, never a gate.</b> Nothing waits for a bump. Hook coverage is partial on purpose — plenty of
/// state (gold, potions, map travel, remote seats, most screen content) moves with no hook attached — so the hub
/// keeps polling at its idle ceiling regardless, and that poll, not this counter, is what guarantees a change is
/// never missed. Treating a stale revision as "nothing happened" would be a correctness bug; treating it as
/// "nothing extra to hurry for" is all this is for.
/// </para>
/// <para>
/// Godot-free and always compiled, so the hub (which is not live-host-only) can read it in both builds and the
/// wake path stays unit-testable without the game assemblies. Written from the game main thread inside Harmony
/// postfixes and read from a pool thread, so the increment is interlocked; a reader that sees a one-tick stale
/// value simply wakes one tick later.
/// </para>
/// </summary>
public static class Sts2SemanticStateRevision
{
    private static long _revision;

    /// <summary>The current revision. Starts at 0 and only ever increases.</summary>
    public static ulong Current => unchecked((ulong)Interlocked.Read(ref _revision));

    /// <summary>
    /// Record that observable semantic state has probably changed. Cheap enough (one interlocked increment) to
    /// call from a hot game-thread postfix, and deliberately carries no payload: the hub re-captures and lets the
    /// fingerprint decide whether anything actually changed, so a spurious bump costs one capture, never a wrong
    /// event.
    /// </summary>
    public static ulong Bump() => unchecked((ulong)Interlocked.Increment(ref _revision));

    /// <summary>Test isolation only: rewind the counter so a test starts from a known revision.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _revision, 0);
}
