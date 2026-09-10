#[derive(Debug, Clone)]
struct DebugEventsStepExecution {
    args: DebugEventsArgs,
    expect_event_kind: Option<String>,
}

fn parse_step(raw_step: RawStep) -> Result<ScenarioStep, StepLoadError> {
    let (requested_id, args_value) = match raw_step {
        RawStep::Bare(step_id) => (step_id, serde_yaml::Value::Null),
        RawStep::Map(fields) => {
            if fields.len() != 1 {
                return Err(StepLoadError {
                    requested_id: "<invalid>".to_string(),
                    canonical_id: "<invalid>".to_string(),
                    input: Value::Null,
                    error: runner_error(
                        "invalid_step_shape",
                        "Each scenario step must be a bare string or a single-key mapping.",
                    ),
                });
            }

            let (step_id, value) = fields.into_iter().next().expect("single-key map");
            (step_id, value)
        }
    };

    let canonical_id = canonical_step_id(&requested_id);
    match canonical_id.as_str() {
        "game.info" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::GameInfo,
        ),
        "game.deploy" => parse_game_deploy_step(requested_id, canonical_id, args_value),
        "dev.logs" => parse_logs_step(requested_id, canonical_id, args_value),
        "dev.log-health" => parse_log_health_step(requested_id, canonical_id, args_value),
        "dev.console" => parse_console_step(requested_id, canonical_id, args_value),
        "dev.diagnostics" => parse_diagnostics_step(requested_id, canonical_id, args_value),
        "dev.hot-reload" => parse_hot_reload_step(requested_id, canonical_id, args_value),
        "dev.delay" => parse_delay_step(requested_id, canonical_id, args_value),
        "dev.assert" => parse_assert_step(requested_id, canonical_id, args_value),
        "dev.http" => parse_http_step(requested_id, canonical_id, args_value),
        "dev.http-wait" => parse_http_wait_step(requested_id, canonical_id, args_value),
        "dev.fetch" => parse_fetch_step(requested_id, canonical_id, args_value),
        "dev.websocket" => parse_websocket_step(requested_id, canonical_id, args_value),
        "dev.debug-events" => parse_debug_events_step(requested_id, canonical_id, args_value),
        "project.hook" => parse_project_hook_step(requested_id, canonical_id, args_value),
        "dev.wait-for" => parse_wait_for_step(requested_id, canonical_id, args_value),
        "dev.load-fixture" | "dev.fixture.load" => {
            parse_load_fixture_step(requested_id, "dev.load-fixture".to_string(), args_value)
        }
        "dev.load-scenario" => parse_load_scenario_step(requested_id, canonical_id, args_value),
        "dev.screenshot" => parse_screenshot_step(requested_id, canonical_id, args_value),
        "dev.screenshot-diff" => parse_screenshot_diff_step(requested_id, canonical_id, args_value),
        "dev.snapshot-compare" => {
            parse_snapshot_compare_step(requested_id, canonical_id, args_value)
        }
        "act.play-card" => parse_play_card_step(requested_id, canonical_id, args_value),
        "act.use-potion" => parse_use_potion_step(requested_id, canonical_id, args_value),
        "act.fallback-choose" => parse_choose_step(requested_id, canonical_id, args_value),
        "act.confirm-selection" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActConfirmSelection,
        ),
        "act.cancel-selection" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActCancelSelection,
        ),
        "act.select-map-node" => parse_select_map_node_step(requested_id, canonical_id, args_value),
        "act.end-turn" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActEndTurn,
        ),
        "act.ready" => parse_player_scoped_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActReady,
        ),
        "act.unready" => parse_player_scoped_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActUnready,
        ),
        "act.select-character" => {
            parse_select_character_step(requested_id, canonical_id, args_value)
        }
        "act.claim-reward" => parse_reward_step(requested_id, canonical_id, args_value),
        "act.skip-rewards" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActSkipRewards,
        ),
        "act.select-card" => parse_card_step(requested_id, canonical_id, args_value),
        "act.skip-card-selection" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActSkipCardSelection,
        ),
        "act.select-bundle" => parse_bundle_step(requested_id, canonical_id, args_value),
        "act.buy-card" => parse_shop_item_step(requested_id, canonical_id, args_value, "buy-card"),
        "act.buy-relic" => {
            parse_shop_item_step(requested_id, canonical_id, args_value, "buy-relic")
        }
        "act.buy-potion" => {
            parse_shop_item_step(requested_id, canonical_id, args_value, "buy-potion")
        }
        "act.remove-card" => {
            parse_shop_item_step(requested_id, canonical_id, args_value, "remove-card")
        }
        "act.leave-shop" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActLeaveShop,
        ),
        "act.close-shop-inventory" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActCloseShopInventory,
        ),
        "act.rest" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActRest,
        ),
        "act.smith" => parse_smith_step(requested_id, canonical_id, args_value),
        "act.use-rest-site-option" => {
            parse_rest_site_option_step(requested_id, canonical_id, args_value)
        }
        "act.proceed-rest-site" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActProceedRestSite,
        ),
        "act.open-chest" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActOpenChest,
        ),
        "act.take-relic" => parse_relic_step(requested_id, canonical_id, args_value),
        "act.proceed-treasure-room" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActProceedTreasureRoom,
        ),
        "act.back-from-map" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActBackFromMap,
        ),
        "act.select-event-option" => parse_event_option_step(
            requested_id,
            canonical_id,
            args_value,
            "select-event-option",
        ),
        "act.open-event-shop" => {
            parse_event_option_step(requested_id, canonical_id, args_value, "open-event-shop")
        }
        "act.use-crystal-sphere-control" => {
            parse_crystal_sphere_control_step(requested_id, canonical_id, args_value)
        }
        "act.proceed-event" => parse_no_arg_step(
            requested_id,
            canonical_id,
            args_value,
            StepExecution::ActProceedEvent,
        ),
        "state" => Err(legacy_state_expect_error(
            requested_id,
            canonical_id,
            args_value,
        )),
        _ => Err(unsupported_step_error(
            requested_id,
            canonical_id,
            args_value,
            "This step is not part of the supported current runner vocabulary. Use modeled action steps such as act.play-card, act.use-potion, act.select-map-node, act.end-turn, act.ready, act.unready, act.select-character, act.confirm-selection, or act.cancel-selection when available; use act.fallback-choose only for generic, modded, or unmodeled visible choices.",
        )),
    }
}

fn parse_no_arg_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
    execution: StepExecution,
) -> Result<ScenarioStep, StepLoadError> {
    if !matches!(args_value, serde_yaml::Value::Null) && !is_empty_mapping(&args_value) {
        return Err(StepLoadError {
            requested_id,
            canonical_id,
            input: yaml_to_json(&args_value),
            error: runner_error(
                "invalid_step_args",
                "This step does not accept any arguments.",
            ),
        });
    }

    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: Value::Null,
        execution,
    })
}

fn parse_player_scoped_no_arg_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
    build: fn(PlayerScopedActionArgs) -> StepExecution,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<PlayerScopedStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = PlayerScopedActionArgs {
        player_id: raw.player_id.clone(),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "playerId": raw.player_id }),
        execution: build(args),
    })
}

fn parse_logs_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<LogsStepArgs>(&requested_id, &canonical_id, args_value)?;
    let level = raw
        .level
        .as_deref()
        .map(parse_log_level)
        .transpose()
        .map_err(|message| StepLoadError {
            requested_id: requested_id.clone(),
            canonical_id: canonical_id.clone(),
            input: Value::Null,
            error: runner_error("invalid_step_args", &message),
        })?;

    let args = LogsArgs {
        source: crate::LogSourceArg::Bridge,
        limit: raw.limit.unwrap_or(50),
        tail: raw.tail,
        after_cursor: raw.after_cursor,
        follow: false,
        level,
        target: raw.target.clone(),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "limit": args.limit,
            "tail": args.tail,
            "afterCursor": args.after_cursor,
            "level": raw.level,
            "target": args.target,
        }),
        execution: StepExecution::DevLogs(args),
    })
}

fn parse_log_health_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<LogHealthStepArgs>(&requested_id, &canonical_id, args_value)?;
    let level = raw
        .level
        .as_deref()
        .map(parse_log_level)
        .transpose()
        .map_err(|message| StepLoadError {
            requested_id: requested_id.clone(),
            canonical_id: canonical_id.clone(),
            input: Value::Null,
            error: runner_error("invalid_step_args", &message),
        })?;

    let args = LogHealthArgs {
        limit: raw.limit.unwrap_or(50),
        tail: raw.tail,
        after_cursor: raw.after_cursor,
        level,
        target: raw.target.clone(),
        exclude_targets: raw.exclude_targets.unwrap_or_default(),
        exclude_message_regexes: raw.exclude_message_regexes.unwrap_or_default(),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "limit": args.limit,
            "tail": args.tail,
            "afterCursor": args.after_cursor,
            "level": raw.level,
            "target": args.target,
            "excludeTargets": args.exclude_targets,
            "excludeMessageRegexes": args.exclude_message_regexes,
        }),
        execution: StepExecution::DevLogHealth(args),
    })
}

fn parse_console_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ConsoleStepExecution>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "command": raw.command,
            "args": raw.args,
        }),
        execution: StepExecution::DevConsole(raw),
    })
}

fn parse_diagnostics_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<DiagnosticsStepArgs>(&requested_id, &canonical_id, args_value)?;
    let level = raw
        .level
        .as_deref()
        .map(parse_log_level)
        .transpose()
        .map_err(|message| StepLoadError {
            requested_id: requested_id.clone(),
            canonical_id: canonical_id.clone(),
            input: Value::Null,
            error: runner_error("invalid_step_args", &message),
        })?;

    let args = DiagnosticsArgs {
        limit: raw.limit.unwrap_or(100),
        tail: raw.tail,
        after_cursor: raw.after_cursor,
        level,
        target: raw.target.clone(),
        exclude_targets: raw.exclude_targets.unwrap_or_default(),
        exclude_message_regexes: raw.exclude_message_regexes.unwrap_or_default(),
        preset: raw.preset.clone(),
        width: raw.width,
        height: raw.height,
        preset_catalogs: raw.preset_catalogs.unwrap_or_default(),
        hot_reload_project: None,
        bundle_dir: raw.bundle_dir,
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "limit": args.limit,
            "tail": args.tail,
            "afterCursor": args.after_cursor,
            "level": raw.level,
            "target": args.target,
            "excludeTargets": args.exclude_targets,
            "excludeMessageRegexes": args.exclude_message_regexes,
            "preset": args.preset,
            "width": args.width,
            "height": args.height,
            "bundleDir": args.bundle_dir.as_ref().map(|path| path.display().to_string()),
        }),
        execution: StepExecution::DevDiagnostics(args),
    })
}

fn parse_hot_reload_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<HotReloadStepArgs>(&requested_id, &canonical_id, args_value)?;
    if raw.expect_generation_changed && !raw.wait {
        return Err(StepLoadError {
            requested_id,
            canonical_id,
            input: json!({
                "project": raw.project.display().to_string(),
                "expectGenerationChanged": raw.expect_generation_changed,
                "wait": raw.wait,
            }),
            error: runner_error(
                "invalid_step_args",
                "dev.hot-reload expectGenerationChanged requires wait: true.",
            ),
        });
    }

    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "project": raw.project.display().to_string(),
            "build": raw.build,
            "wait": raw.wait,
            "timeoutMs": raw.timeout_ms.unwrap_or(30_000),
            "intervalMs": raw.interval_ms.unwrap_or(250),
            "expectGenerationChanged": raw.expect_generation_changed,
        }),
        execution: StepExecution::DevHotReload(HotReloadStepExecution {
            project: raw.project,
            build: raw.build,
            wait: raw.wait,
            timeout_ms: raw.timeout_ms.unwrap_or(30_000),
            interval_ms: raw.interval_ms.unwrap_or(250),
            expect_generation_changed: raw.expect_generation_changed,
        }),
    })
}

fn parse_delay_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<DelayStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "ms": raw.ms,
        }),
        execution: StepExecution::DevDelay(DelayStepExecution { ms: raw.ms }),
    })
}

fn parse_game_deploy_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<DeployStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = DeployArgs {
        path: raw.path.clone(),
        build: raw.build,
        restart: raw.restart,
        verify: raw.verify,
        timeout_ms: raw.timeout_ms.unwrap_or(30_000),
        interval_ms: raw.interval_ms.unwrap_or(250),
        rpc_timeout_ms: raw.rpc_timeout_ms.unwrap_or(5_000),
        verify_stable_ms: raw.verify_stable_ms.unwrap_or(0),
        allow_stale_build: raw.allow_stale_build,
        wait_quiescent_ms: raw.wait_quiescent_ms.unwrap_or(0),
        quiescent_stable_samples: raw.quiescent_stable_samples.unwrap_or(3),
        require_quiescent: raw.require_quiescent,
        launch_args: raw.launch_args.clone(),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "path": raw.path,
            "build": args.build,
            "restart": args.restart,
            "verify": args.verify,
            "timeoutMs": args.timeout_ms,
            "intervalMs": args.interval_ms,
            "rpcTimeoutMs": args.rpc_timeout_ms,
            "verifyStableMs": args.verify_stable_ms,
            "allowStaleBuild": args.allow_stale_build,
            "waitQuiescentMs": args.wait_quiescent_ms,
            "quiescentStableSamples": args.quiescent_stable_samples,
            "requireQuiescent": args.require_quiescent,
            "launchArgs": args.launch_args,
        }),
        execution: StepExecution::GameDeploy(args),
    })
}

fn parse_assert_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<AssertStepArgs>(&requested_id, &canonical_id, args_value)?;
    let parsed = parse_assertion_args(&requested_id, &canonical_id, raw, false)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: parsed.input,
        execution: StepExecution::DevAssert(parsed.assert),
    })
}

fn parse_wait_for_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<WaitForStepArgs>(&requested_id, &canonical_id, args_value)?;
    let parsed = parse_assertion_args(&requested_id, &canonical_id, raw.assert, true)?;
    let timeout_ms = raw.timeout_ms.unwrap_or(5_000);
    let interval_ms = raw.interval_ms.unwrap_or(100);
    let rpc_timeout_ms = raw
        .rpc_timeout_ms
        .unwrap_or_else(|| crate::DEFAULT_BRIDGE_RPC_TIMEOUT_MS.min(timeout_ms.max(1)));
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: parsed
            .input
            .as_object()
            .cloned()
            .map(|mut input| {
                input.insert("timeoutMs".to_string(), json!(timeout_ms));
                input.insert("intervalMs".to_string(), json!(interval_ms));
                input.insert("rpcTimeoutMs".to_string(), json!(rpc_timeout_ms));
                Value::Object(input)
            })
            .unwrap_or_else(|| {
                json!({
                    "timeoutMs": timeout_ms,
                    "intervalMs": interval_ms,
                    "rpcTimeoutMs": rpc_timeout_ms,
                })
            }),
        execution: StepExecution::DevWaitFor(WaitForExecution {
            path: parsed.assert.path,
            predicate: parsed.assert.predicate,
            source: parsed.assert.source,
            perspective: parsed.assert.perspective,
            player_id: parsed.assert.player_id,
            timeout_ms,
            interval_ms,
            rpc_timeout_ms,
        }),
    })
}

fn parse_http_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<HttpStepArgs>(&requested_id, &canonical_id, args_value)?;
    let query = parse_probe_query(&requested_id, &canonical_id, &raw.query)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: probe_step_input(
            &raw.query,
            json!({
                "url": raw.url,
                "method": raw.method.clone().unwrap_or_else(|| "GET".to_string()),
                "headers": raw.headers,
                "body": raw.body,
                "timeoutMs": raw.timeout_ms.unwrap_or(5_000),
                "expectStatus": raw.expect_status,
                "expectHeaders": raw.expect_headers,
            }),
        ),
        execution: StepExecution::DevHttp(HttpProbe {
            url: raw.url,
            method: raw.method.unwrap_or_else(|| "GET".to_string()),
            headers: raw.headers.unwrap_or_default(),
            body: raw.body,
            timeout_ms: raw.timeout_ms.unwrap_or(5_000),
            expect_status: raw.expect_status,
            expect_headers: raw.expect_headers.unwrap_or_default(),
            query,
        }),
    })
}

fn parse_http_wait_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<HttpWaitStepArgs>(&requested_id, &canonical_id, args_value)?;
    let query = parse_probe_query(&requested_id, &canonical_id, &raw.query)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: probe_step_input(
            &raw.query,
            json!({
                "url": raw.url,
                "method": raw.method.clone().unwrap_or_else(|| "GET".to_string()),
                "headers": raw.headers,
                "body": raw.body,
                "timeoutMs": raw.timeout_ms.unwrap_or(5_000),
                "intervalMs": raw.interval_ms.unwrap_or(100),
                "expectStatus": raw.expect_status,
                "expectHeaders": raw.expect_headers,
            }),
        ),
        execution: StepExecution::DevHttpWait(HttpWaitProbe {
            http: HttpProbe {
                url: raw.url,
                method: raw.method.unwrap_or_else(|| "GET".to_string()),
                headers: raw.headers.unwrap_or_default(),
                body: raw.body,
                timeout_ms: raw.timeout_ms.unwrap_or(5_000),
                expect_status: raw.expect_status,
                expect_headers: raw.expect_headers.unwrap_or_default(),
                query,
            },
            interval_ms: raw.interval_ms.unwrap_or(100),
        }),
    })
}

fn parse_fetch_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<FetchStepArgs>(&requested_id, &canonical_id, args_value)?;
    let query = parse_probe_query(&requested_id, &canonical_id, &raw.query)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: probe_step_input(
            &raw.query,
            json!({
                "source": raw.source,
                "output": raw.output,
                "expectSha256": raw.expect_sha256,
            }),
        ),
        execution: StepExecution::DevFetch(FetchProbe {
            source: raw.source,
            output: raw.output,
            expect_sha256: raw.expect_sha256,
            query,
        }),
    })
}

fn parse_websocket_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<WebsocketStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "url": raw.url,
            "headers": raw.headers,
            "sendText": raw.send_text,
            "expectText": raw.expect_text,
            "timeoutMs": raw.timeout_ms.unwrap_or(5_000),
        }),
        execution: StepExecution::DevWebsocket(WebsocketProbe {
            url: raw.url,
            headers: raw.headers.unwrap_or_default(),
            send_text: raw.send_text.unwrap_or_default(),
            expect_text: raw.expect_text.unwrap_or_default(),
            timeout_ms: raw.timeout_ms.unwrap_or(5_000),
        }),
    })
}

fn parse_debug_events_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<DebugEventsStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = DebugEventsArgs {
        session: raw.session.clone(),
        from_sequence: raw.from_sequence.unwrap_or(0),
        limit: raw.limit.unwrap_or(100),
        follow: raw.follow,
        timeout_ms: raw.timeout_ms.unwrap_or(5_000),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "session": args.session,
            "fromSequence": args.from_sequence,
            "limit": args.limit,
            "follow": args.follow,
            "timeoutMs": args.timeout_ms,
            "expectEventKind": raw.expect_event_kind,
        }),
        execution: StepExecution::DevDebugEvents(DebugEventsStepExecution {
            args,
            expect_event_kind: raw.expect_event_kind,
        }),
    })
}

fn parse_project_hook_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ProjectHookStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "name": raw.name,
            "input": raw.input,
        }),
        execution: StepExecution::ProjectHook(ProjectHookExecution {
            name: raw.name,
            input: raw.input,
        }),
    })
}

fn parse_screenshot_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ScreenshotStepArgs>(&requested_id, &canonical_id, args_value)?;
    let input = json!({
        "preset": raw.preset,
        "width": raw.width,
        "height": raw.height,
        "presetCatalogs": raw.preset_catalogs,
        "output": raw.output,
        "rpcTimeoutMs": raw.rpc_timeout_ms,
    });
    let rpc_timeout_ms = raw.rpc_timeout_ms.unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input,
        execution: StepExecution::DevScreenshot(ScreenshotArgs {
            preset: raw.preset,
            width: raw.width,
            height: raw.height,
            preset_catalogs: raw.preset_catalogs.unwrap_or_default(),
            output: raw.output,
            rpc_timeout_ms,
        }),
    })
}

fn parse_screenshot_diff_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ScreenshotDiffStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = ScreenshotDiffArgs {
        baseline: raw.baseline.clone(),
        actual: raw.actual.clone(),
        preset: raw.preset.clone(),
        width: raw.width,
        height: raw.height,
        preset_catalogs: raw.preset_catalogs.clone().unwrap_or_default(),
        bundle_dir: raw.bundle_dir.clone(),
        max_diff_pixels: raw.max_diff_pixels,
        max_diff_ratio: raw.max_diff_ratio,
        pixel_tolerance: raw.pixel_tolerance,
        ignore_alpha: raw.ignore_alpha.unwrap_or(false),
        mask: raw.mask.clone(),
        regions: raw.regions.clone(),
        foreground_max_diff_ratio: raw.foreground_max_diff_ratio,
        roi_max_diff_ratio: raw.roi_max_diff_ratio,
        required_comparisons: raw.required_comparisons.clone().unwrap_or_default(),
        rpc_timeout_ms: raw.rpc_timeout_ms.unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "baseline": raw.baseline,
            "actual": raw.actual,
            "preset": raw.preset,
            "width": raw.width,
            "height": raw.height,
            "presetCatalogs": raw.preset_catalogs,
            "bundleDir": raw.bundle_dir,
            "maxDiffPixels": raw.max_diff_pixels,
            "maxDiffRatio": raw.max_diff_ratio,
            "pixelTolerance": raw.pixel_tolerance,
            "ignoreAlpha": raw.ignore_alpha,
            "mask": raw.mask,
            "regions": raw.regions,
            "foregroundMaxDiffRatio": raw.foreground_max_diff_ratio,
            "roiMaxDiffRatio": raw.roi_max_diff_ratio,
            "requiredComparisons": raw.required_comparisons,
            "rpcTimeoutMs": raw.rpc_timeout_ms,
        }),
        execution: StepExecution::DevScreenshotDiff(args),
    })
}

fn parse_load_fixture_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<LoadFixtureStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = LoadFixtureArgs::from_path(raw.path.clone());
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "path": raw.path,
        }),
        execution: StepExecution::DevLoadFixture(args),
    })
}

fn parse_load_scenario_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<LoadScenarioStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = ScenarioLoadArgs {
        path: Some(raw.path.clone()),
        path_flag: None,
        restart: raw.restart,
        timeout_ms: raw.timeout_ms.unwrap_or(30_000),
        interval_ms: raw.interval_ms.unwrap_or(250),
        allow_degraded_local_multiplayer: raw.allow_degraded_local_multiplayer,
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "path": raw.path,
            "restart": raw.restart,
            "timeoutMs": raw.timeout_ms,
            "intervalMs": raw.interval_ms,
            "allowDegradedLocalMultiplayer": raw.allow_degraded_local_multiplayer,
        }),
        execution: StepExecution::DevLoadScenario(args),
    })
}

fn parse_snapshot_compare_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<SnapshotCompareStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = SnapshotCompareArgs {
        spec: raw.spec.clone(),
        baseline: raw.baseline.clone(),
        preset_catalogs: raw.preset_catalogs.clone().unwrap_or_default(),
        bundle_dir: raw.bundle_dir.clone(),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "spec": raw.spec.display().to_string(),
            "baseline": raw.baseline.display().to_string(),
            "presetCatalogs": raw.preset_catalogs.as_ref().map(|paths| paths.iter().map(|path| path.display().to_string()).collect::<Vec<_>>()),
            "bundleDir": raw.bundle_dir.as_ref().map(|path| path.display().to_string()),
        }),
        execution: StepExecution::DevSnapshotCompare(args),
    })
}

fn parse_choose_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ChooseStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "choice": raw.choice,
            "fallback": true,
            "policy": "Use only for generic, modded, or unmodeled visible choices when no modeled preferredAction is advertised.",
        }),
        execution: StepExecution::ActChoose(ChooseArgs {
            choice: raw.choice,
            player_id: None,
        }),
    })
}

fn parse_play_card_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<PlayCardStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "card": raw.card,
            "target": raw.target,
        }),
        execution: StepExecution::ActPlayCard(PlayCardArgs {
            card: raw.card,
            target: raw.target,
            player_id: None,
        }),
    })
}

fn parse_use_potion_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<UsePotionStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({
            "potion": raw.potion,
            "target": raw.target,
        }),
        execution: StepExecution::ActUsePotion(UsePotionArgs {
            potion: raw.potion,
            target: raw.target,
            player_id: None,
        }),
    })
}

fn parse_select_character_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<SelectCharacterStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "character": raw.character }),
        execution: StepExecution::ActSelectCharacter(SelectCharacterArgs {
            character: raw.character,
            player_id: None,
        }),
    })
}

fn parse_select_map_node_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<SelectMapNodeStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "node": raw.node }),
        execution: StepExecution::ActSelectMapNode(SelectMapNodeArgs {
            node: raw.node,
            player_id: None,
        }),
    })
}

fn parse_reward_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<RewardStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "reward": raw.reward }),
        execution: StepExecution::ActClaimReward(RewardActionArgs {
            reward: raw.reward,
            player_id: None,
        }),
    })
}

fn parse_card_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<CardStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "card": raw.card }),
        execution: StepExecution::ActSelectCard(CardActionArgs {
            card: raw.card,
            player_id: None,
            reward: None,
        }),
    })
}

fn parse_bundle_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<BundleStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "bundle": raw.bundle }),
        execution: StepExecution::ActSelectBundle(BundleActionArgs {
            bundle: raw.bundle,
            player_id: None,
        }),
    })
}

fn parse_shop_item_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
    verb: &str,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<ShopItemStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = ShopItemActionArgs {
        shop_item: raw.shop_item,
        player_id: None,
    };
    let execution = match verb {
        "buy-card" => StepExecution::ActBuyCard(args),
        "buy-relic" => StepExecution::ActBuyRelic(args),
        "buy-potion" => StepExecution::ActBuyPotion(args),
        "remove-card" => StepExecution::ActRemoveCard(args),
        _ => unreachable!("validated shop verb"),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "shopItem": match &execution {
            StepExecution::ActBuyCard(args)
            | StepExecution::ActBuyRelic(args)
            | StepExecution::ActBuyPotion(args)
            | StepExecution::ActRemoveCard(args) => &args.shop_item,
            _ => unreachable!("shop item execution"),
        }}),
        execution,
    })
}

fn parse_smith_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<OptionalCardStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "card": raw.card }),
        execution: StepExecution::ActSmith(SmithActionArgs {
            card: raw.card,
            player_id: None,
        }),
    })
}

fn parse_rest_site_option_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<RestSiteOptionStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "restOption": raw.rest_option }),
        execution: StepExecution::ActUseRestSiteOption(RestSiteOptionActionArgs {
            option: raw.rest_option,
            player_id: None,
        }),
    })
}

fn parse_relic_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<RelicStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "relic": raw.relic }),
        execution: StepExecution::ActTakeRelic(TakeRelicActionArgs {
            relic: raw.relic,
            player_id: None,
        }),
    })
}

fn parse_event_option_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
    verb: &str,
) -> Result<ScenarioStep, StepLoadError> {
    let raw = parse_yaml_args::<EventOptionStepArgs>(&requested_id, &canonical_id, args_value)?;
    let args = EventOptionActionArgs {
        event_option: raw.event_option,
        player_id: None,
    };
    let execution = match verb {
        "select-event-option" => StepExecution::ActSelectEventOption(args),
        "open-event-shop" => StepExecution::ActOpenEventShop(args),
        _ => unreachable!("validated event verb"),
    };
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "eventOption": match &execution {
            StepExecution::ActSelectEventOption(args) | StepExecution::ActOpenEventShop(args) =>
                &args.event_option,
            _ => unreachable!("event option execution"),
        }}),
        execution,
    })
}

fn parse_crystal_sphere_control_step(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> Result<ScenarioStep, StepLoadError> {
    let raw =
        parse_yaml_args::<CrystalSphereControlStepArgs>(&requested_id, &canonical_id, args_value)?;
    Ok(ScenarioStep {
        requested_id,
        canonical_id,
        input: json!({ "control": raw.control, "selectedTool": raw.selected_tool }),
        execution: StepExecution::ActUseCrystalSphereControl(CrystalSphereControlActionArgs {
            control: raw.control,
            selected_tool: raw.selected_tool,
            player_id: None,
        }),
    })
}

fn parse_assertion_args(
    requested_id: &str,
    canonical_id: &str,
    raw: AssertStepArgs,
    include_timing_defaults: bool,
) -> Result<ParsedAssertionArgs, StepLoadError> {
    let predicate = parse_predicate(requested_id, canonical_id, &raw)?;
    let perspective = raw
        .perspective
        .as_deref()
        .map(parse_perspective)
        .transpose()
        .map_err(|message| StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error("invalid_step_args", &message),
        })?;
    let source = raw
        .source
        .as_deref()
        .map(parse_source)
        .transpose()
        .map_err(|message| StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error("invalid_step_args", &message),
        })?;

    let mut input = Map::new();
    input.insert("path".to_string(), Value::String(raw.path.clone()));
    insert_predicate_input(&mut input, &predicate);
    if let Some(source_raw) = &raw.source {
        input.insert("source".to_string(), Value::String(source_raw.clone()));
    }
    if let Some(perspective_raw) = raw.perspective {
        input.insert("perspective".to_string(), Value::String(perspective_raw));
    }
    if let Some(player_id) = &raw.player_id {
        input.insert("playerId".to_string(), Value::String(player_id.clone()));
    }
    if include_timing_defaults {
        // timing fields are added by the caller once defaults are known.
    }

    Ok(ParsedAssertionArgs {
        input: Value::Object(input),
        assert: AssertExecution {
            path: raw.path,
            predicate,
            source,
            perspective,
            player_id: raw.player_id,
        },
    })
}

fn parse_predicate(
    requested_id: &str,
    canonical_id: &str,
    raw: &AssertStepArgs,
) -> Result<Predicate, StepLoadError> {
    let mut predicates = Vec::new();
    if let Some(value) = &raw.equals {
        predicates.push(Predicate::Equals(yaml_to_json(value)));
    }
    if let Some(value) = &raw.contains {
        predicates.push(Predicate::Contains(yaml_to_json(value)));
    }
    if let Some(value) = &raw.regex {
        predicates.push(
            Predicate::from_raw_regex(value).map_err(|message| StepLoadError {
                requested_id: requested_id.to_string(),
                canonical_id: canonical_id.to_string(),
                input: Value::Null,
                error: runner_error("invalid_step_args", &message),
            })?,
        );
    }
    if let Some(value) = &raw.gt {
        predicates.push(Predicate::GreaterThan(yaml_to_json(value)));
    }
    if let Some(value) = &raw.gte {
        predicates.push(Predicate::GreaterThanOrEqual(yaml_to_json(value)));
    }
    if let Some(value) = &raw.lt {
        predicates.push(Predicate::LessThan(yaml_to_json(value)));
    }
    if let Some(value) = &raw.lte {
        predicates.push(Predicate::LessThanOrEqual(yaml_to_json(value)));
    }
    if raw.exists {
        predicates.push(Predicate::Exists);
    }
    if raw.not_exists {
        predicates.push(Predicate::NotExists);
    }

    if predicates.len() != 1 {
        return Err(StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error(
                "invalid_step_args",
                "Assertion steps must set exactly one predicate.",
            ),
        });
    }

    Ok(predicates.remove(0))
}

fn parse_yaml_args<T: for<'de> Deserialize<'de>>(
    requested_id: &str,
    canonical_id: &str,
    args_value: serde_yaml::Value,
) -> Result<T, StepLoadError> {
    let input = yaml_to_json(&args_value);
    let value = if matches!(args_value, serde_yaml::Value::Null) {
        serde_yaml::Value::Mapping(Default::default())
    } else {
        args_value
    };

    serde_yaml::from_value(value).map_err(|source| StepLoadError {
        requested_id: requested_id.to_string(),
        canonical_id: canonical_id.to_string(),
        input,
        error: runner_error(
            "invalid_step_args",
            &format!("Failed to parse step arguments: {source}"),
        ),
    })
}

fn insert_predicate_input(map: &mut Map<String, Value>, predicate: &Predicate) {
    match predicate {
        Predicate::Equals(value) => {
            map.insert("equals".to_string(), value.clone());
        }
        Predicate::Contains(value) => {
            map.insert("contains".to_string(), value.clone());
        }
        Predicate::Regex(value) => {
            map.insert("regex".to_string(), value.clone());
        }
        Predicate::GreaterThan(value) => {
            map.insert("gt".to_string(), value.clone());
        }
        Predicate::GreaterThanOrEqual(value) => {
            map.insert("gte".to_string(), value.clone());
        }
        Predicate::LessThan(value) => {
            map.insert("lt".to_string(), value.clone());
        }
        Predicate::LessThanOrEqual(value) => {
            map.insert("lte".to_string(), value.clone());
        }
        Predicate::Exists => {
            map.insert("exists".to_string(), Value::Bool(true));
        }
        Predicate::NotExists => {
            map.insert("notExists".to_string(), Value::Bool(true));
        }
    }
}

fn parse_probe_query(
    requested_id: &str,
    canonical_id: &str,
    raw: &ProbeQueryStepArgs,
) -> Result<Option<ProbeQuery>, StepLoadError> {
    let predicate = parse_probe_predicate(requested_id, canonical_id, raw)?;
    match (raw.query.clone(), predicate) {
        (None, None) => Ok(None),
        (Some(path), Some(predicate)) => Ok(Some(ProbeQuery { path, predicate })),
        (Some(_), None) => Err(StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error(
                "invalid_step_args",
                "Probe query steps require exactly one predicate when query is set.",
            ),
        }),
        (None, Some(_)) => Err(StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error(
                "invalid_step_args",
                "Probe predicates require a query path.",
            ),
        }),
    }
}

fn parse_probe_predicate(
    requested_id: &str,
    canonical_id: &str,
    raw: &ProbeQueryStepArgs,
) -> Result<Option<Predicate>, StepLoadError> {
    let mut predicates = Vec::new();
    if let Some(value) = &raw.equals {
        predicates.push(Predicate::Equals(yaml_to_json(value)));
    }
    if let Some(value) = &raw.contains {
        predicates.push(Predicate::Contains(yaml_to_json(value)));
    }
    if let Some(value) = &raw.regex {
        predicates.push(
            Predicate::from_raw_regex(value).map_err(|message| StepLoadError {
                requested_id: requested_id.to_string(),
                canonical_id: canonical_id.to_string(),
                input: Value::Null,
                error: runner_error("invalid_step_args", &message),
            })?,
        );
    }
    if let Some(value) = &raw.gt {
        predicates.push(Predicate::GreaterThan(yaml_to_json(value)));
    }
    if let Some(value) = &raw.gte {
        predicates.push(Predicate::GreaterThanOrEqual(yaml_to_json(value)));
    }
    if let Some(value) = &raw.lt {
        predicates.push(Predicate::LessThan(yaml_to_json(value)));
    }
    if let Some(value) = &raw.lte {
        predicates.push(Predicate::LessThanOrEqual(yaml_to_json(value)));
    }
    if raw.exists {
        predicates.push(Predicate::Exists);
    }
    if raw.not_exists {
        predicates.push(Predicate::NotExists);
    }

    if predicates.is_empty() {
        Ok(None)
    } else if predicates.len() == 1 {
        Ok(Some(predicates.remove(0)))
    } else {
        Err(StepLoadError {
            requested_id: requested_id.to_string(),
            canonical_id: canonical_id.to_string(),
            input: Value::Null,
            error: runner_error(
                "invalid_step_args",
                "Probe query steps must set exactly one predicate.",
            ),
        })
    }
}

fn probe_step_input(raw: &ProbeQueryStepArgs, base: Value) -> Value {
    let mut input = base.as_object().cloned().unwrap_or_default();
    if let Some(query) = &raw.query {
        input.insert("query".to_string(), Value::String(query.clone()));
    }
    if let Ok(Some(predicate)) = parse_probe_predicate("<input>", "<input>", raw) {
        insert_predicate_input(&mut input, &predicate);
    }
    Value::Object(input)
}

fn canonical_step_id(step_id: &str) -> String {
    let normalized = step_id.trim().to_ascii_lowercase().replace('_', "-");
    match normalized.as_str() {
        "assert.query" => "dev.assert".to_string(),
        "act.choose" | "choose" => "act.fallback-choose".to_string(),
        "dev.debug.events" => "dev.debug-events".to_string(),
        _ => normalized,
    }
}

fn parse_log_level(value: &str) -> Result<LogLevelArg, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "trace" => Ok(LogLevelArg::Trace),
        "debug" => Ok(LogLevelArg::Debug),
        "info" => Ok(LogLevelArg::Info),
        "warn" => Ok(LogLevelArg::Warn),
        "error" => Ok(LogLevelArg::Error),
        other => Err(format!(
            "Unsupported log level '{other}'. Use trace, debug, info, warn, or error."
        )),
    }
}

fn parse_perspective(value: &str) -> Result<PerspectiveScopeArg, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "local" => Ok(PerspectiveScopeArg::Local),
        "omniscient" => Ok(PerspectiveScopeArg::Omniscient),
        other => Err(format!(
            "Unsupported perspective '{other}'. Use local or omniscient."
        )),
    }
}

/// `source: actions` opts a `dev.assert`/`dev.wait-for` step into querying the resolved
/// `spirectl.state-actions/v0` envelope instead of the plain state snapshot. `None` (the
/// step field omitted) keeps the state-snapshot default.
fn parse_source(value: &str) -> Result<StateViewArg, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "actions" => Ok(StateViewArg::Actions),
        other => Err(format!("Unsupported source '{other}'. Use actions.")),
    }
}

fn unsupported_step_error(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
    message: &str,
) -> StepLoadError {
    StepLoadError {
        requested_id,
        canonical_id,
        input: yaml_to_json(&args_value),
        error: runner_error("unsupported_step", message),
    }
}

fn legacy_state_expect_error(
    requested_id: String,
    canonical_id: String,
    args_value: serde_yaml::Value,
) -> StepLoadError {
    let message = if args_value.as_mapping().is_some_and(|mapping| {
        mapping.contains_key(serde_yaml::Value::String("expect".to_string()))
    }) {
        "Legacy state.expect steps are no longer supported; use dev.assert or dev.wait-for steps instead."
    } else {
        "The state step mini-DSL is not supported by M7; use dev.assert or dev.wait-for."
    };

    StepLoadError {
        requested_id,
        canonical_id,
        input: yaml_to_json(&args_value),
        error: runner_error("unsupported_step", message),
    }
}

fn scenario_error_report(
    entry: &DiscoveredScenario,
    name: String,
    description: Option<String>,
    error: Value,
) -> ScenarioReport {
    ScenarioReport {
        path: entry.display_path.clone(),
        name,
        description,
        status: "invalid",
        elapsed_ms: 0,
        exit_code: 2,
        steps: Vec::new(),
        error: Some(error),
        failure: None,
        recovery_attempt: None,
        artifacts: None,
    }
}

fn scenario_invalid_step_report(
    entry: &DiscoveredScenario,
    name: String,
    description: Option<String>,
    index: usize,
    error: StepLoadError,
) -> ScenarioReport {
    ScenarioReport {
        path: entry.display_path.clone(),
        name,
        description,
        status: "invalid",
        elapsed_ms: 0,
        exit_code: 2,
        steps: vec![StepReport {
            index,
            requested_id: error.requested_id,
            canonical_id: error.canonical_id,
            status: "invalid",
            input: error.input,
            output: None,
            error: Some(error.error),
            exit_code: 2,
        }],
        error: None,
        failure: None,
        recovery_attempt: None,
        artifacts: None,
    }
}

fn scenario_display_name(entry: &DiscoveredScenario, fallback: Option<&str>) -> String {
    fallback
        .filter(|value| !value.trim().is_empty())
        .map(ToString::to_string)
        .or_else(|| match &entry.source {
            ScenarioSource::File(path) => path
                .file_stem()
                .and_then(|stem| stem.to_str())
                .map(ToString::to_string),
            ScenarioSource::Inline(_) => None,
        })
        .unwrap_or_else(|| entry.display_path.clone())
}

fn error_payload(error: &AppError) -> Value {
    error
        .payload
        .get("error")
        .cloned()
        .unwrap_or_else(|| error.payload.clone())
}

fn runner_error(code: &str, message: &str) -> Value {
    json!({
        "code": code,
        "message": message
    })
}

fn yaml_to_json(value: &serde_yaml::Value) -> Value {
    serde_json::to_value(value).unwrap_or(Value::Null)
}

fn is_empty_mapping(value: &serde_yaml::Value) -> bool {
    matches!(value, serde_yaml::Value::Mapping(mapping) if mapping.is_empty())
}
