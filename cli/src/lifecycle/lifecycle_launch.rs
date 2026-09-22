pub(crate) fn execute_game_install_bridge_json(
    args: GameInstallBridgeArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    prepare_instance_for_command(context, "game install-bridge")?;
    let layout = resolve_live_bridge_layout(context.config, "game install-bridge")?;
    cache_lifecycle_paths(context, &layout, true);
    let mut result = deploy_bridge(
        "game install-bridge",
        context.config,
        &layout,
        BridgeDeployOptions {
            no_build: args.no_build,
            force: args.force,
        },
    )?;
    if let Some(instance) = context.instance {
        let _ = instance.write_record(
            &crate::instance::InstanceRunUpdate {
                game_path: Some(layout.game_path.clone()),
                ..Default::default()
            },
            "installed",
        );
    }

    if let Some(object) = result.as_object_mut() {
        if args.restart {
            let launch = execute_game_launch_explicit_relaunch_json(
                GameLaunchArgs {
                    timeout_ms: args.timeout_ms,
                    interval_ms: args.interval_ms,
                    rpc_timeout_ms: args.rpc_timeout_ms,
                    verify_stable_ms: 0,
                    no_detach_session: false,
                    disable_background_throttle: false,
                    verbose: false,
                    // Awaited once below, after the relaunch.
                    wait_quiescent_ms: 0,
                    quiescent_stable_samples: args.quiescent_stable_samples,
                    require_quiescent: false,
                    launch_args: Vec::new(),
                },
                context,
            )?;
            object.insert(
                "restart".to_string(),
                json!({ "requested": true, "launch": launch }),
            );
        }
        if let Some(quiescence) = wait_for_quiescence(
            context,
            "game install-bridge",
            args.wait_quiescent_ms,
            args.quiescent_stable_samples,
            args.require_quiescent,
        )? {
            object.insert("quiescence".to_string(), quiescence);
        }
    }

    Ok(result)
}

/// For an isolated-build instance, create/refresh the per-instance game mirror
/// (symlinked install + real executable + real `mods/`) before deploy/launch so
/// the game reads `<game_root>/mods`. No-op for shared instances or no instance.
fn prepare_instance_for_command(
    context: AppContext<'_>,
    command_name: &str,
) -> Result<(), AppError> {
    let Some(instance) = context.instance else {
        return Ok(());
    };
    // Seed the per-instance Godot user-data (the XDG_DATA_HOME target) before the
    // game boots so a fresh instance has already accepted the one-time mods
    // warning and carries the bridge-enabled mod list — otherwise STS2 skips every
    // mod, including the bridge. Applies to shared and isolated instances alike.
    seed_instance_user_data_if_missing(context.config, command_name)?;
    if !instance.isolated {
        return Ok(());
    }
    let layout = resolve_live_bridge_layout(context.config, command_name)?;
    let exe_basename = instance.launch_executable.file_name().ok_or_else(|| {
        lifecycle_error(
            2,
            "invalid_lifecycle_config",
            command_name,
            "instance launch executable path has no file name",
        )
    })?;
    crate::instance::ensure_game_mirror(&layout.game_path, &instance.game_root, exe_basename)
}

/// Persist/refresh the instance registry entry on a successful launch.
///
/// The pid lives at `launch.pid`, not at the payload root — reading the root
/// meant every record was written with `pid: null`, which is why an
/// instance-scoped `game close` could never fall back to the recorded process.
fn record_instance_running(context: AppContext<'_>, result: &Value) {
    let Some(instance) = context.instance else {
        return;
    };
    let launch = result.get("launch").unwrap_or(&Value::Null);
    let pid = launch
        .get("pid")
        .or_else(|| result.get("pid"))
        .and_then(Value::as_u64)
        .map(|value| value as u32);
    let game_pids = launch
        .get("matchedGamePids")
        .or_else(|| launch.get("matchedPids"))
        .and_then(Value::as_array)
        .map(|pids| {
            pids.iter()
                .filter_map(Value::as_u64)
                .map(|value| value as u32)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let game_path = result
        .pointer("/attachment/gamePath")
        .or_else(|| result.get("gamePath"))
        .and_then(Value::as_str)
        .map(PathBuf::from)
        .or_else(|| {
            resolve_live_bridge_layout(context.config, "game launch")
                .ok()
                .map(|layout| layout.game_path)
        });
    let stdio = launch.get("stdio").unwrap_or(&Value::Null);
    let stdio_path = |key: &str| {
        stdio
            .get(key)
            .and_then(Value::as_str)
            .map(PathBuf::from)
    };
    let _ = instance.write_record(
        &crate::instance::InstanceRunUpdate {
            pid,
            game_pids,
            game_path,
            stdout_path: stdio_path("latestStdoutPath"),
            stderr_path: stdio_path("latestStderrPath"),
        },
        "running",
    );
}

pub(crate) fn execute_game_launch_json(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_game_launch_with_args_json(
        GameLaunchArgs {
            timeout_ms: args.timeout_ms,
            interval_ms: args.interval_ms,
            rpc_timeout_ms: args.rpc_timeout_ms,
            verify_stable_ms: 0,
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
}

pub(crate) fn execute_game_launch_explicit_relaunch_json(
    args: GameLaunchArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_game_launch_with_policy_json(args, context, LaunchPolicy::ExplicitRelaunch)
}

pub(crate) fn execute_game_launch_with_args_json(
    args: GameLaunchArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_game_launch_with_policy_json(args, context, LaunchPolicy::AttachOrLaunch)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LaunchPolicy {
    AttachOrLaunch,
    ExplicitRelaunch,
}

#[derive(Debug, Clone)]
struct LaunchGameProcessObservation {
    target: LifecycleProcessTarget,
    game_executable: PathBuf,
    wrapper_command: String,
    wrapper_args: Vec<String>,
    spawn_command: String,
    spawn_args: Vec<String>,
    spawn_pid: u32,
    ever_observed: bool,
    matched_processes: Vec<DetectedGameProcess>,
}

#[derive(Debug, Clone)]
struct LaunchOutputCapture {
    dir: PathBuf,
    stdout_path: PathBuf,
    stderr_path: PathBuf,
    latest_stdout_path: PathBuf,
    latest_stderr_path: PathBuf,
    /// Only a wrapper launch reports `launch.wrapperLogs`; the same files are
    /// always reported as `launch.stdio`.
    wrapper_configured: bool,
}

impl LaunchOutputCapture {
    fn json_with_tail(&self) -> Value {
        json!({
            "stdoutPath": self.stdout_path.display().to_string(),
            "stderrPath": self.stderr_path.display().to_string(),
            "stdout": launch_output_tail_json(&self.stdout_path),
            "stderr": launch_output_tail_json(&self.stderr_path)
        })
    }

    fn json_paths(&self) -> Value {
        json!({
            "stdoutPath": self.stdout_path.display().to_string(),
            "stderrPath": self.stderr_path.display().to_string()
        })
    }

    fn stdio_json(&self) -> Value {
        json!({
            "dir": self.dir.display().to_string(),
            "stdoutPath": self.stdout_path.display().to_string(),
            "stderrPath": self.stderr_path.display().to_string(),
            "latestStdoutPath": self.latest_stdout_path.display().to_string(),
            "latestStderrPath": self.latest_stderr_path.display().to_string()
        })
    }

    fn stdio_error_json(&self) -> Value {
        let mut value = self.stdio_json();
        if let Some(object) = value.as_object_mut() {
            object.insert("stdout".to_string(), launch_output_tail_json(&self.stdout_path));
            object.insert("stderr".to_string(), launch_output_tail_json(&self.stderr_path));
        }
        value
    }
}

/// Where `game launch` writes the game's own stdout/stderr. Per-instance when an
/// instance is active so parallel instances cannot overwrite each other.
pub(crate) fn launch_output_dir(
    config: &AppConfig,
    instance: Option<&crate::instance::InstanceContext>,
) -> PathBuf {
    let dir = match instance {
        Some(instance) => instance.instance_dir.join("logs"),
        None => absolute_artifacts_dir(config).join("game-launch"),
    };
    // The configured artifacts dir is usually `./.sts2/artifacts`; keep the
    // reported path free of the `.` segment so it compares cleanly.
    normalize_lexical_path(&dir)
}

const LAUNCH_OUTPUT_LATEST_STDOUT: &str = "game.stdout.log";
const LAUNCH_OUTPUT_LATEST_STDERR: &str = "game.stderr.log";
/// Timestamped runs kept per stream before the oldest are removed.
const LAUNCH_OUTPUT_LOG_KEEP: usize = 10;

/// Paths (and existence) of the captured game stdio for `game info` /
/// `dev logs --source game-stdio`, without reading the files.
pub(crate) fn launch_stdio_json(
    config: &AppConfig,
    instance: Option<&crate::instance::InstanceContext>,
) -> Value {
    let dir = launch_output_dir(config, instance);
    let stdout_path = dir.join(LAUNCH_OUTPUT_LATEST_STDOUT);
    let stderr_path = dir.join(LAUNCH_OUTPUT_LATEST_STDERR);
    json!({
        "dir": dir.display().to_string(),
        "latestStdoutPath": stdout_path.display().to_string(),
        "latestStderrPath": stderr_path.display().to_string(),
        "stdoutExists": stdout_path.exists(),
        "stderrExists": stderr_path.exists()
    })
}

/// Same, plus the tail of each stream. Backs `dev logs --source game-stdio`,
/// which is the only way to read a crash that never reached the bridge logger.
pub(crate) fn launch_stdio_tail_json(
    config: &AppConfig,
    instance: Option<&crate::instance::InstanceContext>,
) -> Value {
    let dir = launch_output_dir(config, instance);
    let stdout_path = dir.join(LAUNCH_OUTPUT_LATEST_STDOUT);
    let stderr_path = dir.join(LAUNCH_OUTPUT_LATEST_STDERR);
    json!({
        "dir": dir.display().to_string(),
        "latestStdoutPath": stdout_path.display().to_string(),
        "latestStderrPath": stderr_path.display().to_string(),
        "stdout": launch_output_tail_json(&stdout_path),
        "stderr": launch_output_tail_json(&stderr_path)
    })
}

impl LaunchGameProcessObservation {
    fn observe(&mut self, command_name: &str) -> Result<(), AppError> {
        let matched = lifecycle_processes(&self.target, command_name)?;
        if !matched.is_empty() {
            self.ever_observed = true;
        }
        self.matched_processes = matched;
        Ok(())
    }

    fn matched_pids(&self) -> Vec<u32> {
        process_pids(&self.matched_processes)
    }

    fn matched_processes_json(&self) -> Value {
        detected_processes_json(&self.matched_processes)
    }

    fn to_launch_fields(&self) -> Value {
        json!({
            "gameProcessObserved": self.ever_observed,
            "matchedGamePids": self.matched_pids(),
            "matchedGameProcesses": self.matched_processes_json()
        })
    }
}

/// Always capture the launched game's stdio. A wrapper is not required: the
/// engine writes its startup and crash diagnostics to stderr, and discarding it
/// was the reason a mod-side failure could only be diagnosed from inside the
/// game.
fn prepare_launch_output_capture(
    config: &AppConfig,
    instance_logs_dir: Option<&Path>,
    launch: &LaunchCommand,
) -> Option<LaunchOutputCapture> {
    if agent_host_launch_enabled() {
        return None;
    }
    let dir = match instance_logs_dir {
        Some(dir) => dir.to_path_buf(),
        None => launch_output_dir(config, None),
    };
    fs::create_dir_all(&dir).ok()?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_millis())
        .unwrap_or(0);
    // Never reused: a relaunch must not truncate the log of the run that just
    // failed, which is usually the one being read.
    let prefix = format!("game-launch-{stamp}-{}", std::process::id());
    let capture = LaunchOutputCapture {
        stdout_path: dir.join(format!("{prefix}.stdout.log")),
        stderr_path: dir.join(format!("{prefix}.stderr.log")),
        latest_stdout_path: dir.join(LAUNCH_OUTPUT_LATEST_STDOUT),
        latest_stderr_path: dir.join(LAUNCH_OUTPUT_LATEST_STDERR),
        wrapper_configured: launch.wrapper_command.is_some(),
        dir,
    };
    prune_launch_output_logs(&capture.dir, LAUNCH_OUTPUT_LOG_KEEP);
    Some(capture)
}

/// Keep the newest `keep` runs per stream. Best-effort: a log directory that
/// cannot be read is not a launch failure.
fn prune_launch_output_logs(dir: &Path, keep: usize) {
    for suffix in [".stdout.log", ".stderr.log"] {
        let Ok(entries) = fs::read_dir(dir) else {
            return;
        };
        let mut runs = entries
            .filter_map(Result::ok)
            .map(|entry| entry.path())
            .filter(|path| {
                path.is_file()
                    && path
                        .file_name()
                        .and_then(OsStr::to_str)
                        .is_some_and(|name| {
                            name.starts_with("game-launch-") && name.ends_with(suffix)
                        })
            })
            .collect::<Vec<_>>();
        if runs.len() <= keep {
            continue;
        }
        // Names carry a fixed-width unix-millis stamp, so lexicographic order is
        // chronological.
        runs.sort();
        for path in runs.iter().take(runs.len() - keep) {
            let _ = fs::remove_file(path);
        }
    }
}

#[cfg(unix)]
fn refresh_launch_output_symlink(link: &Path, target: &Path) {
    let _ = fs::remove_file(link);
    let _ = std::os::unix::fs::symlink(target, link);
}

#[cfg(not(unix))]
fn refresh_launch_output_symlink(_link: &Path, _target: &Path) {}

fn configure_launch_output_stdio(command: &mut Command, capture: Option<&LaunchOutputCapture>) {
    if let Some(capture) = capture {
        let stdout = fs::File::create(&capture.stdout_path).ok();
        let stderr = fs::File::create(&capture.stderr_path).ok();
        if stdout.is_some() {
            refresh_launch_output_symlink(&capture.latest_stdout_path, &capture.stdout_path);
        }
        if stderr.is_some() {
            refresh_launch_output_symlink(&capture.latest_stderr_path, &capture.stderr_path);
        }
        command.stdout(stdout.map(Stdio::from).unwrap_or_else(Stdio::null));
        command.stderr(stderr.map(Stdio::from).unwrap_or_else(Stdio::null));
    } else {
        command.stdout(Stdio::null()).stderr(Stdio::null());
    }
}

/// `launch.wrapperLogs` keeps its exact meaning: wrapper output, only when a
/// wrapper is configured.
fn wrapper_output_capture_json(capture: &LaunchOutputCapture) -> Value {
    if !capture.wrapper_configured {
        return Value::Null;
    }
    capture.json_paths()
}

fn wrapper_output_capture_error_json(capture: Option<&LaunchOutputCapture>) -> Value {
    capture
        .filter(|capture| capture.wrapper_configured)
        .map(LaunchOutputCapture::json_with_tail)
        .unwrap_or(Value::Null)
}

fn launch_stdio_capture_error_json(capture: Option<&LaunchOutputCapture>) -> Value {
    capture
        .map(LaunchOutputCapture::stdio_error_json)
        .unwrap_or(Value::Null)
}

fn launch_output_tail_json(path: &Path) -> Value {
    const MAX_LINES: usize = 80;
    const MAX_BYTES: usize = 16 * 1024;

    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) => {
            return json!({
                "path": path.display().to_string(),
                "entries": [],
                "maxLines": MAX_LINES,
                "maxBytes": MAX_BYTES,
                "truncated": false,
                "error": error.to_string()
            });
        }
    };
    let truncated = bytes.len() > MAX_BYTES;
    let start = bytes.len().saturating_sub(MAX_BYTES);
    let text = String::from_utf8_lossy(&bytes[start..]);
    let mut lines = text.lines().map(str::to_string).collect::<Vec<_>>();
    if lines.len() > MAX_LINES {
        lines = lines[lines.len() - MAX_LINES..].to_vec();
    }
    json!({
        "path": path.display().to_string(),
        "entries": lines,
        "maxLines": MAX_LINES,
        "maxBytes": MAX_BYTES,
        "truncated": truncated
    })
}

fn launch_game_process_observation(
    launch: &LaunchCommand,
    target: &LifecycleProcessTarget,
) -> Option<LaunchGameProcessObservation> {
    if !cfg!(target_os = "linux") || agent_host_launch_enabled() {
        return None;
    }
    let wrapper_command = launch.wrapper_command.clone()?;
    Some(LaunchGameProcessObservation {
        target: target.clone(),
        game_executable: launch.game_executable.clone(),
        wrapper_command,
        wrapper_args: launch.wrapper_args.clone(),
        spawn_command: launch.spawn_command.clone(),
        spawn_args: launch.spawn_args.clone(),
        spawn_pid: 0,
        ever_observed: false,
        matched_processes: Vec::new(),
    })
}

fn attach_game_process_observation_to_launch(
    result: &mut Value,
    observation: &LaunchGameProcessObservation,
) {
    let Some(launch) = result.get_mut("launch").and_then(Value::as_object_mut) else {
        return;
    };
    let Some(fields) = observation.to_launch_fields().as_object().cloned() else {
        return;
    };
    for (key, value) in fields {
        launch.insert(key, value);
    }
}

fn execute_game_launch_with_policy_json(
    args: GameLaunchArgs,
    context: AppContext<'_>,
    policy: LaunchPolicy,
) -> Result<Value, AppError> {
    prepare_instance_for_command(context, "game launch")?;
    let result = match execute_game_launch_attempt_json(args.clone(), context, policy) {
        Ok(result) => Ok(result),
        Err(first_error) if should_retry_wrapper_launch(&first_error, context.config) => {
            retry_wrapper_launch_after_failure(args, context, policy, first_error)
        }
        Err(error) => Err(error),
    }?;
    record_instance_running(context, &result);
    Ok(result)
}

fn execute_game_launch_attempt_json(
    args: GameLaunchArgs,
    context: AppContext<'_>,
    policy: LaunchPolicy,
) -> Result<Value, AppError> {
    // Read the instance before the context is re-scoped for the RPC timeout:
    // scoped contexts intentionally drop the instance identity.
    let instance_logs_dir = context
        .instance
        .map(|instance| launch_output_dir(context.config, Some(instance)));
    // Same reason: report a socket path that had to be shortened to stay
    // bindable, so nobody goes looking under `.sts2/ipc/` for a socket that was
    // deliberately put somewhere else.
    let derived_socket_path = context
        .instance
        .filter(|instance| instance.socket_shortened)
        .and_then(|instance| instance.derived_socket.clone());
    let rpc_context = context_with_rpc_timeout(context, args.rpc_timeout_ms);
    let context = rpc_context.as_app_context(context.json_output);
    ensure_live_transport(context.config, "game launch")?;
    let layout = resolve_live_bridge_layout(context.config, "game launch")?;
    cache_lifecycle_paths(context, &layout, false);
    let mod_loadout_plan = resolve_launch_mod_loadout_plan(context.config);
    let mut effective_launch_args = args.launch_args.clone();
    if mod_loadout_plan.use_nomods_arg && !effective_launch_args.iter().any(|arg| arg == "nomods") {
        effective_launch_args.push("nomods".to_string());
    }
    let launch = resolve_launch_command(
        context.config,
        &layout,
        &effective_launch_args,
        args.disable_background_throttle,
    )?;
    let pre_launch_endpoint_cleanup = cleanup_unreachable_local_endpoint(&layout.endpoint);

    let target = resolve_launch_command_process_target(&launch, "game launch")?;
    let matching_processes = lifecycle_processes(&target, "game launch")?;
    let endpoint_reachable =
        mod_loadout_plan.should_attach_bridge && endpoint_is_reachable(&layout.endpoint);
    let pre_launch_stop = match policy {
        LaunchPolicy::AttachOrLaunch => {
            if endpoint_reachable && should_reuse_reachable_launch_endpoint(&matching_processes) {
                let attachment = wait_for_bridge(
                    &layout,
                    args.timeout_ms,
                    args.interval_ms,
                    context,
                    "game launch",
                    None,
                    None,
                    None,
                    None,
                    None,
                )?;
                let stability = verify_post_attach_stability(
                    &layout,
                    args.verify_stable_ms,
                    args.interval_ms,
                    context,
                    "game launch",
                    None,
                    None,
                )?;
                let quiescence = wait_for_quiescence(
                    context,
                    "game launch",
                    args.wait_quiescent_ms,
                    args.quiescent_stable_samples,
                    args.require_quiescent,
                )?;
                let mut result = game_launch_reused_json(
                    &layout,
                    &launch,
                    &mod_loadout_plan,
                    &matching_processes,
                    attachment,
                    stability,
                );
                attach_quiescence_to_launch(&mut result, quiescence);
                return Ok(if args.verbose {
                    result
                } else {
                    compact_game_launch_json(result)
                });
            }
            if !matching_processes.is_empty() {
                return Err(game_already_running_without_bridge_error(
                    &layout,
                    &launch,
                    &matching_processes,
                ));
            }
            None
        }
        LaunchPolicy::ExplicitRelaunch => {
            if should_stop_before_explicit_launch(endpoint_reachable, &matching_processes) {
                Some(stop_existing_game_before_launch(
                    &layout,
                    &target,
                    &matching_processes,
                    endpoint_reachable,
                    &args,
                    context,
                )?)
            } else {
                None
            }
        }
    };

    if mod_loadout_plan.should_attach_bridge {
        ensure_launch_bridge_layout(context, &layout, "game launch")?;
    }
    let steam_appid = ensure_steam_appid_file(&layout.game_path, "game launch")?;
    let launch_diagnostics = launch_diagnostics(&layout);
    let mut prepared_mod_loadout =
        prepare_launch_mod_loadout(context.config, &layout, &mod_loadout_plan, "game launch")?;

    let output_capture =
        prepare_launch_output_capture(context.config, instance_logs_dir.as_deref(), &launch);
    let mut command = Command::new(&launch.spawn_command);
    command
        .args(&launch.spawn_args)
        .current_dir(&launch.working_dir)
        .stdin(Stdio::null());
    configure_launch_output_stdio(&mut command, output_capture.as_ref());
    command.env_remove("LD_LIBRARY_PATH");
    command.env_remove("DYLD_LIBRARY_PATH");
    command.env_remove("SPIRECTL_BRIDGE_SOCKET_PATH");
    command.env_remove("SPIRECTL_BRIDGE_PIPE_NAME");
    command.env_remove("SPIRECTL_BRIDGE_TCP_ADDRESS");
    for (key, value) in &launch.env {
        command.env(key, value);
    }
    let process_isolation =
        configure_launch_process_isolation(&mut command, args.no_detach_session);
    let mut game_process_observation = launch_game_process_observation(&launch, &target);

    let steam_log_cursor = bridge::capture_sts2_log_cursor();
    let recent_log_cursor = bridge::capture_relevant_log_cursor();
    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(source) => {
            let restore = restore_prepared_mod_loadout(&mut prepared_mod_loadout, "game launch");
            let mut error = lifecycle_error(
                4,
                "launch_failed",
                "game launch",
                &format!(
                    "failed to spawn launch command '{}': {source}",
                    launch.spawn_command
                ),
            );
            attach_mod_loadout_restore_to_error(&mut error, restore);
            return Err(error);
        }
    };
    let pid = child.id();
    if let Some(observation) = game_process_observation.as_mut() {
        observation.spawn_pid = pid;
    }
    let (attachment, stability) = if mod_loadout_plan.should_attach_bridge {
        let attachment = match wait_for_bridge(
            &layout,
            args.timeout_ms,
            args.interval_ms,
            context,
            "game launch",
            Some(&mut child),
            Some(&steam_log_cursor),
            Some(&recent_log_cursor),
            game_process_observation.as_mut(),
            output_capture.as_ref(),
        ) {
            Ok(value) => value,
            Err(mut error) => {
                let restore =
                    restore_prepared_mod_loadout(&mut prepared_mod_loadout, "game launch");
                attach_mod_loadout_restore_to_error(&mut error, restore);
                return Err(error);
            }
        };
        let stability = match verify_post_attach_stability(
            &layout,
            args.verify_stable_ms,
            args.interval_ms,
            context,
            "game launch",
            Some(&mut child),
            Some(&recent_log_cursor),
        ) {
            Ok(value) => value,
            Err(mut error) => {
                let restore =
                    restore_prepared_mod_loadout(&mut prepared_mod_loadout, "game launch");
                attach_mod_loadout_restore_to_error(&mut error, restore);
                return Err(error);
            }
        };
        (attachment, stability)
    } else {
        restore_after_no_bridge_delay(context.config);
        (
            json!({
                "status": "skipped",
                "reason": "spirectlbridge_not_enabled",
                "message": "Bridge attachment was skipped because the configured game.modLoadout.enabled list does not include spirectlbridge."
            }),
            json!({
                "requestedMs": args.verify_stable_ms,
                "stable": true,
                "skipped": true,
                "reason": "bridge_attachment_skipped"
            }),
        )
    };
    let quiescence = if mod_loadout_plan.should_attach_bridge {
        match wait_for_quiescence(
            context,
            "game launch",
            args.wait_quiescent_ms,
            args.quiescent_stable_samples,
            args.require_quiescent,
        ) {
            Ok(value) => value,
            Err(mut error) => {
                let restore =
                    restore_prepared_mod_loadout(&mut prepared_mod_loadout, "game launch");
                attach_mod_loadout_restore_to_error(&mut error, restore);
                return Err(error);
            }
        }
    } else {
        None
    };
    let mod_loadout_restore =
        restore_prepared_mod_loadout(&mut prepared_mod_loadout, "game launch");
    let _mod_loadout_post_exit_restore = schedule_prepared_mod_loadout_post_exit_restore(
        &mut prepared_mod_loadout,
        pid,
        "game launch",
    );
    let child_status = child.try_wait().map_err(|source| {
        lifecycle_error(
            4,
            "launch_failed",
            "game launch",
            &format!("failed to inspect launch command state: {source}"),
        )
    })?;
    let mut result = json!({
        "launch": {
            "status": "spawned",
            "spawned": true,
            "executable": launch.spawn_command.clone(),
            "args": launch.spawn_args.clone(),
            "gameExecutable": launch.game_executable.display().to_string(),
            "gameArgs": launch.game_args.clone(),
            "wrapperCommand": launch.wrapper_command.clone(),
            "wrapperArgs": launch.wrapper_args.clone(),
            "spawnCommand": launch.spawn_command.clone(),
            "spawnArgs": launch.spawn_args.clone(),
            "workingDir": launch.working_dir.display().to_string(),
            "endpoint": endpoint_json(&layout.endpoint),
            "socketPath": layout.endpoint.socket_path(),
            "socketPathShortened": derived_socket_path.is_some(),
            "derivedSocketPath": derived_socket_path,
            "pipeName": layout.endpoint.pipe_name(),
            "tcpAddress": layout.endpoint.tcp_address(),
            "pid": pid,
            "exited": child_status.is_some(),
            "process": launch_process_json(pid, child_status.as_ref()),
            "recentLogs": recent_log_tail_json(&recent_log_cursor),
            "wrapperLogs": output_capture.as_ref().map(wrapper_output_capture_json).unwrap_or(Value::Null),
            "stdio": output_capture.as_ref().map(LaunchOutputCapture::stdio_json).unwrap_or(Value::Null),
            "processIsolation": process_isolation,
            "steamAppId": steam_appid,
            "diagnostics": launch_diagnostics,
            "preLaunchEndpointCleanup": pre_launch_endpoint_cleanup
        },
        "attachment": attachment,
        "stability": stability,
        "modLoadout": launch_mod_loadout_json(&mod_loadout_plan, prepared_mod_loadout.as_ref(), mod_loadout_restore)
    });
    if let Some(pre_launch_stop) = pre_launch_stop
        && let Some(object) = result.as_object_mut()
    {
        object.insert("preLaunchStop".to_string(), pre_launch_stop);
    }
    if let Some(observation) = game_process_observation.as_ref() {
        attach_game_process_observation_to_launch(&mut result, observation);
    }
    attach_quiescence_to_launch(&mut result, quiescence);
    Ok(if args.verbose {
        result
    } else {
        compact_game_launch_json(result)
    })
}

const WRAPPER_LAUNCH_RETRY_DELAY_MS: u64 = 8_000;

fn should_retry_wrapper_launch(error: &AppError, config: &AppConfig) -> bool {
    if config.game.launch_wrapper.is_empty() {
        return false;
    }
    matches!(
        error
            .payload
            .get("error")
            .and_then(|error| error.get("code"))
            .and_then(Value::as_str),
        Some("launch_timeout" | "launch_exited_before_ipc")
    )
}

fn retry_wrapper_launch_after_failure(
    args: GameLaunchArgs,
    context: AppContext<'_>,
    policy: LaunchPolicy,
    first_error: AppError,
) -> Result<Value, AppError> {
    let first_error_payload = first_error.payload.clone();
    let cleanup = cleanup_after_failed_wrapper_launch(&args, context);
    thread::sleep(Duration::from_millis(WRAPPER_LAUNCH_RETRY_DELAY_MS));
    match execute_game_launch_attempt_json(args, context, policy) {
        Ok(mut result) => {
            let retry = json!({
                "attempted": true,
                "reason": "wrapper-launch-timeout",
                "delayMs": WRAPPER_LAUNCH_RETRY_DELAY_MS,
                "firstErrorCode": first_error_payload["error"]["code"].clone(),
                "cleanup": cleanup
            });
            if let Some(launch) = result.get_mut("launch").and_then(Value::as_object_mut) {
                launch.insert("retry".to_string(), retry);
            }
            Ok(result)
        }
        Err(mut retry_error) => {
            if let Some(error) = retry_error
                .payload
                .get_mut("error")
                .and_then(Value::as_object_mut)
            {
                error.insert(
                    "launchRetry".to_string(),
                    json!({
                        "attempted": true,
                        "reason": "wrapper-launch-timeout",
                        "delayMs": WRAPPER_LAUNCH_RETRY_DELAY_MS,
                        "firstErrorCode": first_error_payload["error"]["code"].clone(),
                        "cleanup": cleanup
                    }),
                );
                error.insert(
                    "firstFailure".to_string(),
                    first_error_payload["error"].clone(),
                );
            }
            Err(retry_error)
        }
    }
}

fn cleanup_after_failed_wrapper_launch(args: &GameLaunchArgs, context: AppContext<'_>) -> Value {
    let wait = LifecycleWaitArgs {
        timeout_ms: args.timeout_ms,
        interval_ms: args.interval_ms,
        rpc_timeout_ms: args.rpc_timeout_ms,
    };
    let close = execute_game_close_json(wait.clone(), context);
    match close {
        Ok(close) => json!({
            "close": close,
            "kill": Value::Null
        }),
        Err(close_error) => {
            let kill = execute_game_kill_json(wait, context);
            json!({
                "closeError": close_error.payload["error"].clone(),
                "kill": match kill {
                    Ok(kill) => kill,
                    Err(kill_error) => json!({ "error": kill_error.payload["error"].clone() })
                }
            })
        }
    }
}

/// `quiescence` is omitted entirely when no budget was requested, so the
/// default launch payload keeps its exact shape.
fn attach_quiescence_to_launch(result: &mut Value, quiescence: Option<Value>) {
    if let Some(quiescence) = quiescence
        && let Some(object) = result.as_object_mut()
    {
        object.insert("quiescence".to_string(), quiescence);
    }
}

fn compact_game_launch_json(result: Value) -> Value {
    let launch = &result["launch"];
    let attachment = &result["attachment"];
    let bridge = &attachment["gameInfo"]["bridge"];
    let mut compact = json!({
        "status": launch["status"].clone(),
        "launch": {
            "status": launch["status"].clone(),
            "spawned": launch["spawned"].clone(),
            "executable": launch["executable"].clone(),
            "args": launch["args"].clone(),
            "pid": launch["pid"].clone(),
            "matchedPids": launch["matchedPids"].clone(),
            "exited": launch["exited"].clone(),
            "process": launch["process"].clone(),
            "processIsolation": launch["processIsolation"].clone(),
            "endpoint": launch["endpoint"].clone(),
            "socketPath": launch["socketPath"].clone(),
            "pipeName": launch["pipeName"].clone(),
            "tcpAddress": launch["tcpAddress"].clone(),
            "gameExecutable": launch["gameExecutable"].clone(),
            "gameArgs": launch["gameArgs"].clone(),
            "wrapperCommand": launch["wrapperCommand"].clone(),
            "wrapperArgs": launch["wrapperArgs"].clone(),
            "wrapperLogs": compact_wrapper_logs_json(&launch["wrapperLogs"]),
            "stdio": launch["stdio"].clone(),
            "spawnCommand": launch["spawnCommand"].clone(),
            "spawnArgs": launch["spawnArgs"].clone(),
            "workingDir": launch["workingDir"].clone(),
            "steamAppId": launch["steamAppId"].clone(),
            "diagnostics": launch["diagnostics"].clone(),
            "preLaunchEndpointCleanup": launch["preLaunchEndpointCleanup"].clone(),
            "notices": launch["notices"].clone()
        },
        "attachment": compact_launch_attachment_json(attachment),
        "stability": result["stability"].clone(),
        "modLoadout": result["modLoadout"].clone()
    });

    if let Some(pre_launch_stop) = result.get("preLaunchStop")
        && let Some(object) = compact.as_object_mut()
    {
        object.insert("preLaunchStop".to_string(), pre_launch_stop.clone());
    }
    if let Some(quiescence) = result.get("quiescence")
        && let Some(object) = compact.as_object_mut()
    {
        object.insert("quiescence".to_string(), quiescence.clone());
    }
    // Only when it actually happened: a socket that sits where the derivation
    // said it would needs no explaining, and two always-null keys on every
    // launch would be noise.
    if launch["socketPathShortened"] == Value::Bool(true)
        && let Some(launch_object) = compact.get_mut("launch").and_then(Value::as_object_mut)
    {
        launch_object.insert("socketPathShortened".to_string(), Value::Bool(true));
        launch_object.insert(
            "derivedSocketPath".to_string(),
            launch["derivedSocketPath"].clone(),
        );
    }
    if let Some(observed) = launch.get("gameProcessObserved")
        && let Some(launch_object) = compact.get_mut("launch").and_then(Value::as_object_mut)
    {
        launch_object.insert("gameProcessObserved".to_string(), observed.clone());
        launch_object.insert(
            "matchedGamePids".to_string(),
            launch["matchedGamePids"].clone(),
        );
        launch_object.insert(
            "matchedGameProcesses".to_string(),
            launch["matchedGameProcesses"].clone(),
        );
    }
    if let Some(retry) = launch.get("retry")
        && let Some(launch_object) = compact.get_mut("launch").and_then(Value::as_object_mut)
    {
        launch_object.insert("retry".to_string(), retry.clone());
    }

    if !bridge.is_null()
        && let Some(object) = compact.as_object_mut()
    {
        object.insert(
            "bridge".to_string(),
            json!({
                "attachmentState": bridge["attachmentState"].clone(),
                "bridgeVersion": bridge["bridgeVersion"].clone(),
                "buildIdentity": bridge["buildIdentity"].clone(),
                "schemaVersion": bridge["schemaVersion"].clone(),
                "source": bridge["source"].clone(),
                "transportKind": bridge["transportKind"].clone()
            }),
        );
    }

    compact
}

fn compact_wrapper_logs_json(logs: &Value) -> Value {
    let Some(object) = logs.as_object() else {
        return Value::Null;
    };
    json!({
        "stdoutPath": object.get("stdoutPath").cloned().unwrap_or(Value::Null),
        "stderrPath": object.get("stderrPath").cloned().unwrap_or(Value::Null)
    })
}

fn compact_launch_attachment_json(attachment: &Value) -> Value {
    if attachment["status"] == "skipped" {
        return attachment.clone();
    }

    let bridge = &attachment["gameInfo"]["bridge"];
    json!({
        "attempts": attachment["attempts"].clone(),
        "elapsedMs": attachment["elapsedMs"].clone(),
        "endpoint": attachment["endpoint"].clone(),
        "socketPath": attachment["socketPath"].clone(),
        "pipeName": attachment["pipeName"].clone(),
        "tcpAddress": attachment["tcpAddress"].clone(),
        "gamePath": attachment["gamePath"].clone(),
        "modsDir": attachment["modsDir"].clone(),
        "modDir": attachment["modDir"].clone(),
        "screen": attachment["screen"].clone(),
        "stateSummary": attachment["stateSummary"].clone(),
        "gameInfo": if bridge.is_null() {
            Value::Null
        } else {
            json!({
                "bridge": bridge.clone()
            })
        },
        "bridge": if bridge.is_null() {
            Value::Null
        } else {
            json!({
                "attachmentState": bridge["attachmentState"].clone(),
                "bridgeVersion": bridge["bridgeVersion"].clone(),
                "buildIdentity": bridge["buildIdentity"].clone(),
                "schemaVersion": bridge["schemaVersion"].clone(),
                "source": bridge["source"].clone(),
                "transportKind": bridge["transportKind"].clone()
            })
        }
    })
}

fn stop_existing_game_before_launch(
    layout: &ResolvedLiveBridgeLayout,
    target: &LifecycleProcessTarget,
    initially_matched: &[DetectedGameProcess],
    endpoint_reachable: bool,
    args: &GameLaunchArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let wait = LifecycleWaitArgs {
        timeout_ms: args.timeout_ms,
        interval_ms: args.interval_ms,
        rpc_timeout_ms: args.rpc_timeout_ms,
    };
    let mut kill = None;
    let close = match execute_game_close_json(wait.clone(), context) {
        Ok(close) => close,
        Err(error) if error.payload["error"]["code"] == "close_timeout" => {
            kill = Some(execute_game_kill_json(wait.clone(), context)?);
            error.payload["error"].clone()
        }
        Err(error) => return Err(error),
    };

    let remaining = lifecycle_processes(target, "game launch")?;
    let endpoint_wait = if remaining.is_empty() {
        wait_for_pre_launch_endpoint_release(
            &layout.endpoint,
            Duration::from_millis(args.timeout_ms),
            Duration::from_millis(args.interval_ms),
            started_at,
        )
    } else {
        json!({
            "status": "skipped",
            "reason": "process-still-running",
            "elapsedMs": started_at.elapsed().as_millis(),
            "timeoutMs": args.timeout_ms
        })
    };
    if !remaining.is_empty() || endpoint_is_reachable(&layout.endpoint) {
        return Err(pre_launch_stop_incomplete_error(
            layout,
            initially_matched,
            &remaining,
            close,
            endpoint_wait,
        ));
    }

    let mut result = json!({
        "required": true,
        "reason": pre_launch_stop_reason(endpoint_reachable, initially_matched),
        "initialMatchedPids": process_pids(initially_matched),
        "endpointWasReachable": endpoint_reachable,
        "close": close,
        "endpointWait": endpoint_wait
    });
    if let Some(kill) = kill
        && let Some(object) = result.as_object_mut()
    {
        object.insert("kill".to_string(), kill);
    }
    Ok(result)
}

fn wait_for_pre_launch_endpoint_release(
    endpoint: &LiveBridgeEndpoint,
    timeout: Duration,
    interval: Duration,
    started_at: Instant,
) -> Value {
    let mut attempts = 0_u64;
    loop {
        attempts += 1;
        if !endpoint_is_reachable(endpoint) {
            return json!({
                "status": "released",
                "attempts": attempts,
                "elapsedMs": started_at.elapsed().as_millis(),
                "timeoutMs": timeout.as_millis() as u64,
                "cleanup": cleanup_unreachable_local_endpoint(endpoint)
            });
        }
        if started_at.elapsed() >= timeout {
            return json!({
                "status": "timeout",
                "attempts": attempts,
                "elapsedMs": started_at.elapsed().as_millis(),
                "timeoutMs": timeout.as_millis() as u64,
                "cleanup": Value::Null
            });
        }
        thread::sleep(interval);
    }
}

fn pre_launch_stop_reason(
    endpoint_reachable: bool,
    matching_processes: &[DetectedGameProcess],
) -> &'static str {
    if endpoint_reachable && !matching_processes.is_empty() {
        "reachable-bridge-and-matching-process"
    } else if endpoint_reachable {
        "reachable-bridge"
    } else {
        "matching-process"
    }
}

fn pre_launch_stop_incomplete_error(
    layout: &ResolvedLiveBridgeLayout,
    initially_matched: &[DetectedGameProcess],
    remaining: &[DetectedGameProcess],
    close: Value,
    endpoint_wait: Value,
) -> AppError {
    AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "pre_launch_stop_incomplete",
                "command": "game launch",
                "message": "The existing game did not fully stop before launch, so the CLI refused to spawn a second game process.",
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "initialMatchedPids": process_pids(initially_matched),
                "remainingPids": process_pids(remaining),
                "remainingProcesses": detected_processes_json(remaining),
                "endpointStillReachable": endpoint_is_reachable(&layout.endpoint),
                "close": close,
                "endpointWait": endpoint_wait
            }
        }),
    }
}

fn game_launch_reused_json(
    layout: &ResolvedLiveBridgeLayout,
    launch: &LaunchCommand,
    mod_loadout_plan: &LaunchModLoadoutPlan,
    matching_processes: &[DetectedGameProcess],
    attachment: Value,
    stability: Value,
) -> Value {
    let notices = reused_launch_notices(launch, mod_loadout_plan);

    json!({
        "launch": {
            "status": "reused",
            "spawned": false,
            "executable": launch.spawn_command.clone(),
            "args": launch.spawn_args.clone(),
            "gameExecutable": launch.game_executable.display().to_string(),
            "gameArgs": launch.game_args.clone(),
            "wrapperCommand": launch.wrapper_command.clone(),
            "wrapperArgs": launch.wrapper_args.clone(),
            "spawnCommand": launch.spawn_command.clone(),
            "spawnArgs": launch.spawn_args.clone(),
            "workingDir": launch.working_dir.display().to_string(),
            "endpoint": endpoint_json(&layout.endpoint),
            "socketPath": layout.endpoint.socket_path(),
            "pipeName": layout.endpoint.pipe_name(),
            "tcpAddress": layout.endpoint.tcp_address(),
            "pid": matching_processes.first().map(|process| process.pid),
            "matchedPids": process_pids(matching_processes),
            "matchedProcesses": detected_processes_json(matching_processes),
            "exited": false,
            "process": matching_processes.first().map(|process| json!({
                "pid": process.pid,
                "running": true,
                "exitCode": Value::Null,
                "exitSignal": Value::Null
            })).unwrap_or(Value::Null),
            "processIsolation": Value::Null,
            "steamAppId": Value::Null,
            "diagnostics": launch_diagnostics(layout),
            "preLaunchEndpointCleanup": Value::Null,
            "notices": notices
        },
        "attachment": attachment,
        "stability": stability,
        "modLoadout": launch_mod_loadout_reused_json(mod_loadout_plan)
    })
}

fn should_reuse_reachable_launch_endpoint(matching_processes: &[DetectedGameProcess]) -> bool {
    !cfg!(target_os = "linux") || !matching_processes.is_empty()
}

fn should_stop_before_explicit_launch(
    endpoint_reachable: bool,
    matching_processes: &[DetectedGameProcess],
) -> bool {
    !matching_processes.is_empty() || (!cfg!(target_os = "linux") && endpoint_reachable)
}

fn reused_launch_notices(
    launch: &LaunchCommand,
    mod_loadout_plan: &LaunchModLoadoutPlan,
) -> Vec<Value> {
    let mut notices = Vec::new();
    if !launch.game_args.is_empty() {
        notices.push(json!({
            "code": "launch-args-not-applied",
            "message": "The existing game process was reused; requested launch arguments cannot be applied to an already running process.",
            "gameArgs": launch.game_args.clone()
        }));
    }
    if mod_loadout_plan.configured {
        notices.push(json!({
            "code": "mod-loadout-not-applied",
            "message": "The existing game process was reused; configured launch-time mod loadout changes were not applied.",
            "requestedEnabledIds": mod_loadout_plan.requested_enabled_ids.clone()
        }));
    }
    notices
}

fn launch_mod_loadout_reused_json(plan: &LaunchModLoadoutPlan) -> Value {
    if !plan.configured {
        return json!({
            "configured": false,
            "changed": false,
            "attachmentPolicy": "reused"
        });
    }

    json!({
        "configured": true,
        "mode": if plan.use_nomods_arg { "nomods" } else { "allowlist" },
        "changed": false,
        "attachmentPolicy": "reused",
        "requestedEnabledIds": plan.requested_enabled_ids.clone(),
        "notices": [{
            "code": "mod-loadout-not-applied",
            "message": "The existing game process was reused; configured launch-time mod loadout changes were not applied."
        }]
    })
}

fn game_already_running_without_bridge_error(
    layout: &ResolvedLiveBridgeLayout,
    launch: &LaunchCommand,
    matching_processes: &[DetectedGameProcess],
) -> AppError {
    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": "game_already_running_without_bridge",
                "command": "game launch",
                "message": "A matching game process is already running, but the configured live bridge endpoint is not reachable. Refusing to launch a second game process.",
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "gameExecutable": launch.game_executable.display().to_string(),
                "matchedPids": process_pids(matching_processes),
                "matchedProcesses": detected_processes_json(matching_processes),
                "nextCommands": [
                    "sts2 game attach",
                    "sts2 game close",
                    "sts2 game kill"
                ],
                "guidance": "Use game attach if the bridge should become reachable, close/kill the existing process before launching, or use an explicit restart/deploy flow that stops the game first."
            }
        }),
    }
}

#[cfg(unix)]
fn configure_launch_process_isolation(command: &mut Command, no_detach_session: bool) -> Value {
    if no_detach_session {
        return json!({
            "platform": "unix",
            "detachedSession": false,
            "mode": "inherited-session"
        });
    }

    // Detach the game from short-lived automation shells so their process-group
    // cleanup does not terminate the game after `game launch` returns.
    unsafe {
        command.pre_exec(|| {
            unsafe extern "C" {
                fn setsid() -> i32;
            }

            if setsid() == -1 {
                return Err(std::io::Error::last_os_error());
            }

            Ok(())
        });
    }

    json!({
        "platform": "unix",
        "detachedSession": true,
        "mode": "setsid"
    })
}

/// Windows counterpart of the `setsid()` detach: a new process group with no
/// inherited console, so the game survives the automation shell that started it.
#[cfg(windows)]
fn configure_launch_process_isolation(command: &mut Command, no_detach_session: bool) -> Value {
    use std::os::windows::process::CommandExt;

    const DETACHED_PROCESS: u32 = 0x0000_0008;
    const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;

    if no_detach_session {
        return json!({
            "platform": "windows",
            "detachedSession": false,
            "mode": "inherited-session"
        });
    }

    command.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);

    json!({
        "platform": "windows",
        "detachedSession": true,
        "mode": "detached-process-group"
    })
}

#[cfg(not(any(unix, windows)))]
fn configure_launch_process_isolation(_command: &mut Command, no_detach_session: bool) -> Value {
    json!({
        "platform": "unsupported",
        "detachedSession": false,
        "mode": if no_detach_session { "unsupported-explicit-inherited-session" } else { "unsupported" }
    })
}

#[derive(Debug, Clone)]
struct LaunchModLoadoutPlan {
    configured: bool,
    requested_enabled_ids: Vec<String>,
    use_nomods_arg: bool,
    should_attach_bridge: bool,
}

#[derive(Debug, Clone)]
struct PreparedModLoadout {
    settings_path: PathBuf,
    backup_path: Option<PathBuf>,
    temporary: bool,
    changed: bool,
    requested_enabled_ids: Vec<String>,
    effective_enabled_ids: Vec<String>,
    disabled_ids: Vec<String>,
    restore: Option<Value>,
    post_exit_restore: Option<Value>,
}

#[derive(Debug, Clone)]
struct SavedModEntry {
    id: String,
    enabled: bool,
    source: String,
}

fn resolve_launch_mod_loadout_plan(config: &AppConfig) -> LaunchModLoadoutPlan {
    let Some(enabled) = config.game.mod_loadout.enabled.as_ref() else {
        return LaunchModLoadoutPlan {
            configured: false,
            requested_enabled_ids: Vec::new(),
            use_nomods_arg: false,
            should_attach_bridge: true,
        };
    };

    let requested_enabled_ids = normalized_mod_ids(enabled);
    let should_attach_bridge = requested_enabled_ids
        .iter()
        .any(|id| id == DEPLOYED_MOD_DIR_NAME);

    LaunchModLoadoutPlan {
        configured: true,
        use_nomods_arg: requested_enabled_ids.is_empty(),
        requested_enabled_ids,
        should_attach_bridge,
    }
}

fn normalized_mod_ids(ids: &[String]) -> Vec<String> {
    let mut seen = BTreeSet::new();
    let mut normalized = Vec::new();
    for id in ids {
        let trimmed = id.trim();
        if trimmed.is_empty() || !seen.insert(trimmed.to_string()) {
            continue;
        }
        normalized.push(trimmed.to_string());
    }
    normalized
}

fn prepare_launch_mod_loadout(
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    plan: &LaunchModLoadoutPlan,
    command_name: &str,
) -> Result<Option<PreparedModLoadout>, AppError> {
    if !plan.configured || plan.use_nomods_arg {
        return Ok(None);
    }

    let settings_path = resolve_mod_settings_file(config, None, command_name)?;
    let mut settings = read_settings_save_json(&settings_path, command_name)?;
    let installed_mods = enumerate_installed_mod_entries(command_name, &layout.mods_dir)?;
    let rewrite = rewrite_settings_mod_allowlist(
        &mut settings,
        &installed_mods,
        &plan.requested_enabled_ids,
        command_name,
        &settings_path,
    )?;

    let backup_path = if rewrite.changed {
        let backup = backup_settings_save(command_name, &settings_path)?;
        write_settings_save_json(command_name, &settings_path, &settings)?;
        Some(backup)
    } else {
        None
    };

    Ok(Some(PreparedModLoadout {
        settings_path,
        backup_path,
        temporary: config.game.mod_loadout.temporary,
        changed: rewrite.changed,
        requested_enabled_ids: plan.requested_enabled_ids.clone(),
        effective_enabled_ids: rewrite.effective_enabled_ids,
        disabled_ids: rewrite.disabled_ids,
        restore: None,
        post_exit_restore: None,
    }))
}

fn restore_after_no_bridge_delay(config: &AppConfig) {
    let delay = config.game.mod_loadout.restore_after_no_bridge_ms;
    if delay > 0 && config.game.mod_loadout.temporary {
        thread::sleep(Duration::from_millis(delay));
    }
}

fn restore_prepared_mod_loadout(
    prepared: &mut Option<PreparedModLoadout>,
    command_name: &str,
) -> Value {
    let Some(prepared) = prepared.as_mut() else {
        return Value::Null;
    };
    if prepared.restore.is_some() {
        return prepared.restore.clone().unwrap_or(Value::Null);
    }

    if !prepared.temporary {
        let restore = json!({
            "status": "not_requested",
            "settingsFile": prepared.settings_path.display().to_string()
        });
        prepared.restore = Some(restore.clone());
        return restore;
    }

    let restore = if let Some(backup_path) = prepared.backup_path.as_ref() {
        match fs::copy(backup_path, &prepared.settings_path) {
            Ok(_) => json!({
                "status": "restored",
                "settingsFile": prepared.settings_path.display().to_string(),
                "backupPath": backup_path.display().to_string()
            }),
            Err(source) => json!({
                "status": "failed",
                "settingsFile": prepared.settings_path.display().to_string(),
                "backupPath": backup_path.display().to_string(),
                "error": source.to_string()
            }),
        }
    } else {
        json!({
            "status": "not_needed",
            "settingsFile": prepared.settings_path.display().to_string()
        })
    };

    if !command_name.is_empty() {
        prepared.restore = Some(restore.clone());
    }
    restore
}

fn attach_mod_loadout_restore_to_error(error: &mut AppError, restore: Value) {
    if restore.is_null() {
        return;
    }
    if let Some(object) = error.payload.as_object_mut() {
        object.insert("modLoadoutRestore".to_string(), restore);
    }
}

fn schedule_prepared_mod_loadout_post_exit_restore(
    prepared: &mut Option<PreparedModLoadout>,
    pid: u32,
    command_name: &str,
) -> Value {
    let Some(prepared) = prepared.as_mut() else {
        return Value::Null;
    };
    if let Some(existing) = prepared.post_exit_restore.as_ref() {
        return existing.clone();
    }

    let post_exit_restore = schedule_mod_loadout_post_exit_restore(prepared, pid, command_name);
    prepared.post_exit_restore = Some(post_exit_restore.clone());
    post_exit_restore
}

#[cfg(unix)]
fn schedule_mod_loadout_post_exit_restore(
    prepared: &PreparedModLoadout,
    pid: u32,
    _command_name: &str,
) -> Value {
    if !prepared.temporary {
        return json!({
            "status": "not_requested",
            "reason": "temporary_disabled",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    }
    if !prepared.changed {
        return json!({
            "status": "not_needed",
            "reason": "settings_unchanged",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    }
    let Some(backup_path) = prepared.backup_path.as_ref() else {
        return json!({
            "status": "not_needed",
            "reason": "no_backup",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    };

    let script = r#"while kill -0 "$1" 2>/dev/null; do
  sleep 1
done
if [ -f "$2" ]; then
  cp "$2" "$3"
fi
"#;
    let mut command = Command::new("sh");
    command
        .arg("-c")
        .arg(script)
        .arg("spirectl-mod-loadout-post-exit-restore")
        .arg(pid.to_string())
        .arg(backup_path)
        .arg(&prepared.settings_path)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    unsafe {
        command.pre_exec(|| {
            unsafe extern "C" {
                fn setsid() -> i32;
            }

            if setsid() == -1 {
                return Err(std::io::Error::last_os_error());
            }

            Ok(())
        });
    }

    match command.spawn() {
        Ok(child) => json!({
            "status": "scheduled",
            "pid": pid,
            "watcherPid": child.id(),
            "settingsFile": prepared.settings_path.display().to_string(),
            "backupPath": backup_path.display().to_string()
        }),
        Err(source) => json!({
            "status": "failed",
            "pid": pid,
            "settingsFile": prepared.settings_path.display().to_string(),
            "backupPath": backup_path.display().to_string(),
            "error": source.to_string()
        }),
    }
}

/// Windows counterpart of the detached `sh` watcher: a background PowerShell
/// that waits for the game to exit and then puts the saved `settings.save` back.
#[cfg(windows)]
fn schedule_mod_loadout_post_exit_restore(
    prepared: &PreparedModLoadout,
    pid: u32,
    _command_name: &str,
) -> Value {
    use std::os::windows::process::CommandExt;

    const DETACHED_PROCESS: u32 = 0x0000_0008;
    const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;

    if !prepared.temporary {
        return json!({
            "status": "not_requested",
            "reason": "temporary_disabled",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    }
    if !prepared.changed {
        return json!({
            "status": "not_needed",
            "reason": "settings_unchanged",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    }
    let Some(backup_path) = prepared.backup_path.as_ref() else {
        return json!({
            "status": "not_needed",
            "reason": "no_backup",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    };

    // Paths are passed through PowerShell variables (single-quoted, with '
    // doubled) so spaces and quotes in an install path cannot break the script.
    let script = format!(
        "$backup = '{}'; $settings = '{}'; Wait-Process -Id {} -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $backup) {{ Copy-Item -LiteralPath $backup -Destination $settings -Force }}",
        powershell_single_quote(&backup_path.display().to_string()),
        powershell_single_quote(&prepared.settings_path.display().to_string()),
        pid
    );

    let mut command = Command::new("powershell");
    command
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);

    match command.spawn() {
        Ok(child) => json!({
            "status": "scheduled",
            "pid": pid,
            "watcherPid": child.id(),
            "settingsFile": prepared.settings_path.display().to_string(),
            "backupPath": backup_path.display().to_string()
        }),
        Err(source) => json!({
            "status": "failed",
            "pid": pid,
            "settingsFile": prepared.settings_path.display().to_string(),
            "backupPath": backup_path.display().to_string(),
            "error": source.to_string()
        }),
    }
}

/// Escape a value for a PowerShell single-quoted string literal.
#[cfg(windows)]
fn powershell_single_quote(value: &str) -> String {
    value.replace('\'', "''")
}

#[cfg(not(any(unix, windows)))]
fn schedule_mod_loadout_post_exit_restore(
    prepared: &PreparedModLoadout,
    pid: u32,
    _command_name: &str,
) -> Value {
    if !prepared.temporary || !prepared.changed || prepared.backup_path.is_none() {
        return json!({
            "status": "not_needed",
            "settingsFile": prepared.settings_path.display().to_string()
        });
    }

    json!({
        "status": "unsupported",
        "pid": pid,
        "settingsFile": prepared.settings_path.display().to_string(),
        "backupPath": prepared.backup_path.as_ref().map(|path| path.display().to_string()),
        "message": "Post-exit temporary mod loadout restore is not supported on this platform."
    })
}

fn launch_mod_loadout_json(
    plan: &LaunchModLoadoutPlan,
    prepared: Option<&PreparedModLoadout>,
    restore: Value,
) -> Value {
    if !plan.configured {
        return json!({
            "configured": false,
            "changed": false,
            "attachmentPolicy": "default"
        });
    }

    if plan.use_nomods_arg {
        return json!({
            "configured": true,
            "mode": "nomods",
            "changed": false,
            "launchArg": "nomods",
            "attachmentPolicy": "skipped",
            "requestedEnabledIds": []
        });
    }

    let Some(prepared) = prepared else {
        return json!({
            "configured": true,
            "mode": "allowlist",
            "changed": false,
            "attachmentPolicy": if plan.should_attach_bridge { "attach" } else { "skipped" },
            "requestedEnabledIds": plan.requested_enabled_ids
        });
    };

    json!({
        "configured": true,
        "mode": "allowlist",
        "settingsFile": prepared.settings_path.display().to_string(),
        "backupPath": prepared.backup_path.as_ref().map(|path| path.display().to_string()),
        "temporary": prepared.temporary,
        "changed": prepared.changed,
        "attachmentPolicy": if plan.should_attach_bridge { "attach" } else { "skipped" },
        "requestedEnabledIds": prepared.requested_enabled_ids,
        "effectiveEnabledIds": prepared.effective_enabled_ids,
        "disabledIds": prepared.disabled_ids,
        "restore": restore,
        "postExitRestore": prepared.post_exit_restore.clone().unwrap_or(Value::Null)
    })
}

#[derive(Debug)]
struct ModSettingsRewrite {
    changed: bool,
    effective_enabled_ids: Vec<String>,
    disabled_ids: Vec<String>,
}

fn rewrite_settings_mod_allowlist(
    settings: &mut Value,
    installed_mods: &BTreeMap<String, SavedModEntry>,
    requested_enabled_ids: &[String],
    command_name: &str,
    settings_path: &Path,
) -> Result<ModSettingsRewrite, AppError> {
    let requested = requested_enabled_ids
        .iter()
        .cloned()
        .collect::<BTreeSet<_>>();
    let existing_entries = read_mod_entries_from_settings(settings, command_name, settings_path)?;
    let existing_by_id = existing_entries
        .iter()
        .map(|entry| (entry.id.clone(), entry.clone()))
        .collect::<BTreeMap<_, _>>();
    let known_ids = existing_by_id
        .keys()
        .chain(installed_mods.keys())
        .cloned()
        .collect::<BTreeSet<_>>();
    let unknown = requested
        .iter()
        .filter(|id| !known_ids.contains(*id))
        .cloned()
        .collect::<Vec<_>>();
    if !unknown.is_empty() {
        return Err(lifecycle_error(
            2,
            "unknown_mod_loadout_id",
            command_name,
            &format!(
                "game.modLoadout.enabled contains unknown mod id(s): {}",
                unknown.join(", ")
            ),
        ));
    }

    let mut ordered_ids = existing_entries
        .iter()
        .map(|entry| entry.id.clone())
        .collect::<Vec<_>>();
    for id in installed_mods.keys() {
        if !ordered_ids.iter().any(|existing| existing == id) {
            ordered_ids.push(id.clone());
        }
    }

    let before = settings.clone();
    let mut effective_enabled_ids = Vec::new();
    let mut disabled_ids = Vec::new();
    let mut new_mod_list = Vec::new();
    for id in ordered_ids {
        let template = existing_by_id
            .get(&id)
            .or_else(|| installed_mods.get(&id))
            .cloned()
            .unwrap_or_else(|| SavedModEntry {
                id: id.clone(),
                enabled: true,
                source: "mods_directory".to_string(),
            });
        let enabled = requested.contains(&id);
        if enabled {
            effective_enabled_ids.push(id.clone());
        } else {
            disabled_ids.push(id.clone());
        }
        new_mod_list.push(json!({
            "id": id,
            "is_enabled": enabled,
            "source": template.source
        }));
    }

    let mod_settings = settings
        .get_mut("mod_settings")
        .and_then(Value::as_object_mut)
        .ok_or_else(|| {
            settings_save_shape_error(command_name, settings_path, "mod_settings_missing")
        })?;
    mod_settings.insert("mods_enabled".to_string(), Value::Bool(true));
    mod_settings.insert("mod_list".to_string(), Value::Array(new_mod_list));

    Ok(ModSettingsRewrite {
        changed: *settings != before,
        effective_enabled_ids,
        disabled_ids,
    })
}

fn settings_save_shape_error(command_name: &str, settings_path: &Path, reason: &str) -> AppError {
    lifecycle_error(
        4,
        "settings_save_mod_settings_missing",
        command_name,
        &format!(
            "STS2 settings file '{}' does not contain a usable mod_settings.mod_list ({reason}).",
            settings_path.display()
        ),
    )
}
