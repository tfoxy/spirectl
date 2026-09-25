#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2SceneAnimationBindingTests
{
    [Fact]
    public void ReplacingBindingThenDisposingOlderOwnerPreservesEveryNewCallback()
    {
        using var older = Create();
        using var newer = Create();
        var endpoint = Sts2SceneAnimationCallbacks.TweenEndpointResolver;
        var cancel = Sts2SceneAnimationCallbacks.TweenWindowCanceller;
        var shuffle = Sts2SceneAnimationCallbacks.ShuffleResolver;
        var discard = Sts2SceneAnimationCallbacks.DiscardResolver;
        var window = Sts2SceneAnimationCallbacks.HasOpenWindow;
        older.Dispose();
        Assert.Same(endpoint, Sts2SceneAnimationCallbacks.TweenEndpointResolver);
        Assert.Same(endpoint, Sts2SceneAnimationCallbacks.HandEndpointResolver);
        Assert.Same(cancel, Sts2SceneAnimationCallbacks.TweenWindowCanceller);
        Assert.Same(cancel, Sts2SceneAnimationCallbacks.HandWindowCanceller);
        Assert.Same(shuffle, Sts2SceneAnimationCallbacks.ShuffleResolver);
        Assert.Same(discard, Sts2SceneAnimationCallbacks.DiscardResolver);
        Assert.Same(window, Sts2SceneAnimationCallbacks.HasOpenWindow);
        Assert.True(window!(1));
        newer.Dispose();
        Assert.Null(Sts2SceneAnimationCallbacks.TweenEndpointResolver);
        Assert.Null(Sts2SceneAnimationCallbacks.TweenWindowCanceller);
        Assert.Null(Sts2SceneAnimationCallbacks.HandEndpointResolver);
        Assert.Null(Sts2SceneAnimationCallbacks.HandWindowCanceller);
        Assert.Null(Sts2SceneAnimationCallbacks.HasOpenWindow);
        Assert.Null(Sts2SceneAnimationCallbacks.ShuffleResolver);
        Assert.Null(Sts2SceneAnimationCallbacks.DiscardResolver);
    }

    [Fact]
    public void CapturedCallbacksFailOpenAfterDisposal()
    {
        var cancellations = 0;
        using var binding = new Sts2SceneAnimationBinding(
            (_, _, _) => throw new Exception("disposed endpoint invoked"),
            (_, _) => cancellations++,
            _ => throw new Exception("disposed shuffle invoked"),
            _ => throw new Exception("disposed discard invoked"),
            _ => true);
        var endpoint = Sts2SceneAnimationCallbacks.TweenEndpointResolver!;
        var cancel = Sts2SceneAnimationCallbacks.TweenWindowCanceller!;
        var shuffle = Sts2SceneAnimationCallbacks.ShuffleResolver!;
        var discard = Sts2SceneAnimationCallbacks.DiscardResolver!;
        var window = Sts2SceneAnimationCallbacks.HasOpenWindow!;
        cancel([], false);
        binding.Dispose();
        cancel([], true);
        Assert.Equal(1, cancellations);
        Assert.Null(endpoint(1, default, 10));
        Assert.Null(shuffle(default));
        Assert.Null(discard(default));
        Assert.False(window(1));
    }

    [Fact]
    public void DormantBindingDetachesAndCanResumeWithoutRevivingAnOldOwner()
    {
        using var binding = new Sts2SceneAnimationBinding(
            (_, _, _) => new TweenEndpoint(),
            (_, _) => { },
            _ => null,
            _ => null,
            _ => true,
            activate: false);

        Assert.Null(Sts2SceneAnimationCallbacks.TweenEndpointResolver);
        binding.Activate();
        var activeResolver = Sts2SceneAnimationCallbacks.TweenEndpointResolver;
        Assert.NotNull(activeResolver);

        binding.Deactivate();
        Assert.Null(Sts2SceneAnimationCallbacks.TweenEndpointResolver);
        Assert.Null(activeResolver!(1, default, 1));

        binding.Activate();
        Assert.Same(activeResolver, Sts2SceneAnimationCallbacks.TweenEndpointResolver);
        Assert.NotNull(activeResolver(1, default, 1));

        using var replacement = Create();
        binding.Deactivate();
        Assert.NotSame(activeResolver, Sts2SceneAnimationCallbacks.TweenEndpointResolver);
    }

    private static Sts2SceneAnimationBinding Create() => new(
        (_, _, _) => null, (_, _) => { }, _ => null, _ => null, _ => true);
}
#endif
