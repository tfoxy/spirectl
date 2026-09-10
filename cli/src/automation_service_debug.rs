use http_body_util::BodyExt;
use hyper::body::Incoming;
use hyper::{Method, Request, Response, StatusCode};
use percent_encoding::percent_decode_str;
use serde::Deserialize;
use serde_json::{Value, json};

use super::{
    DebugEventsBody, DebugSessionStartBody, DebugWaitBody, ResponseBody, ServiceState,
    json_response,
};
use crate::{
    DebugEventsArgs, DebugSessionEndArgs, DebugSessionRoleArg, DebugSessionStartArgs,
    DebugSessionStatusArgs, DebugWaitArgs, execute_debug_events_json,
    execute_debug_session_end_json, execute_debug_session_start_json,
    execute_debug_session_status_json, execute_debug_wait_json,
};

async fn decode_json_body<T: for<'de> Deserialize<'de> + Default>(
    request: Request<Incoming>,
) -> Result<T, Value> {
    let body = request
        .into_body()
        .collect()
        .await
        .map_err(|source| {
            json!({
                "error": {
                    "code": "invalid_json_body",
                    "message": format!("Failed to read request body: {source}")
                }
            })
        })?
        .to_bytes();

    if body.is_empty() {
        return Ok(T::default());
    }

    serde_json::from_slice(&body).map_err(|source| {
        json!({
            "error": {
                "code": "invalid_json_body",
                "message": format!("Failed to parse request body as JSON: {source}")
            }
        })
    })
}

fn debug_session_id_from_path(path: &str) -> Option<String> {
    let trimmed = path.strip_prefix("/v0/debug-sessions/")?;
    let id_part = trimmed
        .strip_suffix("/wait")
        .or_else(|| trimmed.strip_suffix("/events"))
        .unwrap_or(trimmed);
    let id_part = id_part.trim_end_matches('/');
    if id_part.is_empty() {
        return None;
    }

    Some(percent_decode_str(id_part).decode_utf8_lossy().into_owned())
}

fn debug_events_body_from_query(query: Option<&str>) -> Result<DebugEventsBody, Value> {
    let Some(query) = query else {
        return Ok(DebugEventsBody::default());
    };
    let mut body = DebugEventsBody::default();

    for part in query.split('&').filter(|part| !part.is_empty()) {
        let mut pieces = part.splitn(2, '=');
        let key = percent_decode_str(pieces.next().unwrap_or_default())
            .decode_utf8_lossy()
            .into_owned();
        let value = percent_decode_str(pieces.next().unwrap_or_default())
            .decode_utf8_lossy()
            .into_owned();

        match key.as_str() {
            "fromSequence" => {
                body.from_sequence = Some(value.parse::<u64>().map_err(|source| {
                    json!({
                        "error": {
                            "code": "invalid_query_parameter",
                            "message": format!("Invalid fromSequence query parameter: {source}")
                        }
                    })
                })?);
            }
            "limit" => {
                body.limit = Some(value.parse::<u32>().map_err(|source| {
                    json!({
                        "error": {
                            "code": "invalid_query_parameter",
                            "message": format!("Invalid limit query parameter: {source}")
                        }
                    })
                })?);
            }
            "follow" => {
                body.follow = Some(value.parse::<bool>().map_err(|source| {
                    json!({
                        "error": {
                            "code": "invalid_query_parameter",
                            "message": format!("Invalid follow query parameter: {source}")
                        }
                    })
                })?);
            }
            "timeoutMs" => {
                body.timeout_ms = Some(value.parse::<u32>().map_err(|source| {
                    json!({
                        "error": {
                            "code": "invalid_query_parameter",
                            "message": format!("Invalid timeoutMs query parameter: {source}")
                        }
                    })
                })?);
            }
            _ => {
                return Err(json!({
                    "error": {
                        "code": "invalid_query_parameter",
                        "message": format!("Unknown debug events query parameter '{key}'.")
                    }
                }));
            }
        }
    }

    Ok(body)
}

fn merge_debug_events_body(mut query: DebugEventsBody, body: DebugEventsBody) -> DebugEventsBody {
    query.from_sequence = body.from_sequence.or(query.from_sequence);
    query.limit = body.limit.or(query.limit);
    query.follow = body.follow.or(query.follow);
    query.timeout_ms = body.timeout_ms.or(query.timeout_ms);
    query
}

pub(super) async fn handle_debug_session_start(
    request: Request<Incoming>,
    state: &ServiceState,
) -> Response<ResponseBody> {
    let body = match decode_json_body::<DebugSessionStartBody>(request).await {
        Ok(body) => body,
        Err(error) => return json_response(StatusCode::BAD_REQUEST, error),
    };

    match execute_debug_session_start_json(
        DebugSessionStartArgs {
            name: body.name,
            role: body.role.unwrap_or(DebugSessionRoleArg::Controller),
            pause: body.pause.unwrap_or(false),
            lease_timeout_ms: body.lease_timeout_ms.unwrap_or(60_000),
        },
        state.runtime_context.as_app_context(true),
    ) {
        Ok(payload) => json_response(StatusCode::OK, payload),
        Err(error) => json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    }
}

pub(super) async fn handle_debug_session_events(
    request: Request<Incoming>,
    state: &ServiceState,
) -> Response<ResponseBody> {
    let path = request.uri().path().to_string();
    let Some(session_id) = debug_session_id_from_path(&path) else {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "not_found",
                    "message": "Automation service endpoint not found."
                }
            }),
        );
    };

    let query_body = match debug_events_body_from_query(request.uri().query()) {
        Ok(body) => body,
        Err(error) => return json_response(StatusCode::BAD_REQUEST, error),
    };
    let body = if request.method() == Method::POST {
        match decode_json_body::<DebugEventsBody>(request).await {
            Ok(body) => body,
            Err(error) => return json_response(StatusCode::BAD_REQUEST, error),
        }
    } else {
        DebugEventsBody::default()
    };
    let body = merge_debug_events_body(query_body, body);

    match execute_debug_events_json(
        DebugEventsArgs {
            session: Some(session_id),
            from_sequence: body.from_sequence.unwrap_or(0),
            limit: body.limit.unwrap_or(100),
            follow: body.follow.unwrap_or(false),
            timeout_ms: body.timeout_ms.unwrap_or(5000),
        },
        state.runtime_context.as_app_context(true),
    ) {
        Ok(payload) => json_response(StatusCode::OK, payload),
        Err(error) => json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    }
}

pub(super) fn handle_debug_session_get(path: &str, state: &ServiceState) -> Response<ResponseBody> {
    if path.ends_with("/wait") {
        return json_response(
            StatusCode::METHOD_NOT_ALLOWED,
            json!({
                "error": {
                    "code": "method_not_allowed",
                    "message": "Use POST /v0/debug-sessions/{sessionId}/wait for debug wait."
                }
            }),
        );
    }
    if path.ends_with("/events") {
        return json_response(
            StatusCode::METHOD_NOT_ALLOWED,
            json!({
                "error": {
                    "code": "method_not_allowed",
                    "message": "Use GET or POST /v0/debug-sessions/{sessionId}/events for debug events."
                }
            }),
        );
    }

    let Some(session_id) = debug_session_id_from_path(path) else {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "not_found",
                    "message": "Automation service endpoint not found."
                }
            }),
        );
    };

    match execute_debug_session_status_json(
        DebugSessionStatusArgs { id: session_id },
        state.runtime_context.as_app_context(true),
    ) {
        Ok(payload) => json_response(StatusCode::OK, payload),
        Err(error) => json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    }
}

pub(super) async fn handle_debug_session_end(
    request: Request<Incoming>,
    state: &ServiceState,
) -> Response<ResponseBody> {
    let path = request.uri().path().to_string();
    if path.ends_with("/wait") {
        return json_response(
            StatusCode::METHOD_NOT_ALLOWED,
            json!({
                "error": {
                    "code": "method_not_allowed",
                    "message": "Use POST /v0/debug-sessions/{sessionId}/wait for debug wait."
                }
            }),
        );
    }

    let Some(session_id) = debug_session_id_from_path(&path) else {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "not_found",
                    "message": "Automation service endpoint not found."
                }
            }),
        );
    };

    let resume = request
        .uri()
        .query()
        .map(|query| query.contains("resume=true"))
        .unwrap_or(false);
    match execute_debug_session_end_json(
        DebugSessionEndArgs {
            id: session_id,
            resume,
        },
        state.runtime_context.as_app_context(true),
    ) {
        Ok(payload) => json_response(StatusCode::OK, payload),
        Err(error) => json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    }
}

pub(super) async fn handle_debug_session_wait(
    request: Request<Incoming>,
    state: &ServiceState,
) -> Response<ResponseBody> {
    let path = request.uri().path().to_string();
    let Some(session_id) = debug_session_id_from_path(&path) else {
        return json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "not_found",
                    "message": "Automation service endpoint not found."
                }
            }),
        );
    };
    let body = match decode_json_body::<DebugWaitBody>(request).await {
        Ok(body) => body,
        Err(error) => return json_response(StatusCode::BAD_REQUEST, error),
    };

    match execute_debug_wait_json(
        DebugWaitArgs {
            session: session_id,
            timeout_ms: body.timeout_ms.unwrap_or(5000),
        },
        state.runtime_context.as_app_context(true),
    ) {
        Ok(payload) => json_response(StatusCode::OK, payload),
        Err(error) => json_response(StatusCode::BAD_REQUEST, error.payload.clone()),
    }
}
