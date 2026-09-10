namespace Spirectl.Sts2.Core.Logging;

using Spirectl.Sts2.Core.Protocol;

public enum BridgeLogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogQuery(int Limit, ulong? AfterCursor, BridgeLogLevel? MinimumLevel, string? TargetFilter);

public sealed record LogRecord(ulong Cursor, BridgeLogLevel Level, string Target, string Message);

public sealed record LogReadResult(
    DataSourceKind Source,
    bool Provisional,
    IReadOnlyList<LogRecord> Entries,
    ulong NextCursor);
