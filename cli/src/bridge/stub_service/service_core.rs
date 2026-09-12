#[derive(Debug, Clone)]
pub struct StubBridgeService {
    scenario: MockScenario,
    debug_state: Arc<Mutex<StubDebugState>>,
    build_identity: Option<proto::BridgeBuildIdentity>,
    bridge_version: Option<String>,
}

#[derive(Debug, Default)]
struct StubDebugState {
    next_breakpoint_id: u32,
    breakpoints: Vec<proto::DebugBreakpoint>,
    /// Samples served by `get_runtime_transition_status`. The first one reports
    /// a running transition so quiescence waits have something to settle from.
    transition_samples: u64,
}

impl StubBridgeService {
    pub fn new(scenario: MockScenario) -> Self {
        Self {
            scenario,
            debug_state: Arc::new(Mutex::new(StubDebugState::default())),
            build_identity: None,
            bridge_version: None,
        }
    }

    pub fn with_build_identity(mut self, build_identity: proto::BridgeBuildIdentity) -> Self {
        self.build_identity = Some(build_identity);
        self
    }

    /// Answer the handshake with a bridge version other than the CLI's own, so a
    /// caller can exercise the `live_version_mismatch` arm of `bridge-health`.
    pub fn with_bridge_version(mut self, bridge_version: &str) -> Self {
        self.bridge_version = Some(bridge_version.to_string());
        self
    }

    fn handshake_bridge_version(&self) -> String {
        self.bridge_version
            .clone()
            .unwrap_or_else(|| BRIDGE_VERSION.to_string())
    }

    /// True for the first transition-status sample of this service instance.
    /// A mock scene is otherwise always settled, and a wait that is quiescent on
    /// its very first sample cannot show that waiting works at all.
    fn transition_sample_is_settling(&self) -> bool {
        let mut state = self.debug_state.lock().expect("stub debug state lock");
        state.transition_samples += 1;
        state.transition_samples == 1
    }

    fn active_scenario(&self) -> MockScenario {
        MOCK_LOADED_FIXTURE_SCENARIO.with(|loaded| loaded.borrow().unwrap_or(self.scenario))
    }

    pub fn handshake(&self, request: proto::HandshakeRequest) -> proto::HandshakeResult {
        let mut supported_actions = vec![
            action_descriptor(
                "play-card",
                proto::ActionKind::PlayCard,
                "Play a card against an optional target.",
                "sts2 act play-card --card c_1 --target e_1",
                false,
                proto::ActionStatus::Implemented,
                vec![
                    action_parameter(
                        "cardId",
                        "string",
                        true,
                        "Stable card id from state.combat.hand[].id.",
                    ),
                    action_parameter(
                        "targetId",
                        "string",
                        false,
                        "Optional stable target id when the card requires a target.",
                    ),
                ],
            ),
            action_descriptor(
                "use-potion",
                proto::ActionKind::UsePotion,
                "Use a combat potion against an optional target.",
                "sts2 act use-potion --potion potion:p:100:0:fire-potion --target e_1",
                false,
                proto::ActionStatus::Implemented,
                vec![
                    action_parameter(
                        "potionId",
                        "string",
                        true,
                        "Stable potion id from state.combat.potions[].id.",
                    ),
                    action_parameter(
                        "targetId",
                        "string",
                        false,
                        "Optional stable target id when the potion requires a combat target.",
                    ),
                ],
            ),
            action_descriptor(
                "choose",
                proto::ActionKind::Choose,
                "Fallback-only compatibility action for generic, modded, or unmodeled visible choices when no modeled preferredAction exists.",
                "sts2 act choose --choice reward:p1:0",
                false,
                proto::ActionStatus::Implemented,
                vec![action_parameter(
                    "choiceId",
                    "string",
                    true,
                    "Stable fallback compatibility choice id from state.choices[].id or availableActions[].arguments.choiceId.",
                )],
            ),
            action_descriptor(
                "confirm-selection",
                proto::ActionKind::ConfirmSelection,
                "Confirm the currently staged selection on a supported selection overlay.",
                "sts2 act confirm-selection",
                false,
                proto::ActionStatus::Implemented,
                Vec::new(),
            ),
            action_descriptor(
                "cancel-selection",
                proto::ActionKind::CancelSelection,
                "Cancel the currently staged selection on a supported selection overlay.",
                "sts2 act cancel-selection",
                false,
                proto::ActionStatus::Implemented,
                Vec::new(),
            ),
            action_descriptor(
                "select-map-node",
                proto::ActionKind::SelectMapNode,
                "Select an executable map node by stable id.",
                "sts2 act select-map-node --node map-node:3:1",
                false,
                proto::ActionStatus::Implemented,
                vec![action_parameter(
                    "mapNodeId",
                    "string",
                    true,
                    "Stable map node id from state.map.nodes[].id, state.choices[].id, or availableActions[].arguments.mapNodeId.",
                )],
            ),
            action_descriptor(
                "end-turn",
                proto::ActionKind::EndTurn,
                "End the current turn for the active player.",
                "sts2 act end-turn",
                false,
                proto::ActionStatus::Implemented,
                Vec::new(),
            ),
            action_descriptor(
                "select-character",
                proto::ActionKind::SelectCharacter,
                "Select an executable multiplayer lobby character by stable id.",
                "sts2 act select-character --character silent",
                false,
                proto::ActionStatus::Implemented,
                vec![action_parameter(
                    "characterId",
                    "string",
                    true,
                    "Stable character id from state.lobby.availableCharacters[].id or the visible lobby state.",
                )],
            ),
            action_descriptor(
                "ready",
                proto::ActionKind::Ready,
                "Mark the local multiplayer lobby player ready when the screen allows it.",
                "sts2 act ready",
                false,
                proto::ActionStatus::Implemented,
                Vec::new(),
            ),
            action_descriptor(
                "unready",
                proto::ActionKind::Unready,
                "Clear the local multiplayer lobby ready state when the screen allows it.",
                "sts2 act unready",
                false,
                proto::ActionStatus::Implemented,
                Vec::new(),
            ),
        ];

        supported_actions.extend([
            action_descriptor(
                "claim-reward",
                proto::ActionKind::ClaimReward,
                "Claim a visible reward by stable reward id.",
                "sts2 act claim-reward --reward reward:p1:0",
                false,
                proto::ActionStatus::Implemented,
                vec![action_parameter("rewardId", "string", true, "Stable reward id from state.rewards.rewards[].id.")],
            ),
            action_descriptor("skip-rewards", proto::ActionKind::SkipRewards, "Skip the visible rewards flow when legal.", "sts2 act skip-rewards", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("select-card", proto::ActionKind::SelectCard, "Select a visible card from a supported card-selection screen.", "sts2 act select-card --card card-selection:card:bash:0", false, proto::ActionStatus::Implemented, vec![action_parameter("cardId", "string", true, "Stable card id from the visible card-selection state.")]),
            action_descriptor("skip-card-selection", proto::ActionKind::SkipCardSelection, "Skip a skippable card-selection flow.", "sts2 act skip-card-selection", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("select-bundle", proto::ActionKind::SelectBundle, "Select a visible card bundle by stable bundle id.", "sts2 act select-bundle --bundle card-selection:bundle:offensive-pack:0", false, proto::ActionStatus::Implemented, vec![action_parameter("bundleId", "string", true, "Stable bundle id from state.bundleSelection.bundles[].id.")]),
            action_descriptor("buy-card", proto::ActionKind::BuyCard, "Buy a visible shop card by stable shop item id.", "sts2 act buy-card --shop-item shop:p1:card:strike:0", false, proto::ActionStatus::Implemented, vec![action_parameter("shopItemId", "string", true, "Stable shop item id from state.shop.purchasableItems[].id.")]),
            action_descriptor("buy-relic", proto::ActionKind::BuyRelic, "Buy a visible shop relic by stable shop item id.", "sts2 act buy-relic --shop-item shop:p1:relic:anchor:0", false, proto::ActionStatus::Implemented, vec![action_parameter("shopItemId", "string", true, "Stable shop item id from state.shop.purchasableItems[].id.")]),
            action_descriptor("buy-potion", proto::ActionKind::BuyPotion, "Buy a visible shop potion by stable shop item id.", "sts2 act buy-potion --shop-item shop:p1:potion:fire-potion:0", false, proto::ActionStatus::Implemented, vec![action_parameter("shopItemId", "string", true, "Stable shop item id from state.shop.purchasableItems[].id.")]),
            action_descriptor("remove-card", proto::ActionKind::RemoveCard, "Use a visible shop card-removal service.", "sts2 act remove-card --shop-item shop:p1:card-removal:0", false, proto::ActionStatus::Implemented, vec![action_parameter("shopItemId", "string", true, "Stable shop service id from state.shop.purchasableItems[].id.")]),
            action_descriptor("leave-shop", proto::ActionKind::LeaveShop, "Leave the current shop when the shop exit is visible and legal.", "sts2 act leave-shop", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("close-shop-inventory", proto::ActionKind::CloseShopInventory, "Close an opened Fake Merchant shop inventory when visible.", "sts2 act close-shop-inventory", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("rest", proto::ActionKind::Rest, "Use the visible rest option at a rest site.", "sts2 act rest", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("smith", proto::ActionKind::Smith, "Use the visible smith option at a rest site.", "sts2 act smith --card c_1", false, proto::ActionStatus::Implemented, vec![action_parameter("cardId", "string", false, "Optional card id when the smith flow can accept a card directly.")]),
            action_descriptor("use-rest-site-option", proto::ActionKind::UseRestSiteOption, "Use another visible rest-site option by stable option id.", "sts2 act use-rest-site-option --rest-option heal", false, proto::ActionStatus::Implemented, vec![action_parameter("restOptionId", "string", true, "Stable rest-site option id from state.restSite.controls[].id or action arguments.")]),
            action_descriptor("proceed-rest-site", proto::ActionKind::ProceedRestSite, "Proceed from a rest site when the proceed control is visible.", "sts2 act proceed-rest-site", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("open-chest", proto::ActionKind::OpenChest, "Open a visible treasure-room chest.", "sts2 act open-chest", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("take-relic", proto::ActionKind::TakeRelic, "Take a visible treasure-room or relic-selection relic by stable relic id.", "sts2 act take-relic --relic anchor", false, proto::ActionStatus::Implemented, vec![action_parameter("relicId", "string", true, "Stable relic id from the visible relic state.")]),
            action_descriptor("proceed-treasure-room", proto::ActionKind::ProceedTreasureRoom, "Proceed from a treasure room when visible.", "sts2 act proceed-treasure-room", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("back-from-map", proto::ActionKind::BackFromMap, "Go back from the map when the back control is visible.", "sts2 act back-from-map", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("select-event-option", proto::ActionKind::SelectEventOption, "Select a visible first-party event option by stable option id.", "sts2 act select-event-option --event-option event-room:gain-gold:0", false, proto::ActionStatus::Implemented, vec![action_parameter("eventOptionId", "string", true, "Stable event option id from state.eventRoom.options[].id.")]),
            action_descriptor("open-event-shop", proto::ActionKind::OpenEventShop, "Open a visible event-owned shop entry point such as Fake Merchant.", "sts2 act open-event-shop --event-option event-room:fake-merchant:open-shop", false, proto::ActionStatus::Implemented, vec![action_parameter("eventOptionId", "string", true, "Stable event option id for the shop-entry control.")]),
            action_descriptor("use-crystal-sphere-control", proto::ActionKind::UseCrystalSphereControl, "Use a visible Crystal Sphere control by stable control id.", "sts2 act use-crystal-sphere-control --control crystal-sphere:tool:big", false, proto::ActionStatus::Implemented, vec![action_parameter("controlId", "string", true, "Stable Crystal Sphere control id from event-room state."), action_parameter("selectedTool", "string", false, "Optional Crystal Sphere tool override for cell controls: big or small.")]),
            action_descriptor("proceed-event", proto::ActionKind::ProceedEvent, "Proceed from an event when the proceed control is visible.", "sts2 act proceed-event", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("toggle-map", proto::ActionKind::ToggleMap, "Toggle the run top-bar map.", "sts2 act toggle-map", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("toggle-deck", proto::ActionKind::ToggleDeck, "Toggle the run top-bar deck view.", "sts2 act toggle-deck", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("toggle-settings", proto::ActionKind::ToggleSettings, "Toggle the run top-bar settings menu.", "sts2 act toggle-settings", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("sort-deck-view", proto::ActionKind::SortDeckView, "Sort the run deck view.", "sts2 act sort-deck-view --by type", false, proto::ActionStatus::Implemented, vec![action_parameter("by", "string", true, "Deck view sort key: obtained, type, cost, or alphabet.")]),
            action_descriptor("toggle-deck-view-upgrades", proto::ActionKind::ToggleDeckViewUpgrades, "Toggle run deck view upgrade previews.", "sts2 act toggle-deck-view-upgrades", false, proto::ActionStatus::Implemented, Vec::new()),
            action_descriptor("open-potion-popup", proto::ActionKind::OpenPotionPopup, "Open a visible top-bar potion popup.", "sts2 act open-potion-popup --potion potion:p1:0:fire-potion", false, proto::ActionStatus::Implemented, vec![action_parameter("potionId", "string", true, "Stable potion id or slot index from state.run.players[].potions[].")]),
            action_descriptor("start-potion-targeting", proto::ActionKind::StartPotionTargeting, "Enter target selection for a visible potion.", "sts2 act start-potion-targeting --potion potion:p1:0:fire-potion", false, proto::ActionStatus::Implemented, vec![action_parameter("potionId", "string", true, "Stable potion id or slot index from state.run.players[].potions[].")]),
            action_descriptor("select-target", proto::ActionKind::SelectTarget, "Select a visible target while target selection is active.", "sts2 act select-target --target e_1", false, proto::ActionStatus::Implemented, vec![action_parameter("targetId", "string", true, "Stable target id derived from state.run.view.selectedPotion, combat creatures, or action arguments.")]),
            action_descriptor("discard-potion", proto::ActionKind::DiscardPotion, "Discard a visible top-bar potion through its popup.", "sts2 act discard-potion --potion potion:p1:0:fire-potion", false, proto::ActionStatus::Implemented, vec![action_parameter("potionId", "string", true, "Stable potion id or slot index from state.run.players[].potions[].")]),
        ]);

        if request.mode.eq_ignore_ascii_case("dangerous") {
            supported_actions.push(action_descriptor(
                "mouse-click",
                proto::ActionKind::MouseClick,
                "Dispatch a dangerous raw viewport mouse click at explicit coordinates.",
                "sts2 --mode dangerous act mouse click --x 100 --y 200",
                true,
                proto::ActionStatus::Implemented,
                vec![
                    action_parameter(
                        "x",
                        "integer",
                        true,
                        "Viewport x coordinate, matching screenshot pixel space.",
                    ),
                    action_parameter(
                        "y",
                        "integer",
                        true,
                        "Viewport y coordinate, matching screenshot pixel space.",
                    ),
                    action_parameter(
                        "button",
                        "string",
                        false,
                        "Optional mouse button: left, right, or middle. Defaults to left.",
                    ),
                ],
            ));
        }

        proto::HandshakeResult {
            result: Some(proto::handshake_result::Result::Success(
                proto::HandshakeResponse {
                    schema_version: "spirectl/v0".to_string(),
                    game_version: "unknown".to_string(),
                    bridge_version: self.handshake_bridge_version(),
                    build_identity: Some(self.build_identity.clone().unwrap_or(
                        proto::BridgeBuildIdentity {
                            bridge_semver: BRIDGE_SEMVER.to_string(),
                            bridge_version: BRIDGE_VERSION.to_string(),
                            assembly_informational_version: BRIDGE_SEMVER.to_string(),
                            built_at_utc: "mock".to_string(),
                            // A stub bridge was compiled against no game build at
                            // all, so it claims none. Tests that exercise the
                            // game-build gate override build_identity instead.
                            sts2_api_lane: String::new(),
                            built_against_game_version: String::new(),
                            built_against_main_assembly_hash: String::new(),
                        },
                    )),
                    transport_kind: proto::TransportKind::Mock as i32,
                    attachment_state: proto::AttachmentState::Stubbed as i32,
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    default_perspective: Some(default_perspective(self.active_scenario(), true)),
                    capabilities: vec![
                        capability("handshake", "Bridge metadata and capability negotiation."),
                        capability(
                            "state",
                            "Runtime-only presentation state envelope with perspective selection.",
                        ),
                        capability(
                            "state-watch",
                            "Reactive runtime state envelope stream with duplicate suppression.",
                        ),
                        capability(
                            "actions",
                            "Runtime action execution contract with stubbed semantic and dangerous-mode raw-input support.",
                        ),
                        capability("logs", "Structured bridge log retrieval."),
                        capability(
                            "debug-control",
                            "Explicit dev-only debug status, stepping, and breakpoint scaffolding.",
                        ),
                        capability(
                            "artifacts",
                            "Bridge-backed screenshot capture for dev workflows.",
                        ),
                    ],
                    supported_actions,
                },
            )),
        }
    }

    pub fn get_state(&self, request: proto::StateRequest) -> proto::StateResult {
        let scenario = self.active_scenario();
        let resolved = resolve_perspective(scenario, request.perspective.as_ref());
        // Every run-shaped scenario answers with the rich canonical run projection (see
        // mock_data/state_run.rs); only the main menu and the two lobby screens are run-less.
        let (root_scene, character_select, run) = match scenario {
            MockScenario::Lobby => (
                "screens/character_select_screen",
                Some(mock_character_select(&resolved, false)),
                None,
            ),
            MockScenario::LobbyReady => (
                "screens/character_select_screen",
                Some(mock_character_select(&resolved, true)),
                None,
            ),
            _ if mock_scenario_has_run(scenario) => {
                (MOCK_RUN_ROOT_SCENE, None, Some(mock_run(scenario)))
            }
            _ => ("screens/main_menu", None, None),
        };

        proto::StateResult {
            result: Some(proto::state_result::Result::Success(proto::StateResponse {
                // Canonical schema id, shared with the C# producer
                // (bridge-mod/.../Core/State/StateSnapshot.cs `CurrentSchemaVersion`). Keep in sync.
                schema_version: "spirectl.state/v0".to_string(),
                language: "mock".to_string(),
                root_scene: root_scene.to_string(),
                character_select,
                run,
            })),
        }
    }
}
