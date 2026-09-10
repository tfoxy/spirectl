use std::fs::{self};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant, UNIX_EPOCH};

use super::{
    execute_assert_json, execute_console_json, execute_dev_heal_json, execute_game_info_json,
    execute_load_fixture_json, execute_logs_json, execute_runtime_scene_children_json,
    execute_runtime_scene_hover_json, execute_runtime_scene_node_json,
    execute_runtime_scene_set_visible_json, execute_runtime_scene_tree_json,
    execute_runtime_scene_unhover_json, execute_screenshot_diff_json, execute_screenshot_json,
    execute_wait_for_json, handshake_request,
};
use crate::{AppContext, AppError, RenderedCommand, bridge, lifecycle};
use crate::{
    AssertArgs, BreakpointSubcommand, BridgeHealthArgs, DebugSessionSubcommand, DebugSubcommand,
    DevCommand, DevFetchArgs, DevHttpArgs, DevHttpWaitArgs, DevSceneSubcommand, DevSubcommand,
    DevWebsocketArgs, FetchProbe, FixtureSubcommand, GameCommand, GameInstancesCommand,
    GameInstancesSubcommand, GameModsSubcommand, GameSubcommand, HttpProbe, HttpWaitProbe,
    LiveBridgeOverrides, ModReloadSubcommand, ScenarioSubcommand, SnapshotSubcommand,
    TransportKind, WaitForArgs, WaitForTransitionsArgs, WebsocketProbe, bridge_client, clone_field,
    context_with_transport_rpc_timeout, default_ipc_path, default_tcp_address, detect_game_path,
    detect_report, execute_breakpoint_add_json, execute_breakpoint_list_json,
    execute_breakpoint_remove_json, execute_debug_events_json, execute_debug_pause_json,
    execute_debug_resume_json, execute_debug_session_end_json, execute_debug_session_start_json,
    execute_debug_session_status_json, execute_debug_status_json, execute_debug_step_json,
    execute_debug_wait_json, execute_diagnostics_json, execute_fetch_probe_json,
    execute_http_probe_json, execute_http_wait_probe_json, execute_load_latest_fixture_json,
    execute_log_health_json, execute_mod_reload_json, execute_mod_reload_status_json,
    execute_recorded_fixture_clear_json, execute_recorded_fixture_record_json,
    execute_recorded_fixture_resume_json, execute_recorded_fixture_status_json,
    execute_scenario_export_json, execute_scenario_load_json, execute_snapshot_compare_json,
    execute_snapshot_export_json, execute_visual_preflight_json, execute_websocket_probe_json,
    handshake_json, live_bridge, maybe_cache_discovered_game_path, remove_null_fields,
    render_game_launch_success, render_structured, render_success, render_value_success,
};
use serde_json::{Value, json};

pub(crate) fn handle_dev(
    command: DevCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        DevSubcommand::Logs(args) => {
            render_value_success(execute_logs_json(args, context)?, context.json_output)
        }
        DevSubcommand::LogHealth(args) => {
            render_value_success(execute_log_health_json(args, context)?, context.json_output)
        }
        DevSubcommand::Console(args) => {
            render_value_success(execute_console_json(args, context)?, context.json_output)
        }
        DevSubcommand::Heal(args) => {
            render_value_success(execute_dev_heal_json(args, context)?, context.json_output)
        }
        DevSubcommand::Diagnostics(args) => render_value_success(
            execute_diagnostics_json(args, context)?,
            context.json_output,
        ),
        DevSubcommand::Assert(args) => handle_dev_assert(args, context),
        DevSubcommand::Http(args) => render_value_success(
            execute_http_probe_json(&build_http_probe(args)?)?,
            context.json_output,
        ),
        DevSubcommand::HttpWait(args) => render_value_success(
            execute_http_wait_probe_json(&build_http_wait_probe(args)?)?,
            context.json_output,
        ),
        DevSubcommand::Fetch(args) => render_value_success(
            execute_fetch_probe_json(
                &build_fetch_probe(args)?,
                None,
                &context.config.artifacts.dir,
            )?,
            context.json_output,
        ),
        DevSubcommand::Websocket(args) => render_value_success(
            execute_websocket_probe_json(&build_websocket_probe(args))?,
            context.json_output,
        ),
        DevSubcommand::WaitFor(args) => handle_dev_wait_for(args, context),
        DevSubcommand::WaitForTransitions(args) => render_value_success(
            execute_wait_for_transitions_json(args, context)?,
            context.json_output,
        ),
        DevSubcommand::Fixture(command) => match command.command {
            FixtureSubcommand::Record => render_value_success(
                execute_recorded_fixture_record_json(context)?,
                context.json_output,
            ),
            FixtureSubcommand::Resume(args) => render_value_success(
                execute_recorded_fixture_resume_json(args, context)?,
                context.json_output,
            ),
            FixtureSubcommand::Status => render_value_success(
                execute_recorded_fixture_status_json(context)?,
                context.json_output,
            ),
            FixtureSubcommand::Clear => render_value_success(
                execute_recorded_fixture_clear_json(context)?,
                context.json_output,
            ),
            FixtureSubcommand::Load(args) => render_value_success(
                execute_load_fixture_json(args, context)?,
                context.json_output,
            ),
            FixtureSubcommand::LoadLatest(args) => render_value_success(
                execute_load_latest_fixture_json(args, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::LoadFixture(args) => render_value_success(
            execute_load_fixture_json(args, context)?,
            context.json_output,
        ),
        DevSubcommand::Scenario(command) => match command.command {
            ScenarioSubcommand::Export(args) => render_value_success(
                execute_scenario_export_json(args, context)?,
                context.json_output,
            ),
            ScenarioSubcommand::Load(args) => render_value_success(
                execute_scenario_load_json(args, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::Debug(command) => match command.command {
            DebugSubcommand::Status(args) => render_value_success(
                execute_debug_status_json(args, context)?,
                context.json_output,
            ),
            DebugSubcommand::Session(command) => match command.command {
                DebugSessionSubcommand::Start(args) => render_value_success(
                    execute_debug_session_start_json(args, context)?,
                    context.json_output,
                ),
                DebugSessionSubcommand::Status(args) => render_value_success(
                    execute_debug_session_status_json(args, context)?,
                    context.json_output,
                ),
                DebugSessionSubcommand::End(args) => render_value_success(
                    execute_debug_session_end_json(args, context)?,
                    context.json_output,
                ),
            },
            DebugSubcommand::Events(args) => render_value_success(
                execute_debug_events_json(args, context)?,
                context.json_output,
            ),
            DebugSubcommand::Pause(args) => render_value_success(
                execute_debug_pause_json(args, context)?,
                context.json_output,
            ),
            DebugSubcommand::Resume(args) => render_value_success(
                execute_debug_resume_json(args, context)?,
                context.json_output,
            ),
            DebugSubcommand::Step(args) => {
                render_value_success(execute_debug_step_json(args, context)?, context.json_output)
            }
            DebugSubcommand::Wait(args) => {
                render_value_success(execute_debug_wait_json(args, context)?, context.json_output)
            }
        },
        DevSubcommand::Breakpoint(command) => match command.command {
            BreakpointSubcommand::List(args) => render_value_success(
                execute_breakpoint_list_json(args, context)?,
                context.json_output,
            ),
            BreakpointSubcommand::Add(args) => render_value_success(
                execute_breakpoint_add_json(args, context)?,
                context.json_output,
            ),
            BreakpointSubcommand::Remove(args) => render_value_success(
                execute_breakpoint_remove_json(args, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::Screenshot(args) => {
            render_value_success(execute_screenshot_json(args, context)?, context.json_output)
        }
        DevSubcommand::ScreenshotDiff(args) => render_value_success(
            execute_screenshot_diff_json(args, context)?,
            context.json_output,
        ),
        DevSubcommand::Snapshot(command) => match command.command {
            SnapshotSubcommand::Export(args) => render_value_success(
                execute_snapshot_export_json(args, context)?,
                context.json_output,
            ),
            SnapshotSubcommand::Compare(args) => render_value_success(
                execute_snapshot_compare_json(args, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::ModReload(command) => match command.command {
            Some(ModReloadSubcommand::Status(args)) => render_value_success(
                execute_mod_reload_status_json(args, context)?,
                context.json_output,
            ),
            None => render_value_success(
                execute_mod_reload_json(command, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::Scene(command) => match command.command {
            DevSceneSubcommand::Tree(args) => render_value_success(
                execute_runtime_scene_tree_json(args, context)?,
                context.json_output,
            ),
            DevSceneSubcommand::Node(args) => render_value_success(
                execute_runtime_scene_node_json(args, context)?,
                context.json_output,
            ),
            DevSceneSubcommand::Children(args) => render_value_success(
                execute_runtime_scene_children_json(args, context)?,
                context.json_output,
            ),
            DevSceneSubcommand::Hover(args) => render_value_success(
                execute_runtime_scene_hover_json(args, context)?,
                context.json_output,
            ),
            DevSceneSubcommand::Unhover(args) => render_value_success(
                execute_runtime_scene_unhover_json(args, context)?,
                context.json_output,
            ),
            DevSceneSubcommand::SetVisible(args) => render_value_success(
                execute_runtime_scene_set_visible_json(args, context)?,
                context.json_output,
            ),
        },
        DevSubcommand::VisualPreflight(args) => render_value_success(
            execute_visual_preflight_json(args, context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn build_http_probe(args: DevHttpArgs) -> Result<HttpProbe, AppError> {
    Ok(HttpProbe {
        url: args.url,
        method: args.method,
        headers: args.headers,
        body: args.body,
        timeout_ms: args.timeout_ms,
        expect_status: args.expect_status,
        expect_headers: args.expect_headers,
        query: args
            .query
            .to_probe_query()
            .map_err(|message| AppError::invalid_query("query", &message))?,
    })
}

pub(crate) fn build_http_wait_probe(args: DevHttpWaitArgs) -> Result<HttpWaitProbe, AppError> {
    Ok(HttpWaitProbe {
        http: build_http_probe(args.http)?,
        interval_ms: args.interval_ms,
    })
}

pub(crate) fn build_fetch_probe(args: DevFetchArgs) -> Result<FetchProbe, AppError> {
    Ok(FetchProbe {
        source: args.source,
        output: args.output,
        expect_sha256: args.expect_sha256,
        query: args
            .query
            .to_probe_query()
            .map_err(|message| AppError::invalid_query("query", &message))?,
    })
}

pub(crate) fn build_websocket_probe(args: DevWebsocketArgs) -> WebsocketProbe {
    WebsocketProbe {
        url: args.url,
        headers: args.headers,
        send_text: args.send_text,
        expect_text: args.expect_text,
        timeout_ms: args.timeout_ms,
    }
}

pub(crate) fn handle_dev_assert(
    args: AssertArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let predicate = args
        .predicate
        .to_predicate()
        .map_err(|message| AppError::invalid_query(&args.path, &message))?;
    render_value_success(
        execute_assert_json(
            args.path,
            predicate,
            args.source,
            args.perspective,
            args.player_id,
            context,
        )?,
        context.json_output,
    )
}

pub(crate) fn handle_dev_wait_for(
    args: WaitForArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let predicate = args
        .predicate
        .to_predicate()
        .map_err(|message| AppError::invalid_query(&args.path, &message))?;
    render_value_success(
        execute_wait_for_json(
            args.path,
            predicate,
            args.source,
            args.timeout_ms,
            args.interval_ms,
            args.rpc_timeout_ms,
            args.perspective,
            args.player_id,
            context,
        )?,
        context.json_output,
    )
}

pub(crate) fn execute_wait_for_transitions_json(
    args: WaitForTransitionsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let mut attempts = 0usize;
    let mut stable_samples = 0u32;
    let required_stable_samples = args.stable_samples.max(1);
    let mut last_status = None;

    loop {
        let elapsed_before_poll = started_at
            .elapsed()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX);
        if elapsed_before_poll >= args.timeout_ms {
            return Err(AppError::transition_wait_timeout(
                args.timeout_ms,
                args.interval_ms,
                attempts,
                started_at.elapsed().as_millis(),
                required_stable_samples,
                stable_samples,
                last_status.unwrap_or_else(|| json!(null)),
            ));
        }

        attempts += 1;
        let scoped_context = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
        let client = bridge_client(scoped_context.as_app_context(context.json_output));
        let response = client
            .runtime_transition_status(bridge::proto::RuntimeTransitionStatusRequest {
                request_id: format!("cli-wait-for-transitions-{attempts}"),
            })
            .map_err(AppError::bridge)?;
        let status = runtime_transition_status_json(&response);
        last_status = Some(status.clone());

        if response.quiescent {
            stable_samples += 1;
        } else {
            stable_samples = 0;
        }

        if stable_samples >= required_stable_samples {
            return Ok(json!({
                "quiescent": true,
                "attempts": attempts,
                "elapsedMs": started_at.elapsed().as_millis(),
                "stableSamples": stable_samples,
                "requiredStableSamples": required_stable_samples,
                "status": status,
            }));
        }

        if started_at.elapsed() >= Duration::from_millis(args.timeout_ms) {
            return Err(AppError::transition_wait_timeout(
                args.timeout_ms,
                args.interval_ms,
                attempts,
                started_at.elapsed().as_millis(),
                required_stable_samples,
                stable_samples,
                status,
            ));
        }

        let elapsed_after_poll = started_at
            .elapsed()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX);
        let remaining_after_poll = args.timeout_ms.saturating_sub(elapsed_after_poll);
        thread::sleep(Duration::from_millis(
            args.interval_ms.min(remaining_after_poll),
        ));
    }
}

pub(crate) fn runtime_transition_status_json(
    response: &bridge::proto::RuntimeTransitionStatusResponse,
) -> Value {
    json!({
        "source": bridge::data_source_name(bridge::proto::DataSource::try_from(response.source).unwrap_or(bridge::proto::DataSource::Unspecified)),
        "provisional": response.provisional,
        "screen": response.screen.as_ref().map(|screen| json!({
            "id": screen.id,
            "title": screen.title,
            "instanceId": screen.screen_instance_id,
        })),
        "quiescent": response.quiescent,
        "blockingCount": response.blocking_count,
        "ignoredInfiniteCount": response.ignored_infinite_count,
        "blockers": response.blockers.iter().map(runtime_transition_blocker_json).collect::<Vec<_>>(),
        "notes": response.notes,
    })
}

fn runtime_transition_blocker_json(blocker: &bridge::proto::RuntimeTransitionBlocker) -> Value {
    json!({
        "kind": blocker.kind,
        "nodePath": blocker.node_path,
        "nodeType": blocker.node_type,
        "name": blocker.name,
        "status": blocker.status,
        "reason": blocker.reason,
        "animationName": blocker.animation_name,
        "loopsLeft": blocker.loops_left,
        "running": blocker.running,
        "infinite": blocker.infinite,
    })
}

pub(crate) fn handle_game(
    command: GameCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        GameSubcommand::Detect => {
            let detection = detect_game_path(context.config);
            let live_bridge = detect_report(
                context.config,
                LiveBridgeOverrides {
                    game_path: detection.effective_path(),
                    ..LiveBridgeOverrides::default()
                },
            );
            let mut notes = vec![
                "Steam discovery searches the current host OS only; when running under WSL it also checks Windows Steam roots under /mnt/<drive>."
                    .to_string(),
                "Default config lookup layers sts2.config.yaml and sts2.local.yaml when present."
                    .to_string(),
            ];
            if let Some(error) = detection.configured_error.as_ref() {
                if let Some(detected_path) = detection.discovered_path.as_ref() {
                    notes.push(format!(
                        "Configured game.path '{}' is invalid ({error}); using detected Steam install '{}'.",
                        detection.configured_path,
                        detected_path.display()
                    ));
                } else {
                    notes.push(format!(
                        "Configured game.path '{}' is invalid: {error}",
                        detection.configured_path
                    ));
                }
            } else if detection.status() == "detected" {
                notes.push(
                    "Detected Slay the Spire 2 from Steam library metadata on the current host OS."
                        .to_string(),
                );
            }
            if detection.status() == "not-detected" {
                notes.push(
                    "No Steam install for Slay the Spire 2 was detected on the current host OS."
                        .to_string(),
                );
            }
            if let Some(note) = maybe_cache_discovered_game_path(
                context,
                detection.effective_path(),
                detection.used_discovered_path(),
            ) {
                notes.push(note);
            }
            notes.push(
                "Set game.path in that default config stack or pass --config <path> to use a one-off config file for live bridge packaging and verification."
                    .to_string(),
            );
            render_success(
                json!({
                    "executable": "sts2",
                    "status": detection.status(),
                    "configuredPath": detection.configured_path,
                    "detectedPath": detection.effective_path().map(|path| Value::String(path.display().to_string())).unwrap_or(Value::Null),
                    "liveBridge": live_bridge,
                    "notes": notes
                }),
                context.json_output,
            )
        }
        GameSubcommand::Info => {
            render_value_success(execute_game_info_json(context)?, context.json_output)
        }
        GameSubcommand::BridgeHealth(args) => execute_bridge_health_command(args, context),
        GameSubcommand::InstallBridge(args) => render_value_success(
            lifecycle::execute_game_install_bridge_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Launch(args) => render_game_launch_success(
            lifecycle::execute_game_launch_explicit_relaunch_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Attach(args) => render_value_success(
            lifecycle::execute_game_attach_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Close(args) => render_value_success(
            lifecycle::execute_game_close_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Kill(args) => render_value_success(
            lifecycle::execute_game_kill_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Deploy(args) => render_value_success(
            lifecycle::execute_game_deploy_json(args, context)?,
            context.json_output,
        ),
        GameSubcommand::Mods(args) => match args.command {
            GameModsSubcommand::Settings(settings_args) => render_value_success(
                lifecycle::execute_game_mods_settings_json(settings_args, context)?,
                context.json_output,
            ),
            GameModsSubcommand::Active => render_value_success(
                lifecycle::execute_game_mods_active_json(context)?,
                context.json_output,
            ),
        },
        GameSubcommand::Instances(command) => render_value_success(
            handle_game_instances(command, context)?,
            context.json_output,
        ),
    }
}

fn handle_game_instances(
    command: GameInstancesCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    use crate::instance::{absolute_instances_dir, list_records, pid_is_live};

    let prune = matches!(command.command, Some(GameInstancesSubcommand::Prune));
    let instances_dir = absolute_instances_dir(context.config);
    let mut entries = Vec::new();
    let mut pruned = Vec::new();

    for record in list_records(&instances_dir) {
        let pid_live = record.pid.is_some_and(pid_is_live);
        let socket_exists = std::path::Path::new(&record.socket).exists();
        let running = pid_live && socket_exists;

        if prune && !running {
            let dir = instances_dir.join(&record.name);
            if std::fs::remove_dir_all(&dir).is_ok() {
                pruned.push(record.name.clone());
                continue;
            }
        }

        entries.push(json!({
            "name": record.name,
            "mode": record.mode,
            "socket": record.socket,
            "userDir": record.user_dir,
            "gameRoot": record.game_root,
            "modsDir": record.mods_dir,
            "launchExecutable": record.launch_executable,
            "pid": record.pid,
            "gamePids": record.game_pids,
            "gamePath": record.game_path,
            "stdoutPath": record.stdout_path,
            "stderrPath": record.stderr_path,
            "status": record.status,
            "pidLive": pid_live,
            "socketExists": socket_exists,
            "running": running,
        }));
    }

    let mut payload = json!({
        "instancesDir": instances_dir.display().to_string(),
        "count": entries.len(),
        "instances": entries,
    });
    if prune {
        payload["pruned"] = json!(pruned);
    }
    Ok(payload)
}

pub(crate) fn execute_bridge_health_command(
    args: BridgeHealthArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    if args.non_mutating && args.repair_stale_endpoint {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "bridge_health_conflicting_modes",
                    "message": "game bridge-health accepts either the default non-mutating mode or --repair-stale-endpoint, not both."
                }
            }),
        });
    }

    let mut effective_args = args;
    if !effective_args.repair_stale_endpoint {
        effective_args.non_mutating = true;
    }

    let full_health = bridge_health_json(effective_args.clone(), context);
    let status = full_health["status"].as_str().unwrap_or("unknown");
    let exit_code = if matches!(status, "reachable_current") {
        0
    } else {
        4
    };
    let error_code = full_health["code"]
        .as_str()
        .unwrap_or("bridge_health_failed")
        .to_string();
    let error_message = full_health["message"]
        .as_str()
        .unwrap_or("Bridge health check failed.")
        .to_string();
    let health = if effective_args.verbose {
        full_health.clone()
    } else {
        bridge_health_compact_json(&full_health)
    };
    let payload = if exit_code == 0 {
        health
    } else {
        json!({
            "error": {
                "code": error_code,
                "message": error_message
            },
            "health": health
        })
    };

    Ok(RenderedCommand {
        stdout: render_structured(&payload, context.json_output),
        exit_code,
    })
}

pub(crate) fn bridge_health_compact_json(full: &Value) -> Value {
    let endpoint = &full["endpoint"];
    let connection = &full["connection"];
    let deployed_bridge = &full["deployedBridge"];
    let duplicate_bridge_mods = &full["duplicateBridgeMods"];
    let live_bridge = &full["liveBridge"];
    let local_bridge = &full["localBridge"];
    let duplicate_entries = duplicate_bridge_mods["entries"]
        .as_array()
        .cloned()
        .unwrap_or_default();

    let mut compact = json!({
        "status": clone_field(full, "status"),
        "code": clone_field(full, "code"),
        "message": clone_field(full, "message"),
        "nonMutating": clone_field(full, "nonMutating"),
        "repairStaleEndpoint": clone_field(full, "repairStaleEndpoint"),
        "rpcTimeoutMs": clone_field(full, "rpcTimeoutMs"),
        "endpoint": {
            "transportKind": clone_field(endpoint, "transportKind"),
            "kind": clone_field(endpoint, "kind"),
            "address": clone_field(endpoint, "address"),
            "classification": clone_field(endpoint, "classification"),
            "exists": clone_field(endpoint, "exists"),
            "fileKind": clone_field(endpoint, "fileKind"),
            "ownerProcessIdentified": endpoint.pointer("/staleEndpointEvidence/ownerProcessIdentified").cloned().unwrap_or(Value::Null),
        },
        "connection": {
            "status": clone_field(connection, "status"),
            "bridgeErrorCode": clone_field(connection, "bridgeErrorCode"),
        },
        "bridge": {
            "expectedVersion": clone_field(full, "expectedBridgeVersion"),
            "deployedStatus": clone_field(deployed_bridge, "status"),
            "deployedVersion": clone_field(deployed_bridge, "version"),
            "liveVersion": clone_field(live_bridge, "bridgeVersion"),
            "localVersion": clone_field(local_bridge, "bridgeVersion"),
            "gameVersion": clone_field(live_bridge, "gameVersion"),
            "sourceScanMode": local_bridge
                .pointer("/sourceFreshness/scanMode")
                .cloned()
                .unwrap_or(Value::Null),
        },
        "duplicateBridgeMods": {
            "status": clone_field(duplicate_bridge_mods, "status"),
            "count": duplicate_entries.len(),
        },
        "safeNextCommands": clone_field(full, "safeNextCommands"),
        "fullOutputCommand": "sts2 --json game bridge-health --verbose",
    });

    if !duplicate_entries.is_empty() {
        compact["duplicateBridgeMods"]["entries"] = Value::Array(duplicate_entries);
    }
    if full.get("latestLog").is_some_and(|value| !value.is_null()) {
        compact["latestLog"] = clone_field(full, "latestLog");
    }
    if full
        .get("endpointAfterRepair")
        .is_some_and(|value| !value.is_null())
    {
        compact["endpointAfterRepair"] = clone_field(full, "endpointAfterRepair");
    }
    if full
        .get("staleCleanup")
        .is_some_and(|value| !value.is_null())
    {
        compact["staleCleanup"] = clone_field(full, "staleCleanup");
    }

    remove_null_fields(&mut compact);
    compact
}

pub(crate) fn bridge_health_json(args: BridgeHealthArgs, context: AppContext<'_>) -> Value {
    let endpoint = match context.config.transport.kind {
        TransportKind::Tcp => live_bridge::LiveBridgeEndpoint::Tcp(
            context
                .config
                .transport
                .tcp_address
                .clone()
                .unwrap_or_else(default_tcp_address),
        ),
        TransportKind::Mock | TransportKind::Ipc => live_bridge::LiveBridgeEndpoint::UnixSocket(
            context
                .config
                .transport
                .ipc_path
                .clone()
                .unwrap_or_else(default_ipc_path),
        ),
    };
    let endpoint_probe = endpoint.classify_probe();
    let endpoint_json = bridge_health_endpoint_json(&endpoint, &endpoint_probe);
    let manifest = bridge_health_manifest_json(context);
    let duplicate_bridge_mods = bridge_health_duplicate_bridge_mods_json(context);
    let local_bridge = bridge_health_local_bridge_json(args.verbose || args.check_source);
    let expected_bridge_version = bridge::bridge_version();
    let scoped = context_with_transport_rpc_timeout(context, args.rpc_timeout_ms);
    let scoped_context = scoped.as_app_context(context.json_output);
    let client = bridge_client(scoped_context);

    match client.handshake(handshake_request(scoped_context)) {
        Ok(handshake) => {
            let live_version = handshake.bridge_version.clone();
            let live_current = live_version == expected_bridge_version;
            let live_stale = live_current && bridge_health_live_is_stale(&handshake, &local_bridge);
            let deployed_status = manifest
                .get("status")
                .and_then(Value::as_str)
                .unwrap_or("unknown");
            let deployed_known_mismatch = deployed_status == "version_mismatch";
            let (status, code, message) = if !live_current {
                (
                    "live_version_mismatch",
                    "live_version_mismatch",
                    "Reachable live bridge version does not match the CLI bridge version.",
                )
            } else if live_stale {
                (
                    "stale_live_host",
                    "stale_live_host",
                    "Reachable live bridge version matches, but the loaded bridge build predates local bridge source inputs.",
                )
            } else if deployed_known_mismatch {
                (
                    "deployed_version_mismatch",
                    "deployed_version_mismatch",
                    "Live bridge is reachable, but the deployed bridge manifest is stale.",
                )
            } else {
                (
                    "reachable_current",
                    "reachable_current",
                    "Live bridge is reachable and matches the CLI bridge version.",
                )
            };
            json!({
                "status": status,
                "code": code,
                "message": message,
                "nonMutating": args.non_mutating,
                "repairStaleEndpoint": args.repair_stale_endpoint,
                "rpcTimeoutMs": args.rpc_timeout_ms,
                "endpoint": endpoint_json,
                "staleCleanup": Value::Null,
                "connection": { "status": "reachable" },
                "expectedBridgeVersion": expected_bridge_version,
                "localBridge": local_bridge,
                "deployedBridge": manifest,
                "duplicateBridgeMods": duplicate_bridge_mods,
                "liveBridge": handshake_json(&handshake),
                "compatibility": {},
                "safeNextCommands": bridge_health_next_commands(status)
            })
        }
        Err(error) => {
            let bridge_code = bridge::bridge_error_code_name(
                bridge::proto::BridgeErrorCode::try_from(error.code)
                    .ok()
                    .unwrap_or(bridge::proto::BridgeErrorCode::RuntimeFailure),
            );
            let status = match bridge_code {
                "ipc_socket_missing" => match endpoint_probe.classification {
                    "permission-denied" => "endpoint_permission_denied",
                    "ambiguous" => "endpoint_ambiguous",
                    _ => "endpoint_missing",
                },
                "ipc_connection_failed" | "transport_connection_failed" => {
                    match endpoint_probe.classification {
                        "permission-denied" => "endpoint_permission_denied",
                        "ambiguous" => "endpoint_ambiguous",
                        _ => "endpoint_refused_or_stale",
                    }
                }
                "bridge_rpc_timeout" => "rpc_timeout",
                _ => "unreachable_bridge",
            };
            let latest_log = latest_relevant_log_json();
            let stale_cleanup = if args.repair_stale_endpoint {
                let cleanup = endpoint.cleanup_stale_local_endpoint(&endpoint_probe);
                let endpoint_after_probe = endpoint.classify_probe();
                let endpoint_after = bridge_health_endpoint_json(&endpoint, &endpoint_after_probe);
                let status_after = if matches!(endpoint_after_probe.classification, "missing") {
                    "endpoint_missing"
                } else {
                    status
                };
                return json!({
                    "status": status_after,
                    "code": status_after,
                    "message": error.message,
                    "nonMutating": args.non_mutating,
                    "repairStaleEndpoint": args.repair_stale_endpoint,
                    "rpcTimeoutMs": args.rpc_timeout_ms,
                    "endpoint": endpoint_json,
                    "endpointAfterRepair": endpoint_after,
                    "latestLog": latest_log.clone().unwrap_or(Value::Null),
                    "logPath": latest_log
                        .as_ref()
                        .and_then(|value| value.get("logPath"))
                        .cloned()
                        .unwrap_or(Value::Null),
                    "staleCleanup": cleanup,
                    "connection": {
                        "status": "failed",
                        "bridgeErrorCode": bridge_code,
                        "details": error.details.iter().map(|detail| json!({
                            "field": detail.field,
                            "value": detail.value,
                            "note": detail.note
                        })).collect::<Vec<_>>()
                    },
                    "expectedBridgeVersion": expected_bridge_version,
                    "localBridge": local_bridge,
                    "deployedBridge": manifest,
                    "duplicateBridgeMods": duplicate_bridge_mods,
                    "liveBridge": Value::Null,
                    "compatibility": {},
                    "safeNextCommands": bridge_health_next_commands(status_after)
                });
            } else {
                Value::Null
            };
            json!({
                "status": status,
                "code": status,
                "message": error.message,
                "nonMutating": args.non_mutating,
                "repairStaleEndpoint": args.repair_stale_endpoint,
                "rpcTimeoutMs": args.rpc_timeout_ms,
                "endpoint": endpoint_json,
                "latestLog": latest_log.clone().unwrap_or(Value::Null),
                "logPath": latest_log
                    .as_ref()
                    .and_then(|value| value.get("logPath"))
                    .cloned()
                    .unwrap_or(Value::Null),
                "staleCleanup": stale_cleanup,
                "connection": {
                    "status": "failed",
                    "bridgeErrorCode": bridge_code,
                    "details": error.details.iter().map(|detail| json!({
                        "field": detail.field,
                        "value": detail.value,
                        "note": detail.note
                    })).collect::<Vec<_>>()
                },
                "expectedBridgeVersion": expected_bridge_version,
                "localBridge": local_bridge,
                "deployedBridge": manifest,
                "duplicateBridgeMods": duplicate_bridge_mods,
                "liveBridge": Value::Null,
                "compatibility": {},
                "safeNextCommands": bridge_health_next_commands(status)
            })
        }
    }
}

pub(crate) fn bridge_health_duplicate_bridge_mods_json(context: AppContext<'_>) -> Value {
    let Some(mods_dir) = bridge_health_mods_dir(context) else {
        return json!({
            "status": "unknown",
            "modsDir": Value::Null,
            "entries": [],
            "remediation": "Configure game.modsDir or game.path, then rerun `sts2 game bridge-health`."
        });
    };

    if !mods_dir.is_dir() {
        return json!({
            "status": "mods_dir_missing",
            "modsDir": mods_dir.display().to_string(),
            "entries": [],
            "remediation": "Create or configure the STS2 mods directory before checking duplicate bridge mods."
        });
    }

    let mut entries = Vec::new();
    let read_dir = match fs::read_dir(&mods_dir) {
        Ok(read_dir) => read_dir,
        Err(error) => {
            return json!({
                "status": "unreadable",
                "modsDir": mods_dir.display().to_string(),
                "entries": [],
                "error": error.to_string(),
                "remediation": "Fix filesystem permissions and rerun `sts2 game bridge-health`."
            });
        }
    };

    for entry in read_dir.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        let Some(directory_name) = path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        let Some((manifest_path, manifest_id)) = bridge_health_mod_manifest_id(&path) else {
            continue;
        };
        if manifest_id.eq_ignore_ascii_case(live_bridge::DEPLOYED_MOD_DIR_NAME)
            && directory_name != live_bridge::DEPLOYED_MOD_DIR_NAME
        {
            entries.push(json!({
                "directoryName": directory_name,
                "path": path.display().to_string(),
                "manifestPath": manifest_path.display().to_string(),
                "manifestId": manifest_id,
                "canonicalDirectoryName": live_bridge::DEPLOYED_MOD_DIR_NAME,
                "issue": "legacy_duplicate_bridge_directory",
                "remediation": format!(
                    "Move or remove this legacy bridge directory after confirming '{}' is installed; sts2 will not mutate it from bridge-health.",
                    live_bridge::DEPLOYED_MOD_DIR_NAME
                )
            }));
        }
    }
    entries.sort_by(|left, right| {
        left["path"]
            .as_str()
            .unwrap_or_default()
            .cmp(right["path"].as_str().unwrap_or_default())
    });

    json!({
        "status": if entries.is_empty() { "none" } else { "duplicates_found" },
        "modsDir": mods_dir.display().to_string(),
        "entries": entries,
        "remediation": if entries.is_empty() {
            "No duplicate legacy spirectlbridge mod directories were detected."
        } else {
            "Review listed legacy bridge directories and remove or quarantine them manually; this health check is non-mutating."
        }
    })
}

fn bridge_health_mods_dir(context: AppContext<'_>) -> Option<PathBuf> {
    if let Some(mods_dir) = context.config.game.mods_dir.as_ref() {
        Some(PathBuf::from(mods_dir))
    } else if !context.config.game.path.is_empty() && context.config.game.path != "auto" {
        Some(PathBuf::from(&context.config.game.path).join("mods"))
    } else {
        None
    }
}

fn bridge_health_mod_manifest_id(mod_dir: &Path) -> Option<(PathBuf, String)> {
    for manifest_name in ["spirectlbridge.json", "mod_manifest.json"] {
        let path = mod_dir.join(manifest_name);
        let Ok(raw) = fs::read_to_string(&path) else {
            continue;
        };
        let Ok(parsed) = serde_json::from_str::<Value>(&raw) else {
            continue;
        };
        if let Some(id) = parsed.get("id").and_then(Value::as_str).map(str::trim)
            && !id.is_empty()
        {
            return Some((path, id.to_string()));
        }
    }
    None
}

pub(crate) fn bridge_health_json_for_dev(args: BridgeHealthArgs, context: AppContext<'_>) -> Value {
    bridge_health_json(args, context)
}

pub(crate) fn latest_relevant_log_json() -> Option<Value> {
    let log = bridge::latest_recent_relevant_log()?;
    Some(json!({
        "logPath": log.log_path.display().to_string(),
        "matchedLine": log.matched_line
    }))
}

pub(crate) fn bridge_health_endpoint_json(
    endpoint: &live_bridge::LiveBridgeEndpoint,
    probe: &live_bridge::LiveBridgeEndpointProbe,
) -> Value {
    let mut value = json!({
        "transportKind": endpoint.transport_kind(),
        "kind": endpoint.kind(),
        "address": endpoint.display(),
        "classification": probe.classification,
        "socketPath": endpoint.socket_path(),
        "pipeName": endpoint.pipe_name(),
        "tcpAddress": endpoint.tcp_address(),
    });
    if let Some(exists) = probe.exists {
        value["exists"] = json!(exists);
    }
    if let Some(file_kind) = probe.file_kind {
        value["fileKind"] = json!(file_kind);
    }
    if let Some(evidence) = stale_endpoint_evidence_json(endpoint, probe) {
        value["staleEndpointEvidence"] = evidence;
    }
    value
}

pub(crate) fn stale_endpoint_evidence_json(
    endpoint: &live_bridge::LiveBridgeEndpoint,
    probe: &live_bridge::LiveBridgeEndpointProbe,
) -> Option<Value> {
    let live_bridge::LiveBridgeEndpoint::UnixSocket(socket_path) = endpoint else {
        return None;
    };

    if probe.file_kind != Some("socket") {
        return None;
    }

    Some(unix_socket_owner_evidence_json(socket_path))
}

#[cfg(target_os = "linux")]
pub(crate) fn unix_socket_owner_evidence_json(socket_path: &str) -> Value {
    let proc_net_unix_readable = Path::new("/proc/net/unix").exists();
    let socket_inodes = unix_socket_inodes_for_path(socket_path);
    let proc_fd_readable = Path::new("/proc").exists();
    let matching_pids = if socket_inodes.is_empty() {
        Vec::new()
    } else {
        linux_pids_with_socket_inodes(&socket_inodes)
    };
    let owner_process_identified = !matching_pids.is_empty();
    json!({
        "status": if owner_process_identified {
            "owner_process_identified"
        } else if !socket_inodes.is_empty() {
            "socket_listed_without_identified_owner"
        } else {
            "no_socket_owner_evidence"
        },
        "nonMutating": true,
        "source": "procfs",
        "socketListedInProcNetUnix": !socket_inodes.is_empty(),
        "ownerProcessIdentified": owner_process_identified,
        "matchingPids": matching_pids,
        "procNetUnixReadable": proc_net_unix_readable,
        "procFdReadable": proc_fd_readable,
    })
}

#[cfg(not(target_os = "linux"))]
pub(crate) fn unix_socket_owner_evidence_json(_socket_path: &str) -> Value {
    json!({
        "status": "unsupported_platform",
        "nonMutating": true,
        "source": "none",
        "socketListedInProcNetUnix": Value::Null,
        "ownerProcessIdentified": Value::Null,
        "matchingPids": [],
        "procNetUnixReadable": false,
        "procFdReadable": false,
    })
}

#[cfg(target_os = "linux")]
pub(crate) fn unix_socket_inodes_for_path(socket_path: &str) -> Vec<String> {
    let Ok(raw) = fs::read_to_string("/proc/net/unix") else {
        return Vec::new();
    };
    raw.lines()
        .skip(1)
        .filter_map(|line| {
            let mut fields = line.split_whitespace();
            let _num = fields.next()?;
            let _ref_count = fields.next()?;
            let _protocol = fields.next()?;
            let _flags = fields.next()?;
            let _type = fields.next()?;
            let _state = fields.next()?;
            let inode = fields.next()?;
            let path = fields.next()?;
            (path == socket_path).then(|| inode.to_string())
        })
        .collect()
}

#[cfg(target_os = "linux")]
pub(crate) fn linux_pids_with_socket_inodes(socket_inodes: &[String]) -> Vec<u32> {
    let inode_refs = socket_inodes
        .iter()
        .map(|inode| format!("socket:[{inode}]"))
        .collect::<Vec<_>>();
    let Ok(proc_entries) = fs::read_dir("/proc") else {
        return Vec::new();
    };
    let mut pids = Vec::new();
    for entry in proc_entries.flatten() {
        let file_name = entry.file_name();
        let Some(file_name) = file_name.to_str() else {
            continue;
        };
        let Ok(pid) = file_name.parse::<u32>() else {
            continue;
        };
        let fd_dir = entry.path().join("fd");
        let Ok(fd_entries) = fs::read_dir(fd_dir) else {
            continue;
        };
        let mut matched = false;
        for fd_entry in fd_entries.flatten() {
            let Ok(target) = fs::read_link(fd_entry.path()) else {
                continue;
            };
            let target = target.to_string_lossy();
            if inode_refs
                .iter()
                .any(|inode_ref| target == inode_ref.as_str())
            {
                matched = true;
                break;
            }
        }
        if matched {
            pids.push(pid);
        }
    }
    pids.sort_unstable();
    pids.dedup();
    pids
}

pub(crate) fn bridge_health_manifest_json(context: AppContext<'_>) -> Value {
    let expected = bridge::bridge_version()
        .strip_prefix("spirectl-bridge/")
        .unwrap_or(bridge::bridge_version());
    let mod_dir = if let Some(mods_dir) = context.config.game.mods_dir.as_ref() {
        PathBuf::from(mods_dir).join(live_bridge::DEPLOYED_MOD_DIR_NAME)
    } else if !context.config.game.path.is_empty() && context.config.game.path != "auto" {
        PathBuf::from(&context.config.game.path)
            .join("mods")
            .join(live_bridge::DEPLOYED_MOD_DIR_NAME)
    } else {
        return json!({
            "status": "unknown",
            "path": Value::Null,
            "expectedVersion": expected,
            "version": Value::Null
        });
    };
    let path = mod_dir.join("spirectlbridge.json");
    let raw = match fs::read_to_string(&path) {
        Ok(raw) => raw,
        Err(_) => {
            return json!({
                "status": "not_found",
                "path": path.display().to_string(),
                "expectedVersion": expected,
                "version": Value::Null
            });
        }
    };
    let parsed = serde_json::from_str::<Value>(&raw).ok();
    let version = parsed
        .as_ref()
        .and_then(|value| value.get("version"))
        .and_then(Value::as_str);
    let build_identity = parsed
        .as_ref()
        .and_then(|value| value.get("buildIdentity"))
        .cloned()
        .unwrap_or(Value::Null);
    json!({
        "status": match version {
            Some(value) if value == expected => "current",
            Some(_) => "version_mismatch",
            None => "unknown",
        },
        "path": path.display().to_string(),
        "expectedVersion": expected,
        "version": version,
        "buildIdentity": build_identity
    })
}

pub(crate) fn bridge_health_next_commands(status: &str) -> Vec<&'static str> {
    match status {
        "reachable_current" => vec!["sts2 --json state"],
        "stale_live_host" => {
            vec![
                "sts2 game install-bridge",
                "sts2 game bridge-health --repair-stale-endpoint",
                "sts2 game attach",
                "sts2 game launch",
                "sts2 --json game bridge-health",
            ]
        }
        "deployed_version_mismatch" | "live_version_mismatch" => vec![
            "sts2 game install-bridge",
            "sts2 game close",
            "sts2 game launch",
        ],
        "endpoint_missing"
        | "endpoint_refused_or_stale"
        | "endpoint_permission_denied"
        | "endpoint_ambiguous"
        | "rpc_timeout"
        | "unreachable_bridge" => {
            vec![
                "sts2 game bridge-health --repair-stale-endpoint",
                "sts2 game attach",
                "sts2 game launch",
                "sts2 --json game bridge-health",
            ]
        }
        _ => vec!["sts2 game detect"],
    }
}

pub(crate) fn bridge_health_local_bridge_json(full_scan: bool) -> Value {
    let source_freshness = bridge_source_freshness_json(full_scan);
    json!({
        "bridgeVersion": bridge::bridge_version(),
        "buildIdentity": {
            "bridgeSemVer": bridge::bridge_version()
                .strip_prefix("spirectl-bridge/")
                .unwrap_or(bridge::bridge_version()),
            "bridgeVersion": bridge::bridge_version(),
            "sourceFreshness": source_freshness
        },
        "sourceFreshness": source_freshness
    })
}

pub(crate) fn bridge_health_live_is_stale(
    handshake: &bridge::proto::HandshakeResponse,
    local_bridge: &Value,
) -> bool {
    let Some(live_identity) = handshake.build_identity.as_ref() else {
        return false;
    };
    let Some(live_seconds) = parse_bridge_freshness_seconds(&live_identity.built_at_utc) else {
        return false;
    };
    let Some(local_seconds) = local_bridge["sourceFreshness"]["newestModifiedUnixSeconds"].as_i64()
    else {
        return false;
    };
    live_seconds < local_seconds
}

/// Three files that change on essentially every bridge or contract edit. Statting them is O(3)
/// where the full walk is O(bridge-mod/src + proto), which `bridge-health` used to pay on every
/// call — including the ones a polling loop makes.
pub(crate) const BRIDGE_SOURCE_SENTINELS: &[&str] = &[
    "bridge-mod/Directory.Build.props",
    "bridge-mod/src/Spirectl.BridgeMod/BridgeRuntime.cs",
    "proto/spirectl/v0/runtime.proto",
];

pub(crate) fn bridge_source_freshness_json(full_scan: bool) -> Value {
    let scan_mode = if full_scan { "full" } else { "sentinel" };
    let Some(repo_root) = bridge_health_repo_root() else {
        return json!({
            "status": "unknown",
            "scanMode": scan_mode,
            "newestModifiedUnixSeconds": Value::Null,
            "newestPath": Value::Null
        });
    };

    let mut newest: Option<(u64, PathBuf)> = None;
    if full_scan {
        for root in [
            repo_root.join("bridge-mod/src"),
            repo_root.join("bridge-mod/Directory.Build.props"),
            repo_root.join("proto/spirectl"),
        ] {
            collect_bridge_source_freshness(&root, &mut newest);
        }
    } else {
        for sentinel in BRIDGE_SOURCE_SENTINELS {
            collect_bridge_source_freshness(&repo_root.join(sentinel), &mut newest);
        }
    }

    match newest {
        Some((seconds, path)) => json!({
            "status": "known",
            "scanMode": scan_mode,
            "newestModifiedUnixSeconds": seconds,
            "newestPath": path.display().to_string(),
            "note": bridge_source_freshness_note(full_scan)
        }),
        None => json!({
            "status": "unknown",
            "scanMode": scan_mode,
            "newestModifiedUnixSeconds": Value::Null,
            "newestPath": Value::Null,
            "note": bridge_source_freshness_note(full_scan)
        }),
    }
}

fn bridge_source_freshness_note(full_scan: bool) -> &'static str {
    if full_scan {
        "Full walk of bridge-mod/src and proto/spirectl; authoritative for stale-host detection."
    } else {
        "Sentinel stat only. An edit to a bridge source file outside the sentinels will not be seen here; re-run with --check-source (or --verbose) for the authoritative answer."
    }
}

/// Find the repository root at runtime rather than at compile time. `CARGO_MANIFEST_DIR` bakes in
/// the path of whatever checkout produced the binary, so a `sts2` built elsewhere (a worktree, an
/// installed release binary) used to report a different tree's freshness — or none at all.
pub(crate) fn bridge_health_repo_root() -> Option<PathBuf> {
    let bases = [
        std::env::current_dir().ok(),
        std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(Path::to_path_buf)),
    ];
    for base in bases.into_iter().flatten() {
        for ancestor in base.ancestors() {
            if ancestor.join("bridge-mod/Directory.Build.props").is_file() {
                return Some(ancestor.to_path_buf());
            }
        }
    }

    // Last resort: the checkout this binary was compiled from.
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .map(Path::to_path_buf)
        .filter(|root| root.join("bridge-mod/Directory.Build.props").is_file())
}

pub(crate) fn collect_bridge_source_freshness(path: &Path, newest: &mut Option<(u64, PathBuf)>) {
    let Ok(metadata) = fs::metadata(path) else {
        return;
    };
    if metadata.is_dir() {
        let Ok(entries) = fs::read_dir(path) else {
            return;
        };
        for entry in entries.filter_map(Result::ok) {
            let child = entry.path();
            if child
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| matches!(name, "bin" | "obj" | "target"))
            {
                continue;
            }
            collect_bridge_source_freshness(&child, newest);
        }
        return;
    }
    if !is_bridge_source_input(path) {
        return;
    }
    let Some(seconds) = metadata
        .modified()
        .ok()
        .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .map(|duration| duration.as_secs())
    else {
        return;
    };
    if newest
        .as_ref()
        .is_none_or(|(current_seconds, _)| seconds > *current_seconds)
    {
        *newest = Some((seconds, path.to_path_buf()));
    }
}

pub(crate) fn is_bridge_source_input(path: &Path) -> bool {
    matches!(
        path.extension().and_then(|extension| extension.to_str()),
        Some("cs" | "csproj" | "props" | "proto")
    )
}

pub(crate) fn parse_bridge_freshness_seconds(value: &str) -> Option<i64> {
    let trimmed = value.trim();
    if trimmed.is_empty() || trimmed == "unknown" || trimmed == "mock" {
        return None;
    }
    if let Ok(seconds) = trimmed.parse::<i64>() {
        return Some(seconds);
    }
    parse_rfc3339_utc_seconds(trimmed)
}

pub(crate) fn parse_rfc3339_utc_seconds(value: &str) -> Option<i64> {
    let value = value.strip_suffix('Z')?;
    let (date, time) = value.split_once('T')?;
    let mut date_parts = date.split('-');
    let year = date_parts.next()?.parse::<i32>().ok()?;
    let month = date_parts.next()?.parse::<u32>().ok()?;
    let day = date_parts.next()?.parse::<u32>().ok()?;
    if date_parts.next().is_some() {
        return None;
    }
    let time = time.split_once('.').map_or(time, |(prefix, _)| prefix);
    let mut time_parts = time.split(':');
    let hour = time_parts.next()?.parse::<u32>().ok()?;
    let minute = time_parts.next()?.parse::<u32>().ok()?;
    let second = time_parts.next()?.parse::<u32>().ok()?;
    if time_parts.next().is_some()
        || !(1..=12).contains(&month)
        || !(1..=31).contains(&day)
        || hour > 23
        || minute > 59
        || second > 60
    {
        return None;
    }
    let days = days_from_civil(year, month, day);
    Some(days * 86_400 + i64::from(hour * 3_600 + minute * 60 + second))
}

pub(crate) fn days_from_civil(year: i32, month: u32, day: u32) -> i64 {
    let year = year - i32::from(month <= 2);
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let yoe = year - era * 400;
    let month = month as i32;
    let day = day as i32;
    let doy = (153 * (month + if month > 2 { -3 } else { 9 }) + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    i64::from(era * 146_097 + doe - 719_468)
}
