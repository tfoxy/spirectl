//! Multi-instance support.
//!
//! A single `--instance <name>` fans out into a per-instance bridge socket, a
//! per-instance Godot user-dir (saves/settings/logs), and—when
//! `--isolated-build` is set—a per-instance game mirror with its own `mods/`
//! folder so a divergent bridge build can run alongside others.
//!
//! Everything is **deterministically derived from the name** (plus the
//! configured instances root), so a later CLI invocation with the same
//! `--instance <name>` re-derives the same socket without any shared runtime
//! state. The on-disk registry (`<instances-dir>/<name>/instance.json`) only
//! powers `game instances` listing and instance-scoped `close`.
//!
//! See `sts2-mods-dir-executable-relative`: the game loads mods only from
//! `<dir-of-executable>/mods`, which is why isolated builds need a real
//! per-instance executable (a hardlink/copy, never a symlink — `/proc/self/exe`
//! resolves symlinks back to the shared install).

use crate::install_paths::{PathOverrides, resolve_live_bridge_paths};
use crate::{AppConfig, AppError, TransportKind, bridge};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

/// Reserved instance names that cannot be used explicitly.
const RESERVED_NAMES: &[&str] = &["auto", "all"];
pub(crate) const MAX_NAME_LEN: usize = 64;

/// Resolved per-instance layout, threaded through `AppContext` for the few
/// commands that need the instance identity (launch/install/close/instances).
#[derive(Debug, Clone)]
pub(crate) struct InstanceContext {
    pub(crate) name: String,
    pub(crate) isolated: bool,
    /// Unix domain socket path (also the value written into the overlaid
    /// `transport.ipcPath`). Bindable on this platform — see
    /// [`socket_shortened`](Self::socket_shortened).
    pub(crate) socket: String,
    /// Set when the name-derived path was too long for this platform's
    /// `sun_path` and a short one was substituted. Carried so launch can say so:
    /// the operator would otherwise look for a socket under `.sts2/ipc/` that
    /// deliberately is not there.
    pub(crate) socket_shortened: bool,
    /// The path the derivation asked for, kept only when it was replaced.
    pub(crate) derived_socket: Option<String>,
    /// Godot `--user-dir` target.
    pub(crate) user_dir: PathBuf,
    /// `<instances-dir>/<name>`.
    pub(crate) instance_dir: PathBuf,
    /// `<instances-dir>/<name>/instance.json`.
    pub(crate) registry_path: PathBuf,
    /// `<instances-dir>/<name>/game` — the isolated mirror root (only used when
    /// `isolated`).
    pub(crate) game_root: PathBuf,
    /// Mirror mods folder `<game_root>/mods` (only meaningful when `isolated`).
    pub(crate) mods_dir: PathBuf,
    /// Mirror executable `<game_root>/<exe-basename>` (only meaningful when
    /// `isolated`).
    pub(crate) launch_executable: PathBuf,
}

impl InstanceContext {
    /// Resolve the instance layout. Returns `Ok(None)` when no instance was
    /// requested.
    pub(crate) fn resolve(
        name: Option<&str>,
        isolated: bool,
        config: &AppConfig,
    ) -> Result<Option<Self>, AppError> {
        let Some(raw) = name else {
            return Ok(None);
        };

        if !matches!(config.transport.kind, TransportKind::Ipc) {
            return Err(instance_error(
                "instance_requires_ipc",
                format!(
                    "--instance requires transport.kind 'ipc', but it is '{}'.",
                    transport_kind_label(config.transport.kind)
                ),
            ));
        }

        let instances_dir = absolute_instances_dir(config);
        let name = if raw == "auto" {
            allocate_auto_name(&instances_dir)
        } else {
            validate_name(raw)?
        };

        let instance_dir = instances_dir.join(&name);
        let game_root = instance_dir.join("game");

        // Determine the mirror executable basename from the shared install so
        // Godot finds `<basename>.pck` next to the per-instance executable.
        let (mods_dir, launch_executable) = if isolated {
            let exe_basename = shared_executable_basename(config)?;
            (game_root.join("mods"), game_root.join(exe_basename))
        } else {
            (game_root.join("mods"), game_root.join("game"))
        };

        let socket = bridge::resolve_instance_ipc_path(&name);

        Ok(Some(Self {
            name: name.clone(),
            isolated,
            socket: socket.path,
            socket_shortened: socket.shortened,
            derived_socket: socket.shortened.then_some(socket.derived),
            user_dir: instance_dir.join("user"),
            registry_path: instance_dir.join("instance.json"),
            game_root,
            mods_dir,
            launch_executable,
            instance_dir,
        }))
    }

    /// Overlay the instance's derived endpoint/paths onto a config so existing
    /// resolution (`resolve_endpoint`, `resolve_layout`, `resolve_launch_command`)
    /// becomes instance-aware with no further threading.
    pub(crate) fn apply_overlay(&self, config: &mut AppConfig) {
        #[cfg(not(windows))]
        {
            config.transport.ipc_path = Some(self.socket.clone());
            config.transport.pipe_name = None;
        }
        #[cfg(windows)]
        {
            config.transport.pipe_name = Some(bridge::instance_pipe_name(&self.name));
            config.transport.ipc_path = None;
        }

        config.game.user_dir = Some(self.user_dir.display().to_string());

        if self.isolated {
            config.game.mods_dir = Some(self.mods_dir.display().to_string());
            config.game.launch_executable = Some(self.launch_executable.display().to_string());
        }
    }

    pub(crate) fn mode_label(&self) -> &'static str {
        if self.isolated { "isolated" } else { "shared" }
    }
}

/// Resolve the configured instances root to an absolute path (mirrors
/// `absolute_artifacts_dir`).
pub(crate) fn absolute_instances_dir(config: &AppConfig) -> PathBuf {
    let configured = PathBuf::from(&config.instances.dir);
    if configured.is_absolute() {
        configured
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(&configured))
            .unwrap_or(configured)
    }
}

fn validate_name(raw: &str) -> Result<String, AppError> {
    let name = raw.trim();
    let valid = !name.is_empty()
        && name.len() <= MAX_NAME_LEN
        && !name.starts_with('.')
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'));
    if !valid {
        return Err(instance_error(
            "invalid_instance_name",
            format!(
                "instance name '{raw}' is invalid; use 1-{MAX_NAME_LEN} chars from [A-Za-z0-9._-], not starting with '.'."
            ),
        ));
    }
    if RESERVED_NAMES.contains(&name) {
        return Err(instance_error(
            "reserved_instance_name",
            format!("instance name '{name}' is reserved."),
        ));
    }
    Ok(name.to_string())
}

/// Allocate a short, collision-resistant random instance name (`i-<8hex>`).
fn allocate_auto_name(instances_dir: &Path) -> String {
    for attempt in 0..8u32 {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let entropy =
            (nanos as u64) ^ ((std::process::id() as u64) << 17) ^ ((attempt as u64) << 41);
        let name = format!("i-{:08x}", (entropy & 0xffff_ffff) as u32);
        if !instances_dir.join(&name).exists() {
            return name;
        }
    }
    // Extremely unlikely fall-through: include the full nanos for uniqueness.
    format!(
        "i-{:x}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0)
    )
}

fn shared_executable_basename(config: &AppConfig) -> Result<PathBuf, AppError> {
    if let Some(exe) = &config.game.launch_executable
        && let Some(name) = Path::new(exe).file_name()
    {
        return Ok(PathBuf::from(name));
    }
    let resolved = resolve_live_bridge_paths(config, PathOverrides::default())
        .map_err(|message| instance_error("instance_game_path_unresolved", message))?;
    let exe = crate::lifecycle::derive_launch_executable(
        &resolved.game_path,
        crate::lifecycle::current_launch_platform(),
    )?;
    exe.file_name().map(PathBuf::from).ok_or_else(|| {
        instance_error(
            "instance_exe_basename_unresolved",
            "could not determine the game executable basename for the isolated build mirror."
                .to_string(),
        )
    })
}

fn transport_kind_label(kind: TransportKind) -> &'static str {
    match kind {
        TransportKind::Ipc => "ipc",
        TransportKind::Tcp => "tcp",
        TransportKind::Mock => "mock",
    }
}

pub(crate) fn instance_error(code: &str, message: String) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": code,
                "message": message,
            }
        }),
    }
}

// ---------------------------------------------------------------------------
// Registry
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct InstanceRecord {
    pub(crate) name: String,
    pub(crate) mode: String,
    pub(crate) socket: String,
    /// Present only when the derived socket path did not fit and a short one was
    /// used, so a reader of the registry can see the substitution rather than
    /// wonder why `socket` is not under `.sts2/ipc/`.
    #[serde(skip_serializing_if = "std::ops::Not::not", default)]
    pub(crate) socket_shortened: bool,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) derived_socket: Option<String>,
    pub(crate) user_dir: String,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) game_root: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) mods_dir: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) launch_executable: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) pid: Option<u32>,
    /// Game processes observed for this launch. Lets `close`/`kill` find the
    /// game again when the executable path no longer resolves the same way
    /// (a different worktree, a rebuilt mirror).
    #[serde(skip_serializing_if = "Vec::is_empty", default)]
    pub(crate) game_pids: Vec<u32>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) game_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) stdout_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub(crate) stderr_path: Option<String>,
    pub(crate) transport_kind: String,
    pub(crate) status: String,
    pub(crate) created_at_unix: u64,
    pub(crate) updated_at_unix: u64,
}

/// What a lifecycle command learned about a run when it refreshes the registry.
/// Absent fields keep whatever the existing record holds, so `install-bridge`
/// cannot wipe the pids of a running instance.
#[derive(Debug, Default, Clone)]
pub(crate) struct InstanceRunUpdate {
    pub(crate) pid: Option<u32>,
    pub(crate) game_pids: Vec<u32>,
    pub(crate) game_path: Option<PathBuf>,
    pub(crate) stdout_path: Option<PathBuf>,
    pub(crate) stderr_path: Option<PathBuf>,
}

fn now_unix() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

impl InstanceContext {
    /// Write/refresh this instance's registry entry. Preserves `createdAt` when
    /// an entry already exists.
    pub(crate) fn write_record(
        &self,
        update: &InstanceRunUpdate,
        status: &str,
    ) -> Result<(), AppError> {
        fs::create_dir_all(&self.instance_dir)
            .map_err(|e| instance_error("instance_registry_write_failed", e.to_string()))?;
        let existing = read_record(&self.registry_path);
        let created_at_unix = existing
            .as_ref()
            .map(|existing| existing.created_at_unix)
            .unwrap_or_else(now_unix);
        let carried = |current: Option<String>, previous: fn(&InstanceRecord) -> Option<String>| {
            current.or_else(|| existing.as_ref().and_then(previous))
        };
        let record = InstanceRecord {
            name: self.name.clone(),
            mode: self.mode_label().to_string(),
            socket: self.socket.clone(),
            socket_shortened: self.socket_shortened,
            derived_socket: self.derived_socket.clone(),
            user_dir: self.user_dir.display().to_string(),
            game_root: self.isolated.then(|| self.game_root.display().to_string()),
            mods_dir: self.isolated.then(|| self.mods_dir.display().to_string()),
            // Recorded for every mode: process matching needs the executable
            // this instance was launched with, not only isolated mirrors.
            launch_executable: Some(self.launch_executable.display().to_string()),
            pid: update
                .pid
                .or_else(|| existing.as_ref().and_then(|record| record.pid)),
            game_pids: if update.game_pids.is_empty() {
                existing
                    .as_ref()
                    .map(|record| record.game_pids.clone())
                    .unwrap_or_default()
            } else {
                update.game_pids.clone()
            },
            game_path: carried(
                update.game_path.as_ref().map(|p| p.display().to_string()),
                |record| record.game_path.clone(),
            ),
            stdout_path: carried(
                update.stdout_path.as_ref().map(|p| p.display().to_string()),
                |record| record.stdout_path.clone(),
            ),
            stderr_path: carried(
                update.stderr_path.as_ref().map(|p| p.display().to_string()),
                |record| record.stderr_path.clone(),
            ),
            transport_kind: "ipc".to_string(),
            status: status.to_string(),
            created_at_unix,
            updated_at_unix: now_unix(),
        };
        let json = serde_json::to_string_pretty(&record)
            .map_err(|e| instance_error("instance_registry_write_failed", e.to_string()))?;
        fs::write(&self.registry_path, json)
            .map_err(|e| instance_error("instance_registry_write_failed", e.to_string()))
    }

    /// Update only `status` (+ optional pid) on an existing record; no-op when
    /// the record is missing.
    pub(crate) fn mark_status(&self, status: &str, pid: Option<u32>) -> Result<(), AppError> {
        let Some(mut record) = read_record(&self.registry_path) else {
            return Ok(());
        };
        record.status = status.to_string();
        record.pid = pid.or(record.pid);
        record.updated_at_unix = now_unix();
        let json = serde_json::to_string_pretty(&record)
            .map_err(|e| instance_error("instance_registry_write_failed", e.to_string()))?;
        fs::write(&self.registry_path, json)
            .map_err(|e| instance_error("instance_registry_write_failed", e.to_string()))
    }
}

pub(crate) fn read_record(path: &Path) -> Option<InstanceRecord> {
    let raw = fs::read_to_string(path).ok()?;
    serde_json::from_str(&raw).ok()
}

/// Enumerate all registry records under the instances root.
pub(crate) fn list_records(instances_dir: &Path) -> Vec<InstanceRecord> {
    let mut records = Vec::new();
    let Ok(entries) = fs::read_dir(instances_dir) else {
        return records;
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path().join("instance.json");
        if let Some(record) = read_record(&path) {
            records.push(record);
        }
    }
    records.sort_by(|a, b| a.name.cmp(&b.name));
    records
}

/// Best-effort liveness check for a recorded pid.
///
/// Linux reads `/proc`; Windows asks `tasklist`. A platform with neither answer
/// reports the pid as live so callers fall back to their own probes instead of
/// treating a running game as gone.
pub(crate) fn pid_is_live(pid: u32) -> bool {
    #[cfg(target_os = "linux")]
    {
        Path::new("/proc").join(pid.to_string()).exists()
    }

    #[cfg(windows)]
    {
        crate::lifecycle::windows_process::windows_pid_is_live(pid)
    }

    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = pid;
        true
    }
}

// ---------------------------------------------------------------------------
// Isolated build mirror (Workstream E)
// ---------------------------------------------------------------------------

/// Create/refresh a per-instance game mirror so `OS.GetExecutablePath()`
/// resolves into `game_root` and the game reads `<game_root>/mods`.
///
/// Big read-only files are symlinked to the shared install; the executable is a
/// real hardlink (or copy) so `/proc/self/exe` reports the per-instance path;
/// `mods/` is a real dir with every shared mod symlinked **except**
/// `spirectlbridge`, which `install-bridge` deploys as a real per-instance build.
#[cfg(unix)]
pub(crate) fn ensure_game_mirror(
    shared_install: &Path,
    game_root: &Path,
    exe_basename: &std::ffi::OsStr,
) -> Result<(), AppError> {
    let map_err = |e: std::io::Error| instance_error("instance_mirror_failed", e.to_string());

    fs::create_dir_all(game_root).map_err(map_err)?;

    let entries = fs::read_dir(shared_install).map_err(map_err)?;
    for entry in entries.filter_map(Result::ok) {
        let name = entry.file_name();
        let src = entry.path();
        let dest = game_root.join(&name);

        if name == "mods" {
            mirror_mods_dir(&src, &dest)?;
        } else if name == exe_basename {
            mirror_executable(&src, &dest)?;
        } else {
            ensure_symlink(&src, &dest)?;
        }
    }

    // The executable may not have been enumerated yet if it was filtered; make
    // sure it exists.
    let exe_dest = game_root.join(exe_basename);
    if !exe_dest.exists() {
        let exe_src = shared_install.join(exe_basename);
        if exe_src.exists() {
            mirror_executable(&exe_src, &exe_dest)?;
        }
    }
    Ok(())
}

#[cfg(unix)]
fn mirror_mods_dir(shared_mods: &Path, dest_mods: &Path) -> Result<(), AppError> {
    let map_err = |e: std::io::Error| instance_error("instance_mirror_failed", e.to_string());
    fs::create_dir_all(dest_mods).map_err(map_err)?;
    let Ok(entries) = fs::read_dir(shared_mods) else {
        return Ok(());
    };
    for entry in entries.filter_map(Result::ok) {
        let name = entry.file_name();
        // The per-instance spirectlbridge is deployed real by install-bridge.
        if name == crate::live_bridge::DEPLOYED_MOD_DIR_NAME {
            continue;
        }
        ensure_symlink(&entry.path(), &dest_mods.join(&name))?;
    }
    Ok(())
}

#[cfg(unix)]
fn mirror_executable(src: &Path, dest: &Path) -> Result<(), AppError> {
    let map_err = |e: std::io::Error| instance_error("instance_mirror_failed", e.to_string());

    // Refresh if the destination is stale (older than the shared executable) or
    // is a symlink (must be a real file for /proc/self/exe).
    let needs_refresh = match fs::symlink_metadata(dest) {
        Ok(meta) => {
            meta.file_type().is_symlink()
                || file_mtime(src)
                    .zip(file_mtime(dest))
                    .is_some_and(|(src_mtime, dest_mtime)| src_mtime > dest_mtime)
        }
        Err(_) => true,
    };
    if !needs_refresh {
        return Ok(());
    }
    let _ = fs::remove_file(dest);

    // Prefer a hardlink (free, same inode, reports the per-instance path); fall
    // back to a copy across filesystems.
    if fs::hard_link(src, dest).is_err() {
        fs::copy(src, dest).map_err(map_err)?;
        copy_executable_permissions(src, dest)?;
    }
    Ok(())
}

#[cfg(unix)]
fn copy_executable_permissions(src: &Path, dest: &Path) -> Result<(), AppError> {
    use std::os::unix::fs::PermissionsExt;
    let map_err = |e: std::io::Error| instance_error("instance_mirror_failed", e.to_string());
    let mode = fs::metadata(src).map_err(map_err)?.permissions().mode();
    fs::set_permissions(dest, fs::Permissions::from_mode(mode)).map_err(map_err)
}

/// What [`ensure_symlink`] did (or could not do) at the link path.
#[cfg(unix)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum SymlinkOutcome {
    /// The symlink was created (or repointed from a stale target).
    Linked,
    /// The symlink already pointed at the target.
    AlreadyLinked,
    /// A real file/dir occupies the slot and was left untouched.
    BlockedByExisting,
}

#[cfg(unix)]
pub(crate) fn ensure_symlink(target: &Path, link: &Path) -> Result<SymlinkOutcome, AppError> {
    use std::os::unix::fs::symlink;
    let map_err = |e: std::io::Error| instance_error("instance_mirror_failed", e.to_string());
    if let Ok(existing) = fs::read_link(link) {
        if existing == target {
            return Ok(SymlinkOutcome::AlreadyLinked);
        }
        let _ = fs::remove_file(link);
    } else if link.exists() {
        // A real file/dir occupies the slot; leave it alone rather than clobber.
        return Ok(SymlinkOutcome::BlockedByExisting);
    }
    symlink(target, link).map_err(map_err)?;
    Ok(SymlinkOutcome::Linked)
}

#[cfg(unix)]
fn file_mtime(path: &Path) -> Option<SystemTime> {
    fs::metadata(path).ok()?.modified().ok()
}

#[cfg(not(unix))]
pub(crate) fn ensure_game_mirror(
    _shared_install: &Path,
    _game_root: &Path,
    _exe_basename: &std::ffi::OsStr,
) -> Result<(), AppError> {
    Err(instance_error(
        "instance_isolated_build_unsupported",
        "isolated-build instances are only supported on Unix hosts.".to_string(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ipc_config() -> AppConfig {
        AppConfig::default()
    }

    #[test]
    fn resolve_none_when_no_instance() {
        let ctx = InstanceContext::resolve(None, false, &ipc_config()).unwrap();
        assert!(ctx.is_none());
    }

    #[test]
    fn rejects_invalid_and_reserved_names() {
        assert!(validate_name("../escape").is_err());
        assert!(validate_name("has space").is_err());
        assert!(validate_name(".hidden").is_err());
        assert!(validate_name("auto").is_err());
        assert!(validate_name("all").is_err());
        assert!(validate_name("ok-name_1.2").is_ok());
    }

    #[test]
    fn shared_resolution_is_deterministic() {
        let config = ipc_config();
        let a = InstanceContext::resolve(Some("alpha"), false, &config)
            .unwrap()
            .unwrap();
        let b = InstanceContext::resolve(Some("alpha"), false, &config)
            .unwrap()
            .unwrap();
        assert_eq!(a.socket, b.socket);
        assert_eq!(a.user_dir, b.user_dir);
        assert!(a.socket.ends_with("/.sts2/ipc/alpha.sock"));
        assert!(!a.isolated);
    }

    #[test]
    fn the_resolved_socket_is_always_bindable() {
        // The derivation hangs off the working directory, so from a deep enough
        // one it would otherwise exceed sun_path and the bridge could never
        // listen — a game that launches, looks healthy, and has no transport.
        let config = ipc_config();
        let ctx = InstanceContext::resolve(Some("alpha"), false, &config)
            .unwrap()
            .unwrap();

        assert!(
            crate::host_paths::unix_socket_path_fits(&ctx.socket),
            "resolved socket must fit the platform limit: {}",
            ctx.socket
        );
        // From the crate directory nothing needs substituting, so the registry
        // stays free of the extra fields.
        assert!(!ctx.socket_shortened);
        assert!(ctx.derived_socket.is_none());
    }

    #[test]
    fn auto_allocates_distinct_names() {
        let config = ipc_config();
        let a = InstanceContext::resolve(Some("auto"), false, &config)
            .unwrap()
            .unwrap();
        assert!(a.name.starts_with("i-"));
        assert_ne!(a.name, "auto");
    }

    #[test]
    fn overlay_sets_socket_and_user_dir() {
        let config = ipc_config();
        let ctx = InstanceContext::resolve(Some("beta"), false, &config)
            .unwrap()
            .unwrap();
        let mut overlaid = config.clone();
        ctx.apply_overlay(&mut overlaid);
        #[cfg(not(windows))]
        assert_eq!(
            overlaid.transport.ipc_path.as_deref(),
            Some(ctx.socket.as_str())
        );
        assert_eq!(
            overlaid.game.user_dir.as_deref(),
            Some(ctx.user_dir.display().to_string().as_str())
        );
        // Shared mode must NOT override mods dir / launch executable.
        assert!(overlaid.game.mods_dir.is_none());
    }

    #[cfg(unix)]
    #[test]
    fn mirror_symlinks_install_but_realizes_executable_and_mods() {
        let root = std::env::temp_dir().join(format!("spirectl-mirror-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let shared = root.join("install");
        fs::create_dir_all(shared.join("mods/BaseLib")).unwrap();
        fs::create_dir_all(shared.join("mods/spirectlbridge")).unwrap();
        fs::write(shared.join("Game"), b"#!/bin/true\n").unwrap();
        fs::write(shared.join("Game.pck"), b"pck-bytes").unwrap();
        fs::write(shared.join("mods/BaseLib/BaseLib.dll"), b"dll").unwrap();

        let game_root = root.join("mirror");
        ensure_game_mirror(&shared, &game_root, std::ffi::OsStr::new("Game")).unwrap();

        // Executable must be a real file (not a symlink) for /proc/self/exe.
        let exe_meta = fs::symlink_metadata(game_root.join("Game")).unwrap();
        assert!(
            !exe_meta.file_type().is_symlink(),
            "executable must be real"
        );
        assert!(exe_meta.file_type().is_file());

        // Big files are symlinked.
        assert!(
            fs::symlink_metadata(game_root.join("Game.pck"))
                .unwrap()
                .file_type()
                .is_symlink()
        );

        // mods/ is a real dir; BaseLib symlinked; spirectlbridge absent.
        let mods_meta = fs::symlink_metadata(game_root.join("mods")).unwrap();
        assert!(mods_meta.file_type().is_dir() && !mods_meta.file_type().is_symlink());
        assert!(
            fs::symlink_metadata(game_root.join("mods/BaseLib"))
                .unwrap()
                .file_type()
                .is_symlink()
        );
        assert!(!game_root.join("mods/spirectlbridge").exists());

        // Idempotent: a second pass succeeds without error.
        ensure_game_mirror(&shared, &game_root, std::ffi::OsStr::new("Game")).unwrap();
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn registry_round_trip() {
        let dir = std::env::temp_dir().join(format!("spirectl-itest-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let ctx = InstanceContext {
            name: "rt".to_string(),
            isolated: false,
            socket: "/tmp/rt.sock".to_string(),
            socket_shortened: false,
            derived_socket: None,
            user_dir: dir.join("rt/user"),
            instance_dir: dir.join("rt"),
            registry_path: dir.join("rt/instance.json"),
            game_root: dir.join("rt/game"),
            mods_dir: dir.join("rt/game/mods"),
            launch_executable: dir.join("rt/game/exe"),
        };
        ctx.write_record(
            &InstanceRunUpdate {
                pid: Some(1234),
                game_pids: vec![1234, 1235],
                game_path: Some(PathBuf::from("/games/sts2")),
                stdout_path: Some(dir.join("rt/logs/game.stdout.log")),
                stderr_path: Some(dir.join("rt/logs/game.stderr.log")),
            },
            "running",
        )
        .unwrap();
        let records = list_records(&dir);
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].name, "rt");
        assert_eq!(records[0].pid, Some(1234));
        assert_eq!(records[0].game_pids, vec![1234, 1235]);
        assert_eq!(records[0].status, "running");
        assert_eq!(
            records[0].launch_executable.as_deref(),
            Some(dir.join("rt/game/exe").display().to_string().as_str())
        );
        assert!(
            records[0]
                .stderr_path
                .as_deref()
                .is_some_and(|path| path.ends_with("game.stderr.log"))
        );

        // A later write with nothing new must not wipe what the launch recorded.
        ctx.write_record(&InstanceRunUpdate::default(), "installed")
            .unwrap();
        let carried = read_record(&ctx.registry_path).unwrap();
        assert_eq!(carried.pid, Some(1234));
        assert_eq!(carried.game_pids, vec![1234, 1235]);
        assert_eq!(carried.status, "installed");
        ctx.mark_status("stopped", None).unwrap();
        let after = read_record(&ctx.registry_path).unwrap();
        assert_eq!(after.status, "stopped");
        assert_eq!(after.created_at_unix, records[0].created_at_unix);
        let _ = fs::remove_dir_all(&dir);
    }
}
