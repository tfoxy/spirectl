pub(super) fn action_descriptor(
    id: &str,
    kind: proto::ActionKind,
    summary: &str,
    cli_command_hint: &str,
    provisional: bool,
    status: proto::ActionStatus,
    parameters: Vec<proto::ActionParameter>,
) -> proto::ActionDescriptor {
    proto::ActionDescriptor {
        id: id.to_string(),
        kind: kind as i32,
        summary: summary.to_string(),
        cli_command_hint: cli_command_hint.to_string(),
        provisional,
        status: status as i32,
        parameters,
        ..Default::default()
    }
}

pub(super) fn action_parameter(
    name: &str,
    value_type: &str,
    required: bool,
    summary: &str,
) -> proto::ActionParameter {
    proto::ActionParameter {
        name: name.to_string(),
        value_type: value_type.to_string(),
        required,
        summary: summary.to_string(),
    }
}

pub(super) fn capability(id: &str, summary: &str) -> proto::Capability {
    proto::Capability {
        id: id.to_string(),
        summary: summary.to_string(),
        provisional: true,
        ..Default::default()
    }
}

pub(super) fn debug_notice(code: &str, message: &str) -> proto::DebugNotice {
    proto::DebugNotice {
        code: code.to_string(),
        message: message.to_string(),
    }
}

pub(super) fn stub_debug_status(
    breakpoints: &[proto::DebugBreakpoint],
) -> proto::DebugStatusResponse {
    proto::DebugStatusResponse {
        supported: false,
        execution_state: proto::DebugExecutionState::Unsupported as i32,
        pause_reason: proto::DebugPauseReason::Unsupported as i32,
        pause_reason_detail: "Live debug control has not been implemented for this host."
            .to_string(),
        can_pause: false,
        can_resume: false,
        supported_step_kinds: vec![],
        breakpoints: breakpoints.to_vec(),
        last_breakpoint_hit: None,
        notices: vec![debug_notice(
            "debug_control_unavailable",
            "Live pause/resume/step hooks are not wired in this host yet.",
        )],
        breakpoint_management_supported: true,
        breakpoint_evaluation_supported: false,
        session_ownership: proto::DebugSessionOwnership::Unowned as i32,
        active_session: None,
        caller_role: proto::DebugSessionRole::Unspecified as i32,
        observer_sessions: Vec::new(),
    }
}

pub(super) fn debug_step_kind_name(raw: i32) -> &'static str {
    match proto::DebugStepKind::try_from(raw).ok() {
        Some(proto::DebugStepKind::Action) => "action",
        Some(proto::DebugStepKind::Frame) => "frame",
        _ => "unspecified",
    }
}

pub(super) fn action_response(
    request_id: &str,
    kind: proto::ActionKind,
    message: &str,
) -> proto::ActionResponse {
    action_response_with_provisional(request_id, kind, message, false)
}

pub(super) fn action_response_with_provisional(
    request_id: &str,
    kind: proto::ActionKind,
    message: &str,
    provisional: bool,
) -> proto::ActionResponse {
    proto::ActionResponse {
        request_id: request_id.to_string(),
        action_instance_id: format!("action:{}:{request_id}", action_kind_name(kind)),
        kind: kind as i32,
        accepted: true,
        provisional,
        message: message.to_string(),
        ..Default::default()
    }
}

pub(super) fn mouse_button_name(raw: i32) -> &'static str {
    match proto::RawMouseButton::try_from(raw).ok() {
        Some(proto::RawMouseButton::Right) => "right",
        Some(proto::RawMouseButton::Middle) => "middle",
        _ => "left",
    }
}

pub(super) fn error(
    code: proto::BridgeErrorCode,
    message: &str,
    details: &[proto::ErrorDetail],
) -> proto::BridgeError {
    let action_failure = if code == proto::BridgeErrorCode::InvalidAction {
        let first_detail = details.first();
        Some(proto::ActionFailureDetail {
            reason_code: first_detail
                .map(|detail| detail.action_reason_code)
                .filter(|code| *code != proto::ErrorActionFailureReasonCode::Unspecified as i32)
                .unwrap_or(proto::ErrorActionFailureReasonCode::InvalidAction as i32),
            screen: first_detail
                .map(|detail| detail.screen.clone())
                .unwrap_or_default(),
            player_id: first_detail
                .map(|detail| detail.player_id.clone())
                .unwrap_or_default(),
            perspective: first_detail
                .map(|detail| detail.perspective.clone())
                .unwrap_or_default(),
            checked_hook_paths: first_detail
                .map(|detail| detail.checked_hook_paths.clone())
                .unwrap_or_default(),
            field_diagnostics: details
                .iter()
                .map(|detail| proto::FieldDiagnostic {
                    field: detail.field.clone(),
                    value: detail.value.clone(),
                    note: detail.note.clone(),
                })
                .collect(),
            requested_player_id: first_detail
                .map(|detail| detail.requested_player_id.clone())
                .unwrap_or_default(),
            resolved_owner_player_id: first_detail
                .map(|detail| detail.resolved_owner_player_id.clone())
                .unwrap_or_default(),
            local_player_id: first_detail
                .map(|detail| detail.local_player_id.clone())
                .unwrap_or_default(),
            host_player_id: first_detail
                .map(|detail| detail.host_player_id.clone())
                .unwrap_or_default(),
            local_role: first_detail
                .map(|detail| detail.local_role)
                .unwrap_or_default(),
            action: first_detail
                .map(|detail| detail.action.clone())
                .unwrap_or_default(),
            remote_orchestration: first_detail
                .and_then(|detail| detail.remote_orchestration.clone()),
        })
    } else {
        None
    };
    proto::BridgeError {
        code: code as i32,
        message: message.to_string(),
        details: details.to_vec(),
        action_failure,
    }
}

pub(super) fn detail(field: &str, value: &str, note: &str) -> proto::ErrorDetail {
    proto::ErrorDetail {
        field: field.to_string(),
        value: value.to_string(),
        note: note.to_string(),
        ..Default::default()
    }
}

pub(super) fn wrong_player_detail(
    action: &str,
    requested_player_id: &str,
    resolved_owner_player_id: &str,
    screen: &str,
) -> proto::ErrorDetail {
    proto::ErrorDetail {
        field: "playerId".to_string(),
        value: requested_player_id.to_string(),
        note: "Retry with the advertised ownerPlayerId, or wait for a configured remote client capability before targeting a remote player.".to_string(),
        action_reason_code: proto::ErrorActionFailureReasonCode::WrongPlayer as i32,
        screen: screen.to_string(),
        player_id: requested_player_id.to_string(),
        perspective: requested_player_id.to_string(),
        requested_player_id: requested_player_id.to_string(),
        resolved_owner_player_id: resolved_owner_player_id.to_string(),
        local_player_id: "p:100".to_string(),
        host_player_id: "p:100".to_string(),
        local_role: proto::MultiplayerRole::Host as i32,
        action: action.to_string(),
        remote_orchestration: Some(if mock_is_host_local_seat(resolved_owner_player_id) {
            proto::RemoteClientOrchestrationCapability {
                id: "mock-host-local-seat".to_string(),
                state: proto::RemoteClientOrchestrationState::HostLocalSeat as i32,
                summary: "Mock host bridge owns this local player seat.".to_string(),
                provisional: true,
                ..Default::default()
            }
        } else {
            mock_remote_orchestration(MockScenario::Lobby)
        }),
        ..Default::default()
    }
}

pub(super) fn unsupported_perspective_detail(
    action: &str,
    requested_player_id: &str,
    resolved_owner_player_id: &str,
    screen: &str,
) -> proto::ErrorDetail {
    proto::ErrorDetail {
        field: "playerId".to_string(),
        value: requested_player_id.to_string(),
        note: "Remote-owned semantic actions require an explicitly configured remote client bridge; the host-local seat capability does not apply to true remote players.".to_string(),
        action_reason_code: proto::ErrorActionFailureReasonCode::UnsupportedPerspective as i32,
        screen: screen.to_string(),
        player_id: requested_player_id.to_string(),
        perspective: requested_player_id.to_string(),
        requested_player_id: requested_player_id.to_string(),
        resolved_owner_player_id: resolved_owner_player_id.to_string(),
        local_player_id: "p:100".to_string(),
        host_player_id: "p:100".to_string(),
        local_role: proto::MultiplayerRole::Host as i32,
        action: action.to_string(),
        remote_orchestration: Some(mock_remote_orchestration(MockScenario::Lobby)),
        ..Default::default()
    }
}

pub(super) fn requested_action_player_id(request: &proto::ActionRequest) -> Option<String> {
    request
        .perspective
        .as_ref()
        .map(|perspective| perspective.player_id.trim())
        .filter(|player_id| !player_id.is_empty())
        .map(str::to_string)
}

pub(super) fn stub_log_entries() -> Vec<proto::LogEntry> {
    vec![
        proto::LogEntry {
            cursor: 1,
            level: proto::LogLevel::Info as i32,
            target: "bridge.bootstrap".to_string(),
            message: "Stub bridge runtime created.".to_string(),
        },
        proto::LogEntry {
            cursor: 2,
            level: proto::LogLevel::Info as i32,
            target: "bridge.transport".to_string(),
            message: "Typed mock bridge transport is active.".to_string(),
        },
        proto::LogEntry {
            cursor: 3,
            level: proto::LogLevel::Debug as i32,
            target: "bridge.state".to_string(),
            message: "Returning scaffolded runtime state through the protobuf contract."
                .to_string(),
        },
    ]
}

pub(super) fn matches_minimum_level(
    level: proto::LogLevel,
    minimum_level: proto::LogLevel,
) -> bool {
    log_level_rank(level) >= log_level_rank(minimum_level)
}

pub(super) fn log_level_rank(level: proto::LogLevel) -> u8 {
    match level {
        proto::LogLevel::Trace => 1,
        proto::LogLevel::Debug => 2,
        proto::LogLevel::Info => 3,
        proto::LogLevel::Warn => 4,
        proto::LogLevel::Error => 5,
        proto::LogLevel::Unspecified => 0,
    }
}

pub(super) fn unwrap_handshake(
    result: proto::HandshakeResult,
) -> Result<proto::HandshakeResponse, proto::BridgeError> {
    match result.result {
        Some(proto::handshake_result::Result::Success(response)) => Ok(response),
        Some(proto::handshake_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Handshake returned an empty result envelope.",
            &[],
        )),
    }
}

pub(super) fn unwrap_state(
    result: proto::StateResult,
) -> Result<proto::StateResponse, proto::BridgeError> {
    match result.result {
        Some(proto::state_result::Result::Success(response)) => Ok(response),
        Some(proto::state_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "State returned an empty result envelope.",
            &[],
        )),
    }
}

pub(super) fn unwrap_action(
    result: proto::ActionResult,
) -> Result<proto::ActionResponse, proto::BridgeError> {
    match result.result {
        Some(proto::action_result::Result::Success(response)) => Ok(response),
        Some(proto::action_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Action returned an empty result envelope.",
            &[],
        )),
    }
}

pub(super) fn unwrap_console_command(
    result: proto::ConsoleCommandResult,
) -> Result<proto::ConsoleCommandResponse, proto::BridgeError> {
    match result.result {
        Some(proto::console_command_result::Result::Success(response)) => Ok(response),
        Some(proto::console_command_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty console command result.",
            &[detail(
                "result",
                "",
                "Expected ConsoleCommandResponse or BridgeError.",
            )],
        )),
    }
}

pub(super) fn unwrap_logs(
    result: proto::LogsResult,
) -> Result<proto::LogsResponse, proto::BridgeError> {
    match result.result {
        Some(proto::logs_result::Result::Success(response)) => Ok(response),
        Some(proto::logs_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Logs returned an empty result envelope.",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_status(
    result: proto::DebugStatusResult,
) -> Result<proto::DebugStatusResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_status_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_status_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug status envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_session_start(
    result: proto::DebugSessionStartResult,
) -> Result<proto::DebugSessionStartResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_session_start_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_session_start_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug session start envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_session_status(
    result: proto::DebugSessionStatusResult,
) -> Result<proto::DebugSessionStatusResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_session_status_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_session_status_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug session status envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_session_end(
    result: proto::DebugSessionEndResult,
) -> Result<proto::DebugSessionEndResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_session_end_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_session_end_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug session end envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_pause(
    result: proto::DebugPauseResult,
) -> Result<proto::DebugPauseResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_pause_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_pause_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug pause envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_resume(
    result: proto::DebugResumeResult,
) -> Result<proto::DebugResumeResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_resume_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_resume_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug resume envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_step(
    result: proto::DebugStepResult,
) -> Result<proto::DebugStepResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_step_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_step_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug step envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_wait(
    result: proto::DebugWaitResult,
) -> Result<proto::DebugWaitResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_wait_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_wait_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug wait envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_breakpoint_list(
    result: proto::DebugBreakpointListResult,
) -> Result<proto::DebugBreakpointListResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_breakpoint_list_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_breakpoint_list_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty breakpoint list envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_breakpoint_add(
    result: proto::DebugBreakpointAddResult,
) -> Result<proto::DebugBreakpointAddResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_breakpoint_add_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_breakpoint_add_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty breakpoint add envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_breakpoint_remove(
    result: proto::DebugBreakpointRemoveResult,
) -> Result<proto::DebugBreakpointRemoveResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_breakpoint_remove_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_breakpoint_remove_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty breakpoint remove envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_debug_events(
    result: proto::DebugEventStreamResult,
) -> Result<proto::DebugEventStreamResponse, proto::BridgeError> {
    match result.result {
        Some(proto::debug_event_stream_result::Result::Success(response)) => Ok(response),
        Some(proto::debug_event_stream_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "bridge returned an empty debug event stream envelope",
            &[],
        )),
    }
}

pub(super) fn unwrap_fixture(
    result: proto::FixtureLoadResult,
) -> Result<proto::FixtureLoadResponse, proto::BridgeError> {
    match result.result {
        Some(proto::fixture_load_result::Result::Success(response)) => Ok(response),
        Some(proto::fixture_load_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Fixture load response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_recorded_fixture(
    result: proto::RecordedFixtureResult,
) -> Result<proto::RecordedFixtureResponse, proto::BridgeError> {
    match result.result {
        Some(proto::recorded_fixture_result::Result::Success(response)) => Ok(response),
        Some(proto::recorded_fixture_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Recorded fixture response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_checkpoint_capture(
    result: proto::CheckpointCaptureResult,
) -> Result<proto::CheckpointCaptureResponse, proto::BridgeError> {
    match result.result {
        Some(proto::checkpoint_capture_result::Result::Success(response)) => Ok(response),
        Some(proto::checkpoint_capture_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Checkpoint capture response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_scenario_capture(
    result: proto::ScenarioCaptureResult,
) -> Result<proto::ScenarioCaptureResponse, proto::BridgeError> {
    match result.result {
        Some(proto::scenario_capture_result::Result::Success(response)) => Ok(response),
        Some(proto::scenario_capture_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Scenario capture response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_scenario_restore(
    result: proto::ScenarioRestoreResult,
) -> Result<proto::ScenarioRestoreResponse, proto::BridgeError> {
    match result.result {
        Some(proto::scenario_restore_result::Result::Success(response)) => Ok(response),
        Some(proto::scenario_restore_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Scenario restore response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_checkpoint_restore(
    result: proto::CheckpointRestoreResult,
) -> Result<proto::CheckpointRestoreResponse, proto::BridgeError> {
    match result.result {
        Some(proto::checkpoint_restore_result::Result::Success(response)) => Ok(response),
        Some(proto::checkpoint_restore_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Checkpoint restore response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_screenshot(
    result: proto::ScreenshotResult,
) -> Result<proto::ScreenshotResponse, proto::BridgeError> {
    match result.result {
        Some(proto::screenshot_result::Result::Success(response)) => Ok(response),
        Some(proto::screenshot_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Screenshot response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_scene_tree(
    result: proto::RuntimeSceneTreeResult,
) -> Result<proto::RuntimeSceneTreeResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_scene_tree_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_scene_tree_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime scene-tree response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_scene_node(
    result: proto::RuntimeSceneNodeResult,
) -> Result<proto::RuntimeSceneNodeResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_scene_node_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_scene_node_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime scene-node response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_scene_set_visible(
    result: proto::RuntimeSceneSetVisibleResult,
) -> Result<proto::RuntimeSceneSetVisibleResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_scene_set_visible_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_scene_set_visible_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime scene-node visibility response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_scene_hover(
    result: proto::RuntimeSceneControlHoverResult,
) -> Result<proto::RuntimeSceneControlHoverResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_scene_control_hover_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_scene_control_hover_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime scene control hover response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_scene_unhover(
    result: proto::RuntimeSceneControlUnhoverResult,
) -> Result<proto::RuntimeSceneControlUnhoverResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_scene_control_unhover_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_scene_control_unhover_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime scene control unhover response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_runtime_transition_status(
    result: proto::RuntimeTransitionStatusResult,
) -> Result<proto::RuntimeTransitionStatusResponse, proto::BridgeError> {
    match result.result {
        Some(proto::runtime_transition_status_result::Result::Success(response)) => Ok(response),
        Some(proto::runtime_transition_status_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Runtime transition status response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_presentation_resource_scene(
    result: proto::PresentationResourceSceneResult,
) -> Result<proto::PresentationResourceSceneResponse, proto::BridgeError> {
    match result.result {
        Some(proto::presentation_resource_scene_result::Result::Success(response)) => Ok(response),
        Some(proto::presentation_resource_scene_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Presentation resource scene response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_presentation_localization(
    result: proto::PresentationLocalizationResult,
) -> Result<proto::PresentationLocalizationResponse, proto::BridgeError> {
    match result.result {
        Some(proto::presentation_localization_result::Result::Success(response)) => Ok(response),
        Some(proto::presentation_localization_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Presentation localization response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_asset_extract(
    result: proto::AssetExtractResult,
) -> Result<proto::AssetExtractResponse, proto::BridgeError> {
    match result.result {
        Some(proto::asset_extract_result::Result::Success(response)) => Ok(response),
        Some(proto::asset_extract_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Asset extract response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_asset_catalog(
    result: proto::AssetCatalogResult,
) -> Result<proto::AssetCatalogResponse, proto::BridgeError> {
    match result.result {
        Some(proto::asset_catalog_result::Result::Success(response)) => Ok(response),
        Some(proto::asset_catalog_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Asset catalog response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_asset_explain(
    result: proto::AssetExplainResult,
) -> Result<proto::AssetExplainResponse, proto::BridgeError> {
    match result.result {
        Some(proto::asset_explain_result::Result::Success(response)) => Ok(response),
        Some(proto::asset_explain_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Asset explain response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_game_close(
    result: proto::GameCloseResult,
) -> Result<proto::GameCloseResponse, proto::BridgeError> {
    match result.result {
        Some(proto::game_close_result::Result::Success(response)) => Ok(response),
        Some(proto::game_close_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Game close response was missing both success and error payloads.",
            &[],
        )),
    }
}
