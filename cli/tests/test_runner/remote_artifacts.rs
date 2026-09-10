use super::*;
#[cfg(unix)]
#[test]
fn remote_artifact_index_urls_refs_and_traversal_rejection() {
    let store_dir = format!(
        "remote-artifact-index-{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos()
    );
    let (mut child, startup) = spawn_artifact_index_service(&store_dir);
    let base_url = startup["baseUrl"].as_str().expect("baseUrl");

    let submitted = remote_artifact_index_request(
        reqwest::Method::POST,
        &format!("{base_url}/v0/test-runs"),
        Some(json!({
            "inline": "{name: remote-artifact-index, steps: [game.info]}",
            "durable": true
        })),
    );
    assert_eq!(submitted.status(), 202);
    let submitted_payload: Value = submitted.json().expect("submitted json");
    let run_id = submitted_payload["runId"].as_str().expect("runId");
    let completed = wait_for_remote_artifact_index_completed(base_url, run_id);
    let remote_artifacts = completed["summary"]["remoteArtifacts"]
        .as_array()
        .expect("remote artifacts");
    let summary = remote_artifacts
        .iter()
        .find(|artifact| artifact["relativePath"] == "summary.json")
        .expect("summary artifact");
    assert!(summary["sizeBytes"].is_number());
    assert!(summary["modifiedUnixMs"].is_number());
    assert_eq!(
        summary["downloadUrl"],
        format!("{base_url}/v0/test-runs/{run_id}/artifacts/summary.json")
    );
    assert!(remote_artifacts.iter().any(|artifact| {
        artifact["relativePath"]
            .as_str()
            .expect("relative path")
            .ends_with("result.json")
    }));

    let manifest = remote_artifact_index_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/test-runs/{run_id}/artifacts"),
        None,
    );
    assert_eq!(manifest.status(), 200);
    let manifest_payload: Value = manifest.json().expect("manifest json");
    assert_eq!(
        manifest_payload["artifacts"][0]["relativePath"],
        remote_artifacts[0]["relativePath"]
    );

    let traversal = remote_artifact_index_request(
        reqwest::Method::GET,
        &format!("{base_url}/v0/test-runs/{run_id}/artifacts/%2E%2E%2Fsummary.json"),
        None,
    );
    assert_eq!(traversal.status(), 400);
    assert_eq!(
        traversal.json::<Value>().expect("traversal json")["error"]["code"],
        "invalid_artifact_path"
    );

    stop_artifact_index_service(&mut child);
}
