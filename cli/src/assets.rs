use crate::{
    AppConfig, AppContext, AppError, LifecycleWaitArgs, PathOverrides, RenderedCommand,
    ResolvedSceneSearchPaths, TransportKind, bridge, lifecycle, maybe_cache_resolved_local_paths,
    resolve_dotnet_tools_path, resolve_scene_search_paths, visual_validation,
};
use base64::Engine;
use clap::{Args, Subcommand, ValueEnum};
use image::{ImageFormat, open as open_image};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::BTreeMap;
use std::ffi::OsStr;
use std::fmt::{Display, Formatter};
use std::fs;
use std::fs::File;
use std::fs::OpenOptions;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Instant;

#[path = "assets_render.rs"]
mod render;

use render::{
    render_batch_response, render_catalog_response, render_explain_response,
    render_extract_response, render_resolve_response,
};

include!("assets/assets_types.rs");
include!("assets/assets_extract_batch.rs");
include!("assets/assets_discovery_export.rs");
include!("assets/assets_live_outputs.rs");
include!("assets/assets_catalog_resolve.rs");
include!("assets/assets_helper_manifest_errors.rs");
#[cfg(test)]
#[path = "assets_tests.rs"]
mod tests;

pub(crate) fn execute_asset_extract(
    args: AssetExtractArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let search_paths = resolve_asset_search_paths(context, &args.search_roots)?;
    let output_dir = resolve_absolute_path(Path::new(&context.config.artifacts.dir)).join("assets");
    let mut launch_attempted = false;
    let response = run_asset_extract_request(
        AssetExtractRequestSpec {
            command: "extract",
            query: args.query,
            execution: args.execution,
            format: args.format,
            output_dir,
        },
        &search_paths,
        &mut launch_attempted,
        context,
    )?;

    Ok(render_extract_response(response, context.json_output, 0))
}

pub(crate) fn execute_asset_catalog(
    args: AssetCatalogArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let family = args.family.trim().to_ascii_lowercase();
    let entries = asset_catalog_entries()
        .into_iter()
        .filter(|entry| {
            family == "all"
                || entry.resolution_kind == family
                || entry
                    .model_type
                    .is_some_and(|model_type| model_type == family)
        })
        .collect::<Vec<_>>();
    let response = AssetCatalogResponse {
        command: "catalog",
        status: "ok",
        execution_mode: args.execution.as_str(),
        source: None,
        provisional: false,
        family: args.family,
        entries,
        notes: Vec::new(),
    };

    Ok(render_catalog_response(response, context.json_output, 0))
}

pub(crate) fn execute_asset_resolve(
    args: AssetResolveArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let response = resolve_asset_key_response(&args.query);
    let exit_code = if response.status == "ok" { 0 } else { 2 };
    Ok(render_resolve_response(
        response,
        context.json_output,
        exit_code,
    ))
}

pub(crate) fn execute_asset_explain(
    args: AssetExplainArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let Some(candidate) = try_parse_explain_asset_query(&args.query) else {
        // Encounter render targets (background / visual-state / visual-part) are extractable via
        // `assets extract` but cannot be explained as metadata — only the scene-package form is.
        // Guide the caller to the supported query instead of a generic "unsupported" result.
        if try_parse_composed_encounter_asset_query(&args.query).is_some_and(|candidate| {
            !matches!(candidate.asset_kind, AssetKind::EncounterScenePackage)
        }) {
            return Err(asset_usage_error(
                "command 'assets explain' supports composed://combat-background/<id>/image and composed://encounters/<id>/scene-package",
            ));
        }
        let response = AssetExplainResponse {
            command: "explain",
            status: "unsupported",
            query: args.query,
            execution_mode: args.execution.as_str(),
            source_root: String::new(),
            source_path: String::new(),
            asset_kind: String::new(),
            storage_kind: String::new(),
            container_path: None,
            resource_type: None,
            explanation_kind: "unsupported".to_string(),
            explanation: json!({
                "message": "Unsupported asset explain query.",
            }),
        };
        return Ok(render_explain_response(response, context.json_output, 2));
    };

    let mut launch_attempted = false;
    let bridge_response = explain_live_asset(&candidate, &mut launch_attempted, context)?;
    let (explanation_kind, explanation) = asset_explain_payload_from_proto(bridge_response);
    let source_path = candidate.source_path_text();
    let response = AssetExplainResponse {
        command: "explain",
        status: "ok",
        query: args.query,
        execution_mode: args.execution.as_str(),
        source_root: candidate.source_root,
        source_path,
        asset_kind: candidate.asset_kind.as_str().to_string(),
        storage_kind: candidate.storage_kind,
        container_path: candidate
            .container_path
            .as_ref()
            .map(|path| path.display().to_string()),
        resource_type: candidate.resource_type,
        explanation_kind,
        explanation,
    };

    Ok(render_explain_response(response, context.json_output, 0))
}
