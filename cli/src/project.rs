use std::collections::BTreeMap;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Instant;
use std::time::{SystemTime, UNIX_EPOCH};

use clap::{Args, Subcommand, ValueEnum};
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use serde_json::{Value, json};

use crate::{
    AppContext, AppError, DeployArgs, LifecycleWaitArgs, PathOverrides, lifecycle,
    maybe_cache_resolved_local_paths,
    project_scaffold::{ProjectScaffoldCommand, execute_project_scaffold_json},
    resolve_code_search_paths, resolve_dotnet_tools_path, resolve_scene_search_paths,
};

const DEFAULT_TIMEOUT_MS: u64 = 30_000;
const DEFAULT_INTERVAL_MS: u64 = 250;

#[derive(Debug, Clone, Args)]
pub struct ProjectCommand {
    #[command(subcommand)]
    pub command: ProjectSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ProjectSubcommand {
    Recover(ProjectRecoverArgs),
    Profile(ProjectProfileCommand),
    Hook(ProjectHookCommand),
    Scaffold(ProjectScaffoldCommand),
}

#[derive(Debug, Clone, Copy, ValueEnum)]
pub enum RecoverKindArg {
    Decompile,
    #[value(name = "recovered-project")]
    RecoveredProject,
    All,
}

#[derive(Debug, Clone, Args)]
pub struct ProjectRecoverArgs {
    #[arg(long, value_enum, default_value_t = RecoverKindArg::All)]
    pub kind: RecoverKindArg,
}

#[derive(Debug, Clone, Args)]
pub struct ProjectProfileCommand {
    #[command(subcommand)]
    pub command: ProjectProfileSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ProjectProfileSubcommand {
    List,
    Show(ProjectProfileNameArgs),
    Run(ProjectProfileNameArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ProjectProfileNameArgs {
    pub name: String,
}

#[derive(Debug, Clone, Args)]
pub struct ProjectHookCommand {
    #[command(subcommand)]
    pub command: ProjectHookSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ProjectHookSubcommand {
    List,
    Show(ProjectHookNameArgs),
    Run(ProjectHookRunArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ProjectHookNameArgs {
    pub name: String,
}

#[derive(Debug, Clone, Args)]
pub struct ProjectHookRunArgs {
    pub name: String,
    #[arg(long)]
    pub input: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct ProfilesFile {
    profiles: BTreeMap<String, ProfileDefinition>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct HooksFile {
    hooks: BTreeMap<String, HookDefinition>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[serde(deny_unknown_fields)]
struct ProfileDefinition {
    description: Option<String>,
    steps: Vec<ProfileStep>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HookDefinition {
    description: Option<String>,
    command: String,
    args: Option<Vec<String>>,
    cwd: Option<PathBuf>,
    env: Option<BTreeMap<String, String>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HookResponse {
    output: Value,
    artifacts: Option<Vec<HookArtifactDeclaration>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HookArtifactDeclaration {
    path: String,
    kind: Option<String>,
}

#[derive(Debug, Clone)]
pub(crate) struct HookInvocationContext {
    pub scenario_name: Option<String>,
    pub scenario_path: Option<String>,
    pub scenario_dir: Option<PathBuf>,
    pub step_index: Option<usize>,
    pub step_requested_id: Option<String>,
    pub step_canonical_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "kebab-case")]
enum ProfileStepKind {
    InstallBridge,
    Deploy,
    Launch,
    Attach,
    Command,
}

impl ProfileStepKind {
    fn as_str(&self) -> &'static str {
        match self {
            Self::InstallBridge => "install-bridge",
            Self::Deploy => "deploy",
            Self::Launch => "launch",
            Self::Attach => "attach",
            Self::Command => "command",
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[serde(deny_unknown_fields)]
struct ProfileStep {
    kind: ProfileStepKind,
    path: Option<PathBuf>,
    build: Option<bool>,
    restart: Option<bool>,
    verify: Option<bool>,
    timeout_ms: Option<u64>,
    interval_ms: Option<u64>,
    verify_stable_ms: Option<u64>,
    command: Option<String>,
    args: Option<Vec<String>>,
    cwd: Option<PathBuf>,
    env: Option<BTreeMap<String, String>>,
}

impl ProfileStep {
    fn timeout_ms(&self) -> u64 {
        self.timeout_ms.unwrap_or(DEFAULT_TIMEOUT_MS)
    }

    fn interval_ms(&self) -> u64 {
        self.interval_ms.unwrap_or(DEFAULT_INTERVAL_MS)
    }

    fn build(&self) -> bool {
        self.build.unwrap_or(false)
    }

    fn restart(&self) -> bool {
        self.restart.unwrap_or(false)
    }

    fn verify(&self) -> bool {
        self.verify.unwrap_or(false)
    }

    fn verify_stable_ms(&self) -> u64 {
        self.verify_stable_ms.unwrap_or(0)
    }
}

pub(crate) fn execute_project_command_json(
    command: ProjectCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match command.command {
        ProjectSubcommand::Recover(args) => execute_project_recover_json(args, context),
        ProjectSubcommand::Profile(profile) => execute_project_profile_json(profile, context),
        ProjectSubcommand::Hook(hook) => execute_project_hook_json(hook, context),
        ProjectSubcommand::Scaffold(scaffold) => execute_project_scaffold_json(scaffold, context),
    }
}

fn execute_project_recover_json(
    args: ProjectRecoverArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let managed =
        crate::toolchain::resolve_managed_roots(context.config, &context.config_origin.base_dir);
    let manifests_dir = managed.toolchain_dir.join("manifests");
    std::fs::create_dir_all(&manifests_dir).map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_recover_write_failed",
                "message": "failed to create manifests directory",
                "path": manifests_dir.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    let mut results = Vec::new();
    match args.kind {
        RecoverKindArg::Decompile => {
            results.push(run_decompile_recover(
                &managed.toolchain_dir,
                &manifests_dir,
                context,
            )?);
        }
        RecoverKindArg::RecoveredProject => {
            results.push(run_recovered_project_recover(
                &managed.toolchain_dir,
                &manifests_dir,
                context,
            )?);
        }
        RecoverKindArg::All => {
            results.push(run_decompile_recover(
                &managed.toolchain_dir,
                &manifests_dir,
                context,
            )?);
            results.push(run_recovered_project_recover(
                &managed.toolchain_dir,
                &manifests_dir,
                context,
            )?);
        }
    }

    if results.iter().any(|result| result["status"] == "error") {
        return Err(AppError {
            exit_code: 4,
            payload: json!({
                "results": results,
            }),
        });
    }

    Ok(json!({ "results": results }))
}

fn execute_project_profile_json(
    command: ProjectProfileCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let loaded = load_profiles_file(context)?;
    match command.command {
        ProjectProfileSubcommand::List => Ok(json!({
            "sourcePath": loaded.path.display().to_string(),
            "profiles": loaded.profiles.profiles.iter().map(|(name, profile)| {
                json!({
                    "name": name,
                    "description": profile.description,
                    "stepKinds": profile.steps.iter().map(|step| step.kind.as_str()).collect::<Vec<_>>(),
                })
            }).collect::<Vec<_>>()
        })),
        ProjectProfileSubcommand::Show(args) => {
            let profile = loaded.profiles.profiles.get(&args.name).ok_or_else(|| {
                profile_error(
                    2,
                    "profile_not_found",
                    &format!("profile '{}' was not found", args.name),
                )
            })?;
            Ok(json!({
                "name": args.name,
                "sourcePath": loaded.path.display().to_string(),
                "profile": resolved_profile_json(profile, &loaded.base_dir, context)?,
            }))
        }
        ProjectProfileSubcommand::Run(args) => {
            execute_project_profile_run_json(&args.name, &loaded, context)
        }
    }
}

fn execute_project_hook_json(
    command: ProjectHookCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let loaded = load_hooks_file(context)?;
    match command.command {
        ProjectHookSubcommand::List => Ok(json!({
            "sourcePath": loaded.path.display().to_string(),
            "hooks": loaded.hooks.hooks.iter().map(|(name, hook)| {
                json!({
                    "name": name,
                    "description": hook.description,
                })
            }).collect::<Vec<_>>()
        })),
        ProjectHookSubcommand::Show(args) => {
            let hook = loaded.hooks.hooks.get(&args.name).ok_or_else(|| {
                hook_error(
                    2,
                    "hook_not_found",
                    &format!("hook '{}' was not found", args.name),
                )
            })?;
            Ok(json!({
                "name": args.name,
                "sourcePath": loaded.path.display().to_string(),
                "hook": resolved_hook_json(hook, &loaded.base_dir),
            }))
        }
        ProjectHookSubcommand::Run(args) => {
            let input = args
                .input
                .as_deref()
                .map(parse_hook_input_json)
                .transpose()?;
            execute_project_hook_run_json(&args.name, input, None, context)
        }
    }
}

pub(crate) struct LoadedProfilesFile {
    path: PathBuf,
    base_dir: PathBuf,
    profiles: ProfilesFile,
}

struct LoadedHooksFile {
    path: PathBuf,
    base_dir: PathBuf,
    hooks: HooksFile,
}

fn load_profiles_file(context: AppContext<'_>) -> Result<LoadedProfilesFile, AppError> {
    let path = if let Some(configured) = context.config.project.profiles_file.as_deref() {
        resolve_path(&context.config_origin.base_dir, Path::new(configured))
    } else {
        context.config_origin.base_dir.join("sts2.profiles.yaml")
    };
    load_profiles_file_at(path, context)
}

pub(crate) fn load_project_profiles(
    project_root: &Path,
    context: AppContext<'_>,
) -> Result<LoadedProfilesFile, AppError> {
    load_profiles_file_at(project_root.join("sts2.profiles.yaml"), context)
}

fn load_profiles_file_at(
    path: PathBuf,
    context: AppContext<'_>,
) -> Result<LoadedProfilesFile, AppError> {
    let raw = std::fs::read_to_string(&path).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "profiles_read_failed",
                "message": "failed to read profiles file",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;
    let profiles = serde_yaml::from_str::<ProfilesFile>(&raw).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "profiles_parse_failed",
                "message": "failed to parse profiles file",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    Ok(LoadedProfilesFile {
        base_dir: path
            .parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| context.config_origin.base_dir.clone()),
        path,
        profiles,
    })
}

fn load_hooks_file(context: AppContext<'_>) -> Result<LoadedHooksFile, AppError> {
    let path = if let Some(configured) = context.config.project.hooks_file.as_deref() {
        resolve_path(&context.config_origin.base_dir, Path::new(configured))
    } else {
        context.config_origin.base_dir.join("sts2.hooks.yaml")
    };

    let raw = std::fs::read_to_string(&path).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hooks_read_failed",
                "message": "failed to read hooks file",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;
    let hooks = serde_yaml::from_str::<HooksFile>(&raw).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hooks_parse_failed",
                "message": "failed to parse hooks file",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    Ok(LoadedHooksFile {
        base_dir: path
            .parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| context.config_origin.base_dir.clone()),
        path,
        hooks,
    })
}

fn resolved_profile_json(
    profile: &ProfileDefinition,
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let mut steps = Vec::new();
    for step in &profile.steps {
        let mut value = serde_json::Map::new();
        value.insert(
            "kind".to_string(),
            Value::String(step.kind.as_str().to_string()),
        );
        match step.kind {
            ProfileStepKind::InstallBridge => {}
            ProfileStepKind::Launch | ProfileStepKind::Attach => {
                value.insert("timeoutMs".to_string(), json!(step.timeout_ms()));
                value.insert("intervalMs".to_string(), json!(step.interval_ms()));
            }
            ProfileStepKind::Deploy => {
                let path = step
                    .path
                    .as_deref()
                    .map(|path| resolve_path(base_dir, path).display().to_string());
                value.insert("path".to_string(), json!(path));
                value.insert("build".to_string(), json!(step.build()));
                value.insert("restart".to_string(), json!(step.restart()));
                value.insert("verify".to_string(), json!(step.verify()));
                value.insert("timeoutMs".to_string(), json!(step.timeout_ms()));
                value.insert("intervalMs".to_string(), json!(step.interval_ms()));
                value.insert("verifyStableMs".to_string(), json!(step.verify_stable_ms()));
            }
            ProfileStepKind::Command => {
                value.insert("command".to_string(), json!(step.command.as_deref()));
                value.insert(
                    "args".to_string(),
                    json!(interpolate_profile_values(
                        step.args.as_deref().unwrap_or_default(),
                        base_dir,
                        context,
                    )?),
                );
                let cwd = step
                    .cwd
                    .as_deref()
                    .map(|cwd| resolve_path(base_dir, cwd))
                    .unwrap_or_else(|| base_dir.to_path_buf());
                value.insert("cwd".to_string(), json!(cwd.display().to_string()));
                value.insert(
                    "env".to_string(),
                    json!(interpolate_profile_env(
                        step.env.as_ref(),
                        base_dir,
                        context
                    )?),
                );
            }
        }
        steps.push(Value::Object(value));
    }

    Ok(json!({
        "description": profile.description,
        "steps": steps,
    }))
}

fn resolved_hook_json(hook: &HookDefinition, base_dir: &Path) -> Value {
    json!({
        "description": hook.description,
        "command": resolve_hook_command(base_dir, &hook.command),
        "args": hook.args.clone().unwrap_or_default(),
        "cwd": hook.cwd.as_deref().map(|cwd| resolve_path(base_dir, cwd).display().to_string()),
        "env": hook.env.clone().unwrap_or_default(),
    })
}

fn parse_hook_input_json(raw: &str) -> Result<Value, AppError> {
    serde_json::from_str(raw).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_hook_input",
                "message": format!("failed to parse --input JSON: {source}"),
            }
        }),
    })
}

pub(crate) fn execute_project_hook_run_json(
    name: &str,
    input: Option<Value>,
    invocation: Option<HookInvocationContext>,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let loaded = load_hooks_file(context)?;
    let hook = loaded.hooks.hooks.get(name).ok_or_else(|| {
        hook_error(
            2,
            "hook_not_found",
            &format!("hook '{}' was not found", name),
        )
    })?;
    run_hook(name, hook, &loaded, input, invocation)
}

fn run_hook(
    name: &str,
    hook: &HookDefinition,
    loaded: &LoadedHooksFile,
    input: Option<Value>,
    invocation: Option<HookInvocationContext>,
) -> Result<Value, AppError> {
    let command = resolve_hook_command(&loaded.base_dir, &hook.command);
    let cwd = hook
        .cwd
        .as_deref()
        .map(|cwd| resolve_path(&loaded.base_dir, cwd))
        .unwrap_or_else(|| loaded.base_dir.clone());
    let args = hook.args.clone().unwrap_or_default();
    let stdin_payload = json!({
        "hookName": name,
        "input": input.clone().unwrap_or(Value::Null),
        "scenario": invocation.as_ref().map(|value| json!({
            "name": value.scenario_name,
            "path": value.scenario_path,
            "scenarioDir": value.scenario_dir.as_ref().map(|path| path.display().to_string()),
        })).unwrap_or(Value::Null),
        "step": invocation.as_ref().map(|value| json!({
            "index": value.step_index,
            "requestedId": value.step_requested_id,
            "canonicalId": value.step_canonical_id,
        })).unwrap_or(Value::Null),
    });
    let stdin_text = serde_json::to_vec(&stdin_payload).expect("hook stdin json");

    let mut child = Command::new(&command);
    child
        .args(&args)
        .current_dir(&cwd)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(env) = &hook.env {
        child.envs(env);
    }
    let mut child = child.spawn().map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "hook_spawn_failed",
                "message": format!("failed to spawn hook '{}': {source}", name),
                "name": name,
                "command": command,
            }
        }),
    })?;

    if let Some(stdin) = child.stdin.as_mut() {
        use std::io::Write;
        stdin.write_all(&stdin_text).map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "hook_spawn_failed",
                    "message": format!("failed to write hook stdin for '{}': {source}", name),
                    "name": name,
                }
            }),
        })?;
    }

    let output = child.wait_with_output().map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "hook_spawn_failed",
                "message": format!("failed while waiting for hook '{}': {source}", name),
                "name": name,
            }
        }),
    })?;

    if !output.status.success() {
        return Err(AppError {
            exit_code: output.status.code().unwrap_or(4),
            payload: json!({
                "error": {
                    "code": "hook_nonzero_exit",
                    "message": format!("hook '{}' exited with a nonzero status", name),
                    "name": name,
                    "status": output.status.code(),
                    "stdout": String::from_utf8_lossy(&output.stdout).trim().to_string(),
                    "stderr": String::from_utf8_lossy(&output.stderr).trim().to_string(),
                }
            }),
        });
    }

    let response =
        serde_json::from_slice::<HookResponse>(&output.stdout).map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "hook_invalid_stdout_json",
                    "message": format!("hook '{}' did not return valid JSON: {source}", name),
                    "name": name,
                    "stdout": String::from_utf8_lossy(&output.stdout).trim().to_string(),
                }
            }),
        })?;
    let artifacts = response
        .artifacts
        .unwrap_or_default()
        .into_iter()
        .map(|artifact| resolve_declared_artifact(name, &cwd, artifact))
        .collect::<Result<Vec<_>, _>>()?;

    Ok(json!({
        "name": name,
        "sourcePath": loaded.path.display().to_string(),
        "hook": resolved_hook_json(hook, &loaded.base_dir),
        "result": {
            "output": response.output,
            "artifacts": artifacts,
        }
    }))
}

fn resolve_hook_command(base_dir: &Path, command: &str) -> String {
    let path = Path::new(command);
    if path.is_absolute() || command.contains('/') || command.contains('\\') {
        resolve_path(base_dir, path).display().to_string()
    } else {
        command.to_string()
    }
}

fn resolve_declared_artifact(
    hook_name: &str,
    cwd: &Path,
    artifact: HookArtifactDeclaration,
) -> Result<Value, AppError> {
    let path = resolve_path(cwd, Path::new(&artifact.path));
    if !path.exists() {
        return Err(AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "hook_invalid_declared_artifact",
                    "message": format!("hook '{}' declared an artifact that does not exist", hook_name),
                    "name": hook_name,
                    "path": path.display().to_string(),
                }
            }),
        });
    }

    Ok(json!({
        "path": path.display().to_string(),
        "kind": artifact.kind.unwrap_or_else(|| "file".to_string()),
    }))
}

fn execute_project_profile_run_json(
    name: &str,
    loaded: &LoadedProfilesFile,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let profile = loaded.profiles.profiles.get(name).ok_or_else(|| {
        profile_error(
            2,
            "profile_not_found",
            &format!("profile '{}' was not found", name),
        )
    })?;

    let mut steps = Vec::new();
    for step in &profile.steps {
        let started = Instant::now();
        match execute_profile_step(step, &loaded.base_dir, context) {
            Ok(result) => steps.push(json!({
                "kind": step.kind.as_str(),
                "status": "ok",
                "elapsedMs": started.elapsed().as_millis(),
                "result": result,
            })),
            Err(error) => {
                steps.push(json!({
                    "kind": step.kind.as_str(),
                    "status": "error",
                    "elapsedMs": started.elapsed().as_millis(),
                    "error": error.payload["error"].clone(),
                }));
                return Err(AppError {
                    exit_code: error.exit_code,
                    payload: json!({
                        "name": name,
                        "sourcePath": loaded.path.display().to_string(),
                        "steps": steps,
                    }),
                });
            }
        }
    }

    Ok(json!({
        "name": name,
        "sourcePath": loaded.path.display().to_string(),
        "steps": steps,
    }))
}

pub(crate) fn project_profile_exists(loaded: &LoadedProfilesFile, name: &str) -> bool {
    loaded.profiles.profiles.contains_key(name)
}

pub(crate) fn run_project_profile_by_name(
    loaded: &LoadedProfilesFile,
    name: &str,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_project_profile_run_json(name, loaded, context)
}

fn run_decompile_recover(
    toolchain_dir: &Path,
    manifests_dir: &Path,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let search_paths = resolve_code_search_paths(context.config, PathOverrides::default(), false)
        .map_err(|message| profile_error(2, "invalid_recover_config", &message))?;
    let _ = maybe_cache_resolved_local_paths(
        context,
        search_paths.game_path.as_deref(),
        search_paths.used_discovered_game_path(),
        Some(&search_paths.assemblies_dir),
        search_paths.derived_assemblies_dir_from_game_path(),
    );
    let output_dir = toolchain_dir.join("decompile");
    if output_dir.exists() {
        std::fs::remove_dir_all(&output_dir).map_err(|source| AppError {
            exit_code: 5,
            payload: json!({
                "error": {
                    "code": "project_recover_write_failed",
                    "message": "failed to clear previous decompile output",
                    "path": output_dir.display().to_string(),
                    "details": source.to_string(),
                }
            }),
        })?;
    }
    std::fs::create_dir_all(&output_dir).map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_recover_write_failed",
                "message": "failed to create decompile output directory",
                "path": output_dir.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    let helper = run_decompile_export(context.config, &search_paths.assemblies_dir, &output_dir)?;
    let manifest_path = manifests_dir.join("decompile.json");
    let generated_at = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let manifest = json!({
        "kind": "decompile",
        "sourceRoots": {
            "assembliesDir": search_paths.assemblies_dir.display().to_string(),
        },
        "backend": {
            "id": "embedded-ilspy",
            "helperProject": resolve_dotnet_tools_path(context.config).display().to_string(),
        },
        "outputPath": output_dir.display().to_string(),
        "generatedAtUnixSeconds": generated_at,
        "notes": [
            "The CLI owns this decompile corpus and rewrites only the managed decompile subtree on each run."
        ],
        "summary": helper,
    });
    std::fs::write(
        &manifest_path,
        serde_json::to_string_pretty(&manifest).expect("manifest json"),
    )
    .map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_recover_write_failed",
                "message": "failed to write decompile manifest",
                "path": manifest_path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    Ok(json!({
        "kind": "decompile",
        "status": "ok",
        "outputPath": output_dir.display().to_string(),
        "manifestPath": manifest_path.display().to_string(),
    }))
}

fn run_decompile_export(
    config: &crate::AppConfig,
    assemblies_dir: &Path,
    output_dir: &Path,
) -> Result<Value, AppError> {
    let project_path = resolve_dotnet_tools_path(config);
    let output = Command::new("dotnet")
        .arg("run")
        .arg("--verbosity")
        .arg("quiet")
        .arg("--project")
        .arg(&project_path)
        .arg("--")
        .arg("decompile-export")
        .arg(output_dir)
        .arg("--assemblies-dir")
        .arg(assemblies_dir)
        .arg("--json")
        .output()
        .map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": "dotnet run",
                    "message": format!("failed to invoke .NET helper tool: {source}"),
                }
            }),
        })?;

    if !output.status.success() {
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(AppError {
            exit_code: output.status.code().unwrap_or(4),
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": "dotnet run",
                    "message": if !stdout.is_empty() { stdout } else { stderr },
                }
            }),
        });
    }

    parse_helper_json(&output.stdout).map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "tool_response_parse_failed",
                "message": source.to_string(),
            }
        }),
    })
}

fn parse_helper_json<T>(stdout: &[u8]) -> Result<T, serde_json::Error>
where
    T: DeserializeOwned,
{
    match serde_json::from_slice(stdout) {
        Ok(value) => Ok(value),
        Err(original) => {
            for start in stdout
                .iter()
                .enumerate()
                .filter_map(|(index, byte)| matches!(*byte, b'{' | b'[').then_some(index))
            {
                if let Ok(value) = serde_json::from_slice(&stdout[start..]) {
                    return Ok(value);
                }
            }
            Err(original)
        }
    }
}

fn run_recovered_project_recover(
    toolchain_dir: &Path,
    manifests_dir: &Path,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let diagnostic =
        crate::toolchain::gdre_tool_json(context.config, context.config_provenance.gdre_path);
    if diagnostic["status"] != "available" {
        return Ok(gdre_unavailable_result(context));
    }

    let search_paths = resolve_scene_search_paths(context.config, PathOverrides::default(), false)
        .map_err(|message| profile_error(2, "invalid_recover_config", &message))?;
    let _ = maybe_cache_resolved_local_paths(
        context,
        search_paths.game_path.as_deref(),
        search_paths.used_discovered_game_path(),
        search_paths.assemblies_dir.as_deref(),
        search_paths.derived_assemblies_dir_from_game_path(),
    );
    let output_dir = toolchain_dir.join("recovered-project");
    if output_dir.exists() {
        std::fs::remove_dir_all(&output_dir).map_err(|source| AppError {
            exit_code: 5,
            payload: json!({
                "error": {
                    "code": "project_recover_write_failed",
                    "message": "failed to clear previous recovered-project output",
                    "path": output_dir.display().to_string(),
                    "details": source.to_string(),
                }
            }),
        })?;
    }
    std::fs::create_dir_all(&output_dir).map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_recover_write_failed",
                "message": "failed to create recovered-project output directory",
                "path": output_dir.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    let backend = run_gdre_recover(context.config, &search_paths.resources_dir, &output_dir)?;
    let manifest_path = manifests_dir.join("recovered-project.json");
    let generated_at = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let manifest = json!({
        "kind": "recovered-project",
        "sourceRoots": {
            "resourceDir": search_paths.resources_dir.display().to_string(),
        },
        "backend": backend,
        "outputPath": output_dir.display().to_string(),
        "generatedAtUnixSeconds": generated_at,
        "notes": [
            "The CLI owns this recovered-project export and rewrites only the managed recovered-project subtree on each run."
        ],
    });
    std::fs::write(
        &manifest_path,
        serde_json::to_string_pretty(&manifest).expect("manifest json"),
    )
    .map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_recover_write_failed",
                "message": "failed to write recovered-project manifest",
                "path": manifest_path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;

    Ok(json!({
        "kind": "recovered-project",
        "status": "ok",
        "outputPath": output_dir.display().to_string(),
        "manifestPath": manifest_path.display().to_string(),
        "tool": backend,
    }))
}

fn run_gdre_recover(
    config: &crate::AppConfig,
    source_dir: &Path,
    output_dir: &Path,
) -> Result<Value, AppError> {
    let gdre_path = config.tools.gdre_path.as_deref().ok_or_else(|| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "tool_invocation_failed",
                "command": "gdre",
                "message": "tools.gdrePath must be set before running recovered-project export",
            }
        }),
    })?;
    let gdre_path = PathBuf::from(gdre_path);
    let version = run_gdre_version(&gdre_path)?;
    let gdre_source = resolve_gdre_source_path(source_dir);
    let output = Command::new(&gdre_path)
        .arg("--headless")
        .arg(format!("--recover={}", gdre_source.display()))
        .arg(format!("--output={}", output_dir.display()))
        .output()
        .map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": gdre_path.display().to_string(),
                    "message": format!("failed to invoke GDRE: {source}"),
                }
            }),
        })?;

    if !output.status.success() {
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(AppError {
            exit_code: output.status.code().unwrap_or(4),
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": gdre_path.display().to_string(),
                    "message": if !stderr.is_empty() { stderr } else { stdout },
                }
            }),
        });
    }

    Ok(json!({
        "id": "gdre",
        "path": gdre_path.display().to_string(),
        "version": version,
    }))
}

fn resolve_gdre_source_path(source_dir: &Path) -> PathBuf {
    let pck_path = source_dir.join("SlayTheSpire2.pck");
    if pck_path.is_file() {
        return pck_path;
    }

    source_dir.to_path_buf()
}

fn run_gdre_version(gdre_path: &Path) -> Result<String, AppError> {
    let output = Command::new(gdre_path)
        .arg("--headless")
        .arg("--gdre-version")
        .output()
        .map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": gdre_path.display().to_string(),
                    "message": format!("failed to invoke GDRE for version detection: {source}"),
                }
            }),
        })?;

    if !output.status.success() {
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(AppError {
            exit_code: output.status.code().unwrap_or(4),
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": gdre_path.display().to_string(),
                    "message": if !stderr.is_empty() { stderr } else { stdout },
                }
            }),
        });
    }

    let version = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if version.is_empty() {
        return Ok("unknown".to_string());
    }

    Ok(version)
}

fn gdre_unavailable_result(context: AppContext<'_>) -> Value {
    let diagnostic =
        crate::toolchain::gdre_tool_json(context.config, context.config_provenance.gdre_path);
    let status = diagnostic
        .get("status")
        .and_then(Value::as_str)
        .unwrap_or("unknown");
    let failure_code = diagnostic
        .get("failureCode")
        .and_then(Value::as_str)
        .unwrap_or("gdre_recovered_project_unimplemented");
    let message = match status {
        "not-configured" => "Set tools.gdrePath to enable recovered-project export workflows.",
        "missing" => "Configured GDRE executable was not found for recovered-project export.",
        _ => "GDRE-backed recovered-project export is not implemented in this build.",
    };

    let mut result = serde_json::Map::new();
    result.insert(
        "kind".to_string(),
        Value::String("recovered-project".to_string()),
    );
    result.insert("status".to_string(), Value::String("error".to_string()));
    result.insert(
        "failureCode".to_string(),
        Value::String(failure_code.to_string()),
    );
    result.insert("message".to_string(), Value::String(message.to_string()));
    result.insert("tool".to_string(), diagnostic.clone());

    for key in ["path", "provenance", "notes"] {
        if let Some(value) = diagnostic.get(key) {
            result.insert(key.to_string(), value.clone());
        }
    }

    Value::Object(result)
}

fn resolve_path(base_dir: &Path, path: &Path) -> PathBuf {
    let combined = if path.is_absolute() {
        path.to_path_buf()
    } else {
        base_dir.join(path)
    };
    normalize_path(&combined)
}

fn execute_profile_step(
    step: &ProfileStep,
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match step.kind {
        ProfileStepKind::InstallBridge => lifecycle::execute_game_install_bridge_json(
            crate::GameInstallBridgeArgs::default(),
            context,
        ),
        ProfileStepKind::Launch => lifecycle::execute_game_launch_json(
            LifecycleWaitArgs {
                timeout_ms: step.timeout_ms(),
                interval_ms: step.interval_ms(),
                rpc_timeout_ms: 5_000,
            },
            context,
        ),
        ProfileStepKind::Attach => lifecycle::execute_game_attach_json(
            LifecycleWaitArgs {
                timeout_ms: step.timeout_ms(),
                interval_ms: step.interval_ms(),
                rpc_timeout_ms: 5_000,
            },
            context,
        ),
        ProfileStepKind::Deploy => lifecycle::execute_game_deploy_json(
            DeployArgs {
                path: step
                    .path
                    .as_deref()
                    .map(|path| resolve_path(base_dir, path))
                    .ok_or_else(|| {
                        profile_error(2, "invalid_profile", "deploy steps require a path")
                    })?,
                build: step.build(),
                restart: step.restart(),
                verify: step.verify(),
                timeout_ms: step.timeout_ms(),
                interval_ms: step.interval_ms(),
                rpc_timeout_ms: 5_000,
                verify_stable_ms: step.verify_stable_ms(),
                allow_stale_build: false,
                wait_quiescent_ms: 0,
                quiescent_stable_samples: 3,
                require_quiescent: false,
                launch_args: Vec::new(),
            },
            context,
        ),
        ProfileStepKind::Command => execute_profile_command_step(step, base_dir, context),
    }
}

fn execute_profile_command_step(
    step: &ProfileStep,
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let raw_command = step
        .command
        .as_deref()
        .ok_or_else(|| profile_error(2, "invalid_profile", "command steps require a command"))?;
    let command = interpolate_profile_value(raw_command, base_dir, context)?;
    let command = resolve_hook_command(base_dir, &command);
    let args =
        interpolate_profile_values(step.args.as_deref().unwrap_or_default(), base_dir, context)?;
    let cwd = step
        .cwd
        .as_deref()
        .map(|cwd| resolve_path(base_dir, cwd))
        .unwrap_or_else(|| base_dir.to_path_buf());
    let env = interpolate_profile_env(step.env.as_ref(), base_dir, context)?;

    let output = Command::new(&command)
        .args(&args)
        .current_dir(&cwd)
        .envs(&env)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "profile_command_spawn_failed",
                    "message": format!("failed to spawn profile command: {source}"),
                    "command": command,
                    "cwd": cwd.display().to_string(),
                }
            }),
        })?;

    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).to_string();
    let status = output.status.code().unwrap_or(4);
    let result = json!({
        "command": command,
        "args": args,
        "cwd": cwd.display().to_string(),
        "stdout": stdout,
        "stderr": stderr,
        "status": status,
    });

    if !output.status.success() {
        return Err(AppError {
            exit_code: status,
            payload: json!({
                "error": {
                    "code": "profile_command_failed",
                    "message": "profile command exited with a nonzero status",
                    "command": result["command"].clone(),
                    "args": result["args"].clone(),
                    "cwd": result["cwd"].clone(),
                    "stdout": result["stdout"].clone(),
                    "stderr": result["stderr"].clone(),
                    "status": status,
                }
            }),
        });
    }

    Ok(result)
}

fn interpolate_profile_values(
    values: &[String],
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<Vec<String>, AppError> {
    values
        .iter()
        .map(|value| interpolate_profile_value(value, base_dir, context))
        .collect()
}

fn interpolate_profile_env(
    env: Option<&BTreeMap<String, String>>,
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<BTreeMap<String, String>, AppError> {
    env.unwrap_or(&BTreeMap::new())
        .iter()
        .map(|(key, value)| {
            Ok((
                key.clone(),
                interpolate_profile_value(value, base_dir, context)?,
            ))
        })
        .collect()
}

fn interpolate_profile_value(
    value: &str,
    base_dir: &Path,
    context: AppContext<'_>,
) -> Result<String, AppError> {
    let mut resolved = value.replace("${profileDir}", &base_dir.display().to_string());
    for (variable, config_key, replacement) in [
        (
            "config:game.path",
            "game.path",
            Some(context.config.game.path.as_str()),
        ),
        (
            "config:game.assembliesDir",
            "game.assembliesDir",
            context.config.game.assemblies_dir.as_deref(),
        ),
        (
            "config:game.modsDir",
            "game.modsDir",
            context.config.game.mods_dir.as_deref(),
        ),
    ] {
        let token = format!("${{{variable}}}");
        if resolved.contains(&token) {
            let Some(replacement) = replacement else {
                return Err(profile_variable_unresolved(variable, config_key));
            };
            resolved = resolved.replace(&token, replacement);
        }
    }
    Ok(resolved)
}

pub(crate) fn interpolate_project_value(
    value: &str,
    project_root: &Path,
    context: AppContext<'_>,
) -> Result<String, AppError> {
    interpolate_profile_value(value, project_root, context)
}

fn profile_variable_unresolved(variable: &str, config_key: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "profile_variable_unresolved",
                "message": "profile step references an unset config value",
                "variable": variable,
                "configKey": config_key,
            }
        }),
    }
}

fn normalize_path(path: &Path) -> PathBuf {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            Component::Normal(segment) => normalized.push(segment),
            Component::RootDir | Component::Prefix(_) => {
                normalized.push(component.as_os_str());
            }
        }
    }
    normalized
}

fn profile_error(exit_code: i32, code: &str, message: &str) -> AppError {
    AppError {
        exit_code,
        payload: json!({
            "error": {
                "code": code,
                "message": message,
            }
        }),
    }
}

fn hook_error(exit_code: i32, code: &str, message: &str) -> AppError {
    AppError {
        exit_code,
        payload: json!({
            "error": {
                "code": code,
                "message": message,
            }
        }),
    }
}
