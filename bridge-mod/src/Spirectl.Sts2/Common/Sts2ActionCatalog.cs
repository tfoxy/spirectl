using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

public static partial class Sts2ActionCatalog
{
    public const string MainMenuStartRunChoiceId = "menu:start-run";

    public static bool CanSelectMapNode(
        Sts2MapNodeSnapshot? node,
        bool isTravelEnabled,
        bool isTraveling)
    {
        return node is not null
            && node.Travelable
            && isTravelEnabled
            && !isTraveling;
    }
}
