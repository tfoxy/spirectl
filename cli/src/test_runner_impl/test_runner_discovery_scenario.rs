fn discover_scenarios(
    args: &TestRunArgs,
    profile: Option<&TestRunProfileSummary>,
) -> Result<Vec<DiscoveredScenario>, Value> {
    if args.inline.is_some() && args.path.is_some() {
        return Err(runner_error(
            "invalid_test_run_input",
            "Only one of path or --inline may be supplied.",
        ));
    }
    if args.inline.is_some() && args.profile.is_some() {
        return Err(runner_error(
            "invalid_test_run_input",
            "--inline cannot be combined with --profile.",
        ));
    }
    if let Some(inline) = &args.inline {
        return Ok(vec![DiscoveredScenario {
            source: ScenarioSource::Inline(inline.clone()),
            display_path: "<inline>".to_string(),
        }]);
    }

    let paths = if let Some(path) = args.path.as_ref() {
        vec![path.clone()]
    } else if let Some(profile) = profile {
        profile.paths.clone()
    } else {
        return Err(runner_error(
            "missing_test_run_input",
            "Provide a scenario path, --inline YAML/JSON, or --profile with configured paths.",
        ));
    };

    let mut all_entries = Vec::new();
    for path in paths {
        all_entries.extend(discover_scenarios_at_path(&path)?);
    }
    all_entries.sort_by(|left, right| left.display_path.cmp(&right.display_path));
    Ok(all_entries)
}

fn discover_scenarios_at_path(path: &Path) -> Result<Vec<DiscoveredScenario>, Value> {
    if !path.exists() {
        return Err(runner_error(
            "scenario_path_not_found",
            &format!("Scenario path '{}' does not exist.", path.display()),
        ));
    }

    if path.is_file() {
        return Ok(vec![DiscoveredScenario {
            source: ScenarioSource::File(path.to_path_buf()),
            display_path: path.display().to_string(),
        }]);
    }

    if !path.is_dir() {
        return Err(runner_error(
            "invalid_scenario_path",
            &format!(
                "Scenario path '{}' is neither a file nor a directory.",
                path.display()
            ),
        ));
    }

    let mut entries = Vec::new();
    collect_scenarios(path, path, &mut entries).map_err(|source| {
        runner_error(
            "scenario_discovery_failed",
            &format!(
                "Failed to read scenario directory '{}': {source}",
                path.display()
            ),
        )
    })?;
    entries.sort_by(|left, right| left.display_path.cmp(&right.display_path));
    Ok(entries)
}

fn collect_scenarios(
    root: &Path,
    current: &Path,
    entries: &mut Vec<DiscoveredScenario>,
) -> Result<(), std::io::Error> {
    for entry in fs::read_dir(current)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            collect_scenarios(root, &path, entries)?;
            continue;
        }

        if !path
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(is_scenario_filename)
        {
            continue;
        }

        let relative = path
            .strip_prefix(root)
            .unwrap_or(&path)
            .display()
            .to_string();
        entries.push(DiscoveredScenario {
            source: ScenarioSource::File(path),
            display_path: relative,
        });
    }

    Ok(())
}

fn is_scenario_filename(name: &str) -> bool {
    name.ends_with(".sts2.yaml") || name.ends_with(".sts2.json")
}

#[allow(
    clippy::result_large_err,
    reason = "ScenarioReport is returned by value to preserve the internal runner reporting shape."
)]
fn load_scenario(entry: &DiscoveredScenario) -> Result<LoadedScenario, ScenarioReport> {
    let raw = match &entry.source {
        ScenarioSource::File(path) => fs::read_to_string(path).map_err(|source| {
            scenario_error_report(
                entry,
                scenario_display_name(entry, None),
                None,
                runner_error(
                    "scenario_read_failed",
                    &format!(
                        "Failed to read scenario file '{}': {source}",
                        path.display()
                    ),
                ),
            )
        })?,
        ScenarioSource::Inline(raw) => raw.clone(),
    };
    let raw_scenario = serde_yaml::from_str::<RawScenarioFile>(&raw).map_err(|source| {
        scenario_error_report(
            entry,
            scenario_display_name(entry, None),
            None,
            runner_error(
                "scenario_parse_failed",
                &format!(
                    "Failed to parse scenario '{}': {source}",
                    entry.source_label()
                ),
            ),
        )
    })?;

    let mut steps = Vec::new();
    for (index, raw_step) in raw_scenario.steps.into_iter().enumerate() {
        match parse_step(raw_step) {
            Ok(step) => steps.push(step),
            Err(error) => {
                return Err(scenario_invalid_step_report(
                    entry,
                    raw_scenario.name.clone(),
                    raw_scenario.description.clone(),
                    index + 1,
                    error,
                ));
            }
        }
    }

    Ok(LoadedScenario {
        path: entry.display_path.clone(),
        name: raw_scenario.name,
        description: raw_scenario.description,
        tags: raw_scenario.tags,
        live_validation: raw_scenario.live_validation,
        base_dir: match &entry.source {
            ScenarioSource::File(path) => path.parent().map(Path::to_path_buf),
            ScenarioSource::Inline(_) => None,
        },
        steps,
    })
}

fn execute_scenario(
    scenario: LoadedScenario,
    scenario_dir: Option<&Path>,
    context: AppContext<'_>,
) -> ScenarioReport {
    let started_at = Instant::now();
    let mut steps = Vec::new();
    let mut status = "passed";
    let mut exit_code = 0;

    let scenario_base_dir = scenario.base_dir.clone();
    for (index, step) in scenario.steps.into_iter().enumerate() {
        match execute_step(
            &step,
            scenario_base_dir.as_deref(),
            scenario_dir,
            index + 1,
            context,
        ) {
            Ok(output) => steps.push(StepReport {
                index: index + 1,
                requested_id: step.requested_id,
                canonical_id: step.canonical_id,
                status: "passed",
                input: step.input,
                output: Some(output),
                error: None,
                exit_code: 0,
            }),
            Err(error) => {
                status = "failed";
                exit_code = error.exit_code;
                steps.push(StepReport {
                    index: index + 1,
                    requested_id: step.requested_id,
                    canonical_id: step.canonical_id,
                    status: "failed",
                    input: step.input,
                    output: None,
                    error: Some(error_payload(&error)),
                    exit_code,
                });
                break;
            }
        }
    }

    ScenarioReport {
        path: scenario.path,
        name: scenario.name,
        description: scenario.description,
        status,
        elapsed_ms: started_at.elapsed().as_millis(),
        exit_code,
        steps,
        error: None,
        failure: None,
        recovery_attempt: None,
        artifacts: None,
    }
}

fn execute_scenario_with_recovery(
    scenario: LoadedScenario,
    scenario_dir: Option<&Path>,
    context: AppContext<'_>,
) -> ScenarioReport {
    let first = execute_scenario(scenario.clone(), scenario_dir, context);
    if first.status != "failed" || !scenario_report_is_recoverable(&first) {
        return first;
    }

    let Some(error) = scenario_report_error(&first) else {
        return first;
    };
    let Some(recovery) = maybe_run_sts2_recover(context, error) else {
        return first;
    };
    if !recovery_succeeded(&recovery) {
        let mut report = first;
        report.recovery_attempt = Some(json!({
            "status": "failed",
            "recovery": recovery
        }));
        return report;
    }

    let mut retry = execute_scenario(scenario, scenario_dir, context);
    retry.recovery_attempt = Some(json!({
        "status": "retried",
        "recovery": recovery,
        "firstFailure": scenario_failure_json(&first)
    }));
    retry
}

fn maybe_run_sts2_recover(context: AppContext<'_>, error: &Value) -> Option<Value> {
    if context.config.transport.kind != TransportKind::Ipc || !recoverable_live_bridge_error(error)
    {
        return None;
    }

    let started_at = Instant::now();
    let output = Command::new("sts2-host")
        .arg("recover")
        .arg("--json")
        .output()
        .ok()?;
    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).to_string();
    let parsed = serde_json::from_str::<Value>(stdout.trim()).ok();

    Some(json!({
        "command": "sts2-host recover --json",
        "exitCode": output.status.code().unwrap_or(1),
        "elapsedMs": started_at.elapsed().as_millis(),
        "ok": output.status.success()
            && parsed.as_ref().and_then(|value| value.get("ok")).and_then(Value::as_bool) == Some(true),
        "stdout": parsed.unwrap_or_else(|| Value::String(tail_text(&stdout, 20))),
        "stderr": tail_text(&stderr, 20)
    }))
}

fn recovery_succeeded(recovery: &Value) -> bool {
    recovery.get("ok").and_then(Value::as_bool) == Some(true)
}

fn error_with_recovery(mut error: Value, recovery: Value) -> Value {
    match &mut error {
        Value::Object(map) => {
            map.insert("recovery".to_string(), recovery);
            error
        }
        _ => json!({
            "error": error,
            "recovery": recovery
        }),
    }
}

fn scenario_report_is_recoverable(report: &ScenarioReport) -> bool {
    scenario_report_error(report).is_some_and(recoverable_live_bridge_error)
}

fn scenario_report_error(report: &ScenarioReport) -> Option<&Value> {
    report
        .steps
        .last()
        .and_then(|step| step.error.as_ref())
        .or(report.error.as_ref())
}

fn scenario_failure_json(report: &ScenarioReport) -> Value {
    match report.steps.last() {
        Some(step) => json!({
            "stepIndex": step.index,
            "requestedId": step.requested_id,
            "canonicalId": step.canonical_id,
            "exitCode": step.exit_code,
            "error": step.error
        }),
        None => json!({
            "exitCode": report.exit_code,
            "error": report.error
        }),
    }
}

fn recoverable_live_bridge_error(value: &Value) -> bool {
    const NEEDLES: &[&str] = &[
        "ipc_connection_failed",
        "transport_connection_failed",
        "ipc_socket_missing",
        "bridge_rpc_timeout",
        "endpoint_refused_or_stale",
        "endpoint_missing",
        "invalid standalone transport response",
        "refused follow-up artifact collection",
    ];
    value_contains_any(value, NEEDLES)
}

fn value_contains_any(value: &Value, needles: &[&str]) -> bool {
    match value {
        Value::String(text) => {
            let text = text.to_ascii_lowercase();
            needles.iter().any(|needle| text.contains(needle))
        }
        Value::Array(items) => items.iter().any(|item| value_contains_any(item, needles)),
        Value::Object(map) => map.values().any(|item| value_contains_any(item, needles)),
        _ => false,
    }
}

fn tail_text(text: &str, max_lines: usize) -> String {
    let lines = text.lines().collect::<Vec<_>>();
    let start = lines.len().saturating_sub(max_lines);
    lines[start..].join("\n")
}

fn execute_step(
    step: &ScenarioStep,
    scenario_base_dir: Option<&Path>,
    scenario_dir: Option<&Path>,
    step_index: usize,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match &step.execution {
        StepExecution::GameInfo => execute_game_info_json(context),
        StepExecution::GameDeploy(args) => execute_game_deploy_json(
            DeployArgs {
                path: resolve_step_path(&args.path, scenario_base_dir),
                build: args.build,
                restart: args.restart,
                verify: args.verify,
                timeout_ms: args.timeout_ms,
                interval_ms: args.interval_ms,
                rpc_timeout_ms: args.rpc_timeout_ms,
                verify_stable_ms: args.verify_stable_ms,
                allow_stale_build: args.allow_stale_build,
                wait_quiescent_ms: args.wait_quiescent_ms,
                quiescent_stable_samples: args.quiescent_stable_samples,
                require_quiescent: args.require_quiescent,
                launch_args: args.launch_args.clone(),
            },
            context,
        ),
        StepExecution::DevLogs(args) => execute_logs_json(args.clone(), context),
        StepExecution::DevLogHealth(args) => execute_log_health_json(args.clone(), context),
        StepExecution::DevConsole(args) => execute_console_json(
            ConsoleArgs {
                command: args.command.clone(),
                args: args.args.clone(),
            },
            context,
        ),
        StepExecution::DevDiagnostics(args) => {
            let mut args = args.clone();
            args.preset_catalogs = args
                .preset_catalogs
                .iter()
                .map(|path| resolve_step_path(path, scenario_base_dir))
                .collect();
            args.bundle_dir = args
                .bundle_dir
                .as_ref()
                .map(|path| resolve_diagnostics_bundle_dir(path, scenario_dir));
            execute_diagnostics_json(args, context)
        }
        StepExecution::DevHotReload(args) => {
            let project = resolve_step_path(&args.project, scenario_base_dir);
            execute_hot_reload_step(args, project, context)
        }
        StepExecution::DevDelay(args) => {
            std::thread::sleep(Duration::from_millis(args.ms));
            Ok(json!({
                "delayedMs": args.ms
            }))
        }
        StepExecution::DevAssert(args) => execute_assert_json(
            args.path.clone(),
            args.predicate.clone(),
            args.source,
            args.perspective,
            args.player_id.clone(),
            context,
        ),
        StepExecution::DevWaitFor(args) => execute_wait_for_json(
            args.path.clone(),
            args.predicate.clone(),
            args.source,
            args.timeout_ms,
            args.interval_ms,
            args.rpc_timeout_ms,
            args.perspective,
            args.player_id.clone(),
            context,
        ),
        StepExecution::DevLoadFixture(args) => {
            execute_load_fixture_json_from(args.clone(), scenario_base_dir, context)
        }
        StepExecution::DevLoadScenario(args) => {
            let mut args = args.clone();
            args.path = Some(
                resolve_step_path(args.path(), scenario_base_dir)
                    .components()
                    .collect(),
            );
            args.path_flag = None;
            execute_scenario_load_json(args, context)
        }
        StepExecution::DevHttp(args) => execute_http_probe_json(args),
        StepExecution::DevHttpWait(args) => execute_http_wait_probe_json(args),
        StepExecution::DevFetch(args) => {
            let output = args.output.clone().or_else(|| {
                scenario_dir.map(|dir| {
                    dir.join("steps")
                        .join(format!("{step_index:03}-dev-fetch.bin"))
                })
            });
            execute_fetch_probe_json(
                &FetchProbe {
                    source: resolve_step_path(&args.source, scenario_base_dir),
                    output,
                    expect_sha256: args.expect_sha256.clone(),
                    query: args.query.clone(),
                },
                None,
                &context.config.artifacts.dir,
            )
        }
        StepExecution::DevWebsocket(args) => execute_websocket_probe_json(args),
        StepExecution::DevDebugEvents(args) => execute_debug_events_step(args, context),
        StepExecution::ProjectHook(args) => execute_project_hook_run_json(
            &args.name,
            args.input.clone(),
            Some(HookInvocationContext {
                scenario_name: None,
                scenario_path: None,
                scenario_dir: scenario_dir.map(Path::to_path_buf),
                step_index: Some(step_index),
                step_requested_id: Some(step.requested_id.clone()),
                step_canonical_id: Some(step.canonical_id.clone()),
            }),
            context,
        ),
        StepExecution::DevScreenshot(args) => {
            let mut args = args.clone();
            args.preset_catalogs = args
                .preset_catalogs
                .iter()
                .map(|path| resolve_step_path(path, scenario_base_dir))
                .collect();
            args.output = args.output.clone().or_else(|| {
                scenario_dir.map(|dir| {
                    dir.join("steps")
                        .join(format!("{step_index:03}-dev-screenshot.png"))
                })
            });
            execute_screenshot_json(args, context)
        }
        StepExecution::DevScreenshotDiff(args) => {
            let mut args = args.clone();
            args.baseline = resolve_step_path(&args.baseline, scenario_base_dir);
            args.actual = args
                .actual
                .as_ref()
                .map(|path| resolve_step_path(path, scenario_base_dir));
            args.mask = args
                .mask
                .as_ref()
                .map(|path| resolve_step_path(path, scenario_base_dir));
            args.regions = args
                .regions
                .as_ref()
                .map(|path| resolve_step_path(path, scenario_base_dir));
            args.preset_catalogs = args
                .preset_catalogs
                .iter()
                .map(|path| resolve_step_path(path, scenario_base_dir))
                .collect();
            args.bundle_dir = Some(match args.bundle_dir.as_ref() {
                Some(path) => resolve_diagnostics_bundle_dir(path, scenario_dir),
                None => scenario_dir
                    .map(|dir| {
                        dir.join("steps")
                            .join(format!("{step_index:03}-dev-screenshot-diff"))
                    })
                    .expect("scenario dir for screenshot diff step"),
            });
            execute_screenshot_diff_json(args, context)
        }
        StepExecution::DevSnapshotCompare(args) => {
            let mut args = args.clone();
            args.spec = resolve_step_path(&args.spec, scenario_base_dir);
            args.baseline = resolve_step_path(&args.baseline, scenario_base_dir);
            args.preset_catalogs = args
                .preset_catalogs
                .iter()
                .map(|path| resolve_step_path(path, scenario_base_dir))
                .collect();
            args.bundle_dir = Some(match args.bundle_dir.as_ref() {
                Some(path) => resolve_diagnostics_bundle_dir(path, scenario_dir),
                None => scenario_dir
                    .map(|dir| {
                        dir.join("steps")
                            .join(format!("{step_index:03}-dev-snapshot-compare"))
                    })
                    .expect("scenario dir for snapshot compare step"),
            });
            execute_snapshot_compare_json(args, context)
        }
        StepExecution::ActPlayCard(args) => {
            execute_action_json(ActSubcommand::PlayCard(args.clone()), context)
        }
        StepExecution::ActUsePotion(args) => {
            execute_action_json(ActSubcommand::UsePotion(args.clone()), context)
        }
        StepExecution::ActChoose(args) => {
            execute_action_json(ActSubcommand::Choose(args.clone()), context)
        }
        StepExecution::ActConfirmSelection => execute_action_json(
            ActSubcommand::ConfirmSelection(ConfirmSelectionArgs::default()),
            context,
        ),
        StepExecution::ActCancelSelection => execute_action_json(
            ActSubcommand::CancelSelection(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActSelectMapNode(args) => {
            execute_action_json(ActSubcommand::SelectMapNode(args.clone()), context)
        }
        StepExecution::ActEndTurn => execute_action_json(
            ActSubcommand::EndTurn(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActReady(args) => {
            execute_action_json(ActSubcommand::Ready(args.clone()), context)
        }
        StepExecution::ActUnready(args) => {
            execute_action_json(ActSubcommand::Unready(args.clone()), context)
        }
        StepExecution::ActSelectCharacter(args) => {
            execute_action_json(ActSubcommand::SelectCharacter(args.clone()), context)
        }
        StepExecution::ActClaimReward(args) => {
            execute_action_json(ActSubcommand::ClaimReward(args.clone()), context)
        }
        StepExecution::ActSkipRewards => execute_action_json(
            ActSubcommand::SkipRewards(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActSelectCard(args) => {
            execute_action_json(ActSubcommand::SelectCard(args.clone()), context)
        }
        StepExecution::ActSkipCardSelection => execute_action_json(
            ActSubcommand::SkipCardSelection(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActSelectBundle(args) => {
            execute_action_json(ActSubcommand::SelectBundle(args.clone()), context)
        }
        StepExecution::ActBuyCard(args) => {
            execute_action_json(ActSubcommand::BuyCard(args.clone()), context)
        }
        StepExecution::ActBuyRelic(args) => {
            execute_action_json(ActSubcommand::BuyRelic(args.clone()), context)
        }
        StepExecution::ActBuyPotion(args) => {
            execute_action_json(ActSubcommand::BuyPotion(args.clone()), context)
        }
        StepExecution::ActRemoveCard(args) => {
            execute_action_json(ActSubcommand::RemoveCard(args.clone()), context)
        }
        StepExecution::ActLeaveShop => execute_action_json(
            ActSubcommand::LeaveShop(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActCloseShopInventory => execute_action_json(
            ActSubcommand::CloseShopInventory(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActRest => execute_action_json(
            ActSubcommand::Rest(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActSmith(args) => {
            execute_action_json(ActSubcommand::Smith(args.clone()), context)
        }
        StepExecution::ActUseRestSiteOption(args) => {
            execute_action_json(ActSubcommand::UseRestSiteOption(args.clone()), context)
        }
        StepExecution::ActProceedRestSite => execute_action_json(
            ActSubcommand::ProceedRestSite(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActOpenChest => execute_action_json(
            ActSubcommand::OpenChest(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActTakeRelic(args) => {
            execute_action_json(ActSubcommand::TakeRelic(args.clone()), context)
        }
        StepExecution::ActProceedTreasureRoom => execute_action_json(
            ActSubcommand::ProceedTreasureRoom(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActBackFromMap => execute_action_json(
            ActSubcommand::BackFromMap(PlayerScopedActionArgs::default()),
            context,
        ),
        StepExecution::ActSelectEventOption(args) => {
            execute_action_json(ActSubcommand::SelectEventOption(args.clone()), context)
        }
        StepExecution::ActOpenEventShop(args) => {
            execute_action_json(ActSubcommand::OpenEventShop(args.clone()), context)
        }
        StepExecution::ActUseCrystalSphereControl(args) => execute_action_json(
            ActSubcommand::UseCrystalSphereControl(args.clone()),
            context,
        ),
        StepExecution::ActProceedEvent => execute_action_json(
            ActSubcommand::ProceedEvent(PlayerScopedActionArgs::default()),
            context,
        ),
    }
}

fn execute_hot_reload_step(
    args: &HotReloadStepExecution,
    project: PathBuf,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let before = if args.expect_generation_changed {
        Some(execute_mod_reload_status_json(
            ModReloadStatusArgs {
                project: project.clone(),
            },
            context,
        )?)
    } else {
        None
    };

    let result = execute_mod_reload_json(
        ModReloadCommand {
            command: None,
            project: Some(project),
            build: args.build,
            wait: args.wait,
            timeout_ms: args.timeout_ms,
            interval_ms: args.interval_ms,
        },
        context,
    )?;

    if !args.expect_generation_changed {
        return Ok(result);
    }

    let before_generation = before
        .as_ref()
        .and_then(|value| value.pointer("/shell/activeGeneration"))
        .and_then(Value::as_i64);
    let after_generation = result
        .pointer("/reload/generation")
        .or_else(|| result.pointer("/shell/activeGeneration"))
        .and_then(Value::as_i64);
    let generation_changed = before_generation
        .zip(after_generation)
        .is_some_and(|(before, after)| after > before);

    if !generation_changed {
        return Err(AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "hot_reload_generation_unchanged",
                    "message": "Hot reload completed but the active generation did not increase.",
                    "beforeGeneration": before_generation,
                    "afterGeneration": after_generation,
                    "result": result,
                }
            }),
        });
    }

    Ok(json!({
        "status": result["status"].clone(),
        "project": result["project"].clone(),
        "before": {
            "activeGeneration": before_generation,
        },
        "reload": result["reload"].clone(),
        "generationChanged": true,
        "result": result,
    }))
}

fn execute_debug_events_step(
    step: &DebugEventsStepExecution,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let output = execute_debug_events_json(step.args.clone(), context)?;
    if let Some(expected) = &step.expect_event_kind {
        let matched = output
            .get("events")
            .and_then(Value::as_array)
            .is_some_and(|events| {
                events
                    .iter()
                    .any(|event| event.get("kind").and_then(Value::as_str) == Some(expected))
            });
        if !matched {
            return Err(AppError {
                exit_code: 3,
                payload: json!({
                    "error": {
                        "code": "debug_event_kind_not_observed",
                        "message": format!("Debugger event transcript did not include expected kind '{expected}'."),
                        "expectedKind": expected,
                        "transcript": output,
                    }
                }),
            });
        }
    }
    Ok(output)
}
