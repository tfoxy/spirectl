using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // Force-disconnect a remote ENet client by its netId so the host's net server drops the dead peer
    // (a SIGKILL'd headless leaves its peer registered, holding 1002/1003/1004 — the next headless reusing
    // that netId then fails its ENet join with "connection timed out"). Unlike LeaveLobbyPlayer (which only
    // removes synthetic host-local seats), this evicts the real ENet peer via NetHostGameService.DisconnectClient.
    // The netId arrives via EmbeddableActionRequest.PlayerId (→ request.Perspective?.PlayerId), accepted either
    // as a bare "1002" or the "p:1002" form.
    private ActionExecutionResult ExecuteDisconnectClient(SemanticActionRequest request)
    {
        var requestedNetId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (!TryResolveNetId(requestedNetId, out var netId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "disconnect-client requires a numeric netId.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedNetId ?? string.Empty,
                        Note: "Provide the remote peer's netId, e.g. 1002 or p:1002."),
                ]);
        }

        // The active host NetHostGameService at character-select lobby time lives on StartRunLobby.NetService
        // (RunManager.Instance.NetService is null until a run starts). Fall back to RunManager for in-run use.
        object? netService = null;
        if (TryResolveLobbyContext(out var lobbyContext) && lobbyContext?.StartRunLobby is { } lobby)
        {
            netService = lobby.NetService;
        }
        netService ??= RunManager.Instance?.NetService;

        if (netService is null || !netService.GetType().FullName!.Contains("NetHostGameService", StringComparison.Ordinal))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "disconnect-client is only available on the host's NetHostGameService.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "net_service",
                        Value: netService?.GetType().FullName ?? "null",
                        Note: "StartRunLobby.NetService / RunManager.Instance.NetService is null or not a host net service."),
                ]);
        }

        try
        {

            var netErrorType = Type.GetType("MegaCrit.Sts2.Core.Entities.Multiplayer.NetError, sts2");
            if (netErrorType is null)
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.RuntimeFailure,
                    message: "disconnect-client could not resolve the NetError type.");
            }

            // FindMethod matches by ParameterType.IsInstanceOfType, so these must be REAL instances of the
            // exact parameter types: a NetError enum value, a ulong netId, and a bool. Signature:
            // NetHostGameService.DisconnectClient(ulong peerId, NetError reason, bool now = false).
            var kicked = Enum.Parse(netErrorType, "Kicked");
            Sts2LiveIntrospection.InvokeMethod(netService, "DisconnectClient", netId, kicked, false);

            Console.Error.WriteLine($"[spirectl] disconnect-client evicted ENet peer netId={netId} (Kicked).");
            _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Force-disconnected ENet peer p:{netId}.");
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:disconnect-client:{request.RequestId}",
                kind: request.Kind,
                message: $"Force-disconnected ENet peer p:{netId}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spirectl] disconnect-client failed for netId={netId}: {ex.GetType().Name}: {ex.Message}");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: $"disconnect-client failed for p:{netId}.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }

    // Override the display name shown for a REAL networked client by its netId (e.g. a couch-coop headless
    // ENet peer that joined as 1002 should show the browser-chosen name instead of the platform default "1002").
    // Registers a name override consulted by the PlatformUtil.GetPlayerNameRaw hook (Sts2SyntheticLobbyNameHooks)
    // and the State name resolver, then best-effort refreshes the live lobby nameplate so the host's on-screen
    // lobby updates immediately. Unlike JoinLobbyPlayer this does NOT add a player or mark a host-local seat —
    // the client is a genuine remote player. An empty DisplayName CLEARS the override (used on disconnect).
    private ActionExecutionResult ExecuteSetClientName(SemanticActionRequest request)
    {
        var requestedNetId = request.Perspective?.PlayerId ?? ResolveRequestValue(request, "playerId");
        if (!TryResolveNetId(requestedNetId, out var netId))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "set-client-name requires a numeric netId.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "player_id",
                        Value: requestedNetId ?? string.Empty,
                        Note: "Provide the remote peer's netId, e.g. 1002 or p:1002."),
                ]);
        }

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            Sts2HostLocalSeatRegistry.UnregisterClientName(netId);
            return ActionExecutionResult.Success(
                actionInstanceId: $"action:set-client-name:{request.RequestId}",
                kind: request.Kind,
                message: $"Cleared display-name override for p:{netId}.");
        }

        Sts2HostLocalSeatRegistry.RegisterClientName(netId, displayName);

        // Best-effort: if a start-run lobby is live AND this client's remote-player widget already exists, push
        // the name onto its nameplate now (it was set from PlatformUtil.GetPlayerNameRaw at _Ready, before the
        // override existed). When the widget doesn't exist yet, its later _Ready resolves the name through the
        // hook, so the override alone is sufficient — no refresh needed. Never fails the action on a miss.
        if (TryResolveLobbyContext(out var lobbyContext) && lobbyContext?.StartRunLobby is not null)
        {
            var remotePlayerContainer = Sts2LiveIntrospection.GetMemberValue(lobbyContext.ScreenObject, "_remotePlayerContainer");
            ApplySyntheticNameplate(remotePlayerContainer, netId);
        }

        // Same problem one screen further on: NMultiplayerPlayerState (the per-player panel a RUN shows, with the
        // health bar and turn indicator) stamps its nameplate ONCE, when the panel becomes ready, through the same
        // raw lookup — so a name registered after the run started keeps showing the raw netId until it rebuilds.
        // A run has one panel per player, hence the plural node lookup. Best-effort, like the lobby refresh above.
        ApplyRunPlayerNameplate(netId, displayName);

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Set display name '{displayName}' for client p:{netId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:set-client-name:{request.RequestId}",
            kind: request.Kind,
            message: $"Set display name '{displayName}' for client p:{netId}.");
    }

    // The in-RUN counterpart of ApplySyntheticNameplate: re-stamp the nameplate of every live
    // NMultiplayerPlayerState panel belonging to this netId. Silent on a miss (no run, no panel yet, a game build
    // whose member names moved) — a nameplate refresh must never fail a name registration.
    private const string MultiplayerPlayerStateTypeName = "MegaCrit.Sts2.Core.Nodes.Multiplayer.NMultiplayerPlayerState";

    private static void ApplyRunPlayerNameplate(ulong netId, string displayName)
    {
        try
        {
            foreach (var panel in Sts2LiveIntrospection.FindRunTreeNodesOfType(MultiplayerPlayerStateTypeName))
            {
                if (Sts2LiveIntrospection.GetMemberValue(panel, "Player") is not { } player
                    || Sts2LiveIntrospection.GetMemberValue(player, "NetId") is not ulong panelNetId
                    || panelNetId != netId)
                {
                    continue;
                }

                var nameplateLabel = Sts2LiveIntrospection.GetMemberValue(panel, "_nameplateLabel");
                Sts2LiveIntrospection.TryInvokeMethod(nameplateLabel, "SetTextAutoSize", displayName);
            }
        }
        catch
        {
            // Cosmetic refresh only.
        }
    }

    // Accept either the "p:1002" lobby-id form (reusing TryParseLobbyPlayerId) or a bare integer netId "1002".
    private static bool TryResolveNetId(string? value, out ulong netId)
    {
        if (TryParseLobbyPlayerId(value, out netId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(value)
               && ulong.TryParse(value.Trim(), out netId)
               && netId > 0;
    }
}
