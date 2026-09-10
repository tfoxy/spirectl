use std::collections::BTreeMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};

use crate::debugging::execute_debug_events_json;
use crate::dev_probes::{
    FetchProbe, HttpProbe, HttpWaitProbe, ProbeQuery, WebsocketProbe, execute_fetch_probe_json,
    execute_http_probe_json, execute_http_wait_probe_json, execute_websocket_probe_json,
};
use crate::diagnostics::{execute_diagnostics_json, execute_log_health_json};
use crate::hot_reload::{execute_mod_reload_json, execute_mod_reload_status_json};
use crate::lifecycle::{
    execute_game_attach_json, execute_game_deploy_json, execute_game_install_bridge_json,
    execute_game_launch_with_args_json,
};
use crate::project::{HookInvocationContext, execute_project_hook_run_json};
use crate::{
    ActSubcommand, AppContext, AppError, BundleActionArgs, CardActionArgs, ChooseArgs,
    ConfirmSelectionArgs, ConsoleArgs, CrystalSphereControlActionArgs,
    DEFAULT_BRIDGE_RPC_TIMEOUT_MS, DebugEventsArgs, DeployArgs, DiagnosticsArgs,
    EventOptionActionArgs, FailureArtifactsMode, GameLaunchArgs, LifecycleWaitArgs,
    LoadFixtureArgs, LogHealthArgs, LogLevelArg, LogsArgs, ModReloadCommand, ModReloadStatusArgs,
    PerspectiveScopeArg, PlayCardArgs, PlayerScopedActionArgs, Predicate, RenderedCommand,
    RestSiteOptionActionArgs, RewardActionArgs, RuntimeContextOwned, ScenarioLoadArgs,
    ScreenshotArgs, ScreenshotDiffArgs, SelectCharacterArgs, SelectMapNodeArgs, ShopItemActionArgs,
    SmithActionArgs, SnapshotCompareArgs, StateViewArg, TakeRelicActionArgs,
    TestProfileCleanupConfig, TestProfileConfig, TestRunArgs, TestStressArgs, TransportKind,
    UsePotionArgs, context_with_transport_rpc_timeout, execute_action_json, execute_assert_json,
    execute_console_json, execute_game_info_json, execute_load_fixture_json_from,
    execute_logs_json, execute_scenario_load_json, execute_screenshot_diff_json,
    execute_screenshot_json, execute_wait_for_json, resolve_config_relative_path,
    snapshot_regression::execute_snapshot_compare_json,
};

include!("test_runner_impl/test_runner_profile_run.rs");
include!("test_runner_impl/test_runner_render_artifacts.rs");
include!("test_runner_impl/test_runner_discovery_scenario.rs");
include!("test_runner_impl/test_runner_step_parse.rs");
include!("test_runner_impl/test_runner_types.rs");
#[cfg(test)]
#[path = "test_runner_tests.rs"]
mod tests;
