use super::*;

#[test]
fn test_run_reports_inline_parse_failures() {
    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        "--inline",
        "name: bad\nsteps:\n  - dev.assert: [\n",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["root"], "<inline>");
    assert_eq!(payload["status"], "invalid");
    assert_eq!(payload["scenarios"][0]["path"], "<inline>");
    assert_eq!(payload["scenarios"][0]["name"], "<inline>");
    assert_eq!(
        payload["scenarios"][0]["error"]["code"],
        "scenario_parse_failed"
    );
}

#[test]
fn test_run_reports_step_failures_with_exit_code() {
    let scenario = write_scenario(
        r#"
name: failing-assert
steps:
  - dev.assert:
      path: screen.id
      equals: combat
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

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["status"], "failed");
    assert_eq!(payload["failedScenarioCount"], 1);
    assert_eq!(payload["scenarios"][0]["steps"][0]["status"], "failed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "assertion_failed"
    );
}

#[test]
fn test_run_executes_dev_console_step_and_persists_json_artifact() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: console-step
steps:
  - dev.console:
      command: help
      args: [draw]
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
    assert_eq!(payload["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.console"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["line"],
        "help draw"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("console artifact path"),
    );
    assert!(step_artifact_path.ends_with("steps/001-dev-console.json"));
    assert_eq!(read_json_file(&step_artifact_path)["line"], "help draw");
}

#[test]
fn debugger_event_streams() {
    let artifacts_dir = tempfile::tempdir().expect("artifact dir");
    let scenario = write_scenario(
        r#"
name: debugger-event-streams
steps:
  - game.info
  - dev.debug-events:
      session: dbg:observer
      fromSequence: 1
      limit: 4
      follow: true
      timeoutMs: 25
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        artifact_config(artifacts_dir.path(), MockScenario::MainMenu)
            .path()
            .to_str()
            .expect("utf8 config"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["steps"][0]["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "game.info"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "dev.debug-events"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["output"]["notices"][0]["code"],
        "debug_event_stream_unavailable"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("debug events artifact path"),
    );
    assert!(step_artifact_path.ends_with("steps/002-dev-debug-events.json"));
    assert_eq!(
        read_json_file(&step_artifact_path)["notices"][0]["code"],
        "debug_event_stream_unavailable"
    );
    assert!(
        payload["scenarios"][0]["steps"][0]["output"]["bridge"]["capabilities"]
            .as_array()
            .expect("capabilities")
            .iter()
            .any(|capability| capability["id"] == "state"),
        "normal game.info path should remain independent of debugger stream support: {payload}"
    );
}

#[test]
fn test_run_dev_console_dangerous_command_requires_dangerous_mode() {
    let config = artifact_config(
        tempfile::tempdir().expect("artifacts dir").path(),
        MockScenario::MainMenu,
    );
    let scenario = write_scenario(
        r#"
name: console-dangerous-gate
steps:
  - dev.console:
      command: achievement
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

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "failed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "invalid_mode"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["requiredMode"],
        "dangerous"
    );
}

#[test]
fn test_run_persists_summary_and_scenario_results_by_default() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: persisted-pass
steps:
  - game.info
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

    let run_dir = only_run_dir(artifacts_dir.path());
    let summary_path = run_dir.join("summary.json");
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let result_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["resultPath"]
            .as_str()
            .expect("result path"),
    );

    assert_eq!(
        payload["artifacts"]["rootDir"],
        run_dir.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["artifacts"]["summaryPath"],
        summary_path.to_string_lossy().as_ref()
    );
    assert_eq!(payload["artifacts"]["failureArtifacts"], "on-failure");
    assert_eq!(read_json_file(&summary_path), payload);
    assert_eq!(
        scenario_dir,
        run_dir.join("scenarios").join("001-persisted-pass")
    );
    assert_eq!(result_path, scenario_dir.join("result.json"));
    assert_eq!(read_json_file(&result_path), payload["scenarios"][0]);
}

#[test]
fn test_run_collects_failure_artifacts_on_failed_scenarios() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: persisted-failure
steps:
  - dev.assert:
      path: rootScene
      equals: run
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
    let scenario_payload = &payload["scenarios"][0];
    let failure = &scenario_payload["failure"];
    let failure_artifacts = &scenario_payload["artifacts"]["failure"];
    let failure_summary_path = PathBuf::from(
        failure_artifacts["summaryPath"]
            .as_str()
            .expect("failure summary path"),
    );
    let failure_evidence_dir = PathBuf::from(
        failure_artifacts["evidenceDir"]
            .as_str()
            .expect("evidence dir path"),
    );
    let failure_diagnostics_path = PathBuf::from(
        failure_artifacts["diagnosticsPath"]
            .as_str()
            .expect("diagnostics path"),
    );
    let diagnostics = read_json_file(&failure_diagnostics_path);

    assert_eq!(response.exit_code, 3);
    assert_eq!(failure["stepIndex"], 1);
    assert_eq!(failure["stepCanonicalId"], "dev.assert");
    assert_eq!(failure["exitCode"], 3);
    assert_eq!(failure["error"]["code"], "assertion_failed");
    assert!(failure_summary_path.exists());
    assert!(failure_evidence_dir.exists());
    assert!(failure_diagnostics_path.exists());
    assert_eq!(read_json_file(&failure_summary_path), failure.clone());
    assert_eq!(
        diagnostics["captures"]["state"]["data"]["rootScene"],
        "screens/main_menu"
    );
    assert!(
        !diagnostics["captures"]["logs"]["data"]["entries"]
            .as_array()
            .expect("log entries")
            .is_empty()
    );
    // `inspectActions.data.availableActions` is always `[]` by design now (see
    // cli/src/bridge/output_json.rs `inspect_actions_json` — resolved actions come
    // from `state actions` instead); `supportedActions` is the real, always-
    // populated static capability catalog this diagnostics capture actually proves.
    assert_eq!(
        diagnostics["captures"]["inspectActions"]["data"]["supportedActions"][0]["kind"],
        "play-card"
    );
    assert_eq!(diagnostics["captures"]["sceneTree"]["status"], "error");
    assert!(failure_evidence_dir.join("state.json").exists());
    assert!(failure_evidence_dir.join("logs.json").exists());
    assert!(failure_evidence_dir.join("inspect-actions.json").exists());
    assert!(failure_evidence_dir.join("runtime.png").exists());
    assert!(!failure_evidence_dir.join("scene-tree.json").exists());
}

#[test]
fn test_run_normalizes_aliases_and_bare_no_arg_steps() {
    let scenario = write_scenario(
        r#"
name: alias-smoke
steps:
  - game.info: {}
  - assert.query:
      path: run.currentRoom.scene
      equals: rooms/combat_room
  - act.end_turn
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        write_config(&AppConfig {
            transport: sts2::TransportConfig {
                kind: TransportKind::Mock,
                mock_scenario: MockScenario::Combat,
                ..sts2::TransportConfig::default()
            },
            ..AppConfig::default()
        })
        .path()
        .to_str()
        .expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["requestedId"],
        "assert.query"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][2]["canonicalId"],
        "act.end-turn"
    );
}

#[test]
fn test_run_executes_lobby_action_steps() {
    let scenario = write_scenario(
        r#"
name: lobby-actions
steps:
  - dev.assert:
      path: rootScene
      equals: screens/character_select_screen
  - act.ready
  - act.select-character:
      character: silent
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        write_config(&AppConfig {
            transport: sts2::TransportConfig {
                kind: TransportKind::Mock,
                mock_scenario: MockScenario::Lobby,
                ..sts2::TransportConfig::default()
            },
            ..AppConfig::default()
        })
        .path()
        .to_str()
        .expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "act.ready"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][2]["canonicalId"],
        "act.select-character"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][2]["output"]["kind"],
        "select-character"
    );
}

#[test]
fn multiplayer_ownership() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let scenario_path = repo_root.join("tests/scenarios/multiplayer-ownership.sts2.yaml");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::MainMenu,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario_path.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["scenarios"][0]["status"], "passed");
    assert_eq!(payload["scenarios"][0]["name"], "multiplayer-ownership");

    let steps = payload["scenarios"][0]["steps"]
        .as_array()
        .expect("scenario steps");
    // ownerRole/remoteOrchestration on action items and host-local-seat control of
    // a second player have no state native representation (see the scenario
    // file's header comment) — assert the real lobby-membership facts this
    // scenario proves instead.
    assert!(steps.iter().any(|step| {
        step["canonicalId"] == "dev.assert"
            && step["input"]["path"] == "characterSelect.lobby.players[id=p:200].id"
            && step["output"]["matched"] == true
    }));
    assert!(steps.iter().any(|step| {
        step["canonicalId"] == "dev.assert"
            && step["input"]["path"] == "characterSelect.lobby.players[id=p:300].id"
            && step["output"]["matched"] == true
    }));
}

#[test]
fn multiplayer_ownership_wrong_player_runner_artifact() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Lobby);
    let scenario = write_scenario(
        r#"
name: multiplayer-ownership-wrong-player
steps:
  - act.ready:
      playerId: p:200
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

    assert_eq!(response.exit_code, 3);
    let step_error = &payload["scenarios"][0]["steps"][0]["error"];
    let failure = &step_error["actionFailure"];
    assert_eq!(failure["reasonCode"], "wrong_player");
    assert_eq!(failure["requestedPlayerId"], "p:200");
    assert_eq!(failure["resolvedOwnerPlayerId"], "p:100");
    assert_eq!(failure["localPlayerId"], "p:100");
    assert_eq!(failure["hostPlayerId"], "p:100");
    assert_eq!(failure["localRole"], "host");
    assert_eq!(
        failure["remoteOrchestration"]["state"],
        "local-only-degraded"
    );
    assert_eq!(
        failure["fieldDiagnostics"][0]["note"],
        "Retry with the advertised ownerPlayerId, or wait for a configured remote client capability before targeting a remote player."
    );

    let result_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["resultPath"]
            .as_str()
            .expect("result path"),
    );
    let result = read_json_file(&result_path);
    assert_eq!(
        result["steps"][0]["error"]["actionFailure"]["reasonCode"],
        "wrong_player"
    );
    assert_eq!(
        result["steps"][0]["error"]["actionFailure"]["requestedPlayerId"],
        "p:200"
    );

    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let host_local_fixture =
        repo_root.join("fixtures/multiplayer-ownership-host-local-seat.sts2.fixture.yaml");
    let host_local_scenario = write_scenario(&format!(
        r#"
name: multiplayer-ownership-host-local-wrong-player
steps:
  - dev.load-fixture:
      path: {}
  - act.unready:
      playerId: p:100
"#,
        host_local_fixture.display()
    ));

    let host_local_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        host_local_scenario.path().to_str().expect("utf8 path"),
    ]);
    let host_local_payload: Value =
        serde_json::from_str(&host_local_response.stdout).expect("json");
    assert_eq!(host_local_response.exit_code, 3);
    let host_local_failure =
        &host_local_payload["scenarios"][0]["steps"][1]["error"]["actionFailure"];
    assert_eq!(host_local_failure["reasonCode"], "wrong_player");
    assert_eq!(host_local_failure["requestedPlayerId"], "p:100");
    assert_eq!(host_local_failure["resolvedOwnerPlayerId"], "p:200");
    assert_eq!(host_local_failure["localPlayerId"], "p:100");
    assert_eq!(host_local_failure["hostPlayerId"], "p:100");
    assert_eq!(host_local_failure["localRole"], "host");
    assert_eq!(
        host_local_failure["remoteOrchestration"]["state"],
        "host-local-seat"
    );
}

#[test]
fn multiplayer_ownership_unsupported_perspective_is_rejected() {
    let scenario = write_scenario(
        r#"
name: multiplayer-ownership-unsupported-perspective
steps:
  - dev.assert:
      perspective: remote
      playerId: p:200
      path: screen.id
      equals: Screens.CharacterSelect.NCharacterSelectScreen
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

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["invalidScenarioCount"], 1);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "invalid_step_args"
    );
    assert!(
        payload["scenarios"][0]["steps"][0]["error"]["message"]
            .as_str()
            .expect("message")
            .contains("Unsupported perspective 'remote'")
    );

    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Lobby);
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let host_local_fixture =
        repo_root.join("fixtures/multiplayer-ownership-host-local-seat.sts2.fixture.yaml");
    let scenario = write_scenario(&format!(
        r#"
name: multiplayer-ownership-unsupported-remote-action
steps:
  - dev.load-fixture:
      path: {}
  - act.ready:
      playerId: p:300
"#,
        host_local_fixture.display()
    ));

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
    assert_eq!(response.exit_code, 3);
    let failure = &payload["scenarios"][0]["steps"][1]["error"]["actionFailure"];
    assert_eq!(failure["reasonCode"], "unsupported_perspective");
    assert_eq!(failure["requestedPlayerId"], "p:300");
    assert_eq!(failure["resolvedOwnerPlayerId"], "p:300");
    assert_eq!(failure["localPlayerId"], "p:100");
    assert_eq!(failure["hostPlayerId"], "p:100");
    assert_eq!(failure["localRole"], "host");
    assert_eq!(failure["action"], "ready");
    assert_eq!(
        failure["remoteOrchestration"]["state"],
        "local-only-degraded"
    );
}

#[test]
fn test_run_artifacts_dir_flag_overrides_config() {
    let configured_dir = tempfile::tempdir().expect("configured artifacts dir");
    let override_dir = tempfile::tempdir().expect("override artifacts dir");
    let config = artifact_config(configured_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: override-root
steps:
  - game.info
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--artifacts-dir",
        override_dir.path().to_str().expect("utf8 path"),
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let run_dir = only_run_dir(override_dir.path());

    assert_eq!(
        payload["artifacts"]["rootDir"],
        run_dir.to_string_lossy().as_ref()
    );
    assert!(!configured_dir.path().join("test-runs").exists());
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_executes_dev_load_fixture_relative_to_scenario_file() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("spirectl-runner-fixture.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let scenario_dir = tempfile::tempdir().expect("scenario dir");
    let fixture_path = scenario_dir.path().join("basic-combat.sts2.fixture.yaml");
    write_fixture(&fixture_path);
    let scenario_path = scenario_dir.path().join("runner-fixture.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: fixture-relative
steps:
  - dev.load_fixture:
      path: ./basic-combat.sts2.fixture.yaml
  - dev.assert:
      path: run.currentRoom.scene
      equals: rooms/combat_room
"#,
    )
    .expect("write scenario");

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
            "test",
            "run",
            scenario_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join runner task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.load-fixture"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["requestedPath"],
        "./basic-combat.sts2.fixture.yaml"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["resolvedPath"],
        fixture_path.display().to_string()
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["loaded"]["screen"]["id"],
        "combat"
    );
    let fixture_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("fixture artifact path"),
    );
    assert!(fixture_artifact_path.exists());
    assert_eq!(
        read_json_file(&fixture_artifact_path)["loaded"]["screen"]["id"],
        "combat"
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_accepts_dev_fixture_load_alias() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("spirectl-runner-fixture-alias.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let scenario_dir = tempfile::tempdir().expect("scenario dir");
    let fixture_path = scenario_dir.path().join("basic-combat.sts2.fixture.yaml");
    write_fixture(&fixture_path);
    let scenario_path = scenario_dir.path().join("runner-fixture-alias.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: fixture-load-alias
steps:
  - dev.fixture.load:
      path: ./basic-combat.sts2.fixture.yaml
"#,
    )
    .expect("write scenario");

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
            "test",
            "run",
            scenario_path.to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join fixture alias task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["requestedId"],
        "dev.fixture.load"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.load-fixture"
    );

    server.abort();
}

#[test]
fn test_run_executes_dev_load_scenario_from_relative_workflow_path_and_persists_artifact() {
    let dir = tempfile::tempdir().expect("scenario dir");
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let repro_dir = dir.path().join("repros");
    fs::create_dir_all(&repro_dir).expect("create repro dir");
    let scenario_artifact = repro_dir.join("mock-shop.sts2.scenario.yaml");
    write_sparse_shop_scenario(&scenario_artifact);
    let workflow_path = dir.path().join("load-scenario.sts2.yaml");
    fs::write(
        &workflow_path,
        r#"
name: load-scenario-parser
steps:
  - dev.load-scenario:
      path: ./repros/mock-shop.sts2.scenario.yaml
      restart: true
      timeoutMs: 500
      intervalMs: 5
"#,
    )
    .expect("write workflow");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Shop);

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        workflow_path.to_str().expect("workflow path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.load-scenario"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["input"]["timeoutMs"],
        500
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["path"],
        scenario_artifact.display().to_string()
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["screen"]["id"],
        sts2::bridge::SHOP_SCREEN_ID
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["validation"]["checked"][0],
        "run.seed"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["validation"]["expectedSummary"]["run"]["seed"],
        "mock-seed"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("scenario artifact path"),
    );
    assert!(step_artifact_path.exists());
    assert_eq!(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["stepCanonicalId"],
        "dev.load-scenario"
    );
    assert_eq!(
        read_json_file(&step_artifact_path)["screen"]["id"],
        sts2::bridge::SHOP_SCREEN_ID
    );
    assert_eq!(
        read_json_file(&step_artifact_path)["validation"]["observedSummary"]["run"]["seed"],
        "mock-seed"
    );
}
