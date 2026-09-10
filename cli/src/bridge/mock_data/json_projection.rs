pub(crate) fn enum_value<T>(raw: i32) -> T
where
    T: TryFrom<i32>,
{
    T::try_from(raw)
        .ok()
        .unwrap_or_else(|| T::try_from(0).ok().expect("protobuf enum zero value"))
}

pub(super) fn capability_json(capability: &proto::Capability) -> Value {
    json!({
        "id": capability.id,
        "summary": capability.summary,
        "provisional": capability.provisional,
        "remoteOrchestration": capability.remote_orchestration.as_ref().map(remote_orchestration_json)
    })
}

pub(super) fn perspective_json(perspective: &proto::PerspectiveInfo) -> Value {
    json!({
        "scope": perspective_scope_name(enum_value(perspective.scope)),
        "playerId": if perspective.player_id.is_empty() {
            Value::Null
        } else {
            Value::String(perspective.player_id.clone())
        },
        "usesDefault": perspective.uses_default,
        "localRole": multiplayer_role_name(enum_value(perspective.local_role)),
        "localPlayerId": empty_string_as_null(&perspective.local_player_id),
        "hostPlayerId": empty_string_as_null(&perspective.host_player_id),
        "remoteOrchestration": perspective.remote_orchestration.as_ref().map(remote_orchestration_json)
    })
}

pub(super) fn remote_orchestration_json(
    capability: &proto::RemoteClientOrchestrationCapability,
) -> Value {
    json!({
        "id": empty_string_as_null(&capability.id),
        "state": remote_client_orchestration_state_name(enum_value(capability.state)),
        "summary": empty_string_as_null(&capability.summary),
        "provisional": capability.provisional
    })
}

pub(super) fn action_descriptor_json(action: &proto::ActionDescriptor) -> Value {
    json!({
        "id": action.id,
        "kind": action_kind_name(enum_value(action.kind)),
        "summary": action.summary,
        "cliCommandHint": action.cli_command_hint,
        "provisional": action.provisional,
        "status": action_status_name(enum_value(action.status)),
        "parameters": action.parameters.iter().map(action_parameter_json).collect::<Vec<_>>(),
        "kindDescriptor": action_kind_descriptor_json(action),
        "ownerPlayerId": Value::Null,
        "perspectiveBehavior": default_perspective_behavior_for_action(enum_value(action.kind)),
        "legalityStatus": "unknown",
        "checkedHookPaths": Vec::<String>::new(),
        "argumentSchema": action.parameters.iter().map(action_argument_schema_json_from_parameter).collect::<Vec<_>>(),
        "failureReasonCodes": default_failure_reason_codes_for_action(enum_value(action.kind))
    })
}

pub(super) fn empty_string_as_null(value: &str) -> Value {
    if value.is_empty() {
        Value::Null
    } else {
        Value::String(value.to_string())
    }
}

pub(super) fn log_entry_json(entry: &proto::LogEntry) -> Value {
    json!({
        "cursor": entry.cursor,
        "level": match enum_value(entry.level) {
            proto::LogLevel::Trace => "trace",
            proto::LogLevel::Debug => "debug",
            proto::LogLevel::Info => "info",
            proto::LogLevel::Warn => "warn",
            proto::LogLevel::Error => "error",
            proto::LogLevel::Unspecified => "unspecified",
        },
        "target": entry.target,
        "message": entry.message
    })
}

pub(super) fn hot_reload_shell_status_json(status: &proto::HotReloadShellStatus) -> Value {
    json!({
        "supported": status.supported,
        "protocol": status.protocol.as_ref().map(|protocol| json!({
            "id": protocol.id,
            "version": protocol.version,
        })),
        "shellModId": status.shell_mod_id,
        "shellProtocolVersion": status.shell_protocol_version,
        "activeGeneration": status.active_generation,
        "expectedLogicArtifactPath": status.expected_logic_artifact_path,
        "contractVersion": status.contract_version,
        "reloadInProgress": status.reload_in_progress,
        "lastReloadReport": status.last_reload_report.as_ref().map(hot_reload_report_json),
        "restartRequired": status.restart_required,
        "notices": status.notices.iter().map(hot_reload_notice_json).collect::<Vec<_>>(),
    })
}

pub(super) fn hot_reload_report_json(report: &proto::HotReloadReport) -> Value {
    json!({
        "status": report.status,
        "generation": report.generation,
        "requestedAt": report.requested_at,
        "sourceAssemblyPath": report.source_assembly_path,
        "shadowAssemblyPath": report.shadow_assembly_path,
        "contractVersion": report.contract_version,
        "logicAssemblyName": report.logic_assembly_name,
        "entryType": report.entry_type,
        "previousGeneration": report.previous_generation,
        "previousRemainsActive": report.previous_remains_active,
        "previousDisposed": report.previous_disposed,
        "previousUnloadRequested": report.previous_unload_requested,
        "previousCollected": report.previous_collected,
        "durationMs": report.duration_ms,
        "error": report.error.as_ref().map(hot_reload_error_json),
        "warnings": report.warnings.iter().map(hot_reload_warning_json).collect::<Vec<_>>(),
    })
}

pub(super) fn hot_reload_error_json(error: &proto::HotReloadError) -> Value {
    json!({
        "code": error.code,
        "phase": error.phase,
        "message": error.message,
        "exceptionType": error.exception_type,
        "exceptionMessage": error.exception_message,
        "restartRequired": error.restart_required,
    })
}

pub(super) fn hot_reload_warning_json(warning: &proto::HotReloadWarning) -> Value {
    json!({
        "code": warning.code,
        "phase": warning.phase,
        "message": warning.message,
    })
}

pub(super) fn hot_reload_notice_json(notice: &proto::HotReloadNotice) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
    })
}

pub(super) fn action_status_name(value: proto::ActionStatus) -> &'static str {
    match value {
        proto::ActionStatus::Implemented => "implemented",
        proto::ActionStatus::Scaffolded => "scaffolded",
        proto::ActionStatus::Unspecified => "unspecified",
    }
}

pub(super) fn error_action_failure_reason_code_name(
    value: proto::ErrorActionFailureReasonCode,
) -> &'static str {
    match value {
        proto::ErrorActionFailureReasonCode::NotImplemented => "not_implemented",
        proto::ErrorActionFailureReasonCode::InvalidAction => "invalid_action",
        proto::ErrorActionFailureReasonCode::BridgeNotAttached => "bridge_not_attached",
        proto::ErrorActionFailureReasonCode::RuntimeFailure => "runtime_failure",
        proto::ErrorActionFailureReasonCode::WrongScreen => "wrong_screen",
        proto::ErrorActionFailureReasonCode::WrongPlayer => "wrong_player",
        proto::ErrorActionFailureReasonCode::NotVisible => "not_visible",
        proto::ErrorActionFailureReasonCode::NotEnabled => "not_enabled",
        proto::ErrorActionFailureReasonCode::MissingHook => "missing_hook",
        proto::ErrorActionFailureReasonCode::AmbiguousHook => "ambiguous_hook",
        proto::ErrorActionFailureReasonCode::StaleId => "stale_id",
        proto::ErrorActionFailureReasonCode::UnsupportedPerspective => "unsupported_perspective",
        proto::ErrorActionFailureReasonCode::DangerousModeRequired => "dangerous_mode_required",
        proto::ErrorActionFailureReasonCode::Unspecified => "unspecified",
    }
}

pub(super) fn action_parameter_json(parameter: &proto::ActionParameter) -> Value {
    json!({
        "name": parameter.name,
        "valueType": parameter.value_type,
        "required": parameter.required,
        "summary": parameter.summary
    })
}

pub(super) fn action_argument_schema_json_from_parameter(
    parameter: &proto::ActionParameter,
) -> Value {
    json!({
        "name": normalize_action_argument_name(&parameter.name),
        "valueType": parameter.value_type,
        "required": parameter.required,
        "summary": parameter.summary,
        "allowedValues": Vec::<String>::new(),
        "stableId": parameter.value_type.contains("id")
    })
}

pub(super) fn normalize_action_argument_name(name: &str) -> String {
    match name {
        "card" | "card_id" => "cardId".to_string(),
        "target" | "target_id" => "targetId".to_string(),
        "choice" | "choice_id" => "choiceId".to_string(),
        "node" | "node_id" | "map_node_id" => "mapNodeId".to_string(),
        "potion" | "potion_id" => "potionId".to_string(),
        "character" | "character_id" => "characterId".to_string(),
        "player" | "player_id" => "playerId".to_string(),
        other => other.to_string(),
    }
}

pub(super) fn action_kind_descriptor_json(action: &proto::ActionDescriptor) -> Value {
    let kind = enum_value(action.kind);
    json!({
        "kind": action_kind_name(kind),
        "intentKind": action_kind_name(kind),
        "commandName": action_kind_name(kind),
        "summary": if kind == proto::ActionKind::Choose {
            "Fallback-only compatibility action for generic, modded, or unmodeled visible choices."
        } else {
            action.summary.as_str()
        },
        "fallback": kind == proto::ActionKind::Choose,
        "dangerous": kind == proto::ActionKind::MouseClick,
        "modes": if kind == proto::ActionKind::MouseClick {
            vec!["dangerous"]
        } else {
            vec!["normal"]
        },
        "screenTypes": screen_types_for_action(kind)
    })
}

pub(super) fn default_perspective_behavior_for_action(kind: proto::ActionKind) -> &'static str {
    match kind {
        proto::ActionKind::MouseClick => "dangerous-viewport",
        proto::ActionKind::Choose => "local-only",
        proto::ActionKind::Ready
        | proto::ActionKind::Unready
        | proto::ActionKind::SelectCharacter
        | proto::ActionKind::PlayCard
        | proto::ActionKind::UsePotion
        | proto::ActionKind::SelectMapNode
        | proto::ActionKind::EndTurn
        | proto::ActionKind::CancelEndTurn
        | proto::ActionKind::ConfirmSelection
        | proto::ActionKind::CancelSelection
        | proto::ActionKind::ClaimReward
        | proto::ActionKind::SkipRewards
        | proto::ActionKind::SelectCard
        | proto::ActionKind::SkipCardSelection
        | proto::ActionKind::SelectBundle
        | proto::ActionKind::BuyCard
        | proto::ActionKind::BuyRelic
        | proto::ActionKind::BuyPotion
        | proto::ActionKind::RemoveCard
        | proto::ActionKind::LeaveShop
        | proto::ActionKind::CloseShopInventory
        | proto::ActionKind::Rest
        | proto::ActionKind::Smith
        | proto::ActionKind::UseRestSiteOption
        | proto::ActionKind::ProceedRestSite
        | proto::ActionKind::OpenChest
        | proto::ActionKind::TakeRelic
        | proto::ActionKind::ProceedTreasureRoom
        | proto::ActionKind::BackFromMap
        | proto::ActionKind::SelectEventOption
        | proto::ActionKind::OpenEventShop
        | proto::ActionKind::UseCrystalSphereControl
        | proto::ActionKind::ProceedEvent
        | proto::ActionKind::JoinLobbyPlayer
        | proto::ActionKind::LeaveLobbyPlayer
        | proto::ActionKind::ToggleMap
        | proto::ActionKind::ToggleDeck
        | proto::ActionKind::ToggleSettings
        | proto::ActionKind::SortDeckView
        | proto::ActionKind::ToggleDeckViewUpgrades
        | proto::ActionKind::OpenPotionPopup
        | proto::ActionKind::StartPotionTargeting
        | proto::ActionKind::SelectTarget
        | proto::ActionKind::DiscardPotion
        | proto::ActionKind::ViewDrawPile
        | proto::ActionKind::ViewDiscardPile
        | proto::ActionKind::ViewExhaustPile
        | proto::ActionKind::InspectRelic
        | proto::ActionKind::CloseInspectRelic
        | proto::ActionKind::SelectHandCard
        | proto::ActionKind::DeselectHandCard
        | proto::ActionKind::ConfirmHandSelection
        | proto::ActionKind::DrawMapStroke
        | proto::ActionKind::ClearMapDrawings => "owner-only",
        // Dev-only host-side heal: never advertised as an available action; the host mutates any
        // creature directly regardless of seat ownership.
        proto::ActionKind::Heal => "owner-only",
        proto::ActionKind::Unspecified => "unspecified",
    }
}

pub(super) fn default_failure_reason_codes_for_action(
    kind: proto::ActionKind,
) -> Vec<&'static str> {
    if kind == proto::ActionKind::MouseClick {
        vec![
            "dangerous_mode_required",
            "invalid_action",
            "runtime_failure",
        ]
    } else {
        vec![
            "wrong_screen",
            "wrong_player",
            "not_visible",
            "not_enabled",
            "missing_hook",
            "ambiguous_hook",
            "stale_id",
            "unsupported_perspective",
            "invalid_action",
            "runtime_failure",
        ]
    }
}

pub(super) fn screen_types_for_action(kind: proto::ActionKind) -> Vec<&'static str> {
    match kind {
        proto::ActionKind::PlayCard
        | proto::ActionKind::UsePotion
        | proto::ActionKind::EndTurn
        | proto::ActionKind::CancelEndTurn
        | proto::ActionKind::ViewDrawPile
        | proto::ActionKind::ViewDiscardPile
        | proto::ActionKind::ViewExhaustPile
        | proto::ActionKind::SelectHandCard
        | proto::ActionKind::DeselectHandCard
        | proto::ActionKind::ConfirmHandSelection => {
            vec!["combat"]
        }
        proto::ActionKind::SelectMapNode => vec![MAP_SCREEN_ID],
        proto::ActionKind::Ready
        | proto::ActionKind::Unready
        | proto::ActionKind::SelectCharacter
        | proto::ActionKind::JoinLobbyPlayer
        | proto::ActionKind::LeaveLobbyPlayer => {
            vec![START_RUN_LOBBY_SCREEN_ID, LOAD_RUN_LOBBY_SCREEN_ID]
        }
        proto::ActionKind::ConfirmSelection | proto::ActionKind::CancelSelection => {
            vec![
                SIMPLE_CARD_SELECTION_SCREEN_ID,
                DECK_CARD_SELECTION_SCREEN_ID,
                BUNDLE_SELECTION_SCREEN_ID,
            ]
        }
        proto::ActionKind::ClaimReward | proto::ActionKind::SkipRewards => vec![REWARDS_SCREEN_ID],
        proto::ActionKind::SelectCard | proto::ActionKind::SkipCardSelection => vec![
            CARD_REWARD_SELECTION_SCREEN_ID,
            CHOOSE_CARD_SELECTION_SCREEN_ID,
            SIMPLE_CARD_SELECTION_SCREEN_ID,
            DECK_CARD_SELECTION_SCREEN_ID,
            "card-overlay",
        ],
        proto::ActionKind::SelectBundle => vec![BUNDLE_SELECTION_SCREEN_ID],
        proto::ActionKind::BuyCard
        | proto::ActionKind::BuyRelic
        | proto::ActionKind::BuyPotion
        | proto::ActionKind::RemoveCard
        | proto::ActionKind::LeaveShop
        | proto::ActionKind::CloseShopInventory => {
            vec![SHOP_SCREEN_ID, FAKE_MERCHANT_INVENTORY_SCREEN_ID]
        }
        proto::ActionKind::Rest
        | proto::ActionKind::Smith
        | proto::ActionKind::UseRestSiteOption
        | proto::ActionKind::ProceedRestSite => vec![REST_SITE_SCREEN_ID],
        proto::ActionKind::OpenChest
        | proto::ActionKind::TakeRelic
        | proto::ActionKind::ProceedTreasureRoom => vec![
            TREASURE_ROOM_SCREEN_ID,
            TREASURE_ROOM_RELIC_COLLECTION_SCREEN_ID,
            RELIC_SELECTION_SCREEN_ID,
        ],
        proto::ActionKind::BackFromMap
        | proto::ActionKind::DrawMapStroke
        | proto::ActionKind::ClearMapDrawings => vec![MAP_SCREEN_ID],
        proto::ActionKind::SelectEventOption | proto::ActionKind::OpenEventShop => {
            vec![EVENT_ROOM_SCREEN_ID]
        }
        proto::ActionKind::UseCrystalSphereControl => vec![CRYSTAL_SPHERE_SCREEN_ID],
        proto::ActionKind::ProceedEvent => vec![EVENT_ROOM_SCREEN_ID, CRYSTAL_SPHERE_SCREEN_ID],
        proto::ActionKind::ToggleMap
        | proto::ActionKind::ToggleDeck
        | proto::ActionKind::ToggleSettings
        | proto::ActionKind::SortDeckView
        | proto::ActionKind::ToggleDeckViewUpgrades
        | proto::ActionKind::OpenPotionPopup
        | proto::ActionKind::StartPotionTargeting
        | proto::ActionKind::SelectTarget
        | proto::ActionKind::DiscardPotion
        | proto::ActionKind::InspectRelic
        | proto::ActionKind::CloseInspectRelic => vec!["run"],
        proto::ActionKind::Choose => vec!["generic", "modded", "unmodeled-visible-choice"],
        proto::ActionKind::MouseClick => vec!["viewport"],
        // Dev-only host-side heal: works anywhere a run is active (in or out of combat).
        proto::ActionKind::Heal => vec!["run"],
        proto::ActionKind::Unspecified => Vec::new(),
    }
}
