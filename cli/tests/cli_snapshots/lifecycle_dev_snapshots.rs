use super::*;

#[test]
fn game_detect_json_reports_resolved_live_bridge_layout() {
    let game_dir = tempfile::tempdir().expect("game dir");
    let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.path().join("mods");
    fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
    fs::create_dir_all(&mods_dir).expect("mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = format!(
        "game:\n  path: {}\ntransport:\n  kind: ipc\n  ipcPath: /tmp/custom-spirectl.sock\n",
        game_dir.path().display()
    );
    fs::write(temp.path(), config_text).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "game",
        "detect",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "configured");
    assert_eq!(payload["liveBridge"]["status"], "ready");
    assert_eq!(
        payload["liveBridge"]["gamePath"],
        game_dir.path().to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["liveBridge"]["assembliesDir"],
        assemblies_dir.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["liveBridge"]["modsDir"],
        mods_dir.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["liveBridge"]["modDir"],
        mods_dir.join("spirectlbridge").to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["liveBridge"]["socketPath"],
        "/tmp/custom-spirectl.sock"
    );
}

#[test]
fn dev_logs_snapshot_matches() {
    let response = run(&["sts2", "--json", "dev", "logs"]);
    assert_snapshot(&response.stdout, "dev-logs.json");
}

#[test]
fn act_play_card_succeeds_for_combat_mock() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "act",
        "play-card",
        "--card",
        "c_1",
        "--target",
        "e_1",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["kind"], "play-card");
}

#[test]
fn tcp_transport_returns_transport_connection_failed_error() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Tcp,
            tcp_address: Some("127.0.0.1:51173".to_string()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");

    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "state",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 4);
    assert_eq!(payload["error"]["code"], "transport_connection_failed");
    assert_eq!(payload["error"]["details"][0]["field"], "endpoint");
}
