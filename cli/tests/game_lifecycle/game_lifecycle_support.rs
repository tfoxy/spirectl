use std::fs;
use std::os::unix::fs::PermissionsExt;
#[cfg(unix)]
use std::os::unix::net::UnixListener as StdUnixListener;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
#[cfg(unix)]
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::{Duration, Instant};

use serde_json::Value;
use sts2::bridge::{RuntimeBridgeClient, StubBridgeGrpcService, proto};
use sts2::{AppConfig, Cli, GameConfig, MockScenario, RenderedCommand, TransportKind, run_cli};
use tokio::net::{TcpListener, UnixListener};

fn run(args: &[&str]) -> RenderedCommand {
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
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

fn run_in_dir_with_env_and_deadline(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
    timeout: Duration,
) -> (bool, std::process::Output) {
    let mut child = Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .envs(envs.iter().copied())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn sts2 binary");
    let started = Instant::now();
    loop {
        if child.try_wait().expect("poll sts2 binary").is_some() {
            return (
                false,
                child
                    .wait_with_output()
                    .expect("collect sts2 binary output"),
            );
        }
        if started.elapsed() >= timeout {
            let _ = child.kill();
            return (
                true,
                child
                    .wait_with_output()
                    .expect("collect killed sts2 binary output"),
            );
        }
        std::thread::sleep(Duration::from_millis(10));
    }
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

fn run_error_stdout_in_dir_with_env(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
) -> (std::process::ExitStatus, String) {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        !output.status.success(),
        "expected failure, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    (
        output.status,
        String::from_utf8(output.stdout).expect("utf8 stdout"),
    )
}

fn run_output_in_dir_with_env(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
) -> (std::process::ExitStatus, Value) {
    let output = run_in_dir_with_env(workdir, args, envs);
    let json = serde_json::from_slice(&output.stdout).unwrap_or_else(|source| {
        panic!(
            "expected json output ({source}), status {:?}, stdout: {}, stderr: {}",
            output.status.code(),
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )
    });
    (output.status, json)
}

fn write_config(config: &AppConfig) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn create_fake_game_layout() -> tempfile::TempDir {
    let game_dir = tempfile::tempdir().expect("game dir");
    let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.path().join("mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");
    game_dir
}
fn seed_recent_spirectl_log(data_root: &Path, file_name: &str) -> PathBuf {
    let logs_dir = data_root.join("godot/app_userdata/Slay the Spire 2/logs");
    fs::create_dir_all(&logs_dir).expect("logs dir");
    let log_path = logs_dir.join(file_name);
    fs::write(
        &log_path,
        "INFO booting\n[spirectl] live bridge endpoint was not reachable during test\n",
    )
    .expect("write spirectl log");
    log_path
}

fn create_deployed_bridge_layout(mods_dir: &Path) {
    create_deployed_bridge_layout_with_version(mods_dir, current_bridge_semver());
}

/// A deployed payload that claims no game build — the default, so tests that are
/// not about the game-build gate see `compatibility.gameBuild.status: "unknown"`.
fn create_deployed_bridge_layout_with_version(mods_dir: &Path, version: &str) {
    create_deployed_bridge_layout_with_game_build(mods_dir, version, None);
}

/// A deployed payload stamped with the game build it was compiled against, as a
/// source build writes it: `(lane, release_info.json version, main_assembly_hash)`.
fn create_deployed_bridge_layout_with_game_build(
    mods_dir: &Path,
    version: &str,
    game_build: Option<(&str, &str, i64)>,
) {
    let deploy_dir = mods_dir.join("spirectlbridge");
    fs::create_dir_all(&deploy_dir).expect("create bridge deploy dir");
    let (lane, built_against) = match game_build {
        Some((lane, game_version, main_assembly_hash)) => (
            serde_json::Value::String(lane.to_string()),
            serde_json::json!({
                "identitySource": "install-release-info",
                "version": game_version,
                "mainAssemblyHash": main_assembly_hash,
                "referencePackageVersion": serde_json::Value::Null
            }),
        ),
        None => (
            serde_json::Value::Null,
            serde_json::json!({
                "identitySource": "unknown",
                "version": serde_json::Value::Null,
                "mainAssemblyHash": serde_json::Value::Null,
                "referencePackageVersion": serde_json::Value::Null
            }),
        ),
    };
    fs::write(
        deploy_dir.join("spirectlbridge.json"),
        serde_json::to_string_pretty(&serde_json::json!({
            "id": "spirectlbridge",
            "version": version,
            "has_pck": false,
            "has_dll": true,
            "affects_gameplay": false,
            "buildIdentity": {
                "bridgeSemVer": version,
                "bridgeVersion": format!("spirectl-bridge/{version}"),
                "assemblyInformationalVersion": version,
                "sts2ApiLane": lane,
                "builtAgainstGame": built_against,
                "sourceFreshness": {
                    "status": "known",
                    "newestModifiedUnixSeconds": 1,
                    "newestPath": "test"
                }
            }
        }))
        .expect("manifest json"),
    )
    .expect("write manifest");
    fs::write(deploy_dir.join("spirectlbridge.dll"), []).expect("write loader dll");
    fs::write(deploy_dir.join("Spirectl.BridgeMod.Sts2Host.dll"), []).expect("write host dll");
    fs::write(deploy_dir.join("Spirectl.BridgeMod.dll"), []).expect("write bridge dll");
}

/// Give a fake install a `release_info.json`, so the CLI can tell which STS2
/// game build it is. Both real-world builds are in the committed lane table:
/// `v0.107.1` -> `v107` (stable) and `v0.111.0` -> `v111` (public beta).
fn write_game_release_info(game_dir: &Path, version: &str, main_assembly_hash: i64) {
    fs::write(
        game_dir.join("release_info.json"),
        serde_json::to_string_pretty(&serde_json::json!({
            "commit": "0badc0de",
            "version": version,
            "date": "2026-06-18T15:43:56-07:00",
            "branch": version,
            "main_assembly_hash": main_assembly_hash
        }))
        .expect("release info json"),
    )
    .expect("write release_info.json");
}

fn write_settings_save(path: &Path, entries: &[(&str, bool, &str)]) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("settings parent");
    }
    let mod_list = entries
        .iter()
        .map(|(id, enabled, source)| {
            serde_json::json!({
                "id": id,
                "is_enabled": enabled,
                "source": source
            })
        })
        .collect::<Vec<_>>();
    fs::write(
        path,
        serde_json::to_string_pretty(&serde_json::json!({
            "mod_settings": {
                "mods_enabled": true,
                "mod_list": mod_list
            }
        }))
        .expect("settings json"),
    )
    .expect("write settings.save");
}

fn current_bridge_semver() -> &'static str {
    sts2::bridge::bridge_version()
        .strip_prefix("spirectl-bridge/")
        .expect("bridge version should include semver prefix")
}

fn test_bridge_build_identity(built_at_utc: &str) -> proto::BridgeBuildIdentity {
    proto::BridgeBuildIdentity {
        bridge_semver: current_bridge_semver().to_string(),
        bridge_version: sts2::bridge::bridge_version().to_string(),
        assembly_informational_version: current_bridge_semver().to_string(),
        built_at_utc: built_at_utc.to_string(),
        // Claims no game build. Tests about the game-build gate use
        // `test_bridge_build_identity_for_game_build`.
        sts2_api_lane: String::new(),
        built_against_game_version: String::new(),
        built_against_main_assembly_hash: String::new(),
    }
}

/// A live bridge that says which STS2 game build it was compiled for, the way a
/// real source-built payload does. Built in the far future so `stale_live_host`
/// never fires and the game-build arm is the only thing under test.
fn test_bridge_build_identity_for_game_build(
    lane: &str,
    game_version: &str,
    main_assembly_hash: i64,
) -> proto::BridgeBuildIdentity {
    proto::BridgeBuildIdentity {
        sts2_api_lane: lane.to_string(),
        built_against_game_version: game_version.to_string(),
        built_against_main_assembly_hash: main_assembly_hash.to_string(),
        ..test_bridge_build_identity(NEVER_STALE_BUILT_AT_UTC)
    }
}

/// Far enough in the future that no bridge source file can postdate it.
const NEVER_STALE_BUILT_AT_UTC: &str = "4102444800";

fn wait_for_file(path: &Path) {
    for _ in 0..100 {
        if path.is_file()
            && fs::metadata(path)
                .map(|metadata| metadata.len() > 0)
                .unwrap_or(false)
        {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }
}

fn wait_for_json_file(path: &Path, expected: &Value) {
    for _ in 0..100 {
        if let Ok(contents) = fs::read_to_string(path)
            && let Ok(actual) = serde_json::from_str::<Value>(&contents)
            && &actual == expected
        {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }

    let actual = fs::read_to_string(path).unwrap_or_else(|_| "<missing>".to_string());
    panic!(
        "timed out waiting for {} to match expected JSON; actual: {}",
        path.display(),
        actual
    );
}

#[cfg(unix)]
async fn spawn_unresponsive_unix_listener(listener: UnixListener) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        let (_stream, _) = listener.accept().await.expect("accept unix client");
        tokio::time::sleep(Duration::from_secs(5)).await;
    })
}

#[cfg(unix)]
fn spawn_reachable_unix_listener_until(
    socket_path: &Path,
    stop: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    let listener = StdUnixListener::bind(socket_path).expect("bind reachable unix listener");
    listener
        .set_nonblocking(true)
        .expect("set reachable listener nonblocking");
    std::thread::spawn(move || {
        let mut held_connections = Vec::new();
        while !stop.load(Ordering::SeqCst) {
            match listener.accept() {
                Ok((stream, _)) => held_connections.push(stream),
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(5));
                }
                Err(_) => break,
            }
        }
    })
}

#[cfg(unix)]
fn write_executable_script(dir: &Path, name: &str, contents: &str) -> PathBuf {
    let path = dir.join(name);
    fs::write(&path, contents).expect("write script");
    let mut permissions = fs::metadata(&path).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&path, permissions).expect("chmod script");
    path
}

#[cfg(unix)]
fn current_process_field(field: &str) -> String {
    let output = Command::new("ps")
        .args(["-o", &format!("{field}="), "-p"])
        .arg(std::process::id().to_string())
        .output()
        .expect("query current process field");
    assert!(
        output.status.success(),
        "ps failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8_lossy(&output.stdout).trim().to_string()
}

#[cfg(target_os = "linux")]
fn spawn_launcher_with_retry(path: &Path, args: &[&str]) -> std::process::Child {
    for _ in 0..20 {
        match Command::new(path).args(args).spawn() {
            Ok(child) => return child,
            Err(error) if error.raw_os_error() == Some(26) => {
                std::thread::sleep(Duration::from_millis(10));
            }
            Err(error) => panic!("spawn launcher: {error}"),
        }
    }

    Command::new(path)
        .args(args)
        .spawn()
        .expect("spawn launcher")
}

#[cfg(target_os = "linux")]
fn assert_process_exited(pid: u32) {
    let proc_dir = PathBuf::from(format!("/proc/{pid}"));
    for _ in 0..100 {
        if !proc_dir.exists() {
            return;
        }
        if let Ok(stat) = fs::read_to_string(proc_dir.join("stat"))
            && stat.split_whitespace().nth(2) == Some("Z")
        {
            return;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    panic!("process {pid} was still present after launch returned");
}

/// Minimal `dotnet` stand-in on PATH: `build` drops the loader assemblies where
/// the deploy staging expects them, `publish` drops the host assemblies into
/// `--output`. Lets bridge deploy run with no .NET toolchain.
#[cfg(unix)]
fn write_fake_dotnet(bin_dir: &Path) -> PathBuf {
    write_executable_script(
        bin_dir,
        "dotnet",
        r#"#!/usr/bin/env bash
set -euo pipefail
command_name="${1:-}"
shift || true

if [ "$command_name" = "build" ]; then
  project="${1:?missing build project}"
  shift
  configuration="Debug"
  while [ $# -gt 0 ]; do
    case "$1" in
      --configuration)
        configuration="${2:?missing configuration}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  output_dir="$(dirname "$project")/bin/$configuration/net9.0"
  mkdir -p "$output_dir"
  : > "$output_dir/spirectlbridge.dll"
  : > "$output_dir/spirectlbridge.pdb"
  exit 0
fi

if [ "$command_name" = "publish" ]; then
  project="${1:?missing publish project}"
  shift
  output_dir=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --output)
        output_dir="${2:?missing output dir}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  mkdir -p "$output_dir"
  : > "$output_dir/Spirectl.BridgeMod.Sts2Host.dll"
  : > "$output_dir/Spirectl.BridgeMod.dll"
  exit 0
fi

echo "unexpected dotnet command: $command_name" >&2
exit 1
"#,
    )
}
