use super::*;

#[test]
fn test_run_executes_inline_scenario_successfully() {
    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        "--inline",
        "{\"name\":\"inline-main-menu\",\"steps\":[\"game.info\",{\"dev.assert\":{\"path\":\"rootScene\",\"equals\":\"screens/main_menu\"}}]}",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["root"], "<inline>");
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["path"], "<inline>");
    assert_eq!(payload["scenarios"][0]["name"], "inline-main-menu");
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "dev.assert"
    );
}

#[test]
fn test_run_executes_json_scenario_file_successfully() {
    let scenario = write_json_scenario(
        r#"{"name":"json-main-menu","steps":["game.info",{"dev.assert":{"path":"rootScene","equals":"screens/main_menu"}}]}"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["path"],
        scenario.path().display().to_string()
    );
    assert_eq!(payload["scenarios"][0]["name"], "json-main-menu");
}

#[test]
fn test_run_executes_http_wait_and_fetch_steps_and_persists_probe_artifacts() {
    let dir = tempfile::tempdir().expect("temp dir");
    let artifacts_dir = dir.path().join("artifacts");
    let fetched = dir.path().join("captured.json");
    let source = dir.path().join("source.json");
    let server_script = dir.path().join("probe_server.py");
    fs::write(&source, r#"{"status":"ok","kind":"fixture"}"#).expect("write source");
    write_probe_server_script(&server_script);

    let mut child = std::process::Command::new("python3")
        .arg(&server_script)
        .current_dir(dir.path())
        .stdout(std::process::Stdio::piped())
        .spawn()
        .expect("spawn probe server");
    let mut port_line = String::new();
    std::io::BufRead::read_line(
        &mut std::io::BufReader::new(child.stdout.take().expect("stdout")),
        &mut port_line,
    )
    .expect("read port");
    let port = port_line.trim();

    let scenario = write_scenario(&format!(
        r#"
name: external-probes
steps:
  - dev.http-wait:
      url: http://127.0.0.1:{port}/health
      expectStatus: 200
      query: json.status
      equals: ok
      timeoutMs: 1000
      intervalMs: 10
  - dev.fetch:
      source: {source}
      output: {output}
      query: json.kind
      equals: fixture
"#,
        source = source.display(),
        output = fetched.display(),
    ));

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        artifact_config(&artifacts_dir, MockScenario::MainMenu)
            .path()
            .to_str()
            .expect("utf8 config"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let _ = child.kill();
    let _ = child.wait();

    assert_eq!(payload["status"], "passed");
    let step_artifacts = payload["scenarios"][0]["artifacts"]["stepArtifacts"]
        .as_array()
        .expect("step artifacts");
    assert!(
        step_artifacts
            .iter()
            .any(|artifact| artifact["stepCanonicalId"] == "dev.http-wait"
                && artifact["kind"] == "json"),
        "expected persisted http-wait json artifact"
    );
    assert!(
        step_artifacts.iter().any(
            |artifact| artifact["stepCanonicalId"] == "dev.fetch" && artifact["kind"] == "json"
        ),
        "expected persisted fetch json artifact"
    );
    assert!(
        step_artifacts
            .iter()
            .any(|artifact| artifact["stepCanonicalId"] == "dev.fetch"
                && artifact["kind"] == "file"
                && artifact["path"] == fetched.display().to_string()),
        "expected persisted fetched file artifact entry"
    );
    assert!(fetched.is_file(), "expected fetched output to exist");
}

#[cfg(unix)]
#[test]
fn test_run_executes_project_hook_and_persists_declared_artifacts() {
    let dir = tempfile::tempdir().expect("temp dir");
    let artifacts_dir = dir.path().join("artifacts");
    let hooks_path = dir.path().join("sts2.hooks.yaml");
    let input_path = dir.path().join("hook-input.json");
    let artifact_path = dir.path().join("hook-artifact.txt");
    let hook_script = write_hook_script(dir.path(), &input_path, &artifact_path);
    fs::write(
        &hooks_path,
        format!(
            "hooks:\n  repo-check:\n    command: {}\n",
            hook_script.display()
        ),
    )
    .expect("write hooks");
    let config = write_raw_config(&format!(
        "transport:\n  kind: mock\nproject:\n  hooksFile: {}\nartifacts:\n  dir: {}\n",
        hooks_path.display(),
        artifacts_dir.display()
    ));
    let scenario = write_scenario(
        r#"
name: project-hook
steps:
  - project.hook:
      name: repo-check
      input:
        kind: smoke
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 config"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let stdin_payload: Value =
        serde_json::from_slice(&fs::read(&input_path).expect("read hook stdin")).expect("stdin");
    assert_eq!(payload["status"], "passed");
    assert_eq!(stdin_payload["step"]["canonicalId"], "project.hook");
    assert_eq!(stdin_payload["input"]["kind"], "smoke");
    let step_artifacts = payload["scenarios"][0]["artifacts"]["stepArtifacts"]
        .as_array()
        .expect("step artifacts");
    assert!(
        step_artifacts
            .iter()
            .any(|artifact| artifact["stepCanonicalId"] == "project.hook"
                && artifact["kind"] == "json"),
        "expected persisted hook json artifact"
    );
    assert!(
        step_artifacts
            .iter()
            .any(|artifact| artifact["stepCanonicalId"] == "project.hook"
                && artifact["kind"] == "file"
                && artifact["path"] == artifact_path.display().to_string()),
        "expected declared hook artifact entry"
    );
}

#[test]
fn test_run_executes_regex_assertion_steps() {
    let scenario = write_scenario(
        r#"
name: regex-assert
steps:
  - dev.assert:
      path: rootScene
      regex: "^screens/main.*$"
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["operator"],
        "regex"
    );
}

#[test]
fn test_run_executes_wildcard_assertion_steps() {
    // state has no top-level `choices` array; the combat mock's single enemy
    // gives an equally simple single-element wildcard target.
    let config = combat_mock_config();
    let scenario = write_scenario(
        r#"
name: wildcard-assert
steps:
  - dev.assert:
      path: run.currentRoom.combat.combatState.enemies[*].id
      equals: e_1
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["e_1"])
    );
}

#[test]
fn test_run_executes_filter_assertion_steps() {
    // state has no top-level `choices` array; the combat mock's hand cards give
    // a real two-element array to filter by id.
    let config = combat_mock_config();
    let scenario = write_scenario(
        r#"
name: filter-assert
steps:
  - dev.assert:
      path: run.players[id=p1].combat.hand.cards[id=c_1].id
      equals: c_1
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["c_1"])
    );
}

#[test]
fn test_run_executes_comparison_filter_assertion_steps() {
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let scenario = write_scenario(
        r#"
name: comparison-filter-assert
steps:
  - dev.assert:
      path: run.players[combat.energy>=3].id
      equals: p1
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    // Both mock players sit in combat with energy 3 in this scenario, so the
    // comparison filter legitimately matches both ids; "equals p1" only needs one.
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["p1", "p2"])
    );
}

#[test]
fn test_run_executes_regex_filter_assertion_steps() {
    // The mock `rewards` scenario only advertises the legacy rewards screen id, not
    // populated `run.players[].overlays[].rewards` data (see
    // cli/src/bridge/mock_data/state_run.rs `mock_overlays`), so a real regex
    // filter is exercised against the combat mock's hand cards instead.
    let config = combat_mock_config();
    let scenario = write_scenario(
        r#"
name: regex-filter-assert
steps:
  - dev.assert:
      path: run.players[id=p1].combat.hand.cards[id~=^c_].modelId
      equals: JAB
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["JAB", "GUARD"])
    );
}

#[test]
fn test_run_executes_nested_filter_assertion_steps() {
    // current's action items flatten `preferredAction` away and nest structured
    // arguments under `args` instead; this exercises the same "filter by id, then
    // read a nested leaf" mechanic against a real nested `args.cardId` argument via
    // the resolved `state actions` document (source: actions).
    let config = card_overlay_mock_config();
    let scenario = write_scenario(
        r#"
name: nested-filter-assert
steps:
  - dev.assert:
      path: actions[id=action:choose-a-card:select-card:card:p1:choose-a-card:0].args.cardId
      source: actions
      equals: card-selection:card:DEMONIC_SHIELD:0
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["card-selection:card:DEMONIC_SHIELD:0"])
    );
}

#[test]
fn test_run_executes_object_wide_filter_assertion_steps() {
    let config = combat_mock_config();
    let scenario = write_scenario(
        r#"
name: object-wide-filter-assert
steps:
  - dev.assert:
      path: actions[=action:combat-room:end-turn].kind
      source: actions
      equals: end-turn
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"],
        serde_json::json!(["end-turn"])
    );
}

#[test]
fn checked_in_m36_fixture_scenarios_prove_usable_choice_or_notice_contracts() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    // current flattens `preferredAction`/generic `choose` fallback away: real action
    // kinds and structured `args` come straight from the native `state actions`
    // resolver (cli/src/state_actions.rs). Shop's items are runtime-generated (no
    // stable id to hardcode), so it proves its contract on room-scene identity
    // instead of an action kind; deck-card-selection has no native surfacer yet
    // (out of scope for this migration), so it proves its contract on the raw
    // state doc's overlay discriminant instead of a resolved action.
    let scenario_expectations = [
        (
            "tests/scenarios/fixture-basic-event-room.sts2.yaml",
            "actions[args.eventOptionId=event-room:gain-gold:0].kind",
            "select-event-option",
        ),
        (
            "tests/scenarios/fixture-treasure.sts2.yaml",
            "actions[id=action:treasure-room:open-chest].kind",
            "open-chest",
        ),
        (
            "tests/scenarios/fixture-basic-relic-selection.sts2.yaml",
            "actions[args.relicId=treasure-room:relic:anchor:0].kind",
            "take-relic",
        ),
        (
            "tests/scenarios/fixture-basic-shop.sts2.yaml",
            "run.currentRoom.scene",
            "rooms/shop_room",
        ),
        (
            "tests/scenarios/fixture-basic-card-selection.sts2.yaml",
            "actions[args.cardId=card-selection:card:bash:0].kind",
            "select-card",
        ),
        (
            "tests/scenarios/fixture-basic-bundle-selection.sts2.yaml",
            "actions[args.bundleId=card-selection:bundle:offensive-pack:0].kind",
            "select-bundle",
        ),
        (
            "tests/scenarios/fixture-basic-simple-card-selection.sts2.yaml",
            "actions[args.cardId=card-selection:card:bash:0].kind",
            "select-card",
        ),
        (
            "tests/scenarios/fixture-basic-deck-card-selection.sts2.yaml",
            "run.players[id=p1].overlays[0].deckCardSelection.kind",
            "select",
        ),
    ];

    for (path, expected_path, expected_equals) in scenario_expectations {
        let scenario_path = repo_root.join(path);
        let contents = fs::read_to_string(&scenario_path)
            .unwrap_or_else(|error| panic!("read {}: {error}", scenario_path.display()));
        let document: YamlValue = serde_yaml::from_str(&contents)
            .unwrap_or_else(|error| panic!("parse {}: {error}", scenario_path.display()));
        let root = yaml_mapping(&document, path);
        let steps = yaml_sequence(
            root.get("steps")
                .unwrap_or_else(|| panic!("{path} should define steps")),
            &format!("{path} steps"),
        );

        let matched = steps.iter().any(|step| {
            let step_mapping = yaml_mapping(step, path);
            let assert_body = step_mapping
                .get("dev.assert")
                .and_then(YamlValue::as_mapping);
            let Some(assert_body) = assert_body else {
                return false;
            };

            let path_matches =
                assert_body.get("path").map(|value| yaml_str(value, path)) == Some(expected_path);
            let equals_matches = assert_body.get("equals").map(|value| yaml_str(value, path))
                == Some(expected_equals);

            path_matches && equals_matches
        });

        assert!(
            matched,
            "{path} should assert {expected_path} == {expected_equals} so the checked-in fixture proves its usable post-load contract"
        );
    }

    for path in [
        "tests/scenarios/fixture-basic-simple-card-selection.sts2.yaml",
        "tests/scenarios/fixture-basic-deck-card-selection.sts2.yaml",
    ] {
        let scenario_path = repo_root.join(path);
        let contents = fs::read_to_string(&scenario_path)
            .unwrap_or_else(|error| panic!("read {}: {error}", scenario_path.display()));
        let document: YamlValue = serde_yaml::from_str(&contents)
            .unwrap_or_else(|error| panic!("parse {}: {error}", scenario_path.display()));
        let root = yaml_mapping(&document, path);
        let steps = yaml_sequence(
            root.get("steps")
                .unwrap_or_else(|| panic!("{path} should define steps")),
            &format!("{path} steps"),
        );

        let has_confirm_step = steps.iter().any(|step| {
            yaml_mapping(step, path)
                .contains_key(YamlValue::String("act.confirm-selection".to_string()))
        });
        let has_cancel_step = steps.iter().any(|step| {
            yaml_mapping(step, path)
                .contains_key(YamlValue::String("act.cancel-selection".to_string()))
        });

        assert!(
            has_confirm_step,
            "{path} should exercise act.confirm-selection so the checked-in card-grid fixture proves confirm follow-through"
        );
        assert!(
            has_cancel_step,
            "{path} should exercise act.cancel-selection so the checked-in card-grid fixture proves cancel follow-through"
        );
    }
}

#[test]
fn non_combat_intent_actions() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let scenario_path = repo_root.join("tests/scenarios/non-combat-intent-actions.sts2.yaml");

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        scenario_path.to_str().expect("utf8 scenario path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["name"], "non-combat-intent-actions");

    let steps = payload["scenarios"][0]["steps"]
        .as_array()
        .expect("steps array");
    for expected in [
        "act.claim-reward",
        "act.select-card",
        "act.buy-card",
        "act.rest",
        "act.open-chest",
        "act.select-map-node",
        "act.select-event-option",
    ] {
        assert!(
            steps.iter().any(|step| step["canonicalId"] == expected),
            "scenario should exercise {expected}"
        );
    }
}

#[test]
fn checked_in_smoke_scenarios_keep_mock_vs_live_split_explicit() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let mock_path = repo_root.join("tests/scenarios/smoke-main-menu.sts2.yaml");
    let live_path = repo_root.join("tests/scenarios/smoke-main-menu-live.sts2.yaml");

    let mock_document: YamlValue = serde_yaml::from_str(
        &fs::read_to_string(&mock_path)
            .unwrap_or_else(|error| panic!("read {}: {error}", mock_path.display())),
    )
    .unwrap_or_else(|error| panic!("parse {}: {error}", mock_path.display()));
    let live_document: YamlValue = serde_yaml::from_str(
        &fs::read_to_string(&live_path)
            .unwrap_or_else(|error| panic!("read {}: {error}", live_path.display())),
    )
    .unwrap_or_else(|error| panic!("parse {}: {error}", live_path.display()));

    let mock_root = yaml_mapping(&mock_document, "smoke-main-menu");
    let live_root = yaml_mapping(&live_document, "smoke-main-menu-live");

    let mock_description = yaml_str(
        mock_root
            .get("description")
            .expect("mock smoke description should exist"),
        "mock smoke description",
    );
    assert!(
        mock_description.contains("explicit mock main-menu transport"),
        "mock smoke description should keep its mock-only baseline intent explicit"
    );

    let live_description = yaml_str(
        live_root
            .get("description")
            .expect("live smoke description should exist"),
        "live smoke description",
    );
    assert!(
        live_description.contains("attached real main-menu session"),
        "live smoke description should keep the real-session prerequisite explicit"
    );
    assert!(
        live_description.contains("not part of the lightweight default baseline"),
        "live smoke description should keep the opt-in baseline split explicit"
    );

    let live_steps = yaml_sequence(
        live_root
            .get("steps")
            .expect("live smoke steps should exist"),
        "live smoke steps",
    );
    // Main-menu's "start run" has no native `state actions` surfacer (state
    // dropped the generic legacy choice surface and nothing re-models the main
    // menu — see the scenario file's own comment), so this now proves arrival at
    // the main menu before acting instead of proving the choice is executable.
    let has_wait_for_main_menu_before_acting = live_steps.iter().any(|step| {
        let step_mapping = yaml_mapping(step, "smoke-main-menu-live");
        let Some(wait_for) = step_mapping
            .get("dev.wait-for")
            .and_then(YamlValue::as_mapping)
        else {
            return false;
        };

        wait_for
            .get("path")
            .map(|value| yaml_str(value, "wait path"))
            == Some("rootScene")
            && wait_for
                .get("equals")
                .map(|value| yaml_str(value, "wait equals"))
                == Some("screens/main_menu")
    });
    assert!(
        has_wait_for_main_menu_before_acting,
        "live smoke should wait for the main menu before acting"
    );

    let has_multiplayer_lobby_wait = live_steps.iter().any(|step| {
        let step_mapping = yaml_mapping(step, "smoke-main-menu-live");
        let Some(wait_for) = step_mapping
            .get("dev.wait-for")
            .and_then(YamlValue::as_mapping)
        else {
            return false;
        };

        wait_for
            .get("path")
            .map(|value| yaml_str(value, "wait path"))
            == Some("rootScene")
            && wait_for
                .get("equals")
                .map(|value| yaml_str(value, "wait equals"))
                == Some("screens/character_select_screen")
    });
    assert!(
        has_multiplayer_lobby_wait,
        "live smoke should validate the first stable downstream screen"
    );
}
