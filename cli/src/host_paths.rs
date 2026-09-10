//! Host-platform identity and user-data locations.
//!
//! Godot resolves `user://` under an OS-specific data root, and STS2 sets a
//! custom user-dir name on top of it. Both the local log scan and the mod
//! load-order helpers need that root, so it lives here once instead of being
//! rederived (Linux-only) in each caller.
//!
//! The per-platform rules are pure functions over an environment lookup, so
//! every branch is exercised by the test run on whichever host it happens to
//! run on.

use std::path::PathBuf;

/// STS2's `project.godot` sets `use_custom_user_dir` with this
/// `custom_user_dir_name`, so `user://` resolves to `<data_root>/SlayTheSpire2`.
pub(crate) const STS2_USER_DATA_DIR_NAME: &str = "SlayTheSpire2";

/// Godot's project name for STS2, used for the engine's own
/// `<data_root>/<godot>/app_userdata/<project>` log directory.
pub(crate) const GODOT_PROJECT_NAME: &str = "Slay the Spire 2";

/// The host family the CLI is running on. Behavioural differences between
/// Windows, macOS and Linux are selected through this rather than repeated
/// `cfg!` checks, which also keeps them testable from any host.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum HostPlatform {
    Windows,
    MacOs,
    Linux,
}

pub(crate) fn current_host_platform() -> HostPlatform {
    if cfg!(target_os = "windows") {
        HostPlatform::Windows
    } else if cfg!(target_os = "macos") {
        HostPlatform::MacOs
    } else {
        HostPlatform::Linux
    }
}

/// The engine's `OS.get_data_dir()` equivalent for the current host.
pub(crate) fn user_data_root() -> Option<PathBuf> {
    data_root_for(current_host_platform(), &env_path)
}

/// Where the game writes `user://` on this host.
pub(crate) fn sts2_user_data_root() -> Option<PathBuf> {
    user_data_root().map(|root| root.join(STS2_USER_DATA_DIR_NAME))
}

/// Directories holding the game's own `godot*.log` files.
pub(crate) fn sts2_log_dirs() -> Vec<PathBuf> {
    sts2_user_data_root()
        .map(|root| vec![root.join("logs")])
        .unwrap_or_default()
}

/// Directories holding the engine-level `app_userdata` logs, written before the
/// game's custom user dir takes over.
pub(crate) fn godot_log_dirs() -> Vec<PathBuf> {
    user_data_root()
        .map(|root| godot_log_dirs_for(current_host_platform(), &root))
        .unwrap_or_default()
}

/// Godot's data path per platform:
/// Linux honors `XDG_DATA_HOME` (which is also how per-instance isolation
/// works), Windows uses roaming `%APPDATA%`, macOS uses
/// `~/Library/Application Support`.
fn data_root_for(
    platform: HostPlatform,
    lookup: &dyn Fn(&str) -> Option<PathBuf>,
) -> Option<PathBuf> {
    match platform {
        HostPlatform::Windows => lookup("APPDATA")
            .or_else(|| lookup("USERPROFILE").map(|home| home.join("AppData").join("Roaming"))),
        HostPlatform::MacOs => {
            lookup("HOME").map(|home| home.join("Library").join("Application Support"))
        }
        HostPlatform::Linux => {
            lookup("XDG_DATA_HOME").or_else(|| lookup("HOME").map(|home| home.join(".local/share")))
        }
    }
}

fn godot_log_dirs_for(platform: HostPlatform, data_root: &std::path::Path) -> Vec<PathBuf> {
    // The engine capitalizes this directory on Windows and macOS. Windows paths
    // are case-insensitive anyway, but match what the engine writes.
    let godot_dir = match platform {
        HostPlatform::Linux => "godot",
        HostPlatform::Windows | HostPlatform::MacOs => "Godot",
    };
    vec![
        data_root
            .join(godot_dir)
            .join("app_userdata")
            .join(GODOT_PROJECT_NAME)
            .join("logs"),
    ]
}

fn env_path(name: &str) -> Option<PathBuf> {
    std::env::var_os(name)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

    fn lookup(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<PathBuf> + use<> {
        let map = pairs
            .iter()
            .map(|(key, value)| ((*key).to_string(), PathBuf::from(*value)))
            .collect::<BTreeMap<_, _>>();
        move |name: &str| map.get(name).cloned()
    }

    #[test]
    fn windows_data_root_prefers_appdata_then_derives_it_from_the_profile() {
        assert_eq!(
            data_root_for(
                HostPlatform::Windows,
                &lookup(&[
                    ("APPDATA", r"C:\Users\tester\AppData\Roaming"),
                    ("USERPROFILE", r"C:\Users\tester")
                ])
            ),
            Some(PathBuf::from(r"C:\Users\tester\AppData\Roaming"))
        );
        assert_eq!(
            data_root_for(
                HostPlatform::Windows,
                &lookup(&[("USERPROFILE", r"C:\Users\tester")])
            ),
            Some(
                PathBuf::from(r"C:\Users\tester")
                    .join("AppData")
                    .join("Roaming")
            )
        );
        // XDG means nothing on Windows.
        assert_eq!(
            data_root_for(
                HostPlatform::Windows,
                &lookup(&[("XDG_DATA_HOME", "/tmp/xdg"), ("HOME", "/home/tester")])
            ),
            None
        );
    }

    #[test]
    fn linux_data_root_prefers_xdg_data_home() {
        assert_eq!(
            data_root_for(
                HostPlatform::Linux,
                &lookup(&[("XDG_DATA_HOME", "/tmp/xdg"), ("HOME", "/home/tester")])
            ),
            Some(PathBuf::from("/tmp/xdg"))
        );
        assert_eq!(
            data_root_for(HostPlatform::Linux, &lookup(&[("HOME", "/home/tester")])),
            Some(PathBuf::from("/home/tester/.local/share"))
        );
    }

    #[test]
    fn macos_data_root_is_application_support() {
        assert_eq!(
            data_root_for(HostPlatform::MacOs, &lookup(&[("HOME", "/Users/tester")])),
            Some(PathBuf::from("/Users/tester/Library/Application Support"))
        );
    }

    #[test]
    fn godot_log_dir_casing_follows_the_platform() {
        assert_eq!(
            godot_log_dirs_for(HostPlatform::Linux, std::path::Path::new("/data")),
            vec![PathBuf::from(
                "/data/godot/app_userdata/Slay the Spire 2/logs"
            )]
        );
        assert_eq!(
            godot_log_dirs_for(HostPlatform::Windows, std::path::Path::new("/data")),
            vec![PathBuf::from(
                "/data/Godot/app_userdata/Slay the Spire 2/logs"
            )]
        );
    }

    #[test]
    fn log_dirs_hang_off_the_platform_data_root() {
        let Some(root) = user_data_root() else {
            // A host with neither HOME nor APPDATA reports no directories.
            assert!(sts2_log_dirs().is_empty());
            assert!(godot_log_dirs().is_empty());
            return;
        };

        assert_eq!(
            sts2_log_dirs(),
            vec![root.join(STS2_USER_DATA_DIR_NAME).join("logs")]
        );
        assert_eq!(
            godot_log_dirs(),
            godot_log_dirs_for(current_host_platform(), &root)
        );
    }
}
