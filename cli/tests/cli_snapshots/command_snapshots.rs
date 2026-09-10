use super::*;

#[test]
fn models_character_exports_all_mock_models() {
    let response = run(&["sts2", "--json", "models", "characters"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");

    assert_eq!(payload["schemaVersion"], "spirectl.model-catalog-result/v0");
    assert_eq!(payload["family"], "characters");
    assert_eq!(payload["language"], Value::Null);
    assert_eq!(payload["status"], "ok");
    let models = payload["models"].as_array().expect("models");
    assert!(models.iter().any(|model| model["id"] == "IRONCLAD"));
    assert!(models.iter().any(|model| model["id"] == "THE_SILENT"));
    assert!(models.iter().all(|model| model["family"] == "characters"));
    assert_eq!(models[0]["startingRelics"][0], "EmberHeart");
    let ironclad = models
        .iter()
        .find(|model| model["id"] == "IRONCLAD")
        .expect("ironclad model");
    assert_eq!(
        ironclad["visualsAssetKey"],
        "model://characters/ironclad/visuals"
    );
    assert_eq!(ironclad["iconAssetKey"], "model://characters/ironclad/icon");
    assert_eq!(
        ironclad["iconOutlineAssetKey"],
        "model://characters/ironclad/iconOutline"
    );
    assert_eq!(
        ironclad["energyCounterAssetKey"],
        "model://characters/ironclad/energyCounter"
    );
    assert_eq!(
        ironclad["unlockText"],
        json!({"table": "characters", "key": "IRONCLAD.unlockText"})
    );
    assert_eq!(
        ironclad["mapMarkerAssetKey"],
        "model://characters/ironclad/mapMarker"
    );
    assert_eq!(
        ironclad["characterSelectBgSpineStillAssetKey"],
        "model://characters/ironclad/characterSelectBgSpineStill"
    );
    assert_eq!(
        ironclad["characterSelectBgPath"],
        "res://scenes/screens/char_select/char_select_bg_ironclad.tscn"
    );
    assert_eq!(
        ironclad["iconPath"],
        "res://images/characters/ironclad/icon.png"
    );
    assert_eq!(
        ironclad["iconOutlinePath"],
        "res://images/characters/ironclad/icon_outline.png"
    );
    assert_eq!(
        ironclad["energyCounterPath"],
        "res://scenes/character_energy/ironclad_energy_counter.tscn"
    );
    assert_eq!(
        ironclad["merchantAnimPath"],
        "res://scenes/character_animations/ironclad_merchant.tscn"
    );
    assert_eq!(
        ironclad["restSiteAnimPath"],
        "res://scenes/character_animations/ironclad_rest_site.tscn"
    );
    assert_eq!(
        ironclad["characterSelectIconPath"],
        "res://images/characters/ironclad/select_icon.png"
    );
    assert_eq!(
        ironclad["characterSelectLockedIconPath"],
        "res://images/characters/ironclad/select_locked.png"
    );
    assert_eq!(
        ironclad["mapMarkerPath"],
        "res://images/characters/ironclad/map_marker.png"
    );
    for removed in ["visualsPath", "characterSelectBg"] {
        assert!(
            ironclad.get(removed).is_none(),
            "models characters should not expose removed field {removed}"
        );
    }
    assert!(
        !ironclad
            .as_object()
            .expect("character object")
            .iter()
            .filter(|(key, _)| key.ends_with("AssetKey"))
            .any(|(_, value)| value.as_str().is_some_and(|text| text.contains(".tscn"))),
        "models characters should keep asset keys opaque rather than direct .tscn paths"
    );
}

#[test]
fn models_language_option_is_forwarded_to_catalog_request() {
    let response = run(&[
        "sts2",
        "--json",
        "models",
        "--language",
        "esp",
        "characters",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");

    assert_eq!(payload["family"], "characters");
    assert_eq!(payload["language"], "esp");
    assert_eq!(payload["status"], "ok");
    assert!(payload["models"].as_array().expect("models").len() >= 2);
    assert_eq!(payload["models"][0]["title"], "The Bulwark");
}

#[test]
fn models_auto_language_resolves_with_effective_catalog_language() {
    let response = run(&[
        "sts2",
        "--json",
        "models",
        "--language",
        "auto",
        "characters",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");

    assert_eq!(payload["language"], "mock");
    assert_eq!(payload["models"][0]["title"], "The Bulwark");
}

#[test]
fn models_character_filters_requested_ids_in_order() {
    let response = run(&[
        "sts2",
        "--json",
        "models",
        "characters",
        "silent",
        "ironclad",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let ids: Vec<_> = payload["models"]
        .as_array()
        .expect("models")
        .iter()
        .map(|model| model["id"].as_str().expect("id"))
        .collect();

    assert_eq!(ids, vec!["THE_SILENT", "IRONCLAD"]);
}

#[test]
fn models_relic_reports_missing_ids_as_partial() {
    let response = run(&[
        "sts2",
        "--json",
        "models",
        "relics",
        "ember-heart",
        "missing-relic",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");

    assert_eq!(payload["family"], "relics");
    assert_eq!(payload["language"], Value::Null);
    assert_eq!(payload["status"], "partial");
    assert_eq!(payload["models"][0]["family"], "relics");
    assert_eq!(payload["models"][0]["id"], "EmberHeart");
    assert_eq!(
        payload["models"][0]["iconAssetKey"],
        "model://relics/burning-blood/icon"
    );
    assert_eq!(
        payload["models"][0]["iconOutlineAssetKey"],
        "model://relics/burning-blood/iconOutline"
    );
    assert_eq!(
        payload["models"][0]["bigIconAssetKey"],
        "model://relics/burning-blood/bigIcon"
    );
    assert_eq!(
        payload["models"][0]["iconPath"],
        "res://images/relics/burning_blood.png"
    );
    assert_eq!(
        payload["models"][0]["iconOutlinePath"],
        "res://images/relics/burning_blood_outline.png"
    );
    assert_eq!(
        payload["models"][0]["bigIconPath"],
        "res://images/relics/burning_blood_big.png"
    );
    assert!(payload["models"][0].get("bigBetaIconPath").is_none());
    assert_eq!(payload["missingIds"][0], "missing-relic");
}

#[test]
fn models_card_exports_catalog_fields() {
    let response = run(&["sts2", "--json", "models", "cards", "strike-ironclad"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let card = &payload["models"][0];

    assert_eq!(payload["family"], "cards");
    assert_eq!(payload["status"], "ok");
    assert_eq!(card["family"], "cards");
    assert_eq!(card["id"], "StrikeIronclad");
    assert_eq!(
        card["title"],
        json!({"table": "cards", "key": "StrikeIronclad.title"})
    );
    assert_eq!(
        card["titleLoc"],
        json!({"table": "cards", "key": "StrikeIronclad.title"})
    );
    assert_eq!(
        card["description"],
        json!({"table": "cards", "key": "StrikeIronclad.description"})
    );
    assert_eq!(
        card["descriptionLoc"],
        json!({"table": "cards", "key": "StrikeIronclad.description"})
    );
    assert_eq!(card["type"], "Attack");
    assert_eq!(card["rarity"], "Basic");
    assert_eq!(card["targetType"], "AnyEnemy");
    assert_eq!(card["energyCost"], 1);
    assert_eq!(card["isEnergyXCost"], false);
    assert_eq!(card["maxUpgradeLevel"], 1);
    assert_eq!(card["upgradable"], true);
    assert_eq!(card["dynamicVars"]["damage"], 6);
    assert_eq!(card["upgrade"]["dynamicVars"]["damage"], 9);
    assert_eq!(card["tags"][0], "Strike");
    assert_eq!(card["imageAssetKey"], "model://cards/strike-ironclad/image");
    assert_eq!(
        card["overlayAssetKey"],
        "model://cards/strike-ironclad/overlay"
    );
}

#[test]
fn models_card_exports_energy_cost_upgrade_preview() {
    let response = run(&["sts2", "--json", "models", "cards", "white-noise"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let card = &payload["models"][0];

    assert_eq!(payload["family"], "cards");
    assert_eq!(payload["status"], "ok");
    assert_eq!(card["id"], "WhiteNoise");
    assert_eq!(card["energyCost"], 1);
    assert_eq!(card["dynamicVars"], json!({}));
    assert_eq!(card["upgrade"]["energyCost"], 0);
}

#[test]
fn models_potion_exports_catalog_fields() {
    let response = run(&["sts2", "--json", "models", "potions", "fire-potion"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let potion = &payload["models"][0];

    assert_eq!(payload["family"], "potions");
    assert_eq!(potion["id"], "FirePotion");
    assert_eq!(potion["rarity"], "Common");
    assert_eq!(potion["usage"], "CombatOnly");
    assert_eq!(potion["targetType"], "AnyEnemy");
    assert_eq!(potion["iconAssetKey"], "model://potions/fire-potion/icon");
    assert_eq!(
        potion["outlineAssetKey"],
        "model://potions/fire-potion/outline"
    );
}

#[test]
fn models_events_include_normal_and_ancient_events() {
    let response = run(&["sts2", "--json", "models", "events"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let models = payload["models"].as_array().expect("models");
    let event = models
        .iter()
        .find(|model| model["id"] == "RoomFullOfCheese")
        .expect("normal event");
    let ancient = models
        .iter()
        .find(|model| model["id"] == "Neow")
        .expect("ancient event");

    assert_eq!(payload["family"], "events");
    assert_eq!(event["kind"], "event");
    assert_eq!(event["layoutType"], "Default");
    assert_eq!(
        event["backgroundSceneAssetKey"],
        "model://events/room-full-of-cheese/backgroundScene"
    );
    assert_eq!(ancient["kind"], "ancient");
    assert_eq!(ancient["layoutType"], "Ancient");
    assert_eq!(
        ancient["epithet"],
        json!({"table": "ancients", "key": "Neow.epithet"})
    );
    assert_eq!(ancient["mapIconAssetKey"], "model://events/neow/mapIcon");
}

#[test]
fn models_ancients_filters_to_ancient_events() {
    let response = run(&["sts2", "--json", "models", "ancients"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let models = payload["models"].as_array().expect("models");

    assert_eq!(payload["family"], "ancients");
    assert_eq!(models.len(), 1);
    assert_eq!(models[0]["id"], "Neow");
    assert_eq!(models[0]["kind"], "ancient");
    assert_eq!(models[0]["family"], "events");
}

#[test]
fn models_act_exports_catalog_fields() {
    let response = run(&["sts2", "--json", "models", "acts", "overgrowth"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let act = &payload["models"][0];

    assert_eq!(payload["family"], "acts");
    assert_eq!(act["id"], "Overgrowth");
    assert_eq!(act["defaultOrder"], 1);
    assert_eq!(act["roomCount"], 15);
    assert_eq!(act["multiplayerRoomCount"], 14);
    assert_eq!(act["ancientEventIds"][0], "Neow");
    assert_eq!(
        act["backgroundSceneAssetKey"],
        "model://acts/overgrowth/backgroundScene"
    );
    assert_eq!(
        act["chestSpineAssetKey"],
        "model://acts/overgrowth/chestSpine"
    );

    // The act exposes its grouped combat-background layer pool (the faithful
    // representation of the randomized layer stack).
    let layers = act["combatBackgroundLayers"]
        .as_array()
        .expect("combatBackgroundLayers array");
    assert_eq!(layers.len(), 3);
    assert_eq!(
        layers[0]["assetKey"],
        "model://acts/overgrowth/backgroundLayer/overgrowth_bg_00_a"
    );
    assert_eq!(layers[0]["bgGroupKey"], "00");
    assert_eq!(layers[0]["isForeground"], false);
    let foreground = layers
        .iter()
        .find(|layer| layer["isForeground"] == true)
        .expect("a foreground layer");
    assert_eq!(foreground["bgGroupKey"], Value::Null);
}

#[test]
fn models_monster_exports_catalog_fields() {
    let response = run(&["sts2", "--json", "models", "monsters", "jaw-worm"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let monster = &payload["models"][0];

    assert_eq!(payload["family"], "monsters");
    assert_eq!(payload["status"], "ok");
    assert_eq!(monster["family"], "monsters");
    assert_eq!(monster["id"], "JawWorm");
    assert_eq!(
        monster["typeName"],
        "MegaCrit.Sts2.Core.Models.Monsters.JawWorm"
    );
    assert_eq!(monster["categorySortingId"], 1);
    assert_eq!(monster["entrySortingId"], 10);
    assert_eq!(monster["shouldReceiveCombatHooks"], true);
    assert_eq!(
        monster["title"],
        json!({"table": "monsters", "key": "JawWorm.title"})
    );
    assert_eq!(monster["minInitialHp"], 40);
    assert_eq!(monster["maxInitialHp"], 44);
    assert_eq!(
        monster["moveNames"][0],
        json!({"table": "monsters", "key": "JawWorm.moveNames.0"})
    );
    assert_eq!(
        monster["assetPaths"][0],
        "res://scenes/monsters/jaw_worm.tscn"
    );
    assert_eq!(
        monster["visualsAssetKey"],
        "model://monsters/jaw-worm/visuals"
    );
    assert_eq!(monster["bestiaryAttackAnimId"], "attack");
    assert_eq!(monster["hasDeathSfx"], true);
    assert_eq!(monster["deathSfx"], "event:/sfx/monster/jaw_worm_death");
    assert_eq!(monster["extraDeathVfxPadding"]["x"], 4.0);
}

#[test]
fn models_encounter_exports_references_and_scene_fields() {
    let response = run(&["sts2", "--json", "models", "encounters", "jaw-worm-weak"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let encounter = &payload["models"][0];

    assert_eq!(payload["family"], "encounters");
    assert_eq!(encounter["id"], "JawWormWeak");
    assert_eq!(encounter["roomType"], "Monster");
    assert_eq!(encounter["isWeak"], true);
    assert_eq!(encounter["isDebugEncounter"], false);
    assert_eq!(encounter["monsterIds"][0], "JawWorm");
    assert_eq!(encounter["monstersWithSlots"][0]["monsterId"], "JawWorm");
    assert_eq!(encounter["monstersWithSlots"][0]["slot"], "M");
    assert_eq!(encounter["slots"][0], "M");
    assert_eq!(encounter["tags"][0], "Weak");
    assert_eq!(encounter["minGoldReward"], 10);
    assert_eq!(encounter["maxGoldReward"], 15);
    assert_eq!(encounter["shouldGiveRewards"], true);
    assert_eq!(encounter["hasScene"], true);
    assert_eq!(
        encounter["sceneAssetKey"],
        "model://encounters/jaw-worm-weak/scene"
    );
    assert_eq!(encounter["cameraOffset"]["y"], -12.0);
    assert_eq!(encounter["cameraScaling"], 1.0);

    // A regular encounter inherits the act's background pool: no custom
    // background, so it carries no background fields.
    assert_eq!(encounter["hasCustomBackground"], false);
    assert_eq!(encounter["backgroundSceneAssetKey"], Value::Null);
    assert_eq!(
        encounter["combatBackgroundLayers"]
            .as_array()
            .expect("combatBackgroundLayers array")
            .len(),
        0
    );
}

#[test]
fn models_custom_background_encounter_exports_layer_pool() {
    let response = run(&["sts2", "--json", "models", "encounters", "vantom-boss"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let boss = payload["models"]
        .as_array()
        .expect("models array")
        .iter()
        .find(|model| model["id"] == "VantomBoss")
        .expect("vantom boss encounter");

    assert_eq!(boss["hasCustomBackground"], true);
    assert_eq!(
        boss["backgroundSceneAssetKey"],
        "model://encounters/vantom-boss/background"
    );
    let layers = boss["combatBackgroundLayers"]
        .as_array()
        .expect("combatBackgroundLayers array");
    assert!(!layers.is_empty());
    assert!(layers.iter().any(|layer| layer["isForeground"] == true));
}

#[test]
fn models_power_and_orb_export_gameplay_and_asset_fields() {
    let power_response = run(&["sts2", "--json", "models", "powers", "strength"]);
    let power_payload: Value = serde_json::from_str(&power_response.stdout).expect("models json");
    let power = &power_payload["models"][0];

    assert_eq!(power_payload["family"], "powers");
    assert_eq!(power["id"], "Strength");
    assert_eq!(
        power["title"],
        json!({"table": "powers", "key": "Strength.title"})
    );
    assert_eq!(power["type"], "Buff");
    assert_eq!(power["stackType"], "Amount");
    assert_eq!(power["allowNegative"], true);
    assert_eq!(power["isVisible"], true);
    assert_eq!(power["hasSmartDescription"], true);
    assert_eq!(power["hasRemoteDescription"], false);
    assert_eq!(power["iconAssetKey"], "model://powers/strength/icon");
    assert_eq!(power["bigIconAssetKey"], "model://powers/strength/bigIcon");

    let orb_response = run(&["sts2", "--json", "models", "orbs", "lightning"]);
    let orb_payload: Value = serde_json::from_str(&orb_response.stdout).expect("models json");
    let orb = &orb_payload["models"][0];

    assert_eq!(orb_payload["family"], "orbs");
    assert_eq!(orb["id"], "Lightning");
    assert_eq!(orb["passiveVal"], 3.0);
    assert_eq!(orb["evokeVal"], 8.0);
    assert_eq!(orb["iconAssetKey"], "model://orbs/lightning/icon");
    assert_eq!(orb["spriteAssetKey"], "model://orbs/lightning/sprite");
}

#[test]
fn models_affliction_and_enchantment_export_card_status_fields() {
    let affliction_response = run(&["sts2", "--json", "models", "afflictions", "hexed"]);
    let affliction_payload: Value =
        serde_json::from_str(&affliction_response.stdout).expect("models json");
    let affliction = &affliction_payload["models"][0];

    assert_eq!(affliction_payload["family"], "afflictions");
    assert_eq!(affliction["id"], "Hexed");
    assert_eq!(
        affliction["extraCardText"],
        json!({"table": "afflictions", "key": "Hexed.extraCardText"})
    );
    assert_eq!(affliction["amount"], 1);
    assert_eq!(affliction["isStackable"], false);
    assert_eq!(affliction["canAfflictUnplayableCards"], false);
    assert_eq!(affliction["hasOverlay"], true);
    assert_eq!(
        affliction["overlayAssetKey"],
        "model://afflictions/hexed/overlay"
    );

    let enchantment_response = run(&["sts2", "--json", "models", "enchantments", "innate"]);
    let enchantment_payload: Value =
        serde_json::from_str(&enchantment_response.stdout).expect("models json");
    let enchantment = &enchantment_payload["models"][0];

    assert_eq!(enchantment_payload["family"], "enchantments");
    assert_eq!(enchantment["id"], "Innate");
    assert_eq!(enchantment["displayAmount"], 1);
    assert_eq!(enchantment["showAmount"], false);
    assert_eq!(enchantment["status"], "Permanent");
    assert_eq!(enchantment["previewOutsideOfCombat"], true);
    assert_eq!(enchantment["shouldGlowGold"], true);
    assert_eq!(
        enchantment["iconAssetKey"],
        "model://enchantments/innate/icon"
    );
}

#[test]
fn models_pool_families_export_content_ids_and_visual_fields() {
    let card_pool_response = run(&[
        "sts2",
        "--json",
        "models",
        "card-pools",
        "ironclad-card-pool",
    ]);
    let card_pool_payload: Value =
        serde_json::from_str(&card_pool_response.stdout).expect("models json");
    let card_pool = &card_pool_payload["models"][0];

    assert_eq!(card_pool_payload["family"], "card-pools");
    assert_eq!(card_pool["id"], "IroncladCardPool");
    assert_eq!(card_pool["cardIds"][0], "StrikeIronclad");
    assert_eq!(card_pool["isColorless"], false);
    assert_eq!(card_pool["energyColorName"], "Red");
    assert_eq!(
        card_pool["energyIconAssetKey"],
        "model://card-pools/ironclad-card-pool/energyIcon"
    );
    assert_eq!(
        card_pool["frameMaterialAssetKey"],
        "model://card-pools/ironclad-card-pool/frameMaterial"
    );

    let relic_pool_response = run(&[
        "sts2",
        "--json",
        "models",
        "relic-pools",
        "ironclad-relic-pool",
    ]);
    let relic_pool_payload: Value =
        serde_json::from_str(&relic_pool_response.stdout).expect("models json");
    let relic_pool = &relic_pool_payload["models"][0];

    assert_eq!(relic_pool_payload["family"], "relic-pools");
    assert_eq!(relic_pool["relicIds"][0], "EmberHeart");
    assert_eq!(relic_pool["labOutlineColor"], "rgb(221 64 56)");

    let potion_pool_response = run(&[
        "sts2",
        "--json",
        "models",
        "potion-pools",
        "shared-potion-pool",
    ]);
    let potion_pool_payload: Value =
        serde_json::from_str(&potion_pool_response.stdout).expect("models json");
    let potion_pool = &potion_pool_payload["models"][0];

    assert_eq!(potion_pool_payload["family"], "potion-pools");
    assert_eq!(potion_pool["potionIds"][0], "FirePotion");
    assert_eq!(potion_pool["energyColorName"], "Shared");
}

#[test]
fn models_modifier_exports_polarity_and_exclusions() {
    let response = run(&[
        "sts2",
        "--json",
        "models",
        "modifiers",
        "big-game-hunter",
        "missing-modifier",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let modifier = &payload["models"][0];

    assert_eq!(payload["family"], "modifiers");
    assert_eq!(payload["status"], "partial");
    assert_eq!(payload["missingIds"][0], "missing-modifier");
    assert_eq!(modifier["id"], "BigGameHunter");
    assert_eq!(
        modifier["title"],
        json!({"table": "modifiers", "key": "BigGameHunter.title"})
    );
    assert_eq!(
        modifier["neowOptionTitle"],
        json!({"table": "modifiers", "key": "BigGameHunter.neowOptionTitle"})
    );
    assert_eq!(modifier["clearsPlayerDeck"], false);
    assert_eq!(modifier["polarity"], "good");
    assert_eq!(
        modifier["mutuallyExclusiveModifierIds"][0],
        "SmallGameHunter"
    );
    assert_eq!(
        modifier["iconAssetKey"],
        "model://modifiers/big-game-hunter/icon"
    );
}

#[test]
fn models_achievement_exports_common_metadata_only() {
    let response = run(&["sts2", "--json", "models", "achievements", "first-win"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("models json");
    let achievement = &payload["models"][0];
    let keys = achievement.as_object().expect("achievement object");

    assert_eq!(payload["family"], "achievements");
    assert_eq!(achievement["id"], "FirstWin");
    assert_eq!(
        achievement["typeName"],
        "MegaCrit.Sts2.Core.Models.Achievements.FirstWin"
    );
    assert_eq!(achievement["categorySortingId"], 10);
    assert_eq!(achievement["entrySortingId"], 1);
    assert_eq!(achievement["shouldReceiveCombatHooks"], false);
    assert_eq!(keys.len(), 6);
}

#[test]
fn models_singular_families_are_unsupported() {
    for family in ["character", "relic"] {
        let response = run(&["sts2", "--json", "models", family]);
        let payload: Value = serde_json::from_str(&response.stdout).expect("models json");

        assert_eq!(payload["family"], family);
        assert_eq!(payload["status"], "unsupported-family");
        assert!(payload["models"].as_array().expect("models").is_empty());
    }
}

#[test]
#[ignore = "developer helper for intentional snapshot refreshes"]
fn update_cli_snapshots() {
    let state_cases = [
        ("state-main-menu.json", None),
        ("state-combat.json", Some("combat")),
        ("state-lobby.json", Some("lobby")),
        ("state-map.json", Some("map")),
        ("state-rest-site.json", Some("rest-site")),
        ("state-rewards.json", Some("rewards")),
        ("state-shop.json", Some("shop")),
        (
            "state-fake-merchant-pre-open.json",
            Some("fake-merchant-pre-open"),
        ),
        ("state-crystal-sphere.json", Some("crystal-sphere")),
        (
            "state-crystal-sphere-finished.json",
            Some("crystal-sphere-finished"),
        ),
        ("state-card-selection.json", Some("card-selection")),
        (
            "state-simple-card-selection.json",
            Some("simple-card-selection"),
        ),
        (
            "state-deck-card-selection.json",
            Some("deck-card-selection"),
        ),
        ("state-relic-selection.json", Some("relic-selection")),
        ("state-NCharacterSelectScreen.json", Some("lobby")),
        ("state-card-overlay.json", Some("card-overlay")),
        (
            "state-passive-card-overlay.json",
            Some("passive-card-overlay"),
        ),
        ("state-event-room.json", Some("event-room")),
        ("state-treasure-room.json", Some("treasure-room")),
    ];

    write_snapshot(
        &run(&["sts2", "--json", "inspect", "actions"]).stdout,
        "inspect-actions.json",
    );
    write_snapshot(
        &run(&["sts2", "--json", "inspect", "commands"]).stdout,
        "inspect-commands.json",
    );
    write_snapshot(
        &run(&["sts2", "--json", "inspect", "examples"]).stdout,
        "inspect-examples.json",
    );
    write_snapshot(
        &run(&["sts2", "--json", "inspect", "ai-tools"]).stdout,
        "inspect-ai-tools.json",
    );

    for (snapshot, scenario) in state_cases {
        let config = match scenario {
            Some(scenario) => write_raw_config(&format!(
                "transport:\n  kind: mock\n  mockScenario: {scenario}\n"
            )),
            None => write_raw_config("transport:\n  kind: mock\n"),
        };
        if snapshot == "state-NCharacterSelectScreen.json" {
            run(&[
                "sts2",
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "dev",
                "fixture",
                "load",
                "--path",
                "../fixtures/multiplayer-ownership-host-local-seat.sts2.fixture.yaml",
            ]);
        }
        let response = run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "state",
        ]);
        write_snapshot(&response.stdout, snapshot);
    }
}

#[test]
fn inspect_commands_snapshot_matches() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let names = payload["commands"]
        .as_array()
        .expect("commands array")
        .iter()
        .map(|command| command["name"].as_str().expect("command name"))
        .collect::<Vec<_>>();
    for removed in [
        "dev checkpoint capture",
        "dev checkpoint resume",
        "dev checkpoint list",
        "dev checkpoint delete",
        "render snapshot",
        "render contract",
        "render preflight",
        "presentation contract",
        "presentation preflight",
    ] {
        assert!(!names.contains(&removed), "{removed} should not be public");
    }
    for removed in [
        "dev scene resource",
        "dev scene localization",
        "dev scene synthetic-layout",
    ] {
        assert!(!names.contains(&removed), "{removed} should not be public");
    }
    assert!(names.contains(&"state"), "state should be public");
    assert_snapshot(&response.stdout, "inspect-commands.json");
}

#[test]
fn inspect_commands_json_matches_snapshot() {
    inspect_commands_snapshot_matches();
}

#[test]
fn inspect_commands_json_includes_mod_reload() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");

    let reload = commands
        .iter()
        .find(|command| command["name"] == "dev mod-reload")
        .expect("dev mod-reload command");
    let status = commands
        .iter()
        .find(|command| command["name"] == "dev mod-reload status")
        .expect("dev mod-reload status command");

    assert_eq!(reload["readOnly"], false);
    assert_eq!(status["readOnly"], true);
    assert!(
        reload["examples"]
            .as_array()
            .expect("examples")
            .iter()
            .any(|example| example
                .as_str()
                .expect("example")
                .contains("--json dev mod-reload"))
    );
}

#[test]
fn inspect_ai_tools_promotes_hot_reload_tools() {
    let response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let tools = payload["tools"].as_array().expect("tools array");

    let status = tools
        .iter()
        .find(|tool| tool["name"] == "hot_reload_status")
        .expect("hot_reload_status tool");
    let reload = tools
        .iter()
        .find(|tool| tool["name"] == "hot_reload")
        .expect("hot_reload tool");

    assert_eq!(status["readOnly"], true);
    assert_eq!(status["mapsTo"][0], "dev mod-reload status");
    assert_eq!(status["inputSchema"]["required"][0], "project");
    assert_eq!(reload["readOnly"], false);
    assert_eq!(reload["mapsTo"][0], "dev mod-reload");
    assert!(
        reload["limitations"]
            .as_array()
            .expect("limitations")
            .iter()
            .any(|value| value.as_str().unwrap_or_default().contains("M57"))
    );
}

#[test]
fn inspect_examples_snapshot_matches() {
    let response = run(&["sts2", "--json", "inspect", "examples"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["examples"]
        .as_array()
        .expect("examples array")
        .iter()
        .map(|example| example["command"].as_str().expect("example command"))
        .collect::<Vec<_>>();
    for removed in ["render snapshot", "render contract", "render preflight"] {
        assert!(
            !commands.contains(&removed),
            "{removed} should not be public"
        );
    }
    for expected in ["state"] {
        assert!(commands.contains(&expected), "{expected} should be public");
    }
    assert_snapshot(&response.stdout, "inspect-examples.json");
}

#[test]
fn presentation_bundle_render_commands_are_rejected() {
    for args in [
        ["sts2", "--json", "render", "snapshot"],
        ["sts2", "--json", "render", "contract"],
        ["sts2", "--json", "render", "preflight"],
    ] {
        let err = Cli::try_parse_from(args).expect_err("render command should be rejected");
        assert!(
            err.to_string().contains("render"),
            "error should mention rejected render command"
        );
    }
}

#[test]
fn removed_presentation_contract_command_is_rejected() {
    let err = Cli::try_parse_from(["sts2", "--json", "presentation", "contract"])
        .expect_err("presentation contract should be removed");
    assert!(err.to_string().contains("presentation"));
}
