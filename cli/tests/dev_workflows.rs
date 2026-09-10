// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]
#![allow(dead_code)]

mod ipc_bridge_support;

include!("dev_workflows/support.rs");
include!("dev_workflows/scene_support_debug.rs");
include!("dev_workflows/scene_support_fixtures.rs");
include!("dev_workflows/scene_support_runtime.rs");
include!("dev_workflows/scene_support_state.rs");

#[path = "dev_workflows/actions.rs"]
mod actions;
#[path = "dev_workflows/assertions_watch.rs"]
mod assertions_watch;
#[path = "dev_workflows/dev_probes.rs"]
mod dev_probes;
#[path = "dev_workflows/diagnostics_screenshots.rs"]
mod diagnostics_screenshots;
#[path = "dev_workflows/fixture_loading.rs"]
mod fixture_loading;
#[path = "dev_workflows/scene_debug.rs"]
mod scene_debug;
#[path = "dev_workflows/visual_preflight.rs"]
mod visual_preflight;
