namespace Spirectl.Sts2.Core.Fixtures;


public interface IRecordedFixtureProvider
{
    RecordedFixtureResult Record(RecordedFixtureRequestSnapshot request);
}

public sealed record RecordedFixtureRequestSnapshot(
    string RequestId,
    string PerspectiveScope,
    string PlayerId);
