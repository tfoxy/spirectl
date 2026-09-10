use crate::{
    AppContext, AppError, BreakpointAddArgs, BreakpointRemoveArgs, DebugBreakpointKindArg,
    DebugEventsArgs, DebugSessionBoundArgs, DebugSessionEndArgs, DebugSessionRoleArg,
    DebugSessionStartArgs, DebugSessionStatusArgs, DebugStatusArgs, DebugStepArgs,
    DebugStepKindArg, DebugWaitArgs, Predicate, bridge_client, query::Predicate as QueryPredicate,
};
use serde_json::{Value, json};
use std::time::{Duration, Instant};

pub(crate) fn execute_debug_status_json(
    args: DebugStatusArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_status(crate::bridge::proto::DebugStatusRequest {
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(debug_status_json(&response))
}

pub(crate) fn execute_debug_session_start_json(
    args: DebugSessionStartArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_session_start(crate::bridge::proto::DebugSessionStartRequest {
            name: args.name.unwrap_or_default(),
            pause_on_start: args.pause,
            lease_timeout_ms: args.lease_timeout_ms.max(1),
            role: proto_session_role(args.role) as i32,
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "started": response.started,
        "session": response.session.as_ref().map(debug_session_json).unwrap_or(Value::Null),
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_debug_events_json(
    args: DebugEventsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let mut from_sequence = args.from_sequence;
    let limit = args.limit.max(1);
    let started = Instant::now();
    let timeout = Duration::from_millis(args.timeout_ms.max(1) as u64);
    let mut response;

    loop {
        response = client
            .debug_events(crate::bridge::proto::DebugEventStreamRequest {
                session_id: args.session.clone().unwrap_or_default(),
                from_sequence,
                limit,
            })
            .map_err(AppError::bridge)?;

        if !args.follow || !response.events.is_empty() || started.elapsed() >= timeout {
            break;
        }

        from_sequence = response.next_sequence;
        std::thread::sleep(
            Duration::from_millis(25).min(timeout.saturating_sub(started.elapsed())),
        );
    }

    let timed_out = args.follow && response.events.is_empty() && started.elapsed() >= timeout;
    Ok(debug_events_json(
        &response,
        args.follow,
        timed_out,
        args.timeout_ms.max(1),
    ))
}

pub(crate) fn execute_debug_session_status_json(
    args: DebugSessionStatusArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_session_status(crate::bridge::proto::DebugSessionStatusRequest { id: args.id })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "found": response.found,
        "session": response.session.as_ref().map(debug_session_json).unwrap_or(Value::Null),
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_debug_session_end_json(
    args: DebugSessionEndArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_session_end(crate::bridge::proto::DebugSessionEndRequest {
            id: args.id,
            resume_runtime: args.resume,
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "ended": response.ended,
        "id": response.id,
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_debug_pause_json(
    args: DebugSessionBoundArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_pause(crate::bridge::proto::DebugPauseRequest {
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(debug_operation_json(
        response.applied,
        response.status.as_ref(),
        &response.notices,
    ))
}

pub(crate) fn execute_debug_resume_json(
    args: DebugSessionBoundArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_resume(crate::bridge::proto::DebugResumeRequest {
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(debug_operation_json(
        response.applied,
        response.status.as_ref(),
        &response.notices,
    ))
}

pub(crate) fn execute_debug_step_json(
    args: DebugStepArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_step(crate::bridge::proto::DebugStepRequest {
            kind: proto_step_kind(args.kind) as i32,
            count: args.count.max(1),
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "applied": response.applied,
        "kind": debug_step_kind_arg_name(args.kind),
        "requestedKind": debug_step_kind_arg_name(args.kind),
        "count": args.count.max(1),
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_debug_wait_json(
    args: DebugWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .debug_wait(crate::bridge::proto::DebugWaitRequest {
            session_id: args.session,
            timeout_ms: args.timeout_ms.max(1),
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "completed": response.completed,
        "timedOut": response.timed_out,
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_breakpoint_list_json(
    args: DebugSessionBoundArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .breakpoint_list(crate::bridge::proto::DebugBreakpointListRequest {
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "breakpoints": response.breakpoints.iter().map(debug_breakpoint_json).collect::<Vec<_>>(),
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_breakpoint_add_json(
    args: BreakpointAddArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let predicate = args
        .predicate
        .to_predicate()
        .map_err(|message| AppError::invalid_query(&args.path, &message))?;
    if args.kind == DebugBreakpointKindArg::Match && predicate.is_none() {
        return Err(AppError::invalid_query(
            &args.path,
            "breakpoint add requires a predicate unless --kind change is used.",
        ));
    }
    let response = client
        .breakpoint_add(crate::bridge::proto::DebugBreakpointAddRequest {
            query_path: args.path,
            predicate: predicate.as_ref().map(proto_predicate),
            name: args.name.unwrap_or_default(),
            session_id: args.session.unwrap_or_default(),
            kind: proto_breakpoint_kind(args.kind) as i32,
            min_hit_count: args.min_hit_count.max(1),
            auto_remove_on_hit: args.auto_remove_on_hit,
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "added": response.added,
        "breakpoint": response.breakpoint.as_ref().map(debug_breakpoint_json).unwrap_or(Value::Null),
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_breakpoint_remove_json(
    args: BreakpointRemoveArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .breakpoint_remove(crate::bridge::proto::DebugBreakpointRemoveRequest {
            id: args.id,
            session_id: args.session.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;
    Ok(json!({
        "removed": response.removed,
        "id": response.id,
        "status": response.status.as_ref().map(debug_status_json).unwrap_or(Value::Null),
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    }))
}

fn debug_operation_json(
    applied: bool,
    status: Option<&crate::bridge::proto::DebugStatusResponse>,
    notices: &[crate::bridge::proto::DebugNotice],
) -> Value {
    json!({
        "applied": applied,
        "status": status.map(debug_status_json).unwrap_or(Value::Null),
        "notices": notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    })
}

fn debug_status_json(status: &crate::bridge::proto::DebugStatusResponse) -> Value {
    json!({
        "supported": status.supported,
        "executionState": debug_execution_state_name(status.execution_state),
        "pauseReason": debug_pause_reason_name(status.pause_reason),
        "pauseReasonDetail": empty_string_to_none(&status.pause_reason_detail),
        "canPause": status.can_pause,
        "canResume": status.can_resume,
        "supportedStepKinds": status.supported_step_kinds.iter().map(|raw| debug_step_kind_name(Some(*raw))).collect::<Vec<_>>(),
        "breakpoints": status.breakpoints.iter().map(debug_breakpoint_json).collect::<Vec<_>>(),
        "lastBreakpointHit": status.last_breakpoint_hit.as_ref().map(debug_breakpoint_hit_json),
        "notices": status.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
        "breakpointManagementSupported": status.breakpoint_management_supported,
        "breakpointEvaluationSupported": status.breakpoint_evaluation_supported,
        "sessionOwnership": debug_session_ownership_name(status.session_ownership),
        "activeSession": status.active_session.as_ref().map(debug_session_json),
        "callerRole": debug_session_role_name(status.caller_role),
        "observerSessions": status.observer_sessions.iter().map(debug_session_json).collect::<Vec<_>>(),
    })
}

fn debug_breakpoint_json(breakpoint: &crate::bridge::proto::DebugBreakpoint) -> Value {
    json!({
        "id": breakpoint.id,
        "name": empty_string_to_none(&breakpoint.name),
        "queryPath": breakpoint.query_path,
        "predicate": breakpoint.predicate.as_ref().map(debug_predicate_json).unwrap_or(Value::Null),
        "enabled": breakpoint.enabled,
        "provisional": breakpoint.provisional,
        "kind": debug_breakpoint_kind_name(breakpoint.kind),
        "minHitCount": breakpoint.min_hit_count,
        "hitCount": breakpoint.hit_count,
        "autoRemoveOnHit": breakpoint.auto_remove_on_hit,
        "lastObservedJson": empty_string_to_none(&breakpoint.last_observed_json),
    })
}

fn debug_breakpoint_hit_json(hit: &crate::bridge::proto::DebugBreakpointHit) -> Value {
    json!({
        "breakpointId": hit.breakpoint_id,
        "breakpointName": empty_string_to_none(&hit.breakpoint_name),
        "queryPath": hit.query_path,
        "predicate": hit.predicate.as_ref().map(debug_predicate_json).unwrap_or(Value::Null),
        "actual": empty_string_to_none(&hit.actual_json),
        "screenType": empty_string_to_none(&hit.screen_type),
        "screenInstanceId": empty_string_to_none(&hit.screen_instance_id),
        "kind": debug_breakpoint_kind_name(hit.kind),
        "hitCount": hit.hit_count,
    })
}

fn debug_session_json(session: &crate::bridge::proto::DebugSessionInfo) -> Value {
    json!({
        "id": session.id,
        "name": empty_string_to_none(&session.name),
        "leaseTimeoutMs": session.lease_timeout_ms,
        "leaseExpiresAtUnixMs": session.lease_expires_at_unix_ms,
        "role": debug_session_role_name(session.role),
    })
}

fn debug_events_json(
    response: &crate::bridge::proto::DebugEventStreamResponse,
    follow: bool,
    timed_out: bool,
    timeout_ms: u32,
) -> Value {
    json!({
        "events": response.events.iter().map(debug_event_json).collect::<Vec<_>>(),
        "fromSequence": response.from_sequence,
        "nextSequence": response.next_sequence,
        "oldestRetainedSequence": response.oldest_retained_sequence,
        "newestSequence": response.newest_sequence,
        "retention": {
            "oldestSequence": response.oldest_retained_sequence,
            "newestSequence": response.newest_sequence,
            "limit": response.retention_limit,
        },
        "expired": response.expired,
        "overflow": response.overflow,
        "follow": follow,
        "timedOut": timed_out,
        "timeoutMs": timeout_ms,
        "notices": response.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    })
}

fn debug_event_json(event: &crate::bridge::proto::DebugEvent) -> Value {
    let detail = match &event.detail {
        Some(crate::bridge::proto::debug_event::Detail::StatusChanged(value)) => json!({
            "previousExecutionState": debug_execution_state_name(value.previous_execution_state),
            "executionState": debug_execution_state_name(value.execution_state),
            "pauseReason": debug_pause_reason_name(value.pause_reason),
            "pauseReasonDetail": empty_string_to_none(&value.pause_reason_detail),
        }),
        Some(crate::bridge::proto::debug_event::Detail::BreakpointHit(value)) => json!({
            "hit": value.hit.as_ref().map(debug_breakpoint_hit_json).unwrap_or(Value::Null),
        }),
        Some(crate::bridge::proto::debug_event::Detail::Paused(value)) => json!({
            "reason": debug_pause_reason_name(value.reason),
            "detail": empty_string_to_none(&value.detail),
        }),
        Some(crate::bridge::proto::debug_event::Detail::Resumed(value)) => json!({
            "detail": empty_string_to_none(&value.detail),
        }),
        Some(crate::bridge::proto::debug_event::Detail::Stepped(value)) => json!({
            "kind": debug_step_kind_name(Some(value.kind)),
            "count": value.count,
            "detail": empty_string_to_none(&value.detail),
        }),
        Some(crate::bridge::proto::debug_event::Detail::LeaseChanged(value)) => json!({
            "previousController": value.previous_controller.as_ref().map(debug_session_json).unwrap_or(Value::Null),
            "controller": value.controller.as_ref().map(debug_session_json).unwrap_or(Value::Null),
            "conflict": value.conflict.as_ref().map(debug_lease_conflict_json).unwrap_or(Value::Null),
        }),
        Some(crate::bridge::proto::debug_event::Detail::ObserverAttached(value)) => json!({
            "observer": value.observer.as_ref().map(debug_session_json).unwrap_or(Value::Null),
        }),
        Some(crate::bridge::proto::debug_event::Detail::ObserverDetached(value)) => json!({
            "observer": value.observer.as_ref().map(debug_session_json).unwrap_or(Value::Null),
        }),
        Some(crate::bridge::proto::debug_event::Detail::Disconnected(value)) => json!({
            "session": value.session.as_ref().map(debug_session_json).unwrap_or(Value::Null),
            "reason": empty_string_to_none(&value.reason),
        }),
        Some(crate::bridge::proto::debug_event::Detail::ClientDropped(value)) => json!({
            "session": value.session.as_ref().map(debug_session_json).unwrap_or(Value::Null),
            "reason": empty_string_to_none(&value.reason),
            "droppedEventCount": value.dropped_event_count,
        }),
        Some(crate::bridge::proto::debug_event::Detail::StateChanged(value)) => json!({
            "screenType": empty_string_to_none(&value.screen_type),
            "screenInstanceId": empty_string_to_none(&value.screen_instance_id),
            "changedPaths": value.changed_paths,
            "selectedState": empty_string_to_none(&value.selected_state_json),
        }),
        None => Value::Null,
    };

    json!({
        "sequence": event.sequence,
        "unixTimeMs": event.unix_time_ms,
        "kind": debug_event_kind_name(event.kind),
        "sessionId": empty_string_to_none(&event.session_id),
        "sessionRole": debug_session_role_name(event.session_role),
        "notices": event.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
        "detail": detail,
    })
}

fn debug_lease_conflict_json(conflict: &crate::bridge::proto::DebugLeaseConflict) -> Value {
    json!({
        "activeController": conflict.active_controller.as_ref().map(debug_session_json).unwrap_or(Value::Null),
        "requestedName": empty_string_to_none(&conflict.requested_name),
        "requestedLeaseTimeoutMs": conflict.requested_lease_timeout_ms,
        "notices": conflict.notices.iter().map(debug_notice_json).collect::<Vec<_>>(),
    })
}

fn debug_predicate_json(predicate: &crate::bridge::proto::DebugPredicate) -> Value {
    json!({
        "operator": debug_predicate_operator_name(predicate.operator),
        "expected": empty_string_to_none(&predicate.expected_json),
    })
}

fn debug_notice_json(notice: &crate::bridge::proto::DebugNotice) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
    })
}

fn proto_predicate(predicate: &Predicate) -> crate::bridge::proto::DebugPredicate {
    let (operator, expected) = match predicate {
        QueryPredicate::Equals(value) => (
            crate::bridge::proto::DebugPredicateOperator::Equals,
            Some(value),
        ),
        QueryPredicate::Contains(value) => (
            crate::bridge::proto::DebugPredicateOperator::Contains,
            Some(value),
        ),
        QueryPredicate::Regex(value) => (
            crate::bridge::proto::DebugPredicateOperator::Regex,
            Some(value),
        ),
        QueryPredicate::GreaterThan(value) => (
            crate::bridge::proto::DebugPredicateOperator::Gt,
            Some(value),
        ),
        QueryPredicate::GreaterThanOrEqual(value) => (
            crate::bridge::proto::DebugPredicateOperator::Gte,
            Some(value),
        ),
        QueryPredicate::LessThan(value) => (
            crate::bridge::proto::DebugPredicateOperator::Lt,
            Some(value),
        ),
        QueryPredicate::LessThanOrEqual(value) => (
            crate::bridge::proto::DebugPredicateOperator::Lte,
            Some(value),
        ),
        QueryPredicate::Exists => (crate::bridge::proto::DebugPredicateOperator::Exists, None),
        QueryPredicate::NotExists => (
            crate::bridge::proto::DebugPredicateOperator::NotExists,
            None,
        ),
    };

    crate::bridge::proto::DebugPredicate {
        operator: operator as i32,
        expected_json: expected
            .map(serde_json::to_string)
            .transpose()
            .expect("predicate expected serializes")
            .unwrap_or_default(),
    }
}

fn proto_step_kind(kind: DebugStepKindArg) -> crate::bridge::proto::DebugStepKind {
    match kind {
        DebugStepKindArg::Frame => crate::bridge::proto::DebugStepKind::Frame,
        DebugStepKindArg::Action => crate::bridge::proto::DebugStepKind::Action,
    }
}

fn proto_breakpoint_kind(
    kind: DebugBreakpointKindArg,
) -> crate::bridge::proto::DebugBreakpointKind {
    match kind {
        DebugBreakpointKindArg::Match => crate::bridge::proto::DebugBreakpointKind::Match,
        DebugBreakpointKindArg::Change => crate::bridge::proto::DebugBreakpointKind::Change,
    }
}

fn proto_session_role(kind: DebugSessionRoleArg) -> crate::bridge::proto::DebugSessionRole {
    match kind {
        DebugSessionRoleArg::Controller => crate::bridge::proto::DebugSessionRole::Controller,
        DebugSessionRoleArg::Observer => crate::bridge::proto::DebugSessionRole::Observer,
    }
}

fn debug_step_kind_arg_name(kind: DebugStepKindArg) -> &'static str {
    match kind {
        DebugStepKindArg::Frame => "frame",
        DebugStepKindArg::Action => "action",
    }
}

fn debug_execution_state_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugExecutionState::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugExecutionState::Running) => "running",
        Some(crate::bridge::proto::DebugExecutionState::Paused) => "paused",
        Some(crate::bridge::proto::DebugExecutionState::Unsupported) => "unsupported",
        _ => "unspecified",
    }
}

fn debug_pause_reason_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugPauseReason::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugPauseReason::None) => "none",
        Some(crate::bridge::proto::DebugPauseReason::Manual) => "manual",
        Some(crate::bridge::proto::DebugPauseReason::Breakpoint) => "breakpoint",
        Some(crate::bridge::proto::DebugPauseReason::StepComplete) => "step-complete",
        Some(crate::bridge::proto::DebugPauseReason::Unsupported) => "unsupported",
        Some(crate::bridge::proto::DebugPauseReason::BridgeError) => "bridge-error",
        _ => "unspecified",
    }
}

fn debug_step_kind_name(raw: Option<i32>) -> &'static str {
    match raw.and_then(|value| crate::bridge::proto::DebugStepKind::try_from(value).ok()) {
        Some(crate::bridge::proto::DebugStepKind::Frame) => "frame",
        Some(crate::bridge::proto::DebugStepKind::Action) => "action",
        _ => "unspecified",
    }
}

fn debug_breakpoint_kind_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugBreakpointKind::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugBreakpointKind::Match) => "match",
        Some(crate::bridge::proto::DebugBreakpointKind::Change) => "change",
        _ => "unspecified",
    }
}

fn debug_session_ownership_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugSessionOwnership::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugSessionOwnership::Unowned) => "unowned",
        Some(crate::bridge::proto::DebugSessionOwnership::OwnedByCaller) => "owned-by-caller",
        Some(crate::bridge::proto::DebugSessionOwnership::LeasedElsewhere) => "leased-elsewhere",
        _ => "unspecified",
    }
}

fn debug_session_role_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugSessionRole::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugSessionRole::Controller) => "controller",
        Some(crate::bridge::proto::DebugSessionRole::Observer) => "observer",
        _ => "unspecified",
    }
}

fn debug_event_kind_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugEventKind::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugEventKind::StatusChanged) => "status-changed",
        Some(crate::bridge::proto::DebugEventKind::BreakpointHit) => "breakpoint-hit",
        Some(crate::bridge::proto::DebugEventKind::Paused) => "paused",
        Some(crate::bridge::proto::DebugEventKind::Resumed) => "resumed",
        Some(crate::bridge::proto::DebugEventKind::Stepped) => "stepped",
        Some(crate::bridge::proto::DebugEventKind::LeaseChanged) => "lease-changed",
        Some(crate::bridge::proto::DebugEventKind::ObserverAttached) => "observer-attached",
        Some(crate::bridge::proto::DebugEventKind::ObserverDetached) => "observer-detached",
        Some(crate::bridge::proto::DebugEventKind::Disconnected) => "disconnected",
        Some(crate::bridge::proto::DebugEventKind::ClientDropped) => "client-dropped",
        Some(crate::bridge::proto::DebugEventKind::StateChanged) => "state-changed",
        _ => "unspecified",
    }
}

fn debug_predicate_operator_name(raw: i32) -> &'static str {
    match crate::bridge::proto::DebugPredicateOperator::try_from(raw).ok() {
        Some(crate::bridge::proto::DebugPredicateOperator::Equals) => "equals",
        Some(crate::bridge::proto::DebugPredicateOperator::Contains) => "contains",
        Some(crate::bridge::proto::DebugPredicateOperator::Regex) => "regex",
        Some(crate::bridge::proto::DebugPredicateOperator::Gt) => "gt",
        Some(crate::bridge::proto::DebugPredicateOperator::Gte) => "gte",
        Some(crate::bridge::proto::DebugPredicateOperator::Lt) => "lt",
        Some(crate::bridge::proto::DebugPredicateOperator::Lte) => "lte",
        Some(crate::bridge::proto::DebugPredicateOperator::Exists) => "exists",
        Some(crate::bridge::proto::DebugPredicateOperator::NotExists) => "not-exists",
        _ => "unspecified",
    }
}

fn empty_string_to_none(value: &str) -> Option<&str> {
    if value.is_empty() { None } else { Some(value) }
}
