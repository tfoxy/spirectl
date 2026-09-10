#[cfg(unix)]
fn write_fake_presentation_project(root: &Path) -> PathBuf {
    let project = root.join("WatchProvider.csproj");
    fs::write(
        &project,
        r#"<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"#,
    )
    .expect("write project");
    fs::write(root.join("Provider.cs"), "public class Provider {}\n").expect("write source");
    project
}

#[cfg(unix)]
fn write_fake_dotnet(root: &Path, mode: &str) -> PathBuf {
    let bin = root.join("fake-bin");
    fs::create_dir_all(&bin).expect("fake bin");
    let script = bin.join("dotnet");
    let artifact = root.join("bin/Debug/net9.0/WatchProvider.dll");
    fs::write(
        &script,
        format!(
            r#"#!/usr/bin/env bash
set -euo pipefail
mode="{mode}"
artifact="{artifact}"
if [[ "$1" == "build" ]]; then
  if [[ "$mode" == "fail-build" ]]; then
    echo "compile failed" >&2
    exit 42
  fi
  mkdir -p "$(dirname "$artifact")"
  printf "provider" > "$artifact"
  echo "Build succeeded."
  exit 0
fi
if [[ "$1" == "msbuild" ]]; then
  echo "$artifact"
  exit 0
fi
echo "unexpected dotnet command: $*" >&2
exit 2
"#,
            mode = mode,
            artifact = artifact.display(),
        ),
    )
    .expect("write fake dotnet");
    let mut permissions = fs::metadata(&script).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&script, permissions).expect("chmod fake dotnet");
    bin
}

#[derive(Default)]
struct PresentationWatchBridgeService;

#[tonic::async_trait]
impl BridgeService for PresentationWatchBridgeService {
    impl_state_watch_unimplemented!();
    impl_runtime_scene_set_visible_unimplemented!();
    impl_unimplemented_bridge_rpc!(
        close_game,
        sts2::bridge::proto::GameCloseRequest,
        sts2::bridge::proto::GameCloseResult,
        "game close is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_models,
        sts2::bridge::proto::ModelCatalogRequest,
        sts2::bridge::proto::ModelCatalogResult,
        "models are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_reference,
        sts2::bridge::proto::ReferenceRequest,
        sts2::bridge::proto::ReferenceResult,
        "reference data is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        capture_scenario,
        sts2::bridge::proto::ScenarioCaptureRequest,
        sts2::bridge::proto::ScenarioCaptureResult,
        "scenario capture is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        restore_scenario,
        sts2::bridge::proto::ScenarioRestoreRequest,
        sts2::bridge::proto::ScenarioRestoreResult,
        "scenario restore is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        explain_asset,
        sts2::bridge::proto::AssetExplainRequest,
        sts2::bridge::proto::AssetExplainResult,
        "asset explain is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        capture_checkpoint,
        sts2::bridge::proto::CheckpointCaptureRequest,
        sts2::bridge::proto::CheckpointCaptureResult,
        "checkpoint capture is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        restore_checkpoint,
        sts2::bridge::proto::CheckpointRestoreRequest,
        sts2::bridge::proto::CheckpointRestoreResult,
        "checkpoint restore is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        list_checkpoints,
        sts2::bridge::proto::CheckpointListRequest,
        sts2::bridge::proto::CheckpointListResult,
        "checkpoint listing is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        delete_checkpoint,
        sts2::bridge::proto::CheckpointDeleteRequest,
        sts2::bridge::proto::CheckpointDeleteResult,
        "checkpoint deletion is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        record_fixture,
        sts2::bridge::proto::RecordedFixtureRequest,
        sts2::bridge::proto::RecordedFixtureResult,
        "recorded fixture capture is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        execute_console_command,
        sts2::bridge::proto::ConsoleCommandRequest,
        sts2::bridge::proto::ConsoleCommandResult,
        "console execution is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_hot_reload_status,
        sts2::bridge::proto::HotReloadStatusRequest,
        sts2::bridge::proto::HotReloadStatusResult,
        "hot-reload status is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        request_hot_reload,
        sts2::bridge::proto::HotReloadRequest,
        sts2::bridge::proto::HotReloadResult,
        "hot-reload request is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        inspect_presentation_localization,
        sts2::bridge::proto::PresentationLocalizationRequest,
        sts2::bridge::proto::PresentationLocalizationResult,
        "presentation localization inspection is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_mods,
        sts2::bridge::proto::ModListRequest,
        sts2::bridge::proto::ModListResult,
        "mod listing is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        start_debug_session,
        sts2::bridge::proto::DebugSessionStartRequest,
        sts2::bridge::proto::DebugSessionStartResult,
        "debug sessions are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_debug_session_status,
        sts2::bridge::proto::DebugSessionStatusRequest,
        sts2::bridge::proto::DebugSessionStatusResult,
        "debug sessions are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        end_debug_session,
        sts2::bridge::proto::DebugSessionEndRequest,
        sts2::bridge::proto::DebugSessionEndResult,
        "debug sessions are not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_debug_events,
        sts2::bridge::proto::DebugEventStreamRequest,
        sts2::bridge::proto::DebugEventStreamResult,
        "debug event replay is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        wait_debug,
        sts2::bridge::proto::DebugWaitRequest,
        sts2::bridge::proto::DebugWaitResult,
        "debug wait is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        get_debug_status,
        sts2::bridge::proto::DebugStatusRequest,
        sts2::bridge::proto::DebugStatusResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        pause_debug,
        sts2::bridge::proto::DebugPauseRequest,
        sts2::bridge::proto::DebugPauseResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        resume_debug,
        sts2::bridge::proto::DebugResumeRequest,
        sts2::bridge::proto::DebugResumeResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        step_debug,
        sts2::bridge::proto::DebugStepRequest,
        sts2::bridge::proto::DebugStepResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        list_breakpoints,
        sts2::bridge::proto::DebugBreakpointListRequest,
        sts2::bridge::proto::DebugBreakpointListResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        add_breakpoint,
        sts2::bridge::proto::DebugBreakpointAddRequest,
        sts2::bridge::proto::DebugBreakpointAddResult,
        "debug control is not exercised by this test service"
    );
    impl_unimplemented_bridge_rpc!(
        remove_breakpoint,
        sts2::bridge::proto::DebugBreakpointRemoveRequest,
        sts2::bridge::proto::DebugBreakpointRemoveResult,
        "debug control is not exercised by this test service"
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

    async fn load_fixture(
        &self,
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Err(tonic::Status::unimplemented(
            "fixture loading is not implemented in this test service",
        ))
    }
}

fn parse_ndjson_events(output: &str) -> Vec<Value> {
    output
        .lines()
        .map(|line| serde_json::from_str(line).expect("json event"))
        .collect()
}

#[derive(Default)]
struct DebuggerEventBridgeService {
    state: Mutex<DebuggerEventState>,
}

#[derive(Default)]
struct DebuggerEventState {
    controller: Option<sts2::bridge::proto::DebugSessionInfo>,
    observers: Vec<sts2::bridge::proto::DebugSessionInfo>,
    breakpoints: Vec<sts2::bridge::proto::DebugBreakpoint>,
    events: Vec<sts2::bridge::proto::DebugEvent>,
    next_session: u64,
    next_sequence: u64,
    execution_state: i32,
}

impl DebuggerEventBridgeService {
    fn session(
        state: &mut DebuggerEventState,
        name: String,
        role: sts2::bridge::proto::DebugSessionRole,
    ) -> sts2::bridge::proto::DebugSessionInfo {
        state.next_session += 1;
        sts2::bridge::proto::DebugSessionInfo {
            id: format!("dbg:{}", state.next_session),
            name,
            lease_timeout_ms: 60_000,
            lease_expires_at_unix_ms: 4_102_444_800_000,
            role: role as i32,
        }
    }

    fn push_event(
        state: &mut DebuggerEventState,
        kind: sts2::bridge::proto::DebugEventKind,
        session: Option<&sts2::bridge::proto::DebugSessionInfo>,
        detail: Option<sts2::bridge::proto::debug_event::Detail>,
    ) {
        state.next_sequence += 1;
        state.events.push(sts2::bridge::proto::DebugEvent {
            sequence: state.next_sequence,
            unix_time_ms: 1_700_000_000_000 + state.next_sequence as i64,
            kind: kind as i32,
            session_id: session.map(|value| value.id.clone()).unwrap_or_default(),
            session_role: session.map(|value| value.role).unwrap_or_default(),
            notices: Vec::new(),
            detail,
        });
    }

    fn status(
        state: &DebuggerEventState,
        caller: Option<&str>,
    ) -> sts2::bridge::proto::DebugStatusResponse {
        let caller_role = state
            .controller
            .as_ref()
            .filter(|session| Some(session.id.as_str()) == caller)
            .map(|session| session.role)
            .or_else(|| {
                state
                    .observers
                    .iter()
                    .find(|session| Some(session.id.as_str()) == caller)
                    .map(|session| session.role)
            })
            .unwrap_or_default();
        sts2::bridge::proto::DebugStatusResponse {
            supported: true,
            execution_state: if state.execution_state == 0 {
                sts2::bridge::proto::DebugExecutionState::Running as i32
            } else {
                state.execution_state
            },
            pause_reason: sts2::bridge::proto::DebugPauseReason::None as i32,
            can_pause: true,
            can_resume: true,
            supported_step_kinds: vec![sts2::bridge::proto::DebugStepKind::Action as i32],
            breakpoints: state.breakpoints.clone(),
            breakpoint_management_supported: true,
            breakpoint_evaluation_supported: true,
            session_ownership: if state.controller.is_some() {
                sts2::bridge::proto::DebugSessionOwnership::OwnedByCaller as i32
            } else {
                sts2::bridge::proto::DebugSessionOwnership::Unowned as i32
            },
            active_session: state.controller.clone(),
            caller_role,
            observer_sessions: state.observers.clone(),
            ..Default::default()
        }
    }
}

#[tonic::async_trait]
impl BridgeService for DebuggerEventBridgeService {
    impl_state_watch_unimplemented!();

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

    async fn get_models(
        &self,
        request: tonic::Request<sts2::bridge::proto::ModelCatalogRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ModelCatalogResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.get_models(request.into_inner()),
        ))
    }

    async fn get_reference(
        &self,
        request: tonic::Request<sts2::bridge::proto::ReferenceRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ReferenceResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.get_reference(request.into_inner()),
        ))
    }

    impl_unimplemented_bridge_rpc!(
        get_asset_catalog,
        sts2::bridge::proto::AssetCatalogRequest,
        sts2::bridge::proto::AssetCatalogResult,
        "asset catalog is not exercised by this test service"
    );

    impl_unimplemented_bridge_rpc!(
        inspect_presentation_localization,
        sts2::bridge::proto::PresentationLocalizationRequest,
        sts2::bridge::proto::PresentationLocalizationResult,
        "presentation localization inspection is not exercised by this test service"
    );

    impl_unimplemented_bridge_rpc!(
        get_runtime_transition_status,
        sts2::bridge::proto::RuntimeTransitionStatusRequest,
        sts2::bridge::proto::RuntimeTransitionStatusResult,
        "runtime transition status is not exercised by this test service"
    );

    impl_unimplemented_bridge_rpc!(
        get_mods,
        sts2::bridge::proto::ModListRequest,
        sts2::bridge::proto::ModListResult,
        "mod listing is not exercised by this test service"
    );

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

    async fn start_debug_session(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugSessionStartRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugSessionStartResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        let role = sts2::bridge::proto::DebugSessionRole::try_from(request.role)
            .ok()
            .unwrap_or(sts2::bridge::proto::DebugSessionRole::Controller);
        if role == sts2::bridge::proto::DebugSessionRole::Controller && state.controller.is_some() {
            return Ok(tonic::Response::new(
                sts2::bridge::proto::DebugSessionStartResult {
                    result: Some(
                        sts2::bridge::proto::debug_session_start_result::Result::Error(
                            sts2::bridge::proto::BridgeError {
                                code: sts2::bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
                                message: "debug controller lease conflict".to_string(),
                                ..Default::default()
                            },
                        ),
                    ),
                },
            ));
        }

        let session = Self::session(&mut state, request.name, role);
        if role == sts2::bridge::proto::DebugSessionRole::Controller {
            state.controller = Some(session.clone());
            Self::push_event(
                &mut state,
                sts2::bridge::proto::DebugEventKind::LeaseChanged,
                Some(&session),
                Some(sts2::bridge::proto::debug_event::Detail::LeaseChanged(
                    sts2::bridge::proto::DebugEventLeaseChanged {
                        controller: Some(session.clone()),
                        ..Default::default()
                    },
                )),
            );
        } else {
            state.observers.push(session.clone());
            Self::push_event(
                &mut state,
                sts2::bridge::proto::DebugEventKind::ObserverAttached,
                Some(&session),
                Some(sts2::bridge::proto::debug_event::Detail::ObserverAttached(
                    sts2::bridge::proto::DebugEventObserverChanged {
                        observer: Some(session.clone()),
                    },
                )),
            );
        }
        let status = Self::status(&state, Some(&session.id));
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugSessionStartResult {
                result: Some(
                    sts2::bridge::proto::debug_session_start_result::Result::Success(
                        sts2::bridge::proto::DebugSessionStartResponse {
                            started: true,
                            session: Some(session),
                            status: Some(status),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn get_debug_session_status(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugSessionStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugSessionStatusResult>, tonic::Status> {
        let request = request.into_inner();
        let state = self.state.lock().expect("debug state lock");
        let session = state
            .controller
            .as_ref()
            .filter(|session| session.id == request.id)
            .cloned()
            .or_else(|| {
                state
                    .observers
                    .iter()
                    .find(|session| session.id == request.id)
                    .cloned()
            });
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugSessionStatusResult {
                result: Some(
                    sts2::bridge::proto::debug_session_status_result::Result::Success(
                        sts2::bridge::proto::DebugSessionStatusResponse {
                            found: session.is_some(),
                            session,
                            status: Some(Self::status(&state, Some(&request.id))),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn end_debug_session(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugSessionEndRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugSessionEndResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        let ended = state
            .controller
            .as_ref()
            .is_some_and(|session| session.id == request.id);
        if ended {
            state.controller = None;
        }
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugSessionEndResult {
                result: Some(
                    sts2::bridge::proto::debug_session_end_result::Result::Success(
                        sts2::bridge::proto::DebugSessionEndResponse {
                            ended,
                            id: request.id,
                            status: Some(Self::status(&state, None)),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn get_debug_status(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugStatusRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStatusResult>, tonic::Status> {
        let request = request.into_inner();
        let state = self.state.lock().expect("debug state lock");
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugStatusResult {
                result: Some(sts2::bridge::proto::debug_status_result::Result::Success(
                    Self::status(&state, Some(&request.session_id)),
                )),
            },
        ))
    }

    async fn pause_debug(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugPauseRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugPauseResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        state.execution_state = sts2::bridge::proto::DebugExecutionState::Paused as i32;
        let session = state.controller.clone();
        Self::push_event(
            &mut state,
            sts2::bridge::proto::DebugEventKind::Paused,
            session.as_ref(),
            Some(sts2::bridge::proto::debug_event::Detail::Paused(
                sts2::bridge::proto::DebugEventPause {
                    reason: sts2::bridge::proto::DebugPauseReason::Manual as i32,
                    detail: "manual pause".to_string(),
                },
            )),
        );
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugPauseResult {
                result: Some(sts2::bridge::proto::debug_pause_result::Result::Success(
                    sts2::bridge::proto::DebugPauseResponse {
                        applied: true,
                        status: Some(Self::status(&state, Some(&request.session_id))),
                        notices: Vec::new(),
                    },
                )),
            },
        ))
    }

    async fn resume_debug(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugResumeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugResumeResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        state.execution_state = sts2::bridge::proto::DebugExecutionState::Running as i32;
        let session = state.controller.clone();
        Self::push_event(
            &mut state,
            sts2::bridge::proto::DebugEventKind::Resumed,
            session.as_ref(),
            Some(sts2::bridge::proto::debug_event::Detail::Resumed(
                sts2::bridge::proto::DebugEventResume {
                    detail: "manual resume".to_string(),
                },
            )),
        );
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugResumeResult {
                result: Some(sts2::bridge::proto::debug_resume_result::Result::Success(
                    sts2::bridge::proto::DebugResumeResponse {
                        applied: true,
                        status: Some(Self::status(&state, Some(&request.session_id))),
                        notices: Vec::new(),
                    },
                )),
            },
        ))
    }

    async fn step_debug(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugStepRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugStepResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        let session = state.controller.clone();
        Self::push_event(
            &mut state,
            sts2::bridge::proto::DebugEventKind::Stepped,
            session.as_ref(),
            Some(sts2::bridge::proto::debug_event::Detail::Stepped(
                sts2::bridge::proto::DebugEventStep {
                    kind: request.kind,
                    count: request.count,
                    detail: "step complete".to_string(),
                },
            )),
        );
        Ok(tonic::Response::new(sts2::bridge::proto::DebugStepResult {
            result: Some(sts2::bridge::proto::debug_step_result::Result::Success(
                sts2::bridge::proto::DebugStepResponse {
                    applied: true,
                    status: Some(Self::status(&state, Some(&request.session_id))),
                    notices: Vec::new(),
                },
            )),
        }))
    }

    async fn wait_debug(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugWaitRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugWaitResult>, tonic::Status> {
        let request = request.into_inner();
        let state = self.state.lock().expect("debug state lock");
        Ok(tonic::Response::new(sts2::bridge::proto::DebugWaitResult {
            result: Some(sts2::bridge::proto::debug_wait_result::Result::Success(
                sts2::bridge::proto::DebugWaitResponse {
                    completed: true,
                    timed_out: false,
                    status: Some(Self::status(&state, Some(&request.session_id))),
                    notices: Vec::new(),
                },
            )),
        }))
    }

    async fn list_breakpoints(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugBreakpointListRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointListResult>, tonic::Status>
    {
        let request = request.into_inner();
        let state = self.state.lock().expect("debug state lock");
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugBreakpointListResult {
                result: Some(
                    sts2::bridge::proto::debug_breakpoint_list_result::Result::Success(
                        sts2::bridge::proto::DebugBreakpointListResponse {
                            breakpoints: state.breakpoints.clone(),
                            status: Some(Self::status(&state, Some(&request.session_id))),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn add_breakpoint(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugBreakpointAddRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointAddResult>, tonic::Status> {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        let breakpoint = sts2::bridge::proto::DebugBreakpoint {
            id: "bp:1".to_string(),
            name: request.name,
            query_path: request.query_path,
            predicate: request.predicate,
            enabled: true,
            kind: sts2::bridge::proto::DebugBreakpointKind::Match as i32,
            hit_count: 1,
            min_hit_count: 1,
            ..Default::default()
        };
        state.breakpoints.push(breakpoint.clone());
        let session = state.controller.clone();
        Self::push_event(
            &mut state,
            sts2::bridge::proto::DebugEventKind::BreakpointHit,
            session.as_ref(),
            Some(sts2::bridge::proto::debug_event::Detail::BreakpointHit(
                sts2::bridge::proto::DebugEventBreakpointHit {
                    hit: Some(sts2::bridge::proto::DebugBreakpointHit {
                        breakpoint_id: breakpoint.id.clone(),
                        breakpoint_name: breakpoint.name.clone(),
                        query_path: breakpoint.query_path.clone(),
                        predicate: breakpoint.predicate.clone(),
                        actual_json: "\"combat\"".to_string(),
                        screen_type: "combat".to_string(),
                        screen_instance_id: "screen:combat:1".to_string(),
                        kind: breakpoint.kind,
                        hit_count: 1,
                    }),
                },
            )),
        );
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugBreakpointAddResult {
                result: Some(
                    sts2::bridge::proto::debug_breakpoint_add_result::Result::Success(
                        sts2::bridge::proto::DebugBreakpointAddResponse {
                            added: true,
                            breakpoint: Some(breakpoint),
                            status: Some(Self::status(&state, Some(&request.session_id))),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn remove_breakpoint(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugBreakpointRemoveRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugBreakpointRemoveResult>, tonic::Status>
    {
        let request = request.into_inner();
        let mut state = self.state.lock().expect("debug state lock");
        state
            .breakpoints
            .retain(|breakpoint| breakpoint.id != request.id);
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugBreakpointRemoveResult {
                result: Some(
                    sts2::bridge::proto::debug_breakpoint_remove_result::Result::Success(
                        sts2::bridge::proto::DebugBreakpointRemoveResponse {
                            removed: true,
                            id: request.id,
                            status: Some(Self::status(&state, None)),
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
    }

    async fn get_debug_events(
        &self,
        request: tonic::Request<sts2::bridge::proto::DebugEventStreamRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::DebugEventStreamResult>, tonic::Status> {
        let request = request.into_inner();
        let state = self.state.lock().expect("debug state lock");
        let oldest = state
            .events
            .first()
            .map(|event| event.sequence)
            .unwrap_or(0);
        let newest = state.events.last().map(|event| event.sequence).unwrap_or(0);
        let from = if request.from_sequence == 0 {
            oldest
        } else {
            request.from_sequence
        };
        let limit = request.limit.max(1) as usize;
        let matching = state
            .events
            .iter()
            .filter(|event| event.sequence >= from)
            .cloned()
            .collect::<Vec<_>>();
        let events = matching.iter().take(limit).cloned().collect::<Vec<_>>();
        let next_sequence = events
            .last()
            .map(|event| event.sequence + 1)
            .unwrap_or(from);
        Ok(tonic::Response::new(
            sts2::bridge::proto::DebugEventStreamResult {
                result: Some(
                    sts2::bridge::proto::debug_event_stream_result::Result::Success(
                        sts2::bridge::proto::DebugEventStreamResponse {
                            events,
                            from_sequence: from,
                            next_sequence,
                            oldest_retained_sequence: oldest,
                            newest_sequence: newest,
                            retention_limit: 32,
                            expired: request.from_sequence != 0 && request.from_sequence < oldest,
                            overflow: matching.len() > limit,
                            notices: Vec::new(),
                        },
                    ),
                ),
            },
        ))
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
        Err(tonic::Status::unimplemented("not exercised"))
    }

    async fn get_runtime_scene_node(
        &self,
        _request: tonic::Request<RuntimeSceneNodeRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneNodeResult>, tonic::Status> {
        Err(tonic::Status::unimplemented("not exercised"))
    }

    async fn set_runtime_scene_node_visible(
        &self,
        _request: tonic::Request<RuntimeSceneSetVisibleRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::RuntimeSceneSetVisibleResult>, tonic::Status>
    {
        Err(tonic::Status::unimplemented("not exercised"))
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

    async fn load_fixture(
        &self,
        _request: tonic::Request<FixtureLoadRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::FixtureLoadResult>, tonic::Status> {
        Err(tonic::Status::unimplemented("not exercised"))
    }

    impl_unimplemented_bridge_rpc!(
        extract_asset,
        sts2::bridge::proto::AssetExtractRequest,
        sts2::bridge::proto::AssetExtractResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        explain_asset,
        sts2::bridge::proto::AssetExplainRequest,
        sts2::bridge::proto::AssetExplainResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        close_game,
        sts2::bridge::proto::GameCloseRequest,
        sts2::bridge::proto::GameCloseResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        capture_scenario,
        sts2::bridge::proto::ScenarioCaptureRequest,
        sts2::bridge::proto::ScenarioCaptureResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        restore_scenario,
        sts2::bridge::proto::ScenarioRestoreRequest,
        sts2::bridge::proto::ScenarioRestoreResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        capture_checkpoint,
        sts2::bridge::proto::CheckpointCaptureRequest,
        sts2::bridge::proto::CheckpointCaptureResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        restore_checkpoint,
        sts2::bridge::proto::CheckpointRestoreRequest,
        sts2::bridge::proto::CheckpointRestoreResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        record_fixture,
        sts2::bridge::proto::RecordedFixtureRequest,
        sts2::bridge::proto::RecordedFixtureResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        execute_console_command,
        sts2::bridge::proto::ConsoleCommandRequest,
        sts2::bridge::proto::ConsoleCommandResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        list_checkpoints,
        sts2::bridge::proto::CheckpointListRequest,
        sts2::bridge::proto::CheckpointListResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        delete_checkpoint,
        sts2::bridge::proto::CheckpointDeleteRequest,
        sts2::bridge::proto::CheckpointDeleteResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        get_hot_reload_status,
        sts2::bridge::proto::HotReloadStatusRequest,
        sts2::bridge::proto::HotReloadStatusResult,
        "not exercised"
    );
    impl_unimplemented_bridge_rpc!(
        request_hot_reload,
        sts2::bridge::proto::HotReloadRequest,
        sts2::bridge::proto::HotReloadResult,
        "not exercised"
    );
}
