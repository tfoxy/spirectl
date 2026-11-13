using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>Show fixture defeat state without persisting run progress.</summary>
internal static class GameApiFixtures
{
    internal static void ShowGameOverScreen(NRun node, SerializableRun run)
        => node.ShowGameOverScreen(run);
}
