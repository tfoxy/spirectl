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
    if version.is_empty() {
        return None;
    }
    let needle = format!(";{version}=");
    let rest = &API_LANE_TABLE[API_LANE_TABLE.find(&needle)? + needle.len()..];
    let lane = rest.split(';').next()?;
    if lane.is_empty() { None } else { Some(lane) }
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
    fn lane_table_resolves_supported_builds_and_refuses_to_guess() {
        assert_eq!(api_lane_for_game_version("v0.107.1"), Some("v107"));
        assert_eq!(api_lane_for_game_version("v0.111.0"), Some("v111"));
        assert_eq!(api_lane_for_game_version("v0.999.0"), None);
        assert_eq!(api_lane_for_game_version(""), None);
        // A prefix of a mapped version must not borrow that version's row.
        assert_eq!(api_lane_for_game_version("v0.107"), None);
        assert!(api_lanes().contains(&"v107"));
        assert!(api_lanes().contains(&"v111"));
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
