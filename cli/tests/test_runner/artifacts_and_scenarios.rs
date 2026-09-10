use super::*;

#[test]
fn restore_diagnostics_current() {
    let dir = tempfile::tempdir().expect("scenario dir");
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let repro_dir = dir.path().join("repros");
    fs::create_dir_all(&repro_dir).expect("create repro dir");
    let scenario_artifact = repro_dir.join("mock-shop.sts2.scenario.yaml");
    write_sparse_shop_scenario(&scenario_artifact);
    let workflow_path = dir.path().join("restore-diagnostics-current.sts2.yaml");
    fs::write(
        &workflow_path,
        r#"
name: restore-diagnostics-current
steps:
  - dev.load-scenario:
      path: ./repros/mock-shop.sts2.scenario.yaml
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
    let output = &payload["scenarios"][0]["steps"][0]["output"];
    assert_eq!(
        output["validation"]["expectedSummary"]["screen"]["id"],
        sts2::bridge::SHOP_SCREEN_ID
    );
    assert_eq!(
        output["validation"]["observedSummary"]["screen"]["id"],
        sts2::bridge::SHOP_SCREEN_ID
    );
    assert_eq!(output["validation"]["mismatches"], json!([]));
    assert_eq!(output["bridgeVerification"]["status"], "failed");
    assert_eq!(
        output["bridgeVerification"]["mismatches"][0]["supportClass"],
        "inferred"
    );
    assert_eq!(
        output["bridgeVerification"]["mismatches"][0]["reasonCode"],
        "fixture-choice-expansion"
    );
    assert_eq!(
        output["bridgeVerification"]["expectedSummary"]["choices"][0],
        "shop:leave"
    );
    assert_eq!(
        output["bridgeVerification"]["observedSummary"]["choices"][0],
        "shop:p1:card:strike:0"
    );

    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("scenario artifact path"),
    );
    assert!(step_artifact_path.exists());
    let artifact = read_json_file(&step_artifact_path);
    assert_eq!(
        artifact["validation"]["expectedSummary"],
        output["validation"]["expectedSummary"]
    );
    assert_eq!(
        artifact["validation"]["observedSummary"],
        output["validation"]["observedSummary"]
    );
    assert_eq!(artifact["validation"]["mismatches"], json!([]));
    assert_eq!(
        artifact["bridgeVerification"]["mismatches"][0]["suggestedNextStep"],
        "Inspect validation.expectedSummary and validation.observedSummary for restored choices."
    );
}

#[test]
fn test_run_dev_load_scenario_accepts_degraded_multiplayer_flag() {
    let dir = tempfile::tempdir().expect("scenario dir");
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let repro_dir = dir.path().join("repros");
    fs::create_dir_all(&repro_dir).expect("create repro dir");
    let scenario_artifact = repro_dir.join("active-mp.sts2.scenario.yaml");
    write_active_multiplayer_scenario(&scenario_artifact);
    let workflow_path = dir.path().join("load-active-mp.sts2.yaml");
    fs::write(
        &workflow_path,
        r#"
name: load-active-multiplayer
steps:
  - dev.load-scenario:
      path: ./repros/active-mp.sts2.scenario.yaml
      allowDegradedLocalMultiplayer: true
"#,
    )
    .expect("write workflow");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);

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
        payload["scenarios"][0]["steps"][0]["input"]["allowDegradedLocalMultiplayer"],
        true
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["restoreQuality"],
        "degraded"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["multiplayerRestore"]["mode"],
        "degraded-local-only"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["multiplayerRestore"]["omittedRemotePlayerIds"]
            [0],
        "p:200"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["compatibilityNotes"][1]["code"],
        "remote-clients-omitted"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("scenario artifact path"),
    );
    let artifact = read_json_file(&step_artifact_path);
    assert_eq!(artifact["restoreQuality"], "degraded");
    assert_eq!(
        artifact["multiplayerRestore"]["mode"],
        "degraded-local-only"
    );
    assert_eq!(
        artifact["multiplayerRestore"]["omittedRemotePlayerIds"][0],
        "p:200"
    );
    assert!(
        artifact["compatibilityNotes"]
            .as_array()
            .expect("compatibility notes")
            .iter()
            .any(|note| note["code"] == "remote-clients-omitted")
    );
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_executes_dev_load_fixture_from_relative_scenario_path_with_absolute_output() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-relative-scenario-fixture.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let cwd = std::env::current_dir().expect("cwd");
    let scenario_dir = tempfile::tempdir_in(&cwd).expect("scenario dir");
    let fixture_path = scenario_dir.path().join("basic-combat.sts2.fixture.yaml");
    write_fixture(&fixture_path);
    let scenario_path = scenario_dir.path().join("fixture-relative.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: relative-scenario-fixture
steps:
  - dev.load-fixture:
      path: ./basic-combat.sts2.fixture.yaml
"#,
    )
    .expect("write scenario");
    let relative_scenario_path = scenario_path
        .strip_prefix(&cwd)
        .expect("scenario under cwd")
        .display()
        .to_string();

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let relative_scenario_path_for_run = relative_scenario_path.clone();
    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            &relative_scenario_path_for_run,
        ])
    })
    .await
    .expect("join relative runner task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["resolvedPath"],
        fixture_path.display().to_string()
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_executes_dev_load_fixture_from_inline_json_relative_to_cwd() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("spirectl-inline-fixture.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = FixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let cwd = std::env::current_dir().expect("cwd");
    let fixture = tempfile::Builder::new()
        .prefix("inline-fixture-")
        .suffix(".sts2.fixture.yaml")
        .tempfile_in(&cwd)
        .expect("fixture in cwd");
    write_fixture(fixture.path());
    let fixture_name = fixture
        .path()
        .file_name()
        .and_then(|name| name.to_str())
        .expect("fixture file name")
        .to_string();
    let expected_path = cwd.join(&fixture_name).display().to_string();
    let fixture_name_for_run = fixture_name.clone();

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
            "--inline",
            &format!(
                r#"{{"name":"inline-fixture","steps":[{{"dev.load_fixture":{{"path":"{fixture_name_for_run}"}}}}]}}"#
            ),
        ])
    })
    .await
    .expect("join inline runner task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.load-fixture"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["requestedPath"],
        fixture_name
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["resolvedPath"],
        expected_path
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_preserves_fixture_model_ids_before_ipc() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-inline-normalized-fixture.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = NormalizedFixtureLoadBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let scenario_dir = tempfile::tempdir().expect("scenario dir");
    let fixture_path = scenario_dir.path().join("basic-combat.sts2.fixture.yaml");
    fs::write(
        &fixture_path,
        r#"schemaVersion: spirectl.fixture/v0
name: "  basic-combat  "
run:
  seed: "   "
  players:
    - id: "  p1  "
      characterId: IRONCLAD
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario_path = scenario_dir
        .path()
        .join("runner-normalized-fixture.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: normalized-fixture
steps:
  - dev.load-fixture:
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
    .expect("join runner task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["fixture"]["name"],
        "basic-combat"
    );

    server.abort();
}

#[test]
fn test_run_reports_invalid_fixture_for_dev_load_fixture_step() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-floor
run:
  actFloor: 0
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario = write_scenario(&format!(
        r#"
name: invalid-load-fixture
steps:
  - dev.load-fixture:
      path: {}
"#,
        fixture.path().display()
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
    let scenario_payload = &payload["scenarios"][0];
    let failure = &scenario_payload["failure"];

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "failed");
    assert_eq!(scenario_payload["status"], "failed");
    assert_eq!(failure["stepIndex"], 1);
    assert_eq!(failure["stepCanonicalId"], "dev.load-fixture");
    assert_eq!(failure["phase"], "setup");
    assert_eq!(failure["exitCode"], 2);
    assert_eq!(failure["error"]["code"], "invalid_fixture");
    assert_eq!(failure["error"]["details"][0]["field"], "run.actFloor");
}

#[test]
fn fixture_two_ironclad_nibbits_weak_scenario() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let config = repo_root.join("tests/sts2.mock.yaml");
    let scenario =
        repo_root.join("tests/scenarios/fixture-two-ironclad-nibbits-weak-combat.sts2.yaml");

    let response = run(&[
        "sts2",
        "--config",
        config.to_str().expect("utf8 config"),
        "--json",
        "test",
        "run",
        scenario.to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
}

#[test]
fn fixture_two_ironclad_lobby_scenario() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let config = repo_root.join("tests/sts2.mock.yaml");
    let scenario = repo_root.join("tests/scenarios/fixture-two-ironclad-lobby.sts2.yaml");

    let response = run(&[
        "sts2",
        "--config",
        config.to_str().expect("utf8 config"),
        "--json",
        "test",
        "run",
        scenario.to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
}

#[test]
fn fixture_initial_neow_scenario() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let config = repo_root.join("tests/sts2.mock.yaml");
    let scenario = repo_root.join("tests/scenarios/fixture-initial-neow.sts2.yaml");

    let response = run(&[
        "sts2",
        "--config",
        config.to_str().expect("utf8 config"),
        "--json",
        "test",
        "run",
        scenario.to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
}

#[test]
fn fixture_two_ironclad_neow_scenario() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let config = repo_root.join("tests/sts2.mock.yaml");
    let scenario = repo_root.join("tests/scenarios/fixture-two-ironclad-neow.sts2.yaml");

    let response = run(&[
        "sts2",
        "--config",
        config.to_str().expect("utf8 config"),
        "--json",
        "test",
        "run",
        scenario.to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
}

#[test]
fn test_run_reports_invalid_multiplayer_combat_remote_player() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    // Host-local metadata now defaults; an explicit true-remote player
    // (isLocal: false) is still rejected during fixture normalization.
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-multiplayer-combat
run:
  players:
    - id: "p:1"
      characterId: IRONCLAD
    - id: "p:2"
      characterId: IRONCLAD
      isLocal: false
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario = write_scenario(&format!(
        r#"
name: invalid-multiplayer-combat-load-fixture
steps:
  - dev.load-fixture:
      path: {}
"#,
        fixture.path().display()
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
    let scenario_payload = &payload["scenarios"][0];
    let failure = &scenario_payload["failure"];

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "failed");
    assert_eq!(scenario_payload["status"], "failed");
    assert_eq!(failure["stepIndex"], 1);
    assert_eq!(failure["stepCanonicalId"], "dev.load-fixture");
    assert_eq!(failure["phase"], "setup");
    assert_eq!(failure["exitCode"], 2);
    assert_eq!(failure["error"]["code"], "invalid_fixture");
    assert_eq!(
        failure["error"]["details"][0]["field"],
        "run.players[1].isLocal"
    );
}

#[test]
fn test_run_human_output_marks_load_fixture_failures_as_setup_failures() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-floor
run:
  actFloor: 0
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario = write_scenario(&format!(
        r#"
name: invalid-load-fixture-human
steps:
  - dev.load-fixture:
      path: {}
"#,
        fixture.path().display()
    ));

    let response = run(&[
        "sts2",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);

    assert_eq!(response.exit_code, 2);
    assert!(response.stdout.contains("setup step 1 dev.load-fixture:"));
}

#[test]
fn test_run_reports_missing_schema_version_fixture_for_dev_load_fixture_step() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"name: invalid-schema-version
run:
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario = write_scenario(&format!(
        r#"
name: invalid-load-fixture-act
steps:
  - dev.load-fixture:
      path: {}
"#,
        fixture.path().display()
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
    let scenario_payload = &payload["scenarios"][0];
    let failure = &scenario_payload["failure"];

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "failed");
    assert_eq!(scenario_payload["status"], "failed");
    assert_eq!(failure["stepIndex"], 1);
    assert_eq!(failure["stepCanonicalId"], "dev.load-fixture");
    assert_eq!(failure["phase"], "setup");
    assert_eq!(failure["exitCode"], 2);
    assert_eq!(failure["error"]["code"], "invalid_fixture");
    assert_eq!(failure["error"]["details"][0]["field"], "schemaVersion");
}

#[test]
fn test_run_classifies_game_deploy_failures_as_setup_failures() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: invalid-game-deploy
steps:
  - game.deploy:
      path: ./missing-mod-project
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

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "failed");
    assert_eq!(scenario_payload["status"], "failed");
    assert_eq!(failure["stepIndex"], 1);
    assert_eq!(failure["stepCanonicalId"], "game.deploy");
    assert_eq!(failure["phase"], "setup");
}

#[test]
fn test_run_collects_failure_artifacts_after_dev_load_fixture_validation_failure() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(
        fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-hp
run:
  players:
    - id: "p:1"
      creature: { currentHp: 81, maxHp: 80 }
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
    let scenario = write_scenario(&format!(
        r#"
name: invalid-load-fixture-artifacts
steps:
  - dev.load-fixture:
      path: {}
"#,
        fixture.path().display()
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
    let scenario_payload = &payload["scenarios"][0];
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

    assert_eq!(response.exit_code, 2);
    assert!(failure_summary_path.exists());
    assert_eq!(
        diagnostics["captures"]["gameInfo"]["data"]["repository"],
        "spirectl"
    );
    assert_eq!(
        read_json_file(&failure_summary_path)["error"]["code"],
        "invalid_fixture"
    );
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
    assert!(failure_evidence_dir.join("diagnostics.json").exists());
    assert!(failure_evidence_dir.join("game-info.json").exists());
    assert!(failure_evidence_dir.join("state.json").exists());
    assert!(failure_evidence_dir.join("logs.json").exists());
    assert!(!failure_evidence_dir.join("scene-tree.json").exists());
}

#[test]
fn fixture_restore_expansion() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let scenario_path = repo_root.join("tests/scenarios/fixture-restore-expansion.sts2.yaml");
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario_path.to_str().expect("utf8 scenario path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");

    let step_artifacts = payload["scenarios"][0]["artifacts"]["stepArtifacts"]
        .as_array()
        .expect("step artifacts");
    assert!(
        step_artifacts.len() >= 13,
        "expected one load-fixture artifact for each expanded recipe"
    );

    let first_artifact_path = PathBuf::from(
        step_artifacts[0]["path"]
            .as_str()
            .expect("first load-fixture artifact path"),
    );
    let first_artifact = read_json_file(&first_artifact_path);
    assert_eq!(
        first_artifact["loaded"]["screen"]["id"],
        sts2::bridge::MAP_SCREEN_ID
    );
    assert_eq!(
        first_artifact["recipeReport"]["recipeName"],
        "Screens.Map.NMapScreen-recipe"
    );
    assert_eq!(
        first_artifact["recipeReport"]["bridgeValidation"]["status"],
        "passed"
    );
    assert_eq!(
        first_artifact["recipeReport"]["appliedFields"][0]["fieldPath"],
        "screen"
    );

    let failure_fixture = tempfile::NamedTempFile::new().expect("invalid fixture");
    fs::write(
        failure_fixture.path(),
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-fixture-restore-expansion
run:
  actFloor: 0
  players:
    - id: "p:1"
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write invalid fixture");
    let failure_scenario = write_scenario(&format!(
        r#"
name: invalid-fixture-restore-expansion
steps:
  - dev.load-fixture:
      path: {}
"#,
        failure_fixture.path().display()
    ));

    let failure_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        failure_scenario.path().to_str().expect("utf8 path"),
    ]);
    let failure_payload: Value =
        serde_json::from_str(&failure_response.stdout).expect("failure json");
    let failure = &failure_payload["scenarios"][0]["failure"];

    assert_eq!(failure_response.exit_code, 2);
    assert_eq!(failure_payload["status"], "failed");
    assert_eq!(failure["stepCanonicalId"], "dev.load-fixture");
    assert_eq!(failure["phase"], "setup");
    assert_eq!(failure["error"]["code"], "invalid_fixture");
}

#[test]
fn test_run_executes_unready_step_against_ready_lobby() {
    let scenario = write_scenario(
        r#"
name: lobby-unready
steps:
  - dev.assert:
      path: characterSelect.lobby.players[id=p:100].isReady
      equals: true
  - act.unready
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        write_config(&AppConfig {
            transport: sts2::TransportConfig {
                kind: TransportKind::Mock,
                mock_scenario: MockScenario::LobbyReady,
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
        "act.unready"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["output"]["kind"],
        "unready"
    );
}

#[test]
fn test_run_executes_play_card_step_in_combat() {
    let scenario = write_scenario(
        r#"
name: combat-play-card
steps:
  - dev.assert:
      path: run.currentRoom.scene
      equals: rooms/combat_room
  - act.play-card:
      card: c_1
      target: e_1
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
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "act.play-card"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["output"]["kind"],
        "play-card"
    );
}

#[test]
fn test_run_executes_use_potion_step_in_combat() {
    let scenario = write_scenario(
        r#"
name: combat-use-potion
steps:
  - dev.assert:
      path: run.currentRoom.scene
      equals: rooms/combat_room
  - act.use-potion:
      potion: potion:p1:0:fire-potion
      target: e_1
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
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "act.use-potion"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["output"]["kind"],
        "use-potion"
    );
}

#[test]
fn test_run_validates_combat_potion_fixture_scenarios() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");

    for scenario_name in [
        "fixture-combat-potion-target.sts2.yaml",
        "fixture-combat-potion-card.sts2.yaml",
    ] {
        let scenario_path = repo_root.join("tests/scenarios").join(scenario_name);
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            scenario_path.to_str().expect("scenario path"),
        ]);
        let payload: Value = serde_json::from_str(&response.stdout).expect("json");

        assert_eq!(response.exit_code, 0, "{scenario_name}: {payload:#}");
        assert_eq!(payload["scenarios"][0]["status"], "passed");
    }
}

#[test]
fn test_run_executes_select_map_node_step_on_map_screen() {
    let scenario = write_scenario(
        r#"
name: map-select-node
steps:
  - dev.assert:
      path: run.map.view.isOpen
      equals: true
  - act.select-map-node:
      node: map-node:3:1
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        write_config(&AppConfig {
            transport: sts2::TransportConfig {
                kind: TransportKind::Mock,
                mock_scenario: MockScenario::Map,
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
        "act.select-map-node"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["output"]["kind"],
        "select-map-node"
    );
}

#[test]
fn test_run_executes_screenshot_step_with_scenario_local_default_output() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let scenario = write_scenario(
        r#"
name: capture-combat
steps:
  - dev.screenshot
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
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let screenshot_path = scenario_dir.join("steps").join("001-dev-screenshot.png");
    let bytes = fs::read(&screenshot_path).expect("runner screenshot");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.screenshot"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["path"],
        screenshot_path.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["screen"]["id"],
        "combat"
    );
    assert_eq!(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"],
        screenshot_path.to_string_lossy().as_ref()
    );
    assert_eq!(&bytes[..8], b"\x89PNG\r\n\x1a\n");
}

#[test]
fn test_run_executes_couchcoop_target_screenshot_steps() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let scenario = repo_root.join("tests/scenarios/couchcoop-target-screenshots.sts2.yaml");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 config"),
        "test",
        "run",
        scenario.to_str().expect("utf8 scenario"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let expected_viewport = serde_json::json!({
        "width": 1280,
        "height": 720,
    });
    let screenshot_steps = [
        (1, "Screens.CharacterSelect.NCharacterSelectScreen"),
        (3, sts2::bridge::EVENT_ROOM_SCREEN_ID),
        (5, "combat"),
    ];

    assert_eq!(response.exit_code, 0, "{}", response.stdout);
    assert_eq!(payload["status"], "passed");
    assert!(
        scenario_dir.starts_with(artifacts_dir.path().join("test-runs")),
        "{} should be under ignored test-run artifacts in {}",
        scenario_dir.display(),
        artifacts_dir.path().display()
    );

    for (step_index, screen_type) in screenshot_steps {
        let step = &payload["scenarios"][0]["steps"][step_index];
        let screenshot_path = scenario_dir
            .join("steps")
            .join(format!("{:03}-dev-screenshot.png", step_index + 1));
        let bytes = fs::read(&screenshot_path).expect("runner screenshot");

        assert_eq!(step["canonicalId"], "dev.screenshot");
        assert_eq!(step["output"]["requestedViewport"], expected_viewport);
        assert_eq!(step["output"]["appliedViewport"], expected_viewport);
        assert_eq!(step["output"]["screen"]["id"], screen_type);
        assert_eq!(
            step["output"]["path"],
            screenshot_path.to_string_lossy().as_ref()
        );
        assert!(screenshot_path.starts_with(artifacts_dir.path()));
        assert_eq!(&bytes[..8], b"\x89PNG\r\n\x1a\n");
    }
}

#[test]
fn test_run_executes_dev_log_health_step_and_persists_json_artifact() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: log-health
steps:
  - dev.log-health:
      tail: 10
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
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("step artifact path"),
    );

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.log-health"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["status"],
        "healthy"
    );
    assert!(step_artifact_path.exists());
    assert_eq!(read_json_file(&step_artifact_path)["status"], "healthy");
}

#[test]
fn test_run_executes_dev_diagnostics_step_and_persists_bundle_metadata() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let scenario = write_scenario(
        r#"
name: diagnostics-step
steps:
  - dev.diagnostics:
      bundleDir: ./custom-evidence
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
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("step artifact path"),
    );
    let expected_bundle_dir = scenario_dir.join("custom-evidence");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.diagnostics"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["status"],
        "partial"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["bundle"]["dir"],
        expected_bundle_dir.to_string_lossy().as_ref()
    );
    assert!(step_artifact_path.exists());
    assert!(expected_bundle_dir.join("diagnostics.json").exists());
    assert!(expected_bundle_dir.join("runtime.png").exists());
}

#[test]
fn test_run_rejects_hot_reload_generation_check_without_wait() {
    let scenario = write_scenario(
        r#"
name: hot-reload-invalid
steps:
  - dev.hot-reload:
      project: ./mods/MyHotMod
      expectGenerationChanged: true
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
    assert_eq!(payload["status"], "invalid");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "invalid_step_args"
    );
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_executes_hot_reload_step_and_persists_json_artifact() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("spirectl-runner-hot-reload.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = HotReloadBridgeService { supported: true };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let scenario_dir = tempfile::tempdir().expect("scenario dir");
    write_hot_reload_project(&scenario_dir.path().join("HotMod"));
    let scenario_path = scenario_dir.path().join("runner-hot-reload.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: hot-reload-runner
steps:
  - dev.hot-reload:
      project: ./HotMod
      build: false
      wait: true
      expectGenerationChanged: true
"#,
    )
    .expect("write scenario");

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        artifacts: sts2::ArtifactsConfig {
            dir: artifacts_dir.path().to_string_lossy().into_owned(),
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
    assert_eq!(payload["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.hot-reload"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["generationChanged"],
        true
    );
    assert!(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|artifact| artifact["stepCanonicalId"] == "dev.hot-reload"
                && artifact["kind"] == "json")
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("step artifact path"),
    );
    assert_eq!(
        read_json_file(&step_artifact_path)["generationChanged"],
        true
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_hot_reload_step_fails_for_unsupported_shell() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-runner-hot-reload-unsupported.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = HotReloadBridgeService { supported: false };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let scenario_dir = tempfile::tempdir().expect("scenario dir");
    write_hot_reload_project(&scenario_dir.path().join("HotMod"));
    let scenario_path = scenario_dir
        .path()
        .join("runner-hot-reload-unsupported.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: hot-reload-unsupported
steps:
  - dev.hot-reload:
      project: ./HotMod
      wait: true
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

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["status"], "failed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "hot_reload_shell_unsupported"
    );

    server.abort();
}

#[test]
fn test_run_executes_screenshot_diff_step_and_persists_visual_bundle() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let baseline = artifacts_dir.path().join("baseline.png");
    let baseline_capture = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "screenshot",
        "--output",
        baseline.to_str().expect("utf8 path"),
    ]);
    let _: Value = serde_json::from_str(&baseline_capture.stdout).expect("json");

    let scenario = write_scenario(&format!(
        r#"
name: screenshot-diff
steps:
  - dev.screenshot-diff:
      baseline: {}
"#,
        baseline.display()
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
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let step_dir = scenario_dir.join("steps").join("001-dev-screenshot-diff");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.screenshot-diff"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["matched"],
        true
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["bundle"]["files"]["comparison"],
        step_dir.join("comparison.json").display().to_string()
    );
    assert!(step_dir.join("comparison.json").exists());
    assert!(step_dir.join("baseline.png").exists());
    assert!(step_dir.join("actual.png").exists());
    assert!(step_dir.join("diff.png").exists());
}

#[test]
fn test_run_executes_couchcoop_target_diff_bundle_steps() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let scenario = repo_root
        .join("tests")
        .join("scenarios")
        .join("couchcoop-target-visual-diff.sts2.yaml");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let scenario_payload = &payload["scenarios"][0];
    let scenario_dir = PathBuf::from(
        scenario_payload["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let expected_bundles = [
        (1_usize, "visual-diff/lobby"),
        (3, "visual-diff/initial-neow"),
        (5, "visual-diff/nibbits-weak-combat"),
    ];

    assert_eq!(response.exit_code, 0);
    assert_eq!(scenario_payload["status"], "passed");

    let step_artifacts = scenario_payload["artifacts"]["stepArtifacts"]
        .as_array()
        .expect("step artifacts");

    for (step_array_index, bundle_relative_path) in expected_bundles {
        let step = &scenario_payload["steps"][step_array_index];
        let bundle_dir = scenario_dir.join(bundle_relative_path);
        let comparison_path = bundle_dir.join("comparison.json");
        let baseline_path = bundle_dir.join("baseline.png");
        let actual_path = bundle_dir.join("actual.png");
        let diff_path = bundle_dir.join("diff.png");

        assert_eq!(step["canonicalId"], "dev.screenshot-diff");
        assert_eq!(step["output"]["matched"], true);
        assert_eq!(step["output"]["diffPixels"], 0);
        assert_eq!(step["output"]["diffRatio"], 0.0);
        assert_eq!(
            step["output"]["requestedViewport"],
            json!({ "width": 1280, "height": 720 })
        );
        assert_eq!(
            step["output"]["appliedViewport"],
            json!({ "width": 1280, "height": 720 })
        );
        assert_eq!(step["output"]["restoredViewport"], true);
        assert_eq!(
            step["output"]["bundle"]["files"]["comparison"],
            comparison_path.display().to_string()
        );
        assert_eq!(
            step["output"]["bundle"]["files"]["baseline"],
            baseline_path.display().to_string()
        );
        assert_eq!(
            step["output"]["bundle"]["files"]["actual"],
            actual_path.display().to_string()
        );
        assert_eq!(
            step["output"]["bundle"]["files"]["diff"],
            diff_path.display().to_string()
        );

        assert!(comparison_path.exists());
        assert!(baseline_path.exists());
        assert!(actual_path.exists());
        assert!(diff_path.exists());

        let comparison = read_json_file(&comparison_path);
        assert_eq!(comparison["matched"], true);
        assert_eq!(comparison["diffPixels"], 0);
        assert_eq!(comparison["diffRatio"], 0.0);
        assert_eq!(
            comparison["requestedViewport"],
            json!({ "width": 1280, "height": 720 })
        );
        assert_eq!(
            comparison["appliedViewport"],
            json!({ "width": 1280, "height": 720 })
        );
        assert_eq!(comparison["restoredViewport"], true);
        assert_eq!(comparison["baseline"]["width"], 1);
        assert_eq!(comparison["baseline"]["height"], 1);
        assert_eq!(comparison["actual"]["width"], 1);
        assert_eq!(comparison["actual"]["height"], 1);
        assert_eq!(
            comparison["bundle"]["files"]["comparison"],
            comparison_path.display().to_string()
        );

        let step_number = step["index"].as_u64().expect("step index");
        assert!(step_artifacts.iter().any(|artifact| {
            artifact["stepIndex"] == step_number
                && artifact["stepCanonicalId"] == "dev.screenshot-diff"
                && artifact["kind"] == "json"
                && artifact["path"] == comparison_path.display().to_string()
        }));
    }
}
