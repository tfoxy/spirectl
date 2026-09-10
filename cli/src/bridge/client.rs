use super::*;

#[derive(Debug, Clone)]
pub struct RuntimeBridgeClient {
    inner: BridgeClientKind,
}

impl RuntimeBridgeClient {
    pub fn from_config(config: &TransportConfig) -> Self {
        let inner = match config.kind {
            ConfigTransportKind::Mock => {
                BridgeClientKind::Mock(MockBridgeClient::new(config.mock_scenario))
            }
            ConfigTransportKind::Ipc => resolve_ipc_bridge_client(config),
            ConfigTransportKind::Tcp => resolve_tcp_bridge_client(config),
        };
        Self { inner }
    }

    pub fn handshake(
        &self,
        request: proto::HandshakeRequest,
    ) -> Result<proto::HandshakeResponse, proto::BridgeError> {
        self.inner.handshake(request)
    }

    pub fn state(
        &self,
        request: proto::StateRequest,
    ) -> Result<proto::StateResponse, proto::BridgeError> {
        self.inner.state(request)
    }

    pub fn watch_state<F>(
        &self,
        request: proto::StateWatchRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::StateWatchEvent) -> Result<bool, proto::BridgeError>,
    {
        self.inner.watch_state(request, on_event)
    }

    pub fn watch_combat_events<F>(
        &self,
        request: proto::WatchCombatEventsRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::CombatEvent) -> Result<bool, proto::BridgeError>,
    {
        self.inner.watch_combat_events(request, on_event)
    }

    pub fn execute_action(
        &self,
        request: proto::ActionRequest,
    ) -> Result<proto::ActionResponse, proto::BridgeError> {
        self.inner.execute_action(request)
    }

    pub fn execute_console_command(
        &self,
        request: proto::ConsoleCommandRequest,
    ) -> Result<proto::ConsoleCommandResponse, proto::BridgeError> {
        self.inner.execute_console_command(request)
    }

    pub fn logs(
        &self,
        request: proto::LogsRequest,
    ) -> Result<proto::LogsResponse, proto::BridgeError> {
        self.inner.logs(request)
    }

    pub fn debug_status(
        &self,
        request: proto::DebugStatusRequest,
    ) -> Result<proto::DebugStatusResponse, proto::BridgeError> {
        self.inner.debug_status(request)
    }

    pub fn debug_session_start(
        &self,
        request: proto::DebugSessionStartRequest,
    ) -> Result<proto::DebugSessionStartResponse, proto::BridgeError> {
        self.inner.debug_session_start(request)
    }

    pub fn debug_session_status(
        &self,
        request: proto::DebugSessionStatusRequest,
    ) -> Result<proto::DebugSessionStatusResponse, proto::BridgeError> {
        self.inner.debug_session_status(request)
    }

    pub fn debug_session_end(
        &self,
        request: proto::DebugSessionEndRequest,
    ) -> Result<proto::DebugSessionEndResponse, proto::BridgeError> {
        self.inner.debug_session_end(request)
    }

    pub fn debug_pause(
        &self,
        request: proto::DebugPauseRequest,
    ) -> Result<proto::DebugPauseResponse, proto::BridgeError> {
        self.inner.debug_pause(request)
    }

    pub fn debug_resume(
        &self,
        request: proto::DebugResumeRequest,
    ) -> Result<proto::DebugResumeResponse, proto::BridgeError> {
        self.inner.debug_resume(request)
    }

    pub fn debug_step(
        &self,
        request: proto::DebugStepRequest,
    ) -> Result<proto::DebugStepResponse, proto::BridgeError> {
        self.inner.debug_step(request)
    }

    pub fn debug_wait(
        &self,
        request: proto::DebugWaitRequest,
    ) -> Result<proto::DebugWaitResponse, proto::BridgeError> {
        self.inner.debug_wait(request)
    }

    pub fn breakpoint_list(
        &self,
        request: proto::DebugBreakpointListRequest,
    ) -> Result<proto::DebugBreakpointListResponse, proto::BridgeError> {
        self.inner.breakpoint_list(request)
    }

    pub fn breakpoint_add(
        &self,
        request: proto::DebugBreakpointAddRequest,
    ) -> Result<proto::DebugBreakpointAddResponse, proto::BridgeError> {
        self.inner.breakpoint_add(request)
    }

    pub fn breakpoint_remove(
        &self,
        request: proto::DebugBreakpointRemoveRequest,
    ) -> Result<proto::DebugBreakpointRemoveResponse, proto::BridgeError> {
        self.inner.breakpoint_remove(request)
    }

    pub fn debug_events(
        &self,
        request: proto::DebugEventStreamRequest,
    ) -> Result<proto::DebugEventStreamResponse, proto::BridgeError> {
        self.inner.debug_events(request)
    }

    pub fn load_fixture(
        &self,
        request: proto::FixtureLoadRequest,
    ) -> Result<proto::FixtureLoadResponse, proto::BridgeError> {
        self.inner.load_fixture(request)
    }

    pub fn record_fixture(
        &self,
        request: proto::RecordedFixtureRequest,
    ) -> Result<proto::RecordedFixtureResponse, proto::BridgeError> {
        self.inner.record_fixture(request)
    }

    pub fn screenshot(
        &self,
        request: proto::ScreenshotRequest,
    ) -> Result<proto::ScreenshotResponse, proto::BridgeError> {
        self.inner.screenshot(request)
    }

    pub fn runtime_scene_tree(
        &self,
        request: proto::RuntimeSceneTreeRequest,
    ) -> Result<proto::RuntimeSceneTreeResponse, proto::BridgeError> {
        self.inner.runtime_scene_tree(request)
    }

    pub fn runtime_scene_node(
        &self,
        request: proto::RuntimeSceneNodeRequest,
    ) -> Result<proto::RuntimeSceneNodeResponse, proto::BridgeError> {
        self.inner.runtime_scene_node(request)
    }

    pub fn runtime_scene_set_visible(
        &self,
        request: proto::RuntimeSceneSetVisibleRequest,
    ) -> Result<proto::RuntimeSceneSetVisibleResponse, proto::BridgeError> {
        self.inner.runtime_scene_set_visible(request)
    }

    pub fn runtime_scene_hover(
        &self,
        request: proto::RuntimeSceneControlHoverRequest,
    ) -> Result<proto::RuntimeSceneControlHoverResponse, proto::BridgeError> {
        self.inner.runtime_scene_hover(request)
    }

    pub fn runtime_scene_unhover(
        &self,
        request: proto::RuntimeSceneControlUnhoverRequest,
    ) -> Result<proto::RuntimeSceneControlUnhoverResponse, proto::BridgeError> {
        self.inner.runtime_scene_unhover(request)
    }

    pub fn runtime_transition_status(
        &self,
        request: proto::RuntimeTransitionStatusRequest,
    ) -> Result<proto::RuntimeTransitionStatusResponse, proto::BridgeError> {
        self.inner.runtime_transition_status(request)
    }

    pub fn presentation_resource_scenes(
        &self,
        request: proto::PresentationResourceSceneRequest,
    ) -> Result<proto::PresentationResourceSceneResponse, proto::BridgeError> {
        self.inner.presentation_resource_scenes(request)
    }

    pub fn presentation_localization(
        &self,
        request: proto::PresentationLocalizationRequest,
    ) -> Result<proto::PresentationLocalizationResponse, proto::BridgeError> {
        self.inner.presentation_localization(request)
    }

    pub fn extract_asset(
        &self,
        request: proto::AssetExtractRequest,
    ) -> Result<proto::AssetExtractResponse, proto::BridgeError> {
        self.inner.extract_asset(request)
    }

    pub fn asset_catalog(
        &self,
        request: proto::AssetCatalogRequest,
    ) -> Result<proto::AssetCatalogResponse, proto::BridgeError> {
        self.inner.asset_catalog(request)
    }

    pub fn explain_asset(
        &self,
        request: proto::AssetExplainRequest,
    ) -> Result<proto::AssetExplainResponse, proto::BridgeError> {
        self.inner.explain_asset(request)
    }

    pub fn close_game(
        &self,
        request: proto::GameCloseRequest,
    ) -> Result<proto::GameCloseResponse, proto::BridgeError> {
        self.inner.close_game(request)
    }

    pub fn mods(
        &self,
        request: proto::ModListRequest,
    ) -> Result<proto::ModListResponse, proto::BridgeError> {
        self.inner.mods(request)
    }

    pub fn models(
        &self,
        request: proto::ModelCatalogRequest,
    ) -> Result<proto::ModelCatalogResponse, proto::BridgeError> {
        self.inner.models(request)
    }

    pub fn combat_preview(
        &self,
        request: proto::CombatPreviewRequest,
    ) -> Result<proto::CombatPreviewResponse, proto::BridgeError> {
        self.inner.combat_preview(request)
    }

    pub fn map_drawings(
        &self,
        request: proto::MapDrawingsRequest,
    ) -> Result<proto::MapDrawingsResponse, proto::BridgeError> {
        self.inner.map_drawings(request)
    }

    pub fn reference(
        &self,
        request: proto::ReferenceRequest,
    ) -> Result<proto::ReferenceResponse, proto::BridgeError> {
        self.inner.reference(request)
    }

    pub fn capture_scenario(
        &self,
        request: proto::ScenarioCaptureRequest,
    ) -> Result<proto::ScenarioCaptureResponse, proto::BridgeError> {
        self.inner.capture_scenario(request)
    }

    pub fn restore_scenario(
        &self,
        request: proto::ScenarioRestoreRequest,
    ) -> Result<proto::ScenarioRestoreResponse, proto::BridgeError> {
        self.inner.restore_scenario(request)
    }

    pub fn capture_checkpoint(
        &self,
        request: proto::CheckpointCaptureRequest,
    ) -> Result<proto::CheckpointCaptureResponse, proto::BridgeError> {
        self.inner.capture_checkpoint(request)
    }

    pub fn restore_checkpoint(
        &self,
        request: proto::CheckpointRestoreRequest,
    ) -> Result<proto::CheckpointRestoreResponse, proto::BridgeError> {
        self.inner.restore_checkpoint(request)
    }

    pub fn hot_reload_status(
        &self,
        request: proto::HotReloadStatusRequest,
    ) -> Result<proto::HotReloadStatusResponse, proto::BridgeError> {
        self.inner.hot_reload_status(request)
    }

    pub fn hot_reload(
        &self,
        request: proto::HotReloadRequest,
    ) -> Result<proto::HotReloadResponse, proto::BridgeError> {
        self.inner.hot_reload(request)
    }
}

#[derive(Debug, Clone)]
enum BridgeClientKind {
    Mock(MockBridgeClient),
    Ipc(IpcBridgeClient),
    Tcp(TcpBridgeClient),
    Invalid(InvalidBridgeClient),
    #[allow(dead_code)]
    Unavailable(UnavailableBridgeClient),
}

impl BridgeClientKind {
    fn handshake(
        &self,
        request: proto::HandshakeRequest,
    ) -> Result<proto::HandshakeResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_handshake(client.service.handshake(request)),
            Self::Ipc(client) => client.handshake(request),
            Self::Tcp(client) => client.handshake(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn state(
        &self,
        request: proto::StateRequest,
    ) -> Result<proto::StateResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_state(client.service.get_state(request)),
            Self::Ipc(client) => client.state(request),
            Self::Tcp(client) => client.state(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn watch_state<F>(
        &self,
        request: proto::StateWatchRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::StateWatchEvent) -> Result<bool, proto::BridgeError>,
    {
        match self {
            Self::Mock(client) => client.watch_state(request, on_event),
            Self::Ipc(client) => client.watch_state(request, on_event),
            Self::Tcp(client) => client.watch_state(request, on_event),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn watch_combat_events<F>(
        &self,
        request: proto::WatchCombatEventsRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::CombatEvent) -> Result<bool, proto::BridgeError>,
    {
        match self {
            Self::Mock(client) => client.watch_combat_events(request, on_event),
            Self::Ipc(client) => client.watch_combat_events(request, on_event),
            Self::Tcp(client) => client.watch_combat_events(request, on_event),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn execute_action(
        &self,
        request: proto::ActionRequest,
    ) -> Result<proto::ActionResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_action(client.service.execute_action(request)),
            Self::Ipc(client) => client.execute_action(request),
            Self::Tcp(client) => client.execute_action(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn execute_console_command(
        &self,
        request: proto::ConsoleCommandRequest,
    ) -> Result<proto::ConsoleCommandResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_console_command(client.service.execute_console_command(request))
            }
            Self::Ipc(client) => client.execute_console_command(request),
            Self::Tcp(client) => client.execute_console_command(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn logs(&self, request: proto::LogsRequest) -> Result<proto::LogsResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_logs(client.service.get_logs(request)),
            Self::Ipc(client) => client.logs(request),
            Self::Tcp(client) => client.logs(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_status(
        &self,
        request: proto::DebugStatusRequest,
    ) -> Result<proto::DebugStatusResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_status(client.service.get_debug_status(request)),
            Self::Ipc(client) => client.debug_status(request),
            Self::Tcp(client) => client.debug_status(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_session_start(
        &self,
        request: proto::DebugSessionStartRequest,
    ) -> Result<proto::DebugSessionStartResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_debug_session_start(client.service.start_debug_session(request))
            }
            Self::Ipc(client) => client.debug_session_start(request),
            Self::Tcp(client) => client.debug_session_start(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_session_status(
        &self,
        request: proto::DebugSessionStatusRequest,
    ) -> Result<proto::DebugSessionStatusResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_debug_session_status(client.service.get_debug_session_status(request))
            }
            Self::Ipc(client) => client.debug_session_status(request),
            Self::Tcp(client) => client.debug_session_status(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_session_end(
        &self,
        request: proto::DebugSessionEndRequest,
    ) -> Result<proto::DebugSessionEndResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_debug_session_end(client.service.end_debug_session(request))
            }
            Self::Ipc(client) => client.debug_session_end(request),
            Self::Tcp(client) => client.debug_session_end(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_pause(
        &self,
        request: proto::DebugPauseRequest,
    ) -> Result<proto::DebugPauseResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_pause(client.service.pause_debug(request)),
            Self::Ipc(client) => client.debug_pause(request),
            Self::Tcp(client) => client.debug_pause(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_resume(
        &self,
        request: proto::DebugResumeRequest,
    ) -> Result<proto::DebugResumeResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_resume(client.service.resume_debug(request)),
            Self::Ipc(client) => client.debug_resume(request),
            Self::Tcp(client) => client.debug_resume(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_step(
        &self,
        request: proto::DebugStepRequest,
    ) -> Result<proto::DebugStepResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_step(client.service.step_debug(request)),
            Self::Ipc(client) => client.debug_step(request),
            Self::Tcp(client) => client.debug_step(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_wait(
        &self,
        request: proto::DebugWaitRequest,
    ) -> Result<proto::DebugWaitResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_wait(client.service.wait_debug(request)),
            Self::Ipc(client) => client.debug_wait(request),
            Self::Tcp(client) => client.debug_wait(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn breakpoint_list(
        &self,
        request: proto::DebugBreakpointListRequest,
    ) -> Result<proto::DebugBreakpointListResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_breakpoint_list(client.service.list_breakpoints(request)),
            Self::Ipc(client) => client.breakpoint_list(request),
            Self::Tcp(client) => client.breakpoint_list(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn breakpoint_add(
        &self,
        request: proto::DebugBreakpointAddRequest,
    ) -> Result<proto::DebugBreakpointAddResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_breakpoint_add(client.service.add_breakpoint(request)),
            Self::Ipc(client) => client.breakpoint_add(request),
            Self::Tcp(client) => client.breakpoint_add(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn breakpoint_remove(
        &self,
        request: proto::DebugBreakpointRemoveRequest,
    ) -> Result<proto::DebugBreakpointRemoveResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_breakpoint_remove(client.service.remove_breakpoint(request))
            }
            Self::Ipc(client) => client.breakpoint_remove(request),
            Self::Tcp(client) => client.breakpoint_remove(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn debug_events(
        &self,
        request: proto::DebugEventStreamRequest,
    ) -> Result<proto::DebugEventStreamResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_debug_events(client.service.get_debug_events(request)),
            Self::Ipc(client) => client.debug_events(request),
            Self::Tcp(client) => client.debug_events(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn load_fixture(
        &self,
        request: proto::FixtureLoadRequest,
    ) -> Result<proto::FixtureLoadResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_fixture(client.service.load_fixture(request)),
            Self::Ipc(client) => client.load_fixture(request),
            Self::Tcp(client) => client.load_fixture(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn record_fixture(
        &self,
        request: proto::RecordedFixtureRequest,
    ) -> Result<proto::RecordedFixtureResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_recorded_fixture(client.service.record_fixture(request)),
            Self::Ipc(client) => client.record_fixture(request),
            Self::Tcp(client) => client.record_fixture(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn screenshot(
        &self,
        request: proto::ScreenshotRequest,
    ) -> Result<proto::ScreenshotResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_screenshot(client.service.get_screenshot(request)),
            Self::Ipc(client) => client.screenshot(request),
            Self::Tcp(client) => client.screenshot(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_scene_tree(
        &self,
        request: proto::RuntimeSceneTreeRequest,
    ) -> Result<proto::RuntimeSceneTreeResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_runtime_scene_tree(client.service.get_runtime_scene_tree(request))
            }
            Self::Ipc(client) => client.runtime_scene_tree(request),
            Self::Tcp(client) => client.runtime_scene_tree(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_scene_node(
        &self,
        request: proto::RuntimeSceneNodeRequest,
    ) -> Result<proto::RuntimeSceneNodeResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_runtime_scene_node(client.service.get_runtime_scene_node(request))
            }
            Self::Ipc(client) => client.runtime_scene_node(request),
            Self::Tcp(client) => client.runtime_scene_node(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_scene_set_visible(
        &self,
        request: proto::RuntimeSceneSetVisibleRequest,
    ) -> Result<proto::RuntimeSceneSetVisibleResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_runtime_scene_set_visible(
                client.service.set_runtime_scene_node_visible(request),
            ),
            Self::Ipc(client) => client.runtime_scene_set_visible(request),
            Self::Tcp(client) => client.runtime_scene_set_visible(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_scene_hover(
        &self,
        request: proto::RuntimeSceneControlHoverRequest,
    ) -> Result<proto::RuntimeSceneControlHoverResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_runtime_scene_hover(client.service.hover_runtime_scene_control(request))
            }
            Self::Ipc(client) => client.runtime_scene_hover(request),
            Self::Tcp(client) => client.runtime_scene_hover(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_scene_unhover(
        &self,
        request: proto::RuntimeSceneControlUnhoverRequest,
    ) -> Result<proto::RuntimeSceneControlUnhoverResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_runtime_scene_unhover(client.service.unhover_runtime_scene_control(request))
            }
            Self::Ipc(client) => client.runtime_scene_unhover(request),
            Self::Tcp(client) => client.runtime_scene_unhover(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn runtime_transition_status(
        &self,
        request: proto::RuntimeTransitionStatusRequest,
    ) -> Result<proto::RuntimeTransitionStatusResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_runtime_transition_status(
                client.service.get_runtime_transition_status(request),
            ),
            Self::Ipc(client) => client.runtime_transition_status(request),
            Self::Tcp(client) => client.runtime_transition_status(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn presentation_resource_scenes(
        &self,
        request: proto::PresentationResourceSceneRequest,
    ) -> Result<proto::PresentationResourceSceneResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_presentation_resource_scene(
                client.service.inspect_presentation_resource_scenes(request),
            ),
            Self::Ipc(client) => client.presentation_resource_scenes(request),
            Self::Tcp(client) => client.presentation_resource_scenes(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn presentation_localization(
        &self,
        request: proto::PresentationLocalizationRequest,
    ) -> Result<proto::PresentationLocalizationResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_presentation_localization(
                client.service.inspect_presentation_localization(request),
            ),
            Self::Ipc(client) => client.presentation_localization(request),
            Self::Tcp(client) => client.presentation_localization(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn extract_asset(
        &self,
        request: proto::AssetExtractRequest,
    ) -> Result<proto::AssetExtractResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_asset_extract(client.service.extract_asset(request)),
            Self::Ipc(client) => client.extract_asset(request),
            Self::Tcp(client) => client.extract_asset(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn asset_catalog(
        &self,
        request: proto::AssetCatalogRequest,
    ) -> Result<proto::AssetCatalogResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_asset_catalog(client.service.get_asset_catalog(request)),
            Self::Ipc(client) => client.asset_catalog(request),
            Self::Tcp(client) => client.asset_catalog(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn explain_asset(
        &self,
        request: proto::AssetExplainRequest,
    ) -> Result<proto::AssetExplainResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_asset_explain(client.service.explain_asset(request)),
            Self::Ipc(client) => client.explain_asset(request),
            Self::Tcp(client) => client.explain_asset(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn close_game(
        &self,
        request: proto::GameCloseRequest,
    ) -> Result<proto::GameCloseResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_game_close(client.service.close_game(request)),
            Self::Ipc(client) => client.close_game(request),
            Self::Tcp(client) => client.close_game(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn mods(
        &self,
        request: proto::ModListRequest,
    ) -> Result<proto::ModListResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_mod_list(client.service.get_mods(request)),
            Self::Ipc(client) => client.mods(request),
            Self::Tcp(client) => client.mods(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn models(
        &self,
        request: proto::ModelCatalogRequest,
    ) -> Result<proto::ModelCatalogResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_models(client.service.get_models(request)),
            Self::Ipc(client) => client.models(request),
            Self::Tcp(client) => client.models(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn combat_preview(
        &self,
        request: proto::CombatPreviewRequest,
    ) -> Result<proto::CombatPreviewResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_combat_preview(client.service.get_combat_preview(request)),
            Self::Ipc(client) => client.combat_preview(request),
            Self::Tcp(client) => client.combat_preview(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn map_drawings(
        &self,
        request: proto::MapDrawingsRequest,
    ) -> Result<proto::MapDrawingsResponse, proto::BridgeError> {
        match self {
            // The mock transport has no map screen; return an empty (map-inactive) snapshot.
            Self::Mock(_) => Ok(proto::MapDrawingsResponse::default()),
            Self::Ipc(client) => client.map_drawings(request),
            Self::Tcp(client) => client.map_drawings(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn reference(
        &self,
        request: proto::ReferenceRequest,
    ) -> Result<proto::ReferenceResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_reference(client.service.get_reference(request)),
            Self::Ipc(client) => client.reference(request),
            Self::Tcp(client) => client.reference(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn capture_scenario(
        &self,
        request: proto::ScenarioCaptureRequest,
    ) -> Result<proto::ScenarioCaptureResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_scenario_capture(client.service.capture_scenario(request)),
            Self::Ipc(client) => client.capture_scenario(request),
            Self::Tcp(client) => client.capture_scenario(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn restore_scenario(
        &self,
        request: proto::ScenarioRestoreRequest,
    ) -> Result<proto::ScenarioRestoreResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_scenario_restore(client.service.restore_scenario(request)),
            Self::Ipc(client) => client.restore_scenario(request),
            Self::Tcp(client) => client.restore_scenario(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn capture_checkpoint(
        &self,
        request: proto::CheckpointCaptureRequest,
    ) -> Result<proto::CheckpointCaptureResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_checkpoint_capture(client.service.capture_checkpoint(request))
            }
            Self::Ipc(client) => client.capture_checkpoint(request),
            Self::Tcp(client) => client.capture_checkpoint(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn restore_checkpoint(
        &self,
        request: proto::CheckpointRestoreRequest,
    ) -> Result<proto::CheckpointRestoreResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_checkpoint_restore(client.service.restore_checkpoint(request))
            }
            Self::Ipc(client) => client.restore_checkpoint(request),
            Self::Tcp(client) => client.restore_checkpoint(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn hot_reload_status(
        &self,
        request: proto::HotReloadStatusRequest,
    ) -> Result<proto::HotReloadStatusResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => {
                unwrap_hot_reload_status(client.service.get_hot_reload_status(request))
            }
            Self::Ipc(client) => client.hot_reload_status(request),
            Self::Tcp(client) => client.hot_reload_status(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }

    fn hot_reload(
        &self,
        request: proto::HotReloadRequest,
    ) -> Result<proto::HotReloadResponse, proto::BridgeError> {
        match self {
            Self::Mock(client) => unwrap_hot_reload(client.service.request_hot_reload(request)),
            Self::Ipc(client) => client.hot_reload(request),
            Self::Tcp(client) => client.hot_reload(request),
            Self::Invalid(client) => Err(client.transport_error()),
            Self::Unavailable(client) => Err(client.transport_error()),
        }
    }
}

#[derive(Debug, Clone)]
struct MockBridgeClient {
    service: StubBridgeService,
}

impl MockBridgeClient {
    fn new(scenario: MockScenario) -> Self {
        Self {
            service: StubBridgeService::new(scenario),
        }
    }

    fn watch_state<F>(
        &self,
        request: proto::StateWatchRequest,
        mut on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::StateWatchEvent) -> Result<bool, proto::BridgeError>,
    {
        let state_request = request.state.unwrap_or_default();
        let response = unwrap_state(self.service.get_state(state_request))?;
        let event = proto::StateWatchEvent {
            r#type: proto::StateWatchEventType::Initial as i32,
            sequence: 1,
            observed_at_utc: current_unix_observed_at_utc(),
            fingerprint: String::new(),
            semantic_revision: 1,
            payload: Some(proto::state_watch_event::Payload::State(response)),
        };
        let _ = on_event(event)?;
        Ok(())
    }

    fn watch_combat_events<F>(
        &self,
        _request: proto::WatchCombatEventsRequest,
        _on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::CombatEvent) -> Result<bool, proto::BridgeError>,
    {
        // The stub bridge has no live combat; the stream completes immediately.
        Ok(())
    }
}

#[derive(Debug, Clone)]
struct IpcBridgeClient {
    endpoint: String,
    rpc_timeout_ms: Option<u64>,
}

impl IpcBridgeClient {
    fn new(endpoint: String, rpc_timeout_ms: Option<u64>) -> Self {
        Self {
            endpoint,
            rpc_timeout_ms,
        }
    }

    fn handshake(
        &self,
        request: proto::HandshakeRequest,
    ) -> Result<proto::HandshakeResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::Handshake, request, unwrap_handshake)
    }

    fn state(
        &self,
        request: proto::StateRequest,
    ) -> Result<proto::StateResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetState, request, unwrap_state)
    }

    fn watch_state<F>(
        &self,
        request: proto::StateWatchRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::StateWatchEvent) -> Result<bool, proto::BridgeError>,
    {
        let mut stream = self.connect_watch()?;
        call_standalone_bridge_stream(
            &mut stream,
            ipc_protocol::Method::WatchState,
            request,
            on_event,
            &self.endpoint,
            self.rpc_timeout_ms,
            live_ipc_protocol_error,
        )
    }

    fn watch_combat_events<F>(
        &self,
        request: proto::WatchCombatEventsRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::CombatEvent) -> Result<bool, proto::BridgeError>,
    {
        let mut stream = self.connect_watch()?;
        call_standalone_bridge_stream(
            &mut stream,
            ipc_protocol::Method::WatchCombatEvents,
            request,
            on_event,
            &self.endpoint,
            self.rpc_timeout_ms,
            live_ipc_protocol_error,
        )
    }

    fn execute_action(
        &self,
        request: proto::ActionRequest,
    ) -> Result<proto::ActionResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::ExecuteAction, request, unwrap_action)
    }

    fn execute_console_command(
        &self,
        request: proto::ConsoleCommandRequest,
    ) -> Result<proto::ConsoleCommandResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExecuteConsoleCommand,
            request,
            unwrap_console_command,
        )
    }

    fn logs(&self, request: proto::LogsRequest) -> Result<proto::LogsResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetLogs, request, unwrap_logs)
    }

    fn debug_status(
        &self,
        request: proto::DebugStatusRequest,
    ) -> Result<proto::DebugStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugStatus,
            request,
            unwrap_debug_status,
        )
    }

    fn debug_session_start(
        &self,
        request: proto::DebugSessionStartRequest,
    ) -> Result<proto::DebugSessionStartResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::StartDebugSession,
            request,
            unwrap_debug_session_start,
        )
    }

    fn debug_session_status(
        &self,
        request: proto::DebugSessionStatusRequest,
    ) -> Result<proto::DebugSessionStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugSessionStatus,
            request,
            unwrap_debug_session_status,
        )
    }

    fn debug_session_end(
        &self,
        request: proto::DebugSessionEndRequest,
    ) -> Result<proto::DebugSessionEndResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::EndDebugSession,
            request,
            unwrap_debug_session_end,
        )
    }

    fn debug_pause(
        &self,
        request: proto::DebugPauseRequest,
    ) -> Result<proto::DebugPauseResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::PauseDebug,
            request,
            unwrap_debug_pause,
        )
    }

    fn debug_resume(
        &self,
        request: proto::DebugResumeRequest,
    ) -> Result<proto::DebugResumeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ResumeDebug,
            request,
            unwrap_debug_resume,
        )
    }

    fn debug_step(
        &self,
        request: proto::DebugStepRequest,
    ) -> Result<proto::DebugStepResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::StepDebug, request, unwrap_debug_step)
    }

    fn debug_wait(
        &self,
        request: proto::DebugWaitRequest,
    ) -> Result<proto::DebugWaitResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::WaitDebug, request, unwrap_debug_wait)
    }

    fn breakpoint_list(
        &self,
        request: proto::DebugBreakpointListRequest,
    ) -> Result<proto::DebugBreakpointListResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ListBreakpoints,
            request,
            unwrap_breakpoint_list,
        )
    }

    fn breakpoint_add(
        &self,
        request: proto::DebugBreakpointAddRequest,
    ) -> Result<proto::DebugBreakpointAddResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::AddBreakpoint,
            request,
            unwrap_breakpoint_add,
        )
    }

    fn breakpoint_remove(
        &self,
        request: proto::DebugBreakpointRemoveRequest,
    ) -> Result<proto::DebugBreakpointRemoveResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RemoveBreakpoint,
            request,
            unwrap_breakpoint_remove,
        )
    }

    fn debug_events(
        &self,
        request: proto::DebugEventStreamRequest,
    ) -> Result<proto::DebugEventStreamResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugEvents,
            request,
            unwrap_debug_events,
        )
    }

    fn load_fixture(
        &self,
        request: proto::FixtureLoadRequest,
    ) -> Result<proto::FixtureLoadResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::LoadFixture, request, unwrap_fixture)
    }

    fn record_fixture(
        &self,
        request: proto::RecordedFixtureRequest,
    ) -> Result<proto::RecordedFixtureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RecordFixture,
            request,
            unwrap_recorded_fixture,
        )
    }

    fn screenshot(
        &self,
        request: proto::ScreenshotRequest,
    ) -> Result<proto::ScreenshotResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetScreenshot,
            request,
            unwrap_screenshot,
        )
    }

    fn runtime_scene_tree(
        &self,
        request: proto::RuntimeSceneTreeRequest,
    ) -> Result<proto::RuntimeSceneTreeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeSceneTree,
            request,
            unwrap_runtime_scene_tree,
        )
    }

    fn runtime_scene_node(
        &self,
        request: proto::RuntimeSceneNodeRequest,
    ) -> Result<proto::RuntimeSceneNodeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeSceneNode,
            request,
            unwrap_runtime_scene_node,
        )
    }

    fn runtime_scene_set_visible(
        &self,
        request: proto::RuntimeSceneSetVisibleRequest,
    ) -> Result<proto::RuntimeSceneSetVisibleResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::SetRuntimeSceneNodeVisible,
            request,
            unwrap_runtime_scene_set_visible,
        )
    }

    fn runtime_scene_hover(
        &self,
        request: proto::RuntimeSceneControlHoverRequest,
    ) -> Result<proto::RuntimeSceneControlHoverResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::HoverRuntimeSceneControl,
            request,
            unwrap_runtime_scene_hover,
        )
    }

    fn runtime_scene_unhover(
        &self,
        request: proto::RuntimeSceneControlUnhoverRequest,
    ) -> Result<proto::RuntimeSceneControlUnhoverResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::UnhoverRuntimeSceneControl,
            request,
            unwrap_runtime_scene_unhover,
        )
    }

    fn runtime_transition_status(
        &self,
        request: proto::RuntimeTransitionStatusRequest,
    ) -> Result<proto::RuntimeTransitionStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeTransitionStatus,
            request,
            unwrap_runtime_transition_status,
        )
    }

    fn presentation_resource_scenes(
        &self,
        request: proto::PresentationResourceSceneRequest,
    ) -> Result<proto::PresentationResourceSceneResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::InspectPresentationResourceScenes,
            request,
            unwrap_presentation_resource_scene,
        )
    }

    fn presentation_localization(
        &self,
        request: proto::PresentationLocalizationRequest,
    ) -> Result<proto::PresentationLocalizationResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::InspectPresentationLocalization,
            request,
            unwrap_presentation_localization,
        )
    }

    fn extract_asset(
        &self,
        request: proto::AssetExtractRequest,
    ) -> Result<proto::AssetExtractResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExtractAsset,
            request,
            unwrap_asset_extract,
        )
    }

    fn asset_catalog(
        &self,
        request: proto::AssetCatalogRequest,
    ) -> Result<proto::AssetCatalogResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetAssetCatalog,
            request,
            unwrap_asset_catalog,
        )
    }

    fn explain_asset(
        &self,
        request: proto::AssetExplainRequest,
    ) -> Result<proto::AssetExplainResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExplainAsset,
            request,
            unwrap_asset_explain,
        )
    }

    fn close_game(
        &self,
        request: proto::GameCloseRequest,
    ) -> Result<proto::GameCloseResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::CloseGame, request, unwrap_game_close)
    }

    fn mods(
        &self,
        request: proto::ModListRequest,
    ) -> Result<proto::ModListResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetMods, request, unwrap_mod_list)
    }

    fn models(
        &self,
        request: proto::ModelCatalogRequest,
    ) -> Result<proto::ModelCatalogResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetModels, request, unwrap_models)
    }

    fn combat_preview(
        &self,
        request: proto::CombatPreviewRequest,
    ) -> Result<proto::CombatPreviewResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetCombatPreview,
            request,
            unwrap_combat_preview,
        )
    }

    fn map_drawings(
        &self,
        request: proto::MapDrawingsRequest,
    ) -> Result<proto::MapDrawingsResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetMapDrawings,
            request,
            unwrap_map_drawings,
        )
    }

    fn reference(
        &self,
        request: proto::ReferenceRequest,
    ) -> Result<proto::ReferenceResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetReference,
            request,
            unwrap_reference,
        )
    }

    fn capture_scenario(
        &self,
        request: proto::ScenarioCaptureRequest,
    ) -> Result<proto::ScenarioCaptureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::CaptureScenario,
            request,
            unwrap_scenario_capture,
        )
    }

    fn restore_scenario(
        &self,
        request: proto::ScenarioRestoreRequest,
    ) -> Result<proto::ScenarioRestoreResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RestoreScenario,
            request,
            unwrap_scenario_restore,
        )
    }

    fn capture_checkpoint(
        &self,
        request: proto::CheckpointCaptureRequest,
    ) -> Result<proto::CheckpointCaptureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::CaptureCheckpoint,
            request,
            unwrap_checkpoint_capture,
        )
    }

    fn restore_checkpoint(
        &self,
        request: proto::CheckpointRestoreRequest,
    ) -> Result<proto::CheckpointRestoreResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RestoreCheckpoint,
            request,
            unwrap_checkpoint_restore,
        )
    }

    fn hot_reload_status(
        &self,
        request: proto::HotReloadStatusRequest,
    ) -> Result<proto::HotReloadStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetHotReloadStatus,
            request,
            unwrap_hot_reload_status,
        )
    }

    fn hot_reload(
        &self,
        request: proto::HotReloadRequest,
    ) -> Result<proto::HotReloadResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RequestHotReload,
            request,
            unwrap_hot_reload,
        )
    }

    fn call<Request, ResultEnvelope, Response>(
        &self,
        method: ipc_protocol::Method,
        request: Request,
        unwrap: fn(ResultEnvelope) -> Result<Response, proto::BridgeError>,
    ) -> Result<Response, proto::BridgeError>
    where
        Request: Message,
        ResultEnvelope: Message + Default,
    {
        let mut stream = self.connect()?;
        call_standalone_bridge(
            &mut stream,
            method,
            request,
            unwrap,
            &self.endpoint,
            self.rpc_timeout_ms,
            live_ipc_protocol_error,
        )
    }

    #[cfg(unix)]
    fn connect(&self) -> Result<std::os::unix::net::UnixStream, proto::BridgeError> {
        if !Path::new(&self.endpoint).exists() {
            return Err(live_ipc_missing_socket_error(&self.endpoint));
        }

        let stream = std::os::unix::net::UnixStream::connect(&self.endpoint)
            .map_err(|source| live_ipc_connection_error(&self.endpoint, &source.to_string()))?;
        if let Some(timeout_ms) = self.rpc_timeout_ms {
            let timeout = Some(Duration::from_millis(timeout_ms));
            stream.set_read_timeout(timeout).map_err(|source| {
                live_ipc_protocol_error(
                    &self.endpoint,
                    &format!("failed to configure RPC read timeout: {source}"),
                )
            })?;
            stream.set_write_timeout(timeout).map_err(|source| {
                live_ipc_protocol_error(
                    &self.endpoint,
                    &format!("failed to configure RPC write timeout: {source}"),
                )
            })?;
        }
        Ok(stream)
    }

    #[cfg(unix)]
    fn connect_watch(&self) -> Result<std::os::unix::net::UnixStream, proto::BridgeError> {
        if !Path::new(&self.endpoint).exists() {
            return Err(live_ipc_missing_socket_error(&self.endpoint));
        }

        let stream = std::os::unix::net::UnixStream::connect(&self.endpoint)
            .map_err(|source| live_ipc_connection_error(&self.endpoint, &source.to_string()))?;
        if let Some(timeout_ms) = self.rpc_timeout_ms {
            let timeout = Some(Duration::from_millis(timeout_ms));
            stream.set_write_timeout(timeout).map_err(|source| {
                live_ipc_protocol_error(
                    &self.endpoint,
                    &format!("failed to configure RPC write timeout: {source}"),
                )
            })?;
        }
        Ok(stream)
    }

    /// Open the bridge's named pipe.
    ///
    /// The host serves one `NamedPipeServerStream` per accept iteration, so a
    /// connect landing between accepts legitimately sees `ERROR_PIPE_BUSY`
    /// (231) and must retry rather than report the bridge as unreachable. A
    /// missing pipe maps onto the same "bridge is not listening" error the Unix
    /// socket path raises, so diagnostics read the same on both platforms.
    #[cfg(windows)]
    fn connect(&self) -> Result<std::fs::File, proto::BridgeError> {
        const ERROR_PIPE_BUSY: i32 = 231;
        let pipe_path = format!(r"\\.\pipe\{}", self.endpoint);
        let deadline = std::time::Instant::now()
            + Duration::from_millis(self.rpc_timeout_ms.unwrap_or(1_000).min(5_000));

        loop {
            let source = match std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(&pipe_path)
            {
                Ok(stream) => return Ok(stream),
                Err(source) => source,
            };

            if source.raw_os_error() == Some(ERROR_PIPE_BUSY)
                && std::time::Instant::now() < deadline
            {
                std::thread::sleep(Duration::from_millis(20));
                continue;
            }

            return Err(match source.kind() {
                std::io::ErrorKind::NotFound => live_ipc_missing_pipe_error(&self.endpoint),
                _ => live_transport_connection_error(&pipe_path, &source.to_string()),
            });
        }
    }

    #[cfg(windows)]
    fn connect_watch(&self) -> Result<std::fs::File, proto::BridgeError> {
        self.connect()
    }
}

#[derive(Debug, Clone)]
struct TcpBridgeClient {
    address: String,
    rpc_timeout_ms: Option<u64>,
}

impl TcpBridgeClient {
    fn new(address: String, rpc_timeout_ms: Option<u64>) -> Self {
        Self {
            address,
            rpc_timeout_ms,
        }
    }

    fn handshake(
        &self,
        request: proto::HandshakeRequest,
    ) -> Result<proto::HandshakeResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::Handshake, request, unwrap_handshake)
    }

    fn state(
        &self,
        request: proto::StateRequest,
    ) -> Result<proto::StateResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetState, request, unwrap_state)
    }

    fn watch_state<F>(
        &self,
        request: proto::StateWatchRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::StateWatchEvent) -> Result<bool, proto::BridgeError>,
    {
        let mut stream = self.connect_watch()?;
        call_standalone_bridge_stream(
            &mut stream,
            ipc_protocol::Method::WatchState,
            request,
            on_event,
            &self.address,
            self.rpc_timeout_ms,
            live_tcp_protocol_error,
        )
    }

    fn watch_combat_events<F>(
        &self,
        request: proto::WatchCombatEventsRequest,
        on_event: F,
    ) -> Result<(), proto::BridgeError>
    where
        F: FnMut(proto::CombatEvent) -> Result<bool, proto::BridgeError>,
    {
        let mut stream = self.connect_watch()?;
        call_standalone_bridge_stream(
            &mut stream,
            ipc_protocol::Method::WatchCombatEvents,
            request,
            on_event,
            &self.address,
            self.rpc_timeout_ms,
            live_tcp_protocol_error,
        )
    }

    fn execute_action(
        &self,
        request: proto::ActionRequest,
    ) -> Result<proto::ActionResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::ExecuteAction, request, unwrap_action)
    }

    fn execute_console_command(
        &self,
        request: proto::ConsoleCommandRequest,
    ) -> Result<proto::ConsoleCommandResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExecuteConsoleCommand,
            request,
            unwrap_console_command,
        )
    }

    fn logs(&self, request: proto::LogsRequest) -> Result<proto::LogsResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetLogs, request, unwrap_logs)
    }

    fn debug_status(
        &self,
        request: proto::DebugStatusRequest,
    ) -> Result<proto::DebugStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugStatus,
            request,
            unwrap_debug_status,
        )
    }

    fn debug_session_start(
        &self,
        request: proto::DebugSessionStartRequest,
    ) -> Result<proto::DebugSessionStartResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::StartDebugSession,
            request,
            unwrap_debug_session_start,
        )
    }

    fn debug_session_status(
        &self,
        request: proto::DebugSessionStatusRequest,
    ) -> Result<proto::DebugSessionStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugSessionStatus,
            request,
            unwrap_debug_session_status,
        )
    }

    fn debug_session_end(
        &self,
        request: proto::DebugSessionEndRequest,
    ) -> Result<proto::DebugSessionEndResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::EndDebugSession,
            request,
            unwrap_debug_session_end,
        )
    }

    fn debug_pause(
        &self,
        request: proto::DebugPauseRequest,
    ) -> Result<proto::DebugPauseResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::PauseDebug,
            request,
            unwrap_debug_pause,
        )
    }

    fn debug_resume(
        &self,
        request: proto::DebugResumeRequest,
    ) -> Result<proto::DebugResumeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ResumeDebug,
            request,
            unwrap_debug_resume,
        )
    }

    fn debug_step(
        &self,
        request: proto::DebugStepRequest,
    ) -> Result<proto::DebugStepResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::StepDebug, request, unwrap_debug_step)
    }

    fn debug_wait(
        &self,
        request: proto::DebugWaitRequest,
    ) -> Result<proto::DebugWaitResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::WaitDebug, request, unwrap_debug_wait)
    }

    fn breakpoint_list(
        &self,
        request: proto::DebugBreakpointListRequest,
    ) -> Result<proto::DebugBreakpointListResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ListBreakpoints,
            request,
            unwrap_breakpoint_list,
        )
    }

    fn breakpoint_add(
        &self,
        request: proto::DebugBreakpointAddRequest,
    ) -> Result<proto::DebugBreakpointAddResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::AddBreakpoint,
            request,
            unwrap_breakpoint_add,
        )
    }

    fn breakpoint_remove(
        &self,
        request: proto::DebugBreakpointRemoveRequest,
    ) -> Result<proto::DebugBreakpointRemoveResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RemoveBreakpoint,
            request,
            unwrap_breakpoint_remove,
        )
    }

    fn debug_events(
        &self,
        request: proto::DebugEventStreamRequest,
    ) -> Result<proto::DebugEventStreamResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetDebugEvents,
            request,
            unwrap_debug_events,
        )
    }

    fn load_fixture(
        &self,
        request: proto::FixtureLoadRequest,
    ) -> Result<proto::FixtureLoadResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::LoadFixture, request, unwrap_fixture)
    }

    fn record_fixture(
        &self,
        request: proto::RecordedFixtureRequest,
    ) -> Result<proto::RecordedFixtureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RecordFixture,
            request,
            unwrap_recorded_fixture,
        )
    }

    fn screenshot(
        &self,
        request: proto::ScreenshotRequest,
    ) -> Result<proto::ScreenshotResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetScreenshot,
            request,
            unwrap_screenshot,
        )
    }

    fn runtime_scene_tree(
        &self,
        request: proto::RuntimeSceneTreeRequest,
    ) -> Result<proto::RuntimeSceneTreeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeSceneTree,
            request,
            unwrap_runtime_scene_tree,
        )
    }

    fn runtime_scene_node(
        &self,
        request: proto::RuntimeSceneNodeRequest,
    ) -> Result<proto::RuntimeSceneNodeResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeSceneNode,
            request,
            unwrap_runtime_scene_node,
        )
    }

    fn runtime_scene_set_visible(
        &self,
        request: proto::RuntimeSceneSetVisibleRequest,
    ) -> Result<proto::RuntimeSceneSetVisibleResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::SetRuntimeSceneNodeVisible,
            request,
            unwrap_runtime_scene_set_visible,
        )
    }

    fn runtime_scene_hover(
        &self,
        request: proto::RuntimeSceneControlHoverRequest,
    ) -> Result<proto::RuntimeSceneControlHoverResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::HoverRuntimeSceneControl,
            request,
            unwrap_runtime_scene_hover,
        )
    }

    fn runtime_scene_unhover(
        &self,
        request: proto::RuntimeSceneControlUnhoverRequest,
    ) -> Result<proto::RuntimeSceneControlUnhoverResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::UnhoverRuntimeSceneControl,
            request,
            unwrap_runtime_scene_unhover,
        )
    }

    fn runtime_transition_status(
        &self,
        request: proto::RuntimeTransitionStatusRequest,
    ) -> Result<proto::RuntimeTransitionStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetRuntimeTransitionStatus,
            request,
            unwrap_runtime_transition_status,
        )
    }

    fn presentation_resource_scenes(
        &self,
        request: proto::PresentationResourceSceneRequest,
    ) -> Result<proto::PresentationResourceSceneResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::InspectPresentationResourceScenes,
            request,
            unwrap_presentation_resource_scene,
        )
    }

    fn presentation_localization(
        &self,
        request: proto::PresentationLocalizationRequest,
    ) -> Result<proto::PresentationLocalizationResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::InspectPresentationLocalization,
            request,
            unwrap_presentation_localization,
        )
    }

    fn extract_asset(
        &self,
        request: proto::AssetExtractRequest,
    ) -> Result<proto::AssetExtractResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExtractAsset,
            request,
            unwrap_asset_extract,
        )
    }

    fn asset_catalog(
        &self,
        request: proto::AssetCatalogRequest,
    ) -> Result<proto::AssetCatalogResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetAssetCatalog,
            request,
            unwrap_asset_catalog,
        )
    }

    fn explain_asset(
        &self,
        request: proto::AssetExplainRequest,
    ) -> Result<proto::AssetExplainResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::ExplainAsset,
            request,
            unwrap_asset_explain,
        )
    }

    fn close_game(
        &self,
        request: proto::GameCloseRequest,
    ) -> Result<proto::GameCloseResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::CloseGame, request, unwrap_game_close)
    }

    fn mods(
        &self,
        request: proto::ModListRequest,
    ) -> Result<proto::ModListResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetMods, request, unwrap_mod_list)
    }

    fn models(
        &self,
        request: proto::ModelCatalogRequest,
    ) -> Result<proto::ModelCatalogResponse, proto::BridgeError> {
        self.call(ipc_protocol::Method::GetModels, request, unwrap_models)
    }

    fn combat_preview(
        &self,
        request: proto::CombatPreviewRequest,
    ) -> Result<proto::CombatPreviewResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetCombatPreview,
            request,
            unwrap_combat_preview,
        )
    }

    fn map_drawings(
        &self,
        request: proto::MapDrawingsRequest,
    ) -> Result<proto::MapDrawingsResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetMapDrawings,
            request,
            unwrap_map_drawings,
        )
    }

    fn reference(
        &self,
        request: proto::ReferenceRequest,
    ) -> Result<proto::ReferenceResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetReference,
            request,
            unwrap_reference,
        )
    }

    fn capture_scenario(
        &self,
        request: proto::ScenarioCaptureRequest,
    ) -> Result<proto::ScenarioCaptureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::CaptureScenario,
            request,
            unwrap_scenario_capture,
        )
    }

    fn restore_scenario(
        &self,
        request: proto::ScenarioRestoreRequest,
    ) -> Result<proto::ScenarioRestoreResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RestoreScenario,
            request,
            unwrap_scenario_restore,
        )
    }

    fn capture_checkpoint(
        &self,
        request: proto::CheckpointCaptureRequest,
    ) -> Result<proto::CheckpointCaptureResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::CaptureCheckpoint,
            request,
            unwrap_checkpoint_capture,
        )
    }

    fn restore_checkpoint(
        &self,
        request: proto::CheckpointRestoreRequest,
    ) -> Result<proto::CheckpointRestoreResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RestoreCheckpoint,
            request,
            unwrap_checkpoint_restore,
        )
    }

    fn hot_reload_status(
        &self,
        request: proto::HotReloadStatusRequest,
    ) -> Result<proto::HotReloadStatusResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::GetHotReloadStatus,
            request,
            unwrap_hot_reload_status,
        )
    }

    fn hot_reload(
        &self,
        request: proto::HotReloadRequest,
    ) -> Result<proto::HotReloadResponse, proto::BridgeError> {
        self.call(
            ipc_protocol::Method::RequestHotReload,
            request,
            unwrap_hot_reload,
        )
    }

    fn call<Request, ResultEnvelope, Response>(
        &self,
        method: ipc_protocol::Method,
        request: Request,
        unwrap: fn(ResultEnvelope) -> Result<Response, proto::BridgeError>,
    ) -> Result<Response, proto::BridgeError>
    where
        Request: Message,
        ResultEnvelope: Message + Default,
    {
        let mut stream = self.connect()?;
        call_standalone_bridge(
            &mut stream,
            method,
            request,
            unwrap,
            &self.address,
            self.rpc_timeout_ms,
            live_tcp_protocol_error,
        )
    }

    fn connect(&self) -> Result<TcpStream, proto::BridgeError> {
        let stream = TcpStream::connect(&self.address).map_err(|source| {
            live_transport_connection_error(&self.address, &source.to_string())
        })?;
        if let Some(timeout_ms) = self.rpc_timeout_ms {
            let timeout = Some(Duration::from_millis(timeout_ms));
            stream.set_read_timeout(timeout).map_err(|source| {
                live_tcp_protocol_error(
                    &self.address,
                    &format!("failed to configure RPC read timeout: {source}"),
                )
            })?;
            stream.set_write_timeout(timeout).map_err(|source| {
                live_tcp_protocol_error(
                    &self.address,
                    &format!("failed to configure RPC write timeout: {source}"),
                )
            })?;
        }
        Ok(stream)
    }

    fn connect_watch(&self) -> Result<TcpStream, proto::BridgeError> {
        let stream = TcpStream::connect(&self.address).map_err(|source| {
            live_transport_connection_error(&self.address, &source.to_string())
        })?;
        if let Some(timeout_ms) = self.rpc_timeout_ms {
            stream
                .set_write_timeout(Some(Duration::from_millis(timeout_ms)))
                .map_err(|source| {
                    live_tcp_protocol_error(
                        &self.address,
                        &format!("failed to configure RPC write timeout: {source}"),
                    )
                })?;
        }
        Ok(stream)
    }
}

#[derive(Debug, Clone)]
struct InvalidBridgeClient {
    transport_kind: proto::TransportKind,
    endpoint: String,
    note: String,
}

impl InvalidBridgeClient {
    fn new(
        transport_kind: proto::TransportKind,
        endpoint: impl Into<String>,
        note: impl Into<String>,
    ) -> Self {
        Self {
            transport_kind,
            endpoint: endpoint.into(),
            note: note.into(),
        }
    }

    fn transport_error(&self) -> proto::BridgeError {
        transport_misconfigured_error(self.transport_kind, &self.endpoint, &self.note)
    }
}
fn resolve_ipc_bridge_client(config: &TransportConfig) -> BridgeClientKind {
    if config.ipc_path.is_some() && config.pipe_name.is_some() {
        return BridgeClientKind::Invalid(InvalidBridgeClient::new(
            proto::TransportKind::Ipc,
            format!(
                "socket:{}|pipe:{}",
                config.ipc_path.as_deref().unwrap_or_default(),
                config.pipe_name.as_deref().unwrap_or_default()
            ),
            "Configure either transport.ipcPath or transport.pipeName for transport.kind=ipc, not both.",
        ));
    }

    #[cfg(unix)]
    {
        if let Some(pipe_name) = &config.pipe_name {
            return BridgeClientKind::Invalid(InvalidBridgeClient::new(
                proto::TransportKind::Ipc,
                format!("pipe:{pipe_name}"),
                "transport.pipeName is Windows-only; use transport.ipcPath on Unix-like platforms.",
            ));
        }

        BridgeClientKind::Ipc(IpcBridgeClient::new(
            config.ipc_path.clone().unwrap_or_else(default_ipc_path),
            config.rpc_timeout_ms,
        ))
    }

    #[cfg(windows)]
    {
        if let Some(ipc_path) = &config.ipc_path {
            return BridgeClientKind::Invalid(InvalidBridgeClient::new(
                proto::TransportKind::Ipc,
                ipc_path,
                "transport.ipcPath is not valid on Windows; use transport.pipeName for local IPC.",
            ));
        }

        return BridgeClientKind::Ipc(IpcBridgeClient::new(
            config.pipe_name.clone().unwrap_or_else(default_pipe_name),
            config.rpc_timeout_ms,
        ));
    }

    #[cfg(not(any(unix, windows)))]
    {
        BridgeClientKind::Unavailable(UnavailableBridgeClient::new(
            proto::TransportKind::Ipc,
            "platform-default-ipc".to_string(),
            "IPC transport is not supported on this platform.",
        ))
    }
}

fn resolve_tcp_bridge_client(config: &TransportConfig) -> BridgeClientKind {
    let address = config
        .tcp_address
        .clone()
        .unwrap_or_else(default_tcp_address);
    match validate_loopback_tcp_address(&address) {
        Ok(_) => BridgeClientKind::Tcp(TcpBridgeClient::new(address, config.rpc_timeout_ms)),
        Err(note) => BridgeClientKind::Invalid(InvalidBridgeClient::new(
            proto::TransportKind::Tcp,
            address,
            note,
        )),
    }
}

fn validate_loopback_tcp_address(address: &str) -> Result<Vec<SocketAddr>, String> {
    let resolved = address
        .to_socket_addrs()
        .map_err(|source| {
            format!("Resolve the address as host:port under loopback only. Parse error: {source}")
        })?
        .collect::<Vec<_>>();
    if resolved.is_empty() {
        return Err("Resolve the address to at least one local loopback socket.".to_string());
    }

    if resolved
        .iter()
        .any(|candidate| !candidate.ip().is_loopback())
    {
        return Err(
            "TCP hosting is local-only in this spec; use a loopback address such as 127.0.0.1:51173 or [::1]:51173."
                .to_string(),
        );
    }

    Ok(resolved)
}

fn call_standalone_bridge<Request, ResultEnvelope, Response, Stream>(
    stream: &mut Stream,
    method: ipc_protocol::Method,
    request: Request,
    unwrap: fn(ResultEnvelope) -> Result<Response, proto::BridgeError>,
    endpoint: &str,
    rpc_timeout_ms: Option<u64>,
    protocol_error: fn(&str, &str) -> proto::BridgeError,
) -> Result<Response, proto::BridgeError>
where
    Request: Message,
    ResultEnvelope: Message + Default,
    Stream: Read + Write,
{
    ipc_protocol::write_request_frame(stream, method, &request).map_err(|source| {
        if is_timeout_error(&source) {
            return live_rpc_timeout_error(endpoint, method.name(), rpc_timeout_ms);
        }
        protocol_error(
            endpoint,
            &format!("failed to write the standalone transport request: {source}"),
        )
    })?;

    let frame = ipc_protocol::read_response_frame(stream).map_err(|source| {
        if is_timeout_error(&source) {
            return live_rpc_timeout_error(endpoint, method.name(), rpc_timeout_ms);
        }
        protocol_error(
            endpoint,
            &format!("failed to read the standalone transport response: {source}"),
        )
    })?;

    match frame.status {
        ipc_protocol::ResponseStatus::Success => {
            let result = ResultEnvelope::decode(frame.payload.as_slice()).map_err(|source| {
                protocol_error(
                    endpoint,
                    &format!(
                        "the standalone transport response could not be decoded as protobuf: {source}"
                    ),
                )
            })?;
            unwrap(result)
        }
        ipc_protocol::ResponseStatus::ProtocolError => Err(protocol_error(
            endpoint,
            &String::from_utf8_lossy(&frame.payload),
        )),
    }
}

fn call_standalone_bridge_stream<Request, Stream, Event, F>(
    stream: &mut Stream,
    method: ipc_protocol::Method,
    request: Request,
    mut on_event: F,
    endpoint: &str,
    rpc_timeout_ms: Option<u64>,
    protocol_error: fn(&str, &str) -> proto::BridgeError,
) -> Result<(), proto::BridgeError>
where
    Request: Message,
    Stream: Read + Write,
    Event: Message + Default,
    F: FnMut(Event) -> Result<bool, proto::BridgeError>,
{
    ipc_protocol::write_request_frame(stream, method, &request).map_err(|source| {
        if is_timeout_error(&source) {
            return live_rpc_timeout_error(endpoint, method.name(), rpc_timeout_ms);
        }
        protocol_error(
            endpoint,
            &format!("failed to write the standalone transport request: {source}"),
        )
    })?;

    loop {
        let frame = match ipc_protocol::read_response_frame(stream) {
            Ok(frame) => frame,
            Err(source) if source.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(()),
            Err(source) => {
                if is_timeout_error(&source) {
                    return Err(live_rpc_timeout_error(
                        endpoint,
                        method.name(),
                        rpc_timeout_ms,
                    ));
                }
                return Err(protocol_error(
                    endpoint,
                    &format!("failed to read the standalone transport stream response: {source}"),
                ));
            }
        };

        match frame.status {
            ipc_protocol::ResponseStatus::Success => {
                let event =
                    Event::decode(frame.payload.as_slice()).map_err(|source| {
                        protocol_error(
                            endpoint,
                            &format!(
                                "the standalone transport stream response could not be decoded as protobuf: {source}"
                            ),
                        )
                    })?;
                if !on_event(event)? {
                    return Ok(());
                }
            }
            ipc_protocol::ResponseStatus::ProtocolError => {
                return Err(protocol_error(
                    endpoint,
                    &String::from_utf8_lossy(&frame.payload),
                ));
            }
        }
    }
}

fn current_unix_observed_at_utc() -> String {
    let elapsed = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}.{:03}Z", elapsed.as_secs(), elapsed.subsec_millis())
}

fn is_timeout_error(source: &std::io::Error) -> bool {
    matches!(
        source.kind(),
        std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
    )
}
