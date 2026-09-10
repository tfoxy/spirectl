using Godot;

namespace Spirectl.Sts2.Live;

// Converts a point in the game's CANVAS/GUI space (the space the mirror streams — `Control.GetGlobalRect()` and
// the design 1920x1080 coordinates) to the WINDOW-pixel space Godot's input injection (`Input.WarpMouse` +
// `InputEventMouse.Position`) expects. They differ whenever the window size != the content/base size (a smaller
// or differently-shaped game window): the content-scale stretch maps between them. Using the root viewport's
// own screen transform makes this correct for ANY stretch mode/aspect and identity when window == canvas.
internal static class Sts2InputCoordinates
{
    public static Vector2 CanvasToWindow(Vector2 canvasPoint)
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root is Viewport viewport)
            {
                // GetScreenTransform: viewport(canvas) -> containing-window screen pixels, INCLUDING the
                // content-scale stretch. Maps design/GUI coords to the pixels input injection uses.
                var windowPoint = viewport.GetScreenTransform() * canvasPoint;
                if (float.IsFinite(windowPoint.X) && float.IsFinite(windowPoint.Y))
                {
                    return windowPoint;
                }
            }
        }
        catch
        {
            // Fall back to the canvas point (identity) on any failure — matches the prior behavior.
        }

        return canvasPoint;
    }
}
