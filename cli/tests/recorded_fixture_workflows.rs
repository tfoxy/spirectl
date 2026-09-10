use std::fs;
use std::path::Path;
use std::process::Command;

use serde_json::Value;

fn sts2_bin() -> &'static str {
    env!("CARGO_BIN_EXE_sts2")
}

fn write_config_in(root: &Path, contents: &str) -> std::path::PathBuf {
    let path = root.join("sts2.local.yaml");
    fs::write(&path, contents).expect("write config");
    path
}

fn run_in_dir(root: &Path, args: &[&str]) -> std::process::Output {
    Command::new(sts2_bin())
        .current_dir(root)
        .args(args)
        .output()
        .expect("run sts2")
}

fn parse_json(output: &std::process::Output) -> Value {
    serde_json::from_slice(&output.stdout).expect("json stdout")
}

#[test]
fn recorded_fixture_status_empty_root_returns_absent() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("utf8 config path"),
            "dev",
            "fixture",
            "status",
        ],
    );
    let payload = parse_json(&output);

    assert!(output.status.success());
    assert_eq!(payload["schemaVersion"], "spirectl.recorded-fixture/v0");
    assert_eq!(payload["command"], "dev fixture status");
    assert_eq!(payload["exists"], false);
    assert_eq!(
        payload["path"],
        ".sts2/fixtures/current-screen/fixture.sts2.fixture.yaml"
    );
}

#[test]
fn recorded_fixture_record_writes_fixture_and_metadata() {
    let temp = tempfile::tempdir().expect("temp dir");
    let config = write_config_in(
        temp.path(),
        "transport:\n  kind: mock\n  mockScenario: combat\n",
    );

    let output = run_in_dir(
        temp.path(),
        &[
            "--json",
            "--config",
            config.to_str().expect("utf8 config path"),
            "dev",
            "fixture",
            "record",
        ],
    );
    let payload = parse_json(&output);

    assert!(output.status.success());
    assert_eq!(payload["command"], "dev fixture record");
    assert_eq!(payload["screen"]["id"], "combat");
    assert_eq!(
        payload["path"],
        ".sts2/fixtures/current-screen/fixture.sts2.fixture.yaml"
    );
    assert!(
        temp.path()
            .join(".sts2/fixtures/current-screen/fixture.sts2.fixture.yaml")
            .exists()
    );
    assert!(
        temp.path()
            .join(".sts2/fixtures/current-screen/metadata.json")
            .exists()
    );
}

#[test]
fn recorded_fixture_clear_removes_current_screen_directory() {
    let temp = tempfile::tempdir().expect("temp dir");
    let root = temp.path().join(".sts2/fixtures/current-screen");
    fs::create_dir_all(&root).expect("root");
    fs::write(
        root.join("fixture.sts2.fixture.yaml"),
        "schemaVersion: spirectl.fixture/v0\nname: recorded-current-screen\n",
    )
    .expect("fixture");
    fs::write(
        root.join("metadata.json"),
        r#"{"schemaVersion":"spirectl.recorded-fixture/v0"}"#,
    )
    .expect("metadata");

    let output = run_in_dir(temp.path(), &["--json", "dev", "fixture", "clear"]);
    let payload = parse_json(&output);

    assert!(output.status.success());
    assert_eq!(payload["deleted"], true);
    assert!(!root.exists());
}
