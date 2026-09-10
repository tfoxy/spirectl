// Canonical (`spirectl.state/v0`) run projections for the mock transport.
//
// The mock stub used to answer every scenario with a rich envelope. The state promotion
// (e2aa8028) rewrote it against the new proto but only finished the lobby/character-select arm:
// every other scenario fell through to a bare `screens/main_menu` envelope with `run: null`, so
// `--mock-scenario combat` (and friends) reported the main menu. That silently broke the CLI's own
// `tests/scenarios/smoke-main-menu.sts2.yaml` and 11 npm-wrapper tests.
//
// These builders restore a rich per-scenario projection, in the CANONICAL shape only — the current
// presentation envelope (top-level `screen`/`combat`/`scene`/`actions`, card descriptions on the
// state, relic counter labels, intent labels) was deleted by design by design. Presentation text
// belongs to the model/presentation catalog (`models_presentation.rs`); this is the runtime
// projection.
//
// Ids are deliberately shared with the other mocks so state → actions → models joins hold under
// `--mock-scenario`: hand cards `c_1`/`c_2` and enemy `e_1` (`action_impl.rs` play-card),
// `potion:p1:<slot>:<model>` (use-potion), `card-selection:bundle:offensive-pack:0` (select-bundle),
// and the `jaw-worm`/`fire-potion`/`burning-blood` model ids the presentation mock describes.

pub(super) const MOCK_RUN_ROOT_SCENE: &str = "run";
const MOCK_ENEMY_CREATURE_ID: &str = "e_1";
const MOCK_BUNDLE_ID: &str = "card-selection:bundle:offensive-pack:0";

/// Scenarios that are NOT run-shaped: the main menu and the two lobby/character-select screens keep
/// their own arms in `get_state`.
pub(super) fn mock_scenario_has_run(scenario: MockScenario) -> bool {
    !matches!(
        scenario,
        MockScenario::MainMenu | MockScenario::Lobby | MockScenario::LobbyReady
    )
}

/// True for the scenarios where the local player is inside a combat (so the player carries a
/// `combat` block and the current room carries a combat state).
fn mock_scenario_in_combat(scenario: MockScenario) -> bool {
    matches!(
        scenario,
        MockScenario::Combat
            | MockScenario::CardOverlay
            | MockScenario::PassiveCardOverlay
            | MockScenario::SimpleCardSelection
            | MockScenario::BundleSelection
    )
}

/// The room the scenario is standing in: (room_type, source_type, scene, model_id).
fn mock_room_kind(scenario: MockScenario) -> (&'static str, &'static str, &'static str) {
    match scenario {
        MockScenario::EventRoom | MockScenario::FakeMerchantPreOpen => (
            "Event",
            "MegaCrit.Sts2.Core.Rooms.EventRoom",
            "rooms/event_room",
        ),
        MockScenario::TreasureRoom | MockScenario::RelicSelection => (
            "Treasure",
            "MegaCrit.Sts2.Core.Rooms.TreasureRoom",
            "rooms/treasure_room",
        ),
        MockScenario::RestSite => (
            "RestSite",
            "MegaCrit.Sts2.Core.Rooms.RestSiteRoom",
            "rooms/rest_site_room",
        ),
        MockScenario::Shop => (
            "Shop",
            "MegaCrit.Sts2.Core.Rooms.ShopRoom",
            "rooms/shop_room",
        ),
        MockScenario::Map => ("Map", "MegaCrit.Sts2.Core.Rooms.MapRoom", "rooms/map_room"),
        // `rooms/combat_room` is the scene name the action resolver keys the combat surface on
        // (state_actions.rs `surface_combat`) — the same string the live game reports.
        _ => (
            "Monster",
            "MegaCrit.Sts2.Core.Rooms.MonsterRoom",
            "rooms/combat_room",
        ),
    }
}

/// The overlay stack the scenario shows on top of the room, if any. Overlay ids/screen ids match the
/// screen-id constants the action mock validates against.
fn mock_overlays(scenario: MockScenario) -> Vec<proto::StateRunOverlay> {
    let overlay = |screen_type: &str, screen_id: &str, scene: &str| proto::StateRunOverlay {
        id: format!("overlay:p1:{screen_id}"),
        screen_type: screen_type.to_string(),
        screen_id: screen_id.to_string(),
        scene: scene.to_string(),
        ..Default::default()
    };

    match scenario {
        MockScenario::BundleSelection => vec![proto::StateRunOverlay {
            bundle_card_selection: Some(proto::StateBundleCardSelectionOverlay {
                bundles: vec![
                    proto::StateCardBundle {
                        id: MOCK_BUNDLE_ID.to_string(),
                        cards: vec![
                            mock_card("card:p1:bundle:offensive-pack:0", "JAB", 0),
                            mock_card("card:p1:bundle:offensive-pack:1", "BASH", 0),
                        ],
                    },
                    proto::StateCardBundle {
                        id: "card-selection:bundle:defensive-pack:1".to_string(),
                        cards: vec![mock_card("card:p1:bundle:defensive-pack:0", "GUARD", 0)],
                    },
                ],
                can_confirm: false,
                preview_active: false,
                selected_bundle_id: String::new(),
            }),
            ..overlay(
                "CardSelection",
                BUNDLE_SELECTION_SCREEN_ID,
                "screens/card_selection/choose_a_bundle_selection_screen",
            )
        }],
        MockScenario::CardOverlay | MockScenario::PassiveCardOverlay => {
            vec![proto::StateRunOverlay {
                choose_a_card: Some(proto::StateChooseACardOverlay {
                    can_skip: true,
                    cards: vec![
                        mock_card("card:p1:choose-a-card:0", "DEMONIC_SHIELD", 0),
                        mock_card("card:p1:choose-a-card:1", "BASH", 1),
                    ],
                }),
                ..overlay(
                    "CardSelection",
                    CHOOSE_CARD_SELECTION_SCREEN_ID,
                    "screens/card_selection/choose_a_card_selection_screen",
                )
            }]
        }
        MockScenario::CardSelection => vec![overlay(
            "CardSelection",
            CARD_REWARD_SELECTION_SCREEN_ID,
            "screens/card_selection/card_reward_selection_screen",
        )],
        MockScenario::SimpleCardSelection => vec![overlay(
            "CardSelection",
            SIMPLE_CARD_SELECTION_SCREEN_ID,
            "screens/card_selection/simple_card_select_screen",
        )],
        MockScenario::DeckCardSelection => vec![overlay(
            "CardSelection",
            DECK_CARD_SELECTION_SCREEN_ID,
            "screens/card_selection/deck_card_select_screen",
        )],
        MockScenario::Rewards => vec![overlay(
            "Rewards",
            REWARDS_SCREEN_ID,
            "screens/rewards/rewards_screen",
        )],
        MockScenario::RelicSelection => vec![overlay(
            "RelicSelection",
            RELIC_SELECTION_SCREEN_ID,
            "screens/relic_selection/choose_a_relic_selection",
        )],
        MockScenario::CrystalSphere | MockScenario::CrystalSphereFinished => vec![overlay(
            "CrystalSphere",
            CRYSTAL_SPHERE_SCREEN_ID,
            "events/custom/crystal_sphere/crystal_sphere_screen",
        )],
        _ => Vec::new(),
    }
}

fn mock_card(id: &str, model_id: &str, upgrade_level: i32) -> proto::StateCard {
    proto::StateCard {
        id: id.to_string(),
        model_id: model_id.to_string(),
        upgrade_level,
        energy_cost: -1,
        ..Default::default()
    }
}

fn mock_combat_card(
    id: &str,
    model_id: &str,
    energy_cost: i32,
    description_text: &str,
) -> proto::StateCombatCard {
    proto::StateCombatCard {
        id: id.to_string(),
        model_id: model_id.to_string(),
        energy_cost,
        star_cost: -1,
        description_text: description_text.to_string(),
        ..Default::default()
    }
}

fn mock_combat_pile(
    id: &str,
    pile_type: &str,
    cards: Vec<proto::StateCombatCard>,
) -> Option<proto::StateCombatCardPile> {
    Some(proto::StateCombatCardPile {
        source_type: "MegaCrit.Sts2.Core.Entities.Cards.CardPile".to_string(),
        id: id.to_string(),
        r#type: pile_type.to_string(),
        cards,
    })
}

/// The local player's combat block: the same hand/potion ids `act play-card` / `act use-potion`
/// accept in the combat mock scenario.
fn mock_player_combat() -> proto::StateRunPlayerCombat {
    proto::StateRunPlayerCombat {
        source_type: "MegaCrit.Sts2.Core.Combat.CombatManager".to_string(),
        energy: 3,
        max_energy: 3,
        stars: 0,
        hand: mock_combat_pile(
            "pile:p1:hand",
            "Hand",
            vec![
                mock_combat_card("c_1", "JAB", 1, "Land 6 harm."),
                mock_combat_card("c_2", "GUARD", 1, "Gain 5 block."),
            ],
        ),
        draw_pile: mock_combat_pile(
            "pile:p1:draw",
            "DrawPile",
            vec![mock_combat_card("c_3", "JAB", 1, "Land 6 harm.")],
        ),
        discard_pile: mock_combat_pile("pile:p1:discard", "DiscardPile", Vec::new()),
        exhaust_pile: mock_combat_pile("pile:p1:exhaust", "ExhaustPile", Vec::new()),
        play_pile: mock_combat_pile("pile:p1:play", "PlayPile", Vec::new()),
        has_ended_turn: false,
        ..Default::default()
    }
}

fn mock_enemy() -> proto::StateRunCreature {
    proto::StateRunCreature {
        source_type: "MegaCrit.Sts2.Core.Entities.Creatures.Creature".to_string(),
        id: MOCK_ENEMY_CREATURE_ID.to_string(),
        model_id: "JAW_WORM".to_string(),
        side: "Enemy".to_string(),
        slot_name: "slot_0".to_string(),
        current_hp: 40,
        max_hp: 44,
        block: Some(0),
        is_hittable: Some(true),
        next_move: Some(proto::StateCombatNextMove {
            id: "GNASH".to_string(),
            intents: vec![proto::StateCombatIntent {
                r#type: "Attack".to_string(),
                attack: Some(proto::StateCombatAttackIntent {
                    damage: 11,
                    hits: 1,
                    repeats: 1,
                }),
                card_count: None,
            }],
        }),
        ..Default::default()
    }
}

fn mock_player(
    id: &str,
    display_name: &str,
    character_id: &str,
    is_local: bool,
    scenario: MockScenario,
) -> proto::StateRunPlayer {
    let in_combat = mock_scenario_in_combat(scenario);
    proto::StateRunPlayer {
        id: id.to_string(),
        source_type: "MegaCrit.Sts2.Core.Entities.Players.Player".to_string(),
        net_id: if is_local { "100" } else { "200" }.to_string(),
        display_name: display_name.to_string(),
        character_id: character_id.to_string(),
        is_local,
        is_host: is_local,
        is_remote: !is_local,
        creature: Some(proto::StateRunCreature {
            source_type: "MegaCrit.Sts2.Core.Entities.Creatures.Creature".to_string(),
            id: format!("creature:{id}"),
            model_id: character_id.to_uppercase(),
            side: "Player".to_string(),
            current_hp: 52,
            max_hp: 80,
            block: Some(0),
            is_hittable: Some(true),
            ..Default::default()
        }),
        gold: 123,
        deck: Some(proto::StateCardPile {
            source_type: "MegaCrit.Sts2.Core.Entities.Cards.CardPile".to_string(),
            id: format!("deck:{id}"),
            r#type: "Deck".to_string(),
            count: 2,
            cards: vec![
                mock_card(&format!("card:{id}:deck:0"), "JAB", 0),
                mock_card(&format!("card:{id}:deck:1"), "GUARD", 0),
            ],
            order_observable: true,
        }),
        relics: vec![proto::StateRelic {
            id: format!("relic:{id}:0:burning-blood"),
            model_id: "BURNING_BLOOD".to_string(),
            slot_index: 0,
            has_counter: true,
            counter: 3,
        }],
        potions: vec![
            proto::StateCombatPotion {
                id: 0,
                model_id: "fire-potion".to_string(),
                is_queued: false,
                passes_usability_check: true,
            },
            proto::StateCombatPotion {
                id: 1,
                model_id: "block-potion".to_string(),
                is_queued: false,
                passes_usability_check: true,
            },
        ],
        can_remove_potions: true,
        inventory_complete: true,
        overlays: if is_local {
            mock_overlays(scenario)
        } else {
            Vec::new()
        },
        combat: if in_combat {
            Some(mock_player_combat())
        } else {
            None
        },
        ..Default::default()
    }
}

fn mock_current_room(scenario: MockScenario) -> proto::StateRunCurrentRoom {
    let (room_type, source_type, scene) = mock_room_kind(scenario);
    proto::StateRunCurrentRoom {
        source_type: source_type.to_string(),
        room_type: room_type.to_string(),
        scene: scene.to_string(),
        model_id: if room_type == "Monster" {
            "JAW_WORM_WEAK".to_string()
        } else {
            String::new()
        },
        id: Some(42),
        combat: if mock_scenario_in_combat(scenario) {
            Some(proto::StateRunCombatRoom {
                encounter_id: "JAW_WORM_WEAK".to_string(),
                gold_proportion: 1.0,
                should_create_combat: true,
                combat_state: Some(proto::StateCombatState {
                    source_type: "MegaCrit.Sts2.Core.Combat.CombatState".to_string(),
                    current_side: "Player".to_string(),
                    round_number: 2,
                    enemies: vec![mock_enemy()],
                    ..Default::default()
                }),
                ..Default::default()
            })
        } else {
            None
        },
        ..Default::default()
    }
}

/// The canonical run envelope for a run-shaped mock scenario. `mock_scenario_has_run` gates it.
pub(super) fn mock_run(scenario: MockScenario) -> proto::StateRun {
    let local_player_id = mock_local_player_id(scenario);
    proto::StateRun {
        source_type: "MegaCrit.Sts2.Core.Runs.RunState".to_string(),
        manager_source_type: "MegaCrit.Sts2.Core.Runs.RunManager".to_string(),
        net_game_type: "host".to_string(),
        game_mode: "Standard".to_string(),
        seed: "mock-seed".to_string(),
        ascension_level: 0,
        act_id: "act_1".to_string(),
        current_act_index: 0,
        act_floor: 3,
        total_floor: 3,
        boss_encounter_id: "GUARDIAN".to_string(),
        current_map_coord: Some(proto::StateMapCoord { row: 3, col: 1 }),
        current_map_point_id: "map-node:3:1".to_string(),
        visited_map_coords: vec![proto::StateMapCoord { row: 2, col: 1 }],
        players: vec![
            mock_player(&local_player_id, "Host", "ironclad", true, scenario),
            mock_player("p2", "Guest", "silent", false, scenario),
        ],
        map: Some(proto::StateRunMap {
            source_type: "MegaCrit.Sts2.Core.Map.ActMap".to_string(),
            row_count: 15,
            column_count: 7,
            starting_map_point_id: "map-node:0:3".to_string(),
            boss_map_point_id: "map-node:14:3".to_string(),
            points: vec![proto::StateMapPoint {
                id: "map-node:3:1".to_string(),
                source_type: "MegaCrit.Sts2.Core.Map.MapPoint".to_string(),
                coord: Some(proto::StateMapCoord { row: 3, col: 1 }),
                point_type: "Monster".to_string(),
                travelable: true,
                visited: false,
                ..Default::default()
            }],
            view: Some(proto::StateRunMapView {
                is_open: matches!(scenario, MockScenario::Map),
                is_accepting_votes: matches!(scenario, MockScenario::Map),
            }),
            ..Default::default()
        }),
        current_room: Some(mock_current_room(scenario)),
        view: Some(proto::StateRunView {
            player_id: local_player_id,
            is_in_card_selection: matches!(
                scenario,
                MockScenario::CardSelection
                    | MockScenario::SimpleCardSelection
                    | MockScenario::DeckCardSelection
                    | MockScenario::BundleSelection
                    | MockScenario::CardOverlay
                    | MockScenario::PassiveCardOverlay
            ),
            ..Default::default()
        }),
        notices: vec![proto::StateNotice {
            code: "mock-transport-projection".to_string(),
            message: "State came from the mock transport, not a live game.".to_string(),
            provisional: true,
            path: "run".to_string(),
            severity: "partial".to_string(),
            source: "stub".to_string(),
            ..Default::default()
        }],
        ..Default::default()
    }
}
