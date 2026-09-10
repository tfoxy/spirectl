namespace Spirectl.Sts2.Core.SceneInspection;

// Stateful, incremental scene-tree watcher: the high-frequency "mirror" transport. Unlike
// IRuntimeSceneProvider (stateless, full-tree request/response), a watcher tracks the live tree over time
// and pushes DELTAS to subscribers. The live implementation lives in Live/ (Godot-dependent, live-host
// only); non-live builds use PlaceholderRuntimeSceneWatcher.
public interface IRuntimeSceneWatcher
{
    // Subscribe to scene deltas. The watcher emits a Full keyframe to each new subscriber, then
    // incremental deltas as the tree changes. Returns a disposable that unsubscribes.
    IDisposable Subscribe(Action<RuntimeSceneDelta> onDelta);
}

// No-op watcher for builds/runtimes without a live STS2 host: never emits (the scene-watch capability is
// effectively absent, so the couch-coop observer simply never receives deltas).
public sealed class PlaceholderRuntimeSceneWatcher : IRuntimeSceneWatcher
{
    public IDisposable Subscribe(Action<RuntimeSceneDelta> onDelta) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
