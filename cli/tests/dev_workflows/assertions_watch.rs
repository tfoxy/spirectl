use super::*;

#[test]
fn dev_assert_equals_succeeds_for_matching_path() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "assert",
        "rootScene",
        "--equals",
        "screens/main_menu",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["path"], "rootScene");
    assert_eq!(payload["actual"], "screens/main_menu");
}

#[test]
fn dev_assert_regex_succeeds_for_matching_string_path() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "assert",
        "rootScene",
        "--regex",
        "^screens/main.*$",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["operator"], "regex");
    assert_eq!(payload["expected"], "^screens/main.*$");
}

#[test]
fn dev_assert_wildcard_succeeds_for_matching_choice_id() {
    // state has no top-level `choices` array; the combat mock's single enemy
    // (`run.currentRoom.combat.combatState.enemies`) gives an equally simple
    // single-element wildcard target.
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "run.currentRoom.combat.combatState.enemies[*].id",
        "--equals",
        "e_1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["e_1"]));
}

#[test]
fn dev_assert_filter_succeeds_for_matching_choice_id() {
    // state has no top-level `choices` array; the combat mock's hand cards give
    // a real two-element array to filter by id.
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "run.players[id=p1].combat.hand.cards[id=c_1].id",
        "--equals",
        "c_1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["c_1"]));
}

#[test]
fn dev_assert_comparison_filter_succeeds_for_matching_combat_player() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "run.players[combat.energy>=3].id",
        "--equals",
        "p1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    // Both mock players sit in combat with energy 3 in this scenario, so the
    // comparison filter legitimately matches both ids; "equals p1" only needs one.
    assert_eq!(payload["actual"], serde_json::json!(["p1", "p2"]));
}

#[test]
fn restore_support_matrix_current() {
    let config = combat_mock_config();
    let temp_dir = tempfile::tempdir().expect("tempdir");
    let output = temp_dir.path().join("combat.sts2.scenario.yaml");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "scenario",
        "export",
        "--output",
        output.to_str().expect("utf8 output"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let fields = payload["restoreSupport"]["fields"]
        .as_array()
        .expect("restore support fields");
    let by_path = |path: &str| {
        fields
            .iter()
            .find(|field| field["path"] == path)
            .unwrap_or_else(|| panic!("missing restore support field {path}"))
    };

    assert_eq!(by_path("schemaVersion")["restore"], "exact");
    assert_eq!(by_path("screen.className")["restore"], "inferred");
    assert_eq!(by_path("hostPlayerId")["restore"], "partial");
    assert_eq!(by_path("combat.players[].hand[]")["restore"], "partial");
    assert_eq!(by_path("combat.drawPile")["capture"], "partial");
    assert_eq!(by_path("combat.drawPile")["restore"], "unsupported");
    assert_eq!(by_path("combat.rngContinuation")["capture"], "omitted");
    assert_eq!(by_path("privateUiInternals")["restore"], "unsupported");
    assert!(
        payload["restoreSupport"]["summary"]["exact"]
            .as_u64()
            .expect("exact count")
            > 0
    );

    let lobby_config = lobby_mock_config();
    let lobby_output = temp_dir.path().join("lobby.sts2.scenario.yaml");
    let lobby_response = run(&[
        "sts2",
        "--json",
        "--config",
        lobby_config.path().to_str().expect("utf8 path"),
        "dev",
        "scenario",
        "export",
        "--output",
        lobby_output.to_str().expect("utf8 output"),
    ]);
    let lobby_payload: Value = serde_json::from_str(&lobby_response.stdout).expect("json");
    let lobby_fields = lobby_payload["restoreSupport"]["fields"]
        .as_array()
        .expect("lobby restore support fields");
    assert!(lobby_fields.iter().any(|field| {
        field["path"] == "multiplayer.players[isRemote=true]"
            && field["restore"] == "degraded-local-multiplayer"
    }));
    assert!(lobby_fields.iter().any(|field| {
        field["path"] == "multiplayer.remoteRuntime" && field["restore"] == "unsupported"
    }));
    assert_eq!(
        lobby_payload["restoreSupport"]["summary"]["degradedLocalMultiplayer"],
        1
    );
}

#[test]
fn dev_assert_contains_filter_succeeds_for_matching_choice_label() {
    // Query against the resolved `state actions` document (source: actions) — the
    // current replacement for the legacy top-level `choices` array. "Turn" uniquely
    // substring-matches the combat mock's "End Turn" action label.
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "actions[label*=Turn].id",
        "--source",
        "actions",
        "--equals",
        "action:combat-room:end-turn",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(
        payload["actual"],
        serde_json::json!(["action:combat-room:end-turn"])
    );
}

#[test]
fn dev_assert_nested_filter_succeeds_for_matching_action_argument() {
    // current's action items flatten `preferredAction` away and nest structured
    // arguments under `args` instead (see cli/src/state_actions.rs `emit_action`);
    // this exercises the same "filter by id, then read a nested leaf" mechanic
    // against a real nested `args.cardId` argument.
    let config = card_overlay_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "actions[id=action:choose-a-card:select-card:card:p1:choose-a-card:0].args.cardId",
        "--source",
        "actions",
        "--equals",
        "card-selection:card:DEMONIC_SHIELD:0",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(
        payload["actual"],
        serde_json::json!(["card-selection:card:DEMONIC_SHIELD:0"])
    );
}

#[test]
fn dev_assert_object_wide_filter_succeeds_for_matching_nested_leaf() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "actions[=action:combat-room:end-turn].kind",
        "--source",
        "actions",
        "--equals",
        "end-turn",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["end-turn"]));
}

#[test]
fn dev_assert_contains_fails_with_clear_payload() {
    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "assert",
        "screen.id",
        "--contains",
        "combat",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "assertion_failed");
    assert_eq!(payload["error"]["path"], "screen.id");
}

#[test]
fn dev_assert_rejects_invalid_regex_patterns() {
    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "assert",
        "screen.id",
        "--regex",
        "[unterminated",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_query");
    assert_eq!(payload["error"]["path"], "screen.id");
}

#[test]
fn dev_assert_missing_by_id_path_reports_resolution_details() {
    // state's `characterSelect.lobby` has no id-keyed player map anymore (only
    // an array, `lobby.players[]` — by-id lookup there is an array filter, not an
    // object-key lookup); this now demonstrates the missing-object-key resolution
    // mechanic (resolvedPrefix/missingField/availableKeys) against the lobby
    // object's own fixed fields instead.
    let config = lobby_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "assert",
        "characterSelect.lobby.p:404.isReady",
        "--exists",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "assertion_failed");
    assert_eq!(
        payload["error"]["path"],
        "characterSelect.lobby.p:404.isReady"
    );
    assert_eq!(
        payload["error"]["resolution"]["resolvedPrefix"],
        "characterSelect.lobby"
    );
    assert_eq!(payload["error"]["resolution"]["missingField"], "p:404");
    assert!(
        payload["error"]["resolution"]["availableKeys"]
            .as_array()
            .expect("available keys")
            .iter()
            .any(|value| value == "localPlayerId")
    );
}

#[test]
fn dev_wait_for_times_out_when_condition_never_matches() {
    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "wait-for",
        "screen.id",
        "--equals",
        "combat",
        "--timeout-ms",
        "20",
        "--interval-ms",
        "1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "wait_timeout");
    assert_eq!(payload["error"]["path"], "screen.id");
}

#[test]
fn dev_wait_for_wildcard_succeeds_for_matching_choice_id() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "wait-for",
        "run.currentRoom.combat.combatState.enemies[*].id",
        "--equals",
        "e_1",
        "--timeout-ms",
        "20",
        "--interval-ms",
        "1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["e_1"]));
}

#[test]
fn dev_wait_for_filter_succeeds_for_matching_choice_id() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "wait-for",
        "run.players[id=p1].combat.hand.cards[id=c_1].id",
        "--equals",
        "c_1",
        "--timeout-ms",
        "20",
        "--interval-ms",
        "1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["c_1"]));
}

#[test]
fn dev_wait_for_inequality_filter_succeeds_for_matching_reward_choice_id() {
    // The mock `rewards` scenario only advertises the legacy rewards screen id, not
    // populated `run.players[].overlays[].rewards` data (see
    // cli/src/bridge/mock_data/state_run.rs `mock_overlays`), so a real inequality
    // filter is exercised against the combat mock's hand cards instead: excluding
    // GUARD leaves exactly one card, JAB's `c_1`.
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "wait-for",
        "run.players[id=p1].combat.hand.cards[modelId!=GUARD].id",
        "--equals",
        "c_1",
        "--timeout-ms",
        "20",
        "--interval-ms",
        "1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["c_1"]));
}

#[test]
fn dev_wait_for_regex_filter_succeeds_for_matching_reward_choice_label() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "wait-for",
        "run.players[id=p1].combat.hand.cards[id~=^c_].modelId",
        "--equals",
        "JAB",
        "--timeout-ms",
        "20",
        "--interval-ms",
        "1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], serde_json::json!(["JAB", "GUARD"]));
}

#[cfg(unix)]
#[tokio::test]
async fn dev_wait_for_succeeds_over_ipc_when_state_changes() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-m4.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitioningBridgeService::default();

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "wait-for",
            "run.currentRoom.scene",
            "--equals",
            "rooms/combat_room",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join wait-for task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["actual"], "rooms/combat_room");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_wait_for_transitions_succeeds_after_stable_quiescent_samples() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-transitions.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitionStatusBridgeService::new(2);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "wait-for-transitions",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "1",
            "--stable-samples",
            "2",
        ])
    })
    .await
    .expect("join wait-for-transitions task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["quiescent"], true);
    assert_eq!(payload["requiredStableSamples"], 2);
    assert_eq!(payload["stableSamples"], 2);
    assert_eq!(payload["status"]["screen"]["id"], "combat");
    assert_eq!(payload["status"]["blockingCount"], 0);
    assert!(payload["attempts"].as_u64().expect("attempts") >= 4);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_wait_for_transitions_timeout_reports_last_blockers() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-transitions-timeout.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitionStatusBridgeService::new(usize::MAX);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run_error(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "wait-for-transitions",
            "--timeout-ms",
            "20",
            "--interval-ms",
            "1",
        ])
    })
    .await
    .expect("join wait-for-transitions timeout task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["error"]["code"], "transition_wait_timeout");
    assert_eq!(payload["error"]["lastStatus"]["quiescent"], false);
    assert_eq!(payload["error"]["lastStatus"]["blockingCount"], 1);
    assert_eq!(
        payload["error"]["lastStatus"]["blockers"][0]["reason"],
        "finite-tween-running"
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_wait_for_transitions_timeout_does_not_shrink_rpc_timeout_to_tail_budget() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-transitions-slow-tail.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitionStatusBridgeService::with_response_delay_ms(usize::MAX, 10);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run_error(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "wait-for-transitions",
            "--timeout-ms",
            "20",
            "--interval-ms",
            "1",
            "--rpc-timeout-ms",
            "100",
        ])
    })
    .await
    .expect("join wait-for-transitions timeout task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["error"]["code"], "transition_wait_timeout");
    assert_eq!(payload["error"]["lastStatus"]["quiescent"], false);
    assert_ne!(payload["error"]["code"], "bridge_rpc_timeout");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn state_watch_emits_initial_and_changed_events_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-transition.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitioningBridgeService::default();
    let state_calls = Arc::clone(&service.state_calls);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let output = tokio::task::spawn_blocking(move || {
        let cli = Cli::parse_from([
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
            "--watch",
            "--max-events",
            "2",
            "--poll-interval-ms",
            "1",
        ]);
        let mut output = Vec::new();
        let exit_code = run_cli_streaming(cli, &mut output).expect("watch should stream");
        assert_eq!(exit_code, 0);
        String::from_utf8(output).expect("utf8 output")
    })
    .await
    .expect("join watch task");

    let events = parse_ndjson_events(&output);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0]["type"], "initial");
    assert_eq!(events[0]["sequence"], 1);
    assert_eq!(events[0]["state"]["rootScene"], "screens/main_menu");
    assert_eq!(events[1]["type"], "changed");
    assert_eq!(events[1]["sequence"], 2);
    assert_eq!(events[1]["state"]["rootScene"], "run");
    assert_eq!(
        events[1]["state"]["run"]["currentRoom"]["scene"],
        "rooms/combat_room"
    );
    assert_ne!(events[0]["fingerprint"], events[1]["fingerprint"]);
    assert_eq!(state_calls.load(Ordering::SeqCst), 0);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn state_watch_poll_mode_uses_polling_even_when_reactive_is_available() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-poll-mode.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitioningBridgeService::default();
    let state_calls = Arc::clone(&service.state_calls);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let output = tokio::task::spawn_blocking(move || {
        let cli = Cli::parse_from([
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
            "--watch",
            "--watch-mode",
            "poll",
            "--max-events",
            "2",
            "--poll-interval-ms",
            "1",
        ]);
        let mut output = Vec::new();
        let exit_code = run_cli_streaming(cli, &mut output).expect("watch should stream");
        assert_eq!(exit_code, 0);
        String::from_utf8(output).expect("utf8 output")
    })
    .await
    .expect("join watch task");

    let events = parse_ndjson_events(&output);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0]["type"], "initial");
    assert_eq!(events[1]["type"], "changed");
    assert!(state_calls.load(Ordering::SeqCst) >= 2);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn state_watch_timeout_suppresses_repeated_identical_ipc_state_after_change() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-stable.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = TransitioningBridgeService::without_state_watch();
    let state_calls = Arc::clone(&service.state_calls);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let output = tokio::task::spawn_blocking(move || {
        let cli = Cli::parse_from([
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
            "--watch",
            "--timeout-ms",
            "30",
            "--poll-interval-ms",
            "1",
        ]);
        let mut output = Vec::new();
        let exit_code = run_cli_streaming(cli, &mut output).expect("watch should stream");
        assert_eq!(exit_code, 0);
        String::from_utf8(output).expect("utf8 output")
    })
    .await
    .expect("join watch task");

    let events = parse_ndjson_events(&output);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0]["type"], "initial");
    assert_eq!(events[0]["state"]["rootScene"], "screens/main_menu");
    assert_eq!(events[1]["type"], "changed");
    assert_eq!(events[1]["state"]["rootScene"], "run");
    assert_eq!(
        events[1]["state"]["run"]["currentRoom"]["scene"],
        "rooms/combat_room"
    );
    assert!(state_calls.load(Ordering::SeqCst) >= 2);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn state_watch_preserves_structured_error_events_until_bounded_exit() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-error.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server =
        ipc_bridge_support::spawn_unix_bridge_service(listener, ErroringStateBridgeService);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let output = tokio::task::spawn_blocking(move || {
        let cli = Cli::parse_from([
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
            "--watch",
            "--max-events",
            "2",
            "--poll-interval-ms",
            "1",
        ]);
        let mut output = Vec::new();
        let exit_code = run_cli_streaming(cli, &mut output).expect("watch should stream errors");
        assert_eq!(exit_code, 0);
        String::from_utf8(output).expect("utf8 output")
    })
    .await
    .expect("join watch task");

    let events = parse_ndjson_events(&output);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0]["type"], "error");
    assert_eq!(events[0]["sequence"], 1);
    assert_eq!(events[0]["error"]["code"], "runtime_failure");
    assert_eq!(events[0]["error"]["message"], "runtime-state-failed");
    assert_eq!(events[1]["type"], "error");
    assert_eq!(events[1]["sequence"], 2);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn state_watch_fail_fast_emits_error_then_returns_failure() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-fail-fast.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server =
        ipc_bridge_support::spawn_unix_bridge_service(listener, ErroringStateBridgeService);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let (error, output) = tokio::task::spawn_blocking(move || {
        let cli = Cli::parse_from([
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
            "--watch",
            "--fail-fast",
            "--poll-interval-ms",
            "1",
        ]);
        let mut output = Vec::new();
        let error = run_cli_streaming(cli, &mut output).expect_err("watch should fail fast");
        (
            error.render(true),
            String::from_utf8(output).expect("utf8 output"),
        )
    })
    .await
    .expect("join watch task");

    let events = parse_ndjson_events(&output);
    assert_eq!(events.len(), 1);
    assert_eq!(events[0]["type"], "error");
    assert_ne!(error.exit_code, 0);
    let payload: Value = serde_json::from_str(&error.stdout).expect("json error");
    assert_eq!(payload["error"]["code"], "runtime_failure");
    server.abort();
}
