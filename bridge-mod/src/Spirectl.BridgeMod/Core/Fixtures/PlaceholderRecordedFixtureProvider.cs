using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Fixtures;


public sealed class PlaceholderRecordedFixtureProvider : IRecordedFixtureProvider
{
    public RecordedFixtureResult Record(RecordedFixtureRequestSnapshot request)
        => RecordedFixtureResult.Failure(
            request.RequestId,
            DataSourceKind.Stub,
            provisional: true,
            RecordedFixtureFailureCode.NotImplemented,
            "Recorded fixture capture is not available in this bridge host.",
            [
                new FixtureLoadDetail(
                    Field: "command",
                    Value: "dev fixture record",
                    Note: "Install and attach the live STS2 host bridge to record screen fixtures."),
            ]);
}
