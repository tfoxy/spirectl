struct TransitioningBridgeService {
    state_calls: Arc<AtomicUsize>,
    advertise_watch: bool,
}

impl Default for TransitioningBridgeService {
    fn default() -> Self {
        Self {
            state_calls: Arc::new(AtomicUsize::new(0)),
            advertise_watch: true,
        }
    }
}

impl TransitioningBridgeService {
    fn without_state_watch() -> Self {
        Self {
            advertise_watch: false,
            ..Self::default()
        }
    }
}

struct TransitionStatusBridgeService {
    calls: Arc<AtomicUsize>,
    blocking_attempts: usize,
    response_delay_ms: u64,
}

impl TransitionStatusBridgeService {
    fn new(blocking_attempts: usize) -> Self {
        Self {
            calls: Arc::new(AtomicUsize::new(0)),
            blocking_attempts,
            response_delay_ms: 0,
        }
    }

    fn with_response_delay_ms(blocking_attempts: usize, response_delay_ms: u64) -> Self {
        Self {
            calls: Arc::new(AtomicUsize::new(0)),
            blocking_attempts,
            response_delay_ms,
        }
    }
}

#[tonic::async_trait]
impl BridgeService for TransitionStatusBridgeService {
    impl_debug_session_rpc_stubs!();
    impl_state_watch_unimplemented!();

    impl_unimplemented_bridge_rpc!(
        get_state,
        StateRequest,
        sts2::bridge::proto::StateResult,
        "state is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        execute_action,
        sts2::bridge::proto::ActionRequest,
        sts2::bridge::proto::ActionResult,
        "actions are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        load_fixture,
        sts2::bridge::proto::FixtureLoadRequest,
        sts2::bridge::proto::FixtureLoadResult,
        "fixture loading is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_logs,
        sts2::bridge::proto::LogsRequest,
        sts2::bridge::proto::LogsResult,
        "logs are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_debug_status,
        sts2::bridge::proto::DebugStatusRequest,
        sts2::bridge::proto::DebugStatusResult,
        "debug status is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        pause_debug,
        sts2::bridge::proto::DebugPauseRequest,
        sts2::bridge::proto::DebugPauseResult,
        "debug pause is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        resume_debug,
        sts2::bridge::proto::DebugResumeRequest,
        sts2::bridge::proto::DebugResumeResult,
        "debug resume is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        step_debug,
        sts2::bridge::proto::DebugStepRequest,
        sts2::bridge::proto::DebugStepResult,
        "debug step is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        list_breakpoints,
        sts2::bridge::proto::DebugBreakpointListRequest,
        sts2::bridge::proto::DebugBreakpointListResult,
        "breakpoint listing is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        add_breakpoint,
        sts2::bridge::proto::DebugBreakpointAddRequest,
        sts2::bridge::proto::DebugBreakpointAddResult,
        "breakpoint add is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        remove_breakpoint,
        sts2::bridge::proto::DebugBreakpointRemoveRequest,
        sts2::bridge::proto::DebugBreakpointRemoveResult,
        "breakpoint remove is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_runtime_scene_tree,
        sts2::bridge::proto::RuntimeSceneTreeRequest,
        sts2::bridge::proto::RuntimeSceneTreeResult,
        "runtime scene tree is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_runtime_scene_node,
        sts2::bridge::proto::RuntimeSceneNodeRequest,
        sts2::bridge::proto::RuntimeSceneNodeResult,
        "runtime scene node is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        set_runtime_scene_node_visible,
        sts2::bridge::proto::RuntimeSceneSetVisibleRequest,
        sts2::bridge::proto::RuntimeSceneSetVisibleResult,
        "runtime scene visibility is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        hover_runtime_scene_control,
        sts2::bridge::proto::RuntimeSceneControlHoverRequest,
        sts2::bridge::proto::RuntimeSceneControlHoverResult,
        "runtime scene hover is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        unhover_runtime_scene_control,
        sts2::bridge::proto::RuntimeSceneControlUnhoverRequest,
        sts2::bridge::proto::RuntimeSceneControlUnhoverResult,
        "runtime scene unhover is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        inspect_presentation_resource_scenes,
        sts2::bridge::proto::PresentationResourceSceneRequest,
        sts2::bridge::proto::PresentationResourceSceneResult,
        "presentation resource scene inspection is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_screenshot,
        sts2::bridge::proto::ScreenshotRequest,
        sts2::bridge::proto::ScreenshotResult,
        "screenshot capture is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_asset_catalog,
        sts2::bridge::proto::AssetCatalogRequest,
        sts2::bridge::proto::AssetCatalogResult,
        "asset catalog is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        extract_asset,
        sts2::bridge::proto::AssetExtractRequest,
        sts2::bridge::proto::AssetExtractResult,
        "asset extraction is not exercised by this test service"
    );

    async fn handshake(
        &self,
        request: tonic::Request<HandshakeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HandshakeResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.handshake(request.into_inner()),
        ))
    }

    async fn get_runtime_transition_status(
        &self,
        _request: tonic::Request<sts2::bridge::proto::RuntimeTransitionStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeTransitionStatusResult>, tonic::Status>
    {
        if self.response_delay_ms > 0 {
            tokio::time::sleep(std::time::Duration::from_millis(self.response_delay_ms)).await;
        }

        let attempt = self.calls.fetch_add(1, Ordering::SeqCst) + 1;
        let blocking = attempt <= self.blocking_attempts;
        let response = sts2::bridge::proto::RuntimeTransitionStatusResponse {
            source: sts2::bridge::proto::DataSource::Live as i32,
            provisional: false,
            screen: Some(sts2::bridge::proto::ScreenInfo {
                id: "combat".to_string(),
                title: "Combat".to_string(),
                screen_instance_id: "screen:combat:1".to_string(),
                ..Default::default()
            }),
            quiescent: !blocking,
            blocking_count: u32::from(blocking),
            ignored_infinite_count: 1,
            blockers: blocking
                .then(|| sts2::bridge::proto::RuntimeTransitionBlocker {
                    kind: "tween".to_string(),
                    node_path: "/root/Game/Transition".to_string(),
                    node_type: "Godot.Tween".to_string(),
                    name: "Transition".to_string(),
                    status: "running".to_string(),
                    reason: "finite-tween-running".to_string(),
                    animation_name: None,
                    loops_left: Some(1),
                    running: Some(true),
                    infinite: Some(false),
                })
                .into_iter()
                .collect(),
            notes: vec![
                "Ignored running infinite animation loops while determining transition quiescence."
                    .to_string(),
            ],
        };

        Ok(tonic::Response::new(
            sts2::bridge::proto::RuntimeTransitionStatusResult {
                result: Some(
                    sts2::bridge::proto::runtime_transition_status_result::Result::Success(
                        response,
                    ),
                ),
            },
        ))
    }
}

#[tonic::async_trait]
impl BridgeService for TransitioningBridgeService {
    type WatchStateStream = std::pin::Pin<
        Box<
            dyn tokio_stream::Stream<
                    Item = Result<sts2::bridge::proto::StateWatchEvent, tonic::Status>,
                > + Send,
        >,
    >;

    type WatchCombatEventsStream = std::pin::Pin<
        Box<
            dyn tokio_stream::Stream<
                    Item = Result<sts2::bridge::proto::CombatEvent, tonic::Status>,
                > + Send,
        >,
    >;

    fn watch_combat_events<'life0, 'async_trait>(
        &'life0 self,
        _request: tonic::Request<sts2::bridge::proto::WatchCombatEventsRequest>,
    ) -> core::pin::Pin<
        Box<
            dyn core::future::Future<
                    Output = Result<
                        tonic::Response<Self::WatchCombatEventsStream>,
                        tonic::Status,
                    >,
                > + Send
                + 'async_trait,
        >,
    >
    where
        'life0: 'async_trait,
        Self: 'async_trait,
    {
        Box::pin(async move {
            Err(tonic::Status::unimplemented(
                "combat events watch is not exercised by this test service",
            ))
        })
    }

    impl_unimplemented_bridge_rpc!(
        get_combat_preview,
        sts2::bridge::proto::CombatPreviewRequest,
        sts2::bridge::proto::CombatPreviewResult,
        "combat preview is not exercised by this test service"
    );

    impl_debug_session_rpc_stubs!();
    impl_runtime_scene_set_visible_unimplemented!();

    async fn handshake(
        &self,
        request: tonic::Request<HandshakeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::HandshakeResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        let mut result = service.handshake(request.into_inner());
        if !self.advertise_watch
            && let Some(sts2::bridge::proto::handshake_result::Result::Success(success)) =
                result.result.as_mut()
        {
            success
                .capabilities
                .retain(|capability| capability.id != "state-watch");
        }
        Ok(tonic::Response::new(result))
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

    async fn watch_state(
        &self,
        request: tonic::Request<sts2::bridge::proto::StateWatchRequest>,
    ) -> Result<tonic::Response<Self::WatchStateStream>, tonic::Status> {
        let request = request.into_inner();
        let state_request = request.state.unwrap_or_default();
        let main_menu = StubBridgeService::new(MockScenario::MainMenu)
            .get_state(state_request.clone())
            .result
            .and_then(|result| match result {
                sts2::bridge::proto::state_result::Result::Success(state) => Some(state),
                _ => None,
            })
            .expect("main-menu state");
        let combat = StubBridgeService::new(MockScenario::Combat)
            .get_state(state_request)
            .result
            .and_then(|result| match result {
                sts2::bridge::proto::state_result::Result::Success(state) => Some(state),
                _ => None,
            })
            .expect("combat state");
        let events = vec![
            Ok(sts2::bridge::proto::StateWatchEvent {
                r#type: sts2::bridge::proto::StateWatchEventType::Initial as i32,
                sequence: 1,
                observed_at_utc: "2026-05-19T00:00:00.000Z".to_string(),
                fingerprint: "main-menu".to_string(),
                semantic_revision: 1,
                payload: Some(sts2::bridge::proto::state_watch_event::Payload::State(
                    main_menu,
                )),
            }),
            Ok(sts2::bridge::proto::StateWatchEvent {
                r#type: sts2::bridge::proto::StateWatchEventType::Changed as i32,
                sequence: 2,
                observed_at_utc: "2026-05-19T00:00:00.050Z".to_string(),
                fingerprint: "combat".to_string(),
                semantic_revision: 2,
                payload: Some(sts2::bridge::proto::state_watch_event::Payload::State(
                    combat,
                )),
            }),
        ];
        Ok(tonic::Response::new(Box::pin(tokio_stream::iter(events))))
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
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::FixtureLoadResult {
                result: Some(sts2::bridge::proto::fixture_load_result::Result::Error(
                    sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::NotImplemented as i32,
                        message: "fixture loading is not implemented in this test service"
                            .to_string(),
                        details: Vec::new(),
                        action_failure: None,
                    },
                )),
            },
        ))
    }
}

struct ErroringStateBridgeService;

#[tonic::async_trait]
impl BridgeService for ErroringStateBridgeService {
    type WatchStateStream = std::pin::Pin<
        Box<
            dyn tokio_stream::Stream<
                    Item = Result<sts2::bridge::proto::StateWatchEvent, tonic::Status>,
                > + Send,
        >,
    >;

    type WatchCombatEventsStream = std::pin::Pin<
        Box<
            dyn tokio_stream::Stream<
                    Item = Result<sts2::bridge::proto::CombatEvent, tonic::Status>,
                > + Send,
        >,
    >;

    fn watch_combat_events<'life0, 'async_trait>(
        &'life0 self,
        _request: tonic::Request<sts2::bridge::proto::WatchCombatEventsRequest>,
    ) -> core::pin::Pin<
        Box<
            dyn core::future::Future<
                    Output = Result<
                        tonic::Response<Self::WatchCombatEventsStream>,
                        tonic::Status,
                    >,
                > + Send
                + 'async_trait,
        >,
    >
    where
        'life0: 'async_trait,
        Self: 'async_trait,
    {
        Box::pin(async move {
            Err(tonic::Status::unimplemented(
                "combat events watch is not exercised by this test service",
            ))
        })
    }

    impl_unimplemented_bridge_rpc!(
        get_combat_preview,
        sts2::bridge::proto::CombatPreviewRequest,
        sts2::bridge::proto::CombatPreviewResult,
        "combat preview is not exercised by this test service"
    );

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
        _request: tonic::Request<StateRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::StateResult>, tonic::Status> {
        Ok(tonic::Response::new(sts2::bridge::proto::StateResult {
            result: Some(sts2::bridge::proto::state_result::Result::Error(
                sts2::bridge::proto::BridgeError {
                    code: sts2::bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
                    message: "runtime-state-failed".to_string(),
                    details: vec![sts2::bridge::proto::ErrorDetail {
                        field: "state".to_string(),
                        value: "current-observation".to_string(),
                        note: "state extraction failed in test bridge".to_string(),
                        ..sts2::bridge::proto::ErrorDetail::default()
                    }],
                    action_failure: None,
                },
            )),
        }))
    }

    async fn watch_state(
        &self,
        _request: tonic::Request<sts2::bridge::proto::StateWatchRequest>,
    ) -> Result<tonic::Response<Self::WatchStateStream>, tonic::Status> {
        let error = sts2::bridge::proto::BridgeError {
            code: sts2::bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
            message: "runtime-state-failed".to_string(),
            details: vec![sts2::bridge::proto::ErrorDetail {
                field: "state".to_string(),
                value: "current-observation".to_string(),
                note: "state extraction failed in test bridge".to_string(),
                ..sts2::bridge::proto::ErrorDetail::default()
            }],
            action_failure: None,
        };
        let events = vec![
            Ok(sts2::bridge::proto::StateWatchEvent {
                r#type: sts2::bridge::proto::StateWatchEventType::Error as i32,
                sequence: 1,
                observed_at_utc: "2026-05-19T00:00:00.000Z".to_string(),
                fingerprint: "error-1".to_string(),
                semantic_revision: 0,
                payload: Some(sts2::bridge::proto::state_watch_event::Payload::Error(
                    error.clone(),
                )),
            }),
            Ok(sts2::bridge::proto::StateWatchEvent {
                r#type: sts2::bridge::proto::StateWatchEventType::Error as i32,
                sequence: 2,
                observed_at_utc: "2026-05-19T00:00:00.050Z".to_string(),
                fingerprint: "error-2".to_string(),
                semantic_revision: 0,
                payload: Some(sts2::bridge::proto::state_watch_event::Payload::Error(
                    error,
                )),
            }),
        ];
        Ok(tonic::Response::new(Box::pin(tokio_stream::iter(events))))
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
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::FixtureLoadResult {
                result: Some(sts2::bridge::proto::fixture_load_result::Result::Error(
                    sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::NotImplemented as i32,
                        message: "fixture loading is not implemented in this test service"
                            .to_string(),
                        details: Vec::new(),
                        action_failure: None,
                    },
                )),
            },
        ))
    }
}

struct DiagnosticsBridgeService;

#[tonic::async_trait]
impl BridgeService for DiagnosticsBridgeService {
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
        _request: tonic::Request<LogsRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::LogsResult>, tonic::Status> {
        Ok(tonic::Response::new(sts2::bridge::proto::LogsResult {
            result: Some(sts2::bridge::proto::logs_result::Result::Success(
                sts2::bridge::proto::LogsResponse {
                    source: sts2::bridge::proto::DataSource::Live as i32,
                    provisional: false,
                    next_cursor: 13,
                    entries: vec![
                        sts2::bridge::proto::LogEntry {
                            cursor: 11,
                            level: sts2::bridge::proto::LogLevel::Info as i32,
                            target: "bridge.transport".to_string(),
                            message: "Transport connected.".to_string(),
                        },
                        sts2::bridge::proto::LogEntry {
                            cursor: 12,
                            level: sts2::bridge::proto::LogLevel::Error as i32,
                            target: "bridge.exception".to_string(),
                            message: "System.Exception: bridge exploded".to_string(),
                        },
                        sts2::bridge::proto::LogEntry {
                            cursor: 13,
                            level: sts2::bridge::proto::LogLevel::Warn as i32,
                            target: "bridge.action".to_string(),
                            message: "Unhandled exception while resolving action.".to_string(),
                        },
                    ],
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
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Ok(tonic::Response::new(
            sts2::bridge::proto::FixtureLoadResult {
                result: Some(sts2::bridge::proto::fixture_load_result::Result::Error(
                    sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::NotImplemented as i32,
                        message: "fixture loading is not implemented in this test service"
                            .to_string(),
                        details: Vec::new(),
                        action_failure: None,
                    },
                )),
            },
        ))
    }
}

struct FixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for FixtureLoadBridgeService {
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
        if fixture_json["name"] == "two-player-combat" {
            assert_eq!(fixture_json["schemaVersion"], "spirectl.fixture/v0");
            assert_eq!(fixture_json["screen"], "combat");
            assert_eq!(fixture_json["run"]["view"]["playerId"], "p:1");
            assert_eq!(fixture_json["run"]["players"][0]["id"], "p:1");
            assert_eq!(fixture_json["run"]["players"][0]["isLocal"], true);
            assert_eq!(fixture_json["run"]["players"][0]["isHostLocalSeat"], false);
            assert_eq!(fixture_json["run"]["players"][0]["slotId"], 0);
            assert_eq!(fixture_json["run"]["players"][1]["id"], "p:2");
            assert_eq!(fixture_json["run"]["players"][1]["isLocal"], true);
            assert_eq!(fixture_json["run"]["players"][1]["isHostLocalSeat"], true);
            assert_eq!(fixture_json["run"]["players"][1]["slotId"], 1);
            assert_eq!(
                fixture_json["run"]["currentRoom"]["combat"]["encounterId"],
                "NIBBITS_NORMAL"
            );
        }

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
                            message: format!(
                                "Loaded fixture JSON with {} bytes.",
                                request.fixture_json.len()
                            ),
                            provisional: false,
                            ..Default::default()
                        }],
                        recipe_report: Some(sts2::bridge::proto::FixtureRecipeRestoreReport {
                            recipe_name: "combat-recipe".to_string(),
                            applied_fields: vec![sts2::bridge::proto::FixtureRecipeFieldReport {
                                field_path: "run.currentRoom.combat.encounterId".to_string(),
                                value_summary: "NIBBITS_NORMAL".to_string(),
                                reason_code: "applied_authored_field".to_string(),
                                message: "Applied authored encounter id.".to_string(),
                            }],
                            inferred_fields: vec![sts2::bridge::proto::FixtureRecipeFieldReport {
                                field_path: "run.seed".to_string(),
                                value_summary: "basic-combat".to_string(),
                                reason_code: "inferred_default".to_string(),
                                message: "Inferred deterministic fixture seed.".to_string(),
                            }],
                            omitted_fields: Vec::new(),
                            unsupported_fields: Vec::new(),
                            degraded_multiplayer_fields: Vec::new(),
                            bridge_validation: Some(
                                sts2::bridge::proto::FixtureBridgeValidationResult {
                                    status: "passed".to_string(),
                                    details: Vec::new(),
                                },
                            ),
                        }),
                    },
                )),
            },
        ))
    }
}

struct NormalizedFixtureLoadBridgeService;

#[tonic::async_trait]
impl BridgeService for NormalizedFixtureLoadBridgeService {
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
        assert_eq!(fixture_json["run"]["ascensionLevel"], 3);
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
                            message: "Fixture normalized for direct CLI test.".to_string(),
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
