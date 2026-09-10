namespace Spirectl.Sts2.Core.Debugging;


public sealed class InMemoryDebugEventStore
{
    public const uint DefaultRetentionLimit = 256;

    private readonly object _sync = new();
    private readonly DebugEventSnapshot?[] _events;
    private ulong _nextSequence = 1;
    private int _count;
    private int _start;

    public InMemoryDebugEventStore(uint retentionLimit = DefaultRetentionLimit)
    {
        RetentionLimit = Math.Max(1u, retentionLimit);
        _events = new DebugEventSnapshot[RetentionLimit];
    }

    public uint RetentionLimit { get; }

    public DebugEventSnapshot Append(DebugEventAppendSnapshot append)
    {
        lock (_sync)
        {
            var evt = new DebugEventSnapshot(
                Sequence: _nextSequence++,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind: append.Kind,
                SessionId: append.SessionId,
                SessionRole: append.SessionRole,
                Notices: append.Notices ?? [],
                StatusChanged: append.StatusChanged,
                BreakpointHit: append.BreakpointHit,
                PauseDetail: append.PauseDetail,
                ResumeDetail: append.ResumeDetail,
                Step: append.Step,
                LeaseChanged: append.LeaseChanged,
                SessionChanged: append.SessionChanged,
                StateChanged: append.StateChanged);

            var writeIndex = (_start + _count) % _events.Length;
            if (_count == _events.Length)
            {
                writeIndex = _start;
                _start = (_start + 1) % _events.Length;
            }
            else
            {
                _count += 1;
            }

            _events[writeIndex] = evt;
            return evt;
        }
    }

    public DebugEventStreamResultSnapshot Replay(DebugEventStreamRequestSnapshot request)
    {
        lock (_sync)
        {
            var oldest = _count == 0 ? _nextSequence : _events[_start]!.Sequence;
            var newest = _nextSequence - 1;
            var requested = request.FromSequence == 0 ? oldest : request.FromSequence;
            var expired = _count > 0 && requested < oldest;
            var effectiveFrom = expired ? oldest : requested;
            var limit = Math.Max(1u, request.Limit == 0 ? RetentionLimit : request.Limit);
            var events = new List<DebugEventSnapshot>((int)Math.Min(limit, RetentionLimit));

            for (var offset = 0; offset < _count && events.Count < limit; offset += 1)
            {
                var evt = _events[(_start + offset) % _events.Length]!;
                if (evt.Sequence < effectiveFrom)
                {
                    continue;
                }

                events.Add(evt);
            }

            var next = events.Count > 0 ? events[^1].Sequence + 1 : effectiveFrom;
            var overflow = events.Count == limit && next <= newest;
            var notices = new List<DebugNoticeSnapshot>();
            if (expired)
            {
                notices.Add(new DebugNoticeSnapshot(
                    "debug_event_replay_expired",
                    $"Requested debug event sequence {requested} is older than the oldest retained sequence {oldest}."));
            }

            if (overflow)
            {
                notices.Add(new DebugNoticeSnapshot(
                    "debug_event_replay_limited",
                    $"Debug event replay was limited to {limit} events; request again from sequence {next}."));
            }

            return new DebugEventStreamResultSnapshot(
                Events: events,
                FromSequence: effectiveFrom,
                NextSequence: next,
                Retention: new DebugEventRetentionSnapshot(oldest, newest, RetentionLimit),
                Expired: expired,
                Overflow: overflow,
                Notices: notices);
        }
    }
}
