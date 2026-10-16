using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Core.SceneInspection;

internal static class TransformCancellationHint
{
    public static TweenAnimationHint? Create(ulong id, IReadOnlyList<double>? globalPose)
    {
        if (id == 0 || globalPose is not { Count: 6 } || globalPose.Any(value => !double.IsFinite(value)))
        {
            return null;
        }

        return new TweenAnimationHint("", ".", "position", null, 0, null, null,
            TargetInstanceId: id, CancelTransform: globalPose.ToArray());
    }
}
