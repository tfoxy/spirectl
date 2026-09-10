fn multiplayer_restore_result_json(result: &bridge::proto::MultiplayerRestoreResult) -> Value {
    json!({
        "mode": multiplayer_restore_mode_name(result.mode()),
        "remotePlayerMode": result.remote_player_mode,
        "localPlayerId": result.local_player_id,
        "hostPlayerId": result.host_player_id,
        "restoredPlayerIds": result.restored_player_ids,
        "omittedRemotePlayerIds": result.omitted_remote_player_ids,
        "requiresRemoteClients": result.requires_remote_clients,
    })
}

fn multiplayer_file_json(metadata: &MultiplayerRestoreFile) -> Value {
    json!({
        "isMultiplayer": metadata.is_multiplayer,
        "restoreMode": metadata.restore_mode,
        "localPlayerId": metadata.local_player_id,
        "hostPlayerId": metadata.host_player_id,
        "localPlayerRole": metadata.local_player_role,
        "requiresRemoteClients": metadata.requires_remote_clients,
        "degradedLocalOnlyAvailable": metadata.degraded_local_only_available,
        "lobby": metadata.lobby.as_ref().map(|lobby| json!({
            "lobbyId": lobby.lobby_id,
            "phase": lobby.phase,
            "availableCharacters": lobby.available_characters.iter().map(|character| json!({
                "id": character.id,
                "name": character.name,
                "isUnlocked": character.is_unlocked,
            })).collect::<Vec<_>>(),
        })),
        "players": metadata.players.iter().map(|player| json!({
            "id": player.id,
            "netId": player.net_id,
            "slotId": player.slot_id,
            "displayName": player.display_name,
            "selectedCharacterId": player.selected_character_id,
            "character": player.character,
            "isReady": player.is_ready,
            "isLocal": player.is_local,
            "isHost": player.is_host,
            "isRemote": player.is_remote,
        })).collect::<Vec<_>>(),
        "limitations": metadata.limitations.iter().map(|note| json!({
            "code": note.code,
            "message": note.message,
            "field": note.field,
        })).collect::<Vec<_>>(),
    })
}

fn ensure_multiplayer_degradation_allowed(
    multiplayer: Option<&MultiplayerRestoreFile>,
    allow_degraded_local_multiplayer: bool,
    error: fn(i32, &str, &str, Value) -> AppError,
) -> Result<(), AppError> {
    let Some(multiplayer) = multiplayer else {
        return Ok(());
    };
    if !multiplayer.is_multiplayer
        || !multiplayer.requires_remote_clients
        || allow_degraded_local_multiplayer
    {
        return Ok(());
    }
    if !multiplayer.degraded_local_only_available {
        return Err(error(
            2,
            "remote_clients_required",
            "This multiplayer artifact requires remote clients that the local bridge cannot recreate.",
            json!({
                "classification": "unsupported",
                "screen": Value::Null,
                "localPlayerId": multiplayer.local_player_id,
                "hostPlayerId": multiplayer.host_player_id,
                "remotePlayerIds": multiplayer.players.iter().filter(|player| player.is_remote).map(|player| player.id.clone()).collect::<Vec<_>>(),
            }),
        ));
    }
    Err(error(
        2,
        "degradation_flag_required",
        "This active multiplayer artifact can only be restored locally as a degraded single-client state. Re-run with --allow-degraded-local-multiplayer to accept omitted remote clients.",
        json!({
            "classification": "unsupported",
            "localPlayerId": multiplayer.local_player_id,
            "hostPlayerId": multiplayer.host_player_id,
            "remotePlayerIds": multiplayer.players.iter().filter(|player| player.is_remote).map(|player| player.id.clone()).collect::<Vec<_>>(),
            "requiredFlag": "--allow-degraded-local-multiplayer",
        }),
    ))
}

fn wait_for_scenario_validation(
    document: &ScenarioDocumentFile,
    multiplayer_restore: Option<&bridge::proto::MultiplayerRestoreResult>,
    wait: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    wait_for_restore_validation(wait, context, |state| {
        validate_restored_scenario(document, state, multiplayer_restore)
    })
}

fn wait_for_restore_validation<F>(
    wait: LifecycleWaitArgs,
    context: AppContext<'_>,
    mut validate: F,
) -> Result<Value, AppError>
where
    F: FnMut(&Value) -> Result<Value, AppError>,
{
    if context.config.transport.kind == TransportKind::Mock {
        let state = execute_state_json(
            StateArgs {
                perspective: Some(PerspectiveScopeArg::Local),
                ..StateArgs::default()
            },
            context,
        )?;
        return validate(&state);
    }

    let started_at = Instant::now();
    let timeout = Duration::from_millis(wait.timeout_ms);
    let interval = Duration::from_millis(wait.interval_ms);

    loop {
        let state = execute_state_json(
            StateArgs {
                perspective: Some(PerspectiveScopeArg::Local),
                ..StateArgs::default()
            },
            context,
        )?;

        let last_error = match validate(&state) {
            Ok(validation) => return Ok(validation),
            Err(error) => error,
        };

        if started_at.elapsed() >= timeout {
            return Err(last_error);
        }

        thread::sleep(interval);
    }
}

fn validate_restored_scenario(
    document: &ScenarioDocumentFile,
    state: &Value,
    multiplayer_restore: Option<&bridge::proto::MultiplayerRestoreResult>,
) -> Result<Value, AppError> {
    let mut expected = expected_summary_from_scenario(document);
    append_expected_multiplayer_summary(
        &mut expected,
        document.multiplayer.as_ref(),
        multiplayer_restore,
    );
    let validation_state = scenario_validation_state(state);
    let mut observed = observed_summary_from_state(&validation_state, &document.restore.field_reports);
    append_observed_multiplayer_summary(
        &mut observed,
        document.multiplayer.as_ref(),
        &validation_state,
        multiplayer_restore,
    );
    let multiplayer_paths =
        multiplayer_validation_paths(document.multiplayer.as_ref(), multiplayer_restore);
    let mismatches = compare_restore_summaries(
        &expected,
        &observed,
        &document.restore.field_reports,
        &multiplayer_paths,
    );
    if !mismatches.is_empty() {
        let mut extra = json!({
            "classification": "failed",
            "mismatchFields": mismatches.iter().map(|mismatch| mismatch.path.clone()).collect::<Vec<_>>(),
            "expectedSummary": expected,
            "observedSummary": observed,
            "mismatches": mismatches,
        });
        if let Some(multiplayer_restore) = multiplayer_restore {
            extra["multiplayerRestore"] = multiplayer_restore_result_json(multiplayer_restore);
        }
        return Err(scenario_error(
            3,
            "restore_validation_mismatch",
            "Restored state did not match scenario validation keys.",
            extra,
        ));
    }

    let checked = checked_restore_fields(
        &expected,
        &observed,
        &document.restore.field_reports,
        &multiplayer_paths,
    );

    Ok(json!({
        "status": "passed",
        "quality": document.restore.quality,
        "checked": checked,
        "expectedSummary": expected,
        "observedSummary": observed,
        "mismatches": [],
    }))
}

fn expected_summary_from_scenario(document: &ScenarioDocumentFile) -> Value {
    let mut summary = json!({
        // Wire-contract constant, not scenario-specific data — matches the state
        // envelope's `schemaVersion` (see `state.rs`/`stub_service/service_core.rs`),
        // which is what `report.path == "schemaVersion"` is verified against below.
        "schemaVersion": "spirectl.state/v0",
        "screen": { "id": document.source.screen.screen_type },
    });
    if let Some(perspective) = &document.source.perspective {
        summary["perspective"] = json!({ "playerId": perspective.player_id });
        summary["resolvedPerspective"] = json!({ "playerId": perspective.player_id });
    }
    if let Some(run) = &document.run {
        summary["run"] = json!({
            "seed": run.seed,
            "act": run.act,
            "floor": run.floor,
        });
        if let Some(host) = run.players.iter().find(|player| player.is_host) {
            summary["hostPlayerId"] = Value::String(host.id.clone());
        }
    }
    if let Some(multiplayer) = &document.multiplayer
        && !multiplayer.host_player_id.is_empty()
    {
        summary["hostPlayerId"] = Value::String(multiplayer.host_player_id.clone());
    }
    for report in &document.restore.field_reports {
        if report.validation_key
            && let Some(value) = extract_restore_path(&document.screen_state, &report.path)
        {
            set_summary_path(&mut summary, &report.path, value);
        }
    }
    summary
}

fn append_expected_multiplayer_summary(
    summary: &mut Value,
    metadata: Option<&MultiplayerRestoreFile>,
    restore: Option<&bridge::proto::MultiplayerRestoreResult>,
) {
    let Some(metadata) = metadata else {
        return;
    };
    if !metadata.is_multiplayer {
        return;
    }
    let mode = restore
        .map(|restore| multiplayer_restore_mode_name(restore.mode()))
        .unwrap_or(metadata.restore_mode.as_str());
    match mode {
        "lobby-only" => {
            summary["multiplayer"] = json!({
                "localPlayerId": metadata.local_player_id,
                "hostPlayerId": metadata.host_player_id,
                "playerIds": multiplayer_player_ids(&metadata.players),
                "playerSlotIds": multiplayer_player_slot_ids(&metadata.players),
                "playerSelectedCharacterIds": multiplayer_player_selected_character_ids(&metadata.players),
                "playerReadyStates": multiplayer_player_ready_states(&metadata.players),
                "playerLocalRoles": multiplayer_player_local_roles(&metadata.players),
                "playerHostRoles": multiplayer_player_host_roles(&metadata.players),
                "playerRemoteRoles": multiplayer_player_remote_roles(&metadata.players),
            });
        }
        "degraded-local-only" => {
            let restored = restore
                .map(|restore| sorted_strings(restore.restored_player_ids.iter().cloned()))
                .unwrap_or_else(|| {
                    multiplayer_player_ids(metadata.players.iter().filter(|player| player.is_local))
                });
            summary["multiplayer"] = json!({
                "localPlayerId": metadata.local_player_id,
                "hostPlayerId": metadata.host_player_id,
                "playerIds": restored,
            });
        }
        _ => {}
    }
}

fn append_observed_multiplayer_summary(
    summary: &mut Value,
    metadata: Option<&MultiplayerRestoreFile>,
    state: &Value,
    restore: Option<&bridge::proto::MultiplayerRestoreResult>,
) {
    let Some(metadata) = metadata else {
        return;
    };
    if !metadata.is_multiplayer {
        return;
    }
    let mode = restore
        .map(|restore| multiplayer_restore_mode_name(restore.mode()))
        .unwrap_or(metadata.restore_mode.as_str());
    match mode {
        "lobby-only" => {
            let players = state["lobby"]["players"]
                .as_array()
                .cloned()
                .unwrap_or_default();
            summary["multiplayer"] = json!({
                "localPlayerId": state["lobby"]["localPlayerId"],
                "hostPlayerId": state["lobby"]["hostPlayerId"],
                "playerIds": sorted_lobby_player_strings(&players, "id"),
                "playerSlotIds": sorted_lobby_player_numbers(&players, "slotId"),
                "playerSelectedCharacterIds": sorted_lobby_player_strings(&players, "selectedCharacterId"),
                "playerReadyStates": sorted_lobby_player_bools(&players, "isReady"),
                "playerLocalRoles": sorted_lobby_player_bools(&players, "isLocal"),
                "playerHostRoles": sorted_lobby_player_bools(&players, "isHost"),
                "playerRemoteRoles": sorted_lobby_player_bools(&players, "isRemote"),
            });
        }
        "degraded-local-only" => {
            let restored = restore
                .map(|restore| sorted_strings(restore.restored_player_ids.iter().cloned()))
                .unwrap_or_default();
            summary["multiplayer"] = json!({
                "localPlayerId": restore.map(|restore| restore.local_player_id.clone()).unwrap_or_default(),
                "hostPlayerId": restore.map(|restore| restore.host_player_id.clone()).unwrap_or_default(),
                "playerIds": restored,
            });
        }
        _ => {}
    }
}

fn multiplayer_validation_paths(
    metadata: Option<&MultiplayerRestoreFile>,
    restore: Option<&bridge::proto::MultiplayerRestoreResult>,
) -> Vec<&'static str> {
    let Some(metadata) = metadata else {
        return Vec::new();
    };
    if !metadata.is_multiplayer {
        return Vec::new();
    }
    let mode = restore
        .map(|restore| multiplayer_restore_mode_name(restore.mode()))
        .unwrap_or(metadata.restore_mode.as_str());
    match mode {
        "lobby-only" => vec![
            "multiplayer.localPlayerId",
            "multiplayer.hostPlayerId",
            "multiplayer.players[].id",
            "multiplayer.players[].slotId",
            "multiplayer.players[].selectedCharacterId",
            "multiplayer.players[].isReady",
            "multiplayer.players[].isLocal",
            "multiplayer.players[].isHost",
            "multiplayer.players[].isRemote",
        ],
        "degraded-local-only" => vec![
            "multiplayer.localPlayerId",
            "multiplayer.hostPlayerId",
            "multiplayer.players[].id",
        ],
        _ => Vec::new(),
    }
}

/// Derive the recipe `screen.id` validation anchor from current state
/// (`rootScene` + `run.currentRoom.scene`). Returns `None` when the active screen
/// has no corresponding recipe identifier modeled here.
fn derive_validation_screen_id_from_state(state: &Value) -> Option<Value> {
    let root_scene = state.get("rootScene").and_then(Value::as_str)?;
    let id: &str = match root_scene {
        "screens/main_menu" => "main-menu",
        "screens/character_select_screen" => bridge::START_RUN_LOBBY_SCREEN_ID,
        "run" => match state.pointer("/run/currentRoom/scene").and_then(Value::as_str) {
            Some("rooms/combat_room") => "combat",
            Some("rooms/event_room") => bridge::EVENT_ROOM_SCREEN_ID,
            Some("rooms/treasure_room") => bridge::TREASURE_ROOM_SCREEN_ID,
            Some("rooms/rest_site_room") => bridge::REST_SITE_SCREEN_ID,
            Some("rooms/shop_room") => bridge::SHOP_SCREEN_ID,
            Some("rooms/map_room") => bridge::MAP_SCREEN_ID,
            _ => return None,
        },
        _ => return None,
    };
    Some(Value::String(id.to_string()))
}

/// Derive the recipe `combat` validation summary from current state
/// (`run.currentRoom.combat.combatState.roundNumber` and the local player's
/// `run.players[].combat.hand.cards[].id`). `None` when there is no active combat.
fn derive_validation_combat_from_state(state: &Value) -> Option<Value> {
    let turn = state.pointer("/run/currentRoom/combat/combatState/roundNumber")?;
    let players = state["run"]["players"].as_array();
    let local_player = players.and_then(|players| {
        players
            .iter()
            .find(|player| player["isLocal"] == true)
            .or_else(|| players.first())
    });
    let hand = local_player
        .and_then(|player| player["combat"]["hand"]["cards"].as_array())
        .map(|cards| {
            cards
                .iter()
                .map(|card| json!({ "id": card["id"].clone() }))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    Some(json!({ "turn": turn, "hand": hand }))
}

/// Derive the recipe `lobby` validation summary from `characterSelect.lobby`, including
/// player ownership flags calculated from `localPlayerId` and `hostPlayerId`. `None`
/// outside the character-select lobby.
fn derive_validation_lobby_from_state(state: &Value) -> Option<Value> {
    let lobby = state.get("characterSelect")?.get("lobby")?;
    let local_player_id = lobby["localPlayerId"].as_str().unwrap_or_default();
    let host_player_id = lobby["hostPlayerId"].as_str().unwrap_or_default();
    let players = lobby["players"]
        .as_array()?
        .iter()
        .map(|player| {
            let id = player["id"].as_str().unwrap_or_default();
            json!({
                "id": id,
                "slotId": player["slotId"].clone(),
                "selectedCharacterId": player["characterId"].clone(),
                "isReady": player["isReady"].clone(),
                "isLocal": id == local_player_id,
                "isHost": id == host_player_id,
                "isRemote": id != local_player_id,
            })
        })
        .collect::<Vec<_>>();
    Some(json!({
        "localPlayerId": local_player_id,
        "hostPlayerId": host_player_id,
        "players": players,
    }))
}

/// Derive the resolved local-player id from state (`run.view.playerId` for an active
/// run or `characterSelect.view.playerId` in the character-select lobby).
fn derive_validation_resolved_player_id_from_state(state: &Value) -> Option<Value> {
    let player_id = state
        .pointer("/run/view/playerId")
        .and_then(Value::as_str)
        .or_else(|| {
            state
                .pointer("/characterSelect/view/playerId")
                .and_then(Value::as_str)
        })?;
    Some(Value::String(player_id.to_string()))
}

/// Derive the host-player id from state (`characterSelect.lobby.hostPlayerId` in the
/// character-select lobby, otherwise the `run.players[]` entry with `isHost: true`).
fn derive_validation_host_player_id_from_state(state: &Value) -> Option<Value> {
    if let Some(host_player_id) = state
        .pointer("/characterSelect/lobby/hostPlayerId")
        .and_then(Value::as_str)
    {
        return Some(Value::String(host_player_id.to_string()));
    }
    let host = state["run"]["players"]
        .as_array()?
        .iter()
        .find(|player| player["isHost"] == true)?;
    Some(host["id"].clone())
}

/// Build the recipe-validation projection with derived `combat`, `lobby`,
/// `perspective`, and `hostPlayerId` summaries.
fn scenario_validation_state(state: &Value) -> Value {
    let mut shimmed = state.clone();
    if let Some(combat) = derive_validation_combat_from_state(state) {
        shimmed["combat"] = combat;
    }
    if let Some(lobby) = derive_validation_lobby_from_state(state) {
        shimmed["lobby"] = lobby;
    }
    if let Some(player_id) = derive_validation_resolved_player_id_from_state(state) {
        shimmed["perspective"] = json!({ "playerId": player_id });
        shimmed["resolvedPerspective"] = json!({ "playerId": player_id });
    }
    if let Some(host_player_id) = derive_validation_host_player_id_from_state(state) {
        shimmed["hostPlayerId"] = host_player_id;
    }
    shimmed
}

fn observed_summary_from_state(state: &Value, reports: &[RestoreFieldReportFile]) -> Value {
    let observed_screen_id = derive_validation_screen_id_from_state(state)
        .unwrap_or_else(|| state["screen"]["id"].clone());
    let mut summary = json!({
        "screen": { "id": observed_screen_id },
        "perspective": {
            "playerId": state["resolvedPerspective"]["playerId"]
                .as_str()
                .map(Value::from)
                .unwrap_or_else(|| state["perspective"]["playerId"].clone()),
        },
        "run": {
            "seed": state["run"]["seed"].clone(),
            "act": state["run"]["act"].clone(),
            "floor": state["run"]["floor"].clone(),
        },
    });
    for report in reports {
        if report.validation_key
            && let Some(value) = extract_restore_path(state, &report.path)
        {
            set_summary_path(&mut summary, &report.path, value);
        }
    }
    summary
}

fn compare_restore_summaries(
    expected: &Value,
    observed: &Value,
    reports: &[RestoreFieldReportFile],
    extra_paths: &[&'static str],
) -> Vec<RestoreMismatchFile> {
    let paths = compared_restore_paths(reports, extra_paths);
    paths
        .into_iter()
        .filter_map(|path| {
            let expected_value = extract_restore_path(expected, &path.path);
            let observed_value = extract_restore_path(observed, &path.path);
            if expected_value.is_some() && expected_value == observed_value {
                None
            } else {
                Some(RestoreMismatchFile {
                    path: path.path,
                    expected: expected_value.unwrap_or(Value::Null),
                    observed: observed_value.unwrap_or(Value::Null),
                    severity: "failed".to_string(),
                    support_class: path.support_class,
                    reason_code: path.reason_code,
                    suggested_next_step: path.suggested_next_step,
                })
            }
        })
        .collect()
}

fn compared_restore_paths(
    reports: &[RestoreFieldReportFile],
    extra_paths: &[&'static str],
) -> Vec<ComparedRestorePath> {
    let mut paths = reports
        .iter()
        .filter(|report| {
            report.validation_key
                && matches!(report.restore.as_str(), "exact" | "partial" | "inferred")
        })
        .map(|report| ComparedRestorePath {
            path: report.path.clone(),
            support_class: report.restore.clone(),
            reason_code: report.reason_code.clone(),
            suggested_next_step: report.message.clone(),
        })
        .collect::<Vec<_>>();
    for path in ["screen.id"] {
        if !paths.iter().any(|existing| existing.path == path) {
            paths.push(ComparedRestorePath {
                path: path.to_string(),
                support_class: "exact".to_string(),
                reason_code: "core_restore_validation".to_string(),
                suggested_next_step:
                    "Compare the restored public state summary with the scenario artifact."
                        .to_string(),
            });
        }
    }
    for path in extra_paths {
        if !paths.iter().any(|existing| existing.path == *path) {
            paths.push(ComparedRestorePath {
                path: (*path).to_string(),
                support_class: "degraded-local-multiplayer".to_string(),
                reason_code: "multiplayer_restore_validation".to_string(),
                suggested_next_step:
                    "Inspect restored lobby player metadata and omitted remote-client notices."
                        .to_string(),
            });
        }
    }
    paths
}

fn checked_restore_fields(
    expected: &Value,
    observed: &Value,
    reports: &[RestoreFieldReportFile],
    extra_paths: &[&'static str],
) -> Vec<String> {
    let mut checked = Vec::new();
    for report in reports {
        if report.validation_key
            && matches!(report.restore.as_str(), "exact" | "partial" | "inferred")
            && extract_restore_path(expected, &report.path).is_some()
            && extract_restore_path(observed, &report.path).is_some()
        {
            checked.push(report.path.clone());
        }
    }
    for path in [
        "screen.id",
        "perspective.playerId",
        "run.act",
        "run.floor",
        "run.seed",
    ] {
        if !checked.iter().any(|checked| checked == path)
            && extract_restore_path(expected, path).is_some()
            && extract_restore_path(observed, path).is_some()
        {
            checked.push(path.to_string());
        }
    }
    for path in extra_paths {
        if !checked.iter().any(|checked| checked == path)
            && extract_restore_path(expected, path).is_some()
            && extract_restore_path(observed, path).is_some()
        {
            checked.push((*path).to_string());
        }
    }
    checked
}

fn extract_restore_path(value: &Value, path: &str) -> Option<Value> {
    let extracted = match path {
        "screen.id" => value["screen"]["id"].clone(),
        "perspective.playerId" => value["perspective"]["playerId"]
            .as_str()
            .map(Value::from)
            .unwrap_or_else(|| value["resolvedPerspective"]["playerId"].clone()),
        "run.seed" => value["run"]["seed"].clone(),
        "run.act" => value["run"]["act"].clone(),
        "run.floor" => value["run"]["floor"].clone(),
        "combat.turn" => value["combat"]["turn"].clone(),
        "combat.activePlayerId" => value["combat"]["activePlayerId"].clone(),
        "combat.isPlayerTurn" => value["combat"]["isPlayerTurn"].clone(),
        "combat.localPlayer.hp" => value["combat"]["localPlayer"]["hp"].clone(),
        "combat.localPlayer.maxHp" => value["combat"]["localPlayer"]["maxHp"].clone(),
        "combat.localPlayer.block" => value["combat"]["localPlayer"]["block"].clone(),
        "combat.localPlayer.energy" => value["combat"]["localPlayer"]["energy"].clone(),
        "combat.localPlayer.maxEnergy" => value["combat"]["localPlayer"]["maxEnergy"].clone(),
        "combat.hand[].id" | "combat.hand" => sorted_string_array(&value["combat"]["hand"], "id")
            .or_else(|| sorted_string_values(&value["combat"]["handIds"]))
            .or_else(|| sorted_string_values(&value["combat"]["handCardIds"]))
            .unwrap_or(Value::Null),
        "combat.potions[].id" | "combat.potions" => {
            sorted_string_array(&value["combat"]["potions"], "id")
                .or_else(|| sorted_string_values(&value["combat"]["potionIds"]))
                .unwrap_or(Value::Null)
        }
        "combat.enemies[].id" | "combat.enemies" => {
            sorted_string_array(&value["combat"]["enemies"], "id")
                .or_else(|| sorted_string_values(&value["combat"]["enemyIds"]))
                .unwrap_or(Value::Null)
        }
        "combat.enemies[].hp" => sorted_number_array(&value["combat"]["enemies"], "hp")
            .or_else(|| sorted_number_values(&value["combat"]["enemyHp"]))
            .unwrap_or(Value::Null),
        "schemaVersion" => value["schemaVersion"].clone(),
        "resolvedPerspective.playerId" => value["resolvedPerspective"]["playerId"]
            .as_str()
            .map(Value::from)
            .unwrap_or_else(|| value["perspective"]["playerId"].clone()),
        "hostPlayerId" => value["hostPlayerId"]
            .as_str()
            .map(Value::from)
            .unwrap_or_else(|| value["resolvedPerspective"]["hostPlayerId"].clone()),
        "combat.players[].hand[]" => sorted_string_array(&value["combat"]["hand"], "id")
            .or_else(|| sorted_string_values(&value["combat"]["handIds"]))
            .or_else(|| sorted_string_values(&value["combat"]["handCardIds"]))
            .or_else(|| sorted_string_array(&value["combat"]["players"], "hand"))
            .unwrap_or(Value::Null),
        "shop.gold" => value["shop"]["gold"].clone(),
        "shop.purchasableItems[]" => sorted_string_array(&value["shop"]["purchasableItems"], "id")
            .or_else(|| sorted_string_values(&value["shop"]["choiceIds"]))
            .or_else(|| sorted_string_array(&value["choices"], "id"))
            .unwrap_or(Value::Null),
        "lobby.players[]" => sorted_string_array(&value["lobby"]["players"], "id")
            .or_else(|| sorted_string_values(&value["lobby"]["playerIds"]))
            .or_else(|| sorted_string_array(&value["lobby"]["players"], "id"))
            .unwrap_or(Value::Null),
        "choices[].id" => sorted_string_array(&value["choices"], "id")
            .or_else(|| sorted_string_values(&value["choices"]))
            .unwrap_or(Value::Null),
        "availableActions[].kind" => sorted_string_array(&value["availableActions"], "kind")
            .or_else(|| sorted_string_values(&value["availableActions"]))
            .unwrap_or(Value::Null),
        "multiplayer.localPlayerId" => value["multiplayer"]["localPlayerId"].clone(),
        "multiplayer.hostPlayerId" => value["multiplayer"]["hostPlayerId"].clone(),
        "multiplayer.players[].id" => value["multiplayer"]["playerIds"].clone(),
        "multiplayer.players[].slotId" => value["multiplayer"]["playerSlotIds"].clone(),
        "multiplayer.players[].selectedCharacterId" => {
            value["multiplayer"]["playerSelectedCharacterIds"].clone()
        }
        "multiplayer.players[].isReady" => value["multiplayer"]["playerReadyStates"].clone(),
        "multiplayer.players[].isLocal" => value["multiplayer"]["playerLocalRoles"].clone(),
        "multiplayer.players[].isHost" => value["multiplayer"]["playerHostRoles"].clone(),
        "multiplayer.players[].isRemote" => value["multiplayer"]["playerRemoteRoles"].clone(),
        _ => Value::Null,
    };
    if extracted.is_null() {
        None
    } else {
        Some(extracted)
    }
}

fn set_summary_path(summary: &mut Value, path: &str, value: Value) {
    match path {
        "combat.turn" => summary["combat"]["turn"] = value,
        "combat.activePlayerId" => summary["combat"]["activePlayerId"] = value,
        "combat.isPlayerTurn" => summary["combat"]["isPlayerTurn"] = value,
        "combat.localPlayer.hp" => summary["combat"]["localPlayer"]["hp"] = value,
        "combat.localPlayer.maxHp" => summary["combat"]["localPlayer"]["maxHp"] = value,
        "combat.localPlayer.block" => summary["combat"]["localPlayer"]["block"] = value,
        "combat.localPlayer.energy" => summary["combat"]["localPlayer"]["energy"] = value,
        "combat.localPlayer.maxEnergy" => summary["combat"]["localPlayer"]["maxEnergy"] = value,
        "combat.hand" | "combat.hand[].id" => summary["combat"]["handIds"] = value,
        "combat.potions" | "combat.potions[].id" => summary["combat"]["potionIds"] = value,
        "combat.enemies" | "combat.enemies[].id" => summary["combat"]["enemyIds"] = value,
        "combat.enemies[].hp" => summary["combat"]["enemyHp"] = value,
        "schemaVersion" => summary["schemaVersion"] = value,
        "resolvedPerspective.playerId" => summary["resolvedPerspective"]["playerId"] = value,
        "hostPlayerId" => summary["hostPlayerId"] = value,
        "combat.players[].hand[]" => summary["combat"]["handIds"] = value,
        "shop.gold" => summary["shop"]["gold"] = value,
        "shop.purchasableItems[]" => summary["shop"]["purchasableItemIds"] = value,
        "lobby.players[]" => summary["lobby"]["playerIds"] = value,
        "choices[].id" => summary["choices"] = value,
        "availableActions[].kind" => summary["availableActions"] = value,
        _ => {}
    }
}

fn multiplayer_player_ids<'a, I>(players: I) -> Value
where
    I: IntoIterator<Item = &'a MultiplayerPlayerFile>,
{
    sorted_strings(players.into_iter().map(|player| player.id.clone()))
}

fn multiplayer_player_slot_ids(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.slot_id)),
    )
}

fn multiplayer_player_selected_character_ids(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.selected_character_id)),
    )
}

fn multiplayer_player_ready_states(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.is_ready)),
    )
}

fn multiplayer_player_local_roles(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.is_local)),
    )
}

fn multiplayer_player_host_roles(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.is_host)),
    )
}

fn multiplayer_player_remote_roles(players: &[MultiplayerPlayerFile]) -> Value {
    sorted_strings(
        players
            .iter()
            .map(|player| format!("{}={}", player.id, player.is_remote)),
    )
}

fn sorted_lobby_player_strings(players: &[Value], key: &str) -> Value {
    if key == "id" {
        return sorted_strings(players.iter().filter_map(|player| {
            player
                .get(key)
                .and_then(Value::as_str)
                .map(ToString::to_string)
        }));
    }
    sorted_strings(players.iter().filter_map(|player| {
        let id = player.get("id").and_then(Value::as_str)?;
        let value = player.get(key).and_then(Value::as_str).unwrap_or_default();
        Some(format!("{id}={value}"))
    }))
}

fn sorted_lobby_player_numbers(players: &[Value], key: &str) -> Value {
    sorted_strings(players.iter().filter_map(|player| {
        let id = player.get("id").and_then(Value::as_str)?;
        let value = player.get(key).and_then(Value::as_i64)?;
        Some(format!("{id}={value}"))
    }))
}

fn sorted_lobby_player_bools(players: &[Value], key: &str) -> Value {
    sorted_strings(players.iter().filter_map(|player| {
        let id = player.get("id").and_then(Value::as_str)?;
        let value = player.get(key).and_then(Value::as_bool)?;
        Some(format!("{id}={value}"))
    }))
}

fn sorted_strings<I>(values: I) -> Value
where
    I: IntoIterator<Item = String>,
{
    let values = values.into_iter().collect::<BTreeSet<_>>();
    json!(values.into_iter().collect::<Vec<_>>())
}

fn sorted_string_array(value: &Value, key: &str) -> Option<Value> {
    let mut values = value
        .as_array()?
        .iter()
        .filter_map(|item| item[key].as_str().map(ToString::to_string))
        .collect::<Vec<_>>();
    values.sort();
    Some(json!(values))
}

fn sorted_string_values(value: &Value) -> Option<Value> {
    let mut values = value
        .as_array()?
        .iter()
        .filter_map(|item| item.as_str().map(ToString::to_string))
        .collect::<Vec<_>>();
    values.sort();
    Some(json!(values))
}

fn sorted_number_array(value: &Value, key: &str) -> Option<Value> {
    let mut values = value
        .as_array()?
        .iter()
        .filter_map(|item| item[key].as_i64())
        .collect::<Vec<_>>();
    values.sort();
    Some(json!(values))
}

fn sorted_number_values(value: &Value) -> Option<Value> {
    let mut values = value
        .as_array()?
        .iter()
        .filter_map(Value::as_i64)
        .collect::<Vec<_>>();
    values.sort();
    Some(json!(values))
}

fn restore_field_report_from_proto(
    report: bridge::proto::RestoreFieldReport,
) -> RestoreFieldReportFile {
    let capture = restore_field_fidelity_name(report.capture()).to_string();
    let restore = restore_field_fidelity_name(report.restore()).to_string();
    RestoreFieldReportFile {
        path: report.path,
        capture,
        restore,
        validation_key: report.validation_key,
        reason_code: report.reason_code,
        message: report.message,
    }
}

fn restore_field_report_to_proto(
    report: &RestoreFieldReportFile,
) -> bridge::proto::RestoreFieldReport {
    bridge::proto::RestoreFieldReport {
        path: report.path.clone(),
        capture: restore_field_fidelity_from_name(&report.capture) as i32,
        restore: restore_field_fidelity_from_name(&report.restore) as i32,
        validation_key: report.validation_key,
        reason_code: report.reason_code.clone(),
        message: report.message.clone(),
    }
}

fn restore_support_json(screen: &str, quality: &str, reports: &[RestoreFieldReportFile]) -> Value {
    let mut exact = 0u64;
    let mut partial = 0u64;
    let mut inferred = 0u64;
    let mut omitted = 0u64;
    let mut unsupported = 0u64;
    let mut degraded_local_multiplayer = 0u64;
    for fidelity in reports
        .iter()
        .flat_map(|report| [report.capture.as_str(), report.restore.as_str()])
    {
        match fidelity {
            "exact" => exact += 1,
            "partial" => partial += 1,
            "inferred" => inferred += 1,
            "omitted" => omitted += 1,
            "unsupported" => unsupported += 1,
            "degraded-local-multiplayer" => degraded_local_multiplayer += 1,
            _ => unsupported += 1,
        }
    }

    json!({
        "screen": screen,
        "quality": quality,
        "fields": reports.iter().map(restore_field_report_json).collect::<Vec<_>>(),
        "summary": {
            "exact": exact,
            "partial": partial,
            "inferred": inferred,
            "omitted": omitted,
            "unsupported": unsupported,
            "degradedLocalMultiplayer": degraded_local_multiplayer,
        },
    })
}

fn restore_field_report_json(report: &RestoreFieldReportFile) -> Value {
    json!({
        "path": report.path,
        "capture": report.capture,
        "restore": report.restore,
        "validationKey": report.validation_key,
        "reasonCode": report.reason_code,
        "message": report.message,
    })
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    format!("{digest:x}")
}

fn restore_quality_name(quality: bridge::proto::RestoreQuality) -> &'static str {
    match quality {
        bridge::proto::RestoreQuality::Exact => "exact",
        bridge::proto::RestoreQuality::Partial => "partial",
        bridge::proto::RestoreQuality::Unsupported => "unsupported",
        bridge::proto::RestoreQuality::Degraded => "degraded",
        bridge::proto::RestoreQuality::Unspecified => "unspecified",
    }
}

fn restore_field_fidelity_name(fidelity: bridge::proto::RestoreFieldFidelity) -> &'static str {
    match fidelity {
        bridge::proto::RestoreFieldFidelity::Exact => "exact",
        bridge::proto::RestoreFieldFidelity::Partial => "partial",
        bridge::proto::RestoreFieldFidelity::Inferred => "inferred",
        bridge::proto::RestoreFieldFidelity::Omitted => "omitted",
        bridge::proto::RestoreFieldFidelity::Unsupported => "unsupported",
        bridge::proto::RestoreFieldFidelity::DegradedLocalMultiplayer => {
            "degraded-local-multiplayer"
        }
        bridge::proto::RestoreFieldFidelity::Unspecified => "unspecified",
    }
}

fn restore_field_fidelity_from_name(name: &str) -> bridge::proto::RestoreFieldFidelity {
    match name {
        "exact" => bridge::proto::RestoreFieldFidelity::Exact,
        "partial" => bridge::proto::RestoreFieldFidelity::Partial,
        "inferred" => bridge::proto::RestoreFieldFidelity::Inferred,
        "omitted" => bridge::proto::RestoreFieldFidelity::Omitted,
        "unsupported" => bridge::proto::RestoreFieldFidelity::Unsupported,
        "degraded-local-multiplayer" => {
            bridge::proto::RestoreFieldFidelity::DegradedLocalMultiplayer
        }
        _ => bridge::proto::RestoreFieldFidelity::Unspecified,
    }
}

fn restore_verification_status_name(
    status: bridge::proto::RestoreVerificationStatus,
) -> &'static str {
    match status {
        bridge::proto::RestoreVerificationStatus::Passed => "passed",
        bridge::proto::RestoreVerificationStatus::Partial => "partial",
        bridge::proto::RestoreVerificationStatus::Degraded => "degraded",
        bridge::proto::RestoreVerificationStatus::Failed => "failed",
        bridge::proto::RestoreVerificationStatus::Unspecified => "unspecified",
    }
}

fn restore_mode_name(mode: bridge::proto::RestoreMode) -> &'static str {
    match mode {
        bridge::proto::RestoreMode::Sparse => "sparse",
        bridge::proto::RestoreMode::Exact => "exact",
        bridge::proto::RestoreMode::Hybrid => "hybrid",
        bridge::proto::RestoreMode::Unspecified => "unspecified",
    }
}

fn multiplayer_restore_mode_name(mode: bridge::proto::MultiplayerRestoreMode) -> &'static str {
    match mode {
        bridge::proto::MultiplayerRestoreMode::LobbyOnly => "lobby-only",
        bridge::proto::MultiplayerRestoreMode::HostLocalActiveRun => "host-local-active-run",
        bridge::proto::MultiplayerRestoreMode::RemotePlayerPlaceholder => {
            "remote-player-placeholder"
        }
        bridge::proto::MultiplayerRestoreMode::FullActiveMultiplayer => "full-active-multiplayer",
        bridge::proto::MultiplayerRestoreMode::UnsupportedRemoteClientRequired => {
            "unsupported-remote-client-required"
        }
        bridge::proto::MultiplayerRestoreMode::DegradedLocalOnly => "degraded-local-only",
        bridge::proto::MultiplayerRestoreMode::ActiveMultiplayerUnsupported => {
            "active-multiplayer-unsupported"
        }
        bridge::proto::MultiplayerRestoreMode::Unspecified => "unspecified",
    }
}

fn multiplayer_restore_mode_value(mode: &str) -> bridge::proto::MultiplayerRestoreMode {
    match mode {
        "lobby-only" => bridge::proto::MultiplayerRestoreMode::LobbyOnly,
        "host-local-active-run" => bridge::proto::MultiplayerRestoreMode::HostLocalActiveRun,
        "remote-player-placeholder" => {
            bridge::proto::MultiplayerRestoreMode::RemotePlayerPlaceholder
        }
        "full-active-multiplayer" => bridge::proto::MultiplayerRestoreMode::FullActiveMultiplayer,
        "unsupported-remote-client-required" => {
            bridge::proto::MultiplayerRestoreMode::UnsupportedRemoteClientRequired
        }
        "degraded-local-only" => bridge::proto::MultiplayerRestoreMode::DegradedLocalOnly,
        "active-multiplayer-unsupported" => {
            bridge::proto::MultiplayerRestoreMode::ActiveMultiplayerUnsupported
        }
        _ => bridge::proto::MultiplayerRestoreMode::Unspecified,
    }
}

fn perspective_scope_name(scope: bridge::proto::PerspectiveScope) -> &'static str {
    match scope {
        bridge::proto::PerspectiveScope::Local => "local",
        bridge::proto::PerspectiveScope::Omniscient => "omniscient",
        bridge::proto::PerspectiveScope::Unspecified => "unspecified",
    }
}
