#[test]
fn assets_extract_accepts_format_and_mod_flags() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "assets",
        "extract",
        "hand",
        "--execution",
        "live",
        "--format",
        "auto",
        "--resources-dir",
        "/tmp/sts2/project",
        "--mods-dir",
        "/tmp/sts2/mods",
        "--include-mods",
    ]);

    assert!(parsed.is_ok(), "expected assets extract to parse");
}

#[test]
fn assets_explain_accepts_live_execution_and_roots() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "assets",
        "explain",
        "composed://combat-background/overgrowth/image",
        "--execution",
        "live",
        "--resources-dir",
        "/tmp/resources",
        "--include-mods",
    ])
    .expect("parse assets explain");

    match parsed.command {
        sts2::Commands::Assets(command) => {
            let parsed = format!("{:?}", command.command);
            assert!(parsed.contains("Explain"));
            assert!(parsed.contains("composed://combat-background/overgrowth/image"));
            assert!(parsed.contains("execution: Live"));
            assert!(parsed.contains("resources_dir: Some(\"/tmp/resources\")"));
            assert!(parsed.contains("include_mods: true"));
        }
        _ => panic!("expected assets command"),
    }
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_explain_combat_background_alias_reports_composition() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-explain.sock");
    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [12, 48, 96, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: None,
        notes_override: None,
        expected_load_paths: None,
    };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        &socket_path,
        None,
        None,
    );
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "explain",
        "composed://combat-background/overgrowth/image",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["command"], "explain");
    assert_eq!(payload["status"], "ok");
    assert_eq!(
        payload["sourcePath"],
        "scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(
        payload["explanation"]["rootScene"]["backgroundId"],
        "overgrowth"
    );
    assert_eq!(
        payload["explanation"]["layerGroups"][0]["selectionSource"],
        "deterministic-first-sorted"
    );
    assert_eq!(
        payload["explanation"]["selectedLayers"][0]["visibleBounds"]["width"],
        1880.0
    );
    assert_eq!(
        payload["explanation"]["render"]["renderMode"],
        "flattened-combat-background-composed"
    );
    assert_eq!(
        payload["explanation"]["render"]["trimTransparentBounds"],
        false
    );
    assert!(
        payload["explanation"]["render"]["notes"][0]
            .as_str()
            .expect("render note")
            .contains("viewport framing"),
        "expected viewport-framing render note"
    );
    assert_eq!(
        payload["explanation"]["activeScene"]["status"],
        "not-active"
    );
    assert_eq!(
        payload["explanation"]["warnings"][0]["code"],
        "missing-layer"
    );

    server.abort();
}

#[test]
fn assets_explain_rejects_encounter_render_target_metadata_queries() {
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "assets",
        "explain",
        "composed://encounters/kaiser_crab_boss/background/image",
        "--execution",
        "live",
    ]);
    let response = run_cli(cli.clone())
        .expect_err("encounter render targets should not be explain metadata queries")
        .render(cli.json);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "usage_error");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("composed://encounters/<id>/scene-package")
    );
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_explain_encounter_scene_package_routes_virtual_query() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-encounter-explain.sock");
    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [12, 48, 96, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: None,
        notes_override: None,
        expected_load_paths: Some(vec![
            "composed://encounters/kaiser_crab_boss/scene-package".to_string(),
        ]),
    };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        &socket_path,
        None,
        None,
    );
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "explain",
        "composed://encounters/kaiser_crab_boss/scene-package",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["command"], "explain");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["sourceRoot"], "composed");
    assert_eq!(
        payload["sourcePath"],
        "composed://encounters/kaiser_crab_boss/scene-package"
    );
    assert_eq!(payload["assetKind"], "encounter-scene-package");
    assert_eq!(payload["storageKind"], "composed");
    assert_eq!(payload["explanationKind"], "encounter-scene-package");
    assert_eq!(payload["explanation"]["schemaVersion"], "0");
    assert_eq!(payload["explanation"]["encounterId"], "kaiser_crab_boss");
    assert_eq!(payload["explanation"]["camera"]["scale"], 0.75);
    assert_eq!(payload["explanation"]["camera"]["offset"]["y"], 35.0);
    assert_eq!(payload["explanation"]["visualParts"][0]["partId"], "rocket");
    assert_eq!(
        payload["explanation"]["visualParts"][0]["screenSide"],
        "right"
    );
    assert_eq!(
        payload["explanation"]["visualParts"][0]["selectorDiagnostic"]["status"],
        "resolved"
    );
    assert_eq!(
        payload["explanation"]["visualParts"][0]["selectorDiagnostic"]["resolvedNode"]["path"],
        "/root/Combat/Enemies/KaiserCrab/Rocket"
    );
    assert_eq!(
        payload["explanation"]["visualParts"][0]["selectorDiagnostic"]["visibleBounds"]["width"],
        390.0
    );
    assert_eq!(
        payload["explanation"]["transitions"][0]["transitionId"],
        "rocket-charge-up"
    );
    assert_eq!(
        payload["explanation"]["renderTargets"][0]["query"],
        "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
    );
    assert_eq!(
        payload["explanation"]["renderTargets"][0]["decision"]["decision"],
        "render-overlay"
    );
    assert_eq!(
        payload["explanation"]["renderTargets"][0]["decision"]["affectedPartIds"][0],
        "rocket"
    );
    assert_eq!(
        payload["explanation"]["selectorDiagnostics"][0]["status"],
        "missing"
    );
    assert_eq!(
        payload["explanation"]["selectorDiagnostics"][0]["candidates"][0]["path"],
        "/root/Combat/Enemies/KaiserCrab/MissingClaw"
    );
    assert_eq!(
        payload["explanation"]["notices"][0]["code"],
        "provisional-contract"
    );

    server.abort();
}

#[test]
fn assets_extract_rejects_avif_format() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2", "--json", "assets", "extract", "hand", "--format", "avif",
    ]);

    assert!(parsed.is_err(), "expected avif to be unsupported");
}

#[test]
fn assets_extract_accepts_virtual_character_visual_query() {
    let parsed = <Cli as Parser>::try_parse_from([
        "sts2",
        "--json",
        "assets",
        "extract",
        "model://characters/ironclad/visuals",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert!(
        parsed.is_ok(),
        "expected virtual character asset query to parse"
    );
}

#[test]
fn assets_extract_accepts_public_model_asset_queries() {
    for query in [
        "model://characters/ironclad/iconOutline",
        "model://characters/ironclad/mapMarker",
        "model://characters/ironclad/energyCounter",
        "model://characters/ironclad/characterSelectBgSpineStill",
        "model://characters/ironclad/merchantAnim",
        "model://characters/ironclad/restSiteAnim",
        "model://relics/burning-blood/iconOutline",
        "model://relics/burning-blood/bigIcon",
    ] {
        let parsed = <Cli as Parser>::try_parse_from([
            "sts2",
            "--json",
            "assets",
            "extract",
            query,
            "--execution",
            "live",
            "--format",
            "png",
        ]);

        assert!(parsed.is_ok(), "expected {query} to parse");
    }
}

#[test]
fn assets_extract_batch_rejects_duplicate_request_ids() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            { "id": "hand", "query": "hand" },
            { "id": "hand", "query": "hand.png" }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
    ]);
    let response = run_cli(cli.clone())
        .expect_err("duplicate ids should fail before extraction")
        .render(cli.json);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_asset_batch_manifest");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("duplicate asset id 'hand'")
    );
}

#[test]
fn assets_extract_batch_rejects_empty_manifest_assets() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(&manifest_path, r#"{ "version": 0, "assets": [] }"#).expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
    ]);
    let response = run_cli(cli.clone())
        .expect_err("empty batch should fail")
        .render(cli.json);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_asset_batch_manifest");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("assets must contain at least one request")
    );
}

#[test]
fn assets_extract_batch_dry_run_accepts_doctor_probe_without_writing_outputs() {
    let root = tempfile::tempdir().expect("temp root");
    let manifest_path = root.path().join("doctor-manifest.json");
    let output_dir = root.path().join("doctor-extracted");
    fs::write(
        &manifest_path,
        r#"{ "version": 0, "format": "auto", "requests": [] }"#,
    )
    .expect("write manifest");

    let response = run(&[
        "sts2",
        "--json",
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        output_dir.to_str().expect("utf8 output"),
        "--format",
        "auto",
        "--dry-run",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["command"], "extract-batch");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["dryRun"], true);
    assert_eq!(payload["requestCount"], 0);
    assert_eq!(payload["exportCount"], 0);
    assert_eq!(payload["timing"]["totalMs"], 0);
    assert_eq!(payload["timing"]["indexMs"], 0);
    assert_eq!(payload["timing"]["resolveMs"], 0);
    assert_eq!(payload["timing"]["liveSetupMs"], 0);
    assert_eq!(payload["timing"]["exportMs"], 0);
    assert!(
        !output_dir.exists(),
        "dry-run should not create the artifact output directory"
    );
}

#[test]
fn assets_extract_batch_exports_manifest_requests_with_metadata() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let output_dir = root.path().join("batch-output");

    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);
    fs::create_dir_all(resources_dir.join("assets")).expect("create assets dir");
    fs::write(
        resources_dir.join("assets/hand_icon.tres"),
        "[gd_resource type=\"Resource\" format=3 uid=\"uid://handiconfixture\"]\n\n[resource]\nresource_name = \"HandIcon\"\n",
    )
    .expect("write resource");

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            {
              "id": "hand raster",
              "query": "ui/shared/hand.png",
              "execution": "offline",
              "format": "png",
              "metadata": { "consumerPath": "ui/hand.png" }
            },
            {
              "id": "hand_resource",
              "query": "hand_icon",
              "execution": "offline"
            }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        output_dir.to_str().expect("utf8 output"),
        "--format",
        "webp",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["command"], "extract-batch");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["requestCount"], 2);
    assert_eq!(payload["successCount"], 2);
    assert_eq!(payload["failureCount"], 0);
    assert_eq!(payload["exportCount"], 2);
    assert!(payload["timing"]["totalMs"].is_u64());
    assert!(payload["timing"]["indexMs"].is_u64());
    assert!(payload["timing"]["resolveMs"].is_u64());
    assert!(payload["timing"]["liveSetupMs"].is_u64());
    assert!(payload["timing"]["exportMs"].is_u64());
    assert_eq!(
        PathBuf::from(payload["outputDir"].as_str().expect("outputDir")),
        output_dir
    );

    let results = payload["results"].as_array().expect("results");
    let raster = results
        .iter()
        .find(|entry| entry["id"] == "hand raster")
        .expect("raster result");
    assert_eq!(raster["status"], "ok");
    assert_eq!(raster["format"], "png");
    assert!(raster["resolveMs"].is_u64());
    assert!(raster["liveSetupMs"].is_u64());
    assert!(raster["exportMs"].is_u64());
    assert_eq!(raster["metadata"]["consumerPath"], "ui/hand.png");
    assert!(
        PathBuf::from(raster["outputDir"].as_str().expect("request output"))
            .ends_with("hand_raster")
    );
    let raster_export = &raster["exports"].as_array().expect("exports")[0];
    let raster_path = PathBuf::from(raster_export["outputPath"].as_str().expect("outputPath"));
    assert!(raster_path.exists(), "raster output should exist");
    assert!(raster_path.ends_with("hand.png"));

    let source = results
        .iter()
        .find(|entry| entry["id"] == "hand_resource")
        .expect("resource result");
    assert_eq!(source["format"], "webp");
    assert_eq!(source["exports"][0]["artifactKind"], "source");
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_batch_exports_combat_background_alias() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let batch_output = root.path().join("batch-output");
    let manifest_path = root.path().join("manifest.json");
    let socket_path = root
        .path()
        .join("spirectl-assets-combat-background-batch.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::write(
        &manifest_path,
        r#"{"version": 0,"assets":[{"id":"combat-bg-overgrowth","query":"composed://combat-background/overgrowth/image","execution":"live","format":"auto"}]}"#,
    )
    .expect("write manifest");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [12, 48, 96, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-combat-background-composed".to_string()),
        notes_override: Some(vec![
            "Rendered composed combat background alias 'composed://combat-background/overgrowth/image' from res://scenes/backgrounds/overgrowth/overgrowth_background.tscn.".to_string(),
        ]),
        expected_load_paths: None,
    };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        &socket_path,
        None,
        None,
    );
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        batch_output.to_str().expect("utf8 output"),
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["requestCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["results"][0]["id"], "combat-bg-overgrowth");
    assert_eq!(
        payload["results"][0]["query"],
        "composed://combat-background/overgrowth/image"
    );
    assert_eq!(payload["results"][0]["status"], "ok");
    assert_eq!(
        payload["results"][0]["exports"][0]["sourcePath"],
        "scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(
        payload["results"][0]["exports"][0]["renderMode"],
        "flattened-combat-background-composed"
    );
    assert!(
        payload["results"][0]["exports"][0]["outputPath"]
            .as_str()
            .expect("output path")
            .ends_with(
                "combat-bg-overgrowth/resources/scenes/backgrounds/overgrowth/overgrowth_background.png"
            )
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_batch_exports_encounter_render_targets_as_virtual_queries() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let batch_output = root.path().join("batch-output");
    let manifest_path = root.path().join("manifest.json");
    let socket_path = root.path().join("spirectl-assets-encounter-batch.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::write(
        &manifest_path,
        r#"{"version": 0,"assets":[{"id":"kaiser-background","query":"composed://encounters/kaiser_crab_boss/background/image","execution":"live","format":"png"},{"id":"kaiser-overlay","query":"composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image","execution":"live","format":"png"},{"id":"kaiser-rocket","query":"composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image","execution":"live","format":"png"}]}"#,
    )
    .expect("write manifest");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [24, 96, 160, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-encounter-visual-test".to_string()),
        notes_override: Some(vec!["Rendered encounter virtual target.".to_string()]),
        expected_load_paths: Some(vec![
            "composed://encounters/kaiser_crab_boss/background/image".to_string(),
            "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
                .to_string(),
            "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image"
                .to_string(),
        ]),
    };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        &socket_path,
        None,
        None,
    );
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        batch_output.to_str().expect("utf8 output"),
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["requestCount"], 3);
    assert_eq!(payload["exportCount"], 3);
    assert_eq!(
        payload["results"][0]["exports"][0]["sourceRoot"],
        "composed"
    );
    assert_eq!(
        payload["results"][0]["exports"][0]["sourcePath"],
        "composed://encounters/kaiser_crab_boss/background/image"
    );
    assert_eq!(
        payload["results"][1]["exports"][0]["sourcePath"],
        "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
    );
    assert_eq!(
        payload["results"][1]["exports"][0]["renderMode"],
        "flattened-encounter-visual-test"
    );
    assert_eq!(
        payload["results"][1]["exports"][0]["notes"][0],
        "Rendered encounter virtual target."
    );
    assert_eq!(
        payload["results"][1]["exports"][0]["notices"][0]["code"],
        "asset-key-partial"
    );
    assert_eq!(
        payload["results"][1]["exports"][0]["provenance"]["sourceKind"],
        "virtual-query"
    );
    assert_eq!(
        payload["results"][2]["exports"][0]["sourcePath"],
        "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image"
    );
    assert!(
        payload["results"][2]["exports"][0]["outputPath"]
            .as_str()
            .expect("output path")
            .ends_with(
                "kaiser-rocket/composed/encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up.png"
            )
    );

    server.abort();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_batch_preserves_live_render_failure_diagnostics() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let batch_output = root.path().join("batch-output");
    let manifest_path = root.path().join("manifest.json");
    let socket_path = root.path().join("spirectl-assets-encounter-failure.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::write(
        &manifest_path,
        r#"{"version": 0,"assets":[{"id":"kaiser-overlay","query":"composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image","execution":"live","format":"png"}]}"#,
    )
    .expect("write manifest");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [24, 96, 160, 255],
        force_png_response: true,
        failure_source_substring: Some("visual-state/rocket-charge-up/overlay".to_string()),
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: None,
        notes_override: None,
        expected_load_paths: Some(vec![
            "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image"
                .to_string(),
        ]),
    };
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        &socket_path,
        None,
        None,
    );
    let (exit_code, payload) = run_json_bin_failure_with_env(
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "assets",
            "extract-batch",
            "--manifest",
            manifest_path.to_str().expect("utf8 manifest"),
            "--output",
            batch_output.to_str().expect("utf8 output"),
        ],
        &[],
    );

    assert_eq!(exit_code, 1);
    assert_eq!(payload["status"], "failed");
    assert_eq!(payload["results"][0]["status"], "failed");
    assert_eq!(payload["results"][0]["error"]["code"], "live_render_failed");
    assert_eq!(
        payload["results"][0]["exports"][0]["status"],
        "live_render_failed"
    );
    assert_eq!(
        payload["results"][0]["exports"][0]["notes"][0],
        "The live bridge could not render the requested asset."
    );
    assert_eq!(
        payload["results"][0]["exports"][0]["notes"][1],
        "Only Texture2D, AnimatedTexture, SpriteFrames, StyleBoxTexture, and PackedScene resources are supported in this live asset extraction spec."
    );

    let diagnostic = &payload["results"][0]["exports"][0]["renderDiagnostics"][0];
    assert_eq!(diagnostic["renderTargetId"], "rt-kaiser-overlay");
    assert_eq!(diagnostic["frame"]["x"], 1135.0);
    assert_eq!(diagnostic["pixelEvidence"]["alphaNonZero"], false);
    assert_eq!(
        diagnostic["pixelEvidence"]["rgbNonZeroBeforeAlphaNormalization"],
        true
    );
    assert_eq!(diagnostic["readyHookRan"], true);
    assert_eq!(diagnostic["spinePreviewSetupRan"], false);

    let error_export = &payload["results"][0]["error"]["details"]["exports"][0];
    assert_eq!(error_export["status"], "live_render_failed");
    assert_eq!(
        error_export["renderDiagnostics"][0]["renderTargetId"],
        "rt-kaiser-overlay"
    );
    assert_eq!(
        error_export["notes"][0],
        "The live bridge could not render the requested asset."
    );

    server.abort();
}

#[test]
fn assets_extract_batch_reports_shared_index_timing_for_many_offline_requests() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let output_dir = root.path().join("batch-output");

    write_png(&resources_dir.join("ui/shared/a.png"), [255, 0, 0, 255]);
    write_png(&resources_dir.join("ui/shared/b.png"), [0, 255, 0, 255]);
    write_png(&resources_dir.join("ui/shared/c.png"), [0, 0, 255, 255]);

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            { "id": "a", "query": "a.png", "execution": "offline" },
            { "id": "b", "query": "b.png", "execution": "offline" },
            { "id": "c", "query": "c.png", "execution": "offline" }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        output_dir.to_str().expect("utf8 output"),
        "--format",
        "auto",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["requestCount"], 3);
    assert!(payload["timing"]["indexMs"].is_u64());
    assert!(payload["timing"]["resolveMs"].is_u64());
    assert!(payload["timing"]["exportMs"].is_u64());

    let results = payload["results"].as_array().expect("results");
    assert_eq!(results.len(), 3);
    for result in results {
        assert_eq!(result["status"], "ok");
        assert_eq!(result["matchCount"], 1);
        assert_eq!(result["exportCount"], 1);
        assert!(result["resolveMs"].is_u64());
        assert!(result["exportMs"].is_u64());
        assert_eq!(result["liveSetupMs"], 0);
    }
}

#[test]
fn assets_extract_batch_uses_command_defaults_and_disambiguates_sanitized_ids() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let output_dir = root.path().join("batch-output");

    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            { "id": "hand raster", "query": "hand" },
            { "id": "hand/raster", "query": "hand" }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--output",
        output_dir.to_str().expect("utf8 output"),
        "--execution",
        "offline",
        "--format",
        "png",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "ok");
    let results = payload["results"].as_array().expect("results");
    assert_eq!(results[0]["executionMode"], "offline");
    assert_eq!(results[0]["format"], "png");
    assert_eq!(results[1]["executionMode"], "offline");
    assert_eq!(results[1]["format"], "png");
    assert!(
        PathBuf::from(results[0]["outputDir"].as_str().expect("outputDir"))
            .ends_with("hand_raster")
    );
    assert!(
        PathBuf::from(results[1]["outputDir"].as_str().expect("outputDir"))
            .ends_with("hand_raster-2")
    );
}

#[test]
fn assets_extract_batch_reports_partial_failures_without_discarding_successes() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            { "id": "hand", "query": "hand", "execution": "offline" },
            { "id": "missing", "query": "missing_asset", "execution": "offline" },
            { "id": "virtual_offline", "query": "model://characters/ironclad/visuals", "execution": "offline" }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 1);
    assert_eq!(payload["status"], "partial");
    assert_eq!(payload["successCount"], 1);
    assert_eq!(payload["failureCount"], 2);
    assert_eq!(payload["skippedCount"], 0);

    let results = payload["results"].as_array().expect("results");
    assert_eq!(results[0]["id"], "hand");
    assert_eq!(results[0]["status"], "ok");
    assert_eq!(results[1]["id"], "missing");
    assert_eq!(results[1]["status"], "no-match");
    assert_eq!(results[1]["error"]["code"], "asset_no_match");
    assert_eq!(results[2]["id"], "virtual_offline");
    assert_eq!(results[2]["status"], "failed");
    assert_eq!(results[2]["error"]["code"], "virtual_asset_live_required");
}

#[test]
fn assets_extract_batch_fail_fast_marks_later_requests_skipped() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);

    let manifest_path = root.path().join("asset-manifest.json");
    fs::write(
        &manifest_path,
        r#"{
          "version": 0,
          "assets": [
            { "id": "missing", "query": "missing_asset", "execution": "offline" },
            { "id": "hand", "query": "hand", "execution": "offline" }
          ]
        }"#,
    )
    .expect("write manifest");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract-batch",
        "--manifest",
        manifest_path.to_str().expect("utf8 manifest"),
        "--fail-fast",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 1);
    assert_eq!(payload["status"], "failed");
    assert_eq!(payload["successCount"], 0);
    assert_eq!(payload["failureCount"], 1);
    assert_eq!(payload["skippedCount"], 1);
    assert_eq!(payload["results"][0]["status"], "no-match");
    assert_eq!(payload["results"][1]["status"], "skipped");
    assert_eq!(
        payload["results"][1]["error"]["code"],
        "asset_batch_skipped_after_failure"
    );
}

#[test]
fn assets_extract_help_describes_query_format_and_search_roots() {
    let error = <Cli as Parser>::try_parse_from(["sts2", "assets", "extract", "--help"])
        .expect_err("help should short-circuit parsing");

    assert_eq!(error.kind(), ErrorKind::DisplayHelp);

    let help = error.to_string();
    assert!(
        help.contains("Search resource roots for asset exports with offline/live execution modes.")
    );
    assert!(help.contains("<QUERY>"));
    assert!(help.contains(
        "Asset path, filename, substring, direct res:// path, or typed semantic asset key such as `model://characters/<id>/visuals` or `model://relics/<id>/bigIcon`."
    ));
    assert!(help.contains("--execution <EXECUTION>"));
    assert!(help.contains("Choose offline-only, live-only, or auto extraction."));
    assert!(help.contains(
        "Auto exports readable configured resources offline first, then uses live IPC only when needed."
    ));
    assert!(help.contains("--format <FORMAT>"));
    assert!(help.contains("Output format for raster and live-preview exports."));
    assert!(help.contains("--resources-dir <RESOURCES_DIR>"));
    assert!(help.contains("Override the root directory used for static scene/resource search."));
    assert!(
        help.contains("Recovered project files are used only when this points at them explicitly.")
    );
    assert!(help.contains("--mods-dir <MODS_DIR>"));
    assert!(help.contains("Override the mod root used when --include-mods is set."));
    assert!(help.contains("--include-mods"));
    assert!(help.contains("Include mod-root assets in addition to the base resources root."));
    assert!(
        !help.contains("--assemblies-dir <ASSEMBLIES_DIR>"),
        "assets extract should not advertise managed-assembly overrides"
    );
}

#[test]
fn assets_extract_batch_help_describes_dry_run() {
    let error = <Cli as Parser>::try_parse_from(["sts2", "assets", "extract-batch", "--help"])
        .expect_err("help should short-circuit parsing");

    assert_eq!(error.kind(), ErrorKind::DisplayHelp);

    let help = error.to_string();
    assert!(help.contains("--dry-run"));
    assert!(help.contains(
        "Validate the batch request surface without resolving roots, launching the game, or writing artifacts."
    ));
}
