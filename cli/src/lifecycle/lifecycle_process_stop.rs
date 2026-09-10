fn resolve_deploy_project_root(path: &Path) -> Result<PathBuf, AppError> {
    if !path.exists() {
        return Err(lifecycle_error(
            2,
            "deploy_source_missing",
            "game deploy",
            &format!("deploy path '{}' does not exist", path.display()),
        ));
    }
    if !path.is_dir() {
        return Err(lifecycle_error(
            2,
            "deploy_source_missing",
            "game deploy",
            &format!("deploy path '{}' must be a directory", path.display()),
        ));
    }

    Ok(path.to_path_buf())
}

/// The directory `game deploy` will copy into the mods folder, whether or not it
/// exists yet. Pure so `--build` can hand the same absolute path to the user's
/// build command *before* the build runs, and check it afterwards.
fn expected_mod_deploy_source(config: &AppConfig, project_root: &Path) -> PathBuf {
    let source = if let Some(subdir) = config.game.deploy_output_subdir.as_deref() {
        project_root.join(subdir)
    } else {
        project_root.to_path_buf()
    };
    normalize_lexical_path(&absolute_lifecycle_path(&source))
}

/// Make a path absolute against the CWD without requiring it to exist
/// (`fs::canonicalize` fails on missing paths, which is exactly the case the
/// freshness gate has to report).
fn absolute_lifecycle_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

/// Collapse `.` / `..` segments lexically so a configured
/// `deployOutputSubdir: ../../dist/mod` yields the directory name the mod is
/// deployed under. Symlinks are not resolved (the path may not exist yet).
fn normalize_lexical_path(path: &Path) -> PathBuf {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                if !normalized.pop() {
                    normalized.push(component.as_os_str());
                }
            }
            other => normalized.push(other.as_os_str()),
        }
    }
    normalized
}

fn resolve_mod_deploy_source(config: &AppConfig, project_root: &Path) -> Result<PathBuf, AppError> {
    let source = expected_mod_deploy_source(config, project_root);

    if !source.is_dir() {
        return Err(lifecycle_error(
            2,
            "deploy_source_missing",
            "game deploy",
            &format!(
                "deploy source '{}' does not exist; set game.deployOutputSubdir correctly or point game deploy at a deployable mod directory",
                source.display()
            ),
        ));
    }

    Ok(source)
}

fn deployable_mod_name(source: &Path) -> Result<String, AppError> {
    source
        .file_name()
        .and_then(OsStr::to_str)
        .map(|value| value.to_string())
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| {
            lifecycle_error(
                2,
                "deploy_source_missing",
                "game deploy",
                &format!(
                    "could not derive a deployable mod name from '{}'",
                    source.display()
                ),
            )
        })
}

fn copy_directory(command_name: &str, source: &Path, target: &Path) -> Result<(), AppError> {
    if target.exists() {
        fs::remove_dir_all(target).map_err(io_lifecycle_error(command_name))?;
    }
    fs::create_dir_all(target).map_err(io_lifecycle_error(command_name))?;

    for entry in fs::read_dir(source).map_err(io_lifecycle_error(command_name))? {
        let entry = entry.map_err(io_lifecycle_error(command_name))?;
        let source_path = entry.path();
        let target_path = target.join(entry.file_name());
        let file_type = entry
            .file_type()
            .map_err(io_lifecycle_error(command_name))?;
        if file_type.is_dir() {
            copy_directory(command_name, &source_path, &target_path)?;
        } else if file_type.is_file() {
            fs::copy(&source_path, &target_path).map_err(io_lifecycle_error(command_name))?;
        }
    }

    Ok(())
}

fn stop_running_game(
    config: &AppConfig,
    layout: &ResolvedLiveBridgeLayout,
    instance: Option<&crate::instance::InstanceContext>,
) -> Result<Vec<u32>, AppError> {
    if !config.game.stop_command.is_empty() {
        let mut command = Command::new(&config.game.stop_command[0]);
        command.args(&config.game.stop_command[1..]);
        let output = command.output().map_err(|source| {
            lifecycle_error(
                4,
                "stop_command_failed",
                "game deploy",
                &format!(
                    "failed to invoke stop command '{}': {source}",
                    config.game.stop_command[0]
                ),
            )
        })?;
        if !output.status.success() {
            return Err(command_output_error(
                "stop_command_failed",
                "game deploy",
                &config.game.stop_command,
                &output,
            ));
        }
        return Ok(Vec::new());
    }

    #[cfg(any(target_os = "linux", windows))]
    {
        let target = resolve_lifecycle_process_target(config, layout, "game deploy", instance)?;
        let matches = lifecycle_processes(&target, "game deploy")?;
        terminate_processes("game deploy", &matches, false)?;
        thread::sleep(Duration::from_millis(250));
        let remaining = lifecycle_processes(&target, "game deploy")?;
        terminate_processes("game deploy", &remaining, true)?;
        return Ok(matches.into_iter().map(|process| process.pid).collect());
    }

    #[allow(unreachable_code)]
    Err(AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_lifecycle_config",
                "command": "game deploy",
                "configKey": "game.stopCommand",
                "message": format!(
                    "automatic restart on {} requires game.stopCommand; set it to stop the running game before redeploying.",
                    restart_stop_platform_name()
                ),
                "platform": restart_stop_platform_name()
            }
        }),
    })
}

fn execute_configured_restart_stop(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
    layout: &ResolvedLiveBridgeLayout,
    command_name: &str,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let output = Command::new(&context.config.game.stop_command[0])
        .args(&context.config.game.stop_command[1..])
        .output()
        .map_err(|source| {
            lifecycle_error(
                4,
                "configured_stop_command_failed",
                command_name,
                &format!(
                    "failed to invoke stop command '{}': {source}",
                    context.config.game.stop_command[0]
                ),
            )
        })?;
    if !output.status.success() {
        return Err(command_output_error(
            "configured_stop_command_failed",
            command_name,
            &context.config.game.stop_command,
            &output,
        ));
    }

    let target =
        resolve_lifecycle_process_target(context.config, layout, command_name, context.instance)
            .ok();
    let wait = wait_for_lifecycle_stop(
        target.as_ref(),
        None,
        command_name,
        "configured-restart",
        &[],
        &[],
        &layout.endpoint,
        Duration::from_millis(args.timeout_ms),
        Duration::from_millis(args.interval_ms),
        started_at,
        Vec::new(),
    )?;

    Ok(json!({
        "command": command_name,
        "strategy": "configured-restart",
        "matchedPids": [],
        "signaledPids": [],
        "forcedPids": [],
        "remainingPids": process_pids(&wait.remaining),
        "endpoint": endpoint_json(&layout.endpoint),
        "elapsedMs": started_at.elapsed().as_millis(),
        "timeoutMs": args.timeout_ms,
        "success": true,
        "bridgeEndpointDisappeared": wait.endpoint_disappeared,
        "notices": []
    }))
}

#[cfg(any(target_os = "linux", windows))]
fn execute_restart_stop_for_target(
    target: &LifecycleProcessTarget,
    endpoint: &LiveBridgeEndpoint,
    args: LifecycleWaitArgs,
    command_name: &str,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let matched = lifecycle_processes(target, command_name)?;
    if matched.is_empty() {
        return Ok(json!({
            "command": command_name,
            "strategy": "platform-restart",
            "matchedPids": [],
            "signaledPids": [],
            "forcedPids": [],
            "remainingPids": [],
            "endpoint": endpoint_json(endpoint),
            "elapsedMs": started_at.elapsed().as_millis(),
            "timeoutMs": args.timeout_ms,
            "success": true,
            "bridgeEndpointDisappeared": !endpoint_is_reachable(endpoint),
            "notices": [{
                "code": "no-process-found",
                "message": "No running game process matched the configured launch executable before restart."
            }]
        }));
    }

    terminate_processes(command_name, &matched, false)?;
    thread::sleep(Duration::from_millis(250));

    let after_grace = lifecycle_processes(target, command_name)?;
    terminate_processes(command_name, &after_grace, true)?;

    let wait = wait_for_lifecycle_stop(
        Some(target),
        None,
        command_name,
        "platform-restart",
        &matched,
        &matched,
        endpoint,
        Duration::from_millis(args.timeout_ms),
        Duration::from_millis(args.interval_ms),
        started_at,
        Vec::new(),
    )?;

    Ok(json!({
        "command": command_name,
        "strategy": "platform-restart",
        "matchedPids": process_pids(&matched),
        "signaledPids": process_pids(&matched),
        "forcedPids": process_pids(&after_grace),
        "remainingPids": process_pids(&wait.remaining),
        "wrapperProcesses": wrapper_stop_json(&[], &[], &wait.wrapper_remaining),
        "endpoint": endpoint_json(endpoint),
        "elapsedMs": started_at.elapsed().as_millis(),
        "timeoutMs": args.timeout_ms,
        "success": true,
        "bridgeEndpointDisappeared": wait.endpoint_disappeared,
        "notices": []
    }))
}

fn restart_stop_platform_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "Windows"
    } else if cfg!(target_os = "macos") {
        "macOS"
    } else {
        "Linux"
    }
}

#[derive(Debug)]
struct LifecycleWaitResult {
    remaining: Vec<DetectedGameProcess>,
    wrapper_remaining: Vec<DetectedWrapperProcess>,
    endpoint_disappeared: bool,
}

/// Whether this host can find and stop the game process itself.
///
/// When false, every stop path needs an explicit `game.stopCommand`.
pub(crate) fn platform_process_control_available() -> bool {
    cfg!(any(target_os = "linux", windows))
}

fn lifecycle_processes(
    target: &LifecycleProcessTarget,
    command_name: &str,
) -> Result<Vec<DetectedGameProcess>, AppError> {
    #[cfg(target_os = "linux")]
    {
        matching_linux_processes(target, command_name)
    }

    #[cfg(windows)]
    {
        windows_process::matching_windows_processes(target, command_name)
    }

    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = target;
        let _ = command_name;
        Ok(Vec::new())
    }
}

fn lifecycle_wrapper_processes(
    target: &LifecycleWrapperTarget,
    command_name: &str,
) -> Result<Vec<DetectedWrapperProcess>, AppError> {
    #[cfg(target_os = "linux")]
    {
        matching_linux_wrapper_processes(target, command_name)
    }

    #[cfg(windows)]
    {
        windows_process::matching_windows_wrapper_processes(target, command_name)
    }

    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = target;
        let _ = command_name;
        Ok(Vec::new())
    }
}

/// Ask the matched game processes to stop, or force them when `force`.
///
/// Linux signals TERM/KILL; Windows posts a close through `taskkill /T` and
/// escalates with `/F`. Both are no-ops for an empty match list.
fn terminate_processes(
    command_name: &str,
    processes: &[DetectedGameProcess],
    force: bool,
) -> Result<(), AppError> {
    #[cfg(target_os = "linux")]
    {
        signal_linux_processes(command_name, if force { "KILL" } else { "TERM" }, processes)
    }

    #[cfg(windows)]
    {
        windows_process::taskkill_processes(command_name, processes, force)
    }

    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = (command_name, processes, force);
        Ok(())
    }
}

fn terminate_wrapper_processes(
    command_name: &str,
    processes: &[DetectedWrapperProcess],
    force: bool,
) -> Result<(), AppError> {
    #[cfg(target_os = "linux")]
    {
        signal_linux_wrapper_processes(command_name, if force { "KILL" } else { "TERM" }, processes)
    }

    #[cfg(windows)]
    {
        windows_process::taskkill_wrapper_processes(command_name, processes, force)
    }

    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = (command_name, processes, force);
        Ok(())
    }
}

#[allow(
    clippy::too_many_arguments,
    reason = "Lifecycle stop reporting keeps process metadata and notices explicit."
)]
fn wait_for_lifecycle_stop(
    target: Option<&LifecycleProcessTarget>,
    wrapper_target: Option<&LifecycleWrapperTarget>,
    command_name: &str,
    strategy: &str,
    matched: &[DetectedGameProcess],
    stopped: &[DetectedGameProcess],
    endpoint: &LiveBridgeEndpoint,
    timeout: Duration,
    interval: Duration,
    started_at: Instant,
    notices: Vec<Value>,
) -> Result<LifecycleWaitResult, AppError> {
    let process_tracking_available =
        (target.is_some() || wrapper_target.is_some()) && platform_process_control_available();
    let mut samples = 0u64;
    loop {
        samples += 1;
        let remaining = match target {
            Some(target) => lifecycle_processes(target, command_name)?,
            None => Vec::new(),
        };
        let wrapper_remaining = match wrapper_target {
            Some(target) => lifecycle_wrapper_processes(target, command_name)?,
            None => Vec::new(),
        };
        let endpoint_disappeared = !endpoint_is_reachable(endpoint);
        crate::progress::emit(
            command_name,
            "stop.wait",
            json!({
                "samples": samples,
                "strategy": strategy,
                "remainingPids": process_pids(&remaining),
                "endpointDisappeared": endpoint_disappeared
            }),
        );
        if (process_tracking_available && remaining.is_empty() && wrapper_remaining.is_empty())
            || (!process_tracking_available && endpoint_disappeared)
        {
            return Ok(LifecycleWaitResult {
                remaining,
                wrapper_remaining,
                endpoint_disappeared,
            });
        }
        if started_at.elapsed() >= timeout {
            return Err(lifecycle_stop_error(
                5,
                "close_timeout",
                command_name,
                strategy,
                matched,
                stopped,
                &remaining,
                wrapper_target,
                &wrapper_remaining,
                endpoint,
                started_at.elapsed().as_millis(),
                timeout.as_millis() as u64,
                notices,
                "Timed out waiting for the game/wrapper process or bridge endpoint to stop.",
            ));
        }
        thread::sleep(interval);
    }
}

fn endpoint_is_reachable(endpoint: &LiveBridgeEndpoint) -> bool {
    match endpoint {
        LiveBridgeEndpoint::UnixSocket(path) => unix_socket_is_reachable(path),
        LiveBridgeEndpoint::NamedPipe(name) => named_pipe_is_reachable(name),
        LiveBridgeEndpoint::Tcp(address) => tcp_endpoint_is_reachable(address),
    }
}

#[cfg(unix)]
fn unix_socket_is_reachable(path: &str) -> bool {
    UnixStream::connect(path).is_ok()
}

#[cfg(not(unix))]
fn unix_socket_is_reachable(path: &str) -> bool {
    Path::new(path).exists()
}

#[cfg(windows)]
fn named_pipe_is_reachable(name: &str) -> bool {
    std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(format!(r"\\.\pipe\{}", name))
        .is_ok()
}

#[cfg(not(windows))]
fn named_pipe_is_reachable(_name: &str) -> bool {
    false
}

fn tcp_endpoint_is_reachable(address: &str) -> bool {
    let Ok(addresses) = address.to_socket_addrs() else {
        return false;
    };
    addresses
        .into_iter()
        .any(|address| TcpStream::connect_timeout(&address, Duration::from_millis(50)).is_ok())
}

fn cleanup_unreachable_local_endpoint(endpoint: &LiveBridgeEndpoint) -> Value {
    if endpoint_is_reachable(endpoint) {
        return Value::Null;
    }

    let probe = endpoint.classify_probe();
    let cleanup = endpoint.cleanup_stale_local_endpoint(&probe);
    if cleanup.attempted || cleanup.removed {
        json!(cleanup)
    } else {
        Value::Null
    }
}

#[allow(
    clippy::too_many_arguments,
    reason = "Lifecycle stop JSON mirrors the command result fields without changing behavior."
)]
fn lifecycle_stop_success(
    command_name: &str,
    strategy: &str,
    matched: &[DetectedGameProcess],
    stopped: &[DetectedGameProcess],
    remaining: &[DetectedGameProcess],
    matched_wrappers: &[DetectedWrapperProcess],
    stopped_wrappers: &[DetectedWrapperProcess],
    remaining_wrappers: &[DetectedWrapperProcess],
    endpoint: &LiveBridgeEndpoint,
    elapsed_ms: u128,
    timeout_ms: u64,
    endpoint_disappeared: bool,
    notices: Vec<Value>,
) -> Value {
    json!({
        "command": command_name,
        "strategy": strategy,
        "matchedPids": process_pids(matched),
        // Carries `matchKind`, which is how a caller can tell an executable-path
        // match from a registry `recorded-pid` rescue.
        "matchedProcesses": detected_processes_json(matched),
        "stoppedPids": process_pids(stopped),
        "remainingPids": process_pids(remaining),
        "wrapperProcesses": wrapper_stop_json(matched_wrappers, stopped_wrappers, remaining_wrappers),
        "endpoint": endpoint_json(endpoint),
        "elapsedMs": elapsed_ms,
        "timeoutMs": timeout_ms,
        "success": true,
        "bridgeEndpointDisappeared": endpoint_disappeared,
        "notices": notices
    })
}

#[allow(clippy::too_many_arguments)]
fn lifecycle_stop_error(
    exit_code: i32,
    code: &str,
    command_name: &str,
    strategy: &str,
    matched: &[DetectedGameProcess],
    stopped: &[DetectedGameProcess],
    remaining: &[DetectedGameProcess],
    wrapper_target: Option<&LifecycleWrapperTarget>,
    remaining_wrappers: &[DetectedWrapperProcess],
    endpoint: &LiveBridgeEndpoint,
    elapsed_ms: u128,
    timeout_ms: u64,
    notices: Vec<Value>,
    message: &str,
) -> AppError {
    let mut error = json!({
        "code": code,
        "command": command_name,
        "message": message,
        "strategy": strategy,
        "matchedPids": process_pids(matched),
        "stoppedPids": process_pids(stopped),
        "remainingPids": process_pids(remaining),
        "wrapperProcesses": if wrapper_target.is_some() {
            json!({
                "remainingPids": wrapper_process_pids(remaining_wrappers),
                "remainingProcesses": detected_wrapper_processes_json(remaining_wrappers)
            })
        } else {
            Value::Null
        },
        "endpoint": endpoint_json(endpoint),
        "elapsedMs": elapsed_ms,
        "timeoutMs": timeout_ms,
        "notices": notices
    });
    if command_name == "game close" && code == "close_timeout" {
        error["nextCommand"] = Value::String("sts2 game kill".to_string());
        error["nextAction"] = json!({
            "command": "game kill",
            "reason": "Force-terminate exact local game process matches when graceful close times out.",
            "destructive": true
        });
    }

    AppError {
        exit_code,
        payload: json!({ "error": error }),
    }
}

fn process_pids(processes: &[DetectedGameProcess]) -> Vec<u32> {
    processes.iter().map(|process| process.pid).collect()
}

fn detected_processes_json(processes: &[DetectedGameProcess]) -> Value {
    json!(
        processes
            .iter()
            .map(|process| {
                json!({
                    "pid": process.pid,
                    "executable": process.executable.as_ref().map(|path| path.display().to_string()),
                    "argv0": process.argv0.as_ref().map(|path| path.display().to_string()),
                    "matchKind": process.match_kind
                })
            })
            .collect::<Vec<_>>()
    )
}

fn wrapper_process_pids(processes: &[DetectedWrapperProcess]) -> Vec<u32> {
    processes.iter().map(|process| process.pid).collect()
}

fn detected_wrapper_processes_json(processes: &[DetectedWrapperProcess]) -> Value {
    json!(
        processes
            .iter()
            .map(|process| {
                json!({
                    "pid": process.pid,
                    "executable": process.executable.as_ref().map(|path| path.display().to_string()),
                    "argv0": process.argv0.as_ref().map(|path| path.display().to_string()),
                    "commandLine": process.command_line,
                    "matchKind": process.match_kind
                })
            })
            .collect::<Vec<_>>()
    )
}

fn wrapper_stop_json(
    matched: &[DetectedWrapperProcess],
    stopped: &[DetectedWrapperProcess],
    remaining: &[DetectedWrapperProcess],
) -> Value {
    if matched.is_empty() && stopped.is_empty() && remaining.is_empty() {
        return Value::Null;
    }
    json!({
        "matchedPids": wrapper_process_pids(matched),
        "stoppedPids": wrapper_process_pids(stopped),
        "remainingPids": wrapper_process_pids(remaining),
        "matchedProcesses": detected_wrapper_processes_json(matched),
        "remainingProcesses": detected_wrapper_processes_json(remaining)
    })
}

/// Does a pid the registry recorded still look like this instance's game?
///
/// Recycled pids are the risk, so the process has to prove itself: inside an
/// instance scope the `--user-dir` check already did that; otherwise the
/// executable must match the one we resolve or the one the launch recorded
/// (by path, or by file name when the same build lives under a different
/// checkout). An unreadable `/proc/<pid>/exe` falls back to the command line.
#[cfg(target_os = "linux")]
fn recorded_pid_looks_like_game(
    target: &LifecycleProcessTarget,
    proc_dir: &Path,
    executable: Option<&Path>,
) -> bool {
    if target.instance_user_dir.is_some() {
        return true;
    }
    let candidates = [
        Some(&target.launch_executable),
        target.recorded_launch_executable.as_ref(),
    ];
    if let Some(executable) = executable {
        return candidates.into_iter().flatten().any(|candidate| {
            candidate.as_path() == executable
                || (candidate.file_name().is_some() && candidate.file_name() == executable.file_name())
        });
    }
    read_linux_process_cmdline(proc_dir).is_some_and(|command_line| {
        candidates
            .into_iter()
            .flatten()
            .any(|candidate| command_line_mentions_path(&command_line, candidate))
    })
}

/// Registry-recorded pids that are still alive and still look like the game.
/// Runs before the executable scan: an exe-path match fails whenever the game
/// was launched from a different checkout or a rebuilt mirror, which is exactly
/// when `game close` used to report success while the game kept running.
#[cfg(target_os = "linux")]
fn matching_recorded_processes(target: &LifecycleProcessTarget) -> Vec<DetectedGameProcess> {
    let current_pid = std::process::id();
    let mut matches = Vec::new();
    for pid in &target.recorded_pids {
        let pid = *pid;
        if pid == current_pid || matches.iter().any(|found: &DetectedGameProcess| found.pid == pid) {
            continue;
        }
        let proc_dir = PathBuf::from("/proc").join(pid.to_string());
        if !proc_dir.is_dir() {
            continue;
        }
        if !process_matches_instance_scope(&proc_dir, target.instance_user_dir.as_deref()) {
            continue;
        }
        let executable = fs::read_link(proc_dir.join("exe"))
            .ok()
            .and_then(|path| fs::canonicalize(path).ok());
        if !recorded_pid_looks_like_game(target, &proc_dir, executable.as_deref()) {
            continue;
        }
        matches.push(DetectedGameProcess {
            pid,
            argv0: read_linux_process_argv0(&proc_dir, &target.launch_executable),
            executable,
            match_kind: "recorded-pid",
        });
    }
    matches
}

#[cfg(target_os = "linux")]
fn matching_linux_processes(
    target: &LifecycleProcessTarget,
    command_name: &str,
) -> Result<Vec<DetectedGameProcess>, AppError> {
    let current_pid = std::process::id();
    let mut matches = matching_recorded_processes(target);
    for entry in fs::read_dir("/proc").map_err(io_lifecycle_error(command_name))? {
        let entry = entry.map_err(io_lifecycle_error(command_name))?;
        let Some(pid) = entry
            .file_name()
            .to_str()
            .and_then(|value| value.parse::<u32>().ok())
        else {
            continue;
        };
        if pid == current_pid || matches.iter().any(|found| found.pid == pid) {
            continue;
        }

        let proc_dir = entry.path();
        // When scoped to an instance, require the process command line to carry
        // the instance's --user-dir before matching.
        if !process_matches_instance_scope(&proc_dir, target.instance_user_dir.as_deref()) {
            continue;
        }
        let executable = fs::read_link(proc_dir.join("exe"))
            .ok()
            .and_then(|path| fs::canonicalize(path).ok());
        if executable.as_ref() == Some(&target.launch_executable) {
            matches.push(DetectedGameProcess {
                pid,
                executable,
                argv0: read_linux_process_argv0(&proc_dir, &target.launch_executable),
                match_kind: "exe",
            });
            continue;
        }

        let argv0 = read_linux_process_argv0(&proc_dir, &target.launch_executable);
        if argv0.as_ref() == Some(&target.launch_executable) {
            matches.push(DetectedGameProcess {
                pid,
                executable,
                argv0,
                match_kind: "argv0",
            });
        }
    }
    Ok(matches)
}

/// True when no instance scope is requested, or the process command line
/// contains the instance's `--user-dir` path.
#[cfg(target_os = "linux")]
fn process_matches_instance_scope(proc_dir: &Path, instance_user_dir: Option<&Path>) -> bool {
    let Some(user_dir) = instance_user_dir else {
        return true;
    };
    let Some(command_line) = read_linux_process_cmdline(proc_dir) else {
        return false;
    };
    command_line_mentions_path(&command_line, user_dir)
}

#[cfg(target_os = "linux")]
fn command_line_mentions_path(command_line: &[String], path: &Path) -> bool {
    let target = path.display().to_string();
    let canonical_target = fs::canonicalize(path).ok();
    command_line.iter().any(|arg| {
        arg == &target
            || canonical_target
                .as_ref()
                .is_some_and(|canonical| fs::canonicalize(arg).ok().as_ref() == Some(canonical))
    })
}

#[cfg(target_os = "linux")]
fn matching_linux_wrapper_processes(
    target: &LifecycleWrapperTarget,
    command_name: &str,
) -> Result<Vec<DetectedWrapperProcess>, AppError> {
    let current_pid = std::process::id();
    let mut matches = Vec::new();
    for entry in fs::read_dir("/proc").map_err(io_lifecycle_error(command_name))? {
        let entry = entry.map_err(io_lifecycle_error(command_name))?;
        let Some(pid) = entry
            .file_name()
            .to_str()
            .and_then(|value| value.parse::<u32>().ok())
        else {
            continue;
        };
        if pid == current_pid {
            continue;
        }

        let proc_dir = entry.path();
        let Some(command_line) = read_linux_process_cmdline(&proc_dir) else {
            continue;
        };
        if !wrapper_command_matches(&target.wrapper_command, &proc_dir, &command_line) {
            continue;
        }
        if !command_line_mentions_launch_executable(&command_line, &target.launch_executable) {
            continue;
        }
        if let Some(user_dir) = target.instance_user_dir.as_deref()
            && !command_line_mentions_path(&command_line, user_dir)
        {
            continue;
        }

        matches.push(DetectedWrapperProcess {
            pid,
            executable: fs::read_link(proc_dir.join("exe"))
                .ok()
                .and_then(|path| fs::canonicalize(path).ok()),
            argv0: read_linux_process_argv0(&proc_dir, Path::new(&target.wrapper_command)),
            command_line,
            match_kind: "wrapper-cmdline",
        });
    }
    Ok(matches)
}

#[cfg(target_os = "linux")]
fn read_linux_process_cmdline(proc_dir: &Path) -> Option<Vec<String>> {
    let raw = fs::read(proc_dir.join("cmdline")).ok()?;
    let args = raw
        .split(|byte| *byte == 0)
        .filter(|arg| !arg.is_empty())
        .filter_map(|arg| std::str::from_utf8(arg).ok().map(str::to_string))
        .collect::<Vec<_>>();
    (!args.is_empty()).then_some(args)
}

#[cfg(target_os = "linux")]
fn wrapper_command_matches(command: &str, proc_dir: &Path, command_line: &[String]) -> bool {
    let Some(argv0) = command_line.first() else {
        return false;
    };
    let command_path = Path::new(command);
    if command_path.components().count() > 1 {
        let expected = fs::canonicalize(command_path).ok();
        let actual = fs::read_link(proc_dir.join("exe"))
            .ok()
            .and_then(|path| fs::canonicalize(path).ok())
            .or_else(|| fs::canonicalize(argv0).ok());
        if expected.is_some() && expected == actual {
            return true;
        }
        return expected.is_some_and(|expected| {
            command_line.iter().any(|arg| {
                fs::canonicalize(arg)
                    .map(|candidate| candidate == expected)
                    .unwrap_or(false)
            })
        });
    }

    Path::new(argv0)
        .file_name()
        .and_then(OsStr::to_str)
        .is_some_and(|name| name == command)
}

#[cfg(target_os = "linux")]
fn command_line_mentions_launch_executable(
    command_line: &[String],
    launch_executable: &Path,
) -> bool {
    command_line.iter().any(|arg| {
        let path = Path::new(arg);
        if path.is_absolute()
            && let Ok(canonical) = fs::canonicalize(path)
        {
            return canonical == launch_executable;
        }
        arg == &launch_executable.display().to_string()
    })
}

#[cfg(target_os = "linux")]
fn read_linux_process_argv0(proc_dir: &Path, launch_executable: &Path) -> Option<PathBuf> {
    let raw = fs::read(proc_dir.join("cmdline")).ok()?;
    let first = raw.split(|byte| *byte == 0).next()?;
    if first.is_empty() {
        return None;
    }
    let text = std::str::from_utf8(first).ok()?;
    let path = PathBuf::from(text);
    let candidate = if path.is_absolute() {
        path
    } else {
        launch_executable.parent()?.join(path)
    };
    fs::canonicalize(candidate).ok()
}

#[cfg(target_os = "linux")]
fn signal_linux_processes(
    command_name: &str,
    signal: &str,
    processes: &[DetectedGameProcess],
) -> Result<(), AppError> {
    for process in processes {
        send_signal(command_name, process.pid, signal)?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn signal_linux_wrapper_processes(
    command_name: &str,
    signal: &str,
    processes: &[DetectedWrapperProcess],
) -> Result<(), AppError> {
    for pid in wrapper_process_pids(processes) {
        send_signal(command_name, pid, signal)?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn send_signal(command_name: &str, pid: u32, signal: &str) -> Result<(), AppError> {
    let error_code = if command_name == "game kill" {
        "force_kill_failed"
    } else {
        "configured_stop_command_failed"
    };
    let output = Command::new("kill")
        .arg(format!("-{signal}"))
        .arg(pid.to_string())
        .output()
        .map_err(|source| {
            lifecycle_error(
                4,
                error_code,
                command_name,
                &format!("failed to invoke kill for pid {pid}: {source}"),
            )
        })?;
    if !output.status.success() {
        return Err(command_output_error(
            error_code,
            command_name,
            &["kill".to_string(), format!("-{signal}"), pid.to_string()],
            &output,
        ));
    }
    Ok(())
}

fn command_output_error(
    code: &str,
    command_name: &str,
    argv: &[String],
    output: &std::process::Output,
) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": code,
                "command": command_name,
                "message": format!("command '{}' exited with status {}", argv.join(" "), output.status),
                "argv": argv,
                "stdout": String::from_utf8_lossy(&output.stdout).trim().to_string(),
                "stderr": String::from_utf8_lossy(&output.stderr).trim().to_string()
            }
        }),
    }
}

fn bounded_bridge_close_rpc_timeout_ms(
    started_at: Instant,
    lifecycle_timeout: Duration,
    requested_rpc_timeout_ms: u64,
) -> u64 {
    let remaining_ms = lifecycle_timeout
        .checked_sub(started_at.elapsed())
        .map(|remaining| u64::try_from(remaining.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or(0)
        .max(1);
    if requested_rpc_timeout_ms == 0 {
        remaining_ms
    } else {
        requested_rpc_timeout_ms.min(remaining_ms).max(1)
    }
}

fn bridge_close_fallback_notice(error: &bridge::proto::BridgeError, rpc_timeout_ms: u64) -> Value {
    let error_code = bridge::proto::BridgeErrorCode::try_from(error.code)
        .ok()
        .unwrap_or(bridge::proto::BridgeErrorCode::RuntimeFailure);
    if error_code == bridge::proto::BridgeErrorCode::NotImplemented {
        json!({
            "code": "bridge-close-unsupported",
            "message": "The live bridge does not support graceful close; falling back.",
            "bridgeErrorCode": bridge::bridge_error_code_name(error_code),
            "rpcTimeoutMs": rpc_timeout_ms
        })
    } else {
        json!({
            "code": "bridge-close-request-failed",
            "message": error.message,
            "bridgeErrorCode": bridge::bridge_error_code_name(error_code),
            "rpcTimeoutMs": rpc_timeout_ms
        })
    }
}

fn launch_process_json(pid: u32, status: Option<&ExitStatus>) -> Value {
    let exited = status.is_some();
    json!({
        "pid": pid,
        "alive": !exited,
        "exited": exited,
        "exitCode": status.and_then(ExitStatus::code),
        "signal": exit_signal(status),
        "status": match status {
            Some(status) => status.to_string(),
            None => "running".to_string(),
        }
    })
}

#[cfg(unix)]
fn exit_signal(status: Option<&ExitStatus>) -> Value {
    status
        .and_then(ExitStatusExt::signal)
        .map(Value::from)
        .unwrap_or(Value::Null)
}

#[cfg(not(unix))]
fn exit_signal(_status: Option<&ExitStatus>) -> Value {
    Value::Null
}

fn recent_log_tail_json(cursor: &bridge::Sts2LogCursor) -> Value {
    let tail = bridge::recent_relevant_log_tail_since(cursor, 40, 8 * 1024);
    json!({
        "maxLines": tail.max_lines,
        "maxBytes": tail.max_bytes,
        "truncated": tail.truncated,
        "entries": tail.entries.into_iter().map(|entry| {
            json!({
                "logPath": entry.log_path.display().to_string(),
                "line": entry.line,
            })
        }).collect::<Vec<_>>()
    })
}

fn launch_wait_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    attempts: u64,
    elapsed_ms: u128,
    pid: u32,
    status: &std::process::ExitStatus,
    last_error: Option<&Value>,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
    output_capture: Option<&LaunchOutputCapture>,
) -> AppError {
    let mut error = json!({
        "code": "launch_exited_before_ipc",
        "command": command_name,
        "message": format!(
            "The launched process exited before the live {} bridge became reachable at '{}'.",
            layout.endpoint.kind(),
            layout.endpoint.display()
        ),
        "endpoint": endpoint_json(&layout.endpoint),
        "socketPath": layout.endpoint.socket_path(),
        "pipeName": layout.endpoint.pipe_name(),
        "tcpAddress": layout.endpoint.tcp_address(),
        "attempts": attempts,
        "elapsedMs": elapsed_ms,
        "exitStatus": status.code(),
        "process": launch_process_json(pid, Some(status)),
        "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
        "wrapperLogs": wrapper_output_capture_error_json(output_capture),
        "stdio": launch_stdio_capture_error_json(output_capture),
        "launchDiagnostics": launch_diagnostics(layout),
        "lastError": last_error.cloned().unwrap_or(Value::Null)
    });
    if let Some(log) = bridge::latest_recent_relevant_log() {
        error["logPath"] = json!(log.log_path.display().to_string());
        error["latestLog"] = json!({
            "logPath": log.log_path.display().to_string(),
            "matchedLine": log.matched_line
        });
        error["safeNextCommands"] = json!(crate::bridge_health_next_commands(
            "endpoint_refused_or_stale"
        ));
    }

    AppError {
        exit_code: 4,
        payload: json!({ "error": error }),
    }
}

fn wait_timeout_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    timeout_ms: u64,
    interval_ms: u64,
    attempts: u64,
    elapsed_ms: u128,
    last_error: Value,
    process: Option<Value>,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
    output_capture: Option<&LaunchOutputCapture>,
) -> AppError {
    let (code, message) = if command_name == "game launch" {
        (
            "launch_timeout",
            format!(
                "Timed out waiting for the launched process to expose a live {} bridge at '{}'.",
                layout.endpoint.kind(),
                layout.endpoint.display()
            ),
        )
    } else {
        (
            "attach_timeout",
            format!(
                "Timed out waiting for a live {} bridge at '{}'.",
                layout.endpoint.kind(),
                layout.endpoint.display()
            ),
        )
    };

    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": code,
                "command": command_name,
                "message": message,
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "timeoutMs": timeout_ms,
                "intervalMs": interval_ms,
                "attempts": attempts,
                "elapsedMs": elapsed_ms,
                "process": process.unwrap_or(Value::Null),
                "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
                "wrapperLogs": wrapper_output_capture_error_json(output_capture),
                "stdio": launch_stdio_capture_error_json(output_capture),
                "launchDiagnostics": if command_name == "game launch" {
                    launch_diagnostics(layout)
                } else {
                    Value::Null
                },
                "lastError": last_error
            }
        }),
    }
}

#[allow(clippy::too_many_arguments)]
fn launch_game_process_not_started_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    timeout_ms: u64,
    interval_ms: u64,
    attempts: u64,
    elapsed_ms: u128,
    last_error: Value,
    wrapper_process: Value,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
    observation: &LaunchGameProcessObservation,
    output_capture: Option<&LaunchOutputCapture>,
) -> AppError {
    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": "launch_game_process_not_started",
                "command": command_name,
                "message": format!(
                    "The launch wrapper stayed alive, but the configured game executable '{}' did not start before the live {} bridge timeout at '{}'.",
                    observation.game_executable.display(),
                    layout.endpoint.kind(),
                    layout.endpoint.display()
                ),
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "timeoutMs": timeout_ms,
                "intervalMs": interval_ms,
                "attempts": attempts,
                "elapsedMs": elapsed_ms,
                "process": wrapper_process,
                "wrapperCommand": observation.wrapper_command,
                "wrapperArgs": observation.wrapper_args,
                "spawnCommand": observation.spawn_command,
                "spawnArgs": observation.spawn_args,
                "spawnPid": observation.spawn_pid,
                "gameExecutable": observation.game_executable.display().to_string(),
                "gameProcessObserved": observation.ever_observed,
                "matchedGamePids": observation.matched_pids(),
                "matchedGameProcesses": observation.matched_processes_json(),
                "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
                "wrapperLogs": wrapper_output_capture_error_json(output_capture),
                "stdio": launch_stdio_capture_error_json(output_capture),
                "launchDiagnostics": launch_diagnostics(layout),
                "lastError": last_error
            }
        }),
    }
}

fn steam_initialization_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    attempts: u64,
    elapsed_ms: u128,
    log_path: &Path,
    log_line: &str,
    last_error: Value,
    recent_log_cursor: Option<&bridge::Sts2LogCursor>,
    output_capture: Option<&LaunchOutputCapture>,
) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "launch_steam_initialization_failed",
                "command": command_name,
                "message": "The launched game reported Steamworks initialization failure before the live bridge became reachable.",
                "endpoint": endpoint_json(&layout.endpoint),
                "socketPath": layout.endpoint.socket_path(),
                "pipeName": layout.endpoint.pipe_name(),
                "tcpAddress": layout.endpoint.tcp_address(),
                "attempts": attempts,
                "elapsedMs": elapsed_ms,
                "logPath": log_path.display().to_string(),
                "logLine": log_line,
                "recentLogs": recent_log_cursor.map(recent_log_tail_json).unwrap_or(Value::Null),
                "wrapperLogs": wrapper_output_capture_error_json(output_capture),
                "stdio": launch_stdio_capture_error_json(output_capture),
                "launchDiagnostics": launch_diagnostics(layout),
                "lastError": last_error
            }
        }),
    }
}

fn launch_diagnostics(layout: &ResolvedLiveBridgeLayout) -> Value {
    json!({
        "steam": steam_launch_diagnostic(&layout.game_path)
    })
}

fn steam_launch_diagnostic(game_path: &Path) -> Value {
    if agent_host_launch_enabled() {
        let detection_basis = steam_requirement_basis(game_path);
        return json!({
            "status": "host_broker_managed",
            "likelyRequiresSteamClient": !detection_basis.is_empty(),
            "detectionBasis": detection_basis,
            "clientRunning": Value::Null,
            "clientDetection": "not_attempted_agent_host_launch",
            "message": "Steam client process detection is not attempted inside the agent container; launch failures are detected from host-written game logs."
        });
    }

    let detection_basis = steam_requirement_basis(game_path);
    let likely_requires_steam = !detection_basis.is_empty();
    let client_status = detect_steam_client_status();
    let status = if !likely_requires_steam {
        "not_applicable"
    } else if client_status.running == Some(true) {
        "client_detected"
    } else if client_status.running == Some(false) {
        "client_not_detected"
    } else {
        "unknown"
    };
    let message = match (likely_requires_steam, client_status.running) {
        (false, _) => {
            "The launch path does not look like a Steam-managed or Steamworks-backed install."
        }
        (true, Some(true)) => {
            "The launch path looks Steam-backed and a Steam client process was detected."
        }
        (true, Some(false)) => {
            "The launch path looks Steam-backed, but no Steam client process was detected. STS2 may fail SteamAPI_Init until Steam is running for the owning user session."
        }
        (true, None) => {
            "The launch path looks Steam-backed, but this platform could not determine whether Steam is running."
        }
    };

    json!({
        "status": status,
        "likelyRequiresSteamClient": likely_requires_steam,
        "detectionBasis": detection_basis,
        "clientRunning": client_status.running,
        "clientDetection": client_status.detection,
        "message": message
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct SteamClientStatus {
    running: Option<bool>,
    detection: String,
}

fn detect_steam_client_status() -> SteamClientStatus {
    detect_steam_client_status_with_proc_root(Path::new("/proc"))
}

#[cfg(target_os = "linux")]
fn detect_steam_client_status_with_proc_root(proc_root: &Path) -> SteamClientStatus {
    if !proc_root.is_dir() {
        return SteamClientStatus {
            running: None,
            detection: "linux_proc_unavailable".to_string(),
        };
    }

    let entries = match fs::read_dir(proc_root) {
        Ok(entries) => entries,
        Err(_) => {
            return SteamClientStatus {
                running: None,
                detection: "linux_proc_unreadable".to_string(),
            };
        }
    };

    for entry in entries.flatten() {
        let file_name = entry.file_name();
        let Some(pid) = file_name.to_str() else {
            continue;
        };
        if !pid.chars().all(|character| character.is_ascii_digit()) {
            continue;
        }
        let process_dir = entry.path();
        if linux_process_looks_like_steam(&process_dir) {
            return SteamClientStatus {
                running: Some(true),
                detection: "linux_proc_process_match".to_string(),
            };
        }
    }

    SteamClientStatus {
        running: Some(false),
        detection: "linux_proc_no_process_match".to_string(),
    }
}

#[cfg(not(target_os = "linux"))]
fn detect_steam_client_status_with_proc_root(_proc_root: &Path) -> SteamClientStatus {
    SteamClientStatus {
        running: None,
        detection: "unsupported_platform".to_string(),
    }
}

#[cfg(target_os = "linux")]
fn linux_process_looks_like_steam(process_dir: &Path) -> bool {
    let comm_matches = fs::read_to_string(process_dir.join("comm"))
        .ok()
        .map(|contents| steam_process_name_matches(contents.trim()))
        .unwrap_or(false);
    if comm_matches {
        return true;
    }

    let Ok(cmdline) = fs::read(process_dir.join("cmdline")) else {
        return false;
    };
    cmdline
        .split(|byte| *byte == 0)
        .filter(|part| !part.is_empty())
        .filter_map(|part| std::str::from_utf8(part).ok())
        .filter_map(|part| Path::new(part).file_name().and_then(OsStr::to_str))
        .any(steam_process_name_matches)
}

#[cfg(target_os = "linux")]
fn steam_process_name_matches(name: &str) -> bool {
    name == "steam" || name == "steamwebhelper" || name.starts_with("steam-runtime")
}

fn steam_requirement_basis(game_path: &Path) -> Vec<String> {
    let mut basis = Vec::new();
    if path_looks_steam_managed(game_path) {
        basis.push("steamapps-path".to_string());
    }
    for file_name in STEAMWORKS_MARKER_FILES {
        if game_path.join(file_name).is_file() {
            basis.push(file_name.to_string());
        }
    }
    if game_path
        .join("Contents/MacOS/libsteam_api.dylib")
        .is_file()
    {
        basis.push("Contents/MacOS/libsteam_api.dylib".to_string());
    }
    basis
}

const STEAMWORKS_MARKER_FILES: [&str; 5] = [
    STEAM_APPID_FILE_NAME,
    "steam_api.dll",
    "steam_api64.dll",
    "libsteam_api.so",
    "libsteam_api.dylib",
];

fn steamworks_file_exists(game_path: &Path) -> bool {
    STEAMWORKS_MARKER_FILES
        .iter()
        .any(|file_name| game_path.join(file_name).is_file())
        || game_path
            .join("Contents/MacOS/libsteam_api.dylib")
            .is_file()
}

fn path_looks_steam_managed(game_path: &Path) -> bool {
    let components = game_path
        .components()
        .filter_map(|component| component.as_os_str().to_str())
        .map(|component| component.to_ascii_lowercase())
        .collect::<Vec<_>>();
    components
        .windows(2)
        .any(|window| window[0] == "steamapps" && window[1] == "common")
}

fn lifecycle_error(exit_code: i32, code: &str, command: &str, message: &str) -> AppError {
    AppError {
        exit_code,
        payload: json!({
            "error": {
                "code": code,
                "command": command,
                "message": message
            }
        }),
    }
}

fn endpoint_json(endpoint: &LiveBridgeEndpoint) -> Value {
    match endpoint {
        LiveBridgeEndpoint::UnixSocket(path) => json!({
            "transportKind": endpoint.transport_kind(),
            "kind": endpoint.kind(),
            "path": path,
            "socketPath": path
        }),
        LiveBridgeEndpoint::NamedPipe(name) => json!({
            "transportKind": endpoint.transport_kind(),
            "kind": endpoint.kind(),
            "name": name,
            "pipeName": name
        }),
        LiveBridgeEndpoint::Tcp(address) => json!({
            "transportKind": endpoint.transport_kind(),
            "kind": endpoint.kind(),
            "address": address
        }),
    }
}

fn io_lifecycle_error(command: &str) -> impl Fn(std::io::Error) -> AppError + '_ {
    move |source| {
        lifecycle_error(
            4,
            "tool_invocation_failed",
            command,
            &format!("filesystem operation failed: {source}"),
        )
    }
}
