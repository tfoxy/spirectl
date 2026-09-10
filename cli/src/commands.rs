#[path = "commands_combat_events_watch.rs"]
mod combat_events_watch;
#[path = "commands/commands_dev_game_health.rs"]
mod dev_game_health;
#[path = "commands/commands_fixtures_state_helpers.rs"]
mod fixtures_state_helpers;
#[path = "commands/commands_logs_screenshots_scene.rs"]
mod logs_screenshots_scene;
#[path = "commands/commands_project_code_assets.rs"]
mod project_code_assets;
#[path = "commands_runtime_scene_text.rs"]
mod runtime_scene_text;
#[path = "commands_state_watch.rs"]
mod state_watch;
#[path = "commands/commands_test_inspect_actions.rs"]
mod test_inspect_actions;
#[path = "commands/commands_usage_dispatch.rs"]
mod usage_dispatch;

use combat_events_watch::stream_combat_events_watch_json;
pub(crate) use dev_game_health::*;
pub(crate) use fixtures_state_helpers::*;
pub(crate) use logs_screenshots_scene::*;
pub(crate) use project_code_assets::*;
use runtime_scene_text::{
    attach_runtime_scene_text_screenshot_ink, measure_runtime_scene_text_ink,
};
use state_watch::stream_state_watch_json;
pub(crate) use test_inspect_actions::*;
pub(crate) use usage_dispatch::*;
pub use usage_dispatch::{cli_uses_streaming, run_cli, run_cli_streaming};
