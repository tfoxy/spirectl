use serde_json::Value;
use sts2::{Cli, RenderedCommand, run_cli};

fn run(args: &[&str]) -> RenderedCommand {
    let cli = Cli::parse_from(args.iter().copied());
    run_cli(cli).expect("command should succeed")
}

#[test]
fn state_schema_documents_current_state_contract() {
    let response = run(&["sts2", "--json", "inspect", "state-schema"]);
    let payload: Value = serde_json::from_str(&response.stdout).expect("json");

    assert_eq!(payload["schemaVersion"], "spirectl.state-schema/v0");
    assert_eq!(payload["primaryCommand"], "state");
    // The current state contract dropped the legacy `availableActions` top-level field from the
    // default state in favour of the state-native `actions[]` contract (see commit e2aa8028
    // "Promote state to canonical state"). It must no longer surface as a top-level field.
    assert!(
        !payload["topLevelFields"]
            .as_array()
            .expect("topLevelFields")
            .iter()
            .any(|field| field == "availableActions")
    );
    assert_eq!(payload["actionContract"]["path"], "actions[]");
    assert_eq!(
        payload["actionContract"]["schemaVersion"],
        "spirectl.state-actions/v0"
    );
}
