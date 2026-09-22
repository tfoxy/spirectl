fn should_launch_live_bridge(
    error: &bridge::proto::BridgeError,
    transport_kind: TransportKind,
    launch_attempted: bool,
) -> bool {
    if launch_attempted || transport_kind != TransportKind::Ipc {
        return false;
    }

    matches!(
        bridge::proto::BridgeErrorCode::try_from(error.code).ok(),
        Some(bridge::proto::BridgeErrorCode::TransportUnavailable)
            | Some(bridge::proto::BridgeErrorCode::TransportConnectionFailed)
            | Some(bridge::proto::BridgeErrorCode::IpcSocketMissing)
            | Some(bridge::proto::BridgeErrorCode::IpcConnectionFailed)
            | Some(bridge::proto::BridgeErrorCode::BridgeBootstrapFailed)
            | Some(bridge::proto::BridgeErrorCode::BridgeNotAttached)
    )
}

fn load_offline_image(
    candidate: &AssetCandidate,
    config: &AppConfig,
    output_dir: &Path,
) -> Result<image::DynamicImage, String> {
    if let Some(file_path) = &candidate.file_path {
        return open_image(file_path).map_err(|source| source.to_string());
    }

    let bytes = read_packed_asset_bytes(config, candidate).map_err(|error| {
        error.payload["error"]["message"]
            .as_str()
            .unwrap_or("failed to read packed asset bytes")
            .to_string()
    })?;
    let guessed = candidate_source_extension(candidate)
        .as_deref()
        .and_then(parse_image_format)
        .unwrap_or(ImageFormat::Png);
    image::load_from_memory_with_format(&bytes, guessed)
        .or_else(|_| image::load_from_memory(&bytes))
        .map_err(|source| {
            format!(
                "failed to decode packed raster bytes for '{}' under '{}': {source}",
                candidate.source_path_text(),
                output_dir.display()
            )
        })
}

/// PNG/WebP can't encode the 32-bit float pixels an EXR/HDR source decodes to, so
/// fold those down to RGBA8 (clamping to [0,1] — the SDF alpha and luminance both sit
/// in range). Keeps the ALPHA channel, which Godot's VRAM import drops. A no-op for
/// already-integer images (the common PNG-in/PNG-out case).
fn ensure_raster_encodable(image: image::DynamicImage) -> image::DynamicImage {
    if matches!(
        image,
        image::DynamicImage::ImageRgb32F(_) | image::DynamicImage::ImageRgba32F(_)
    ) {
        image::DynamicImage::ImageRgba8(image.to_rgba8())
    } else {
        image
    }
}

fn write_live_raster_output(
    destination_path: &Path,
    requested_format: AssetFormatArg,
    response_format: &str,
    contents: &[u8],
) -> Result<(), AppError> {
    if response_format.eq_ignore_ascii_case(&requested_format.to_string()) {
        return fs::write(destination_path, contents).map_err(|source| {
            asset_output_error(
                destination_path,
                format!("failed to write live-rendered asset: {source}"),
            )
        });
    }

    let source_format = parse_image_format(response_format).ok_or_else(|| {
        asset_output_error(
            destination_path,
            format!(
                "live bridge returned unsupported raster format '{}'",
                response_format
            ),
        )
    })?;
    let image = image::load_from_memory_with_format(contents, source_format).map_err(|source| {
        asset_output_error(
            destination_path,
            format!(
                "failed to decode live-rendered {} bytes: {source}",
                response_format
            ),
        )
    })?;
    let mut output = File::create(destination_path).map_err(|source| {
        asset_output_error(
            destination_path,
            format!("failed to create export file: {source}"),
        )
    })?;
    image
        .write_to(
            &mut output,
            requested_format
                .image_format()
                .expect("explicit raster format has image encoder"),
        )
        .map_err(|source| {
            asset_output_error(
                destination_path,
                format!(
                    "failed to transcode live-rendered {} bytes as {}: {source}",
                    response_format, requested_format
                ),
            )
        })
}

struct TimelineWriteResult {
    manifest_path: PathBuf,
    frames_dir: PathBuf,
    frame_count: u32,
    duration_ms: u32,
}

fn write_live_timeline_output(
    candidate: &AssetCandidate,
    output_dir: &Path,
    requested_format: AssetFormatArg,
    response: &bridge::proto::AssetExtractResponse,
) -> Result<TimelineWriteResult, AppError> {
    let manifest_path = asset_timeline_manifest_path(candidate, output_dir);
    let frames_dir = asset_timeline_frames_dir(candidate, output_dir);
    if let Some(parent) = manifest_path.parent() {
        fs::create_dir_all(parent).map_err(|source| {
            asset_output_error(
                &manifest_path,
                format!("failed to create timeline export directory: {source}"),
            )
        })?;
    }
    fs::create_dir_all(&frames_dir).map_err(|source| {
        asset_output_error(
            &frames_dir,
            format!("failed to create timeline frames directory: {source}"),
        )
    })?;

    let mut manifest_frames = Vec::new();
    for frame in &response.frames {
        let frame_path = frames_dir.join(format!(
            "frame-{index:04}.{}",
            requested_format
                .extension()
                .expect("explicit format has extension"),
            index = frame.index
        ));
        write_live_raster_output(
            &frame_path,
            requested_format,
            &frame.format,
            &frame.contents,
        )?;
        manifest_frames.push(AssetTimelineFrameManifest {
            index: frame.index,
            path: frame_path.display().to_string(),
            format: requested_format.to_string(),
            width: frame.width,
            height: frame.height,
            duration_ms: frame.duration_ms,
            offset_x: frame.offset_x,
            offset_y: frame.offset_y,
            canvas_width: frame.canvas_width,
            canvas_height: frame.canvas_height,
        });
    }

    let manifest = AssetTimelineManifest {
        source_id: candidate.source_id(),
        source_path: candidate.source_path_text(),
        source_root: candidate.source_root.to_string(),
        asset_kind: candidate.asset_kind.as_str().to_string(),
        artifact_kind: "timeline",
        actual_format: requested_format.to_string(),
        render_mode: response.render_mode.clone(),
        width: response.width,
        height: response.height,
        frame_count: response.frame_count.max(manifest_frames.len() as u32),
        duration_ms: response.duration_ms,
        frames_dir: frames_dir.display().to_string(),
        frames: manifest_frames,
        notes: response.notes.clone(),
    };
    let manifest_bytes = serde_json::to_vec_pretty(&manifest).map_err(|source| {
        asset_output_error(
            &manifest_path,
            format!("failed to serialize timeline manifest: {source}"),
        )
    })?;
    fs::write(&manifest_path, manifest_bytes).map_err(|source| {
        asset_output_error(
            &manifest_path,
            format!("failed to write timeline manifest: {source}"),
        )
    })?;

    Ok(TimelineWriteResult {
        manifest_path,
        frames_dir,
        frame_count: manifest.frame_count,
        duration_ms: manifest.duration_ms,
    })
}

fn parse_image_format(raw: &str) -> Option<ImageFormat> {
    match raw.to_ascii_lowercase().as_str() {
        "png" => Some(ImageFormat::Png),
        "webp" => Some(ImageFormat::WebP),
        // HDR float sources we decode locally (alpha preserved — Godot's VRAM import
        // drops it). `card_frame_sdf.exr` stores its signed distance field in alpha,
        // which the WebGL card_ripple shader samples as `COLOR.a`.
        "exr" => Some(ImageFormat::OpenExr),
        "hdr" => Some(ImageFormat::Hdr),
        _ => None,
    }
}

fn asset_destination_path(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
) -> PathBuf {
    output_dir
        .join(&candidate.source_root)
        .join(&candidate.relative_path)
        .with_extension(format.extension().expect("explicit format has extension"))
}

fn asset_original_destination_path(candidate: &AssetCandidate, output_dir: &Path) -> PathBuf {
    output_dir
        .join(&candidate.source_root)
        .join(&candidate.relative_path)
}

fn candidate_source_extension(candidate: &AssetCandidate) -> Option<String> {
    candidate
        .relative_path
        .extension()
        .and_then(OsStr::to_str)
        .map(|extension| extension.to_ascii_lowercase())
}

fn asset_source_destination_path(candidate: &AssetCandidate, output_dir: &Path) -> PathBuf {
    output_dir
        .join(&candidate.source_root)
        .join(&candidate.relative_path)
}

fn asset_timeline_manifest_path(candidate: &AssetCandidate, output_dir: &Path) -> PathBuf {
    output_dir
        .join(&candidate.source_root)
        .join(&candidate.relative_path)
        .with_extension("json")
}

fn asset_timeline_frames_dir(candidate: &AssetCandidate, output_dir: &Path) -> PathBuf {
    let manifest_path = asset_timeline_manifest_path(candidate, output_dir);
    let stem = manifest_path
        .file_stem()
        .and_then(OsStr::to_str)
        .unwrap_or("timeline");
    manifest_path.with_file_name(format!("{stem}-frames"))
}

fn remove_stale_live_outputs(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
) -> Result<(), AppError> {
    let raster_format = format.live_format();
    let raster_path = asset_destination_path(candidate, output_dir, raster_format);
    if raster_path.exists() {
        fs::remove_file(&raster_path).map_err(|source| {
            asset_output_error(
                &raster_path,
                format!("failed to remove stale live-rendered asset: {source}"),
            )
        })?;
    }

    let timeline_manifest_path = asset_timeline_manifest_path(candidate, output_dir);
    if timeline_manifest_path.exists() {
        fs::remove_file(&timeline_manifest_path).map_err(|source| {
            asset_output_error(
                &timeline_manifest_path,
                format!("failed to remove stale live-rendered timeline manifest: {source}"),
            )
        })?;
    }

    let frames_dir = asset_timeline_frames_dir(candidate, output_dir);
    if frames_dir.exists() {
        fs::remove_dir_all(&frames_dir).map_err(|source| {
            asset_output_error(
                &frames_dir,
                format!("failed to remove stale live-rendered timeline frames: {source}"),
            )
        })?;
    }

    Ok(())
}

fn proto_asset_artifact_kind(response: &bridge::proto::AssetExtractResponse) -> &'static str {
    match bridge::proto::AssetArtifactKind::try_from(response.artifact_kind).ok() {
        Some(bridge::proto::AssetArtifactKind::Timeline) => "timeline",
        Some(bridge::proto::AssetArtifactKind::Metadata) => "metadata",
        Some(bridge::proto::AssetArtifactKind::Font) => "font",
        _ => "raster",
    }
}

fn font_content_type(extension: &str) -> Option<&'static str> {
    match extension
        .trim_start_matches('.')
        .to_ascii_lowercase()
        .as_str()
    {
        "ttf" => Some("font/ttf"),
        "otf" => Some("font/otf"),
        "woff" => Some("font/woff"),
        "woff2" => Some("font/woff2"),
        _ => None,
    }
}

#[allow(
    clippy::too_many_arguments,
    reason = "This helper assembles a flat export record from already-computed optional fields."
)]
fn exported_record(
    candidate: &AssetCandidate,
    execution_mode: &'static str,
    artifact_kind: &'static str,
    content_type: Option<String>,
    actual_format: Option<String>,
    render_mode: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
    output_path: Option<PathBuf>,
    frames_dir: Option<PathBuf>,
    frame_count: Option<u32>,
    duration_ms: Option<u32>,
    provenance: Option<AssetExtractProvenanceRecord>,
    placement_metadata: Option<AssetExtractPlacementMetadataRecord>,
    notices: Vec<AssetExtractNoticeRecord>,
    notes: Vec<String>,
) -> AssetExportRecord {
    let artifact_checks = output_path
        .as_ref()
        .and_then(|path| encounter_artifact_checks_for_export(candidate, path))
        .map(|result| match result {
            Ok(checks) => match visual_validation::artifact_checks_json(&checks) {
                serde_json::Value::Array(values) => values,
                _ => Vec::new(),
            },
            Err(error) => vec![json!({
                "id": "artifact-png-decode",
                "status": "failed",
                "message": error.payload["error"]["message"]
                    .as_str()
                    .unwrap_or("failed to analyze PNG artifact"),
            })],
        })
        .unwrap_or_default();

    AssetExportRecord {
        source_id: candidate.source_id(),
        source_path: candidate.source_path_text(),
        source_root: candidate.source_root.to_string(),
        asset_kind: candidate.asset_kind.as_str().to_string(),
        storage_kind: candidate.storage_kind.clone(),
        container_path: candidate
            .container_path
            .as_ref()
            .map(|path| path.display().to_string()),
        resource_type: candidate.resource_type.clone(),
        status: "exported",
        execution_mode,
        artifact_kind: Some(artifact_kind),
        content_type,
        actual_format,
        render_mode,
        width,
        height,
        output_path: output_path.map(|path| path.display().to_string()),
        frames_dir: frames_dir.map(|path| path.display().to_string()),
        frame_count,
        duration_ms,
        extraction_ms: 0,
        provenance,
        placement_metadata,
        notices,
        notes,
        artifact_checks,
        render_diagnostics: Vec::new(),
    }
}

fn unsupported_record(candidate: &AssetCandidate, notes: Vec<String>) -> AssetExportRecord {
    unsupported_record_with_execution_mode(candidate, "offline", notes)
}

fn unsupported_record_with_execution_mode(
    candidate: &AssetCandidate,
    execution_mode: &'static str,
    notes: Vec<String>,
) -> AssetExportRecord {
    AssetExportRecord {
        source_id: candidate.source_id(),
        source_path: candidate.source_path_text(),
        source_root: candidate.source_root.to_string(),
        asset_kind: candidate.asset_kind.as_str().to_string(),
        storage_kind: candidate.storage_kind.clone(),
        container_path: candidate
            .container_path
            .as_ref()
            .map(|path| path.display().to_string()),
        resource_type: candidate.resource_type.clone(),
        status: "unsupported_live_required",
        execution_mode,
        artifact_kind: None,
        content_type: None,
        actual_format: None,
        render_mode: None,
        width: None,
        height: None,
        output_path: None,
        frames_dir: None,
        frame_count: None,
        duration_ms: None,
        extraction_ms: 0,
        provenance: None,
        placement_metadata: None,
        notices: Vec::new(),
        notes,
        artifact_checks: Vec::new(),
        render_diagnostics: Vec::new(),
    }
}

fn live_render_failed_record(
    candidate: &AssetCandidate,
    notes: Vec<String>,
    render_diagnostics: Vec<serde_json::Value>,
) -> AssetExportRecord {
    AssetExportRecord {
        source_id: candidate.source_id(),
        source_path: candidate.source_path_text(),
        source_root: candidate.source_root.to_string(),
        asset_kind: candidate.asset_kind.as_str().to_string(),
        storage_kind: candidate.storage_kind.clone(),
        container_path: candidate
            .container_path
            .as_ref()
            .map(|path| path.display().to_string()),
        resource_type: candidate.resource_type.clone(),
        status: "live_render_failed",
        execution_mode: "live",
        artifact_kind: None,
        content_type: None,
        actual_format: None,
        render_mode: None,
        width: None,
        height: None,
        output_path: None,
        frames_dir: None,
        frame_count: None,
        duration_ms: None,
        extraction_ms: 0,
        provenance: None,
        placement_metadata: None,
        notices: Vec::new(),
        notes,
        artifact_checks: Vec::new(),
        render_diagnostics,
    }
}

fn encounter_artifact_checks_for_export(
    candidate: &AssetCandidate,
    output_path: &Path,
) -> Option<Result<Vec<visual_validation::ArtifactCheck>, AppError>> {
    if candidate.asset_kind != AssetKind::EncounterVisual {
        return None;
    }
    if output_path.extension().and_then(OsStr::to_str) != Some("png") {
        return None;
    }

    let source_path = candidate.source_path_text();
    let query = source_path
        .strip_prefix("composed://")
        .unwrap_or(source_path.as_str());
    let parsed = parse_encounter_asset_query(query)?;
    let (expected_bounds, forbid_full_viewport, max_transparent_ratio) = match parsed {
        EncounterAssetQuery::Background { .. } => (None, false, Some(0.99)),
        EncounterAssetQuery::VisualStateOverlay {
            encounter_id,
            state_id,
            ..
        } => (
            expected_encounter_overlay_bounds(&encounter_id, &state_id),
            true,
            Some(0.99),
        ),
        EncounterAssetQuery::VisualPartState {
            encounter_id,
            part_id,
            ..
        } => (
            expected_encounter_part_bounds(&encounter_id, &part_id),
            true,
            Some(0.999),
        ),
        EncounterAssetQuery::ScenePackage { .. } => return None,
    };

    Some(visual_validation::encounter_artifact_checks(
        output_path,
        expected_bounds,
        forbid_full_viewport,
        max_transparent_ratio,
    ))
}

fn expected_encounter_overlay_bounds(
    encounter_id: &str,
    state_id: &str,
) -> Option<visual_validation::AlphaBounds> {
    match (encounter_id, state_id) {
        ("kaiser_crab_boss", "rocket-charge-up") => Some(visual_validation::AlphaBounds {
            x: 0,
            y: 120,
            width: 1920,
            height: 760,
        }),
        _ => None,
    }
}

fn expected_encounter_part_bounds(
    encounter_id: &str,
    part_id: &str,
) -> Option<visual_validation::AlphaBounds> {
    match (encounter_id, part_id) {
        ("kaiser_crab_boss", "rocket") => Some(visual_validation::AlphaBounds {
            x: 1135,
            y: 150,
            width: 535,
            height: 690,
        }),
        _ => None,
    }
}

fn elapsed_ms(started_at: Instant) -> u64 {
    u64::try_from(started_at.elapsed().as_millis()).unwrap_or(u64::MAX)
}

fn asset_provenance_record_from_proto(
    provenance: bridge::proto::AssetExtractProvenance,
) -> AssetExtractProvenanceRecord {
    AssetExtractProvenanceRecord {
        source_root: provenance.source_root,
        source_path: provenance.source_path,
        load_path: provenance.load_path,
        render_mode: provenance.render_mode,
        source_kind: provenance.source_kind,
    }
}

fn asset_placement_metadata_record_from_proto(
    placement: bridge::proto::AssetExtractPlacementMetadata,
) -> AssetExtractPlacementMetadataRecord {
    AssetExtractPlacementMetadataRecord {
        source_scene_path: placement.source_scene_path,
        source_node_path: placement.source_node_path,
        local_bounds: placement
            .local_bounds
            .map(asset_composition_rect_record_from_proto)
            .unwrap_or_default(),
        global_bounds: placement
            .global_bounds
            .map(asset_composition_rect_record_from_proto)
            .unwrap_or_default(),
        output_width: placement.output_width,
        output_height: placement.output_height,
        render_mode: placement.render_mode,
    }
}

fn asset_composition_rect_record_from_proto(
    rect: bridge::proto::AssetCompositionRect,
) -> AssetCompositionRectRecord {
    AssetCompositionRectRecord {
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
    }
}

fn asset_notice_record_from_proto(
    notice: bridge::proto::AssetExtractNotice,
) -> AssetExtractNoticeRecord {
    AssetExtractNoticeRecord {
        code: notice.code,
        severity: notice.severity,
        path: notice.path,
        message: notice.message,
    }
}

fn export_stage_ms(
    total_candidate_ms: u64,
    live_setup_before_ms: u64,
    timing: &AssetRequestTiming,
) -> u64 {
    total_candidate_ms.saturating_sub(timing.live_setup_ms.saturating_sub(live_setup_before_ms))
}

fn is_live_render_failure(error: &AppError) -> bool {
    error.payload["error"]["code"] == "runtime_failure"
}

fn live_render_failure_notes(error: &AppError) -> Vec<String> {
    let message = error
        .payload
        .get("error")
        .and_then(|value| value.get("message"))
        .and_then(|value| value.as_str())
        .unwrap_or("live asset extraction failed");

    let detail_notes = error
        .payload
        .get("error")
        .and_then(|value| value.get("details"))
        .and_then(|value| value.as_array())
        .into_iter()
        .flatten()
        .filter_map(|detail| detail.get("note").and_then(|value| value.as_str()))
        .map(str::to_string);

    std::iter::once(message.to_string())
        .chain(detail_notes)
        .collect()
}

fn live_render_failure_diagnostics(error: &AppError) -> Vec<serde_json::Value> {
    error
        .payload
        .get("error")
        .and_then(|value| value.get("details"))
        .and_then(|value| value.as_array())
        .into_iter()
        .flatten()
        .filter_map(|detail| detail.get("diagnostic").cloned())
        .collect()
}

fn live_unavailable_notes(source_path: &str, error: &AppError) -> Vec<String> {
    let message = error
        .payload
        .get("error")
        .and_then(|value| value.get("message"))
        .and_then(|value| value.as_str())
        .unwrap_or("live asset extraction failed");
    vec![
        format!("Live fallback remained unavailable for '{}'.", source_path),
        message.to_string(),
    ]
}

/// Key schemes whose QUERY SELECTORS are case-sensitive, so the whole key must reach the bridge verbatim.
///
/// `spine://` names Godot nodes and Spine animations; `scene-subtree://` names a Godot node path and (for an
/// effect still) shader uniform names. Folding either to lower case turns a valid key into one the live
/// extractor cannot resolve — it reports "the scene has no node at 'cardcontainer/highlight'" and a caller has
/// to work out that the CLI, not their key, lost the capitals. The extractor matches the STRUCTURAL segments
/// case-insensitively itself, so preserving case here costs nothing.
const CASE_PRESERVING_KEY_SCHEMES: [&str; 2] = ["spine://", "scene-subtree://"];

fn normalize_query(query: &str) -> String {
    let slashed = query.replace('\\', "/");
    let trimmed = slashed.trim();
    if CASE_PRESERVING_KEY_SCHEMES.iter().any(|scheme| {
        trimmed.len() >= scheme.len() && trimmed[..scheme.len()].eq_ignore_ascii_case(scheme)
    }) {
        return trimmed.to_string();
    }

    let mut normalized = normalize_path_fragment(query);
    for prefix in ["res://", "file://", "./"] {
        if let Some(stripped) = normalized.strip_prefix(prefix) {
            normalized = stripped.to_string();
        }
    }
    normalized.trim_start_matches('/').to_string()
}

fn normalize_path_fragment(fragment: &str) -> String {
    fragment.replace('\\', "/").trim().to_ascii_lowercase()
}

fn try_parse_model_character_visual_query(query: &str) -> Option<VirtualCharacterVisualQuery> {
    let (model_kind, character_id, variant) = parse_model_asset_path(query)?;
    if model_kind != "characters"
        || !matches!(
            variant.as_str(),
            "icon"
                | "iconoutline"
                | "characterselecticon"
                | "characterselectlockedicon"
                | "mapmarker"
                | "characterselectbg"
                | "characterselectbgspinestill"
                | "visuals"
        )
    {
        return None;
    }

    Some(VirtualCharacterVisualQuery {
        character_id,
        variant,
    })
}

fn canonical_model_character_key(character_id: &str, variant: &str) -> String {
    let normalized_variant = variant.to_ascii_lowercase();
    let canonical_variant = match normalized_variant.as_str() {
        "iconoutline" => "iconOutline",
        "characterselecticon" => "characterSelectIcon",
        "characterselectlockedicon" => "characterSelectLockedIcon",
        "mapmarker" => "mapMarker",
        "characterselectbg" => "characterSelectBg",
        "characterselectbgspinestill" => "characterSelectBgSpineStill",
        "energycounter" => "energyCounter",
        "merchantanim" => "merchantAnim",
        "restsiteanim" => "restSiteAnim",
        other => other,
    };
    format!("model://characters/{character_id}/{canonical_variant}")
}

// spine://<scene-path-no-res-prefix>?node=<sceneRelativePath>&anim=<name> -> a Spine animation clip
// (Timeline of frames) addressed by its scene + scene-relative node path. The scene path, node, and
// anim are case-PRESERVED (scene paths and Spine names are case-sensitive). The key is passed through
// unchanged as the bridge load_path; the live extractor parses the scene/node/anim selectors.
/// `scene-subtree://<res-scene>?node=<relPath>[&posing knobs]` — render only the addressed subtree of a scene.
///
/// The bridge has understood this key for as long as couch-coop's room-backdrop still has existed, but only
/// in-process: nothing forwarded it from the CLI, so `assets extract` answered `no-match` for a key the live
/// host would happily have rendered. This is the same pass-through `try_parse_spine_clip_query` above does for
/// `spine://`, and for the same reason — the query string IS the key, and the bridge owns its grammar.
///
/// The key therefore rides through VERBATIM as `load_path`; nothing here re-orders or re-spells it. Only the
/// artifact's on-disk layout is derived, and it folds in the posing knobs, because two different POSES of one
/// node are two different images and must not overwrite each other.
fn try_parse_scene_subtree_query(query: &str) -> Option<AssetCandidate> {
    const PREFIX: &str = "scene-subtree://";
    let trimmed = query.trim().replace('\\', "/");
    if trimmed.len() < PREFIX.len() || !trimmed[..PREFIX.len()].eq_ignore_ascii_case(PREFIX) {
        return None;
    }

    let rest = &trimmed[PREFIX.len()..];
    let (scene_part, query_part) = rest.split_once('?')?;
    if !scene_part.to_ascii_lowercase().starts_with("res://") || query_part.is_empty() {
        return None;
    }

    // `node` is required by the bridge's own parse; refusing here too keeps a malformed key a CLI-side
    // `no-match` rather than a round trip that fails at the far end.
    let mut node: Option<String> = None;
    let mut pose = Vec::new();
    for pair in query_part.split('&').filter(|p| !p.is_empty()) {
        match pair.split_once('=') {
            Some(("node", value)) if !value.is_empty() => node = Some(value.to_string()),
            _ => pose.push(pair),
        }
    }
    let node = node?;

    let scene_stem = scene_part["res://".len()..].to_string();
    let mut relative = PathBuf::from("scene-subtree")
        .join(&scene_stem)
        .join(node.replace(['/', '%'], "_"));
    relative = relative.join(pose_variant_segment(&pose));

    Some(AssetCandidate {
        source_root: "scene-subtree".to_string(),
        logical_path: trimmed.clone(),
        source_path: trimmed.clone(),
        relative_path: relative,
        asset_kind: AssetKind::Scene,
        storage_kind: "scene-subtree".to_string(),
        load_path: trimmed,
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type: Some("SceneSubtree".to_string()),
    })
}

/// A filesystem-safe, self-describing, collision-free directory name for one POSE.
///
/// Readable prefix so a bake's output can be recognised without decoding it, plus an unconditional hash of the
/// full pose so two poses that sanitize or truncate to the same prefix still land in different directories. FNV-1a
/// rather than `DefaultHasher`, which is explicitly not stable across Rust releases — an artifact path that moved
/// on a toolchain upgrade would silently re-bake instead of reusing.
fn pose_variant_segment(pose: &[&str]) -> String {
    if pose.is_empty() {
        return "default".to_string();
    }

    // NO DOTS. The export layout treats the last dot-segment of a name as its extension and REPLACES it, so a
    // slug carrying `0.075` lands on disk as `..._0.png` — and two poses that differ only past their first dot
    // (`modulate=1,1,1,0.98` vs `1,1,1,0.5`) would overwrite each other's artifact. Observed, not theorised.
    let joined = pose.join("&");
    let mut readable: String = joined
        .chars()
        .map(|ch| if ch.is_ascii_alphanumeric() || ch == '-' { ch } else { '_' })
        .collect();
    readable.truncate(64);

    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in joined.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }

    format!("{readable}-{:08x}", (hash >> 32) as u32)
}

fn try_parse_spine_clip_query(query: &str) -> Option<AssetCandidate> {
    let trimmed = query.trim().replace('\\', "/");
    if trimmed.len() < "spine://".len() || !trimmed[.."spine://".len()].eq_ignore_ascii_case("spine://")
    {
        return None;
    }

    let rest = &trimmed["spine://".len()..];
    let (scene_part, query_part) = rest.split_once('?').unwrap_or((rest, ""));
    let scene_part = scene_part.trim().trim_matches('/');
    if scene_part.is_empty() {
        return None;
    }

    let mut node: Option<String> = None;
    let mut anim: Option<String> = None;
    for pair in query_part.split('&').filter(|p| !p.is_empty()) {
        if let Some((name, value)) = pair.split_once('=') {
            match name.to_ascii_lowercase().as_str() {
                "node" if !value.is_empty() => node = Some(value.to_string()),
                "anim" => anim = Some(value.to_string()),
                _ => {}
            }
        }
    }

    let anim = anim.filter(|a| !a.is_empty())?;

    // Reassemble a canonical key so the bridge load_path + output layout are stable regardless of query
    // ordering. node is omitted when absent (defaults to the sole/first SpineSprite in the scene).
    let key = match &node {
        Some(n) => format!("spine://{scene_part}?node={n}&anim={anim}"),
        None => format!("spine://{scene_part}?anim={anim}"),
    };

    let mut relative = PathBuf::from("spine").join(scene_part);
    if let Some(n) = &node {
        relative = relative.join(n.replace('/', "_"));
    }
    relative = relative.join(&anim);

    Some(AssetCandidate {
        source_root: "spine".to_string(),
        logical_path: key.clone(),
        source_path: key.clone(),
        relative_path: relative,
        asset_kind: AssetKind::CharacterVisual,
        storage_kind: "spine".to_string(),
        load_path: key,
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type: Some("SpineClip".to_string()),
    })
}

fn try_parse_model_resource_asset_query(query: &str) -> Option<AssetCandidate> {
    let (model_kind, model_id, raw_variant) = parse_model_asset_path(query)?;

    // Per-layer combat background keys carry a dynamic <stem> segment, so they
    // can't be enumerated in the fixed match below.
    if matches!(model_kind.as_str(), "acts" | "encounters") {
        if let Some(stem) = raw_variant.strip_prefix("backgroundlayer/") {
            if stem.is_empty() || stem.contains('/') {
                return None;
            }
            let model_type = model_type_path_segment(&model_kind);
            let resource_type = if model_kind == "acts" {
                "ActModelBackgroundLayer"
            } else {
                "EncounterModelBackgroundLayer"
            };
            let source_path = format!("model://{model_type}/{model_id}/backgroundLayer/{stem}");
            return Some(AssetCandidate {
                source_root: "model".to_string(),
                logical_path: source_path.clone(),
                source_path: source_path.clone(),
                relative_path: PathBuf::from("model")
                    .join(model_type)
                    .join(&model_id)
                    .join("backgroundLayer")
                    .join(stem),
                asset_kind: AssetKind::Scene,
                storage_kind: "model".to_string(),
                load_path: source_path,
                file_path: None,
                container_path: None,
                offline_readable: false,
                resource_type: Some(resource_type.to_string()),
            });
        }
    }

    let (model_type, variant, resource_type) = match (model_kind.as_str(), raw_variant.as_str()) {
        ("cards", "image") => ("cards", "image", "CardModelImage"),
        ("cards", "overlay") => ("cards", "overlay", "CardModelOverlay"),
        ("relics", "icon") => ("relics", "icon", "RelicModelIcon"),
        ("relics", "iconoutline") => ("relics", "iconOutline", "RelicModelIconOutline"),
        ("relics", "bigicon") => ("relics", "bigIcon", "RelicModelBigIcon"),
        ("potions", "icon") => ("potions", "icon", "PotionModelIcon"),
        ("potions", "outline") => ("potions", "outline", "PotionModelOutline"),
        ("characters", "characterselectbg") => (
            "characters",
            "characterSelectBg",
            "CharacterSelectBackground",
        ),
        ("characters", "characterselectbgspinestill") => (
            "characters",
            "characterSelectBgSpineStill",
            "CharacterSelectBgSpineStill",
        ),
        ("characters", "energycounter") => {
            ("characters", "energyCounter", "CharacterModelEnergyCounter")
        }
        ("characters", "merchantanim") => {
            ("characters", "merchantAnim", "CharacterModelMerchantAnim")
        }
        ("characters", "restsiteanim") => {
            ("characters", "restSiteAnim", "CharacterModelRestSiteAnim")
        }
        ("monsters", "visuals") => ("monsters", "visuals", "MonsterModelVisuals"),
        ("events", "backgroundscene") => ("events", "backgroundScene", "EventModelBackgroundScene"),
        ("events", "backgroundspinestill") => (
            "events",
            "backgroundSpineStill",
            "EventModelBackgroundSpineStill",
        ),
        ("events", "initialportrait") => ("events", "initialPortrait", "EventModelInitialPortrait"),
        ("events", "vfx") => ("events", "vfx", "EventModelVfx"),
        ("events", "mapicon") => ("events", "mapIcon", "AncientEventModelMapIcon"),
        ("events", "mapiconoutline") => (
            "events",
            "mapIconOutline",
            "AncientEventModelMapIconOutline",
        ),
        ("events", "runhistoryicon") => (
            "events",
            "runHistoryIcon",
            "AncientEventModelRunHistoryIcon",
        ),
        ("events", "runhistoryiconoutline") => (
            "events",
            "runHistoryIconOutline",
            "AncientEventModelRunHistoryIconOutline",
        ),
        ("acts", "backgroundscene") => ("acts", "backgroundScene", "ActModelBackgroundScene"),
        ("acts", "restsitebackground") => (
            "acts",
            "restSiteBackground",
            "ActModelRestSiteBackground",
        ),
        ("acts", "maptopbg") => ("acts", "mapTopBg", "ActModelMapTopBg"),
        ("acts", "mapmidbg") => ("acts", "mapMidBg", "ActModelMapMidBg"),
        ("acts", "mapbotbg") => ("acts", "mapBotBg", "ActModelMapBotBg"),
        ("acts", "chestspine") => ("acts", "chestSpine", "ActModelChestSpine"),
        ("rooms", "backgroundspinestill") => (
            "rooms",
            "backgroundSpineStill",
            "RoomModelBackgroundSpineStill",
        ),
        ("encounters", "scene") => ("encounters", "scene", "EncounterModelScene"),
        ("encounters", "background") => ("encounters", "background", "EncounterModelBackground"),
        ("encounters", "backgroundspinestill") => (
            "encounters",
            "backgroundSpineStill",
            "EncounterModelBackgroundSpineStill",
        ),
        _ => return None,
    };
    let source_path = if model_type == "characters" {
        canonical_model_character_key(&model_id, variant)
    } else {
        format!(
            "model://{}/{model_id}/{variant}",
            model_type_path_segment(model_type)
        )
    };

    // Scene-backed variants render as scenes; map tiles/icons stay images.
    let asset_kind = match (model_type, variant) {
        ("acts", "backgroundScene")
        | ("acts", "restSiteBackground")
        | ("encounters", "scene")
        | ("encounters", "background") => AssetKind::Scene,
        _ => AssetKind::Image,
    };

    Some(AssetCandidate {
        source_root: "model".to_string(),
        logical_path: source_path.clone(),
        source_path: source_path.clone(),
        relative_path: PathBuf::from("model")
            .join(model_type)
            .join(model_id)
            .join(variant),
        asset_kind,
        storage_kind: "model".to_string(),
        load_path: source_path,
        file_path: None,
        container_path: None,
        offline_readable: false,
        resource_type: Some(resource_type.to_string()),
    })
}

fn parse_model_asset_path(query: &str) -> Option<(String, String, String)> {
    let normalized = normalize_path_fragment(query);
    let path = normalized.strip_prefix("model://")?;
    let parts = path.split('/').collect::<Vec<_>>();
    match parts.as_slice() {
        // At least type/id/variant. The variant may itself contain '/' (e.g.
        // backgroundLayer/<stem>); rejoin the trailing segments.
        [model_kind, model_id, variant_parts @ ..]
            if !model_kind.is_empty()
                && !model_id.is_empty()
                && !variant_parts.is_empty()
                && variant_parts.iter().all(|part| !part.is_empty()) =>
        {
            Some((
                (*model_kind).to_string(),
                (*model_id).to_string(),
                variant_parts.join("/"),
            ))
        }
        _ => None,
    }
}

fn model_type_path_segment(model_type: &str) -> &str {
    match model_type {
        "card" | "cards" => "cards",
        "relic" | "relics" => "relics",
        "potion" | "potions" => "potions",
        "character" | "characters" => "characters",
        "monster" | "monsters" => "monsters",
        "event" | "events" => "events",
        other => other,
    }
}
