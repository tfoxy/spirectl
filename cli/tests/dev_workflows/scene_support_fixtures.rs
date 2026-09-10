struct LobbyFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for LobbyFixtureLoadBridgeService {
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
        let service = StubBridgeService::new(MockScenario::Lobby);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Lobby);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Lobby);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Lobby);
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
        let service = StubBridgeService::new(MockScenario::Lobby);
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
        assert_eq!(fixture_json["name"], "basic-lobby");
        assert_eq!(
            fixture_json["screen"],
            "Screens.CharacterSelect.NCharacterSelectScreen"
        );
        let character_select = &fixture_json["characterSelect"];
        assert_eq!(character_select["kind"], "start-run");
        assert_eq!(character_select["view"]["playerId"], "p1");
        assert_eq!(character_select["lobby"]["localPlayerId"], "p1");
        assert_eq!(character_select["lobby"]["hostPlayerId"], "p1");
        assert_eq!(character_select["lobby"]["seed"], "basic-lobby");
        assert_eq!(character_select["lobby"]["players"][0]["id"], "p1");
        assert_eq!(
            character_select["lobby"]["players"][0]["characterId"],
            "IRONCLAD"
        );
        assert_eq!(character_select["lobby"]["players"][0]["slotId"], 3);
        assert_eq!(character_select["lobby"]["players"][1]["id"], "p2");
        assert_eq!(character_select["lobby"]["players"][1]["isReady"], true);
        assert_eq!(
            character_select["characterButtons"][0]["characterId"],
            "DEFECT"
        );
        assert_eq!(character_select["characterButtons"][0]["isLocked"], true);
        assert!(fixture_json["run"].is_null());

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
                            id: "Screens.CharacterSelect.NCharacterSelectScreen".to_string(),
                            title: "Character Select".to_string(),
                            screen_instance_id: "screen:lobby:start-run".to_string(),
                            source: String::new(),
                            raw_type: String::new(),
                            class_name: String::new(),
                        }),
                        resolved_perspective: Some(sts2::bridge::proto::PerspectiveInfo {
                            scope: sts2::bridge::proto::PerspectiveScope::Local as i32,
                            player_id: "p1".to_string(),
                            uses_default: false,
                            ..Default::default()
                        }),
                        notices: vec![sts2::bridge::proto::StateNotice {
                            code: "fixture.loaded".to_string(),
                            message: "Fixture normalized for lobby CLI test.".to_string(),
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

struct MainMenuFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for MainMenuFixtureLoadBridgeService {
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
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
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
        let service = StubBridgeService::new(MockScenario::MainMenu);
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
        assert_eq!(fixture_json["name"], "basic-main-menu");
        assert_eq!(fixture_json["screen"], "main-menu");
        assert_eq!(fixture_json["rootScene"], "main-menu");
        assert_eq!(fixture_json["run"]["view"]["playerId"], "p:1");
        assert_eq!(fixture_json["run"]["players"][0]["id"], "p:1");
        assert_eq!(fixture_json["run"]["players"][0]["characterId"], "IRONCLAD");
        assert!(fixture_json["run"]["currentRoom"].is_null());
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
                            id: "main-menu".to_string(),
                            title: "Main Menu".to_string(),
                            screen_instance_id: "screen:main-menu:live".to_string(),
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
                            message: "Fixture normalized for main-menu CLI test.".to_string(),
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

struct MapFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for MapFixtureLoadBridgeService {
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
        let service = StubBridgeService::new(MockScenario::Map);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Map);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Map);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Map);
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
        let service = StubBridgeService::new(MockScenario::Map);
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
        assert_eq!(fixture_json["name"], "basic-map");
        assert_eq!(fixture_json["screen"], "Screens.Map.NMapScreen");
        assert_eq!(fixture_json["run"]["view"]["playerId"], "p:1");
        assert_eq!(fixture_json["run"]["actFloor"], 3);
        assert_eq!(fixture_json["run"]["players"][0]["id"], "p:1");
        assert!(fixture_json["run"]["currentRoom"]["mapRoom"].is_object());
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
                            id: "Screens.Map.NMapScreen".to_string(),
                            title: "Map".to_string(),
                            screen_instance_id: "screen:map:live".to_string(),
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
                            message: "Fixture normalized for map CLI test.".to_string(),
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

struct RewardsFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for RewardsFixtureLoadBridgeService {
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
        let service = StubBridgeService::new(MockScenario::Rewards);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Rewards);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Rewards);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Rewards);
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
        let service = StubBridgeService::new(MockScenario::Rewards);
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
        assert_eq!(fixture_json["name"], "basic-rewards");
        assert_eq!(fixture_json["screen"], "Screens.NRewardsScreen");
        assert_eq!(fixture_json["run"]["view"]["playerId"], "p1");
        assert_eq!(fixture_json["run"]["actFloor"], 3);
        assert!(
            fixture_json["run"]["players"][0]["overlays"][0]["rewards"].is_object()
        );
        assert!(fixture_json["run"]["currentRoom"].is_null());
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
                            id: "Screens.NRewardsScreen".to_string(),
                            title: "Rewards".to_string(),
                            screen_instance_id: "screen:rewards:live".to_string(),
                            source: String::new(),
                            raw_type: String::new(),
                            class_name: String::new(),
                        }),
                        resolved_perspective: Some(sts2::bridge::proto::PerspectiveInfo {
                            scope: sts2::bridge::proto::PerspectiveScope::Local as i32,
                            player_id: "p1".to_string(),
                            uses_default: false,
                            ..Default::default()
                        }),
                        notices: vec![sts2::bridge::proto::StateNotice {
                            code: "fixture.loaded".to_string(),
                            message: "Fixture normalized for rewards CLI test.".to_string(),
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

