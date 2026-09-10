use super::*;

#[test]
fn test_run_executes_single_scenario_successfully() {
    let scenario = write_scenario(
        r#"
name: smoke-main-menu
steps:
  - game.info
  - dev.assert:
      path: rootScene
      equals: screens/main_menu
  - act.fallback-choose:
      choice: menu:start-run
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarioCount"], 1);
    assert_eq!(payload["passedScenarioCount"], 1);
    assert_eq!(payload["scenarios"][0]["name"], "smoke-main-menu");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][1]["canonicalId"],
        "dev.assert"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][2]["canonicalId"],
        "act.fallback-choose"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][2]["input"]["fallback"],
        true
    );
}

#[test]
fn test_run_profile_uses_profile_paths_config_and_tags() {
    let workspace = tempfile::tempdir().expect("workspace");
    let scenario_dir = workspace.path().join("scenarios");
    fs::create_dir_all(&scenario_dir).expect("scenario dir");
    fs::write(
        scenario_dir.join("combat.sts2.yaml"),
        r#"
name: combat-profile
tags: [profiled]
steps:
  - dev.assert:
      path: rootScene
      equals: run
"#,
    )
    .expect("write matching scenario");
    fs::write(
        scenario_dir.join("main-menu.sts2.yaml"),
        r#"
name: main-menu-profile
tags: [other]
steps:
  - dev.assert:
      path: rootScene
      equals: screens/main_menu
"#,
    )
    .expect("write nonmatching scenario");
    let config = write_raw_config(&format!(
        r#"
test:
  profiles:
    profiled:
      config:
        transport:
          kind: mock
          mockScenario: combat
      paths:
        - {}
      includeTags:
        - profiled
"#,
        scenario_dir.display()
    ));

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "profiled",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["profile"]["name"], "profiled");
    assert_eq!(payload["scenarioCount"], 1);
    assert_eq!(payload["scenarios"][0]["name"], "combat-profile");
    assert_eq!(payload["scenarios"][0]["status"], "passed");
}

#[test]
fn test_run_profile_path_narrows_profile_but_keeps_filters() {
    let matching = write_scenario(
        r#"
name: narrowed-profile
tags: [liveValidation]
steps:
  - game.info
"#,
    );
    let nonmatching = write_scenario(
        r#"
name: narrowed-profile-nonmatching
tags: [other]
steps:
  - game.info
"#,
    );
    let config = write_raw_config(
        r#"
transport:
  kind: mock
test:
  profiles:
    live-ish:
      paths:
        - /unused/default/path
      includeTags:
        - liveValidation
      gate:
        requireMatchingScenario: true
"#,
    );

    let passing = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "live-ish",
        matching.path().to_str().expect("utf8 path"),
    ]);
    let passing_payload: Value = serde_json::from_str(&passing.stdout).expect("json");
    assert_eq!(passing.exit_code, 0);
    assert_eq!(passing_payload["scenarioCount"], 1);
    assert_eq!(passing_payload["scenarios"][0]["name"], "narrowed-profile");

    let blocked = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "live-ish",
        nonmatching.path().to_str().expect("utf8 path"),
    ]);
    let blocked_payload: Value = serde_json::from_str(&blocked.stdout).expect("json");
    assert_eq!(blocked.exit_code, 2);
    assert_eq!(
        blocked_payload["errors"][0]["code"],
        "test_profile_no_matching_scenarios"
    );
}

#[test]
fn test_run_profile_deploy_preflight_requires_path() {
    let scenario = write_scenario(
        r#"
name: preflight-deploy-missing-path
tags: [liveValidation]
steps:
  - game.info
"#,
    );
    let config = write_raw_config(&format!(
        r#"
test:
  profiles:
    live-ish:
      paths:
        - {}
      includeTags:
        - liveValidation
      preflight:
        deploy:
          build: true
      gate:
        requireMatchingScenario: true
"#,
        scenario.path().display(),
    ));

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "live-ish",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "invalid");
    assert_eq!(payload["scenarioCount"], 0);
    assert_eq!(
        payload["errors"][0]["code"],
        "test_profile_preflight_failed"
    );
    assert_eq!(payload["errors"][0]["command"], "game deploy");
    assert_eq!(
        payload["errors"][0]["error"]["code"],
        "invalid_test_profile"
    );
}

#[test]
fn test_run_profile_reports_unknown_profile() {
    let config = write_raw_config(
        r#"
test:
  profiles:
    mock:
      paths:
        - tests/scenarios
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "missing",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["errors"][0]["code"], "unknown_test_profile");
}

#[test]
fn test_run_profile_rejects_mock_transport_when_live_transport_required() {
    let config = write_raw_config(
        r#"
transport:
  kind: mock
test:
  profiles:
    live:
      paths:
        - tests/scenarios
      gate:
        requireLiveTransport: true
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "live",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(
        payload["errors"][0]["code"],
        "test_profile_requires_live_transport"
    );
}

#[test]
fn test_run_profile_allows_explicit_not_applicable_when_gate_allows_it() {
    let scenario = write_scenario(
        r#"
name: docs-only
tags: [spec:S99]
liveValidation:
  status: notApplicable
  reason: "Docs-only spec."
steps:
  - game.info
"#,
    );
    let config = write_raw_config(&format!(
        r#"
transport:
  kind: mock
test:
  profiles:
    live:
      paths:
        - {}
      includeTags:
        - liveValidation
      gate:
        requireMatchingScenario: true
        allowNotApplicable: true
"#,
        scenario.path().display()
    ));

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--profile",
        "live",
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["scenarioCount"], 0);
}

#[test]
fn test_run_reports_explicit_not_applicable_without_profile() {
    let scenario = write_scenario(
        r#"
name: docs-only
liveValidation:
  status: notApplicable
  reason: "Docs-only scenario."
steps:
  - game.info
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["status"], "invalid");
    assert_eq!(payload["errors"][0]["code"], "no_applicable_scenarios");
    assert_eq!(
        payload["errors"][0]["skippedScenarios"][0]["name"],
        "docs-only"
    );
    assert_eq!(
        payload["errors"][0]["skippedScenarios"][0]["reason"],
        "Docs-only scenario."
    );
}
