#[cfg(unix)]
#[tokio::test]
async fn game_deploy_builds_and_restarts_through_cli_lifecycle_flow() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("MyModProject");
    let build_output = project_root.join("dist/MyMod");
    fs::create_dir_all(&project_root).expect("project root");

    let build_script = write_executable_script(
        workspace.path(),
        "build-mod.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nmkdir -p \"{}\"\nprintf 'built mod\\n' > \"{}\"\n",
            build_output.display(),
            build_output.join("mod.txt").display()
        ),
    );
    let stop_marker = workspace.path().join("stop.txt");
    let stop_script = write_executable_script(
        workspace.path(),
        "stop-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf 'stopped' > \"{}\"\n",
            stop_marker.display()
        ),
    );
    let launch_capture = workspace.path().join("launch.txt");
    let launch_script = write_executable_script(
        workspace.path(),
        "launch-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\npython3 - \"$SPIRECTL_BRIDGE_SOCKET_PATH\" \"$@\" <<'PY' > \"{}\"\nimport json, sys\nprint(json.dumps({{\"socket\": sys.argv[1], \"args\": sys.argv[2:]}}))\nPY\n",
            launch_capture.display()
        ),
    );

    let socket_path = workspace.path().join("spirectl-deploy.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launch_script.to_string_lossy().into_owned()),
            launch_args: vec!["--configured-from-deploy".to_string()],
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            stop_command: vec![stop_script.to_string_lossy().into_owned()],
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/MyMod".to_string()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let project_path = project_root.to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                &project_path,
                "--build",
                "--restart",
                "--verify",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join deploy task");

    assert_eq!(payload["mod"]["name"], "MyMod");
    assert_eq!(payload["verify"]["requested"], true);
    assert_eq!(
        payload["restart"]["launch"]["attachment"]["stateSummary"]["rootScene"],
        "screens/main_menu"
    );
    wait_for_file(&launch_capture);
    let captured_launch: Value =
        serde_json::from_str(&fs::read_to_string(&launch_capture).expect("launch capture"))
            .expect("launch capture json");
    assert_eq!(
        captured_launch["socket"],
        socket_path.to_string_lossy().as_ref()
    );
    assert_eq!(
        captured_launch["args"],
        serde_json::json!(["--configured-from-deploy"])
    );
    assert_eq!(
        fs::read_to_string(&stop_marker).expect("stop marker"),
        "stopped"
    );
    assert!(game_dir.path().join("mods/MyMod/mod.txt").is_file());
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json")
            .is_file()
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.dll")
            .is_file()
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_install_bridge_builds_and_installs_bridge_only() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    let dependent_mod_dir = game_dir.path().join("mods/couchcoop");
    fs::create_dir_all(&dependent_mod_dir).expect("dependent mod dir");
    fs::write(
        dependent_mod_dir.join("couchcoop.json"),
        r#"{
  "id": "couchcoop",
  "name": "CouchCoop",
  "has_pck": false,
  "has_dll": true
}"#,
    )
    .expect("dependent mod manifest");
    fs::write(dependent_mod_dir.join("Spirectl.Sts2.dll"), []).expect("dependent spirectl dll");

    let data_root = workspace.path().join("data");
    let settings_path = data_root
        .join("SlayTheSpire2")
        .join("steam")
        .join("test-user")
        .join("settings.save");
    fs::create_dir_all(settings_path.parent().expect("settings parent")).expect("settings dir");
    fs::write(
        &settings_path,
        r#"{
  "schema_version": 5,
  "mod_settings": {
    "mod_list": [
      {
        "id": "BaseLib",
        "is_enabled": false,
        "source": "mods_directory"
      },
      {
        "id": "couchcoop",
        "is_enabled": true,
        "source": "mods_directory"
      },
      {
        "id": "spirectlbridge",
        "is_enabled": true,
        "source": "mods_directory"
      }
    ],
    "mods_enabled": true
  }
}
"#,
    )
    .expect("settings save");

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &["--json", "--config", &config_path, "game", "install-bridge"],
            &[("PATH", &prefixed_path), ("XDG_DATA_HOME", &xdg_data_home)],
        )
    })
    .await
    .expect("join install-bridge task");

    assert_eq!(
        payload["modDir"],
        game_dir
            .path()
            .join("mods/spirectlbridge")
            .to_string_lossy()
            .as_ref()
    );
    assert_eq!(payload["version"], current_bridge_semver());
    assert_eq!(
        payload["modLoadOrder"]["dependentModIds"],
        serde_json::json!(["couchcoop"])
    );
    assert_eq!(
        payload["modLoadOrder"]["settingsFiles"][0]["status"],
        "updated"
    );
    assert!(
        PathBuf::from(
            payload["modLoadOrder"]["settingsFiles"][0]["backupPath"]
                .as_str()
                .expect("backup path")
        )
        .is_file()
    );
    let updated_settings: Value =
        serde_json::from_str(&fs::read_to_string(&settings_path).expect("updated settings"))
            .expect("updated settings json");
    assert_eq!(
        updated_settings["mod_settings"]["mod_list"],
        serde_json::json!([
            {
                "id": "BaseLib",
                "is_enabled": false,
                "source": "mods_directory"
            },
            {
                "id": "spirectlbridge",
                "is_enabled": true,
                "source": "mods_directory"
            },
            {
                "id": "couchcoop",
                "is_enabled": true,
                "source": "mods_directory"
            }
        ])
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json")
            .is_file()
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.dll")
            .is_file()
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/Spirectl.BridgeMod.Sts2Host.dll")
            .is_file()
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/Spirectl.BridgeMod.dll")
            .is_file()
    );
    assert!(
        !game_dir
            .path()
            .join("mods/spirectlbridge/Microsoft.AspNetCore.Hosting.dll")
            .exists()
    );
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_auto_installs_missing_bridge_before_launch() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-launch-heal.sock");
    let capture_path = game_dir.path().join("launch-heal-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-heal.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    wait_for_file(&capture_path);
    assert_eq!(
        fs::read_to_string(&capture_path).expect("capture env"),
        socket_path.to_string_lossy().as_ref()
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json")
            .is_file()
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_reinstalls_bridge_when_manifest_version_is_stale() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout_with_version(&game_dir.path().join("mods"), "0.0.1");

    let socket_path = game_dir.path().join("spirectl-launch-version.sock");
    let capture_path = game_dir.path().join("launch-version-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-version.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    wait_for_file(&capture_path);
    assert_eq!(
        fs::read_to_string(&capture_path).expect("capture env"),
        socket_path.to_string_lossy().as_ref()
    );
    let manifest = fs::read_to_string(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json"),
    )
    .expect("manifest");
    assert!(manifest.contains(&format!("\"version\": \"{}\"", current_bridge_semver())));
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_repairs_bridge_when_manifest_is_invalid() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    fs::write(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json"),
        "{ not valid json }\n",
    )
    .expect("write invalid manifest");

    let socket_path = game_dir.path().join("spirectl-launch-invalid.sock");
    let capture_path = game_dir.path().join("launch-invalid-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-invalid.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    wait_for_file(&capture_path);
    assert_eq!(
        fs::read_to_string(&capture_path).expect("capture env"),
        socket_path.to_string_lossy().as_ref()
    );
    let manifest = fs::read_to_string(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json"),
    )
    .expect("manifest");
    assert!(manifest.contains(&format!("\"version\": \"{}\"", current_bridge_semver())));
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_repairs_bridge_when_required_host_dll_is_missing() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
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
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let required_path = game_dir
        .path()
        .join("mods/spirectlbridge/Spirectl.BridgeMod.Sts2Host.dll");
    assert!(required_path.exists());
    fs::remove_file(&required_path).expect("remove required host dll");

    let socket_path = game_dir.path().join("spirectl-launch-runtime-heal.sock");
    let capture_path = game_dir.path().join("launch-runtime-heal-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-runtime-heal.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    wait_for_file(&capture_path);
    assert_eq!(
        fs::read_to_string(&capture_path).expect("capture env"),
        socket_path.to_string_lossy().as_ref()
    );
    assert!(required_path.is_file());
    server.abort();
}

/// The `--build` output contract: the build command is told exactly where sts2
/// will read from (absolute), so a build that resolves relative paths against a
/// different root cannot silently disagree with `game.deployOutputSubdir`.
#[cfg(unix)]
#[tokio::test]
async fn game_deploy_build_exports_absolute_output_dir_env() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    write_fake_dotnet(&fake_bin_dir);

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("MyModProject");
    fs::create_dir_all(&project_root).expect("project root");
    let env_capture = workspace.path().join("build-env.txt");
    let build_script = write_executable_script(
        workspace.path(),
        "build-mod-env.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n%s\\n%s\\n' \"$SPIRECTL_DEPLOY_OUTPUT_DIR\" \"$SPIRECTL_DEPLOY_PROJECT_ROOT\" \"$SPIRECTL_DEPLOY_MOD_NAME\" > \"{}\"\nmkdir -p \"$SPIRECTL_DEPLOY_OUTPUT_DIR\"\nprintf 'built\\n' > \"$SPIRECTL_DEPLOY_OUTPUT_DIR/mod.txt\"\n",
            env_capture.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/MyMod".to_string()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("deploy-env.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                // Relative on purpose: the env var must still be absolute.
                "MyModProject",
                "--build",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join deploy task");

    let captured = fs::read_to_string(&env_capture).expect("build env capture");
    let lines = captured.lines().collect::<Vec<_>>();
    let expected_output_dir = project_root.join("dist/MyMod");
    assert_eq!(lines[0], expected_output_dir.to_string_lossy());
    assert!(Path::new(lines[0]).is_absolute());
    assert_eq!(lines[1], project_root.to_string_lossy());
    assert_eq!(lines[2], "MyMod");
    assert_eq!(
        payload["build"]["outputDir"],
        expected_output_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["build"]["freshness"]["fresh"], true);
    assert_eq!(payload["build"]["freshness"]["fileCount"], 1);
    assert_eq!(
        payload["build"]["freshness"]["outputDir"],
        expected_output_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["mod"]["name"], "MyMod");
    assert!(game_dir.path().join("mods/MyMod/mod.txt").is_file());
}

/// Opt-in `{deployOutputDir}` token: a build command that takes the output as
/// an argument needs one spelling, not two.
#[cfg(unix)]
#[tokio::test]
async fn game_deploy_build_substitutes_deploy_output_dir_token() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    write_fake_dotnet(&fake_bin_dir);

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("TokenModProject");
    fs::create_dir_all(&project_root).expect("project root");
    let arg_capture = workspace.path().join("build-arg.txt");
    let build_script = write_executable_script(
        workspace.path(),
        "build-mod-token.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$2\" > \"{}\"\nmkdir -p \"$2\"\nprintf 'built\\n' > \"$2/mod.txt\"\n",
            arg_capture.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            deploy_build_command: vec![
                build_script.to_string_lossy().into_owned(),
                "--output".to_string(),
                "{deployOutputDir}".to_string(),
            ],
            deploy_output_subdir: Some("dist/TokenMod".to_string()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("deploy-token.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let project_path = project_root.to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                &project_path,
                "--build",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join deploy task");

    let expected_output_dir = project_root.join("dist/TokenMod");
    assert_eq!(
        fs::read_to_string(&arg_capture)
            .expect("build arg capture")
            .trim(),
        expected_output_dir.to_string_lossy()
    );
    assert_eq!(
        payload["build"]["command"][2],
        expected_output_dir.to_string_lossy().as_ref()
    );
    assert_eq!(payload["build"]["configuredCommand"][2], "{deployOutputDir}");
    assert_eq!(payload["build"]["freshness"]["fresh"], true);
}

#[cfg(unix)]
#[tokio::test]
async fn game_deploy_build_reports_missing_output_dir() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("MissingOutputProject");
    fs::create_dir_all(&project_root).expect("project root");
    let build_script = write_executable_script(
        workspace.path(),
        "build-mod-nothing.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/MissingMod".to_string()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("deploy-missing.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let project_path = project_root.to_string_lossy().into_owned();
    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                &project_path,
                "--build",
            ],
            &[],
        )
    })
    .await
    .expect("join deploy task");

    assert_eq!(status.code(), Some(4));
    assert_eq!(payload["error"]["code"], "deploy_build_output_missing");
    assert_eq!(
        payload["error"]["expectedOutputDir"],
        project_root.join("dist/MissingMod").to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["error"]["projectRoot"],
        project_root.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["error"]["buildCommand"][0],
        build_script.to_string_lossy().as_ref()
    );
}

#[cfg(unix)]
#[tokio::test]
async fn game_deploy_build_reports_stale_output_until_allow_stale_build() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    write_fake_dotnet(&fake_bin_dir);

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("StaleOutputProject");
    let output_dir = project_root.join("dist/StaleMod");
    fs::create_dir_all(&output_dir).expect("output dir");
    fs::write(output_dir.join("mod.txt"), "stale").expect("stale artifact");
    // Force an mtime well before the build starts: the build below writes
    // nothing, so this file is what the freshness gate sees.
    let touched = Command::new("touch")
        .args(["-d", "@1000000000"])
        .arg(output_dir.join("mod.txt"))
        .status()
        .expect("touch stale artifact");
    assert!(touched.success());

    let build_script = write_executable_script(
        workspace.path(),
        "build-mod-noop.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/StaleMod".to_string()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("deploy-stale.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let project_path = project_root.to_string_lossy().into_owned();
    let stale_workdir = workdir.clone();
    let stale_config_path = config_path.clone();
    let stale_project_path = project_path.clone();
    let stale_path = prefixed_path.clone();
    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            &stale_workdir,
            &[
                "--json",
                "--config",
                &stale_config_path,
                "game",
                "deploy",
                &stale_project_path,
                "--build",
            ],
            &[("PATH", &stale_path)],
        )
    })
    .await
    .expect("join deploy task");

    assert_eq!(status.code(), Some(4));
    assert_eq!(payload["error"]["code"], "deploy_build_output_stale");
    assert_eq!(payload["error"]["freshness"]["fresh"], false);
    assert_eq!(payload["error"]["freshness"]["fileCount"], 1);
    assert!(!game_dir.path().join("mods/StaleMod").exists());

    let allowed = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                &project_path,
                "--build",
                "--allow-stale-build",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join allow-stale deploy task");

    assert_eq!(allowed["build"]["freshness"]["fresh"], false);
    assert_eq!(allowed["build"]["freshness"]["stale"], true);
    assert!(game_dir.path().join("mods/StaleMod/mod.txt").is_file());
}

/// `--no-build` is only meaningful when there is something to stage from;
/// saying so is better than a confusing layout-validation failure later.
#[cfg(unix)]
#[tokio::test]
async fn game_install_bridge_no_build_fails_without_a_previous_publish() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    // Any dotnet invocation is a test failure: --no-build must not build.
    write_executable_script(
        &fake_bin_dir,
        "dotnet",
        "#!/usr/bin/env bash\necho 'dotnet must not be invoked with --no-build' >&2\nexit 17\n",
    );

    let game_dir = create_fake_game_layout();
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let data_root = workspace.path().join("data");
    fs::create_dir_all(&data_root).expect("data root");
    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "install-bridge",
                "--no-build",
            ],
            &[("PATH", &prefixed_path), ("XDG_DATA_HOME", &xdg_data_home)],
        )
    })
    .await
    .expect("join install task");

    assert_eq!(status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "bridge_publish_missing");
    assert!(
        payload["error"]["missing"]
            .as_array()
            .expect("missing list")
            .iter()
            .any(|entry| entry
                .as_str()
                .expect("missing entry")
                .ends_with("Spirectl.BridgeMod.Sts2Host.dll"))
    );
}

/// Reinstalling an unchanged bridge used to wipe and re-copy the mod directory
/// every time. The staged content hash makes the copy conditional — and only
/// the copy: `--no-build` is what skips the build.
#[cfg(unix)]
#[tokio::test]
async fn game_install_bridge_hash_skips_the_copy_until_forced() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    write_fake_dotnet(&fake_bin_dir);

    let game_dir = create_fake_game_layout();
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let data_root = workspace.path().join("data");
    fs::create_dir_all(&data_root).expect("data root");
    let xdg_data_home = data_root.to_string_lossy().into_owned();

    let install = |args: Vec<String>, path: String| {
        let workdir = workdir.clone();
        let config_path = config_path.clone();
        let xdg_data_home = xdg_data_home.clone();
        tokio::task::spawn_blocking(move || {
            let mut argv = vec![
                "--json".to_string(),
                "--config".to_string(),
                config_path,
                "game".to_string(),
                "install-bridge".to_string(),
            ];
            argv.extend(args);
            let argv = argv.iter().map(String::as_str).collect::<Vec<_>>();
            run_json_in_dir_with_env(
                &workdir,
                &argv,
                &[("PATH", &path), ("XDG_DATA_HOME", &xdg_data_home)],
            )
        })
    };

    let first = install(Vec::new(), prefixed_path.clone())
        .await
        .expect("join first install");
    assert_eq!(first["installed"], true);
    assert_eq!(first["reason"], "not_installed");
    let content_hash = first["buildIdentity"]["contentHash"]
        .as_str()
        .expect("content hash")
        .to_string();
    assert!(content_hash.starts_with("sha256:"));

    // A file only this test wrote: it survives a skipped copy and disappears
    // when the mod directory is actually replaced.
    let marker = game_dir.path().join("mods/spirectlbridge/marker.txt");
    fs::write(&marker, "installed once").expect("write marker");

    // Second run with a dotnet that refuses to run: --no-build must not build,
    // and the unchanged hash must not copy.
    let no_build_bin = workspace.path().join("no-build-bin");
    fs::create_dir_all(&no_build_bin).expect("no-build bin dir");
    write_executable_script(
        &no_build_bin,
        "dotnet",
        "#!/usr/bin/env bash\necho 'dotnet must not be invoked with --no-build' >&2\nexit 17\n",
    );
    let no_build_path = format!("{}:{}", no_build_bin.display(), path_value);

    let second = install(vec!["--no-build".to_string()], no_build_path.clone())
        .await
        .expect("join second install");
    assert_eq!(second["installed"], false);
    assert_eq!(second["reason"], "content_unchanged");
    assert_eq!(second["build"]["skipped"], true);
    assert_eq!(second["buildIdentity"]["contentHash"], content_hash.as_str());
    assert!(marker.is_file(), "an unchanged bridge must not be recopied");

    let forced = install(
        vec!["--no-build".to_string(), "--force".to_string()],
        no_build_path,
    )
    .await
    .expect("join forced install");
    assert_eq!(forced["installed"], true);
    assert_eq!(forced["reason"], "forced");
    assert!(
        !marker.exists(),
        "--force must replace the installed mod directory"
    );
    assert!(
        game_dir
            .path()
            .join("mods/spirectlbridge/spirectlbridge.json")
            .is_file()
    );
}

/// `--progress` must never contaminate stdout: the structured result has to
/// stay parseable while the progress lines go to stderr.
#[cfg(unix)]
#[tokio::test]
async fn game_install_bridge_progress_writes_ndjson_to_stderr_only() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    write_fake_dotnet(&fake_bin_dir);

    let game_dir = create_fake_game_layout();
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        ..AppConfig::default()
    });

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let data_root = workspace.path().join("data");
    fs::create_dir_all(&data_root).expect("data root");
    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let output = tokio::task::spawn_blocking(move || {
        run_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--progress",
                "--config",
                &config_path,
                "game",
                "install-bridge",
            ],
            &[("PATH", &prefixed_path), ("XDG_DATA_HOME", &xdg_data_home)],
        )
    })
    .await
    .expect("join install task");

    assert!(output.status.success());
    let payload: Value = serde_json::from_slice(&output.stdout).expect("stdout stays pure json");
    assert_eq!(payload["installed"], true);

    let stderr = String::from_utf8_lossy(&output.stderr);
    let phases = stderr
        .lines()
        .filter_map(|line| serde_json::from_str::<Value>(line).ok())
        .filter(|line| line["progress"] == "game install-bridge")
        .map(|line| line["phase"].as_str().unwrap_or_default().to_string())
        .collect::<Vec<_>>();
    assert!(
        phases.iter().any(|phase| phase == "bridge.build"),
        "expected bridge.build progress, got {phases:?}"
    );
    assert!(phases.iter().any(|phase| phase == "bridge.publish"));
    assert!(phases.iter().any(|phase| phase == "bridge.copy"));
}
