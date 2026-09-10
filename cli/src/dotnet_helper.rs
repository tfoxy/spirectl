use serde::Serialize;
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};
use std::time::SystemTime;

use crate::{
    AppConfig, AppError, ConfigProvenance, DEFAULT_DOTNET_TOOLS_PATH, RenderedCommand,
    render_structured, resolve_dotnet_tools_path,
};

const HELPER_TARGET_FRAMEWORK: &str = "net9.0";
const HELPER_BUILD_CONFIGURATION: &str = "Debug";
const DEFAULT_HELPER_DLL: &str = "Spirectl.DotnetTools.dll";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum DotnetHelperLaunchKind {
    DefaultProject,
    Project,
    Dll,
    Executable,
    Missing,
    Unsupported,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum DotnetHelperBuildReason {
    DefaultOutputMissing,
    DefaultOutputStale,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DotnetHelperLaunchPlan {
    pub configured_path: PathBuf,
    pub resolved_path: PathBuf,
    pub invocation_path: Option<PathBuf>,
    pub build_output_path: Option<PathBuf>,
    pub provenance: String,
    pub launch_kind: DotnetHelperLaunchKind,
    pub available: bool,
    pub build_needed: bool,
    pub build_reason: Option<DotnetHelperBuildReason>,
    pub failure_reason: Option<String>,
    /// False when the default-project staleness walk was skipped. Only a lazy resolve can leave
    /// this false, and only for the default project; every other launch kind has nothing to stale.
    pub stale_checked: bool,
}

/// Resolve the helper launch plan, including whether the default project's build output is stale.
/// Determining that walks every source file under `dotnet-tools/`, so callers that are about to
/// run the helper use this and callers that are only reporting use the lazy form below.
pub fn resolve_dotnet_helper_launch(
    config: &AppConfig,
    provenance: &ConfigProvenance,
) -> DotnetHelperLaunchPlan {
    resolve_dotnet_helper_launch_with(config, provenance, true)
}

/// Same resolution without the staleness walk. `toolchain info` is a read-only report that never
/// builds anything, so paying for a recursive stat of `dotnet-tools/` to fill in a field nobody
/// acts on is pure cost. The returned plan carries `stale_checked: false` so a caller cannot
/// mistake "not checked" for "not stale"; the eager path still runs before any real helper launch.
pub fn resolve_dotnet_helper_launch_lazy(
    config: &AppConfig,
    provenance: &ConfigProvenance,
) -> DotnetHelperLaunchPlan {
    resolve_dotnet_helper_launch_with(config, provenance, false)
}

fn resolve_dotnet_helper_launch_with(
    config: &AppConfig,
    provenance: &ConfigProvenance,
    check_stale: bool,
) -> DotnetHelperLaunchPlan {
    let configured_path = PathBuf::from(&config.tools.dotnet_tools_path);
    let resolved_path = resolve_dotnet_tools_path(config);
    let is_default = is_default_helper_project_path(&configured_path, &resolved_path);

    if !resolved_path.exists() {
        return DotnetHelperLaunchPlan {
            configured_path,
            resolved_path,
            invocation_path: None,
            build_output_path: None,
            provenance: provenance.dotnet_tools_path.name().to_string(),
            launch_kind: DotnetHelperLaunchKind::Missing,
            available: false,
            build_needed: false,
            build_reason: None,
            failure_reason: Some("configured helper path does not exist".to_string()),
            stale_checked: true,
        };
    }

    if resolved_path.is_file() && path_has_extension(&resolved_path, "csproj") {
        if is_default {
            let build_output_path = default_project_build_output(&resolved_path);
            let build_reason = if !build_output_path.exists() {
                Some(DotnetHelperBuildReason::DefaultOutputMissing)
            } else if check_stale
                && helper_project_inputs_newer_than_output(&resolved_path, &build_output_path)
            {
                Some(DotnetHelperBuildReason::DefaultOutputStale)
            } else {
                None
            };

            return DotnetHelperLaunchPlan {
                configured_path,
                resolved_path,
                invocation_path: Some(build_output_path.clone()),
                build_output_path: Some(build_output_path),
                provenance: provenance.dotnet_tools_path.name().to_string(),
                launch_kind: DotnetHelperLaunchKind::DefaultProject,
                available: build_reason.is_none(),
                build_needed: build_reason.is_some(),
                build_reason,
                failure_reason: None,
                stale_checked: check_stale,
            };
        }

        return DotnetHelperLaunchPlan {
            configured_path,
            invocation_path: Some(resolved_path.clone()),
            resolved_path,
            build_output_path: None,
            provenance: provenance.dotnet_tools_path.name().to_string(),
            launch_kind: DotnetHelperLaunchKind::Project,
            available: true,
            build_needed: false,
            build_reason: None,
            failure_reason: None,
            stale_checked: true,
        };
    }

    if resolved_path.is_file() && path_has_extension(&resolved_path, "dll") {
        return DotnetHelperLaunchPlan {
            configured_path,
            invocation_path: Some(resolved_path.clone()),
            resolved_path,
            build_output_path: None,
            provenance: provenance.dotnet_tools_path.name().to_string(),
            launch_kind: DotnetHelperLaunchKind::Dll,
            available: true,
            build_needed: false,
            build_reason: None,
            failure_reason: None,
            stale_checked: true,
        };
    }

    if resolved_path.is_file() && is_executable_candidate(&resolved_path) {
        return DotnetHelperLaunchPlan {
            configured_path,
            invocation_path: Some(resolved_path.clone()),
            resolved_path,
            build_output_path: None,
            provenance: provenance.dotnet_tools_path.name().to_string(),
            launch_kind: DotnetHelperLaunchKind::Executable,
            available: true,
            build_needed: false,
            build_reason: None,
            failure_reason: None,
            stale_checked: true,
        };
    }

    DotnetHelperLaunchPlan {
        configured_path,
        resolved_path,
        invocation_path: None,
        build_output_path: None,
        provenance: provenance.dotnet_tools_path.name().to_string(),
        launch_kind: DotnetHelperLaunchKind::Unsupported,
        available: false,
        build_needed: false,
        build_reason: None,
        failure_reason: Some(
            "configured helper path must be a .csproj, .dll, or executable file".to_string(),
        ),
        stale_checked: true,
    }
}

fn is_default_helper_project_path(configured_path: &Path, resolved_path: &Path) -> bool {
    configured_path == Path::new(DEFAULT_DOTNET_TOOLS_PATH)
        || resolved_path.ends_with(DEFAULT_DOTNET_TOOLS_PATH)
}

pub fn default_project_build_output(project_path: &Path) -> PathBuf {
    project_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("bin")
        .join(HELPER_BUILD_CONFIGURATION)
        .join(HELPER_TARGET_FRAMEWORK)
        .join(DEFAULT_HELPER_DLL)
}

pub fn helper_project_inputs_newer_than_output(project_path: &Path, output_path: &Path) -> bool {
    let Ok(output_modified) = modified_at(output_path) else {
        return true;
    };

    project_input_paths(project_path)
        .into_iter()
        .any(|input| modified_at(&input).is_ok_and(|modified| modified > output_modified))
}

pub(crate) fn run_dotnet_helper(
    plan: &DotnetHelperLaunchPlan,
    helper_args: &[std::ffi::OsString],
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    if !plan.available && !plan.build_needed {
        return Err(unusable_helper_path_error(plan));
    }

    if plan.launch_kind == DotnetHelperLaunchKind::DefaultProject && plan.build_needed {
        build_default_helper(plan)?;
    }

    let output = match plan.launch_kind {
        DotnetHelperLaunchKind::DefaultProject | DotnetHelperLaunchKind::Dll => {
            let Some(invocation_path) = plan.invocation_path.as_deref() else {
                return Err(unusable_helper_path_error(plan));
            };
            run_dotnet_dll(invocation_path, helper_args)
        }
        DotnetHelperLaunchKind::Executable => {
            let Some(invocation_path) = plan.invocation_path.as_deref() else {
                return Err(unusable_helper_path_error(plan));
            };
            run_executable(invocation_path, helper_args)
        }
        DotnetHelperLaunchKind::Project => {
            let Some(project_path) = plan.invocation_path.as_deref() else {
                return Err(unusable_helper_path_error(plan));
            };
            run_dotnet_project(project_path, helper_args)
        }
        DotnetHelperLaunchKind::Missing | DotnetHelperLaunchKind::Unsupported => {
            return Err(unusable_helper_path_error(plan));
        }
    }
    .map_err(|source| {
        AppError::tool_invocation(
            &helper_invocation_label(plan),
            &format!("failed to invoke .NET helper tool: {source}"),
        )
    })?;

    helper_output_to_rendered(plan, output, json_output)
}

fn build_default_helper(plan: &DotnetHelperLaunchPlan) -> Result<(), AppError> {
    let output = Command::new("dotnet")
        .arg("build")
        .arg(&plan.resolved_path)
        .arg("--verbosity")
        .arg("quiet")
        .arg("-m:1")
        .output()
        .map_err(|source| {
            AppError::tool_invocation(
                "dotnet build",
                &format!(
                    "failed to build default .NET helper project '{}': {source}",
                    plan.resolved_path.display()
                ),
            )
        })?;

    if output.status.success() {
        return Ok(());
    }

    let stdout = helper_stdout_for_context(&output.stdout, true);
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    let message = if !stderr.is_empty() {
        stderr
    } else if !stdout.trim().is_empty() {
        stdout.trim().to_string()
    } else {
        format!(
            "dotnet build failed for default helper project '{}'",
            plan.resolved_path.display()
        )
    };
    Err(AppError::tool_invocation("dotnet build", &message))
}

fn run_dotnet_dll(dll_path: &Path, helper_args: &[std::ffi::OsString]) -> std::io::Result<Output> {
    let mut command = Command::new("dotnet");
    command.arg(dll_path).args(helper_args);
    command.output()
}

fn run_dotnet_project(
    project_path: &Path,
    helper_args: &[std::ffi::OsString],
) -> std::io::Result<Output> {
    let mut command = Command::new("dotnet");
    command
        .arg("run")
        .arg("--verbosity")
        .arg("quiet")
        .arg("--project")
        .arg(project_path)
        .arg("--")
        .args(helper_args);
    command.output()
}

fn run_executable(
    executable_path: &Path,
    helper_args: &[std::ffi::OsString],
) -> std::io::Result<Output> {
    let mut command = Command::new(executable_path);
    command.args(helper_args);
    command.output()
}

fn helper_output_to_rendered(
    plan: &DotnetHelperLaunchPlan,
    output: Output,
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    if !output.status.success() {
        let stdout = helper_stdout_for_context(&output.stdout, json_output);
        if !stdout.trim().is_empty() {
            return Ok(RenderedCommand {
                stdout,
                exit_code: output.status.code().unwrap_or(4),
            });
        }

        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let message = if stderr.is_empty() {
            "helper process failed without stdout or stderr".to_string()
        } else {
            stderr
        };
        return Err(AppError::tool_invocation(
            &helper_invocation_label(plan),
            &message,
        ));
    }

    Ok(RenderedCommand {
        stdout: helper_stdout_for_context(&output.stdout, json_output),
        exit_code: 0,
    })
}

pub(crate) fn helper_stdout_for_context(stdout: &[u8], json_output: bool) -> String {
    let mut stdout = if json_output {
        normalized_helper_json_stdout(stdout)
            .unwrap_or_else(|| String::from_utf8_lossy(stdout).into_owned())
    } else {
        String::from_utf8_lossy(stdout).into_owned()
    };

    if !stdout.ends_with('\n') {
        stdout.push('\n');
    }

    stdout
}

pub(crate) fn normalized_helper_json_stdout(stdout: &[u8]) -> Option<String> {
    if let Ok(value) = serde_json::from_slice::<Value>(stdout) {
        return Some(render_structured(&value, true));
    }

    for start in stdout
        .iter()
        .enumerate()
        .filter_map(|(index, byte)| matches!(*byte, b'{' | b'[').then_some(index))
    {
        if let Ok(value) = serde_json::from_slice::<Value>(&stdout[start..]) {
            return Some(render_structured(&value, true));
        }
    }

    None
}

fn unusable_helper_path_error(plan: &DotnetHelperLaunchPlan) -> AppError {
    let reason = plan
        .failure_reason
        .as_deref()
        .unwrap_or(match plan.launch_kind {
            DotnetHelperLaunchKind::Missing => "configured helper path does not exist",
            DotnetHelperLaunchKind::Unsupported => {
                "configured helper path must be a .csproj, .dll, or executable file"
            }
            _ => "helper launch plan does not include a usable invocation path",
        });
    AppError::tool_invocation(
        "dotnet-helper",
        &format!(
            "{reason}: configured='{}', resolved='{}'",
            plan.configured_path.display(),
            plan.resolved_path.display()
        ),
    )
}

fn helper_invocation_label(plan: &DotnetHelperLaunchPlan) -> String {
    match plan.launch_kind {
        DotnetHelperLaunchKind::DefaultProject | DotnetHelperLaunchKind::Dll => "dotnet <dll>",
        DotnetHelperLaunchKind::Project => "dotnet run",
        DotnetHelperLaunchKind::Executable => "dotnet-helper executable",
        DotnetHelperLaunchKind::Missing | DotnetHelperLaunchKind::Unsupported => "dotnet-helper",
    }
    .to_string()
}

fn project_input_paths(project_path: &Path) -> Vec<PathBuf> {
    let project_dir = project_path.parent().unwrap_or_else(|| Path::new("."));
    let mut inputs = Vec::new();
    collect_project_inputs(project_dir, &mut inputs);
    inputs
}

fn collect_project_inputs(dir: &Path, inputs: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };

    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };

        if file_type.is_dir() {
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if matches!(name.as_ref(), "bin" | "obj") {
                continue;
            }
            collect_project_inputs(&path, inputs);
        } else if file_type.is_file() && is_project_input(&path) {
            inputs.push(path);
        }
    }
}

fn is_project_input(path: &Path) -> bool {
    matches!(
        path.extension().and_then(|extension| extension.to_str()),
        Some("cs" | "csproj" | "props" | "targets" | "json" | "config" | "editorconfig")
    )
}

fn modified_at(path: &Path) -> std::io::Result<SystemTime> {
    fs::metadata(path)?.modified()
}

fn path_has_extension(path: &Path, expected: &str) -> bool {
    path.extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case(expected))
}

fn is_executable_candidate(path: &Path) -> bool {
    if cfg!(windows) {
        return path_has_extension(path, "exe");
    }

    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::metadata(path)
            .map(|metadata| metadata.permissions().mode() & 0o111 != 0)
            .unwrap_or(false)
    }

    #[cfg(not(unix))]
    {
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AppConfig, ConfigProvenance, ConfigValueSource};
    use std::fs::{self, File};
    use std::thread;
    use std::time::Duration;
    use tempfile::TempDir;

    #[test]
    fn fresh_default_project_uses_built_dll_without_build() {
        let fixture = helper_fixture();
        let dll = fixture.default_output();
        touch(&dll);
        wait_for_distinct_mtime();

        let plan = resolve_dotnet_helper_launch(&fixture.config(), &ConfigProvenance::default());

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::DefaultProject);
        assert_eq!(plan.invocation_path, Some(dll.clone()));
        assert_eq!(plan.build_output_path, Some(dll));
        assert!(plan.available);
        assert!(!plan.build_needed);
        assert_eq!(plan.build_reason, None);
    }

    #[test]
    fn missing_default_output_requires_build() {
        let fixture = helper_fixture();

        let plan = resolve_dotnet_helper_launch(&fixture.config(), &ConfigProvenance::default());

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::DefaultProject);
        assert!(!plan.available);
        assert!(plan.build_needed);
        assert_eq!(
            plan.build_reason,
            Some(DotnetHelperBuildReason::DefaultOutputMissing)
        );
    }

    #[test]
    fn stale_default_output_requires_build() {
        let fixture = helper_fixture();
        let dll = fixture.default_output();
        touch(&dll);
        wait_for_distinct_mtime();
        touch(&fixture.project_dir.join("Program.cs"));

        let plan = resolve_dotnet_helper_launch(&fixture.config(), &ConfigProvenance::default());

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::DefaultProject);
        assert!(!plan.available);
        assert!(plan.build_needed);
        assert_eq!(
            plan.build_reason,
            Some(DotnetHelperBuildReason::DefaultOutputStale)
        );
    }

    #[test]
    fn lazy_resolve_skips_the_staleness_walk() {
        let fixture = helper_fixture();
        let dll = fixture.default_output();
        touch(&dll);
        wait_for_distinct_mtime();
        touch(&fixture.project_dir.join("Program.cs"));

        let eager = resolve_dotnet_helper_launch(&fixture.config(), &ConfigProvenance::default());
        let lazy =
            resolve_dotnet_helper_launch_lazy(&fixture.config(), &ConfigProvenance::default());

        // Same inputs: the eager resolve sees the stale output, the lazy one does not look.
        assert!(eager.stale_checked);
        assert!(eager.build_needed);
        assert_eq!(
            eager.build_reason,
            Some(DotnetHelperBuildReason::DefaultOutputStale)
        );

        assert!(!lazy.stale_checked);
        assert!(!lazy.build_needed);
        assert_eq!(lazy.build_reason, None);
        assert!(lazy.available);
        assert_eq!(lazy.invocation_path, Some(dll));
    }

    #[test]
    fn lazy_resolve_still_reports_a_missing_default_output() {
        let fixture = helper_fixture();

        let plan =
            resolve_dotnet_helper_launch_lazy(&fixture.config(), &ConfigProvenance::default());

        // A missing output is a plain existence check, not a walk, so laziness must not hide it.
        assert!(!plan.stale_checked);
        assert!(plan.build_needed);
        assert_eq!(
            plan.build_reason,
            Some(DotnetHelperBuildReason::DefaultOutputMissing)
        );
    }

    #[test]
    fn lazy_resolve_marks_non_default_launch_kinds_as_checked() {
        let dir = TempDir::new().unwrap();
        let dll = dir.path().join("helper.dll");
        touch(&dll);
        let mut config = AppConfig::default();
        config.tools.dotnet_tools_path = dll.display().to_string();

        let plan = resolve_dotnet_helper_launch_lazy(&config, &ConfigProvenance::default());

        // Only the default project has a build output that can go stale.
        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Dll);
        assert!(plan.stale_checked);
    }

    #[test]
    fn checked_in_config_default_project_requires_build_when_output_missing() {
        let fixture = helper_fixture();
        let dll = fixture.default_output();

        let plan = resolve_dotnet_helper_launch(
            &fixture.config(),
            &provenance(ConfigValueSource::CheckedInConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::DefaultProject);
        assert_eq!(plan.invocation_path, Some(dll.clone()));
        assert_eq!(plan.build_output_path, Some(dll));
        assert!(!plan.available);
        assert!(plan.build_needed);
        assert_eq!(
            plan.build_reason,
            Some(DotnetHelperBuildReason::DefaultOutputMissing)
        );
    }

    #[test]
    fn explicit_config_default_project_uses_fresh_built_dll_without_build() {
        let fixture = helper_fixture();
        let dll = fixture.default_output();
        touch(&dll);
        wait_for_distinct_mtime();

        let plan = resolve_dotnet_helper_launch(
            &fixture.config(),
            &provenance(ConfigValueSource::ExplicitConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::DefaultProject);
        assert_eq!(plan.invocation_path, Some(dll.clone()));
        assert_eq!(plan.build_output_path, Some(dll));
        assert!(plan.available);
        assert!(!plan.build_needed);
        assert_eq!(plan.build_reason, None);
    }

    #[test]
    fn configured_dll_invokes_directly() {
        let dir = TempDir::new().unwrap();
        let dll = dir.path().join("helper.dll");
        touch(&dll);

        let plan = resolve_dotnet_helper_launch(
            &config_for(&dll),
            &provenance(ConfigValueSource::ExplicitConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Dll);
        assert_eq!(plan.invocation_path, Some(dll));
        assert!(plan.available);
        assert!(!plan.build_needed);
    }

    #[test]
    fn configured_executable_invokes_directly() {
        let dir = TempDir::new().unwrap();
        let executable = if cfg!(windows) {
            dir.path().join("helper.exe")
        } else {
            dir.path().join("helper")
        };
        touch(&executable);
        make_executable(&executable);

        let plan = resolve_dotnet_helper_launch(
            &config_for(&executable),
            &provenance(ConfigValueSource::LocalConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Executable);
        assert_eq!(plan.invocation_path, Some(executable));
        assert!(plan.available);
        assert!(!plan.build_needed);
    }

    #[test]
    fn configured_project_is_preserved_as_project_launch() {
        let dir = TempDir::new().unwrap();
        let project = dir.path().join("Custom.Helper.csproj");
        touch(&project);

        let plan = resolve_dotnet_helper_launch(
            &config_for(&project),
            &provenance(ConfigValueSource::ExplicitConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Project);
        assert_eq!(plan.invocation_path, Some(project));
        assert!(plan.available);
        assert!(!plan.build_needed);
    }

    #[test]
    fn missing_configured_path_is_reported() {
        let dir = TempDir::new().unwrap();
        let missing = dir.path().join("missing.dll");

        let plan = resolve_dotnet_helper_launch(
            &config_for(&missing),
            &provenance(ConfigValueSource::ExplicitConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Missing);
        assert!(!plan.available);
        assert_eq!(plan.invocation_path, None);
        assert!(plan.failure_reason.is_some());
    }

    #[test]
    fn unsupported_directory_is_reported() {
        let dir = TempDir::new().unwrap();
        let helper_dir = dir.path().join("helper");
        fs::create_dir_all(&helper_dir).unwrap();

        let plan = resolve_dotnet_helper_launch(
            &config_for(&helper_dir),
            &provenance(ConfigValueSource::ExplicitConfig),
        );

        assert_eq!(plan.launch_kind, DotnetHelperLaunchKind::Unsupported);
        assert!(!plan.available);
        assert_eq!(plan.invocation_path, None);
        assert!(plan.failure_reason.is_some());
    }

    fn helper_fixture() -> HelperFixture {
        let root = TempDir::new().unwrap();
        let project_dir = root.path().join("dotnet-tools/src/Spirectl.DotnetTools");
        fs::create_dir_all(&project_dir).unwrap();
        touch(&project_dir.join("Spirectl.DotnetTools.csproj"));
        touch(&project_dir.join("Program.cs"));

        HelperFixture {
            _root: root,
            project_dir,
        }
    }

    struct HelperFixture {
        _root: TempDir,
        project_dir: PathBuf,
    }

    impl HelperFixture {
        fn project_path(&self) -> PathBuf {
            self.project_dir.join("Spirectl.DotnetTools.csproj")
        }

        fn default_output(&self) -> PathBuf {
            default_project_build_output(&self.project_path())
        }

        fn config(&self) -> AppConfig {
            let mut config = AppConfig::default();
            config.tools.dotnet_tools_path = self.project_path().display().to_string();
            config
        }
    }

    fn config_for(path: &Path) -> AppConfig {
        let mut config = AppConfig::default();
        config.tools.dotnet_tools_path = path.display().to_string();
        config
    }

    fn provenance(source: ConfigValueSource) -> ConfigProvenance {
        ConfigProvenance {
            dotnet_tools_path: source,
            ..Default::default()
        }
    }

    fn touch(path: &Path) {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        File::create(path).unwrap();
    }

    fn wait_for_distinct_mtime() {
        thread::sleep(Duration::from_millis(25));
    }

    fn make_executable(path: &Path) {
        #[cfg(not(unix))]
        let _ = path;

        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mut permissions = fs::metadata(path).unwrap().permissions();
            permissions.set_mode(0o755);
            fs::set_permissions(path, permissions).unwrap();
        }
    }
}
