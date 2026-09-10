use super::*;

#[test]
fn dev_logs_applies_limit_level_and_target_filters() {
    let response = run(&[
        "sts2",
        "--json",
        "dev",
        "logs",
        "--limit",
        "1",
        "--level",
        "info",
        "--target",
        "transport",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["entries"].as_array().expect("entries").len(), 1);
    assert_eq!(payload["entries"][0]["cursor"], 2);
    assert_eq!(payload["nextCursor"], 2);
    assert_eq!(payload["entries"][0]["target"], "bridge.transport");
    assert_eq!(payload["entries"][0]["level"], "info");
}

#[test]
fn dev_logs_tail_returns_latest_entries() {
    let response = try_run(&["sts2", "--json", "dev", "logs", "--tail", "2"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["nextCursor"], 3);
    assert_eq!(
        payload["entries"]
            .as_array()
            .expect("entries")
            .iter()
            .map(|entry| entry["cursor"].as_u64().expect("cursor"))
            .collect::<Vec<_>>(),
        vec![2, 3]
    );
    assert_eq!(payload["entries"][0]["target"], "bridge.transport");
    assert_eq!(payload["entries"][1]["target"], "bridge.state");
}

#[test]
fn dev_logs_after_cursor_returns_newer_entries() {
    let response = try_run(&["sts2", "--json", "dev", "logs", "--after-cursor", "2"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["nextCursor"], 3);
    assert_eq!(
        payload["entries"]
            .as_array()
            .expect("entries")
            .iter()
            .map(|entry| entry["cursor"].as_u64().expect("cursor"))
            .collect::<Vec<_>>(),
        vec![3]
    );
    assert_eq!(payload["entries"][0]["target"], "bridge.state");
}

#[tokio::test]
async fn dev_log_health_fails_when_logs_contain_errors_and_exceptions() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-diagnostics.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, DiagnosticsBridgeService);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = tokio::task::spawn_blocking(move || {
        run_error(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "log-health",
            "--tail",
            "20",
        ])
    })
    .await
    .expect("join log-health task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "log_unhealthy");
    assert_eq!(payload["error"]["logHealth"]["status"], "unhealthy");
    assert_eq!(payload["error"]["logHealth"]["unhealthyEntryCount"], 2);
    assert_eq!(payload["error"]["exceptions"][0]["kind"], "log");
    assert_eq!(
        payload["error"]["exceptions"][0]["message"],
        "System.Exception: bridge exploded"
    );
    server.abort();
}

#[tokio::test]
async fn dev_log_health_can_exclude_targets_and_messages() {
    let socket_dir = tempfile::tempdir().expect("tempdir");
    let socket_path = socket_dir.path().join("spirectl-diagnostics-excluded.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, DiagnosticsBridgeService);

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
            "log-health",
            "--tail",
            "20",
            "--exclude-target",
            "bridge.exception",
            "--exclude-message-regex",
            "(?i)unhandled exception",
        ])
    })
    .await
    .expect("join log-health task");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "healthy");
    assert_eq!(payload["unhealthyEntryCount"], 0);
    assert_eq!(payload["excludedEntryCount"], 2);
    server.abort();
}

#[test]
fn dev_diagnostics_writes_bundle_and_reports_partial_capture() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let bundle_dir = temp.path().join("evidence");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "diagnostics",
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "partial");
    assert_eq!(payload["captures"]["gameInfo"]["status"], "captured");
    assert_eq!(payload["captures"]["state"]["status"], "captured");
    assert_eq!(payload["captures"]["logs"]["status"], "captured");
    assert_eq!(payload["captures"]["inspectActions"]["status"], "captured");
    assert_eq!(payload["captures"]["screenshot"]["status"], "captured");
    assert_eq!(payload["captures"]["sceneTree"]["status"], "error");
    assert_eq!(
        payload["bundle"]["files"]["diagnostics"],
        bundle_dir.join("diagnostics.json").display().to_string()
    );
    assert!(bundle_dir.join("diagnostics.json").exists());
    assert!(bundle_dir.join("game-info.json").exists());
    assert!(bundle_dir.join("state.json").exists());
    assert!(bundle_dir.join("logs.json").exists());
    assert!(bundle_dir.join("inspect-actions.json").exists());
    assert!(bundle_dir.join("runtime.png").exists());
    assert!(!bundle_dir.join("scene-tree.json").exists());
}

#[test]
fn dev_diagnostics_captures_hot_reload_status() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let project_dir = temp.path().join("hotmod");
    std::fs::create_dir_all(&project_dir).expect("create project dir");
    std::fs::write(
        project_dir.join("sts2.hot-reload.yaml"),
        r#"
schemaVersion: spirectl.hot-reload-project/v0
projectId: my-hot-mod
shellModId: my-hot-mod
shellProject: MyHotMod.Shell
logicProject: MyHotMod.Logic
logicBuildProfile: my-hot-mod-logic-build
logicArtifactPath: ${profileDir}/out/MyHotMod.Logic.dll
expectedContractVersion: 0
protocol:
  id: spirectl.m57.hot-reload-shell
  version: 0
"#,
    )
    .expect("write hot reload metadata");
    let bundle_dir = temp.path().join("evidence");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "diagnostics",
        "--hot-reload-project",
        project_dir.to_str().expect("utf8 path"),
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["captures"]["hotReload"]["status"], "captured");
    assert_eq!(
        payload["captures"]["hotReload"]["data"]["shell"]["supported"],
        false
    );
    assert!(bundle_dir.join("hot-reload.json").exists());
}

#[test]
fn dev_diagnostics_accepts_viewport_preset_for_runtime_capture() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let bundle_dir = temp.path().join("evidence");
    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "diagnostics",
        "--preset",
        "desktop-1080p",
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(
        payload["captures"]["screenshot"]["data"]["preset"],
        "desktop-1080p"
    );
    assert_eq!(
        payload["captures"]["screenshot"]["data"]["requestedViewport"]["width"],
        1920
    );
    assert_eq!(
        payload["captures"]["screenshot"]["data"]["requestedViewport"]["height"],
        1080
    );
}

#[test]
fn dev_diagnostics_does_not_report_diagnostics_bundle_file_when_write_fails() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let bundle_path = temp.path().join("bundle-file");
    fs::write(&bundle_path, "not-a-directory\n").expect("bundle file");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "diagnostics",
        "--bundle-dir",
        bundle_path.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "partial");
    assert_eq!(payload["bundle"]["dir"], bundle_path.display().to_string());
    assert!(
        payload["bundle"]["files"].get("diagnostics").is_none(),
        "diagnostics.json should not be reported when the write fails"
    );
    assert!(
        payload["errors"]
            .as_array()
            .expect("errors array")
            .iter()
            .any(|error| {
                error["artifact"] == "diagnostics"
                    && error["operation"] == "write"
                    && error["path"] == bundle_path.join("diagnostics.json").display().to_string()
            }),
        "expected diagnostics write failure to be surfaced"
    );
}

#[test]
fn dev_screenshot_writes_png_artifact() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let output = temp.path().join("artifacts").join("combat.png");
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "screenshot",
        "--output",
        output.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let bytes = fs::read(&output).expect("written screenshot");

    assert_eq!(payload["format"], "png");
    assert_eq!(payload["path"], output.display().to_string());
    assert_eq!(payload["screen"]["id"], "combat");
    assert_eq!(payload["byteLength"], bytes.len());
    assert_eq!(&bytes[..8], b"\x89PNG\r\n\x1a\n");
}

#[test]
fn dev_screenshot_accepts_viewport_preset_and_reports_viewport_metadata() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let output = temp.path().join("artifacts").join("combat-1080p.png");
    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "screenshot",
        "--preset",
        "desktop-1080p",
        "--output",
        output.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["path"], output.display().to_string());
    assert_eq!(payload["preset"], "desktop-1080p");
    assert_eq!(payload["requestedViewport"]["width"], 1920);
    assert_eq!(payload["requestedViewport"]["height"], 1080);
    assert_eq!(payload["appliedViewport"]["width"], 1920);
    assert_eq!(payload["appliedViewport"]["height"], 1080);
    assert_eq!(payload["restoredViewport"], true);
}

#[test]
fn dev_screenshot_diff_writes_bundle_and_reports_zero_difference_for_matching_baseline() {
    let config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let bundle_dir = temp.path().join("bundle");

    let baseline_capture = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "screenshot",
        "--output",
        baseline.to_str().expect("utf8 path"),
    ]);
    let _: Value = serde_json::from_str(&baseline_capture.stdout).expect("json");

    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["diffPixels"], 0);
    assert_eq!(payload["diffRatio"], 0.0);
    assert_eq!(
        payload["bundle"]["files"]["comparison"],
        bundle_dir.join("comparison.json").display().to_string()
    );
    assert!(bundle_dir.join("comparison.json").exists());
    assert!(bundle_dir.join("baseline.png").exists());
    assert!(bundle_dir.join("actual.png").exists());
    assert!(bundle_dir.join("diff.png").exists());
}

#[test]
fn dev_screenshot_diff_supports_offline_actual_png_without_live_capture() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let bundle_dir = temp.path().join("bundle");

    write_png(&baseline, [0, 255, 0, 255]);
    write_png(&actual, [0, 255, 0, 255]);

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["preset"], Value::Null);
    assert_eq!(payload["requestedViewport"], Value::Null);
    assert_eq!(payload["appliedViewport"], Value::Null);
    assert_eq!(payload["restoredViewport"], Value::Null);
    assert_eq!(
        payload["baseline"]["path"],
        baseline
            .canonicalize()
            .expect("baseline path")
            .display()
            .to_string()
    );
    assert_eq!(
        payload["actual"]["path"],
        actual
            .canonicalize()
            .expect("actual path")
            .display()
            .to_string()
    );
    assert!(bundle_dir.join("comparison.json").exists());
    assert!(bundle_dir.join("baseline.png").exists());
    assert!(bundle_dir.join("actual.png").exists());
    assert!(bundle_dir.join("diff.png").exists());
}

#[test]
fn dev_screenshot_diff_supports_mask_and_roi_outputs() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    let regions = temp.path().join("regions.json");
    let bundle_dir = temp.path().join("bundle");

    write_visual_diff_fixture(&baseline, &actual, &mask);
    fs::write(
        &regions,
        serde_json::to_vec_pretty(&serde_json::json!({
            "regions": [
                { "id": "selected-character-panel", "x": 50, "y": 50, "width": 1, "height": 1 }
            ]
        }))
        .expect("regions json"),
    )
    .expect("write regions");

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--mask",
        mask.to_str().expect("utf8 path"),
        "--regions",
        regions.to_str().expect("utf8 path"),
        "--max-diff-ratio",
        "0.01",
        "--max-diff-pixels",
        "1",
        "--foreground-max-diff-ratio",
        "0",
        "--roi-max-diff-ratio",
        "0",
        "--required-comparison",
        "foreground",
        "--required-comparison",
        "roi",
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let comparison = &payload["error"]["comparison"];

    assert_eq!(payload["error"]["code"], "visual_mismatch");
    assert_eq!(comparison["matched"], false);
    assert_eq!(comparison["diffPixels"], 1);
    assert_eq!(comparison["diffRatio"], 0.0001);
    assert_eq!(comparison["comparisons"]["full"]["matched"], true);
    assert_eq!(comparison["comparisons"]["foreground"]["matched"], false);
    assert_eq!(comparison["comparisons"]["foreground"]["diffRatio"], 1.0);
    assert_eq!(comparison["comparisons"]["roi"]["matched"], false);
    assert_eq!(comparison["comparisons"]["roi"]["diffRatio"], 1.0);
    assert_eq!(
        comparison["bundle"]["files"]["foregroundDiff"],
        bundle_dir.join("foreground-diff.png").display().to_string()
    );
    assert_eq!(
        comparison["bundle"]["files"]["roiDiff"],
        bundle_dir.join("roi-diff.png").display().to_string()
    );
    assert!(bundle_dir.join("comparison.json").exists());
    assert!(bundle_dir.join("baseline.png").exists());
    assert!(bundle_dir.join("actual.png").exists());
    assert!(bundle_dir.join("diff.png").exists());
    assert!(bundle_dir.join("foreground-diff.png").exists());
    assert!(bundle_dir.join("roi-diff.png").exists());

    let missing_bundle = temp.path().join("missing-bundle");
    let missing_response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        baseline.to_str().expect("utf8 path"),
        "--max-diff-ratio",
        "0.01",
        "--required-comparison",
        "foreground",
        "--required-comparison",
        "roi",
        "--bundle-dir",
        missing_bundle.to_str().expect("utf8 path"),
    ]);
    let missing_payload: Value = serde_json::from_str(&missing_response.stdout).expect("json");
    let notices = missing_payload["error"]["comparison"]["notices"]
        .as_array()
        .expect("notices");
    assert!(
        notices
            .iter()
            .any(|notice| notice["code"] == "missing-mask")
    );
    assert!(
        notices
            .iter()
            .any(|notice| notice["code"] == "missing-region")
    );

    let weak_threshold = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--max-diff-ratio",
        "1.0",
    ]);
    let weak_payload: Value = serde_json::from_str(&weak_threshold.stdout).expect("json");
    assert_eq!(weak_payload["error"]["code"], "invalid_query");
    assert_eq!(weak_payload["error"]["path"], "maxDiffRatio");

    let weak_foreground = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--foreground-max-diff-ratio",
        "1.0",
    ]);
    let weak_foreground_payload: Value =
        serde_json::from_str(&weak_foreground.stdout).expect("json");
    assert_eq!(weak_foreground_payload["error"]["code"], "invalid_query");
    assert_eq!(
        weak_foreground_payload["error"]["path"],
        "foregroundMaxDiffRatio"
    );

    let weak_roi = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--roi-max-diff-ratio",
        "1.0",
    ]);
    let weak_roi_payload: Value = serde_json::from_str(&weak_roi.stdout).expect("json");
    assert_eq!(weak_roi_payload["error"]["code"], "invalid_query");
    assert_eq!(weak_roi_payload["error"]["path"], "roiMaxDiffRatio");
}

#[test]
fn dev_snapshot_export_writes_bounded_baseline_bundle() {
    // Combat (not main-menu): main-menu's "start run" has no `state actions`
    // surfacer (state dropped the generic legacy choice surface and nothing
    // re-models the main menu natively), so its captured availableActionIds is
    // always empty — combat gives this test real, non-empty action ids to assert
    // on while still exercising the same shared main-menu.sts2.snapshot.yaml spec
    // (its selectors, rootScene/language, resolve for any scenario).
    let config = combat_mock_config();
    let temp =
        tempfile::tempdir_in(std::env::current_dir().expect("current dir")).expect("temp dir");
    let output_dir = temp.path().join("baseline");
    let spec_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("tests")
        .join("snapshots")
        .join("main-menu.sts2.snapshot.yaml");

    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "export",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--output",
        output_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "exported");
    assert_eq!(
        payload["specPath"],
        "tests/snapshots/main-menu.sts2.snapshot.yaml"
    );
    assert!(
        !payload["outputDir"]
            .as_str()
            .expect("outputDir")
            .starts_with('/')
    );
    assert!(
        !payload["captures"]["state"]["path"]
            .as_str()
            .expect("state path")
            .starts_with('/')
    );
    assert_eq!(
        payload["captures"]["state"]["selectors"][0]["path"],
        "rootScene"
    );
    assert_eq!(
        payload["captures"]["actions"]["availableActionIds"][0],
        "action:combat-room:end-turn"
    );
    assert!(output_dir.join("snapshot.json").exists());
    assert!(output_dir.join("state.json").exists());
    assert!(output_dir.join("inspect-actions.json").exists());
    assert!(output_dir.join("log-health.json").exists());
    assert!(output_dir.join("diagnostics.json").exists());
    assert!(output_dir.join("runtime.png").exists());
}

#[test]
fn dev_snapshot_compare_reports_match_for_same_mock_scenario() {
    let config = write_raw_config("transport:\n  kind: mock\n  mockScenario: main-menu\n");
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline_dir = temp.path().join("baseline");
    let bundle_dir = temp.path().join("bundle");
    let spec_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("tests")
        .join("snapshots")
        .join("main-menu.sts2.snapshot.yaml");

    let _ = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "export",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--output",
        baseline_dir.to_str().expect("utf8 path"),
    ]);

    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "compare",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--baseline",
        baseline_dir.to_str().expect("utf8 path"),
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["sections"]["state"]["matched"], true);
    assert_eq!(payload["sections"]["actions"]["matched"], true);
    assert_eq!(payload["sections"]["screenshot"]["matched"], true);
    assert!(bundle_dir.join("snapshot-compare.json").exists());
    assert!(bundle_dir.join("state.json").exists());
    assert!(bundle_dir.join("inspect-actions.json").exists());
    assert!(bundle_dir.join("comparison.json").exists());
}

#[test]
fn dev_snapshot_compare_ignores_versions_when_spec_does_not_opt_in() {
    let config = write_raw_config("transport:\n  kind: mock\n  mockScenario: main-menu\n");
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline_dir = temp.path().join("baseline");
    let spec_path = temp.path().join("compare-versions-off.sts2.snapshot.yaml");
    fs::write(
        &spec_path,
        r#"schemaVersion: spirectl.snapshot/v0
name: main-menu-no-screenshot
state:
  selectors:
    - rootScene
    - language
"#,
    )
    .expect("write spec");

    let _ = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "export",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--output",
        baseline_dir.to_str().expect("utf8 path"),
    ]);

    let state_path = baseline_dir.join("state.json");
    let mut state_payload: Value =
        serde_json::from_str(&fs::read_to_string(&state_path).expect("read state baseline"))
            .expect("parse state baseline");
    state_payload["versions"]["gameVersion"] = Value::String("changed-game-version".to_string());
    state_payload["versions"]["bridgeVersion"] =
        Value::String("changed-bridge-version".to_string());
    fs::write(
        &state_path,
        format!(
            "{}\n",
            serde_json::to_string_pretty(&state_payload).expect("render state baseline")
        ),
    )
    .expect("rewrite state baseline");

    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "compare",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--baseline",
        baseline_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["sections"]["state"]["matched"], true);
}

#[test]
fn dev_snapshot_compare_with_screenshot_succeeds_without_bundle_dir() {
    let config = write_raw_config("transport:\n  kind: mock\n  mockScenario: main-menu\n");
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline_dir = temp.path().join("baseline");
    let spec_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("tests")
        .join("snapshots")
        .join("main-menu.sts2.snapshot.yaml");

    let _ = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "export",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--output",
        baseline_dir.to_str().expect("utf8 path"),
    ]);

    let response = try_run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "compare",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--baseline",
        baseline_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["matched"], true);
    assert_eq!(payload["sections"]["screenshot"]["matched"], true);
}

#[test]
fn dev_snapshot_compare_reports_structured_mismatch_for_different_runtime_state() {
    let baseline_config = write_raw_config("transport:\n  kind: mock\n  mockScenario: main-menu\n");
    let compare_config = combat_mock_config();
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline_dir = temp.path().join("baseline");
    let bundle_dir = temp.path().join("bundle");
    let spec_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("tests")
        .join("snapshots")
        .join("main-menu.sts2.snapshot.yaml");

    let _ = try_run(&[
        "sts2",
        "--json",
        "--config",
        baseline_config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "export",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--output",
        baseline_dir.to_str().expect("utf8 path"),
    ]);

    let response = try_run_error(&[
        "sts2",
        "--json",
        "--config",
        compare_config.path().to_str().expect("utf8 path"),
        "dev",
        "snapshot",
        "compare",
        "--spec",
        spec_path.to_str().expect("utf8 path"),
        "--baseline",
        baseline_dir.to_str().expect("utf8 path"),
        "--bundle-dir",
        bundle_dir.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["error"]["code"], "snapshot_mismatch");
    assert_eq!(payload["error"]["comparison"]["matched"], false);
    assert_eq!(
        payload["error"]["comparison"]["mismatches"][0]["section"],
        "state"
    );
    assert!(bundle_dir.join("snapshot-compare.json").exists());
}

#[test]
fn bridge_health_defaults_to_a_sentinel_source_scan() {
    let response = run(&["sts2", "--json", "game", "bridge-health"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["bridge"]["sourceScanMode"], "sentinel");
}

#[test]
fn bridge_health_check_source_requests_the_full_source_walk() {
    let response = run(&["sts2", "--json", "game", "bridge-health", "--check-source"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["bridge"]["sourceScanMode"], "full");
}

#[test]
fn bridge_health_verbose_reports_a_full_scan_rooted_at_the_running_checkout() {
    let response = run(&["sts2", "--json", "game", "bridge-health", "--verbose"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let freshness = &payload["localBridge"]["sourceFreshness"];

    assert_eq!(freshness["scanMode"], "full");
    assert_eq!(freshness["status"], "known");
    let newest_path = freshness["newestPath"].as_str().expect("newest path");
    // The walk must root on the checkout the process runs in, not on the checkout the binary
    // happened to be compiled from.
    let repo_root = std::env::current_dir()
        .expect("cwd")
        .ancestors()
        .find(|dir| dir.join("bridge-mod/Directory.Build.props").is_file())
        .expect("repo root")
        .to_path_buf();
    assert!(
        Path::new(newest_path).starts_with(&repo_root),
        "{newest_path} should live under {}",
        repo_root.display()
    );
}

#[test]
fn bridge_health_sentinel_scan_stays_within_the_full_walk() {
    let response = run(&["sts2", "--json", "game", "bridge-health", "--verbose"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let full_seconds = payload["localBridge"]["sourceFreshness"]["newestModifiedUnixSeconds"]
        .as_u64()
        .expect("full scan timestamp");

    let sentinel = sts2::bridge_source_freshness_json_for_tests(false);
    assert_eq!(sentinel["scanMode"], "sentinel");
    assert_eq!(sentinel["status"], "known");
    assert!(
        sentinel["newestModifiedUnixSeconds"]
            .as_u64()
            .expect("sentinel timestamp")
            <= full_seconds,
        "a sentinel stat can never be newer than the full walk"
    );
    assert!(
        sentinel["note"]
            .as_str()
            .expect("note")
            .contains("--check-source"),
        "sentinel mode must point at the authoritative flag"
    );
}

#[test]
fn bridge_health_sentinel_tracks_the_bridge_runtime_owner() {
    let working_directory = std::env::current_dir().expect("cwd");
    let repo_root = working_directory
        .ancestors()
        .find(|dir| dir.join("bridge-mod/Directory.Build.props").is_file())
        .expect("repo root");
    assert!(
        repo_root
            .join("bridge-mod/src/Spirectl.BridgeMod/BridgeRuntime.cs")
            .is_file()
    );
    assert!(
        !repo_root
            .join("bridge-mod/src/Spirectl.Sts2/BridgeRuntime.cs")
            .exists()
    );
}

/// A 10x10 pair whose only differences are a small per-channel delta at (1,1) and an alpha-only
/// delta at (2,2), plus a mask selecting just (1,1).
fn write_tolerance_fixture(baseline: &Path, actual: &Path, mask: &Path) {
    let mut baseline_image = image::RgbaImage::from_pixel(10, 10, image::Rgba([10, 20, 30, 255]));
    let mut actual_image = baseline_image.clone();
    let mut mask_image = image::RgbaImage::from_pixel(10, 10, image::Rgba([0, 0, 0, 0]));

    baseline_image.put_pixel(1, 1, image::Rgba([100, 100, 100, 255]));
    actual_image.put_pixel(1, 1, image::Rgba([103, 97, 100, 255]));
    baseline_image.put_pixel(2, 2, image::Rgba([40, 40, 40, 255]));
    actual_image.put_pixel(2, 2, image::Rgba([40, 40, 40, 200]));
    mask_image.put_pixel(1, 1, image::Rgba([255, 255, 255, 255]));

    baseline_image.save(baseline).expect("write baseline png");
    actual_image.save(actual).expect("write actual png");
    mask_image.save(mask).expect("write mask png");
}

#[test]
fn dev_screenshot_diff_defaults_to_exact_rgba_and_echoes_the_tolerance() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    write_tolerance_fixture(&baseline, &actual, &mask);

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
    ]);
    let comparison =
        &serde_json::from_str::<Value>(&response.stdout).expect("json")["error"]["comparison"]
            .clone();

    assert_eq!(comparison["pixelTolerance"], 0);
    assert_eq!(comparison["ignoreAlpha"], false);
    assert_eq!(comparison["diffPixels"], 2);
    assert_eq!(comparison["matched"], false);
}

#[test]
fn dev_screenshot_diff_pixel_tolerance_absorbs_small_channel_deltas() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    write_tolerance_fixture(&baseline, &actual, &mask);

    // The RGB delta at (1,1) is at most 3; the alpha delta at (2,2) is 55 and must survive.
    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--pixel-tolerance",
        "3",
    ]);
    let comparison =
        serde_json::from_str::<Value>(&response.stdout).expect("json")["error"]["comparison"]
            .clone();

    assert_eq!(comparison["pixelTolerance"], 3);
    assert_eq!(comparison["diffPixels"], 1);
}

#[test]
fn dev_screenshot_diff_pixel_tolerance_below_the_delta_still_reports_it() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    write_tolerance_fixture(&baseline, &actual, &mask);

    let response = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--pixel-tolerance",
        "2",
    ]);
    let comparison =
        serde_json::from_str::<Value>(&response.stdout).expect("json")["error"]["comparison"]
            .clone();

    assert_eq!(comparison["pixelTolerance"], 2);
    assert_eq!(comparison["diffPixels"], 2);
}

#[test]
fn dev_screenshot_diff_ignore_alpha_drops_alpha_only_differences() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    write_tolerance_fixture(&baseline, &actual, &mask);

    let response = try_run(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--pixel-tolerance",
        "3",
        "--ignore-alpha",
    ]);
    let comparison: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(comparison["ignoreAlpha"], true);
    assert_eq!(comparison["diffPixels"], 0);
    assert_eq!(comparison["matched"], true);
}

#[test]
fn dev_screenshot_diff_tolerance_reaches_the_masked_and_roi_comparators() {
    let temp = tempfile::tempdir().expect("temp dir");
    let baseline = temp.path().join("baseline.png");
    let actual = temp.path().join("actual.png");
    let mask = temp.path().join("mask.png");
    let regions = temp.path().join("regions.json");
    write_tolerance_fixture(&baseline, &actual, &mask);
    fs::write(
        &regions,
        serde_json::to_vec_pretty(&serde_json::json!({
            "regions": [{ "id": "tolerant", "x": 1, "y": 1, "width": 1, "height": 1 }]
        }))
        .expect("regions json"),
    )
    .expect("write regions");

    // Without the tolerance the masked/ROI pixel differs; with it, all three comparators agree.
    let strict = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--mask",
        mask.to_str().expect("utf8 path"),
        "--regions",
        regions.to_str().expect("utf8 path"),
    ]);
    let strict_comparison =
        serde_json::from_str::<Value>(&strict.stdout).expect("json")["error"]["comparison"].clone();
    assert_eq!(
        strict_comparison["comparisons"]["foreground"]["diffRatio"],
        1.0
    );
    assert_eq!(strict_comparison["comparisons"]["roi"]["diffRatio"], 1.0);

    let tolerant = try_run_error(&[
        "sts2",
        "--json",
        "dev",
        "screenshot-diff",
        "--baseline",
        baseline.to_str().expect("utf8 path"),
        "--actual",
        actual.to_str().expect("utf8 path"),
        "--mask",
        mask.to_str().expect("utf8 path"),
        "--regions",
        regions.to_str().expect("utf8 path"),
        "--pixel-tolerance",
        "3",
    ]);
    let tolerant_comparison =
        serde_json::from_str::<Value>(&tolerant.stdout).expect("json")["error"]["comparison"]
            .clone();
    assert_eq!(
        tolerant_comparison["comparisons"]["foreground"]["diffRatio"],
        0.0
    );
    assert_eq!(tolerant_comparison["comparisons"]["roi"]["diffRatio"], 0.0);
    // The full comparison still fails on the untouched alpha-only pixel outside mask and ROI.
    assert_eq!(tolerant_comparison["comparisons"]["full"]["diffPixels"], 1);
}
