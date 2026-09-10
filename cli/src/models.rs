use super::*;

pub(crate) fn execute_models_json(
    args: ModelCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .models(bridge::proto::ModelCatalogRequest {
            request_id: "cli-models-1".to_string(),
            family: args.family,
            ids: args.ids,
            language: args.language.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;

    Ok(model_catalog_json(&response))
}

pub(crate) fn model_catalog_json(response: &bridge::proto::ModelCatalogResponse) -> Value {
    json!({
        "requestId": response.request_id,
        "schemaVersion": "spirectl.model-catalog-result/v0",
        "source": bridge::data_source_name(bridge::enum_value(response.source)),
        "provisional": response.provisional,
        "family": response.family,
        "language": empty_string_to_json(&response.language),
        "status": model_catalog_status_name(bridge::enum_value(response.status)),
        "models": response.models.iter().map(game_model_json).collect::<Vec<_>>(),
        "missingIds": response.missing_ids,
        "notices": response.notices.iter().map(model_catalog_notice_json).collect::<Vec<_>>(),
    })
}

fn game_model_json(model: &bridge::proto::GameModel) -> Value {
    match model.model.as_ref() {
        Some(bridge::proto::game_model::Model::Character(character)) => {
            character_model_json(character, &model.family)
        }
        Some(bridge::proto::game_model::Model::Relic(relic)) => {
            relic_model_json(relic, &model.family)
        }
        Some(bridge::proto::game_model::Model::Card(card)) => card_model_json(card, &model.family),
        Some(bridge::proto::game_model::Model::Potion(potion)) => {
            potion_model_json(potion, &model.family)
        }
        Some(bridge::proto::game_model::Model::Event(event)) => {
            event_model_json(event, &model.family)
        }
        Some(bridge::proto::game_model::Model::Act(act)) => act_model_json(act, &model.family),
        Some(bridge::proto::game_model::Model::Monster(monster)) => {
            monster_model_json(monster, &model.family)
        }
        Some(bridge::proto::game_model::Model::Encounter(encounter)) => {
            encounter_model_json(encounter, &model.family)
        }
        Some(bridge::proto::game_model::Model::Power(power)) => {
            power_model_json(power, &model.family)
        }
        Some(bridge::proto::game_model::Model::Orb(orb)) => orb_model_json(orb, &model.family),
        Some(bridge::proto::game_model::Model::Affliction(affliction)) => {
            affliction_model_json(affliction, &model.family)
        }
        Some(bridge::proto::game_model::Model::Enchantment(enchantment)) => {
            enchantment_model_json(enchantment, &model.family)
        }
        Some(bridge::proto::game_model::Model::CardPool(pool)) => {
            card_pool_model_json(pool, &model.family)
        }
        Some(bridge::proto::game_model::Model::RelicPool(pool)) => {
            relic_pool_model_json(pool, &model.family)
        }
        Some(bridge::proto::game_model::Model::PotionPool(pool)) => {
            potion_pool_model_json(pool, &model.family)
        }
        Some(bridge::proto::game_model::Model::Modifier(modifier)) => {
            modifier_model_json(modifier, &model.family)
        }
        Some(bridge::proto::game_model::Model::Achievement(achievement)) => {
            achievement_model_json(achievement, &model.family)
        }
        None => json!({
            "family": model.family,
        }),
    }
}

// pub(crate): also reused by reference.rs to flatten the "randomCharacter" reference topic
// (the synthetic Random Character lobby entry, which rides CharacterModelInfo but is not a real
// "characters" model-catalog entry).
pub(crate) fn character_model_json(
    model: &bridge::proto::CharacterModelInfo,
    family: &str,
) -> Value {
    json!({
        "family": family,
        "id": model.id,
        "title": localized_json(&model.title, model.title_loc.as_ref()),
        "nameColor": empty_string_to_json(&model.name_color),
        "startingHp": model.starting_hp,
        "startingGold": model.starting_gold,
        "maxEnergy": model.max_energy,
        "energyLabelOutlineColor": empty_string_to_json(&model.energy_label_outline_color),
        "baseOrbSlotCount": model.base_orb_slot_count,
        "shouldAlwaysShowStarCounter": model.should_always_show_star_counter,
        "startingRelics": model.starting_relics,
        "characterSelectTitle": localized_json(&model.character_select_title, model.character_select_title_loc.as_ref()),
        "characterSelectDesc": localized_json(&model.character_select_desc, model.character_select_desc_loc.as_ref()),
        "unlockText": localized_json(&model.unlock_text, model.unlock_text_loc.as_ref()),
        "dialogueColor": empty_string_to_json(&model.dialogue_color),
        "speechBubbleColor": empty_string_to_json(&model.speech_bubble_color),
        "mapDrawingColor": empty_string_to_json(&model.map_drawing_color),
        "visualsBounds": model_vector2_json(model.visuals_bounds.as_ref()),
        "intentPos": model_vector2_json(model.intent_pos.as_ref()),
        "visualsAssetKey": empty_string_to_json(&model.visuals_asset_key),
        "iconAssetKey": empty_string_to_json(&model.icon_asset_key),
        "iconOutlineAssetKey": empty_string_to_json(&model.icon_outline_asset_key),
        "energyCounterAssetKey": empty_string_to_json(&model.energy_counter_asset_key),
        "merchantAnimAssetKey": empty_string_to_json(&model.merchant_anim_asset_key),
        "restSiteAnimAssetKey": empty_string_to_json(&model.rest_site_anim_asset_key),
        "characterSelectBgAssetKey": empty_string_to_json(&model.character_select_bg_asset_key),
        "characterSelectBgSpineStillAssetKey": empty_string_to_json(&model.character_select_bg_spine_still_asset_key),
        "characterSelectIconAssetKey": empty_string_to_json(&model.character_select_icon_asset_key),
        "characterSelectLockedIconAssetKey": empty_string_to_json(&model.character_select_locked_icon_asset_key),
        "mapMarkerAssetKey": empty_string_to_json(&model.map_marker_asset_key),
        "iconPath": empty_string_to_json(&model.icon_path),
        "iconOutlinePath": empty_string_to_json(&model.icon_outline_path),
        "energyCounterPath": empty_string_to_json(&model.energy_counter_path),
        "merchantAnimPath": empty_string_to_json(&model.merchant_anim_path),
        "restSiteAnimPath": empty_string_to_json(&model.rest_site_anim_path),
        "characterSelectBgPath": empty_string_to_json(&model.character_select_bg_path),
        "characterSelectIconPath": empty_string_to_json(&model.character_select_icon_path),
        "characterSelectLockedIconPath": empty_string_to_json(&model.character_select_locked_icon_path),
        "mapMarkerPath": empty_string_to_json(&model.map_marker_path),
    })
}

fn relic_model_json(model: &bridge::proto::RelicModelInfo, family: &str) -> Value {
    json!({
        "family": family,
        "id": model.id,
        "title": localized_json(&model.title, model.title_loc.as_ref()),
        "flavor": localized_json(&model.flavor, model.flavor_loc.as_ref()),
        "description": localized_json(&model.description, model.description_loc.as_ref()),
        "iconPath": empty_string_to_json(&model.icon_path),
        "iconOutlinePath": empty_string_to_json(&model.icon_outline_path),
        "bigIconPath": empty_string_to_json(&model.big_icon_path),
        "rarity": empty_string_to_json(&model.rarity),
        "iconAssetKey": empty_string_to_json(&model.icon_asset_key),
        "iconOutlineAssetKey": empty_string_to_json(&model.icon_outline_asset_key),
        "bigIconAssetKey": empty_string_to_json(&model.big_icon_asset_key),
        "poolId": empty_string_to_json(&model.pool_id),
        "isTradable": model.is_tradable,
        "isAllowedInShops": model.is_allowed_in_shops,
        "hasUponPickupEffect": model.has_upon_pickup_effect,
        "spawnsPets": model.spawns_pets,
        "addsPet": model.adds_pet,
        "isStackable": model.is_stackable,
        "merchantCost": model.merchant_cost,
        "showCounter": model.show_counter,
        "flashSfx": empty_string_to_json(&model.flash_sfx),
        "hoverTips": model.hover_tips.iter().map(model_hover_tip_json).collect::<Vec<_>>(),
        "dynamicVars": json!(model.dynamic_vars),
    })
}

pub(crate) fn model_hover_tip_json(tip: &bridge::proto::ModelHoverTipInfo) -> Value {
    json!({
        "title": empty_string_to_json(&tip.title),
        "description": empty_string_to_json(&tip.description),
        "isDebuff": tip.is_debuff,
        "iconAssetKey": empty_string_to_json(&tip.icon_asset_key),
    })
}

fn card_model_json(model: &bridge::proto::CardModelInfo, family: &str) -> Value {
    let mut object = serde_json::Map::new();
    object.insert("family".to_string(), json!(family));
    object.insert("id".to_string(), json!(model.id));
    object.insert(
        "title".to_string(),
        localized_json(&model.title, model.title_loc.as_ref()),
    );
    object.insert(
        "titleLoc".to_string(),
        model
            .title_loc
            .as_ref()
            .map(model_localization_ref_json)
            .unwrap_or(Value::Null),
    );
    object.insert(
        "description".to_string(),
        localized_json(&model.description, model.description_loc.as_ref()),
    );
    object.insert(
        "descriptionLoc".to_string(),
        model
            .description_loc
            .as_ref()
            .map(model_localization_ref_json)
            .unwrap_or(Value::Null),
    );
    object.extend([
        ("type".to_string(), empty_string_to_json(&model.r#type)),
        ("rarity".to_string(), empty_string_to_json(&model.rarity)),
        (
            "targetType".to_string(),
            empty_string_to_json(&model.target_type),
        ),
        ("poolId".to_string(), empty_string_to_json(&model.pool_id)),
        (
            "visualPoolId".to_string(),
            empty_string_to_json(&model.visual_pool_id),
        ),
        ("energyCost".to_string(), json!(model.energy_cost)),
        ("isEnergyXCost".to_string(), json!(model.is_energy_x_cost)),
        ("starCost".to_string(), json!(model.star_cost)),
        ("isStarXCost".to_string(), json!(model.is_star_x_cost)),
        ("replayCount".to_string(), json!(model.replay_count)),
        ("keywords".to_string(), json!(model.keywords)),
        ("tags".to_string(), json!(model.tags)),
        (
            "maxUpgradeLevel".to_string(),
            json!(model.max_upgrade_level),
        ),
        ("upgradable".to_string(), json!(model.upgradable)),
        (
            "upgradePreviewDescription".to_string(),
            localized_json(
                &model.upgrade_preview_description,
                model.upgrade_preview_description_loc.as_ref(),
            ),
        ),
        (
            "canBeGeneratedInCombat".to_string(),
            json!(model.can_be_generated_in_combat),
        ),
        (
            "canBeGeneratedByModifiers".to_string(),
            json!(model.can_be_generated_by_modifiers),
        ),
        (
            "multiplayerConstraint".to_string(),
            empty_string_to_json(&model.multiplayer_constraint),
        ),
        (
            "shouldShowInCardLibrary".to_string(),
            json!(model.should_show_in_card_library),
        ),
        ("gainsBlock".to_string(), json!(model.gains_block)),
        (
            "orbEvokeType".to_string(),
            empty_string_to_json(&model.orb_evoke_type),
        ),
        (
            "hasBuiltInOverlay".to_string(),
            json!(model.has_built_in_overlay),
        ),
        (
            "imageAssetKey".to_string(),
            empty_string_to_json(&model.image_asset_key),
        ),
        (
            "imagePath".to_string(),
            empty_string_to_json(&model.image_path),
        ),
        (
            "betaImagePath".to_string(),
            empty_string_to_json(&model.beta_image_path),
        ),
        (
            "overlayAssetKey".to_string(),
            empty_string_to_json(&model.overlay_asset_key),
        ),
        (
            "overlayPath".to_string(),
            empty_string_to_json(&model.overlay_path),
        ),
        ("dynamicVars".to_string(), json!(model.dynamic_vars)),
    ]);
    if let Some(upgrade) = model.upgrade.as_ref() {
        let mut upgrade_object = serde_json::Map::new();
        if let Some(energy_cost) = upgrade.energy_cost {
            upgrade_object.insert("energyCost".to_string(), json!(energy_cost));
        }
        if !upgrade.dynamic_vars.is_empty() {
            upgrade_object.insert("dynamicVars".to_string(), json!(upgrade.dynamic_vars));
        }
        object.insert("upgrade".to_string(), Value::Object(upgrade_object));
    }

    Value::Object(object)
}

fn potion_model_json(model: &bridge::proto::PotionModelInfo, family: &str) -> Value {
    json!({
        "family": family,
        "id": model.id,
        "title": localized_json(&model.title, model.title_loc.as_ref()),
        "description": localized_json(&model.description, model.description_loc.as_ref()),
        "selectionScreenPrompt": localized_json(&model.selection_screen_prompt, model.selection_screen_prompt_loc.as_ref()),
        "rarity": empty_string_to_json(&model.rarity),
        "usage": empty_string_to_json(&model.usage),
        "targetType": empty_string_to_json(&model.target_type),
        "poolId": empty_string_to_json(&model.pool_id),
        "canBeGeneratedInCombat": model.can_be_generated_in_combat,
        "passesCustomUsabilityCheck": model.passes_custom_usability_check,
        "iconAssetKey": empty_string_to_json(&model.icon_asset_key),
        "iconPath": empty_string_to_json(&model.icon_path),
        "outlineAssetKey": empty_string_to_json(&model.outline_asset_key),
        "outlinePath": empty_string_to_json(&model.outline_path),
        "hoverTips": model.hover_tips.iter().map(potion_hover_tip_json).collect::<Vec<_>>(),
    })
}

fn potion_hover_tip_json(tip: &bridge::proto::PotionHoverTipInfo) -> Value {
    json!({
        "title": empty_string_to_json(&tip.title),
        "description": empty_string_to_json(&tip.description),
        "isDebuff": tip.is_debuff,
        "iconAssetKey": empty_string_to_json(&tip.icon_asset_key),
    })
}

fn event_model_json(model: &bridge::proto::EventModelInfo, family: &str) -> Value {
    json!({
        "family": family,
        "id": model.id,
        "kind": empty_string_to_json(&model.kind),
        "title": localized_json(&model.title, model.title_loc.as_ref()),
        "initialDescription": localized_json(&model.initial_description, model.initial_description_loc.as_ref()),
        "layoutType": empty_string_to_json(&model.layout_type),
        "isShared": model.is_shared,
        "isDeterministic": model.is_deterministic,
        "hasVfx": model.has_vfx,
        "canonicalEncounterId": empty_string_to_json(&model.canonical_encounter_id),
        "gameInfoOptions": model.game_info_options,
        "backgroundSceneAssetKey": empty_string_to_json(&model.background_scene_asset_key),
        "backgroundScenePath": empty_string_to_json(&model.background_scene_path),
        "backgroundSpineStillAssetKey": empty_string_to_json(&model.background_spine_still_asset_key),
        "backgroundSpineStillPath": empty_string_to_json(&model.background_spine_still_path),
        "initialPortraitAssetKey": empty_string_to_json(&model.initial_portrait_asset_key),
        "initialPortraitPath": empty_string_to_json(&model.initial_portrait_path),
        "vfxAssetKey": empty_string_to_json(&model.vfx_asset_key),
        "vfxPath": empty_string_to_json(&model.vfx_path),
        "epithet": localized_json(&model.epithet, model.epithet_loc.as_ref()),
        "dialogueColor": empty_string_to_json(&model.dialogue_color),
        "buttonColor": empty_string_to_json(&model.button_color),
        "ambientBgm": empty_string_to_json(&model.ambient_bgm),
        "hasAmbientBgm": model.has_ambient_bgm,
        "anyCharacterDialogueBlacklistIds": model.any_character_dialogue_blacklist_ids,
        "mapIconAssetKey": empty_string_to_json(&model.map_icon_asset_key),
        "mapIconPath": empty_string_to_json(&model.map_icon_path),
        "mapIconOutlineAssetKey": empty_string_to_json(&model.map_icon_outline_asset_key),
        "mapIconOutlinePath": empty_string_to_json(&model.map_icon_outline_path),
        "runHistoryIconAssetKey": empty_string_to_json(&model.run_history_icon_asset_key),
        "runHistoryIconPath": empty_string_to_json(&model.run_history_icon_path),
        "runHistoryIconOutlineAssetKey": empty_string_to_json(&model.run_history_icon_outline_asset_key),
        "runHistoryIconOutlinePath": empty_string_to_json(&model.run_history_icon_outline_path),
    })
}

fn act_model_json(model: &bridge::proto::ActModelInfo, family: &str) -> Value {
    json!({
        "family": family,
        "id": model.id,
        "title": localized_json(&model.title, model.title_loc.as_ref()),
        "defaultOrder": model.default_order,
        "roomCount": model.room_count,
        "multiplayerRoomCount": model.multiplayer_room_count,
        "floorCount": model.floor_count,
        "multiplayerFloorCount": model.multiplayer_floor_count,
        "bossEncounterIds": model.boss_encounter_ids,
        "eventIds": model.event_ids,
        "ancientEventIds": model.ancient_event_ids,
        "weakEncounterIds": model.weak_encounter_ids,
        "regularEncounterIds": model.regular_encounter_ids,
        "eliteEncounterIds": model.elite_encounter_ids,
        "monsterIds": model.monster_ids,
        "bgMusicOptions": model.bg_music_options,
        "musicBankPaths": model.music_bank_paths,
        "ambientSfx": empty_string_to_json(&model.ambient_sfx),
        "chestOpenSfx": empty_string_to_json(&model.chest_open_sfx),
        "mapTraveledColor": empty_string_to_json(&model.map_traveled_color),
        "mapUntraveledColor": empty_string_to_json(&model.map_untraveled_color),
        "mapBgColor": empty_string_to_json(&model.map_bg_color),
        "backgroundSceneAssetKey": empty_string_to_json(&model.background_scene_asset_key),
        "backgroundScenePath": empty_string_to_json(&model.background_scene_path),
        "restSiteBackgroundAssetKey": empty_string_to_json(&model.rest_site_background_asset_key),
        "restSiteBackgroundPath": empty_string_to_json(&model.rest_site_background_path),
        "mapTopBgAssetKey": empty_string_to_json(&model.map_top_bg_asset_key),
        "mapTopBgPath": empty_string_to_json(&model.map_top_bg_path),
        "mapMidBgAssetKey": empty_string_to_json(&model.map_mid_bg_asset_key),
        "mapMidBgPath": empty_string_to_json(&model.map_mid_bg_path),
        "mapBotBgAssetKey": empty_string_to_json(&model.map_bot_bg_asset_key),
        "mapBotBgPath": empty_string_to_json(&model.map_bot_bg_path),
        "chestSpineAssetKey": empty_string_to_json(&model.chest_spine_asset_key),
        "chestSpineResourcePath": empty_string_to_json(&model.chest_spine_resource_path),
        "combatBackgroundLayers": combat_background_layers_json(&model.combat_background_layers),
    })
}

fn combat_background_layers_json(layers: &[bridge::proto::CombatBackgroundLayerInfo]) -> Value {
    json!(
        layers
            .iter()
            .map(|layer| json!({
                "assetKey": layer.asset_key,
                "resPath": layer.res_path,
                "isForeground": layer.is_foreground,
                "bgGroupKey": empty_string_to_json(&layer.bg_group_key),
            }))
            .collect::<Vec<_>>()
    )
}

fn common_model_json(
    family: &str,
    id: &str,
    type_name: &str,
    category_sorting_id: i32,
    entry_sorting_id: i32,
    should_receive_combat_hooks: bool,
) -> serde_json::Map<String, Value> {
    let mut object = serde_json::Map::new();
    object.insert("family".to_string(), json!(family));
    object.insert("id".to_string(), json!(id));
    object.insert("typeName".to_string(), empty_string_to_json(type_name));
    object.insert("categorySortingId".to_string(), json!(category_sorting_id));
    object.insert("entrySortingId".to_string(), json!(entry_sorting_id));
    object.insert(
        "shouldReceiveCombatHooks".to_string(),
        json!(should_receive_combat_hooks),
    );
    object
}

fn model_vector2_json(vector: Option<&bridge::proto::ModelVector2>) -> Value {
    vector
        .map(|vector| json!({ "x": vector.x, "y": vector.y }))
        .unwrap_or(Value::Null)
}

fn monster_model_json(model: &bridge::proto::MonsterModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        ("minInitialHp".to_string(), json!(model.min_initial_hp)),
        ("maxInitialHp".to_string(), json!(model.max_initial_hp)),
        (
            "moveNames".to_string(),
            localized_array_json(&model.move_names, &model.move_name_locs),
        ),
        ("assetPaths".to_string(), json!(model.asset_paths)),
        (
            "visualsAssetKey".to_string(),
            empty_string_to_json(&model.visuals_asset_key),
        ),
        (
            "visualsPath".to_string(),
            empty_string_to_json(&model.visuals_path),
        ),
        (
            "bestiaryAttackAnimId".to_string(),
            empty_string_to_json(&model.bestiary_attack_anim_id),
        ),
        ("canChangeScale".to_string(), json!(model.can_change_scale)),
        (
            "isHealthBarVisible".to_string(),
            json!(model.is_health_bar_visible),
        ),
        (
            "deathAnimLengthOverride".to_string(),
            json!(model.death_anim_length_override),
        ),
        (
            "hasDeathAnimLengthOverride".to_string(),
            json!(model.has_death_anim_length_override),
        ),
        ("hasDeathSfx".to_string(), json!(model.has_death_sfx)),
        (
            "deathSfx".to_string(),
            empty_string_to_json(&model.death_sfx),
        ),
        ("hasHurtSfx".to_string(), json!(model.has_hurt_sfx)),
        ("hurtSfx".to_string(), empty_string_to_json(&model.hurt_sfx)),
        (
            "takeDamageSfx".to_string(),
            empty_string_to_json(&model.take_damage_sfx),
        ),
        (
            "takeDamageSfxType".to_string(),
            empty_string_to_json(&model.take_damage_sfx_type),
        ),
        (
            "shouldFadeAfterDeath".to_string(),
            json!(model.should_fade_after_death),
        ),
        (
            "shouldDisappearFromDoom".to_string(),
            json!(model.should_disappear_from_doom),
        ),
        (
            "hpBarSizeReduction".to_string(),
            json!(model.hp_bar_size_reduction),
        ),
        (
            "extraDeathVfxPadding".to_string(),
            model_vector2_json(model.extra_death_vfx_padding.as_ref()),
        ),
        (
            "visualsBounds".to_string(),
            model_vector2_json(model.visuals_bounds.as_ref()),
        ),
        (
            "intentPos".to_string(),
            model_vector2_json(model.intent_pos.as_ref()),
        ),
    ]);
    Value::Object(object)
}

fn encounter_model_json(model: &bridge::proto::EncounterModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "roomType".to_string(),
            empty_string_to_json(&model.room_type),
        ),
        ("isWeak".to_string(), json!(model.is_weak)),
        (
            "isDebugEncounter".to_string(),
            json!(model.is_debug_encounter),
        ),
        ("monsterIds".to_string(), json!(model.monster_ids)),
        (
            "monstersWithSlots".to_string(),
            json!(
                model
                    .monsters_with_slots
                    .iter()
                    .map(|slot| json!({"monsterId": slot.monster_id, "slot": slot.slot}))
                    .collect::<Vec<_>>()
            ),
        ),
        ("slots".to_string(), json!(model.slots)),
        ("tags".to_string(), json!(model.tags)),
        ("minGoldReward".to_string(), json!(model.min_gold_reward)),
        ("maxGoldReward".to_string(), json!(model.max_gold_reward)),
        (
            "shouldGiveRewards".to_string(),
            json!(model.should_give_rewards),
        ),
        ("hasBgm".to_string(), json!(model.has_bgm)),
        (
            "customBgm".to_string(),
            empty_string_to_json(&model.custom_bgm),
        ),
        ("hasAmbientSfx".to_string(), json!(model.has_ambient_sfx)),
        (
            "ambientSfx".to_string(),
            empty_string_to_json(&model.ambient_sfx),
        ),
        ("hasScene".to_string(), json!(model.has_scene)),
        (
            "sceneAssetKey".to_string(),
            empty_string_to_json(&model.scene_asset_key),
        ),
        (
            "scenePath".to_string(),
            empty_string_to_json(&model.scene_path),
        ),
        (
            "bossNodePath".to_string(),
            empty_string_to_json(&model.boss_node_path),
        ),
        (
            "mapNodeAssetPaths".to_string(),
            json!(model.map_node_asset_paths),
        ),
        (
            "extraAssetPaths".to_string(),
            json!(model.extra_asset_paths),
        ),
        (
            "customRewardDescription".to_string(),
            localized_json(
                &model.custom_reward_description,
                model.custom_reward_description_loc.as_ref(),
            ),
        ),
        (
            "fullyCenterPlayers".to_string(),
            json!(model.fully_center_players),
        ),
        (
            "cameraOffset".to_string(),
            model_vector2_json(model.camera_offset.as_ref()),
        ),
        ("cameraScaling".to_string(), json!(model.camera_scaling)),
        (
            "slotPositions".to_string(),
            json!(
                model
                    .slot_positions
                    .iter()
                    .map(|slot| json!({
                        "slot": slot.slot,
                        "position": model_vector2_json(slot.position.as_ref()),
                    }))
                    .collect::<Vec<_>>()
            ),
        ),
        (
            "hasCustomBackground".to_string(),
            json!(model.has_custom_background),
        ),
        (
            "backgroundSceneAssetKey".to_string(),
            empty_string_to_json(&model.background_scene_asset_key),
        ),
        (
            "backgroundScenePath".to_string(),
            empty_string_to_json(&model.background_scene_path),
        ),
        (
            "combatBackgroundLayers".to_string(),
            combat_background_layers_json(&model.combat_background_layers),
        ),
        (
            "backgroundSpineStillAssetKey".to_string(),
            empty_string_to_json(&model.background_spine_still_asset_key),
        ),
        (
            "backgroundSpineStillPath".to_string(),
            empty_string_to_json(&model.background_spine_still_path),
        ),
        (
            "backgroundSpinePosition".to_string(),
            model_vector2_json(model.background_spine_position.as_ref()),
        ),
    ]);
    Value::Object(object)
}

fn power_model_json(model: &bridge::proto::PowerModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "description".to_string(),
            localized_json(&model.description, model.description_loc.as_ref()),
        ),
        (
            "smartDescription".to_string(),
            localized_json(
                &model.smart_description,
                model.smart_description_loc.as_ref(),
            ),
        ),
        (
            "remoteDescription".to_string(),
            localized_json(
                &model.remote_description,
                model.remote_description_loc.as_ref(),
            ),
        ),
        ("type".to_string(), empty_string_to_json(&model.r#type)),
        (
            "stackType".to_string(),
            empty_string_to_json(&model.stack_type),
        ),
        ("amount".to_string(), json!(model.amount)),
        ("displayAmount".to_string(), json!(model.display_amount)),
        (
            "amountOnTurnStart".to_string(),
            json!(model.amount_on_turn_start),
        ),
        ("allowNegative".to_string(), json!(model.allow_negative)),
        ("isVisible".to_string(), json!(model.is_visible)),
        ("isInstanced".to_string(), json!(model.is_instanced)),
        (
            "hasSmartDescription".to_string(),
            json!(model.has_smart_description),
        ),
        (
            "hasRemoteDescription".to_string(),
            json!(model.has_remote_description),
        ),
        ("shouldPlayVfx".to_string(), json!(model.should_play_vfx)),
        (
            "shouldScaleInMultiplayer".to_string(),
            json!(model.should_scale_in_multiplayer),
        ),
        (
            "amountLabelColor".to_string(),
            empty_string_to_json(&model.amount_label_color),
        ),
        (
            "iconAssetKey".to_string(),
            empty_string_to_json(&model.icon_asset_key),
        ),
        (
            "iconPath".to_string(),
            empty_string_to_json(&model.icon_path),
        ),
        (
            "packedIconPath".to_string(),
            empty_string_to_json(&model.packed_icon_path),
        ),
        (
            "bigIconAssetKey".to_string(),
            empty_string_to_json(&model.big_icon_asset_key),
        ),
        (
            "resolvedBigIconPath".to_string(),
            empty_string_to_json(&model.resolved_big_icon_path),
        ),
    ]);
    Value::Object(object)
}

fn orb_model_json(model: &bridge::proto::OrbModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "description".to_string(),
            localized_json(&model.description, model.description_loc.as_ref()),
        ),
        (
            "smartDescription".to_string(),
            localized_json(
                &model.smart_description,
                model.smart_description_loc.as_ref(),
            ),
        ),
        (
            "hasSmartDescription".to_string(),
            json!(model.has_smart_description),
        ),
        ("passiveVal".to_string(), json!(model.passive_val)),
        ("evokeVal".to_string(), json!(model.evoke_val)),
        (
            "darkenedColor".to_string(),
            empty_string_to_json(&model.darkened_color),
        ),
        ("assetPaths".to_string(), json!(model.asset_paths)),
        (
            "iconAssetKey".to_string(),
            empty_string_to_json(&model.icon_asset_key),
        ),
        (
            "iconPath".to_string(),
            empty_string_to_json(&model.icon_path),
        ),
        (
            "spriteAssetKey".to_string(),
            empty_string_to_json(&model.sprite_asset_key),
        ),
        (
            "spritePath".to_string(),
            empty_string_to_json(&model.sprite_path),
        ),
    ]);
    Value::Object(object)
}

fn affliction_model_json(model: &bridge::proto::AfflictionModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "description".to_string(),
            localized_json(&model.description, model.description_loc.as_ref()),
        ),
        (
            "extraCardText".to_string(),
            localized_json(&model.extra_card_text, model.extra_card_text_loc.as_ref()),
        ),
        ("amount".to_string(), json!(model.amount)),
        ("isStackable".to_string(), json!(model.is_stackable)),
        (
            "canAfflictUnplayableCards".to_string(),
            json!(model.can_afflict_unplayable_cards),
        ),
        (
            "hasExtraCardText".to_string(),
            json!(model.has_extra_card_text),
        ),
        ("hasOverlay".to_string(), json!(model.has_overlay)),
        (
            "overlayAssetKey".to_string(),
            empty_string_to_json(&model.overlay_asset_key),
        ),
        (
            "overlayPath".to_string(),
            empty_string_to_json(&model.overlay_path),
        ),
    ]);
    Value::Object(object)
}

fn enchantment_model_json(model: &bridge::proto::EnchantmentModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "description".to_string(),
            localized_json(&model.description, model.description_loc.as_ref()),
        ),
        (
            "extraCardText".to_string(),
            localized_json(&model.extra_card_text, model.extra_card_text_loc.as_ref()),
        ),
        ("amount".to_string(), json!(model.amount)),
        ("displayAmount".to_string(), json!(model.display_amount)),
        ("showAmount".to_string(), json!(model.show_amount)),
        ("status".to_string(), empty_string_to_json(&model.status)),
        ("isStackable".to_string(), json!(model.is_stackable)),
        (
            "hasExtraCardText".to_string(),
            json!(model.has_extra_card_text),
        ),
        (
            "previewOutsideOfCombat".to_string(),
            json!(model.preview_outside_of_combat),
        ),
        ("shouldGlowGold".to_string(), json!(model.should_glow_gold)),
        ("shouldGlowRed".to_string(), json!(model.should_glow_red)),
        (
            "shouldStartAtBottomOfDrawPile".to_string(),
            json!(model.should_start_at_bottom_of_draw_pile),
        ),
        (
            "iconAssetKey".to_string(),
            empty_string_to_json(&model.icon_asset_key),
        ),
        (
            "iconPath".to_string(),
            empty_string_to_json(&model.icon_path),
        ),
        (
            "intendedIconPath".to_string(),
            empty_string_to_json(&model.intended_icon_path),
        ),
        (
            "missingIconPath".to_string(),
            empty_string_to_json(&model.missing_icon_path),
        ),
    ]);
    Value::Object(object)
}

fn card_pool_model_json(model: &bridge::proto::CardPoolModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        ("cardIds".to_string(), json!(model.card_ids)),
        ("isColorless".to_string(), json!(model.is_colorless)),
        (
            "energyColorName".to_string(),
            empty_string_to_json(&model.energy_color_name),
        ),
        (
            "energyOutlineColor".to_string(),
            empty_string_to_json(&model.energy_outline_color),
        ),
        (
            "deckEntryCardColor".to_string(),
            empty_string_to_json(&model.deck_entry_card_color),
        ),
        (
            "energyIconAssetKey".to_string(),
            empty_string_to_json(&model.energy_icon_asset_key),
        ),
        (
            "energyIconPath".to_string(),
            empty_string_to_json(&model.energy_icon_path),
        ),
        (
            "frameMaterialAssetKey".to_string(),
            empty_string_to_json(&model.frame_material_asset_key),
        ),
        (
            "frameMaterialPath".to_string(),
            empty_string_to_json(&model.frame_material_path),
        ),
        (
            "cardFrameMaterialPath".to_string(),
            empty_string_to_json(&model.card_frame_material_path),
        ),
    ]);
    Value::Object(object)
}

fn relic_pool_model_json(model: &bridge::proto::RelicPoolModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        ("relicIds".to_string(), json!(model.relic_ids)),
        (
            "energyColorName".to_string(),
            empty_string_to_json(&model.energy_color_name),
        ),
        (
            "labOutlineColor".to_string(),
            empty_string_to_json(&model.lab_outline_color),
        ),
    ]);
    Value::Object(object)
}

fn potion_pool_model_json(model: &bridge::proto::PotionPoolModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        ("potionIds".to_string(), json!(model.potion_ids)),
        (
            "energyColorName".to_string(),
            empty_string_to_json(&model.energy_color_name),
        ),
        (
            "labOutlineColor".to_string(),
            empty_string_to_json(&model.lab_outline_color),
        ),
    ]);
    Value::Object(object)
}

fn modifier_model_json(model: &bridge::proto::ModifierModelInfo, family: &str) -> Value {
    let mut object = common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    );
    object.extend([
        (
            "title".to_string(),
            localized_json(&model.title, model.title_loc.as_ref()),
        ),
        (
            "description".to_string(),
            localized_json(&model.description, model.description_loc.as_ref()),
        ),
        (
            "neowOptionTitle".to_string(),
            localized_json(
                &model.neow_option_title,
                model.neow_option_title_loc.as_ref(),
            ),
        ),
        (
            "neowOptionDescription".to_string(),
            localized_json(
                &model.neow_option_description,
                model.neow_option_description_loc.as_ref(),
            ),
        ),
        (
            "clearsPlayerDeck".to_string(),
            json!(model.clears_player_deck),
        ),
        (
            "polarity".to_string(),
            empty_string_to_json(&model.polarity),
        ),
        (
            "mutuallyExclusiveModifierIds".to_string(),
            json!(model.mutually_exclusive_modifier_ids),
        ),
        (
            "iconAssetKey".to_string(),
            empty_string_to_json(&model.icon_asset_key),
        ),
        (
            "iconPath".to_string(),
            empty_string_to_json(&model.icon_path),
        ),
    ]);
    Value::Object(object)
}

fn achievement_model_json(model: &bridge::proto::AchievementModelInfo, family: &str) -> Value {
    Value::Object(common_model_json(
        family,
        &model.id,
        &model.type_name,
        model.category_sorting_id,
        model.entry_sorting_id,
        model.should_receive_combat_hooks,
    ))
}

fn localized_json(text: &str, loc_ref: Option<&bridge::proto::ModelLocalizationRef>) -> Value {
    loc_ref
        .map(model_localization_ref_json)
        .unwrap_or_else(|| empty_string_to_json(text))
}

fn localized_array_json(
    texts: &[String],
    loc_refs: &[bridge::proto::ModelLocalizationRef],
) -> Value {
    if loc_refs.is_empty() {
        return json!(texts);
    }
    Value::Array(
        loc_refs
            .iter()
            .map(model_localization_ref_json)
            .collect::<Vec<_>>(),
    )
}

fn model_localization_ref_json(loc_ref: &bridge::proto::ModelLocalizationRef) -> Value {
    json!({
        "table": loc_ref.table,
        "key": loc_ref.key,
    })
}

fn model_catalog_notice_json(notice: &bridge::proto::ModelCatalogNotice) -> Value {
    json!({
        "code": notice.code,
        "severity": notice.severity,
        "message": notice.message,
        "path": empty_string_to_json(&notice.path),
    })
}

fn model_catalog_status_name(status: bridge::proto::ModelCatalogStatus) -> &'static str {
    match status {
        bridge::proto::ModelCatalogStatus::Ok => "ok",
        bridge::proto::ModelCatalogStatus::Partial => "partial",
        bridge::proto::ModelCatalogStatus::UnsupportedFamily => "unsupported-family",
        bridge::proto::ModelCatalogStatus::Unavailable => "unavailable",
        bridge::proto::ModelCatalogStatus::Unspecified => "unspecified",
    }
}
