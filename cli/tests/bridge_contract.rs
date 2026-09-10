// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]
#![allow(
    clippy::result_large_err,
    reason = "Bridge contract tests exercise the public protobuf BridgeError return shape by value."
)]

mod ipc_bridge_support;

use sts2::MockScenario;
use sts2::bridge::proto::bridge_service_client::BridgeServiceClient;
use sts2::bridge::proto::bridge_service_server::BridgeServiceServer;
use sts2::bridge::proto::{
    BridgeErrorCode, CheckpointListRequest, HandshakeRequest, LogsRequest, RecordedFixtureRequest,
    ReferenceRequest, ScenarioCaptureRequest, ScenarioRestoreRequest, StateRequest,
    StateWatchRequest,
};
use sts2::bridge::{RuntimeBridgeClient, StubBridgeGrpcService, bridge_error_code_name};
use sts2::{TransportConfig, TransportKind};
use tokio::net::{TcpListener, UnixListener};
use tokio_stream::wrappers::TcpListenerStream;
use tonic::Request;
use tonic::transport::Server;

#[tokio::test]
async fn generated_client_round_trips_stub_bridge_service() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = BridgeServiceServer::new(StubBridgeGrpcService::new(MockScenario::Combat));

    let server = tokio::spawn(async move {
        Server::builder()
            .add_service(service)
            .serve_with_incoming(TcpListenerStream::new(listener))
            .await
            .expect("serve bridge service");
    });

    let endpoint = format!("http://{address}");
    let mut client = BridgeServiceClient::connect(endpoint)
        .await
        .expect("connect gRPC client");

    let handshake = client
        .handshake(Request::new(HandshakeRequest {
            cli_version: "0.0.0".to_string(),
            requested_schema_version: "spirectl/v0".to_string(),
            mode: "normal".to_string(),
            transport_kind: sts2::bridge::proto::TransportKind::Mock as i32,
        }))
        .await
        .expect("handshake rpc")
        .into_inner();

    match handshake.result.expect("handshake result") {
        sts2::bridge::proto::handshake_result::Result::Success(response) => {
            assert_eq!(
                response.transport_kind,
                sts2::bridge::proto::TransportKind::Mock as i32
            );
        }
        _ => panic!("expected handshake success"),
    }

    let state = client
        .get_state(Request::new(StateRequest { perspective: None }))
        .await
        .expect("state rpc")
        .into_inner();

    let state = match state.result.expect("state result") {
        sts2::bridge::proto::state_result::Result::Success(response) => response,
        _ => panic!("expected state success"),
    };
    assert_eq!(state.schema_version, "spirectl.state/v0");
    assert!(!state.root_scene.is_empty());

    let logs = client
        .get_logs(Request::new(LogsRequest {
            limit: 0,
            minimum_level: 0,
            target_filter: String::new(),
            after_cursor: 0,
        }))
        .await
        .expect("logs rpc")
        .into_inner();

    match logs.result.expect("logs result") {
        sts2::bridge::proto::logs_result::Result::Error(error) => {
            assert_eq!(error.code, BridgeErrorCode::InvalidQueryFilter as i32);
        }
        _ => panic!("expected logs error"),
    }

    server.abort();
}

#[tokio::test]
async fn generated_client_round_trips_get_reference() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = BridgeServiceServer::new(StubBridgeGrpcService::new(MockScenario::MainMenu));

    let server = tokio::spawn(async move {
        Server::builder()
            .add_service(service)
            .serve_with_incoming(TcpListenerStream::new(listener))
            .await
            .expect("serve bridge service");
    });

    let endpoint = format!("http://{address}");
    let mut client = BridgeServiceClient::connect(endpoint)
        .await
        .expect("connect gRPC client");

    let colors = client
        .get_reference(Request::new(ReferenceRequest {
            request_id: "contract-colors".to_string(),
            topic: "colors".to_string(),
            keys: vec!["gold".to_string(), "missing".to_string()],
        }))
        .await
        .expect("reference colors rpc")
        .into_inner();

    match colors.result.expect("colors result") {
        sts2::bridge::proto::reference_result::Result::Success(response) => {
            assert_eq!(response.topic, "colors");
            assert_eq!(response.missing_keys, vec!["missing".to_string()]);
            match response.payload.expect("colors payload") {
                sts2::bridge::proto::reference_response::Payload::Colors(colors) => {
                    assert_eq!(colors.colors.len(), 1);
                    assert_eq!(colors.colors[0].name, "gold");
                    assert!(!colors.colors[0].hex.is_empty());
                }
                _ => panic!("expected colors payload"),
            }
        }
        _ => panic!("expected reference success"),
    }

    let version = client
        .get_reference(Request::new(ReferenceRequest {
            request_id: "contract-version".to_string(),
            topic: "version".to_string(),
            keys: Vec::new(),
        }))
        .await
        .expect("reference version rpc")
        .into_inner();

    match version.result.expect("version result") {
        sts2::bridge::proto::reference_result::Result::Success(response) => {
            assert_eq!(response.topic, "version");
            match response.payload.expect("version payload") {
                sts2::bridge::proto::reference_response::Payload::Version(info) => {
                    assert!(!info.version.is_empty());
                    assert!(info.modding.is_some());
                }
                _ => panic!("expected version payload"),
            }
        }
        _ => panic!("expected reference success"),
    }

    server.abort();
}
#[tokio::test]
async fn generated_client_exposes_scenario_checkpoint_contracts() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = BridgeServiceServer::new(StubBridgeGrpcService::new(MockScenario::MainMenu));

    let server = tokio::spawn(async move {
        Server::builder()
            .add_service(service)
            .serve_with_incoming(TcpListenerStream::new(listener))
            .await
            .expect("serve bridge service");
    });

    let endpoint = format!("http://{address}");
    let mut client = BridgeServiceClient::connect(endpoint)
        .await
        .expect("connect gRPC client");

    let scenario = client
        .capture_scenario(Request::new(ScenarioCaptureRequest {
            request_id: "test-scenario-capture".to_string(),
            schema_version: "spirectl.scenario/v0".to_string(),
            include_exact: false,
            perspective: None,
        }))
        .await
        .expect("capture scenario rpc")
        .into_inner();

    match scenario.result.expect("scenario result") {
        sts2::bridge::proto::scenario_capture_result::Result::Error(error) => {
            assert_eq!(error.code, BridgeErrorCode::InvalidAction as i32);
        }
        _ => panic!("expected unsupported mock scenario capture to fail"),
    }

    let checkpoints = client
        .list_checkpoints(Request::new(CheckpointListRequest {
            request_id: "test-checkpoint-list".to_string(),
        }))
        .await
        .expect("list checkpoints rpc")
        .into_inner();

    match checkpoints.result.expect("checkpoint result") {
        sts2::bridge::proto::checkpoint_list_result::Result::Error(error) => {
            assert_eq!(error.code, BridgeErrorCode::NotImplemented as i32);
        }
        _ => panic!("expected checkpoint list to be planned/not implemented"),
    }

    server.abort();
}

#[tokio::test]
async fn generated_client_exposes_record_fixture_contract() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = BridgeServiceServer::new(StubBridgeGrpcService::new(MockScenario::MainMenu));

    let server = tokio::spawn(async move {
        Server::builder()
            .add_service(service)
            .serve_with_incoming(TcpListenerStream::new(listener))
            .await
            .expect("serve bridge service");
    });

    let endpoint = format!("http://{address}");
    let mut client = BridgeServiceClient::connect(endpoint)
        .await
        .expect("connect gRPC client");

    let response = client
        .record_fixture(Request::new(RecordedFixtureRequest {
            request_id: "record-fixture-contract".to_string(),
            perspective: None,
        }))
        .await
        .expect("record fixture rpc")
        .into_inner();

    match response.result.expect("recorded fixture result") {
        sts2::bridge::proto::recorded_fixture_result::Result::Error(error) => {
            assert!(
                error.message.contains("not exercised")
                    || error.message.contains("unavailable")
                    || error.message.contains("unsupported")
            );
        }
        sts2::bridge::proto::recorded_fixture_result::Result::Success(success) => {
            assert!(!success.request_id.is_empty());
        }
    }

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn runtime_bridge_client_uses_ipc_transport_over_unix_socket() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-m3.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Ipc,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: Some(socket_path.to_string_lossy().into_owned()),
        pipe_name: None,
        tcp_address: None,
        rpc_timeout_ms: None,
    });

    let response =
        tokio::task::spawn_blocking(move || client.state(StateRequest { perspective: None }))
            .await
            .expect("join ipc state")
            .expect("ipc state");

    assert_eq!(response.schema_version, "spirectl.state/v0");
    assert!(!response.root_scene.is_empty());

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn runtime_bridge_client_streams_state_watch_over_unix_socket() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-watch-ipc.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Ipc,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: Some(socket_path.to_string_lossy().into_owned()),
        pipe_name: None,
        tcp_address: None,
        rpc_timeout_ms: None,
    });

    let events = tokio::task::spawn_blocking(move || {
        let mut events = Vec::new();
        client
            .watch_state(
                StateWatchRequest {
                    state: Some(StateRequest { perspective: None }),
                    max_events: 1,
                    timeout_ms: 0,
                    fail_fast: false,
                    min_capture_interval_ms: 0,
                    state_timeout_ms: 0,
                    buffer_capacity: 16,
                    emit_diff: false,
                },
                |event| {
                    events.push(event);
                    Ok(false)
                },
            )
            .expect("ipc watch state");
        events
    })
    .await
    .expect("join ipc watch");

    assert_eq!(events.len(), 1);
    assert_eq!(
        events[0].r#type,
        sts2::bridge::proto::StateWatchEventType::Initial as i32
    );
    assert!(matches!(
        events[0].payload,
        Some(sts2::bridge::proto::state_watch_event::Payload::State(_))
    ));

    server.abort();
}

#[tokio::test]
async fn runtime_bridge_client_uses_tcp_transport_over_framed_protocol() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_tcp_bridge_service(listener, service);

    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Tcp,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: None,
        pipe_name: None,
        tcp_address: Some(address.to_string()),
        rpc_timeout_ms: None,
    });

    let response =
        tokio::task::spawn_blocking(move || client.state(StateRequest { perspective: None }))
            .await
            .expect("join tcp state")
            .expect("tcp state");

    assert_eq!(response.schema_version, "spirectl.state/v0");
    assert!(!response.root_scene.is_empty());

    server.abort();
}

#[tokio::test]
async fn runtime_bridge_client_streams_state_watch_over_tcp_transport() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_tcp_bridge_service(listener, service);

    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Tcp,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: None,
        pipe_name: None,
        tcp_address: Some(address.to_string()),
        rpc_timeout_ms: None,
    });

    let events = tokio::task::spawn_blocking(move || {
        let mut events = Vec::new();
        client
            .watch_state(
                StateWatchRequest {
                    state: Some(StateRequest::default()),
                    max_events: 1,
                    timeout_ms: 0,
                    fail_fast: false,
                    min_capture_interval_ms: 0,
                    state_timeout_ms: 0,
                    buffer_capacity: 16,
                    emit_diff: false,
                },
                |event| {
                    events.push(event);
                    Ok(false)
                },
            )
            .expect("tcp watch state");
        events
    })
    .await
    .expect("join tcp watch");

    assert_eq!(events.len(), 1);
    assert_eq!(
        events[0].r#type,
        sts2::bridge::proto::StateWatchEventType::Initial as i32
    );

    server.abort();
}

#[tokio::test]
async fn runtime_bridge_client_routes_scenario_capture_and_restore_over_tcp_transport() {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind tcp listener");
    let address = listener.local_addr().expect("listener addr");
    let service = StubBridgeGrpcService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_tcp_bridge_service(listener, service);

    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Tcp,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: None,
        pipe_name: None,
        tcp_address: Some(address.to_string()),
        rpc_timeout_ms: None,
    });

    let capture_error = tokio::task::spawn_blocking({
        let client = client.clone();
        move || {
            client.capture_scenario(ScenarioCaptureRequest {
                request_id: "scenario-capture-tcp".to_string(),
                schema_version: "spirectl.scenario/v0".to_string(),
                include_exact: true,
                perspective: None,
            })
        }
    })
    .await
    .expect("join tcp scenario capture")
    .expect_err("scaffold capture remains unsupported until the provider is wired");

    assert_eq!(capture_error.code, BridgeErrorCode::InvalidAction as i32);

    let restore_error = tokio::task::spawn_blocking(move || {
        client.restore_scenario(ScenarioRestoreRequest {
            request_id: "scenario-restore-tcp".to_string(),
            schema_version: "spirectl.scenario/v0".to_string(),
            scenario: None,
            restart_requested: false,
            exact_bundle_payload: None,
            allow_sparse_fallback: true,
            allow_degraded_local_multiplayer: false,
        })
    })
    .await
    .expect("join tcp scenario restore")
    .expect_err("scaffold restore remains unsupported until the provider is wired");

    assert_eq!(restore_error.code, BridgeErrorCode::InvalidFixture as i32);

    server.abort();
}

#[test]
fn runtime_bridge_client_rejects_non_loopback_tcp_addresses() {
    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Tcp,
        mock_scenario: MockScenario::MainMenu,
        ipc_path: None,
        pipe_name: None,
        tcp_address: Some("192.168.1.10:51173".to_string()),
        rpc_timeout_ms: None,
    });

    let error = client
        .state(StateRequest { perspective: None })
        .expect_err("non-loopback tcp should be rejected");

    assert_eq!(
        bridge_error_code_name(BridgeErrorCode::try_from(error.code).expect("error code")),
        "transport_misconfigured"
    );
}
