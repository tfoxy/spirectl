struct RestSiteFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for RestSiteFixtureLoadBridgeService {
    impl_state_watch_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_runtime_scene_set_visible_unimplemented!();

    async fn handshake(
        &self,
        request: tonic::Request<HandshakeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HandshakeResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.handshake(request.into_inner()),
        ))
    }

    async fn get_state(
        &self,
        request: tonic::Request<StateRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::StateResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::RestSite);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::RestSite);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::RestSite);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::RestSite);
        Ok(tonic::Response::new(
            service.get_screenshot(request.into_inner()),
        ))
    }

    async fn inspect_presentation_resource_scenes(
        &self,
        _request: tonic::Request<sts2::bridge::proto::PresentationResourceSceneRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::PresentationResourceSceneResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "presentation resource scene inspection is not implemented for this test bridge",
        ))
    }

    async fn get_runtime_scene_tree(
        &self,
        _request: tonic::Request<RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<RuntimeSceneNodeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneNodeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_asset_catalog(
        &self,
        _request: tonic::Request<sts2::bridge::proto::AssetCatalogRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetCatalogResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::AssetCatalogResult {
                result: Some(sts2::bridge::proto::asset_catalog_result::Result::Error(
                    sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::NotImplemented as i32,
                        message: "asset catalog is not implemented by this test bridge".to_string(),
                        details: Vec::new(),
                        action_failure: None,
                    },
                )),
            },
        ))
    }

    async fn extract_asset(
        &self,
        request: tonic::Request<sts2::bridge::proto::AssetExtractRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetExtractResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::RestSite);
        Ok(tonic::Response::new(
            service.extract_asset(request.into_inner()),
        ))
    }

    async fn get_debug_status(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStatusResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn pause_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugPauseRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugPauseResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn resume_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugResumeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugResumeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn step_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugStepRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStepResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn list_breakpoints(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointListRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointListResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn add_breakpoint(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointAddRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointAddResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn remove_breakpoint(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointRemoveRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointRemoveResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn load_fixture(
        &self,
        request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        let request = request.into_inner();
        let fixture_json: Value =
            serde_json::from_str(&request.fixture_json).expect("fixture json should parse");

        assert_eq!(fixture_json["schemaVersion"], "spirectl.fixture/v0");
        assert_eq!(fixture_json["name"], "basic-rest-site");
        assert_eq!(fixture_json["screen"], "Rooms.NRestSiteRoom");
        assert_eq!(fixture_json["run"]["view"]["playerId"], "p:1");
        assert_eq!(fixture_json["run"]["actFloor"], 3);
        assert_eq!(fixture_json["run"]["players"][0]["id"], "p:1");
        assert!(fixture_json["run"]["currentRoom"]["restSite"].is_object());
        assert!(fixture_json["run"]["currentRoom"]["combat"].is_null());
        assert!(fixture_json["characterSelect"].is_null());

        Ok(tonic::Response::new(
            sts2::bridge::proto::FixtureLoadResult {
                result: Some(sts2::bridge::proto::fixture_load_result::Result::Success(
                    sts2::bridge::proto::FixtureLoadResponse {
                        request_id: request.request_id,
                        fixture_name: request.fixture_name,
                        source_path: request.source_path,
                        source: sts2::bridge::proto::DataSource::Live as i32,
                        provisional: false,
                        screen: Some(sts2::bridge::proto::ScreenInfo {
                            id: "Rooms.NRestSiteRoom".to_string(),
                            title: "Rest Site".to_string(),
                            screen_instance_id: "screen:rest-site:live".to_string(),
                            source: String::new(),
                            raw_type: String::new(),
                            class_name: String::new(),
                        }),
                        resolved_perspective: Some(sts2::bridge::proto::PerspectiveInfo {
                            scope: sts2::bridge::proto::PerspectiveScope::Local as i32,
                            player_id: "p:1".to_string(),
                            uses_default: false,
                            ..Default::default()
                        }),
                        notices: vec![sts2::bridge::proto::StateNotice {
                            code: "fixture.loaded".to_string(),
                            message: "Fixture normalized for rest-site CLI test.".to_string(),
                            provisional: false,
                            ..Default::default()
                        }],
                        ..Default::default()
                    },
                )),
            },
        ))
    }
}

struct RuntimeSceneBridgeService;

#[tonic::async_trait]
impl BridgeService for RuntimeSceneBridgeService {
    impl_state_watch_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_unimplemented_bridge_rpc!(
        get_runtime_transition_status,
        sts2::bridge::proto::RuntimeTransitionStatusRequest,
        sts2::bridge::proto::RuntimeTransitionStatusResult,
        "runtime transition status is not exercised by this test service"
    );

    async fn handshake(
        &self,
        request: tonic::Request<HandshakeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HandshakeResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(
            service.handshake(request.into_inner()),
        ))
    }

    async fn get_state(
        &self,
        request: tonic::Request<StateRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::StateResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn inspect_presentation_resource_scenes(
        &self,
        _request: tonic::Request<sts2::bridge::proto::PresentationResourceSceneRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::PresentationResourceSceneResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "presentation resource scene inspection is not implemented for this test bridge",
        ))
    }

    async fn get_runtime_scene_tree(
        &self,
        request: tonic::Request<RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        let request = request.into_inner();
        let root = if request.node_path.is_empty() {
            "/root".to_string()
        } else {
            request.node_path
        };

        Ok(tonic::Response::new(
            sts2::bridge::proto::RuntimeSceneTreeResult {
                result: Some(
                    sts2::bridge::proto::runtime_scene_tree_result::Result::Success(
                        sts2::bridge::proto::RuntimeSceneTreeResponse {
                            source: sts2::bridge::proto::DataSource::Live as i32,
                            provisional: false,
                            screen: Some(sts2::bridge::proto::ScreenInfo {
                                id: "combat".to_string(),
                                title: "Combat".to_string(),
                                screen_instance_id: "screen:combat:live".to_string(),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            root_node_path: root,
                            nodes: vec![
                                runtime_scene_node(
                                    "/root/CombatScreen",
                                    "CombatScreen",
                                    "/root",
                                    Some("res://ui/CombatScreen.tscn"),
                                    false,
                                    false,
                                    true,
                                ),
                                runtime_scene_node(
                                    "/root/CombatScreen/HandPanel",
                                    "HandPanel",
                                    "/root/CombatScreen",
                                    None,
                                    false,
                                    false,
                                    true,
                                ),
                            ],
                            notes: vec![],
                        },
                    ),
                ),
            },
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        request: tonic::Request<RuntimeSceneNodeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneNodeResult>, tonic::Status> {
        let request = request.into_inner();
        let visible = !request.node_path.contains("StableHidden");
        Ok(tonic::Response::new(
            sts2::bridge::proto::RuntimeSceneNodeResult {
                result: Some(
                    sts2::bridge::proto::runtime_scene_node_result::Result::Success(
                        sts2::bridge::proto::RuntimeSceneNodeResponse {
                            source: sts2::bridge::proto::DataSource::Live as i32,
                            provisional: false,
                            screen: Some(sts2::bridge::proto::ScreenInfo {
                                id: "combat".to_string(),
                                title: "Combat".to_string(),
                                screen_instance_id: "screen:combat:live".to_string(),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            node: Some(runtime_scene_node(
                                "/root/CombatScreen",
                                "CombatScreen",
                                "/root",
                                Some("res://ui/CombatScreen.tscn"),
                                request.include_properties,
                                request.include_computed_transform,
                                visible,
                            )),
                            children: vec![runtime_scene_node(
                                "/root/CombatScreen/HandPanel",
                                "HandPanel",
                                "/root/CombatScreen",
                                None,
                                request.include_properties,
                                request.include_computed_transform,
                                visible,
                            )],
                            notes: vec![
                                "Runtime node ids are stable only for the current screen instance."
                                    .to_string(),
                            ],
                        },
                    ),
                ),
            },
        ))
    }

    async fn set_runtime_scene_node_visible(
        &self,
        request: tonic::Request<RuntimeSceneSetVisibleRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneSetVisibleResult>, tonic::Status>
    {
        let request = request.into_inner();
        Ok(tonic::Response::new(
            sts2::bridge::proto::RuntimeSceneSetVisibleResult {
                result: Some(
                    sts2::bridge::proto::runtime_scene_set_visible_result::Result::Success(
                        sts2::bridge::proto::RuntimeSceneSetVisibleResponse {
                            source: sts2::bridge::proto::DataSource::Live as i32,
                            provisional: false,
                            screen: Some(sts2::bridge::proto::ScreenInfo {
                                id: "combat".to_string(),
                                title: "Combat".to_string(),
                                screen_instance_id: "screen:combat:live".to_string(),
                                source: String::new(),
                                raw_type: String::new(),
                                class_name: String::new(),
                            }),
                            node: Some(runtime_scene_node(
                                &request.node_path,
                                "CombatScreen",
                                "/root",
                                Some("res://ui/CombatScreen.tscn"),
                                true,
                                request.include_computed_transform,
                                request.visible,
                            )),
                            previous_visible: true,
                            requested_visible: request.visible,
                            changed: true,
                            notes: vec![],
                        },
                    ),
                ),
            },
        ))
    }

    async fn hover_runtime_scene_control(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneControlHoverRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneControlHoverResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented("not exercised"))
    }

    async fn unhover_runtime_scene_control(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneControlUnhoverRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneControlUnhoverResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented("not exercised"))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(
            service.get_screenshot(request.into_inner()),
        ))
    }

    async fn get_asset_catalog(
        &self,
        _request: tonic::Request<sts2::bridge::proto::AssetCatalogRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetCatalogResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::AssetCatalogResult {
                result: Some(sts2::bridge::proto::asset_catalog_result::Result::Error(
                    sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::NotImplemented as i32,
                        message: "asset catalog is not implemented by this test bridge".to_string(),
                        details: Vec::new(),
                        action_failure: None,
                    },
                )),
            },
        ))
    }

    async fn extract_asset(
        &self,
        request: tonic::Request<sts2::bridge::proto::AssetExtractRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetExtractResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
        Ok(tonic::Response::new(
            service.extract_asset(request.into_inner()),
        ))
    }

    async fn get_debug_status(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStatusResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn pause_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugPauseRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugPauseResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn resume_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugResumeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugResumeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn step_debug(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugStepRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStepResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn list_breakpoints(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointListRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointListResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn add_breakpoint(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointAddRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointAddResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn remove_breakpoint(
        &self,
        _request: tonic::Request<sts2::bridge::proto::DebugBreakpointRemoveRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointRemoveResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented(
            "debug control is not implemented in this test service",
        ))
    }

    async fn load_fixture(
        &self,
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "fixture loading is not implemented in this test service",
        ))
    }
}

fn runtime_scene_node(
    path: &str,
    name: &str,
    parent_path: &str,
    scene_file_path: Option<&str>,
    include_properties: bool,
    include_computed_transform: bool,
    visible: bool,
) -> sts2::bridge::proto::RuntimeSceneNodeInfo {
    sts2::bridge::proto::RuntimeSceneNodeInfo {
        node_id: format!("node:screen:combat:live:{path}"),
        node_path: path.to_string(),
        name: name.to_string(),
        node_type: format!("MegaCrit.Sts2.{name}"),
        parent_node_path: parent_path.to_string(),
        owner_path: "/root/CombatScreen".to_string(),
        scene_file_path: scene_file_path.unwrap_or_default().to_string(),
        attached_script_path: String::new(),
        attached_script_type: String::new(),
        child_count: if name == "CombatScreen" { 1 } else { 0 },
        notes: vec![],
        properties: include_properties.then(|| runtime_scene_properties(visible)),
        computed_transform: include_computed_transform.then(runtime_scene_computed_transform),
        native_node_type: String::new(),
    }
}

fn runtime_scene_properties(visible: bool) -> sts2::bridge::proto::RuntimeSceneNodeProperties {
    sts2::bridge::proto::RuntimeSceneNodeProperties {
        visible: Some(visible),
        effective_visible: Some(visible),
        position: Some(runtime_scene_vec2(10.0, 20.0)),
        global_position: Some(runtime_scene_vec2(10.0, 20.0)),
        scale: Some(runtime_scene_vec2(1.0, 1.0)),
        rotation_radians: Some(0.0),
        size: Some(runtime_scene_vec2(640.0, 360.0)),
        pivot_offset: Some(runtime_scene_vec2(0.0, 0.0)),
        anchors: Some(sts2::bridge::proto::RuntimeSceneAnchors {
            left: Some(0.0),
            top: Some(0.0),
            right: Some(1.0),
            bottom: Some(1.0),
        }),
        offsets: Some(sts2::bridge::proto::RuntimeSceneOffsets {
            left: Some(0.0),
            top: Some(0.0),
            right: Some(0.0),
            bottom: Some(0.0),
        }),
        z_index: Some(0),
        modulate: Some(runtime_scene_color()),
        self_modulate: Some(runtime_scene_color()),
        effective_modulate: Some(runtime_scene_color()),
        clip_contents: Some(true),
        texture_rect: Some(sts2::bridge::proto::RuntimeSceneTextureRectProperties {
            stretch_mode: "keep-aspect-centered".to_string(),
            expand_mode: "ignore-size".to_string(),
            flip_h: false,
            flip_v: true,
        }),
        material: Some(sts2::bridge::proto::RuntimeSceneMaterialProperties {
            material: Some(sts2::bridge::proto::RuntimeSceneResourceRef {
                field: "Material".to_string(),
                resource_path: "res://materials/lobby/dim.tres".to_string(),
                resource_type: "Godot.ShaderMaterial".to_string(),
                resource_name: "dim".to_string(),
            }),
            use_parent_material: Some(false),
            shader: Some(sts2::bridge::proto::RuntimeSceneResourceRef {
                field: "Shader".to_string(),
                resource_path: "res://shaders/ui_dim.gdshader".to_string(),
                resource_type: "Godot.Shader".to_string(),
                resource_name: "ui_dim".to_string(),
            }),
            shader_parameters: vec![sts2::bridge::proto::RuntimeSceneShaderParameter {
                name: "darken".to_string(),
                value_kind: "number".to_string(),
                string_value: String::new(),
                number_value: Some(0.5),
                bool_value: None,
                color_value: None,
                vector2_value: None,
                resource_value: None,
            }],
            blend_mode: "mix".to_string(),
        }),
        layout: Some(sts2::bridge::proto::RuntimeSceneLayoutProperties {
            minimum_size: Some(runtime_scene_vec2(100.0, 40.0)),
            combined_minimum_size: Some(runtime_scene_vec2(120.0, 48.0)),
            custom_minimum_size: Some(runtime_scene_vec2(80.0, 32.0)),
            size_flags_horizontal: Some(3),
            size_flags_vertical: Some(1),
            size_flags_stretch_ratio: Some(1.0),
            layout_direction: "inherit".to_string(),
            theme_type_variation: "Panel".to_string(),
            container_alignment: Some(1),
            flow_vertical: Some(true),
            theme_constants: vec![sts2::bridge::proto::RuntimeSceneThemeConstant {
                name: "separation".to_string(),
                value: Some(8.0),
            }],
        }),
        textures: vec![sts2::bridge::proto::RuntimeSceneResourceRef {
            field: "Texture".to_string(),
            resource_path: "res://combat/backgrounds/overgrowth/layer.png".to_string(),
            resource_type: "Godot.Texture2D".to_string(),
            resource_name: "layer".to_string(),
        }],
        text: Some(sts2::bridge::proto::RuntimeSceneTextProperties {
            text: Some("A spire label".to_string()),
            raw_text: Some("A {0} label".to_string()),
            rich_text_enabled: Some(true),
            source: "mega-rich-text-label".to_string(),
            diagnostic_surface: "dev.runtime_scene.text".to_string(),
            notices: vec![sts2::bridge::proto::RuntimeScenePropertyNotice {
                code: "dev_scene_text".to_string(),
                field: "properties.text".to_string(),
                message: "Text diagnostics are developer runtime scene internals.".to_string(),
            }],
            font: Some(sts2::bridge::proto::RuntimeSceneResourceRef {
                field: "Theme.Font".to_string(),
                resource_path: "res://ui/fonts/spire_label.tres".to_string(),
                resource_type: "Godot.Font".to_string(),
                resource_name: "SpireLabel".to_string(),
            }),
            font_size: Some(24.0),
            font_size_source: "mega-text._lastSetSize".to_string(),
            applied_font_size: Some(24.0),
            theme_font_size: Some(28.0),
            configured_min_font_size: Some(8.0),
            configured_max_font_size: Some(100.0),
            line_height: Some(28.0),
            letter_spacing: None,
            font_weight: Some("700".to_string()),
            font_style: Some("italic".to_string()),
            text_color: Some(runtime_scene_color()),
            outline_color: Some(runtime_scene_color()),
            outline_size: Some(2.0),
            shadow: Some(sts2::bridge::proto::RuntimeSceneTextShadow {
                color: Some(sts2::bridge::proto::RuntimeSceneColor {
                    r: 0.0,
                    g: 0.0,
                    b: 0.0,
                    a: 0.5,
                    html: "#00000080".to_string(),
                }),
                offset: Some(runtime_scene_vec2(2.0, 3.0)),
                size: Some(4.0),
                outline_size: Some(1.0),
                source: "theme:font_shadow_color".to_string(),
                stacked_shadows: vec![sts2::bridge::proto::RuntimeSceneStackedTextShadow {
                    index: 0,
                    color: Some(sts2::bridge::proto::RuntimeSceneColor {
                        r: 0.0,
                        g: 0.0,
                        b: 0.0,
                        a: 0.25,
                        html: "#00000040".to_string(),
                    }),
                    offset: Some(runtime_scene_vec2(1.0, 1.0)),
                    outline_size: Some(0.5),
                    source: "label-settings".to_string(),
                }],
            }),
            rich_text_spans: vec![sts2::bridge::proto::RuntimeSceneRichTextSpan {
                tag: "color".to_string(),
                text: "spire".to_string(),
                color: Some(runtime_scene_color()),
            }],
            layout: None,
            recipe: Some(sts2::bridge::proto::RuntimeSceneTextRecipe {
                source: "mega-rich-text-label".to_string(),
                auto_size_enabled: Some(true),
                min_font_size_px: Some(8.0),
                max_font_size_px: Some(100.0),
                nominal_font_size_px: Some(24.0),
                rich_text_enabled: Some(true),
                wrap_mode: None,
                break_flags: Some("mandatory,word-bound".to_string()),
                justification_flags: Some("kashida,word-bound".to_string()),
                text_overrun_behavior: Some("trim-char".to_string()),
                horizontally_bound: Some(false),
                vertically_bound: Some(true),
            }),
            rendered_metrics: Some(sts2::bridge::proto::RuntimeSceneTextRenderedMetrics {
                metric_source: "godot-font,godot-text-paragraph".to_string(),
                font_ascent_px: Some(20.5),
                font_descent_px: Some(5.5),
                font_height_px: Some(28.0),
                paragraph_size_width_px: Some(180.0),
                paragraph_size_height_px: Some(56.0),
                lines: vec![
                    sts2::bridge::proto::RuntimeSceneTextRenderedLineMetrics {
                        index: 0,
                        paragraph_ascent_px: Some(20.5),
                        paragraph_descent_px: Some(5.5),
                        paragraph_line_size_width_px: Some(180.0),
                        paragraph_line_size_height_px: Some(28.0),
                        paragraph_line_width_px: Some(160.0),
                    },
                    sts2::bridge::proto::RuntimeSceneTextRenderedLineMetrics {
                        index: 1,
                        paragraph_ascent_px: Some(20.5),
                        paragraph_descent_px: Some(5.5),
                        paragraph_line_size_width_px: Some(160.0),
                        paragraph_line_size_height_px: Some(28.0),
                        paragraph_line_width_px: Some(140.0),
                    },
                ],
            }),
        }),
        nine_patch: Some(sts2::bridge::proto::RuntimeSceneNinePatchProperties {
            texture: Some(sts2::bridge::proto::RuntimeSceneResourceRef {
                field: "Texture".to_string(),
                resource_path: "res://combat/backgrounds/overgrowth/layer.png".to_string(),
                resource_type: "Godot.Texture2D".to_string(),
                resource_name: "layer".to_string(),
            }),
            draw_center: true,
            patch_margins: Some(sts2::bridge::proto::RuntimeScenePatchMargins {
                left: 12.0,
                top: 13.0,
                right: 14.0,
                bottom: 15.0,
            }),
            axis_stretch_horizontal: "tile-fit".to_string(),
            axis_stretch_vertical: "stretch".to_string(),
            effective_modulate: Some(sts2::bridge::proto::RuntimeSceneColor {
                r: 0.0,
                g: 0.0,
                b: 0.0,
                a: 0.36,
                html: "#0000005c".to_string(),
            }),
        }),
        notices: vec![],
        show_behind_parent: None,
        z_as_relative: None,
        clip_children_mode: None,
        mouse_filter: Some(2),
        focus_mode: Some(1),
        mouse_default_cursor_shape: Some(3),
    }
}

fn runtime_scene_computed_transform() -> sts2::bridge::proto::RuntimeSceneComputedTransform {
    let transform = sts2::bridge::proto::RuntimeSceneTransform2D {
        x_axis: Some(runtime_scene_vec2(1.0, 0.0)),
        y_axis: Some(runtime_scene_vec2(0.0, 1.0)),
        origin: Some(runtime_scene_vec2(10.0, 20.0)),
    };
    sts2::bridge::proto::RuntimeSceneComputedTransform {
        local_transform: Some(transform),
        global_transform: Some(transform),
        global_rect: Some(sts2::bridge::proto::RuntimeSceneRect2 {
            position: Some(runtime_scene_vec2(10.0, 20.0)),
            size: Some(runtime_scene_vec2(640.0, 360.0)),
        }),
        viewport_clipped_rect: Some(sts2::bridge::proto::RuntimeSceneRect2 {
            position: Some(runtime_scene_vec2(10.0, 20.0)),
            size: Some(runtime_scene_vec2(640.0, 360.0)),
        }),
        notices: vec![sts2::bridge::proto::RuntimeScenePropertyNotice {
            code: "not_applicable".to_string(),
            field: "computedTransform.extraBounds".to_string(),
            message: "extra bounds are not available for this fixture node.".to_string(),
        }],
    }
}

fn runtime_scene_vec2(x: f64, y: f64) -> sts2::bridge::proto::RuntimeSceneVector2 {
    sts2::bridge::proto::RuntimeSceneVector2 { x, y }
}

fn runtime_scene_color() -> sts2::bridge::proto::RuntimeSceneColor {
    sts2::bridge::proto::RuntimeSceneColor {
        r: 1.0,
        g: 1.0,
        b: 1.0,
        a: 1.0,
        html: "#ffffffff".to_string(),
    }
}
