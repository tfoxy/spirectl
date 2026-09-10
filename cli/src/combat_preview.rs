use super::*;

pub(crate) fn execute_combat_preview_json(
    args: CombatPreviewArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .combat_preview(bridge::proto::CombatPreviewRequest {
            request_id: "cli-combat-preview-1".to_string(),
            player_id: args.player_id.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;

    Ok(combat_preview_json(&response))
}

pub(crate) fn combat_preview_json(response: &bridge::proto::CombatPreviewResponse) -> Value {
    // Deterministic key order so `--json` output (and the captured oracle) diffs cleanly.
    let mut card_ids: Vec<&String> = response.cards.keys().collect();
    card_ids.sort();
    let cards: serde_json::Map<String, Value> = card_ids
        .into_iter()
        .map(|id| {
            let card = &response.cards[id];
            let mut target_ids: Vec<&String> = card.damage_by_target.keys().collect();
            target_ids.sort();
            let damage_by_target: serde_json::Map<String, Value> = target_ids
                .into_iter()
                .map(|target| (target.clone(), json!(card.damage_by_target[target])))
                .collect();
            (
                id.clone(),
                json!({
                    "block": card.block,
                    "selfDamage": card.self_damage,
                    "damageByTarget": Value::Object(damage_by_target),
                }),
            )
        })
        .collect();

    json!({
        "requestId": response.request_id,
        "schemaVersion": "spirectl.combat-preview-result/v0",
        "source": bridge::data_source_name(bridge::enum_value(response.source)),
        "provisional": response.provisional,
        "combatActive": response.combat_active,
        "cards": Value::Object(cards),
    })
}
