namespace Spirectl.Sts2.Core.Logging;

public sealed class InMemoryLogStream : ILogStream
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Core.Protocol.DataSourceKind _source;
    private readonly bool _provisional;
    private readonly List<LogRecord> _records;
    private ulong _nextCursor;

    public InMemoryLogStream(
        int capacity = 200,
        Core.Protocol.DataSourceKind source = Core.Protocol.DataSourceKind.Stub,
        bool provisional = true)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be greater than zero.");
        }

        _capacity = capacity;
        _source = source;
        _provisional = provisional;
        _records =
        [
            new(
                1,
                BridgeLogLevel.Info,
                "bridge.bootstrap",
                source == Core.Protocol.DataSourceKind.Live
                    ? "Live bridge log stream initialized."
                    : "Scaffold runtime created."),
        ];
        _nextCursor = 2;
    }

    public void Write(BridgeLogLevel level, string target, string message)
    {
        lock (_lock)
        {
            if (_records.Count == _capacity)
            {
                _records.RemoveAt(0);
            }

            _records.Add(new LogRecord(_nextCursor, level, target, message));
            _nextCursor += 1;
        }
    }

    public LogReadResult Read(LogQuery query)
    {
        lock (_lock)
        {
            var currentCursor = _nextCursor - 1;
            var matchingEntries = _records
                .Where(record => query.MinimumLevel is null || record.Level >= query.MinimumLevel.Value)
                .Where(record => string.IsNullOrWhiteSpace(query.TargetFilter)
                    || record.Target.Contains(query.TargetFilter!, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var entries = query.AfterCursor is ulong afterCursor
                ? matchingEntries
                    .Where(record => record.Cursor > afterCursor)
                    .Take(query.Limit)
                    .ToArray()
                : matchingEntries
                    .TakeLast(query.Limit)
                    .ToArray();
            var nextCursor = entries.Length > 0 ? entries[^1].Cursor : currentCursor;

            return new LogReadResult(
                Source: _source,
                Provisional: _provisional,
                Entries: entries,
                NextCursor: nextCursor);
        }
    }
}
