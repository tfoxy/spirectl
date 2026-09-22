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
///
/// `extra_data_root` is a second data root to search alongside the one this
/// process's environment resolves to — the game's `--user-dir` when one is in
/// play. A `--user-dir` occupies exactly the position of the platform data root
/// (`user://` = `<root>/SlayTheSpire2`), so the same derivation applies to both.
/// Without it an instance's logs are invisible: the CLI resolves the root from
/// its OWN environment, while the game wrote under the instance's user dir, so
/// every log scan reads the operator's default directory instead. See
/// [`sts2_log_dirs_for`].
pub(crate) fn sts2_log_dirs(extra_data_root: Option<&std::path::Path>) -> Vec<PathBuf> {
    data_roots(extra_data_root)
        .iter()
        .map(|root| sts2_log_dirs_for(root))
        .collect()
}

/// Directories holding the engine-level `app_userdata` logs, written before the
/// game's custom user dir takes over.
pub(crate) fn godot_log_dirs(extra_data_root: Option<&std::path::Path>) -> Vec<PathBuf> {
    data_roots(extra_data_root)
        .iter()
        .flat_map(|root| godot_log_dirs_for(current_host_platform(), root))
        .collect()
}

/// The game's own log directory under one data root.
pub(crate) fn sts2_log_dirs_for(data_root: &std::path::Path) -> PathBuf {
    data_root.join(STS2_USER_DATA_DIR_NAME).join("logs")
}

/// Every data root worth scanning: the caller's extra root first (it is the more
/// specific answer), then this process's own. Deduplicated so passing the
/// operator's default user dir explicitly does not scan it twice.
fn data_roots(extra_data_root: Option<&std::path::Path>) -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(extra) = extra_data_root {
        roots.push(extra.to_path_buf());
    }
    if let Some(own) = user_data_root()
        && !roots.contains(&own)
    {
        roots.push(own);
    }
    roots
}

/// Longest Unix domain socket path this platform can bind.
///
/// A kernel ABI limit, not a preference: `sockaddr_un.sun_path` is a fixed
/// char array and the path must fit with its NUL terminator — 108 bytes on
/// Linux, 104 on the BSD-derived macOS. Exceeding it does not truncate, it
/// fails the bind outright, so a path derived from a deep working directory
/// produces a game whose bridge never comes up.
pub(crate) fn max_unix_socket_path_len_for(platform: HostPlatform) -> usize {
    match platform {
        HostPlatform::MacOs => 103,
        // Named pipes have no such limit; the value is unused on Windows.
        HostPlatform::Linux | HostPlatform::Windows => 107,
    }
}

pub(crate) fn max_unix_socket_path_len() -> usize {
    max_unix_socket_path_len_for(current_host_platform())
}

/// Whether `path` can actually be bound as a Unix domain socket here.
pub(crate) fn unix_socket_path_fits(path: &str) -> bool {
    path.len() <= max_unix_socket_path_len()
}

/// A short directory to fall back to when a derived socket path cannot fit.
/// `XDG_RUNTIME_DIR` first (`/run/user/<uid>`, the shortest real option and the
/// correct home for runtime sockets), then the platform temp dir.
pub(crate) fn short_runtime_dir() -> PathBuf {
    env_path("XDG_RUNTIME_DIR")
        .filter(|dir| dir.is_absolute())
        .unwrap_or_else(std::env::temp_dir)
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
            assert!(sts2_log_dirs(None).is_empty());
            assert!(godot_log_dirs(None).is_empty());
            return;
        };

        assert_eq!(
            sts2_log_dirs(None),
            vec![root.join(STS2_USER_DATA_DIR_NAME).join("logs")]
        );
        assert_eq!(
            godot_log_dirs(None),
            godot_log_dirs_for(current_host_platform(), &root)
        );
    }

    #[test]
    fn an_extra_data_root_is_scanned_first_and_does_not_replace_the_process_root() {
        let extra = PathBuf::from("/tmp/instances/segvqa/user");

        let sts2 = sts2_log_dirs(Some(&extra));
        assert_eq!(sts2.first(), Some(&sts2_log_dirs_for(&extra)));
        assert_eq!(
            sts2.len(),
            1 + sts2_log_dirs(None).len(),
            "the process's own root must still be scanned"
        );

        let godot = godot_log_dirs(Some(&extra));
        assert_eq!(
            godot.first(),
            godot_log_dirs_for(current_host_platform(), &extra).first()
        );
    }

    #[test]
    fn an_extra_root_equal_to_the_process_root_is_not_scanned_twice() {
        let Some(root) = user_data_root() else {
            return;
        };
        assert_eq!(sts2_log_dirs(Some(&root)), sts2_log_dirs(None));
        assert_eq!(godot_log_dirs(Some(&root)), godot_log_dirs(None));
    }

    #[test]
    fn the_unix_socket_limit_is_the_platform_sun_path_size() {
        // Linux sockaddr_un.sun_path is 108 bytes, macOS 104 — both including
        // the NUL terminator, hence one less usable character.
        assert_eq!(max_unix_socket_path_len_for(HostPlatform::Linux), 107);
        assert_eq!(max_unix_socket_path_len_for(HostPlatform::MacOs), 103);
    }

    #[test]
    fn unix_socket_path_fits_at_the_boundary() {
        let limit = max_unix_socket_path_len();
        assert!(unix_socket_path_fits(&"a".repeat(limit)));
        assert!(!unix_socket_path_fits(&"a".repeat(limit + 1)));
    }
}
