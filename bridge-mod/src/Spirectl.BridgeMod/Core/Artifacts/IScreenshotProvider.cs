using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Artifacts;


public interface IScreenshotProvider
{
    ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request);
}

public sealed record ScreenshotCaptureRequest(
    int? ViewportWidth,
    int? ViewportHeight);

public sealed record ScreenshotCaptureResult(
    DataSourceKind Source,
    bool Provisional,
    string Format,
    int Width,
    int Height,
    byte[] Contents,
    string ScreenType,
    string ScreenInstanceId,
    int? RequestedViewportWidth,
    int? RequestedViewportHeight,
    int AppliedViewportWidth,
    int AppliedViewportHeight,
    bool RestoredViewport,
    int? RestoredViewportWidth,
    int? RestoredViewportHeight,
    ScreenshotCaptureFailure? Error)
{
    public static ScreenshotCaptureResult Success(
        DataSourceKind source,
        bool provisional,
        string format,
        int width,
        int height,
        byte[] contents,
        string screenType,
        string screenInstanceId,
        int? requestedViewportWidth = null,
        int? requestedViewportHeight = null,
        int? appliedViewportWidth = null,
        int? appliedViewportHeight = null,
        bool restoredViewport = false,
        int? restoredViewportWidth = null,
        int? restoredViewportHeight = null)
        => new(
            Source: source,
            Provisional: provisional,
            Format: format,
            Width: width,
            Height: height,
            Contents: contents,
            ScreenType: screenType,
            ScreenInstanceId: screenInstanceId,
            RequestedViewportWidth: requestedViewportWidth,
            RequestedViewportHeight: requestedViewportHeight,
            AppliedViewportWidth: appliedViewportWidth ?? width,
            AppliedViewportHeight: appliedViewportHeight ?? height,
            RestoredViewport: restoredViewport,
            RestoredViewportWidth: restoredViewportWidth,
            RestoredViewportHeight: restoredViewportHeight,
            Error: null);

    public static ScreenshotCaptureResult Failure(
        DataSourceKind source,
        bool provisional,
        ScreenshotFailureCode code,
        string message,
        IReadOnlyList<ScreenshotCaptureDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            Format: "png",
            Width: 0,
            Height: 0,
            Contents: [],
            ScreenType: string.Empty,
            ScreenInstanceId: string.Empty,
            RequestedViewportWidth: null,
            RequestedViewportHeight: null,
            AppliedViewportWidth: 0,
            AppliedViewportHeight: 0,
            RestoredViewport: false,
            RestoredViewportWidth: null,
            RestoredViewportHeight: null,
            Error: new ScreenshotCaptureFailure(code, message, details));
}

public enum ScreenshotFailureCode
{
    NotImplemented,
    BridgeNotAttached,
    InvalidViewport,
    RuntimeFailure,
}

public sealed record ScreenshotCaptureFailure(
    ScreenshotFailureCode Code,
    string Message,
    IReadOnlyList<ScreenshotCaptureDetail> Details);

public sealed record ScreenshotCaptureDetail(
    string Field,
    string Value,
    string Note);
