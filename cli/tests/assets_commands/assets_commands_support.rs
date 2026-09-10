use std::fs;
use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use clap::Parser;
use clap::error::ErrorKind;
use image::ImageFormat;
use prost_types::{Struct, Value as ProtoValue, value::Kind};
use serde_json::Value;
use sts2::bridge::StubBridgeService;
use sts2::bridge::proto::bridge_service_server::BridgeService;
use sts2::bridge::proto::{
    ActionRequest, AssetExplainRequest, AssetExtractRequest, FixtureLoadRequest, HandshakeRequest,
    LogsRequest, ModelCatalogRequest, ScreenshotRequest, StateRequest,
};
use sts2::{
    AppConfig, Cli, MockScenario, RenderedCommand, TransportConfig, TransportKind, run_cli,
};
use tokio::net::UnixListener;
use tokio::time::sleep;

fn run(args: &[&str]) -> RenderedCommand {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

fn test_lock() -> &'static Mutex<()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
}

fn sts2_bin() -> String {
    std::env::var("CARGO_BIN_EXE_sts2").expect("sts2 binary path")
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

fn run_json_bin(args: &[&str]) -> Value {
    run_json_bin_with_env(args, &[])
}

fn run_json_bin_with_env(args: &[&str], envs: &[(&str, &str)]) -> Value {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let output = Command::new(sts2_bin())
        .args(args)
        .current_dir(repo_root())
        .envs(envs.iter().copied())
        .output()
        .expect("run sts2 binary");
    assert!(
        output.status.success(),
        "expected success, got status {:?}, stdout: {}, stderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("json output")
}

fn run_json_bin_failure_with_env(args: &[&str], envs: &[(&str, &str)]) -> (i32, Value) {
    let _lock = test_lock()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let output = Command::new(sts2_bin())
        .args(args)
        .current_dir(repo_root())
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

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repo root")
        .to_path_buf()
}

fn write_asset_config(
    resources_dir: &Path,
    artifacts_dir: &Path,
    mods_dir: Option<&Path>,
) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let mut config = AppConfig {
        game: sts2::GameConfig {
            path: "auto".to_string(),
            resources_dir: Some(resources_dir.display().to_string()),
            mods_dir: mods_dir.map(|path| path.display().to_string()),
            ..sts2::GameConfig::default()
        },
        artifacts: sts2::ArtifactsConfig {
            dir: artifacts_dir.display().to_string(),
        },
        transport: TransportConfig {
            kind: TransportKind::Mock,
            ..TransportConfig::default()
        },
        ..AppConfig::default()
    };
    config.tools.dotnet_tools_path = repo_root()
        .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
        .display()
        .to_string();
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn write_ipc_asset_config(
    game_path: Option<&Path>,
    resources_dir: &Path,
    artifacts_dir: &Path,
    mods_dir: Option<&Path>,
    socket_path: &Path,
    launch_executable: Option<&Path>,
    launch_working_dir: Option<&Path>,
) -> tempfile::NamedTempFile {
    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let mut config = AppConfig {
        game: sts2::GameConfig {
            path: game_path
                .map(|path| path.display().to_string())
                .unwrap_or_else(|| "auto".to_string()),
            resources_dir: Some(resources_dir.display().to_string()),
            mods_dir: mods_dir.map(|path| path.display().to_string()),
            launch_executable: launch_executable.map(|path| path.display().to_string()),
            launch_working_dir: launch_working_dir.map(|path| path.display().to_string()),
            ..sts2::GameConfig::default()
        },
        artifacts: sts2::ArtifactsConfig {
            dir: artifacts_dir.display().to_string(),
        },
        transport: TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.display().to_string()),
            ..TransportConfig::default()
        },
        ..AppConfig::default()
    };
    config.tools.dotnet_tools_path = repo_root()
        .join("dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj")
        .display()
        .to_string();
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");
    temp
}

fn write_png(path: &Path, rgba: [u8; 4]) {
    fs::create_dir_all(path.parent().expect("png parent")).expect("create png parent");
    image::save_buffer_with_format(path, &rgba, 1, 1, image::ColorType::Rgba8, ImageFormat::Png)
        .expect("write png");
}

fn image_bytes_for_format(rgba: [u8; 4], format: ImageFormat) -> Vec<u8> {
    let mut bytes = Vec::new();
    image::write_buffer_with_format(
        &mut std::io::Cursor::new(&mut bytes),
        &rgba,
        1,
        1,
        image::ColorType::Rgba8,
        format,
    )
    .expect("image bytes");
    bytes
}

fn image_bytes_with_rect(
    width: u32,
    height: u32,
    rect: Option<(u32, u32, u32, u32)>,
    rgba: [u8; 4],
    format: ImageFormat,
) -> Vec<u8> {
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    if let Some((x, y, w, h)) = rect {
        for py in y..(y + h).min(height) {
            for px in x..(x + w).min(width) {
                let index = ((py * width + px) * 4) as usize;
                pixels[index..index + 4].copy_from_slice(&rgba);
            }
        }
    }
    let mut bytes = Vec::new();
    image::write_buffer_with_format(
        &mut std::io::Cursor::new(&mut bytes),
        &pixels,
        width,
        height,
        image::ColorType::Rgba8,
        format,
    )
    .expect("image bytes");
    bytes
}

fn write_packed_resource(path: &Path, entries: &[(&str, &[u8])]) {
    fs::create_dir_all(path.parent().expect("pck parent")).expect("create pck parent");

    const PACK_HEADER_MAGIC: u32 = 0x4350_4447;
    const PACK_VERSION: u32 = 3;
    const HEADER_LENGTH: u64 = 40;

    let mut data_offset = HEADER_LENGTH;
    let directory_offset = HEADER_LENGTH
        + entries
            .iter()
            .map(|(_, bytes)| bytes.len() as u64)
            .sum::<u64>();

    let mut file = File::create(path).expect("create pck");
    file.write_all(&PACK_HEADER_MAGIC.to_le_bytes())
        .expect("write magic");
    file.write_all(&PACK_VERSION.to_le_bytes())
        .expect("write version");
    file.write_all(&0u32.to_le_bytes())
        .expect("write version major");
    file.write_all(&0u32.to_le_bytes())
        .expect("write version minor");
    file.write_all(&0u32.to_le_bytes())
        .expect("write version patch");
    file.write_all(&0u32.to_le_bytes()).expect("write flags");
    file.write_all(&0u64.to_le_bytes())
        .expect("write file base");
    file.write_all(&directory_offset.to_le_bytes())
        .expect("write directory offset");

    let mut offsets = Vec::with_capacity(entries.len());
    for (_, bytes) in entries {
        offsets.push(data_offset);
        file.write_all(bytes).expect("write entry bytes");
        data_offset += bytes.len() as u64;
    }

    file.write_all(&(entries.len() as i32).to_le_bytes())
        .expect("write file count");
    for ((logical_path, bytes), offset) in entries.iter().zip(offsets.iter().copied()) {
        let path_bytes = format!("{logical_path}\0").into_bytes();
        file.write_all(&(path_bytes.len() as u32).to_le_bytes())
            .expect("write path len");
        file.write_all(&path_bytes).expect("write path");
        file.write_all(&offset.to_le_bytes()).expect("write offset");
        file.write_all(&(bytes.len() as u64).to_le_bytes())
            .expect("write size");
        file.write_all(&[0u8; 16]).expect("write hash");
        file.write_all(&0u32.to_le_bytes())
            .expect("write file flags");
    }
}

fn create_fake_game_layout() -> tempfile::TempDir {
    let game_dir = tempfile::tempdir().expect("game dir");
    let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
    let mods_dir = game_dir.path().join("mods");
    fs::create_dir_all(&assemblies_dir).expect("create assemblies dir");
    fs::create_dir_all(&mods_dir).expect("create mods dir");
    fs::write(assemblies_dir.join("sts2.dll"), []).expect("write sts2 dll");
    fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("write godot dll");
    game_dir
}

#[cfg(unix)]
fn write_executable_script(dir: &Path, name: &str, contents: &str) -> PathBuf {
    use std::os::unix::fs::PermissionsExt;

    let path = dir.join(name);
    fs::write(&path, contents).expect("write script");
    let mut permissions = fs::metadata(&path).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&path, permissions).expect("chmod script");
    path
}

#[cfg(unix)]
fn current_dotnet_path() -> String {
    let output = Command::new("sh")
        .args(["-c", "command -v dotnet"])
        .output()
        .expect("locate dotnet");
    assert!(
        output.status.success(),
        "expected dotnet on PATH, stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout)
        .expect("dotnet path utf8")
        .trim()
        .to_owned()
}

struct AssetExtractBridgeService {
    rgba: [u8; 4],
    force_png_response: bool,
    failure_source_substring: Option<String>,
    timeline_source_substring: Option<String>,
    rpc_unimplemented: bool,
    render_mode_override: Option<String>,
    notes_override: Option<Vec<String>>,
    expected_load_paths: Option<Vec<String>>,
}

#[tonic::async_trait]
impl BridgeService for AssetExtractBridgeService {
    impl_state_watch_unimplemented!();
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

    async fn get_models(
        &self,
        request: tonic::Request<ModelCatalogRequest>,
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

    async fn execute_action(
        &self,
        request: tonic::Request<ActionRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ActionResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.execute_action(request.into_inner()),
        ))
    }

    async fn execute_console_command(
        &self,
        request: tonic::Request<sts2::bridge::proto::ConsoleCommandRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::ConsoleCommandResult>, tonic::Status> {
        let service = StubBridgeService::new(MockScenario::MainMenu);
        Ok(tonic::Response::new(
            service.execute_console_command(request.into_inner()),
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
        request: tonic::Request<AssetExtractRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetExtractResult>, tonic::Status> {
        if self.rpc_unimplemented {
            return Err(tonic::Status::unimplemented(
                "ExtractAsset is not implemented by this bridge host.",
            ));
        }
        let request = request.into_inner();
        if let Some(expected) = self.expected_load_paths.as_ref() {
            assert!(
                expected.contains(&request.load_path),
                "unexpected extract load_path {}; expected one of {:?}",
                request.load_path,
                expected
            );
            assert!(
                matches!(
                    request.source_root.as_str(),
                    "model" | "composed" | "virtual"
                ),
                "unexpected source_root {}",
                request.source_root
            );
            assert_eq!(request.source_path, request.load_path);
        }
        if self
            .failure_source_substring
            .as_deref()
            .is_some_and(|substring| request.source_path.contains(substring))
        {
            let diagnostic = request
                .source_path
                .starts_with("composed://encounters/")
                .then(|| Struct {
                    fields: [
                        (
                            "renderTargetId".to_string(),
                            ProtoValue {
                                kind: Some(Kind::StringValue("rt-kaiser-overlay".to_string())),
                            },
                        ),
                        (
                            "frame".to_string(),
                            ProtoValue {
                                kind: Some(Kind::StructValue(Struct {
                                    fields: [
                                        (
                                            "x".to_string(),
                                            ProtoValue {
                                                kind: Some(Kind::NumberValue(1135.0)),
                                            },
                                        ),
                                        (
                                            "y".to_string(),
                                            ProtoValue {
                                                kind: Some(Kind::NumberValue(150.0)),
                                            },
                                        ),
                                    ]
                                    .into_iter()
                                    .collect(),
                                })),
                            },
                        ),
                        (
                            "pixelEvidence".to_string(),
                            ProtoValue {
                                kind: Some(Kind::StructValue(Struct {
                                    fields: [
                                        (
                                            "alphaNonZero".to_string(),
                                            ProtoValue {
                                                kind: Some(Kind::BoolValue(false)),
                                            },
                                        ),
                                        (
                                            "rgbNonZeroBeforeAlphaNormalization".to_string(),
                                            ProtoValue {
                                                kind: Some(Kind::BoolValue(true)),
                                            },
                                        ),
                                    ]
                                    .into_iter()
                                    .collect(),
                                })),
                            },
                        ),
                        (
                            "readyHookRan".to_string(),
                            ProtoValue {
                                kind: Some(Kind::BoolValue(true)),
                            },
                        ),
                        (
                            "spinePreviewSetupRan".to_string(),
                            ProtoValue {
                                kind: Some(Kind::BoolValue(false)),
                            },
                        ),
                    ]
                    .into_iter()
                    .collect(),
                });
            return Ok(tonic::Response::new(sts2::bridge::proto::AssetExtractResult {
                result: Some(sts2::bridge::proto::asset_extract_result::Result::Error(
                            sts2::bridge::proto::BridgeError {
                        code: sts2::bridge::proto::BridgeErrorCode::RuntimeFailure as i32,
                        message:
                            "The live bridge could not render the requested asset.".to_string(),
                        details: vec![sts2::bridge::proto::ErrorDetail {
                            field: "resource_type".to_string(),
                            value: "UnsupportedResource".to_string(),
                            note: "Only Texture2D, AnimatedTexture, SpriteFrames, StyleBoxTexture, and PackedScene resources are supported in this live asset extraction spec.".to_string(),
                            diagnostic,
                            ..Default::default()
                        }],
                        action_failure: None,
                    },
                )),
            }));
        }
        let response_format = if self.force_png_response {
            "png".to_string()
        } else {
            request.output_format.clone()
        };
        if self
            .timeline_source_substring
            .as_deref()
            .is_some_and(|substring| request.source_path.contains(substring))
        {
            let frames = vec![
                sts2::bridge::proto::AssetTimelineFrame {
                    index: 0,
                    format: response_format.clone(),
                    content_type: String::new(),
                    width: 1,
                    height: 1,
                    contents: image_bytes_for_format([255, 0, 0, 255], ImageFormat::Png),
                    duration_ms: 120,
                    offset_x: 0,
                    offset_y: 0,
                    canvas_width: 0,
                    canvas_height: 0,
                },
                sts2::bridge::proto::AssetTimelineFrame {
                    index: 1,
                    format: response_format.clone(),
                    content_type: String::new(),
                    width: 1,
                    height: 1,
                    contents: image_bytes_for_format([0, 0, 255, 255], ImageFormat::Png),
                    duration_ms: 80,
                    offset_x: 0,
                    offset_y: 0,
                    canvas_width: 0,
                    canvas_height: 0,
                },
            ];
            return Ok(tonic::Response::new(
                sts2::bridge::proto::AssetExtractResult {
                    result: Some(sts2::bridge::proto::asset_extract_result::Result::Success(
                        sts2::bridge::proto::AssetExtractResponse {
                            request_id: request.request_id,
                            source: sts2::bridge::proto::DataSource::Live as i32,
                            provisional: false,
                            format: response_format,
                            content_type: String::new(),
                            width: 1,
                            height: 1,
                            contents: Vec::new(),
                            render_mode: "timeline-frames".to_string(),
                            provenance: None,
                            placement_metadata: None,
                            notices: Vec::new(),
                            notes: vec!["Rendered timeline through test asset bridge.".to_string()],
                            artifact_kind: sts2::bridge::proto::AssetArtifactKind::Timeline as i32,
                            duration_ms: 200,
                            frame_count: 2,
                            frames,
                        },
                    )),
                },
            ));
        }
        let (format, content_type) = match response_format.as_str() {
            "png" => (ImageFormat::Png, "image/png"),
            _ => (ImageFormat::WebP, "image/webp"),
        };
        let (width, height, contents) = if request.source_path
            == "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image"
        {
            (
                1920,
                1080,
                image_bytes_with_rect(1920, 1080, Some((1135, 150, 535, 690)), self.rgba, format),
            )
        } else if request.source_path
            == "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
        {
            (
                1920,
                1080,
                image_bytes_with_rect(1920, 1080, Some((0, 120, 1920, 760)), self.rgba, format),
            )
        } else if request.source_path == "composed://encounters/kaiser_crab_boss/background/image" {
            (
                1920,
                1080,
                image_bytes_with_rect(1920, 1080, Some((0, 0, 1920, 1080)), self.rgba, format),
            )
        } else {
            (1, 1, image_bytes_for_format(self.rgba, format))
        };
        Ok(tonic::Response::new(
            sts2::bridge::proto::AssetExtractResult {
                result: Some(sts2::bridge::proto::asset_extract_result::Result::Success(
                    sts2::bridge::proto::AssetExtractResponse {
                        request_id: request.request_id,
                        source: sts2::bridge::proto::DataSource::Live as i32,
                        provisional: false,
                        format: response_format,
                        content_type: content_type.to_string(),
                        width,
                        height,
                        contents,
                        render_mode: self
                            .render_mode_override
                            .clone()
                            .unwrap_or_else(|| "flattened-first-frame".to_string()),
                        provenance: Some(sts2::bridge::proto::AssetExtractProvenance {
                            source_root: request.source_root.clone(),
                            source_path: request.source_path.clone(),
                            load_path: request.load_path.clone(),
                            render_mode: "flattened-first-frame".to_string(),
                            source_kind: "virtual-query".to_string(),
                        }),
                        placement_metadata: None,
                        notices: vec![sts2::bridge::proto::AssetExtractNotice {
                            code: "asset-key-partial".to_string(),
                            severity: "info".to_string(),
                            path: "render/overlay".to_string(),
                            message: "Rendered with fallback selector.".to_string(),
                        }],
                        notes: self.notes_override.clone().unwrap_or_else(|| {
                            vec!["Rendered through test asset bridge.".to_string()]
                        }),
                        artifact_kind: sts2::bridge::proto::AssetArtifactKind::Raster as i32,
                        duration_ms: 0,
                        frame_count: 0,
                        frames: Vec::new(),
                    },
                )),
            },
        ))
    }

    async fn explain_asset(
        &self,
        request: tonic::Request<AssetExplainRequest>,
    ) -> Result<tonic::Response<sts2::bridge::proto::AssetExplainResult>, tonic::Status> {
        let request = request.into_inner();
        if let Some(expected) = self.expected_load_paths.as_ref() {
            assert_eq!(
                expected,
                &vec![request.load_path.clone()],
                "unexpected explain load_path"
            );
            assert!(
                matches!(request.source_root.as_str(), "composed" | "virtual"),
                "unexpected source_root {}",
                request.source_root
            );
            assert_eq!(request.source_path, request.load_path);
        }
        let is_encounter =
            request.load_path == "composed://encounters/kaiser_crab_boss/scene-package";
        Ok(tonic::Response::new(
            sts2::bridge::proto::AssetExplainResult {
                result: Some(sts2::bridge::proto::asset_explain_result::Result::Success(
                    sts2::bridge::proto::AssetExplainResponse {
                        request_id: request.request_id,
                        source: sts2::bridge::proto::DataSource::Live as i32,
                        provisional: false,
                        schema_version: if is_encounter { "0" } else { "" }.to_string(),
                        explanation_kind: if is_encounter {
                            "encounter-scene-package"
                        } else {
                            ""
                        }
                        .to_string(),
                        encounter_scene_package: is_encounter.then(|| {
                            sts2::bridge::proto::AssetEncounterScenePackage {
                                schema_version: "0".to_string(),
                                encounter_id: "kaiser_crab_boss".to_string(),
                                viewport: Some(sts2::bridge::proto::AssetEncounterViewport {
                                    width: 1920,
                                    height: 1080,
                                    coordinate_space: "game-viewport".to_string(),
                                }),
                                camera: Some(sts2::bridge::proto::AssetEncounterCamera {
                                    scale: 0.75,
                                    offset: Some(sts2::bridge::proto::AssetVector2 {
                                        x: 0.0,
                                        y: 35.0,
                                    }),
                                    source: "EncounterModel.GetCameraScaling/GetCameraOffset"
                                        .to_string(),
                                    provenance:
                                        "NKaiserCrabBossEncounterModel.GetCameraScaling"
                                            .to_string(),
                                }),
                                background: Some(sts2::bridge::proto::AssetEncounterBackground {
                                    source_scene:
                                        "res://scenes/backgrounds/kaiser_crab_boss/kaiser_crab_boss_background.tscn"
                                            .to_string(),
                                    source_query:
                                        "composed://combat-background/kaiser_crab_boss/image".to_string(),
                                    render_query:
                                        "composed://encounters/kaiser_crab_boss/background/image".to_string(),
                                }),
                                logical_actors: vec![
                                    sts2::bridge::proto::AssetEncounterLogicalActor {
                                        actor_id: "rocket".to_string(),
                                        slot_id: "rocket".to_string(),
                                        target_rect: Some(
                                            sts2::bridge::proto::AssetCompositionRect {
                                                x: 1180.0,
                                                y: 430.0,
                                                width: 260.0,
                                                height: 280.0,
                                            },
                                        ),
                                        state_part_ids: vec!["rocket".to_string()],
                                    },
                                ],
                                visual_parts: vec![sts2::bridge::proto::AssetEncounterVisualPart {
                                    part_id: "rocket".to_string(),
                                    actor_id: "rocket".to_string(),
                                    screen_side: "right".to_string(),
                                    anatomical_side: "right".to_string(),
                                    layer: 20,
                                    viewport_rect: Some(
                                        sts2::bridge::proto::AssetCompositionRect {
                                            x: 1120.0,
                                            y: 260.0,
                                            width: 420.0,
                                            height: 520.0,
                                        },
                                    ),
                                    selector_diagnostic: Some(
                                        sts2::bridge::proto::AssetEncounterSelectorDiagnostic {
                                            part_id: "rocket".to_string(),
                                            selector: "%Rocket".to_string(),
                                            normalized_selectors: vec![
                                                "%Rocket".to_string(),
                                                "Rocket".to_string(),
                                            ],
                                            candidates: vec![
                                                sts2::bridge::proto::AssetEncounterSelectorCandidate {
                                                    path:
                                                        "/root/Combat/Enemies/KaiserCrab/Rocket"
                                                            .to_string(),
                                                    selector: "%Rocket".to_string(),
                                                    source: "catalog".to_string(),
                                                    status: "resolved".to_string(),
                                                },
                                            ],
                                            resolved_node: Some(
                                                sts2::bridge::proto::AssetEncounterResolvedNode {
                                                    path:
                                                        "/root/Combat/Enemies/KaiserCrab/Rocket"
                                                            .to_string(),
                                                    name: "Rocket".to_string(),
                                                    r#type: "Node2D".to_string(),
                                                },
                                            ),
                                            local_bounds: Some(
                                                sts2::bridge::proto::AssetCompositionRect {
                                                    x: -12.0,
                                                    y: -20.0,
                                                    width: 128.0,
                                                    height: 180.0,
                                                },
                                            ),
                                            visible_bounds: Some(
                                                sts2::bridge::proto::AssetCompositionRect {
                                                    x: 1122.0,
                                                    y: 262.0,
                                                    width: 390.0,
                                                    height: 500.0,
                                                },
                                            ),
                                            status: "resolved".to_string(),
                                            target_state_id: "rocket-charge-up".to_string(),
                                            render_target_id: "rocket-charge-up-overlay".to_string(),
                                        },
                                    ),
                                }],
                                states: vec![sts2::bridge::proto::AssetEncounterVisualState {
                                    state_id: "rocket-charge-up".to_string(),
                                    affected_part_ids: vec!["rocket".to_string()],
                                }],
                                transitions: vec![
                                    sts2::bridge::proto::AssetEncounterVisualTransition {
                                        transition_id: "rocket-charge-up".to_string(),
                                        hook: "PlayRightSideChargeUpAnim".to_string(),
                                        affected_part_ids: vec!["rocket".to_string()],
                                        active_state_id: "rocket-charge-up".to_string(),
                                    },
                                ],
                                render_targets: vec![
                                    sts2::bridge::proto::AssetEncounterRenderTarget {
                                        target_id: "rocket-charge-up-overlay".to_string(),
                                        kind: "overlay".to_string(),
                                        query:
                                            "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
                                                .to_string(),
                                        decision: Some(
                                            sts2::bridge::proto::AssetEncounterRenderTargetDecision {
                                                target_id: "rocket-charge-up-overlay".to_string(),
                                                kind: "overlay".to_string(),
                                                state_id: "rocket-charge-up".to_string(),
                                                part_id: String::new(),
                                                decision: "render-overlay".to_string(),
                                                reason: "state-affects-parts".to_string(),
                                                affected_part_ids: vec!["rocket".to_string()],
                                            },
                                        ),
                                    },
                                ],
                                notices: vec![sts2::bridge::proto::AssetExplainNotice {
                                    code: "provisional-contract".to_string(),
                                    severity: "info".to_string(),
                                    path: "encounterVisuals".to_string(),
                                    message: "Encounter visual packages are provisional."
                                        .to_string(),
                                    provisional: true,
                                }],
                                selector_diagnostics: vec![
                                    sts2::bridge::proto::AssetEncounterSelectorDiagnostic {
                                        part_id: "missing-claw".to_string(),
                                        selector: "%MissingClaw".to_string(),
                                        normalized_selectors: vec!["%MissingClaw".to_string()],
                                        candidates: vec![
                                            sts2::bridge::proto::AssetEncounterSelectorCandidate {
                                                path:
                                                    "/root/Combat/Enemies/KaiserCrab/MissingClaw"
                                                        .to_string(),
                                                selector: "%MissingClaw".to_string(),
                                                source: "catalog".to_string(),
                                                status: "missing".to_string(),
                                            },
                                        ],
                                        resolved_node: None,
                                        local_bounds: None,
                                        visible_bounds: None,
                                        status: "missing".to_string(),
                                        target_state_id: "rocket-charge-up".to_string(),
                                        render_target_id: "missing-claw-overlay".to_string(),
                                    },
                                ],
                            }
                        }),
                        root_scene: Some(sts2::bridge::proto::AssetCompositionRootScene {
                            background_id: "overgrowth".to_string(),
                            path: "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"
                                .to_string(),
                            load_source: "live-host".to_string(),
                        }),
                        placeholders: vec![sts2::bridge::proto::AssetCompositionPlaceholder {
                            name: "Layer_00".to_string(),
                            node_path: "/OvergrowthBackground/Layer_00".to_string(),
                            order: 0,
                            matched: true,
                        }],
                        layer_groups: vec![sts2::bridge::proto::AssetCompositionLayerGroup {
                            name: "Layer_00".to_string(),
                            placeholder: "Layer_00".to_string(),
                            candidate_count: 1,
                            candidates: vec![
                                "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"
                                    .to_string(),
                            ],
                            selected_path:
                                "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"
                                    .to_string(),
                            selection_source: "deterministic-first-sorted".to_string(),
                            order: 0,
                        }],
                        selected_layers: vec![sts2::bridge::proto::AssetCompositionSelectedLayer {
                            placeholder: "Layer_00".to_string(),
                            path:
                                "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"
                                    .to_string(),
                            order: 0,
                            load_status: "loaded".to_string(),
                            texture_refs: vec!["res://textures/overgrowth_bg_00_a.webp".to_string()],
                            local_bounds: Some(sts2::bridge::proto::AssetCompositionRect {
                                x: 0.0,
                                y: 0.0,
                                width: 1920.0,
                                height: 1080.0,
                            }),
                            visible_bounds: Some(sts2::bridge::proto::AssetCompositionRect {
                                x: 0.0,
                                y: 0.0,
                                width: 1880.0,
                                height: 1012.0,
                            }),
                        }],
                        bounds: Some(sts2::bridge::proto::AssetCompositionBounds {
                            viewport: Some(sts2::bridge::proto::AssetCompositionRect {
                                x: 0.0,
                                y: 0.0,
                                width: 1920.0,
                                height: 1080.0,
                            }),
                            final_composed: Some(sts2::bridge::proto::AssetCompositionRect {
                                x: 0.0,
                                y: 0.0,
                                width: 1920.0,
                                height: 1080.0,
                            }),
                            final_visible: Some(sts2::bridge::proto::AssetCompositionRect {
                                x: 0.0,
                                y: 0.0,
                                width: 1880.0,
                                height: 1012.0,
                            }),
                            transparent_pixel_ratio: 0.07,
                        }),
                        render: Some(sts2::bridge::proto::AssetCompositionRenderPlan {
                            render_mode: "flattened-combat-background-composed".to_string(),
                            warmup_frames: 3,
                            trim_transparent_bounds: false,
                            transparent_crop_padding: 0,
                            notes: vec![
                                "Rendered composed combat background at viewport framing using runtime BgContainer placement: centered, x-offset 23, scale 0.9, preserved root Control size."
                                    .to_string(),
                            ],
                        }),
                        active_scene: Some(sts2::bridge::proto::AssetCompositionActiveScene {
                            status: "not-active".to_string(),
                            matched_root: false,
                            observed_layer_paths: Vec::new(),
                            differences: Vec::new(),
                        }),
                        warnings: vec![sts2::bridge::proto::AssetCompositionWarning {
                            code: "missing-layer".to_string(),
                            severity: "warning".to_string(),
                            message: "Placeholder Layer_02 did not have a matching deterministic layer scene."
                                .to_string(),
                            details: [("placeholder".to_string(), "Layer_02".to_string())]
                                .into_iter()
                                .collect(),
                        }],
                    },
                )),
            },
        ))
    }
}
