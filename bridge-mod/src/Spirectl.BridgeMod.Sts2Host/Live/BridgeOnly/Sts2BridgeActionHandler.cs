using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2.Live;

/// <summary>Bridge-only router for explicit development verbs; embedded runtime exposes reusable actions only.</summary>
public sealed class Sts2BridgeActionHandler(IActionHandler reusable, IActionHandler development) : IActionHandler
{
    public ActionExecutionResult Execute(SemanticActionRequest request)
        => IsDevelopment(request.Kind) ? development.Execute(request) : reusable.Execute(request);

    private static bool IsDevelopment(SemanticActionKind kind) => kind is
        SemanticActionKind.DebugApplyGodMode or SemanticActionKind.DebugSetSeed or SemanticActionKind.DebugStartRun or
        SemanticActionKind.DebugAdvance or SemanticActionKind.DebugPlayCombat or SemanticActionKind.DebugTravel or
        SemanticActionKind.DebugResolveEvent or SemanticActionKind.DebugOpenShop or SemanticActionKind.DebugUsePotions or
        SemanticActionKind.DebugSpeedFast or SemanticActionKind.Heal;
}
