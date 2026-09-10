use super::{
    build_perspective_selector_values, handshake_request, observed_at_utc, semantic_fingerprint,
    write_ndjson_event,
};
use crate::{
    AppContext, AppError, DEFAULT_BRIDGE_RPC_TIMEOUT_MS, RuntimeBridgeClient, StateArgs,
    StateWatchModeArg, TransportKind, bridge, bridge_client, bridge_error_payload,
    context_with_transport_rpc_timeout,
};
use serde_json::{Value, json};
use std::io::Write;
use std::thread;
use std::time::{Duration, Instant};

/// Dispatch a `state --watch` invocation to the reactive (server-push) or polling
/// implementation depending on transport and bridge capability.
pub(crate) fn stream_state_watch_json<W: Write>(
    args: StateArgs,
    context: AppContext<'_>,
    writer: &mut W,
) -> Result<(), AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));

    let use_reactive = match args.watch_mode {
        StateWatchModeArg::Poll => false,
        StateWatchModeArg::Auto => {
            is_live_watch_transport(context.config.transport.kind)
                && bridge_supports_state_watch(&client, context)
        }
        StateWatchModeArg::Reactive => {
            if !is_live_watch_transport(context.config.transport.kind)
                || !bridge_supports_state_watch(&client, context)
            {
                return Err(state_watch_reactive_unsupported_error());
            }
            true
        }
    };

    if use_reactive {
        stream_state_watch_reactive_json(args, client, rpc_timeout_ms, writer)
    } else {
        stream_state_watch_polling_json(args, client, writer)
    }
}

fn stream_state_watch_polling_json<W: Write>(
    args: StateArgs,
    client: RuntimeBridgeClient,
    writer: &mut W,
) -> Result<(), AppError> {
    let poll_interval = Duration::from_millis(args.poll_interval_ms);
    let timeout = args.timeout_ms.map(Duration::from_millis);
    let started = Instant::now();
    let mut sequence = 0_u64;
    let mut last_fingerprint: Option<String> = None;
    let mut suppressed_duplicates = 0_u64;
    let request = bridge::proto::StateRequest {
        perspective: build_perspective_selector_values(args.perspective, args.player_id.as_deref()),
    };

    loop {
        if let Some(timeout) = timeout
            && started.elapsed() >= timeout
            && sequence > 0
        {
            break;
        }

        match client.state(request.clone()) {
            Ok(response) => {
                let state = crate::state::state_json(&response);
                let fingerprint = semantic_fingerprint(&state);
                if last_fingerprint.as_deref() != Some(fingerprint.as_str()) {
                    sequence += 1;
                    let event_type = if last_fingerprint.is_none() {
                        "initial"
                    } else {
                        "changed"
                    };
                    let mut event = json!({
                        "type": event_type,
                        "sequence": sequence,
                        "observedAtUtc": observed_at_utc(),
                        "fingerprint": fingerprint,
                        "state": state,
                    });
                    if suppressed_duplicates > 0 {
                        event["suppressedDuplicateCount"] = json!(suppressed_duplicates);
                        suppressed_duplicates = 0;
                    }
                    write_ndjson_event(writer, &event)?;
                    last_fingerprint = Some(fingerprint);
                    if args
                        .max_events
                        .is_some_and(|max_events| sequence >= max_events)
                    {
                        break;
                    }
                } else {
                    suppressed_duplicates += 1;
                }
            }
            Err(error) => {
                sequence += 1;
                let payload = bridge_error_payload(&error);
                let mut event = json!({
                    "type": "error",
                    "sequence": sequence,
                    "observedAtUtc": observed_at_utc(),
                    "error": payload.get("error").cloned().unwrap_or(payload),
                });
                if suppressed_duplicates > 0 {
                    event["suppressedDuplicateCount"] = json!(suppressed_duplicates);
                    suppressed_duplicates = 0;
                }
                write_ndjson_event(writer, &event)?;
                if args.fail_fast {
                    return Err(AppError::bridge(error));
                }
                if args
                    .max_events
                    .is_some_and(|max_events| sequence >= max_events)
                {
                    break;
                }
            }
        }

        if poll_interval > Duration::ZERO {
            thread::sleep(poll_interval);
        }
    }

    Ok(())
}

fn stream_state_watch_reactive_json<W: Write>(
    args: StateArgs,
    client: RuntimeBridgeClient,
    rpc_timeout_ms: u64,
    writer: &mut W,
) -> Result<(), AppError> {
    let mut sequence = 0_u64;
    let mut last_fingerprint: Option<String> = None;
    let mut suppressed_duplicates = 0_u64;
    let max_events = args.max_events;
    let fail_fast = args.fail_fast;
    let watch_request = build_state_watch_request(&args, rpc_timeout_ms);

    client
        .watch_state(watch_request, |event| {
            match event.payload {
                Some(bridge::proto::state_watch_event::Payload::State(response)) => {
                    let state = crate::state::state_json(&response);
                    let fingerprint = semantic_fingerprint(&state);
                    if last_fingerprint.as_deref() != Some(fingerprint.as_str()) {
                        sequence += 1;
                        let event_type = if last_fingerprint.is_none() {
                            "initial"
                        } else {
                            "changed"
                        };
                        let mut output = json!({
                            "type": event_type,
                            "sequence": sequence,
                            "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                            "fingerprint": fingerprint,
                            "state": state,
                        });
                        if suppressed_duplicates > 0 {
                            output["suppressedDuplicateCount"] = json!(suppressed_duplicates);
                            suppressed_duplicates = 0;
                        }
                        write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                        last_fingerprint = Some(fingerprint);
                        if max_events.is_some_and(|max_events| sequence >= max_events) {
                            return Ok(false);
                        }
                    } else {
                        suppressed_duplicates += 1;
                    }
                    Ok(true)
                }
                Some(bridge::proto::state_watch_event::Payload::Error(error)) => {
                    sequence += 1;
                    let mut output = json!({
                        "type": "error",
                        "sequence": sequence,
                        "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                        "error": bridge_error_payload(&error).get("error").cloned().unwrap_or_else(|| bridge_error_payload(&error)),
                    });
                    if suppressed_duplicates > 0 {
                        output["suppressedDuplicateCount"] = json!(suppressed_duplicates);
                        suppressed_duplicates = 0;
                    }
                    write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                    if fail_fast {
                        return Err(error);
                    }
                    if max_events.is_some_and(|max_events| sequence >= max_events) {
                        return Ok(false);
                    }
                    Ok(true)
                }
                None => Ok(true),
            }
        })
        .map_err(AppError::bridge)
}

fn build_state_watch_request(
    args: &StateArgs,
    rpc_timeout_ms: u64,
) -> bridge::proto::StateWatchRequest {
    bridge::proto::StateWatchRequest {
        state: Some(bridge::proto::StateRequest {
            perspective: build_perspective_selector_values(
                args.perspective,
                args.player_id.as_deref(),
            ),
        }),
        max_events: args.max_events.unwrap_or(0),
        timeout_ms: args.timeout_ms.unwrap_or(0),
        fail_fast: args.fail_fast,
        min_capture_interval_ms: args.poll_interval_ms,
        state_timeout_ms: rpc_timeout_ms,
        buffer_capacity: 16,
        emit_diff: false,
    }
}

pub(super) fn is_live_watch_transport(kind: TransportKind) -> bool {
    matches!(kind, TransportKind::Ipc | TransportKind::Tcp)
}

pub(super) fn bridge_supports_state_watch(
    client: &RuntimeBridgeClient,
    context: AppContext<'_>,
) -> bool {
    client
        .handshake(handshake_request(context))
        .map(|handshake| {
            handshake
                .capabilities
                .iter()
                .any(|capability| capability.id == "state-watch")
        })
        .unwrap_or(false)
}

pub(super) fn state_watch_reactive_unsupported_error() -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "state_watch_reactive_unsupported",
                "message": "state watch reactive mode requires a live bridge that advertises the state-watch capability."
            }
        }),
    }
}

pub(super) fn app_error_to_bridge_error(error: AppError) -> bridge::proto::BridgeError {
    let payload = error.payload;
    let message = payload
        .get("error")
        .and_then(|error| error.get("message"))
        .and_then(Value::as_str)
        .unwrap_or("state watch output failed")
        .to_string();
    bridge::proto::BridgeError {
        code: bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
        message,
        details: Vec::new(),
        action_failure: None,
    }
}

pub(super) fn non_empty_or_observed(value: String) -> String {
    if value.trim().is_empty() {
        observed_at_utc()
    } else {
        value
    }
}
