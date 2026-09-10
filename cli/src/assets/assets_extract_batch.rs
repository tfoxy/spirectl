fn run_asset_extract_request(
    spec: AssetExtractRequestSpec,
    search_paths: &ResolvedSceneSearchPaths,
    launch_attempted: &mut bool,
    context: AppContext<'_>,
) -> Result<AssetExtractResponse, AppError> {
    let query = normalize_query(&spec.query);
    if query.is_empty() {
        return Err(asset_usage_error(
            "command 'assets extract' requires a non-empty <query> argument",
        ));
    }
    fs::create_dir_all(&spec.output_dir).map_err(|source| {
        asset_output_error(
            &spec.output_dir,
            format!("failed to create asset output directory: {source}"),
        )
    })?;

    let candidates = discover_assets(context.config, search_paths, &spec.output_dir, &query)?;
    let mut exports = Vec::new();
    for candidate in candidates {
        let started_at = Instant::now();
        let mut record = match spec.execution {
            AssetExecutionArg::Offline => {
                export_or_record_offline(&candidate, &spec.output_dir, spec.format, context.config)?
            }
            AssetExecutionArg::Auto => export_with_auto_fallback(
                &candidate,
                &spec.output_dir,
                spec.format,
                launch_attempted,
                context,
            )?,
            AssetExecutionArg::Live => export_live(
                &candidate,
                &spec.output_dir,
                spec.format,
                launch_attempted,
                context,
            )?,
        };
        record.extraction_ms = elapsed_ms(started_at);
        exports.push(record);
    }

    let export_count = exports
        .iter()
        .filter(|record| record.status == "exported")
        .count();
    let status = if exports.is_empty() { "no-match" } else { "ok" };
    Ok(AssetExtractResponse {
        command: spec.command,
        status,
        query: spec.query,
        format: spec.format.to_string(),
        execution_mode: spec.execution.as_str(),
        output_dir: spec.output_dir.display().to_string(),
        match_count: exports.len(),
        export_count,
        exports,
    })
}

fn run_asset_extract_batch_request(
    spec: AssetExtractRequestSpec,
    discovery_index: &AssetDiscoveryIndex,
    launch_attempted: &mut bool,
    request_timing: &mut AssetRequestTiming,
    context: AppContext<'_>,
) -> Result<AssetExtractResponse, AppError> {
    let query = normalize_query(&spec.query);
    if query.is_empty() {
        return Err(asset_usage_error(
            "command 'assets extract-batch' requires each request query to be non-empty",
        ));
    }

    fs::create_dir_all(&spec.output_dir).map_err(|source| {
        asset_output_error(
            &spec.output_dir,
            format!("failed to create asset output directory: {source}"),
        )
    })?;

    let resolve_started_at = Instant::now();
    let candidates = discover_batch_assets(discovery_index, &query);
    request_timing.resolve_ms = elapsed_ms(resolve_started_at);

    let mut exports = Vec::new();
    for candidate in candidates {
        let started_at = Instant::now();
        let live_setup_before = request_timing.live_setup_ms;
        let mut record = match spec.execution {
            AssetExecutionArg::Offline => {
                export_or_record_offline(&candidate, &spec.output_dir, spec.format, context.config)?
            }
            AssetExecutionArg::Auto => export_with_auto_fallback_timed(
                &candidate,
                &spec.output_dir,
                spec.format,
                launch_attempted,
                Some(&mut *request_timing),
                context,
            )?,
            AssetExecutionArg::Live => export_live_timed(
                &candidate,
                &spec.output_dir,
                spec.format,
                launch_attempted,
                Some(&mut *request_timing),
                context,
            )?,
        };
        record.extraction_ms =
            export_stage_ms(elapsed_ms(started_at), live_setup_before, request_timing);
        request_timing.export_ms += record.extraction_ms;
        exports.push(record);
    }

    let export_count = exports
        .iter()
        .filter(|record| record.status == "exported")
        .count();
    let status = if exports.is_empty() { "no-match" } else { "ok" };
    Ok(AssetExtractResponse {
        command: spec.command,
        status,
        query: spec.query,
        format: spec.format.to_string(),
        execution_mode: spec.execution.as_str(),
        output_dir: spec.output_dir.display().to_string(),
        match_count: exports.len(),
        export_count,
        exports,
    })
}

pub(crate) fn execute_asset_extract_batch(
    args: AssetExtractBatchArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let manifest = load_asset_batch_manifest(&args.manifest, args.dry_run)?;
    let output_dir = args
        .output
        .as_deref()
        .map(resolve_absolute_path)
        .unwrap_or_else(|| {
            resolve_absolute_path(Path::new(&context.config.artifacts.dir)).join("assets-batch")
        });
    if args.dry_run {
        let response = AssetBatchExtractResponse {
            command: "extract-batch",
            status: "ok",
            dry_run: true,
            manifest_path: resolve_absolute_path(&args.manifest).display().to_string(),
            output_dir: output_dir.display().to_string(),
            execution_mode: args.execution.as_str(),
            format: args.format.to_string(),
            fail_fast: args.fail_fast,
            request_count: manifest.request_count(),
            success_count: 0,
            failure_count: 0,
            skipped_count: 0,
            export_count: 0,
            timing: AssetBatchTiming::default(),
            results: Vec::new(),
        };

        return Ok(render_batch_response(response, context.json_output, 0));
    }

    let batch_started_at = Instant::now();
    let search_paths = resolve_asset_search_paths(context, &args.search_roots)?;
    fs::create_dir_all(&output_dir).map_err(|source| {
        asset_output_error(
            &output_dir,
            format!("failed to create asset batch output directory: {source}"),
        )
    })?;

    let index_started_at = Instant::now();
    let helper_index = run_asset_helper_index(context.config, &search_paths, &output_dir)?;
    let index_ms = elapsed_ms(index_started_at);
    let discovery_index = AssetDiscoveryIndex {
        candidates: candidates_from_helper_matches(helper_index.matches),
    };
    let mut timing = AssetBatchTiming {
        index_ms,
        ..AssetBatchTiming::default()
    };
    let request_output_dirs = unique_asset_batch_output_dirs(&output_dir, &manifest.assets);
    let mut results = Vec::new();
    let mut launch_attempted = false;
    let mut stopped = false;

    for request in &manifest.assets {
        let execution = request.execution.unwrap_or(args.execution);
        let format = request.format.unwrap_or(args.format);
        let request_output_dir = request_output_dirs
            .get(&request.id)
            .expect("validated request output dir");

        if stopped {
            results.push(skipped_batch_result(
                request,
                execution,
                format,
                request_output_dir,
                AssetRequestTiming::default(),
            ));
            continue;
        }

        let mut request_timing = AssetRequestTiming::default();
        let result = match run_asset_extract_batch_request(
            AssetExtractRequestSpec {
                command: "extract",
                query: request.query.clone(),
                execution,
                format,
                output_dir: request_output_dir.clone(),
            },
            &discovery_index,
            &mut launch_attempted,
            &mut request_timing,
            context,
        ) {
            Ok(response) => batch_result_from_extract(
                request,
                response,
                request.metadata.clone(),
                request_timing.clone(),
            ),
            Err(error) => batch_result_from_error(
                request,
                execution,
                format,
                request_output_dir,
                error,
                request_timing.clone(),
            ),
        };
        timing.resolve_ms += request_timing.resolve_ms;
        timing.live_setup_ms += request_timing.live_setup_ms;
        timing.export_ms += request_timing.export_ms;

        if args.fail_fast && result.status != "ok" {
            stopped = true;
        }
        results.push(result);
    }

    let success_count = results
        .iter()
        .filter(|result| result.status == "ok")
        .count();
    let skipped_count = results
        .iter()
        .filter(|result| result.status == "skipped")
        .count();
    let failure_count = results.len() - success_count - skipped_count;
    let export_count = results.iter().map(|result| result.export_count).sum();
    let status = if success_count == results.len() {
        "ok"
    } else if success_count > 0 {
        "partial"
    } else {
        "failed"
    };
    timing.total_ms = elapsed_ms(batch_started_at);
    let response = AssetBatchExtractResponse {
        command: "extract-batch",
        status,
        dry_run: false,
        manifest_path: resolve_absolute_path(&args.manifest).display().to_string(),
        output_dir: output_dir.display().to_string(),
        execution_mode: args.execution.as_str(),
        format: args.format.to_string(),
        fail_fast: args.fail_fast,
        request_count: results.len(),
        success_count,
        failure_count,
        skipped_count,
        export_count,
        timing,
        results,
    };

    Ok(render_batch_response(
        response,
        context.json_output,
        if status == "ok" { 0 } else { 1 },
    ))
}
