use std::env;
use std::io::Write;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicU64;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use hyper::header::{AUTHORIZATION, CONTENT_TYPE, HeaderValue};
use hyper::server::conn::http1;
use hyper::service::service_fn;
use hyper::{Request, Response, StatusCode};
use hyper_util::rt::TokioIo;
use percent_encoding::{AsciiSet, CONTROLS};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::net::TcpListener;

use crate::{
    AppContext, AppError, DebugSessionRoleArg, FailureArtifactsMode, RuntimeContextOwned,
    ServiceConfig, ServiceJobStoreMode, ServiceMcpMode, ServiceServeArgs, TestRunArgs,
    ai_tools_catalog_json, execute_ai_tool_json, record_usage_command, test_runner,
};

#[path = "automation_service_artifacts.rs"]
mod automation_service_artifacts;
#[path = "automation_service_debug.rs"]
mod automation_service_debug;
#[path = "automation_service_durable.rs"]
mod automation_service_durable;
#[path = "automation_service_execution.rs"]
mod automation_service_execution;
#[path = "automation_service_job_store.rs"]
mod automation_service_job_store;
#[path = "automation_service_routing.rs"]
mod automation_service_routing;

use automation_service_artifacts::serve_artifact_file;
use automation_service_debug::{
    handle_debug_session_end, handle_debug_session_events, handle_debug_session_get,
    handle_debug_session_start, handle_debug_session_wait,
};
use automation_service_execution::{
    artifact_index_json, handle_submit_test_run, refresh_job_artifact_urls,
};
use automation_service_routing::handle_request;

type ResponseBody = Full<Bytes>;

const URL_PATH_SEGMENT_ENCODE_SET: &AsciiSet = &CONTROLS
    .add(b' ')
    .add(b'"')
    .add(b'#')
    .add(b'%')
    .add(b'<')
    .add(b'>')
    .add(b'?')
    .add(b'`')
    .add(b'{')
    .add(b'}');

#[derive(Debug, Clone)]
struct ServiceState {
    metadata: ServiceMetadata,
    auth_token: Option<String>,
    runtime_context: RuntimeContextOwned,
    jobs: Arc<Mutex<std::collections::BTreeMap<String, RemoteTestRunJob>>>,
    job_store: Option<DurableJobStore>,
    next_run_id: Arc<AtomicU64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceMetadata {
    service: &'static str,
    version: &'static str,
    source_of_truth: &'static str,
    base_url: String,
    listen_address: String,
    artifact_root: String,
    job_store: ServiceJobStoreMetadata,
    recovery: ServiceRecoveryMetadata,
    auth: ServiceAuthMetadata,
    mcp: ServiceMcpMetadata,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceJobStoreMetadata {
    mode: ServiceJobStoreMode,
    root: Option<String>,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
struct ServiceRecoveryMetadata {
    recovered_jobs: usize,
    orphaned_jobs: usize,
    unknown_jobs: usize,
    warnings: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceAuthMetadata {
    mode: &'static str,
    required: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceMcpMetadata {
    mode: ServiceMcpMode,
    endpoint: Option<String>,
    bind_address: String,
    auth: ServiceMcpAuthMetadata,
    backend_source: &'static str,
    test_run_backend: ServiceMcpTestRunBackendMetadata,
    threat_model_acknowledged: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceMcpAuthMetadata {
    mode: &'static str,
    required: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ServiceMcpTestRunBackendMetadata {
    endpoint: &'static str,
    status_endpoint: &'static str,
    artifacts_endpoint: &'static str,
    schema: &'static str,
}

#[derive(Debug, Clone)]
struct EffectiveMcpConfig {
    mode: ServiceMcpMode,
    listen: SocketAddr,
    auth_token: Option<String>,
    threat_model_acknowledged: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ToolCallRequest {
    name: String,
    #[serde(default)]
    arguments: Value,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RemoteTestRunRequest {
    profile: Option<String>,
    #[serde(default)]
    tags: Vec<String>,
    path: Option<PathBuf>,
    inline: Option<String>,
    artifacts_dir: Option<PathBuf>,
    failure_artifacts: Option<FailureArtifactsMode>,
    durable: Option<bool>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DebugSessionStartBody {
    name: Option<String>,
    role: Option<DebugSessionRoleArg>,
    pause: Option<bool>,
    lease_timeout_ms: Option<u32>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DebugWaitBody {
    timeout_ms: Option<u32>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DebugEventsBody {
    from_sequence: Option<u64>,
    limit: Option<u32>,
    follow: Option<bool>,
    timeout_ms: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
enum RemoteTestRunJobStatus {
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
    Orphaned,
    Unknown,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RemoteTestRunJob {
    status: RemoteTestRunJobStatus,
    submitted_at: String,
    started_at: Option<String>,
    updated_at: String,
    completed_at: Option<String>,
    command: Value,
    config: Value,
    summary: Option<Value>,
    summary_ref: Option<String>,
    error: Option<Value>,
    #[serde(default)]
    recovery: Option<Value>,
    runner_root: Option<PathBuf>,
    runner_root_ref: Option<String>,
    artifacts: Vec<RemoteArtifactIndexEntry>,
}

#[derive(Debug, Clone)]
struct DurableJobStore {
    root: PathBuf,
    artifact_root: PathBuf,
}

#[derive(Debug)]
struct DurableJobRecovery {
    jobs: std::collections::BTreeMap<String, RemoteTestRunJob>,
    next_run_counter: u64,
    metadata: ServiceRecoveryMetadata,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct RemoteArtifactIndexEntry {
    relative_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    size_bytes: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    modified_unix_ms: Option<u128>,
    #[serde(skip)]
    download_url: Option<String>,
}

impl Default for DurableJobRecovery {
    fn default() -> Self {
        Self {
            jobs: std::collections::BTreeMap::new(),
            next_run_counter: 1,
            metadata: ServiceRecoveryMetadata::default(),
        }
    }
}

pub(crate) fn serve<W: Write>(
    args: ServiceServeArgs,
    context: AppContext<'_>,
    writer: &mut W,
) -> Result<i32, AppError> {
    validate_auth_policy_with_config(&args, &context.config.service)?;

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_io()
        .build()
        .map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "service_runtime_init_failed",
                    "message": format!("Failed to initialize the automation service runtime: {source}")
                }
            }),
        })?;

    runtime.block_on(async move {
        let listener = TcpListener::bind(args.listen).await.map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "service_bind_failed",
                    "message": format!(
                        "Failed to bind automation service listener '{}': {source}",
                        args.listen
                    )
                }
            }),
        })?;
        let local_addr = listener.local_addr().map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "service_bind_failed",
                    "message": format!("Failed to inspect bound automation service listener: {source}")
                }
            }),
        })?;

        let artifact_root = resolve_absolute_path(Path::new(&context.config.artifacts.dir));
        let job_store = resolve_service_job_store(&args, &context.config.service, &artifact_root)?;
        let durable_job_store = match &job_store.root {
            Some(root) => Some(DurableJobStore::initialize(
                PathBuf::from(root),
                artifact_root.clone(),
            )?),
            None => None,
        };
        let base_url = format_base_url(local_addr);
        let recovered = match &durable_job_store {
            Some(store) => store.recover_jobs(&base_url)?,
            None => DurableJobRecovery::default(),
        };
        let mcp_config = effective_mcp_config(&args, &context.config.service);

        let metadata = ServiceMetadata {
            service: "sts2",
            version: env!("CARGO_PKG_VERSION"),
            source_of_truth: "sts2 CLI",
            base_url,
            listen_address: local_addr.to_string(),
            artifact_root: artifact_root.display().to_string(),
            job_store,
            recovery: recovered.metadata.clone(),
            auth: ServiceAuthMetadata {
                mode: if args.auth_token.is_some() { "bearer" } else { "none" },
                required: args.auth_token.is_some(),
            },
            mcp: ServiceMcpMetadata {
                mode: mcp_config.mode,
                endpoint: (mcp_config.mode == ServiceMcpMode::Network)
                    .then(|| format!("http://{}/v0/mcp", mcp_config.listen)),
                bind_address: mcp_config.listen.to_string(),
                auth: ServiceMcpAuthMetadata {
                    mode: if mcp_config.auth_token.is_some() {
                        "bearer"
                    } else {
                        "none"
                    },
                    required: mcp_config.auth_token.is_some(),
                },
                backend_source: "inspect ai-tools",
                test_run_backend: ServiceMcpTestRunBackendMetadata {
                    endpoint: "/v0/test-runs",
                    status_endpoint: "/v0/test-runs/{runId}",
                    artifacts_endpoint: "/v0/test-runs/{runId}/artifacts",
                    schema: "service-job-store",
                },
                threat_model_acknowledged: mcp_config.threat_model_acknowledged,
            },
        };
        let state = ServiceState {
            metadata: metadata.clone(),
            auth_token: args.auth_token.clone(),
            runtime_context: RuntimeContextOwned::from_app_context(context),
            jobs: Arc::new(Mutex::new(recovered.jobs)),
            job_store: durable_job_store,
            next_run_id: Arc::new(AtomicU64::new(recovered.next_run_counter)),
        };

        write_startup_metadata(writer, &metadata, context.json_output)?;

        loop {
            let (stream, _) = listener.accept().await.map_err(|source| AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "service_accept_failed",
                        "message": format!("Automation service listener failed while accepting a connection: {source}")
                    }
                }),
            })?;
            let io = TokioIo::new(stream);
            let state = state.clone();
            tokio::spawn(async move {
                let service = service_fn(move |request| handle_request(request, state.clone()));
                let _ = http1::Builder::new().serve_connection(io, service).await;
            });
        }
    })
}

pub(crate) fn validate_auth_policy_with_config(
    args: &ServiceServeArgs,
    service_config: &ServiceConfig,
) -> Result<(), AppError> {
    if !args.listen.ip().is_loopback() && args.auth_token.is_none() {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "service_auth_token_required",
                    "message": "Non-loopback service binds require --auth-token."
                }
            }),
        });
    }

    let mcp_config = effective_mcp_config(args, service_config);
    if mcp_config.mode == ServiceMcpMode::Network
        && !mcp_config.listen.ip().is_loopback()
        && mcp_config.auth_token.is_none()
    {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "network_mcp_auth_token_required",
                    "message": "Non-loopback network MCP binds require --mcp-auth-token."
                }
            }),
        });
    }

    if mcp_config.mode == ServiceMcpMode::Network
        && !mcp_config.listen.ip().is_loopback()
        && !mcp_config.threat_model_acknowledged
    {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "network_mcp_threat_model_acknowledgement_required",
                    "message": "Non-loopback network MCP binds require --acknowledge-non-loopback-mcp-threat-model."
                }
            }),
        });
    }

    Ok(())
}

fn effective_mcp_config(
    args: &ServiceServeArgs,
    service_config: &ServiceConfig,
) -> EffectiveMcpConfig {
    EffectiveMcpConfig {
        mode: args.mcp_mode.unwrap_or(service_config.mcp.mode),
        listen: args.mcp_listen.unwrap_or(service_config.mcp.listen),
        auth_token: args
            .mcp_auth_token
            .clone()
            .or_else(|| service_config.mcp.auth_token.clone()),
        threat_model_acknowledged: args.acknowledge_non_loopback_mcp_threat_model
            || service_config.mcp.acknowledge_non_loopback_threat_model,
    }
}

fn resolve_service_job_store(
    args: &ServiceServeArgs,
    service_config: &ServiceConfig,
    artifact_root: &Path,
) -> Result<ServiceJobStoreMetadata, AppError> {
    let mode = args.job_store.unwrap_or(service_config.job_store.mode);
    let configured_dir = service_config.job_store.dir.as_deref().map(Path::new);
    let root = match mode {
        ServiceJobStoreMode::Memory => None,
        ServiceJobStoreMode::Durable => Some(automation_service_job_store::resolve_root(
            args.job_store_dir.as_deref().or(configured_dir),
            artifact_root,
        )?),
    };

    Ok(ServiceJobStoreMetadata {
        mode,
        root: root.map(|path| path.display().to_string()),
    })
}

fn now_timestamp() -> String {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default();
    format!("{millis}")
}

fn relative_path_string(path: &Path) -> String {
    path.components()
        .map(|component| component.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/")
}

fn write_startup_metadata<W: Write>(
    writer: &mut W,
    metadata: &ServiceMetadata,
    json_output: bool,
) -> Result<(), AppError> {
    let rendered = if json_output {
        let mut line = serde_json::to_string(metadata).expect("startup metadata json");
        line.push('\n');
        line
    } else {
        serde_yaml::to_string(metadata).map_err(|source| AppError {
            exit_code: 5,
            payload: json!({
                "error": {
                    "code": "serialization_failed",
                    "message": source.to_string()
                }
            }),
        })?
    };

    writer.write_all(rendered.as_bytes()).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "stdout_write_failed",
                "message": format!("Failed to write automation service startup metadata: {source}")
            }
        }),
    })?;
    writer.flush().map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "stdout_write_failed",
                "message": format!("Failed to flush automation service startup metadata: {source}")
            }
        }),
    })?;
    Ok(())
}

async fn decode_tool_call_request(request: Request<Incoming>) -> Result<ToolCallRequest, Value> {
    let body = request
        .into_body()
        .collect()
        .await
        .map_err(|source| {
            json!({
                "error": {
                    "code": "invalid_json_body",
                    "message": format!("Failed to read request body: {source}")
                }
            })
        })?
        .to_bytes();

    serde_json::from_slice(&body).map_err(|source| {
        json!({
            "error": {
                "code": "invalid_json_body",
                "message": format!("Failed to parse request body as JSON: {source}")
            }
        })
    })
}

async fn decode_remote_test_run_request(
    request: Request<Incoming>,
) -> Result<RemoteTestRunRequest, Value> {
    let body = request
        .into_body()
        .collect()
        .await
        .map_err(|source| {
            json!({
                "error": {
                    "code": "invalid_json_body",
                    "message": format!("Failed to read request body: {source}")
                }
            })
        })?
        .to_bytes();

    serde_json::from_slice(&body).map_err(|source| {
        json!({
            "error": {
                "code": "invalid_json_body",
                "message": format!("Failed to parse request body as JSON: {source}")
            }
        })
    })
}

fn handle_test_run_get(path: &str, state: &ServiceState) -> Response<ResponseBody> {
    let Some(remainder) = path.strip_prefix("/v0/test-runs/") else {
        return not_found_response();
    };

    let jobs = state.jobs.lock().expect("jobs lock");
    let Some((run_id, suffix)) = split_run_id_and_suffix(remainder) else {
        return not_found_response();
    };
    let Some(job) = jobs.get(run_id) else {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "remote_test_run_not_found",
                    "message": format!("Remote test run '{run_id}' was not found.")
                }
            }),
        );
    };

    if suffix.is_empty() {
        return json_response(StatusCode::OK, remote_test_run_status_json(run_id, job));
    }

    if suffix == "/artifacts" {
        return match &job.status {
            RemoteTestRunJobStatus::Completed => json_response(
                StatusCode::OK,
                json!({
                    "runId": run_id,
                    "artifacts": artifact_index_json(&job.artifacts, &state.metadata.base_url, run_id),
                }),
            ),
            _ => json_response(
                StatusCode::CONFLICT,
                json!({
                    "error": {
                        "code": "remote_test_run_artifacts_unavailable",
                        "message": "Remote test run artifacts are only available after the run completes."
                    }
                }),
            ),
        };
    }

    if let Some(relative_path) = suffix.strip_prefix("/artifacts/") {
        return match &job.status {
            RemoteTestRunJobStatus::Completed => match &job.runner_root {
                Some(root_dir) => {
                    serve_artifact_file(run_id, root_dir, &job.artifacts, relative_path)
                }
                None => not_found_response(),
            },
            _ => json_response(
                StatusCode::CONFLICT,
                json!({
                    "error": {
                        "code": "remote_test_run_artifacts_unavailable",
                        "message": "Remote test run artifacts are only available after the run completes."
                    }
                }),
            ),
        };
    }

    not_found_response()
}

fn split_run_id_and_suffix(remainder: &str) -> Option<(&str, &str)> {
    if remainder.is_empty() {
        return None;
    }

    match remainder.split_once('/') {
        Some((run_id, _)) => Some((run_id, &remainder[run_id.len()..])),
        None => Some((remainder, "")),
    }
}

fn remote_test_run_status_json(run_id: &str, job: &RemoteTestRunJob) -> Value {
    let mut payload = json!({
        "runId": run_id,
        "status": job_status_name(&job.status),
        "submittedAt": job.submitted_at,
        "startedAt": job.started_at,
        "updatedAt": job.updated_at,
        "completedAt": job.completed_at,
    });
    if let Some(summary) = &job.summary {
        payload["summary"] = summary.clone();
    }
    if let Some(error) = &job.error {
        payload["error"] = error.clone();
    }
    if let Some(recovery) = &job.recovery {
        payload["recovery"] = recovery.clone();
    }
    payload
}

fn job_status_name(status: &RemoteTestRunJobStatus) -> &'static str {
    match status {
        RemoteTestRunJobStatus::Queued => "queued",
        RemoteTestRunJobStatus::Running => "running",
        RemoteTestRunJobStatus::Completed => "completed",
        RemoteTestRunJobStatus::Failed => "failed",
        RemoteTestRunJobStatus::Canceled => "canceled",
        RemoteTestRunJobStatus::Orphaned => "orphaned",
        RemoteTestRunJobStatus::Unknown => "unknown",
    }
}

fn authorize(
    request: &Request<Incoming>,
    expected_token: Option<&str>,
) -> Option<Response<ResponseBody>> {
    let expected_token = expected_token?;

    let Some(header) = request.headers().get(AUTHORIZATION) else {
        return Some(json_response(
            StatusCode::UNAUTHORIZED,
            json!({
                "error": {
                    "code": "unauthorized",
                    "message": "Bearer token required."
                }
            }),
        ));
    };

    let Ok(header) = header.to_str() else {
        return Some(json_response(
            StatusCode::UNAUTHORIZED,
            json!({
                "error": {
                    "code": "unauthorized",
                    "message": "Bearer token required."
                }
            }),
        ));
    };

    let expected_header = format!("Bearer {expected_token}");
    if header != expected_header {
        return Some(json_response(
            StatusCode::UNAUTHORIZED,
            json!({
                "error": {
                    "code": "unauthorized",
                    "message": "Bearer token required."
                }
            }),
        ));
    }

    None
}

fn json_response(status: StatusCode, payload: Value) -> Response<ResponseBody> {
    let body = serde_json::to_vec(&payload).expect("json response");
    let mut response = Response::new(Full::new(Bytes::from(body)));
    *response.status_mut() = status;
    response
        .headers_mut()
        .insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

fn service_app_error_payload(error: &AppError) -> Value {
    let mut payload = error.payload.clone();
    if let Some(object) = payload.as_object_mut() {
        object.insert("exitCode".to_string(), json!(error.exit_code));
    }
    payload
}

fn not_found_response() -> Response<ResponseBody> {
    json_response(
        StatusCode::NOT_FOUND,
        json!({
            "error": {
                "code": "not_found",
                "message": "Automation service endpoint not found."
            }
        }),
    )
}

fn format_base_url(address: std::net::SocketAddr) -> String {
    match address {
        std::net::SocketAddr::V4(_) => format!("http://{address}"),
        std::net::SocketAddr::V6(_) => format!("http://[{address}]"),
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

#[cfg(test)]
mod tests {
    use super::artifact_index_json;
    use super::automation_service_artifacts::{build_remote_artifact_manifest, safe_artifact_path};
    use std::fs;
    use tempfile::tempdir;

    #[test]
    fn remote_artifact_manifest_url_encodes_relative_paths() {
        let temp = tempdir().expect("tempdir");
        let root_dir = temp.path().join("run-root");
        let artifact_path = root_dir.join("folder with spaces").join("summary #1.json");
        fs::create_dir_all(artifact_path.parent().expect("artifact parent"))
            .expect("create artifact dir");
        fs::write(&artifact_path, "{}\n").expect("write artifact");

        let manifest = build_remote_artifact_manifest(&root_dir, "http://127.0.0.1:4317", "run-1");

        assert_eq!(manifest.len(), 1);
        assert_eq!(
            manifest[0].relative_path,
            "folder with spaces/summary #1.json"
        );
        assert_eq!(
            artifact_index_json(&manifest, "http://127.0.0.1:4317", "run-1")[0]["downloadUrl"],
            "http://127.0.0.1:4317/v0/test-runs/run-1/artifacts/folder%20with%20spaces/summary%20%231.json"
        );
    }

    #[test]
    fn safe_artifact_path_decodes_percent_encoded_segments() {
        let temp = tempdir().expect("tempdir");
        let root_dir = temp.path().join("run-root");

        let resolved =
            safe_artifact_path(&root_dir, "folder%20with%20spaces/summary%20%231%25.json");

        assert_eq!(
            resolved,
            Some((
                "folder with spaces/summary #1%.json".to_string(),
                root_dir.join("folder with spaces").join("summary #1%.json")
            ))
        );
    }

    #[test]
    fn safe_artifact_path_rejects_encoded_parent_segments() {
        let temp = tempdir().expect("tempdir");
        let root_dir = temp.path().join("run-root");

        let resolved = safe_artifact_path(&root_dir, "%2E%2E/secrets.json");

        assert_eq!(resolved, None);
    }
}
