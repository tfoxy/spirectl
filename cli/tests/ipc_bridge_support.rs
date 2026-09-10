// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]

use prost::Message;
use std::io;
use std::sync::Arc;
use sts2::bridge::{ipc_protocol, proto};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::{TcpListener, UnixListener};
use tokio_stream::StreamExt;
use tonic::Request;

pub fn spawn_unix_bridge_service<T>(
    listener: UnixListener,
    service: T,
) -> tokio::task::JoinHandle<()>
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    tokio::spawn(run_unix_bridge_service(listener, Arc::new(service)))
}

#[allow(dead_code)]
pub fn spawn_unix_bridge_service_without_presentation<T>(
    listener: UnixListener,
    service: T,
) -> tokio::task::JoinHandle<()>
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    tokio::spawn(run_unix_bridge_service(listener, Arc::new(service)))
}

#[allow(dead_code)]
pub fn spawn_tcp_bridge_service<T>(listener: TcpListener, service: T) -> tokio::task::JoinHandle<()>
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    tokio::spawn(run_tcp_bridge_service(listener, Arc::new(service)))
}

#[allow(dead_code)]
pub async fn serve_unix_bridge_service<T>(listener: UnixListener, service: T)
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    run_unix_bridge_service(listener, Arc::new(service)).await;
}

async fn run_unix_bridge_service<T>(listener: UnixListener, service: Arc<T>)
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    loop {
        let (mut stream, _) = listener.accept().await.expect("accept unix bridge client");
        let service = Arc::clone(&service);
        tokio::spawn(async move {
            if let Err(error) = serve_connection(&mut stream, service).await {
                assert!(
                    is_client_disconnect(error.as_ref()),
                    "serve standalone IPC bridge: {error}"
                );
            }
        });
    }
}

#[allow(dead_code)]
async fn run_tcp_bridge_service<T>(listener: TcpListener, service: Arc<T>)
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    loop {
        let (mut stream, _) = listener.accept().await.expect("accept tcp bridge client");
        let service = Arc::clone(&service);
        tokio::spawn(async move {
            if let Err(error) = serve_connection(&mut stream, service).await {
                assert!(
                    is_client_disconnect(error.as_ref()),
                    "serve standalone TCP bridge: {error}"
                );
            }
        });
    }
}

fn is_client_disconnect(error: &(dyn std::error::Error + 'static)) -> bool {
    if let Some(io_error) = error.downcast_ref::<io::Error>() {
        return matches!(
            io_error.kind(),
            io::ErrorKind::BrokenPipe
                | io::ErrorKind::ConnectionReset
                | io::ErrorKind::ConnectionAborted
                | io::ErrorKind::UnexpectedEof
        );
    }

    false
}

async fn serve_connection<T>(
    stream: &mut (impl AsyncRead + AsyncWrite + Unpin),
    service: Arc<T>,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>>
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    let mut header = [0u8; 12];
    stream.read_exact(&mut header).await?;

    let magic = u32::from_le_bytes(header[0..4].try_into().expect("magic bytes"));
    if magic != ipc_protocol::MAGIC {
        return write_protocol_error(stream, "unexpected IPC frame magic").await;
    }

    let version = u16::from_le_bytes(header[4..6].try_into().expect("version bytes"));
    if version != ipc_protocol::VERSION {
        return write_protocol_error(stream, "unsupported IPC frame version").await;
    }

    let tag = u16::from_le_bytes(header[6..8].try_into().expect("tag bytes"));
    let method = match ipc_protocol::Method::from_tag(tag) {
        Some(method) => method,
        None => return write_protocol_error(stream, "unsupported IPC method").await,
    };

    let payload_len = u32::from_le_bytes(header[8..12].try_into().expect("length bytes"));
    let mut payload = vec![0u8; payload_len as usize];
    if payload_len > 0 {
        stream.read_exact(&mut payload).await?;
    }

    if method == ipc_protocol::Method::WatchState {
        let response_stream = service
            .watch_state(Request::new(proto::StateWatchRequest::decode(
                payload.as_slice(),
            )?))
            .await?
            .into_inner();
        let mut response_stream = Box::pin(response_stream);
        while let Some(event) = response_stream.next().await {
            write_response(
                stream,
                ipc_protocol::ResponseStatus::Success,
                &event?.encode_to_vec(),
            )
            .await?;
        }
        return Ok(());
    }

    let response_payload = dispatch(service, method, payload).await?;
    write_response(
        stream,
        ipc_protocol::ResponseStatus::Success,
        &response_payload,
    )
    .await
}

async fn dispatch<T>(
    service: Arc<T>,
    method: ipc_protocol::Method,
    payload: Vec<u8>,
) -> Result<Vec<u8>, Box<dyn std::error::Error + Send + Sync>>
where
    T: proto::bridge_service_server::BridgeService + Send + Sync + 'static,
{
    let bytes = match method {
        ipc_protocol::Method::Handshake => match service
            .handshake(Request::new(proto::HandshakeRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::HandshakeResult {
                result: Some(proto::handshake_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetState => match service
            .get_state(Request::new(proto::StateRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::StateResult {
                result: Some(proto::state_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetModels => match service
            .get_models(Request::new(proto::ModelCatalogRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ModelCatalogResult {
                result: Some(proto::model_catalog_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetReference => match service
            .get_reference(Request::new(proto::ReferenceRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ReferenceResult {
                result: Some(proto::reference_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ExecuteAction => match service
            .execute_action(Request::new(proto::ActionRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ActionResult {
                result: Some(proto::action_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ExecuteConsoleCommand => match service
            .execute_console_command(Request::new(proto::ConsoleCommandRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ConsoleCommandResult {
                result: Some(proto::console_command_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::LoadFixture => match service
            .load_fixture(Request::new(proto::FixtureLoadRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::FixtureLoadResult {
                result: Some(proto::fixture_load_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetLogs => match service
            .get_logs(Request::new(proto::LogsRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::LogsResult {
                result: Some(proto::logs_result::Result::Error(bridge_error_from_status(
                    &status,
                ))),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetDebugStatus => match service
            .get_debug_status(Request::new(proto::DebugStatusRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugStatusResult {
                result: Some(proto::debug_status_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::StartDebugSession => match service
            .start_debug_session(Request::new(proto::DebugSessionStartRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugSessionStartResult {
                result: Some(proto::debug_session_start_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetDebugSessionStatus => match service
            .get_debug_session_status(Request::new(proto::DebugSessionStatusRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugSessionStatusResult {
                result: Some(proto::debug_session_status_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::EndDebugSession => match service
            .end_debug_session(Request::new(proto::DebugSessionEndRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugSessionEndResult {
                result: Some(proto::debug_session_end_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::PauseDebug => match service
            .pause_debug(Request::new(proto::DebugPauseRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugPauseResult {
                result: Some(proto::debug_pause_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ResumeDebug => match service
            .resume_debug(Request::new(proto::DebugResumeRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugResumeResult {
                result: Some(proto::debug_resume_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::StepDebug => match service
            .step_debug(Request::new(proto::DebugStepRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugStepResult {
                result: Some(proto::debug_step_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::WaitDebug => match service
            .wait_debug(Request::new(proto::DebugWaitRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugWaitResult {
                result: Some(proto::debug_wait_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ListBreakpoints => match service
            .list_breakpoints(Request::new(proto::DebugBreakpointListRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugBreakpointListResult {
                result: Some(proto::debug_breakpoint_list_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::AddBreakpoint => match service
            .add_breakpoint(Request::new(proto::DebugBreakpointAddRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugBreakpointAddResult {
                result: Some(proto::debug_breakpoint_add_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::RemoveBreakpoint => match service
            .remove_breakpoint(Request::new(proto::DebugBreakpointRemoveRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugBreakpointRemoveResult {
                result: Some(proto::debug_breakpoint_remove_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetDebugEvents => match service
            .get_debug_events(Request::new(proto::DebugEventStreamRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::DebugEventStreamResult {
                result: Some(proto::debug_event_stream_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetRuntimeSceneTree => match service
            .get_runtime_scene_tree(Request::new(proto::RuntimeSceneTreeRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeSceneTreeResult {
                result: Some(proto::runtime_scene_tree_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetRuntimeSceneNode => match service
            .get_runtime_scene_node(Request::new(proto::RuntimeSceneNodeRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeSceneNodeResult {
                result: Some(proto::runtime_scene_node_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::SetRuntimeSceneNodeVisible => match service
            .set_runtime_scene_node_visible(Request::new(
                proto::RuntimeSceneSetVisibleRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeSceneSetVisibleResult {
                result: Some(proto::runtime_scene_set_visible_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::HoverRuntimeSceneControl => match service
            .hover_runtime_scene_control(Request::new(
                proto::RuntimeSceneControlHoverRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeSceneControlHoverResult {
                result: Some(proto::runtime_scene_control_hover_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::UnhoverRuntimeSceneControl => match service
            .unhover_runtime_scene_control(Request::new(
                proto::RuntimeSceneControlUnhoverRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeSceneControlUnhoverResult {
                result: Some(proto::runtime_scene_control_unhover_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetRuntimeTransitionStatus => match service
            .get_runtime_transition_status(Request::new(
                proto::RuntimeTransitionStatusRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RuntimeTransitionStatusResult {
                result: Some(proto::runtime_transition_status_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::InspectPresentationResourceScenes => match service
            .inspect_presentation_resource_scenes(Request::new(
                proto::PresentationResourceSceneRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::PresentationResourceSceneResult {
                result: Some(proto::presentation_resource_scene_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::InspectPresentationLocalization => match service
            .inspect_presentation_localization(Request::new(
                proto::PresentationLocalizationRequest::decode(payload.as_slice())?,
            ))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::PresentationLocalizationResult {
                result: Some(proto::presentation_localization_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetScreenshot => match service
            .get_screenshot(Request::new(proto::ScreenshotRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ScreenshotResult {
                result: Some(proto::screenshot_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetAssetCatalog => match service
            .get_asset_catalog(Request::new(proto::AssetCatalogRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::AssetCatalogResult {
                result: Some(proto::asset_catalog_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ExtractAsset => match service
            .extract_asset(Request::new(proto::AssetExtractRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::AssetExtractResult {
                result: Some(proto::asset_extract_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ExplainAsset => match service
            .explain_asset(Request::new(proto::AssetExplainRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::AssetExplainResult {
                result: Some(proto::asset_explain_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetMods => match service
            .get_mods(Request::new(proto::ModListRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ModListResult {
                result: Some(proto::mod_list_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::CaptureScenario => match service
            .capture_scenario(Request::new(proto::ScenarioCaptureRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ScenarioCaptureResult {
                result: Some(proto::scenario_capture_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::RestoreScenario => match service
            .restore_scenario(Request::new(proto::ScenarioRestoreRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::ScenarioRestoreResult {
                result: Some(proto::scenario_restore_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::CaptureCheckpoint => match service
            .capture_checkpoint(Request::new(proto::CheckpointCaptureRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::CheckpointCaptureResult {
                result: Some(proto::checkpoint_capture_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::RestoreCheckpoint => match service
            .restore_checkpoint(Request::new(proto::CheckpointRestoreRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::CheckpointRestoreResult {
                result: Some(proto::checkpoint_restore_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::ListCheckpoints => match service
            .list_checkpoints(Request::new(proto::CheckpointListRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::CheckpointListResult {
                result: Some(proto::checkpoint_list_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::DeleteCheckpoint => match service
            .delete_checkpoint(Request::new(proto::CheckpointDeleteRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::CheckpointDeleteResult {
                result: Some(proto::checkpoint_delete_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::RecordFixture => match service
            .record_fixture(Request::new(proto::RecordedFixtureRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::RecordedFixtureResult {
                result: Some(proto::recorded_fixture_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetHotReloadStatus => match service
            .get_hot_reload_status(Request::new(proto::HotReloadStatusRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::HotReloadStatusResult {
                result: Some(proto::hot_reload_status_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::RequestHotReload => match service
            .request_hot_reload(Request::new(proto::HotReloadRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::HotReloadResult {
                result: Some(proto::hot_reload_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::CloseGame => match service
            .close_game(Request::new(proto::GameCloseRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::GameCloseResult {
                result: Some(proto::game_close_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetCombatPreview => match service
            .get_combat_preview(Request::new(proto::CombatPreviewRequest::decode(
                payload.as_slice(),
            )?))
            .await
        {
            Ok(response) => response.into_inner().encode_to_vec(),
            Err(status) => proto::CombatPreviewResult {
                result: Some(proto::combat_preview_result::Result::Error(
                    bridge_error_from_status(&status),
                )),
            }
            .encode_to_vec(),
        },
        ipc_protocol::Method::GetMapDrawings => proto::MapDrawingsResult {
            result: Some(proto::map_drawings_result::Result::Error(
                bridge_error_from_status(&tonic::Status::unimplemented(
                    "map drawings are not implemented by this test bridge service",
                )),
            )),
        }
        .encode_to_vec(),
        ipc_protocol::Method::WatchState | ipc_protocol::Method::WatchCombatEvents => Vec::new(),
    };

    Ok(bytes)
}

fn bridge_error_from_status(status: &tonic::Status) -> proto::BridgeError {
    let bridge_code = match status.code() {
        tonic::Code::Unimplemented => proto::BridgeErrorCode::NotImplemented,
        _ => proto::BridgeErrorCode::RuntimeFailure,
    };
    let note = match status.code() {
        tonic::Code::Unimplemented => "The IPC bridge host does not implement the requested RPC.",
        _ => "The IPC bridge host returned a non-OK RPC status.",
    };

    proto::BridgeError {
        code: bridge_code as i32,
        message: format!("Bridge RPC failed: {}", status.message()),
        details: vec![proto::ErrorDetail {
            field: "rpc_status".to_string(),
            value: format!("{:?}", status.code()),
            note: note.to_string(),
            ..Default::default()
        }],
        action_failure: None,
    }
}

async fn write_protocol_error(
    stream: &mut (impl AsyncWrite + Unpin),
    message: &str,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    write_response(
        stream,
        ipc_protocol::ResponseStatus::ProtocolError,
        message.as_bytes(),
    )
    .await
}

async fn write_response(
    stream: &mut (impl AsyncWrite + Unpin),
    status: ipc_protocol::ResponseStatus,
    payload: &[u8],
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    stream.write_all(&ipc_protocol::MAGIC.to_le_bytes()).await?;
    stream
        .write_all(&ipc_protocol::VERSION.to_le_bytes())
        .await?;
    stream.write_all(&status.tag().to_le_bytes()).await?;
    stream
        .write_all(&(payload.len() as u32).to_le_bytes())
        .await?;
    if !payload.is_empty() {
        stream.write_all(payload).await?;
    }
    stream.flush().await?;
    Ok(())
}
