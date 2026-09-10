struct MockConsoleOutput {
    success: bool,
    text: String,
}

fn canonical_console_line(command: &str, args: &[String]) -> String {
    if args.is_empty() {
        command.to_string()
    } else {
        format!("{command} {}", args.join(" "))
    }
}

fn mock_console_output(command: &str, args: &[String], line: &str) -> MockConsoleOutput {
    let normalized = command.trim().to_ascii_lowercase();
    match (normalized.as_str(), args) {
        ("help", []) => MockConsoleOutput {
            success: true,
            text: "Usage: help <cmd>\nAvailable commands include draw, die, and help <cmd>.\n"
                .to_string(),
        },
        ("help", [arg]) if arg.eq_ignore_ascii_case("draw") => MockConsoleOutput {
            success: true,
            text: "draw <count>\nDraw cards into the current player's hand.\n".to_string(),
        },
        ("draw", [count]) if count == "3" => MockConsoleOutput {
            success: true,
            text: "Drew 3 card(s).\n".to_string(),
        },
        ("die", []) => MockConsoleOutput {
            success: true,
            text: "Applied die command.\n".to_string(),
        },
        _ => MockConsoleOutput {
            success: false,
            text: format!(
                "The command '{}' does not exist.\nYou can use the 'help' to get a list of all possible commands.\n",
                line
            ),
        },
    }
}

fn output_lines(output: &str) -> Vec<String> {
    output
        .replace("\r\n", "\n")
        .split('\n')
        .filter(|line| !line.is_empty())
        .map(|line| line.trim_end_matches('\r').to_string())
        .collect()
}

fn mock_screen_type(scenario: MockScenario) -> &'static str {
    match scenario {
        MockScenario::MainMenu => "main-menu",
        MockScenario::Combat => "combat",
        MockScenario::Map => MAP_SCREEN_ID,
        MockScenario::EventRoom => EVENT_ROOM_SCREEN_ID,
        MockScenario::TreasureRoom => TREASURE_ROOM_SCREEN_ID,
        MockScenario::RelicSelection => RELIC_SELECTION_SCREEN_ID,
        MockScenario::RestSite => REST_SITE_SCREEN_ID,
        MockScenario::Shop => SHOP_SCREEN_ID,
        MockScenario::FakeMerchantPreOpen => EVENT_ROOM_SCREEN_ID,
        MockScenario::CrystalSphere | MockScenario::CrystalSphereFinished => {
            CRYSTAL_SPHERE_SCREEN_ID
        }
        MockScenario::Rewards => REWARDS_SCREEN_ID,
        MockScenario::CardSelection => CARD_REWARD_SELECTION_SCREEN_ID,
        MockScenario::SimpleCardSelection => SIMPLE_CARD_SELECTION_SCREEN_ID,
        MockScenario::DeckCardSelection => DECK_CARD_SELECTION_SCREEN_ID,
        MockScenario::BundleSelection => BUNDLE_SELECTION_SCREEN_ID,
        MockScenario::CardOverlay => "card-overlay",
        MockScenario::PassiveCardOverlay => SHOP_SCREEN_ID,
        MockScenario::Lobby | MockScenario::LobbyReady => START_RUN_LOBBY_SCREEN_ID,
    }
}
