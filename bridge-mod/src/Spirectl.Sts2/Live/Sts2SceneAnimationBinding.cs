using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Owns one runtime's callbacks into process-lifetime animation hooks. Replacing a runtime does
/// not unpatch hooks, and disposing an older binding cannot remove its replacement's callbacks.
/// </summary>
internal sealed class Sts2SceneAnimationBinding : IDisposable
{
    private readonly Func<ulong, TweenTargetChange, double, TweenEndpoint?> _endpoint;
    private readonly Action<IReadOnlyCollection<ulong>, bool> _cancel;
    private readonly Func<Sts2CardFlightResolveRequest, CardFlightHint?> _shuffle;
    private readonly Func<Sts2DiscardFlightResolveRequest, CardFlightHint?> _discard;
    private readonly Func<ulong, bool> _hasWindow;
    private int _disposed;

    public Sts2SceneAnimationBinding(
        Func<ulong, TweenTargetChange, double, TweenEndpoint?> endpoint,
        Action<IReadOnlyCollection<ulong>, bool> cancel,
        Func<Sts2CardFlightResolveRequest, CardFlightHint?> shuffle,
        Func<Sts2DiscardFlightResolveRequest, CardFlightHint?> discard,
        Func<ulong, bool> hasWindow)
    {
        _endpoint = (id, change, duration) => IsDisposed ? null : endpoint(id, change, duration);
        _cancel = (ids, opacity) => { if (!IsDisposed) cancel(ids, opacity); };
        _shuffle = request => IsDisposed ? null : shuffle(request);
        _discard = request => IsDisposed ? null : discard(request);
        _hasWindow = id => !IsDisposed && hasWindow(id);

        Sts2SceneAnimationCallbacks.TweenEndpointResolver = _endpoint;
        Sts2SceneAnimationCallbacks.TweenWindowCanceller = _cancel;
        Sts2SceneAnimationCallbacks.ShuffleResolver = _shuffle;
        Sts2SceneAnimationCallbacks.DiscardResolver = _discard;
        Sts2SceneAnimationCallbacks.HandEndpointResolver = _endpoint;
        Sts2SceneAnimationCallbacks.HandWindowCanceller = _cancel;
        Sts2SceneAnimationCallbacks.HasOpenWindow = _hasWindow;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // CompareExchange is identity-based, so even an in-flight replacement is retained.
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.TweenEndpointResolver, null, _endpoint);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.TweenWindowCanceller, null, _cancel);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.ShuffleResolver, null, _shuffle);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.DiscardResolver, null, _discard);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HandEndpointResolver, null, _endpoint);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HandWindowCanceller, null, _cancel);
        Interlocked.CompareExchange(ref Sts2SceneAnimationCallbacks.HasOpenWindow, null, _hasWindow);
    }
}
