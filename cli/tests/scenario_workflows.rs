use std::fs;
use std::path::Path;
use std::process::Command;

use serde_json::Value;
use sha2::Digest;

fn sts2_bin() -> &'static str {
    env!("CARGO_BIN_EXE_sts2")
}

fn write_config_in(root: &Path, contents: &str) -> std::path::PathBuf {
    let path = root.join("sts2.local.yaml");
    fs::write(&path, contents).expect("write config");
    path
}

fn run_in_dir(root: &Path, args: &[&str]) -> std::process::Output {
    Command::new(sts2_bin())
        .current_dir(root)
        .args(args)
        .output()
        .expect("run sts2")
}

fn parse_json(output: &std::process::Output) -> Value {
    serde_json::from_slice(&output.stdout).expect("json stdout")
}

fn active_multiplayer_scenario_yaml() -> &'static str {
    r#"schemaVersion: spirectl.scenario/v0
name: active-multiplayer-combat
createdAt: "2026-04-24T00:00:00Z"
source:
  gameVersion: test-game
  bridgeVersion: test-bridge
  spirectlVersion: test-cli
  screen:
    id: combat
    title: Combat
    instanceId: screen:combat:1
restore:
  mode: sparse
  quality: degraded
  fallbackPolicy: fall-back-to-sparse-with-degraded-quality
multiplayer:
  isMultiplayer: true
  restoreMode: degraded-local-only
  localPlayerId: p:100
  hostPlayerId: p:100
  localPlayerRole: host
  requiresRemoteClients: true
  degradedLocalOnlyAvailable: true
  players:
    - id: p:100
      netId: "100"
      slotId: 0
      selectedCharacterId: ironclad
      character: ironclad
      isReady: true
      isLocal: true
      isHost: true
      isRemote: false
    - id: p:200
      netId: "200"
      slotId: 1
      selectedCharacterId: silent
      character: silent
      isReady: true
      isLocal: false
      isHost: false
      isRemote: true
screenState:
  screen:
    id: combat
  combat:
    turn: 1
notices: []
"#
}

#[test]
fn scenario_export_writes_sparse_yaml_without_exact_bundle_by_default() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: shop\n",
    );
    let output_path = temp.path().join("shop-anchor.sts2.scenario.yaml");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&output);

    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(payload["schemaVersion"], "spirectl.scenario/v0");
    assert_eq!(payload["command"], "dev scenario export");
    assert_eq!(payload["includeExact"], false);
    assert_eq!(payload["screen"]["id"], "Screens.Shops.NMerchantInventory");
    assert_eq!(payload["restoreQuality"], "partial");
    assert_eq!(
        payload["restoreSupport"]["screen"],
        "Screens.Shops.NMerchantInventory"
    );
    assert!(
        payload["restoreSupport"]["fields"]
            .as_array()
            .expect("restore support fields")
            .iter()
            .any(|field| field["path"] == "shop.purchasableItems[]")
    );
    assert!(output_path.exists());
    assert!(
        !temp
            .path()
            .join("shop-anchor.sts2.scenario.exact-bundle.json")
            .exists()
    );

    let scenario: serde_json::Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    assert_eq!(scenario["schemaVersion"], "spirectl.scenario/v0");
    assert_eq!(scenario["restore"]["mode"], "sparse");
    assert!(scenario["restore"].get("exactBundle").is_none());
    assert!(
        scenario["restore"]["fieldReports"]
            .as_array()
            .expect("field reports")
            .iter()
            .any(|field| field["path"] == "shop.purchasableItems[]")
    );
    assert_eq!(
        scenario["source"]["screen"]["id"],
        "Screens.Shops.NMerchantInventory"
    );
    assert!(scenario["screenState"]["shop"].is_object());
    assert_eq!(
        scenario["screenState"]["shop"]["choiceIds"][0],
        "shop:relic:anchor:0"
    );
    assert!(scenario["screenState"]["choices"].is_array());
    assert!(scenario["screenState"]["availableActions"].is_array());
}

#[test]
fn scenario_export_writes_multiplayer_lobby_metadata() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: lobby\n",
    );
    let output_path = temp.path().join("lobby.sts2.scenario.yaml");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&output);

    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        payload["screen"]["id"],
        "Screens.CharacterSelect.NCharacterSelectScreen"
    );
    assert_eq!(payload["multiplayer"]["isMultiplayer"], true);
    assert_eq!(payload["multiplayer"]["restoreMode"], "lobby-only");
    assert_eq!(payload["multiplayer"]["localPlayerId"], "p:100");
    assert_eq!(payload["multiplayer"]["hostPlayerId"], "p:100");
    assert_eq!(payload["multiplayer"]["players"][1]["id"], "p:200");
    assert_eq!(payload["multiplayer"]["players"][1]["isRemote"], true);

    let scenario: Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    assert_eq!(scenario["multiplayer"]["lobby"]["lobbyId"], "start-run");
    assert_eq!(
        scenario["multiplayer"]["lobby"]["availableCharacters"][0]["id"],
        "ironclad"
    );
}

#[test]
fn scenario_export_writes_exact_bundle_sidecar_when_requested() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-a.sts2.scenario.yaml");
    let bundle_path = temp.path().join("combat-a.sts2.scenario.exact-bundle.json");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
            "--include-exact",
        ],
    );
    let payload = parse_json(&output);

    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(payload["schemaVersion"], "spirectl.scenario/v0");
    assert_eq!(payload["includeExact"], true);
    assert_eq!(payload["screen"]["id"], "combat");
    assert_eq!(payload["exactBundleKind"], "fixture-backed");
    assert_eq!(
        payload["exactBundlePath"],
        bundle_path.to_string_lossy().as_ref()
    );
    assert!(output_path.exists());
    assert!(bundle_path.exists());

    let scenario: serde_json::Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    let exact_bundle = &scenario["restore"]["exactBundle"];
    assert_eq!(
        exact_bundle["path"],
        "combat-a.sts2.scenario.exact-bundle.json"
    );
    assert_eq!(exact_bundle["formatVersion"], "spirectl.scenario.bundle/v0");
    assert_eq!(exact_bundle["contentType"], "application/json");
    assert!(exact_bundle["sha256"].as_str().expect("sha").len() == 64);
    assert!(exact_bundle["sizeBytes"].as_u64().expect("size") > 0);
}

#[test]
fn scenario_load_accepts_save_backed_exact_sidecar_metadata() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: lobby\n",
    );
    let scenario_path = temp.path().join("load-run.sts2.scenario.yaml");
    let save_path = temp.path().join("load-run.sts2.scenario.exact-save");
    let save_bytes = br#"{"seed":"LFWLSYZ5SP","rng":{"counters":{"shuffle":605}}}"#;
    fs::write(&save_path, save_bytes).expect("save sidecar");
    let sha = sha2::Sha256::digest(save_bytes);
    let sha_hex = format!("{sha:x}");
    fs::write(
        &scenario_path,
        format!(
            r#"schemaVersion: spirectl.scenario/v0
name: load-run
createdAt: "2026-04-24T00:00:00Z"
source:
  gameVersion: mock-game/0
  bridgeVersion: spirectl-bridge/0.0.0
  screen:
    id: Screens.CharacterSelect.NCharacterSelectScreen
restore:
  mode: sparse
  quality: partial
  exactBundle:
    path: load-run.sts2.scenario.exact-save
    formatVersion: spirectl.scenario.save-run/v0
    contentType: application/vnd.spirectl.sts2-save+json
    sha256: {sha_hex}
    sizeBytes: {}
    versionSensitive: true
  fallbackPolicy: fall-back-to-sparse-with-degraded-quality
run:
  seed: LFWLSYZ5SP
  act: 2
  floor: 17
  ascension: 0
  players:
    - id: p:100
      character: ironclad
      isLocal: true
      isHost: true
      isRemote: false
screenState: {{}}
notices: []
"#,
            save_bytes.len()
        ),
    )
    .expect("scenario");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            scenario_path.to_str().expect("scenario path"),
        ],
    );
    let payload = parse_json(&output);

    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(payload["exactBundleUsed"], true);
}

#[test]
fn scenario_load_restores_through_bridge_and_validates_state() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-a.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(
        export_output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&export_output.stderr)
    );

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert!(
        load_output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&load_output.stderr)
    );
    assert_eq!(payload["schemaVersion"], "spirectl.scenario/v0");
    assert_eq!(payload["command"], "dev scenario load");
    assert_eq!(payload["restoreQuality"], "partial");
    assert_eq!(payload["exactBundleUsed"], false);
    assert_eq!(payload["sparseFallbackUsed"], false);
    assert_eq!(payload["screen"]["id"], "combat");
    assert_eq!(payload["validation"]["status"], "passed");
    assert!(
        payload["validation"]["checked"]
            .as_array()
            .expect("checked")
            .iter()
            .any(|item| item == "screen.id")
    );
}

#[test]
fn scenario_load_restores_multiplayer_lobby_metadata() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: lobby\n",
    );
    let output_path = temp.path().join("lobby.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(export_output.status.success());

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert!(
        load_output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&load_output.stderr)
    );
    assert_eq!(
        payload["screen"]["id"],
        "Screens.CharacterSelect.NCharacterSelectScreen"
    );
    assert_eq!(payload["multiplayerRestore"]["mode"], "lobby-only");
    assert_eq!(
        payload["multiplayerRestore"]["remotePlayerMode"],
        "placeholder"
    );
    assert_eq!(
        payload["multiplayerRestore"]["restoredPlayerIds"][1],
        "p:200"
    );
    assert_eq!(payload["validation"]["status"], "passed");
    assert!(
        payload["validation"]["checked"]
            .as_array()
            .expect("checked fields")
            .iter()
            .any(|field| field == "multiplayer.players[].isReady")
    );
}

#[test]
fn scenario_load_reports_multiplayer_lobby_validation_mismatches() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: lobby\n",
    );
    let output_path = temp.path().join("lobby.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(export_output.status.success());
    let mut scenario: serde_yaml::Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    scenario["multiplayer"]["players"][1]["isReady"] = serde_yaml::Value::Bool(false);
    fs::write(
        &output_path,
        serde_yaml::to_string(&scenario).expect("scenario yaml"),
    )
    .expect("write scenario");

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(3));
    assert_eq!(payload["error"]["code"], "restore_validation_mismatch");
    assert_eq!(payload["error"]["classification"], "failed");
    assert_eq!(payload["error"]["multiplayerRestore"]["mode"], "lobby-only");
    assert!(
        payload["error"]["mismatchFields"]
            .as_array()
            .expect("mismatch fields")
            .iter()
            .any(|field| field == "multiplayer.players[].isReady")
    );
    let mismatch = payload["error"]["mismatches"]
        .as_array()
        .expect("mismatches")
        .iter()
        .find(|mismatch| mismatch["path"] == "multiplayer.players[].isReady")
        .expect("isReady mismatch");
    assert_eq!(mismatch["supportClass"], "degraded-local-multiplayer");
    assert_eq!(mismatch["reasonCode"], "multiplayer_restore_validation");
    assert!(
        mismatch["suggestedNextStep"]
            .as_str()
            .expect("next step")
            .len()
            > 10
    );
    assert!(mismatch["expected"].is_array());
    assert!(mismatch["observed"].is_array());
    assert!(payload["error"]["expectedSummary"].is_object());
    assert!(payload["error"]["observedSummary"].is_object());
}

#[test]
fn scenario_load_active_multiplayer_requires_degradation_flag() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let path = temp.path().join("active.sts2.scenario.yaml");
    fs::write(&path, active_multiplayer_scenario_yaml()).expect("write scenario");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            path.to_str().expect("scenario path"),
        ],
    );
    let payload = parse_json(&output);

    assert_eq!(output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "degradation_flag_required");
    assert_eq!(
        payload["error"]["requiredFlag"],
        "--allow-degraded-local-multiplayer"
    );
    assert_eq!(payload["error"]["remotePlayerIds"][0], "p:200");
}

#[test]
fn scenario_load_active_multiplayer_degraded_local_only_with_flag() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let path = temp.path().join("active.sts2.scenario.yaml");
    fs::write(&path, active_multiplayer_scenario_yaml()).expect("write scenario");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            path.to_str().expect("scenario path"),
            "--allow-degraded-local-multiplayer",
        ],
    );
    let payload = parse_json(&output);

    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(payload["restoreQuality"], "degraded");
    assert_eq!(payload["multiplayerRestore"]["mode"], "degraded-local-only");
    assert_eq!(payload["multiplayerRestore"]["remotePlayerMode"], "omitted");
    assert_eq!(
        payload["multiplayerRestore"]["omittedRemotePlayerIds"][0],
        "p:200"
    );
}

#[test]
fn scenario_load_reports_summary_mismatches() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-a.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(export_output.status.success());
    let mut scenario: serde_yaml::Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    scenario["screenState"]["combat"]["turn"] = serde_yaml::Value::Number(99.into());
    fs::write(
        &output_path,
        serde_yaml::to_string(&scenario).expect("scenario yaml"),
    )
    .expect("write scenario");

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(3));
    assert_eq!(payload["error"]["code"], "restore_validation_mismatch");
    assert_eq!(payload["error"]["classification"], "failed");
    assert!(
        payload["error"]["mismatchFields"]
            .as_array()
            .expect("mismatch fields")
            .iter()
            .any(|field| field == "combat.turn")
    );
    let mismatch = payload["error"]["mismatches"]
        .as_array()
        .expect("mismatches")
        .iter()
        .find(|mismatch| mismatch["path"] == "combat.turn")
        .expect("combat turn mismatch");
    assert_eq!(mismatch["supportClass"], "exact");
    assert!(mismatch["reasonCode"].as_str().expect("reason").len() > 3);
    assert!(
        mismatch["suggestedNextStep"]
            .as_str()
            .expect("next step")
            .len()
            > 10
    );
    assert_eq!(mismatch["expected"], 99);
    assert_eq!(mismatch["observed"], 2);
    assert!(payload["error"]["expectedSummary"].is_object());
    assert!(payload["error"]["observedSummary"].is_object());
}

#[test]
fn scenario_load_reports_missing_current_validation_field() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-missing.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(export_output.status.success());
    let mut scenario: serde_yaml::Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario");
    scenario["screenState"]["combat"]["turn"] = serde_yaml::Value::Null;
    fs::write(
        &output_path,
        serde_yaml::to_string(&scenario).expect("scenario yaml"),
    )
    .expect("write scenario");

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(3));
    assert_eq!(payload["error"]["code"], "restore_validation_mismatch");
    let mismatch = payload["error"]["mismatches"]
        .as_array()
        .expect("mismatches")
        .iter()
        .find(|mismatch| mismatch["path"] == "combat.turn")
        .expect("combat turn mismatch");
    assert!(mismatch["expected"].is_null());
    assert_eq!(mismatch["observed"], 2);
    assert_eq!(mismatch["supportClass"], "exact");
    assert!(mismatch["reasonCode"].as_str().expect("reason").len() > 3);
    assert!(
        mismatch["suggestedNextStep"]
            .as_str()
            .expect("next step")
            .len()
            > 10
    );
}

#[test]
fn scenario_load_rejects_missing_file() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(temp.path(), "transport:\n  kind: mock\n");
    let missing = temp.path().join("missing.sts2.scenario.yaml");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            missing.to_str().expect("missing path"),
        ],
    );
    let payload = parse_json(&output);

    assert_eq!(output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "scenario_file_not_found");
}

#[test]
fn scenario_load_rejects_invalid_schema_version() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(temp.path(), "transport:\n  kind: mock\n");
    let path = temp.path().join("bad.sts2.scenario.yaml");
    fs::write(
        &path,
        r#"schemaVersion: spirectl.scenario/unsupported
name: bad
createdAt: "2026-04-24T00:00:00Z"
source:
  screen:
    id: combat
restore:
  mode: sparse
  quality: partial
  fallbackPolicy: fall-back-to-sparse-with-degraded-quality
screenState: {}
"#,
    )
    .expect("write scenario");

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            path.to_str().expect("scenario path"),
        ],
    );
    let payload = parse_json(&output);

    assert_eq!(output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "invalid_scenario_schema_version");
}

#[test]
fn scenario_load_rejects_exact_bundle_hash_mismatch() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-a.sts2.scenario.yaml");
    let bundle_path = temp.path().join("combat-a.sts2.scenario.exact-bundle.json");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
            "--include-exact",
        ],
    );
    assert!(export_output.status.success());
    let mut bundle_bytes = fs::read(&bundle_path).expect("read sidecar");
    let first = bundle_bytes.first_mut().expect("non-empty sidecar");
    *first = first.wrapping_add(1);
    fs::write(&bundle_path, bundle_bytes).expect("tamper sidecar");

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "exact_bundle_hash_mismatch");
}

#[test]
fn scenario_load_rejects_exact_bundle_size_mismatch() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let output_path = temp.path().join("combat-a.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
            "--include-exact",
        ],
    );
    assert!(export_output.status.success());

    let mut scenario: Value =
        serde_yaml::from_str(&fs::read_to_string(&output_path).expect("scenario yaml"))
            .expect("scenario value");
    let size = scenario["restore"]["exactBundle"]["sizeBytes"]
        .as_u64()
        .expect("size bytes");
    scenario["restore"]["exactBundle"]["sizeBytes"] = Value::from(size + 1);
    fs::write(
        &output_path,
        serde_yaml::to_string(&scenario).expect("serialize scenario"),
    )
    .expect("rewrite scenario");

    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "exact_bundle_size_mismatch");
}

#[test]
fn scenario_load_rejects_mock_screen_mismatch() {
    let temp = tempfile::tempdir().expect("temp dir");
    let shop_config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: shop\n",
    );
    let output_path = temp.path().join("shop-anchor.sts2.scenario.yaml");

    let export_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            shop_config.to_str().expect("shop config path"),
            "dev",
            "scenario",
            "export",
            "--output",
            output_path.to_str().expect("output path"),
        ],
    );
    assert!(export_output.status.success());

    let combat_config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );
    let load_output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            combat_config.to_str().expect("combat config path"),
            "dev",
            "scenario",
            "load",
            "--path",
            output_path.to_str().expect("output path"),
        ],
    );
    let payload = parse_json(&load_output);

    assert_eq!(load_output.status.code(), Some(3));
    assert_eq!(payload["error"]["code"], "invalid_action");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("unsupported_source_screen")
    );
}
