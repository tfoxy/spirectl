use super::*;

#[test]
fn inspect_reference_topics_catalog_matches() {
    let response = run(&["sts2", "--json", "inspect", "reference-topics"]);
    let expected = read_repo_doc("docs/reference-topics.json");
    assert_eq!(response.stdout.trim_end(), expected.trim_end());
}

#[test]
fn inspect_actions_snapshot_matches() {
    let response = run(&["sts2", "--json", "inspect", "actions"]);
    assert_snapshot(&response.stdout, "inspect-actions.json");
}

#[cfg(unix)]
#[test]
fn inspect_actions_refused_repo_local_ipc_reports_environment_blocked() {
    let temp_dir = tempfile::tempdir().expect("temp dir");
    let socket_path = temp_dir.path().join("stale.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind stale socket");
    drop(listener);
    let config = write_raw_config(&format!(
        "transport:\n  kind: ipc\n  ipcPath: {}\n",
        socket_path.display()
    ));
    let config_path = config.path().to_str().expect("utf8 config");

    let response = run(&[
        "sts2",
        "--config",
        config_path,
        "--json",
        "inspect",
        "actions",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["source"], "cli");
    assert_eq!(payload["screen"], Value::Null);
    assert_eq!(payload["availableActions"], Value::Array(Vec::new()));
    assert!(
        !payload["supportedActions"]
            .as_array()
            .expect("actions")
            .is_empty()
    );
    assert_eq!(payload["notices"][0]["code"], "environment_blocked");
    assert_eq!(payload["environment"]["code"], "environment_blocked");
    assert_eq!(
        payload["environment"]["status"],
        "endpoint_refused_or_stale"
    );
    assert_eq!(
        payload["environment"]["connection"]["bridgeErrorCode"],
        "ipc_connection_failed"
    );
    assert_eq!(payload["environment"]["endpoint"]["kind"], "unix-socket");
    assert_eq!(
        payload["environment"]["endpoint"]["socketPath"],
        socket_path.display().to_string()
    );
    assert_eq!(payload["environment"]["endpoint"]["exists"], true);
    assert_eq!(payload["environment"]["endpoint"]["fileKind"], "socket");
    assert!(
        payload["environment"]["safeNextCommands"]
            .as_array()
            .expect("safe next commands")
            .iter()
            .any(|command| command == "sts2 game bridge-health --repair-stale-endpoint")
    );
    assert!(
        payload["environment"]["safeNextCommands"]
            .as_array()
            .expect("safe next commands")
            .iter()
            .any(|command| command == "sts2 game attach")
    );
    assert!(
        payload["environment"]["safeNextCommands"]
            .as_array()
            .expect("safe next commands")
            .iter()
            .any(|command| command == "sts2 game launch")
    );
}

#[test]
fn inspect_viewport_presets_json_exposes_builtin_catalog() {
    let response = run(&["sts2", "--json", "inspect", "viewport-presets"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let presets = payload["presets"].as_array().expect("presets array");
    let desktop = presets
        .iter()
        .find(|preset| preset["name"] == "desktop-1080p")
        .expect("desktop-1080p preset");
    let deck = presets
        .iter()
        .find(|preset| preset["name"] == "steam-deck")
        .expect("steam-deck preset");

    assert_eq!(payload["source"], "command-catalog");
    assert_eq!(desktop["width"], 1920);
    assert_eq!(desktop["height"], 1080);
    assert_eq!(deck["width"], 1280);
    assert_eq!(deck["height"], 800);
}

#[test]
fn inspect_viewport_presets_json_merges_explicit_catalogs_with_source_metadata() {
    let catalog = write_viewport_catalog(
        r#"schemaVersion: spirectl.viewport-presets/v0
presets:
  - name: cinematic-5k
    width: 5120
    height: 2880
    description: Ultra-wide marketing capture
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "inspect",
        "viewport-presets",
        "--preset-catalog",
        catalog.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let presets = payload["presets"].as_array().expect("presets array");
    let builtin = presets
        .iter()
        .find(|preset| preset["name"] == "desktop-1080p")
        .expect("desktop-1080p preset");
    let custom = presets
        .iter()
        .find(|preset| preset["name"] == "cinematic-5k")
        .expect("cinematic-5k preset");

    assert_eq!(builtin["source"]["kind"], "built-in");
    assert_eq!(custom["width"], 5120);
    assert_eq!(custom["height"], 2880);
    assert_eq!(custom["description"], "Ultra-wide marketing capture");
    assert_eq!(custom["source"]["kind"], "catalog");
    assert_eq!(
        custom["source"]["path"],
        catalog
            .path()
            .canonicalize()
            .expect("catalog path")
            .display()
            .to_string()
    );
}

#[test]
fn inspect_viewport_presets_rejects_duplicate_catalog_names() {
    let catalog = write_viewport_catalog(
        r#"schemaVersion: spirectl.viewport-presets/v0
presets:
  - name: desktop-1080p
    width: 1111
    height: 777
"#,
    );

    let response = run_error(&[
        "sts2",
        "--json",
        "inspect",
        "viewport-presets",
        "--preset-catalog",
        catalog.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["error"]["code"], "invalid_query");
    assert_eq!(payload["error"]["path"], "preset-catalog");
    assert!(
        payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("duplicates existing preset 'desktop-1080p'")
    );
}

#[test]
fn inspect_reference_topics_json_exposes_m32_catalog() {
    let response = run(&["sts2", "--json", "inspect", "reference-topics"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let topics = payload["topics"].as_array().expect("topics array");
    let managed = topics
        .iter()
        .find(|topic| topic["id"] == "managed-hook-discovery")
        .expect("managed-hook-discovery topic");
    let runtime = topics
        .iter()
        .find(|topic| topic["id"] == "runtime-evidence-loop")
        .expect("runtime-evidence-loop topic");
    let trampoline = topics
        .iter()
        .find(|topic| topic["id"] == "stable-harmony-trampoline")
        .expect("stable-harmony-trampoline topic");

    assert_eq!(payload["source"], "command-catalog");
    assert_eq!(managed["docPath"], "docs/modding-reference.md");
    assert!(
        managed["relatedCommands"]
            .as_array()
            .expect("relatedCommands")
            .iter()
            .any(|command| command == "code hooks")
    );
    assert!(
        runtime["exampleSequence"]
            .as_array()
            .expect("exampleSequence")
            .iter()
            .any(|step| step == "sts2 dev diagnostics --bundle-dir ./.sts2/artifacts/modding")
    );
    assert_eq!(trampoline["docPath"], "docs/modding-reference.md");
    assert!(
        trampoline["limits"]
            .as_array()
            .expect("limits")
            .iter()
            .any(|limit| limit.as_str().expect("limit").contains("startup-bound"))
    );
    assert!(
        trampoline["relatedCommands"]
            .as_array()
            .expect("relatedCommands")
            .iter()
            .any(|command| command == "code hook-info")
    );
}

#[test]
fn inspect_actions_dangerous_mode_includes_mouse_click_supported_action() {
    let response = run(&[
        "sts2",
        "--json",
        "--mode",
        "dangerous",
        "inspect",
        "actions",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    let supported_actions = payload["supportedActions"]
        .as_array()
        .expect("supportedActions array");
    let mouse_click = supported_actions
        .iter()
        .find(|action| action["id"] == "mouse-click")
        .expect("mouse-click supported action");

    assert_eq!(mouse_click["kind"], "mouse-click");
    assert_eq!(mouse_click["provisional"], true);
    assert_eq!(mouse_click["parameters"][0]["name"], "x");
    assert_eq!(mouse_click["parameters"][1]["name"], "y");
    assert_eq!(mouse_click["parameters"][2]["name"], "button");
}

#[test]
fn inspect_ai_tools_snapshot_matches() {
    let response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let tools = payload["tools"].as_array().expect("tools array");
    let names = tools
        .iter()
        .map(|tool| tool["name"].as_str().expect("tool name"))
        .collect::<Vec<_>>();
    assert!(names.contains(&"test_run"), "test_run should be AI-visible");
    for omitted in [
        "mouse-click",
        "game kill",
        "dev scene tree",
        "dev scene node",
        "dev scene children",
        "dev scene resource",
        "dev scene localization",
        "dev scene synthetic-layout",
        "dev scene set-visible",
        "dev scene hover",
        "render snapshot",
        "render contract",
        "render preflight",
        "presentation contract",
        "presentation preflight",
    ] {
        assert!(
            !names.contains(&omitted),
            "{omitted} should stay outside the AI tool catalog"
        );
    }
    for removed in [
        "render snapshot",
        "render contract",
        "render preflight",
        "presentation contract",
        "presentation preflight",
        "presentation bundle",
    ] {
        assert!(
            !response.stdout.contains(removed),
            "{removed} should not appear in AI metadata"
        );
    }
    let scenario_load = tools
        .iter()
        .find(|tool| tool["name"] == "scenario_load")
        .expect("scenario_load tool");
    let output_paths = scenario_load["outputShape"]["primaryFields"]
        .as_array()
        .expect("primaryFields")
        .iter()
        .map(|field| field["path"].as_str().expect("field path"))
        .collect::<Vec<_>>();
    for path in [
        "validation.mismatches",
        "validation.mismatches[].supportClass",
        "validation.mismatches[].reasonCode",
        "validation.mismatches[].suggestedNextStep",
    ] {
        assert!(output_paths.contains(&path), "missing {path}");
    }
    assert_snapshot(&response.stdout, "inspect-ai-tools.json");
}

#[test]
fn inspect_ai_tools_promotes_m33_locked_set_with_honest_statuses() {
    let response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let tools = payload["tools"].as_array().expect("tools array");

    let find = |name: &str| {
        tools
            .iter()
            .find(|tool| tool["name"] == name)
            .unwrap_or_else(|| panic!("expected AI tool catalog entry for {name}"))
    };

    assert_eq!(find("toolchain_info")["status"], "stable");
    assert_eq!(find("project_profile_list")["status"], "stable");
    assert_eq!(find("project_profile_show")["status"], "stable");
    assert_eq!(find("project_profile_run")["status"], "partial");
    assert_eq!(find("log_health")["status"], "stable");
    assert_eq!(find("diagnostics")["status"], "stable");
    assert_eq!(find("http")["status"], "partial");
    assert_eq!(find("http_wait")["status"], "partial");
    assert_eq!(find("fetch")["status"], "partial");
    assert_eq!(find("websocket")["status"], "partial");
    assert_eq!(find("project_hook_list")["status"], "stable");
    assert_eq!(find("project_hook_show")["status"], "stable");
    assert_eq!(find("project_hook_run")["status"], "partial");
    assert_eq!(find("inspect_viewport_presets")["status"], "stable");
    assert_eq!(find("screenshot_diff")["status"], "stable");
    assert_eq!(find("debug_status")["status"], "partial");
    assert_eq!(find("debug_pause")["status"], "partial");
    assert_eq!(find("debug_resume")["status"], "partial");
    assert_eq!(find("debug_step")["status"], "partial");
    assert_eq!(find("breakpoint_list")["status"], "partial");
    assert_eq!(find("breakpoint_add")["status"], "partial");
    assert_eq!(find("breakpoint_remove")["status"], "partial");
    assert_eq!(find("code_hooks")["status"], "stable");
    assert_eq!(find("code_hook_info")["status"], "stable");
    assert_eq!(find("inspect_reference_topics")["status"], "stable");

    assert!(
        tools.iter().all(|tool| tool["name"] != "project_recover"),
        "project_recover should stay outside the AI tool catalog"
    );
}

#[test]
fn inspect_commands_reports_scenario_and_recorded_fixture_implemented() {
    let response = run(&["sts2", "--json", "inspect", "commands"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let commands = payload["commands"].as_array().expect("commands array");
    let names = commands
        .iter()
        .map(|command| command["name"].as_str().expect("command name"))
        .collect::<Vec<_>>();

    for name in ["dev scenario export", "dev scenario load"] {
        assert!(names.contains(&name), "missing {name}");
    }
    for name in [
        "dev fixture record",
        "dev fixture resume",
        "dev fixture status",
        "dev fixture clear",
        "dev fixture load",
    ] {
        let command = commands
            .iter()
            .find(|command| command["name"] == name)
            .unwrap_or_else(|| panic!("missing {name}"));
        assert_eq!(command["status"], "implemented");
    }
    for removed in [
        "dev checkpoint capture",
        "dev checkpoint resume",
        "dev checkpoint list",
        "dev checkpoint delete",
        "dev load-fixture",
    ] {
        assert!(!names.contains(&removed), "{removed} should not be public");
    }
}

#[test]
fn inspect_ai_tools_promotes_scenario_tools_but_omits_checkpoints() {
    let response = run(&["sts2", "--json", "inspect", "ai-tools"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let tools = payload["tools"].as_array().expect("tools array");

    for name in ["scenario_export", "scenario_load"] {
        let tool = tools
            .iter()
            .find(|tool| tool["name"] == name)
            .unwrap_or_else(|| panic!("expected AI tool catalog entry for {name}"));
        assert_eq!(tool["status"], "partial");
        assert_eq!(tool["readOnly"], false);
    }

    let scenario_export = tools
        .iter()
        .find(|tool| tool["name"] == "scenario_export")
        .expect("scenario_export tool");
    let export_output_paths = scenario_export["outputShape"]["primaryFields"]
        .as_array()
        .expect("primaryFields")
        .iter()
        .map(|field| field["path"].as_str().expect("field path"))
        .collect::<Vec<_>>();
    for path in [
        "restoreSupport",
        "restoreSupport.fields[].supportClass",
        "restoreSupport.fields[].reasonCode",
        "restoreSupport.fields[].suggestedNextStep",
    ] {
        assert!(export_output_paths.contains(&path), "missing {path}");
    }

    for name in [
        "fixture_record",
        "fixture_resume",
        "fixture_status",
        "fixture_clear",
        "checkpoint_capture",
        "checkpoint_resume",
        "checkpoint_list",
        "checkpoint_delete",
    ] {
        assert!(
            tools.iter().all(|tool| tool["name"] != name),
            "{name} should stay outside the AI tool catalog"
        );
    }
}

#[test]
fn dev_checkpoint_command_is_not_in_active_parser() {
    let result = Cli::try_parse_from(["sts2", "dev", "checkpoint", "list"]);
    assert!(result.is_err());
    let message = result.unwrap_err().to_string();
    assert!(message.contains("unrecognized subcommand") || message.contains("invalid subcommand"));
}

#[test]
fn state_main_menu_returns_runtime_envelope() {
    let response = run(&["sts2", "--json", "state"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    assert_eq!(payload["language"], "mock");
    assert_eq!(payload["schemaVersion"], "spirectl.state/v0");
    assert_eq!(payload["rootScene"], "screens/main_menu");
}
#[test]
fn state_watch_max_events_one_emits_initial_compact_event() {
    let config = write_raw_config("transport:\n  kind: mock\n");
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "state",
        "--watch",
        "--max-events",
        "1",
        "--poll-interval-ms",
        "0",
    ]);
    let mut output = Vec::new();

    let exit_code = run_cli_streaming(cli, &mut output).expect("watch should stream");
    let output = String::from_utf8(output).expect("utf8");
    let lines = output.lines().collect::<Vec<_>>();
    assert_eq!(exit_code, 0);
    assert_eq!(lines.len(), 1);

    let event: Value = serde_json::from_str(lines[0]).expect("json event");
    assert_eq!(event["type"], "initial");
    assert_eq!(event["sequence"], 1);
    assert!(event["observedAtUtc"].as_str().is_some());
    assert!(event["fingerprint"].as_str().is_some());
    assert_eq!(event["state"]["schemaVersion"], "spirectl.state/v0");
}

#[test]
fn state_watch_suppresses_unchanged_state_until_timeout() {
    let config = write_raw_config("transport:\n  kind: mock\n");
    let cli = Cli::parse_from([
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "state",
        "--watch",
        "--timeout-ms",
        "1",
        "--poll-interval-ms",
        "0",
    ]);
    let mut output = Vec::new();

    run_cli_streaming(cli, &mut output).expect("watch should stream");
    let output = String::from_utf8(output).expect("utf8");
    let lines = output.lines().collect::<Vec<_>>();
    assert_eq!(lines.len(), 1);
    let event: Value = serde_json::from_str(lines[0]).expect("json event");
    assert_eq!(event["type"], "initial");
}
