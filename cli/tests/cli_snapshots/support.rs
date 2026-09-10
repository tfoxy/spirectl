use std::fs;
#[cfg(unix)]
use std::os::unix::net::UnixListener;
use std::path::PathBuf;

use clap::Parser;
use serde_json::{Value, json};
use sts2::{
    AppConfig, Cli, MockScenario, RenderedCommand, TransportKind, run_cli, run_cli_streaming,
};

fn read_snapshot(name: &str) -> String {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("snapshots")
        .join(name);
    fs::read_to_string(path).expect("snapshot file")
}

fn read_repo_doc(path: &str) -> String {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join(path);
    fs::read_to_string(path).expect("repo doc file")
}

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

fn run_error(args: &[&str]) -> RenderedCommand {
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli.clone())
        .expect_err("command should fail")
        .render(cli.json)
}

fn write_raw_config(config_text: &str) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn write_viewport_catalog(contents: &str) -> tempfile::NamedTempFile {
    let temp = tempfile::Builder::new()
        .suffix(".sts2.viewport-presets.yaml")
        .tempfile()
        .expect("temp viewport catalog");
    fs::write(temp.path(), contents).expect("write viewport catalog");
    temp
}

fn assert_snapshot(actual: &str, name: &str) {
    let expected = read_snapshot(name);
    assert_eq!(actual.trim_end(), expected.trim_end());
}

fn write_snapshot(actual: &str, name: &str) {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("snapshots")
        .join(name);
    fs::write(path, actual).expect("write snapshot file");
}
