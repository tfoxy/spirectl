#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_exports_virtual_character_visual() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-virtual-character.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [96, 128, 160, 255],
        force_png_response: false,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-character-model-battlefield".to_string()),
        notes_override: Some(vec![
            "Rendered model-backed battlefield character visual for 'ironclad' via CharacterModel.CreateVisuals().".to_string(),
            "This is a virtual live asset, not a packed creature_visuals scene.".to_string(),
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
        "extract",
        "model://characters/ironclad/visuals",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["format"], "auto");
    assert_eq!(payload["executionMode"], "live");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(
        payload["exports"][0]["sourceId"],
        "model://characters/ironclad/visuals"
    );
    assert_eq!(
        payload["exports"][0]["sourcePath"],
        "model://characters/ironclad/visuals"
    );
    assert_eq!(payload["exports"][0]["sourceRoot"], "model");
    assert_eq!(payload["exports"][0]["assetKind"], "character-visual");
    assert_eq!(payload["exports"][0]["storageKind"], "model");
    assert_eq!(
        payload["exports"][0]["resourceType"],
        "CharacterModelVisual"
    );
    assert_eq!(
        payload["exports"][0]["renderMode"],
        "flattened-character-model-battlefield"
    );
    assert_eq!(payload["exports"][0]["actualFormat"], "png");
    assert!(
        payload["exports"][0]["extractionMs"].is_u64(),
        "live export should report elapsed extraction time"
    );
    assert!(
        payload["exports"][0]["outputPath"]
            .as_str()
            .expect("output path")
            .ends_with("/assets/model/character/ironclad/visuals.png")
    );
    assert!(
        payload["exports"][0]["notes"][0]
            .as_str()
            .expect("note")
            .contains("model-backed battlefield character visual")
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_exports_exact_res_scene_without_helper_match() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-exact-res-scene.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [48, 96, 144, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-full-scene-background".to_string()),
        notes_override: Some(vec![
            "Rendered full-frame background scene with the in-game ancient background container transform.".to_string(),
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
        "extract",
        "res://scenes/events/background_scenes/neow.tscn",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "live");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(
        payload["exports"][0]["sourcePath"],
        "scenes/events/background_scenes/neow.tscn"
    );
    assert_eq!(payload["exports"][0]["sourceRoot"], "resources");
    assert_eq!(payload["exports"][0]["assetKind"], "scene");
    assert_eq!(payload["exports"][0]["storageKind"], "runtime-resource");
    assert_eq!(payload["exports"][0]["resourceType"], "PackedScene");
    assert_eq!(
        payload["exports"][0]["renderMode"],
        "flattened-full-scene-background"
    );
    assert!(
        payload["exports"][0]["outputPath"]
            .as_str()
            .expect("output path")
            .ends_with("scenes/events/background_scenes/neow.png")
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_exports_exact_combat_background_scene_without_helper_match() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-combat-background.sock");

    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [12, 48, 96, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-combat-background-scene".to_string()),
        notes_override: Some(vec![
            "Rendered literal combat background root scene from res://scenes/backgrounds/overgrowth/overgrowth_background.tscn.".to_string(),
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
        "extract",
        "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "live");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(
        payload["exports"][0]["sourcePath"],
        "scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(payload["exports"][0]["sourceRoot"], "resources");
    assert_eq!(payload["exports"][0]["assetKind"], "scene");
    assert_eq!(payload["exports"][0]["storageKind"], "runtime-resource");
    assert_eq!(payload["exports"][0]["resourceType"], "PackedScene");
    assert_eq!(
        payload["exports"][0]["renderMode"],
        "flattened-combat-background-scene"
    );
    assert_eq!(payload["exports"][0]["width"], 1);
    assert_eq!(payload["exports"][0]["height"], 1);
    assert!(
        payload["exports"][0]["outputPath"]
            .as_str()
            .expect("output path")
            .ends_with("scenes/backgrounds/overgrowth/overgrowth_background.png")
    );
    assert!(
        payload["exports"][0]["notes"][0]
            .as_str()
            .expect("note")
            .contains("literal combat background root scene")
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_exports_encounter_render_targets_as_virtual_queries() {
    let cases = [
        (
            "composed://encounters/kaiser_crab_boss/background/image",
            "encounter-background",
            "composed/encounters/kaiser_crab_boss/background.png",
        ),
        (
            "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
            "encounter-overlay",
            "composed/encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay.png",
        ),
        (
            "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image",
            "encounter-part",
            "composed/encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up.png",
        ),
    ];

    for (query, socket_name, expected_suffix) in cases {
        let root = tempfile::tempdir().expect("temp root");
        let resources_dir = root.path().join("resources");
        let artifacts_dir = root.path().join("artifacts");
        let socket_path = root
            .path()
            .join(format!("spirectl-assets-{socket_name}.sock"));

        fs::create_dir_all(&resources_dir).expect("create resources dir");

        let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
        let service = AssetExtractBridgeService {
            rgba: [24, 96, 160, 255],
            force_png_response: true,
            failure_source_substring: None,
            timeline_source_substring: None,
            rpc_unimplemented: false,
            render_mode_override: Some("flattened-encounter-visual-test".to_string()),
            notes_override: Some(vec!["Rendered encounter virtual target.".to_string()]),
            expected_load_paths: Some(vec![query.to_string()]),
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
            "extract",
            query,
            "--execution",
            "live",
            "--format",
            "png",
        ]);

        assert_eq!(payload["status"], "ok");
        assert_eq!(payload["matchCount"], 1);
        assert_eq!(payload["exportCount"], 1);
        assert_eq!(payload["exports"][0]["sourceRoot"], "composed");
        assert_eq!(payload["exports"][0]["sourcePath"], query);
        assert_eq!(payload["exports"][0]["storageKind"], "composed");
        assert_eq!(payload["exports"][0]["assetKind"], "encounter-visual");
        assert_eq!(
            payload["exports"][0]["renderMode"],
            "flattened-encounter-visual-test"
        );
        assert!(
            payload["exports"][0]["outputPath"]
                .as_str()
                .expect("output path")
                .ends_with(expected_suffix)
        );

        server.abort();
    }
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn kaiser_encounter_visual_part_artifact_is_not_full_overlay_bbox() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-kaiser-artifacts.sock");
    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [24, 96, 160, 255],
        force_png_response: true,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-encounter-visual-test".to_string()),
        notes_override: None,
        expected_load_paths: Some(vec![
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

    let overlay = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
        "--execution",
        "live",
        "--format",
        "png",
    ]);
    let part = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert_eq!(
        overlay["exports"][0]["artifactChecks"][0]["status"],
        "passed"
    );
    let overlay_checks = overlay["exports"][0]["artifactChecks"]
        .as_array()
        .expect("overlay artifact checks");
    assert!(
        overlay_checks
            .iter()
            .all(|check| check["status"] == "passed")
    );
    assert_eq!(
        overlay_checks
            .iter()
            .find(|check| check["id"] == "encounter-isolated-alpha-bounds")
            .expect("overlay isolation check")["status"],
        "passed"
    );
    assert_eq!(
        overlay_checks
            .iter()
            .find(|check| check["id"] == "expected-alpha-framing")
            .expect("overlay framing check")["expectedBounds"]["y"],
        120
    );
    let part_checks = part["exports"][0]["artifactChecks"]
        .as_array()
        .expect("part artifact checks");
    assert!(part_checks.iter().all(|check| check["status"] == "passed"));
    assert_eq!(
        part_checks
            .iter()
            .find(|check| check["id"] == "encounter-isolated-alpha-bounds")
            .expect("part isolation check")["status"],
        "passed"
    );
    assert_eq!(
        part_checks
            .iter()
            .find(|check| check["id"] == "expected-alpha-framing")
            .expect("framing check")["expectedBounds"]["x"],
        1135
    );

    server.abort();
}

#[test]
fn assets_extract_offline_rejects_virtual_character_visual_with_structured_error() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    fs::create_dir_all(&resources_dir).expect("create resources dir");

    let config = write_ipc_asset_config(
        None,
        &resources_dir,
        &artifacts_dir,
        None,
        Path::new("/tmp/spirectl-unused.sock"),
        None,
        None,
    );
    let (code, payload) = run_json_bin_failure_with_env(
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "assets",
            "extract",
            "model://characters/ironclad/visuals",
            "--execution",
            "offline",
            "--format",
            "png",
        ],
        &[],
    );

    assert_ne!(code, 0);
    assert_eq!(payload["error"]["code"], "virtual_asset_live_required");
    assert_eq!(
        payload["error"]["query"],
        "model://characters/ironclad/visuals"
    );
    assert_eq!(payload["error"]["requiredExecution"][0], "live");
    assert_eq!(payload["error"]["requiredExecution"][1], "auto");
}

#[test]
fn assets_extract_old_untyped_character_key_has_no_match() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let payload = run_json_bin_with_env(
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "assets",
            "extract",
            "character:ironclad:portrait",
            "--execution",
            "live",
            "--format",
            "png",
        ],
        &[],
    );

    assert_eq!(payload["status"], "no-match");
    assert_eq!(payload["query"], "character:ironclad:portrait");
    assert_eq!(payload["matchCount"], 0);
    assert_eq!(payload["exportCount"], 0);
}

#[test]
fn assets_extract_old_colon_virtual_keys_have_no_match_or_warning() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    let config = write_asset_config(&resources_dir, &artifacts_dir, None);

    for query in [
        "model:character:ironclad:selectIcon",
        "model:character:ironclad:selectBg",
        "composed:combat-background:overgrowth:image",
    ] {
        let payload = run_json_bin_with_env(
            &[
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "assets",
                "extract",
                query,
                "--execution",
                "live",
                "--format",
                "png",
            ],
            &[],
        );

        assert_eq!(payload["status"], "no-match", "{query}");
        assert_eq!(payload["query"], query);
        assert_eq!(payload["matchCount"], 0);
        assert_eq!(payload["exportCount"], 0);
        let payload_text = serde_json::to_string(&payload).expect("serialize payload");
        assert!(!payload_text.contains("deprecat"), "{query}");
    }
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_auto_uses_live_bridge_for_placeholder_matches_when_attached() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-live.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/hand_panel.tscn"),
        "[gd_scene format=3]\n[node name=\"HandPanel\" type=\"Control\"]\n",
    )
    .expect("write scene");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [0, 255, 0, 255],
        force_png_response: false,
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
        "extract",
        "hand_panel",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "auto");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["assetKind"], "scene");
    assert_eq!(payload["exports"][0]["executionMode"], "offline");
    assert_eq!(payload["exports"][0]["status"], "exported");
    assert_eq!(payload["exports"][0]["artifactKind"], "source");

    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert!(output_path.exists(), "offline source export should exist");
    assert_eq!(
        fs::read_to_string(&output_path).expect("read source output"),
        "[gd_scene format=3]\n[node name=\"HandPanel\" type=\"Control\"]\n"
    );

    server.abort();
}

#[test]
fn assets_extract_auto_exports_readable_font_offline_without_live_fallback() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("missing-live.sock");

    fs::create_dir_all(resources_dir.join("fonts")).expect("create font dir");
    fs::write(
        resources_dir.join("fonts/kreon_regular.ttf"),
        b"\0\x01\0\0fake-font-bytes",
    )
    .expect("write font");

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
        "extract",
        "kreon_regular",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "auto");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["assetKind"], "font");
    assert_eq!(payload["exports"][0]["executionMode"], "offline");
    assert_eq!(payload["exports"][0]["artifactKind"], "font");
    assert_eq!(payload["exports"][0]["contentType"], "font/ttf");
    assert_eq!(
        fs::read(PathBuf::from(
            payload["exports"][0]["outputPath"]
                .as_str()
                .expect("output path")
        ))
        .expect("read font output"),
        b"\0\x01\0\0fake-font-bytes"
    );
}

#[test]
fn assets_extract_auto_does_not_search_recovered_project_implicitly() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join(".sts2/artifacts");
    let recovered_font = root
        .path()
        .join(".sts2/toolchain/recovered-project/fonts/kreon_regular.ttf");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::create_dir_all(recovered_font.parent().expect("font parent"))
        .expect("create recovered font dir");
    fs::write(&recovered_font, b"\0\x01\0\0stale-recovered-font").expect("write font");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "res://fonts/kreon_regular.ttf",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 0);
    assert_eq!(payload["exports"][0]["assetKind"], "font");
    assert_eq!(payload["exports"][0]["status"], "unsupported_live_required");
    assert!(payload["exports"][0]["outputPath"].is_null());

    let payload_text = serde_json::to_string(&payload).expect("payload json");
    assert!(!payload_text.contains("recovered-project"));
    assert!(!payload_text.contains("source-font-fallback"));
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_font_does_not_use_recovered_project_fallback() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join(".sts2/artifacts");
    let socket_path = root.path().join("spirectl-assets-font-live.sock");
    let recovered_font = root
        .path()
        .join(".sts2/toolchain/recovered-project/fonts/kreon_regular.ttf");

    fs::create_dir_all(&resources_dir).expect("create resources dir");
    fs::create_dir_all(recovered_font.parent().expect("font parent"))
        .expect("create recovered font dir");
    fs::write(&recovered_font, b"\0\x01\0\0stale-recovered-font").expect("write font");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [0, 0, 0, 255],
        force_png_response: false,
        failure_source_substring: Some("fonts/kreon_regular.ttf".to_string()),
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
        "extract",
        "res://fonts/kreon_regular.ttf",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "live");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 0);
    assert_eq!(payload["exports"][0]["assetKind"], "font");
    assert_eq!(payload["exports"][0]["executionMode"], "live");
    assert_eq!(payload["exports"][0]["status"], "unsupported_live_required");
    assert!(payload["exports"][0]["outputPath"].is_null());

    let payload_text = serde_json::to_string(&payload).expect("payload json");
    assert!(!payload_text.contains("recovered-project"));
    assert!(!payload_text.contains("source-font-fallback"));

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_explicit_format_uses_live_bridge_when_offline_raster_decode_fails() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-corrupt-raster.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create image dir");
    fs::write(
        resources_dir.join("ui/shared/corrupt_hand.png"),
        b"not-a-valid-png",
    )
    .expect("write corrupt png");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [12, 34, 56, 255],
        force_png_response: false,
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
        "extract",
        "corrupt_hand",
        "--format",
        "webp",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "auto");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["assetKind"], "image");
    assert_eq!(payload["exports"][0]["executionMode"], "live");
    assert_eq!(payload["exports"][0]["status"], "exported");

    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert!(output_path.exists(), "live export should exist");
    let pixel = image::open(&output_path)
        .expect("open output")
        .to_rgba8()
        .get_pixel(0, 0)
        .0;
    assert_eq!(pixel, [12, 34, 56, 255]);

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_auto_launches_bridge_for_live_required_matches_when_detached() {
    let game_dir = create_fake_game_layout();
    let resources_dir = game_dir.path().join("resources");
    let artifacts_dir = game_dir.path().join("artifacts");
    let socket_path = game_dir.path().join("spirectl-assets-launch.sock");
    let launch_capture = game_dir.path().join("launch-assets-env.txt");
    let fake_bin_dir = tempfile::tempdir().expect("fake bin dir");
    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/map_node.tscn"),
        "[gd_scene format=3]\n[node name=\"MapNode\" type=\"Control\"]\n",
    )
    .expect("write scene");

    let real_dotnet = current_dotnet_path();
    let dotnet = write_executable_script(
        fake_bin_dir.path(),
        "dotnet",
        r#"#!/usr/bin/env bash
set -euo pipefail
command_name="${1:-}"
shift || true

if [ "$command_name" = "build" ]; then
  project="${1:?missing build project}"
  shift
  configuration="Debug"
  while [ $# -gt 0 ]; do
    case "$1" in
      --configuration)
        configuration="${2:?missing configuration}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  output_dir="$(dirname "$project")/bin/$configuration/net9.0"
  mkdir -p "$output_dir"
  : > "$output_dir/spirectlbridge.dll"
  : > "$output_dir/spirectlbridge.pdb"
  exit 0
fi

if [ "$command_name" = "publish" ]; then
  project="${1:?missing publish project}"
  shift
  output_dir=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --output)
        output_dir="${2:?missing output dir}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  mkdir -p "$output_dir"
  : > "$output_dir/Spirectl.BridgeMod.Sts2Host.dll"
  : > "$output_dir/Spirectl.BridgeMod.dll"
  exit 0
fi

if [ "$command_name" = "run" ]; then
  exec "${SPIRECTL_TEST_REAL_DOTNET:?missing real dotnet path}" run "$@"
fi

echo "unexpected dotnet command: $command_name" >&2
exit 1
"#,
    );
    assert!(dotnet.exists());

    let launch_script = write_executable_script(
        game_dir.path(),
        "launch-assets.sh",
        &format!(
            "#!/bin/sh\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\nsleep 1\n",
            launch_capture.display()
        ),
    );

    let delayed_socket_path = socket_path.clone();
    let delayed_launch_capture = launch_capture.clone();
    let delayed_server = tokio::spawn(async move {
        for _ in 0..400 {
            if delayed_launch_capture.is_file()
                && fs::metadata(&delayed_launch_capture)
                    .map(|metadata| metadata.len() > 0)
                    .unwrap_or(false)
            {
                break;
            }
            sleep(Duration::from_millis(10)).await;
        }
        let listener = UnixListener::bind(&delayed_socket_path).expect("bind unix listener");
        let service = AssetExtractBridgeService {
            rgba: [255, 0, 255, 255],
            force_png_response: false,
            failure_source_substring: None,
            timeline_source_substring: None,
            rpc_unimplemented: false,
            render_mode_override: None,
            notes_override: None,
            expected_load_paths: None,
        };
        ipc_bridge_support::serve_unix_bridge_service(listener, service).await;
    });

    let config = write_ipc_asset_config(
        Some(game_dir.path()),
        &resources_dir,
        &artifacts_dir,
        Some(&game_dir.path().join("mods")),
        &socket_path,
        Some(&launch_script),
        Some(game_dir.path()),
    );
    let path_env = format!(
        "{}:{}",
        fake_bin_dir.path().display(),
        std::env::var("PATH").unwrap_or_default()
    );
    let payload = run_json_bin_with_env(
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "assets",
            "extract",
            "map_node",
        ],
        &[
            ("PATH", path_env.as_str()),
            ("SPIRECTL_TEST_REAL_DOTNET", real_dotnet.as_str()),
        ],
    );

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "auto");
    assert_eq!(
        payload["exports"][0]["executionMode"],
        "offline",
        "payload: {}",
        serde_json::to_string_pretty(&payload).expect("payload json")
    );
    assert_eq!(payload["exports"][0]["status"], "exported");
    assert_eq!(payload["exports"][0]["artifactKind"], "source");

    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert!(output_path.exists(), "offline source export should exist");
    assert_eq!(
        fs::read_to_string(&output_path).expect("read source output"),
        "[gd_scene format=3]\n[node name=\"MapNode\" type=\"Control\"]\n"
    );
    let bridge_manifest = game_dir
        .path()
        .join("mods/spirectlbridge/spirectlbridge.json");
    assert!(
        !bridge_manifest.exists(),
        "auto mode should not install or launch the bridge when offline source export is available"
    );

    delayed_server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_reports_legacy_bridge_rpc_gaps_as_session_failures() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-legacy.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/legacy_scene.tscn"),
        "[gd_scene format=3]\n[node name=\"LegacyScene\" type=\"Control\"]\n",
    )
    .expect("write scene");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [0, 0, 0, 0],
        force_png_response: false,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: true,
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
    let (exit_code, payload) = run_json_bin_failure_with_env(
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "assets",
            "extract",
            "legacy_scene",
            "--execution",
            "live",
        ],
        &[],
    );

    assert_eq!(exit_code, 3);
    assert_eq!(payload["error"]["code"], "not_implemented");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("error message")
            .contains("ExtractAsset"),
        "payload: {}",
        serde_json::to_string_pretty(&payload).expect("payload json")
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_transcodes_bridge_png_response_to_requested_webp() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-webp.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/card_art.tres"),
        "[gd_resource type=\"Resource\" format=3]\n",
    )
    .expect("write resource");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [32, 64, 128, 255],
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
        "extract",
        "card_art",
        "--execution",
        "live",
        "--format",
        "webp",
    ]);

    assert_eq!(payload["exports"][0]["executionMode"], "live");
    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert_eq!(
        output_path.extension().and_then(|value| value.to_str()),
        Some("webp")
    );
    let bytes = fs::read(&output_path).expect("read webp");
    assert!(bytes.windows(4).any(|window| window == b"WEBP"));

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_preserves_standalone_skeleton_preview_metadata() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-skeleton-preview.sock");

    fs::create_dir_all(resources_dir.join("monsters/nibbit")).expect("create skeleton dir");
    fs::write(
        resources_dir.join("monsters/nibbit/nibbit_skel_data.tres"),
        "[gd_resource type=\"Resource\" format=3]\n",
    )
    .expect("write skeleton resource");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [128, 64, 32, 255],
        force_png_response: false,
        failure_source_substring: None,
        timeline_source_substring: None,
        rpc_unimplemented: false,
        render_mode_override: Some("flattened-spine-skeleton-resource-preview".to_string()),
        notes_override: Some(vec![
            "Rendered standalone skeleton data through a synthetic Spine preview node; composed scenes remain the preferred exact in-game visual path.".to_string(),
            "Applied deterministic skeleton preview defaults: skin 'default', animation 'idle_loop', scale 1x1, material 'runtime/default', bounds [synthetic].".to_string(),
            "Skeleton data alone does not define authored scene placement; preview bounds are synthetic and may differ from composed in-game scenes.".to_string(),
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
        "extract",
        "nibbit_skel_data",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["status"], "exported");
    assert_eq!(payload["exports"][0]["executionMode"], "live");
    assert_eq!(payload["exports"][0]["artifactKind"], "raster");
    assert_eq!(
        payload["exports"][0]["renderMode"],
        "flattened-spine-skeleton-resource-preview"
    );
    assert!(
        payload["exports"][0]["notes"][0]
            .as_str()
            .expect("note")
            .contains("synthetic Spine preview node")
    );
    assert!(
        payload["exports"][0]["notes"][2]
            .as_str()
            .expect("note")
            .contains("preview bounds are synthetic")
    );

    let output_path = PathBuf::from(
        payload["exports"][0]["outputPath"]
            .as_str()
            .expect("output path"),
    );
    assert!(output_path.exists(), "skeleton preview raster should exist");

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_keeps_batch_results_when_one_match_cannot_render() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-partial-live.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/renderable_asset.tscn"),
        "[gd_scene format=3]\n[node name=\"RenderableAsset\" type=\"Control\"]\n",
    )
    .expect("write scene");
    fs::write(
        resources_dir.join("ui/shared/unsupported_asset.tres"),
        "[gd_resource type=\"Resource\" format=3]\n",
    )
    .expect("write resource");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [16, 32, 48, 255],
        force_png_response: false,
        failure_source_substring: Some("unsupported_asset".to_string()),
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
        "extract",
        "asset",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["executionMode"], "live");
    assert_eq!(payload["matchCount"], 2);
    assert_eq!(payload["exportCount"], 1);

    let exports = payload["exports"].as_array().expect("exports array");
    let exported = exports
        .iter()
        .find(|entry| entry["sourcePath"] == "ui/shared/renderable_asset.tscn")
        .expect("exported asset");
    let unsupported = exports
        .iter()
        .find(|entry| entry["status"] == "unsupported_live_required")
        .expect("unsupported asset");

    assert_eq!(exported["executionMode"], "live");
    assert_eq!(exported["contentType"], "image/png");
    assert_eq!(exported["provenance"]["sourceRoot"], "resources");
    assert_eq!(exported["notices"][0]["code"], "asset-key-partial");
    assert_eq!(unsupported["executionMode"], "live");
    assert_eq!(
        unsupported["sourcePath"],
        "ui/shared/unsupported_asset.tres"
    );
    assert_eq!(
        unsupported["notes"][0],
        "The live bridge could not render the requested asset."
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_removes_stale_raster_when_match_cannot_render() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-stale-live.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create resource dir");
    fs::write(
        resources_dir.join("ui/shared/unsupported_asset.tres"),
        "[gd_resource type=\"Resource\" format=3]\n",
    )
    .expect("write resource");

    let stale_output = artifacts_dir
        .join("assets")
        .join("resources")
        .join("ui/shared/unsupported_asset.png");
    fs::create_dir_all(stale_output.parent().expect("stale parent")).expect("create stale parent");
    fs::write(&stale_output, b"stale transparent png").expect("write stale output");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [16, 32, 48, 255],
        force_png_response: false,
        failure_source_substring: Some("unsupported_asset".to_string()),
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
        "extract",
        "unsupported_asset",
        "--execution",
        "live",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["exportCount"], 0);
    assert_eq!(payload["exports"][0]["status"], "unsupported_live_required");
    assert!(
        !stale_output.exists(),
        "failed live exports should remove stale deterministic raster outputs"
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn assets_extract_live_writes_timeline_manifest_and_frames() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let socket_path = root.path().join("spirectl-assets-timeline.sock");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create resource dir");
    fs::write(
        resources_dir.join("ui/shared/anim.tres"),
        "[gd_resource type=\"Resource\" format=3]\n",
    )
    .expect("write resource");

    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = AssetExtractBridgeService {
        rgba: [0, 0, 0, 0],
        force_png_response: false,
        failure_source_substring: None,
        timeline_source_substring: Some("anim".to_string()),
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
        "extract",
        "anim",
        "--execution",
        "live",
        "--format",
        "png",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["artifactKind"], "timeline");
    assert_eq!(payload["exports"][0]["frameCount"], 2);
    assert_eq!(payload["exports"][0]["durationMs"], 200);
    assert_eq!(payload["exports"][0]["renderMode"], "timeline-frames");

    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert_eq!(
        output_path.extension().and_then(|value| value.to_str()),
        Some("json")
    );
    assert!(output_path.exists(), "timeline manifest should exist");

    let frames_dir = PathBuf::from(
        payload["exports"][0]["framesDir"]
            .as_str()
            .expect("framesDir"),
    );
    assert!(frames_dir.is_dir(), "timeline frames dir should exist");
    assert!(frames_dir.join("frame-0000.png").exists());
    assert!(frames_dir.join("frame-0001.png").exists());

    let manifest: Value =
        serde_json::from_slice(&fs::read(&output_path).expect("read timeline manifest"))
            .expect("parse timeline manifest");
    assert_eq!(manifest["frameCount"], 2);
    assert_eq!(manifest["durationMs"], 200);
    assert_eq!(manifest["frames"][0]["durationMs"], 120);
    assert_eq!(manifest["frames"][1]["durationMs"], 80);

    server.abort();
}
