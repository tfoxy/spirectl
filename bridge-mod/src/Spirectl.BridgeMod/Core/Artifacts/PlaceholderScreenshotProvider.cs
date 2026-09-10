using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Artifacts;


public sealed class PlaceholderScreenshotProvider : IScreenshotProvider
{
    public ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request)
    {
        return ScreenshotCaptureResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: ScreenshotFailureCode.NotImplemented,
            message: "screenshot capture requires the live STS2 bridge host.",
            details:
            [
                new ScreenshotCaptureDetail(
                    Field: "command",
                    Value: "dev screenshot",
                    Note: "Retry through transport.kind=ipc after launching or attaching the live bridge."),
            ]);
    }
}
