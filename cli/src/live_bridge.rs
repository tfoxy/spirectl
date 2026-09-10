use crate::AppConfig;
use crate::bridge;
use crate::install_paths::{PathOverrides, resolve_live_bridge_paths};
use serde::Serialize;
use std::fs;
#[cfg(unix)]
use std::os::unix::fs::FileTypeExt;
use std::path::{Path, PathBuf};

pub const DEPLOYED_MOD_DIR_NAME: &str = "spirectlbridge";

#[derive(Debug, Clone, Copy, Default)]
pub struct LiveBridgeOverrides<'a> {
    pub game_path: Option<&'a Path>,
    pub assemblies_dir: Option<&'a Path>,
    pub mods_dir: Option<&'a Path>,
    pub socket_path: Option<&'a str>,
    pub pipe_name: Option<&'a str>,
    pub tcp_address: Option<&'a str>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LiveBridgeEndpoint {
    UnixSocket(String),
    NamedPipe(String),
    Tcp(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveBridgeEndpointProbe {
    pub classification: &'static str,
    pub exists: Option<bool>,
    pub file_kind: Option<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StaleEndpointCleanupReport {
    pub attempted: bool,
    pub removed: bool,
    pub skipped: bool,
    pub reason: &'static str,
    pub path: Option<String>,
}

impl LiveBridgeEndpoint {
    pub fn transport_kind(&self) -> &'static str {
        match self {
            Self::UnixSocket(_) | Self::NamedPipe(_) => "ipc",
            Self::Tcp(_) => "tcp",
        }
    }

    pub fn kind(&self) -> &'static str {
        match self {
            Self::UnixSocket(_) => "unix-socket",
            Self::NamedPipe(_) => "named-pipe",
            Self::Tcp(_) => "tcp",
        }
    }

    pub fn display(&self) -> &str {
        match self {
            Self::UnixSocket(path) | Self::NamedPipe(path) | Self::Tcp(path) => path,
        }
    }

    pub fn socket_path(&self) -> Option<&str> {
        match self {
            Self::UnixSocket(path) => Some(path),
            Self::NamedPipe(_) | Self::Tcp(_) => None,
        }
    }

    pub fn pipe_name(&self) -> Option<&str> {
        match self {
            Self::NamedPipe(name) => Some(name),
            Self::UnixSocket(_) | Self::Tcp(_) => None,
        }
    }

    pub fn tcp_address(&self) -> Option<&str> {
        match self {
            Self::Tcp(address) => Some(address),
            Self::UnixSocket(_) | Self::NamedPipe(_) => None,
        }
    }

    pub fn launch_env_var(&self) -> (&'static str, &str) {
        match self {
            Self::UnixSocket(path) => ("SPIRECTL_BRIDGE_SOCKET_PATH", path),
            Self::NamedPipe(name) => ("SPIRECTL_BRIDGE_PIPE_NAME", name),
            Self::Tcp(address) => ("SPIRECTL_BRIDGE_TCP_ADDRESS", address),
        }
    }

    pub fn classify_probe(&self) -> LiveBridgeEndpointProbe {
        match self {
            Self::UnixSocket(path) => classify_unix_socket_probe(Path::new(path)),
            Self::NamedPipe(name) => classify_named_pipe_probe(name),
            Self::Tcp(_) => LiveBridgeEndpointProbe {
                classification: "tcp",
                exists: None,
                file_kind: None,
            },
        }
    }

    pub fn cleanup_stale_local_endpoint(
        &self,
        probe: &LiveBridgeEndpointProbe,
    ) -> StaleEndpointCleanupReport {
        match self {
            Self::UnixSocket(path) => cleanup_unix_socket_path(Path::new(path), probe),
            Self::NamedPipe(_) => StaleEndpointCleanupReport {
                attempted: false,
                removed: false,
                skipped: true,
                reason: "unsupported_endpoint_kind",
                path: None,
            },
            Self::Tcp(_) => StaleEndpointCleanupReport {
                attempted: false,
                removed: false,
                skipped: true,
                reason: "unsupported_endpoint_kind",
                path: None,
            },
        }
    }
}

fn cleanup_unix_socket_path(
    path: &Path,
    probe: &LiveBridgeEndpointProbe,
) -> StaleEndpointCleanupReport {
    let path_string = Some(path.display().to_string());
    if probe.classification != "local_socket_candidate" {
        return StaleEndpointCleanupReport {
            attempted: false,
            removed: false,
            skipped: true,
            reason: "not_stale_socket_candidate",
            path: path_string,
        };
    }
    let metadata = match fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) => {
            return StaleEndpointCleanupReport {
                attempted: false,
                removed: false,
                skipped: true,
                reason: match error.kind() {
                    std::io::ErrorKind::NotFound => "missing",
                    std::io::ErrorKind::PermissionDenied => "permission_denied",
                    _ => "metadata_error",
                },
                path: path_string,
            };
        }
    };
    let file_kind = socket_file_kind(&metadata);
    if file_kind != "socket" {
        return StaleEndpointCleanupReport {
            attempted: false,
            removed: false,
            skipped: true,
            reason: "unsafe_file_kind",
            path: path_string,
        };
    }
    match fs::remove_file(path) {
        Ok(()) => StaleEndpointCleanupReport {
            attempted: true,
            removed: true,
            skipped: false,
            reason: "removed",
            path: path_string,
        },
        Err(error) => StaleEndpointCleanupReport {
            attempted: true,
            removed: false,
            skipped: true,
            reason: match error.kind() {
                std::io::ErrorKind::PermissionDenied => "permission_denied",
                std::io::ErrorKind::NotFound => "missing",
                _ => "remove_failed",
            },
            path: path_string,
        },
    }
}

/// Whether the bridge's named pipe is currently published.
///
/// `\\.\pipe\` entries only exist while a server instance is waiting, so a
/// metadata hit is the Windows analogue of finding the Unix socket file. Off
/// Windows the pipe namespace does not exist and the answer stays unknown.
fn classify_named_pipe_probe(name: &str) -> LiveBridgeEndpointProbe {
    #[cfg(windows)]
    {
        let exists = fs::metadata(format!(r"\\.\pipe\{name}")).is_ok();
        LiveBridgeEndpointProbe {
            classification: if exists {
                "local_pipe_candidate"
            } else {
                "missing"
            },
            exists: Some(exists),
            file_kind: Some(if exists { "named-pipe" } else { "missing" }),
        }
    }

    #[cfg(not(windows))]
    {
        let _ = name;
        LiveBridgeEndpointProbe {
            classification: "named_pipe",
            exists: None,
            file_kind: None,
        }
    }
}

fn classify_unix_socket_probe(path: &Path) -> LiveBridgeEndpointProbe {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            let file_kind = socket_file_kind(&metadata);
            let classification = if file_kind == "socket" {
                "local_socket_candidate"
            } else {
                "ambiguous"
            };
            LiveBridgeEndpointProbe {
                classification,
                exists: Some(true),
                file_kind: Some(file_kind),
            }
        }
        Err(error) => {
            let classification = match error.kind() {
                std::io::ErrorKind::NotFound => "missing",
                std::io::ErrorKind::PermissionDenied => "permission-denied",
                _ => "ambiguous",
            };
            LiveBridgeEndpointProbe {
                classification,
                exists: Some(false),
                file_kind: Some("missing"),
            }
        }
    }
}

fn socket_file_kind(metadata: &fs::Metadata) -> &'static str {
    let file_type = metadata.file_type();
    #[cfg(unix)]
    {
        if file_type.is_socket() {
            return "socket";
        }
    }
    if file_type.is_file() {
        "file"
    } else if file_type.is_dir() {
        "directory"
    } else if file_type.is_symlink() {
        "symlink"
    } else {
        "other"
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedLiveBridgeLayout {
    pub game_path: PathBuf,
    pub assemblies_dir: PathBuf,
    pub mods_dir: PathBuf,
    pub mod_dir: PathBuf,
    pub endpoint: LiveBridgeEndpoint,
    pub(crate) used_discovered_game_path: bool,
    pub(crate) derived_assemblies_dir_from_game_path: bool,
}

impl ResolvedLiveBridgeLayout {
    pub fn used_discovered_game_path(&self) -> bool {
        self.used_discovered_game_path
    }

    pub fn derived_assemblies_dir_from_game_path(&self) -> bool {
        self.derived_assemblies_dir_from_game_path
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LiveBridgeDetectReport {
    pub status: &'static str,
    pub game_path: Option<String>,
    pub assemblies_dir: Option<String>,
    pub mods_dir: Option<String>,
    pub mod_dir: Option<String>,
    pub transport_kind: String,
    pub endpoint_kind: String,
    pub endpoint: String,
    pub socket_path: Option<String>,
    pub pipe_name: Option<String>,
    pub tcp_address: Option<String>,
    pub notes: Vec<String>,
}

pub fn detect_report(
    config: &AppConfig,
    overrides: LiveBridgeOverrides<'_>,
) -> LiveBridgeDetectReport {
    let endpoint = resolve_endpoint(config, overrides);

    match resolve_layout(config, overrides) {
        Ok(layout) => {
            let mut notes = vec![
                "Use `sts2 game install-bridge` to deploy only the bridge payload, or `sts2 game deploy <mod-project>` to deploy the bridge and a mod project together."
                    .to_string(),
                "Use `sts2 game launch` to start the configured game process or `sts2 game attach` to wait for an already-running live bridge."
                    .to_string(),
            ];
            if !layout.mods_dir.exists() {
                notes.push(format!(
                    "Mods directory '{}' does not exist yet; deploy will create it.",
                    layout.mods_dir.display()
                ));
            }
            match &endpoint {
                LiveBridgeEndpoint::UnixSocket(path)
                    if config.transport.ipc_path.is_none() && overrides.socket_path.is_none() =>
                {
                    notes.push(format!(
                        "No transport.ipcPath is configured; the CLI will use the default socket '{}'.",
                        path
                    ));
                }
                LiveBridgeEndpoint::NamedPipe(name)
                    if config.transport.pipe_name.is_none() && overrides.pipe_name.is_none() =>
                {
                    notes.push(format!(
                        "No transport.pipeName is configured; the CLI will use the default pipe '{}'.",
                        name
                    ));
                }
                LiveBridgeEndpoint::Tcp(address)
                    if config.transport.tcp_address.is_none() && overrides.tcp_address.is_none() =>
                {
                    notes.push(format!(
                        "No transport.tcpAddress is configured; the CLI will use the default TCP address '{}'.",
                        address
                    ));
                }
                _ => {}
            }

            LiveBridgeDetectReport {
                status: "ready",
                game_path: Some(layout.game_path.display().to_string()),
                assemblies_dir: Some(layout.assemblies_dir.display().to_string()),
                mods_dir: Some(layout.mods_dir.display().to_string()),
                mod_dir: Some(layout.mod_dir.display().to_string()),
                transport_kind: layout.endpoint.transport_kind().to_string(),
                endpoint_kind: layout.endpoint.kind().to_string(),
                endpoint: layout.endpoint.display().to_string(),
                socket_path: layout.endpoint.socket_path().map(ToOwned::to_owned),
                pipe_name: layout.endpoint.pipe_name().map(ToOwned::to_owned),
                tcp_address: layout.endpoint.tcp_address().map(ToOwned::to_owned),
                notes,
            }
        }
        Err(message) => LiveBridgeDetectReport {
            status: "needs-config",
            game_path: None,
            assemblies_dir: None,
            mods_dir: None,
            mod_dir: None,
            transport_kind: endpoint.transport_kind().to_string(),
            endpoint_kind: endpoint.kind().to_string(),
            endpoint: endpoint.display().to_string(),
            socket_path: endpoint.socket_path().map(ToOwned::to_owned),
            pipe_name: endpoint.pipe_name().map(ToOwned::to_owned),
            tcp_address: endpoint.tcp_address().map(ToOwned::to_owned),
            notes: vec![
                message,
                "Set game.path in sts2.config.yaml or sts2.local.yaml before using `sts2 game install-bridge`, `sts2 game deploy`, `sts2 game launch`, or `sts2 game attach`."
                    .to_string(),
            ],
        },
    }
}

pub fn resolve_layout(
    config: &AppConfig,
    overrides: LiveBridgeOverrides<'_>,
) -> Result<ResolvedLiveBridgeLayout, String> {
    let resolved = resolve_live_bridge_paths(
        config,
        PathOverrides {
            game_path: overrides.game_path,
            assemblies_dir: overrides.assemblies_dir,
            resources_dir: None,
            mods_dir: overrides.mods_dir,
        },
    )?;

    let used_discovered_game_path = resolved.used_discovered_game_path();
    let derived_assemblies_dir_from_game_path = resolved.derived_assemblies_dir_from_game_path();

    Ok(ResolvedLiveBridgeLayout {
        mod_dir: resolved.mods_dir.join(DEPLOYED_MOD_DIR_NAME),
        game_path: resolved.game_path,
        assemblies_dir: resolved.assemblies_dir,
        mods_dir: resolved.mods_dir,
        endpoint: resolve_endpoint(config, overrides),
        used_discovered_game_path,
        derived_assemblies_dir_from_game_path,
    })
}

fn resolve_endpoint(config: &AppConfig, overrides: LiveBridgeOverrides<'_>) -> LiveBridgeEndpoint {
    match config.transport.kind {
        crate::TransportKind::Mock => LiveBridgeEndpoint::UnixSocket(bridge::default_ipc_path()),
        crate::TransportKind::Ipc => {
            #[cfg(windows)]
            {
                let pipe_name = overrides
                    .pipe_name
                    .map(ToOwned::to_owned)
                    .or_else(|| config.transport.pipe_name.clone())
                    .unwrap_or_else(bridge::default_pipe_name);
                LiveBridgeEndpoint::NamedPipe(pipe_name)
            }

            #[cfg(not(windows))]
            {
                let socket_path = overrides
                    .socket_path
                    .map(ToOwned::to_owned)
                    .or_else(|| config.transport.ipc_path.clone())
                    .unwrap_or_else(bridge::default_ipc_path);
                LiveBridgeEndpoint::UnixSocket(socket_path)
            }
        }
        crate::TransportKind::Tcp => {
            let address = overrides
                .tcp_address
                .map(ToOwned::to_owned)
                .or_else(|| config.transport.tcp_address.clone())
                .unwrap_or_else(bridge::default_tcp_address);
            LiveBridgeEndpoint::Tcp(address)
        }
    }
}
