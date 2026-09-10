use std::path::{Component, Path, PathBuf};

use serde_json::json;

use crate::AppError;

/// Resolves the durable metadata root without creating it. Keeping this path policy separate from
/// recovery and request handling makes the artifact-root containment rule available at startup.
pub(super) fn resolve_root(
    override_dir: Option<&Path>,
    artifact_root: &Path,
) -> Result<PathBuf, AppError> {
    let artifact_root = normalize_lexical(artifact_root);
    let root = match override_dir {
        Some(path) if path.is_absolute() => normalize_lexical(path),
        Some(path) => normalize_lexical(&artifact_root.join(path)),
        None => artifact_root.join("remote-jobs"),
    };

    if !root.starts_with(&artifact_root) || has_parent_component(override_dir.unwrap_or(&root)) {
        return Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "unsafe_job_store_dir",
                    "message": "Durable job-store roots must stay under artifacts.dir and must not contain '..' path components."
                }
            }),
        });
    }

    Ok(root)
}

fn normalize_lexical(path: &Path) -> PathBuf {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            other => normalized.push(other.as_os_str()),
        }
    }
    normalized
}

fn has_parent_component(path: &Path) -> bool {
    path.components()
        .any(|component| matches!(component, Component::ParentDir))
}
