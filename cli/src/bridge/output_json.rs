use super::*;

pub fn bridge_error_exit_code(error: &proto::BridgeError) -> i32 {
    match proto::BridgeErrorCode::try_from(error.code)
        .ok()
        .unwrap_or(proto::BridgeErrorCode::RuntimeFailure)
    {
        proto::BridgeErrorCode::TransportUnavailable
        | proto::BridgeErrorCode::TransportMisconfigured
        | proto::BridgeErrorCode::TransportConnectionFailed
        | proto::BridgeErrorCode::IpcSocketMissing
        | proto::BridgeErrorCode::IpcConnectionFailed
        | proto::BridgeErrorCode::BridgeBootstrapFailed
        | proto::BridgeErrorCode::BridgeRpcTimeout => 4,
        proto::BridgeErrorCode::RuntimeFailure => 5,
        proto::BridgeErrorCode::NotImplemented
        | proto::BridgeErrorCode::InvalidFixture
        | proto::BridgeErrorCode::InvalidAction
        | proto::BridgeErrorCode::InvalidQueryFilter
        | proto::BridgeErrorCode::PresentationGeometryUnavailable
        | proto::BridgeErrorCode::BridgeNotAttached => 3,
        proto::BridgeErrorCode::Unspecified => 5,
    }
}

pub fn bridge_error_payload(error: &proto::BridgeError) -> Value {
    let details = error
        .details
        .iter()
        .map(|detail| {
            let mut value = json!({
                "field": detail.field,
                "value": detail.value,
                "note": detail.note,
                "actionReasonCode": error_action_failure_reason_code_name(enum_value(detail.action_reason_code)),
                "screen": empty_string_as_null(&detail.screen),
                "playerId": empty_string_as_null(&detail.player_id),
                "perspective": empty_string_as_null(&detail.perspective),
                "checkedHookPaths": detail.checked_hook_paths,
                "fieldDiagnostics": detail.field_diagnostics.iter().map(field_diagnostic_json).collect::<Vec<_>>(),
                "requestedPlayerId": empty_string_as_null(&detail.requested_player_id),
                "resolvedOwnerPlayerId": empty_string_as_null(&detail.resolved_owner_player_id),
                "localPlayerId": empty_string_as_null(&detail.local_player_id),
                "hostPlayerId": empty_string_as_null(&detail.host_player_id),
                "localRole": multiplayer_role_name(enum_value(detail.local_role)),
                "action": empty_string_as_null(&detail.action),
                "remoteOrchestration": detail.remote_orchestration.as_ref().map(remote_orchestration_json),
            });
            if let (Value::Object(object), Some(diagnostic)) = (&mut value, &detail.diagnostic) {
                object.insert("diagnostic".to_string(), protobuf_struct_json(diagnostic));
            }

            value
        })
        .collect::<Vec<_>>();

    json!({
        "error": {
            "code": bridge_error_code_name(
                proto::BridgeErrorCode::try_from(error.code)
                    .ok()
                    .unwrap_or(proto::BridgeErrorCode::RuntimeFailure)
            ),
            "message": error.message,
            "details": details,
            "actionFailure": error.action_failure.as_ref().map(action_failure_detail_json)
        }
    })
}

fn protobuf_struct_json(value: &prost_types::Struct) -> Value {
    Value::Object(
        value
            .fields
            .iter()
            .map(|(key, value)| (key.clone(), protobuf_value_json(value)))
            .collect(),
    )
}

fn protobuf_value_json(value: &prost_types::Value) -> Value {
    match value.kind.as_ref() {
        Some(prost_types::value::Kind::NullValue(_)) | None => Value::Null,
        Some(prost_types::value::Kind::NumberValue(number)) => json!(number),
        Some(prost_types::value::Kind::StringValue(text)) => Value::String(text.clone()),
        Some(prost_types::value::Kind::BoolValue(flag)) => Value::Bool(*flag),
        Some(prost_types::value::Kind::StructValue(value)) => protobuf_struct_json(value),
        Some(prost_types::value::Kind::ListValue(list)) => {
            Value::Array(list.values.iter().map(protobuf_value_json).collect())
        }
    }
}

fn action_failure_detail_json(failure: &proto::ActionFailureDetail) -> Value {
    json!({
        "reasonCode": error_action_failure_reason_code_name(enum_value(failure.reason_code)),
        "screen": empty_string_as_null(&failure.screen),
        "playerId": empty_string_as_null(&failure.player_id),
        "perspective": empty_string_as_null(&failure.perspective),
        "checkedHookPaths": failure.checked_hook_paths,
        "fieldDiagnostics": failure.field_diagnostics.iter().map(field_diagnostic_json).collect::<Vec<_>>(),
        "requestedPlayerId": empty_string_as_null(&failure.requested_player_id),
        "resolvedOwnerPlayerId": empty_string_as_null(&failure.resolved_owner_player_id),
        "localPlayerId": empty_string_as_null(&failure.local_player_id),
        "hostPlayerId": empty_string_as_null(&failure.host_player_id),
        "localRole": multiplayer_role_name(enum_value(failure.local_role)),
        "action": empty_string_as_null(&failure.action),
        "remoteOrchestration": failure.remote_orchestration.as_ref().map(remote_orchestration_json)
    })
}

fn field_diagnostic_json(diagnostic: &proto::FieldDiagnostic) -> Value {
    json!({
        "field": diagnostic.field,
        "value": diagnostic.value,
        "note": diagnostic.note
    })
}

pub fn handshake_json(response: &proto::HandshakeResponse) -> Value {
    json!({
        "schemaVersion": response.schema_version,
        "gameVersion": response.game_version,
        "bridgeVersion": response.bridge_version,
        "buildIdentity": response.build_identity.as_ref().map(bridge_build_identity_json),
        "transportKind": transport_kind_name(enum_value(response.transport_kind)),
        "attachmentState": attachment_state_name(enum_value(response.attachment_state)),
        "source": data_source_name(enum_value(response.source)),
        "provisional": response.provisional,
        "defaultPerspective": response.default_perspective.as_ref().map(perspective_json),
        "capabilities": response.capabilities.iter().map(capability_json).collect::<Vec<_>>(),
        "supportedActions": response.supported_actions.iter().map(action_descriptor_json).collect::<Vec<_>>()
    })
}

fn bridge_build_identity_json(identity: &proto::BridgeBuildIdentity) -> Value {
    json!({
        "bridgeSemVer": identity.bridge_semver,
        "bridgeVersion": identity.bridge_version,
        "assemblyInformationalVersion": identity.assembly_informational_version,
        "builtAtUtc": identity.built_at_utc,
        // The game build this payload was compiled for. Null means the payload
        // cannot claim one, not that it matches anything: a released payload
        // compiles against the declaration-only reference SDK, which has no
        // release_info.json and no game hash, so only the lane is ever set
        // there. A bridge predating these fields reports all three as null.
        "sts2ApiLane": empty_string_as_null(&identity.sts2_api_lane),
        "builtAgainstGameVersion": empty_string_as_null(&identity.built_against_game_version),
        "builtAgainstMainAssemblyHash": empty_string_as_null(&identity.built_against_main_assembly_hash)
    })
}

pub fn logs_json(response: &proto::LogsResponse) -> Value {
    json!({
        "source": data_source_name(enum_value(response.source)),
        "provisional": response.provisional,
        "nextCursor": response.next_cursor,
        "entries": response.entries.iter().map(log_entry_json).collect::<Vec<_>>()
    })
}

pub fn console_command_json(response: &proto::ConsoleCommandResponse) -> Value {
    json!({
        "requestId": response.request_id,
        "command": response.command,
        "args": response.args,
        "line": response.line,
        "accepted": response.accepted,
        "success": response.success,
        "output": response.output,
        "outputLines": response.output_lines,
        "source": data_source_name(enum_value(response.source)),
        "provisional": response.provisional,
        "notices": response.notices.iter().map(console_command_notice_json).collect::<Vec<_>>(),
    })
}

fn console_command_notice_json(notice: &proto::ConsoleCommandNotice) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
        "provisional": notice.provisional,
    })
}

pub fn hot_reload_status_json(response: &proto::HotReloadStatusResponse) -> Value {
    json!({
        "shell": response.status.as_ref().map(hot_reload_shell_status_json),
        "notices": response.notices.iter().map(hot_reload_notice_json).collect::<Vec<_>>(),
    })
}

pub fn hot_reload_response_json(response: &proto::HotReloadResponse) -> Value {
    json!({
        "requestId": response.request_id,
        "accepted": response.accepted,
        "shell": response.status.as_ref().map(hot_reload_shell_status_json),
        "reload": response.report.as_ref().map(hot_reload_report_json),
        "notices": response.notices.iter().map(hot_reload_notice_json).collect::<Vec<_>>(),
    })
}

pub fn inspect_actions_json(handshake: &proto::HandshakeResponse) -> Value {
    json!({
        "source": "bridge",
        "provisional": handshake.provisional,
        "stateContract": inspect_actions_state_contract_json(),
        "screen": Value::Null,
        "resolvedPerspective": Value::Null,
        "notices": [],
        "supportedActions": handshake.supported_actions.iter().map(action_descriptor_json).collect::<Vec<_>>(),
        // Live, resolved available actions now come from `sts2 --json state actions`.
        "availableActions": []
    })
}

pub fn inspect_actions_static_json() -> Value {
    let supported_actions = match StubBridgeService::new(MockScenario::MainMenu)
        .handshake(proto::HandshakeRequest::default())
    {
        proto::HandshakeResult {
            result: Some(proto::handshake_result::Result::Success(response)),
        } => response
            .supported_actions
            .iter()
            .map(action_descriptor_json)
            .collect::<Vec<_>>(),
        _ => Vec::new(),
    };

    json!({
        "source": "cli",
        "provisional": true,
        "stateContract": inspect_actions_state_contract_json(),
        "screen": Value::Null,
        "resolvedPerspective": Value::Null,
        "notices": [],
        "supportedActions": supported_actions,
        "availableActions": []
    })
}

pub fn inspect_actions_unavailable_json(error_payload: Value) -> Value {
    let mut payload = inspect_actions_static_json();
    payload["notices"] = json!([
        {
            "code": "bridge_unavailable",
            "message": "Live bridge state and currently available actions could not be queried; static inspect metadata is still returned.",
            "severity": "warning",
            "source": "cli",
            "path": "availableActions",
            "stability": "stable",
            "perspective": Value::Null,
            "details": error_payload
        }
    ]);
    payload
}

pub fn inspect_actions_environment_blocked_json(environment_payload: Value) -> Value {
    let mut payload = inspect_actions_static_json();
    payload["notices"] = json!([
        {
            "code": "environment_blocked",
            "message": "Live bridge state and currently available actions could not be queried because the configured IPC endpoint is unavailable; static inspect metadata is still returned.",
            "severity": "warning",
            "source": "cli",
            "path": "availableActions",
            "stability": "stable",
            "perspective": Value::Null,
            "details": environment_payload
        }
    ]);
    payload["environment"] = environment_payload;
    payload
}

fn inspect_actions_state_contract_json() -> Value {
    json!({
        "preferredReadSurface": "typed-state-sections",
        "typedNonCombatSections": [
            "map",
            "eventRoom",
            "treasureRoom",
            "relicSelection",
            "restSite",
            "shop",
            "rewards",
            "cardSelection",
            "simpleCardSelection",
            "deckCardSelection",
            "bundleSelection",
            "lobby"
        ],
        "typedOverlaySections": [
            "cardOverlay"
        ],
        "typedNonCombatSectionDetails": {
            "eventRoom": "Typed event-room state. eventRoom.page exposes the currently visible localized rich event title/body/shared label with LocString provenance when available; ancient event banner/epithet/dialogue live under eventRoom.page.ancient, and eventRoom.options[] exposes visible option labels and descriptions."
        },
        "typedOverlaySectionDetails": {
            "cardOverlay": "Typed partial card detail overlay state. Prefer this section before compatibility choices/actions; it may include visible card/detail data, preview text, source-screen breadcrumbs, overlayPolicy, validated close/back compatibility metadata, fallback visible controls, and structured notices for partial, passive, unsupported, or ambiguous ownership cases."
        },
        "overlayPolicy": {
            "blocking": "Blocking overlays take precedence in default state and preserve underlying screen breadcrumbs when observable.",
            "passive": "Passive overlays keep the underlying screen authoritative while exposing additive typed overlay detail and a notice.",
            "unsupported": "Unsupported or partial named overlays emit structured notices instead of exposing raw scene-tree internals or fabricating choices."
        },
        "fallbackChoosePolicy": "Use typed overlay state and preferredAction metadata first. Generic choose is retained only for currently executable compatibility controls or unmodeled visible overlay controls; close/back actions are advertised only when the bridge can validate a legal hook.",
        "compatibilityMetadata": [
            "choiceKind",
            "intentKind",
            "ownerPlayerId",
            "perspective",
            "preferredAction",
            "hostPlayerId",
            "localRole",
            "remoteOrchestration"
        ],
        "perspectiveInputs": {
            "state": ["--perspective local", "--perspective omniscient", "--player-id <stable-player-id>"],
            "act": ["--player-id <stable-player-id> on semantic action commands"],
            "rules": [
                "Single-player callers can omit perspective and playerId; the bridge resolves the local default.",
                "playerId selects the requested owner/perspective, but action legality remains separate from remote-client orchestration capability.",
                "Wrong-owner actions are rejected with actionFailure.reasonCode=wrong_player and ownership details."
            ]
        },
        "remoteOrchestrationStates": [
            "unavailable",
            "local-only-degraded",
            "host-local-seat",
            "host-mediated",
            "configured-client",
            "unsupported"
        ],
        "ownershipFailureReasonCodes": [
            "wrong_player",
            "unsupported_perspective"
        ],
        "noticeFields": [
            "path",
            "severity",
            "source",
            "stability",
            "perspective"
        ],
        "runtimeInternals": "Raw runtime scene internals are excluded from default state; use dev scene tree, dev scene node, or dev scene children for developer scene diagnostics. dev scene node --properties may include text-bearing label fields such as Text/raw text and rich-text enablement when available, but those fields remain outside default observable state."
    })
}

pub fn transport_kind_name(value: proto::TransportKind) -> &'static str {
    match value {
        proto::TransportKind::Mock => "mock",
        proto::TransportKind::Ipc => "ipc",
        proto::TransportKind::Tcp => "tcp",
        proto::TransportKind::Embedded => "embedded",
        proto::TransportKind::Unspecified => "unspecified",
    }
}

pub fn attachment_state_name(value: proto::AttachmentState) -> &'static str {
    match value {
        proto::AttachmentState::Stubbed => "stubbed",
        proto::AttachmentState::Detached => "detached",
        proto::AttachmentState::Attached => "attached",
        proto::AttachmentState::Unspecified => "unspecified",
    }
}

pub fn data_source_name(value: proto::DataSource) -> &'static str {
    match value {
        proto::DataSource::Stub => "stub",
        proto::DataSource::Live => "live",
        proto::DataSource::Unspecified => "unspecified",
    }
}

pub fn action_kind_name(value: proto::ActionKind) -> &'static str {
    match value {
        proto::ActionKind::PlayCard => "play-card",
        proto::ActionKind::UsePotion => "use-potion",
        proto::ActionKind::Choose => "choose",
        proto::ActionKind::ConfirmSelection => "confirm-selection",
        proto::ActionKind::CancelSelection => "cancel-selection",
        proto::ActionKind::MouseClick => "mouse-click",
        proto::ActionKind::SelectMapNode => "select-map-node",
        proto::ActionKind::EndTurn => "end-turn",
        proto::ActionKind::CancelEndTurn => "cancel-end-turn",
        proto::ActionKind::Ready => "ready",
        proto::ActionKind::Unready => "unready",
        proto::ActionKind::SelectCharacter => "select-character",
        proto::ActionKind::ClaimReward => "claim-reward",
        proto::ActionKind::SkipRewards => "skip-rewards",
        proto::ActionKind::SelectCard => "select-card",
        proto::ActionKind::SkipCardSelection => "skip-card-selection",
        proto::ActionKind::SelectBundle => "select-bundle",
        proto::ActionKind::BuyCard => "buy-card",
        proto::ActionKind::BuyRelic => "buy-relic",
        proto::ActionKind::BuyPotion => "buy-potion",
        proto::ActionKind::RemoveCard => "remove-card",
        proto::ActionKind::LeaveShop => "leave-shop",
        proto::ActionKind::CloseShopInventory => "close-shop-inventory",
        proto::ActionKind::Rest => "rest",
        proto::ActionKind::Smith => "smith",
        proto::ActionKind::UseRestSiteOption => "use-rest-site-option",
        proto::ActionKind::ProceedRestSite => "proceed-rest-site",
        proto::ActionKind::OpenChest => "open-chest",
        proto::ActionKind::TakeRelic => "take-relic",
        proto::ActionKind::ProceedTreasureRoom => "proceed-treasure-room",
        proto::ActionKind::BackFromMap => "back-from-map",
        proto::ActionKind::SelectEventOption => "select-event-option",
        proto::ActionKind::OpenEventShop => "open-event-shop",
        proto::ActionKind::UseCrystalSphereControl => "use-crystal-sphere-control",
        proto::ActionKind::ProceedEvent => "proceed-event",
        proto::ActionKind::JoinLobbyPlayer => "join-lobby-player",
        proto::ActionKind::LeaveLobbyPlayer => "leave-lobby-player",
        proto::ActionKind::ToggleMap => "toggle-map",
        proto::ActionKind::ToggleDeck => "toggle-deck",
        proto::ActionKind::ToggleSettings => "toggle-settings",
        proto::ActionKind::SortDeckView => "sort-deck-view",
        proto::ActionKind::ToggleDeckViewUpgrades => "toggle-deck-view-upgrades",
        proto::ActionKind::OpenPotionPopup => "open-potion-popup",
        proto::ActionKind::StartPotionTargeting => "start-potion-targeting",
        proto::ActionKind::SelectTarget => "select-target",
        proto::ActionKind::DiscardPotion => "discard-potion",
        proto::ActionKind::ViewDrawPile => "view-draw-pile",
        proto::ActionKind::ViewDiscardPile => "view-discard-pile",
        proto::ActionKind::ViewExhaustPile => "view-exhaust-pile",
        proto::ActionKind::InspectRelic => "inspect-relic",
        proto::ActionKind::CloseInspectRelic => "close-inspect-relic",
        proto::ActionKind::SelectHandCard => "select-hand-card",
        proto::ActionKind::DeselectHandCard => "deselect-hand-card",
        proto::ActionKind::ConfirmHandSelection => "confirm-hand-selection",
        proto::ActionKind::DrawMapStroke => "draw-map-stroke",
        proto::ActionKind::ClearMapDrawings => "clear-map-drawings",
        proto::ActionKind::Heal => "heal",
        proto::ActionKind::Unspecified => "unspecified",
    }
}

pub fn perspective_scope_name(value: proto::PerspectiveScope) -> &'static str {
    match value {
        proto::PerspectiveScope::Local => "local",
        proto::PerspectiveScope::Omniscient => "omniscient",
        proto::PerspectiveScope::Unspecified => "unspecified",
    }
}

pub fn multiplayer_role_name(value: proto::MultiplayerRole) -> &'static str {
    match value {
        proto::MultiplayerRole::Local => "local",
        proto::MultiplayerRole::Host => "host",
        proto::MultiplayerRole::Remote => "remote",
        proto::MultiplayerRole::HostLocalSeat => "host-local-seat",
        proto::MultiplayerRole::Unspecified => "unspecified",
    }
}

pub fn asset_artifact_kind_name(value: proto::AssetArtifactKind) -> &'static str {
    match value {
        proto::AssetArtifactKind::Raster => "raster",
        proto::AssetArtifactKind::Timeline => "timeline",
        proto::AssetArtifactKind::Metadata => "metadata",
        proto::AssetArtifactKind::Font => "font",
        proto::AssetArtifactKind::Unspecified => "unspecified",
    }
}

pub fn remote_client_orchestration_state_name(
    value: proto::RemoteClientOrchestrationState,
) -> &'static str {
    match value {
        proto::RemoteClientOrchestrationState::Unavailable => "unavailable",
        proto::RemoteClientOrchestrationState::LocalOnlyDegraded => "local-only-degraded",
        proto::RemoteClientOrchestrationState::HostMediated => "host-mediated",
        proto::RemoteClientOrchestrationState::ConfiguredClient => "configured-client",
        proto::RemoteClientOrchestrationState::Unsupported => "unsupported",
        proto::RemoteClientOrchestrationState::HostLocalSeat => "host-local-seat",
        proto::RemoteClientOrchestrationState::Unspecified => "unspecified",
    }
}

pub fn bridge_error_code_name(value: proto::BridgeErrorCode) -> &'static str {
    match value {
        proto::BridgeErrorCode::NotImplemented => "not_implemented",
        proto::BridgeErrorCode::InvalidFixture => "invalid_fixture",
        proto::BridgeErrorCode::InvalidAction => "invalid_action",
        proto::BridgeErrorCode::InvalidQueryFilter => "invalid_query_filter",
        proto::BridgeErrorCode::TransportUnavailable => "transport_unavailable",
        proto::BridgeErrorCode::TransportMisconfigured => "transport_misconfigured",
        proto::BridgeErrorCode::TransportConnectionFailed => "transport_connection_failed",
        proto::BridgeErrorCode::IpcSocketMissing => "ipc_socket_missing",
        proto::BridgeErrorCode::IpcConnectionFailed => "ipc_connection_failed",
        proto::BridgeErrorCode::BridgeBootstrapFailed => "bridge_bootstrap_failed",
        proto::BridgeErrorCode::BridgeRpcTimeout => "bridge_rpc_timeout",
        proto::BridgeErrorCode::PresentationGeometryUnavailable => {
            "presentation_geometry_unavailable"
        }
        proto::BridgeErrorCode::BridgeNotAttached => "bridge_not_attached",
        proto::BridgeErrorCode::RuntimeFailure => "runtime_failure",
        proto::BridgeErrorCode::Unspecified => "runtime_failure",
    }
}

pub(super) fn recent_bootstrap_failure_error(socket_path: &str) -> Option<proto::BridgeError> {
    let (log_path, log_line) = recent_bootstrap_failure()?;
    Some(error(
        proto::BridgeErrorCode::BridgeBootstrapFailed,
        "Recent STS2 Godot logs show the spirectl bridge failed during bootstrap.",
        &[
            detail(
                "endpoint",
                socket_path,
                "The live bridge never became reachable at the configured local endpoint.",
            ),
            detail(
                "logPath",
                &log_path.display().to_string(),
                "Latest Godot log file containing a recent [spirectl] bootstrap failure.",
            ),
            detail(
                "logLine",
                &log_line,
                "Recent [spirectl] failure line observed while probing live IPC startup.",
            ),
        ],
    ))
}

fn recent_bootstrap_failure() -> Option<(PathBuf, String)> {
    recent_godot_log_line(parse_bootstrap_failure_line)
}

#[derive(Debug, Clone)]
pub(crate) struct RecentRelevantLog {
    pub(crate) log_path: PathBuf,
    pub(crate) matched_line: Option<String>,
}

pub(crate) fn latest_recent_relevant_log() -> Option<RecentRelevantLog> {
    for path in candidate_log_files() {
        if !log_is_recent(&path) {
            continue;
        }

        let matched_line = fs::read_to_string(&path).ok().and_then(|contents| {
            contents
                .lines()
                .rev()
                .find_map(parse_relevant_spirectl_log_line)
                .map(str::to_string)
        });

        return Some(RecentRelevantLog {
            log_path: path,
            matched_line,
        });
    }

    None
}

#[derive(Debug, Clone, Default)]
pub(crate) struct Sts2LogCursor {
    file_lengths: BTreeMap<PathBuf, u64>,
}

pub(crate) fn capture_sts2_log_cursor() -> Sts2LogCursor {
    capture_sts2_log_cursor_from_files(sts2_log_files())
}

pub(crate) fn capture_relevant_log_cursor() -> Sts2LogCursor {
    let mut files = godot_log_files();
    files.extend(sts2_log_files());
    files.sort();
    files.dedup();
    capture_sts2_log_cursor_from_files(files)
}

pub(crate) fn steam_initialization_failure_since(
    cursor: &Sts2LogCursor,
) -> Option<(PathBuf, String)> {
    steam_initialization_failure_since_in_files(cursor, sts2_log_files())
}

#[derive(Debug, Clone)]
pub(crate) struct RecentLogTailEntry {
    pub(crate) log_path: PathBuf,
    pub(crate) line: String,
}

#[derive(Debug, Clone)]
pub(crate) struct RecentLogTail {
    pub(crate) entries: Vec<RecentLogTailEntry>,
    pub(crate) truncated: bool,
    pub(crate) max_lines: usize,
    pub(crate) max_bytes: usize,
}

pub(crate) fn recent_relevant_log_tail_since(
    cursor: &Sts2LogCursor,
    max_lines: usize,
    max_bytes: usize,
) -> RecentLogTail {
    let mut files = godot_log_files();
    files.extend(sts2_log_files());
    files.sort_by(|left, right| {
        file_modified(left)
            .cmp(&file_modified(right))
            .then_with(|| left.cmp(right))
    });
    files.dedup();

    let mut entries = Vec::new();
    for path in files {
        let Ok(contents) = fs::read(&path) else {
            continue;
        };
        let start = cursor
            .file_lengths
            .get(&path)
            .copied()
            .filter(|length| *length <= contents.len() as u64)
            .unwrap_or(0) as usize;
        if start >= contents.len() {
            continue;
        }

        let appended = String::from_utf8_lossy(&contents[start..]);
        entries.extend(appended.lines().map(|line| RecentLogTailEntry {
            log_path: path.clone(),
            line: line.to_string(),
        }));
    }

    let original_len = entries.len();
    if entries.len() > max_lines {
        entries = entries[entries.len() - max_lines..].to_vec();
    }

    let mut bytes = 0usize;
    let mut kept = Vec::new();
    for entry in entries.into_iter().rev() {
        let entry_bytes = entry.line.len() + entry.log_path.display().to_string().len();
        if !kept.is_empty() && bytes.saturating_add(entry_bytes) > max_bytes {
            break;
        }

        bytes = bytes.saturating_add(entry_bytes);
        kept.push(entry);
    }
    kept.reverse();
    let kept_len = kept.len();

    RecentLogTail {
        entries: kept,
        truncated: original_len > kept_len,
        max_lines,
        max_bytes,
    }
}

fn capture_sts2_log_cursor_from_files(files: Vec<PathBuf>) -> Sts2LogCursor {
    let file_lengths = files
        .into_iter()
        .filter_map(|path| {
            let length = fs::metadata(&path).ok()?.len();
            Some((path, length))
        })
        .collect();
    Sts2LogCursor { file_lengths }
}

fn steam_initialization_failure_since_in_files(
    cursor: &Sts2LogCursor,
    files: Vec<PathBuf>,
) -> Option<(PathBuf, String)> {
    for path in files {
        let Ok(contents) = fs::read(&path) else {
            continue;
        };
        let start = cursor
            .file_lengths
            .get(&path)
            .copied()
            .filter(|length| *length <= contents.len() as u64)
            .unwrap_or(0) as usize;
        if start >= contents.len() {
            continue;
        }

        let appended = String::from_utf8_lossy(&contents[start..]);
        if let Some(line) = appended
            .lines()
            .rev()
            .find_map(parse_steam_initialization_failure_line)
        {
            return Some((path, line.to_string()));
        }
    }

    None
}

/// Every log file worth reading for a recent `[spirectl]` line, newest first.
///
/// BOTH FAMILIES, and that is the point. The engine writes
/// `<data-root>/godot/app_userdata/<project>/logs` before the game's custom user
/// dir takes over, and the game then writes `<data-root>/SlayTheSpire2/logs` —
/// and the bridge's own bootstrap failure lands in the SECOND. Scanning only the
/// engine family silently loses it, which is how a bridge that said exactly why
/// it failed still surfaced as a bare missing socket.
fn candidate_log_files() -> Vec<PathBuf> {
    let mut files = godot_log_files();
    files.extend(sts2_log_files());
    files.sort_by(|left, right| {
        file_modified(right)
            .cmp(&file_modified(left))
            .then_with(|| left.cmp(right))
    });
    files.dedup();
    files
}

fn recent_godot_log_line(
    parse_line: for<'a> fn(&'a str) -> Option<&'a str>,
) -> Option<(PathBuf, String)> {
    for path in candidate_log_files() {
        if !log_is_recent(&path) {
            continue;
        }

        let Ok(contents) = fs::read_to_string(&path) else {
            continue;
        };
        if let Some(line) = contents.lines().rev().find_map(parse_line) {
            return Some((path, line.to_string()));
        }
    }

    None
}

fn sts2_log_files() -> Vec<PathBuf> {
    let extra = super::live_log_data_root();
    log_files(crate::host_paths::sts2_log_dirs(extra.as_deref()))
}

fn godot_log_files() -> Vec<PathBuf> {
    let extra = super::live_log_data_root();
    log_files(crate::host_paths::godot_log_dirs(extra.as_deref()))
}

fn log_files(log_dirs: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut entries = log_dirs
        .into_iter()
        .filter_map(|logs_dir| fs::read_dir(logs_dir).ok())
        .flat_map(|entries| entries.filter_map(|entry| entry.ok()))
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_file()
                && path.extension().is_some_and(|ext| ext == "log")
                && path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.starts_with("godot"))
        })
        .collect::<Vec<_>>();
    entries.sort_by(|left, right| {
        file_modified(right)
            .cmp(&file_modified(left))
            .then_with(|| left.cmp(right))
    });
    entries
}

fn file_modified(path: &Path) -> Option<SystemTime> {
    fs::metadata(path).ok()?.modified().ok()
}

fn log_is_recent(path: &Path) -> bool {
    file_modified(path)
        .and_then(|modified| SystemTime::now().duration_since(modified).ok())
        .is_some_and(|age| age <= BOOTSTRAP_LOG_MAX_AGE)
}

fn parse_bootstrap_failure_line(line: &str) -> Option<&str> {
    let trimmed = line.trim();
    if !trimmed.contains("[spirectl]") {
        return None;
    }

    if trimmed.contains("bootstrap loader failed:")
        || trimmed.contains("Live IPC bridge host failed:")
        || trimmed.contains("Live bridge host failed:")
    {
        return Some(trimmed);
    }

    None
}

fn parse_relevant_spirectl_log_line(line: &str) -> Option<&str> {
    let trimmed = line.trim();
    if trimmed.contains("[spirectl]") {
        Some(trimmed)
    } else {
        None
    }
}

fn parse_steam_initialization_failure_line(line: &str) -> Option<&str> {
    let trimmed = line.trim();
    if trimmed.contains("Steamworks initialization failed!")
        && trimmed.contains("k_ESteamAPIInitResult_NoSteamClient")
    {
        return Some(trimmed);
    }

    None
}

#[cfg(test)]
mod bridge_error_payload_tests {
    use super::*;
    use prost_types::{ListValue, Struct, Value as ProtoValue, value::Kind};

    #[test]
    fn bridge_error_payload_preserves_structured_detail_diagnostic() {
        let error = proto::BridgeError {
            code: proto::BridgeErrorCode::RuntimeFailure as i32,
            message: "Asset extraction failed.".to_string(),
            details: vec![proto::ErrorDetail {
                field: "scene".to_string(),
                value: "composed://encounters/kaiser_crab_boss/background/image".to_string(),
                note: "transparent render".to_string(),
                diagnostic: Some(Struct {
                    fields: [
                        (
                            "requestId".to_string(),
                            ProtoValue {
                                kind: Some(Kind::StringValue("request-1".to_string())),
                            },
                        ),
                        (
                            "readyRan".to_string(),
                            ProtoValue {
                                kind: Some(Kind::BoolValue(true)),
                            },
                        ),
                        (
                            "alpha".to_string(),
                            ProtoValue {
                                kind: Some(Kind::StructValue(Struct {
                                    fields: [(
                                        "rgbNonZeroBeforeAlphaNormalization".to_string(),
                                        ProtoValue {
                                            kind: Some(Kind::BoolValue(true)),
                                        },
                                    )]
                                    .into_iter()
                                    .collect(),
                                })),
                            },
                        ),
                        (
                            "hiddenPartIds".to_string(),
                            ProtoValue {
                                kind: Some(Kind::ListValue(ListValue {
                                    values: vec![ProtoValue {
                                        kind: Some(Kind::StringValue("rocket".to_string())),
                                    }],
                                })),
                            },
                        ),
                    ]
                    .into_iter()
                    .collect(),
                }),
                ..Default::default()
            }],
            action_failure: None,
        };

        let payload = bridge_error_payload(&error);

        assert_eq!(
            payload["error"]["details"][0]["diagnostic"]["requestId"],
            "request-1"
        );
        assert_eq!(
            payload["error"]["details"][0]["diagnostic"]["readyRan"],
            true
        );
        assert_eq!(
            payload["error"]["details"][0]["diagnostic"]["alpha"]["rgbNonZeroBeforeAlphaNormalization"],
            true
        );
        assert_eq!(
            payload["error"]["details"][0]["diagnostic"]["hiddenPartIds"][0],
            "rocket"
        );
    }

    #[test]
    fn bridge_error_payload_omits_absent_detail_diagnostic() {
        let error = proto::BridgeError {
            code: proto::BridgeErrorCode::RuntimeFailure as i32,
            message: "Asset extraction failed.".to_string(),
            details: vec![proto::ErrorDetail {
                field: "scene".to_string(),
                value: "composed://encounters/kaiser_crab_boss/background/image".to_string(),
                note: "transparent render".to_string(),
                ..Default::default()
            }],
            action_failure: None,
        };

        let payload = bridge_error_payload(&error);

        assert!(
            payload["error"]["details"][0]
                .as_object()
                .is_some_and(|detail| !detail.contains_key("diagnostic"))
        );
    }
}

#[cfg(all(test, target_os = "linux"))]
mod live_ipc_socket_discovery_tests {
    use super::*;

    fn owner(pid: &str, command: &str) -> UnixSocketOwner {
        UnixSocketOwner {
            pid: pid.to_string(),
            command: command.to_string(),
        }
    }

    #[test]
    fn discovers_matching_socket_name_and_ranks_game_owned_bridge_like_candidates_first() {
        let home = std::env::var("HOME").unwrap_or_else(|_| "/tmp/home".to_string());
        let repo_socket = format!("{home}/repo/spirectl/.sts2/ipc/custom-live.sock");
        let configured_socket = format!("{home}/repo/spirectl/.sts2/ipc/configured.sock");
        let configured_path = format!("{home}/.config/custom-live.sock");
        let proc_net_unix = format!(
            "\
Num       RefCount Protocol Flags    Type St Inode Path
00000000: 00000002 00000000 00010000 0001 01 11111 /tmp/spirectl-bridge.sock
00000000: 00000002 00000000 00010000 0001 01 22222 /tmp/custom-live.sock
00000000: 00000002 00000000 00010000 0001 01 33333 /tmp/unrelated.sock
00000000: 00000002 00000000 00010000 0001 01 44444 {repo_socket}
00000000: 00000002 00000000 00010000 0001 01 55555 {configured_socket}
"
        );
        let owners_by_inode = BTreeMap::from([
            ("11111".to_string(), vec![owner("101", "helper")]),
            ("22222".to_string(), vec![owner("202", "SlayTheSpire2")]),
            ("44444".to_string(), vec![owner("303", "SlayTheSpire2")]),
        ]);

        let candidates =
            discover_live_ipc_socket_candidates(&proc_net_unix, &configured_path, &owners_by_inode);

        assert_eq!(candidates.len(), 3);
        assert_eq!(candidates[0].path, repo_socket);
        assert_eq!(candidates[0].owners, vec![owner("303", "SlayTheSpire2")]);
        assert_eq!(candidates[1].path, "/tmp/custom-live.sock");
        assert_eq!(candidates[1].owners, vec![owner("202", "SlayTheSpire2")]);
        assert_eq!(candidates[2].path, "/tmp/spirectl-bridge.sock");
    }

    #[test]
    fn parses_socket_inode_from_proc_fd_symlink_target() {
        assert_eq!(
            parse_proc_fd_socket_inode("socket:[123456]"),
            Some("123456")
        );
        assert_eq!(parse_proc_fd_socket_inode("/tmp/file"), None);
    }
}

#[cfg(test)]
mod log_cursor_tests {
    use super::*;

    const FAILURE_LINE: &str = "[ERROR] Steamworks initialization failed! Result: k_ESteamAPIInitResult_NoSteamClient, message: Cannot create IPC pipe to Steam client process.  Steam is probably not running.";

    fn write(path: &Path, contents: &str) {
        fs::write(path, contents).expect("write test log");
    }

    fn append(path: &Path, contents: &str) {
        use std::io::Write as _;
        let mut file = fs::OpenOptions::new()
            .append(true)
            .open(path)
            .expect("open test log");
        file.write_all(contents.as_bytes())
            .expect("append test log");
    }

    #[test]
    fn steam_cursor_ignores_existing_failure_lines() {
        let dir = tempfile::tempdir().expect("temp dir");
        let log = dir.path().join("godot.log");
        write(&log, &format!("{FAILURE_LINE}\n"));

        let cursor = capture_sts2_log_cursor_from_files(vec![log.clone()]);

        assert_eq!(
            steam_initialization_failure_since_in_files(&cursor, vec![log]),
            None
        );
    }

    #[test]
    fn steam_cursor_detects_appended_failure_lines() {
        let dir = tempfile::tempdir().expect("temp dir");
        let log = dir.path().join("godot.log");
        write(&log, "[INFO] existing launch log\n");
        let cursor = capture_sts2_log_cursor_from_files(vec![log.clone()]);

        append(&log, &format!("{FAILURE_LINE}\n"));

        assert_eq!(
            steam_initialization_failure_since_in_files(&cursor, vec![log.clone()]),
            Some((log, FAILURE_LINE.to_string()))
        );
    }

    #[test]
    fn steam_cursor_detects_new_log_files() {
        let dir = tempfile::tempdir().expect("temp dir");
        let existing = dir.path().join("godot.log");
        let created = dir.path().join("godot2026-05-04T10.08.09.log");
        write(&existing, "[INFO] existing launch log\n");
        let cursor = capture_sts2_log_cursor_from_files(vec![existing.clone()]);

        write(&created, &format!("{FAILURE_LINE}\n"));

        assert_eq!(
            steam_initialization_failure_since_in_files(&cursor, vec![created.clone(), existing]),
            Some((created, FAILURE_LINE.to_string()))
        );
    }

    #[test]
    fn steam_cursor_ignores_appended_non_failure_lines() {
        let dir = tempfile::tempdir().expect("temp dir");
        let log = dir.path().join("godot.log");
        write(&log, &format!("{FAILURE_LINE}\n"));
        let cursor = capture_sts2_log_cursor_from_files(vec![log.clone()]);

        append(&log, "[INFO] Steam is running: True\n");

        assert_eq!(
            steam_initialization_failure_since_in_files(&cursor, vec![log]),
            None
        );
    }
}
