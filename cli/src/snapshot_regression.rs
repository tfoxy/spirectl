use crate::path_display::display_path;
use crate::query::{Predicate, evaluate};
use crate::visual_validation::{
    ComparisonRequest, compare_screenshots, resolve_viewport_selection,
};
use crate::{
    AppContext, AppError, DEFAULT_BRIDGE_RPC_TIMEOUT_MS, DiagnosticsArgs, LogHealthArgs,
    ScreenshotArgs, SnapshotCompareArgs, SnapshotExportArgs, StateArgs, StateViewArg,
    effective_viewport_presets, execute_diagnostics_json, execute_log_health_json,
    execute_screenshot_json, execute_state_json,
};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct SnapshotSpec {
    schema_version: Option<String>,
    name: Option<String>,
    description: Option<String>,
    compare_versions: bool,
    state: Option<StateSnapshotSpec>,
    actions: Option<ActionSnapshotSpec>,
    log_health: Option<LogHealthSnapshotSpec>,
    diagnostics: Option<DiagnosticsSnapshotSpec>,
    screenshot: Option<ScreenshotSnapshotSpec>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct StateSnapshotSpec {
    selectors: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct ActionSnapshotSpec {
    include_available_action_ids: bool,
}

impl Default for ActionSnapshotSpec {
    fn default() -> Self {
        Self {
            include_available_action_ids: true,
        }
    }
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct LogHealthSnapshotSpec {
    limit: Option<u32>,
    tail: Option<u32>,
    after_cursor: Option<u64>,
    level: Option<String>,
    target: Option<String>,
    exclude_targets: Vec<String>,
    exclude_message_regexes: Vec<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct DiagnosticsSnapshotSpec {
    limit: Option<u32>,
    tail: Option<u32>,
    after_cursor: Option<u64>,
    level: Option<String>,
    target: Option<String>,
    exclude_targets: Vec<String>,
    exclude_message_regexes: Vec<String>,
    preset: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
struct ScreenshotSnapshotSpec {
    preset: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
    max_diff_pixels: Option<u64>,
    max_diff_ratio: Option<f64>,
}

pub(crate) fn execute_snapshot_export_json(
    args: SnapshotExportArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let spec_path = resolve_absolute_path(&args.spec);
    let output_dir = resolve_absolute_path(&args.output);
    let spec = read_snapshot_spec(&spec_path)?;
    validate_spec(&spec, &spec_path)?;

    fs::create_dir_all(&output_dir)
        .map_err(|source| AppError::output_write(&output_dir, &source))?;

    let captures = export_snapshot_captures(&spec, &output_dir, &args.preset_catalogs, context)?;
    let payload = json!({
        "status": "exported",
        "specPath": display_path(&spec_path, &context.config_origin.base_dir),
        "outputDir": display_path(&output_dir, &context.config_origin.base_dir),
        "name": spec.name,
        "description": spec.description,
        "schemaVersion": spec.schema_version,
        "compareVersions": spec.compare_versions,
        "captures": captures,
    });
    write_json_file(&output_dir.join("snapshot.json"), &payload)?;
    Ok(payload)
}

pub(crate) fn execute_snapshot_compare_json(
    args: SnapshotCompareArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let spec_path = resolve_absolute_path(&args.spec);
    let baseline_dir = resolve_absolute_path(&args.baseline);
    let bundle_dir = args
        .bundle_dir
        .as_ref()
        .map(|path| resolve_absolute_path(path));
    let spec = read_snapshot_spec(&spec_path)?;
    validate_spec(&spec, &spec_path)?;

    if !baseline_dir.exists() {
        return Err(AppError::invalid_query(
            "baseline",
            &format!(
                "Baseline directory '{}' does not exist.",
                baseline_dir.display()
            ),
        ));
    }

    let mut sections = Map::new();
    let mut mismatches = Vec::new();

    if spec.state.is_some() {
        let state_result =
            compare_state_section(&spec, &baseline_dir, bundle_dir.as_deref(), context)?;
        if !state_result.matched {
            mismatches.push(json!({"section": "state", "details": state_result.details.clone()}));
        }
        sections.insert("state".to_string(), state_result.details);
    }

    if spec.actions.is_some() {
        let actions_result =
            compare_actions_section(&spec, &baseline_dir, bundle_dir.as_deref(), context)?;
        if !actions_result.matched {
            mismatches
                .push(json!({"section": "actions", "details": actions_result.details.clone()}));
        }
        sections.insert("actions".to_string(), actions_result.details);
    }

    if spec.log_health.is_some() {
        let log_health_result =
            compare_log_health_section(&spec, &baseline_dir, bundle_dir.as_deref(), context)?;
        if !log_health_result.matched {
            mismatches.push(
                json!({"section": "logHealth", "details": log_health_result.details.clone()}),
            );
        }
        sections.insert("logHealth".to_string(), log_health_result.details);
    }

    if spec.diagnostics.is_some() {
        let diagnostics_result = compare_diagnostics_section(
            &spec,
            &baseline_dir,
            &args.preset_catalogs,
            bundle_dir.as_deref(),
            context,
        )?;
        if !diagnostics_result.matched {
            mismatches.push(
                json!({"section": "diagnostics", "details": diagnostics_result.details.clone()}),
            );
        }
        sections.insert("diagnostics".to_string(), diagnostics_result.details);
    }

    if spec.screenshot.is_some() {
        let screenshot_result = compare_screenshot_section(
            &spec,
            &baseline_dir,
            &args.preset_catalogs,
            bundle_dir.as_deref(),
            context,
        )?;
        if !screenshot_result.matched {
            mismatches.push(
                json!({"section": "screenshot", "details": screenshot_result.details.clone()}),
            );
        }
        sections.insert("screenshot".to_string(), screenshot_result.details);
    }

    let mut payload = json!({
        "matched": mismatches.is_empty(),
        "specPath": display_path(&spec_path, &context.config_origin.base_dir),
        "baselineDir": display_path(&baseline_dir, &context.config_origin.base_dir),
        "sections": sections,
        "mismatches": mismatches,
    });

    if let Some(bundle_dir) = &bundle_dir {
        fs::create_dir_all(bundle_dir)
            .map_err(|source| AppError::output_write(bundle_dir, &source))?;
        let comparison_path = bundle_dir.join("snapshot-compare.json");
        write_json_file(&comparison_path, &payload)?;
        payload["bundle"] = json!({
            "dir": display_path(bundle_dir, &context.config_origin.base_dir),
            "files": {
                "comparison": display_path(&comparison_path, &context.config_origin.base_dir),
            }
        });
    }

    if payload["matched"].as_bool().unwrap_or(false) {
        Ok(payload)
    } else {
        Err(AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "snapshot_mismatch",
                    "message": "Snapshot regression comparison failed.",
                    "comparison": payload,
                }
            }),
        })
    }
}

fn export_snapshot_captures(
    spec: &SnapshotSpec,
    output_dir: &Path,
    preset_catalogs: &[PathBuf],
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let mut captures = Map::new();

    if spec.state.is_some() {
        let payload = capture_state(spec, context)?;
        let path = output_dir.join("state.json");
        write_json_file(&path, &payload)?;
        captures.insert(
            "state".to_string(),
            json!({
                "path": display_path(&path, &context.config_origin.base_dir),
                "selectors": payload["selectors"],
            }),
        );
    }

    if spec.actions.is_some() {
        let payload = capture_actions(context)?;
        let path = output_dir.join("inspect-actions.json");
        write_json_file(&path, &payload)?;
        captures.insert(
            "actions".to_string(),
            json!({
                "path": display_path(&path, &context.config_origin.base_dir),
                "availableActionIds": payload["availableActionIds"],
            }),
        );
    }

    if spec.log_health.is_some() {
        let payload = capture_log_health(spec, context)?;
        let path = output_dir.join("log-health.json");
        write_json_file(&path, &payload)?;
        captures.insert(
            "logHealth".to_string(),
            json!({
                "path": display_path(&path, &context.config_origin.base_dir),
                "status": payload["status"],
            }),
        );
    }

    if spec.diagnostics.is_some() {
        let payload = capture_diagnostics(spec, preset_catalogs, context)?;
        let path = output_dir.join("diagnostics.json");
        write_json_file(&path, &payload)?;
        captures.insert(
            "diagnostics".to_string(),
            json!({
                "path": display_path(&path, &context.config_origin.base_dir),
                "status": payload["status"],
            }),
        );
    }

    if let Some(screenshot_spec) = &spec.screenshot {
        let screenshot_path = output_dir.join("runtime.png");
        let payload = execute_screenshot_json(
            ScreenshotArgs {
                preset: screenshot_spec.preset.clone(),
                width: screenshot_spec.width,
                height: screenshot_spec.height,
                preset_catalogs: preset_catalogs.to_vec(),
                output: Some(screenshot_path.clone()),
                rpc_timeout_ms: DEFAULT_BRIDGE_RPC_TIMEOUT_MS,
            },
            context,
        )?;
        captures.insert(
            "screenshot".to_string(),
            json!({
                "path": display_path(&screenshot_path, &context.config_origin.base_dir),
                "preset": payload["preset"],
                "requestedViewport": payload["requestedViewport"],
                "appliedViewport": payload["appliedViewport"],
                "maxDiffPixels": screenshot_spec.max_diff_pixels.unwrap_or(0),
                "maxDiffRatio": screenshot_spec.max_diff_ratio.unwrap_or(0.0),
            }),
        );
    }

    Ok(Value::Object(captures))
}

fn compare_state_section(
    spec: &SnapshotSpec,
    baseline_dir: &Path,
    bundle_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<SectionComparison, AppError> {
    let baseline = read_json_file(&baseline_dir.join("state.json"))?;
    let current = capture_state(spec, context)?;
    maybe_write_capture(bundle_dir, "state.json", &current)?;
    Ok(compare_json_section(
        "selectors",
        baseline,
        current,
        spec.compare_versions,
    ))
}

fn compare_actions_section(
    spec: &SnapshotSpec,
    baseline_dir: &Path,
    bundle_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<SectionComparison, AppError> {
    let baseline = read_json_file(&baseline_dir.join("inspect-actions.json"))?;
    let current = capture_actions(context)?;
    maybe_write_capture(bundle_dir, "inspect-actions.json", &current)?;
    Ok(compare_json_section(
        "availableActionIds",
        baseline,
        current,
        spec.compare_versions,
    ))
}

fn compare_log_health_section(
    spec: &SnapshotSpec,
    baseline_dir: &Path,
    bundle_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<SectionComparison, AppError> {
    let baseline = read_json_file(&baseline_dir.join("log-health.json"))?;
    let current = capture_log_health(spec, context)?;
    maybe_write_capture(bundle_dir, "log-health.json", &current)?;
    Ok(compare_json_section(
        "status",
        baseline,
        current,
        spec.compare_versions,
    ))
}

fn compare_diagnostics_section(
    spec: &SnapshotSpec,
    baseline_dir: &Path,
    preset_catalogs: &[PathBuf],
    bundle_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<SectionComparison, AppError> {
    let baseline = read_json_file(&baseline_dir.join("diagnostics.json"))?;
    let current = capture_diagnostics(spec, preset_catalogs, context)?;
    maybe_write_capture(bundle_dir, "diagnostics.json", &current)?;
    Ok(compare_json_section(
        "status",
        baseline,
        current,
        spec.compare_versions,
    ))
}

fn compare_screenshot_section(
    spec: &SnapshotSpec,
    baseline_dir: &Path,
    preset_catalogs: &[PathBuf],
    bundle_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<SectionComparison, AppError> {
    let screenshot_spec = spec.screenshot.as_ref().expect("screenshot section");
    let baseline_path = baseline_dir.join("runtime.png");
    let baseline_bytes = fs::read(&baseline_path).map_err(|source| {
        AppError::invalid_query(
            "baseline",
            &format!(
                "Failed to read baseline screenshot '{}': {source}",
                baseline_path.display()
            ),
        )
    })?;
    let actual_path = bundle_dir
        .map(|dir| dir.join("runtime.png"))
        .unwrap_or_else(|| baseline_dir.join(".snapshot-compare-runtime.png"));
    let screenshot_payload = execute_screenshot_json(
        ScreenshotArgs {
            preset: screenshot_spec.preset.clone(),
            width: screenshot_spec.width,
            height: screenshot_spec.height,
            preset_catalogs: preset_catalogs.to_vec(),
            output: Some(actual_path.clone()),
            rpc_timeout_ms: DEFAULT_BRIDGE_RPC_TIMEOUT_MS,
        },
        context,
    )?;
    let actual_bytes =
        fs::read(&actual_path).map_err(|source| AppError::output_write(&actual_path, &source))?;
    let presets = effective_viewport_presets(context, preset_catalogs)?;
    let viewport = resolve_viewport_selection(
        &presets,
        screenshot_spec.preset.as_deref(),
        screenshot_spec.width,
        screenshot_spec.height,
    )?;
    let comparison = compare_screenshots(ComparisonRequest {
        baseline_path: &baseline_path,
        actual_path: None,
        baseline_bytes: &baseline_bytes,
        actual_bytes: &actual_bytes,
        preset: viewport
            .as_ref()
            .and_then(|selection| selection.preset.as_deref()),
        requested_width: screenshot_payload["requestedViewport"]["width"]
            .as_u64()
            .map(|value| value as u32),
        requested_height: screenshot_payload["requestedViewport"]["height"]
            .as_u64()
            .map(|value| value as u32),
        applied_width: screenshot_payload["appliedViewport"]["width"]
            .as_u64()
            .map(|value| value as u32),
        applied_height: screenshot_payload["appliedViewport"]["height"]
            .as_u64()
            .map(|value| value as u32),
        restored_viewport: screenshot_payload["restoredViewport"].as_bool(),
        restored_width: screenshot_payload["restoredViewportSize"]["width"]
            .as_u64()
            .map(|value| value as u32),
        restored_height: screenshot_payload["restoredViewportSize"]["height"]
            .as_u64()
            .map(|value| value as u32),
        max_diff_pixels: screenshot_spec.max_diff_pixels,
        max_diff_ratio: screenshot_spec.max_diff_ratio,
        // Snapshot regression keeps the historical exact-RGBA rule; the tolerance is opt-in per
        // `dev screenshot-diff` invocation, not a global loosening.
        pixel_tolerance: crate::visual_validation::PixelTolerance::default(),
        mask_path: None,
        mask_bytes: None,
        regions_path: None,
        regions_bytes: None,
        foreground_max_diff_ratio: None,
        roi_max_diff_ratio: None,
        required_comparisons: &[],
        bundle_dir,
        display_base_dir: Some(&context.config_origin.base_dir),
    })?;
    if bundle_dir.is_none() {
        let _ = fs::remove_file(&actual_path);
    }
    Ok(SectionComparison {
        matched: comparison.matched,
        details: comparison.payload,
    })
}

fn capture_state(spec: &SnapshotSpec, context: AppContext<'_>) -> Result<Value, AppError> {
    let payload = execute_state_json(StateArgs::default(), context)?;
    let selectors = spec
        .state
        .as_ref()
        .expect("state spec")
        .selectors
        .iter()
        .map(|path| {
            evaluate(&payload, path, &Predicate::Exists)
                .map(|evaluation| {
                    json!({
                        "path": path,
                        "value": evaluation.actual,
                        "resolution": evaluation.resolution.map(|resolution| {
                            json!({
                                "resolvedPrefix": resolution.resolved_prefix,
                                "missingField": resolution.missing_field,
                                "missingIndex": resolution.missing_index,
                                "availableKeys": resolution.available_keys,
                                "arrayLength": resolution.array_length,
                                "encounteredType": resolution.encountered_type,
                            })
                        }),
                    })
                })
                .map_err(|error| {
                    AppError::invalid_query(
                        "state.selectors",
                        &format!("Invalid snapshot selector '{path}': {error:?}"),
                    )
                })
        })
        .collect::<Result<Vec<_>, _>>()?;

    Ok(json!({
        "versions": version_metadata(&payload),
        "selectors": selectors,
    }))
}

fn capture_actions(context: AppContext<'_>) -> Result<Value, AppError> {
    // `inspect actions` advertises the static capability catalog only (its own
    // `availableActions` is always `[]`, by design — see output_json.rs
    // `inspect_actions_json`). The resolved, currently-executable actions live in
    // `state actions` (`spirectl.state-actions/v0`); capture from there instead.
    let args = StateArgs {
        view: Some(StateViewArg::Actions),
        ..StateArgs::default()
    };
    let payload = crate::state_actions::execute_state_actions_json(&args, context)?;
    let mut ids = payload["actions"]
        .as_array()
        .cloned()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|action| action.get("id").and_then(Value::as_str).map(str::to_string))
        .collect::<Vec<_>>();
    ids.sort();
    Ok(json!({
        "versions": version_metadata(&payload),
        "availableActionIds": ids,
    }))
}

fn capture_log_health(spec: &SnapshotSpec, context: AppContext<'_>) -> Result<Value, AppError> {
    let section = spec.log_health.as_ref().expect("log health section");
    let payload = execute_log_health_json(
        LogHealthArgs {
            limit: section.limit.unwrap_or(50),
            tail: section.tail,
            after_cursor: section.after_cursor,
            level: parse_log_level(section.level.as_deref())?,
            target: section.target.clone(),
            exclude_targets: section.exclude_targets.clone(),
            exclude_message_regexes: section.exclude_message_regexes.clone(),
        },
        context,
    )?;
    Ok(json!({
        "status": payload["status"],
        "unhealthyEntryCount": payload["unhealthyEntryCount"],
        "excludedEntryCount": payload["excludedEntryCount"],
    }))
}

fn capture_diagnostics(
    spec: &SnapshotSpec,
    preset_catalogs: &[PathBuf],
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let section = spec.diagnostics.as_ref().expect("diagnostics section");
    let payload = execute_diagnostics_json(
        DiagnosticsArgs {
            limit: section.limit.unwrap_or(100),
            tail: section.tail,
            after_cursor: section.after_cursor,
            level: parse_log_level(section.level.as_deref())?,
            target: section.target.clone(),
            exclude_targets: section.exclude_targets.clone(),
            exclude_message_regexes: section.exclude_message_regexes.clone(),
            preset: section.preset.clone(),
            width: section.width,
            height: section.height,
            preset_catalogs: preset_catalogs.to_vec(),
            hot_reload_project: None,
            bundle_dir: None,
        },
        context,
    )?;

    let capture_statuses = payload["captures"]
        .as_object()
        .map(|captures| {
            captures
                .iter()
                .map(|(name, value)| {
                    (
                        name.clone(),
                        value
                            .get("status")
                            .cloned()
                            .unwrap_or(Value::String("unknown".to_string())),
                    )
                })
                .collect::<Map<_, _>>()
        })
        .unwrap_or_default();

    Ok(json!({
        "status": payload["status"],
        "logHealth": {
            "status": payload["logHealth"]["status"],
        },
        "captures": capture_statuses,
    }))
}

fn compare_json_section(
    field: &str,
    baseline: Value,
    current: Value,
    compare_versions: bool,
) -> SectionComparison {
    let field_matches = baseline.get(field) == current.get(field);
    let versions_match = !compare_versions || baseline.get("versions") == current.get("versions");
    let matched = field_matches && versions_match;
    let mut details = json!({
        "matched": matched,
        "compareVersions": compare_versions,
        "baseline": baseline,
        "current": current,
    });
    if !matched {
        let (mismatch_field, expected, actual) = if !field_matches {
            (
                field,
                details["baseline"][field].clone(),
                details["current"][field].clone(),
            )
        } else {
            (
                "versions",
                details["baseline"]["versions"].clone(),
                details["current"]["versions"].clone(),
            )
        };
        details["mismatch"] = json!({
            "field": mismatch_field,
            "expected": expected,
            "actual": actual,
        });
    }

    SectionComparison { matched, details }
}

fn version_metadata(payload: &Value) -> Value {
    json!({
        "schemaVersion": payload["schemaVersion"],
        "gameVersion": payload["gameVersion"],
        "bridgeVersion": payload["bridgeVersion"],
    })
}

fn maybe_write_capture(
    bundle_dir: Option<&Path>,
    file_name: &str,
    payload: &Value,
) -> Result<(), AppError> {
    if let Some(bundle_dir) = bundle_dir {
        fs::create_dir_all(bundle_dir)
            .map_err(|source| AppError::output_write(bundle_dir, &source))?;
        write_json_file(&bundle_dir.join(file_name), payload)?;
    }
    Ok(())
}

fn read_snapshot_spec(path: &Path) -> Result<SnapshotSpec, AppError> {
    let text = fs::read_to_string(path).map_err(|source| AppError::config(path, source))?;
    serde_yaml::from_str(&text).map_err(|source| AppError::config_parse(path, source))
}

fn read_json_file(path: &Path) -> Result<Value, AppError> {
    let text = fs::read_to_string(path).map_err(|source| AppError::config(path, source))?;
    serde_json::from_str(&text).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "snapshot_read_failed",
                "message": format!("Failed to parse snapshot JSON '{}'.", path.display()),
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })
}

fn validate_spec(spec: &SnapshotSpec, path: &Path) -> Result<(), AppError> {
    let has_sections = spec.state.is_some()
        || spec.actions.is_some()
        || spec.log_health.is_some()
        || spec.diagnostics.is_some()
        || spec.screenshot.is_some();
    if !has_sections {
        return Err(AppError::invalid_query(
            "spec",
            &format!(
                "Snapshot spec '{}' must define at least one bounded capture section.",
                path.display()
            ),
        ));
    }
    if spec
        .state
        .as_ref()
        .is_some_and(|state| state.selectors.is_empty())
    {
        return Err(AppError::invalid_query(
            "state.selectors",
            "Snapshot state selectors must not be empty.",
        ));
    }
    Ok(())
}

fn parse_log_level(value: Option<&str>) -> Result<Option<crate::LogLevelArg>, AppError> {
    match value {
        None => Ok(None),
        Some(raw) => match raw.trim().to_ascii_lowercase().as_str() {
            "trace" => Ok(Some(crate::LogLevelArg::Trace)),
            "debug" => Ok(Some(crate::LogLevelArg::Debug)),
            "info" => Ok(Some(crate::LogLevelArg::Info)),
            "warn" => Ok(Some(crate::LogLevelArg::Warn)),
            "error" => Ok(Some(crate::LogLevelArg::Error)),
            other => Err(AppError::invalid_query(
                "level",
                &format!("Unsupported log level '{other}'."),
            )),
        },
    }
}

fn write_json_file(path: &Path, value: &Value) -> Result<(), AppError> {
    let rendered = serde_json::to_string_pretty(value).expect("json render");
    fs::write(path, format!("{rendered}\n")).map_err(|source| AppError::output_write(path, &source))
}

fn resolve_absolute_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

struct SectionComparison {
    matched: bool,
    details: Value,
}
