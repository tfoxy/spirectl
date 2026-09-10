use super::*;

#[test]
fn act_choose_succeeds_for_visible_main_menu_choice() {
    let response = run(&[
        "sts2",
        "--json",
        "act",
        "choose",
        "--choice",
        "menu:start-run",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_choose_succeeds_for_visible_reward_choice() {
    let config = rewards_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "reward:p1:0",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_choose_succeeds_for_visible_reward_flow_choice() {
    let config = rewards_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "reward-flow:skip",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_shop_intents_succeed_for_visible_shop_choices() {
    let config = shop_mock_config();
    for (verb, args, expected_kind) in [
        (
            "buy-card",
            vec!["--shop-item", "shop:p1:card:strike:0"],
            "buy-card",
        ),
        (
            "remove-card",
            vec!["--shop-item", "shop:p1:card-removal:0"],
            "remove-card",
        ),
        ("leave-shop", vec![], "leave-shop"),
    ] {
        let mut command = vec![
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "act",
            verb,
        ];
        command.extend(args);
        let response = run(&command);
        let payload: Value = serde_json::from_str(&response.stdout).expect("json");

        assert_eq!(payload["kind"], expected_kind);
        assert_eq!(payload["accepted"], true);
        assert_eq!(payload["provisional"], false);
    }
}

#[test]
fn act_choose_succeeds_for_custom_event_choices() {
    let fake_config = fake_merchant_pre_open_mock_config();
    let fake_response = run(&[
        "sts2",
        "--json",
        "--config",
        fake_config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "event-room:fake-merchant:open-shop",
    ]);
    let fake_payload: Value = serde_json::from_str(&fake_response.stdout).expect("json");
    assert_eq!(fake_payload["kind"], "choose");
    assert_eq!(fake_payload["accepted"], true);

    let crystal_config = crystal_sphere_mock_config();
    for choice_id in [
        "crystal-sphere:tool:big",
        "crystal-sphere:tool:small",
        "crystal-sphere:cell:0:0",
    ] {
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            crystal_config.path().to_str().expect("utf8 path"),
            "act",
            "choose",
            "--choice",
            choice_id,
        ]);
        let payload: Value = serde_json::from_str(&response.stdout).expect("json");
        assert_eq!(payload["kind"], "choose");
        assert_eq!(payload["accepted"], true);
    }

    let finished_config = crystal_sphere_finished_mock_config();
    let proceed_response = run(&[
        "sts2",
        "--json",
        "--config",
        finished_config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "crystal-sphere:proceed",
    ]);
    let proceed_payload: Value = serde_json::from_str(&proceed_response.stdout).expect("json");
    assert_eq!(proceed_payload["kind"], "choose");
    assert_eq!(proceed_payload["accepted"], true);
}

#[test]
fn act_choose_rejects_non_current_crystal_sphere_choice() {
    let config = crystal_sphere_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "crystal-sphere:proceed",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "invalid_action");
    assert_eq!(payload["error"]["details"][0]["field"], "choice_id");
}

#[test]
fn act_choose_succeeds_for_visible_event_room_choice() {
    let config = event_room_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "event-room:gain-gold:0",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_treasure_room_intents_succeed_for_visible_controls() {
    let config = treasure_room_mock_config();

    for (command, args, expected_kind) in [
        ("open-chest", vec![], "open-chest"),
        ("take-relic", vec!["--relic", "anchor"], "take-relic"),
        ("proceed-treasure-room", vec![], "proceed-treasure-room"),
    ] {
        let mut argv = vec![
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "act",
            command,
        ];
        argv.extend(args);
        let response = run(&argv);
        let payload: Value = serde_json::from_str(&response.stdout).expect("json");

        assert_eq!(payload["kind"], expected_kind);
        assert_eq!(payload["accepted"], true);
        assert_eq!(payload["provisional"], false);
    }
}

#[test]
fn act_choose_succeeds_for_visible_card_selection_choice() {
    let config = card_selection_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "card-selection:card:bash:0",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_confirm_selection_succeeds_for_staged_card_selection() {
    let config = card_selection_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "confirm-selection",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "confirm-selection");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_cancel_selection_succeeds_for_staged_card_selection() {
    let config = card_selection_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "cancel-selection",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "cancel-selection");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_rest_site_option_intent_succeeds_for_visible_rest() {
    let config = write_raw_config("transport:\n  kind: mock\n  mockScenario: rest-site\n");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "rest",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "rest");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_proceed_rest_site_intent_succeeds_for_visible_proceed() {
    let config = write_raw_config("transport:\n  kind: mock\n  mockScenario: rest-site\n");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "proceed-rest-site",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "proceed-rest-site");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_choose_succeeds_for_visible_card_selection_skip_choice() {
    let config = card_selection_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "card-selection:skip",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "choose");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_choose_rejects_target_markers_in_combat() {
    let config = combat_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "choose",
        "--choice",
        "target:e_1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "invalid_action");
    assert_eq!(payload["error"]["details"][0]["field"], "choice_id");
}

#[test]
fn act_mouse_click_requires_dangerous_mode() {
    let response = try_run_error(&[
        "sts2", "--json", "act", "mouse", "click", "--x", "100", "--y", "200",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_mode");
    assert_eq!(payload["error"]["command"], "act mouse click");
    assert_eq!(payload["error"]["requiredMode"], "dangerous");
}

#[test]
fn mod_reload_requires_project_in_normal_mode() {
    let response = try_run_error(&["sts2", "--json", "dev", "mod-reload"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "hot_reload_project_not_found");
}

#[test]
fn mod_reload_status_reaches_runtime_in_normal_mode() {
    let temp = tempfile::tempdir().expect("temp project");
    std::fs::write(
        temp.path().join("sts2.hot-reload.yaml"),
        "schemaVersion: spirectl.hot-reload-project/v0\nprojectId: hot\nshellModId: hot\nshellProject: Hot.Shell\nlogicProject: Hot.Logic\nlogicBuildProfile: hot-logic-build\nlogicArtifactPath: ${projectRoot}/Hot.Logic.dll\nexpectedContractVersion: 0\nprotocol:\n  id: spirectl.m57.hot-reload-shell\n  version: 0\n",
    )
    .expect("write metadata");

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "mod-reload",
        "status",
        "--project",
        temp.path().to_str().expect("utf8"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    // Normal mode no longer gates `dev mod-reload status`; it reaches the runtime
    // and reports the (mock) bridge's shell status as a soft notice, exit 0.
    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["transport"]["kind"], "mock");
    assert_eq!(payload["shell"]["supported"], false);
    assert_eq!(
        payload["notices"][0]["code"],
        "hot-reload-shell-unsupported"
    );
}

#[test]
fn mod_reload_requires_project() {
    let response = try_run_error(&["sts2", "--json", "dev", "mod-reload"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "hot_reload_project_not_found");
    assert!(
        payload["error"]["metadataFile"]
            .as_str()
            .expect("metadata file")
            .ends_with("sts2.hot-reload.yaml")
    );
}

#[test]
fn act_mouse_click_succeeds_in_dangerous_mode() {
    let response = try_run(&[
        "sts2",
        "--json",
        "--mode",
        "dangerous",
        "act",
        "mouse",
        "click",
        "--x",
        "100",
        "--y",
        "200",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "mouse-click");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], true);
}

#[test]
fn act_end_turn_succeeds_in_combat() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "end-turn",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "end-turn");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_ready_succeeds_in_lobby_when_local_player_is_not_ready() {
    let config = lobby_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "ready",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "ready");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_unready_succeeds_in_lobby_when_local_player_is_ready() {
    let config = lobby_ready_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "unready",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "unready");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_select_character_succeeds_for_visible_unlocked_lobby_character() {
    let config = lobby_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "select-character",
        "--character",
        "silent",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "select-character");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_select_map_node_succeeds_for_travelable_map_node() {
    let config = map_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "select-map-node",
        "--node",
        "map-node:3:1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "select-map-node");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_use_potion_succeeds_for_executable_combat_potion() {
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "use-potion",
        "--potion",
        "potion:p1:0:fire-potion",
        "--target",
        "e_1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "use-potion");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
}

#[test]
fn act_unready_rejects_when_local_player_is_not_ready() {
    let config = lobby_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "unready",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "invalid_action");
    assert_eq!(payload["error"]["details"][0]["value"], "unready");
}

#[test]
fn act_select_character_rejects_unknown_lobby_character() {
    let config = lobby_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "act",
        "select-character",
        "--character",
        "defect",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "invalid_action");
    assert_eq!(payload["error"]["details"][0]["field"], "character_id");
    assert_eq!(payload["error"]["details"][0]["value"], "defect");
}

// `dev heal` is a devtool (host-direct CreatureCmd heal), not a legal player action: it is
// validated CLI-side (positive --amount or --full), and rides the
// generic action transport with kind "heal".
#[test]
fn dev_heal_succeeds_in_normal_mode() {
    let response = run(&["sts2", "--json", "dev", "heal", "--amount", "5"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "heal");
}

#[test]
fn dev_heal_requires_amount_or_full() {
    let response = run_error(&["sts2", "--json", "dev", "heal"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_heal_request");
}

#[test]
fn dev_heal_rejects_non_positive_amount() {
    let response = run_error(&["sts2", "--json", "dev", "heal", "--amount", "0"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_heal_request");
}

#[test]
fn dev_heal_amount_succeeds_against_mock_bridge() {
    let response = run(&["sts2", "--json", "dev", "heal", "--amount", "5"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "heal");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["provisional"], false);
    assert!(
        payload["message"]
            .as_str()
            .expect("message")
            .contains("5 HP"),
        "message should echo the amount: {}",
        payload["message"]
    );
}

#[test]
fn dev_heal_full_targets_explicit_creature_against_mock_bridge() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "heal",
        "--target",
        "creature:2",
        "--full",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["kind"], "heal");
    assert_eq!(payload["accepted"], true);
    assert!(
        payload["message"]
            .as_str()
            .expect("message")
            .contains("creature:2"),
        "message should echo the explicit target: {}",
        payload["message"]
    );
}
