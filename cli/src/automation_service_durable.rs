use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;
use serde_json::json;

use super::{
    AppError, DurableJobRecovery, DurableJobStore, RemoteArtifactIndexEntry, RemoteTestRunJob,
    RemoteTestRunJobStatus, job_status_name, now_timestamp, refresh_job_artifact_urls,
    relative_path_string,
};

impl DurableJobStore {
    pub(super) fn initialize(root: PathBuf, artifact_root: PathBuf) -> Result<Self, AppError> {
        fs::create_dir_all(&root).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "durable_job_store_init_failed",
                    "message": format!("Failed to initialize durable job store '{}': {source}", root.display())
                }
            }),
        })?;
        Ok(Self {
            root,
            artifact_root,
        })
    }

    pub(super) fn write_job(&self, run_id: &str, job: &RemoteTestRunJob) -> Result<(), AppError> {
        let job_dir = self.job_dir(run_id)?;
        fs::create_dir_all(&job_dir).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "durable_job_store_write_failed",
                    "message": format!("Failed to create durable job directory '{}': {source}", job_dir.display())
                }
            }),
        })?;
        atomic_write_json(&job_dir.join("job.json"), job)
    }

    pub(super) fn write_artifacts(
        &self,
        run_id: &str,
        artifacts: &[RemoteArtifactIndexEntry],
    ) -> Result<(), AppError> {
        let job_dir = self.job_dir(run_id)?;
        fs::create_dir_all(&job_dir).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "durable_job_store_write_failed",
                    "message": format!("Failed to create durable job directory '{}': {source}", job_dir.display())
                }
            }),
        })?;
        atomic_write_json(&job_dir.join("artifacts.json"), artifacts)
    }

    pub(super) fn recover_jobs(&self, base_url: &str) -> Result<DurableJobRecovery, AppError> {
        let mut recovery = DurableJobRecovery::default();
        let entries = fs::read_dir(&self.root).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "durable_job_store_recovery_failed",
                    "message": format!("Failed to read durable job store '{}': {source}", self.root.display())
                }
            }),
        })?;
        let recovered_at = now_timestamp();

        for entry in entries.flatten() {
            let Ok(file_type) = entry.file_type() else {
                continue;
            };
            if !file_type.is_dir() {
                continue;
            }

            let run_id = entry.file_name().to_string_lossy().to_string();
            if validate_run_id(&run_id).is_err() {
                recovery.metadata.warnings.push(json!({
                    "code": "invalid_remote_run_id",
                    "runId": run_id,
                    "message": "Skipped durable job directory with an invalid run id."
                }));
                continue;
            }
            if let Some(counter) = parse_run_counter(&run_id) {
                recovery.next_run_counter = recovery.next_run_counter.max(counter + 1);
            }

            let job_path = entry.path().join("job.json");
            let mut job = match fs::read(&job_path)
                .map_err(|source| source.to_string())
                .and_then(|bytes| {
                    serde_json::from_slice::<RemoteTestRunJob>(&bytes)
                        .map_err(|source| source.to_string())
                }) {
                Ok(job) => job,
                Err(message) => {
                    recovery.metadata.unknown_jobs += 1;
                    recovery.metadata.recovered_jobs += 1;
                    let unknown_job =
                        unknown_recovered_job(&run_id, &recovered_at, message.clone());
                    recovery.metadata.warnings.push(json!({
                        "code": "durable_job_metadata_unreadable",
                        "runId": run_id,
                        "message": "Recovered durable job metadata could not be parsed; the job was exposed with unknown status.",
                        "detail": message,
                    }));
                    self.write_job(&run_id, &unknown_job)?;
                    recovery.jobs.insert(run_id.clone(), unknown_job);
                    continue;
                }
            };

            if let Ok(bytes) = fs::read(entry.path().join("artifacts.json"))
                && let Ok(artifacts) =
                    serde_json::from_slice::<Vec<RemoteArtifactIndexEntry>>(&bytes)
            {
                job.artifacts = artifacts;
            }
            if job.runner_root.is_none() {
                job.runner_root = job
                    .runner_root_ref
                    .as_ref()
                    .map(|reference| self.artifact_root.join(reference));
            }

            match &job.status {
                RemoteTestRunJobStatus::Queued | RemoteTestRunJobStatus::Running => {
                    recovery.metadata.orphaned_jobs += 1;
                    let previous_status = job_status_name(&job.status);
                    job.status = RemoteTestRunJobStatus::Orphaned;
                    job.updated_at = recovered_at.clone();
                    job.completed_at = Some(recovered_at.clone());
                    job.error = Some(json!({
                        "code": "remote_test_run_orphaned",
                        "message": "Remote test run was queued or running when the service stopped; it was marked orphaned during durable job recovery."
                    }));
                    job.recovery = Some(json!({
                        "recoveredAt": recovered_at.clone(),
                        "previousStatus": previous_status
                    }));
                    self.write_job(&run_id, &job)?;
                }
                RemoteTestRunJobStatus::Unknown => {
                    recovery.metadata.unknown_jobs += 1;
                }
                RemoteTestRunJobStatus::Completed
                | RemoteTestRunJobStatus::Failed
                | RemoteTestRunJobStatus::Canceled
                | RemoteTestRunJobStatus::Orphaned => {}
            }

            refresh_job_artifact_urls(&mut job, base_url, &run_id);
            recovery.metadata.recovered_jobs += 1;
            recovery.jobs.insert(run_id, job);
        }

        Ok(recovery)
    }

    pub(super) fn path_ref(&self, path: &Path) -> String {
        if let Ok(relative) = path.strip_prefix(&self.artifact_root) {
            return relative_path_string(relative);
        }
        path.display().to_string()
    }

    fn job_dir(&self, run_id: &str) -> Result<PathBuf, AppError> {
        validate_run_id(run_id).map_err(|message| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_remote_run_id",
                    "message": message
                }
            }),
        })?;
        Ok(self.root.join(run_id))
    }
}

fn unknown_recovered_job(run_id: &str, recovered_at: &str, detail: String) -> RemoteTestRunJob {
    RemoteTestRunJob {
        status: RemoteTestRunJobStatus::Unknown,
        submitted_at: recovered_at.to_string(),
        started_at: None,
        updated_at: recovered_at.to_string(),
        completed_at: Some(recovered_at.to_string()),
        command: json!({ "name": "test run" }),
        config: json!({ "durable": true, "recovered": true }),
        summary: None,
        summary_ref: None,
        error: Some(json!({
            "code": "durable_job_metadata_unreadable",
            "message": format!("Durable metadata for remote test run '{run_id}' could not be parsed."),
            "detail": detail,
        })),
        recovery: Some(json!({
            "recoveredAt": recovered_at,
            "status": "unknown"
        })),
        runner_root: None,
        runner_root_ref: None,
        artifacts: Vec::new(),
    }
}

fn parse_run_counter(run_id: &str) -> Option<u64> {
    run_id.strip_prefix("run-")?.parse().ok()
}

fn validate_run_id(run_id: &str) -> Result<(), String> {
    if run_id.is_empty()
        || !run_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err(format!(
            "Remote test run id '{run_id}' is invalid for durable storage."
        ));
    }
    Ok(())
}

fn atomic_write_json<T: Serialize + ?Sized>(path: &Path, value: &T) -> Result<(), AppError> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("job.json");
    let temp_path = parent.join(format!(
        ".{file_name}.tmp-{}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default()
    ));
    let bytes = serde_json::to_vec_pretty(value).expect("durable job json");
    fs::write(&temp_path, bytes).map_err(|source| durable_write_error(&temp_path, source))?;
    fs::rename(&temp_path, path).map_err(|source| durable_write_error(path, source))?;
    Ok(())
}

fn durable_write_error(path: &Path, source: std::io::Error) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "durable_job_store_write_failed",
                "message": format!("Failed to write durable job metadata '{}': {source}", path.display())
            }
        }),
    }
}
