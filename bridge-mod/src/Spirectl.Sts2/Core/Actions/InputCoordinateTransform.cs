namespace Spirectl.Sts2.Core.Actions;

internal readonly record struct InputCoordinateTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    public static InputCoordinateTransform Identity => new(1, 1, 0, 0);

    public (double X, double Y) CanvasToWindow(double x, double y)
        => (x * ScaleX + OffsetX, y * ScaleY + OffsetY);

    public (double X, double Y) WindowToCanvas(double x, double y)
        => ((x - OffsetX) / ScaleX, (y - OffsetY) / ScaleY);

    public (double X, double Y) ToWindow(double x, double y, MouseCoordinateSpaceKind space)
        => space switch
        {
            MouseCoordinateSpaceKind.Window => (x, y),
            MouseCoordinateSpaceKind.Canvas or MouseCoordinateSpaceKind.Unspecified => CanvasToWindow(x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
}

internal static class MouseClickInjection
{
    // Stop at the first revoked edge so a cancelled gesture never reports success.
    public static bool TryInject(Func<bool> motion, Func<bool, bool> button, bool? pressed, int count)
    {
        if (!motion()) return false;
        if (pressed is { } edge) return button(edge);
        for (var i = 0; i < count; i++)
        {
            if (!button(true) || !button(false)) return false;
        }
        return true;
    }
}
