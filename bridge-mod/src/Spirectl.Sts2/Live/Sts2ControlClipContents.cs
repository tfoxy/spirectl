using Godot;

namespace Spirectl.Sts2.Live;

/// <summary>Single typed read of a control's rectangular child-clipping setting.</summary>
internal static class Sts2ControlClipContents
{
    internal static bool? Read(Node node)
        => node is Control control ? control.ClipContents : null;
}
