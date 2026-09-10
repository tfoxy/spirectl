use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::{AppContext, AppError, bridge};
use crate::{
    Evaluation, LoadFixtureArgs, PathResolution, PerspectiveScopeArg, Predicate, QueryError,
    RuntimeBridgeClient, StateArgs, StateViewArg, TransportKind, ViewportPreset,
    attachment_state_name, bridge_client, context_with_transport_rpc_timeout, default_ipc_path,
    default_pipe_name, default_tcp_address, fixtures, handshake_json, load_viewport_presets, query,
    record_latest_fixture, transport_kind_name,
};
use serde_json::{Value, json};

pub(crate) fn execute_load_fixture_json(
    args: LoadFixtureArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let mut result = execute_load_fixture_json_from(args, None, context)?;
    // Best-effort: record this as the worktree's latest fixture so `dev fixture load-latest`
    // can replay it. A write failure must not fail an already-succeeded load, so surface it
    // as a non-fatal note instead.
    let recorded = match record_latest_fixture(&result) {
        Ok(path) => json!({ "recorded": true, "path": path.display().to_string() }),
        Err(error) => json!({ "recorded": false, "error": error.payload }),
    };
    if let Value::Object(map) = &mut result {
        map.insert("latestFixtureRecorded".to_string(), recorded);
    }
    Ok(result)
}

pub(crate) fn execute_load_fixture_json_from(
    args: LoadFixtureArgs,
    base_dir: Option<&Path>,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let prepared = fixtures::prepare_fixture(args.path(), base_dir)?;
    let client = bridge_client(context);
    let response = client
        .load_fixture(prepared.bridge_request("cli-load-fixture"))
        .map_err(AppError::bridge)?;
    Ok(prepared.success_json(&response))
}

pub(crate) fn execute_assert_json(
    path: String,
    predicate: Predicate,
    source: Option<StateViewArg>,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<String>,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let state = load_query_source_value(source, perspective, player_id.as_deref(), None, context)?;
    let evaluation = evaluate_query(&state, &path, &predicate)?;
    if !evaluation.matched {
        return Err(AppError::assertion_failed(&evaluation));
    }

    Ok(assertion_result_json(&evaluation))
}

#[allow(
    clippy::too_many_arguments,
    reason = "The wait command entrypoint mirrors CLI arguments and shared execution context."
)]
pub(crate) fn execute_wait_for_json(
    path: String,
    predicate: Predicate,
    source: Option<StateViewArg>,
    timeout_ms: u64,
    interval_ms: u64,
    rpc_timeout_ms: u64,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<String>,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let mut attempts = 0usize;

    loop {
        attempts += 1;
        let elapsed_ms = started_at
            .elapsed()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX);
        let remaining_ms = timeout_ms.saturating_sub(elapsed_ms).max(1);
        let effective_rpc_timeout_ms =
            effective_wait_for_rpc_timeout_ms(rpc_timeout_ms, remaining_ms);
        let state = load_query_source_value(
            source,
            perspective,
            player_id.as_deref(),
            Some(effective_rpc_timeout_ms),
            context,
        )?;
        let evaluation = evaluate_query(&state, &path, &predicate)?;
        if evaluation.matched {
            return Ok(json!({
                "matched": true,
                "path": evaluation.path,
                "operator": evaluation.operator,
                "expected": evaluation.expected,
                "actual": evaluation.actual,
                "pathExists": evaluation.actual.is_some(),
                "attempts": attempts,
                "elapsedMs": started_at.elapsed().as_millis()
            }));
        }

        if started_at.elapsed() >= Duration::from_millis(timeout_ms) {
            return Err(AppError::wait_timeout(
                &evaluation,
                timeout_ms,
                interval_ms,
                attempts,
                started_at.elapsed().as_millis(),
            ));
        }

        thread::sleep(Duration::from_millis(interval_ms));
    }
}

pub(crate) fn effective_wait_for_rpc_timeout_ms(rpc_timeout_ms: u64, remaining_ms: u64) -> u64 {
    if rpc_timeout_ms == 0 {
        0
    } else {
        rpc_timeout_ms.min(remaining_ms)
    }
}

pub(crate) fn execute_game_info_json(context: AppContext<'_>) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let handshake = client
        .handshake(handshake_request(context))
        .map_err(AppError::bridge)?;
    Ok(json!({
        "repository": "spirectl",
        "executable": "sts2",
        "transport": {
            "configuredKind": context.config.transport.kind,
            "configuredIpcPath": context.config.transport.ipc_path,
            "configuredPipeName": context.config.transport.pipe_name,
            "configuredTcpAddress": context.config.transport.tcp_address,
            "effectiveKind": transport_kind_name(enum_transport_kind(handshake.transport_kind)),
            "attachmentState": attachment_state_name(enum_attachment_state(handshake.attachment_state)),
        },
        "bridge": handshake_json(&handshake),
        "launchStdio": crate::lifecycle::launch_stdio_json(context.config, context.instance),
        "config": context.config,
        "notes": [
            "transport.kind=ipc is the default live local bridge path.",
            &format!(
                "transport.kind=ipc uses transport.ipcPath on Unix-like hosts (default {}) and transport.pipeName on Windows (default {}).",
                default_ipc_path(),
                default_pipe_name()
            ),
            "Use transport.kind=mock only for deterministic fixtures, docs, and tests.",
            "Use game.attach to poll a running IPC bridge, or use game.launch / game.deploy --restart --verify for CLI-managed lifecycle flows.",
            &format!(
                "transport.kind=tcp is executable over the standalone framed bridge protocol and defaults to {} with loopback-only validation.",
                default_tcp_address()
            )
        ]
    }))
}

pub(crate) fn resolve_screenshot_output_path(
    requested: Option<&Path>,
    artifacts_dir: &str,
    screen_type: &str,
) -> PathBuf {
    match requested {
        Some(path) => resolve_absolute_path(path),
        None => {
            let timestamp = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis();
            resolve_absolute_path(Path::new(artifacts_dir))
                .join("screenshots")
                .join(format!(
                    "{}-{}.png",
                    sanitize_screenshot_name(screen_type),
                    timestamp
                ))
        }
    }
}

pub(crate) fn sanitize_screenshot_name(screen_type: &str) -> String {
    let mut sanitized = screen_type
        .trim()
        .chars()
        .map(|ch| {
            if ch.is_ascii_alphanumeric() {
                ch.to_ascii_lowercase()
            } else {
                '-'
            }
        })
        .collect::<String>();
    sanitized = sanitized.trim_matches('-').to_string();
    if sanitized.is_empty() {
        "screenshot".to_string()
    } else {
        sanitized
    }
}

pub(crate) fn resolve_absolute_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

pub(crate) fn resolve_config_relative_path(path: &Path, context: AppContext<'_>) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        context.config_origin.config_dir.join(path)
    }
}

pub(crate) fn effective_viewport_presets(
    context: AppContext<'_>,
    cli_catalogs: &[PathBuf],
) -> Result<Vec<ViewportPreset>, AppError> {
    let mut catalog_paths = context
        .config
        .visual_validation
        .preset_catalogs
        .iter()
        .map(|path| resolve_config_relative_path(path, context))
        .collect::<Vec<_>>();
    catalog_paths.extend(cli_catalogs.iter().map(|path| resolve_absolute_path(path)));
    load_viewport_presets(&catalog_paths)
}

pub(crate) fn build_perspective_selector_values(
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<&str>,
) -> Option<bridge::proto::PerspectiveSelector> {
    if perspective.is_none() && player_id.is_none() {
        return None;
    }

    Some(bridge::proto::PerspectiveSelector {
        scope: match perspective.unwrap_or(PerspectiveScopeArg::Local) {
            PerspectiveScopeArg::Local => bridge::proto::PerspectiveScope::Local as i32,
            PerspectiveScopeArg::Omniscient => bridge::proto::PerspectiveScope::Omniscient as i32,
        },
        player_id: player_id.unwrap_or_default().to_string(),
    })
}

pub(crate) fn load_state_value(
    client: &RuntimeBridgeClient,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<&str>,
) -> Result<Value, AppError> {
    let response = client
        .state(bridge::proto::StateRequest {
            perspective: build_perspective_selector_values(perspective, player_id),
        })
        .map_err(AppError::bridge)?;
    Ok(crate::state::state_json(&response))
}

/// Resolve the document `dev assert`/`dev wait-for` query against: the plain state
/// snapshot (`source: None`, the default) or the resolved `spirectl.state-actions/v0`
/// envelope (`source: Some(StateViewArg::Actions)`), the same document `sts2 state
/// actions` returns. `rpc_timeout_ms`, when given, overrides the configured/default
/// bridge RPC timeout for this single fetch (used by `dev wait-for` to shrink the
/// per-attempt timeout as its overall deadline approaches).
pub(crate) fn load_query_source_value(
    source: Option<StateViewArg>,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<&str>,
    rpc_timeout_ms: Option<u64>,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match source {
        Some(StateViewArg::Actions) => {
            let args = StateArgs {
                view: Some(StateViewArg::Actions),
                perspective,
                player_id: player_id.map(str::to_string),
                rpc_timeout_ms,
                ..StateArgs::default()
            };
            crate::state_actions::execute_state_actions_json(&args, context)
        }
        None => {
            let scoped_context =
                rpc_timeout_ms.map(|ms| context_with_transport_rpc_timeout(context, ms));
            let effective_context = scoped_context
                .as_ref()
                .map(|scoped| scoped.as_app_context(context.json_output))
                .unwrap_or(context);
            let client = bridge_client(effective_context);
            load_state_value(&client, perspective, player_id)
        }
    }
}

pub(crate) fn evaluate_query(
    root: &Value,
    path: &str,
    predicate: &Predicate,
) -> Result<Evaluation, AppError> {
    query::evaluate(root, path, predicate).map_err(|error| match error {
        QueryError::InvalidPath(message) => AppError::invalid_query(path, &message),
    })
}

pub(crate) fn assertion_result_json(evaluation: &Evaluation) -> Value {
    json!({
        "matched": evaluation.matched,
        "path": evaluation.path,
        "operator": evaluation.operator,
        "expected": evaluation.expected,
        "actual": evaluation.actual,
        "pathExists": evaluation.actual.is_some(),
        "resolution": evaluation.resolution.as_ref().map(query_resolution_json)
    })
}

pub(crate) fn query_resolution_json(resolution: &PathResolution) -> Value {
    json!({
        "resolvedPrefix": if resolution.resolved_prefix.is_empty() {
            Value::Null
        } else {
            Value::String(resolution.resolved_prefix.clone())
        },
        "missingField": resolution.missing_field,
        "missingIndex": resolution.missing_index,
        "availableKeys": resolution.available_keys,
        "arrayLength": resolution.array_length,
        "encounteredType": resolution.encountered_type
    })
}

pub(crate) fn handshake_request(context: AppContext<'_>) -> bridge::proto::HandshakeRequest {
    bridge::proto::HandshakeRequest {
        cli_version: env!("CARGO_PKG_VERSION").to_string(),
        requested_schema_version: "spirectl/v0".to_string(),
        mode: context.mode.to_string(),
        transport_kind: match context.config.transport.kind {
            TransportKind::Mock => bridge::proto::TransportKind::Mock as i32,
            TransportKind::Ipc => bridge::proto::TransportKind::Ipc as i32,
            TransportKind::Tcp => bridge::proto::TransportKind::Tcp as i32,
        },
    }
}

pub(crate) fn enum_transport_kind(value: i32) -> bridge::proto::TransportKind {
    bridge::proto::TransportKind::try_from(value)
        .ok()
        .unwrap_or(bridge::proto::TransportKind::Unspecified)
}

pub(crate) fn enum_attachment_state(value: i32) -> bridge::proto::AttachmentState {
    bridge::proto::AttachmentState::try_from(value)
        .ok()
        .unwrap_or(bridge::proto::AttachmentState::Unspecified)
}

pub(crate) fn enum_action_kind(value: i32) -> bridge::proto::ActionKind {
    bridge::proto::ActionKind::try_from(value)
        .ok()
        .unwrap_or(bridge::proto::ActionKind::Unspecified)
}

pub(crate) fn enum_data_source(value: i32) -> bridge::proto::DataSource {
    bridge::proto::DataSource::try_from(value)
        .ok()
        .unwrap_or(bridge::proto::DataSource::Unspecified)
}
