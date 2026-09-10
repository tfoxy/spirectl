using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

// Callback slots have no hook initialization side effects. The binding owns their registration;
// the process-lifetime hook patches only consume them.
internal static class Sts2SceneAnimationCallbacks
{
    internal static Func<ulong, TweenTargetChange, double, TweenEndpoint?>? TweenEndpointResolver;
    internal static Action<IReadOnlyCollection<ulong>, bool>? TweenWindowCanceller;
    internal static Func<Sts2CardFlightResolveRequest, CardFlightHint?>? ShuffleResolver;
    internal static Func<Sts2DiscardFlightResolveRequest, CardFlightHint?>? DiscardResolver;
    internal static Func<ulong, TweenTargetChange, double, TweenEndpoint?>? HandEndpointResolver;
    internal static Action<IReadOnlyCollection<ulong>, bool>? HandWindowCanceller;
    internal static Func<ulong, bool>? HasOpenWindow;
}
