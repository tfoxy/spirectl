/// Self-contained mock character-select envelope. Mirrors the player/character
/// layout the former `lobby_state_response` produced, projected directly into the
/// runtime-only `StateCharacterSelect` shape (no intermediate current snapshot).
pub(super) fn mock_character_select(
    requested: &proto::PerspectiveInfo,
    local_ready: bool,
) -> proto::StateCharacterSelect {
    let player_ids = mock_lobby_player_ids();
    let default_local_player_id = player_ids
        .first()
        .cloned()
        .unwrap_or_else(|| "p:100".to_string());
    let secondary_player_id = player_ids
        .get(1)
        .cloned()
        .unwrap_or_else(|| "p:200".to_string());
    let factual_local_player_id = mock_local_player_id(MockScenario::Lobby);
    let host_player_id = mock_host_player_id(MockScenario::Lobby);
    let local_selected_character = if local_ready { "silent" } else { "ironclad" };

    let mut players = vec![
        proto::StateCharacterSelectPlayer {
            id: default_local_player_id,
            slot_id: 0,
            character_id: local_selected_character.to_string(),
            is_ready: local_ready,
            max_multiplayer_ascension_unlocked: 0,
            display_name: String::new(),
            is_connected: true,
        },
        proto::StateCharacterSelectPlayer {
            id: secondary_player_id.clone(),
            slot_id: 1,
            character_id: "ironclad".to_string(),
            is_ready: true,
            max_multiplayer_ascension_unlocked: 0,
            display_name: String::new(),
            is_connected: true,
        },
    ];
    if mock_is_host_local_seat(&secondary_player_id) {
        players.push(proto::StateCharacterSelectPlayer {
            id: "p:300".to_string(),
            slot_id: 2,
            character_id: "defect".to_string(),
            is_ready: true,
            max_multiplayer_ascension_unlocked: 0,
            display_name: String::new(),
            is_connected: true,
        });
    }

    let character_buttons = ["ironclad", "silent", "defect"]
        .into_iter()
        .map(|id| proto::StateCharacterButton {
            id: id.to_string(),
            character_id: id.to_string(),
            is_locked: !mock_lobby_character_unlocked(id),
        })
        .collect();

    let view_player_id = if requested.player_id.is_empty() {
        factual_local_player_id.clone()
    } else {
        requested.player_id.clone()
    };
    let selected_character_button_id = players
        .iter()
        .find(|player| player.id == view_player_id)
        .map(|player| player.character_id.clone())
        .unwrap_or_default();

    proto::StateCharacterSelect {
        lobby: Some(proto::StateCharacterSelectLobby {
            net_game_type: "host".to_string(),
            local_player_id: factual_local_player_id,
            host_player_id,
            connecting_player_count: 0,
            ascension: 0,
            max_ascension: 0,
            act1: "random".to_string(),
            seed: String::new(),
            modifier_ids: Vec::new(),
            players,
            saved_run: None,
            max_players: 4,
        }),
        character_buttons,
        view: Some(proto::StateCharacterSelectView {
            selected_character_button_id,
            player_id: view_player_id,
        }),
    }
}

pub(super) fn default_perspective(
    scenario: MockScenario,
    uses_default: bool,
) -> proto::PerspectiveInfo {
    let player_id = match scenario {
        MockScenario::Combat => "p1".to_string(),
        MockScenario::Map => "p1".to_string(),
        MockScenario::EventRoom => "p1".to_string(),
        MockScenario::TreasureRoom | MockScenario::RelicSelection => "p1".to_string(),
        MockScenario::RestSite => "p1".to_string(),
        MockScenario::Shop => "p1".to_string(),
        MockScenario::FakeMerchantPreOpen => "p1".to_string(),
        MockScenario::CrystalSphere | MockScenario::CrystalSphereFinished => "p1".to_string(),
        MockScenario::Rewards => "p1".to_string(),
        MockScenario::CardSelection
        | MockScenario::SimpleCardSelection
        | MockScenario::DeckCardSelection
        | MockScenario::BundleSelection
        | MockScenario::CardOverlay
        | MockScenario::PassiveCardOverlay => "p1".to_string(),
        MockScenario::MainMenu => String::new(),
        MockScenario::Lobby | MockScenario::LobbyReady => mock_local_player_id(scenario),
    };
    let host_player_id = mock_host_player_id(scenario);
    proto::PerspectiveInfo {
        scope: proto::PerspectiveScope::Local as i32,
        player_id: player_id.clone(),
        uses_default,
        local_role: mock_local_role(scenario, &player_id) as i32,
        local_player_id: mock_local_player_id(scenario),
        host_player_id,
        remote_orchestration: Some(mock_remote_orchestration(scenario)),
    }
}

pub(super) fn resolve_perspective(
    scenario: MockScenario,
    selector: Option<&proto::PerspectiveSelector>,
) -> proto::PerspectiveInfo {
    let default_player_id = match scenario {
        MockScenario::Combat => "p1".to_string(),
        MockScenario::Map => "p1".to_string(),
        MockScenario::EventRoom => "p1".to_string(),
        MockScenario::TreasureRoom | MockScenario::RelicSelection => "p1".to_string(),
        MockScenario::RestSite => "p1".to_string(),
        MockScenario::Shop => "p1".to_string(),
        MockScenario::FakeMerchantPreOpen => "p1".to_string(),
        MockScenario::CrystalSphere | MockScenario::CrystalSphereFinished => "p1".to_string(),
        MockScenario::Rewards => "p1".to_string(),
        MockScenario::CardSelection
        | MockScenario::SimpleCardSelection
        | MockScenario::DeckCardSelection
        | MockScenario::BundleSelection
        | MockScenario::CardOverlay
        | MockScenario::PassiveCardOverlay => "p1".to_string(),
        MockScenario::MainMenu => String::new(),
        MockScenario::Lobby | MockScenario::LobbyReady => mock_local_player_id(scenario),
    };

    match selector {
        Some(selector) => {
            let scope = proto::PerspectiveScope::try_from(selector.scope)
                .ok()
                .unwrap_or(proto::PerspectiveScope::Local);
            proto::PerspectiveInfo {
                scope: scope as i32,
                player_id: if selector.player_id.is_empty() {
                    default_player_id
                } else {
                    selector.player_id.clone()
                },
                uses_default: false,
                local_role: mock_local_role(scenario, &selector.player_id) as i32,
                local_player_id: mock_local_player_id(scenario),
                host_player_id: mock_host_player_id(scenario),
                remote_orchestration: Some(mock_remote_orchestration(scenario)),
            }
        }
        None => proto::PerspectiveInfo {
            scope: proto::PerspectiveScope::Local as i32,
            player_id: default_player_id.clone(),
            uses_default: true,
            local_role: mock_local_role(scenario, &default_player_id) as i32,
            local_player_id: mock_local_player_id(scenario),
            host_player_id: mock_host_player_id(scenario),
            remote_orchestration: Some(mock_remote_orchestration(scenario)),
        },
    }
}

pub(super) fn mock_local_player_id(scenario: MockScenario) -> String {
    match scenario {
        MockScenario::MainMenu => String::new(),
        MockScenario::Lobby | MockScenario::LobbyReady => {
            if mock_loaded_fixture_name().as_deref() == Some("two-ironclad-lobby") {
                "p:1".to_string()
            } else {
                "p:100".to_string()
            }
        }
        _ => "p1".to_string(),
    }
}

pub(super) fn mock_host_player_id(scenario: MockScenario) -> String {
    match scenario {
        MockScenario::MainMenu => String::new(),
        MockScenario::Lobby | MockScenario::LobbyReady => {
            if mock_loaded_fixture_name().as_deref() == Some("two-ironclad-lobby") {
                "p:1".to_string()
            } else {
                "p:100".to_string()
            }
        }
        _ => "p1".to_string(),
    }
}

pub(super) fn mock_is_host_local_seat(player_id: &str) -> bool {
    MOCK_LOADED_HOST_LOCAL_SEATS.with(|loaded| {
        let loaded = loaded.borrow();
        loaded.iter().any(|id| id == player_id)
            || (loaded.is_empty() && !mock_has_loaded_fixture_scenario() && player_id == "p:200")
    })
}

pub(super) fn mock_loaded_player_ids() -> Vec<String> {
    MOCK_LOADED_PLAYER_IDS.with(|loaded| loaded.borrow().clone())
}

pub(super) fn mock_locked_lobby_characters() -> Vec<String> {
    MOCK_LOADED_LOCKED_CHARACTERS.with(|loaded| loaded.borrow().clone())
}

pub(super) fn mock_lobby_screen_id_supported(screen_id: &str) -> bool {
    matches!(
        screen_id,
        START_RUN_LOBBY_SCREEN_ID | LOAD_RUN_LOBBY_SCREEN_ID
    )
}

pub(super) fn mock_lobby_character_unlocked(id: &str) -> bool {
    !mock_locked_lobby_characters()
        .iter()
        .any(|locked| locked.eq_ignore_ascii_case(id))
}

pub(super) fn mock_loaded_fixture_json() -> Option<Value> {
    MOCK_LOADED_FIXTURE_JSON.with(|loaded| loaded.borrow().clone())
}

pub(super) fn mock_loaded_fixture_name() -> Option<String> {
    mock_loaded_fixture_json().and_then(|fixture| {
        fixture
            .get("name")
            .and_then(Value::as_str)
            .map(str::to_string)
    })
}

pub(super) fn mock_lobby_player_ids() -> Vec<String> {
    if mock_loaded_fixture_name().as_deref() == Some("two-ironclad-lobby") {
        let loaded = mock_loaded_player_ids();
        if !loaded.is_empty() {
            return loaded;
        }
    }
    vec!["p:100".to_string(), "p:200".to_string()]
}

pub(super) fn mock_has_loaded_fixture_scenario() -> bool {
    MOCK_LOADED_FIXTURE_SCENARIO.with(|loaded| loaded.borrow().is_some())
}

pub(super) fn mock_local_role(scenario: MockScenario, player_id: &str) -> proto::MultiplayerRole {
    if mock_is_host_local_seat(player_id) {
        return proto::MultiplayerRole::HostLocalSeat;
    }

    match scenario {
        MockScenario::MainMenu => proto::MultiplayerRole::Unspecified,
        MockScenario::Lobby | MockScenario::LobbyReady
            if player_id == mock_local_player_id(scenario) || player_id.is_empty() =>
        {
            proto::MultiplayerRole::Host
        }
        MockScenario::Lobby | MockScenario::LobbyReady => proto::MultiplayerRole::Remote,
        _ => proto::MultiplayerRole::Host,
    }
}

pub(super) fn mock_remote_orchestration(
    scenario: MockScenario,
) -> proto::RemoteClientOrchestrationCapability {
    let state = match scenario {
        MockScenario::Lobby | MockScenario::LobbyReady => {
            proto::RemoteClientOrchestrationState::LocalOnlyDegraded
        }
        MockScenario::MainMenu => proto::RemoteClientOrchestrationState::Unavailable,
        _ => proto::RemoteClientOrchestrationState::Unsupported,
    };
    proto::RemoteClientOrchestrationCapability {
        id: "mock-remote-orchestration".to_string(),
        state: state as i32,
        summary: match state {
            proto::RemoteClientOrchestrationState::LocalOnlyDegraded => {
                "Mock bridge exposes remote-player state but cannot orchestrate an independent remote client."
            }
            proto::RemoteClientOrchestrationState::Unavailable => {
                "No remote-client orchestration is available on this screen."
            }
            _ => "Remote-client orchestration is unsupported by this mock scenario.",
        }
        .to_string(),
        provisional: true,
        ..Default::default()
    }
}
