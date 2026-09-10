using Godot;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;


public sealed class Sts2ScreenshotProvider : IScreenshotProvider
{
    private readonly Sts2ScreenLocator _screenLocator;
    private readonly ILogStream _logStream;

    public Sts2ScreenshotProvider(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream)
    {
        _screenLocator = screenLocator;
        _logStream = logStream;
    }

    public ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => CaptureOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.artifacts", $"Screenshot capture failed: {ex}");
            return ScreenshotCaptureResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: ScreenshotFailureCode.RuntimeFailure,
                message: "failed to capture a screenshot from the live bridge host.",
                details:
                [
                    new ScreenshotCaptureDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }

    private ScreenshotCaptureResult CaptureOnMainThread(ScreenshotCaptureRequest request)
    {
        var screen = _screenLocator.Locate();
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return ScreenshotCaptureResult.Failure(
                source: DataSourceKind.Live,
                provisional: false,
                code: ScreenshotFailureCode.BridgeNotAttached,
                message: "the live bridge host could not resolve the game viewport for screenshot capture.",
                details:
                [
                    new ScreenshotCaptureDetail(
                        Field: "viewport",
                        Value: "root",
                        Note: "Engine.GetMainLoop() did not expose a SceneTree root viewport."),
                ]);
        }

        var requestedSize = ResolveRequestedSize(request);
        if (requestedSize.Error is not null)
        {
            return requestedSize.Error;
        }

        var window = rootViewport.GetWindow();
        var originalSize = window?.Size;

        if (window is not null && requestedSize.Size is not null)
        {
            window.Size = requestedSize.Size.Value;
            RenderingServer.ForceDraw();
        }

        try
        {
            var texture = rootViewport.GetTexture();
            var image = texture?.GetImage();
            if (image is null || image.IsEmpty())
            {
                return ScreenshotCaptureResult.Failure(
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: ScreenshotFailureCode.RuntimeFailure,
                    message: "the live bridge host returned an empty viewport image.",
                    details:
                    [
                        new ScreenshotCaptureDetail(
                            Field: "screen",
                            Value: screen.ScreenType,
                            Note: $"screenInstanceId={screen.ScreenInstanceId}"),
                    ]);
            }

            var contents = image.SavePngToBuffer();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.artifacts",
                $"Captured screenshot for screen '{screen.ScreenType}' at {image.GetWidth()}x{image.GetHeight()}.");

            return ScreenshotCaptureResult.Success(
                source: DataSourceKind.Live,
                provisional: false,
                format: "png",
                width: image.GetWidth(),
                height: image.GetHeight(),
                contents: contents,
                screenType: screen.ScreenType,
                screenInstanceId: screen.ScreenInstanceId,
                requestedViewportWidth: request.ViewportWidth,
                requestedViewportHeight: request.ViewportHeight,
                appliedViewportWidth: image.GetWidth(),
                appliedViewportHeight: image.GetHeight(),
                restoredViewport: requestedSize.Size is not null && window is not null && originalSize is not null,
                restoredViewportWidth: requestedSize.Size is not null ? originalSize?.X : null,
                restoredViewportHeight: requestedSize.Size is not null ? originalSize?.Y : null);
        }
        finally
        {
            if (window is not null && requestedSize.Size is not null && originalSize is not null)
            {
                window.Size = originalSize.Value;
                RenderingServer.ForceDraw();
            }
        }
    }

    private static (Vector2I? Size, ScreenshotCaptureResult? Error) ResolveRequestedSize(ScreenshotCaptureRequest request)
    {
        var hasWidth = request.ViewportWidth is not null;
        var hasHeight = request.ViewportHeight is not null;
        if (hasWidth != hasHeight)
        {
            return (
                null,
                ScreenshotCaptureResult.Failure(
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: ScreenshotFailureCode.InvalidViewport,
                    message: "viewport screenshot overrides require both width and height.",
                    details:
                    [
                        new ScreenshotCaptureDetail(
                            Field: "viewport",
                            Value: $"{request.ViewportWidth?.ToString() ?? "null"}x{request.ViewportHeight?.ToString() ?? "null"}",
                            Note: "Provide both viewportWidth and viewportHeight together."),
                    ]));
        }

        if (!hasWidth)
        {
            return (null, null);
        }

        if (request.ViewportWidth <= 0 || request.ViewportHeight <= 0)
        {
            return (
                null,
                ScreenshotCaptureResult.Failure(
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: ScreenshotFailureCode.InvalidViewport,
                    message: "viewport screenshot overrides must be greater than zero.",
                    details:
                    [
                        new ScreenshotCaptureDetail(
                            Field: "viewport",
                            Value: $"{request.ViewportWidth}x{request.ViewportHeight}",
                            Note: "Use positive pixel dimensions."),
                    ]));
        }

        return (new Vector2I(request.ViewportWidth!.Value, request.ViewportHeight!.Value), null);
    }
}
