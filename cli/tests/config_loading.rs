use serde_json::Value;
use std::fs;
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
#[cfg(not(unix))]
use std::path::Path;
#[cfg(unix)]
use std::path::{Path, PathBuf};
use std::process::Command;

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn run_in_dir(workdir: &Path, args: &[&str]) -> std::process::Output {
    run_in_dir_with_env(workdir, args, &[])
}

fn run_in_dir_with_env(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
) -> std::process::Output {
    Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .envs(envs.iter().copied())
        .output()
        .expect("run sts2 binary")
}

fn run_json_in_dir(workdir: &Path, args: &[&str]) -> Value {
    run_json_in_dir_with_env(workdir, args, &[])
}

fn run_json_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> Value {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("json output")
}

fn run_error_json_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> Value {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        !output.status.success(),
        "expected failure, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("json output")
}

#[cfg_attr(
    not(unix),
    allow(
        dead_code,
        reason = "Only the Unix-gated cases in this suite read YAML back."
    )
)]
fn read_yaml_value(path: &Path) -> serde_yaml::Value {
    let raw = fs::read_to_string(path).expect("read yaml");
    serde_yaml::from_str(&raw).expect("parse yaml")
}

#[cfg(target_os = "linux")]
fn create_linux_steam_install(home_dir: &Path) -> PathBuf {
    let steam_root = home_dir.join(".local/share/Steam");
    let steamapps_dir = steam_root.join("steamapps");
    let game_dir = steamapps_dir.join("common/Slay the Spire 2");
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");

    fs::write(
        steamapps_dir.join("libraryfolders.vdf"),
        format!(
            "\"libraryfolders\"\n{{\n  \"0\"\n  {{\n    \"path\"  \"{}\"\n  }}\n}}\n",
            steam_root.display()
        ),
    )
    .expect("write library folders");
    fs::write(
        steamapps_dir.join("appmanifest_2868840.acf"),
        "\"AppState\"\n{\n  \"appid\"  \"2868840\"\n  \"installdir\"  \"Slay the Spire 2\"\n}\n",
    )
    .expect("write app manifest");

    game_dir
}

#[cfg(target_os = "linux")]
fn create_deployed_bridge_layout(game_dir: &Path) {
    let deploy_dir = game_dir.join("mods/spirectlbridge");
    fs::create_dir_all(&deploy_dir).expect("create bridge deploy dir");
    let version = sts2::bridge::bridge_version()
        .strip_prefix("spirectl-bridge/")
        .expect("bridge version prefix");
    fs::write(
        deploy_dir.join("spirectlbridge.json"),
        format!(
            r#"{{
  "id": "spirectlbridge",
  "version": "{version}",
  "has_pck": false,
  "has_dll": true,
  "affects_gameplay": false
}}"#
        ),
    )
    .expect("write bridge manifest");
    fs::write(deploy_dir.join("spirectlbridge.dll"), []).expect("write loader dll");
    fs::write(deploy_dir.join("Spirectl.BridgeMod.Sts2Host.dll"), []).expect("write host dll");
    fs::write(deploy_dir.join("Spirectl.BridgeMod.dll"), []).expect("write bridge dll");
}

#[cfg(all(target_os = "linux", unix))]
fn write_executable_script(path: &Path, contents: &str) {
    fs::write(path, contents).expect("write script");
    let mut permissions = fs::metadata(path).expect("script metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(path, permissions).expect("chmod script");
}

#[test]
fn built_in_defaults_use_live_ipc_transport() {
    let config = sts2::AppConfig::default();

    assert_eq!(config.game.path, "auto");
    assert_eq!(config.transport.kind, sts2::TransportKind::Ipc);
    assert_eq!(config.transport.mock_scenario, sts2::MockScenario::MainMenu);
    assert_eq!(
        config.service.job_store.mode,
        sts2::ServiceJobStoreMode::Memory
    );
    assert_eq!(config.service.job_store.dir, None);
}

#[test]
fn service_job_store_config_layers_local_over_baseline() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
artifacts:
  dir: ./.sts2/baseline-artifacts
transport:
  kind: mock
service:
  jobStore:
    mode: durable
    dir: remote-baseline
"#,
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
service:
  jobStore:
    dir: remote-local
"#,
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "info"]);

    assert_eq!(
        payload["config"]["artifacts"]["dir"],
        "./.sts2/baseline-artifacts"
    );
    assert_eq!(payload["config"]["service"]["jobStore"]["mode"], "durable");
    assert_eq!(
        payload["config"]["service"]["jobStore"]["dir"],
        "remote-local"
    );
}

#[test]
fn game_info_uses_baseline_config_when_local_is_absent() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
game:
  path: /baseline/game
  resourcesDir: /baseline/resources
transport:
  kind: mock
  mockScenario: combat
"#,
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "info"]);

    assert_eq!(payload["config"]["game"]["path"], "/baseline/game");
    assert_eq!(
        payload["config"]["game"]["resourcesDir"],
        "/baseline/resources"
    );
    assert_eq!(payload["config"]["transport"]["mockScenario"], "combat");
}

#[test]
fn game_info_layers_local_config_over_baseline_by_default() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
game:
  path: /baseline/game
  assembliesDir: /baseline/assemblies
  resourcesDir: /baseline/resources
transport:
  kind: mock
  mockScenario: main-menu
  ipcPath: /baseline.sock
"#,
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
game:
  path: /local/game
  assembliesDir: null
transport:
  mockScenario: combat
  ipcPath: /local.sock
"#,
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "info"]);

    assert_eq!(payload["config"]["game"]["path"], "/local/game");
    assert_eq!(payload["config"]["game"]["assembliesDir"], Value::Null);
    assert_eq!(
        payload["config"]["game"]["resourcesDir"],
        "/baseline/resources"
    );
    assert_eq!(payload["config"]["transport"]["kind"], "mock");
    assert_eq!(payload["config"]["transport"]["mockScenario"], "combat");
    assert_eq!(payload["config"]["transport"]["ipcPath"], "/local.sock");
}

#[test]
fn game_info_reports_toolchain_and_project_config_keys() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
transport:
  kind: mock
tools:
  gdrePath: /baseline/gdre
toolchain:
  dir: /baseline/toolchain
  sharedCacheDir: /baseline/cache
project:
  profilesFile: /baseline/profiles.yaml
"#,
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "info"]);

    assert_eq!(payload["config"]["tools"]["gdrePath"], "/baseline/gdre");
    assert_eq!(payload["config"]["toolchain"]["dir"], "/baseline/toolchain");
    assert_eq!(
        payload["config"]["toolchain"]["sharedCacheDir"],
        "/baseline/cache"
    );
    assert_eq!(
        payload["config"]["project"]["profilesFile"],
        "/baseline/profiles.yaml"
    );
}

#[test]
fn game_info_accepts_legacy_mod_loadout_list_config() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
game:
  modLoadout:
    - spirectlbridge
    - godotexplorer
transport:
  kind: mock
"#,
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "info"]);

    assert_eq!(
        payload["config"]["game"]["modLoadout"]["enabled"],
        serde_json::json!(["spirectlbridge", "godotexplorer"])
    );
    assert_eq!(
        payload["config"]["game"]["modLoadout"]["settingsFile"],
        "auto"
    );
    assert_eq!(payload["config"]["game"]["modLoadout"]["temporary"], true);
    assert_eq!(
        payload["config"]["game"]["modLoadout"]["restoreAfterNoBridgeMs"],
        5000
    );
}

#[test]
fn inspect_viewport_presets_loads_configured_catalogs_relative_to_config_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sample.sts2.viewport-presets.yaml"),
        r#"schemaVersion: spirectl.viewport-presets/v0
presets:
  - name: repo-portrait
    width: 900
    height: 1600
"#,
    )
    .expect("write viewport catalog");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
transport:
  kind: mock
visualValidation:
  presetCatalogs:
    - ./sample.sts2.viewport-presets.yaml
"#,
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "inspect", "viewport-presets"]);
    let presets = payload["presets"].as_array().expect("presets");
    let custom = presets
        .iter()
        .find(|preset| preset["name"] == "repo-portrait")
        .expect("repo-portrait preset");

    assert_eq!(custom["width"], 900);
    assert_eq!(custom["height"], 1600);
    assert_eq!(custom["source"]["kind"], "catalog");
    assert_eq!(
        custom["source"]["path"],
        dir.path()
            .join("sample.sts2.viewport-presets.yaml")
            .display()
            .to_string()
    );
}

#[test]
fn game_detect_uses_local_override_by_default() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = dir.path().join("sts2-game");
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.join("mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        format!("game:\n  path: {}\n", game_dir.display()),
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "game", "detect"]);

    assert_eq!(payload["status"], "configured");
    assert_eq!(
        payload["configuredPath"],
        game_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["detectedPath"], game_dir.to_string_lossy().as_ref());
    assert_eq!(
        payload["liveBridge"]["gamePath"],
        game_dir.to_string_lossy().as_ref()
    );
}

#[test]
fn explicit_config_ignores_sibling_local_overlay() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
game:
  path: /local/game
transport:
  kind: mock
  mockScenario: combat
"#,
    )
    .expect("write local config");
    fs::write(
        dir.path().join("custom.yaml"),
        r#"
game:
  path: /explicit/game
transport:
  kind: mock
  mockScenario: lobby
"#,
    )
    .expect("write explicit config");

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "--config", "custom.yaml", "game", "info"],
    );

    assert_eq!(payload["config"]["game"]["path"], "/explicit/game");
    assert_eq!(payload["config"]["transport"]["mockScenario"], "lobby");
    assert_eq!(payload["config"]["game"]["assembliesDir"], Value::Null);
}

#[test]
fn malformed_local_config_fails_with_local_path() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\n",
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  path: [unterminated\n",
    )
    .expect("write local config");

    let output = run_in_dir(dir.path(), &["--json", "game", "detect"]);
    assert_eq!(output.status.code(), Some(2));

    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(payload["error"]["code"], "config_parse_failed");
    assert_eq!(
        payload["error"]["path"],
        dir.path()
            .join("sts2.local.yaml")
            .to_string_lossy()
            .as_ref()
    );
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_discovers_auto_path_from_linux_steam_metadata() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["configuredPath"], "auto");
    assert_eq!(payload["status"], "detected");
    assert_eq!(payload["detectedPath"], game_dir.to_string_lossy().as_ref());
    assert_eq!(
        payload["liveBridge"]["gamePath"],
        game_dir.to_string_lossy().as_ref()
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
    assert!(
        payload["notes"]
            .as_array()
            .expect("notes")
            .iter()
            .any(|note| note
                .as_str()
                .expect("note text")
                .contains("Cached detected game.path"))
    );
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_falls_back_to_steam_discovery_when_configured_path_is_invalid() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: /missing/sts2\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["configuredPath"], "/missing/sts2");
    assert_eq!(payload["status"], "detected");
    assert_eq!(payload["detectedPath"], game_dir.to_string_lossy().as_ref());
    assert!(
        payload["notes"]
            .as_array()
            .expect("notes")
            .iter()
            .any(|note| note
                .as_str()
                .expect("note text")
                .contains("Configured game.path"))
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_updates_existing_local_config_without_clobbering_other_fields() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");
    let invalid_dir = dir.path().join("not-sts2");
    fs::create_dir_all(&invalid_dir).expect("invalid dir");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: {}\ntransport:\n  kind: mock\n",
            invalid_dir.display()
        ),
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  assembliesDir: null\n  resourcesDir: /local/resources\ntransport:\n  ipcPath: /tmp/local.sock\n",
    )
    .expect("write local config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["status"], "detected");

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["resourcesDir"].as_str(),
        Some("/local/resources")
    );
    assert_eq!(
        local_config["transport"]["ipcPath"].as_str(),
        Some("/tmp/local.sock")
    );
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_preserves_existing_local_config_comments_when_caching_paths() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        concat!(
            "# local roots\n",
            "game:\n",
            "  # keep resource override comment\n",
            "  resourcesDir: /local/resources\n",
            "\n",
            "# transport comment\n",
            "transport:\n",
            "  ipcPath: /tmp/local.sock\n",
        ),
    )
    .expect("write local config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["status"], "detected");

    let rendered = fs::read_to_string(dir.path().join("sts2.local.yaml")).expect("read local");
    assert!(
        rendered.contains("# local roots\n"),
        "rendered:\n{rendered}"
    );
    assert!(
        rendered.contains("  # keep resource override comment\n"),
        "rendered:\n{rendered}"
    );
    assert!(
        rendered.contains("# transport comment\n"),
        "rendered:\n{rendered}"
    );
    assert!(rendered.contains(&format!("  path: {}\n", game_dir.display())));
    assert!(rendered.contains(&format!(
        "  assembliesDir: {}\n",
        game_dir.join("data_sts2_linux_x86_64").display()
    )));
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_preserves_existing_local_assemblies_dir_override() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let invalid_dir = dir.path().join("not-sts2");
    fs::create_dir_all(&invalid_dir).expect("invalid dir");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: {}\ntransport:\n  kind: mock\n",
            invalid_dir.display()
        ),
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  assembliesDir: /local/assemblies\n",
    )
    .expect("write local config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["status"], "detected");

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some("/local/assemblies")
    );
}

#[cfg(target_os = "linux")]
#[test]
fn explicit_config_does_not_mutate_sibling_local_cache_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let _game_dir = create_linux_steam_install(&home_dir);

    fs::write(
        dir.path().join("custom.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write explicit config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  path: /keep/me\ntransport:\n  ipcPath: /tmp/keep.sock\n",
    )
    .expect("write local config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "--config", "custom.yaml", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["status"], "detected");
    assert_eq!(
        fs::read_to_string(dir.path().join("sts2.local.yaml")).expect("read local config"),
        "game:\n  path: /keep/me\ntransport:\n  ipcPath: /tmp/keep.sock\n"
    );
}

#[cfg(all(target_os = "linux", unix))]
#[test]
fn game_detect_reports_cache_write_failures_without_failing_command() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let _game_dir = create_linux_steam_install(&home_dir);

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let original_permissions = fs::metadata(dir.path()).expect("metadata").permissions();
    let mut read_only_permissions = original_permissions.clone();
    read_only_permissions.set_mode(0o555);
    fs::set_permissions(dir.path(), read_only_permissions).expect("chmod temp dir");

    let output = run_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    fs::set_permissions(dir.path(), original_permissions).expect("restore temp dir mode");

    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert!(
        payload["notes"]
            .as_array()
            .expect("notes")
            .iter()
            .any(|note| note
                .as_str()
                .expect("note text")
                .contains("Failed to cache detected local paths"))
    );
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_reports_not_detected_when_auto_finds_no_steam_install() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["configuredPath"], "auto");
    assert_eq!(payload["status"], "not-detected");
    assert_eq!(payload["detectedPath"], Value::Null);
    assert_eq!(payload["liveBridge"]["status"], "needs-config");
}

#[cfg(all(target_os = "linux", unix))]
#[test]
fn game_launch_caches_discovered_game_path_without_unused_assemblies_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    create_deployed_bridge_layout(&game_dir);
    write_executable_script(
        &game_dir.join("SlayTheSpire2"),
        "#!/usr/bin/env bash\nexit 0\n",
    );

    fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: auto\ntransport:\n  kind: ipc\n  ipcPath: {}\n",
            dir.path().join("missing-bridge.sock").display()
        ),
    )
    .expect("write baseline config");

    let _payload = run_error_json_in_dir_with_env(
        dir.path(),
        &[
            "--json",
            "game",
            "launch",
            "--timeout-ms",
            "1",
            "--interval-ms",
            "1",
        ],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert!(local_config["game"]["assembliesDir"].is_null());
}

#[cfg(target_os = "linux")]
#[test]
fn game_install_bridge_caches_discovered_game_and_assemblies_paths_before_build() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: ipc\n",
    )
    .expect("write baseline config");

    let _payload = run_error_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "install-bridge"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
}

#[cfg(target_os = "linux")]
#[test]
fn code_locate_caches_discovered_game_and_derived_assemblies_paths_before_helper_runs() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let _payload = run_error_json_in_dir_with_env(
        dir.path(),
        &["--json", "code", "locate", "type", "Example"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
}

#[cfg(target_os = "linux")]
#[test]
fn scene_search_caches_discovered_game_and_optional_assemblies_paths_before_helper_runs() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let _payload = run_error_json_in_dir_with_env(
        dir.path(),
        &["--json", "code", "scene-search", "Example"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    let local_config = read_yaml_value(&dir.path().join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
    assert_eq!(
        local_config["game"]["assembliesDir"].as_str(),
        Some(assemblies_dir.to_string_lossy().as_ref())
    );
}

#[test]
fn config_discovery_walks_up_to_nearest_config_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: /discovered/game\ntransport:\n  kind: mock\n  mockScenario: combat\n",
    )
    .expect("write baseline config");
    let nested = dir.path().join("nested/deeper");
    fs::create_dir_all(&nested).expect("nested dir");

    let payload = run_json_in_dir(&nested, &["--json", "game", "info"]);

    assert_eq!(payload["config"]["game"]["path"], "/discovered/game");
    assert_eq!(payload["config"]["transport"]["mockScenario"], "combat");
}

#[test]
fn config_discovery_finds_a_local_only_config_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  path: /local-only/game\ntransport:\n  kind: mock\n",
    )
    .expect("write local config");
    let nested = dir.path().join("nested");
    fs::create_dir_all(&nested).expect("nested dir");

    let payload = run_json_in_dir(&nested, &["--json", "game", "info"]);

    assert_eq!(payload["config"]["game"]["path"], "/local-only/game");
}

#[test]
fn config_discovery_stops_at_a_repository_boundary() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: /outside/game\ntransport:\n  kind: mock\n",
    )
    .expect("write outer config");
    let repo = dir.path().join("repo");
    let nested = repo.join("nested");
    fs::create_dir_all(&nested).expect("nested dir");
    fs::create_dir_all(repo.join(".git")).expect("git dir");

    let payload = run_json_in_dir(&nested, &["--json", "game", "detect"]);

    assert_ne!(payload["configuredPath"], "/outside/game");
}

#[test]
fn config_discovery_prefers_a_config_in_the_repository_root() {
    let dir = tempfile::tempdir().expect("temp dir");
    let repo = dir.path().join("repo");
    let nested = repo.join("nested");
    fs::create_dir_all(&nested).expect("nested dir");
    fs::create_dir_all(repo.join(".git")).expect("git dir");
    fs::write(
        repo.join("sts2.config.yaml"),
        "game:\n  path: /repo/game\ntransport:\n  kind: mock\n",
    )
    .expect("write repo config");

    let payload = run_json_in_dir(&nested, &["--json", "game", "info"]);

    assert_eq!(payload["config"]["game"]["path"], "/repo/game");
}

#[test]
fn config_dir_env_overrides_upward_discovery() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: /discovered/game\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");
    let elsewhere = dir.path().join("elsewhere");
    fs::create_dir_all(&elsewhere).expect("elsewhere dir");
    fs::write(
        elsewhere.join("sts2.config.yaml"),
        "game:\n  path: /env/game\ntransport:\n  kind: mock\n",
    )
    .expect("write env config");
    let nested = dir.path().join("nested");
    fs::create_dir_all(&nested).expect("nested dir");

    let payload = run_json_in_dir_with_env(
        &nested,
        &["--json", "game", "info"],
        &[("SPIRECTL_CONFIG_DIR", elsewhere.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["config"]["game"]["path"], "/env/game");
}

#[test]
fn config_dir_env_without_any_config_file_fails() {
    let dir = tempfile::tempdir().expect("temp dir");
    let empty = dir.path().join("empty");
    fs::create_dir_all(&empty).expect("empty dir");

    let payload = run_error_json_in_dir_with_env(
        dir.path(),
        &["--json", "game", "info"],
        &[("SPIRECTL_CONFIG_DIR", empty.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["error"]["code"], "config_dir_missing");
    assert_eq!(payload["error"]["path"], empty.to_string_lossy().as_ref());
}

#[test]
fn explicit_config_beats_the_config_dir_env() {
    let dir = tempfile::tempdir().expect("temp dir");
    let elsewhere = dir.path().join("elsewhere");
    fs::create_dir_all(&elsewhere).expect("elsewhere dir");
    fs::write(
        elsewhere.join("sts2.config.yaml"),
        "game:\n  path: /env/game\ntransport:\n  kind: mock\n",
    )
    .expect("write env config");
    fs::write(
        dir.path().join("custom.yaml"),
        "game:\n  path: /explicit/game\ntransport:\n  kind: mock\n",
    )
    .expect("write explicit config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "--config", "custom.yaml", "game", "info"],
        &[("SPIRECTL_CONFIG_DIR", elsewhere.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["config"]["game"]["path"], "/explicit/game");
}

#[cfg(target_os = "linux")]
#[test]
fn game_detect_caches_into_the_discovered_config_dir_not_the_working_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home_dir = dir.path().join("home");
    fs::create_dir_all(&home_dir).expect("home dir");
    let game_dir = create_linux_steam_install(&home_dir);
    let config_root = dir.path().join("root");
    let nested = config_root.join("nested/deeper");
    fs::create_dir_all(&nested).expect("nested dir");
    fs::write(
        config_root.join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir_with_env(
        &nested,
        &["--json", "game", "detect"],
        &[("HOME", home_dir.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["status"], "detected");
    assert!(
        !nested.join("sts2.local.yaml").exists(),
        "cache must not land in the working directory"
    );
    let local_config = read_yaml_value(&config_root.join("sts2.local.yaml"));
    assert_eq!(
        local_config["game"]["path"].as_str(),
        Some(game_dir.to_string_lossy().as_ref())
    );
}

#[test]
fn config_resolve_reports_absolute_paths_and_sources() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = dir.path().join("sts2-game");
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.join("mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        format!("game:\n  path: {}\n", game_dir.display()),
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "config", "resolve"]);

    assert_eq!(payload["gamePath"], game_dir.to_string_lossy().as_ref());
    assert_eq!(
        payload["assembliesDir"],
        assemblies_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["modsDir"], mods_dir.to_string_lossy().as_ref());
    assert_eq!(payload["sources"]["gamePath"], "local-config");
    assert_eq!(payload["sources"]["assembliesDir"], "derived");
    assert_eq!(payload["sources"]["modsDir"], "derived");
    assert_eq!(payload["configDir"], dir.path().to_string_lossy().as_ref());
    assert_eq!(payload["configDirSource"], "discovered");
    assert_eq!(
        payload["configFiles"],
        serde_json::json!([
            dir.path().join("sts2.config.yaml").to_string_lossy(),
            dir.path().join("sts2.local.yaml").to_string_lossy(),
        ])
    );
    assert_eq!(payload["errors"], serde_json::json!([]));
    for key in ["gamePath", "assembliesDir", "resourcesDir", "modsDir"] {
        assert!(
            Path::new(payload[key].as_str().expect("path string")).is_absolute(),
            "{key} must be absolute"
        );
    }
}

#[test]
fn config_resolve_honours_explicit_config_overrides() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = dir.path().join("sts2-game");
    let assemblies_dir = game_dir.join("data_sts2_linux_x86_64");
    let resources_dir = dir.path().join("resources");
    let mods_dir = dir.path().join("elsewhere-mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");

    fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: {}\n  resourcesDir: {}\n  modsDir: {}\ntransport:\n  kind: mock\nartifacts:\n  dir: /tmp/spirectl-artifacts\n",
            game_dir.display(),
            resources_dir.display(),
            mods_dir.display()
        ),
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "config", "resolve"]);

    assert_eq!(
        payload["resourcesDir"],
        resources_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["modsDir"], mods_dir.to_string_lossy().as_ref());
    assert_eq!(payload["artifactsDir"], "/tmp/spirectl-artifacts");
    assert_eq!(payload["sources"]["gamePath"], "checked-in-config");
    assert_eq!(payload["sources"]["resourcesDir"], "checked-in-config");
    assert_eq!(payload["sources"]["modsDir"], "checked-in-config");
    assert_eq!(payload["sources"]["artifactsDir"], "checked-in-config");
}

#[test]
fn config_resolve_limits_output_to_requested_keys() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\nartifacts:\n  dir: /tmp/spirectl-artifacts\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "config", "resolve", "artifactsDir"]);

    assert_eq!(payload["artifactsDir"], "/tmp/spirectl-artifacts");
    assert!(payload.get("gamePath").is_none());
    assert!(payload["sources"].get("gamePath").is_none());
    assert_eq!(payload["errors"], serde_json::json!([]));
}

#[test]
fn config_resolve_rejects_an_unknown_key() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload =
        run_error_json_in_dir_with_env(dir.path(), &["--json", "config", "resolve", "nope"], &[]);

    assert_eq!(payload["error"]["code"], "unknown_config_key");
    assert_eq!(payload["error"]["key"], "nope");
}

#[test]
fn config_resolve_reports_unresolvable_keys_without_failing() {
    let dir = tempfile::tempdir().expect("temp dir");
    let empty_home = dir.path().join("home");
    fs::create_dir_all(&empty_home).expect("home dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "game:\n  path: auto\ntransport:\n  kind: mock\n",
    )
    .expect("write baseline config");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "config", "resolve"],
        &[("HOME", empty_home.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["gamePath"], Value::Null);
    let errors = payload["errors"].as_array().expect("errors array");
    assert!(
        errors
            .iter()
            .any(|error| error["key"] == "gamePath" || error["key"] == "resourcesDir"),
        "expected an unresolved-path error, got {errors:?}"
    );
}

#[test]
fn config_resolve_from_a_subdirectory_reports_the_discovered_config_dir() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\nartifacts:\n  dir: /tmp/spirectl-artifacts\n",
    )
    .expect("write baseline config");
    let nested = dir.path().join("nested/deeper");
    fs::create_dir_all(&nested).expect("nested dir");

    let payload = run_json_in_dir(&nested, &["--json", "config", "resolve", "artifactsDir"]);

    assert_eq!(payload["configDir"], dir.path().to_string_lossy().as_ref());
    assert_eq!(payload["artifactsDir"], "/tmp/spirectl-artifacts");
}
