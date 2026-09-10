pub(crate) fn execute_scenario_export_json(
    args: ScenarioExportArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = crate::bridge_client(context);
    let response = client
        .capture_scenario(bridge::proto::ScenarioCaptureRequest {
            request_id: format!("scenario-capture-{}", std::process::id()),
            schema_version: SCENARIO_SCHEMA_VERSION.to_string(),
            include_exact: args.include_exact,
            perspective: Some(bridge::proto::PerspectiveSelector {
                scope: bridge::proto::PerspectiveScope::Local as i32,
                player_id: String::new(),
            }),
        })
        .map_err(scenario_bridge_error)?;

    let output_path = args.output;
    if let Some(parent) = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent)
            .map_err(|error| scenario_io_error("create scenario output dir", parent, error))?;
    }

    let bundle = response.exact_bundle_payload.as_ref().and_then(|payload| {
        if payload.data.is_empty() {
            None
        } else {
            Some(payload)
        }
    });
    if args.include_exact && bundle.is_none() {
        return Err(scenario_error(
            3,
            "exact_bundle_unavailable",
            "Bridge scenario capture did not return an exact bundle.",
            json!({ "output": output_path.display().to_string() }),
        ));
    }

    let sidecar_path = bundle
        .map(|payload| scenario_exact_bundle_path(&output_path, payload))
        .unwrap_or_else(|| scenario_exact_bundle_json_path(&output_path));
    let bundle_metadata = if let Some(payload) = bundle {
        let temp_sidecar = output_path.with_file_name(format!(
            ".{}.tmp-{}",
            sidecar_path
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or("scenario.exact-bundle.json"),
            std::process::id()
        ));
        fs::write(&temp_sidecar, &payload.data).map_err(|error| {
            scenario_io_error("write scenario exact bundle", &temp_sidecar, error)
        })?;
        fs::rename(&temp_sidecar, &sidecar_path).map_err(|error| {
            scenario_io_error("publish scenario exact bundle", &sidecar_path, error)
        })?;
        Some(ExactBundleFile {
            path: sidecar_path
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or_default()
                .to_string(),
            format_version: payload.format_version.clone(),
            content_type: payload.content_type.clone(),
            sha256: sha256_hex(&payload.data),
            size_bytes: payload.data.len() as u64,
            version_sensitive: true,
        })
    } else {
        None
    };

    let response_source = response.source();
    let response_provisional = response.provisional;
    let scenario_proto = response.scenario.ok_or_else(|| {
        scenario_error(
            3,
            "unsupported_source_screen",
            "Bridge scenario capture did not return a scenario document.",
            json!({ "output": output_path.display().to_string() }),
        )
    })?;
    let scenario = scenario_from_proto(scenario_proto, bundle_metadata)?;
    let temp_output = output_path.with_file_name(format!(
        ".{}.tmp-{}",
        output_path
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("scenario.sts2.scenario.yaml"),
        std::process::id()
    ));
    let scenario_yaml = serde_yaml::to_string(&scenario).expect("scenario document serializes");
    fs::write(&temp_output, scenario_yaml)
        .map_err(|error| scenario_io_error("write scenario yaml", &temp_output, error))?;
    fs::rename(&temp_output, &output_path)
        .map_err(|error| scenario_io_error("publish scenario yaml", &output_path, error))?;

    let mut payload = json!({
        "schemaVersion": SCENARIO_SCHEMA_VERSION,
        "command": "dev scenario export",
        "path": output_path.display().to_string(),
        "exactBundlePath": scenario.restore.exact_bundle.as_ref().map(|_| sidecar_path.display().to_string()),
        "exactBundleKind": scenario.restore.exact_bundle.as_ref().map(|bundle| exact_bundle_kind(&bundle.format_version, &bundle.content_type)),
        "includeExact": args.include_exact,
        "restoreQuality": scenario.restore.quality,
        "restoreSupport": restore_support_json(&scenario.source.screen.screen_type, &scenario.restore.quality, &scenario.restore.field_reports),
        "screen": screen_json(&scenario.source.screen),
        "source": bridge::data_source_name(response_source),
        "provisional": response_provisional,
        "notices": scenario.notices.iter().map(state_notice_file_json).collect::<Vec<_>>(),
    });
    if let Some(multiplayer) = &scenario.multiplayer {
        payload["multiplayer"] = multiplayer_file_json(multiplayer);
    }
    Ok(payload)
}

fn scenario_from_proto(
    proto: bridge::proto::ScenarioDocument,
    bundle_metadata: Option<ExactBundleFile>,
) -> Result<ScenarioDocumentFile, AppError> {
    if proto.schema_version != SCENARIO_SCHEMA_VERSION {
        return Err(scenario_error(
            2,
            "invalid_scenario_schema_version",
            "Bridge returned an incompatible scenario document schema.",
            json!({
                "schemaVersion": proto.schema_version,
                "expectedSchemaVersion": SCENARIO_SCHEMA_VERSION,
            }),
        ));
    }
    let source = proto.source.ok_or_else(|| {
        scenario_error(
            2,
            "invalid_scenario_document",
            "Bridge scenario document omitted source metadata.",
            json!({ "name": proto.name }),
        )
    })?;
    let screen = source.screen.ok_or_else(|| {
        scenario_error(
            2,
            "invalid_scenario_document",
            "Bridge scenario document omitted screen metadata.",
            json!({ "name": proto.name }),
        )
    })?;
    let restore = proto.restore.unwrap_or_default();
    let screen_state = if proto.screen_state_json.trim().is_empty() {
        Value::Null
    } else {
        serde_json::from_str(&proto.screen_state_json).map_err(|error| {
            scenario_error(
                2,
                "invalid_scenario_document",
                "Bridge scenario screen state JSON is invalid.",
                json!({ "detail": error.to_string() }),
            )
        })?
    };

    Ok(ScenarioDocumentFile {
        schema_version: proto.schema_version,
        name: proto.name,
        description: if proto.description.is_empty() {
            None
        } else {
            Some(proto.description)
        },
        created_at: proto.created_at,
        source: ScenarioSourceFile {
            game_version: source.game_version,
            bridge_version: source.bridge_version,
            spirectl_version: source.spirectl_version,
            screen: ScreenFile {
                screen_type: screen.id,
                title: screen.title,
                screen_instance_id: screen.screen_instance_id,
            },
            perspective: source.perspective.map(|perspective| PerspectiveFile {
                scope: perspective_scope_name(perspective.scope()).to_string(),
                player_id: perspective.player_id,
            }),
        },
        restore: ScenarioRestoreFile {
            mode: restore_mode_name(restore.mode()).to_string(),
            quality: restore_quality_name(restore.quality()).to_string(),
            exact_bundle: bundle_metadata,
            fallback_policy: SCENARIO_FALLBACK_POLICY_DEGRADED.to_string(),
            compatibility_notes: restore
                .compatibility_notes
                .into_iter()
                .map(|note| CompatibilityNoteFile {
                    code: note.code,
                    message: note.message,
                    field: note.field,
                })
                .collect(),
            field_reports: restore
                .field_reports
                .into_iter()
                .map(restore_field_report_from_proto)
                .collect(),
        },
        run: proto.run.map(|run| ScenarioRunFile {
            seed: run.seed,
            act: run.act,
            floor: run.floor,
            ascension: run.ascension,
            players: run
                .players
                .into_iter()
                .map(|player| ScenarioPlayerFile {
                    id: player.id,
                    character: player.character,
                    is_local: player.is_local,
                    is_host: player.is_host,
                    is_remote: player.is_remote,
                })
                .collect(),
        }),
        screen_state,
        notices: proto
            .notices
            .into_iter()
            .map(|notice| StateNoticeFile {
                code: notice.code,
                message: notice.message,
                provisional: notice.provisional,
                path: notice.path,
                severity: notice.severity,
                source: notice.source,
            })
            .collect(),
        multiplayer: proto.multiplayer.and_then(multiplayer_from_proto),
    })
}

fn read_scenario_document(path: &Path) -> Result<ScenarioDocumentFile, AppError> {
    if !path.exists() {
        return Err(scenario_error(
            2,
            "scenario_file_not_found",
            "Scenario file was not found.",
            json!({ "path": path.display().to_string() }),
        ));
    }
    let text = fs::read_to_string(path)
        .map_err(|error| scenario_io_error("read scenario", path, error))?;
    let document: ScenarioDocumentFile = serde_yaml::from_str(&text).map_err(|error| {
        scenario_error(
            2,
            "invalid_scenario_document",
            "Scenario YAML is invalid.",
            json!({
                "path": path.display().to_string(),
                "detail": error.to_string(),
            }),
        )
    })?;
    if document.schema_version != SCENARIO_SCHEMA_VERSION {
        return Err(scenario_error(
            2,
            "invalid_scenario_schema_version",
            "Scenario schema version is not supported.",
            json!({
                "schemaVersion": document.schema_version,
                "expectedSchemaVersion": SCENARIO_SCHEMA_VERSION,
            }),
        ));
    }
    if document.name.is_empty() || document.source.screen.screen_type.is_empty() {
        return Err(scenario_error(
            2,
            "invalid_scenario_document",
            "Scenario document is missing required fields.",
            json!({ "path": path.display().to_string() }),
        ));
    }
    Ok(document)
}

fn read_scenario_exact_bundle_payload(
    scenario_path: &Path,
    document: &ScenarioDocumentFile,
) -> Result<Option<bridge::proto::ExactBundlePayload>, AppError> {
    let Some(bundle) = &document.restore.exact_bundle else {
        return Ok(None);
    };
    let relative_path = Path::new(&bundle.path);
    let invalid_path = relative_path.is_absolute()
        || relative_path.components().any(|component| {
            matches!(
                component,
                Component::ParentDir | Component::RootDir | Component::Prefix(_)
            )
        });
    if invalid_path {
        return Err(scenario_error(
            2,
            "invalid_scenario_document",
            "Scenario exact bundle path must be a relative sidecar path.",
            json!({ "path": bundle.path }),
        ));
    }
    let base_dir = scenario_path.parent().unwrap_or_else(|| Path::new("."));
    let bundle_path = base_dir.join(relative_path);
    if !bundle_path.exists() {
        return Err(scenario_error(
            2,
            "exact_bundle_requested_but_absent",
            "Scenario references an exact bundle that is missing.",
            json!({ "path": bundle_path.display().to_string() }),
        ));
    }
    let bytes = fs::read(&bundle_path)
        .map_err(|error| scenario_io_error("read scenario exact bundle", &bundle_path, error))?;
    let actual_size = u64::try_from(bytes.len()).unwrap_or(u64::MAX);
    if actual_size != bundle.size_bytes {
        return Err(scenario_error(
            2,
            "exact_bundle_size_mismatch",
            "Scenario exact bundle size does not match metadata.",
            json!({
                "path": bundle_path.display().to_string(),
                "expectedSizeBytes": bundle.size_bytes,
                "actualSizeBytes": actual_size,
            }),
        ));
    }
    let actual_hash = sha256_hex(&bytes);
    if actual_hash != bundle.sha256 {
        return Err(scenario_error(
            2,
            "exact_bundle_hash_mismatch",
            "Scenario exact bundle SHA-256 does not match metadata.",
            json!({
                "path": bundle_path.display().to_string(),
                "expectedSha256": bundle.sha256,
                "actualSha256": actual_hash,
            }),
        ));
    }
    Ok(Some(bridge::proto::ExactBundlePayload {
        path: bundle.path.clone(),
        format_version: bundle.format_version.clone(),
        content_type: if bundle.content_type.trim().is_empty() {
            "application/json".to_string()
        } else {
            bundle.content_type.clone()
        },
        data: bytes,
    }))
}

fn scenario_to_proto(document: &ScenarioDocumentFile) -> bridge::proto::ScenarioDocument {
    bridge::proto::ScenarioDocument {
        schema_version: document.schema_version.clone(),
        name: document.name.clone(),
        description: document.description.clone().unwrap_or_default(),
        created_at: document.created_at.clone(),
        source: Some(bridge::proto::ScenarioSourceMetadata {
            game_version: document.source.game_version.clone(),
            bridge_version: document.source.bridge_version.clone(),
            spirectl_version: document.source.spirectl_version.clone(),
            screen: Some(bridge::proto::ScreenInfo {
                id: document.source.screen.screen_type.clone(),
                title: document.source.screen.title.clone(),
                screen_instance_id: document.source.screen.screen_instance_id.clone(),
                source: String::new(),
                raw_type: String::new(),
                class_name: String::new(),
            }),
            perspective: document.source.perspective.as_ref().map(|perspective| {
                bridge::proto::PerspectiveInfo {
                    scope: match perspective.scope.as_str() {
                        "local" => bridge::proto::PerspectiveScope::Local as i32,
                        "omniscient" => bridge::proto::PerspectiveScope::Omniscient as i32,
                        _ => bridge::proto::PerspectiveScope::Unspecified as i32,
                    },
                    player_id: perspective.player_id.clone(),
                    uses_default: false,
                    ..Default::default()
                }
            }),
        }),
        restore: Some(bridge::proto::RestoreMetadata {
            mode: match document.restore.mode.as_str() {
                "sparse" => bridge::proto::RestoreMode::Sparse as i32,
                "exact" => bridge::proto::RestoreMode::Exact as i32,
                "hybrid" => bridge::proto::RestoreMode::Hybrid as i32,
                _ => bridge::proto::RestoreMode::Unspecified as i32,
            },
            quality: match document.restore.quality.as_str() {
                "exact" => bridge::proto::RestoreQuality::Exact as i32,
                "partial" => bridge::proto::RestoreQuality::Partial as i32,
                "unsupported" => bridge::proto::RestoreQuality::Unsupported as i32,
                "degraded" => bridge::proto::RestoreQuality::Degraded as i32,
                _ => bridge::proto::RestoreQuality::Unspecified as i32,
            },
            exact_bundle: document.restore.exact_bundle.as_ref().map(|bundle| {
                bridge::proto::ExactBundleMetadata {
                    path: bundle.path.clone(),
                    format_version: bundle.format_version.clone(),
                    sha256: bundle.sha256.clone(),
                    size_bytes: bundle.size_bytes,
                    version_sensitive: bundle.version_sensitive,
                    content_type: bundle.content_type.clone(),
                }
            }),
            compatibility_notes: document
                .restore
                .compatibility_notes
                .iter()
                .map(|note| bridge::proto::RestoreCompatibilityNote {
                    code: note.code.clone(),
                    message: note.message.clone(),
                    field: note.field.clone(),
                })
                .collect(),
            field_reports: document
                .restore
                .field_reports
                .iter()
                .map(restore_field_report_to_proto)
                .collect(),
        }),
        run: document
            .run
            .as_ref()
            .map(|run| bridge::proto::ScenarioRunState {
                seed: run.seed.clone(),
                act: run.act,
                floor: run.floor,
                ascension: run.ascension,
                players: run
                    .players
                    .iter()
                    .map(|player| bridge::proto::ScenarioPlayerState {
                        id: player.id.clone(),
                        character: player.character.clone(),
                        is_local: player.is_local,
                        is_host: player.is_host,
                        is_remote: player.is_remote,
                    })
                    .collect(),
            }),
        screen_state_json: serde_json::to_string(&document.screen_state)
            .expect("scenario screen state serializes"),
        notices: document
            .notices
            .iter()
            .map(|notice| bridge::proto::StateNotice {
                code: notice.code.clone(),
                message: notice.message.clone(),
                provisional: notice.provisional,
                path: notice.path.clone(),
                severity: notice.severity.clone(),
                source: notice.source.clone(),
                stability: String::new(),
                perspective: String::new(),
            })
            .collect(),
        multiplayer: document.multiplayer.as_ref().map(multiplayer_to_proto),
    }
}

pub(crate) fn execute_scenario_load_json(
    args: ScenarioLoadArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let document = read_scenario_document(args.path())?;
    let exact_bundle_payload = read_scenario_exact_bundle_payload(args.path(), &document)?;
    let allow_sparse_fallback = document.restore.mode == "hybrid"
        || document.restore.fallback_policy == SCENARIO_FALLBACK_POLICY_DEGRADED;
    ensure_multiplayer_degradation_allowed(
        document.multiplayer.as_ref(),
        args.allow_degraded_local_multiplayer,
        scenario_error,
    )?;
    let lifecycle_wait = LifecycleWaitArgs {
        timeout_ms: args.timeout_ms,
        interval_ms: args.interval_ms,
        rpc_timeout_ms: 5_000,
    };
    let restart_payload = if args.restart && context.config.transport.kind != TransportKind::Mock {
        let stop = lifecycle::execute_game_restart_stop_json(
            lifecycle_wait.clone(),
            context,
            "dev scenario load",
        )
        .map_err(|error| scenario_lifecycle_error("game restart stop", error))?;
        let launch = lifecycle::execute_game_launch_json(lifecycle_wait.clone(), context)
            .map_err(|error| scenario_lifecycle_error("game launch", error))?;
        json!({
            "requested": true,
            "stop": stop,
            "launch": launch["launch"].clone(),
            "attachment": launch["attachment"].clone(),
        })
    } else {
        if !args.restart && context.config.transport.kind != TransportKind::Mock {
            lifecycle::try_attach_for_restore(lifecycle_wait.clone(), context)
                .map_err(|error| scenario_lifecycle_error("game attach", error))?;
        }
        json!({
            "requested": args.restart,
        })
    };

    let client = crate::bridge_client(context);
    let response = client
        .restore_scenario(bridge::proto::ScenarioRestoreRequest {
            request_id: format!("scenario-restore-{}", std::process::id()),
            schema_version: SCENARIO_SCHEMA_VERSION.to_string(),
            scenario: Some(scenario_to_proto(&document)),
            restart_requested: args.restart,
            exact_bundle_payload,
            allow_sparse_fallback,
            allow_degraded_local_multiplayer: args.allow_degraded_local_multiplayer,
        })
        .map_err(scenario_bridge_error)?;

    let validation = wait_for_scenario_validation(
        &document,
        response.multiplayer_restore.as_ref(),
        lifecycle_wait,
        context,
    )?;
    let omitted_remote_player_ids = response
        .multiplayer_restore
        .as_ref()
        .map(|restore| restore.omitted_remote_player_ids.clone())
        .unwrap_or_default();
    let mut compatibility_notes = response
        .compatibility_notes
        .iter()
        .map(proto_compatibility_note_json)
        .collect::<Vec<_>>();
    if !omitted_remote_player_ids.is_empty()
        && !compatibility_notes
            .iter()
            .any(|note| note.get("code").and_then(Value::as_str) == Some("remote-clients-omitted"))
    {
        compatibility_notes.push(json!({
            "code": "remote-clients-omitted",
            "message": "Remote multiplayer clients were omitted from degraded local-only scenario restore.",
            "field": "multiplayer.players",
        }));
    }

    let mut payload = json!({
        "schemaVersion": SCENARIO_SCHEMA_VERSION,
        "command": "dev scenario load",
        "path": args.path().display().to_string(),
        "restart": restart_payload,
        "restoreQuality": restore_quality_name(response.quality()).to_string(),
        "exactBundleUsed": response.exact_bundle_used,
        "sparseFallbackUsed": response.sparse_fallback_used,
        "screen": response.screen.as_ref().map(|screen| json!({
            "id": screen.id,
            "title": screen.title,
            "instanceId": screen.screen_instance_id,
        })),
        "validation": validation,
        "bridgeVerification": response.verification.as_ref().map(proto_restore_verification_json),
        "notices": response.notices.iter().map(state_notice_json).collect::<Vec<_>>(),
        "compatibilityNotes": compatibility_notes,
    });
    if let Some(multiplayer_restore) = &response.multiplayer_restore {
        payload["multiplayerRestore"] = multiplayer_restore_result_json(multiplayer_restore);
    }
    Ok(payload)
}
