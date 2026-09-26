using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    /// <summary>
    /// The embedded profile's dispatcher: an arm for each semantic action kind an embedder sends, and nothing
    /// else. It stands in for <c>Live/Sts2ActionHandler.Dispatch.cs</c>, which routes all of them, and it is
    /// what lets the profile leave the action bodies no embedder reaches out of the assembly. A kind without
    /// an arm answers <see cref="ActionFailureCode.InvalidAction"/>, exactly as an unknown kind does; the
    /// <see cref="SemanticActionKind"/> enum itself stays complete so its wire values never move.
    /// </summary>
    private ActionExecutionResult ExecuteOnMainThread(SemanticActionRequest request)
    {
        try
        {
            return request.Kind switch
            {
                SemanticActionKind.SelectMapNode => ExecuteSelectMapNode(request),
                SemanticActionKind.DisconnectClient => ExecuteDisconnectClient(request),
                SemanticActionKind.SetClientName => ExecuteSetClientName(request),
                SemanticActionKind.ClaimReward => ExecuteClaimReward(request),
                SemanticActionKind.MouseClick => ExecuteMouseClick(request),
                SemanticActionKind.HoverElement => ExecuteHoverElement(request),
                SemanticActionKind.KeyInput => ExecuteKeyInput(request),
                SemanticActionKind.ControllerInput => ExecuteControllerInput(request),
                SemanticActionKind.SetScrollOffset => ExecuteSetScrollOffset(request),
                _ => ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: $"Semantic action kind '{request.Kind}' is not available in the embedded runtime profile.")
            };
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"Action '{request.Kind}' failed with an unhandled exception: {ex}");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: $"Action '{request.Kind}' failed with an unhandled runtime exception.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }
}
