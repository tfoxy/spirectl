use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use reqwest::blocking::Client;
use serde_json::{Value, json};
use sts2::{Cli, run_cli};

fn run_error(args: &[&str]) -> Value {
    let cli = Cli::parse_from(args.iter().copied());
    let rendered = run_cli(cli.clone())
        .expect_err("command should fail")
        .render(cli.json);
    serde_json::from_str(&rendered.stdout).expect("json output")
}

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn spawn_service(args: &[&str]) -> (Child, Value) {
    let repo_root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let mock_config = repo_root.join("tests/sts2.mock.yaml");
    let mut owned_args: Vec<String> = args.iter().map(|arg| (*arg).to_string()).collect();
    if !owned_args.iter().any(|arg| arg == "--config") {
        owned_args.insert(0, mock_config.display().to_string());
        owned_args.insert(0, "--config".to_string());
    }
    let mut child = Command::new(sts2_bin())
        .args(&owned_args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn sts2 service");

    let stdout = child.stdout.take().expect("child stdout");
    let mut reader = BufReader::new(stdout);
    let mut line = String::new();
    reader.read_line(&mut line).expect("read startup line");
    assert!(!line.trim().is_empty(), "expected service startup metadata");

    let payload: Value = serde_json::from_str(line.trim()).expect("startup json");
    (child, payload)
}

struct ServiceTestRoot {
    _temp: tempfile::TempDir,
    config_path: PathBuf,
    artifacts_dir: PathBuf,
}

impl ServiceTestRoot {
    fn new(name: &str) -> Self {
        let temp = tempfile::Builder::new()
            .prefix(&format!("sts2-service-{name}-"))
            .tempdir()
            .expect("service temp root");
        let artifacts_dir = temp.path().join("artifacts");
        fs::create_dir_all(&artifacts_dir).expect("artifacts dir");
        let config_path = temp.path().join("sts2.mock.yaml");
        fs::write(
            &config_path,
            format!(
                "transport:\n  kind: mock\n  mockScenario: main-menu\nartifacts:\n  dir: {}\n",
                artifacts_dir.display()
            ),
        )
        .expect("write service config");
        Self {
            _temp: temp,
            config_path,
            artifacts_dir,
        }
    }

    fn config_arg(&self) -> &str {
        self.config_path.to_str().expect("utf8 config path")
    }

    fn job_store_dir(&self) -> &str {
        "remote-jobs"
    }
}

fn spawn_service_with_config(config_path: &str, args: &[&str]) -> (Child, Value) {
    let mut owned_args: Vec<String> = args.iter().map(|arg| (*arg).to_string()).collect();
    owned_args.insert(0, config_path.to_string());
    owned_args.insert(0, "--config".to_string());

    let mut child = Command::new(sts2_bin())
        .args(&owned_args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn sts2 service");

    let stdout = child.stdout.take().expect("child stdout");
    let mut reader = BufReader::new(stdout);
    let mut line = String::new();
    reader.read_line(&mut line).expect("read startup line");
    assert!(!line.trim().is_empty(), "expected service startup metadata");

    let payload: Value = serde_json::from_str(line.trim()).expect("startup json");
    (child, payload)
}

fn spawn_durable_service(root: &ServiceTestRoot, token: &str) -> (Child, Value) {
    spawn_service_with_config(
        root.config_arg(),
        &[
            "--json",
            "service",
            "serve",
            "--listen",
            "127.0.0.1:0",
            "--auth-token",
            token,
            "--job-store",
            "durable",
            "--job-store-dir",
            root.job_store_dir(),
        ],
    )
}

fn wait_for_service(url: &str, token: Option<&str>) -> reqwest::blocking::Response {
    let client = Client::new();
    let started = Instant::now();
    loop {
        let mut request = client.get(url);
        if let Some(token) = token {
            request = request.bearer_auth(token);
        }

        match request.send() {
            Ok(response) => return response,
            Err(error) if started.elapsed() < Duration::from_secs(5) => {
                let _ = error;
                thread::sleep(Duration::from_millis(50));
            }
            Err(error) => panic!("service never became ready at {url}: {error}"),
        }
    }
}

fn stop_service(child: &mut Child) {
    let _ = child.kill();
    let status = child.wait().expect("wait for child");
    assert!(
        status.success() || status.code().is_none(),
        "expected clean shutdown or signal, got {status:?}"
    );
}

fn json_request(
    method: reqwest::Method,
    url: &str,
    token: &str,
    body: Option<Value>,
) -> reqwest::blocking::Response {
    let client = Client::new();
    let mut request = client.request(method, url).bearer_auth(token);
    if let Some(body) = body {
        request = request.json(&body);
    }
    request.send().expect("send request")
}

fn wait_for_completed_run(base_url: &str, token: &str, run_id: &str) -> Value {
    wait_for_run_status(base_url, token, run_id, &["completed"])
}

fn wait_for_run_status(base_url: &str, token: &str, run_id: &str, statuses: &[&str]) -> Value {
    let started = Instant::now();
    loop {
        let response = json_request(
            reqwest::Method::GET,
            &format!("{base_url}/v0/test-runs/{run_id}"),
            token,
            None,
        );
        assert_eq!(response.status(), 200);
        let payload: Value = response.json().expect("run status json");
        let status = payload["status"].as_str().expect("status");
        if statuses.contains(&status) {
            return payload;
        }

        assert!(
            started.elapsed() < Duration::from_secs(5),
            "test run never reached {statuses:?}: {payload}"
        );
        thread::sleep(Duration::from_millis(50));
    }
}

#[test]
fn service_serve_emits_startup_metadata_and_enforces_bearer_auth() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let unauthenticated = wait_for_service(&format!("{base_url}/v0/service"), None);
    assert_eq!(unauthenticated.status(), 401);

    let authenticated = wait_for_service(&format!("{base_url}/v0/service"), Some(token));
    assert_eq!(authenticated.status(), 200);

    let payload: Value = authenticated.json().expect("service metadata json");
    assert_eq!(payload["service"], "sts2");
    assert_eq!(payload["auth"]["mode"], "bearer");
    assert_eq!(payload["auth"]["required"], true);
    assert_eq!(payload["artifactRoot"], startup["artifactRoot"]);
    assert_eq!(payload["jobStore"]["mode"], "memory");
    assert_eq!(payload["jobStore"]["root"], Value::Null);

    stop_service(&mut child);
}

#[test]
fn service_serve_reports_durable_job_store_metadata() {
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--job-store",
        "durable",
    ]);

    let artifact_root = startup["artifactRoot"].as_str().expect("artifactRoot");
    assert_eq!(startup["jobStore"]["mode"], "durable");
    let expected_root = format!("{artifact_root}/remote-jobs").replace("/./", "/");
    assert_eq!(startup["jobStore"]["root"], expected_root);

    stop_service(&mut child);
}

#[test]
fn service_serve_rejects_non_loopback_without_auth_token() {
    let payload = run_error(&[
        "sts2",
        "--json",
        "service",
        "serve",
        "--listen",
        "0.0.0.0:4317",
    ]);

    assert_eq!(payload["error"]["code"], "service_auth_token_required");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("Non-loopback service binds require --auth-token")
    );
}

#[test]
fn network_mcp_mode() {
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--mcp-mode",
        "network",
    ]);
    assert_eq!(startup["mcp"]["mode"], "network");
    assert_eq!(startup["mcp"]["bindAddress"], "127.0.0.1:4318");
    assert_eq!(startup["mcp"]["endpoint"], "http://127.0.0.1:4318/v0/mcp");
    assert_eq!(startup["mcp"]["auth"]["required"], false);
    assert_eq!(startup["mcp"]["backendSource"], "inspect ai-tools");
    assert_eq!(startup["mcp"]["threatModelAcknowledged"], false);
    stop_service(&mut child);

    let public_without_auth = run_error(&[
        "sts2",
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:4317",
        "--mcp-mode",
        "network",
        "--mcp-listen",
        "0.0.0.0:4318",
    ]);
    assert_eq!(
        public_without_auth["error"]["code"],
        "network_mcp_auth_token_required"
    );

    let public_without_ack = run_error(&[
        "sts2",
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:4317",
        "--mcp-mode",
        "network",
        "--mcp-listen",
        "0.0.0.0:4318",
        "--mcp-auth-token",
        "local-mcp-token",
    ]);
    assert_eq!(
        public_without_ack["error"]["code"],
        "network_mcp_threat_model_acknowledgement_required"
    );

    let accepted_public_policy = run_error(&[
        "sts2",
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:4317",
        "--mcp-mode",
        "network",
        "--mcp-listen",
        "0.0.0.0:4318",
        "--mcp-auth-token",
        "local-mcp-token",
        "--acknowledge-non-loopback-mcp-threat-model",
    ]);
    assert_eq!(
        accepted_public_policy["error"]["code"],
        "service_requires_streaming"
    );

    let (mut public_child, public_startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--mcp-mode",
        "network",
        "--mcp-listen",
        "0.0.0.0:4318",
        "--mcp-auth-token",
        "local-mcp-token",
        "--acknowledge-non-loopback-mcp-threat-model",
    ]);
    assert_eq!(public_startup["mcp"]["mode"], "network");
    assert_eq!(public_startup["mcp"]["bindAddress"], "0.0.0.0:4318");
    assert_eq!(public_startup["mcp"]["auth"]["mode"], "bearer");
    assert_eq!(public_startup["mcp"]["auth"]["required"], true);
    assert_eq!(public_startup["mcp"]["threatModelAcknowledged"], true);
    stop_service(&mut public_child);

    let (mut durable_child, durable_startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--job-store",
        "durable",
        "--mcp-mode",
        "network",
    ]);
    assert_eq!(durable_startup["jobStore"]["mode"], "durable");
    assert_eq!(durable_startup["mcp"]["mode"], "network");
    assert_eq!(durable_startup["mcp"]["backendSource"], "inspect ai-tools");
    assert_eq!(
        durable_startup["mcp"]["testRunBackend"]["endpoint"],
        "/v0/test-runs"
    );
    assert_eq!(
        durable_startup["mcp"]["testRunBackend"]["schema"],
        "service-job-store"
    );
    assert_ne!(
        durable_startup["mcp"]["testRunBackend"]["endpoint"],
        "/v0/mcp/test-runs"
    );
    stop_service(&mut durable_child);
}

#[test]
fn service_exposes_ai_tool_catalog_and_dispatches_representative_calls() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let catalog = json_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/ai-tools"),
        token,
        None,
    );
    assert_eq!(catalog.status(), 200);
    let catalog_payload: Value = catalog.json().expect("catalog json");
    assert_eq!(catalog_payload["source"], "command-catalog");
    assert!(
        catalog_payload["tools"]
            .as_array()
            .expect("tools")
            .iter()
            .any(|tool| tool["name"] == "game_info")
    );
    assert!(
        catalog_payload["tools"]
            .as_array()
            .expect("tools")
            .iter()
            .any(|tool| tool["name"] == "hot_reload_status")
    );
    assert!(
        catalog_payload["tools"]
            .as_array()
            .expect("tools")
            .iter()
            .any(|tool| tool["name"] == "hot_reload")
    );
    assert!(
        catalog_payload["tools"]
            .as_array()
            .expect("tools")
            .iter()
            .any(|tool| tool["name"] == "console")
    );

    let game_info = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "game_info",
            "arguments": {}
        })),
    );
    assert_eq!(game_info.status(), 200);
    let game_info_payload: Value = game_info.json().expect("game info json");
    assert_eq!(game_info_payload["repository"], "spirectl");

    let state = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "state",
            "arguments": {}
        })),
    );
    assert_eq!(state.status(), 200);
    let state_payload: Value = state.json().expect("state json");
    assert!(state_payload.is_object());

    let action = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "act",
            "arguments": {
                "kind": "choose",
                "choiceId": "menu:start-run"
            }
        })),
    );
    assert_eq!(action.status(), 200);
    let action_payload: Value = action.json().expect("action json");
    assert_eq!(action_payload["kind"], "choose");
    assert_eq!(action_payload["accepted"], true);

    let console = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "console",
            "arguments": {
                "command": "help",
                "args": ["draw"]
            }
        })),
    );
    assert_eq!(console.status(), 200);
    let console_payload: Value = console.json().expect("console json");
    assert_eq!(console_payload["line"], "help draw");
    assert_eq!(console_payload["success"], true);

    stop_service(&mut child);
}

#[test]
fn service_exposes_debug_session_endpoints() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let start = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/debug-sessions"),
        token,
        Some(json!({
            "name": "m45",
            "pause": true,
            "leaseTimeoutMs": 9000
        })),
    );
    assert_eq!(start.status(), 200);
    let start_payload: Value = start.json().expect("start json");
    assert_eq!(start_payload["started"], false);
    assert_eq!(
        start_payload["notices"][0]["code"],
        "debug_session_unsupported"
    );

    let status = json_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/debug-sessions/dbg%3A1"),
        token,
        None,
    );
    assert_eq!(status.status(), 200);
    let status_payload: Value = status.json().expect("status json");
    assert_eq!(status_payload["found"], false);

    let wait = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/debug-sessions/dbg%3A1/wait"),
        token,
        Some(json!({
            "timeoutMs": 2500
        })),
    );
    assert_eq!(wait.status(), 200);
    let wait_payload: Value = wait.json().expect("wait json");
    assert_eq!(wait_payload["completed"], false);

    let end = json_request(
        reqwest::Method::DELETE,
        &format!("{base_url}/v0/debug-sessions/dbg%3A1?resume=true"),
        token,
        None,
    );
    assert_eq!(end.status(), 200);
    let end_payload: Value = end.json().expect("end json");
    assert_eq!(end_payload["ended"], false);

    stop_service(&mut child);
}

#[test]
fn debugger_event_streams() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let controller = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/debug-sessions"),
        token,
        Some(json!({
            "name": "service-controller",
            "role": "controller",
            "pause": true,
            "leaseTimeoutMs": 9000
        })),
    );
    assert_eq!(controller.status(), 200);
    let controller_payload: Value = controller.json().expect("controller json");
    assert_eq!(controller_payload["started"], false);
    assert_eq!(
        controller_payload["notices"][0]["code"],
        "debug_session_unsupported"
    );

    let observer = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/debug-sessions"),
        token,
        Some(json!({
            "name": "service-observer",
            "role": "observer"
        })),
    );
    assert_eq!(observer.status(), 200);
    let observer_payload: Value = observer.json().expect("observer json");
    assert_eq!(observer_payload["started"], false);
    assert_eq!(
        observer_payload["notices"][0]["code"],
        "debug_session_unsupported"
    );

    let events = json_request(
        reqwest::Method::GET,
        &format!(
            "{base_url}/v0/debug-sessions/dbg%3Aobserver/events?fromSequence=4&limit=2&follow=false&timeoutMs=25"
        ),
        token,
        None,
    );
    assert_eq!(events.status(), 200);
    let events_payload: Value = events.json().expect("events json");
    assert_eq!(events_payload["fromSequence"], 4);
    assert_eq!(events_payload["nextSequence"], 4);
    assert_eq!(events_payload["retention"]["oldestSequence"], 0);
    assert_eq!(events_payload["retention"]["newestSequence"], 0);
    assert_eq!(events_payload["retention"]["limit"], 0);
    assert_eq!(events_payload["expired"], false);
    assert_eq!(events_payload["overflow"], false);
    assert_eq!(events_payload["follow"], false);
    assert_eq!(events_payload["timedOut"], false);
    assert_eq!(
        events_payload["notices"][0]["code"],
        "debug_event_stream_unavailable"
    );

    let post_events = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/debug-sessions/dbg%3Aobserver/events"),
        token,
        Some(json!({
            "fromSequence": 7,
            "limit": 1,
            "follow": true,
            "timeoutMs": 1
        })),
    );
    assert_eq!(post_events.status(), 200);
    let post_events_payload: Value = post_events.json().expect("post events json");
    assert_eq!(post_events_payload["fromSequence"], 7);
    assert_eq!(post_events_payload["follow"], true);
    assert_eq!(post_events_payload["timeoutMs"], 1);
    assert_eq!(post_events_payload["timedOut"], true);
    assert_eq!(
        post_events_payload["notices"][0]["code"],
        "debug_event_stream_unavailable"
    );

    let tool_events = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "debug_events",
            "arguments": {
                "session": "dbg:observer",
                "fromSequence": 9,
                "limit": 3,
                "follow": false,
                "timeoutMs": 10
            }
        })),
    );
    assert_eq!(tool_events.status(), 200);
    let tool_events_payload: Value = tool_events.json().expect("tool events json");
    assert_eq!(tool_events_payload["fromSequence"], 9);
    assert_eq!(
        tool_events_payload["retention"],
        post_events_payload["retention"]
    );
    assert_eq!(
        tool_events_payload["notices"][0]["code"],
        "debug_event_stream_unavailable"
    );

    stop_service(&mut child);
}

#[test]
fn service_dispatches_non_interactive_catalog_tools_beyond_the_initial_subset() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let toolchain = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "toolchain_info",
            "arguments": {}
        })),
    );
    assert_eq!(toolchain.status(), 200);
    let payload: Value = toolchain.json().expect("toolchain json");
    assert!(payload["roots"].is_object());
    assert!(
        payload["tools"]
            .as_array()
            .expect("tools")
            .iter()
            .any(|tool| tool["id"] == "dotnet-helper")
    );

    stop_service(&mut child);
}

#[test]
fn service_rejects_test_run_on_tool_call_endpoint() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let response = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "test_run",
            "arguments": {
                "inline": "{name: remote-smoke, steps: [game.info]}"
            }
        })),
    );
    assert_eq!(response.status(), 400);
    let payload: Value = response.json().expect("error json");
    assert_eq!(payload["error"]["code"], "unsupported_service_tool");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("/v0/test-runs")
    );

    stop_service(&mut child);
}

#[test]
fn service_tool_call_failures_preserve_the_cli_error_envelope() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let response = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "act",
            "arguments": {
                "kind": "choose"
            }
        })),
    );
    assert_eq!(response.status(), 400);
    let payload: Value = response.json().expect("error json");
    assert_eq!(payload["error"]["code"], "invalid_ai_tool_arguments");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("choiceId")
    );

    stop_service(&mut child);
}

#[test]
fn service_console_tool_preserves_dangerous_mode_rejection() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let response = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "console",
            "arguments": {
                "command": "achievement"
            }
        })),
    );
    assert_eq!(response.status(), 400);
    let payload: Value = response.json().expect("error json");
    assert_eq!(payload["error"]["code"], "invalid_mode");
    assert_eq!(payload["error"]["requiredMode"], "dangerous");

    stop_service(&mut child);
}

#[test]
fn service_hot_reload_status_preserves_cli_error_envelope() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let response = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/tools/call"),
        token,
        Some(json!({
            "name": "hot_reload_status",
            "arguments": {
                "project": "./missing-hot-reload-project"
            }
        })),
    );
    assert_eq!(response.status(), 400);
    let payload: Value = response.json().expect("error json");
    assert_eq!(payload["error"]["code"], "hot_reload_project_not_found");
    assert!(
        payload["error"]["metadataFile"]
            .as_str()
            .expect("metadataFile")
            .ends_with("missing-hot-reload-project/sts2.hot-reload.yaml")
    );

    stop_service(&mut child);
}

#[test]
fn service_runs_remote_test_jobs_and_serves_artifacts() {
    let token = "local-dev-token";
    let (mut child, startup) = spawn_service(&[
        "--json",
        "service",
        "serve",
        "--listen",
        "127.0.0.1:0",
        "--auth-token",
        token,
    ]);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "{name: remote-smoke, steps: [game.info]}"
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"].as_str().expect("runId");

    let completed = wait_for_completed_run(base_url, token, run_id);
    assert_eq!(completed["status"], "completed");
    assert_eq!(completed["summary"]["status"], "passed");
    assert!(
        completed["summary"]["remoteArtifacts"]
            .as_array()
            .expect("remoteArtifacts")
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );

    let manifest = json_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/test-runs/{run_id}/artifacts"),
        token,
        None,
    );
    assert_eq!(manifest.status(), 200);
    let manifest_payload: Value = manifest.json().expect("manifest json");
    assert!(
        manifest_payload["artifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );

    let summary = json_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/test-runs/{run_id}/artifacts/summary.json"),
        token,
        None,
    );
    assert_eq!(summary.status(), 200);
    let summary_payload: Value = summary.json().expect("summary json");
    assert_eq!(summary_payload["status"], "passed");

    stop_service(&mut child);
}

#[test]
fn durable_remote_jobs_artifacts_survive_restart() {
    let token = "local-dev-token";
    let root = ServiceTestRoot::new("recover-after-restart");
    let (mut child, startup) = spawn_durable_service(&root, token);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "{name: durable-restart-smoke, steps: [game.info]}",
            "durable": true
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"]
        .as_str()
        .expect("runId")
        .to_string();

    let completed = wait_for_completed_run(base_url, token, &run_id);
    assert_eq!(completed["summary"]["status"], "passed");
    stop_service(&mut child);

    let (mut restarted, restarted_startup) = spawn_durable_service(&root, token);
    assert_eq!(restarted_startup["recovery"]["recoveredJobs"], 1);
    assert_eq!(restarted_startup["recovery"]["orphanedJobs"], 0);

    let restarted_base_url = restarted_startup["baseUrl"].as_str().expect("baseUrl");
    let recovered = wait_for_completed_run(restarted_base_url, token, &run_id);
    assert_eq!(recovered["runId"], run_id);
    assert_eq!(recovered["status"], "completed");
    assert_eq!(recovered["summary"]["status"], "passed");
    let remote_artifacts = recovered["summary"]["remoteArtifacts"]
        .as_array()
        .expect("remote artifacts");
    let summary_artifact = remote_artifacts
        .iter()
        .find(|artifact| artifact["relativePath"] == "summary.json")
        .expect("summary artifact");
    assert!(
        summary_artifact["downloadUrl"]
            .as_str()
            .expect("download URL")
            .starts_with(restarted_base_url)
    );
    assert!(
        !summary_artifact["downloadUrl"]
            .as_str()
            .expect("download URL")
            .starts_with(base_url)
    );

    let summary = json_request(
        reqwest::Method::GET,
        summary_artifact["downloadUrl"]
            .as_str()
            .expect("download URL"),
        token,
        None,
    );
    assert_eq!(summary.status(), 200);
    assert_eq!(
        summary.json::<Value>().expect("summary json")["status"],
        "passed"
    );

    let manifest = json_request(
        reqwest::Method::GET,
        &format!("{restarted_base_url}/v0/test-runs/{run_id}/artifacts"),
        token,
        None,
    );
    assert_eq!(manifest.status(), 200);
    let manifest_payload: Value = manifest.json().expect("manifest json");
    assert!(
        manifest_payload["artifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );

    let traversal = json_request(
        reqwest::Method::GET,
        &format!("{restarted_base_url}/v0/test-runs/{run_id}/artifacts/%2E%2E%2Fjob.json"),
        token,
        None,
    );
    assert_eq!(traversal.status(), 400);
    assert_eq!(
        traversal.json::<Value>().expect("traversal json")["error"]["code"],
        "invalid_artifact_path"
    );

    let unindexed = json_request(
        reqwest::Method::GET,
        &format!("{restarted_base_url}/v0/test-runs/{run_id}/artifacts/not-indexed.json"),
        token,
        None,
    );
    assert_eq!(unindexed.status(), 404);
    assert_eq!(
        unindexed.json::<Value>().expect("unindexed json")["error"]["code"],
        "remote_test_run_artifact_not_indexed"
    );

    stop_service(&mut restarted);
}

#[test]
fn durable_remote_jobs_orphan_running_after_restart() {
    let token = "local-dev-token";
    let root = ServiceTestRoot::new("orphan-running");
    let (mut child, startup) = spawn_durable_service(&root, token);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "{name: durable-orphan-smoke, steps: [{dev.delay: {ms: 3000}}, game.info]}",
            "durable": true
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"]
        .as_str()
        .expect("runId")
        .to_string();
    let running = wait_for_run_status(base_url, token, &run_id, &["running"]);
    assert_eq!(running["status"], "running");
    stop_service(&mut child);

    let (mut restarted, restarted_startup) = spawn_durable_service(&root, token);
    assert_eq!(restarted_startup["recovery"]["recoveredJobs"], 1);
    assert_eq!(restarted_startup["recovery"]["orphanedJobs"], 1);

    let restarted_base_url = restarted_startup["baseUrl"].as_str().expect("baseUrl");
    let orphaned = wait_for_run_status(restarted_base_url, token, &run_id, &["orphaned"]);
    assert_eq!(orphaned["runId"], run_id);
    assert_eq!(orphaned["status"], "orphaned");
    assert_eq!(orphaned["error"]["code"], "remote_test_run_orphaned");
    assert_eq!(orphaned["recovery"]["previousStatus"], "running");
    assert!(orphaned.get("summary").is_none());

    stop_service(&mut restarted);
}

#[test]
fn durable_remote_jobs_store() {
    let token = "local-dev-token";
    let root = ServiceTestRoot::new("durable-test");
    let (mut child, startup) = spawn_durable_service(&root, token);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let job_store_root = startup["jobStore"]["root"]
        .as_str()
        .expect("job store root")
        .to_string();
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "{name: durable-remote-smoke, steps: [game.info]}",
            "durable": true
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"].as_str().expect("runId");
    assert!(!run_id.contains('/'));
    assert!(!run_id.contains(".."));

    let completed = wait_for_completed_run(base_url, token, run_id);
    assert_eq!(completed["status"], "completed");

    let job_dir = Path::new(&job_store_root).join(run_id);
    let job_path = job_dir.join("job.json");
    let artifacts_path = job_dir.join("artifacts.json");
    let job: Value = serde_json::from_slice(&fs::read(&job_path).expect("durable job json exists"))
        .expect("durable job json");
    let artifacts: Value =
        serde_json::from_slice(&fs::read(&artifacts_path).expect("artifact index exists"))
            .expect("artifact index json");

    assert_eq!(job["status"], "completed");
    assert!(job["submittedAt"].as_str().is_some());
    assert!(job["startedAt"].as_str().is_some());
    assert!(job["updatedAt"].as_str().is_some());
    assert!(job["completedAt"].as_str().is_some());
    assert_eq!(job["command"]["name"], "test run");
    assert_eq!(job["config"]["durable"], true);
    assert_eq!(job["summary"]["status"], "passed");
    assert!(
        job["summaryRef"]
            .as_str()
            .expect("summary ref")
            .ends_with("summary.json")
    );
    assert!(
        job["runnerRootRef"]
            .as_str()
            .expect("runner root ref")
            .contains("test-runs/")
    );
    let artifact_entries = artifacts.as_array().expect("artifact index array");
    assert!(
        artifact_entries
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );
    assert!(artifact_entries.iter().any(|artifact| {
        artifact["relativePath"]
            .as_str()
            .expect("relative path")
            .ends_with("result.json")
    }));

    let runner_root = Path::new(job["runnerRoot"].as_str().expect("runner root"));
    assert!(runner_root.join("summary.json").exists());
    assert!(runner_root.starts_with(&root.artifacts_dir));
    assert!(!job_dir.join("summary.json").exists());
    assert!(!job_dir.join("result.json").exists());

    let temp_entries: Vec<_> = fs::read_dir(&job_dir)
        .expect("read job dir")
        .flatten()
        .filter(|entry| entry.file_name().to_string_lossy().contains(".tmp-"))
        .collect();
    assert!(
        temp_entries.is_empty(),
        "atomic temp files should be renamed"
    );

    stop_service(&mut child);
}

#[test]
fn durable_remote_jobs_failed_runner_summary_survives_restart() {
    let token = "local-dev-token";
    let root = ServiceTestRoot::new("failed-summary");
    let (mut child, startup) = spawn_durable_service(&root, token);

    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "name: durable-failed-summary\nsteps:\n  - dev.assert:\n      path: screen.id\n      equals: combat\n",
            "durable": true
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"]
        .as_str()
        .expect("runId")
        .to_string();

    let completed = wait_for_completed_run(base_url, token, &run_id);
    assert_eq!(completed["status"], "completed");
    assert_eq!(completed["summary"]["status"], "failed");
    assert_eq!(completed["summary"]["failedScenarioCount"], 1);
    assert_eq!(
        completed["summary"]["scenarios"][0]["failure"]["error"]["code"],
        "assertion_failed"
    );
    stop_service(&mut child);

    let job_path = root
        .artifacts_dir
        .join(root.job_store_dir())
        .join(&run_id)
        .join("job.json");
    let job: Value = serde_json::from_slice(&fs::read(&job_path).expect("durable job json"))
        .expect("durable job json");
    assert_eq!(job["status"], "completed");
    assert_eq!(job["summary"]["status"], "failed");
    assert_eq!(
        job["summary"]["scenarios"][0]["failure"]["error"]["code"],
        "assertion_failed"
    );

    let (mut restarted, restarted_startup) = spawn_durable_service(&root, token);
    assert_eq!(restarted_startup["recovery"]["recoveredJobs"], 1);
    let restarted_base_url = restarted_startup["baseUrl"].as_str().expect("baseUrl");
    let recovered = wait_for_completed_run(restarted_base_url, token, &run_id);
    assert_eq!(recovered["summary"]["status"], "failed");
    assert_eq!(
        recovered["summary"]["scenarios"][0]["failure"]["error"]["code"],
        "assertion_failed"
    );
    assert!(
        recovered["summary"]["remoteArtifacts"]
            .as_array()
            .expect("remote artifacts")
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );

    stop_service(&mut restarted);
}

#[test]
fn durable_remote_jobs_memory_mode_compatibility() {
    let token = "local-dev-token";
    let root = ServiceTestRoot::new("memory-compat");
    let (mut child, startup) = spawn_service_with_config(
        root.config_arg(),
        &[
            "--json",
            "service",
            "serve",
            "--listen",
            "127.0.0.1:0",
            "--auth-token",
            token,
        ],
    );

    assert_eq!(startup["jobStore"]["mode"], "memory");
    assert_eq!(startup["jobStore"]["root"], Value::Null);
    let base_url = startup["baseUrl"].as_str().expect("baseUrl");
    let submitted = json_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        token,
        Some(json!({
            "inline": "{name: memory-remote-smoke, steps: [game.info]}"
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"].as_str().expect("runId");
    let completed = wait_for_completed_run(base_url, token, run_id);
    assert_eq!(completed["status"], "completed");
    assert_eq!(completed["summary"]["status"], "passed");

    let manifest = json_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/test-runs/{run_id}/artifacts"),
        token,
        None,
    );
    assert_eq!(manifest.status(), 200);
    assert!(
        manifest.json::<Value>().expect("manifest json")["artifacts"]
            .as_array()
            .expect("artifacts")
            .iter()
            .any(|artifact| artifact["relativePath"] == "summary.json")
    );
    assert!(!root.artifacts_dir.join(root.job_store_dir()).exists());

    stop_service(&mut child);
}
