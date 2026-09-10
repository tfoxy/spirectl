use super::{
    bridge_health_endpoint_json, bridge_health_next_commands, build_perspective_selector_values,
    enum_action_kind, handshake_request,
};
use crate::{
    ActSubcommand, ConsoleArgs, DevHealArgs, InspectActionsArgs, Mode, MouseButtonArg,
    MouseSubcommand, PerspectiveScopeArg, TestCommand, TestSubcommand, TransportKind,
    bridge_client, bridge_error_payload, console_command_json, default_ipc_path, default_pipe_name,
    inspect_actions_environment_blocked_json, inspect_actions_json, inspect_actions_static_json,
    inspect_actions_unavailable_json, live_bridge,
};
use crate::{AppContext, AppError, RenderedCommand, bridge, test_runner};
use serde_json::{Value, json};

pub(crate) fn handle_test(
    command: TestCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        TestSubcommand::Run(args) => Ok(test_runner::run(args, context)),
        TestSubcommand::Stress(args) => Ok(test_runner::stress(args, context)),
    }
}

// Parses a draw-map-stroke `--points "x,y;x,y;…"` string into content-space proto Points. Malformed
// segments are skipped; the bridge guards on (>=2 points) so a too-short/empty list still fails cleanly.
fn parse_stroke_points(raw: &str) -> Vec<bridge::proto::Point> {
    raw.split(';')
        .filter_map(|segment| {
            let mut parts = segment.split(',');
            let x = parts.next()?.trim().parse::<f64>().ok()?;
            let y = parts.next()?.trim().parse::<f64>().ok()?;
            Some(bridge::proto::Point { x, y })
        })
        .collect()
}

pub(crate) fn execute_inspect_actions_json(
    args: InspectActionsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    if args.offline {
        return Ok(inspect_actions_static_json());
    }

    let client = bridge_client(context);
    let handshake = match client.handshake(handshake_request(context)) {
        Ok(handshake) => handshake,
        Err(error) => {
            if let Some(payload) = inspect_actions_environment_payload(context, &error) {
                return Ok(inspect_actions_environment_blocked_json(payload));
            }
            return Ok(inspect_actions_unavailable_json(bridge_error_payload(
                &error,
            )));
        }
    };
    Ok(inspect_actions_json(&handshake))
}

pub(crate) fn inspect_actions_environment_payload(
    context: AppContext<'_>,
    error: &bridge::proto::BridgeError,
) -> Option<Value> {
    if context.config.transport.kind != TransportKind::Ipc {
        return None;
    }

    let bridge_code = bridge::bridge_error_code_name(
        bridge::proto::BridgeErrorCode::try_from(error.code)
            .ok()
            .unwrap_or(bridge::proto::BridgeErrorCode::RuntimeFailure),
    );
    let status = match bridge_code {
        "ipc_socket_missing" => "endpoint_missing",
        "ipc_connection_failed" | "transport_connection_failed" => "endpoint_refused_or_stale",
        "bridge_rpc_timeout" => "rpc_timeout",
        _ => return None,
    };

    let endpoint = inspect_actions_ipc_endpoint(context);
    let endpoint_probe = endpoint.classify_probe();
    let status = match (bridge_code, endpoint_probe.classification) {
        ("ipc_socket_missing", "permission-denied") => "endpoint_permission_denied",
        ("ipc_socket_missing", "ambiguous") => "endpoint_ambiguous",
        ("ipc_connection_failed" | "transport_connection_failed", "permission-denied") => {
            "endpoint_permission_denied"
        }
        ("ipc_connection_failed" | "transport_connection_failed", "ambiguous") => {
            "endpoint_ambiguous"
        }
        _ => status,
    };

    Some(json!({
        "code": "environment_blocked",
        "status": status,
        "message": error.message,
        "endpoint": bridge_health_endpoint_json(&endpoint, &endpoint_probe),
        "connection": {
            "status": "failed",
            "bridgeErrorCode": bridge_code,
            "details": error.details.iter().map(|detail| json!({
                "field": detail.field,
                "value": detail.value,
                "note": detail.note
            })).collect::<Vec<_>>()
        },
        "safeNextCommands": bridge_health_next_commands(status)
    }))
}

pub(crate) fn inspect_actions_ipc_endpoint(
    context: AppContext<'_>,
) -> live_bridge::LiveBridgeEndpoint {
    if cfg!(windows) {
        live_bridge::LiveBridgeEndpoint::NamedPipe(
            context
                .config
                .transport
                .pipe_name
                .clone()
                .unwrap_or_else(default_pipe_name),
        )
    } else {
        live_bridge::LiveBridgeEndpoint::UnixSocket(
            context
                .config
                .transport
                .ipc_path
                .clone()
                .unwrap_or_else(default_ipc_path),
        )
    }
}

pub(crate) fn execute_action_json(
    command: ActSubcommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let perspective = action_perspective_selector(&command);
    let request = match command {
        ActSubcommand::PlayCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-play-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::PlayCard(
                bridge::proto::PlayCardAction {
                    card_id: args.card,
                    target_id: args.target.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::Choose(args) => bridge::proto::ActionRequest {
            request_id: "cli-choose".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::Choose(
                bridge::proto::ChooseAction {
                    choice_id: args.choice,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ConfirmSelection(args) => bridge::proto::ActionRequest {
            request_id: "cli-confirm-selection".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ConfirmSelection(
                bridge::proto::ConfirmSelectionAction {
                    player_id: args.player_id.unwrap_or_default(),
                    card_ids: args.cards,
                    reward_id: args.reward.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::CancelSelection(_) => bridge::proto::ActionRequest {
            request_id: "cli-cancel-selection".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::CancelSelection(
                bridge::proto::CancelSelectionAction {},
            )),
            ..Default::default()
        },
        ActSubcommand::UsePotion(args) => bridge::proto::ActionRequest {
            request_id: "cli-use-potion".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::UsePotion(
                bridge::proto::UsePotionAction {
                    potion_id: args.potion,
                    target_id: args.target.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::OpenPotionPopup(args) => bridge::proto::ActionRequest {
            request_id: "cli-open-potion-popup".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::OpenPotionPopup(
                bridge::proto::OpenPotionPopupAction {
                    potion_id: args.potion,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::StartPotionTargeting(args) => bridge::proto::ActionRequest {
            request_id: "cli-start-potion-targeting".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::StartPotionTargeting(
                bridge::proto::StartPotionTargetingAction {
                    potion_id: args.potion,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SelectTarget(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-target".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectTarget(
                bridge::proto::SelectTargetAction {
                    target_id: args.target,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::DiscardPotion(args) => bridge::proto::ActionRequest {
            request_id: "cli-discard-potion".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::DiscardPotion(
                bridge::proto::DiscardPotionAction {
                    potion_id: args.potion,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::Mouse(mouse) => match mouse.command {
            MouseSubcommand::Click(args) => {
                ensure_mode(
                    context.mode,
                    Mode::Dangerous,
                    "act mouse click",
                    "act mouse click dispatches dangerous raw input and requires --mode dangerous.",
                )?;
                bridge::proto::ActionRequest {
                    request_id: "cli-mouse-click".to_string(),
                    perspective: None,
                    action: Some(bridge::proto::action_request::Action::MouseClick(
                        bridge::proto::MouseClickAction {
                            x: args.x,
                            y: args.y,
                            button: proto_mouse_button(args.button) as i32,
                        },
                    )),
                    ..Default::default()
                }
            }
        },
        ActSubcommand::SelectMapNode(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-map-node".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectMapNode(
                bridge::proto::SelectMapNodeAction { node_id: args.node },
            )),
            ..Default::default()
        },
        ActSubcommand::DrawMapStroke(args) => {
            let points = parse_stroke_points(&args.points);
            bridge::proto::ActionRequest {
                request_id: "cli-draw-map-stroke".to_string(),
                perspective,
                action: Some(bridge::proto::action_request::Action::DrawMapStroke(
                    bridge::proto::DrawMapStrokeAction {
                        player_id: args.player_id.unwrap_or_default(),
                        is_eraser: args.eraser,
                        points,
                    },
                )),
                ..Default::default()
            }
        }
        ActSubcommand::ClearMapDrawings(args) => bridge::proto::ActionRequest {
            request_id: "cli-clear-map-drawings".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ClearMapDrawings(
                bridge::proto::ClearMapDrawingsAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::EndTurn(_) => bridge::proto::ActionRequest {
            request_id: "cli-end-turn".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::EndTurn(
                bridge::proto::EndTurnAction {},
            )),
            ..Default::default()
        },
        ActSubcommand::CancelEndTurn(_) => bridge::proto::ActionRequest {
            request_id: "cli-cancel-end-turn".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::CancelEndTurn(
                bridge::proto::CancelEndTurnAction {},
            )),
            ..Default::default()
        },
        ActSubcommand::Ready(_) => bridge::proto::ActionRequest {
            request_id: "cli-ready".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::Ready(
                bridge::proto::ReadyAction {},
            )),
            ..Default::default()
        },
        ActSubcommand::Unready(_) => bridge::proto::ActionRequest {
            request_id: "cli-unready".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::Unready(
                bridge::proto::UnreadyAction {},
            )),
            ..Default::default()
        },
        ActSubcommand::SelectCharacter(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-character".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectCharacter(
                bridge::proto::SelectCharacterAction {
                    character_id: args.character,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::JoinLobbyPlayer(args) => bridge::proto::ActionRequest {
            request_id: "cli-join-lobby-player".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::JoinLobbyPlayer(
                bridge::proto::JoinLobbyPlayerAction {
                    display_name: args.display_name,
                },
            )),
            ..Default::default()
        },
        ActSubcommand::LeaveLobbyPlayer(args) => bridge::proto::ActionRequest {
            request_id: "cli-leave-lobby-player".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::LeaveLobbyPlayer(
                bridge::proto::LeaveLobbyPlayerAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ClaimReward(args) => bridge::proto::ActionRequest {
            request_id: "cli-claim-reward".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ClaimReward(
                bridge::proto::ClaimRewardAction {
                    reward_id: args.reward,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SkipRewards(args) => bridge::proto::ActionRequest {
            request_id: "cli-skip-rewards".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SkipRewards(
                bridge::proto::SkipRewardsAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SelectCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectCard(
                bridge::proto::SelectCardAction {
                    card_id: args.card,
                    player_id: args.player_id.unwrap_or_default(),
                    reward_id: args.reward.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SkipCardSelection(args) => bridge::proto::ActionRequest {
            request_id: "cli-skip-card-selection".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SkipCardSelection(
                bridge::proto::SkipCardSelectionAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SelectBundle(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-bundle".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectBundle(
                bridge::proto::SelectBundleAction {
                    bundle_id: args.bundle,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::BuyCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-buy-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::BuyCard(
                bridge::proto::BuyCardAction {
                    shop_item_id: args.shop_item,
                    player_id: args.player_id.unwrap_or_default(),
                    ..Default::default()
                },
            )),
            ..Default::default()
        },
        ActSubcommand::BuyRelic(args) => bridge::proto::ActionRequest {
            request_id: "cli-buy-relic".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::BuyRelic(
                bridge::proto::BuyRelicAction {
                    shop_item_id: args.shop_item,
                    player_id: args.player_id.unwrap_or_default(),
                    ..Default::default()
                },
            )),
            ..Default::default()
        },
        ActSubcommand::BuyPotion(args) => bridge::proto::ActionRequest {
            request_id: "cli-buy-potion".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::BuyPotion(
                bridge::proto::BuyPotionAction {
                    shop_item_id: args.shop_item,
                    player_id: args.player_id.unwrap_or_default(),
                    ..Default::default()
                },
            )),
            ..Default::default()
        },
        ActSubcommand::RemoveCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-remove-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::RemoveCard(
                bridge::proto::RemoveCardAction {
                    shop_item_id: args.shop_item,
                    player_id: args.player_id.unwrap_or_default(),
                    ..Default::default()
                },
            )),
            ..Default::default()
        },
        ActSubcommand::LeaveShop(args) => bridge::proto::ActionRequest {
            request_id: "cli-leave-shop".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::LeaveShop(
                bridge::proto::LeaveShopAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::CloseShopInventory(args) => bridge::proto::ActionRequest {
            request_id: "cli-close-shop-inventory".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::CloseShopInventory(
                bridge::proto::CloseShopInventoryAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::Rest(args) => bridge::proto::ActionRequest {
            request_id: "cli-rest".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::Rest(
                bridge::proto::RestAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::Smith(args) => bridge::proto::ActionRequest {
            request_id: "cli-smith".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::Smith(
                bridge::proto::SmithAction {
                    card_id: args.card.unwrap_or_default(),
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::UseRestSiteOption(args) => bridge::proto::ActionRequest {
            request_id: "cli-use-rest-site-option".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::UseRestSiteOption(
                bridge::proto::UseRestSiteOptionAction {
                    rest_option_id: args.option,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ProceedRestSite(args) => bridge::proto::ActionRequest {
            request_id: "cli-proceed-rest-site".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ProceedRestSite(
                bridge::proto::ProceedRestSiteAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::OpenChest(args) => bridge::proto::ActionRequest {
            request_id: "cli-open-chest".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::OpenChest(
                bridge::proto::OpenChestAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::TakeRelic(args) => bridge::proto::ActionRequest {
            request_id: "cli-take-relic".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::TakeRelic(
                bridge::proto::TakeRelicAction {
                    relic_id: args.relic,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ProceedTreasureRoom(args) => bridge::proto::ActionRequest {
            request_id: "cli-proceed-treasure-room".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ProceedTreasureRoom(
                bridge::proto::ProceedTreasureRoomAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::BackFromMap(args) => bridge::proto::ActionRequest {
            request_id: "cli-back-from-map".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::BackFromMap(
                bridge::proto::BackFromMapAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SelectEventOption(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-event-option".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectEventOption(
                bridge::proto::SelectEventOptionAction {
                    event_option_id: args.event_option,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::OpenEventShop(args) => bridge::proto::ActionRequest {
            request_id: "cli-open-event-shop".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::OpenEventShop(
                bridge::proto::OpenEventShopAction {
                    event_option_id: args.event_option,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::UseCrystalSphereControl(args) => bridge::proto::ActionRequest {
            request_id: "cli-use-crystal-sphere-control".to_string(),
            perspective,
            action: Some(
                bridge::proto::action_request::Action::UseCrystalSphereControl(
                    bridge::proto::UseCrystalSphereControlAction {
                        control_id: args.control,
                        player_id: args.player_id.unwrap_or_default(),
                        selected_tool: args.selected_tool.unwrap_or_default(),
                    },
                ),
            ),
            ..Default::default()
        },
        ActSubcommand::ProceedEvent(args) => bridge::proto::ActionRequest {
            request_id: "cli-proceed-event".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ProceedEvent(
                bridge::proto::ProceedEventAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ToggleMap(args) => bridge::proto::ActionRequest {
            request_id: "cli-toggle-map".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ToggleMap(
                bridge::proto::ToggleMapAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ToggleDeck(args) => bridge::proto::ActionRequest {
            request_id: "cli-toggle-deck".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ToggleDeck(
                bridge::proto::ToggleDeckAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ToggleSettings(args) => bridge::proto::ActionRequest {
            request_id: "cli-toggle-settings".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ToggleSettings(
                bridge::proto::ToggleSettingsAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SortDeckView(args) => bridge::proto::ActionRequest {
            request_id: "cli-sort-deck-view".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SortDeckView(
                bridge::proto::SortDeckViewAction {
                    by: args.by,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ToggleDeckViewUpgrades(args) => bridge::proto::ActionRequest {
            request_id: "cli-toggle-deck-view-upgrades".to_string(),
            perspective,
            action: Some(
                bridge::proto::action_request::Action::ToggleDeckViewUpgrades(
                    bridge::proto::ToggleDeckViewUpgradesAction {
                        player_id: args.player_id.unwrap_or_default(),
                    },
                ),
            ),
            ..Default::default()
        },
        ActSubcommand::ViewDrawPile(args) => bridge::proto::ActionRequest {
            request_id: "cli-view-draw-pile".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ViewDrawPile(
                bridge::proto::ViewDrawPileAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ViewDiscardPile(args) => bridge::proto::ActionRequest {
            request_id: "cli-view-discard-pile".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ViewDiscardPile(
                bridge::proto::ViewDiscardPileAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ViewExhaustPile(args) => bridge::proto::ActionRequest {
            request_id: "cli-view-exhaust-pile".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ViewExhaustPile(
                bridge::proto::ViewExhaustPileAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::InspectRelic(args) => bridge::proto::ActionRequest {
            request_id: "cli-inspect-relic".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::InspectRelic(
                bridge::proto::InspectRelicAction {
                    relic_id: args.relic,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::CloseInspectRelic(args) => bridge::proto::ActionRequest {
            request_id: "cli-close-inspect-relic".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::CloseInspectRelic(
                bridge::proto::CloseInspectRelicAction {
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::SelectHandCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-select-hand-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::SelectHandCard(
                bridge::proto::SelectHandCardAction {
                    card_id: args.card,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::DeselectHandCard(args) => bridge::proto::ActionRequest {
            request_id: "cli-deselect-hand-card".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::DeselectHandCard(
                bridge::proto::DeselectHandCardAction {
                    card_id: args.card,
                    player_id: args.player_id.unwrap_or_default(),
                },
            )),
            ..Default::default()
        },
        ActSubcommand::ConfirmHandSelection(args) => bridge::proto::ActionRequest {
            request_id: "cli-confirm-hand-selection".to_string(),
            perspective,
            action: Some(bridge::proto::action_request::Action::ConfirmHandSelection(
                bridge::proto::ConfirmHandSelectionAction {
                    player_id: args.player_id.unwrap_or_default(),
                    card_ids: args.cards,
                },
            )),
            ..Default::default()
        },
    };

    let response = client.execute_action(request).map_err(AppError::bridge)?;
    Ok(json!({
        "requestId": response.request_id,
        "actionInstanceId": response.action_instance_id,
        "kind": bridge::action_kind_name(enum_action_kind(response.kind)),
        "accepted": response.accepted,
        "provisional": response.provisional,
        "message": response.message
    }))
}

pub(crate) fn action_perspective_selector(
    command: &ActSubcommand,
) -> Option<bridge::proto::PerspectiveSelector> {
    action_player_id(command).and_then(|player_id| {
        if player_id.trim().is_empty() {
            None
        } else {
            build_perspective_selector_values(Some(PerspectiveScopeArg::Local), Some(player_id))
        }
    })
}

pub(crate) fn action_player_id(command: &ActSubcommand) -> Option<&str> {
    match command {
        ActSubcommand::PlayCard(args) => args.player_id.as_deref(),
        ActSubcommand::Choose(args) => args.player_id.as_deref(),
        ActSubcommand::ConfirmSelection(args) => args.player_id.as_deref(),
        ActSubcommand::CancelSelection(args) => args.player_id.as_deref(),
        ActSubcommand::Mouse(_) => None,
        ActSubcommand::UsePotion(args) => args.player_id.as_deref(),
        ActSubcommand::OpenPotionPopup(args) => args.player_id.as_deref(),
        ActSubcommand::StartPotionTargeting(args) => args.player_id.as_deref(),
        ActSubcommand::SelectTarget(args) => args.player_id.as_deref(),
        ActSubcommand::DiscardPotion(args) => args.player_id.as_deref(),
        ActSubcommand::SelectMapNode(args) => args.player_id.as_deref(),
        ActSubcommand::DrawMapStroke(args) => args.player_id.as_deref(),
        ActSubcommand::ClearMapDrawings(args) => args.player_id.as_deref(),
        ActSubcommand::EndTurn(args) => args.player_id.as_deref(),
        ActSubcommand::CancelEndTurn(args) => args.player_id.as_deref(),
        ActSubcommand::Ready(args) => args.player_id.as_deref(),
        ActSubcommand::Unready(args) => args.player_id.as_deref(),
        ActSubcommand::SelectCharacter(args) => args.player_id.as_deref(),
        ActSubcommand::JoinLobbyPlayer(_) => None,
        ActSubcommand::LeaveLobbyPlayer(args) => args.player_id.as_deref(),
        ActSubcommand::ClaimReward(args) => args.player_id.as_deref(),
        ActSubcommand::SkipRewards(args) => args.player_id.as_deref(),
        ActSubcommand::SelectCard(args) => args.player_id.as_deref(),
        ActSubcommand::SkipCardSelection(args) => args.player_id.as_deref(),
        ActSubcommand::SelectBundle(args) => args.player_id.as_deref(),
        ActSubcommand::BuyCard(args) => args.player_id.as_deref(),
        ActSubcommand::BuyRelic(args) => args.player_id.as_deref(),
        ActSubcommand::BuyPotion(args) => args.player_id.as_deref(),
        ActSubcommand::RemoveCard(args) => args.player_id.as_deref(),
        ActSubcommand::LeaveShop(args) => args.player_id.as_deref(),
        ActSubcommand::CloseShopInventory(args) => args.player_id.as_deref(),
        ActSubcommand::Rest(args) => args.player_id.as_deref(),
        ActSubcommand::Smith(args) => args.player_id.as_deref(),
        ActSubcommand::UseRestSiteOption(args) => args.player_id.as_deref(),
        ActSubcommand::ProceedRestSite(args) => args.player_id.as_deref(),
        ActSubcommand::OpenChest(args) => args.player_id.as_deref(),
        ActSubcommand::TakeRelic(args) => args.player_id.as_deref(),
        ActSubcommand::ProceedTreasureRoom(args) => args.player_id.as_deref(),
        ActSubcommand::BackFromMap(args) => args.player_id.as_deref(),
        ActSubcommand::SelectEventOption(args) => args.player_id.as_deref(),
        ActSubcommand::OpenEventShop(args) => args.player_id.as_deref(),
        ActSubcommand::UseCrystalSphereControl(args) => args.player_id.as_deref(),
        ActSubcommand::ProceedEvent(args) => args.player_id.as_deref(),
        ActSubcommand::ToggleMap(args) => args.player_id.as_deref(),
        ActSubcommand::ToggleDeck(args) => args.player_id.as_deref(),
        ActSubcommand::ToggleSettings(args) => args.player_id.as_deref(),
        ActSubcommand::SortDeckView(args) => args.player_id.as_deref(),
        ActSubcommand::ToggleDeckViewUpgrades(args) => args.player_id.as_deref(),
        ActSubcommand::ViewDrawPile(args) => args.player_id.as_deref(),
        ActSubcommand::ViewDiscardPile(args) => args.player_id.as_deref(),
        ActSubcommand::ViewExhaustPile(args) => args.player_id.as_deref(),
        ActSubcommand::InspectRelic(args) => args.player_id.as_deref(),
        ActSubcommand::CloseInspectRelic(args) => args.player_id.as_deref(),
        ActSubcommand::SelectHandCard(args) => args.player_id.as_deref(),
        ActSubcommand::DeselectHandCard(args) => args.player_id.as_deref(),
        ActSubcommand::ConfirmHandSelection(args) => args.player_id.as_deref(),
    }
}

const DANGEROUS_CONSOLE_COMMANDS: &[&str] = &["achievement", "cloud", "unlock"];

pub(crate) fn canonical_console_line(command: &str, args: &[String]) -> String {
    if args.is_empty() {
        command.to_string()
    } else {
        format!("{command} {}", args.join(" "))
    }
}

pub(crate) fn normalized_console_command(command: &str) -> String {
    command.trim().to_ascii_lowercase()
}

pub(crate) fn console_command_requires_dangerous(command: &str) -> bool {
    DANGEROUS_CONSOLE_COMMANDS.contains(&normalized_console_command(command).as_str())
}

pub(crate) fn ensure_console_mode(
    args: &ConsoleArgs,
    context: AppContext<'_>,
) -> Result<(), AppError> {
    let command = normalized_console_command(&args.command);
    if command.is_empty() {
        return Err(AppError::invalid_console_command());
    }

    if console_command_requires_dangerous(&args.command) {
        return ensure_mode(
            context.mode,
            Mode::Dangerous,
            &format!("dev console {command}"),
            &format!(
                "Console command '{command}' can mutate persisted files and requires --mode dangerous."
            ),
        );
    }

    Ok(())
}

pub(crate) fn execute_console_json(
    args: ConsoleArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    ensure_console_mode(&args, context)?;

    let command = args.command.trim().to_string();
    if command.is_empty() {
        return Err(AppError::invalid_console_command());
    }

    let line = canonical_console_line(&command, &args.args);
    let client = bridge_client(context);
    let response = client
        .execute_console_command(bridge::proto::ConsoleCommandRequest {
            request_id: "cli-dev-console".to_string(),
            command,
            args: args.args,
            line,
        })
        .map_err(AppError::bridge)?;

    if !response.success {
        return Err(AppError::console_command_rejected(&response));
    }

    Ok(console_command_json(&response))
}

// Devtool, not a legal player action: heals (or revives) any creature host-side through the
// game's CreatureCmd. Host-direct mutation can desync real remote
// network clients — acceptable under couch co-op's one-real-player norm.
pub(crate) fn execute_dev_heal_json(
    args: DevHealArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    if !args.full && args.amount.is_none() {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_heal_request",
                    "message": "dev heal requires --amount <hp> or --full.",
                }
            }),
        });
    }
    if let Some(amount) = args.amount {
        if amount <= 0 {
            return Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_heal_request",
                        "message": "dev heal --amount must be a positive HP amount.",
                    }
                }),
            });
        }
    }

    let client = bridge_client(context);
    let request = bridge::proto::ActionRequest {
        request_id: "cli-dev-heal".to_string(),
        perspective: None,
        action: Some(bridge::proto::action_request::Action::Heal(
            bridge::proto::HealAction {
                target_id: args.target.unwrap_or_default(),
                amount: args.amount.unwrap_or_default(),
                full: args.full,
            },
        )),
        ..Default::default()
    };

    let response = client.execute_action(request).map_err(AppError::bridge)?;
    Ok(json!({
        "requestId": response.request_id,
        "actionInstanceId": response.action_instance_id,
        "kind": bridge::action_kind_name(enum_action_kind(response.kind)),
        "accepted": response.accepted,
        "provisional": response.provisional,
        "message": response.message
    }))
}

pub(crate) fn ensure_mode(
    current_mode: Mode,
    required_mode: Mode,
    command: &str,
    message: &str,
) -> Result<(), AppError> {
    if current_mode == required_mode {
        Ok(())
    } else {
        Err(AppError::invalid_mode(
            command,
            current_mode,
            required_mode,
            message,
        ))
    }
}

pub(crate) fn proto_mouse_button(button: MouseButtonArg) -> bridge::proto::RawMouseButton {
    match button {
        MouseButtonArg::Left => bridge::proto::RawMouseButton::Left,
        MouseButtonArg::Right => bridge::proto::RawMouseButton::Right,
        MouseButtonArg::Middle => bridge::proto::RawMouseButton::Middle,
    }
}
