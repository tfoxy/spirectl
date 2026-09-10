use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use reqwest::Method;
use reqwest::blocking::Client;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tungstenite::client::IntoClientRequest;
use tungstenite::http::{HeaderName, HeaderValue};
use tungstenite::{Message, connect};

use crate::{AppError, Predicate, query};

#[derive(Debug, Clone)]
pub(crate) struct ProbeQuery {
    pub path: String,
    pub predicate: Predicate,
}

#[derive(Debug, Clone)]
pub(crate) struct HttpProbe {
    pub url: String,
    pub method: String,
    pub headers: Vec<String>,
    pub body: Option<String>,
    pub timeout_ms: u64,
    pub expect_status: Option<u16>,
    pub expect_headers: Vec<String>,
    pub query: Option<ProbeQuery>,
}

#[derive(Debug, Clone)]
pub(crate) struct HttpWaitProbe {
    pub http: HttpProbe,
    pub interval_ms: u64,
}

#[derive(Debug, Clone)]
pub(crate) struct FetchProbe {
    pub source: PathBuf,
    pub output: Option<PathBuf>,
    pub expect_sha256: Option<String>,
    pub query: Option<ProbeQuery>,
}

#[derive(Debug, Clone)]
pub(crate) struct WebsocketProbe {
    pub url: String,
    pub headers: Vec<String>,
    pub send_text: Vec<String>,
    pub expect_text: Vec<String>,
    pub timeout_ms: u64,
}

pub(crate) fn execute_http_probe_json(args: &HttpProbe) -> Result<Value, AppError> {
    let response = perform_http_request(args)?;
    validate_http_response("http", &response, args)?;
    Ok(json!({
        "probe": "http",
        "status": "passed",
        "request": {
            "method": args.method.to_uppercase(),
            "url": args.url,
            "timeoutMs": args.timeout_ms,
            "headers": args.headers,
        },
        "timing": response.timing,
        "response": response.rendered,
    }))
}

pub(crate) fn execute_http_wait_probe_json(args: &HttpWaitProbe) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let mut attempts = 0_usize;
    let mut last_response: Option<HttpResponseData> = None;

    loop {
        attempts += 1;
        if let Ok(response) = perform_http_request(&args.http) {
            match validate_http_response("http-wait", &response, &args.http) {
                Ok(()) => {
                    return Ok(json!({
                        "probe": "http-wait",
                        "status": "passed",
                        "attempts": attempts,
                        "elapsedMs": started_at.elapsed().as_millis(),
                        "request": {
                            "method": args.http.method.to_uppercase(),
                            "url": args.http.url,
                        },
                        "response": response.rendered,
                    }));
                }
                Err(error) => {
                    last_response = Some(response);
                    let _ = error;
                }
            }
        }

        if started_at.elapsed().as_millis() >= u128::from(args.http.timeout_ms) {
            return Err(AppError {
                exit_code: 3,
                payload: json!({
                    "error": {
                        "code": "probe_wait_timeout",
                        "message": format!(
                            "Timed out waiting for '{}' to satisfy the requested HTTP expectations.",
                            args.http.url
                        ),
                        "probe": "http-wait",
                        "url": args.http.url,
                        "attempts": attempts,
                        "timeoutMs": args.http.timeout_ms,
                        "intervalMs": args.interval_ms,
                        "timing": {
                            "elapsedMs": started_at.elapsed().as_millis(),
                        },
                        "lastResponse": last_response.map(|response| response.rendered),
                    }
                }),
            });
        }

        thread::sleep(Duration::from_millis(args.interval_ms));
    }
}

pub(crate) fn execute_fetch_probe_json(
    args: &FetchProbe,
    default_output: Option<&Path>,
    artifacts_dir: &str,
) -> Result<Value, AppError> {
    let bytes = read_fetch_source(&args.source)?;
    let output_path =
        resolve_fetch_output_path(args.output.as_deref(), default_output, artifacts_dir)?;
    if let Some(parent) = output_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|source| AppError::output_write(&output_path, &source))?;
    }
    fs::write(&output_path, &bytes)
        .map_err(|source| AppError::output_write(&output_path, &source))?;

    let sha256 = hex_sha256(&bytes);
    if let Some(expected) = &args.expect_sha256
        && !sha256.eq_ignore_ascii_case(expected)
    {
        return Err(AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "probe_assertion_failed",
                    "message": "Fetched artifact sha256 did not match the expected value.",
                    "probe": "fetch",
                    "field": "sha256",
                    "expected": expected,
                    "actual": sha256,
                }
            }),
        });
    }

    let text = String::from_utf8(bytes.clone()).ok();
    let parsed_json = text
        .as_deref()
        .and_then(|value| serde_json::from_str::<Value>(value).ok());
    let query_root = json!({
        "source": args.source.display().to_string(),
        "text": text,
        "json": parsed_json,
        "artifact": {
            "path": output_path.display().to_string(),
            "sha256": sha256,
        }
    });
    validate_query("fetch", &query_root, args.query.as_ref())?;

    Ok(json!({
        "probe": "fetch",
        "status": "passed",
        "source": args.source.display().to_string(),
        "artifact": {
            "path": output_path.display().to_string(),
            "byteLength": bytes.len(),
            "sha256": sha256,
        },
        "json": parsed_json,
        "text": text,
    }))
}

pub(crate) fn execute_websocket_probe_json(args: &WebsocketProbe) -> Result<Value, AppError> {
    let started_at = Instant::now();
    let mut request = args
        .url
        .as_str()
        .into_client_request()
        .map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_probe_request",
                    "message": format!("Invalid websocket URL '{}': {source}", args.url),
                    "probe": "websocket",
                }
            }),
        })?;
    for header in &args.headers {
        let (name, value) = split_key_value(header, "websocket header")?;
        request.headers_mut().insert(
            HeaderName::from_bytes(name.as_bytes()).map_err(|source| AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_probe_request",
                        "message": format!("Invalid websocket header name '{name}': {source}"),
                        "probe": "websocket",
                    }
                }),
            })?,
            HeaderValue::from_str(&value).map_err(|source| AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_probe_request",
                        "message": format!("Invalid websocket header value for '{name}': {source}"),
                        "probe": "websocket",
                    }
                }),
            })?,
        );
    }

    let (mut socket, response) = connect(request).map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "probe_request_failed",
                "message": format!("Failed to connect websocket '{}': {source}", args.url),
                "probe": "websocket",
                "url": args.url,
                "timing": {
                    "elapsedMs": started_at.elapsed().as_millis(),
                },
            }
        }),
    })?;

    let mut sent_frames = Vec::new();
    for frame in &args.send_text {
        socket
            .send(Message::Text(frame.clone()))
            .map_err(|source| AppError {
                exit_code: 4,
                payload: json!({
                    "error": {
                        "code": "probe_request_failed",
                        "message": format!("Failed to send websocket text frame: {source}"),
                        "probe": "websocket",
                        "url": args.url,
                        "sentFrames": sent_frames,
                        "timing": {
                            "elapsedMs": started_at.elapsed().as_millis(),
                        },
                    }
                }),
            })?;
        sent_frames.push(json!({"kind": "text", "text": frame}));
    }

    let mut received_frames = Vec::new();
    for expected in &args.expect_text {
        let message = socket.read().map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "probe_request_failed",
                    "message": format!("Failed to read websocket frame: {source}"),
                    "probe": "websocket",
                    "url": args.url,
                    "sentFrames": sent_frames,
                    "receivedFrames": received_frames,
                    "timing": {
                        "elapsedMs": started_at.elapsed().as_millis(),
                    },
                }
            }),
        })?;
        let text = match message {
            Message::Text(text) => text,
            other => {
                return Err(AppError {
                    exit_code: 3,
                    payload: json!({
                        "error": {
                            "code": "probe_assertion_failed",
                            "message": "Websocket frame kind did not match the expected text frame.",
                            "probe": "websocket",
                            "expected": "text",
                            "actual": format!("{other:?}"),
                            "url": args.url,
                            "sentFrames": sent_frames,
                            "receivedFrames": received_frames,
                            "timing": {
                                "elapsedMs": started_at.elapsed().as_millis(),
                            },
                        }
                    }),
                });
            }
        };
        if text != *expected {
            return Err(AppError {
                exit_code: 3,
                payload: json!({
                    "error": {
                        "code": "probe_assertion_failed",
                        "message": "Websocket text frame did not match the expected value.",
                        "probe": "websocket",
                        "expected": expected,
                        "actual": text,
                        "url": args.url,
                        "sentFrames": sent_frames,
                        "receivedFrames": received_frames,
                        "timing": {
                            "elapsedMs": started_at.elapsed().as_millis(),
                        },
                    }
                }),
            });
        }
        received_frames.push(json!({
            "kind": "text",
            "text": text,
        }));
    }

    let _ = socket.close(None);

    Ok(json!({
        "probe": "websocket",
        "status": "passed",
        "url": args.url,
        "request": {
            "url": args.url,
            "timeoutMs": args.timeout_ms,
            "headers": args.headers,
            "expectedTextFrames": args.expect_text,
        },
        "handshake": {
            "status": response.status().as_u16(),
            "headers": response
                .headers()
                .iter()
                .filter_map(|(name, value)| value.to_str().ok().map(|value| (name.to_string(), value.to_string())))
                .collect::<BTreeMap<_, _>>(),
        },
        "timing": {
            "elapsedMs": started_at.elapsed().as_millis(),
        },
        "sentFrames": sent_frames,
        "receivedFrames": received_frames,
    }))
}

fn perform_http_request(args: &HttpProbe) -> Result<HttpResponseData, AppError> {
    let started_at = Instant::now();
    let client = Client::builder()
        .timeout(Duration::from_millis(args.timeout_ms))
        .build()
        .map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "probe_request_failed",
                    "message": format!("Failed to build HTTP client: {source}"),
                    "probe": "http",
                }
            }),
        })?;
    let method =
        Method::from_bytes(args.method.trim().to_uppercase().as_bytes()).map_err(|source| {
            AppError {
                exit_code: 2,
                payload: json!({
                    "error": {
                        "code": "invalid_probe_request",
                        "message": format!("Unsupported HTTP method '{}': {source}", args.method),
                        "probe": "http",
                    }
                }),
            }
        })?;
    let mut request = client.request(method.clone(), &args.url);
    for header in &args.headers {
        let (name, value) = split_key_value(header, "HTTP header")?;
        request = request.header(name, value);
    }
    if let Some(body) = &args.body {
        request = request.body(body.clone());
    }

    let response = request.send().map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "probe_request_failed",
                "message": format!("HTTP request to '{}' failed: {source}", args.url),
                "probe": "http",
                "url": args.url,
                "timing": {
                    "elapsedMs": started_at.elapsed().as_millis(),
                },
            }
        }),
    })?;

    let status = response.status().as_u16();
    let status_text = response
        .status()
        .canonical_reason()
        .unwrap_or("")
        .to_string();
    let headers = response
        .headers()
        .iter()
        .filter_map(|(name, value)| {
            value
                .to_str()
                .ok()
                .map(|value| (name.to_string(), Value::String(value.to_string())))
        })
        .collect::<BTreeMap<_, _>>();
    let bytes = response.bytes().map_err(|source| AppError {
        exit_code: 4,
        payload: json!({
            "error": {
                "code": "probe_request_failed",
                "message": format!("Failed to read HTTP response body from '{}': {source}", args.url),
                "probe": "http",
                "url": args.url,
                "status": status,
                "timing": {
                    "elapsedMs": started_at.elapsed().as_millis(),
                },
            }
        }),
    })?;
    let body = bytes.to_vec();
    let text = String::from_utf8(body.clone()).ok();
    let parsed_json = text
        .as_deref()
        .and_then(|value| serde_json::from_str::<Value>(value).ok());
    let query_root = json!({
        "status": status,
        "headers": headers,
        "text": text,
        "json": parsed_json,
    });

    Ok(HttpResponseData {
        query_root,
        timing: json!({
            "elapsedMs": started_at.elapsed().as_millis(),
        }),
        rendered: json!({
            "status": status,
            "statusText": status_text,
            "headers": headers,
            "text": text,
            "json": parsed_json,
            "byteLength": body.len(),
            "resource": {
                "url": args.url,
                "method": args.method.to_uppercase(),
                "contentType": headers.get("content-type").and_then(Value::as_str),
            },
        }),
    })
}

fn validate_http_response(
    probe: &str,
    response: &HttpResponseData,
    args: &HttpProbe,
) -> Result<(), AppError> {
    if let Some(expected_status) = args.expect_status {
        let actual_status = response.query_root["status"].as_u64().unwrap_or_default() as u16;
        if actual_status != expected_status {
            return Err(AppError {
                exit_code: 3,
                payload: json!({
                    "error": {
                        "code": "probe_assertion_failed",
                        "message": "HTTP response status did not match the expected value.",
                        "probe": probe,
                        "field": "status",
                        "expected": expected_status,
                        "actual": actual_status,
                        "response": response.rendered,
                        "timing": response.timing,
                    }
                }),
            });
        }
    }

    for expected in &args.expect_headers {
        let (name, value) = split_key_value(expected, "expected header")?;
        let actual = response
            .query_root
            .get("headers")
            .and_then(Value::as_object)
            .and_then(|headers| headers.get(&name))
            .and_then(Value::as_str);
        if actual != Some(value.as_str()) {
            return Err(AppError {
                exit_code: 3,
                payload: json!({
                    "error": {
                        "code": "probe_assertion_failed",
                        "message": "HTTP response header did not match the expected value.",
                        "probe": probe,
                        "field": format!("headers.{name}"),
                        "expected": value,
                        "actual": actual,
                        "response": response.rendered,
                        "timing": response.timing,
                    }
                }),
            });
        }
    }

    validate_query(probe, &response.query_root, args.query.as_ref())
}

fn validate_query(probe: &str, root: &Value, query: Option<&ProbeQuery>) -> Result<(), AppError> {
    let Some(query) = query else {
        return Ok(());
    };
    let evaluation =
        query::evaluate(root, &query.path, &query.predicate).map_err(|error| match error {
            query::QueryError::InvalidPath(message) => {
                AppError::invalid_query(&query.path, &message)
            }
        })?;
    if evaluation.matched {
        Ok(())
    } else {
        Err(AppError {
            exit_code: 3,
            payload: json!({
                "error": {
                    "code": "probe_assertion_failed",
                    "message": format!("Probe assertion failed for path '{}'.", query.path),
                    "probe": probe,
                    "path": evaluation.path,
                    "operator": evaluation.operator,
                    "expected": evaluation.expected,
                    "actual": evaluation.actual,
                    "pathExists": evaluation.actual.is_some(),
                    "resolution": evaluation.resolution.map(|resolution| json!({
                        "resolvedPrefix": resolution.resolved_prefix,
                        "missingField": resolution.missing_field,
                        "missingIndex": resolution.missing_index,
                        "availableKeys": resolution.available_keys,
                        "arrayLength": resolution.array_length,
                        "encounteredType": resolution.encountered_type,
                    })),
                }
            }),
        })
    }
}

fn split_key_value(raw: &str, label: &str) -> Result<(String, String), AppError> {
    raw.split_once('=')
        .map(|(name, value)| (name.trim().to_string(), value.to_string()))
        .filter(|(name, _)| !name.is_empty())
        .ok_or_else(|| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "invalid_probe_request",
                    "message": format!("Invalid {label} '{raw}'. Use NAME=VALUE syntax."),
                }
            }),
        })
}

fn read_fetch_source(source: &Path) -> Result<Vec<u8>, AppError> {
    let source_string = source.display().to_string();
    let is_http = source_string.starts_with("http://") || source_string.starts_with("https://");
    let is_file_uri = source_string.starts_with("file://");
    if is_http {
        let response =
            reqwest::blocking::get(source_string.as_str()).map_err(|source| AppError {
                exit_code: 4,
                payload: json!({
                    "error": {
                        "code": "probe_request_failed",
                        "message": format!("Failed to fetch '{}': {source}", source_string),
                        "probe": "fetch",
                    }
                }),
            })?;
        let bytes = response.bytes().map_err(|source| AppError {
            exit_code: 4,
            payload: json!({
                "error": {
                    "code": "probe_request_failed",
                    "message": format!("Failed to read fetched response '{}': {source}", source_string),
                    "probe": "fetch",
                }
            }),
        })?;
        return Ok(bytes.to_vec());
    }

    let path = if is_file_uri {
        PathBuf::from(source_string.trim_start_matches("file://"))
    } else {
        source.to_path_buf()
    };
    fs::read(&path).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "probe_source_read_failed",
                "message": "failed to read fetch source",
                "probe": "fetch",
                "path": path.display().to_string(),
                "details": source.to_string(),
            }
        }),
    })
}

fn resolve_fetch_output_path(
    explicit: Option<&Path>,
    default_output: Option<&Path>,
    artifacts_dir: &str,
) -> Result<PathBuf, AppError> {
    if let Some(path) = explicit {
        return Ok(resolve_absolute_path(path));
    }
    if let Some(path) = default_output {
        return Ok(resolve_absolute_path(path));
    }
    let timestamp_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis();
    Ok(resolve_absolute_path(
        &Path::new(artifacts_dir)
            .join("fetches")
            .join(format!("fetch-{timestamp_ms}.bin")),
    ))
}

fn resolve_absolute_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    }
}

fn hex_sha256(bytes: &[u8]) -> String {
    let mut digest = Sha256::new();
    digest.update(bytes);
    format!("{:x}", digest.finalize())
}

#[derive(Debug, Clone)]
struct HttpResponseData {
    query_root: Value,
    timing: Value,
    rendered: Value,
}
