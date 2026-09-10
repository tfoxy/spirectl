use std::ffi::OsStr;
use std::path::{Component, Path, PathBuf};

use clap::{Args, Subcommand};
use serde_json::{Value, json};

use crate::{AppContext, AppError};

#[derive(Debug, Clone, Args)]
pub struct ProjectScaffoldCommand {
    #[command(subcommand)]
    pub command: ProjectScaffoldSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ProjectScaffoldSubcommand {
    #[command(name = "stable-harmony-trampoline")]
    StableHarmonyTrampoline(StableHarmonyScaffoldArgs),
}

#[derive(Debug, Clone, Args)]
pub struct StableHarmonyScaffoldArgs {
    #[arg(long)]
    pub output: PathBuf,
    #[arg(long)]
    pub mod_id: String,
    #[arg(long)]
    pub name: String,
    #[arg(long)]
    pub namespace: String,
    #[arg(long)]
    pub assembly_prefix: Option<String>,
    #[arg(long)]
    pub author: Option<String>,
    #[arg(long)]
    pub package_id: Option<String>,
    #[arg(long)]
    pub enable_guardrails: bool,
    #[arg(long)]
    pub force_empty: bool,
}

pub(crate) fn execute_project_scaffold_json(
    command: ProjectScaffoldCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match command.command {
        ProjectScaffoldSubcommand::StableHarmonyTrampoline(args) => {
            execute_stable_harmony_scaffold_json(args, context)
        }
    }
}

fn execute_stable_harmony_scaffold_json(
    args: StableHarmonyScaffoldArgs,
    _context: AppContext<'_>,
) -> Result<Value, AppError> {
    validate_mod_id(&args.mod_id)?;
    validate_namespace(&args.namespace)?;
    if let Some(assembly_prefix) = args.assembly_prefix.as_deref() {
        validate_assembly_prefix(assembly_prefix)?;
    }

    let output_path = prepare_output_dir(&args.output, args.force_empty)?;
    let mut model = ScaffoldModel::new(args, output_path.clone());
    render_scaffold(&mut model)?;

    Ok(json!({
        "template": "stable-harmony-trampoline",
        "outputPath": model.output_path.display().to_string(),
        "createdFiles": model.created_files,
        "skippedFiles": model.skipped_files,
        "profiles": model.profile_names,
        "guardrails": {
            "enabled": model.guardrails_enabled,
            "msbuildProperty": "EnableHotReloadGuardrails",
            "analyzerProject": format!("src/{}/{}.csproj", model.guardrails_project, model.guardrails_project),
            "overlayEnvironmentVariable": format!("{}_DEV_OVERLAY", model.env_prefix),
        },
        "localConfigExamplePath": "sts2.local.example.yaml",
        "nextCommands": [
            "cp sts2.local.example.yaml sts2.local.yaml",
            format!("sts2 project profile run {}", model.profile_names[2]),
            "sts2 dev mod-reload --project . --wait".to_string(),
        ],
        "notices": [
            {
                "code": "reload-trigger-deferred-to-m61",
                "message": "The generated logic-build profile builds and copies reloadable logic; explicit reload control remains a later spec."
            }
        ],
    }))
}

#[derive(Debug, Clone)]
struct ScaffoldModel {
    output_path: PathBuf,
    mod_id: String,
    display_name: String,
    namespace: String,
    assembly_prefix: String,
    author: Option<String>,
    package_id: String,
    env_prefix: String,
    harmony_owner_id: String,
    shell_project: String,
    contracts_project: String,
    logic_project: String,
    guardrails_project: String,
    tests_project: String,
    guardrails_enabled: bool,
    profile_names: Vec<String>,
    created_files: Vec<String>,
    skipped_files: Vec<String>,
}

impl ScaffoldModel {
    fn new(args: StableHarmonyScaffoldArgs, output_path: PathBuf) -> Self {
        let assembly_prefix = args.assembly_prefix.unwrap_or_else(|| {
            args.namespace
                .rsplit('.')
                .next()
                .expect("namespace has at least one segment")
                .to_string()
        });
        let harmony_owner_id = format!("com.spirectl.hotmod.{}", args.mod_id.replace('-', "."));
        let env_prefix = uppercase_snake(&args.mod_id);
        let package_id = args.package_id.unwrap_or_else(|| args.mod_id.clone());
        let profile_names = vec![
            format!("{}-shell-build", args.mod_id),
            format!("{}-shell-deploy", args.mod_id),
            format!("{}-shell-deploy-restart", args.mod_id),
            format!("{}-logic-build", args.mod_id),
            format!("{}-clean-reload-artifacts", args.mod_id),
        ];

        Self {
            output_path,
            mod_id: args.mod_id,
            display_name: args.name,
            namespace: args.namespace,
            shell_project: format!("{assembly_prefix}.Shell"),
            contracts_project: format!("{assembly_prefix}.Contracts"),
            logic_project: format!("{assembly_prefix}.Logic"),
            guardrails_project: format!("{assembly_prefix}.Guardrails"),
            tests_project: format!("{assembly_prefix}.ReloadTests"),
            guardrails_enabled: args.enable_guardrails,
            assembly_prefix,
            author: args.author,
            package_id,
            env_prefix,
            harmony_owner_id,
            profile_names,
            created_files: Vec::new(),
            skipped_files: Vec::new(),
        }
    }
}

fn prepare_output_dir(path: &Path, force_empty: bool) -> Result<PathBuf, AppError> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    };

    if absolute.exists() {
        if !absolute.is_dir() {
            return Err(scaffold_path_error(
                "project_scaffold_output_not_directory",
                "scaffold output path is not a directory",
                &absolute,
            ));
        }
        let mut entries =
            std::fs::read_dir(&absolute).map_err(|source| scaffold_io_error(&absolute, source))?;
        if entries
            .next()
            .transpose()
            .map_err(|source| scaffold_io_error(&absolute, source))?
            .is_some()
        {
            return Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "project_scaffold_output_not_empty",
                        "message": "scaffold output directory is not empty",
                        "path": absolute.display().to_string(),
                        "resolutions": {
                            "forceEmpty": false,
                            "differentOutputDirectory": true
                        }
                    }
                }),
            });
        }
        if !force_empty {
            return Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "project_scaffold_output_exists_empty",
                        "message": "scaffold output directory already exists and is empty",
                        "path": absolute.display().to_string(),
                        "resolutions": {
                            "forceEmpty": true,
                            "differentOutputDirectory": true
                        }
                    }
                }),
            });
        }
    }

    Ok(absolute)
}

fn render_scaffold(model: &mut ScaffoldModel) -> Result<(), AppError> {
    let output_parent = model.output_path.parent().unwrap_or_else(|| Path::new("."));
    std::fs::create_dir_all(output_parent)
        .map_err(|source| scaffold_io_error(output_parent, source))?;
    let temp_dir = model.output_path.with_file_name(format!(
        ".{}.tmp-{}",
        model
            .output_path
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("scaffold"),
        std::process::id()
    ));
    if temp_dir.exists() {
        std::fs::remove_dir_all(&temp_dir)
            .map_err(|source| scaffold_io_error(&temp_dir, source))?;
    }
    std::fs::create_dir_all(&temp_dir).map_err(|source| scaffold_io_error(&temp_dir, source))?;

    let result = render_scaffold_to_temp(model, &temp_dir).and_then(|_| {
        if model.output_path.exists() {
            std::fs::remove_dir(&model.output_path)
                .map_err(|source| scaffold_io_error(&model.output_path, source))?;
        }
        std::fs::rename(&temp_dir, &model.output_path)
            .map_err(|source| scaffold_io_error(&model.output_path, source))
    });

    if result.is_err() {
        let _ = std::fs::remove_dir_all(&temp_dir);
    }

    result
}

fn render_scaffold_to_temp(model: &mut ScaffoldModel, temp_dir: &Path) -> Result<(), AppError> {
    let mut created = Vec::new();
    let mut skipped = Vec::new();
    copy_template_dir(
        &template_root(),
        &template_root(),
        temp_dir,
        model,
        &mut created,
        &mut skipped,
    )?;

    write_generated_file(
        temp_dir,
        Path::new("README.md"),
        &render_readme(model),
        &mut created,
    )?;
    write_generated_file(
        temp_dir,
        Path::new("sts2.profiles.yaml"),
        &render_profiles(model),
        &mut created,
    )?;
    write_generated_file(
        temp_dir,
        Path::new("sts2.hot-reload.yaml"),
        &render_hot_reload_project(model),
        &mut created,
    )?;
    write_generated_file(
        temp_dir,
        Path::new("sts2.local.example.yaml"),
        &render_local_example(model),
        &mut created,
    )?;

    created.sort();
    created.dedup();
    skipped.sort();
    skipped.dedup();
    model.created_files = created;
    model.skipped_files = skipped;
    Ok(())
}

fn copy_template_dir(
    root: &Path,
    current: &Path,
    temp_dir: &Path,
    model: &ScaffoldModel,
    created: &mut Vec<String>,
    skipped: &mut Vec<String>,
) -> Result<(), AppError> {
    for entry in std::fs::read_dir(current).map_err(|source| scaffold_io_error(current, source))? {
        let entry = entry.map_err(|source| scaffold_io_error(current, source))?;
        let path = entry.path();
        let relative = path.strip_prefix(root).expect("template child");
        if has_skipped_component(relative) {
            if path.is_file() {
                skipped.push(relative_to_string(relative));
            }
            continue;
        }
        if path.is_dir() {
            copy_template_dir(root, &path, temp_dir, model, created, skipped)?;
            continue;
        }

        let rendered_relative = render_relative_path(relative, model);
        let destination = temp_dir.join(&rendered_relative);
        if let Some(parent) = destination.parent() {
            std::fs::create_dir_all(parent).map_err(|source| scaffold_io_error(parent, source))?;
        }
        if is_binary_path(&path) {
            std::fs::copy(&path, &destination)
                .map_err(|source| scaffold_io_error(&path, source))?;
        } else {
            let contents = std::fs::read_to_string(&path)
                .map_err(|source| scaffold_io_error(&path, source))?;
            std::fs::write(&destination, render_text(&contents, model))
                .map_err(|source| scaffold_io_error(&destination, source))?;
        }
        created.push(relative_to_string(&rendered_relative));
    }
    Ok(())
}

fn template_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root")
        .join("templates/stable-harmony-trampoline")
}

fn render_relative_path(relative: &Path, model: &ScaffoldModel) -> PathBuf {
    let mut rendered = PathBuf::new();
    for component in relative.components() {
        let Component::Normal(text) = component else {
            continue;
        };
        let text = text.to_string_lossy();
        let text = if text == "HotMod.Template.sln" {
            format!("{}.sln", model.assembly_prefix)
        } else {
            text.replace("HotMod", &model.assembly_prefix)
        };
        rendered.push(text);
    }
    rendered
}

fn render_text(contents: &str, model: &ScaffoldModel) -> String {
    contents
        .replace(
            "<EnableHotReloadGuardrails Condition=\"'$(EnableHotReloadGuardrails)' == ''\">false</EnableHotReloadGuardrails>",
            if model.guardrails_enabled {
                "<EnableHotReloadGuardrails Condition=\"'$(EnableHotReloadGuardrails)' == ''\">true</EnableHotReloadGuardrails>"
            } else {
                "<EnableHotReloadGuardrails Condition=\"'$(EnableHotReloadGuardrails)' == ''\">false</EnableHotReloadGuardrails>"
            },
        )
        .replace("com.spirectl.hotmod.template", &model.harmony_owner_id)
        .replace("hotmod.template", &model.mod_id)
        .replace("HotMod.Template.sln", "__STS2_SOLUTION_FILE__")
        .replace("HotMod Template", &model.display_name)
        .replace("HOTMOD_", &format!("{}_", model.env_prefix))
        .replace("HOTMOD", &model.env_prefix)
        .replace(
            "\"HotMod.Shell.HarmonyPatches\"",
            "\"__STS2_NAMESPACE__.Shell.HarmonyPatches\"",
        )
        .replace(
            "\"HotMod.Logic.HotLogic\"",
            "\"__STS2_NAMESPACE__.Logic.HotLogic\"",
        )
        .replace("namespace HotMod", "namespace __STS2_NAMESPACE__")
        .replace("using HotMod", "using __STS2_NAMESPACE__")
        .replace("HotMod", &model.assembly_prefix)
        .replace("__STS2_NAMESPACE__", &model.namespace)
        .replace(
            "__STS2_SOLUTION_FILE__",
            &format!("{}.sln", model.assembly_prefix),
        )
}

fn render_readme(model: &ScaffoldModel) -> String {
    let author = model
        .author
        .as_deref()
        .map(|author| format!("\nAuthor: {author}\n"))
        .unwrap_or_default();
    format!(
        "# {display_name}\n{author}\nPackage id: `{package_id}`\n\nProjects: `{shell_project}`, `{contracts_project}`, `{logic_project}`, `{tests_project}`.\n\n## First Local Setup\n\n```bash\ncp sts2.local.example.yaml sts2.local.yaml\nsts2 project profile run {deploy_restart}\n```\n\n## Reloadable Logic Build\n\n```bash\nsts2 project profile run {logic_build}\nsts2 --json dev mod-reload --project . --wait\n```\n\nShell, Harmony patch, contract, and default-context dependency changes require the shell deploy/restart profile. Pure logic changes usually only need the logic-build profile, then explicit M61 reload control. The M57 marker trigger remains available as a shell-owned fallback.\n",
        display_name = model.display_name,
        author = author,
        package_id = model.package_id,
        shell_project = model.shell_project,
        contracts_project = model.contracts_project,
        logic_project = model.logic_project,
        tests_project = model.tests_project,
        deploy_restart = model.profile_names[2],
        logic_build = model.profile_names[3],
    )
}

fn render_hot_reload_project(model: &ScaffoldModel) -> String {
    format!(
        "schemaVersion: spirectl.hot-reload-project/v0\nprojectId: {mod_id}\nshellModId: {mod_id}\nshellProject: {shell_project}\nlogicProject: {logic_project}\nlogicBuildProfile: {logic_build}\nlogicArtifactPath: ${{config:game.modsDir}}/{shell_project}/hot-reload/{logic_project}.dll\nexpectedContractVersion: 0\nprotocol:\n  id: spirectl.m57.hot-reload-shell\n  version: 0\n",
        mod_id = model.mod_id,
        shell_project = model.shell_project,
        logic_project = model.logic_project,
        logic_build = model.profile_names[3],
    )
}

fn render_profiles(model: &ScaffoldModel) -> String {
    format!(
        "profiles:\n  {shell_build}:\n    description: Build the stable shell and contract without deploying.\n    steps:\n      - kind: command\n        command: dotnet\n        args:\n          - build\n          - src/{shell_project}/{shell_project}.csproj\n  {shell_deploy}:\n    description: Build and deploy the stable shell without restarting the game.\n    steps:\n      - kind: deploy\n        path: src/{shell_project}\n        build: true\n  {shell_deploy_restart}:\n    description: Build, deploy, restart, and verify the stable shell.\n    steps:\n      - kind: deploy\n        path: src/{shell_project}\n        build: true\n        restart: true\n        verify: true\n  {logic_build}:\n    description: Build reloadable logic and copy it into the deployed shell hot-reload directory when game.modsDir is configured.\n    steps:\n      - kind: command\n        command: dotnet\n        args:\n          - build\n          - src/{logic_project}/{logic_project}.csproj\n          - -p:HotReloadDeployDir=${{config:game.modsDir}}/{shell_project}/hot-reload\n  {clean}:\n    description: Remove generated local/deployed reload artifacts through the logic project MSBuild target.\n    steps:\n      - kind: command\n        command: dotnet\n        args:\n          - msbuild\n          - src/{logic_project}/{logic_project}.csproj\n          - -target:CleanHotReloadArtifacts\n          - -p:HotReloadDeployDir=${{config:game.modsDir}}/{shell_project}/hot-reload\n",
        shell_build = model.profile_names[0],
        shell_deploy = model.profile_names[1],
        shell_deploy_restart = model.profile_names[2],
        logic_build = model.profile_names[3],
        clean = model.profile_names[4],
        shell_project = model.shell_project,
        logic_project = model.logic_project,
    )
}

fn render_local_example(model: &ScaffoldModel) -> String {
    format!(
        "# Copy this file to sts2.local.yaml and keep sts2.local.yaml uncommitted.\ntransport:\n  kind: ipc\ngame:\n  path: /absolute/path/to/SlayTheSpire2\n  assembliesDir: /absolute/path/to/SlayTheSpire2/data_sts2_linux_x86_64\n  modsDir: /absolute/path/to/SlayTheSpire2/mods\n  launchEnv:\n    {env_prefix}_HOT_RELOAD: \"1\"\n    {env_prefix}_DEV_OVERLAY: \"0\"\n  deployBuildCommand:\n    - dotnet\n    - publish\n    - -c\n    - Debug\n    - -o\n    - dist/{shell_project}\n  deployOutputSubdir: dist/{shell_project}\n",
        env_prefix = model.env_prefix,
        shell_project = model.shell_project,
    )
}

fn write_generated_file(
    temp_dir: &Path,
    relative: &Path,
    contents: &str,
    created: &mut Vec<String>,
) -> Result<(), AppError> {
    let path = temp_dir.join(relative);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|source| scaffold_io_error(parent, source))?;
    }
    std::fs::write(&path, contents).map_err(|source| scaffold_io_error(&path, source))?;
    created.push(relative_to_string(relative));
    Ok(())
}

fn has_skipped_component(path: &Path) -> bool {
    path.components().any(|component| match component {
        Component::Normal(name) => name == OsStr::new("bin") || name == OsStr::new("obj"),
        _ => false,
    })
}

fn is_binary_path(path: &Path) -> bool {
    matches!(
        path.extension().and_then(OsStr::to_str),
        Some("dll" | "pdb" | "png" | "res" | "scn" | "pck")
    )
}

fn relative_to_string(path: &Path) -> String {
    path.components()
        .filter_map(|component| match component {
            Component::Normal(value) => Some(value.to_string_lossy()),
            _ => None,
        })
        .collect::<Vec<_>>()
        .join("/")
}

fn uppercase_snake(value: &str) -> String {
    value
        .bytes()
        .map(|byte| match byte {
            b'a'..=b'z' => (byte as char).to_ascii_uppercase(),
            b'0'..=b'9' => byte as char,
            b'-' => '_',
            _ => '_',
        })
        .collect()
}

fn scaffold_path_error(code: &'static str, message: &'static str, path: &Path) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": code,
                "message": message,
                "path": path.display().to_string(),
            }
        }),
    }
}

fn scaffold_io_error(path: &Path, source: std::io::Error) -> AppError {
    AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "project_scaffold_io_failed",
                "message": "failed to scaffold project",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    }
}

#[derive(Debug, Clone)]
struct ScaffoldValidationError {
    code: &'static str,
    field: &'static str,
    value: String,
    message: &'static str,
}

impl ScaffoldValidationError {
    fn new(code: &'static str, field: &'static str, value: &str, message: &'static str) -> Self {
        Self {
            code,
            field,
            value: value.to_string(),
            message,
        }
    }
}

impl From<ScaffoldValidationError> for AppError {
    fn from(error: ScaffoldValidationError) -> Self {
        AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": error.code,
                    "message": error.message,
                    "field": error.field,
                    "value": error.value,
                }
            }),
        }
    }
}

fn validate_mod_id(value: &str) -> Result<(), ScaffoldValidationError> {
    let valid = !value.is_empty()
        && value.len() <= 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
        && value
            .bytes()
            .next()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && value
            .bytes()
            .last()
            .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
        && !value.contains("--");
    if valid {
        Ok(())
    } else {
        Err(ScaffoldValidationError::new(
            "project_scaffold_invalid_mod_id",
            "modId",
            value,
            "mod id must use lowercase kebab-case letters, digits, and single hyphens",
        ))
    }
}

fn validate_namespace(value: &str) -> Result<(), ScaffoldValidationError> {
    let valid = !value.is_empty() && value.split('.').all(is_csharp_identifier);
    if valid {
        Ok(())
    } else {
        Err(ScaffoldValidationError::new(
            "project_scaffold_invalid_namespace",
            "namespace",
            value,
            "namespace must be dot-separated C# identifiers",
        ))
    }
}

fn validate_assembly_prefix(value: &str) -> Result<(), ScaffoldValidationError> {
    if is_csharp_identifier(value) && !value.contains('.') {
        Ok(())
    } else {
        Err(ScaffoldValidationError::new(
            "project_scaffold_invalid_assembly_prefix",
            "assemblyPrefix",
            value,
            "assembly prefix must be a single C# identifier",
        ))
    }
}

fn is_csharp_identifier(segment: &str) -> bool {
    let mut chars = segment.chars();
    match chars.next() {
        Some(first) if first == '_' || first.is_ascii_alphabetic() => {}
        _ => return false,
    }
    chars.all(|ch| ch == '_' || ch.is_ascii_alphanumeric())
}
