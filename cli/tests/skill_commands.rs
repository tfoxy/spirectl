use std::fs;
use std::path::Path;
use std::process::Command;

use serde_json::Value;

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn run_in_dir(workdir: &Path, args: &[&str]) -> std::process::Output {
    Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .output()
        .expect("run sts2 binary")
}

fn run_json_in_dir(workdir: &Path, args: &[&str]) -> Value {
    let output = run_in_dir(workdir, args);
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("json output")
}

fn command_summary<'a>(payload: &'a Value, name: &str) -> &'a str {
    payload["commands"]
        .as_array()
        .expect("commands array")
        .iter()
        .find(|command| command["name"] == name)
        .and_then(|command| command["summary"].as_str())
        .expect("command summary")
}

#[test]
fn skill_install_inspect_summary_mentions_project_local_and_explicit_roots() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir(dir.path(), &["--json", "inspect", "commands"]);

    assert_eq!(
        command_summary(&payload, "skill install"),
        "Install the checked-in spirectl skill pack into a project-local .agents/skills directory or an explicit root path."
    );
}

#[test]
fn skill_install_writes_project_local_skill_by_default() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir(dir.path(), &["--json", "skill", "install"]);

    let skill_root = dir.path().join(".agents/skills");
    let skill_dir = skill_root.join("spirectl");
    let installed_path = skill_dir.join("SKILL.md");
    let installed = fs::read_to_string(&installed_path).expect("installed skill");
    let runtime_reference = skill_dir.join("references/cli-runtime.md");
    let library_reference = skill_dir.join("references/library-integration.md");

    assert_eq!(payload["name"], "spirectl");
    assert_eq!(payload["source"], "checked-in-skill-pack");
    assert_eq!(payload["pathSource"], "managed-default");
    assert_eq!(payload["managedDir"], skill_root.to_string_lossy().as_ref());
    assert_eq!(payload["skillDir"], skill_dir.to_string_lossy().as_ref());
    assert_eq!(payload["path"], installed_path.to_string_lossy().as_ref());
    assert_eq!(payload["sourcePath"], "skills/spirectl/SKILL.md");
    assert_eq!(payload["installedFiles"].as_array().unwrap().len(), 6);
    assert_eq!(payload["installedFiles"][0]["relativePath"], "SKILL.md");
    assert_eq!(payload["installedFiles"][0]["kind"], "skill");
    assert!(installed.contains("name: spirectl"));
    assert!(installed.contains("references/cli-runtime.md"));
    assert!(installed.contains("cargo run -q -p sts2 --"));
    assert!(
        fs::read_to_string(&runtime_reference)
            .expect("installed runtime reference")
            .contains("compact agent-first")
    );
    assert!(
        fs::read_to_string(&library_reference)
            .expect("installed library reference")
            .contains("ISpirectlRuntime")
    );
}

#[test]
fn skill_install_path_override_writes_under_requested_root() {
    let dir = tempfile::tempdir().expect("temp dir");
    let target_root = dir.path().join("vendor/skills");

    let payload = run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "skill",
            "install",
            "--path",
            target_root.to_string_lossy().as_ref(),
        ],
    );

    let skill_dir = target_root.join("spirectl");
    let installed_path = skill_dir.join("SKILL.md");
    let installed = fs::read_to_string(&installed_path).expect("installed skill");
    let testing_reference = skill_dir.join("references/testing-debugging.md");

    assert_eq!(payload["pathSource"], "explicit");
    assert!(payload["managedDir"].is_null());
    assert_eq!(payload["skillDir"], skill_dir.to_string_lossy().as_ref());
    assert_eq!(payload["path"], installed_path.to_string_lossy().as_ref());
    assert!(installed.contains("references/testing-debugging.md"));
    assert!(
        fs::read_to_string(&testing_reference)
            .expect("installed testing reference")
            .contains("test run")
    );
}
