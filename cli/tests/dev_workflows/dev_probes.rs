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
