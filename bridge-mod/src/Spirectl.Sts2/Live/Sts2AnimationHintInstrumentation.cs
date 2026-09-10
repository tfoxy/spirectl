namespace Spirectl.Sts2.Live;

internal interface ISts2AnimationHintCounter
{
    long Emitted { get; }
    long RecordEmitted();
    void RecordEarlyOutNoSubscribers();
    void RecordEarlyOutLeverOff();
    void RecordEarlyOutNoResolver();
    void RecordResolveNull();
    void RecordPublishFailed();
    void RecordResolveParked();
    void RecordResolveRetryHit();
    void RecordResolveRetryDropped();
    void RecordResolveNotReplayable();
    void RecordResolveSingular();
}

internal interface ISts2AnimationHintInstrumentation
{
    ISts2AnimationHintCounter CardFlight { get; }
    ISts2AnimationHintCounter CardDiscard { get; }
    ISts2AnimationHintCounter HandTween { get; }
}

internal static class Sts2AnimationHintInstrumentation
{
    internal static ISts2AnimationHintInstrumentation Current { get; private set; } = NoOp.Instance;
    internal static IDisposable Install(ISts2AnimationHintInstrumentation instrumentation)
    {
        var previous = Current;
        Current = instrumentation;
        return new Scope(previous);
    }

    private sealed class Scope(ISts2AnimationHintInstrumentation previous) : IDisposable
    {
        private ISts2AnimationHintInstrumentation? _previous = previous;
        public void Dispose() { if (_previous is { } previous) { Current = previous; _previous = null; } }
    }

    private sealed class NoOp : ISts2AnimationHintInstrumentation, ISts2AnimationHintCounter
    {
        internal static readonly NoOp Instance = new();
        public ISts2AnimationHintCounter CardFlight => this;
        public ISts2AnimationHintCounter CardDiscard => this;
        public ISts2AnimationHintCounter HandTween => this;
        public long Emitted => 0;
        public long RecordEmitted() => 0;
        public void RecordEarlyOutNoSubscribers() { }
        public void RecordEarlyOutLeverOff() { }
        public void RecordEarlyOutNoResolver() { }
        public void RecordResolveNull() { }
        public void RecordPublishFailed() { }
        public void RecordResolveParked() { }
        public void RecordResolveRetryHit() { }
        public void RecordResolveRetryDropped() { }
        public void RecordResolveNotReplayable() { }
        public void RecordResolveSingular() { }
    }
}
