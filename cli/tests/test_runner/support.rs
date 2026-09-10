use std::fs;
#[cfg(unix)]
use std::io::{BufRead, BufReader};
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
#[cfg(unix)]
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};

use serde_json::{Value, json};
use serde_yaml::Value as YamlValue;
use sts2::bridge::StubBridgeService;
use sts2::bridge::proto::bridge_service_server::BridgeService;
use sts2::bridge::proto::{
    ActionRequest, FixtureLoadRequest, HandshakeRequest, LogsRequest, ScreenshotRequest,
    StateRequest,
};
use sts2::{AppConfig, Cli, MockScenario, RenderedCommand, TransportKind, run_cli};
use tokio::net::UnixListener;

fn run(args: &[&str]) -> RenderedCommand {
    let config = write_raw_config("transport:\n  kind: mock\n");
    let mut owned_args: Vec<String> = args.iter().map(|arg| (*arg).to_string()).collect();
    if !owned_args.iter().any(|arg| arg == "--config") {
        owned_args.insert(1, config.path().to_str().expect("utf8 config").to_string());
        owned_args.insert(1, "--config".to_string());
    }
    let cli = Cli::parse_from(owned_args.iter().map(String::as_str));
    run_cli(cli).expect("command should succeed")
}

#[allow(dead_code)]
fn run_error(args: &[&str]) -> RenderedCommand {
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli.clone())
        .expect_err("command should fail")
        .render(cli.json)
}

#[cfg(unix)]
fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

#[cfg(unix)]
fn spawn_artifact_index_service(job_store_dir: &str) -> (Child, Value) {
    let repo_root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root");
    let mock_config = repo_root.join("tests/sts2.mock.yaml");
    let mut child = Command::new(sts2_bin())
        .args([
            "--config",
            mock_config.to_str().expect("mock config utf8"),
            "--json",
            "service",
            "serve",
            "--listen",
            "127.0.0.1:0",
            "--auth-token",
            "local-dev-token",
            "--job-store",
            "durable",
            "--job-store-dir",
            job_store_dir,
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn sts2 service");

    let stdout = child.stdout.take().expect("child stdout");
    let mut reader = BufReader::new(stdout);
    let mut line = String::new();
    reader.read_line(&mut line).expect("read startup line");
    assert!(!line.trim().is_empty(), "expected startup metadata");
    let startup = serde_json::from_str(line.trim()).expect("startup json");
    (child, startup)
}

#[cfg(unix)]
fn stop_artifact_index_service(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait().expect("wait for service");
}

#[cfg(unix)]
fn remote_artifact_index_request(
    method: reqwest::Method,
    url: &str,
    body: Option<Value>,
) -> reqwest::blocking::Response {
    let client = reqwest::blocking::Client::new();
    let mut request = client.request(method, url).bearer_auth("local-dev-token");
    if let Some(body) = body {
        request = request.json(&body);
    }
    request.send().expect("send service request")
}

#[cfg(unix)]
fn wait_for_remote_artifact_index_completed(base_url: &str, run_id: &str) -> Value {
    let started = std::time::Instant::now();
    loop {
        let response = remote_artifact_index_request(
            reqwest::Method::GET,
            &format!("{base_url}/v0/test-runs/{run_id}"),
            None,
        );
        assert_eq!(response.status(), 200);
        let payload: Value = response.json().expect("run status json");
        if payload["status"] == "completed" {
            return payload;
        }
        assert!(
            started.elapsed() < std::time::Duration::from_secs(5),
            "remote run never completed: {payload}"
        );
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
}

