use super::*;
use crate::dotnet_helper::helper_stdout_for_context;
use std::fs;
use std::sync::{Arc, Mutex};
use std::time::Duration;

#[test]
fn parses_state_command_with_global_json() {
    let cli = Cli::parse_from(["sts2", "--json", "state"]);
    assert!(cli.json);
    assert!(matches!(cli.command, Commands::State(_)));
}

#[test]
fn rejects_removed_dev_mode() {
    let error = Cli::try_parse_from(["sts2", "--mode", "dev", "state"])
        .expect_err("dev must not remain a CLI mode");
    assert!(error.to_string().contains("dev"));
}

#[test]
fn parses_remaining_normal_and_dangerous_modes() {
    let normal = Cli::try_parse_from(["sts2", "--mode", "normal", "state"])
        .expect("normal mode should parse");
    assert_eq!(normal.mode, Mode::Normal);

    let dangerous = Cli::try_parse_from([
        "sts2",
        "--mode",
        "dangerous",
        "act",
        "mouse",
        "click",
        "--x",
        "1",
        "--y",
        "1",
    ])
    .expect("dangerous mode should parse");
    assert_eq!(dangerous.mode, Mode::Dangerous);
}

#[test]
fn helper_stdout_for_json_skips_leading_dotnet_noise() {
    let stdout = b"Determining projects to restore...\n$HOME/project.csproj : warning NU1900: Permission denied\n{\"status\":\"ok\",\"command\":\"locate\"}";

    let rendered = helper_stdout_for_context(stdout, true);
    let payload: Value = serde_json::from_str(&rendered).expect("valid helper json");

    assert_eq!(payload["status"], "ok");
    assert_eq!(payload["command"], "locate");
}

#[test]
fn parses_state_command_with_perspective_flags() {
    let cli = Cli::parse_from([
        "sts2",
        "state",
        "--perspective",
        "omniscient",
        "--player-id",
        "p2",
    ]);

    match cli.command {
        Commands::State(args) => {
            assert_eq!(args.perspective, Some(PerspectiveScopeArg::Omniscient));
            assert_eq!(args.player_id.as_deref(), Some("p2"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_nested_action_command() {
    let cli = Cli::parse_from([
        "sts2",
        "act",
        "play-card",
        "--card",
        "c_1",
        "--target",
        "e_1",
    ]);
    assert_eq!(cli.mode, Mode::Normal);
    match cli.command {
        Commands::Act(ActCommand {
            command: ActSubcommand::PlayCard(args),
        }) => {
            assert_eq!(args.card, "c_1");
            assert_eq!(args.target.as_deref(), Some("e_1"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_action_command_with_player_id() {
    let cli = Cli::parse_from([
        "sts2",
        "act",
        "play-card",
        "--card",
        "c_1",
        "--target",
        "e_1",
        "--player-id",
        "p2",
    ]);

    match cli.command {
        Commands::Act(ActCommand {
            command: ActSubcommand::PlayCard(args),
        }) => {
            assert_eq!(args.card, "c_1");
            assert_eq!(args.target.as_deref(), Some("e_1"));
            assert_eq!(args.player_id.as_deref(), Some("p2"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn action_perspective_selector_uses_player_id_when_present() {
    let command = ActSubcommand::EndTurn(PlayerScopedActionArgs {
        player_id: Some("p2".to_string()),
    });

    let perspective = action_perspective_selector(&command).expect("perspective");

    assert_eq!(
        perspective.scope,
        bridge::proto::PerspectiveScope::Local as i32
    );
    assert_eq!(perspective.player_id, "p2");
}

#[test]
fn action_perspective_selector_omits_empty_player_id() {
    let command = ActSubcommand::Ready(PlayerScopedActionArgs::default());

    assert!(action_perspective_selector(&command).is_none());
}

#[test]
fn parses_dev_console_command_and_args() {
    let cli = Cli::parse_from(["sts2", "dev", "console", "draw", "3"]);
    assert_eq!(cli.mode, Mode::Normal);
    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Console(args),
        }) => {
            assert_eq!(args.command, "draw");
            assert_eq!(args.args, vec!["3"]);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_console_args_that_look_like_flags() {
    let cli = Cli::parse_from(["sts2", "dev", "console", "help", "--verbose"]);
    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Console(args),
        }) => {
            assert_eq!(args.command, "help");
            assert_eq!(args.args, vec!["--verbose"]);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn canonical_usage_command_omits_arguments_and_globals() {
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "act",
        "play-card",
        "--card",
        "c_1",
        "--target",
        "e_1",
    ]);

    assert_eq!(canonical_usage_command(&cli.command), "act play-card");

    let launch = Cli::parse_from(["sts2", "game", "launch", "--", "--foo"]);
    assert_eq!(canonical_usage_command(&launch.command), "game launch");
}

#[test]
fn app_config_loads_usage_tracking_defaults_and_overrides() {
    let default_config = AppConfig::default();
    assert!(!default_config.usage_tracking.enabled);
    assert_eq!(default_config.usage_tracking.dir, "./.sts2");

    let config = serde_yaml::from_str::<AppConfig>(
        r#"
usageTracking:
  enabled: true
  dir: ./tmp/usage
"#,
    )
    .expect("yaml config");

    assert!(config.usage_tracking.enabled);
    assert_eq!(config.usage_tracking.dir, "./tmp/usage");
}

#[test]
fn usage_tracking_appends_history_and_updates_stats_without_args() {
    let dir = tempfile::tempdir().expect("temp dir");
    let config = AppConfig {
        usage_tracking: UsageTrackingConfig {
            enabled: true,
            dir: dir.path().join(".sts2").display().to_string(),
        },
        ..AppConfig::default()
    };
    let origin = ConfigOrigin {
        uses_default_stack: true,
        base_dir: dir.path().to_path_buf(),
        config_dir: dir.path().to_path_buf(),
        local_config_path: dir.path().join("sts2.local.yaml"),
        config_dir_source: ConfigDirSource::CurrentDir,
    };
    let provenance = ConfigProvenance::default();
    let context = AppContext {
        config: &config,
        config_origin: &origin,
        config_provenance: &provenance,
        json_output: true,
        mode: Mode::Normal,
        instance: None,
    };

    record_usage_command("act play-card", context);
    record_usage_command("state", context);
    record_usage_command("act play-card", context);

    let usage_dir = dir.path().join(".sts2");
    assert_eq!(
        fs::read_to_string(usage_dir.join("cli-history.txt")).expect("history"),
        "act play-card\nstate\nact play-card\n"
    );
    assert_eq!(
        fs::read_to_string(usage_dir.join("cli-stats.csv")).expect("stats"),
        "\"act play-card\",2\nstate,1\n"
    );
}

#[test]
fn usage_tracking_is_disabled_by_default() {
    let dir = tempfile::tempdir().expect("temp dir");
    let config = AppConfig {
        usage_tracking: UsageTrackingConfig {
            enabled: false,
            dir: dir.path().join(".sts2").display().to_string(),
        },
        ..AppConfig::default()
    };
    let origin = ConfigOrigin {
        uses_default_stack: true,
        base_dir: dir.path().to_path_buf(),
        config_dir: dir.path().to_path_buf(),
        local_config_path: dir.path().join("sts2.local.yaml"),
        config_dir_source: ConfigDirSource::CurrentDir,
    };
    let provenance = ConfigProvenance::default();
    let context = AppContext {
        config: &config,
        config_origin: &origin,
        config_provenance: &provenance,
        json_output: true,
        mode: Mode::Normal,
        instance: None,
    };

    record_usage_command("state", context);

    assert!(!dir.path().join(".sts2").exists());
}

#[test]
fn ai_tool_scenario_load_forwards_degraded_multiplayer_flag() {
    let argv = ai_tool_command_argv(
        "scenario_load",
        json!({
            "path": "repros/active.sts2.scenario.yaml",
            "allowDegradedLocalMultiplayer": true,
        }),
    )
    .expect("argv");

    assert!(
        argv.iter()
            .any(|arg| arg == "--allow-degraded-local-multiplayer")
    );
    assert!(!argv.iter().any(|arg| arg == "game"));
}

#[test]
fn ai_tool_hot_reload_forwards_dev_mode_and_reload_flags() {
    let status_argv = ai_tool_command_argv(
        "hot_reload_status",
        json!({
            "project": "./mods/MyHotMod",
        }),
    )
    .expect("status argv");
    assert_eq!(
        status_argv,
        vec![
            "sts2",
            "--json",
            "dev",
            "mod-reload",
            "status",
            "--project",
            "./mods/MyHotMod",
        ]
    );

    let reload_argv = ai_tool_command_argv(
        "hot_reload",
        json!({
            "project": "./mods/MyHotMod",
            "build": true,
            "wait": true,
            "timeoutMs": 30_000,
            "intervalMs": 250,
        }),
    )
    .expect("reload argv");
    assert_eq!(
        reload_argv,
        vec![
            "sts2",
            "--json",
            "dev",
            "mod-reload",
            "--project",
            "./mods/MyHotMod",
            "--build",
            "--wait",
            "--timeout-ms",
            "30000",
            "--interval-ms",
            "250",
        ]
    );
}

#[test]
fn ai_tool_console_forwards_dev_mode_by_default() {
    let argv = ai_tool_command_argv(
        "console",
        json!({
            "command": "help",
            "args": ["draw"],
        }),
    )
    .expect("argv");

    assert_eq!(
        argv,
        vec!["sts2", "--json", "dev", "console", "help", "--", "draw"]
    );
}

#[test]
fn ai_tool_console_forwards_dangerous_mode() {
    let argv = ai_tool_command_argv(
        "console",
        json!({
            "command": "achievement",
            "mode": "dangerous",
        }),
    )
    .expect("argv");

    assert_eq!(
        argv,
        vec![
            "sts2",
            "--json",
            "--mode",
            "dangerous",
            "dev",
            "console",
            "achievement"
        ]
    );
}

#[test]
fn ai_tool_console_rejects_invalid_mode() {
    let error = ai_tool_command_argv(
        "console",
        json!({
            "command": "help",
            "mode": "normal",
        }),
    )
    .expect_err("invalid mode should fail");

    assert_eq!(error.payload["error"]["code"], "tool_invocation_failed");
    assert_eq!(
        error.payload["error"]["message"],
        "console.mode must be 'dangerous' when supplied."
    );
}

#[test]
fn ai_tool_screenshot_tools_forward_rpc_timeout() {
    let screenshot_argv = ai_tool_command_argv(
        "screenshot",
        json!({
            "output": ".sts2/artifacts/runtime.png",
            "rpcTimeoutMs": 750,
        }),
    )
    .expect("screenshot argv");
    assert_eq!(
        screenshot_argv,
        vec![
            "sts2",
            "--json",
            "dev",
            "screenshot",
            "--rpc-timeout-ms",
            "750",
            "--output",
            ".sts2/artifacts/runtime.png",
        ]
    );

    let screenshot_diff_argv = ai_tool_command_argv(
        "screenshot_diff",
        json!({
            "baseline": "tests/scenarios/baselines/mock-main-menu.png",
            "rpcTimeoutMs": 800,
        }),
    )
    .expect("screenshot diff argv");
    assert_eq!(
        screenshot_diff_argv,
        vec![
            "sts2",
            "--json",
            "dev",
            "screenshot-diff",
            "--baseline",
            "tests/scenarios/baselines/mock-main-menu.png",
            "--rpc-timeout-ms",
            "800",
        ]
    );
}

#[test]
fn parses_follow_logs_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2", "--json", "dev", "logs", "--follow", "--tail", "20", "--level", "warn",
    ])
    .expect("expected follow logs command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Logs(args),
        }) => {
            assert_eq!(args.after_cursor, None);
            assert!(args.follow);
            assert_eq!(args.tail, Some(20));
            assert_eq!(args.level, Some(LogLevelArg::Warn));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_screenshot_rpc_timeout_flags() {
    let screenshot = Cli::try_parse_from([
        "sts2",
        "dev",
        "screenshot",
        "--output",
        "runtime.png",
        "--rpc-timeout-ms",
        "750",
    ])
    .expect("expected screenshot command to parse");
    match screenshot.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Screenshot(args),
        }) => {
            assert_eq!(
                args.output.as_deref(),
                Some(std::path::Path::new("runtime.png"))
            );
            assert_eq!(args.rpc_timeout_ms, 750);
        }
        _ => panic!("unexpected screenshot command shape"),
    }

    let screenshot_diff = Cli::try_parse_from([
        "sts2",
        "dev",
        "screenshot-diff",
        "--baseline",
        "baseline.png",
        "--rpc-timeout-ms",
        "800",
    ])
    .expect("expected screenshot-diff command to parse");
    match screenshot_diff.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::ScreenshotDiff(args),
        }) => {
            assert_eq!(
                args.baseline.as_path(),
                std::path::Path::new("baseline.png")
            );
            assert_eq!(args.rpc_timeout_ms, 800);
        }
        _ => panic!("unexpected screenshot-diff command shape"),
    }
}

#[test]
fn parses_state_watch_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "state",
        "--watch",
        "--max-events",
        "5",
        "--timeout-ms",
        "1000",
        "--poll-interval-ms",
        "25",
        "--watch-mode",
        "poll",
        "--rpc-timeout-ms",
        "50",
        "--fail-fast",
    ])
    .expect("expected state watch command to parse");

    assert!(cli.json);
    assert!(cli_uses_streaming(&cli));
    match cli.command {
        Commands::State(args) => {
            assert!(args.watch);
            assert_eq!(args.max_events, Some(5));
            assert_eq!(args.timeout_ms, Some(1000));
            assert_eq!(args.poll_interval_ms, 25);
            assert_eq!(args.watch_mode, StateWatchModeArg::Poll);
            assert_eq!(args.rpc_timeout_ms, Some(50));
            assert!(args.fail_fast);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn state_without_watch_is_not_streaming() {
    let cli = Cli::try_parse_from(["sts2", "state"]).expect("state should parse");
    assert!(!cli_uses_streaming(&cli));
}

#[test]
fn mock_watch_state_emits_initial_state_event() {
    let client = bridge::RuntimeBridgeClient::from_config(&crate::TransportConfig {
        kind: crate::TransportKind::Mock,
        mock_scenario: MockScenario::Lobby,
        ..Default::default()
    });

    let mut events = Vec::new();
    client
        .watch_state(bridge::proto::StateWatchRequest::default(), |event| {
            events.push(event);
            Ok(true)
        })
        .expect("mock state watch should succeed");

    assert_eq!(events.len(), 1);
    let event = &events[0];
    assert_eq!(
        event.r#type,
        bridge::proto::StateWatchEventType::Initial as i32
    );
    assert_eq!(event.sequence, 1);
    assert!(matches!(
        event.payload,
        Some(bridge::proto::state_watch_event::Payload::State(_))
    ));
}

#[test]
fn parses_dev_wait_for_transitions_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "dev",
        "wait-for-transitions",
        "--timeout-ms",
        "7000",
        "--interval-ms",
        "25",
        "--stable-samples",
        "4",
        "--rpc-timeout-ms",
        "750",
    ])
    .expect("expected dev wait-for-transitions command to parse");

    assert!(cli.json);
    assert_eq!(cli.mode, Mode::Normal);
    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::WaitForTransitions(args),
        }) => {
            assert_eq!(args.timeout_ms, 7000);
            assert_eq!(args.interval_ms, 25);
            assert_eq!(args.stable_samples, 4);
            assert_eq!(args.rpc_timeout_ms, 750);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_after_cursor_logs_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "dev",
        "logs",
        "--after-cursor",
        "20",
        "--limit",
        "10",
    ])
    .expect("expected after-cursor logs command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Logs(args),
        }) => {
            assert_eq!(args.after_cursor, Some(20));
            assert_eq!(args.limit, 10);
            assert_eq!(args.tail, None);
            assert!(!args.follow);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_http_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "dev",
        "http",
        "--url",
        "http://127.0.0.1:3000/health",
        "--expect-status",
        "200",
        "--query",
        "json.status",
        "--equals",
        "ok",
    ])
    .expect("expected dev http command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Http(args),
        }) => {
            assert_eq!(args.url, "http://127.0.0.1:3000/health");
            assert_eq!(args.expect_status, Some(200));
            assert_eq!(args.query.query.as_deref(), Some("json.status"));
            assert_eq!(args.query.equals.as_deref(), Some("ok"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_websocket_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "dev",
        "websocket",
        "--url",
        "ws://127.0.0.1:3001/events",
        "--send-text",
        "ping",
        "--expect-text",
        "pong",
    ])
    .expect("expected dev websocket command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Websocket(args),
        }) => {
            assert_eq!(args.url, "ws://127.0.0.1:3001/events");
            assert_eq!(args.send_text, vec!["ping".to_string()]);
            assert_eq!(args.expect_text, vec!["pong".to_string()]);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_mod_reload_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "mod-reload",
        "--project",
        "mods/MyHotMod",
        "--build",
        "--wait",
        "--timeout-ms",
        "10000",
        "--interval-ms",
        "100",
    ])
    .expect("expected mod-reload command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::ModReload(args),
        }) => {
            assert_eq!(
                args.project.as_deref(),
                Some(std::path::Path::new("mods/MyHotMod"))
            );
            assert!(args.build);
            assert!(args.wait);
            assert_eq!(args.timeout_ms, 10_000);
            assert_eq!(args.interval_ms, 100);
            assert!(args.command.is_none());
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_mod_reload_status_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "mod-reload",
        "status",
        "--project",
        "mods/MyHotMod",
    ])
    .expect("expected mod-reload status command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::ModReload(ModReloadCommand {
                    command: Some(ModReloadSubcommand::Status(args)),
                    ..
                }),
        }) => {
            assert_eq!(args.project, std::path::PathBuf::from("mods/MyHotMod"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_snapshot_compare_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "dev",
        "snapshot",
        "compare",
        "--spec",
        "tests/snapshots/main-menu.sts2.snapshot.yaml",
        "--baseline",
        "tests/snapshots/baselines/main-menu",
        "--bundle-dir",
        "./tmp/snapshot-bundle",
    ])
    .expect("expected dev snapshot compare command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Snapshot(SnapshotCommand {
                    command: SnapshotSubcommand::Compare(args),
                }),
        }) => {
            assert_eq!(
                args.spec,
                PathBuf::from("tests/snapshots/main-menu.sts2.snapshot.yaml")
            );
            assert_eq!(
                args.baseline,
                PathBuf::from("tests/snapshots/baselines/main-menu")
            );
            assert_eq!(
                args.bundle_dir,
                Some(PathBuf::from("./tmp/snapshot-bundle"))
            );
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_project_hook_run_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "project",
        "hook",
        "run",
        "repo-check",
        "--input",
        "{\"kind\":\"smoke\"}",
    ])
    .expect("expected project hook run command to parse");

    match cli.command {
        Commands::Project(ProjectCommand {
            command:
                project::ProjectSubcommand::Hook(project::ProjectHookCommand {
                    command: project::ProjectHookSubcommand::Run(args),
                }),
        }) => {
            assert_eq!(args.name, "repo-check");
            assert_eq!(args.input.as_deref(), Some("{\"kind\":\"smoke\"}"));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_stable_harmony_scaffold_command() {
    let cli = Cli::parse_from([
        "sts2",
        "project",
        "scaffold",
        "stable-harmony-trampoline",
        "--output",
        "mods/MyHotMod",
        "--mod-id",
        "my-hot-mod",
        "--name",
        "My Hot Mod",
        "--namespace",
        "MyHotMod",
        "--assembly-prefix",
        "MyHotMod",
        "--author",
        "Example Author",
        "--package-id",
        "com.example.my-hot-mod",
        "--enable-guardrails",
        "--force-empty",
    ]);

    match cli.command {
        Commands::Project(ProjectCommand {
            command:
                project::ProjectSubcommand::Scaffold(project_scaffold::ProjectScaffoldCommand {
                    command:
                        project_scaffold::ProjectScaffoldSubcommand::StableHarmonyTrampoline(args),
                }),
        }) => {
            assert_eq!(args.output, PathBuf::from("mods/MyHotMod"));
            assert_eq!(args.mod_id, "my-hot-mod");
            assert_eq!(args.name, "My Hot Mod");
            assert_eq!(args.namespace, "MyHotMod");
            assert_eq!(args.assembly_prefix.as_deref(), Some("MyHotMod"));
            assert_eq!(args.author.as_deref(), Some("Example Author"));
            assert_eq!(args.package_id.as_deref(), Some("com.example.my-hot-mod"));
            assert!(args.enable_guardrails);
            assert!(args.force_empty);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_test_run_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--artifacts-dir",
        "./tmp/sts2-artifacts",
        "--failure-artifacts",
        "never",
        "--profile",
        "mock",
        "--tag",
        "smoke",
        "tests/scenarios",
    ])
    .expect("expected test run command to parse");
    assert!(!cli.json);
    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Run(args),
        }) => {
            assert_eq!(
                args.artifacts_dir,
                Some(PathBuf::from("./tmp/sts2-artifacts"))
            );
            assert_eq!(args.failure_artifacts, FailureArtifactsMode::Never);
            assert_eq!(args.profile.as_deref(), Some("mock"));
            assert_eq!(args.tags, vec!["smoke".to_string()]);
            assert_eq!(args.path, Some(PathBuf::from("tests/scenarios")));
            assert_eq!(args.inline, None);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_test_stress_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "test",
        "stress",
        "--iterations",
        "5",
        "--max-failures",
        "1",
        "--cooldown-ms",
        "25",
        "tests/scenarios/regression-main-menu.sts2.yaml",
    ])
    .expect("expected test stress command to parse");

    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Stress(args),
        }) => {
            assert_eq!(args.iterations, Some(5));
            assert_eq!(args.duration_ms, None);
            assert_eq!(args.max_failures, 1);
            assert_eq!(args.cooldown_ms, 25);
            assert_eq!(
                args.path,
                PathBuf::from("tests/scenarios/regression-main-menu.sts2.yaml")
            );
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_fixture_load_positional_path() {
    let cli = Cli::try_parse_from(["sts2", "dev", "fixture", "load", "fixtures/x.yaml"])
        .expect("expected dev fixture load positional path to parse");

    assert_eq!(cli.mode, Mode::Normal);
    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Fixture(FixtureCommand {
                    command: FixtureSubcommand::Load(args),
                }),
        }) => {
            assert_eq!(args.path(), Path::new("fixtures/x.yaml"));
            assert_eq!(args.path_flag, None);
        }
        _ => panic!("unexpected fixture load command shape"),
    }
}

#[test]
fn parses_dev_fixture_load_legacy_path_flag() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "fixture",
        "load",
        "--path",
        "fixtures/x.yaml",
    ])
    .expect("expected dev fixture load legacy --path to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Fixture(FixtureCommand {
                    command: FixtureSubcommand::Load(args),
                }),
        }) => {
            assert_eq!(args.path, None);
            assert_eq!(args.path(), Path::new("fixtures/x.yaml"));
        }
        _ => panic!("unexpected fixture load command shape"),
    }
}

#[test]
fn rejects_dev_fixture_load_duplicate_path_forms() {
    let err = Cli::try_parse_from([
        "sts2",
        "dev",
        "fixture",
        "load",
        "fixtures/a.yaml",
        "--path",
        "fixtures/b.yaml",
    ])
    .expect_err("expected positional path and --path to conflict");

    assert_eq!(err.kind(), clap::error::ErrorKind::ArgumentConflict);
}

#[test]
fn parses_hidden_dev_load_fixture_positional_path() {
    let cli = Cli::try_parse_from(["sts2", "dev", "load-fixture", "fixtures/x.yaml"])
        .expect("expected hidden dev load-fixture positional path to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::LoadFixture(args),
        }) => {
            assert_eq!(args.path(), Path::new("fixtures/x.yaml"));
        }
        _ => panic!("unexpected hidden load-fixture command shape"),
    }
}

#[test]
fn parses_dev_scenario_load_positional_and_legacy_path() {
    let positional = Cli::try_parse_from([
        "sts2",
        "dev",
        "scenario",
        "load",
        "fixtures/x.sts2.scenario.yaml",
        "--restart",
    ])
    .expect("expected dev scenario load positional path to parse");

    match positional.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Scenario(ScenarioCommand {
                    command: ScenarioSubcommand::Load(args),
                }),
        }) => {
            assert_eq!(args.path(), Path::new("fixtures/x.sts2.scenario.yaml"));
            assert!(args.restart);
        }
        _ => panic!("unexpected scenario load command shape"),
    }

    let legacy = Cli::try_parse_from([
        "sts2",
        "dev",
        "scenario",
        "load",
        "--path",
        "fixtures/x.sts2.scenario.yaml",
    ])
    .expect("expected dev scenario load legacy --path to parse");

    match legacy.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Scenario(ScenarioCommand {
                    command: ScenarioSubcommand::Load(args),
                }),
        }) => {
            assert_eq!(args.path, None);
            assert_eq!(args.path(), Path::new("fixtures/x.sts2.scenario.yaml"));
        }
        _ => panic!("unexpected scenario load command shape"),
    }
}

#[test]
fn parses_dev_debug_step_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2", "dev", "debug", "step", "--kind", "action", "--count", "3",
    ])
    .expect("expected dev debug step command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Debug(DebugCommand {
                    command: DebugSubcommand::Step(args),
                }),
        }) => {
            assert_eq!(args.kind, DebugStepKindArg::Action);
            assert_eq!(args.count, 3);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_breakpoint_add_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "breakpoint",
        "add",
        "--path",
        "screen.id",
        "--equals",
        "\"main-menu\"",
        "--name",
        "menu-break",
    ])
    .expect("expected dev breakpoint add command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Breakpoint(BreakpointCommand {
                    command: BreakpointSubcommand::Add(args),
                }),
        }) => {
            assert_eq!(args.path, "screen.id");
            assert_eq!(args.name.as_deref(), Some("menu-break"));
            assert_eq!(args.predicate.equals.as_deref(), Some("\"main-menu\""));
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_debug_session_start_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "debug",
        "session",
        "start",
        "--name",
        "m45",
        "--pause",
        "--lease-timeout-ms",
        "9000",
    ])
    .expect("expected dev debug session start command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Debug(DebugCommand {
                    command:
                        DebugSubcommand::Session(DebugSessionCommand {
                            command: DebugSessionSubcommand::Start(args),
                        }),
                }),
        }) => {
            assert_eq!(args.name.as_deref(), Some("m45"));
            assert!(args.pause);
            assert_eq!(args.lease_timeout_ms, 9000);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_dev_breakpoint_change_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "dev",
        "breakpoint",
        "add",
        "--session",
        "dbg:1",
        "--path",
        "screen.id",
        "--kind",
        "change",
        "--min-hit-count",
        "2",
        "--auto-remove-on-hit",
    ])
    .expect("expected change breakpoint add command to parse");

    match cli.command {
        Commands::Dev(DevCommand {
            command:
                DevSubcommand::Breakpoint(BreakpointCommand {
                    command: BreakpointSubcommand::Add(args),
                }),
        }) => {
            assert_eq!(args.session.as_deref(), Some("dbg:1"));
            assert_eq!(args.kind, DebugBreakpointKindArg::Change);
            assert_eq!(args.min_hit_count, 2);
            assert!(args.auto_remove_on_hit);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn stream_follow_logs_emits_ndjson_entries_without_duplicates() {
    let requests = Arc::new(Mutex::new(Vec::new()));
    let requests_for_reader = Arc::clone(&requests);
    let args = LogsArgs {
        source: LogSourceArg::Bridge,
        limit: 50,
        tail: Some(2),
        after_cursor: None,
        follow: true,
        level: None,
        target: None,
    };
    let mut output = Vec::new();

    stream_follow_logs(
        &args,
        true,
        &mut output,
        move |request| {
            requests_for_reader
                .lock()
                .expect("requests")
                .push((request.limit, request.after_cursor));

            match request.after_cursor {
                0 => Ok(bridge::proto::LogsResponse {
                    source: bridge::proto::DataSource::Stub as i32,
                    provisional: true,
                    entries: vec![
                        bridge::proto::LogEntry {
                            cursor: 2,
                            level: bridge::proto::LogLevel::Info as i32,
                            target: "bridge.transport".to_string(),
                            message: "Typed mock bridge transport is active.".to_string(),
                        },
                        bridge::proto::LogEntry {
                            cursor: 3,
                            level: bridge::proto::LogLevel::Debug as i32,
                            target: "bridge.state".to_string(),
                            message: "Returning scaffolded runtime state.".to_string(),
                        },
                    ],
                    next_cursor: 3,
                }),
                3 => Ok(bridge::proto::LogsResponse {
                    source: bridge::proto::DataSource::Stub as i32,
                    provisional: true,
                    entries: vec![bridge::proto::LogEntry {
                        cursor: 4,
                        level: bridge::proto::LogLevel::Warn as i32,
                        target: "bridge.action".to_string(),
                        message: "Action legality changed.".to_string(),
                    }],
                    next_cursor: 4,
                }),
                value => panic!("unexpected after_cursor {value}"),
            }
        },
        Some(2),
        Duration::ZERO,
    )
    .expect("follow logs should stream");

    assert_eq!(*requests.lock().expect("requests"), vec![(2, 0), (50, 3)]);
    assert_eq!(
        String::from_utf8(output).expect("utf8"),
        concat!(
            "{\"cursor\":2,\"level\":\"info\",\"message\":\"Typed mock bridge transport is active.\",\"provisional\":true,\"source\":\"stub\",\"target\":\"bridge.transport\"}\n",
            "{\"cursor\":3,\"level\":\"debug\",\"message\":\"Returning scaffolded runtime state.\",\"provisional\":true,\"source\":\"stub\",\"target\":\"bridge.state\"}\n",
            "{\"cursor\":4,\"level\":\"warn\",\"message\":\"Action legality changed.\",\"provisional\":true,\"source\":\"stub\",\"target\":\"bridge.action\"}\n"
        )
    );
}

#[test]
fn parses_test_run_inline_command_shape() {
    let cli = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--inline",
        "{name: smoke-inline, steps: [game.info]}",
    ])
    .expect("expected inline test run command to parse");
    assert!(!cli.json);
    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Run(args),
        }) => {
            assert_eq!(
                args.inline.as_deref(),
                Some("{name: smoke-inline, steps: [game.info]}")
            );
            assert_eq!(args.path, None);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn rejects_test_run_with_path_and_inline() {
    let error = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--inline",
        "{name: smoke-inline, steps: [game.info]}",
        "tests/scenarios",
    ])
    .expect_err("expected mixed test run inputs to fail");

    assert_eq!(error.kind(), clap::error::ErrorKind::ArgumentConflict);
}

#[test]
fn rejects_test_run_without_path_or_inline() {
    let cli = Cli::try_parse_from(["sts2", "test", "run"])
        .expect("test run without path now parses for profile-time validation");
    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Run(args),
        }) => {
            assert_eq!(args.path, None);
            assert_eq!(args.inline, None);
            assert_eq!(args.profile, None);
        }
        _ => panic!("unexpected test command shape"),
    }
}

#[test]
fn rejects_test_run_with_multiple_inline_flags() {
    let error = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--inline",
        "{name: smoke-inline, steps: [game.info]}",
        "--inline",
        "{name: smoke-inline-2, steps: [game.info]}",
    ])
    .expect_err("expected repeated inline input to fail");

    assert_eq!(error.kind(), clap::error::ErrorKind::ArgumentConflict);
}

#[test]
fn rejects_test_run_with_path_and_artifact_flags_only_when_input_missing() {
    let cli = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--artifacts-dir",
        "./tmp/sts2-artifacts",
    ])
    .expect("test run input is validated after profile resolution");
    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Run(args),
        }) => {
            assert_eq!(
                args.artifacts_dir,
                Some(PathBuf::from("./tmp/sts2-artifacts"))
            );
            assert_eq!(args.path, None);
            assert_eq!(args.inline, None);
            assert_eq!(args.profile, None);
        }
        _ => panic!("unexpected test command shape"),
    }
}

#[test]
fn parses_test_run_inline_with_artifact_overrides() {
    let cli = Cli::try_parse_from([
        "sts2",
        "test",
        "run",
        "--artifacts-dir",
        "./tmp/sts2-artifacts",
        "--failure-artifacts",
        "never",
        "--inline",
        "{name: smoke-inline, steps: [game.info]}",
    ])
    .expect("expected inline test run command with overrides to parse");
    assert!(!cli.json);
    match cli.command {
        Commands::Test(TestCommand {
            command: TestSubcommand::Run(args),
        }) => {
            assert_eq!(
                args.artifacts_dir,
                Some(PathBuf::from("./tmp/sts2-artifacts"))
            );
            assert_eq!(args.failure_artifacts, FailureArtifactsMode::Never);
            assert_eq!(
                args.inline.as_deref(),
                Some("{name: smoke-inline, steps: [game.info]}")
            );
            assert_eq!(args.path, None);
        }
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_game_launch_and_attach_timeout_flags() {
    let launch = Cli::try_parse_from([
        "sts2",
        "game",
        "launch",
        "--timeout-ms",
        "9000",
        "--interval-ms",
        "25",
        "--rpc-timeout-ms",
        "750",
        "--verify-stable-ms",
        "2000",
        "--no-detach-session",
        "--disable-background-throttle",
        "--",
        "--headless",
        "-fastmp",
        "host_standard",
    ])
    .expect("expected game launch command to parse");
    match launch.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::Launch(args),
        }) => {
            assert_eq!(args.timeout_ms, 9_000);
            assert_eq!(args.interval_ms, 25);
            assert_eq!(args.rpc_timeout_ms, 750);
            assert_eq!(args.verify_stable_ms, 2_000);
            assert!(args.no_detach_session);
            assert!(args.disable_background_throttle);
            assert_eq!(
                args.launch_args,
                vec![
                    "--headless".to_string(),
                    "-fastmp".to_string(),
                    "host_standard".to_string()
                ]
            );
        }
        _ => panic!("unexpected launch command shape"),
    }

    let attach = Cli::try_parse_from([
        "sts2",
        "game",
        "attach",
        "--timeout-ms",
        "3000",
        "--interval-ms",
        "10",
        "--rpc-timeout-ms",
        "500",
    ])
    .expect("expected game attach command to parse");
    match attach.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::Attach(args),
        }) => {
            assert_eq!(args.timeout_ms, 3_000);
            assert_eq!(args.interval_ms, 10);
            assert_eq!(args.rpc_timeout_ms, 500);
        }
        _ => panic!("unexpected attach command shape"),
    }
}

#[test]
fn parses_dev_scene_hover_targets() {
    let by_path = Cli::try_parse_from([
        "sts2",
        "dev",
        "scene",
        "hover",
        "--path",
        "/root/CharacterSelect/DEFECT_button",
        "--hover-tip",
        "--settle-ms",
        "100",
        "--rpc-timeout-ms",
        "750",
    ])
    .expect("expected dev scene hover command to parse");
    match by_path.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Scene(command),
        }) => match command.command {
            DevSceneSubcommand::Hover(args) => {
                assert_eq!(
                    args.node_path.as_deref(),
                    Some("/root/CharacterSelect/DEFECT_button")
                );
                assert_eq!(args.element_id, None);
                assert!(args.hover_tip);
                assert_eq!(args.settle_ms, 100);
                assert_eq!(args.rpc_timeout_ms, 750);
            }
            _ => panic!("unexpected dev scene command"),
        },
        _ => panic!("unexpected command shape"),
    }

    let by_element = Cli::try_parse_from([
        "sts2",
        "dev",
        "scene",
        "hover",
        "--element-id",
        "character:defect:tile",
    ])
    .expect("expected dev scene hover element target to parse");
    match by_element.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Scene(command),
        }) => match command.command {
            DevSceneSubcommand::Hover(args) => {
                assert_eq!(args.node_path, None);
                assert_eq!(args.element_id.as_deref(), Some("character:defect:tile"));
                assert!(!args.hover_tip);
            }
            _ => panic!("unexpected dev scene command"),
        },
        _ => panic!("unexpected command shape"),
    }
}

#[test]
fn parses_game_deploy_with_restart_verify_and_wait_flags() {
    let cli = Cli::try_parse_from([
        "sts2",
        "game",
        "deploy",
        "./mods/MyMod",
        "--build",
        "--restart",
        "--verify",
        "--timeout-ms",
        "12000",
        "--interval-ms",
        "50",
        "--rpc-timeout-ms",
        "800",
        "--verify-stable-ms",
        "3000",
    ])
    .expect("expected game deploy command to parse");

    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::Deploy(args),
        }) => {
            assert_eq!(args.path, PathBuf::from("./mods/MyMod"));
            assert!(args.build);
            assert!(args.restart);
            assert!(args.verify);
            assert_eq!(args.timeout_ms, 12_000);
            assert_eq!(args.interval_ms, 50);
            assert_eq!(args.rpc_timeout_ms, 800);
            assert_eq!(args.verify_stable_ms, 3_000);
            assert!(!args.allow_stale_build);
            assert_eq!(args.wait_quiescent_ms, 0);
            assert_eq!(args.quiescent_stable_samples, 3);
            assert!(!args.require_quiescent);
            assert!(args.launch_args.is_empty());
        }
        _ => panic!("unexpected deploy command shape"),
    }
}

#[test]
fn parses_game_deploy_build_freshness_and_quiescence_flags() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "game",
        "deploy",
        "./mods/MyMod",
        "--build",
        "--restart",
        "--allow-stale-build",
        "--wait-quiescent-ms",
        "8000",
        "--quiescent-stable-samples",
        "5",
        "--require-quiescent",
        "--",
        "--prerender-spines",
        "nomods",
    ])
    .expect("expected game deploy command to parse");

    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::Deploy(args),
        }) => {
            assert!(args.build);
            assert!(args.restart);
            assert!(args.allow_stale_build);
            assert_eq!(args.wait_quiescent_ms, 8_000);
            assert_eq!(args.quiescent_stable_samples, 5);
            assert!(args.require_quiescent);
            assert_eq!(
                args.launch_args,
                vec!["--prerender-spines".to_string(), "nomods".to_string()]
            );
        }
        _ => panic!("unexpected deploy command shape"),
    }
}

#[test]
fn parses_game_launch_quiescence_flags() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "game",
        "launch",
        "--wait-quiescent-ms",
        "8000",
        "--quiescent-stable-samples",
        "2",
        "--require-quiescent",
        "--",
        "--prerender-spines",
    ])
    .expect("expected game launch command to parse");

    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::Launch(args),
        }) => {
            assert_eq!(args.wait_quiescent_ms, 8_000);
            assert_eq!(args.quiescent_stable_samples, 2);
            assert!(args.require_quiescent);
            assert_eq!(args.launch_args, vec!["--prerender-spines".to_string()]);
        }
        _ => panic!("unexpected launch command shape"),
    }
}

#[test]
fn parses_game_install_bridge() {
    let cli = Cli::try_parse_from(["sts2", "game", "install-bridge"])
        .expect("expected game install-bridge command to parse");

    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::InstallBridge(args),
        }) => {
            // Bare `install-bridge` must keep working: every argument is opt-in.
            assert!(!args.no_build);
            assert!(!args.force);
            assert!(!args.restart);
            assert_eq!(args.timeout_ms, 30_000);
            assert_eq!(args.wait_quiescent_ms, 0);
            assert_eq!(args.quiescent_stable_samples, 3);
        }
        _ => panic!("unexpected install-bridge command shape"),
    }
}

#[test]
fn parses_global_progress_flag() {
    let cli = Cli::try_parse_from(["sts2", "--json", "--progress", "game", "info"])
        .expect("expected --progress to parse as a global flag");
    assert!(cli.progress);

    let default = Cli::try_parse_from(["sts2", "game", "info"]).expect("expected game info");
    assert!(!default.progress, "--progress must default to off");
}

#[test]
fn parses_game_install_bridge_flags() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "game",
        "install-bridge",
        "--no-build",
        "--force",
        "--restart",
        "--timeout-ms",
        "12000",
        "--interval-ms",
        "50",
        "--rpc-timeout-ms",
        "800",
        "--wait-quiescent-ms",
        "8000",
        "--quiescent-stable-samples",
        "4",
        "--require-quiescent",
    ])
    .expect("expected game install-bridge command to parse");

    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::InstallBridge(args),
        }) => {
            assert!(args.no_build);
            assert!(args.force);
            assert!(args.restart);
            assert_eq!(args.timeout_ms, 12_000);
            assert_eq!(args.interval_ms, 50);
            assert_eq!(args.rpc_timeout_ms, 800);
            assert_eq!(args.wait_quiescent_ms, 8_000);
            assert_eq!(args.quiescent_stable_samples, 4);
            assert!(args.require_quiescent);
        }
        _ => panic!("unexpected install-bridge command shape"),
    }
}

#[test]
fn parses_game_bridge_health_default() {
    let cli = Cli::try_parse_from(["sts2", "--json", "game", "bridge-health"])
        .expect("expected game bridge-health command to parse");

    assert!(cli.json);
    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::BridgeHealth(args),
        }) => {
            assert!(!args.non_mutating);
            assert!(!args.repair_stale_endpoint);
            assert!(!args.verbose);
            assert_eq!(args.rpc_timeout_ms, 1000);
        }
        _ => panic!("unexpected bridge-health command shape"),
    }
}

#[test]
fn parses_game_bridge_health_non_mutating_and_verbose() {
    let cli = Cli::try_parse_from([
        "sts2",
        "--json",
        "game",
        "bridge-health",
        "--non-mutating",
        "--verbose",
        "--rpc-timeout-ms",
        "750",
    ])
    .expect("expected game bridge-health command to parse");

    assert!(cli.json);
    match cli.command {
        Commands::Game(GameCommand {
            command: GameSubcommand::BridgeHealth(args),
        }) => {
            assert!(args.non_mutating);
            assert!(!args.repair_stale_endpoint);
            assert!(args.verbose);
            assert_eq!(args.rpc_timeout_ms, 750);
        }
        _ => panic!("unexpected bridge-health command shape"),
    }
}

#[test]
fn parses_skill_install_with_path_override() {
    let cli = Cli::try_parse_from(["sts2", "skill", "install", "--path", "./vendor/skills"])
        .expect("expected skill install command to parse");

    match cli.command {
        Commands::Skill(SkillCommand {
            command: SkillSubcommand::Install(args),
        }) => {
            assert_eq!(args.path, None);
            assert_eq!(
                args.explicit_path(),
                Some(&PathBuf::from("./vendor/skills"))
            );
        }
        _ => panic!("unexpected skill install command shape"),
    }
}

#[test]
fn parses_skill_install_with_positional_path() {
    let cli = Cli::try_parse_from(["sts2", "skill", "install", "./vendor/skills"])
        .expect("expected skill install positional path to parse");

    match cli.command {
        Commands::Skill(SkillCommand {
            command: SkillSubcommand::Install(args),
        }) => {
            assert_eq!(
                args.explicit_path(),
                Some(&PathBuf::from("./vendor/skills"))
            );
            assert_eq!(args.path_flag, None);
        }
        _ => panic!("unexpected skill install command shape"),
    }
}

#[test]
fn rejects_skill_install_duplicate_path_forms() {
    let err = Cli::try_parse_from([
        "sts2",
        "skill",
        "install",
        "./vendor/skills",
        "--path",
        "./other/skills",
    ])
    .expect_err("expected positional path and --path to conflict");

    assert_eq!(err.kind(), clap::error::ErrorKind::ArgumentConflict);
}

#[test]
fn app_config_loads_live_bridge_game_paths() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  path: /tmp/sts2
  assembliesDir: /tmp/sts2/data_sts2_linux_x86_64
  resourcesDir: /tmp/sts2/project
  modsDir: /tmp/sts2/mods
"#,
    )
    .expect("yaml config");

    assert_eq!(config.game.path, "/tmp/sts2");
    assert_eq!(
        config.game.assemblies_dir.as_deref(),
        Some("/tmp/sts2/data_sts2_linux_x86_64")
    );
    assert_eq!(
        config.game.resources_dir.as_deref(),
        Some("/tmp/sts2/project")
    );
    assert_eq!(config.game.mods_dir.as_deref(), Some("/tmp/sts2/mods"));
}

#[test]
fn app_config_loads_lifecycle_overrides() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  path: /tmp/sts2
  launchWrapper:
    - xvfb-run
    - -a
  launchExecutable: /tmp/sts2/SlayTheSpire2.x86_64
  launchArgs:
    - --headless
    - --modded
  launchWorkingDir: /tmp/sts2
  launchEnv:
    FOO: bar
    BAR: baz
  disableBackgroundThrottle: true
  stopCommand:
    - /usr/bin/pkill
    - -f
    - SlayTheSpire2.x86_64
  deployBuildCommand:
    - ./scripts/build-mod.sh
    - --release
  deployOutputSubdir: dist/MyMod
"#,
    )
    .expect("yaml config");

    assert_eq!(
        config.game.launch_executable.as_deref(),
        Some("/tmp/sts2/SlayTheSpire2.x86_64")
    );
    assert_eq!(
        config.game.launch_wrapper,
        vec!["xvfb-run".to_string(), "-a".to_string()]
    );
    assert_eq!(
        config.game.launch_args,
        vec!["--headless".to_string(), "--modded".to_string()]
    );
    assert_eq!(config.game.launch_working_dir.as_deref(), Some("/tmp/sts2"));
    assert_eq!(
        config.game.launch_env.get("FOO").map(String::as_str),
        Some("bar")
    );
    assert_eq!(
        config.game.launch_env.get("BAR").map(String::as_str),
        Some("baz")
    );
    assert!(config.game.disable_background_throttle);
    assert_eq!(
        config.game.stop_command,
        vec![
            "/usr/bin/pkill".to_string(),
            "-f".to_string(),
            "SlayTheSpire2.x86_64".to_string()
        ]
    );
    assert_eq!(
        config.game.deploy_build_command,
        vec![
            "./scripts/build-mod.sh".to_string(),
            "--release".to_string()
        ]
    );
    assert_eq!(
        config.game.deploy_output_subdir.as_deref(),
        Some("dist/MyMod")
    );
}

#[test]
fn app_config_loads_instance_defaults() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
instances:
  dir: ./.sts2/custom-instances
  default: work-a
  isolatedBuild: true
  symlinkUserDataDirs:
    - my-mod
    - my-mod/asset-cache
"#,
    )
    .expect("yaml config");

    assert_eq!(config.instances.dir, "./.sts2/custom-instances");
    assert_eq!(config.instances.default_name.as_deref(), Some("work-a"));
    assert!(config.instances.isolated_build);
    assert_eq!(
        config.instances.symlink_user_data_dirs,
        vec!["my-mod".to_string(), "my-mod/asset-cache".to_string()]
    );
    // Unset means "copy everything", matching pre-feature behavior.
    assert!(
        AppConfig::default()
            .instances
            .symlink_user_data_dirs
            .is_empty()
    );
}

#[test]
fn app_config_loads_lifecycle_string_command_shorthand() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  launchWrapper: "xvfb-run -a"
  launchArgs: "--headless -fastmp host_standard"
"#,
    )
    .expect("yaml config");

    assert_eq!(
        config.game.launch_wrapper,
        vec!["xvfb-run".to_string(), "-a".to_string()]
    );
    assert_eq!(
        config.game.launch_args,
        vec![
            "--headless".to_string(),
            "-fastmp".to_string(),
            "host_standard".to_string()
        ]
    );
}

#[test]
fn app_config_lifecycle_string_shorthand_preserves_quoted_segments() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  launchArgs: "--profile '/path with spaces/profile.json'"
"#,
    )
    .expect("yaml config");

    assert_eq!(
        config.game.launch_args,
        vec![
            "--profile".to_string(),
            "/path with spaces/profile.json".to_string()
        ]
    );
}

#[test]
fn app_config_lifecycle_whitespace_string_shorthand_is_empty() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  launchWrapper: "   "
  launchArgs: "   "
"#,
    )
    .expect("yaml config");

    assert!(config.game.launch_wrapper.is_empty());
    assert!(config.game.launch_args.is_empty());
}

#[test]
fn app_config_lifecycle_string_shorthand_rejects_malformed_quoting() {
    let error = serde_yaml::from_str::<AppConfig>(
        r#"
game:
  launchArgs: "--profile '/path with spaces/profile.json"
"#,
    )
    .expect_err("malformed launch args should fail");

    assert!(
        error.to_string().contains(
            "game.launchArgs must be a string or array of strings with valid shell-style quoting"
        ),
        "{error}"
    );
}

#[test]
fn app_config_loads_named_test_profiles() {
    let config = serde_yaml::from_str::<AppConfig>(
        r#"
test:
  profiles:
    mock:
      description: Deterministic mock scenario run.
      config:
        transport:
          kind: mock
          mockScenario: combat
      paths:
        - tests/scenarios
      includeTags:
        - smoke
      excludeTags:
        - liveValidation
    live:
      description: Real-game validation.
      config:
        transport:
          kind: ipc
      paths:
        - tests/scenarios
      includeTags:
        - liveValidation
      preflight:
        deployBridge: true
        deploy:
          path: mods/MyMod
          build: true
          restart: true
          verify: true
          timeoutMs: 40000
          intervalMs: 500
          rpcTimeoutMs: 1500
          verifyStableMs: 2500
        launch: true
        timeoutMs: 30000
        intervalMs: 250
        rpcTimeoutMs: 1000
        verifyStableMs: 2000
      cleanup:
        launchedGame: true
        timeoutMs: 5000
        intervalMs: 100
      gate:
        requireMatchingScenario: true
        allowNotApplicable: true
"#,
    )
    .expect("yaml config");

    let mock = config.test.profiles.get("mock").expect("mock profile");
    assert_eq!(
        mock.description.as_deref(),
        Some("Deterministic mock scenario run.")
    );
    assert_eq!(mock.paths, vec![PathBuf::from("tests/scenarios")]);
    assert_eq!(mock.include_tags, vec!["smoke".to_string()]);
    assert_eq!(mock.exclude_tags, vec!["liveValidation".to_string()]);

    let live = config.test.profiles.get("live").expect("live profile");
    assert_eq!(live.preflight.timeout_ms, 30_000);
    assert_eq!(live.preflight.interval_ms, 250);
    assert_eq!(live.preflight.rpc_timeout_ms, 1_000);
    assert_eq!(live.preflight.verify_stable_ms, 2_000);
    assert!(live.preflight.deploy_bridge);
    let deploy = live.preflight.deploy.as_ref().expect("deploy preflight");
    assert_eq!(deploy.path.as_deref(), Some(Path::new("mods/MyMod")));
    assert!(deploy.build);
    assert!(deploy.restart);
    assert!(deploy.verify);
    assert_eq!(deploy.timeout_ms, 40_000);
    assert_eq!(deploy.interval_ms, 500);
    assert_eq!(deploy.rpc_timeout_ms, 1_500);
    assert_eq!(deploy.verify_stable_ms, 2_500);
    assert!(live.preflight.launch);
    assert!(live.cleanup.launched_game);
    assert_eq!(live.cleanup.timeout_ms, 5_000);
    assert_eq!(live.cleanup.interval_ms, 100);
    assert!(live.gate.require_matching_scenario);
    assert!(live.gate.allow_not_applicable);
}

#[test]
fn app_config_load_from_dir_layers_local_over_baseline() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.config.yaml"),
        r#"
game:
  path: /baseline/game
  assembliesDir: /baseline/assemblies
  resourcesDir: /baseline/resources
transport:
  kind: mock
  mockScenario: main-menu
  ipcPath: /baseline.sock
"#,
    )
    .expect("write baseline config");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
game:
  path: /local/game
  assembliesDir: null
transport:
  mockScenario: combat
"#,
    )
    .expect("write local config");

    let config = AppConfig::load_from_dir(None, dir.path()).expect("load layered config");

    assert_eq!(config.game.path, "/local/game");
    assert_eq!(config.game.assemblies_dir, None);
    assert_eq!(
        config.game.resources_dir.as_deref(),
        Some("/baseline/resources")
    );
    assert_eq!(config.transport.kind, TransportKind::Mock);
    assert_eq!(config.transport.mock_scenario, MockScenario::Combat);
    assert_eq!(config.transport.ipc_path.as_deref(), Some("/baseline.sock"));
}

#[test]
fn app_config_load_from_dir_uses_only_explicit_config_file() {
    let dir = tempfile::tempdir().expect("temp dir");
    fs::write(
        dir.path().join("sts2.local.yaml"),
        r#"
game:
  path: /local/game
transport:
  mockScenario: combat
"#,
    )
    .expect("write local config");
    fs::write(
        dir.path().join("custom.yaml"),
        r#"
game:
  path: /explicit/game
transport:
  kind: mock
  mockScenario: lobby
"#,
    )
    .expect("write explicit config");

    let config = AppConfig::load_from_dir(Some(Path::new("custom.yaml")), dir.path())
        .expect("load explicit config");

    assert_eq!(config.game.path, "/explicit/game");
    assert_eq!(config.transport.mock_scenario, MockScenario::Lobby);
    assert_eq!(config.game.assemblies_dir, None);
}
