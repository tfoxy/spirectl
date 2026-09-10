namespace Spirectl.Sts2.Core.Perspective;

public sealed class DefaultPerspectiveProvider : IPerspectiveProvider
{
    public PlayerPerspective GetDefaultPerspective()
    {
        return new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true);
    }

    public PlayerPerspective Resolve(PerspectiveSelection? requested)
    {
        if (requested is null)
        {
            return GetDefaultPerspective();
        }

        return new PlayerPerspective(requested.Scope, requested.PlayerId, UsesDefault: false);
    }
}
