pub(super) fn mock_character_models() -> Vec<proto::GameModel> {
    vec![
        proto::GameModel {
            family: "characters".to_string(),
            model: Some(proto::game_model::Model::Character(
                proto::CharacterModelInfo {
                    id: "IRONCLAD".to_string(),
                    title: "The Bulwark".to_string(),
                    name_color: "rgb(221 64 56)".to_string(),
                    starting_hp: 80,
                    starting_gold: 99,
                    max_energy: 3,
                    energy_label_outline_color: "rgb(92 28 26)".to_string(),
                    base_orb_slot_count: 0,
                    should_always_show_star_counter: false,
                    starting_relics: vec!["EmberHeart".to_string()],
                    character_select_title: "The Bulwark".to_string(),
                    character_select_desc: "A veteran of the frontier wars.".to_string(),
                    unlock_text: "Complete the prior unlock requirement.".to_string(),
                    dialogue_color: "rgb(221 64 56)".to_string(),
                    speech_bubble_color: "rgb(238 74 74)".to_string(),
                    map_drawing_color: "rgb(221 64 56)".to_string(),
                    visuals_asset_key: "model://characters/ironclad/visuals".to_string(),
                    icon_asset_key: "model://characters/ironclad/icon".to_string(),
                    icon_outline_asset_key: "model://characters/ironclad/iconOutline".to_string(),
                    energy_counter_asset_key: "model://characters/ironclad/energyCounter"
                        .to_string(),
                    merchant_anim_asset_key: "model://characters/ironclad/merchantAnim".to_string(),
                    rest_site_anim_asset_key: "model://characters/ironclad/restSiteAnim"
                        .to_string(),
                    character_select_bg_asset_key: "model://characters/ironclad/characterSelectBg"
                        .to_string(),
                    character_select_bg_spine_still_asset_key:
                        "model://characters/ironclad/characterSelectBgSpineStill".to_string(),
                    character_select_icon_asset_key:
                        "model://characters/ironclad/characterSelectIcon".to_string(),
                    character_select_locked_icon_asset_key:
                        "model://characters/ironclad/characterSelectLockedIcon".to_string(),
                    map_marker_asset_key: "model://characters/ironclad/mapMarker".to_string(),
                    icon_path: "res://images/characters/ironclad/icon.png".to_string(),
                    icon_outline_path: "res://images/characters/ironclad/icon_outline.png"
                        .to_string(),
                    energy_counter_path:
                        "res://scenes/character_energy/ironclad_energy_counter.tscn".to_string(),
                    merchant_anim_path: "res://scenes/character_animations/ironclad_merchant.tscn"
                        .to_string(),
                    rest_site_anim_path:
                        "res://scenes/character_animations/ironclad_rest_site.tscn".to_string(),
                    character_select_bg_path:
                        "res://scenes/screens/char_select/char_select_bg_ironclad.tscn".to_string(),
                    character_select_icon_path: "res://images/characters/ironclad/select_icon.png"
                        .to_string(),
                    character_select_locked_icon_path:
                        "res://images/characters/ironclad/select_locked.png".to_string(),
                    map_marker_path: "res://images/characters/ironclad/map_marker.png".to_string(),
                    ..Default::default()
                },
            )),
        },
        proto::GameModel {
            family: "characters".to_string(),
            model: Some(proto::game_model::Model::Character(
                proto::CharacterModelInfo {
                    id: "THE_SILENT".to_string(),
                    title: "The Shade".to_string(),
                    name_color: "rgb(78 190 92)".to_string(),
                    starting_hp: 70,
                    starting_gold: 99,
                    max_energy: 3,
                    energy_label_outline_color: "rgb(24 82 35)".to_string(),
                    base_orb_slot_count: 0,
                    should_always_show_star_counter: false,
                    starting_relics: vec!["RingOfTheSnake".to_string()],
                    character_select_title: "The Shade".to_string(),
                    character_select_desc: "A deadly hunter from the mistfens.".to_string(),
                    unlock_text: "Complete the prior unlock requirement.".to_string(),
                    dialogue_color: "rgb(78 190 92)".to_string(),
                    speech_bubble_color: "rgb(79 210 99)".to_string(),
                    map_drawing_color: "rgb(78 190 92)".to_string(),
                    visuals_asset_key: "model://characters/silent/visuals".to_string(),
                    icon_asset_key: "model://characters/silent/icon".to_string(),
                    icon_outline_asset_key: "model://characters/silent/iconOutline".to_string(),
                    energy_counter_asset_key: "model://characters/silent/energyCounter".to_string(),
                    merchant_anim_asset_key: "model://characters/silent/merchantAnim".to_string(),
                    rest_site_anim_asset_key: "model://characters/silent/restSiteAnim".to_string(),
                    character_select_bg_asset_key: "model://characters/silent/characterSelectBg"
                        .to_string(),
                    character_select_bg_spine_still_asset_key:
                        "model://characters/silent/characterSelectBgSpineStill".to_string(),
                    character_select_icon_asset_key:
                        "model://characters/silent/characterSelectIcon".to_string(),
                    character_select_locked_icon_asset_key:
                        "model://characters/silent/characterSelectLockedIcon".to_string(),
                    map_marker_asset_key: "model://characters/silent/mapMarker".to_string(),
                    icon_path: "res://images/characters/silent/icon.png".to_string(),
                    icon_outline_path: "res://images/characters/silent/icon_outline.png"
                        .to_string(),
                    energy_counter_path: "res://scenes/character_energy/silent_energy_counter.tscn"
                        .to_string(),
                    merchant_anim_path: "res://scenes/character_animations/silent_merchant.tscn"
                        .to_string(),
                    rest_site_anim_path: "res://scenes/character_animations/silent_rest_site.tscn"
                        .to_string(),
                    character_select_bg_path:
                        "res://scenes/screens/char_select/char_select_bg_silent.tscn".to_string(),
                    character_select_icon_path: "res://images/characters/silent/select_icon.png"
                        .to_string(),
                    character_select_locked_icon_path:
                        "res://images/characters/silent/select_locked.png".to_string(),
                    map_marker_path: "res://images/characters/silent/map_marker.png".to_string(),
                    ..Default::default()
                },
            )),
        },
    ]
}

pub(super) fn mock_relic_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "relics".to_string(),
        model: Some(proto::game_model::Model::Relic(proto::RelicModelInfo {
            id: "EmberHeart".to_string(),
            title: "Ember Heart".to_string(),
            flavor: "A cinder smolders on, refusing to die.".to_string(),
            description: "When a battle ends, restore 6 HP.".to_string(),
            icon_path: "res://images/relics/burning_blood.png".to_string(),
            rarity: "Starter".to_string(),
            icon_asset_key: "model://relics/burning-blood/icon".to_string(),
            icon_outline_asset_key: "model://relics/burning-blood/iconOutline".to_string(),
            big_icon_asset_key: "model://relics/burning-blood/bigIcon".to_string(),
            icon_outline_path: "res://images/relics/burning_blood_outline.png".to_string(),
            big_icon_path: "res://images/relics/burning_blood_big.png".to_string(),
            pool_id: "IroncladRelicPool".to_string(),
            is_tradable: false,
            is_allowed_in_shops: true,
            has_upon_pickup_effect: false,
            spawns_pets: false,
            adds_pet: false,
            is_stackable: false,
            merchant_cost: 999999999,
            show_counter: false,
            flash_sfx: "event:/sfx/ui/relic_activate_general".to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_card_models() -> Vec<proto::GameModel> {
    vec![
        proto::GameModel {
            family: "cards".to_string(),
            model: Some(proto::game_model::Model::Card(proto::CardModelInfo {
                id: "StrikeIronclad".to_string(),
                title: "Jab".to_string(),
                description: "Land 6 harm.".to_string(),
                r#type: "Attack".to_string(),
                rarity: "Basic".to_string(),
                target_type: "AnyEnemy".to_string(),
                pool_id: "IroncladCardPool".to_string(),
                visual_pool_id: "IroncladCardPool".to_string(),
                energy_cost: 1,
                is_energy_x_cost: false,
                star_cost: -1,
                is_star_x_cost: false,
                replay_count: 0,
                keywords: Vec::new(),
                tags: vec!["Strike".to_string()],
                max_upgrade_level: 1,
                upgradable: true,
                upgrade_preview_description: "Land 9 harm.".to_string(),
                can_be_generated_in_combat: true,
                can_be_generated_by_modifiers: true,
                multiplayer_constraint: "None".to_string(),
                should_show_in_card_library: true,
                gains_block: false,
                orb_evoke_type: "None".to_string(),
                has_built_in_overlay: false,
                image_asset_key: "model://cards/strike-ironclad/image".to_string(),
                image_path: "res://images/atlases/card_atlas.sprites/ironclad/strikeironclad.tres"
                    .to_string(),
                beta_image_path:
                    "res://images/atlases/card_atlas.sprites/ironclad/beta/strikeironclad.tres"
                        .to_string(),
                overlay_asset_key: "model://cards/strike-ironclad/overlay".to_string(),
                overlay_path: "res://scenes/cards/overlays/strikeironclad.tscn".to_string(),
                dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 6)]),
                upgrade: Some(proto::CardModelUpgradeInfo {
                    energy_cost: None,
                    dynamic_vars: std::collections::HashMap::from([("damage".to_string(), 9)]),
                }),
                ..Default::default()
            })),
        },
        proto::GameModel {
            family: "cards".to_string(),
            model: Some(proto::game_model::Model::Card(proto::CardModelInfo {
                id: "WhiteNoise".to_string(),
                title: "Static Veil".to_string(),
                description: "Conjure a random Aura card into your hand. It costs 0 this turn.".to_string(),
                r#type: "Skill".to_string(),
                rarity: "Uncommon".to_string(),
                target_type: "Self".to_string(),
                pool_id: "DefectCardPool".to_string(),
                visual_pool_id: "DefectCardPool".to_string(),
                energy_cost: 1,
                star_cost: -1,
                max_upgrade_level: 1,
                upgradable: true,
                upgrade: Some(proto::CardModelUpgradeInfo {
                    energy_cost: Some(0),
                    dynamic_vars: Default::default(),
                }),
                ..Default::default()
            })),
        },
    ]
}

pub(super) fn mock_potion_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "potions".to_string(),
        model: Some(proto::game_model::Model::Potion(proto::PotionModelInfo {
            id: "FirePotion".to_string(),
            title: "Ember Draught".to_string(),
            description: "Land 20 harm.".to_string(),
            selection_screen_prompt: "Choose a target.".to_string(),
            rarity: "Common".to_string(),
            usage: "CombatOnly".to_string(),
            target_type: "AnyEnemy".to_string(),
            pool_id: "SharedPotionPool".to_string(),
            can_be_generated_in_combat: true,
            passes_custom_usability_check: true,
            icon_asset_key: "model://potions/fire-potion/icon".to_string(),
            icon_path: "res://images/atlases/potion_atlas.sprites/firepotion.tres".to_string(),
            outline_asset_key: "model://potions/fire-potion/outline".to_string(),
            outline_path: "res://images/atlases/potion_outline_atlas.sprites/firepotion.tres"
                .to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_event_models() -> Vec<proto::GameModel> {
    let mut models = vec![proto::GameModel {
        family: "events".to_string(),
        model: Some(proto::game_model::Model::Event(proto::EventModelInfo {
            id: "RoomFullOfCheese".to_string(),
            kind: "event".to_string(),
            title: "Hall of Oddities".to_string(),
            initial_description: "An odd aroma drifts through the hall.".to_string(),
            layout_type: "Default".to_string(),
            is_shared: false,
            is_deterministic: true,
            has_vfx: false,
            canonical_encounter_id: String::new(),
            game_info_options: vec!["Search the shelves.".to_string()],
            background_scene_asset_key: "model://events/room-full-of-cheese/backgroundScene"
                .to_string(),
            background_scene_path:
                "res://scenes/events/background_scenes/roomfullofcheese.tscn".to_string(),
            background_spine_still_asset_key:
                "model://events/room-full-of-cheese/backgroundSpineStill".to_string(),
            background_spine_still_path:
                "res://scenes/events/background_scenes/roomfullofcheese.tscn".to_string(),
            initial_portrait_asset_key: "model://events/room-full-of-cheese/initialPortrait"
                .to_string(),
            initial_portrait_path: "res://images/events/roomfullofcheese.png".to_string(),
            vfx_asset_key: "model://events/room-full-of-cheese/vfx".to_string(),
            vfx_path: String::new(),
            epithet: String::new(),
            dialogue_color: String::new(),
            button_color: "rgba(255 255 255 / 0.9)".to_string(),
            ambient_bgm: String::new(),
            has_ambient_bgm: false,
            any_character_dialogue_blacklist_ids: Vec::new(),
            map_icon_asset_key: String::new(),
            map_icon_path: String::new(),
            map_icon_outline_asset_key: String::new(),
            map_icon_outline_path: String::new(),
            run_history_icon_asset_key: String::new(),
            run_history_icon_path: String::new(),
            run_history_icon_outline_asset_key: String::new(),
            run_history_icon_outline_path: String::new(),
            ..Default::default()
        })),
    }];
    models.extend(mock_ancient_models());
    models
}

pub(super) fn mock_ancient_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "events".to_string(),
        model: Some(proto::game_model::Model::Event(proto::EventModelInfo {
            id: "Neow".to_string(),
            kind: "ancient".to_string(),
            title: "The Wyrm".to_string(),
            initial_description: "Select a boon.".to_string(),
            layout_type: "Ancient".to_string(),
            is_shared: false,
            is_deterministic: true,
            has_vfx: false,
            canonical_encounter_id: String::new(),
            game_info_options: vec!["Claim a boon.".to_string()],
            background_scene_asset_key: "model://events/neow/backgroundScene".to_string(),
            background_scene_path: "res://scenes/events/background_scenes/neow.tscn".to_string(),
            background_spine_still_asset_key: "model://events/neow/backgroundSpineStill"
                .to_string(),
            background_spine_still_path: "res://scenes/events/background_scenes/neow.tscn"
                .to_string(),
            initial_portrait_asset_key: "model://events/neow/initialPortrait".to_string(),
            initial_portrait_path: "res://images/events/neow.png".to_string(),
            vfx_asset_key: "model://events/neow/vfx".to_string(),
            vfx_path: String::new(),
            epithet: "The Elder of Rebirth".to_string(),
            dialogue_color: "rgb(40 69 79)".to_string(),
            button_color: "rgba(0 0 0 / 0.35)".to_string(),
            ambient_bgm: String::new(),
            has_ambient_bgm: false,
            any_character_dialogue_blacklist_ids: Vec::new(),
            map_icon_asset_key: "model://events/neow/mapIcon".to_string(),
            map_icon_path: "res://images/packed/map/ancients/ancient_node_neow.png".to_string(),
            map_icon_outline_asset_key: "model://events/neow/mapIconOutline".to_string(),
            map_icon_outline_path:
                "res://images/packed/map/ancients/ancient_node_neow_outline.png".to_string(),
            run_history_icon_asset_key: "model://events/neow/runHistoryIcon".to_string(),
            run_history_icon_path: "res://images/ui/run_history/neow.png".to_string(),
            run_history_icon_outline_asset_key: "model://events/neow/runHistoryIconOutline"
                .to_string(),
            run_history_icon_outline_path: "res://images/ui/run_history/neow_outline.png"
                .to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_act_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "acts".to_string(),
        model: Some(proto::game_model::Model::Act(proto::ActModelInfo {
            id: "Overgrowth".to_string(),
            title: "The Thornwild".to_string(),
            default_order: 1,
            room_count: 15,
            multiplayer_room_count: 14,
            floor_count: 17,
            multiplayer_floor_count: 16,
            boss_encounter_ids: vec!["VantomBoss".to_string()],
            event_ids: vec!["RoomFullOfCheese".to_string()],
            ancient_event_ids: vec!["Neow".to_string()],
            weak_encounter_ids: vec!["SlimesWeak".to_string()],
            regular_encounter_ids: vec!["SlimesNormal".to_string()],
            elite_encounter_ids: vec!["ByrdonisElite".to_string()],
            monster_ids: vec!["Cultist".to_string()],
            bg_music_options: vec!["event:/music/act1_a1_v1".to_string()],
            music_bank_paths: vec!["res://banks/desktop/act1_a1.bank".to_string()],
            ambient_sfx: "event:/sfx/ambience/act1_ambience".to_string(),
            chest_open_sfx: "event:/sfx/ui/treasure/treasure_act1".to_string(),
            map_traveled_color: "rgb(40 35 29)".to_string(),
            map_untraveled_color: "rgb(135 114 86)".to_string(),
            map_bg_color: "rgb(167 138 103)".to_string(),
            background_scene_asset_key: "model://acts/overgrowth/backgroundScene".to_string(),
            background_scene_path:
                "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn".to_string(),
            rest_site_background_asset_key: "model://acts/overgrowth/restSiteBackground"
                .to_string(),
            rest_site_background_path: "res://scenes/rest_site/overgrowth_rest_site.tscn"
                .to_string(),
            map_top_bg_asset_key: "model://acts/overgrowth/mapTopBg".to_string(),
            map_top_bg_path:
                "res://images/packed/map/map_bgs/overgrowth/map_top_overgrowth.png"
                    .to_string(),
            map_mid_bg_asset_key: "model://acts/overgrowth/mapMidBg".to_string(),
            map_mid_bg_path:
                "res://images/packed/map/map_bgs/overgrowth/map_middle_overgrowth.png"
                    .to_string(),
            map_bot_bg_asset_key: "model://acts/overgrowth/mapBotBg".to_string(),
            map_bot_bg_path:
                "res://images/packed/map/map_bgs/overgrowth/map_bottom_overgrowth.png"
                    .to_string(),
            chest_spine_asset_key: "model://acts/overgrowth/chestSpine".to_string(),
            chest_spine_resource_path:
                "res://animations/backgrounds/treasure_room/chest_room_act_1_skel_data.tres"
                    .to_string(),
            combat_background_layers: vec![
                proto::CombatBackgroundLayerInfo {
                    asset_key: "model://acts/overgrowth/backgroundLayer/overgrowth_bg_00_a"
                        .to_string(),
                    res_path:
                        "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"
                            .to_string(),
                    is_foreground: false,
                    bg_group_key: "00".to_string(),
                },
                proto::CombatBackgroundLayerInfo {
                    asset_key: "model://acts/overgrowth/backgroundLayer/overgrowth_bg_01_a"
                        .to_string(),
                    res_path:
                        "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_01_a.tscn"
                            .to_string(),
                    is_foreground: false,
                    bg_group_key: "01".to_string(),
                },
                proto::CombatBackgroundLayerInfo {
                    asset_key: "model://acts/overgrowth/backgroundLayer/overgrowth_fg_a"
                        .to_string(),
                    res_path: "res://scenes/backgrounds/overgrowth/layers/overgrowth_fg_a.tscn"
                        .to_string(),
                    is_foreground: true,
                    bg_group_key: String::new(),
                },
            ],
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_monster_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "monsters".to_string(),
        model: Some(proto::game_model::Model::Monster(proto::MonsterModelInfo {
            id: "JawWorm".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.Monsters.JawWorm".to_string(),
            category_sorting_id: 1,
            entry_sorting_id: 10,
            should_receive_combat_hooks: true,
            title: "Gnash Grub".to_string(),
            min_initial_hp: 40,
            max_initial_hp: 44,
            move_names: vec!["Gnash".to_string()],
            asset_paths: vec!["res://scenes/monsters/jaw_worm.tscn".to_string()],
            visuals_asset_key: "model://monsters/jaw-worm/visuals".to_string(),
            visuals_path: "res://scenes/monsters/jaw_worm.tscn".to_string(),
            bestiary_attack_anim_id: "attack".to_string(),
            can_change_scale: true,
            is_health_bar_visible: true,
            death_anim_length_override: 0.0,
            has_death_anim_length_override: false,
            has_death_sfx: true,
            death_sfx: "event:/sfx/monster/jaw_worm_death".to_string(),
            has_hurt_sfx: true,
            hurt_sfx: "event:/sfx/monster/jaw_worm_hurt".to_string(),
            take_damage_sfx: "event:/sfx/combat/damage_fleshy".to_string(),
            take_damage_sfx_type: "Fleshy".to_string(),
            should_fade_after_death: true,
            should_disappear_from_doom: true,
            hp_bar_size_reduction: 0.0,
            extra_death_vfx_padding: Some(proto::ModelVector2 { x: 4.0, y: 8.0 }),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_encounter_models() -> Vec<proto::GameModel> {
    vec![
        // Regular encounter: no custom background — inherits the act's pool, so it
        // carries no background fields (has_custom_background defaults false).
        proto::GameModel {
            family: "encounters".to_string(),
            model: Some(proto::game_model::Model::Encounter(proto::EncounterModelInfo {
                id: "JawWormWeak".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.Encounters.JawWormWeak".to_string(),
                category_sorting_id: 1,
                entry_sorting_id: 20,
                should_receive_combat_hooks: true,
                title: "Gnash Grub".to_string(),
                room_type: "Monster".to_string(),
                is_weak: true,
                is_debug_encounter: false,
                monster_ids: vec!["JawWorm".to_string()],
                monsters_with_slots: vec![proto::EncounterMonsterSlotInfo {
                    monster_id: "JawWorm".to_string(),
                    slot: "M".to_string(),
                }],
                slots: vec!["M".to_string()],
                tags: vec!["Weak".to_string()],
                min_gold_reward: 10,
                max_gold_reward: 15,
                should_give_rewards: true,
                has_bgm: false,
                custom_bgm: String::new(),
                has_ambient_sfx: false,
                ambient_sfx: String::new(),
                has_scene: true,
                scene_asset_key: "model://encounters/jaw-worm-weak/scene".to_string(),
                scene_path: "res://scenes/encounters/jaw_worm_weak.tscn".to_string(),
                boss_node_path: String::new(),
                map_node_asset_paths: vec!["res://images/map/monster.png".to_string()],
                extra_asset_paths: Vec::new(),
                custom_reward_description: String::new(),
                fully_center_players: false,
                camera_offset: Some(proto::ModelVector2 { x: 0.0, y: -12.0 }),
                camera_scaling: 1.0,
                has_custom_background: false,
                ..Default::default()
            })),
        },
        // Boss encounter with a custom background: carries its own layer pool.
        proto::GameModel {
            family: "encounters".to_string(),
            model: Some(proto::game_model::Model::Encounter(proto::EncounterModelInfo {
                id: "VantomBoss".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.Encounters.VantomBoss".to_string(),
                category_sorting_id: 3,
                entry_sorting_id: 90,
                should_receive_combat_hooks: true,
                title: "Vantom".to_string(),
                room_type: "Boss".to_string(),
                is_weak: false,
                is_debug_encounter: false,
                monster_ids: vec!["Vantom".to_string()],
                monsters_with_slots: vec![proto::EncounterMonsterSlotInfo {
                    monster_id: "Vantom".to_string(),
                    slot: "M".to_string(),
                }],
                slots: vec!["M".to_string()],
                tags: vec!["Boss".to_string()],
                min_gold_reward: 0,
                max_gold_reward: 0,
                should_give_rewards: true,
                has_bgm: true,
                custom_bgm: "event:/music/boss_a1".to_string(),
                has_ambient_sfx: false,
                ambient_sfx: String::new(),
                has_scene: true,
                scene_asset_key: "model://encounters/vantom-boss/scene".to_string(),
                scene_path: "res://scenes/encounters/vantom_boss.tscn".to_string(),
                boss_node_path: String::new(),
                map_node_asset_paths: vec!["res://images/map/boss.png".to_string()],
                extra_asset_paths: Vec::new(),
                custom_reward_description: String::new(),
                fully_center_players: false,
                camera_offset: Some(proto::ModelVector2 { x: 0.0, y: -8.0 }),
                camera_scaling: 1.0,
                has_custom_background: true,
                background_scene_asset_key: "model://encounters/vantom-boss/background".to_string(),
                background_scene_path:
                    "res://scenes/backgrounds/vantomboss/vantomboss_background.tscn".to_string(),
                combat_background_layers: vec![
                    proto::CombatBackgroundLayerInfo {
                        asset_key: "model://encounters/vantom-boss/backgroundLayer/vantomboss_bg_00_a"
                            .to_string(),
                        res_path:
                            "res://scenes/backgrounds/vantomboss/layers/vantomboss_bg_00_a.tscn"
                                .to_string(),
                        is_foreground: false,
                        bg_group_key: "00".to_string(),
                    },
                    proto::CombatBackgroundLayerInfo {
                        asset_key: "model://encounters/vantom-boss/backgroundLayer/vantomboss_fg_a"
                            .to_string(),
                        res_path:
                            "res://scenes/backgrounds/vantomboss/layers/vantomboss_fg_a.tscn"
                                .to_string(),
                        is_foreground: true,
                        bg_group_key: String::new(),
                    },
                ],
                ..Default::default()
            })),
        },
    ]
}

pub(super) fn mock_power_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "powers".to_string(),
        model: Some(proto::game_model::Model::Power(proto::PowerModelInfo {
            id: "Strength".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.Powers.Strength".to_string(),
            category_sorting_id: 2,
            entry_sorting_id: 1,
            should_receive_combat_hooks: true,
            title: "Resolve".to_string(),
            description: "Raises the harm you deal.".to_string(),
            smart_description: "Gain Resolve.".to_string(),
            remote_description: String::new(),
            r#type: "Buff".to_string(),
            stack_type: "Amount".to_string(),
            amount: 0,
            display_amount: 0,
            amount_on_turn_start: 0,
            allow_negative: true,
            is_visible: true,
            is_instanced: false,
            has_smart_description: true,
            has_remote_description: false,
            should_play_vfx: true,
            should_scale_in_multiplayer: true,
            amount_label_color: "rgb(255 255 255)".to_string(),
            icon_asset_key: "model://powers/strength/icon".to_string(),
            icon_path: "res://images/powers/strength.png".to_string(),
            packed_icon_path: "res://images/powers/strength.png".to_string(),
            big_icon_asset_key: "model://powers/strength/bigIcon".to_string(),
            resolved_big_icon_path: "res://images/powers/strength_big.png".to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_orb_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "orbs".to_string(),
        model: Some(proto::game_model::Model::Orb(proto::OrbModelInfo {
            id: "Lightning".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.Orbs.Lightning".to_string(),
            category_sorting_id: 3,
            entry_sorting_id: 1,
            should_receive_combat_hooks: true,
            title: "Lightning".to_string(),
            description: "Passive: harm a random foe.".to_string(),
            smart_description: "Deal charged harm.".to_string(),
            has_smart_description: true,
            passive_val: 3.0,
            evoke_val: 8.0,
            darkened_color: "rgb(90 90 150)".to_string(),
            asset_paths: vec!["res://images/orbs/lightning.png".to_string()],
            icon_asset_key: "model://orbs/lightning/icon".to_string(),
            icon_path: "res://images/orbs/lightning_icon.png".to_string(),
            sprite_asset_key: "model://orbs/lightning/sprite".to_string(),
            sprite_path: "res://images/orbs/lightning.png".to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_affliction_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "afflictions".to_string(),
        model: Some(proto::game_model::Model::Affliction(
            proto::AfflictionModelInfo {
                id: "Hexed".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.Afflictions.Hexed".to_string(),
                category_sorting_id: 4,
                entry_sorting_id: 1,
                should_receive_combat_hooks: true,
                title: "Hexed".to_string(),
                description: "This card bears a curse.".to_string(),
                extra_card_text: "Hexed".to_string(),
                amount: 1,
                is_stackable: false,
                can_afflict_unplayable_cards: false,
                has_extra_card_text: true,
                has_overlay: true,
                overlay_asset_key: "model://afflictions/hexed/overlay".to_string(),
                overlay_path: "res://scenes/cards/afflictions/hexed_overlay.tscn".to_string(),
                ..Default::default()
            },
        )),
    }]
}

pub(super) fn mock_enchantment_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "enchantments".to_string(),
        model: Some(proto::game_model::Model::Enchantment(
            proto::EnchantmentModelInfo {
                id: "Innate".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.Enchantments.Innate".to_string(),
                category_sorting_id: 5,
                entry_sorting_id: 1,
                should_receive_combat_hooks: true,
                title: "Innate".to_string(),
                description: "Begins in your first hand.".to_string(),
                extra_card_text: "Innate".to_string(),
                amount: 1,
                display_amount: 1,
                show_amount: false,
                status: "Permanent".to_string(),
                is_stackable: false,
                has_extra_card_text: true,
                preview_outside_of_combat: true,
                should_glow_gold: true,
                should_glow_red: false,
                should_start_at_bottom_of_draw_pile: false,
                icon_asset_key: "model://enchantments/innate/icon".to_string(),
                icon_path: "res://images/enchantments/innate.png".to_string(),
                intended_icon_path: "res://images/enchantments/innate.png".to_string(),
                missing_icon_path: "res://images/enchantments/missing.png".to_string(),
                ..Default::default()
            },
        )),
    }]
}

pub(super) fn mock_card_pool_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "card-pools".to_string(),
        model: Some(proto::game_model::Model::CardPool(proto::CardPoolModelInfo {
            id: "IroncladCardPool".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.CardPools.IroncladCardPool".to_string(),
            category_sorting_id: 6,
            entry_sorting_id: 1,
            should_receive_combat_hooks: false,
            title: "Bulwark".to_string(),
            card_ids: vec!["StrikeIronclad".to_string()],
            is_colorless: false,
            energy_color_name: "Red".to_string(),
            energy_outline_color: "rgb(92 28 26)".to_string(),
            deck_entry_card_color: "rgb(221 64 56)".to_string(),
            energy_icon_asset_key: "model://card-pools/ironclad-card-pool/energyIcon"
                .to_string(),
            energy_icon_path: "res://images/cards/energy_red.png".to_string(),
            frame_material_asset_key: "model://card-pools/ironclad-card-pool/frameMaterial"
                .to_string(),
            frame_material_path: "res://materials/cards/ironclad_frame.tres".to_string(),
            card_frame_material_path: "res://materials/cards/ironclad_card_frame.tres"
                .to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_relic_pool_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "relic-pools".to_string(),
        model: Some(proto::game_model::Model::RelicPool(proto::RelicPoolModelInfo {
            id: "IroncladRelicPool".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.RelicPools.IroncladRelicPool".to_string(),
            category_sorting_id: 7,
            entry_sorting_id: 1,
            should_receive_combat_hooks: false,
            relic_ids: vec!["EmberHeart".to_string()],
            energy_color_name: "Red".to_string(),
            lab_outline_color: "rgb(221 64 56)".to_string(),
        })),
    }]
}

pub(super) fn mock_potion_pool_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "potion-pools".to_string(),
        model: Some(proto::game_model::Model::PotionPool(
            proto::PotionPoolModelInfo {
                id: "SharedPotionPool".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.PotionPools.SharedPotionPool".to_string(),
                category_sorting_id: 8,
                entry_sorting_id: 1,
                should_receive_combat_hooks: false,
                potion_ids: vec!["FirePotion".to_string()],
                energy_color_name: "Shared".to_string(),
                lab_outline_color: "rgb(255 255 255)".to_string(),
            },
        )),
    }]
}

pub(super) fn mock_modifier_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "modifiers".to_string(),
        model: Some(proto::game_model::Model::Modifier(proto::ModifierModelInfo {
            id: "BigGameHunter".to_string(),
            type_name: "MegaCrit.Sts2.Core.Models.Modifiers.BigGameHunter".to_string(),
            category_sorting_id: 9,
            entry_sorting_id: 1,
            should_receive_combat_hooks: true,
            title: "Trophy Seeker".to_string(),
            description: "Elite foes yield richer spoils.".to_string(),
            neow_option_title: "Trophy Seeker".to_string(),
            neow_option_description: "Seek out mightier foes.".to_string(),
            clears_player_deck: false,
            polarity: "good".to_string(),
            mutually_exclusive_modifier_ids: vec!["SmallGameHunter".to_string()],
            icon_asset_key: "model://modifiers/big-game-hunter/icon".to_string(),
            icon_path: "res://images/modifiers/big_game_hunter.png".to_string(),
            ..Default::default()
        })),
    }]
}

pub(super) fn mock_achievement_models() -> Vec<proto::GameModel> {
    vec![proto::GameModel {
        family: "achievements".to_string(),
        model: Some(proto::game_model::Model::Achievement(
            proto::AchievementModelInfo {
                id: "FirstWin".to_string(),
                type_name: "MegaCrit.Sts2.Core.Models.Achievements.FirstWin".to_string(),
                category_sorting_id: 10,
                entry_sorting_id: 1,
                should_receive_combat_hooks: false,
            },
        )),
    }]
}

pub(super) fn normalize_mock_game_model_id(model: &proto::GameModel) -> String {
    match model.model.as_ref() {
        Some(proto::game_model::Model::Character(character)) => {
            normalize_mock_model_id(&character.id)
        }
        Some(proto::game_model::Model::Relic(relic)) => normalize_mock_model_id(&relic.id),
        Some(proto::game_model::Model::Card(card)) => normalize_mock_model_id(&card.id),
        Some(proto::game_model::Model::Potion(potion)) => normalize_mock_model_id(&potion.id),
        Some(proto::game_model::Model::Event(event)) => normalize_mock_model_id(&event.id),
        Some(proto::game_model::Model::Act(act)) => normalize_mock_model_id(&act.id),
        Some(proto::game_model::Model::Monster(monster)) => normalize_mock_model_id(&monster.id),
        Some(proto::game_model::Model::Encounter(encounter)) => {
            normalize_mock_model_id(&encounter.id)
        }
        Some(proto::game_model::Model::Power(power)) => normalize_mock_model_id(&power.id),
        Some(proto::game_model::Model::Orb(orb)) => normalize_mock_model_id(&orb.id),
        Some(proto::game_model::Model::Affliction(affliction)) => {
            normalize_mock_model_id(&affliction.id)
        }
        Some(proto::game_model::Model::Enchantment(enchantment)) => {
            normalize_mock_model_id(&enchantment.id)
        }
        Some(proto::game_model::Model::CardPool(pool)) => normalize_mock_model_id(&pool.id),
        Some(proto::game_model::Model::RelicPool(pool)) => normalize_mock_model_id(&pool.id),
        Some(proto::game_model::Model::PotionPool(pool)) => normalize_mock_model_id(&pool.id),
        Some(proto::game_model::Model::Modifier(modifier)) => {
            normalize_mock_model_id(&modifier.id)
        }
        Some(proto::game_model::Model::Achievement(achievement)) => {
            normalize_mock_model_id(&achievement.id)
        }
        None => String::new(),
    }
}

pub(super) fn normalize_mock_game_model_id_aliases(model: &proto::GameModel) -> Vec<String> {
    let normalized = normalize_mock_game_model_id(model);
    if normalized.starts_with("the-") {
        let trimmed = normalized["the-".len()..].to_string();
        vec![normalized, trimmed]
    } else {
        vec![normalized]
    }
}

pub(super) fn normalize_mock_model_id(id: &str) -> String {
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut previous_was_lower_or_digit = false;
    for character in id.trim().chars() {
        if character.is_ascii_alphanumeric() {
            if character.is_ascii_uppercase() && previous_was_lower_or_digit && !current.is_empty()
            {
                segments.push(current);
                current = String::new();
            }
            current.push(character.to_ascii_lowercase());
            previous_was_lower_or_digit =
                character.is_ascii_lowercase() || character.is_ascii_digit();
        } else {
            if !current.is_empty() {
                segments.push(current);
                current = String::new();
            }
            previous_was_lower_or_digit = false;
        }
    }
    if !current.is_empty() {
        segments.push(current);
    }
    segments.join("-")
}

pub(super) fn unwrap_mod_list(
    result: proto::ModListResult,
) -> Result<proto::ModListResponse, proto::BridgeError> {
    match result.result {
        Some(proto::mod_list_result::Result::Success(response)) => Ok(response),
        Some(proto::mod_list_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Mod list response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_models(
    result: proto::ModelCatalogResult,
) -> Result<proto::ModelCatalogResponse, proto::BridgeError> {
    match result.result {
        Some(proto::model_catalog_result::Result::Success(response)) => Ok(response),
        Some(proto::model_catalog_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Model catalog response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_combat_preview(
    result: proto::CombatPreviewResult,
) -> Result<proto::CombatPreviewResponse, proto::BridgeError> {
    match result.result {
        Some(proto::combat_preview_result::Result::Success(response)) => Ok(response),
        Some(proto::combat_preview_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Combat preview response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_map_drawings(
    result: proto::MapDrawingsResult,
) -> Result<proto::MapDrawingsResponse, proto::BridgeError> {
    match result.result {
        Some(proto::map_drawings_result::Result::Success(response)) => Ok(response),
        Some(proto::map_drawings_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Map drawings response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_reference(
    result: proto::ReferenceResult,
) -> Result<proto::ReferenceResponse, proto::BridgeError> {
    match result.result {
        Some(proto::reference_result::Result::Success(response)) => Ok(response),
        Some(proto::reference_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Reference response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_hot_reload_status(
    result: proto::HotReloadStatusResult,
) -> Result<proto::HotReloadStatusResponse, proto::BridgeError> {
    match result.result {
        Some(proto::hot_reload_status_result::Result::Success(response)) => Ok(response),
        Some(proto::hot_reload_status_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Hot-reload status response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn unwrap_hot_reload(
    result: proto::HotReloadResult,
) -> Result<proto::HotReloadResponse, proto::BridgeError> {
    match result.result {
        Some(proto::hot_reload_result::Result::Success(response)) => Ok(response),
        Some(proto::hot_reload_result::Result::Error(error)) => Err(error),
        None => Err(error(
            proto::BridgeErrorCode::RuntimeFailure,
            "Hot-reload response was missing both success and error payloads.",
            &[],
        )),
    }
}

pub(super) fn mock_presentation_resource_scene(scene_id: &str) -> proto::PresentationResourceScene {
    let resource_path = format!("res://scenes/{scene_id}.tscn");
    let nodes = match scene_id {
        "game" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 1920.0, 1080.0),
            mock_resource_scene_node("RootSceneContainer", "Control", 0.0, 0.0, 1920.0, 1080.0),
        ],
        "scene_container" => vec![mock_resource_scene_node(
            ".",
            "Control",
            0.0,
            0.0,
            1920.0,
            1080.0,
        )],
        "run" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 1920.0, 1080.0),
            mock_resource_scene_node("RoomContainer", "Control", 0.0, 0.0, 1920.0, 1080.0),
        ],
        "screens/character_select_screen" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 1280.0, 720.0),
            mock_resource_scene_node("AnimatedBg", "TextureRect", 0.0, 0.0, 1280.0, 720.0),
            mock_resource_scene_node("ActLabel", "Label", 920.0, 56.0, 160.0, 24.0),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/Name",
                "RichTextLabel",
                200.0,
                220.0,
                380.0,
                72.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/DescriptionLabel",
                "RichTextLabel",
                200.0,
                306.0,
                450.0,
                96.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Hp/Label",
                "Label",
                240.0,
                304.0,
                100.0,
                28.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Gold/Label",
                "Label",
                360.0,
                304.0,
                100.0,
                28.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/Relic/Icon",
                "TextureRect",
                214.0,
                410.0,
                52.0,
                52.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/Relic/Name/RichTextLabel",
                "RichTextLabel",
                276.0,
                414.0,
                260.0,
                28.0,
            ),
            mock_resource_scene_node(
                "InfoPanel/VBoxContainer/Relic/Description",
                "RichTextLabel",
                276.0,
                442.0,
                380.0,
                60.0,
            ),
            mock_resource_scene_node(
                "CharSelectButtons/ButtonContainer",
                "HBoxContainer",
                460.0,
                602.0,
                360.0,
                92.0,
            ),
            mock_resource_scene_node("BackButton", "TextureButton", 16.0, 520.0, 90.0, 64.0),
            mock_resource_scene_node("ConfirmButton", "TextureButton", 1180.0, 520.0, 90.0, 64.0),
            mock_resource_scene_node(
                "RemotePlayerContainer",
                "VBoxContainer",
                28.0,
                70.0,
                180.0,
                50.0,
            ),
        ],
        "screens/char_select/char_select_button" => vec![
            mock_resource_scene_node(".", "TextureButton", 0.0, 0.0, 80.0, 88.0),
            mock_resource_scene_node(
                "MarginContainer/Mask/Icon",
                "TextureRect",
                8.0,
                6.0,
                64.0,
                64.0,
            ),
            mock_resource_scene_node(
                "MarginContainer/Mask/IconAdd",
                "TextureRect",
                8.0,
                6.0,
                64.0,
                64.0,
            ),
            mock_resource_scene_node("Lock", "TextureRect", 24.0, 28.0, 32.0, 32.0),
            mock_resource_scene_node(
                "PlayerIconContainer",
                "HBoxContainer",
                8.0,
                66.0,
                64.0,
                20.0,
            ),
        ],
        "screens/char_select/char_select_player_icon" => {
            vec![mock_resource_scene_node(
                ".",
                "TextureRect",
                0.0,
                0.0,
                18.0,
                18.0,
            )]
        }
        "ui/remote_lobby_player" => vec![
            mock_resource_scene_node("CharacterIcon", "TextureRect", 0.0, 0.0, 42.0, 42.0),
            mock_resource_scene_node(
                "NameplateContainer/NameplateLabel",
                "Label",
                54.0,
                4.0,
                130.0,
                20.0,
            ),
            mock_resource_scene_node(
                "NameplateContainer/CharacterLabel",
                "Label",
                54.0,
                24.0,
                130.0,
                20.0,
            ),
        ],
        "ui/back_button" | "ui/confirm_button" => {
            vec![mock_resource_scene_node(
                ".",
                "TextureButton",
                0.0,
                0.0,
                92.0,
                64.0,
            )]
        }
        _ => Vec::new(),
    };
    let loaded = !nodes.is_empty();
    proto::PresentationResourceScene {
        scene_id: scene_id.to_string(),
        resource_path,
        loaded,
        nodes,
        notes: Vec::new(),
        unavailable_reason: if loaded {
            String::new()
        } else {
            "mock bridge has no resource scene fixture for this scene".to_string()
        },
    }
}

pub(super) fn mock_presentation_synthetic_layout_scene(
    case_id: &str,
) -> proto::PresentationResourceScene {
    let resource_path = format!("synthetic://presentation-layout/{case_id}");
    let nodes = match case_id {
        "hbox-basic" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 320.0, 180.0),
            mock_resource_scene_node("HBox", "HBoxContainer", 10.0, 20.0, 300.0, 80.0),
            mock_resource_scene_node("HBox/A", "TextureRect", 10.0, 20.0, 40.0, 40.0),
            mock_resource_scene_node("HBox/B", "TextureRect", 58.0, 20.0, 60.0, 40.0),
        ],
        "margin-center" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 320.0, 180.0),
            mock_resource_scene_node("Margin", "MarginContainer", 20.0, 20.0, 200.0, 120.0),
            mock_resource_scene_node("Margin/Center", "CenterContainer", 32.0, 32.0, 176.0, 96.0),
            mock_resource_scene_node(
                "Margin/Center/Label",
                "RichTextLabel",
                88.0,
                70.0,
                64.0,
                20.0,
            ),
        ],
        "grid-basic" => vec![
            mock_resource_scene_node(".", "Control", 0.0, 0.0, 320.0, 180.0),
            mock_resource_scene_node("Grid", "GridContainer", 10.0, 10.0, 160.0, 120.0),
            mock_resource_scene_node("Grid/A", "TextureRect", 10.0, 10.0, 40.0, 40.0),
            mock_resource_scene_node("Grid/B", "TextureRect", 50.0, 10.0, 40.0, 40.0),
            mock_resource_scene_node("Grid/C", "TextureRect", 10.0, 50.0, 40.0, 40.0),
        ],
        _ => Vec::new(),
    };
    let loaded = !nodes.is_empty();
    proto::PresentationResourceScene {
        scene_id: format!("synthetic:{case_id}"),
        resource_path,
        loaded,
        nodes,
        notes: if loaded {
            vec!["mock synthetic layout probe fixture".to_string()]
        } else {
            Vec::new()
        },
        unavailable_reason: if loaded {
            String::new()
        } else {
            "mock bridge has no synthetic layout fixture for this case".to_string()
        },
    }
}

pub(super) fn mock_resource_scene_node(
    node_path: &str,
    node_type: &str,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
) -> proto::RuntimeSceneNodeInfo {
    let rich_text = node_type == "RichTextLabel";
    proto::RuntimeSceneNodeInfo {
        node_id: format!("mock-resource:{node_path}"),
        node_path: node_path.to_string(),
        name: node_path
            .rsplit('/')
            .next()
            .filter(|name| !name.is_empty() && *name != ".")
            .unwrap_or("Root")
            .to_string(),
        node_type: node_type.to_string(),
        parent_node_path: node_path
            .rsplit_once('/')
            .map(|(parent, _)| parent.to_string())
            .unwrap_or_default(),
        owner_path: String::new(),
        scene_file_path: String::new(),
        attached_script_path: String::new(),
        attached_script_type: String::new(),
        child_count: 0,
        notes: Vec::new(),
        properties: Some(proto::RuntimeSceneNodeProperties {
            visible: Some(true),
            effective_visible: Some(true),
            position: Some(proto::RuntimeSceneVector2 { x, y }),
            global_position: Some(proto::RuntimeSceneVector2 { x, y }),
            scale: Some(proto::RuntimeSceneVector2 { x: 1.0, y: 1.0 }),
            size: Some(proto::RuntimeSceneVector2 {
                x: width,
                y: height,
            }),
            anchors: Some(proto::RuntimeSceneAnchors {
                left: Some(0.0),
                top: Some(0.0),
                right: Some(0.0),
                bottom: Some(0.0),
            }),
            offsets: Some(proto::RuntimeSceneOffsets {
                left: Some(x),
                top: Some(y),
                right: Some(x + width),
                bottom: Some(y + height),
            }),
            z_index: Some(0),
            text: if node_type == "Label" || rich_text {
                Some(proto::RuntimeSceneTextProperties {
                    rich_text_enabled: Some(rich_text),
                    source: "mock-resource-scene".to_string(),
                    ..Default::default()
                })
            } else {
                None
            },
            layout: Some(proto::RuntimeSceneLayoutProperties {
                minimum_size: Some(proto::RuntimeSceneVector2 {
                    x: width,
                    y: height,
                }),
                combined_minimum_size: Some(proto::RuntimeSceneVector2 {
                    x: width,
                    y: height,
                }),
                custom_minimum_size: Some(proto::RuntimeSceneVector2 {
                    x: width,
                    y: height,
                }),
                size_flags_horizontal: Some(0),
                size_flags_vertical: Some(0),
                size_flags_stretch_ratio: Some(1.0),
                layout_direction: String::new(),
                theme_type_variation: String::new(),
                container_alignment: None,
                flow_vertical: None,
                theme_constants: if node_type.ends_with("BoxContainer") {
                    vec![proto::RuntimeSceneThemeConstant {
                        name: "separation".to_string(),
                        value: Some(8.0),
                    }]
                } else {
                    Vec::new()
                },
            }),
            ..Default::default()
        }),
        computed_transform: None,
        native_node_type: String::new(),
    }
}

pub(super) fn stub_png_bytes() -> Vec<u8> {
    let image = image::RgbaImage::from_pixel(1, 1, image::Rgba([0, 0, 0, 0]));
    let mut cursor = std::io::Cursor::new(Vec::new());
    image::DynamicImage::ImageRgba8(image)
        .write_to(&mut cursor, image::ImageFormat::Png)
        .expect("encode mock screenshot png");
    cursor.into_inner()
}
