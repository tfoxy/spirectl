use super::*;

pub(crate) fn execute_state_json(
    args: StateArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    if args.view == Some(StateViewArg::Actions) {
        return crate::state_actions::execute_state_actions_json(&args, context);
    }
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .state(bridge::proto::StateRequest {
            perspective: build_perspective_selector_values(
                args.perspective,
                args.player_id.as_deref(),
            ),
        })
        .map_err(AppError::bridge)?;
    Ok(state_json(&response))
}

pub(crate) fn state_json(response: &bridge::proto::StateResponse) -> Value {
    json!({
        "schemaVersion": response.schema_version,
        "language": nullable_string_json(&response.language),
        "rootScene": response.root_scene,
        "characterSelect": response.character_select.as_ref().map(character_select_json).unwrap_or(Value::Null),
        "run": response.run.as_ref().map(run_json).unwrap_or(Value::Null),
    })
}

fn character_select_json(value: &bridge::proto::StateCharacterSelect) -> Value {
    json!({
        "lobby": value.lobby.as_ref().map(character_select_lobby_json).unwrap_or(Value::Null),
        "characterButtons": value.character_buttons.iter().map(character_button_json).collect::<Vec<_>>(),
        "view": value.view.as_ref().map(character_select_view_json).unwrap_or(Value::Null),
    })
}

fn character_select_view_json(value: &bridge::proto::StateCharacterSelectView) -> Value {
    json!({
        "playerId": nullable_string_json(&value.player_id),
        "selectedCharacterButtonId": nullable_string_json(&value.selected_character_button_id),
    })
}

fn character_select_lobby_json(value: &bridge::proto::StateCharacterSelectLobby) -> Value {
    json!({
        "netGameType": value.net_game_type,
        "localPlayerId": nullable_string_json(&value.local_player_id),
        "hostPlayerId": nullable_string_json(&value.host_player_id),
        "connectingPlayerCount": value.connecting_player_count,
        "ascension": value.ascension,
        "maxAscension": value.max_ascension,
        "act1": nullable_string_json(&value.act1),
        "seed": nullable_string_json(&value.seed),
        "modifierIds": value.modifier_ids,
        "players": value.players.iter().map(character_select_player_json).collect::<Vec<_>>(),
        "savedRun": value.saved_run.as_ref().map(character_select_saved_run_json).unwrap_or(Value::Null),
        "maxPlayers": value.max_players,
    })
}

fn character_select_player_json(value: &bridge::proto::StateCharacterSelectPlayer) -> Value {
    json!({
        "id": value.id,
        "slotId": value.slot_id,
        "characterId": nullable_string_json(&value.character_id),
        "isReady": value.is_ready,
        "maxMultiplayerAscensionUnlocked": value.max_multiplayer_ascension_unlocked,
        "displayName": nullable_string_json(&value.display_name),
        "isConnected": value.is_connected,
    })
}

fn character_select_saved_run_json(value: &bridge::proto::StateCharacterSelectSavedRun) -> Value {
    json!({
        "currentActIndex": value.current_act_index,
        "actFloor": value.act_floor,
        "players": value.players.iter().map(character_select_saved_run_player_json).collect::<Vec<_>>(),
    })
}

fn character_select_saved_run_player_json(
    value: &bridge::proto::StateCharacterSelectSavedRunPlayer,
) -> Value {
    json!({
        "id": value.id,
        "currentHp": value.current_hp,
        "maxHp": value.max_hp,
        "gold": value.gold,
    })
}

fn character_button_json(value: &bridge::proto::StateCharacterButton) -> Value {
    json!({
        "id": value.id,
        "characterId": value.character_id,
        "isLocked": value.is_locked,
    })
}

fn run_json(value: &bridge::proto::StateRun) -> Value {
    json!({
        "sourceType": value.source_type,
        "managerSourceType": value.manager_source_type,
        "netGameType": value.net_game_type,
        "gameMode": value.game_mode,
        "seed": value.seed,
        "ascensionLevel": value.ascension_level,
        "actId": nullable_string_json(&value.act_id),
        "currentActIndex": value.current_act_index,
        "actFloor": value.act_floor,
        "totalFloor": value.total_floor,
        "bossEncounterId": nullable_string_json(&value.boss_encounter_id),
        "secondBossEncounterId": nullable_string_json(&value.second_boss_encounter_id),
        "currentMapCoord": value.current_map_coord.as_ref().map(map_coord_json).unwrap_or(Value::Null),
        "currentMapPointId": nullable_string_json(&value.current_map_point_id),
        "visitedMapCoords": value.visited_map_coords.iter().map(map_coord_json).collect::<Vec<_>>(),
        "players": value.players.iter().map(run_player_json).collect::<Vec<_>>(),
        "map": value.map.as_ref().map(run_map_json).unwrap_or(Value::Null),
        "currentRoom": value.current_room.as_ref().map(run_current_room_json).unwrap_or(Value::Null),
        "view": value.view.as_ref().map(run_view_json).unwrap_or(Value::Null),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
    })
}

fn run_view_json(value: &bridge::proto::StateRunView) -> Value {
    json!({
        "playerId": nullable_string_json(&value.player_id),
        "capstone": value.capstone.as_ref().map(run_capstone_view_json).unwrap_or(Value::Null),
        "selectedPotion": value.selected_potion.as_ref().map(selected_potion_json).unwrap_or(Value::Null),
        "isInCardSelection": value.is_in_card_selection,
        "selectedCard": value.selected_card.as_ref().map(selected_card_json).unwrap_or(Value::Null),
        "inspectRelic": value.inspect_relic.as_ref().map(inspect_relic_view_json).unwrap_or(Value::Null),
        "handSelection": value.hand_selection.as_ref().map(hand_selection_view_json).unwrap_or(Value::Null),
        "gameOver": value.game_over.as_ref().map(game_over_view_json).unwrap_or(Value::Null),
    })
}

fn game_over_view_json(value: &bridge::proto::StateGameOverView) -> Value {
    json!({
        "win": value.win,
        "bannerText": value.banner_text,
        "deathQuote": value.death_quote,
        "score": value.score,
        "killedByEncounterId": nullable_string_json(&value.killed_by_encounter_id),
    })
}

fn hand_selection_view_json(value: &bridge::proto::StateHandSelectionView) -> Value {
    json!({
        "playerId": nullable_string_json(&value.player_id),
        "mode": value.mode,
        "promptText": value.prompt_text,
        "promptLoc": value.prompt_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "minSelect": value.min_select,
        "maxSelect": value.max_select,
        "requireManualConfirmation": value.require_manual_confirmation,
        "canConfirm": value.can_confirm,
        "selectedCardIds": value.selected_card_ids,
        "selectableCardIds": value.selectable_card_ids,
        "sourceCardId": nullable_string_json(&value.source_card_id),
        "sourceModelId": nullable_string_json(&value.source_model_id),
        "isPeeking": value.is_peeking,
        "previewCard": value.preview_card.as_ref().map(card_json).unwrap_or(Value::Null),
    })
}

fn inspect_relic_view_json(value: &bridge::proto::StateInspectRelicView) -> Value {
    json!({
        "relicModelId": value.relic_model_id,
        "index": value.index,
        "count": value.count,
    })
}

fn selected_potion_json(value: &bridge::proto::StateSelectedPotion) -> Value {
    json!({
        "mode": value.mode,
        "slotIndex": value.slot_index,
    })
}

fn selected_card_json(value: &bridge::proto::StateSelectedCard) -> Value {
    json!({
        "cardId": value.card_id,
        "playerId": value.player_id,
    })
}

fn run_capstone_view_json(value: &bridge::proto::StateRunCapstoneView) -> Value {
    json!({
        "scene": value.scene,
        "sourceType": value.source_type,
        "stack": value.stack.iter().map(run_stack_entry_json).collect::<Vec<_>>(),
        "deckView": value.deck_view.as_ref().map(deck_view_json).unwrap_or(Value::Null),
        "cardPileView": value.card_pile_view.as_ref().map(card_pile_view_json).unwrap_or(Value::Null),
    })
}

fn card_pile_view_json(value: &bridge::proto::StateCardPileView) -> Value {
    json!({
        "pileType": value.pile_type,
        "playerId": nullable_string_json(&value.player_id),
    })
}

fn run_stack_entry_json(value: &bridge::proto::StateRunStackEntry) -> Value {
    json!({
        "scene": nullable_string_json(&value.scene),
        "sourceType": value.source_type,
    })
}

fn deck_view_json(value: &bridge::proto::StateDeckView) -> Value {
    json!({
        "sort": value.sort.iter().map(deck_view_sort_json).collect::<Vec<_>>(),
        "showUpgrades": value.show_upgrades,
    })
}

fn deck_view_sort_json(value: &bridge::proto::StateDeckViewSort) -> Value {
    json!({
        "by": value.by,
        "direction": value.direction,
    })
}

fn run_current_room_json(value: &bridge::proto::StateRunCurrentRoom) -> Value {
    json!({
        "sourceType": value.source_type,
        "id": value.id.map_or(Value::Null, |id| json!(id)),
        "roomType": value.room_type,
        "scene": value.scene,
        "modelId": nullable_string_json(&value.model_id),
        "event": value.event.as_ref().map(run_event_room_json).unwrap_or(Value::Null),
        "combat": value.combat.as_ref().map(run_combat_room_json).unwrap_or(Value::Null),
        "treasure": value.treasure.as_ref().map(run_treasure_room_json).unwrap_or(Value::Null),
        "shop": value.shop.as_ref().map(run_shop_room_json).unwrap_or(Value::Null),
        "restSite": value.rest_site.as_ref().map(run_rest_site_room_json).unwrap_or(Value::Null),
        "mapRoom": value.map_room.as_ref().map(run_map_room_json).unwrap_or(Value::Null),
    })
}

fn run_combat_room_json(value: &bridge::proto::StateRunCombatRoom) -> Value {
    json!({
        "encounterId": nullable_string_json(&value.encounter_id),
        "parentEventId": nullable_string_json(&value.parent_event_id),
        "goldProportion": value.gold_proportion,
        "isPreFinished": value.is_pre_finished,
        "shouldCreateCombat": value.should_create_combat,
        "shouldResumeParentEventAfterCombat": value.should_resume_parent_event_after_combat,
        "combatState": value.combat_state.as_ref().map(combat_state_json).unwrap_or(Value::Null),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "background": value.background.as_ref().map(combat_background_json).unwrap_or(Value::Null),
    })
}

fn combat_background_json(value: &bridge::proto::StateCombatBackground) -> Value {
    json!({
        "scenePath": nullable_string_json(&value.scene_path),
        "layers": value
            .layers
            .iter()
            .map(|layer| json!({
                "assetKey": nullable_string_json(&layer.asset_key),
                "resPath": layer.res_path,
                "isForeground": layer.is_foreground,
                "bgGroupKey": nullable_string_json(&layer.bg_group_key),
            }))
            .collect::<Vec<_>>(),
    })
}

fn run_event_room_json(value: &bridge::proto::StateRunEventRoom) -> Value {
    json!({
        "canonicalEventModelId": nullable_string_json(&value.canonical_event_model_id),
        "canonicalSourceType": nullable_string_json(&value.canonical_source_type),
        "scene": nullable_string_json(&value.scene),
        "isPreFinished": value.is_pre_finished,
        "isShared": value.is_shared,
        "playerStates": value.player_states.iter().map(run_event_player_state_json).collect::<Vec<_>>(),
        "sharedVotes": value.shared_votes.iter().map(run_event_shared_vote_json).collect::<Vec<_>>(),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
    })
}

fn run_event_player_state_json(value: &bridge::proto::StateRunEventPlayerState) -> Value {
    json!({
        "playerId": value.player_id,
        "eventModelId": nullable_string_json(&value.event_model_id),
        "canonicalEventModelId": nullable_string_json(&value.canonical_event_model_id),
        "sourceType": value.source_type,
        "ownerPlayerId": nullable_string_json(&value.owner_player_id),
        "layoutType": value.layout_type,
        "isFinished": value.is_finished,
        "descriptionLoc": value.description_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "options": value.options.iter().map(run_event_option_json).collect::<Vec<_>>(),
        "ancient": value.ancient.as_ref().map(run_event_ancient_json).unwrap_or(Value::Null),
    })
}

fn run_event_option_json(value: &bridge::proto::StateRunEventOption) -> Value {
    json!({
        "id": value.id,
        "index": value.index,
        "textKey": nullable_string_json(&value.text_key),
        "titleLoc": value.title_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "descriptionLoc": value.description_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "titleText": nullable_string_json(&value.title_text),
        "descriptionText": nullable_string_json(&value.description_text),
        "isLocked": value.is_locked,
        "isProceed": value.is_proceed,
        "wasChosen": value.was_chosen,
        "relicId": nullable_string_json(&value.relic_id),
        "shouldSaveChoiceToHistory": value.should_save_choice_to_history,
        "shouldSaveVariablesToHistory": value.should_save_variables_to_history,
        "hoverTips": value.hover_tips.iter().map(crate::models::model_hover_tip_json).collect::<Vec<_>>(),
        "previewedCard": value.previewed_card.as_ref().map(card_json).unwrap_or(Value::Null),
    })
}

fn run_event_shared_vote_json(value: &bridge::proto::StateRunEventSharedVote) -> Value {
    json!({
        "playerId": value.player_id,
        "optionIndex": if value.has_option_index { json!(value.option_index) } else { Value::Null },
    })
}

fn run_event_ancient_json(value: &bridge::proto::StateRunEventAncient) -> Value {
    json!({
        "healedAmount": value.healed_amount,
        "view": value.view.as_ref().map(run_event_ancient_view_json).unwrap_or(Value::Null),
    })
}

fn run_event_ancient_view_json(value: &bridge::proto::StateRunEventAncientView) -> Value {
    json!({
        "visibleDialogue": value.visible_dialogue.as_ref().map(run_event_ancient_visible_dialogue_json).unwrap_or(Value::Null),
    })
}

fn run_event_ancient_visible_dialogue_json(
    value: &bridge::proto::StateRunEventAncientVisibleDialogue,
) -> Value {
    json!({
        "sourceType": value.source_type,
        "dialogueId": nullable_string_json(&value.dialogue_id),
        "currentLineIndex": value.current_line_index,
        "currentLineLocKey": nullable_string_json(&value.current_line_loc_key),
        "lineLocKeys": value.line_loc_keys,
    })
}

fn run_treasure_room_json(value: &bridge::proto::StateRunTreasureRoom) -> Value {
    json!({
        "currentRelics": if value.current_relics_active {
            Value::Array(value.current_relics.iter().map(treasure_relic_json).collect::<Vec<_>>())
        } else {
            Value::Null
        },
        "playerVotes": value.player_votes.iter().map(treasure_player_vote_json).collect::<Vec<_>>(),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "canProceed": value.can_proceed,
    })
}

fn treasure_relic_json(value: &bridge::proto::StateTreasureRelic) -> Value {
    json!({
        "id": value.id,
        "modelId": value.model_id,
    })
}

fn treasure_player_vote_json(value: &bridge::proto::StateTreasurePlayerVote) -> Value {
    json!({
        "playerId": value.player_id,
        "index": value.index.map_or(Value::Null, |index| json!(index)),
        "voteReceived": value.vote_received,
    })
}

fn run_shop_room_json(value: &bridge::proto::StateRunShopRoom) -> Value {
    json!({
        "inventory": value.inventory.as_ref().map(shop_inventory_json).unwrap_or(Value::Null),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "view": value.view.as_ref().map(shop_view_json).unwrap_or(Value::Null),
    })
}

fn shop_view_json(value: &bridge::proto::StateShopView) -> Value {
    json!({
        "isOpen": value.is_open,
    })
}

fn shop_inventory_json(value: &bridge::proto::StateShopInventory) -> Value {
    json!({
        "sourceType": value.source_type,
        "playerId": nullable_string_json(&value.player_id),
        "characterCardEntries": value.character_card_entries.iter().map(shop_card_entry_json).collect::<Vec<_>>(),
        "colorlessCardEntries": value.colorless_card_entries.iter().map(shop_card_entry_json).collect::<Vec<_>>(),
        "relicEntries": value.relic_entries.iter().map(shop_relic_entry_json).collect::<Vec<_>>(),
        "potionEntries": value.potion_entries.iter().map(shop_potion_entry_json).collect::<Vec<_>>(),
        "cardRemovalEntry": value.card_removal_entry.as_ref().map(shop_card_removal_entry_json).unwrap_or(Value::Null),
    })
}

fn shop_card_entry_json(value: &bridge::proto::StateShopCardEntry) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "cost": value.cost.map_or(Value::Null, |cost| json!(cost)),
        "isOnSale": value.is_on_sale,
        "card": value.card.as_ref().map(card_json).unwrap_or(Value::Null),
    })
}

fn shop_relic_entry_json(value: &bridge::proto::StateShopRelicEntry) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "cost": value.cost.map_or(Value::Null, |cost| json!(cost)),
        "modelId": nullable_string_json(&value.model_id),
    })
}

fn shop_potion_entry_json(value: &bridge::proto::StateShopPotionEntry) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "cost": value.cost.map_or(Value::Null, |cost| json!(cost)),
        "modelId": nullable_string_json(&value.model_id),
    })
}

fn shop_card_removal_entry_json(value: &bridge::proto::StateShopCardRemovalEntry) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "cost": value.cost.map_or(Value::Null, |cost| json!(cost)),
        "used": value.used,
    })
}

fn run_rest_site_room_json(value: &bridge::proto::StateRunRestSiteRoom) -> Value {
    json!({
        "playerStates": value.player_states.iter().map(rest_site_player_state_json).collect::<Vec<_>>(),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "view": value.view.as_ref().map(rest_site_room_view_json).unwrap_or(Value::Null),
    })
}

fn rest_site_room_view_json(value: &bridge::proto::StateRestSiteRoomView) -> Value {
    json!({
        "canProceed": value.can_proceed,
    })
}

fn rest_site_player_state_json(value: &bridge::proto::StateRestSitePlayerState) -> Value {
    json!({
        "playerId": value.player_id,
        "hoveredOptionIndex": value.hovered_option_index.map_or(Value::Null, |index| json!(index)),
        "chosenOptionIndex": value.chosen_option_index.map_or(Value::Null, |index| json!(index)),
        "options": value.options.iter().map(rest_site_option_json).collect::<Vec<_>>(),
    })
}

fn rest_site_option_json(value: &bridge::proto::StateRestSiteOption) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "optionId": value.option_id,
        "isEnabled": value.is_enabled,
        "smithCount": value.smith_count.map_or(Value::Null, |count| json!(count)),
        "liftsLeft": value.lifts_left.map_or(Value::Null, |count| json!(count)),
        "disabledReason": value.disabled_reason.as_deref().map_or(Value::Null, |reason| json!(reason)),
    })
}

fn run_map_room_json(value: &bridge::proto::StateRunMapRoom) -> Value {
    json!({
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
    })
}

fn loc_ref_json(value: &bridge::proto::StateLocRef) -> Value {
    json!({
        "table": value.table,
        "key": value.key,
    })
}

fn run_player_json(value: &bridge::proto::StateRunPlayer) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "netId": nullable_string_json(&value.net_id),
        "displayName": nullable_string_json(&value.display_name),
        "characterId": value.character_id,
        "isLocal": value.is_local,
        "isHost": value.is_host,
        "isRemote": value.is_remote,
        "creature": value.creature.as_ref().map(run_creature_json).unwrap_or(Value::Null),
        "gold": value.gold,
        "deck": value.deck.as_ref().map(card_pile_json).unwrap_or(Value::Null),
        "relics": value.relics.iter().map(relic_json).collect::<Vec<_>>(),
        "overlays": value.overlays.iter().map(run_overlay_json).collect::<Vec<_>>(),
        "inventoryComplete": value.inventory_complete,
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "combat": value.combat.as_ref().map(run_player_combat_json).unwrap_or(Value::Null),
        "potions": value.potions.iter().map(combat_potion_json).collect::<Vec<_>>(),
        "canRemovePotions": value.can_remove_potions,
    })
}

fn run_overlay_json(value: &bridge::proto::StateRunOverlay) -> Value {
    json!({
        "id": value.id,
        "screenType": value.screen_type,
        "screenId": value.screen_id,
        "scene": value.scene,
        "chooseACard": value.choose_a_card.as_ref().map(choose_a_card_overlay_json).unwrap_or(Value::Null),
        "rewards": value.rewards.as_ref().map(rewards_overlay_json).unwrap_or(Value::Null),
        "deckCardSelection": value
            .deck_card_selection
            .as_ref()
            .map(deck_card_selection_overlay_json)
            .unwrap_or(Value::Null),
        "simpleGridCardSelection": value
            .simple_grid_card_selection
            .as_ref()
            .map(simple_grid_card_selection_overlay_json)
            .unwrap_or(Value::Null),
        "bundleSelection": value
            .bundle_card_selection
            .as_ref()
            .map(bundle_card_selection_overlay_json)
            .unwrap_or(Value::Null),
        "crystalSphere": value
            .crystal_sphere
            .as_ref()
            .map(crystal_sphere_overlay_json)
            .unwrap_or(Value::Null),
    })
}

fn choose_a_card_overlay_json(value: &bridge::proto::StateChooseACardOverlay) -> Value {
    json!({
        "canSkip": value.can_skip,
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
    })
}

fn crystal_sphere_overlay_json(value: &bridge::proto::StateCrystalSphereOverlay) -> Value {
    json!({
        "divinationsRemaining": value.divinations_remaining,
        "selectedTool": value.selected_tool,
        "isFinished": value.is_finished,
        "cells": value.cells.iter().map(crystal_sphere_cell_json).collect::<Vec<_>>(),
        "revealedItems": value.revealed_items.iter().map(crystal_sphere_item_json).collect::<Vec<_>>(),
    })
}

fn crystal_sphere_cell_json(value: &bridge::proto::StateCrystalSphereCell) -> Value {
    json!({
        "id": value.id,
        "x": value.x,
        "y": value.y,
        "isHidden": value.is_hidden,
        "isHighlighted": value.is_highlighted,
        "isHovered": value.is_hovered,
        "enabled": value.enabled,
    })
}

fn crystal_sphere_item_json(value: &bridge::proto::StateCrystalSphereItem) -> Value {
    json!({
        "id": value.id,
        "x": value.x,
        "y": value.y,
        "widthCells": value.width_cells,
        "heightCells": value.height_cells,
        "iconAssetKey": value.icon_asset_key,
        "showsCard": value.shows_card,
        "cardRarity": value.card_rarity,
        "cardBannerMaterialKey": value.card_banner_material_key,
        "cardFrameMaterialKey": value.card_frame_material_key,
    })
}

fn simple_grid_card_selection_overlay_json(
    value: &bridge::proto::StateSimpleGridCardSelectionOverlay,
) -> Value {
    json!({
        "promptText": value.prompt_text,
        "promptLoc": value.prompt_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "minSelect": value.min_select,
        "maxSelect": value.max_select,
        "canConfirm": value.can_confirm,
        "canCancel": value.can_cancel,
        "previewActive": value.preview_active,
        "selectedCardIds": value.selected_card_ids,
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
    })
}

fn bundle_card_selection_overlay_json(
    value: &bridge::proto::StateBundleCardSelectionOverlay,
) -> Value {
    json!({
        "canConfirm": value.can_confirm,
        "previewActive": value.preview_active,
        "selectedBundleId": nullable_string_json(&value.selected_bundle_id),
        "bundles": value.bundles.iter().map(card_bundle_json).collect::<Vec<_>>(),
    })
}

fn card_bundle_json(value: &bridge::proto::StateCardBundle) -> Value {
    json!({
        "id": value.id,
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
    })
}

fn deck_card_selection_overlay_json(value: &bridge::proto::StateDeckCardSelectionOverlay) -> Value {
    use bridge::proto::StateDeckCardSelectionKind as Kind;
    let kind = match Kind::try_from(value.kind).unwrap_or(Kind::Unspecified) {
        Kind::Upgrade => "upgrade",
        Kind::Transform => "transform",
        Kind::Enchant => "enchant",
        Kind::Remove => "remove",
        Kind::Unspecified => "unspecified",
    };
    let non_empty = |text: &str| {
        if text.is_empty() {
            Value::Null
        } else {
            Value::String(text.to_string())
        }
    };
    json!({
        "kind": kind,
        "canSkip": value.can_skip,
        "canConfirm": value.can_confirm,
        "canCancel": value.can_cancel,
        "previewActive": value.preview_active,
        "promptText": value.prompt_text,
        "promptLoc": value.prompt_loc.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "minSelect": value.min_select,
        "maxSelect": value.max_select,
        "selectedCardIds": value.selected_card_ids,
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
        "enchantmentTitle": non_empty(&value.enchantment_title),
        "enchantmentDescription": non_empty(&value.enchantment_description),
        "enchantmentIconPath": non_empty(&value.enchantment_icon_path),
        "enchantmentExtraCardText": non_empty(&value.enchantment_extra_card_text),
    })
}

fn rewards_overlay_json(value: &bridge::proto::StateRewardsOverlay) -> Value {
    json!({
        "flow": value.flow.as_ref().map(reward_flow_json).unwrap_or(Value::Null),
        "items": value.items.iter().map(reward_item_json).collect::<Vec<_>>(),
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
    })
}

fn reward_flow_json(value: &bridge::proto::StateRewardFlow) -> Value {
    json!({
        "mode": value.mode,
        "enabled": value.enabled,
    })
}

fn reward_item_json(value: &bridge::proto::StateRewardItem) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "description": value.description.as_ref().map(loc_ref_json).unwrap_or(Value::Null),
        "gold": value.gold.map_or(Value::Null, |gold| json!(gold)),
        "relic": nullable_string_json(&value.relic),
        "potion": nullable_string_json(&value.potion),
        "card": value.card.as_ref().map(card_json).unwrap_or(Value::Null),
        "cardReward": value.card_reward.as_ref().map(card_reward_json).unwrap_or(Value::Null),
        "cardRemoval": value.card_removal,
        "linked": value.linked.iter().map(reward_item_json).collect::<Vec<_>>(),
        "iconAssetKey": nullable_string_json(&value.icon_asset_key),
        "descriptionArgs": reward_description_args_json(&value.description_args),
    })
}

// Emits the reward description's substitution variables as a JSON object (or null when
// empty), with keys sorted so the output stays deterministic/diff-friendly.
fn reward_description_args_json(args: &std::collections::HashMap<String, String>) -> Value {
    if args.is_empty() {
        return Value::Null;
    }
    let mut entries: Vec<(&String, &String)> = args.iter().collect();
    entries.sort_by(|a, b| a.0.cmp(b.0));
    Value::Object(
        entries
            .into_iter()
            .map(|(key, value)| (key.clone(), json!(value)))
            .collect(),
    )
}

fn card_reward_json(value: &bridge::proto::StateCardReward) -> Value {
    json!({
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
        "canReroll": value.can_reroll,
        "canSkip": value.can_skip,
    })
}

fn run_creature_json(value: &bridge::proto::StateRunCreature) -> Value {
    json!({
        "sourceType": value.source_type,
        "currentHp": value.current_hp,
        "maxHp": value.max_hp,
        "id": nullable_string_json(&value.id),
        "modelId": nullable_string_json(&value.model_id),
        "side": nullable_string_json(&value.side),
        "slotName": nullable_string_json(&value.slot_name),
        "block": value.block.map_or(Value::Null, |block| json!(block)),
        "isHittable": value.is_hittable.map_or(Value::Null, |is_hittable| json!(is_hittable)),
        "powerInstances": value.power_instances.iter().map(combat_power_instance_json).collect::<Vec<_>>(),
        "nextMove": value.next_move.as_ref().map(combat_next_move_json).unwrap_or(Value::Null),
        "previewedCard": value.previewed_card.as_ref().map(card_json).unwrap_or(Value::Null),
    })
}

fn run_player_combat_json(value: &bridge::proto::StateRunPlayerCombat) -> Value {
    json!({
        "sourceType": value.source_type,
        "energy": value.energy,
        "maxEnergy": value.max_energy,
        "stars": value.stars,
        "hand": value.hand.as_ref().map(combat_card_pile_json).unwrap_or(Value::Null),
        "drawPile": value.draw_pile.as_ref().map(combat_card_pile_json).unwrap_or(Value::Null),
        "discardPile": value.discard_pile.as_ref().map(combat_card_pile_json).unwrap_or(Value::Null),
        "exhaustPile": value.exhaust_pile.as_ref().map(combat_card_pile_json).unwrap_or(Value::Null),
        "playPile": value.play_pile.as_ref().map(combat_card_pile_json).unwrap_or(Value::Null),
        "petCreatures": value.pet_creatures.iter().map(run_creature_json).collect::<Vec<_>>(),
        "orbQueue": value.orb_queue.as_ref().map(combat_orb_queue_json).unwrap_or(Value::Null),
        "hasEndedTurn": value.has_ended_turn,
        "notices": value.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
    })
}

fn combat_card_pile_json(value: &bridge::proto::StateCombatCardPile) -> Value {
    json!({
        "sourceType": value.source_type,
        "id": value.id,
        "type": value.r#type,
        "cards": value.cards.iter().map(combat_card_json).collect::<Vec<_>>(),
    })
}

fn combat_card_json(value: &bridge::proto::StateCombatCard) -> Value {
    let mut object = json!({
        "id": value.id,
        "modelId": value.model_id,
        "upgradeLevel": value.upgrade_level,
        "currentTargetCreatureId": nullable_string_json(&value.current_target_creature_id),
        "exhaustOnNextPlay": value.exhaust_on_next_play,
        "hasSingleTurnRetain": value.has_single_turn_retain,
        "hasSingleTurnSly": value.has_single_turn_sly,
        "shouldRetainThisTurn": value.should_retain_this_turn,
        "energyCost": value.energy_cost,
        "starCost": value.star_cost,
        "unplayableReason": if value.unplayable_reason.is_empty() {
            Value::Null
        } else {
            json!(value.unplayable_reason)
        },
        "shouldGlowGold": value.should_glow_gold,
        "shouldGlowRed": value.should_glow_red,
    });
    if !value.affliction_model_id.is_empty() {
        object
            .as_object_mut()
            .expect("combat card JSON object")
            .insert(
                "afflictionModelId".to_string(),
                json!(value.affliction_model_id),
            );
        object
            .as_object_mut()
            .expect("combat card JSON object")
            .insert(
                "afflictionAmount".to_string(),
                json!(value.affliction_amount),
            );
    }
    if let Some(enchantment) = card_enchantment_json(value.enchantment.as_ref()) {
        object
            .as_object_mut()
            .expect("combat card JSON object")
            .insert("enchantment".to_string(), enchantment);
    }
    object
}

fn combat_power_instance_json(value: &bridge::proto::StateCombatPowerInstance) -> Value {
    json!({
        "id": value.id,
        "modelId": value.model_id,
        "sourceType": value.source_type,
        "amount": value.amount,
        "displayAmount": value.display_amount,
        "amountOnTurnStart": value.amount_on_turn_start,
        "type": value.r#type,
        "typeForCurrentAmount": value.type_for_current_amount,
        "stackType": value.stack_type,
        "isVisible": value.is_visible,
        "skipNextDurationTick": value.skip_next_duration_tick,
        "amountLabelColor": nullable_string_json(&value.amount_label_color),
        "ownerCreatureId": nullable_string_json(&value.owner_creature_id),
        "targetCreatureId": nullable_string_json(&value.target_creature_id),
        "applierCreatureId": nullable_string_json(&value.applier_creature_id),
        "hoverTips": value.hover_tips.iter().map(crate::models::model_hover_tip_json).collect::<Vec<_>>(),
        "previewedCard": value.previewed_card.as_ref().map(card_json).unwrap_or(Value::Null),
    })
}

fn combat_orb_queue_json(value: &bridge::proto::StateCombatOrbQueue) -> Value {
    json!({
        "sourceType": value.source_type,
        "capacity": value.capacity,
        "orbs": value.orbs.iter().map(combat_orb_json).collect::<Vec<_>>(),
    })
}

fn combat_orb_json(value: &bridge::proto::StateCombatOrb) -> Value {
    json!({
        "id": value.id,
        "modelId": value.model_id,
        "passiveVal": value.passive_val,
        "evokeVal": value.evoke_val,
        "ownerPlayerId": nullable_string_json(&value.owner_player_id),
        "hasBeenRemovedFromState": value.has_been_removed_from_state,
    })
}

fn combat_state_json(value: &bridge::proto::StateCombatState) -> Value {
    json!({
        "sourceType": value.source_type,
        "currentSide": value.current_side,
        "roundNumber": value.round_number,
        "modifierIds": value.modifier_ids,
        "escapedCreatureIds": value.escaped_creature_ids,
        "enemies": value.enemies.iter().map(run_creature_json).collect::<Vec<_>>(),
        "playerActionsDisabled": value.player_actions_disabled,
        "transientEffects": value.transient_effects.iter().map(combat_transient_effect_json).collect::<Vec<_>>(),
    })
}

fn combat_transient_effect_json(value: &bridge::proto::StateCombatTransientEffect) -> Value {
    json!({
        "id": value.id,
        "anchorCreatureId": nullable_string_json(&value.anchor_creature_id),
        "kind": value.kind,
        "amount": value.amount,
        "spawnedAtMs": value.spawned_at_ms,
        "cardModelId": nullable_string_json(&value.card_model_id),
        "cardId": nullable_string_json(&value.card_id),
        "sourceRelicModelId": nullable_string_json(&value.source_relic_model_id),
        "scenePath": nullable_string_json(&value.scene_path),
    })
}

fn combat_next_move_json(value: &bridge::proto::StateCombatNextMove) -> Value {
    json!({
        "id": value.id,
        "intents": value.intents.iter().map(combat_intent_json).collect::<Vec<_>>(),
    })
}

fn combat_intent_json(value: &bridge::proto::StateCombatIntent) -> Value {
    json!({
        "type": value.r#type,
        "attack": value.attack.as_ref().map(combat_attack_intent_json).unwrap_or(Value::Null),
        "cardCount": value.card_count.map(Value::from).unwrap_or(Value::Null),
    })
}

fn combat_attack_intent_json(value: &bridge::proto::StateCombatAttackIntent) -> Value {
    json!({
        "damage": value.damage,
        "hits": value.hits,
        "repeats": value.repeats,
    })
}

fn combat_potion_json(value: &bridge::proto::StateCombatPotion) -> Value {
    json!({
        "id": value.id,
        "modelId": nullable_string_json(&value.model_id),
        "isQueued": value.is_queued,
        "passesUsabilityCheck": value.passes_usability_check,
    })
}

fn card_pile_json(value: &bridge::proto::StateCardPile) -> Value {
    json!({
        "sourceType": value.source_type,
        "id": value.id,
        "type": value.r#type,
        "count": value.count,
        "cards": value.cards.iter().map(card_json).collect::<Vec<_>>(),
        "orderObservable": value.order_observable,
    })
}

fn card_json(value: &bridge::proto::StateCard) -> Value {
    let mut object = Map::new();
    object.insert("id".to_string(), json!(value.id));
    object.insert("modelId".to_string(), json!(value.model_id));
    object.insert("upgradeLevel".to_string(), json!(value.upgrade_level));
    // -1 = unknown (renderer falls back to the model cost); only surface a real cost.
    if value.energy_cost >= 0 {
        object.insert("energyCost".to_string(), json!(value.energy_cost));
    }
    if !value.dynamic_vars.is_empty() {
        object.insert("dynamicVars".to_string(), json!(value.dynamic_vars));
    }
    if !value.next_dynamic_vars.is_empty() {
        object.insert(
            "nextDynamicVars".to_string(),
            json!(value.next_dynamic_vars),
        );
    }
    if !value.affliction_model_id.is_empty() {
        object.insert(
            "afflictionModelId".to_string(),
            json!(value.affliction_model_id),
        );
        object.insert(
            "afflictionAmount".to_string(),
            json!(value.affliction_amount),
        );
    }
    if let Some(enchantment) = card_enchantment_json(value.enchantment.as_ref()) {
        object.insert("enchantment".to_string(), enchantment);
    }

    Value::Object(object)
}

fn card_enchantment_json(value: Option<&bridge::proto::StateCardEnchantment>) -> Option<Value> {
    let enchantment = value?;
    let mut object = Map::new();
    object.insert("modelId".to_string(), json!(enchantment.model_id));
    object.insert("title".to_string(), json!(enchantment.title));
    object.insert("description".to_string(), json!(enchantment.description));
    if !enchantment.extra_card_text.is_empty() {
        object.insert(
            "extraCardText".to_string(),
            json!(enchantment.extra_card_text),
        );
    }
    if !enchantment.icon_path.is_empty() {
        object.insert("iconPath".to_string(), json!(enchantment.icon_path));
    }
    object.insert(
        "displayAmount".to_string(),
        json!(enchantment.display_amount),
    );
    object.insert("showAmount".to_string(), json!(enchantment.show_amount));
    object.insert("status".to_string(), json!(enchantment.status));
    if !enchantment.extra_hover_tips.is_empty() {
        object.insert(
            "extraHoverTips".to_string(),
            json!(
                enchantment
                    .extra_hover_tips
                    .iter()
                    .map(crate::models::model_hover_tip_json)
                    .collect::<Vec<_>>()
            ),
        );
    }
    if !enchantment.added_keywords.is_empty() {
        object.insert(
            "addedKeywords".to_string(),
            json!(enchantment.added_keywords),
        );
    }
    if enchantment.replay_count > 0 {
        object.insert("replayCount".to_string(), json!(enchantment.replay_count));
    }
    Some(Value::Object(object))
}

fn relic_json(value: &bridge::proto::StateRelic) -> Value {
    json!({
        "id": value.id,
        "modelId": value.model_id,
        "slotIndex": value.slot_index,
        "hasCounter": value.has_counter,
        "counter": value.counter,
    })
}

fn run_map_json(value: &bridge::proto::StateRunMap) -> Value {
    json!({
        "sourceType": value.source_type,
        "rowCount": value.row_count,
        "columnCount": value.column_count,
        "startingMapPointId": nullable_string_json(&value.starting_map_point_id),
        "bossMapPointId": nullable_string_json(&value.boss_map_point_id),
        "secondBossMapPointId": nullable_string_json(&value.second_boss_map_point_id),
        "mapPointHistory": value.map_point_history.iter().map(map_point_history_act_json).collect::<Vec<_>>(),
        "points": value.points.iter().map(map_point_json).collect::<Vec<_>>(),
        "view": value.view.as_ref().map(run_map_view_json).unwrap_or(Value::Null),
    })
}

fn run_map_view_json(value: &bridge::proto::StateRunMapView) -> Value {
    json!({
        "isOpen": value.is_open,
        "isAcceptingVotes": value.is_accepting_votes,
    })
}

fn map_coord_json(value: &bridge::proto::StateMapCoord) -> Value {
    json!({
        "row": value.row,
        "col": value.col,
    })
}

fn map_point_history_act_json(value: &bridge::proto::StateMapPointHistoryAct) -> Value {
    Value::Array(
        value
            .entries
            .iter()
            .map(map_point_history_entry_json)
            .collect(),
    )
}

fn map_point_history_entry_json(value: &bridge::proto::StateMapPointHistoryEntry) -> Value {
    json!({
        "floor": value.floor,
        "coord": value.coord.as_ref().map(map_coord_json).unwrap_or(Value::Null),
        "mapPointType": value.map_point_type,
    })
}

fn map_point_json(value: &bridge::proto::StateMapPoint) -> Value {
    json!({
        "id": value.id,
        "sourceType": value.source_type,
        "coord": value.coord.as_ref().map(map_coord_json).unwrap_or(Value::Null),
        "pointType": value.point_type,
        "canBeModified": value.can_be_modified,
        "parentIds": value.parent_ids,
        "childIds": value.child_ids,
        "travelable": value.travelable,
        "visited": value.visited,
        "revealedRoomType": nullable_string_json(&value.revealed_room_type),
    })
}

fn state_notice_json(value: &bridge::proto::StateNotice) -> Value {
    json!({
        "code": value.code,
        "message": value.message,
        "provisional": value.provisional,
        "path": nullable_string_json(&value.path),
        "severity": nullable_string_json(&value.severity),
        "source": nullable_string_json(&value.source),
        "stability": nullable_string_json(&value.stability),
        "perspective": nullable_string_json(&value.perspective),
    })
}

fn nullable_string_json(value: &str) -> Value {
    if value.trim().is_empty() {
        Value::Null
    } else {
        Value::String(value.to_string())
    }
}

pub(crate) fn state_schema_json() -> Value {
    json!({
        "schemaVersion": "spirectl.state-schema/v0",
        "primaryCommand": "state",
        "summary": "sts2 state is the runtime-only presentation envelope: a screen-aware snapshot of what the attached client can observe. Semantic actions are derived natively from it via state actions.",
        "commands": {
            "state": "Runtime-only envelope. Character Select includes characterSelect; active runs include run; unsupported sections return null.",
            "state actions": "Native semantic actions derived from the state envelope plus presentation/actions.json.",
            "state watch": "CLI NDJSON stream over the state envelope; uses reactive live bridge subscriptions when advertised, with polling fallback and explicit polling mode."
        },
        "command": "sts2 --json state",
        "stateSchemaVersion": "spirectl.state/v0",
        "summaryDetail": "Runtime-only state shape for presentation render consumers. It excludes immutable model data, localization text, assets, actions, layout, and computed UI-control fields.",
        "viewSemantics": "All state view objects describe attached-client local UI state. They are omitted with notices when the requested perspective is remote and the attached client cannot observe that player's UI.",
        "topLevelFields": ["schemaVersion", "language", "rootScene", "characterSelect", "run"],
        "unsupportedScreens": "All screens return the envelope; characterSelect is null except the start-run character select scene, and run is null unless RunManager has an active run.",
        "actionContract": {
            "command": "sts2 --json state actions",
            "schemaVersion": "spirectl.state-actions/v0",
            "source": "state plus presentation/actions.json",
            "path": "actions[]",
            "fields": ["id", "kind", "label", "enabled", "args", "ownerPlayerId", "sourcePath", "disabledReason"],
            "args": "Only relevant non-null semantic action arguments are emitted.",
            "execution": "act remains the final authority for stale, wrong-player, unsupported, or hook-missing failures.",
            "perspective": ["playerId", "requestedPlayerId", "scope", "usesDefault", "seatCount"],
            "perspective.playerId": "The seat the returned actions belong to, as reported back by the state envelope this resolution read.",
            "perspective.requestedPlayerId": "The --player-id that was asked for, or null when the seat was defaulted.",
            "perspective.usesDefault": "True when no --player-id was given, so the bridge chose the seat.",
            "warnings": "warnings[] carries {code, severity, message} entries. perspective_defaulted_multi_seat is emitted when usesDefault is true and seatCount is greater than 1: on a multi-seat game an empty actions[] for the defaulted seat is indistinguishable from 'this seat has nothing to do'. Warnings never change the exit code."
        },
        "watchEventContract": {
            "command": "sts2 --json state watch",
            "format": "newline-delimited JSON; one event object per line",
            "readOnly": true,
            "source": "same state envelope path used by one-shot state; reactive live bridge subscription when available, polling fallback otherwise",
            "eventTypes": ["initial", "changed", "error"],
            "fields": ["type", "sequence", "observedAtUtc", "fingerprint", "state", "error", "suppressedDuplicateCount"],
            "bounds": ["--max-events", "--timeout-ms", "--poll-interval-ms", "--rpc-timeout-ms", "--watch-mode"],
            "errorSemantics": "Structured state errors are emitted as error events and the stream continues until bounded unless --fail-fast is set.",
            "nonGoals": ["logs", "debug streams", "latest-state cache", "presentation render payloads", "browser startup/session contracts", "raw scene-tree internals"]
        },
        "stateContract": {
            "command": "sts2 --json state",
            "schemaVersion": "spirectl.state/v0",
            "summary": "Runtime-only state shape for presentation render consumers. It excludes immutable model data, localization text, assets, actions, layout, and computed UI-control fields.",
            "viewSemantics": "All state view objects describe attached-client local UI state. They are omitted with notices when the requested perspective is remote and the attached client cannot observe that player's UI.",
            "topLevelFields": ["schemaVersion", "language", "rootScene", "characterSelect", "run"],
            "unsupportedScreens": "All screens return the envelope; characterSelect is null except the start-run character select scene, and run is null unless RunManager has an active run.",
            "characterSelect": {
                "lobby": ["netGameType", "localPlayerId", "hostPlayerId", "connectingPlayerCount", "ascension", "maxAscension", "act1", "seed", "modifierIds", "players", "maxPlayers"],
                "players": ["id", "slotId", "characterId", "isReady", "maxMultiplayerAscensionUnlocked", "displayName"],
                "characterButtons": ["id", "characterId", "isLocked"],
                "view": ["playerId", "selectedCharacterButtonId"],
                "view.playerId": "The player whose local presentation perspective is being rendered.",
                "view.selectedCharacterButtonId": "References characterButtons[].id and follows view.playerId when the selected character is available."
            },
            "run": {
                "source": "RunState plus RunManager.NetService metadata; currentRoom comes from RunState.CurrentRoom and event rooms add EventRoom/EventSynchronizer/EventOption state.",
                "fields": ["sourceType", "managerSourceType", "netGameType", "gameMode", "seed", "ascensionLevel", "actId", "currentActIndex", "actFloor", "totalFloor", "bossEncounterId", "secondBossEncounterId", "currentMapCoord", "currentMapPointId", "visitedMapCoords", "players", "map", "currentRoom", "view", "notices"],
                "view": ["playerId", "capstone", "inspectRelic"],
                "players": ["id", "netId", "displayName", "characterId", "isLocal", "isHost", "isRemote", "creature", "gold", "deck", "relics", "overlays", "inventoryComplete", "notices"],
                "playerOverlays": ["id", "screenType", "screenId", "scene", "chooseACard", "rewards", "deckCardSelection"],
                "chooseACardOverlay": ["canSkip", "cards"],
                "deckCardSelectionOverlay": ["kind", "canSkip", "canConfirm", "canCancel", "previewActive", "promptText", "promptLoc", "minSelect", "maxSelect", "selectedCardIds", "cards"],
                "map": ["rowCount", "columnCount", "startingMapPointId", "bossMapPointId", "secondBossMapPointId", "mapPointHistory", "points", "view"],
                "map.view": ["isOpen"],
                "map.view.isOpen": "Whether NMapScreen is currently open/visible for the attached local game client; independent from currentRoom.mapRoom.",
                "currentRoom": ["id", "sourceType", "roomType", "modelId", "event", "combat", "treasure", "shop", "restSite", "mapRoom"],
                "shopRoom": ["inventory", "view", "notices"],
                "shopRoom.view": ["isOpen"],
                "shopRoom.view.isOpen": "Whether NMerchantInventory is currently open/visible for the attached local game client.",
                "eventRoom": ["canonicalEventModelId", "canonicalSourceType", "isPreFinished", "isShared", "playerStates", "sharedVotes", "notices"],
                "eventPlayerStates": ["playerId", "eventModelId", "canonicalEventModelId", "sourceType", "ownerPlayerId", "layoutType", "isFinished", "descriptionLoc", "options", "ancient"],
                "eventOptions": ["id", "index", "textKey", "titleLoc", "descriptionLoc", "titleText", "descriptionText", "isLocked", "isProceed", "wasChosen", "relicId", "shouldSaveChoiceToHistory", "shouldSaveVariablesToHistory", "hoverTips"],
                "modelPayloads": "Names, descriptions, localization text, portraits, icons, and static card/relic/character metadata stay in models."
            },
            "computedValues": {
                "characterSelect.isBeginningRun": "state.characterSelect.lobby.connectingPlayerCount == 0 && state.characterSelect.lobby.players.length > 0 && state.characterSelect.lobby.players.every(player => player.isReady)",
                "characterSelect.selectedButton": "state.characterSelect.characterButtons.find(button => button.id == state.characterSelect.view.selectedCharacterButtonId)",
                "characterSelect.localPlayer": "state.characterSelect.lobby.players.find(player => player.id == state.characterSelect.view.playerId)",
                "characterSelect.hostPlayer": "state.characterSelect.lobby.players.find(player => player.id == state.characterSelect.lobby.hostPlayerId)",
                "characterSelect.selectedCharacterButton": "state.characterSelect.characterButtons.find(button => button.id == state.characterSelect.view.selectedCharacterButtonId)",
                "characterSelect.remoteSelectedPlayerIdsByButtonId": "Object.fromEntries(state.characterSelect.characterButtons.map(button => [button.id, state.characterSelect.lobby.players.filter(player => player.id != state.characterSelect.view.playerId && player.characterId == button.characterId).map(player => player.id)]))",
                "characterSelect.characterButtons[].isSelected": "button.id == state.characterSelect.view.selectedCharacterButtonId",
                "characterSelect.characterButtons[].isRandom": "button.characterId == 'RANDOM_CHARACTER'"
            }
        },
        "stability": {
            "stable": ["rootScene", "characterSelect.view.playerId", "run.view.playerId", "run.players[].id", "actions[].id", "actions[].kind", "actions[].args"],
            "bestEffort": ["run.currentRoom.*", "actions[].sourcePath"]
        },
        "remoteOrchestrationStates": [
            "unavailable",
            "local-only-degraded",
            "host-local-seat",
            "host-mediated",
            "configured-client",
            "unsupported"
        ],
        "ownershipFailures": {
            "reasonCodes": ["wrong_player", "unsupported_perspective"],
            "guidance": "Retry wrong_player with the advertised ownerPlayerId. Treat unsupported_perspective as requiring a configured owning client or host-local-seat capability; do not fall back to raw input in normal mode."
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bridge::proto;

    #[test]
    fn state_json_projects_generic_run_payload() {
        let response = proto::StateResponse {
            schema_version: "spirectl.state/v0".to_string(),
            language: "en".to_string(),
            root_scene: "run".to_string(),
            character_select: None,
            run: Some(proto::StateRun {
                source_type: "MegaCrit.Sts2.Core.Runs.RunState".to_string(),
                manager_source_type: "MegaCrit.Sts2.Core.Runs.RunManager".to_string(),
                net_game_type: "host".to_string(),
                game_mode: "Standard".to_string(),
                seed: "seed-1".to_string(),
                ascension_level: 10,
                act_id: "act_2".to_string(),
                current_act_index: 1,
                act_floor: 4,
                total_floor: 21,
                boss_encounter_id: "VANTOM_BOSS".to_string(),
                second_boss_encounter_id: String::new(),
                current_map_coord: Some(proto::StateMapCoord { row: 3, col: 1 }),
                current_map_point_id: "map-point:3:1".to_string(),
                visited_map_coords: vec![proto::StateMapCoord { row: 0, col: 3 }],
                players: vec![proto::StateRunPlayer {
                    id: "p:100".to_string(),
                    source_type: "MegaCrit.Sts2.Core.Entities.Players.Player".to_string(),
                    net_id: "100".to_string(),
                    display_name: "Host".to_string(),
                    character_id: "ironclad".to_string(),
                    is_local: true,
                    is_host: true,
                    is_remote: false,
                    creature: Some(proto::StateRunCreature {
                        source_type: "MegaCrit.Sts2.Core.Entities.Creatures.Creature".to_string(),
                        current_hp: 52,
                        max_hp: 80,
                        id: String::new(),
                        model_id: String::new(),
                        side: String::new(),
                        slot_name: String::new(),
                        block: None,
                        is_hittable: None,
                        power_instances: Vec::new(),
                        previewed_card: None,
                        next_move: None,
                    }),
                    gold: 123,
                    deck: Some(proto::StateCardPile {
                        source_type: "MegaCrit.Sts2.Core.Entities.Cards.CardPile".to_string(),
                        id: "deck:p:100".to_string(),
                        r#type: "Deck".to_string(),
                        count: 1,
                        cards: vec![proto::StateCard {
                            id: "card:p:100:deck:0".to_string(),
                            model_id: "strike_red".to_string(),
                            upgrade_level: 1,
                            dynamic_vars: Default::default(),
                            next_dynamic_vars: Default::default(),
                            affliction_model_id: String::new(),
                            affliction_amount: 0,
                            enchantment: None,
                            energy_cost: -1,
                        }],
                        order_observable: true,
                    }),
                    relics: vec![proto::StateRelic {
                        id: "relic:p:100:0:burning_blood".to_string(),
                        model_id: "burning_blood".to_string(),
                        slot_index: 0,
                        has_counter: false,
                        counter: 0,
                    }],
                    overlays: vec![proto::StateRunOverlay {
                        id: "overlay:p:100:choose-a-card:1".to_string(),
                        screen_type: "CardSelection".to_string(),
                        screen_id: "Screens.CardSelection.NChooseACardSelectionScreen".to_string(),
                        scene: "screens/card_selection/choose_a_card_selection_screen".to_string(),
                        choose_a_card: Some(proto::StateChooseACardOverlay {
                            can_skip: true,
                            cards: vec![proto::StateCard {
                                id: "card:p:100:choose-a-card:1:0".to_string(),
                                model_id: "DEMONIC_SHIELD".to_string(),
                                upgrade_level: 0,
                                dynamic_vars: Default::default(),
                                next_dynamic_vars: Default::default(),
                                affliction_model_id: String::new(),
                                affliction_amount: 0,
                                enchantment: None,
                                energy_cost: -1,
                            }],
                        }),
                        rewards: None,
                        deck_card_selection: None,
                        simple_grid_card_selection: None,
                        bundle_card_selection: None,
                        crystal_sphere: None,
                    }],
                    inventory_complete: true,
                    notices: Vec::new(),
                    combat: None,
                    potions: vec![
                        proto::StateCombatPotion {
                            id: 0,
                            model_id: "fire-potion".to_string(),
                            is_queued: false,
                            passes_usability_check: true,
                        },
                        proto::StateCombatPotion {
                            id: 1,
                            model_id: String::new(),
                            is_queued: false,
                            passes_usability_check: false,
                        },
                        proto::StateCombatPotion {
                            id: 2,
                            model_id: "block-potion".to_string(),
                            is_queued: true,
                            passes_usability_check: true,
                        },
                    ],
                    can_remove_potions: true,
                }],
                map: Some(proto::StateRunMap {
                    source_type: "MegaCrit.Sts2.Core.Map.ActMap".to_string(),
                    row_count: 15,
                    column_count: 7,
                    starting_map_point_id: "map-point:0:3".to_string(),
                    boss_map_point_id: "map-point:14:3".to_string(),
                    second_boss_map_point_id: String::new(),
                    map_point_history: vec![proto::StateMapPointHistoryAct {
                        entries: vec![proto::StateMapPointHistoryEntry {
                            floor: 1,
                            coord: Some(proto::StateMapCoord { row: 0, col: 3 }),
                            map_point_type: "Monster".to_string(),
                        }],
                    }],
                    points: vec![proto::StateMapPoint {
                        id: "map-point:3:1".to_string(),
                        source_type: "MegaCrit.Sts2.Core.Map.MapPoint".to_string(),
                        coord: Some(proto::StateMapCoord { row: 3, col: 1 }),
                        point_type: "Monster".to_string(),
                        can_be_modified: true,
                        parent_ids: vec!["map-point:2:1".to_string()],
                        child_ids: vec!["map-point:4:1".to_string()],
                        travelable: true,
                        visited: false,
                        revealed_room_type: String::new(),
                    }],
                    view: Some(proto::StateRunMapView {
                        is_open: true,
                        is_accepting_votes: true,
                    }),
                }),
                current_room: Some(proto::StateRunCurrentRoom {
                    source_type: "MegaCrit.Sts2.Core.Rooms.EventRoom".to_string(),
                    id: Some(42),
                    room_type: "Event".to_string(),
                    scene: "rooms/event_room".to_string(),
                    model_id: "Neow".to_string(),
                    combat: None,
                    treasure: None,
                    shop: None,
                    rest_site: None,
                    map_room: None,
                    event: Some(proto::StateRunEventRoom {
                        scene: String::new(),
                        canonical_event_model_id: "Neow".to_string(),
                        canonical_source_type: "MegaCrit.Sts2.Core.Events.AncientEventModel"
                            .to_string(),
                        is_pre_finished: false,
                        is_shared: true,
                        player_states: vec![proto::StateRunEventPlayerState {
                            player_id: "p:100".to_string(),
                            event_model_id: "Neow".to_string(),
                            canonical_event_model_id: "Neow".to_string(),
                            source_type: "MegaCrit.Sts2.Core.Events.AncientEventModel".to_string(),
                            owner_player_id: "p:100".to_string(),
                            layout_type: "Ancient".to_string(),
                            is_finished: false,
                            description_loc: Some(proto::StateLocRef {
                                table: "events".to_string(),
                                key: "NEOW.pages.INITIAL.description".to_string(),
                            }),
                            options: vec![proto::StateRunEventOption {
                                id: "event-room:arcanescroll:0".to_string(),
                                index: 0,
                                text_key: "ArcaneScroll".to_string(),
                                title_loc: Some(proto::StateLocRef {
                                    table: "relics".to_string(),
                                    key: "ARCANE_SCROLL.title".to_string(),
                                }),
                                description_loc: Some(proto::StateLocRef {
                                    table: "relics".to_string(),
                                    key: "ARCANE_SCROLL.eventDescription".to_string(),
                                }),
                                title_text: "Arcane Scroll".to_string(),
                                description_text: "Obtain a rare relic.".to_string(),
                                is_locked: false,
                                is_proceed: false,
                                was_chosen: false,
                                relic_id: "ARCANE_SCROLL".to_string(),
                                should_save_choice_to_history: true,
                                should_save_variables_to_history: false,
                                hover_tips: vec![proto::ModelHoverTipInfo {
                                    title: "Arcane Scroll".to_string(),
                                    description: "Obtain a rare relic.".to_string(),
                                    is_debuff: false,
                                    icon_asset_key: String::new(),
                                }],
                                previewed_card: None,
                            }],
                            ancient: Some(proto::StateRunEventAncient {
                                healed_amount: 6,
                                view: Some(proto::StateRunEventAncientView {
                                    visible_dialogue: Some(
                                        proto::StateRunEventAncientVisibleDialogue {
                                            source_type:
                                                "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout"
                                                    .to_string(),
                                            dialogue_id: "NEOW.talk.ANY.4".to_string(),
                                            current_line_index: 0,
                                            current_line_loc_key: "NEOW.talk.ANY.4-0.ancient"
                                                .to_string(),
                                            line_loc_keys: vec![
                                                "NEOW.talk.ANY.4-0.ancient".to_string(),
                                                "NEOW.talk.ANY.4-1.char".to_string(),
                                            ],
                                        },
                                    ),
                                }),
                            }),
                        }],
                        shared_votes: vec![proto::StateRunEventSharedVote {
                            player_id: "p:100".to_string(),
                            has_option_index: false,
                            option_index: 0,
                        }],
                        notices: Vec::new(),
                    }),
                }),
                view: Some(proto::StateRunView {
                    player_id: "p:100".to_string(),
                    selected_potion: Some(proto::StateSelectedPotion {
                        mode: "popup".to_string(),
                        slot_index: 0,
                    }),
                    selected_card: Some(proto::StateSelectedCard {
                        card_id: "card:p:100:hand:0".to_string(),
                        player_id: "p:100".to_string(),
                    }),
                    is_in_card_selection: true,
                    hand_selection: Some(proto::StateHandSelectionView {
                        player_id: "p:100".to_string(),
                        mode: "simple-select".to_string(),
                        prompt_text: "Choose a card to [gold]discard[/gold].".to_string(),
                        prompt_loc: Some(proto::StateLocRef {
                            table: "card_selection".to_string(),
                            key: "TO_DISCARD".to_string(),
                        }),
                        min_select: 1,
                        max_select: 1,
                        require_manual_confirmation: false,
                        can_confirm: true,
                        selected_card_ids: vec!["7".to_string()],
                        selectable_card_ids: vec!["5".to_string(), "6".to_string()],
                        source_card_id: "10".to_string(),
                        source_model_id: "SURVIVOR".to_string(),
                        is_peeking: false,
                        preview_card: None,
                    }),
                    capstone: Some(proto::StateRunCapstoneView {
                        scene: "screens/capstone_submenu_stack".to_string(),
                        source_type:
                            "MegaCrit.Sts2.Core.Nodes.Screens.NCapstoneSubmenuStack"
                                .to_string(),
                        stack: vec![
                            proto::StateRunStackEntry {
                                scene: "screens/pause_menu/pause_menu".to_string(),
                                source_type:
                                    "MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu"
                                        .to_string(),
                            },
                            proto::StateRunStackEntry {
                                scene: "screens/settings_screen".to_string(),
                                source_type:
                                    "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen"
                                        .to_string(),
                            },
                        ],
                        deck_view: None,
                        card_pile_view: None,
                    }),
                    inspect_relic: Some(proto::StateInspectRelicView {
                        relic_model_id: "BURNING_BLOOD".to_string(),
                        index: 1,
                        count: 3,
                    }),
                    game_over: None,
                }),
                notices: Vec::new(),
            }),
        };

        let payload = state_json(&response);

        assert_eq!(payload["rootScene"], "run");
        assert_eq!(payload["run"]["netGameType"], "host");
        assert_eq!(payload["run"]["gameMode"], "Standard");
        assert_eq!(payload["run"]["currentMapCoord"]["row"], 3);
        assert_eq!(payload["run"]["map"]["points"][0]["travelable"], true);
        assert_eq!(payload["run"]["map"]["points"][0]["visited"], false);
        assert_eq!(payload["run"]["map"]["view"]["isAcceptingVotes"], true);
        assert_eq!(payload["run"]["players"][0]["displayName"], "Host");
        assert_eq!(payload["run"]["players"][0]["creature"]["currentHp"], 52);
        assert_eq!(payload["run"]["players"][0]["creature"]["maxHp"], 80);
        assert!(payload["run"]["players"][0]["creature"]["block"].is_null());
        assert!(payload["run"]["players"][0]["creature"]["isHittable"].is_null());
        assert!(payload["run"]["players"][0]["creature"]["nextMove"].is_null());
        assert!(
            payload["run"]["players"][0]["creature"]
                .get("combatId")
                .is_none()
        );
        assert!(
            payload["run"]["players"][0]["creature"]
                .get("isStunned")
                .is_none()
        );
        assert_eq!(
            payload["run"]["players"][0]["deck"]["cards"][0]["upgradeLevel"],
            1
        );
        assert!(
            payload["run"]["players"][0]["deck"]["cards"][0]
                .get("dynamicVars")
                .is_none()
        );
        assert!(
            payload["run"]["players"][0]["deck"]["cards"][0]
                .get("nextDynamicVars")
                .is_none()
        );
        assert_eq!(
            payload["run"]["players"][0]["overlays"][0]["screenId"],
            "Screens.CardSelection.NChooseACardSelectionScreen"
        );
        assert_eq!(
            payload["run"]["players"][0]["overlays"][0]["chooseACard"]["cards"][0]["modelId"],
            "DEMONIC_SHIELD"
        );
        assert_eq!(
            payload["run"]["players"][0]["overlays"][0]["chooseACard"]["canSkip"],
            true
        );
        assert!(
            payload["run"]["players"][0]["overlays"][0]["chooseACard"]["cards"][0]
                .get("dynamicVars")
                .is_none()
        );
        assert!(
            payload["run"]["players"][0]["overlays"][0]["chooseACard"]["cards"][0]
                .get("nextDynamicVars")
                .is_none()
        );
        assert_eq!(
            payload["run"]["players"][0]["relics"][0]["modelId"],
            "burning_blood"
        );
        assert_eq!(
            payload["run"]["players"][0]["potions"],
            json!([
                {
                    "id": 0,
                    "modelId": "fire-potion",
                    "isQueued": false,
                    "passesUsabilityCheck": true
                },
                {
                    "id": 1,
                    "modelId": Value::Null,
                    "isQueued": false,
                    "passesUsabilityCheck": false
                },
                {
                    "id": 2,
                    "modelId": "block-potion",
                    "isQueued": true,
                    "passesUsabilityCheck": true
                }
            ])
        );
        assert!(
            payload["run"]["players"][0]
                .get("potionSlotModelIds")
                .is_none()
        );
        assert!(payload["run"]["players"][0].get("potionUi").is_none());
        assert_eq!(payload["run"]["players"][0]["canRemovePotions"], true);
        assert_eq!(
            payload["run"]["map"]["mapPointHistory"][0][0]["mapPointType"],
            "Monster"
        );
        assert_eq!(payload["run"]["map"]["view"]["isOpen"], true);
        assert_eq!(payload["run"]["currentRoom"]["scene"], "rooms/event_room");
        assert!(payload["run"]["currentRoom"]["event"]["scene"].is_null());
        assert_eq!(payload["run"]["view"]["playerId"], "p:100");
        assert_eq!(payload["run"]["view"]["selectedPotion"]["mode"], "popup");
        assert_eq!(payload["run"]["view"]["selectedPotion"]["slotIndex"], 0);
        assert_eq!(
            payload["run"]["view"]["selectedCard"]["cardId"],
            "card:p:100:hand:0"
        );
        assert_eq!(payload["run"]["view"]["selectedCard"]["playerId"], "p:100");
        assert_eq!(payload["run"]["view"]["isInCardSelection"], true);
        assert_eq!(
            payload["run"]["view"]["capstone"]["scene"],
            "screens/capstone_submenu_stack"
        );
        assert_eq!(
            payload["run"]["view"]["capstone"]["stack"][0]["scene"],
            "screens/pause_menu/pause_menu"
        );
        assert_eq!(
            payload["run"]["view"]["capstone"]["stack"][1]["scene"],
            "screens/settings_screen"
        );
        assert!(payload["run"]["view"]["capstone"]["deckView"].is_null());
        assert_eq!(
            payload["run"]["view"]["inspectRelic"]["relicModelId"],
            "BURNING_BLOOD"
        );
        assert_eq!(payload["run"]["view"]["inspectRelic"]["index"], 1);
        assert_eq!(payload["run"]["view"]["inspectRelic"]["count"], 3);
        assert_eq!(
            payload["run"]["map"]["points"][0]["childIds"][0],
            "map-point:4:1"
        );
        assert_eq!(payload["run"]["currentRoom"]["roomType"], "Event");
        assert_eq!(payload["run"]["currentRoom"]["id"], 42);
        assert_eq!(
            payload["run"]["currentRoom"]["event"]["canonicalEventModelId"],
            "Neow"
        );
        assert_eq!(
            payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"][0]["relicId"],
            "ARCANE_SCROLL"
        );
        assert!(
            payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"][0]
                .get("kind")
                .is_none()
        );
        assert_eq!(
            payload["run"]["currentRoom"]["event"]["playerStates"][0]["options"][0]["hoverTips"],
            serde_json::json!([{
                "title": "Arcane Scroll",
                "description": "Obtain a rare relic.",
                "isDebuff": false,
                "iconAssetKey": null,
            }])
        );
        assert_eq!(
            payload["run"]["currentRoom"]["event"]["playerStates"][0]["ancient"]["view"]["visibleDialogue"]
                ["currentLineLocKey"],
            "NEOW.talk.ANY.4-0.ancient"
        );
        assert!(payload["run"]["currentRoom"]["event"]["sharedVotes"][0]["optionIndex"].is_null());
    }

    #[test]
    fn state_card_json_projects_dynamic_var_overrides_only_when_present() {
        let omitted = card_json(&proto::StateCard {
            id: "card:p:1:deck:0".to_string(),
            model_id: "BASH".to_string(),
            upgrade_level: 0,
            dynamic_vars: Default::default(),
            next_dynamic_vars: Default::default(),
            affliction_model_id: String::new(),
            affliction_amount: 0,
            enchantment: None,
            energy_cost: -1,
        });
        assert!(omitted.get("dynamicVars").is_none());
        assert!(omitted.get("nextDynamicVars").is_none());

        let emitted = card_json(&proto::StateCard {
            id: "card:p:1:deck:1".to_string(),
            model_id: "MOCK_MULTI_UPGRADE".to_string(),
            upgrade_level: 1,
            dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 12)]),
            next_dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 15)]),
            affliction_model_id: String::new(),
            affliction_amount: 0,
            enchantment: None,
            energy_cost: -1,
        });
        assert_eq!(emitted["dynamicVars"]["damage"], 12);
        assert_eq!(emitted["nextDynamicVars"]["damage"], 15);
    }

    #[test]
    fn state_card_json_projects_affliction_fields_only_when_present() {
        let omitted = card_json(&proto::StateCard {
            id: "card:p:1:hand:0".to_string(),
            model_id: "BASH".to_string(),
            upgrade_level: 0,
            dynamic_vars: Default::default(),
            next_dynamic_vars: Default::default(),
            affliction_model_id: String::new(),
            affliction_amount: 0,
            enchantment: None,
            energy_cost: -1,
        });
        assert!(omitted.get("afflictionModelId").is_none());
        assert!(omitted.get("afflictionAmount").is_none());

        let emitted = card_json(&proto::StateCard {
            id: "card:p:1:hand:1".to_string(),
            model_id: "ANGER".to_string(),
            upgrade_level: 0,
            dynamic_vars: Default::default(),
            next_dynamic_vars: Default::default(),
            affliction_model_id: "ENTANGLED".to_string(),
            affliction_amount: 1,
            enchantment: None,
            energy_cost: -1,
        });
        assert_eq!(emitted["afflictionModelId"], "ENTANGLED");
        assert_eq!(emitted["afflictionAmount"], 1);
    }

    #[test]
    fn state_deck_card_selection_overlay_projects_kind_and_preview_cards() {
        let overlay = run_overlay_json(&proto::StateRunOverlay {
            id: "overlay:p:1:deck-card-selection:upgrade:visible".to_string(),
            screen_type: "CardSelection".to_string(),
            screen_id: "Screens.CardSelection.NDeckUpgradeSelectScreen".to_string(),
            scene: "screens/card_selection/deck_upgrade_select_screen".to_string(),
            choose_a_card: None,
            rewards: None,
            simple_grid_card_selection: None,
            bundle_card_selection: None,
            crystal_sphere: None,
            deck_card_selection: Some(proto::StateDeckCardSelectionOverlay {
                kind: proto::StateDeckCardSelectionKind::Upgrade as i32,
                can_skip: false,
                can_confirm: true,
                preview_active: false,
                prompt_text: "Choose a card to remove from your deck.".to_string(),
                prompt_loc: None,
                min_select: 1,
                max_select: 2,
                selected_card_ids: vec!["card:p:1:deck-card-selection:upgrade:0".to_string()],
                can_cancel: false,
                cards: vec![proto::StateCard {
                    id: "card:p:1:deck-card-selection:upgrade:0".to_string(),
                    model_id: "STRIKE_IRONCLAD".to_string(),
                    upgrade_level: 0,
                    dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 6)]),
                    next_dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 9)]),
                    affliction_model_id: String::new(),
                    affliction_amount: 0,
                    enchantment: None,
                    energy_cost: -1,
                }],
                enchantment_title: String::new(),
                enchantment_description: String::new(),
                enchantment_icon_path: String::new(),
                enchantment_extra_card_text: String::new(),
            }),
        });
        assert_eq!(overlay["deckCardSelection"]["kind"], "upgrade");
        assert_eq!(overlay["deckCardSelection"]["canConfirm"], true);
        assert_eq!(overlay["deckCardSelection"]["previewActive"], false);
        assert_eq!(overlay["deckCardSelection"]["minSelect"], 1);
        assert_eq!(overlay["deckCardSelection"]["maxSelect"], 2);
        assert_eq!(overlay["deckCardSelection"]["canCancel"], false);
        assert_eq!(
            overlay["deckCardSelection"]["promptText"],
            "Choose a card to remove from your deck."
        );
        assert_eq!(
            overlay["deckCardSelection"]["selectedCardIds"][0],
            "card:p:1:deck-card-selection:upgrade:0"
        );
        assert_eq!(
            overlay["deckCardSelection"]["cards"][0]["modelId"],
            "STRIKE_IRONCLAD"
        );
        assert_eq!(
            overlay["deckCardSelection"]["cards"][0]["nextDynamicVars"]["damage"],
            9
        );
        // Empty enchantment fields (non-enchant kinds) surface as null, not "".
        assert!(overlay["deckCardSelection"]["enchantmentTitle"].is_null());
        assert!(overlay["deckCardSelection"]["enchantmentDescription"].is_null());
        assert!(overlay["chooseACard"].is_null());
    }

    #[test]
    fn state_crystal_sphere_overlay_projects_cells() {
        let overlay = run_overlay_json(&proto::StateRunOverlay {
            id: "overlay:p:1:crystal-sphere:visible".to_string(),
            screen_type: "CrystalSphere".to_string(),
            screen_id: "Events.Custom.CrystalSphere.NCrystalSphereScreen".to_string(),
            scene: "screens/crystal_sphere_screen".to_string(),
            choose_a_card: None,
            rewards: None,
            deck_card_selection: None,
            simple_grid_card_selection: None,
            bundle_card_selection: None,
            crystal_sphere: Some(proto::StateCrystalSphereOverlay {
                divinations_remaining: 6,
                selected_tool: "big".to_string(),
                is_finished: false,
                cells: vec![proto::StateCrystalSphereCell {
                    id: "crystal-sphere:cell:3:4".to_string(),
                    x: 3,
                    y: 4,
                    is_hidden: true,
                    is_highlighted: false,
                    is_hovered: true,
                    enabled: true,
                }],
                revealed_items: vec![proto::StateCrystalSphereItem {
                    id: "crystal-sphere:item:3:4:2:1".to_string(),
                    x: 3,
                    y: 4,
                    width_cells: 2,
                    height_cells: 1,
                    icon_asset_key: "res://images/events/crystal_sphere/crystal_sphere_relic.png"
                        .to_string(),
                    shows_card: false,
                    card_rarity: "".to_string(),
                    card_banner_material_key: "".to_string(),
                    card_frame_material_key: "".to_string(),
                }],
            }),
        });

        assert_eq!(overlay["crystalSphere"]["divinationsRemaining"], 6);
        assert_eq!(overlay["crystalSphere"]["selectedTool"], "big");
        assert_eq!(
            overlay["crystalSphere"]["cells"][0]["id"],
            "crystal-sphere:cell:3:4"
        );
        assert_eq!(overlay["crystalSphere"]["cells"][0]["x"], 3);
        assert_eq!(overlay["crystalSphere"]["cells"][0]["y"], 4);
        assert_eq!(overlay["crystalSphere"]["cells"][0]["isHidden"], true);
        assert_eq!(overlay["crystalSphere"]["cells"][0]["isHovered"], true);
        assert_eq!(overlay["crystalSphere"]["cells"][0]["enabled"], true);
        assert_eq!(
            overlay["crystalSphere"]["revealedItems"][0]["id"],
            "crystal-sphere:item:3:4:2:1"
        );
        assert_eq!(
            overlay["crystalSphere"]["revealedItems"][0]["widthCells"],
            2
        );
        assert_eq!(
            overlay["crystalSphere"]["revealedItems"][0]["iconAssetKey"],
            "res://images/events/crystal_sphere/crystal_sphere_relic.png"
        );
        assert_eq!(
            overlay["crystalSphere"]["revealedItems"][0]["cardBannerMaterialKey"],
            ""
        );
    }

    #[test]
    fn state_combat_creature_projects_next_move_attack_intent() {
        let enemy = run_creature_json(&proto::StateRunCreature {
            source_type: "MegaCrit.Sts2.Core.Entities.Creatures.Creature".to_string(),
            current_hp: 38,
            max_hp: 42,
            id: "creature:5".to_string(),
            model_id: "jaw_worm".to_string(),
            side: "enemy".to_string(),
            slot_name: "front".to_string(),
            block: Some(0),
            is_hittable: Some(true),
            power_instances: vec![proto::StateCombatPowerInstance {
                id: "power:creature:5:0:CRAB_RAGE_POWER".to_string(),
                model_id: "CRAB_RAGE_POWER".to_string(),
                amount: 1,
                display_amount: 1,
                r#type: "Buff".to_string(),
                is_visible: true,
                hover_tips: vec![
                    proto::ModelHoverTipInfo {
                        title: "Crab Rage".to_string(),
                        description: "If a comrade dies, Pincer gains 6 Strength and 99 Block."
                            .to_string(),
                        is_debuff: false,
                        icon_asset_key: "model://powers/crab-rage-power/icon".to_string(),
                    },
                    proto::ModelHoverTipInfo {
                        title: "Strength".to_string(),
                        description: "Strength adds additional damage to attacks.".to_string(),
                        is_debuff: false,
                        icon_asset_key: "model://powers/strength-power/icon".to_string(),
                    },
                ],
                ..Default::default()
            }],
            previewed_card: None,
            next_move: Some(proto::StateCombatNextMove {
                id: "CHOMP".to_string(),
                intents: vec![
                    proto::StateCombatIntent {
                        r#type: "Attack".to_string(),
                        attack: Some(proto::StateCombatAttackIntent {
                            damage: 11,
                            hits: 3,
                            repeats: 2,
                        }),
                        card_count: None,
                    },
                    proto::StateCombatIntent {
                        r#type: "StatusCard".to_string(),
                        attack: None,
                        card_count: Some(2),
                    },
                ],
            }),
        });

        assert_eq!(enemy["id"], "creature:5");
        assert_eq!(enemy["side"], "enemy");
        assert!(enemy.get("combatId").is_none());
        assert!(enemy.get("isStunned").is_none());
        assert_eq!(enemy["nextMove"]["id"], "CHOMP");
        assert_eq!(enemy["nextMove"]["intents"][0]["type"], "Attack");
        assert_eq!(enemy["nextMove"]["intents"][0]["attack"]["damage"], 11);
        assert_eq!(enemy["nextMove"]["intents"][0]["attack"]["hits"], 3);
        assert_eq!(enemy["nextMove"]["intents"][0]["attack"]["repeats"], 2);
        assert!(enemy["nextMove"]["intents"][0]["cardCount"].is_null());
        assert_eq!(enemy["nextMove"]["intents"][1]["type"], "StatusCard");
        assert!(enemy["nextMove"]["intents"][1]["attack"].is_null());
        assert_eq!(enemy["nextMove"]["intents"][1]["cardCount"], 2);
        assert_eq!(
            enemy["powerInstances"][0]["hoverTips"],
            serde_json::json!([
                {
                    "title": "Crab Rage",
                    "description": "If a comrade dies, Pincer gains 6 Strength and 99 Block.",
                    "isDebuff": false,
                    "iconAssetKey": "model://powers/crab-rage-power/icon",
                },
                {
                    "title": "Strength",
                    "description": "Strength adds additional damage to attacks.",
                    "isDebuff": false,
                    "iconAssetKey": "model://powers/strength-power/icon",
                },
            ])
        );
    }

    #[test]
    fn state_combat_card_projects_costs_and_unplayable_reason() {
        let playable = combat_card_json(&proto::StateCombatCard {
            id: "0".to_string(),
            model_id: "strike_red".to_string(),
            upgrade_level: 0,
            current_target_creature_id: "creature:5".to_string(),
            exhaust_on_next_play: false,
            has_single_turn_retain: false,
            has_single_turn_sly: false,
            should_retain_this_turn: false,
            energy_cost: 1,
            star_cost: -1,
            unplayable_reason: Vec::new(),
            should_glow_gold: true,
            should_glow_red: false,
            affliction_model_id: String::new(),
            affliction_amount: 0,
            enchantment: None,
            description_template: String::new(),
            description_text: String::new(),
            preview: None,
        });
        assert_eq!(playable["id"], "0");
        assert_eq!(playable["energyCost"], 1);
        assert_eq!(playable["starCost"], -1);
        assert!(playable["unplayableReason"].is_null());
        assert_eq!(playable["shouldGlowGold"], true);
        assert_eq!(playable["shouldGlowRed"], false);
        assert!(playable.get("ownerPlayerId").is_none());
        assert!(playable.get("currentStarCost").is_none());

        let blocked = combat_card_json(&proto::StateCombatCard {
            id: "1".to_string(),
            model_id: "clash".to_string(),
            upgrade_level: 0,
            current_target_creature_id: String::new(),
            exhaust_on_next_play: false,
            has_single_turn_retain: false,
            has_single_turn_sly: false,
            should_retain_this_turn: false,
            energy_cost: 0,
            star_cost: 2,
            unplayable_reason: vec!["NotEnoughEnergy".to_string(), "NonAttackInHand".to_string()],
            should_glow_gold: false,
            should_glow_red: true,
            affliction_model_id: "HEXED".to_string(),
            affliction_amount: 1,
            enchantment: None,
            description_template: String::new(),
            description_text: String::new(),
            preview: None,
        });
        assert_eq!(blocked["starCost"], 2);
        assert_eq!(
            blocked["unplayableReason"],
            json!(["NotEnoughEnergy", "NonAttackInHand"])
        );
        assert_eq!(blocked["shouldGlowGold"], false);
        assert_eq!(blocked["shouldGlowRed"], true);
        assert_eq!(blocked["afflictionModelId"], "HEXED");
        assert_eq!(blocked["afflictionAmount"], 1);
        assert!(blocked["currentTargetCreatureId"].is_null());
    }

    #[test]
    fn state_combat_state_drops_derivable_id_lists() {
        let combat_state = combat_state_json(&proto::StateCombatState {
            source_type: "MegaCrit.Sts2.Core.Combat.CombatState".to_string(),
            current_side: "ally".to_string(),
            round_number: 2,
            modifier_ids: vec!["mod:1".to_string()],
            escaped_creature_ids: vec!["creature:9".to_string()],
            player_actions_disabled: true,
            enemies: vec![proto::StateRunCreature {
                source_type: "MegaCrit.Sts2.Core.Entities.Creatures.Creature".to_string(),
                current_hp: 38,
                max_hp: 42,
                id: "creature:5".to_string(),
                model_id: "jaw_worm".to_string(),
                side: "enemy".to_string(),
                slot_name: "front".to_string(),
                block: Some(0),
                is_hittable: Some(true),
                power_instances: Vec::new(),
                previewed_card: None,
                next_move: None,
            }],
            transient_effects: vec![
                proto::StateCombatTransientEffect {
                    id: "fx:1".to_string(),
                    anchor_creature_id: "creature:5".to_string(),
                    kind: "damage".to_string(),
                    amount: 12,
                    spawned_at_ms: 1_234,
                    card_model_id: String::new(),
                    card_id: String::new(),
                    source_relic_model_id: String::new(),
                    scene_path: String::new(),
                },
                proto::StateCombatTransientEffect {
                    id: "fx:2".to_string(),
                    anchor_creature_id: String::new(),
                    kind: "cardUpgrade".to_string(),
                    amount: 0,
                    spawned_at_ms: 2_000,
                    card_model_id: "STRIKE_IRONCLAD".to_string(),
                    card_id: "card:1".to_string(),
                    source_relic_model_id: "STONE_CRACKER".to_string(),
                    scene_path: String::new(),
                },
                proto::StateCombatTransientEffect {
                    id: "fx:3".to_string(),
                    anchor_creature_id: "creature:5".to_string(),
                    kind: "vfx".to_string(),
                    amount: 0,
                    spawned_at_ms: 3_000,
                    card_model_id: String::new(),
                    card_id: String::new(),
                    source_relic_model_id: String::new(),
                    scene_path: "vfx/hit_spark_vfx".to_string(),
                },
            ],
        });
        assert_eq!(combat_state["currentSide"], "ally");
        assert_eq!(combat_state["roundNumber"], 2);
        assert_eq!(combat_state["modifierIds"], json!(["mod:1"]));
        assert_eq!(combat_state["escapedCreatureIds"], json!(["creature:9"]));
        assert_eq!(combat_state["playerActionsDisabled"], true);
        assert_eq!(combat_state["enemies"][0]["id"], "creature:5");
        assert!(combat_state["enemies"][0]["nextMove"].is_null());
        assert!(combat_state.get("playerIds").is_none());
        assert!(combat_state.get("allyCreatureIds").is_none());
        assert!(combat_state.get("enemyCreatureIds").is_none());
        // Transient effects (floating damage numbers) project as a camelCase list.
        let fx = &combat_state["transientEffects"][0];
        assert_eq!(fx["id"], "fx:1");
        assert_eq!(fx["anchorCreatureId"], "creature:5");
        assert_eq!(fx["kind"], "damage");
        assert_eq!(fx["amount"], 12);
        assert_eq!(fx["spawnedAtMs"], 1_234);
        // Damage effects carry no card identity (empty strings project as null).
        assert!(fx["cardModelId"].is_null());
        assert!(fx["sourceRelicModelId"].is_null());
        // cardUpgrade effects carry the upgraded card + driving relic, centered (no anchor).
        let upgrade = &combat_state["transientEffects"][1];
        assert_eq!(upgrade["kind"], "cardUpgrade");
        assert!(upgrade["anchorCreatureId"].is_null());
        assert_eq!(upgrade["cardModelId"], "STRIKE_IRONCLAD");
        assert_eq!(upgrade["cardId"], "card:1");
        assert_eq!(upgrade["sourceRelicModelId"], "STONE_CRACKER");
        // vfx effects (the native VFX backbone) carry the scene to mount, anchored to a creature.
        let vfx = &combat_state["transientEffects"][2];
        assert_eq!(vfx["kind"], "vfx");
        assert_eq!(vfx["anchorCreatureId"], "creature:5");
        assert_eq!(vfx["scenePath"], "vfx/hit_spark_vfx");
        // Non-vfx effects carry no scenePath (empty string projects as null).
        assert!(fx["scenePath"].is_null());
    }

    #[test]
    fn state_combat_state_transient_effects_default_empty() {
        let combat_state = combat_state_json(&proto::StateCombatState::default());
        assert_eq!(combat_state["transientEffects"], json!([]));
    }

    #[test]
    fn state_run_player_combat_projects_has_ended_turn() {
        let ended = run_player_combat_json(&proto::StateRunPlayerCombat {
            has_ended_turn: true,
            ..Default::default()
        });
        assert_eq!(ended["hasEndedTurn"], true);

        let acting = run_player_combat_json(&proto::StateRunPlayerCombat::default());
        assert_eq!(acting["hasEndedTurn"], false);
    }

    #[test]
    fn state_combat_potion_projects_empty_slot_as_null_model_id() {
        let filled = combat_potion_json(&proto::StateCombatPotion {
            id: 0,
            model_id: "fire-potion".to_string(),
            is_queued: true,
            passes_usability_check: true,
        });
        assert_eq!(filled["id"], 0);
        assert_eq!(filled["modelId"], "fire-potion");
        assert_eq!(filled["isQueued"], true);
        assert_eq!(filled["passesUsabilityCheck"], true);
        assert!(filled.get("potionId").is_none());
        assert!(filled.get("slotIndex").is_none());
        assert!(filled.get("targetType").is_none());
        assert!(filled.get("requiresTarget").is_none());
        assert!(filled.get("targetIds").is_none());
        assert!(filled.get("usable").is_none());
        assert!(filled.get("unusableReason").is_none());

        let empty = combat_potion_json(&proto::StateCombatPotion {
            id: 1,
            model_id: String::new(),
            is_queued: false,
            passes_usability_check: false,
        });
        assert_eq!(empty["id"], 1);
        assert!(empty["modelId"].is_null());
        assert_eq!(empty["passesUsabilityCheck"], false);
    }

    #[test]
    fn state_json_projects_character_select_view_player_id() {
        let payload = state_json(&proto::StateResponse {
            schema_version: "spirectl.state/v0".to_string(),
            language: "en".to_string(),
            root_scene: "screens/character_select_screen".to_string(),
            character_select: Some(proto::StateCharacterSelect {
                lobby: Some(proto::StateCharacterSelectLobby {
                    net_game_type: "host".to_string(),
                    local_player_id: "p:1".to_string(),
                    host_player_id: "p:1".to_string(),
                    connecting_player_count: 0,
                    ascension: 0,
                    max_ascension: 0,
                    act1: "random".to_string(),
                    seed: String::new(),
                    modifier_ids: Vec::new(),
                    players: Vec::new(),
                    saved_run: None,
                    max_players: 4,
                }),
                character_buttons: Vec::new(),
                view: Some(proto::StateCharacterSelectView {
                    player_id: "p:2".to_string(),
                    selected_character_button_id: "silent".to_string(),
                }),
            }),
            run: None,
        });

        assert_eq!(payload["characterSelect"]["view"]["playerId"], "p:2");
        assert_eq!(
            payload["characterSelect"]["view"]["selectedCharacterButtonId"],
            "silent"
        );
    }

    #[test]
    fn state_json_keeps_map_view_independent_from_map_room() {
        let payload = run_json(&proto::StateRun {
            map: Some(proto::StateRunMap {
                source_type: "MegaCrit.Sts2.Core.Map.ActMap".to_string(),
                view: Some(proto::StateRunMapView {
                    is_open: false,
                    is_accepting_votes: false,
                }),
                ..Default::default()
            }),
            current_room: Some(proto::StateRunCurrentRoom {
                room_type: "Map".to_string(),
                scene: "rooms/map_room".to_string(),
                map_room: Some(proto::StateRunMapRoom {
                    notices: Vec::new(),
                }),
                ..Default::default()
            }),
            ..Default::default()
        });

        assert_eq!(payload["map"]["view"]["isOpen"], false);
        assert!(payload["currentRoom"]["mapRoom"].is_object());
    }

    #[test]
    fn state_json_projects_shop_view_independent_from_inventory() {
        let payload = run_json(&proto::StateRun {
            current_room: Some(proto::StateRunCurrentRoom {
                room_type: "Merchant".to_string(),
                scene: "rooms/merchant_room".to_string(),
                shop: Some(proto::StateRunShopRoom {
                    inventory: Some(proto::StateShopInventory::default()),
                    view: Some(proto::StateShopView { is_open: false }),
                    notices: Vec::new(),
                }),
                ..Default::default()
            }),
            ..Default::default()
        });

        assert_eq!(payload["currentRoom"]["shop"]["view"]["isOpen"], false);
        assert!(payload["currentRoom"]["shop"]["inventory"].is_object());
    }

    #[test]
    fn state_json_projects_deck_view_compact_ui_state() {
        let payload = run_capstone_view_json(&proto::StateRunCapstoneView {
            scene: "screens/deck_view_screen".to_string(),
            source_type: "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen".to_string(),
            stack: Vec::new(),
            deck_view: Some(proto::StateDeckView {
                sort: vec![
                    proto::StateDeckViewSort {
                        by: "obtained".to_string(),
                        direction: "ascending".to_string(),
                    },
                    proto::StateDeckViewSort {
                        by: "type".to_string(),
                        direction: "descending".to_string(),
                    },
                ],
                show_upgrades: true,
            }),
            card_pile_view: None,
        });

        assert_eq!(payload["deckView"]["sort"][0]["by"], "obtained");
        assert_eq!(payload["deckView"]["sort"][0]["direction"], "ascending");
        assert_eq!(payload["deckView"]["sort"][1]["by"], "type");
        assert_eq!(payload["deckView"]["sort"][1]["direction"], "descending");
        assert_eq!(payload["deckView"]["showUpgrades"], true);
        assert!(payload["cardPileView"].is_null());
    }

    #[test]
    fn state_json_projects_card_pile_view() {
        let payload = run_capstone_view_json(&proto::StateRunCapstoneView {
            scene: "screens/card_pile_screen".to_string(),
            source_type: "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen".to_string(),
            stack: Vec::new(),
            deck_view: None,
            card_pile_view: Some(proto::StateCardPileView {
                pile_type: "discard".to_string(),
                player_id: "p1".to_string(),
            }),
        });

        assert_eq!(payload["scene"], "screens/card_pile_screen");
        assert_eq!(payload["cardPileView"]["pileType"], "discard");
        assert_eq!(payload["cardPileView"]["playerId"], "p1");
        assert!(payload["deckView"].is_null());
    }

    #[test]
    fn state_json_projects_treasure_vote_runtime_state() {
        let payload = run_treasure_room_json(&proto::StateRunTreasureRoom {
            current_relics_active: true,
            can_proceed: false,
            current_relics: vec![proto::StateTreasureRelic {
                id: "treasure-relic:0:anchor".to_string(),
                model_id: "anchor".to_string(),
            }],
            player_votes: vec![
                proto::StateTreasurePlayerVote {
                    player_id: "p:100".to_string(),
                    index: None,
                    vote_received: false,
                },
                proto::StateTreasurePlayerVote {
                    player_id: "p:200".to_string(),
                    index: Some(0),
                    vote_received: true,
                },
                proto::StateTreasurePlayerVote {
                    player_id: "p:300".to_string(),
                    index: None,
                    vote_received: true,
                },
            ],
            notices: Vec::new(),
        });

        assert_eq!(payload["currentRelics"][0]["modelId"], "anchor");
        assert!(payload["playerVotes"][0]["index"].is_null());
        assert_eq!(payload["playerVotes"][0]["voteReceived"], false);
        assert_eq!(payload["playerVotes"][1]["index"], 0);
        assert_eq!(payload["playerVotes"][1]["voteReceived"], true);
        assert!(payload["playerVotes"][2]["index"].is_null());
        assert_eq!(payload["playerVotes"][2]["voteReceived"], true);
    }
}
