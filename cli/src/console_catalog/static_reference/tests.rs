use super::*;
use tempfile::TempDir;

fn write(root: &Path, name: &str, source: &str) {
    fs::write(root.join(format!("{name}.cs")), source).unwrap();
}

fn command(root: &Path, class: &str, name: &str) {
    write(
        root,
        &format!("{COMMAND_NAMESPACE}{class}ConsoleCmd"),
        &format!(
            "public class {class}ConsoleCmd {{ public string CmdName => \"{name}\"; public string Args => \"<item>\"; public string Description => \"Synthetic command\"; }}"
        ),
    );
}

fn fixture() -> TempDir {
    let dir = tempfile::tempdir().unwrap();
    for name in [
        "fight",
        "event",
        "ancient",
        "card",
        "remove_card",
        "relic",
        "potion",
        "power",
        "enchant",
        "afflict",
        "room",
        "art",
        "open",
        "unlock",
        "draw",
    ] {
        command(dir.path(), &format!("Synthetic{name}"), name);
    }
    for (family, class, base) in [
        ("Encounters", "PaperKnightsElite", "EncounterModel"),
        ("Encounters", "PaperKnight2Elite", "EncounterModel"),
        ("Encounters.Mocks", "PaperMockFight", "EncounterModel"),
        ("Events", "PaperFestival", "EventModel"),
        ("Events", "PaperSage", "AncientEventModel"),
        ("Events.Mocks", "PaperMockEvent", "EventModel"),
        ("Events.Mocks", "PaperMockAncient", "AncientEventModel"),
        ("Cards", "PaperBlade", "CardModel"),
        ("Cards.Mocks", "PaperMockCard", "CardModel"),
        ("Relics", "PaperCrown", "RelicModel"),
        ("Potions", "PaperTonic", "PotionModel"),
        ("Powers", "PaperMight", "PowerModel"),
        ("Enchantments", "PaperGleam", "EnchantmentModel"),
        ("Afflictions", "PaperTear", "AfflictionModel"),
        ("Powers.Mocks", "PaperDebug", "PowerModel"),
    ] {
        write(
            dir.path(),
            &format!("{MODEL_NAMESPACE}{family}.{class}"),
            &format!("public sealed class {class} : {base} {{ }}"),
        );
    }
    write(
        dir.path(),
        &format!("{MODEL_NAMESPACE}Cards.BasePaper"),
        "public abstract class BasePaper : CardModel { }",
    );
    write(
        dir.path(),
        "MegaCrit.Sts2.Core.Rooms.RoomType",
        "public enum RoomType { Unassigned, PaperHall = 3, /* ignored */ PaperVault }",
    );
    write(
        dir.path(),
        "MegaCrit.Sts2.Core.Entities.Cards.PileType",
        "public enum PileType { PaperHand, // ignored\n PaperDeck = 2, }",
    );
    dir
}

fn completion(root: &Path, command: &str, position: usize, prefix: &str) -> Value {
    complete(
        root,
        &bridge::proto::ConsoleCompletionRequest {
            request_id: "synthetic".into(),
            command: command.into(),
            args: vec![String::new(); position],
            prefix: prefix.into(),
        },
    )
    .unwrap()
}

#[test]
fn console_static_catalog_extracts_literals_flags_and_skips_noncommands() {
    let dir = tempfile::tempdir().unwrap();
    command(dir.path(), "SyntheticZebra", "zebra");
    command(dir.path(), "AbstractSynthetic", "excluded");
    command(dir.path(), "Subtypes.Synthetic", "excluded-too");
    write(
        dir.path(),
        &format!("{COMMAND_NAMESPACE}SyntheticAlphaConsoleCmd"),
        r#"
        public class SyntheticAlphaConsoleCmd {
            public string CmdName => "alpha";
            public string Args => "<text>";
            public string Description => "A \"quoted\" line\nwith text";
            public bool IsNetworked => true;
            public bool DebugOnly => false;
        }
    "#,
    );
    let value = catalog(dir.path(), "synthetic-catalog").unwrap();
    assert_eq!(value["count"], 2);
    assert_eq!(value["commands"][0]["name"], "alpha");
    assert_eq!(
        value["commands"][0]["description"],
        "A \"quoted\" line\nwith text"
    );
    assert_eq!(value["commands"][0]["networked"], true);
    assert_eq!(value["commands"][0]["debugOnly"], false);
    assert_eq!(value["commands"][1]["debugOnly"], true);
    assert_eq!(value["source"], "static-reference");
    assert_eq!(value["complete"], false);
    assert_eq!(value["notices"][0]["code"], "static-fallback-incomplete");
    assert_eq!(value["notices"][0]["provisional"], true);
}

#[test]
fn console_static_slugify_handles_digits_and_consecutive_capitals() {
    assert_eq!(slugify("KnightsElite"), "KNIGHTS_ELITE");
    assert_eq!(slugify("Knight2Elite"), "KNIGHT2_ELITE");
    assert_eq!(slugify("ABCTest"), "A_B_C_TEST");
}

#[test]
fn console_static_resolves_every_model_family_and_enum() {
    let dir = fixture();
    for (command, position, candidate) in [
        ("fight", 0, "PAPER_KNIGHTS_ELITE"),
        ("event", 0, "PAPER_FESTIVAL"),
        ("event", 0, "PAPER_SAGE"),
        ("ancient", 0, "PAPER_SAGE"),
        ("card", 0, "PAPER_BLADE"),
        ("remove_card", 0, "PAPER_BLADE"),
        ("relic", 1, "PAPER_CROWN"),
        ("potion", 0, "PAPER_TONIC"),
        ("power", 0, "PAPER_MIGHT"),
        ("power", 0, "PAPER_DEBUG"),
        ("enchant", 0, "PAPER_GLEAM"),
        ("afflict", 0, "PAPER_TEAR"),
        ("room", 0, "PaperHall"),
        ("card", 1, "PaperHand"),
        ("art", 0, "enchantment"),
        ("open", 0, "loc-override"),
    ] {
        let value = completion(dir.path(), command, position, "");
        assert!(
            value["candidates"]
                .as_array()
                .unwrap()
                .contains(&json!(candidate)),
            "{command}: {value}"
        );
        assert_eq!(value["knownCommand"], true);
        assert_eq!(value["position"], position);
        assert_eq!(value["source"], "static-reference");
        assert_eq!(value["complete"], false);
    }
    assert_eq!(
        completion(dir.path(), "ancient", 0, "")["candidates"],
        json!(["PAPER_SAGE"])
    );
    assert_eq!(
        completion(dir.path(), "card", 0, "")["candidates"],
        json!(["PAPER_BLADE"])
    );
    assert_eq!(
        completion(dir.path(), "room", 0, "")["candidates"],
        json!(["PaperHall", "PaperVault"])
    );
}

#[test]
fn console_static_filters_prefix_and_contains_and_completes_command_names() {
    let dir = fixture();
    assert_eq!(
        completion(dir.path(), "fight", 0, "paper_knights")["candidates"],
        json!(["PAPER_KNIGHTS_ELITE"])
    );
    assert_eq!(completion(dir.path(), "fight", 0, "elite")["count"], 0);
    assert_eq!(
        completion(dir.path(), "relic", 0, "crown")["candidates"],
        json!(["PAPER_CROWN"])
    );
    assert_eq!(
        completion(dir.path(), "relic", 0, "r")["candidates"],
        json!(["remove"])
    );
    assert_eq!(
        completion(dir.path(), "relic", 0, "")["completionType"],
        "subcommand"
    );
    assert_eq!(
        completion(dir.path(), "unlock", 0, "sion")["candidates"],
        json!(["ascensions"])
    );
    let value = completion(dir.path(), "FI", 0, "");
    assert_eq!(value["knownCommand"], false);
    assert_eq!(value["completionType"], "command");
    assert_eq!(value["commonPrefix"], "fight ");
}

#[test]
fn console_static_common_prefix_is_a_console_replacement_line() {
    let dir = fixture();
    for (command, prefix, expected) in [
        ("fight", "paper_knights", "fight PAPER_KNIGHTS_ELITE "),
        ("fight", "paper", "fight PAPER_KNIGHT"),
        ("fight", "missing", ""),
        ("art", "", ""),
        ("FI", "", "fight "),
        ("missing", "", ""),
    ] {
        assert_eq!(
            completion(dir.path(), command, 0, prefix)["commonPrefix"],
            expected,
            "{command} {prefix}"
        );
    }
    let value = complete(
        dir.path(),
        &bridge::proto::ConsoleCompletionRequest {
            command: "card".into(),
            args: vec!["PAPER_BLADE".into()],
            prefix: "paperh".into(),
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(value["commonPrefix"], "card PAPER_BLADE PaperHand ");
}

#[test]
fn console_static_common_prefix_matches_case_insensitively() {
    let dir = fixture();
    write(
        dir.path(),
        "MegaCrit.Sts2.Core.Entities.Cards.PileType",
        "public enum PileType { PaperHand, paperHeap }",
    );
    let value = completion(dir.path(), "card", 1, "paperh");
    assert_eq!(value["candidates"], json!(["PaperHand", "paperHeap"]));
    assert_eq!(value["commonPrefix"], "card  PaperH");

    command(dir.path(), "SyntheticMatch", "paperMatch");
    command(dir.path(), "SyntheticMore", "papermore");
    let value = completion(dir.path(), "PAPER", 0, "");
    assert_eq!(value["candidates"], json!(["paperMatch", "papermore"]));
    assert_eq!(value["commonPrefix"], "paperM");
}

#[test]
fn console_static_excludes_mock_models_only_for_registered_content_families() {
    let dir = fixture();
    for command in ["fight", "event", "ancient", "card", "remove_card"] {
        let value = completion(dir.path(), command, 0, "paper_mock");
        assert_eq!(value["candidates"], json!([]), "{command}: {value}");
    }
    for (command, family, base) in [
        ("potion", "Potions", "PotionModel"),
        ("power", "Powers", "PowerModel"),
        ("enchant", "Enchantments", "EnchantmentModel"),
        ("afflict", "Afflictions", "AfflictionModel"),
        ("relic", "Relics", "RelicModel"),
    ] {
        write(
            dir.path(),
            &format!("{MODEL_NAMESPACE}{family}.Mocks.PaperDebug"),
            &format!("public sealed class PaperDebug : {base} {{ }}"),
        );
        let value = completion(dir.path(), command, 0, "paper_debug");
        assert_eq!(value["candidates"], json!(["PAPER_DEBUG"]), "{command}");
    }
}

#[test]
fn console_static_unsupported_command_positions_are_marked() {
    let dir = fixture();
    for (command, position) in [("draw", 0), ("ancient", 1), ("fight", 2)] {
        let value = completion(dir.path(), command, position, "");
        assert_eq!(value["candidates"], json!([]));
        assert_eq!(value["notices"][1]["code"], "static-fallback-unsupported");
        assert_eq!(value["preceding"], json!(vec![""; position]));
    }
}

fn error(code: bridge::proto::BridgeErrorCode) -> bridge::proto::BridgeError {
    bridge::proto::BridgeError {
        code: code as i32,
        message: "synthetic error".into(),
        ..Default::default()
    }
}

#[test]
fn console_static_fallback_only_runs_for_unreachable_transport_not_bridge_errors() {
    use bridge::proto::BridgeErrorCode::*;
    let dir = fixture();
    for code in [
        IpcSocketMissing,
        IpcConnectionFailed,
        TransportConnectionFailed,
        TransportUnavailable,
    ] {
        let result = resolve_discovery::<()>(
            Err(error(code)),
            dir.path(),
            |_| unreachable!(),
            |root| catalog(root, "test"),
        )
        .unwrap();
        assert_eq!(result["source"], "static-reference");
        let result = resolve_discovery::<()>(
            Ok(Err(error(code))),
            dir.path(),
            |_| unreachable!(),
            |_| panic!("must not mask bridge errors"),
        );
        assert!(result.is_err());
    }
    for code in [
        RuntimeFailure,
        NotImplemented,
        BridgeRpcTimeout,
        TransportMisconfigured,
    ] {
        let result = resolve_discovery::<()>(
            Err(error(code)),
            dir.path(),
            |_| unreachable!(),
            |_| panic!("must not mask runtime/protocol errors"),
        );
        assert!(result.is_err());
    }
    let value = resolve_discovery(
        Ok(Ok(json!({"source":"live"}))),
        dir.path(),
        |v| v,
        |_| panic!("must prefer live"),
    )
    .unwrap();
    assert_eq!(value["source"], "live");
}

#[test]
fn console_static_missing_tree_preserves_bridge_error_and_adds_recovery_hint() {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().join("missing");
    let result = resolve_discovery::<()>(
        Err(error(bridge::proto::BridgeErrorCode::IpcSocketMissing)),
        &root,
        |_| unreachable!(),
        |root| catalog(root, "test"),
    )
    .unwrap_err();
    assert_eq!(result.payload["error"]["code"], "ipc_socket_missing");
    assert!(
        result.payload["error"]["hint"]
            .as_str()
            .unwrap()
            .contains("sts2 project recover --kind decompile")
    );
    assert!(catalog(dir.path(), "test").is_none());
}

#[cfg(unix)]
#[test]
fn console_static_fallback_handles_refused_and_missing_sockets() {
    use std::os::unix::net::UnixListener;
    let dir = fixture();
    let socket_dir = tempfile::tempdir().unwrap();
    let socket = socket_dir.path().join("bridge.sock");
    for stale in [false, true] {
        if stale {
            drop(UnixListener::bind(&socket).unwrap());
            assert!(socket.exists());
        }
        let client = bridge::RuntimeBridgeClient::from_config(&crate::TransportConfig {
            kind: crate::TransportKind::Ipc,
            ipc_path: Some(socket.display().to_string()),
            ..Default::default()
        });
        let result = client.discover_console_arguments(bridge::proto::ConsoleCompletionRequest {
            command: "fight".into(),
            ..Default::default()
        });
        let error = result.as_ref().unwrap_err();
        assert_eq!(
            error.code,
            if stale {
                bridge::proto::BridgeErrorCode::IpcConnectionFailed
            } else {
                bridge::proto::BridgeErrorCode::IpcSocketMissing
            } as i32
        );
        let value = resolve_discovery(
            result,
            dir.path(),
            |_| unreachable!(),
            |root| Some(completion(root, "fight", 0, "paper_knights")),
        )
        .unwrap();
        assert_eq!(value["source"], "static-reference");
        assert_eq!(value["complete"], false);
        assert_eq!(
            value["candidateDetails"],
            json!([
                {"value": "PAPER_KNIGHTS_ELITE", "label": null}
            ])
        );
    }
}
