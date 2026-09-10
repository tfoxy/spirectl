use super::*;

#[test]
fn state_lobby_returns_runtime_only_character_select() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Lobby,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "state",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["schemaVersion"], "spirectl.state/v0");
    assert_eq!(payload["rootScene"], "screens/character_select_screen");
    assert_eq!(payload["language"], "mock");
    assert_eq!(payload["characterSelect"]["view"]["playerId"], "p:100");
    assert_eq!(
        payload["characterSelect"]["view"]["selectedCharacterButtonId"],
        "ironclad"
    );
    assert_eq!(
        payload["characterSelect"]["lobby"]["localPlayerId"],
        "p:100"
    );
    assert_eq!(
        payload["characterSelect"]["characterButtons"][0]["id"],
        payload["characterSelect"]["characterButtons"][0]["characterId"]
    );
    assert!(
        payload["characterSelect"]["characterButtons"][0]
            .get("visible")
            .is_none()
    );
    assert!(payload["characterSelect"]["lobby"]["players"][0]["slotId"].is_number());
    assert!(
        payload["characterSelect"]["characterButtons"][0]
            .get("description")
            .is_none()
    );
    assert!(
        payload["characterSelect"]["characterButtons"][0]
            .get("portraitAssetKey")
            .is_none()
    );
    assert!(payload["run"].is_null());
    assert!(payload.get("actions").is_none());
    assert!(payload.get("choices").is_none());
    assert!(payload.get("scene").is_none());
}

#[test]
fn state_lobby_player_id_selects_rendered_character_select_view() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Lobby,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(temp.path(), serde_yaml::to_string(&config).expect("yaml")).expect("write config");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "state",
        "--player-id",
        "p:300",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(
        payload["characterSelect"]["lobby"]["localPlayerId"],
        "p:100"
    );
    assert_eq!(payload["characterSelect"]["view"]["playerId"], "p:300");
    assert_eq!(
        payload["characterSelect"]["view"]["selectedCharacterButtonId"],
        "defect"
    );
}

#[test]
fn state_non_character_select_returns_null_character_select() {
    let response = run(&["sts2", "--json", "state"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["schemaVersion"], "spirectl.state/v0");
    assert_eq!(payload["rootScene"], "screens/main_menu");
    assert!(payload["characterSelect"].is_null());
    assert!(payload["run"].is_null());
}

#[test]
fn state_actions_reports_the_resolved_perspective() {
    let response = run(&["sts2", "--json", "state", "actions"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    // Main menu: no run and no lobby, so there is no seat to default between.
    assert_eq!(payload["perspective"]["requestedPlayerId"], Value::Null);
    assert_eq!(payload["perspective"]["scope"], "local");
    assert_eq!(payload["perspective"]["usesDefault"], true);
    assert_eq!(payload["perspective"]["seatCount"], 0);
    assert_eq!(payload["warnings"], serde_json::json!([]));
}

#[test]
fn state_actions_warns_when_a_multi_seat_perspective_is_defaulted() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(temp.path(), serde_yaml::to_string(&config).expect("yaml")).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "state",
        "actions",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(
        response.exit_code, 0,
        "a warning must not change the exit code"
    );
    assert_eq!(payload["perspective"]["usesDefault"], true);
    assert_eq!(payload["perspective"]["seatCount"], 2);
    assert_eq!(payload["perspective"]["playerId"], "p1");
    let warnings = payload["warnings"].as_array().expect("warnings");
    assert_eq!(warnings.len(), 1);
    assert_eq!(warnings[0]["code"], "perspective_defaulted_multi_seat");
    assert_eq!(warnings[0]["severity"], "warning");
    assert_eq!(warnings[0]["seatCount"], 2);
    assert_eq!(warnings[0]["playerId"], "p1");
}

#[test]
fn state_actions_drops_the_warning_when_a_seat_is_named() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(temp.path(), serde_yaml::to_string(&config).expect("yaml")).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "state",
        "actions",
        "--perspective",
        "omniscient",
        "--player-id",
        "p2",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["perspective"]["requestedPlayerId"], "p2");
    assert_eq!(payload["perspective"]["scope"], "omniscient");
    assert_eq!(payload["perspective"]["usesDefault"], false);
    assert_eq!(payload["warnings"], serde_json::json!([]));
}
