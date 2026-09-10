namespace Spirectl.Sts2.Core.Perspective;

public sealed record PerspectiveSelection(PlayerScope Scope, string? PlayerId);

public sealed record PlayerPerspective(PlayerScope Scope, string? PlayerId, bool UsesDefault);

public enum PlayerScope
{
    Local,
    Omniscient,
}
