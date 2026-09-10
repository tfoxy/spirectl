using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Core.Actions;

public sealed class PlaceholderActionHandler : IActionHandler
{
    public ActionExecutionResult Execute(SemanticActionRequest request)
    {
        var requestedPlayerId = request.Perspective?.PlayerId;
        var remoteOrchestration = OwnershipMetadata.LocalOnlyDegraded(provisional: true);
        var code = string.IsNullOrWhiteSpace(requestedPlayerId)
            ? ActionFailureCode.NotImplemented
            : ActionFailureCode.UnsupportedPerspective;
        var note = string.IsNullOrWhiteSpace(requestedPlayerId)
            ? "Retry after the live bridge action host is implemented."
            : "The scaffold action host cannot own or orchestrate remote semantic action players.";

        return ActionExecutionResult.Failure(
            kind: request.Kind,
            code: code,
            message: code == ActionFailureCode.UnsupportedPerspective
                ? $"Action handling for {request.Kind} is unsupported from the requested player perspective."
                : $"Action handling is not wired yet for {request.Kind}.",
            details:
            [
                new ActionFailureDetail(
                    Field: "action",
                    Value: request.Kind.ToString().ToLowerInvariant(),
                    Note: note,
                    ReasonCode: code,
                    PlayerId: requestedPlayerId,
                    RequestedPlayerId: requestedPlayerId,
                    LocalRole: MultiplayerRoleSnapshot.Unspecified,
                    Action: request.Kind.ToString().ToLowerInvariant(),
                    RemoteOrchestration: remoteOrchestration),
            ],
            playerId: requestedPlayerId,
            requestedPlayerId: requestedPlayerId,
            localRole: MultiplayerRoleSnapshot.Unspecified,
            action: request.Kind.ToString().ToLowerInvariant(),
            remoteOrchestration: remoteOrchestration);
    }
}
