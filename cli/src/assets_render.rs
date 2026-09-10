use super::*;

pub(super) fn render_extract_response(
    response: AssetExtractResponse,
    json_output: bool,
    exit_code: i32,
) -> RenderedCommand {
    let stdout = if json_output {
        serde_json::to_string(&response)
            .map(|text| format!("{text}\n"))
            .unwrap_or_else(|source| render_json_error(&source.to_string()))
    } else {
        render_human_response(&response)
    };

    RenderedCommand { stdout, exit_code }
}

pub(super) fn render_explain_response(
    response: AssetExplainResponse,
    json_output: bool,
    exit_code: i32,
) -> RenderedCommand {
    let stdout = if json_output {
        serde_json::to_string(&response)
            .map(|text| format!("{text}\n"))
            .unwrap_or_else(|source| render_json_error(&source.to_string()))
    } else {
        render_human_explain_response(&response)
    };

    RenderedCommand { stdout, exit_code }
}

pub(super) fn render_catalog_response(
    response: AssetCatalogResponse,
    json_output: bool,
    exit_code: i32,
) -> RenderedCommand {
    if json_output {
        return RenderedCommand {
            stdout: serde_json::to_string_pretty(&response)
                .unwrap_or_else(|source| render_json_error(&source.to_string())),
            exit_code,
        };
    }

    let mut lines = vec![format!("status: {}", response.status)];
    for entry in &response.entries {
        lines.push(format!(
            "{} ({})",
            entry.key.as_deref().unwrap_or(entry.key_pattern),
            entry.resolution_kind
        ));
    }
    RenderedCommand {
        stdout: lines.join("\n"),
        exit_code,
    }
}

pub(super) fn render_resolve_response(
    response: AssetResolveResponse,
    json_output: bool,
    exit_code: i32,
) -> RenderedCommand {
    if json_output {
        return RenderedCommand {
            stdout: serde_json::to_string_pretty(&response)
                .unwrap_or_else(|source| render_json_error(&source.to_string())),
            exit_code,
        };
    }

    let mut lines = vec![format!("status: {}", response.status)];
    if let Some(key) = &response.key {
        lines.push(format!("key: {key}"));
    }
    if let Some(load_path) = &response.load_path {
        lines.push(format!("loadPath: {load_path}"));
    }
    RenderedCommand {
        stdout: lines.join("\n"),
        exit_code,
    }
}

fn render_human_explain_response(response: &AssetExplainResponse) -> String {
    let explanation = &response.explanation;
    if response.explanation_kind == "encounter-scene-package" {
        let mut lines = vec![
            format!("command: {}", response.command),
            format!("status: {}", response.status),
            format!("query: {}", response.query),
            format!("executionMode: {}", response.execution_mode),
            format!("explanationKind: {}", response.explanation_kind),
            format!(
                "encounterId: {}",
                explanation
                    .get("encounterId")
                    .and_then(|value| value.as_str())
                    .unwrap_or("")
            ),
            format!(
                "visualPartCount: {}",
                explanation
                    .get("visualParts")
                    .and_then(|value| value.as_array())
                    .map_or(0, Vec::len)
            ),
            format!(
                "renderTargetCount: {}",
                explanation
                    .get("renderTargets")
                    .and_then(|value| value.as_array())
                    .map_or(0, Vec::len)
            ),
        ];
        lines.push(String::new());
        return lines.join("\n");
    }

    let mut lines = vec![
        format!("command: {}", response.command),
        format!("status: {}", response.status),
        format!("query: {}", response.query),
        format!("executionMode: {}", response.execution_mode),
        format!("explanationKind: {}", response.explanation_kind),
        format!(
            "rootScene.path: {}",
            explanation
                .get("rootScene")
                .and_then(|value| value.get("path"))
                .and_then(|value| value.as_str())
                .unwrap_or("")
        ),
        format!(
            "selectedLayerCount: {}",
            explanation
                .get("selectedLayers")
                .and_then(|value| value.as_array())
                .map_or(0, Vec::len)
        ),
        format!(
            "bounds.finalVisible: x={} y={} width={} height={}",
            explanation
                .pointer("/bounds/finalVisible/x")
                .and_then(|value| value.as_f64())
                .unwrap_or(0.0),
            explanation
                .pointer("/bounds/finalVisible/y")
                .and_then(|value| value.as_f64())
                .unwrap_or(0.0),
            explanation
                .pointer("/bounds/finalVisible/width")
                .and_then(|value| value.as_f64())
                .unwrap_or(0.0),
            explanation
                .pointer("/bounds/finalVisible/height")
                .and_then(|value| value.as_f64())
                .unwrap_or(0.0)
        ),
        format!(
            "activeScene.status: {}",
            explanation
                .pointer("/activeScene/status")
                .and_then(|value| value.as_str())
                .unwrap_or("")
        ),
    ];

    if let Some(warnings) = explanation
        .get("warnings")
        .and_then(|value| value.as_array())
    {
        lines.push("warnings:".to_string());
        for warning in warnings {
            lines.push(format!(
                "  - {}: {}",
                warning
                    .get("code")
                    .and_then(|value| value.as_str())
                    .unwrap_or(""),
                warning
                    .get("message")
                    .and_then(|value| value.as_str())
                    .unwrap_or("")
            ));
        }
    }

    lines.push(String::new());
    lines.join("\n")
}

pub(super) fn render_batch_response(
    response: AssetBatchExtractResponse,
    json_output: bool,
    exit_code: i32,
) -> RenderedCommand {
    let stdout = if json_output {
        serde_json::to_string(&response)
            .map(|text| format!("{text}\n"))
            .unwrap_or_else(|source| render_json_error(&source.to_string()))
    } else {
        render_human_batch_response(&response)
    };
    RenderedCommand { stdout, exit_code }
}

fn render_human_batch_response(response: &AssetBatchExtractResponse) -> String {
    let mut lines = vec![
        format!("command: {}", response.command),
        format!("status: {}", response.status),
        format!("manifestPath: {}", response.manifest_path),
        format!("outputDir: {}", response.output_dir),
        format!("requestCount: {}", response.request_count),
        format!("successCount: {}", response.success_count),
        format!("failureCount: {}", response.failure_count),
        format!("skippedCount: {}", response.skipped_count),
        format!("exportCount: {}", response.export_count),
        format!("timing.totalMs: {}", response.timing.total_ms),
        format!("timing.indexMs: {}", response.timing.index_ms),
        format!("timing.resolveMs: {}", response.timing.resolve_ms),
        format!("timing.liveSetupMs: {}", response.timing.live_setup_ms),
        format!("timing.exportMs: {}", response.timing.export_ms),
        "results:".to_string(),
    ];
    for result in &response.results {
        lines.push(format!("  - id: {}", result.id));
        lines.push(format!("    status: {}", result.status));
        lines.push(format!("    query: {}", result.query));
        lines.push(format!("    outputDir: {}", result.output_dir));
        lines.push(format!("    exportCount: {}", result.export_count));
        lines.push(format!("    resolveMs: {}", result.resolve_ms));
        lines.push(format!("    liveSetupMs: {}", result.live_setup_ms));
        lines.push(format!("    exportMs: {}", result.export_ms));
        if !result.exports.is_empty() {
            lines.push("    exports:".to_string());
            for export in &result.exports {
                lines.push(format!("      - status: {}", export.status));
                lines.push(format!("        sourcePath: {}", export.source_path));
                lines.push(format!("        extractionMs: {}", export.extraction_ms));
                lines.push(format!(
                    "        outputPath: {}",
                    export.output_path.as_deref().unwrap_or("null")
                ));
            }
        }
        if let Some(error) = &result.error {
            lines.push(format!("    errorCode: {}", error.code));
            lines.push(format!("    errorMessage: {}", error.message));
        }
    }
    lines.join("\n") + "\n"
}

fn render_human_response(response: &AssetExtractResponse) -> String {
    let mut lines = vec![
        format!("command: {}", response.command),
        format!("status: {}", response.status),
        format!("query: {}", response.query),
        format!("format: {}", response.format),
        format!("executionMode: {}", response.execution_mode),
        format!("outputDir: {}", response.output_dir),
        format!("matchCount: {}", response.match_count),
        format!("exportCount: {}", response.export_count),
        "exports:".to_string(),
    ];

    for export in &response.exports {
        lines.push(format!("  - status: {}", export.status));
        lines.push(format!("    sourceId: {}", export.source_id));
        lines.push(format!("    sourcePath: {}", export.source_path));
        lines.push(format!("    sourceRoot: {}", export.source_root));
        lines.push(format!("    assetKind: {}", export.asset_kind));
        lines.push(format!("    executionMode: {}", export.execution_mode));
        lines.push(format!("    extractionMs: {}", export.extraction_ms));
        lines.push(format!(
            "    outputPath: {}",
            export.output_path.as_deref().unwrap_or("null")
        ));
        if !export.notes.is_empty() {
            lines.push("    notes:".to_string());
            for note in &export.notes {
                lines.push(format!("      - {}", note));
            }
        }
    }

    lines.join("\n") + "\n"
}

fn render_json_error(message: &str) -> String {
    format!(
        "{}\n",
        serde_json::to_string(&json!({
            "error": {
                "code": "serialization_failed",
                "message": message
            }
        }))
        .unwrap_or_else(|_| "{\"error\":{\"code\":\"serialization_failed\",\"message\":\"failed to serialize assets response\"}}".to_string())
    )
}
