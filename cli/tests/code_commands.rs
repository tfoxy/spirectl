use clap::Parser;
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::Duration;
use sts2::{Cli, RenderedCommand, run_cli};

fn run(args: &[&str]) -> RenderedCommand {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

fn run_in_dir(workdir: &Path, args: &[&str]) -> RenderedCommand {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let _guard = CurrentDirGuard::enter(workdir);
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

fn test_lock() -> &'static Mutex<()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
}

struct CurrentDirGuard {
    original: PathBuf,
}

impl CurrentDirGuard {
    fn enter(path: &Path) -> Self {
        let original = std::env::current_dir().expect("current dir");
        std::env::set_current_dir(path).expect("set current dir");
        Self { original }
    }
}

impl Drop for CurrentDirGuard {
    fn drop(&mut self) {
        std::env::set_current_dir(&self.original).expect("restore current dir");
    }
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

fn fixture_resources_source_dir() -> PathBuf {
    repo_root().join("dotnet-tools/tests/Spirectl.DotnetTools.TestSymbols/Fixtures")
}

fn copy_dir_recursive(source: &Path, destination: &Path) {
    fs::create_dir_all(destination).expect("create destination directory");

    for entry in fs::read_dir(source).expect("read source directory") {
        let entry = entry.expect("directory entry");
        let entry_path = entry.path();
        let destination_path = destination.join(entry.file_name());

        if entry_path.is_dir() {
            copy_dir_recursive(&entry_path, &destination_path);
        } else {
            fs::copy(&entry_path, &destination_path).expect("copy file");
        }
    }
}

fn write_code_config(
    assemblies_dir: &Path,
    resources_dir: Option<&Path>,
) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let mut config_text = format!(
        "game:\n  path: auto\n  assembliesDir: {}\n",
        assemblies_dir.display()
    );
    if let Some(resources_dir) = resources_dir {
        config_text.push_str(&format!("  resourcesDir: {}\n", resources_dir.display()));
    }
    config_text.push_str("transport:\n  kind: mock\n");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn write_code_config_with_helper(
    assemblies_dir: &Path,
    resources_dir: Option<&Path>,
    helper_path: Option<&Path>,
    shared_cache_dir: Option<&str>,
) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let mut config_text = format!(
        "game:\n  path: auto\n  assembliesDir: {}\n",
        assemblies_dir.display()
    );
    if let Some(resources_dir) = resources_dir {
        config_text.push_str(&format!("  resourcesDir: {}\n", resources_dir.display()));
    }
    if let Some(helper_path) = helper_path {
        config_text.push_str(&format!(
            "tools:\n  dotnetToolsPath: {}\n",
            helper_path.display()
        ));
    }
    if let Some(shared_cache_dir) = shared_cache_dir {
        config_text.push_str(&format!(
            "toolchain:\n  sharedCacheDir: {}\n",
            shared_cache_dir
        ));
    }
    config_text.push_str("transport:\n  kind: mock\n");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn fixture_assemblies_dir() -> tempfile::TempDir {
    let assemblies_dir = tempfile::tempdir().expect("assemblies dir");
    let fixture_assembly = build_fixture_assembly();
    fs::copy(
        fixture_assembly.as_path(),
        assemblies_dir.path().join(
            fixture_assembly
                .file_name()
                .expect("fixture assembly file name"),
        ),
    )
    .expect("copy fixture assembly");
    assemblies_dir
}

fn fixture_resources_dir() -> tempfile::TempDir {
    let resources_dir = tempfile::tempdir().expect("resources dir");
    copy_dir_recursive(&fixture_resources_source_dir(), resources_dir.path());
    resources_dir
}

fn fake_dotnet_path() -> PathBuf {
    repo_root().join("tests/fixtures/fake-dotnet.sh")
}

fn run_in_dir_with_fake_dotnet(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, String)],
) -> RenderedCommand {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let _dir_guard = CurrentDirGuard::enter(workdir);
    let fake_dotnet = fake_dotnet_path();
    let fake_bin = workdir.join(".fake-bin");
    fs::create_dir_all(&fake_bin).expect("create fake bin");
    let fake_dotnet_link = fake_bin.join(if cfg!(windows) {
        "dotnet.cmd"
    } else {
        "dotnet"
    });
    if !fake_dotnet_link.exists() {
        fs::copy(&fake_dotnet, &fake_dotnet_link).expect("copy fake dotnet");
        make_executable(&fake_dotnet_link);
    }
    let mut path = fake_bin.display().to_string();
    if let Some(existing_path) = std::env::var_os("PATH") {
        path.push(if cfg!(windows) { ';' } else { ':' });
        path.push_str(&existing_path.to_string_lossy());
    }

    let mut guards = Vec::new();
    guards.push(EnvGuard::set("PATH", path));
    for (key, value) in envs {
        guards.push(EnvGuard::set(key, value.clone()));
    }

    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

struct EnvGuard {
    key: String,
    original: Option<std::ffi::OsString>,
}

impl EnvGuard {
    fn set(key: &str, value: String) -> Self {
        let original = std::env::var_os(key);
        unsafe {
            std::env::set_var(key, value);
        }
        Self {
            key: key.to_string(),
            original,
        }
    }
}

impl Drop for EnvGuard {
    fn drop(&mut self) {
        unsafe {
            if let Some(value) = &self.original {
                std::env::set_var(&self.key, value);
            } else {
                std::env::remove_var(&self.key);
            }
        }
    }
}

fn create_default_helper_project(root: &Path) -> (PathBuf, PathBuf) {
    let project_dir = root.join("dotnet-tools/src/Spirectl.DotnetTools");
    fs::create_dir_all(&project_dir).expect("create helper project dir");
    let project = project_dir.join("Spirectl.DotnetTools.csproj");
    let source = project_dir.join("Program.cs");
    fs::write(&project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n").expect("write project");
    fs::write(&source, "class Program {}\n").expect("write source");
    let dll = project_dir.join("bin/Debug/net9.0/Spirectl.DotnetTools.dll");
    (project, dll)
}

fn fake_assemblies_dir() -> tempfile::TempDir {
    let assemblies_dir = tempfile::tempdir().expect("assemblies dir");
    touch(&assemblies_dir.path().join("GameAssembly.dll"));
    assemblies_dir
}

fn touch(path: &Path) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("create parent");
    }
    fs::write(path, "x\n").expect("write file");
}

fn read_log(path: &Path) -> Vec<String> {
    fs::read_to_string(path)
        .expect("read fake dotnet log")
        .lines()
        .map(str::to_string)
        .collect()
}

fn make_executable(path: &Path) {
    #[cfg(not(unix))]
    let _ = path;

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mut permissions = fs::metadata(path).expect("metadata").permissions();
        permissions.set_mode(0o755);
        fs::set_permissions(path, permissions).expect("chmod executable");
    }
}

fn assert_log_has_dll_invocation(log: &[String]) {
    assert!(
        log.iter().any(
            |line| line.starts_with('/') && line.contains("Spirectl.DotnetTools.dll describe ")
        ),
        "expected direct DLL invocation, got {log:?}"
    );
}

fn assert_helper_args_include_cache_dir_before_json(call: &str, expected_cache_dir: &Path) {
    let args: Vec<&str> = call.split_whitespace().collect();
    let cache_flag = args
        .iter()
        .position(|arg| *arg == "--cache-dir")
        .unwrap_or_else(|| panic!("expected --cache-dir in helper args: {call}"));
    let json_flag = args
        .iter()
        .position(|arg| *arg == "--json")
        .unwrap_or_else(|| panic!("expected --json in helper args: {call}"));

    assert!(
        cache_flag + 1 < args.len(),
        "expected --cache-dir value in helper args: {call}"
    );
    assert_eq!(
        args[cache_flag + 1],
        expected_cache_dir.display().to_string()
    );
    assert!(
        cache_flag < json_flag,
        "expected --cache-dir before --json in helper args: {call}"
    );
}

fn declaration_manifest_files(cache_dir: &Path) -> Vec<PathBuf> {
    cache_json_files_matching(
        &cache_dir.join("metadata-catalog").join("declarations"),
        "manifest-",
    )
}

fn declaration_assembly_entry_files(cache_dir: &Path) -> Vec<PathBuf> {
    cache_json_files_matching(
        &cache_dir
            .join("metadata-catalog")
            .join("declarations")
            .join("assemblies"),
        "",
    )
}

fn reference_entry_files(cache_dir: &Path) -> Vec<PathBuf> {
    cache_json_files_matching(&cache_dir.join("metadata-catalog").join("references"), "")
}

fn cache_json_files_matching(entry_dir: &Path, prefix: &str) -> Vec<PathBuf> {
    if !entry_dir.exists() {
        return Vec::new();
    }

    let mut files: Vec<_> = fs::read_dir(entry_dir)
        .expect("read cache entry dir")
        .map(|entry| entry.expect("cache entry").path())
        .filter(|path| {
            path.extension()
                .is_some_and(|extension| extension == "json")
                && path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.starts_with(prefix))
        })
        .collect();
    files.sort();
    files
}

#[test]
fn code_helper_launch_fresh_default_dll_avoids_dotnet_run() {
    let downstream = tempfile::tempdir().expect("downstream");
    let assemblies_dir = fake_assemblies_dir();
    let (_project, dll) = create_default_helper_project(downstream.path());
    touch(&dll);
    thread::sleep(Duration::from_millis(25));
    let log = downstream.path().join("dotnet.log");
    let config = write_code_config(assemblies_dir.path(), None);

    let response = run_in_dir_with_fake_dotnet(
        downstream.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[("FAKE_DOTNET_LOG", log.display().to_string())],
    );
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let calls = read_log(&log);

    assert_eq!(payload["command"], "describe");
    assert_log_has_dll_invocation(&calls);
    assert!(
        calls.iter().all(|line| !line.starts_with("run ")),
        "fresh default helper should not use dotnet run: {calls:?}"
    );
}

#[test]
fn code_helper_launch_checked_in_default_path_uses_fresh_dll() {
    let downstream = tempfile::tempdir().expect("downstream");
    let assemblies_dir = fake_assemblies_dir();
    let (_project, dll) = create_default_helper_project(downstream.path());
    touch(&dll);
    thread::sleep(Duration::from_millis(25));
    let log = downstream.path().join("dotnet.log");
    fs::write(
        downstream.path().join("sts2.config.yaml"),
        format!(
            "game:\n  path: auto\n  assembliesDir: {}\ntools:\n  dotnetToolsPath: dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj\ntransport:\n  kind: mock\n",
            assemblies_dir.path().display()
        ),
    )
    .expect("write checked-in config");

    let response = run_in_dir_with_fake_dotnet(
        downstream.path(),
        &[
            "sts2",
            "--json",
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[("FAKE_DOTNET_LOG", log.display().to_string())],
    );
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let calls = read_log(&log);

    assert_eq!(payload["command"], "describe");
    assert_log_has_dll_invocation(&calls);
    assert!(
        calls.iter().all(|line| !line.starts_with("run ")),
        "checked-in default helper path should not use dotnet run: {calls:?}"
    );
}

#[test]
fn code_helper_launch_missing_default_output_builds_then_invokes_dll() {
    let downstream = tempfile::tempdir().expect("downstream");
    let assemblies_dir = fake_assemblies_dir();
    let (_project, dll) = create_default_helper_project(downstream.path());
    let log = downstream.path().join("dotnet.log");
    let config = write_code_config(assemblies_dir.path(), None);

    let _response = run_in_dir_with_fake_dotnet(
        downstream.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[
            ("FAKE_DOTNET_LOG", log.display().to_string()),
            ("FAKE_DOTNET_BUILD_OUTPUT", dll.display().to_string()),
        ],
    );
    let calls = read_log(&log);

    assert!(
        calls.first().is_some_and(|line| line.starts_with("build ")),
        "missing default helper should build first: {calls:?}"
    );
    assert_log_has_dll_invocation(&calls);
}

#[test]
fn code_helper_launch_stale_default_output_builds_then_invokes_dll() {
    let downstream = tempfile::tempdir().expect("downstream");
    let assemblies_dir = fake_assemblies_dir();
    let (_project, dll) = create_default_helper_project(downstream.path());
    touch(&dll);
    thread::sleep(Duration::from_millis(25));
    touch(
        &downstream
            .path()
            .join("dotnet-tools/src/Spirectl.DotnetTools/Program.cs"),
    );
    let log = downstream.path().join("dotnet.log");
    let config = write_code_config(assemblies_dir.path(), None);

    let _response = run_in_dir_with_fake_dotnet(
        downstream.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[
            ("FAKE_DOTNET_LOG", log.display().to_string()),
            ("FAKE_DOTNET_BUILD_OUTPUT", dll.display().to_string()),
        ],
    );
    let calls = read_log(&log);

    assert!(
        calls.first().is_some_and(|line| line.starts_with("build ")),
        "stale default helper should build first: {calls:?}"
    );
    assert_log_has_dll_invocation(&calls);
}

#[test]
fn code_helper_launch_configured_dll_invokes_dotnet_dll_directly() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let dll = workdir.path().join("custom-helper.dll");
    touch(&dll);
    let log = workdir.path().join("dotnet.log");
    let config = write_code_config_with_helper(assemblies_dir.path(), None, Some(&dll), None);

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[("FAKE_DOTNET_LOG", log.display().to_string())],
    );
    let calls = read_log(&log);

    assert_eq!(calls.len(), 1, "expected one dotnet invocation: {calls:?}");
    assert!(
        calls[0].starts_with(&dll.display().to_string()),
        "configured DLL should be invoked via dotnet <dll>: {calls:?}"
    );
}

#[test]
fn code_helper_launch_configured_executable_invokes_directly() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let executable = workdir.path().join("helper");
    let log = workdir.path().join("helper.log");
    fs::write(
        &executable,
        format!(
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >>'{}'\nprintf '{{\"command\":\"%s\",\"status\":\"ok\"}}\\n' \"$1\"\n",
            log.display()
        ),
    )
    .expect("write executable");
    make_executable(&executable);
    let config =
        write_code_config_with_helper(assemblies_dir.path(), None, Some(&executable), None);

    let response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[(
            "FAKE_DOTNET_LOG",
            workdir.path().join("dotnet.log").display().to_string(),
        )],
    );
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let calls = read_log(&log);

    assert_eq!(payload["command"], "describe");
    assert_eq!(calls.len(), 1);
    assert!(calls[0].starts_with("describe type DeckController"));
}

#[test]
fn code_locate_passes_configured_shared_cache_dir_to_helper() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let executable = workdir.path().join("helper");
    let log = workdir.path().join("helper.log");
    fs::write(
        &executable,
        format!(
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >>'{}'\nprintf '{{\"command\":\"%s\",\"status\":\"ok\"}}\\n' \"$1\"\n",
            log.display()
        ),
    )
    .expect("write executable");
    make_executable(&executable);
    let expected_cache_dir = workdir.path().join("helper-cache");
    let expected_cache_dir_text = expected_cache_dir.display().to_string();
    let config = write_code_config_with_helper(
        assemblies_dir.path(),
        None,
        Some(&executable),
        Some(&expected_cache_dir_text),
    );

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "locate",
            "type",
            "DeckController",
        ],
        &[(
            "FAKE_DOTNET_LOG",
            workdir.path().join("dotnet.log").display().to_string(),
        )],
    );
    let calls = read_log(&log);

    assert_eq!(calls.len(), 1);
    assert!(calls[0].starts_with("locate type DeckController"));
    assert_helper_args_include_cache_dir_before_json(&calls[0], &expected_cache_dir);
}

#[test]
fn code_refs_passes_configured_shared_cache_dir_to_helper() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let executable = workdir.path().join("helper");
    let log = workdir.path().join("helper.log");
    fs::write(
        &executable,
        format!(
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >>'{}'\nprintf '{{\"command\":\"%s\",\"status\":\"ok\"}}\\n' \"$1\"\n",
            log.display()
        ),
    )
    .expect("write executable");
    make_executable(&executable);
    let expected_cache_dir = workdir.path().join("helper-cache");
    let expected_cache_dir_text = expected_cache_dir.display().to_string();
    let config = write_code_config_with_helper(
        assemblies_dir.path(),
        None,
        Some(&executable),
        Some(&expected_cache_dir_text),
    );

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "refs",
            "type",
            "DeckController",
        ],
        &[(
            "FAKE_DOTNET_LOG",
            workdir.path().join("dotnet.log").display().to_string(),
        )],
    );
    let calls = read_log(&log);

    assert_eq!(calls.len(), 1);
    assert!(calls[0].starts_with("refs type DeckController"));
    assert_helper_args_include_cache_dir_before_json(&calls[0], &expected_cache_dir);
}

#[test]
fn code_helper_launch_configured_csproj_uses_dotnet_run() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let project = workdir.path().join("Custom.Helper.csproj");
    touch(&project);
    let log = workdir.path().join("dotnet.log");
    let config = write_code_config_with_helper(assemblies_dir.path(), None, Some(&project), None);

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[("FAKE_DOTNET_LOG", log.display().to_string())],
    );
    let calls = read_log(&log);

    assert_eq!(
        calls.len(),
        1,
        "expected one dotnet run invocation: {calls:?}"
    );
    assert!(
        calls[0].starts_with("run --verbosity quiet --project "),
        "configured project should use diagnostic dotnet run path: {calls:?}"
    );
}

#[test]
fn code_helper_launch_preserves_structured_failure_stdout() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let dll = workdir.path().join("custom-helper.dll");
    touch(&dll);
    let log = workdir.path().join("dotnet.log");
    let config = write_code_config_with_helper(assemblies_dir.path(), None, Some(&dll), None);

    let response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "type",
            "DeckController",
        ],
        &[
            ("FAKE_DOTNET_LOG", log.display().to_string()),
            (
                "FAKE_DOTNET_STDOUT",
                "SDK noise\n{\"error\":{\"code\":\"helper_failed\",\"message\":\"kept\"}}"
                    .to_string(),
            ),
            ("FAKE_DOTNET_EXIT_CODE", "7".to_string()),
        ],
    );
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 7);
    assert_eq!(payload["error"]["code"], "helper_failed");
    assert_eq!(payload["error"]["message"], "kept");
}

#[test]
fn code_locate_accepts_new_search_root_flags() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "code",
        "locate",
        "method",
        "DrawCard",
        "--limit",
        "5",
        "--game-path",
        "/tmp/sts2",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--mods-dir",
        "/tmp/sts2/mods",
        "--include-mods",
        "--include-dependencies",
    ]);

    assert!(parsed.is_ok(), "expected new code locate flags to parse");
}

#[test]
fn code_managed_commands_accept_include_dependencies() {
    let commands: &[&[&str]] = &[
        &[
            "sts2",
            "code",
            "locate",
            "method",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "describe",
            "method",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "refs",
            "symbol",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "derived",
            "type",
            "BaseCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "hooks",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "hook-info",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
        &[
            "sts2",
            "code",
            "decompile",
            "method",
            "DrawCard",
            "--assemblies-dir",
            "/tmp/sts2/data_sts2_linux_x86_64",
            "--include-dependencies",
        ],
    ];

    for args in commands {
        let parsed = <Cli as Parser>::try_parse_from(args.iter().copied());
        assert!(
            parsed.is_ok(),
            "expected include-dependencies command to parse: {args:?}"
        );
    }
}

#[test]
fn code_include_dependencies_is_forwarded_without_include_mods() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let executable = workdir.path().join("helper");
    let log = workdir.path().join("helper.log");
    fs::write(
        &executable,
        format!(
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >>'{}'\nprintf '{{\"command\":\"%s\",\"status\":\"ok\"}}\\n' \"$1\"\n",
            log.display()
        ),
    )
    .expect("write executable");
    make_executable(&executable);
    let config =
        write_code_config_with_helper(assemblies_dir.path(), None, Some(&executable), None);

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "locate",
            "method",
            "DrawCard",
            "--include-dependencies",
        ],
        &[(
            "FAKE_DOTNET_LOG",
            workdir.path().join("dotnet.log").display().to_string(),
        )],
    );
    let calls = read_log(&log);

    assert_eq!(calls.len(), 1);
    assert!(calls[0].contains("--include-dependencies"));
    assert!(!calls[0].contains("--include-mods"));
}

#[test]
fn code_include_dependencies_is_not_forwarded_by_default() {
    let workdir = tempfile::tempdir().expect("workdir");
    let assemblies_dir = fake_assemblies_dir();
    let executable = workdir.path().join("helper");
    let log = workdir.path().join("helper.log");
    fs::write(
        &executable,
        format!(
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >>'{}'\nprintf '{{\"command\":\"%s\",\"status\":\"ok\"}}\\n' \"$1\"\n",
            log.display()
        ),
    )
    .expect("write executable");
    make_executable(&executable);
    let config =
        write_code_config_with_helper(assemblies_dir.path(), None, Some(&executable), None);

    let _response = run_in_dir_with_fake_dotnet(
        workdir.path(),
        &[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "code",
            "describe",
            "method",
            "DrawCard",
        ],
        &[(
            "FAKE_DOTNET_LOG",
            workdir.path().join("dotnet.log").display().to_string(),
        )],
    );
    let calls = read_log(&log);

    assert_eq!(calls.len(), 1);
    assert!(!calls[0].contains("--include-dependencies"));
    assert!(!calls[0].contains("--include-mods"));
}

#[test]
fn code_decompile_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "decompile",
        "type",
        "Spirectl.TestSymbols.Gameplay.DeckController",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--full",
    ]);

    assert!(parsed.is_ok(), "expected code decompile to parse");
}

#[test]
fn code_refs_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "refs",
        "symbol",
        "DeckSnapshot",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--limit",
        "12",
    ]);

    assert!(parsed.is_ok(), "expected code refs to parse");
}

#[test]
fn code_derived_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "derived",
        "type",
        "Spirectl.TestSymbols.Gameplay.BaseController",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--limit",
        "8",
    ]);

    assert!(parsed.is_ok(), "expected code derived to parse");
}

#[test]
fn code_hooks_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "hooks",
        "OnDeckChanged",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--resources-dir",
        "/tmp/sts2/project",
        "--limit",
        "6",
        "--offset",
        "2",
        "--source",
        "game",
        "--form",
        "managed-prefix",
        "--has-script",
        "--sort",
        "reference-count",
    ]);

    assert!(parsed.is_ok(), "expected code hooks to parse");
}

#[test]
fn code_hook_info_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "hook-info",
        "method:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.CombatDeckHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
    ]);

    assert!(parsed.is_ok(), "expected code hook-info to parse");
}

#[test]
fn code_scene_search_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "scene-search",
        "HandPanel",
        "--resources-dir",
        "/tmp/sts2/project",
        "--assemblies-dir",
        "/tmp/sts2/data_sts2_linux_x86_64",
        "--limit",
        "12",
    ]);

    assert!(parsed.is_ok(), "expected code scene-search to parse");
}

#[test]
fn code_scene_tree_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "scene-tree",
        "res://ui/CombatScreen.tscn",
        "--resources-dir",
        "/tmp/sts2/project",
    ]);

    assert!(parsed.is_ok(), "expected code scene-tree to parse");
}

#[test]
fn code_scene_node_is_a_valid_command_shape() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "code",
        "scene-node",
        "res://ui/shared/HandPanel.tscn",
        "/HandPanel/ConfirmButton",
        "--resources-dir",
        "/tmp/sts2/project",
    ]);

    assert!(parsed.is_ok(), "expected code scene-node to parse");
}

#[test]
fn code_locate_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "locate",
        "type",
        "DeckController",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "locate");
    assert_eq!(payload["status"], "ok");
    assert!(payload["matchCount"].as_i64().expect("matchCount") >= 1);
    assert_eq!(payload["matches"][0]["kind"], "type");
    assert_eq!(payload["matches"][0]["source"], "game");
}

#[test]
fn code_locate_real_helper_uses_configured_shared_cache_without_payload_changes() {
    let assemblies_dir = fixture_assemblies_dir();
    let cache_dir = tempfile::tempdir().expect("shared cache dir");
    let cache_dir_text = cache_dir.path().display().to_string();
    let config =
        write_code_config_with_helper(assemblies_dir.path(), None, None, Some(&cache_dir_text));

    let first_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "locate",
        "type",
        "DeckController",
    ]);
    let second_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "locate",
        "type",
        "DeckController",
    ]);
    let first_payload: Value = serde_json::from_str(&first_response.stdout).expect("first json");
    let second_payload: Value = serde_json::from_str(&second_response.stdout).expect("second json");

    assert_eq!(first_payload, second_payload);
    assert_eq!(first_payload["command"], "locate");
    assert_eq!(first_payload["status"], "ok");
    assert!(
        !declaration_manifest_files(cache_dir.path()).is_empty(),
        "expected declaration manifest under {}",
        cache_dir.path().display()
    );
    assert!(
        !declaration_assembly_entry_files(cache_dir.path()).is_empty(),
        "expected per-assembly declaration cache entries under {}",
        cache_dir.path().display()
    );
}

#[test]
fn code_locate_resolves_default_helper_from_downstream_cwd() {
    let downstream = tempfile::tempdir().expect("downstream repo");
    let assemblies_dir = fixture_assemblies_dir();
    fs::write(
        downstream.path().join("sts2.local.yaml"),
        format!(
            "transport:\n  kind: mock\ngame:\n  path: auto\n  assembliesDir: {}\n",
            assemblies_dir.path().display()
        ),
    )
    .expect("write downstream config");

    assert!(
        !downstream.path().join("dotnet-tools").exists(),
        "downstream fixture should not contain a helper project"
    );

    let response = run_in_dir(
        downstream.path(),
        &["sts2", "--json", "code", "locate", "type", "DeckController"],
    );
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "locate");
    assert_eq!(payload["status"], "ok");
    assert!(payload["matchCount"].as_i64().expect("matchCount") >= 1);
    assert_eq!(payload["matches"][0]["kind"], "type");
    assert_eq!(payload["matches"][0]["source"], "game");
}

#[test]
fn code_describe_passes_structured_helper_errors_through() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "describe",
        "method",
        "DrawCard",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "ambiguous_query");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("DrawCard")
    );

    let missing_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "describe",
        "method",
        "MissingMethodName",
    ]);
    let missing_payload: Value = serde_json::from_str(&missing_response.stdout).expect("json");

    assert_eq!(missing_response.exit_code, 3);
    assert_eq!(missing_payload["error"]["code"], "not_found");
    assert!(
        missing_payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("MissingMethodName")
    );
}

#[test]
fn code_describe_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let cache_dir = tempfile::tempdir().expect("shared cache dir");
    let cache_dir_text = cache_dir.path().display().to_string();
    let config =
        write_code_config_with_helper(assemblies_dir.path(), None, None, Some(&cache_dir_text));
    let first_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "describe",
        "type",
        "Spirectl.TestSymbols.Gameplay.DeckController",
    ]);
    let second_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "describe",
        "type",
        "Spirectl.TestSymbols.Gameplay.DeckController",
    ]);
    let payload: Value = serde_json::from_str(&first_response.stdout).expect("first json");
    let second_payload: Value = serde_json::from_str(&second_response.stdout).expect("second json");

    assert_eq!(payload, second_payload);
    assert_eq!(payload["command"], "describe");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["subject"], "type");
    assert_eq!(payload["namespace"], "Spirectl.TestSymbols.Gameplay");
    assert_eq!(payload["typeKind"], "class");
    assert_eq!(
        payload["baseTypeId"],
        "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.BaseController"
    );
    assert!(
        payload["interfaceIds"]
            .as_array()
            .expect("interfaceIds array")
            .iter()
            .any(|item| item == "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.IInspectable")
    );
    assert!(
        payload["fields"]
            .as_array()
            .expect("fields array")
            .iter()
            .any(|field| field["name"] == "_drawnCards")
    );
    assert!(
        payload["properties"]
            .as_array()
            .expect("properties array")
            .iter()
            .any(|property| property["name"] == "DrawCount")
    );
    assert!(
        payload["events"]
            .as_array()
            .expect("events array")
            .iter()
            .any(|event_info| event_info["name"] == "DeckChanged")
    );
    assert!(
        payload["methods"]
            .as_array()
            .expect("methods array")
            .iter()
            .any(|method| {
                method["name"] == "DrawCard"
                    && method["id"]
                        .as_str()
                        .is_some_and(|id| id.starts_with("method:"))
            })
    );
    assert!(
        payload["nestedTypes"]
            .as_array()
            .expect("nestedTypes array")
            .iter()
            .any(|nested_type| nested_type["name"] == "DeckSnapshot")
    );
    assert!(
        payload["memberCounts"]["fields"]
            .as_i64()
            .expect("field count")
            >= 1
    );
    assert_eq!(payload["memberCounts"]["properties"], 3);
    assert_eq!(payload["memberCounts"]["events"], 1);
    assert!(
        payload["memberCounts"]["methods"]
            .as_i64()
            .expect("method count")
            >= 1
    );
    assert!(
        payload["memberCounts"]["nestedTypes"]
            .as_i64()
            .expect("nested type count")
            >= 1
    );
    assert!(
        !declaration_manifest_files(cache_dir.path()).is_empty(),
        "expected declaration manifest under {}",
        cache_dir.path().display()
    );
    assert!(
        !declaration_assembly_entry_files(cache_dir.path()).is_empty(),
        "expected per-assembly declaration cache entries under {}",
        cache_dir.path().display()
    );
    assert!(
        reference_entry_files(cache_dir.path()).is_empty(),
        "describe should not populate reference cache entries under {}",
        cache_dir.path().display()
    );
}

#[test]
fn code_describe_method_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "describe",
        "method",
        "Spirectl.TestSymbols.Gameplay.DeckController::DescribeCard(System.String,System.Int32)",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "describe");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["subject"], "method");
    assert_eq!(
        payload["signature"],
        "System.String DescribeCard(System.String, System.Int32)"
    );
    assert_eq!(
        payload["declaringTypeId"],
        "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController"
    );
    assert_eq!(payload["visibility"], "public");
    assert_eq!(payload["returnType"], "System.String");
    assert_eq!(
        payload["parameters"].as_array().expect("parameters").len(),
        2
    );
    assert_eq!(payload["parameters"][0]["type"], "System.String");
    assert_eq!(payload["parameters"][1]["type"], "System.Int32");
    assert_eq!(payload["isStatic"], true);
    assert_eq!(payload["isVirtual"], false);
    assert_eq!(payload["isAbstract"], false);
}

#[test]
fn code_refs_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "refs",
        "method",
        "Spirectl.TestSymbols.Gameplay.DeckController::DescribeCard(System.String,System.Int32)",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "refs");
    assert_eq!(payload["status"], "ok");
    assert!(payload["referenceCount"].as_i64().expect("referenceCount") >= 1);
    assert_eq!(
        payload["references"][0]["targetDisplayName"],
        "DescribeCard"
    );
    assert!(
        payload["references"]
            .as_array()
            .expect("references array")
            .iter()
            .any(|reference| reference["referenceKind"] == "method-call")
    );
    assert!(
        payload["references"]
            .as_array()
            .expect("references array")
            .iter()
            .any(|reference| {
                reference["containerFullName"]
                    == "Spirectl.TestSymbols.Gameplay.ModdingNavigator::Run"
            })
    );

    let type_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "refs",
        "type",
        "Spirectl.TestSymbols.Gameplay.DeckController.DeckSnapshot",
    ]);
    let type_payload: Value = serde_json::from_str(&type_response.stdout).expect("json");

    assert_eq!(type_payload["command"], "refs");
    assert_eq!(type_payload["status"], "ok");
    assert!(
        type_payload["references"]
            .as_array()
            .expect("type references array")
            .iter()
            .any(|reference| {
                reference["targetDisplayName"] == "DeckSnapshot"
                    && reference["referenceKind"] == "type-use"
                    && reference["containerDisplayName"] == "CreateSnapshot"
            })
    );
}

#[test]
fn code_derived_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "derived",
        "type",
        "Spirectl.TestSymbols.Gameplay.BaseController",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "derived");
    assert_eq!(payload["status"], "ok");
    assert!(payload["derivedCount"].as_i64().expect("derivedCount") >= 2);
    assert!(
        payload["derivedTypes"]
            .as_array()
            .expect("derivedTypes array")
            .iter()
            .any(|derived| derived["relationKind"] == "extends")
    );
}

#[test]
fn code_decompile_supports_metadata_default_and_full_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);

    let metadata_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "decompile",
        "type",
        "Spirectl.TestSymbols.Gameplay.ModdingNavigator",
    ]);
    let metadata_payload: Value = serde_json::from_str(&metadata_response.stdout).expect("json");

    assert_eq!(metadata_payload["command"], "decompile");
    assert_eq!(metadata_payload["backend"], "metadata");
    assert!(
        metadata_payload["text"]
            .as_str()
            .expect("text")
            .contains("// Methods")
    );
    assert!(
        metadata_payload["memberSummaries"]
            .as_array()
            .expect("memberSummaries array")
            .iter()
            .any(|summary| {
                summary["signature"]
                    == "System.String Run(Spirectl.TestSymbols.Gameplay.DeckController, Spirectl.TestSymbols.Gameplay.HandController)"
                    && summary["calls"]
                        .as_array()
                        .expect("calls array")
                        .iter()
                        .any(|call| call["displayName"] == "DescribeCard")
            })
    );

    let full_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "decompile",
        "type",
        "Spirectl.TestSymbols.Gameplay.ModdingNavigator",
        "--full",
    ]);
    let full_payload: Value = serde_json::from_str(&full_response.stdout).expect("json");

    assert_eq!(full_payload["command"], "decompile");
    assert_eq!(full_payload["backend"], "ilspy");
    assert!(
        full_payload["text"]
            .as_str()
            .expect("text")
            .contains("deck.DrawCard();")
    );
}

#[test]
fn code_hooks_uses_real_helper_output_against_fixture_assembly() {
    let assemblies_dir = fixture_assemblies_dir();
    let resources_dir = fixture_resources_dir();
    let config = write_code_config(assemblies_dir.path(), Some(resources_dir.path()));
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "hooks",
        "OnDeckChanged",
        "--limit",
        "5",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "hooks");
    assert_eq!(payload["status"], "ok");
    assert!(payload["matchCount"].as_i64().expect("matchCount") >= 3);
    assert_eq!(payload["limit"], 5);
    assert_eq!(payload["offset"], 0);
    assert!(payload["totalCount"].as_i64().expect("totalCount") >= 3);
    assert!(payload["returnedCount"].as_i64().expect("returnedCount") >= 3);
    assert!(payload["facets"]["hookForms"].is_array());
    assert!(
        payload["matches"]
            .as_array()
            .expect("matches array")
            .iter()
            .any(|item| {
                item["fullName"].as_str().is_some_and(|full_name| {
                    full_name.contains("CombatScreenController::OnDeckChanged")
                }) && item["scriptPath"] == "res://scripts/CombatScreen.cs"
            })
    );
}

#[test]
fn code_hook_info_uses_real_helper_output_and_preserves_structured_errors() {
    let assemblies_dir = fixture_assemblies_dir();
    let config = write_code_config(assemblies_dir.path(), None);

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "hook-info",
        "Spirectl.TestSymbols.Gameplay.CombatDeckHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "hook-info");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["isOverride"], true);
    assert!(
        payload["implementedInterfaceMethodIds"]
            .as_array()
            .expect("implementedInterfaceMethodIds array")
            .iter()
            .any(|item| item == "method:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.ICombatHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)")
    );

    let ambiguous = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "hook-info",
        "OnCombatOpened",
    ]);
    let ambiguous_payload: Value = serde_json::from_str(&ambiguous.stdout).expect("json");

    assert_eq!(ambiguous.exit_code, 3);
    assert_eq!(ambiguous_payload["error"]["code"], "ambiguous_query");
    assert!(
        ambiguous_payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("OnCombatOpened")
    );
}

#[test]
fn code_scene_search_uses_real_helper_output_against_fixture_resources() {
    let assemblies_dir = fixture_assemblies_dir();
    let resources_dir = fixture_resources_dir();
    let config = write_code_config(assemblies_dir.path(), Some(resources_dir.path()));
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-search",
        "HandPanel",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "scene-search");
    assert_eq!(payload["status"], "ok");
    assert!(payload["matchCount"].as_i64().expect("matchCount") >= 2);
    assert!(
        payload["matches"]
            .as_array()
            .expect("matches array")
            .iter()
            .any(|item| {
                item["kind"] == "node"
                    && item["nodePath"] == "/HandPanel"
                    && item["attachedScriptType"]
                        .as_str()
                        .is_some_and(|attached_script_type| {
                            attached_script_type.contains("HandPanelController")
                        })
            })
    );
    let binary_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-search",
        "BinaryOnly",
    ]);
    let binary_payload: Value = serde_json::from_str(&binary_response.stdout).expect("json");

    assert!(
        binary_payload["matches"]
            .as_array()
            .expect("matches array")
            .iter()
            .any(|item| {
                item["kind"] == "scene"
                    && item["scenePath"] == "res://binary/BinaryOnly.scn"
                    && item["storageKind"] == "binary-file"
            })
    );

    let packed_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-search",
        "PackedPanel",
    ]);
    let packed_payload: Value = serde_json::from_str(&packed_response.stdout).expect("json");

    assert!(
        packed_payload["matches"]
            .as_array()
            .expect("matches array")
            .iter()
            .any(|item| {
                item["kind"] == "scene"
                    && item["scenePath"] == "res://packed/PackedPanel.tscn"
                    && item["storageKind"] == "packed-entry"
                    && item["containerPath"]
                        .as_str()
                        .expect("containerPath")
                        .ends_with("packed-fixtures.pck")
            })
    );
}

#[test]
fn code_scene_tree_uses_real_helper_output_against_fixture_resources() {
    let assemblies_dir = fixture_assemblies_dir();
    let resources_dir = fixture_resources_dir();
    let config = write_code_config(assemblies_dir.path(), Some(resources_dir.path()));
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-tree",
        "res://ui/shared/HandPanel.tscn",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "scene-tree");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["scenePath"], "res://ui/shared/HandPanel.tscn");
    assert_eq!(payload["nodes"][0]["nodePath"], "/HandPanel");
    assert!(
        payload["nodes"]
            .as_array()
            .expect("nodes array")
            .iter()
            .any(|node| {
                node["nodePath"] == "/HandPanel/ConfirmButton"
                    && node["parentNodePath"] == "/HandPanel"
            })
    );

    let binary_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-tree",
        "res://binary/BinaryOnly.scn",
    ]);
    let binary_payload: Value = serde_json::from_str(&binary_response.stdout).expect("json");

    assert_eq!(binary_payload["storageKind"], "binary-file");
    assert!(
        binary_payload["nodes"]
            .as_array()
            .expect("nodes array")
            .iter()
            .any(|node| {
                node["nodePath"] == "/HandPanel/ConfirmButton"
                    && node["parentNodePath"] == "/HandPanel"
            })
    );
}

#[test]
fn code_scene_node_uses_real_helper_output_against_fixture_resources() {
    let assemblies_dir = fixture_assemblies_dir();
    let resources_dir = fixture_resources_dir();
    let config = write_code_config(assemblies_dir.path(), Some(resources_dir.path()));
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-node",
        "res://ui/shared/HandPanel.tscn",
        "/HandPanel/ConfirmButton",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "scene-node");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["nodePath"], "/HandPanel/ConfirmButton");
    assert!(
        payload["nodeRefs"]
            .as_array()
            .expect("nodeRefs array")
            .iter()
            .any(|node_ref| node_ref["targetNodePath"] == "/HandPanel/EnergyLabel")
    );

    let binary_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "code",
        "scene-node",
        "res://binary/BinaryOnly.scn",
        "/HandPanel",
    ]);
    let binary_payload: Value = serde_json::from_str(&binary_response.stdout).expect("json");

    assert_eq!(binary_payload["storageKind"], "binary-file");
    assert!(
        binary_payload["resourceRefs"]
            .as_array()
            .expect("resourceRefs array")
            .iter()
            .any(|resource_ref| {
                resource_ref["property"] == "panel_style"
                    && resource_ref["targetKind"] == "subresource"
                    && resource_ref["resourceType"] == "StyleBoxFlat"
            })
    );
}
