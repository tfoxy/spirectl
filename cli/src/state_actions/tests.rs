//! Parity + golden tests for the native `state actions` resolver.
//!
//! * [`native_matches_cel_resolver`] asserts the native resolver reproduces the
//!   former CEL resolver ([`resolve_presentation_state_actions_json`]) exactly,
//!   over both captured fixtures and synthetic per-surface states. This is the
//!   correctness anchor against the catalog source of truth. It is removed when
//!   the CEL resolver is deleted.
//! * [`native_matches_golden`] freezes the verified output as committed goldens
//!   so the contract is guarded after the CEL resolver is gone. Regenerate with
//!   `STATE_ACTIONS_BLESS=1 cargo test -p sts2 native_matches_golden`.

use super::{PerspectiveRequest, resolve_state_actions_json};
use crate::models_ext::PresentationModels;
use serde_json::{Value, json};
use std::path::PathBuf;

fn read_json(path: &std::path::Path) -> Value {
    serde_json::from_slice(
        &std::fs::read(path).unwrap_or_else(|error| panic!("read {path:?}: {error}")),
    )
    .unwrap_or_else(|error| panic!("parse {path:?}: {error}"))
}

fn golden_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("src/state_actions/testdata")
}

fn card_models(id: &str) -> PresentationModels {
    let mut models = PresentationModels::default();
    models.cards.insert(
        id.to_string(),
        json!({ "targetType": "AnyEnemy", "title": { "table": "cards", "key": id.to_uppercase() } }),
    );
    models
}

/// Synthetic parity cases exercising each resolver surface. States mirror the
/// real state shape (sibling `run`/`characterSelect` present as null, projected
/// child keys present) so the resolver does not error on missing keys. These are
/// self-contained and committed; captured fixtures under the gitignored
/// `presentation/dev/fixtures` are intentionally not referenced here.
fn cases() -> Vec<(String, Value, PresentationModels)> {
    let mut cases: Vec<(String, Value, PresentationModels)> = Vec::new();

    cases.push((
        "character-select-not-ready".into(),
        json!({
            "schemaVersion": "spirectl.state/v0",
            "rootScene": "screens/character_select_screen",
            "run": Value::Null,
            "characterSelect": {
                "lobby": { "players": [{ "id": "p1", "isReady": false }] },
                "characterButtons": [
                    { "id": "b1", "characterId": "the-silent", "isLocked": false },
                    { "id": "b2", "characterId": "locked", "isLocked": true }
                ],
                "view": { "playerId": "p1", "selectedCharacterButtonId": "b1" }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "character-select-ready".into(),
        json!({
            "rootScene": "screens/character_select_screen",
            "run": Value::Null,
            "characterSelect": {
                "lobby": { "players": [{ "id": "p1", "isReady": true }] },
                "characterButtons": [{ "id": "b1", "characterId": "the-silent", "isLocked": false }],
                "view": { "playerId": "p1", "selectedCharacterButtonId": "b1" }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "character-select-selected-locked".into(),
        json!({
            "rootScene": "screens/character_select_screen",
            "run": Value::Null,
            "characterSelect": {
                "lobby": { "players": [{ "id": "p1", "isReady": false }] },
                "characterButtons": [{ "id": "b1", "characterId": "locked", "isLocked": true }],
                "view": { "playerId": "p1", "selectedCharacterButtonId": "b1" }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "deck-view".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": {
                    "playerId": "p1",
                    "capstone": { "deckView": { "sort": [{ "by": "cost" }, { "by": "name" }], "showUpgrades": false } }
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "event-room-options".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": {
                    "scene": "rooms/event_room",
                    "event": {
                        "playerStates": [{
                            "playerId": "p1",
                            "isFinished": false,
                            "options": [
                                { "id": "o1", "titleText": "Investigate", "isLocked": false, "wasChosen": false, "isProceed": false },
                                { "id": "o2", "titleText": "Leave", "isLocked": false, "wasChosen": false, "isProceed": true }
                            ]
                        }]
                    }
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "event-room-finished".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": {
                    "scene": "rooms/event_room",
                    "event": {
                        "playerStates": [{
                            "playerId": "p1",
                            "isFinished": true,
                            "options": [{ "id": "o1", "titleText": "Done", "wasChosen": true }]
                        }]
                    }
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "choose-a-card".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "view": { "playerId": "p1" },
                "players": [{
                    "id": "p1",
                    "overlays": [{
                        "id": "ov1",
                        "chooseACard": { "canSkip": true, "cards": [{ "id": "c1", "modelId": "strike" }] }
                    }]
                }]
            }
        }),
        card_models("strike"),
    ));

    cases.push((
        "rewards".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "view": { "playerId": "p1" },
                "players": [{
                    "id": "p1",
                    "overlays": [{
                        "id": "ov1",
                        "rewards": {
                            "flow": { "mode": "proceed", "enabled": true },
                            "items": [
                                { "id": "rw1", "description": Value::Null, "gold": 25, "relic": Value::Null, "potion": Value::Null, "card": Value::Null, "cardReward": Value::Null, "cardRemoval": false },
                                { "id": "rw2", "description": Value::Null, "gold": Value::Null, "relic": "burning-blood", "potion": Value::Null, "card": Value::Null, "cardReward": Value::Null, "cardRemoval": false }
                            ]
                        }
                    }]
                }]
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "treasure".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": {
                    "scene": "rooms/treasure_room",
                    "treasure": { "currentRelics": [{ "id": "r1", "modelId": "relic-a" }] }
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "rest-site".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": {
                    "scene": "rooms/rest_site_room",
                    "restSite": {
                        "view": { "canProceed": true },
                        "playerStates": [{
                            "playerId": "p1",
                            "hoveredOptionIndex": Value::Null,
                            "chosenOptionIndex": Value::Null,
                            "options": [
                                { "id": "rest-site-option:p1:0:HEAL", "optionId": "HEAL", "isEnabled": true },
                                { "id": "rest-site-option:p1:1:SMITH", "optionId": "SMITH", "isEnabled": false, "disabledReason": "not-enabled" },
                                { "id": "rest-site-option:p1:2:LIFT", "optionId": "LIFT", "isEnabled": true, "liftsLeft": 2 },
                                { "id": "rest-site-option:p1:3:DIG", "optionId": "DIG", "isEnabled": true }
                            ]
                        }]
                    }
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "shop".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": {
                    "scene": "rooms/shop_room",
                    "shop": {
                        "inventory": {
                            "characterCardEntries": [
                                { "id": "shop:card:0", "cost": 50, "isOnSale": false, "card": { "id": "card:shop:0", "modelId": "strike" } }
                            ],
                            "colorlessCardEntries": [],
                            "relicEntries": [
                                { "id": "shop:relic:0", "cost": 150, "modelId": "relic-a" }
                            ],
                            "potionEntries": [
                                { "id": "shop:potion:0", "cost": 20, "modelId": "fire-potion" }
                            ],
                            "cardRemovalEntry": { "id": "shop:remove:0", "cost": 75, "used": false }
                        }
                    }
                }
            }
        }),
        card_models("strike"),
    ));

    cases.push((
        "map-room".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "players": [{ "id": "p1" }],
                "view": { "playerId": "p1" },
                "currentRoom": { "scene": "rooms/map_room" },
                "map": {
                    "points": [
                        { "id": "map-node:3:1", "coord": { "row": 3, "col": 1 }, "pointType": "Monster", "travelable": true, "visited": false },
                        { "id": "map-node:2:1", "coord": { "row": 2, "col": 1 }, "pointType": "Event", "travelable": false, "visited": true }
                    ]
                }
            }
        }),
        PresentationModels::default(),
    ));

    cases.push((
        "combat".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "view": { "playerId": "p1" },
                "players": [{
                    "id": "p1",
                    "creature": { "id": "player:p1", "side": "Player", "currentHp": 60, "modelId": "the-silent" },
                    "potions": [],
                    "combat": {
                        "hand": { "cards": [{ "id": "card:1", "modelId": "strike" }] },
                        "drawPile": { "cards": [{ "id": "card:2", "modelId": "defend" }, { "id": "card:3", "modelId": "strike" }] },
                        "discardPile": { "cards": [{ "id": "card:4", "modelId": "strike" }] },
                        "exhaustPile": { "cards": [{ "id": "card:5", "modelId": "defend" }] }
                    }
                }],
                "currentRoom": {
                    "scene": "rooms/combat_room",
                    "combat": {
                        "combatState": {
                            "currentSide": "Player",
                            "enemies": [{ "id": "enemy:1", "side": "Enemy", "currentHp": 30, "modelId": "nibbit" }]
                        }
                    }
                }
            }
        }),
        card_models("strike"),
    ));

    // In-hand selection mode (run.view.handSelection): the selection surface
    // emits select/deselect/confirm and the normal combat surface (play-card,
    // end-turn, pile viewers) is suppressed by the game's selection backstop.
    cases.push((
        "combat-hand-selection".into(),
        json!({
            "rootScene": "run",
            "characterSelect": Value::Null,
            "run": {
                "view": {
                    "playerId": "p1",
                    "isInCardSelection": true,
                    "handSelection": {
                        "playerId": "p1",
                        "mode": "simple-select",
                        "promptText": "Choose a card to [gold]discard[/gold].",
                        "minSelect": 1,
                        "maxSelect": 1,
                        "requireManualConfirmation": false,
                        "canConfirm": true,
                        "selectedCardIds": ["card:2"],
                        "selectableCardIds": ["card:1", "card:3"],
                        "sourceModelId": "SURVIVOR",
                        "isPeeking": false
                    }
                },
                "players": [{
                    "id": "p1",
                    "creature": { "id": "player:p1", "side": "Player", "currentHp": 60, "modelId": "the-silent" },
                    "potions": [],
                    "combat": {
                        "hand": { "cards": [
                            { "id": "card:1", "modelId": "strike" },
                            { "id": "card:2", "modelId": "strike" },
                            { "id": "card:3", "modelId": "strike" }
                        ] },
                        "drawPile": { "cards": [{ "id": "card:4", "modelId": "strike" }] },
                        "discardPile": { "cards": [] },
                        "exhaustPile": { "cards": [] }
                    }
                }],
                "currentRoom": {
                    "scene": "rooms/combat_room",
                    "combat": {
                        "combatState": {
                            "currentSide": "Player",
                            "enemies": [{ "id": "enemy:1", "side": "Enemy", "currentHp": 30, "modelId": "nibbit" }]
                        }
                    }
                }
            }
        }),
        card_models("strike"),
    ));

    cases.push((
        "unmatched".into(),
        json!({ "rootScene": "main_menu", "run": Value::Null, "characterSelect": Value::Null }),
        PresentationModels::default(),
    ));

    cases
}

#[test]
fn native_matches_golden() {
    let bless = std::env::var_os("STATE_ACTIONS_BLESS").is_some();
    if bless {
        std::fs::create_dir_all(golden_dir()).expect("create testdata dir");
    }
    for (name, state, models) in cases() {
        let actual = resolve_state_actions_json(&state, &models, PerspectiveRequest::default());
        let path = golden_dir().join(format!("{name}.json"));
        if bless {
            let mut bytes = serde_json::to_vec_pretty(&actual).expect("serialize golden");
            bytes.push(b'\n');
            std::fs::write(&path, bytes).expect("write golden");
            continue;
        }
        let golden = read_json(&path);
        assert_eq!(
            golden, actual,
            "golden mismatch for `{name}` (rebless if intended)"
        );
    }
}
