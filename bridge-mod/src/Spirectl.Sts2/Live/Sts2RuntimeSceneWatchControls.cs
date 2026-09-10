using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>Publishes only safe, coupled scene-watch controls to embedding hosts.</summary>
public sealed class Sts2RuntimeSceneWatchControls : IRuntimeSceneWatchControls
{
    public static Sts2RuntimeSceneWatchControls Instance { get; } = new();

    private Sts2RuntimeSceneWatchControls()
    {
    }

    public void SetTweenReplayEnabled(bool enabled)
    {
        Sts2SceneWatchRuntimeSettings.SuppressTweenedTransforms = enabled;
        Sts2SceneWatchRuntimeSettings.SuppressTweenedOpacity = enabled;
    }

    public void SetCardFlightReplayEnabled(bool enabled)
        => Sts2SceneWatchRuntimeSettings.CardFlightHints = enabled;

    public void SetHandTweenReplayEnabled(bool enabled)
        => Sts2SceneWatchRuntimeSettings.HandTweenHints = enabled;

    public void SetTrailReplayEnabled(bool enabled)
        => Sts2SceneWatchRuntimeSettings.TrailSelfSuppressLocalMode = enabled;
}

public sealed class UnsupportedRuntimeSceneWatchControls(string message) : IRuntimeSceneWatchControls
{
    public static UnsupportedRuntimeSceneWatchControls Instance { get; } = new("Runtime scene-watch controls require a live STS2 host.");
    public void SetTweenReplayEnabled(bool enabled) => Throw();
    public void SetCardFlightReplayEnabled(bool enabled) => Throw();
    public void SetHandTweenReplayEnabled(bool enabled) => Throw();
    public void SetTrailReplayEnabled(bool enabled) => Throw();

    private void Throw() => throw new NotSupportedException(message);
}
