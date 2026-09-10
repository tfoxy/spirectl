// Fixture schema (`spirectl.fixture/v0`): the authored YAML mirrors the
// state snapshot shape (`run`, `characterSelect`, `run.players`,
// `run.view.playerId`, `run.currentRoom.*`) with exact state field names.
// Normalization applies defaults (one Ironclad player, act/floor 1, seed =
// fixture name, view player = first/host-owned player) so fixtures only carry
// scenario-relevant data, then derives the loader recipe (`screen` hint on the
// wire document) from the document structure instead of an authored screen id.
//
// Fields with no state equivalent are fixture-only authoring extensions and
// are marked FIXTURE-ONLY below (e.g. `characterSelect.kind`,
// `players[].isHostLocalSeat`, `treasure.relicSelectionOpen`, the
// `cardOverlay`/`simpleCardSelection`/`bundleSelection` overlay payloads).

const FIXTURE_SCHEMA_VERSION: &str = "spirectl.fixture/v0";
// Stable runtime player-id form (`p:<positive-net-id>`); the live fixture loader
// rejects anything else, so defaults must already be in this shape.
const DEFAULT_PLAYER_ID: &str = "p:1";
const DEFAULT_CHARACTER_ID: &str = "IRONCLAD";
const MAIN_MENU_ROOT_SCENE: &str = "main-menu";

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureDocumentInput {
    schema_version: Option<String>,
    name: String,
    description: Option<String>,
    // FIXTURE-ONLY disambiguator; only "main-menu" is valid today. Mirrors
    // state `rootScene` for the one screen with no structural signal.
    root_scene: Option<String>,
    character_select: Option<FixtureCharacterSelectInput>,
    run: Option<FixtureRunInput>,
}

impl FixtureDocumentInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureDocument, AppError> {
        let normalized_name = self.name.trim().to_string();
        let schema_version = match self.schema_version.as_deref().map(str::trim) {
            Some(FIXTURE_SCHEMA_VERSION) => FIXTURE_SCHEMA_VERSION.to_string(),
            Some(other) => {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture schemaVersion is not supported.",
                    &[validation_detail(
                        "schemaVersion",
                        other,
                        "Use schemaVersion: spirectl.fixture/v0 (the state-shaped fixture schema).",
                    )],
                ));
            }
            None => {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture schemaVersion is required.",
                    &[validation_detail(
                        "schemaVersion",
                        "null",
                        "Add schemaVersion: spirectl.fixture/v0 (the state-shaped fixture schema).",
                    )],
                ));
            }
        };

        if normalized_name.is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture name must not be empty.",
                &[validation_detail(
                    "name",
                    self.name,
                    "Provide a stable authored fixture name.",
                )],
            ));
        }

        let root_scene = match self.root_scene.as_deref().map(str::trim) {
            None => None,
            Some(MAIN_MENU_ROOT_SCENE) => Some(MAIN_MENU_ROOT_SCENE.to_string()),
            Some(other) => {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture rootScene is not supported.",
                    &[validation_detail(
                        "rootScene",
                        other,
                        "Only rootScene: main-menu is authorable; every other screen is derived from characterSelect or run.currentRoom/overlays structure.",
                    )],
                ));
            }
        };

        match (self.character_select, self.run) {
            (Some(_), Some(_)) => Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect and run are mutually exclusive.",
                &[validation_detail(
                    "characterSelect",
                    "present",
                    "Author characterSelect for lobby fixtures or run for in-run fixtures, mirroring state where run is absent during character select.",
                )],
            )),
            (Some(character_select), None) => {
                if root_scene.is_some() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture rootScene is not supported for characterSelect fixtures.",
                        &[validation_detail(
                            "rootScene",
                            MAIN_MENU_ROOT_SCENE,
                            "Remove rootScene; lobby fixtures derive their screen from characterSelect.kind.",
                        )],
                    ));
                }
                let character_select =
                    character_select.normalize(resolved_path, &normalized_name)?;
                let screen = match character_select.kind.as_str() {
                    "load-run" => bridge::LOAD_RUN_LOBBY_SCREEN_ID,
                    _ => bridge::START_RUN_LOBBY_SCREEN_ID,
                }
                .to_string();
                Ok(FixtureDocument {
                    schema_version,
                    name: normalized_name,
                    description: self.description,
                    screen,
                    root_scene: None,
                    character_select: Some(character_select),
                    run: None,
                })
            }
            (None, run) => {
                let run = run.unwrap_or_default();
                let (run, screen) =
                    run.normalize(resolved_path, &normalized_name, root_scene.as_deref())?;
                Ok(FixtureDocument {
                    schema_version,
                    name: normalized_name,
                    description: self.description,
                    screen,
                    root_scene,
                    character_select: None,
                    run: Some(run),
                })
            }
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureDocument {
    pub schema_version: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    // Derived loader-recipe hint (same ids the bridge dispatch switches on).
    // Computed by structure derivation; never authored.
    pub screen: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub root_scene: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub character_select: Option<FixtureCharacterSelect>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run: Option<FixtureRun>,
}

// ---------------------------------------------------------------------------
// characterSelect (lobby fixtures)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCharacterSelectInput {
    // FIXTURE-ONLY: start-run | load-run lobby UI mode (state netGameType
    // does not encode this). Defaults to start-run.
    kind: Option<String>,
    lobby: Option<FixtureCharacterSelectLobbyInput>,
    #[serde(default)]
    character_buttons: Vec<FixtureCharacterButtonInput>,
    view: Option<FixtureCharacterSelectViewInput>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCharacterSelectLobbyInput {
    local_player_id: Option<String>,
    host_player_id: Option<String>,
    seed: Option<String>,
    players: Option<Vec<FixtureCharacterSelectPlayerInput>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCharacterSelectPlayerInput {
    id: String,
    character_id: Option<String>,
    is_ready: Option<bool>,
    slot_id: Option<u32>,
    // FIXTURE-ONLY: marks an extra couch-coop seat owned by the host machine.
    is_host_local_seat: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCharacterButtonInput {
    character_id: String,
    is_locked: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCharacterSelectViewInput {
    player_id: String,
}

impl FixtureCharacterSelectInput {
    fn normalize(
        self,
        resolved_path: &Path,
        fixture_name: &str,
    ) -> Result<FixtureCharacterSelect, AppError> {
        let kind = self
            .kind
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .unwrap_or("start-run")
            .to_string();
        if !matches!(kind.as_str(), "start-run" | "load-run") {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.kind is not supported.",
                &[validation_detail(
                    "characterSelect.kind",
                    kind,
                    "Use start-run or load-run for the executable lobby fixture slice.",
                )],
            ));
        }

        let lobby = self.lobby.unwrap_or_default();
        let players = lobby.players.unwrap_or_else(|| {
            vec![FixtureCharacterSelectPlayerInput {
                id: DEFAULT_PLAYER_ID.to_string(),
                character_id: Some(DEFAULT_CHARACTER_ID.to_string()),
                is_ready: None,
                slot_id: None,
                is_host_local_seat: None,
            }]
        });
        if players.is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.lobby.players must not be empty.",
                &[validation_detail(
                    "characterSelect.lobby.players",
                    "[]",
                    "Author one or more lobby players, or omit players entirely for the default single Ironclad.",
                )],
            ));
        }

        let mut player_ids = std::collections::BTreeSet::new();
        let mut normalized_players = Vec::with_capacity(players.len());
        for (index, player) in players.into_iter().enumerate() {
            let id = normalize_required_fixture_id(
                resolved_path,
                &format!("characterSelect.lobby.players[{index}].id"),
                player.id,
                "Each lobby player requires a stable id.",
            )?;
            if !player_ids.insert(id.clone()) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture contains duplicate lobby player ids.",
                    &[validation_detail(
                        &format!("characterSelect.lobby.players[{index}].id"),
                        id,
                        "Player ids must be unique within one fixture document.",
                    )],
                ));
            }
            let character_id = player
                .character_id
                .map(str_trimmed)
                .filter(|value| !value.is_empty())
                .unwrap_or_else(|| DEFAULT_CHARACTER_ID.to_string());
            if kind == "load-run" && (!id.starts_with("p:") || id.len() <= 2) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture load-run lobby players must use stable local player ids.",
                    &[validation_detail(
                        &format!("characterSelect.lobby.players[{index}].id"),
                        id,
                        "Use stable p:<net-id> player ids for load-run lobby fixtures.",
                    )],
                ));
            }
            normalized_players.push(FixtureCharacterSelectPlayer {
                id,
                character_id,
                is_ready: player.is_ready,
                slot_id: player.slot_id.unwrap_or(index as u32),
                is_host_local_seat: player.is_host_local_seat,
            });
        }

        let local_player_id = match lobby
            .local_player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
        {
            Some(value) => value,
            None => normalized_players[0].id.clone(),
        };
        if !player_ids.contains(&local_player_id) {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.lobby.localPlayerId must refer to a declared lobby player.",
                &[validation_detail(
                    "characterSelect.lobby.localPlayerId",
                    local_player_id,
                    "Choose one characterSelect.lobby.players[].id value as the local player.",
                )],
            ));
        }
        if normalized_players
            .iter()
            .any(|player| player.id == local_player_id && player.is_host_local_seat == Some(true))
        {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.lobby.localPlayerId cannot point at a host-local seat.",
                &[validation_detail(
                    "characterSelect.lobby.localPlayerId",
                    local_player_id,
                    "Host-local seats are bridge-owned extra seats; the local player must be the primary seat.",
                )],
            ));
        }

        let has_explicit_host_player_id = lobby
            .host_player_id
            .as_ref()
            .is_some_and(|value| !value.trim().is_empty());
        let host_player_id = lobby
            .host_player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| local_player_id.clone());
        if !player_ids.contains(&host_player_id) {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.lobby.hostPlayerId must refer to a declared lobby player.",
                &[validation_detail(
                    "characterSelect.lobby.hostPlayerId",
                    host_player_id,
                    "Choose one characterSelect.lobby.players[].id value for the lobby host.",
                )],
            ));
        }
        if !has_explicit_host_player_id
            && normalized_players
                .iter()
                .any(|player| player.is_host_local_seat.is_some())
        {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture characterSelect.lobby.players[].isHostLocalSeat requires characterSelect.lobby.hostPlayerId.",
                &[validation_detail(
                    "characterSelect.lobby.hostPlayerId",
                    "null",
                    "Declare the host player id before marking extra host-local seats.",
                )],
            ));
        }

        let mut character_buttons = Vec::with_capacity(self.character_buttons.len());
        for (index, button) in self.character_buttons.into_iter().enumerate() {
            let character_id = normalize_required_fixture_id(
                resolved_path,
                &format!("characterSelect.characterButtons[{index}].characterId"),
                button.character_id,
                "Provide a character id for each authored character button.",
            )?;
            if button.is_locked != Some(true) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture characterSelect.characterButtons only supports locked buttons.",
                    &[validation_detail(
                        &format!("characterSelect.characterButtons[{index}].isLocked"),
                        button
                            .is_locked
                            .map(|value| value.to_string())
                            .unwrap_or_else(|| "null".to_string()),
                        "Author characterButtons entries with isLocked: true to mark visible locked character choices; omit the section to use the normal game character list.",
                    )],
                ));
            }
            character_buttons.push(FixtureCharacterButton {
                character_id,
                is_locked: true,
            });
        }

        let view_player_id = match self.view {
            Some(view) => {
                let player_id = normalize_required_fixture_id(
                    resolved_path,
                    "characterSelect.view.playerId",
                    view.player_id,
                    "Point characterSelect.view.playerId at the local lobby player.",
                )?;
                if player_id != local_player_id {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture characterSelect.view.playerId must point at the local lobby player.",
                        &[validation_detail(
                            "characterSelect.view.playerId",
                            player_id,
                            "Use characterSelect.lobby.localPlayerId (or omit view entirely) for the lobby perspective.",
                        )],
                    ));
                }
                player_id
            }
            None => local_player_id.clone(),
        };

        let seed = normalize_seed(lobby.seed, fixture_name);

        Ok(FixtureCharacterSelect {
            kind,
            lobby: FixtureCharacterSelectLobby {
                local_player_id,
                host_player_id,
                seed,
                players: normalized_players,
            },
            character_buttons,
            view: FixtureCharacterSelectView {
                player_id: view_player_id,
            },
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCharacterSelect {
    pub kind: String,
    pub lobby: FixtureCharacterSelectLobby,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub character_buttons: Vec<FixtureCharacterButton>,
    pub view: FixtureCharacterSelectView,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCharacterSelectLobby {
    pub local_player_id: String,
    pub host_player_id: String,
    pub seed: String,
    pub players: Vec<FixtureCharacterSelectPlayer>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCharacterSelectPlayer {
    pub id: String,
    pub character_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub is_ready: Option<bool>,
    pub slot_id: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub is_host_local_seat: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCharacterButton {
    pub character_id: String,
    pub is_locked: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCharacterSelectView {
    pub player_id: String,
}

// ---------------------------------------------------------------------------
// run
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRunInput {
    seed: Option<String>,
    ascension_level: Option<u32>,
    current_act_index: Option<u32>,
    act_floor: Option<u32>,
    players: Option<FixtureRunPlayersInput>,
    current_room: Option<FixtureCurrentRoomInput>,
    view: Option<FixtureRunViewInput>,
    // Run-terminal outcome. When present, the run has ended and the loader shows
    // the game-over (defeat) screen — an overlay pushed over the finished run, not
    // a room. Mutually exclusive with currentRoom/overlays/rootScene.
    game_over: Option<FixtureGameOverInput>,
}

/// `run.gameOver` authors a run-terminal defeat outcome. In scope only combat
/// death is supported: `win` must be `false` and `killedByEncounter` names the
/// combat encounter that defeated the party (drives `GameOverType.CombatDeath`).
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureGameOverInput {
    win: Option<bool>,
    killed_by_encounter: Option<String>,
}

impl FixtureGameOverInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureGameOver, AppError> {
        let win = self.win.ok_or_else(|| {
            AppError::invalid_fixture(
                resolved_path,
                "fixture run.gameOver.win is required.",
                &[validation_detail(
                    "run.gameOver.win",
                    "missing",
                    "Author win: false to reach the combat-defeat screen (victory is out of scope).",
                )],
            )
        })?;
        if win {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.gameOver.win: true (victory) is not supported yet.",
                &[validation_detail(
                    "run.gameOver.win",
                    "true",
                    "Only combat defeat is in scope; author win: false.",
                )],
            ));
        }
        let killed_by_encounter = self
            .killed_by_encounter
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .ok_or_else(|| {
                AppError::invalid_fixture(
                    resolved_path,
                    "fixture run.gameOver.killedByEncounter is required for combat defeat.",
                    &[validation_detail(
                        "run.gameOver.killedByEncounter",
                        "missing",
                        "Name the combat encounter that defeated the party (e.g. NIBBITS_NORMAL).",
                    )],
                )
            })?;
        Ok(FixtureGameOver {
            win,
            killed_by_encounter,
        })
    }
}

/// `run.players` accepts either an explicit list or a player count. `players: N`
/// is shorthand for N default players with enumerated ids `p:1`..`p:N` and the
/// host-local metadata defaults applied during normalization.
#[derive(Debug, Clone, Deserialize)]
#[serde(untagged)]
enum FixtureRunPlayersInput {
    Count(u32),
    List(Vec<FixtureRunPlayerInput>),
}

impl FixtureRunInput {
    fn normalize(
        self,
        resolved_path: &Path,
        fixture_name: &str,
        root_scene: Option<&str>,
    ) -> Result<(FixtureRun, String), AppError> {
        let act_floor = self.act_floor.unwrap_or(1);
        if act_floor < 1 {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.actFloor must be greater than or equal to 1.",
                &[validation_detail(
                    "run.actFloor",
                    act_floor.to_string(),
                    "Use a 1-based floor number within the current act.",
                )],
            ));
        }
        let current_act_index = self.current_act_index.unwrap_or(0);
        let seed = normalize_seed(self.seed, fixture_name);

        let players = match self.players {
            None => vec![FixtureRunPlayerInput {
                id: DEFAULT_PLAYER_ID.to_string(),
                character_id: Some(DEFAULT_CHARACTER_ID.to_string()),
                ..FixtureRunPlayerInput::default()
            }],
            Some(FixtureRunPlayersInput::List(list)) => list,
            Some(FixtureRunPlayersInput::Count(count)) => {
                if count == 0 {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.players count must be greater than zero.",
                        &[validation_detail(
                            "run.players",
                            count.to_string(),
                            "Use a positive player count (e.g. players: 2), or author an explicit players list.",
                        )],
                    ));
                }
                // `players: N` enumerates default players `p:1`..`p:N`; the
                // host-local metadata defaults are applied later (multi-player
                // recipes only) so the first id is the host-owned local seat.
                (1..=count)
                    .map(|net_id| FixtureRunPlayerInput {
                        id: format!("p:{net_id}"),
                        character_id: Some(DEFAULT_CHARACTER_ID.to_string()),
                        ..FixtureRunPlayerInput::default()
                    })
                    .collect()
            }
        };
        if players.is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.players must not be empty.",
                &[validation_detail(
                    "run.players",
                    "[]",
                    "Author one or more run players, or omit players entirely for the default single Ironclad.",
                )],
            ));
        }

        let mut player_ids = std::collections::BTreeSet::new();
        let mut normalized_players = Vec::with_capacity(players.len());
        for (index, player) in players.into_iter().enumerate() {
            normalized_players.push(player.normalize(resolved_path, index, &mut player_ids)?);
        }

        let current_room = self
            .current_room
            .map(|room| room.normalize(resolved_path))
            .transpose()?;

        let game_over = self
            .game_over
            .map(|game_over| game_over.normalize(resolved_path))
            .transpose()?;

        let view_input = self.view.unwrap_or_default();
        let explicit_view_player_id = view_input
            .player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty());
        if let Some(player_id) = explicit_view_player_id.as_ref()
            && !player_ids.contains(player_id)
        {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.playerId must refer to a declared player.",
                &[validation_detail(
                    "run.view.playerId",
                    player_id.clone(),
                    "Choose one run.players[].id value for the fixture perspective.",
                )],
            ));
        }

        // Derive the loader recipe from structure before resolving the view
        // player, since the recipe decides single-player vs host-local
        // multiplayer player requirements.
        let screen = derive_run_screen(
            resolved_path,
            root_scene,
            current_room.as_ref(),
            &normalized_players,
            game_over.as_ref(),
        )?;

        let view_player_id = resolve_view_player_id(
            resolved_path,
            &screen,
            &mut normalized_players,
            explicit_view_player_id,
        )?;

        validate_overlay_owner(resolved_path, &normalized_players, &view_player_id)?;

        let current_room = current_room
            .map(|room| {
                resolve_room_player_state_owners(resolved_path, room, &player_ids, &view_player_id)
            })
            .transpose()?;

        let capstone = view_input
            .capstone
            .map(|capstone| capstone.normalize(resolved_path, &player_ids, &view_player_id))
            .transpose()?
            .flatten();
        let selected_potion = view_input
            .selected_potion
            .map(|potion| potion.normalize(resolved_path))
            .transpose()?;
        let selected_card = view_input
            .selected_card
            .map(|card| card.normalize(resolved_path, &player_ids, &view_player_id))
            .transpose()?;
        let inspect_relic = view_input
            .inspect_relic
            .map(|relic| relic.normalize(resolved_path))
            .transpose()?;
        let has_combat_room = current_room
            .as_ref()
            .is_some_and(|room| room.combat.is_some());
        let hand_selection = view_input
            .hand_selection
            .map(|hand_selection| hand_selection.normalize(resolved_path, has_combat_room))
            .transpose()?;

        Ok((
            FixtureRun {
                seed,
                ascension_level: self.ascension_level,
                current_act_index,
                act_floor,
                players: normalized_players,
                current_room,
                view: FixtureRunView {
                    player_id: view_player_id,
                    capstone,
                    selected_potion,
                    selected_card,
                    inspect_relic,
                    hand_selection,
                },
                game_over,
            },
            screen,
        ))
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRun {
    pub seed: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ascension_level: Option<u32>,
    pub current_act_index: u32,
    pub act_floor: u32,
    pub players: Vec<FixtureRunPlayer>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_room: Option<FixtureCurrentRoom>,
    pub view: FixtureRunView,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_over: Option<FixtureGameOver>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureGameOver {
    pub win: bool,
    pub killed_by_encounter: String,
}

// ---------------------------------------------------------------------------
// run.players
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRunPlayerInput {
    #[serde(default)]
    id: String,
    character_id: Option<String>,
    creature: Option<FixtureCreatureInput>,
    gold: Option<u32>,
    #[serde(default)]
    potions: Vec<FixtureModelRefInput>,
    #[serde(default)]
    relics: Vec<FixtureModelRefInput>,
    deck: Option<FixtureDeckInput>,
    combat: Option<FixturePlayerCombatInput>,
    // Combat-only: orbs channeled into the player's orb queue on load. Empty
    // slots are expressed via `orbSlotCount` (capacity) exceeding the orb count,
    // not a sentinel entry. Authorable for any character (Defect by default,
    // but allies can hold orbs in special situations).
    #[serde(default)]
    orbs: Vec<FixtureModelRefInput>,
    // Combat-only: orb-queue capacity. Defaults to the character's base orb slot
    // count when omitted; required when the desired capacity differs (e.g.
    // granting an orb to a character whose base capacity is 0).
    orb_slot_count: Option<u32>,
    is_local: Option<bool>,
    // FIXTURE-ONLY: couch-coop extra seat owned by the host machine.
    is_host_local_seat: Option<bool>,
    // FIXTURE-ONLY on run players (state keeps slots on the lobby).
    slot_id: Option<u32>,
    // FIXTURE-ONLY: co-op turn state. When true, the loader marks this player as
    // ready-to-end-turn on load (CombatManager.SetReadyToEndTurn), leaving them in
    // the "ended, waiting for others" state. Combat advances to the enemy phase
    // only once every seat is ready, so do not flag all seats at once.
    ended_turn: Option<bool>,
    // Combat-only: status effects applied to the player's creature on load (e.g.
    // WEAK_POWER, FRAIL_POWER, STRENGTH_POWER). Drive the card damage/block preview
    // numbers/colors via the game's own modifier hooks.
    #[serde(default)]
    powers: Vec<FixturePowerInput>,
    #[serde(default)]
    overlays: Vec<FixtureOverlayEntryInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCreatureInput {
    current_hp: Option<u32>,
    max_hp: Option<u32>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureDeckInput {
    #[serde(default)]
    cards: Vec<FixtureCombatCardInput>,
    // When true, REPLACE the character's starter deck with `cards` (clear it first on
    // load) instead of the default append. Lets a fixture show exactly the authored
    // master deck (e.g. only enchanted cards) in the deck viewer.
    replace: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureModelRefInput {
    model_id: String,
}

// A status effect / power applied to a creature on load (e.g. VULNERABLE_POWER on an
// enemy, WEAK_POWER / FRAIL_POWER on the player). `amount` is the stack count and
// defaults to 1 when omitted. The loader resolves `modelId` through the installed
// STS2 power registry and applies it via the game's PowerCmd so card damage/block
// previews recompute exactly as they would in play.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixturePowerInput {
    model_id: String,
    amount: Option<i64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixturePower {
    pub model_id: String,
    pub amount: i64,
}

fn normalize_powers(
    resolved_path: &Path,
    field: &str,
    powers: Vec<FixturePowerInput>,
) -> Result<Vec<FixturePower>, AppError> {
    powers
        .into_iter()
        .enumerate()
        .map(|(index, power)| {
            let model_id = power.model_id.trim().to_string();
            if model_id.is_empty() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture power entries require a non-empty modelId.",
                    &[validation_detail(
                        &format!("{field}[{index}].modelId"),
                        String::new(),
                        "Provide a power modelId (e.g. VULNERABLE_POWER, WEAK_POWER, FRAIL_POWER).",
                    )],
                ));
            }
            let amount = power.amount.unwrap_or(1);
            if amount <= 0 {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture power amount must be a positive stack count.",
                    &[validation_detail(
                        &format!("{field}[{index}].amount"),
                        amount.to_string(),
                        "Use a positive amount (omit to default to 1).",
                    )],
                ));
            }
            Ok(FixturePower { model_id, amount })
        })
        .collect()
}

impl FixtureRunPlayerInput {
    fn normalize(
        self,
        resolved_path: &Path,
        index: usize,
        player_ids: &mut std::collections::BTreeSet<String>,
    ) -> Result<FixtureRunPlayer, AppError> {
        let field = format!("run.players[{index}]");
        // Omitted ids enumerate `p:1`..`p:N` by author order, matching the
        // `players: N` shorthand.
        let id = match self.id.trim() {
            "" => format!("p:{}", index + 1),
            trimmed => trimmed.to_string(),
        };
        if !player_ids.insert(id.clone()) {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture contains duplicate player ids.",
                &[validation_detail(
                    &format!("{field}.id"),
                    id,
                    "Player ids must be unique within one fixture document.",
                )],
            ));
        }
        let character_id = self
            .character_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| DEFAULT_CHARACTER_ID.to_string());

        let creature = self
            .creature
            .map(|creature| {
                if let (Some(current_hp), Some(max_hp)) = (creature.current_hp, creature.max_hp)
                    && current_hp > max_hp
                {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.players[].creature.currentHp must not exceed creature.maxHp.",
                        &[validation_detail(
                            &format!("{field}.creature.currentHp"),
                            current_hp.to_string(),
                            "Use hp <= maxHp in the authored player creature.",
                        )],
                    ));
                }
                Ok(FixtureCreature {
                    current_hp: creature.current_hp,
                    max_hp: creature.max_hp,
                })
            })
            .transpose()?;

        let potions = normalize_model_refs(
            resolved_path,
            &format!("{field}.potions"),
            self.potions,
            "Provide potion modelId entries, or omit potions entirely.",
        )?;
        let relics = normalize_model_refs(
            resolved_path,
            &format!("{field}.relics"),
            self.relics,
            "Provide relic modelId entries, or omit relics entirely.",
        )?;
        // NOTE: deck.cards APPENDS the authored cards to the character's starter deck
        // on load (live recipe behavior) UNLESS deck.replace is true, which clears the
        // starter first so the master deck is exactly the authored cards. Cards may carry
        // an enchantment (modelId + enchantmentId + enchantAmount); afflictionId is unused
        // for the master deck.
        let deck = self
            .deck
            .map(|deck| {
                Ok::<_, AppError>(FixtureDeck {
                    replace: deck.replace.unwrap_or(false),
                    cards: normalize_combat_cards(
                        resolved_path,
                        &format!("{field}.deck.cards"),
                        deck.cards,
                        "Provide card modelId entries, or omit deck entirely.",
                    )?,
                })
            })
            .transpose()?;

        let combat = self
            .combat
            .map(|combat| combat.normalize(resolved_path, &format!("{field}.combat")))
            .transpose()?;

        let orbs = normalize_model_refs(
            resolved_path,
            &format!("{field}.orbs"),
            self.orbs,
            "Provide orb modelId entries, or omit orbs entirely.",
        )?;
        // An empty slot is `orbSlotCount` > orb count; the loader rejects a
        // capacity smaller than the authored orbs, so catch it early here.
        if let Some(orb_slot_count) = self.orb_slot_count
            && (orb_slot_count as usize) < orbs.len()
        {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.players[].orbSlotCount must be at least the number of authored orbs.",
                &[validation_detail(
                    &format!("{field}.orbSlotCount"),
                    orb_slot_count.to_string(),
                    "Set orbSlotCount >= the number of orbs (extra capacity becomes empty slots), or omit it to use the character base.",
                )],
            ));
        }

        let powers = normalize_powers(resolved_path, &format!("{field}.powers"), self.powers)?;

        let overlays = self
            .overlays
            .into_iter()
            .enumerate()
            .map(|(overlay_index, overlay)| {
                overlay.normalize(resolved_path, &format!("{field}.overlays[{overlay_index}]"))
            })
            .collect::<Result<Vec<_>, _>>()?;

        Ok(FixtureRunPlayer {
            id,
            character_id,
            creature,
            gold: self.gold,
            potions,
            relics,
            deck,
            combat,
            orbs,
            orb_slot_count: self.orb_slot_count,
            is_local: self.is_local,
            is_host_local_seat: self.is_host_local_seat,
            slot_id: self.slot_id,
            ended_turn: self.ended_turn,
            powers,
            overlays,
        })
    }
}

impl FixturePlayerCombatInput {
    fn normalize(self, resolved_path: &Path, field: &str) -> Result<FixturePlayerCombat, AppError> {
        Ok(FixturePlayerCombat {
            hand: normalize_optional_combat_pile(
                resolved_path,
                &format!("{field}.hand.cards"),
                self.hand,
            )?,
            draw_pile: normalize_optional_combat_pile(
                resolved_path,
                &format!("{field}.drawPile.cards"),
                self.draw_pile,
            )?,
            discard_pile: normalize_optional_combat_pile(
                resolved_path,
                &format!("{field}.discardPile.cards"),
                self.discard_pile,
            )?,
            exhaust_pile: normalize_optional_combat_pile(
                resolved_path,
                &format!("{field}.exhaustPile.cards"),
                self.exhaust_pile,
            )?,
            play_pile: normalize_optional_combat_pile(
                resolved_path,
                &format!("{field}.playPile.cards"),
                self.play_pile,
            )?,
        })
    }
}

fn normalize_optional_combat_pile(
    resolved_path: &Path,
    field: &str,
    pile: Option<FixtureCombatPileInput>,
) -> Result<Option<FixtureCombatPile>, AppError> {
    pile.map(|pile| {
        Ok::<_, AppError>(FixtureCombatPile {
            cards: normalize_combat_cards(
                resolved_path,
                field,
                pile.cards,
                "Provide card modelId entries, or omit this combat pile entirely.",
            )?,
        })
    })
    .transpose()
}

fn normalize_combat_cards(
    resolved_path: &Path,
    field: &str,
    values: Vec<FixtureCombatCardInput>,
    empty_note: &str,
) -> Result<Vec<FixtureCombatCard>, AppError> {
    values
        .into_iter()
        .enumerate()
        .map(|(index, value)| {
            let model_id = normalize_required_model_id(
                resolved_path,
                &format!("{field}[{index}].modelId"),
                value.model_id,
                empty_note,
            )?;
            let affliction_id = value.affliction_id.map(str_trimmed);
            if matches!(affliction_id.as_deref(), Some("")) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture combat card afflictionId must not be empty.",
                    &[validation_detail(
                        &format!("{field}[{index}].afflictionId"),
                        String::new(),
                        "Provide an affliction id (e.g. Bound, Hexed), or omit afflictionId.",
                    )],
                ));
            }

            let affliction_amount = match (affliction_id.as_ref(), value.affliction_amount) {
                (Some(_), Some(amount)) if amount <= 0 => {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture combat card afflictionAmount must be positive.",
                        &[validation_detail(
                            &format!("{field}[{index}].afflictionAmount"),
                            amount.to_string(),
                            "Use a positive amount, or omit it to default to 1.",
                        )],
                    ));
                }
                (Some(_), Some(amount)) => Some(amount),
                (Some(_), None) => Some(1),
                (None, Some(amount)) => {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture combat card afflictionAmount requires afflictionId.",
                        &[validation_detail(
                            &format!("{field}[{index}].afflictionAmount"),
                            amount.to_string(),
                            "Add afflictionId, or omit afflictionAmount.",
                        )],
                    ));
                }
                (None, None) => None,
            };

            let enchantment_id = value.enchantment_id.map(str_trimmed);
            if matches!(enchantment_id.as_deref(), Some("")) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture combat card enchantmentId must not be empty.",
                    &[validation_detail(
                        &format!("{field}[{index}].enchantmentId"),
                        String::new(),
                        "Provide an enchantment id (e.g. SHARP, TEZCATARAS_EMBER), or omit enchantmentId.",
                    )],
                ));
            }

            let enchant_amount = match (enchantment_id.as_ref(), value.enchant_amount) {
                (Some(_), Some(amount)) if amount <= 0 => {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture combat card enchantAmount must be positive.",
                        &[validation_detail(
                            &format!("{field}[{index}].enchantAmount"),
                            amount.to_string(),
                            "Use a positive amount, or omit it to default to 1.",
                        )],
                    ));
                }
                (Some(_), Some(amount)) => Some(amount),
                (Some(_), None) => Some(1),
                (None, Some(amount)) => {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture combat card enchantAmount requires enchantmentId.",
                        &[validation_detail(
                            &format!("{field}[{index}].enchantAmount"),
                            amount.to_string(),
                            "Add enchantmentId, or omit enchantAmount.",
                        )],
                    ));
                }
                (None, None) => None,
            };

            Ok(FixtureCombatCard {
                model_id,
                affliction_id,
                affliction_amount,
                enchantment_id,
                enchant_amount,
            })
        })
        .collect()
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRunPlayer {
    pub id: String,
    pub character_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub creature: Option<FixtureCreature>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gold: Option<u32>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub potions: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub relics: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deck: Option<FixtureDeck>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub combat: Option<FixturePlayerCombat>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub orbs: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub orb_slot_count: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub is_local: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub is_host_local_seat: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub slot_id: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ended_turn: Option<bool>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub powers: Vec<FixturePower>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub overlays: Vec<FixtureOverlayEntry>,
}

impl FixtureRunPlayer {
    fn has_multiplayer_fields(&self) -> bool {
        self.is_local.is_some() || self.is_host_local_seat.is_some() || self.slot_id.is_some()
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCreature {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_hp: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_hp: Option<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureDeck {
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub replace: bool,
    pub cards: Vec<FixtureCombatCard>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixturePlayerCombatInput {
    hand: Option<FixtureCombatPileInput>,
    draw_pile: Option<FixtureCombatPileInput>,
    discard_pile: Option<FixtureCombatPileInput>,
    exhaust_pile: Option<FixtureCombatPileInput>,
    play_pile: Option<FixtureCombatPileInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCombatPileInput {
    #[serde(default)]
    cards: Vec<FixtureCombatCardInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCombatCardInput {
    model_id: String,
    affliction_id: Option<String>,
    affliction_amount: Option<i64>,
    enchantment_id: Option<String>,
    enchant_amount: Option<i64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixturePlayerCombat {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hand: Option<FixtureCombatPile>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub draw_pile: Option<FixtureCombatPile>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub discard_pile: Option<FixtureCombatPile>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exhaust_pile: Option<FixtureCombatPile>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub play_pile: Option<FixtureCombatPile>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCombatPile {
    pub cards: Vec<FixtureCombatCard>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCombatCard {
    pub model_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub affliction_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub affliction_amount: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enchantment_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enchant_amount: Option<i64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureModelRef {
    pub model_id: String,
}

// ---------------------------------------------------------------------------
// run.players[].overlays
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureOverlayEntryInput {
    choose_a_card: Option<FixtureChooseACardOverlayInput>,
    // FIXTURE-ONLY payload (no state simple-card-selection overlay).
    simple_card_selection: Option<FixtureSimpleCardSelectionOverlayInput>,
    deck_card_selection: Option<FixtureDeckCardSelectionOverlayInput>,
    // FIXTURE-ONLY payload (no state bundle overlay).
    bundle_selection: Option<FixtureBundleSelectionOverlayInput>,
    rewards: Option<FixtureRewardsOverlayInput>,
    // FIXTURE-ONLY payload (card-overlay / passive-card-overlay recipes).
    card_overlay: Option<FixtureCardOverlayInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureChooseACardOverlayInput {
    #[serde(default)]
    cards: Vec<FixtureModelRefInput>,
    can_skip: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureSimpleCardSelectionOverlayInput {
    #[serde(default)]
    cards: Vec<FixtureModelRefInput>,
    can_confirm: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureDeckCardSelectionOverlayInput {
    // upgrade | transform | enchant | select. Mirrors the state kind enum;
    // `select` is FIXTURE-ONLY and maps to the base NDeckCardSelectScreen.
    kind: Option<String>,
    #[serde(default)]
    cards: Vec<FixtureModelRefInput>,
    can_skip: Option<bool>,
    can_confirm: Option<bool>,
    // FIXTURE-ONLY (state only carries formatted display strings): the
    // enchantment applied in the enchant variant's preview.
    enchantment_id: Option<String>,
    enchant_amount: Option<i64>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureBundleSelectionOverlayInput {
    #[serde(default)]
    bundles: Vec<FixtureBundleInput>,
    can_confirm: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRewardsOverlayInput {
    // Drives the synthetic combat room the loader enters so relic reward hooks
    // (`TryModifyRewards`) fire against a real CombatRoom. monster | elite | boss.
    room_type: Option<String>,
    // Optional explicit encounter for that room type; the loader picks a default
    // per room type when omitted.
    encounter_id: Option<String>,
    #[serde(default)]
    items: Vec<FixtureRewardItemInput>,
}

// One authored reward. Exactly one field may be set (tagged-union, mirroring the
// one-key-per-entry overlay style).
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRewardItemInput {
    gold: Option<i64>,
    card: Option<FixtureCardRewardInput>,
    special_card: Option<FixtureModelRefInput>,
    potion: Option<FixtureRewardPotionInput>,
    relic: Option<FixtureRewardRelicInput>,
    card_removal: Option<bool>,
    linked: Option<FixtureLinkedRewardInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCardRewardInput {
    // Specific card choices (any rarity; include a rare card model to author a
    // "card with rare rarity" reward).
    #[serde(default)]
    model_ids: Vec<String>,
    // Pooled choice: draw `count` cards from the room pool instead of authoring
    // specific cards. Mutually exclusive with modelIds.
    room_type: Option<String>,
    count: Option<u32>,
    // When false, the card reward cannot be skipped. The game caps card-reward
    // alternatives at 2 (Skip/Reroll/Sacrifice); set false to free the Skip slot
    // when reroll- and alternative-adding relics (Driftwood + Pael's Wing) coexist.
    can_skip: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRewardPotionInput {
    // Specific potion; omit for a random potion reward.
    model_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRewardRelicInput {
    // Specific relic; omit for a random relic reward. `rarity` pulls a relic of
    // the given rarity. modelId and rarity are mutually exclusive.
    model_id: Option<String>,
    rarity: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureLinkedRewardInput {
    #[serde(default)]
    items: Vec<FixtureRewardItemInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCardOverlayInput {
    policy: String,
    source_screen: String,
    #[serde(default)]
    cards: Vec<FixtureModelRefInput>,
    close: Option<FixtureControlInput>,
    follow_through: Option<FixtureOverlayFollowThroughInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureBundleInput {
    id: String,
    #[serde(default)]
    card_ids: Vec<String>,
}

impl FixtureOverlayEntryInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureOverlayEntry, AppError> {
        let kind_count = [
            self.choose_a_card.is_some(),
            self.simple_card_selection.is_some(),
            self.deck_card_selection.is_some(),
            self.bundle_selection.is_some(),
            self.rewards.is_some(),
            self.card_overlay.is_some(),
        ]
        .into_iter()
        .filter(|present| *present)
        .count();
        if kind_count != 1 {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture overlay entries must author exactly one overlay kind.",
                &[validation_detail(
                    field,
                    format!("kinds={kind_count}"),
                    "Author exactly one of chooseACard, simpleCardSelection, deckCardSelection, bundleSelection, rewards, or cardOverlay per overlay entry.",
                )],
            ));
        }

        let choose_a_card = self
            .choose_a_card
            .map(|overlay| {
                Ok::<_, AppError>(FixtureChooseACardOverlay {
                    cards: normalize_required_model_refs(
                        resolved_path,
                        &format!("{field}.chooseACard.cards"),
                        overlay.cards,
                        "Provide one or more visible card modelId entries for the choose-a-card overlay.",
                    )?,
                    can_skip: overlay.can_skip,
                })
            })
            .transpose()?;
        let simple_card_selection = self
            .simple_card_selection
            .map(|overlay| {
                Ok::<_, AppError>(FixtureSimpleCardSelectionOverlay {
                    cards: normalize_required_model_refs(
                        resolved_path,
                        &format!("{field}.simpleCardSelection.cards"),
                        overlay.cards,
                        "Provide one or more visible card modelId entries for the simple-card-selection overlay.",
                    )?,
                    can_confirm: overlay.can_confirm,
                })
            })
            .transpose()?;
        let deck_card_selection = self
            .deck_card_selection
            .map(|overlay| overlay.normalize(resolved_path, field))
            .transpose()?;
        let bundle_selection = self
            .bundle_selection
            .map(|overlay| {
                if overlay.bundles.is_empty() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture bundleSelection.bundles must not be empty.",
                        &[validation_detail(
                            &format!("{field}.bundleSelection.bundles"),
                            "[]",
                            "Provide one or more authored bundles for the bundle-selection overlay.",
                        )],
                    ));
                }
                Ok::<_, AppError>(FixtureBundleSelectionOverlay {
                    bundles: overlay
                        .bundles
                        .into_iter()
                        .enumerate()
                        .map(|(index, bundle)| {
                            bundle.normalize(
                                resolved_path,
                                &format!("{field}.bundleSelection.bundles[{index}]"),
                            )
                        })
                        .collect::<Result<Vec<_>, _>>()?,
                    can_confirm: overlay.can_confirm,
                })
            })
            .transpose()?;
        let rewards = self
            .rewards
            .map(|overlay| overlay.normalize(resolved_path, field))
            .transpose()?;
        let card_overlay = self
            .card_overlay
            .map(|overlay| overlay.normalize(resolved_path, field))
            .transpose()?;

        Ok(FixtureOverlayEntry {
            choose_a_card,
            simple_card_selection,
            deck_card_selection,
            bundle_selection,
            rewards,
            card_overlay,
        })
    }
}

impl FixtureDeckCardSelectionOverlayInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureDeckCardSelectionOverlay, AppError> {
        let kind = self
            .kind
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "upgrade".to_string());
        if !matches!(kind.as_str(), "upgrade" | "transform" | "enchant" | "select") {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture deckCardSelection.kind is not supported.",
                &[validation_detail(
                    &format!("{field}.deckCardSelection.kind"),
                    kind,
                    "Use upgrade, transform, enchant, or select for the deck-card-selection overlay.",
                )],
            ));
        }
        let enchantment_id = self
            .enchantment_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty());
        if (enchantment_id.is_some() || self.enchant_amount.is_some()) && kind != "enchant" {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture deckCardSelection enchantment fields require kind: enchant.",
                &[validation_detail(
                    &format!("{field}.deckCardSelection.enchantmentId"),
                    enchantment_id.unwrap_or_else(|| "null".to_string()),
                    "Author enchantmentId/enchantAmount only on the enchant deck-card-selection variant.",
                )],
            ));
        }
        Ok(FixtureDeckCardSelectionOverlay {
            kind,
            cards: normalize_model_refs(
                resolved_path,
                &format!("{field}.deckCardSelection.cards"),
                self.cards,
                "Provide card modelId entries, or omit cards when the overlay reads the live player deck.",
            )?,
            can_skip: self.can_skip,
            can_confirm: self.can_confirm,
            enchantment_id,
            enchant_amount: self.enchant_amount,
        })
    }
}

impl FixtureCardOverlayInput {
    fn normalize(self, resolved_path: &Path, field: &str) -> Result<FixtureCardOverlay, AppError> {
        let policy = normalize_required_fixture_id(
            resolved_path,
            &format!("{field}.cardOverlay.policy"),
            self.policy,
            "Provide a visible overlay policy such as blocking or passive.",
        )?;
        if !matches!(policy.as_str(), "blocking" | "passive" | "dismissible") {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture cardOverlay.policy is not supported.",
                &[validation_detail(
                    &format!("{field}.cardOverlay.policy"),
                    policy,
                    "Use blocking, passive, or dismissible for the bounded overlay recipe.",
                )],
            ));
        }
        let source_screen = normalize_required_fixture_id(
            resolved_path,
            &format!("{field}.cardOverlay.sourceScreen"),
            self.source_screen,
            "Provide the observable screen id that opened the overlay.",
        )?;
        let cards = normalize_required_model_refs(
            resolved_path,
            &format!("{field}.cardOverlay.cards"),
            self.cards,
            "Provide one or more visible card modelId entries for the overlay recipe.",
        )?;
        Ok(FixtureCardOverlay {
            policy,
            source_screen,
            cards,
            close: self
                .close
                .map(|control| {
                    control.normalize(resolved_path, &format!("{field}.cardOverlay.close"))
                })
                .transpose()?,
            follow_through: self
                .follow_through
                .map(|follow| follow.normalize(resolved_path, field))
                .transpose()?,
        })
    }
}

impl FixtureBundleInput {
    fn normalize(self, resolved_path: &Path, field: &str) -> Result<FixtureBundle, AppError> {
        let id = normalize_required_fixture_id(
            resolved_path,
            &format!("{field}.id"),
            self.id,
            "Provide a stable bundle id for the authored bundle-selection overlay.",
        )?;
        let card_ids = normalize_model_id_list(
            resolved_path,
            &format!("{field}.cardIds"),
            self.card_ids,
            "Provide one or more card ids for each authored bundle.",
            true,
        )?;
        Ok(FixtureBundle { id, card_ids })
    }
}

impl FixtureRewardsOverlayInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureRewardsOverlay, AppError> {
        let field = format!("{field}.rewards");
        let room_type = self
            .room_type
            .map(|value| normalize_reward_room_type(resolved_path, &format!("{field}.roomType"), value))
            .transpose()?;
        let encounter_id = self
            .encounter_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty());
        let items = self
            .items
            .into_iter()
            .enumerate()
            .map(|(index, item)| item.normalize(resolved_path, &format!("{field}.items[{index}]"), true))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(FixtureRewardsOverlay {
            room_type,
            encounter_id,
            items,
        })
    }
}

impl FixtureRewardItemInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
        allow_linked: bool,
    ) -> Result<FixtureRewardItem, AppError> {
        let kind_count = [
            self.gold.is_some(),
            self.card.is_some(),
            self.special_card.is_some(),
            self.potion.is_some(),
            self.relic.is_some(),
            self.card_removal.is_some(),
            self.linked.is_some(),
        ]
        .into_iter()
        .filter(|present| *present)
        .count();
        if kind_count != 1 {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture reward items must author exactly one reward kind.",
                &[validation_detail(
                    field,
                    format!("kinds={kind_count}"),
                    "Author exactly one of gold, card, specialCard, potion, relic, cardRemoval, or linked per reward item.",
                )],
            ));
        }

        let card = self
            .card
            .map(|card| card.normalize(resolved_path, &format!("{field}.card")))
            .transpose()?;
        let special_card = self
            .special_card
            .map(|card| {
                Ok::<_, AppError>(FixtureModelRef {
                    model_id: normalize_required_model_id(
                        resolved_path,
                        &format!("{field}.specialCard.modelId"),
                        card.model_id,
                        "Provide a card modelId for the single specific-card reward.",
                    )?,
                })
            })
            .transpose()?;
        let potion = self
            .potion
            .map(|potion| {
                Ok::<_, AppError>(FixtureRewardPotion {
                    model_id: potion
                        .model_id
                        .map(str_trimmed)
                        .filter(|value| !value.is_empty()),
                })
            })
            .transpose()?;
        let relic = self
            .relic
            .map(|relic| relic.normalize(resolved_path, &format!("{field}.relic")))
            .transpose()?;
        if let Some(true) = self.card_removal {
        } else if self.card_removal.is_some() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture reward cardRemoval must be true.",
                &[validation_detail(
                    &format!("{field}.cardRemoval"),
                    "false",
                    "Set cardRemoval: true to author a card-removal reward, or omit the item.",
                )],
            ));
        }
        let linked = self
            .linked
            .map(|linked| {
                if !allow_linked {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture linked rewards must not nest.",
                        &[validation_detail(
                            &format!("{field}.linked"),
                            "nested",
                            "A linked reward may not contain another linked reward.",
                        )],
                    ));
                }
                if linked.items.is_empty() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture linked rewards must not be empty.",
                        &[validation_detail(
                            &format!("{field}.linked.items"),
                            "[]",
                            "Provide one or more rewards inside the linked reward set.",
                        )],
                    ));
                }
                Ok::<_, AppError>(FixtureLinkedReward {
                    items: linked
                        .items
                        .into_iter()
                        .enumerate()
                        .map(|(index, item)| {
                            item.normalize(
                                resolved_path,
                                &format!("{field}.linked.items[{index}]"),
                                false,
                            )
                        })
                        .collect::<Result<Vec<_>, _>>()?,
                })
            })
            .transpose()?;

        Ok(FixtureRewardItem {
            gold: self.gold,
            card,
            special_card,
            potion,
            relic,
            card_removal: self.card_removal,
            linked,
        })
    }
}

impl FixtureCardRewardInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureCardReward, AppError> {
        let has_specific = !self.model_ids.is_empty();
        let has_pool = self.room_type.is_some() || self.count.is_some();
        if has_specific && has_pool {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture card reward must be either specific cards or a pooled choice.",
                &[validation_detail(
                    field,
                    "modelIds+pool",
                    "Author modelIds for specific cards, or roomType/count for a pooled card reward, but not both.",
                )],
            ));
        }
        if !has_specific && !has_pool {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture card reward must author cards.",
                &[validation_detail(
                    field,
                    "{}",
                    "Provide modelIds for specific cards, or roomType/count for a pooled card reward.",
                )],
            ));
        }
        let model_ids = normalize_model_id_list(
            resolved_path,
            &format!("{field}.modelIds"),
            self.model_ids,
            "Provide non-empty card modelId entries for the card reward.",
            false,
        )?;
        let room_type = self
            .room_type
            .map(|value| normalize_reward_room_type(resolved_path, &format!("{field}.roomType"), value))
            .transpose()?;
        Ok(FixtureCardReward {
            model_ids,
            room_type,
            count: self.count,
            can_skip: self.can_skip,
        })
    }
}

impl FixtureRewardRelicInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureRewardRelic, AppError> {
        let model_id = self
            .model_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty());
        let rarity = self
            .rarity
            .map(str_trimmed)
            .filter(|value| !value.is_empty());
        if model_id.is_some() && rarity.is_some() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture relic reward must set either modelId or rarity.",
                &[validation_detail(
                    field,
                    "modelId+rarity",
                    "Author a specific relic via modelId, or a rarity via rarity, but not both.",
                )],
            ));
        }
        Ok(FixtureRewardRelic { model_id, rarity })
    }
}

fn normalize_reward_room_type(
    resolved_path: &Path,
    field: &str,
    value: String,
) -> Result<String, AppError> {
    let normalized = value.trim().to_ascii_lowercase();
    if !matches!(normalized.as_str(), "monster" | "elite" | "boss") {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture reward roomType must be monster, elite, or boss.",
            &[validation_detail(
                field,
                value,
                "Use roomType: monster, elite, or boss.",
            )],
        ));
    }
    Ok(normalized)
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureOverlayEntry {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub choose_a_card: Option<FixtureChooseACardOverlay>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub simple_card_selection: Option<FixtureSimpleCardSelectionOverlay>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deck_card_selection: Option<FixtureDeckCardSelectionOverlay>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bundle_selection: Option<FixtureBundleSelectionOverlay>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rewards: Option<FixtureRewardsOverlay>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_overlay: Option<FixtureCardOverlay>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureChooseACardOverlay {
    pub cards: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_skip: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureSimpleCardSelectionOverlay {
    pub cards: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_confirm: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureDeckCardSelectionOverlay {
    pub kind: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub cards: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_skip: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_confirm: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enchantment_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enchant_amount: Option<i64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureBundleSelectionOverlay {
    pub bundles: Vec<FixtureBundle>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_confirm: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRewardsOverlay {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub room_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub encounter_id: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub items: Vec<FixtureRewardItem>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRewardItem {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gold: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card: Option<FixtureCardReward>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub special_card: Option<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub potion: Option<FixtureRewardPotion>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub relic: Option<FixtureRewardRelic>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_removal: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub linked: Option<FixtureLinkedReward>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCardReward {
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub model_ids: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub room_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub count: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_skip: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRewardPotion {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub model_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRewardRelic {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub model_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rarity: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureLinkedReward {
    pub items: Vec<FixtureRewardItem>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCardOverlay {
    pub policy: String,
    pub source_screen: String,
    pub cards: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub close: Option<FixtureControl>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub follow_through: Option<FixtureOverlayFollowThrough>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureBundle {
    pub id: String,
    pub card_ids: Vec<String>,
}

// ---------------------------------------------------------------------------
// run.currentRoom
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCurrentRoomInput {
    combat: Option<FixtureCombatRoomInput>,
    event: Option<FixtureEventRoomInput>,
    treasure: Option<FixtureTreasureRoomInput>,
    shop: Option<FixtureShopRoomInput>,
    rest_site: Option<FixtureRestSiteRoomInput>,
    map_room: Option<FixtureMapRoomInput>,
}

impl FixtureCurrentRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureCurrentRoom, AppError> {
        let kind_count = [
            self.combat.is_some(),
            self.event.is_some(),
            self.treasure.is_some(),
            self.shop.is_some(),
            self.rest_site.is_some(),
            self.map_room.is_some(),
        ]
        .into_iter()
        .filter(|present| *present)
        .count();
        if kind_count != 1 {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.currentRoom must author exactly one room kind.",
                &[validation_detail(
                    "run.currentRoom",
                    format!("kinds={kind_count}"),
                    "Author exactly one of combat, event, treasure, shop, restSite, or mapRoom under run.currentRoom.",
                )],
            ));
        }

        Ok(FixtureCurrentRoom {
            combat: self
                .combat
                .map(|combat| combat.normalize(resolved_path))
                .transpose()?,
            event: self
                .event
                .map(|event| event.normalize(resolved_path))
                .transpose()?,
            treasure: self
                .treasure
                .map(|treasure| treasure.normalize(resolved_path))
                .transpose()?,
            shop: self
                .shop
                .map(|shop| shop.normalize(resolved_path))
                .transpose()?,
            rest_site: self
                .rest_site
                .map(|rest_site| rest_site.normalize(resolved_path))
                .transpose()?,
            map_room: self.map_room.map(|input| FixtureMapRoom {
                travel_to_row: input.travel_to_row,
                first_node: input.first_node,
                travel_path: input.travel_path,
            }),
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCurrentRoom {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub combat: Option<FixtureCombatRoom>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub event: Option<FixtureEventRoom>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub treasure: Option<FixtureTreasureRoom>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shop: Option<FixtureShopRoom>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rest_site: Option<FixtureRestSiteRoom>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub map_room: Option<FixtureMapRoom>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCombatRoomInput {
    encounter_id: String,
    // Per-enemy status effects, addressed by position in the live encounter's enemy
    // list (entry 0 → enemies[0], etc.). Used to author e.g. VULNERABLE_POWER on some
    // (or all) enemies so a selected attack's per-target damage preview reflects it.
    #[serde(default)]
    enemies: Vec<FixtureCombatEnemyInput>,
    // Transient, time-bound combat VFX (e.g. a cardUpgrade preview for STONE_CRACKER).
    // Normally a host synthesizes these from the combat-event stream; a fixture authors
    // them directly so the snapshot renderer can be validated statically (no live frame
    // to catch). They project to run.currentRoom.combat.combatState.transientEffects.
    #[serde(default)]
    transient_effects: Vec<FixtureCombatTransientEffectInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCombatTransientEffectInput {
    id: String,
    kind: String,
    card_model_id: Option<String>,
    card_id: Option<String>,
    source_relic_model_id: Option<String>,
    anchor_creature_id: Option<String>,
    amount: Option<i32>,
    spawned_at_ms: Option<i64>,
    // Generic native-VFX backbone: the res://-relative scene to mount for kind: vfx
    // (e.g. "vfx/hit_spark_vfx"). Empty for damage/cardUpgrade.
    scene_path: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCombatEnemyInput {
    #[serde(default)]
    powers: Vec<FixturePowerInput>,
    // Combat-only: override the enemy creature's HP on load (e.g. to show a damaged
    // bar). Applied through the game's HP setters at capture time, the same way the
    // player creature's `currentHp`/`maxHp` is. Omitted fields keep the encounter
    // defaults. `currentHp` must not exceed `maxHp` when both are given.
    current_hp: Option<u32>,
    max_hp: Option<u32>,
}

impl FixtureCombatRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureCombatRoom, AppError> {
        let enemies = self
            .enemies
            .into_iter()
            .enumerate()
            .map(|(index, enemy)| {
                if let (Some(current_hp), Some(max_hp)) = (enemy.current_hp, enemy.max_hp)
                    && current_hp > max_hp
                {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.currentRoom.combat.enemies[].currentHp must not exceed maxHp.",
                        &[validation_detail(
                            &format!("run.currentRoom.combat.enemies[{index}].currentHp"),
                            current_hp.to_string(),
                            "Use currentHp <= maxHp in the authored enemy.",
                        )],
                    ));
                }
                Ok::<_, AppError>(FixtureCombatEnemy {
                    powers: normalize_powers(
                        resolved_path,
                        &format!("run.currentRoom.combat.enemies[{index}].powers"),
                        enemy.powers,
                    )?,
                    current_hp: enemy.current_hp,
                    max_hp: enemy.max_hp,
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        let transient_effects = self
            .transient_effects
            .into_iter()
            .enumerate()
            .map(|(index, effect)| {
                if effect.id.trim().is_empty() || effect.kind.trim().is_empty() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.currentRoom.combat.transientEffects[] requires non-empty id and kind.",
                        &[validation_detail(
                            &format!("run.currentRoom.combat.transientEffects[{index}]"),
                            effect.id.clone(),
                            "Provide an id (DOM/animation key) and kind (e.g. cardUpgrade).",
                        )],
                    ));
                }
                Ok::<_, AppError>(FixtureCombatTransientEffect {
                    id: effect.id.trim().to_string(),
                    kind: effect.kind.trim().to_string(),
                    card_model_id: normalize_optional_trimmed(effect.card_model_id),
                    card_id: normalize_optional_trimmed(effect.card_id),
                    source_relic_model_id: normalize_optional_trimmed(effect.source_relic_model_id),
                    anchor_creature_id: normalize_optional_trimmed(effect.anchor_creature_id),
                    amount: effect.amount,
                    spawned_at_ms: effect.spawned_at_ms,
                    scene_path: normalize_optional_trimmed(effect.scene_path),
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        Ok(FixtureCombatRoom {
            encounter_id: normalize_required_model_id(
                resolved_path,
                "run.currentRoom.combat.encounterId",
                self.encounter_id,
                "Provide a runtime-resolvable encounter id for combat loading.",
            )?,
            enemies,
            transient_effects,
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCombatRoom {
    pub encounter_id: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub enemies: Vec<FixtureCombatEnemy>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub transient_effects: Vec<FixtureCombatTransientEffect>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCombatTransientEffect {
    pub id: String,
    pub kind: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_model_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_relic_model_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub anchor_creature_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub spawned_at_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scene_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCombatEnemy {
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub powers: Vec<FixturePower>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_hp: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_hp: Option<u32>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventRoomInput {
    canonical_event_model_id: String,
    #[serde(default)]
    player_states: Vec<FixtureEventPlayerStateInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventPlayerStateInput {
    player_id: Option<String>,
    #[serde(default)]
    options: Vec<FixtureEventOptionInput>,
    ancient: Option<FixtureEventAncientInput>,
    crystal_sphere: Option<FixtureCrystalSphereInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventOptionInput {
    id: Option<String>,
    title_text: Option<String>,
    text_key: Option<String>,
    was_chosen: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventAncientInput {
    view: FixtureEventAncientViewInput,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventAncientViewInput {
    visible_dialogue: FixtureEventAncientVisibleDialogueInput,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureEventAncientVisibleDialogueInput {
    dialogue_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCrystalSphereInput {
    selected_tool: String,
    divinations_remaining: u32,
    #[serde(default)]
    cells: Vec<FixtureCrystalSphereCellInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCrystalSphereCellInput {
    id: String,
    x: u32,
    y: u32,
    is_hidden: bool,
}

impl FixtureEventRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureEventRoom, AppError> {
        let canonical_event_model_id = normalize_required_model_id(
            resolved_path,
            "run.currentRoom.event.canonicalEventModelId",
            self.canonical_event_model_id,
            "Provide a stable event id for the authored event-room recipe.",
        )?;
        let player_states = self
            .player_states
            .into_iter()
            .enumerate()
            .map(|(index, state)| {
                let field = format!("run.currentRoom.event.playerStates[{index}]");
                let options = state
                    .options
                    .into_iter()
                    .enumerate()
                    .map(|(option_index, option)| {
                        let option_field = format!("{field}.options[{option_index}]");
                        let id = option
                            .id
                            .map(|id| {
                                normalize_required_fixture_id(
                                    resolved_path,
                                    &format!("{option_field}.id"),
                                    id,
                                    "Provide a stable authored option id.",
                                )
                            })
                            .transpose()?;
                        let title_text = option
                            .title_text
                            .map(|title_text| {
                                normalize_required_fixture_id(
                                    resolved_path,
                                    &format!("{option_field}.titleText"),
                                    title_text,
                                    "Provide the visible label for the authored option.",
                                )
                            })
                            .transpose()?;
                        let text_key = option
                            .text_key
                            .map(|text_key| {
                                normalize_required_fixture_id(
                                    resolved_path,
                                    &format!("{option_field}.textKey"),
                                    text_key,
                                    "Provide the game EventOption.TextKey for state-shaped event options.",
                                )
                            })
                            .transpose()?;
                        if (id.is_some() && title_text.is_none())
                            || (id.is_none() && title_text.is_some())
                        {
                            return Err(AppError::invalid_fixture(
                                resolved_path,
                                "fixture event options must author id/titleText together.",
                                &[validation_detail(
                                    &option_field,
                                    "partial",
                                    "Provide both id and titleText for visible option assertions, or provide textKey for state-shaped event option replay.",
                                )],
                            ));
                        }
                        if id.is_none() && text_key.is_none() {
                            return Err(AppError::invalid_fixture(
                                resolved_path,
                                "fixture event options require a stable identity.",
                                &[validation_detail(
                                    &option_field,
                                    "missing",
                                    "Provide id/titleText for visible option assertions, or textKey for state-shaped event option replay.",
                                )],
                            ));
                        }
                        Ok(FixtureEventOption {
                            id,
                            title_text,
                            text_key,
                            was_chosen: option.was_chosen,
                        })
                    })
                    .collect::<Result<Vec<_>, AppError>>()?;
                if is_crystal_sphere_event_id(&canonical_event_model_id)
                    && options
                        .iter()
                        .filter(|option| option.was_chosen == Some(true))
                        .count()
                        > 1
                {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture Crystal Sphere event state has multiple chosen options.",
                        &[validation_detail(
                            &format!("{field}.options"),
                            "multiple wasChosen=true",
                            "Author exactly one Crystal Sphere option with wasChosen: true when loading the grid state.",
                        )],
                    ));
                }
                let crystal_sphere = state
                    .crystal_sphere
                    .map(|crystal_sphere| {
                        normalize_crystal_sphere_state(
                            resolved_path,
                            &field,
                            &canonical_event_model_id,
                            &options,
                            crystal_sphere,
                        )
                    })
                    .transpose()?;
                let ancient = state
                    .ancient
                    .map(|ancient| {
                        let dialogue_id = normalize_required_fixture_id(
                            resolved_path,
                            &format!("{field}.ancient.view.visibleDialogue.dialogueId"),
                            ancient.view.visible_dialogue.dialogue_id,
                            "Provide the visible ancient dialogue id (e.g. NEOW.talk.ANY.4).",
                        )?;
                        Ok::<_, AppError>(FixtureEventAncient {
                            view: FixtureEventAncientView {
                                visible_dialogue: FixtureEventAncientVisibleDialogue {
                                    dialogue_id,
                                },
                            },
                        })
                    })
                    .transpose()?;
                if options.is_empty() && ancient.is_none() && crystal_sphere.is_none() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture event playerStates entries must author options or ancient state.",
                        &[validation_detail(
                            &field,
                            "empty",
                            "Provide visible event options, ancient dialogue state, Crystal Sphere state, or omit the playerStates entry entirely.",
                        )],
                    ));
                }
                Ok(FixtureEventPlayerState {
                    player_id: state
                        .player_id
                        .map(str_trimmed)
                        .filter(|value| !value.is_empty()),
                    options,
                    ancient,
                    crystal_sphere,
                })
            })
            .collect::<Result<Vec<_>, AppError>>()?;

        Ok(FixtureEventRoom {
            canonical_event_model_id,
            player_states,
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventRoom {
    pub canonical_event_model_id: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub player_states: Vec<FixtureEventPlayerState>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventPlayerState {
    // Defaulted to the view player during run normalization.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_id: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub options: Vec<FixtureEventOption>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ancient: Option<FixtureEventAncient>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub crystal_sphere: Option<FixtureCrystalSphere>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventOption {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title_text: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub was_chosen: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventAncient {
    pub view: FixtureEventAncientView,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventAncientView {
    pub visible_dialogue: FixtureEventAncientVisibleDialogue,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureEventAncientVisibleDialogue {
    pub dialogue_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCrystalSphere {
    pub selected_tool: String,
    pub divinations_remaining: u32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub cells: Vec<FixtureCrystalSphereCell>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCrystalSphereCell {
    pub id: String,
    pub x: u32,
    pub y: u32,
    pub is_hidden: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureTreasureRoomInput {
    current_relics_active: Option<bool>,
    // FIXTURE-ONLY: opens the choose-a-relic selection screen on top of the
    // treasure room (state has no marker for NChooseARelicSelection).
    relic_selection_open: Option<bool>,
    #[serde(default)]
    current_relics: Vec<FixtureModelRefInput>,
    can_proceed: Option<bool>,
    #[serde(default)]
    player_votes: Vec<FixtureTreasurePlayerVoteInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureTreasurePlayerVoteInput {
    player_id: String,
    #[serde(default)]
    index: Option<i32>,
}

impl FixtureTreasureRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureTreasureRoom, AppError> {
        let relic_selection_open = self.relic_selection_open.unwrap_or(false);
        let current_relics_active = self.current_relics_active.unwrap_or(false);
        let current_relics = normalize_model_refs(
            resolved_path,
            "run.currentRoom.treasure.currentRelics",
            self.current_relics,
            "Provide relic modelId entries, or omit currentRelics entirely.",
        )?;

        if relic_selection_open {
            if current_relics_active {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.currentRelicsActive is not supported for relic-selection fixtures.",
                    &[validation_detail(
                        "run.currentRoom.treasure.currentRelicsActive",
                        "true",
                        "relicSelectionOpen authors the choose-a-relic screen; remove currentRelicsActive.",
                    )],
                ));
            }
            if self.can_proceed.is_some() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.canProceed is not supported for relic-selection fixtures.",
                    &[validation_detail(
                        "run.currentRoom.treasure.canProceed",
                        self.can_proceed.unwrap_or_default().to_string(),
                        "Remove canProceed for relic-selection fixtures; the live loader only realizes visible relic picks there.",
                    )],
                ));
            }
        } else {
            if !current_relics.is_empty() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.currentRelics is not supported for treasure-room fixtures.",
                    &[validation_detail(
                        "run.currentRoom.treasure.currentRelics",
                        format!("count={}", current_relics.len()),
                        "Remove currentRelics for treasure-room fixtures; visible relic picks are only authored with relicSelectionOpen: true today.",
                    )],
                ));
            }
            if !current_relics_active && self.can_proceed.is_some() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.canProceed is not supported for closed treasure-room fixtures.",
                    &[validation_detail(
                        "run.currentRoom.treasure.canProceed",
                        self.can_proceed.unwrap_or_default().to_string(),
                        "Only opened treasure rooms (currentRelicsActive: true) may author the proceed control.",
                    )],
                ));
            }
        }

        if !self.player_votes.is_empty() && !current_relics_active {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture treasure.playerVotes is only supported for opened treasure-room fixtures.",
                &[validation_detail(
                    "run.currentRoom.treasure.playerVotes",
                    self.player_votes.len().to_string(),
                    "Set currentRelicsActive: true to author player votes; closed and relic-selection fixtures do not expose the vote synchronizer.",
                )],
            ));
        }

        let mut player_votes = Vec::with_capacity(self.player_votes.len());
        for vote in self.player_votes {
            let player_id = vote.player_id.trim().to_string();
            if player_id.is_empty() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.playerVotes[].playerId is required.",
                    &[validation_detail(
                        "run.currentRoom.treasure.playerVotes[].playerId",
                        vote.player_id,
                        "Provide the authored playerId (e.g. p:2) that has already cast a treasure vote.",
                    )],
                ));
            }
            if let Some(index) = vote.index
                && index < 0
            {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture treasure.playerVotes[].index must be non-negative.",
                    &[validation_detail(
                        "run.currentRoom.treasure.playerVotes[].index",
                        index.to_string(),
                        "Use a 0-based relic index, or omit index to author a skip vote.",
                    )],
                ));
            }
            player_votes.push(FixtureTreasurePlayerVote {
                player_id,
                index: vote.index,
            });
        }

        Ok(FixtureTreasureRoom {
            current_relics_active,
            relic_selection_open,
            current_relics,
            can_proceed: self.can_proceed,
            player_votes,
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureTreasureRoom {
    pub current_relics_active: bool,
    pub relic_selection_open: bool,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub current_relics: Vec<FixtureModelRef>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub can_proceed: Option<bool>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub player_votes: Vec<FixtureTreasurePlayerVote>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureTreasurePlayerVote {
    pub player_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub index: Option<i32>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureShopRoomInput {
    inventory: Option<FixtureShopInventoryInput>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureShopInventoryInput {
    #[serde(default)]
    character_card_entries: Vec<FixtureShopCardEntryInput>,
    #[serde(default)]
    relic_entries: Vec<FixtureShopModelEntryInput>,
    #[serde(default)]
    potion_entries: Vec<FixtureShopModelEntryInput>,
    card_removal_entry: Option<FixtureShopCardRemovalEntryInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureShopCardEntryInput {
    card: FixtureModelRefInput,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureShopModelEntryInput {
    model_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureShopCardRemovalEntryInput {
    used: Option<bool>,
}

impl FixtureShopRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureShopRoom, AppError> {
        let inventory = self
            .inventory
            .map(|inventory| {
                let character_card_entries = inventory
                    .character_card_entries
                    .into_iter()
                    .enumerate()
                    .map(|(index, entry)| {
                        let model_id = normalize_required_model_id(
                            resolved_path,
                            &format!(
                                "run.currentRoom.shop.inventory.characterCardEntries[{index}].card.modelId"
                            ),
                            entry.card.model_id,
                            "Provide an authored card id for each shop card entry.",
                        )?;
                        Ok(FixtureShopCardEntry {
                            card: FixtureModelRef { model_id },
                        })
                    })
                    .collect::<Result<Vec<_>, AppError>>()?;
                let relic_entries = normalize_shop_model_entries(
                    resolved_path,
                    "run.currentRoom.shop.inventory.relicEntries",
                    inventory.relic_entries,
                )?;
                let potion_entries = normalize_shop_model_entries(
                    resolved_path,
                    "run.currentRoom.shop.inventory.potionEntries",
                    inventory.potion_entries,
                )?;
                Ok::<_, AppError>(FixtureShopInventory {
                    character_card_entries,
                    relic_entries,
                    potion_entries,
                    card_removal_entry: inventory.card_removal_entry.map(|entry| {
                        FixtureShopCardRemovalEntry {
                            used: entry.used.unwrap_or(false),
                        }
                    }),
                })
            })
            .transpose()?;
        Ok(FixtureShopRoom { inventory })
    }
}

fn normalize_shop_model_entries(
    resolved_path: &Path,
    field: &str,
    entries: Vec<FixtureShopModelEntryInput>,
) -> Result<Vec<FixtureShopModelEntry>, AppError> {
    entries
        .into_iter()
        .enumerate()
        .map(|(index, entry)| {
            let model_id = normalize_required_model_id(
                resolved_path,
                &format!("{field}[{index}].modelId"),
                entry.model_id,
                "Provide an authored model id for each shop entry.",
            )?;
            Ok(FixtureShopModelEntry { model_id })
        })
        .collect()
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureShopRoom {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inventory: Option<FixtureShopInventory>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureShopInventory {
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub character_card_entries: Vec<FixtureShopCardEntry>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub relic_entries: Vec<FixtureShopModelEntry>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub potion_entries: Vec<FixtureShopModelEntry>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_removal_entry: Option<FixtureShopCardRemovalEntry>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureShopCardEntry {
    pub card: FixtureModelRef,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureShopModelEntry {
    pub model_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureShopCardRemovalEntry {
    pub used: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRestSiteRoomInput {
    #[serde(default)]
    player_states: Vec<FixtureRestSitePlayerStateInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRestSitePlayerStateInput {
    player_id: Option<String>,
    #[serde(default)]
    options: Vec<FixtureRestSiteOptionInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRestSiteOptionInput {
    option_id: String,
}

impl FixtureRestSiteRoomInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureRestSiteRoom, AppError> {
        let player_states = self
            .player_states
            .into_iter()
            .enumerate()
            .map(|(index, state)| {
                let field = format!("run.currentRoom.restSite.playerStates[{index}]");
                let options = state
                    .options
                    .into_iter()
                    .enumerate()
                    .map(|(option_index, option)| {
                        let option_id = normalize_required_fixture_id(
                            resolved_path,
                            &format!("{field}.options[{option_index}].optionId"),
                            option.option_id,
                            "Provide rest-site option ids to force-show (e.g. CLONE, HATCH).",
                        )?;
                        Ok(FixtureRestSiteOption { option_id })
                    })
                    .collect::<Result<Vec<_>, AppError>>()?;
                if options.is_empty() {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture restSite playerStates entries must author options.",
                        &[validation_detail(
                            &format!("{field}.options"),
                            "[]",
                            "Provide force-shown rest-site options, or omit the playerStates entry entirely.",
                        )],
                    ));
                }
                Ok(FixtureRestSitePlayerState {
                    player_id: state
                        .player_id
                        .map(str_trimmed)
                        .filter(|value| !value.is_empty()),
                    options,
                })
            })
            .collect::<Result<Vec<_>, AppError>>()?;
        Ok(FixtureRestSiteRoom { player_states })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRestSiteRoom {
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub player_states: Vec<FixtureRestSitePlayerState>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRestSitePlayerState {
    // Defaulted to the view player during run normalization.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub player_id: Option<String>,
    pub options: Vec<FixtureRestSiteOption>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRestSiteOption {
    pub option_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureMapRoomInput {
    // Author a "progressed" map: the loader auto-walks a connected path from the start node up to
    // this row, populating run.visitedMapCoords (so the current location = the deepest reached node
    // and the already-walked edges render as the traveled route). 0/absent = the default
    // start-parked map (only the start node visited).
    travel_to_row: Option<u32>,
    // The starting (row-0) node's kind: "combat" (a Monster node, like a profile's FIRST run, where
    // Neow has not appeared yet) or "ancient" (the Neow node — the normal start of any later run/act).
    // Absent = "combat" (the historical default). Drives the start node's pointType + the game's
    // StartedWithNeow flag so the rendered first node matches.
    first_node: Option<String>,
    // Per-row types for an authored traveled path (a richer alternative to travel_to_row): one entry
    // per visited node from row 0 upward, each a MapPointType name (Monster/Elite/Shop/Treasure/
    // RestSite/Unknown) or "Unknown:<RoomType>" for a revealed `?` node (e.g. "Unknown:Event",
    // "Unknown:Shop"). The loader walks the deterministic connected path that deep and stamps each
    // visited node with the authored type, so the traveled route shows all node kinds incl. revealed
    // unknowns. When present it supersedes travel_to_row.
    travel_path: Option<Vec<String>>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureMapRoom {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub travel_to_row: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_node: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub travel_path: Option<Vec<String>>,
}

// ---------------------------------------------------------------------------
// run.view
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureRunViewInput {
    player_id: Option<String>,
    capstone: Option<FixtureCapstoneViewInput>,
    selected_potion: Option<FixtureSelectedPotionInput>,
    selected_card: Option<FixtureSelectedCardInput>,
    inspect_relic: Option<FixtureInspectRelicViewInput>,
    hand_selection: Option<FixtureHandSelectionViewInput>,
}

// In-hand card selection mode (state run.view.handSelection, e.g.
// Survivor's "Discard 1 card."). The loader enters NPlayerHand.SelectCards
// for real, so select/deselect/confirm-hand-selection actions are
// live-functional against the fixture (confirming resolves the loader's
// selection task with no further card effect). selectedCardIndexes pre-stages
// picks by dealt-hand position (0 = leftmost).
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureHandSelectionViewInput {
    // Informational: surfaces as run.view.handSelection.sourceModelId (the
    // card that "opened" the selection, e.g. SURVIVOR or ARMAMENTS).
    source_model_id: Option<String>,
    // Hand selection mode: simple-select (default; e.g. Survivor's discard) or
    // upgrade-select (e.g. Armaments — shows the before/after upgrade preview).
    mode: Option<String>,
    // Prompt kind; currently only "discard" (the game's TO_DISCARD prompt).
    // Ignored in upgrade-select mode (the upgrade screen owns its own prompt).
    prompt: Option<String>,
    min_select: Option<u32>,
    max_select: Option<u32>,
    #[serde(default)]
    selected_card_indexes: Vec<u32>,
}

impl FixtureHandSelectionViewInput {
    fn normalize(
        self,
        resolved_path: &Path,
        has_combat_room: bool,
    ) -> Result<FixtureHandSelectionView, AppError> {
        if !has_combat_room {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.handSelection requires run.currentRoom.combat.",
                &[validation_detail(
                    "run.view.handSelection",
                    "present",
                    "In-hand selection is combat hand UI; author a combat room alongside it.",
                )],
            ));
        }
        let mode = self
            .mode
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "simple-select".to_string());
        if mode != "simple-select" && mode != "upgrade-select" {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.handSelection.mode is not supported.",
                &[validation_detail(
                    "run.view.handSelection.mode",
                    mode,
                    "Use simple-select (default) or upgrade-select.",
                )],
            ));
        }
        let prompt = self
            .prompt
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "discard".to_string());
        // The discard prompt only applies to simple-select; upgrade-select drives
        // the game's own upgrade screen prompt.
        if mode == "simple-select" && prompt != "discard" {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.handSelection.prompt is not supported.",
                &[validation_detail(
                    "run.view.handSelection.prompt",
                    prompt,
                    "Use discard (the game's TO_DISCARD selection prompt) or omit the field.",
                )],
            ));
        }
        let min_select = self.min_select.unwrap_or(1);
        let max_select = self.max_select.unwrap_or(min_select.max(1));
        if max_select == 0 || min_select > max_select {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.handSelection selection bounds are invalid.",
                &[validation_detail(
                    "run.view.handSelection",
                    format!("minSelect={min_select}, maxSelect={max_select}"),
                    "Require maxSelect >= 1 and minSelect <= maxSelect.",
                )],
            ));
        }
        if self.selected_card_indexes.len() > max_select as usize {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.handSelection.selectedCardIndexes exceeds maxSelect.",
                &[validation_detail(
                    "run.view.handSelection.selectedCardIndexes",
                    format!("count={}", self.selected_card_indexes.len()),
                    "Pre-stage at most maxSelect cards.",
                )],
            ));
        }
        Ok(FixtureHandSelectionView {
            source_model_id: self
                .source_model_id
                .map(str_trimmed)
                .filter(|value| !value.is_empty()),
            mode,
            prompt,
            min_select,
            max_select,
            selected_card_indexes: self.selected_card_indexes,
        })
    }
}

// The relic-details overlay (NInspectRelicScreen). The fixture selects the
// displayed relic by model id; the live relic bar is the browse list (the
// character's starter relic plus authored relics[]), so index/count are
// resolved live rather than authored.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureInspectRelicViewInput {
    relic_model_id: String,
}

impl FixtureInspectRelicViewInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureInspectRelicView, AppError> {
        let relic_model_id = str_trimmed(self.relic_model_id);
        if relic_model_id.is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.inspectRelic.relicModelId must not be empty.",
                &[validation_detail(
                    "run.view.inspectRelic.relicModelId",
                    relic_model_id,
                    "Reference a relic on the view player's relic bar (the starter relic or one of relics[]).",
                )],
            ));
        }
        Ok(FixtureInspectRelicView { relic_model_id })
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCapstoneViewInput {
    card_pile_view: Option<FixtureCardPileViewInput>,
    deck_view: Option<FixtureDeckViewInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureCardPileViewInput {
    player_id: Option<String>,
    pile_type: String,
}

// The master deck viewer (NDeckViewScreen). Unlike cardPileView (a combat
// draw/discard/exhaust pile), the deck view is the whole-run deck and is opened
// by the top-bar Deck button. sort[] lists the applied sort keys (each toggles
// the matching sorter control) and showUpgrades previews upgraded cards.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureDeckViewInput {
    player_id: Option<String>,
    #[serde(default)]
    sort: Vec<FixtureDeckViewSortInput>,
    show_upgrades: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureDeckViewSortInput {
    by: String,
    direction: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureSelectedPotionInput {
    slot_index: u32,
    mode: String,
}

// FIXTURE-ONLY: state carries the runtime hand-card id, which is unknowable at
// authoring time, so the fixture selects by card model id instead. The loader
// resolves the first matching opening-hand card.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureSelectedCardInput {
    player_id: Option<String>,
    card_model_id: String,
}

impl FixtureCapstoneViewInput {
    fn normalize(
        self,
        resolved_path: &Path,
        player_ids: &std::collections::BTreeSet<String>,
        view_player_id: &str,
    ) -> Result<Option<FixtureCapstoneView>, AppError> {
        if self.card_pile_view.is_some() && self.deck_view.is_some() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.capstone may open only one of cardPileView or deckView.",
                &[validation_detail(
                    "run.view.capstone",
                    "cardPileView + deckView",
                    "Author either a combat pile (cardPileView) or the master deck (deckView), not both.",
                )],
            ));
        }
        if let Some(deck_view) = self.deck_view {
            let deck_view = deck_view.normalize(resolved_path, player_ids, view_player_id)?;
            return Ok(Some(FixtureCapstoneView {
                card_pile_view: None,
                deck_view: Some(deck_view),
            }));
        }
        let Some(card_pile_view) = self.card_pile_view else {
            return Ok(None);
        };
        let player_id = match card_pile_view
            .player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
        {
            Some(value) => {
                if !player_ids.contains(&value) {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.view.capstone.cardPileView.playerId must refer to a declared player.",
                        &[validation_detail(
                            "run.view.capstone.cardPileView.playerId",
                            value,
                            "Point cardPileView.playerId at the pile owner from run.players[].id.",
                        )],
                    ));
                }
                value
            }
            None => view_player_id.to_string(),
        };
        let pile_type = card_pile_view.pile_type.trim().to_ascii_lowercase();
        if pile_type != "draw" && pile_type != "discard" && pile_type != "exhaust" {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.capstone.cardPileView.pileType is not supported.",
                &[validation_detail(
                    "run.view.capstone.cardPileView.pileType",
                    pile_type,
                    "Use draw, discard, or exhaust.",
                )],
            ));
        }
        Ok(Some(FixtureCapstoneView {
            card_pile_view: Some(FixtureCardPileView {
                player_id,
                pile_type,
            }),
            deck_view: None,
        }))
    }
}

impl FixtureDeckViewInput {
    fn normalize(
        self,
        resolved_path: &Path,
        player_ids: &std::collections::BTreeSet<String>,
        view_player_id: &str,
    ) -> Result<FixtureDeckView, AppError> {
        let player_id = match self
            .player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
        {
            Some(value) => {
                if !player_ids.contains(&value) {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.view.capstone.deckView.playerId must refer to a declared player.",
                        &[validation_detail(
                            "run.view.capstone.deckView.playerId",
                            value,
                            "Point deckView.playerId at the deck owner from run.players[].id.",
                        )],
                    ));
                }
                value
            }
            None => view_player_id.to_string(),
        };
        let mut sort = Vec::with_capacity(self.sort.len());
        for (index, entry) in self.sort.into_iter().enumerate() {
            let by = entry.by.trim().to_ascii_lowercase();
            if by != "obtained" && by != "type" && by != "cost" && by != "alphabet" {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture run.view.capstone.deckView.sort[].by is not supported.",
                    &[validation_detail(
                        &format!("run.view.capstone.deckView.sort[{index}].by"),
                        by,
                        "Use obtained, type, cost, or alphabet.",
                    )],
                ));
            }
            let direction = match entry
                .direction
                .map(|value| value.trim().to_ascii_lowercase())
                .filter(|value| !value.is_empty())
            {
                Some(value) => {
                    if value != "ascending" && value != "descending" {
                        return Err(AppError::invalid_fixture(
                            resolved_path,
                            "fixture run.view.capstone.deckView.sort[].direction is not supported.",
                            &[validation_detail(
                                &format!("run.view.capstone.deckView.sort[{index}].direction"),
                                value,
                                "Use ascending or descending.",
                            )],
                        ));
                    }
                    value
                }
                None => "ascending".to_string(),
            };
            sort.push(FixtureDeckViewSort { by, direction });
        }
        Ok(FixtureDeckView {
            player_id,
            sort,
            show_upgrades: self.show_upgrades.unwrap_or(false),
        })
    }
}

impl FixtureSelectedPotionInput {
    fn normalize(self, resolved_path: &Path) -> Result<FixtureSelectedPotion, AppError> {
        let mode = normalize_required_fixture_id(
            resolved_path,
            "run.view.selectedPotion.mode",
            self.mode,
            "Use popup or targeting for authored potion UI setup.",
        )?;
        if mode != "popup" && mode != "targeting" {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.selectedPotion.mode is not supported.",
                &[validation_detail(
                    "run.view.selectedPotion.mode",
                    mode,
                    "Use popup or targeting.",
                )],
            ));
        }
        Ok(FixtureSelectedPotion {
            slot_index: self.slot_index,
            mode,
        })
    }
}

impl FixtureSelectedCardInput {
    fn normalize(
        self,
        resolved_path: &Path,
        player_ids: &std::collections::BTreeSet<String>,
        view_player_id: &str,
    ) -> Result<FixtureSelectedCard, AppError> {
        let player_id = match self
            .player_id
            .map(str_trimmed)
            .filter(|value| !value.is_empty())
        {
            Some(value) => {
                if !player_ids.contains(&value) {
                    return Err(AppError::invalid_fixture(
                        resolved_path,
                        "fixture run.view.selectedCard.playerId must refer to a declared player.",
                        &[validation_detail(
                            "run.view.selectedCard.playerId",
                            value,
                            "Point selectedCard.playerId at the card owner from run.players[].id.",
                        )],
                    ));
                }
                value
            }
            None => view_player_id.to_string(),
        };
        let card_model_id = normalize_required_model_id(
            resolved_path,
            "run.view.selectedCard.cardModelId",
            self.card_model_id,
            "Use a card model id (e.g. STRIKE_IRONCLAD) present in the player's opening hand.",
        )?;
        Ok(FixtureSelectedCard {
            player_id,
            card_model_id,
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureRunView {
    pub player_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub capstone: Option<FixtureCapstoneView>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_potion: Option<FixtureSelectedPotion>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_card: Option<FixtureSelectedCard>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub inspect_relic: Option<FixtureInspectRelicView>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hand_selection: Option<FixtureHandSelectionView>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureHandSelectionView {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_model_id: Option<String>,
    pub mode: String,
    pub prompt: String,
    pub min_select: u32,
    pub max_select: u32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub selected_card_indexes: Vec<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureInspectRelicView {
    pub relic_model_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCapstoneView {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_pile_view: Option<FixtureCardPileView>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deck_view: Option<FixtureDeckView>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureCardPileView {
    pub player_id: String,
    pub pile_type: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureDeckView {
    pub player_id: String,
    pub sort: Vec<FixtureDeckViewSort>,
    pub show_upgrades: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureDeckViewSort {
    pub by: String,
    pub direction: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureSelectedPotion {
    pub slot_index: u32,
    pub mode: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureSelectedCard {
    pub player_id: String,
    pub card_model_id: String,
}

// ---------------------------------------------------------------------------
// shared overlay controls
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureControlInput {
    id: String,
    label: Option<String>,
    enabled: Option<bool>,
    intent: Option<String>,
}

impl FixtureControlInput {
    fn normalize(self, resolved_path: &Path, field: &str) -> Result<FixtureControl, AppError> {
        Ok(FixtureControl {
            id: normalize_required_fixture_id(
                resolved_path,
                &format!("{field}.id"),
                self.id,
                "Provide a stable visible control id.",
            )?,
            label: self
                .label
                .map(str_trimmed)
                .filter(|value| !value.is_empty()),
            enabled: self.enabled,
            intent: self
                .intent
                .map(str_trimmed)
                .filter(|value| !value.is_empty()),
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureControl {
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub label: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enabled: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub intent: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FixtureOverlayFollowThroughInput {
    controls: Vec<FixtureControlInput>,
}

impl FixtureOverlayFollowThroughInput {
    fn normalize(
        self,
        resolved_path: &Path,
        field: &str,
    ) -> Result<FixtureOverlayFollowThrough, AppError> {
        if self.controls.is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture cardOverlay.followThrough.controls must not be empty.",
                &[validation_detail(
                    &format!("{field}.cardOverlay.followThrough.controls"),
                    "[]",
                    "Provide visible follow-through controls or omit followThrough entirely.",
                )],
            ));
        }
        Ok(FixtureOverlayFollowThrough {
            controls: self
                .controls
                .into_iter()
                .enumerate()
                .map(|(index, control)| {
                    control.normalize(
                        resolved_path,
                        &format!("{field}.cardOverlay.followThrough.controls[{index}]"),
                    )
                })
                .collect::<Result<Vec<_>, _>>()?,
        })
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FixtureOverlayFollowThrough {
    pub controls: Vec<FixtureControl>,
}

// ---------------------------------------------------------------------------
// recipe derivation + player/perspective rules
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq)]
enum PlayerRule {
    SinglePlayer,
    HostLocalAllowed,
}

fn screen_player_rule(screen: &str) -> PlayerRule {
    match screen {
        "combat"
        | bridge::EVENT_ROOM_SCREEN_ID
        | bridge::TREASURE_ROOM_SCREEN_ID
        | bridge::REST_SITE_SCREEN_ID
        | bridge::REWARDS_SCREEN_ID => PlayerRule::HostLocalAllowed,
        _ => PlayerRule::SinglePlayer,
    }
}

fn screen_label(screen: &str) -> &str {
    match screen {
        "combat" => "combat",
        bridge::EVENT_ROOM_SCREEN_ID => "event-room",
        bridge::TREASURE_ROOM_SCREEN_ID => "treasure-room",
        bridge::REST_SITE_SCREEN_ID => "rest-site",
        bridge::GAME_OVER_SCREEN_ID => "game-over",
        other => other,
    }
}

/// Derives the loader recipe (wire `screen` hint) from the document structure
/// and enforces the structural exclusivity rules: exactly one of
/// rootScene/currentRoom/overlays may decide the recipe, except the rest-site +
/// deck-card-selection composition.
fn derive_run_screen(
    resolved_path: &Path,
    root_scene: Option<&str>,
    current_room: Option<&FixtureCurrentRoom>,
    players: &[FixtureRunPlayer],
    game_over: Option<&FixtureGameOver>,
) -> Result<String, AppError> {
    let overlay_entries = players
        .iter()
        .flat_map(|player| player.overlays.iter())
        .collect::<Vec<_>>();
    if overlay_entries.len() > 1 {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture authors more than one overlay entry.",
            &[validation_detail(
                "run.players[].overlays",
                format!("count={}", overlay_entries.len()),
                "The fixture loader currently supports one authored overlay entry per fixture.",
            )],
        ));
    }
    let overlay = overlay_entries.first().copied();

    // A run-terminal defeat is an overlay pushed over a finished run: it cannot
    // coexist with a room, an in-run overlay, or a non-run root scene.
    if game_over.is_some() {
        if root_scene.is_some() || current_room.is_some() || overlay.is_some() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.gameOver cannot be combined with rootScene, currentRoom, or overlays.",
                &[validation_detail(
                    "run.gameOver",
                    "present",
                    "A defeat outcome is a terminal overlay over a finished run; remove rootScene/currentRoom/overlays.",
                )],
            ));
        }
        return Ok(bridge::GAME_OVER_SCREEN_ID.to_string());
    }

    if root_scene == Some(MAIN_MENU_ROOT_SCENE) {
        if current_room.is_some() || overlay.is_some() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture rootScene: main-menu cannot be combined with rooms or overlays.",
                &[validation_detail(
                    "rootScene",
                    MAIN_MENU_ROOT_SCENE,
                    "Remove run.currentRoom and run.players[].overlays for main-menu fixtures.",
                )],
            ));
        }
        return Ok("main-menu".to_string());
    }

    if let Some(room) = current_room {
        let room_screen = if room.combat.is_some() {
            "combat".to_string()
        } else if let Some(event) = room.event.as_ref() {
            if is_crystal_sphere_event_id(&event.canonical_event_model_id)
                && event.player_states.iter().any(|state| {
                    state
                        .options
                        .iter()
                        .any(|option| option.was_chosen == Some(true))
                })
            {
                bridge::CRYSTAL_SPHERE_SCREEN_ID.to_string()
            } else {
                bridge::EVENT_ROOM_SCREEN_ID.to_string()
            }
        } else if let Some(treasure) = room.treasure.as_ref() {
            if treasure.relic_selection_open {
                bridge::RELIC_SELECTION_SCREEN_ID.to_string()
            } else {
                bridge::TREASURE_ROOM_SCREEN_ID.to_string()
            }
        } else if room.shop.is_some() {
            bridge::SHOP_SCREEN_ID.to_string()
        } else if room.rest_site.is_some() {
            bridge::REST_SITE_SCREEN_ID.to_string()
        } else {
            bridge::MAP_SCREEN_ID.to_string()
        };

        if let Some(overlay) = overlay {
            // Room-composed overlays: the rest-site SMITH/upgrade dialog and the
            // event enchant/upgrade dialog (a deckCardSelection without authored
            // cards — the live screen reads the player's own deck, e.g. Field of
            // Man-Sized Holes -> PerfectFit enchant), and mid-combat card-choice
            // overlays (chooseACard, e.g. Attack Potion; simpleCardSelection,
            // e.g. Headbutt; bundleSelection, e.g. ScrollBoxes).
            let is_rest_site_deck_overlay =
                room.rest_site.is_some() && overlay.deck_card_selection.is_some();
            let is_event_deck_overlay =
                room.event.is_some() && overlay.deck_card_selection.is_some();
            let is_combat_choose_a_card_overlay =
                room.combat.is_some() && overlay.choose_a_card.is_some();
            let is_combat_simple_grid_overlay =
                room.combat.is_some() && overlay.simple_card_selection.is_some();
            let is_combat_bundle_overlay =
                room.combat.is_some() && overlay.bundle_selection.is_some();
            if !is_rest_site_deck_overlay
                && !is_event_deck_overlay
                && !is_combat_choose_a_card_overlay
                && !is_combat_simple_grid_overlay
                && !is_combat_bundle_overlay
            {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture overlays cannot be combined with this run.currentRoom kind.",
                    &[validation_detail(
                        "run.players[].overlays",
                        "present",
                        "Room-composed overlays are limited to rest-site + deckCardSelection (smith dialog), event + deckCardSelection (event enchant/upgrade dialog), and combat + chooseACard/simpleCardSelection/bundleSelection (mid-combat card choices); other rooms derive their screen from run.currentRoom alone.",
                    )],
                ));
            }
            if let Some(deck) = overlay.deck_card_selection.as_ref()
                && !deck.cards.is_empty()
            {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "room-composed deck-card-selection overlays read cards from the live player deck.",
                    &[validation_detail(
                        "run.players[].overlays[].deckCardSelection.cards",
                        "set",
                        "Remove cards; the room-composed deck overlay (rest-site smith / event enchant) shows the player's own deck cards.",
                    )],
                ));
            }
        }

        return Ok(room_screen);
    }

    if let Some(overlay) = overlay {
        if let Some(deck) = overlay.deck_card_selection.as_ref() {
            if deck.cards.is_empty() {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture standalone deck-card-selection overlays must author cards.",
                    &[validation_detail(
                        "run.players[].overlays[].deckCardSelection.cards",
                        "[]",
                        "Provide visible card modelId entries, or compose the overlay on a rest-site room to read the live deck.",
                    )],
                ));
            }
            let screen = match deck.kind.as_str() {
                "upgrade" => bridge::DECK_UPGRADE_SELECTION_SCREEN_ID,
                "transform" => bridge::DECK_TRANSFORM_SELECTION_SCREEN_ID,
                "enchant" => bridge::DECK_ENCHANT_SELECTION_SCREEN_ID,
                _ => bridge::DECK_CARD_SELECTION_SCREEN_ID,
            };
            return Ok(screen.to_string());
        }
        if overlay.choose_a_card.is_some() {
            return Ok(bridge::CARD_REWARD_SELECTION_SCREEN_ID.to_string());
        }
        if overlay.simple_card_selection.is_some() {
            return Ok(bridge::SIMPLE_CARD_SELECTION_SCREEN_ID.to_string());
        }
        if overlay.bundle_selection.is_some() {
            return Ok(bridge::BUNDLE_SELECTION_SCREEN_ID.to_string());
        }
        if overlay.rewards.is_some() {
            return Ok(bridge::REWARDS_SCREEN_ID.to_string());
        }
        if let Some(card_overlay) = overlay.card_overlay.as_ref() {
            return Ok(if card_overlay.policy == "passive" {
                "passive-card-overlay".to_string()
            } else {
                "card-overlay".to_string()
            });
        }
    }

    Err(AppError::invalid_fixture(
        resolved_path,
        "fixture recipe could not be derived from the document structure.",
        &[validation_detail(
            "screen",
            "underived",
            "Author one structural signal: characterSelect (lobby), rootScene: main-menu, run.currentRoom.{combat|event|treasure|shop|restSite|mapRoom}, or one run.players[].overlays entry.",
        )],
    ))
}

/// Resolves `run.view.playerId` (default: the single player, or the host-owned
/// local player in host-local multiplayer fixtures) and enforces the per-recipe
/// player rules shared by the fixture schema.
fn resolve_view_player_id(
    resolved_path: &Path,
    screen: &str,
    players: &mut Vec<FixtureRunPlayer>,
    explicit_view_player_id: Option<String>,
) -> Result<String, AppError> {
    let rule = screen_player_rule(screen);
    let label = screen_label(screen);

    if players.len() == 1 {
        if players[0].has_multiplayer_fields() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                &format!("fixture includes unsupported {label} player fields."),
                &[validation_detail(
                    "run.players[]",
                    "isLocal/isHostLocalSeat/slotId",
                    &format!(
                        "Single-player {label} fixtures only support id, characterId, creature, gold, potions, relics, deck, combat, and overlays."
                    ),
                )],
            ));
        }
        let player_id = players[0].id.clone();
        if let Some(explicit) = explicit_view_player_id
            && explicit != player_id
        {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture run.view.playerId must refer to a declared player.",
                &[validation_detail(
                    "run.view.playerId",
                    explicit,
                    "Choose one run.players[].id value for the fixture perspective.",
                )],
            ));
        }
        return Ok(player_id);
    }

    if rule == PlayerRule::SinglePlayer {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture player count is not supported.",
            &[validation_detail(
                "run.players",
                format!("count={}", players.len()),
                &format!("{label} fixtures currently support exactly one authored player."),
            )],
        ));
    }

    // Default the host-local metadata: the first authored player is the
    // host-owned local seat, the rest are host-local seats, slots follow author
    // order, and every seat is locally controlled. Explicit values win and are
    // still validated below. The defaults serialize into the wire document so
    // the bridge receives fully-populated players.
    for (index, player) in players.iter_mut().enumerate() {
        player.is_local.get_or_insert(true);
        player.slot_id.get_or_insert(index as u32);
        player.is_host_local_seat.get_or_insert(index != 0);
    }

    for (index, player) in players.iter().enumerate() {
        let field = format!("run.players[{index}]");
        if !player.id.starts_with("p:") || player.id.len() <= 2 {
            return Err(AppError::invalid_fixture(
                resolved_path,
                &format!("fixture {label} multiplayer player ids are not stable net ids."),
                &[validation_detail(
                    &format!("{field}.id"),
                    player.id.clone(),
                    &format!("Use stable p:<net-id> player ids for multi-player {label} fixtures."),
                )],
            ));
        }
        if player.is_local != Some(true) {
            return Err(AppError::invalid_fixture(
                resolved_path,
                &format!("fixture {label} multiplayer remote players are not supported."),
                &[validation_detail(
                    &format!("{field}.isLocal"),
                    "false",
                    &format!(
                        "True remote {label} players are not actionable by this local fixture setup path."
                    ),
                )],
            ));
        }
    }

    let host_local_players = players
        .iter()
        .filter(|player| player.is_local == Some(true) && player.is_host_local_seat == Some(false))
        .collect::<Vec<_>>();
    if host_local_players.len() != 1 {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {label} multiplayer host-owned local player is ambiguous."),
            &[validation_detail(
                "run.players[].isLocal",
                format!("hostLocalCount={}", host_local_players.len()),
                &format!(
                    "Mark exactly one {label} player with isLocal: true and isHostLocalSeat: false."
                ),
            )],
        ));
    }
    let host_local_player = host_local_players[0];

    for (index, player) in players.iter().enumerate() {
        if player.id != host_local_player.id && player.is_host_local_seat != Some(true) {
            return Err(AppError::invalid_fixture(
                resolved_path,
                &format!("fixture {label} multiplayer host-local seat metadata is incomplete."),
                &[validation_detail(
                    &format!("run.players[{index}].isHostLocalSeat"),
                    player
                        .is_host_local_seat
                        .map(|value| value.to_string())
                        .unwrap_or_else(|| "null".to_string()),
                    &format!(
                        "Every additional local {label} player must be marked isHostLocalSeat: true."
                    ),
                )],
            ));
        }
    }

    if let Some(explicit) = explicit_view_player_id
        && explicit != host_local_player.id
    {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture run.view.playerId must use the host-owned local {label} player."),
            &[validation_detail(
                "run.view.playerId",
                explicit,
                &format!(
                    "Use the {label} player marked isLocal: true and isHostLocalSeat: false as the local perspective (or omit run.view.playerId to default to it)."
                ),
            )],
        ));
    }

    Ok(host_local_player.id.clone())
}

/// Fills the per-player room state owners (event/restSite playerStates) with
/// the view player when omitted, and validates explicit owners against the
/// declared players.
fn resolve_room_player_state_owners(
    resolved_path: &Path,
    mut room: FixtureCurrentRoom,
    player_ids: &std::collections::BTreeSet<String>,
    view_player_id: &str,
) -> Result<FixtureCurrentRoom, AppError> {
    let resolve = |player_id: &mut Option<String>, field: String| match player_id {
        Some(id) if !player_ids.contains(id) => Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {field} must refer to a declared player."),
            &[validation_detail(
                &field,
                id.clone(),
                "Choose one run.players[].id value, or omit playerId to default to the view player.",
            )],
        )),
        Some(_) => Ok(()),
        None => {
            *player_id = Some(view_player_id.to_string());
            Ok(())
        }
    };

    if let Some(event) = room.event.as_mut() {
        for (index, state) in event.player_states.iter_mut().enumerate() {
            resolve(
                &mut state.player_id,
                format!("run.currentRoom.event.playerStates[{index}].playerId"),
            )?;
        }
    }
    if let Some(rest_site) = room.rest_site.as_mut() {
        for (index, state) in rest_site.player_states.iter_mut().enumerate() {
            resolve(
                &mut state.player_id,
                format!("run.currentRoom.restSite.playerStates[{index}].playerId"),
            )?;
        }
    }

    Ok(room)
}

fn validate_overlay_owner(
    resolved_path: &Path,
    players: &[FixtureRunPlayer],
    view_player_id: &str,
) -> Result<(), AppError> {
    for (index, player) in players.iter().enumerate() {
        if !player.overlays.is_empty() && player.id != view_player_id {
            return Err(AppError::invalid_fixture(
                resolved_path,
                "fixture overlays must be authored on the view player.",
                &[validation_detail(
                    &format!("run.players[{index}].overlays"),
                    "present",
                    "Author overlays on the run.view.playerId player; other players' overlays are not loadable by the local fixture setup path.",
                )],
            ));
        }
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// shared normalization helpers
// ---------------------------------------------------------------------------

// Trim an optional authored string, dropping it when empty/whitespace.
fn normalize_optional_trimmed(value: Option<String>) -> Option<String> {
    value
        .map(|text| text.trim().to_string())
        .filter(|text| !text.is_empty())
}

fn normalize_seed(seed: Option<String>, fixture_name: &str) -> String {
    match seed {
        Some(value) if value.trim().is_empty() => fixture_name.to_string(),
        Some(value) => value.trim().to_string(),
        None => fixture_name.to_string(),
    }
}

fn normalize_model_refs(
    resolved_path: &Path,
    field: &str,
    values: Vec<FixtureModelRefInput>,
    empty_note: &str,
) -> Result<Vec<FixtureModelRef>, AppError> {
    values
        .into_iter()
        .enumerate()
        .map(|(index, value)| {
            let model_id = normalize_required_model_id(
                resolved_path,
                &format!("{field}[{index}].modelId"),
                value.model_id,
                empty_note,
            )?;
            Ok(FixtureModelRef { model_id })
        })
        .collect()
}

fn normalize_required_model_refs(
    resolved_path: &Path,
    field: &str,
    values: Vec<FixtureModelRefInput>,
    empty_note: &str,
) -> Result<Vec<FixtureModelRef>, AppError> {
    if values.is_empty() {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {field} must not be empty."),
            &[validation_detail(field, "[]", empty_note)],
        ));
    }
    normalize_model_refs(resolved_path, field, values, empty_note)
}

fn normalize_required_fixture_id(
    resolved_path: &Path,
    field: &str,
    value: String,
    note: &str,
) -> Result<String, AppError> {
    let normalized = value.trim().to_string();
    if normalized.is_empty() {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {field} must not be empty."),
            &[validation_detail(field, value, note)],
        ));
    }
    Ok(normalized)
}

fn normalize_required_model_id(
    resolved_path: &Path,
    field: &str,
    value: String,
    note: &str,
) -> Result<String, AppError> {
    if value.trim().is_empty() {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {field} must not be empty."),
            &[validation_detail(field, value, note)],
        ));
    }
    Ok(value)
}

fn is_crystal_sphere_event_id(value: &str) -> bool {
    let normalized = value.trim().replace('_', "-").to_ascii_lowercase();
    normalized == "crystal-sphere"
}

fn normalize_crystal_sphere_state(
    resolved_path: &Path,
    field: &str,
    canonical_event_model_id: &str,
    options: &[FixtureEventOption],
    crystal_sphere: FixtureCrystalSphereInput,
) -> Result<FixtureCrystalSphere, AppError> {
    if !is_crystal_sphere_event_id(canonical_event_model_id) {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture Crystal Sphere state requires the CRYSTAL_SPHERE event.",
            &[validation_detail(
                &format!("{field}.crystalSphere"),
                canonical_event_model_id,
                "Only run.currentRoom.event.canonicalEventModelId: CRYSTAL_SPHERE can author crystalSphere state.",
            )],
        ));
    }

    let chosen_count = options
        .iter()
        .filter(|option| option.was_chosen == Some(true))
        .count();
    if chosen_count != 1 {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture Crystal Sphere state requires one chosen event option.",
            &[validation_detail(
                &format!("{field}.options"),
                format!("chosenCount={chosen_count}"),
                "Author exactly one Crystal Sphere option with wasChosen: true before authoring crystalSphere grid state.",
            )],
        ));
    }

    let selected_tool = crystal_sphere
        .selected_tool
        .trim()
        .to_ascii_lowercase();
    if !matches!(selected_tool.as_str(), "big" | "small" | "none") {
        return Err(AppError::invalid_fixture(
            resolved_path,
            "fixture Crystal Sphere selectedTool is invalid.",
            &[validation_detail(
                &format!("{field}.crystalSphere.selectedTool"),
                crystal_sphere.selected_tool,
                "Use selectedTool: big, small, or none.",
            )],
        ));
    }

    let mut seen = std::collections::BTreeSet::new();
    let cells = crystal_sphere
        .cells
        .into_iter()
        .enumerate()
        .map(|(index, cell)| {
            let cell_field = format!("{field}.crystalSphere.cells[{index}]");
            if cell.x > 10 || cell.y > 10 {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture Crystal Sphere cell coordinate is outside the 11x11 grid.",
                    &[validation_detail(
                        &cell_field,
                        format!("{},{}", cell.x, cell.y),
                        "Use x/y coordinates between 0 and 10.",
                    )],
                ));
            }

            let id = normalize_required_fixture_id(
                resolved_path,
                &format!("{cell_field}.id"),
                cell.id,
                "Use the stable id shape crystal-sphere:cell:<x>:<y>.",
            )?;
            let expected_id = format!("crystal-sphere:cell:{}:{}", cell.x, cell.y);
            if id != expected_id {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture Crystal Sphere cell id does not match x/y.",
                    &[validation_detail(
                        &format!("{cell_field}.id"),
                        id,
                        &format!("Use {expected_id} for x={}, y={}.", cell.x, cell.y),
                    )],
                ));
            }

            if !seen.insert((cell.x, cell.y)) {
                return Err(AppError::invalid_fixture(
                    resolved_path,
                    "fixture Crystal Sphere cells must be unique.",
                    &[validation_detail(
                        &cell_field,
                        expected_id,
                        "Remove duplicate Crystal Sphere cell entries.",
                    )],
                ));
            }

            Ok(FixtureCrystalSphereCell {
                id: expected_id,
                x: cell.x,
                y: cell.y,
                is_hidden: cell.is_hidden,
            })
        })
        .collect::<Result<Vec<_>, AppError>>()?;

    Ok(FixtureCrystalSphere {
        selected_tool,
        divinations_remaining: crystal_sphere.divinations_remaining,
        cells,
    })
}

fn normalize_model_id_list(
    resolved_path: &Path,
    field: &str,
    values: Vec<String>,
    empty_note: &str,
    require_non_empty: bool,
) -> Result<Vec<String>, AppError> {
    for (index, value) in values.iter().enumerate() {
        if value.trim().is_empty() {
            return Err(AppError::invalid_fixture(
                resolved_path,
                &format!("fixture {field}[{index}] must not be empty."),
                &[validation_detail(
                    &format!("{field}[{index}]"),
                    value,
                    empty_note,
                )],
            ));
        }
    }
    let normalized = values;
    if require_non_empty && normalized.is_empty() {
        return Err(AppError::invalid_fixture(
            resolved_path,
            &format!("fixture {field} must not be empty."),
            &[validation_detail(field, "[]", empty_note)],
        ));
    }
    Ok(normalized)
}
