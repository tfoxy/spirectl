use super::{
    AssetCandidate, AssetDiscoveryIndex, AssetFormatArg, AssetKind, AssetRequestTiming,
    asset_search_root_error, cached_live_raster_export_record, discover_assets_from_index,
    export_or_record_offline, export_stage_ms, parse_helper_json,
    try_parse_exact_runtime_resource_query,
};
use crate::AppConfig;
use image::{ImageFormat, Rgba, RgbaImage};
use serde::Deserialize;
use std::fs;
use std::path::PathBuf;

#[derive(Debug, Deserialize)]
struct TinyHelperResponse {
    status: String,
}

#[test]
fn asset_search_root_errors_use_asset_extract_wording_for_missing_resources() {
    let error = asset_search_root_error(
            "Static scene inspection requires --resources-dir, game.resourcesDir, or an explicit game.path."
                .to_string(),
        );

    assert_eq!(error.exit_code, 2);
    assert_eq!(error.payload["error"]["code"], "invalid_asset_search_roots");
    assert_eq!(
        error.payload["error"]["message"],
        "Asset extraction requires --resources-dir, game.resourcesDir, or an explicit game.path."
    );
}

#[test]
fn asset_extract_reuses_cached_flattened_tres_resource() {
    let temp = tempfile::tempdir().expect("temp dir");
    let candidate = try_parse_exact_runtime_resource_query(
        "res://images/atlases/ui_atlas.sprites/back_button.tres",
    )
    .expect("exact runtime resource candidate");
    let cached_path = temp
        .path()
        .join("resources/images/atlases/ui_atlas.sprites/back_button.png");
    fs::create_dir_all(cached_path.parent().expect("parent")).expect("create cache dir");
    RgbaImage::from_pixel(1, 1, Rgba([255, 255, 255, 255]))
        .save_with_format(&cached_path, ImageFormat::Png)
        .expect("write cached png");

    let record = cached_live_raster_export_record(&candidate, temp.path(), AssetFormatArg::Auto)
        .expect("cached record");

    assert_eq!(record.status, "exported");
    assert_eq!(record.execution_mode, "cache");
    assert_eq!(record.content_type.as_deref(), Some("image/png"));
    assert_eq!(
        record.output_path.as_deref(),
        Some(cached_path.to_str().expect("utf8"))
    );
}

#[test]
fn asset_search_root_errors_use_asset_extract_wording_for_include_mods() {
    let error = asset_search_root_error(
            "Static inspection with --include-mods requires --mods-dir, game.modsDir, or an explicit game.path."
                .to_string(),
        );

    assert_eq!(error.exit_code, 2);
    assert_eq!(
        error.payload["error"]["message"],
        "Asset extraction with --include-mods requires --mods-dir, game.modsDir, or an explicit game.path."
    );
}

#[test]
fn helper_json_parser_ignores_leading_dotnet_noise() {
    let parsed: TinyHelperResponse =
        parse_helper_json(b"Determining projects to restore...\n{\"status\":\"ok\"}")
            .expect("parse helper json with leading noise");

    assert_eq!(parsed.status, "ok");
}

#[test]
fn helper_json_parser_ignores_leading_msbuild_warning_paths() {
    let parsed: TinyHelperResponse = parse_helper_json(
            b"/tmp/sdk/Microsoft.Common.CurrentVersion.targets(4917,5): warning MSB3026: retry [details]\n{\"status\":\"ok\"}",
        )
        .expect("parse helper json after MSBuild warning noise");

    assert_eq!(parsed.status, "ok");
}

#[test]
fn helper_json_parser_skips_non_payload_braces_in_leading_noise() {
    let parsed: TinyHelperResponse =
        parse_helper_json(b"MSBuild warning: property {NotJson}\n{\"status\":\"ok\"}")
            .expect("parse helper json after brace-containing noise");

    assert_eq!(parsed.status, "ok");
}

#[test]
fn helper_json_parser_ignores_leading_noise_before_array_payloads() {
    let parsed: Vec<TinyHelperResponse> =
        parse_helper_json(b"Determining projects to restore...\n[{\"status\":\"ok\"}]")
            .expect("parse helper json array after leading noise");

    assert_eq!(parsed[0].status, "ok");
}

#[test]
fn batch_asset_index_filters_by_path_and_stem_without_helper_search() {
    let index = AssetDiscoveryIndex {
        candidates: vec![
            AssetCandidate {
                source_root: "resources".to_string(),
                logical_path: "res://ui/shared/hand.png".to_string(),
                source_path: "ui/shared/hand.png".to_string(),
                relative_path: PathBuf::from("ui/shared/hand.png"),
                asset_kind: AssetKind::Image,
                storage_kind: "filesystem-file".to_string(),
                load_path: "/tmp/resources/ui/shared/hand.png".to_string(),
                file_path: Some(PathBuf::from("/tmp/resources/ui/shared/hand.png")),
                container_path: None,
                offline_readable: true,
                resource_type: None,
            },
            AssetCandidate {
                source_root: "resources".to_string(),
                logical_path: "res://packed/theme.tres".to_string(),
                source_path: "packed/theme.tres".to_string(),
                relative_path: PathBuf::from("packed/theme.tres"),
                asset_kind: AssetKind::Resource,
                storage_kind: "packed-entry".to_string(),
                load_path: "res://packed/theme.tres".to_string(),
                file_path: None,
                container_path: Some(PathBuf::from("/tmp/resources/packed-assets.pck")),
                offline_readable: true,
                resource_type: Some("Theme".to_string()),
            },
        ],
    };

    let hand = discover_assets_from_index(&index, "hand");
    assert_eq!(hand.len(), 1);
    assert_eq!(hand[0].source_path, "ui/shared/hand.png");

    let hand_by_logical_path = discover_assets_from_index(&index, "res://ui/shared/hand.png");
    assert_eq!(hand_by_logical_path.len(), 1);
    assert_eq!(hand_by_logical_path[0].source_path, "ui/shared/hand.png");

    let theme = discover_assets_from_index(&index, "theme");
    assert_eq!(theme.len(), 1);
    assert_eq!(theme[0].source_path, "packed/theme.tres");

    let root_name = discover_assets_from_index(&index, "resources");
    assert!(root_name.is_empty());

    let missing = discover_assets_from_index(&index, "missing");
    assert!(missing.is_empty());
}

#[test]
fn combat_background_alias_resolves_to_runtime_scene_candidate() {
    let candidate = super::try_parse_composed_combat_background_query(
        "composed://combat-background/OverGrowth/image",
    )
    .expect("combat background alias");

    assert_eq!(candidate.source_root, "resources");
    assert_eq!(
        candidate.logical_path,
        "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(
        candidate.source_path,
        "scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(
        candidate.relative_path,
        PathBuf::from("scenes/backgrounds/overgrowth/overgrowth_background.tscn")
    );
    assert_eq!(candidate.asset_kind, AssetKind::Scene);
    assert_eq!(candidate.storage_kind, "composed");
    assert_eq!(
        candidate.load_path,
        "composed://combat-background/overgrowth/image"
    );
    assert!(!candidate.offline_readable);
    assert_eq!(candidate.resource_type.as_deref(), Some("PackedScene"));
}

#[test]
fn exact_runtime_resource_query_keeps_literal_load_path() {
    let candidate = super::try_parse_exact_runtime_resource_query(
        "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
    )
    .expect("exact runtime resource");

    assert_eq!(
        candidate.source_path,
        "scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
    assert_eq!(
        candidate.load_path,
        "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"
    );
}

#[test]
fn localization_json_query_routes_to_live_seam() {
    // The exact-runtime parser excludes `.json`, so the loc seam relies on the
    // dedicated localization branch to be reachable from `assets extract`.
    assert!(
        super::try_parse_exact_runtime_resource_query("res://localization/eng/cards.json")
            .is_none()
    );

    let candidate =
        super::try_parse_localization_resource_query("res://localization/eng/cards.json")
            .expect("localization resource candidate");

    assert_eq!(candidate.source_root, "resources");
    assert_eq!(candidate.source_path, "localization/eng/cards.json");
    assert_eq!(candidate.load_path, "res://localization/eng/cards.json");
    assert_eq!(candidate.asset_kind, AssetKind::Resource);
    assert!(candidate.asset_kind.is_reportable());
    assert!(!candidate.offline_readable);
    assert_eq!(
        candidate.resource_type.as_deref(),
        Some("LocalizationTable")
    );

    // Discovery runs on the `normalize_query` output (res:// stripped + lowercased),
    // so the branch must accept that form too — this is the form the real flow passes.
    let normalized = super::try_parse_localization_resource_query("localization/eng/cards.json")
        .expect("normalized localization candidate");
    assert_eq!(normalized.load_path, "res://localization/eng/cards.json");

    // Non-localization JSON is left to the generic seam handling, not this branch.
    assert!(super::try_parse_localization_resource_query("res://config/settings.json").is_none());
}

#[test]
fn model_monster_visuals_query_is_model_backed_scene_candidate() {
    let candidate =
        super::try_parse_model_resource_asset_query("model://monsters/Jaw-Worm/visuals")
            .expect("monster visuals model query");

    assert_eq!(candidate.source_root, "model");
    assert_eq!(candidate.logical_path, "model://monsters/jaw-worm/visuals");
    assert_eq!(candidate.asset_kind, AssetKind::Image);
    assert_eq!(candidate.storage_kind, "model");
    assert_eq!(candidate.load_path, "model://monsters/jaw-worm/visuals");
    assert_eq!(
        candidate.resource_type.as_deref(),
        Some("MonsterModelVisuals")
    );

    let resolved = super::resolve_asset_key_response("model://monsters/jaw-worm/visuals");
    assert_eq!(resolved.status, "ok");
    assert_eq!(resolved.model_type, Some("monsters"));
    assert_eq!(resolved.model_property, Some("VisualsPath"));
}

#[test]
fn model_character_public_asset_queries_are_canonicalized() {
    let icon_outline =
        super::try_parse_model_character_visual_query("model://characters/IRONCLAD/iconOutline")
            .expect("character icon outline model query");
    assert_eq!(
        super::canonical_model_character_key(&icon_outline.character_id, &icon_outline.variant),
        "model://characters/ironclad/iconOutline"
    );

    let map_marker =
        super::try_parse_model_character_visual_query("model://characters/ironclad/mapMarker")
            .expect("character map marker model query");
    assert_eq!(
        super::canonical_model_character_key(&map_marker.character_id, &map_marker.variant),
        "model://characters/ironclad/mapMarker"
    );

    let energy_counter =
        super::try_parse_model_resource_asset_query("model://characters/ironclad/energyCounter")
            .expect("character energy counter model query");
    assert_eq!(
        energy_counter.logical_path,
        "model://characters/ironclad/energyCounter"
    );
    assert_eq!(
        energy_counter.resource_type.as_deref(),
        Some("CharacterModelEnergyCounter")
    );

    let bg_spine_still = super::try_parse_model_resource_asset_query(
        "model://characters/ironclad/characterSelectBgSpineStill",
    )
    .expect("character select background spine still model query");
    assert_eq!(
        bg_spine_still.logical_path,
        "model://characters/ironclad/characterSelectBgSpineStill"
    );
    assert_eq!(
        bg_spine_still.resource_type.as_deref(),
        Some("CharacterSelectBgSpineStill")
    );

    let merchant_bg_spine_still = super::try_parse_model_resource_asset_query(
        "model://rooms/merchant_room/backgroundSpineStill",
    )
    .expect("merchant room background spine still model query");
    assert_eq!(
        merchant_bg_spine_still.logical_path,
        "model://rooms/merchant_room/backgroundSpineStill"
    );
    assert_eq!(
        merchant_bg_spine_still.resource_type.as_deref(),
        Some("RoomModelBackgroundSpineStill")
    );

    let resolved = super::resolve_asset_key_response("model://characters/ironclad/mapMarker");
    assert_eq!(resolved.status, "ok");
    assert_eq!(resolved.model_type, Some("characters"));
    assert_eq!(resolved.model_property, Some("MapMarker"));

    let resolved_spine_still = super::resolve_asset_key_response(
        "model://characters/ironclad/characterSelectBgSpineStill",
    );
    assert_eq!(resolved_spine_still.status, "ok");
    assert_eq!(
        resolved_spine_still.model_property,
        Some("CharacterSelectBgSpineStill")
    );
    assert_eq!(
        resolved_spine_still.render_mode,
        Some("flattened-character-select-bg-spine-still")
    );
}

#[test]
fn model_relic_public_texture_queries_are_model_candidates() {
    let outline =
        super::try_parse_model_resource_asset_query("model://relics/burning-blood/iconOutline")
            .expect("relic icon outline model query");
    assert_eq!(
        outline.logical_path,
        "model://relics/burning-blood/iconOutline"
    );
    assert_eq!(
        outline.resource_type.as_deref(),
        Some("RelicModelIconOutline")
    );

    let big_icon =
        super::try_parse_model_resource_asset_query("model://relics/burning-blood/bigIcon")
            .expect("relic big icon model query");
    assert_eq!(
        big_icon.logical_path,
        "model://relics/burning-blood/bigIcon"
    );
    assert_eq!(big_icon.resource_type.as_deref(), Some("RelicModelBigIcon"));

    let resolved = super::resolve_asset_key_response("model://relics/burning-blood/bigIcon");
    assert_eq!(resolved.status, "ok");
    assert_eq!(resolved.model_type, Some("relics"));
    assert_eq!(resolved.model_property, Some("BigIcon"));
}

#[test]
fn model_event_queries_are_property_specific() {
    let background =
        super::try_parse_model_resource_asset_query("model://events/Fake-Merchant/backgroundScene")
            .expect("event background scene model query");
    assert_eq!(
        background.logical_path,
        "model://events/fake-merchant/backgroundScene"
    );
    assert_eq!(
        background.resource_type.as_deref(),
        Some("EventModelBackgroundScene")
    );

    let portrait =
        super::try_parse_model_resource_asset_query("model://events/Fake-Merchant/initialPortrait")
            .expect("event initial portrait model query");
    assert_eq!(
        portrait.logical_path,
        "model://events/fake-merchant/initialPortrait"
    );
    assert_eq!(
        portrait.resource_type.as_deref(),
        Some("EventModelInitialPortrait")
    );

    assert!(
        super::try_parse_model_resource_asset_query("model://events/fake-merchant/background")
            .is_none()
    );

    let resolved =
        super::resolve_asset_key_response("model://events/fake-merchant/backgroundScene");
    assert_eq!(resolved.status, "ok");
    assert_eq!(resolved.model_type, Some("events"));
    assert_eq!(resolved.model_property, Some("BackgroundScenePath"));
}

#[test]
fn expanded_model_queries_are_canonicalized() {
    let card_overlay =
        super::try_parse_model_resource_asset_query("model://cards/Strike-Ironclad/overlay")
            .expect("card overlay model query");
    assert_eq!(
        card_overlay.logical_path,
        "model://cards/strike-ironclad/overlay"
    );
    assert_eq!(
        card_overlay.resource_type.as_deref(),
        Some("CardModelOverlay")
    );

    let potion_outline =
        super::try_parse_model_resource_asset_query("model://potions/Fire-Potion/outline")
            .expect("potion outline model query");
    assert_eq!(
        potion_outline.logical_path,
        "model://potions/fire-potion/outline"
    );
    assert_eq!(
        potion_outline.resource_type.as_deref(),
        Some("PotionModelOutline")
    );

    let resolved_potion_outline =
        super::resolve_asset_key_response("model://potions/Fire-Potion/outline");
    assert_eq!(resolved_potion_outline.status, "ok");
    assert_eq!(resolved_potion_outline.resolution_kind, Some("model"));
    assert_eq!(resolved_potion_outline.model_type, Some("potions"));
    assert_eq!(
        resolved_potion_outline.model_id.as_deref(),
        Some("fire-potion")
    );
    assert_eq!(resolved_potion_outline.model_property, Some("OutlinePath"));

    let catalog_entries = super::asset_catalog_entries();
    let potion_outline_catalog = catalog_entries
        .iter()
        .find(|entry| entry.key_pattern == "model://potions/<id>/outline")
        .expect("potion outline catalog entry");
    assert_eq!(potion_outline_catalog.resolution_kind, "model");
    assert_eq!(potion_outline_catalog.model_type, Some("potions"));
    assert_eq!(potion_outline_catalog.model_property, Some("OutlinePath"));
    assert_eq!(potion_outline_catalog.render_mode, Some("flattened-static"));

    let ancient_map =
        super::try_parse_model_resource_asset_query("model://events/Neow/mapIconOutline")
            .expect("ancient map icon outline model query");
    assert_eq!(
        ancient_map.logical_path,
        "model://events/neow/mapIconOutline"
    );
    assert_eq!(
        ancient_map.resource_type.as_deref(),
        Some("AncientEventModelMapIconOutline")
    );

    let act_chest =
        super::try_parse_model_resource_asset_query("model://acts/Overgrowth/chestSpine")
            .expect("act chest spine model query");
    assert_eq!(act_chest.logical_path, "model://acts/overgrowth/chestSpine");
    assert_eq!(
        act_chest.resource_type.as_deref(),
        Some("ActModelChestSpine")
    );
}

#[test]
fn act_and_encounter_model_keys_resolve_with_metadata() {
    // Deterministic act sub-asset: 1:1 to its res:// path.
    let map_top = super::resolve_asset_key_response("model://acts/Overgrowth/mapTopBg");
    assert_eq!(map_top.status, "ok");
    assert_eq!(map_top.model_type, Some("acts"));
    assert_eq!(map_top.model_property, Some("MapTopBgPath"));

    // Per-layer key with a dynamic <stem> segment.
    let layer = super::try_parse_model_resource_asset_query(
        "model://acts/Overgrowth/backgroundLayer/overgrowth_bg_00_a",
    )
    .expect("act background layer query");
    assert_eq!(
        layer.logical_path,
        "model://acts/overgrowth/backgroundLayer/overgrowth_bg_00_a"
    );
    assert_eq!(layer.asset_kind, AssetKind::Scene);
    let layer_meta = super::resolve_asset_key_response(
        "model://acts/Overgrowth/backgroundLayer/overgrowth_bg_00_a",
    );
    assert_eq!(layer_meta.model_type, Some("acts"));
    assert_eq!(layer_meta.model_property, Some("layers"));

    // Encounter setup scene + custom background root.
    let scene = super::resolve_asset_key_response("model://encounters/VantomBoss/scene");
    assert_eq!(scene.status, "ok");
    assert_eq!(scene.model_type, Some("encounters"));
    assert_eq!(scene.model_property, Some("ScenePath"));

    let background = super::resolve_asset_key_response("model://encounters/VantomBoss/background");
    assert_eq!(background.status, "ok");
    assert_eq!(background.model_type, Some("encounters"));
    assert_eq!(background.model_property, Some("BackgroundScenePath"));
}

#[test]
fn exact_runtime_font_query_keeps_literal_load_path() {
    let candidate = super::try_parse_exact_runtime_resource_query("res://fonts/sts2-title.woff2")
        .expect("exact runtime font");

    assert_eq!(candidate.asset_kind, AssetKind::Font);
    assert_eq!(candidate.source_path, "fonts/sts2-title.woff2");
    assert_eq!(candidate.load_path, "res://fonts/sts2-title.woff2");
    assert_eq!(candidate.resource_type.as_deref(), Some("FontFile"));
}

#[test]
fn imported_fontdata_resource_query_is_a_direct_font_asset() {
    let candidate = super::try_parse_exact_runtime_resource_query(
        "res://.godot/imported/kreon_regular.ttf-a20220d5ba8adf850addcf353acfb496.fontdata",
    )
    .expect("fontdata resource");
    assert_eq!(candidate.asset_kind, AssetKind::Font);
    assert_eq!(candidate.source_root, "resources");
    assert_eq!(
        candidate.load_path,
        "res://.godot/imported/kreon_regular.ttf-a20220d5ba8adf850addcf353acfb496.fontdata"
    );
}

#[test]
fn offline_font_export_preserves_original_bytes_and_rejects_raster_format() {
    let unique = format!(
        "spirectl-font-export-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos()
    );
    let root = std::env::temp_dir().join(unique);
    let source_dir = root.join("resources").join("fonts");
    let output_dir = root.join("out");
    fs::create_dir_all(&source_dir).expect("create source dir");
    let font_path = source_dir.join("title.ttf");
    fs::write(&font_path, [0_u8, 1, 2, 3]).expect("write font");

    let candidate = AssetCandidate {
        source_root: "resources".to_string(),
        logical_path: "res://fonts/title.ttf".to_string(),
        source_path: "fonts/title.ttf".to_string(),
        relative_path: PathBuf::from("fonts/title.ttf"),
        asset_kind: AssetKind::Font,
        storage_kind: "filesystem-file".to_string(),
        load_path: font_path.display().to_string(),
        file_path: Some(font_path.clone()),
        container_path: None,
        offline_readable: true,
        resource_type: Some("FontFile".to_string()),
    };

    let exported = export_or_record_offline(
        &candidate,
        &output_dir,
        AssetFormatArg::Auto,
        &AppConfig::default(),
    )
    .expect("export font");
    assert_eq!(exported.artifact_kind, Some("font"));
    assert_eq!(exported.content_type.as_deref(), Some("font/ttf"));
    assert_eq!(
        fs::read(output_dir.join("resources").join("fonts").join("title.ttf"))
            .expect("read exported font"),
        vec![0_u8, 1, 2, 3]
    );

    let error = export_or_record_offline(
        &candidate,
        &output_dir,
        AssetFormatArg::Png,
        &AppConfig::default(),
    )
    .expect_err("raster font export should fail");
    assert_eq!(error.exit_code, 2);

    fs::remove_dir_all(root).ok();
}

#[test]
fn combat_background_alias_rejects_unsupported_variant_and_bad_id() {
    assert!(
        super::try_parse_composed_combat_background_query(
            "composed://combat-background/overgrowth/timeline"
        )
        .is_none()
    );
    assert!(
        super::try_parse_composed_combat_background_query(
            "composed://combat-background/../overgrowth/image"
        )
        .is_none()
    );
    assert!(
        super::try_parse_composed_combat_background_query("composed://combat-background//image")
            .is_none()
    );
}

#[test]
fn export_stage_ms_excludes_live_setup_delta() {
    let timing = AssetRequestTiming {
        resolve_ms: 0,
        live_setup_ms: 250,
        export_ms: 0,
    };

    assert_eq!(export_stage_ms(300, 125, &timing), 175);
    assert_eq!(export_stage_ms(100, 250, &timing), 100);
}
