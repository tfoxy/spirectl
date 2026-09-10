use super::*;

#[test]
fn inspect_actions_json_exposes_structured_action_arguments() {
    // `inspect actions` is the static action catalog; live, resolved available actions
    // now come from `state actions`.
    let response = run(&["sts2", "--json", "inspect", "actions"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert!(payload["availableActions"].as_array().unwrap().is_empty());
    let choose = payload["supportedActions"]
        .as_array()
        .expect("supported actions")
        .iter()
        .find(|action| action["kind"] == "choose")
        .expect("choose descriptor");
    assert_eq!(choose["argumentSchema"][0]["name"], "choiceId");
}

#[test]
fn inspect_actions_json_exposes_action_status_and_parameters() {
    let config = AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Mock,
            mock_scenario: MockScenario::Lobby,
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    };

    let temp = tempfile::NamedTempFile::new().expect("temp config");
    let config_text = serde_yaml::to_string(&config).expect("yaml");
    fs::write(temp.path(), config_text).expect("write config");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        temp.path().to_str().expect("utf8 path"),
        "inspect",
        "actions",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["supportedActions"][0]["status"], "implemented");
    let select_character = payload["supportedActions"]
        .as_array()
        .expect("supported actions")
        .iter()
        .find(|action| action["id"] == "select-character")
        .expect("select-character action");
    assert_eq!(select_character["parameters"][0]["name"], "characterId");
}

#[test]
fn intent_action_contract_current() {
    let inspect_response = run(&["sts2", "--json", "inspect", "actions"]);
    let inspect_payload: Value = serde_json::from_str(&inspect_response.stdout).expect("json");

    assert_eq!(
        inspect_payload["stateContract"]["fallbackChoosePolicy"],
        "Use typed overlay state and preferredAction metadata first. Generic choose is retained only for currently executable compatibility controls or unmodeled visible overlay controls; close/back actions are advertised only when the bridge can validate a legal hook."
    );
    let choose = inspect_payload["supportedActions"]
        .as_array()
        .expect("supported actions")
        .iter()
        .find(|action| action["kind"] == "choose")
        .expect("choose descriptor");
    assert_eq!(choose["kindDescriptor"]["fallback"], true);
    assert_eq!(
        choose["kindDescriptor"]["summary"],
        "Fallback-only compatibility action for generic, modded, or unmodeled visible choices."
    );
    assert_eq!(choose["argumentSchema"][0]["name"], "choiceId");
    assert!(
        choose["failureReasonCodes"]
            .as_array()
            .expect("failure codes")
            .iter()
            .any(|code| code == "wrong_screen")
    );

    let error_config = write_raw_config("transport:\n  kind: mock\n");
    let error_response = run_error(&[
        "sts2",
        "--json",
        "--config",
        error_config.path().to_str().expect("utf8 path"),
        "act",
        "play-card",
        "--card",
        "missing",
    ]);
    let error_payload: Value = serde_json::from_str(&error_response.stdout).expect("json");
    assert_eq!(error_payload["error"]["code"], "invalid_action");
    assert_eq!(
        error_payload["error"]["actionFailure"]["reasonCode"],
        "invalid_action"
    );
    assert_eq!(
        error_payload["error"]["actionFailure"]["fieldDiagnostics"][0]["field"],
        "card_id"
    );

    let ai_response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let ai_payload: Value = serde_json::from_str(&ai_response.stdout).expect("json");
    let act_tool = ai_payload["tools"]
        .as_array()
        .expect("tools")
        .iter()
        .find(|tool| tool["name"] == "act")
        .expect("act tool");
    let supported = act_tool["mapsTo"].as_array().expect("supported commands");
    assert_eq!(
        supported.last().expect("last supported command"),
        "act choose"
    );
    assert!(
        act_tool["limitations"]
            .as_array()
            .expect("limitations")
            .iter()
            .any(|item| item
                .as_str()
                .expect("limitation")
                .contains("choose is fallback-only"))
    );

    let runner_response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        "--inline",
        r#"{"name":"fallback-vocabulary","steps":[{"act.fallback-choose":{"choice":"menu:start-run"}}]}"#,
    ]);
    let runner_payload: Value = serde_json::from_str(&runner_response.stdout).expect("runner json");
    assert_eq!(
        runner_payload["scenarios"][0]["steps"][0]["canonicalId"],
        "act.fallback-choose"
    );
    assert_eq!(
        runner_payload["scenarios"][0]["steps"][0]["input"]["fallback"],
        true
    );

    let runner_error_response = run(&[
        "sts2",
        "--json",
        "--config",
        error_config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--inline",
        r#"{"name":"structured-action-error","steps":[{"act.play-card":{"card":"missing"}}]}"#,
    ]);
    let runner_error_payload: Value =
        serde_json::from_str(&runner_error_response.stdout).expect("runner error json");
    assert_eq!(
        runner_error_payload["scenarios"][0]["steps"][0]["error"]["actionFailure"]["reasonCode"],
        "invalid_action"
    );
    assert_eq!(
        runner_error_payload["scenarios"][0]["steps"][0]["error"]["actionFailure"]["fieldDiagnostics"]
            [0]["field"],
        "card_id"
    );
}

#[test]
fn inspect_ai_tools_json_exposes_agent_facing_catalog_metadata() {
    let response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let tools = payload["tools"].as_array().expect("tools array");

    let act_tool = tools
        .iter()
        .find(|tool| tool["name"] == "act")
        .expect("act tool");
    assert_eq!(payload["source"], "command-catalog");
    assert_eq!(payload["adapter"]["transport"], "stdio");
    assert_eq!(act_tool["mapsTo"][0], "act confirm-selection");
    assert_eq!(
        act_tool["mapsTo"]
            .as_array()
            .expect("mapsTo")
            .last()
            .expect("last"),
        "act choose"
    );
    assert_eq!(
        act_tool["inputSchema"]["properties"]["kind"]["type"],
        "string"
    );
    assert_eq!(
        act_tool["limitations"][0],
        "Use state.actions[] first; each action already carries kind, compact args, owner, and stateRefs for the current screen."
    );
    assert_eq!(
        act_tool["recommendedUsage"][0],
        "Read state and inspect_actions before calling act so the agent uses currently executable action ids."
    );
    assert_eq!(
        act_tool["limitations"][1],
        "choose is fallback-only for generic, modded, or unmodeled visible controls exposed through state.fallbackChoices[]."
    );
    assert!(
        act_tool["mapsTo"]
            .as_array()
            .expect("act mapsTo")
            .iter()
            .any(|value| value == "act use-potion"),
        "act AI tool should advertise act use-potion in mapsTo"
    );
    assert_eq!(
        act_tool["inputSchema"]["properties"]["cardId"]["description"],
        "Stable card id from state.combat.hand[].id or state.actions[].args.cardId."
    );
    assert_eq!(
        act_tool["inputSchema"]["properties"]["characterId"]["description"],
        "Stable lobby character id from state.scene.items[].id or state.actions[].args.characterId."
    );

    let play_card = act_tool["inputSchema"]["properties"]["kind"]["enum"]
        .as_array()
        .expect("act enum");
    assert!(play_card.iter().any(|value| value == "play-card"));

    let test_run_tool = tools
        .iter()
        .find(|tool| tool["name"] == "test_run")
        .expect("test_run tool");
    assert_eq!(test_run_tool["readOnly"], false);

    let game_launch_tool = tools
        .iter()
        .find(|tool| tool["name"] == "game_launch")
        .expect("game_launch tool");
    assert_eq!(game_launch_tool["readOnly"], false);
    assert_eq!(game_launch_tool["mapsTo"][0], "game launch");
    assert_eq!(
        game_launch_tool["inputSchema"]["properties"]["timeoutMs"]["description"],
        "Maximum time to wait for live attachment after launch."
    );
    assert_eq!(
        game_launch_tool["inputSchema"]["properties"]["launchArgs"]["description"],
        "Optional Godot/game arguments appended after configured game.launchArgs for this launch."
    );
    assert_eq!(
        game_launch_tool["recommendedUsage"][0],
        "Call game_info first, then use game_launch when you need a fresh game process with the current bridge and launch configuration."
    );

    let game_attach_tool = tools
        .iter()
        .find(|tool| tool["name"] == "game_attach")
        .expect("game_attach tool");
    assert_eq!(game_attach_tool["mapsTo"][0], "game attach");
    assert_eq!(
        game_attach_tool["limitations"][0],
        "This only waits for an already running bridge/game process; it does not launch the game or repair installs on its own."
    );

    let game_deploy_tool = tools
        .iter()
        .find(|tool| tool["name"] == "game_deploy")
        .expect("game_deploy tool");
    assert_eq!(game_deploy_tool["readOnly"], false);
    assert_eq!(game_deploy_tool["inputSchema"]["required"][0], "path");
    assert_eq!(
        game_deploy_tool["limitations"][0],
        "Provide an authored mod project path; this mutates the configured mods directory and may stop/restart the local game process when requested."
    );

    let game_detect_tool = tools
        .iter()
        .find(|tool| tool["name"] == "game_detect")
        .expect("game_detect tool");
    assert_eq!(game_detect_tool["readOnly"], true);
    assert_eq!(game_detect_tool["mapsTo"][0], "game detect");
    assert_eq!(
        game_detect_tool["limitations"][0],
        "This is a read-only setup probe that reports configured-vs-detected install paths and live-bridge layout status; it does not attach to a running game."
    );
    assert_eq!(
        game_detect_tool["recommendedUsage"][0],
        "Use game_detect before game_info when install roots or local bridge layout are uncertain and you need setup guidance."
    );

    let assets_extract_tool = tools
        .iter()
        .find(|tool| tool["name"] == "assets_extract")
        .expect("assets_extract tool");
    assert_eq!(assets_extract_tool["readOnly"], false);
    assert_eq!(assets_extract_tool["mapsTo"][0], "assets extract");
    assert_eq!(assets_extract_tool["inputSchema"]["required"][0], "query");
    assert_eq!(
        assets_extract_tool["inputSchema"]["properties"]["resourcesDir"]["description"],
        "Optional override for the Godot scene/resource search root."
    );
    assert_eq!(
        assets_extract_tool["limitations"][0],
        "This writes extraction artifacts to disk and may still trigger the existing lifecycle repair/launch flow when live execution is required."
    );

    let assets_extract_batch_tool = tools
        .iter()
        .find(|tool| tool["name"] == "assets_extract_batch")
        .expect("assets_extract_batch tool");
    assert_eq!(assets_extract_batch_tool["readOnly"], false);
    assert_eq!(
        assets_extract_batch_tool["mapsTo"][0],
        "assets extract-batch"
    );
    assert_eq!(
        assets_extract_batch_tool["inputSchema"]["required"][0],
        "manifest"
    );
    assert_eq!(
        assets_extract_batch_tool["inputSchema"]["properties"]["manifest"]["description"],
        "Path to a generic JSON asset batch manifest with version 0 and assets[]."
    );
    assert_eq!(
        assets_extract_batch_tool["limitations"][0],
        "This writes extraction artifacts to disk and may trigger the existing lifecycle repair/launch flow when live execution is required by any request."
    );

    let load_fixture_tool = tools
        .iter()
        .find(|tool| tool["name"] == "load_fixture")
        .expect("load_fixture tool");
    assert_eq!(load_fixture_tool["mapsTo"][0], "dev fixture load");
    assert_eq!(
        load_fixture_tool["limitations"][0],
        "Only file-backed authored fixture recipes are supported here; inline fixture bodies and arbitrary runtime mutation remain out of scope."
    );
    assert_eq!(
        load_fixture_tool["limitations"][2],
        "The shipped executable fixture subset currently covers main-menu, combat, game-derived screen ids for map, rewards, rest-site, event-room, treasure/relic, shop, card/simple/deck/bundle selection, card-overlay, passive-card-overlay, Screens.CharacterSelect.NCharacterSelectScreen start-run recipes, and Screens.CharacterSelect.NMultiplayerLoadGameScreen load-run recipes including explicit event options, opened treasure-room proceed flow, shop inventory/card-removal overrides, visible overlay entry, and local-only degraded multiplayer reporting."
    );
    let load_fixture_output_paths = load_fixture_tool["outputShape"]["primaryFields"]
        .as_array()
        .expect("load_fixture output fields")
        .iter()
        .map(|field| field["path"].as_str().expect("field path"))
        .collect::<Vec<_>>();
    for path in ["recipeReport", "loaded"] {
        assert!(load_fixture_output_paths.contains(&path), "missing {path}");
    }

    let screenshot_tool = tools
        .iter()
        .find(|tool| tool["name"] == "screenshot")
        .expect("screenshot tool");
    assert_eq!(screenshot_tool["mapsTo"][0], "dev screenshot");
    assert_eq!(
        screenshot_tool["inputSchema"]["properties"]["output"]["description"],
        "Optional explicit PNG output path. When omitted, the CLI writes into the configured artifacts directory."
    );
    assert_eq!(
        screenshot_tool["inputSchema"]["properties"]["rpcTimeoutMs"]["description"],
        "Maximum bridge RPC time in milliseconds before screenshot capture fails with bridge_rpc_timeout."
    );
    let screenshot_diff_tool = tools
        .iter()
        .find(|tool| tool["name"] == "screenshot_diff")
        .expect("screenshot_diff tool");
    assert_eq!(
        screenshot_diff_tool["inputSchema"]["properties"]["rpcTimeoutMs"]["description"],
        "Maximum bridge RPC time in milliseconds for live screenshot capture when actual is omitted."
    );

    let skill_install_tool = tools
        .iter()
        .find(|tool| tool["name"] == "skill_install")
        .expect("skill_install tool");
    assert_eq!(skill_install_tool["readOnly"], false);
    assert_eq!(skill_install_tool["mapsTo"][0], "skill install");
    assert_eq!(
        skill_install_tool["inputSchema"]["properties"]["path"]["description"],
        "Optional skill root where the checked-in spirectl skill pack should be installed."
    );
    assert_eq!(
        skill_install_tool["limitations"][1],
        "When path is omitted, the CLI writes the self-contained spirectl skill under ./.agents/skills/ in the current working directory."
    );

    let scene_search_tool = tools
        .iter()
        .find(|tool| tool["name"] == "code_scene_search")
        .expect("code_scene_search tool");
    assert_eq!(
        scene_search_tool["summary"],
        "Search static Godot text, binary, and packed scenes, nodes, and resources without using the runtime bridge."
    );
    assert_eq!(
        scene_search_tool["inputSchema"]["properties"]["query"]["description"],
        "Scene, node, or resource search query for static Godot text, binary, and packed assets."
    );

    let scene_tree_tool = tools
        .iter()
        .find(|tool| tool["name"] == "code_scene_tree")
        .expect("code_scene_tree tool");
    assert_eq!(
        scene_tree_tool["summary"],
        "Show one exact static Godot scene tree from supported text, binary, or packed scene assets."
    );
    assert_eq!(
        scene_tree_tool["limitations"][0],
        "Static scene inspection covers supported text, binary, and packed assets only; live runtime trees remain a separate dev surface."
    );

    let scene_node_tool = tools
        .iter()
        .find(|tool| tool["name"] == "code_scene_node")
        .expect("code_scene_node tool");
    assert_eq!(
        scene_node_tool["limitations"][0],
        "Static scene inspection covers supported text, binary, and packed assets only; live runtime node observation remains a separate dev surface."
    );
}

#[test]
fn inspect_examples_json_includes_test_run_artifact_examples() {
    let response = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "test run",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let examples = payload["examples"].as_array().expect("examples array");

    assert!(examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("--artifacts-dir")
    }));
    assert!(examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("--failure-artifacts never")
    }));
}

#[test]
fn inspect_commands_json_includes_static_navigation_commands() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");

    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code refs")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code derived")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code hooks")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code hook-info")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code scene-search")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code scene-tree")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "code scene-node")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "dev scene tree")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "dev scene node")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "dev scene children")
    );
    assert!(
        commands
            .iter()
            .any(|command| command["name"] == "dev scene set-visible")
    );

    let scene_search_command = commands
        .iter()
        .find(|command| command["name"] == "code scene-search")
        .expect("code scene-search command");
    assert_eq!(
        scene_search_command["summary"],
        "Search static Godot text, binary, and packed scenes, nodes, and resources without using the runtime bridge."
    );

    let scene_tree_command = commands
        .iter()
        .find(|command| command["name"] == "code scene-tree")
        .expect("code scene-tree command");
    assert_eq!(
        scene_tree_command["summary"],
        "Show one exact static Godot scene tree from supported text, binary, or packed scene assets."
    );

    let hooks_command = commands
        .iter()
        .find(|command| command["name"] == "code hooks")
        .expect("code hooks command");
    assert_eq!(
        hooks_command["summary"],
        "List static managed hook candidates with pagination, filters, script hints, and staged follow-up ids."
    );
}

#[test]
fn inspect_commands_json_includes_stable_harmony_scaffold() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands");
    let command = commands
        .iter()
        .find(|command| command["name"] == "project scaffold stable-harmony-trampoline")
        .expect("scaffold command");

    assert_eq!(command["status"], "implemented");
    assert_eq!(command["readOnly"], false);
    assert!(
        command["summary"]
            .as_str()
            .expect("summary")
            .contains("stable Harmony trampoline")
    );
}

#[test]
fn inspect_examples_json_includes_stable_harmony_scaffold_loop() {
    let response = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "project scaffold stable-harmony-trampoline",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let examples = payload["examples"].as_array().expect("examples");
    assert!(examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("sts2 project scaffold stable-harmony-trampoline")
    }));
}

#[test]
fn inspect_commands_json_marks_choose_as_fallback() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");
    let choose_command = commands
        .iter()
        .find(|command| command["name"] == "act choose")
        .expect("act choose command");

    assert!(
        choose_command["summary"]
            .as_str()
            .expect("summary")
            .contains("Fallback-only")
    );
    assert!(choose_command["examples"]
        .as_array()
        .expect("examples")
        .iter()
        .any(|example| example == "sts2 act choose --choice event-room:fake-merchant:open-shop"));
    assert!(
        !choose_command["examples"]
            .as_array()
            .expect("examples")
            .iter()
            .any(|example| example == "sts2 act choose --choice menu:start-run")
    );
}

#[test]
fn inspect_commands_json_select_character_mentions_load_run_lobbies() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");
    let select_character_command = commands
        .iter()
        .find(|command| command["name"] == "act select-character")
        .expect("act select-character command");

    assert_eq!(
        select_character_command["summary"],
        "Select an executable multiplayer lobby character by stable character id in start-run or load-run lobbies."
    );
}

#[test]
fn inspect_commands_json_use_potion_mentions_combat_potion_ids() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");
    let use_potion_command = commands
        .iter()
        .find(|command| command["name"] == "act use-potion")
        .expect("act use-potion command");

    assert_eq!(
        use_potion_command["summary"],
        "Use an executable combat potion by stable potion id, with an optional target id when required."
    );
}

#[test]
fn inspect_examples_json_includes_code_navigation_examples() {
    let response = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "code refs",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let examples = payload["examples"].as_array().expect("examples array");

    assert!(examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("code refs")
    }));

    let hook_examples = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "code hooks",
    ]);
    let hook_payload: Value = serde_json::from_str(&hook_examples.stdout).expect("json");
    let hook_examples = hook_payload["examples"]
        .as_array()
        .expect("hook examples array");

    assert!(hook_examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("code hooks")
    }));

    let scene_examples = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "code scene-tree",
    ]);
    let scene_payload: Value = serde_json::from_str(&scene_examples.stdout).expect("json");
    let scene_examples = scene_payload["examples"]
        .as_array()
        .expect("scene examples array");

    assert!(scene_examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("code scene-tree")
    }));
}

#[test]
fn inspect_examples_json_marks_choose_as_fallback() {
    let response = run(&[
        "sts2",
        "--json",
        "inspect",
        "examples",
        "--command",
        "act choose",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let examples = payload["examples"].as_array().expect("examples array");

    assert!(examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("event-room:fake-merchant:open-shop")
    }));
    assert!(!examples.iter().any(|example| {
        example["example"]
            .as_str()
            .expect("example")
            .contains("menu:start-run")
    }));
}
