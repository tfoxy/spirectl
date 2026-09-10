use super::state_watch::{
    app_error_to_bridge_error, is_live_watch_transport, non_empty_or_observed,
};
use super::{handshake_request, write_ndjson_event};
use crate::{
    AppContext, AppError, DEFAULT_BRIDGE_RPC_TIMEOUT_MS, EventsArgs, RuntimeBridgeClient, bridge,
    bridge_client, bridge_error_payload, context_with_transport_rpc_timeout,
};
use serde_json::{Value, json};
use std::io::Write;

/// Stream the ordered, non-deduplicated transient combat-event stream (floating damage
/// numbers) as newline-delimited JSON. Reactive (server-push) only — combat events are
/// inherently push, so there is no polling fallback. Requires a live IPC/TCP bridge that
/// advertises the `combat-events` capability.
pub(crate) fn stream_combat_events_watch_json<W: Write>(
    args: EventsArgs,
    context: AppContext<'_>,
    writer: &mut W,
) -> Result<(), AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));

    if !is_live_watch_transport(context.config.transport.kind)
        || !bridge_supports_combat_events(&client, context)
    {
        return Err(combat_events_unsupported_error());
    }

    let mut emitted = 0_u64;
    let max_events = args.max_events;
    let fail_fast = args.fail_fast;
    let watch_request = build_combat_events_request(&args, rpc_timeout_ms);

    client
        .watch_combat_events(watch_request, |event| {
            // The bridge assigns the authoritative monotonic sequence; emit it verbatim.
            match event.payload {
                Some(bridge::proto::combat_event::Payload::Damage(damage)) => {
                    emitted += 1;
                    let output = json!({
                        "type": "damage",
                        "sequence": event.sequence,
                        "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                        "damage": {
                            "targetCreatureId": damage.target_creature_id,
                            "amount": damage.amount,
                            "dealerCreatureId": nullable_string(damage.dealer_creature_id),
                            "sourceCardModelId": nullable_string(damage.source_card_model_id),
                        },
                    });
                    write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                    if max_events.is_some_and(|max_events| emitted >= max_events) {
                        return Ok(false);
                    }
                    Ok(true)
                }
                Some(bridge::proto::combat_event::Payload::CardUpgrade(upgrade)) => {
                    emitted += 1;
                    let output = json!({
                        "type": "cardUpgrade",
                        "sequence": event.sequence,
                        "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                        "cardUpgrade": {
                            "cardId": nullable_string(upgrade.card_id),
                            "cardModelId": upgrade.card_model_id,
                            "sourceRelicModelId": nullable_string(upgrade.source_relic_model_id),
                        },
                    });
                    write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                    if max_events.is_some_and(|max_events| emitted >= max_events) {
                        return Ok(false);
                    }
                    Ok(true)
                }
                Some(bridge::proto::combat_event::Payload::Vfx(vfx)) => {
                    emitted += 1;
                    let output = json!({
                        "type": "vfx",
                        "sequence": event.sequence,
                        "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                        "vfx": {
                            "scenePath": vfx.scene_path,
                            "anchorCreatureId": nullable_string(vfx.anchor_creature_id),
                            "amount": vfx.amount,
                        },
                    });
                    write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                    if max_events.is_some_and(|max_events| emitted >= max_events) {
                        return Ok(false);
                    }
                    Ok(true)
                }
                Some(bridge::proto::combat_event::Payload::Error(error)) => {
                    emitted += 1;
                    let output = json!({
                        "type": "error",
                        "sequence": event.sequence,
                        "observedAtUtc": non_empty_or_observed(event.observed_at_utc),
                        "error": bridge_error_payload(&error).get("error").cloned().unwrap_or_else(|| bridge_error_payload(&error)),
                    });
                    write_ndjson_event(writer, &output).map_err(app_error_to_bridge_error)?;
                    if fail_fast {
                        return Err(error);
                    }
                    if max_events.is_some_and(|max_events| emitted >= max_events) {
                        return Ok(false);
                    }
                    Ok(true)
                }
                None => Ok(true),
            }
        })
        .map_err(AppError::bridge)
}

fn build_combat_events_request(
    args: &EventsArgs,
    rpc_timeout_ms: u64,
) -> bridge::proto::WatchCombatEventsRequest {
    bridge::proto::WatchCombatEventsRequest {
        max_events: args.max_events.unwrap_or(0),
        timeout_ms: args.timeout_ms.unwrap_or(rpc_timeout_ms),
        fail_fast: args.fail_fast,
        since_sequence: args.since_sequence,
        buffer_capacity: 256,
    }
}

fn nullable_string(value: String) -> Value {
    if value.is_empty() {
        Value::Null
    } else {
        json!(value)
    }
}

pub(super) fn bridge_supports_combat_events(
    client: &RuntimeBridgeClient,
    context: AppContext<'_>,
) -> bool {
    client
        .handshake(handshake_request(context))
        .map(|handshake| {
            handshake
                .capabilities
                .iter()
                .any(|capability| capability.id == "combat-events")
        })
        .unwrap_or(false)
}

pub(super) fn combat_events_unsupported_error() -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "combat_events_unsupported",
                "message": "combat events watch requires a live IPC/TCP bridge that advertises the combat-events capability."
            }
        }),
    }
}
