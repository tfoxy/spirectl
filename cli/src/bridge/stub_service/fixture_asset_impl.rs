impl StubBridgeService {
    pub fn load_fixture(&self, request: proto::FixtureLoadRequest) -> proto::FixtureLoadResult {
        let fixture = serde_json::from_str::<Value>(&request.fixture_json).unwrap_or(Value::Null);
        let screen_type = fixture
            .get("screen")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let scenario = match screen_type {
            "main-menu" => MockScenario::MainMenu,
            "combat" => MockScenario::Combat,
            MAP_SCREEN_ID => MockScenario::Map,
            EVENT_ROOM_SCREEN_ID => MockScenario::EventRoom,
            TREASURE_ROOM_SCREEN_ID => MockScenario::TreasureRoom,
            TREASURE_ROOM_RELIC_COLLECTION_SCREEN_ID | RELIC_SELECTION_SCREEN_ID => {
                MockScenario::RelicSelection
            }
            REST_SITE_SCREEN_ID => MockScenario::RestSite,
            SHOP_SCREEN_ID | FAKE_MERCHANT_INVENTORY_SCREEN_ID => MockScenario::Shop,
            REWARDS_SCREEN_ID => MockScenario::Rewards,
            CARD_REWARD_SELECTION_SCREEN_ID | CHOOSE_CARD_SELECTION_SCREEN_ID => {
                MockScenario::CardSelection
            }
            SIMPLE_CARD_SELECTION_SCREEN_ID => MockScenario::SimpleCardSelection,
            DECK_CARD_SELECTION_SCREEN_ID
            | DECK_UPGRADE_SELECTION_SCREEN_ID
            | DECK_TRANSFORM_SELECTION_SCREEN_ID
            | DECK_ENCHANT_SELECTION_SCREEN_ID => MockScenario::DeckCardSelection,
            BUNDLE_SELECTION_SCREEN_ID => MockScenario::BundleSelection,
            "card-overlay" => MockScenario::CardOverlay,
            "passive-card-overlay" => MockScenario::PassiveCardOverlay,
            START_RUN_LOBBY_SCREEN_ID | LOAD_RUN_LOBBY_SCREEN_ID => MockScenario::Lobby,
            _ => {
                return proto::FixtureLoadResult {
                    result: Some(proto::fixture_load_result::Result::Error(error(
                        proto::BridgeErrorCode::NotImplemented,
                        "mock fixture loading supports only the checked-in deterministic fixture families.",
                        &[detail(
                            "screen",
                            screen_type,
                            "Use an authored fixture with a supported screen id.",
                        )],
                    ))),
                };
            }
        };

        MOCK_LOADED_FIXTURE_SCENARIO.with(|loaded| {
            *loaded.borrow_mut() = Some(scenario);
        });
        MOCK_LOADED_FIXTURE_JSON.with(|loaded| {
            *loaded.borrow_mut() = Some(fixture.clone());
        });
        // Fixture current keeps run players at run.players and lobby players at
        // characterSelect.lobby.players; the mock observes whichever is set.
        let fixture_players = fixture
            .get("run")
            .and_then(|run| run.get("players"))
            .or_else(|| {
                fixture
                    .get("characterSelect")
                    .and_then(|character_select| character_select.get("lobby"))
                    .and_then(|lobby| lobby.get("players"))
            })
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        let player_ids = fixture_players
            .iter()
            .filter_map(|player| player.get("id").and_then(Value::as_str).map(str::to_string))
            .collect::<Vec<_>>();
        MOCK_LOADED_PLAYER_IDS.with(|loaded| {
            *loaded.borrow_mut() = player_ids;
        });
        let host_local_seats = fixture_players
            .iter()
            .filter(|player| {
                player
                    .get("isHostLocalSeat")
                    .and_then(Value::as_bool)
                    .unwrap_or(false)
            })
            .filter_map(|player| player.get("id").and_then(Value::as_str).map(str::to_string))
            .collect::<Vec<_>>();
        MOCK_LOADED_HOST_LOCAL_SEATS.with(|loaded| {
            *loaded.borrow_mut() = host_local_seats;
        });
        let locked_characters = fixture
            .get("characterSelect")
            .and_then(|character_select| character_select.get("characterButtons"))
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter(|button| {
                button
                    .get("isLocked")
                    .and_then(Value::as_bool)
                    .unwrap_or(false)
            })
            .filter_map(|button| {
                button
                    .get("characterId")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|character| !character.is_empty())
                    .map(str::to_string)
            })
            .collect::<Vec<_>>();
        MOCK_LOADED_LOCKED_CHARACTERS.with(|loaded| {
            *loaded.borrow_mut() = locked_characters;
        });
        let perspective = fixture
            .get("run")
            .and_then(|run| run.get("view"))
            .or_else(|| {
                fixture
                    .get("characterSelect")
                    .and_then(|character_select| character_select.get("view"))
            })
            .and_then(|view| view.get("playerId"))
            .and_then(Value::as_str)
            .unwrap_or(match scenario {
                MockScenario::Lobby | MockScenario::LobbyReady => "p:100",
                MockScenario::SimpleCardSelection
                | MockScenario::DeckCardSelection
                | MockScenario::RelicSelection
                | MockScenario::CardOverlay
                | MockScenario::PassiveCardOverlay => "p1",
                _ => "p1",
            });

        proto::FixtureLoadResult {
            result: Some(proto::fixture_load_result::Result::Success(
                proto::FixtureLoadResponse {
                    request_id: request.request_id,
                    fixture_name: request.fixture_name,
                    source_path: request.source_path,
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    screen: Some(proto::ScreenInfo {
                        id: screen_type.to_string(),
                        title: mock_screen_title(screen_type).to_string(),
                        screen_instance_id: format!("screen:{screen_type}:mock-fixture"),
                        source: "mock-fixture".to_string(),
                        raw_type: String::new(),
                        class_name: String::new(),
                    }),
                    resolved_perspective: Some(proto::PerspectiveInfo {
                        scope: proto::PerspectiveScope::Local as i32,
                        player_id: perspective.to_string(),
                        uses_default: false,
                        ..Default::default()
                    }),
                    notices: vec![proto::StateNotice {
                        code: "mock-fixture-loaded".to_string(),
                        message:
                            "Mock transport loaded deterministic fixture-backed state locally."
                                .to_string(),
                        provisional: true,
                        path: "state".to_string(),
                        severity: "info".to_string(),
                        source: "MockBridgeService".to_string(),
                        stability: "stable".to_string(),
                        perspective: "local".to_string(),
                    }],
                    recipe_report: Some(proto::FixtureRecipeRestoreReport {
                        recipe_name: format!("{screen_type}-recipe"),
                        applied_fields: vec![proto::FixtureRecipeFieldReport {
                            field_path: "screen".to_string(),
                            value_summary: screen_type.to_string(),
                            reason_code: "applied_authored_field".to_string(),
                            message: "Applied authored fixture screen id.".to_string(),
                        }],
                        inferred_fields: Vec::new(),
                        omitted_fields: Vec::new(),
                        unsupported_fields: Vec::new(),
                        degraded_multiplayer_fields: Vec::new(),
                        bridge_validation: Some(proto::FixtureBridgeValidationResult {
                            status: "passed".to_string(),
                            details: Vec::new(),
                        }),
                    }),
                },
            )),
        }
    }

    pub fn record_fixture(
        &self,
        request: proto::RecordedFixtureRequest,
    ) -> proto::RecordedFixtureResult {
        let scenario = self.active_scenario();
        match scenario {
            MockScenario::Combat => proto::RecordedFixtureResult {
                result: Some(proto::recorded_fixture_result::Result::Success(
                    proto::RecordedFixtureResponse {
                        request_id: request.request_id,
                        fixture_yaml: String::new(),
                        fixture_json: recorded_fixture_json_for_mock_combat(),
                        metadata: Some(proto::RecordedFixtureMetadata {
                            schema_version: "spirectl.recorded-fixture/v0".to_string(),
                            recorded_at: "2026-04-24T00:00:00Z".to_string(),
                            screen: Some(proto::ScreenInfo {
                                id: "combat".to_string(),
                                title: "Combat".to_string(),
                                screen_instance_id: "screen:combat:1".to_string(),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            game_version: "unknown".to_string(),
                            bridge_version: BRIDGE_VERSION.to_string(),
                            spirectl_version: env!("CARGO_PKG_VERSION").to_string(),
                            restore_quality: proto::RestoreQuality::Partial as i32,
                            known_omissions: recorded_fixture_combat_omissions(),
                            notices: recorded_fixture_entry_notices(),
                        }),
                        source: proto::DataSource::Stub as i32,
                        provisional: true,
                    },
                )),
            },
            _ => proto::RecordedFixtureResult {
                result: Some(proto::recorded_fixture_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "The current screen is unsupported for recorded fixture capture.",
                    &[detail(
                        "screen.id",
                        mock_screen_type(scenario),
                        "M59 only records screens with authoritative fixture recipe data.",
                    )],
                ))),
            },
        }
    }

    pub fn get_screenshot(&self, request: proto::ScreenshotRequest) -> proto::ScreenshotResult {
        let scenario = self.active_scenario();
        let (screen_type, screen_instance_id) = match scenario {
            MockScenario::MainMenu => ("main-menu", "screen:main-menu:1"),
            MockScenario::Combat => ("combat", "screen:combat:1"),
            MockScenario::Map => (MAP_SCREEN_ID, "screen:map:1"),
            MockScenario::EventRoom => (EVENT_ROOM_SCREEN_ID, "screen:event-room:1"),
            MockScenario::TreasureRoom => (TREASURE_ROOM_SCREEN_ID, "screen:treasure-room:1"),
            MockScenario::RelicSelection => (RELIC_SELECTION_SCREEN_ID, "screen:relic-selection:1"),
            MockScenario::RestSite => (REST_SITE_SCREEN_ID, "screen:rest-site:1"),
            MockScenario::Shop => (SHOP_SCREEN_ID, "screen:shop:1"),
            MockScenario::FakeMerchantPreOpen => {
                (EVENT_ROOM_SCREEN_ID, "screen:event-room:NFakeMerchant")
            }
            MockScenario::CrystalSphere => (
                CRYSTAL_SPHERE_SCREEN_ID,
                "screen:crystal-sphere:NCrystalSphereScreen",
            ),
            MockScenario::CrystalSphereFinished => (
                CRYSTAL_SPHERE_SCREEN_ID,
                "screen:crystal-sphere:NCrystalSphereScreen",
            ),
            MockScenario::Rewards => (REWARDS_SCREEN_ID, "screen:rewards:1"),
            MockScenario::CardSelection => {
                (CARD_REWARD_SELECTION_SCREEN_ID, "screen:card-selection:1")
            }
            MockScenario::SimpleCardSelection => (
                SIMPLE_CARD_SELECTION_SCREEN_ID,
                "screen:simple-card-selection:1",
            ),
            MockScenario::DeckCardSelection => (
                DECK_CARD_SELECTION_SCREEN_ID,
                "screen:deck-card-selection:1",
            ),
            MockScenario::BundleSelection => {
                (BUNDLE_SELECTION_SCREEN_ID, "screen:bundle-selection:1")
            }
            MockScenario::CardOverlay => ("card-overlay", "screen:card-overlay:1"),
            MockScenario::PassiveCardOverlay => {
                (SHOP_SCREEN_ID, "screen:shop:passive-card-overlay")
            }
            MockScenario::Lobby => (START_RUN_LOBBY_SCREEN_ID, "screen:lobby:start-run:1"),
            MockScenario::LobbyReady => (START_RUN_LOBBY_SCREEN_ID, "screen:lobby:start-run:ready"),
        };

        proto::ScreenshotResult {
            result: Some(proto::screenshot_result::Result::Success(
                proto::ScreenshotResponse {
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    format: "png".to_string(),
                    width: 1,
                    height: 1,
                    contents: stub_png_bytes(),
                    screen_type: screen_type.to_string(),
                    screen_instance_id: screen_instance_id.to_string(),
                    requested_viewport_width: request.viewport_width,
                    requested_viewport_height: request.viewport_height,
                    applied_viewport_width: if request.viewport_width > 0 {
                        request.viewport_width
                    } else {
                        1
                    },
                    applied_viewport_height: if request.viewport_height > 0 {
                        request.viewport_height
                    } else {
                        1
                    },
                    restored_viewport: request.viewport_width > 0 && request.viewport_height > 0,
                    restored_viewport_width: if request.viewport_width > 0 { 1 } else { 0 },
                    restored_viewport_height: if request.viewport_height > 0 { 1 } else { 0 },
                },
            )),
        }
    }

    pub fn get_runtime_scene_tree(
        &self,
        _request: proto::RuntimeSceneTreeRequest,
    ) -> proto::RuntimeSceneTreeResult {
        proto::RuntimeSceneTreeResult {
            result: Some(proto::runtime_scene_tree_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "runtime scene inspection requires the live STS2 bridge host.",
                &[detail(
                    "command",
                    "dev scene tree",
                    "Mock transport does not expose the live Godot scene tree.",
                )],
            ))),
        }
    }

    pub fn get_runtime_scene_node(
        &self,
        _request: proto::RuntimeSceneNodeRequest,
    ) -> proto::RuntimeSceneNodeResult {
        proto::RuntimeSceneNodeResult {
            result: Some(proto::runtime_scene_node_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "runtime scene inspection requires the live STS2 bridge host.",
                &[detail(
                    "command",
                    "dev scene node",
                    "Mock transport does not expose the live Godot scene tree.",
                )],
            ))),
        }
    }

    pub fn set_runtime_scene_node_visible(
        &self,
        _request: proto::RuntimeSceneSetVisibleRequest,
    ) -> proto::RuntimeSceneSetVisibleResult {
        proto::RuntimeSceneSetVisibleResult {
            result: Some(proto::runtime_scene_set_visible_result::Result::Error(
                error(
                    proto::BridgeErrorCode::NotImplemented,
                    "runtime scene mutation requires the live STS2 bridge host.",
                    &[detail(
                        "command",
                        "dev scene set-visible",
                        "Mock transport does not expose the live Godot scene tree.",
                    )],
                ),
            )),
        }
    }

    pub fn hover_runtime_scene_control(
        &self,
        _request: proto::RuntimeSceneControlHoverRequest,
    ) -> proto::RuntimeSceneControlHoverResult {
        proto::RuntimeSceneControlHoverResult {
            result: Some(proto::runtime_scene_control_hover_result::Result::Error(
                error(
                    proto::BridgeErrorCode::NotImplemented,
                    "runtime scene control hover requires the live STS2 bridge host.",
                    &[detail(
                        "command",
                        "dev scene hover",
                        "Mock transport does not expose the live Godot scene tree.",
                    )],
                ),
            )),
        }
    }

    pub fn unhover_runtime_scene_control(
        &self,
        _request: proto::RuntimeSceneControlUnhoverRequest,
    ) -> proto::RuntimeSceneControlUnhoverResult {
        proto::RuntimeSceneControlUnhoverResult {
            result: Some(proto::runtime_scene_control_unhover_result::Result::Error(
                error(
                    proto::BridgeErrorCode::NotImplemented,
                    "runtime scene control unhover requires the live STS2 bridge host.",
                    &[detail(
                        "command",
                        "dev scene unhover",
                        "Mock transport does not expose the live Godot scene tree.",
                    )],
                ),
            )),
        }
    }

    /// Mock transitions settle after one sample: the fixture scene is static, so
    /// everything after the first "still running" sample is quiescent. That keeps
    /// `dev wait-for-transitions` and the lifecycle `--wait-quiescent-ms` waits
    /// exercisable without a live host, instead of failing as not-implemented.
    pub fn get_runtime_transition_status(
        &self,
        _request: proto::RuntimeTransitionStatusRequest,
    ) -> proto::RuntimeTransitionStatusResult {
        let scenario = self.active_scenario();
        let screen_id = mock_screen_type(scenario);
        let settling = self.transition_sample_is_settling();
        proto::RuntimeTransitionStatusResult {
            result: Some(proto::runtime_transition_status_result::Result::Success(
                proto::RuntimeTransitionStatusResponse {
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    screen: Some(proto::ScreenInfo {
                        id: screen_id.to_string(),
                        title: screen_id.to_string(),
                        screen_instance_id: format!("screen:{screen_id}:1"),
                        ..Default::default()
                    }),
                    quiescent: !settling,
                    blocking_count: u32::from(settling),
                    ignored_infinite_count: 0,
                    blockers: settling
                        .then(|| proto::RuntimeTransitionBlocker {
                            kind: "animation".to_string(),
                            node_path: "/root/Mock/Transition".to_string(),
                            node_type: "Godot.AnimationPlayer".to_string(),
                            name: "Transition".to_string(),
                            status: "running".to_string(),
                            reason: "mock-initial-sample".to_string(),
                            animation_name: Some("intro".to_string()),
                            loops_left: Some(0),
                            running: Some(true),
                            infinite: Some(false),
                        })
                        .into_iter()
                        .collect(),
                    notes: vec![
                        "Mock transport reports a settled scene after the first sample."
                            .to_string(),
                    ],
                },
            )),
        }
    }

    pub fn inspect_presentation_resource_scenes(
        &self,
        request: proto::PresentationResourceSceneRequest,
    ) -> proto::PresentationResourceSceneResult {
        let mut scenes = request
            .scene_ids
            .iter()
            .map(|scene_id| mock_presentation_resource_scene(scene_id))
            .collect::<Vec<_>>();
        scenes.extend(
            request
                .synthetic_probe_ids
                .iter()
                .map(|case_id| mock_presentation_synthetic_layout_scene(case_id)),
        );
        proto::PresentationResourceSceneResult {
            result: Some(proto::presentation_resource_scene_result::Result::Success(
                proto::PresentationResourceSceneResponse {
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    scenes,
                    notes: Vec::new(),
                },
            )),
        }
    }

    pub fn inspect_presentation_localization(
        &self,
        request: proto::PresentationLocalizationRequest,
    ) -> proto::PresentationLocalizationResult {
        proto::PresentationLocalizationResult {
            result: Some(proto::presentation_localization_result::Result::Success(
                proto::PresentationLocalizationResponse {
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    language: request.language,
                    tables: request
                        .table_names
                        .into_iter()
                        .map(|name| proto::PresentationLocalizationTable {
                            entries: mock_presentation_localization_entries(&name),
                            name,
                        })
                        .collect(),
                    diagnostics: Vec::new(),
                    notes: Vec::new(),
                },
            )),
        }
    }

    pub fn extract_asset(&self, request: proto::AssetExtractRequest) -> proto::AssetExtractResult {
        proto::AssetExtractResult {
            result: Some(proto::asset_extract_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "live asset extraction requires the live STS2 bridge host.",
                &[detail(
                    "command",
                    "assets extract",
                    &format!(
                        "Mock transport cannot extract '{}'; retry through transport.kind=ipc after launching or attaching the live bridge.",
                        request.source_path
                    ),
                )],
            ))),
        }
    }

    pub fn get_asset_catalog(
        &self,
        _request: proto::AssetCatalogRequest,
    ) -> proto::AssetCatalogResult {
        proto::AssetCatalogResult {
            result: Some(proto::asset_catalog_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "live asset catalog enumeration requires the live STS2 bridge host.",
                &[detail(
                    "command",
                    "assets catalog",
                    "Mock transport cannot enumerate live ModelDb-backed asset ids; retry through transport.kind=ipc after launching or attaching the live bridge.",
                )],
            ))),
        }
    }

    pub fn explain_asset(&self, request: proto::AssetExplainRequest) -> proto::AssetExplainResult {
        proto::AssetExplainResult {
            result: Some(proto::asset_explain_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "asset composition explanation requires the live STS2 bridge host.",
                &[detail(
                    "command",
                    "assets explain",
                    &format!(
                        "Mock transport cannot explain '{}'; retry through transport.kind=ipc after launching or attaching the live bridge.",
                        request.source_path
                    ),
                )],
            ))),
        }
    }

    pub fn close_game(&self, request: proto::GameCloseRequest) -> proto::GameCloseResult {
        proto::GameCloseResult {
            result: Some(proto::game_close_result::Result::Success(
                proto::GameCloseResponse {
                    request_id: request.request_id,
                    accepted: true,
                    notices: vec!["Requested mock game close.".to_string()],
                },
            )),
        }
    }

    pub fn get_mods(&self, request: proto::ModListRequest) -> proto::ModListResult {
        proto::ModListResult {
            result: Some(proto::mod_list_result::Result::Success(
                proto::ModListResponse {
                    request_id: request.request_id,
                    mods: vec![proto::LiveModInfo {
                        id: "spirectlbridge".to_string(),
                        name: "spirectl bridge".to_string(),
                        version: env!("SPIRECTL_BRIDGE_SEMVER").to_string(),
                        source: "mock".to_string(),
                        path: String::new(),
                        load_state: proto::LiveModLoadState::Loaded as i32,
                        enabled: true,
                        active: true,
                        assembly_path: String::new(),
                        errors: Vec::new(),
                    }],
                    notices: vec![
                        "Mock transport reports a synthetic active mod list.".to_string(),
                    ],
                },
            )),
        }
    }

    pub fn get_combat_preview(
        &self,
        request: proto::CombatPreviewRequest,
    ) -> proto::CombatPreviewResult {
        // The stub bridge has no live combat; report inactive with an empty matrix.
        proto::CombatPreviewResult {
            result: Some(proto::combat_preview_result::Result::Success(
                proto::CombatPreviewResponse {
                    request_id: request.request_id,
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    combat_active: false,
                    cards: Default::default(),
                },
            )),
        }
    }

    pub fn get_models(&self, request: proto::ModelCatalogRequest) -> proto::ModelCatalogResult {
        let family = request.family.trim().to_ascii_lowercase();
        let reference_mode = request.language.trim().is_empty();
        let language = if reference_mode {
            String::new()
        } else if request.language.trim().eq_ignore_ascii_case("auto") {
            "mock".to_string()
        } else {
            request.language.trim().to_string()
        };
        let all_models = match family.as_str() {
            "characters" => mock_character_models(),
            "relics" => mock_relic_models(),
            "cards" => mock_card_models(),
            "potions" => mock_potion_models(),
            "events" => mock_event_models(),
            "ancients" => mock_ancient_models(),
            "acts" => mock_act_models(),
            "monsters" => mock_monster_models(),
            "encounters" => mock_encounter_models(),
            "powers" => mock_power_models(),
            "orbs" => mock_orb_models(),
            "afflictions" => mock_affliction_models(),
            "enchantments" => mock_enchantment_models(),
            "card-pools" => mock_card_pool_models(),
            "relic-pools" => mock_relic_pool_models(),
            "potion-pools" => mock_potion_pool_models(),
            "modifiers" => mock_modifier_models(),
            "achievements" => mock_achievement_models(),
            _ => {
                return proto::ModelCatalogResult {
                    result: Some(proto::model_catalog_result::Result::Success(
                        proto::ModelCatalogResponse {
                            request_id: request.request_id,
                            source: proto::DataSource::Stub as i32,
                            provisional: true,
                            family,
                            language,
                            status: proto::ModelCatalogStatus::UnsupportedFamily as i32,
                            models: Vec::new(),
                            missing_ids: Vec::new(),
                            notices: vec![proto::ModelCatalogNotice {
                                code: "unsupported-model-family".to_string(),
                                severity: "warning".to_string(),
                                message: "Unsupported model family.".to_string(),
                                path: "family".to_string(),
                            }],
                        },
                    )),
                };
            }
        };

        let mut missing = Vec::new();
        let mut models = if request.ids.is_empty() {
            all_models
        } else {
            request
                .ids
                .iter()
                .filter_map(|id| {
                    let normalized = normalize_mock_model_id(id);
                    let found = all_models
                        .iter()
                        .find(|model| {
                            normalize_mock_game_model_id_aliases(model).contains(&normalized)
                        })
                        .cloned();
                    if found.is_none() {
                        missing.push(id.clone());
                    }
                    found
                })
                .collect()
        };
        if reference_mode {
            models = models
                .into_iter()
                .map(mock_model_localization_refs)
                .collect();
        }

        proto::ModelCatalogResult {
            result: Some(proto::model_catalog_result::Result::Success(
                proto::ModelCatalogResponse {
                    request_id: request.request_id,
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    family,
                    language,
                    status: if missing.is_empty() {
                        proto::ModelCatalogStatus::Ok as i32
                    } else {
                        proto::ModelCatalogStatus::Partial as i32
                    },
                    models,
                    missing_ids: missing,
                    notices: Vec::new(),
                },
            )),
        }
    }

    pub fn get_reference(&self, request: proto::ReferenceRequest) -> proto::ReferenceResult {
        let topic = request.topic.trim().to_ascii_lowercase();
        let response = match topic.as_str() {
            "colors" => {
                let all = vec![
                    proto::GameColor {
                        name: "aqua".to_string(),
                        hex: "2aebbe".to_string(),
                        r: 0.164,
                        g: 0.921,
                        b: 0.745,
                        a: 1.0,
                    },
                    proto::GameColor {
                        name: "gold".to_string(),
                        hex: "efc851".to_string(),
                        r: 0.937,
                        g: 0.784,
                        b: 0.318,
                        a: 1.0,
                    },
                    proto::GameColor {
                        name: "screenBackdrop".to_string(),
                        hex: "000000cc".to_string(),
                        r: 0.0,
                        g: 0.0,
                        b: 0.0,
                        a: 0.8,
                    },
                ];
                let mut missing = Vec::new();
                let colors = if request.keys.is_empty() {
                    all
                } else {
                    request
                        .keys
                        .iter()
                        .filter_map(|key| {
                            let found = all
                                .iter()
                                .find(|color| color.name.eq_ignore_ascii_case(key))
                                .cloned();
                            if found.is_none() {
                                missing.push(key.clone());
                            }
                            found
                        })
                        .collect()
                };
                proto::ReferenceResponse {
                    request_id: request.request_id,
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    topic,
                    status: if missing.is_empty() {
                        proto::ReferenceStatus::Ok as i32
                    } else {
                        proto::ReferenceStatus::Partial as i32
                    },
                    missing_keys: missing,
                    notices: Vec::new(),
                    payload: Some(proto::reference_response::Payload::Colors(
                        proto::GameColors { colors },
                    )),
                }
            }
            "version" => proto::ReferenceResponse {
                request_id: request.request_id,
                source: proto::DataSource::Stub as i32,
                provisional: true,
                topic,
                status: proto::ReferenceStatus::Ok as i32,
                missing_keys: Vec::new(),
                notices: Vec::new(),
                payload: Some(proto::reference_response::Payload::Version(
                    proto::GameVersionInfo {
                        version: "v0.0.0-mock".to_string(),
                        version_date: "2026.01.01".to_string(),
                        commit: "mock".to_string(),
                        branch: "mock".to_string(),
                        main_assembly_hash: 0,
                        steam_branch: String::new(),
                        steam_build_id: 0,
                        steam_branch_source: String::new(),
                        modding: Some(proto::ModdingSummary {
                            is_running_modded: true,
                            loaded_mod_count: 1,
                            total_mod_count: 1,
                        }),
                    },
                )),
            },
            _ => proto::ReferenceResponse {
                request_id: request.request_id,
                source: proto::DataSource::Stub as i32,
                provisional: true,
                topic,
                status: proto::ReferenceStatus::UnsupportedTopic as i32,
                missing_keys: Vec::new(),
                notices: vec![proto::ReferenceNotice {
                    code: "reference-unsupported-topic".to_string(),
                    severity: "warning".to_string(),
                    message: "Unsupported reference topic.".to_string(),
                    path: "topic".to_string(),
                }],
                payload: None,
            },
        };

        proto::ReferenceResult {
            result: Some(proto::reference_result::Result::Success(response)),
        }
    }
}

fn mock_loc_ref(table: &str, key: &str) -> Option<proto::ModelLocalizationRef> {
    Some(proto::ModelLocalizationRef {
        table: table.to_string(),
        key: key.to_string(),
    })
}

fn mock_presentation_localization_entries(name: &str) -> std::collections::HashMap<String, String> {
    match name {
        "characters" => [
            ("IRONCLAD.characterSelectTitle", "Bulwark"),
            ("IRONCLAD.characterSelectDesc", "Bulwark description."),
            ("IRONCLAD.unlockText", ""),
            ("THE_SILENT.characterSelectTitle", "Shade"),
            ("THE_SILENT.characterSelectDesc", "Shade description."),
            ("THE_SILENT.unlockText", "Unlock the Shade"),
            ("RANDOM_CHARACTER.characterSelectTitle", "Random"),
            (
                "RANDOM_CHARACTER.characterSelectDesc",
                "Random description.",
            ),
        ]
        .into_iter()
        .map(|(key, value)| (key.to_string(), value.to_string()))
        .collect(),
        "relics" => [
            ("EmberHeart.title", "Ember Heart"),
            ("EmberHeart.description", "Restore health after a battle."),
            ("RingOfTheSnake.title", "Serpent Coil"),
            ("RingOfTheSnake.description", "Draw extra cards."),
        ]
        .into_iter()
        .map(|(key, value)| (key.to_string(), value.to_string()))
        .collect(),
        _ => Default::default(),
    }
}

fn mock_model_localization_refs(mut model: proto::GameModel) -> proto::GameModel {
    match model.model.as_mut() {
        Some(proto::game_model::Model::Character(character)) => {
            character.title.clear();
            character.character_select_title.clear();
            character.character_select_desc.clear();
            character.unlock_text.clear();
            character.title_loc = mock_loc_ref("characters", &format!("{}.title", character.id));
            character.character_select_title_loc = mock_loc_ref(
                "characters",
                &format!("{}.characterSelectTitle", character.id),
            );
            character.character_select_desc_loc = mock_loc_ref(
                "characters",
                &format!("{}.characterSelectDesc", character.id),
            );
            character.unlock_text_loc =
                mock_loc_ref("characters", &format!("{}.unlockText", character.id));
        }
        Some(proto::game_model::Model::Relic(relic)) => {
            relic.title.clear();
            relic.flavor.clear();
            relic.description.clear();
            relic.title_loc = mock_loc_ref("relics", &format!("{}.title", relic.id));
            relic.flavor_loc = mock_loc_ref("relics", &format!("{}.flavor", relic.id));
            relic.description_loc = mock_loc_ref("relics", &format!("{}.description", relic.id));
        }
        Some(proto::game_model::Model::Card(card)) => {
            card.title.clear();
            card.description.clear();
            card.upgrade_preview_description.clear();
            card.title_loc = mock_loc_ref("cards", &format!("{}.title", card.id));
            card.description_loc = mock_loc_ref("cards", &format!("{}.description", card.id));
            card.upgrade_preview_description_loc =
                mock_loc_ref("cards", &format!("{}.upgradePreviewDescription", card.id));
        }
        Some(proto::game_model::Model::Potion(potion)) => {
            potion.title.clear();
            potion.description.clear();
            potion.selection_screen_prompt.clear();
            potion.title_loc = mock_loc_ref("potions", &format!("{}.title", potion.id));
            potion.description_loc = mock_loc_ref("potions", &format!("{}.description", potion.id));
            potion.selection_screen_prompt_loc =
                mock_loc_ref("potions", &format!("{}.selectionScreenPrompt", potion.id));
        }
        Some(proto::game_model::Model::Event(event)) => {
            let table = if event.kind == "ancient" {
                "ancients"
            } else {
                "events"
            };
            event.title.clear();
            event.initial_description.clear();
            event.epithet.clear();
            event.title_loc = mock_loc_ref(table, &format!("{}.title", event.id));
            event.initial_description_loc =
                mock_loc_ref(table, &format!("{}.initialDescription", event.id));
            event.epithet_loc = mock_loc_ref("ancients", &format!("{}.epithet", event.id));
        }
        Some(proto::game_model::Model::Act(act)) => {
            act.title.clear();
            act.title_loc = mock_loc_ref("acts", &format!("{}.title", act.id));
        }
        Some(proto::game_model::Model::Monster(monster)) => {
            monster.title.clear();
            let move_name_locs = monster
                .move_names
                .iter()
                .enumerate()
                .map(|(index, _)| proto::ModelLocalizationRef {
                    table: "monsters".to_string(),
                    key: format!("{}.moveNames.{index}", monster.id),
                })
                .collect();
            monster.move_names.clear();
            monster.title_loc = mock_loc_ref("monsters", &format!("{}.title", monster.id));
            monster.move_name_locs = move_name_locs;
        }
        Some(proto::game_model::Model::Encounter(encounter)) => {
            encounter.title.clear();
            encounter.custom_reward_description.clear();
            encounter.title_loc = mock_loc_ref("encounters", &format!("{}.title", encounter.id));
            encounter.custom_reward_description_loc = mock_loc_ref(
                "encounters",
                &format!("{}.customRewardDescription", encounter.id),
            );
        }
        Some(proto::game_model::Model::Power(power)) => {
            power.title.clear();
            power.description.clear();
            power.smart_description.clear();
            power.remote_description.clear();
            power.title_loc = mock_loc_ref("powers", &format!("{}.title", power.id));
            power.description_loc = mock_loc_ref("powers", &format!("{}.description", power.id));
            power.smart_description_loc =
                mock_loc_ref("powers", &format!("{}.smartDescription", power.id));
            power.remote_description_loc =
                mock_loc_ref("powers", &format!("{}.remoteDescription", power.id));
        }
        Some(proto::game_model::Model::Orb(orb)) => {
            orb.title.clear();
            orb.description.clear();
            orb.smart_description.clear();
            orb.title_loc = mock_loc_ref("orbs", &format!("{}.title", orb.id));
            orb.description_loc = mock_loc_ref("orbs", &format!("{}.description", orb.id));
            orb.smart_description_loc =
                mock_loc_ref("orbs", &format!("{}.smartDescription", orb.id));
        }
        Some(proto::game_model::Model::Affliction(affliction)) => {
            affliction.title.clear();
            affliction.description.clear();
            affliction.extra_card_text.clear();
            affliction.title_loc = mock_loc_ref("afflictions", &format!("{}.title", affliction.id));
            affliction.description_loc =
                mock_loc_ref("afflictions", &format!("{}.description", affliction.id));
            affliction.extra_card_text_loc =
                mock_loc_ref("afflictions", &format!("{}.extraCardText", affliction.id));
        }
        Some(proto::game_model::Model::Enchantment(enchantment)) => {
            enchantment.title.clear();
            enchantment.description.clear();
            enchantment.extra_card_text.clear();
            enchantment.title_loc =
                mock_loc_ref("enchantments", &format!("{}.title", enchantment.id));
            enchantment.description_loc =
                mock_loc_ref("enchantments", &format!("{}.description", enchantment.id));
            enchantment.extra_card_text_loc =
                mock_loc_ref("enchantments", &format!("{}.extraCardText", enchantment.id));
        }
        Some(proto::game_model::Model::CardPool(pool)) => {
            pool.title.clear();
            pool.title_loc = mock_loc_ref("card_pools", &format!("{}.title", pool.id));
        }
        Some(proto::game_model::Model::Modifier(modifier)) => {
            modifier.title.clear();
            modifier.description.clear();
            modifier.neow_option_title.clear();
            modifier.neow_option_description.clear();
            modifier.title_loc = mock_loc_ref("modifiers", &format!("{}.title", modifier.id));
            modifier.description_loc =
                mock_loc_ref("modifiers", &format!("{}.description", modifier.id));
            modifier.neow_option_title_loc =
                mock_loc_ref("modifiers", &format!("{}.neowOptionTitle", modifier.id));
            modifier.neow_option_description_loc = mock_loc_ref(
                "modifiers",
                &format!("{}.neowOptionDescription", modifier.id),
            );
        }
        _ => {}
    }
    model
}
