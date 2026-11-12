using Godot;
using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2.Live;

internal static class Sts2InputCoordinates
{
    public static InputCoordinateTransform GetTransform()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root is Viewport viewport)
            {
                var transform = viewport.GetScreenTransform();
                if (float.IsFinite(transform.X.X) && float.IsFinite(transform.Y.Y)
                    && transform.X.X > 0 && transform.Y.Y > 0
                    && float.IsFinite(transform.Origin.X) && float.IsFinite(transform.Origin.Y))
                {
                    return new(transform.X.X, transform.Y.Y, transform.Origin.X, transform.Origin.Y);
                }
            }
        }
        catch
        {
            // Preserve identity when the viewport is unavailable.
        }
        return InputCoordinateTransform.Identity;
    }

    public static Vector2 CanvasToWindow(Vector2 point)
        => ToWindow(point, MouseCoordinateSpaceKind.Canvas);

    public static Vector2 ToWindow(Vector2 point, MouseCoordinateSpaceKind space)
    {
        var mapped = GetTransform().ToWindow(point.X, point.Y, space);
        return new Vector2((float)mapped.X, (float)mapped.Y);
    }
}
