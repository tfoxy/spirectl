// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]
#![allow(
    clippy::result_large_err,
    reason = "Lifecycle bridge tests exercise the public protobuf BridgeError return shape by value."
)]

mod ipc_bridge_support;

include!("game_lifecycle/game_lifecycle_support.rs");
include!("game_lifecycle/game_lifecycle_bridge_health.rs");
include!("game_lifecycle/game_lifecycle_close_launch.rs");
include!("game_lifecycle/game_lifecycle_ipc_deploy.rs");
include!("game_lifecycle/game_lifecycle_deploy_install_repair.rs");
