use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const BRIDGE_MANIFEST_NAME: &str = "spirectlbridge.json";
const BUILD_CONFIGURATION: &str = "Debug";
const STS2_STEAM_APP_ID: &str = "2868840";
const STEAM_APPID_FILE_NAME: &str = "steam_appid.txt";
const LINUX_LAUNCH_CANDIDATES: [&str; 4] = [
    "SlayTheSpire2",
    "SlayTheSpire2.x86_64",
    "Slay the Spire 2.x86_64",
    "slaythespire2.x86_64",
];
const WINDOWS_LAUNCH_CANDIDATES: [&str; 2] = ["SlayTheSpire2.exe", "Slay the Spire 2.exe"];
const MACOS_APP_BUNDLE_CANDIDATES: [(&str, &str); 2] = [
    ("Slay the Spire 2.app", "Slay the Spire 2"),
    ("SlayTheSpire2.app", "SlayTheSpire2"),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum LaunchPlatform {
    Linux,
    Windows,
    MacOs,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum BridgeLayoutIssue {
    MissingFile(String),
    UnexpectedJson(String),
    InvalidManifest(String),
    VersionMismatch { expected: String, found: String },
}

impl BridgeLayoutIssue {
    fn message(&self, target_dir: &Path) -> String {
        match self {
            Self::MissingFile(name) => {
                format!("expected '{name}' in '{}'", target_dir.display())
            }
            Self::UnexpectedJson(path) => {
                format!("unexpected JSON artifact '{path}' in deployed bridge layout")
            }
            Self::InvalidManifest(message) => message.clone(),
            Self::VersionMismatch { expected, found } => format!(
                "expected bridge manifest version '{expected}' in '{}', found '{found}'",
                target_dir.display()
            ),
        }
    }

    fn kind(&self) -> &'static str {
        match self {
            Self::MissingFile(_) => "missing-file",
            Self::UnexpectedJson(_) => "unexpected-json",
            Self::InvalidManifest(_) => "invalid-manifest",
            Self::VersionMismatch { .. } => "version-mismatch",
        }
    }

    fn to_json(&self) -> Value {
        match self {
            Self::MissingFile(name) => json!({
                "kind": self.kind(),
                "name": name
            }),
            Self::UnexpectedJson(path) => json!({
                "kind": self.kind(),
                "path": path
            }),
            Self::InvalidManifest(message) => json!({
                "kind": self.kind(),
                "message": message
            }),
            Self::VersionMismatch { expected, found } => json!({
                "kind": self.kind(),
                "expectedVersion": expected,
                "foundVersion": found
            }),
        }
    }
}

pub(crate) fn execute_game_attach_json(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let rpc_context = context_with_rpc_timeout(context, args.rpc_timeout_ms);
    let context = rpc_context.as_app_context(context.json_output);
    ensure_live_transport(context.config, "game attach")?;
    let layout = resolve_live_bridge_layout(context.config, "game attach")?;
    cache_lifecycle_paths(context, &layout, false);
    wait_for_bridge(
        &layout,
        args.timeout_ms,
        args.interval_ms,
        context,
        "game attach",
        None,
        None,
        None,
        None,
        None,
    )
}

pub(crate) fn try_attach_for_restore(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_game_attach_json(args, context)
}

pub(crate) fn execute_game_close_json(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let result = execute_game_close_json_inner(args, context)?;
    if let Some(instance) = context.instance {
        let _ = instance.mark_status("stopped", None);
    }
    Ok(result)
}

fn execute_game_close_json_inner(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let layout = resolve_live_bridge_layout(context.config, "game close")?;
    cache_lifecycle_paths(context, &layout, false);
    let started_at = Instant::now();
    let timeout = Duration::from_millis(args.timeout_ms);
    let interval = Duration::from_millis(args.interval_ms);
    let mut notices = Vec::new();

    if context.config.transport.kind != TransportKind::Mock {
        let close_rpc_timeout_ms =
            bounded_bridge_close_rpc_timeout_ms(started_at, timeout, args.rpc_timeout_ms);
        let mut transport = context.config.transport.clone();
        transport.rpc_timeout_ms = Some(close_rpc_timeout_ms);
        let client = bridge::RuntimeBridgeClient::from_config(&transport);
        match client.close_game(bridge::proto::GameCloseRequest {
            request_id: format!("game-close-{}", std::process::id()),
        }) {
            Ok(response) if response.accepted => {
                notices.push(json!({
                    "code": "bridge-close-requested",
                    "message": "Requested graceful shutdown through the live bridge.",
                    "rpcTimeoutMs": close_rpc_timeout_ms
                }));
                notices.extend(
                    response.notices.into_iter().map(
                        |message| json!({ "code": "bridge-close-notice", "message": message }),
                    ),
                );
                let target = resolve_lifecycle_process_target(
                    context.config,
                    &layout,
                    "game close",
                    context.instance,
                )
                .ok();
                let wrapper_target =
                    resolve_lifecycle_wrapper_target(context.config, &layout, "game close")
                        .ok()
                        .flatten();
                let matched = match &target {
                    Some(target) => lifecycle_processes(target, "game close")?,
                    None => Vec::new(),
                };
                let matched_wrappers = match &wrapper_target {
                    Some(target) => lifecycle_wrapper_processes(target, "game close")?,
                    None => Vec::new(),
                };
                let wait = wait_for_lifecycle_stop(
                    target.as_ref(),
                    wrapper_target.as_ref(),
                    "game close",
                    "bridge",
                    &matched,
                    &matched,
                    &layout.endpoint,
                    timeout,
                    interval,
                    started_at,
                    notices.clone(),
                )?;
                return Ok(lifecycle_stop_success(
                    "game close",
                    "bridge",
                    &matched,
                    &matched,
                    &wait.remaining,
                    &matched_wrappers,
                    &matched_wrappers,
                    &wait.wrapper_remaining,
                    &layout.endpoint,
                    started_at.elapsed().as_millis(),
                    args.timeout_ms,
                    wait.endpoint_disappeared,
                    notices,
                ));
            }
            Ok(_) => notices.push(json!({
                "code": "bridge-close-unsupported",
                "message": "The live bridge did not accept graceful close; falling back."
            })),
            Err(error) => notices.push(bridge_close_fallback_notice(&error, close_rpc_timeout_ms)),
        }
    }

    if !context.config.game.stop_command.is_empty() {
        let output = Command::new(&context.config.game.stop_command[0])
            .args(&context.config.game.stop_command[1..])
            .output()
            .map_err(|source| {
                lifecycle_error(
                    4,
                    "configured_stop_command_failed",
                    "game close",
                    &format!(
                        "failed to invoke stop command '{}': {source}",
                        context.config.game.stop_command[0]
                    ),
                )
            })?;
        if !output.status.success() {
            return Err(command_output_error(
                "configured_stop_command_failed",
                "game close",
                &context.config.game.stop_command,
                &output,
            ));
        }
        let target =
            resolve_lifecycle_process_target(context.config, &layout, "game close", context.instance)
                .ok();
        let wrapper_target = resolve_lifecycle_wrapper_target(context.config, &layout, "game close")
            .ok()
            .flatten();
        let wait = wait_for_lifecycle_stop(
            target.as_ref(),
            wrapper_target.as_ref(),
            "game close",
            "configured-command",
            &[],
            &[],
            &layout.endpoint,
            timeout,
            interval,
            started_at,
            notices.clone(),
        )?;
        return Ok(lifecycle_stop_success(
            "game close",
            "configured-command",
            &[],
            &[],
            &wait.remaining,
            &[],
            &[],
            &wait.wrapper_remaining,
            &layout.endpoint,
            started_at.elapsed().as_millis(),
            args.timeout_ms,
            wait.endpoint_disappeared,
            notices,
        ));
    }

    #[cfg(any(target_os = "linux", windows))]
    {
        let target = resolve_lifecycle_process_target(
            context.config,
            &layout,
            "game close",
            context.instance,
        )?;
        let wrapper_target =
            resolve_lifecycle_wrapper_target(context.config, &layout, "game close")?;
        let matched = lifecycle_processes(&target, "game close")?;
        let matched_wrappers = match &wrapper_target {
            Some(target) => lifecycle_wrapper_processes(target, "game close")?,
            None => Vec::new(),
        };
        if matched.is_empty() && matched_wrappers.is_empty() {
            if !endpoint_is_reachable(&layout.endpoint) {
                let stale_cleanup = cleanup_unreachable_local_endpoint(&layout.endpoint);
                notices.push(json!({
                    "code": "no-process-found",
                    "message": "No running game process matched the configured launch executable."
                }));
                if !stale_cleanup.is_null() {
                    notices.push(json!({
                        "code": "stale-endpoint-cleanup",
                        "message": "Cleaned up the unreachable local bridge endpoint.",
                        "cleanup": stale_cleanup
                    }));
                }
                // Exit stays 0 — nothing is running, which is what was asked
                // for — but say plainly that this command stopped nothing, and
                // what it looked at. A silent `stoppedPids: []` is exactly how a
                // failed match used to read as success.
                let mut payload = lifecycle_stop_success(
                    "game close",
                    "platform-graceful",
                    &matched,
                    &[],
                    &[],
                    &matched_wrappers,
                    &[],
                    &[],
                    &layout.endpoint,
                    started_at.elapsed().as_millis(),
                    args.timeout_ms,
                    true,
                    notices,
                );
                if let Some(object) = payload.as_object_mut() {
                    object.insert("stopped".to_string(), Value::Bool(false));
                    object.insert(
                        "matchAttempts".to_string(),
                        lifecycle_match_attempts_json(&target),
                    );
                }
                return Ok(payload);
            }
            return Err(lifecycle_stop_error(
                3,
                "no_process_found",
                "game close",
                "platform-graceful",
                &matched,
                &[],
                &[],
                wrapper_target.as_ref(),
                &[],
                &layout.endpoint,
                started_at.elapsed().as_millis(),
                args.timeout_ms,
                notices,
                "No process matched the configured launch executable.",
            ));
        }
        terminate_processes("game close", &matched, false)?;
        terminate_wrapper_processes("game close", &matched_wrappers, false)?;
        let wait = wait_for_lifecycle_stop(
            Some(&target),
            wrapper_target.as_ref(),
            "game close",
            "platform-graceful",
            &matched,
            &matched,
            &layout.endpoint,
            timeout,
            interval,
            started_at,
            notices.clone(),
        )?;
        return Ok(lifecycle_stop_success(
            "game close",
            "platform-graceful",
            &matched,
            &matched,
            &wait.remaining,
            &matched_wrappers,
            &matched_wrappers,
            &wait.wrapper_remaining,
            &layout.endpoint,
            started_at.elapsed().as_millis(),
            args.timeout_ms,
            wait.endpoint_disappeared,
            notices,
        ));
    }

    #[allow(unreachable_code)]
    Err(AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "unsupported_platform",
                "command": "game close",
                "message": "platform graceful close requires game.stopCommand on this platform.",
                "configKey": "game.stopCommand",
                "platform": restart_stop_platform_name()
            }
        }),
    })
}

pub(crate) fn execute_game_kill_json(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let result = execute_game_kill_json_inner(args, context)?;
    if let Some(instance) = context.instance {
        let _ = instance.mark_status("stopped", None);
    }
    Ok(result)
}

fn execute_game_kill_json_inner(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let layout = resolve_live_bridge_layout(context.config, "game kill")?;
    cache_lifecycle_paths(context, &layout, false);
    let started_at = Instant::now();

    #[cfg(any(target_os = "linux", windows))]
    {
        let target =
            resolve_lifecycle_process_target(context.config, &layout, "game kill", context.instance)?;
        let wrapper_target =
            resolve_lifecycle_wrapper_target(context.config, &layout, "game kill")?;
        let matched = lifecycle_processes(&target, "game kill")?;
        let matched_wrappers = match &wrapper_target {
            Some(target) => lifecycle_wrapper_processes(target, "game kill")?,
            None => Vec::new(),
        };
        if matched.is_empty() && matched_wrappers.is_empty() {
            return Err(lifecycle_stop_error(
                3,
                "no_process_found",
                "game kill",
                "platform-force",
                &matched,
                &[],
                &[],
                wrapper_target.as_ref(),
                &[],
                &layout.endpoint,
                started_at.elapsed().as_millis(),
                args.timeout_ms,
                Vec::new(),
                "No process matched the configured launch executable.",
            ));
        }
        terminate_processes("game kill", &matched, true)?;
        terminate_wrapper_processes("game kill", &matched_wrappers, true)?;
        let wait = wait_for_lifecycle_stop(
            Some(&target),
            wrapper_target.as_ref(),
            "game kill",
            "platform-force",
            &matched,
            &matched,
            &layout.endpoint,
            Duration::from_millis(args.timeout_ms),
            Duration::from_millis(args.interval_ms),
            started_at,
            Vec::new(),
        )?;
        return Ok(lifecycle_stop_success(
            "game kill",
            "platform-force",
            &matched,
            &matched,
            &wait.remaining,
            &matched_wrappers,
            &matched_wrappers,
            &wait.wrapper_remaining,
            &layout.endpoint,
            started_at.elapsed().as_millis(),
            args.timeout_ms,
            wait.endpoint_disappeared,
            Vec::new(),
        ));
    }

    #[allow(unreachable_code)]
    Err(AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "unsupported_platform",
                "command": "game kill",
                "message": "platform force kill is not supported on this platform.",
                "platform": restart_stop_platform_name()
            }
        }),
    })
}

pub(crate) fn execute_game_restart_stop_json(
    args: LifecycleWaitArgs,
    context: AppContext<'_>,
    command_name: &str,
) -> Result<Value, AppError> {
    let layout = resolve_live_bridge_layout(context.config, command_name)?;
    cache_lifecycle_paths(context, &layout, false);

    if !context.config.game.stop_command.is_empty() {
        return execute_configured_restart_stop(args, context, &layout, command_name);
    }

    #[cfg(any(target_os = "linux", windows))]
    {
        let target =
            resolve_lifecycle_process_target(context.config, &layout, command_name, context.instance)?;
        return execute_restart_stop_for_target(&target, &layout.endpoint, args, command_name);
    }

    #[allow(unreachable_code)]
    Err(AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_lifecycle_config",
                "command": command_name,
                "configKey": "game.stopCommand",
                "message": format!(
                    "automatic restart on {} requires game.stopCommand; set it to stop the running game before relaunching.",
                    restart_stop_platform_name()
                ),
                "platform": restart_stop_platform_name()
            }
        }),
    })
}
