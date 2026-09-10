use super::*;
use serde_json::json;

fn write_fixture(raw: &str) -> tempfile::NamedTempFile {
    let fixture = tempfile::NamedTempFile::new().expect("temp fixture");
    fs::write(fixture.path(), raw).expect("write fixture");
    fixture
}

fn prepared_fixture(raw: &str) -> PreparedFixture {
    let fixture = write_fixture(raw);
    prepare_fixture(fixture.path(), None).expect("fixture should normalize")
}

fn invalid_fixture(raw: &str) -> AppError {
    let fixture = write_fixture(raw);
    prepare_fixture(fixture.path(), None).expect_err("fixture should be invalid")
}

#[test]
fn prepare_fixture_applies_state_defaults() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: minimal-shop
run:
  currentRoom:
    shop: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["schemaVersion"], "spirectl.fixture/v0");
    assert_eq!(payload["screen"], bridge::SHOP_SCREEN_ID);
    assert_eq!(payload["run"]["seed"], "minimal-shop");
    assert_eq!(payload["run"]["currentActIndex"], 0);
    assert_eq!(payload["run"]["actFloor"], 1);
    assert_eq!(payload["run"]["players"][0]["id"], "p:1");
    assert_eq!(payload["run"]["players"][0]["characterId"], "IRONCLAD");
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
    assert!(payload["run"]["currentRoom"]["shop"]["inventory"].is_null());
}

#[test]
fn prepare_fixture_accepts_minimal_main_menu_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-main-menu
rootScene: main-menu
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "main-menu");
    assert_eq!(payload["rootScene"], "main-menu");
    assert_eq!(payload["run"]["players"][0]["id"], "p:1");
    assert!(payload["run"]["currentRoom"].is_null());
}

#[test]
fn prepare_fixture_accepts_minimal_character_select_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: minimal-lobby
characterSelect: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::START_RUN_LOBBY_SCREEN_ID);
    assert_eq!(payload["characterSelect"]["kind"], "start-run");
    assert_eq!(payload["characterSelect"]["lobby"]["localPlayerId"], "p:1");
    assert_eq!(payload["characterSelect"]["lobby"]["hostPlayerId"], "p:1");
    assert_eq!(payload["characterSelect"]["lobby"]["seed"], "minimal-lobby");
    assert_eq!(
        payload["characterSelect"]["lobby"]["players"][0]["characterId"],
        "IRONCLAD"
    );
    assert_eq!(
        payload["characterSelect"]["lobby"]["players"][0]["slotId"],
        0
    );
    assert_eq!(payload["characterSelect"]["view"]["playerId"], "p:1");
    assert!(payload["run"].is_null());
}

#[test]
fn prepare_fixture_rejects_non_v0_schema_version() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/unsupported
name: invalid-schema
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    let rendered = format!("{payload}");
    assert!(rendered.contains("fixture schemaVersion is not supported"));
    assert!(rendered.contains("Use schemaVersion: spirectl.fixture/v0"));
}

#[test]
fn prepare_fixture_rejects_underivable_recipe() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: no-signal
run:
  actFloor: 3
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["details"][0]["field"], "screen");
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "unknown_screen_id"
    );
}

#[test]
fn prepare_fixture_accepts_event_room_recipe_sections() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-event-room
run:
  actFloor: 7
  players:
    - id: p1
      characterId: ironclad
  currentRoom:
    event:
      canonicalEventModelId: golden-idol
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::EVENT_ROOM_SCREEN_ID);
    assert_eq!(
        payload["run"]["currentRoom"]["event"]["canonicalEventModelId"],
        "golden-idol"
    );
    assert!(payload["run"]["currentRoom"]["event"]["playerStates"].is_null());
    assert!(payload["run"]["currentRoom"]["treasure"].is_null());
    assert!(payload["run"]["currentRoom"]["shop"].is_null());
}

#[test]
fn prepare_fixture_accepts_neow_ancient_event_room() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: initial-neow
run:
  currentRoom:
    event:
      canonicalEventModelId: neow
      playerStates:
        - ancient:
            view:
              visibleDialogue:
                dialogueId: NEOW.talk.ANY.4
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::EVENT_ROOM_SCREEN_ID);
    let event = &payload["run"]["currentRoom"]["event"];
    assert_eq!(event["canonicalEventModelId"], "neow");
    // The omitted playerStates owner defaults to the view player.
    assert_eq!(event["playerStates"][0]["playerId"], "p:1");
    assert_eq!(
        event["playerStates"][0]["ancient"]["view"]["visibleDialogue"]["dialogueId"],
        "NEOW.talk.ANY.4"
    );
}

#[test]
fn prepare_fixture_accepts_multiplayer_event_room_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: two-ironclad-neow
run:
  seed: fixture-two-ironclad-neow-ancient-event
  players:
    - id: "p:1"
      characterId: ironclad
      creature:
        currentHp: 80
        maxHp: 80
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      creature:
        currentHp: 80
        maxHp: 80
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    event:
      canonicalEventModelId: neow
      playerStates:
        - ancient:
            view:
              visibleDialogue:
                dialogueId: NEOW.talk.ANY.4
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::EVENT_ROOM_SCREEN_ID);
    assert_eq!(payload["run"]["players"][0]["id"], "p:1");
    assert_eq!(payload["run"]["players"][1]["isHostLocalSeat"], true);
    // The view player defaults to the host-owned local player.
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
    assert_eq!(
        payload["run"]["currentRoom"]["event"]["playerStates"][0]["playerId"],
        "p:1"
    );
}

#[test]
fn prepare_fixture_defaults_multiplayer_player_metadata() {
    // Multi-player fixtures may omit isLocal/isHostLocalSeat/slotId: the first
    // authored player defaults to the host-owned local seat, the rest to
    // host-local seats, slots follow author order, and all are local.
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: defaulted-multiplayer-event
run:
  players:
    - id: "p:1"
      characterId: ironclad
    - id: "p:2"
      characterId: ironclad
  currentRoom:
    event:
      canonicalEventModelId: neow
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let players = &payload["run"]["players"];
    assert_eq!(players[0]["isLocal"], true);
    assert_eq!(players[0]["isHostLocalSeat"], false);
    assert_eq!(players[0]["slotId"], 0);
    assert_eq!(players[1]["isLocal"], true);
    assert_eq!(players[1]["isHostLocalSeat"], true);
    assert_eq!(players[1]["slotId"], 1);
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
}

#[test]
fn prepare_fixture_expands_player_count_shorthand() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: player-count-shorthand
run:
  players: 2
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    let players = &payload["run"]["players"];
    assert_eq!(players[0]["id"], "p:1");
    assert_eq!(players[0]["characterId"], "IRONCLAD");
    assert_eq!(players[0]["isHostLocalSeat"], false);
    assert_eq!(players[1]["id"], "p:2");
    assert_eq!(players[1]["characterId"], "IRONCLAD");
    assert_eq!(players[1]["isHostLocalSeat"], true);
    assert_eq!(players[1]["slotId"], 1);
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
}

#[test]
fn prepare_fixture_round_trips_per_player_ended_turn() {
    // Per-player `endedTurn` carries to the wire doc only for the flagged seat;
    // the loader uses it to mark that seat ready-to-end-turn on load.
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-end-turn-own
run:
  players:
    - id: "p:1"
      endedTurn: true
    - id: "p:2"
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let players = &payload["run"]["players"];
    assert_eq!(players[0]["endedTurn"], true);
    // skip_serializing_if keeps the key absent for un-ended seats.
    assert!(players[1].get("endedTurn").is_none());
}

#[test]
fn prepare_fixture_defaults_omitted_player_ids() {
    // List players may omit id; ids enumerate p:1..p:N by author order.
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: omitted-player-ids
run:
  players:
    - characterId: NECROBINDER
    - characterId: NECROBINDER
    - characterId: NECROBINDER
  currentRoom:
    combat:
      encounterId: KAISER_CRAB_BOSS
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let players = &payload["run"]["players"];
    assert_eq!(players[0]["id"], "p:1");
    assert_eq!(players[1]["id"], "p:2");
    assert_eq!(players[2]["id"], "p:3");
    assert_eq!(players[0]["characterId"], "NECROBINDER");
    assert_eq!(players[0]["isHostLocalSeat"], false);
    assert_eq!(players[2]["isHostLocalSeat"], true);
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
}

#[test]
fn prepare_fixture_rejects_zero_player_count_shorthand() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: zero-players
run:
  players: 0
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["details"][0]["field"], "run.players");
}

#[test]
fn prepare_fixture_rejects_multiplayer_event_room_remote_players() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-multiplayer-event
run:
  players:
    - id: "p:1"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      isLocal: false
      isHostLocalSeat: false
      slotId: 1
  currentRoom:
    event:
      canonicalEventModelId: neow
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("event-room multiplayer remote players are not supported"));
    assert!(rendered.contains("True remote event-room players are not actionable"));
}

#[test]
fn prepare_fixture_rejects_explicit_view_player_not_host_owned() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-view-player
run:
  view:
    playerId: "p:2"
  players:
    - id: "p:1"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("run.view.playerId must use the host-owned local combat player"));
}

#[test]
fn prepare_fixture_accepts_event_room_options_when_authored() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-event-room
run:
  actFloor: 7
  currentRoom:
    event:
      canonicalEventModelId: golden-idol
      playerStates:
        - options:
            - id: gain-gold
              titleText: Take 75 Gold
            - id: leave
              titleText: Leave
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let options = &payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"];
    assert_eq!(options[0]["id"], "gain-gold");
    assert_eq!(options[0]["titleText"], "Take 75 Gold");
    assert_eq!(options[1]["id"], "leave");
}

#[test]
fn prepare_fixture_accepts_state_shaped_event_options() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-event-room-state-options
run:
  currentRoom:
    event:
      canonicalEventModelId: golden-idol
      playerStates:
        - options:
            - textKey: GOLDEN_IDOL.pages.INITIAL.options.GAIN_GOLD
              wasChosen: false
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let options = &payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"];
    assert_eq!(
        options[0]["textKey"],
        "GOLDEN_IDOL.pages.INITIAL.options.GAIN_GOLD"
    );
    assert_eq!(options[0]["wasChosen"], false);
    assert!(options[0]["id"].is_null());
    assert_eq!(payload["screen"], bridge::EVENT_ROOM_SCREEN_ID);
}

#[test]
fn prepare_fixture_derives_crystal_sphere_screen_from_chosen_option() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: event-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE
              wasChosen: false
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::CRYSTAL_SPHERE_SCREEN_ID);
    let options = &payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"];
    assert_eq!(
        options[1]["textKey"],
        "CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN"
    );
    assert_eq!(options[1]["wasChosen"], true);
}

#[test]
fn prepare_fixture_preserves_crystal_sphere_grid_state() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: event-crystal-sphere-grid-revealed
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE
              wasChosen: false
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
          crystalSphere:
            selectedTool: big
            divinationsRemaining: 2
            cells:
              - id: crystal-sphere:cell:3:2
                x: 3
                y: 2
                isHidden: false
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::CRYSTAL_SPHERE_SCREEN_ID);
    let crystal_sphere =
        &payload["run"]["currentRoom"]["event"]["playerStates"][0]["crystalSphere"];
    assert_eq!(crystal_sphere["selectedTool"], "big");
    assert_eq!(crystal_sphere["divinationsRemaining"], 2);
    assert_eq!(crystal_sphere["cells"][0]["id"], "crystal-sphere:cell:3:2");
    assert_eq!(crystal_sphere["cells"][0]["x"], 3);
    assert_eq!(crystal_sphere["cells"][0]["y"], 2);
    assert_eq!(crystal_sphere["cells"][0]["isHidden"], false);
}

#[test]
fn prepare_fixture_rejects_multiple_chosen_crystal_sphere_options() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE
              wasChosen: true
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("multiple chosen options"));
}

#[test]
fn prepare_fixture_rejects_invalid_crystal_sphere_cell_id_or_coordinate() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
          crystalSphere:
            selectedTool: big
            divinationsRemaining: 2
            cells:
              - id: crystal-sphere:cell:3:2
                x: 12
                y: 2
                isHidden: false
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("outside the 11x11 grid"));

    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
          crystalSphere:
            selectedTool: big
            divinationsRemaining: 2
            cells:
              - id: crystal-sphere:cell:4:2
                x: 3
                y: 2
                isHidden: false
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("cell id does not match x/y"));
}

#[test]
fn prepare_fixture_rejects_duplicate_crystal_sphere_cells() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: true
          crystalSphere:
            selectedTool: big
            divinationsRemaining: 2
            cells:
              - id: crystal-sphere:cell:3:2
                x: 3
                y: 2
                isHidden: false
              - id: crystal-sphere:cell:3:2
                x: 3
                y: 2
                isHidden: false
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("cells must be unique"));
}

#[test]
fn prepare_fixture_rejects_crystal_sphere_state_without_chosen_option() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-crystal-sphere-grid
run:
  currentRoom:
    event:
      canonicalEventModelId: CRYSTAL_SPHERE
      playerStates:
        - options:
            - textKey: CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN
              wasChosen: false
          crystalSphere:
            selectedTool: big
            divinationsRemaining: 2
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("requires one chosen event option"));
}

#[test]
fn prepare_fixture_rejects_empty_event_player_state() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-event-room
run:
  currentRoom:
    event:
      canonicalEventModelId: golden-idol
      playerStates:
        - playerId: p1
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("must author options or ancient state"));
}

#[test]
fn prepare_fixture_accepts_treasure_room_recipe_sections() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: treasure
run:
  currentActIndex: 1
  actFloor: 9
  currentRoom:
    treasure: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::TREASURE_ROOM_SCREEN_ID);
    let treasure = &payload["run"]["currentRoom"]["treasure"];
    assert_eq!(treasure["currentRelicsActive"], false);
    assert_eq!(treasure["relicSelectionOpen"], false);
    assert!(treasure["currentRelics"].is_null());
    assert!(treasure["canProceed"].is_null());
}

#[test]
fn prepare_fixture_rejects_current_relics_for_treasure_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: treasure
run:
  currentRoom:
    treasure:
      currentRelicsActive: true
      currentRelics:
        - modelId: anchor
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.currentRoom.treasure.currentRelics"
    );
}

#[test]
fn prepare_fixture_accepts_opened_treasure_room_with_proceed_override() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: opened-treasure-room
run:
  currentRoom:
    treasure:
      currentRelicsActive: true
      canProceed: true
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let treasure = &payload["run"]["currentRoom"]["treasure"];
    assert_eq!(treasure["currentRelicsActive"], true);
    assert_eq!(treasure["canProceed"], true);
}

#[test]
fn prepare_fixture_rejects_can_proceed_for_closed_treasure_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: treasure
run:
  currentRoom:
    treasure:
      canProceed: true
"#,
    );

    let payload = error.payload;
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.currentRoom.treasure.canProceed"
    );
}

#[test]
fn prepare_fixture_accepts_relic_selection_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-relic-selection
run:
  currentRoom:
    treasure:
      relicSelectionOpen: true
      currentRelics:
        - modelId: ANCHOR
        - modelId: BAG_OF_MARBLES
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::RELIC_SELECTION_SCREEN_ID);
    let treasure = &payload["run"]["currentRoom"]["treasure"];
    assert_eq!(treasure["relicSelectionOpen"], true);
    assert_eq!(treasure["currentRelics"][0]["modelId"], "ANCHOR");
}

#[test]
fn prepare_fixture_accepts_multiplayer_treasure_room_with_player_votes() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: treasure-opened-multi
run:
  players:
    - id: "p:1"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    treasure:
      currentRelicsActive: true
      canProceed: true
      playerVotes:
        - playerId: "p:2"
          index: 0
        - playerId: "p:1"
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::TREASURE_ROOM_SCREEN_ID);
    let treasure = &payload["run"]["currentRoom"]["treasure"];
    assert_eq!(treasure["playerVotes"][0]["playerId"], "p:2");
    assert_eq!(treasure["playerVotes"][0]["index"], 0);
    assert!(treasure["playerVotes"][1]["index"].is_null());
}

#[test]
fn prepare_fixture_rejects_player_votes_for_closed_treasure_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: treasure
run:
  currentRoom:
    treasure:
      playerVotes:
        - playerId: p1
"#,
    );

    let payload = error.payload;
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.currentRoom.treasure.playerVotes"
    );
}

#[test]
fn prepare_fixture_accepts_shop_inventory_and_player_gold() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-shop-inventory
run:
  actFloor: 5
  players:
    - id: p1
      characterId: ironclad
      gold: 175
  currentRoom:
    shop:
      inventory:
        characterCardEntries:
          - card:
              modelId: strike
          - card:
              modelId: bash
        relicEntries:
          - modelId: anchor
        potionEntries:
          - modelId: fire-potion
        cardRemovalEntry:
          used: false
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::SHOP_SCREEN_ID);
    assert_eq!(payload["run"]["players"][0]["gold"], 175);
    let inventory = &payload["run"]["currentRoom"]["shop"]["inventory"];
    assert_eq!(
        inventory["characterCardEntries"][0]["card"]["modelId"],
        "strike"
    );
    assert_eq!(inventory["relicEntries"][0]["modelId"], "anchor");
    assert_eq!(inventory["potionEntries"][0]["modelId"], "fire-potion");
    assert_eq!(inventory["cardRemovalEntry"]["used"], false);
}

#[test]
fn prepare_fixture_accepts_player_potions() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-potion-target
run:
  players:
    - id: "p:1"
      characterId: ironclad
      potions:
        - modelId: FIRE_POTION
        - modelId: POTION_OF_BINDING
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    assert_eq!(
        payload["run"]["players"][0]["potions"],
        json!([{"modelId": "FIRE_POTION"}, {"modelId": "POTION_OF_BINDING"}])
    );
    assert!(payload["run"]["players"][1]["potions"].is_null());
}

#[test]
fn prepare_fixture_accepts_selected_potion_view() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-potion-press
run:
  view:
    playerId: "p:1"
    selectedPotion:
      slotIndex: 0
      mode: popup
  players:
    - id: "p:1"
      characterId: ironclad
      potions:
        - modelId: FIRE_POTION
      isLocal: true
      isHostLocalSeat: false
      slotId: 0
    - id: "p:2"
      characterId: ironclad
      isLocal: true
      isHostLocalSeat: true
      slotId: 1
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["run"]["view"]["playerId"], "p:1");
    assert_eq!(payload["run"]["view"]["selectedPotion"]["slotIndex"], 0);
    assert_eq!(payload["run"]["view"]["selectedPotion"]["mode"], "popup");
}

#[test]
fn prepare_fixture_accepts_selected_card_view_with_default_owner() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-card-strike
run:
  view:
    selectedCard:
      cardModelId: STRIKE_IRONCLAD
  players: 2
  currentRoom:
    combat:
      encounterId: QUEEN_BOSS
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let view_player_id = payload["run"]["view"]["playerId"].clone();
    assert_eq!(
        payload["run"]["view"]["selectedCard"]["playerId"],
        view_player_id
    );
    assert_eq!(
        payload["run"]["view"]["selectedCard"]["cardModelId"],
        "STRIKE_IRONCLAD"
    );
}

#[test]
fn prepare_fixture_rejects_selected_card_for_unknown_player() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-card-bad-owner
run:
  view:
    selectedCard:
      playerId: "p:9"
      cardModelId: STRIKE_IRONCLAD
  players: 2
  currentRoom:
    combat:
      encounterId: QUEEN_BOSS
"#,
    );

    let rendered = format!("{error:?}");
    assert!(
        rendered.contains("fixture run.view.selectedCard.playerId must refer to a declared player")
    );
}

#[test]
fn prepare_fixture_rejects_empty_selected_card_model_id() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-card-empty-model
run:
  view:
    selectedCard:
      cardModelId: ""
  players: 2
  currentRoom:
    combat:
      encounterId: QUEEN_BOSS
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("fixture run.view.selectedCard.cardModelId must not be empty"));
}

#[test]
fn prepare_fixture_rejects_unknown_selected_potion_mode() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-potion-bad-ui
run:
  view:
    selectedPotion:
      slotIndex: 0
      mode: focused
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("fixture run.view.selectedPotion.mode is not supported"));
}

#[test]
fn prepare_fixture_accepts_card_pile_view_with_default_owner() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-draw-pile-open
run:
  view:
    capstone:
      cardPileView:
        pileType: draw
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let card_pile_view = &payload["run"]["view"]["capstone"]["cardPileView"];
    assert_eq!(card_pile_view["pileType"], "draw");
    assert_eq!(card_pile_view["playerId"], "p:1");
}

#[test]
fn prepare_fixture_accepts_deck_view_with_defaults() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-deck-pile
run:
  view:
    capstone:
      deckView:
        sort:
          - by: type
          - by: cost
            direction: descending
        showUpgrades: true
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let deck_view = &payload["run"]["view"]["capstone"]["deckView"];
    assert_eq!(deck_view["playerId"], "p:1");
    assert_eq!(deck_view["showUpgrades"], true);
    assert_eq!(deck_view["sort"][0]["by"], "type");
    assert_eq!(deck_view["sort"][0]["direction"], "ascending");
    assert_eq!(deck_view["sort"][1]["by"], "cost");
    assert_eq!(deck_view["sort"][1]["direction"], "descending");
    assert!(payload["run"]["view"]["capstone"]["cardPileView"].is_null());
}

#[test]
fn prepare_fixture_rejects_deck_view_with_card_pile_view() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-both-views
run:
  view:
    capstone:
      cardPileView:
        pileType: draw
      deckView: {}
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("may open only one of cardPileView or deckView"));
}

#[test]
fn prepare_fixture_rejects_unknown_deck_view_sort_key() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-bad-deck-sort
run:
  view:
    capstone:
      deckView:
        sort:
          - by: rarity
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("deckView.sort[].by is not supported"));
}

#[test]
fn prepare_fixture_accepts_inspect_relic_view() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-inspect-relic-open
run:
  view:
    inspectRelic:
      relicModelId: BURNING_BLOOD
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(
        payload["run"]["view"]["inspectRelic"]["relicModelId"],
        "BURNING_BLOOD"
    );
}

#[test]
fn prepare_fixture_rejects_empty_inspect_relic_model_id() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-inspect-relic-empty
run:
  view:
    inspectRelic:
      relicModelId: "  "
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("inspectRelic.relicModelId must not be empty"));
}

#[test]
fn prepare_fixture_rejects_unknown_card_pile_type() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-bad-pile
run:
  view:
    capstone:
      cardPileView:
        pileType: hand
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("cardPileView.pileType is not supported"));
}

#[test]
fn prepare_fixture_rejects_load_run_lobby_without_stable_player_ids() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-load-run-lobby
characterSelect:
  kind: load-run
  lobby:
    players:
      - id: local-player
        characterId: ironclad
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "characterSelect.lobby.players[0].id"
    );
}

#[test]
fn prepare_fixture_accepts_load_run_lobby_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-load-run-lobby
characterSelect:
  kind: load-run
  lobby:
    seed: "  seeded-run  "
    hostPlayerId: "p:100"
    players:
      - id: "p:100"
        characterId: ironclad
        isReady: false
        slotId: 0
      - id: "p:200"
        characterId: silent
        isReady: true
        slotId: 1
  characterButtons:
    - characterId: defect
      isLocked: true
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::LOAD_RUN_LOBBY_SCREEN_ID);
    let character_select = &payload["characterSelect"];
    assert_eq!(character_select["kind"], "load-run");
    assert_eq!(character_select["lobby"]["seed"], "seeded-run");
    assert_eq!(character_select["lobby"]["localPlayerId"], "p:100");
    assert_eq!(character_select["lobby"]["hostPlayerId"], "p:100");
    assert_eq!(character_select["lobby"]["players"][1]["isReady"], true);
    assert_eq!(
        character_select["characterButtons"][0]["characterId"],
        "defect"
    );
    assert_eq!(character_select["characterButtons"][0]["isLocked"], true);
}

#[test]
fn prepare_fixture_accepts_two_ironclad_lobby_fixture() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: two-ironclad-lobby
characterSelect:
  lobby:
    hostPlayerId: "p:1"
    players:
      - id: "p:1"
        characterId: ironclad
        isReady: false
        slotId: 0
      - id: "p:2"
        characterId: ironclad
        isHostLocalSeat: true
        isReady: true
        slotId: 1
  characterButtons:
    - characterId: silent
      isLocked: true
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::START_RUN_LOBBY_SCREEN_ID);
    let lobby = &payload["characterSelect"]["lobby"];
    assert_eq!(lobby["players"][1]["isHostLocalSeat"], true);
    assert_eq!(lobby["hostPlayerId"], "p:1");
    assert_eq!(lobby["localPlayerId"], "p:1");
}

#[test]
fn prepare_fixture_rejects_host_local_seat_without_host_player_id() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-lobby
characterSelect:
  lobby:
    players:
      - id: "p:1"
        characterId: ironclad
      - id: "p:2"
        characterId: ironclad
        isHostLocalSeat: true
"#,
    );

    let payload = error.payload;
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "characterSelect.lobby.hostPlayerId"
    );
}

#[test]
fn prepare_fixture_accepts_bundle_selection_overlay() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-bundle-selection
run:
  actFloor: 6
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - bundleSelection:
            canConfirm: true
            bundles:
              - id: offensive-pack
                cardIds:
                  - bash
                  - anger
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::BUNDLE_SELECTION_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["bundleSelection"];
    assert_eq!(overlay["bundles"][0]["id"], "offensive-pack");
    assert_eq!(overlay["bundles"][0]["cardIds"], json!(["bash", "anger"]));
    assert_eq!(overlay["canConfirm"], true);
}

#[test]
fn prepare_fixture_accepts_choose_a_card_overlay() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-card-selection
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - chooseACard:
            canSkip: true
            cards:
              - modelId: BASH
              - modelId: ANGER
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::CARD_REWARD_SELECTION_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["chooseACard"];
    assert_eq!(overlay["cards"][0]["modelId"], "BASH");
    assert_eq!(overlay["canSkip"], true);
}

#[test]
fn prepare_fixture_accepts_empty_rewards_overlay() {
    // Backward-compat: the legacy `rewards: {}` overlay still normalizes.
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-rewards
run:
  players:
    - id: p1
      overlays:
        - rewards: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::REWARDS_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["rewards"];
    assert!(overlay.is_object());
    assert!(overlay["items"].is_null());
}

#[test]
fn prepare_fixture_accepts_authored_rewards_items() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rewards-all
run:
  players:
    - id: p1
      relics:
        - modelId: PRAYER_WHEEL
      overlays:
        - rewards:
            roomType: elite
            items:
              - gold: 30
              - potion: { modelId: FIRE_POTION }
              - relic: { modelId: ANCHOR }
              - relic: { rarity: rare }
              - cardRemoval: true
              - specialCard: { modelId: IMMOLATE }
              - card: { modelIds: [STRIKE, ANGER, IMMOLATE] }
              - card: { roomType: monster, count: 3 }
              - linked:
                  items:
                    - gold: 10
                    - relic: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::REWARDS_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["rewards"];
    assert_eq!(overlay["roomType"], "elite");
    let items = overlay["items"].as_array().expect("items array");
    assert_eq!(items.len(), 9);
    assert_eq!(items[0]["gold"], 30);
    assert_eq!(items[1]["potion"]["modelId"], "FIRE_POTION");
    assert_eq!(items[2]["relic"]["modelId"], "ANCHOR");
    assert_eq!(items[3]["relic"]["rarity"], "rare");
    assert_eq!(items[4]["cardRemoval"], true);
    assert_eq!(items[5]["specialCard"]["modelId"], "IMMOLATE");
    assert_eq!(items[6]["card"]["modelIds"][2], "IMMOLATE");
    assert_eq!(items[7]["card"]["roomType"], "monster");
    assert_eq!(items[7]["card"]["count"], 3);
    assert_eq!(items[8]["linked"]["items"][0]["gold"], 10);
    assert_eq!(
        payload["run"]["players"][0]["relics"][0]["modelId"],
        "PRAYER_WHEEL"
    );
}

#[test]
fn prepare_fixture_rejects_reward_item_with_two_kinds() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rewards-bad
run:
  players:
    - id: p1
      overlays:
        - rewards:
            items:
              - gold: 30
                cardRemoval: true
"#,
    );

    let rendered = format!("{}", error.payload);
    assert!(
        rendered.contains("exactly one reward kind"),
        "unexpected error: {rendered}"
    );
}

#[test]
fn prepare_fixture_rejects_invalid_reward_room_type() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rewards-bad-room
run:
  players:
    - id: p1
      overlays:
        - rewards:
            roomType: shop
            items:
              - gold: 10
"#,
    );

    let rendered = format!("{}", error.payload);
    assert!(
        rendered.contains("roomType"),
        "unexpected error: {rendered}"
    );
}

#[test]
fn prepare_fixture_accepts_combat_simple_grid_overlay() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-simple-grid
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - simpleCardSelection:
            cards:
              - modelId: STRIKE
              - modelId: DEFEND
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    let overlay = &payload["run"]["players"][0]["overlays"][0]["simpleCardSelection"];
    assert_eq!(overlay["cards"][0]["modelId"], "STRIKE");
}

#[test]
fn prepare_fixture_accepts_combat_bundle_overlay() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-bundle
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - bundleSelection:
            bundles:
              - id: offensive
                cardIds: [STRIKE, ANGER]
              - id: defensive
                cardIds: [DEFEND, IRON_WAVE]
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    let overlay = &payload["run"]["players"][0]["overlays"][0]["bundleSelection"];
    assert_eq!(overlay["bundles"][0]["id"], "offensive");
    assert_eq!(overlay["bundles"][1]["cardIds"][0], "DEFEND");
}

#[test]
fn prepare_fixture_rejects_simple_grid_overlay_combined_with_non_combat_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: shop-simple-grid
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - simpleCardSelection:
            cards:
              - modelId: STRIKE
  currentRoom:
    shop: {}
"#,
    );
    let rendered = format!("{error:?}");
    assert!(
        rendered.contains("overlays cannot be combined with this run.currentRoom kind"),
        "unexpected error: {rendered}"
    );
}

#[test]
fn prepare_fixture_accepts_hand_upgrade_select_mode() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-hand-upgrade
run:
  players:
    - id: p1
      characterId: ironclad
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
  view:
    handSelection:
      sourceModelId: ARMAMENTS
      mode: upgrade-select
      minSelect: 1
      maxSelect: 1
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    let hand = &payload["run"]["view"]["handSelection"];
    assert_eq!(hand["mode"], "upgrade-select");
    assert_eq!(hand["sourceModelId"], "ARMAMENTS");
}

#[test]
fn prepare_fixture_rejects_unknown_hand_selection_mode() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-hand-bad-mode
run:
  players:
    - id: p1
      characterId: ironclad
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
  view:
    handSelection:
      mode: bogus-mode
"#,
    );
    let rendered = format!("{error:?}");
    assert!(
        rendered.contains("handSelection.mode is not supported"),
        "unexpected error: {rendered}"
    );
}

#[test]
fn prepare_fixture_derives_deck_card_selection_screens_from_kind() {
    for (kind, expected_screen) in [
        ("upgrade", bridge::DECK_UPGRADE_SELECTION_SCREEN_ID),
        ("transform", bridge::DECK_TRANSFORM_SELECTION_SCREEN_ID),
        ("enchant", bridge::DECK_ENCHANT_SELECTION_SCREEN_ID),
        ("select", bridge::DECK_CARD_SELECTION_SCREEN_ID),
    ] {
        let enchant_fields = if kind == "enchant" {
            "\n            enchantmentId: SWIFT\n            enchantAmount: 1"
        } else {
            ""
        };
        let raw = format!(
            r#"schemaVersion: spirectl.fixture/v0
name: deck-selection
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - deckCardSelection:
            kind: {kind}
            canConfirm: true{enchant_fields}
            cards:
              - modelId: BASH
"#
        );
        let prepared = prepared_fixture(&raw);
        let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
        assert_eq!(payload["screen"], expected_screen, "kind={kind}");
    }
}

#[test]
fn prepare_fixture_rejects_enchant_fields_for_non_enchant_kind() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: deck-selection
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - deckCardSelection:
            kind: upgrade
            enchantmentId: SWIFT
            cards:
              - modelId: BASH
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("enchantment fields require kind: enchant"));
}

#[test]
fn prepare_fixture_accepts_rest_site_composed_deck_card_selection() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rest-site-smith
run:
  actFloor: 6
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - deckCardSelection:
            kind: upgrade
            canConfirm: true
  currentRoom:
    restSite: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::REST_SITE_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["deckCardSelection"];
    assert_eq!(overlay["kind"], "upgrade");
    assert_eq!(overlay["canConfirm"], true);
    assert!(overlay["cards"].is_null());
}

#[test]
fn prepare_fixture_rejects_authored_cards_for_rest_site_composed_overlay() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rest-site-smith
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - deckCardSelection:
            kind: upgrade
            cards:
              - modelId: BASH
  currentRoom:
    restSite: {}
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("read cards from the live player deck"));
}

#[test]
fn prepare_fixture_accepts_event_composed_deck_enchant_selection() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: field-of-man-sized-holes-enchant
run:
  actFloor: 6
  players:
    - id: p1
      characterId: ironclad
      deck:
        cards:
          - modelId: BASH
      overlays:
        - deckCardSelection:
            kind: enchant
            enchantmentId: PERFECT_FIT
            enchantAmount: 1
            canConfirm: true
  currentRoom:
    event:
      canonicalEventModelId: FIELD_OF_MAN_SIZED_HOLES
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::EVENT_ROOM_SCREEN_ID);
    let overlay = &payload["run"]["players"][0]["overlays"][0]["deckCardSelection"];
    assert_eq!(overlay["kind"], "enchant");
    assert_eq!(overlay["enchantmentId"], "PERFECT_FIT");
    assert_eq!(overlay["enchantAmount"], 1);
    assert!(overlay["cards"].is_null());
}

#[test]
fn prepare_fixture_rejects_authored_cards_for_event_composed_overlay() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: field-of-man-sized-holes-enchant
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - deckCardSelection:
            kind: enchant
            enchantmentId: PERFECT_FIT
            cards:
              - modelId: BASH
  currentRoom:
    event:
      canonicalEventModelId: FIELD_OF_MAN_SIZED_HOLES
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("read cards from the live player deck"));
}

#[test]
fn prepare_fixture_accepts_rest_site_player_state_options() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: rest-site-all-options
run:
  currentRoom:
    restSite:
      playerStates:
        - options:
            - optionId: CLONE
            - optionId: HATCH
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let state = &payload["run"]["currentRoom"]["restSite"]["playerStates"][0];
    assert_eq!(state["playerId"], "p:1");
    assert_eq!(state["options"][0]["optionId"], "CLONE");
    assert_eq!(state["options"][1]["optionId"], "HATCH");
}

#[test]
fn prepare_fixture_rejects_overlay_combined_with_non_rest_site_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-combo
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - chooseACard:
            cards:
              - modelId: BASH
  currentRoom:
    shop: {}
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("overlays cannot be combined with this run.currentRoom kind"));
}

#[test]
fn prepare_fixture_rejects_multiple_room_kinds() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-rooms
run:
  currentRoom:
    shop: {}
    treasure: {}
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["details"][0]["field"], "run.currentRoom");
}

#[test]
fn prepare_fixture_accepts_card_overlay_recipe_sections() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-card-overlay
run:
  actFloor: 3
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - cardOverlay:
            policy: blocking
            sourceScreen: combat
            cards:
              - modelId: bash
            close:
              id: card-overlay:close
              label: Close
              enabled: true
              intent: close-overlay
            followThrough:
              controls:
                - id: card-overlay:confirm
                  label: Confirm
                  enabled: false
                  intent: confirm
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "card-overlay");
    let overlay = &payload["run"]["players"][0]["overlays"][0]["cardOverlay"];
    assert_eq!(overlay["policy"], "blocking");
    assert_eq!(overlay["cards"][0]["modelId"], "bash");
    assert_eq!(overlay["close"]["id"], "card-overlay:close");
    assert_eq!(
        overlay["followThrough"]["controls"][0]["id"],
        "card-overlay:confirm"
    );
}

#[test]
fn prepare_fixture_derives_passive_card_overlay_from_policy() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: passive-card-overlay
run:
  actFloor: 5
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - cardOverlay:
            policy: passive
            sourceScreen: shop
            cards:
              - modelId: zap
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "passive-card-overlay");
    let overlay = &payload["run"]["players"][0]["overlays"][0]["cardOverlay"];
    assert_eq!(overlay["sourceScreen"], "shop");
}

#[test]
fn prepare_fixture_rejects_card_overlay_without_cards() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-card-overlay
run:
  players:
    - id: p1
      characterId: ironclad
      overlays:
        - cardOverlay:
            policy: blocking
            sourceScreen: combat
            cards: []
"#,
    );

    let payload = error.payload;
    assert_eq!(
        payload["error"]["details"][0]["field"],
        "run.players[0].overlays[0].cardOverlay.cards"
    );
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "invalid-authored-value"
    );
}

#[test]
fn prepare_fixture_rejects_multiplayer_fields_on_single_player_screens() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-shop
run:
  players:
    - id: p1
      characterId: ironclad
      isLocal: true
  currentRoom:
    shop: {}
"#,
    );

    let rendered = format!("{error:?}");
    assert!(rendered.contains("unsupported"));
    assert!(rendered.contains("player fields"));
}

#[test]
fn prepare_fixture_rejects_run_act_floor_zero() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-floor
run:
  actFloor: 0
  currentRoom:
    shop: {}
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["details"][0]["field"], "run.actFloor");
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "invalid_run_floor"
    );
}

#[test]
fn prepare_fixture_rejects_hp_exceeding_max_hp() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-hp
run:
  players:
    - id: p1
      characterId: ironclad
      creature:
        currentHp: 90
        maxHp: 80
  currentRoom:
    shop: {}
"#,
    );

    let payload = error.payload;
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "hp_exceeds_max_hp"
    );
}

#[test]
fn prepare_fixture_accepts_combat_fixture_with_deck_and_relics() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-combat
run:
  players:
    - id: p1
      characterId: IRONCLAD
      creature:
        currentHp: 67
        maxHp: 80
      relics:
        - modelId: SHOVEL
      deck:
        cards:
          - modelId: BYRDONIS_EGG
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    assert_eq!(
        payload["run"]["currentRoom"]["combat"]["encounterId"],
        "NIBBITS_NORMAL"
    );
    let player = &payload["run"]["players"][0];
    assert_eq!(player["creature"]["currentHp"], 67);
    assert_eq!(player["relics"][0]["modelId"], "SHOVEL");
    assert_eq!(player["deck"]["cards"][0]["modelId"], "BYRDONIS_EGG");
}

#[test]
fn prepare_fixture_accepts_authored_combat_piles() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-many-cards
run:
  players:
    - id: p1
      characterId: IRONCLAD
      combat:
        hand:
          cards:
            - modelId: NEOWS_FURY
        drawPile:
          cards:
            - modelId: STRIKE_IRONCLAD
        discardPile:
          cards:
            - modelId: BURN
        exhaustPile:
          cards:
            - modelId: REGRET
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let combat = &payload["run"]["players"][0]["combat"];
    assert_eq!(combat["hand"]["cards"][0]["modelId"], "NEOWS_FURY");
    assert_eq!(combat["drawPile"]["cards"][0]["modelId"], "STRIKE_IRONCLAD");
    assert_eq!(combat["discardPile"]["cards"][0]["modelId"], "BURN");
    assert_eq!(combat["exhaustPile"]["cards"][0]["modelId"], "REGRET");
}

#[test]
fn prepare_fixture_accepts_authored_combat_card_afflictions() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-afflictions
run:
  players:
    - id: p1
      characterId: IRONCLAD
      combat:
        hand:
          cards:
            - modelId: BASH
              afflictionId: bound
            - modelId: ANGER
              afflictionId: entangled
              afflictionAmount: 3
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let hand = &payload["run"]["players"][0]["combat"]["hand"]["cards"];
    assert_eq!(hand[0]["modelId"], "BASH");
    assert_eq!(hand[0]["afflictionId"], "bound");
    assert_eq!(hand[0]["afflictionAmount"], 1);
    assert_eq!(hand[1]["modelId"], "ANGER");
    assert_eq!(hand[1]["afflictionId"], "entangled");
    assert_eq!(hand[1]["afflictionAmount"], 3);
}

#[test]
fn prepare_fixture_accepts_authored_combat_transient_effects() {
    // Authored cardUpgrade transient effects (e.g. STONE_CRACKER's center preview) carry to
    // the wire doc verbatim so the renderer can be validated without catching a live frame.
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-stone-cracker
run:
  players:
    - id: p1
      characterId: IRONCLAD
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
      transientEffects:
        - id: "fx:upgrade:1"
          kind: cardUpgrade
          cardModelId: STRIKE_IRONCLAD
          sourceRelicModelId: STONE_CRACKER
          spawnedAtMs: 0
        - id: "fx:vfx:1"
          kind: vfx
          scenePath: vfx/hit_spark_vfx
          anchorCreatureId: "creature:1"
          spawnedAtMs: 0
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let effect = &payload["run"]["currentRoom"]["combat"]["transientEffects"][0];
    assert_eq!(effect["id"], "fx:upgrade:1");
    assert_eq!(effect["kind"], "cardUpgrade");
    assert_eq!(effect["cardModelId"], "STRIKE_IRONCLAD");
    assert_eq!(effect["sourceRelicModelId"], "STONE_CRACKER");
    // Omitted optionals stay absent (skip_serializing_if).
    assert!(effect.get("anchorCreatureId").is_none());
    assert!(effect.get("amount").is_none());
    // The native VFX backbone: kind: vfx carries the scene to mount + its anchor.
    let vfx = &payload["run"]["currentRoom"]["combat"]["transientEffects"][1];
    assert_eq!(vfx["id"], "fx:vfx:1");
    assert_eq!(vfx["kind"], "vfx");
    assert_eq!(vfx["scenePath"], "vfx/hit_spark_vfx");
    assert_eq!(vfx["anchorCreatureId"], "creature:1");
}

#[test]
fn prepare_fixture_rejects_transient_effect_without_id_or_kind() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-bad-transient
run:
  players:
    - id: p1
      characterId: IRONCLAD
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
      transientEffects:
        - id: ""
          kind: cardUpgrade
"#,
    );
    assert!(
        format!("{error:?}").contains("transientEffects"),
        "expected a transientEffects validation error, got {error:?}"
    );
}

#[test]
fn prepare_fixture_accepts_every_repo_fixture_file() {
    let fixtures_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures");
    let mut checked = 0usize;
    for entry in fs::read_dir(&fixtures_dir).expect("fixtures dir") {
        let path = entry.expect("fixtures dir entry").path();
        if path.extension().and_then(|ext| ext.to_str()) != Some("yaml") {
            continue;
        }
        prepare_fixture(&path, None)
            .unwrap_or_else(|error| panic!("{} should normalize: {error:?}", path.display()));
        checked += 1;
    }
    assert!(
        checked >= 40,
        "expected the repo fixture catalog, found {checked}"
    );
}

#[test]
fn prepare_fixture_rejects_exact_sidecar_fields_with_reason_code() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: invalid-sidecar
exactSidecar:
  path: .sts2/fixtures/native-save.json
run:
  currentRoom:
    shop: {}
"#,
    );

    let payload = error.payload;
    assert_eq!(payload["error"]["details"][0]["fieldPath"], "exactSidecar");
    assert_eq!(
        payload["error"]["details"][0]["reasonCode"],
        "unsupported-recipe-field"
    );
}

#[test]
fn prepare_fixture_rejects_hidden_runtime_fields_with_reason_code() {
    for (field, reason) in [
        ("rngContinuation: 42", "unsupported-hidden-state"),
        ("animationState:\n  node: tween", "unsupported-hidden-state"),
        (
            "enemyAiHistory:\n  lagavulin: sleeping",
            "unsupported-hidden-state",
        ),
        (
            "transientUi:\n  nodePath: /root/Shop",
            "unsupported-hidden-state",
        ),
        (
            "scenarioArtifact:\n  path: trace.json",
            "unsupported-recipe-field",
        ),
        (
            "remoteClients:\n  - id: fake-remote",
            "degraded-local-multiplayer",
        ),
    ] {
        let raw = format!(
            r#"schemaVersion: spirectl.fixture/v0
name: invalid-hidden
run:
  currentRoom:
    shop: {{}}
{field}
"#
        );
        let error = invalid_fixture(&raw);
        assert_eq!(error.payload["error"]["details"][0]["reasonCode"], reason);
    }
}

#[test]
fn prepare_fixture_accepts_combat_hand_selection_view() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-hand-discard
run:
  players:
    - id: "p:1"
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
  view:
    handSelection:
      sourceModelId: SURVIVOR
      prompt: discard
      minSelect: 1
      maxSelect: 1
      selectedCardIndexes: [0]
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    let hand_selection = &payload["run"]["view"]["handSelection"];
    assert_eq!(hand_selection["sourceModelId"], "SURVIVOR");
    assert_eq!(hand_selection["prompt"], "discard");
    assert_eq!(hand_selection["minSelect"], 1);
    assert_eq!(hand_selection["maxSelect"], 1);
    assert_eq!(hand_selection["selectedCardIndexes"][0], 0);
}

#[test]
fn prepare_fixture_rejects_hand_selection_without_combat_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: hand-selection-no-combat
run:
  players:
    - id: "p:1"
  currentRoom:
    restSite: {}
  view:
    handSelection:
      minSelect: 1
      maxSelect: 1
"#,
    );

    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.view.handSelection"
    );
}

#[test]
fn prepare_fixture_rejects_hand_selection_invalid_bounds() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: hand-selection-bad-bounds
run:
  players:
    - id: "p:1"
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
  view:
    handSelection:
      minSelect: 3
      maxSelect: 1
"#,
    );

    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.view.handSelection"
    );
}

#[test]
fn prepare_fixture_accepts_combat_choose_a_card_overlay() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-choose-a-card
run:
  players:
    - id: "p:1"
      overlays:
        - chooseACard:
            canSkip: true
            cards:
              - modelId: ANGER
              - modelId: SWORD_BOOMERANG
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], "combat");
    assert_eq!(
        payload["run"]["players"][0]["overlays"][0]["chooseACard"]["cards"][0]["modelId"],
        "ANGER"
    );
}

#[test]
fn prepare_fixture_rejects_combat_composed_deck_card_selection_overlay() {
    // chooseACard / simpleCardSelection / bundleSelection compose over combat, but
    // deckCardSelection is a rest-site-only (smith) dialog and must stay rejected.
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-bad-overlay
run:
  players:
    - id: "p:1"
      overlays:
        - deckCardSelection:
            kind: upgrade
            cards:
              - modelId: ANGER
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    );

    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.players[].overlays"
    );
}

#[test]
fn prepare_fixture_accepts_player_orbs_and_slot_count() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-orbs
run:
  players:
    - id: "p:1"
      characterId: DEFECT
      orbSlotCount: 3
      orbs:
        - modelId: LIGHTNING_ORB
        - modelId: PLASMA_ORB
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    let player = &payload["run"]["players"][0];
    assert_eq!(player["orbSlotCount"], 3);
    assert_eq!(player["orbs"][0]["modelId"], "LIGHTNING_ORB");
    assert_eq!(player["orbs"][1]["modelId"], "PLASMA_ORB");
}

#[test]
fn prepare_fixture_rejects_orb_slot_count_below_orb_count() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-orbs-bad
run:
  players:
    - id: "p:1"
      characterId: DEFECT
      orbSlotCount: 1
      orbs:
        - modelId: LIGHTNING_ORB
        - modelId: PLASMA_ORB
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    assert_eq!(error.payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.players[0].orbSlotCount"
    );
}

#[test]
fn prepare_fixture_derives_game_over_screen_from_run_game_over() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-defeat
run:
  actFloor: 3
  players:
    - id: "p:1"
      creature: { currentHp: 0, maxHp: 80 }
  gameOver:
    win: false
    killedByEncounter: NIBBITS_NORMAL
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::GAME_OVER_SCREEN_ID);
    assert_eq!(payload["run"]["gameOver"]["win"], false);
    assert_eq!(
        payload["run"]["gameOver"]["killedByEncounter"],
        "NIBBITS_NORMAL"
    );
    assert!(payload["run"]["currentRoom"].is_null());
}

#[test]
fn prepare_fixture_rejects_game_over_with_current_room() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-defeat-bad
run:
  gameOver:
    win: false
    killedByEncounter: NIBBITS_NORMAL
  currentRoom:
    combat:
      encounterId: NIBBITS_WEAK
"#,
    );

    assert_eq!(error.payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.gameOver"
    );
}

#[test]
fn prepare_fixture_rejects_game_over_win_true() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-victory-unsupported
run:
  gameOver:
    win: true
    killedByEncounter: NIBBITS_NORMAL
"#,
    );

    assert_eq!(error.payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.gameOver.win"
    );
}

#[test]
fn prepare_fixture_rejects_game_over_without_killed_by_encounter() {
    let error = invalid_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: combat-defeat-missing-cause
run:
  gameOver:
    win: false
"#,
    );

    assert_eq!(error.payload["error"]["code"], "invalid_fixture");
    assert_eq!(
        error.payload["error"]["details"][0]["field"],
        "run.gameOver.killedByEncounter"
    );
}

#[test]
fn prepare_fixture_serializes_map_room_travel_to_row() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: traveled-map
run:
  actFloor: 5
  currentRoom:
    mapRoom:
      travelToRow: 5
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::MAP_SCREEN_ID);
    // The authored map-progression knob round-trips into the fixture document the bridge loader reads.
    assert_eq!(payload["run"]["currentRoom"]["mapRoom"]["travelToRow"], 5);
}

#[test]
fn prepare_fixture_omits_map_room_travel_to_row_when_unset() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-map
run:
  currentRoom:
    mapRoom: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::MAP_SCREEN_ID);
    // A start-parked map authors no travelToRow (skip_serializing_if keeps it out of the doc).
    assert!(payload["run"]["currentRoom"]["mapRoom"]["travelToRow"].is_null());
}

#[test]
fn prepare_fixture_serializes_map_room_first_node_and_travel_path() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: map-travel
run:
  currentRoom:
    mapRoom:
      firstNode: ancient
      travelPath:
        - Ancient
        - Monster
        - Unknown:Event
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    assert_eq!(payload["screen"], bridge::MAP_SCREEN_ID);
    // The start-node kind + the typed traveled path round-trip into the fixture document the bridge reads.
    let map_room = &payload["run"]["currentRoom"]["mapRoom"];
    assert_eq!(map_room["firstNode"], "ancient");
    assert_eq!(map_room["travelPath"][0], "Ancient");
    assert_eq!(map_room["travelPath"][2], "Unknown:Event");
}

#[test]
fn prepare_fixture_omits_map_room_first_node_and_travel_path_when_unset() {
    let prepared = prepared_fixture(
        r#"schemaVersion: spirectl.fixture/v0
name: basic-map
run:
  currentRoom:
    mapRoom: {}
"#,
    );

    let payload: Value = serde_json::from_str(&prepared.fixture_json).expect("fixture json");
    // skip_serializing_if keeps the optional map-room knobs out of a start-parked doc.
    assert!(payload["run"]["currentRoom"]["mapRoom"]["firstNode"].is_null());
    assert!(payload["run"]["currentRoom"]["mapRoom"]["travelPath"].is_null());
}
