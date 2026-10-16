using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class TransformCancellationHintTests
{
    [Fact]
    public void CancellationCarriesASnapshotWithoutArmingOtherChannels()
    {
        double[] pose = [1, 0, 0, 1, 958, 1030];
        var cancel = TransformCancellationHint.Create(42, pose)!;
        pose[4] = 0;
        Assert.Equal(958, cancel.CancelTransform![4]);
        Assert.Equal(0, cancel.DurationMs);
        Assert.Null(cancel.EndTransform);
        Assert.Null(cancel.EndOpacity);
        Assert.Null(cancel.CardFlight);
        var hub = new EmbeddableAnimationHintHub();
        var seen = new List<TweenAnimationHint>();
        using var sub = hub.Subscribe(seen.Add);
        var replacement = cancel with { CancelTransform = null, EndTransform = [1, 0, 0, 1, 1000, 1030], DurationMs = 200 };
        hub.Publish(cancel);
        hub.Publish(replacement);
        Assert.Equal(new[] { cancel, replacement }, seen);
    }

    [Fact]
    public void UnreadablePoseCannotProduceACancellationWithInventedGeometry()
    {
        Assert.Null(TransformCancellationHint.Create(0, [1, 0, 0, 1, 0, 0]));
        Assert.Null(TransformCancellationHint.Create(42, null));
        Assert.Null(TransformCancellationHint.Create(42, [1, 0]));
        Assert.Null(TransformCancellationHint.Create(42, [1, 0, 0, 1, double.NaN, 0]));
    }
}
