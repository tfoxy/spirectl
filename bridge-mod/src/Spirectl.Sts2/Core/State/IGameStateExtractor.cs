using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.State;

public interface IGameStateExtractor
{
    GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective);
}
