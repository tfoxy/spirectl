using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class InMemoryLogStreamTests
{
    [Fact]
    public void ReadReturnsLatestMatchingEntriesInChronologicalOrder()
    {
        var stream = new InMemoryLogStream(capacity: 5, source: DataSourceKind.Live, provisional: false);
        stream.Write(BridgeLogLevel.Debug, "bridge.state", "state-alpha");
        stream.Write(BridgeLogLevel.Warn, "bridge.action", "action-1");
        stream.Write(BridgeLogLevel.Error, "bridge.action", "action-2");

        var result = stream.Read(new LogQuery(
            Limit: 2,
            AfterCursor: null,
            MinimumLevel: BridgeLogLevel.Warn,
            TargetFilter: "bridge.action"));

        Assert.Equal(DataSourceKind.Live, result.Source);
        Assert.False(result.Provisional);
        Assert.Collection(result.Entries,
            entry => Assert.Equal("action-1", entry.Message),
            entry => Assert.Equal("action-2", entry.Message));
    }

    [Fact]
    public void ReadWithoutCursorReturnsTailAndLatestCursor()
    {
        var stream = new InMemoryLogStream(capacity: 5, source: DataSourceKind.Live, provisional: false);
        stream.Write(BridgeLogLevel.Debug, "bridge.state", "state-alpha");
        stream.Write(BridgeLogLevel.Warn, "bridge.action", "action-1");
        stream.Write(BridgeLogLevel.Error, "bridge.action", "action-2");

        var result = stream.Read(new LogQuery(
            Limit: 2,
            AfterCursor: null,
            MinimumLevel: null,
            TargetFilter: null));

        Assert.Equal((ulong)4, result.NextCursor);
        Assert.Collection(result.Entries,
            entry =>
            {
                Assert.Equal((ulong)3, entry.Cursor);
                Assert.Equal("action-1", entry.Message);
            },
            entry =>
            {
                Assert.Equal((ulong)4, entry.Cursor);
                Assert.Equal("action-2", entry.Message);
            });
    }

    [Fact]
    public void ReadWithCursorReturnsNextChronologicalWindow()
    {
        var stream = new InMemoryLogStream(capacity: 6, source: DataSourceKind.Live, provisional: false);
        stream.Write(BridgeLogLevel.Debug, "bridge.state", "state-alpha");
        stream.Write(BridgeLogLevel.Warn, "bridge.action", "action-1");
        stream.Write(BridgeLogLevel.Error, "bridge.action", "action-2");
        stream.Write(BridgeLogLevel.Info, "bridge.state", "state-beta");

        var result = stream.Read(new LogQuery(
            Limit: 2,
            AfterCursor: 2,
            MinimumLevel: null,
            TargetFilter: null));

        Assert.Equal((ulong)4, result.NextCursor);
        Assert.Collection(result.Entries,
            entry =>
            {
                Assert.Equal((ulong)3, entry.Cursor);
                Assert.Equal("action-1", entry.Message);
            },
            entry =>
            {
                Assert.Equal((ulong)4, entry.Cursor);
                Assert.Equal("action-2", entry.Message);
            });
    }

    [Fact]
    public void WriteEvictsOldestEntriesWhenCapacityIsExceeded()
    {
        var stream = new InMemoryLogStream(capacity: 2);
        stream.Write(BridgeLogLevel.Info, "bridge.bootstrap", "one");
        stream.Write(BridgeLogLevel.Info, "bridge.bootstrap", "two");
        stream.Write(BridgeLogLevel.Info, "bridge.bootstrap", "three");

        var result = stream.Read(new LogQuery(
            Limit: 10,
            AfterCursor: null,
            MinimumLevel: null,
            TargetFilter: null));

        Assert.Collection(result.Entries,
            entry => Assert.Equal("two", entry.Message),
            entry => Assert.Equal("three", entry.Message));
    }
}
