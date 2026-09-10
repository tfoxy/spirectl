use crate::lifecycle;
use crate::{
    AppContext, AppError, BridgeHealthArgs, GameLaunchArgs, LifecycleWaitArgs, VisualPreflightArgs,
    bridge_health_json_for_dev,
};
use serde_json::{Value, json};

fn value_array_strings(value: &Value) -> Vec<String> {
    value
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(str::to_string)
        .collect()
}

fn push_unique_command(commands: &mut Vec<String>, command: String) {
    if !commands.iter().any(|existing| existing == &command) {
        commands.push(command);
    }
}

fn catalog_render_targets(encounter_id: &str) -> Vec<Value> {
    match encounter_id {
        "kaiser_crab_boss" => vec![
            json!({
                "id": "kaiser-background",
                "kind": "background",
                "query": "composed://encounters/kaiser_crab_boss/background/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "framing"]
            }),
            json!({
                "id": "kaiser-rocket-overlay",
                "kind": "overlay",
                "query": "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "isolation"]
            }),
            json!({
                "id": "kaiser-rocket-part",
                "kind": "part",
                "query": "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "bounds", "isolation"]
            }),
        ],
        "ovicopter_normal" => vec![
            json!({
                "id": "ovicopter-background",
                "kind": "background",
                "query": "composed://encounters/ovicopter_normal/background/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "framing"]
            }),
            json!({
                "id": "ovicopter-lay-eggs-overlay",
                "kind": "overlay",
                "query": "composed://encounters/ovicopter_normal/visual-state/lay-eggs/overlay/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "isolation"]
            }),
            json!({
                "id": "ovicopter-body-part",
                "kind": "part",
                "query": "composed://encounters/ovicopter_normal/visual-part/body/state/lay-eggs/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "bounds", "isolation"]
            }),
        ],
        "knowledge_demon_boss" => vec![
            json!({
                "id": "knowledge-demon-background",
                "kind": "background",
                "query": "composed://encounters/knowledge_demon_boss/background/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "framing"]
            }),
            json!({
                "id": "knowledge-demon-heavy-attack-burnt-overlay",
                "kind": "overlay",
                "query": "composed://encounters/knowledge_demon_boss/visual-state/heavy-attack-burnt/overlay/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "isolation"]
            }),
            json!({
                "id": "knowledge-demon-burn-fire-part",
                "kind": "part",
                "query": "composed://encounters/knowledge_demon_boss/visual-part/burn-fire/state/heavy-attack-burnt/image",
                "execution": "live",
                "format": "png",
                "artifactChecks": ["nonblank", "transparent-background", "bounds", "isolation"]
            }),
        ],
        _ => Vec::new(),
    }
}

pub(crate) fn execute_visual_preflight_json(
    args: VisualPreflightArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let mut launch_attach_action = "none";

    let health = bridge_health_json_for_dev(
        BridgeHealthArgs {
            non_mutating: !args.repair_stale_endpoint,
            repair_stale_endpoint: args.repair_stale_endpoint,
            verbose: true,
            check_source: false,
            rpc_timeout_ms: args.rpc_timeout_ms,
        },
        context,
    );

    let mut diagnostics = vec![health.clone()];

    if args.launch {
        launch_attach_action = "launch";
        diagnostics.push(lifecycle::execute_game_launch_with_args_json(
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
                launch_args: vec![],
            },
            context,
        )?);
    } else if args.attach {
        launch_attach_action = "attach";
        diagnostics.push(lifecycle::execute_game_attach_json(
            LifecycleWaitArgs {
                timeout_ms: args.timeout_ms,
                interval_ms: args.interval_ms,
                rpc_timeout_ms: args.rpc_timeout_ms,
            },
            context,
        )?);
    }

    let handshake_health = bridge_health_json_for_dev(
        BridgeHealthArgs {
            non_mutating: true,
            repair_stale_endpoint: false,
            verbose: true,
            check_source: false,
            rpc_timeout_ms: args.rpc_timeout_ms,
        },
        context,
    );
    diagnostics.push(handshake_health.clone());

    let status = handshake_health["status"]
        .as_str()
        .unwrap_or("unreachable_bridge");
    let code = handshake_health["code"].as_str().unwrap_or(status);
    let message = handshake_health["message"]
        .as_str()
        .unwrap_or("Live bridge is not reachable after visual preflight.");
    let endpoint = handshake_health["endpoint"].clone();
    let bridge_version = handshake_health["liveBridge"]["bridgeVersion"].clone();
    let latest_log = handshake_health["latestLog"].clone();
    let log_path = handshake_health["logPath"].clone();
    let stale_cleanup = health["staleCleanup"].clone();

    let render_targets = catalog_render_targets(&args.encounter);
    let catalog_status = if render_targets.is_empty() {
        "unsupported"
    } else {
        "cataloged"
    };
    let manifest_path = format!(
        ".sts2/artifacts/encounters/{}/manifest.json",
        args.encounter
    );
    let mut next_commands = value_array_strings(&handshake_health["safeNextCommands"]);
    for command in value_array_strings(&health["safeNextCommands"]) {
        push_unique_command(&mut next_commands, command);
    }
    push_unique_command(
        &mut next_commands,
        format!(
            "sts2 --json assets explain composed://encounters/{}/scene-package --execution live",
            args.encounter
        ),
    );
    if catalog_status == "cataloged" {
        for target in &render_targets {
            if let Some(query) = target["query"].as_str() {
                push_unique_command(
                    &mut next_commands,
                    format!(
                        "sts2 --json assets extract {} --execution live --format png",
                        query
                    ),
                );
            }
        }
        push_unique_command(
            &mut next_commands,
            format!(
                "sts2 --json assets extract-batch --manifest {} --output ./.sts2/artifacts/encounters/{} --execution live --format png",
                manifest_path, args.encounter
            ),
        );
        push_unique_command(
            &mut next_commands,
            format!(
                "scripts/validate.sh m78-live-encounter-artifacts --encounter {} --json",
                args.encounter
            ),
        );
    }

    let payload = json!({
        "status": status,
        "code": code,
        "message": message,
        "endpoint": endpoint,
        "bridgeVersion": bridge_version,
        "expectedBridgeVersion": handshake_health["expectedBridgeVersion"].clone(),
        "localBridge": handshake_health["localBridge"].clone(),
        "deployedBridge": handshake_health["deployedBridge"].clone(),
        "liveBridge": handshake_health["liveBridge"].clone(),
        "latestLog": latest_log,
        "logPath": log_path,
        "launchAttachAction": launch_attach_action,
        "staleCleanup": stale_cleanup,
        "diagnostics": diagnostics,
        "catalog": {
            "status": catalog_status,
            "encounter": args.encounter,
            "packageQuery": format!("composed://encounters/{}/scene-package", args.encounter),
            "explainCommand": format!("sts2 --json assets explain composed://encounters/{}/scene-package --execution live", args.encounter),
            "manifestPath": if catalog_status == "cataloged" { Some(manifest_path) } else { None },
            "renderTargets": render_targets,
            "unsupportedNotice": if catalog_status == "unsupported" {
                Some(json!({
                    "code": "encounter-visual-catalog-unsupported",
                    "message": "No checked-in encounter visual package metadata is cataloged for this encounter; use assets explain for the structured bridge notice and do not infer render targets from the live scene tree."
                }))
            } else {
                None
            }
        },
        "nextCommands": next_commands,
        "printOnly": args.print_only,
    });

    if status == "reachable_current" || args.print_only {
        Ok(payload)
    } else {
        Err(AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": code,
                    "message": message
                },
                "preflight": payload
            }),
        })
    }
}
