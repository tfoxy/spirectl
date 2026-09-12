pub(crate) fn execute_game_deploy_json(
    args: DeployArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    prepare_instance_for_command(context, "game deploy")?;
    // Read before the context is re-scoped for the RPC timeout: scoped contexts
    // intentionally drop the instance identity.
    let instance = context.instance;
    let rpc_context = context_with_rpc_timeout(context, args.rpc_timeout_ms);
    let context = rpc_context.as_app_context(context.json_output);
    let layout = resolve_live_bridge_layout(context.config, "game deploy")?;
    cache_lifecycle_paths(context, &layout, true);
    let project_root = resolve_deploy_project_root(&args.path)?;

    let expected_mod_source = expected_mod_deploy_source(context.config, &project_root);

    let build = if args.build {
        let run = run_user_build_command(context.config, &project_root, &expected_mod_source)?;
        let freshness = assert_deploy_source_fresh(
            context.config,
            &project_root,
            &expected_mod_source,
            &run,
            args.allow_stale_build,
        )?;
        let mut build_json = run.json;
        build_json["freshness"] = freshness;
        Some(build_json)
    } else {
        None
    };

    let bridge = deploy_bridge(
        "game deploy",
        context.config,
        &layout,
        BridgeDeployOptions::default(),
    )?;
    let mod_source = resolve_mod_deploy_source(context.config, &project_root)?;
    let mod_name = deployable_mod_name(&mod_source)?;
    let mod_target = layout.mods_dir.join(&mod_name);
    copy_directory("game deploy", &mod_source, &mod_target)?;

    let stopped = if args.restart {
        stop_running_game(context.config, &layout, instance)?
    } else {
        Vec::new()
    };

    let launch = if args.restart {
        Some(execute_game_launch_with_args_json(
            GameLaunchArgs {
                timeout_ms: args.timeout_ms,
                interval_ms: args.interval_ms,
                rpc_timeout_ms: args.rpc_timeout_ms,
                verify_stable_ms: args.verify_stable_ms,
                no_detach_session: false,
                disable_background_throttle: false,
                verbose: false,
                // Quiescence is awaited once by `game deploy` itself, after the
                // launch and the optional verify, not inside the nested launch.
                wait_quiescent_ms: 0,
                quiescent_stable_samples: args.quiescent_stable_samples,
                require_quiescent: false,
                launch_args: args.launch_args.clone(),
            },
            context,
        )?)
    } else {
        None
    };

    let verification = if args.verify {
        Some(match &launch {
            Some(payload) => {
                add_attach_stability(payload["attachment"].clone(), payload["stability"].clone())
            }
            None => add_attach_stability(
                execute_game_attach_json(
                    LifecycleWaitArgs {
                        timeout_ms: args.timeout_ms,
                        interval_ms: args.interval_ms,
                        rpc_timeout_ms: args.rpc_timeout_ms,
                    },
                    context,
                )?,
                verify_post_attach_stability(
                    &layout,
                    args.verify_stable_ms,
                    args.interval_ms,
                    context,
                    "game deploy",
                    None,
                    None,
                )?,
            ),
        })
    } else {
        None
    };

    // Only meaningful once something re-attached: a plain file copy leaves the
    // running game (if any) exactly where it was.
    let quiescence = if args.restart || args.verify {
        wait_for_quiescence(
            context,
            "game deploy",
            args.wait_quiescent_ms,
            args.quiescent_stable_samples,
            args.require_quiescent,
        )?
    } else {
        None
    };

    let mut payload = json!({
        "bridge": bridge,
        "mod": {
            "projectRoot": project_root.display().to_string(),
            "sourceDir": mod_source.display().to_string(),
            "targetDir": mod_target.display().to_string(),
            "name": mod_name
        },
        "build": build,
        "restart": {
            "requested": args.restart,
            "stoppedPids": stopped,
            "launchArgs": args.launch_args,
            "launch": launch
        },
        "verify": {
            "requested": args.verify,
            "result": verification
        }
    });
    if let Some(quiescence) = quiescence
        && let Some(object) = payload.as_object_mut()
    {
        object.insert("quiescence".to_string(), quiescence);
    }
    Ok(payload)
}

fn add_attach_stability(mut attachment: Value, stability: Value) -> Value {
    if stability["requestedMs"].as_u64().unwrap_or(0) > 0
        && let Some(object) = attachment.as_object_mut()
    {
        object.insert("stability".to_string(), stability);
    }
    attachment
}

fn verify_post_attach_stability(
    layout: &ResolvedLiveBridgeLayout,
    verify_stable_ms: u64,
    interval_ms: u64,
    context: AppContext<'_>,
    command_name: &str,
    mut child: Option<&mut std::process::Child>,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
) -> Result<Value, AppError> {
    if verify_stable_ms == 0 {
        return Ok(json!({
            "requestedMs": 0,
            "stable": true
        }));
    }

    let started_at = Instant::now();
    let mut attempts = 0u64;
    let mut last_error = Value::Null;

    loop {
        attempts += 1;
        if let Some(child) = child.as_mut() {
            let pid = child.id();
            let status = child.try_wait().map_err(|source| {
                lifecycle_error(
                    4,
                    "launch_failed",
                    command_name,
                    &format!("failed to inspect launch command state: {source}"),
                )
            })?;
            if let Some(status) = status {
                return Err(launch_exited_after_attach_error(
                    command_name,
                    layout,
                    verify_stable_ms,
                    attempts,
                    started_at.elapsed().as_millis(),
                    pid,
                    &status,
                    &last_error,
                    recent_log_cursor,
                ));
            }
        }

        let elapsed = started_at.elapsed();
        let window_done = elapsed >= Duration::from_millis(verify_stable_ms);
        crate::progress::emit(
            command_name,
            "stability.sample",
            json!({ "attempts": attempts, "requestedMs": verify_stable_ms }),
        );
        // Same trade as wait_for_bridge: the handshake proves the bridge is
        // still answering every sample, the full state walk runs once at the end
        // of the window.
        let sample = execute_game_info_json(context).and(if window_done {
            execute_state_json(StateArgs::default(), context).map(|_| ())
        } else {
            Ok(())
        });
        if let Err(error) = sample {
            if is_bridge_rpc_timeout(&error.payload) {
                return Err(error);
            }
            last_error = error.payload;
            return Err(bridge_lost_after_attach_error(
                command_name,
                layout,
                verify_stable_ms,
                attempts,
                started_at.elapsed().as_millis(),
                &last_error,
                recent_log_cursor,
            ));
        }

        if window_done {
            return Ok(json!({
                "requestedMs": verify_stable_ms,
                "stable": true,
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "attempts": attempts,
                "elapsedMs": elapsed.as_millis()
            }));
        }

        let remaining_ms = Duration::from_millis(verify_stable_ms)
            .saturating_sub(elapsed)
            .as_millis()
            .min(interval_ms as u128)
            .max(1) as u64;
        thread::sleep(Duration::from_millis(remaining_ms));
    }
}

fn launch_exited_after_attach_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    verify_stable_ms: u64,
    attempts: u64,
    elapsed_ms: u128,
    pid: u32,
    status: &std::process::ExitStatus,
    last_error: &Value,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
) -> AppError {
    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": "launch_exited_after_attach",
                "command": command_name,
                "message": format!(
                    "The launched process exited during the {} ms post-attach stability window.",
                    verify_stable_ms
                ),
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "verifyStableMs": verify_stable_ms,
                "attempts": attempts,
                "elapsedMs": elapsed_ms,
                "exitStatus": status.code(),
                "process": launch_process_json(pid, Some(status)),
                "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
                "lastError": last_error
            }
        }),
    }
}

fn bridge_lost_after_attach_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    verify_stable_ms: u64,
    attempts: u64,
    elapsed_ms: u128,
    last_error: &Value,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
) -> AppError {
    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": "bridge_lost_after_attach",
                "command": command_name,
                "message": format!(
                    "The live {} bridge became unreachable during the {} ms post-attach stability window.",
                    layout.endpoint.kind(),
                    verify_stable_ms
                ),
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "verifyStableMs": verify_stable_ms,
                "attempts": attempts,
                "elapsedMs": elapsed_ms,
                "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
                "lastError": last_error
            }
        }),
    }
}

fn ensure_live_transport(config: &AppConfig, command: &str) -> Result<(), AppError> {
    if config.transport.kind == TransportKind::Mock {
        return Err(lifecycle_error(
            2,
            "invalid_lifecycle_config",
            command,
            "transport.kind must be 'ipc' or 'tcp' for live lifecycle commands.",
        ));
    }

    Ok(())
}

fn context_with_rpc_timeout(context: AppContext<'_>, rpc_timeout_ms: u64) -> RuntimeContextOwned {
    let mut config = context.config.clone();
    config.transport.rpc_timeout_ms = (rpc_timeout_ms > 0).then_some(rpc_timeout_ms);
    RuntimeContextOwned::from_app_context_with_config(context, config)
}

fn is_bridge_rpc_timeout(payload: &Value) -> bool {
    payload
        .get("error")
        .and_then(|error| error.get("code"))
        .and_then(Value::as_str)
        == Some("bridge_rpc_timeout")
}

fn resolve_live_bridge_layout(
    config: &AppConfig,
    command: &str,
) -> Result<ResolvedLiveBridgeLayout, AppError> {
    resolve_layout(config, Default::default())
        .map_err(|message| lifecycle_error(2, "invalid_lifecycle_config", command, &message))
}

fn cache_lifecycle_paths(
    context: AppContext<'_>,
    layout: &ResolvedLiveBridgeLayout,
    used_assemblies_dir: bool,
) {
    let _ = maybe_cache_resolved_local_paths(
        context,
        Some(&layout.game_path),
        layout.used_discovered_game_path(),
        Some(&layout.assemblies_dir),
        used_assemblies_dir && layout.derived_assemblies_dir_from_game_path(),
    );
}

#[allow(unused_assignments)]
fn wait_for_bridge(
    layout: &ResolvedLiveBridgeLayout,
    timeout_ms: u64,
    interval_ms: u64,
    context: AppContext<'_>,
    command_name: &str,
    mut child: Option<&mut std::process::Child>,
    steam_log_cursor: Option<&bridge::Sts2LogCursor>,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
    mut game_process_observation: Option<&mut LaunchGameProcessObservation>,
    output_capture: Option<&LaunchOutputCapture>,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let mut attempts = 0u64;
    let mut last_error = None;

    loop {
        attempts += 1;
        crate::progress::emit(
            command_name,
            "attach.poll",
            json!({ "attempts": attempts, "timeoutMs": timeout_ms }),
        );
        if let Some(observation) = game_process_observation.as_deref_mut() {
            observation.observe(command_name)?;
        }
        // Poll the cheap handshake only. The full state extraction is worth one
        // call to prove the runtime really answers, not one per poll: on a
        // 4-player boss fight that walk costs more than the whole wait budget.
        match execute_game_info_json(context)
            .and_then(|game_info| Ok((game_info, execute_state_json(StateArgs::default(), context)?)))
        {
            Ok((game_info, state)) => {
                return Ok(json!({
                    "gamePath": layout.game_path.display().to_string(),
                    "assembliesDir": layout.assemblies_dir.display().to_string(),
                    "modsDir": layout.mods_dir.display().to_string(),
                    "modDir": layout.mod_dir.display().to_string(),
                    "endpoint": endpoint_json(&layout.endpoint),
                    "socketPath": layout.endpoint.socket_path(),
                    "pipeName": layout.endpoint.pipe_name(),
                    "tcpAddress": layout.endpoint.tcp_address(),
                    "attempts": attempts,
                    "elapsedMs": started_at.elapsed().as_millis(),
                    "gameInfo": game_info,
                    "screen": state["screen"].clone(),
                    "stateSummary": attach_state_summary_json(&state)
                }));
            }
            Err(error) => {
                if is_bridge_rpc_timeout(&error.payload) {
                    return Err(error);
                }
                last_error = Some(error.payload);
            }
        }

        if let Some(child) = child.as_mut() {
            let pid = child.id();
            let status = child.try_wait().map_err(|source| {
                lifecycle_error(
                    4,
                    "launch_failed",
                    command_name,
                    &format!("failed to inspect launch command state: {source}"),
                )
            })?;
            if let Some(status) = status {
                return Err(launch_wait_error(
                    command_name,
                    layout,
                    attempts,
                    started_at.elapsed().as_millis(),
                    pid,
                    &status,
                    last_error.as_ref(),
                    recent_log_cursor,
                    output_capture,
                ));
            }
        }

        if command_name == "game launch"
            && let Some((log_path, log_line)) =
                steam_log_cursor.and_then(bridge::steam_initialization_failure_since)
        {
            if let Some(child) = child.as_mut() {
                terminate_launch_child(command_name, child)?;
            }
            return Err(steam_initialization_error(
                command_name,
                layout,
                attempts,
                started_at.elapsed().as_millis(),
                &log_path,
                &log_line,
                last_error.unwrap_or(Value::Null),
                recent_log_cursor,
                output_capture,
            ));
        }

        if started_at.elapsed() >= Duration::from_millis(timeout_ms) {
            let process = child
                .as_ref()
                .map(|child| launch_process_json(child.id(), None));
            if command_name == "game launch"
                && let Some(observation) = game_process_observation.as_deref()
                && !observation.ever_observed
                && let Some(child) = child.as_mut()
            {
                terminate_launch_child(command_name, child)?;
                let terminated_status = child.try_wait().map_err(|source| {
                    lifecycle_error(
                        4,
                        "launch_failed",
                        command_name,
                        &format!(
                            "failed to inspect launch command state after wrapper termination: {source}"
                        ),
                    )
                })?;
                return Err(launch_game_process_not_started_error(
                    command_name,
                    layout,
                    timeout_ms,
                    interval_ms,
                    attempts,
                    started_at.elapsed().as_millis(),
                    last_error.unwrap_or(Value::Null),
                    launch_process_json(child.id(), terminated_status.as_ref()),
                    recent_log_cursor,
                    observation,
                    output_capture,
                ));
            }
            return Err(wait_timeout_error(
                command_name,
                layout,
                timeout_ms,
                interval_ms,
                attempts,
                started_at.elapsed().as_millis(),
                last_error.unwrap_or(Value::Null),
                process,
                recent_log_cursor,
                output_capture,
            ));
        }

        thread::sleep(Duration::from_millis(interval_ms));
    }
}

/// Compact proof that the attached runtime answered a full state extraction.
/// (`state.screen` disappeared with the state envelope; the summary is what a
/// caller can actually assert on.)
fn attach_state_summary_json(state: &Value) -> Value {
    json!({
        "schemaVersion": state["schemaVersion"].clone(),
        "language": state["language"].clone(),
        "rootScene": state["rootScene"].clone(),
        "hasCharacterSelect": !state["characterSelect"].is_null(),
        "hasRun": !state["run"].is_null()
    })
}

/// Poll interval for the optional post-attach quiescence wait. Matches the
/// `dev wait-for-transitions` default: the RPC is cheap and the point is to
/// notice the moment the transitions stop.
const QUIESCENCE_INTERVAL_MS: u64 = 50;
const QUIESCENCE_RPC_TIMEOUT_MS: u64 = 1_000;

/// Optional settle step for lifecycle commands: a reachable bridge only means
/// the mod is up, not that the game has finished its boot/room transitions.
/// Callers that immediately drive the UI otherwise have to sleep blindly.
///
/// Returns `None` when no budget was requested so the JSON key stays absent.
/// Non-fatal by default: a timeout reports `quiescent:false` and exit 0.
fn wait_for_quiescence(
    context: AppContext<'_>,
    command_name: &str,
    budget_ms: u64,
    stable_samples: u32,
    require: bool,
) -> Result<Option<Value>, AppError> {
    if budget_ms == 0 {
        return Ok(None);
    }

    let required_stable_samples = stable_samples.max(1);
    let started_at = Instant::now();
    crate::progress::emit(
        command_name,
        "quiescence.wait",
        json!({
            "budgetMs": budget_ms,
            "requiredStableSamples": required_stable_samples
        }),
    );
    let result = execute_wait_for_transitions_json(
        WaitForTransitionsArgs {
            timeout_ms: budget_ms,
            interval_ms: QUIESCENCE_INTERVAL_MS,
            stable_samples: required_stable_samples,
            rpc_timeout_ms: QUIESCENCE_RPC_TIMEOUT_MS,
        },
        context,
    );

    crate::progress::emit(
        command_name,
        "quiescence.done",
        json!({ "quiescent": result.is_ok() }),
    );
    match result {
        Ok(result) => Ok(Some(json!({
            "requested": true,
            "quiescent": true,
            "timedOut": false,
            "budgetMs": budget_ms,
            "requiredStableSamples": required_stable_samples,
            "attempts": result["attempts"].clone(),
            "elapsedMs": result["elapsedMs"].clone(),
            "status": result["status"].clone()
        }))),
        Err(error) if require => Err(AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "quiescence_timeout",
                    "command": command_name,
                    "message": format!(
                        "The game did not reach transition quiescence within the {budget_ms} ms budget."
                    ),
                    "budgetMs": budget_ms,
                    "requiredStableSamples": required_stable_samples,
                    "elapsedMs": started_at.elapsed().as_millis(),
                    "cause": error.payload["error"].clone()
                }
            }),
        }),
        Err(error) => {
            let reason = error.payload["error"]["code"]
                .as_str()
                .unwrap_or("unknown")
                .to_string();
            Ok(Some(json!({
                "requested": true,
                "quiescent": false,
                "timedOut": reason == "transition_wait_timeout",
                "reason": reason,
                "budgetMs": budget_ms,
                "requiredStableSamples": required_stable_samples,
                "elapsedMs": started_at.elapsed().as_millis(),
                "status": error.payload["error"]["lastStatus"].clone(),
                "lastError": error.payload["error"].clone()
            })))
        }
    }
}

fn terminate_launch_child(
    command_name: &str,
    child: &mut std::process::Child,
) -> Result<(), AppError> {
    if agent_host_launch_enabled() {
        let _ = Command::new("agent-host-client")
            .args(["sts2", "kill"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }

    #[cfg(target_os = "linux")]
    {
        send_signal(command_name, child.id(), "TERM")?;
        for _ in 0..30 {
            if child
                .try_wait()
                .map_err(|source| {
                    lifecycle_error(
                        4,
                        "launch_termination_failed",
                        command_name,
                        &format!("failed to inspect launch command state after TERM: {source}"),
                    )
                })?
                .is_some()
            {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(100));
        }
    }

    child.kill().map_err(|source| {
        lifecycle_error(
            4,
            "launch_termination_failed",
            command_name,
            &format!("failed to terminate launched process: {source}"),
        )
    })?;
    let _ = child.wait();
    Ok(())
}

#[derive(Debug, Clone)]
struct LaunchCommand {
    game_executable: PathBuf,
    game_args: Vec<String>,
    wrapper_command: Option<String>,
    wrapper_args: Vec<String>,
    spawn_command: String,
    spawn_args: Vec<String>,
    working_dir: PathBuf,
    env: Vec<(String, String)>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct DetectedGameProcess {
    pid: u32,
    executable: Option<PathBuf>,
    argv0: Option<PathBuf>,
    match_kind: &'static str,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct DetectedWrapperProcess {
    pid: u32,
    executable: Option<PathBuf>,
    argv0: Option<PathBuf>,
    command_line: Vec<String>,
    match_kind: &'static str,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
struct LifecycleProcessTarget {
    launch_executable: PathBuf,
    /// When targeting a specific instance, only `/proc` entries whose command
    /// line carries this `--user-dir` are matched. This disambiguates
    /// shared-build instances that all run the same executable.
    instance_user_dir: Option<PathBuf>,
    /// Pids this instance's registry recorded for its last launch. Checked
    /// before the executable-path scan so a game launched from a different
    /// checkout (or through a rebuilt mirror) can still be found.
    recorded_pids: Vec<u32>,
    /// The executable the recorded pids were launched with, which may differ
    /// from the one this invocation resolves.
    recorded_launch_executable: Option<PathBuf>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct LifecycleWrapperTarget {
    wrapper_command: String,
    launch_executable: PathBuf,
    instance_user_dir: Option<PathBuf>,
}

fn resolve_launch_command_process_target(
    launch: &LaunchCommand,
    command_name: &str,
) -> Result<LifecycleProcessTarget, AppError> {
    let launch_executable = fs::canonicalize(&launch.game_executable).map_err(|source| {
        lifecycle_error(
            2,
            "invalid_lifecycle_config",
            command_name,
            &format!(
                "failed to canonicalize launch executable '{}': {source}",
                launch.game_executable.display()
            ),
        )
    })?;

    Ok(LifecycleProcessTarget {
        launch_executable,
        instance_user_dir: extract_user_dir_arg(&launch.game_args),
        ..LifecycleProcessTarget::default()
    })
}

/// Pids (and the executable they were launched with) recorded by the instance
/// registry. Empty without an active instance: the registry is the only place
/// that remembers a process this invocation did not start.
fn recorded_instance_process_identity(
    instance: Option<&crate::instance::InstanceContext>,
) -> (Vec<u32>, Option<PathBuf>) {
    let Some(instance) = instance else {
        return (Vec::new(), None);
    };
    let Some(record) = crate::instance::read_record(&instance.registry_path) else {
        return (Vec::new(), None);
    };
    let mut pids = record.game_pids.clone();
    if let Some(pid) = record.pid
        && !pids.contains(&pid)
    {
        pids.push(pid);
    }
    (pids, record.launch_executable.map(PathBuf::from))
}

/// Extract the `--user-dir <path>` value injected for a per-instance launch.
fn extract_user_dir_arg(game_args: &[String]) -> Option<PathBuf> {
    let mut iter = game_args.iter();
    while let Some(arg) = iter.next() {
        if arg == "--user-dir" {
            return iter.next().map(PathBuf::from);
        }
        if let Some(rest) = arg.strip_prefix("--user-dir=") {
            return Some(PathBuf::from(rest));
        }
    }
    None
}

fn resolve_launch_command_wrapper_target(
    launch: &LaunchCommand,
    process_target: &LifecycleProcessTarget,
) -> Option<LifecycleWrapperTarget> {
    launch
        .wrapper_command
        .as_ref()
        .filter(|_| !agent_host_launch_enabled())
        .map(|wrapper_command| LifecycleWrapperTarget {
            wrapper_command: wrapper_command.clone(),
            launch_executable: process_target.launch_executable.clone(),
            instance_user_dir: process_target.instance_user_dir.clone(),
        })
}

fn resolve_lifecycle_process_target(
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    command_name: &str,
    instance: Option<&crate::instance::InstanceContext>,
) -> Result<LifecycleProcessTarget, AppError> {
    // Process matching only reads the resolved command line, never the launch
    // env, so per-launch env opt-ins are irrelevant here.
    let launch = resolve_launch_command(config, layout, &[], false).map_err(|error| AppError {
        exit_code: error.exit_code,
        payload: rewrite_error_command(error.payload, command_name),
    })?;
    let mut target = resolve_launch_command_process_target(&launch, command_name)?;
    let (recorded_pids, recorded_launch_executable) = recorded_instance_process_identity(instance);
    target.recorded_pids = recorded_pids;
    target.recorded_launch_executable = recorded_launch_executable;
    Ok(target)
}

/// What the matcher had to work with, reported when nothing matched so the
/// caller can tell "already stopped" from "looked in the wrong place".
fn lifecycle_match_attempts_json(target: &LifecycleProcessTarget) -> Value {
    json!({
        "recordedPids": target.recorded_pids,
        "launchExecutable": target.launch_executable.display().to_string(),
        "recordedLaunchExecutable": target
            .recorded_launch_executable
            .as_ref()
            .map(|path| path.display().to_string()),
        "instanceUserDir": target
            .instance_user_dir
            .as_ref()
            .map(|path| path.display().to_string())
    })
}

fn resolve_lifecycle_wrapper_target(
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    command_name: &str,
) -> Result<Option<LifecycleWrapperTarget>, AppError> {
    let launch = resolve_launch_command(config, layout, &[], false).map_err(|error| AppError {
        exit_code: error.exit_code,
        payload: rewrite_error_command(error.payload, command_name),
    })?;
    let process_target = resolve_launch_command_process_target(&launch, command_name)?;
    Ok(resolve_launch_command_wrapper_target(
        &launch,
        &process_target,
    ))
}

fn ensure_steam_appid_file(game_path: &Path, command_name: &str) -> Result<Value, AppError> {
    let path = game_path.join(STEAM_APPID_FILE_NAME);
    let should_exist = path_looks_steam_managed(game_path) || steamworks_file_exists(game_path);
    if !should_exist {
        return Ok(json!({
            "status": "not_applicable",
            "path": path.display().to_string(),
            "appId": STS2_STEAM_APP_ID,
            "message": "The launch path does not look Steam-managed or Steamworks-backed."
        }));
    }

    match fs::read_to_string(&path) {
        Ok(contents) if contents.trim() == STS2_STEAM_APP_ID => {
            return Ok(json!({
                "status": "present",
                "path": path.display().to_string(),
                "appId": STS2_STEAM_APP_ID,
                "message": "steam_appid.txt already exists with the expected STS2 app id."
            }));
        }
        Ok(_) => {
            return Ok(json!({
                "status": "present_unexpected_value",
                "path": path.display().to_string(),
                "appId": STS2_STEAM_APP_ID,
                "message": "steam_appid.txt already exists but does not contain the expected STS2 app id; sts2 left it unchanged."
            }));
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => {
            return Err(steam_appid_error(
                command_name,
                &path,
                &format!("failed to read existing steam_appid.txt: {error}"),
            ));
        }
    }

    fs::write(&path, format!("{STS2_STEAM_APP_ID}\n")).map_err(|source| {
        steam_appid_error(
            command_name,
            &path,
            &format!("failed to create steam_appid.txt: {source}"),
        )
    })?;

    Ok(json!({
        "status": "created",
        "path": path.display().to_string(),
        "appId": STS2_STEAM_APP_ID,
        "message": "Created steam_appid.txt so direct launches can initialize Steamworks with the STS2 app id."
    }))
}

fn steam_appid_error(command_name: &str, path: &Path, message: &str) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "launch_steam_appid_file_failed",
                "command": command_name,
                "path": path.display().to_string(),
                "appId": STS2_STEAM_APP_ID,
                "message": message
            }
        }),
    }
}

fn resolve_launch_command(
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    launch_args: &[String],
    disable_background_throttle: bool,
) -> Result<LaunchCommand, AppError> {
    let game_executable = if let Some(path) = config.game.launch_executable.as_deref() {
        resolve_runtime_path(path)
    } else {
        derive_launch_executable(&layout.game_path, current_launch_platform())?
    };
    let (wrapper_command, wrapper_args) = match config.game.launch_wrapper.split_first() {
        Some((command, _)) if command.trim().is_empty() => {
            return Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_lifecycle_config",
                        "command": "game launch",
                        "configKey": "game.launchWrapper",
                        "message": "game.launchWrapper must be empty or start with a non-empty wrapper command."
                    }
                }),
            });
        }
        Some((command, args)) => (Some(command.clone()), args.to_vec()),
        None => (None, Vec::new()),
    };

    let working_dir = config
        .game
        .launch_working_dir
        .as_deref()
        .map(resolve_runtime_path)
        .unwrap_or_else(|| layout.game_path.clone());
    let mut env = config
        .game
        .launch_env
        .iter()
        .map(|(key, value)| (key.clone(), value.clone()))
        .collect::<Vec<_>>();
    env.retain(|(key, _)| {
        key != "SPIRECTL_BRIDGE_SOCKET_PATH"
            && key != "SPIRECTL_BRIDGE_PIPE_NAME"
            && key != "SPIRECTL_BRIDGE_TCP_ADDRESS"
    });
    let (endpoint_var, endpoint_value) = layout.endpoint.launch_env_var();
    env.push((endpoint_var.to_string(), endpoint_value.to_string()));

    // Temporary auto-player workaround: enable the bridge's asset-load crash
    // guards. Off by default so normal launches render real textures; pushed
    // after `launch_env` so the config flag wins if both are set. A consumer can
    // also set this directly via `game.launchEnv` when the flag is left false.
    if config.game.asset_load_guard {
        env.push((
            "SPIRECTL_BRIDGE_ASSET_LOAD_GUARD".to_string(),
            "1".to_string(),
        ));
    }

    // Developer opt-in: keep this instance responsive while its window is
    // backgrounded (the bridge mod skips the game's background FPS limit and pins
    // vertical sync on, which is what keeps the engine from parking its main loop
    // in the swapchain acquire while hidden). Off by default so ordinary launches
    // keep the shipped power-saving behavior; pushed after `launch_env` so the
    // config key / launch flag wins if both are set. Setting the env var
    // directly through `game.launchEnv` works too.
    if config.game.disable_background_throttle || disable_background_throttle {
        env.push((
            "SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE".to_string(),
            "1".to_string(),
        ));
    }

    let mut game_args = config.game.launch_args.clone();
    game_args.extend(launch_args.iter().cloned());

    // Per-instance Godot user-dir isolation (saves/settings/logs/shader cache).
    // The engine parses `--user-dir` regardless of position, so prepend it to
    // keep any trailing game-specific args intact. Reaches the executable in
    // both the wrapper and agent-host launch paths because all game_args are
    // forwarded to the executable.
    if let Some(user_dir) = config.game.user_dir.as_deref() {
        let resolved = resolve_runtime_path(user_dir);
        let _ = fs::create_dir_all(&resolved);
        // `--user-dir` is kept so lifecycle process-matching can identify this
        // instance's game by its command line — but STS2 itself IGNORES it (it
        // uses a custom user-dir name). The engine DOES honor `XDG_DATA_HOME`, so
        // that is what actually isolates the instance's saves/settings/enabled-mods
        // (`user://` becomes `<resolved>/SlayTheSpire2`). The mirror keeps the
        // user's other XDG data (Vulkan, fonts, …) resolvable.
        game_args.insert(0, resolved.display().to_string());
        game_args.insert(0, "--user-dir".to_string());
        #[cfg(target_os = "linux")]
        {
            ensure_xdg_data_mirror(&resolved);
            env.retain(|(key, _)| key != "XDG_DATA_HOME");
            env.push(("XDG_DATA_HOME".to_string(), resolved.display().to_string()));
        }
    }

    if agent_host_launch_enabled() {
        if let Some(command) = &wrapper_command {
            env.push((
                "STS2_AGENT_LAUNCH_WRAPPER_JSON".to_string(),
                json!({
                    "command": command,
                    "args": wrapper_args
                })
                .to_string(),
            ));
        }
        return Ok(LaunchCommand {
            spawn_command: game_executable.display().to_string(),
            spawn_args: game_args.clone(),
            game_executable,
            game_args,
            wrapper_command,
            wrapper_args,
            working_dir,
            env,
        });
    }

    let (spawn_command, spawn_args) = if let Some(command) = &wrapper_command {
        let mut args = wrapper_args.clone();
        args.push(game_executable.display().to_string());
        args.extend(game_args.iter().cloned());
        (command.clone(), args)
    } else {
        (game_executable.display().to_string(), game_args.clone())
    };

    Ok(LaunchCommand {
        game_executable,
        game_args,
        wrapper_command,
        wrapper_args,
        spawn_command,
        spawn_args,
        working_dir,
        env,
    })
}

fn agent_host_launch_enabled() -> bool {
    std::env::var("STS2_AGENT_HOST_LAUNCH")
        .ok()
        .is_some_and(|value| value == "1" || value.eq_ignore_ascii_case("true"))
}

fn resolve_runtime_path(raw: &str) -> PathBuf {
    let path = PathBuf::from(raw);
    if path.is_absolute() {
        path
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| PathBuf::from(raw))
    }
}

pub(crate) fn current_launch_platform() -> LaunchPlatform {
    if cfg!(target_os = "windows") {
        LaunchPlatform::Windows
    } else if cfg!(target_os = "macos") {
        LaunchPlatform::MacOs
    } else {
        LaunchPlatform::Linux
    }
}

pub(crate) fn derive_launch_executable(
    game_path: &Path,
    platform: LaunchPlatform,
) -> Result<PathBuf, AppError> {
    match platform {
        LaunchPlatform::Linux => derive_linux_launch_executable(game_path),
        LaunchPlatform::Windows => derive_windows_launch_executable(game_path),
        LaunchPlatform::MacOs => derive_macos_launch_executable(game_path),
    }
}

fn derive_linux_launch_executable(game_path: &Path) -> Result<PathBuf, AppError> {
    for candidate in LINUX_LAUNCH_CANDIDATES {
        let path = game_path.join(candidate);
        if path.is_file() {
            return Ok(path);
        }
    }

    let mut matches = read_matching_launch_paths(game_path, |path| {
        path.extension() == Some(OsStr::new("x86_64"))
    })?;

    if matches.len() == 1 {
        return Ok(matches.remove(0));
    }

    Err(launch_executable_resolution_error(
        if matches.is_empty() {
            "launch_executable_not_found"
        } else {
            "launch_executable_ambiguous"
        },
        "game.launchExecutable",
        game_path,
        &matches,
        &LINUX_LAUNCH_CANDIDATES,
        "Linux",
    ))
}

fn derive_windows_launch_executable(game_path: &Path) -> Result<PathBuf, AppError> {
    for candidate in WINDOWS_LAUNCH_CANDIDATES {
        let path = game_path.join(candidate);
        if path.is_file() {
            return Ok(path);
        }
    }

    let mut matches = read_matching_launch_paths(game_path, |path| {
        path.extension() == Some(OsStr::new("exe"))
    })?;

    if matches.len() == 1 {
        return Ok(matches.remove(0));
    }

    Err(launch_executable_resolution_error(
        if matches.is_empty() {
            "launch_executable_not_found"
        } else {
            "launch_executable_ambiguous"
        },
        "game.launchExecutable",
        game_path,
        &matches,
        &WINDOWS_LAUNCH_CANDIDATES,
        "Windows",
    ))
}

fn derive_macos_launch_executable(game_path: &Path) -> Result<PathBuf, AppError> {
    if game_path.extension() == Some(OsStr::new("app"))
        && let Some(executable_name) = game_path.file_stem().and_then(OsStr::to_str)
    {
        let executable = game_path.join("Contents/MacOS").join(executable_name);
        if executable.is_file() {
            return Ok(executable);
        }
    }

    for (bundle_name, executable_name) in MACOS_APP_BUNDLE_CANDIDATES {
        let executable = game_path
            .join(bundle_name)
            .join("Contents/MacOS")
            .join(executable_name);
        if executable.is_file() {
            return Ok(executable);
        }
    }

    Err(launch_executable_resolution_error(
        "launch_executable_not_found",
        "game.launchExecutable",
        game_path,
        &[],
        &[
            "Slay the Spire 2.app/Contents/MacOS/Slay the Spire 2",
            "SlayTheSpire2.app/Contents/MacOS/SlayTheSpire2",
        ],
        "macOS",
    ))
}

fn read_matching_launch_paths<F>(game_path: &Path, predicate: F) -> Result<Vec<PathBuf>, AppError>
where
    F: Fn(&Path) -> bool,
{
    let mut matches = fs::read_dir(game_path)
        .map_err(|source| {
            lifecycle_error(
                2,
                "launch_executable_probe_failed",
                "game launch",
                &format!(
                    "failed to inspect '{}' for a launchable executable: {source}",
                    game_path.display()
                ),
            )
        })?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| predicate(path))
        .collect::<Vec<_>>();
    matches.sort();
    Ok(matches)
}

fn launch_executable_resolution_error(
    code: &str,
    config_key: &str,
    game_path: &Path,
    matches: &[PathBuf],
    searched_candidates: &[&str],
    platform_name: &str,
) -> AppError {
    let detected = matches
        .iter()
        .filter_map(|path| path.file_name().and_then(OsStr::to_str))
        .collect::<Vec<_>>();
    let message = match code {
        "launch_executable_ambiguous" => format!(
            "found multiple possible {platform_name} launch executables under '{}'; set game.launchExecutable explicitly",
            game_path.display(),
        ),
        _ => format!(
            "could not derive a native {platform_name} launch executable from '{}'; set game.launchExecutable explicitly",
            game_path.display(),
        ),
    };

    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": code,
                "command": "game launch",
                "configKey": config_key,
                "message": message,
                "gamePath": game_path.display().to_string(),
                "searchedCandidates": searched_candidates,
                "detectedExecutables": detected
            }
        }),
    }
}

fn rewrite_error_command(mut payload: Value, command_name: &str) -> Value {
    if let Some(error) = payload.get_mut("error").and_then(Value::as_object_mut) {
        error.insert(
            "command".to_string(),
            Value::String(command_name.to_string()),
        );
    }
    payload
}

/// Literal token callers may put in `game.deployBuildCommand` elements to
/// receive the exact directory `game deploy` will copy from. Opt-in: a command
/// that does not use it keeps working unchanged and reads the env vars instead.
const DEPLOY_OUTPUT_DIR_TOKEN: &str = "{deployOutputDir}";

struct DeployBuildRun {
    json: Value,
    started_at: SystemTime,
}

fn substitute_deploy_build_command(command: &[String], output_dir: &Path) -> Vec<String> {
    let replacement = output_dir.display().to_string();
    command
        .iter()
        .map(|element| element.replace(DEPLOY_OUTPUT_DIR_TOKEN, &replacement))
        .collect()
}

fn run_user_build_command(
    config: &AppConfig,
    project_root: &Path,
    output_dir: &Path,
) -> Result<DeployBuildRun, AppError> {
    if config.game.deploy_build_command.is_empty() {
        return Err(lifecycle_error(
            2,
            "invalid_lifecycle_config",
            "game deploy",
            "game.deployBuildCommand is required when --build is used.",
        ));
    }

    let argv = substitute_deploy_build_command(&config.game.deploy_build_command, output_dir);
    let mod_name = output_dir
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_string();
    let absolute_project_root = absolute_lifecycle_path(project_root);
    let started_at = SystemTime::now();
    crate::progress::emit(
        "game deploy",
        "build.start",
        json!({
            "command": argv,
            "outputDir": output_dir.display().to_string()
        }),
    );

    let mut command = Command::new(&argv[0]);
    command
        .args(&argv[1..])
        .current_dir(project_root)
        // The build command owns where it writes; these tell it exactly where
        // sts2 will read from afterwards so both sides cannot disagree.
        .env("SPIRECTL_DEPLOY_OUTPUT_DIR", output_dir)
        .env("SPIRECTL_DEPLOY_PROJECT_ROOT", &absolute_project_root)
        .env("SPIRECTL_DEPLOY_MOD_NAME", &mod_name);
    let output = command.output().map_err(|source| {
        lifecycle_error(
            4,
            "deploy_build_failed",
            "game deploy",
            &format!("failed to invoke deploy build command '{}': {source}", argv[0]),
        )
    })?;

    if !output.status.success() {
        return Err(command_output_error(
            "deploy_build_failed",
            "game deploy",
            &argv,
            &output,
        ));
    }
    crate::progress::emit("game deploy", "build.done", Value::Null);

    Ok(DeployBuildRun {
        json: json!({
            "command": argv,
            "configuredCommand": config.game.deploy_build_command,
            "projectRoot": absolute_project_root.display().to_string(),
            "outputDir": output_dir.display().to_string(),
            "modName": mod_name,
            "env": {
                "SPIRECTL_DEPLOY_OUTPUT_DIR": output_dir.display().to_string(),
                "SPIRECTL_DEPLOY_PROJECT_ROOT": absolute_project_root.display().to_string(),
                "SPIRECTL_DEPLOY_MOD_NAME": mod_name
            }
        }),
        started_at,
    })
}

/// Newest-file scan of the deploy output directory used by the `--build`
/// freshness gate.
struct DeployOutputScan {
    file_count: u64,
    newest: Option<(PathBuf, SystemTime)>,
}

fn scan_deploy_output(dir: &Path, scan: &mut DeployOutputScan) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            scan_deploy_output(&path, scan);
            continue;
        }
        if !file_type.is_file() {
            continue;
        }
        scan.file_count += 1;
        let Some(modified) = entry.metadata().ok().and_then(|meta| meta.modified().ok()) else {
            continue;
        };
        if scan
            .newest
            .as_ref()
            .is_none_or(|(_, current)| modified > *current)
        {
            scan.newest = Some((path, modified));
        }
    }
}

fn unix_millis(time: SystemTime) -> Option<u64> {
    time.duration_since(UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_millis() as u64)
}

/// Tolerance for build systems whose output timestamps trail the build start
/// slightly (copies that preserve mtimes, coarse filesystem clocks).
const DEPLOY_FRESHNESS_SLACK: Duration = Duration::from_secs(2);

/// After `--build`, prove the build actually produced/refreshed the directory
/// `game deploy` is about to copy. Without this, a build that wrote somewhere
/// else silently redeploys whatever stale content was left behind.
fn assert_deploy_source_fresh(
    config: &AppConfig,
    project_root: &Path,
    output_dir: &Path,
    build: &DeployBuildRun,
    allow_stale: bool,
) -> Result<Value, AppError> {
    let build_command = substitute_deploy_build_command(&config.game.deploy_build_command, output_dir);
    if !output_dir.is_dir() {
        return Err(AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "deploy_build_output_missing",
                    "command": "game deploy",
                    "message": format!(
                        "the deploy build command finished but '{}' does not exist; point the build at $SPIRECTL_DEPLOY_OUTPUT_DIR (or the {DEPLOY_OUTPUT_DIR_TOKEN} token) or fix game.deployOutputSubdir",
                        output_dir.display()
                    ),
                    "expectedOutputDir": output_dir.display().to_string(),
                    "projectRoot": absolute_lifecycle_path(project_root).display().to_string(),
                    "buildCommand": build_command,
                    "deployOutputSubdir": config.game.deploy_output_subdir
                }
            }),
        });
    }

    let mut scan = DeployOutputScan {
        file_count: 0,
        newest: None,
    };
    scan_deploy_output(output_dir, &mut scan);
    let threshold = build.started_at - DEPLOY_FRESHNESS_SLACK;
    let fresh = scan
        .newest
        .as_ref()
        .is_some_and(|(_, modified)| *modified >= threshold);
    let mut freshness = json!({
        "outputDir": output_dir.display().to_string(),
        "newestFile": scan.newest.as_ref().map(|(path, _)| path.display().to_string()),
        "newestFileUnixMs": scan.newest.as_ref().and_then(|(_, modified)| unix_millis(*modified)),
        "fileCount": scan.file_count,
        "buildStartedUnixMs": unix_millis(build.started_at),
        "fresh": fresh
    });

    if fresh {
        return Ok(freshness);
    }
    if allow_stale {
        freshness["stale"] = Value::Bool(true);
        freshness["allowedStale"] = Value::Bool(true);
        return Ok(freshness);
    }

    Err(AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "deploy_build_output_stale",
                "command": "game deploy",
                "message": format!(
                    "the deploy build command finished but nothing under '{}' was modified by it; sts2 refused to redeploy stale content (pass --allow-stale-build for a no-op incremental build)",
                    output_dir.display()
                ),
                "expectedOutputDir": output_dir.display().to_string(),
                "projectRoot": absolute_lifecycle_path(project_root).display().to_string(),
                "buildCommand": build_command,
                "deployOutputSubdir": config.game.deploy_output_subdir,
                "freshness": freshness
            }
        }),
    })
}

#[derive(Debug, Clone, Copy, Default)]
struct BridgeDeployOptions {
    /// Stage from the existing host publish output instead of rebuilding.
    no_build: bool,
    /// Copy even when the staged bridge is byte-identical to the installed one.
    force: bool,
}

const RELEASE_REPOSITORY: &str = "tfoxy/spirectl";
const RELEASE_MANIFEST_SCHEMA: &str = "spirectl-bridge-release/v1";
const RELEASE_MAX_ARCHIVE_BYTES: usize = 256 * 1024 * 1024;
const RELEASE_MAX_UNPACKED_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BridgeReleaseManifest {
    schema_version: String,
    version: String,
    archive: BridgeReleaseArchive,
}

#[derive(Debug, Deserialize)]
struct BridgeReleaseArchive {
    name: String,
    sha256: String,
}

fn deploy_bridge(
    command_name: &str,
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    options: BridgeDeployOptions,
) -> Result<Value, AppError> {
    let semver = expected_bridge_semver(command_name)?;
    let artifacts_root = absolute_artifacts_dir(config).join("live-bridge");
    let publish_dir = artifacts_root.join("host-publish");
    let stage_dir = artifacts_root.join("staging").join(DEPLOYED_MOD_DIR_NAME);
    if stage_dir.exists() {
        fs::remove_dir_all(&stage_dir).map_err(io_lifecycle_error(command_name))?;
    }
    fs::create_dir_all(&stage_dir).map_err(io_lifecycle_error(command_name))?;
    fs::create_dir_all(&layout.mods_dir).map_err(io_lifecycle_error(command_name))?;

    // Which game build this install IS, resolved before anything is staged. The
    // bridge payload is per-build, so this is what selects it: a payload built
    // for another build will load, log a few soft "not found" lines, and then
    // throw on the first lobby walk. The assemblies-dir fallback uses the `..`
    // anchor `bridge-mod/Sts2GameApi.props` compiles against, so the CLI and
    // MSBuild can never disagree about which install a build was for.
    let release_info_path = game_build::release_info_path(&layout.game_path);
    let install_release_info = game_build::read_release_info(&layout.game_path)
        .or_else(|| game_build::read_release_info_for_assemblies_dir(&layout.assemblies_dir));
    let install_lane = install_release_info
        .as_ref()
        .and_then(game_build::GameReleaseInfo::api_lane);

    let source_checkout = resolve_repo_root(command_name).ok();
    let acquisition = if !options.no_build {
        if let Some(repo_root) = source_checkout.as_deref() {
            stage_source_bridge(
                command_name,
                layout,
                repo_root,
                &publish_dir,
                &stage_dir,
                semver,
                install_release_info.as_ref(),
                true,
            )?
        } else {
            stage_release_bridge(
                command_name,
                &artifacts_root,
                &stage_dir,
                semver,
                install_lane,
                install_release_info.as_ref(),
                &release_info_path,
                true,
            )?
        }
    } else {
        match stage_release_bridge(
            command_name,
            &artifacts_root,
            &stage_dir,
            semver,
            install_lane,
            install_release_info.as_ref(),
            &release_info_path,
            source_checkout.is_none(),
        ) {
            Ok(acquisition) => acquisition,
            Err(release_error) => {
                // A checkout can still stage a previous publish without invoking
                // dotnet. This preserves offline developer iteration while a
                // packaged CLI only ever consumes a verified release archive.
                if let Some(repo_root) = source_checkout.as_deref() {
                    match stage_source_bridge(
                        command_name,
                        layout,
                        repo_root,
                        &publish_dir,
                        &stage_dir,
                        semver,
                        install_release_info.as_ref(),
                        false,
                    ) {
                        Ok(acquisition) => acquisition,
                        Err(source_error) => return Err(source_error),
                    }
                } else {
                    return Err(release_error);
                }
            }
        }
    };

    let content_hash = stage_content_hash(command_name, &stage_dir)?;
    validate_deployed_bridge_layout(command_name, &stage_dir, semver)?;

    let installed_hash = installed_bridge_content_hash(&layout.mod_dir);
    let unchanged = installed_hash.as_deref() == Some(content_hash.as_str());
    // The hash short-circuits the COPY only. The build above always ran unless
    // --no-build was passed, so this can never hide a rebuilt bridge.
    let (installed, reason) = if unchanged && !options.force {
        (false, "content_unchanged")
    } else if unchanged {
        (true, "forced")
    } else if installed_hash.is_none() {
        (true, "not_installed")
    } else {
        (true, "content_changed")
    };

    crate::progress::emit(
        command_name,
        if installed {
            "bridge.copy"
        } else {
            "bridge.copy-skipped"
        },
        json!({ "reason": reason, "contentHash": content_hash }),
    );
    if installed {
        if layout.mod_dir.exists() {
            fs::remove_dir_all(&layout.mod_dir).map_err(io_lifecycle_error(command_name))?;
        }
        copy_directory(command_name, &stage_dir, &layout.mod_dir)?;
    }
    validate_deployed_bridge_layout(command_name, &layout.mod_dir, semver)?;
    let mod_load_order = update_bridge_mod_load_order(config, command_name, layout)?;

    Ok(json!({
        "modDir": layout.mod_dir.display().to_string(),
        "modsDir": layout.mods_dir.display().to_string(),
        "endpoint": endpoint_json(&layout.endpoint),
        "socketPath": layout.endpoint.socket_path(),
        "pipeName": layout.endpoint.pipe_name(),
        "tcpAddress": layout.endpoint.tcp_address(),
        "artifactsDir": artifacts_root.display().to_string(),
        "modLoadOrder": mod_load_order,
        "version": semver,
        "installed": installed,
        "reason": reason,
        "forced": options.force,
        "build": {
            "skipped": acquisition["kind"] != "source-build",
            "publishDir": publish_dir.display().to_string()
        },
        "acquisition": acquisition,
        "buildIdentity": {
            "contentHash": content_hash,
            "installedContentHash": installed_hash
        },
        "gameBuild": {
            "releaseInfoPath": release_info_path.display().to_string(),
            "version": install_release_info.as_ref().map(|info| info.version.clone()),
            "mainAssemblyHash": install_release_info
                .as_ref()
                .and_then(|info| info.main_assembly_hash),
            "apiLane": install_lane
        }
    }))
}

#[allow(clippy::too_many_arguments)]
fn stage_source_bridge(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    repo_root: &Path,
    publish_dir: &Path,
    stage_dir: &Path,
    semver: &str,
    install_release_info: Option<&game_build::GameReleaseInfo>,
    build: bool,
) -> Result<Value, AppError> {
    // A rejected release ZIP may have left partial files in staging before a
    // source-checkout fallback is attempted. Never mix those files into the
    // trusted source payload.
    if stage_dir.exists() {
        fs::remove_dir_all(stage_dir).map_err(io_lifecycle_error(command_name))?;
    }
    fs::create_dir_all(stage_dir).map_err(io_lifecycle_error(command_name))?;
    let loader_project = repo_root
        .join("bridge-mod/src/Spirectl.BridgeMod.Sts2Host.Loader/Spirectl.BridgeMod.Sts2Host.Loader.csproj");
    let host_project = repo_root
        .join("bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj");
    let loader_output_dir = repo_root.join(format!(
        "bridge-mod/src/Spirectl.BridgeMod.Sts2Host.Loader/bin/{BUILD_CONFIGURATION}/net9.0"
    ));
    let loader_dll = loader_output_dir.join(format!("{DEPLOYED_MOD_DIR_NAME}.dll"));

    if build {
        if publish_dir.exists() {
            fs::remove_dir_all(publish_dir).map_err(io_lifecycle_error(command_name))?;
        }
        fs::create_dir_all(publish_dir).map_err(io_lifecycle_error(command_name))?;
        crate::progress::emit(command_name, "bridge.build", Value::Null);
        run_dotnet_command(
            command_name,
            &[
                "build".to_string(),
                loader_project.display().to_string(),
                "--configuration".to_string(),
                BUILD_CONFIGURATION.to_string(),
                "-m:1".to_string(),
                "-p:Sts2AssembliesDir=".to_string() + &layout.assemblies_dir.display().to_string(),
                "-p:EnableSts2LiveHost=true".to_string(),
            ],
        )?;
        crate::progress::emit(command_name, "bridge.publish", Value::Null);
        run_dotnet_command(
            command_name,
            &[
                "publish".to_string(),
                host_project.display().to_string(),
                "--configuration".to_string(),
                BUILD_CONFIGURATION.to_string(),
                "--output".to_string(),
                publish_dir.display().to_string(),
                "-m:1".to_string(),
                "-p:Sts2AssembliesDir=".to_string() + &layout.assemblies_dir.display().to_string(),
                "-p:EnableSts2LiveHost=true".to_string(),
            ],
        )?;
    }

    let missing = [
        publish_dir.join("Spirectl.BridgeMod.Sts2Host.dll"),
        loader_dll.clone(),
    ]
    .into_iter()
    .filter(|path| !path.is_file())
    .map(|path| path.display().to_string())
    .collect::<Vec<_>>();
    if !missing.is_empty() {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "bridge_publish_missing",
                    "command": command_name,
                    "message": "the source checkout has no staged bridge publish output to install",
                    "publishDir": publish_dir.display().to_string(),
                    "loaderOutputDir": loader_output_dir.display().to_string(),
                    "missing": missing
                }
            }),
        });
    }

    crate::progress::emit(command_name, "bridge.stage", Value::Null);
    copy_if_present(command_name, &loader_dll, stage_dir)?;
    copy_if_present(
        command_name,
        &loader_output_dir.join(format!("{DEPLOYED_MOD_DIR_NAME}.pdb")),
        stage_dir,
    )?;
    stage_publish_artifacts(command_name, publish_dir, stage_dir)?;
    let content_hash = stage_content_hash(command_name, stage_dir)?;
    // A source build compiled against a real install, so it can claim that
    // install's version AND its main_assembly_hash — the strong form of the
    // stamp. An install whose release_info.json is unreadable claims nothing:
    // the lane MSBuild picked is then whatever an explicit override said, and
    // guessing an identity for it would be worse than reporting unknown.
    let game_build_stamp = install_release_info
        .map(BridgeGameBuildStamp::from_install)
        .unwrap_or_else(BridgeGameBuildStamp::unknown);
    write_bridge_manifest(
        command_name,
        stage_dir,
        semver,
        &content_hash,
        &game_build_stamp,
    )?;
    Ok(json!({
        "kind": if build { "source-build" } else { "source-publish" },
        "repoRoot": repo_root.display().to_string(),
        "publishDir": publish_dir.display().to_string(),
        "sts2ApiLane": game_build_stamp.api_lane,
        "builtAgainstGame": game_build_stamp.to_json()
    }))
}

/// Released bridge payloads are per-STS2-API-lane, so the *whole* selection path
/// is lane scoped: the cache directory, the archive name and the sidecar
/// manifest name. Keyed on the bridge semver alone, `--no-build` would happily
/// install a payload built for another game build — which loads, logs a few soft
/// "not found" lines, and then throws on the first lobby walk. `install_lane` is
/// the lane the *install* needs, and there is no best-effort fallback: an
/// unresolvable lane, a lane no release covers, or a lane with no payload, is a
/// refusal.
#[allow(clippy::too_many_arguments)]
fn stage_release_bridge(
    command_name: &str,
    artifacts_root: &Path,
    stage_dir: &Path,
    semver: &str,
    install_lane: Option<&str>,
    install_release_info: Option<&game_build::GameReleaseInfo>,
    release_info_path: &Path,
    allow_download: bool,
) -> Result<Value, AppError> {
    let lane = match install_lane {
        Some(lane) => lane,
        None => return Err(release_lane_unresolved_error(
            command_name,
            install_release_info,
            release_info_path,
        )),
    };
    // A supported lane is not automatically a released one: a release is built
    // against the locked, declaration-only reference SDK, which pins one game
    // build's declarations. Say that, instead of going looking for an asset that
    // was never published and reporting a 404.
    if !game_build::is_releasable_api_lane(lane) {
        return Err(release_error(
            command_name,
            "bridge_release_lane_unreleasable",
            format!(
                "no released bridge payload exists for STS2 API lane '{lane}' (this install needs \
                 that lane); released payloads are built against the pinned STS2 reference SDK, \
                 which covers lanes {}. Build from a source checkout instead: `sts2 game \
                 install-bridge` detects the lane from the install.",
                game_build::releasable_api_lanes().join(", ")
            ),
        ));
    }
    let cache_dir = artifacts_root.join("release-cache").join(semver).join(lane);
    let archive_name = format!("spirectlbridge-v{semver}-{lane}.zip");
    let manifest_name = format!("spirectlbridge-v{semver}-{lane}.manifest.json");
    let archive_path = cache_dir.join(&archive_name);
    let manifest_path = cache_dir.join(&manifest_name);
    let tag = format!("v{semver}");
    let base_url = format!(
        "https://github.com/{RELEASE_REPOSITORY}/releases/download/{tag}"
    );

    let cached = read_release_manifest(command_name, &manifest_path, semver, &archive_name)
        .and_then(|manifest| verify_release_archive(command_name, &archive_path, &manifest))
        .map(|bytes| (bytes, "release-cache"));
    let (archive, kind) = match cached {
        Ok(value) => value,
        Err(_) => {
            if !allow_download {
                return Err(release_error(
                    command_name,
                    "bridge_release_cache_missing",
                    format!(
                        "no verified bridge release archive is cached for bridge {semver} and STS2 \
                         API lane '{lane}' (this install needs that lane); expected \
                         '{}'",
                        archive_path.display()
                    ),
                ));
            }
            fs::create_dir_all(&cache_dir).map_err(io_lifecycle_error(command_name))?;
            crate::progress::emit(
                command_name,
                "bridge.release-download",
                json!({ "tag": tag, "sts2ApiLane": lane }),
            );
            let manifest_url = format!("{base_url}/{manifest_name}");
            let archive_url = format!("{base_url}/{archive_name}");
            let manifest_bytes = download_release_asset(command_name, &manifest_url)?;
            write_atomic(command_name, &manifest_path, &manifest_bytes)?;
            let manifest = read_release_manifest(command_name, &manifest_path, semver, &archive_name)?;
            let archive_bytes = download_release_asset(command_name, &archive_url)?;
            write_atomic(command_name, &archive_path, &archive_bytes)?;
            (verify_release_archive(command_name, &archive_path, &manifest)?, "release-download")
        }
    };

    crate::progress::emit(
        command_name,
        "bridge.release-stage",
        json!({ "source": kind, "sts2ApiLane": lane }),
    );
    extract_release_bridge(command_name, &archive, stage_dir, semver)?;
    // The payload was selected by name, so check the claim inside it too: a
    // mis-named archive is exactly the failure lane scoping exists to stop.
    let packaged_lane = staged_manifest_api_lane(stage_dir);
    if packaged_lane.as_deref() != Some(lane) {
        return Err(release_error(
            command_name,
            "bridge_release_lane_mismatch",
            format!(
                "bridge release archive '{}' is named for STS2 API lane '{lane}' but its manifest \
                 claims {}",
                archive_path.display(),
                packaged_lane
                    .as_deref()
                    .map_or_else(|| "no lane at all".to_string(), |value| format!("'{value}'"))
            ),
        ));
    }
    // The archive manifest establishes the release version before extraction;
    // rewrite the deployed manifest with the same local content identity used
    // by source builds so future installs can still skip identical copies.
    //
    // A released payload compiles against the locked, declaration-only STS2
    // reference SDK. That directory has no release_info.json, so there is no
    // game version and no main_assembly_hash to record: it can claim its lane
    // and the reference package version it was built from, and nothing else.
    let content_hash = stage_content_hash(command_name, stage_dir)?;
    let game_build_stamp = BridgeGameBuildStamp::from_reference_sdk(lane, semver);
    write_bridge_manifest(
        command_name,
        stage_dir,
        semver,
        &content_hash,
        &game_build_stamp,
    )?;
    Ok(json!({
        "kind": kind,
        "tag": tag,
        "repository": RELEASE_REPOSITORY,
        "archivePath": archive_path.display().to_string(),
        "manifestPath": manifest_path.display().to_string(),
        "sts2ApiLane": lane,
        "builtAgainstGame": game_build_stamp.to_json()
    }))
}

/// The lane claimed by the `spirectlbridge.json` a release archive carried.
fn staged_manifest_api_lane(stage_dir: &Path) -> Option<String> {
    let raw = fs::read_to_string(stage_dir.join(BRIDGE_MANIFEST_NAME)).ok()?;
    let parsed = serde_json::from_str::<Value>(&raw).ok()?;
    parsed
        .pointer("/buildIdentity/sts2ApiLane")
        .and_then(Value::as_str)
        .filter(|lane| !lane.is_empty())
        .map(str::to_string)
}

/// Never install a best-effort payload. Say which file was read, what it said,
/// and which lanes exist.
fn release_lane_unresolved_error(
    command_name: &str,
    install_release_info: Option<&game_build::GameReleaseInfo>,
    release_info_path: &Path,
) -> AppError {
    let lanes = game_build::api_lanes().join(", ");
    let detail = match install_release_info {
        Some(info) => format!(
            "game build '{}' has no STS2 API lane, so no released bridge payload matches this \
             install (known lanes: {lanes})",
            info.version
        ),
        None => format!(
            "could not read a game build version from '{}', so no released bridge payload can be \
             matched to this install (known lanes: {lanes})",
            release_info_path.display()
        ),
    };
    release_error(command_name, "bridge_release_lane_unresolved", detail)
}

fn read_release_manifest(
    command_name: &str,
    path: &Path,
    semver: &str,
    archive_name: &str,
) -> Result<BridgeReleaseManifest, AppError> {
    let bytes = fs::read(path).map_err(|source| release_error(
        command_name,
        "bridge_release_cache_invalid",
        format!("failed to read cached bridge release manifest '{}': {source}", path.display()),
    ))?;
    let manifest: BridgeReleaseManifest = serde_json::from_slice(&bytes).map_err(|source| release_error(
        command_name,
        "bridge_release_manifest_invalid",
        format!("failed to parse bridge release manifest '{}': {source}", path.display()),
    ))?;
    if manifest.schema_version != RELEASE_MANIFEST_SCHEMA
        || manifest.version != semver
        || manifest.archive.name != archive_name
        || !is_sha256_hex(&manifest.archive.sha256)
    {
        return Err(release_error(
            command_name,
            "bridge_release_manifest_invalid",
            format!("bridge release manifest '{}' does not describe {archive_name} version {semver}", path.display()),
        ));
    }
    Ok(manifest)
}

fn download_release_asset(command_name: &str, url: &str) -> Result<Vec<u8>, AppError> {
    let client = reqwest::blocking::Client::builder()
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(30))
        .user_agent("spirectl-bridge-installer")
        .build()
        .map_err(|source| release_error(command_name, "bridge_release_download_failed", format!("failed to create HTTP client: {source}")))?;
    let response = client
        .get(url)
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|source| release_error(command_name, "bridge_release_download_failed", format!("failed to download bridge release asset '{url}': {source}")))?;
    let bytes = response
        .bytes()
        .map_err(|source| release_error(command_name, "bridge_release_download_failed", format!("failed to read bridge release asset '{url}': {source}")))?;
    if bytes.len() > RELEASE_MAX_ARCHIVE_BYTES {
        return Err(release_error(command_name, "bridge_release_download_failed", format!("bridge release asset '{url}' exceeds the {} byte limit", RELEASE_MAX_ARCHIVE_BYTES)));
    }
    Ok(bytes.to_vec())
}

fn verify_release_archive(
    command_name: &str,
    archive_path: &Path,
    manifest: &BridgeReleaseManifest,
) -> Result<Vec<u8>, AppError> {
    let bytes = fs::read(archive_path).map_err(|source| release_error(
        command_name,
        "bridge_release_cache_invalid",
        format!("failed to read cached bridge release archive '{}': {source}", archive_path.display()),
    ))?;
    if bytes.len() > RELEASE_MAX_ARCHIVE_BYTES {
        return Err(release_error(command_name, "bridge_release_checksum_invalid", format!("bridge release archive '{}' exceeds the {} byte limit", archive_path.display(), RELEASE_MAX_ARCHIVE_BYTES)));
    }
    let actual = sha256_hex(&bytes);
    if actual != manifest.archive.sha256 {
        return Err(release_error(
            command_name,
            "bridge_release_checksum_invalid",
            format!("bridge release archive '{}' SHA-256 does not match its release manifest", archive_path.display()),
        ));
    }
    Ok(bytes)
}

fn extract_release_bridge(
    command_name: &str,
    archive: &[u8],
    stage_dir: &Path,
    semver: &str,
) -> Result<(), AppError> {
    let mut zip = zip::ZipArchive::new(Cursor::new(archive)).map_err(|source| release_error(
        command_name,
        "bridge_release_archive_invalid",
        format!("bridge release archive is not a readable ZIP: {source}"),
    ))?;
    let mut seen = BTreeSet::new();
    let mut total = 0u64;
    for index in 0..zip.len() {
        let mut file = zip.by_index(index).map_err(|source| release_error(
            command_name,
            "bridge_release_archive_invalid",
            format!("failed to read bridge release ZIP entry: {source}"),
        ))?;
        let name = file.name().to_string();
        let enclosed = file.enclosed_name().ok_or_else(|| release_error(
            command_name,
            "bridge_release_archive_invalid",
            format!("bridge release ZIP contains unsafe path '{name}'"),
        ))?;
        let relative = enclosed.strip_prefix(DEPLOYED_MOD_DIR_NAME).map_err(|_| release_error(
            command_name,
            "bridge_release_archive_invalid",
            format!("bridge release ZIP entry '{name}' is not rooted at '{DEPLOYED_MOD_DIR_NAME}/'"),
        ))?;
        if relative.as_os_str().is_empty() {
            continue;
        }
        if file.is_dir() {
            fs::create_dir_all(stage_dir.join(relative)).map_err(io_lifecycle_error(command_name))?;
            continue;
        }
        if file.unix_mode().is_some_and(|mode| mode & 0o170000 == 0o120000) {
            return Err(release_error(command_name, "bridge_release_archive_invalid", format!("bridge release ZIP entry '{name}' is a symlink")));
        }
        if forbidden_release_bridge_file(relative) {
            return Err(release_error(
                command_name,
                "bridge_release_archive_invalid",
                format!("bridge release ZIP contains forbidden reference assembly '{name}'"),
            ));
        }
        let destination = stage_dir.join(relative);
        let key = relative.to_string_lossy().replace('\\', "/");
        if !seen.insert(key) {
            return Err(release_error(command_name, "bridge_release_archive_invalid", format!("bridge release ZIP contains duplicate entry '{name}'")));
        }
        total = total.saturating_add(file.size());
        if total > RELEASE_MAX_UNPACKED_BYTES {
            return Err(release_error(command_name, "bridge_release_archive_invalid", "bridge release ZIP exceeds the unpacked size limit".to_string()));
        }
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent).map_err(io_lifecycle_error(command_name))?;
        }
        let mut output = fs::File::create(&destination).map_err(io_lifecycle_error(command_name))?;
        std::io::copy(&mut file, &mut output).map_err(io_lifecycle_error(command_name))?;
    }
    inspect_deployed_bridge_layout(stage_dir, semver).map_err(|issue| match issue {
        BridgeLayoutIssue::VersionMismatch { expected, found } => AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "bridge_release_version_mismatch",
                    "command": command_name,
                    "message": format!("bridge release ZIP declares bridge version '{found}', expected '{expected}'"),
                    "expectedVersion": expected,
                    "foundVersion": found
                }
            }),
        },
        issue => release_error(
            command_name,
            "bridge_release_archive_invalid",
            issue.message(stage_dir),
        ),
    })
    .and_then(|_| {
        if stage_dir.join("Spirectl.Sts2.dll").is_file() {
            Ok(())
        } else {
            Err(release_error(
                command_name,
                "bridge_release_archive_invalid",
                "bridge release ZIP is missing Spirectl.Sts2.dll".to_string(),
            ))
        }
    })
}

fn write_atomic(command_name: &str, path: &Path, bytes: &[u8]) -> Result<(), AppError> {
    let temporary = path.with_extension(format!("{}.{}.tmp", path.extension().and_then(OsStr::to_str).unwrap_or("download"), std::process::id()));
    {
        let mut file = fs::File::create(&temporary).map_err(io_lifecycle_error(command_name))?;
        file.write_all(bytes).map_err(io_lifecycle_error(command_name))?;
        file.sync_all().map_err(io_lifecycle_error(command_name))?;
    }
    fs::rename(&temporary, path).map_err(io_lifecycle_error(command_name))
}

fn sha256_hex(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn is_sha256_hex(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_digit() || (byte.is_ascii_lowercase() && byte.is_ascii_hexdigit()))
}

fn forbidden_release_bridge_file(relative: &Path) -> bool {
    let Some(name) = relative.file_name().and_then(OsStr::to_str) else {
        return false;
    };
    [
        "sts2.dll",
        "slaythespire2.dll",
        "assembly-csharp.dll",
        "godotsharp.dll",
        "0harmony.dll",
        "harmonylib.dll",
        "monomod.backports.dll",
        "monomod.ilhelpers.dll",
        "sentry.dll",
        "smartformat.dll",
        "smartformat.zstring.dll",
        "steamworks.net.dll",
    ]
    .iter()
    .any(|forbidden| name.eq_ignore_ascii_case(forbidden))
}

fn release_error(command_name: &str, code: &str, message: String) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({ "error": { "code": code, "command": command_name, "message": message } }),
    }
}

/// Content identity of a staged bridge: sha256 over every staged file, in a
/// stable order, excluding the manifest (which carries the hash). Two builds of
/// unchanged sources produce the same value, which is what lets a redeploy skip
/// the copy without guessing from timestamps.
fn stage_content_hash(command_name: &str, stage_dir: &Path) -> Result<String, AppError> {
    let mut relative_paths = Vec::new();
    collect_stage_entries(command_name, stage_dir, Path::new(""), &mut relative_paths)?;
    relative_paths.sort();

    let mut hasher = Sha256::new();
    for relative in relative_paths {
        if relative == BRIDGE_MANIFEST_NAME {
            continue;
        }
        let bytes = fs::read(stage_dir.join(&relative)).map_err(io_lifecycle_error(command_name))?;
        hasher.update(relative.as_bytes());
        hasher.update([0u8]);
        hasher.update((bytes.len() as u64).to_le_bytes());
        hasher.update(&bytes);
    }
    Ok(format!("sha256:{:x}", hasher.finalize()))
}

fn collect_stage_entries(
    command_name: &str,
    root: &Path,
    relative_dir: &Path,
    out: &mut Vec<String>,
) -> Result<(), AppError> {
    for entry in fs::read_dir(root.join(relative_dir)).map_err(io_lifecycle_error(command_name))? {
        let entry = entry.map_err(io_lifecycle_error(command_name))?;
        let file_type = entry.file_type().map_err(io_lifecycle_error(command_name))?;
        let relative = relative_dir.join(entry.file_name());
        if file_type.is_dir() {
            collect_stage_entries(command_name, root, &relative, out)?;
        } else if file_type.is_file() {
            out.push(relative.to_string_lossy().replace('\\', "/"));
        }
    }
    Ok(())
}

fn installed_bridge_content_hash(mod_dir: &Path) -> Option<String> {
    let manifest = fs::read_to_string(mod_dir.join(BRIDGE_MANIFEST_NAME)).ok()?;
    serde_json::from_str::<Value>(&manifest)
        .ok()?
        .pointer("/buildIdentity/contentHash")?
        .as_str()
        .map(str::to_string)
}

fn update_bridge_mod_load_order(
    config: &AppConfig,
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
) -> Result<Value, AppError> {
    let seeded = seed_instance_user_data_if_missing(config, command_name)?;
    let dependent_mod_ids = find_installed_spirectl_sts2_consumers(command_name, &layout.mods_dir)?;
    let settings_paths = settings_save_candidates_for(config)
        .into_iter()
        .filter(|path| path.is_file())
        .collect::<Vec<_>>();

    let mut settings_reports = Vec::new();
    for settings_path in settings_paths {
        settings_reports.push(update_settings_mod_load_order(
            command_name,
            &settings_path,
            &dependent_mod_ids,
        )?);
    }

    Ok(json!({
        "searchedModsDir": layout.mods_dir.display().to_string(),
        "dependentModIds": dependent_mod_ids,
        "seededSettings": seeded,
        "settingsFiles": settings_reports,
    }))
}

fn find_installed_spirectl_sts2_consumers(
    command_name: &str,
    mods_dir: &Path,
) -> Result<Vec<String>, AppError> {
    if !mods_dir.is_dir() {
        return Ok(Vec::new());
    }

    let mut dependent_mod_ids = fs::read_dir(mods_dir)
        .map_err(io_lifecycle_error(command_name))?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .filter(|path| {
            path.file_name()
                .and_then(OsStr::to_str)
                .is_none_or(|name| name != DEPLOYED_MOD_DIR_NAME)
        })
        .filter(|path| contains_file_named(path, "Spirectl.Sts2.dll"))
        .map(|path| resolve_mod_id_from_directory(&path))
        .collect::<Vec<_>>();
    dependent_mod_ids.sort();
    dependent_mod_ids.dedup();
    Ok(dependent_mod_ids)
}

fn contains_file_named(root: &Path, file_name: &str) -> bool {
    let Ok(entries) = fs::read_dir(root) else {
        return false;
    };

    for entry in entries.filter_map(|entry| entry.ok()) {
        let path = entry.path();
        if path.is_file() && path.file_name() == Some(OsStr::new(file_name)) {
            return true;
        }
        if path.is_dir() && contains_file_named(&path, file_name) {
            return true;
        }
    }

    false
}

fn resolve_mod_id_from_directory(mod_dir: &Path) -> String {
    for manifest_path in immediate_json_files(mod_dir) {
        if let Ok(contents) = fs::read_to_string(&manifest_path)
            && let Ok(Value::Object(manifest)) = serde_json::from_str::<Value>(&contents)
            && let Some(id) = manifest.get("id").and_then(Value::as_str)
            && !id.trim().is_empty()
        {
            return id.trim().to_string();
        }
    }

    mod_dir
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default()
        .to_string()
}

fn immediate_json_files(dir: &Path) -> Vec<PathBuf> {
    let mut paths = fs::read_dir(dir)
        .ok()
        .into_iter()
        .flat_map(|entries| entries.filter_map(|entry| entry.ok()))
        .map(|entry| entry.path())
        .filter(|path| path.is_file() && path.extension() == Some(OsStr::new("json")))
        .collect::<Vec<_>>();
    paths.sort_by_key(|path| {
        let name = path
            .file_name()
            .and_then(OsStr::to_str)
            .unwrap_or_default()
            .to_ascii_lowercase();
        match name.as_str() {
            "mod_manifest.json" => (0, name),
            _ => (1, name),
        }
    });
    paths
}
