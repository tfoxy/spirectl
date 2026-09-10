using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Scenarios;


public sealed class PlaceholderScenarioProvider : IScenarioProvider
{
    public ScenarioCaptureResultSnapshot Capture(ScenarioCaptureRequestSnapshot request)
    {
        return ScenarioCaptureResultSnapshot.Failure(
            request.RequestId,
            DataSourceKind.Stub,
            provisional: true,
            ScenarioFailureCode.NotImplemented,
            "Scenario export requires the live STS2 bridge host.",
            [new ScenarioFailureDetail("command", "dev scenario export", "Attach to the live bridge before exporting a scenario.")]);
    }

    public ScenarioRestoreResultSnapshot Restore(ScenarioRestoreRequestSnapshot request)
    {
        return ScenarioRestoreResultSnapshot.Failure(
            request.RequestId,
            ScenarioRestoreQuality.Unsupported,
            ScenarioFailureCode.NotImplemented,
            "Scenario load requires the live STS2 bridge host.",
            [new ScenarioFailureDetail("command", "dev scenario load", "Attach to the live bridge before loading a scenario.")]);
    }
}
