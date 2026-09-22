#![allow(
    clippy::result_large_err,
    reason = "Bridge APIs intentionally return generated protobuf BridgeError values by value for public API compatibility."
)]
#![allow(
    clippy::needless_update,
    reason = "Bridge mocks and transport adapters build generated protobuf structs with default updates to tolerate additive contract fields."
)]

use crate::{MockScenario, TransportConfig, TransportKind as ConfigTransportKind};
use prost::Message;
use serde_json::{Value, json};
use std::cell::RefCell;
use std::collections::BTreeMap;
use std::fs;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

pub mod proto {
    #![allow(
        clippy::large_enum_variant,
        reason = "Generated prost oneof enums mirror protobuf contracts; boxing variants would change the generated Rust API."
    )]

    tonic::include_proto!("spirectl.v0");
}

pub const DEFAULT_IPC_PATH: &str = ".sts2/ipc/spirectl-bridge.sock";
pub const DEFAULT_PIPE_NAME: &str = "spirectl-bridge";
pub const DEFAULT_TCP_ADDRESS: &str = "127.0.0.1:51173";
pub const START_RUN_LOBBY_SCREEN_ID: &str = "Screens.CharacterSelect.NCharacterSelectScreen";
pub const LOAD_RUN_LOBBY_SCREEN_ID: &str = "Screens.CharacterSelect.NMultiplayerLoadGameScreen";
pub const MAP_SCREEN_ID: &str = "Screens.Map.NMapScreen";
pub const REWARDS_SCREEN_ID: &str = "Screens.NRewardsScreen";
pub const SHOP_SCREEN_ID: &str = "Screens.Shops.NMerchantInventory";
pub const FAKE_MERCHANT_INVENTORY_SCREEN_ID: &str = "Screens.Shops.NFakeMerchantInventory";
pub const EVENT_ROOM_SCREEN_ID: &str = "Rooms.NEventRoom";
pub const REST_SITE_SCREEN_ID: &str = "Rooms.NRestSiteRoom";
pub const TREASURE_ROOM_SCREEN_ID: &str = "Rooms.NTreasureRoom";
pub const TREASURE_ROOM_RELIC_COLLECTION_SCREEN_ID: &str =
    "Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
pub const RELIC_SELECTION_SCREEN_ID: &str = "Screens.NChooseARelicSelection";
pub const CARD_REWARD_SELECTION_SCREEN_ID: &str =
    "Screens.CardSelection.NCardRewardSelectionScreen";
pub const CHOOSE_CARD_SELECTION_SCREEN_ID: &str =
    "Screens.CardSelection.NChooseACardSelectionScreen";
pub const SIMPLE_CARD_SELECTION_SCREEN_ID: &str = "Screens.CardSelection.NSimpleCardSelectScreen";
pub const DECK_CARD_SELECTION_SCREEN_ID: &str = "Screens.CardSelection.NDeckCardSelectScreen";
pub const DECK_UPGRADE_SELECTION_SCREEN_ID: &str = "Screens.CardSelection.NDeckUpgradeSelectScreen";
pub const DECK_TRANSFORM_SELECTION_SCREEN_ID: &str =
    "Screens.CardSelection.NDeckTransformSelectScreen";
pub const DECK_ENCHANT_SELECTION_SCREEN_ID: &str = "Screens.CardSelection.NDeckEnchantSelectScreen";
pub const BUNDLE_SELECTION_SCREEN_ID: &str = "Screens.CardSelection.NChooseABundleSelectionScreen";
pub const CRYSTAL_SPHERE_SCREEN_ID: &str = "Events.Custom.CrystalSphere.NCrystalSphereScreen";
// Run-terminal defeat/victory overlay (NGameOverScreen). Pushed onto the overlay
// stack (capstone closed) when a run ends; in scope we only author combat defeat.
pub const GAME_OVER_SCREEN_ID: &str = "Screens.GameOverScreen.NGameOverScreen";
const BRIDGE_VERSION: &str = env!("SPIRECTL_BRIDGE_VERSION");
const BRIDGE_SEMVER: &str = env!("SPIRECTL_BRIDGE_SEMVER");
const BOOTSTRAP_LOG_MAX_AGE: Duration = Duration::from_secs(300);

thread_local! {
    static MOCK_LOADED_FIXTURE_SCENARIO: RefCell<Option<MockScenario>> = const { RefCell::new(None) };
    static MOCK_LOADED_FIXTURE_JSON: RefCell<Option<Value>> = const { RefCell::new(None) };
    static MOCK_LOADED_PLAYER_IDS: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    static MOCK_LOADED_HOST_LOCAL_SEATS: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    static MOCK_LOADED_LOCKED_CHARACTERS: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
    static MOCK_PRESERVE_LOADED_FIXTURE_ON_NEXT_RUN: RefCell<bool> = const { RefCell::new(false) };

    /// The game's resolved `--user-dir`, when this run has one. Read by the log
    /// scans behind the bridge's own error reporting.
    static LIVE_LOG_DATA_ROOT: RefCell<Option<PathBuf>> = const { RefCell::new(None) };
}

/// Point the bridge's log scans at the game's user dir for this CLI run.
///
/// The scans exist to turn "the bridge is not reachable" into the bridge's OWN
/// failure line, which the game writes under its user dir. With `--instance`
/// (or any config setting `game.userDir`) that is NOT where this process's
/// environment resolves to, so without this the scan reads the operator's
/// default directory, finds nothing, and every instance bootstrap failure is
/// reported as a bare missing socket instead of the reason it is missing.
///
/// Set once per run from the already-resolved effective config, alongside the
/// other run-scoped state above, rather than threaded through
/// `RuntimeBridgeClient::from_config` — that takes only a `TransportConfig` and
/// so cannot see `game.userDir`.
pub(crate) fn set_live_log_data_root(root: Option<PathBuf>) {
    LIVE_LOG_DATA_ROOT.with(|slot| {
        *slot.borrow_mut() = root;
    });
}

pub(crate) fn live_log_data_root() -> Option<PathBuf> {
    LIVE_LOG_DATA_ROOT.with(|slot| slot.borrow().clone())
}

pub(crate) fn reset_mock_loaded_fixture_scenario() {
    MOCK_LOADED_FIXTURE_SCENARIO.with(|loaded| {
        *loaded.borrow_mut() = None;
    });
    MOCK_LOADED_FIXTURE_JSON.with(|loaded| {
        *loaded.borrow_mut() = None;
    });
    MOCK_LOADED_HOST_LOCAL_SEATS.with(|loaded| {
        loaded.borrow_mut().clear();
    });
    MOCK_LOADED_PLAYER_IDS.with(|loaded| {
        loaded.borrow_mut().clear();
    });
    MOCK_LOADED_LOCKED_CHARACTERS.with(|loaded| {
        loaded.borrow_mut().clear();
    });
    set_live_log_data_root(None);
}

#[doc(hidden)]
pub fn preserve_mock_loaded_fixture_for_next_cli_run() {
    MOCK_PRESERVE_LOADED_FIXTURE_ON_NEXT_RUN.with(|preserve| {
        *preserve.borrow_mut() = true;
    });
}

pub(crate) fn should_preserve_mock_loaded_fixture_for_next_cli_run() -> bool {
    MOCK_PRESERVE_LOADED_FIXTURE_ON_NEXT_RUN.with(|preserve| {
        let should_preserve = *preserve.borrow();
        *preserve.borrow_mut() = false;
        should_preserve
    })
}

#[doc(hidden)]
pub mod ipc_protocol;

mod mock_data;
mod output_json;

pub(crate) use mock_data::enum_value;
use mock_data::*;
use output_json::recent_bootstrap_failure_error;
pub(crate) use output_json::{
    Sts2LogCursor, capture_relevant_log_cursor, capture_sts2_log_cursor,
    latest_recent_relevant_log, recent_relevant_log_tail_since, steam_initialization_failure_since,
};
pub use output_json::{
    action_kind_name, asset_artifact_kind_name, attachment_state_name, bridge_error_code_name,
    bridge_error_exit_code, bridge_error_payload, console_command_json, data_source_name,
    handshake_json, hot_reload_response_json, hot_reload_status_json,
    inspect_actions_environment_blocked_json, inspect_actions_json, inspect_actions_static_json,
    inspect_actions_unavailable_json, logs_json, multiplayer_role_name, perspective_scope_name,
    remote_client_orchestration_state_name, transport_kind_name,
};

mod client;
pub use client::RuntimeBridgeClient;

pub fn bridge_version() -> &'static str {
    BRIDGE_VERSION
}

pub fn default_ipc_path() -> String {
    if let Ok(path) = std::env::var("SPIRECTL_BRIDGE_SOCKET_PATH")
        && !path.trim().is_empty()
    {
        return path;
    }

    default_ipc_root()
        .join(DEFAULT_IPC_PATH)
        .display()
        .to_string()
}

/// Deterministic per-instance Unix socket path: `<ipc-root>/.sts2/ipc/<name>.sock`.
///
/// Derived purely from the instance name (and the repo/ipc root) so any later
/// CLI invocation with the same `--instance <name>` re-derives the same path
/// without shared runtime state. Unlike [`default_ipc_path`], this does NOT
/// consult `SPIRECTL_BRIDGE_SOCKET_PATH`: the instance name owns the path.
///
/// This is the derivation alone, and it can exceed what the platform will bind —
/// callers that need a usable path want [`resolve_instance_ipc_path`].
pub fn instance_ipc_path(name: &str) -> String {
    default_ipc_root()
        .join(".sts2/ipc")
        .join(format!("{name}.sock"))
        .display()
        .to_string()
}

/// A per-instance socket path that this platform can actually bind.
///
/// `shortened` records whether the derived path had to be replaced, so the
/// caller can say so rather than leaving the operator to wonder why nothing
/// appeared under `.sts2/ipc/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedInstanceSocket {
    pub path: String,
    pub derived: String,
    pub shortened: bool,
}

/// Resolve the per-instance socket path, falling back to a short one when the
/// derived path is too long for this platform's `sun_path`.
///
/// WHY THIS FALLBACK EXISTS. `instance_ipc_path` hangs off the repo root, and
/// `default_ipc_root` returns the working directory unchanged when there is no
/// `.git` above it — so running from a deep directory (an agent scratch dir, a
/// nested worktree) derives a path the kernel refuses to bind. The game then
/// launches, looks healthy, and has no bridge; the CLI reports only that the
/// socket is missing. Shortening keeps `--instance` usable from anywhere.
///
/// DETERMINISM IS PRESERVED, which is the whole contract of the derivation: the
/// fallback is a digest of the derived path, so the same `--instance <name>` in
/// the same directory re-derives the same socket in a later invocation, with no
/// shared runtime state. The digest is SHA-256 rather than a `Hash` impl on
/// purpose — `DefaultHasher`'s output is not promised to be stable across Rust
/// versions, and an upgrade that silently moved the path would orphan the socket
/// of an instance that is still running.
pub fn resolve_instance_ipc_path(name: &str) -> ResolvedInstanceSocket {
    let derived = instance_ipc_path(name);
    if crate::host_paths::unix_socket_path_fits(&derived) {
        return ResolvedInstanceSocket {
            path: derived.clone(),
            derived,
            shortened: false,
        };
    }

    ResolvedInstanceSocket {
        path: shortened_instance_ipc_path(name, &derived),
        derived,
        shortened: true,
    }
}

/// `<short-runtime-dir>/spirectl-<name>-<digest>.sock`, trimming the name until
/// it fits. The digest is what keeps two instances of the same name in different
/// directories apart, so it is never the part that gets trimmed.
fn shortened_instance_ipc_path(name: &str, derived: &str) -> String {
    let digest = &crate::dev_probes::hex_sha256(derived.as_bytes())[..8];
    let dir = crate::host_paths::short_runtime_dir();
    let limit = crate::host_paths::max_unix_socket_path_len();

    let mut trimmed = name.to_string();
    loop {
        let stem = if trimmed.is_empty() {
            format!("spirectl-{digest}.sock")
        } else {
            format!("spirectl-{trimmed}-{digest}.sock")
        };
        let candidate = dir.join(&stem).display().to_string();
        if candidate.len() <= limit || trimmed.is_empty() {
            // Even the nameless form can overflow if the runtime dir is itself
            // pathological. Returning it anyway keeps this total; the bind then
            // fails with the platform's own message, which is the honest report.
            return candidate;
        }
        trimmed.pop();
    }
}

/// Deterministic per-instance Windows named pipe: `spirectl-<name>`.
pub fn instance_pipe_name(name: &str) -> String {
    format!("{DEFAULT_PIPE_NAME}-{name}")
}

pub fn default_pipe_name() -> String {
    DEFAULT_PIPE_NAME.to_string()
}

pub fn default_tcp_address() -> String {
    DEFAULT_TCP_ADDRESS.to_string()
}

fn default_ipc_root() -> PathBuf {
    let Ok(mut dir) = std::env::current_dir() else {
        return PathBuf::from(".");
    };
    let original = dir.clone();
    loop {
        if dir.join(".git").exists() {
            return dir;
        }
        if !dir.pop() {
            return original;
        }
    }
}

fn transport_unavailable_error(
    transport_kind: proto::TransportKind,
    endpoint: &str,
    note: &str,
) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::TransportUnavailable,
        &format!(
            "{} transport is not hosted yet in this spec",
            transport_kind_name(transport_kind)
        ),
        &[detail("endpoint", endpoint, note)],
    )
}

fn transport_misconfigured_error(
    transport_kind: proto::TransportKind,
    endpoint: &str,
    note: &str,
) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::TransportMisconfigured,
        &format!(
            "{} transport is configured with an invalid local endpoint",
            transport_kind_name(transport_kind)
        ),
        &[detail("endpoint", endpoint, note)],
    )
}

fn live_ipc_protocol_error(socket_path: &str, source: &str) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::RuntimeFailure,
        "The live IPC bridge returned an invalid standalone transport response.",
        &[detail(
            "endpoint",
            socket_path,
            &format!(
                "Confirm the CLI and deployed bridge agree on the standalone IPC protocol version. Transport detail: {source}"
            ),
        )],
    )
}

fn live_tcp_protocol_error(address: &str, source: &str) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::RuntimeFailure,
        "The live TCP bridge returned an invalid standalone transport response.",
        &[detail(
            "endpoint",
            address,
            &format!(
                "Confirm the CLI and deployed bridge agree on the standalone transport protocol version. Transport detail: {source}"
            ),
        )],
    )
}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, PartialEq, Eq)]
struct UnixSocketOwner {
    pid: String,
    command: String,
}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, PartialEq, Eq)]
struct UnixSocketCandidate {
    path: String,
    owners: Vec<UnixSocketOwner>,
}

#[cfg(unix)]
fn likely_live_ipc_socket_details(socket_path: &str) -> Vec<proto::ErrorDetail> {
    #[cfg(target_os = "linux")]
    {
        let Ok(proc_net_unix) = fs::read_to_string("/proc/net/unix") else {
            return Vec::new();
        };
        let owners_by_inode = unix_socket_owners_by_inode();
        return discover_live_ipc_socket_candidates(
            &proc_net_unix,
            socket_path,
            &owners_by_inode,
        )
        .into_iter()
        .take(3)
        .map(|candidate| {
            let owner_note = unix_socket_candidate_owner_note(&candidate);
            detail(
                "candidateEndpoint",
                &candidate.path,
                &format!(
                    "A different Unix socket matching the configured bridge socket name is listed by /proc/net/unix{owner_note}. If this is the live game bridge, set SPIRECTL_BRIDGE_SOCKET_PATH or transport.ipcPath to this path."
                ),
            )
        })
        .collect();
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = socket_path;
        Vec::new()
    }
}

#[cfg(target_os = "linux")]
fn discover_live_ipc_socket_candidates(
    proc_net_unix: &str,
    configured_socket_path: &str,
    owners_by_inode: &BTreeMap<String, Vec<UnixSocketOwner>>,
) -> Vec<UnixSocketCandidate> {
    let Some(configured_socket_name) = Path::new(configured_socket_path)
        .file_name()
        .and_then(|name| name.to_str())
    else {
        return Vec::new();
    };
    let Some(default_socket_name) = Path::new(DEFAULT_IPC_PATH)
        .file_name()
        .and_then(|name| name.to_str())
    else {
        return Vec::new();
    };
    let mut candidates_by_path: BTreeMap<String, UnixSocketCandidate> = BTreeMap::new();

    for line in proc_net_unix.lines().skip(1) {
        let Some((inode, path)) = parse_proc_net_unix_socket_line(line) else {
            continue;
        };
        if path == configured_socket_path {
            continue;
        }
        let Some(socket_name) = Path::new(&path).file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        if socket_name != configured_socket_name && socket_name != default_socket_name {
            continue;
        }

        let owners = owners_by_inode.get(&inode).cloned().unwrap_or_default();
        candidates_by_path.insert(path.clone(), UnixSocketCandidate { path, owners });
    }

    let mut candidates = candidates_by_path.into_values().collect::<Vec<_>>();
    candidates.sort_by(|left, right| {
        unix_socket_candidate_rank(right)
            .cmp(&unix_socket_candidate_rank(left))
            .then_with(|| left.path.cmp(&right.path))
    });
    candidates
}

#[cfg(target_os = "linux")]
fn parse_proc_net_unix_socket_line(line: &str) -> Option<(String, String)> {
    let columns = line.split_whitespace().collect::<Vec<_>>();
    if columns.len() < 8 {
        return None;
    }
    let inode = columns.get(6)?.to_string();
    let path = columns[7..].join(" ");
    if path.starts_with('/') {
        Some((inode, path))
    } else {
        None
    }
}

#[cfg(target_os = "linux")]
fn unix_socket_candidate_rank(candidate: &UnixSocketCandidate) -> (bool, bool, bool) {
    let game_owned = candidate
        .owners
        .iter()
        .any(|owner| owner.command == "SlayTheSpire2");
    let bridge_like_path = unix_socket_candidate_has_bridge_like_path(candidate);
    let has_known_owner = !candidate.owners.is_empty();
    (game_owned, bridge_like_path, has_known_owner)
}

#[cfg(target_os = "linux")]
fn unix_socket_candidate_has_bridge_like_path(candidate: &UnixSocketCandidate) -> bool {
    let path = candidate.path.to_ascii_lowercase();
    path.contains("spirectl") || path.contains(".sts2")
}

#[cfg(target_os = "linux")]
fn unix_socket_candidate_owner_note(candidate: &UnixSocketCandidate) -> String {
    if candidate.owners.is_empty() {
        return String::new();
    }

    let owners = candidate
        .owners
        .iter()
        .take(3)
        .map(|owner| format!("{} pid {}", owner.command, owner.pid))
        .collect::<Vec<_>>()
        .join(", ");
    format!(", with owner process {owners}")
}

#[cfg(target_os = "linux")]
fn unix_socket_owners_by_inode() -> BTreeMap<String, Vec<UnixSocketOwner>> {
    let mut owners_by_inode = BTreeMap::<String, Vec<UnixSocketOwner>>::new();
    let Ok(proc_entries) = fs::read_dir("/proc") else {
        return owners_by_inode;
    };

    for proc_entry in proc_entries.flatten() {
        let pid = proc_entry.file_name().to_string_lossy().into_owned();
        if !pid.chars().all(|ch| ch.is_ascii_digit()) {
            continue;
        }
        let proc_path = proc_entry.path();
        let command = fs::read_to_string(proc_path.join("comm"))
            .ok()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| "unknown".to_string());
        let Ok(fd_entries) = fs::read_dir(proc_path.join("fd")) else {
            continue;
        };

        for fd_entry in fd_entries.flatten() {
            let Ok(target) = fs::read_link(fd_entry.path()) else {
                continue;
            };
            let target = target.to_string_lossy();
            let Some(inode) = parse_proc_fd_socket_inode(&target) else {
                continue;
            };
            let owners = owners_by_inode.entry(inode.to_string()).or_default();
            if owners
                .iter()
                .any(|owner| owner.pid == pid && owner.command == command)
            {
                continue;
            }
            owners.push(UnixSocketOwner {
                pid: pid.clone(),
                command: command.clone(),
            });
        }
    }

    owners_by_inode
}

#[cfg(target_os = "linux")]
fn parse_proc_fd_socket_inode(target: &str) -> Option<&str> {
    target
        .strip_prefix("socket:[")
        .and_then(|rest| rest.strip_suffix(']'))
}

#[cfg(unix)]
fn live_ipc_connection_error(socket_path: &str, source: &str) -> proto::BridgeError {
    if let Some(error) = recent_bootstrap_failure_error(socket_path) {
        return error;
    }

    let mut details = vec![detail(
        "endpoint",
        socket_path,
        &format!(
            "The socket path exists but the live bridge did not accept the connection. Confirm STS2 is still running and inspect recent [spirectl] Godot logs. Transport error: {source}"
        ),
    )];
    details.extend(likely_live_ipc_socket_details(socket_path));

    error(
        proto::BridgeErrorCode::IpcConnectionFailed,
        "Failed to connect to the live IPC bridge host.",
        &details,
    )
}

fn live_transport_connection_error(endpoint: &str, source: &str) -> proto::BridgeError {
    error(
        proto::BridgeErrorCode::TransportConnectionFailed,
        "Failed to connect to the configured live bridge endpoint.",
        &[detail(
            "endpoint",
            endpoint,
            &format!(
                "Confirm the live bridge host is listening on the selected local endpoint. Transport error: {source}"
            ),
        )],
    )
}

fn live_rpc_timeout_error(
    endpoint: &str,
    method: &str,
    rpc_timeout_ms: Option<u64>,
) -> proto::BridgeError {
    let timeout_value = rpc_timeout_ms
        .map(|timeout| timeout.to_string())
        .unwrap_or_else(|| "unknown".to_string());
    error(
        proto::BridgeErrorCode::BridgeRpcTimeout,
        "Timed out waiting for the live bridge RPC to complete.",
        &[
            detail(
                "endpoint",
                endpoint,
                "The bridge accepted the local transport connection.",
            ),
            detail(
                "method",
                method,
                "The request was sent, but no complete response arrived before the RPC timeout.",
            ),
            detail(
                "timeoutMs",
                &timeout_value,
                "Increase --rpc-timeout-ms only after checking recent [spirectl] game logs for a stuck bridge handler.",
            ),
        ],
    )
}

#[cfg(unix)]
fn live_ipc_missing_socket_error(socket_path: &str) -> proto::BridgeError {
    if let Some(error) = recent_bootstrap_failure_error(socket_path) {
        return error;
    }

    let mut details = vec![detail(
        "endpoint",
        socket_path,
        "Launch STS2 through `sts2 game launch` or confirm SPIRECTL_BRIDGE_SOCKET_PATH and transport.ipcPath point to the same Unix socket path.",
    )];
    details.extend(likely_live_ipc_socket_details(socket_path));

    error(
        proto::BridgeErrorCode::IpcSocketMissing,
        "The configured live IPC socket does not exist.",
        &details,
    )
}

/// Windows counterpart of [`live_ipc_missing_socket_error`]: the named pipe the
/// bridge would serve is not published, so nothing is listening.
#[cfg(windows)]
fn live_ipc_missing_pipe_error(pipe_name: &str) -> proto::BridgeError {
    if let Some(error) = recent_bootstrap_failure_error(pipe_name) {
        return error;
    }

    error(
        proto::BridgeErrorCode::IpcSocketMissing,
        "The configured live IPC named pipe is not being served.",
        &[detail(
            "endpoint",
            pipe_name,
            "Launch STS2 through `sts2 game launch` or confirm SPIRECTL_BRIDGE_PIPE_NAME and transport.pipeName name the same pipe.",
        )],
    )
}

fn current_unix_observed_at_utc() -> String {
    let elapsed = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}.{:03}Z", elapsed.as_secs(), elapsed.subsec_millis())
}

#[derive(Debug, Clone)]
#[allow(dead_code)]
struct UnavailableBridgeClient {
    transport_kind: proto::TransportKind,
    endpoint: String,
    note: String,
}

#[allow(dead_code)]
impl UnavailableBridgeClient {
    fn new(
        transport_kind: proto::TransportKind,
        endpoint: String,
        note: impl Into<String>,
    ) -> Self {
        Self {
            transport_kind,
            endpoint,
            note: note.into(),
        }
    }

    fn transport_error(&self) -> proto::BridgeError {
        transport_unavailable_error(self.transport_kind, &self.endpoint, &self.note)
    }
}

mod stub_service;
pub use stub_service::{StubBridgeGrpcService, StubBridgeService};

#[cfg(test)]
mod instance_socket_path_tests {
    use super::*;

    /// A derived path long enough to be unbindable on any platform here.
    fn overlong_derived(name: &str) -> String {
        format!("/tmp/{}/.sts2/ipc/{name}.sock", "d".repeat(160))
    }

    #[test]
    fn a_short_derived_path_is_used_unchanged() {
        // The crate directory is short, so the ordinary derivation stands and
        // the documented `.sts2/ipc/<name>.sock` shape is preserved.
        let resolved = resolve_instance_ipc_path("alpha");
        assert!(!resolved.shortened);
        assert_eq!(resolved.path, resolved.derived);
        assert!(resolved.path.ends_with("/.sts2/ipc/alpha.sock"));
    }

    #[test]
    fn an_overlong_derived_path_is_shortened_to_something_bindable() {
        let derived = overlong_derived("alpha");
        assert!(!crate::host_paths::unix_socket_path_fits(&derived));

        let shortened = shortened_instance_ipc_path("alpha", &derived);
        assert!(
            crate::host_paths::unix_socket_path_fits(&shortened),
            "fallback must fit: {shortened}"
        );
        assert!(shortened.ends_with(".sock"));
        assert!(shortened.contains("alpha"));
    }

    #[test]
    fn the_fallback_is_stable_across_calls() {
        // The whole point of deriving rather than storing: a later invocation
        // must land on the same socket without any shared runtime state.
        let derived = overlong_derived("alpha");
        assert_eq!(
            shortened_instance_ipc_path("alpha", &derived),
            shortened_instance_ipc_path("alpha", &derived)
        );
    }

    #[test]
    fn instances_of_the_same_name_in_different_directories_stay_apart() {
        let here = shortened_instance_ipc_path("alpha", &overlong_derived("alpha"));
        let elsewhere = shortened_instance_ipc_path(
            "alpha",
            &format!("/tmp/{}/.sts2/ipc/alpha.sock", "e".repeat(160)),
        );
        assert_ne!(
            here, elsewhere,
            "the digest is what keeps two same-named instances from colliding"
        );
    }

    #[test]
    fn a_name_too_long_for_the_runtime_dir_is_trimmed_not_abandoned() {
        let name = "n".repeat(crate::instance::MAX_NAME_LEN);
        let shortened = shortened_instance_ipc_path(&name, &overlong_derived(&name));
        assert!(
            crate::host_paths::unix_socket_path_fits(&shortened),
            "even a maximal instance name must produce a bindable path: {shortened}"
        );
        assert!(shortened.ends_with(".sock"));
    }
}
