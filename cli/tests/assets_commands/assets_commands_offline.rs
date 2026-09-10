#[test]
fn assets_extract_offline_exports_packed_raster_assets() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let packed_path = resources_dir.join("packed-assets.pck");
    let png_bytes = image_bytes_for_format([7, 11, 13, 255], ImageFormat::Png);
    write_packed_resource(
        &packed_path,
        &[("res://packed/packed_icon.png", png_bytes.as_slice())],
    );

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "packed_icon",
        "--execution",
        "offline",
        "--format",
        "png",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["matchCount"], 1);
    assert_eq!(payload["exportCount"], 1);
    assert_eq!(payload["exports"][0]["artifactKind"], "raster");
    assert_eq!(payload["exports"][0]["actualFormat"], "png");
    assert_eq!(payload["exports"][0]["sourceRoot"], "resources");

    let output_path = PathBuf::from(payload["exports"][0]["outputPath"].as_str().expect("path"));
    assert!(output_path.exists(), "packed offline export should exist");
    let pixel = image::open(&output_path)
        .expect("open output")
        .to_rgba8()
        .get_pixel(0, 0)
        .0;
    assert_eq!(pixel, [7, 11, 13, 255]);
}

#[test]
fn assets_extract_offline_exports_filesystem_scene_and_packed_resource_as_source_artifacts() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    fs::create_dir_all(resources_dir.join("ui/shared")).expect("create scene dir");
    fs::write(
        resources_dir.join("ui/shared/panel.tscn"),
        "[gd_scene format=3]\n\n[node name=\"Panel\" type=\"Panel\"]\n",
    )
    .expect("write scene");
    write_packed_resource(
        &resources_dir.join("packed-assets.pck"),
        &[(
            "res://packed/theme.tres",
            b"[gd_resource type=\"Theme\" format=3]\n\n[resource]\n",
        )],
    );

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "p",
        "--execution",
        "offline",
    ]);

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["matchCount"], 2);
    assert_eq!(payload["exportCount"], 2);

    let exports = payload["exports"].as_array().expect("exports array");
    let scene = exports
        .iter()
        .find(|entry| entry["sourcePath"] == "ui/shared/panel.tscn")
        .expect("scene export");
    assert_eq!(scene["artifactKind"], "source");
    assert_eq!(scene["storageKind"], "filesystem-file");
    assert_eq!(scene["resourceType"], "PackedScene");
    assert!(scene["containerPath"].is_null());
    assert!(scene["actualFormat"].is_null());
    let scene_output = PathBuf::from(scene["outputPath"].as_str().expect("scene path"));
    assert_eq!(
        scene_output.extension().and_then(|value| value.to_str()),
        Some("tscn")
    );
    assert_eq!(
        fs::read_to_string(&scene_output).expect("read scene output"),
        "[gd_scene format=3]\n\n[node name=\"Panel\" type=\"Panel\"]\n"
    );

    let resource = exports
        .iter()
        .find(|entry| entry["sourcePath"] == "packed/theme.tres")
        .expect("resource export");
    assert_eq!(resource["artifactKind"], "source");
    assert_eq!(resource["storageKind"], "packed-entry");
    assert_eq!(resource["resourceType"], "Theme");
    assert!(
        resource["containerPath"]
            .as_str()
            .expect("container path")
            .ends_with("packed-assets.pck")
    );
    let resource_output = PathBuf::from(resource["outputPath"].as_str().expect("resource path"));
    assert_eq!(
        fs::read_to_string(&resource_output).expect("read resource output"),
        "[gd_resource type=\"Theme\" format=3]\n\n[resource]\n"
    );
}

#[test]
fn assets_extract_keeps_mixed_filesystem_and_packed_matches_in_one_batch() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    write_png(&resources_dir.join("ui/shared/icon.png"), [1, 2, 3, 255]);
    write_packed_resource(
        &resources_dir.join("packed-assets.pck"),
        &[(
            "res://packed/icon_theme.tres",
            b"[gd_resource type=\"Theme\" format=3]\n\n[resource]\n",
        )],
    );

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let payload = run_json_bin(&[
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
        "--execution",
        "offline",
        "--format",
        "png",
    ]);

    assert_eq!(payload["matchCount"], 2);
    assert_eq!(payload["exportCount"], 2);
    let exports = payload["exports"].as_array().expect("exports array");
    assert!(
        exports
            .iter()
            .any(|entry| entry["artifactKind"] == "raster")
    );
    assert!(
        exports
            .iter()
            .any(|entry| entry["artifactKind"] == "source")
    );
}

#[test]
fn assets_extract_exports_direct_raster_assets_and_reports_placeholders() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    write_png(&resources_dir.join("ui/shared/hand.png"), [255, 0, 0, 255]);
    fs::create_dir_all(resources_dir.join("assets")).expect("create tres directory");
    fs::write(
        resources_dir.join("assets/hand_icon.tres"),
        "[gd_resource type=\"Resource\" format=3 uid=\"uid://handiconfixture\"]\n\n[resource]\nresource_name = \"HandIcon\"\n",
    )
    .expect("write placeholder tres");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "hand",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["command"], "extract");
    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["format"], "auto");
    assert_eq!(payload["matchCount"], 2);
    assert_eq!(payload["exportCount"], 2);
    assert_eq!(
        PathBuf::from(payload["outputDir"].as_str().expect("outputDir")),
        artifacts_dir.join("assets")
    );

    let exports = payload["exports"].as_array().expect("exports array");
    let exported = exports
        .iter()
        .find(|entry| entry["assetKind"] == "image")
        .expect("exported asset");
    let source_export = exports
        .iter()
        .find(|entry| entry["assetKind"] == "resource")
        .expect("resource source export");

    let output_path = PathBuf::from(exported["outputPath"].as_str().expect("outputPath"));
    assert!(output_path.exists(), "exported path should exist");
    assert_eq!(exported["assetKind"], "image");
    assert_eq!(exported["executionMode"], "offline");
    assert_eq!(exported["actualFormat"], "png");

    let decoded = image::open(&output_path).expect("open exported image");
    assert_eq!(decoded.width(), 1);
    assert_eq!(decoded.height(), 1);

    assert_eq!(source_export["executionMode"], "offline");
    assert_eq!(source_export["artifactKind"], "source");
    assert_eq!(source_export["resourceType"], "Resource");
}

#[test]
fn assets_extract_keeps_resource_and_mod_exports_distinct() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let mods_dir = root.path().join("mods");
    let artifacts_dir = root.path().join("artifacts");

    write_png(&resources_dir.join("ui/shared/icon.png"), [255, 0, 0, 255]);
    write_png(&mods_dir.join("ui/shared/icon.png"), [0, 255, 0, 255]);

    let config = write_asset_config(&resources_dir, &artifacts_dir, Some(&mods_dir));
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
        "--include-mods",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["matchCount"], 2);
    assert_eq!(payload["exportCount"], 2);

    let exports = payload["exports"].as_array().expect("exports array");
    let resource_export = exports
        .iter()
        .find(|entry| entry["sourceRoot"] == "resources")
        .expect("resource export");
    let mod_export = exports
        .iter()
        .find(|entry| entry["sourceRoot"] == "mods")
        .expect("mod export");

    let resource_output = PathBuf::from(
        resource_export["outputPath"]
            .as_str()
            .expect("resource outputPath"),
    );
    let mod_output = PathBuf::from(mod_export["outputPath"].as_str().expect("mod outputPath"));

    assert_ne!(
        resource_output, mod_output,
        "resource and mod assets should not overwrite each other"
    );
    assert!(resource_output.exists(), "resource output should exist");
    assert!(mod_output.exists(), "mod output should exist");

    let resource_pixel = image::open(&resource_output)
        .expect("open resource output")
        .to_rgba8()
        .get_pixel(0, 0)
        .0;
    let mod_pixel = image::open(&mod_output)
        .expect("open mod output")
        .to_rgba8()
        .get_pixel(0, 0)
        .0;

    assert_eq!(resource_pixel, [255, 0, 0, 255]);
    assert_eq!(mod_pixel, [0, 255, 0, 255]);
}

#[test]
fn assets_extract_does_not_rediscover_previous_exports_inside_resources_root() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = resources_dir.join("artifacts");

    write_png(&resources_dir.join("ui/shared/icon.png"), [255, 0, 0, 255]);

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let first_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
    ]);
    let first_payload: Value = serde_json::from_str(&first_response.stdout).expect("json");
    assert_eq!(first_payload["matchCount"], 1);
    assert_eq!(first_payload["exportCount"], 1);

    let second_response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
    ]);
    let second_payload: Value = serde_json::from_str(&second_response.stdout).expect("json");
    let exports = second_payload["exports"].as_array().expect("exports array");

    assert_eq!(
        second_payload["matchCount"], 1,
        "prior exports under the artifacts tree should not be rediscovered as new inputs"
    );
    assert_eq!(second_payload["exportCount"], 1);
    assert_eq!(exports.len(), 1);
    assert_eq!(exports[0]["sourcePath"], "ui/shared/icon.png");
}

#[test]
fn assets_extract_exports_requested_nondefault_formats() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    write_png(&resources_dir.join("ui/shared/icon.png"), [255, 0, 0, 255]);

    let png_config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let png_response = run(&[
        "sts2",
        "--json",
        "--config",
        png_config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
        "--format",
        "png",
    ]);
    let png_payload: Value = serde_json::from_str(&png_response.stdout).expect("json");
    let png_export = png_payload["exports"]
        .as_array()
        .expect("exports array")
        .first()
        .expect("single png export");
    let png_output_path = PathBuf::from(png_export["outputPath"].as_str().expect("outputPath"));

    assert_eq!(png_payload["status"], "ok");
    assert_eq!(png_payload["format"], "png");
    assert_eq!(png_payload["matchCount"], 1);
    assert_eq!(png_payload["exportCount"], 1);
    assert!(png_output_path.exists(), "png export should exist");
    assert_eq!(
        png_output_path.extension().and_then(|value| value.to_str()),
        Some("png")
    );

    let decoded_png = image::open(&png_output_path).expect("open exported png");
    assert_eq!(decoded_png.width(), 1);
    assert_eq!(decoded_png.height(), 1);

    let webp_config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let webp_response = run(&[
        "sts2",
        "--json",
        "--config",
        webp_config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
        "--format",
        "webp",
    ]);
    let webp_payload: Value = serde_json::from_str(&webp_response.stdout).expect("json");
    let webp_export = webp_payload["exports"]
        .as_array()
        .expect("exports array")
        .first()
        .expect("single webp export");
    let webp_output_path = PathBuf::from(webp_export["outputPath"].as_str().expect("outputPath"));

    assert_eq!(webp_payload["status"], "ok");
    assert_eq!(webp_payload["format"], "webp");
    assert_eq!(webp_payload["matchCount"], 1);
    assert_eq!(webp_payload["exportCount"], 1);
    assert!(webp_output_path.exists(), "webp export should exist");
    assert_eq!(
        webp_output_path
            .extension()
            .and_then(|value| value.to_str()),
        Some("webp")
    );

    let webp_bytes = fs::read(&webp_output_path).expect("read webp export");
    assert!(
        webp_bytes.len() > 16,
        "webp export should produce a non-empty container"
    );
    assert!(
        webp_bytes.windows(4).any(|window| window == b"WEBP"),
        "webp export should advertise the webp brand"
    );
}

#[test]
fn assets_extract_auto_format_preserves_offline_raster_bytes_and_extension() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");
    let source_path = resources_dir.join("ui/shared/icon.png");

    write_png(&source_path, [12, 34, 56, 255]);
    let source_bytes = fs::read(&source_path).expect("read source png");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "icon",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let export = payload["exports"]
        .as_array()
        .expect("exports array")
        .first()
        .expect("single export");
    let output_path = PathBuf::from(export["outputPath"].as_str().expect("outputPath"));

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["format"], "auto");
    assert_eq!(export["actualFormat"], "png");
    assert!(
        export["extractionMs"].is_u64(),
        "offline export should report elapsed extraction time"
    );
    assert_eq!(
        output_path.extension().and_then(|value| value.to_str()),
        Some("png")
    );
    assert_eq!(
        fs::read(&output_path).expect("read preserved output"),
        source_bytes
    );
}

#[test]
fn assets_extract_ignores_non_asset_files_that_only_match_by_name() {
    let root = tempfile::tempdir().expect("temp root");
    let resources_dir = root.path().join("resources");
    let artifacts_dir = root.path().join("artifacts");

    fs::create_dir_all(resources_dir.join("ui")).expect("create script directory");
    fs::write(
        resources_dir.join("ui/hand.gdshader"),
        "shader_type canvas_item;\n",
    )
    .expect("write shader");

    let config = write_asset_config(&resources_dir, &artifacts_dir, None);
    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "assets",
        "extract",
        "hand",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "no-match");
    assert_eq!(payload["matchCount"], 0);
    assert_eq!(payload["exportCount"], 0);
    assert_eq!(
        payload["exports"].as_array().expect("exports array").len(),
        0,
        "non-asset files should not surface as live-required placeholders"
    );
}
