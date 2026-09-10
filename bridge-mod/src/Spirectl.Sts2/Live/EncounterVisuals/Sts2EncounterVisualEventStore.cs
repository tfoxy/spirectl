using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live.EncounterVisuals;

public sealed class Sts2EncounterVisualEventStore(int capacity = Sts2EncounterVisualEventStore.DefaultCapacity)
{
    public const int DefaultCapacity = 32;

    private readonly Lock _gate = new();
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly Queue<EncounterVisualTransitionEventSnapshot> _events = new();
    private ulong _nextSequence;

    public static Sts2EncounterVisualEventStore Shared { get; } = new();

    public EncounterVisualTransitionEventSnapshot Record(
        string packageId,
        string transitionId,
        IReadOnlyList<string> affectedPartIds,
        string activeStateId,
        string sourceHook)
    {
        lock (_gate)
        {
            var evt = new EncounterVisualTransitionEventSnapshot(
                ++_nextSequence,
                packageId,
                transitionId,
                affectedPartIds,
                activeStateId,
                sourceHook);
            _events.Enqueue(evt);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
            }

            return evt;
        }
    }

    public IReadOnlyList<EncounterVisualTransitionEventSnapshot> Recent()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    public IReadOnlyList<EncounterVisualTransitionEventSnapshot> Recent(string packageId)
    {
        lock (_gate)
        {
            return
            [
                .. _events.Where(evt => string.Equals(evt.PackageId, packageId, StringComparison.Ordinal)),
            ];
        }
    }

    public string? LatestActiveStateForPart(string partId)
    {
        lock (_gate)
        {
            return _events
                .Reverse()
                .FirstOrDefault(evt => evt.AffectedPartIds.Contains(partId, StringComparer.Ordinal))
                ?.ActiveStateId;
        }
    }

    public string? LatestActiveStateForPart(string packageId, string partId)
    {
        lock (_gate)
        {
            return _events
                .Reverse()
                .FirstOrDefault(evt =>
                    string.Equals(evt.PackageId, packageId, StringComparison.Ordinal)
                    && evt.AffectedPartIds.Contains(partId, StringComparer.Ordinal))
                ?.ActiveStateId;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _nextSequence = 0;
        }
    }
}
