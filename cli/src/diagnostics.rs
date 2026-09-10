use crate::{
    AppContext, AppError, DEFAULT_BRIDGE_RPC_TIMEOUT_MS, DevSceneTreeArgs, DiagnosticsArgs,
    InspectActionsArgs, LogHealthArgs, LogsArgs, ModReloadStatusArgs, ScreenshotArgs, StateArgs,
    context_with_transport_rpc_timeout, execute_game_info_json, execute_inspect_actions_json,
    execute_logs_json, execute_mod_reload_status_capture_json, execute_runtime_scene_tree_json,
    execute_screenshot_json, execute_state_json,
};
use regex::Regex;
use serde_json::{Map, Value, json};
use std::fs;
use std::path::Path;

#[derive(Debug)]
struct LogHealthEvaluation {
    payload: Value,
    exceptions: Vec<Value>,
    healthy: bool,
}

#[derive(Debug)]
struct DiagnosticsCapture {
    capture: &'static str,
    artifact: &'static str,
    bundle_file_name: &'static str,
    payload: Result<Value, AppError>,
}

pub(crate) fn execute_log_health_json(
    args: LogHealthArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let filters = compile_filters(&args.exclude_message_regexes)?;
    let logs = execute_logs_json(logs_args_from_log_health(&args), context)?;
    let evaluation = evaluate_log_health(
        &logs,
        &args.exclude_targets,
        &filters,
        args.after_cursor.unwrap_or(0),
    );

    if evaluation.healthy {
        Ok(evaluation.payload)
    } else {
        Err(AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "log_unhealthy",
                    "message": "Runtime logs are unhealthy.",
                    "logHealth": evaluation.payload,
                    "exceptions": evaluation.exceptions,
                }
            }),
        })
    }
}

pub(crate) fn execute_diagnostics_json(
    args: DiagnosticsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let bridge_context = context_with_transport_rpc_timeout(
        context,
        context
            .config
            .transport
            .rpc_timeout_ms
            .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS),
    );
    let bridge_context = bridge_context.as_app_context(context.json_output);
    let filters = compile_filters(&args.exclude_message_regexes)?;
    let bundle_dir = args.bundle_dir.as_deref();
    let mut errors = Vec::new();
    let mut capture_map = Map::new();
    let mut bundle_files = Map::new();
    let mut exceptions = Vec::new();
    let mut successes = 0_usize;
    let mut status = "complete";

    if let Some(dir) = bundle_dir
        && let Err(source) = fs::create_dir_all(dir)
    {
        errors.push(bundle_error(
            "bundle",
            "create-dir",
            dir,
            &format!(
                "Failed to create diagnostics bundle directory '{}': {source}",
                dir.display()
            ),
        ));
        status = "partial";
    }

    let logs_capture = capture_json(
        "logs",
        "logs",
        "logs.json",
        execute_logs_json(logs_args_from_diagnostics(&args), context),
        bundle_dir,
        &mut errors,
        &mut bundle_files,
    );
    if logs_capture
        .get("status")
        .and_then(Value::as_str)
        .is_some_and(|value| value == "captured")
    {
        successes += 1;
    } else {
        status = "partial";
    }

    let log_health = if let Some(data) = logs_capture.get("data") {
        let evaluation = evaluate_log_health(
            data,
            &args.exclude_targets,
            &filters,
            args.after_cursor.unwrap_or(0),
        );
        if !evaluation.healthy {
            status = "partial";
        }
        exceptions.extend(evaluation.exceptions.clone());
        evaluation.payload
    } else {
        status = "partial";
        Value::Null
    };
    capture_map.insert("logs".to_string(), logs_capture);

    for capture in [
        DiagnosticsCapture {
            capture: "gameInfo",
            artifact: "game-info",
            bundle_file_name: "game-info.json",
            payload: execute_game_info_json(bridge_context),
        },
        DiagnosticsCapture {
            capture: "state",
            artifact: "state",
            bundle_file_name: "state.json",
            payload: execute_state_json(StateArgs::default(), bridge_context),
        },
        DiagnosticsCapture {
            capture: "inspectActions",
            artifact: "inspect-actions",
            bundle_file_name: "inspect-actions.json",
            payload: execute_inspect_actions_json(InspectActionsArgs::default(), bridge_context),
        },
        DiagnosticsCapture {
            capture: "sceneTree",
            artifact: "scene-tree",
            bundle_file_name: "scene-tree.json",
            payload: execute_runtime_scene_tree_json(
                DevSceneTreeArgs {
                    node_path: None,
                    properties: false,
                    computed_transform: false,
                    rpc_timeout_ms: DEFAULT_BRIDGE_RPC_TIMEOUT_MS,
                },
                bridge_context,
            ),
        },
    ] {
        let value = capture_json(
            capture.capture,
            capture.artifact,
            capture.bundle_file_name,
            capture.payload,
            bundle_dir,
            &mut errors,
            &mut bundle_files,
        );
        if value
            .get("status")
            .and_then(Value::as_str)
            .is_some_and(|state| state == "captured")
        {
            successes += 1;
        } else {
            status = "partial";
        }
        if let Some(error) = value.get("error")
            && looks_like_exception_error(error)
        {
            exceptions.push(json!({
                "kind": "capture-error",
                "capture": capture.capture,
                "message": error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("capture failed"),
                "error": error,
            }));
        }
        capture_map.insert(capture.capture.to_string(), value);
    }

    let screenshot_capture =
        capture_screenshot(&args, bundle_dir, &mut errors, &mut bundle_files, context);
    match screenshot_capture.get("status").and_then(Value::as_str) {
        Some("captured") => successes += 1,
        Some("error") => status = "partial",
        _ => {}
    }
    capture_map.insert("screenshot".to_string(), screenshot_capture);

    if let Some(project) = args.hot_reload_project.clone() {
        let value = capture_json(
            "hotReload",
            "hot-reload",
            "hot-reload.json",
            execute_mod_reload_status_capture_json(ModReloadStatusArgs { project }, context),
            bundle_dir,
            &mut errors,
            &mut bundle_files,
        );
        if value
            .get("status")
            .and_then(Value::as_str)
            .is_some_and(|state| state == "captured")
        {
            successes += 1;
        } else {
            status = "partial";
        }
        capture_map.insert("hotReload".to_string(), value);
    }

    if successes == 0 {
        return Err(AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "diagnostics_capture_failed",
                    "message": "No requested diagnostics captures could be produced.",
                    "errors": errors,
                }
            }),
        });
    }

    let bundle = bundle_dir.map(|dir| {
        json!({
            "dir": dir.display().to_string(),
            "files": Value::Object(bundle_files.clone()),
        })
    });

    let mut payload = json!({
        "status": status,
        "logHealth": log_health,
        "exceptions": exceptions,
        "captures": Value::Object(capture_map),
        "errors": errors,
    });

    if let Some(bundle) = bundle {
        payload
            .as_object_mut()
            .expect("diagnostics payload object")
            .insert("bundle".to_string(), bundle);
    }

    if let Some(dir) = bundle_dir {
        let diagnostics_path = dir.join("diagnostics.json");
        if let Err(source) = write_json(&diagnostics_path, &payload) {
            let issue = bundle_error(
                "diagnostics",
                "write",
                &diagnostics_path,
                &format!(
                    "Failed to write diagnostics bundle artifact '{}': {source}",
                    diagnostics_path.display()
                ),
            );
            payload["errors"]
                .as_array_mut()
                .expect("errors array")
                .push(issue);
            payload["status"] = Value::String("partial".to_string());
        } else if let Some(bundle_files) = payload
            .get_mut("bundle")
            .and_then(Value::as_object_mut)
            .and_then(|bundle| bundle.get_mut("files"))
            .and_then(Value::as_object_mut)
        {
            bundle_files.insert(
                "diagnostics".to_string(),
                Value::String(diagnostics_path.display().to_string()),
            );
        }
    }

    Ok(payload)
}

fn logs_args_from_log_health(args: &LogHealthArgs) -> LogsArgs {
    LogsArgs {
        source: crate::LogSourceArg::Bridge,
        limit: args.limit,
        tail: args.tail,
        after_cursor: args.after_cursor,
        follow: false,
        level: args.level,
        target: args.target.clone(),
    }
}

fn logs_args_from_diagnostics(args: &DiagnosticsArgs) -> LogsArgs {
    LogsArgs {
        source: crate::LogSourceArg::Bridge,
        limit: args.limit,
        tail: args.tail,
        after_cursor: args.after_cursor,
        follow: false,
        level: args.level,
        target: args.target.clone(),
    }
}

fn compile_filters(patterns: &[String]) -> Result<Vec<Regex>, AppError> {
    patterns
        .iter()
        .map(|pattern| {
            Regex::new(pattern).map_err(|source| AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_query",
                        "message": source.to_string(),
                        "path": "exclude-message-regex",
                    }
                }),
            })
        })
        .collect()
}

fn evaluate_log_health(
    logs: &Value,
    exclude_targets: &[String],
    exclude_message_regexes: &[Regex],
    after_cursor: u64,
) -> LogHealthEvaluation {
    let entries = logs
        .get("entries")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let next_cursor = logs.get("nextCursor").cloned().unwrap_or(Value::Null);

    let mut unhealthy_entries = Vec::new();
    let mut exceptions = Vec::new();
    let mut excluded_entry_count = 0_usize;

    for entry in &entries {
        if is_excluded(entry, exclude_targets, exclude_message_regexes) {
            excluded_entry_count += 1;
            continue;
        }

        let message = entry
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let is_exception = looks_like_exception_message(message);
        let is_error = entry
            .get("level")
            .and_then(Value::as_str)
            .is_some_and(|level| level == "error");

        if is_error || is_exception {
            unhealthy_entries.push(entry.clone());
        }
        if is_exception {
            exceptions.push(json!({
                "kind": "log",
                "cursor": entry.get("cursor").cloned().unwrap_or(Value::Null),
                "target": entry.get("target").cloned().unwrap_or(Value::Null),
                "message": message,
            }));
        }
    }

    let payload = json!({
        "status": if unhealthy_entries.is_empty() { "healthy" } else { "unhealthy" },
        "inspectedEntryCount": entries.len(),
        "excludedEntryCount": excluded_entry_count,
        "unhealthyEntryCount": unhealthy_entries.len(),
        "afterCursor": after_cursor,
        "nextCursor": next_cursor,
        "entries": unhealthy_entries,
    });

    LogHealthEvaluation {
        healthy: payload
            .get("status")
            .and_then(Value::as_str)
            .is_some_and(|value| value == "healthy"),
        payload,
        exceptions,
    }
}

fn is_excluded(
    entry: &Value,
    exclude_targets: &[String],
    exclude_message_regexes: &[Regex],
) -> bool {
    let target = entry
        .get("target")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    let message = entry
        .get("message")
        .and_then(Value::as_str)
        .unwrap_or_default();

    exclude_targets
        .iter()
        .any(|candidate| target.contains(&candidate.to_ascii_lowercase()))
        || exclude_message_regexes
            .iter()
            .any(|pattern| pattern.is_match(message))
}

fn looks_like_exception_message(message: &str) -> bool {
    let lower = message.to_ascii_lowercase();
    lower.contains("exception")
        || lower.contains("panic")
        || lower.contains("fatal")
        || lower.contains("traceback")
}

fn looks_like_exception_error(error: &Value) -> bool {
    error
        .get("message")
        .and_then(Value::as_str)
        .is_some_and(looks_like_exception_message)
}

fn capture_json(
    capture: &'static str,
    artifact: &'static str,
    bundle_file_name: &'static str,
    result: Result<Value, AppError>,
    bundle_dir: Option<&Path>,
    errors: &mut Vec<Value>,
    bundle_files: &mut Map<String, Value>,
) -> Value {
    match result {
        Ok(data) => {
            let mut payload = json!({
                "status": "captured",
                "data": data,
            });
            if let Some(dir) = bundle_dir {
                let path = dir.join(bundle_file_name);
                if let Err(source) = write_json(&path, payload.get("data").expect("capture data")) {
                    errors.push(bundle_error(
                        artifact,
                        "write",
                        &path,
                        &format!(
                            "Failed to write diagnostics artifact '{}': {source}",
                            path.display()
                        ),
                    ));
                } else {
                    payload
                        .as_object_mut()
                        .expect("capture payload object")
                        .insert(
                            "path".to_string(),
                            Value::String(path.display().to_string()),
                        );
                    bundle_files.insert(
                        capture.to_string(),
                        Value::String(path.display().to_string()),
                    );
                }
            }
            payload
        }
        Err(error) => {
            let error_payload = extract_error_payload(&error);
            errors.push(json!({
                "capture": capture,
                "operation": "collect",
                "error": error_payload,
            }));
            json!({
                "status": "error",
                "error": error_payload,
            })
        }
    }
}

fn capture_screenshot(
    args: &DiagnosticsArgs,
    bundle_dir: Option<&Path>,
    errors: &mut Vec<Value>,
    bundle_files: &mut Map<String, Value>,
    context: AppContext<'_>,
) -> Value {
    let Some(bundle_dir) = bundle_dir else {
        return json!({
            "status": "skipped",
            "reason": "bundle-dir-required",
        });
    };

    match execute_screenshot_json(
        ScreenshotArgs {
            preset: args.preset.clone(),
            width: args.width,
            height: args.height,
            preset_catalogs: args.preset_catalogs.clone(),
            output: Some(bundle_dir.join("runtime.png")),
            rpc_timeout_ms: DEFAULT_BRIDGE_RPC_TIMEOUT_MS,
        },
        context,
    ) {
        Ok(data) => {
            let mut payload = json!({
                "status": "captured",
                "data": data,
            });
            let captured_path = payload
                .get("data")
                .and_then(|data| data.get("path"))
                .and_then(Value::as_str)
                .map(ToString::to_string);
            if let Some(path) = captured_path {
                payload
                    .as_object_mut()
                    .expect("capture payload object")
                    .insert("path".to_string(), Value::String(path.clone()));
                bundle_files.insert("screenshot".to_string(), Value::String(path));
            }
            payload
        }
        Err(error) => {
            let error_payload = extract_error_payload(&error);
            errors.push(json!({
                "capture": "screenshot",
                "operation": "collect",
                "error": error_payload,
            }));
            json!({
                "status": "error",
                "error": error_payload,
            })
        }
    }
}

fn write_json(path: &Path, value: &Value) -> Result<(), std::io::Error> {
    let mut rendered = serde_json::to_string_pretty(value).expect("json render");
    rendered.push('\n');
    fs::write(path, rendered)
}

fn extract_error_payload(error: &AppError) -> Value {
    error
        .payload
        .get("error")
        .cloned()
        .unwrap_or_else(|| error.payload.clone())
}

fn bundle_error(artifact: &str, operation: &str, path: &Path, message: &str) -> Value {
    json!({
        "artifact": artifact,
        "operation": operation,
        "path": path.display().to_string(),
        "error": {
            "code": "output_write_failed",
            "message": message,
        }
    })
}
