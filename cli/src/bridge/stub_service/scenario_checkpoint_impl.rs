impl StubBridgeService {
    pub fn capture_scenario(
        &self,
        request: proto::ScenarioCaptureRequest,
    ) -> proto::ScenarioCaptureResult {
        let scenario = self.active_scenario();
        let screen = match scenario {
            MockScenario::Combat => ("combat", "Combat"),
            MockScenario::Shop => (SHOP_SCREEN_ID, "Shop"),
            MockScenario::Lobby | MockScenario::LobbyReady => {
                (START_RUN_LOBBY_SCREEN_ID, "Character Select")
            }
            _ => {
                return proto::ScenarioCaptureResult {
                    result: Some(proto::scenario_capture_result::Result::Error(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unsupported_source_screen: mock scenario capture supports combat and shop only.",
                        &[],
                    ))),
                };
            }
        };
        let scenario_name = if screen.0 == SHOP_SCREEN_ID {
            "shop-anchor"
        } else {
            "combat-a"
        };
        let exact_bundle_payload = if request.include_exact {
            Some(proto::ExactBundlePayload {
                path: format!("{scenario_name}.exact-bundle.json"),
                format_version: "spirectl.scenario.bundle/v0".to_string(),
                content_type: "application/json".to_string(),
                data: mock_scenario_bundle_json(scenario_name, screen.0).into_bytes(),
            })
        } else {
            None
        };

        proto::ScenarioCaptureResult {
            result: Some(proto::scenario_capture_result::Result::Success(
                proto::ScenarioCaptureResponse {
                    request_id: request.request_id,
                    scenario: Some(proto::ScenarioDocument {
                        schema_version: "spirectl.scenario/v0".to_string(),
                        name: scenario_name.to_string(),
                        description: format!("Mock {title} sparse scenario.", title = screen.1),
                        created_at: "2026-04-24T00:00:00Z".to_string(),
                        source: Some(proto::ScenarioSourceMetadata {
                            game_version: "mock-game/0".to_string(),
                            bridge_version: "spirectl-bridge/0.0.0".to_string(),
                            spirectl_version: env!("CARGO_PKG_VERSION").to_string(),
                            screen: Some(proto::ScreenInfo {
                                id: screen.0.to_string(),
                                title: screen.1.to_string(),
                                screen_instance_id: format!("screen:{}:1", screen.0),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            perspective: Some(proto::PerspectiveInfo {
                                scope: proto::PerspectiveScope::Local as i32,
                                player_id: if mock_lobby_screen_id_supported(screen.0) {
                                    "p:100".to_string()
                                } else {
                                    "p1".to_string()
                                },
                                uses_default: false,
                                ..Default::default()
                            }),
                        }),
                        restore: Some(proto::RestoreMetadata {
                            mode: proto::RestoreMode::Sparse as i32,
                            quality: proto::RestoreQuality::Partial as i32,
                            exact_bundle: None,
                            compatibility_notes: vec![proto::RestoreCompatibilityNote {
                                code: "runtime-queues-omitted".to_string(),
                                message: "Hidden runtime queues were not captured.".to_string(),
                                field: String::new(),
                            }],
                            field_reports: mock_restore_field_reports(screen.0),
                        }),
                        run: Some(proto::ScenarioRunState {
                            seed: "MIL2-CONTRACT-SEED".to_string(),
                            act: 1,
                            floor: if screen.0 == SHOP_SCREEN_ID { 4 } else { 3 },
                            ascension: 0,
                            players: vec![proto::ScenarioPlayerState {
                                id: "p1".to_string(),
                                character: "ironclad".to_string(),
                                is_local: true,
                                is_host: true,
                                is_remote: false,
                            }],
                        }),
                        screen_state_json: mock_scenario_screen_state_json(screen.0),
                        notices: vec![proto::StateNotice {
                            code: "sparse-export".to_string(),
                            message: "Static model defaults are referenced by stable id and not repeated.".to_string(),
                            provisional: false,
                            path: String::new(),
                            severity: String::new(),
                            source: String::new(),
                        stability: String::new(),
                        perspective: String::new(),
                        }],
                        multiplayer: if mock_lobby_screen_id_supported(screen.0) {
                            Some(mock_multiplayer_metadata(
                                scenario == MockScenario::LobbyReady,
                            ))
                        } else {
                            None
                        },
                    }),
                    source: proto::DataSource::Stub as i32,
                    provisional: true,
                    exact_bundle_payload,
                },
            )),
        }
    }

    pub fn restore_scenario(
        &self,
        request: proto::ScenarioRestoreRequest,
    ) -> proto::ScenarioRestoreResult {
        let Some(scenario) = request.scenario else {
            return proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidFixture,
                    "Scenario restore requires a scenario document.",
                    &[],
                ))),
            };
        };
        if scenario.schema_version != "spirectl.scenario/v0" {
            return proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidFixture,
                    "invalid_scenario_schema_version: scenario schema version is not supported.",
                    &[],
                ))),
            };
        }
        if let Some(multiplayer) = scenario.multiplayer.as_ref()
            && multiplayer.is_multiplayer
            && multiplayer.requires_remote_clients
        {
            if !request.allow_degraded_local_multiplayer {
                return proto::ScenarioRestoreResult {
                    result: Some(proto::scenario_restore_result::Result::Error(
                        multiplayer_degradation_required_error(multiplayer),
                    )),
                };
            }
            return proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Success(
                    mock_restore_response(
                        request.request_id,
                        "combat",
                        proto::RestoreQuality::Degraded,
                        Some(mock_degraded_multiplayer_restore_result(multiplayer)),
                        request.exact_bundle_payload.is_some(),
                    ),
                )),
            };
        }
        let source_screen = scenario
            .source
            .as_ref()
            .and_then(|source| source.screen.as_ref())
            .map(|screen| screen.id.as_str())
            .unwrap_or_default();
        let active_screen = match self.scenario {
            MockScenario::Combat => "combat",
            MockScenario::Shop => SHOP_SCREEN_ID,
            MockScenario::Lobby | MockScenario::LobbyReady => START_RUN_LOBBY_SCREEN_ID,
            _ => "",
        };
        if source_screen != active_screen {
            return proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "unsupported_source_screen: scenario screen does not match the active mock scenario.",
                    &[],
                ))),
            };
        }
        let Some(restored_scenario) = mock_scenario_for_screen(source_screen) else {
            return proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "unsupported_source_screen: mock scenario restore supports combat and shop only.",
                    &[],
                ))),
            };
        };
        MOCK_LOADED_FIXTURE_SCENARIO.with(|loaded| {
            *loaded.borrow_mut() = Some(restored_scenario);
        });
        let exact_bundle_used = request.exact_bundle_payload.is_some();
        proto::ScenarioRestoreResult {
            result: Some(proto::scenario_restore_result::Result::Success(
                mock_restore_response(
                    request.request_id,
                    source_screen,
                    proto::RestoreQuality::Partial,
                    scenario
                        .multiplayer
                        .as_ref()
                        .filter(|metadata| metadata.is_multiplayer)
                        .map(mock_lobby_multiplayer_restore_result),
                    exact_bundle_used,
                ),
            )),
        }
    }

    pub fn capture_checkpoint(
        &self,
        request: proto::CheckpointCaptureRequest,
    ) -> proto::CheckpointCaptureResult {
        let scenario = self.active_scenario();
        let screen = match scenario {
            MockScenario::Combat => ("combat", "Combat"),
            MockScenario::Shop => ("shop", "Shop"),
            MockScenario::Lobby | MockScenario::LobbyReady => {
                (START_RUN_LOBBY_SCREEN_ID, "Character Select")
            }
            _ => {
                return proto::CheckpointCaptureResult {
                    result: Some(proto::checkpoint_capture_result::Result::Error(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unsupported_source_screen: mock checkpoint capture supports combat and shop only.",
                        &[],
                    ))),
                };
            }
        };
        let summary_json = mock_checkpoint_summary_json(screen.0);
        let bundle_json = mock_checkpoint_bundle_json(&request.name, screen.0);

        proto::CheckpointCaptureResult {
            result: Some(proto::checkpoint_capture_result::Result::Success(
                proto::CheckpointCaptureResponse {
                    request_id: request.request_id,
                    manifest: Some(proto::CheckpointManifest {
                        schema_version: "spirectl.checkpoint/v0".to_string(),
                        name: request.name.clone(),
                        created_at: "2026-04-24T00:00:00Z".to_string(),
                        source: Some(proto::ScenarioSourceMetadata {
                            game_version: "mock-game/0".to_string(),
                            bridge_version: BRIDGE_VERSION.to_string(),
                            spirectl_version: env!("CARGO_PKG_VERSION").to_string(),
                            screen: Some(proto::ScreenInfo {
                                id: screen.0.to_string(),
                                title: screen.1.to_string(),
                                screen_instance_id: format!("screen:{}:1", screen.0),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            perspective: Some(proto::PerspectiveInfo {
                                scope: proto::PerspectiveScope::Local as i32,
                                player_id: if mock_lobby_screen_id_supported(screen.0) {
                                    "p:100".to_string()
                                } else {
                                    "p1".to_string()
                                },
                                uses_default: true,
                                ..Default::default()
                            }),
                        }),
                        summary_json,
                        exact_bundle: None,
                        restore_quality: proto::RestoreQuality::Partial as i32,
                        known_omissions: vec![proto::RestoreCompatibilityNote {
                            code: "runtime-queues-omitted".to_string(),
                            message: "Hidden runtime queues were not captured.".to_string(),
                            field: String::new(),
                        }],
                        fallback_policy: "fall-back-to-sparse-with-degraded-quality".to_string(),
                        field_reports: mock_restore_field_reports(screen.0),
                        multiplayer: if mock_lobby_screen_id_supported(screen.0) {
                            Some(mock_multiplayer_metadata(
                                scenario == MockScenario::LobbyReady,
                            ))
                        } else {
                            None
                        },
                    }),
                    source: proto::DataSource::Stub as i32,
                    provisional: false,
                    exact_bundle_payload: Some(proto::ExactBundlePayload {
                        path: "bundle.json".to_string(),
                        format_version: "spirectl.checkpoint.bundle/v0".to_string(),
                        content_type: "application/json".to_string(),
                        data: bundle_json.into_bytes(),
                    }),
                },
            )),
        }
    }

    pub fn restore_checkpoint(
        &self,
        request: proto::CheckpointRestoreRequest,
    ) -> proto::CheckpointRestoreResult {
        let Some(manifest) = request.manifest else {
            return proto::CheckpointRestoreResult {
                result: Some(proto::checkpoint_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "Checkpoint restore requires a manifest.",
                    &[],
                ))),
            };
        };
        if let Some(multiplayer) = manifest.multiplayer.as_ref()
            && multiplayer.is_multiplayer
            && multiplayer.requires_remote_clients
        {
            if !request.allow_degraded_local_multiplayer {
                return proto::CheckpointRestoreResult {
                    result: Some(proto::checkpoint_restore_result::Result::Error(
                        multiplayer_degradation_required_error(multiplayer),
                    )),
                };
            }
            return proto::CheckpointRestoreResult {
                result: Some(proto::checkpoint_restore_result::Result::Success(
                    mock_checkpoint_restore_response(
                        request.request_id,
                        "combat",
                        proto::RestoreQuality::Degraded,
                        Some(mock_degraded_multiplayer_restore_result(multiplayer)),
                        request.exact_bundle_payload.is_some(),
                    ),
                )),
            };
        }
        let active_scenario = self.active_scenario();
        let expected_screen = match active_scenario {
            MockScenario::Combat => "combat",
            MockScenario::Shop => SHOP_SCREEN_ID,
            MockScenario::Lobby | MockScenario::LobbyReady => START_RUN_LOBBY_SCREEN_ID,
            _ => {
                return proto::CheckpointRestoreResult {
                    result: Some(proto::checkpoint_restore_result::Result::Error(error(
                        proto::BridgeErrorCode::InvalidAction,
                        "unsupported_source_screen: mock checkpoint restore supports combat and shop only.",
                        &[],
                    ))),
                };
            }
        };
        let manifest_screen = manifest
            .source
            .as_ref()
            .and_then(|source| source.screen.as_ref())
            .map(|screen| screen.id.as_str())
            .unwrap_or_default();
        if manifest_screen != expected_screen {
            return proto::CheckpointRestoreResult {
                result: Some(proto::checkpoint_restore_result::Result::Error(error(
                    proto::BridgeErrorCode::InvalidAction,
                    "restore_validation_mismatch: checkpoint screen does not match the active mock scenario.",
                    &[],
                ))),
            };
        }

        proto::CheckpointRestoreResult {
            result: Some(proto::checkpoint_restore_result::Result::Success(
                mock_checkpoint_restore_response(
                    request.request_id,
                    expected_screen,
                    proto::RestoreQuality::Partial,
                    manifest
                        .multiplayer
                        .as_ref()
                        .filter(|metadata| metadata.is_multiplayer)
                        .map(mock_lobby_multiplayer_restore_result),
                    request.exact_bundle_payload.is_some(),
                ),
            )),
        }
    }

    pub fn list_checkpoints(
        &self,
        _request: proto::CheckpointListRequest,
    ) -> proto::CheckpointListResult {
        proto::CheckpointListResult {
            result: Some(proto::checkpoint_list_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "Checkpoint listing is planned for M52 and is not implemented in M51.",
                &[],
            ))),
        }
    }

    pub fn delete_checkpoint(
        &self,
        _request: proto::CheckpointDeleteRequest,
    ) -> proto::CheckpointDeleteResult {
        proto::CheckpointDeleteResult {
            result: Some(proto::checkpoint_delete_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "Checkpoint deletion is planned for M52 and is not implemented in M51.",
                &[],
            ))),
        }
    }

    pub fn get_hot_reload_status(
        &self,
        _request: proto::HotReloadStatusRequest,
    ) -> proto::HotReloadStatusResult {
        proto::HotReloadStatusResult {
            result: Some(proto::hot_reload_status_result::Result::Success(
                proto::HotReloadStatusResponse {
                    status: Some(proto::HotReloadShellStatus {
                        supported: false,
                        protocol: Some(proto::HotReloadProtocolInfo {
                            id: "spirectl.m57.hot-reload-shell".to_string(),
                            version: 0,
                        }),
                        shell_mod_id: String::new(),
                        shell_protocol_version: 0,
                        active_generation: 0,
                        expected_logic_artifact_path: String::new(),
                        contract_version: 0,
                        reload_in_progress: false,
                        last_reload_report: None,
                        restart_required: false,
                        notices: vec![proto::HotReloadNotice {
                            code: "hot-reload-shell-unsupported".to_string(),
                            message: "The mock bridge does not host a hot-reload shell."
                                .to_string(),
                        }],
                    }),
                    notices: vec![proto::HotReloadNotice {
                        code: "hot-reload-shell-unsupported".to_string(),
                        message: "The mock bridge does not host a hot-reload shell.".to_string(),
                    }],
                },
            )),
        }
    }

    pub fn request_hot_reload(&self, _request: proto::HotReloadRequest) -> proto::HotReloadResult {
        proto::HotReloadResult {
            result: Some(proto::hot_reload_result::Result::Error(error(
                proto::BridgeErrorCode::NotImplemented,
                "Hot-reload control requires a live M57-compatible shell.",
                &[],
            ))),
        }
    }
}
