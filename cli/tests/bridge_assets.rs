use sts2::bridge::proto;
use sts2::bridge::{RuntimeBridgeClient, StubBridgeService, bridge_error_code_name};
use sts2::{MockScenario, TransportConfig, TransportKind};

#[test]
fn stub_bridge_service_asset_extract_requires_live_bridge_host() {
    let service = StubBridgeService::new(MockScenario::MainMenu);

    let result = service.extract_asset(proto::AssetExtractRequest {
        request_id: "asset-1".to_string(),
        source_root: "resources".to_string(),
        source_path: "ui/shared/hand_panel.tscn".to_string(),
        load_path: "res://ui/shared/hand_panel.tscn".to_string(),
        output_format: "png".to_string(),
    });

    let error = match result.result {
        Some(proto::asset_extract_result::Result::Error(error)) => error,
        other => panic!("expected asset extract error, got {other:?}"),
    };
    assert_eq!(
        bridge_error_code_name(proto::BridgeErrorCode::try_from(error.code).expect("error code")),
        "not_implemented"
    );
    assert_eq!(error.details[0].field, "command");
    assert_eq!(error.details[0].value, "assets extract");
}

#[test]
fn runtime_bridge_client_mock_asset_extract_returns_bridge_error() {
    let client = RuntimeBridgeClient::from_config(&TransportConfig {
        kind: TransportKind::Mock,
        mock_scenario: MockScenario::Combat,
        ..TransportConfig::default()
    });

    let error = client
        .extract_asset(proto::AssetExtractRequest {
            request_id: "asset-2".to_string(),
            source_root: "resources".to_string(),
            source_path: "ui/shared/hand_panel.tscn".to_string(),
            load_path: "res://ui/shared/hand_panel.tscn".to_string(),
            output_format: "png".to_string(),
        })
        .expect_err("mock transport should reject live asset extraction");

    assert_eq!(
        bridge_error_code_name(proto::BridgeErrorCode::try_from(error.code).expect("error code")),
        "not_implemented"
    );
}
