use crate::{
    AppContext, AppError, ConsoleCompletionArgs, ConsoleSubcommand, DevConsoleArgs,
    RenderedCommand, bridge, bridge_client, render_value_success,
};

mod static_reference;

pub(crate) fn console_command_requirement(command: &str) -> &'static str {
    match command.trim().to_ascii_lowercase().as_str() {
        "afflict" | "block" | "damage" | "draw" | "enchant" | "energy" | "kill" | "power"
        | "stars" | "upgrade" | "win" => "combat",
        "act" | "ancient" | "card" | "die" | "event" | "fight" | "godmode" | "gold" | "heal"
        | "potion" | "relic" | "remove_card" | "room" | "travel" => "run",
        "achievement" | "art" | "bestiary" | "cloud" | "dump" | "getlogs" | "help" | "instant"
        | "leaderboard" | "log" | "multiplayer" | "open" | "screenshot" | "sentry" | "trailer"
        | "unlock" => "none",
        _ => "unknown",
    }
}

pub(crate) fn console_completion_preceding(
    args: &ConsoleCompletionArgs,
) -> Result<Vec<String>, AppError> {
    if args.command.is_empty()
        || std::iter::once(&args.command)
            .chain(args.preceding.iter())
            .chain(std::iter::once(&args.prefix))
            .any(|token| token.chars().any(char::is_whitespace))
    {
        return Err(AppError::invalid_console_command());
    }
    let position = args.position.unwrap_or(args.preceding.len());
    if position > u32::MAX as usize {
        return Err(AppError::invalid_console_command());
    }
    let mut preceding = args.preceding.clone();
    preceding.resize(position, String::new());
    Ok(preceding)
}

pub(crate) fn handle_console(
    args: DevConsoleArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let value = match args.discovery {
        Some(ConsoleSubcommand::Commands) => {
            let root = crate::toolchain::resolve_managed_roots(
                context.config,
                &context.config_origin.base_dir,
            )
            .toolchain_dir
            .join("decompile/sts2");
            static_reference::resolve_discovery(
                bridge_client(context).discover_console_commands(
                    bridge::proto::ConsoleCommandCatalogRequest {
                        request_id: "cli-dev-console-commands".into(),
                    },
                ),
                &root,
                |response| bridge::console_catalog_json(&response),
                |root| static_reference::catalog(root, "cli-dev-console-commands"),
            )?
        }
        Some(ConsoleSubcommand::Args(args)) => {
            let preceding = console_completion_preceding(&args)?;
            let request = bridge::proto::ConsoleCompletionRequest {
                request_id: "cli-dev-console-args".into(),
                command: args.command,
                args: preceding.clone(),
                prefix: args.prefix.clone(),
            };
            let root = crate::toolchain::resolve_managed_roots(
                context.config,
                &context.config_origin.base_dir,
            )
            .toolchain_dir
            .join("decompile/sts2");
            static_reference::resolve_discovery(
                bridge_client(context).discover_console_arguments(request.clone()),
                &root,
                |response| bridge::console_completion_json(&response, &preceding, &args.prefix),
                |root| static_reference::complete(root, &request),
            )?
        }
        None => {
            return render_value_success(
                crate::commands::execute_console_json(
                    crate::ConsoleArgs {
                        command: args.command.ok_or_else(AppError::invalid_console_command)?,
                        args: args.args,
                    },
                    context,
                )?,
                context.json_output,
            );
        }
    };
    if context.json_output {
        return render_value_success(value, true);
    }
    let lines: Vec<String> = if let Some(commands) = value["commands"].as_array() {
        commands
            .iter()
            .map(|cmd| {
                format!(
                    "{}  {}",
                    cmd["name"].as_str().unwrap_or_default(),
                    cmd["usage"].as_str().unwrap_or_default()
                )
            })
            .collect()
    } else {
        value["candidateDetails"]
            .as_array()
            .into_iter()
            .flatten()
            .map(|candidate| {
                let value = candidate["value"].as_str().unwrap_or_default();
                match candidate["label"].as_str() {
                    Some(label) => format!("{value}  {label}"),
                    None => value.to_string(),
                }
            })
            .collect()
    };
    Ok(RenderedCommand {
        stdout: if lines.is_empty() {
            String::new()
        } else {
            lines.join("\n") + "\n"
        },
        exit_code: 0,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn console_completion_position_defaults_truncates_and_pads() {
        let mut args = ConsoleCompletionArgs {
            command: "card".into(),
            preceding: vec!["STRIKE".into()],
            prefix: "de".into(),
            position: None,
        };
        assert_eq!(console_completion_preceding(&args).unwrap(), ["STRIKE"]);
        args.position = Some(0);
        assert!(console_completion_preceding(&args).unwrap().is_empty());
        args.position = Some(3);
        assert_eq!(
            console_completion_preceding(&args).unwrap(),
            ["STRIKE", "", ""]
        );
    }

    #[test]
    fn console_completion_rejects_whitespace_in_every_token() {
        for (command, preceding, prefix) in [
            ("two words", "ok", "ok"),
            ("card", "two\twords", "ok"),
            ("card", "ok", "two\nwords"),
        ] {
            let args = ConsoleCompletionArgs {
                command: command.into(),
                preceding: vec![preceding.into()],
                prefix: prefix.into(),
                position: Some(0),
            };
            assert_eq!(
                console_completion_preceding(&args).unwrap_err().payload["error"]["code"],
                "invalid_console_command"
            );
        }
    }

    #[test]
    fn console_catalog_enriches_requirements_and_dangerous_commands() {
        use crate::commands::console_command_requires_dangerous;
        for command in [
            "fight",
            "room",
            "event",
            "ancient",
            "die",
            "heal",
            "card",
            "remove_card",
            "godmode",
        ] {
            assert_eq!(console_command_requirement(command), "run", "{command}");
        }
        for command in ["draw", "upgrade", "power", "energy", "stars", "kill", "win"] {
            assert_eq!(console_command_requirement(command), "combat", "{command}");
        }
        for command in ["achievement", "cloud", "unlock"] {
            assert_eq!(console_command_requirement(command), "none");
            assert!(console_command_requires_dangerous(command));
        }
        assert_eq!(console_command_requirement("screenshot"), "none");
        assert!(!console_command_requires_dangerous("screenshot"));
        assert_eq!(console_command_requirement("MOD_COMMAND"), "unknown");
        assert!(!console_command_requires_dangerous("MOD_COMMAND"));
    }
}

#[cfg(test)]
mod output_tests {
    use super::*;

    #[test]
    fn console_completion_json_preserves_labels_and_deduplicates_values_in_order() {
        let response = bridge::proto::ConsoleCompletionResponse {
            candidates: vec![
                "KNIGHTS_ELITE".into(),
                "SLIMES".into(),
                "KNIGHTS_ELITE".into(),
            ],
            candidate_details: vec![bridge::proto::ConsoleCompletionCandidate {
                value: "KNIGHTS_ELITE".into(),
                label: "Knight Gang".into(),
                raw: "KNIGHTS_ELITE (Knight Gang)".into(),
            }],
            ..Default::default()
        };
        let value = bridge::console_completion_json(&response, &[], "");
        assert_eq!(
            value["candidates"],
            serde_json::json!(["KNIGHTS_ELITE", "SLIMES"])
        );
        assert_eq!(value["count"], 2);
        assert_eq!(
            value["candidateDetails"],
            serde_json::json!([
                {"value": "KNIGHTS_ELITE", "label": "Knight Gang"},
                {"value": "SLIMES", "label": null},
            ])
        );
    }

    #[test]
    fn console_catalog_json_preserves_metadata_and_enriches_unknown_commands() {
        let response = bridge::proto::ConsoleCommandCatalogResponse {
            request_id: "catalog".into(),
            source: bridge::proto::DataSource::Live as i32,
            commands: vec![
                bridge::proto::ConsoleCommandDescriptor {
                    name: "unlock".into(),
                    usage: "<kind>".into(),
                    description: "test".into(),
                    networked: true,
                    debug_only: true,
                },
                bridge::proto::ConsoleCommandDescriptor {
                    name: "modded".into(),
                    ..Default::default()
                },
            ],
            notices: vec![bridge::proto::ConsoleCommandNotice {
                code: "notice".into(),
                message: "test notice".into(),
                provisional: true,
            }],
            ..Default::default()
        };
        let value = bridge::console_catalog_json(&response);
        assert_eq!(value["requestId"], "catalog");
        assert_eq!(value["source"], "live");
        assert_eq!(value["complete"], true);
        assert_eq!(value["count"], 2);
        assert_eq!(value["commands"][0]["dangerous"], true);
        assert_eq!(value["commands"][0]["requires"], "none");
        assert_eq!(value["commands"][0]["usage"], "<kind>");
        assert_eq!(value["commands"][0]["networked"], true);
        assert_eq!(value["commands"][0]["debugOnly"], true);
        assert_eq!(value["commands"][1]["dangerous"], false);
        assert_eq!(value["commands"][1]["requires"], "unknown");
        assert_eq!(
            value["notices"][0],
            serde_json::json!({"code":"notice", "message":"test notice", "provisional":true})
        );
        let value = bridge::console_catalog_json(&bridge::proto::ConsoleCommandCatalogResponse {
            source: bridge::proto::DataSource::Stub as i32,
            provisional: true,
            ..Default::default()
        });
        assert_eq!(value["source"], "stub");
        assert_eq!(value["provisional"], true);
        assert_eq!(value["complete"], false);
    }
}
