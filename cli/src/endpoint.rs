//! Per-invocation endpoint selection, shared with bridge installation.

use crate::{AppConfig, AppError, TransportKind};
use serde_json::json;
use std::net::{SocketAddr, ToSocketAddrs};

pub(crate) const ENDPOINT_FILE_NAME: &str = "spirectl-bridge.endpoint.json";

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum EndpointSpec {
    Tcp(String),
    Pipe(String),
    Unix(String),
}

impl EndpointSpec {
    pub(crate) fn parse(spec: &str) -> Result<Self, AppError> {
        let invalid = |message: &str| invalid_endpoint(spec, message);
        let (kind, address) = spec
            .split_once(':')
            .ok_or_else(|| invalid("Expected tcp:<host:port>, pipe:<name>, or unix:<path>."))?;
        if address.trim().is_empty() || address.contains('\0') {
            return Err(invalid(
                "Endpoint address must be non-empty and contain no NUL.",
            ));
        }
        match kind {
            "tcp" => {
                Self::tcp_addresses(address)?;
                Ok(Self::Tcp(address.to_string()))
            }
            "pipe" => Ok(Self::Pipe(address.to_string())),
            "unix" => Ok(Self::Unix(address.to_string())),
            _ => Err(invalid(
                "Expected tcp:<host:port>, pipe:<name>, or unix:<path>.",
            )),
        }
    }

    pub(crate) fn apply(&self, config: &mut AppConfig) {
        config.transport.ipc_path = None;
        config.transport.pipe_name = None;
        config.transport.tcp_address = None;
        config.transport.kind = match self {
            Self::Tcp(address) => {
                config.transport.tcp_address = Some(address.clone());
                TransportKind::Tcp
            }
            Self::Pipe(name) => {
                config.transport.pipe_name = Some(name.clone());
                TransportKind::Ipc
            }
            Self::Unix(path) => {
                config.transport.ipc_path = Some(path.clone());
                TransportKind::Ipc
            }
        };
    }

    pub(crate) fn file_json(&self) -> Result<serde_json::Value, AppError> {
        let (kind, address) = match self {
            Self::Tcp(address) => {
                let addresses = Self::tcp_addresses(address)?;
                // The bridge file needs a literal IP; prefer its default IPv4 loopback family.
                let literal = addresses
                    .iter()
                    .find(|candidate| candidate.is_ipv4())
                    .unwrap_or(&addresses[0]);
                ("tcp", literal.to_string())
            }
            Self::Pipe(name) => ("named-pipe", name.clone()),
            Self::Unix(path) => ("unix-socket", path.clone()),
        };
        Ok(json!({"schema": "spirectl-bridge-endpoint/1", "kind": kind, "address": address}))
    }

    fn tcp_addresses(address: &str) -> Result<Vec<SocketAddr>, AppError> {
        let invalid = |message: &str| invalid_endpoint(&format!("tcp:{address}"), message);
        let addresses = address
            .to_socket_addrs()
            .map_err(|_| invalid("TCP endpoint must be a loopback host:port pair."))?
            .collect::<Vec<_>>();
        if addresses.is_empty()
            || addresses
                .iter()
                .any(|candidate| !candidate.ip().is_loopback() || candidate.port() == 0)
        {
            return Err(invalid(
                "TCP endpoint must use loopback addresses and a non-zero port.",
            ));
        }
        Ok(addresses)
    }
}

fn invalid_endpoint(spec: &str, message: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({"error": {"code": "invalid_endpoint", "message": message, "endpoint": spec}}),
    }
}

pub(crate) fn apply_override(
    config: &mut AppConfig,
    flag: Option<&str>,
    environment: Option<&str>,
    instance: Option<&str>,
) -> Result<(), AppError> {
    let Some(spec) = flag.or(environment) else {
        return Ok(());
    };
    if instance.is_some() {
        return Err(AppError {
            exit_code: 2,
            payload: json!({"error": {"code": "endpoint_instance_conflict", "message": "--endpoint / SPIRECTL_ENDPOINT cannot be combined with --instance or a configured default instance."}}),
        });
    }
    EndpointSpec::parse(spec)?.apply(config);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn endpoint_specs_parse_valid_addresses() {
        for (input, expected) in [
            (
                "tcp:127.0.0.1:51173",
                EndpointSpec::Tcp("127.0.0.1:51173".into()),
            ),
            ("tcp:[::1]:51173", EndpointSpec::Tcp("[::1]:51173".into())),
            (
                "tcp:localhost:51173",
                EndpointSpec::Tcp("localhost:51173".into()),
            ),
            ("pipe:seat-a", EndpointSpec::Pipe("seat-a".into())),
            (
                "unix:/tmp/seat:a.sock",
                EndpointSpec::Unix("/tmp/seat:a.sock".into()),
            ),
        ] {
            assert_eq!(EndpointSpec::parse(input).unwrap(), expected);
        }
    }

    #[test]
    fn endpoint_tcp_hostnames_are_preserved_for_clients_and_resolved_for_files() {
        for address in ["localhost:51173", "127.0.0.1:51173", "[::1]:51173"] {
            let endpoint = EndpointSpec::parse(&format!("tcp:{address}")).unwrap();
            let mut config = AppConfig::default();
            endpoint.apply(&mut config);
            assert_eq!(config.transport.tcp_address.as_deref(), Some(address));

            let file = endpoint.file_json().unwrap();
            let written = file["address"].as_str().unwrap();
            let literal = written.parse::<std::net::SocketAddr>().unwrap();
            assert!(literal.ip().is_loopback());
            assert_eq!(literal.port(), 51173);
            if address == "localhost:51173" {
                assert!(address.parse::<std::net::SocketAddr>().is_err());
                if address
                    .to_socket_addrs()
                    .unwrap()
                    .any(|addr| addr.is_ipv4())
                {
                    assert!(literal.is_ipv4());
                }
            } else {
                assert_eq!(written, address);
            }
        }
    }

    #[test]
    fn endpoint_specs_reject_invalid_or_non_loopback_addresses() {
        for input in [
            "",
            "tcp",
            "pipe:",
            "unix: ",
            "http:localhost",
            "tcp:127.0.0.1",
            "tcp:127.0.0.1:0",
            "tcp:127.0.0.1:99999",
            "tcp:0.0.0.0:51173",
            "tcp:192.0.2.1:51173",
            "tcp:[::]:51173",
            "pipe:a\0b",
        ] {
            let error = EndpointSpec::parse(input).unwrap_err();
            assert_eq!(error.exit_code, 2, "{input}");
            assert_eq!(
                error.payload["error"]["code"], "invalid_endpoint",
                "{input}"
            );
        }
    }

    #[test]
    fn endpoint_override_flag_wins_and_clears_other_addresses() {
        let mut config = AppConfig::default();
        config.transport.pipe_name = Some("old-pipe".into());
        config.transport.ipc_path = Some("old.sock".into());
        apply_override(
            &mut config,
            Some("tcp:127.0.0.1:1234"),
            Some("invalid"),
            None,
        )
        .unwrap();
        assert!(matches!(config.transport.kind, TransportKind::Tcp));
        assert_eq!(
            config.transport.tcp_address.as_deref(),
            Some("127.0.0.1:1234")
        );
        assert!(config.transport.pipe_name.is_none());
        assert!(config.transport.ipc_path.is_none());
        apply_override(&mut config, None, Some("unix:/tmp/seat.sock"), None).unwrap();
        assert!(matches!(config.transport.kind, TransportKind::Ipc));
        assert!(config.transport.tcp_address.is_none());
        assert_eq!(config.transport.ipc_path.as_deref(), Some("/tmp/seat.sock"));
    }

    #[test]
    fn endpoint_override_rejects_instance_for_flag_and_env() {
        for (flag, env) in [
            (Some("pipe:seat"), None),
            (None, Some("unix:/tmp/seat.sock")),
        ] {
            let error =
                apply_override(&mut AppConfig::default(), flag, env, Some("seat")).unwrap_err();
            assert_eq!(error.exit_code, 2);
            assert_eq!(error.payload["error"]["code"], "endpoint_instance_conflict");
        }
    }
}
