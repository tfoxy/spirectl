use super::*;

#[derive(Debug, Clone)]
pub struct AppError {
    pub exit_code: i32,
    pub payload: Value,
}

impl AppError {
    pub(crate) fn config(path: &Path, source: std::io::Error) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "config_read_failed",
                    "message": "failed to read config file",
                    "path": path.display().to_string(),
                    "details": source.to_string()
                }
            }),
        }
    }

    pub(crate) fn config_parse(path: &Path, source: serde_yaml::Error) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "config_parse_failed",
                    "message": "failed to parse config file",
                    "path": path.display().to_string(),
                    "details": source.to_string()
                }
            }),
        }
    }

    pub(crate) fn config_dir_missing(dir: &Path) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "config_dir_missing",
                    "message": format!(
                        "{} points at a directory with neither {} nor {}.",
                        crate::config::CONFIG_DIR_ENV,
                        crate::config::CHECKED_IN_CONFIG_FILE_NAME,
                        crate::config::LOCAL_CONFIG_FILE_NAME
                    ),
                    "path": dir.display().to_string(),
                    "envVar": crate::config::CONFIG_DIR_ENV
                }
            }),
        }
    }

    pub(crate) fn unknown_config_key(key: &str, known: &[&str]) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "unknown_config_key",
                    "message": format!("Unknown config key '{key}'."),
                    "key": key,
                    "knownKeys": known
                }
            }),
        }
    }

    pub(crate) fn fixture_read(path: &Path, source: &std::io::Error) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "fixture_read_failed",
                    "message": "failed to read fixture file",
                    "path": path.display().to_string(),
                    "details": source.to_string()
                }
            }),
        }
    }

    pub(crate) fn fixture_parse(path: &Path, source: &serde_yaml::Error) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "fixture_parse_failed",
                    "message": "failed to parse fixture file",
                    "path": path.display().to_string(),
                    "details": source.to_string()
                }
            }),
        }
    }

    pub(crate) fn invalid_fixture(path: &Path, message: &str, details: &[Value]) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_fixture",
                    "message": message,
                    "path": path.display().to_string(),
                    "details": details
                }
            }),
        }
    }

    pub(crate) fn tool_invocation(command: &str, message: &str) -> Self {
        Self {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "tool_invocation_failed",
                    "command": command,
                    "message": message
                }
            }),
        }
    }

    pub(crate) fn invalid_console_command() -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_console_command",
                    "message": "dev console requires a command name.",
                    "command": "dev console",
                }
            }),
        }
    }

    pub(crate) fn console_command_rejected(
        response: &bridge::proto::ConsoleCommandResponse,
    ) -> Self {
        Self {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "console_command_rejected",
                    "message": format!(
                        "Console command '{}' was rejected by the runtime console.",
                        response.line
                    ),
                    "requestId": response.request_id,
                    "command": response.command,
                    "args": response.args,
                    "line": response.line,
                    "accepted": response.accepted,
                    "success": response.success,
                    "output": response.output,
                    "outputLines": response.output_lines,
                    "source": bridge::data_source_name(bridge::proto::DataSource::try_from(response.source).unwrap_or(bridge::proto::DataSource::Unspecified)),
                    "provisional": response.provisional,
                    "notices": response.notices.iter().map(|notice| json!({
                        "code": notice.code,
                        "message": notice.message,
                        "provisional": notice.provisional,
                    })).collect::<Vec<_>>(),
                }
            }),
        }
    }

    pub(crate) fn output_write(path: &Path, source: &std::io::Error) -> Self {
        Self {
            exit_code: 5,
            payload: json!({
                "error": {
                    "code": "output_write_failed",
                    "message": "failed to write command output",
                    "path": path.display().to_string(),
                    "details": source.to_string()
                }
            }),
        }
    }

    pub(crate) fn invalid_query(path: &str, message: &str) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_query",
                    "message": message,
                    "path": path
                }
            }),
        }
    }

    pub(crate) fn assertion_failed(evaluation: &Evaluation) -> Self {
        Self {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "assertion_failed",
                    "message": format!(
                        "Assertion failed for state path '{}'.",
                        evaluation.path
                    ),
                    "path": evaluation.path,
                    "operator": evaluation.operator,
                    "expected": evaluation.expected,
                    "actual": evaluation.actual,
                    "pathExists": evaluation.actual.is_some(),
                    "resolution": evaluation.resolution.as_ref().map(query_resolution_json)
                }
            }),
        }
    }

    pub(crate) fn wait_timeout(
        evaluation: &Evaluation,
        timeout_ms: u64,
        interval_ms: u64,
        attempts: usize,
        elapsed_ms: u128,
    ) -> Self {
        Self {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "wait_timeout",
                    "message": format!(
                        "Timed out waiting for state path '{}' to satisfy {}.",
                        evaluation.path,
                        evaluation.operator
                    ),
                    "path": evaluation.path,
                    "operator": evaluation.operator,
                    "expected": evaluation.expected,
                    "actual": evaluation.actual,
                    "pathExists": evaluation.actual.is_some(),
                    "resolution": evaluation.resolution.as_ref().map(query_resolution_json),
                    "timeoutMs": timeout_ms,
                    "intervalMs": interval_ms,
                    "attempts": attempts,
                    "elapsedMs": elapsed_ms
                }
            }),
        }
    }

    pub(crate) fn transition_wait_timeout(
        timeout_ms: u64,
        interval_ms: u64,
        attempts: usize,
        elapsed_ms: u128,
        required_stable_samples: u32,
        stable_samples: u32,
        last_status: Value,
    ) -> Self {
        Self {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "transition_wait_timeout",
                    "message": "Timed out waiting for runtime transitions to settle.",
                    "timeoutMs": timeout_ms,
                    "intervalMs": interval_ms,
                    "attempts": attempts,
                    "elapsedMs": elapsed_ms,
                    "requiredStableSamples": required_stable_samples,
                    "stableSamples": stable_samples,
                    "lastStatus": last_status,
                }
            }),
        }
    }

    pub(crate) fn invalid_mode(
        command: &str,
        current_mode: Mode,
        required_mode: Mode,
        message: &str,
    ) -> Self {
        Self {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_mode",
                    "command": command,
                    "mode": current_mode.to_string(),
                    "requiredMode": required_mode.to_string(),
                    "message": message,
                }
            }),
        }
    }

    pub(crate) fn bridge(error: bridge::proto::BridgeError) -> Self {
        Self {
            exit_code: bridge_error_exit_code(&error),
            payload: bridge_error_payload(&error),
        }
    }

    pub fn render(self, json_output: bool) -> RenderedCommand {
        if !json_output && let Some(stdout) = render_game_launch_error_human(&self.payload) {
            return RenderedCommand {
                stdout,
                exit_code: self.exit_code,
            };
        }

        RenderedCommand {
            stdout: render_structured(&self.payload, json_output),
            exit_code: self.exit_code,
        }
    }

    pub(crate) fn visual_mismatch(payload: Value) -> Self {
        Self {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "visual_mismatch",
                    "message": "Visual validation failed.",
                    "comparison": payload,
                }
            }),
        }
    }
}

fn render_game_launch_error_human(payload: &Value) -> Option<String> {
    let error = payload.get("error")?;
    if error.get("command").and_then(Value::as_str) != Some("game launch") {
        return None;
    }

    let code = error.get("code").and_then(Value::as_str)?;
    let summary = game_launch_error_summary(code)?;
    let mut output = String::new();
    output.push_str("error: ");
    output.push_str(summary.title);
    output.push('\n');
    output.push_str(summary.explanation);
    output.push_str("\n\nnext steps:\n");
    for step in summary.next_steps {
        output.push_str("- ");
        output.push_str(step);
        output.push('\n');
    }

    output.push_str("\ndetails:\n");
    push_detail(&mut output, "code", Some(code));
    push_detail(
        &mut output,
        "command",
        error.get("command").and_then(Value::as_str),
    );
    push_endpoint_details(&mut output, error);
    push_detail(
        &mut output,
        "log",
        error
            .get("logPath")
            .or_else(|| error.pointer("/latestLog/logPath"))
            .and_then(Value::as_str),
    );
    push_detail_value(&mut output, "attempts", error.get("attempts"));
    push_detail_value(&mut output, "elapsedMs", error.get("elapsedMs"));
    push_detail_value(&mut output, "exitStatus", error.get("exitStatus"));
    push_detail_value(&mut output, "pid", error.pointer("/process/pid"));

    output.push_str("\nRun with --json for full diagnostics.\n");
    Some(output)
}

struct GameLaunchErrorSummary {
    title: &'static str,
    explanation: &'static str,
    next_steps: &'static [&'static str],
}

fn game_launch_error_summary(code: &str) -> Option<GameLaunchErrorSummary> {
    match code {
        "launch_steam_initialization_failed" => Some(GameLaunchErrorSummary {
            title: "Steam client is not running",
            explanation: "The game reported SteamAPI initialization failure before the spirectl bridge became reachable.",
            next_steps: &[
                "Start Steam in the same user session that owns Slay the Spire 2.",
                "Confirm Steam is logged in and can launch Slay the Spire 2 normally.",
                "Retry `sts2 game launch` after Steam is running.",
            ],
        }),
        "launch_timeout" => Some(GameLaunchErrorSummary {
            title: "game launch timed out",
            explanation: "The launched process kept running, but the spirectl bridge did not become reachable before the timeout.",
            next_steps: &[
                "Check the game window and Godot log for startup errors.",
                "Run `sts2 game bridge-health` to inspect the configured bridge endpoint.",
                "Retry `sts2 game launch` with a longer `--timeout-ms` if the game is still starting.",
            ],
        }),
        "launch_game_process_not_started" => Some(GameLaunchErrorSummary {
            title: "game process did not start",
            explanation: "The launch wrapper stayed alive, but no process matched the configured game.launchExecutable before the timeout.",
            next_steps: &[
                "Check game.launchWrapper and its arguments.",
                "Confirm game.launchExecutable points to the actual Slay the Spire 2 executable.",
                "Retry `sts2 game launch` after fixing the wrapper command.",
            ],
        }),
        "launch_exited_before_ipc" => Some(GameLaunchErrorSummary {
            title: "game exited before the bridge was ready",
            explanation: "The launched process ended before the spirectl bridge became reachable.",
            next_steps: &[
                "Check the game log for the startup failure.",
                "Confirm the launch executable and working directory are correct.",
                "Retry `sts2 game launch` after fixing the game startup issue.",
            ],
        }),
        "launch_exited_after_attach" => Some(GameLaunchErrorSummary {
            title: "game exited after the bridge attached",
            explanation: "The bridge became reachable, but the game process exited during the post-attach stability check.",
            next_steps: &[
                "Check the game log for the shutdown reason.",
                "Retry without `--verify-stable-ms` only if you are debugging a short-lived launch.",
                "Retry `sts2 game launch` after fixing the game shutdown issue.",
            ],
        }),
        "bridge_lost_after_attach" => Some(GameLaunchErrorSummary {
            title: "bridge disconnected after launch",
            explanation: "The bridge became reachable, then stopped responding during the post-attach stability check.",
            next_steps: &[
                "Check the game log for bridge or mod load failures.",
                "Run `sts2 game bridge-health` to inspect the configured endpoint.",
                "Retry `sts2 game launch` after fixing the bridge startup issue.",
            ],
        }),
        _ => None,
    }
}

fn push_endpoint_details(output: &mut String, error: &Value) {
    if let Some(endpoint) = error.get("endpoint") {
        let kind = endpoint.get("kind").and_then(Value::as_str);
        let value = endpoint
            .get("path")
            .or_else(|| endpoint.get("socketPath"))
            .or_else(|| endpoint.get("pipeName"))
            .or_else(|| endpoint.get("address"))
            .and_then(Value::as_str);
        match (kind, value) {
            (Some(kind), Some(value)) => {
                output.push_str("  endpoint: ");
                output.push_str(kind);
                output.push_str(" ");
                output.push_str(value);
                output.push('\n');
                return;
            }
            (Some(kind), None) => {
                push_detail(output, "endpoint", Some(kind));
                return;
            }
            _ => {}
        }
    }

    push_detail(
        output,
        "socket",
        error.get("socketPath").and_then(Value::as_str),
    );
    push_detail(
        output,
        "pipe",
        error.get("pipeName").and_then(Value::as_str),
    );
    push_detail(
        output,
        "tcp",
        error.get("tcpAddress").and_then(Value::as_str),
    );
}

fn push_detail(output: &mut String, label: &str, value: Option<&str>) {
    if let Some(value) = value
        && !value.is_empty()
    {
        output.push_str("  ");
        output.push_str(label);
        output.push_str(": ");
        output.push_str(value);
        output.push('\n');
    }
}

fn push_detail_value(output: &mut String, label: &str, value: Option<&Value>) {
    let Some(value) = value else {
        return;
    };
    if value.is_null() {
        return;
    }

    output.push_str("  ");
    output.push_str(label);
    output.push_str(": ");
    match value {
        Value::String(value) => output.push_str(value),
        Value::Number(value) => output.push_str(&value.to_string()),
        Value::Bool(value) => output.push_str(if *value { "true" } else { "false" }),
        _ => output.push_str(&value.to_string()),
    }
    output.push('\n');
}

#[cfg(test)]
mod human_error_rendering_tests {
    use super::*;

    #[test]
    fn steam_launch_failure_renders_concise_human_output() {
        let error = AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "launch_steam_initialization_failed",
                    "command": "game launch",
                    "message": "The launched game reported Steamworks initialization failure before the live bridge became reachable.",
                    "endpoint": {
                        "kind": "unix-socket",
                        "path": "/tmp/spirectl.sock",
                    },
                    "attempts": 10,
                    "elapsedMs": 3976,
                    "logPath": "/tmp/godot.log",
                    "logLine": "[ERROR] Steamworks initialization failed!",
                    "recentLogs": {
                        "entries": [
                            { "line": "stack frame" }
                        ]
                    },
                    "lastError": {
                        "error": {
                            "code": "ipc_socket_missing"
                        }
                    }
                }
            }),
        };

        let rendered = error.render(false);

        assert_eq!(rendered.exit_code, 4);
        assert!(
            rendered
                .stdout
                .starts_with("error: Steam client is not running\n")
        );
        assert!(
            rendered
                .stdout
                .contains("Start Steam in the same user session")
        );
        assert!(rendered.stdout.contains("  log: /tmp/godot.log\n"));
        assert!(
            rendered
                .stdout
                .contains("Run with --json for full diagnostics.")
        );
        assert!(!rendered.stdout.contains("recentLogs"));
        assert!(!rendered.stdout.contains("lastError"));
        assert!(!rendered.stdout.contains("stack frame"));
    }

    #[test]
    fn non_launch_error_uses_existing_structured_human_rendering() {
        let error = AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_query",
                    "message": "bad query"
                }
            }),
        };

        let rendered = error.render(false);

        assert!(rendered.stdout.starts_with("error:\n"));
        assert!(rendered.stdout.contains("code: invalid_query"));
    }

    #[test]
    fn json_launch_error_keeps_full_structured_payload() {
        let error = AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "launch_timeout",
                    "command": "game launch",
                    "lastError": {
                        "error": {
                            "code": "ipc_socket_missing"
                        }
                    }
                }
            }),
        };

        let rendered = error.render(true);
        let payload: Value = serde_json::from_str(&rendered.stdout).expect("json");

        assert_eq!(payload["error"]["code"], "launch_timeout");
        assert_eq!(
            payload["error"]["lastError"]["error"]["code"],
            "ipc_socket_missing"
        );
    }
}
