#[cfg(unix)]
fn run_json_in_dir_with_env(workdir: &Path, args: &[&str], envs: &[(&str, &str)]) -> Value {
    let output = Command::new(sts2_bin())
        .args(args)
        .current_dir(workdir)
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

fn write_scenario(contents: &str) -> tempfile::NamedTempFile {
    let file = tempfile::Builder::new()
        .suffix(".sts2.yaml")
        .tempfile()
        .expect("scenario temp file");
    fs::write(file.path(), contents).expect("write scenario");
    file
}

fn write_json_scenario(contents: &str) -> tempfile::NamedTempFile {
    let file = tempfile::Builder::new()
        .suffix(".sts2.json")
        .tempfile()
        .expect("json scenario temp file");
    fs::write(file.path(), contents).expect("write json scenario");
    file
}

fn write_fixture(path: &Path) {
    fs::write(
        path,
        r#"schemaVersion: spirectl.fixture/v0
name: basic-combat
run:
  players:
    - id: p1
      characterId: IRONCLAD
      creature:
        currentHp: 67
        maxHp: 80
  currentRoom:
    combat:
      encounterId: NIBBITS_NORMAL
"#,
    )
    .expect("write fixture");
}

fn write_hot_reload_project(path: &Path) {
    fs::create_dir_all(path).expect("create hot reload project");
    fs::write(
        path.join("sts2.hot-reload.yaml"),
        "schemaVersion: spirectl.hot-reload-project/v0\nprojectId: hot\nshellModId: hot\nshellProject: Hot.Shell\nlogicProject: Hot.Logic\nlogicBuildProfile: hot-logic-build\nlogicArtifactPath: ${projectRoot}/Hot.Logic.dll\nexpectedContractVersion: 0\nprotocol:\n  id: spirectl.m57.hot-reload-shell\n  version: 0\n",
    )
    .expect("write hot reload metadata");
}

fn write_sparse_shop_scenario(path: &Path) {
    fs::write(
        path,
        r#"schemaVersion: spirectl.scenario/v0
name: mock-shop
createdAt: "2026-04-24T00:00:00Z"
source:
  gameVersion: mock-game/0
  bridgeVersion: spirectl-bridge/0.0.0
  spirectlVersion: 0.0.0
  screen:
    id: Screens.Shops.NMerchantInventory
    title: Shop
    instanceId: screen:shop:1
  perspective:
    scope: local
    playerId: p1
restore:
  mode: sparse
  quality: partial
  fallbackPolicy: fall-back-to-sparse-with-degraded-quality
  fieldReports:
    # state has no top-level `choices` document to restore-validate against
    # (the mock transport's `dev.load-scenario` path doesn't populate
    # `run.currentRoom.shop` either — see cli/src/scenario_checkpoint/
    # validation.rs `extract_restore_path`'s "choices[].id" arm, which reads a
    # legacy-only key). `run.seed` is real on both sides (mock always answers
    # "mock-seed") and exercises the same forced-comparison field-report
    # mechanism this fixture is here to prove.
    - path: run.seed
      capture: exact
      restore: inferred
      validationKey: true
      reasonCode: shop-seed
      message: Run seed is captured from the live run and restored through fixture recipes.
  compatibilityNotes: []
run:
  seed: mock-seed
  act: 1
  floor: 5
  players:
    - id: p1
      character: IRONCLAD
      isLocal: true
      isHost: true
      isRemote: false
screenState:
  shop:
    gold: 123
    choiceIds:
      - shop:p1:card:strike:0
      - shop:p1:relic:anchor:0
      - shop:p1:potion:fire-potion:0
      - shop:p1:card-removal:0
      - shop:leave
    availableActionKinds:
      - choose
  choices:
    - id: shop:p1:card:strike:0
      label: Jab
      ownerPlayerId: p1
    - id: shop:p1:relic:anchor:0
      label: Ballast
      ownerPlayerId: p1
    - id: shop:p1:potion:fire-potion:0
      label: Ember Draught
      ownerPlayerId: p1
    - id: shop:p1:card-removal:0
      label: Remove
      ownerPlayerId: p1
    - id: shop:leave
      label: Leave Shop
  availableActions:
    - kind: choose
      arguments:
        choiceId: shop:relic:anchor:0
notices: []
"#,
    )
    .expect("write sparse scenario");
}

fn write_active_multiplayer_scenario(path: &Path) {
    fs::write(
        path,
        r#"schemaVersion: spirectl.scenario/v0
name: active-multiplayer-combat
createdAt: "2026-04-24T00:00:00Z"
source:
  gameVersion: test-game
  bridgeVersion: test-bridge
  spirectlVersion: test-cli
  screen:
    id: combat
    title: Combat
    instanceId: screen:combat:1
  perspective:
    scope: local
    playerId: p1
restore:
  mode: sparse
  quality: degraded
  fallbackPolicy: fall-back-to-sparse-with-degraded-quality
multiplayer:
  isMultiplayer: true
  restoreMode: degraded-local-only
  localPlayerId: p:100
  hostPlayerId: p:100
  localPlayerRole: host
  requiresRemoteClients: true
  degradedLocalOnlyAvailable: true
  players:
    - id: p:100
      netId: "100"
      slotId: 0
      selectedCharacterId: ironclad
      character: IRONCLAD
      isReady: true
      isLocal: true
      isHost: true
      isRemote: false
    - id: p:200
      netId: "200"
      slotId: 1
      selectedCharacterId: silent
      character: SILENT
      isReady: true
      isLocal: false
      isHost: false
      isRemote: true
run:
  seed: MIL2-CONTRACT-SEED
  act: 1
  floor: 3
screenState:
  screen:
    id: combat
  combat:
    turn: 1
notices: []
"#,
    )
    .expect("write active multiplayer scenario");
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

macro_rules! impl_state_watch_and_models_unimplemented {
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

        impl_unimplemented_bridge_rpc!(
            inspect_presentation_localization,
            sts2::bridge::proto::PresentationLocalizationRequest,
            sts2::bridge::proto::PresentationLocalizationResult,
            "presentation localization inspection is not exercised by this test service"
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

macro_rules! impl_hot_reload_unimplemented_rpc_stubs {
    () => {
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
    };
}

fn write_png(path: &Path, rgba: [u8; 4]) {
    let image = image::RgbaImage::from_pixel(2, 2, image::Rgba(rgba));
    image.save(path).expect("write png");
}

fn write_probe_server_script(path: &Path) {
    fs::write(
        path,
        r#"#!/usr/bin/env python3
import http.server
import json
import socketserver
import sys

state = {"count": 0}

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        state["count"] += 1
        if self.path == "/health":
            if state["count"] == 1:
                payload = {"status": "starting"}
                self.send_response(503)
            else:
                payload = {"status": "ok"}
                self.send_response(200)
            body = json.dumps(payload).encode("utf-8")
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        self.send_response(404)
        self.end_headers()

    def log_message(self, format, *args):
        return

with socketserver.TCPServer(("127.0.0.1", 0), Handler) as httpd:
    print(httpd.server_address[1], flush=True)
    httpd.serve_forever()
"#,
    )
    .expect("write probe server");
}

fn write_config(config: &AppConfig) -> tempfile::NamedTempFile {
    let file = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(file.path(), serde_yaml::to_string(config).expect("yaml")).expect("write config");
    file
}

fn write_raw_config(config_text: &str) -> tempfile::NamedTempFile {
    let file = tempfile::NamedTempFile::new().expect("temp config");
    fs::write(file.path(), config_text).expect("write config");
    file
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

fn artifact_config(artifacts_dir: &Path, scenario: MockScenario) -> tempfile::NamedTempFile {
    write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: scenario,
            ..sts2::TransportConfig::default()
        },
        artifacts: sts2::ArtifactsConfig {
            dir: artifacts_dir.to_string_lossy().into_owned(),
        },
        ..AppConfig::default()
    })
}

fn read_json_file(path: &Path) -> Value {
    serde_json::from_str(&fs::read_to_string(path).expect("read json file")).expect("json file")
}

fn only_run_dir(artifacts_dir: &Path) -> PathBuf {
    let root = artifacts_dir.join("test-runs");
    let mut entries = fs::read_dir(&root)
        .expect("test-runs dir")
        .map(|entry| entry.expect("dir entry").path())
        .collect::<Vec<_>>();
    entries.sort();
    assert_eq!(entries.len(), 1, "expected a single test run dir");
    entries.remove(0)
}

fn yaml_mapping<'a>(value: &'a YamlValue, context: &str) -> &'a serde_yaml::Mapping {
    value
        .as_mapping()
        .unwrap_or_else(|| panic!("{context} should be a mapping"))
}

fn yaml_sequence<'a>(value: &'a YamlValue, context: &str) -> &'a Vec<YamlValue> {
    value
        .as_sequence()
        .unwrap_or_else(|| panic!("{context} should be a sequence"))
}

fn yaml_str<'a>(value: &'a YamlValue, context: &str) -> &'a str {
    value
        .as_str()
        .unwrap_or_else(|| panic!("{context} should be a string"))
}

#[cfg(unix)]
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
    let path = dir.join(name);
    fs::write(&path, contents).expect("write script");
    let mut permissions = fs::metadata(&path).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&path, permissions).expect("chmod script");
    path
}

#[cfg(unix)]
fn write_hook_script(dir: &Path, input_path: &Path, artifact_path: &Path) -> PathBuf {
    write_executable_script(
        dir,
        "hook.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\ncat > {input_path:?}\nprintf 'runner-hook\\n' > {artifact_path:?}\nprintf '{{\"output\":{{\"status\":\"ok\"}},\"artifacts\":[{{\"path\":%s,\"kind\":\"file\"}}]}}\\n' '\"{artifact}\"'\n",
            artifact = artifact_path.display()
        ),
    )
}
