use std::net::TcpListener;
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;
use std::time::Duration;

use serde_json::Value;

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root")
        .to_path_buf()
}

fn build_fixture_assembly() -> &'static PathBuf {
    static FIXTURE_ASSEMBLY: OnceLock<PathBuf> = OnceLock::new();

    FIXTURE_ASSEMBLY.get_or_init(|| {
        let repo_root = repo_root();
        let fixture_project = repo_root
            .join("dotnet-tools/tests/Spirectl.DotnetTools.TestSymbols/Spirectl.DotnetTools.TestSymbols.csproj");
        let status = Command::new("dotnet")
            .arg("build")
            .arg(&fixture_project)
            .current_dir(&repo_root)
            .status()
            .expect("build fixture assembly");
        assert!(status.success(), "expected fixture project build to succeed");

        repo_root.join(
            "dotnet-tools/tests/Spirectl.DotnetTools.TestSymbols/bin/Debug/net9.0/Spirectl.DotnetTools.TestSymbols.dll",
        )
    })
}

fn fixture_assemblies_dir() -> tempfile::TempDir {
    let assemblies_dir = tempfile::tempdir().expect("assemblies dir");
    let fixture_assembly = build_fixture_assembly();
    std::fs::copy(
        fixture_assembly,
        assemblies_dir.path().join(
            fixture_assembly
                .file_name()
                .expect("fixture assembly file name"),
        ),
    )
    .expect("copy fixture assembly");
    assemblies_dir
}

fn create_fake_game_layout() -> tempfile::TempDir {
    let game_dir = tempfile::tempdir().expect("game dir");
    let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.path().join("mods");
    std::fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    std::fs::create_dir_all(&mods_dir).expect("create mods dir");
    std::fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    std::fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");
    std::fs::write(game_dir.path().join("SlayTheSpire2.pck"), []).expect("write game pck");
    game_dir
}

#[cfg(unix)]
fn write_executable_script(dir: &Path, name: &str, contents: &str) -> PathBuf {
    let path = dir.join(name);
    std::fs::write(&path, contents).expect("write script");
    let mut permissions = std::fs::metadata(&path).expect("metadata").permissions();
    permissions.set_mode(0o755);
    std::fs::set_permissions(&path, permissions).expect("chmod");
    path
}

#[cfg(unix)]
fn write_hook_script(dir: &Path, input_path: &Path, artifact_path: &Path) -> PathBuf {
    write_executable_script(
        dir,
        "hook.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\ncat > {input_path:?}\nprintf 'hook-artifact\\n' > {artifact_path:?}\nprintf '{{\"output\":{{\"status\":\"ok\",\"cwd\":\"%s\"}},\"artifacts\":[{{\"path\":%s,\"kind\":\"file\"}}]}}\\n' \"$PWD\" '\"{artifact}\"'\n",
            artifact = artifact_path.display()
        ),
    )
}

fn run_in_dir(workdir: &std::path::Path, args: &[&str]) -> std::process::Output {
    Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .output()
        .expect("run sts2 binary")
}

fn run_json_in_dir(workdir: &std::path::Path, args: &[&str]) -> Value {
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

fn dotnet_helper_tool(payload: &Value) -> &Value {
    payload["tools"]
        .as_array()
        .expect("tools array")
        .iter()
        .find(|tool| tool["id"] == "dotnet-helper")
        .expect("dotnet-helper tool")
}

fn create_default_helper_project_layout(dir: &Path) -> PathBuf {
    let project_dir = dir.join("dotnet-tools/src/Spirectl.DotnetTools");
    std::fs::create_dir_all(&project_dir).expect("create helper project dir");
    std::fs::write(project_dir.join("Spirectl.DotnetTools.csproj"), []).expect("write csproj");
    std::fs::write(project_dir.join("Program.cs"), []).expect("write program");
    project_dir
}

fn default_helper_build_output(dir: &Path) -> PathBuf {
    dir.join("dotnet-tools/src/Spirectl.DotnetTools/bin/Debug/net9.0/Spirectl.DotnetTools.dll")
}

fn write_recover_config(dir: &Path, assemblies_dir: &Path) {
    std::fs::write(
        dir.join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ngame:\n  path: auto\n  assembliesDir: {}\ntools:\n  dotnetToolsPath: {}\ntoolchain:\n  dir: {}\n",
            assemblies_dir.display(),
            repo_root()
                .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
                .display(),
            dir.join(".owned-toolchain").display(),
        ),
    )
    .expect("write config");
}

#[test]
fn toolchain_info_reports_default_roots_and_provenance() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);

    assert_eq!(
        payload["roots"]["toolchain"]["path"],
        dir.path().join(".sts2/toolchain").display().to_string()
    );
    assert_eq!(payload["roots"]["toolchain"]["provenance"], "default");
    assert_eq!(payload["roots"]["sharedCache"]["provenance"], "default");
    assert_eq!(
        payload["roots"]["profilesFile"]["path"],
        dir.path().join("sts2.profiles.yaml").display().to_string()
    );
    assert_eq!(payload["roots"]["profilesFile"]["provenance"], "default");
    assert_eq!(payload["tools"][0]["id"], "embedded-ilspy");
    let helper = dotnet_helper_tool(&payload);
    assert_eq!(helper["status"], "available");
    assert_eq!(helper["provenance"], "default");
    assert_eq!(helper["kind"], "helper-project");
    let helper_path = helper["path"].as_str().expect("helper path");
    assert!(
        helper_path.ends_with("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj"),
        "expected helper project path, got {helper_path}"
    );
    assert!(
        !helper_path.starts_with(&dir.path().display().to_string()),
        "default helper path should not be rooted in arbitrary caller cwd"
    );
    assert_eq!(
        helper["launch"]["configuredPath"],
        "dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj"
    );
    assert_eq!(helper["launch"]["resolvedPath"], helper["path"]);
    assert_eq!(helper["launch"]["launchKind"], "defaultProject");
    assert_eq!(
        helper["launch"]["buildOutputPath"],
        repo_root()
            .join("dotnet-tools/src/Spirectl.DotnetTools/bin/Debug/net9.0/Spirectl.DotnetTools.dll")
            .display()
            .to_string()
    );
    assert!(helper["launch"]["invocationPath"].is_string());
    assert!(helper["launch"]["available"].is_boolean());
    assert!(helper["launch"]["buildNeeded"].is_boolean());
    assert_eq!(
        helper["launch"]["available"]
            .as_bool()
            .expect("available bool"),
        !helper["launch"]["buildNeeded"]
            .as_bool()
            .expect("buildNeeded bool")
    );
    if helper["launch"]["buildNeeded"]
        .as_bool()
        .expect("buildNeeded bool")
    {
        assert!(
            matches!(
                helper["launch"]["buildReason"].as_str(),
                Some("defaultOutputMissing" | "defaultOutputStale")
            ),
            "expected missing or stale default helper build reason, got {}",
            helper["launch"]["buildReason"]
        );
    } else {
        assert_eq!(helper["launch"]["buildReason"], Value::Null);
    }
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("Default helper")
    );
}

#[test]
fn toolchain_info_reports_fresh_default_dll_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    create_default_helper_project_layout(dir.path());
    std::thread::sleep(Duration::from_millis(20));
    let helper_dll = default_helper_build_output(dir.path());
    std::fs::create_dir_all(helper_dll.parent().expect("helper dll parent"))
        .expect("create helper output dir");
    std::fs::write(&helper_dll, []).expect("write helper dll");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["launch"]["launchKind"], "defaultProject");
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(
        helper["launch"]["buildOutputPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(helper["launch"]["buildReason"], Value::Null);
}

#[test]
fn toolchain_info_reports_checked_in_default_project_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    create_default_helper_project_layout(dir.path());
    std::thread::sleep(Duration::from_millis(20));
    let helper_dll = default_helper_build_output(dir.path());
    std::fs::create_dir_all(helper_dll.parent().expect("helper dll parent"))
        .expect("create helper output dir");
    std::fs::write(&helper_dll, []).expect("write helper dll");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj\n",
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["provenance"], "checked-in-config");
    assert_eq!(
        helper["launch"]["configuredPath"],
        "dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj"
    );
    assert_eq!(
        helper["launch"]["resolvedPath"],
        dir.path()
            .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
            .display()
            .to_string()
    );
    assert_eq!(helper["launch"]["launchKind"], "defaultProject");
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(
        helper["launch"]["buildOutputPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(helper["launch"]["buildReason"], Value::Null);
    let note = helper["launch"]["notes"][0].as_str().expect("launch note");
    assert!(
        note.contains("Spirectl.DotnetTools.dll"),
        "expected note to name built helper DLL, got {note}"
    );
    assert!(
        !note.contains("dotnet run"),
        "expected default helper note to avoid dotnet run, got {note}"
    );
}

#[test]
fn toolchain_info_reports_missing_default_dll_build_needed_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    create_default_helper_project_layout(dir.path());
    let helper_dll = default_helper_build_output(dir.path());

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["launch"]["launchKind"], "defaultProject");
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(helper["launch"]["available"], false);
    assert_eq!(helper["launch"]["buildNeeded"], true);
    assert_eq!(helper["launch"]["buildReason"], "defaultOutputMissing");
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("missing")
    );
}

#[test]
fn toolchain_info_skips_stale_check_for_default_dll() {
    // `toolchain info` is a read-only report and never builds, so it resolves the default
    // helper launch lazily and intentionally skips the recursive staleness walk of
    // dotnet-tools/ (see `dotnet_helper::resolve_dotnet_helper_launch_lazy`); a stale DLL
    // relative to newer sources is reported via `staleChecked: false`, not `buildNeeded`.
    let dir = tempfile::tempdir().expect("temp dir");
    let project_dir = create_default_helper_project_layout(dir.path());
    let helper_dll = default_helper_build_output(dir.path());
    std::fs::create_dir_all(helper_dll.parent().expect("helper dll parent"))
        .expect("create helper output dir");
    std::fs::write(&helper_dll, []).expect("write helper dll");
    std::thread::sleep(Duration::from_millis(20));
    std::fs::write(project_dir.join("Program.cs"), "newer input").expect("touch program");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["launch"]["launchKind"], "defaultProject");
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(helper["launch"]["buildReason"], Value::Null);
    assert_eq!(helper["launch"]["staleChecked"], false);
    assert!(
        helper["launch"]["notes"][1]
            .as_str()
            .expect("launch note")
            .contains("staleness was not checked")
    );
}

#[test]
fn toolchain_info_reports_configured_dll_helper_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    let helper_dll = dir.path().join("helper.dll");
    std::fs::write(&helper_dll, []).expect("write helper dll");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: {}\n",
            helper_dll.display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["path"], helper_dll.display().to_string());
    assert_eq!(helper["provenance"], "checked-in-config");
    assert_eq!(
        helper["launch"]["configuredPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(
        helper["launch"]["resolvedPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_dll.display().to_string()
    );
    assert_eq!(helper["launch"]["launchKind"], "dll");
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(helper["launch"]["buildReason"], Value::Null);
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("launch directly")
    );
}

#[test]
fn toolchain_info_reports_configured_executable_helper_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    #[cfg(windows)]
    let helper_exe = dir.path().join("helper.exe");
    #[cfg(unix)]
    let helper_exe = write_executable_script(dir.path(), "helper", "#!/usr/bin/env bash\n");
    #[cfg(windows)]
    std::fs::write(&helper_exe, []).expect("write helper exe");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: {}\n",
            helper_exe.display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["path"], helper_exe.display().to_string());
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_exe.display().to_string()
    );
    assert_eq!(helper["launch"]["launchKind"], "executable");
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
}

#[test]
fn toolchain_info_reports_configured_project_helper_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    let helper_project = dir.path().join("Custom.Helper.csproj");
    std::fs::write(&helper_project, []).expect("write helper project");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: {}\n",
            helper_project.display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["path"], helper_project.display().to_string());
    assert_eq!(
        helper["launch"]["invocationPath"],
        helper_project.display().to_string()
    );
    assert_eq!(helper["launch"]["launchKind"], "project");
    assert_eq!(helper["launch"]["available"], true);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("dotnet run")
    );
}

#[test]
fn toolchain_info_reports_missing_helper_path_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    let missing_helper = dir.path().join("missing-helper.dll");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: {}\n",
            missing_helper.display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "missing");
    assert_eq!(helper["path"], missing_helper.display().to_string());
    assert_eq!(
        helper["launch"]["configuredPath"],
        missing_helper.display().to_string()
    );
    assert_eq!(
        helper["launch"]["resolvedPath"],
        missing_helper.display().to_string()
    );
    assert_eq!(helper["launch"]["invocationPath"], Value::Null);
    assert_eq!(helper["launch"]["launchKind"], "missing");
    assert_eq!(helper["launch"]["available"], false);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(
        helper["launch"]["failureReason"],
        "configured helper path does not exist"
    );
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("tools.dotnetToolsPath")
    );
}

#[test]
fn toolchain_info_reports_unsupported_helper_path_launch_diagnostics() {
    let dir = tempfile::tempdir().expect("temp dir");
    let helper_dir = dir.path().join("helper-dir");
    std::fs::create_dir_all(&helper_dir).expect("create helper dir");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ntools:\n  dotnetToolsPath: {}\n",
            helper_dir.display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);
    let helper = dotnet_helper_tool(&payload);

    assert_eq!(helper["status"], "available");
    assert_eq!(helper["path"], helper_dir.display().to_string());
    assert_eq!(helper["launch"]["invocationPath"], Value::Null);
    assert_eq!(helper["launch"]["launchKind"], "unsupported");
    assert_eq!(helper["launch"]["available"], false);
    assert_eq!(helper["launch"]["buildNeeded"], false);
    assert_eq!(
        helper["launch"]["failureReason"],
        "configured helper path must be a .csproj, .dll, or executable file"
    );
    assert!(
        helper["launch"]["notes"][0]
            .as_str()
            .expect("launch note")
            .contains("not a supported helper")
    );
}

#[test]
fn toolchain_info_distinguishes_checked_in_and_local_config_provenance() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
transport:
  kind: mock
toolchain:
  dir: /baseline/toolchain
project:
  profilesFile: /baseline/profiles.yaml
"#,
    )
    .expect("write baseline config");
    std::fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
toolchain:
  sharedCacheDir: /local/cache
tools:
  gdrePath: /local/gdre
"#,
    )
    .expect("write local config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);

    assert_eq!(
        payload["roots"]["toolchain"]["provenance"],
        "checked-in-config"
    );
    assert_eq!(
        payload["roots"]["profilesFile"]["provenance"],
        "checked-in-config"
    );
    assert_eq!(
        payload["roots"]["sharedCache"]["provenance"],
        "local-config"
    );
    assert_eq!(payload["tools"][2]["id"], "gdre");
    assert_eq!(payload["tools"][2]["provenance"], "local-config");
}

#[test]
fn toolchain_info_reports_configured_missing_gdre_with_explicit_failure_metadata() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
transport:
  kind: mock
tools:
  gdrePath: /missing/gdre
"#,
    )
    .expect("write baseline config");

    let payload = run_json_in_dir(dir.path(), &["--json", "toolchain", "info"]);

    assert_eq!(payload["tools"][2]["id"], "gdre");
    assert_eq!(payload["tools"][2]["status"], "missing");
    assert_eq!(payload["tools"][2]["failureCode"], "gdre_path_missing");
    assert_eq!(payload["tools"][2]["path"], "/missing/gdre");
    assert_eq!(payload["tools"][2]["provenance"], "checked-in-config");
    assert!(
        payload["tools"][2]["notes"][0]
            .as_str()
            .expect("gdre note")
            .contains("not found"),
        "expected note to explain missing gdre executable"
    );
}

#[test]
fn project_profile_list_reads_checked_in_profiles_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  install-bridge:
    description: Install bridge only.
    steps:
      - kind: install-bridge
  launch-and-attach:
    description: Launch then attach.
    steps:
      - kind: launch
      - kind: attach
"#,
    )
    .expect("write profiles");

    let payload = run_json_in_dir(dir.path(), &["--json", "project", "profile", "list"]);

    let profiles = payload["profiles"].as_array().expect("profiles array");
    assert_eq!(profiles.len(), 2);
    assert_eq!(profiles[0]["name"], "install-bridge");
    assert_eq!(
        profiles[0]["stepKinds"],
        serde_json::json!(["install-bridge"])
    );
    assert_eq!(profiles[1]["name"], "launch-and-attach");
}

#[test]
fn project_profile_show_resolves_relative_deploy_paths_from_profile_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    let config_dir = dir.path().join("config");
    let mod_dir = dir.path().join("mods/example");
    std::fs::create_dir_all(&config_dir).expect("create config dir");
    std::fs::create_dir_all(&mod_dir).expect("create mod dir");
    std::fs::write(
        config_dir.join("profiles.yaml"),
        r#"
profiles:
  deploy-local:
    description: Deploy a checked-in local mod.
    steps:
      - kind: deploy
        path: ../mods/example
"#,
    )
    .expect("write profiles");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\nproject:\n  profilesFile: {}\n",
            config_dir.join("profiles.yaml").display()
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "project", "profile", "show", "deploy-local"],
    );

    assert_eq!(payload["name"], "deploy-local");
    assert_eq!(
        payload["sourcePath"],
        config_dir.join("profiles.yaml").display().to_string()
    );
    assert_eq!(
        payload["profile"]["steps"][0]["path"],
        mod_dir.display().to_string()
    );
}

#[test]
fn project_profile_show_reports_optional_step_settings() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  tuned:
    description: Exercise optional lifecycle step fields.
    steps:
      - kind: launch
        timeoutMs: 111
        intervalMs: 7
      - kind: deploy
        path: ./MyMod
        build: true
        restart: true
        verify: true
        timeoutMs: 222
        intervalMs: 9
      - kind: attach
        timeoutMs: 333
        intervalMs: 11
"#,
    )
    .expect("write profiles");

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "project", "profile", "show", "tuned"],
    );

    let steps = payload["profile"]["steps"].as_array().expect("steps array");
    assert_eq!(steps[0]["timeoutMs"], 111);
    assert_eq!(steps[0]["intervalMs"], 7);
    assert_eq!(
        steps[1]["path"],
        dir.path().join("MyMod").display().to_string()
    );
    assert_eq!(steps[1]["build"], true);
    assert_eq!(steps[1]["restart"], true);
    assert_eq!(steps[1]["verify"], true);
    assert_eq!(steps[1]["timeoutMs"], 222);
    assert_eq!(steps[1]["intervalMs"], 9);
    assert_eq!(steps[2]["timeoutMs"], 333);
    assert_eq!(steps[2]["intervalMs"], 11);
}

#[test]
fn project_profile_show_renders_command_steps_with_resolved_cwd_and_env() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  build-logic:
    description: Build reloadable logic.
    steps:
      - kind: command
        command: dotnet
        args:
          - build
          - src/MyHotMod.Logic/MyHotMod.Logic.csproj
        cwd: .
        env:
          HotReloadDeployDir: ${config:game.modsDir}/MyHotMod.Shell/hot-reload
"#,
    )
    .expect("write profiles");
    std::fs::write(
        dir.path().join("sts2.local.yaml"),
        "game:\n  modsDir: /tmp/sts2/mods\n",
    )
    .expect("write local config");

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "project", "profile", "show", "build-logic"],
    );

    let step = &payload["profile"]["steps"][0];
    assert_eq!(step["kind"], "command");
    assert_eq!(step["command"], "dotnet");
    assert_eq!(
        step["args"],
        serde_json::json!(["build", "src/MyHotMod.Logic/MyHotMod.Logic.csproj"])
    );
    assert_eq!(step["cwd"], dir.path().display().to_string());
    assert_eq!(
        step["env"]["HotReloadDeployDir"],
        "/tmp/sts2/mods/MyHotMod.Shell/hot-reload"
    );
}

#[cfg(unix)]
#[test]
fn project_profile_run_executes_command_step() {
    let dir = tempfile::tempdir().expect("temp dir");
    let log_path = dir.path().join("command.log");
    let script = write_executable_script(
        dir.path(),
        "record-command.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf 'cwd=%s\\narg=%s\\nenv=%s\\n' \"$PWD\" \"$1\" \"$HOT_RELOAD_DEPLOY_DIR\" > {log_path:?}\n",
        ),
    );
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        format!(
            "profiles:\n  run-command:\n    steps:\n      - kind: command\n        command: {}\n        args:\n          - ok\n        env:\n          HOT_RELOAD_DEPLOY_DIR: .sts2/hot-reload\n",
            script.display()
        ),
    )
    .expect("write profiles");

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "project", "profile", "run", "run-command"],
    );

    assert_eq!(payload["steps"][0]["kind"], "command");
    assert_eq!(payload["steps"][0]["status"], "ok");
    let log = std::fs::read_to_string(log_path).expect("read log");
    assert!(log.contains("arg=ok"));
    assert!(log.contains("env=.sts2/hot-reload"));
}

#[test]
fn project_scaffold_rejects_invalid_mod_id() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output = run_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            "mods/MyHotMod",
            "--mod-id",
            "Bad--Mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyHotMod",
        ],
    );

    assert_eq!(output.status.code(), Some(2));
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(payload["error"]["code"], "project_scaffold_invalid_mod_id");
    assert_eq!(payload["error"]["field"], "modId");
}

#[test]
fn project_scaffold_rejects_invalid_namespace() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output = run_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            "mods/MyHotMod",
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "123.Bad",
        ],
    );

    assert_eq!(output.status.code(), Some(2));
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(
        payload["error"]["code"],
        "project_scaffold_invalid_namespace"
    );
    assert_eq!(payload["error"]["field"], "namespace");
}

#[test]
fn project_scaffold_stable_harmony_writes_expected_layout() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output_dir = dir.path().join("MyHotMod");

    let payload = run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            output_dir.to_str().expect("utf8 path"),
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyHotMod",
        ],
    );

    assert_eq!(payload["template"], "stable-harmony-trampoline");
    assert_eq!(payload["outputPath"], output_dir.display().to_string());
    assert!(output_dir.join("README.md").is_file());
    assert!(output_dir.join("sts2.profiles.yaml").is_file());
    assert!(output_dir.join("sts2.local.example.yaml").is_file());
    assert!(output_dir.join("MyHotMod.sln").is_file());
    assert!(
        output_dir
            .join("src/MyHotMod.Shell/MyHotMod.Shell.csproj")
            .is_file()
    );
    assert!(
        output_dir
            .join("src/MyHotMod.Contracts/MyHotMod.Contracts.csproj")
            .is_file()
    );
    assert!(
        output_dir
            .join("src/MyHotMod.Logic/MyHotMod.Logic.csproj")
            .is_file()
    );
    assert!(
        output_dir
            .join("tests/MyHotMod.ReloadTests/MyHotMod.ReloadTests.csproj")
            .is_file()
    );
    assert!(!output_dir.join("src/MyHotMod.Shell/obj").exists());

    let mod_entry = std::fs::read_to_string(output_dir.join("src/MyHotMod.Shell/ModEntry.cs"))
        .expect("mod entry");
    assert!(mod_entry.contains("namespace MyHotMod.Shell;"));
    assert!(mod_entry.contains("com.spirectl.hotmod.my.hot.mod"));

    let logic =
        std::fs::read_to_string(output_dir.join("src/MyHotMod.Logic/HotLogic.cs")).expect("logic");
    assert!(logic.contains("using MyHotMod.Contracts;"));
    assert!(logic.contains("\"my-hot-mod\""));
    assert!(logic.contains("\"My Hot Mod\""));

    let created = payload["createdFiles"].as_array().expect("created files");
    assert!(created.iter().any(|path| path == "README.md"));
    assert!(created.iter().any(|path| path == "sts2.hot-reload.yaml"));
    assert!(
        payload["profiles"]
            .as_array()
            .expect("profiles")
            .iter()
            .any(|name| name == "my-hot-mod-logic-build")
    );

    let profiles =
        std::fs::read_to_string(output_dir.join("sts2.profiles.yaml")).expect("profiles");
    assert!(profiles.contains("my-hot-mod-shell-build:"));
    assert!(profiles.contains("kind: command"));
    assert!(profiles.contains("command: dotnet"));
    assert!(profiles.contains("my-hot-mod-shell-deploy:"));
    assert!(profiles.contains("kind: deploy"));
    assert!(profiles.contains("path: src/MyHotMod.Shell"));
    assert!(profiles.contains("my-hot-mod-shell-deploy-restart:"));
    assert!(profiles.contains("restart: true"));
    assert!(profiles.contains("verify: true"));
    assert!(profiles.contains("my-hot-mod-logic-build:"));
    assert!(
        profiles.contains("-p:HotReloadDeployDir=${config:game.modsDir}/MyHotMod.Shell/hot-reload")
    );
    assert!(profiles.contains("my-hot-mod-clean-reload-artifacts:"));

    let hot_reload =
        std::fs::read_to_string(output_dir.join("sts2.hot-reload.yaml")).expect("hot reload yaml");
    assert!(hot_reload.contains("schemaVersion: spirectl.hot-reload-project/v0"));
    assert!(hot_reload.contains("projectId: my-hot-mod"));
    assert!(hot_reload.contains("shellModId: my-hot-mod"));
    assert!(hot_reload.contains("shellProject: MyHotMod.Shell"));
    assert!(hot_reload.contains("logicProject: MyHotMod.Logic"));
    assert!(hot_reload.contains("logicBuildProfile: my-hot-mod-logic-build"));
    assert!(hot_reload.contains(
        "logicArtifactPath: ${config:game.modsDir}/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll"
    ));
    assert!(hot_reload.contains("expectedContractVersion: 0"));
    assert!(hot_reload.contains("id: spirectl.m57.hot-reload-shell"));
    assert!(hot_reload.contains("version: 0"));

    let logic_project =
        std::fs::read_to_string(output_dir.join("src/MyHotMod.Logic/MyHotMod.Logic.csproj"))
            .expect("logic project");
    assert!(logic_project.contains("Target Name=\"CopyHotReloadLogic\""));
    assert!(logic_project.contains("Target Name=\"CleanHotReloadArtifacts\""));
    assert!(logic_project.contains(".sts2/hot-reload/my-hot-mod"));

    let fixture_builder = std::fs::read_to_string(
        output_dir.join("tests/MyHotMod.ReloadTests/FixtureLogicBuilder.cs"),
    )
    .expect("fixture builder");
    assert!(fixture_builder.contains("\"MyHotMod.sln\""));
    assert!(!fixture_builder.contains("MyHotMod.Template.sln"));
}

#[test]
fn project_scaffold_can_enable_hot_reload_guardrails() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output_dir = dir.path().join("MyHotMod");

    let payload = run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            output_dir.to_str().expect("utf8 path"),
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyHotMod",
            "--enable-guardrails",
        ],
    );

    assert_eq!(payload["guardrails"]["enabled"], true);
    assert!(
        output_dir
            .join("src/MyHotMod.Guardrails/MyHotMod.Guardrails.csproj")
            .is_file()
    );
    let props =
        std::fs::read_to_string(output_dir.join("Directory.Build.props")).expect("directory props");
    assert!(props.contains("<EnableHotReloadGuardrails Condition=\"'$(EnableHotReloadGuardrails)' == ''\">true</EnableHotReloadGuardrails>"));
    let local_example =
        std::fs::read_to_string(output_dir.join("sts2.local.example.yaml")).expect("local example");
    assert!(local_example.contains("MY_HOT_MOD_DEV_OVERLAY: \"0\""));
}

#[test]
fn project_scaffold_keeps_project_paths_assembly_based_when_namespace_differs() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output_dir = dir.path().join("MyHotMod");

    run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            output_dir.to_str().expect("utf8 path"),
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyCompany.MyHotMod",
            "--assembly-prefix",
            "MyHotMod",
        ],
    );

    assert!(
        output_dir
            .join("src/MyHotMod.Shell/MyHotMod.Shell.csproj")
            .is_file()
    );
    assert!(
        !output_dir
            .join("src/MyCompany.MyHotMod.Shell/MyCompany.MyHotMod.Shell.csproj")
            .exists()
    );

    let solution = std::fs::read_to_string(output_dir.join("MyHotMod.sln")).expect("solution");
    assert!(
        solution.contains(
            r#""MyHotMod.Contracts", "src\MyHotMod.Contracts\MyHotMod.Contracts.csproj""#
        )
    );
    assert!(solution.contains(r#""MyHotMod.Shell", "src\MyHotMod.Shell\MyHotMod.Shell.csproj""#));
    assert!(!solution.contains("src\\MyCompany.MyHotMod."));

    let shell_project =
        std::fs::read_to_string(output_dir.join("src/MyHotMod.Shell/MyHotMod.Shell.csproj"))
            .expect("shell project");
    assert!(shell_project.contains(
        r#"<ProjectReference Include="..\MyHotMod.Contracts\MyHotMod.Contracts.csproj" />"#
    ));
    assert!(!shell_project.contains("..\\MyCompany.MyHotMod.Contracts"));

    let logic =
        std::fs::read_to_string(output_dir.join("src/MyHotMod.Logic/HotLogic.cs")).expect("logic");
    assert!(logic.contains("using MyCompany.MyHotMod.Contracts;"));
    assert!(logic.contains("namespace MyCompany.MyHotMod.Logic;"));

    let patch_boundary = std::fs::read_to_string(
        output_dir.join("tests/MyHotMod.ReloadTests/PatchBoundaryTests.cs"),
    )
    .expect("patch boundary tests");
    assert!(patch_boundary.contains(r#""MyCompany.MyHotMod.Shell.HarmonyPatches""#));
    assert!(patch_boundary.contains(r#""MyHotMod.Logic""#));
}

#[test]
fn project_scaffold_refuses_existing_non_empty_directory() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output_dir = dir.path().join("MyHotMod");
    std::fs::create_dir_all(&output_dir).expect("create output");
    std::fs::write(output_dir.join("keep.txt"), "existing").expect("write existing");

    let output = run_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            output_dir.to_str().expect("utf8 path"),
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyHotMod",
        ],
    );

    assert!(!output.status.success());
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json");
    assert_eq!(
        payload["error"]["code"],
        "project_scaffold_output_not_empty"
    );
    assert_eq!(payload["error"]["path"], output_dir.display().to_string());
    assert_eq!(payload["error"]["resolutions"]["forceEmpty"], false);
    assert_eq!(
        payload["error"]["resolutions"]["differentOutputDirectory"],
        true
    );
    assert_eq!(
        std::fs::read_to_string(output_dir.join("keep.txt")).expect("existing"),
        "existing"
    );
}

#[test]
fn generated_scaffold_profiles_resolve_relative_paths_from_scaffold_root() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output_dir = dir.path().join("MyHotMod");
    run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "scaffold",
            "stable-harmony-trampoline",
            "--output",
            output_dir.to_str().expect("utf8 path"),
            "--mod-id",
            "my-hot-mod",
            "--name",
            "My Hot Mod",
            "--namespace",
            "MyHotMod",
        ],
    );
    std::fs::write(
        output_dir.join("sts2.local.yaml"),
        "game:\n  modsDir: /tmp/sts2/mods\n",
    )
    .expect("write local config");

    let payload = run_json_in_dir(
        &output_dir,
        &[
            "--json",
            "project",
            "profile",
            "show",
            "my-hot-mod-shell-deploy",
        ],
    );

    assert_eq!(
        payload["profile"]["steps"][0]["path"],
        output_dir.join("src/MyHotMod.Shell").display().to_string()
    );
}

#[test]
fn project_recover_decompile_writes_owned_output_and_manifest() {
    let dir = tempfile::tempdir().expect("temp dir");
    let assemblies_dir = fixture_assemblies_dir();
    write_recover_config(dir.path(), assemblies_dir.path());

    let payload = run_json_in_dir(
        dir.path(),
        &["--json", "project", "recover", "--kind", "decompile"],
    );

    let output_dir = dir.path().join(".owned-toolchain/decompile");
    let manifest_path = dir.path().join(".owned-toolchain/manifests/decompile.json");

    assert_eq!(payload["results"][0]["kind"], "decompile");
    assert_eq!(payload["results"][0]["status"], "ok");
    assert_eq!(
        payload["results"][0]["outputPath"],
        output_dir.display().to_string()
    );
    assert!(output_dir.is_dir(), "expected decompile output dir");
    assert!(manifest_path.is_file(), "expected decompile manifest");
}

#[test]
fn project_recover_all_preserves_partial_success_and_exits_nonzero_when_gdre_is_unavailable() {
    let dir = tempfile::tempdir().expect("temp dir");
    let assemblies_dir = fixture_assemblies_dir();
    write_recover_config(dir.path(), assemblies_dir.path());

    let output = run_in_dir(
        dir.path(),
        &["--json", "project", "recover", "--kind", "all"],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    let results = payload["results"].as_array().expect("results array");
    assert_eq!(results[0]["kind"], "decompile");
    assert_eq!(results[0]["status"], "ok");
    assert_eq!(results[1]["kind"], "recovered-project");
    assert_eq!(results[1]["status"], "error");
    assert_eq!(results[1]["failureCode"], "gdre_path_unset");
    assert!(
        dir.path().join(".owned-toolchain/decompile").is_dir(),
        "expected partial success on disk"
    );
}

#[test]
fn project_recover_reports_configured_missing_gdre_distinctly() {
    let dir = tempfile::tempdir().expect("temp dir");
    let assemblies_dir = fixture_assemblies_dir();
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ngame:\n  path: auto\n  assembliesDir: {}\ntools:\n  dotnetToolsPath: {}\n  gdrePath: /missing/gdre\ntoolchain:\n  dir: {}\n",
            assemblies_dir.path().display(),
            repo_root()
                .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
                .display(),
            dir.path().join(".owned-toolchain").display(),
        ),
    )
    .expect("write config");

    let output = run_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "recover",
            "--kind",
            "recovered-project",
        ],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    let results = payload["results"].as_array().expect("results array");
    assert_eq!(results.len(), 1);
    assert_eq!(results[0]["kind"], "recovered-project");
    assert_eq!(results[0]["status"], "error");
    assert_eq!(results[0]["failureCode"], "gdre_path_missing");
    assert_eq!(results[0]["path"], "/missing/gdre");
    assert!(
        results[0]["notes"][0]
            .as_str()
            .expect("gdre note")
            .contains("not found"),
        "expected note to explain missing gdre executable"
    );
}

#[cfg(unix)]
#[test]
fn project_recover_recovered_project_runs_gdre_and_writes_manifest() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = create_fake_game_layout();
    let log_path = dir.path().join("gdre-args.log");
    let gdre = write_executable_script(
        dir.path(),
        "fake-gdre.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nlog_path={log_path:?}\nif [ \"$2\" = \"--gdre-version\" ]; then\n  echo \"gdre-test-1.0\"\n  exit 0\nfi\nprintf '%s\\n' \"$@\" > \"$log_path\"\noutput_dir=\"\"\nfor arg in \"$@\"; do\n  case \"$arg\" in\n    --output=*) output_dir=\"${{arg#--output=}}\" ;;\n  esac\ndone\nmkdir -p \"$output_dir\"\nprintf 'project_name=\"Recovered\"\\n' > \"$output_dir/project.godot\"\n",
        ),
    );
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\ngame:\n  path: {}\ntools:\n  dotnetToolsPath: {}\n  gdrePath: {}\ntoolchain:\n  dir: {}\n",
            game_dir.path().display(),
            repo_root()
                .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
                .display(),
            gdre.display(),
            dir.path().join(".owned-toolchain").display(),
        ),
    )
    .expect("write config");

    let payload = run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "recover",
            "--kind",
            "recovered-project",
        ],
    );

    let output_dir = dir.path().join(".owned-toolchain/recovered-project");
    let manifest_path = dir
        .path()
        .join(".owned-toolchain/manifests/recovered-project.json");
    assert_eq!(payload["results"][0]["kind"], "recovered-project");
    assert_eq!(payload["results"][0]["status"], "ok");
    assert_eq!(
        payload["results"][0]["outputPath"],
        output_dir.display().to_string()
    );
    assert!(output_dir.join("project.godot").is_file());
    assert!(manifest_path.is_file());

    let gdre_args = std::fs::read_to_string(&log_path).expect("read gdre args");
    assert!(gdre_args.contains("--headless"));
    assert!(gdre_args.contains(&format!(
        "--recover={}",
        game_dir.path().join("SlayTheSpire2.pck").display()
    )));
    assert!(gdre_args.contains(&format!("--output={}", output_dir.display())));

    let manifest: Value =
        serde_json::from_slice(&std::fs::read(manifest_path).expect("read manifest"))
            .expect("manifest json");
    assert_eq!(manifest["kind"], "recovered-project");
    assert_eq!(manifest["backend"]["id"], "gdre");
    assert_eq!(manifest["backend"]["version"], "gdre-test-1.0");
}

#[test]
fn project_profile_run_reports_failed_step_and_stops() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  attach-only:
    description: Attempt attach under mock transport.
    steps:
      - kind: attach
"#,
    )
    .expect("write profiles");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\n",
    )
    .expect("write config");

    let output = run_in_dir(
        dir.path(),
        &["--json", "project", "profile", "run", "attach-only"],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(payload["name"], "attach-only");
    assert_eq!(payload["steps"][0]["kind"], "attach");
    assert_eq!(payload["steps"][0]["status"], "error");
    assert_eq!(
        payload["steps"][0]["error"]["code"],
        "invalid_lifecycle_config"
    );
}

#[test]
fn hot_reload_missing_metadata_returns_project_not_found() {
    let dir = tempfile::tempdir().expect("temp dir");

    let output = run_in_dir(
        dir.path(),
        &["--json", "dev", "mod-reload", "--project", "."],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "hot_reload_project_not_found");
    assert_eq!(
        payload["error"]["metadataFile"],
        dir.path()
            .join("sts2.hot-reload.yaml")
            .display()
            .to_string()
    );
}

#[test]
fn hot_reload_build_missing_profile_returns_profile_missing() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.hot-reload.yaml"),
        r#"
schemaVersion: spirectl.hot-reload-project/v0
projectId: my-hot-mod
shellModId: my-hot-mod
shellProject: MyHotMod.Shell
logicProject: MyHotMod.Logic
logicBuildProfile: my-hot-mod-logic-build
logicArtifactPath: ${profileDir}/out/MyHotMod.Logic.dll
expectedContractVersion: 0
protocol:
  id: spirectl.m57.hot-reload-shell
  version: 0
"#,
    )
    .expect("write hot reload metadata");
    std::fs::write(dir.path().join("sts2.profiles.yaml"), "profiles: {}\n")
        .expect("write profiles");

    let output = run_in_dir(
        dir.path(),
        &["--json", "dev", "mod-reload", "--project", ".", "--build"],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(output.status.code(), Some(2));
    assert_eq!(payload["error"]["code"], "hot_reload_profile_missing");
    assert_eq!(payload["error"]["profile"], "my-hot-mod-logic-build");
}

#[cfg(unix)]
#[test]
fn hot_reload_build_failure_returns_build_failed() {
    let dir = tempfile::tempdir().expect("temp dir");
    let script = write_executable_script(
        dir.path(),
        "fail-build.sh",
        "#!/usr/bin/env bash\nprintf 'nope\\n' >&2\nexit 7\n",
    );
    std::fs::write(
        dir.path().join("sts2.hot-reload.yaml"),
        r#"
schemaVersion: spirectl.hot-reload-project/v0
projectId: my-hot-mod
shellModId: my-hot-mod
shellProject: MyHotMod.Shell
logicProject: MyHotMod.Logic
logicBuildProfile: my-hot-mod-logic-build
logicArtifactPath: ${profileDir}/out/MyHotMod.Logic.dll
expectedContractVersion: 0
protocol:
  id: spirectl.m57.hot-reload-shell
  version: 0
"#,
    )
    .expect("write hot reload metadata");
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        format!(
            "profiles:\n  my-hot-mod-logic-build:\n    steps:\n      - kind: command\n        command: {}\n",
            script.display()
        ),
    )
    .expect("write profiles");

    let output = run_in_dir(
        dir.path(),
        &["--json", "dev", "mod-reload", "--project", ".", "--build"],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(output.status.code(), Some(4));
    assert_eq!(payload["error"]["code"], "hot_reload_build_failed");
    assert_eq!(payload["error"]["profile"], "my-hot-mod-logic-build");
    assert_eq!(payload["error"]["build"]["steps"][0]["status"], "error");
}

#[test]
fn hot_reload_unsupported_mock_bridge_returns_shell_unsupported() {
    let dir = tempfile::tempdir().expect("temp dir");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\n",
    )
    .expect("write mock config");
    std::fs::write(
        dir.path().join("sts2.hot-reload.yaml"),
        r#"
schemaVersion: spirectl.hot-reload-project/v0
projectId: my-hot-mod
shellModId: my-hot-mod
shellProject: MyHotMod.Shell
logicProject: MyHotMod.Logic
logicBuildProfile: my-hot-mod-logic-build
logicArtifactPath: ${profileDir}/out/MyHotMod.Logic.dll
expectedContractVersion: 0
protocol:
  id: spirectl.m57.hot-reload-shell
  version: 0
"#,
    )
    .expect("write hot reload metadata");

    let output = run_in_dir(
        dir.path(),
        &["--json", "dev", "mod-reload", "--project", "."],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(output.status.code(), Some(3));
    assert_eq!(payload["error"]["code"], "hot_reload_shell_unsupported");
    assert_eq!(payload["error"]["shell"]["supported"], false);
}

#[test]
fn project_profile_run_uses_step_specific_wait_settings() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = create_fake_game_layout();
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind tcp listener");
    let tcp_address = listener.local_addr().expect("listener addr");
    drop(listener);
    std::fs::write(
        dir.path().join("sts2.profiles.yaml"),
        r#"
profiles:
  attach-only:
    steps:
      - kind: attach
        timeoutMs: 1
        intervalMs: 1
"#,
    )
    .expect("write profiles");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: {}\ntransport:\n  kind: tcp\n  tcpAddress: {}\n",
            game_dir.path().display(),
            tcp_address
        ),
    )
    .expect("write config");

    let output = run_in_dir(
        dir.path(),
        &["--json", "project", "profile", "run", "attach-only"],
    );

    assert!(!output.status.success(), "expected nonzero exit");
    let payload: Value = serde_json::from_slice(&output.stdout).expect("json output");
    assert_eq!(payload["steps"][0]["kind"], "attach");
    assert_eq!(payload["steps"][0]["status"], "error");
    assert_eq!(payload["steps"][0]["error"]["code"], "attach_timeout");
    assert_eq!(payload["steps"][0]["error"]["timeoutMs"], 1);
    assert_eq!(payload["steps"][0]["error"]["intervalMs"], 1);
}

#[cfg(unix)]
#[test]
fn project_hook_list_and_show_read_checked_in_hooks_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    let config_dir = dir.path().join("config");
    let scripts_dir = dir.path().join("scripts");
    let work_dir = dir.path().join("hook-workdir");
    std::fs::create_dir_all(&config_dir).expect("config dir");
    std::fs::create_dir_all(&scripts_dir).expect("scripts dir");
    std::fs::create_dir_all(&work_dir).expect("work dir");
    let hook_script = write_executable_script(
        &scripts_dir,
        "noop.sh",
        "#!/usr/bin/env bash\nprintf '{\"output\":{\"status\":\"ok\"}}\\n'\n",
    );
    std::fs::write(
        config_dir.join("hooks.yaml"),
        format!(
            "hooks:\n  repo-check:\n    description: Verify repo-local extension.\n    command: ../scripts/{}\n    cwd: ../hook-workdir\n    env:\n      MODE: check\n",
            hook_script.file_name().expect("file name").to_string_lossy()
        ),
    )
    .expect("write hooks");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        format!(
            "transport:\n  kind: mock\nproject:\n  hooksFile: {}\n",
            config_dir.join("hooks.yaml").display()
        ),
    )
    .expect("write config");

    let list = run_json_in_dir(dir.path(), &["--json", "project", "hook", "list"]);
    let hooks = list["hooks"].as_array().expect("hooks array");
    assert_eq!(hooks.len(), 1);
    assert_eq!(hooks[0]["name"], "repo-check");
    assert_eq!(hooks[0]["description"], "Verify repo-local extension.");

    let show = run_json_in_dir(
        dir.path(),
        &["--json", "project", "hook", "show", "repo-check"],
    );
    assert_eq!(show["name"], "repo-check");
    assert_eq!(
        show["sourcePath"],
        config_dir.join("hooks.yaml").display().to_string()
    );
    assert_eq!(show["hook"]["command"], hook_script.display().to_string());
    assert_eq!(show["hook"]["cwd"], work_dir.display().to_string());
    assert_eq!(show["hook"]["env"]["MODE"], "check");
}

#[cfg(unix)]
#[test]
fn project_hook_run_passes_json_stdin_and_returns_declared_artifacts() {
    let dir = tempfile::tempdir().expect("temp dir");
    let scripts_dir = dir.path().join("scripts");
    let work_dir = dir.path().join("hook-workdir");
    std::fs::create_dir_all(&scripts_dir).expect("scripts dir");
    std::fs::create_dir_all(&work_dir).expect("work dir");
    let input_path = dir.path().join("hook-input.json");
    let artifact_path = dir.path().join("hook-artifact.txt");
    let hook_script = write_hook_script(&scripts_dir, &input_path, &artifact_path);
    std::fs::write(
        dir.path().join("sts2.hooks.yaml"),
        format!(
            "hooks:\n  repo-check:\n    description: Verify repo-local extension.\n    command: {}\n    cwd: {}\n",
            hook_script.display(),
            work_dir.display()
        ),
    )
    .expect("write hooks");
    std::fs::write(
        dir.path().join("sts2.config.yaml"),
        "transport:\n  kind: mock\n",
    )
    .expect("write config");

    let payload = run_json_in_dir(
        dir.path(),
        &[
            "--json",
            "project",
            "hook",
            "run",
            "repo-check",
            "--input",
            "{\"kind\":\"smoke\"}",
        ],
    );

    let stdin_payload: Value =
        serde_json::from_slice(&std::fs::read(&input_path).expect("read hook stdin"))
            .expect("stdin json");
    assert_eq!(stdin_payload["hookName"], "repo-check");
    assert_eq!(stdin_payload["input"]["kind"], "smoke");
    assert_eq!(payload["name"], "repo-check");
    assert_eq!(payload["result"]["output"]["status"], "ok");
    assert_eq!(
        payload["result"]["artifacts"][0]["path"],
        artifact_path.display().to_string()
    );
    assert_eq!(
        payload["result"]["output"]["cwd"],
        work_dir.display().to_string()
    );
}
