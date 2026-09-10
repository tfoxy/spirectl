const FAILURE_DIAGNOSTICS_RPC_TIMEOUT_MS: u64 = 250;

pub(crate) fn run(args: TestRunArgs, context: AppContext<'_>) -> RenderedCommand {
    let prepared = prepare_profiled_run(args, context);
    let report = match prepared {
        PreparedRun::Ready {
            args,
            owned_context,
            profile,
            preflight,
            cleanup,
        } => {
            let effective_context = owned_context
                .as_ref()
                .map(|owned| owned.as_app_context(context.json_output))
                .unwrap_or(context);
            execute_run_report_with_profile(&args, effective_context, profile, preflight, cleanup)
        }
        PreparedRun::Invalid {
            args,
            profile,
            error,
        } => invalid_profile_report(&args, context, profile, error),
    };
    render_report(report, context.json_output)
}

#[allow(
    clippy::large_enum_variant,
    reason = "Prepared run state carries owned execution context only while dispatching a test run."
)]
enum PreparedRun {
    Ready {
        args: TestRunArgs,
        owned_context: Option<RuntimeContextOwned>,
        profile: Option<TestRunProfileSummary>,
        preflight: Option<Value>,
        cleanup: Option<TestProfileCleanupConfig>,
    },
    Invalid {
        args: TestRunArgs,
        profile: Option<TestRunProfileSummary>,
        error: Value,
    },
}

fn prepare_profiled_run(args: TestRunArgs, context: AppContext<'_>) -> PreparedRun {
    let Some(profile_name) = args.profile.clone() else {
        return PreparedRun::Ready {
            args,
            owned_context: None,
            profile: None,
            preflight: None,
            cleanup: None,
        };
    };

    let Some(profile_config) = context.config.test.profiles.get(&profile_name) else {
        return PreparedRun::Invalid {
            args,
            profile: Some(TestRunProfileSummary::unknown(&profile_name)),
            error: runner_error(
                "unknown_test_profile",
                &format!("Test profile '{profile_name}' is not defined in config.test.profiles."),
            ),
        };
    };

    let mut effective_config = match profile_config.config.clone() {
        Some(overlay) => match context.config.with_yaml_overlay(overlay) {
            Ok(config) => config,
            Err(error) => {
                return PreparedRun::Invalid {
                    args,
                    profile: Some(TestRunProfileSummary::from_config(
                        &profile_name,
                        profile_config,
                    )),
                    error: error_payload(&error),
                };
            }
        },
        None => context.config.clone(),
    };
    if effective_config.transport.rpc_timeout_ms.is_none()
        && (profile_config.preflight.deploy_bridge
            || profile_config.preflight.deploy.is_some()
            || profile_config.preflight.launch
            || profile_config.preflight.attach)
    {
        effective_config.transport.rpc_timeout_ms = Some(profile_config.preflight.rpc_timeout_ms);
    }

    if profile_config.gate.require_live_transport
        && effective_config.transport.kind == TransportKind::Mock
    {
        return PreparedRun::Invalid {
            args,
            profile: Some(TestRunProfileSummary::from_config(
                &profile_name,
                profile_config,
            )),
            error: runner_error(
                "test_profile_requires_live_transport",
                &format!("Test profile '{profile_name}' requires non-mock transport."),
            ),
        };
    }

    let owned_context =
        RuntimeContextOwned::from_app_context_with_config(context, effective_config);
    let effective_context = owned_context.as_app_context(context.json_output);
    let preflight =
        match execute_profile_preflight(&profile_name, profile_config, effective_context) {
            Ok(preflight) => preflight,
            Err(error) => match maybe_run_sts2_recover(effective_context, &error) {
                Some(recovery) if recovery_succeeded(&recovery) => {
                    match execute_profile_preflight(
                        &profile_name,
                        profile_config,
                        effective_context,
                    ) {
                        Ok(preflight) => Some(json!({
                            "status": "recovered",
                            "recovery": recovery,
                            "preflight": preflight
                        })),
                        Err(retry_error) => {
                            return PreparedRun::Invalid {
                                args,
                                profile: Some(TestRunProfileSummary::from_config(
                                    &profile_name,
                                    profile_config,
                                )),
                                error: error_with_recovery(retry_error, recovery),
                            };
                        }
                    }
                }
                Some(recovery) => {
                    return PreparedRun::Invalid {
                        args,
                        profile: Some(TestRunProfileSummary::from_config(
                            &profile_name,
                            profile_config,
                        )),
                        error: error_with_recovery(error, recovery),
                    };
                }
                None => {
                    return PreparedRun::Invalid {
                        args,
                        profile: Some(TestRunProfileSummary::from_config(
                            &profile_name,
                            profile_config,
                        )),
                        error,
                    };
                }
            },
        };

    PreparedRun::Ready {
        args,
        owned_context: Some(owned_context),
        profile: Some(TestRunProfileSummary::from_config(
            &profile_name,
            profile_config,
        )),
        preflight,
        cleanup: Some(profile_config.cleanup.clone()),
    }
}

fn execute_profile_preflight(
    profile_name: &str,
    profile: &TestProfileConfig,
    context: AppContext<'_>,
) -> Result<Option<Value>, Value> {
    if !profile.preflight.deploy_bridge
        && profile.preflight.deploy.is_none()
        && !profile.preflight.launch
        && !profile.preflight.attach
    {
        return Ok(None);
    }

    let mut steps = Map::new();
    if profile.preflight.deploy_bridge {
        let result = execute_game_install_bridge_json(
            crate::GameInstallBridgeArgs::default(),
            context,
        )
        .map_err(|error| {
            profile_preflight_error(profile_name, "game install-bridge", error_payload(&error))
        })?;
        steps.insert("installBridge".to_string(), result);
    }
    if let Some(deploy) = &profile.preflight.deploy {
        let path = deploy.path.as_deref().ok_or_else(|| {
            profile_preflight_error(
                profile_name,
                "game deploy",
                runner_error(
                    "invalid_test_profile",
                    "Test profile preflight deploy requires a path.",
                ),
            )
        })?;
        let result = execute_game_deploy_json(
            DeployArgs {
                path: resolve_config_relative_path(path, context),
                build: deploy.build,
                restart: deploy.restart,
                verify: deploy.verify,
                timeout_ms: deploy.timeout_ms,
                interval_ms: deploy.interval_ms,
                rpc_timeout_ms: deploy.rpc_timeout_ms,
                verify_stable_ms: deploy.verify_stable_ms,
                allow_stale_build: false,
                wait_quiescent_ms: 0,
                quiescent_stable_samples: 3,
                require_quiescent: false,
                launch_args: Vec::new(),
            },
            context,
        )
        .map_err(|error| {
            profile_preflight_error(profile_name, "game deploy", error_payload(&error))
        })?;
        steps.insert("deploy".to_string(), result);
    }
    if profile.preflight.launch {
        let result = execute_game_launch_with_args_json(
            GameLaunchArgs {
                timeout_ms: profile.preflight.timeout_ms,
                interval_ms: profile.preflight.interval_ms,
                rpc_timeout_ms: profile.preflight.rpc_timeout_ms,
                verify_stable_ms: profile.preflight.verify_stable_ms,
                no_detach_session: false,
                disable_background_throttle: false,
                verbose: false,
                wait_quiescent_ms: 0,
                quiescent_stable_samples: 3,
                require_quiescent: false,
                launch_args: Vec::new(),
            },
            context,
        )
        .map_err(|error| {
            profile_preflight_error(profile_name, "game launch", error_payload(&error))
        })?;
        steps.insert("launch".to_string(), result);
    } else if profile.preflight.attach {
        let result = execute_game_attach_json(
            LifecycleWaitArgs {
                timeout_ms: profile.preflight.timeout_ms,
                interval_ms: profile.preflight.interval_ms,
                rpc_timeout_ms: profile.preflight.rpc_timeout_ms,
            },
            context,
        )
        .map_err(|error| {
            profile_preflight_error(profile_name, "game attach", error_payload(&error))
        })?;
        steps.insert("attach".to_string(), result);
    }

    Ok(Some(json!({
        "profile": profile_name,
        "steps": steps
    })))
}

fn profile_preflight_error(profile_name: &str, command: &str, error: Value) -> Value {
    json!({
        "code": "test_profile_preflight_failed",
        "message": format!("Test profile '{profile_name}' preflight command '{command}' failed."),
        "profile": profile_name,
        "command": command,
        "error": error
    })
}

pub(crate) fn stress(args: TestStressArgs, context: AppContext<'_>) -> RenderedCommand {
    let started_at = Instant::now();
    let configured_dir = args
        .artifacts_dir
        .clone()
        .unwrap_or_else(|| PathBuf::from(&context.config.artifacts.dir));
    let root_dir = resolve_absolute_path(&configured_dir)
        .join("test-stress")
        .join(format!(
            "stress-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis()
        ));
    let summary_path = root_dir.join("summary.json");
    let mut artifact_errors = Vec::new();
    if let Err(source) = fs::create_dir_all(&root_dir) {
        artifact_errors.push(io_artifact_error(
            "stress",
            "create-dir",
            &root_dir,
            &format!(
                "Failed to create test stress artifact directory '{}': {source}",
                root_dir.display()
            ),
        ));
    }

    let mut iterations = Vec::new();
    let mut completed_iterations = 0_u32;
    let mut passed_iterations = 0_u32;
    let mut failed_iterations = 0_u32;
    let mut total_passed_scenarios = 0_usize;
    let mut total_failed_scenarios = 0_usize;
    let mut total_invalid_scenarios = 0_usize;
    let mut exit_code = 0_i32;
    let mut first_failure: Option<Value> = None;
    let mut last_failure: Option<Value> = None;
    let requested_iterations = args.iterations;
    let stop_reason = loop {
        if let Some(limit) = args.iterations
            && completed_iterations >= limit
        {
            break "iterations-complete".to_string();
        }
        if let Some(limit_ms) = args.duration_ms
            && completed_iterations > 0
            && started_at.elapsed() >= Duration::from_millis(limit_ms)
        {
            break "duration-elapsed".to_string();
        }

        let iteration_index = completed_iterations + 1;
        let run_report = execute_run_report(
            &TestRunArgs {
                profile: None,
                tags: Vec::new(),
                artifacts_dir: args.artifacts_dir.clone(),
                failure_artifacts: args.failure_artifacts,
                inline: None,
                path: Some(args.path.clone()),
            },
            context,
        );

        completed_iterations += 1;
        total_passed_scenarios += run_report.passed_scenario_count;
        total_failed_scenarios += run_report.failed_scenario_count;
        total_invalid_scenarios += run_report.invalid_scenario_count;
        exit_code = exit_code.max(run_report.exit_code);

        let iteration_status = if run_report.exit_code == 0 {
            passed_iterations += 1;
            "passed"
        } else {
            failed_iterations += 1;
            "failed"
        };

        let iteration_summary = json!({
            "iteration": iteration_index,
            "status": iteration_status,
            "exitCode": run_report.exit_code,
            "scenarioCount": run_report.scenario_count,
            "passedScenarioCount": run_report.passed_scenario_count,
            "failedScenarioCount": run_report.failed_scenario_count,
            "invalidScenarioCount": run_report.invalid_scenario_count,
            "elapsedMs": run_report.elapsed_ms,
            "runRootDir": run_report.artifacts.root_dir,
            "runSummaryPath": run_report.artifacts.summary_path,
        });

        if run_report.exit_code != 0 {
            if first_failure.is_none() {
                first_failure = Some(iteration_summary.clone());
            }
            last_failure = Some(iteration_summary.clone());
            if failed_iterations > args.max_failures {
                iterations.push(iteration_summary);
                break "max-failures-reached".to_string();
            }
        }

        iterations.push(iteration_summary);

        if args.cooldown_ms > 0 {
            std::thread::sleep(Duration::from_millis(args.cooldown_ms));
        }
    };

    let status = if failed_iterations > 0 {
        "failed"
    } else {
        "passed"
    };
    let payload = json!({
        "status": status,
        "path": args.path.display().to_string(),
        "requestedIterations": requested_iterations,
        "requestedDurationMs": args.duration_ms,
        "maxFailures": args.max_failures,
        "cooldownMs": args.cooldown_ms,
        "completedIterations": completed_iterations,
        "passedIterations": passed_iterations,
        "failedIterations": failed_iterations,
        "passedScenarioCount": total_passed_scenarios,
        "failedScenarioCount": total_failed_scenarios,
        "invalidScenarioCount": total_invalid_scenarios,
        "elapsedMs": started_at.elapsed().as_millis(),
        "exitCode": exit_code,
        "stopReason": stop_reason,
        "iterations": iterations,
        "firstFailure": first_failure,
        "lastFailure": last_failure,
        "artifacts": {
            "rootDir": root_dir.display().to_string(),
            "summaryPath": summary_path.display().to_string(),
            "errors": artifact_errors,
        }
    });

    if artifact_errors.is_empty()
        && let Err(source) = write_json_file(&summary_path, &payload)
    {
        let mut payload = payload;
        payload["artifacts"]["errors"] = json!([io_artifact_error(
            "stress",
            "write",
            &summary_path,
            &format!(
                "Failed to write test stress summary '{}': {source}",
                summary_path.display()
            ),
        )]);
        return RenderedCommand {
            stdout: if context.json_output {
                let mut rendered = serde_json::to_string_pretty(&payload).expect("json");
                rendered.push('\n');
                rendered
            } else {
                render_human_stress_report(&payload)
            },
            exit_code,
        };
    }

    RenderedCommand {
        stdout: if context.json_output {
            let mut rendered = serde_json::to_string_pretty(&payload).expect("json");
            rendered.push('\n');
            rendered
        } else {
            render_human_stress_report(&payload)
        },
        exit_code,
    }
}

pub(crate) fn execute_run_report(args: &TestRunArgs, context: AppContext<'_>) -> TestRunReport {
    execute_run_report_with_profile(args, context, None, None, None)
}

fn execute_run_report_with_profile(
    args: &TestRunArgs,
    context: AppContext<'_>,
    profile: Option<TestRunProfileSummary>,
    preflight: Option<Value>,
    cleanup: Option<TestProfileCleanupConfig>,
) -> TestRunReport {
    let started_at = Instant::now();
    let root = run_root_label(args, profile.as_ref());
    let mut artifacts = RunArtifactsManager::new(
        args.artifacts_dir.as_deref(),
        args.failure_artifacts,
        context,
    );

    let mut report = match discover_scenarios(args, profile.as_ref()) {
        Ok(entries) => {
            let discovered =
                run_discovered(entries, args, profile.as_ref(), &mut artifacts, context);
            if discovered.scenarios.is_empty() {
                let no_match =
                    profile_no_match_result(profile.as_ref(), &discovered.not_applicable);
                build_report(
                    root.clone(),
                    started_at.elapsed().as_millis(),
                    no_match.exit_code,
                    discovered.scenarios,
                    no_match.errors,
                    artifacts.snapshot(),
                    profile.clone(),
                    preflight.clone(),
                )
            } else {
                let exit_code = discovered
                    .scenarios
                    .iter()
                    .map(|scenario| scenario.exit_code)
                    .max()
                    .unwrap_or(0);
                build_report(
                    root.clone(),
                    started_at.elapsed().as_millis(),
                    exit_code,
                    discovered.scenarios,
                    Vec::new(),
                    artifacts.snapshot(),
                    profile.clone(),
                    preflight.clone(),
                )
            }
        }
        Err(error) => build_report(
            root,
            started_at.elapsed().as_millis(),
            2,
            Vec::new(),
            vec![error],
            artifacts.snapshot(),
            profile.clone(),
            preflight.clone(),
        ),
    };
    apply_profile_cleanup(&mut report, cleanup.as_ref(), preflight.as_ref());
    artifacts.persist_summary(&mut report);
    report
}

fn apply_profile_cleanup(
    report: &mut TestRunReport,
    cleanup: Option<&TestProfileCleanupConfig>,
    preflight: Option<&Value>,
) {
    let Some(cleanup) = cleanup else {
        return;
    };
    if !cleanup.launched_game {
        return;
    }

    let mut pids = Vec::new();
    if let Some(preflight) = preflight {
        collect_launch_pids(preflight, &mut pids);
    }
    pids.sort_unstable();
    pids.dedup();

    let mut targets = Vec::new();
    let mut ok = true;
    for pid in pids {
        let target = cleanup_launched_process_group(pid, cleanup.timeout_ms, cleanup.interval_ms);
        if !target["ok"].as_bool().unwrap_or(false) {
            ok = false;
        }
        targets.push(target);
    }

    let payload = json!({
        "launchedGame": {
            "requested": true,
            "ok": ok,
            "targetCount": targets.len(),
            "targets": targets
        }
    });
    if !ok {
        report.exit_code = report.exit_code.max(2);
        if report.failed_scenario_count == 0 {
            report.status = "invalid";
        }
        report.errors.push(json!({
            "code": "test_profile_cleanup_failed",
            "message": "Test profile cleanup failed to stop every launched game process group.",
            "cleanup": payload
        }));
    }
    report.cleanup = Some(payload);
}

fn collect_launch_pids(value: &Value, pids: &mut Vec<u32>) {
    match value {
        Value::Object(object) => {
            if let Some(pid) = object
                .get("launch")
                .and_then(|launch| launch.get("pid"))
                .and_then(Value::as_u64)
                .and_then(|pid| u32::try_from(pid).ok())
            {
                pids.push(pid);
            }
            for child in object.values() {
                collect_launch_pids(child, pids);
            }
        }
        Value::Array(items) => {
            for child in items {
                collect_launch_pids(child, pids);
            }
        }
        _ => {}
    }
}

#[cfg(unix)]
fn cleanup_launched_process_group(pid: u32, timeout_ms: u64, interval_ms: u64) -> Value {
    let pgid = pid;
    let existed_before = process_group_exists(pgid);
    if existed_before {
        let _ = signal_process_group(pgid, "TERM");
        wait_for_process_group_exit(pgid, timeout_ms.min(3_000), interval_ms);
        if process_group_exists(pgid) {
            let _ = signal_process_group(pgid, "KILL");
            wait_for_process_group_exit(pgid, timeout_ms, interval_ms);
        }
    }
    let remaining = process_group_exists(pgid);
    json!({
        "pid": pid,
        "pgid": pgid,
        "ok": !remaining,
        "existedBeforeCleanup": existed_before,
        "remaining": remaining,
        "strategy": "unix-process-group"
    })
}

#[cfg(not(unix))]
fn cleanup_launched_process_group(pid: u32, _timeout_ms: u64, _interval_ms: u64) -> Value {
    json!({
        "pid": pid,
        "ok": false,
        "strategy": "unsupported-platform",
        "remaining": true,
        "error": {
            "code": "unsupported_platform",
            "message": "Test profile launched-game cleanup is only implemented for Unix process groups."
        }
    })
}

#[cfg(unix)]
fn signal_process_group(pgid: u32, signal: &str) -> std::io::Result<std::process::ExitStatus> {
    Command::new("kill")
        .args([format!("-{signal}"), "--".to_string(), format!("-{pgid}")])
        .status()
}

#[cfg(unix)]
fn process_group_exists(pgid: u32) -> bool {
    let Ok(output) = Command::new("ps").args(["-eo", "pgid=,stat="]).output() else {
        return Command::new("kill")
            .args(["-0".to_string(), "--".to_string(), format!("-{pgid}")])
            .status()
            .map(|status| status.success())
            .unwrap_or(false);
    };
    if !output.status.success() {
        return false;
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    stdout.lines().any(|line| {
        let mut parts = line.split_whitespace();
        let Some(candidate_pgid) = parts.next() else {
            return false;
        };
        let Some(stat) = parts.next() else {
            return false;
        };
        candidate_pgid == pgid.to_string() && !stat.starts_with('Z')
    })
}

#[cfg(unix)]
fn wait_for_process_group_exit(pgid: u32, timeout_ms: u64, interval_ms: u64) {
    let started = Instant::now();
    let timeout = Duration::from_millis(timeout_ms);
    let interval = Duration::from_millis(interval_ms.max(1));
    while started.elapsed() < timeout {
        if !process_group_exists(pgid) {
            break;
        }
        std::thread::sleep(interval);
    }
}

fn invalid_profile_report(
    args: &TestRunArgs,
    context: AppContext<'_>,
    profile: Option<TestRunProfileSummary>,
    error: Value,
) -> TestRunReport {
    let started_at = Instant::now();
    let mut artifacts = RunArtifactsManager::new(
        args.artifacts_dir.as_deref(),
        args.failure_artifacts,
        context,
    );
    let mut report = build_report(
        run_root_label(args, profile.as_ref()),
        started_at.elapsed().as_millis(),
        2,
        Vec::new(),
        vec![error],
        artifacts.snapshot(),
        profile,
        None,
    );
    artifacts.persist_summary(&mut report);
    report
}

fn run_root_label(args: &TestRunArgs, profile: Option<&TestRunProfileSummary>) -> String {
    args.path
        .as_ref()
        .map(|path| path.display().to_string())
        .or_else(|| args.inline.as_ref().map(|_| "<inline>".to_string()))
        .or_else(|| profile.map(|profile| format!("<profile:{}>", profile.name)))
        .unwrap_or_else(|| "<missing>".to_string())
}

struct RunDiscoveredResult {
    scenarios: Vec<ScenarioReport>,
    not_applicable: Vec<Value>,
}

fn run_discovered(
    entries: Vec<DiscoveredScenario>,
    args: &TestRunArgs,
    profile: Option<&TestRunProfileSummary>,
    artifacts: &mut RunArtifactsManager,
    context: AppContext<'_>,
) -> RunDiscoveredResult {
    let mut reports = Vec::new();
    let mut not_applicable = Vec::new();
    for entry in entries {
        let mut report = match load_scenario(&entry) {
            Ok(scenario) => {
                if scenario_is_not_applicable(&scenario) {
                    if let Some(error) = validate_live_validation_metadata(&scenario) {
                        scenario_error_report(
                            &entry,
                            scenario.name.clone(),
                            scenario.description.clone(),
                            error,
                        )
                    } else {
                        not_applicable.push(json!({
                            "path": scenario.path,
                            "name": scenario.name,
                            "reason": scenario.live_validation.as_ref().map(|value| value.reason.clone()).unwrap_or_default()
                        }));
                        continue;
                    }
                } else {
                    if !scenario_matches_filters(&scenario, args, profile) {
                        continue;
                    }
                    if let Some(error) = validate_live_validation_metadata(&scenario) {
                        scenario_error_report(
                            &entry,
                            scenario.name.clone(),
                            scenario.description.clone(),
                            error,
                        )
                    } else {
                        let scenario_dir = artifacts.scenario_dir_for(
                            reports.len() + 1,
                            &scenario.name,
                            &scenario.path,
                        );
                        execute_scenario_with_recovery(
                            scenario,
                            Some(scenario_dir.as_path()),
                            context,
                        )
                    }
                }
            }
            Err(report) => report,
        };
        artifacts.persist_scenario(reports.len() + 1, &mut report, context);
        reports.push(report);
    }
    RunDiscoveredResult {
        scenarios: reports,
        not_applicable,
    }
}

fn scenario_is_not_applicable(scenario: &LoadedScenario) -> bool {
    scenario
        .live_validation
        .as_ref()
        .is_some_and(|value| value.status == "notApplicable")
}

fn scenario_matches_filters(
    scenario: &LoadedScenario,
    args: &TestRunArgs,
    profile: Option<&TestRunProfileSummary>,
) -> bool {
    let mut include_tags = Vec::new();
    if let Some(profile) = profile {
        include_tags.extend(profile.include_tags.iter().cloned());
        if profile
            .exclude_tags
            .iter()
            .any(|tag| scenario.tags.iter().any(|candidate| candidate == tag))
        {
            return false;
        }
    }
    include_tags.extend(args.tags.iter().cloned());
    include_tags
        .iter()
        .all(|tag| scenario.tags.iter().any(|candidate| candidate == tag))
}

fn validate_live_validation_metadata(scenario: &LoadedScenario) -> Option<Value> {
    let live_validation = scenario.live_validation.as_ref()?;
    if live_validation.status == "notApplicable" && live_validation.reason.trim().is_empty() {
        return Some(runner_error(
            "invalid_live_validation_metadata",
            &format!(
                "Scenario '{}' declares liveValidation.status=notApplicable without a non-empty reason.",
                scenario.name
            ),
        ));
    }
    None
}

struct NoMatchResult {
    exit_code: i32,
    errors: Vec<Value>,
}

fn profile_no_match_result(
    profile: Option<&TestRunProfileSummary>,
    not_applicable: &[Value],
) -> NoMatchResult {
    let Some(profile) = profile else {
        if !not_applicable.is_empty() {
            return NoMatchResult {
                exit_code: 2,
                errors: vec![json!({
                    "code": "no_applicable_scenarios",
                    "message": "All discovered scenarios were marked not applicable.",
                    "skippedScenarios": not_applicable
                })],
            };
        }
        return NoMatchResult {
            exit_code: 2,
            errors: vec![runner_error(
                "no_scenarios_found",
                "No .sts2.yaml or .sts2.json scenario files were found.",
            )],
        };
    };

    if profile.require_matching_scenario {
        if profile.allow_not_applicable && !not_applicable.is_empty() {
            return NoMatchResult {
                exit_code: 0,
                errors: Vec::new(),
            };
        }
        NoMatchResult {
            exit_code: 2,
            errors: vec![runner_error(
                "test_profile_no_matching_scenarios",
                &format!(
                    "Test profile '{}' did not find any scenarios matching its paths and tag filters.",
                    profile.name
                ),
            )],
        }
    } else {
        NoMatchResult {
            exit_code: 0,
            errors: Vec::new(),
        }
    }
}
