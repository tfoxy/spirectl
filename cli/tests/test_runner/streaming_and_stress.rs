use super::*;

#[test]
fn test_run_executes_offline_screenshot_diff_with_scenario_relative_paths() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let workspace = tempfile::tempdir().expect("workspace");
    let baseline = workspace.path().join("baseline.png");
    let actual = workspace.path().join("actual.png");
    write_png(&baseline, [10, 20, 30, 255]);
    write_png(&actual, [10, 20, 30, 255]);

    let scenario = write_scenario(
        r#"
name: offline-screenshot-diff
steps:
  - dev.screenshot-diff:
      baseline: ./baseline.png
      actual: ./actual.png
"#,
    );
    let scenario_path = workspace.path().join("offline.sts2.yaml");
    fs::copy(scenario.path(), &scenario_path).expect("copy scenario");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario_path.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let step_dir = scenario_dir.join("steps").join("001-dev-screenshot-diff");

    assert_eq!(response.exit_code, 0);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["matched"],
        true
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["actual"]["path"],
        actual.display().to_string()
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["requestedViewport"],
        Value::Null
    );
    assert!(step_dir.join("comparison.json").exists());
    assert!(step_dir.join("baseline.png").exists());
    assert!(step_dir.join("actual.png").exists());
    assert!(step_dir.join("diff.png").exists());
}

#[test]
fn test_run_executes_screenshot_diff_step_with_mask_and_regions() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::Combat);
    let workspace = tempfile::tempdir().expect("workspace");
    let baseline = workspace.path().join("baseline.png");
    let actual = workspace.path().join("actual.png");
    let mask = workspace.path().join("mask.png");
    let regions = workspace.path().join("regions.json");

    write_png(&baseline, [10, 20, 30, 255]);
    write_png(&actual, [10, 20, 30, 255]);
    let mut mask_image = image::RgbaImage::from_pixel(2, 2, image::Rgba([0, 0, 0, 0]));
    mask_image.put_pixel(0, 0, image::Rgba([255, 255, 255, 255]));
    mask_image.save(&mask).expect("write mask png");
    fs::write(
        &regions,
        serde_json::to_vec_pretty(&json!({
            "regions": [
                { "id": "selected-character-panel", "x": 0, "y": 0, "width": 1, "height": 1 }
            ]
        }))
        .expect("regions json"),
    )
    .expect("write regions");

    let scenario = write_scenario(
        r#"
name: screenshot-diff-mask-roi
steps:
  - dev.screenshot-diff:
      baseline: ./baseline.png
      actual: ./actual.png
      mask: ./mask.png
      regions: ./regions.json
      maxDiffRatio: 0.01
      foregroundMaxDiffRatio: 0
      roiMaxDiffRatio: 0
      requiredComparisons:
        - foreground
        - roi
"#,
    );
    let scenario_path = workspace.path().join("mask-roi.sts2.yaml");
    fs::copy(scenario.path(), &scenario_path).expect("copy scenario");

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario_path.to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let scenario_payload = &payload["scenarios"][0];
    let step = &scenario_payload["steps"][0];
    let scenario_dir = PathBuf::from(
        scenario_payload["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let step_dir = scenario_dir.join("steps").join("001-dev-screenshot-diff");
    let comparison_path = step_dir.join("comparison.json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(scenario_payload["status"], "passed");
    assert_eq!(step["canonicalId"], "dev.screenshot-diff");
    assert_eq!(step["output"]["matched"], true);
    assert_eq!(step["output"]["comparisons"]["full"]["matched"], true);
    assert_eq!(step["output"]["comparisons"]["foreground"]["matched"], true);
    assert_eq!(step["output"]["comparisons"]["roi"]["matched"], true);
    assert_eq!(
        step["output"]["bundle"]["files"]["comparison"],
        comparison_path.display().to_string()
    );
    assert_eq!(
        step["output"]["bundle"]["files"]["foregroundDiff"],
        step_dir.join("foreground-diff.png").display().to_string()
    );
    assert_eq!(
        step["output"]["bundle"]["files"]["roiDiff"],
        step_dir.join("roi-diff.png").display().to_string()
    );

    assert!(comparison_path.exists());
    assert!(step_dir.join("baseline.png").exists());
    assert!(step_dir.join("actual.png").exists());
    assert!(step_dir.join("diff.png").exists());
    assert!(step_dir.join("foreground-diff.png").exists());
    assert!(step_dir.join("roi-diff.png").exists());

    let comparison = read_json_file(&comparison_path);
    assert_eq!(comparison["comparisons"]["full"]["matched"], true);
    assert_eq!(comparison["comparisons"]["foreground"]["matched"], true);
    assert_eq!(comparison["comparisons"]["roi"]["matched"], true);
    assert_eq!(comparison["comparisons"]["foreground"]["diffRatio"], 0.0);
    assert_eq!(comparison["comparisons"]["roi"]["diffRatio"], 0.0);
}

#[test]
fn test_run_executes_snapshot_compare_step_and_persists_bundle() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let workspace = tempfile::tempdir().expect("workspace");
    let snapshots_dir = workspace.path().join("snapshots");
    let baseline_dir = snapshots_dir.join("baselines/main-menu");
    let spec_path = snapshots_dir.join("main-menu.sts2.snapshot.yaml");
    fs::create_dir_all(&baseline_dir).expect("baseline dir");
    fs::write(
        &spec_path,
        r#"schemaVersion: spirectl.snapshot/v0
name: main-menu
state:
  selectors:
    - screen.id
    - screen.title
actions:
  includeAvailableActionIds: true
screenshot:
  preset: desktop-1080p
"#,
    )
    .expect("write spec");

    let _ = run_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "dev",
            "snapshot",
            "export",
            "--spec",
            "snapshots/main-menu.sts2.snapshot.yaml",
            "--output",
            "snapshots/baselines/main-menu",
        ],
        &[],
    );

    let scenario_path = workspace.path().join("snapshot-compare.sts2.yaml");
    fs::write(
        &scenario_path,
        r#"
name: snapshot-compare
steps:
  - dev.snapshot-compare:
      spec: ./snapshots/main-menu.sts2.snapshot.yaml
      baseline: ./snapshots/baselines/main-menu
"#,
    )
    .expect("write scenario");

    let response = run_json_in_dir_with_env(
        workspace.path(),
        &[
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            scenario_path.to_str().expect("utf8 path"),
        ],
        &[],
    );
    let scenario_dir = PathBuf::from(
        response["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );
    let step_dir = scenario_dir.join("steps").join("001-dev-snapshot-compare");

    assert_eq!(response["status"], "passed");
    assert_eq!(
        response["scenarios"][0]["steps"][0]["canonicalId"],
        "dev.snapshot-compare"
    );
    assert_eq!(
        response["scenarios"][0]["steps"][0]["output"]["matched"],
        true
    );
    assert!(step_dir.join("snapshot-compare.json").exists());
    assert!(step_dir.join("state.json").exists());
    assert!(step_dir.join("inspect-actions.json").exists());
    assert!(step_dir.join("comparison.json").exists());
}

#[test]
fn test_stress_repeats_scenario_runs_and_records_iteration_summaries() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: stress-smoke
steps:
  - game.info
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "stress",
        "--artifacts-dir",
        artifacts_dir.path().to_str().expect("utf8 path"),
        "--iterations",
        "2",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let summary_path = PathBuf::from(
        payload["artifacts"]["summaryPath"]
            .as_str()
            .expect("summary path"),
    );

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["status"], "passed");
    assert_eq!(payload["completedIterations"], 2);
    assert_eq!(payload["passedIterations"], 2);
    assert_eq!(payload["failedIterations"], 0);
    assert_eq!(payload["stopReason"], "iterations-complete");
    assert!(summary_path.exists());
    assert_eq!(payload["iterations"][0]["status"], "passed");
    assert!(
        PathBuf::from(
            payload["iterations"][0]["runSummaryPath"]
                .as_str()
                .expect("iteration summary path")
        )
        .exists()
    );
}

#[test]
fn test_stress_stops_after_first_failure_by_default() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: stress-fail
steps:
  - dev.assert:
      path: screen.id
      equals: combat
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "stress",
        "--artifacts-dir",
        artifacts_dir.path().to_str().expect("utf8 path"),
        "--iterations",
        "3",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(payload["status"], "failed");
    assert_eq!(payload["completedIterations"], 1);
    assert_eq!(payload["failedIterations"], 1);
    assert_eq!(payload["stopReason"], "max-failures-reached");
    assert_eq!(payload["firstFailure"]["iteration"], 1);
    assert_eq!(payload["lastFailure"]["iteration"], 1);
}

#[test]
fn test_run_rejects_unsupported_scaffolded_steps() {
    let scenario = write_scenario(
        r#"
name: unsupported-step
steps:
  - game.launch
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
    assert_eq!(payload["scenarios"][0]["steps"][0]["status"], "invalid");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "unsupported_step"
    );
}
#[tokio::test]
async fn test_run_executes_game_deploy_relative_to_scenario_file() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    let scenario_dir = workspace.path().join("scenarios");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    fs::create_dir_all(&scenario_dir).expect("scenario dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
        "dotnet",
        r#"#!/usr/bin/env bash
set -euo pipefail
command_name="${1:-}"
shift || true

if [ "$command_name" = "build" ]; then
  project="${1:?missing build project}"
  shift
  configuration="Debug"
  while [ $# -gt 0 ]; do
    case "$1" in
      --configuration)
        configuration="${2:?missing configuration}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  output_dir="$(dirname "$project")/bin/$configuration/net9.0"
  mkdir -p "$output_dir"
  : > "$output_dir/spirectlbridge.dll"
  : > "$output_dir/spirectlbridge.pdb"
  exit 0
fi

if [ "$command_name" = "publish" ]; then
  shift
  output_dir=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --output)
        output_dir="${2:?missing output dir}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  mkdir -p "$output_dir"
  : > "$output_dir/Spirectl.BridgeMod.Sts2Host.dll"
  : > "$output_dir/Spirectl.BridgeMod.dll"
  exit 0
fi

echo "unexpected dotnet command: $command_name" >&2
exit 1
"#,
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("MyModProject");
    let build_output = project_root.join("dist/MyMod");
    fs::create_dir_all(&project_root).expect("project root");

    let build_script = write_executable_script(
        workspace.path(),
        "build-mod.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nmkdir -p \"{}\"\nprintf 'built mod\\n' > \"{}\"\n",
            build_output.display(),
            build_output.join("mod.txt").display()
        ),
    );
    let stop_script = write_executable_script(
        workspace.path(),
        "stop-game.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );
    let launch_capture = workspace.path().join("launch.txt");
    let launch_script = write_executable_script(
        workspace.path(),
        "launch-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            launch_capture.display()
        ),
    );

    let socket_path = workspace.path().join("spirectl-runner-deploy.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = sts2::bridge::StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        game: sts2::GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launch_script.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            stop_command: vec![stop_script.to_string_lossy().into_owned()],
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/MyMod".to_string()),
            ..sts2::GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let scenario = scenario_dir.join("deploy-relative.sts2.yaml");
    fs::write(
        &scenario,
        r#"name: deploy-relative
steps:
  - game.deploy:
      path: ../MyModProject
      build: true
      restart: true
      verify: true
      timeoutMs: 500
      intervalMs: 5
"#,
    )
    .expect("write scenario");

    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let scenario_path = scenario.to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "test",
                "run",
                &scenario_path,
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join test run");

    assert_eq!(payload["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "game.deploy"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["mod"]["name"],
        "MyMod"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("step artifact path"),
    );
    assert!(step_artifact_path.exists());
    assert_eq!(read_json_file(&step_artifact_path)["mod"]["name"], "MyMod");
    // `state.screen` disappeared with the state envelope (see
    // cli/src/lifecycle/lifecycle_deploy_wait.rs `attach_state_summary_json`);
    // `stateSummary.rootScene` is the real, currently-populated replacement proof
    // that the restarted runtime answered a full state extraction.
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["restart"]["launch"]["attachment"]["stateSummary"]
            ["rootScene"],
        "screens/main_menu"
    );
    assert_eq!(
        fs::read_to_string(&launch_capture).expect("launch capture"),
        socket_path.to_string_lossy().as_ref()
    );

    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_executes_inline_game_deploy_relative_to_cwd() {
    let workspace = tempfile::tempdir().expect("workspace");
    let fake_bin_dir = workspace.path().join("fake-bin");
    fs::create_dir_all(&fake_bin_dir).expect("fake bin dir");
    let dotnet = write_executable_script(
        &fake_bin_dir,
        "dotnet",
        r#"#!/usr/bin/env bash
set -euo pipefail
command_name="${1:-}"
shift || true

if [ "$command_name" = "build" ]; then
  project="${1:?missing build project}"
  shift
  configuration="Debug"
  while [ $# -gt 0 ]; do
    case "$1" in
      --configuration)
        configuration="${2:?missing configuration}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  output_dir="$(dirname "$project")/bin/$configuration/net9.0"
  mkdir -p "$output_dir"
  : > "$output_dir/spirectlbridge.dll"
  : > "$output_dir/spirectlbridge.pdb"
  exit 0
fi

if [ "$command_name" = "publish" ]; then
  shift
  output_dir=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --output)
        output_dir="${2:?missing output dir}"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  mkdir -p "$output_dir"
  : > "$output_dir/Spirectl.BridgeMod.Sts2Host.dll"
  : > "$output_dir/Spirectl.BridgeMod.dll"
  exit 0
fi

echo "unexpected dotnet command: $command_name" >&2
exit 1
"#,
    );
    assert!(dotnet.exists());

    let game_dir = create_fake_game_layout();
    let project_root = workspace.path().join("MyInlineMod");
    let build_output = project_root.join("dist/MyInlineMod");
    fs::create_dir_all(&project_root).expect("project root");

    let build_script = write_executable_script(
        workspace.path(),
        "build-inline-mod.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nmkdir -p \"{}\"\nprintf 'built mod\\n' > \"{}\"\n",
            build_output.display(),
            build_output.join("mod.txt").display()
        ),
    );
    let stop_script = write_executable_script(
        workspace.path(),
        "stop-inline-game.sh",
        "#!/usr/bin/env bash\nset -euo pipefail\nexit 0\n",
    );
    let launch_capture = workspace.path().join("inline-launch.txt");
    let launch_script = write_executable_script(
        workspace.path(),
        "launch-inline-game.sh",
        &format!(
            "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$SPIRECTL_BRIDGE_SOCKET_PATH\" > \"{}\"\n",
            launch_capture.display()
        ),
    );

    let socket_path = workspace.path().join("spirectl-inline-runner-deploy.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind unix listener");
    let service = sts2::bridge::StubBridgeGrpcService::new(MockScenario::MainMenu);
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        game: sts2::GameConfig {
            path: game_dir.path().to_string_lossy().into_owned(),
            launch_executable: Some(launch_script.to_string_lossy().into_owned()),
            launch_working_dir: Some(game_dir.path().to_string_lossy().into_owned()),
            stop_command: vec![stop_script.to_string_lossy().into_owned()],
            deploy_build_command: vec![build_script.to_string_lossy().into_owned()],
            deploy_output_subdir: Some("dist/MyInlineMod".to_string()),
            ..sts2::GameConfig::default()
        },
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let inline = r#"{"name":"deploy-inline","steps":[{"game.deploy":{"path":"./MyInlineMod","build":true,"restart":true,"verify":true,"timeoutMs":500,"intervalMs":5}}]}"#;
    let path_value = std::env::var("PATH").expect("PATH");
    let prefixed_path = format!("{}:{}", fake_bin_dir.display(), path_value);
    let workdir = workspace.path().to_path_buf();
    let config_path = config.path().to_string_lossy().into_owned();
    let payload = tokio::task::spawn_blocking(move || {
        run_json_in_dir_with_env(
            &workdir,
            &[
                "--json",
                "--config",
                &config_path,
                "test",
                "run",
                "--inline",
                inline,
            ],
            &[("PATH", &prefixed_path)],
        )
    })
    .await
    .expect("join inline test run");

    assert_eq!(payload["status"], "passed");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["canonicalId"],
        "game.deploy"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["mod"]["name"],
        "MyInlineMod"
    );
    let step_artifact_path = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["stepArtifacts"][0]["path"]
            .as_str()
            .expect("step artifact path"),
    );
    assert!(step_artifact_path.exists());
    assert_eq!(
        read_json_file(&step_artifact_path)["mod"]["name"],
        "MyInlineMod"
    );
    // `state.screen` disappeared with the state envelope (see
    // cli/src/lifecycle/lifecycle_deploy_wait.rs `attach_state_summary_json`);
    // `stateSummary.rootScene` is the real, currently-populated replacement proof
    // that the restarted runtime answered a full state extraction.
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["output"]["restart"]["launch"]["attachment"]["stateSummary"]
            ["rootScene"],
        "screens/main_menu"
    );
    assert_eq!(
        fs::read_to_string(&launch_capture).expect("launch capture"),
        socket_path.to_string_lossy().as_ref()
    );

    server.abort();
}

#[test]
fn test_run_rejects_legacy_state_expect_steps() {
    let scenario = write_scenario(
        r#"
name: legacy-state
steps:
  - state:
      expect:
        screen.id: combat
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
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "unsupported_step"
    );
}

#[test]
fn test_run_can_skip_failure_artifact_collection() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: no-failure-artifacts
steps:
  - dev.assert:
      path: screen.id
      equals: combat
"#,
    );

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        "--failure-artifacts",
        "never",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    let run_dir = only_run_dir(artifacts_dir.path());
    let scenario_dir = PathBuf::from(
        payload["scenarios"][0]["artifacts"]["scenarioDir"]
            .as_str()
            .expect("scenario dir"),
    );

    assert_eq!(payload["artifacts"]["failureArtifacts"], "never");
    assert!(payload["scenarios"][0]["artifacts"]["failure"].is_null());
    assert!(run_dir.join("summary.json").exists());
    assert!(scenario_dir.join("result.json").exists());
    assert!(!scenario_dir.join("failure").exists());
}

#[test]
fn test_run_reports_parse_failures() {
    let scenario = write_scenario("name: bad\nsteps:\n  - dev.assert: [\n");
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
    assert_eq!(
        payload["scenarios"][0]["error"]["code"],
        "scenario_parse_failed"
    );
}

#[test]
fn test_run_human_summary_reports_artifact_locations() {
    let artifacts_dir = tempfile::tempdir().expect("artifacts dir");
    let config = artifact_config(artifacts_dir.path(), MockScenario::MainMenu);
    let scenario = write_scenario(
        r#"
name: human-summary
steps:
  - dev.assert:
      path: screen.id
      equals: combat
"#,
    );

    let response = run(&[
        "sts2",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let run_dir = only_run_dir(artifacts_dir.path());
    let summary_path = run_dir.join("summary.json");

    assert_eq!(response.exit_code, 3);
    assert!(response.stdout.contains("FAIL human-summary"));
    assert!(response.stdout.contains("step 1 dev.assert"));
    assert!(
        response
            .stdout
            .contains(summary_path.to_string_lossy().as_ref())
    );
    assert!(response.stdout.contains("result.json"));
}

#[test]
fn test_run_executes_directory_in_sorted_order() {
    let dir = tempfile::tempdir().expect("scenario dir");
    let alpha_dir = dir.path().join("a");
    let beta_dir = dir.path().join("b");
    fs::create_dir_all(&alpha_dir).expect("alpha dir");
    fs::create_dir_all(&beta_dir).expect("beta dir");
    fs::write(
        alpha_dir.join("alpha.sts2.json"),
        r#"{"name":"alpha","steps":["game.info"]}"#,
    )
    .expect("write alpha");
    fs::write(
        beta_dir.join("beta.sts2.yaml"),
        "name: beta\nsteps:\n  - game.info\n",
    )
    .expect("write beta");
    fs::write(
        dir.path().join("ignored.yaml"),
        "name: ignored\nsteps: []\n",
    )
    .expect("write ignored");

    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        dir.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["scenarioCount"], 2);
    assert_eq!(payload["scenarios"][0]["path"], "a/alpha.sts2.json");
    assert_eq!(payload["scenarios"][1]["path"], "b/beta.sts2.yaml");
}

#[test]
fn test_run_reports_empty_directory() {
    let dir = tempfile::tempdir().expect("scenario dir");
    let response = run(&[
        "sts2",
        "--json",
        "test",
        "run",
        dir.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 2);
    assert_eq!(payload["errors"][0]["code"], "no_scenarios_found");
}

#[test]
fn test_run_uses_highest_nonzero_exit_code() {
    let scenario = write_scenario(
        r#"
name: tcp-failure
steps:
  - game.info
"#,
    );
    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Tcp,
            tcp_address: Some("127.0.0.1:51173".to_string()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });

    let response = run(&[
        "sts2",
        "--json",
        "--config",
        config.path().to_str().expect("utf8 path"),
        "test",
        "run",
        scenario.path().to_str().expect("utf8 path"),
    ]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 4, "{}", response.stdout);
    assert_eq!(payload["exitCode"], 4);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "transport_connection_failed"
    );
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_wait_for_succeeds_over_ipc() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("runner.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind listener");
    let service = TransitioningBridgeService::default();

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let scenario = write_scenario(
        r#"
name: wait-success
steps:
  - dev.wait-for:
      path: run.currentRoom.scene
      equals: rooms/combat_room
      timeoutMs: 500
      intervalMs: 5
"#,
    );

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            scenario.path().to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join runner");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 0);
    assert_eq!(payload["scenarios"][0]["steps"][0]["status"], "passed");
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_wait_for_timeout_is_reported() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("runner-timeout.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind listener");
    let service = StaticBridgeService::new(MockScenario::MainMenu);

    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let scenario = write_scenario(
        r#"
name: wait-timeout
steps:
  - dev.wait-for:
      path: screen.id
      equals: combat
      timeoutMs: 20
      intervalMs: 1
      rpcTimeoutMs: 0
"#,
    );

    let response = tokio::task::spawn_blocking(move || {
        run(&[
            "sts2",
            "--json",
            "--config",
            config.path().to_str().expect("utf8 path"),
            "test",
            "run",
            scenario.path().to_str().expect("utf8 path"),
        ])
    })
    .await
    .expect("join runner");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 3);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "wait_timeout"
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_wait_for_reports_state_rpc_timeout() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let socket_path = socket_dir.path().join("runner-state-rpc-timeout.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind listener");
    let service = StaticBridgeService::new(MockScenario::MainMenu).with_hanging_state();
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);

    let config = write_config(&AppConfig {
        transport: sts2::TransportConfig {
            kind: TransportKind::Ipc,
            ipc_path: Some(socket_path.to_string_lossy().into_owned()),
            ..sts2::TransportConfig::default()
        },
        ..AppConfig::default()
    });
    let scenario = write_scenario(
        r#"
name: wait-rpc-timeout
steps:
  - dev.wait-for:
      path: screen.id
      equals: combat
      timeoutMs: 100
      intervalMs: 1
"#,
    );

    let response = tokio::time::timeout(
        std::time::Duration::from_secs(10),
        tokio::task::spawn_blocking(move || {
            run(&[
                "sts2",
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "test",
                "run",
                scenario.path().to_str().expect("utf8 path"),
            ])
        }),
    )
    .await
    .expect("runner should report the stuck state RPC instead of hanging")
    .expect("join runner");
    assert_eq!(response.exit_code, 4, "{}", response.stdout);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "bridge_rpc_timeout"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["details"][1]["value"],
        "state"
    );
    server.abort();
}

#[cfg(unix)]
#[tokio::test]
async fn test_run_profile_rpc_timeout_applies_to_scenario_steps_after_preflight() {
    let socket_dir = tempfile::tempdir().expect("socket dir");
    let game_dir = create_fake_game_layout();
    let socket_path = socket_dir
        .path()
        .join("runner-profile-action-rpc-timeout.sock");
    let listener = UnixListener::bind(&socket_path).expect("bind listener");
    let service = StaticBridgeService::new(MockScenario::MainMenu).with_hanging_actions();
    let server = ipc_bridge_support::spawn_unix_bridge_service(listener, service);
    let scenario = write_scenario(
        r#"
name: profile-action-rpc-timeout
tags: [liveValidation]
steps:
  - act.fallback-choose:
      choice: menu:start-run
"#,
    );
    let config = write_raw_config(&format!(
        r#"
test:
  profiles:
    live-ish:
      config:
        game:
          path: {}
        transport:
          kind: ipc
          ipcPath: {}
      paths:
        - {}
      includeTags:
        - liveValidation
      preflight:
        attach: true
        timeoutMs: 1000
        intervalMs: 10
        rpcTimeoutMs: 500
      gate:
        requireLiveTransport: true
        requireMatchingScenario: true
"#,
        game_dir.path().display(),
        socket_path.display(),
        scenario.path().display(),
    ));

    let response = tokio::time::timeout(
        std::time::Duration::from_secs(10),
        tokio::task::spawn_blocking(move || {
            run(&[
                "sts2",
                "--json",
                "--config",
                config.path().to_str().expect("utf8 path"),
                "test",
                "run",
                "--profile",
                "live-ish",
            ])
        }),
    )
    .await
    .expect("profile scenario step should inherit preflight RPC timeout instead of hanging")
    .expect("join runner");
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(response.exit_code, 4, "{}", response.stdout);
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["code"],
        "bridge_rpc_timeout"
    );
    assert_eq!(
        payload["scenarios"][0]["steps"][0]["error"]["details"][1]["value"],
        "act"
    );
    server.abort();
}
