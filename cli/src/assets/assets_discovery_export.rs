fn resolve_asset_search_paths(
    context: AppContext<'_>,
    search_roots: &AssetSearchRootArgs,
) -> Result<ResolvedSceneSearchPaths, AppError> {
    let search_paths = resolve_scene_search_paths(
        context.config,
        PathOverrides {
            game_path: search_roots.game_path.as_deref(),
            assemblies_dir: None,
            resources_dir: search_roots.resources_dir.as_deref(),
            mods_dir: search_roots.mods_dir.as_deref(),
        },
        search_roots.include_mods,
    )
    .map_err(asset_search_root_error)?;
    let _ = maybe_cache_resolved_local_paths(
        context,
        search_paths.game_path.as_deref(),
        search_paths.used_discovered_game_path(),
        search_paths.assemblies_dir.as_deref(),
        search_paths.derived_assemblies_dir_from_game_path(),
    );
    Ok(search_paths)
}

fn discover_assets(
    config: &AppConfig,
    search_paths: &ResolvedSceneSearchPaths,
    output_dir: &Path,
    query: &str,
) -> Result<Vec<AssetCandidate>, AppError> {
    if let Some(candidate) = try_parse_spine_clip_query(query) {
        return Ok(vec![candidate]);
    }

    if let Some(candidate) = try_parse_scene_subtree_query(query) {
        return Ok(vec![candidate]);
    }

    if let Some(parsed) = try_parse_model_character_visual_query(query) {
        let source_path = canonical_model_character_key(&parsed.character_id, &parsed.variant);
        return Ok(vec![AssetCandidate {
            source_root: "model".to_string(),
            logical_path: source_path.clone(),
            source_path: source_path.clone(),
            relative_path: PathBuf::from("character")
                .join(&parsed.character_id)
                .join(&parsed.variant),
            asset_kind: AssetKind::CharacterVisual,
            storage_kind: "model".to_string(),
            load_path: source_path,
            file_path: None,
            container_path: None,
            offline_readable: false,
            resource_type: Some("CharacterModelVisual".to_string()),
        }]);
    }

    if let Some(candidate) = try_parse_model_resource_asset_query(query) {
        return Ok(vec![candidate]);
    }

    if let Some(candidate) = try_parse_composed_encounter_asset_query(query) {
        return Ok(vec![candidate]);
    }

    if let Some(candidate) = try_parse_composed_combat_background_query(query) {
        return Ok(vec![candidate]);
    }

    if let Some(candidate) = try_parse_localization_resource_query(query) {
        return Ok(vec![candidate]);
    }

    let response = run_asset_helper_search(config, search_paths, output_dir, query)?;
    let candidates = candidates_from_helper_matches(response.matches);
    if !candidates.is_empty() {
        return Ok(candidates);
    }

    if let Some(candidate) =
        try_parse_exact_runtime_resource_query_with_search_paths(query, search_paths)
    {
        return Ok(vec![candidate]);
    }

    Ok(Vec::new())
}

fn discover_batch_assets(index: &AssetDiscoveryIndex, query: &str) -> Vec<AssetCandidate> {
    if let Some(candidate) = try_parse_spine_clip_query(query) {
        return vec![candidate];
    }

    if let Some(parsed) = try_parse_model_character_visual_query(query) {
        let source_path = canonical_model_character_key(&parsed.character_id, &parsed.variant);
        return vec![AssetCandidate {
            source_root: "model".to_string(),
            logical_path: source_path.clone(),
            source_path: source_path.clone(),
            relative_path: PathBuf::from("character")
                .join(&parsed.character_id)
                .join(&parsed.variant),
            asset_kind: AssetKind::CharacterVisual,
            storage_kind: "model".to_string(),
            load_path: source_path,
            file_path: None,
            container_path: None,
            offline_readable: false,
            resource_type: Some("CharacterModelVisual".to_string()),
        }];
    }

    if let Some(candidate) = try_parse_model_resource_asset_query(query) {
        return vec![candidate];
    }

    if let Some(candidate) = try_parse_composed_encounter_asset_query(query) {
        return vec![candidate];
    }

    if let Some(candidate) = try_parse_composed_combat_background_query(query) {
        return vec![candidate];
    }

    if let Some(candidate) = try_parse_localization_resource_query(query) {
        return vec![candidate];
    }

    let candidates = discover_assets_from_index(index, query);
    if !candidates.is_empty() {
        return candidates;
    }

    if let Some(candidate) = try_parse_exact_runtime_resource_query(query) {
        return vec![candidate];
    }

    Vec::new()
}

fn discover_assets_from_index(index: &AssetDiscoveryIndex, query: &str) -> Vec<AssetCandidate> {
    let normalized_query = normalize_query(query);
    index
        .candidates
        .iter()
        .filter(|candidate| candidate_matches_query(candidate, &normalized_query))
        .cloned()
        .collect()
}

fn candidate_matches_query(candidate: &AssetCandidate, normalized_query: &str) -> bool {
    normalized_candidate_text(&candidate.logical_path).contains(normalized_query)
        || normalized_candidate_text(&candidate.source_path).contains(normalized_query)
}

fn normalized_candidate_text(value: &str) -> String {
    value.replace('\\', "/").trim().to_lowercase()
}

fn candidates_from_helper_matches(matches: Vec<HelperAssetMatch>) -> Vec<AssetCandidate> {
    let mut candidates = Vec::new();
    for helper_match in matches {
        let asset_kind = match helper_match.asset_kind.as_str() {
            "image" => AssetKind::Image,
            "scene" => AssetKind::Scene,
            "resource" => AssetKind::Resource,
            "font" => AssetKind::Font,
            _ => AssetKind::Other,
        };
        if !asset_kind.is_reportable() {
            continue;
        }

        let file_path = if helper_match.storage_kind == "filesystem-file" {
            Some(PathBuf::from(&helper_match.load_path))
        } else {
            None
        };

        candidates.push(AssetCandidate {
            source_root: helper_match.source_root,
            logical_path: helper_match.logical_path,
            source_path: helper_match.source_path.clone(),
            relative_path: PathBuf::from(helper_match.source_path),
            asset_kind,
            storage_kind: helper_match.storage_kind.clone(),
            load_path: helper_match.load_path,
            file_path,
            container_path: helper_match.container_path.map(PathBuf::from),
            offline_readable: helper_match.readable_offline,
            resource_type: helper_match.resource_type,
        });
    }
    candidates
}

fn export_with_auto_fallback(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    launch_attempted: &mut bool,
    context: AppContext<'_>,
) -> Result<AssetExportRecord, AppError> {
    export_with_auto_fallback_timed(
        candidate,
        output_dir,
        format,
        launch_attempted,
        None,
        context,
    )
}

fn export_with_auto_fallback_timed(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    launch_attempted: &mut bool,
    timing: Option<&mut AssetRequestTiming>,
    context: AppContext<'_>,
) -> Result<AssetExportRecord, AppError> {
    // `auto` falls back between execution modes only: authoritative local
    // resources/packed entries first, then live IPC when needed. It must not
    // silently consult recovered-project files; callers can pass those as an
    // explicit --resources-dir when they want that source.
    if let Some(record) = cached_live_raster_export_record(candidate, output_dir, format) {
        return Ok(record);
    }

    if is_virtual_character_visual(candidate) {
        return export_live_timed(
            candidate,
            output_dir,
            format,
            launch_attempted,
            timing,
            context,
        );
    }

    if candidate.offline_readable && !should_prefer_live_resource_export(candidate, context.config)
    {
        let record = export_or_record_offline(candidate, output_dir, format, context.config)?;
        if record.status == "exported" || context.config.transport.kind != TransportKind::Ipc {
            return Ok(record);
        }
    } else if context.config.transport.kind != TransportKind::Ipc {
        return Ok(unsupported_record(candidate, unsupported_notes(candidate)));
    }

    match export_live_timed(
        candidate,
        output_dir,
        format,
        launch_attempted,
        timing,
        context,
    ) {
        Ok(record) => Ok(record),
        Err(error) => Ok(unsupported_record(
            candidate,
            live_unavailable_notes(&candidate.source_path_text(), &error),
        )),
    }
}

// An EXR/HDR raster whose alpha (e.g. the card_ripple SDF) we must preserve by
// decoding locally rather than via Godot's alpha-dropping VRAM path.
fn is_hdr_raster_source(candidate: &AssetCandidate) -> bool {
    candidate.asset_kind == AssetKind::Image
        && candidate_source_extension(candidate)
            .map(|ext| matches!(ext.as_str(), "exr" | "hdr"))
            .unwrap_or(false)
}

fn export_or_record_offline(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    config: &AppConfig,
) -> Result<AssetExportRecord, AppError> {
    if is_virtual_character_visual(candidate) {
        return Err(virtual_asset_live_required_error(candidate));
    }

    if candidate.asset_kind == AssetKind::Font {
        return export_offline_font(candidate, output_dir, format, config);
    }

    if candidate.asset_kind != AssetKind::Image {
        return export_offline_source(candidate, output_dir, config);
    }

    // HDR float sources (EXR/HDR) ALWAYS prefer the local decode: Godot's VRAM import
    // drops their alpha (the card_ripple SDF), so the live/bridge encode can't be
    // faithful. We can decode the source directly (`image` exr/hdr) and keep alpha, so
    // don't bail to live even when the helper marked it not-offline-readable.
    let hdr_source = is_hdr_raster_source(candidate);
    if !candidate.offline_readable && !hdr_source {
        return Ok(unsupported_record(candidate, unsupported_notes(candidate)));
    }

    // `auto` keeps non-HDR raster bytes verbatim; HDR must be transcoded (a browser
    // can't paint raw EXR/HDR) so fall through to the decode + PNG/WebP re-encode.
    if format == AssetFormatArg::Auto && !hdr_source {
        return export_offline_raster_original(candidate, output_dir, config);
    }
    let format = if format == AssetFormatArg::Auto {
        AssetFormatArg::Png
    } else {
        format
    };

    let destination_path = asset_destination_path(candidate, output_dir, format);
    if let Some(parent) = destination_path.parent() {
        fs::create_dir_all(parent).map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to create export directory: {source}"),
            )
        })?;
    }

    let image = match load_offline_image(candidate, config, output_dir) {
        // Fold HDR float pixels to RGBA8 so the PNG/WebP encoder accepts them while
        // KEEPING alpha (the EXR distance field) — see `ensure_raster_encodable`.
        Ok(image) => ensure_raster_encodable(image),
        Err(source) => {
            return Ok(unsupported_record(
                candidate,
                vec![format!(
                    "Failed to decode offline raster asset '{}': {source}",
                    candidate.source_path_text()
                )],
            ));
        }
    };
    let width = image.width();
    let height = image.height();

    let mut output = File::create(&destination_path).map_err(|source| {
        asset_output_error(
            &destination_path,
            format!("failed to create export file: {source}"),
        )
    })?;
    image
        .write_to(
            &mut output,
            format
                .image_format()
                .expect("explicit raster format has image encoder"),
        )
        .map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to encode image as {}: {source}", format),
            )
        })?;

    Ok(exported_record(
        candidate,
        "offline",
        "raster",
        None,
        Some(format.to_string()),
        Some("offline-raster".to_string()),
        Some(width),
        Some(height),
        Some(destination_path),
        None,
        None,
        None,
        None,
        None,
        Vec::new(),
        Vec::new(),
    ))
}

fn export_offline_raster_original(
    candidate: &AssetCandidate,
    output_dir: &Path,
    config: &AppConfig,
) -> Result<AssetExportRecord, AppError> {
    let destination_path = asset_original_destination_path(candidate, output_dir);
    if let Some(parent) = destination_path.parent() {
        fs::create_dir_all(parent).map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to create export directory: {source}"),
            )
        })?;
    }

    let contents = read_source_bytes(candidate, config)?;
    fs::write(&destination_path, contents).map_err(|source| {
        asset_output_error(
            &destination_path,
            format!("failed to write original raster export: {source}"),
        )
    })?;

    Ok(exported_record(
        candidate,
        "offline",
        "raster",
        None,
        candidate_source_extension(candidate),
        Some("offline-raster-original".to_string()),
        None,
        None,
        Some(destination_path),
        None,
        None,
        None,
        None,
        None,
        Vec::new(),
        Vec::new(),
    ))
}

fn export_offline_source(
    candidate: &AssetCandidate,
    output_dir: &Path,
    config: &AppConfig,
) -> Result<AssetExportRecord, AppError> {
    if !candidate.offline_readable {
        return Ok(unsupported_record(candidate, unsupported_notes(candidate)));
    }

    let destination_path = asset_source_destination_path(candidate, output_dir);
    if let Some(parent) = destination_path.parent() {
        fs::create_dir_all(parent).map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to create export directory: {source}"),
            )
        })?;
    }

    let contents = read_source_bytes(candidate, config)?;
    fs::write(&destination_path, contents).map_err(|source| {
        asset_output_error(
            &destination_path,
            format!("failed to write source asset export: {source}"),
        )
    })?;

    Ok(exported_record(
        candidate,
        "offline",
        "source",
        None,
        None,
        None,
        None,
        None,
        Some(destination_path),
        None,
        None,
        None,
        None,
        None,
        Vec::new(),
        Vec::new(),
    ))
}

fn export_offline_font(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    config: &AppConfig,
) -> Result<AssetExportRecord, AppError> {
    if format != AssetFormatArg::Auto {
        return Err(asset_usage_error(
            "font assets can only be exported with --format auto; raster formats are not valid for font bytes.",
        ));
    }

    if !candidate.offline_readable {
        return Ok(unsupported_record(candidate, unsupported_notes(candidate)));
    }

    let destination_path = asset_original_destination_path(candidate, output_dir);
    if let Some(parent) = destination_path.parent() {
        fs::create_dir_all(parent).map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to create font export directory: {source}"),
            )
        })?;
    }

    let contents = read_source_bytes(candidate, config)?;
    fs::write(&destination_path, contents).map_err(|source| {
        asset_output_error(
            &destination_path,
            format!("failed to write font asset export: {source}"),
        )
    })?;

    let extension = candidate_source_extension(candidate);
    Ok(exported_record(
        candidate,
        "offline",
        "font",
        extension
            .as_deref()
            .and_then(font_content_type)
            .map(str::to_string),
        extension,
        Some("offline-font-original".to_string()),
        None,
        None,
        Some(destination_path),
        None,
        None,
        None,
        None,
        None,
        Vec::new(),
        Vec::new(),
    ))
}

fn export_live(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    launch_attempted: &mut bool,
    context: AppContext<'_>,
) -> Result<AssetExportRecord, AppError> {
    export_live_timed(
        candidate,
        output_dir,
        format,
        launch_attempted,
        None,
        context,
    )
}

fn should_prefer_live_resource_export(candidate: &AssetCandidate, config: &AppConfig) -> bool {
    candidate.asset_kind == AssetKind::Resource && config.transport.kind == TransportKind::Ipc
}

fn cached_live_raster_export_record(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
) -> Option<AssetExportRecord> {
    if matches!(candidate.asset_kind, AssetKind::Font) {
        return None;
    }
    if candidate.offline_readable && candidate.asset_kind != AssetKind::Resource {
        return None;
    }

    let output_format = format.live_format();
    let destination_path = asset_destination_path(candidate, output_dir, output_format);
    if !destination_path.is_file() {
        return None;
    }

    let content_type = image_content_type_for_path(&destination_path)?;
    let dimensions = open_image(&destination_path)
        .ok()
        .map(|image| (image.width(), image.height()));
    Some(exported_record(
        candidate,
        "cache",
        "raster",
        Some(content_type.to_string()),
        output_format.extension().map(str::to_string),
        Some("cached-live-raster-asset".to_string()),
        dimensions.map(|(width, _)| width),
        dimensions.map(|(_, height)| height),
        Some(destination_path),
        None,
        None,
        None,
        None,
        None,
        Vec::new(),
        vec!["Reused a previously exported live raster asset.".to_string()],
    ))
}

fn image_content_type_for_path(path: &Path) -> Option<&'static str> {
    match path
        .extension()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_ascii_lowercase()
        .as_str()
    {
        "png" => Some("image/png"),
        "webp" => Some("image/webp"),
        _ => None,
    }
}

fn export_live_timed(
    candidate: &AssetCandidate,
    output_dir: &Path,
    format: AssetFormatArg,
    launch_attempted: &mut bool,
    timing: Option<&mut AssetRequestTiming>,
    context: AppContext<'_>,
) -> Result<AssetExportRecord, AppError> {
    if candidate.asset_kind == AssetKind::Font && format != AssetFormatArg::Auto {
        return Err(asset_usage_error(
            "font assets can only be exported with --format auto; raster formats are not valid for font bytes.",
        ));
    }

    let live_format = if candidate.asset_kind == AssetKind::Font {
        AssetFormatArg::Auto
    } else {
        format.live_format()
    };
    let response =
        match extract_live_asset_timed(candidate, live_format, launch_attempted, timing, context) {
            Ok(response) => response,
            Err(error)
                if is_virtual_character_visual(candidate) && is_live_render_failure(&error) =>
            {
                return Err(error);
            }
            Err(error)
                if is_virtual_encounter_visual(candidate) && is_live_render_failure(&error) =>
            {
                remove_stale_live_outputs(candidate, output_dir, live_format)?;
                return Ok(live_render_failed_record(
                    candidate,
                    live_render_failure_notes(&error),
                    live_render_failure_diagnostics(&error),
                ));
            }
            Err(error) if is_live_render_failure(&error) => {
                remove_stale_live_outputs(candidate, output_dir, live_format)?;
                return Ok(unsupported_record_with_execution_mode(
                    candidate,
                    "live",
                    live_render_failure_notes(&error),
                ));
            }
            Err(error) => return Err(error),
        };
    let artifact_kind = proto_asset_artifact_kind(&response);
    let render_mode = response.render_mode.clone();
    let actual_format = response.format.clone();
    let width = response.width;
    let height = response.height;
    let notes = response.notes.clone();

    // A structured (non-raster) payload — a scene's GodotSceneState JSON or a
    // localization table — is persisted verbatim. The seam decides JSON-vs-raster
    // (e.g. `--format structure` on a scene); the CLI just writes what it returns, so
    // `assets extract` faithfully surfaces the seam instead of forcing a render.
    if response.content_type.eq_ignore_ascii_case("application/json") {
        let destination_path = asset_destination_path(candidate, output_dir, AssetFormatArg::Structure);
        if let Some(parent) = destination_path.parent() {
            fs::create_dir_all(parent).map_err(|source| {
                asset_output_error(
                    &destination_path,
                    format!("failed to create structured export directory: {source}"),
                )
            })?;
        }
        fs::write(&destination_path, &response.contents).map_err(|source| {
            asset_output_error(
                &destination_path,
                format!("failed to write structured live asset: {source}"),
            )
        })?;

        return Ok(exported_record(
            candidate,
            "live",
            "metadata",
            Some(response.content_type.clone()),
            Some(actual_format),
            Some(render_mode),
            None,
            None,
            Some(destination_path),
            None,
            None,
            None,
            response
                .provenance
                .clone()
                .map(asset_provenance_record_from_proto),
            response
                .placement_metadata
                .clone()
                .map(asset_placement_metadata_record_from_proto),
            response
                .notices
                .clone()
                .into_iter()
                .map(asset_notice_record_from_proto)
                .collect(),
            notes,
        ));
    }

    match artifact_kind {
        "timeline" => {
            let timeline =
                write_live_timeline_output(candidate, output_dir, live_format, &response)?;
            Ok(exported_record(
                candidate,
                "live",
                "timeline",
                Some(response.content_type.clone()),
                Some(actual_format),
                Some(render_mode),
                Some(width),
                Some(height),
                Some(timeline.manifest_path),
                Some(timeline.frames_dir),
                Some(timeline.frame_count),
                Some(timeline.duration_ms),
                response
                    .provenance
                    .clone()
                    .map(asset_provenance_record_from_proto),
                response
                    .placement_metadata
                    .clone()
                    .map(asset_placement_metadata_record_from_proto),
                response
                    .notices
                    .clone()
                    .into_iter()
                    .map(asset_notice_record_from_proto)
                    .collect(),
                notes,
            ))
        }
        "font" => {
            let destination_path = asset_original_destination_path(candidate, output_dir);
            if let Some(parent) = destination_path.parent() {
                fs::create_dir_all(parent).map_err(|source| {
                    asset_output_error(
                        &destination_path,
                        format!("failed to create font export directory: {source}"),
                    )
                })?;
            }
            fs::write(&destination_path, &response.contents).map_err(|source| {
                asset_output_error(
                    &destination_path,
                    format!("failed to write live font asset: {source}"),
                )
            })?;

            Ok(exported_record(
                candidate,
                "live",
                "font",
                Some(response.content_type.clone()),
                Some(actual_format),
                Some(render_mode),
                None,
                None,
                Some(destination_path),
                None,
                None,
                None,
                response
                    .provenance
                    .clone()
                    .map(asset_provenance_record_from_proto),
                response
                    .placement_metadata
                    .clone()
                    .map(asset_placement_metadata_record_from_proto),
                response
                    .notices
                    .clone()
                    .into_iter()
                    .map(asset_notice_record_from_proto)
                    .collect(),
                notes,
            ))
        }
        _ => {
            // Honor an explicit raster request (png/webp); otherwise (auto/structure)
            // use whatever raster the bridge actually produced so the output extension
            // is concrete (a non-raster `structure` request that still rendered — e.g.
            // a texture — lands here and writes the real image).
            let output_format = match live_format.image_format() {
                Some(_) => live_format,
                None => parse_asset_output_format(&response.format).unwrap_or(AssetFormatArg::Png),
            };
            let destination_path = asset_destination_path(candidate, output_dir, output_format);
            if let Some(parent) = destination_path.parent() {
                fs::create_dir_all(parent).map_err(|source| {
                    asset_output_error(
                        &destination_path,
                        format!("failed to create export directory: {source}"),
                    )
                })?;
            }
            write_live_raster_output(
                &destination_path,
                output_format,
                &response.format,
                &response.contents,
            )?;

            Ok(exported_record(
                candidate,
                "live",
                "raster",
                Some(response.content_type.clone()),
                Some(actual_format),
                Some(render_mode),
                Some(width),
                Some(height),
                Some(destination_path),
                None,
                None,
                None,
                response
                    .provenance
                    .clone()
                    .map(asset_provenance_record_from_proto),
                response
                    .placement_metadata
                    .clone()
                    .map(asset_placement_metadata_record_from_proto),
                response
                    .notices
                    .clone()
                    .into_iter()
                    .map(asset_notice_record_from_proto)
                    .collect(),
                notes,
            ))
        }
    }
}

fn extract_live_asset_timed(
    candidate: &AssetCandidate,
    format: AssetFormatArg,
    launch_attempted: &mut bool,
    timing: Option<&mut AssetRequestTiming>,
    context: AppContext<'_>,
) -> Result<bridge::proto::AssetExtractResponse, AppError> {
    let request = bridge::proto::AssetExtractRequest {
        request_id: candidate.source_id(),
        source_root: candidate.source_root.clone(),
        source_path: candidate.source_path_text(),
        load_path: candidate.load_path.clone(),
        output_format: format.to_string(),
    };

    let client = bridge::RuntimeBridgeClient::from_config(&context.config.transport);
    match client.extract_asset(request.clone()) {
        Ok(response) => Ok(response),
        Err(error)
            if should_launch_live_bridge(
                &error,
                context.config.transport.kind,
                *launch_attempted,
            ) =>
        {
            let setup_started_at = Instant::now();
            let launch_result = lifecycle::execute_game_launch_json(
                LifecycleWaitArgs {
                    timeout_ms: 30_000,
                    interval_ms: 250,
                    rpc_timeout_ms: 5_000,
                },
                context,
            );
            if let Some(timing) = timing {
                timing.live_setup_ms += elapsed_ms(setup_started_at);
            }
            launch_result?;
            *launch_attempted = true;
            bridge::RuntimeBridgeClient::from_config(&context.config.transport)
                .extract_asset(request)
                .map_err(AppError::bridge)
        }
        Err(error) => Err(AppError::bridge(error)),
    }
}

fn explain_live_asset(
    candidate: &AssetCandidate,
    launch_attempted: &mut bool,
    context: AppContext<'_>,
) -> Result<bridge::proto::AssetExplainResponse, AppError> {
    let request = bridge::proto::AssetExplainRequest {
        request_id: candidate.source_id(),
        source_root: candidate.source_root.clone(),
        source_path: candidate.source_path_text(),
        load_path: candidate.load_path.clone(),
    };

    let client = bridge::RuntimeBridgeClient::from_config(&context.config.transport);
    match client.explain_asset(request.clone()) {
        Ok(response) => Ok(response),
        Err(error)
            if should_launch_live_bridge(
                &error,
                context.config.transport.kind,
                *launch_attempted,
            ) =>
        {
            lifecycle::execute_game_launch_json(
                LifecycleWaitArgs {
                    timeout_ms: 30_000,
                    interval_ms: 250,
                    rpc_timeout_ms: 5_000,
                },
                context,
            )?;
            *launch_attempted = true;
            bridge::RuntimeBridgeClient::from_config(&context.config.transport)
                .explain_asset(request)
                .map_err(AppError::bridge)
        }
        Err(error) => Err(AppError::bridge(error)),
    }
}

fn asset_explain_payload_from_proto(
    response: bridge::proto::AssetExplainResponse,
) -> (String, serde_json::Value) {
    if let Some(package) = response.encounter_scene_package {
        let explanation_kind = if response.explanation_kind.is_empty() {
            "encounter-scene-package".to_string()
        } else {
            response.explanation_kind
        };
        let payload = json!({
            "requestId": response.request_id,
            "source": data_source_name(response.source),
            "provisional": response.provisional,
            "schemaVersion": package.schema_version,
            "encounterId": package.encounter_id,
            "viewport": package.viewport.map(|viewport| json!({
                "width": viewport.width,
                "height": viewport.height,
                "coordinateSpace": viewport.coordinate_space,
            })).unwrap_or_else(|| json!({})),
            "camera": package.camera.map(|camera| json!({
                "scale": camera.scale,
                "offset": camera.offset.map(|offset| json!({
                    "x": offset.x,
                    "y": offset.y,
                })).unwrap_or_else(|| json!({"x": 0.0, "y": 0.0})),
                "source": camera.source,
                "provenance": camera.provenance,
            })).unwrap_or_else(|| json!({})),
            "background": package.background.map(|background| json!({
                "sourceScene": background.source_scene,
                "sourceQuery": background.source_query,
                "renderQuery": background.render_query,
            })).unwrap_or_else(|| json!({})),
            "logicalActors": package.logical_actors.into_iter().map(|actor| json!({
                "actorId": actor.actor_id,
                "slotId": actor.slot_id,
                "targetRect": actor.target_rect.map(asset_explain_rect_json_from_proto),
                "statePartIds": actor.state_part_ids,
            })).collect::<Vec<_>>(),
            "visualParts": package.visual_parts.into_iter().map(|part| json!({
                "partId": part.part_id,
                "actorId": part.actor_id,
                "screenSide": part.screen_side,
                "anatomicalSide": part.anatomical_side,
                "layer": part.layer,
                "viewportRect": part.viewport_rect.map(asset_explain_rect_json_from_proto),
                "selectorDiagnostic": part.selector_diagnostic.map(asset_encounter_selector_diagnostic_json_from_proto),
            })).collect::<Vec<_>>(),
            "states": package.states.into_iter().map(|state| json!({
                "stateId": state.state_id,
                "affectedPartIds": state.affected_part_ids,
            })).collect::<Vec<_>>(),
            "transitions": package.transitions.into_iter().map(|transition| json!({
                "transitionId": transition.transition_id,
                "hook": transition.hook,
                "affectedPartIds": transition.affected_part_ids,
                "activeStateId": transition.active_state_id,
            })).collect::<Vec<_>>(),
            "renderTargets": package.render_targets.into_iter().map(|target| json!({
                "targetId": target.target_id,
                "kind": target.kind,
                "query": target.query,
                "decision": target.decision.map(asset_encounter_render_target_decision_json_from_proto),
            })).collect::<Vec<_>>(),
            "notices": package.notices.into_iter().map(|notice| json!({
                "code": notice.code,
                "severity": notice.severity,
                "path": notice.path,
                "message": notice.message,
                "provisional": notice.provisional,
            })).collect::<Vec<_>>(),
            "selectorDiagnostics": package.selector_diagnostics.into_iter().map(asset_encounter_selector_diagnostic_json_from_proto).collect::<Vec<_>>(),
        });
        return (explanation_kind, payload);
    }

    let explanation_kind = if response.explanation_kind.is_empty() {
        "combat-background-composition".to_string()
    } else {
        response.explanation_kind.clone()
    };
    let payload = AssetExplainPayload {
        request_id: response.request_id,
        source: data_source_name(response.source),
        provisional: response.provisional,
        root_scene: response
            .root_scene
            .map(|root| AssetExplainRootScene {
                background_id: root.background_id,
                path: root.path,
                load_source: root.load_source,
            })
            .unwrap_or_else(|| AssetExplainRootScene {
                background_id: String::new(),
                path: String::new(),
                load_source: String::new(),
            }),
        placeholders: response
            .placeholders
            .into_iter()
            .map(|placeholder| AssetExplainPlaceholder {
                name: placeholder.name,
                node_path: placeholder.node_path,
                order: placeholder.order,
                matched: placeholder.matched,
            })
            .collect(),
        layer_groups: response
            .layer_groups
            .into_iter()
            .map(|group| AssetExplainLayerGroup {
                name: group.name,
                placeholder: group.placeholder,
                candidate_count: group.candidate_count,
                candidates: group.candidates,
                selected_path: group.selected_path,
                selection_source: group.selection_source,
                order: group.order,
            })
            .collect(),
        selected_layers: response
            .selected_layers
            .into_iter()
            .map(|layer| AssetExplainSelectedLayer {
                placeholder: layer.placeholder,
                path: layer.path,
                order: layer.order,
                load_status: layer.load_status,
                texture_refs: layer.texture_refs,
                local_bounds: layer.local_bounds.map(asset_explain_rect_from_proto),
                visible_bounds: layer.visible_bounds.map(asset_explain_rect_from_proto),
            })
            .collect(),
        bounds: response
            .bounds
            .map(|bounds| AssetExplainBounds {
                viewport: bounds
                    .viewport
                    .map(asset_explain_rect_from_proto)
                    .unwrap_or_else(empty_asset_explain_rect),
                final_composed: bounds
                    .final_composed
                    .map(asset_explain_rect_from_proto)
                    .unwrap_or_else(empty_asset_explain_rect),
                final_visible: bounds
                    .final_visible
                    .map(asset_explain_rect_from_proto)
                    .unwrap_or_else(empty_asset_explain_rect),
                transparent_pixel_ratio: bounds.transparent_pixel_ratio,
            })
            .unwrap_or_else(|| AssetExplainBounds {
                viewport: empty_asset_explain_rect(),
                final_composed: empty_asset_explain_rect(),
                final_visible: empty_asset_explain_rect(),
                transparent_pixel_ratio: 0.0,
            }),
        render: response
            .render
            .map(|render| AssetExplainRenderPlan {
                render_mode: render.render_mode,
                warmup_frames: render.warmup_frames,
                trim_transparent_bounds: render.trim_transparent_bounds,
                transparent_crop_padding: render.transparent_crop_padding,
                notes: render.notes,
            })
            .unwrap_or_else(|| AssetExplainRenderPlan {
                render_mode: String::new(),
                warmup_frames: 0,
                trim_transparent_bounds: false,
                transparent_crop_padding: 0,
                notes: Vec::new(),
            }),
        active_scene: response
            .active_scene
            .map(|active_scene| AssetExplainActiveScene {
                status: active_scene.status,
                matched_root: active_scene.matched_root,
                observed_layer_paths: active_scene.observed_layer_paths,
                differences: active_scene.differences,
            })
            .unwrap_or_else(|| AssetExplainActiveScene {
                status: String::new(),
                matched_root: false,
                observed_layer_paths: Vec::new(),
                differences: Vec::new(),
            }),
        warnings: response
            .warnings
            .into_iter()
            .map(|warning| AssetExplainWarning {
                code: warning.code,
                severity: warning.severity,
                message: warning.message,
                details: warning.details.into_iter().collect(),
            })
            .collect(),
    };
    (
        explanation_kind,
        serde_json::to_value(payload).expect("asset explain payload serializes"),
    )
}

fn asset_explain_rect_from_proto(rect: bridge::proto::AssetCompositionRect) -> AssetExplainRect {
    AssetExplainRect {
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
    }
}

fn asset_explain_rect_json_from_proto(
    rect: bridge::proto::AssetCompositionRect,
) -> serde_json::Value {
    json!({
        "x": rect.x,
        "y": rect.y,
        "width": rect.width,
        "height": rect.height,
    })
}

fn optional_asset_explain_rect_json_from_proto(
    rect: Option<bridge::proto::AssetCompositionRect>,
) -> serde_json::Value {
    rect.map(asset_explain_rect_json_from_proto)
        .unwrap_or(serde_json::Value::Null)
}

fn asset_encounter_selector_diagnostic_json_from_proto(
    diagnostic: bridge::proto::AssetEncounterSelectorDiagnostic,
) -> serde_json::Value {
    json!({
        "partId": diagnostic.part_id,
        "selector": diagnostic.selector,
        "normalizedSelectors": diagnostic.normalized_selectors,
        "candidates": diagnostic.candidates.into_iter().map(|candidate| json!({
            "path": candidate.path,
            "selector": candidate.selector,
            "source": candidate.source,
            "status": candidate.status,
        })).collect::<Vec<_>>(),
        "resolvedNode": diagnostic.resolved_node.map(|node| json!({
            "path": node.path,
            "name": node.name,
            "type": node.r#type,
        })),
        "localBounds": optional_asset_explain_rect_json_from_proto(diagnostic.local_bounds),
        "visibleBounds": optional_asset_explain_rect_json_from_proto(diagnostic.visible_bounds),
        "status": diagnostic.status,
        "targetStateId": diagnostic.target_state_id,
        "renderTargetId": diagnostic.render_target_id,
    })
}

fn asset_encounter_render_target_decision_json_from_proto(
    decision: bridge::proto::AssetEncounterRenderTargetDecision,
) -> serde_json::Value {
    json!({
        "targetId": decision.target_id,
        "kind": decision.kind,
        "stateId": decision.state_id,
        "partId": decision.part_id,
        "decision": decision.decision,
        "reason": decision.reason,
        "affectedPartIds": decision.affected_part_ids,
    })
}

fn empty_asset_explain_rect() -> AssetExplainRect {
    AssetExplainRect {
        x: 0.0,
        y: 0.0,
        width: 0.0,
        height: 0.0,
    }
}

fn data_source_name(source: i32) -> String {
    match bridge::proto::DataSource::try_from(source)
        .unwrap_or(bridge::proto::DataSource::Unspecified)
    {
        bridge::proto::DataSource::Live => "live",
        bridge::proto::DataSource::Stub => "stub",
        bridge::proto::DataSource::Unspecified => "unspecified",
    }
    .to_string()
}
