using Godot;

namespace Spirectl.Sts2.Live;

/// <summary>Optional bridge diagnostics for the live geoclip baker; embedded runtimes use the no-op default.</summary>
internal interface ISts2GeoClipDiagnostics
{
    bool Armed { get; }
    bool Suppressed { get; set; }
    long Skipped { get; }
    Task RunAsync(Viewport rootViewport, ulong? validMeshRid, Action<string> log, Func<int, Task> awaitFrames);
    IDisposable Arm(Action<string> log);
}

internal static class Sts2GeoClipDiagnostics
{
    internal static ISts2GeoClipDiagnostics Current { get; private set; } = NoOp.Instance;
    internal static IDisposable Install(ISts2GeoClipDiagnostics diagnostics)
    {
        var previous = Current;
        Current = diagnostics;
        return new Scope(previous);
    }

    private sealed class Scope(ISts2GeoClipDiagnostics previous) : IDisposable
    {
        private ISts2GeoClipDiagnostics? _previous = previous;
        public void Dispose() { if (_previous is { } previous) { Current = previous; _previous = null; } }
    }

    private sealed class NoOp : ISts2GeoClipDiagnostics
    {
        internal static readonly NoOp Instance = new();
        public bool Armed => false;
        public bool Suppressed { get; set; }
        public long Skipped => 0;
        public Task RunAsync(Viewport rootViewport, ulong? validMeshRid, Action<string> log, Func<int, Task> awaitFrames) => Task.CompletedTask;
        public IDisposable Arm(Action<string> log) => EmptyScope.Instance;
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
