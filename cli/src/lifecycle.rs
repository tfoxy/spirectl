use crate::game_build::{self, BridgeGameBuildStamp};
use crate::live_bridge::{
    DEPLOYED_MOD_DIR_NAME, LiveBridgeEndpoint, ResolvedLiveBridgeLayout, resolve_layout,
};
use crate::{
    AppConfig, AppContext, AppError, DeployArgs, GameInstallBridgeArgs, GameLaunchArgs,
    GameModsSettingsArgs, LifecycleWaitArgs, RuntimeContextOwned, StateArgs, TransportKind,
    WaitForTransitionsArgs, bridge, execute_game_info_json, execute_state_json,
    execute_wait_for_transitions_json, maybe_cache_resolved_local_paths,
};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsStr;
use std::fs;
use std::io::{Cursor, Write};
use std::net::{TcpStream, ToSocketAddrs};
#[cfg(unix)]
use std::os::unix::net::UnixStream;
#[cfg(unix)]
use std::os::unix::process::CommandExt;
#[cfg(unix)]
use std::os::unix::process::ExitStatusExt;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, ExitStatus, Stdio};

include!("lifecycle/lifecycle_types_attach_close.rs");
include!("lifecycle/lifecycle_launch.rs");
include!("lifecycle/lifecycle_deploy_wait.rs");
include!("lifecycle/lifecycle_mods_settings.rs");
include!("lifecycle/lifecycle_process_stop.rs");
include!("lifecycle/lifecycle_process_windows.rs");
#[cfg(test)]
#[path = "lifecycle_tests.rs"]
mod tests;
