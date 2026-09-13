use super::*;
use crate::models::character_model_json;

pub(crate) fn execute_reference_json(
    args: ReferenceCommand,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .reference(bridge::proto::ReferenceRequest {
            request_id: "cli-reference-1".to_string(),
            topic: args.topic,
            keys: args.keys,
        })
        .map_err(AppError::bridge)?;

    Ok(reference_response_json(&response))
}

pub(crate) fn reference_response_json(response: &bridge::proto::ReferenceResponse) -> Value {
    let mut value = json!({
        "requestId": response.request_id,
        "schemaVersion": "spirectl.reference-result/v0",
        "source": bridge::data_source_name(bridge::enum_value(response.source)),
        "provisional": response.provisional,
        "topic": response.topic,
        "status": reference_status_name(bridge::enum_value(response.status)),
        "missingKeys": response.missing_keys,
        "notices": response.notices.iter().map(reference_notice_json).collect::<Vec<_>>(),
    });

    let object = value.as_object_mut().expect("reference response object");
    match response.payload.as_ref() {
        Some(bridge::proto::reference_response::Payload::Colors(colors)) => {
            object.insert("colors".to_string(), game_colors_json(colors));
        }
        Some(bridge::proto::reference_response::Payload::Version(version)) => {
            object.insert(
                "version".to_string(),
                empty_string_to_json(&version.version),
            );
            object.insert(
                "versionDate".to_string(),
                empty_string_to_json(&version.version_date),
            );
            object.insert("commit".to_string(), empty_string_to_json(&version.commit));
            object.insert("branch".to_string(), empty_string_to_json(&version.branch));
            object.insert(
                "mainAssemblyHash".to_string(),
                json!(version.main_assembly_hash),
            );
            // `branch` above is the game's own release TAG; these two are the Steam install identity,
            // which is the pair that distinguishes two installs of the same product whose content
            // differs. Null rather than "" when undeterminable, matching every other optional field here.
            object.insert(
                "steamBranch".to_string(),
                empty_string_to_json(&version.steam_branch),
            );
            object.insert("steamBuildId".to_string(), json!(version.steam_build_id));
            object.insert(
                "steamBranchSource".to_string(),
                empty_string_to_json(&version.steam_branch_source),
            );
            object.insert(
                "modding".to_string(),
                version
                    .modding
                    .as_ref()
                    .map(modding_summary_json)
                    .unwrap_or(Value::Null),
            );
        }
        Some(bridge::proto::reference_response::Payload::RandomCharacter(character)) => {
            // Reuse the SAME character-model flatten `models` uses (the synthetic Random
            // Character lobby entry rides CharacterModelInfo but is not a real "characters"
            // model-catalog entry, so it never appears through `sts2 models characters`).
            object.insert(
                "randomCharacter".to_string(),
                character_model_json(character, "characters"),
            );
        }
        None => {}
    }

    value
}

// Flat `{ name: "#RRGGBB" | "#RRGGBBAA" }` map of the StsColors palette. The bridge already emits
// the hex (8 digits when alpha<1); we only prefix `#` and uppercase so the value drops straight into
// a `color()`/web hex consumer. The float r/g/b/a components are intentionally dropped — consumers
// parse the hex themselves (the catalog's `hexColor()` CEL helper does).
fn game_colors_json(colors: &bridge::proto::GameColors) -> Value {
    Value::Object(
        colors
            .colors
            .iter()
            .map(|color| {
                (
                    color.name.clone(),
                    Value::String(format!("#{}", color.hex.to_uppercase())),
                )
            })
            .collect::<serde_json::Map<String, Value>>(),
    )
}

fn modding_summary_json(modding: &bridge::proto::ModdingSummary) -> Value {
    json!({
        "isRunningModded": modding.is_running_modded,
        "loadedModCount": modding.loaded_mod_count,
        "totalModCount": modding.total_mod_count,
    })
}

fn reference_notice_json(notice: &bridge::proto::ReferenceNotice) -> Value {
    json!({
        "code": notice.code,
        "severity": notice.severity,
        "message": notice.message,
        "path": empty_string_to_json(&notice.path),
    })
}

fn reference_status_name(status: bridge::proto::ReferenceStatus) -> &'static str {
    match status {
        bridge::proto::ReferenceStatus::Ok => "ok",
        bridge::proto::ReferenceStatus::Partial => "partial",
        bridge::proto::ReferenceStatus::UnsupportedTopic => "unsupported-topic",
        bridge::proto::ReferenceStatus::Unavailable => "unavailable",
        bridge::proto::ReferenceStatus::Unspecified => "unspecified",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn version_response() -> bridge::proto::ReferenceResponse {
        bridge::proto::ReferenceResponse {
            request_id: "cli-reference-1".to_string(),
            source: bridge::proto::DataSource::Live as i32,
            provisional: false,
            topic: "version".to_string(),
            status: bridge::proto::ReferenceStatus::Ok as i32,
            missing_keys: vec![],
            notices: vec![],
            payload: Some(bridge::proto::reference_response::Payload::Version(
                bridge::proto::GameVersionInfo {
                    version: "v0.103.2".to_string(),
                    version_date: "2026.04.16".to_string(),
                    commit: "abc123".to_string(),
                    branch: "main".to_string(),
                    main_assembly_hash: 42,
                    steam_branch: "public-beta".to_string(),
                    steam_build_id: 23811903,
                    steam_branch_source: "steamworks".to_string(),
                    modding: Some(bridge::proto::ModdingSummary {
                        is_running_modded: true,
                        loaded_mod_count: 2,
                        total_mod_count: 2,
                    }),
                },
            )),
        }
    }

    fn colors_response() -> bridge::proto::ReferenceResponse {
        bridge::proto::ReferenceResponse {
            request_id: "cli-reference-1".to_string(),
            source: bridge::proto::DataSource::Live as i32,
            provisional: false,
            topic: "colors".to_string(),
            status: bridge::proto::ReferenceStatus::Partial as i32,
            missing_keys: vec!["nope".to_string()],
            notices: vec![],
            payload: Some(bridge::proto::reference_response::Payload::Colors(
                bridge::proto::GameColors {
                    colors: vec![bridge::proto::GameColor {
                        name: "screenBackdrop".to_string(),
                        hex: "000000cc".to_string(),
                        r: 0.0,
                        g: 0.0,
                        b: 0.0,
                        a: 0.8,
                    }],
                },
            )),
        }
    }

    fn random_character_response() -> bridge::proto::ReferenceResponse {
        bridge::proto::ReferenceResponse {
            request_id: "cli-reference-1".to_string(),
            source: bridge::proto::DataSource::Live as i32,
            provisional: false,
            topic: "randomCharacter".to_string(),
            status: bridge::proto::ReferenceStatus::Ok as i32,
            missing_keys: vec![],
            notices: vec![],
            payload: Some(bridge::proto::reference_response::Payload::RandomCharacter(
                bridge::proto::CharacterModelInfo {
                    id: "RANDOM_CHARACTER".to_string(),
                    character_select_icon_asset_key:
                        "res://images/packed/character_select/char_select_random.png".to_string(),
                    character_select_locked_icon_asset_key:
                        "res://images/packed/character_select/char_select_random_locked.png"
                            .to_string(),
                    character_select_bg_path:
                        "res://scenes/screens/char_select/char_select_bg_random_character.tscn"
                            .to_string(),
                    character_select_title_loc: Some(bridge::proto::ModelLocalizationRef {
                        table: "characters".to_string(),
                        key: "RANDOM_CHARACTER.name".to_string(),
                    }),
                    ..Default::default()
                },
            )),
        }
    }

    #[test]
    fn random_character_payload_is_flattened() {
        let value = reference_response_json(&random_character_response());
        assert_eq!(value["topic"], "randomCharacter");
        assert_eq!(value["status"], "ok");
        let random_character = &value["randomCharacter"];
        assert_eq!(random_character["family"], "characters");
        assert_eq!(random_character["id"], "RANDOM_CHARACTER");
        assert_eq!(
            random_character["characterSelectIconAssetKey"],
            "res://images/packed/character_select/char_select_random.png"
        );
        assert_eq!(
            random_character["characterSelectLockedIconAssetKey"],
            "res://images/packed/character_select/char_select_random_locked.png"
        );
        assert_ne!(
            random_character["characterSelectIconAssetKey"],
            random_character["characterSelectLockedIconAssetKey"],
            "the locked icon must be the distinct char_select_random_locked.png asset, not a reuse of the unlocked icon"
        );
        assert_eq!(
            random_character["characterSelectTitle"]["table"],
            "characters"
        );
        assert_eq!(
            random_character["characterSelectTitle"]["key"],
            "RANDOM_CHARACTER.name"
        );
        assert!(value.get("colors").is_none());
        assert!(value.get("version").is_none());
    }

    #[test]
    fn version_payload_is_flattened() {
        let value = reference_response_json(&version_response());
        assert_eq!(value["schemaVersion"], "spirectl.reference-result/v0");
        assert_eq!(value["topic"], "version");
        assert_eq!(value["status"], "ok");
        assert_eq!(value["version"], "v0.103.2");
        assert_eq!(value["versionDate"], "2026.04.16");
        assert_eq!(value["commit"], "abc123");
        assert_eq!(value["mainAssemblyHash"], 42);
        // The release tag and the Steam install identity are separate answers and both ride the payload.
        assert_eq!(value["branch"], "main");
        assert_eq!(value["steamBranch"], "public-beta");
        assert_eq!(value["steamBuildId"], 23811903);
        assert_eq!(value["steamBranchSource"], "steamworks");
        assert_eq!(value["modding"]["isRunningModded"], true);
        assert_eq!(value["modding"]["loadedModCount"], 2);
        assert_eq!(value["modding"]["totalModCount"], 2);
        assert!(value.get("colors").is_none());
    }

    #[test]
    fn colors_payload_is_a_flat_hex_map_with_missing_keys() {
        let value = reference_response_json(&colors_response());
        assert_eq!(value["topic"], "colors");
        assert_eq!(value["status"], "partial");
        assert_eq!(value["missingKeys"][0], "nope");
        assert_eq!(value["colors"]["screenBackdrop"], "#000000CC");
        assert!(
            value["colors"].get("name").is_none(),
            "should be a flat map, not an array of objects"
        );
        assert!(value.get("version").is_none());
    }

    #[test]
    fn unsupported_topic_has_no_payload() {
        let response = bridge::proto::ReferenceResponse {
            request_id: "cli-reference-1".to_string(),
            source: bridge::proto::DataSource::Live as i32,
            provisional: false,
            topic: "bogus".to_string(),
            status: bridge::proto::ReferenceStatus::UnsupportedTopic as i32,
            missing_keys: vec![],
            notices: vec![],
            payload: None,
        };
        let value = reference_response_json(&response);
        assert_eq!(value["status"], "unsupported-topic");
        assert!(value.get("colors").is_none());
        assert!(value.get("version").is_none());
    }
}
