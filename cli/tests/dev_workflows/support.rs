use std::fs;
use std::io::{Read, Write};
use std::net::TcpListener;
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;

use clap::Parser;
use serde_json::Value;
use sts2::bridge::StubBridgeService;
use sts2::bridge::proto::bridge_service_server::BridgeService;
use sts2::bridge::proto::{
    ActionRequest, FixtureLoadRequest, HandshakeRequest, LogsRequest, RuntimeSceneNodeRequest,
    RuntimeSceneSetVisibleRequest, RuntimeSceneTreeRequest, ScreenshotRequest, StateRequest,
};
use sts2::{
    AppConfig, Cli, MockScenario, RenderedCommand, TransportKind, run_cli, run_cli_streaming,
};
use tokio::net::UnixListener;
use tungstenite::{Message, accept};

fn run(args: &[&str]) -> RenderedCommand {
    let (owned_args, _config) = args_with_mock_config(args);
    let cli = Cli::parse_from(owned_args.iter().map(String::as_str));
    run_cli(cli).expect("command should succeed")
}

fn run_error(args: &[&str]) -> RenderedCommand {
    let (owned_args, _config) = args_with_mock_config(args);
    let cli = Cli::parse_from(owned_args.iter().map(String::as_str));
    run_cli(cli.clone())
        .expect_err("command should fail")
        .render(cli.json)
}

fn try_run(args: &[&str]) -> RenderedCommand {
    let (owned_args, _config) = args_with_mock_config(args);
    let cli = <Cli as Parser>::try_parse_from(owned_args.iter().map(String::as_str))
        .expect("command should parse");
    run_cli(cli).expect("command should succeed")
}

fn try_run_error(args: &[&str]) -> RenderedCommand {
    let (owned_args, _config) = args_with_mock_config(args);
    let cli = <Cli as Parser>::try_parse_from(owned_args.iter().map(String::as_str))
        .expect("command should parse");
    run_cli(cli.clone())
        .expect_err("command should fail")
        .render(cli.json)
}

fn args_with_mock_config(args: &[&str]) -> (Vec<String>, Option<tempfile::NamedTempFile>) {
    let mut owned_args: Vec<String> = args.iter().map(|arg| (*arg).to_string()).collect();
    if !owned_args.iter().any(|arg| arg == "--config") {
        let config = write_raw_config("transport:\n  kind: mock\n");
        owned_args.insert(1, config.path().to_str().expect("utf8 config").to_string());
        owned_args.insert(1, "--config".to_string());
        return (owned_args, Some(config));
    }
    (owned_args, None)
}

fn write_config(config: &AppConfig) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn write_raw_config(config_text: &str) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
}

fn run_binary_error_json_in_dir_with_env(
    workdir: &Path,
    args: &[&str],
    envs: &[(&str, &str)],
) -> (i32, Value) {
    let output = Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
        .envs(envs.iter().copied())
        .output()
        .expect("run sts2 binary");
    assert!(
        !output.status.success(),
        "expected failure, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    (
        output.status.code().unwrap_or_default(),
        serde_json::from_slice(&output.stdout).expect("json output"),
    )
}

#[cfg(unix)]
fn seed_recent_spirectl_log(data_root: &Path, file_name: &str) -> PathBuf {
    let logs_dir = data_root.join("godot/app_userdata/Slay the Spire 2/logs");
    fs::create_dir_all(&logs_dir).expect("logs dir");
    let log_path = logs_dir.join(file_name);
    fs::write(
        &log_path,
        "INFO booting\n[spirectl] live bridge endpoint was not reachable during visual preflight test\n",
    )
    .expect("write spirectl log");
    log_path
}

fn write_png(path: &std::path::Path, rgba: [u8; 4]) {
    let image = image::RgbaImage::from_pixel(2, 2, image::Rgba(rgba));
    image.save(path).expect("write png");
}

fn write_visual_diff_fixture(
    baseline: &std::path::Path,
    actual: &std::path::Path,
    mask: &std::path::Path,
) {
    let mut baseline_image = image::RgbaImage::from_pixel(100, 100, image::Rgba([0, 0, 0, 255]));
    let mut actual_image = baseline_image.clone();
    let mut mask_image = image::RgbaImage::from_pixel(100, 100, image::Rgba([0, 0, 0, 0]));
    baseline_image.put_pixel(50, 50, image::Rgba([20, 20, 20, 255]));
    actual_image.put_pixel(50, 50, image::Rgba([250, 10, 250, 255]));
    mask_image.put_pixel(50, 50, image::Rgba([255, 255, 255, 255]));
    baseline_image.save(baseline).expect("write baseline png");
    actual_image.save(actual).expect("write actual png");
    mask_image.save(mask).expect("write mask png");
}

macro_rules! impl_unimplemented_bridge_rpc {
    ($method:ident, $request:ty, $response:ty, $message:literal) => {
        fn $method<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<$request>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<Output = Result<tonic::Response<$response>, tonic::Status>>
                    + Send
                    + 'async_trait,
            >,
        >
        where
            'life0: 'async_trait,
            Self: 'async_trait,
        {
            Box::pin(async move { Err(tonic::Status::unimplemented($message)) })
        }
    };
}

macro_rules! impl_state_watch_unimplemented {
    () => {
        type WatchStateStream = std::pin::Pin<
            Box<
                dyn tokio_stream::Stream<
                        Item = Result<sts2::bridge::proto::StateWatchEvent, tonic::Status>,
                    > + Send,
            >,
        >;

        fn watch_state<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::StateWatchRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<tonic::Response<Self::WatchStateStream>, tonic::Status>,
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
                    "state watch is not exercised by this test service",
                ))
            })
        }

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

    };
}

macro_rules! impl_debug_session_rpc_stubs {
    () => {
        fn close_game<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::GameCloseRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::GameCloseResult>,
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
                    "game close is not exercised by this test service",
                ))
            })
        }

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

        fn capture_checkpoint<'life0, 'async_trait>(
            &'life0 self,
            request: tonic::Request<sts2::bridge::proto::CheckpointCaptureRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::CheckpointCaptureResult>,
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
                let service = StubBridgeService::new(MockScenario::Combat);
                Ok(tonic::Response::new(
                    service.capture_checkpoint(request.into_inner()),
                ))
            })
        }

        fn restore_checkpoint<'life0, 'async_trait>(
            &'life0 self,
            request: tonic::Request<sts2::bridge::proto::CheckpointRestoreRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::CheckpointRestoreResult>,
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
                let service = StubBridgeService::new(MockScenario::Combat);
                Ok(tonic::Response::new(
                    service.restore_checkpoint(request.into_inner()),
                ))
            })
        }

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

        fn start_debug_session<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::DebugSessionStartRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::DebugSessionStartResult>,
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
                    "debug sessions are not exercised by this test service",
                ))
            })
        }

        fn get_debug_session_status<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::DebugSessionStatusRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::DebugSessionStatusResult>,
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
                    "debug sessions are not exercised by this test service",
                ))
            })
        }

        fn end_debug_session<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::DebugSessionEndRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::DebugSessionEndResult>,
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
                    "debug sessions are not exercised by this test service",
                ))
            })
        }

        impl_unimplemented_bridge_rpc!(
            get_debug_events,
            sts2::bridge::proto::DebugEventStreamRequest,
            sts2::bridge::proto::DebugEventStreamResult,
            "debug event replay is not exercised by this test service"
        );

        fn wait_debug<'life0, 'async_trait>(
            &'life0 self,
            _request: tonic::Request<sts2::bridge::proto::DebugWaitRequest>,
        ) -> core::pin::Pin<
            Box<
                dyn core::future::Future<
                        Output = Result<
                            tonic::Response<sts2::bridge::proto::DebugWaitResult>,
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
                    "debug wait is not exercised by this test service",
                ))
            })
        }
    };
}

macro_rules! impl_runtime_scene_set_visible_unimplemented {
    () => {
        impl_unimplemented_bridge_rpc!(
            set_runtime_scene_node_visible,
            sts2::bridge::proto::RuntimeSceneSetVisibleRequest,
            sts2::bridge::proto::RuntimeSceneSetVisibleResult,
            "runtime scene visibility mutation is not exercised by this test service"
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
            get_runtime_transition_status,
            sts2::bridge::proto::RuntimeTransitionStatusRequest,
            sts2::bridge::proto::RuntimeTransitionStatusResult,
            "runtime transition status is not exercised by this test service"
        );
    };
}

fn spawn_http_json_server(responses: Vec<(u16, &'static str)>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind http listener");
    let address = listener.local_addr().expect("listener addr");
    thread::spawn(move || {
        for (status, body) in responses {
            let (mut stream, _) = listener.accept().expect("accept http request");
            let mut request = [0_u8; 4096];
            let _ = stream.read(&mut request).expect("read request");
            let reason = match status {
                200 => "OK",
                202 => "Accepted",
                503 => "Service Unavailable",
                _ => "Custom",
            };
            let response = format!(
                "HTTP/1.1 {status} {reason}\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                body.len()
            );
            stream
                .write_all(response.as_bytes())
                .expect("write response");
        }
    });

    format!("http://{}", address)
}

fn spawn_websocket_echo_server() -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind websocket listener");
    let address = listener.local_addr().expect("listener addr");
    thread::spawn(move || {
        let (stream, _) = listener.accept().expect("accept websocket");
        let mut websocket = accept(stream).expect("accept websocket");
        let frame = websocket.read().expect("read frame");
        assert_eq!(frame, Message::Text("ping".into()));
        websocket
            .send(Message::Text("pong".into()))
            .expect("send frame");
        websocket.close(None).expect("close websocket");
    });

    format!("ws://{}", address)
}

fn spawn_websocket_single_response_server(response_text: &'static str) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind websocket listener");
    let address = listener.local_addr().expect("listener addr");
    thread::spawn(move || {
        let (stream, _) = listener.accept().expect("accept websocket");
        let mut websocket = accept(stream).expect("accept websocket");
        let frame = websocket.read().expect("read frame");
        assert_eq!(frame, Message::Text("ping".into()));
        websocket
            .send(Message::Text(response_text.into()))
            .expect("send frame");
        websocket.close(None).expect("close websocket");
    });

    format!("ws://{}", address)
}

fn combat_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Combat,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn lobby_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Lobby,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn lobby_ready_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::LobbyReady,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn map_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Map,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn rewards_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Rewards,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn shop_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Shop,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn card_overlay_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::CardOverlay,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn card_selection_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::CardSelection,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn event_room_mock_config() -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::EventRoom,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    })
}

fn fake_merchant_pre_open_mock_config() -> tempfile::NamedTempFile {
    write_raw_config("transport:\n  kind: mock\n  mockScenario: fake-merchant-pre-open\n")
}

fn crystal_sphere_mock_config() -> tempfile::NamedTempFile {
    write_raw_config("transport:\n  kind: mock\n  mockScenario: crystal-sphere\n")
}

fn crystal_sphere_finished_mock_config() -> tempfile::NamedTempFile {
    write_raw_config("transport:\n  kind: mock\n  mockScenario: crystal-sphere-finished\n")
}

fn treasure_room_mock_config() -> tempfile::NamedTempFile {
    write_raw_config("transport:\n  kind: mock\n  mockScenario: treasure-room\n")
}
