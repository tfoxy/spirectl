fn scenario_exact_bundle_path(
    output_path: &Path,
    payload: &bridge::proto::ExactBundlePayload,
) -> PathBuf {
    if is_save_backed_exact_bundle(&payload.format_version, &payload.content_type) {
        return scenario_exact_bundle_path_with_suffix(output_path, SCENARIO_EXACT_SAVE_SUFFIX);
    }

    scenario_exact_bundle_json_path(output_path)
}

fn scenario_exact_bundle_json_path(output_path: &Path) -> PathBuf {
    scenario_exact_bundle_path_with_suffix(output_path, SCENARIO_EXACT_BUNDLE_JSON_SUFFIX)
}

fn scenario_exact_bundle_path_with_suffix(output_path: &Path, suffix: &str) -> PathBuf {
    let sidecar_name = format!(
        "{}{}",
        output_path
            .file_stem()
            .and_then(|stem| stem.to_str())
            .unwrap_or("scenario"),
        suffix
    );
    output_path.with_file_name(sidecar_name)
}

fn is_save_backed_exact_bundle(format_version: &str, content_type: &str) -> bool {
    format_version == "spirectl.scenario.save-run/v0"
        || content_type == "application/vnd.spirectl.sts2-save+json"
}

fn exact_bundle_kind(format_version: &str, content_type: &str) -> &'static str {
    if is_save_backed_exact_bundle(format_version, content_type) {
        "save-backed"
    } else {
        "fixture-backed"
    }
}

fn screen_json(screen: &ScreenFile) -> Value {
    json!({
        "id": screen.screen_type,
        "title": screen.title,
        "instanceId": screen.screen_instance_id,
    })
}

fn proto_compatibility_note_json(note: &bridge::proto::RestoreCompatibilityNote) -> Value {
    json!({
        "code": note.code,
        "message": note.message,
        "field": note.field,
    })
}

fn proto_restore_verification_json(verification: &bridge::proto::RestoreVerification) -> Value {
    let expected_summary = serde_json::from_str::<Value>(&verification.expected_summary_json)
        .unwrap_or_else(|_| json!(verification.expected_summary_json));
    let observed_summary = serde_json::from_str::<Value>(&verification.observed_summary_json)
        .unwrap_or_else(|_| json!(verification.observed_summary_json));
    json!({
        "status": restore_verification_status_name(verification.status()),
        "quality": restore_quality_name(verification.quality()),
        "checked": verification.checked_fields,
        "expectedSummary": expected_summary,
        "observedSummary": observed_summary,
        "mismatches": verification.mismatches.iter().map(|mismatch| {
            json!({
                "path": mismatch.path,
                "expected": serde_json::from_str::<Value>(&mismatch.expected_json).unwrap_or_else(|_| json!(mismatch.expected_json)),
                "observed": serde_json::from_str::<Value>(&mismatch.observed_json).unwrap_or_else(|_| json!(mismatch.observed_json)),
                "severity": mismatch.severity,
                "supportClass": mismatch.support_class,
                "reasonCode": mismatch.reason_code,
                "suggestedNextStep": mismatch.suggested_next_step,
            })
        }).collect::<Vec<_>>(),
    })
}

fn state_notice_json(notice: &bridge::proto::StateNotice) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
        "provisional": notice.provisional,
        "path": empty_string_as_null(&notice.path),
        "severity": empty_string_as_null(&notice.severity),
        "source": empty_string_as_null(&notice.source),
    })
}

fn state_notice_file_json(notice: &StateNoticeFile) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
        "provisional": notice.provisional,
        "path": empty_string_as_null(&notice.path),
        "severity": empty_string_as_null(&notice.severity),
        "source": empty_string_as_null(&notice.source),
    })
}

fn empty_string_as_null(value: &str) -> Value {
    if value.is_empty() {
        Value::Null
    } else {
        Value::String(value.to_string())
    }
}

fn scenario_bridge_error(error: bridge::proto::BridgeError) -> AppError {
    AppError {
        exit_code: bridge::bridge_error_exit_code(&error),
        payload: bridge::bridge_error_payload(&error),
    }
}

fn scenario_io_error(action: &str, path: &Path, error: std::io::Error) -> AppError {
    scenario_error(
        2,
        "scenario_io_error",
        "Scenario filesystem operation failed.",
        json!({
            "action": action,
            "path": path.display().to_string(),
            "detail": error.to_string(),
        }),
    )
}

fn scenario_lifecycle_error(step: &str, error: AppError) -> AppError {
    AppError {
        exit_code: error.exit_code,
        payload: json!({
            "error": {
                "code": "scenario_lifecycle_failed",
                "message": "Scenario load lifecycle step failed.",
                "step": step,
                "cause": error.payload,
            }
        }),
    }
}

fn scenario_error(exit_code: i32, code: &str, message: &str, extra: Value) -> AppError {
    let mut error = json!({
        "code": code,
        "message": message,
    });
    if let (Some(error_object), Some(extra_object)) = (error.as_object_mut(), extra.as_object()) {
        for (key, value) in extra_object {
            error_object.insert(key.clone(), value.clone());
        }
    }
    AppError {
        exit_code,
        payload: json!({ "error": error }),
    }
}

