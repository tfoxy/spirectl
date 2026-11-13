//! The identity of an STS2 *game build*, and the bridge API lane it needs.
//!
//! Every install carries a `release_info.json` at its root. It is the only
//! first-party statement of which build is on disk, and it is what
//! `bridge-mod/Sts2GameApi.props` already reads at compile time to pick the
//! `src/Spirectl.Sts2/GameApi/<lane>/` sources the live host binds through. A
//! bridge compiled for one build and loaded into another starts, logs a few
//! soft "not found" lines, and then throws `MissingMethodException` the first
//! time it walks a lobby — so the same file has to be readable on this side, to
//! bind a deployed payload to the build it was compiled for.
//!
//! The lane table is *not* restated here. `cli/build.rs` parses it out of
//! `bridge-mod/Sts2GameApi.props` and compiles it in, so MSBuild (spirectl),
//! MSBuild (CouchCoop, via import) and this module are three readers of one
//! source of truth.

use serde::Deserialize;
use std::path::{Path, PathBuf};

/// `Sts2GameApiLanes` — every lane with a `src/Spirectl.Sts2/GameApi/<lane>/`
/// directory, oldest first.
const API_LANES: &str = env!("SPIRECTL_STS2_API_LANES");
/// `Sts2GameApiLaneTable` — `;<release_info.json version>=<lane>;…;`, fenced at
/// both ends so a row lookup is an exact match on `;<version>=`.
const API_LANE_TABLE: &str = env!("SPIRECTL_STS2_API_LANE_TABLE");
/// `Sts2GameApiReleasableLanes` — the subset of lanes a *released* payload can be
/// built for. A release compiles against the locked, declaration-only reference
/// SDK, which pins one game build's declarations; a lane whose sources need a type
/// that package does not declare cannot be packaged at all. Such a lane is still
/// fully supported from source. See the props file for the current reason.
const RELEASABLE_API_LANES: &str = env!("SPIRECTL_STS2_API_RELEASABLE_LANES");

pub const RELEASE_INFO_FILE_NAME: &str = "release_info.json";

/// The fields of `release_info.json` that identify a build. `version` picks the
/// API lane; `main_assembly_hash` is the game's own content identity for
/// `sts2.dll`, which moves even when the version string does not.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct GameReleaseInfo {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub commit: String,
    #[serde(default)]
    pub main_assembly_hash: Option<i64>,
}

impl GameReleaseInfo {
    /// The lane this build compiles against, or `None` when the table has no
    /// row for it (an unsupported build — never guess a lane).
    pub fn api_lane(&self) -> Option<&'static str> {
        api_lane_for_game_version(&self.version)
    }
}

/// Every lane the bridge sources can compile, oldest first.
pub fn api_lanes() -> Vec<&'static str> {
    API_LANES
        .split(';')
        .filter(|lane| !lane.is_empty())
        .collect()
}

/// The lanes a published bridge payload exists for. A subset of [`api_lanes`].
pub fn releasable_api_lanes() -> Vec<&'static str> {
    RELEASABLE_API_LANES
        .split(';')
        .filter(|lane| !lane.is_empty())
        .collect()
}

pub fn is_releasable_api_lane(lane: &str) -> bool {
    !lane.is_empty() && releasable_api_lanes().contains(&lane)
}

/// Exact-match row lookup, matching the fenced `;<version>=` contract the props
/// file's MSBuild regex uses, so both readers agree on every input.
pub fn api_lane_for_game_version(version: &str) -> Option<&'static str> {
    api_lane_in_table(version, API_LANE_TABLE)
}

fn api_lane_in_table<'a>(version: &str, table: &'a str) -> Option<&'a str> {
    if version.is_empty() {
        return None;
    }
    let needle = format!(";{version}=");
    let rest = &table[table.find(&needle)? + needle.len()..];
    let lane = rest.split(';').next()?;
    if lane.is_empty() { None } else { Some(lane) }
}

/// Explain an unmapped build without using a newer source table to select a
/// payload the running CLI does not know how to validate.
pub(crate) fn api_lane_resolution_diagnostic(version: &str) -> Option<serde_json::Value> {
    if api_lane_for_game_version(version).is_some() {
        return None;
    }
    lane_resolution_diagnostic(version, API_LANE_TABLE, source_lane_props().as_deref())
}

fn source_lane_props() -> Option<PathBuf> {
    for base in [
        std::env::current_dir().ok(),
        std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(Path::to_path_buf)),
    ]
    .into_iter()
    .flatten()
    {
        for ancestor in base.ancestors() {
            let props = ancestor.join("bridge-mod/Sts2GameApi.props");
            if props.is_file() {
                return Some(props);
            }
        }
    }
    None
}

fn lane_resolution_diagnostic(
    version: &str,
    table: &str,
    source_props: Option<&Path>,
) -> Option<serde_json::Value> {
    use serde_json::json;

    if api_lane_in_table(version, table).is_some() {
        return None;
    }
    let known_lanes: Vec<_> = table
        .split(';')
        .filter_map(|row| row.split_once('='))
        .map(|(version, lane)| json!({ "gameVersion": version, "apiLane": lane }))
        .collect();
    let known = known_lanes
        .iter()
        .map(|row| {
            format!(
                "{}→{}",
                row["gameVersion"].as_str().unwrap(),
                row["apiLane"].as_str().unwrap()
            )
        })
        .collect::<Vec<_>>()
        .join(", ");
    let source_lane = source_props
        .and_then(|path| std::fs::read_to_string(path).ok())
        .and_then(|contents| {
            let start = contents.find("<Sts2GameApiLaneTable>")? + "<Sts2GameApiLaneTable>".len();
            let table = contents[start..]
                .split_once("</Sts2GameApiLaneTable>")?
                .0
                .trim();
            api_lane_in_table(version, table).map(str::to_string)
        });
    let mut message =
        format!("game build {version} has no API lane in this sts2 build (known: {known})");
    let (code, remediation) = if let Some(lane) = source_lane.as_deref() {
        message.push_str(&format!(
            "; this sts2 build predates lane {lane} (mapped in {}); rebuild with scripts/install-cli.sh",
            source_props.unwrap().display()
        ));
        (
            "cli_lane_table_stale",
            "rebuild with scripts/install-cli.sh",
        )
    } else {
        message.push_str("; update sts2 to a build supporting this game version");
        (
            "bridge_release_lane_unresolved",
            "update sts2 to a build supporting this game version",
        )
    };
    Some(json!({
        "code": code,
        "message": message,
        "gameVersion": version,
        "knownLanes": known_lanes,
        "sourceLane": source_lane,
        "sourcePath": source_lane.as_ref().and(source_props),
        "remediation": remediation
    }))
}

/// Read `<game_root>/release_info.json`. `None` when it is absent or
/// unparseable: callers decide whether an unknown build is a refusal or simply
/// an unknown, and none of them may invent an identity for it.
pub fn read_release_info(game_root: &Path) -> Option<GameReleaseInfo> {
    let raw = std::fs::read_to_string(release_info_path(game_root)).ok()?;
    let info: GameReleaseInfo = serde_json::from_str(&raw).ok()?;
    (!info.version.is_empty()).then_some(info)
}

pub fn release_info_path(game_root: &Path) -> PathBuf {
    game_root.join(RELEASE_INFO_FILE_NAME)
}

/// The install a bridge is *compiled against*, resolved exactly the way
/// `Sts2GameApi.props` resolves it: `$(Sts2AssembliesDir)/../release_info.json`.
/// Using the same anchor as the compiler is the whole point — a stamp taken
/// from somewhere else could disagree with the lane that was actually built.
pub fn read_release_info_for_assemblies_dir(assemblies_dir: &Path) -> Option<GameReleaseInfo> {
    read_release_info(assemblies_dir.parent()?)
}

/// The build identity a *deployed* payload claims, as stamped into
/// `spirectlbridge.json`'s `buildIdentity` and carried on the handshake.
///
/// The two sides are deliberately asymmetric, and the asymmetry is the honest
/// part: a **source** build compiles against a real install, so it can name
/// both the game version and that install's `main_assembly_hash`. A
/// **released** payload compiles against the locked, declaration-only reference
/// SDK, which has no `release_info.json` and no game hash at all — it can claim
/// a lane and the reference package version, and nothing more. No hash is
/// invented for it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BridgeGameBuildStamp {
    pub api_lane: Option<String>,
    pub identity_source: &'static str,
    pub game_version: Option<String>,
    pub main_assembly_hash: Option<i64>,
    pub reference_package_version: Option<String>,
}

pub const IDENTITY_SOURCE_INSTALL: &str = "install-release-info";
pub const IDENTITY_SOURCE_REFERENCE_SDK: &str = "reference-sdk";
pub const IDENTITY_SOURCE_UNKNOWN: &str = "unknown";

impl BridgeGameBuildStamp {
    /// A bridge compiled against a real install.
    pub fn from_install(release_info: &GameReleaseInfo) -> Self {
        Self {
            api_lane: release_info.api_lane().map(str::to_string),
            identity_source: IDENTITY_SOURCE_INSTALL,
            game_version: Some(release_info.version.clone()),
            main_assembly_hash: release_info.main_assembly_hash,
            reference_package_version: None,
        }
    }

    /// A released payload: a lane plus the reference package version. There is
    /// no game version and no hash to claim, so neither is claimed.
    pub fn from_reference_sdk(lane: &str, reference_package_version: &str) -> Self {
        Self {
            api_lane: Some(lane.to_string()),
            identity_source: IDENTITY_SOURCE_REFERENCE_SDK,
            game_version: None,
            main_assembly_hash: None,
            reference_package_version: Some(reference_package_version.to_string()),
        }
    }

    /// An install whose build could not be identified at all. Everything stays
    /// empty so downstream comparisons report `unknown` rather than a verdict.
    pub fn unknown() -> Self {
        Self {
            api_lane: None,
            identity_source: IDENTITY_SOURCE_UNKNOWN,
            game_version: None,
            main_assembly_hash: None,
            reference_package_version: None,
        }
    }

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "identitySource": self.identity_source,
            "version": self.game_version,
            "mainAssemblyHash": self.main_assembly_hash,
            "referencePackageVersion": self.reference_package_version
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stale_lane_table_diagnostic_names_the_source_lane_and_rebuild() {
        let temp = tempfile::tempdir().unwrap();
        let props = temp.path().join("Sts2GameApi.props");
        std::fs::write(
            &props,
            "<Project><Sts2GameApiLaneTable>;v0.107.1=v107;v0.113.0=v113;</Sts2GameApiLaneTable></Project>",
        ).unwrap();
        let diagnostic =
            lane_resolution_diagnostic("v0.113.0", ";v0.107.1=v107;", Some(&props)).unwrap();
        assert_eq!(diagnostic["code"], "cli_lane_table_stale");
        assert_eq!(diagnostic["gameVersion"], "v0.113.0");
        assert_eq!(
            diagnostic["knownLanes"],
            serde_json::json!([
                { "gameVersion": "v0.107.1", "apiLane": "v107" }
            ])
        );
        assert_eq!(diagnostic["sourceLane"], "v113");
        assert_eq!(diagnostic["sourcePath"], serde_json::json!(props));
        assert_eq!(
            diagnostic["remediation"],
            "rebuild with scripts/install-cli.sh"
        );
        let message = diagnostic["message"].as_str().unwrap();
        assert!(message.contains(
            "game build v0.113.0 has no API lane in this sts2 build (known: v0.107.1→v107)"
        ));
        assert!(message.contains("this sts2 build predates lane v113"));
        assert!(message.contains(&format!("mapped in {}", props.display())));
        assert!(message.contains("rebuild with scripts/install-cli.sh"));
        // Finding a source row must not change what this binary can resolve.
        assert_eq!(api_lane_in_table("v0.113.0", ";v0.107.1=v107;"), None);
    }

    #[test]
    fn unmapped_lane_diagnostic_does_not_claim_staleness_without_a_source_row() {
        let temp = tempfile::tempdir().unwrap();
        let props = temp.path().join("Sts2GameApi.props");
        for contents in [
            None,
            Some("not XML"),
            Some("<Sts2GameApiLaneTable>;v0.113.0=v113;</Sts2GameApiLaneTable>"),
        ] {
            if let Some(contents) = contents {
                std::fs::write(&props, contents).unwrap();
            }
            let diagnostic =
                lane_resolution_diagnostic("v0.113", ";v0.107.1=v107;", Some(&props)).unwrap();
            assert_eq!(diagnostic["code"], "bridge_release_lane_unresolved");
            assert_eq!(diagnostic["gameVersion"], "v0.113");
            assert!(diagnostic["sourceLane"].is_null());
            assert!(diagnostic["sourcePath"].is_null());
            assert!(
                diagnostic["message"]
                    .as_str()
                    .unwrap()
                    .contains("known: v0.107.1→v107")
            );
        }
    }

    #[test]
    fn mapped_lane_needs_no_diagnostic_or_source_checkout() {
        assert!(lane_resolution_diagnostic("v0.113.0", ";v0.113.0=v113;", None).is_none());
    }

    #[test]
    fn lane_table_resolves_supported_builds_and_refuses_to_guess() {
        assert_eq!(api_lane_for_game_version("v0.107.1"), Some("v107"));
        assert_eq!(api_lane_for_game_version("v0.111.0"), Some("v111"));
        assert_eq!(api_lane_for_game_version("v0.113.0"), Some("v113"));
        assert_eq!(api_lane_for_game_version("v0.999.0"), None);
        assert_eq!(api_lane_for_game_version(""), None);
        // A prefix of a mapped version must not borrow that version's row.
        assert_eq!(api_lane_for_game_version("v0.107"), None);
        assert!(api_lanes().contains(&"v107"));
        assert!(api_lanes().contains(&"v111"));
        assert!(api_lanes().contains(&"v113"));
    }

    #[test]
    fn releasable_lanes_are_a_subset_of_supported_lanes() {
        let lanes = api_lanes();
        let releasable = releasable_api_lanes();
        assert!(!releasable.is_empty());
        for lane in &releasable {
            assert!(
                lanes.contains(lane),
                "{lane} is releasable but not supported"
            );
        }
        assert!(!is_releasable_api_lane("v113"));
        assert!(!is_releasable_api_lane("v999"));
        assert!(!is_releasable_api_lane(""));
    }

    #[test]
    fn release_info_reader_yields_version_commit_and_hash() {
        let temp = tempfile::tempdir().expect("temp dir");
        std::fs::write(
            release_info_path(temp.path()),
            r#"{"commit":"41cef1ea","version":"v0.111.0","date":"2026-08-13T17:39:18-07:00","branch":"v0.111.0","main_assembly_hash":1579942752}"#,
        )
        .expect("write release info");

        let info = read_release_info(temp.path()).expect("release info");
        assert_eq!(info.version, "v0.111.0");
        assert_eq!(info.commit, "41cef1ea");
        assert_eq!(info.main_assembly_hash, Some(1_579_942_752));
        assert_eq!(info.api_lane(), Some("v111"));
    }

    #[test]
    fn release_info_reader_reports_unknown_rather_than_inventing_one() {
        let temp = tempfile::tempdir().expect("temp dir");
        assert_eq!(read_release_info(temp.path()), None);
        std::fs::write(release_info_path(temp.path()), "not json").expect("write junk");
        assert_eq!(read_release_info(temp.path()), None);
        std::fs::write(release_info_path(temp.path()), r#"{"commit":"abc"}"#).expect("write empty");
        assert_eq!(read_release_info(temp.path()), None);
    }

    #[test]
    fn assemblies_dir_reader_uses_the_same_anchor_as_msbuild() {
        let temp = tempfile::tempdir().expect("temp dir");
        let assemblies = temp.path().join("data_sts2_linuxbsd_x86_64");
        std::fs::create_dir_all(&assemblies).expect("assemblies dir");
        std::fs::write(
            release_info_path(temp.path()),
            r#"{"version":"v0.107.1","main_assembly_hash":1692500715}"#,
        )
        .expect("write release info");

        let info = read_release_info_for_assemblies_dir(&assemblies).expect("release info");
        assert_eq!(info.api_lane(), Some("v107"));
        assert_eq!(info.main_assembly_hash, Some(1_692_500_715));
    }

    #[test]
    fn released_payload_stamp_claims_a_lane_but_never_a_game_hash() {
        let released = BridgeGameBuildStamp::from_reference_sdk("v107", "0.1.0");
        assert_eq!(released.api_lane.as_deref(), Some("v107"));
        assert_eq!(released.game_version, None);
        assert_eq!(released.main_assembly_hash, None);
        assert_eq!(released.reference_package_version.as_deref(), Some("0.1.0"));

        let source = BridgeGameBuildStamp::from_install(&GameReleaseInfo {
            version: "v0.111.0".to_string(),
            commit: "41cef1ea".to_string(),
            main_assembly_hash: Some(1_579_942_752),
        });
        assert_eq!(source.api_lane.as_deref(), Some("v111"));
        assert_eq!(source.game_version.as_deref(), Some("v0.111.0"));
        assert_eq!(source.main_assembly_hash, Some(1_579_942_752));
        assert_eq!(source.reference_package_version, None);
    }
}
