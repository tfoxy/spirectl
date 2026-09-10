use super::*;
use crate::models::model_catalog_json;

#[derive(Clone, Default)]
pub(crate) struct PresentationModels {
    pub(crate) cards: Map<String, Value>,
    pub(crate) potions: Map<String, Value>,
}

pub(crate) fn load_presentation_models(
    client: &bridge::RuntimeBridgeClient,
    state: &Value,
) -> Result<PresentationModels, AppError> {
    let potion_ids = presentation_run_potion_ids_for_state(state);
    let mut card_ids = presentation_run_overlay_card_ids_for_state(state);
    card_ids.extend(presentation_run_combat_card_ids_for_state(state));

    let potion_client = client.clone();
    let card_client = client.clone();
    let (potion_map, card_map) = thread::scope(|scope| {
        let potion_handle = scope.spawn(|| {
            load_presentation_model_family_map(
                &potion_client,
                "potions",
                "cli-presentation-render-models-potion",
                &potion_ids,
            )
        });
        let card_handle = scope.spawn(|| {
            load_presentation_model_family_map(
                &card_client,
                "cards",
                "cli-presentation-render-models-card",
                &card_ids,
            )
        });

        let potion_map = potion_handle
            .join()
            .map_err(|_| presentation_render_thread_error("potion models"))??;
        let card_map = card_handle
            .join()
            .map_err(|_| presentation_render_thread_error("card models"))??;
        Ok::<_, AppError>((potion_map, card_map))
    })?;

    Ok(PresentationModels {
        cards: card_map,
        potions: potion_map,
    })
}

fn load_presentation_model_family_map(
    client: &bridge::RuntimeBridgeClient,
    family: &str,
    request_id: &str,
    ids: &BTreeSet<String>,
) -> Result<Map<String, Value>, AppError> {
    if ids.is_empty() {
        return Ok(Map::new());
    }
    let response = client
        .models(bridge::proto::ModelCatalogRequest {
            request_id: request_id.to_string(),
            family: family.to_string(),
            ids: ids.iter().cloned().collect(),
            language: String::new(),
        })
        .map_err(AppError::bridge)?;
    Ok(model_family_map(&model_catalog_json(&response)))
}

fn presentation_render_thread_error(stage: &str) -> AppError {
    AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "presentation_input_thread_failed",
                "message": "presentation input loading failed unexpectedly.",
                "stage": stage,
            }
        }),
    }
}

fn presentation_run_potion_ids_for_state(state: &Value) -> BTreeSet<String> {
    let mut ids = BTreeSet::new();
    for player in state
        .pointer("/run/players")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .unwrap_or(&[])
    {
        for potion in player
            .get("potions")
            .and_then(Value::as_array)
            .map(Vec::as_slice)
            .unwrap_or(&[])
        {
            insert_presentation_model_id(&mut ids, potion.get("modelId").and_then(Value::as_str));
        }
    }
    ids
}

fn presentation_run_overlay_card_ids_for_state(state: &Value) -> BTreeSet<String> {
    let mut ids = BTreeSet::new();
    for player in state
        .pointer("/run/players")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .unwrap_or(&[])
    {
        for overlay in player
            .get("overlays")
            .and_then(Value::as_array)
            .map(Vec::as_slice)
            .unwrap_or(&[])
        {
            for card in overlay
                .pointer("/chooseACard/cards")
                .and_then(Value::as_array)
                .map(Vec::as_slice)
                .unwrap_or(&[])
            {
                insert_presentation_model_id(&mut ids, card.get("modelId").and_then(Value::as_str));
            }
        }
    }
    ids
}

fn presentation_run_combat_card_ids_for_state(state: &Value) -> BTreeSet<String> {
    let mut ids = BTreeSet::new();
    for player in state
        .pointer("/run/players")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .unwrap_or(&[])
    {
        let Some(combat) = player.get("combat").filter(|value| !value.is_null()) else {
            continue;
        };
        for pile in ["hand", "drawPile", "discardPile", "exhaustPile", "playPile"] {
            for card in combat
                .pointer(&format!("/{pile}/cards"))
                .and_then(Value::as_array)
                .map(Vec::as_slice)
                .unwrap_or(&[])
            {
                insert_presentation_model_id(&mut ids, card.get("modelId").and_then(Value::as_str));
            }
        }
    }
    ids
}

fn insert_presentation_model_id(ids: &mut BTreeSet<String>, id: Option<&str>) {
    if let Some(id) = id.map(str::trim).filter(|id| !id.is_empty()) {
        ids.insert(id.to_string());
    }
}

fn model_family_map(catalog: &Value) -> Map<String, Value> {
    let mut result = Map::new();
    for model in catalog
        .get("models")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .unwrap_or(&[])
    {
        let Some(id) = model.get("id").and_then(Value::as_str) else {
            continue;
        };
        for alias in model_id_aliases(id) {
            result.entry(alias).or_insert_with(|| model.clone());
        }
    }
    result
}

fn model_id_aliases(id: &str) -> Vec<String> {
    let normalized = normalize_presentation_id(id);
    let mut aliases = vec![id.to_string(), normalized.clone()];
    if let Some(trimmed) = normalized.strip_prefix("the-") {
        aliases.push(trimmed.to_string());
    }
    aliases.sort();
    aliases.dedup();
    aliases
}

fn normalize_presentation_id(id: &str) -> String {
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut previous_was_lower_or_digit = false;
    for character in id.trim().chars() {
        if character.is_ascii_alphanumeric() {
            if character.is_ascii_uppercase() && previous_was_lower_or_digit && !current.is_empty()
            {
                segments.push(std::mem::take(&mut current));
            }
            current.push(character.to_ascii_lowercase());
            previous_was_lower_or_digit =
                character.is_ascii_lowercase() || character.is_ascii_digit();
        } else {
            if !current.is_empty() {
                segments.push(std::mem::take(&mut current));
            }
            previous_was_lower_or_digit = false;
        }
    }
    if !current.is_empty() {
        segments.push(current);
    }
    segments.join("-")
}
