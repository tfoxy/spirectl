use std::fs::{self};
use std::io::Write;
use std::path::PathBuf;
use std::thread;
use std::time::Duration;

use super::{
    attach_runtime_scene_text_screenshot_ink, effective_viewport_presets, enum_data_source,
    measure_runtime_scene_text_ink, resolve_absolute_path, resolve_screenshot_output_path,
};
use crate::{AppContext, AppError, bridge};
use crate::{
    ComparisonMode, DevSceneChildrenArgs, DevSceneHoverArgs, DevSceneNodeArgs,
    DevSceneSetVisibleArgs, DevSceneTreeArgs, DevSceneUnhoverArgs, LogLevelArg, LogSourceArg,
    LogsArgs, ScreenshotArgs, ScreenshotDiffArgs, bridge_client, compare_screenshots,
    context_with_transport_rpc_timeout, logs_json, resolve_viewport_selection, stdout_write_error,
    visual_validation,
};
use serde_json::{Value, json};

pub(crate) fn execute_logs_json(
    args: LogsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    // The captured game stdio is a file read, not an RPC: it is readable after a
    // crash that took the bridge with it.
    if args.source == LogSourceArg::GameStdio {
        return Ok(json!({
            "source": "game-stdio",
            "gameStdio": crate::lifecycle::launch_stdio_tail_json(context.config, context.instance)
        }));
    }

    let client = bridge_client(context);
    let response = client
        .logs(logs_request(
            &args,
            args.tail.unwrap_or(args.limit),
            args.after_cursor.unwrap_or(0),
        ))
        .map_err(AppError::bridge)?;
    let mut payload = logs_json(&response);
    if args.source == LogSourceArg::Both
        && let Some(object) = payload.as_object_mut()
    {
        object.insert(
            "gameStdio".to_string(),
            crate::lifecycle::launch_stdio_tail_json(context.config, context.instance),
        );
    }
    Ok(payload)
}

pub(crate) fn logs_request(
    args: &LogsArgs,
    limit: u32,
    after_cursor: u64,
) -> bridge::proto::LogsRequest {
    bridge::proto::LogsRequest {
        limit,
        minimum_level: match args.level {
            Some(LogLevelArg::Trace) => bridge::proto::LogLevel::Trace as i32,
            Some(LogLevelArg::Debug) => bridge::proto::LogLevel::Debug as i32,
            Some(LogLevelArg::Info) => bridge::proto::LogLevel::Info as i32,
            Some(LogLevelArg::Warn) => bridge::proto::LogLevel::Warn as i32,
            Some(LogLevelArg::Error) => bridge::proto::LogLevel::Error as i32,
            None => bridge::proto::LogLevel::Unspecified as i32,
        },
        target_filter: args.target.clone().unwrap_or_default(),
        after_cursor,
    }
}

pub(crate) fn log_level_name_for_raw(raw: i32) -> &'static str {
    match bridge::proto::LogLevel::try_from(raw).ok() {
        Some(bridge::proto::LogLevel::Trace) => "trace",
        Some(bridge::proto::LogLevel::Debug) => "debug",
        Some(bridge::proto::LogLevel::Info) => "info",
        Some(bridge::proto::LogLevel::Warn) => "warn",
        Some(bridge::proto::LogLevel::Error) => "error",
        _ => "unspecified",
    }
}

pub(crate) fn data_source_name_for_raw(raw: i32) -> &'static str {
    match bridge::proto::DataSource::try_from(raw).ok() {
        Some(bridge::proto::DataSource::Stub) => "stub",
        Some(bridge::proto::DataSource::Live) => "live",
        _ => "unspecified",
    }
}

pub(crate) fn stream_follow_logs<W, F>(
    args: &LogsArgs,
    json_output: bool,
    writer: &mut W,
    mut read: F,
    max_polls: Option<usize>,
    poll_interval: Duration,
) -> Result<(), AppError>
where
    W: Write,
    F: FnMut(bridge::proto::LogsRequest) -> Result<bridge::proto::LogsResponse, AppError>,
{
    let initial_limit = args.tail.unwrap_or(args.limit);
    let mut after_cursor = 0_u64;
    let mut polls = 0_usize;

    loop {
        let limit = if polls == 0 {
            initial_limit
        } else {
            args.limit
        };
        let response = read(logs_request(args, limit, after_cursor))?;
        write_follow_logs_response(writer, &response, json_output)?;
        after_cursor = response.next_cursor.max(after_cursor);
        polls += 1;

        if max_polls.is_some_and(|max| polls >= max) {
            return Ok(());
        }

        if response.entries.is_empty() {
            thread::sleep(poll_interval);
        }
    }
}

pub(crate) fn write_follow_logs_response<W: Write>(
    writer: &mut W,
    response: &bridge::proto::LogsResponse,
    json_output: bool,
) -> Result<(), AppError> {
    for entry in &response.entries {
        if json_output {
            let line = serde_json::to_string(&json!({
                "cursor": entry.cursor,
                "level": log_level_name_for_raw(entry.level),
                "message": entry.message,
                "provisional": response.provisional,
                "source": data_source_name_for_raw(response.source),
                "target": entry.target,
            }))
            .expect("follow log lines serialize");
            writer
                .write_all(line.as_bytes())
                .map_err(stdout_write_error)?;
            writer.write_all(b"\n").map_err(stdout_write_error)?;
        } else {
            let line = format!(
                "[{}] {}: {}\n",
                log_level_name_for_raw(entry.level),
                entry.target,
                entry.message
            );
            writer
                .write_all(line.as_bytes())
                .map_err(stdout_write_error)?;
        }
    }

    writer.flush().map_err(stdout_write_error)
}

pub(crate) fn execute_screenshot_json(
    args: ScreenshotArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let presets = effective_viewport_presets(context, &args.preset_catalogs)?;
    let viewport =
        resolve_viewport_selection(&presets, args.preset.as_deref(), args.width, args.height)?;
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .screenshot(bridge::proto::ScreenshotRequest {
            viewport_width: viewport
                .as_ref()
                .map(|selection| selection.width)
                .unwrap_or_default(),
            viewport_height: viewport
                .as_ref()
                .map(|selection| selection.height)
                .unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    let output_path = resolve_screenshot_output_path(
        args.output.as_deref(),
        &context.config.artifacts.dir,
        &response.screen_type,
    );

    if let Some(parent) = output_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|source| AppError::output_write(&output_path, &source))?;
    }
    fs::write(&output_path, &response.contents)
        .map_err(|source| AppError::output_write(&output_path, &source))?;

    Ok(json!({
        "path": output_path.display().to_string(),
        "preset": viewport.and_then(|selection| selection.preset),
        "format": response.format,
        "byteLength": response.contents.len(),
        "width": response.width,
        "height": response.height,
        "requestedViewport": screenshot_viewport_json(response.requested_viewport_width, response.requested_viewport_height),
        "appliedViewport": screenshot_viewport_json(response.applied_viewport_width, response.applied_viewport_height),
        "restoredViewport": response.restored_viewport,
        "restoredViewportSize": screenshot_viewport_json(response.restored_viewport_width, response.restored_viewport_height),
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": response.screen_type,
            "instanceId": response.screen_instance_id,
        }
    }))
}

pub(crate) fn screenshot_viewport_json(width: u32, height: u32) -> Value {
    if width == 0 || height == 0 {
        Value::Null
    } else {
        json!({
            "width": width,
            "height": height,
        })
    }
}

pub(crate) fn execute_screenshot_diff_json(
    args: ScreenshotDiffArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    validate_diff_ratio("maxDiffRatio", args.max_diff_ratio)?;
    validate_diff_ratio("foregroundMaxDiffRatio", args.foreground_max_diff_ratio)?;
    validate_diff_ratio("roiMaxDiffRatio", args.roi_max_diff_ratio)?;
    let required_comparisons = parse_required_comparisons(&args.required_comparisons)?;
    let baseline_path = resolve_absolute_path(&args.baseline);
    let baseline_bytes = fs::read(&baseline_path).map_err(|source| {
        AppError::invalid_query(
            "baseline",
            &format!(
                "Failed to read baseline PNG '{}': {source}",
                baseline_path.display()
            ),
        )
    })?;
    let (mask_path, mask_bytes) = read_optional_png_input("mask", args.mask.as_ref())?;
    let (regions_path, regions_bytes) = read_optional_json_input("regions", args.regions.as_ref())?;

    let (
        actual_path,
        actual_bytes,
        viewport,
        requested_width,
        requested_height,
        applied_width,
        applied_height,
        restored_viewport,
        restored_width,
        restored_height,
    ) = if let Some(actual) = args.actual.as_ref() {
        if args.preset.is_some() || args.width.is_some() || args.height.is_some() {
            return Err(AppError::invalid_query(
                "viewport",
                "Offline screenshot diff mode does not accept --preset, --width, or --height.",
            ));
        }

        let actual_path = resolve_absolute_path(actual);
        let actual_bytes = fs::read(&actual_path).map_err(|source| {
            AppError::invalid_query(
                "actual",
                &format!(
                    "Failed to read actual PNG '{}': {source}",
                    actual_path.display()
                ),
            )
        })?;

        (
            Some(actual_path),
            actual_bytes,
            None,
            None,
            None,
            None,
            None,
            None,
            None,
            None,
        )
    } else {
        let presets = effective_viewport_presets(context, &args.preset_catalogs)?;
        let viewport =
            resolve_viewport_selection(&presets, args.preset.as_deref(), args.width, args.height)?;
        let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
        let client = bridge_client(scoped_context.as_app_context(context.json_output));
        let response = client
            .screenshot(bridge::proto::ScreenshotRequest {
                viewport_width: viewport
                    .as_ref()
                    .map(|selection| selection.width)
                    .unwrap_or_default(),
                viewport_height: viewport
                    .as_ref()
                    .map(|selection| selection.height)
                    .unwrap_or_default(),
            })
            .map_err(AppError::bridge)?;

        (
            None,
            response.contents,
            viewport,
            Some(response.requested_viewport_width),
            Some(response.requested_viewport_height),
            Some(response.applied_viewport_width),
            Some(response.applied_viewport_height),
            Some(response.restored_viewport),
            Some(response.restored_viewport_width),
            Some(response.restored_viewport_height),
        )
    };

    let comparison = compare_screenshots(visual_validation::ComparisonRequest {
        baseline_path: &baseline_path,
        actual_path: actual_path.as_deref(),
        baseline_bytes: &baseline_bytes,
        actual_bytes: &actual_bytes,
        preset: viewport
            .as_ref()
            .and_then(|selection| selection.preset.as_deref()),
        requested_width,
        requested_height,
        applied_width,
        applied_height,
        restored_viewport,
        restored_width,
        restored_height,
        max_diff_pixels: args.max_diff_pixels,
        max_diff_ratio: args.max_diff_ratio,
        pixel_tolerance: visual_validation::PixelTolerance {
            channel: args.pixel_tolerance.unwrap_or(0),
            ignore_alpha: args.ignore_alpha,
        },
        mask_path: mask_path.as_deref(),
        mask_bytes: mask_bytes.as_deref(),
        regions_path: regions_path.as_deref(),
        regions_bytes: regions_bytes.as_deref(),
        foreground_max_diff_ratio: args.foreground_max_diff_ratio,
        roi_max_diff_ratio: args.roi_max_diff_ratio,
        required_comparisons: &required_comparisons,
        bundle_dir: args.bundle_dir.as_deref(),
        display_base_dir: None,
    })?;

    if comparison.matched {
        Ok(comparison.payload)
    } else {
        Err(AppError::visual_mismatch(comparison.payload))
    }
}

pub(crate) fn validate_diff_ratio(field: &str, value: Option<f64>) -> Result<(), AppError> {
    if let Some(value) = value
        && (!value.is_finite() || !(0.0..1.0).contains(&value))
    {
        return Err(AppError::invalid_query(
            field,
            &format!("{field} must be greater than or equal to 0.0 and less than 1.0."),
        ));
    }
    Ok(())
}

pub(crate) fn parse_required_comparisons(
    values: &[String],
) -> Result<Vec<ComparisonMode>, AppError> {
    let mut modes = Vec::new();
    for value in values {
        let mode = match value.as_str() {
            "full" => ComparisonMode::Full,
            "foreground" => ComparisonMode::Foreground,
            "roi" => ComparisonMode::Roi,
            _ => {
                return Err(AppError::invalid_query(
                    "requiredComparison",
                    "required comparison must be one of: full, foreground, roi.",
                ));
            }
        };
        if !modes.contains(&mode) {
            modes.push(mode);
        }
    }
    Ok(modes)
}

pub(crate) fn read_optional_png_input(
    field: &str,
    path: Option<&PathBuf>,
) -> Result<(Option<PathBuf>, Option<Vec<u8>>), AppError> {
    read_optional_file_input(field, "PNG", path)
}

pub(crate) fn read_optional_json_input(
    field: &str,
    path: Option<&PathBuf>,
) -> Result<(Option<PathBuf>, Option<Vec<u8>>), AppError> {
    read_optional_file_input(field, "JSON", path)
}

pub(crate) fn read_optional_file_input(
    field: &str,
    kind: &str,
    path: Option<&PathBuf>,
) -> Result<(Option<PathBuf>, Option<Vec<u8>>), AppError> {
    let Some(path) = path else {
        return Ok((None, None));
    };
    let absolute = resolve_absolute_path(path);
    let bytes = fs::read(&absolute).map_err(|source| {
        AppError::invalid_query(
            field,
            &format!(
                "Failed to read {kind} input '{}': {source}",
                absolute.display()
            ),
        )
    })?;
    Ok((Some(absolute), Some(bytes)))
}

pub(crate) fn execute_runtime_scene_tree_json(
    args: DevSceneTreeArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .runtime_scene_tree(bridge::proto::RuntimeSceneTreeRequest {
            node_path: args.node_path.unwrap_or_default(),
            include_properties: args.properties,
            include_computed_transform: args.computed_transform,
        })
        .map_err(AppError::bridge)?;
    let screen = response.screen.as_ref();

    Ok(json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "rootNodePath": response.root_node_path,
        "nodes": response.nodes.iter().map(runtime_scene_node_json).collect::<Vec<_>>(),
        "notes": response.notes,
    }))
}

pub(crate) fn execute_runtime_scene_node_json(
    args: DevSceneNodeArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .runtime_scene_node(bridge::proto::RuntimeSceneNodeRequest {
            node_path: args.node_path,
            include_properties: args.properties || args.measure_text_ink,
            include_computed_transform: args.computed_transform || args.measure_text_ink,
        })
        .map_err(AppError::bridge)?;
    let screenshot_ink = if args.measure_text_ink {
        match response.node.as_ref() {
            Some(node) => measure_runtime_scene_text_ink(&client, node)?,
            None => None,
        }
    } else {
        None
    };
    let screen = response.screen.as_ref();

    let mut payload = json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "node": response.node.as_ref().map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
        "children": response.children.iter().map(runtime_scene_node_json).collect::<Vec<_>>(),
        "notes": response.notes,
    });

    if let Some(ink) = screenshot_ink {
        attach_runtime_scene_text_screenshot_ink(&mut payload, ink);
    }

    Ok(payload)
}

pub(crate) fn execute_runtime_scene_children_json(
    args: DevSceneChildrenArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .runtime_scene_node(bridge::proto::RuntimeSceneNodeRequest {
            node_path: args.node_path,
            include_properties: args.properties,
            include_computed_transform: false,
        })
        .map_err(AppError::bridge)?;
    let screen = response.screen.as_ref();

    Ok(json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "node": response.node.as_ref().map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
        "children": response.children.iter().map(runtime_scene_node_json).collect::<Vec<_>>(),
        "notes": response.notes,
    }))
}

pub(crate) fn execute_runtime_scene_hover_json(
    args: DevSceneHoverArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .runtime_scene_hover(bridge::proto::RuntimeSceneControlHoverRequest {
            node_path: args.node_path.unwrap_or_default(),
            presentation_element_id: args.element_id.unwrap_or_default(),
            include_hover_tip: args.hover_tip,
            settle_ms: args.settle_ms,
            ensure_visible: args.ensure_visible,
            allow_offscreen: args.allow_offscreen,
        })
        .map_err(AppError::bridge)?;
    let screen = response.screen.as_ref();

    Ok(json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "node": response.node.as_ref().map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
        "resolvedNodePath": empty_string_to_json(&response.resolved_node_path),
        "presentationElementId": empty_string_to_json(&response.presentation_element_id),
        "hovered": response.hovered,
        "hoverPosition": response.hover_position.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "hoverTip": response.hover_tip.as_ref().map(runtime_scene_hover_tip_json).unwrap_or(Value::Null),
        "visibility": response.visibility.as_ref().map(runtime_scene_hover_visibility_json).unwrap_or(Value::Null),
        "notes": response.notes,
    }))
}

pub(crate) fn runtime_scene_hover_visibility_json(
    visibility: &bridge::proto::RuntimeSceneHoverVisibility,
) -> Value {
    json!({
        "fullyVisible": visibility.fully_visible,
        "controlRect": visibility.control_rect.as_ref().map(runtime_scene_rect2_json).unwrap_or(Value::Null),
        "visibleRect": visibility.visible_rect.as_ref().map(runtime_scene_rect2_json).unwrap_or(Value::Null),
        "clippedBy": visibility.clipped_by.iter().map(|clip| {
            json!({
                "nodePath": empty_string_to_json(&clip.node_path),
                "nodeType": empty_string_to_json(&clip.node_type),
                "reason": empty_string_to_json(&clip.reason),
                "rect": clip.rect.as_ref().map(runtime_scene_rect2_json).unwrap_or(Value::Null),
            })
        }).collect::<Vec<_>>(),
        "scrolled": visibility.scrolled.iter().map(|scroll| {
            json!({
                "nodePath": empty_string_to_json(&scroll.node_path),
                "previousHorizontal": scroll.previous_horizontal,
                "previousVertical": scroll.previous_vertical,
                "horizontal": scroll.horizontal,
                "vertical": scroll.vertical,
            })
        }).collect::<Vec<_>>(),
    })
}

pub(crate) fn execute_runtime_scene_unhover_json(
    args: DevSceneUnhoverArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .runtime_scene_unhover(bridge::proto::RuntimeSceneControlUnhoverRequest {
            node_path: args.node_path.unwrap_or_default(),
            presentation_element_id: args.element_id.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    let screen = response.screen.as_ref();

    Ok(json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "node": response.node.as_ref().map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
        "resolvedNodePath": empty_string_to_json(&response.resolved_node_path),
        "presentationElementId": empty_string_to_json(&response.presentation_element_id),
        "hovered": response.hovered,
        "pointerPosition": response.pointer_position.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "notes": response.notes,
    }))
}

pub(crate) fn execute_runtime_scene_set_visible_json(
    args: DevSceneSetVisibleArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let requested_visible = args.visible && !args.hidden;
    let client = bridge_client(context);
    let response = client
        .runtime_scene_set_visible(bridge::proto::RuntimeSceneSetVisibleRequest {
            node_path: args.node_path.clone(),
            visible: requested_visible,
            include_computed_transform: args.computed_transform,
        })
        .map_err(AppError::bridge)?;
    let screen = response.screen.as_ref();

    let mut payload = json!({
        "source": bridge::data_source_name(enum_data_source(response.source)),
        "provisional": response.provisional,
        "screen": {
            "id": screen.map(|screen| screen.id.as_str()).unwrap_or_default(),
            "title": screen.map(|screen| screen.title.as_str()).unwrap_or_default(),
            "instanceId": screen.map(|screen| screen.screen_instance_id.as_str()).unwrap_or_default(),
        },
        "node": response.node.as_ref().map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
        "previousVisible": response.previous_visible,
        "requestedVisible": response.requested_visible,
        "changed": response.changed,
        "notes": response.notes,
    });

    if let Some(delay_ms) = args.verify_after_ms {
        std::thread::sleep(std::time::Duration::from_millis(delay_ms));
        let verification_response = client
            .runtime_scene_node(bridge::proto::RuntimeSceneNodeRequest {
                node_path: args.node_path,
                include_properties: true,
                include_computed_transform: args.computed_transform,
            })
            .map_err(AppError::bridge)?;
        let verification_node = verification_response.node.as_ref();
        let observed_visible = verification_node
            .and_then(|node| node.properties.as_ref())
            .and_then(|properties| properties.visible);
        let observed_effective_visible = verification_node
            .and_then(|node| node.properties.as_ref())
            .and_then(|properties| properties.effective_visible);
        let stable = observed_visible == Some(requested_visible);
        let notes = if stable {
            vec!["visibility_stable_after_mutation"]
        } else {
            vec!["visibility_reverted_after_mutation"]
        };

        payload["verification"] = json!({
            "requestedDelayMs": delay_ms,
            "stable": stable,
            "requestedVisible": requested_visible,
            "observedVisible": observed_visible,
            "observedEffectiveVisible": observed_effective_visible,
            "node": verification_node.map(runtime_scene_node_json).unwrap_or_else(|| json!(null)),
            "notes": notes,
        });
    }

    Ok(payload)
}

pub(crate) fn runtime_scene_node_json(node: &bridge::proto::RuntimeSceneNodeInfo) -> Value {
    let mut value = json!({
        "nodeId": node.node_id,
        "nodePath": node.node_path,
        "name": node.name,
        "nodeType": node.node_type,
        "parentNodePath": empty_string_to_json(node.parent_node_path.as_str()),
        "ownerPath": empty_string_to_json(node.owner_path.as_str()),
        "sceneFilePath": empty_string_to_json(node.scene_file_path.as_str()),
        "attachedScriptPath": empty_string_to_json(node.attached_script_path.as_str()),
        "attachedScriptType": empty_string_to_json(node.attached_script_type.as_str()),
        "nativeNodeType": empty_string_to_json(node.native_node_type.as_str()),
        "childCount": node.child_count,
        "notes": node.notes,
    });

    if let Some(properties) = node.properties.as_ref() {
        value["properties"] = runtime_scene_properties_json(properties);
    }

    if let Some(computed_transform) = node.computed_transform.as_ref() {
        value["computedTransform"] = runtime_scene_computed_transform_json(computed_transform);
    }

    value
}

pub(crate) fn runtime_scene_properties_json(
    properties: &bridge::proto::RuntimeSceneNodeProperties,
) -> Value {
    let mut value = json!({
        "visible": properties.visible,
        "effectiveVisible": properties.effective_visible,
        "position": properties.position.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "globalPosition": properties.global_position.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "scale": properties.scale.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "rotationRadians": properties.rotation_radians,
        "size": properties.size.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "pivotOffset": properties.pivot_offset.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "anchors": properties.anchors.as_ref().map(runtime_scene_anchors_json).unwrap_or(Value::Null),
        "offsets": properties.offsets.as_ref().map(runtime_scene_offsets_json).unwrap_or(Value::Null),
        "zIndex": properties.z_index,
        "showBehindParent": properties.show_behind_parent.map(Value::Bool).unwrap_or(Value::Null),
        "zAsRelative": properties.z_as_relative.map(Value::Bool).unwrap_or(Value::Null),
        "modulate": properties.modulate.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "selfModulate": properties.self_modulate.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "effectiveModulate": properties.effective_modulate.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "clipContents": properties.clip_contents.map(Value::Bool).unwrap_or(Value::Null),
        "clipChildrenMode": properties.clip_children_mode.map(Value::from).unwrap_or(Value::Null),
        "mouseFilter": properties.mouse_filter.map(Value::from).unwrap_or(Value::Null),
        "focusMode": properties.focus_mode.map(Value::from).unwrap_or(Value::Null),
        "mouseDefaultCursorShape": properties.mouse_default_cursor_shape.map(Value::from).unwrap_or(Value::Null),
        "textureRect": properties.texture_rect.as_ref().map(runtime_scene_texture_rect_json).unwrap_or(Value::Null),
        "material": properties.material.as_ref().map(runtime_scene_material_json).unwrap_or(Value::Null),
        "layout": properties.layout.as_ref().map(runtime_scene_layout_json).unwrap_or(Value::Null),
        "textures": properties.textures.iter().map(runtime_scene_resource_ref_json).collect::<Vec<_>>(),
        "notices": properties.notices.iter().map(runtime_scene_property_notice_json).collect::<Vec<_>>(),
    });

    if let Some(text) = properties.text.as_ref() {
        value["text"] = runtime_scene_text_properties_json(text);
    }
    if let Some(nine_patch) = properties.nine_patch.as_ref() {
        value["ninePatch"] = runtime_scene_nine_patch_json(nine_patch);
    }

    value
}

pub(crate) fn runtime_scene_layout_json(
    layout: &bridge::proto::RuntimeSceneLayoutProperties,
) -> Value {
    json!({
        "minimumSize": layout.minimum_size.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "combinedMinimumSize": layout.combined_minimum_size.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "customMinimumSize": layout.custom_minimum_size.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "sizeFlagsHorizontal": layout.size_flags_horizontal.map(|value| json!(value)).unwrap_or(Value::Null),
        "sizeFlagsVertical": layout.size_flags_vertical.map(|value| json!(value)).unwrap_or(Value::Null),
        "sizeFlagsStretchRatio": layout.size_flags_stretch_ratio.map(|value| json!(value)).unwrap_or(Value::Null),
        "layoutDirection": empty_string_to_json(&layout.layout_direction),
        "themeTypeVariation": empty_string_to_json(&layout.theme_type_variation),
        "containerAlignment": layout.container_alignment.map(|value| json!(value)).unwrap_or(Value::Null),
        "flowVertical": layout.flow_vertical.map(Value::Bool).unwrap_or(Value::Null),
        "themeConstants": layout.theme_constants.iter().map(runtime_scene_theme_constant_json).collect::<Vec<_>>(),
    })
}

pub(crate) fn runtime_scene_theme_constant_json(
    constant: &bridge::proto::RuntimeSceneThemeConstant,
) -> Value {
    json!({
        "name": constant.name,
        "value": constant.value.map(|value| json!(value)).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_texture_rect_json(
    texture_rect: &bridge::proto::RuntimeSceneTextureRectProperties,
) -> Value {
    json!({
        "stretchMode": empty_string_to_json(&texture_rect.stretch_mode),
        "expandMode": empty_string_to_json(&texture_rect.expand_mode),
        "flipH": texture_rect.flip_h,
        "flipV": texture_rect.flip_v,
    })
}

pub(crate) fn runtime_scene_material_json(
    material: &bridge::proto::RuntimeSceneMaterialProperties,
) -> Value {
    json!({
        "material": material.material.as_ref().map(runtime_scene_resource_ref_json).unwrap_or(Value::Null),
        "useParentMaterial": material.use_parent_material.map(Value::Bool).unwrap_or(Value::Null),
        "shader": material.shader.as_ref().map(runtime_scene_resource_ref_json).unwrap_or(Value::Null),
        "shaderParameters": material.shader_parameters.iter().map(runtime_scene_shader_parameter_json).collect::<Vec<_>>(),
        "blendMode": empty_string_to_json(&material.blend_mode),
    })
}

pub(crate) fn runtime_scene_shader_parameter_json(
    parameter: &bridge::proto::RuntimeSceneShaderParameter,
) -> Value {
    json!({
        "name": parameter.name,
        "valueKind": parameter.value_kind,
        "stringValue": empty_string_to_json(&parameter.string_value),
        "numberValue": parameter.number_value.map(Value::from).unwrap_or(Value::Null),
        "boolValue": parameter.bool_value.map(Value::Bool).unwrap_or(Value::Null),
        "colorValue": parameter.color_value.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "vector2Value": parameter.vector2_value.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "resourceValue": parameter.resource_value.as_ref().map(runtime_scene_resource_ref_json).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_nine_patch_json(
    nine_patch: &bridge::proto::RuntimeSceneNinePatchProperties,
) -> Value {
    json!({
        "texture": nine_patch.texture.as_ref().map(runtime_scene_resource_ref_json).unwrap_or(Value::Null),
        "drawCenter": nine_patch.draw_center,
        "patchMargins": nine_patch.patch_margins.as_ref().map(runtime_scene_patch_margins_json).unwrap_or(Value::Null),
        "axisStretchHorizontal": empty_string_to_json(&nine_patch.axis_stretch_horizontal),
        "axisStretchVertical": empty_string_to_json(&nine_patch.axis_stretch_vertical),
        "effectiveModulate": nine_patch.effective_modulate.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_patch_margins_json(
    margins: &bridge::proto::RuntimeScenePatchMargins,
) -> Value {
    json!({
        "left": margins.left,
        "top": margins.top,
        "right": margins.right,
        "bottom": margins.bottom,
    })
}

pub(crate) fn runtime_scene_text_properties_json(
    text: &bridge::proto::RuntimeSceneTextProperties,
) -> Value {
    let mut value = json!({
        "text": text.text,
        "rawText": text.raw_text,
        "richTextEnabled": text.rich_text_enabled,
        "source": text.source,
        "diagnosticSurface": text.diagnostic_surface,
        "font": text.font.as_ref().map(runtime_scene_resource_ref_json).unwrap_or(Value::Null),
        "fontSize": text.font_size,
        "lineHeight": text.line_height,
        "letterSpacing": text.letter_spacing,
        "fontWeight": text.font_weight,
        "fontStyle": text.font_style,
        "fontSizeSource": empty_string_to_json(&text.font_size_source),
        "appliedFontSize": text.applied_font_size,
        "themeFontSize": text.theme_font_size,
        "configuredMinFontSize": text.configured_min_font_size,
        "configuredMaxFontSize": text.configured_max_font_size,
        "textColor": text.text_color.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "outlineColor": text.outline_color.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "outlineSize": text.outline_size,
        "richTextSpans": text.rich_text_spans.iter().map(runtime_scene_rich_text_span_json).collect::<Vec<_>>(),
        "notices": text.notices.iter().map(runtime_scene_property_notice_json).collect::<Vec<_>>(),
    });
    if let Some(layout) = text.layout.as_ref() {
        value["layout"] = runtime_scene_text_layout_json(layout);
    }
    if let Some(recipe) = text.recipe.as_ref() {
        value["recipe"] = runtime_scene_text_recipe_json(recipe);
    }
    if let Some(metrics) = text.rendered_metrics.as_ref() {
        value["renderedMetrics"] = runtime_scene_text_rendered_metrics_json(metrics);
    }
    if let Some(shadow) = text.shadow.as_ref() {
        value["shadow"] = runtime_scene_text_shadow_json(shadow);
    }

    value
}

pub(crate) fn runtime_scene_text_recipe_json(
    recipe: &bridge::proto::RuntimeSceneTextRecipe,
) -> Value {
    json!({
        "source": recipe.source,
        "autoSizeEnabled": recipe.auto_size_enabled,
        "minFontSizePx": recipe.min_font_size_px,
        "maxFontSizePx": recipe.max_font_size_px,
        "nominalFontSizePx": recipe.nominal_font_size_px,
        "richTextEnabled": recipe.rich_text_enabled,
        "wrapMode": recipe.wrap_mode.as_deref().map(empty_string_to_json).unwrap_or(Value::Null),
        "breakFlags": recipe.break_flags.as_deref().map(empty_string_to_json).unwrap_or(Value::Null),
        "justificationFlags": recipe.justification_flags.as_deref().map(empty_string_to_json).unwrap_or(Value::Null),
        "textOverrunBehavior": recipe.text_overrun_behavior.as_deref().map(empty_string_to_json).unwrap_or(Value::Null),
        "horizontallyBound": recipe.horizontally_bound,
        "verticallyBound": recipe.vertically_bound,
    })
}

pub(crate) fn runtime_scene_text_rendered_metrics_json(
    metrics: &bridge::proto::RuntimeSceneTextRenderedMetrics,
) -> Value {
    json!({
        "metricSource": empty_string_to_json(&metrics.metric_source),
        "fontAscentPx": metrics.font_ascent_px,
        "fontDescentPx": metrics.font_descent_px,
        "fontHeightPx": metrics.font_height_px,
        "paragraphSizeWidthPx": metrics.paragraph_size_width_px,
        "paragraphSizeHeightPx": metrics.paragraph_size_height_px,
        "lines": metrics.lines.iter().map(runtime_scene_text_rendered_line_metrics_json).collect::<Vec<_>>(),
    })
}

pub(crate) fn runtime_scene_text_rendered_line_metrics_json(
    line: &bridge::proto::RuntimeSceneTextRenderedLineMetrics,
) -> Value {
    json!({
        "index": line.index,
        "paragraphAscentPx": line.paragraph_ascent_px,
        "paragraphDescentPx": line.paragraph_descent_px,
        "paragraphLineSizeWidthPx": line.paragraph_line_size_width_px,
        "paragraphLineSizeHeightPx": line.paragraph_line_size_height_px,
        "paragraphLineWidthPx": line.paragraph_line_width_px,
    })
}

pub(crate) fn runtime_scene_text_layout_json(
    layout: &bridge::proto::RuntimeSceneTextLayout,
) -> Value {
    json!({
        "horizontalAlignment": layout.horizontal_alignment,
        "verticalAlignment": layout.vertical_alignment,
        "baselineOffset": layout.baseline_offset,
        "ascent": layout.ascent,
        "descent": layout.descent,
        "lineHeight": layout.line_height,
        "contentWidth": layout.content_width,
        "contentHeight": layout.content_height,
        "clipContents": layout.clip_contents,
        "lines": layout.lines.iter().map(runtime_scene_text_line_json).collect::<Vec<_>>(),
    })
}

pub(crate) fn runtime_scene_text_line_json(line: &bridge::proto::RuntimeSceneTextLine) -> Value {
    json!({
        "index": line.index,
        "text": line.text,
        "x": line.x,
        "y": line.y,
        "baselineY": line.baseline_y,
        "width": line.width,
        "height": line.height,
        "start": line.start,
        "end": line.end,
    })
}

pub(crate) fn runtime_scene_text_shadow_json(
    shadow: &bridge::proto::RuntimeSceneTextShadow,
) -> Value {
    json!({
        "color": shadow.color.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "offset": shadow.offset.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "size": shadow.size.map(Value::from).unwrap_or(Value::Null),
        "outlineSize": shadow.outline_size.map(Value::from).unwrap_or(Value::Null),
        "source": empty_string_to_json(&shadow.source),
        "stackedShadows": shadow.stacked_shadows.iter().map(runtime_scene_stacked_text_shadow_json).collect::<Vec<_>>(),
    })
}

pub(crate) fn runtime_scene_stacked_text_shadow_json(
    shadow: &bridge::proto::RuntimeSceneStackedTextShadow,
) -> Value {
    json!({
        "index": shadow.index,
        "color": shadow.color.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
        "offset": shadow.offset.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "outlineSize": shadow.outline_size.map(Value::from).unwrap_or(Value::Null),
        "source": empty_string_to_json(&shadow.source),
    })
}

pub(crate) fn runtime_scene_hover_tip_json(
    hover_tip: &bridge::proto::RuntimeSceneHoverTip,
) -> Value {
    json!({
        "visible": hover_tip.visible,
        "nodePath": empty_string_to_json(&hover_tip.node_path),
        "title": hover_tip.title,
        "text": hover_tip.text,
        "labels": hover_tip.labels.iter().map(|label| {
            json!({
                "nodePath": label.node_path,
                "name": label.name,
                "text": label.text,
            })
        }).collect::<Vec<_>>(),
        "notes": hover_tip.notes,
    })
}

pub(crate) fn runtime_scene_rich_text_span_json(
    span: &bridge::proto::RuntimeSceneRichTextSpan,
) -> Value {
    json!({
        "tag": span.tag,
        "text": span.text,
        "color": span.color.as_ref().map(runtime_scene_color_json).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_computed_transform_json(
    computed: &bridge::proto::RuntimeSceneComputedTransform,
) -> Value {
    json!({
        "localTransform": computed.local_transform.as_ref().map(runtime_scene_transform2d_json).unwrap_or(Value::Null),
        "globalTransform": computed.global_transform.as_ref().map(runtime_scene_transform2d_json).unwrap_or(Value::Null),
        "globalRect": computed.global_rect.as_ref().map(runtime_scene_rect2_json).unwrap_or(Value::Null),
        "viewportClippedRect": computed.viewport_clipped_rect.as_ref().map(runtime_scene_rect2_json).unwrap_or(Value::Null),
        "notices": computed.notices.iter().map(runtime_scene_property_notice_json).collect::<Vec<_>>(),
    })
}

pub(crate) fn runtime_scene_vector2_json(vector: &bridge::proto::RuntimeSceneVector2) -> Value {
    json!({ "x": vector.x, "y": vector.y })
}

pub(crate) fn runtime_scene_color_json(color: &bridge::proto::RuntimeSceneColor) -> Value {
    json!({
        "r": color.r,
        "g": color.g,
        "b": color.b,
        "a": color.a,
        "html": color.html,
    })
}

pub(crate) fn runtime_scene_transform2d_json(
    transform: &bridge::proto::RuntimeSceneTransform2D,
) -> Value {
    json!({
        "xAxis": transform.x_axis.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "yAxis": transform.y_axis.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "origin": transform.origin.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_rect2_json(rect: &bridge::proto::RuntimeSceneRect2) -> Value {
    json!({
        "position": rect.position.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
        "size": rect.size.as_ref().map(runtime_scene_vector2_json).unwrap_or(Value::Null),
    })
}

pub(crate) fn runtime_scene_anchors_json(anchors: &bridge::proto::RuntimeSceneAnchors) -> Value {
    json!({
        "left": anchors.left,
        "top": anchors.top,
        "right": anchors.right,
        "bottom": anchors.bottom,
    })
}

pub(crate) fn runtime_scene_offsets_json(offsets: &bridge::proto::RuntimeSceneOffsets) -> Value {
    json!({
        "left": offsets.left,
        "top": offsets.top,
        "right": offsets.right,
        "bottom": offsets.bottom,
    })
}

pub(crate) fn runtime_scene_resource_ref_json(
    resource: &bridge::proto::RuntimeSceneResourceRef,
) -> Value {
    json!({
        "field": resource.field,
        "resourcePath": empty_string_to_json(resource.resource_path.as_str()),
        "resourceType": resource.resource_type,
        "resourceName": empty_string_to_json(resource.resource_name.as_str()),
    })
}

pub(crate) fn runtime_scene_property_notice_json(
    notice: &bridge::proto::RuntimeScenePropertyNotice,
) -> Value {
    json!({
        "code": notice.code,
        "field": notice.field,
        "message": notice.message,
    })
}

pub(crate) fn empty_string_to_json(value: &str) -> Value {
    if value.is_empty() {
        Value::Null
    } else {
        Value::String(value.to_string())
    }
}
