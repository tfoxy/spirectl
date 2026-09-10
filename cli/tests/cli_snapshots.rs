include!("cli_snapshots/support.rs");

#[path = "cli_snapshots/action_contract_snapshots.rs"]
mod action_contract_snapshots;
#[path = "cli_snapshots/command_snapshots.rs"]
mod command_snapshots;
#[path = "cli_snapshots/inspect_snapshots.rs"]
mod inspect_snapshots;
#[path = "cli_snapshots/lifecycle_dev_snapshots.rs"]
mod lifecycle_dev_snapshots;
#[path = "cli_snapshots/state_snapshots.rs"]
mod state_snapshots;
