use super::*;
#[cfg(unix)]
#[tokio::test]
async fn dev_scene_tree_returns_structured_success_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-runtime-scene-tree.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "tree",
            "/root/CombatScreen",
        ])
    })
    .await
    .expect("join scene tree task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["screen"]["id"], "combat");
    assert_eq!(payload["rootNodePath"], "/root/CombatScreen");
    assert_eq!(payload["nodes"][0]["nodePath"], "/root/CombatScreen");
    assert_eq!(payload["nodes"][1]["parentNodePath"], "/root/CombatScreen");
    server.abort();
}

#[test]
fn dev_debug_status_returns_structured_scaffold_payload() {
    let response = run(&["sts2", "--json", "dev", "debug", "status"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["supported"], false);
    assert_eq!(payload["executionState"], "unsupported");
    assert_eq!(payload["pauseReason"], "unsupported");
    assert_eq!(payload["canPause"], false);
    assert_eq!(payload["canResume"], false);
    assert_eq!(payload["breakpointManagementSupported"], true);
    assert_eq!(payload["breakpointEvaluationSupported"], false);
    assert_eq!(payload["sessionOwnership"], "unowned");
    assert_eq!(payload["activeSession"], Value::Null);
}

#[test]
fn dev_debug_step_reports_requested_step_kind_in_payload() {
    let response = run(&[
        "sts2", "--json", "dev", "debug", "step", "--kind", "action", "--count", "2",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["applied"], false);
    assert_eq!(payload["kind"], "action");
    assert_eq!(payload["requestedKind"], "action");
    assert_eq!(payload["count"], 2);
}

#[test]
fn dev_debug_session_start_returns_structured_stub_payload() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "debug",
        "session",
        "start",
        "--name",
        "m45",
        "--pause",
        "--lease-timeout-ms",
        "9000",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["started"], false);
    assert_eq!(payload["session"], Value::Null);
    assert_eq!(payload["status"]["sessionOwnership"], "unowned");
    assert_eq!(payload["notices"][0]["code"], "debug_session_unsupported");
}

#[cfg(unix)]
#[tokio::test]
async fn debugger_event_streams() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-debug-events.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server = ipc_bridge_support::spawn_unix_bridge_service(
        listener,
        DebuggerEventBridgeService::default(),
    );

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let config_path = config.path().to_str().expect("utf8 path").to_string();
    async fn run_ipc(args: Vec<String>) -> RenderedCommand {
        tokio::task::spawn_blocking(move || {
            let refs = args.iter().map(String::as_str).collect::<Vec<_>>();
            run(&refs)
        })
        .await
        .expect("join ipc cli task")
    }
    async fn run_ipc_error(args: Vec<String>) -> RenderedCommand {
        tokio::task::spawn_blocking(move || {
            let refs = args.iter().map(String::as_str).collect::<Vec<_>>();
            run_error(&refs)
        })
        .await
        .expect("join ipc cli task")
    }

    let controller = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "session".into(),
        "start".into(),
        "--name".into(),
        "controller".into(),
    ])
    .await;
    let controller_payload: Value = serde_json::from_str(&controller.stdout).expect("json");
    let controller_id = controller_payload["session"]["id"]
        .as_str()
        .expect("controller id")
        .to_string();
    assert_eq!(controller_payload["session"]["role"], "controller");

    let observer_one = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "session".into(),
        "start".into(),
        "--role".into(),
        "observer".into(),
        "--name".into(),
        "observer-one".into(),
    ])
    .await;
    let observer_one_payload: Value = serde_json::from_str(&observer_one.stdout).expect("json");
    assert_eq!(observer_one_payload["session"]["role"], "observer");

    let observer_two = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "session".into(),
        "start".into(),
        "--role".into(),
        "observer".into(),
        "--name".into(),
        "observer-two".into(),
    ])
    .await;
    let observer_two_payload: Value = serde_json::from_str(&observer_two.stdout).expect("json");
    assert_eq!(
        observer_two_payload["status"]["observerSessions"]
            .as_array()
            .unwrap()
            .len(),
        2
    );

    let second_controller = run_ipc_error(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "session".into(),
        "start".into(),
        "--name".into(),
        "second-controller".into(),
    ])
    .await;
    let second_controller_payload: Value =
        serde_json::from_str(&second_controller.stdout).expect("json");
    assert_eq!(second_controller.exit_code, 5);
    assert_eq!(
        second_controller_payload["error"]["message"],
        "debug controller lease conflict"
    );

    let _ = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "pause".into(),
        "--session".into(),
        controller_id.clone(),
    ])
    .await;
    let _ = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "resume".into(),
        "--session".into(),
        controller_id.clone(),
    ])
    .await;
    let _ = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "step".into(),
        "--session".into(),
        controller_id.clone(),
        "--kind".into(),
        "action".into(),
    ])
    .await;
    let _ = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "breakpoint".into(),
        "add".into(),
        "--session".into(),
        controller_id.clone(),
        "--path".into(),
        "screen.id".into(),
        "--equals".into(),
        "\"combat\"".into(),
    ])
    .await;

    let events = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "events".into(),
        "--session".into(),
        controller_id.clone(),
        "--from-sequence".into(),
        "2".into(),
        "--limit".into(),
        "20".into(),
    ])
    .await;
    let events_payload: Value = serde_json::from_str(&events.stdout).expect("json");
    let kinds = events_payload["events"]
        .as_array()
        .expect("events")
        .iter()
        .map(|event| event["kind"].as_str().expect("kind"))
        .collect::<Vec<_>>();
    assert!(kinds.contains(&"observer-attached"));
    assert!(kinds.contains(&"paused"));
    assert!(kinds.contains(&"resumed"));
    assert!(kinds.contains(&"stepped"));
    assert!(kinds.contains(&"breakpoint-hit"));
    assert_eq!(events_payload["fromSequence"], 2);
    assert!(events_payload["nextSequence"].as_u64().unwrap() > 2);
    assert_eq!(events_payload["retention"]["limit"], 32);

    let next_sequence = events_payload["nextSequence"].as_u64().unwrap().to_string();
    let follow = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "dev".into(),
        "debug".into(),
        "events".into(),
        "--session".into(),
        controller_id.clone(),
        "--from-sequence".into(),
        next_sequence,
        "--follow".into(),
        "--timeout-ms".into(),
        "25".into(),
    ])
    .await;
    let follow_payload: Value = serde_json::from_str(&follow.stdout).expect("json");
    assert_eq!(follow_payload["follow"], true);
    assert_eq!(follow_payload["timedOut"], true);

    let state = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path.clone(),
        "state".into(),
    ])
    .await;
    let state_payload: Value = serde_json::from_str(&state.stdout).expect("json");
    assert_eq!(state_payload["rootScene"], "screens/main_menu");

    let act = run_ipc(vec![
        "sts2".into(),
        "--json".into(),
        "--config".into(),
        config_path,
        "act".into(),
        "choose".into(),
        "--choice".into(),
        "menu:start-run".into(),
    ])
    .await;
    let act_payload: Value = serde_json::from_str(&act.stdout).expect("json");
    assert_eq!(act_payload["accepted"], true);

    server.abort();
}

#[test]
fn dev_breakpoint_add_returns_registered_breakpoint_payload() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "breakpoint",
        "add",
        "--path",
        "screen.id",
        "--equals",
        "\"main-menu\"",
        "--name",
        "menu-break",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["added"], true);
    assert_eq!(payload["breakpoint"]["queryPath"], "screen.id");
    assert_eq!(payload["breakpoint"]["name"], "menu-break");
    assert_eq!(payload["breakpoint"]["predicate"]["operator"], "equals");
    assert_eq!(
        payload["breakpoint"]["predicate"]["expected"],
        "\"main-menu\""
    );
    assert_eq!(payload["breakpoint"]["kind"], "match");
    assert_eq!(payload["breakpoint"]["minHitCount"], 1);
    assert_eq!(payload["breakpoint"]["hitCount"], 0);
    assert_eq!(payload["breakpoint"]["autoRemoveOnHit"], false);
    assert_eq!(payload["status"]["executionState"], "unsupported");
}

#[test]
fn dev_breakpoint_add_change_kind_omits_predicate_and_preserves_session_fields() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "breakpoint",
        "add",
        "--session",
        "dbg:1",
        "--path",
        "screen.id",
        "--kind",
        "change",
        "--min-hit-count",
        "2",
        "--auto-remove-on-hit",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["added"], true);
    assert_eq!(payload["breakpoint"]["kind"], "change");
    assert_eq!(payload["breakpoint"]["predicate"], Value::Null);
    assert_eq!(payload["breakpoint"]["minHitCount"], 2);
    assert_eq!(payload["breakpoint"]["autoRemoveOnHit"], true);
}

#[cfg(unix)]
#[tokio::test]
async fn dev_scene_node_returns_structured_success_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-runtime-scene-node.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "node",
            "/root/CombatScreen",
        ])
    })
    .await
    .expect("join scene node task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["screen"]["instanceId"], "screen:combat:live");
    assert_eq!(payload["node"]["name"], "CombatScreen");
    assert!(payload["node"].get("properties").is_none());
    assert!(payload["node"]["properties"].get("text").is_none());
    assert!(payload["node"].get("computedTransform").is_none());
    assert_eq!(
        payload["node"]["sceneFilePath"],
        "res://ui/CombatScreen.tscn"
    );
    assert_eq!(
        payload["children"][0]["nodePath"],
        "/root/CombatScreen/HandPanel"
    );
    assert_eq!(
        payload["notes"][0],
        "Runtime node ids are stable only for the current screen instance."
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_scene_children_returns_direct_children_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-runtime-scene-children.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "children",
            "/root/CombatScreen",
            "--properties",
        ])
    })
    .await
    .expect("join scene children task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["node"]["name"], "CombatScreen");
    assert_eq!(
        payload["children"][0]["nodePath"],
        "/root/CombatScreen/HandPanel"
    );
    assert_eq!(payload["children"][0]["properties"]["visible"], true);
    assert!(payload["children"][0].get("computedTransform").is_none());

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_scene_node_renders_requested_properties_and_computed_transform_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-runtime-scene-node-detail.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "node",
            "/root/CombatScreen",
            "--properties",
            "--computed-transform",
        ])
    })
    .await
    .expect("join scene node task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["node"]["properties"]["visible"], true);
    assert_eq!(payload["node"]["properties"]["size"]["x"], 640.0);
    assert_eq!(
        payload["node"]["properties"]["textures"][0]["resourcePath"],
        "res://combat/backgrounds/overgrowth/layer.png"
    );
    assert_eq!(
        payload["node"]["properties"]["effectiveModulate"]["html"],
        "#ffffffff"
    );
    assert_eq!(payload["node"]["properties"]["clipContents"], true);
    assert_eq!(payload["node"]["properties"]["mouseFilter"], 2);
    assert_eq!(payload["node"]["properties"]["focusMode"], 1);
    assert_eq!(payload["node"]["properties"]["mouseDefaultCursorShape"], 3);
    assert_eq!(
        payload["node"]["properties"]["textureRect"]["stretchMode"],
        "keep-aspect-centered"
    );
    assert_eq!(
        payload["node"]["properties"]["textureRect"]["expandMode"],
        "ignore-size"
    );
    assert_eq!(payload["node"]["properties"]["textureRect"]["flipH"], false);
    assert_eq!(payload["node"]["properties"]["textureRect"]["flipV"], true);
    assert_eq!(
        payload["node"]["properties"]["material"]["material"]["resourcePath"],
        "res://materials/lobby/dim.tres"
    );
    assert_eq!(
        payload["node"]["properties"]["material"]["shader"]["resourcePath"],
        "res://shaders/ui_dim.gdshader"
    );
    assert_eq!(
        payload["node"]["properties"]["material"]["blendMode"],
        "mix"
    );
    assert_eq!(
        payload["node"]["properties"]["material"]["shaderParameters"][0]["name"],
        "darken"
    );
    assert_eq!(
        payload["node"]["properties"]["material"]["shaderParameters"][0]["numberValue"],
        0.5
    );
    assert_eq!(
        payload["node"]["properties"]["layout"]["containerAlignment"],
        1
    );
    assert_eq!(
        payload["node"]["properties"]["layout"]["flowVertical"],
        true
    );
    assert_eq!(
        payload["node"]["properties"]["ninePatch"]["texture"]["resourcePath"],
        "res://combat/backgrounds/overgrowth/layer.png"
    );
    assert_eq!(
        payload["node"]["properties"]["ninePatch"]["patchMargins"]["left"],
        12.0
    );
    assert_eq!(
        payload["node"]["properties"]["ninePatch"]["axisStretchHorizontal"],
        "tile-fit"
    );
    assert_eq!(
        payload["node"]["properties"]["ninePatch"]["effectiveModulate"]["html"],
        "#0000005c"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["text"],
        "A spire label"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["rawText"],
        "A {0} label"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["richTextEnabled"],
        true
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["source"],
        "mega-rich-text-label"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["diagnosticSurface"],
        "dev.runtime_scene.text"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["fontSizeSource"],
        "mega-text._lastSetSize"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["appliedFontSize"],
        24.0
    );
    assert_eq!(payload["node"]["properties"]["text"]["themeFontSize"], 28.0);
    assert_eq!(
        payload["node"]["properties"]["text"]["configuredMinFontSize"],
        8.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["configuredMaxFontSize"],
        100.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["recipe"]["source"],
        "mega-rich-text-label"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["recipe"]["autoSizeEnabled"],
        true
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["recipe"]["nominalFontSizePx"],
        24.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["recipe"]["horizontallyBound"],
        false
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["recipe"]["verticallyBound"],
        true
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["metricSource"],
        "godot-font,godot-text-paragraph"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["fontAscentPx"],
        20.5
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["fontDescentPx"],
        5.5
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["fontHeightPx"],
        28.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["paragraphSizeHeightPx"],
        56.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["lines"][0]["paragraphAscentPx"],
        20.5
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["renderedMetrics"]["lines"][0]["paragraphLineSizeHeightPx"],
        28.0
    );
    assert_eq!(payload["node"]["properties"]["text"]["fontWeight"], "700");
    assert_eq!(payload["node"]["properties"]["text"]["fontStyle"], "italic");
    assert_eq!(
        payload["node"]["properties"]["text"]["notices"][0]["code"],
        "dev_scene_text"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["shadow"]["color"]["html"],
        "#00000080"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["shadow"]["offset"]["x"],
        2.0
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["shadow"]["source"],
        "theme:font_shadow_color"
    );
    assert_eq!(
        payload["node"]["properties"]["text"]["shadow"]["stackedShadows"][0]["outlineSize"],
        0.5
    );
    assert_eq!(
        payload["node"]["computedTransform"]["globalTransform"]["origin"]["x"],
        10.0
    );
    assert_eq!(
        payload["node"]["computedTransform"]["notices"][0]["code"],
        "not_applicable"
    );
    assert_eq!(payload["children"][0]["properties"]["visible"], true);

    server.abort();
}

#[test]
fn dev_scene_hover_mock_transport_reports_structured_unsupported_error() {
    let response = run_error(&[
        "sts2",
        "--json",
        "dev",
        "scene",
        "hover",
        "--path",
        "/root/CombatScreen",
        "--hover-tip",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "not_implemented");
    assert_eq!(
        payload["error"]["message"],
        "runtime scene control hover requires the live STS2 bridge host."
    );
    assert_eq!(payload["error"]["details"][0]["field"], "command");
    assert_eq!(payload["error"]["details"][0]["value"], "dev scene hover");
}

#[test]
fn dev_scene_unhover_mock_transport_reports_structured_unsupported_error() {
    // A global clear (no --path/--element-id) is valid at the CLI layer; the mock transport still
    // reports it as unsupported because there is no live Godot scene tree to clear.
    let response = run_error(&["sts2", "--json", "dev", "scene", "unhover"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "not_implemented");
    assert_eq!(
        payload["error"]["message"],
        "runtime scene control unhover requires the live STS2 bridge host."
    );
    assert_eq!(payload["error"]["details"][0]["field"], "command");
    assert_eq!(payload["error"]["details"][0]["value"], "dev scene unhover");
}

#[cfg(unix)]
#[tokio::test]
async fn dev_scene_set_visible_reports_stable_verification_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-runtime-scene-visible-stable.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "set-visible",
            "/root/StableHidden",
            "--hidden",
            "--verify-after-ms",
            "0",
        ])
    })
    .await
    .expect("join scene set-visible stable task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["changed"], true);
    assert_eq!(payload["verification"]["stable"], true);
    assert_eq!(payload["verification"]["observedVisible"], false);
    assert_eq!(
        payload["verification"]["notes"][0],
        "visibility_stable_after_mutation"
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn dev_scene_set_visible_reports_reverted_verification_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir
        .path()
        .join("spirectl-runtime-scene-visible-reverted.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = RuntimeSceneBridgeService;

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "scene",
            "set-visible",
            "/root/CombatScreen",
            "--hidden",
            "--verify-after-ms",
            "0",
        ])
    })
    .await
    .expect("join scene set-visible reverted task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["changed"], true);
    assert_eq!(payload["verification"]["stable"], false);
    assert_eq!(payload["verification"]["observedVisible"], true);
    assert_eq!(
        payload["verification"]["notes"][0],
        "visibility_reverted_after_mutation"
    );

    server.abort();
}
