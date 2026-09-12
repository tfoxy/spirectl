use std::ffi::OsString;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::{
    AppConfig, AssetCommand, AssetSubcommand, CodeCommand, CodeSearchRootArgs, CodeSubcommand,
    ConfigCommand, ConfigSubcommand, DEFAULT_DOTNET_TOOLS_PATH, PathOverrides, ProjectCommand,
    ResolvedCodeSearchPaths, ResolvedSceneSearchPaths, ServiceCommand, ServiceSubcommand,
    SkillCommand, SkillSubcommand, ToolchainCommand, assets, dotnet_helper, execute_asset_catalog,
    execute_asset_explain, execute_asset_extract, execute_asset_resolve,
    execute_skill_install_json, maybe_cache_resolved_local_paths, render_value_success,
    resolve_code_search_paths, resolve_scene_search_paths, stdout_write_error,
};
use crate::{
    AppContext, AppError, RenderedCommand, automation_service, config_resolve, project, toolchain,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

pub(crate) fn handle_toolchain(
    command: ToolchainCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        toolchain::ToolchainSubcommand::Info => render_value_success(
            toolchain::execute_toolchain_info_json(context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn handle_config(
    command: ConfigCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        ConfigSubcommand::Resolve(args) => render_value_success(
            config_resolve::execute_config_resolve_json(args, context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn handle_project(
    command: ProjectCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    render_value_success(
        project::execute_project_command_json(command, context)?,
        context.json_output,
    )
}

pub(crate) fn resolve_code_search_paths_for_command(
    context: AppContext<'_>,
    search_roots: &CodeSearchRootArgs,
) -> Result<ResolvedCodeSearchPaths, AppError> {
    let search_paths = resolve_code_search_paths(
        context.config,
        path_overrides(search_roots),
        search_roots.include_mods,
    )
    .map_err(invalid_code_search_roots)?;
    let _ = maybe_cache_resolved_local_paths(
        context,
        search_paths.game_path.as_deref(),
        search_paths.used_discovered_game_path(),
        Some(&search_paths.assemblies_dir),
        search_paths.derived_assemblies_dir_from_game_path(),
    );
    Ok(search_paths)
}

pub(crate) fn resolve_scene_search_paths_for_command(
    context: AppContext<'_>,
    search_roots: &CodeSearchRootArgs,
) -> Result<ResolvedSceneSearchPaths, AppError> {
    let search_paths = resolve_scene_search_paths(
        context.config,
        path_overrides(search_roots),
        search_roots.include_mods,
    )
    .map_err(invalid_code_search_roots)?;
    let _ = maybe_cache_resolved_local_paths(
        context,
        search_paths.game_path.as_deref(),
        search_paths.used_discovered_game_path(),
        search_paths.assemblies_dir.as_deref(),
        search_paths.derived_assemblies_dir_from_game_path(),
    );
    Ok(search_paths)
}

pub(crate) fn resolve_optional_scene_resources_dir(
    context: AppContext<'_>,
    search_roots: &CodeSearchRootArgs,
    resolved_game_path: Option<&Path>,
) -> Result<Option<PathBuf>, AppError> {
    let candidate = search_roots
        .resources_dir
        .clone()
        .or_else(|| {
            context
                .config
                .game
                .resources_dir
                .as_ref()
                .map(PathBuf::from)
        })
        .or_else(|| resolved_game_path.map(Path::to_path_buf));

    let Some(candidate) = candidate else {
        return Ok(None);
    };

    if !candidate.is_dir() {
        return Err(invalid_code_search_roots(format!(
            "Resolved resources directory '{}' does not exist or is not a directory.",
            candidate.display()
        )));
    }

    Ok(Some(candidate))
}

pub(crate) fn handle_code(
    command: CodeCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let mut helper_args = Vec::<OsString>::new();

    match command.command {
        CodeSubcommand::Locate(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("locate".into());
            helper_args.push(args.subject.to_string().into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
            helper_args.push("--limit".into());
            helper_args.push(args.limit.to_string().into());
        }
        CodeSubcommand::Refs(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("refs".into());
            helper_args.push(args.subject.to_string().into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
            helper_args.push("--limit".into());
            helper_args.push(args.limit.to_string().into());
        }
        CodeSubcommand::Describe(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("describe".into());
            helper_args.push(args.subject.to_string().into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
        }
        CodeSubcommand::Derived(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("derived".into());
            helper_args.push(args.subject.to_string().into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
            helper_args.push("--limit".into());
            helper_args.push(args.limit.to_string().into());
        }
        CodeSubcommand::Hooks(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;
            let resources_dir = resolve_optional_scene_resources_dir(
                context,
                &args.search_roots,
                search_paths.game_path.as_deref(),
            )?;

            helper_args.push("hooks".into());
            if let Some(query) = args.query {
                helper_args.push(query.into());
            }
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            if let Some(resources_dir) = resources_dir {
                helper_args.push("--resources-dir".into());
                helper_args.push(resources_dir.into_os_string());
            }

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
            helper_args.push("--limit".into());
            helper_args.push(args.limit.to_string().into());
            helper_args.push("--offset".into());
            helper_args.push(args.offset.to_string().into());
            if let Some(source) = args.source {
                helper_args.push("--source".into());
                helper_args.push(source.to_string().into());
            }
            if let Some(assembly) = args.assembly {
                helper_args.push("--assembly".into());
                helper_args.push(assembly.into());
            }
            for form in args.forms {
                helper_args.push("--form".into());
                helper_args.push(form.to_string().into());
            }
            if args.has_script {
                helper_args.push("--has-script".into());
            }
            helper_args.push("--sort".into());
            helper_args.push(args.sort.to_string().into());
        }
        CodeSubcommand::HookInfo(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;
            let resources_dir = resolve_optional_scene_resources_dir(
                context,
                &args.search_roots,
                search_paths.game_path.as_deref(),
            )?;

            helper_args.push("hook-info".into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            if let Some(resources_dir) = resources_dir {
                helper_args.push("--resources-dir".into());
                helper_args.push(resources_dir.into_os_string());
            }

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
        }
        CodeSubcommand::VerifyReferences(args) => {
            let search_roots = args.search_roots();
            let search_paths = resolve_code_search_paths_for_command(context, &search_roots)?;

            helper_args.push("verify-references".into());
            helper_args.push(args.assemblies.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            if let Some(control_assemblies_dir) = args.control_assemblies_dir {
                helper_args.push("--control-assemblies-dir".into());
                helper_args.push(control_assemblies_dir.into_os_string());
            }
        }
        CodeSubcommand::Decompile(args) => {
            let search_paths = resolve_code_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("decompile".into());
            helper_args.push(args.subject.to_string().into());
            helper_args.push(args.query.into());
            helper_args.push("--assemblies-dir".into());
            helper_args.push(search_paths.assemblies_dir.into_os_string());

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            append_include_dependencies_arg(
                &mut helper_args,
                args.search_roots.include_dependencies,
            );
            if args.full {
                helper_args.push("--full".into());
            }
        }
        CodeSubcommand::SceneSearch(args) => {
            let search_paths = resolve_scene_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("scene-search".into());
            helper_args.push(args.query.into());
            helper_args.push("--resources-dir".into());
            helper_args.push(search_paths.resources_dir.into_os_string());

            if let Some(assemblies_dir) = search_paths.assemblies_dir {
                helper_args.push("--assemblies-dir".into());
                helper_args.push(assemblies_dir.into_os_string());
            }

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
            helper_args.push("--limit".into());
            helper_args.push(args.limit.to_string().into());
        }
        CodeSubcommand::SceneTree(args) => {
            let search_paths = resolve_scene_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("scene-tree".into());
            helper_args.push(args.scene.into());
            helper_args.push("--resources-dir".into());
            helper_args.push(search_paths.resources_dir.into_os_string());

            if let Some(assemblies_dir) = search_paths.assemblies_dir {
                helper_args.push("--assemblies-dir".into());
                helper_args.push(assemblies_dir.into_os_string());
            }

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
        }
        CodeSubcommand::SceneNode(args) => {
            let search_paths = resolve_scene_search_paths_for_command(context, &args.search_roots)?;

            helper_args.push("scene-node".into());
            helper_args.push(args.scene.into());
            helper_args.push(args.node_path.into());
            helper_args.push("--resources-dir".into());
            helper_args.push(search_paths.resources_dir.into_os_string());

            if let Some(assemblies_dir) = search_paths.assemblies_dir {
                helper_args.push("--assemblies-dir".into());
                helper_args.push(assemblies_dir.into_os_string());
            }

            append_mod_search_args(
                &mut helper_args,
                args.search_roots.include_mods,
                search_paths.mods_dir,
            );
        }
    }

    append_helper_cache_args(&mut helper_args, context);

    if context.json_output {
        helper_args.push("--json".into());
    }

    let plan =
        dotnet_helper::resolve_dotnet_helper_launch(context.config, context.config_provenance);
    dotnet_helper::run_dotnet_helper(&plan, &helper_args, context.json_output)
}

pub(crate) fn append_helper_cache_args(args: &mut Vec<OsString>, context: AppContext<'_>) {
    let managed = toolchain::resolve_managed_roots(context.config, &context.config_origin.base_dir);
    args.push("--cache-dir".into());
    args.push(managed.shared_cache_dir.into_os_string());
}

pub(crate) fn handle_assets(
    command: AssetCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        AssetSubcommand::Extract(args) => execute_asset_extract(args, context),
        AssetSubcommand::Catalog(args) => execute_asset_catalog(args, context),
        AssetSubcommand::Resolve(args) => execute_asset_resolve(args, context),
        AssetSubcommand::Explain(args) => execute_asset_explain(args, context),
        AssetSubcommand::ExtractBatch(args) => assets::execute_asset_extract_batch(args, context),
    }
}

pub(crate) fn handle_skill(
    command: SkillCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        SkillSubcommand::Install(args) => render_value_success(
            execute_skill_install_json(args, context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn handle_service(
    command: ServiceCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        ServiceSubcommand::Serve(args) => {
            automation_service::validate_auth_policy_with_config(&args, &context.config.service)?;
            Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "service_requires_streaming",
                        "message": "service serve must run through the streaming CLI path."
                    }
                }),
            })
        }
    }
}

pub(crate) fn path_overrides(search_roots: &CodeSearchRootArgs) -> PathOverrides<'_> {
    PathOverrides {
        game_path: search_roots.game_path.as_deref(),
        assemblies_dir: search_roots.assemblies_dir.as_deref(),
        resources_dir: search_roots.resources_dir.as_deref(),
        mods_dir: search_roots.mods_dir.as_deref(),
    }
}

pub(crate) fn write_ndjson_event<W: Write>(writer: &mut W, event: &Value) -> Result<(), AppError> {
    serde_json::to_writer(&mut *writer, event).map_err(|source| AppError {
        exit_code: 1,
        payload: json!({
            "error": {
                "code": "stdout_write_failed",
                "message": source.to_string()
            }
        }),
    })?;
    writer.write_all(b"\n").map_err(stdout_write_error)?;
    writer.flush().map_err(stdout_write_error)
}

pub(crate) fn semantic_fingerprint(value: &Value) -> String {
    let bytes = serde_json::to_vec(value).expect("state projection should serialize");
    let digest = Sha256::digest(bytes);
    format!("{digest:x}")
}

pub(crate) fn observed_at_utc() -> String {
    let elapsed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let seconds = elapsed.as_secs();
    let millis = elapsed.subsec_millis();
    let days = seconds / 86_400;
    let seconds_of_day = seconds % 86_400;
    let (year, month, day) = civil_from_unix_days(days as i64);
    let hour = seconds_of_day / 3_600;
    let minute = (seconds_of_day % 3_600) / 60;
    let second = seconds_of_day % 60;
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

pub(crate) fn civil_from_unix_days(days: i64) -> (i32, u32, u32) {
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp + if mp < 10 { 3 } else { -9 };
    let year = y + if month <= 2 { 1 } else { 0 };
    (year as i32, month as u32, day as u32)
}

pub(crate) fn invalid_code_search_roots(message: String) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_code_search_roots",
                "message": message
            }
        }),
    }
}

pub(crate) fn append_mod_search_args(
    args: &mut Vec<OsString>,
    include_mods: bool,
    mods_dir: Option<PathBuf>,
) {
    if include_mods {
        args.push("--include-mods".into());
    }

    if let Some(mods_dir) = mods_dir {
        args.push("--mods-dir".into());
        args.push(mods_dir.into_os_string());
    }
}

pub(crate) fn append_include_dependencies_arg(
    args: &mut Vec<OsString>,
    include_dependencies: bool,
) {
    if include_dependencies {
        args.push("--include-dependencies".into());
    }
}

pub(crate) fn resolve_dotnet_tools_path(config: &AppConfig) -> PathBuf {
    let configured = PathBuf::from(&config.tools.dotnet_tools_path);
    if configured.is_absolute() {
        configured
    } else {
        let current_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
        for ancestor in current_dir.ancestors() {
            let candidate = ancestor.join(&configured);
            if candidate.exists() {
                return candidate;
            }
        }

        if config.tools.dotnet_tools_path == DEFAULT_DOTNET_TOOLS_PATH {
            let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
            if let Some(repo_root) = manifest_dir.parent() {
                let candidate = repo_root.join(&configured);
                if candidate.exists() {
                    return candidate;
                }
            }
        }

        current_dir.join(configured)
    }
}
