use super::*;

#[derive(Debug, Clone)]
pub struct RenderedCommand {
    pub stdout: String,
    pub exit_code: i32,
}

pub(crate) fn render_structured(value: &Value, json_output: bool) -> String {
    if json_output {
        let mut rendered = serde_json::to_string_pretty(value).expect("valid json rendering");
        rendered.push('\n');
        rendered
    } else {
        serde_yaml::to_string(value).expect("valid yaml rendering")
    }
}

pub(crate) fn render_success<T: Serialize>(
    payload: T,
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    let value = serde_json::to_value(payload).map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "serialization_failed",
                "message": source.to_string()
            }
        }),
    })?;
    render_value_success(value, json_output)
}

pub(crate) fn render_value_success(
    value: Value,
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    Ok(RenderedCommand {
        stdout: render_structured(&value, json_output),
        exit_code: 0,
    })
}

pub(crate) fn render_value_success_compact_json(
    value: Value,
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    if json_output {
        let mut stdout = serde_json::to_string(&value).map_err(|source| AppError {
            exit_code: 5,
            payload: json!({
                "error": {
                    "code": "serialization_failed",
                    "message": source.to_string()
                }
            }),
        })?;
        stdout.push('\n');
        Ok(RenderedCommand {
            stdout,
            exit_code: 0,
        })
    } else {
        render_value_success(value, false)
    }
}

pub(crate) fn render_game_launch_success(
    value: Value,
    json_output: bool,
) -> Result<RenderedCommand, AppError> {
    if json_output {
        return render_value_success(value, json_output);
    }

    let launch = value.get("launch").unwrap_or(&Value::Null);
    let attachment = value.get("attachment").unwrap_or(&Value::Null);
    let screen = attachment.get("screen").unwrap_or(&Value::Null);
    let state_summary = attachment.get("stateSummary").unwrap_or(&Value::Null);

    let mut summary = json!({
        "status": "attached",
        "launch": {
            "pid": clone_field(launch, "pid"),
            "executable": clone_field(launch, "executable"),
            "gameExecutable": clone_field(launch, "gameExecutable"),
            "workingDir": clone_field(launch, "workingDir"),
            "endpoint": clone_field(launch, "endpoint"),
            "steamAppIdStatus": launch.pointer("/steamAppId/status").cloned().unwrap_or(Value::Null),
            "preLaunchEndpointCleanup": clone_field(launch, "preLaunchEndpointCleanup"),
        },
        "attachment": {
            "screen": {
                "id": clone_field(screen, "id"),
                "title": clone_field(screen, "title"),
                "instanceId": clone_field(screen, "instanceId"),
            },
            "state": {
                "schemaVersion": clone_field(state_summary, "schemaVersion"),
                "rootScene": clone_field(state_summary, "rootScene"),
            },
            "attempts": clone_field(attachment, "attempts"),
            "elapsedMs": clone_field(attachment, "elapsedMs"),
            "gameVersion": attachment.pointer("/gameInfo/bridge/gameVersion").cloned().unwrap_or(Value::Null),
            "bridgeVersion": attachment.pointer("/gameInfo/bridge/bridgeVersion").cloned().unwrap_or(Value::Null),
            "bridgeBuiltAtUtc": attachment.pointer("/gameInfo/bridge/buildIdentity/builtAtUtc").cloned().unwrap_or(Value::Null),
        },
        "stability": clone_field(&value, "stability"),
        "quiescence": value.get("quiescence").map(|quiescence| json!({
            "quiescent": clone_field(quiescence, "quiescent"),
            "budgetMs": clone_field(quiescence, "budgetMs"),
            "elapsedMs": clone_field(quiescence, "elapsedMs"),
            "timedOut": clone_field(quiescence, "timedOut"),
        })).unwrap_or(Value::Null),
        "fullOutput": "Run with --json for full launch, attachment, and handshake metadata."
    });

    remove_null_fields(&mut summary);
    render_value_success(summary, false)
}

pub(crate) fn clone_field(value: &Value, key: &str) -> Value {
    value.get(key).cloned().unwrap_or(Value::Null)
}

pub(crate) fn remove_null_fields(value: &mut Value) {
    match value {
        Value::Object(object) => {
            object.retain(|_, field| {
                remove_null_fields(field);
                !field.is_null()
            });
        }
        Value::Array(values) => {
            for value in values {
                remove_null_fields(value);
            }
        }
        _ => {}
    }
}

pub(crate) fn stdout_write_error(source: std::io::Error) -> AppError {
    AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "output_write_failed",
                "message": "failed to write command output",
                "details": source.to_string()
            }
        }),
    }
}
