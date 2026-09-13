using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Perspective;
using System.Reflection;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;

internal static class CharacterSelectStateBuilder
{
    private const string RandomCharacterId = "RANDOM_CHARACTER";
    internal static StateCharacterSelectSnapshot? ResolveCharacterSelect(object? screenObject, PlayerPerspective perspective)
    {
        if (screenObject is null)
        {
            return null;
        }

        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject)
            && Sts2LiveIntrospection.GetMemberValue(screenObject, "Lobby") is { } startRunLobby)
        {
            return BuildCharacterSelect(ResolveLobby(startRunLobby), screenObject, perspective);
        }

        if (Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screenObject)
            && (Sts2LiveIntrospection.GetMemberValue(screenObject, "_runLobby") as LoadRunLobby
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "RunLobby") as LoadRunLobby) is { } loadRunLobby)
        {
            return BuildCharacterSelect(ResolveLoadRunLobby(loadRunLobby), screenObject, perspective);
        }

        return null;
    }

    private static StateCharacterSelectSnapshot BuildCharacterSelect(
        StateCharacterSelectLobbySnapshot lobby,
        object screenObject,
        PlayerPerspective perspective)
    {
        var characterButtons = ResolveCharacterButtons(screenObject);
        return new StateCharacterSelectSnapshot(
            lobby,
            characterButtons,
            ResolveCharacterSelectView(lobby, characterButtons, perspective));
    }

    private static StateCharacterSelectLobbySnapshot ResolveLobby(object lobby)
    {
        var netService = Sts2LiveIntrospection.GetMemberValue(lobby, "NetService");
        var localPlayer = Sts2LiveIntrospection.GetMemberValue(lobby, "LocalPlayer");
        var localPlayerId = StateProjectionValues.ResolvePlayerId(Sts2LiveIntrospection.GetMemberValue(localPlayer, "id"))
            ?? StateProjectionValues.ResolvePlayerId(Sts2LiveIntrospection.GetMemberValue(netService, "NetId"));
        var localNetId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(localPlayer, "id"))
            ?? StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(netService, "NetId"));
        var hostPlayerId = Sts2LobbyHostResolver.Resolve(netService, localPlayerId);
        var platform = Sts2LiveIntrospection.GetMemberValue(netService, "Platform");
        var nameNotices = new List<StateNoticeSnapshot>();

        return new StateCharacterSelectLobbySnapshot(
            NetGameType: StateProjectionValues.ResolveNetGameType(Sts2LiveIntrospection.GetMemberValue(netService, "Type")),
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            ConnectingPlayerCount: StateProjectionValues.ResolveCollectionCount(Sts2LiveIntrospection.GetMemberValue(lobby, "_connectingPlayers")),
            Ascension: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(lobby, "Ascension")),
            MaxAscension: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(lobby, "MaxAscension")),
            Act1: StateProjectionValues.NormalizeIdentifier(Sts2LiveIntrospection.GetMemberValue(lobby, "Act1")?.ToString()),
            Seed: StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(lobby, "Seed")?.ToString()),
            ModifierIds: StateProjectionValues.ResolveModelIds(Sts2LiveIntrospection.GetMemberValue(lobby, "Modifiers")),
            Players: ResolvePlayers(
                Sts2LiveIntrospection.GetMemberValue(lobby, "Players"),
                platform,
                nameNotices,
                // Use observed transport membership for lobby connectivity.
                Sts2RunPlayerConnectivity.ResolveConnectedNetIds(netService, localNetId),
                localPlayerId),
            MaxPlayers: ResolveMaxPlayers(lobby));
    }

    /// <summary>
    /// The start-run lobby's live player cap (<see cref="GameApiNames.LobbyMaxPlayers"/>, which the lobby itself
    /// checks in its join handler). Read fresh on every observation rather than cached: the multiplayer limit mods
    /// raise it at different moments — "Unlimited" rewrites the argument the lobby is constructed with, so it is
    /// right from the start, while "Multiplayer Limit Break" writes the field from its own join/connect hooks, so
    /// an early read still sees the stock 4.
    /// </summary>
    /// <remarks>
    /// No rename fallback. The member is a startup requirement (<see cref="Sts2GameApiProbe"/>), so a build that
    /// does not expose the cap refuses the bridge instead of quietly sizing every seat limit — admission caps,
    /// free-slot checks, the host transport's lobby probe — off the stock constant.
    /// </remarks>
    private static int ResolveMaxPlayers(object lobby)
        => StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(lobby, GameApiNames.LobbyMaxPlayers));

    private static IReadOnlyList<StateCharacterSelectPlayerSnapshot> ResolvePlayers(
        object? playersObject,
        object? platform,
        ICollection<StateNoticeSnapshot> nameNotices,
        object? connectedPlayerIds,
        string? localPlayerId)
    {
        if (playersObject is not IEnumerable players)
        {
            return [];
        }

        var result = new List<StateCharacterSelectPlayerSnapshot>();
        foreach (var player in players)
        {
            var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "id"));
            var playerId = netId.HasValue ? $"p:{netId.Value}" : string.Empty;
            result.Add(new StateCharacterSelectPlayerSnapshot(
                Id: playerId,
                SlotId: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "slotId")),
                CharacterId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(player, "character")),
                IsReady: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(player, "isReady")),
                MaxMultiplayerAscensionUnlocked: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "maxMultiplayerAscensionUnlocked")),
                DisplayName: netId.HasValue
                    ? Sts2LobbyNameResolver.Resolve(platform, netId.Value, nameNotices)
                    : null,
                IsConnected: ResolvePlayerConnected(connectedPlayerIds, netId, playerId, localPlayerId)));
        }

        return result;
    }

    // The local player is connected. Remote players require membership in the available observation source.
    private static bool ResolvePlayerConnected(object? connectedPlayerIds, ulong? netId, string? playerId, string? localPlayerId)
    {
        if (!string.IsNullOrEmpty(playerId) && string.Equals(playerId, localPlayerId, StringComparison.Ordinal))
        {
            return true;
        }

        if (connectedPlayerIds is IReadOnlySet<ulong> observedPeerIds)
        {
            return Sts2RunPlayerConnectivity.IsHostObservedLobbyPlayerConnected(observedPeerIds, netId);
        }

        return netId.HasValue && ContainsNetId(connectedPlayerIds, netId.Value);
    }

    private static bool ContainsNetId(object? netIdCollection, ulong netId)
    {
        if (netIdCollection is not IEnumerable items)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (StateProjectionValues.ToUInt64(item) is { } value && value == netId)
            {
                return true;
            }
        }

        return false;
    }

    private static StateCharacterSelectLobbySnapshot ResolveLoadRunLobby(LoadRunLobby lobby)
    {
        var netService = lobby.NetService;
        var localPlayerId = StateProjectionValues.ResolvePlayerId(Sts2LiveIntrospection.GetMemberValue(netService, "NetId"));
        var hostPlayerId = Sts2LobbyHostResolver.Resolve(netService, localPlayerId);
        var platform = Sts2LiveIntrospection.GetMemberValue(netService, "Platform");
        var savedRun = lobby.Run;
        var nameNotices = new List<StateNoticeSnapshot>();
        var players = ResolveLoadRunPlayers(savedRun, lobby, platform, nameNotices, localPlayerId);

        return new StateCharacterSelectLobbySnapshot(
            NetGameType: StateProjectionValues.ResolveNetGameType(Sts2LiveIntrospection.GetMemberValue(netService, "Type")),
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            ConnectingPlayerCount: StateProjectionValues.ResolveCollectionCount(Sts2LiveIntrospection.GetMemberValue(lobby, "_connectingPlayers")),
            Ascension: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(savedRun, "Ascension")),
            // The saved run fixes its ascension; the load-run lobby has no selectable range.
            MaxAscension: 0,
            // act1 is a start-run picker only; the saved run carries its own act/floor.
            Act1: null,
            Seed: ResolveSavedRunSeed(savedRun),
            ModifierIds: StateProjectionValues.ResolveModelIds(Sts2LiveIntrospection.GetMemberValue(savedRun, "Modifiers")),
            Players: players,
            SavedRun: ResolveSavedRunSummary(savedRun),
            // LoadRunLobby has NO MaxPlayers of its own: it admits exactly the netIds recorded in the save and
            // disconnects anything else as NetError.NotInSaveGame. The saved roster IS the cap, so report it —
            // that is the honest answer for a caller sizing per-player resources, and it needs no stock fallback
            // (a save always knows how many players it has).
            MaxPlayers: players.Count);
    }

    private static IReadOnlyList<StateCharacterSelectPlayerSnapshot> ResolveLoadRunPlayers(
        object? savedRun,
        LoadRunLobby lobby,
        object? platform,
        ICollection<StateNoticeSnapshot> nameNotices,
        string? localPlayerId)
    {
        if (Sts2LiveIntrospection.GetMemberValue(savedRun, "Players") is not IEnumerable players)
        {
            return [];
        }

        var connectedPlayerIds = Sts2LiveIntrospection.GetMemberValue(lobby, GameApiNames.LobbyPlayerIds);
        var result = new List<StateCharacterSelectPlayerSnapshot>();
        var index = 0;
        foreach (var player in players)
        {
            var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
            var playerId = netId.HasValue ? $"p:{netId.Value}" : string.Empty;
            result.Add(new StateCharacterSelectPlayerSnapshot(
                Id: playerId,
                SlotId: index,
                CharacterId: ResolveSavedPlayerCharacterId(player),
                IsReady: netId.HasValue && lobby.IsPlayerReady(netId.Value),
                MaxMultiplayerAscensionUnlocked: 0,
                DisplayName: netId.HasValue
                    ? Sts2LobbyNameResolver.Resolve(platform, netId.Value, nameNotices)
                    : null,
                IsConnected: ResolvePlayerConnected(connectedPlayerIds, netId, playerId, localPlayerId)));
            index++;
        }

        return result;
    }

    // SerializablePlayer.CharacterId is a ModelId (not a live model object), so read its
    // .Entry directly instead of the .Id.Entry path ResolveModelId uses for live models.
    private static string? ResolveSavedPlayerCharacterId(object? player)
        => StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(player, "CharacterId"), "Entry")?.ToString());

    private static string? ResolveSavedRunSeed(object? savedRun)
        => StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(savedRun, "SerializableRng"), "Seed")?.ToString());

    // Saved-run summary for the load-run lobby InfoPanel (act/floor + per-player hp/gold).
    // Mirrors the game's SerializableRun/SerializablePlayer save types; field names follow them.
    private static StateCharacterSelectSavedRunSnapshot? ResolveSavedRunSummary(object? savedRun)
    {
        if (savedRun is null)
        {
            return null;
        }

        return new StateCharacterSelectSavedRunSnapshot(
            CurrentActIndex: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(savedRun, "CurrentActIndex")),
            // Reuse the existing map_point_history floor derivation (counts visited points).
            ActFloor: Sts2SavedRunSnapshotResolver.Resolve(savedRun)?.Floor ?? 0,
            Players: ResolveSavedRunSummaryPlayers(savedRun));
    }

    private static IReadOnlyList<StateCharacterSelectSavedRunPlayerSnapshot> ResolveSavedRunSummaryPlayers(object? savedRun)
    {
        if (Sts2LiveIntrospection.GetMemberValue(savedRun, "Players") is not IEnumerable players)
        {
            return [];
        }

        var result = new List<StateCharacterSelectSavedRunPlayerSnapshot>();
        foreach (var player in players)
        {
            // Same NetId source + p:{id} formatting as ResolveLoadRunPlayers, so saved-run player
            // ids match the lobby player ids (the catalog binds by matching view.playerId).
            var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
            result.Add(new StateCharacterSelectSavedRunPlayerSnapshot(
                Id: netId.HasValue ? $"p:{netId.Value}" : string.Empty,
                CurrentHp: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "CurrentHp")),
                MaxHp: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "MaxHp")),
                Gold: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "Gold"))));
        }

        return result;
    }

    private static IReadOnlyList<StateCharacterButtonSnapshot> ResolveCharacterButtons(object screenObject)
    {
        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Node
            ?? screenObject as Node;
        if (root is null)
        {
            return [];
        }

        return Sts2TreeSearch.FindDescendants(
                root,
                static node => node.GetChildren().OfType<Node>(),
                static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
            .Where(StateProjectionValues.ResolveVisible)
            .Select(ResolveCharacterButton)
            .Where(button => !string.IsNullOrWhiteSpace(button.Id))
            .GroupBy(button => button.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static StateCharacterButtonSnapshot ResolveCharacterButton(Node button)
    {
        var id = ResolveCharacterButtonId(button) ?? string.Empty;
        return new StateCharacterButtonSnapshot(
            Id: id,
            CharacterId: id,
            IsLocked: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(button, "IsLocked")));
    }

    private static StateCharacterSelectViewSnapshot ResolveCharacterSelectView(
        StateCharacterSelectLobbySnapshot lobby,
        IReadOnlyList<StateCharacterButtonSnapshot> characterButtons,
        PlayerPerspective perspective)
    {
        var playerId = string.IsNullOrWhiteSpace(perspective.PlayerId)
            ? lobby.LocalPlayerId
            : perspective.PlayerId;
        var selectedCharacterId = lobby.Players
            .FirstOrDefault(player => string.Equals(player.Id, playerId, StringComparison.Ordinal))
            ?.CharacterId;
        var selectedCharacterButtonId = string.IsNullOrWhiteSpace(selectedCharacterId)
            ? null
            : characterButtons
                .FirstOrDefault(button => string.Equals(button.CharacterId, selectedCharacterId, StringComparison.Ordinal))
                ?.Id;
        return new StateCharacterSelectViewSnapshot(playerId, selectedCharacterButtonId);
    }

    private static string? ResolveCharacterButtonId(object? button)
    {
        if (button is null)
        {
            return null;
        }

        var characterId = StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(button, "Character"));
        if (!string.IsNullOrWhiteSpace(characterId))
        {
            return characterId;
        }

        return StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(button, "IsRandom"))
            ? RandomCharacterId
            : null;
    }

}
