#[cfg(unix)]
#[tokio::test]
async fn test_run_reports_partial_failure_artifact_collection_errors() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("runner-artifact-failure.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind listener");
    let service = LogsFailingBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        artifacts: sts2::ArtifactsConfig {
            dir: artifacts_dir.path().to_string_lossy().into_owned(),
        },
        ..AppConfig::default()
    });
    let scenario = write_scenario(
        r#"
name: artifact-errors
steps:
  - dev.assert:
      path: screen.id
      equals: combat
"#,
    );

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            scenario.path().to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join runner");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let scenario_artifacts = &payload["scenarios"][0]["artifacts"];
    let scenario_dir = PathBuf::from(
        scenario_artifacts["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let errors = scenario_artifacts["errors"]
        .as_array()
        .expect("artifact errors");

    assert_eq!(response.exit_code, 3);
    assert_eq!(
        payload["scenarios"][0]["failure"]["error"]["code"],
        "assertion_failed"
    );
    assert!(errors.iter().any(|error| {
        error["artifact"] == "logs" && error["error"]["code"] == "runtime_failure"
    }));
    assert!(scenario_dir.join("failure/evidence/state.json").exists());
    assert!(
        scenario_dir
            .join("failure/evidence/inspect-actions.json")
            .exists()
    );
    assert!(!scenario_dir.join("failure/evidence/logs.json").exists());
    server.abort();
}

#[derive(Default)]
struct TransitioningBridgeService {
    state_calls: AtomicUsize,
}

#[tonic::async_trait]
impl BridgeService for TransitioningBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_hot_reload_unimplemented_rpc_stubs!();

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
        let scenario = if self.state_calls.fetch_add(1, Ordering::SeqCst) == 0 {
            MockScenario::MainMenu
        } else {
            MockScenario::Combat
        };
        let service = StubBridgeService::new(scenario);
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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.load_fixture(request.into_inner()),
        ))
    }
}

struct StaticBridgeService {
    scenario: MockScenario,
    hang_state: bool,
    hang_actions: bool,
}

impl StaticBridgeService {
    fn new(scenario: MockScenario) -> Self {
        Self {
            scenario,
            hang_state: false,
            hang_actions: false,
        }
    }

    fn with_hanging_state(mut self) -> Self {
        self.hang_state = true;
        self
    }

    fn with_hanging_actions(mut self) -> Self {
        self.hang_actions = true;
        self
    }
}

#[tonic::async_trait]
impl BridgeService for StaticBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_hot_reload_unimplemented_rpc_stubs!();

    async fn handshake(
        &self,
        request: tonic::Request<HandshakeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HandshakeResult>, tonic::Status> {
        let service = StubBridgeService::new(self.scenario);
        Ok(tonic::Response::new(
            service.handshake(request.into_inner()),
        ))
    }

    async fn get_state(
        &self,
        request: tonic::Request<StateRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::StateResult>, tonic::Status> {
        if self.hang_state {
            tokio::time::sleep(std::time::Duration::from_secs(10)).await;
        }
        let service = StubBridgeService::new(self.scenario);
        Ok(tonic::Response::new(
            service.get_state(request.into_inner()),
        ))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        if self.hang_actions {
            tokio::time::sleep(std::time::Duration::from_secs(10)).await;
        }
        let service = StubBridgeService::new(self.scenario);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        let service = StubBridgeService::new(self.scenario);
        Ok(tonic::Response::new(service.get_logs(request.into_inner())))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(self.scenario);
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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        let service = StubBridgeService::new(self.scenario);
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
        let service = StubBridgeService::new(self.scenario);
        Ok(tonic::Response::new(
            service.load_fixture(request.into_inner()),
        ))
    }
}

#[cfg(unix)]
struct HotReloadBridgeService {
    supported: bool,
}

#[cfg(unix)]
#[tonic::async_trait]
impl BridgeService for HotReloadBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();

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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.load_fixture(request.into_inner()),
        ))
    }

    async fn get_hot_reload_status(
        &self,
        _request: tonic::Request<sts2::bridge::proto::HotReloadStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HotReloadStatusResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::HotReloadStatusResult {
                result: Some(
                    sts2::bridge::proto::hot_reload_status_result::Result::Success(
                        sts2::bridge::proto::HotReloadStatusResponse {
                            status: Some(hot_reload_shell_status(self.supported, 3)),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn request_hot_reload(
        &self,
        request: tonic::Request<sts2::bridge::proto::HotReloadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HotReloadResult>, tonic::Status> {
        let request = request.into_inner();
        Ok(tonic::Response::new(sts2::bridge::proto::HotReloadResult {
            result: Some(sts2::bridge::proto::hot_reload_result::Result::Success(
                sts2::bridge::proto::HotReloadResponse {
                    request_id: request.request_id,
                    accepted: true,
                    status: Some(hot_reload_shell_status(true, 4)),
                    report: Some(sts2::bridge::proto::HotReloadReport {
                        status: "loaded".to_string(),
                        generation: 4,
                        requested_at: "2026-04-24T12:00:00Z".to_string(),
                        source_assembly_path: request.logic_artifact_path,
                        shadow_assembly_path: "/tmp/hot/Hot.Logic.dll".to_string(),
                        contract_version: 0,
                        logic_assembly_name: "Hot.Logic".to_string(),
                        entry_type: "Hot.Logic.Entry".to_string(),
                        previous_generation: 3,
                        previous_remains_active: false,
                        previous_disposed: true,
                        previous_unload_requested: true,
                        previous_collected: true,
                        duration_ms: 12,
                        error: None,
                        warnings: Vec::new(),
                    }),
                    notices: Vec::new(),
                },
            )),
        }))
    }
}

#[cfg(unix)]
fn hot_reload_shell_status(
    supported: bool,
    active_generation: u32,
) -> sts2::bridge::proto::HotReloadShellStatus {
    sts2::bridge::proto::HotReloadShellStatus {
        supported,
        protocol: Some(sts2::bridge::proto::HotReloadProtocolInfo {
            id: "spirectl.m57.hot-reload-shell".to_string(),
            version: 0,
        }),
        shell_mod_id: "hot".to_string(),
        shell_protocol_version: if supported { 0 } else { 0 },
        active_generation: if supported { active_generation } else { 0 },
        expected_logic_artifact_path: "/mods/HotMod/Hot.Logic.dll".to_string(),
        contract_version: if supported { 1 } else { 0 },
        reload_in_progress: false,
        last_reload_report: None,
        restart_required: false,
        notices: if supported {
            Vec::new()
        } else {
            vec![sts2::bridge::proto::HotReloadNotice {
                code: "hot-reload-shell-unsupported".to_string(),
                message: "The test bridge does not host a hot-reload shell.".to_string(),
            }]
        },
    }
}

#[cfg(unix)]
struct FixtureLoadBridgeService;

#[cfg(unix)]
#[tonic::async_trait]
impl BridgeService for FixtureLoadBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_hot_reload_unimplemented_rpc_stubs!();

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

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        let request = request.into_inner();
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
                            id: "combat".to_string(),
                            title: "Combat".to_string(),
                            screen_instance_id: "screen:combat:live".to_string(),
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
                            message: "Fixture loaded for runner test.".to_string(),
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

#[cfg(unix)]
struct NormalizedFixtureLoadBridgeService;

#[cfg(unix)]
#[tonic::async_trait]
impl BridgeService for NormalizedFixtureLoadBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_hot_reload_unimplemented_rpc_stubs!();

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

    async fn get_screenshot(
        &self,
        request: tonic::Request<ScreenshotRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ScreenshotResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::Combat);
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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        let request = request.into_inner();
        let fixture_json: Value =
            serde_json::from_str(&request.fixture_json).expect("fixture json should parse");

        assert_eq!(fixture_json["schemaVersion"], "spirectl.fixture/v0");
        assert_eq!(fixture_json["name"], "basic-combat");
        assert_eq!(fixture_json["run"]["view"]["playerId"], "p1");
        assert_eq!(fixture_json["run"]["actFloor"], 1);
        assert_eq!(fixture_json["run"]["seed"], "basic-combat");
        assert_eq!(fixture_json["run"]["players"][0]["id"], "p1");
        assert_eq!(fixture_json["run"]["players"][0]["characterId"], "IRONCLAD");
        assert_eq!(
            fixture_json["run"]["currentRoom"]["combat"]["encounterId"],
            "NIBBITS_NORMAL"
        );

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
                            id: "combat".to_string(),
                            title: "Combat".to_string(),
                            screen_instance_id: "screen:combat:live".to_string(),
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
                            message: "Fixture normalized for runner test.".to_string(),
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

#[cfg(unix)]
struct LogsFailingBridgeService;

#[cfg(unix)]
#[tonic::async_trait]
impl BridgeService for LogsFailingBridgeService {
    impl_state_watch_and_models_unimplemented!();
    impl_debug_session_rpc_stubs!();
    impl_hot_reload_unimplemented_rpc_stubs!();

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
        _request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        Ok(tonic::Response::new(sts2::bridge::proto::LogsResult {
            result: Some(sts2::bridge::proto::logs_result::Result::Error(
                sts2::bridge::proto::BridgeError {
                    code: sts2::bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
                    message: "forced log collection failure".to_string(),
                    details: vec![sts2::bridge::proto::ErrorDetail {
                        field: "artifact".to_string(),
                        value: "logs".to_string(),
                        note: "forced failure for runner artifact coverage".to_string(),
                        ..Default::default()
                    }],
                    action_failure: None,
                },
            )),
        }))
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
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneTreeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "runtime scene inspection is not implemented in this test service",
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeSceneNodeRequest>,
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
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.load_fixture(request.into_inner()),
        ))
    }
}
