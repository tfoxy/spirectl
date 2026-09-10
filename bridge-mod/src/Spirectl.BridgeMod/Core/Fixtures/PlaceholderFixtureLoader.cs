using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Fixtures;


public sealed class PlaceholderFixtureLoader : IFixtureLoader
{
    public FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
    {
        return FixtureLoadResult.Failure(
            requestId: request.RequestId,
            fixtureName: request.FixtureName,
            sourcePath: request.SourcePath,
            source: DataSourceKind.Stub,
            provisional: true,
            code: FixtureLoadFailureCode.NotImplemented,
            message: "fixture loading requires the live STS2 bridge host.",
            details:
            [
                new FixtureLoadDetail(
                    Field: "command",
                    Value: "dev fixture load",
                    Note: "Retry through transport.kind=ipc after launching or attaching the live bridge."),
            ]);
    }
}
