#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioDocumentFile {
    schema_version: String,
    name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    description: Option<String>,
    created_at: String,
    source: ScenarioSourceFile,
    restore: ScenarioRestoreFile,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    run: Option<ScenarioRunFile>,
    #[serde(default)]
    screen_state: Value,
    #[serde(default)]
    notices: Vec<StateNoticeFile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    multiplayer: Option<MultiplayerRestoreFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct MultiplayerRestoreFile {
    #[serde(default)]
    is_multiplayer: bool,
    #[serde(default)]
    restore_mode: String,
    #[serde(default)]
    local_player_id: String,
    #[serde(default)]
    host_player_id: String,
    #[serde(default)]
    local_player_role: String,
    #[serde(default)]
    players: Vec<MultiplayerPlayerFile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    lobby: Option<MultiplayerLobbyFile>,
    #[serde(default)]
    requires_remote_clients: bool,
    #[serde(default)]
    degraded_local_only_available: bool,
    #[serde(default)]
    limitations: Vec<CompatibilityNoteFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct MultiplayerPlayerFile {
    id: String,
    #[serde(default)]
    net_id: String,
    #[serde(default)]
    slot_id: i32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    display_name: Option<String>,
    #[serde(default)]
    selected_character_id: String,
    #[serde(default)]
    character: String,
    #[serde(default)]
    is_ready: bool,
    #[serde(default)]
    is_local: bool,
    #[serde(default)]
    is_host: bool,
    #[serde(default)]
    is_remote: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct MultiplayerLobbyFile {
    lobby_id: String,
    phase: String,
    #[serde(default)]
    available_characters: Vec<LobbyCharacterFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct LobbyCharacterFile {
    id: String,
    name: String,
    is_unlocked: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioSourceFile {
    #[serde(default)]
    game_version: String,
    #[serde(default)]
    bridge_version: String,
    #[serde(default)]
    spirectl_version: String,
    screen: ScreenFile,
    #[serde(default)]
    perspective: Option<PerspectiveFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioRestoreFile {
    mode: String,
    quality: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    exact_bundle: Option<ExactBundleFile>,
    fallback_policy: String,
    #[serde(default)]
    compatibility_notes: Vec<CompatibilityNoteFile>,
    #[serde(default)]
    field_reports: Vec<RestoreFieldReportFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RestoreFieldReportFile {
    path: String,
    capture: String,
    restore: String,
    validation_key: bool,
    reason_code: String,
    message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RestoreMismatchFile {
    path: String,
    expected: Value,
    observed: Value,
    severity: String,
    #[serde(default)]
    support_class: String,
    #[serde(default)]
    reason_code: String,
    #[serde(default)]
    suggested_next_step: String,
}

#[derive(Debug, Clone)]
struct ComparedRestorePath {
    path: String,
    support_class: String,
    reason_code: String,
    suggested_next_step: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioRunFile {
    #[serde(default)]
    seed: String,
    #[serde(default)]
    act: i32,
    #[serde(default)]
    floor: i32,
    #[serde(default)]
    ascension: i32,
    #[serde(default)]
    players: Vec<ScenarioPlayerFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioPlayerFile {
    id: String,
    character: String,
    is_local: bool,
    is_host: bool,
    is_remote: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScreenFile {
    #[serde(rename = "id")]
    screen_type: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    #[serde(rename = "instanceId")]
    screen_instance_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PerspectiveFile {
    #[serde(default)]
    scope: String,
    #[serde(default)]
    player_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ExactBundleFile {
    path: String,
    format_version: String,
    #[serde(default)]
    content_type: String,
    sha256: String,
    size_bytes: u64,
    version_sensitive: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CompatibilityNoteFile {
    code: String,
    message: String,
    #[serde(default)]
    field: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StateNoticeFile {
    code: String,
    message: String,
    #[serde(default)]
    provisional: bool,
    #[serde(default)]
    path: String,
    #[serde(default)]
    severity: String,
    #[serde(default)]
    source: String,
}
