namespace Spirectl.Sts2;

public static class Sts2ScreenPriority
{
    public static bool ShouldOpenMapOverrideScreen(string? screenType, bool isMapOpen)
        => ShouldOpenMapOverrideScreen(screenType, isMapOpen, overlayPolicy: null);

    public static bool ShouldOpenMapOverrideScreen(string? screenType, bool isMapOpen, string? overlayPolicy)
        => isMapOpen && !IsBlockingOverlayPolicy(overlayPolicy);

    public static bool ShouldOverlayOverrideScreen(string? overlayPolicy, string? retainedScreenType)
        => IsBlockingOverlayPolicy(overlayPolicy) || string.IsNullOrWhiteSpace(retainedScreenType);

    public static bool ShouldRetainUnderlyingScreen(string? overlayPolicy, string? retainedScreenType)
        => IsPassiveOverlayPolicy(overlayPolicy) && !string.IsNullOrWhiteSpace(retainedScreenType);

    private static bool IsBlockingOverlayPolicy(string? overlayPolicy)
        => string.Equals(overlayPolicy, "blocking", StringComparison.Ordinal)
            || string.Equals(overlayPolicy, "unsupported", StringComparison.Ordinal);

    private static bool IsPassiveOverlayPolicy(string? overlayPolicy)
        => string.Equals(overlayPolicy, "passive", StringComparison.Ordinal);
}
