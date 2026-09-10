use clap::{Args, Subcommand};
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

use crate::{
    AppConfig, AppContext, AppError, ConfigValueSource, dotnet_helper, resolve_dotnet_tools_path,
};

#[derive(Debug, Clone, Subcommand)]
pub enum ToolchainSubcommand {
    Info,
}

#[derive(Debug, Clone, Args)]
pub struct ToolchainCommand {
    #[command(subcommand)]
    pub command: ToolchainSubcommand,
}

pub(crate) fn execute_toolchain_info_json(context: AppContext<'_>) -> Result<Value, AppError> {
    let managed = resolve_managed_roots(context.config, &context.config_origin.base_dir);
    let dotnet_tools_path = resolve_dotnet_tools_path(context.config);
    // Read-only report: skip the recursive staleness walk of `dotnet-tools/`. The payload says so
    // via `staleChecked`, and any command that actually launches the helper still resolves eagerly.
    let dotnet_helper_launch =
        dotnet_helper::resolve_dotnet_helper_launch_lazy(context.config, context.config_provenance);

    Ok(json!({
        "roots": {
            "toolchain": root_json(&managed.toolchain_dir, context.config_provenance.toolchain_dir),
            "sharedCache": root_json(&managed.shared_cache_dir, context.config_provenance.shared_cache_dir),
            "profilesFile": root_json(&managed.profiles_file, context.config_provenance.profiles_file),
        },
        "tools": [
            {
                "id": "embedded-ilspy",
                "kind": "decompiler",
                "status": "available",
                "version": "embedded",
                "notes": [
                    "Full exact-match decompile support uses the helper's embedded ILSpy backend."
                ],
            },
            {
                "id": "dotnet-helper",
                "kind": "helper-project",
                "status": if dotnet_tools_path.exists() { "available" } else { "missing" },
                "path": dotnet_tools_path.display().to_string(),
                "provenance": context.config_provenance.dotnet_tools_path.name(),
                "launch": dotnet_helper_launch_json(&dotnet_helper_launch),
            },
            gdre_tool_json(context.config, context.config_provenance.gdre_path),
        ],
    }))
}

fn dotnet_helper_launch_json(plan: &dotnet_helper::DotnetHelperLaunchPlan) -> Value {
    json!({
        "configuredPath": plan.configured_path.display().to_string(),
        "resolvedPath": plan.resolved_path.display().to_string(),
        "invocationPath": plan.invocation_path.as_deref().map(display_path),
        "buildOutputPath": plan.build_output_path.as_deref().map(display_path),
        "launchKind": plan.launch_kind,
        "available": plan.available,
        "buildNeeded": plan.build_needed,
        "buildReason": plan.build_reason,
        "staleChecked": plan.stale_checked,
        "failureReason": plan.failure_reason,
        "notes": dotnet_helper_launch_notes(plan),
    })
}

fn display_path(path: &Path) -> String {
    path.display().to_string()
}

fn dotnet_helper_launch_notes(plan: &dotnet_helper::DotnetHelperLaunchPlan) -> Vec<String> {
    use dotnet_helper::{DotnetHelperBuildReason, DotnetHelperLaunchKind};

    match plan.launch_kind {
        DotnetHelperLaunchKind::DefaultProject => match plan.build_reason {
            Some(DotnetHelperBuildReason::DefaultOutputMissing) => vec![format!(
                "Default helper build output '{}' is missing; run a code command or build the helper to create it.",
                plan.build_output_path
                    .as_deref()
                    .unwrap_or(&plan.resolved_path)
                    .display()
            )],
            Some(DotnetHelperBuildReason::DefaultOutputStale) => vec![format!(
                "Default helper build output '{}' is older than helper inputs; run a code command or rebuild the helper.",
                plan.build_output_path
                    .as_deref()
                    .unwrap_or(&plan.resolved_path)
                    .display()
            )],
            None => {
                let mut notes = vec![format!(
                    "Default helper project will launch the built DLL '{}'.",
                    plan.invocation_path
                        .as_deref()
                        .unwrap_or(&plan.resolved_path)
                        .display()
                )];
                if !plan.stale_checked {
                    notes.push(
                        "Helper staleness was not checked: toolchain info never builds, so it skips the recursive scan of the helper project. A code or asset command re-checks and rebuilds if needed."
                            .to_string(),
                    );
                }
                notes
            }
        },
        DotnetHelperLaunchKind::Project => vec![format!(
            "Configured helper project '{}' will launch through dotnet run.",
            plan.resolved_path.display()
        )],
        DotnetHelperLaunchKind::Dll => vec![format!(
            "Configured helper DLL '{}' will launch directly through dotnet.",
            plan.resolved_path.display()
        )],
        DotnetHelperLaunchKind::Executable => vec![format!(
            "Configured helper executable '{}' will launch directly.",
            plan.resolved_path.display()
        )],
        DotnetHelperLaunchKind::Missing => vec![format!(
            "Configured helper path '{}' was not found; set tools.dotnetToolsPath to a .csproj, .dll, or executable helper.",
            plan.resolved_path.display()
        )],
        DotnetHelperLaunchKind::Unsupported => vec![format!(
            "Configured helper path '{}' is not a supported helper .csproj, .dll, or executable.",
            plan.resolved_path.display()
        )],
    }
}

#[derive(Debug, Clone)]
pub(crate) struct ManagedRoots {
    pub(crate) toolchain_dir: PathBuf,
    pub(crate) shared_cache_dir: PathBuf,
    pub(crate) profiles_file: PathBuf,
}

pub(crate) fn resolve_managed_roots(config: &AppConfig, base_dir: &Path) -> ManagedRoots {
    let toolchain_dir = resolve_config_path(
        base_dir,
        config.toolchain.dir.as_deref(),
        Path::new(".sts2/toolchain"),
    )
    .0;
    let profiles_file = resolve_config_path(
        base_dir,
        config.project.profiles_file.as_deref(),
        Path::new("sts2.profiles.yaml"),
    )
    .0;
    let shared_cache_dir = config
        .toolchain
        .shared_cache_dir
        .as_deref()
        .map(|path| resolve_optional_path(base_dir, Path::new(path)))
        .unwrap_or_else(default_shared_cache_dir);

    ManagedRoots {
        toolchain_dir,
        shared_cache_dir,
        profiles_file,
    }
}

fn resolve_optional_path(base_dir: &Path, path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        base_dir.join(path)
    }
}

fn resolve_config_path(
    base_dir: &Path,
    configured: Option<&str>,
    default_relative: &Path,
) -> (PathBuf, ConfigValueSource) {
    match configured {
        Some(path) => {
            let path = PathBuf::from(path);
            if path.is_absolute() {
                (path, ConfigValueSource::CheckedInConfig)
            } else {
                (base_dir.join(path), ConfigValueSource::CheckedInConfig)
            }
        }
        None => (base_dir.join(default_relative), ConfigValueSource::Default),
    }
}

fn root_json(path: &Path, provenance: ConfigValueSource) -> Value {
    json!({
        "path": path.display().to_string(),
        "provenance": provenance.name(),
    })
}

pub(crate) fn gdre_tool_json(config: &AppConfig, provenance: ConfigValueSource) -> Value {
    match config.tools.gdre_path.as_deref() {
        Some(path) => {
            let executable = PathBuf::from(path);
            if executable.exists() {
                json!({
                    "id": "gdre",
                    "kind": "external-tool",
                    "status": "available",
                    "path": executable.display().to_string(),
                    "provenance": provenance.name(),
                })
            } else {
                json!({
                    "id": "gdre",
                    "kind": "external-tool",
                    "status": "missing",
                    "failureCode": "gdre_path_missing",
                    "path": executable.display().to_string(),
                    "provenance": provenance.name(),
                    "notes": [
                        format!(
                            "Configured GDRE executable '{}' was not found.",
                            executable.display()
                        )
                    ],
                })
            }
        }
        None => json!({
            "id": "gdre",
            "kind": "external-tool",
            "status": "not-configured",
            "failureCode": "gdre_path_unset",
            "notes": [
                "Set tools.gdrePath to enable recovered-project export workflows."
            ],
            "provenance": provenance.name(),
        }),
    }
}

#[cfg(windows)]
fn default_shared_cache_dir() -> PathBuf {
    env_path("LOCALAPPDATA")
        .or_else(|| env_path("APPDATA"))
        .unwrap_or_else(std::env::temp_dir)
        .join("spirectl")
}

#[cfg(not(windows))]
fn default_shared_cache_dir() -> PathBuf {
    env_path("XDG_CACHE_HOME")
        .or_else(|| env_path("HOME").map(|path| path.join(".cache")))
        .unwrap_or_else(std::env::temp_dir)
        .join("spirectl")
}

fn env_path(name: &str) -> Option<PathBuf> {
    std::env::var_os(name)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
}
