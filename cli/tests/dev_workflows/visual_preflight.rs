use super::*;

#[test]
fn visual_preflight_print_only_emits_kaiser_commands() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "visual-preflight",
        "--encounter",
        "kaiser_crab_boss",
        "--print-only",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let cmds = payload["nextCommands"].as_array().expect("commands");
    assert!(cmds.iter().any(|cmd| {
        cmd.as_str()
            .unwrap_or_default()
            .contains("composed://encounters/kaiser_crab_boss/background/image")
    }));
    assert!(cmds.iter().any(|cmd| {
        cmd.as_str()
            .unwrap_or_default()
            .contains("m78-live-encounter-artifacts --encounter kaiser_crab_boss --json")
    }));
}

#[cfg(unix)]
#[test]
fn visual_preflight_unavailable_host_returns_diagnostics() {
    let workspace = tempfile::tempdir().expect("workspace");
    let data_root = workspace.path().join("xdg-data");
    let log_path = seed_recent_spirectl_log(&data_root, "godot-visual-preflight.log");
    let socket_path = workspace.path().join("refused-visual-preflight.sock");
    let listener =
        std::os::unix::net::UnixListener::bind(&socket_path).expect("bind unix listener");
    drop(listener);
    let config = write_raw_config(&format!(
        "transport:\n  kind: ipc\n  ipcPath: {}\n",
        socket_path.to_string_lossy()
    ));
    let (exit_code, payload) = run_binary_error_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "visual-preflight",
            "--encounter",
            "kaiser_crab_boss",
        ],
        &[("XDG_DATA_HOME", data_root.to_str().expect("utf8 data root"))],
    );
    assert_eq!(exit_code, 4);
    assert_eq!(payload["error"]["code"], "endpoint_refused_or_stale");
    assert_eq!(payload["preflight"]["status"], "endpoint_refused_or_stale");
    assert_eq!(payload["preflight"]["code"], "endpoint_refused_or_stale");
    assert_eq!(
        payload["preflight"]["latestLog"]["logPath"],
        log_path.to_string_lossy().as_ref()
    );
    assert_eq!(
        payload["preflight"]["endpoint"]["staleEndpointEvidence"]["ownerProcessIdentified"],
        false
    );
    assert!(payload["preflight"]["diagnostics"].is_array());
    let next_commands = payload["preflight"]["nextCommands"]
        .as_array()
        .expect("next commands");
    assert!(
        next_commands
            .iter()
            .any(|command| command == "sts2 game bridge-health --repair-stale-endpoint")
    );
    assert!(
        next_commands
            .iter()
            .any(|command| command == "sts2 game attach")
    );
    assert!(
        next_commands
            .iter()
            .any(|command| command == "sts2 game launch")
    );
    assert!(
        next_commands
            .iter()
            .any(|command| command == "sts2 --json game bridge-health")
    );
}
