use super::*;

pub(crate) fn execute_map_drawings_json(
    args: MapDrawingsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let rpc_timeout_ms = args
        .rpc_timeout_ms
        .or(context.config.transport.rpc_timeout_ms)
        .unwrap_or(DEFAULT_BRIDGE_RPC_TIMEOUT_MS);
    let scoped_context = context_with_transport_rpc_timeout(context, rpc_timeout_ms);
    let client = bridge_client(scoped_context.as_app_context(context.json_output));
    let response = client
        .map_drawings(bridge::proto::MapDrawingsRequest {
            request_id: "cli-map-drawings-1".to_string(),
            player_id: args.player_id.unwrap_or_default(),
        })
        .map_err(AppError::bridge)?;

    Ok(map_drawings_json(&response))
}

pub(crate) fn map_drawings_json(response: &bridge::proto::MapDrawingsResponse) -> Value {
    // Strokes are returned in capture order so `--json` output (and the captured
    // raw/drawings.json) diffs cleanly; each point is a [x, y] pair in TheMap content space.
    let strokes: Vec<Value> = response
        .strokes
        .iter()
        .map(|stroke| {
            let points: Vec<Value> = stroke.points.iter().map(|p| json!([p.x, p.y])).collect();
            json!({
                "id": stroke.id,
                "ownerPlayerId": stroke.owner_player_id,
                "color": stroke.color,
                "isEraser": stroke.is_eraser,
                "points": Value::Array(points),
            })
        })
        .collect();

    json!({
        "requestId": response.request_id,
        "schemaVersion": "spirectl.map-drawings-result/v0",
        "source": bridge::data_source_name(bridge::enum_value(response.source)),
        "mapActive": response.map_active,
        "strokes": Value::Array(strokes),
    })
}
