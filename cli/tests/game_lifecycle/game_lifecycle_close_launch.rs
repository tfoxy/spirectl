#[cfg(unix)]
#[tokio::test]
async fn game_close_client_round_trips_over_ipc_bridge() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-close.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    let client = RuntimeBridgeClient::from_config(&sts2::TransportConfig {
        kind: TransportKind::Ipc,
        ipc_path: Some(socket_path.to_string_lossy().into_owned()),
        ..sts2::TransportConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        client.close_game(proto::GameCloseRequest {
            request_id: "close-ipc".to_string(),
        })
    })
    .await
    .expect("join close client task")
    .expect("close game response");

    assert_eq!(response.request_id, "close-ipc");
    assert!(response.accepted);
    assert_eq!(response.notices, ["Requested mock game close."]);
    server.abort();
}
#[test]
fn game_close_runs_configured_stop_command_after_bridge_unavailable() {
    let game_dir = create_fake_game_layout();
    let marker = game_dir.path().join("stopped.txt");
    let stop_script = write_executable_script(
        game_dir.path(),
        "stop-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf stopped > '{}'\n",
            marker.display()
        ),
    );
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            stop_command: vec![stop_script.to_string_lossy().into_owned()],
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("missing.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "close",
            "--timeout-ms",
            "100",
            "--interval-ms",
            "5",
        ],
        &[],
    );

    assert_eq!(payload["command"], "game close");
    assert_eq!(payload["strategy"], "configured-command");
    assert_eq!(payload["success"], true);
    assert_eq!(fs::read_to_string(marker).expect("marker"), "stopped");
}

#[cfg(unix)]
#[test]
fn game_close_bounds_nonresponsive_bridge_rpc_and_falls_back_to_stop_command() {
    let game_dir = create_fake_game_layout();
    let marker = game_dir.path().join("stopped.txt");
    let socket_path = game_dir.path().join("stuck-bridge.sock");
    let stop_script = write_executable_script(
        game_dir.path(),
        "stop-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nrm -f '{}'\nprintf stopped > '{}'\n",
            socket_path.display(),
            marker.display(),
        ),
    );
    let listener = match StdUnixListener::bind(&socket_path) {
        Ok(listener) => listener,
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
            eprintln!(
                "skipping nonresponsive bridge timeout assertion because this environment blocks Unix socket binding: {error}"
            );
            return;
        }
        Err(error) => panic!("bind socket: {error}"),
    };
    listener
        .set_nonblocking(true)
        .expect("set listener nonblocking");
    let stop_server = Arc::new(AtomicBool::new(false));
    let server_stop = Arc::clone(&stop_server);
    let server = std::thread::spawn(move || {
        let mut held_connections = Vec::new();
        while !server_stop.load(Ordering::SeqCst) {
            match listener.accept() {
                Ok((stream, _)) => held_connections.push(stream),
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(10));
                }
                Err(_) => break,
            }
        }
    });
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            stop_command: vec![stop_script.to_string_lossy().into_owned()],
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let started = Instant::now();
    let (timed_out, output) = run_in_dir_with_env_and_deadline(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "close",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
            "--rpc-timeout-ms",
            "75",
        ],
        &[],
        Duration::from_secs(3),
    );
    stop_server.store(true, Ordering::SeqCst);
    server.join().expect("join stuck bridge server");

    assert!(
        !timed_out,
        "game close did not honor RPC/lifecycle timeout; stdout: {}, stderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "game close took too long: {:?}",
        started.elapsed()
    );
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");

    assert_eq!(payload["command"], "game close");
    assert_eq!(payload["strategy"], "configured-command");
    assert_eq!(payload["success"], true);
    assert_eq!(fs::read_to_string(marker).expect("marker"), "stopped");
    let notices = payload["notices"].as_array().expect("notices array");
    assert!(notices.iter().any(|notice| {
        notice["code"] == "bridge-close-request-failed"
            && notice["bridgeErrorCode"] == "bridge_rpc_timeout"
            && notice["rpcTimeoutMs"] == 75
    }));
}

#[cfg(all(unix, target_os = "linux"))]
#[test]
fn game_close_cleans_stale_socket_when_no_process_is_running() {
    let game_dir = create_fake_game_layout();
    let launcher = write_executable_script(game_dir.path(), "fake-sts2", "#!/usr/bin/env bash\n");
    let socket_path = game_dir.path().join("stale.sock");
    let listener = match StdUnixListener::bind(&socket_path) {
        Ok(listener) => listener,
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
            eprintln!(
                "skipping stale socket cleanup assertion because this environment blocks Unix socket binding: {error}"
            );
            return;
        }
        Err(error) => panic!("bind socket: {error}"),
    };
    drop(listener);

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "close",
            "--timeout-ms",
            "100",
            "--interval-ms",
            "5",
        ],
        &[],
    );

    assert_eq!(payload["command"], "game close");
    assert_eq!(payload["strategy"], "platform-graceful");
    assert_eq!(payload["success"], true);
    assert_eq!(payload["bridgeEndpointDisappeared"], true);
    assert!(!socket_path.exists(), "stale socket should be removed");
    assert!(payload["notices"].as_array().unwrap().iter().any(|notice| {
        notice["code"] == "stale-endpoint-cleanup"
            && notice["cleanup"]["removed"] == true
            && notice["cleanup"]["reason"] == "removed"
    }));
}

#[cfg(target_os = "linux")]
#[test]
fn game_kill_terminates_exact_configured_launch_process() {
    let game_dir = create_fake_game_layout();
    let launcher = game_dir.path().join("fake-sts2");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod");
    let mut child = spawn_launcher_with_retry(&launcher, &["30"]);
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("missing.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "kill",
            "--timeout-ms",
            "1000",
            "--interval-ms",
            "10",
        ],
        &[],
    );

    assert_eq!(payload["command"], "game kill");
    assert_eq!(payload["strategy"], "platform-force");
    assert_eq!(payload["success"], true);
    assert_eq!(payload["matchedPids"], serde_json::json!([child.id()]));
    let _ = child.wait();
}

#[tokio::test]
async fn game_attach_waits_for_tcp_and_returns_endpoint_summary() {
    let game_dir = create_fake_game_layout();
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_tcp_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Tcp,
            tcp_address: Some(address.to_string()),
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
            "game",
            "attach",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join attach task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["endpoint"]["kind"], "tcp");
    assert_eq!(payload["endpoint"]["address"], address.to_string());
    assert_eq!(payload["stateSummary"]["rootScene"], "screens/main_menu");
    assert_eq!(payload["gameInfo"]["transport"]["configuredKind"], "tcp");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_uses_configured_command_and_injects_socket_env() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir.path().join("spirectl-launch.sock");
    let capture_path = game_dir.path().join("launch-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch.sh",
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

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join launch task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    wait_for_file(&capture_path);
    assert_eq!(
        fs::read_to_string(&capture_path).expect("capture env"),
        socket_path.to_string_lossy().as_ref()
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_temporary_mod_loadout_restores_after_game_exit_save() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    fs::create_dir_all(game_dir.path().join("mods/BaseLib")).expect("BaseLib dir");
    fs::create_dir_all(game_dir.path().join("mods/godotexplorer")).expect("godotexplorer dir");

    let data_root = workspace.path().join("data");
    let settings_path = data_root
        .join("SlayTheSpire2")
        .join("steam")
        .join("test-user")
        .join("settings.save");
    write_settings_save(
        &settings_path,
        &[
            ("BaseLib", true, "mods_directory"),
            ("godotexplorer", true, "mods_directory"),
            ("spirectlbridge", false, "mods_directory"),
        ],
    );
    let original_settings: Value =
        serde_json::from_str(&fs::read_to_string(&settings_path).expect("settings"))
            .expect("settings json");

    let socket_path = game_dir.path().join("spirectl-launch-loadout-restore.sock");
    let marker_path = workspace.path().join("game-exit-save.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let restricted_settings = serde_json::to_string_pretty(&serde_json::json!({
        "mod_settings": {
            "mods_enabled": true,
            "mod_list": [
                {"id": "BaseLib", "is_enabled": false, "source": "mods_directory"},
                {"id": "godotexplorer", "is_enabled": true, "source": "mods_directory"},
                {"id": "spirectlbridge", "is_enabled": true, "source": "mods_directory"}
            ]
        }
    }))
    .expect("restricted settings json");
    let launcher = write_executable_script(
        workspace.path(),
        "fake-launch-save-loadout-on-exit.sh",
        &format!(
            r#"#!/usr/bin/env bash
set -euo pipefail
sleep 0.5
cat > "{}" <<'JSON'
{}
JSON
printf done > "{}"
"#,
            settings_path.display(),
            restricted_settings,
            marker_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(workspace.path().to_string_lossy().into_owned()),
            mod_loadout: sts2::GameModLoadoutConfig {
                enabled: Some(vec![
                    "spirectlbridge".to_string(),
                    "godotexplorer".to_string(),
                ]),
                temporary: true,
                ..sts2::GameModLoadoutConfig::default()
            },
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = workspace.path().to_path_buf();
    let xdg_data_home = data_root.to_string_lossy().into_owned();
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
            &[("XDG_DATA_HOME", &xdg_data_home)],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["modLoadout"]["restore"]["status"], "restored");
    assert_eq!(
        payload["modLoadout"]["postExitRestore"]["status"],
        "scheduled"
    );
    wait_for_file(&marker_path);
    wait_for_json_file(&settings_path, &original_settings);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_default_output_summarizes_attachment() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir.path().join("spirectl-launch-summary.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-summary.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\n",
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

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join launch task");

    assert_eq!(response.exit_code, 0);
    assert!(response.stdout.contains("status: attached"));
    assert!(response.stdout.contains("state:"));
    assert!(response.stdout.contains("rootScene: screens/main_menu"));
    assert!(response.stdout.contains("bridgeVersion:"));
    assert!(response.stdout.contains("bridgeBuiltAtUtc:"));
    assert!(response.stdout.contains("Run with --json"));
    assert!(!response.stdout.contains("gameInfo:"));
    assert!(!response.stdout.contains("supportedActions:"));
    assert!(!response.stdout.contains("capabilities:"));
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_creates_missing_steam_appid_file_for_steam_install() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_path = workspace
        .path()
        .join("SteamLibrary/steamapps/common/Slay the Spire 2");
    let assemblies_dir = game_path.join("data_sts2_linux_x86_64");
    let mods_dir = game_path.join("mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");
    create_deployed_bridge_layout(&mods_dir);

    let steam_appid_path = game_path.join("steam_appid.txt");
    assert!(!steam_appid_path.exists());

    let socket_path = game_path.join("spirectl-launch-steam-appid.sock");
    let capture_path = game_path.join("launch-steam-appid-env.txt");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        &game_path,
        "fake-launch-steam-appid.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_path.to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_path.to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
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
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join launch task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["attachment"]["stateSummary"]["rootScene"], "screens/main_menu");
    assert_eq!(payload["launch"]["steamAppId"]["status"], "created");
    assert_eq!(payload["launch"]["steamAppId"]["appId"], "2868840");
    assert_eq!(
        fs::read_to_string(&steam_appid_path).expect("steam appid"),
        "2868840\n"
    );
    assert!(
        payload["launch"]["diagnostics"]["steam"]["detectionBasis"]
            .as_array()
            .expect("detection basis")
            .iter()
            .any(|value| value == "steam_appid.txt")
    );
    wait_for_file(&capture_path);
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_appends_cli_passthrough_args_after_configured_args() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir.path().join("spirectl-launch-args.sock");
    let capture_path = game_dir.path().join("launch-args.json");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-args.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\npython3 - \"$@\" <<'PY' > \"{}\"\nimport json, sys\nprint(json.dumps(sys.argv[1:]))\nPY\n",
            capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec!["--configured".to_string(), "value".to_string()],
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

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
            "--",
            "--headless",
            "-fastmp",
        ])
    })
    .await
    .expect("join launch task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(
        payload["launch"]["args"],
        serde_json::json!(["--configured", "value", "--headless", "-fastmp"])
    );
    wait_for_file(&capture_path);
    let captured: Value =
        serde_json::from_str(&fs::read_to_string(&capture_path).expect("capture args"))
            .expect("args json");
    assert_eq!(
        captured,
        serde_json::json!(["--configured", "value", "--headless", "-fastmp"])
    );
    server.abort();
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn game_launch_stops_existing_process_before_spawning() {
    let game_dir = create_fake_game_layout();

    let launcher = game_dir.path().join("fake-explicit-relaunch");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod launcher");
    let mut existing = spawn_launcher_with_retry(&launcher, &["30"]);

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec!["0".to_string()],
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            mod_loadout: sts2::GameModLoadoutConfig {
                enabled: Some(Vec::new()),
                ..sts2::GameModLoadoutConfig::default()
            },
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("explicit-relaunch-missing.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = game_dir.path().to_path_buf();
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
            &[],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(payload["launch"]["status"], "spawned");
    assert_eq!(payload["launch"]["spawned"], true);
    assert_eq!(payload["preLaunchStop"]["required"], true);
    assert!(
        payload["preLaunchStop"]["initialMatchedPids"]
            .as_array()
            .expect("matched pids")
            .iter()
            .any(|pid| pid.as_u64() == Some(u64::from(existing.id()))),
        "expected pre-launch stop to report existing pid {} in {}",
        existing.id(),
        payload
    );
    assert!(
        existing.try_wait().expect("inspect existing").is_some(),
        "expected old process to be stopped before launch"
    );
    assert_eq!(
        payload["launch"]["gameArgs"],
        serde_json::json!(["0", "nomods"])
    );

    let _ = existing.kill();
    let _ = existing.wait();
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn game_launch_waits_for_endpoint_release_after_existing_process_stops() {
    let game_dir = create_fake_game_layout();

    let launcher = game_dir.path().join("fake-explicit-relaunch-endpoint-wait");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod launcher");
    let mut existing = spawn_launcher_with_retry(&launcher, &["30"]);

    let socket_path = game_dir.path().join("relaunch-endpoint-wait.sock");
    let stop_listener = Arc::new(AtomicBool::new(false));
    let listener = spawn_reachable_unix_listener_until(&socket_path, Arc::clone(&stop_listener));
    let listener_stop = Arc::clone(&stop_listener);
    let existing_pid = existing.id();
    let delayed_stop = std::thread::spawn(move || {
        // Anchor to observable progress: wait until the old process is actually
        // gone before releasing the endpoint, so the CLI's close-time endpoint
        // sample deterministically sees it still reachable regardless of how long
        // CLI startup took under a loaded parallel run.
        let proc_dir = PathBuf::from(format!("/proc/{existing_pid}"));
        for _ in 0..500 {
            let gone = !proc_dir.exists()
                || fs::read_to_string(proc_dir.join("stat"))
                    .ok()
                    .and_then(|stat| stat.split_whitespace().nth(2).map(|s| s == "Z"))
                    .unwrap_or(false);
            if gone {
                break;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        // Hold the endpoint up a margin after the process death so the close
        // sample sees `bridgeEndpointDisappeared == false`, then release well
        // inside the 1000 ms launch timeout.
        std::thread::sleep(Duration::from_millis(100));
        listener_stop.store(true, Ordering::SeqCst);
    });

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec!["0".to_string()],
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            mod_loadout: sts2::GameModLoadoutConfig {
                enabled: Some(Vec::new()),
                ..sts2::GameModLoadoutConfig::default()
            },
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = game_dir.path().to_path_buf();
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
                "1000",
                "--interval-ms",
                "10",
                "--rpc-timeout-ms",
                "25",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    stop_listener.store(true, Ordering::SeqCst);
    delayed_stop.join().expect("join delayed listener stop");
    listener.join().expect("join listener");

    assert_eq!(payload["launch"]["status"], "spawned");
    assert_eq!(payload["preLaunchStop"]["required"], true);
    assert_eq!(
        payload["preLaunchStop"]["close"]["bridgeEndpointDisappeared"],
        false
    );
    assert_eq!(
        payload["preLaunchStop"]["endpointWait"]["status"],
        "released"
    );
    assert_eq!(
        payload["preLaunchStop"]["endpointWait"]["cleanup"]["removed"],
        true
    );
    assert!(
        existing.try_wait().expect("inspect existing").is_some(),
        "expected old process to be stopped before launch"
    );

    let _ = existing.kill();
    let _ = existing.wait();
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn game_launch_reports_incomplete_stop_when_endpoint_remains_reachable() {
    let game_dir = create_fake_game_layout();

    let launcher = game_dir
        .path()
        .join("fake-explicit-relaunch-endpoint-timeout");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod launcher");
    let mut existing = spawn_launcher_with_retry(&launcher, &["30"]);

    let socket_path = game_dir.path().join("relaunch-endpoint-timeout.sock");
    let stop_listener = Arc::new(AtomicBool::new(false));
    let listener = spawn_reachable_unix_listener_until(&socket_path, Arc::clone(&stop_listener));

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec!["0".to_string()],
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            mod_loadout: sts2::GameModLoadoutConfig {
                enabled: Some(Vec::new()),
                ..sts2::GameModLoadoutConfig::default()
            },
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = game_dir.path().to_path_buf();
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
                "150",
                "--interval-ms",
                "10",
                "--rpc-timeout-ms",
                "25",
            ],
            &[],
        )
    })
    .await
    .expect("join launch task");

    stop_listener.store(true, Ordering::SeqCst);
    listener.join().expect("join listener");

    assert_eq!(payload["error"]["code"], "pre_launch_stop_incomplete");
    assert_eq!(payload["error"]["remainingPids"], serde_json::json!([]));
    assert_eq!(payload["error"]["endpointStillReachable"], true);
    assert_eq!(payload["error"]["endpointWait"]["status"], "timeout");
    assert_eq!(
        payload["error"]["close"]["bridgeEndpointDisappeared"],
        false
    );
    assert!(
        existing.try_wait().expect("inspect existing").is_some(),
        "expected old process to be stopped before timeout"
    );

    let _ = existing.kill();
    let _ = existing.wait();
}

#[cfg(unix)]
#[test]
fn game_launch_repairs_stale_unix_socket_before_spawn() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    let socket_path = workspace.path().join("stale-launch.sock");
    let listener = StdUnixListener::bind(&socket_path).expect("bind stale socket");
    drop(listener);

    let capture_path = workspace.path().join("launch-stale-cleanup.txt");
    let launcher = write_executable_script(
        workspace.path(),
        "launch-stale-cleanup.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nprintf launched > \"$1\"\n",
    );
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec![capture_path.to_string_lossy().into_owned()],
            launch_working_dir: Some(workspace.path().to_string_lossy().into_owned()),
            mod_loadout: sts2::GameModLoadoutConfig {
                enabled: Some(Vec::new()),
                ..sts2::GameModLoadoutConfig::default()
            },
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ],
        &[],
    );

    assert_eq!(payload["launch"]["status"], "spawned");
    assert_eq!(
        payload["launch"]["preLaunchEndpointCleanup"]["attempted"],
        true
    );
    assert_eq!(
        payload["launch"]["preLaunchEndpointCleanup"]["removed"],
        true
    );
    assert_eq!(
        payload["launch"]["preLaunchEndpointCleanup"]["reason"],
        "removed"
    );
    wait_for_file(&capture_path);
    assert!(!socket_path.exists());
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn project_profile_launch_refuses_duplicate_when_process_runs_without_bridge() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();

    let launcher = game_dir.path().join("fake-sts2-no-bridge");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod launcher");
    let mut existing = spawn_launcher_with_retry(&launcher, &["30"]);

    fs::write(
        workspace.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  duplicate-guard:
    description: Refuse duplicate launch.
    steps:
      - kind: launch
        timeoutMs: 100
        intervalMs: 5
"#,
    )
    .expect("write profiles");

    let socket_path = game_dir.path().join("missing-launch-bridge.sock");
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
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

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = workspace.path().to_path_buf();
    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "project",
                "profile",
                "run",
                "duplicate-guard",
            ],
            &[],
        )
    })
    .await
    .expect("join profile task");

    assert_eq!(
        payload["steps"][0]["error"]["code"],
        "game_already_running_without_bridge"
    );
    assert!(
        payload["steps"][0]["error"]["matchedPids"]
            .as_array()
            .expect("matched pids")
            .iter()
            .any(|pid| pid.as_u64() == Some(u64::from(existing.id()))),
        "expected duplicate guard to report existing pid {} in {}",
        existing.id(),
        payload
    );
    assert!(
        existing.try_wait().expect("inspect existing").is_none(),
        "implicit launch should not stop the existing process"
    );

    let _ = existing.kill();
    let _ = existing.wait();
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn project_profile_launch_reuses_existing_running_bridge() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();

    let launcher = workspace.path().join("fake-profile-sts2");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod launcher");
    let mut existing = spawn_launcher_with_retry(&launcher, &["30"]);

    fs::write(
        workspace.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  reuse-launch:
    description: Reuse an existing launch.
    steps:
      - kind: launch
        timeoutMs: 500
        intervalMs: 5
"#,
    )
    .expect("write profiles");

    let socket_path = workspace.path().join("spirectl-profile-launch-reuse.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
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

    let config_path = config.path().to_string_lossy().into_owned();
    let workdir = workspace.path().to_path_buf();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "project",
                "profile",
                "run",
                "reuse-launch",
            ],
            &[],
        )
    })
    .await
    .expect("join profile task");

    assert_eq!(payload["steps"][0]["kind"], "launch");
    assert_eq!(payload["steps"][0]["status"], "ok");
    assert_eq!(payload["steps"][0]["result"]["launch"]["status"], "reused");
    assert_eq!(
        payload["steps"][0]["result"]["attachment"]["stateSummary"]["rootScene"],
        "screens/main_menu"
    );

    server.abort();
    let _ = existing.kill();
    let _ = existing.wait();
}

#[cfg(all(unix, target_os = "linux"))]
#[tokio::test]
async fn game_launch_detaches_session_by_default_and_allows_opt_out() {
    async fn run_capture(no_detach_session: bool) -> (Value, Value, String) {
        let game_dir = create_fake_game_layout();
        create_deployed_bridge_layout(&game_dir.path().join("mods"));

        let socket_path = game_dir.path().join(if no_detach_session {
            "spirectl-launch-no-detach.sock"
        } else {
            "spirectl-launch-detach.sock"
        });
        let capture_path = game_dir.path().join(if no_detach_session {
            "launch-no-detach-process.json"
        } else {
            "launch-detach-process.json"
        });
        let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
        let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
        let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

        let launcher = write_executable_script(
            game_dir.path(),
            if no_detach_session {
                "fake-launch-no-detach.sh"
            } else {
                "fake-launch-detach.sh"
            },
            &format!(
                "#!/usr/bin/env bash\nset -euo pipefail\npid=$$\npgid=$(ps -o pgid= -p $$ | tr -d ' ')\nsid=$(ps -o sid= -p $$ | tr -d ' ')\nprintf '{{\"pid\":\"%s\",\"pgid\":\"%s\",\"sid\":\"%s\",\"expectedSid\":\"%s\"}}' \"$pid\" \"$pgid\" \"$sid\" \"${{EXPECTED_PARENT_SID}}\" > \"{}\"\n",
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

        let expected_sid = current_process_field("sid");
        let config_path = config.path().to_string_lossy().into_owned();
        let mut args = vec![
            "--json".to_string(),
            "--config".to_string(),
            config_path,
            "game".to_string(),
            "launch".to_string(),
            "--timeout-ms".to_string(),
            "500".to_string(),
            "--interval-ms".to_string(),
            "5".to_string(),
        ];
        if no_detach_session {
            args.push("--no-detach-session".to_string());
        }

        let workdir = game_dir.path().to_path_buf();
        let expected_sid_for_env = expected_sid.clone();
        let payload = tokio::task::spawn_blocking(move || {
            let arg_refs: Vec<&str> = args.iter().map(String::as_str).collect();
            run_json_in_dir_with_env(
                &workdir,
                &arg_refs,
                &[("EXPECTED_PARENT_SID", &expected_sid_for_env)],
            )
        })
        .await
        .expect("join launch task");
        wait_for_file(&capture_path);
        let captured: Value =
            serde_json::from_str(&fs::read_to_string(&capture_path).expect("capture process"))
                .expect("process json");
        server.abort();
        (payload, captured, expected_sid)
    }

    let (detached_payload, detached_capture, expected_sid) = run_capture(false).await;
    assert_eq!(
        detached_payload["launch"]["processIsolation"]["detachedSession"],
        true
    );
    assert_eq!(
        detached_payload["launch"]["processIsolation"]["mode"],
        "setsid"
    );
    assert_eq!(detached_capture["sid"], detached_capture["pid"]);
    assert_eq!(detached_capture["pgid"], detached_capture["pid"]);
    assert_ne!(detached_capture["sid"], expected_sid);

    let (inherited_payload, inherited_capture, expected_sid) = run_capture(true).await;
    assert_eq!(
        inherited_payload["launch"]["processIsolation"]["detachedSession"],
        false
    );
    assert_eq!(
        inherited_payload["launch"]["processIsolation"]["mode"],
        "inherited-session"
    );
    assert_eq!(inherited_capture["sid"], expected_sid);
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_wraps_configured_game_executable_and_preserves_game_env() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir.path().join("spirectl-launch-wrapper.sock");
    let wrapper_capture_path = game_dir.path().join("launch-wrapper-args.json");
    let game_capture_path = game_dir.path().join("launch-wrapper-game.json");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-wrapped-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\npython3 - \"$@\" <<'PY' > \"{}\"\nimport json, os, sys\nprint(json.dumps({{\"args\": sys.argv[1:], \"socket\": os.environ.get(\"SPIRECTL_BRIDGE_SOCKET_PATH\", \"\")}}))\nPY\n",
            game_capture_path.display()
        ),
    );
    let wrapper = write_executable_script(
        game_dir.path(),
        "fake-wrapper.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\npython3 - \"$@\" <<'PY' > \"{}\"\nimport json, sys\nprint(json.dumps(sys.argv[1:]))\nPY\nif [ \"${{1-}}\" = \"--wrap\" ]; then shift; fi\n\"$@\"\n",
            wrapper_capture_path.display()
        ),
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_wrapper: vec![
                wrapper
                    .file_name()
                    .expect("wrapper filename")
                    .to_string_lossy()
                    .into_owned(),
                "--wrap".to_string(),
            ],
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_args: vec!["--configured".to_string()],
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
    let prefixed_path = format!("{}:{}", game_dir.path().display(), path_value);

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
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
                "--",
                "--headless",
            ],
            &[("PATH", &prefixed_path), ("STS2_AGENT_HOST_LAUNCH", "0")],
        )
    })
    .await
    .expect("join launch task");

    assert_eq!(
        payload["launch"]["executable"],
        serde_json::json!(
            wrapper
                .file_name()
                .expect("wrapper filename")
                .to_string_lossy()
                .to_string()
        )
    );
    assert_eq!(
        payload["launch"]["gameExecutable"],
        serde_json::json!(launcher.to_string_lossy().to_string())
    );
    assert_eq!(
        payload["launch"]["gameArgs"],
        serde_json::json!(["--configured", "--headless"])
    );
    assert_eq!(
        payload["launch"]["wrapperCommand"],
        serde_json::json!(
            wrapper
                .file_name()
                .expect("wrapper filename")
                .to_string_lossy()
                .to_string()
        )
    );
    assert_eq!(
        payload["launch"]["wrapperArgs"],
        serde_json::json!(["--wrap"])
    );
    assert_eq!(
        payload["launch"]["spawnArgs"],
        serde_json::json!([
            "--wrap",
            launcher.to_string_lossy().to_string(),
            "--configured",
            "--headless"
        ])
    );
    wait_for_file(&wrapper_capture_path);
    wait_for_file(&game_capture_path);
    let wrapper_capture: Value =
        serde_json::from_str(&fs::read_to_string(&wrapper_capture_path).expect("wrapper capture"))
            .expect("wrapper json");
    assert_eq!(
        wrapper_capture,
        serde_json::json!([
            "--wrap",
            launcher.to_string_lossy().to_string(),
            "--configured",
            "--headless"
        ])
    );
    let game_capture: Value =
        serde_json::from_str(&fs::read_to_string(&game_capture_path).expect("game capture"))
            .expect("game json");
    assert_eq!(
        game_capture["args"],
        serde_json::json!(["--configured", "--headless"])
    );
    assert_eq!(
        game_capture["socket"],
        socket_path.to_string_lossy().as_ref()
    );
    server.abort();
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn game_close_signals_matching_launch_wrapper_process() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let game_executable = game_dir.path().join("fake-wrapped-sleep");
    fs::copy("/bin/sleep", &game_executable).expect("copy sleep");
    let mut permissions = fs::metadata(&game_executable)
        .expect("metadata")
        .permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&game_executable, permissions).expect("chmod sleep");

    let wrapper_pid_path = game_dir.path().join("wrapper.pid");
    let wrapper = write_executable_script(
        game_dir.path(),
        "fake-close-wrapper.sh",
        &format!(
            r#"#!/usr/bin/env bash
set -euo pipefail
echo $$ > "{}"
"$@" &
child=$!
trap 'kill "$child" 2>/dev/null || true; exit 0' TERM INT
wait "$child" || true
while :; do sleep 0.1; done
"#,
            wrapper_pid_path.display()
        ),
    );

    let socket_path = game_dir.path().join("spirectl-close-wrapper.sock");
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

    let mut wrapper_child = spawn_launcher_with_retry(&wrapper, &[game_executable.to_str().unwrap(), "30"]);
    wait_for_file(&wrapper_pid_path);
    let wrapper_pid = fs::read_to_string(&wrapper_pid_path)
        .expect("wrapper pid")
        .trim()
        .parse::<u32>()
        .expect("wrapper pid");

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
                "close",
                "--timeout-ms",
                "3000",
                "--interval-ms",
                "20",
            ],
            &[],
        )
    })
    .await
    .expect("join close task");

    assert_eq!(payload["success"], true);
    assert!(
        payload["wrapperProcesses"]["matchedPids"]
            .as_array()
            .expect("matched wrapper pids")
            .iter()
            .any(|pid| pid.as_u64() == Some(u64::from(wrapper_pid)))
    );
    assert_process_exited(wrapper_pid);
    let _ = wrapper_child.wait();
}

#[cfg(unix)]
#[tokio::test]
async fn game_launch_scrubs_inherited_ld_library_path() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let socket_path = game_dir.path().join("spirectl-launch-clean-env.sock");
    let capture_path = game_dir.path().join("launch-clean-env.json");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-clean-env.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '{{\"ld\":\"%s\",\"socket\":\"%s\"}}' \"${{LD_LIBRARY_PATH-}}\" \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
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

    let workdir = game_dir.path().to_path_buf();
    let response = tokio::task::spawn_blocking(move || {
        run_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "10",
            ],
            &[("LD_LIBRARY_PATH", "/tmp/cargo-target-debug")],
        )
    })
    .await
    .expect("join launch task");
    assert!(
        response.status.success(),
        "expected launch success, got status {:?}, stdout: {}, stderr: {}",
        response.status.code(),
        String::from_utf8_lossy(&response.stdout),
        String::from_utf8_lossy(&response.stderr)
    );

    wait_for_file(&capture_path);
    let capture: Value =
        serde_json::from_str(&fs::read_to_string(&capture_path).expect("capture json"))
            .expect("capture payload");
    assert_eq!(capture["ld"], "");
    assert_eq!(capture["socket"], socket_path.to_string_lossy().as_ref());
    server.abort();
}

#[tokio::test]
async fn game_launch_injects_tcp_env_and_clears_other_endpoint_vars() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_tcp_bridge_service(listener, service);

    let capture_path = game_dir.path().join("launch-tcp-env.json");
    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-tcp.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\ncat > \"{}\" <<EOF\n{{\"socket\":\"${{SPIRECTL_BRIDGE_SOCKET_PATH-}}\",\"pipe\":\"${{SPIRECTL_BRIDGE_PIPE_NAME-}}\",\"tcp\":\"${{SPIRECTL_BRIDGE_TCP_ADDRESS-}}\"}}\nEOF\n",
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
            kind: TransportKind::Tcp,
            tcp_address: Some(address.to_string()),
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
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join launch task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["attachment"]["endpoint"]["kind"], "tcp");
    assert_eq!(
        payload["attachment"]["endpoint"]["address"],
        address.to_string()
    );
    wait_for_file(&capture_path);
    let env_payload: Value =
        serde_json::from_str(&fs::read_to_string(&capture_path).expect("capture env"))
            .expect("env json");
    assert_eq!(env_payload["socket"], "");
    assert_eq!(env_payload["pipe"], "");
    assert_eq!(env_payload["tcp"], address.to_string());
    server.abort();
}

#[cfg(unix)]
#[test]
fn game_launch_reports_missing_launch_executable_guidance() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("launch.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "100",
            "--interval-ms",
            "10",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "launch_executable_not_found");
    assert_eq!(payload["error"]["configKey"], "game.launchExecutable");
    assert_eq!(
        payload["error"]["gamePath"],
        game_dir.path().display().to_string()
    );
    assert_eq!(
        payload["error"]["detectedExecutables"],
        serde_json::json!([])
    );
    assert_eq!(
        payload["error"]["searchedCandidates"],
        serde_json::json!([
            "SlayTheSpire2",
            "SlayTheSpire2.x86_64",
            "Slay the Spire 2.x86_64",
            "slaythespire2.x86_64"
        ])
    );
}

#[cfg(unix)]
#[test]
fn game_launch_reports_ambiguous_launch_executable_guidance() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    fs::write(game_dir.path().join("alpha.x86_64"), []).expect("alpha executable");
    fs::write(game_dir.path().join("beta.x86_64"), []).expect("beta executable");

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("launch.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "100",
            "--interval-ms",
            "10",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "launch_executable_ambiguous");
    assert_eq!(payload["error"]["configKey"], "game.launchExecutable");
    assert_eq!(
        payload["error"]["detectedExecutables"],
        serde_json::json!(["alpha.x86_64", "beta.x86_64"])
    );
    assert_eq!(
        payload["error"]["searchedCandidates"],
        serde_json::json!([
            "SlayTheSpire2",
            "SlayTheSpire2.x86_64",
            "Slay the Spire 2.x86_64",
            "slaythespire2.x86_64"
        ])
    );
}

#[cfg(unix)]
#[test]
fn game_launch_reports_invalid_empty_launch_wrapper_guidance() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-empty-wrapper.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_wrapper: vec!["   ".to_string()],
            launch_executable: Some(launcher.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                game_dir
                    .path()
                    .join("launch-empty-wrapper.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "launch",
            "--timeout-ms",
            "100",
            "--interval-ms",
            "10",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "invalid_lifecycle_config");
    assert_eq!(payload["error"]["configKey"], "game.launchWrapper");
    assert_eq!(
        payload["error"]["message"],
        "game.launchWrapper must be empty or start with a non-empty wrapper command."
    );
}

/// `--wait-quiescent-ms` is the difference between "the bridge answers" and
/// "the game stopped moving": callers that drive the UI right after a launch
/// otherwise have to sleep blindly.
#[cfg(unix)]
#[tokio::test]
async fn game_launch_waits_for_quiescence_when_budget_is_set() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    let socket_path = game_dir.path().join("spirectl-quiescent.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-quiescent.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
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

    let config_path = config.path().to_string_lossy().into_owned();
    let default_config_path = config_path.clone();
    let default_payload = tokio::task::spawn_blocking(move || {
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            &default_config_path,
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
        ]);
        serde_json::from_str::<Value>(&response.stdout).expect("json")
    })
    .await
    .expect("join default launch task");
    // Budget 0 keeps the key out of the payload entirely.
    assert!(default_payload.get("quiescence").is_none());

    let payload = tokio::task::spawn_blocking(move || {
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            &config_path,
            "game",
            "launch",
            "--timeout-ms",
            "500",
            "--interval-ms",
            "5",
            "--wait-quiescent-ms",
            "5000",
            "--quiescent-stable-samples",
            "2",
        ]);
        serde_json::from_str::<Value>(&response.stdout).expect("json")
    })
    .await
    .expect("join quiescent launch task");

    assert_eq!(payload["quiescence"]["requested"], true);
    assert_eq!(payload["quiescence"]["quiescent"], true);
    assert_eq!(payload["quiescence"]["timedOut"], false);
    assert_eq!(payload["quiescence"]["budgetMs"], 5_000);
    assert_eq!(payload["quiescence"]["requiredStableSamples"], 2);
    assert_eq!(payload["quiescence"]["status"]["quiescent"], true);
    server.abort();
}

/// A quiescence budget that runs out is a notice, not a failure: the launch
/// itself succeeded and the caller decides what to do about the wait.
#[cfg(unix)]
#[tokio::test]
async fn game_launch_quiescence_timeout_is_non_fatal_unless_required() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    let socket_path = game_dir.path().join("spirectl-quiescent-timeout.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-quiescent-timeout.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
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
    let required_workdir = workdir.clone();
    let required_config_path = config_path.clone();

    // The stub's first transition sample always reports a running animation, so
    // a 10 ms budget can never collect three consecutive quiescent samples.
    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
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
                "--wait-quiescent-ms",
                "10",
                "--quiescent-stable-samples",
                "3",
            ],
            &[],
        )
    })
    .await
    .expect("join non-fatal quiescence task");

    assert_eq!(status.code(), Some(0));
    assert_eq!(payload["quiescence"]["requested"], true);
    assert_eq!(payload["quiescence"]["quiescent"], false);
    assert_eq!(payload["quiescence"]["timedOut"], true);
    assert_eq!(payload["quiescence"]["reason"], "transition_wait_timeout");
    assert_eq!(payload["launch"]["status"], "spawned");

    let (required_status, required_payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            &required_workdir,
            &[
                "--json",
                "--config",
                &required_config_path,
                "game",
                "launch",
                "--timeout-ms",
                "500",
                "--interval-ms",
                "5",
                "--wait-quiescent-ms",
                "10",
                "--quiescent-stable-samples",
                "3",
                "--require-quiescent",
            ],
            &[],
        )
    })
    .await
    .expect("join required quiescence task");

    assert_eq!(required_status.code(), Some(4));
    assert_eq!(required_payload["error"]["code"], "quiescence_timeout");
    assert_eq!(required_payload["error"]["command"], "game launch");
    assert_eq!(required_payload["error"]["budgetMs"], 10);
    assert_eq!(
        required_payload["error"]["cause"]["code"],
        "transition_wait_timeout"
    );
    server.abort();
}

/// The game's own stdout/stderr is where an engine or mod startup failure shows
/// up. It used to be discarded unless a launch wrapper was configured.
#[cfg(unix)]
#[tokio::test]
async fn game_launch_captures_child_stdio_without_a_wrapper() {
    let game_dir = create_fake_game_layout();
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
    let socket_path = game_dir.path().join("spirectl-stdio.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let launcher = write_executable_script(
        game_dir.path(),
        "fake-launch-stdio.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\necho 'game stdout line'\necho 'game stderr line' >&2\n",
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
    let logs_workdir = workdir.clone();
    let logs_config_path = config_path.clone();
    let info_workdir = workdir.clone();
    let info_config_path = config_path.clone();
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
            &[],
        )
    })
    .await
    .expect("join launch task");

    let expected_dir = game_dir.path().join(".sts2/artifacts/game-launch");
    assert_eq!(
        payload["launch"]["stdio"]["dir"],
        expected_dir.to_string_lossy().as_ref()
    );
    // No wrapper is configured, so wrapperLogs keeps meaning "wrapper output".
    assert!(payload["launch"]["wrapperLogs"].is_null());

    let stdout_path = PathBuf::from(
        payload["launch"]["stdio"]["stdoutPath"]
            .as_str()
            .expect("stdout path"),
    );
    let stderr_path = PathBuf::from(
        payload["launch"]["stdio"]["stderrPath"]
            .as_str()
            .expect("stderr path"),
    );
    wait_for_file(&stdout_path);
    wait_for_file(&stderr_path);
    assert!(
        fs::read_to_string(&stdout_path)
            .expect("stdout log")
            .contains("game stdout line")
    );
    assert!(
        fs::read_to_string(&stderr_path)
            .expect("stderr log")
            .contains("game stderr line")
    );

    // `latest` symlinks point at this run.
    let latest_stderr = expected_dir.join("game.stderr.log");
    assert_eq!(
        payload["launch"]["stdio"]["latestStderrPath"],
        latest_stderr.to_string_lossy().as_ref()
    );
    assert_eq!(
        fs::canonicalize(&latest_stderr).expect("latest stderr link"),
        fs::canonicalize(&stderr_path).expect("stderr log")
    );

    let logs = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &logs_workdir,
            &[
                "--json",
                "--config",
                &logs_config_path,
                "dev",
                "logs",
                "--source",
                "game-stdio",
            ],
            &[],
        )
    })
    .await
    .expect("join logs task");
    assert_eq!(logs["source"], "game-stdio");
    assert!(
        logs["gameStdio"]["stderr"]["entries"]
            .as_array()
            .expect("stderr entries")
            .iter()
            .any(|entry| entry == "game stderr line")
    );

    let info = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &info_workdir,
            &["--json", "--config", &info_config_path, "game", "info"],
            &[],
        )
    })
    .await
    .expect("join info task");
    assert_eq!(
        info["launchStdio"]["dir"],
        expected_dir.to_string_lossy().as_ref()
    );
    assert_eq!(info["launchStdio"]["stderrExists"], true);
    server.abort();
}

/// The failure this fixes: `game close` from a checkout whose configured
/// executable path differs from the running game's reported "success" while the
/// game kept running. The registry now remembers the pids, and they are checked
/// before the executable scan.
#[cfg(target_os = "linux")]
#[tokio::test]
async fn game_close_stops_a_recorded_pid_whose_executable_path_differs() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    let instance_name = "closerec";
    let instance_dir = workspace.path().join(".sts2/instances").join(instance_name);
    let user_dir = instance_dir.join("user");
    fs::create_dir_all(&user_dir).expect("instance user dir");

    // The game actually running was started from a different path than the one
    // this invocation resolves, so only the recorded pid can find it.
    let other_checkout = workspace.path().join("other-checkout");
    fs::create_dir_all(&other_checkout).expect("other checkout");
    let running_game = write_executable_script(
        &other_checkout,
        "SlayTheSpire2",
        "#!/usr/bin/env bash\nset -euo pipefail\nsleep 30\n",
    );
    let configured_game = write_executable_script(
        game_dir.path(),
        "SlayTheSpire2",
        "#!/usr/bin/env bash\nset -euo pipefail\nsleep 30\n",
    );

    let mut child = spawn_launcher_with_retry(
        &running_game,
        &["--user-dir", user_dir.to_str().expect("utf8 user dir")],
    );
    let recorded_pid = child.id();

    fs::write(
        instance_dir.join("instance.json"),
        serde_json::to_string_pretty(&serde_json::json!({
            "name": instance_name,
            "mode": "shared",
            "socket": workspace.path().join(".sts2/ipc/closerec.sock").to_string_lossy(),
            "userDir": user_dir.to_string_lossy(),
            "launchExecutable": running_game.to_string_lossy(),
            "gamePids": [recorded_pid],
            "transportKind": "ipc",
            "status": "running",
            "createdAtUnix": 1,
            "updatedAtUnix": 1
        }))
        .expect("record json"),
    )
    .expect("write instance record");

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(configured_game.to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("unused-close.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--instance",
                instance_name,
                "--config",
                &config_path,
                "game",
                "close",
                "--timeout-ms",
                "5000",
                "--interval-ms",
                "25",
            ],
            &[],
        )
    })
    .await
    .expect("join close task");

    assert_eq!(
        payload["matchedPids"],
        serde_json::json!([recorded_pid]),
        "the recorded pid should be matched even though the executable path differs"
    );
    assert_eq!(payload["matchedProcesses"][0]["matchKind"], "recorded-pid");
    assert_eq!(payload["stoppedPids"], serde_json::json!([recorded_pid]));
    assert_eq!(payload["remainingPids"], serde_json::json!([]));
    assert_process_exited(recorded_pid);
    let _ = child.kill();
    let _ = child.wait();
}

/// Nothing matched still exits 0 (there is nothing to stop), but it says so
/// instead of reporting an empty success that reads like a stop.
#[cfg(target_os = "linux")]
#[tokio::test]
async fn game_close_reports_stopped_false_and_match_attempts_when_nothing_matches() {
    let workspace = tempfile::tempdir().expect("workspace");
    let game_dir = create_fake_game_layout();
    let configured_game = write_executable_script(
        game_dir.path(),
        "SlayTheSpire2-absent",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );

    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(configured_game.to_string_lossy().into_owned()),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(
                workspace
                    .path()
                    .join("absent-close.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "game",
                "close",
                "--timeout-ms",
                "1000",
                "--interval-ms",
                "25",
            ],
            &[],
        )
    })
    .await
    .expect("join close task");

    assert_eq!(status.code(), Some(0));
    assert_eq!(payload["success"], true);
    assert_eq!(payload["stopped"], false);
    assert_eq!(payload["stoppedPids"], serde_json::json!([]));
    assert_eq!(
        payload["matchAttempts"]["launchExecutable"],
        configured_game.to_string_lossy().as_ref()
    );
    assert_eq!(payload["matchAttempts"]["recordedPids"], serde_json::json!([]));
}
