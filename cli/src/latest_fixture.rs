use crate::{
    AppContext, AppError, LifecycleWaitArgs, LoadFixtureArgs, LoadLatestFixtureArgs, TransportKind,
    execute_load_fixture_json_from, lifecycle,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

const LATEST_FIXTURE_SCHEMA_VERSION: &str = "spirectl.latest-fixture/v0";
const LATEST_FIXTURE_ROOT: &str = ".sts2/fixtures";
const LATEST_FIXTURE_FILE: &str = "last-loaded.json";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LatestFixtureFile {
    schema_version: String,
    requested_path: String,
    #[serde(default)]
    resolved_path: String,
    #[serde(default)]
    fixture_name: String,
    #[serde(default)]
    screen: Value,
}

/// Persist the just-loaded fixture as the worktree's "latest" pointer.
///
/// Best-effort: callers fold any error into the load result as a non-fatal note rather than
/// failing the already-succeeded bridge load. Reads the canonical fields from the
/// `PreparedFixture::success_json` payload of the load.
pub(crate) fn record_latest_fixture(result: &Value) -> Result<PathBuf, AppError> {
    let record = record_from_result(result)?;
    write_latest_fixture_in(&latest_fixture_root(), &record)?;
    Ok(latest_fixture_path())
}

fn record_from_result(result: &Value) -> Result<LatestFixtureFile, AppError> {
    let requested_path = result
        .get("requestedPath")
        .and_then(Value::as_str)
        .ok_or_else(|| {
            latest_fixture_error(
                2,
                "latest_fixture_invalid_result",
                "Fixture load result did not include requestedPath.",
                json!({}),
            )
        })?
        .to_string();
    let resolved_path = result
        .get("resolvedPath")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string();
    let fixture = result.get("fixture");
    let fixture_name = fixture
        .and_then(|fixture| fixture.get("name"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string();
    let screen = fixture
        .and_then(|fixture| fixture.get("screen"))
        .cloned()
        .unwrap_or(Value::Null);

    Ok(LatestFixtureFile {
        schema_version: LATEST_FIXTURE_SCHEMA_VERSION.to_string(),
        requested_path,
        resolved_path,
        fixture_name,
        screen,
    })
}

pub(crate) fn execute_load_latest_fixture_json(
    args: LoadLatestFixtureArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let path = latest_fixture_path();
    if !path.exists() {
        return Err(latest_fixture_error(
            2,
            "latest_fixture_not_found",
            "No latest fixture has been recorded; run 'dev fixture load <path>' first.",
            json!({ "path": latest_fixture_path_string() }),
        ));
    }

    let record = read_latest_fixture(&path)?;

    let restart_payload = if args.restart && context.config.transport.kind != TransportKind::Mock {
        let wait = LifecycleWaitArgs {
            timeout_ms: args.timeout_ms,
            interval_ms: args.interval_ms,
            rpc_timeout_ms: 5_000,
        };
        let stop = lifecycle::execute_game_restart_stop_json(
            wait.clone(),
            context,
            "dev fixture load-latest",
        )
        .map_err(|error| latest_fixture_lifecycle_error("game restart stop", error))?;
        let launch = lifecycle::execute_game_launch_json(wait, context)
            .map_err(|error| latest_fixture_lifecycle_error("game launch", error))?;
        json!({
            "requested": true,
            "stop": stop,
            "launch": launch["launch"].clone(),
            "attachment": launch["attachment"].clone(),
        })
    } else {
        json!({ "requested": args.restart })
    };

    // Re-resolve the requested path relative to the current worktree's cwd (base_dir None),
    // matching how the original `dev fixture load` resolved it.
    let fixture_load = execute_load_fixture_json_from(
        LoadFixtureArgs::from_path(PathBuf::from(&record.requested_path)),
        None,
        context,
    )?;

    Ok(json!({
        "schemaVersion": LATEST_FIXTURE_SCHEMA_VERSION,
        "command": "dev fixture load-latest",
        "requestedPath": record.requested_path,
        "resolvedPath": record.resolved_path,
        "fixtureName": record.fixture_name,
        "screen": record.screen,
        "restart": restart_payload,
        "fixtureLoad": fixture_load,
    }))
}

fn write_latest_fixture_in(root: &Path, record: &LatestFixtureFile) -> Result<(), AppError> {
    fs::create_dir_all(root).map_err(|source| {
        latest_fixture_io_error(
            root,
            "Latest fixture directory could not be created.",
            source,
        )
    })?;

    let final_path = root.join(LATEST_FIXTURE_FILE);
    // Unique per-write temp name (pid + monotonic counter) so concurrent in-process writers
    // — e.g. parallel fixture-load tests sharing one cwd — never collide on the temp file.
    static TMP_COUNTER: AtomicU64 = AtomicU64::new(0);
    let tmp_path = root.join(format!(
        ".tmp-last-loaded-{}-{}.json",
        std::process::id(),
        TMP_COUNTER.fetch_add(1, Ordering::Relaxed)
    ));
    let payload = serde_json::to_string_pretty(record).map_err(|source| {
        latest_fixture_error(
            2,
            "latest_fixture_invalid_record",
            "Latest fixture pointer could not be serialized.",
            json!({ "detail": source.to_string() }),
        )
    })?;
    fs::write(&tmp_path, payload).map_err(|source| {
        latest_fixture_io_error(
            &tmp_path,
            "Latest fixture pointer could not be written.",
            source,
        )
    })?;
    fs::rename(&tmp_path, &final_path).map_err(|source| {
        latest_fixture_io_error(
            &final_path,
            "Latest fixture pointer could not be published.",
            source,
        )
    })?;
    Ok(())
}

fn read_latest_fixture(path: &Path) -> Result<LatestFixtureFile, AppError> {
    let raw = fs::read_to_string(path).map_err(|source| {
        latest_fixture_io_error(path, "Latest fixture pointer could not be read.", source)
    })?;
    let record: LatestFixtureFile = serde_json::from_str(&raw).map_err(|source| {
        latest_fixture_error(
            2,
            "latest_fixture_invalid_record",
            "Latest fixture pointer JSON was invalid.",
            json!({ "path": path.display().to_string(), "detail": source.to_string() }),
        )
    })?;
    if record.schema_version != LATEST_FIXTURE_SCHEMA_VERSION {
        return Err(latest_fixture_error(
            2,
            "latest_fixture_invalid_record",
            "Latest fixture pointer schema is unsupported.",
            json!({ "schemaVersion": record.schema_version }),
        ));
    }
    if record.requested_path.trim().is_empty() {
        return Err(latest_fixture_error(
            2,
            "latest_fixture_invalid_record",
            "Latest fixture pointer did not include a fixture path.",
            json!({ "path": path.display().to_string() }),
        ));
    }
    Ok(record)
}

fn latest_fixture_root() -> PathBuf {
    PathBuf::from(LATEST_FIXTURE_ROOT)
}

fn latest_fixture_path() -> PathBuf {
    latest_fixture_root().join(LATEST_FIXTURE_FILE)
}

fn latest_fixture_path_string() -> String {
    format!("{LATEST_FIXTURE_ROOT}/{LATEST_FIXTURE_FILE}")
}

fn latest_fixture_io_error(path: &Path, message: &str, source: std::io::Error) -> AppError {
    latest_fixture_error(
        2,
        "latest_fixture_io_error",
        message,
        json!({ "path": path.display().to_string(), "detail": source.to_string() }),
    )
}

fn latest_fixture_lifecycle_error(step: &str, error: AppError) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "latest_fixture_lifecycle_failed",
                "message": "Latest fixture load-latest lifecycle step failed.",
                "step": step,
                "cause": error.payload,
            }
        }),
    }
}

fn latest_fixture_error(exit_code: i32, code: &str, message: &str, detail: Value) -> AppError {
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

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_result() -> Value {
        json!({
            "requestedPath": "fixtures/basic-combat.sts2.fixture.yaml",
            "resolvedPath": "/abs/fixtures/basic-combat.sts2.fixture.yaml",
            "fixture": { "name": "basic-combat", "screen": "combat" },
        })
    }

    #[test]
    fn record_from_result_extracts_pointer_fields() {
        let record = record_from_result(&sample_result()).expect("record");
        assert_eq!(record.schema_version, LATEST_FIXTURE_SCHEMA_VERSION);
        assert_eq!(
            record.requested_path,
            "fixtures/basic-combat.sts2.fixture.yaml"
        );
        assert_eq!(
            record.resolved_path,
            "/abs/fixtures/basic-combat.sts2.fixture.yaml"
        );
        assert_eq!(record.fixture_name, "basic-combat");
        assert_eq!(record.screen, json!("combat"));
    }

    #[test]
    fn record_from_result_requires_requested_path() {
        let error = record_from_result(&json!({ "fixture": { "name": "x" } }))
            .expect_err("missing requestedPath should error");
        assert_eq!(error.exit_code, 2);
        assert_eq!(
            error.payload["error"]["code"],
            "latest_fixture_invalid_result"
        );
    }

    #[test]
    fn write_then_read_roundtrips() {
        let dir = tempfile::tempdir().expect("tempdir");
        let record = record_from_result(&sample_result()).expect("record");
        write_latest_fixture_in(dir.path(), &record).expect("write");

        let path = dir.path().join(LATEST_FIXTURE_FILE);
        assert!(path.exists(), "pointer file should be written");
        let back = read_latest_fixture(&path).expect("read");
        assert_eq!(back.requested_path, record.requested_path);
        assert_eq!(back.fixture_name, "basic-combat");
        assert_eq!(back.screen, json!("combat"));
    }

    #[test]
    fn read_rejects_unsupported_schema() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join(LATEST_FIXTURE_FILE);
        fs::write(
            &path,
            r#"{"schemaVersion":"spirectl.latest-fixture/v999","requestedPath":"x"}"#,
        )
        .expect("seed");
        let error = read_latest_fixture(&path).expect_err("schema mismatch should error");
        assert_eq!(error.exit_code, 2);
        assert_eq!(
            error.payload["error"]["code"],
            "latest_fixture_invalid_record"
        );
    }

    #[test]
    fn read_rejects_blank_requested_path() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join(LATEST_FIXTURE_FILE);
        fs::write(
            &path,
            format!(
                r#"{{"schemaVersion":"{LATEST_FIXTURE_SCHEMA_VERSION}","requestedPath":"   "}}"#
            ),
        )
        .expect("seed");
        let error = read_latest_fixture(&path).expect_err("blank path should error");
        assert_eq!(
            error.payload["error"]["code"],
            "latest_fixture_invalid_record"
        );
    }
}
