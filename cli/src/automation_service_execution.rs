use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use hyper::body::Incoming;
use hyper::{Request, Response, StatusCode};
use serde_json::{Value, json};

use super::automation_service_artifacts::{
    build_remote_artifact_manifest, encode_relative_path_string_for_url,
};
use super::{
    AppError, DurableJobStore, FailureArtifactsMode, RemoteArtifactIndexEntry, RemoteTestRunJob,
    RemoteTestRunJobStatus, RemoteTestRunRequest, ResponseBody, ServiceJobStoreMode, ServiceState,
    TestRunArgs, decode_remote_test_run_request, json_response, now_timestamp,
    record_usage_command, test_runner,
};

pub(super) async fn handle_submit_test_run(
    request: Request<Incoming>,
    state: ServiceState,
) -> Response<ResponseBody> {
    let submitted = match decode_remote_test_run_request(request).await {
        Ok(submitted) => submitted,
        Err(error) => return json_response(StatusCode::BAD_REQUEST, error),
    };

    if submitted.durable.unwrap_or(false)
        && state.metadata.job_store.mode != ServiceJobStoreMode::Durable
    {
        return json_response(
            StatusCode::BAD_REQUEST,
            json!({
                "error": {
                    "code": "durable_job_store_not_enabled",
                    "message": "Remote test run requested durable job handling, but the service job store is not durable."
                }
            }),
        );
    }

    let request_summary = remote_test_run_request_summary(&submitted);
    let test_run_args = match build_test_run_args(submitted) {
        Ok(args) => args,
        Err(error) => return json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    };
    record_usage_command("test run", state.runtime_context.as_app_context(true));

    let run_id = format!("run-{}", state.next_run_id.fetch_add(1, Ordering::Relaxed));
    let now = now_timestamp();
    let queued_job = RemoteTestRunJob {
        status: RemoteTestRunJobStatus::Queued,
        submitted_at: now.clone(),
        started_at: None,
        updated_at: now,
        completed_at: None,
        command: json!({ "name": "test run" }),
        config: request_summary,
        summary: None,
        summary_ref: None,
        error: None,
        recovery: None,
        runner_root: None,
        runner_root_ref: None,
        artifacts: Vec::new(),
    };
    if let Some(store) = &state.job_store
        && let Err(error) = store.write_job(&run_id, &queued_job)
    {
        return json_response(StatusCode::INTERNAL_SERVER_ERROR, error.payload);
    }
    {
        let mut jobs = state.jobs.lock().expect("jobs lock");
        jobs.insert(run_id.clone(), queued_job);
    }

    let jobs = state.jobs.clone();
    let job_store = state.job_store.clone();
    let run_id_for_task = run_id.clone();
    let runtime_context = state.runtime_context.clone();
    let base_url = state.metadata.base_url.clone();
    tokio::spawn(async move {
        update_job_status(&jobs, job_store.as_ref(), &run_id_for_task, |job| {
            let now = now_timestamp();
            job.status = RemoteTestRunJobStatus::Running;
            job.started_at = Some(now.clone());
            job.updated_at = now;
        });

        let run_result = tokio::task::spawn_blocking(move || {
            let context = runtime_context.as_app_context(true);
            test_runner::execute_run_report(&test_run_args, context)
        })
        .await;

        match run_result {
            Ok(report) => {
                let mut summary = serde_json::to_value(&report).expect("test run summary json");
                let root_dir = summary["artifacts"]["rootDir"]
                    .as_str()
                    .map(PathBuf::from)
                    .unwrap_or_default();
                let manifest =
                    build_remote_artifact_manifest(&root_dir, &base_url, &run_id_for_task);
                summary["remoteArtifacts"] =
                    Value::Array(artifact_index_json(&manifest, &base_url, &run_id_for_task));
                update_job_status(&jobs, job_store.as_ref(), &run_id_for_task, |job| {
                    let now = now_timestamp();
                    job.status = RemoteTestRunJobStatus::Completed;
                    job.updated_at = now.clone();
                    job.completed_at = Some(now);
                    let summary_path = root_dir.join("summary.json");
                    job.summary_ref = Some(
                        job_store
                            .as_ref()
                            .map(|store| store.path_ref(&summary_path))
                            .unwrap_or_else(|| summary_path.display().to_string()),
                    );
                    job.summary = Some(summary);
                    job.runner_root_ref = Some(
                        job_store
                            .as_ref()
                            .map(|store| store.path_ref(&root_dir))
                            .unwrap_or_else(|| root_dir.display().to_string()),
                    );
                    job.runner_root = Some(root_dir);
                    job.artifacts = manifest;
                });
            }
            Err(error) => update_job_status(&jobs, job_store.as_ref(), &run_id_for_task, |job| {
                let now = now_timestamp();
                job.status = RemoteTestRunJobStatus::Failed;
                job.updated_at = now.clone();
                job.completed_at = Some(now);
                job.error = Some(json!({
                    "code": "remote_test_run_join_failed",
                    "message": format!("Remote test run task failed: {error}")
                }));
            }),
        }
    });

    json_response(
        StatusCode::ACCEPTED,
        json!({
            "runId": run_id,
            "status": "queued",
        }),
    )
}

fn build_test_run_args(submitted: RemoteTestRunRequest) -> Result<TestRunArgs, AppError> {
    let input_count =
        usize::from(submitted.path.is_some()) + usize::from(submitted.inline.is_some());
    if input_count > 1 || (input_count == 0 && submitted.profile.is_none()) {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_test_run_request",
                    "message": "Remote test run requests require exactly one of 'path', 'inline', or 'profile'."
                }
            }),
        });
    }

    Ok(TestRunArgs {
        profile: submitted.profile,
        tags: submitted.tags,
        artifacts_dir: submitted.artifacts_dir,
        failure_artifacts: submitted
            .failure_artifacts
            .unwrap_or(FailureArtifactsMode::OnFailure),
        inline: submitted.inline,
        path: submitted.path,
    })
}

fn remote_test_run_request_summary(submitted: &RemoteTestRunRequest) -> Value {
    json!({
        "profile": submitted.profile,
        "tags": submitted.tags,
        "path": submitted.path.as_ref().map(|path| path.display().to_string()),
        "inline": submitted.inline.as_ref().map(|inline| json!({
            "present": true,
            "bytes": inline.len(),
        })),
        "artifactsDir": submitted.artifacts_dir.as_ref().map(|path| path.display().to_string()),
        "failureArtifacts": submitted.failure_artifacts.map(|mode| mode.to_string()),
        "durable": submitted.durable.unwrap_or(false),
    })
}

fn update_job_status(
    jobs: &Arc<Mutex<std::collections::BTreeMap<String, RemoteTestRunJob>>>,
    job_store: Option<&DurableJobStore>,
    run_id: &str,
    update: impl FnOnce(&mut RemoteTestRunJob),
) {
    let mut jobs = jobs.lock().expect("jobs lock");
    if let Some(job) = jobs.get_mut(run_id) {
        update(job);
        if let Some(store) = job_store {
            let _ = store.write_job(run_id, job);
            let _ = store.write_artifacts(run_id, &job.artifacts);
        }
    }
}

pub(super) fn artifact_index_json(
    artifacts: &[RemoteArtifactIndexEntry],
    base_url: &str,
    run_id: &str,
) -> Vec<Value> {
    artifacts
        .iter()
        .map(|artifact| {
            json!({
                "relativePath": artifact.relative_path,
                "sizeBytes": artifact.size_bytes,
                "modifiedUnixMs": artifact.modified_unix_ms,
                "downloadUrl": format!(
                    "{base_url}/v0/test-runs/{run_id}/artifacts/{}",
                    encode_relative_path_string_for_url(&artifact.relative_path)
                ),
            })
        })
        .collect()
}

pub(super) fn refresh_job_artifact_urls(job: &mut RemoteTestRunJob, base_url: &str, run_id: &str) {
    if job.artifacts.is_empty() {
        return;
    }

    let remote_artifacts = Value::Array(artifact_index_json(&job.artifacts, base_url, run_id));
    if let Some(summary) = &mut job.summary {
        summary["remoteArtifacts"] = remote_artifacts;
    }
}
