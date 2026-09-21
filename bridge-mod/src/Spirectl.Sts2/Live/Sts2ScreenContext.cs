using System.Reflection;

namespace Spirectl.Sts2.Live;

/// <summary>
/// A read-only seam onto the game's active screen: which screen node is currently on top, whether a node an
/// embedder already holds IS that screen, and a subscription that fires when the answer may have changed.
/// </summary>
/// <remarks>
/// <para>WHY THIS EXISTS. An embedder that reacts to screens (mounting a panel on a lobby, say) otherwise polls:
/// walk or re-read state every few hundred milliseconds and compare. Polling costs the game's main thread on
/// every tick including the overwhelming majority where nothing moved, and it still answers the wrong question —
/// "is this node visible in the tree?" is not "is this node the screen the player is looking at", and the two
/// disagree whenever a screen is parked visible behind another one. The game already raises an event when its
/// active screen may have changed, and the current-screen comparison is O(1), so an embedder can be told rather
/// than ask.</para>
/// <para>TOTALITY IS THE CONTRACT. Everything here resolves through reflection against the game assembly, so
/// everything here is optional: <see cref="Current"/> is null and <see cref="SubscribeUpdated"/> returns null
/// whenever the game is not behind this process, or its shape has moved. A null subscription means "unavailable"
/// — callers fall back to whatever they did before — so this must never throw instead.</para>
/// <para>WHY IT BELONGS HERE. Resolving the active screen is generic STS2 runtime inspection, reusable by any
/// embedder; it is not specific to any one of them.</para>
/// </remarks>
public static class Sts2ScreenContext
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The screen node currently on top, or null when it cannot be resolved (no game behind this process, or no
    /// screen mounted yet).
    /// </summary>
    public static object? Current => Sts2LiveIntrospection.ResolveCurrentScreenObject();

    /// <summary>
    /// Whether <paramref name="node"/> is the screen currently on top. A plain reference comparison against
    /// <see cref="Current"/> — the same test the game applies for its own answer, so no reflection is needed
    /// beyond resolving the current screen.
    /// </summary>
    /// <remarks>
    /// Distinct from "visible in the tree": a screen can be left mounted and visible underneath the one the
    /// player is actually on, and only this test tells the two apart.
    /// </remarks>
    public static bool IsCurrent(object? node) => node is not null && ReferenceEquals(node, Current);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the game's "the active screen may have changed" event. Dispose
    /// the returned handle to unsubscribe. Returns null — never throws — when the event cannot be resolved, which
    /// the caller must read as "unavailable" and handle by other means.
    /// </summary>
    /// <remarks>
    /// <para>The event is raised on the game's own thread when a screen is pushed, popped, opened or closed. It
    /// carries no payload and makes no promise that the screen actually differs, so a handler should re-read
    /// <see cref="Current"/> (or call <see cref="IsCurrent"/>) rather than assume a transition happened.</para>
    /// <para>The handle unsubscribes from the SAME instance it subscribed to, captured at subscription time
    /// rather than re-resolved at dispose: if the context instance were replaced in between, re-resolving would
    /// detach a handler from an object that never had it and leak the one that does.</para>
    /// </remarks>
    public static IDisposable? SubscribeUpdated(Action handler)
    {
        if (handler is null)
        {
            return null;
        }

        try
        {
            var contextType = Type.GetType(Sts2LiveIntrospection.ActiveScreenContextTypeName);
            var instance = contextType?.GetProperty("Instance", StaticFlags)?.GetValue(null);
            if (contextType is null || instance is null)
            {
                return null;
            }

            var updated = contextType.GetEvent("Updated", InstanceFlags);
            if (updated is null)
            {
                return null;
            }

            updated.AddEventHandler(instance, handler);
            return new EventSubscription(updated, instance, handler);
        }
        catch
        {
            return null;
        }
    }

    private sealed class EventSubscription(EventInfo updated, object instance, Action handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                updated.RemoveEventHandler(instance, handler);
            }
            catch
            {
                // Detaching is best-effort: a torn-down context has already dropped the handler with it, and a
                // throwing Dispose would take down whatever scope owns the subscription.
            }
        }
    }
}
