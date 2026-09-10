pub(crate) fn execute_game_mods_settings_json(
    args: GameModsSettingsArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let settings_path = resolve_mod_settings_file(
        context.config,
        args.settings_file.as_deref(),
        "game mods settings",
    )?;
    let mut settings = read_settings_save_json(&settings_path, "game mods settings")?;
    let entries =
        read_mod_entries_from_settings(&mut settings, "game mods settings", &settings_path)?;
    let mods = entries
        .into_iter()
        .map(|entry| {
            json!({
                "id": entry.id,
                "enabled": entry.enabled,
                "source": entry.source,
            })
        })
        .collect::<Vec<_>>();
    let enabled_ids = mods
        .iter()
        .filter(|entry| entry["enabled"].as_bool().unwrap_or(false))
        .filter_map(|entry| entry["id"].as_str())
        .map(str::to_string)
        .collect::<Vec<_>>();
    let disabled_ids = mods
        .iter()
        .filter(|entry| !entry["enabled"].as_bool().unwrap_or(false))
        .filter_map(|entry| entry["id"].as_str())
        .map(str::to_string)
        .collect::<Vec<_>>();

    Ok(json!({
        "settingsFile": settings_path.display().to_string(),
        "mods": mods,
        "enabledIds": enabled_ids,
        "disabledIds": disabled_ids
    }))
}

pub(crate) fn execute_game_mods_active_json(context: AppContext<'_>) -> Result<Value, AppError> {
    let client = bridge::RuntimeBridgeClient::from_config(&context.config.transport);
    let response = client
        .mods(bridge::proto::ModListRequest {
            request_id: format!("game-mods-active-{}", std::process::id()),
        })
        .map_err(AppError::bridge)?;

    Ok(json!({
        "requestId": response.request_id,
        "mods": response.mods.iter().map(live_mod_json).collect::<Vec<_>>(),
        "activeIds": response.mods.iter()
            .filter(|entry| entry.active)
            .map(|entry| entry.id.clone())
            .collect::<Vec<_>>(),
        "enabledIds": response.mods.iter()
            .filter(|entry| entry.enabled)
            .map(|entry| entry.id.clone())
            .collect::<Vec<_>>(),
        "notices": response.notices
    }))
}

fn live_mod_json(mod_info: &bridge::proto::LiveModInfo) -> Value {
    json!({
        "id": mod_info.id,
        "name": mod_info.name,
        "version": mod_info.version,
        "source": mod_info.source,
        "path": empty_string_as_null(&mod_info.path),
        "loadState": live_mod_load_state_name(mod_info.load_state),
        "enabled": mod_info.enabled,
        "active": mod_info.active,
        "assemblyPath": empty_string_as_null(&mod_info.assembly_path),
        "errors": mod_info.errors,
    })
}

fn live_mod_load_state_name(value: i32) -> &'static str {
    match bridge::proto::LiveModLoadState::try_from(value)
        .unwrap_or(bridge::proto::LiveModLoadState::Unspecified)
    {
        bridge::proto::LiveModLoadState::Unspecified => "unspecified",
        bridge::proto::LiveModLoadState::None => "none",
        bridge::proto::LiveModLoadState::Loaded => "loaded",
        bridge::proto::LiveModLoadState::Disabled => "disabled",
        bridge::proto::LiveModLoadState::Failed => "failed",
        bridge::proto::LiveModLoadState::AddedAtRuntime => "added-at-runtime",
        bridge::proto::LiveModLoadState::Unknown => "unknown",
    }
}

fn empty_string_as_null(value: &str) -> Value {
    if value.is_empty() {
        Value::Null
    } else {
        Value::String(value.to_string())
    }
}

fn resolve_mod_settings_file(
    config: &AppConfig,
    override_path: Option<&Path>,
    command_name: &str,
) -> Result<PathBuf, AppError> {
    if let Some(path) = override_path {
        return Ok(resolve_runtime_path(&path.display().to_string()));
    }

    let configured = config.game.mod_loadout.settings_file.trim();
    if !configured.is_empty() && configured != "auto" {
        return Ok(resolve_runtime_path(configured));
    }

    let candidates = sts2_mod_settings_save_candidates(config, command_name)?;
    match candidates.as_slice() {
        [path] => Ok(path.clone()),
        [] => Err(lifecycle_error(
            2,
            "settings_save_not_found",
            command_name,
            "No STS2 settings.save file with mod_settings.mod_list was found under the local SlayTheSpire2 data directory.",
        )),
        _ => Err(AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "settings_save_ambiguous",
                    "command": command_name,
                    "message": "Multiple STS2 settings.save files contain mod_settings.mod_list; set game.modLoadout.settingsFile or pass --settings-file.",
                    "candidates": candidates.iter().map(|path| path.display().to_string()).collect::<Vec<_>>()
                }
            }),
        }),
    }
}

fn sts2_mod_settings_save_candidates(
    config: &AppConfig,
    command_name: &str,
) -> Result<Vec<PathBuf>, AppError> {
    seed_instance_user_data_if_missing(config, command_name)?;
    let mut candidates = Vec::new();
    for path in settings_save_candidates_for(config) {
        if !path.is_file() {
            continue;
        }
        let Ok(mut settings) = read_settings_save_json(&path, command_name) else {
            continue;
        };
        if read_mod_entries_from_settings(&mut settings, command_name, &path).is_ok() {
            candidates.push(path);
        }
    }
    Ok(candidates)
}

fn read_settings_save_json(settings_path: &Path, command_name: &str) -> Result<Value, AppError> {
    let contents = fs::read_to_string(settings_path).map_err(io_lifecycle_error(command_name))?;
    serde_json::from_str::<Value>(&contents).map_err(|source| {
        lifecycle_error(
            4,
            "settings_save_parse_failed",
            command_name,
            &format!(
                "failed to parse STS2 settings file '{}': {source}",
                settings_path.display()
            ),
        )
    })
}

fn write_settings_save_json(
    command_name: &str,
    settings_path: &Path,
    settings: &Value,
) -> Result<(), AppError> {
    let updated = serde_json::to_string_pretty(settings)
        .map(|contents| format!("{contents}\n"))
        .map_err(|source| {
            lifecycle_error(
                4,
                "settings_save_serialize_failed",
                command_name,
                &format!(
                    "failed to serialize STS2 settings file '{}': {source}",
                    settings_path.display()
                ),
            )
        })?;
    let temp_path = settings_path.with_file_name(format!(
        "{}.tmp-{}",
        settings_path
            .file_name()
            .and_then(OsStr::to_str)
            .unwrap_or("settings.save"),
        std::process::id()
    ));
    fs::write(&temp_path, updated).map_err(io_lifecycle_error(command_name))?;
    fs::rename(&temp_path, settings_path).map_err(|source| {
        let _ = fs::remove_file(&temp_path);
        io_lifecycle_error(command_name)(source)
    })
}

fn read_mod_entries_from_settings(
    settings: &mut Value,
    command_name: &str,
    settings_path: &Path,
) -> Result<Vec<SavedModEntry>, AppError> {
    let mod_list = settings
        .get_mut("mod_settings")
        .and_then(Value::as_object_mut)
        .and_then(|mod_settings| mod_settings.get_mut("mod_list"))
        .and_then(Value::as_array_mut)
        .ok_or_else(|| {
            settings_save_shape_error(command_name, settings_path, "mod_list_missing")
        })?;

    Ok(mod_list
        .iter()
        .filter_map(|entry| {
            let id = entry.get("id").and_then(Value::as_str)?.trim();
            if id.is_empty() {
                return None;
            }
            Some(SavedModEntry {
                id: id.to_string(),
                enabled: entry
                    .get("is_enabled")
                    .and_then(Value::as_bool)
                    .unwrap_or(true),
                source: entry
                    .get("source")
                    .and_then(Value::as_str)
                    .unwrap_or("mods_directory")
                    .to_string(),
            })
        })
        .collect())
}

fn enumerate_installed_mod_entries(
    command_name: &str,
    mods_dir: &Path,
) -> Result<BTreeMap<String, SavedModEntry>, AppError> {
    let mut mods = BTreeMap::new();
    if !mods_dir.is_dir() {
        return Ok(mods);
    }

    for entry in fs::read_dir(mods_dir).map_err(io_lifecycle_error(command_name))? {
        let path = entry.map_err(io_lifecycle_error(command_name))?.path();
        let id = if path.is_dir() {
            Some(resolve_mod_id_from_directory(&path))
        } else if path.is_file() && path.extension() == Some(OsStr::new("json")) {
            read_mod_id_from_manifest(&path)
        } else {
            None
        };
        if let Some(id) = id.filter(|id| !id.trim().is_empty()) {
            mods.entry(id.clone()).or_insert(SavedModEntry {
                id,
                enabled: true,
                source: "mods_directory".to_string(),
            });
        }
    }

    Ok(mods)
}

fn read_mod_id_from_manifest(path: &Path) -> Option<String> {
    let contents = fs::read_to_string(path).ok()?;
    let value = serde_json::from_str::<Value>(contents.trim_start_matches('\u{feff}')).ok()?;
    value
        .get("id")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .map(ToOwned::to_owned)
}

/// Root under which to look for STS2 `settings.save` files.
///
/// For an instance (`game.userDir` set via `--instance`) this is the instance's
/// Godot user-dir, so mod load-order writes target that instance and never
/// clobber the shared install's settings or another instance. Otherwise it is
/// the host's default STS2 data root (see [`crate::host_paths`]).
fn sts2_settings_root(config: &AppConfig) -> Option<PathBuf> {
    if let Some(user_dir) = config.game.user_dir.as_deref() {
        // `game.userDir` is the instance `XDG_DATA_HOME`; the game writes
        // `user://` into its `SlayTheSpire2` subdir (see STS2_USER_DATA_DIR_NAME).
        return Some(instance_game_data_dir(&resolve_runtime_path(user_dir)));
    }
    crate::host_paths::sts2_user_data_root()
}

fn settings_save_candidates_for(config: &AppConfig) -> Vec<PathBuf> {
    let Some(root) = sts2_settings_root(config) else {
        return Vec::new();
    };
    let mut paths = Vec::new();
    collect_settings_save_candidates(&root, 0, &mut paths);
    paths.sort();
    paths.dedup();
    paths
}

/// Directories under the shared user-data dir that are large, machine-specific,
/// or regenerable and must NOT be seeded into an instance.
const SEED_SKIP_DIR_NAMES: &[&str] = &["shader_cache", "sentry", "logs", "resource-cache"];

/// Outcome of the `instances.symlinkUserDataDirs` pass for one instance.
#[derive(Debug, Default)]
struct SharedUserDataLinks {
    /// Validated relative paths the copy pass must leave alone, whether or not
    /// the link itself could be established.
    reserved: Vec<PathBuf>,
    /// Relative paths that are now symlinks into the shared user-data dir.
    linked: Vec<String>,
    /// Absolute instance paths still holding a real copy, left untouched.
    blocked: Vec<String>,
    /// Configured entries rejected by [`normalize_user_data_rel_path`].
    invalid: Vec<String>,
}

impl SharedUserDataLinks {
    fn report(&self) -> Option<Value> {
        if self.linked.is_empty() && self.blocked.is_empty() && self.invalid.is_empty() {
            return None;
        }
        Some(json!({
            "linked": self.linked,
            "notLinkedExistingCopy": self.blocked,
            "invalid": self.invalid,
        }))
    }
}

/// Normalize a configured `instances.symlinkUserDataDirs` entry into a path
/// relative to the shared user-data root. Rejects absolute paths and `..` so a
/// configured entry can never address anything outside that root.
#[cfg(unix)]
fn normalize_user_data_rel_path(raw: &str) -> Option<PathBuf> {
    let mut normalized = PathBuf::new();
    for component in Path::new(raw.trim()).components() {
        match component {
            Component::Normal(part) => normalized.push(part),
            Component::CurDir => continue,
            Component::ParentDir | Component::RootDir | Component::Prefix(_) => return None,
        }
    }
    (!normalized.as_os_str().is_empty()).then_some(normalized)
}

/// Point the configured instance paths at the shared user-data dir instead of
/// letting the copy pass duplicate them, so several instances share one big
/// mod cache (see `instances.symlinkUserDataDirs`).
///
/// Missing shared sources are created so the link is established from the start
/// rather than after the instance has grown its own copy. A path already holding
/// a real directory in the instance is left untouched and reported — instance
/// data is never deleted here.
#[cfg(unix)]
fn ensure_shared_user_data_links(
    shared_root: &Path,
    instance_root: &Path,
    configured: &[String],
    command_name: &str,
) -> Result<SharedUserDataLinks, AppError> {
    use crate::instance::{SymlinkOutcome, ensure_symlink};

    let mut links = SharedUserDataLinks::default();
    for raw in configured {
        let Some(rel) = normalize_user_data_rel_path(raw) else {
            if !raw.trim().is_empty() {
                links.invalid.push(raw.clone());
            }
            continue;
        };
        if links.reserved.contains(&rel) {
            continue;
        }
        links.reserved.push(rel.clone());

        let src = shared_root.join(&rel);
        let dest = instance_root.join(&rel);
        if !src.exists() {
            fs::create_dir_all(&src).map_err(io_lifecycle_error(command_name))?;
        }
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent).map_err(io_lifecycle_error(command_name))?;
        }
        match ensure_symlink(&src, &dest)? {
            SymlinkOutcome::Linked | SymlinkOutcome::AlreadyLinked => {
                links.linked.push(rel.display().to_string());
            }
            SymlinkOutcome::BlockedByExisting => links.blocked.push(dest.display().to_string()),
        }
    }
    Ok(links)
}

/// Symlink mirroring is unix-only, and so is instance isolation itself, so
/// reserve nothing and let the copy pass behave exactly as it did before.
#[cfg(not(unix))]
fn ensure_shared_user_data_links(
    _shared_root: &Path,
    _instance_root: &Path,
    _configured: &[String],
    _command_name: &str,
) -> Result<SharedUserDataLinks, AppError> {
    Ok(SharedUserDataLinks::default())
}

/// On first launch of an instance the per-instance user-dir has no
/// `settings.save` yet, so the bridge mod may not be enabled. Seed it from the
/// shared install's `settings.save` files (which already enable the bridge),
/// each copied at the same relative subpath.
///
/// All shared profiles are copied (e.g. both `default/<n>` and
/// `steam/<id>`) because only one of them carries `mod_settings.mod_list`, and
/// which profile the game uses depends on Steam login state. Files already
/// present in the instance user-dir are left untouched. No-op when not an
/// instance or when no shared template is available.
///
/// Paths listed in `instances.symlinkUserDataDirs` are symlinked to the shared
/// dir instead of copied. That pass runs on every invocation rather than once,
/// so adding an entry later still takes effect on an already-seeded instance.
fn seed_instance_user_data_if_missing(
    config: &AppConfig,
    command_name: &str,
) -> Result<Option<Value>, AppError> {
    let Some(user_dir) = config.game.user_dir.as_deref() else {
        return Ok(None);
    };
    // Seed into the `SlayTheSpire2` subdir the XDG-redirected game actually reads.
    let instance_root = instance_game_data_dir(&resolve_runtime_path(user_dir));

    let Some(shared_root) = crate::host_paths::sts2_user_data_root() else {
        return Ok(None);
    };
    if !shared_root.is_dir() {
        return Ok(None);
    }

    let links = ensure_shared_user_data_links(
        &shared_root,
        &instance_root,
        &config.instances.symlink_user_data_dirs,
        command_name,
    )?;

    // Seed exactly once; afterwards the instance owns its diverged state.
    let sentinel = instance_root.join(".spirectl-seeded");
    if sentinel.exists() {
        return Ok(links.report().map(|report| json!({
            "symlinkedUserDataDirs": report,
        })));
    }

    let mut count = 0usize;
    seed_copy_tree(
        &shared_root,
        &instance_root,
        Path::new(""),
        &links.reserved,
        &mut count,
        command_name,
    )?;

    fs::create_dir_all(&instance_root).map_err(io_lifecycle_error(command_name))?;
    fs::write(&sentinel, b"").map_err(io_lifecycle_error(command_name))?;

    let link_report = links.report();
    if count == 0 && link_report.is_none() {
        return Ok(None);
    }
    let mut payload = json!({
        "seededFrom": shared_root.display().to_string(),
        "seededTo": instance_root.display().to_string(),
        "fileCount": count,
    });
    if let Some(report) = link_report {
        payload["symlinkedUserDataDirs"] = report;
    }
    Ok(Some(payload))
}

/// Recursively copy `src` into `dest` (copy-if-missing), skipping large or
/// regenerable directories ([`SEED_SKIP_DIR_NAMES`]), paths reserved by
/// `instances.symlinkUserDataDirs`, and our own settings.save backups.
/// Best-effort over unreadable subtrees; copies regular files only.
///
/// `rel` is the path of `src` relative to the seed root, used to match the
/// reserved paths.
fn seed_copy_tree(
    src: &Path,
    dest: &Path,
    rel: &Path,
    reserved: &[PathBuf],
    count: &mut usize,
    command_name: &str,
) -> Result<(), AppError> {
    let Ok(entries) = fs::read_dir(src) else {
        return Ok(());
    };
    for entry in entries.filter_map(Result::ok) {
        let name = entry.file_name();
        let src_path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            if SEED_SKIP_DIR_NAMES.iter().any(|skip| name == *skip) {
                continue;
            }
            let child_rel = rel.join(&name);
            // Never descend into a reserved path: its destination is a symlink
            // into the shared dir, so copying there would write through the link
            // into the user's real user-data dir.
            if reserved.contains(&child_rel) {
                continue;
            }
            seed_copy_tree(
                &src_path,
                &dest.join(&name),
                &child_rel,
                reserved,
                count,
                command_name,
            )?;
        } else if file_type.is_file() {
            // Don't clone the (potentially hundreds of) settings.save backups.
            if name.to_string_lossy().contains(".spirectl-backup-") {
                continue;
            }
            let dest_path = dest.join(&name);
            if dest_path.exists() {
                continue;
            }
            if let Some(parent) = dest_path.parent() {
                fs::create_dir_all(parent).map_err(io_lifecycle_error(command_name))?;
            }
            fs::copy(&src_path, &dest_path).map_err(io_lifecycle_error(command_name))?;
            *count += 1;
        }
    }
    Ok(())
}

/// STS2's `project.godot` sets `application/config/use_custom_user_dir` with this
/// `custom_user_dir_name`, so the engine resolves `user://` to
/// `<data_path>/SlayTheSpire2` and IGNORES the injected `--user-dir`. Per-instance
/// isolation is therefore achieved by pointing `XDG_DATA_HOME` at the instance dir
/// (the engine's `get_data_path()` honors it); the game's user data then lives in
/// this subdir of the instance dir.
pub(crate) use crate::host_paths::STS2_USER_DATA_DIR_NAME;

/// The directory the XDG-redirected game actually writes `user://` to for an
/// instance whose `XDG_DATA_HOME` is `xdg_root`.
pub(crate) fn instance_game_data_dir(xdg_root: &Path) -> PathBuf {
    xdg_root.join(STS2_USER_DATA_DIR_NAME)
}

/// Point an instance's `XDG_DATA_HOME` at `xdg_root` so the game writes
/// `user://` to `<xdg_root>/SlayTheSpire2` (per-instance) instead of the shared
/// `~/.local/share/SlayTheSpire2`. The engine, Steam, Vulkan, fonts, etc. read
/// other user data through `XDG_DATA_HOME` too, so symlink every sibling entry of
/// the real data dir into `xdg_root`, leaving the game's own `SlayTheSpire2`
/// subdir real and isolated. Idempotent; best-effort (never fails the launch).
#[cfg(target_os = "linux")]
pub(crate) fn ensure_xdg_data_mirror(xdg_root: &Path) {
    let _ = fs::create_dir_all(xdg_root);
    let Some(home) = std::env::var_os("HOME") else {
        return;
    };
    let real_root = PathBuf::from(home).join(".local/share");
    let Ok(entries) = fs::read_dir(&real_root) else {
        return;
    };
    for entry in entries.filter_map(Result::ok) {
        let name = entry.file_name();
        // The game's own data dir must stay real and per-instance.
        if name == STS2_USER_DATA_DIR_NAME {
            continue;
        }
        let dest = xdg_root.join(&name);
        // Leave anything already present (a prior symlink, or instance state).
        if fs::symlink_metadata(&dest).is_ok() {
            continue;
        }
        let _ = std::os::unix::fs::symlink(entry.path(), &dest);
    }
}

fn collect_settings_save_candidates(root: &Path, depth: usize, paths: &mut Vec<PathBuf>) {
    if depth > 4 {
        return;
    }
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.filter_map(|entry| entry.ok()) {
        let path = entry.path();
        if path.is_file() && path.file_name() == Some(OsStr::new("settings.save")) {
            paths.push(path);
        } else if path.is_dir() {
            collect_settings_save_candidates(&path, depth + 1, paths);
        }
    }
}

fn update_settings_mod_load_order(
    command_name: &str,
    settings_path: &Path,
    dependent_mod_ids: &[String],
) -> Result<Value, AppError> {
    let contents = fs::read_to_string(settings_path).map_err(io_lifecycle_error(command_name))?;
    let mut settings = serde_json::from_str::<Value>(&contents).map_err(|source| {
        lifecycle_error(
            4,
            "settings_save_parse_failed",
            command_name,
            &format!(
                "failed to parse STS2 settings file '{}': {source}",
                settings_path.display()
            ),
        )
    })?;

    let Some(mod_settings) = settings
        .get_mut("mod_settings")
        .and_then(Value::as_object_mut)
    else {
        return Ok(json!({
            "path": settings_path.display().to_string(),
            "status": "skipped",
            "reason": "mod_settings_missing",
            "changed": false
        }));
    };

    let Some(mod_list) = mod_settings
        .get_mut("mod_list")
        .and_then(Value::as_array_mut)
    else {
        return Ok(json!({
            "path": settings_path.display().to_string(),
            "status": "skipped",
            "reason": "mod_list_missing",
            "changed": false
        }));
    };

    let before_order = mod_list_ids(mod_list);
    let changed = reorder_mod_list_for_bridge(mod_list, dependent_mod_ids);
    let after_order = mod_list_ids(mod_list);
    let mut backup_path = None;

    if changed {
        let updated = serde_json::to_string_pretty(&settings)
            .map(|contents| format!("{contents}\n"))
            .map_err(|source| {
                lifecycle_error(
                    4,
                    "settings_save_serialize_failed",
                    command_name,
                    &format!(
                        "failed to serialize STS2 settings file '{}': {source}",
                        settings_path.display()
                    ),
                )
            })?;
        let backup = backup_settings_save(command_name, settings_path)?;
        backup_path = Some(backup.display().to_string());
        fs::write(settings_path, updated).map_err(io_lifecycle_error(command_name))?;
    }

    Ok(json!({
        "path": settings_path.display().to_string(),
        "status": if changed { "updated" } else { "unchanged" },
        "changed": changed,
        "backupPath": backup_path,
        "beforeOrder": before_order,
        "afterOrder": after_order
    }))
}

fn backup_settings_save(command_name: &str, settings_path: &Path) -> Result<PathBuf, AppError> {
    let timestamp = UNIX_EPOCH
        .elapsed()
        .map_or(0, |duration| duration.as_secs());
    let parent = settings_path.parent().unwrap_or_else(|| Path::new("."));
    let base_name = settings_path
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or("settings.save");

    for attempt in 0..100 {
        let suffix = if attempt == 0 {
            format!("spirectl-backup-{timestamp}")
        } else {
            format!("spirectl-backup-{timestamp}-{attempt}")
        };
        let backup_path = parent.join(format!("{base_name}.{suffix}"));
        if !backup_path.exists() {
            fs::copy(settings_path, &backup_path).map_err(io_lifecycle_error(command_name))?;
            return Ok(backup_path);
        }
    }

    Err(lifecycle_error(
        4,
        "settings_save_backup_failed",
        command_name,
        &format!(
            "failed to choose a unique backup path for '{}'",
            settings_path.display()
        ),
    ))
}

fn reorder_mod_list_for_bridge(mod_list: &mut Vec<Value>, dependent_mod_ids: &[String]) -> bool {
    let first_dependent_index = mod_list.iter().position(|entry| {
        mod_entry_id(entry)
            .is_some_and(|id| dependent_mod_ids.iter().any(|candidate| candidate == id))
    });

    let Some(target_index) = first_dependent_index else {
        return false;
    };

    let bridge_index = mod_list
        .iter()
        .position(|entry| mod_entry_id(entry) == Some(DEPLOYED_MOD_DIR_NAME));

    if bridge_index.is_some_and(|index| index < target_index) {
        return false;
    }

    let bridge_entry = match bridge_index {
        Some(index) => mod_list.remove(index),
        None => default_bridge_mod_list_entry(),
    };
    let insertion_index = mod_list
        .iter()
        .position(|entry| {
            mod_entry_id(entry)
                .is_some_and(|id| dependent_mod_ids.iter().any(|candidate| candidate == id))
        })
        .unwrap_or(mod_list.len());
    mod_list.insert(insertion_index, bridge_entry);
    true
}

fn default_bridge_mod_list_entry() -> Value {
    let mut entry = Map::new();
    entry.insert(
        "id".to_string(),
        Value::String(DEPLOYED_MOD_DIR_NAME.to_string()),
    );
    entry.insert("is_enabled".to_string(), Value::Bool(true));
    entry.insert(
        "source".to_string(),
        Value::String("mods_directory".to_string()),
    );
    Value::Object(entry)
}

fn mod_entry_id(entry: &Value) -> Option<&str> {
    entry.get("id").and_then(Value::as_str)
}

fn mod_list_ids(mod_list: &[Value]) -> Vec<String> {
    mod_list
        .iter()
        .filter_map(mod_entry_id)
        .map(ToOwned::to_owned)
        .collect()
}

fn absolute_artifacts_dir(config: &AppConfig) -> PathBuf {
    let configured = PathBuf::from(&config.artifacts.dir);
    if configured.is_absolute() {
        configured
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(configured))
            .unwrap_or_else(|_| PathBuf::from(&config.artifacts.dir))
    }
}

/// Read a child stream to completion, optionally echoing it as it arrives.
///
/// `.output()` cannot do both: it buffers everything until exit, which is why a
/// multi-minute bridge build looked like a hang. The buffer is still kept, so
/// `command_output_error` reports the same payload as before.
fn drain_child_stream<R>(reader: R, tee_to_stderr: bool) -> thread::JoinHandle<Vec<u8>>
where
    R: std::io::Read + Send + 'static,
{
    thread::spawn(move || {
        let mut reader = std::io::BufReader::new(reader);
        let mut collected = Vec::new();
        let mut line = Vec::new();
        loop {
            line.clear();
            match std::io::BufRead::read_until(&mut reader, b'\n', &mut line) {
                Ok(0) | Err(_) => break,
                Ok(_) => {
                    if tee_to_stderr {
                        let mut stderr = std::io::stderr().lock();
                        let _ = std::io::Write::write_all(&mut stderr, &line);
                        let _ = std::io::Write::flush(&mut stderr);
                    }
                    collected.extend_from_slice(&line);
                }
            }
        }
        collected
    })
}

fn run_dotnet_command(command_name: &str, args: &[String]) -> Result<(), AppError> {
    let invocation_error = |source: std::io::Error| {
        lifecycle_error(
            4,
            "tool_invocation_failed",
            command_name,
            &format!("failed to invoke dotnet: {source}"),
        )
    };

    let mut child = Command::new("dotnet")
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(invocation_error)?;

    // Under --progress the build's own diagnostics stream through to stderr
    // while it runs; stdout stays reserved for the command result.
    let tee = crate::progress::is_enabled();
    let stdout = child
        .stdout
        .take()
        .map(|stream| drain_child_stream(stream, false));
    let stderr = child
        .stderr
        .take()
        .map(|stream| drain_child_stream(stream, tee));
    let status = child.wait().map_err(invocation_error)?;
    let output = std::process::Output {
        status,
        stdout: stdout
            .map(|handle| handle.join().unwrap_or_default())
            .unwrap_or_default(),
        stderr: stderr
            .map(|handle| handle.join().unwrap_or_default())
            .unwrap_or_default(),
    };

    if !output.status.success() {
        let mut argv = vec!["dotnet".to_string()];
        argv.extend(args.iter().cloned());
        return Err(command_output_error(
            "tool_invocation_failed",
            command_name,
            &argv,
            &output,
        ));
    }

    Ok(())
}

fn resolve_repo_root(command_name: &str) -> Result<PathBuf, AppError> {
    let current_dir = std::env::current_dir().map_err(|source| {
        lifecycle_error(
            4,
            "tool_invocation_failed",
            command_name,
            &format!("failed to resolve current working directory: {source}"),
        )
    })?;

    for base in [
        Some(current_dir),
        std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(Path::to_path_buf)),
    ]
    .into_iter()
    .flatten()
    {
        for ancestor in base.ancestors() {
            if ancestor.join("bridge-mod/Directory.Build.props").is_file() {
                return Ok(ancestor.to_path_buf());
            }
        }
    }

    Err(lifecycle_error(
        2,
        "invalid_lifecycle_config",
        command_name,
        "could not locate the repository root needed to build bridge artifacts",
    ))
}

fn expected_bridge_semver(command_name: &str) -> Result<&'static str, AppError> {
    bridge::bridge_version()
        .strip_prefix("spirectl-bridge/")
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            lifecycle_error(
                4,
                "tool_invocation_failed",
                command_name,
                "embedded CLI bridge version is missing the expected 'spirectl-bridge/<semver>' shape",
            )
        })
}

fn write_bridge_manifest(
    command_name: &str,
    target_dir: &Path,
    semver: &str,
    content_hash: &str,
) -> Result<(), AppError> {
    let source_freshness = bridge_manifest_source_freshness_json();
    let contents = serde_json::to_string_pretty(&json!({
        "id": DEPLOYED_MOD_DIR_NAME,
        "version": semver,
        "has_pck": false,
        "has_dll": true,
        "affects_gameplay": false,
        "buildIdentity": {
            "bridgeSemVer": semver,
            "bridgeVersion": format!("spirectl-bridge/{semver}"),
            "assemblyInformationalVersion": semver,
            // Identity of the staged payload, so a redeploy can tell "same
            // bridge" from "same version, different build".
            "contentHash": content_hash,
            "sourceFreshness": source_freshness
        }
    }))
    .map(|contents| format!("{contents}\n"))
    .map_err(|error| {
        let message = format!("failed to serialize bridge manifest: {error}");
        lifecycle_error(4, "tool_invocation_failed", command_name, &message)
    })?;
    fs::write(target_dir.join(BRIDGE_MANIFEST_NAME), contents)
        .map_err(io_lifecycle_error(command_name))
}

fn bridge_manifest_source_freshness_json() -> Value {
    let repo_root = match resolve_repo_root("game install-bridge") {
        Ok(root) => root,
        Err(_) => {
            return json!({
                "status": "unknown",
                "newestModifiedUnixSeconds": Value::Null,
                "newestPath": Value::Null
            });
        }
    };
    let roots = [
        repo_root.join("bridge-mod/src"),
        repo_root.join("bridge-mod/Directory.Build.props"),
        repo_root.join("proto/spirectl"),
    ];
    let mut newest: Option<(u64, PathBuf)> = None;
    for root in roots {
        collect_bridge_manifest_source_freshness(&root, &mut newest);
    }
    match newest {
        Some((seconds, path)) => json!({
            "status": "known",
            "newestModifiedUnixSeconds": seconds,
            "newestPath": path.display().to_string()
        }),
        None => json!({
            "status": "unknown",
            "newestModifiedUnixSeconds": Value::Null,
            "newestPath": Value::Null
        }),
    }
}

fn collect_bridge_manifest_source_freshness(path: &Path, newest: &mut Option<(u64, PathBuf)>) {
    let Ok(metadata) = fs::metadata(path) else {
        return;
    };
    if metadata.is_dir() {
        let Ok(entries) = fs::read_dir(path) else {
            return;
        };
        for entry in entries.filter_map(Result::ok) {
            let child = entry.path();
            if child
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| matches!(name, "bin" | "obj" | "target"))
            {
                continue;
            }
            collect_bridge_manifest_source_freshness(&child, newest);
        }
        return;
    }
    if !matches!(
        path.extension().and_then(|extension| extension.to_str()),
        Some("cs" | "csproj" | "props" | "proto")
    ) {
        return;
    }
    let Some(seconds) = metadata
        .modified()
        .ok()
        .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .map(|duration| duration.as_secs())
    else {
        return;
    };
    if newest
        .as_ref()
        .is_none_or(|(current_seconds, _)| seconds > *current_seconds)
    {
        *newest = Some((seconds, path.to_path_buf()));
    }
}

fn copy_if_present(
    command_name: &str,
    source_path: &Path,
    target_dir: &Path,
) -> Result<(), AppError> {
    if source_path.is_file() {
        let file_name = source_path
            .file_name()
            .expect("copy_if_present only called with files");
        fs::copy(source_path, target_dir.join(file_name))
            .map_err(io_lifecycle_error(command_name))?;
    }

    Ok(())
}

fn stage_publish_artifacts(
    command_name: &str,
    publish_dir: &Path,
    target_dir: &Path,
) -> Result<(), AppError> {
    let mut entries = fs::read_dir(publish_dir)
        .map_err(io_lifecycle_error(command_name))?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_file()
                && matches!(
                    path.extension().and_then(OsStr::to_str),
                    Some("dll") | Some("pdb")
                )
        })
        .collect::<Vec<_>>();
    entries.sort();

    for source in entries {
        let file_name = source.file_name().expect("publish entry file name");
        fs::copy(&source, target_dir.join(file_name)).map_err(io_lifecycle_error(command_name))?;
    }

    Ok(())
}

fn inspect_deployed_bridge_layout(
    target_dir: &Path,
    expected_version: &str,
) -> Result<(), BridgeLayoutIssue> {
    let required = [
        BRIDGE_MANIFEST_NAME,
        "spirectlbridge.dll",
        "Spirectl.BridgeMod.Sts2Host.dll",
        "Spirectl.BridgeMod.dll",
    ];
    for name in required {
        if !target_dir.join(name).is_file() {
            return Err(BridgeLayoutIssue::MissingFile(name.to_string()));
        }
    }

    let mut unexpected_json = fs::read_dir(target_dir)
        .map_err(|source| {
            BridgeLayoutIssue::InvalidManifest(format!(
                "failed to inspect '{}' for deployed bridge artifacts: {source}",
                target_dir.display()
            ))
        })?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_file()
                && path.extension() == Some(OsStr::new("json"))
                && path.file_name() != Some(OsStr::new(BRIDGE_MANIFEST_NAME))
        })
        .collect::<Vec<_>>();
    unexpected_json.sort();
    if let Some(path) = unexpected_json.first() {
        return Err(BridgeLayoutIssue::UnexpectedJson(
            path.display().to_string(),
        ));
    }

    let manifest_path = target_dir.join(BRIDGE_MANIFEST_NAME);
    let manifest = fs::read_to_string(&manifest_path).map_err(|source| {
        BridgeLayoutIssue::InvalidManifest(format!(
            "failed to read '{}': {source}",
            manifest_path.display()
        ))
    })?;
    let manifest_json = serde_json::from_str::<Value>(&manifest).map_err(|source| {
        BridgeLayoutIssue::InvalidManifest(format!(
            "failed to parse '{}': {source}",
            manifest_path.display()
        ))
    })?;
    let deployed_version = manifest_json
        .get("version")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            BridgeLayoutIssue::InvalidManifest(format!(
                "manifest '{}' is missing a non-empty string version",
                manifest_path.display()
            ))
        })?;
    if deployed_version != expected_version {
        return Err(BridgeLayoutIssue::VersionMismatch {
            expected: expected_version.to_string(),
            found: deployed_version.to_string(),
        });
    }

    Ok(())
}

fn validate_deployed_bridge_layout(
    command_name: &str,
    target_dir: &Path,
    expected_version: &str,
) -> Result<(), AppError> {
    inspect_deployed_bridge_layout(target_dir, expected_version).map_err(|issue| {
        lifecycle_error(
            2,
            "bridge_layout_invalid",
            command_name,
            &issue.message(target_dir),
        )
    })
}

fn ensure_launch_bridge_layout(
    context: AppContext<'_>,
    layout: &ResolvedLiveBridgeLayout,
    command_name: &str,
) -> Result<(), AppError> {
    let expected_version = expected_bridge_semver(command_name)?;
    match inspect_deployed_bridge_layout(&layout.mod_dir, expected_version) {
        Ok(()) => Ok(()),
        Err(issue) => {
            cache_lifecycle_paths(context, layout, true);
            deploy_bridge(
                command_name,
                context.config,
                layout,
                BridgeDeployOptions::default(),
            )
            .map(|_| ())
                .map_err(|source| auto_install_error(command_name, layout, &issue, source))
        }
    }
}

fn auto_install_error(
    command_name: &str,
    layout: &ResolvedLiveBridgeLayout,
    issue: &BridgeLayoutIssue,
    source: AppError,
) -> AppError {
    AppError {
        exit_code: source.exit_code,
        payload: json!({
            "error": {
                "code": "bridge_auto_install_failed",
                "command": command_name,
                "message": format!(
                    "failed to repair the deployed bridge at '{}' after detecting a {} issue",
                    layout.mod_dir.display(),
                    issue.kind()
                ),
                "bridgeIssue": issue.to_json(),
                "cause": source.payload["error"].clone()
            }
        }),
    }
}

