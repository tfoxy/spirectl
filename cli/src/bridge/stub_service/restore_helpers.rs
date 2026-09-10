fn mock_restore_response(
    request_id: String,
    screen: &str,
    quality: proto::RestoreQuality,
    multiplayer_restore: Option<proto::MultiplayerRestoreResult>,
    exact_bundle_used: bool,
) -> proto::ScenarioRestoreResponse {
    proto::ScenarioRestoreResponse {
        request_id,
        quality: quality as i32,
        screen: Some(proto::ScreenInfo {
            id: screen.to_string(),
            title: mock_screen_title(screen).to_string(),
            screen_instance_id: format!("screen:{screen}:1"),
            source: String::new(),
            raw_type: String::new(),
            class_name: String::new(),
        }),
        resolved_perspective: Some(proto::PerspectiveInfo {
            scope: proto::PerspectiveScope::Local as i32,
            player_id: if mock_lobby_screen_id_supported(screen) {
                "p:100".to_string()
            } else {
                "p1".to_string()
            },
            uses_default: false,
            ..Default::default()
        }),
        notices: vec![],
        exact_bundle_used,
        sparse_fallback_used: false,
        compatibility_notes: vec![proto::RestoreCompatibilityNote {
            code: "fixture-backed-restore".to_string(),
            message: "Mock scenario restore used fixture-backed partial realization.".to_string(),
            field: String::new(),
        }],
        verification: Some(mock_restore_verification(screen, quality)),
        multiplayer_restore,
    }
}

fn mock_restore_verification(
    screen: &str,
    quality: proto::RestoreQuality,
) -> proto::RestoreVerification {
    if screen == SHOP_SCREEN_ID {
        return proto::RestoreVerification {
            status: proto::RestoreVerificationStatus::Failed as i32,
            quality: quality as i32,
            checked_fields: vec!["screen.id".to_string(), "choices[].id".to_string()],
            expected_summary_json: serde_json::json!({
                "screen": { "id": screen },
                "choices": ["shop:leave"],
            })
            .to_string(),
            observed_summary_json: serde_json::json!({
                "screen": { "id": screen },
                "choices": ["shop:p1:card:strike:0", "shop:leave"],
            })
            .to_string(),
            mismatches: vec![proto::RestoreMismatch {
                path: "choices[].id".to_string(),
                expected_json: serde_json::json!(["shop:leave"]).to_string(),
                observed_json: serde_json::json!(["shop:p1:card:strike:0", "shop:leave"])
                    .to_string(),
                severity: "warning".to_string(),
                support_class: "inferred".to_string(),
                reason_code: "fixture-choice-expansion".to_string(),
                suggested_next_step:
                    "Inspect validation.expectedSummary and validation.observedSummary for restored choices."
                        .to_string(),
            }],
        };
    }

    proto::RestoreVerification {
        status: proto::RestoreVerificationStatus::Passed as i32,
        quality: quality as i32,
        checked_fields: vec!["screen.id".to_string()],
        expected_summary_json: serde_json::json!({
            "screen": { "id": screen },
        })
        .to_string(),
        observed_summary_json: serde_json::json!({
            "screen": { "id": screen },
        })
        .to_string(),
        mismatches: Vec::new(),
    }
}

fn mock_scenario_for_screen(screen: &str) -> Option<MockScenario> {
    match screen {
        "combat" => Some(MockScenario::Combat),
        SHOP_SCREEN_ID => Some(MockScenario::Shop),
        START_RUN_LOBBY_SCREEN_ID | LOAD_RUN_LOBBY_SCREEN_ID => Some(MockScenario::Lobby),
        _ => None,
    }
}

fn mock_checkpoint_restore_response(
    request_id: String,
    screen: &str,
    quality: proto::RestoreQuality,
    multiplayer_restore: Option<proto::MultiplayerRestoreResult>,
    exact_bundle_used: bool,
) -> proto::CheckpointRestoreResponse {
    proto::CheckpointRestoreResponse {
        request_id,
        quality: quality as i32,
        screen: Some(proto::ScreenInfo {
            id: screen.to_string(),
            title: mock_screen_title(screen).to_string(),
            screen_instance_id: format!("screen:{screen}:1"),
            source: String::new(),
            raw_type: String::new(),
            class_name: String::new(),
        }),
        resolved_perspective: Some(proto::PerspectiveInfo {
            scope: proto::PerspectiveScope::Local as i32,
            player_id: if mock_lobby_screen_id_supported(screen) {
                "p:100".to_string()
            } else {
                "p1".to_string()
            },
            uses_default: true,
            ..Default::default()
        }),
        notices: Vec::new(),
        exact_bundle_used,
        sparse_fallback_used: false,
        compatibility_notes: Vec::new(),
        verification: None,
        multiplayer_restore,
    }
}

fn mock_screen_title(screen: &str) -> &'static str {
    match screen {
        SHOP_SCREEN_ID => "Shop",
        START_RUN_LOBBY_SCREEN_ID => "Character Select",
        LOAD_RUN_LOBBY_SCREEN_ID => "Load Run Lobby",
        _ => "Combat",
    }
}

fn mock_multiplayer_metadata(local_ready: bool) -> proto::MultiplayerRestoreMetadata {
    let local_character = if local_ready { "silent" } else { "ironclad" };
    proto::MultiplayerRestoreMetadata {
        is_multiplayer: true,
        restore_mode: proto::MultiplayerRestoreMode::LobbyOnly as i32,
        local_player_id: "p:100".to_string(),
        host_player_id: "p:100".to_string(),
        local_player_role: "host".to_string(),
        players: vec![
            proto::MultiplayerPlayerState {
                id: "p:100".to_string(),
                net_id: "100".to_string(),
                slot_id: 0,
                display_name: String::new(),
                selected_character_id: local_character.to_string(),
                is_ready: local_ready,
                is_local: true,
                is_host: true,
                is_remote: false,
                character: local_character.to_string(),
            },
            proto::MultiplayerPlayerState {
                id: "p:200".to_string(),
                net_id: "200".to_string(),
                slot_id: 1,
                display_name: String::new(),
                selected_character_id: "ironclad".to_string(),
                is_ready: true,
                is_local: false,
                is_host: false,
                is_remote: true,
                character: "ironclad".to_string(),
            },
        ],
        lobby: Some(proto::MultiplayerLobbySnapshot {
            lobby_id: "start-run".to_string(),
            phase: if local_ready {
                "ready".to_string()
            } else {
                "selecting".to_string()
            },
            available_characters: vec![
                proto::LobbyCharacter {
                    id: "ironclad".to_string(),
                    name: "Ironclad".to_string(),
                    is_unlocked: true,
                    ..Default::default()
                },
                proto::LobbyCharacter {
                    id: "silent".to_string(),
                    name: "Silent".to_string(),
                    is_unlocked: true,
                    ..Default::default()
                },
                proto::LobbyCharacter {
                    id: "defect".to_string(),
                    name: "Defect".to_string(),
                    is_unlocked: true,
                    ..Default::default()
                },
            ],
        }),
        requires_remote_clients: false,
        degraded_local_only_available: false,
        limitations: Vec::new(),
    }
}

fn mock_lobby_multiplayer_restore_result(
    metadata: &proto::MultiplayerRestoreMetadata,
) -> proto::MultiplayerRestoreResult {
    proto::MultiplayerRestoreResult {
        mode: proto::MultiplayerRestoreMode::LobbyOnly as i32,
        remote_player_mode: "placeholder".to_string(),
        local_player_id: metadata.local_player_id.clone(),
        host_player_id: metadata.host_player_id.clone(),
        restored_player_ids: metadata
            .players
            .iter()
            .map(|player| player.id.clone())
            .collect(),
        omitted_remote_player_ids: Vec::new(),
        requires_remote_clients: false,
    }
}

fn mock_degraded_multiplayer_restore_result(
    metadata: &proto::MultiplayerRestoreMetadata,
) -> proto::MultiplayerRestoreResult {
    proto::MultiplayerRestoreResult {
        mode: proto::MultiplayerRestoreMode::DegradedLocalOnly as i32,
        remote_player_mode: "omitted".to_string(),
        local_player_id: metadata.local_player_id.clone(),
        host_player_id: metadata.host_player_id.clone(),
        restored_player_ids: metadata
            .players
            .iter()
            .filter(|player| player.is_local)
            .map(|player| player.id.clone())
            .collect(),
        omitted_remote_player_ids: metadata
            .players
            .iter()
            .filter(|player| player.is_remote)
            .map(|player| player.id.clone())
            .collect(),
        requires_remote_clients: true,
    }
}

fn multiplayer_degradation_required_error(
    metadata: &proto::MultiplayerRestoreMetadata,
) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::InvalidFixture,
        "degradation_flag_required: this active multiplayer artifact can only be restored locally as a degraded single-client state.",
        &[
            detail(
                "code",
                "degradation_flag_required",
                "Explicit degradation opt-in is required.",
            ),
            detail(
                "requiredFlag",
                "--allow-degraded-local-multiplayer",
                "Pass this flag to omit remote clients intentionally.",
            ),
            detail(
                "remotePlayerIds",
                &metadata
                    .players
                    .iter()
                    .filter(|player| player.is_remote)
                    .map(|player| player.id.as_str())
                    .collect::<Vec<_>>()
                    .join(","),
                "Remote players cannot be recreated by one local mock bridge.",
            ),
        ],
    )
}

fn mock_checkpoint_summary_json(screen: &str) -> String {
    json!({
        "schemaVersion": "spirectl.checkpoint.summary/v0",
        "screen": {
            "id": screen,
            "title": match screen {
                SHOP_SCREEN_ID => "Shop",
                START_RUN_LOBBY_SCREEN_ID => "Character Select",
                LOAD_RUN_LOBBY_SCREEN_ID => "Load Run Lobby",
                _ => "Combat",
            },
        },
        "perspective": {
            "scope": "local",
            "playerId": if mock_lobby_screen_id_supported(screen) { "p:100" } else { "p1" },
        },
        "run": {
            "seed": "MIL2-CONTRACT-SEED",
            "act": 1,
            "floor": if screen == SHOP_SCREEN_ID { 4 } else { 3 },
        },
        "combat": if screen == "combat" {
            json!({
                "turn": 2,
                "activePlayerId": "p1",
                "playerHp": 67,
            })
        } else {
            Value::Null
        },
        "shop": if screen == SHOP_SCREEN_ID {
            json!({
                "gold": 123,
            })
        } else {
            Value::Null
        },
        "lobby": if mock_lobby_screen_id_supported(screen) {
            json!({
                "lobbyId": "start-run",
                "localPlayerId": "p:100",
                "hostPlayerId": "p:100",
                "players": [{"id": "p:100"}, {"id": "p:200"}],
            })
        } else {
            Value::Null
        },
        "validationKeys": ["screen.id", "perspective.playerId", "run.act", "run.floor"],
    })
    .to_string()
}

fn mock_restore_field_reports(screen: &str) -> Vec<proto::RestoreFieldReport> {
    let mut reports = vec![
        mock_restore_field_report(
            "schemaVersion",
            proto::RestoreFieldFidelity::Exact,
            proto::RestoreFieldFidelity::Exact,
            true,
            "runtime-schema-version",
            "Runtime schema version is captured from the public state envelope.",
        ),
        mock_restore_field_report(
            "screen.id",
            proto::RestoreFieldFidelity::Exact,
            proto::RestoreFieldFidelity::Exact,
            true,
            "screen-id",
            "Screen id is captured and verified.",
        ),
        mock_restore_field_report(
            "screen.className",
            proto::RestoreFieldFidelity::Exact,
            proto::RestoreFieldFidelity::Inferred,
            false,
            "screen-class-name-metadata",
            "Screen class name is diagnostic metadata and is not a sparse restore input.",
        ),
        mock_restore_field_report(
            "resolvedPerspective.playerId",
            proto::RestoreFieldFidelity::Exact,
            proto::RestoreFieldFidelity::Exact,
            true,
            "resolved-perspective-player",
            "Resolved perspective player id is captured and verified.",
        ),
        mock_restore_field_report(
            "hostPlayerId",
            proto::RestoreFieldFidelity::Exact,
            proto::RestoreFieldFidelity::Partial,
            true,
            "host-player-id",
            "Host player id is captured when the runtime exposes ownership metadata.",
        ),
        mock_restore_field_report(
            "privateUiInternals",
            proto::RestoreFieldFidelity::Omitted,
            proto::RestoreFieldFidelity::Unsupported,
            false,
            "private-ui-internals-omitted",
            "Private UI internals and scene-tree details are intentionally outside default state and restore support.",
        ),
    ];
    match screen {
        SHOP_SCREEN_ID => reports.extend([
            mock_restore_field_report(
                "shop.purchasableItems[]",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Inferred,
                true,
                "shop-purchasableItems-typed-section",
                "Shop purchasable items and controls are captured as public typed state and replayed through fixture recipes.",
            ),
            mock_restore_field_report(
                "choices[].preferredActionRef",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Inferred,
                false,
                "choices-v0-preferred-action",
                "Preferred action references are captured and re-derived from live action legality.",
            ),
            mock_restore_field_report(
                "availableActions[].ownerPlayerId",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Partial,
                true,
                "available-actions-v0-owner",
                "Action ownership metadata is captured; one local bridge cannot claim unconfigured remote ownership.",
            ),
        ]),
        START_RUN_LOBBY_SCREEN_ID | LOAD_RUN_LOBBY_SCREEN_ID => reports.extend([
            mock_restore_field_report(
                "lobby.players[]",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Inferred,
                true,
                "lobby-players-typed-section",
                "Multiplayer lobby players and local controls are captured as public typed state and replayed through fixture recipes.",
            ),
            mock_restore_field_report(
                "multiplayer.players[isRemote=true]",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::DegradedLocalMultiplayer,
                true,
                "remote-player-degraded-local",
                "Remote-owned player metadata is captured, but allow-degraded local replay omits remote clients explicitly.",
            ),
            mock_restore_field_report(
                "multiplayer.remoteRuntime",
                proto::RestoreFieldFidelity::Omitted,
                proto::RestoreFieldFidelity::Unsupported,
                false,
                "remote-clients-not-captured",
                "Remote client runtime processes are not captured by the local bridge and are never reported as successful replay.",
            ),
        ]),
        _ => reports.extend([
            mock_restore_field_report(
                "combat.turn",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Exact,
                true,
                "combat-turn",
                "Combat turn is captured from public combat state and verified.",
            ),
            mock_restore_field_report(
                "combat.players[].hand[]",
                proto::RestoreFieldFidelity::Exact,
                proto::RestoreFieldFidelity::Partial,
                true,
                "combat-hand-observable",
                "Visible hand cards are captured by stable card ids; sparse restore recreates the visible hand without hidden draw sequencing.",
            ),
            mock_restore_field_report(
                "combat.drawPile",
                proto::RestoreFieldFidelity::Partial,
                proto::RestoreFieldFidelity::Unsupported,
                false,
                "combat-pile-observable-subset",
                "Pile count and exposed card ids are public when available, but exact pile order continuation is unsupported.",
            ),
            mock_restore_field_report(
                "combat.rngContinuation",
                proto::RestoreFieldFidelity::Omitted,
                proto::RestoreFieldFidelity::Unsupported,
                false,
                "rng-continuation-omitted",
                "RNG continuation is not exposed through a supported public save/runtime path.",
            ),
        ]),
    }
    reports
}

fn mock_restore_field_report(
    path: &str,
    capture: proto::RestoreFieldFidelity,
    restore: proto::RestoreFieldFidelity,
    validation_key: bool,
    reason_code: &str,
    message: &str,
) -> proto::RestoreFieldReport {
    proto::RestoreFieldReport {
        path: path.to_string(),
        capture: capture as i32,
        restore: restore as i32,
        validation_key,
        reason_code: reason_code.to_string(),
        message: message.to_string(),
    }
}

fn mock_restore_field_reports_json(screen: &str) -> Vec<Value> {
    mock_restore_field_reports(screen)
        .into_iter()
        .map(|report| {
            json!({
                "path": report.path,
                "capture": mock_restore_field_fidelity_name(report.capture),
                "restore": mock_restore_field_fidelity_name(report.restore),
                "validationKey": report.validation_key,
                "reasonCode": report.reason_code,
                "message": report.message,
            })
        })
        .collect()
}

fn mock_restore_field_fidelity_name(fidelity: i32) -> &'static str {
    match proto::RestoreFieldFidelity::try_from(fidelity)
        .unwrap_or(proto::RestoreFieldFidelity::Unspecified)
    {
        proto::RestoreFieldFidelity::Exact => "exact",
        proto::RestoreFieldFidelity::Partial => "partial",
        proto::RestoreFieldFidelity::Inferred => "inferred",
        proto::RestoreFieldFidelity::Omitted => "omitted",
        proto::RestoreFieldFidelity::Unsupported => "unsupported",
        proto::RestoreFieldFidelity::DegradedLocalMultiplayer => "degraded-local-multiplayer",
        proto::RestoreFieldFidelity::Unspecified => "unspecified",
    }
}

fn mock_checkpoint_bundle_json(name: &str, screen: &str) -> String {
    json!({
        "formatVersion": "spirectl.checkpoint.bundle/v0",
        "name": name,
        "capturedAt": "2026-04-24T00:00:00Z",
        "screen": screen,
        "quality": "partial",
        "fieldReports": mock_restore_field_reports_json(screen),
        "fixture": {
            "schemaVersion": "spirectl.fixture/v0",
            "name": format!("checkpoint-{name}"),
            "screen": screen,
            "run": {
                "seed": "MOCK",
                "currentActIndex": 0,
                "actFloor": if screen == SHOP_SCREEN_ID { 4 } else { 3 },
                "view": { "playerId": "p1" },
                "players": [{
                    "id": "p1",
                    "characterId": "ironclad",
                    "creature": { "currentHp": 67, "maxHp": 80 },
                }],
            },
        },
        "screenState": {},
    })
    .to_string()
}

fn mock_scenario_screen_state_json(screen: &str) -> String {
    json!({
        "combat": if screen == "combat" {
            json!({
                "activePlayerId": "p1",
                "turn": 2,
                "round": 1,
                "player": {
                    "id": "p1",
                    "hp": 67,
                    "maxHp": 80,
                    "block": 0,
                    "energy": 3,
                },
                "handCardIds": ["c_1", "c_2"],
                "drawPileCardIds": ["defend"],
                "discardPileCardIds": [],
                "exhaustPileCardIds": [],
                "potionIds": ["fire-potion"],
            })
        } else {
            Value::Null
        },
        "shop": if screen == SHOP_SCREEN_ID {
            json!({
                "gold": 123,
                "cardIds": ["strike", "bash"],
                "relicIds": ["anchor"],
                "potionIds": ["fire-potion"],
                "cardRemovalAvailable": true,
                "choiceIds": ["shop:relic:anchor:0"],
                "availableActionKinds": ["choose"],
            })
        } else {
            Value::Null
        },
        "lobby": if mock_lobby_screen_id_supported(screen) {
            json!({
                "lobbyId": "start-run",
                "localPlayerId": "p:100",
                "hostPlayerId": "p:100",
                "players": [{"id": "p:100"}, {"id": "p:200"}],
            })
        } else {
            Value::Null
        },
        "choices": if screen == SHOP_SCREEN_ID {
            json!([{
                "id": "shop:relic:anchor:0",
                "label": "Anchor",
                "ownerPlayerId": "p1",
            }])
        } else if mock_lobby_screen_id_supported(screen) {
            json!([
                { "id": "lobby:ready", "label": "Ready", "ownerPlayerId": "p:100" },
                { "id": "lobby:character:ironclad", "label": "Select Ironclad", "ownerPlayerId": "p:100" },
                { "id": "lobby:character:silent", "label": "Select Silent", "ownerPlayerId": "p:100" },
                { "id": "lobby:character:defect", "label": "Select Defect", "ownerPlayerId": "p:100" }
            ])
        } else {
            json!([{
                "id": "combat:card:strike:0",
                "label": "Strike",
                "ownerPlayerId": "p1",
            }])
        },
        "availableActions": if screen == SHOP_SCREEN_ID {
            json!([{
                "kind": "choose",
                "arguments": {
                    "choiceId": "shop:relic:anchor:0",
                },
            }])
        } else if mock_lobby_screen_id_supported(screen) {
            json!([{
                "kind": "ready",
                "arguments": {
                    "playerId": "p:100",
                },
            }])
        } else {
            json!([{
                "kind": "play-card",
                "arguments": {
                    "cardId": "strike",
                },
            }])
        },
    })
    .to_string()
}

fn mock_scenario_bundle_json(name: &str, screen: &str) -> String {
    json!({
        "formatVersion": "spirectl.scenario.bundle/v0",
        "name": name,
        "capturedAt": "2026-04-24T00:00:00Z",
        "screen": screen,
        "quality": "partial",
        "fieldReports": mock_restore_field_reports_json(screen),
        "fixture": {
            "schemaVersion": "spirectl.fixture/v0",
            "name": format!("scenario-{name}"),
            "screen": screen,
            "run": {
                "seed": "MOCK",
                "currentActIndex": 0,
                "actFloor": if screen == SHOP_SCREEN_ID { 4 } else { 3 },
                "view": { "playerId": "p1" },
                "players": [{
                    "id": "p1",
                    "characterId": "ironclad",
                    "creature": { "currentHp": 67, "maxHp": 80 },
                }],
            },
        },
        "screenState": serde_json::from_str::<Value>(&mock_scenario_screen_state_json(screen))
            .expect("mock scenario screen state json"),
    })
    .to_string()
}

fn recorded_fixture_json_for_mock_combat() -> String {
    json!({
        "schemaVersion": "spirectl.fixture/v0",
        "name": "recorded-current-screen",
        "description": "Recorded screen-entry fixture for combat captured from screen:combat:1.",
        "screen": "combat",
        "run": {
            "currentActIndex": 0,
            "actFloor": 3,
            "seed": "MIL2-CONTRACT-SEED",
            "view": {
                "playerId": "p1"
            },
            "players": [
                {
                    "id": "p1",
                    "characterId": "ironclad",
                    "creature": { "currentHp": 67, "maxHp": 80 }
                }
            ],
            "currentRoom": {
                "combat": {
                    "encounterId": "nibbits-normal"
                }
            }
        }
    })
    .to_string()
}

fn recorded_fixture_combat_omissions() -> Vec<proto::RestoreCompatibilityNote> {
    vec![
        proto::RestoreCompatibilityNote {
            code: "mid-combat-deltas-omitted".to_string(),
            message: "Played-card history, current queues, turn-local relic history, and transient combat UI state are not recorded.".to_string(),
            field: "combat".to_string(),
        },
        proto::RestoreCompatibilityNote {
            code: "rng-continuation-omitted".to_string(),
            message: "Runtime RNG continuation is not recorded.".to_string(),
            field: "run.rng".to_string(),
        },
    ]
}

fn recorded_fixture_entry_notices() -> Vec<proto::StateNotice> {
    vec![proto::StateNotice {
        code: "screen-entry-fixture".to_string(),
        message: "This artifact recreates the screen-entry recipe, not the current runtime frame."
            .to_string(),
        provisional: false,
        path: String::new(),
        severity: String::new(),
        source: String::new(),
        stability: String::new(),
        perspective: String::new(),
    }]
}

