use super::*;

fn discovered(path: &Path) -> DiscoveredScenario {
    DiscoveredScenario {
        source: ScenarioSource::File(path.to_path_buf()),
        display_path: path.display().to_string(),
    }
}

fn discovered_inline(raw: &str) -> DiscoveredScenario {
    DiscoveredScenario {
        source: ScenarioSource::Inline(raw.to_string()),
        display_path: "<inline>".to_string(),
    }
}

#[test]
fn recoverable_live_bridge_error_matches_transport_failures() {
    let error = json!({
        "error": {
            "code": "runtime_failure",
            "message": "The live IPC bridge returned an invalid standalone transport response.",
            "details": [
                {
                    "note": "refused follow-up artifact collection"
                }
            ]
        }
    });

    assert!(recoverable_live_bridge_error(&error));
}

#[test]
fn recoverable_live_bridge_error_ignores_product_assertions() {
    let error = runner_error(
        "assertion_failed",
        "Expected the asset cache to contain the requested font.",
    );

    assert!(!recoverable_live_bridge_error(&error));
}

#[test]
fn parse_step_normalizes_legacy_aliases() {
    let step: RawStep = serde_yaml::from_str(
        r#"
assert.query:
  path: screen.id
  equals: main-menu
"#,
    )
    .expect("yaml step");

    let parsed = parse_step(step).expect("parse step");
    assert_eq!(parsed.requested_id, "assert.query");
    assert_eq!(parsed.canonical_id, "dev.assert");
}

#[test]
fn parse_step_accepts_bare_no_arg_forms() {
    let step: RawStep = serde_yaml::from_str("game.info").expect("yaml step");
    let parsed = parse_step(step).expect("parse step");

    assert_eq!(parsed.canonical_id, "game.info");
    assert_eq!(parsed.input, Value::Null);
}

#[test]
fn parse_step_accepts_non_combat_intent_actions() {
    for (raw, expected) in [
        (
            "act.claim-reward: { reward: reward:p1:0 }",
            "act.claim-reward",
        ),
        (
            "act.select-card: { card: card-selection:card:bash:0 }",
            "act.select-card",
        ),
        (
            "act.select-bundle: { bundle: card-selection:bundle:offensive-pack:0 }",
            "act.select-bundle",
        ),
        (
            "act.buy-card: { shopItem: shop:p1:card:strike:0 }",
            "act.buy-card",
        ),
        ("act.rest: {}", "act.rest"),
        (
            "act.use-rest-site-option: { restOption: heal }",
            "act.use-rest-site-option",
        ),
        ("act.open-chest: {}", "act.open-chest"),
        ("act.take-relic: { relic: anchor }", "act.take-relic"),
        ("act.back-from-map: {}", "act.back-from-map"),
        (
            "act.select-event-option: { eventOption: event-room:gain-gold:0 }",
            "act.select-event-option",
        ),
        (
            "act.use-crystal-sphere-control: { control: crystal-sphere:tool:big }",
            "act.use-crystal-sphere-control",
        ),
        ("act.proceed-event: {}", "act.proceed-event"),
    ] {
        let step: RawStep = serde_yaml::from_str(raw).expect("yaml step");
        let parsed = parse_step(step).expect("parse step");
        assert_eq!(parsed.canonical_id, expected);
    }
}

#[test]
fn parse_step_accepts_game_deploy_steps() {
    let step: RawStep = serde_yaml::from_str(
        r#"
game.deploy:
  path: ./mods/MyMod
  build: true
  restart: true
  verify: true
  timeoutMs: 500
  intervalMs: 5
"#,
    )
    .expect("yaml step");
    let parsed = parse_step(step).expect("parse step");

    assert_eq!(parsed.canonical_id, "game.deploy");
    assert_eq!(parsed.input["path"], "./mods/MyMod");
    assert_eq!(parsed.input["build"], true);
    assert_eq!(parsed.input["timeoutMs"], 500);
    assert_eq!(parsed.input["intervalMs"], 5);
}

#[test]
fn parse_step_accepts_dev_delay_steps() {
    let step: RawStep = serde_yaml::from_str(
        r#"
dev.delay:
  ms: 1250
"#,
    )
    .expect("yaml step");
    let parsed = parse_step(step).expect("parse step");

    assert_eq!(parsed.canonical_id, "dev.delay");
    assert_eq!(parsed.input["ms"], 1250);
}

#[test]
fn parse_step_accepts_debug_events_steps() {
    let step: RawStep = serde_yaml::from_str(
        r#"
dev.debug.events:
  session: dbg:observer
  fromSequence: 2
  limit: 20
  follow: true
  timeoutMs: 250
  expectEventKind: observer-attached
"#,
    )
    .expect("yaml step");
    let parsed = parse_step(step).expect("parse step");

    assert_eq!(parsed.requested_id, "dev.debug.events");
    assert_eq!(parsed.canonical_id, "dev.debug-events");
    assert_eq!(parsed.input["session"], "dbg:observer");
    assert_eq!(parsed.input["fromSequence"], 2);
    assert_eq!(parsed.input["limit"], 20);
    assert_eq!(parsed.input["follow"], true);
    assert_eq!(parsed.input["timeoutMs"], 250);
    assert_eq!(parsed.input["expectEventKind"], "observer-attached");
}

#[test]
fn parse_step_accepts_screenshot_steps_with_optional_output() {
    let step: RawStep = serde_yaml::from_str(
        r#"
dev.screenshot:
  output: ./artifacts/combat.png
"#,
    )
    .expect("yaml step");
    let parsed = parse_step(step).expect("parse step");

    assert_eq!(parsed.canonical_id, "dev.screenshot");
    assert_eq!(parsed.input["output"], "./artifacts/combat.png");
}

#[test]
fn load_scenario_accepts_game_deploy_steps() {
    let file = tempfile::Builder::new()
        .suffix(".sts2.yaml")
        .tempfile()
        .expect("scenario file");
    fs::write(
        file.path(),
        "name: unsupported\nsteps:\n  - game.deploy:\n      path: ./mods/MyMod\n",
    )
    .expect("write scenario");

    let scenario = load_scenario(&discovered(file.path())).expect("valid scenario");
    assert_eq!(scenario.steps.len(), 1);
    assert_eq!(scenario.steps[0].canonical_id, "game.deploy");
}

#[test]
fn load_scenario_rejects_state_expect_mini_dsl() {
    let file = tempfile::Builder::new()
        .suffix(".sts2.yaml")
        .tempfile()
        .expect("scenario file");
    fs::write(
        file.path(),
        "name: legacy\nsteps:\n  - state:\n      expect:\n        screen.id: combat\n",
    )
    .expect("write scenario");

    let report = load_scenario(&discovered(file.path())).expect_err("invalid scenario");
    assert_eq!(
        report.steps[0].error.as_ref().expect("error")["code"],
        "unsupported_step"
    );
}

#[test]
fn load_scenario_rejects_invalid_regex_predicates() {
    let file = tempfile::Builder::new()
        .suffix(".sts2.yaml")
        .tempfile()
        .expect("scenario file");
    fs::write(
            file.path(),
            "name: bad-regex\nsteps:\n  - dev.assert:\n      path: screen.id\n      regex: \"[unterminated\"\n",
        )
        .expect("write scenario");

    let report = load_scenario(&discovered(file.path())).expect_err("invalid scenario");
    assert_eq!(
        report.steps[0].error.as_ref().expect("error")["code"],
        "invalid_step_args"
    );
}

#[test]
fn load_scenario_reports_yaml_parse_failures() {
    let file = tempfile::Builder::new()
        .suffix(".sts2.yaml")
        .tempfile()
        .expect("scenario file");
    fs::write(file.path(), "name: bad\nsteps:\n  - dev.assert: [\n").expect("write scenario");

    let report = load_scenario(&discovered(file.path())).expect_err("invalid scenario");
    assert_eq!(
        report.error.as_ref().expect("error")["code"],
        "scenario_parse_failed"
    );
}

#[test]
fn load_inline_scenario_reports_yaml_parse_failures() {
    let report = load_scenario(&discovered_inline("name: bad\nsteps:\n  - dev.assert: [\n"))
        .expect_err("invalid scenario");

    assert_eq!(report.path, "<inline>");
    assert_eq!(report.name, "<inline>");
    assert_eq!(
        report.error.as_ref().expect("error")["code"],
        "scenario_parse_failed"
    );
}

#[test]
fn profile_cleanup_collects_launch_pids_from_nested_preflight() {
    let preflight = json!({
        "profile": "live",
        "steps": {
            "deploy": {
                "restart": {
                    "launch": {
                        "launch": {
                            "pid": 1234
                        }
                    }
                }
            },
            "launch": {
                "launch": {
                    "pid": 5678
                }
            }
        }
    });
    let mut pids = Vec::new();

    collect_launch_pids(&preflight, &mut pids);
    pids.sort_unstable();

    assert_eq!(pids, vec![1234, 5678]);
}
