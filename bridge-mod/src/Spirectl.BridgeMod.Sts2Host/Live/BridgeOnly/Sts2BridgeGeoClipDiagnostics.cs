using Godot;

namespace Spirectl.Sts2.Live;

internal sealed class Sts2BridgeGeoClipDiagnostics : ISts2GeoClipDiagnostics
{
    public bool Armed => Sts2SpineGeoClipBacktraceBench.Armed;
    public bool Suppressed
    {
        get => Sts2ScriptBacktraceSuppressor.Suppressed;
        set => Sts2ScriptBacktraceSuppressor.Suppressed = value;
    }

    public long Skipped => Sts2ScriptBacktraceSuppressor.Skipped;

    public Task RunAsync(Viewport rootViewport, ulong? validMeshRid, Action<string> log, Func<int, Task> awaitFrames)
        => Sts2SpineGeoClipBacktraceBench.RunAsync(rootViewport, validMeshRid, log, awaitFrames);

    public IDisposable Arm(Action<string> log) => Sts2ScriptBacktraceSuppressor.Arm(log);
}
