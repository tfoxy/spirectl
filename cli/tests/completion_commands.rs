use std::fs;
use std::path::Path;
use std::process::Command;

use serde_json::Value;

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn run_in_dir_with_env(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
) -> std::process::Output {
    Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .envs(envs.iter().copied())
        .output()
        .expect("run sts2 binary")
}

fn run_json_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> Value {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("json output")
}

fn run_stdout_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> String {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).expect("utf8 stdout")
}

fn run_error_json_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> Value {
    let output = run_in_dir_with_env(workdir, args, envs);
    assert!(
        !output.status.success(),
        "expected failure, got status {:?}, stdout: {}, stderr: {}",
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

fn activation_strategies(payload: &Value) -> &[Value] {
    payload["activationStrategies"]
        .as_array()
        .expect("activationStrategies array")
}

fn activation_strategy<'a>(payload: &'a Value, id: &str) -> &'a Value {
    activation_strategies(payload)
        .iter()
        .find(|strategy| strategy["id"] == id)
        .expect("activation strategy")
}

#[test]
fn completion_bash_prints_generated_script() {
    let dir = tempfile::tempdir().expect("temp dir");

    let stdout = run_stdout_in_dir_with_env(dir.path(), &["completion", "bash"], &[]);

    assert!(stdout.contains("_sts2()"));
    assert!(stdout.contains("complete -F _sts2"));
    assert!(stdout.contains("fixture"));
    // The presentation/catalog machinery is gone: no `presentation` group at any level.
    assert!(!stdout.contains("creature-bounds"));
    assert!(!stdout.contains("presentation__subcmd__render"));
    assert!(!stdout.contains("render-snapshot"));
    assert!(!stdout.contains("checkpoint"));
    assert!(!stdout.contains("load-fixture"));
}

#[test]
fn completion_help_describes_generation_and_install_commands() {
    let dir = tempfile::tempdir().expect("temp dir");

    let stdout = run_stdout_in_dir_with_env(dir.path(), &["completion", "--help"], &[]);

    assert!(stdout.contains("Generate shell completion text"));
    assert!(stdout.contains("install     Install shell completion"));
}

#[test]
fn completion_install_help_describes_shell_and_path_arguments() {
    let dir = tempfile::tempdir().expect("temp dir");

    let stdout = run_stdout_in_dir_with_env(dir.path(), &["completion", "install", "--help"], &[]);

    assert!(stdout.contains("[SHELL]  Shell to generate or install completion for"));
    assert!(stdout.contains("--path <PATH>"));
    assert!(
        stdout.contains("Override the output path instead of using the managed per-user default")
    );
}

#[test]
fn completion_json_auto_detects_shell_from_shell_env() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion"],
        &[("SHELL", "/usr/bin/fish")],
    );

    assert_eq!(payload["shell"], "fish");
    assert_eq!(payload["source"], "clap-complete");
    assert!(
        payload["script"]
            .as_str()
            .expect("script string")
            .contains("complete -c sts2")
    );
}

#[test]
fn completion_install_inspect_summary_mentions_managed_and_explicit_paths() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir_with_env(dir.path(), &["--json", "inspect", "commands"], &[]);

    assert_eq!(
        command_summary(&payload, "completion install"),
        "Install shell completion text into a spirectl-managed per-user path or an explicit file path and print activation metadata."
    );
}

#[test]
fn completion_json_auto_detects_powershell_from_psmodulepath_without_shell_env() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion"],
        &[
            ("SHELL", ""),
            (
                "PSModulePath",
                "C:\\Users\\tester\\Documents\\PowerShell\\Modules",
            ),
        ],
    );

    assert_eq!(payload["shell"], "powershell");
    assert_eq!(payload["shellSource"], "detected");
    assert!(
        payload["script"]
            .as_str()
            .expect("script string")
            .contains("Register-ArgumentCompleter")
    );
}

#[test]
fn completion_json_auto_detects_powershell_from_uppercase_exe_shell_path() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion"],
        &[("SHELL", "/Program Files/PowerShell/PwSh.EXE")],
    );

    assert_eq!(payload["shell"], "powershell");
    assert_eq!(payload["shellSource"], "detected");
    assert!(
        payload["script"]
            .as_str()
            .expect("script string")
            .contains("Register-ArgumentCompleter")
    );
}

#[test]
fn completion_install_writes_managed_file_and_reports_activation_command() {
    let dir = tempfile::tempdir().expect("temp dir");
    let xdg_config_home = dir.path().join("xdg-config");
    let home = dir.path().join("home");
    fs::create_dir_all(&xdg_config_home).expect("create xdg config home");
    fs::create_dir_all(home.join(".zfunc")).expect("create zsh fpath dir");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install"],
        &[
            ("SHELL", "/bin/zsh"),
            ("HOME", home.to_string_lossy().as_ref()),
            ("FPATH", home.join(".zfunc").to_string_lossy().as_ref()),
            (
                "XDG_CONFIG_HOME",
                xdg_config_home.to_string_lossy().as_ref(),
            ),
        ],
    );

    let installed_path = xdg_config_home.join("spirectl/completions/sts2.zsh");
    assert_eq!(payload["shell"], "zsh");
    assert_eq!(payload["path"], installed_path.to_string_lossy().as_ref());
    assert_eq!(
        payload["activationCommand"],
        format!("source {}", installed_path.display())
    );
    assert_eq!(
        activation_strategies(&payload)[0]["command"],
        payload["activationCommand"]
    );

    let source_strategy = activation_strategy(&payload, "source");
    assert_eq!(
        source_strategy["summary"],
        "Load the installed completion file in the current shell."
    );
    assert_eq!(source_strategy["loadsOnDemand"], false);

    let zsh_fpath_strategy = activation_strategy(&payload, "zsh-fpath");
    assert_eq!(zsh_fpath_strategy["loadsOnDemand"], true);
    assert_eq!(
        zsh_fpath_strategy["command"],
        format!(
            "mkdir -p {} && ln -sf {} {}",
            home.join(".zfunc").display(),
            installed_path.display(),
            home.join(".zfunc/_sts2").display()
        )
    );
    assert!(
        zsh_fpath_strategy["notes"]
            .as_array()
            .expect("notes array")
            .iter()
            .any(|note| note == "The target directory must already be present in $fpath.")
    );
    assert!(
        zsh_fpath_strategy["notes"]
            .as_array()
            .expect("notes array")
            .iter()
            .any(|note| note
                == "Your shell startup must run compinit for autoloaded completions to activate.")
    );

    let script = fs::read_to_string(&installed_path).expect("installed completion");
    assert!(script.contains("#compdef sts2"));
    assert!(script.contains("compdef _sts2 sts2"));
}

#[test]
fn completion_install_ignores_empty_xdg_config_home() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home = dir.path().join("home");
    fs::create_dir_all(&home).expect("create home");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install", "bash"],
        &[
            ("SHELL", "/bin/zsh"),
            ("HOME", home.to_string_lossy().as_ref()),
            ("XDG_CONFIG_HOME", ""),
        ],
    );

    let installed_path = home.join(".config/spirectl/completions/sts2.bash");
    let bash_completion_path = home.join(".local/share/bash-completion/completions/sts2.bash");
    assert_eq!(payload["shell"], "bash");
    assert_eq!(payload["path"], installed_path.to_string_lossy().as_ref());

    let bash_completion_strategy = activation_strategy(&payload, "bash-completion-user-dir");
    assert_eq!(bash_completion_strategy["loadsOnDemand"], true);
    assert_eq!(
        bash_completion_strategy["command"],
        format!(
            "mkdir -p {} && ln -sf {} {}",
            bash_completion_path.parent().expect("parent").display(),
            installed_path.display(),
            bash_completion_path.display()
        )
    );

    let script = fs::read_to_string(&installed_path).expect("installed completion");
    assert!(script.contains("complete -F _sts2"));
}

#[test]
fn completion_install_ignores_relative_xdg_config_home() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home = dir.path().join("home");
    fs::create_dir_all(&home).expect("create home");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install", "bash"],
        &[
            ("SHELL", "/bin/zsh"),
            ("HOME", home.to_string_lossy().as_ref()),
            ("XDG_CONFIG_HOME", "relative-xdg"),
        ],
    );

    let installed_path = home.join(".config/spirectl/completions/sts2.bash");
    assert_eq!(payload["shell"], "bash");
    assert_eq!(payload["path"], installed_path.to_string_lossy().as_ref());
    assert_eq!(
        payload["managedDir"],
        home.join(".config/spirectl/completions")
            .to_string_lossy()
            .as_ref()
    );

    let script = fs::read_to_string(&installed_path).expect("installed completion");
    assert!(script.contains("complete -F _sts2"));
}

#[test]
fn completion_install_without_absolute_managed_home_returns_structured_error() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload = run_error_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install", "bash"],
        &[
            ("SHELL", "/bin/zsh"),
            ("HOME", "relative-home"),
            ("XDG_CONFIG_HOME", "relative-xdg"),
        ],
    );

    assert_eq!(payload["error"]["code"], "completion_install_failed");
    assert_eq!(
        payload["error"]["message"],
        "could not resolve a per-user completion directory; pass --path explicitly"
    );
}

#[test]
fn completion_install_reports_fish_lazy_activation_strategy() {
    let dir = tempfile::tempdir().expect("temp dir");
    let xdg_config_home = dir.path().join("xdg-config");
    fs::create_dir_all(&xdg_config_home).expect("create xdg config home");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install", "fish"],
        &[
            ("HOME", dir.path().join("home").to_string_lossy().as_ref()),
            (
                "XDG_CONFIG_HOME",
                xdg_config_home.to_string_lossy().as_ref(),
            ),
        ],
    );

    let installed_path = xdg_config_home.join("spirectl/completions/sts2.fish");
    let fish_completion_path = xdg_config_home.join("fish/completions/sts2.fish");
    let fish_strategy = activation_strategy(&payload, "fish-user-completions");
    assert_eq!(fish_strategy["loadsOnDemand"], true);
    assert_eq!(
        fish_strategy["command"],
        format!(
            "mkdir -p {} && ln -sf {} {}",
            fish_completion_path.parent().expect("parent").display(),
            installed_path.display(),
            fish_completion_path.display()
        )
    );
}

#[test]
fn completion_without_detectable_shell_returns_structured_error() {
    let dir = tempfile::tempdir().expect("temp dir");

    let payload =
        run_error_json_in_dir_with_env(dir.path(), &["--json", "completion"], &[("SHELL", "")]);

    assert_eq!(payload["error"]["code"], "shell_detection_failed");
}

#[test]
fn completion_install_quotes_posix_activation_command_for_shell_metacharacters() {
    let dir = tempfile::tempdir().expect("temp dir");
    let home = dir.path().join("home");
    let target_path = dir.path().join("custom/sts2;completion.bash");
    fs::create_dir_all(&home).expect("create home");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &[
            "--json",
            "completion",
            "install",
            "bash",
            "--path",
            target_path.to_string_lossy().as_ref(),
        ],
        &[("HOME", home.to_string_lossy().as_ref())],
    );

    assert_eq!(payload["shell"], "bash");
    assert_eq!(
        payload["activationCommand"],
        format!("source '{}'", target_path.display())
    );

    let bash_completion_strategy = activation_strategy(&payload, "bash-completion-user-dir");
    let bash_completion_path = home.join(".local/share/bash-completion/completions/sts2.bash");
    assert_eq!(
        bash_completion_strategy["command"],
        format!(
            "mkdir -p {} && ln -sf '{}' {}",
            bash_completion_path.parent().expect("parent").display(),
            target_path.display(),
            bash_completion_path.display()
        )
    );
}

#[test]
fn completion_install_path_override_writes_requested_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    let target_path = dir.path().join("custom/sts2-completion.bash");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &[
            "--json",
            "completion",
            "install",
            "bash",
            "--path",
            target_path.to_string_lossy().as_ref(),
        ],
        &[("SHELL", "/bin/zsh")],
    );

    assert_eq!(payload["shell"], "bash");
    assert_eq!(payload["path"], target_path.to_string_lossy().as_ref());
    assert_eq!(payload["pathSource"], "explicit");
    assert!(payload["managedDir"].is_null());

    let script = fs::read_to_string(&target_path).expect("installed completion");
    assert!(script.contains("complete -F _sts2"));
}

#[test]
fn completion_install_powershell_quotes_activation_command_for_literal_paths() {
    let dir = tempfile::tempdir().expect("temp dir");
    let target_path = dir.path().join("custom/O'Brien Path/sts2.ps1");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &[
            "--json",
            "completion",
            "install",
            "powershell",
            "--path",
            target_path.to_string_lossy().as_ref(),
        ],
        &[],
    );

    assert_eq!(payload["shell"], "powershell");
    assert_eq!(
        payload["activationCommand"],
        format!(
            ". '{}'",
            target_path.display().to_string().replace('\'', "''")
        )
    );
    let strategies = activation_strategies(&payload);
    assert_eq!(strategies.len(), 1);
    assert_eq!(strategies[0]["id"], "source");
    assert_eq!(strategies[0]["command"], payload["activationCommand"]);
}

#[test]
fn completion_install_omits_zsh_fpath_strategy_without_user_fpath_entry() {
    let dir = tempfile::tempdir().expect("temp dir");
    let xdg_config_home = dir.path().join("xdg-config");
    let home = dir.path().join("home");
    fs::create_dir_all(&xdg_config_home).expect("create xdg config home");
    fs::create_dir_all(&home).expect("create home");

    let payload = run_json_in_dir_with_env(
        dir.path(),
        &["--json", "completion", "install", "zsh"],
        &[
            ("HOME", home.to_string_lossy().as_ref()),
            (
                "FPATH",
                "/usr/share/zsh/site-functions:/usr/local/share/zsh/site-functions",
            ),
            (
                "XDG_CONFIG_HOME",
                xdg_config_home.to_string_lossy().as_ref(),
            ),
        ],
    );

    assert!(
        activation_strategies(&payload)
            .iter()
            .all(|strategy| strategy["id"] != "zsh-fpath")
    );
}
