#[cfg(unix)]
#[tokio::test]
async fn game_attach_waits_for_ipc_and_returns_screen_summary() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-attach.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

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

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "attach",
            "--timeout-ms",
            "5000",
            "--interval-ms",
            "5",
        ])
    })
    .await
    .expect("join attach task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(
        payload["socketPath"],
        socket_path.to_string_lossy().as_ref()
    );
    assert_eq!(payload["stateSummary"]["rootScene"], "screens/main_menu");
    assert_eq!(payload["gameInfo"]["transport"]["configuredKind"], "ipc");
    assert!(payload["attempts"].as_u64().expect("attempts") >= 1);
    server.abort();
}
#[test]
fn game_bridge_health_defaults_to_non_mutating_mode() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("default-health.sock");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "endpoint_missing");
    assert_eq!(payload["health"]["status"], "endpoint_missing");
    assert_eq!(payload["health"]["nonMutating"], true);
    assert_eq!(payload["health"]["repairStaleEndpoint"], false);
    assert!(!socket_path.exists());
}

#[test]
fn game_bridge_health_rejects_conflicting_mode_flags() {
    let game_dir = create_fake_game_layout();
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            ..GameConfig::default()
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
            "bridge-health",
            "--non-mutating",
            "--repair-stale-endpoint",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "bridge_health_conflicting_modes");
}

#[cfg(unix)]
#[test]
fn game_bridge_health_repair_removes_stale_unix_socket() {
    let game_dir = create_fake_game_layout();
    let data_root = game_dir.path().join("xdg-data");
    let log_path = seed_recent_spirectl_log(&data_root, "godot-repair.log");
    let socket_path = game_dir.path().join("repair-health.sock");
    let listener =
        std::os::unix::net::UnixListener::bind(&socket_path).expect("bind unix listener");
    drop(listener);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--repair-stale-endpoint",
        ],
        &[("XDG_DATA_HOME", data_root.to_str().expect("utf8 data root"))],
    );

    assert_eq!(payload["error"]["code"], "endpoint_missing");
    assert_eq!(
        payload["health"]["latestLog"]["logPath"],
        log_path.to_string_lossy().as_ref()
    );
    let safe_next = payload["health"]["safeNextCommands"]
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
    assert_eq!(payload["health"]["staleCleanup"]["attempted"], true);
    assert_eq!(payload["health"]["staleCleanup"]["removed"], true);
    assert_eq!(payload["health"]["staleCleanup"]["reason"], "removed");
    assert_eq!(
        payload["health"]["endpointAfterRepair"]["classification"],
        "missing"
    );
    assert!(!socket_path.exists());
}

#[cfg(unix)]
#[test]
fn game_bridge_health_reports_refused_endpoint_without_repairing_by_default() {
    let game_dir = create_fake_game_layout();
    let data_root = game_dir.path().join("xdg-data");
    let log_path = seed_recent_spirectl_log(&data_root, "godot-refused.log");
    let socket_path = game_dir.path().join("refused-health.sock");
    let listener =
        std::os::unix::net::UnixListener::bind(&socket_path).expect("bind unix listener");
    drop(listener);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[("XDG_DATA_HOME", data_root.to_str().expect("utf8 data root"))],
    );

    assert_eq!(payload["error"]["code"], "endpoint_refused_or_stale");
    assert_eq!(payload["health"]["status"], "endpoint_refused_or_stale");
    assert_eq!(
        payload["health"]["connection"]["bridgeErrorCode"],
        "ipc_connection_failed"
    );
    assert_eq!(
        payload["health"]["endpoint"]["ownerProcessIdentified"],
        false
    );
    assert!(payload["health"].get("staleCleanup").is_none());
    assert!(socket_path.exists());
    assert_eq!(
        payload["health"]["latestLog"]["logPath"],
        log_path.to_string_lossy().as_ref()
    );
    let safe_next = payload["health"]["safeNextCommands"]
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
#[test]
fn game_bridge_health_repair_skips_unsafe_directory_and_keeps_unrelated_entries() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("repair-directory.sock");
    fs::create_dir(&socket_path).expect("create directory placeholder");
    let unrelated = game_dir.path().join("unrelated.txt");
    fs::write(&unrelated, "keep").expect("write unrelated");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--repair-stale-endpoint",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "endpoint_ambiguous");
    assert_eq!(payload["health"]["staleCleanup"]["attempted"], false);
    assert_eq!(payload["health"]["staleCleanup"]["skipped"], true);
    assert_eq!(
        payload["health"]["staleCleanup"]["reason"],
        "not_stale_socket_candidate"
    );
    assert!(socket_path.is_dir());
    assert!(unrelated.exists());
}

#[cfg(unix)]
#[test]
fn game_bridge_health_reports_missing_socket_without_mutating() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("missing-health.sock");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "endpoint_missing");
    assert_eq!(payload["health"]["status"], "endpoint_missing");
    assert_eq!(payload["health"]["endpoint"]["classification"], "missing");
    assert_eq!(payload["health"]["endpoint"]["exists"], false);
    assert_eq!(payload["health"]["bridge"]["deployedStatus"], "current");
}

#[test]
fn game_bridge_health_reports_duplicate_legacy_bridge_mod_directory() {
    let game_dir = create_fake_game_layout();
    let mods_dir = game_dir.path().join("mods");
    let socket_path = game_dir.path().join("missing-duplicate-health.sock");
    create_deployed_bridge_layout(&mods_dir);
    let legacy_dir = mods_dir.join("SpirectlBridge");
    fs::create_dir_all(&legacy_dir).expect("legacy dir");
    fs::write(
        legacy_dir.join("mod_manifest.json"),
        r#"{"id":"spirectlbridge","version":"0.0.1"}"#,
    )
    .expect("legacy manifest");
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[],
    );
    let duplicate = &payload["health"]["duplicateBridgeMods"];

    assert_eq!(duplicate["status"], "duplicates_found");
    assert_eq!(duplicate["count"], 1);
    assert_eq!(duplicate["entries"][0]["directoryName"], "SpirectlBridge");
    assert_eq!(duplicate["entries"][0]["manifestId"], "spirectlbridge");
    assert_eq!(
        duplicate["entries"][0]["canonicalDirectoryName"],
        "spirectlbridge"
    );
    assert_eq!(
        duplicate["entries"][0]["issue"],
        "legacy_duplicate_bridge_directory"
    );
    assert!(legacy_dir.exists(), "bridge-health must not delete legacy dirs");
}

#[test]
fn game_bridge_health_ignores_canonical_bridge_mod_directory() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("missing-canonical-health.sock");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[],
    );

    assert_eq!(
        payload["health"]["duplicateBridgeMods"]["status"],
        "none"
    );
    assert_eq!(payload["health"]["duplicateBridgeMods"]["count"], 0);
    assert!(payload["health"]["duplicateBridgeMods"].get("entries").is_none());
}

#[cfg(unix)]
#[test]
fn game_bridge_health_reports_stale_socket_path() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("stale-health.sock");
    fs::write(&socket_path, b"stale socket placeholder").expect("write stale path");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "endpoint_ambiguous");
    assert_eq!(payload["health"]["status"], "endpoint_ambiguous");
    assert_eq!(payload["health"]["endpoint"]["classification"], "ambiguous");
    assert_eq!(payload["health"]["endpoint"]["exists"], true);
    assert_eq!(payload["health"]["endpoint"]["fileKind"], "file");
    assert_eq!(payload["health"]["connection"]["status"], "failed");
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_rpc_timeout() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("timeout-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server = spawn_unresponsive_unix_listener(listener).await;
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "100",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health timeout command");

    assert_eq!(payload["error"]["code"], "rpc_timeout");
    assert_eq!(payload["health"]["status"], "rpc_timeout");
    assert_eq!(payload["health"]["endpoint"]["fileKind"], "socket");
    assert_eq!(
        payload["health"]["endpoint"]["ownerProcessIdentified"],
        true
    );
    assert_eq!(payload["health"]["connection"]["status"], "failed");
    server.abort();
}

#[cfg(unix)]
#[test]
fn game_bridge_health_reports_directory_socket_placeholder() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("directory-health.sock");
    fs::create_dir(&socket_path).expect("create socket placeholder directory");
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = run_error_json_in_dir_with_env(
        game_dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "bridge-health",
            "--non-mutating",
        ],
        &[],
    );

    assert_eq!(payload["error"]["code"], "endpoint_ambiguous");
    assert_eq!(payload["health"]["status"], "endpoint_ambiguous");
    assert_eq!(payload["health"]["endpoint"]["classification"], "ambiguous");
    assert_eq!(payload["health"]["endpoint"]["exists"], true);
    assert_eq!(payload["health"]["endpoint"]["fileKind"], "directory");
    assert_eq!(payload["health"]["connection"]["status"], "failed");
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_stale_manifest_as_diagnostic_when_live_bridge_current() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("mismatch-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    create_deployed_bridge_layout_with_version(&game_dir.path().join("mods"), "0.0.1");
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

    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health mismatch command");

    assert_eq!(payload["error"]["code"], "deployed_version_mismatch");
    assert_eq!(payload["health"]["status"], "deployed_version_mismatch");
    assert_eq!(payload["health"]["connection"]["status"], "reachable");
    assert_eq!(
        payload["health"]["bridge"]["deployedStatus"],
        "version_mismatch"
    );
    assert_eq!(
        payload["health"]["safeNextCommands"],
        serde_json::json!([
            "sts2 game install-bridge",
            "sts2 game close",
            "sts2 game launch"
        ])
    );
    assert!(
        !payload["health"]["safeNextCommands"]
            .as_array()
            .expect("safe next commands")
            .iter()
            .any(|command| command
                .as_str()
                .expect("command string")
                .contains("game restart"))
    );
    assert_eq!(payload["health"]["bridge"]["deployedVersion"], "0.0.1");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_reachable_current_bridge() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(
        payload["endpoint"]["classification"],
        "local_socket_candidate"
    );
    assert_eq!(payload["endpoint"]["fileKind"], "socket");
    assert_eq!(payload["connection"]["status"], "reachable");
    assert!(payload.get("compatibility").is_none());
    assert_eq!(payload["bridge"]["deployedStatus"], "current");
    assert_eq!(
        payload["bridge"]["liveVersion"],
        sts2::bridge::bridge_version()
    );
    assert_eq!(
        payload["fullOutputCommand"],
        "sts2 --json game bridge-health --verbose"
    );
    server.abort();
}

#[test]
fn game_mods_settings_reads_explicit_settings_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    let settings_path = dir.path().join("settings.save");
    write_settings_save(
        &settings_path,
        &[
            ("BaseLib", true, "workshop"),
            ("godotexplorer", false, "mods_directory"),
        ],
    );
    let config = write_config(&AppConfig::default());

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "game",
            "mods",
            "settings",
            "--settings-file",
            settings_path.to_str().expect("settings utf8"),
        ],
        &[],
    );

    assert_eq!(payload["settingsFile"], settings_path.display().to_string());
    assert_eq!(payload["enabledIds"], serde_json::json!(["BaseLib"]));
    assert_eq!(payload["disabledIds"], serde_json::json!(["godotexplorer"]));
    assert_eq!(payload["mods"][0]["source"], "workshop");
}

#[cfg(unix)]
#[tokio::test]
async fn game_mods_active_reads_live_bridge_mod_list() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-mods.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "mods",
                "active",
            ],
            &[],
        )
    })
    .await
    .expect("join game mods active");

    assert_eq!(payload["activeIds"], serde_json::json!(["spirectlbridge"]));
    assert_eq!(payload["mods"][0]["id"], "spirectlbridge");
    assert_eq!(payload["mods"][0]["loadState"], "loaded");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_current_handshake_without_legacy_presentation_probe() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("spirectl-no-presentation.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server =
        ipc_bridge_support::spawn_unix_bridge_service_without_presentation(listener, service);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(payload["connection"]["status"], "reachable");
    assert_eq!(
        payload["bridge"]["liveVersion"],
        sts2::bridge::bridge_version()
    );
    assert!(payload.get("compatibility").is_none());
    assert_eq!(
        payload["safeNextCommands"],
        serde_json::json!(["sts2 --json state"])
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_same_semver_stale_live_host() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("same-semver-stale-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu)
        .with_build_identity(test_bridge_build_identity("1"));
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let payload = tokio::task::spawn_blocking(move || {
        run_error_json_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join stale live host bridge health command");

    assert_eq!(payload["error"]["code"], "stale_live_host");
    assert_eq!(payload["health"]["status"], "stale_live_host");
    assert_eq!(payload["health"]["connection"]["status"], "reachable");
    assert_eq!(
        payload["health"]["bridge"]["liveVersion"],
        sts2::bridge::bridge_version()
    );
    assert_eq!(
        payload["health"]["safeNextCommands"],
        serde_json::json!([
            "sts2 game install-bridge",
            "sts2 game bridge-health --repair-stale-endpoint",
            "sts2 game attach",
            "sts2 game launch",
            "sts2 --json game bridge-health"
        ])
    );
    assert!(
        !payload["health"]["safeNextCommands"]
            .as_array()
            .expect("safe next commands")
            .iter()
            .any(|command| command
                .as_str()
                .expect("command string")
                .contains("game close"))
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_reports_same_semver_current_live_host() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("same-semver-current-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu)
        .with_build_identity(test_bridge_build_identity("4102444800"));
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join current live host bridge health command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(
        payload["bridge"]["liveVersion"],
        sts2::bridge::bridge_version()
    );
    assert!(payload.get("liveBridge").is_none());
    assert!(payload.get("localBridge").is_none());
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_verbose_preserves_full_diagnostic_payload() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("verbose-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu)
        .with_build_identity(test_bridge_build_identity("4102444800"));
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    create_deployed_bridge_layout(&game_dir.path().join("mods"));
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--verbose",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join verbose bridge health command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(payload["nonMutating"], true);
    assert_eq!(payload["compatibility"], serde_json::json!({}));
    assert_eq!(
        payload["liveBridge"]["bridgeVersion"],
        sts2::bridge::bridge_version()
    );
    assert_eq!(
        payload["liveBridge"]["buildIdentity"]["builtAtUtc"],
        "4102444800"
    );
    assert_eq!(payload["localBridge"]["sourceFreshness"]["status"], "known");
    assert_eq!(payload["deployedBridge"]["status"], "current");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_allows_unknown_deployed_manifest_when_live_bridge_is_current() {
    let game_dir = tempfile::tempdir().expect("create temp game dir");
    let socket_path = game_dir.path().join("spirectl-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    let config = write_config(&AppConfig {
        game: GameConfig {
            path: "auto".to_string(),
            ..GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(payload["bridge"]["deployedStatus"], "unknown");
    assert_eq!(
        payload["safeNextCommands"],
        serde_json::json!(["sts2 --json state"])
    );
    assert_eq!(
        payload["bridge"]["liveVersion"],
        sts2::bridge::bridge_version()
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_allows_malformed_deployed_manifest_when_live_bridge_is_current() {
    let game_dir = create_fake_game_layout();
    let manifest_path = game_dir
        .path()
        .join("mods")
        .join(sts2::live_bridge::DEPLOYED_MOD_DIR_NAME)
        .join("spirectlbridge.json");
    std::fs::create_dir_all(manifest_path.parent().expect("manifest parent"))
        .expect("create deployed mod dir");
    std::fs::write(&manifest_path, "{\"unexpected\":true}").expect("write malformed manifest");
    let socket_path = game_dir.path().join("malformed-manifest-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health malformed manifest command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(payload["bridge"]["deployedStatus"], "unknown");
    assert!(payload["bridge"].get("deployedVersion").is_none());
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn game_bridge_health_allows_missing_deployed_manifest_when_live_bridge_is_current() {
    let game_dir = create_fake_game_layout();
    let socket_path = game_dir.path().join("missing-manifest-health.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
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

    let (status, payload) = tokio::task::spawn_blocking(move || {
        run_output_in_dir_with_env(
            game_dir.path(),
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "game",
                "bridge-health",
                "--non-mutating",
                "--rpc-timeout-ms",
                "5000",
            ],
            &[],
        )
    })
    .await
    .expect("join bridge health missing manifest command");

    assert!(status.success(), "payload: {payload:#}");
    assert_eq!(payload["status"], "reachable_current");
    assert_eq!(payload["bridge"]["deployedStatus"], "not_found");
    assert_eq!(
        payload["safeNextCommands"],
        serde_json::json!(["sts2 --json state"])
    );
    server.abort();
}
