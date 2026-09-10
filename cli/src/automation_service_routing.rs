use std::convert::Infallible;

use hyper::body::Incoming;
use hyper::{Method, Request, Response, StatusCode};
use serde_json::json;

use super::{
    ResponseBody, ServiceState, ai_tools_catalog_json, authorize, decode_tool_call_request,
    execute_ai_tool_json, handle_debug_session_end, handle_debug_session_events,
    handle_debug_session_get, handle_debug_session_start, handle_debug_session_wait,
    handle_submit_test_run, handle_test_run_get, json_response, service_app_error_payload,
};

pub(super) async fn handle_request(
    request: Request<Incoming>,
    state: ServiceState,
) -> Result<Response<ResponseBody>, Infallible> {
    if let Some(response) = authorize(&request, state.auth_token.as_deref()) {
        return Ok(response);
    }

    let method = request.method().clone();
    let path = request.uri().path().to_string();

    let response = if method == Method::GET && path == "/v0/service" {
        json_response(StatusCode::OK, json!(state.metadata))
    } else if method == Method::GET && path == "/v0/ai-tools" {
        json_response(StatusCode::OK, ai_tools_catalog_json())
    } else if method == Method::POST && path == "/v0/debug-sessions" {
        handle_debug_session_start(request, &state).await
    } else if (method == Method::GET || method == Method::POST)
        && path.starts_with("/v0/debug-sessions/")
        && path.ends_with("/events")
    {
        handle_debug_session_events(request, &state).await
    } else if method == Method::GET && path.starts_with("/v0/debug-sessions/") {
        handle_debug_session_get(&path, &state)
    } else if method == Method::DELETE && path.starts_with("/v0/debug-sessions/") {
        handle_debug_session_end(request, &state).await
    } else if method == Method::POST
        && path.starts_with("/v0/debug-sessions/")
        && path.ends_with("/wait")
    {
        handle_debug_session_wait(request, &state).await
    } else if method == Method::POST && path == "/v0/tools/call" {
        match decode_tool_call_request(request).await {
            Ok(tool_call) => {
                if tool_call.name == "test_run" {
                    json_response(
                        StatusCode::BAD_REQUEST,
                        json!({
                            "error": {
                                "code": "unsupported_service_tool",
                                "message": "Remote test_run is only available through POST /v0/test-runs so callers can poll status and download bounded artifacts."
                            }
                        }),
                    )
                } else {
                    match execute_ai_tool_json(
                        &tool_call.name,
                        tool_call.arguments,
                        state.runtime_context.as_app_context(true),
                    ) {
                        Ok(payload) => json_response(StatusCode::OK, payload),
                        Err(error) => json_response(
                            StatusCode::BAD_REQUEST,
                            service_app_error_payload(&error),
                        ),
                    }
                }
            }
            Err(error) => json_response(StatusCode::BAD_REQUEST, error),
        }
    } else if method == Method::POST && path == "/v0/test-runs" {
        handle_submit_test_run(request, state).await
    } else if method == Method::GET && path.starts_with("/v0/test-runs/") {
        handle_test_run_get(&path, &state)
    } else {
        json_response(
            StatusCode::NOT_FOUND,
            json!({
                "error": {
                    "code": "not_found",
                    "message": "Automation service endpoint not found."
                }
            }),
        )
    };
    Ok(response)
}
