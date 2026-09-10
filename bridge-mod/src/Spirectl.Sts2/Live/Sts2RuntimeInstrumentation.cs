namespace Spirectl.Sts2.Live;

/// <summary>Optional bridge diagnostics; the embedded runtime retains no profiler/report implementation.</summary>
internal interface ISts2RuntimeInstrumentation
{
    bool ProducerProfilingEnabled { get; }
    bool StateWatchProfilingEnabled { get; }
    ISts2ProducerWalkAccumulator CreateProducerWalkAccumulator();
    void MarkProducerProfilingEnabled(bool enabled);
    void RecordStateRevisionWake();
    void RecordStateSkippedByBackoff(int count);
    void RecordStateCapture(double captureMs, double fingerprintMs, bool emitted);
}

internal interface ISts2ProducerWalkAccumulator
{
    void RecordCapture(long nowMs, double captureMs, bool emitted);
    void CompleteWindow(long nowMs, long windowMs, int nodes, int prefixes);
    void RecordNode(int category, long ticks);
    void RecordSkelElided();
    void RecordPrefixRefresh(long ticks);
    void RecordSuppressWindow();
    void RecordSuppressOpacityWindow();
    void RecordSuppressDrop();
    void RecordSuppressOpacityDrop();
}

internal static class Sts2RuntimeInstrumentation
{
    internal static ISts2RuntimeInstrumentation Current { get; private set; } = NoOp.Instance;
    internal static ISts2RuntimeInstrumentation None => NoOp.Instance;

    internal static IDisposable Install(ISts2RuntimeInstrumentation instrumentation)
    {
        ArgumentNullException.ThrowIfNull(instrumentation);
        var previous = Current;
        Current = instrumentation;
        return new Scope(previous);
    }

    private sealed class Scope(ISts2RuntimeInstrumentation previous) : IDisposable
    {
        private ISts2RuntimeInstrumentation? _previous = previous;
        public void Dispose()
        {
            if (_previous is { } previous)
            {
                Current = previous;
                _previous = null;
            }
        }
    }

    private sealed class NoOp : ISts2RuntimeInstrumentation, ISts2ProducerWalkAccumulator
    {
        internal static readonly NoOp Instance = new();
        public bool ProducerProfilingEnabled => false;
        public bool StateWatchProfilingEnabled => false;
        public ISts2ProducerWalkAccumulator CreateProducerWalkAccumulator() => this;
        public void MarkProducerProfilingEnabled(bool enabled) { }
        public void RecordStateRevisionWake() { }
        public void RecordStateSkippedByBackoff(int count) { }
        public void RecordStateCapture(double captureMs, double fingerprintMs, bool emitted) { }
        public void RecordCapture(long nowMs, double captureMs, bool emitted) { }
        public void CompleteWindow(long nowMs, long windowMs, int nodes, int prefixes) { }
        public void RecordNode(int category, long ticks) { }
        public void RecordSkelElided() { }
        public void RecordPrefixRefresh(long ticks) { }
        public void RecordSuppressWindow() { }
        public void RecordSuppressOpacityWindow() { }
        public void RecordSuppressDrop() { }
        public void RecordSuppressOpacityDrop() { }
    }
}
