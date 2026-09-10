// Shared multiplayer restore-metadata <-> proto conversions used by the scenario
// export/load path (previously colocated with the removed checkpoint flow).

fn multiplayer_from_proto(
    metadata: bridge::proto::MultiplayerRestoreMetadata,
) -> Option<MultiplayerRestoreFile> {
    let empty = !metadata.is_multiplayer
        && metadata.restore_mode == bridge::proto::MultiplayerRestoreMode::Unspecified as i32
        && metadata.local_player_id.is_empty()
        && metadata.host_player_id.is_empty()
        && metadata.local_player_role.is_empty()
        && metadata.players.is_empty()
        && metadata.lobby.is_none()
        && !metadata.requires_remote_clients
        && !metadata.degraded_local_only_available
        && metadata.limitations.is_empty();
    if empty {
        return None;
    }

    Some(MultiplayerRestoreFile {
        is_multiplayer: metadata.is_multiplayer,
        restore_mode: multiplayer_restore_mode_name(metadata.restore_mode()).to_string(),
        local_player_id: metadata.local_player_id,
        host_player_id: metadata.host_player_id,
        local_player_role: metadata.local_player_role,
        players: metadata
            .players
            .into_iter()
            .map(|player| MultiplayerPlayerFile {
                id: player.id,
                net_id: player.net_id,
                slot_id: player.slot_id,
                display_name: if player.display_name.is_empty() {
                    None
                } else {
                    Some(player.display_name)
                },
                selected_character_id: player.selected_character_id,
                character: player.character,
                is_ready: player.is_ready,
                is_local: player.is_local,
                is_host: player.is_host,
                is_remote: player.is_remote,
            })
            .collect(),
        lobby: metadata.lobby.map(|lobby| MultiplayerLobbyFile {
            lobby_id: lobby.lobby_id,
            phase: lobby.phase,
            available_characters: lobby
                .available_characters
                .into_iter()
                .map(|character| LobbyCharacterFile {
                    id: character.id,
                    name: character.name,
                    is_unlocked: character.is_unlocked,
                })
                .collect(),
        }),
        requires_remote_clients: metadata.requires_remote_clients,
        degraded_local_only_available: metadata.degraded_local_only_available,
        limitations: metadata
            .limitations
            .into_iter()
            .map(|note| CompatibilityNoteFile {
                code: note.code,
                message: note.message,
                field: note.field,
            })
            .collect(),
    })
}

fn multiplayer_to_proto(
    metadata: &MultiplayerRestoreFile,
) -> bridge::proto::MultiplayerRestoreMetadata {
    bridge::proto::MultiplayerRestoreMetadata {
        is_multiplayer: metadata.is_multiplayer,
        restore_mode: multiplayer_restore_mode_value(&metadata.restore_mode) as i32,
        local_player_id: metadata.local_player_id.clone(),
        host_player_id: metadata.host_player_id.clone(),
        local_player_role: metadata.local_player_role.clone(),
        players: metadata
            .players
            .iter()
            .map(|player| bridge::proto::MultiplayerPlayerState {
                id: player.id.clone(),
                net_id: player.net_id.clone(),
                slot_id: player.slot_id,
                display_name: player.display_name.clone().unwrap_or_default(),
                selected_character_id: player.selected_character_id.clone(),
                is_ready: player.is_ready,
                is_local: player.is_local,
                is_host: player.is_host,
                is_remote: player.is_remote,
                character: player.character.clone(),
            })
            .collect(),
        lobby: metadata
            .lobby
            .as_ref()
            .map(|lobby| bridge::proto::MultiplayerLobbySnapshot {
                lobby_id: lobby.lobby_id.clone(),
                phase: lobby.phase.clone(),
                available_characters: lobby
                    .available_characters
                    .iter()
                    .map(|character| bridge::proto::LobbyCharacter {
                        id: character.id.clone(),
                        name: character.name.clone(),
                        is_unlocked: character.is_unlocked,
                        ..Default::default()
                    })
                    .collect(),
            }),
        requires_remote_clients: metadata.requires_remote_clients,
        degraded_local_only_available: metadata.degraded_local_only_available,
        limitations: metadata
            .limitations
            .iter()
            .map(|note| bridge::proto::RestoreCompatibilityNote {
                code: note.code.clone(),
                message: note.message.clone(),
                field: note.field.clone(),
            })
            .collect(),
    }
}
