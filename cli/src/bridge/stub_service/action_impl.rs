impl StubBridgeService {
    pub fn execute_action(&self, request: proto::ActionRequest) -> proto::ActionResult {
        let scenario = self.active_scenario();
        let result = match request.action {
            None => Err(error(
                proto::BridgeErrorCode::InvalidAction,
                "Action request did not include a supported action payload.",
                &[detail(
                    "action",
                    "",
                    "Provide play_card, use_potion, choose, select_map_node, end_turn, ready, unready, select_character, or mouse_click.",
                )],
            )),
            Some(proto::action_request::Action::PlayCard(play_card)) => {
                if play_card.card_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "play-card requires a stable card id.",
                        &[detail(
                            "card_id",
                            "",
                            "Provide the card id from state.combat.hand[].id.",
                        )],
                    ))
                } else if scenario != MockScenario::Combat {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "play-card is only available on the combat screen in the mock bridge.",
                        &[detail(
                            "card_id",
                            &play_card.card_id,
                            "Switch the mock scenario to combat or use state.availableActions from a live bridge.",
                        )],
                    ))
                } else if play_card.card_id == "c_1" {
                    if play_card.target_id != "e_1" {
                        Err(error(
                            proto::BridgeErrorCode::InvalidAction,
                            "play-card requires a legal target for the requested mock combat card.",
                            &[detail(
                                "target_id",
                                &play_card.target_id,
                                "Use target id e_1 for Jab in the combat mock scenario.",
                            )],
                        ))
                    } else {
                        Ok(action_response(
                            &request.request_id,
                            proto::ActionKind::PlayCard,
                            "Played mock card 'Jab' targeting 'Gnash Grub'.",
                        ))
                    }
                } else if play_card.card_id == "c_2" {
                    if play_card.target_id.is_empty() {
                        Ok(action_response(
                            &request.request_id,
                            proto::ActionKind::PlayCard,
                            "Played mock card 'Guard'.",
                        ))
                    } else {
                        Err(error(
                            proto::BridgeErrorCode::InvalidAction,
                            "play-card does not accept a target for the requested mock combat card.",
                            &[detail(
                                "target_id",
                                &play_card.target_id,
                                "Omit target_id when playing Guard in the combat mock scenario.",
                            )],
                        ))
                    }
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "play-card requires a stable card id from the current mock combat hand.",
                        &[detail(
                            "card_id",
                            &play_card.card_id,
                            "Use c_1 for Jab or c_2 for Guard in the combat mock scenario.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::UsePotion(use_potion)) => {
                if use_potion.potion_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-potion requires a stable potion id.",
                        &[detail(
                            "potion_id",
                            "",
                            "Provide the potion id from state.combat.potions[].id or availableActions[].arguments.potionId.",
                        )],
                    ))
                } else if scenario != MockScenario::Combat {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-potion is only available on the combat screen in the mock bridge.",
                        &[detail(
                            "potion_id",
                            &use_potion.potion_id,
                            "Switch the mock scenario to combat or use state.availableActions from a live bridge.",
                        )],
                    ))
                } else if use_potion.potion_id == "potion:p1:0:fire-potion" {
                    if use_potion.target_id != "e_1" {
                        Err(error(
                            proto::BridgeErrorCode::InvalidAction,
                            "use-potion requires a legal target for the requested mock combat potion.",
                            &[detail(
                                "target_id",
                                &use_potion.target_id,
                                "Use target id e_1 for Ember Draught in the combat mock scenario.",
                            )],
                        ))
                    } else {
                        Ok(action_response(
                            &request.request_id,
                            proto::ActionKind::UsePotion,
                            "Used mock potion 'Ember Draught' targeting 'Gnash Grub'.",
                        ))
                    }
                } else if use_potion.potion_id == "potion:p1:1:block-potion" {
                    if use_potion.target_id.is_empty() {
                        Ok(action_response(
                            &request.request_id,
                            proto::ActionKind::UsePotion,
                            "Used mock potion 'Block Potion'.",
                        ))
                    } else {
                        Err(error(
                            proto::BridgeErrorCode::InvalidAction,
                            "use-potion does not accept a target for the requested mock combat potion.",
                            &[detail(
                                "target_id",
                                &use_potion.target_id,
                                "Omit target_id when using Block Potion in the combat mock scenario.",
                            )],
                        ))
                    }
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-potion requires a stable potion id from the current mock combat belt.",
                        &[detail(
                            "potion_id",
                            &use_potion.potion_id,
                            "Use potion:p1:0:fire-potion or potion:p1:1:block-potion in the combat mock scenario.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::Choose(choose)) => {
                if choose.choice_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "choose requires a stable choice id.",
                        &[detail(
                            "choice_id",
                            "",
                            "Provide the choice id from state.choices[].id.",
                        )],
                    ))
                } else if scenario == MockScenario::MainMenu && choose.choice_id == "menu:start-run"
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        "Accepted visible choice 'menu:start-run'.",
                    ))
                } else if scenario == MockScenario::Map && choose.choice_id == "map:back" {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        "Accepted visible map choice 'map:back'.",
                    ))
                } else if scenario == MockScenario::Rewards
                    && matches!(choose.choice_id.as_str(), "reward:p1:0" | "reward:p1:1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!("Accepted visible reward choice '{}'.", choose.choice_id),
                    ))
                } else if scenario == MockScenario::EventRoom
                    && matches!(
                        choose.choice_id.as_str(),
                        "event-room:gain-gold:0" | "event-room:leave:1"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!("Accepted visible event-room choice '{}'.", choose.choice_id),
                    ))
                } else if scenario == MockScenario::TreasureRoom
                    && matches!(
                        choose.choice_id.as_str(),
                        "treasure-room:open-chest"
                            | "treasure-room:relic:anchor:0"
                            | "treasure-room:relic:bag-of-marbles:1"
                            | "treasure-room:proceed"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!(
                            "Accepted visible treasure-room choice '{}'.",
                            choose.choice_id
                        ),
                    ))
                } else if scenario == MockScenario::Rewards
                    && matches!(
                        choose.choice_id.as_str(),
                        "reward:p1:0" | "reward-flow:skip"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!("Accepted visible reward choice '{}'.", choose.choice_id),
                    ))
                } else if scenario == MockScenario::Shop
                    && matches!(
                        choose.choice_id.as_str(),
                        "shop:p1:card:strike:0"
                            | "shop:p1:relic:anchor:0"
                            | "shop:p1:potion:fire-potion:0"
                            | "shop:p1:card-removal:0"
                            | "shop:leave"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!("Accepted visible shop choice '{}'.", choose.choice_id),
                    ))
                } else if scenario == MockScenario::FakeMerchantPreOpen
                    && choose.choice_id == "event-room:fake-merchant:open-shop"
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        "Accepted visible Fake Merchant shop-entry choice 'event-room:fake-merchant:open-shop'.",
                    ))
                } else if scenario == MockScenario::CrystalSphere
                    && matches!(
                        choose.choice_id.as_str(),
                        "crystal-sphere:tool:big"
                            | "crystal-sphere:tool:small"
                            | "crystal-sphere:cell:0:0"
                            | "crystal-sphere:cell:1:0"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!(
                            "Accepted visible Crystal Sphere choice '{}'.",
                            choose.choice_id
                        ),
                    ))
                } else if scenario == MockScenario::CrystalSphereFinished
                    && choose.choice_id == "crystal-sphere:proceed"
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        "Accepted visible Crystal Sphere choice 'crystal-sphere:proceed'.",
                    ))
                } else if scenario == MockScenario::RestSite
                    && matches!(
                        choose.choice_id.as_str(),
                        "rest-site:p1:heal:0" | "rest-site:p1:smith:1" | "rest-site:proceed"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!("Accepted visible rest-site choice '{}'.", choose.choice_id),
                    ))
                } else if scenario == MockScenario::CardSelection
                    && matches!(
                        choose.choice_id.as_str(),
                        "card-selection:card:bash:0" | "card-selection:skip"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!(
                            "Accepted visible card-selection choice '{}'.",
                            choose.choice_id
                        ),
                    ))
                } else if scenario == MockScenario::BundleSelection
                    && matches!(
                        choose.choice_id.as_str(),
                        "card-selection:bundle:offensive-pack:0"
                            | "card-selection:bundle:defensive-pack:1"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Choose,
                        &format!(
                            "Accepted visible bundle-selection choice '{}'.",
                            choose.choice_id
                        ),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "choose requires a visible choice id from the current screen.",
                        &[detail(
                            "choice_id",
                            &choose.choice_id,
                            "Provide a stable choice id from state.choices[].id on the active screen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ConfirmSelection(_)) => {
                if scenario == MockScenario::CardSelection {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::ConfirmSelection,
                        "Confirmed the staged selection.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "confirm-selection requires a staged selection on the current screen.",
                        &[detail(
                            "selection",
                            "",
                            "Retry when availableActions includes confirm-selection on the active screen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::CancelSelection(_)) => {
                if scenario == MockScenario::CardSelection {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::CancelSelection,
                        "Cancelled the staged selection.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "cancel-selection requires a staged selection on the current screen.",
                        &[detail(
                            "selection",
                            "",
                            "Retry when availableActions includes cancel-selection on the active screen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::Rest(rest)) => {
                if scenario == MockScenario::RestSite
                    && (rest.player_id.is_empty() || rest.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Rest,
                        "Rested at the mock rest site.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "rest is only available on the rest-site screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &rest.player_id,
                            "Retry when state.screen.id is rest-site and availableActions includes rest.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::Smith(smith)) => {
                if scenario == MockScenario::RestSite
                    && (smith.player_id.is_empty() || smith.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Smith,
                        "Opened mock smithing from the rest site.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "smith is only available on the rest-site screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &smith.player_id,
                            "Retry when state.screen.id is rest-site and availableActions includes smith.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::UseRestSiteOption(option)) => {
                if option.rest_option_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-rest-site-option requires a stable rest option id.",
                        &[detail(
                            "rest_option_id",
                            "",
                            "Use a rest option id from availableActions[].arguments.values.restOptionId.",
                        )],
                    ))
                } else if scenario == MockScenario::RestSite
                    && option.rest_option_id == "heal"
                    && (option.player_id.is_empty() || option.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::UseRestSiteOption,
                        "Used mock rest-site option 'heal'.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-rest-site-option requires a visible rest-site option in the mock bridge.",
                        &[detail(
                            "rest_option_id",
                            &option.rest_option_id,
                            "Retry with a visible rest-site option id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ProceedRestSite(proceed)) => {
                if scenario == MockScenario::RestSite
                    && (proceed.player_id.is_empty() || proceed.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::ProceedRestSite,
                        "Proceeded from the mock rest site.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "proceed-rest-site is only available on the rest-site screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &proceed.player_id,
                            "Retry when state.screen.id is rest-site and availableActions includes proceed-rest-site.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::OpenChest(open_chest)) => {
                if scenario == MockScenario::TreasureRoom
                    && (open_chest.player_id.is_empty() || open_chest.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::OpenChest,
                        "Opened the mock treasure chest.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "open-chest is only available on the treasure-room screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &open_chest.player_id,
                            "Retry when state.screen.id is treasure-room and availableActions includes open-chest.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::TakeRelic(take_relic)) => {
                if take_relic.relic_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "take-relic requires a stable relic id.",
                        &[detail(
                            "relic_id",
                            "",
                            "Use a relic id from availableActions[].arguments.values.relicId.",
                        )],
                    ))
                } else if scenario == MockScenario::TreasureRoom
                    && matches!(take_relic.relic_id.as_str(), "anchor" | "bag-of-marbles")
                    && (take_relic.player_id.is_empty() || take_relic.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::TakeRelic,
                        &format!("Took mock relic '{}'.", take_relic.relic_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "take-relic requires a visible treasure-room relic in the mock bridge.",
                        &[detail(
                            "relic_id",
                            &take_relic.relic_id,
                            "Retry with a visible relic id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ProceedTreasureRoom(proceed)) => {
                if scenario == MockScenario::TreasureRoom
                    && (proceed.player_id.is_empty() || proceed.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::ProceedTreasureRoom,
                        "Proceeded from the mock treasure room.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "proceed-treasure-room is only available on the treasure-room screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &proceed.player_id,
                            "Retry when state.screen.id is treasure-room and availableActions includes proceed-treasure-room.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SelectMapNode(select_map_node)) => {
                if select_map_node.node_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-map-node requires a stable map node id.",
                        &[detail(
                            "node_id",
                            "",
                            "Provide the map node id from state.choices[].id or availableActions[].arguments.mapNodeId.",
                        )],
                    ))
                } else if scenario != MockScenario::Map {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-map-node is only available on the map screen in the mock bridge.",
                        &[detail(
                            "node_id",
                            &select_map_node.node_id,
                            "Retry when state.screen.id is map and availableActions includes select-map-node.",
                        )],
                    ))
                } else if select_map_node.node_id == "map-node:3:1" {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectMapNode,
                        "Selected map node 'Monster (3,1)'.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-map-node requires an executable map node id from the current screen.",
                        &[detail(
                            "node_id",
                            &select_map_node.node_id,
                            "Retry with a travelable map node id from availableActions[].arguments.mapNodeId.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::BackFromMap(back_from_map)) => {
                if scenario == MockScenario::Map
                    && (back_from_map.player_id.is_empty() || back_from_map.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::BackFromMap,
                        "Went back from the mock map.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "back-from-map is only available on the map screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &back_from_map.player_id,
                            "Retry when state.screen.id is map and availableActions includes back-from-map.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::MouseClick(mouse_click)) => {
                if mouse_click.x < 0 || mouse_click.y < 0 {
                    let field = if mouse_click.x < 0 { "x" } else { "y" };
                    let value = if mouse_click.x < 0 {
                        mouse_click.x.to_string()
                    } else {
                        mouse_click.y.to_string()
                    };
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "mouse-click requires non-negative viewport coordinates.",
                        &[detail(
                            field,
                            &value,
                            "Use screenshot/viewport pixel coordinates at or above zero.",
                        )],
                    ))
                } else {
                    let button = mouse_button_name(mouse_click.button);
                    Ok(action_response_with_provisional(
                        &request.request_id,
                        proto::ActionKind::MouseClick,
                        &format!(
                            "Injected mock raw {button} click at ({},{}).",
                            mouse_click.x, mouse_click.y
                        ),
                        true,
                    ))
                }
            }
            Some(proto::action_request::Action::EndTurn(_)) => {
                if scenario == MockScenario::Combat {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::EndTurn,
                        "Ended the active combat turn.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "end-turn is only available during combat.",
                        &[detail(
                            "action",
                            "end-turn",
                            "Retry when state.screen.id is combat and availableActions includes end-turn.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::CancelEndTurn(_)) => {
                if scenario == MockScenario::Combat {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::CancelEndTurn,
                        "Cancelled the pending end-turn for the active combat player.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "cancel-end-turn is only available during combat.",
                        &[detail(
                            "action",
                            "cancel-end-turn",
                            "Retry when state.screen.id is combat and the player has ended their turn.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::Ready(_)) => {
                if scenario == MockScenario::Lobby
                    && requested_action_player_id(&request)
                        .as_deref()
                        .is_some_and(|player_id| player_id == "p:300")
                {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "ready cannot execute for a true remote lobby player from this local bridge.",
                        &[unsupported_perspective_detail(
                            "ready",
                            requested_action_player_id(&request)
                                .as_deref()
                                .unwrap_or_default(),
                            "p:300",
                            START_RUN_LOBBY_SCREEN_ID,
                        )],
                    ))
                } else if scenario == MockScenario::Lobby
                    && requested_action_player_id(&request)
                        .as_deref()
                        .is_some_and(|player_id| player_id != "p:100")
                {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "ready cannot execute for a non-local lobby player.",
                        &[wrong_player_detail(
                            "ready",
                            requested_action_player_id(&request)
                                .as_deref()
                                .unwrap_or_default(),
                            "p:100",
                            START_RUN_LOBBY_SCREEN_ID,
                        )],
                    ))
                } else if scenario == MockScenario::Lobby {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Ready,
                        "Marked the local lobby player ready.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "ready is only available when the local lobby player can ready up.",
                        &[detail(
                            "action",
                            "ready",
                            "Retry when state.screen.id is NCharacterSelectScreen and availableActions includes ready.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::Unready(_)) => {
                if scenario == MockScenario::Lobby
                    && requested_action_player_id(&request)
                        .as_deref()
                        .is_some_and(|player_id| {
                            player_id == "p:100"
                                && MOCK_LOADED_HOST_LOCAL_SEATS
                                    .with(|loaded| loaded.borrow().iter().any(|id| id == "p:200"))
                        })
                {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unready cannot execute for a player that does not own the visible host-local lobby action.",
                        &[wrong_player_detail(
                            "unready",
                            requested_action_player_id(&request)
                                .as_deref()
                                .unwrap_or_default(),
                            "p:200",
                            START_RUN_LOBBY_SCREEN_ID,
                        )],
                    ))
                } else if scenario == MockScenario::Lobby
                    && requested_action_player_id(&request)
                        .as_deref()
                        .is_some_and(mock_is_host_local_seat)
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Unready,
                        "Marked the host-local lobby seat unready.",
                    ))
                } else if scenario == MockScenario::LobbyReady
                    && requested_action_player_id(&request)
                        .as_deref()
                        .is_some_and(|player_id| player_id != "p:100")
                {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unready cannot execute for a non-local lobby player.",
                        &[wrong_player_detail(
                            "unready",
                            requested_action_player_id(&request)
                                .as_deref()
                                .unwrap_or_default(),
                            "p:100",
                            START_RUN_LOBBY_SCREEN_ID,
                        )],
                    ))
                } else if scenario == MockScenario::LobbyReady {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Unready,
                        "Marked the local lobby player unready.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unready is only available when the local lobby player is already ready.",
                        &[detail(
                            "action",
                            "unready",
                            "Retry when state.screen.id is NCharacterSelectScreen and availableActions includes unready.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SelectCharacter(select_character)) => {
                if select_character.character_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-character requires a stable character id.",
                        &[detail(
                            "character_id",
                            "",
                            "Provide the character id from state.lobby.availableCharacters[].id or availableActions[].arguments.characterId.",
                        )],
                    ))
                } else if scenario == MockScenario::Lobby
                    && select_character.character_id == "silent"
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectCharacter,
                        "Selected lobby character 'silent'.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-character requires an executable lobby character id from the current screen.",
                        &[detail(
                            "character_id",
                            &select_character.character_id,
                            "Retry with a stable lobby character id from availableActions[].arguments.characterId.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::JoinLobbyPlayer(join)) => {
                if join.display_name.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "join-lobby-player requires a display name.",
                        &[detail(
                            "display_name",
                            "",
                            "Provide the browser player's trimmed display name.",
                        )],
                    ))
                } else if scenario == MockScenario::Lobby {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::JoinLobbyPlayer,
                        "Created a mock host-local lobby player.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "join-lobby-player is only available in the multiplayer lobby.",
                        &[detail(
                            "action",
                            "join-lobby-player",
                            "Retry when state.screen.id is NCharacterSelectScreen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::LeaveLobbyPlayer(leave)) => {
                if leave.player_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "leave-lobby-player requires a player id.",
                        &[detail(
                            "player_id",
                            "",
                            "Provide a synthetic host-local lobby player id.",
                        )],
                    ))
                } else if scenario == MockScenario::Lobby {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::LeaveLobbyPlayer,
                        "Removed a mock host-local lobby player.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "leave-lobby-player is only available in the multiplayer lobby.",
                        &[detail(
                            "action",
                            "leave-lobby-player",
                            "Retry when state.screen.id is NCharacterSelectScreen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ClaimReward(claim)) => {
                if claim.reward_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "claim-reward requires a stable reward id.",
                        &[detail(
                            "reward_id",
                            "",
                            "Use rewardId from state.rewards.rewards[].id.",
                        )],
                    ))
                } else if scenario == MockScenario::Rewards
                    && matches!(claim.reward_id.as_str(), "reward:p1:0" | "reward:p1:1")
                    && (claim.player_id.is_empty() || claim.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::ClaimReward,
                        &format!("Claimed mock reward '{}'.", claim.reward_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "claim-reward requires a visible reward on the rewards screen in the mock bridge.",
                        &[detail(
                            "reward_id",
                            &claim.reward_id,
                            "Retry with a visible reward id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SkipRewards(skip)) => {
                if scenario == MockScenario::Rewards
                    && (skip.player_id.is_empty() || skip.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SkipRewards,
                        "Skipped mock rewards.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "skip-rewards is only available on the rewards screen in the mock bridge.",
                        &[detail(
                            "player_id",
                            &skip.player_id,
                            "Retry on the rewards screen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SelectCard(select)) => {
                if select.card_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-card requires a stable card id.",
                        &[detail(
                            "card_id",
                            "",
                            "Use a card id from the visible card-selection state.",
                        )],
                    ))
                } else if matches!(
                    scenario,
                    MockScenario::CardSelection
                        | MockScenario::SimpleCardSelection
                        | MockScenario::DeckCardSelection
                ) && matches!(
                    select.card_id.as_str(),
                    "card-selection:card:bash:0" | "c_1" | "c_2"
                ) {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectCard,
                        &format!("Selected mock card '{}'.", select.card_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-card requires a visible card-selection card in the mock bridge.",
                        &[detail(
                            "card_id",
                            &select.card_id,
                            "Retry with a visible card id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SkipCardSelection(skip)) => {
                if scenario == MockScenario::CardSelection
                    && (skip.player_id.is_empty() || skip.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SkipCardSelection,
                        "Skipped mock card selection.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "skip-card-selection is only available when the card selection can be skipped.",
                        &[detail(
                            "player_id",
                            &skip.player_id,
                            "Retry on a skippable card-selection screen.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::SelectBundle(select)) => {
                if scenario == MockScenario::BundleSelection
                    && matches!(
                        select.bundle_id.as_str(),
                        "card-selection:bundle:offensive-pack:0"
                            | "card-selection:bundle:defensive-pack:1"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectBundle,
                        &format!("Selected mock bundle '{}'.", select.bundle_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-bundle requires a visible bundle on the bundle-selection screen in the mock bridge.",
                        &[detail(
                            "bundle_id",
                            &select.bundle_id,
                            "Retry with a visible bundle id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::BuyCard(buy_card)) => self.mock_shop_intent_action(
                &request.request_id,
                proto::ActionKind::BuyCard,
                &buy_card.shop_item_id,
                "shop:p1:card:strike:0",
                "buy-card",
            ),
            Some(proto::action_request::Action::BuyRelic(buy_relic)) => self
                .mock_shop_intent_action(
                    &request.request_id,
                    proto::ActionKind::BuyRelic,
                    &buy_relic.shop_item_id,
                    "shop:p1:relic:anchor:0",
                    "buy-relic",
                ),
            Some(proto::action_request::Action::BuyPotion(buy_potion)) => self
                .mock_shop_intent_action(
                    &request.request_id,
                    proto::ActionKind::BuyPotion,
                    &buy_potion.shop_item_id,
                    "shop:p1:potion:fire-potion:0",
                    "buy-potion",
                ),
            Some(proto::action_request::Action::RemoveCard(remove_card)) => self
                .mock_shop_intent_action(
                    &request.request_id,
                    proto::ActionKind::RemoveCard,
                    &remove_card.shop_item_id,
                    "shop:p1:card-removal:0",
                    "remove-card",
                ),
            Some(proto::action_request::Action::LeaveShop(_)) => self.mock_shop_intent_action(
                &request.request_id,
                proto::ActionKind::LeaveShop,
                "shop:leave",
                "shop:leave",
                "leave-shop",
            ),
            Some(proto::action_request::Action::CloseShopInventory(_)) => self
                .mock_shop_intent_action(
                    &request.request_id,
                    proto::ActionKind::CloseShopInventory,
                    "shop:close-inventory",
                    "shop:close-inventory",
                    "close-shop-inventory",
                ),
            Some(proto::action_request::Action::SelectEventOption(select)) => {
                if scenario == MockScenario::EventRoom
                    && matches!(
                        select.event_option_id.as_str(),
                        "event-room:gain-gold:0" | "event-room:leave:1"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectEventOption,
                        &format!("Selected mock event option '{}'.", select.event_option_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-event-option requires a visible event option in the mock bridge.",
                        &[detail(
                            "event_option_id",
                            &select.event_option_id,
                            "Retry with a visible event option id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::OpenEventShop(open)) => {
                if scenario == MockScenario::FakeMerchantPreOpen
                    && open.event_option_id == "event-room:fake-merchant:open-shop"
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::OpenEventShop,
                        "Opened the mock event shop.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "open-event-shop requires the visible Fake Merchant shop option in the mock bridge.",
                        &[detail(
                            "event_option_id",
                            &open.event_option_id,
                            "Retry with the Fake Merchant open-shop option id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::UseCrystalSphereControl(control)) => {
                if scenario == MockScenario::CrystalSphere
                    && matches!(
                        control.control_id.as_str(),
                        "crystal-sphere:tool:big"
                            | "crystal-sphere:tool:small"
                            | "crystal-sphere:cell:0:0"
                            | "crystal-sphere:cell:1:0"
                    )
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::UseCrystalSphereControl,
                        &format!("Used mock Crystal Sphere control '{}'.", control.control_id),
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "use-crystal-sphere-control requires a visible Crystal Sphere control in the mock bridge.",
                        &[detail(
                            "control_id",
                            &control.control_id,
                            "Retry with a visible Crystal Sphere control id.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ProceedEvent(proceed)) => {
                if matches!(
                    scenario,
                    MockScenario::EventRoom | MockScenario::CrystalSphereFinished
                ) && (proceed.player_id.is_empty() || proceed.player_id == "p1")
                {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::ProceedEvent,
                        "Proceeded from the mock event.",
                    ))
                } else {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "proceed-event is only available on event screens with a visible proceed control in the mock bridge.",
                        &[detail(
                            "player_id",
                            &proceed.player_id,
                            "Retry when availableActions includes proceed-event.",
                        )],
                    ))
                }
            }
            Some(proto::action_request::Action::ToggleMap(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ToggleMap,
                "Opened the mock run map.",
            )),
            Some(proto::action_request::Action::ToggleDeck(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ToggleDeck,
                "Opened the mock run deck view.",
            )),
            Some(proto::action_request::Action::ToggleSettings(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ToggleSettings,
                "Opened the mock run settings menu.",
            )),
            Some(proto::action_request::Action::SortDeckView(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::SortDeckView,
                "Sorted the mock run deck view.",
            )),
            Some(proto::action_request::Action::ToggleDeckViewUpgrades(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ToggleDeckViewUpgrades,
                "Toggled mock run deck view upgrade previews.",
            )),
            Some(proto::action_request::Action::OpenPotionPopup(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::OpenPotionPopup,
                "Opened the mock potion popup.",
            )),
            Some(proto::action_request::Action::StartPotionTargeting(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::StartPotionTargeting,
                "Started mock potion targeting.",
            )),
            Some(proto::action_request::Action::SelectTarget(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::SelectTarget,
                "Selected the mock target.",
            )),
            Some(proto::action_request::Action::DiscardPotion(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::DiscardPotion,
                "Discarded the mock potion.",
            )),
            Some(proto::action_request::Action::ViewDrawPile(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ViewDrawPile,
                "Opened the mock draw pile viewer.",
            )),
            Some(proto::action_request::Action::ViewDiscardPile(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ViewDiscardPile,
                "Opened the mock discard pile viewer.",
            )),
            Some(proto::action_request::Action::ViewExhaustPile(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ViewExhaustPile,
                "Opened the mock exhaust pile viewer.",
            )),
            Some(proto::action_request::Action::InspectRelic(inspect_relic)) => {
                if inspect_relic.relic_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "inspect-relic requires a stable relic id.",
                        &[detail(
                            "relic_id",
                            "",
                            "Pass a stable relic id from state.run.players[].relics[].id.",
                        )],
                    ))
                } else {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::InspectRelic,
                        &format!("Opened the mock relic-details overlay for '{}'.", inspect_relic.relic_id),
                    ))
                }
            }
            Some(proto::action_request::Action::CloseInspectRelic(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::CloseInspectRelic,
                "Closed the mock relic-details overlay.",
            )),
            Some(proto::action_request::Action::SelectHandCard(select_hand_card)) => {
                if select_hand_card.card_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "select-hand-card requires a stable card id.",
                        &[detail(
                            "card_id",
                            "",
                            "Provide a card id from state.run.view.handSelection.selectableCardIds.",
                        )],
                    ))
                } else {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::SelectHandCard,
                        &format!(
                            "Staged mock hand card '{}' for the active hand selection.",
                            select_hand_card.card_id
                        ),
                    ))
                }
            }
            Some(proto::action_request::Action::DeselectHandCard(deselect_hand_card)) => {
                if deselect_hand_card.card_id.trim().is_empty() {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "deselect-hand-card requires a stable card id.",
                        &[detail(
                            "card_id",
                            "",
                            "Provide a card id from state.run.view.handSelection.selectedCardIds.",
                        )],
                    ))
                } else {
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::DeselectHandCard,
                        &format!(
                            "Unstaged mock hand card '{}' from the active hand selection.",
                            deselect_hand_card.card_id
                        ),
                    ))
                }
            }
            Some(proto::action_request::Action::ConfirmHandSelection(_)) => Ok(action_response(
                &request.request_id,
                proto::ActionKind::ConfirmHandSelection,
                "Confirmed the mock in-hand card selection.",
            )),
            // Map-drawing RPCs (actions.proto) are a live-only surface; the mock bridge has no
            // map drawing state to mutate.
            Some(proto::action_request::Action::DrawMapStroke(_))
            | Some(proto::action_request::Action::ClearMapDrawings(_)) => Err(error(
                proto::BridgeErrorCode::InvalidAction,
                "Map drawing actions are not supported by the mock bridge.",
                &[],
            )),
            // Dev-only host-side heal (`sts2 dev heal`). The mock bridge has no HP to mutate, so it
            // accepts a well-formed request with a canned response (exercising the CLI wiring) and
            // rejects payloads the live adapter would also reject.
            Some(proto::action_request::Action::Heal(heal)) => {
                if !heal.full && heal.amount <= 0 {
                    Err(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "heal requires a positive amount or full=true.",
                        &[detail(
                            "amount",
                            &heal.amount.to_string(),
                            "Pass --amount <hp> or --full.",
                        )],
                    ))
                } else {
                    let target = if heal.target_id.trim().is_empty() {
                        "the acting seat's player creature".to_string()
                    } else {
                        format!("'{}'", heal.target_id)
                    };
                    Ok(action_response(
                        &request.request_id,
                        proto::ActionKind::Heal,
                        &if heal.full {
                            format!("Set mock creature {target} to full HP.")
                        } else {
                            format!("Healed mock creature {target} by {} HP.", heal.amount)
                        },
                    ))
                }
            }
        };

        match result {
            Ok(response) => proto::ActionResult {
                result: Some(proto::action_result::Result::Success(response)),
            },
            Err(error) => proto::ActionResult {
                result: Some(proto::action_result::Result::Error(error)),
            },
        }
    }

    fn mock_shop_intent_action(
        &self,
        request_id: &str,
        kind: proto::ActionKind,
        requested_id: &str,
        expected_id: &str,
        action_name: &str,
    ) -> Result<proto::ActionResponse, proto::BridgeError> {
        let scenario = self.active_scenario();
        if requested_id.trim().is_empty() {
            return Err(error(
                proto::BridgeErrorCode::InvalidAction,
                &format!("{action_name} requires a stable shop id."),
                &[detail(
                    "shop_item_id",
                    "",
                    "Provide the shop item id from state.shop.purchasableItems[].id or availableActions[].arguments.values.shopItemId.",
                )],
            ));
        }

        if scenario != MockScenario::Shop {
            return Err(error(
                proto::BridgeErrorCode::InvalidAction,
                &format!("{action_name} is only available on the shop screen in the mock bridge."),
                &[detail(
                    "shop_item_id",
                    requested_id,
                    "Switch the mock scenario to shop or use state.availableActions from a live bridge.",
                )],
            ));
        }

        if requested_id != expected_id {
            return Err(error(
                proto::BridgeErrorCode::InvalidAction,
                &format!("{action_name} requires an executable shop id from the current screen."),
                &[detail(
                    "shop_item_id",
                    requested_id,
                    "Retry with a shop id from availableActions[].arguments.values.shopItemId.",
                )],
            ));
        }

        Ok(action_response(
            request_id,
            kind,
            &format!("Executed mock {action_name} '{requested_id}'."),
        ))
    }

}
