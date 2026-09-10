fn render_report(report: TestRunReport, json_output: bool) -> RenderedCommand {
    let stdout = if json_output {
        let mut rendered = serde_json::to_string_pretty(&report).expect("json report");
        rendered.push('\n');
        rendered
    } else {
        render_human_report(&report)
    };

    RenderedCommand {
        stdout,
        exit_code: report.exit_code,
    }
}

fn render_human_report(report: &TestRunReport) -> String {
    let mut lines = Vec::new();

    for scenario in &report.scenarios {
        let label = match scenario.status {
            "passed" => "PASS",
            "failed" => "FAIL",
            _ => "INVALID",
        };
        lines.push(format!("{label} {} [{}]", scenario.name, scenario.path));

        if let Some(step) = scenario.steps.last() {
            if let Some(error) = &step.error {
                let failure_phase = scenario
                    .failure
                    .as_ref()
                    .map(|failure| failure.phase)
                    .unwrap_or_else(|| classify_failure_phase(&step.canonical_id));
                lines.push(format!(
                    "  {} step {} {}: {}",
                    failure_phase,
                    step.index,
                    step.canonical_id,
                    error
                        .get("message")
                        .and_then(Value::as_str)
                        .unwrap_or("step failed")
                ));
            }
        } else if let Some(error) = &scenario.error {
            lines.push(format!(
                "  {}",
                error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("scenario failed")
            ));
        }

        if let Some(artifacts) = &scenario.artifacts {
            lines.push(format!("  result: {}", artifacts.result_path));
            if let Some(failure) = &artifacts.failure
                && let Some(summary_path) = &failure.summary_path
            {
                lines.push(format!("  failure summary: {summary_path}"));
            }
            for error in &artifacts.errors {
                lines.push(format!(
                    "  artifact {} {}: {}",
                    error.artifact,
                    error.operation,
                    error
                        .error
                        .get("message")
                        .and_then(Value::as_str)
                        .unwrap_or("artifact error")
                ));
            }
        }
    }

    for error in &report.errors {
        lines.push(format!(
            "ERROR {}",
            error
                .get("message")
                .and_then(Value::as_str)
                .unwrap_or("runner failed")
        ));
    }

    for error in &report.artifacts.errors {
        lines.push(format!(
            "ARTIFACT {} {}: {}",
            error.artifact,
            error.operation,
            error
                .error
                .get("message")
                .and_then(Value::as_str)
                .unwrap_or("artifact error")
        ));
    }

    lines.push(format!(
        "Summary: {} passed, {} failed, {} invalid, {} scenarios, {} steps, exit {}",
        report.passed_scenario_count,
        report.failed_scenario_count,
        report.invalid_scenario_count,
        report.scenario_count,
        report.step_count,
        report.exit_code
    ));
    lines.push(format!("Run summary: {}", report.artifacts.summary_path));

    let mut rendered = lines.join("\n");
    rendered.push('\n');
    rendered
}

fn render_human_stress_report(report: &Value) -> String {
    let mut lines = Vec::new();
    for iteration in report["iterations"].as_array().into_iter().flatten() {
        lines.push(format!(
            "{} iteration {} exit {}",
            iteration["status"]
                .as_str()
                .unwrap_or("unknown")
                .to_ascii_uppercase(),
            iteration["iteration"].as_u64().unwrap_or(0),
            iteration["exitCode"].as_i64().unwrap_or(0)
        ));
        if let Some(path) = iteration["runSummaryPath"].as_str() {
            lines.push(format!("  summary: {path}"));
        }
    }
    lines.push(format!(
        "Stress summary: {} passed, {} failed, completed {}, exit {}",
        report["passedIterations"].as_u64().unwrap_or(0),
        report["failedIterations"].as_u64().unwrap_or(0),
        report["completedIterations"].as_u64().unwrap_or(0),
        report["exitCode"].as_i64().unwrap_or(0)
    ));
    if let Some(path) = report["artifacts"]["summaryPath"].as_str() {
        lines.push(format!("Aggregate summary: {path}"));
    }
    let mut rendered = lines.join("\n");
    rendered.push('\n');
    rendered
}

#[allow(
    clippy::too_many_arguments,
    reason = "Report assembly keeps the final JSON-facing fields explicit at the call site."
)]
fn build_report(
    root: String,
    elapsed_ms: u128,
    exit_code: i32,
    scenarios: Vec<ScenarioReport>,
    errors: Vec<Value>,
    artifacts: TestRunArtifacts,
    profile: Option<TestRunProfileSummary>,
    preflight: Option<Value>,
) -> TestRunReport {
    let scenario_count = scenarios.len();
    let passed_scenario_count = scenarios
        .iter()
        .filter(|scenario| scenario.status == "passed")
        .count();
    let failed_scenario_count = scenarios
        .iter()
        .filter(|scenario| scenario.status == "failed")
        .count();
    let invalid_scenario_count = scenarios
        .iter()
        .filter(|scenario| scenario.status == "invalid")
        .count();
    let step_count = scenarios.iter().map(|scenario| scenario.steps.len()).sum();
    let passed_step_count = scenarios
        .iter()
        .flat_map(|scenario| scenario.steps.iter())
        .filter(|step| step.status == "passed")
        .count();
    let failed_step_count = scenarios
        .iter()
        .flat_map(|scenario| scenario.steps.iter())
        .filter(|step| step.status == "failed")
        .count();
    let invalid_step_count = scenarios
        .iter()
        .flat_map(|scenario| scenario.steps.iter())
        .filter(|step| step.status == "invalid")
        .count();

    let status = if exit_code == 0 {
        "passed"
    } else if failed_scenario_count > 0 {
        "failed"
    } else {
        "invalid"
    };

    TestRunReport {
        status,
        root,
        scenario_count,
        passed_scenario_count,
        failed_scenario_count,
        invalid_scenario_count,
        step_count,
        passed_step_count,
        failed_step_count,
        invalid_step_count,
        elapsed_ms,
        exit_code,
        scenarios,
        profile,
        preflight,
        cleanup: None,
        errors,
        artifacts,
    }
}

#[derive(Debug, Clone)]
struct RunArtifactsManager {
    root_dir: PathBuf,
    summary_path: PathBuf,
    failure_artifacts: FailureArtifactsMode,
    errors: Vec<ArtifactIssue>,
    writable: bool,
}

impl RunArtifactsManager {
    fn new(
        override_dir: Option<&Path>,
        failure_artifacts: FailureArtifactsMode,
        context: AppContext<'_>,
    ) -> Self {
        let configured_dir = override_dir
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(&context.config.artifacts.dir));
        let artifacts_dir = resolve_absolute_path(&configured_dir);
        let timestamp_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let root_dir = artifacts_dir
            .join("test-runs")
            .join(format!("run-{timestamp_ms}"));
        let summary_path = root_dir.join("summary.json");
        let mut errors = Vec::new();
        let writable = match fs::create_dir_all(&root_dir) {
            Ok(_) => true,
            Err(source) => {
                errors.push(io_artifact_error(
                    "run",
                    "create-dir",
                    &root_dir,
                    &format!(
                        "Failed to create test run artifact directory '{}': {source}",
                        root_dir.display()
                    ),
                ));
                false
            }
        };

        Self {
            root_dir,
            summary_path,
            failure_artifacts,
            errors,
            writable,
        }
    }

    fn snapshot(&self) -> TestRunArtifacts {
        TestRunArtifacts {
            root_dir: self.root_dir.display().to_string(),
            summary_path: self.summary_path.display().to_string(),
            failure_artifacts: self.failure_artifacts,
            errors: self.errors.clone(),
        }
    }

    fn scenario_dir_for(&self, index: usize, scenario_name: &str, scenario_path: &str) -> PathBuf {
        self.root_dir.join("scenarios").join(format!(
            "{index:03}-{}",
            slugify(&scenario_name_for_slug_parts(scenario_name, scenario_path))
        ))
    }

    fn persist_scenario(
        &mut self,
        index: usize,
        report: &mut ScenarioReport,
        context: AppContext<'_>,
    ) {
        let scenario_dir = self.scenario_dir_for(index, &report.name, &report.path);
        let result_path = scenario_dir.join("result.json");
        let steps_dir = scenario_dir.join("steps");
        let mut artifacts = ScenarioArtifacts {
            scenario_dir: scenario_dir.display().to_string(),
            result_path: result_path.display().to_string(),
            steps_dir: None,
            step_artifacts: Vec::new(),
            failure: None,
            errors: Vec::new(),
        };

        if self.writable
            && let Err(source) = fs::create_dir_all(&scenario_dir)
        {
            artifacts.errors.push(io_artifact_error(
                "scenario",
                "create-dir",
                &scenario_dir,
                &format!(
                    "Failed to create scenario artifact directory '{}': {source}",
                    scenario_dir.display()
                ),
            ));
        }

        if self.writable {
            if let Err(source) = fs::create_dir_all(&steps_dir) {
                artifacts.errors.push(io_artifact_error(
                    "steps",
                    "create-dir",
                    &steps_dir,
                    &format!(
                        "Failed to create step artifact directory '{}': {source}",
                        steps_dir.display()
                    ),
                ));
            } else {
                artifacts.steps_dir = Some(steps_dir.display().to_string());
                artifacts.step_artifacts =
                    self.collect_step_artifacts(report, &steps_dir, &mut artifacts.errors);
            }
        }

        report.failure = scenario_failure(report);
        if report.status == "failed" && self.failure_artifacts == FailureArtifactsMode::OnFailure {
            artifacts.failure = self.collect_failure_artifacts(
                &scenario_dir,
                report.failure.as_ref(),
                &mut artifacts.errors,
                context,
            );
        }

        report.artifacts = Some(artifacts.clone());
        if self.writable {
            write_json_artifact("result", &result_path, report, &mut artifacts.errors);
        } else {
            artifacts.errors.push(io_artifact_error(
                "result",
                "write",
                &result_path,
                &format!(
                    "Skipped writing result artifact because the run artifact root '{}' is unavailable.",
                    self.root_dir.display()
                ),
            ));
        }
        report.artifacts = Some(artifacts);
    }

    fn collect_failure_artifacts(
        &self,
        scenario_dir: &Path,
        failure: Option<&ScenarioFailure>,
        errors: &mut Vec<ArtifactIssue>,
        context: AppContext<'_>,
    ) -> Option<FailureArtifactPaths> {
        let failure_dir = scenario_dir.join("failure");
        if let Err(source) = fs::create_dir_all(&failure_dir) {
            errors.push(io_artifact_error(
                "failure",
                "create-dir",
                &failure_dir,
                &format!(
                    "Failed to create failure artifact directory '{}': {source}",
                    failure_dir.display()
                ),
            ));
            return None;
        }

        let mut paths = FailureArtifactPaths::default();
        let failure_summary_path = failure_dir.join("summary.json");
        if let Some(failure) = failure
            && write_json_artifact("failure-summary", &failure_summary_path, failure, errors)
        {
            paths.summary_path = Some(failure_summary_path.display().to_string());
        }

        let evidence_dir = failure_dir.join("evidence");
        let diagnostics_context =
            context_with_transport_rpc_timeout(context, FAILURE_DIAGNOSTICS_RPC_TIMEOUT_MS);
        match execute_diagnostics_json(
            DiagnosticsArgs {
                limit: 100,
                tail: None,
                after_cursor: None,
                level: None,
                target: None,
                exclude_targets: Vec::new(),
                exclude_message_regexes: Vec::new(),
                preset: None,
                width: None,
                height: None,
                preset_catalogs: Vec::new(),
                hot_reload_project: None,
                bundle_dir: Some(evidence_dir.clone()),
            },
            diagnostics_context.as_app_context(context.json_output),
        ) {
            Ok(payload) => {
                errors.extend(diagnostics_artifact_issues(&payload));
                if let Some(diagnostics_path) = payload
                    .get("bundle")
                    .and_then(|bundle| bundle.get("files"))
                    .and_then(|files| files.get("diagnostics"))
                    .and_then(Value::as_str)
                {
                    paths.evidence_dir = Some(evidence_dir.display().to_string());
                    paths.diagnostics_path = Some(diagnostics_path.to_string());
                } else {
                    errors.push(command_artifact_error(
                        "failure-evidence",
                        "collect",
                        &evidence_dir.join("diagnostics.json"),
                        &runner_error(
                            "diagnostics_bundle_missing",
                            "Failure diagnostics did not report a diagnostics.json bundle path.",
                        ),
                    ));
                }
            }
            Err(error) => {
                errors.push(command_artifact_error(
                    "failure-evidence",
                    "collect",
                    &evidence_dir.join("diagnostics.json"),
                    &error_payload(&error),
                ));
            }
        }

        Some(paths)
    }

    fn collect_step_artifacts(
        &self,
        report: &ScenarioReport,
        steps_dir: &Path,
        errors: &mut Vec<ArtifactIssue>,
    ) -> Vec<StepArtifact> {
        let mut artifacts = Vec::new();
        for step in &report.steps {
            let Some(output) = step.output.as_ref() else {
                continue;
            };

            match step.canonical_id.as_str() {
                "dev.screenshot-diff" => {
                    if let Some(path) = output
                        .get("bundle")
                        .and_then(|bundle| bundle.get("files"))
                        .and_then(|files| files.get("comparison"))
                        .and_then(Value::as_str)
                    {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.to_string(),
                            kind: "json".to_string(),
                        });
                    }
                }
                "game.deploy" | "dev.console" | "dev.load-fixture" | "dev.load-scenario"
                | "dev.log-health" | "dev.diagnostics" | "dev.hot-reload" => {
                    let path = steps_dir.join(format!(
                        "{:03}-{}.json",
                        step.index,
                        step.canonical_id.replace('.', "-")
                    ));
                    if write_json_artifact("step-output", &path, output, errors) {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.display().to_string(),
                            kind: "json".to_string(),
                        });
                    }
                }
                "dev.screenshot" => {
                    if let Some(path) = output.get("path").and_then(Value::as_str) {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.to_string(),
                            kind: "png".to_string(),
                        });
                    }
                }
                "dev.http" | "dev.http-wait" | "dev.websocket" | "dev.debug-events" => {
                    let path = steps_dir.join(format!(
                        "{:03}-{}.json",
                        step.index,
                        step.canonical_id.replace('.', "-")
                    ));
                    if write_json_artifact("step-output", &path, output, errors) {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.display().to_string(),
                            kind: "json".to_string(),
                        });
                    }
                }
                "dev.fetch" => {
                    let path = steps_dir.join(format!(
                        "{:03}-{}.json",
                        step.index,
                        step.canonical_id.replace('.', "-")
                    ));
                    if write_json_artifact("step-output", &path, output, errors) {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.display().to_string(),
                            kind: "json".to_string(),
                        });
                    }
                    if let Some(path) = output
                        .get("artifact")
                        .and_then(|artifact| artifact.get("path"))
                        .and_then(Value::as_str)
                    {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.to_string(),
                            kind: "file".to_string(),
                        });
                    }
                }
                "project.hook" => {
                    let path = steps_dir.join(format!(
                        "{:03}-{}.json",
                        step.index,
                        step.canonical_id.replace('.', "-")
                    ));
                    if write_json_artifact("step-output", &path, output, errors) {
                        artifacts.push(StepArtifact {
                            step_index: step.index,
                            step_canonical_id: step.canonical_id.clone(),
                            path: path.display().to_string(),
                            kind: "json".to_string(),
                        });
                    }
                    if let Some(artifacts_json) = output
                        .get("result")
                        .and_then(|result| result.get("artifacts"))
                        .and_then(Value::as_array)
                    {
                        for artifact in artifacts_json {
                            if let Some(path) = artifact.get("path").and_then(Value::as_str) {
                                artifacts.push(StepArtifact {
                                    step_index: step.index,
                                    step_canonical_id: step.canonical_id.clone(),
                                    path: path.to_string(),
                                    kind: artifact
                                        .get("kind")
                                        .and_then(Value::as_str)
                                        .unwrap_or("file")
                                        .to_string(),
                                });
                            }
                        }
                    }
                }
                _ => {}
            }
        }

        artifacts
    }

    fn persist_summary(&mut self, report: &mut TestRunReport) {
        report.artifacts = self.snapshot();
        if self.writable
            && !write_json_artifact("summary", &self.summary_path, report, &mut self.errors)
        {
            report.artifacts = self.snapshot();
        }
        report.artifacts = self.snapshot();
    }
}

fn resolve_absolute_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

fn resolve_step_path(path: &Path, base_dir: Option<&Path>) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else if let Some(base_dir) = base_dir {
        resolve_absolute_path(&base_dir.join(path))
    } else {
        resolve_absolute_path(path)
    }
}

fn resolve_diagnostics_bundle_dir(path: &Path, scenario_dir: Option<&Path>) -> PathBuf {
    let resolved = if path.is_absolute() {
        path.to_path_buf()
    } else if let Some(scenario_dir) = scenario_dir {
        scenario_dir.join(path)
    } else {
        resolve_absolute_path(path)
    };
    resolved.components().collect()
}

fn scenario_name_for_slug_parts(name: &str, path: &str) -> String {
    if !name.trim().is_empty() {
        name.to_string()
    } else {
        path.to_string()
    }
}

fn slugify(value: &str) -> String {
    let mut slug = String::new();
    let mut last_was_dash = false;

    for character in value.chars() {
        if character.is_ascii_alphanumeric() {
            slug.push(character.to_ascii_lowercase());
            last_was_dash = false;
        } else if !last_was_dash {
            slug.push('-');
            last_was_dash = true;
        }
    }

    let trimmed = slug.trim_matches('-').to_string();
    if trimmed.is_empty() {
        "scenario".to_string()
    } else {
        trimmed
    }
}

fn scenario_failure(report: &ScenarioReport) -> Option<ScenarioFailure> {
    if report.status != "failed" {
        return None;
    }

    report.steps.last().map(|step| ScenarioFailure {
        phase: classify_failure_phase(&step.canonical_id),
        step_index: step.index,
        step_requested_id: step.requested_id.clone(),
        step_canonical_id: step.canonical_id.clone(),
        exit_code: step.exit_code,
        error: step
            .error
            .clone()
            .unwrap_or_else(|| runner_error("scenario_failed", "Scenario step failed.")),
    })
}

fn classify_failure_phase(canonical_id: &str) -> &'static str {
    match canonical_id {
        "game.deploy" | "dev.load-fixture" | "dev.load-scenario" => "setup",
        _ => "execution",
    }
}

fn write_json_artifact<T: Serialize>(
    artifact: &str,
    path: &Path,
    value: &T,
    errors: &mut Vec<ArtifactIssue>,
) -> bool {
    let mut rendered = match serde_json::to_string_pretty(value) {
        Ok(rendered) => rendered,
        Err(source) => {
            errors.push(serialization_artifact_error(
                artifact,
                "serialize",
                path,
                &format!(
                    "Failed to serialize '{artifact}' artifact for '{}': {source}",
                    path.display()
                ),
            ));
            return false;
        }
    };
    rendered.push('\n');

    match fs::write(path, rendered) {
        Ok(_) => true,
        Err(source) => {
            errors.push(io_artifact_error(
                artifact,
                "write",
                path,
                &format!(
                    "Failed to write '{artifact}' artifact to '{}': {source}",
                    path.display()
                ),
            ));
            false
        }
    }
}

fn io_artifact_error(artifact: &str, operation: &str, path: &Path, message: &str) -> ArtifactIssue {
    ArtifactIssue {
        artifact: artifact.to_string(),
        operation: operation.to_string(),
        path: path.display().to_string(),
        error: json!({
            "code": "artifact_write_failed",
            "message": message,
        }),
    }
}

fn write_json_file(path: &Path, value: &Value) -> Result<(), std::io::Error> {
    let mut rendered = serde_json::to_string_pretty(value).expect("json artifact");
    rendered.push('\n');
    fs::write(path, rendered)
}

fn serialization_artifact_error(
    artifact: &str,
    operation: &str,
    path: &Path,
    message: &str,
) -> ArtifactIssue {
    ArtifactIssue {
        artifact: artifact.to_string(),
        operation: operation.to_string(),
        path: path.display().to_string(),
        error: json!({
            "code": "artifact_serialization_failed",
            "message": message,
        }),
    }
}

fn command_artifact_error(
    artifact: &str,
    operation: &str,
    path: &Path,
    error: &Value,
) -> ArtifactIssue {
    ArtifactIssue {
        artifact: artifact.to_string(),
        operation: operation.to_string(),
        path: path.display().to_string(),
        error: error.clone(),
    }
}

fn diagnostics_artifact_issues(payload: &Value) -> Vec<ArtifactIssue> {
    payload
        .get("errors")
        .and_then(Value::as_array)
        .into_iter()
        .flat_map(|errors| errors.iter())
        .map(|issue| ArtifactIssue {
            artifact: issue
                .get("artifact")
                .or_else(|| issue.get("capture"))
                .and_then(Value::as_str)
                .unwrap_or("diagnostics")
                .to_string(),
            operation: issue
                .get("operation")
                .and_then(Value::as_str)
                .unwrap_or("collect")
                .to_string(),
            path: issue
                .get("path")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_string(),
            error: issue.get("error").cloned().unwrap_or_else(|| {
                runner_error(
                    "diagnostics_error",
                    "Diagnostics capture reported an unknown error.",
                )
            }),
        })
        .collect()
}

