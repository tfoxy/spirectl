namespace Spirectl.Sts2.Live;

internal interface ISts2SpineDiagnostics
{
    bool Enabled { get; }
    void Log(string message);
}

internal static class Sts2SpineDiagnostics
{
    internal static ISts2SpineDiagnostics Current { get; private set; } = NoOp.Instance;
    internal static IDisposable Install(ISts2SpineDiagnostics diagnostics)
    {
        var previous = Current;
        Current = diagnostics;
        return new Scope(previous);
    }

    private sealed class Scope(ISts2SpineDiagnostics previous) : IDisposable
    {
        private ISts2SpineDiagnostics? _previous = previous;
        public void Dispose() { if (_previous is { } previous) { Current = previous; _previous = null; } }
    }

    private sealed class NoOp : ISts2SpineDiagnostics
    {
        internal static readonly NoOp Instance = new();
        public bool Enabled => false;
        public void Log(string message) { }
    }
}
