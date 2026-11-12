using Spirectl.Sts2.Core.Actions;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    private ActionExecutionResult ExecuteDismissModal(SemanticActionRequest request)
        => Sts2BlockingModalResolver.Dismiss(request, Sts2BlockingModalResolver.ResolveLive());

    private static async Task AwaitModalProcessFrameAsync()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
