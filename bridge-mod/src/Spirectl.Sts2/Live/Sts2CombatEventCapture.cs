using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The one flag every combat-event capture hook early-outs on: is anybody actually watching
/// <c>WatchCombatEvents</c>?
/// </summary>
/// <remarks>
/// <para>
/// WHY IT EXISTS. The damage, card-upgrade and VFX-spawn hooks are installed unconditionally at bridge
/// startup and used to do their FULL work whether or not the stream had a subscriber — resolving ids,
/// allocating payloads and taking the hub's fan-out lock on the game thread for every damage number, every
/// upgrade and every VFX spawn in an ordinary single-player game. The VFX one is the worst of the three
/// because its Harmony target is the game's GENERIC add-child helper, so it runs on every node attach in the
/// process. The couch-coop mod, which is the main embedder, never subscribes to combat events at all, so on
/// that host it was pure waste — and the product requires an unused install to be indistinguishable from no
/// install.
/// </para>
/// <para>
/// A VOLATILE BOOL, NOT <c>Shared.HasSubscribers</c>. That property takes the hub's fan-out lock, and these
/// are per-node-attach and per-damage-tick paths; a lock acquisition there is the very cost being removed.
/// The hub raises <see cref="EmbeddableCombatEventHub.ActiveChanged"/> on the 0↔1 subscriber transitions and
/// this caches the answer, which is the pattern <c>Sts2TweenRecorderHooks</c> already uses for the animation
/// hint hub.
/// </para>
/// <para>
/// Fail OPEN is deliberately NOT the policy here. If the arming subscription somehow never runs, capture
/// stays off and a watcher sees an empty stream — a visible, diagnosable failure of a developer tool. The
/// opposite default would silently reinstate the cost on every player's machine, which is the bug this fixes.
/// <see cref="EnsureArmed"/> syncs from <see cref="EmbeddableCombatEventHub.HasSubscribers"/> at install time
/// so a hook installed AFTER a subscriber attached still comes up armed.
/// </para>
/// </remarks>
internal static class Sts2CombatEventCapture
{
    private static readonly object Sync = new();
    private static bool _armed;
    private static volatile bool _active;

    /// <summary>
    /// Whether any <c>WatchCombatEvents</c> subscriber is attached. Read this at the TOP of a capture hook,
    /// before any id resolution or allocation.
    /// </summary>
    public static bool Active => _active;

    /// <summary>
    /// Attach to the hub's active-changed signal. Idempotent; called from every capture hook's
    /// <c>Install</c> so the flag is live no matter which of them is installed, or in what order.
    /// </summary>
    public static void EnsureArmed()
    {
        lock (Sync)
        {
            if (_armed)
            {
                return;
            }

            _armed = true;
            EmbeddableCombatEventHub.Shared.ActiveChanged += SetActive;
            // A subscriber that attached before this hook installed would otherwise never be seen: the
            // 0->1 transition it raised is already in the past.
            _active = EmbeddableCombatEventHub.Shared.HasSubscribers;
        }
    }

    /// <summary>Test seam: force the flag without a hub subscription.</summary>
    internal static void SetActive(bool value) => _active = value;
}
