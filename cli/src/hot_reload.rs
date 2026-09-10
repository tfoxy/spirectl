use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::{Value, json};

use crate::bridge::{
    RuntimeBridgeClient, bridge_error_exit_code, bridge_error_payload, hot_reload_response_json,
    hot_reload_status_json,
};
use crate::project::{
    interpolate_project_value, load_project_profiles, project_profile_exists,
    run_project_profile_by_name,
};
use crate::{AppContext, AppError, ModReloadCommand, ModReloadStatusArgs};

pub(crate) fn execute_mod_reload_json(
    args: ModReloadCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let Some(project) = args.project else {
        return Err(hot_reload_project_not_found(&absolute_path(Path::new(
            "sts2.hot-reload.yaml",
        ))));
    };
    let project = load_hot_reload_project(&project, context)?;

    let build = if args.build {
        let profile = project
            .logic_build_profile
            .as_deref()
            .ok_or_else(|| hot_reload_profile_missing(&project, ""))?;
        let profiles = load_project_profiles(&project.project_root, context)
            .map_err(|_| hot_reload_profile_missing(&project, profile))?;
        if !project_profile_exists(&profiles, profile) {
            return Err(hot_reload_profile_missing(&project, profile));
        }

        match run_project_profile_by_name(&profiles, profile, context) {
            Ok(payload) => json!({
                "requested": true,
                "profile": profile,
                "status": "succeeded",
                "steps": payload["steps"].clone(),
            }),
            Err(error) => {
                let build = json!({
                    "requested": true,
                    "profile": profile,
                    "status": "failed",
                    "steps": error.payload["steps"].clone(),
                });
                return Err(hot_reload_build_failed(&project, profile, build));
            }
        }
    } else {
        json!({
            "requested": false,
            "profile": project.logic_build_profile,
            "status": "skipped",
            "steps": [],
        })
    };

    let client = RuntimeBridgeClient::from_config(&context.config.transport);
    let status = client
        .hot_reload_status(hot_reload_status_request(&project))
        .map_err(from_bridge_error)?;
    let status_json = hot_reload_status_json(&status);
    if status
        .status
        .as_ref()
        .map(|shell| !shell.supported)
        .unwrap_or(true)
    {
        return Err(hot_reload_shell_unavailable(
            &project,
            status_json["shell"].clone(),
        ));
    }

    let request_id = new_request_id();
    let response = client
        .hot_reload(crate::bridge::proto::HotReloadRequest {
            request_id: request_id.clone(),
            project_id: project.project_id.clone(),
            shell_mod_id: project.shell_mod_id.clone(),
            logic_artifact_path: project.logic_artifact_path.clone(),
            expected_contract_version: project.expected_contract_version.unwrap_or_default(),
            wait_for_completion: args.wait,
            timeout_ms: args.timeout_ms.min(u32::MAX as u64) as u32,
        })
        .map_err(from_bridge_error)?;
    let response_json = hot_reload_response_json(&response);
    let shell = response_json["shell"].clone();
    let reload = response_json["reload"].clone();
    if let Some(error) = map_reload_failure(&project, &shell, &reload) {
        return Err(error);
    }

    Ok(json!({
        "status": reload["status"].as_str().unwrap_or("accepted"),
        "requestId": request_id,
        "project": project_json(&project),
        "build": build,
        "transport": transport_json(context),
        "shell": shell,
        "reload": reload,
        "notices": response_json["notices"].clone()
    }))
}

pub(crate) fn execute_mod_reload_status_json(
    args: ModReloadStatusArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    execute_mod_reload_status_capture_json(args, context)
}

pub(crate) fn execute_mod_reload_status_capture_json(
    args: ModReloadStatusArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let project = load_hot_reload_project(&args.project, context)?;
    let client = RuntimeBridgeClient::from_config(&context.config.transport);
    let status = client
        .hot_reload_status(hot_reload_status_request(&project))
        .map_err(from_bridge_error)?;
    let status_json = hot_reload_status_json(&status);
    Ok(json!({
        "project": project_json(&project),
        "transport": transport_json(context),
        "shell": status_json["shell"].clone(),
        "notices": status_json["notices"].clone()
    }))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HotReloadProjectFile {
    schema_version: String,
    project_id: String,
    shell_mod_id: String,
    shell_project: String,
    logic_project: String,
    logic_build_profile: Option<String>,
    logic_artifact_path: String,
    expected_contract_version: Option<u32>,
    protocol: HotReloadProtocolFile,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HotReloadProtocolFile {
    id: String,
    version: u32,
}

#[derive(Debug, Clone)]
struct ResolvedHotReloadProject {
    project_id: String,
    project_root: PathBuf,
    shell_mod_id: String,
    shell_project: String,
    logic_project: String,
    logic_build_profile: Option<String>,
    logic_artifact_path: String,
    expected_contract_version: Option<u32>,
}

fn load_hot_reload_project(
    project_root: &Path,
    context: AppContext<'_>,
) -> Result<ResolvedHotReloadProject, AppError> {
    let project_root = absolute_path(project_root);
    let metadata_path = project_root.join("sts2.hot-reload.yaml");
    let raw = std::fs::read_to_string(&metadata_path)
        .map_err(|_| hot_reload_project_not_found(&metadata_path))?;
    let file = serde_yaml::from_str::<HotReloadProjectFile>(&raw).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hot_reload_project_invalid",
                "message": "failed to parse hot-reload project metadata",
                "metadataFile": metadata_path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })?;
    if file.schema_version != "spirectl.hot-reload-project/v0" {
        return Err(hot_reload_project_invalid(
            &metadata_path,
            "schemaVersion must be spirectl.hot-reload-project/v0",
        ));
    }
    if file.protocol.id != "spirectl.m57.hot-reload-shell" {
        return Err(hot_reload_project_invalid(
            &metadata_path,
            "protocol.id must be spirectl.m57.hot-reload-shell",
        ));
    }
    if file.protocol.version != 0 {
        return Err(hot_reload_project_invalid(
            &metadata_path,
            "protocol.version must be 0",
        ));
    }

    let logic_artifact_path =
        interpolate_project_value(&file.logic_artifact_path, &project_root, context)?;

    Ok(ResolvedHotReloadProject {
        project_id: file.project_id,
        project_root,
        shell_mod_id: file.shell_mod_id,
        shell_project: file.shell_project,
        logic_project: file.logic_project,
        logic_build_profile: file.logic_build_profile,
        logic_artifact_path,
        expected_contract_version: file.expected_contract_version,
    })
}

fn absolute_path(path: &Path) -> PathBuf {
    let path = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    };
    path.components().collect()
}

fn project_json(project: &ResolvedHotReloadProject) -> Value {
    json!({
        "projectId": project.project_id,
        "projectRoot": project.project_root.display().to_string(),
        "shellModId": project.shell_mod_id,
        "shellProject": project.shell_project,
        "logicProject": project.logic_project,
        "logicBuildProfile": project.logic_build_profile,
        "logicArtifactPath": project.logic_artifact_path,
        "expectedContractVersion": project.expected_contract_version,
    })
}

fn hot_reload_status_request(
    project: &ResolvedHotReloadProject,
) -> crate::bridge::proto::HotReloadStatusRequest {
    crate::bridge::proto::HotReloadStatusRequest {
        request_id: new_request_id(),
        project_id: project.project_id.clone(),
        shell_mod_id: project.shell_mod_id.clone(),
    }
}

fn transport_json(context: AppContext<'_>) -> Value {
    let kind = match context.config.transport.kind {
        crate::TransportKind::Mock => "mock",
        crate::TransportKind::Ipc => "ipc",
        crate::TransportKind::Tcp => "tcp",
    };
    json!({
        "kind": kind,
        "source": if context.config.transport.kind == crate::TransportKind::Mock {
            "stub"
        } else {
            "live"
        },
    })
}

fn new_request_id() -> String {
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    static COUNTER: AtomicU32 = AtomicU32::new(1);
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let timestamp = compact_utc_timestamp(seconds);
    let suffix = COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("hr:{timestamp}:{suffix:08x}")
}

fn compact_utc_timestamp(unix_seconds: u64) -> String {
    let days = (unix_seconds / 86_400) as i64;
    let seconds_of_day = unix_seconds % 86_400;
    let (year, month, day) = civil_from_days(days);
    let hour = seconds_of_day / 3_600;
    let minute = (seconds_of_day % 3_600) / 60;
    let second = seconds_of_day % 60;

    format!("{year:04}{month:02}{day:02}T{hour:02}{minute:02}{second:02}Z")
}

fn civil_from_days(days_since_unix_epoch: i64) -> (i32, u32, u32) {
    let z = days_since_unix_epoch + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp + if mp < 10 { 3 } else { -9 };
    let year = y + if month <= 2 { 1 } else { 0 };

    (year as i32, month as u32, day as u32)
}

fn from_bridge_error(error: crate::bridge::proto::BridgeError) -> AppError {
    AppError {
        exit_code: bridge_error_exit_code(&error),
        payload: bridge_error_payload(&error),
    }
}

fn map_reload_failure(
    project: &ResolvedHotReloadProject,
    shell: &Value,
    reload: &Value,
) -> Option<AppError> {
    if reload["status"].as_str() != Some("failed") {
        return None;
    }

    let code = if reload["error"]["code"].as_str() == Some("reload_busy")
        && reload["error"]["message"]
            .as_str()
            .is_some_and(|message| message.to_ascii_lowercase().contains("timed out"))
    {
        "hot_reload_timeout"
    } else if reload["error"]["code"].as_str() == Some("reload_busy") {
        "hot_reload_busy"
    } else if reload["error"]["restartRequired"]
        .as_bool()
        .unwrap_or(false)
        || shell["restartRequired"].as_bool().unwrap_or(false)
    {
        "hot_reload_failed_restart_required"
    } else {
        "hot_reload_failed_previous_active"
    };

    Some(AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": code,
                "message": "hot reload failed",
                "project": project.project_root.display().to_string(),
                "shell": shell,
                "reload": reload,
            }
        }),
    })
}

fn hot_reload_shell_unavailable(project: &ResolvedHotReloadProject, shell: Value) -> AppError {
    let code = shell["notices"]
        .as_array()
        .and_then(|notices| {
            notices
                .iter()
                .find_map(|notice| match notice["code"].as_str() {
                    Some("hot-reload-shell-not-running") => Some("hot_reload_shell_not_running"),
                    Some("hot-reload-shell-mismatch") => Some("hot_reload_shell_mismatch"),
                    _ => None,
                })
        })
        .unwrap_or("hot_reload_shell_unsupported");

    AppError {
        exit_code: 3,
        payload: json!({
            "error": {
                "code": code,
                "message": "hot-reload shell is unavailable",
                "project": project.project_root.display().to_string(),
                "shell": shell,
            }
        }),
    }
}

fn hot_reload_project_not_found(metadata_path: &Path) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hot_reload_project_not_found",
                "message": "hot-reload project metadata was not found",
                "metadataFile": metadata_path.display().to_string()
            }
        }),
    }
}

fn hot_reload_project_invalid(metadata_path: &Path, details: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hot_reload_project_invalid",
                "message": "hot-reload project metadata is invalid",
                "metadataFile": metadata_path.display().to_string(),
                "details": details,
            }
        }),
    }
}

fn hot_reload_profile_missing(project: &ResolvedHotReloadProject, profile: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "hot_reload_profile_missing",
                "message": "hot-reload logic build profile was not found",
                "project": project.project_root.display().to_string(),
                "profile": profile,
            }
        }),
    }
}

fn hot_reload_build_failed(
    project: &ResolvedHotReloadProject,
    profile: &str,
    build: Value,
) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "hot_reload_build_failed",
                "message": "reloadable logic build failed; reload request was not sent",
                "project": project.project_root.display().to_string(),
                "profile": profile,
                "build": build,
            }
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AppConfig, ConfigOrigin, ConfigProvenance, Mode};

    fn test_context(config: &AppConfig, mode: Mode) -> AppContext<'_> {
        let origin = Box::leak(Box::new(ConfigOrigin {
            uses_default_stack: true,
            base_dir: PathBuf::from("."),
            config_dir: PathBuf::from("."),
            local_config_path: PathBuf::from("sts2.local.yaml"),
            config_dir_source: crate::config::ConfigDirSource::CurrentDir,
        }));
        let provenance = Box::leak(Box::new(ConfigProvenance::default()));
        AppContext {
            config,
            config_origin: origin,
            config_provenance: provenance,
            json_output: true,
            mode,
            instance: None,
        }
    }

    fn project() -> ResolvedHotReloadProject {
        ResolvedHotReloadProject {
            project_id: "my-hot-mod".to_string(),
            project_root: PathBuf::from("/mods/MyHotMod"),
            shell_mod_id: "my-hot-mod".to_string(),
            shell_project: "MyHotMod.Shell".to_string(),
            logic_project: "MyHotMod.Logic".to_string(),
            logic_build_profile: Some("my-hot-mod-logic-build".to_string()),
            logic_artifact_path: "/game/mods/MyHotMod.Shell/hot-reload/MyHotMod.Logic.dll"
                .to_string(),
            expected_contract_version: Some(1),
        }
    }

    #[test]
    fn reload_executor_reaches_project_loading_in_normal_mode() {
        let config = AppConfig::default();
        let error = execute_mod_reload_json(
            ModReloadCommand {
                command: None,
                project: Some(PathBuf::from("./missing-project")),
                build: false,
                wait: false,
                timeout_ms: 30_000,
                interval_ms: 250,
            },
            test_context(&config, Mode::Normal),
        )
        .expect_err("missing project should fail");

        assert_eq!(error.exit_code, 2);
        assert_eq!(
            error.payload["error"]["code"],
            "hot_reload_project_not_found"
        );
    }

    #[test]
    fn status_executor_reaches_project_loading_in_normal_mode() {
        let config = AppConfig::default();
        let error = execute_mod_reload_status_json(
            ModReloadStatusArgs {
                project: PathBuf::from("./missing-project"),
            },
            test_context(&config, Mode::Normal),
        )
        .expect_err("missing project should fail");

        assert_eq!(error.exit_code, 2);
        assert_eq!(
            error.payload["error"]["code"],
            "hot_reload_project_not_found"
        );
    }

    #[test]
    fn reload_timeout_report_maps_to_timeout_error() {
        let shell = json!({
            "restartRequired": false
        });
        let reload = json!({
            "status": "failed",
            "durationMs": 10,
            "error": {
                "code": "reload_busy",
                "phase": "load",
                "message": "Timed out waiting for hot reload completion.",
                "restartRequired": false
            }
        });

        let error = map_reload_failure(&project(), &shell, &reload).expect("mapped error");

        assert_eq!(error.exit_code, 3);
        assert_eq!(error.payload["error"]["code"], "hot_reload_timeout");
        assert_eq!(
            error.payload["error"]["reload"]["error"]["message"],
            "Timed out waiting for hot reload completion."
        );
    }

    #[test]
    fn generated_request_id_uses_compact_utc_and_hex_suffix() {
        let request_id = new_request_id();
        let parts = request_id.split(':').collect::<Vec<_>>();

        assert_eq!(parts.len(), 3);
        assert_eq!(parts[0], "hr");
        assert_eq!(parts[1].len(), 16);
        assert_eq!(&parts[1][8..9], "T");
        assert!(parts[1].ends_with('Z'));
        assert!(parts[1][..8].chars().all(|ch| ch.is_ascii_digit()));
        assert!(parts[1][9..15].chars().all(|ch| ch.is_ascii_digit()));
        assert_eq!(parts[2].len(), 8);
        assert!(parts[2].chars().all(|ch| ch.is_ascii_hexdigit()));
    }

    #[test]
    fn compact_utc_timestamp_formats_known_unix_seconds() {
        assert_eq!(compact_utc_timestamp(0), "19700101T000000Z");
        assert_eq!(compact_utc_timestamp(1_777_034_096), "20260424T123456Z");
    }
}
