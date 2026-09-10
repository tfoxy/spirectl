use crate::{AppError, bridge};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use serde_yaml::Value as YamlValue;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone)]
pub(crate) struct PreparedFixture {
    pub requested_path: String,
    pub resolved_path: PathBuf,
    pub document: FixtureDocument,
    pub fixture_json: String,
}

impl PreparedFixture {
    pub fn bridge_request(&self, request_id: &str) -> bridge::proto::FixtureLoadRequest {
        bridge::proto::FixtureLoadRequest {
            request_id: request_id.to_string(),
            schema_version: self.document.schema_version.clone(),
            fixture_name: self.document.name.clone(),
            source_path: self.resolved_path.display().to_string(),
            fixture_json: self.fixture_json.clone(),
        }
    }

    pub fn success_json(&self, response: &bridge::proto::FixtureLoadResponse) -> Value {
        let recipe_report = recipe_report_json(response.recipe_report.as_ref());
        json!({
            "requestedPath": self.requested_path,
            "resolvedPath": self.resolved_path.display().to_string(),
            "fixture": {
                "schemaVersion": self.document.schema_version,
                "name": self.document.name,
                "screen": self.document.screen,
            },
            "source": bridge::data_source_name(
                bridge::proto::DataSource::try_from(response.source)
                    .ok()
                    .unwrap_or(bridge::proto::DataSource::Unspecified),
            ),
            "provisional": response.provisional,
            "recipeReport": recipe_report.clone(),
            "loaded": {
                "screen": {
                    "id": response.screen.as_ref().map(|screen| screen.id.clone()).unwrap_or_default(),
                    "instanceId": response
                        .screen
                        .as_ref()
                        .map(|screen| screen.screen_instance_id.clone())
                        .unwrap_or_default(),
                },
                "resolvedPerspective": response
                    .resolved_perspective
                    .as_ref()
                    .map(|perspective| json!({
                        "scope": bridge::perspective_scope_name(
                            bridge::proto::PerspectiveScope::try_from(perspective.scope)
                                .ok()
                                .unwrap_or(bridge::proto::PerspectiveScope::Unspecified),
                        ),
                        "playerId": perspective.player_id,
                        "usesDefault": perspective.uses_default,
                    }))
                    .unwrap_or_else(|| json!({
                        "scope": "unspecified",
                        "playerId": "",
                        "usesDefault": false,
                    })),
                "notices": response
                    .notices
                    .iter()
                    .map(|notice| json!({
                        "code": notice.code,
                        "message": notice.message,
                        "provisional": notice.provisional,
                    }))
                    .collect::<Vec<_>>(),
                "recipeReport": recipe_report,
            }
        })
    }
}

pub(crate) fn prepare_fixture(
    path: &Path,
    base_dir: Option<&Path>,
) -> Result<PreparedFixture, AppError> {
    let requested_path = path.display().to_string();
    let resolved_path = resolve_fixture_path(path, base_dir)?;
    let raw = fs::read_to_string(&resolved_path)
        .map_err(|source| AppError::fixture_read(&resolved_path, &source))?;
    let raw_yaml: YamlValue = serde_yaml::from_str(&raw)
        .map_err(|source| AppError::fixture_parse(&resolved_path, &source))?;
    reject_unsupported_fixture_fields(&resolved_path, &raw_yaml)?;
    let parsed: FixtureDocumentInput = serde_yaml::from_str(&raw)
        .map_err(|source| AppError::fixture_parse(&resolved_path, &source))?;

    let document = parsed.normalize(&resolved_path)?;
    let fixture_json = serde_json::to_string(&document).map_err(|source| {
        AppError::invalid_fixture(
            &resolved_path,
            &format!("failed to serialize normalized fixture: {source}"),
            &[],
        )
    })?;

    Ok(PreparedFixture {
        requested_path,
        resolved_path,
        document,
        fixture_json,
    })
}

pub(crate) fn resolve_fixture_path(
    path: &Path,
    base_dir: Option<&Path>,
) -> Result<PathBuf, AppError> {
    let candidate = if path.is_absolute() {
        path.to_path_buf()
    } else if let Some(base_dir) = base_dir {
        base_dir.join(path)
    } else {
        std::env::current_dir()
            .map_err(|source| AppError::fixture_read(path, &source))?
            .join(path)
    };
    let absolute = if candidate.is_absolute() {
        candidate
    } else {
        std::env::current_dir()
            .map_err(|source| AppError::fixture_read(path, &source))?
            .join(candidate)
    };
    Ok(normalize_path(absolute))
}

fn normalize_path(path: PathBuf) -> PathBuf {
    use std::path::Component;

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

fn validation_detail(field: &str, value: impl Into<String>, note: &str) -> Value {
    let reason_code = validation_reason_code(field, note);
    let value = value.into();
    json!({
        "field": field,
        "fieldPath": field,
        "value": value,
        "valueSummary": value,
        "reasonCode": reason_code,
        "note": note,
        "message": note,
    })
}

fn validation_reason_code(field: &str, note: &str) -> &'static str {
    let lower_note = note.to_ascii_lowercase();
    if lower_note.contains("hidden")
        || lower_note.contains("rng")
        || lower_note.contains("animation")
        || lower_note.contains("enemy ai")
        || lower_note.contains("transient")
    {
        "unsupported-hidden-state"
    } else if lower_note.contains("remote-client") || lower_note.contains("remote client") {
        "degraded-local-multiplayer"
    } else if lower_note.contains("sidecar")
        || lower_note.contains("scenario artifact")
        || lower_note.contains("not supported")
        || lower_note.contains("do not use")
    {
        "unsupported-recipe-field"
    } else if lower_note.contains("hp <= maxhp") {
        "hp_exceeds_max_hp"
    } else if field == "run.actFloor" {
        "invalid_run_floor"
    } else if field == "run.currentActIndex" {
        "invalid_run_act"
    } else if field.contains("lobby")
        || (field.contains("players") && !field.contains(".overlays"))
        || field.contains("view.playerId")
    {
        "invalid_lobby_player"
    } else if field == "screen" || field == "rootScene" {
        "unknown_screen_id"
    } else if lower_note.contains("must not be empty") {
        "empty_fixture_id"
    } else {
        "invalid-authored-value"
    }
}

fn recipe_report_json(report: Option<&bridge::proto::FixtureRecipeRestoreReport>) -> Value {
    let Some(report) = report else {
        return json!({
            "recipeName": "",
            "appliedFields": [],
            "inferredFields": [],
            "omittedFields": [],
            "unsupportedFields": [],
            "degradedMultiplayerFields": [],
            "bridgeValidation": {
                "status": "unspecified",
                "details": [],
            },
        });
    };

    json!({
        "recipeName": report.recipe_name,
        "appliedFields": field_reports_json(&report.applied_fields),
        "inferredFields": field_reports_json(&report.inferred_fields),
        "omittedFields": field_reports_json(&report.omitted_fields),
        "unsupportedFields": field_reports_json(&report.unsupported_fields),
        "degradedMultiplayerFields": field_reports_json(&report.degraded_multiplayer_fields),
        "bridgeValidation": report.bridge_validation.as_ref()
            .map(bridge_validation_json)
            .unwrap_or_else(|| json!({
                "status": "unspecified",
                "details": [],
            })),
    })
}

fn bridge_validation_json(validation: &bridge::proto::FixtureBridgeValidationResult) -> Value {
    json!({
        "status": validation.status,
        "details": field_reports_json(&validation.details),
    })
}

fn field_reports_json(reports: &[bridge::proto::FixtureRecipeFieldReport]) -> Vec<Value> {
    reports
        .iter()
        .map(|report| {
            json!({
                "fieldPath": report.field_path,
                "valueSummary": report.value_summary,
                "reasonCode": report.reason_code,
                "message": report.message,
            })
        })
        .collect()
}

fn yaml_value_string(value: &YamlValue) -> String {
    serde_yaml::to_string(value)
        .unwrap_or_else(|_| "<unrenderable>".to_string())
        .trim()
        .to_string()
}

fn reject_unsupported_fixture_fields(
    resolved_path: &Path,
    value: &YamlValue,
) -> Result<(), AppError> {
    fn visit(resolved_path: &Path, value: &YamlValue, path: &str) -> Result<(), AppError> {
        let YamlValue::Mapping(mapping) = value else {
            return Ok(());
        };
        for (key, child) in mapping {
            let Some(key) = key.as_str() else {
                continue;
            };
            let field_path = if path.is_empty() {
                key.to_string()
            } else {
                format!("{path}.{key}")
            };
            let note = match key {
                "exactSidecar" | "sidecar" | "sidecarPath" => {
                    Some("Exact sidecar loading is not supported inside authored fixture recipes.")
                }
                "scenarioArtifact" | "scenarioArtifacts" | "artifactPath" => Some(
                    "Scenario artifact fields are not supported inside authored fixture recipes.",
                ),
                "hiddenCombatQueue" | "hiddenCombatQueues" | "combatQueue" | "combatQueues" => {
                    Some("Hidden combat queues are unsupported hidden state in fixture recipes.")
                }
                "rngContinuation" | "rngState" | "rngSeedContinuation" => {
                    Some("RNG continuation is unsupported hidden state in fixture recipes.")
                }
                "animationState" | "animations" => Some(
                    "Animation state is unsupported transient runtime state in fixture recipes.",
                ),
                "enemyAiHistory" | "enemyAIHistory" | "enemyHistory" => {
                    Some("Enemy AI history is unsupported hidden state in fixture recipes.")
                }
                "transientUi" | "transientUI" | "nodePath" | "sceneNodeId" => {
                    Some("Transient UI internals are unsupported hidden state in fixture recipes.")
                }
                "remoteClients" | "remoteClient" | "remoteClientOrchestration" => Some(
                    "Remote-client orchestration is not supported; author remote player metadata only for local degraded multiplayer examples.",
                ),
                _ => None,
            };
            if let Some(note) = note {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture includes unsupported recipe fields.",
                    &[validation_detail(
                        &field_path,
                        yaml_value_string(child),
                        note,
                    )],
                ));
            }
            visit(resolved_path, child, &field_path)?;
        }
        Ok(())
    }

    visit(resolved_path, value, "")
}

fn str_trimmed(value: String) -> String {
    value.trim().to_string()
}

include!("document.rs");

#[cfg(test)]
mod tests;
