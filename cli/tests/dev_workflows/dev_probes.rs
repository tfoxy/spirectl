use super::*;

#[test]
fn dev_console_help_returns_structured_json_from_mock_bridge() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "console",
        "help",
        "draw",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "help");
    assert_eq!(payload["args"], serde_json::json!(["draw"]));
    assert_eq!(payload["line"], "help draw");
    assert_eq!(payload["accepted"], true);
    assert_eq!(payload["success"], true);
    assert_eq!(payload["source"], "stub");
    assert_eq!(payload["provisional"], true);
}

#[test]
fn dev_console_regular_commands_run_in_normal_mode() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "console",
        "help",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["success"], true);
}

#[test]
fn dev_console_dangerous_commands_require_exact_dangerous_mode() {
    let config = combat_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "console",
        "achievement",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_mode");
    assert_eq!(payload["error"]["requiredMode"], "dangerous");
}

#[test]
fn dev_console_unknown_command_returns_structured_rejection() {
    let config = combat_mock_config();
    let response = run_error(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "console",
        "nope",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "console_command_rejected");
    assert_eq!(payload["error"]["command"], "nope");
    assert_eq!(payload["error"]["accepted"], true);
    assert_eq!(payload["error"]["success"], false);
}

#[test]
fn dev_http_returns_structured_http_probe_output() {
    let url = format!(
        "{}/health",
        spawn_http_json_server(vec![(200, r#"{"status":"ok"}"#)])
    );

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "http",
        "--url",
        &url,
        "--expect-status",
        "200",
        "--query",
        "json.status",
        "--equals",
        "ok",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["probe"], "http");
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["request"]["url"], url);
    assert_eq!(payload["request"]["method"], "GET");
    assert!(payload["timing"]["elapsedMs"].is_number());
    assert_eq!(payload["response"]["status"], 200);
    assert_eq!(payload["response"]["statusText"], "OK");
    assert_eq!(payload["response"]["resource"]["url"], url);
    assert_eq!(
        payload["response"]["resource"]["contentType"],
        "application/json"
    );
    assert_eq!(payload["response"]["json"]["status"], "ok");
}

#[test]
fn dev_http_status_assertion_preserves_response_evidence() {
    let url = format!(
        "{}/health",
        spawn_http_json_server(vec![(503, r#"{"status":"starting"}"#)])
    );

    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "http",
        "--url",
        &url,
        "--expect-status",
        "200",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "probe_assertion_failed");
    assert_eq!(payload["error"]["probe"], "http");
    assert_eq!(payload["error"]["field"], "status");
    assert_eq!(payload["error"]["expected"], 200);
    assert_eq!(payload["error"]["actual"], 503);
    assert_eq!(payload["error"]["response"]["status"], 503);
    assert_eq!(payload["error"]["response"]["json"]["status"], "starting");
    assert!(payload["error"]["timing"]["elapsedMs"].is_number());
}

#[test]
fn dev_http_wait_polls_until_the_expected_response_matches() {
    let url = format!(
        "{}/health",
        spawn_http_json_server(vec![
            (503, r#"{"status":"starting"}"#),
            (200, r#"{"status":"ok"}"#),
        ])
    );

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "http-wait",
        "--url",
        &url,
        "--expect-status",
        "200",
        "--query",
        "json.status",
        "--equals",
        "ok",
        "--timeout-ms",
        "1000",
        "--interval-ms",
        "10",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["probe"], "http-wait");
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["attempts"], 2);
    assert_eq!(payload["response"]["status"], 200);
}

#[test]
fn dev_fetch_reads_local_json_and_writes_the_requested_output() {
    let dir = tempfile::tempdir().expect("temp dir");
    let source = dir.path().join("input.json");
    let output = dir.path().join("output.json");
    fs::write(&source, r#"{"status":"ok","items":[1,2,3]}"#).expect("write source");

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "fetch",
        "--source",
        source.to_str().expect("utf8 source"),
        "--output",
        output.to_str().expect("utf8 output"),
        "--query",
        "json.status",
        "--equals",
        "ok",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["probe"], "fetch");
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["artifact"]["path"], output.display().to_string());
    assert!(output.is_file(), "expected fetched artifact on disk");
}

#[test]
fn dev_websocket_connects_sends_and_receives_expected_frames() {
    let url = spawn_websocket_echo_server();

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "websocket",
        "--url",
        &url,
        "--send-text",
        "ping",
        "--expect-text",
        "pong",
        "--timeout-ms",
        "1000",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["probe"], "websocket");
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["request"]["url"], url);
    assert_eq!(payload["request"]["timeoutMs"], 1000);
    assert_eq!(payload["handshake"]["status"], 101);
    assert!(payload["timing"]["elapsedMs"].is_number());
    assert_eq!(payload["sentFrames"][0]["text"], "ping");
    assert_eq!(payload["receivedFrames"][0]["text"], "pong");
}

#[test]
fn dev_websocket_assertion_failure_preserves_frame_evidence() {
    let url = spawn_websocket_single_response_server("not-pong");

    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "websocket",
        "--url",
        &url,
        "--send-text",
        "ping",
        "--expect-text",
        "pong",
        "--timeout-ms",
        "1000",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "probe_assertion_failed");
    assert_eq!(payload["error"]["probe"], "websocket");
    assert_eq!(payload["error"]["expected"], "pong");
    assert_eq!(payload["error"]["actual"], "not-pong");
    assert_eq!(payload["error"]["sentFrames"][0]["text"], "ping");
    assert!(
        payload["error"]["receivedFrames"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    assert!(payload["error"]["timing"]["elapsedMs"].is_number());
}

#[test]
fn dev_console_commands_lists_registered_commands_without_help() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().unwrap(),
        "dev",
        "console",
        "commands",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).unwrap();
    assert_eq!(payload["source"], "stub");
    assert_eq!(payload["complete"], false);
    assert_eq!(payload["count"], 3);
    for name in ["fight", "room", "event"] {
        let command = payload["commands"]
            .as_array()
            .unwrap()
            .iter()
            .find(|c| c["name"] == name)
            .unwrap();
        assert_eq!(command["requires"], "run");
        assert_eq!(command["dangerous"], false);
        assert!(command["usage"].is_string());
        assert!(command["description"].is_string());
        assert_eq!(command["networked"], true);
        assert_eq!(command["debugOnly"], true);
    }
    assert!(
        !payload["commands"]
            .as_array()
            .unwrap()
            .iter()
            .any(|c| c["name"] == "help")
    );
}

#[test]
fn dev_console_args_completes_and_filters_fight_ids() {
    let config = combat_mock_config();
    for (prefix, expected) in [
        ("", serde_json::json!(["KNIGHTS_ELITE", "SLIMES"])),
        ("kn", serde_json::json!(["KNIGHTS_ELITE"])),
        ("missing", serde_json::json!([])),
    ] {
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().unwrap(),
            "dev",
            "console",
            "args",
            "fight",
            "--prefix",
            prefix,
        ]);
        let payload: Value = serde_json::from_str(&response.stdout).unwrap();
        assert_eq!(payload["knownCommand"], true);
        assert_eq!(payload["command"], "fight");
        assert_eq!(payload["source"], "stub");
        assert_eq!(payload["complete"], false);
        assert_eq!(payload["position"], 0);
        assert_eq!(payload["prefix"], prefix);
        assert_eq!(payload["preceding"], serde_json::json!([]));
        assert_eq!(payload["candidates"], expected);
        if prefix != "missing" {
            assert_eq!(
                payload["candidateDetails"][0],
                serde_json::json!({
                    "value": "KNIGHTS_ELITE", "label": "Knight Gang"
                })
            );
        }
        assert_eq!(payload["count"], expected.as_array().unwrap().len());
        assert_eq!(payload["completionType"], "argument");
    }
}

#[test]
fn dev_console_args_position_truncates_and_pads_without_shifting_prefix() {
    let config = combat_mock_config();
    for (position, preceding, candidates) in [
        ("2", serde_json::json!(["old", ""]), serde_json::json!([])),
        (
            "0",
            serde_json::json!([]),
            serde_json::json!(["KNIGHTS_ELITE"]),
        ),
    ] {
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().unwrap(),
            "dev",
            "console",
            "args",
            "fight",
            "old",
            "--position",
            position,
            "--prefix",
            "KN",
        ]);
        let payload: Value = serde_json::from_str(&response.stdout).unwrap();
        assert_eq!(payload["position"], position.parse::<u64>().unwrap());
        assert_eq!(payload["prefix"], "KN");
        assert_eq!(payload["preceding"], preceding);
        assert_eq!(payload["candidates"], candidates);
    }
}

#[test]
fn dev_console_args_unknown_command_completes_command_names() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().unwrap(),
        "dev",
        "console",
        "args",
        "fi",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).unwrap();
    assert_eq!(payload["knownCommand"], false);
    assert_eq!(payload["candidates"], serde_json::json!(["fight"]));
    assert_eq!(payload["completionType"], "command");
}

#[test]
fn dev_console_discovery_text_output_has_one_item_per_line() {
    let config = combat_mock_config();
    let response = run(&[
        "sts2",
        "--config",
        config.path().to_str().unwrap(),
        "dev",
        "console",
        "commands",
    ]);
    assert_eq!(response.stdout, "event  <id>\nfight  <id>\nroom  <type>\n");
    let response = run(&[
        "sts2",
        "--config",
        config.path().to_str().unwrap(),
        "dev",
        "console",
        "args",
        "fight",
        "--prefix",
        "KN",
    ]);
    assert_eq!(response.stdout, "KNIGHTS_ELITE  Knight Gang\n");
}

#[test]
fn dev_console_args_rejects_whitespace_tokens() {
    let config = combat_mock_config();
    for tail in [
        vec!["two words"],
        vec!["fight", "two words"],
        vec!["fight", "--prefix", "two words"],
    ] {
        let response = run_error(
            &[
                vec![
                    "sts2",
                    "--json",
                    "--config",
                    config.path().to_str().unwrap(),
                    "dev",
                    "console",
                    "args",
                ],
                tail,
            ]
            .concat(),
        );
        let payload: Value = serde_json::from_str(&response.stdout).unwrap();
        assert_eq!(payload["error"]["code"], "invalid_console_command");
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn dev_console_discovery_uses_ipc_and_tcp_methods() {
    for tcp in [false, true] {
        let temp = tempfile::tempdir().unwrap();
        let socket_path = temp.path().join("console.sock");
        let service = sts2::bridge::StubBridgeGrpcService::new(MockScenario::Combat);
        let (server, config) = if tcp {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap().to_string();
            (
                ipc_bridge_support::spawn_tcp_bridge_service(listener, service),
                AppConfig {
                    transport: sts2::TransportConfig {
                        kind: TransportKind::Tcp,
                        tcp_address: Some(address),
                        ..Default::default()
                    },
                    ..Default::default()
                },
            )
        } else {
            let listener = tokio::net::UnixListener::bind(&socket_path).unwrap();
            (
                ipc_bridge_support::spawn_unix_bridge_service(listener, service),
                AppConfig {
                    transport: sts2::TransportConfig {
                        kind: TransportKind::Ipc,
                        ipc_path: Some(socket_path.display().to_string()),
                        ..Default::default()
                    },
                    ..Default::default()
                },
            )
        };
        let config_file = write_config(&config);
        let config_path = config_file.path().to_str().unwrap().to_string();
        tokio::task::spawn_blocking(move || {
            let response = run(&[
                "sts2",
                "--json",
                "--config",
                &config_path,
                "dev",
                "console",
                "commands",
            ]);
            let catalog: Value = serde_json::from_str(&response.stdout).unwrap();
            assert_eq!(catalog["count"], 3);
            for name in ["fight", "room", "event"] {
                assert!(
                    catalog["commands"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .any(|cmd| cmd["name"] == name)
                );
            }
            let response = run(&[
                "sts2",
                "--json",
                "--config",
                &config_path,
                "dev",
                "console",
                "args",
                "fight",
                "old",
                "--position",
                "0",
                "--prefix",
                "kn",
            ]);
            let args: Value = serde_json::from_str(&response.stdout).unwrap();
            assert_eq!(args["candidates"], serde_json::json!(["KNIGHTS_ELITE"]));
            assert_eq!(args["candidateDetails"][0]["label"], "Knight Gang");
            assert_eq!(args["commonPrefix"], "fight KNIGHTS_ELITE ");
            let response = run(&[
                "sts2",
                "--json",
                "--config",
                &config_path,
                "dev",
                "console",
                "args",
                "fight",
                "--position",
                "2",
            ]);
            let args: Value = serde_json::from_str(&response.stdout).unwrap();
            assert_eq!(args["preceding"], serde_json::json!(["", ""]));
            assert_eq!(args["position"], 2);
            assert_eq!(args["candidates"], serde_json::json!([]));
        })
        .await
        .unwrap();
        server.abort();
    }
}

#[test]
fn dev_console_discovery_falls_back_after_socket_listener_closes() {
    let dir = tempfile::tempdir().unwrap();
    let reference = dir.path().join("toolchain/decompile/sts2");
    fs::create_dir_all(&reference).unwrap();
    fs::write(reference.join("MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.PaperFightConsoleCmd.cs"),
        r#"public class PaperFightConsoleCmd { public string CmdName => "fight"; public string Args => "<id>"; public string Description => "Synthetic encounter command"; }"#).unwrap();
    fs::write(
        reference.join("MegaCrit.Sts2.Core.Models.Encounters.PaperKnightsElite.cs"),
        "public class PaperKnightsElite : EncounterModel { }",
    )
    .unwrap();
    let socket_dir = tempfile::tempdir().unwrap();
    let socket = socket_dir.path().join("bridge.sock");
    let mut config = AppConfig::default();
    config.transport.kind = TransportKind::Ipc;
    config.transport.ipc_path = Some(socket.display().to_string());
    config.toolchain.dir = Some(dir.path().join("toolchain").display().to_string());
    let config = write_config(&config);
    for stale in [false, true] {
        if stale {
            drop(std::os::unix::net::UnixListener::bind(&socket).unwrap());
            assert!(socket.exists());
        }
        for discovery in [vec!["commands"], vec!["args", "fight"]] {
            let mut argv = vec![
                "sts2",
                "--json",
                "--config",
                config.path().to_str().unwrap(),
                "dev",
                "console",
            ];
            argv.extend(discovery);
            let response = run(&argv);
            let value: Value = serde_json::from_str(&response.stdout).unwrap();
            assert_eq!(value["source"], "static-reference");
            assert_eq!(value["complete"], false);
            assert_eq!(value["count"], 1);
            if value["candidates"].is_array() {
                assert_eq!(
                    value["candidates"],
                    serde_json::json!(["PAPER_KNIGHTS_ELITE"])
                );
            }
        }
    }
}
