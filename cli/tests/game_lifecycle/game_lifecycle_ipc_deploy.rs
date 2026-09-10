#[cfg(unix)]
#[tokio::test]
async fn game_launch_exited_before_ipc_uses_human_error_output_without_json() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-human-exit.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 17\n",
    );

    let socket_path = game_dir.path().join("spirectl-launch-human-exit.sock");
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

    let workdir = game_dir.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let (status, stdout) = tokio::task::spawn_blocking(move || {
        run_error_stdout_in_dir_with_env(
            &workdir,
            &[
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "100",
                "--interval-ms",
                "10",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(status.code(), Some(4));
    assert!(stdout.starts_with("error: game exited before the bridge was ready\n"));
    assert!(stdout.contains("Run with --json for full diagnostics."));
    assert!(stdout.contains("  code: launch_exited_before_ipc\n"));
    assert!(stdout.contains("  exitStatus: 17\n"));
    assert!(!stdout.starts_with("error:\n"));
    assert!(!stdout.contains("lastError"));
    assert!(!stdout.contains("recentLogs"));
}
#[tokio::test]
async fn game_launch_exited_before_ipc_reports_when_spawned_process_exits_before_ipc_is_ready() {
    let game_dir = create_fake_game_layout();
    let data_root = game_dir.path().join("xdg-data");
    let log_path = seed_recent_spirectl_log(&data_root, "godot-launch-exit.log");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-exit.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 17\n",
    );

    let socket_path = game_dir.path().join("spirectl-launch-exit.sock");
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

    let workdir = game_dir.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "100",
                "--interval-ms",
                "10",
            ],
            &[("XDG_DATA_HOME", &xdg_data_home)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["error"]["code"], "launch_exited_before_ipc");
    assert_eq!(
        payload["error"]["socketPath"],
        socket_path.to_string_lossy().as_ref()
    );
    assert_eq!(payload["error"]["exitStatus"], 17);
    assert_eq!(
        payload["error"]["logPath"],
        log_path.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["error"]["latestLog"]["logPath"],
        log_path.to_string_lossy().as_ref()
    );
    let safe_next = payload["error"]["safeNextCommands"]
        .as_array()
        .expect("safe next commands");
    assert!(
        safe_next
            .iter()
            .any(|command| command == "sts2 game bridge-health --repair-stale-endpoint")
    );
    assert!(
        safe_next
            .iter()
            .any(|command| command == "sts2 game attach")
    );
    assert!(
        safe_next
            .iter()
            .any(|command| command == "sts2 game launch")
    );
    assert!(
        safe_next
            .iter()
            .any(|command| command == "sts2 --json game bridge-health")
    );
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_reports_when_spawned_process_exits_after_attach_stability_check() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir
        .path()
        .join("spirectl-launch-exit-after-attach.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-exit-after-attach.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nsleep 0.03\nexit 17\n",
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

    let workdir = game_dir.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
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
                "10",
                "--verify-stable-ms",
                "100",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["error"]["code"], "launch_exited_after_attach");
    assert_eq!(
        payload["error"]["socketPath"],
        socket_path.to_string_lossy().as_ref()
    );
    assert_eq!(payload["error"]["exitStatus"], 17);
    server.abort();
}

#[cfg(all(not(target_os = "linux"), unix))]
#[tokio::test]
async fn game_deploy_restart_reports_missing_stop_command_guidance() {
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
    fs::create_dir_all(&project_root).expect("project root");
    fs::write(project_root.join("mod.txt"), "mod").expect("project file");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let launch_script = write_executable_script(
        workspace.path(),
        "launch-game.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launch_script.to_string_lossy().into_owned()),
            launch_working_dir: Some(workspace.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("deploy.sock")
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
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "deploy",
                &project_path,
                "--restart",
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join deploy task");

    assert_eq!(payload["error"]["code"], "invalid_lifecycle_config");
    assert_eq!(payload["error"]["configKey"], "game.stopCommand");
    assert_eq!(payload["error"]["command"], "game deploy");
    assert_eq!(
        payload["error"]["message"],
        "automatic restart on macOS requires game.stopCommand; set it to stop the running game before redeploying."
    );
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_uses_launch_timeout_code_when_process_stays_running_without_ipc() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-sleep.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nsleep 1\n",
    );

    let socket_path = game_dir.path().join("spirectl-launch-timeout.sock");
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

    let workdir = game_dir.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "50",
                "--interval-ms",
                "10",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["error"]["code"], "launch_timeout");
    assert_eq!(
        payload["error"]["socketPath"],
        socket_path.to_string_lossy().as_ref()
    );
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn game_launch_reports_wrapper_alive_without_game_process_and_terminates_wrapper() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let wrapper_pid_path = game_dir.path().join("wrapper.pid");
    let wrapper = write_executable_script(
        game_dir.path(),
        "fake-wrapper-never-launches-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\necho $$ > {}\necho wrapper stayed alive >&2\ntrap 'exit 0' TERM INT\nwhile :; do sleep 0.1; done\n",
            wrapper_pid_path.display()
        ),
    );
    let game_executable = write_executable_script(
        game_dir.path(),
        "fake-sts2-game.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nsleep 1\n",
    );

    let socket_path = game_dir
        .path()
        .join("spirectl-wrapper-game-not-started.sock");
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(game_executable.to_string_lossy().into_owned()),
            launch_wrapper: vec![
                wrapper.to_string_lossy().into_owned(),
                "--wrapper-flag".to_string(),
            ],
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

    let workdir = game_dir.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "launch",
                "--timeout-ms",
                "80",
                "--interval-ms",
                "10",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["error"]["code"], "launch_game_process_not_started");
    assert_eq!(payload["error"]["wrapperCommand"], wrapper.to_string_lossy().as_ref());
    assert_eq!(payload["error"]["wrapperArgs"], serde_json::json!(["--wrapper-flag"]));
    assert_eq!(
        payload["error"]["gameExecutable"],
        game_executable.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["error"]["socketPath"],
        socket_path.to_string_lossy().as_ref()
    );
    assert_eq!(payload["error"]["gameProcessObserved"], false);
    assert_eq!(payload["error"]["matchedGamePids"], serde_json::json!([]));
    assert_eq!(
        payload["error"]["spawnPid"].as_u64(),
        payload["error"]["process"]["pid"].as_u64()
    );

    let wrapper_pid = fs::read_to_string(&wrapper_pid_path)
        .expect("wrapper pid file")
        .trim()
        .parse::<u32>()
        .expect("wrapper pid");
    assert_eq!(
        payload["error"]["spawnPid"].as_u64(),
        Some(u64::from(wrapper_pid))
    );
    assert!(
        payload["error"]["wrapperLogs"]["stderr"]["entries"]
            .as_array()
            .expect("stderr entries")
            .iter()
            .any(|entry| entry == "wrapper stayed alive")
    );
    assert_process_exited(wrapper_pid);
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn game_launch_wrapper_timeout_retries_once_and_surfaces_first_failure() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let attempt_path = game_dir.path().join("wrapper-attempt.txt");
    let game_executable = game_dir.path().join("fake-retry-sleep");
    fs::copy("/bin/sleep", &game_executable).expect("copy sleep");
    let mut permissions = fs::metadata(&game_executable)
        .expect("metadata")
        .permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&game_executable, permissions).expect("chmod sleep");
    let wrapper = write_executable_script(
        game_dir.path(),
        "fake-wrapper-retries.sh",
        &format!(
            r#"#!/usr/bin/env bash
set -euo pipefail
attempt_file="{}"
attempt=0
if [ -f "$attempt_file" ]; then
  attempt="$(cat "$attempt_file")"
fi
attempt=$((attempt + 1))
printf '%s' "$attempt" > "$attempt_file"
printf 'wrapper attempt %s\n' "$attempt" >&2
if [ "$attempt" = "1" ]; then
  "$@" &
  child=$!
  trap 'kill "$child" 2>/dev/null || true; exit 0' TERM INT
  wait "$child" || true
  while :; do sleep 0.1; done
fi
exec "$@"
"#,
            attempt_path.display()
        ),
    );
    let socket_path = game_dir.path().join("spirectl-wrapper-retry.sock");
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(game_executable.to_string_lossy().into_owned()),
            launch_wrapper: vec![wrapper.to_string_lossy().into_owned()],
            launch_args: vec!["30".to_string()],
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

    let socket_for_server = socket_path.clone();
    let attempt_for_server = attempt_path.clone();
    let stop_flag = Arc::new(AtomicBool::new(false));
    let stop_for_server = stop_flag.clone();
    let server_thread = std::thread::spawn(move || {
        wait_for_file(&attempt_for_server);
        // Hold the socket back until well past attempt #1's launch timeout so the
        // first attempt deterministically times out (the retryable code) even when
        // suite load delays scheduling; attempt #2 (~8s later) then finds it bound.
        std::thread::sleep(Duration::from_millis(2_000));
        let listener =
            StdUnixListener::bind(&socket_for_server).expect("bind retry bridge listener");
        listener
            .set_nonblocking(true)
            .expect("set listener nonblocking");
        let runtime = tokio::runtime::Runtime::new().expect("tokio runtime");
        runtime.block_on(async move {
            let listener = UnixListener::from_std(listener).expect("tokio listener");
            let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
            let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
            // Stay available until the test signals completion, with a generous
            // safety cap so a hung run still tears down. Decoupling the server
            // lifetime from the 8s wrapper-retry delay + cleanup keeps attempt #2
            // from racing a fixed server-abort window under full-suite load.
            let deadline = Instant::now() + Duration::from_secs(60);
            while !stop_for_server.load(Ordering::SeqCst) && Instant::now() < deadline {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            server.abort();
        });
    });

    let workdir = game_dir.path().to_path_buf();
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
                // A generous attempt window so the backgrounded fake game process is
                // reliably observed within attempt #1, yielding the retryable
                // `launch_timeout` (not the non-retryable
                // `launch_game_process_not_started`) under full-suite load.
                "--timeout-ms",
                "1000",
                "--interval-ms",
                "10",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["launch"]["status"], "spawned");
    assert_eq!(payload["launch"]["retry"]["attempted"], true);
    assert_eq!(
        payload["launch"]["retry"]["firstErrorCode"],
        "launch_timeout"
    );
    assert_eq!(fs::read_to_string(&attempt_path).expect("attempt"), "2");
    assert_eq!(
        payload["launch"]["wrapperLogs"]["stderrPath"]
            .as_str()
            .is_some_and(|path| path.ends_with(".stderr.log")),
        true
    );

    if let Some(game_pid) = payload["launch"]["matchedGamePids"]
        .as_array()
        .and_then(|pids| pids.first())
        .and_then(Value::as_u64)
    {
        let _ = Command::new("kill")
            .arg("-TERM")
            .arg(game_pid.to_string())
            .status();
    }
    stop_flag.store(true, Ordering::SeqCst);
    server_thread.join().expect("retry server thread");
}

#[cfg(unix)]
#[tokio::test]
async fn game_attach_reports_rpc_timeout_when_socket_accepts_without_response() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    let socket_path = workspace.path().join("accepted-no-response.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server = tokio::spawn(async move {
        let (_stream, _) = listener.accept().await.expect("accept bridge client");
        tokio::time::sleep(Duration::from_millis(250)).await;
    });

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "attach",
                "--timeout-ms",
                "1000",
                "--interval-ms",
                "10",
                "--rpc-timeout-ms",
                "50",
            ],
            &[],
        )
    })
    .await
    .expect("join attach task");

    assert_eq!(payload["error"]["code"], "bridge_rpc_timeout");
    assert_eq!(payload["error"]["details"][1]["value"], "game.info");
    assert_eq!(payload["error"]["details"][2]["value"], "50");
    server.await.expect("server task");
}

#[cfg(unix)]
#[test]
fn live_ipc_reports_missing_socket_when_no_socket_file_exists() {
    let workspace = tempfile::tempdir().expect("workspace");
    let socket_path = workspace.path().join("missing.sock");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_error_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8"),
            "state",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "ipc_socket_missing");
}

#[cfg(unix)]
#[test]
fn live_ipc_reports_connection_failure_when_socket_path_exists_but_is_not_listening() {
    let workspace = tempfile::tempdir().expect("workspace");
    let socket_path = workspace.path().join("stale.sock");
    fs::write(&socket_path, []).expect("stale socket placeholder");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_error_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8"),
            "state",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "ipc_connection_failed");
}

#[cfg(unix)]
#[test]
fn live_ipc_surfaces_recent_bootstrap_failure_from_godot_log() {
    let workspace = tempfile::tempdir().expect("workspace");
    let data_root = workspace.path().join("xdg-data");
    let logs_dir = data_root.join("godot/app_userdata/Slay the Spire 2/logs");
    fs::create_dir_all(&logs_dir).expect("logs dir");
    let log_path = logs_dir.join("godot.log");
    fs::write(
        &log_path,
        concat!(
            "INFO booting\n",
            "[spirectl] bootstrap loader failed: missing Spirectl.BridgeMod.Sts2Host.dll\n",
            "[spirectl] Live bridge host failed: ModManager is not finished initializing!\n"
        ),
    )
    .expect("write log");

    let socket_path = workspace.path().join("missing.sock");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let payload = run_error_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8"),
            "state",
        ],
        &[("XDG_DATA_HOME", &xdg_data_home)],
    );

    assert_eq!(payload["error"]["code"], "bridge_bootstrap_failed");
    let details = payload["error"]["details"].as_array().expect("details");
    assert!(
        details.iter().any(|detail| {
            detail["field"] == "logPath" && detail["value"] == log_path.to_string_lossy().as_ref()
        }),
        "expected logPath detail, got {details:?}"
    );
}

#[cfg(unix)]
#[test]
fn live_ipc_ignores_undecodable_recent_log_when_older_recent_log_contains_bootstrap_failure() {
    let workspace = tempfile::tempdir().expect("workspace");
    let data_root = workspace.path().join("xdg-data");
    let logs_dir = data_root.join("godot/app_userdata/Slay the Spire 2/logs");
    fs::create_dir_all(&logs_dir).expect("logs dir");

    let readable_log_path = logs_dir.join("godot-readable.log");
    fs::write(
        &readable_log_path,
        "INFO booting\n[spirectl] Live IPC bridge host failed: missing managed runtime\n",
    )
    .expect("write readable log");

    std::thread::sleep(std::time::Duration::from_millis(20));
    let undecodable_log_path = logs_dir.join("godot-undecodable.log");
    fs::write(&undecodable_log_path, [0xFF, 0xFE, 0xFD]).expect("write undecodable log");

    let socket_path = workspace.path().join("missing.sock");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let xdg_data_home = data_root.to_string_lossy().into_owned();
    let payload = run_error_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8"),
            "state",
        ],
        &[("XDG_DATA_HOME", &xdg_data_home)],
    );

    assert_eq!(payload["error"]["code"], "bridge_bootstrap_failed");
    let details = payload["error"]["details"].as_array().expect("details");
    assert!(
        details.iter().any(|detail| {
            detail["field"] == "logPath"
                && detail["value"] == readable_log_path.to_string_lossy().as_ref()
        }),
        "expected readable logPath detail, got {details:?}"
    );
}
