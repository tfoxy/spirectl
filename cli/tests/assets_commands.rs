// These suites drive the CLI against a Unix-domain-socket bridge stub (and
// shell out to `bash` fixtures), so they only build and run on Unix hosts. The
// Windows build of the CLI is covered by the crate's unit tests plus a
// `--target x86_64-pc-windows-gnu` check; see docs/testing.md.
#![cfg(unix)]

mod ipc_bridge_support;

include!("assets_commands/assets_commands_support.rs");
include!("assets_commands/assets_commands_args_batch.rs");
include!("assets_commands/assets_commands_live.rs");
include!("assets_commands/assets_commands_offline.rs");
