use super::*;

mod catalog;
pub(crate) use catalog::*;

pub(crate) fn ai_tools_catalog_json() -> Value {
    json!({
        "source": "command-catalog",
        "adapter": {
            "name": "sts2-mcp",
            "transport": "stdio",
            "sourceOfTruth": "sts2 CLI",
            "inspectCommand": "sts2 --json inspect ai-tools",
            "serviceModes": {
                "default": "stdio",
                "network": {
                    "command": "sts2 --json service serve --mcp-mode network",
                    "defaultBind": "127.0.0.1:4318",
                    "auth": "bearer",
                    "nonLoopbackRequirements": [
                        "--mcp-auth-token",
                        "--acknowledge-non-loopback-mcp-threat-model"
                    ]
                }
            },
            "notes": [
                "This catalog describes the thin machine-facing adapter surface for the repo-local MCP server.",
                "The safe default MCP path is local stdio; network MCP is an explicit service mode.",
                "Tool success payloads preserve the underlying CLI JSON output instead of inventing a second business-logic schema.",
                "Unsupported or scaffolded capabilities stay explicit through tool status, limitations, and structured CLI errors."
            ]
        },
        "tools": ai_tool_catalog()
    })
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StateToolArgs {
    view: Option<String>,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<String>,
    rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ActToolArgs {
    kind: String,
    choice_id: Option<String>,
    map_node_id: Option<String>,
    card_id: Option<String>,
    reward_id: Option<String>,
    bundle_id: Option<String>,
    shop_item_id: Option<String>,
    relic_id: Option<String>,
    rest_option_id: Option<String>,
    event_option_id: Option<String>,
    control_id: Option<String>,
    selected_tool: Option<String>,
    by: Option<String>,
    potion_id: Option<String>,
    target_id: Option<String>,
    character_id: Option<String>,
    display_name: Option<String>,
    player_id: Option<String>,
}

pub(crate) fn execute_ai_tool_json(
    name: &str,
    arguments: Value,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    match name {
        "game_info" => {
            record_usage_command("game info", context);
            execute_game_info_json(context)
        }
        "state" => {
            let parsed: StateToolArgs =
                parse_ai_tool_arguments(name, arguments, "state tool arguments")?;
            record_usage_command("state", context);
            execute_state_json(
                StateArgs {
                    view: parse_state_tool_view(parsed.view.as_deref())?,
                    perspective: parsed.perspective,
                    player_id: parsed.player_id,
                    rpc_timeout_ms: parsed.rpc_timeout_ms,
                    watch: false,
                    max_events: None,
                    timeout_ms: None,
                    poll_interval_ms: 250,
                    watch_mode: StateWatchModeArg::Auto,
                    fail_fast: false,
                },
                context,
            )
        }
        "act" => {
            let parsed: ActToolArgs =
                parse_ai_tool_arguments(name, arguments, "act tool arguments")?;
            let command = act_subcommand_from_tool_args(parsed)?;
            record_usage_command(canonical_act_usage_command(&command), context);
            execute_action_json(command, context)
        }
        _ => execute_ai_tool_via_cli(name, arguments, context),
    }
}

pub(crate) fn parse_ai_tool_arguments<T: for<'de> Deserialize<'de>>(
    tool_name: &str,
    arguments: Value,
    description: &str,
) -> Result<T, AppError> {
    serde_json::from_value(arguments).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_ai_tool_arguments",
                "message": format!("Invalid arguments for AI tool '{tool_name}': {source}"),
                "details": description,
            }
        }),
    })
}

pub(crate) fn parse_state_tool_view(value: Option<&str>) -> Result<Option<StateViewArg>, AppError> {
    match value {
        None | Some("") => Ok(None),
        Some("actions") => Ok(Some(StateViewArg::Actions)),
        Some(other) => Err(AppError::invalid_query(
            "view",
            &format!("Unknown state view '{other}'. Expected actions."),
        )),
    }
}

pub(crate) fn act_subcommand_from_tool_args(args: ActToolArgs) -> Result<ActSubcommand, AppError> {
    match args.kind.as_str() {
        "choose" => Ok(ActSubcommand::Choose(ChooseArgs {
            choice: required_ai_tool_string("act", "choiceId", args.choice_id)?,
            player_id: args.player_id,
        })),
        "confirm-selection" => Ok(ActSubcommand::ConfirmSelection(ConfirmSelectionArgs {
            cards: args.card_id.into_iter().collect(),
            player_id: args.player_id,
            reward: args.reward_id,
        })),
        "cancel-selection" => Ok(ActSubcommand::CancelSelection(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-map-node" => Ok(ActSubcommand::SelectMapNode(SelectMapNodeArgs {
            node: required_ai_tool_string("act", "mapNodeId", args.map_node_id)?,
            player_id: args.player_id,
        })),
        "end-turn" => Ok(ActSubcommand::EndTurn(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "cancel-end-turn" => Ok(ActSubcommand::CancelEndTurn(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "ready" => Ok(ActSubcommand::Ready(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "unready" => Ok(ActSubcommand::Unready(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-character" => Ok(ActSubcommand::SelectCharacter(SelectCharacterArgs {
            character: required_ai_tool_string("act", "characterId", args.character_id)?,
            player_id: args.player_id,
        })),
        "join-lobby-player" => Ok(ActSubcommand::JoinLobbyPlayer(JoinLobbyPlayerArgs {
            display_name: required_ai_tool_string("act", "displayName", args.display_name)?,
        })),
        "leave-lobby-player" => Ok(ActSubcommand::LeaveLobbyPlayer(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "claim-reward" => Ok(ActSubcommand::ClaimReward(RewardActionArgs {
            reward: required_ai_tool_string("act", "rewardId", args.reward_id)?,
            player_id: args.player_id,
        })),
        "skip-rewards" => Ok(ActSubcommand::SkipRewards(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-card" => Ok(ActSubcommand::SelectCard(CardActionArgs {
            card: required_ai_tool_string("act", "cardId", args.card_id)?,
            player_id: args.player_id,
            reward: args.reward_id,
        })),
        "skip-card-selection" => Ok(ActSubcommand::SkipCardSelection(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-bundle" => Ok(ActSubcommand::SelectBundle(BundleActionArgs {
            bundle: required_ai_tool_string("act", "bundleId", args.bundle_id)?,
            player_id: args.player_id,
        })),
        "buy-card" => Ok(ActSubcommand::BuyCard(ShopItemActionArgs {
            shop_item: required_ai_tool_string("act", "shopItemId", args.shop_item_id)?,
            player_id: args.player_id,
        })),
        "buy-relic" => Ok(ActSubcommand::BuyRelic(ShopItemActionArgs {
            shop_item: required_ai_tool_string("act", "shopItemId", args.shop_item_id)?,
            player_id: args.player_id,
        })),
        "buy-potion" => Ok(ActSubcommand::BuyPotion(ShopItemActionArgs {
            shop_item: required_ai_tool_string("act", "shopItemId", args.shop_item_id)?,
            player_id: args.player_id,
        })),
        "remove-card" => Ok(ActSubcommand::RemoveCard(ShopItemActionArgs {
            shop_item: required_ai_tool_string("act", "shopItemId", args.shop_item_id)?,
            player_id: args.player_id,
        })),
        "leave-shop" => Ok(ActSubcommand::LeaveShop(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "close-shop-inventory" => Ok(ActSubcommand::CloseShopInventory(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "rest" => Ok(ActSubcommand::Rest(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "smith" => Ok(ActSubcommand::Smith(SmithActionArgs {
            card: args.card_id,
            player_id: args.player_id,
        })),
        "use-rest-site-option" => Ok(ActSubcommand::UseRestSiteOption(RestSiteOptionActionArgs {
            option: required_ai_tool_string("act", "restOptionId", args.rest_option_id)?,
            player_id: args.player_id,
        })),
        "proceed-rest-site" => Ok(ActSubcommand::ProceedRestSite(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "open-chest" => Ok(ActSubcommand::OpenChest(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "take-relic" => Ok(ActSubcommand::TakeRelic(TakeRelicActionArgs {
            relic: required_ai_tool_string("act", "relicId", args.relic_id)?,
            player_id: args.player_id,
        })),
        "proceed-treasure-room" => Ok(ActSubcommand::ProceedTreasureRoom(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "back-from-map" => Ok(ActSubcommand::BackFromMap(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-event-option" => Ok(ActSubcommand::SelectEventOption(EventOptionActionArgs {
            event_option: required_ai_tool_string("act", "eventOptionId", args.event_option_id)?,
            player_id: args.player_id,
        })),
        "open-event-shop" => Ok(ActSubcommand::OpenEventShop(EventOptionActionArgs {
            event_option: required_ai_tool_string("act", "eventOptionId", args.event_option_id)?,
            player_id: args.player_id,
        })),
        "use-crystal-sphere-control" => Ok(ActSubcommand::UseCrystalSphereControl(
            CrystalSphereControlActionArgs {
                control: required_ai_tool_string("act", "controlId", args.control_id)?,
                selected_tool: args.selected_tool,
                player_id: args.player_id,
            },
        )),
        "proceed-event" => Ok(ActSubcommand::ProceedEvent(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "toggle-map" => Ok(ActSubcommand::ToggleMap(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "toggle-deck" => Ok(ActSubcommand::ToggleDeck(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "toggle-settings" => Ok(ActSubcommand::ToggleSettings(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "sort-deck-view" => Ok(ActSubcommand::SortDeckView(SortDeckViewActionArgs {
            by: required_ai_tool_string("act", "by", args.by)?,
            player_id: args.player_id,
        })),
        "toggle-deck-view-upgrades" => Ok(ActSubcommand::ToggleDeckViewUpgrades(
            PlayerScopedActionArgs {
                player_id: args.player_id,
            },
        )),
        "view-draw-pile" => Ok(ActSubcommand::ViewDrawPile(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "view-discard-pile" => Ok(ActSubcommand::ViewDiscardPile(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "view-exhaust-pile" => Ok(ActSubcommand::ViewExhaustPile(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "inspect-relic" => Ok(ActSubcommand::InspectRelic(InspectRelicActionArgs {
            relic: required_ai_tool_string("act", "relicId", args.relic_id)?,
            player_id: args.player_id,
        })),
        "close-inspect-relic" => Ok(ActSubcommand::CloseInspectRelic(PlayerScopedActionArgs {
            player_id: args.player_id,
        })),
        "select-hand-card" => Ok(ActSubcommand::SelectHandCard(CardActionArgs {
            card: required_ai_tool_string("act", "cardId", args.card_id)?,
            player_id: args.player_id,
            reward: None,
        })),
        "deselect-hand-card" => Ok(ActSubcommand::DeselectHandCard(CardActionArgs {
            card: required_ai_tool_string("act", "cardId", args.card_id)?,
            player_id: args.player_id,
            reward: None,
        })),
        "confirm-hand-selection" => Ok(ActSubcommand::ConfirmHandSelection(
            ConfirmHandSelectionArgs {
                cards: args.card_id.into_iter().collect(),
                player_id: args.player_id,
            },
        )),
        "use-potion" => Ok(ActSubcommand::UsePotion(UsePotionArgs {
            potion: required_ai_tool_string("act", "potionId", args.potion_id)?,
            target: args.target_id,
            player_id: args.player_id,
        })),
        "play-card" => Ok(ActSubcommand::PlayCard(PlayCardArgs {
            card: required_ai_tool_string("act", "cardId", args.card_id)?,
            target: args.target_id,
            player_id: args.player_id,
        })),
        other => Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_ai_tool_arguments",
                    "message": format!("Invalid act.kind '{other}'.")
                }
            }),
        }),
    }
}

pub(crate) fn required_ai_tool_string(
    tool_name: &str,
    field_name: &str,
    value: Option<String>,
) -> Result<String, AppError> {
    value.ok_or_else(|| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_ai_tool_arguments",
                "message": format!("AI tool '{tool_name}' requires '{field_name}'.")
            }
        }),
    })
}

pub(crate) fn execute_ai_tool_via_cli(
    name: &str,
    arguments: Value,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let argv = ai_tool_command_argv(name, arguments)?;
    let cli = Cli::try_parse_from(argv).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_ai_tool_arguments",
                "message": format!("Invalid arguments for AI tool '{name}': {source}"),
            }
        }),
    })?;
    if cli_uses_streaming(&cli) {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "unsupported_ai_tool",
                    "message": format!("AI tool '{name}' requires a streaming command and is not supported by the JSON tool dispatcher.")
                }
            }),
        });
    }

    let tool_context = AppContext {
        config: context.config,
        config_origin: context.config_origin,
        config_provenance: context.config_provenance,
        json_output: cli.json,
        mode: cli.mode,
        instance: context.instance,
    };
    record_usage_command(canonical_usage_command(&cli.command), tool_context);
    let rendered = dispatch_cli_command(cli.command, tool_context)?;
    let value = serde_json::from_str(&rendered.stdout).map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "invalid_ai_tool_output",
                "message": format!("AI tool '{name}' returned invalid JSON: {source}"),
            }
        }),
    })?;

    let _ = context;
    Ok(value)
}

pub(crate) fn ai_tool_command_argv(name: &str, arguments: Value) -> Result<Vec<String>, AppError> {
    let mut argv = vec!["sts2".to_string(), "--json".to_string()];
    let mut arguments = ai_tool_arguments_object(name, arguments)?;

    match name {
        "game_detect" => argv.extend(["game", "detect"].into_iter().map(str::to_string)),
        "toolchain_info" => argv.extend(["toolchain", "info"].into_iter().map(str::to_string)),
        "game_launch" => {
            argv.extend(["game", "launch"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--rpc-timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "rpcTimeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--verify-stable-ms",
                take_optional_scalar_string(name, &mut arguments, "verifyStableMs")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--no-detach-session",
                take_optional_bool(name, &mut arguments, "noDetachSession")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--disable-background-throttle",
                take_optional_bool(name, &mut arguments, "disableBackgroundThrottle")?,
            );
            push_trailing_scalar_args(
                name,
                &mut argv,
                take_optional_array(name, &mut arguments, "launchArgs")?,
            )?;
        }
        "game_attach" => {
            argv.extend(["game", "attach"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--rpc-timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "rpcTimeoutMs")?,
            );
        }
        "game_close" => {
            argv.extend(["game", "close"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--rpc-timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "rpcTimeoutMs")?,
            );
        }
        "game_deploy" => {
            argv.extend(["game", "deploy"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "path")?);
            push_optional_bool_flag(
                &mut argv,
                "--build",
                take_optional_bool(name, &mut arguments, "build")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--restart",
                take_optional_bool(name, &mut arguments, "restart")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--verify",
                take_optional_bool(name, &mut arguments, "verify")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--rpc-timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "rpcTimeoutMs")?,
            );
        }
        "console" => {
            let mode = take_optional_scalar_string(name, &mut arguments, "mode")?;
            if mode.as_deref().is_some_and(|mode| mode != "dangerous") {
                return Err(AppError::tool_invocation(
                    name,
                    "console.mode must be 'dangerous' when supplied.",
                ));
            }

            let command = take_required_scalar_string(name, &mut arguments, "command")?;
            let args = take_optional_array(name, &mut arguments, "args")?;
            if mode.as_deref() == Some("dangerous") {
                argv.push("--mode".to_string());
                argv.push("dangerous".to_string());
            }
            argv.push("dev".to_string());
            argv.push("console".to_string());
            argv.push(command);
            push_trailing_scalar_args(name, &mut argv, args)?;
        }
        "inspect_actions" => argv.extend(["inspect", "actions"].into_iter().map(str::to_string)),
        "inspect_viewport_presets" => {
            argv.extend(
                ["inspect", "viewport-presets"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--preset-catalog",
                take_optional_array(name, &mut arguments, "presetCatalogs")?,
            )?;
        }
        "inspect_reference_topics" => {
            argv.extend(
                ["inspect", "reference-topics"]
                    .into_iter()
                    .map(str::to_string),
            );
        }
        "logs" => {
            argv.extend(["dev", "logs"].into_iter().map(str::to_string));
            push_log_flags(name, &mut argv, &mut arguments)?;
        }
        "log_health" => {
            argv.extend(["dev", "log-health"].into_iter().map(str::to_string));
            push_log_flags(name, &mut argv, &mut arguments)?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--exclude-target",
                take_optional_array(name, &mut arguments, "excludeTargets")?,
            )?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--exclude-message-regex",
                take_optional_array(name, &mut arguments, "excludeMessageRegexes")?,
            )?;
        }
        "diagnostics" => {
            argv.extend(["dev", "diagnostics"].into_iter().map(str::to_string));
            push_log_flags(name, &mut argv, &mut arguments)?;
            push_optional_scalar_flag(
                &mut argv,
                "--preset",
                take_optional_scalar_string(name, &mut arguments, "preset")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--width",
                take_optional_scalar_string(name, &mut arguments, "width")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--height",
                take_optional_scalar_string(name, &mut arguments, "height")?,
            );
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--preset-catalog",
                take_optional_array(name, &mut arguments, "presetCatalogs")?,
            )?;
            push_optional_scalar_flag(
                &mut argv,
                "--bundle-dir",
                take_optional_scalar_string(name, &mut arguments, "bundleDir")?,
            );
        }
        "hot_reload_status" => {
            argv.extend(
                ["dev", "mod-reload", "status"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--project", &mut arguments, "project")?;
        }
        "hot_reload" => {
            argv.extend(["dev", "mod-reload"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--project", &mut arguments, "project")?;
            push_optional_bool_flag(
                &mut argv,
                "--build",
                take_optional_bool(name, &mut arguments, "build")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--wait",
                take_optional_bool(name, &mut arguments, "wait")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
        }
        "assets_extract" => {
            argv.extend(["assets", "extract"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--execution",
                take_optional_scalar_string(name, &mut arguments, "execution")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--format",
                take_optional_scalar_string(name, &mut arguments, "format")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "assets_explain" => {
            argv.extend(["assets", "explain"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--execution",
                take_optional_scalar_string(name, &mut arguments, "execution")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "assets_extract_batch" => {
            argv.extend(["assets", "extract-batch"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--manifest", &mut arguments, "manifest")?;
            push_optional_scalar_flag(
                &mut argv,
                "--output",
                take_optional_scalar_string(name, &mut arguments, "output")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--execution",
                take_optional_scalar_string(name, &mut arguments, "execution")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--format",
                take_optional_scalar_string(name, &mut arguments, "format")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--fail-fast",
                take_optional_bool(name, &mut arguments, "failFast")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--dry-run",
                take_optional_bool(name, &mut arguments, "dryRun")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "load_fixture" => {
            argv.extend(["dev", "fixture", "load"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--path", &mut arguments, "path")?;
        }
        "scenario_export" => {
            argv.extend(
                ["dev", "scenario", "export"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--output", &mut arguments, "output")?;
            push_optional_bool_flag(
                &mut argv,
                "--include-exact",
                take_optional_bool(name, &mut arguments, "includeExact")?,
            );
        }
        "scenario_load" => {
            argv.extend(["dev", "scenario", "load"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--path", &mut arguments, "path")?;
            push_optional_bool_flag(
                &mut argv,
                "--restart",
                take_optional_bool(name, &mut arguments, "restart")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--allow-degraded-local-multiplayer",
                take_optional_bool(name, &mut arguments, "allowDegradedLocalMultiplayer")?,
            );
        }
        "debug_status" => {
            argv.extend(["dev", "debug", "status"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
        }
        "debug_session_start" => {
            argv.extend(
                ["dev", "debug", "session", "start"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_optional_scalar_flag(
                &mut argv,
                "--name",
                take_optional_scalar_string(name, &mut arguments, "name")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--role",
                take_optional_scalar_string(name, &mut arguments, "role")?,
            );
            if take_optional_bool(name, &mut arguments, "pause")? {
                argv.push("--pause".to_string());
            }
            push_optional_scalar_flag(
                &mut argv,
                "--lease-timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "leaseTimeoutMs")?,
            );
        }
        "debug_events" => {
            argv.extend(["dev", "debug", "events"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--from-sequence",
                take_optional_scalar_string(name, &mut arguments, "fromSequence")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            if take_optional_bool(name, &mut arguments, "follow")? {
                argv.push("--follow".to_string());
            }
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
        }
        "debug_session_status" => {
            argv.extend(
                ["dev", "debug", "session", "status"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--id", &mut arguments, "sessionId")?;
        }
        "debug_session_end" => {
            argv.extend(
                ["dev", "debug", "session", "end"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--id", &mut arguments, "sessionId")?;
            if take_optional_bool(name, &mut arguments, "resume")? {
                argv.push("--resume".to_string());
            }
        }
        "debug_pause" => {
            argv.extend(["dev", "debug", "pause"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
        }
        "debug_resume" => {
            argv.extend(["dev", "debug", "resume"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
        }
        "debug_step" => {
            argv.extend(["dev", "debug", "step"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
            push_required_scalar_flag(name, &mut argv, "--kind", &mut arguments, "kind")?;
            push_optional_scalar_flag(
                &mut argv,
                "--count",
                take_optional_scalar_string(name, &mut arguments, "count")?,
            );
        }
        "debug_wait" => {
            argv.extend(["dev", "debug", "wait"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--session", &mut arguments, "sessionId")?;
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
        }
        "breakpoint_list" => {
            argv.extend(
                ["dev", "breakpoint", "list"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
        }
        "breakpoint_add" => {
            argv.extend(["dev", "breakpoint", "add"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
            push_required_scalar_flag(name, &mut argv, "--path", &mut arguments, "path")?;
            push_optional_scalar_flag(
                &mut argv,
                "--kind",
                take_optional_scalar_string(name, &mut arguments, "kind")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--name",
                take_optional_scalar_string(name, &mut arguments, "name")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--min-hit-count",
                take_optional_scalar_string(name, &mut arguments, "minHitCount")?,
            );
            if take_optional_bool(name, &mut arguments, "autoRemoveOnHit")? {
                argv.push("--auto-remove-on-hit".to_string());
            }
            if arguments.get("kind").and_then(Value::as_str) != Some("change") {
                push_predicate_flags(name, &mut argv, &mut arguments, true)?;
            }
        }
        "breakpoint_remove" => {
            argv.extend(
                ["dev", "breakpoint", "remove"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_optional_scalar_flag(
                &mut argv,
                "--session",
                take_optional_scalar_string(name, &mut arguments, "session")?,
            );
            push_required_scalar_flag(name, &mut argv, "--id", &mut arguments, "id")?;
        }
        "http" => {
            argv.extend(["dev", "http"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--url", &mut arguments, "url")?;
            push_http_probe_flags(name, &mut argv, &mut arguments)?;
        }
        "http_wait" => {
            argv.extend(["dev", "http-wait"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--url", &mut arguments, "url")?;
            push_http_probe_flags(name, &mut argv, &mut arguments)?;
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
        }
        "fetch" => {
            argv.extend(["dev", "fetch"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--source", &mut arguments, "source")?;
            push_optional_scalar_flag(
                &mut argv,
                "--output",
                take_optional_scalar_string(name, &mut arguments, "output")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--expect-sha256",
                take_optional_scalar_string(name, &mut arguments, "expectSha256")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--query",
                take_optional_scalar_string(name, &mut arguments, "query")?,
            );
            push_predicate_flags(name, &mut argv, &mut arguments, false)?;
        }
        "websocket" => {
            argv.extend(["dev", "websocket"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--url", &mut arguments, "url")?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--header",
                take_optional_array(name, &mut arguments, "headers")?,
            )?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--send-text",
                take_optional_array(name, &mut arguments, "sendText")?,
            )?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--expect-text",
                take_optional_array(name, &mut arguments, "expectText")?,
            )?;
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
        }
        "project_profile_list" => {
            argv.extend(
                ["project", "profile", "list"]
                    .into_iter()
                    .map(str::to_string),
            );
        }
        "project_profile_show" => {
            argv.extend(
                ["project", "profile", "show"]
                    .into_iter()
                    .map(str::to_string),
            );
            argv.push(take_required_scalar_string(name, &mut arguments, "name")?);
        }
        "project_profile_run" => {
            argv.extend(
                ["project", "profile", "run"]
                    .into_iter()
                    .map(str::to_string),
            );
            argv.push(take_required_scalar_string(name, &mut arguments, "name")?);
        }
        "project_hook_list" => {
            argv.extend(["project", "hook", "list"].into_iter().map(str::to_string));
        }
        "project_hook_show" => {
            argv.extend(["project", "hook", "show"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "name")?);
        }
        "project_hook_run" => {
            argv.extend(["project", "hook", "run"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "name")?);
            push_optional_scalar_flag(
                &mut argv,
                "--input",
                take_optional_serialized_json(name, &mut arguments, "input")?,
            );
        }
        "skill_install" => {
            argv.extend(["skill", "install"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--path",
                take_optional_scalar_string(name, &mut arguments, "path")?,
            );
        }
        "screenshot" => {
            argv.extend(["dev", "screenshot"].into_iter().map(str::to_string));
            push_screenshot_flags(name, &mut argv, &mut arguments)?;
            push_optional_scalar_flag(
                &mut argv,
                "--output",
                take_optional_scalar_string(name, &mut arguments, "output")?,
            );
        }
        "screenshot_diff" => {
            argv.extend(["dev", "screenshot-diff"].into_iter().map(str::to_string));
            push_required_scalar_flag(name, &mut argv, "--baseline", &mut arguments, "baseline")?;
            push_optional_scalar_flag(
                &mut argv,
                "--actual",
                take_optional_scalar_string(name, &mut arguments, "actual")?,
            );
            push_screenshot_flags(name, &mut argv, &mut arguments)?;
            push_optional_scalar_flag(
                &mut argv,
                "--bundle-dir",
                take_optional_scalar_string(name, &mut arguments, "bundleDir")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--max-diff-pixels",
                take_optional_scalar_string(name, &mut arguments, "maxDiffPixels")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--max-diff-ratio",
                take_optional_scalar_string(name, &mut arguments, "maxDiffRatio")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--pixel-tolerance",
                take_optional_scalar_string(name, &mut arguments, "pixelTolerance")?,
            );
            push_optional_bool_flag(
                &mut argv,
                "--ignore-alpha",
                take_optional_bool(name, &mut arguments, "ignoreAlpha")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--mask",
                take_optional_scalar_string(name, &mut arguments, "mask")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--regions",
                take_optional_scalar_string(name, &mut arguments, "regions")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--foreground-max-diff-ratio",
                take_optional_scalar_string(name, &mut arguments, "foregroundMaxDiffRatio")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--roi-max-diff-ratio",
                take_optional_scalar_string(name, &mut arguments, "roiMaxDiffRatio")?,
            );
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--required-comparison",
                take_optional_array(name, &mut arguments, "requiredComparisons")?,
            )?;
        }
        "snapshot_export" => {
            argv.extend(
                ["dev", "snapshot", "export"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--spec", &mut arguments, "spec")?;
            push_required_scalar_flag(name, &mut argv, "--output", &mut arguments, "output")?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--preset-catalog",
                take_optional_array(name, &mut arguments, "presetCatalogs")?,
            )?;
        }
        "snapshot_compare" => {
            argv.extend(
                ["dev", "snapshot", "compare"]
                    .into_iter()
                    .map(str::to_string),
            );
            push_required_scalar_flag(name, &mut argv, "--spec", &mut arguments, "spec")?;
            push_required_scalar_flag(name, &mut argv, "--baseline", &mut arguments, "baseline")?;
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--preset-catalog",
                take_optional_array(name, &mut arguments, "presetCatalogs")?,
            )?;
            push_optional_scalar_flag(
                &mut argv,
                "--bundle-dir",
                take_optional_scalar_string(name, &mut arguments, "bundleDir")?,
            );
        }
        "wait_for" => {
            argv.extend(["dev", "wait-for"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "path")?);
            push_predicate_flags(name, &mut argv, &mut arguments, true)?;
            push_optional_scalar_flag(
                &mut argv,
                "--timeout-ms",
                take_optional_scalar_string(name, &mut arguments, "timeoutMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--interval-ms",
                take_optional_scalar_string(name, &mut arguments, "intervalMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--perspective",
                take_optional_scalar_string(name, &mut arguments, "perspective")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--player-id",
                take_optional_scalar_string(name, &mut arguments, "playerId")?,
            );
        }
        "assert" => {
            argv.extend(["dev", "assert"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "path")?);
            push_predicate_flags(name, &mut argv, &mut arguments, true)?;
            push_optional_scalar_flag(
                &mut argv,
                "--perspective",
                take_optional_scalar_string(name, &mut arguments, "perspective")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--player-id",
                take_optional_scalar_string(name, &mut arguments, "playerId")?,
            );
        }
        "code_locate" => {
            argv.extend(["code", "locate"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "subject",
            )?);
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_describe" => {
            argv.extend(["code", "describe"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "subject",
            )?);
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_refs" => {
            argv.extend(["code", "refs"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "subject",
            )?);
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_derived" => {
            argv.extend(["code", "derived"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "subject",
            )?);
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_decompile" => {
            argv.extend(["code", "decompile"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "subject",
            )?);
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_bool_flag(
                &mut argv,
                "--full",
                take_optional_bool(name, &mut arguments, "full")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_hooks" => {
            argv.extend(["code", "hooks"].into_iter().map(str::to_string));
            if let Some(query) = take_optional_scalar_string(name, &mut arguments, "query")? {
                argv.push(query);
            }
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--offset",
                take_optional_scalar_string(name, &mut arguments, "offset")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--source",
                take_optional_scalar_string(name, &mut arguments, "source")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--assembly",
                take_optional_scalar_string(name, &mut arguments, "assembly")?,
            );
            push_repeated_scalar_flag(
                name,
                &mut argv,
                "--form",
                take_optional_array(name, &mut arguments, "form")?,
            )?;
            push_optional_bool_flag(
                &mut argv,
                "--has-script",
                take_optional_bool(name, &mut arguments, "hasScript")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--sort",
                take_optional_scalar_string(name, &mut arguments, "sort")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_hook_info" => {
            argv.extend(["code", "hook-info"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_scene_search" => {
            argv.extend(["code", "scene-search"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "query")?);
            push_optional_scalar_flag(
                &mut argv,
                "--limit",
                take_optional_scalar_string(name, &mut arguments, "limit")?,
            );
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_scene_tree" => {
            argv.extend(["code", "scene-tree"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "scene")?);
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "code_scene_node" => {
            argv.extend(["code", "scene-node"].into_iter().map(str::to_string));
            argv.push(take_required_scalar_string(name, &mut arguments, "scene")?);
            argv.push(take_required_scalar_string(
                name,
                &mut arguments,
                "nodePath",
            )?);
            push_code_search_root_flags(name, &mut argv, &mut arguments)?;
        }
        "test_run" => {
            argv.extend(["test", "run"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--artifacts-dir",
                take_optional_scalar_string(name, &mut arguments, "artifactsDir")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--failure-artifacts",
                take_optional_scalar_string(name, &mut arguments, "failureArtifacts")?,
            );
            argv.extend(test_run_input_argv(name, &mut arguments)?);
        }
        "test_stress" => {
            argv.extend(["test", "stress"].into_iter().map(str::to_string));
            push_optional_scalar_flag(
                &mut argv,
                "--artifacts-dir",
                take_optional_scalar_string(name, &mut arguments, "artifactsDir")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--failure-artifacts",
                take_optional_scalar_string(name, &mut arguments, "failureArtifacts")?,
            );
            let has_iterations = arguments.contains_key("iterations");
            let has_duration = arguments.contains_key("durationMs");
            if has_iterations == has_duration {
                return Err(invalid_ai_tool_arguments(
                    name,
                    "testStress requires exactly one of 'iterations' or 'durationMs'.",
                ));
            }
            push_optional_scalar_flag(
                &mut argv,
                "--iterations",
                take_optional_scalar_string(name, &mut arguments, "iterations")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--duration-ms",
                take_optional_scalar_string(name, &mut arguments, "durationMs")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--max-failures",
                take_optional_scalar_string(name, &mut arguments, "maxFailures")?,
            );
            push_optional_scalar_flag(
                &mut argv,
                "--cooldown-ms",
                take_optional_scalar_string(name, &mut arguments, "cooldownMs")?,
            );
            argv.push(take_required_scalar_string(name, &mut arguments, "path")?);
        }
        _ => {
            return Err(AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "unknown_ai_tool",
                        "message": format!("Unsupported AI tool '{name}'.")
                    }
                }),
            });
        }
    }

    ensure_no_extra_ai_tool_arguments(name, &arguments)?;
    Ok(argv)
}

pub(crate) fn ai_tool_arguments_object(
    tool_name: &str,
    arguments: Value,
) -> Result<Map<String, Value>, AppError> {
    match arguments {
        Value::Null => Ok(Map::new()),
        Value::Object(map) => Ok(map),
        _ => Err(invalid_ai_tool_arguments(
            tool_name,
            "tool arguments must be a JSON object",
        )),
    }
}

pub(crate) fn invalid_ai_tool_arguments(tool_name: &str, message: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "invalid_ai_tool_arguments",
                "message": format!("Invalid arguments for AI tool '{tool_name}': {message}"),
            }
        }),
    }
}

pub(crate) fn ensure_no_extra_ai_tool_arguments(
    tool_name: &str,
    arguments: &Map<String, Value>,
) -> Result<(), AppError> {
    if arguments.is_empty() {
        return Ok(());
    }

    let unknown = arguments.keys().cloned().collect::<Vec<_>>().join(", ");
    Err(invalid_ai_tool_arguments(
        tool_name,
        &format!("unknown argument field(s): {unknown}"),
    ))
}

pub(crate) fn take_required_scalar_string(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<String, AppError> {
    let Some(value) = arguments.remove(key) else {
        return Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("missing required field '{key}'"),
        ));
    };
    scalar_value_to_string(tool_name, key, value)
}

pub(crate) fn take_optional_scalar_string(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<Option<String>, AppError> {
    arguments
        .remove(key)
        .map(|value| scalar_value_to_string(tool_name, key, value))
        .transpose()
}

pub(crate) fn take_optional_bool(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<bool, AppError> {
    match arguments.remove(key) {
        None | Some(Value::Null) => Ok(false),
        Some(Value::Bool(value)) => Ok(value),
        Some(_) => Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("field '{key}' must be a boolean"),
        )),
    }
}

pub(crate) fn take_optional_array(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<Vec<Value>, AppError> {
    match arguments.remove(key) {
        None | Some(Value::Null) => Ok(Vec::new()),
        Some(Value::Array(values)) => Ok(values),
        Some(_) => Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("field '{key}' must be an array"),
        )),
    }
}

pub(crate) fn take_optional_serialized_json(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<Option<String>, AppError> {
    match arguments.remove(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) => Ok(Some(value)),
        Some(value @ Value::Object(_)) | Some(value @ Value::Array(_)) => {
            serde_json::to_string(&value)
                .map(Some)
                .map_err(|source| invalid_ai_tool_arguments(tool_name, &source.to_string()))
        }
        Some(_) => Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("field '{key}' must be a string, object, or array"),
        )),
    }
}

pub(crate) fn scalar_value_to_string(
    tool_name: &str,
    key: &str,
    value: Value,
) -> Result<String, AppError> {
    match value {
        Value::String(value) => Ok(value),
        Value::Number(value) => Ok(value.to_string()),
        Value::Bool(value) => Ok(value.to_string()),
        Value::Null => Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("field '{key}' must not be null"),
        )),
        _ => Err(invalid_ai_tool_arguments(
            tool_name,
            &format!("field '{key}' must be a scalar JSON value"),
        )),
    }
}

pub(crate) fn push_optional_scalar_flag(argv: &mut Vec<String>, flag: &str, value: Option<String>) {
    if let Some(value) = value {
        argv.push(flag.to_string());
        argv.push(value);
    }
}

pub(crate) fn push_optional_bool_flag(argv: &mut Vec<String>, flag: &str, enabled: bool) {
    if enabled {
        argv.push(flag.to_string());
    }
}

pub(crate) fn push_required_scalar_flag(
    tool_name: &str,
    argv: &mut Vec<String>,
    flag: &str,
    arguments: &mut Map<String, Value>,
    key: &str,
) -> Result<(), AppError> {
    argv.push(flag.to_string());
    argv.push(take_required_scalar_string(tool_name, arguments, key)?);
    Ok(())
}

pub(crate) fn push_repeated_scalar_flag(
    tool_name: &str,
    argv: &mut Vec<String>,
    flag: &str,
    values: Vec<Value>,
) -> Result<(), AppError> {
    for value in values {
        argv.push(flag.to_string());
        argv.push(scalar_value_to_string(tool_name, flag, value)?);
    }
    Ok(())
}

pub(crate) fn push_trailing_scalar_args(
    tool_name: &str,
    argv: &mut Vec<String>,
    values: Vec<Value>,
) -> Result<(), AppError> {
    if values.is_empty() {
        return Ok(());
    }

    argv.push("--".to_string());
    for value in values {
        argv.push(scalar_value_to_string(tool_name, "launchArgs", value)?);
    }
    Ok(())
}

pub(crate) fn push_log_flags(
    tool_name: &str,
    argv: &mut Vec<String>,
    arguments: &mut Map<String, Value>,
) -> Result<(), AppError> {
    push_optional_scalar_flag(
        argv,
        "--limit",
        take_optional_scalar_string(tool_name, arguments, "limit")?,
    );
    push_optional_scalar_flag(
        argv,
        "--tail",
        take_optional_scalar_string(tool_name, arguments, "tail")?,
    );
    push_optional_scalar_flag(
        argv,
        "--after-cursor",
        take_optional_scalar_string(tool_name, arguments, "afterCursor")?,
    );
    push_optional_scalar_flag(
        argv,
        "--level",
        take_optional_scalar_string(tool_name, arguments, "level")?,
    );
    push_optional_scalar_flag(
        argv,
        "--target",
        take_optional_scalar_string(tool_name, arguments, "target")?,
    );
    Ok(())
}

pub(crate) fn push_code_search_root_flags(
    tool_name: &str,
    argv: &mut Vec<String>,
    arguments: &mut Map<String, Value>,
) -> Result<(), AppError> {
    push_optional_scalar_flag(
        argv,
        "--game-path",
        take_optional_scalar_string(tool_name, arguments, "gamePath")?,
    );
    push_optional_scalar_flag(
        argv,
        "--assemblies-dir",
        take_optional_scalar_string(tool_name, arguments, "assembliesDir")?,
    );
    push_optional_scalar_flag(
        argv,
        "--resources-dir",
        take_optional_scalar_string(tool_name, arguments, "resourcesDir")?,
    );
    push_optional_scalar_flag(
        argv,
        "--mods-dir",
        take_optional_scalar_string(tool_name, arguments, "modsDir")?,
    );
    push_optional_bool_flag(
        argv,
        "--include-mods",
        take_optional_bool(tool_name, arguments, "includeMods")?,
    );
    push_optional_bool_flag(
        argv,
        "--include-dependencies",
        take_optional_bool(tool_name, arguments, "includeDependencies")?,
    );
    Ok(())
}

pub(crate) fn push_screenshot_flags(
    tool_name: &str,
    argv: &mut Vec<String>,
    arguments: &mut Map<String, Value>,
) -> Result<(), AppError> {
    push_optional_scalar_flag(
        argv,
        "--preset",
        take_optional_scalar_string(tool_name, arguments, "preset")?,
    );
    push_optional_scalar_flag(
        argv,
        "--width",
        take_optional_scalar_string(tool_name, arguments, "width")?,
    );
    push_optional_scalar_flag(
        argv,
        "--height",
        take_optional_scalar_string(tool_name, arguments, "height")?,
    );
    push_repeated_scalar_flag(
        tool_name,
        argv,
        "--preset-catalog",
        take_optional_array(tool_name, arguments, "presetCatalogs")?,
    )?;
    push_optional_scalar_flag(
        argv,
        "--rpc-timeout-ms",
        take_optional_scalar_string(tool_name, arguments, "rpcTimeoutMs")?,
    );
    Ok(())
}

pub(crate) fn push_http_probe_flags(
    tool_name: &str,
    argv: &mut Vec<String>,
    arguments: &mut Map<String, Value>,
) -> Result<(), AppError> {
    push_optional_scalar_flag(
        argv,
        "--method",
        take_optional_scalar_string(tool_name, arguments, "method")?,
    );
    push_repeated_scalar_flag(
        tool_name,
        argv,
        "--header",
        take_optional_array(tool_name, arguments, "headers")?,
    )?;
    push_optional_scalar_flag(
        argv,
        "--body",
        take_optional_scalar_string(tool_name, arguments, "body")?,
    );
    push_optional_scalar_flag(
        argv,
        "--timeout-ms",
        take_optional_scalar_string(tool_name, arguments, "timeoutMs")?,
    );
    push_optional_scalar_flag(
        argv,
        "--expect-status",
        take_optional_scalar_string(tool_name, arguments, "expectStatus")?,
    );
    push_repeated_scalar_flag(
        tool_name,
        argv,
        "--expect-header",
        take_optional_array(tool_name, arguments, "expectHeaders")?,
    )?;
    push_optional_scalar_flag(
        argv,
        "--query",
        take_optional_scalar_string(tool_name, arguments, "query")?,
    );
    push_predicate_flags(tool_name, argv, arguments, false)?;
    Ok(())
}

pub(crate) fn push_predicate_flags(
    tool_name: &str,
    argv: &mut Vec<String>,
    arguments: &mut Map<String, Value>,
    required: bool,
) -> Result<(), AppError> {
    let mut selected: Option<(String, Option<String>)> = None;
    for (field, flag) in [
        ("equals", "--equals"),
        ("contains", "--contains"),
        ("regex", "--regex"),
        ("gt", "--gt"),
        ("gte", "--gte"),
        ("lt", "--lt"),
        ("lte", "--lte"),
    ] {
        if let Some(value) = take_optional_scalar_string(tool_name, arguments, field)? {
            if selected.is_some() {
                return Err(invalid_ai_tool_arguments(
                    tool_name,
                    "exactly one predicate option is allowed",
                ));
            }
            selected = Some((flag.to_string(), Some(value)));
        }
    }

    for (field, flag) in [("exists", "--exists"), ("notExists", "--not-exists")] {
        if take_optional_bool(tool_name, arguments, field)? {
            if selected.is_some() {
                return Err(invalid_ai_tool_arguments(
                    tool_name,
                    "exactly one predicate option is allowed",
                ));
            }
            selected = Some((flag.to_string(), None));
        }
    }

    if required && selected.is_none() {
        return Err(invalid_ai_tool_arguments(
            tool_name,
            "one predicate option is required",
        ));
    }

    if let Some((flag, value)) = selected {
        argv.push(flag);
        if let Some(value) = value {
            argv.push(value);
        }
    }

    Ok(())
}

pub(crate) fn test_run_input_argv(
    tool_name: &str,
    arguments: &mut Map<String, Value>,
) -> Result<Vec<String>, AppError> {
    let profile = take_optional_scalar_string(tool_name, arguments, "profile")?;
    let tags = take_optional_array(tool_name, arguments, "tag")?;
    let path = take_optional_scalar_string(tool_name, arguments, "path")?;
    let inline = take_optional_scalar_string(tool_name, arguments, "inline")?;
    let scenario = arguments.remove("scenario");
    let count = usize::from(path.is_some())
        + usize::from(inline.is_some())
        + usize::from(scenario.is_some());
    if count == 0 && profile.is_none() {
        return Err(invalid_ai_tool_arguments(
            tool_name,
            "one of 'path', 'inline', 'scenario', or 'profile' is required",
        ));
    }
    if count > 1 {
        return Err(invalid_ai_tool_arguments(
            tool_name,
            "only one of 'path', 'inline', or 'scenario' is allowed",
        ));
    }

    let mut argv = Vec::new();
    if let Some(profile) = profile {
        argv.push("--profile".to_string());
        argv.push(profile);
    }
    push_repeated_scalar_flag(tool_name, &mut argv, "--tag", tags)?;
    if let Some(path) = path {
        argv.push(path);
        return Ok(argv);
    }
    if let Some(inline) = inline {
        argv.extend(["--inline".to_string(), inline]);
        return Ok(argv);
    }
    if scenario.is_none() {
        return Ok(argv);
    }

    let scenario = scenario.expect("scenario counted");
    let serialized = match scenario {
        Value::Object(_) | Value::Array(_) => serde_json::to_string(&scenario)
            .map_err(|source| invalid_ai_tool_arguments(tool_name, &source.to_string()))?,
        _ => {
            return Err(invalid_ai_tool_arguments(
                tool_name,
                "field 'scenario' must be a JSON object or array",
            ));
        }
    };

    argv.extend(["--inline".to_string(), serialized]);
    Ok(argv)
}

pub(crate) fn handle_inspect(
    command: InspectCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        InspectSubcommand::Commands => render_success(
            json!({
                "source": "command-catalog",
                "commands": COMMANDS
            }),
            context.json_output,
        ),
        InspectSubcommand::Examples(args) => render_success(
            json!({
                "source": "command-catalog",
                "examples": catalog_examples(args.command.as_deref())
            }),
            context.json_output,
        ),
        InspectSubcommand::Actions(args) => render_value_success(
            execute_inspect_actions_json(args, context)?,
            context.json_output,
        ),
        InspectSubcommand::StateSchema => {
            render_value_success(state_schema_json(), context.json_output)
        }
        InspectSubcommand::ReferenceTopics => {
            render_value_success(reference_topics_json(), context.json_output)
        }
        InspectSubcommand::ViewportPresets(args) => {
            let presets = effective_viewport_presets(context, &args.preset_catalogs)?;
            render_value_success(viewport_presets_json(&presets), context.json_output)
        }
        InspectSubcommand::AiTools => {
            render_value_success(ai_tools_catalog_json(), context.json_output)
        }
    }
}
