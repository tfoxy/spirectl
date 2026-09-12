use std::collections::BTreeMap;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::Path;
use std::time::Duration;

use super::{
    execute_action_json, handle_assets, handle_code, handle_config, handle_dev, handle_game,
    handle_project, handle_service, handle_skill, handle_test, handle_toolchain,
    resolve_config_relative_path, stream_combat_events_watch_json, stream_follow_logs,
    stream_state_watch_json,
};
use crate::{
    ActCommand, ActSubcommand, AppConfig, AssetSubcommand, BreakpointSubcommand, Cli,
    CodeSubcommand, CombatCommand, CombatSubcommand, Commands, ConfigSubcommand,
    DebugSessionSubcommand, DebugSubcommand, DevCommand, DevSceneSubcommand, DevSubcommand,
    EventsArgs, FixtureSubcommand, GameModsSubcommand, GameSubcommand, InspectSubcommand,
    LoadedConfig, LogsArgs, MapCommand, MapSubcommand, ModReloadSubcommand, ModelCommand,
    MouseSubcommand, ReferenceCommand, ScenarioSubcommand, ServiceCommand, ServiceSubcommand,
    SkillSubcommand, SnapshotSubcommand, StateArgs, StateViewArg, TestSubcommand, bridge_client,
    completion, execute_combat_preview_json, execute_map_drawings_json, execute_models_json,
    execute_reference_json, execute_state_json, handle_completion, handle_inspect,
    render_value_success, render_value_success_compact_json, stdout_write_error,
};
use crate::{
    AppContext, AppError, RenderedCommand, automation_service, bridge, project, project_scaffold,
    toolchain,
};
use serde_json::json;

pub(crate) fn canonical_usage_command(command: &Commands) -> &'static str {
    match command {
        Commands::State(args) if args.watch => "state watch",
        Commands::State(args) if args.view == Some(StateViewArg::Actions) => "state actions",
        Commands::State(_) => "state",
        Commands::Events(_) => "events watch",
        Commands::Act(command) => canonical_act_usage_command(&command.command),
        Commands::Dev(command) => match &command.command {
            DevSubcommand::Logs(_) => "dev logs",
            DevSubcommand::LogHealth(_) => "dev log-health",
            DevSubcommand::Console(_) => "dev console",
            DevSubcommand::Heal(_) => "dev heal",
            DevSubcommand::Diagnostics(_) => "dev diagnostics",
            DevSubcommand::Assert(_) => "dev assert",
            DevSubcommand::Http(_) => "dev http",
            DevSubcommand::HttpWait(_) => "dev http-wait",
            DevSubcommand::Fetch(_) => "dev fetch",
            DevSubcommand::Websocket(_) => "dev websocket",
            DevSubcommand::WaitFor(_) => "dev wait-for",
            DevSubcommand::WaitForTransitions(_) => "dev wait-for-transitions",
            DevSubcommand::Fixture(command) => match &command.command {
                FixtureSubcommand::Record => "dev fixture record",
                FixtureSubcommand::Resume(_) => "dev fixture resume",
                FixtureSubcommand::Status => "dev fixture status",
                FixtureSubcommand::Clear => "dev fixture clear",
                FixtureSubcommand::Load(_) => "dev fixture load",
                FixtureSubcommand::LoadLatest(_) => "dev fixture load-latest",
            },
            DevSubcommand::LoadFixture(_) => "dev load-fixture",
            DevSubcommand::Scenario(command) => match &command.command {
                ScenarioSubcommand::Export(_) => "dev scenario export",
                ScenarioSubcommand::Load(_) => "dev scenario load",
            },
            DevSubcommand::Debug(command) => match &command.command {
                DebugSubcommand::Status(_) => "dev debug status",
                DebugSubcommand::Session(command) => match &command.command {
                    DebugSessionSubcommand::Start(_) => "dev debug session start",
                    DebugSessionSubcommand::Status(_) => "dev debug session status",
                    DebugSessionSubcommand::End(_) => "dev debug session end",
                },
                DebugSubcommand::Events(_) => "dev debug events",
                DebugSubcommand::Pause(_) => "dev debug pause",
                DebugSubcommand::Resume(_) => "dev debug resume",
                DebugSubcommand::Step(_) => "dev debug step",
                DebugSubcommand::Wait(_) => "dev debug wait",
            },
            DevSubcommand::Breakpoint(command) => match &command.command {
                BreakpointSubcommand::List(_) => "dev breakpoint list",
                BreakpointSubcommand::Add(_) => "dev breakpoint add",
                BreakpointSubcommand::Remove(_) => "dev breakpoint remove",
            },
            DevSubcommand::Screenshot(_) => "dev screenshot",
            DevSubcommand::ScreenshotDiff(_) => "dev screenshot-diff",
            DevSubcommand::Snapshot(command) => match &command.command {
                SnapshotSubcommand::Export(_) => "dev snapshot export",
                SnapshotSubcommand::Compare(_) => "dev snapshot compare",
            },
            DevSubcommand::ModReload(command) => match &command.command {
                Some(ModReloadSubcommand::Status(_)) => "dev mod-reload status",
                None => "dev mod-reload",
            },
            DevSubcommand::Scene(command) => match &command.command {
                DevSceneSubcommand::Tree(_) => "dev scene tree",
                DevSceneSubcommand::Node(_) => "dev scene node",
                DevSceneSubcommand::Children(_) => "dev scene children",
                DevSceneSubcommand::Hover(_) => "dev scene hover",
                DevSceneSubcommand::Unhover(_) => "dev scene unhover",
                DevSceneSubcommand::SetVisible(_) => "dev scene set-visible",
            },
            DevSubcommand::VisualPreflight(_) => "dev visual-preflight",
        },
        Commands::Game(command) => match &command.command {
            GameSubcommand::Detect => "game detect",
            GameSubcommand::Info => "game info",
            GameSubcommand::BridgeHealth(_) => "game bridge-health",
            GameSubcommand::InstallBridge(_) => "game install-bridge",
            GameSubcommand::Launch(_) => "game launch",
            GameSubcommand::Attach(_) => "game attach",
            GameSubcommand::Close(_) => "game close",
            GameSubcommand::Kill(_) => "game kill",
            GameSubcommand::Deploy(_) => "game deploy",
            GameSubcommand::Mods(command) => match &command.command {
                GameModsSubcommand::Settings(_) => "game mods settings",
                GameModsSubcommand::Active => "game mods active",
            },
            GameSubcommand::Instances(_) => "game instances",
        },
        Commands::Config(command) => match &command.command {
            ConfigSubcommand::Resolve(_) => "config resolve",
        },
        Commands::Service(command) => match &command.command {
            ServiceSubcommand::Serve(_) => "service serve",
        },
        Commands::Toolchain(command) => match &command.command {
            toolchain::ToolchainSubcommand::Info => "toolchain info",
        },
        Commands::Project(command) => match &command.command {
            project::ProjectSubcommand::Recover(_) => "project recover",
            project::ProjectSubcommand::Profile(command) => match &command.command {
                project::ProjectProfileSubcommand::List => "project profile list",
                project::ProjectProfileSubcommand::Show(_) => "project profile show",
                project::ProjectProfileSubcommand::Run(_) => "project profile run",
            },
            project::ProjectSubcommand::Hook(command) => match &command.command {
                project::ProjectHookSubcommand::List => "project hook list",
                project::ProjectHookSubcommand::Show(_) => "project hook show",
                project::ProjectHookSubcommand::Run(_) => "project hook run",
            },
            project::ProjectSubcommand::Scaffold(command) => match &command.command {
                project_scaffold::ProjectScaffoldSubcommand::StableHarmonyTrampoline(_) => {
                    "project scaffold stable-harmony-trampoline"
                }
            },
        },
        Commands::Code(command) => match &command.command {
            CodeSubcommand::Locate(_) => "code locate",
            CodeSubcommand::Describe(_) => "code describe",
            CodeSubcommand::Decompile(_) => "code decompile",
            CodeSubcommand::Refs(_) => "code refs",
            CodeSubcommand::Derived(_) => "code derived",
            CodeSubcommand::Hooks(_) => "code hooks",
            CodeSubcommand::HookInfo(_) => "code hook-info",
            CodeSubcommand::VerifyReferences(_) => "code verify-references",
            CodeSubcommand::SceneSearch(_) => "code scene-search",
            CodeSubcommand::SceneTree(_) => "code scene-tree",
            CodeSubcommand::SceneNode(_) => "code scene-node",
        },
        Commands::Assets(command) => match &command.command {
            AssetSubcommand::Extract(_) => "assets extract",
            AssetSubcommand::Catalog(_) => "assets catalog",
            AssetSubcommand::Resolve(_) => "assets resolve",
            AssetSubcommand::Explain(_) => "assets explain",
            AssetSubcommand::ExtractBatch(_) => "assets extract-batch",
        },
        Commands::Models(_) => "models",
        Commands::Combat(_) => "combat",
        Commands::Map(command) => match &command.command {
            MapSubcommand::Drawings(_) => "map drawings",
            MapSubcommand::DrawStroke(_) => "map draw-stroke",
        },
        Commands::Reference(_) => "reference",
        Commands::Skill(command) => match &command.command {
            SkillSubcommand::Install(_) => "skill install",
        },
        Commands::Completion(command) => match &command.command {
            None => "completion",
            Some(completion::CompletionSubcommand::Bash) => "completion bash",
            Some(completion::CompletionSubcommand::Zsh) => "completion zsh",
            Some(completion::CompletionSubcommand::Fish) => "completion fish",
            Some(completion::CompletionSubcommand::Powershell) => "completion powershell",
            Some(completion::CompletionSubcommand::Install(_)) => "completion install",
        },
        Commands::Inspect(command) => match &command.command {
            InspectSubcommand::Commands => "inspect commands",
            InspectSubcommand::Examples(_) => "inspect examples",
            InspectSubcommand::Actions(_) => "inspect actions",
            InspectSubcommand::StateSchema => "inspect state-schema",
            InspectSubcommand::ReferenceTopics => "inspect reference-topics",
            InspectSubcommand::ViewportPresets(_) => "inspect viewport-presets",
            InspectSubcommand::AiTools => "inspect ai-tools",
        },
        Commands::Test(command) => match &command.command {
            TestSubcommand::Run(_) => "test run",
            TestSubcommand::Stress(_) => "test stress",
        },
    }
}

pub(crate) fn canonical_act_usage_command(command: &ActSubcommand) -> &'static str {
    match command {
        ActSubcommand::PlayCard(_) => "act play-card",
        ActSubcommand::Choose(_) => "act choose",
        ActSubcommand::ConfirmSelection(_) => "act confirm-selection",
        ActSubcommand::CancelSelection(_) => "act cancel-selection",
        ActSubcommand::Mouse(command) => match &command.command {
            MouseSubcommand::Click(_) => "act mouse click",
        },
        ActSubcommand::UsePotion(_) => "act use-potion",
        ActSubcommand::OpenPotionPopup(_) => "act open-potion-popup",
        ActSubcommand::StartPotionTargeting(_) => "act start-potion-targeting",
        ActSubcommand::SelectTarget(_) => "act select-target",
        ActSubcommand::DiscardPotion(_) => "act discard-potion",
        ActSubcommand::SelectMapNode(_) => "act select-map-node",
        ActSubcommand::DrawMapStroke(_) => "act draw-map-stroke",
        ActSubcommand::ClearMapDrawings(_) => "act clear-map-drawings",
        ActSubcommand::EndTurn(_) => "act end-turn",
        ActSubcommand::CancelEndTurn(_) => "act cancel-end-turn",
        ActSubcommand::Ready(_) => "act ready",
        ActSubcommand::Unready(_) => "act unready",
        ActSubcommand::SelectCharacter(_) => "act select-character",
        ActSubcommand::JoinLobbyPlayer(_) => "act join-lobby-player",
        ActSubcommand::LeaveLobbyPlayer(_) => "act leave-lobby-player",
        ActSubcommand::ClaimReward(_) => "act claim-reward",
        ActSubcommand::SkipRewards(_) => "act skip-rewards",
        ActSubcommand::SelectCard(_) => "act select-card",
        ActSubcommand::SkipCardSelection(_) => "act skip-card-selection",
        ActSubcommand::SelectBundle(_) => "act select-bundle",
        ActSubcommand::BuyCard(_) => "act buy-card",
        ActSubcommand::BuyRelic(_) => "act buy-relic",
        ActSubcommand::BuyPotion(_) => "act buy-potion",
        ActSubcommand::RemoveCard(_) => "act remove-card",
        ActSubcommand::LeaveShop(_) => "act leave-shop",
        ActSubcommand::CloseShopInventory(_) => "act close-shop-inventory",
        ActSubcommand::Rest(_) => "act rest",
        ActSubcommand::Smith(_) => "act smith",
        ActSubcommand::UseRestSiteOption(_) => "act use-rest-site-option",
        ActSubcommand::ProceedRestSite(_) => "act proceed-rest-site",
        ActSubcommand::OpenChest(_) => "act open-chest",
        ActSubcommand::TakeRelic(_) => "act take-relic",
        ActSubcommand::ProceedTreasureRoom(_) => "act proceed-treasure-room",
        ActSubcommand::BackFromMap(_) => "act back-from-map",
        ActSubcommand::SelectEventOption(_) => "act select-event-option",
        ActSubcommand::OpenEventShop(_) => "act open-event-shop",
        ActSubcommand::UseCrystalSphereControl(_) => "act use-crystal-sphere-control",
        ActSubcommand::ProceedEvent(_) => "act proceed-event",
        ActSubcommand::ToggleMap(_) => "act toggle-map",
        ActSubcommand::ToggleDeck(_) => "act toggle-deck",
        ActSubcommand::ToggleSettings(_) => "act toggle-settings",
        ActSubcommand::SortDeckView(_) => "act sort-deck-view",
        ActSubcommand::ToggleDeckViewUpgrades(_) => "act toggle-deck-view-upgrades",
        ActSubcommand::ViewDrawPile(_) => "act view-draw-pile",
        ActSubcommand::ViewDiscardPile(_) => "act view-discard-pile",
        ActSubcommand::ViewExhaustPile(_) => "act view-exhaust-pile",
        ActSubcommand::InspectRelic(_) => "act inspect-relic",
        ActSubcommand::CloseInspectRelic(_) => "act close-inspect-relic",
        ActSubcommand::SelectHandCard(_) => "act select-hand-card",
        ActSubcommand::DeselectHandCard(_) => "act deselect-hand-card",
        ActSubcommand::ConfirmHandSelection(_) => "act confirm-hand-selection",
    }
}

pub(crate) fn record_usage_command(command: &str, context: AppContext<'_>) {
    if !context.config.usage_tracking.enabled {
        return;
    }

    let usage_dir =
        resolve_config_relative_path(Path::new(&context.config.usage_tracking.dir), context);
    let _ = write_usage_files(&usage_dir, command);
}

pub(crate) fn write_usage_files(usage_dir: &Path, command: &str) -> std::io::Result<()> {
    fs::create_dir_all(usage_dir)?;

    let history_path = usage_dir.join("cli-history.txt");
    let mut history = OpenOptions::new()
        .create(true)
        .append(true)
        .open(history_path)?;
    writeln!(history, "{command}")?;

    let stats_path = usage_dir.join("cli-stats.csv");
    let mut stats = read_usage_stats(&stats_path)?;
    *stats.entry(command.to_string()).or_insert(0) += 1;

    let mut rendered = String::new();
    for (command, count) in stats {
        rendered.push_str(&csv_field(&command));
        rendered.push(',');
        rendered.push_str(&count.to_string());
        rendered.push('\n');
    }
    fs::write(stats_path, rendered)?;
    Ok(())
}

pub(crate) fn read_usage_stats(path: &Path) -> std::io::Result<BTreeMap<String, u64>> {
    let raw = match fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(BTreeMap::new()),
        Err(error) => return Err(error),
    };

    let mut stats = BTreeMap::new();
    for line in raw.lines() {
        if let Some((command, count)) = parse_usage_stats_line(line) {
            stats.insert(command, count);
        }
    }
    Ok(stats)
}

pub(crate) fn parse_usage_stats_line(line: &str) -> Option<(String, u64)> {
    let (command, count) = if let Some(rest) = line.strip_prefix('"') {
        let mut command = String::new();
        let mut chars = rest.char_indices().peekable();
        let mut end_index = None;
        while let Some((index, ch)) = chars.next() {
            if ch == '"' {
                if matches!(chars.peek(), Some((_, '"'))) {
                    command.push('"');
                    let _ = chars.next();
                } else {
                    end_index = Some(index + 1);
                    break;
                }
            } else {
                command.push(ch);
            }
        }
        let count = rest.get(end_index?..)?.strip_prefix(',')?;
        (command, count)
    } else {
        let (command, count) = line.split_once(',')?;
        (command.to_string(), count)
    };

    count.parse::<u64>().ok().map(|count| (command, count))
}

pub(crate) fn csv_field(value: &str) -> String {
    if value
        .chars()
        .any(|ch| ch == '"' || ch == ',' || ch == '\n' || ch == '\r' || ch == ' ')
    {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.to_string()
    }
}

pub fn cli_uses_streaming(cli: &Cli) -> bool {
    matches!(
        cli.command,
        Commands::Dev(DevCommand {
            command: DevSubcommand::Logs(LogsArgs { follow: true, .. }),
        }) | Commands::State(StateArgs { watch: true, .. })
            | Commands::Events(EventsArgs { watch: true, .. })
            | Commands::Service(ServiceCommand {
                command: ServiceSubcommand::Serve(_),
            })
    )
}

/// Resolve the active `--instance` (if any) and produce the effective,
/// instance-overlaid config. Returns the loaded config (for origin/provenance),
/// the overlaid config to run against, and the resolved instance context.
fn resolve_instance_and_config(
    cli: &Cli,
) -> Result<
    (
        LoadedConfig,
        AppConfig,
        Option<crate::instance::InstanceContext>,
    ),
    AppError,
> {
    let loaded_config = LoadedConfig::load(cli.config.as_deref())?;
    let mut effective_config = loaded_config.config.clone();
    let instance_name =
        cli.instance
            .as_deref()
            .or(loaded_config.config.instances.default_name.as_deref());
    if cli.isolated_build && instance_name.is_none() {
        return Err(crate::instance::instance_error(
            "isolated_build_requires_instance",
            "--isolated-build requires --instance, SPIRECTL_INSTANCE, or instances.default."
                .to_string(),
        ));
    }
    let isolated_build = cli.isolated_build || loaded_config.config.instances.isolated_build;
    let instance_ctx = crate::instance::InstanceContext::resolve(
        instance_name,
        isolated_build,
        &effective_config,
    )?;
    if let Some(ctx) = &instance_ctx {
        ctx.apply_overlay(&mut effective_config);
    }
    Ok((loaded_config, effective_config, instance_ctx))
}

pub fn run_cli(cli: Cli) -> Result<RenderedCommand, AppError> {
    crate::progress::set_enabled(cli.progress);
    if !bridge::should_preserve_mock_loaded_fixture_for_next_cli_run() {
        bridge::reset_mock_loaded_fixture_scenario();
    }
    let (loaded_config, effective_config, instance_ctx) = resolve_instance_and_config(&cli)?;
    let context = AppContext {
        config: &effective_config,
        config_origin: &loaded_config.origin,
        config_provenance: &loaded_config.provenance,
        json_output: cli.json,
        mode: cli.mode,
        instance: instance_ctx.as_ref(),
    };
    record_usage_command(canonical_usage_command(&cli.command), context);

    dispatch_cli_command(cli.command, context)
}

pub(crate) fn dispatch_cli_command(
    command: Commands,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command {
        Commands::State(args) => handle_state(args, context),
        Commands::Events(args) => handle_events(args, context),
        Commands::Act(command) => handle_act(command, context),
        Commands::Dev(command) => handle_dev(command, context),
        Commands::Game(command) => handle_game(command, context),
        Commands::Config(command) => handle_config(command, context),
        Commands::Service(command) => handle_service(command, context),
        Commands::Toolchain(command) => handle_toolchain(command, context),
        Commands::Project(command) => handle_project(command, context),
        Commands::Code(command) => handle_code(command, context),
        Commands::Assets(command) => handle_assets(command, context),
        Commands::Models(command) => handle_models(command, context),
        Commands::Combat(command) => handle_combat(command, context),
        Commands::Map(command) => handle_map(command, context),
        Commands::Reference(command) => handle_reference(command, context),
        Commands::Skill(command) => handle_skill(command, context),
        Commands::Completion(command) => handle_completion(command, context),
        Commands::Inspect(command) => handle_inspect(command, context),
        Commands::Test(command) => handle_test(command, context),
    }
}

pub fn run_cli_streaming<W: Write>(cli: Cli, writer: &mut W) -> Result<i32, AppError> {
    crate::progress::set_enabled(cli.progress);
    if !bridge::should_preserve_mock_loaded_fixture_for_next_cli_run() {
        bridge::reset_mock_loaded_fixture_scenario();
    }
    let (loaded_config, effective_config, instance_ctx) = resolve_instance_and_config(&cli)?;
    let context = AppContext {
        config: &effective_config,
        config_origin: &loaded_config.origin,
        config_provenance: &loaded_config.provenance,
        json_output: cli.json,
        mode: cli.mode,
        instance: instance_ctx.as_ref(),
    };
    if cli_uses_streaming(&cli) {
        record_usage_command(canonical_usage_command(&cli.command), context);
    }

    match cli.command {
        Commands::Dev(DevCommand {
            command: DevSubcommand::Logs(args),
        }) if args.follow => {
            stream_follow_logs(
                &args,
                context.json_output,
                writer,
                |request| {
                    bridge_client(context)
                        .logs(request)
                        .map_err(AppError::bridge)
                },
                None,
                Duration::from_millis(250),
            )?;
            Ok(0)
        }
        Commands::Service(ServiceCommand {
            command: ServiceSubcommand::Serve(args),
        }) => automation_service::serve(args, context, writer),
        Commands::State(args) if args.watch => {
            stream_state_watch_json(args, context, writer)?;
            Ok(0)
        }
        Commands::Events(args) if args.watch => {
            stream_combat_events_watch_json(args, context, writer)?;
            Ok(0)
        }
        _ => {
            let rendered = run_cli(cli)?;
            writer
                .write_all(rendered.stdout.as_bytes())
                .map_err(stdout_write_error)?;
            Ok(rendered.exit_code)
        }
    }
}

pub(crate) fn handle_state(
    args: StateArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    if args.watch {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "state_watch_requires_streaming",
                    "message": "state watch must run through the streaming CLI path."
                }
            }),
        });
    }
    render_value_success(execute_state_json(args, context)?, context.json_output)
}

pub(crate) fn handle_events(
    args: EventsArgs,
    _context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    // Combat events are push-only; they only exist on the streaming path with --watch.
    let (code, message) = if args.watch {
        (
            "events_watch_requires_streaming",
            "combat events watch must run through the streaming CLI path.",
        )
    } else {
        (
            "events_requires_watch",
            "sts2 events is push-only; pass --watch to stream combat events.",
        )
    };
    Err(AppError {
        exit_code: 2,
        payload: json!({ "error": { "code": code, "message": message } }),
    })
}

pub(crate) fn handle_act(
    command: ActCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    render_value_success(
        execute_action_json(command.command, context)?,
        context.json_output,
    )
}

pub(crate) fn handle_models(
    command: ModelCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    render_value_success_compact_json(execute_models_json(command, context)?, context.json_output)
}

pub(crate) fn handle_combat(
    command: CombatCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        CombatSubcommand::Preview(args) => render_value_success_compact_json(
            execute_combat_preview_json(args, context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn handle_map(
    command: MapCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        MapSubcommand::Drawings(args) => render_value_success_compact_json(
            execute_map_drawings_json(args, context)?,
            context.json_output,
        ),
        // Reuse the canonical draw-map-stroke action dispatch (also exposed as `act draw-map-stroke`).
        MapSubcommand::DrawStroke(args) => render_value_success(
            execute_action_json(ActSubcommand::DrawMapStroke(args), context)?,
            context.json_output,
        ),
    }
}

pub(crate) fn handle_reference(
    command: ReferenceCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    render_value_success_compact_json(
        execute_reference_json(command, context)?,
        context.json_output,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn write_config(contents: &str) -> (tempfile::TempDir, std::path::PathBuf) {
        let dir = tempfile::tempdir().expect("temp dir");
        let path = dir.path().join("sts2.local.yaml");
        fs::write(&path, contents).expect("write config");
        (dir, path)
    }

    fn cli_with_config(path: &std::path::Path, extra: &[&str]) -> Cli {
        let mut args = vec![
            "sts2".to_string(),
            "--config".to_string(),
            path.display().to_string(),
        ];
        args.extend(extra.iter().map(|value| value.to_string()));
        args.extend(["game".to_string(), "instances".to_string()]);
        Cli::parse_from(args)
    }

    #[test]
    fn config_default_instance_overlays_effective_config() {
        let (_dir, path) = write_config(
            r#"
transport:
  kind: ipc
instances:
  dir: ./.sts2/test-instances
  default: alpha
"#,
        );
        let cli = cli_with_config(&path, &[]);

        let (_loaded, effective, instance) =
            resolve_instance_and_config(&cli).expect("resolve instance");
        let instance = instance.expect("config default instance");

        assert_eq!(instance.name, "alpha");
        assert!(!instance.isolated);
        assert_eq!(
            effective.transport.ipc_path.as_deref(),
            Some(instance.socket.as_str())
        );
        assert_eq!(
            effective.game.user_dir.as_deref(),
            Some(instance.user_dir.display().to_string().as_str())
        );
    }

    #[test]
    fn explicit_instance_overrides_config_default() {
        let (_dir, path) = write_config(
            r#"
transport:
  kind: ipc
instances:
  default: alpha
"#,
        );
        let cli = cli_with_config(&path, &["--instance", "bravo"]);

        let (_loaded, _effective, instance) =
            resolve_instance_and_config(&cli).expect("resolve instance");

        assert_eq!(instance.expect("explicit instance").name, "bravo");
    }

    #[test]
    fn isolated_build_flag_overrides_false_config_default() {
        let (_dir, path) = write_config(
            r#"
game:
  launchExecutable: /tmp/SlayTheSpire2.x86_64
transport:
  kind: ipc
instances:
  default: alpha
  isolatedBuild: false
"#,
        );
        let cli = cli_with_config(&path, &["--isolated-build"]);

        let (_loaded, effective, instance) =
            resolve_instance_and_config(&cli).expect("resolve instance");
        let instance = instance.expect("config default instance");

        assert!(instance.isolated);
        assert_eq!(
            effective.game.mods_dir.as_deref(),
            Some(instance.mods_dir.display().to_string().as_str())
        );
        assert_eq!(
            effective.game.launch_executable.as_deref(),
            Some(instance.launch_executable.display().to_string().as_str())
        );
    }

    #[test]
    fn isolated_build_flag_requires_some_active_instance() {
        let (_dir, path) = write_config(
            r#"
transport:
  kind: ipc
"#,
        );
        let cli = cli_with_config(&path, &["--isolated-build"]);

        let error = resolve_instance_and_config(&cli).expect_err("isolated build should fail");

        assert_eq!(
            error.payload["error"]["code"],
            "isolated_build_requires_instance"
        );
    }

    #[test]
    fn isolated_config_default_overlays_isolated_paths() {
        let (_dir, path) = write_config(
            r#"
game:
  launchExecutable: /tmp/SlayTheSpire2.x86_64
transport:
  kind: ipc
instances:
  default: alpha
  isolatedBuild: true
"#,
        );
        let cli = cli_with_config(&path, &[]);

        let (_loaded, effective, instance) =
            resolve_instance_and_config(&cli).expect("resolve instance");
        let instance = instance.expect("config default instance");

        assert!(instance.isolated);
        assert_eq!(
            effective.game.mods_dir.as_deref(),
            Some(instance.mods_dir.display().to_string().as_str())
        );
        assert_eq!(
            effective.game.launch_executable.as_deref(),
            Some(instance.launch_executable.display().to_string().as_str())
        );
    }

    #[test]
    fn config_default_instance_rejects_non_ipc_transport() {
        let (_dir, path) = write_config(
            r#"
transport:
  kind: mock
instances:
  default: alpha
"#,
        );
        let cli = cli_with_config(&path, &[]);

        let error = resolve_instance_and_config(&cli).expect_err("mock transport should fail");

        assert_eq!(error.payload["error"]["code"], "instance_requires_ipc");
    }
}
