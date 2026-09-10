namespace Spirectl.Sts2.Core.Perspective;

public interface IPerspectiveProvider
{
    PlayerPerspective GetDefaultPerspective();

    PlayerPerspective Resolve(PerspectiveSelection? requested);
}
