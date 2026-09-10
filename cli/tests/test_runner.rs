// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]
#![allow(dead_code)]

mod ipc_bridge_support;

include!("test_runner/support.rs");
include!("test_runner/support_more.rs");
include!("test_runner/support_tail.rs");

#[path = "test_runner/artifacts_and_scenarios.rs"]
mod artifacts_and_scenarios;
#[path = "test_runner/failures_and_actions.rs"]
mod failures_and_actions;
#[path = "test_runner/profiles.rs"]
mod profiles;
#[path = "test_runner/remote_artifacts.rs"]
mod remote_artifacts;
#[path = "test_runner/scenario_execution.rs"]
mod scenario_execution;
#[path = "test_runner/streaming_and_stress.rs"]
mod streaming_and_stress;
