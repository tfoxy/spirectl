#[derive(Clone)]
pub struct StubBridgeGrpcService {
    inner: StubBridgeService,
}

impl StubBridgeGrpcService {
    pub fn new(scenario: MockScenario) -> Self {
        Self {
            inner: StubBridgeService::new(scenario),
        }
    }

    pub fn with_build_identity(mut self, build_identity: proto::BridgeBuildIdentity) -> Self {
        self.inner = self.inner.with_build_identity(build_identity);
        self
    }

    pub fn with_bridge_version(mut self, bridge_version: &str) -> Self {
        self.inner = self.inner.with_bridge_version(bridge_version);
        self
    }
}

#[tonic::async_trait]
impl proto::bridge_service_server::BridgeService for StubBridgeGrpcService {
    type WatchStateStream = std::pin::Pin<
        Box<dyn tokio_stream::Stream<Item = Result<proto::StateWatchEvent, tonic::Status>> + Send>,
    >;

    type WatchCombatEventsStream = std::pin::Pin<
        Box<dyn tokio_stream::Stream<Item = Result<proto::CombatEvent, tonic::Status>> + Send>,
    >;

    async fn handshake(
        &self,
        request: tonic::Request<proto::HandshakeRequest>,
    ) -> Result<tonic::Response<proto::HandshakeResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.handshake(request.into_inner()),
        ))
    }

    async fn get_state(
        &self,
        request: tonic::Request<proto::StateRequest>,
    ) -> Result<tonic::Response<proto::StateResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_state(request.into_inner()),
        ))
    }

    async fn watch_state(
        &self,
        request: tonic::Request<proto::StateWatchRequest>,
    ) -> Result<tonic::Response<Self::WatchStateStream>, tonic::Status> {
        let state_request = request.into_inner().state.unwrap_or_default();
        let event = match self.inner.get_state(state_request).result {
            Some(proto::state_result::Result::Success(state)) => proto::StateWatchEvent {
                r#type: proto::StateWatchEventType::Initial as i32,
                sequence: 1,
                observed_at_utc: current_unix_observed_at_utc(),
                fingerprint: String::new(),
                semantic_revision: 1,
                payload: Some(proto::state_watch_event::Payload::State(state)),
            },
            Some(proto::state_result::Result::Error(error)) => proto::StateWatchEvent {
                r#type: proto::StateWatchEventType::Error as i32,
                sequence: 1,
                observed_at_utc: current_unix_observed_at_utc(),
                fingerprint: String::new(),
                semantic_revision: 0,
                payload: Some(proto::state_watch_event::Payload::Error(error)),
            },
            None => proto::StateWatchEvent {
                r#type: proto::StateWatchEventType::Error as i32,
                sequence: 1,
                observed_at_utc: current_unix_observed_at_utc(),
                fingerprint: String::new(),
                semantic_revision: 0,
                payload: Some(proto::state_watch_event::Payload::Error(error(
                    proto::BridgeErrorCode::RuntimeFailure,
                    "State watch returned an empty result envelope.",
                    &[],
                ))),
            },
        };
        Ok(tonic::Response::new(Box::pin(tokio_stream::iter(vec![
            Ok(event),
        ]))))
    }

    async fn watch_combat_events(
        &self,
        _request: tonic::Request<proto::WatchCombatEventsRequest>,
    ) -> Result<tonic::Response<Self::WatchCombatEventsStream>, tonic::Status> {
        // The stub bridge has no live combat; emit an empty (immediately-completing) stream.
        Ok(tonic::Response::new(Box::pin(tokio_stream::iter(
            Vec::<Result<proto::CombatEvent, tonic::Status>>::new(),
        ))))
    }

    async fn execute_action(
        &self,
        request: tonic::Request<proto::ActionRequest>,
    ) -> Result<tonic::Response<proto::ActionResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.execute_action(request.into_inner()),
        ))
    }

    async fn execute_console_command(
        &self,
        request: tonic::Request<proto::ConsoleCommandRequest>,
    ) -> Result<tonic::Response<proto::ConsoleCommandResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.execute_console_command(request.into_inner()),
        ))
    }

    async fn get_logs(
        &self,
        request: tonic::Request<proto::LogsRequest>,
    ) -> Result<tonic::Response<proto::LogsResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_logs(request.into_inner()),
        ))
    }

    async fn get_debug_status(
        &self,
        request: tonic::Request<proto::DebugStatusRequest>,
    ) -> Result<tonic::Response<proto::DebugStatusResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_debug_status(request.into_inner()),
        ))
    }

    async fn start_debug_session(
        &self,
        request: tonic::Request<proto::DebugSessionStartRequest>,
    ) -> Result<tonic::Response<proto::DebugSessionStartResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.start_debug_session(request.into_inner()),
        ))
    }

    async fn get_debug_session_status(
        &self,
        request: tonic::Request<proto::DebugSessionStatusRequest>,
    ) -> Result<tonic::Response<proto::DebugSessionStatusResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_debug_session_status(request.into_inner()),
        ))
    }

    async fn end_debug_session(
        &self,
        request: tonic::Request<proto::DebugSessionEndRequest>,
    ) -> Result<tonic::Response<proto::DebugSessionEndResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.end_debug_session(request.into_inner()),
        ))
    }

    async fn pause_debug(
        &self,
        request: tonic::Request<proto::DebugPauseRequest>,
    ) -> Result<tonic::Response<proto::DebugPauseResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.pause_debug(request.into_inner()),
        ))
    }

    async fn resume_debug(
        &self,
        request: tonic::Request<proto::DebugResumeRequest>,
    ) -> Result<tonic::Response<proto::DebugResumeResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.resume_debug(request.into_inner()),
        ))
    }

    async fn step_debug(
        &self,
        request: tonic::Request<proto::DebugStepRequest>,
    ) -> Result<tonic::Response<proto::DebugStepResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.step_debug(request.into_inner()),
        ))
    }

    async fn wait_debug(
        &self,
        request: tonic::Request<proto::DebugWaitRequest>,
    ) -> Result<tonic::Response<proto::DebugWaitResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.wait_debug(request.into_inner()),
        ))
    }

    async fn list_breakpoints(
        &self,
        request: tonic::Request<proto::DebugBreakpointListRequest>,
    ) -> Result<tonic::Response<proto::DebugBreakpointListResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.list_breakpoints(request.into_inner()),
        ))
    }

    async fn add_breakpoint(
        &self,
        request: tonic::Request<proto::DebugBreakpointAddRequest>,
    ) -> Result<tonic::Response<proto::DebugBreakpointAddResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.add_breakpoint(request.into_inner()),
        ))
    }

    async fn remove_breakpoint(
        &self,
        request: tonic::Request<proto::DebugBreakpointRemoveRequest>,
    ) -> Result<tonic::Response<proto::DebugBreakpointRemoveResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.remove_breakpoint(request.into_inner()),
        ))
    }

    async fn get_debug_events(
        &self,
        request: tonic::Request<proto::DebugEventStreamRequest>,
    ) -> Result<tonic::Response<proto::DebugEventStreamResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_debug_events(request.into_inner()),
        ))
    }

    async fn load_fixture(
        &self,
        request: tonic::Request<proto::FixtureLoadRequest>,
    ) -> Result<tonic::Response<proto::FixtureLoadResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.load_fixture(request.into_inner()),
        ))
    }

    async fn record_fixture(
        &self,
        request: tonic::Request<proto::RecordedFixtureRequest>,
    ) -> Result<tonic::Response<proto::RecordedFixtureResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.record_fixture(request.into_inner()),
        ))
    }

    async fn get_screenshot(
        &self,
        request: tonic::Request<proto::ScreenshotRequest>,
    ) -> Result<tonic::Response<proto::ScreenshotResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_screenshot(request.into_inner()),
        ))
    }

    async fn get_runtime_scene_tree(
        &self,
        request: tonic::Request<proto::RuntimeSceneTreeRequest>,
    ) -> Result<tonic::Response<proto::RuntimeSceneTreeResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_runtime_scene_tree(request.into_inner()),
        ))
    }

    async fn get_runtime_scene_node(
        &self,
        request: tonic::Request<proto::RuntimeSceneNodeRequest>,
    ) -> Result<tonic::Response<proto::RuntimeSceneNodeResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_runtime_scene_node(request.into_inner()),
        ))
    }

    async fn set_runtime_scene_node_visible(
        &self,
        request: tonic::Request<proto::RuntimeSceneSetVisibleRequest>,
    ) -> Result<tonic::Response<proto::RuntimeSceneSetVisibleResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner
                .set_runtime_scene_node_visible(request.into_inner()),
        ))
    }

    async fn hover_runtime_scene_control(
        &self,
        request: tonic::Request<proto::RuntimeSceneControlHoverRequest>,
    ) -> Result<tonic::Response<proto::RuntimeSceneControlHoverResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.hover_runtime_scene_control(request.into_inner()),
        ))
    }

    async fn unhover_runtime_scene_control(
        &self,
        request: tonic::Request<proto::RuntimeSceneControlUnhoverRequest>,
    ) -> Result<tonic::Response<proto::RuntimeSceneControlUnhoverResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.unhover_runtime_scene_control(request.into_inner()),
        ))
    }

    async fn get_runtime_transition_status(
        &self,
        request: tonic::Request<proto::RuntimeTransitionStatusRequest>,
    ) -> Result<tonic::Response<proto::RuntimeTransitionStatusResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner
                .get_runtime_transition_status(request.into_inner()),
        ))
    }

    async fn inspect_presentation_resource_scenes(
        &self,
        request: tonic::Request<proto::PresentationResourceSceneRequest>,
    ) -> Result<tonic::Response<proto::PresentationResourceSceneResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner
                .inspect_presentation_resource_scenes(request.into_inner()),
        ))
    }

    async fn inspect_presentation_localization(
        &self,
        request: tonic::Request<proto::PresentationLocalizationRequest>,
    ) -> Result<tonic::Response<proto::PresentationLocalizationResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner
                .inspect_presentation_localization(request.into_inner()),
        ))
    }

    async fn extract_asset(
        &self,
        request: tonic::Request<proto::AssetExtractRequest>,
    ) -> Result<tonic::Response<proto::AssetExtractResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.extract_asset(request.into_inner()),
        ))
    }

    async fn get_asset_catalog(
        &self,
        request: tonic::Request<proto::AssetCatalogRequest>,
    ) -> Result<tonic::Response<proto::AssetCatalogResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_asset_catalog(request.into_inner()),
        ))
    }

    async fn explain_asset(
        &self,
        request: tonic::Request<proto::AssetExplainRequest>,
    ) -> Result<tonic::Response<proto::AssetExplainResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.explain_asset(request.into_inner()),
        ))
    }

    async fn close_game(
        &self,
        request: tonic::Request<proto::GameCloseRequest>,
    ) -> Result<tonic::Response<proto::GameCloseResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.close_game(request.into_inner()),
        ))
    }

    async fn get_mods(
        &self,
        request: tonic::Request<proto::ModListRequest>,
    ) -> Result<tonic::Response<proto::ModListResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_mods(request.into_inner()),
        ))
    }

    async fn get_models(
        &self,
        request: tonic::Request<proto::ModelCatalogRequest>,
    ) -> Result<tonic::Response<proto::ModelCatalogResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_models(request.into_inner()),
        ))
    }

    async fn get_combat_preview(
        &self,
        request: tonic::Request<proto::CombatPreviewRequest>,
    ) -> Result<tonic::Response<proto::CombatPreviewResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_combat_preview(request.into_inner()),
        ))
    }

    async fn get_reference(
        &self,
        request: tonic::Request<proto::ReferenceRequest>,
    ) -> Result<tonic::Response<proto::ReferenceResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_reference(request.into_inner()),
        ))
    }

    async fn capture_scenario(
        &self,
        request: tonic::Request<proto::ScenarioCaptureRequest>,
    ) -> Result<tonic::Response<proto::ScenarioCaptureResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.capture_scenario(request.into_inner()),
        ))
    }

    async fn restore_scenario(
        &self,
        request: tonic::Request<proto::ScenarioRestoreRequest>,
    ) -> Result<tonic::Response<proto::ScenarioRestoreResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.restore_scenario(request.into_inner()),
        ))
    }

    async fn capture_checkpoint(
        &self,
        request: tonic::Request<proto::CheckpointCaptureRequest>,
    ) -> Result<tonic::Response<proto::CheckpointCaptureResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.capture_checkpoint(request.into_inner()),
        ))
    }

    async fn restore_checkpoint(
        &self,
        request: tonic::Request<proto::CheckpointRestoreRequest>,
    ) -> Result<tonic::Response<proto::CheckpointRestoreResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.restore_checkpoint(request.into_inner()),
        ))
    }

    async fn list_checkpoints(
        &self,
        request: tonic::Request<proto::CheckpointListRequest>,
    ) -> Result<tonic::Response<proto::CheckpointListResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.list_checkpoints(request.into_inner()),
        ))
    }

    async fn delete_checkpoint(
        &self,
        request: tonic::Request<proto::CheckpointDeleteRequest>,
    ) -> Result<tonic::Response<proto::CheckpointDeleteResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.delete_checkpoint(request.into_inner()),
        ))
    }

    async fn get_hot_reload_status(
        &self,
        request: tonic::Request<proto::HotReloadStatusRequest>,
    ) -> Result<tonic::Response<proto::HotReloadStatusResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.get_hot_reload_status(request.into_inner()),
        ))
    }

    async fn request_hot_reload(
        &self,
        request: tonic::Request<proto::HotReloadRequest>,
    ) -> Result<tonic::Response<proto::HotReloadResult>, tonic::Status> {
        Ok(tonic::Response::new(
            self.inner.request_hot_reload(request.into_inner()),
        ))
    }
}
