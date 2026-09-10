using Godot;

namespace Spirectl.Sts2.Live;

internal sealed class Sts2BridgeRuntimeInstrumentation : ISts2RuntimeInstrumentation
{
    public bool ProducerProfilingEnabled =>
        string.Equals(System.Environment.GetEnvironmentVariable(Sts2ProducerWalkProfile.ProfileEnvVar), "1", StringComparison.Ordinal);

    public bool StateWatchProfilingEnabled => Sts2StateWatchProfile.ProfilingEnabled;

    public ISts2ProducerWalkAccumulator CreateProducerWalkAccumulator() => new ProducerAccumulator();

    public void MarkProducerProfilingEnabled(bool enabled) => Sts2ProducerWalkProfile.MarkProfilingEnabled(enabled);
    public void RecordStateRevisionWake() => Sts2StateWatchProfile.RecordRevisionWake();
    public void RecordStateSkippedByBackoff(int count) => Sts2StateWatchProfile.RecordSkippedByBackoff(count);
    public void RecordStateCapture(double captureMs, double fingerprintMs, bool emitted) => Sts2StateWatchProfile.RecordCapture(captureMs, fingerprintMs, emitted);

    private sealed class ProducerAccumulator : ISts2ProducerWalkAccumulator
    {
        private readonly Sts2ProducerWalkProfile.Accumulator _inner = new();
        public void RecordCapture(long nowMs, double captureMs, bool emitted) => _inner.RecordCapture(nowMs, captureMs, emitted);
        public void RecordNode(int category, long ticks) => _inner.RecordNode(category, ticks);
        public void RecordSkelElided() => _inner.RecordSkelElided();
        public void RecordPrefixRefresh(long ticks) => _inner.RecordPrefixRefresh(ticks);
        public void RecordSuppressWindow() => _inner.RecordSuppressWindow();
        public void RecordSuppressOpacityWindow() => _inner.RecordSuppressOpacityWindow();
        public void RecordSuppressDrop() => _inner.RecordSuppressDrop();
        public void RecordSuppressOpacityDrop() => _inner.RecordSuppressOpacityDrop();

        public void CompleteWindow(long nowMs, long windowMs, int nodes, int prefixes)
        {
            if (!_inner.TryCloseWindow(nowMs, windowMs, nodes, prefixes, out var snapshot))
            {
                return;
            }

            foreach (var line in Sts2ProducerWalkProfile.FormatLogLines(snapshot))
            {
                GD.Print(line);
            }

            Sts2ProducerWalkProfile.Publish(snapshot);
        }
    }
}
