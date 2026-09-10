use crate::{
    AppContext, AppError, LifecycleWaitArgs, LoadFixtureArgs, RecordedFixtureResumeArgs,
    TransportKind, bridge, bridge_client, execute_load_fixture_json_from, lifecycle,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

const RECORDED_FIXTURE_SCHEMA_VERSION: &str = "spirectl.recorded-fixture/v0";
const RECORDED_FIXTURE_ROOT: &str = ".sts2/fixtures/current-screen";
const RECORDED_FIXTURE_FILE: &str = "fixture.sts2.fixture.yaml";
const RECORDED_METADATA_FILE: &str = "metadata.json";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecordedFixtureMetadataFile {
    schema_version: String,
    command: String,
    recorded_at: String,
    path: String,
    metadata_path: String,
    screen: RecordedFixtureScreenFile,
    source: RecordedFixtureSourceFile,
    restore_quality: String,
    #[serde(default)]
    known_omissions: Vec<RecordedFixtureNoteFile>,
    #[serde(default)]
    notices: Vec<RecordedFixtureNoticeFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecordedFixtureScreenFile {
    #[serde(rename = "type")]
    screen_type: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    screen_instance_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecordedFixtureSourceFile {
    #[serde(default)]
    game_version: String,
    #[serde(default)]
    bridge_version: String,
    #[serde(default)]
    spirectl_version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecordedFixtureNoteFile {
    code: String,
    message: String,
    #[serde(default)]
    field: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RecordedFixtureNoticeFile {
    code: String,
    message: String,
    #[serde(default)]
    provisional: bool,
}

pub(crate) fn execute_recorded_fixture_record_json(
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let client = bridge_client(context);
    let response = client
        .record_fixture(bridge::proto::RecordedFixtureRequest {
            request_id: format!("record-fixture-{}", std::process::id()),
            perspective: Some(bridge::proto::PerspectiveSelector {
                scope: bridge::proto::PerspectiveScope::Local as i32,
                player_id: String::new(),
            }),
        })
        .map_err(AppError::bridge)?;

    if response.fixture_json.trim().is_empty() {
        return Err(recorded_fixture_error(
            2,
            "recorded_fixture_invalid_fixture",
            "Bridge recorded fixture response did not include fixture JSON.",
            json!({}),
        ));
    }

    let fixture_json: Value = serde_json::from_str(&response.fixture_json).map_err(|source| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_fixture",
            "Bridge recorded fixture JSON was invalid.",
            json!({ "detail": source.to_string() }),
        )
    })?;
    let fixture_yaml = serde_yaml::to_string(&fixture_json).map_err(|source| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_fixture",
            "Bridge recorded fixture could not be serialized to YAML.",
            json!({ "detail": source.to_string() }),
        )
    })?;

    write_current_recorded_fixture(context, &fixture_yaml, &response)
}

pub(crate) fn execute_recorded_fixture_status_json(
    _context: AppContext<'_>,
) -> Result<Value, AppError> {
    let root = recorded_fixture_root();
    let fixture_path = root.join(RECORDED_FIXTURE_FILE);
    let metadata_path = root.join(RECORDED_METADATA_FILE);
    if !fixture_path.exists() || !metadata_path.exists() {
        return Ok(json!({
            "schemaVersion": RECORDED_FIXTURE_SCHEMA_VERSION,
            "command": "dev fixture status",
            "exists": false,
            "path": recorded_fixture_path_string(),
            "metadataPath": recorded_metadata_path_string(),
            "screen": Value::Null,
            "recordedAt": Value::Null,
            "restoreQuality": Value::Null,
            "knownOmissions": [],
        }));
    }

    let metadata = read_recorded_fixture_metadata(&metadata_path)?;
    Ok(json!({
        "schemaVersion": RECORDED_FIXTURE_SCHEMA_VERSION,
        "command": "dev fixture status",
        "exists": true,
        "path": metadata.path,
        "metadataPath": metadata.metadata_path,
        "screen": screen_json(&metadata.screen),
        "recordedAt": metadata.recorded_at,
        "restoreQuality": metadata.restore_quality,
        "knownOmissions": metadata.known_omissions.iter().map(note_json).collect::<Vec<_>>(),
    }))
}

pub(crate) fn execute_recorded_fixture_clear_json(
    _context: AppContext<'_>,
) -> Result<Value, AppError> {
    let root = recorded_fixture_root();
    let deleted = root.exists();
    if deleted {
        fs::remove_dir_all(&root).map_err(|source| {
            recorded_fixture_error(
                2,
                "recorded_fixture_io_error",
                "Recorded fixture directory could not be deleted.",
                json!({ "path": root.display().to_string(), "detail": source.to_string() }),
            )
        })?;
    }

    Ok(json!({
        "schemaVersion": RECORDED_FIXTURE_SCHEMA_VERSION,
        "command": "dev fixture clear",
        "deleted": deleted,
        "path": RECORDED_FIXTURE_ROOT,
    }))
}

pub(crate) fn execute_recorded_fixture_resume_json(
    args: RecordedFixtureResumeArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let root = recorded_fixture_root();
    let fixture_path = root.join(RECORDED_FIXTURE_FILE);
    let metadata_path = root.join(RECORDED_METADATA_FILE);
    if !fixture_path.exists() || !metadata_path.exists() {
        return Err(recorded_fixture_error(
            2,
            "recorded_fixture_not_found",
            "Recorded fixture was not found.",
            json!({
                "path": recorded_fixture_path_string(),
                "metadataPath": recorded_metadata_path_string(),
            }),
        ));
    }

    let metadata = read_recorded_fixture_metadata(&metadata_path)?;
    let restart_payload = if args.restart && context.config.transport.kind != TransportKind::Mock {
        let wait = LifecycleWaitArgs {
            timeout_ms: args.timeout_ms,
            interval_ms: args.interval_ms,
            rpc_timeout_ms: 5_000,
        };
        let stop =
            lifecycle::execute_game_restart_stop_json(wait.clone(), context, "dev fixture resume")
                .map_err(|error| recorded_fixture_lifecycle_error("game restart stop", error))?;
        let launch = lifecycle::execute_game_launch_json(wait, context)
            .map_err(|error| recorded_fixture_lifecycle_error("game launch", error))?;
        json!({
            "requested": true,
            "stop": stop,
            "launch": launch["launch"].clone(),
            "attachment": launch["attachment"].clone(),
        })
    } else {
        json!({ "requested": args.restart })
    };

    let fixture_load = execute_load_fixture_json_from(
        LoadFixtureArgs::from_path(fixture_path.clone()),
        None,
        context,
    )?;

    Ok(json!({
        "schemaVersion": RECORDED_FIXTURE_SCHEMA_VERSION,
        "command": "dev fixture resume",
        "path": metadata.path,
        "screen": screen_json(&metadata.screen),
        "restoreQuality": metadata.restore_quality,
        "restart": restart_payload,
        "fixtureLoad": fixture_load,
        "validation": {
            "status": "not-run",
            "checked": [],
            "mismatches": [],
        },
        "knownOmissions": metadata.known_omissions.iter().map(note_json).collect::<Vec<_>>(),
        "notices": metadata.notices.iter().map(notice_json).collect::<Vec<_>>(),
    }))
}

fn write_current_recorded_fixture(
    _context: AppContext<'_>,
    fixture_yaml: &str,
    response: &bridge::proto::RecordedFixtureResponse,
) -> Result<Value, AppError> {
    let root = recorded_fixture_root();
    let tmp_root = root
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(format!(".tmp-current-screen-{}", std::process::id()));
    if tmp_root.exists() {
        fs::remove_dir_all(&tmp_root).map_err(|source| {
            recorded_fixture_io_error(
                &tmp_root,
                "Temporary recorded fixture directory could not be cleared.",
                source,
            )
        })?;
    }
    fs::create_dir_all(&tmp_root).map_err(|source| {
        recorded_fixture_io_error(
            &tmp_root,
            "Temporary recorded fixture directory could not be created.",
            source,
        )
    })?;

    let fixture_path = tmp_root.join(RECORDED_FIXTURE_FILE);
    let metadata_path = tmp_root.join(RECORDED_METADATA_FILE);
    fs::write(&fixture_path, fixture_yaml).map_err(|source| {
        recorded_fixture_io_error(
            &fixture_path,
            "Recorded fixture file could not be written.",
            source,
        )
    })?;

    let metadata = metadata_file(response)?;
    let metadata_json = serde_json::to_string_pretty(&metadata).map_err(|source| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_metadata",
            "Recorded fixture metadata could not be serialized.",
            json!({ "detail": source.to_string() }),
        )
    })?;
    fs::write(&metadata_path, metadata_json).map_err(|source| {
        recorded_fixture_io_error(
            &metadata_path,
            "Recorded fixture metadata could not be written.",
            source,
        )
    })?;

    if root.exists() {
        fs::remove_dir_all(&root).map_err(|source| {
            recorded_fixture_io_error(
                &root,
                "Previous recorded fixture directory could not be replaced.",
                source,
            )
        })?;
    }
    fs::create_dir_all(root.parent().unwrap_or_else(|| Path::new("."))).map_err(|source| {
        recorded_fixture_io_error(
            &root,
            "Recorded fixture parent directory could not be created.",
            source,
        )
    })?;
    fs::rename(&tmp_root, &root).map_err(|source| {
        recorded_fixture_io_error(
            &root,
            "Recorded fixture directory could not be published.",
            source,
        )
    })?;

    Ok(json!({
        "schemaVersion": RECORDED_FIXTURE_SCHEMA_VERSION,
        "command": "dev fixture record",
        "path": metadata.path,
        "metadataPath": metadata.metadata_path,
        "screen": screen_json(&metadata.screen),
        "recordedAt": metadata.recorded_at,
        "restoreQuality": metadata.restore_quality,
        "knownOmissions": metadata.known_omissions.iter().map(note_json).collect::<Vec<_>>(),
        "notices": metadata.notices.iter().map(notice_json).collect::<Vec<_>>(),
    }))
}

fn metadata_file(
    response: &bridge::proto::RecordedFixtureResponse,
) -> Result<RecordedFixtureMetadataFile, AppError> {
    let metadata = response.metadata.as_ref().ok_or_else(|| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_metadata",
            "Bridge recorded fixture response was missing metadata.",
            json!({}),
        )
    })?;
    let screen = metadata.screen.as_ref().ok_or_else(|| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_metadata",
            "Bridge recorded fixture metadata was missing screen information.",
            json!({}),
        )
    })?;

    Ok(RecordedFixtureMetadataFile {
        schema_version: RECORDED_FIXTURE_SCHEMA_VERSION.to_string(),
        command: "dev fixture record".to_string(),
        recorded_at: metadata.recorded_at.clone(),
        path: recorded_fixture_path_string(),
        metadata_path: recorded_metadata_path_string(),
        screen: RecordedFixtureScreenFile {
            screen_type: screen.id.clone(),
            title: screen.title.clone(),
            screen_instance_id: screen.screen_instance_id.clone(),
        },
        source: RecordedFixtureSourceFile {
            game_version: metadata.game_version.clone(),
            bridge_version: metadata.bridge_version.clone(),
            spirectl_version: metadata.spirectl_version.clone(),
        },
        restore_quality: restore_quality_name(metadata.restore_quality),
        known_omissions: metadata
            .known_omissions
            .iter()
            .map(|note| RecordedFixtureNoteFile {
                code: note.code.clone(),
                message: note.message.clone(),
                field: note.field.clone(),
            })
            .collect(),
        notices: metadata
            .notices
            .iter()
            .map(|notice| RecordedFixtureNoticeFile {
                code: notice.code.clone(),
                message: notice.message.clone(),
                provisional: notice.provisional,
            })
            .collect(),
    })
}

fn read_recorded_fixture_metadata(path: &Path) -> Result<RecordedFixtureMetadataFile, AppError> {
    let raw = fs::read_to_string(path).map_err(|source| {
        recorded_fixture_io_error(path, "Recorded fixture metadata could not be read.", source)
    })?;
    let metadata: RecordedFixtureMetadataFile = serde_json::from_str(&raw).map_err(|source| {
        recorded_fixture_error(
            2,
            "recorded_fixture_invalid_metadata",
            "Recorded fixture metadata JSON was invalid.",
            json!({ "path": path.display().to_string(), "detail": source.to_string() }),
        )
    })?;
    if metadata.schema_version != RECORDED_FIXTURE_SCHEMA_VERSION {
        return Err(recorded_fixture_error(
            2,
            "recorded_fixture_invalid_metadata",
            "Recorded fixture metadata schema is unsupported.",
            json!({ "schemaVersion": metadata.schema_version }),
        ));
    }
    Ok(metadata)
}

fn recorded_fixture_root() -> PathBuf {
    PathBuf::from(RECORDED_FIXTURE_ROOT)
}

fn recorded_fixture_path_string() -> String {
    format!("{RECORDED_FIXTURE_ROOT}/{RECORDED_FIXTURE_FILE}")
}

fn recorded_metadata_path_string() -> String {
    format!("{RECORDED_FIXTURE_ROOT}/{RECORDED_METADATA_FILE}")
}

fn screen_json(screen: &RecordedFixtureScreenFile) -> Value {
    json!({
        "id": screen.screen_type,
        "title": screen.title,
        "instanceId": screen.screen_instance_id,
    })
}

fn note_json(note: &RecordedFixtureNoteFile) -> Value {
    json!({
        "code": note.code,
        "message": note.message,
        "field": note.field,
    })
}

fn notice_json(notice: &RecordedFixtureNoticeFile) -> Value {
    json!({
        "code": notice.code,
        "message": notice.message,
        "provisional": notice.provisional,
    })
}

fn restore_quality_name(value: i32) -> String {
    match bridge::proto::RestoreQuality::try_from(value)
        .ok()
        .unwrap_or(bridge::proto::RestoreQuality::Unspecified)
    {
        bridge::proto::RestoreQuality::Exact => "exact",
        bridge::proto::RestoreQuality::Partial => "partial",
        bridge::proto::RestoreQuality::Unsupported => "unsupported",
        bridge::proto::RestoreQuality::Degraded => "degraded",
        bridge::proto::RestoreQuality::Unspecified => "unspecified",
    }
    .to_string()
}

fn recorded_fixture_io_error(path: &Path, message: &str, source: std::io::Error) -> AppError {
    recorded_fixture_error(
        2,
        "recorded_fixture_io_error",
        message,
        json!({ "path": path.display().to_string(), "detail": source.to_string() }),
    )
}

fn recorded_fixture_lifecycle_error(step: &str, error: AppError) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "recorded_fixture_lifecycle_failed",
                "message": "Recorded fixture resume lifecycle step failed.",
                "step": step,
                "cause": error.payload,
            }
        }),
    }
}

fn recorded_fixture_error(exit_code: i32, code: &str, message: &str, detail: Value) -> AppError {
    AppError {
        exit_code,
        payload: json!({
            "error": {
                "code": code,
                "message": message,
                "detail": detail,
            }
        }),
    }
}
