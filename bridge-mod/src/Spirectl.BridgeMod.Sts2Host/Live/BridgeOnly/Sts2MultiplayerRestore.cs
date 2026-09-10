using Spirectl.Sts2.Core.Restore;
using Spirectl.Sts2.Core.State;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;

namespace Spirectl.Sts2.Live;


internal static class Sts2MultiplayerRestore
{
    public static MultiplayerRestoreSnapshot? BuildMetadata(GameStateSnapshot snapshot)
    {
        if (snapshot.Lobby is not null || Sts2SupportedScreenIds.IsLobbyScreenType(snapshot.ScreenType))
        {
            return BuildLobbyMetadata(snapshot);
        }

        var players = snapshot.Run?.Players ?? [];
        if (players.Count <= 1 && !players.Any(player => player.IsRemote))
        {
            players = snapshot.Combat?.Players
                .Select(player => new PlayerStateSnapshot(player.Id, player.Character, player.Hp, player.MaxHp, player.IsLocal, player.IsHost, player.IsRemote))
                .ToArray() ?? [];
        }

        if (players.Count <= 1 && !players.Any(player => player.IsRemote))
        {
            return null;
        }

        var localPlayerId = players.FirstOrDefault(player => player.IsLocal)?.Id
            ?? snapshot.ResolvedPerspective.PlayerId
            ?? string.Empty;
        var hostPlayerId = players.FirstOrDefault(player => player.IsHost)?.Id
            ?? localPlayerId;
        var hasRemote = players.Any(player => player.IsRemote);
        return new MultiplayerRestoreSnapshot(
            IsMultiplayer: true,
            RestoreMode: MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported,
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            LocalPlayerRole: localPlayerId == hostPlayerId ? "host" : "client",
            Players: players.Select((player, index) => new MultiplayerPlayerSnapshot(
                player.Id,
                NetIdFromPlayerId(player.Id),
                SlotId: index,
                DisplayName: string.Empty,
                SelectedCharacterId: player.Character,
                IsReady: false,
                player.IsLocal,
                player.IsHost,
                player.IsRemote,
                Character: player.Character)).ToArray(),
            Lobby: null,
            RequiresRemoteClients: hasRemote,
            DegradedLocalOnlyAvailable: hasRemote && !string.IsNullOrWhiteSpace(localPlayerId),
            Limitations: hasRemote
                ? [
                    new MultiplayerRestoreLimitationSnapshot("remote-player-degraded-local", "Remote-owned player metadata can be reviewed locally, but active remote clients are omitted during allow-degraded local replay.", "multiplayer.players[isRemote=true]"),
                    new MultiplayerRestoreLimitationSnapshot("remote-clients-not-captured", "Remote client runtime processes are not captured by the local bridge.", "multiplayer.remoteRuntime"),
                ]
                : []);
    }

    public static object? Json(MultiplayerRestoreSnapshot? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        return new
        {
            isMultiplayer = metadata.IsMultiplayer,
            restoreMode = RestoreModeName(metadata.RestoreMode),
            localPlayerId = metadata.LocalPlayerId,
            hostPlayerId = metadata.HostPlayerId,
            localPlayerRole = metadata.LocalPlayerRole,
            requiresRemoteClients = metadata.RequiresRemoteClients,
            degradedLocalOnlyAvailable = metadata.DegradedLocalOnlyAvailable,
            lobby = metadata.Lobby is null ? null : new
            {
                lobbyId = metadata.Lobby.LobbyId,
                phase = metadata.Lobby.Phase,
                availableCharacters = metadata.Lobby.AvailableCharacters.Select(character => new
                {
                    id = character.Id,
                    name = character.Name,
                    isUnlocked = character.IsUnlocked,
                }).ToArray(),
            },
            players = metadata.Players.Select(player => new
            {
                id = player.Id,
                netId = player.NetId,
                slotId = player.SlotId,
                displayName = string.IsNullOrWhiteSpace(player.DisplayName) ? null : player.DisplayName,
                selectedCharacterId = player.SelectedCharacterId,
                character = player.Character,
                isReady = player.IsReady,
                isLocal = player.IsLocal,
                isHost = player.IsHost,
                isRemote = player.IsRemote,
            }).ToArray(),
            limitations = metadata.Limitations.Select(note => new
            {
                code = note.Code,
                message = note.Message,
                field = note.Field,
                supportClass = note.Code == "remote-player-degraded-local"
                    ? "degraded-local-multiplayer"
                    : note.Code == "remote-clients-not-captured"
                        ? "unsupported"
                        : null,
            }).ToArray(),
        };
    }

    private static MultiplayerRestoreSnapshot BuildLobbyMetadata(GameStateSnapshot snapshot)
    {
        var lobby = snapshot.Lobby;
        var players = lobby?.Players ?? [];
        var localPlayerId = lobby?.LocalPlayerId
            ?? players.FirstOrDefault(player => player.IsLocal)?.Id
            ?? snapshot.ResolvedPerspective.PlayerId
            ?? string.Empty;
        var hostPlayerId = lobby?.HostPlayerId
            ?? players.FirstOrDefault(player => player.IsHost)?.Id
            ?? localPlayerId;
        return new MultiplayerRestoreSnapshot(
            IsMultiplayer: true,
            RestoreMode: MultiplayerRestoreModeSnapshot.LobbyOnly,
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            LocalPlayerRole: lobby?.LocalPlayerRole ?? (localPlayerId == hostPlayerId ? "host" : "client"),
            Players: players.Select(player => new MultiplayerPlayerSnapshot(
                player.Id,
                NetIdFromPlayerId(player.Id),
                player.SlotId,
                player.Name ?? string.Empty,
                player.SelectedCharacterId ?? string.Empty,
                player.IsReady,
                player.IsLocal,
                player.IsHost,
                player.IsRemote,
                player.SelectedCharacterId ?? string.Empty)).ToArray(),
            Lobby: new MultiplayerLobbySnapshot(
                lobby?.LobbyId ?? "start-run",
                lobby?.Phase ?? string.Empty,
                (lobby?.AvailableCharacters ?? []).Select(character => new RestoreLobbyCharacterSnapshot(
                    character.Id,
                    character.Name,
                    character.IsUnlocked)).ToArray()),
            RequiresRemoteClients: false,
            DegradedLocalOnlyAvailable: false,
            Limitations: []);
    }

    private static string NetIdFromPlayerId(string playerId)
        => playerId.StartsWith("p:", StringComparison.Ordinal) && playerId.Length > 2
            ? playerId[2..]
            : string.Empty;

    private static string RestoreModeName(MultiplayerRestoreModeSnapshot mode)
        => mode switch
        {
            MultiplayerRestoreModeSnapshot.LobbyOnly => "lobby-only",
            MultiplayerRestoreModeSnapshot.HostLocalActiveRun => "host-local-active-run",
            MultiplayerRestoreModeSnapshot.RemotePlayerPlaceholder => "remote-player-placeholder",
            MultiplayerRestoreModeSnapshot.FullActiveMultiplayer => "full-active-multiplayer",
            MultiplayerRestoreModeSnapshot.UnsupportedRemoteClientRequired => "unsupported-remote-client-required",
            MultiplayerRestoreModeSnapshot.DegradedLocalOnly => "degraded-local-only",
            MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported => "active-multiplayer-unsupported",
            _ => "unspecified",
        };
}
