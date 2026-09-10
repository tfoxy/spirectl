//! Native (non-CEL) resolution of `state actions`.
//!
//! This is the hand-written Rust port of the former `presentation/actions.json`
//! surface catalog. It reads `state` directly and emits the same
//! `spirectl.state-actions/v0` envelope the CEL resolver produced. The
//! `presentation/` materialize/CEL engine is no longer in the path for
//! `state actions`.
//!
//! Localization is intentionally a no-op here: the command resolves against an
//! empty loc table, so every `locRef`/`locKey` in the original catalog collapsed
//! to its fallback. [`loc_ref_label`] preserves exactly that behaviour (a plain
//! string passes through; a `{table,key}` ref or null yields `None`, which then
//! falls back to the action `kind`).

use super::*;

use crate::models_ext::{PresentationModels, load_presentation_models};

const STATE_ACTIONS_SCHEMA_VERSION: &str = "spirectl.state-actions/v0";
const ACTIONS_CATALOG_VERSION: &str = "spirectl.presentation.actions/v0";

/// Fetch live `state` + the model catalogs the labels need, then resolve the
/// available semantic actions natively. Mirrors the bridge/timing orchestration
/// of the former `execute_state_actions_json`.
pub(crate) fn execute_state_actions_json(
    args: &StateArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let build_started = std::time::Instant::now();
    let mut timings = StateActionsTimings::default();
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let stage = std::time::Instant::now();
    let state_response = client
        .state(bridge::proto::StateRequest {
            perspective: build_perspective_selector_values(
                args.perspective,
                args.player_id.as_deref(),
            ),
        })
        .map_err(AppError::bridge)?;
    let state = state_json(&state_response);
    timings.record("state", stage);
    let stage = std::time::Instant::now();
    let models = load_presentation_models(&client, &state)?;
    timings.record("models", stage);
    let stage = std::time::Instant::now();
    let mut result = resolve_state_actions_json(
        &state,
        &models,
        PerspectiveRequest {
            scope: args.perspective,
            player_id: args.player_id.as_deref(),
        },
    );
    timings.record("resolveActions", stage);
    let total_ms = build_started.elapsed().as_millis() as u64;
    if let Some(Value::Array(diagnostics)) = result.get_mut("diagnostics") {
        diagnostics.push(timings.into_diagnostic("state_actions_timings", total_ms));
    }
    Ok(result)
}

/// What the caller asked the bridge to answer from. `player_id: None` means "whatever seat the
/// bridge considers local", which is exactly the silent default this command used to hide.
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct PerspectiveRequest<'a> {
    pub scope: Option<PerspectiveScopeArg>,
    pub player_id: Option<&'a str>,
}

/// Pure resolver: `(state, models, perspective)` -> `spirectl.state-actions/v0` envelope.
/// Deterministic (no timing diagnostics); the orchestration above appends those.
pub(crate) fn resolve_state_actions_json(
    state: &Value,
    models: &PresentationModels,
    perspective: PerspectiveRequest<'_>,
) -> Value {
    let mut diagnostics = Vec::new();
    let actions = resolve_state_actions(state, models, &mut diagnostics);
    let (perspective_json, warnings) = perspective_json(state, perspective);
    json!({
        "schemaVersion": STATE_ACTIONS_SCHEMA_VERSION,
        "source": {
            "stateSchemaVersion": state.get("schemaVersion").and_then(Value::as_str).unwrap_or(""),
            "actionsCatalogVersion": ACTIONS_CATALOG_VERSION,
        },
        "screen": {
            "rootScene": state.get("rootScene").cloned().unwrap_or(Value::Null),
            "scene": state.pointer("/run/currentRoom/scene").cloned().unwrap_or(Value::Null),
        },
        "perspective": perspective_json,
        "actions": actions,
        "warnings": warnings,
        "diagnostics": diagnostics,
    })
}

/// Report which seat the returned actions belong to, and warn when that seat was chosen by default
/// on a multi-seat game. An empty action list on a co-op run reads identically to "this seat has
/// nothing to do", so the defaulted seat has to be visible rather than inferred.
fn perspective_json(state: &Value, request: PerspectiveRequest<'_>) -> (Value, Vec<Value>) {
    // The state envelope was fetched with this very selector, so the seat it reports back is the
    // seat the bridge resolved — including the default the bridge picked for us.
    let resolved_player_id = state
        .pointer("/run/view/playerId")
        .and_then(Value::as_str)
        .or_else(|| {
            state
                .pointer("/characterSelect/view/playerId")
                .and_then(Value::as_str)
        })
        .or(request.player_id);
    let seat_count = seat_count(state);
    let uses_default = request.player_id.is_none();
    let scope = match request.scope.unwrap_or(PerspectiveScopeArg::Local) {
        PerspectiveScopeArg::Local => "local",
        PerspectiveScopeArg::Omniscient => "omniscient",
    };

    let mut warnings = Vec::new();
    if uses_default && seat_count > 1 {
        warnings.push(json!({
            "code": "perspective_defaulted_multi_seat",
            "severity": "warning",
            "playerId": resolved_player_id,
            "seatCount": seat_count,
            "message": format!(
                "No --player-id was given, so actions were resolved for the default seat {} of {seat_count}. Pass --player-id to choose a seat explicitly.",
                resolved_player_id.unwrap_or("<unknown>")
            ),
        }));
    }

    (
        json!({
            "playerId": resolved_player_id,
            "requestedPlayerId": request.player_id,
            "scope": scope,
            "usesDefault": uses_default,
            "seatCount": seat_count,
        }),
        warnings,
    )
}

fn seat_count(state: &Value) -> usize {
    if let Some(players) = state.pointer("/run/players").and_then(Value::as_array) {
        return players.len();
    }
    state
        .pointer("/characterSelect/lobby/players")
        .and_then(Value::as_array)
        .map(Vec::len)
        .unwrap_or(0)
}

fn resolve_state_actions(
    state: &Value,
    models: &PresentationModels,
    diagnostics: &mut Vec<Value>,
) -> Vec<Value> {
    let mut actions = Vec::new();
    let mut matched = false;
    // Surfaces are evaluated in catalog order; several can match at once (e.g. a
    // combat screen matches run-top-bar AND combat-room), and their actions
    // concatenate in this order.
    matched |= surface_character_select(state, &mut actions);
    matched |= surface_run_top_bar(state, &mut actions);
    matched |= surface_deck_view(state, &mut actions);
    matched |= surface_event_room(state, &mut actions);
    matched |= surface_choose_a_card(state, models, &mut actions);
    matched |= surface_simple_grid_card_selection(state, models, &mut actions);
    matched |= surface_bundle_card_selection(state, &mut actions);
    matched |= surface_rewards(state, &mut actions);
    matched |= surface_treasure(state, &mut actions);
    matched |= surface_rest_site(state, &mut actions);
    matched |= surface_shop(state, models, &mut actions);
    matched |= surface_map_room(state, &mut actions);
    matched |= surface_hand_selection(state, models, &mut actions);
    matched |= surface_combat(state, models, &mut actions);
    if !matched {
        diagnostics.push(json!({
            "code": "presentation_action_surface_unmatched",
            "severity": "info",
            "rootScene": state.get("rootScene").cloned().unwrap_or(Value::Null),
            "scene": state.pointer("/run/currentRoom/scene").cloned().unwrap_or(Value::Null),
            "message": "No presentation actions surface matched the current state screen.",
        }));
    }
    actions
}

// ---------------------------------------------------------------------------
// Surfaces
// ---------------------------------------------------------------------------

fn surface_character_select(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("screens/character_select_screen") {
        return false;
    }
    let Some(cs) = non_null(state.get("characterSelect")) else {
        return false;
    };
    let view_player_id = cs.pointer("/view/playerId").and_then(Value::as_str);
    let local_player = character_select_local_player(cs);
    let local_ready = local_player
        .and_then(|p| p.get("isReady"))
        .and_then(Value::as_bool);
    let selected_button = character_select_selected_button(cs);

    // select-character (repeat over characterButtons)
    if let Some(buttons) = cs.pointer("/characterButtons").and_then(Value::as_array) {
        for (index, item) in buttons.iter().enumerate() {
            let when = local_player.is_some()
                && local_ready != Some(true)
                && item.get("isLocked").and_then(Value::as_bool) != Some(true);
            if !when {
                continue;
            }
            let Some(character_id) = item.get("characterId").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("characterId".into(), json!(character_id));
            emit_action(
                actions,
                "select-character",
                format!("action:lobby:select-character:{character_id}"),
                Some(character_id.to_string()),
                args,
                view_player_id.map(str::to_string),
                Some(format!("characterSelect.characterButtons[{index}]")),
            );
        }
    }

    // ready
    let show_ready = local_player.is_some() && local_ready != Some(true);
    if show_ready
        && selected_button.is_some()
        && selected_button
            .and_then(|b| b.get("isLocked"))
            .and_then(Value::as_bool)
            != Some(true)
    {
        emit_action(
            actions,
            "ready",
            "action:lobby:ready".to_string(),
            Some("Ready".to_string()),
            Map::new(),
            view_player_id.map(str::to_string),
            Some("characterSelect.lobby".to_string()),
        );
    }

    // unready
    if local_player.is_some() && local_ready == Some(true) {
        emit_action(
            actions,
            "unready",
            "action:lobby:unready".to_string(),
            Some("Unready".to_string()),
            Map::new(),
            view_player_id.map(str::to_string),
            Some("characterSelect.lobby".to_string()),
        );
    }

    // leave-lobby-player (shares showReadyButton gate: localPlayer present & not ready)
    if show_ready && let Some(player_id) = view_player_id {
        let mut args = Map::new();
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            "leave-lobby-player",
            format!("action:lobby:leave-lobby-player:{player_id}"),
            Some("Back".to_string()),
            args,
            Some(player_id.to_string()),
            Some("characterSelect.view".to_string()),
        );
    }

    true
}

fn surface_run_top_bar(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(player_id) = run_view_player_id(state) else {
        return false;
    };
    for (kind, id, label) in [
        ("toggle-map", "action:top-bar:toggle-map", "Map"),
        ("toggle-deck", "action:top-bar:toggle-deck", "Deck"),
        (
            "toggle-settings",
            "action:top-bar:toggle-settings",
            "Settings",
        ),
    ] {
        let mut args = Map::new();
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            kind,
            id.to_string(),
            Some(label.to_string()),
            args,
            Some(player_id.to_string()),
            Some("run.view".to_string()),
        );
    }

    // The relic bar: one inspect-relic action per local-player relic (opens the
    // relic-details overlay), plus a close action while the overlay is open.
    let relics = local_player(state)
        .and_then(|player| player.get("relics"))
        .and_then(Value::as_array);
    for relic in relics.into_iter().flatten() {
        let Some(relic_id) = relic.get("id").and_then(Value::as_str) else {
            continue;
        };
        let label = relic
            .get("modelId")
            .and_then(Value::as_str)
            .unwrap_or(relic_id);
        let mut args = Map::new();
        args.insert("relicId".into(), json!(relic_id));
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            "inspect-relic",
            format!("action:relic-bar:inspect-relic:{relic_id}"),
            Some(label.to_string()),
            args,
            Some(player_id.to_string()),
            Some("run.players[].relics".to_string()),
        );
    }

    if non_null(state.pointer("/run/view/inspectRelic")).is_some() {
        let mut args = Map::new();
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            "close-inspect-relic",
            "action:inspect-relic:close".to_string(),
            Some("Close Relic Details".to_string()),
            args,
            Some(player_id.to_string()),
            Some("run.view.inspectRelic".to_string()),
        );
    }
    true
}

fn surface_deck_view(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(deck_view) = non_null(state.pointer("/run/view/capstone/deckView")) else {
        return false;
    };
    let player_id = run_view_player_id(state);

    if let Some(sort) = deck_view.get("sort").and_then(Value::as_array) {
        for (index, item) in sort.iter().enumerate() {
            let Some(by) = item.get("by").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("by".into(), json!(by));
            if let Some(player_id) = player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "sort-deck-view",
                format!("action:deck-view:sort:{by}"),
                Some(format!("Sort by {by}")),
                args,
                player_id.map(str::to_string),
                Some(format!("run.view.capstone.deckView.sort[{index}]")),
            );
        }
    }

    let mut args = Map::new();
    if let Some(player_id) = player_id {
        args.insert("playerId".into(), json!(player_id));
    }
    emit_action(
        actions,
        "toggle-deck-view-upgrades",
        "action:deck-view:toggle-upgrades".to_string(),
        Some("Show upgrades".to_string()),
        args,
        player_id.map(str::to_string),
        Some("run.view.capstone.deckView.showUpgrades".to_string()),
    );
    true
}

fn surface_event_room(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/event_room")
    {
        return false;
    }
    let Some(event) = non_null(state.pointer("/run/currentRoom/event")) else {
        return false;
    };
    let view_player_id = state.pointer("/run/view/playerId").and_then(Value::as_str);
    let local_player_state = event_local_player_state(state, event);
    let local_unchosen = local_event_options_unchosen(local_player_state);
    let options =
        event_options_with_player_ids(event.get("playerStates").and_then(Value::as_array));

    // select-event-option
    for item in &options {
        let when = local_unchosen
            && item.get("playerId").and_then(Value::as_str) == view_player_id
            && item.get("isLocked").and_then(Value::as_bool) != Some(true)
            && item.get("wasChosen").and_then(Value::as_bool) != Some(true)
            && item.get("isProceed").and_then(Value::as_bool) != Some(true);
        if !when {
            continue;
        }
        let Some(id) = item.get("id").and_then(Value::as_str) else {
            continue;
        };
        let mut args = Map::new();
        args.insert("eventOptionId".into(), json!(id));
        if let Some(player_id) = item.get("playerId").and_then(Value::as_str) {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "select-event-option",
            format!("action:event-room:select-event-option:{id}"),
            event_option_title(item),
            args,
            item.get("playerId")
                .and_then(Value::as_str)
                .map(str::to_string),
            item.get("sourcePath")
                .and_then(Value::as_str)
                .map(str::to_string),
        );
    }

    // proceed-event (finished player state)
    for item in finished_event_proceed_actions(local_player_state) {
        let Some(player_id) = item.get("playerId").and_then(Value::as_str) else {
            continue;
        };
        let mut args = Map::new();
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            "proceed-event",
            format!("action:event-room:proceed:{player_id}"),
            item.get("label")
                .and_then(Value::as_str)
                .map(str::to_string),
            args,
            Some(player_id.to_string()),
            item.get("sourcePath")
                .and_then(Value::as_str)
                .map(str::to_string),
        );
    }

    // proceed-event (a "proceed" option)
    for item in &options {
        let when = local_unchosen
            && item.get("playerId").and_then(Value::as_str) == view_player_id
            && item.get("isLocked").and_then(Value::as_bool) != Some(true)
            && item.get("wasChosen").and_then(Value::as_bool) != Some(true)
            && item.get("isProceed").and_then(Value::as_bool) == Some(true);
        if !when {
            continue;
        }
        let Some(player_id) = item.get("playerId").and_then(Value::as_str) else {
            continue;
        };
        let mut args = Map::new();
        args.insert("playerId".into(), json!(player_id));
        emit_action(
            actions,
            "proceed-event",
            format!("action:event-room:proceed:{player_id}"),
            event_option_title(item),
            args,
            Some(player_id.to_string()),
            item.get("sourcePath")
                .and_then(Value::as_str)
                .map(str::to_string),
        );
    }

    true
}

fn surface_choose_a_card(
    state: &Value,
    models: &PresentationModels,
    actions: &mut Vec<Value>,
) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(overlay) = top_overlay(state, "chooseACard") else {
        return false;
    };
    let Some(choose) = non_null(overlay.get("chooseACard")) else {
        return false;
    };
    let local_player_id = local_player(state)
        .and_then(|p| p.get("id"))
        .and_then(Value::as_str);
    let overlay_id = overlay
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or_default();

    if let Some(cards) = choose.get("cards").and_then(Value::as_array) {
        for (index, item) in cards.iter().enumerate() {
            let Some(id) = item.get("id").and_then(Value::as_str) else {
                continue;
            };
            let model_id = item.get("modelId").and_then(Value::as_str);
            let label = choose_a_card_label(models, model_id);
            let mut args = Map::new();
            if let (Some(model_id), Some(_)) = (model_id, local_player_id) {
                args.insert(
                    "cardId".into(),
                    json!(format!("card-selection:card:{model_id}:{index}")),
                );
            }
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "select-card",
                format!("action:choose-a-card:select-card:{id}"),
                label,
                args,
                local_player_id.map(str::to_string),
                local_player_id.map(|player_id| {
                    format!(
                        "run.players[id={player_id}].overlays[id={overlay_id}].chooseACard.cards[{index}]"
                    )
                }),
            );
        }
    }

    if choose.get("canSkip").and_then(Value::as_bool) == Some(true) {
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "skip-card-selection",
            format!("action:choose-a-card:skip:{overlay_id}"),
            Some("Skip".to_string()),
            args,
            local_player_id.map(str::to_string),
            local_player_id.map(|player_id| {
                format!("run.players[id={player_id}].overlays[id={overlay_id}].chooseACard")
            }),
        );
    }

    true
}

fn surface_simple_grid_card_selection(
    state: &Value,
    models: &PresentationModels,
    actions: &mut Vec<Value>,
) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(overlay) = top_overlay(state, "simpleGridCardSelection") else {
        return false;
    };
    let Some(grid) = non_null(overlay.get("simpleGridCardSelection")) else {
        return false;
    };
    let local_player_id = local_player(state)
        .and_then(|p| p.get("id"))
        .and_then(Value::as_str);
    let overlay_id = overlay
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or_default();

    if let Some(cards) = grid.get("cards").and_then(Value::as_array) {
        for (index, item) in cards.iter().enumerate() {
            // Card ids are already the select-card choice ids
            // (`card-selection:card:<MODEL>:<index>`).
            let Some(id) = item.get("id").and_then(Value::as_str) else {
                continue;
            };
            let model_id = item.get("modelId").and_then(Value::as_str);
            let label = choose_a_card_label(models, model_id);
            let mut args = Map::new();
            args.insert("cardId".into(), json!(id));
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "select-card",
                format!("action:simple-card-selection:select-card:{id}"),
                label,
                args,
                local_player_id.map(str::to_string),
                local_player_id.map(|player_id| {
                    format!(
                        "run.players[id={player_id}].overlays[id={overlay_id}].simpleGridCardSelection.cards[{index}]"
                    )
                }),
            );
        }
    }

    // Multi-pick grids confirm/cancel explicitly (single-pick screens auto-resolve
    // in the game and never reach a confirmable state here).
    if grid.get("canConfirm").and_then(Value::as_bool) == Some(true) {
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "confirm-selection",
            format!("action:simple-card-selection:confirm:{overlay_id}"),
            Some("Confirm".to_string()),
            args,
            local_player_id.map(str::to_string),
            local_player_id.map(|player_id| {
                format!(
                    "run.players[id={player_id}].overlays[id={overlay_id}].simpleGridCardSelection"
                )
            }),
        );
    }
    if grid.get("canCancel").and_then(Value::as_bool) == Some(true) {
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "cancel-selection",
            format!("action:simple-card-selection:cancel:{overlay_id}"),
            Some("Cancel".to_string()),
            args,
            local_player_id.map(str::to_string),
            local_player_id.map(|player_id| {
                format!(
                    "run.players[id={player_id}].overlays[id={overlay_id}].simpleGridCardSelection"
                )
            }),
        );
    }

    true
}

fn surface_bundle_card_selection(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(overlay) = top_overlay(state, "bundleSelection") else {
        return false;
    };
    let Some(bundle_overlay) = non_null(overlay.get("bundleSelection")) else {
        return false;
    };
    let local_player_id = local_player(state)
        .and_then(|p| p.get("id"))
        .and_then(Value::as_str);
    let overlay_id = overlay
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or_default();

    if let Some(bundles) = bundle_overlay.get("bundles").and_then(Value::as_array) {
        for (index, item) in bundles.iter().enumerate() {
            // Bundle ids are already the select-bundle choice ids
            // (`card-selection:bundle:<stable>:<index>`).
            let Some(id) = item.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("bundleId".into(), json!(id));
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "select-bundle",
                format!("action:bundle-selection:select-bundle:{id}"),
                Some(format!("Bundle {}", index + 1)),
                args,
                local_player_id.map(str::to_string),
                local_player_id.map(|player_id| {
                    format!(
                        "run.players[id={player_id}].overlays[id={overlay_id}].bundleSelection.bundles[{index}]"
                    )
                }),
            );
        }
    }

    // After a bundle is clicked the preview confirm becomes available.
    if bundle_overlay.get("canConfirm").and_then(Value::as_bool) == Some(true) {
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "confirm-selection",
            format!("action:bundle-selection:confirm:{overlay_id}"),
            Some("Confirm".to_string()),
            args,
            local_player_id.map(str::to_string),
            local_player_id.map(|player_id| {
                format!("run.players[id={player_id}].overlays[id={overlay_id}].bundleSelection")
            }),
        );
    }

    true
}

fn surface_rewards(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(overlay) = top_overlay(state, "rewards") else {
        return false;
    };
    let Some(rewards) = non_null(overlay.get("rewards")) else {
        return false;
    };
    let local_player_id = local_player(state)
        .and_then(|p| p.get("id"))
        .and_then(Value::as_str);
    let overlay_id = overlay
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or_default();

    if let Some(items) = rewards.pointer("/items").and_then(Value::as_array) {
        for (index, item) in items.iter().enumerate() {
            let Some(id) = item.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("rewardId".into(), json!(id));
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "claim-reward",
                format!("action:rewards:claim:{id}"),
                Some(reward_label(item)),
                args,
                local_player_id.map(str::to_string),
                local_player_id.map(|player_id| {
                    format!(
                        "run.players[id={player_id}].overlays[id={overlay_id}].rewards.items[{index}]"
                    )
                }),
            );
        }
    }

    let flow = non_null(rewards.get("flow"));
    if flow
        .and_then(|flow| flow.get("enabled"))
        .and_then(Value::as_bool)
        == Some(true)
    {
        let proceed = flow
            .and_then(|flow| flow.get("mode"))
            .and_then(Value::as_str)
            == Some("proceed");
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "skip-rewards",
            format!("action:rewards:flow:{overlay_id}"),
            Some(if proceed { "Proceed" } else { "Skip" }.to_string()),
            args,
            local_player_id.map(str::to_string),
            local_player_id.map(|player_id| {
                format!("run.players[id={player_id}].overlays[id={overlay_id}].rewards.flow")
            }),
        );
    }

    true
}

fn surface_treasure(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/treasure_room")
    {
        return false;
    }
    let Some(treasure) = non_null(state.pointer("/run/currentRoom/treasure")) else {
        return false;
    };
    let view_player_id = state.pointer("/run/view/playerId").and_then(Value::as_str);
    // The catalog gates both actions on `treasure.currentRelicsActive`, a field
    // state never emits (activeness is conveyed by `currentRelics` being an
    // array vs null). The prior CEL resolver therefore *errored* evaluating both
    // `when` clauses and fell back to enabled (`unwrap_or(true)`), so BOTH
    // open-chest and take-relic surface whenever the treasure room shows. This
    // preserves that exact behaviour; `currentRelicsActive` is honoured if a
    // future projection adds it. (Known quirk: open-chest is offered even when
    // relics are already active — see docs/known-gaps.md.)
    let current_relics_active = treasure.get("currentRelicsActive");
    let when_enabled = |comparison_is_true: bool| match current_relics_active {
        None => true, // absent -> CEL eval error -> rule defaults to enabled
        Some(value) => (value.as_bool() == Some(true)) == comparison_is_true,
    };

    // open-chest: `currentRelicsActive != true`
    if when_enabled(false) {
        emit_action(
            actions,
            "open-chest",
            "action:treasure-room:open-chest".to_string(),
            Some("Open Chest".to_string()),
            Map::new(),
            view_player_id.map(str::to_string),
            Some("run.currentRoom.treasure".to_string()),
        );
    }

    // take-relic: `currentRelicsActive == true`, repeated over currentRelics
    if when_enabled(true)
        && let Some(relics) = treasure.get("currentRelics").and_then(Value::as_array)
    {
        for (index, item) in relics.iter().enumerate() {
            let Some(id) = item.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("relicId".into(), json!(id));
            emit_action(
                actions,
                "take-relic",
                format!("action:treasure-room:take-relic:{id}"),
                item.get("modelId")
                    .and_then(Value::as_str)
                    .map(str::to_string),
                args,
                view_player_id.map(str::to_string),
                Some(format!("run.currentRoom.treasure.currentRelics[{index}]")),
            );
        }
    }

    true
}

// Rest-site (campfire): pick an option, or proceed. Mirrors surface_event_room
// (per-player options, surfaced for the machine-local view player) + surface_treasure
// (room-level proceed). Action kinds are dictated by the bridge executor, which routes
// HEAL→`rest`, SMITH→`smith`, every other option→`use-rest-site-option`, and the
// proceed/continue flow control→`proceed-rest-site` (see Sts2RestSiteScreenInspector +
// Sts2ActionHandler.ScreenIntents). `canProceed` is machine-local-only state under the
// rest-site room `view`, not a per-player field.
fn surface_rest_site(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/rest_site_room")
    {
        return false;
    }
    let Some(rest_site) = non_null(state.pointer("/run/currentRoom/restSite")) else {
        return false;
    };
    let view_player_id = state.pointer("/run/view/playerId").and_then(Value::as_str);

    // use-rest-site-option / rest / smith: the machine-local view player's executable
    // options (disabledReason absent). Each action carries playerId so it stays
    // per-player addressable; consistent with event/treasure/combat surfacing.
    let local_player_state = view_player_id.and_then(|player_id| {
        rest_site
            .get("playerStates")
            .and_then(Value::as_array)?
            .iter()
            .enumerate()
            .find(|(_, player_state)| {
                player_state.get("playerId").and_then(Value::as_str) == Some(player_id)
            })
    });
    if let Some((player_index, player_state)) = local_player_state
        && let Some(options) = player_state.get("options").and_then(Value::as_array)
    {
        for (option_index, option) in options.iter().enumerate() {
            if non_null(option.get("disabledReason")).is_some() {
                continue; // not executable (not-enabled / confirmation-required)
            }
            let Some(option_id) = option.get("optionId").and_then(Value::as_str) else {
                continue;
            };
            let (kind, include_option_arg) = match option_id.to_ascii_lowercase().as_str() {
                "heal" | "rest" => ("rest", false),
                "smith" | "upgrade" => ("smith", false),
                _ => ("use-rest-site-option", true),
            };
            let mut args = Map::new();
            if include_option_arg {
                args.insert("restOptionId".into(), json!(option_id));
            }
            if let Some(player_id) = view_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                kind,
                format!("action:rest-site:{kind}:{option_id}"),
                None,
                args,
                view_player_id.map(str::to_string),
                Some(format!(
                    "run.currentRoom.restSite.playerStates[{player_index}].options[{option_index}]"
                )),
            );
        }
    }

    // proceed-rest-site: machine-local view (the local proceed/continue control).
    if rest_site
        .pointer("/view/canProceed")
        .and_then(Value::as_bool)
        == Some(true)
    {
        let mut args = Map::new();
        if let Some(player_id) = view_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "proceed-rest-site",
            "action:rest-site:proceed-rest-site".to_string(),
            Some("Proceed".to_string()),
            args,
            view_player_id.map(str::to_string),
            Some("run.currentRoom.restSite.view".to_string()),
        );
    }

    true
}

// Shop (merchant): buy a card/relic/potion, or pay to remove a card. Mirrors
// surface_treasure's item-repeat shape. Card entries carry only `card.modelId` (no
// embedded title), so card labels resolve through the model catalog exactly like
// surface_choose_a_card (`choose_a_card_label`); relic/potion entries already carry
// a `modelId` directly, like treasure relics.
fn surface_shop(state: &Value, models: &PresentationModels, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/shop_room")
    {
        return false;
    }
    let Some(shop) = non_null(state.pointer("/run/currentRoom/shop")) else {
        return false;
    };
    let view_player_id = state.pointer("/run/view/playerId").and_then(Value::as_str);
    let Some(inventory) = non_null(shop.get("inventory")) else {
        return true; // room matched; nothing purchasable to surface yet
    };

    surface_shop_card_entries(
        inventory,
        "characterCardEntries",
        models,
        view_player_id,
        actions,
    );
    surface_shop_card_entries(
        inventory,
        "colorlessCardEntries",
        models,
        view_player_id,
        actions,
    );

    if let Some(relics) = inventory.get("relicEntries").and_then(Value::as_array) {
        for (index, entry) in relics.iter().enumerate() {
            let Some(id) = entry.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("shopItemId".into(), json!(id));
            emit_action(
                actions,
                "buy-relic",
                format!("action:shop:buy-relic:{id}"),
                entry
                    .get("modelId")
                    .and_then(Value::as_str)
                    .map(str::to_string),
                args,
                view_player_id.map(str::to_string),
                Some(format!(
                    "run.currentRoom.shop.inventory.relicEntries[{index}]"
                )),
            );
        }
    }

    if let Some(potions) = inventory.get("potionEntries").and_then(Value::as_array) {
        for (index, entry) in potions.iter().enumerate() {
            let Some(id) = entry.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("shopItemId".into(), json!(id));
            emit_action(
                actions,
                "buy-potion",
                format!("action:shop:buy-potion:{id}"),
                entry
                    .get("modelId")
                    .and_then(Value::as_str)
                    .map(str::to_string),
                args,
                view_player_id.map(str::to_string),
                Some(format!(
                    "run.currentRoom.shop.inventory.potionEntries[{index}]"
                )),
            );
        }
    }

    if let Some(removal) = non_null(inventory.get("cardRemovalEntry"))
        && removal.get("used").and_then(Value::as_bool) != Some(true)
        && let Some(id) = removal.get("id").and_then(Value::as_str)
    {
        let mut args = Map::new();
        args.insert("shopItemId".into(), json!(id));
        emit_action(
            actions,
            "remove-card",
            format!("action:shop:remove-card:{id}"),
            Some("Remove Card".to_string()),
            args,
            view_player_id.map(str::to_string),
            Some("run.currentRoom.shop.inventory.cardRemovalEntry".to_string()),
        );
    }

    true
}

fn surface_shop_card_entries(
    inventory: &Value,
    field: &str,
    models: &PresentationModels,
    view_player_id: Option<&str>,
    actions: &mut Vec<Value>,
) {
    let Some(entries) = inventory.get(field).and_then(Value::as_array) else {
        return;
    };
    for (index, entry) in entries.iter().enumerate() {
        let Some(id) = entry.get("id").and_then(Value::as_str) else {
            continue;
        };
        let mut args = Map::new();
        args.insert("shopItemId".into(), json!(id));
        emit_action(
            actions,
            "buy-card",
            format!("action:shop:buy-card:{id}"),
            choose_a_card_label(
                models,
                entry.pointer("/card/modelId").and_then(Value::as_str),
            ),
            args,
            view_player_id.map(str::to_string),
            Some(format!("run.currentRoom.shop.inventory.{field}[{index}]")),
        );
    }
}

// Map screen: select a travelable next node. Unlike the other room surfaces,
// `run.currentRoom.mapRoom` carries no per-node data (`StateRunMapRoom` is
// room-level `notices` only) — the actual node graph is the run-global
// `run.map.points`, gated here by the current room being the map screen.
fn surface_map_room(state: &Value, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/map_room")
    {
        return false;
    }
    let view_player_id = state.pointer("/run/view/playerId").and_then(Value::as_str);

    if let Some(points) = state.pointer("/run/map/points").and_then(Value::as_array) {
        for (index, point) in points.iter().enumerate() {
            if point.get("travelable").and_then(Value::as_bool) != Some(true) {
                continue;
            }
            let Some(id) = point.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut args = Map::new();
            args.insert("mapNodeId".into(), json!(id));
            emit_action(
                actions,
                "select-map-node",
                format!("action:map:select-map-node:{id}"),
                map_point_label(point),
                args,
                view_player_id.map(str::to_string),
                Some(format!("run.map.points[{index}]")),
            );
        }
    }

    true
}

fn map_point_label(point: &Value) -> Option<String> {
    let point_type = point.get("pointType").and_then(Value::as_str)?;
    let row = point.pointer("/coord/row").and_then(Value::as_i64)?;
    let col = point.pointer("/coord/col").and_then(Value::as_i64)?;
    Some(format!("{point_type} ({row},{col})"))
}

// In-hand card selection mode (run.view.handSelection, e.g. Survivor's
// "Discard 1 card."): a card effect is asking the local seat to stage hand
// cards. While active the game's selection backstop blocks the normal combat
// surface (play-card, end-turn, pile viewers), so surface_combat suppresses
// those and this surface emits select/deselect/confirm instead.
fn surface_hand_selection(
    state: &Value,
    models: &PresentationModels,
    actions: &mut Vec<Value>,
) -> bool {
    if root_scene(state) != Some("run") {
        return false;
    }
    let Some(hand_selection) = non_null(state.pointer("/run/view/handSelection")) else {
        return false;
    };
    let Some(local_player) = local_player(state) else {
        return false;
    };
    let local_player_id = local_player.get("id").and_then(Value::as_str);
    let owner_player_id = hand_selection
        .get("playerId")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .or(local_player_id);
    if owner_player_id != local_player_id {
        // Hand selection is local-client UI; a different owner means the view
        // perspective cannot drive it.
        return true;
    }
    if hand_selection
        .get("isPeeking")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        // Selection input is suspended while peeking at the combat state.
        return true;
    }

    let string_ids = |key: &str| -> Vec<String> {
        hand_selection
            .get(key)
            .and_then(Value::as_array)
            .map(|ids| {
                ids.iter()
                    .filter_map(Value::as_str)
                    .map(str::to_string)
                    .collect()
            })
            .unwrap_or_default()
    };
    let selected_ids = string_ids("selectedCardIds");
    let selectable_ids = string_ids("selectableCardIds");
    let mode = hand_selection
        .get("mode")
        .and_then(Value::as_str)
        .unwrap_or("simple-select");

    let hand_cards = local_player
        .pointer("/combat/hand/cards")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let card_label = |card_id: &str| -> Option<String> {
        let card = hand_cards
            .iter()
            .find(|card| card.get("id").map(cel_string).as_deref() == Some(card_id))?;
        let model_id = card.get("modelId").and_then(Value::as_str)?;
        Some(combat_card_label(models, model_id, card))
    };

    for card_id in selectable_ids
        .iter()
        .filter(|card_id| !selected_ids.contains(card_id))
    {
        let mut args = Map::new();
        args.insert("cardId".into(), json!(card_id));
        if let Some(player_id) = owner_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            "select-hand-card",
            format!("action:combat-room:select-hand-card:{card_id}"),
            card_label(card_id),
            args,
            owner_player_id.map(str::to_string),
            Some("run.view.handSelection.selectableCardIds".to_string()),
        );
    }

    // The upgrade-select hand UI swaps the staged card instead of deselecting.
    if mode != "upgrade-select" {
        for card_id in &selected_ids {
            let mut args = Map::new();
            args.insert("cardId".into(), json!(card_id));
            if let Some(player_id) = owner_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "deselect-hand-card",
                format!("action:combat-room:deselect-hand-card:{card_id}"),
                card_label(card_id),
                args,
                owner_player_id.map(str::to_string),
                Some("run.view.handSelection.selectedCardIds".to_string()),
            );
        }
    }

    if hand_selection
        .get("canConfirm")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        let mut args = Map::new();
        if let Some(player_id) = owner_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        // Echo the staged selection so a client that kept selection local can stage
        // + confirm in one call (the bridge stages any cardId not already staged).
        args.insert("cardIds".into(), json!(selected_ids));
        let label = hand_selection
            .get("promptText")
            .and_then(Value::as_str)
            .filter(|prompt| !prompt.is_empty())
            .map(|prompt| format!("Confirm: {prompt}"))
            .unwrap_or_else(|| "Confirm".to_string());
        emit_action(
            actions,
            "confirm-hand-selection",
            "action:combat-room:confirm-hand-selection".to_string(),
            Some(label),
            args,
            owner_player_id.map(str::to_string),
            Some("run.view.handSelection".to_string()),
        );
    }

    true
}

fn surface_combat(state: &Value, models: &PresentationModels, actions: &mut Vec<Value>) -> bool {
    if root_scene(state) != Some("run")
        || state
            .pointer("/run/currentRoom/scene")
            .and_then(Value::as_str)
            != Some("rooms/combat_room")
        || non_null(state.pointer("/run/currentRoom/combat")).is_none()
    {
        return false;
    }
    let combat_state = non_null(state.pointer("/run/currentRoom/combat/combatState"));
    let Some(local_player) = local_player(state) else {
        return false;
    };
    if combat_state.is_none() {
        return false;
    }
    let local_player_id = local_player.get("id").and_then(Value::as_str);
    let is_local_turn = combat_state
        .and_then(|cs| cs.get("currentSide"))
        .and_then(Value::as_str)
        == Some("Player");
    let player_actions_disabled = combat_state
        .and_then(|cs| cs.get("playerActionsDisabled"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let view = state.pointer("/run/view");
    let is_in_card_selection = view
        .and_then(|view| view.get("isInCardSelection"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    // In-hand selection mode: the game's selection backstop blocks the normal
    // combat surface; surface_hand_selection emits the legal actions instead.
    let hand_selection_active = non_null(state.pointer("/run/view/handSelection")).is_some();
    let can_remove_potions = local_player
        .get("canRemovePotions")
        .and_then(Value::as_bool)
        .unwrap_or(true);
    let local_player_alive = local_player
        .pointer("/creature/currentHp")
        .and_then(Value::as_i64)
        .unwrap_or(1)
        > 0;
    let local_player_has_ended = local_player
        .pointer("/combat/hasEndedTurn")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let can_use_potions = is_local_turn
        && can_remove_potions
        && local_player_alive
        && !player_actions_disabled
        && !is_in_card_selection;
    let can_discard_potions = can_remove_potions && local_player_alive;

    let hand_cards = local_player
        .pointer("/combat/hand/cards")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let creatures = combat_creatures(state, combat_state);

    // play-card
    if is_local_turn && !hand_selection_active {
        for item in combat_play_card_actions(&hand_cards, &creatures, &models.cards) {
            let card_id = item.get("cardId").map(cel_string).unwrap_or_default();
            let target_id = item.get("targetId").and_then(Value::as_str);
            let model_id = item
                .get("modelId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let id = match target_id {
                Some(target_id) => {
                    format!("action:combat-room:play-card:{card_id}:{target_id}")
                }
                None => format!("action:combat-room:play-card:{card_id}"),
            };
            let mut args = Map::new();
            args.insert("cardId".into(), json!(card_id));
            if let Some(target_id) = target_id {
                args.insert("targetId".into(), json!(target_id));
            }
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            let source_index = item.get("sourceIndex").map(cel_string).unwrap_or_default();
            emit_action(
                actions,
                "play-card",
                id,
                Some(combat_card_label(models, model_id, &item)),
                args,
                local_player_id.map(str::to_string),
                Some(format!("run.players[].combat.hand.cards[{source_index}]")),
            );
        }
    }

    // use-potion
    if is_local_turn {
        let potions = local_player
            .get("potions")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        for potion in potions.iter().filter_map(Value::as_object) {
            let model_id = potion
                .get("modelId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            if model_id.is_empty()
                || !can_use_potions
                || !potion_is_manually_usable(potion, models.potions.get(model_id))
            {
                continue;
            }

            let potion_id = potion.get("id").map(cel_string).unwrap_or_default();
            let mut args = Map::new();
            args.insert("potionId".into(), json!(potion_id.clone()));
            if let Some(player_id) = local_player_id {
                args.insert("playerId".into(), json!(player_id));
            }
            emit_action(
                actions,
                "open-potion-popup",
                format!("action:combat-room:open-potion-popup:{potion_id}"),
                Some(potion_label(
                    models,
                    model_id,
                    &Value::Object(potion.clone()),
                )),
                args,
                local_player_id.map(str::to_string),
                Some(format!("run.players[].potions[{potion_id}]")),
            );
        }

        if can_use_potions {
            for item in combat_use_potion_actions(&potions, &creatures, &models.potions) {
                let potion_id = item.get("potionId").map(cel_string).unwrap_or_default();
                let target_id = item.get("targetId").and_then(Value::as_str);
                let model_id = item
                    .get("modelId")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                let id = match target_id {
                    Some(target_id) => {
                        format!("action:combat-room:use-potion:{potion_id}:{target_id}")
                    }
                    None => format!("action:combat-room:use-potion:{potion_id}"),
                };
                let mut args = Map::new();
                args.insert("potionId".into(), json!(potion_id));
                if let Some(target_id) = target_id {
                    args.insert("targetId".into(), json!(target_id));
                }
                if let Some(player_id) = local_player_id {
                    args.insert("playerId".into(), json!(player_id));
                }
                emit_action(
                    actions,
                    "use-potion",
                    id,
                    Some(potion_label(models, model_id, &item)),
                    args,
                    local_player_id.map(str::to_string),
                    Some(format!("run.players[].potions[{potion_id}]")),
                );
            }
        }

        if let Some(selected_potion) = view
            .and_then(|view| view.get("selectedPotion"))
            .and_then(Value::as_object)
        {
            let mode = selected_potion
                .get("mode")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let slot_index = selected_potion.get("slotIndex").and_then(Value::as_i64);
            let selected_potion_state = slot_index.and_then(|slot_index| {
                potions.iter().find(|potion| {
                    potion
                        .get("id")
                        .and_then(Value::as_i64)
                        .or_else(|| potion.get("slotIndex").and_then(Value::as_i64))
                        == Some(slot_index)
                })
            });
            if let Some(selected_potion_state_value) = selected_potion_state {
                if let Some(selected_potion_state) = selected_potion_state_value.as_object() {
                    let potion_id = selected_potion_state
                        .get("id")
                        .map(cel_string)
                        .unwrap_or_default();
                    let model_id = selected_potion_state
                        .get("modelId")
                        .and_then(Value::as_str)
                        .unwrap_or_default();
                    let model = models.potions.get(model_id);

                    if mode == "popup" && !potion_id.is_empty() {
                        if can_use_potions
                            && potion_use_button_enabled(
                                selected_potion_state_value,
                                &creatures,
                                &models.potions,
                            )
                        {
                            let action = if potion_requires_target(model) {
                                "start-potion-targeting"
                            } else {
                                "use-potion"
                            };
                            let mut args = Map::new();
                            args.insert("potionId".into(), json!(potion_id.clone()));
                            if let Some(player_id) = local_player_id {
                                args.insert("playerId".into(), json!(player_id));
                            }
                            emit_action(
                                actions,
                                action,
                                format!("action:combat-room:{action}:{potion_id}"),
                                None,
                                args,
                                local_player_id.map(str::to_string),
                                Some("run.view.selectedPotion".to_string()),
                            );
                        }

                        if can_discard_potions
                            && !selected_potion_state
                                .get("isQueued")
                                .and_then(Value::as_bool)
                                .unwrap_or(false)
                        {
                            let mut args = Map::new();
                            args.insert("potionId".into(), json!(potion_id.clone()));
                            if let Some(player_id) = local_player_id {
                                args.insert("playerId".into(), json!(player_id));
                            }
                            emit_action(
                                actions,
                                "discard-potion",
                                format!("action:combat-room:discard-potion:{potion_id}"),
                                None,
                                args,
                                local_player_id.map(str::to_string),
                                Some("run.view.selectedPotion".to_string()),
                            );
                        }
                    } else if mode == "targeting" {
                        for item in combat_use_potion_actions(
                            std::slice::from_ref(selected_potion_state_value),
                            &creatures,
                            &models.potions,
                        ) {
                            let Some(target_id) = item.get("targetId").and_then(Value::as_str)
                            else {
                                continue;
                            };
                            let mut args = Map::new();
                            args.insert("targetId".into(), json!(target_id));
                            if let Some(player_id) = local_player_id {
                                args.insert("playerId".into(), json!(player_id));
                            }
                            emit_action(
                                actions,
                                "select-target",
                                format!("action:combat-room:select-target:{target_id}"),
                                None,
                                args,
                                local_player_id.map(str::to_string),
                                Some("run.view.selectedPotion".to_string()),
                            );
                        }

                        let mut args = Map::new();
                        if let Some(player_id) = local_player_id {
                            args.insert("playerId".into(), json!(player_id));
                        }
                        emit_action(
                            actions,
                            "cancel-selection",
                            "action:combat-room:cancel-selection:potion-targeting".to_string(),
                            None,
                            args,
                            local_player_id.map(str::to_string),
                            Some("run.view.selectedPotion".to_string()),
                        );
                    }
                }
            }
        }
    }

    // end-turn / cancel-end-turn — a single in-game toggle. While the local
    // seat has not ended, the button ends the turn; once ended (ready, waiting
    // on living allies) it cancels the pending end. Surface whichever applies.
    if is_local_turn && local_player_alive && !hand_selection_active {
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        let (kind, id, label) = if local_player_has_ended {
            (
                "cancel-end-turn",
                "action:combat-room:cancel-end-turn",
                "Cancel",
            )
        } else {
            ("end-turn", "action:combat-room:end-turn", "End Turn")
        };
        emit_action(
            actions,
            kind,
            id.to_string(),
            Some(label.to_string()),
            args,
            local_player_id.map(str::to_string),
            Some("run.currentRoom.combat.combatState".to_string()),
        );
    }

    // view-{draw,discard,exhaust}-pile — open the in-game card pile viewer.
    // The pile cards are already in state; these are available regardless of
    // whose turn it is. The exhaust button hides while empty in-game, so it is
    // only surfaced when the exhaust pile has cards.
    // The selection backstop also covers the pile buttons while a hand
    // selection is active.
    let pile_viewers: &[(&str, &str, &str)] = if hand_selection_active {
        &[]
    } else {
        &[
            ("view-draw-pile", "drawPile", "Draw Pile"),
            ("view-discard-pile", "discardPile", "Discard Pile"),
            ("view-exhaust-pile", "exhaustPile", "Exhaust Pile"),
        ]
    };
    for (kind, key, label) in pile_viewers.iter().copied() {
        let Some(cards) = local_player
            .pointer(&format!("/combat/{key}/cards"))
            .and_then(Value::as_array)
        else {
            continue;
        };
        if kind == "view-exhaust-pile" && cards.is_empty() {
            continue;
        }
        let mut args = Map::new();
        if let Some(player_id) = local_player_id {
            args.insert("playerId".into(), json!(player_id));
        }
        emit_action(
            actions,
            kind,
            format!("action:combat-room:{kind}"),
            Some(label.to_string()),
            args,
            local_player_id.map(str::to_string),
            Some(format!("run.players[].combat.{key}")),
        );
    }

    true
}

// ---------------------------------------------------------------------------
// Computed helpers (port of the catalog `computed.*` block)
// ---------------------------------------------------------------------------

fn root_scene(state: &Value) -> Option<&str> {
    state.get("rootScene").and_then(Value::as_str)
}

fn non_null(value: Option<&Value>) -> Option<&Value> {
    value.filter(|value| !value.is_null())
}

fn run_view_player_id(state: &Value) -> Option<&str> {
    state
        .pointer("/run/view/playerId")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
}

fn local_player(state: &Value) -> Option<&Value> {
    let player_id = state
        .pointer("/run/view/playerId")
        .and_then(Value::as_str)?;
    state
        .pointer("/run/players")
        .and_then(Value::as_array)?
        .iter()
        .find(|player| player.get("id").and_then(Value::as_str) == Some(player_id))
}

fn character_select_local_player(cs: &Value) -> Option<&Value> {
    let player_id = cs.pointer("/view/playerId").and_then(Value::as_str)?;
    cs.pointer("/lobby/players")
        .and_then(Value::as_array)?
        .iter()
        .find(|player| player.get("id").and_then(Value::as_str) == Some(player_id))
}

fn character_select_selected_button(cs: &Value) -> Option<&Value> {
    let selected_id = cs
        .pointer("/view/selectedCharacterButtonId")
        .and_then(Value::as_str)?;
    cs.pointer("/characterButtons")
        .and_then(Value::as_array)?
        .iter()
        .find(|button| button.get("id").and_then(Value::as_str) == Some(selected_id))
}

fn top_overlay<'a>(state: &'a Value, key: &str) -> Option<&'a Value> {
    let overlays = local_player(state)?
        .get("overlays")
        .and_then(Value::as_array)?;
    overlays
        .iter()
        .rfind(|overlay| non_null(overlay.get(key)).is_some())
}

fn event_local_player_state<'a>(state: &Value, event: &'a Value) -> Option<&'a Value> {
    let player_id = state
        .pointer("/run/view/playerId")
        .and_then(Value::as_str)?;
    event
        .get("playerStates")
        .and_then(Value::as_array)?
        .iter()
        .find(|player_state| {
            player_state.get("playerId").and_then(Value::as_str) == Some(player_id)
        })
}

fn local_event_options_unchosen(player_state: Option<&Value>) -> bool {
    let Some(player_state) = player_state else {
        return false;
    };
    let Some(options) = player_state.get("options").and_then(Value::as_array) else {
        return false;
    };
    options
        .iter()
        .all(|option| option.get("wasChosen").and_then(Value::as_bool) != Some(true))
}

fn finished_event_proceed_actions(player_state: Option<&Value>) -> Vec<Value> {
    let Some(player_state) = player_state else {
        return Vec::new();
    };
    if player_state.get("isFinished").and_then(Value::as_bool) != Some(true) {
        return Vec::new();
    }
    let player_id = player_state.get("playerId").cloned().unwrap_or(Value::Null);
    // eventProceedTitle = locKey('events','PROCEED') ?? 'Continue'; loc is empty.
    vec![json!({
        "playerId": player_id,
        "label": "Continue",
        "sourcePath": "run.currentRoom.event.playerStates[].isFinished",
    })]
}

/// Port of the catalog `eventOptionsWithPlayerIds` function: flatten each
/// player's event options, stamping `playerId` and a `sourcePath`.
fn event_options_with_player_ids(player_states: Option<&Vec<Value>>) -> Vec<Value> {
    let Some(states) = player_states else {
        return Vec::new();
    };
    let mut result = Vec::new();
    for (player_index, player_state) in states.iter().enumerate() {
        let Some(player_state) = player_state.as_object() else {
            continue;
        };
        let player_id = player_state
            .get("playerId")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string();
        let Some(options) = player_state.get("options").and_then(Value::as_array) else {
            continue;
        };
        for (option_index, option) in options.iter().enumerate() {
            let Some(option) = option.as_object() else {
                continue;
            };
            let mut option = option.clone();
            option.insert("playerId".to_string(), json!(player_id));
            option.insert(
                "sourcePath".to_string(),
                json!(format!(
                    "run.currentRoom.event.playerStates[{player_index}].options[{option_index}]"
                )),
            );
            result.push(Value::Object(option));
        }
    }
    result
}

fn combat_creatures(state: &Value, combat_state: Option<&Value>) -> Vec<Value> {
    let mut creatures: Vec<Value> = combat_state
        .and_then(|cs| cs.get("enemies"))
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    if let Some(players) = state.pointer("/run/players").and_then(Value::as_array) {
        for player in players {
            if let Some(creature) = non_null(player.get("creature")) {
                creatures.push(creature.clone());
            }
        }
    }
    creatures
}

// ---------------------------------------------------------------------------
// Combat list-builders (ports of the catalog `combatPlayCardActions` /
// `combatUsePotionActions`)
// ---------------------------------------------------------------------------

fn combat_play_card_actions(
    cards: &[Value],
    creatures: &[Value],
    models: &Map<String, Value>,
) -> Vec<Value> {
    let mut result = Vec::new();
    for (index, card) in cards.iter().enumerate() {
        let Some(card) = card.as_object() else {
            continue;
        };
        if card
            .get("unplayableReason")
            .and_then(Value::as_array)
            .is_some_and(|reasons| !reasons.is_empty())
        {
            continue;
        }
        let card_id = card.get("id").cloned().unwrap_or(Value::Null);
        let model_id = card
            .get("modelId")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let target_type = models
            .get(model_id)
            .and_then(|model| model.get("targetType"))
            .and_then(Value::as_str);
        match combat_target_side(target_type) {
            Some(side) => {
                for target in combat_alive_creatures_on_side(creatures, side) {
                    result.push(json!({
                        "cardId": card_id,
                        "modelId": model_id,
                        "targetId": target.get("id").cloned().unwrap_or(Value::Null),
                        "targetName": target.get("modelId").cloned().unwrap_or(Value::Null),
                        "sourceIndex": index,
                    }));
                }
            }
            None => result.push(json!({
                "cardId": card_id,
                "modelId": model_id,
                "targetId": Value::Null,
                "targetName": Value::Null,
                "sourceIndex": index,
            })),
        }
    }
    result
}

fn combat_use_potion_actions(
    potions: &[Value],
    creatures: &[Value],
    models: &Map<String, Value>,
) -> Vec<Value> {
    let mut result = Vec::new();
    for potion in potions {
        let Some(potion) = potion.as_object() else {
            continue;
        };
        let model_id = match potion.get("modelId").and_then(Value::as_str) {
            Some(model_id) if !model_id.is_empty() => model_id,
            _ => continue,
        };
        let model = models.get(model_id);
        if !potion_is_manually_usable(potion, model) {
            continue;
        }
        let potion_id = potion.get("id").cloned().unwrap_or(Value::Null);
        let target_type = model
            .and_then(|model| model.get("targetType"))
            .and_then(Value::as_str);
        match combat_potion_target_side(target_type) {
            Some(side) => {
                for target in combat_alive_creatures_on_side(creatures, side) {
                    result.push(json!({
                        "potionId": potion_id,
                        "modelId": model_id,
                        "targetId": target.get("id").cloned().unwrap_or(Value::Null),
                        "targetName": target.get("modelId").cloned().unwrap_or(Value::Null),
                    }));
                }
            }
            None => result.push(json!({
                "potionId": potion_id,
                "modelId": model_id,
                "targetId": Value::Null,
                "targetName": Value::Null,
            })),
        }
    }
    result
}

fn potion_is_manually_usable(potion: &Map<String, Value>, model: Option<&Value>) -> bool {
    if potion
        .get("isQueued")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        return false;
    }
    if !potion
        .get("passesUsabilityCheck")
        .and_then(Value::as_bool)
        .unwrap_or(true)
    {
        return false;
    }
    model
        .and_then(|model| model.get("usage"))
        .and_then(Value::as_str)
        != Some("Automatic")
}

fn potion_use_button_enabled(
    potion: &Value,
    creatures: &[Value],
    models: &Map<String, Value>,
) -> bool {
    let Some(potion_object) = potion.as_object() else {
        return false;
    };
    let model_id = match potion_object.get("modelId").and_then(Value::as_str) {
        Some(model_id) if !model_id.is_empty() => model_id,
        _ => return false,
    };
    let model = models.get(model_id);
    if !potion_is_manually_usable(potion_object, model) {
        return false;
    }
    if !potion_requires_target(model) {
        return true;
    }
    combat_use_potion_actions(std::slice::from_ref(potion), creatures, models)
        .iter()
        .any(|action| action.get("targetId").and_then(Value::as_str).is_some())
}

fn potion_requires_target(model: Option<&Value>) -> bool {
    matches!(
        model
            .and_then(|model| model.get("targetType"))
            .and_then(Value::as_str),
        Some("Self" | "AnyEnemy" | "AnyAlly" | "AnyPlayer")
    )
}

fn combat_target_side(target_type: Option<&str>) -> Option<&'static str> {
    match target_type {
        Some("AnyEnemy") => Some("Enemy"),
        Some("AnyAlly") | Some("AnyPlayer") => Some("Player"),
        _ => None,
    }
}

fn combat_potion_target_side(target_type: Option<&str>) -> Option<&'static str> {
    match target_type {
        Some("AnyEnemy") => Some("Enemy"),
        Some("AnyAlly") | Some("AnyPlayer") | Some("Self") => Some("Player"),
        _ => None,
    }
}

fn combat_alive_creatures_on_side<'a>(
    creatures: &'a [Value],
    side: &str,
) -> impl Iterator<Item = &'a Map<String, Value>> {
    let side = side.to_string();
    creatures
        .iter()
        .filter_map(Value::as_object)
        .filter(move |creature| {
            creature
                .get("currentHp")
                .and_then(Value::as_i64)
                .unwrap_or(0)
                > 0
                && creature.get("side").and_then(Value::as_str) == Some(side.as_str())
        })
}

// ---------------------------------------------------------------------------
// Labels (empty-loc fallbacks)
// ---------------------------------------------------------------------------

/// Empty-loc `locRef`: a plain string passes through; a `{table,key}` ref or
/// null yields `None` (the catalog then falls back to the action `kind`).
fn loc_ref_label(value: Option<&Value>) -> Option<String> {
    match value {
        Some(Value::String(text)) => Some(text.clone()),
        _ => None,
    }
}

/// `models.cards[modelId] == null ? modelId : locRef(title)`. When the card
/// model is present (always, on real input) the label is `locRef(title)`, which
/// is `None` under empty loc -> the caller falls back to the action `kind`.
fn choose_a_card_label(models: &PresentationModels, model_id: Option<&str>) -> Option<String> {
    let model_id = model_id?;
    match models.cards.get(model_id) {
        None => Some(model_id.to_string()),
        Some(model) => loc_ref_label(model.get("title")),
    }
}

fn event_option_title(item: &Value) -> Option<String> {
    match item.get("titleText").and_then(Value::as_str) {
        Some(text) if !text.is_empty() => Some(text.to_string()),
        _ => loc_ref_label(item.get("titleLoc")),
    }
}

fn combat_card_label(models: &PresentationModels, model_id: &str, item: &Value) -> String {
    let base = models
        .cards
        .get(model_id)
        .and_then(|model| loc_ref_label(model.get("title")))
        .unwrap_or_else(|| model_id.to_string());
    combat_target_suffix(base, item)
}

fn potion_label(models: &PresentationModels, model_id: &str, item: &Value) -> String {
    let base = models
        .potions
        .get(model_id)
        .and_then(|model| loc_ref_label(model.get("title")))
        .unwrap_or_else(|| model_id.to_string());
    combat_target_suffix(base, item)
}

fn combat_target_suffix(base: String, item: &Value) -> String {
    match item.get("targetId").and_then(Value::as_str) {
        None => base,
        Some(target_id) => {
            let target_name = item
                .get("targetName")
                .and_then(Value::as_str)
                .unwrap_or(target_id);
            format!("{base} → {target_name}")
        }
    }
}

fn reward_label(item: &Value) -> String {
    if let Some(description) = loc_ref_label(item.get("description")) {
        return description;
    }
    if let Some(gold) = item.get("gold").filter(|gold| !gold.is_null()) {
        return format!("{} Gold", cel_string(gold));
    }
    if let Some(relic) = item.get("relic").and_then(Value::as_str) {
        return relic.to_string();
    }
    if let Some(potion) = item.get("potion").and_then(Value::as_str) {
        return potion.to_string();
    }
    if let Some(model_id) = item.pointer("/card/modelId").and_then(Value::as_str) {
        return model_id.to_string();
    }
    if non_null(item.get("cardReward")).is_some() {
        return "Card Reward".to_string();
    }
    if item.get("cardRemoval").and_then(Value::as_bool) == Some(true) {
        return "Card Removal".to_string();
    }
    "Reward".to_string()
}

/// CEL `string()` for the scalar cases that reach it here (string / number / bool).
fn cel_string(value: &Value) -> String {
    match value {
        Value::String(text) => text.clone(),
        Value::Number(number) => number.to_string(),
        Value::Bool(boolean) => boolean.to_string(),
        _ => String::new(),
    }
}

// ---------------------------------------------------------------------------
// Action assembly + timings
// ---------------------------------------------------------------------------

fn emit_action(
    actions: &mut Vec<Value>,
    kind: &str,
    id: String,
    label: Option<String>,
    args: Map<String, Value>,
    owner_player_id: Option<String>,
    source_path: Option<String>,
) {
    let label = label.unwrap_or_else(|| kind.to_string());
    let mut action = Map::new();
    action.insert("id".to_string(), json!(id));
    action.insert("kind".to_string(), json!(kind));
    action.insert("label".to_string(), json!(label));
    action.insert("enabled".to_string(), json!(true));
    action.insert("args".to_string(), Value::Object(args));
    if let Some(owner_player_id) = owner_player_id.filter(|value| !value.trim().is_empty()) {
        action.insert("ownerPlayerId".to_string(), json!(owner_player_id));
    }
    if let Some(source_path) = source_path.filter(|value| !value.trim().is_empty()) {
        action.insert("sourcePath".to_string(), json!(source_path));
    }
    actions.push(Value::Object(action));
}

#[derive(Default)]
struct StateActionsTimings {
    stages: Vec<(&'static str, u64)>,
}

impl StateActionsTimings {
    fn record(&mut self, label: &'static str, start: std::time::Instant) {
        self.stages
            .push((label, start.elapsed().as_millis() as u64));
    }

    fn into_diagnostic(self, code: &str, total_ms: u64) -> Value {
        let mut map = Map::new();
        for (label, ms) in self.stages {
            map.insert(label.to_string(), json!(ms));
        }
        json!({
            "code": code,
            "severity": "info",
            "totalMs": total_ms,
            "stagesMs": Value::Object(map),
        })
    }
}

#[cfg(test)]
mod tests;
