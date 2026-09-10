use super::*;

pub(crate) fn ai_tool_output_shape(summary: &str, primary_fields: &[(&str, &str)]) -> Value {
    json!({
        "summary": summary,
        "primaryFields": primary_fields
            .iter()
            .map(|(path, summary)| json!({
                "path": path,
                "summary": summary
            }))
            .collect::<Vec<_>>()
    })
}

#[allow(
    clippy::too_many_arguments,
    reason = "AI tool metadata is clearer as declarative fields at each call site."
)]
pub(crate) fn ai_tool(
    name: &str,
    summary: &str,
    status: &str,
    read_only: bool,
    maps_to: &[&str],
    input_schema: Value,
    output_summary: &str,
    primary_fields: &[(&str, &str)],
    limitations: &[&str],
    recommended_usage: &[&str],
) -> Value {
    json!({
        "name": name,
        "summary": summary,
        "status": status,
        "readOnly": read_only,
        "mapsTo": maps_to,
        "inputSchema": input_schema,
        "outputShape": ai_tool_output_shape(output_summary, primary_fields),
        "limitations": limitations,
        "recommendedUsage": recommended_usage
    })
}

pub(crate) fn ai_tool_from_command(
    tool_name: &str,
    command_name: &str,
    input_schema: Value,
    output_summary: &str,
    primary_fields: &[(&str, &str)],
    limitations: &[&str],
    recommended_usage: &[&str],
) -> Value {
    let command = command_info(command_name);
    ai_tool(
        tool_name,
        command.summary,
        command.status,
        command.read_only,
        &[command.name],
        input_schema,
        output_summary,
        primary_fields,
        limitations,
        recommended_usage,
    )
}

#[allow(
    clippy::too_many_arguments,
    reason = "AI tool metadata is clearer as declarative fields at each call site."
)]
pub(crate) fn ai_tool_from_command_with_status(
    tool_name: &str,
    command_name: &str,
    status: &str,
    input_schema: Value,
    output_summary: &str,
    primary_fields: &[(&str, &str)],
    limitations: &[&str],
    recommended_usage: &[&str],
) -> Value {
    let command = command_info(command_name);
    ai_tool(
        tool_name,
        command.summary,
        status,
        command.read_only,
        &[command.name],
        input_schema,
        output_summary,
        primary_fields,
        limitations,
        recommended_usage,
    )
}

pub(crate) fn ai_tool_catalog() -> Vec<Value> {
    let mut state_schema_properties = Map::new();
    insert_perspective_properties(&mut state_schema_properties);
    state_schema_properties.insert(
        "view".to_string(),
        schema_enum(
            "Optional state view. Omit for current state; use full only when the expanded bridge-native payload is required.",
            &["full", "screen", "actions", "refs"],
        ),
    );
    state_schema_properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer("Maximum time to wait for the live bridge state RPC.", 1),
    );

    let mut logs_schema_properties = Map::new();
    logs_schema_properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of recent log entries to return.", 1),
    );
    logs_schema_properties.insert(
        "tail".to_string(),
        schema_integer(
            "Return the latest matching entries instead of the default recent window size.",
            1,
        ),
    );
    logs_schema_properties.insert(
        "afterCursor".to_string(),
        schema_integer(
            "Return only matching entries whose cursor is greater than this value.",
            0,
        ),
    );
    logs_schema_properties.insert(
        "level".to_string(),
        schema_enum(
            "Optional minimum log level filter.",
            &["trace", "debug", "info", "warn", "error"],
        ),
    );
    logs_schema_properties.insert(
        "target".to_string(),
        schema_string("Optional substring filter applied to structured log targets."),
    );

    let mut act_schema_properties = Map::new();
    act_schema_properties.insert(
        "kind".to_string(),
        schema_enum(
            "Semantic action kind to execute through the CLI-backed bridge surface.",
            &[
                "confirm-selection",
                "cancel-selection",
                "select-map-node",
                "end-turn",
                "cancel-end-turn",
                "ready",
                "unready",
                "select-character",
                "claim-reward",
                "skip-rewards",
                "select-card",
                "skip-card-selection",
                "select-bundle",
                "buy-card",
                "buy-relic",
                "buy-potion",
                "remove-card",
                "leave-shop",
                "close-shop-inventory",
                "rest",
                "smith",
                "use-rest-site-option",
                "proceed-rest-site",
                "open-chest",
                "take-relic",
                "proceed-treasure-room",
                "back-from-map",
                "select-event-option",
                "open-event-shop",
                "use-crystal-sphere-control",
                "proceed-event",
                "toggle-map",
                "toggle-deck",
                "toggle-settings",
                "sort-deck-view",
                "toggle-deck-view-upgrades",
                "view-draw-pile",
                "view-discard-pile",
                "view-exhaust-pile",
                "inspect-relic",
                "close-inspect-relic",
                "select-hand-card",
                "deselect-hand-card",
                "confirm-hand-selection",
                "use-potion",
                "play-card",
                "choose",
            ],
        ),
    );
    act_schema_properties.insert(
        "choiceId".to_string(),
        schema_string("Stable fallback choice id from state.fallbackChoices[].id or state.actions[].args.choiceId. Use choose only for generic, modded, or unmodeled visible choices when no modeled action exists."),
    );
    act_schema_properties.insert(
        "rewardId".to_string(),
        schema_string(
            "Stable reward id from state.scene.items[].id or state.actions[].args.rewardId.",
        ),
    );
    act_schema_properties.insert(
        "bundleId".to_string(),
        schema_string(
            "Stable bundle id from state.scene.items[].id or state.actions[].args.bundleId.",
        ),
    );
    act_schema_properties.insert(
        "shopItemId".to_string(),
        schema_string(
            "Stable shop item id from state.scene.items[].id or state.actions[].args.shopItemId.",
        ),
    );
    act_schema_properties.insert(
        "relicId".to_string(),
        schema_string(
            "Stable relic id from state.scene.items[].id or state.actions[].args.relicId.",
        ),
    );
    act_schema_properties.insert(
        "restOptionId".to_string(),
        schema_string("Stable rest option id from state.scene.controls[].id or state.actions[].args.restOptionId."),
    );
    act_schema_properties.insert(
        "eventOptionId".to_string(),
        schema_string("Stable event option id from state.scene.items[].id or state.actions[].args.eventOptionId."),
    );
    act_schema_properties.insert(
        "controlId".to_string(),
        schema_string("Stable Crystal Sphere control id from state.scene.items[].id or state.actions[].args.controlId."),
    );
    act_schema_properties.insert(
        "by".to_string(),
        json!({
            "type": "string",
            "enum": ["obtained", "type", "cost", "alphabet"],
            "description": "Deck view sort key for sort-deck-view."
        }),
    );
    act_schema_properties.insert(
        "mapNodeId".to_string(),
        schema_string(
            "Stable map node id from state.scene.items[].id or state.actions[].args.mapNodeId.",
        ),
    );
    act_schema_properties.insert(
        "cardId".to_string(),
        schema_string("Stable card id from state.combat.hand[].id or state.actions[].args.cardId."),
    );
    act_schema_properties.insert(
        "potionId".to_string(),
        schema_string(
            "Stable potion id from state.combat.potions[].id or state.actions[].args.potionId.",
        ),
    );
    act_schema_properties.insert(
        "targetId".to_string(),
        schema_string(
            "Optional stable target id from runtime state when the action requires a target.",
        ),
    );
    act_schema_properties.insert(
        "characterId".to_string(),
        schema_string(
            "Stable lobby character id from state.scene.items[].id or state.actions[].args.characterId.",
        ),
    );
    act_schema_properties.insert(
        "playerId".to_string(),
        schema_string(
            "Optional stable player id for multiplayer-aware action legality; unsupported remote-player actions can still be rejected by the bridge.",
        ),
    );

    let mut test_run_schema_properties = Map::new();
    test_run_schema_properties.insert(
        "profile".to_string(),
        schema_string("Optional named test profile from config.test.profiles."),
    );
    test_run_schema_properties.insert(
        "tag".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" },
            "description": "Optional scenario tag filters applied in addition to any selected test profile includeTags."
        }),
    );
    test_run_schema_properties.insert(
        "path".to_string(),
        schema_string(
            "Path to one .sts2.yaml or .sts2.json scenario file or a directory of scenarios.",
        ),
    );
    test_run_schema_properties.insert(
        "inline".to_string(),
        schema_string(
            "Inline YAML or JSON scenario body executed as one scenario without reading a .sts2.yaml or .sts2.json file from disk.",
        ),
    );
    test_run_schema_properties.insert(
        "artifactsDir".to_string(),
        schema_string("Optional override for the persisted test artifact root."),
    );
    test_run_schema_properties.insert(
        "failureArtifacts".to_string(),
        schema_enum(
            "Failure artifact persistence mode.",
            &["on-failure", "never"],
        ),
    );
    test_run_schema_properties.insert(
        "durable".to_string(),
        schema_boolean("When routed through the automation service /v0/test-runs endpoint, request filesystem-backed durable job metadata and remote artifact indexing."),
    );
    let mut test_stress_schema_properties = Map::new();
    test_stress_schema_properties.insert(
        "path".to_string(),
        schema_string(
            "Path to one .sts2.yaml or .sts2.json scenario file or a directory of scenarios.",
        ),
    );
    test_stress_schema_properties.insert(
        "artifactsDir".to_string(),
        schema_string("Optional override for the persisted stress-run artifact root."),
    );
    test_stress_schema_properties.insert(
        "failureArtifacts".to_string(),
        schema_enum(
            "Failure artifact persistence mode for each nested test run.",
            &["on-failure", "never"],
        ),
    );
    test_stress_schema_properties.insert(
        "iterations".to_string(),
        schema_integer("Exact number of repeated iterations to execute.", 1),
    );
    test_stress_schema_properties.insert(
        "durationMs".to_string(),
        schema_integer("Maximum elapsed runtime budget in milliseconds.", 1),
    );
    test_stress_schema_properties.insert(
        "maxFailures".to_string(),
        schema_integer(
            "Maximum failed iterations allowed before the stress run stops.",
            0,
        ),
    );
    test_stress_schema_properties.insert(
        "cooldownMs".to_string(),
        schema_integer("Optional cooldown between iterations in milliseconds.", 0),
    );

    vec![
        ai_tool_from_command(
            "game_info",
            "game info",
            empty_object_schema(),
            "Structured environment, transport, bridge, and config metadata for the current CLI setup.",
            &[
                (
                    "bridge",
                    "Handshake-derived bridge metadata including supported action kinds.",
                ),
                ("transport", "Configured and effective transport details."),
                ("config", "Resolved CLI config defaults and overrides."),
                (
                    "notes",
                    "Important environment and lifecycle caveats for automation.",
                ),
            ],
            &[
                "This reports configured transport and bridge metadata; it does not launch, attach, or repair the game process.",
            ],
            &[
                "Call game_info first so the agent can see transport.kind, supportedActions, and current environment notes before choosing a workflow.",
            ],
        ),
        ai_tool_from_command(
            "game_detect",
            "game detect",
            empty_object_schema(),
            "Configured-vs-detected install metadata plus the derived live-bridge layout for the current local setup.",
            &[
                (
                    "status",
                    "Whether the game path was configured, auto-detected, or missing.",
                ),
                (
                    "configuredPath",
                    "Install root from config when one was supplied or resolved.",
                ),
                (
                    "detectedPath",
                    "Install root discovered from Steam-backed host detection when available.",
                ),
                (
                    "liveBridge",
                    "Derived bridge layout, endpoint, and next-step notes for the current setup.",
                ),
            ],
            &[
                "This is a read-only setup probe that reports configured-vs-detected install paths and live-bridge layout status; it does not attach to a running game.",
            ],
            &[
                "Use game_detect before game_info when install roots or local bridge layout are uncertain and you need setup guidance.",
            ],
        ),
        ai_tool_from_command(
            "game_launch",
            "game launch",
            game_launch_schema(),
            "Launch metadata plus the successful live-attachment payload gathered after the game starts.",
            &[
                (
                    "launch",
                    "Spawned process metadata, endpoint details, and launch command context.",
                ),
                (
                    "attachment",
                    "Live bridge attachment result including screen, endpoint, and game-info snapshots.",
                ),
                (
                    "stability",
                    "Optional post-attach stability result when verifyStableMs is non-zero.",
                ),
            ],
            &[
                "This may close or kill an existing configured game process before launching a fresh one; it is not a read-only probe.",
                "Use game_attach or implicit profile/test launch flows when you want to reuse an already running bridge instead of restarting.",
                "If the deployed bridge layout is missing or stale, the CLI may repair it before launch using the same safe bridge payload rules as the normal lifecycle flow.",
            ],
            &[
                "Call game_info first, then use game_launch when you need a fresh game process with the current bridge and launch configuration.",
            ],
        ),
        ai_tool_from_command(
            "game_attach",
            "game attach",
            lifecycle_wait_schema("Maximum time to wait for live attachment."),
            "Successful live-attachment result including endpoint details, game-info metadata, and the current screen.",
            &[
                (
                    "gameInfo",
                    "Handshake-derived transport and bridge metadata after attachment succeeds.",
                ),
                (
                    "screen",
                    "Current visible screen snapshot at the time of attachment.",
                ),
                (
                    "endpoint",
                    "Resolved local IPC/TCP endpoint used for the attachment.",
                ),
                ("elapsedMs", "Total wait time before attachment succeeded."),
            ],
            &[
                "This only waits for an already running bridge/game process; it does not launch the game or repair installs on its own.",
            ],
            &[
                "Call game_info first, then prefer game_attach when the game is already running and you only need to wait for the bridge to come up.",
            ],
        ),
        ai_tool_from_command(
            "game_close",
            "game close",
            lifecycle_wait_schema("Maximum time to wait for graceful shutdown."),
            "Structured graceful shutdown result with strategy, process ids, endpoint metadata, elapsed time, and notices.",
            &[
                (
                    "strategy",
                    "Shutdown strategy used: bridge, configured-command, or platform-graceful.",
                ),
                (
                    "matchedPids",
                    "Exact local process ids considered to be STS2.",
                ),
                (
                    "remainingPids",
                    "Exact process ids still present after the bounded wait.",
                ),
                (
                    "notices",
                    "Bridge or fallback notices gathered during shutdown.",
                ),
            ],
            &[
                "This mutates local process state and may run configured game.stopCommand.",
                "Force kill remains intentionally outside the AI/MCP catalog.",
            ],
            &[
                "Use game_close before lifecycle flows that need the local game stopped without forcing termination.",
            ],
        ),
        ai_tool_from_command(
            "game_deploy",
            "game deploy",
            game_deploy_schema(),
            "Bridge deploy result, copied mod metadata, optional build output, and optional restart/verification payloads.",
            &[
                (
                    "bridge",
                    "Bridge install/deploy summary for the configured live layout.",
                ),
                (
                    "mod",
                    "Resolved project, source, and target directories for the deployed mod payload.",
                ),
                (
                    "restart",
                    "Whether restart was requested plus any launch result when a restart occurred.",
                ),
                (
                    "verify",
                    "Whether verification was requested plus the resulting attachment payload.",
                ),
            ],
            &[
                "Provide an authored mod project path; this mutates the configured mods directory and may stop/restart the local game process when requested.",
                "Prefer game_deploy over lower-level repair/install steps when the goal is to get a mod plus bridge into a runnable state.",
            ],
            &[
                "Call game_info first, then use game_deploy for the end-to-end local deploy flow instead of trying to sequence install-only lifecycle commands yourself.",
            ],
        ),
        ai_tool_from_command_with_status(
            "toolchain_info",
            "toolchain info",
            "stable",
            empty_object_schema(),
            "Managed toolchain roots, config provenance, and helper availability for the current repo-local environment.",
            &[
                (
                    "roots",
                    "Resolved repo-local toolchain, shared-cache, and profiles-file roots with provenance.",
                ),
                (
                    "tools",
                    "Availability and provenance metadata for embedded and configured helper tools. The dotnet-helper entry includes launch diagnostics with configuredPath, resolvedPath, invocationPath, launchKind, available, buildNeeded, buildReason, failureReason, and notes.",
                ),
            ],
            &[
                "This is repo-local environment metadata only; it does not run recovery or mutate any files.",
            ],
            &[
                "Use toolchain_info before recovery, profile, or hook workflows when you need to confirm which repo-local paths and helper binaries the CLI will use.",
            ],
        ),
        ai_tool_from_command(
            "state",
            "state",
            object_schema(state_schema_properties, &[]),
            "Runtime-only presentation state envelope (schemaVersion spirectl.state/v0): the screen-aware snapshot of what the attached client can observe.",
            &[
                (
                    "rootScene",
                    "The current root scene path (e.g. screens/character_select_screen or run).",
                ),
                (
                    "characterSelect",
                    "Character-select lobby, character buttons, and view; null outside the start-run character select screen.",
                ),
                (
                    "run",
                    "Active run: players, map, currentRoom (combat/event/treasure/shop/rest), and view; null when no run is active.",
                ),
                (
                    "language",
                    "Active localization language code for the attached client.",
                ),
            ],
            &[
                "state is the runtime envelope only; immutable model data, localization text, assets, and resolved actions are not inlined.",
                "Use state actions for the live, resolved semantic action list; act remains authoritative for legality.",
            ],
            &[
                "Read state to understand the current screen and run, then use state actions to pick an executable action by kind and args.",
                "Remote-perspective view objects are omitted with notices when the attached client cannot observe that player's UI.",
            ],
        ),
        ai_tool_from_command(
            "inspect_actions",
            "inspect actions",
            empty_object_schema(),
            "Supported bridge action kinds plus currently available runtime actions for the active screen and perspective.",
            &[
                (
                    "supportedActions",
                    "Handshake-level action kinds, parameters, and implementation status.",
                ),
                (
                    "availableActions",
                    "Screen-specific currently executable actions and their arguments, including ownerPlayerId, ownerRole, remoteOrchestration, perspective, intentKind, and preferredAction metadata when available.",
                ),
                (
                    "presentation.interaction",
                    "The scene-first presentation surface combines state, models, localization, ResourceLoader scene data, assets, and action refs so downstream consumers can render without screen-kind heuristics.",
                ),
                (
                    "stateContract.perspectiveInputs/remoteOrchestrationStates/ownershipFailureReasonCodes",
                    "Machine-readable guidance for --perspective, --player-id, wrong-player repair, and remote orchestration capability states.",
                ),
                (
                    "screen",
                    "Current screen classification used to resolve availability.",
                ),
                (
                    "notices",
                    "Explicit caveats about partial or unstable action/state surfaces, using structured notice fields when the bridge can provide them.",
                ),
            ],
            &[
                "supportedActions describe bridge-known action kinds, while availableActions only lists the actions that are executable on the current screen and perspective.",
                "Use ownerPlayerId and actionFailure.reasonCode=wrong_player for ownership repair; remoteOrchestration states include host-local-seat for host-owned local seats and describe whether this bridge can execute or mediate remote-owned actions.",
                "For non-combat screens and overlays, inspect typed state sections first; generic choose remains only a compatibility/fallback action for currently executable generic, modded, or unmodeled visible controls until the intent-first action contract lands.",
                "Overlay close/back controls are advertised only when the bridge can validate a legal hook; unsupported or partial overlays should emit structured notices instead of fabricated actions.",
                "cardOverlay is the typed card detail overlay section. Passive overlays keep the underlying screen authoritative, blocking overlays take precedence, and unsupported overlays should appear as structured notices.",
                "Presentation interaction metadata describes initiation semantics only; presentation action refs, act, or ExecuteAction remain authoritative for mutation, legality, wrong-player failures, and unsupported-perspective errors.",
                "Dangerous raw input remains outside presentation semantics and should be used only as an explicit dangerous-mode fallback.",
                "Raw scene debugging is intentionally outside inspect_actions; use dev scene tree, dev scene node, or dev scene children when runtime internals are required.",
                "Use --offline or --config tests/sts2.mock.yaml when only static JSON metadata rendering is needed and no live host should be contacted.",
            ],
            &[
                "Call inspect_actions before act when the agent needs parameter guidance, preferredAction linkage, or needs to distinguish implemented vs scaffolded action kinds.",
                "Pair inspect_actions with state so preferredAction links and compatibility ids can be resolved against the active typed screen or cardOverlay section.",
            ],
        ),
        ai_tool(
            "act",
            "Execute one semantic runtime action through the CLI-backed bridge surface.",
            "partial",
            false,
            &[
                "act confirm-selection",
                "act cancel-selection",
                "act select-map-node",
                "act end-turn",
                "act ready",
                "act unready",
                "act select-character",
                "act use-potion",
                "act play-card",
                "act choose",
            ],
            object_schema(act_schema_properties, &["kind"]),
            "Accepted or rejected action result preserving the bridge-backed CLI payload.",
            &[
                ("requestId", "Stable request id generated by the CLI."),
                (
                    "actionInstanceId",
                    "Bridge action instance identifier when accepted.",
                ),
                ("kind", "Resolved semantic action kind."),
                (
                    "accepted",
                    "Whether the bridge accepted the requested action.",
                ),
                ("message", "Human-readable bridge response message."),
            ],
            &[
                "Use state.actions[] first; each action already carries kind, compact args, owner, and stateRefs for the current screen.",
                "choose is fallback-only for generic, modded, or unmodeled visible controls exposed through state.fallbackChoices[].",
                "play-card may require targetId depending on the chosen card; prefer state.actions[].args over guessing from card text.",
                "For multiplayer actions, pass playerId only when intentionally targeting a stable player id; wrong-owner or unsupported-perspective failures preserve requestedPlayerId, resolvedOwnerPlayerId, localPlayerId, hostPlayerId, localRole, action, and remoteOrchestration in the structured error payload. host-local-seat is actionable through semantic actions; true remote clients still require an owning configured-client or host-mediated path.",
            ],
            &[
                "Read state and inspect_actions before calling act so the agent uses currently executable action ids.",
            ],
        ),
        ai_tool_from_command(
            "console",
            "dev console",
            console_schema(),
            "Execute an explicit mode-gated STS2 in-game developer console command through the live bridge.",
            &[
                ("requestId", "Stable request id generated by the CLI."),
                ("command", "Console command name sent to the bridge."),
                ("args", "Console arguments sent after the command name."),
                (
                    "line",
                    "Canonical command line joined from command and args.",
                ),
                (
                    "accepted",
                    "Whether the bridge delivered the request to the runtime console API.",
                ),
                ("success", "STS2 console CmdResult success flag."),
                ("output", "Captured STS2 console CmdResult message text."),
                (
                    "outputLines",
                    "Captured console output split into non-empty lines.",
                ),
                (
                    "source",
                    "Whether the response came from stub or live bridge data.",
                ),
                (
                    "provisional",
                    "Whether the response is provisional stub data.",
                ),
                ("notices", "Structured console execution notices."),
            ],
            &[
                "This is explicit developer console execution, not a semantic action and not default state.",
                "Safe commands run in normal mode by default.",
                "achievement, cloud, and unlock require dangerous mode because they can mutate persisted files.",
            ],
            &[
                "Use console for developer setup and validation commands such as help, help draw, draw 3, or die when semantic actions are not the right surface.",
            ],
        ),
        ai_tool_from_command(
            "logs",
            "dev logs",
            object_schema(logs_schema_properties, &[]),
            "Recent structured bridge logs with cursor metadata plus bounded filtering by level and target.",
            &[
                (
                    "entries",
                    "Ordered recent log entries returned by the current bridge transport, each with a stable cursor.",
                ),
                (
                    "nextCursor",
                    "Cursor to resume from in a later CLI follow/tail poll.",
                ),
                ("source", "Origin of the log payload."),
                (
                    "provisional",
                    "Whether the underlying log surface is still provisional.",
                ),
            ],
            &[
                "The AI tool surface is one-shot only; CLI follow mode is available through `sts2 dev logs --follow` rather than this catalog entry.",
            ],
            &[
                "Use logs after failures or surprising runtime state to capture recent bridge evidence for debugging.",
                "Use `tail` to anchor the latest matching window, then continue with CLI follow mode if you need a live stream.",
            ],
        ),
        ai_tool_from_command_with_status(
            "log_health",
            "dev log-health",
            "stable",
            log_health_schema(),
            "Structured bounded log-health evaluation with excluded-entry counts and offending records when unhealthy.",
            &[
                ("status", "Healthy/unhealthy evaluation result."),
                (
                    "unhealthyEntryCount",
                    "Number of entries that failed the health check after exclusions.",
                ),
                (
                    "excludedEntryCount",
                    "Number of entries ignored by the configured exclusions.",
                ),
                (
                    "entries",
                    "Matching unhealthy log entries when the window is not healthy.",
                ),
            ],
            &[
                "This checks the bounded current log window only; use logs for full investigation when you need raw recent history.",
            ],
            &[
                "Use log_health inside repo-local smoke checks or failure triage when you need a boolean health gate over recent logs rather than the full log stream.",
            ],
        ),
        ai_tool_from_command_with_status(
            "diagnostics",
            "dev diagnostics",
            "stable",
            diagnostics_schema(),
            "One structured diagnostics payload with log-health evaluation, captures, and optional bundle paths.",
            &[
                ("status", "Overall diagnostics capture status."),
                (
                    "logHealth",
                    "Embedded log-health result for the inspected window.",
                ),
                (
                    "captures",
                    "Per-surface capture status and data for state, actions, logs, and related evidence.",
                ),
                (
                    "bundle",
                    "Persisted diagnostics.json and sibling artifact paths when bundleDir was requested.",
                ),
            ],
            &[
                "This is an evidence surface, not the primary state API; use state and inspect_actions for normal runtime control flow.",
            ],
            &[
                "Use diagnostics after failures or before deleting fallback evidence flows so automation can persist one predictable bundle instead of hand-assembling screenshots and logs.",
            ],
        ),
        ai_tool_from_command_with_status(
            "hot_reload_status",
            "dev mod-reload status",
            "partial",
            hot_reload_status_schema(),
            "Status for an M57/M60 shell-supported hot-reload project, including active generation, last reload report, restart-required state, and notices.",
            &[
                ("project", "Resolved hot-reload project metadata."),
                (
                    "shell",
                    "Current shell protocol support, active generation, last report, and restart-required state.",
                ),
                (
                    "notices",
                    "Structured support and mismatch notices from the bridge.",
                ),
            ],
            &[
                "Requires an explicit local project containing sts2.hot-reload.yaml.",
                "Only M57-compatible shells advertising spirectl.m57.hot-reload-shell are supported.",
                "This does not deploy the shell, restart the game, or edit source files.",
            ],
            &[
                "Use hot_reload_status before hot_reload when an automation agent needs to confirm the current generation and shell support.",
            ],
        ),
        ai_tool_from_command_with_status(
            "hot_reload",
            "dev mod-reload",
            "partial",
            hot_reload_schema(),
            "Build and request reload for an M57/M60 shell-supported hot-reload project through the live bridge.",
            &[
                (
                    "status",
                    "Reload request status such as accepted, loaded, or failed.",
                ),
                (
                    "requestId",
                    "Stable reload request id generated by the CLI.",
                ),
                ("build", "Optional logic build result when build=true."),
                (
                    "shell",
                    "Post-request shell status and restart-required state.",
                ),
                (
                    "reload",
                    "Shell reload report including generation, unload/collection details, previous-generation status, and structured errors.",
                ),
            ],
            &[
                "Requires an explicit local project containing sts2.hot-reload.yaml.",
                "Only M57-compatible shells advertising spirectl.m57.hot-reload-shell are supported.",
                "This does not deploy the shell, restart the game, mutate original game files, or edit mod source files.",
                "Failures preserve whether the previous generation remains active and whether a restart is required.",
            ],
            &[
                "Use hot_reload with build=true and wait=true after editing reloadable logic, then inspect reload.generation and reload.previousRemainsActive before continuing validation.",
            ],
        ),
        ai_tool_from_command(
            "assets_extract",
            "assets extract",
            assets_extract_schema(),
            "Artifact-writing extract result preserving the CLI export summary and per-match output metadata.",
            &[
                ("query", "Original asset search query."),
                (
                    "executionMode",
                    "Resolved offline/live execution mode used by the CLI.",
                ),
                (
                    "outputDir",
                    "Artifact root where extracted files were written.",
                ),
                (
                    "exports",
                    "Per-match extraction results including output paths and provenance.",
                ),
            ],
            &[
                "This writes extraction artifacts to disk and may still trigger the existing lifecycle repair/launch flow when live execution is required.",
                "Generic extraction searches configured resource/mod roots and packed entries only; recovered-project files are used only if the caller passes that directory as an explicit resource root.",
                "Typed keys distinguish model-backed assets from composed outputs; live-only families remain environment-gated.",
                "Use this for repo-local asset export only; it is not a runtime scene-inspection or live UI-observation API.",
            ],
            &[
                "Use assets_extract when you need filesystem artifacts for representative keys such as model://characters/ironclad/visuals, model://cards/strike_r/image, composed://combat-background/hive/image, or composed://encounters/knowledge_demon_boss/background/image.",
            ],
        ),
        ai_tool_from_command(
            "assets_explain",
            "assets explain",
            assets_explain_schema(),
            "Structured composition, encounter package, and optional render diagnostics for live composed assets without writing artifacts.",
            &[
                (
                    "rootScene",
                    "Authored combat-background root scene and load source.",
                ),
                (
                    "layerGroups",
                    "Discovered candidates, selected variants, and ordering.",
                ),
                (
                    "selectedLayers",
                    "Loaded layers with texture refs and bounds.",
                ),
                (
                    "bounds",
                    "Viewport, final composed, final visible, and transparency diagnostics.",
                ),
                (
                    "warnings",
                    "Missing layers, transparent-heavy output, suspicious bounds, and active-scene differences.",
                ),
                (
                    "encounterScenePackage",
                    "Encounter package metadata exposed as `explanation` fields including viewport, camera, background, logical actors, visual parts, selector diagnostics, states, transitions, render target decisions, optional render diagnostics, isolation evidence, and notices for cataloged encounters such as Kaiser Crab, Ovicopter, and Knowledge Demon Boss.",
                ),
                (
                    "renderDiagnostics",
                    "Optional failed live encounter render evidence, when supplied by the bridge, including request/render target ids, selector resolution, hidden/kept part ids, viewport, frame, hook/timing notes, and alpha/RGB pixel evidence.",
                ),
                (
                    "selectorDiagnostics",
                    "Catalog selector resolution details with normalized selectors, missing selectors, candidate paths, resolved nodes, bounding boxes, status, target state, and render target decisions.",
                ),
                (
                    "notices",
                    "Structured package notices, including unsupported dynamic parts, live-only VFX requirements, inferred bounds, missing selectors, and provisional catalog coverage when the bridge can report it.",
                ),
            ],
            &[
                "Supports composed://combat-background/<id>/image and cataloged composed://encounters/<id>/scene-package metadata for Kaiser Crab, Ovicopter, Knowledge Demon Boss, and later additive base-game packages.",
                "Unknown or uncataloged encounters should be handled as structured notices/failures from the bridge, not inferred visual packages.",
                "This does not write extraction artifacts and does not replace assets_extract.",
            ],
            &[
                "Use assets_explain before changing extraction behavior when a composed combat background, encounter package, VFX-backed visual part, or encounter render target looks incomplete, misframed, selector-missing, non-isolated, or unsupported.",
            ],
        ),
        ai_tool_from_command(
            "assets_extract_batch",
            "assets extract-batch",
            assets_extract_batch_schema(),
            "Generic batch extraction result preserving per-request artifact records, failures, render diagnostics, and caller metadata.",
            &[
                ("status", "Aggregate batch status: ok, partial, or failed."),
                (
                    "outputDir",
                    "Batch artifact root where request-scoped outputs were written.",
                ),
                (
                    "results",
                    "Per-request extraction results including carried metadata, output paths, provenance, notes, notices, and errors.",
                ),
                (
                    "exports",
                    "Per-export artifact records preserve bridge notes/notices/provenance, artifact checks, and optional renderDiagnostics so encounter render target diagnostics stay attached to the artifact.",
                ),
                (
                    "renderDiagnostics",
                    "Optional failed live encounter render evidence on exports, including selector, frame, hook, timing, alpha, and RGB evidence for transparent, blank, misframed, or invalid captures.",
                ),
                (
                    "artifactChecks",
                    "When the live helper can validate rendered outputs, checks summarize nonblank pixels, expected transparency, framing, part/overlay isolation, and selector/bounds failures.",
                ),
                (
                    "error",
                    "Per-request failure envelope preserves structured bridge payloads for unsupported uncataloged encounters, selector failures, and error.details.diagnostic render evidence when a live render fails before export.",
                ),
            ],
            &[
                "This writes extraction artifacts to disk and may trigger the existing lifecycle repair/launch flow when live execution is required by any request.",
                "For S90/S107 encounter reliability, feed catalog render target queries from assets_explain or dev visual-preflight into a manifest and run m78-live-encounter-artifacts for the opt-in live artifact path.",
                "This is generic batch extraction only; downstream repos remain responsible for choosing assets and building project-specific indexes or bundle layouts.",
                "Do not collapse uncataloged encounter failures into no-match ambiguity; consume the per-request error/details or per-export notices.",
            ],
            &[
                "Use assets_extract_batch when a downstream workflow already has a manifest of model, composed, and direct res:// asset queries and needs stable artifact paths plus script-friendly partial failure reports.",
            ],
        ),
        ai_tool_from_command(
            "load_fixture",
            "dev fixture load",
            load_fixture_schema(),
            "Canonicalized live fixture-load metadata preserving the bridge-backed result envelope.",
            &[
                (
                    "status",
                    "Fixture-load status reported by the CLI/runtime bridge.",
                ),
                (
                    "fixture",
                    "Canonicalized fixture metadata and authored recipe summary when available.",
                ),
                (
                    "recipeReport",
                    "Structured recipe diagnostics with recipeName, appliedFields, inferredFields, omittedFields, unsupportedFields, degradedMultiplayerFields, and bridgeValidation.",
                ),
                (
                    "loaded",
                    "Bridge/runtime payload describing loaded screen, resolvedPerspective, notices, and the same recipeReport for step-artifact consumers.",
                ),
            ],
            &[
                "Only file-backed authored fixture recipes are supported here; inline fixture bodies and arbitrary runtime mutation remain out of scope.",
                "Use this only against checked-in or otherwise intentional recipe files, not as a generic state-editing API.",
                "The shipped executable fixture subset currently covers main-menu, combat, game-derived screen ids for map, rewards, rest-site, event-room, treasure/relic, shop, card/simple/deck/bundle selection, card-overlay, passive-card-overlay, Screens.CharacterSelect.NCharacterSelectScreen start-run recipes, and Screens.CharacterSelect.NMultiplayerLoadGameScreen load-run recipes including explicit event options, opened treasure-room proceed flow, shop inventory/card-removal overrides, visible overlay entry, and local-only degraded multiplayer reporting.",
                "Exact native sidecars and dev.load-scenario sparse artifacts are separate restore surfaces; load_fixture does not restore hidden combat queues, RNG continuation, animation state, enemy AI history, or transient UI internals.",
            ],
            &[
                "Call game_info first, then use load_fixture only when you already have an authored recipe file for deterministic setup.",
            ],
        ),
        ai_tool_from_command_with_status(
            "scenario_export",
            "dev scenario export",
            "partial",
            scenario_export_schema(),
            "Shareable sparse scenario export metadata plus optional exact sidecar path and field-level restoreSupport.",
            &[
                (
                    "path",
                    "Filesystem path of the written sparse scenario YAML artifact.",
                ),
                (
                    "exactBundlePath",
                    "Optional opaque sidecar path when includeExact was requested and save-backed or fixture-backed continuation data was available.",
                ),
                (
                    "exactBundleKind",
                    "Optional sidecar kind: save-backed for native run-save bundles or fixture-backed for JSON fixture bundles.",
                ),
                (
                    "restoreQuality",
                    "Bridge-reported restore quality expected from the exported artifact.",
                ),
                (
                    "restoreSupport",
                    "per-field restore support report with exact, partial, inferred, omitted, unsupported, and degraded-local-multiplayer field details.",
                ),
                (
                    "restoreSupport.fields[].supportClass",
                    "Field-level support class for the exported public state/action path.",
                ),
                (
                    "restoreSupport.fields[].reasonCode",
                    "Stable reason code explaining why the field has that restore support class.",
                ),
                (
                    "restoreSupport.fields[].suggestedNextStep",
                    "Actionable next step for partial, inferred, omitted, unsupported, or degraded fields.",
                ),
                (
                    "screen",
                    "Captured screen metadata used as the restore anchor.",
                ),
                (
                    "multiplayer",
                    "Optional multiplayer capture metadata, including lobby players, host/local identity, and restore limits.",
                ),
            ],
            &[
                "Requires an attached local bridge; this is not a normal gameplay command.",
                "Exact sidecars are opt-in opaque continuation data, not sparse recipes and not recorded screen-entry fixtures.",
                "Lobby multiplayer metadata is captured when observable; active multiplayer restore still requires explicit degraded local-only opt-in and reports omitted remote clients, or returns structured unsupported errors.",
                "Native save-backed exact continuation is bounded to supported save/run data; hidden queues, RNG continuations, enemy AI history, and transient UI state are not claimed as exact unless exposed by a supported native path.",
            ],
            &[
                "Use scenario_export when a live local state should become a reviewable repro artifact for test-runner workflows.",
            ],
        ),
        ai_tool_from_command_with_status(
            "scenario_load",
            "dev scenario load",
            "partial",
            scenario_load_schema(),
            "Scenario restore result with lifecycle metadata, restore quality, and field-level post-restore validation.",
            &[
                ("path", "Scenario YAML path loaded by the CLI."),
                (
                    "restoreQuality",
                    "Bridge-reported restore quality after exact or sparse restore.",
                ),
                (
                    "exactBundleUsed",
                    "Whether the bridge used an exact-bundle sidecar during restore.",
                ),
                (
                    "sparseFallbackUsed",
                    "Whether the bridge fell back from exact restore to sparse restore.",
                ),
                (
                    "validation.status",
                    "Post-restore validation status for the screen/run anchor.",
                ),
                (
                    "validation.expectedSummary",
                    "Expected public-state summary used for post-restore validation when available.",
                ),
                (
                    "validation.observedSummary",
                    "Observed public-state summary after restore when available.",
                ),
                (
                    "validation.mismatches",
                    "Per-field restore validation mismatches with path, expected/observed summaries, supportClass, reasonCode, and suggestedNextStep.",
                ),
                (
                    "validation.mismatches[].supportClass",
                    "Support class for the mismatched field: exact, partial, inferred, omitted, unsupported, or degraded-local-multiplayer.",
                ),
                (
                    "validation.mismatches[].reasonCode",
                    "Stable reason code explaining the mismatch.",
                ),
                (
                    "validation.mismatches[].suggestedNextStep",
                    "Actionable repair or validation guidance for the mismatch.",
                ),
                (
                    "multiplayerRestore",
                    "Optional multiplayer restore mode, restored player ids, omitted remote player ids, and remote-client requirement metadata.",
                ),
            ],
            &[
                "May restart the local game when restart is true.",
                "Scenario load is bridge-mediated and bounded; it is not unstructured runtime memory editing.",
                "Exact sidecar restore, sparse recipe restore, native save-backed continuation, and recorded screen-entry fixtures are separate workflows with separate fidelity claims.",
                "Lobby multiplayer restore is supported where the bridge can materialize it; active multiplayer restore requires allowDegradedLocalMultiplayer for local-only degradation and must report omitted remote clients, or fails explicitly.",
            ],
            &[
                "Use scenario_load at the start of a runner or automation workflow when an exported repro state should anchor later actions and assertions.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_status",
            "dev debug status",
            "partial",
            debug_status_schema(),
            "Live debug status payload exposing runtime pause state, leased-session ownership, registered breakpoints, and the most recent breakpoint hit.",
            &[
                (
                    "supported",
                    "Whether the attached live host currently exposes executable debug hooks.",
                ),
                (
                    "executionState",
                    "Current live debug execution state: running, paused, or unsupported.",
                ),
                (
                    "sessionOwnership",
                    "Whether the request is session-free, owner-bound, conflicted, or requires a leased session.",
                ),
                (
                    "activeSession",
                    "Current leased debugger session metadata when the bridge owns one.",
                ),
                (
                    "pauseReason",
                    "Why the runtime is currently paused when executionState is paused.",
                ),
                (
                    "breakpoints",
                    "Registered debug breakpoints visible to the live bridge.",
                ),
                (
                    "lastBreakpointHit",
                    "Most recent matching breakpoint metadata when available.",
                ),
            ],
            &[
                "This dev-only surface still depends on a live host build exposing debug hooks; mock/stub transports will continue to report unsupported status honestly.",
                "Pause/resume, waiting, and stepping are explicit runtime controls and should not be treated as a general-purpose automation prerequisite for routine scenarios.",
            ],
            &[
                "Use debug_status first in live debugging workflows to confirm hook support, inspect the current lease owner, and review the current breakpoint registry.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_session_start",
            "dev debug session start",
            "partial",
            debug_session_start_schema(),
            "Leased debugger-session result preserving the created session id, ownership details, and post-start live debug status.",
            &[
                (
                    "session",
                    "Debugger session metadata including stable id, optional name, role, and expiry.",
                ),
                (
                    "status",
                    "Post-start live debug status snapshot including ownership and breakpoints.",
                ),
                (
                    "paused",
                    "Whether the runtime was paused as part of session start.",
                ),
            ],
            &[
                "Session leasing still depends on a live host build exposing debug hooks; unsupported or non-live hosts return structured notices instead of pretending a lease exists.",
                "Only one controller lease is supported at a time; observer sessions can attach without mutating debugger state.",
            ],
            &[
                "Use debug_session_start before leased pause, step, wait, or breakpoint workflows so the bridge can enforce one explicit owner for mutating debugger operations.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_events",
            "dev debug events",
            "partial",
            debug_events_schema(),
            "Bounded debugger event replay payload exposing sequence cursors, retention metadata, lease role context, and structured notices.",
            &[
                (
                    "events",
                    "Retained debugger events with sequence numbers, kind, session id, session role, notices, and kind-specific detail.",
                ),
                (
                    "nextSequence",
                    "Cursor callers should use for the next replay request.",
                ),
                (
                    "oldestRetainedSequence",
                    "Oldest event sequence still retained by the live bridge.",
                ),
                (
                    "newestSequence",
                    "Newest event sequence known to the live bridge.",
                ),
                (
                    "expired",
                    "Whether the requested fromSequence was older than the retained window.",
                ),
                (
                    "overflow",
                    "Whether more events were available than the requested limit.",
                ),
            ],
            &[
                "This is an explicit dev debugger surface; it is not part of default gameplay actions.",
                "Follow mode is bounded polling over unary transports and reports timeout metadata instead of waiting indefinitely.",
            ],
            &[
                "Use debug_events after debug_session_start with role=observer when a passive consumer needs replay or live-ish status, breakpoint, pause, resume, or step events.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_session_status",
            "dev debug session status",
            "partial",
            debug_session_status_schema(),
            "One leased debugger-session snapshot plus the current live debug status.",
            &[
                (
                    "session",
                    "Current metadata for the requested debugger session id.",
                ),
                (
                    "status",
                    "Live debug status snapshot including current ownership and breakpoints.",
                ),
                (
                    "found",
                    "Whether the requested debugger session id is still active.",
                ),
            ],
            &[
                "Session ids are lease-scoped and may expire; callers must handle not-found or expired responses explicitly.",
            ],
            &[
                "Use debug_session_status when a workflow needs to refresh lease metadata or confirm whether a previously created debugger session is still active.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_session_end",
            "dev debug session end",
            "partial",
            debug_session_end_schema(),
            "Debugger-session release result preserving whether the lease ended, whether the runtime resumed, and the post-end live debug status.",
            &[
                (
                    "ended",
                    "Whether the requested debugger session lease was released.",
                ),
                (
                    "resumed",
                    "Whether the runtime resumed as part of ending the session.",
                ),
                ("status", "Post-end live debug status snapshot."),
            ],
            &[
                "Ending a session is a mutating dev-only operation and may fail with structured ownership or not-found notices when the lease is gone or belongs to another caller.",
            ],
            &[
                "Use debug_session_end when a leased workflow is finished so later debugger calls do not stay blocked behind a stale owner.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_pause",
            "dev debug pause",
            "partial",
            object_schema(
                Map::from_iter([(
                    "session".to_string(),
                    schema_string(
                        "Optional leased debugger session id that should own this pause request.",
                    ),
                )]),
                &[],
            ),
            "Manual live debug pause result preserving whether the pause request applied, the post-operation status, and any idempotent notices.",
            &[
                (
                    "applied",
                    "Whether the live host actually transitioned into a paused state.",
                ),
                ("status", "Post-operation live debug status snapshot."),
                (
                    "notices",
                    "Structured notices when the runtime was already paused or hooks are unavailable.",
                ),
            ],
            &[
                "This is dev-only live control, not a safe read-only inspection command.",
                "Hosts without wired debug hooks or inactive live runtimes may return structured unsupported/idempotent notices instead of pausing.",
                "When a lease owner exists, callers should pass the matching session id instead of relying on session-free mutation.",
            ],
            &[
                "Use debug_pause only after debug_status or debug_session_start confirms the attached host supports live debug control and the workflow truly needs a paused runtime.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_resume",
            "dev debug resume",
            "partial",
            object_schema(
                Map::from_iter([(
                    "session".to_string(),
                    schema_string(
                        "Optional leased debugger session id that should own this resume request.",
                    ),
                )]),
                &[],
            ),
            "Manual live debug resume result preserving whether the resume request applied, the post-operation status, and any idempotent notices.",
            &[
                (
                    "applied",
                    "Whether the live host actually transitioned back into a running state.",
                ),
                ("status", "Post-operation live debug status snapshot."),
                (
                    "notices",
                    "Structured notices when the runtime was already running or hooks are unavailable.",
                ),
            ],
            &[
                "This is dev-only live control, not a read-only inspection command.",
                "Resuming may be a no-op with structured notices when the runtime is not currently paused.",
            ],
            &[
                "Use debug_resume only after an intentional leased or session-free pause/step/wait workflow when the runtime should continue running.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_step",
            "dev debug step",
            "partial",
            debug_step_schema(),
            "Bounded live debug step result preserving the requested step kind/count, whether the step applied, the post-step status, and any notices.",
            &[
                (
                    "kind",
                    "Requested step kind echoed back by the CLI envelope.",
                ),
                (
                    "count",
                    "Requested step count echoed back by the CLI envelope.",
                ),
                ("applied", "Whether the live host completed a step request."),
                ("status", "Post-step live debug status snapshot."),
            ],
            &[
                "Step requests require a live debug runtime that is already paused; unsupported or running runtimes return structured notices instead of guessing.",
                "Action stepping remains tied to the live host's current action-queue boundaries and can report partial/idle outcomes honestly.",
            ],
            &[
                "Use debug_step after debug_pause when the workflow needs one bounded frame or action progression instead of fully resuming the runtime.",
            ],
        ),
        ai_tool_from_command_with_status(
            "debug_wait",
            "dev debug wait",
            "partial",
            debug_wait_schema(),
            "Leased debug wait result preserving whether a breakpoint hit before timeout, the matched breakpoint when present, and the post-wait live debug status.",
            &[
                (
                    "completed",
                    "Whether the wait finished without transport-level failure.",
                ),
                (
                    "timedOut",
                    "Whether the wait reached its timeout before a breakpoint matched.",
                ),
                (
                    "breakpointHit",
                    "Breakpoint-hit metadata when the wait stopped on a match.",
                ),
                ("status", "Post-wait live debug status snapshot."),
            ],
            &[
                "This still depends on a live host build exposing debug hooks plus a currently leased debugger session id.",
                "Wait is not a general scenario-runner primitive; it is a debugger-oriented resume-until-breakpoint operation.",
            ],
            &[
                "Use debug_wait after debug_session_start and breakpoint_add when the workflow should resume execution until one observed change or match breakpoint fires.",
            ],
        ),
        ai_tool_from_command_with_status(
            "breakpoint_list",
            "dev breakpoint list",
            "partial",
            object_schema(
                Map::from_iter([(
                    "session".to_string(),
                    schema_string(
                        "Optional leased debugger session id used to explain ownership for the listed breakpoint registry.",
                    ),
                )]),
                &[],
            ),
            "Live debug breakpoint registry plus the current live debug status snapshot.",
            &[
                (
                    "breakpoints",
                    "Registered debug breakpoints with stable ids, kinds, hit counts, and predicates or observed values.",
                ),
                (
                    "status",
                    "Current live debug status, including the latest breakpoint hit when available.",
                ),
                (
                    "notices",
                    "Structured notices returned alongside the registry snapshot.",
                ),
            ],
            &[
                "Breakpoint listing is dev-only and only as useful as the attached live runtime's current debug support.",
            ],
            &[
                "Use breakpoint_list before removing breakpoints or to confirm the current live registry and latest hit metadata.",
            ],
        ),
        ai_tool_from_command_with_status(
            "breakpoint_add",
            "dev breakpoint add",
            "partial",
            debug_breakpoint_add_schema(),
            "Breakpoint registration result preserving the stored predicate, the post-add live debug status, and validation/notices from the bridge.",
            &[
                ("added", "Whether the requested breakpoint was registered."),
                (
                    "breakpoint",
                    "Stored breakpoint metadata including stable id and normalized predicate.",
                ),
                ("status", "Current live debug status after registration."),
            ],
            &[
                "Match breakpoints require exactly one predicate option matching the shared dev assert / wait-for grammar, while change breakpoints watch for observed JSON value changes and may omit predicates.",
                "Malformed paths or regex predicates fail fast with structured invalid_query_filter errors from the bridge.",
            ],
            &[
                "Use breakpoint_add after debug_status or debug_session_start when you need either a predicate-backed match breakpoint or a change-watching breakpoint on a known observable state path.",
            ],
        ),
        ai_tool_from_command_with_status(
            "breakpoint_remove",
            "dev breakpoint remove",
            "partial",
            debug_breakpoint_remove_schema(),
            "Breakpoint removal result preserving whether the id was removed, the post-remove live debug status, and any idempotent notices.",
            &[
                (
                    "removed",
                    "Whether a registered breakpoint with the requested id was removed.",
                ),
                ("id", "Breakpoint id the CLI attempted to remove."),
                ("status", "Current live debug status after removal."),
            ],
            &[
                "Removal is dev-only and requires a stable breakpoint id previously returned by breakpoint_list or breakpoint_add.",
            ],
            &[
                "Use breakpoint_remove after breakpoint_list when a named live breakpoint is no longer needed.",
            ],
        ),
        ai_tool_from_command_with_status(
            "http",
            "dev http",
            "partial",
            http_probe_schema(false),
            "One bounded HTTP probe result preserving response metadata, optional query evaluation, and failure details.",
            &[
                ("probe", "Probe family identifier."),
                (
                    "status",
                    "Structured success/failure status for the requested HTTP check.",
                ),
                (
                    "response",
                    "Captured HTTP response metadata including status, headers, and body details when available.",
                ),
            ],
            &[
                "Probe success still depends on the external endpoint being reachable and in the expected state.",
            ],
            &[
                "Use http for one-shot external validation steps that should stay in the CLI/test-runner flow instead of inventing repo-specific shell probes.",
            ],
        ),
        ai_tool_from_command_with_status(
            "http_wait",
            "dev http-wait",
            "partial",
            http_probe_schema(true),
            "Polling HTTP probe result preserving the final response, attempts, and elapsed time.",
            &[
                ("probe", "Probe family identifier."),
                (
                    "status",
                    "Structured success/failure status for the waited HTTP check.",
                ),
                (
                    "attempts",
                    "Number of probe attempts before success or timeout.",
                ),
                (
                    "elapsedMs",
                    "Total wait duration before the probe finished.",
                ),
            ],
            &[
                "Probe success still depends on the external endpoint being reachable and eventually satisfying the requested contract.",
            ],
            &[
                "Use http_wait when a repo-local service needs time to warm up and you want the existing CLI/test-runner wait semantics instead of a separate shell loop.",
            ],
        ),
        ai_tool_from_command_with_status(
            "fetch",
            "dev fetch",
            "partial",
            fetch_probe_schema(),
            "Fetch result preserving output-path, digest, and optional query-evaluation metadata.",
            &[
                ("probe", "Probe family identifier."),
                (
                    "status",
                    "Structured success/failure status for the fetch step.",
                ),
                (
                    "output",
                    "Persisted output metadata when the fetched bytes were written to disk.",
                ),
                ("sha256", "Computed SHA-256 digest for the fetched bytes."),
            ],
            &[
                "Fetch success depends on the referenced source existing and being readable from the current machine.",
            ],
            &[
                "Use fetch when a repo-local workflow needs reproducible artifact download/copy validation inside the same CLI/test-runner surface as other checks.",
            ],
        ),
        ai_tool_from_command_with_status(
            "websocket",
            "dev websocket",
            "partial",
            websocket_probe_schema(),
            "Bounded websocket probe result preserving sent/received frames and timing metadata.",
            &[
                ("probe", "Probe family identifier."),
                (
                    "status",
                    "Structured success/failure status for the websocket session.",
                ),
                (
                    "receivedText",
                    "Observed text frames captured before the session completed.",
                ),
                (
                    "elapsedMs",
                    "Total session duration before success or timeout.",
                ),
            ],
            &[
                "Probe success depends on the external websocket endpoint being reachable and returning the expected frames.",
            ],
            &[
                "Use websocket for repo-local event-stream smoke checks that should share the CLI/test-runner output and exit-code contract with the other probe tools.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_profile_list",
            "project profile list",
            "stable",
            empty_object_schema(),
            "Checked-in lifecycle profile summaries with source-path provenance.",
            &[
                ("sourcePath", "Profiles file path used for the listing."),
                (
                    "profiles",
                    "Profile names, descriptions, and declared step kinds.",
                ),
            ],
            &[
                "This only reads the configured profiles file; it does not validate live game state or execute any lifecycle steps.",
            ],
            &[
                "Use project_profile_list before project_profile_show or project_profile_run when you need the repo-authored profile names available in the current checkout.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_profile_show",
            "project profile show",
            "stable",
            tool_name_schema("Exact checked-in lifecycle profile name from project_profile_list."),
            "One resolved lifecycle profile with normalized step settings and path resolution.",
            &[
                ("name", "Resolved profile name."),
                ("sourcePath", "Profiles file path used for the lookup."),
                (
                    "profile",
                    "Resolved step definitions with defaults and normalized paths applied.",
                ),
            ],
            &[
                "This reads repo-authored profile data only; execution still happens through project_profile_run.",
            ],
            &[
                "Use project_profile_show before project_profile_run when an agent needs to inspect the exact composed lifecycle steps and resolved deploy paths.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_profile_run",
            "project profile run",
            "partial",
            tool_name_schema("Exact checked-in lifecycle profile name from project_profile_list."),
            "Profile-run result preserving per-step success or structured failure details from the composed lifecycle commands.",
            &[
                ("name", "Resolved profile name."),
                ("steps", "Per-step execution results or structured errors."),
                ("sourcePath", "Profiles file path used for the run."),
            ],
            &[
                "Execution success depends on the underlying lifecycle steps, local config, and live environment readiness.",
            ],
            &[
                "Use project_profile_run for repo-authored lifecycle orchestration when the checked-in profile already matches the desired deploy/launch/attach flow.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_hook_list",
            "project hook list",
            "stable",
            empty_object_schema(),
            "Repo-authored hook summaries with source-path provenance.",
            &[
                ("sourcePath", "Hooks file path used for the listing."),
                ("hooks", "Available hook names and descriptions."),
            ],
            &[
                "This only reads the configured hooks file; it does not spawn any repo hook commands.",
            ],
            &[
                "Use project_hook_list before project_hook_show or project_hook_run when you need to discover the repo-authored extension points available in the current checkout.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_hook_show",
            "project hook show",
            "stable",
            tool_name_schema("Exact repo-local hook name from project_hook_list."),
            "One resolved repo-local hook definition with normalized command, cwd, and env metadata.",
            &[
                ("name", "Resolved hook name."),
                ("sourcePath", "Hooks file path used for the lookup."),
                (
                    "hook",
                    "Resolved command, args, cwd, and env for the selected hook.",
                ),
            ],
            &[
                "This reads repo-authored hook metadata only; execution still happens through project_hook_run.",
            ],
            &[
                "Use project_hook_show before project_hook_run when an agent needs to inspect what the repo-authored extension will execute and which working directory it uses.",
            ],
        ),
        ai_tool_from_command_with_status(
            "project_hook_run",
            "project hook run",
            "partial",
            project_hook_run_schema(),
            "Hook execution result preserving repo-local hook output and declared artifacts.",
            &[
                ("name", "Resolved hook name."),
                ("sourcePath", "Hooks file path used for the run."),
                (
                    "result",
                    "Hook-reported output payload plus resolved artifact metadata.",
                ),
            ],
            &[
                "Execution success depends on the repo-authored hook command, local files, and any external tools it shells out to.",
            ],
            &[
                "Use project_hook_run only for repo-authored extension flows that already exist in the checked-in hooks file; it is not a second plugin protocol.",
            ],
        ),
        ai_tool_from_command(
            "skill_install",
            "skill install",
            skill_install_schema(),
            "Repo-local skill installation metadata preserving the CLI-written target paths.",
            &[
                ("name", "Installed skill name."),
                (
                    "path",
                    "Filesystem path to the written router SKILL.md entrypoint.",
                ),
                (
                    "skillDir",
                    "Directory containing the installed repo-local router skill.",
                ),
                (
                    "sourcePath",
                    "Checked-in source router skill path copied by the CLI.",
                ),
                (
                    "installedFiles",
                    "Skill and reference files written under the installed skill directory.",
                ),
            ],
            &[
                "This installs the checked-in repo-local skill pack into another repo tree; it is not a hosted package installer or remote registry workflow.",
                "When path is omitted, the CLI writes the self-contained spirectl skill under ./.agents/skills/ in the current working directory.",
            ],
            &[
                "Use skill_install when another local repo needs the checked-in spirectl skill directory without shelling out to a separate installer.",
            ],
        ),
        ai_tool_from_command(
            "screenshot",
            "dev screenshot",
            screenshot_schema(),
            "PNG artifact metadata for one bridge-backed runtime screenshot written to disk.",
            &[
                (
                    "path",
                    "Filesystem path of the written screenshot artifact.",
                ),
                ("format", "Encoded image format reported by the bridge."),
                (
                    "screen",
                    "Visible screen metadata captured at screenshot time.",
                ),
                ("byteLength", "Written PNG byte length."),
            ],
            &[
                "This captures an artifact to disk; use state for structured runtime inspection instead of treating screenshots as the primary data surface.",
            ],
            &[
                "Call game_info first, then use screenshot when you need a visual artifact or debugging evidence tied to a known file path.",
            ],
        ),
        ai_tool_from_command_with_status(
            "inspect_viewport_presets",
            "inspect viewport-presets",
            "stable",
            {
                let mut properties = Map::new();
                properties.insert(
                    "presetCatalogs".to_string(),
                    json!({
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "Optional explicit viewport preset catalog paths loaded in addition to built-ins."
                    }),
                );
                object_schema(properties, &[])
            },
            "Effective viewport preset catalog for repeatable screenshot and diagnostics capture.",
            &[
                ("source", "Catalog source marker."),
                (
                    "presets",
                    "Preset names plus dimensions and source metadata.",
                ),
            ],
            &[],
            &[
                "Use inspect_viewport_presets before screenshot, screenshot_diff, or diagnostics when you need repeatable viewport sizing without hardcoding dimensions.",
            ],
        ),
        ai_tool_from_command_with_status(
            "screenshot_diff",
            "dev screenshot-diff",
            "stable",
            screenshot_diff_schema(),
            "Screenshot comparison result preserving diff metrics and optional bundle paths for live or offline diffing.",
            &[
                (
                    "matched",
                    "Whether the screenshot comparison stayed within the requested diff thresholds.",
                ),
                ("diffPixels", "Number of differing pixels."),
                ("diffRatio", "Ratio of differing pixels to total pixels."),
                (
                    "bundle",
                    "Persisted comparison artifact paths when bundleDir was requested.",
                ),
            ],
            &[],
            &[
                "Use screenshot_diff when visual regression checks should stay on the same CLI/test-runner path as live screenshot capture and diagnostics.",
            ],
        ),
        ai_tool_from_command_with_status(
            "snapshot_export",
            "dev snapshot export",
            "stable",
            snapshot_export_schema(),
            "Bounded snapshot baseline export result with inspectable JSON captures and optional runtime PNG evidence.",
            &[
                (
                    "status",
                    "Export status for the requested bounded snapshot bundle.",
                ),
                (
                    "outputDir",
                    "Directory where the baseline bundle was written.",
                ),
                (
                    "captures",
                    "Per-section captured bundle file paths and normalized values.",
                ),
            ],
            &[
                "Snapshot export is compare/export oriented only; it does not create a restore or runtime-mutation surface.",
                "The authored spec must stay within the bounded sections supported by the CLI snapshot workflow.",
            ],
            &[
                "Use snapshot_export to create or refresh a checked-in bounded regression baseline after deterministic setup through existing scenarios or fixtures.",
            ],
        ),
        ai_tool_from_command_with_status(
            "snapshot_compare",
            "dev snapshot compare",
            "stable",
            snapshot_compare_schema(),
            "Bounded snapshot regression comparison result with per-section mismatches and optional current evidence bundle paths.",
            &[
                (
                    "matched",
                    "Whether every requested bounded snapshot section matched the baseline.",
                ),
                ("sections", "Per-section normalized comparison details."),
                ("mismatches", "Summary of any section-level mismatches."),
                (
                    "bundle",
                    "Current comparison bundle paths when bundleDir was requested.",
                ),
            ],
            &[
                "Snapshot comparison requires a previously exported bounded baseline bundle; it is not a generic full-state diff or replay mechanism.",
            ],
            &[
                "Use snapshot_compare after deterministic setup when a stable screen or workflow should match a checked-in bounded regression baseline.",
            ],
        ),
        ai_tool_from_command(
            "wait_for",
            "dev wait-for",
            wait_or_assert_schema(true),
            "Assertion-style wait result that preserves timeout failures and last observed values.",
            &[
                ("matched", "Whether the predicate eventually matched."),
                (
                    "actual",
                    "Last observed value for the requested state path.",
                ),
                ("attempts", "Number of polling attempts performed."),
                (
                    "elapsedMs",
                    "Total wait duration before success or timeout.",
                ),
            ],
            &[
                "Only the current simple dotted-path query language is supported; array wildcards and array filter selectors (`=`, `!=`, `*=`, `~=`, `>`, `>=`, `<`, `<=`) are available.",
                "source: \"actions\" queries the resolved spirectl.state-actions/v0 document (the same document `sts2 state actions` returns) instead of the plain state snapshot.",
            ],
            &[
                "Use wait_for for script-friendly eventual conditions, then follow with state or assert once the condition is satisfied.",
            ],
        ),
        ai_tool_from_command(
            "assert",
            "dev assert",
            wait_or_assert_schema(false),
            "Immediate assertion result against the current runtime state snapshot.",
            &[
                (
                    "matched",
                    "Whether the predicate matched the current value.",
                ),
                ("actual", "Observed value at the requested state path."),
                ("expected", "Expected predicate value when applicable."),
                (
                    "resolution",
                    "Missing-path details when the lookup cannot be fully resolved.",
                ),
            ],
            &[
                "Only the current simple dotted-path query language is supported; array wildcards and array filter selectors (`=`, `!=`, `*=`, `~=`, `>`, `>=`, `<`, `<=`) are available.",
                "source: \"actions\" queries the resolved spirectl.state-actions/v0 document (the same document `sts2 state actions` returns) instead of the plain state snapshot.",
            ],
            &[
                "Use assert for single-snapshot checks after state or wait_for, especially in test and automation workflows.",
            ],
        ),
        ai_tool_from_command(
            "code_locate",
            "code locate",
            code_locate_schema(),
            "Search-oriented static inspection result for managed types/methods or fallback symbols.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("matchCount", "Total number of returned matches."),
                (
                    "matches",
                    "Ranked static inspection results with stable ids for follow-up commands.",
                ),
            ],
            &[
                "This is static inspection only and does not use the live runtime bridge.",
                "Managed dependency DLLs are excluded by default; set includeDependencies=true only when dependency symbols are the intended target.",
                "includeDependencies is independent from includeMods.",
            ],
            &[
                "Use code_locate first in managed-code workflows so later commands can consume stable type or method ids instead of repeating fuzzy queries.",
            ],
        ),
        ai_tool_from_command(
            "code_describe",
            "code describe",
            code_resolve_schema(),
            "Exact structured metadata view for one type or method resolved from static inspection.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("result", "Resolved type or method metadata."),
                ("notes", "Coverage notes when enrichment is partial."),
            ],
            &[
                "This expects an exact subject query and may return structured ambiguous_query or no_match errors when the input is fuzzy.",
                "Managed dependency DLLs are excluded by default; set includeDependencies=true only when dependency symbols are the intended target.",
                "includeDependencies is independent from includeMods.",
            ],
            &[
                "Follow code_locate with code_describe once the agent has selected a stable id or exact symbol.",
            ],
        ),
        ai_tool_from_command(
            "code_refs",
            "code refs",
            code_locate_schema(),
            "Static reference navigation result for one exact symbol or a symbol-name fallback search.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("matchCount", "Total number of returned references."),
                (
                    "matches",
                    "Reference records including container ids and precision metadata.",
                ),
            ],
            &[
                "Symbol-mode reference search is a fallback and can be broader or less exact than type or method lookups.",
                "Managed dependency DLLs are excluded by default; set includeDependencies=true only when dependency symbols are the intended target.",
                "includeDependencies is independent from includeMods.",
            ],
            &[
                "Prefer code_refs after code_describe when the agent already knows the exact type or method id it wants to navigate from.",
            ],
        ),
        ai_tool_from_command(
            "code_derived",
            "code derived",
            code_derived_schema(),
            "Direct derived and implementation relationships for one exact static type.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("matchCount", "Total number of returned relationships."),
                (
                    "matches",
                    "Derived or implementation edges anchored to stable type ids.",
                ),
            ],
            &[
                "Only type relationships are supported; methods and symbols are out of scope for this command.",
                "Managed dependency DLLs are excluded by default; set includeDependencies=true only when dependency symbols are the intended target.",
                "includeDependencies is independent from includeMods.",
            ],
            &[
                "Use code_derived after code_locate or code_describe when the agent needs inheritance or implementation context.",
            ],
        ),
        ai_tool_from_command(
            "code_decompile",
            "code decompile",
            code_decompile_schema(),
            "Explicit deeper static view for one exact type or method, with optional ILSpy full output when metadata alone is not enough.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                (
                    "backend",
                    "Which decompile backend produced the text field.",
                ),
                (
                    "result",
                    "Metadata summary text by default, or ILSpy-backed text when full=true.",
                ),
                (
                    "notes",
                    "Coverage notes for the selected metadata or ILSpy decompile backend.",
                ),
            ],
            &[
                "Use the default metadata path first; set full=true only when the lightweight summary is not enough.",
                "Managed dependency DLLs are excluded by default; set includeDependencies=true only when dependency symbols are the intended target.",
                "includeDependencies is independent from includeMods.",
            ],
            &[
                "Use code_decompile last, after code_locate and code_describe, and reserve full=true for exact matches that need source-grade detail.",
            ],
        ),
        ai_tool_from_command_with_status(
            "code_hooks",
            "code hooks",
            "stable",
            code_hooks_schema(),
            "Paginated hook-oriented static catalog with likely managed method targets, script hints, filters, facets, and follow-up ids.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("totalCount", "Total hook candidates after filters."),
                (
                    "returnedCount",
                    "Number of hook candidates returned in this page.",
                ),
                (
                    "matches",
                    "Hook candidates with stable ids, advisory forms, reference counts, and script hints.",
                ),
            ],
            &[
                "This is staged static inspection only; it does not prove runtime patch safety or load-order behavior.",
                "Omit query to browse the installed hook catalog; use limit and offset for large result sets.",
            ],
            &[
                "Use code_hooks to browse or narrow hook candidates before resolving one exact candidate with code_hook_info.",
            ],
        ),
        ai_tool_from_command_with_status(
            "code_hook_info",
            "code hook-info",
            "stable",
            code_hook_info_schema(),
            "Exact hook-ready signature projection for one managed method id or exact lookup signature.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("id", "Exact managed method id resolved by the helper."),
                (
                    "signature",
                    "Exact lookup signature that can be reused in later commands.",
                ),
            ],
            &[
                "This expects one exact hook candidate and will return structured ambiguous-query or no-match failures when the input is still fuzzy.",
            ],
            &[
                "Use code_hook_info after code_hooks once the agent has chosen one exact method candidate to carry into docs, decompile, or follow-up reference navigation.",
            ],
        ),
        ai_tool_from_command(
            "code_scene_search",
            "code scene-search",
            code_scene_search_schema(),
            "Search-oriented static Godot scene/resource inspection result with stable ids for follow-up scene commands.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                (
                    "matchCount",
                    "Total number of returned scene/resource matches.",
                ),
                (
                    "matches",
                    "Ranked scene, node, or resource matches with follow-up ids.",
                ),
            ],
            &[
                "Scene inspection is static-only in this spec and does not reflect the live runtime scene tree.",
            ],
            &[
                "Use code_scene_search first for UI/resource workflows, then follow with code_scene_tree or code_scene_node using the returned ids.",
            ],
        ),
        ai_tool_from_command(
            "code_scene_tree",
            "code scene-tree",
            code_scene_tree_schema(),
            "Exact static scene tree for one supported Godot text, binary, or packed scene asset.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("scene", "Resolved scene metadata."),
                ("nodes", "Tree-ordered static node structure."),
            ],
            &[
                "Static scene inspection covers supported text, binary, and packed assets only; live runtime trees remain a separate dev surface.",
            ],
            &[
                "Use code_scene_tree after code_scene_search when the agent needs the full node hierarchy for one scene.",
            ],
        ),
        ai_tool_from_command(
            "code_scene_node",
            "code scene-node",
            code_scene_node_schema(),
            "Exact static node details for one node inside one supported Godot text, binary, or packed scene asset.",
            &[
                ("command", "Helper command that produced the response."),
                ("status", "Structured helper status."),
                ("scene", "Resolved scene metadata."),
                (
                    "node",
                    "Resolved node details including references and attached script ids when available.",
                ),
            ],
            &[
                "Static scene inspection covers supported text, binary, and packed assets only; live runtime node observation remains a separate dev surface.",
            ],
            &[
                "Use code_scene_node after code_scene_search or code_scene_tree when the agent needs one exact node and its attached script/resource references.",
            ],
        ),
        ai_tool_from_command_with_status(
            "inspect_reference_topics",
            "inspect reference-topics",
            "stable",
            empty_object_schema(),
            "Checked-in modding workflow topic catalog with staged commands, limits, and doc links.",
            &[
                ("source", "Catalog source marker."),
                (
                    "topics",
                    "Reference topics with limits, related commands, and example sequences.",
                ),
            ],
            &[
                "This is a checked-in reference catalog, not a live search engine or IDE-grade documentation index.",
            ],
            &[
                "Use inspect_reference_topics when replacing legacy modding-reference lookups with the shipped staged workflow catalog before drilling into code_hook_* or scene commands.",
            ],
        ),
        ai_tool_from_command(
            "test_run",
            "test run",
            json!({
                "type": "object",
                "additionalProperties": false,
                "properties": test_run_schema_properties,
                "oneOf": [
                    { "required": ["path"] },
                    { "required": ["inline"] }
                ]
            }),
            "Structured scenario-run summary with persisted artifact locations and per-step outputs/errors.",
            &[
                (
                    "status",
                    "Aggregate run status across all discovered scenarios.",
                ),
                (
                    "scenarios",
                    "Per-scenario step history, outputs, and structured failures.",
                ),
                (
                    "artifacts",
                    "Persisted summary and failure artifact locations.",
                ),
                (
                    "remoteArtifacts",
                    "Service-backed completed runs add S92 artifact metadata with relativePath and downloadUrl values.",
                ),
                ("exitCode", "CLI exit code preserved for automation."),
            ],
            &[
                "Existing .sts2.yaml and .sts2.json files/directories plus inline YAML or JSON bodies are supported, but there is still no non-document runner API beyond the current scenario model.",
                "Named test profiles are project-defined config entries; profile names such as mock or live are not built in.",
                "durable is honored by the automation-service /v0/test-runs job store; local CLI execution ignores remote job persistence and does not expose a separate MCP runner schema.",
            ],
            &[
                "Use profile for project-defined scenario selection/config/lifecycle policy, path for checked-in reusable .sts2.yaml or .sts2.json scenarios, or inline for one-off YAML/JSON bodies.",
                "Use durable with a service-backed client when callers need restart-recoverable job status and remoteArtifacts[] for later artifact download.",
            ],
        ),
        ai_tool_from_command_with_status(
            "test_stress",
            "test stress",
            "stable",
            json!({
                "type": "object",
                "additionalProperties": false,
                "properties": test_stress_schema_properties,
                "oneOf": [
                    { "required": ["path", "iterations"] },
                    { "required": ["path", "durationMs"] }
                ]
            }),
            "Aggregate repeated-run summary with per-iteration nested run references, failure caps, and persisted stress artifacts.",
            &[
                (
                    "status",
                    "Aggregate stress-run status across all completed iterations.",
                ),
                (
                    "completedIterations",
                    "Number of iterations actually executed before the stop condition.",
                ),
                (
                    "iterations",
                    "Per-iteration nested run summaries and artifact paths.",
                ),
                (
                    "firstFailure",
                    "First failed iteration reference when any iteration failed.",
                ),
                (
                    "lastFailure",
                    "Last failed iteration reference when any iteration failed.",
                ),
                (
                    "artifacts",
                    "Persisted aggregate summary path and stress artifact root.",
                ),
            ],
            &[
                "This reuses the existing scenario runner only; there is still no distributed or remote orchestration service.",
                "Exactly one bound must be supplied: iterations or durationMs.",
            ],
            &[
                "Use test_stress when an existing stable scenario should be replayed repeatedly with bounded failure tolerance and inspectable aggregate artifacts.",
            ],
        ),
    ]
}
