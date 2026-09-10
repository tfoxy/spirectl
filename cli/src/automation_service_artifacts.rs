use std::fs;
use std::path::{Component, Path, PathBuf};
use std::time::UNIX_EPOCH;

use bytes::Bytes;
use http_body_util::Full;
use hyper::header::{CONTENT_TYPE, HeaderValue};
use hyper::{Response, StatusCode};
use percent_encoding::{percent_decode_str, utf8_percent_encode};
use serde_json::json;

use super::{
    RemoteArtifactIndexEntry, ResponseBody, URL_PATH_SEGMENT_ENCODE_SET, json_response,
    relative_path_string,
};

pub(super) fn build_remote_artifact_manifest(
    root_dir: &Path,
    base_url: &str,
    run_id: &str,
) -> Vec<RemoteArtifactIndexEntry> {
    if !root_dir.exists() {
        return Vec::new();
    }

    let mut files = Vec::new();
    collect_artifact_files(root_dir, root_dir, &mut files);
    files.sort_by(|left, right| left.relative_path.cmp(&right.relative_path));
    for file in &mut files {
        file.download_url = Some(format!(
            "{base_url}/v0/test-runs/{run_id}/artifacts/{}",
            encode_relative_path_string_for_url(&file.relative_path)
        ));
    }
    files
}

fn collect_artifact_files(
    root_dir: &Path,
    current_dir: &Path,
    files: &mut Vec<RemoteArtifactIndexEntry>,
) {
    let Ok(entries) = fs::read_dir(current_dir) else {
        return;
    };

    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect_artifact_files(root_dir, &path, files);
            continue;
        }

        let Ok(relative) = path.strip_prefix(root_dir) else {
            continue;
        };
        let metadata = fs::metadata(&path).ok();
        let modified_unix_ms = metadata
            .as_ref()
            .and_then(|metadata| metadata.modified().ok())
            .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
            .map(|duration| duration.as_millis());
        files.push(RemoteArtifactIndexEntry {
            relative_path: relative_path_string(relative),
            size_bytes: metadata.as_ref().map(|metadata| metadata.len()),
            modified_unix_ms,
            download_url: None,
        });
    }
}

pub(super) fn encode_relative_path_string_for_url(path: &str) -> String {
    path.split('/')
        .map(|component| utf8_percent_encode(component, URL_PATH_SEGMENT_ENCODE_SET).to_string())
        .collect::<Vec<_>>()
        .join("/")
}

pub(super) fn serve_artifact_file(
    run_id: &str,
    root_dir: &Path,
    artifacts: &[RemoteArtifactIndexEntry],
    relative_path: &str,
) -> Response<ResponseBody> {
    let Some((decoded_relative_path, resolved_path)) = safe_artifact_path(root_dir, relative_path)
    else {
        return json_response(
            StatusCode::BAD_REQUEST,
            json!({
                "error": {
                    "code": "invalid_artifact_path",
                    "message": format!("Remote test run artifact path '{}' is invalid.", relative_path)
                }
            }),
        );
    };

    if !artifacts
        .iter()
        .any(|artifact| artifact.relative_path == decoded_relative_path)
    {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "remote_test_run_artifact_not_indexed",
                    "message": format!("Artifact '{}' is not indexed for remote test run '{}'.", decoded_relative_path, run_id)
                }
            }),
        );
    }

    match fs::read(&resolved_path) {
        Ok(bytes) => {
            let mut response = Response::new(Full::new(Bytes::from(bytes)));
            *response.status_mut() = StatusCode::OK;
            response.headers_mut().insert(
                CONTENT_TYPE,
                HeaderValue::from_static(content_type_for_path(&resolved_path)),
            );
            response
        }
        Err(_) => json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "remote_test_run_artifact_not_found",
                    "message": format!("Artifact '{}' was not found for remote test run '{}'.", relative_path, run_id)
                }
            }),
        ),
    }
}

pub(super) fn safe_artifact_path(
    root_dir: &Path,
    relative_path: &str,
) -> Option<(String, PathBuf)> {
    let mut resolved = PathBuf::new();
    for segment in relative_path.split('/') {
        if segment.is_empty() {
            return None;
        }

        let decoded = percent_decode_str(segment).decode_utf8().ok()?;
        let decoded_path = Path::new(decoded.as_ref());
        if decoded_path.is_absolute()
            || decoded_path.components().any(|component| {
                matches!(
                    component,
                    Component::ParentDir | Component::RootDir | Component::Prefix(_)
                )
            })
        {
            return None;
        }

        let mut components = decoded_path.components();
        let Some(Component::Normal(component)) = components.next() else {
            return None;
        };
        if components.next().is_some() {
            return None;
        }

        resolved.push(component);
    }

    if resolved.as_os_str().is_empty() {
        return None;
    }

    let decoded_relative_path = relative_path_string(&resolved);
    Some((decoded_relative_path, root_dir.join(resolved)))
}

pub(super) fn content_type_for_path(path: &Path) -> &'static str {
    match path.extension().and_then(|extension| extension.to_str()) {
        Some("json") => "application/json",
        Some("png") => "image/png",
        _ => "application/octet-stream",
    }
}
