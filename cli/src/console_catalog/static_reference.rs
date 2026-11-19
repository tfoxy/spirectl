use std::{fs, path::Path};

use regex::Regex;
use serde_json::{Value, json};

use crate::{AppError, bridge};

const COMMAND_NAMESPACE: &str = "MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.";
const MODEL_NAMESPACE: &str = "MegaCrit.Sts2.Core.Models.";

pub(super) fn resolve_discovery<T>(
    result: Result<Result<T, bridge::proto::BridgeError>, bridge::proto::BridgeError>,
    root: &Path,
    live: impl FnOnce(T) -> Value,
    fallback: impl FnOnce(&Path) -> Option<Value>,
) -> Result<Value, AppError> {
    match result {
        Ok(response) => response.map(live).map_err(AppError::bridge),
        Err(error) => {
            use bridge::proto::BridgeErrorCode;
            if matches!(
                BridgeErrorCode::try_from(error.code),
                Ok(BridgeErrorCode::IpcSocketMissing
                    | BridgeErrorCode::IpcConnectionFailed
                    | BridgeErrorCode::TransportConnectionFailed
                    | BridgeErrorCode::TransportUnavailable)
            ) {
                if let Some(value) = fallback(root) {
                    return Ok(value);
                }
                let mut error = AppError::bridge(error);
                error.payload["error"]["hint"] = json!(
                    "Create the local decompiled reference with `sts2 project recover --kind decompile`."
                );
                Err(error)
            } else {
                Err(AppError::bridge(error))
            }
        }
    }
}

fn source_files(root: &Path) -> Option<Vec<(String, String)>> {
    let mut files = Vec::new();
    for entry in fs::read_dir(root).ok()? {
        let entry = entry.ok()?;
        let name = entry.file_name().to_string_lossy().into_owned();
        let relevant = name.starts_with(COMMAND_NAMESPACE)
            || [
                "Encounters",
                "Events",
                "Cards",
                "Relics",
                "Potions",
                "Powers",
                "Enchantments",
                "Afflictions",
            ]
            .iter()
            .any(|family| name.starts_with(&format!("{MODEL_NAMESPACE}{family}.")))
            || name == "MegaCrit.Sts2.Core.Rooms.RoomType.cs"
            || name == "MegaCrit.Sts2.Core.Entities.Cards.PileType.cs";
        if relevant && name.ends_with(".cs") && entry.file_type().ok()?.is_file() {
            files.push((name, fs::read_to_string(entry.path()).ok()?));
        }
    }
    files.sort_by(|a, b| a.0.cmp(&b.0));
    Some(files)
}

fn literal(source: &str, property: &str) -> Option<String> {
    let regex = Regex::new(&format!(
        r#"\b{}\s*=>\s*("(?:\\.|[^"\\])*")"#,
        regex::escape(property)
    ))
    .ok()?;
    // The common escapes in C# metadata literals are also JSON escapes.
    serde_json::from_str(&regex.captures(source)?[1]).ok()
}

fn descriptors(files: &[(String, String)]) -> Vec<bridge::proto::ConsoleCommandDescriptor> {
    let mut commands = Vec::new();
    for (name, source) in files {
        let Some(class) = name.strip_prefix(COMMAND_NAMESPACE) else {
            continue;
        };
        if class.contains('.') && !class.ends_with("ConsoleCmd.cs") {
            continue;
        }
        let Some(class) = class.strip_suffix("ConsoleCmd.cs") else {
            continue;
        };
        if class.contains('.') || class.starts_with("Abstract") {
            continue;
        }
        let Some(name) = literal(source, "CmdName") else {
            continue;
        };
        commands.push(bridge::proto::ConsoleCommandDescriptor {
            name,
            usage: literal(source, "Args").unwrap_or_default(),
            description: literal(source, "Description").unwrap_or_default(),
            networked: Regex::new(r"\bIsNetworked\s*=>\s*true\b")
                .unwrap()
                .is_match(source),
            debug_only: !Regex::new(r"\bDebugOnly\s*=>\s*false\b")
                .unwrap()
                .is_match(source),
        });
    }
    commands.sort_by(|a, b| a.name.cmp(&b.name));
    commands
}

fn mark_static(mut value: Value) -> Value {
    value["source"] = json!("static-reference");
    value["complete"] = json!(false);
    value["provisional"] = json!(true);
    value["notices"] = json!([{
        "code": "static-fallback-incomplete",
        "message": "approximated from the local decompiled reference without a running game; mods and content filters can add or remove commands and ids",
        "provisional": true,
    }]);
    value
}

pub(super) fn catalog(root: &Path, request_id: &str) -> Option<Value> {
    let commands = descriptors(&source_files(root)?);
    if commands.is_empty() {
        return None;
    }
    Some(mark_static(bridge::console_catalog_json(
        &bridge::proto::ConsoleCommandCatalogResponse {
            request_id: request_id.into(),
            commands,
            ..Default::default()
        },
    )))
}

fn slugify(name: &str) -> String {
    let mut slug = String::new();
    let mut previous = None;
    for ch in name.trim().chars() {
        if ch.is_ascii_uppercase() && previous.is_some_and(|p: char| p.is_ascii_alphanumeric()) {
            slug.push('_');
        }
        if ch.is_ascii_alphanumeric() || ch == '_' {
            slug.push(ch.to_ascii_uppercase());
        } else if ch.is_whitespace() && !slug.ends_with('_') {
            slug.push('_');
        }
        previous = Some(ch);
    }
    slug
}

fn model_ids(
    files: &[(String, String)],
    family: &str,
    ancient: Option<bool>,
    include_mocks: bool,
) -> Vec<String> {
    let prefix = format!("{MODEL_NAMESPACE}{family}.");
    let declaration =
        Regex::new(r"\bpublic\s+(?:(?:sealed|partial)\s+)*class\s+\w+\s*:\s*(\w+)").unwrap();
    files
        .iter()
        .filter_map(|(name, source)| {
            let class = name.strip_prefix(&prefix)?.strip_suffix(".cs")?;
            // Potion, power, enchantment, affliction and relic candidates may include mocks.
            // Content membership remains approximate without the live registry.
            if !include_mocks && class.starts_with("Mocks.") {
                return None;
            }
            let base = declaration.captures(source)?;
            if ancient.is_some_and(|wanted| wanted != (&base[1] == "AncientEventModel")) {
                return None;
            }
            Some(slugify(class.rsplit('.').next()?))
        })
        .collect()
}

fn enum_values(files: &[(String, String)], full_name: &str) -> Vec<String> {
    let Some((_, source)) = files
        .iter()
        .find(|(name, _)| name == &format!("{full_name}.cs"))
    else {
        return Vec::new();
    };
    let enum_body = Regex::new(r"(?s)\benum\s+\w+[^\{]*\{([^}]*)\}").unwrap();
    let Some(capture) = enum_body.captures(source) else {
        return Vec::new();
    };
    let comments = Regex::new(r"(?m)//[^\n]*|(?s:/\*.*?\*/)").unwrap();
    let body = comments.replace_all(&capture[1], "");
    body.split(',')
        .filter_map(|entry| {
            let name = entry.split('=').next()?.trim();
            (!name.is_empty()
                && name
                    .chars()
                    .all(|ch| ch.is_ascii_alphanumeric() || ch == '_'))
            .then(|| name.into())
        })
        .collect()
}

fn candidates(
    files: &[(String, String)],
    command: &str,
    position: usize,
    prefix: &str,
) -> Option<(Vec<String>, &'static str)> {
    let ids = |family, include_mocks| model_ids(files, family, None, include_mocks);
    let fixed = |items: &[&str]| items.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    let mut kind = "argument";
    let values = match (command, position) {
        ("fight", 0) => ids("Encounters", false),
        ("event", 0) => ids("Events", false),
        ("ancient", 0) => model_ids(files, "Events", Some(true), false),
        ("card" | "remove_card", 0) => ids("Cards", false),
        ("potion", 0) => ids("Potions", true),
        ("power", 0) => ids("Powers", true),
        ("enchant", 0) => ids("Enchantments", true),
        ("afflict", 0) => ids("Afflictions", true),
        ("relic", 0) => {
            let subcommands: Vec<_> = fixed(&["add", "remove"])
                .into_iter()
                .filter(|s| s.starts_with(&prefix.to_ascii_lowercase()))
                .collect();
            if !subcommands.is_empty() {
                kind = "subcommand";
                subcommands
            } else {
                ids("Relics", true)
            }
        }
        ("relic", 1) => ids("Relics", true),
        ("room", 0) => enum_values(files, "MegaCrit.Sts2.Core.Rooms.RoomType")
            .into_iter()
            .filter(|s| s != "Unassigned")
            .collect(),
        ("card", 1) => enum_values(files, "MegaCrit.Sts2.Core.Entities.Cards.PileType"),
        ("art", 0) => fixed(&["affliction", "card", "enchantment", "power", "relic"]),
        ("open", 0) => fixed(&["logs", "saves", "root", "build-logs", "loc-override"]),
        ("unlock", 0) => fixed(&[
            "cards",
            "potions",
            "relics",
            "monsters",
            "events",
            "acts",
            "epochs",
            "ascensions",
            "all",
        ]),
        _ => return None,
    };
    let prefix = prefix.to_ascii_lowercase();
    Some((
        values
            .into_iter()
            .filter(|value| {
                let value = value.to_ascii_lowercase();
                if command == "unlock" || (command == "relic" && kind == "argument") {
                    value.contains(&prefix)
                } else {
                    value.starts_with(&prefix)
                }
            })
            .collect(),
        kind,
    ))
}

pub(super) fn complete(
    root: &Path,
    request: &bridge::proto::ConsoleCompletionRequest,
) -> Option<Value> {
    let files = source_files(root)?;
    let commands = descriptors(&files);
    if commands.is_empty() {
        return None;
    }
    let command = request.command.to_ascii_lowercase();
    let known = commands.iter().any(|cmd| cmd.name == command);
    let rule = if known {
        candidates(&files, &command, request.args.len(), &request.prefix)
    } else {
        Some((
            commands
                .into_iter()
                .filter(|cmd| cmd.name.to_ascii_lowercase().starts_with(&command))
                .map(|cmd| cmd.name)
                .collect(),
            "command",
        ))
    };
    let unsupported = rule.is_none();
    let (mut candidates, kind) = rule.unwrap_or_default();
    candidates.sort();
    candidates.dedup();
    let mut common_prefix = candidates.first().cloned().unwrap_or_default();
    for candidate in &candidates {
        while !candidate
            .to_ascii_lowercase()
            .starts_with(&common_prefix.to_ascii_lowercase())
        {
            common_prefix.pop();
        }
    }
    if !common_prefix.is_empty() {
        if known {
            common_prefix = if request.args.is_empty() {
                format!("{command} {common_prefix}")
            } else {
                format!("{command} {} {common_prefix}", request.args.join(" "))
            };
        }
        if candidates.len() == 1 {
            common_prefix.push(' ');
        }
    }
    let response = bridge::proto::ConsoleCompletionResponse {
        request_id: request.request_id.clone(),
        command: request.command.clone(),
        known_command: known,
        argument_index: request.args.len() as u32,
        candidates,
        common_prefix,
        completion_type: if unsupported {
            "argument".into()
        } else {
            kind.into()
        },
        ..Default::default()
    };
    let mut value = mark_static(bridge::console_completion_json(
        &response,
        &request.args,
        &request.prefix,
    ));
    if unsupported {
        value["notices"].as_array_mut()?.push(json!({
            "code": "static-fallback-unsupported", "message": "No static completion rule for this command and argument position.", "provisional": true,
        }));
    }
    Some(value)
}

#[cfg(test)]
mod tests;
