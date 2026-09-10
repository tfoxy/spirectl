#![recursion_limit = "256"]
#![allow(
    clippy::needless_update,
    reason = "CLI command handlers build generated protobuf structs with default updates to tolerate additive contract fields."
)]

mod assets;
mod automation_service;
pub mod bridge;
mod combat_preview;
mod completion;
mod debugging;
mod dev_probes;
mod diagnostics;
mod dotnet_helper;
mod fixtures;
mod host_paths;
mod hot_reload;
pub mod install_paths;
mod latest_fixture;
pub mod lifecycle;
pub mod live_bridge;
mod map_drawings;
mod models;
mod path_display;
pub mod project;
pub mod project_scaffold;
pub mod query;
mod recorded_fixture;
mod reference;
mod reference_topics;
mod scenario_checkpoint;
mod skill;
mod snapshot_regression;
pub mod steam_discovery;
mod test_runner;
pub mod toolchain;
mod visual_preflight;
mod visual_validation;

use assets::{
    AssetCommand, AssetSubcommand, execute_asset_catalog, execute_asset_explain,
    execute_asset_extract, execute_asset_resolve,
};
use clap::{ArgGroup, Args, Parser, Subcommand, ValueEnum};
use combat_preview::execute_combat_preview_json;
use completion::{CompletionCommand, handle_completion};
use debugging::{
    execute_breakpoint_add_json, execute_breakpoint_list_json, execute_breakpoint_remove_json,
    execute_debug_events_json, execute_debug_pause_json, execute_debug_resume_json,
    execute_debug_session_end_json, execute_debug_session_start_json,
    execute_debug_session_status_json, execute_debug_status_json, execute_debug_step_json,
    execute_debug_wait_json,
};
use dev_probes::{
    FetchProbe, HttpProbe, HttpWaitProbe, ProbeQuery, WebsocketProbe, execute_fetch_probe_json,
    execute_http_probe_json, execute_http_wait_probe_json, execute_websocket_probe_json,
};
use diagnostics::{execute_diagnostics_json, execute_log_health_json};
use hot_reload::{
    execute_mod_reload_json, execute_mod_reload_status_capture_json, execute_mod_reload_status_json,
};
use install_paths::{
    PathOverrides, ResolvedCodeSearchPaths, ResolvedSceneSearchPaths,
    derive_assemblies_dir_from_game_path, detect_game_path, resolve_code_search_paths,
    resolve_scene_search_paths,
};
use latest_fixture::{execute_load_latest_fixture_json, record_latest_fixture};
use live_bridge::{LiveBridgeOverrides, detect_report};
use map_drawings::execute_map_drawings_json;
use models::execute_models_json;
use project::ProjectCommand;
use query::{Evaluation, PathResolution, Predicate, QueryError};
use recorded_fixture::{
    execute_recorded_fixture_clear_json, execute_recorded_fixture_record_json,
    execute_recorded_fixture_resume_json, execute_recorded_fixture_status_json,
};
use reference::execute_reference_json;
use reference_topics::reference_topics_json;
use scenario_checkpoint::{execute_scenario_export_json, execute_scenario_load_json};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use serde_yaml::Value as YamlValue;
use skill::{SkillCommand, SkillSubcommand, execute_skill_install_json};
use snapshot_regression::{execute_snapshot_compare_json, execute_snapshot_export_json};
use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fmt::{Display, Formatter};
use std::fs;
use std::path::{Path, PathBuf};
use std::thread;
use toolchain::ToolchainCommand;
use visual_preflight::execute_visual_preflight_json;
use visual_validation::{
    ComparisonMode, ViewportPreset, compare_screenshots, load_viewport_presets,
    resolve_viewport_selection, viewport_presets_json,
};

use bridge::{
    RuntimeBridgeClient, attachment_state_name, bridge_error_exit_code, bridge_error_payload,
    console_command_json, default_ipc_path, default_pipe_name, default_tcp_address, handshake_json,
    inspect_actions_environment_blocked_json, inspect_actions_json, inspect_actions_static_json,
    inspect_actions_unavailable_json, logs_json, transport_kind_name,
};

mod cli_args;
mod commands;
mod config;
mod config_resolve;
mod context;
mod error;
mod inspect;
mod instance;
mod models_ext;
mod output;
mod progress;
mod state;
mod state_actions;

pub use cli_args::*;
pub use commands::{cli_uses_streaming, run_cli, run_cli_streaming};
pub use config::*;
pub use error::AppError;
pub use output::RenderedCommand;

/// Bridge source-freshness payload, exposed so integration tests can compare the cheap sentinel
/// stat against the authoritative full walk without going through a bridge handshake.
#[doc(hidden)]
pub fn bridge_source_freshness_json_for_tests(full_scan: bool) -> serde_json::Value {
    commands::bridge_source_freshness_json(full_scan)
}

/// Normalizes an authored fixture file into the canonical wire JSON the CLI
/// sends to the bridge (defaults applied, recipe `screen` hint derived).
#[doc(hidden)]
pub fn canonical_fixture_json(path: &std::path::Path) -> Result<String, AppError> {
    fixtures::prepare_fixture(path, None).map(|prepared| prepared.fixture_json)
}

pub(crate) use commands::*;
#[allow(unused_imports)]
pub(crate) use config::*;
pub(crate) use context::*;
pub(crate) use inspect::*;
pub(crate) use output::*;
pub(crate) use state::*;

#[cfg(test)]
mod tests;
