using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // The members an embedded-profile dispatch arm reaches. The rest of this area is in
    // Sts2ActionHandler.ContextOwnership.Full.cs, moved verbatim, which the embedded profile does not compile.

    private static string? ResolveLocalPlayerId()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState is null)
            {
                return null;
            }

            var player = LocalContext.GetMe(runState);
            return player is null
                ? null
                : Sts2LiveIntrospection.GetMemberValue(player, "NetId")?.ToString() is { Length: > 0 } netId
                    ? $"p:{netId}"
                    : $"p:{player.GetHashCode()}";
        }
        catch
        {
            return null;
        }
    }

    private bool TryResolveMapContext([NotNullWhen(true)] out MapActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var mapScreen = NMapScreen.Instance;
        // The browser renders the map as an OVERLAY over the run scene, so the located screen is the
        // room ("run"), never the map screen — requiring IsMapScreenType here made select-map-node
        // permanently unavailable to browser play. Resolve the context whenever the live map is open;
        // CanSelectMapNode (IsTravelEnabled + travelable) is the real gate on whether a vote is allowed.
        if (mapScreen is null || !mapScreen.IsOpen)
        {
            context = null;
            return false;
        }

        var nodesById = Sts2MapScreenInspector.ResolveNodes(mapScreen)
            .ToDictionary(node => node.Snapshot.Id, StringComparer.Ordinal);
        var localPlayerId = ResolveLocalPlayerId();
        var choicesById = Sts2MapScreenInspector.ResolveFlowChoices(mapScreen, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);

        context = new MapActionContext(
            Screen: screen,
            LocalPlayerId: localPlayerId,
            MapScreen: mapScreen,
            NodesById: nodesById,
            ChoicesById: choicesById);
        return true;
    }

#if !SPIRECTL_PROFILE_EMBEDDED
    private bool TryResolveRewardContext([NotNullWhen(true)] out RewardActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var localPlayerId = ResolveLocalPlayerId();
        if (!Sts2SupportedScreenIds.IsRewardsScreenType(screen.ScreenType)
            || screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen"))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2RewardScreenInspector.ResolveChoices(screenObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new RewardActionContext(
            Screen: screen,
            ScreenObject: screenObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

#endif
    private bool TryResolveLobbyContext([NotNullWhen(true)] out LobbyActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (!Sts2SupportedScreenIds.IsLobbyScreenType(screen.ScreenType) || screenObject is null)
        {
            context = null;
            return false;
        }

        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject)
            && Sts2LiveIntrospection.GetMemberValue(screenObject, "Lobby") is StartRunLobby startRunLobby)
        {
            var localPlayerId = startRunLobby.LocalPlayer is GameLobbyPlayer localPlayer
                ? $"p:{localPlayer.id}"
                : $"p:{startRunLobby.NetService.NetId}";
            var hostPlayerId = ResolveLobbyHostPlayerId(startRunLobby.NetService, localPlayerId);
            var players = startRunLobby.Players
                .Select(player =>
                {
                    var isLocal = player.id == startRunLobby.NetService.NetId;
                    var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.id);
                    return new LobbyPlayerSnapshot(
                        Id: $"p:{player.id}",
                        Status: player.isReady ? "ready" : "not-ready",
                        Name: null,
                        SelectedCharacterId: player.character?.Id.Entry,
                        IsReady: player.isReady,
                        SlotId: player.slotId,
                        IsLocal: isLocal,
                        IsHost: string.Equals($"p:{player.id}", hostPlayerId, StringComparison.Ordinal),
                        IsHostLocalSeat: isHostLocalSeat,
                        IsRemote: !isLocal && !isHostLocalSeat);
                })
                .ToArray();
            var availableCharacters = ResolveSelectableCharacters(screenObject);
            var lobby = new LobbyStateSnapshot(
                LobbyId: "start-run",
                Phase: startRunLobby.IsAboutToBeginGame() ? "starting" : "selecting",
                Players: players,
                AvailableCharacters: availableCharacters,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                LocalPlayerRole: startRunLobby.NetService.Type == NetGameType.Host ? "host" : "client",
                PlayersById: players.ToDictionary(player => player.Id, StringComparer.Ordinal),
                AvailableCharactersById: availableCharacters.ToDictionary(character => character.Id, StringComparer.Ordinal));
            context = new LobbyActionContext(screen, screenObject, lobby, startRunLobby, null);
            return true;
        }

        if (Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screenObject)
            && (Sts2LiveIntrospection.GetMemberValue(screenObject, "_runLobby") as LoadRunLobby
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "RunLobby") as LoadRunLobby) is LoadRunLobby loadRunLobby)
        {
            var localPlayerId = $"p:{loadRunLobby.NetService.NetId}";
            var hostPlayerId = ResolveLobbyHostPlayerId(loadRunLobby.NetService, localPlayerId);
            var players = (loadRunLobby.Run?.Players ?? [])
                .Select((player, index) =>
                {
                    var isLocal = player.NetId == loadRunLobby.NetService.NetId;
                    var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.NetId);
                    return new LobbyPlayerSnapshot(
                        Id: $"p:{player.NetId}",
                        Status: loadRunLobby.IsPlayerReady(player.NetId) ? "ready" : "not-ready",
                        Name: null,
                        SelectedCharacterId: player.CharacterId?.Entry,
                        IsReady: loadRunLobby.IsPlayerReady(player.NetId),
                        SlotId: index,
                        IsLocal: isLocal,
                        IsHost: string.Equals($"p:{player.NetId}", hostPlayerId, StringComparison.Ordinal),
                        IsHostLocalSeat: isHostLocalSeat,
                        IsRemote: !isLocal && !isHostLocalSeat);
                })
                .ToArray();
            var availableCharacters = ResolveSelectableCharacters(screenObject);
            var lobby = new LobbyStateSnapshot(
                LobbyId: "load-run",
                Phase: players.Any(player => player.IsReady) ? "confirming" : "waiting",
                Players: players,
                AvailableCharacters: availableCharacters,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                LocalPlayerRole: loadRunLobby.NetService.Type == NetGameType.Host ? "host" : "client",
                PlayersById: players.ToDictionary(player => player.Id, StringComparer.Ordinal),
                AvailableCharactersById: availableCharacters.ToDictionary(character => character.Id, StringComparer.Ordinal));
            context = new LobbyActionContext(screen, screenObject, lobby, null, loadRunLobby);
            return true;
        }

        context = null;
        return false;
    }

    private static IReadOnlyList<LobbyCharacterSnapshot> ResolveSelectableCharacters(object screenObject)
    {
        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Godot.Node
            ?? screenObject as Godot.Node;
        if (root is null)
        {
            return [];
        }

        return Sts2TreeSearch.FindDescendants(
                root,
                static node => node.GetChildren().OfType<Godot.Node>(),
                static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
            .Select(child => new
            {
                IsRandom = Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool isRandom && isRandom,
                IsLocked = Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool isLocked && isLocked,
                Character = Sts2LiveIntrospection.GetMemberValue(child, "Character") as CharacterModel,
            })
            .Where(entry => entry.IsRandom || entry.Character is not null)
            .Select(entry => new LobbyCharacterSnapshot(
                Id: entry.IsRandom ? "RANDOM_CHARACTER" : entry.Character!.Id.Entry,
                Name: entry.IsRandom ? "Random Character" : entry.Character!.CharacterSelectTitle,
                IsUnlocked: !entry.IsLocked))
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static string? ResolveLobbyHostPlayerId(object? netService, string? localPlayerId)
        => Sts2LobbyHostResolver.Resolve(netService, localPlayerId);

    private static string? ResolveRunHostPlayerId(string? localPlayerId)
        => Sts2LobbyHostResolver.ResolveRunHostPlayerId(RunManager.Instance?.NetService, localPlayerId);

    private static bool IsHostLocalPlayerId(string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !playerId.StartsWith("p:", StringComparison.Ordinal))
        {
            return false;
        }

        return ulong.TryParse(playerId[2..], out var netId)
            && Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId);
    }

    private static bool BuildOwnershipFailure(
        SemanticActionRequest request,
        ActionOwnershipContext context,
        out ActionExecutionResult failure)
    {
        var requestedPlayerId = string.IsNullOrWhiteSpace(context.RequestedPlayerId)
            ? context.ResolvedOwnerPlayerId
            : context.RequestedPlayerId;
        var localRole = ResolveLocalRole(context.LocalPlayerId, context.HostPlayerId);
        var isHostLocalOwner = context.HostLocalPlayerIds.Contains(requestedPlayerId ?? string.Empty, StringComparer.Ordinal);
        var remoteOrchestration = isHostLocalOwner
            ? OwnershipMetadata.HostLocalSeat()
            : OwnershipMetadata.LocalOnlyDegraded();

        if (string.IsNullOrWhiteSpace(requestedPlayerId))
        {
            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.UnsupportedPerspective,
                "unsupported-perspective",
                "The local bridge could not resolve which player owns this semantic action.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context.ResolvedOwnerPlayerId)
            && !string.Equals(requestedPlayerId, context.ResolvedOwnerPlayerId, StringComparison.Ordinal))
        {
            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.WrongPlayer,
                "wrong-player",
                "The requested player does not own the visible control or action surface.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context.LocalPlayerId)
            && !string.Equals(requestedPlayerId, context.LocalPlayerId, StringComparison.Ordinal))
        {
            if (isHostLocalOwner)
            {
                failure = null!;
                return false;
            }

            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.UnsupportedPerspective,
                "unsupported-perspective",
                "This local bridge can execute only actions owned by its local player; remote-owned semantic actions require an explicitly configured remote client bridge.");
            return true;
        }

        failure = null!;
        return false;
    }

    private static ActionExecutionResult BuildOwnershipFailure(
        SemanticActionRequest request,
        ActionOwnershipContext context,
        string? requestedPlayerId,
        MultiplayerRoleSnapshot localRole,
        RemoteClientOrchestrationCapabilitySnapshot remoteOrchestration,
        ActionFailureCode code,
        string reasonCode,
        string note)
    {
        return ActionExecutionResult.Failure(
            kind: request.Kind,
            code: code,
            message: code == ActionFailureCode.WrongPlayer
                ? $"{context.Action} cannot execute for a player that does not own the current action surface."
                : $"{context.Action} cannot execute from the current local bridge perspective.",
            details:
            [
                new ActionFailureDetail(
                    Field: "player_id",
                    Value: requestedPlayerId ?? string.Empty,
                    Note: $"{note} reasonCode={reasonCode}; remoteOrchestration={remoteOrchestration.Id}.",
                    ReasonCode: code,
                    Screen: context.Screen,
                    PlayerId: requestedPlayerId,
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: context.ResolvedOwnerPlayerId,
                    LocalPlayerId: context.LocalPlayerId,
                    HostPlayerId: context.HostPlayerId,
                    LocalRole: localRole,
                    Action: context.Action,
                    RemoteOrchestration: remoteOrchestration),
            ],
            screen: context.Screen,
            playerId: requestedPlayerId,
            requestedPlayerId: requestedPlayerId,
            resolvedOwnerPlayerId: context.ResolvedOwnerPlayerId,
            localPlayerId: context.LocalPlayerId,
            hostPlayerId: context.HostPlayerId,
            localRole: localRole,
            action: context.Action,
            remoteOrchestration: remoteOrchestration);
    }

    private static MultiplayerRoleSnapshot ResolveLocalRole(string? localPlayerId, string? hostPlayerId)
    {
        if (string.IsNullOrWhiteSpace(localPlayerId))
        {
            return MultiplayerRoleSnapshot.Unspecified;
        }

        return string.Equals(localPlayerId, hostPlayerId, StringComparison.Ordinal)
            ? MultiplayerRoleSnapshot.Host
            : MultiplayerRoleSnapshot.Local;
    }

    private static void ApplySyntheticNameplate(object? remotePlayerContainer, ulong playerId)
    {
        var syntheticName = Sts2HostLocalSeatRegistry.ResolveSyntheticName(playerId);
        if (string.IsNullOrWhiteSpace(syntheticName)
            || Sts2LiveIntrospection.GetMemberValue(remotePlayerContainer, "_nodes") is not System.Collections.IEnumerable nodes)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (Sts2LiveIntrospection.GetMemberValue(node, "PlayerId") is ulong nodePlayerId
                && nodePlayerId == playerId)
            {
                var nameplateLabel = Sts2LiveIntrospection.GetMemberValue(node, "_nameplateLabel");
                Sts2LiveIntrospection.TryInvokeMethod(nameplateLabel, "SetTextAutoSize", syntheticName);
                return;
            }
        }
    }

    private static bool TryParseLobbyPlayerId(string? playerId, out ulong netId)
    {
        netId = 0;
        return !string.IsNullOrWhiteSpace(playerId)
               && playerId.StartsWith("p:", StringComparison.Ordinal)
               && ulong.TryParse(playerId[2..], out netId)
               && netId > 0;
    }

}
