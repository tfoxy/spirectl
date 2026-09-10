use super::*;

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct AppConfig {
    pub game: GameConfig,
    pub mode: ModeConfig,
    pub transport: TransportConfig,
    pub tools: ToolsConfig,
    pub toolchain: ToolchainConfig,
    pub project: ProjectConfig,
    pub test: TestConfig,
    pub service: ServiceConfig,
    pub artifacts: ArtifactsConfig,
    pub instances: InstancesConfig,
    pub usage_tracking: UsageTrackingConfig,
    pub visual_validation: VisualValidationConfig,
}

impl AppConfig {
    pub fn load(path: Option<&Path>) -> Result<Self, AppError> {
        let base_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
        Self::load_from_dir(path, &base_dir)
    }

    pub(crate) fn load_from_dir(path: Option<&Path>, base_dir: &Path) -> Result<Self, AppError> {
        if let Some(path) = path {
            return Self::load_required(&resolve_config_path(base_dir, path));
        }

        let resolved = ResolvedConfigDir::resolve(base_dir)?;
        Self::load_layered(&[
            resolved.dir.join(CHECKED_IN_CONFIG_FILE_NAME),
            resolved.dir.join(LOCAL_CONFIG_FILE_NAME),
        ])
    }

    fn load_required(path: &Path) -> Result<Self, AppError> {
        let value = read_yaml_value(path)?;
        deserialize_app_config(value, path)
    }

    fn load_layered(paths: &[PathBuf]) -> Result<Self, AppError> {
        let mut merged = serde_yaml::to_value(Self::default()).expect("default config serializes");
        let mut last_loaded_path: Option<&Path> = None;

        for path in paths {
            if !path.exists() {
                continue;
            }

            let overlay = read_yaml_value(path)?;
            merge_yaml_value(&mut merged, overlay);
            last_loaded_path = Some(path.as_path());
        }

        match last_loaded_path {
            Some(path) => deserialize_app_config(merged, path),
            None => Ok(Self::default()),
        }
    }

    pub(crate) fn with_yaml_overlay(&self, overlay: YamlValue) -> Result<Self, AppError> {
        let mut merged = serde_yaml::to_value(self).expect("config serializes");
        merge_yaml_value(&mut merged, overlay);
        deserialize_app_config(merged, Path::new("<test-profile>"))
    }
}

/// Environment override for the directory the default config stack is read from.
pub(crate) const CONFIG_DIR_ENV: &str = "SPIRECTL_CONFIG_DIR";
pub(crate) const CHECKED_IN_CONFIG_FILE_NAME: &str = "sts2.config.yaml";
pub(crate) const LOCAL_CONFIG_FILE_NAME: &str = "sts2.local.yaml";

/// How the directory holding the default config stack was chosen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ConfigDirSource {
    /// `--config <file>` was passed; the directory is that file's parent.
    ExplicitConfig,
    /// `SPIRECTL_CONFIG_DIR` pointed at the directory.
    Environment,
    /// Found by walking up from the working directory.
    Discovered,
    /// Nothing was found; the working directory is used unchanged.
    CurrentDir,
}

impl ConfigDirSource {
    pub(crate) fn name(self) -> &'static str {
        match self {
            Self::ExplicitConfig => "explicit-config",
            Self::Environment => "environment",
            Self::Discovered => "discovered",
            Self::CurrentDir => "current-dir",
        }
    }
}

#[derive(Debug, Clone)]
pub(crate) struct ResolvedConfigDir {
    pub(crate) dir: PathBuf,
    pub(crate) source: ConfigDirSource,
}

impl ResolvedConfigDir {
    /// Resolve the directory the default (`sts2.config.yaml` + `sts2.local.yaml`) stack is read
    /// from. Precedence: `SPIRECTL_CONFIG_DIR` > nearest ancestor of `base_dir` holding either
    /// config file (stopping at a repository boundary) > `base_dir` itself.
    pub(crate) fn resolve(base_dir: &Path) -> Result<Self, AppError> {
        if let Some(raw) = std::env::var_os(CONFIG_DIR_ENV) {
            let raw = PathBuf::from(raw);
            if !raw.as_os_str().is_empty() {
                let dir = if raw.is_absolute() {
                    raw
                } else {
                    base_dir.join(raw)
                };
                if !config_dir_holds_config(&dir) {
                    return Err(AppError::config_dir_missing(&dir));
                }
                return Ok(Self {
                    dir,
                    source: ConfigDirSource::Environment,
                });
            }
        }

        if let Some(dir) = discover_config_dir(base_dir) {
            return Ok(Self {
                dir,
                source: ConfigDirSource::Discovered,
            });
        }

        Ok(Self {
            dir: base_dir.to_path_buf(),
            source: ConfigDirSource::CurrentDir,
        })
    }
}

pub(crate) fn config_dir_holds_config(dir: &Path) -> bool {
    dir.join(CHECKED_IN_CONFIG_FILE_NAME).is_file() || dir.join(LOCAL_CONFIG_FILE_NAME).is_file()
}

/// Walk `base_dir` and its ancestors looking for a config directory. A directory holding `.git`
/// but no config file ends the walk: config below a checkout never leaks in from outside it.
fn discover_config_dir(base_dir: &Path) -> Option<PathBuf> {
    for dir in base_dir.ancestors() {
        if dir.as_os_str().is_empty() {
            break;
        }
        if config_dir_holds_config(dir) {
            return Some(dir.to_path_buf());
        }
        if dir.join(".git").exists() {
            return None;
        }
    }
    None
}

#[derive(Debug, Clone)]
pub(crate) struct ConfigOrigin {
    pub(crate) uses_default_stack: bool,
    pub(crate) base_dir: PathBuf,
    pub(crate) config_dir: PathBuf,
    pub(crate) local_config_path: PathBuf,
    pub(crate) config_dir_source: ConfigDirSource,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ConfigValueSource {
    Default,
    ExplicitConfig,
    CheckedInConfig,
    LocalConfig,
}

impl ConfigValueSource {
    pub(crate) fn name(self) -> &'static str {
        match self {
            Self::Default => "default",
            Self::ExplicitConfig => "explicit-config",
            Self::CheckedInConfig => "checked-in-config",
            Self::LocalConfig => "local-config",
        }
    }
}

#[derive(Debug, Clone)]
pub(crate) struct ConfigProvenance {
    pub(crate) dotnet_tools_path: ConfigValueSource,
    pub(crate) gdre_path: ConfigValueSource,
    pub(crate) toolchain_dir: ConfigValueSource,
    pub(crate) shared_cache_dir: ConfigValueSource,
    pub(crate) profiles_file: ConfigValueSource,
    pub(crate) game_path: ConfigValueSource,
    pub(crate) assemblies_dir: ConfigValueSource,
    pub(crate) resources_dir: ConfigValueSource,
    pub(crate) mods_dir: ConfigValueSource,
    pub(crate) instances_dir: ConfigValueSource,
    pub(crate) artifacts_dir: ConfigValueSource,
}

impl Default for ConfigProvenance {
    fn default() -> Self {
        Self {
            dotnet_tools_path: ConfigValueSource::Default,
            gdre_path: ConfigValueSource::Default,
            toolchain_dir: ConfigValueSource::Default,
            shared_cache_dir: ConfigValueSource::Default,
            profiles_file: ConfigValueSource::Default,
            game_path: ConfigValueSource::Default,
            assemblies_dir: ConfigValueSource::Default,
            resources_dir: ConfigValueSource::Default,
            mods_dir: ConfigValueSource::Default,
            instances_dir: ConfigValueSource::Default,
            artifacts_dir: ConfigValueSource::Default,
        }
    }
}

#[derive(Debug, Clone)]
pub(crate) struct LoadedConfig {
    pub(crate) config: AppConfig,
    pub(crate) origin: ConfigOrigin,
    pub(crate) provenance: ConfigProvenance,
}

impl LoadedConfig {
    pub(crate) fn load(path: Option<&Path>) -> Result<Self, AppError> {
        let base_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
        Self::load_from_dir(path, &base_dir)
    }

    pub(crate) fn load_from_dir(path: Option<&Path>, base_dir: &Path) -> Result<Self, AppError> {
        let origin = match path {
            // An explicit `--config` file anchors only the config directory: the working directory
            // still anchors toolchain/profile lookups, and no local cache is written.
            Some(candidate) => ConfigOrigin {
                uses_default_stack: false,
                base_dir: base_dir.to_path_buf(),
                config_dir: resolve_config_path(base_dir, candidate)
                    .parent()
                    .map(Path::to_path_buf)
                    .unwrap_or_else(|| base_dir.to_path_buf()),
                local_config_path: base_dir.join(LOCAL_CONFIG_FILE_NAME),
                config_dir_source: ConfigDirSource::ExplicitConfig,
            },
            // The default stack may live above the working directory; every origin path follows
            // the resolved directory so relative config values and the local cache write agree.
            None => {
                let resolved = ResolvedConfigDir::resolve(base_dir)?;
                ConfigOrigin {
                    uses_default_stack: true,
                    base_dir: resolved.dir.clone(),
                    config_dir: resolved.dir.clone(),
                    local_config_path: resolved.dir.join(LOCAL_CONFIG_FILE_NAME),
                    config_dir_source: resolved.source,
                }
            }
        };
        let (config, provenance) = load_app_config_with_provenance(path, base_dir, &origin)?;
        Ok(Self {
            config,
            origin,
            provenance,
        })
    }
}

fn load_app_config_with_provenance(
    path: Option<&Path>,
    base_dir: &Path,
    origin: &ConfigOrigin,
) -> Result<(AppConfig, ConfigProvenance), AppError> {
    if let Some(path) = path {
        let resolved = resolve_config_path(base_dir, path);
        let value = read_yaml_value(&resolved)?;
        let config = deserialize_app_config(value.clone(), &resolved)?;
        let provenance =
            config_provenance_from_documents(&[(ConfigValueSource::ExplicitConfig, value)]);
        return Ok((config, provenance));
    }

    let mut merged = serde_yaml::to_value(AppConfig::default()).expect("default config serializes");
    let mut documents = Vec::new();
    for (source, path) in [
        (
            ConfigValueSource::CheckedInConfig,
            origin.config_dir.join(CHECKED_IN_CONFIG_FILE_NAME),
        ),
        (
            ConfigValueSource::LocalConfig,
            origin.local_config_path.clone(),
        ),
    ] {
        if !path.exists() {
            continue;
        }

        let overlay = read_yaml_value(&path)?;
        merge_yaml_value(&mut merged, overlay.clone());
        documents.push((source, overlay));
    }

    let config = deserialize_app_config(merged, &origin.local_config_path)?;
    let provenance = config_provenance_from_documents(&documents);
    Ok((config, provenance))
}

fn config_provenance_from_documents(
    documents: &[(ConfigValueSource, YamlValue)],
) -> ConfigProvenance {
    let mut provenance = ConfigProvenance::default();

    for (source, document) in documents.iter().rev() {
        if matches!(provenance.dotnet_tools_path, ConfigValueSource::Default)
            && yaml_string_present(document, &["tools", "dotnetToolsPath"])
        {
            provenance.dotnet_tools_path = *source;
        }
        if matches!(provenance.gdre_path, ConfigValueSource::Default)
            && yaml_string_present(document, &["tools", "gdrePath"])
        {
            provenance.gdre_path = *source;
        }
        if matches!(provenance.toolchain_dir, ConfigValueSource::Default)
            && yaml_string_present(document, &["toolchain", "dir"])
        {
            provenance.toolchain_dir = *source;
        }
        if matches!(provenance.shared_cache_dir, ConfigValueSource::Default)
            && yaml_string_present(document, &["toolchain", "sharedCacheDir"])
        {
            provenance.shared_cache_dir = *source;
        }
        if matches!(provenance.profiles_file, ConfigValueSource::Default)
            && yaml_string_present(document, &["project", "profilesFile"])
        {
            provenance.profiles_file = *source;
        }
        for (slot, path) in [
            (&mut provenance.game_path, ["game", "path"]),
            (&mut provenance.assemblies_dir, ["game", "assembliesDir"]),
            (&mut provenance.resources_dir, ["game", "resourcesDir"]),
            (&mut provenance.mods_dir, ["game", "modsDir"]),
            (&mut provenance.instances_dir, ["instances", "dir"]),
            (&mut provenance.artifacts_dir, ["artifacts", "dir"]),
        ] {
            if matches!(*slot, ConfigValueSource::Default) && yaml_string_present(document, &path) {
                *slot = *source;
            }
        }
    }

    provenance
}

fn yaml_string_present(value: &YamlValue, path: &[&str]) -> bool {
    yaml_value_at_path(value, path).is_some_and(YamlValue::is_string)
}

fn yaml_value_at_path<'a>(value: &'a YamlValue, path: &[&str]) -> Option<&'a YamlValue> {
    let mut current = value;
    for segment in path {
        let map = current.as_mapping()?;
        current = map.get(YamlValue::String((*segment).to_string()))?;
    }
    Some(current)
}

fn resolve_config_path(base_dir: &Path, path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        base_dir.join(path)
    }
}

fn read_yaml_value(path: &Path) -> Result<YamlValue, AppError> {
    let raw = std::fs::read_to_string(path).map_err(|source| AppError::config(path, source))?;
    serde_yaml::from_str(&raw).map_err(|source| AppError::config_parse(path, source))
}

fn deserialize_app_config(value: YamlValue, path: &Path) -> Result<AppConfig, AppError> {
    serde_yaml::from_value(value).map_err(|source| AppError::config_parse(path, source))
}

fn merge_yaml_value(base: &mut YamlValue, overlay: YamlValue) {
    match (base, overlay) {
        (YamlValue::Mapping(base_map), YamlValue::Mapping(overlay_map)) => {
            for (key, overlay_value) in overlay_map {
                match base_map.get_mut(&key) {
                    Some(base_value) => merge_yaml_value(base_value, overlay_value),
                    None => {
                        base_map.insert(key, overlay_value);
                    }
                }
            }
        }
        (base_value, overlay_value) => *base_value = overlay_value,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LocalGamePathCacheOutcome {
    Written { path: bool, assemblies_dir: bool },
    Unchanged,
}

pub(crate) fn maybe_cache_discovered_game_path(
    context: AppContext<'_>,
    game_path: Option<&Path>,
    used_discovered_path: bool,
) -> Option<String> {
    let assemblies_dir = game_path.and_then(|path| derive_assemblies_dir_from_game_path(path).ok());
    maybe_cache_resolved_local_paths(
        context,
        game_path,
        used_discovered_path,
        assemblies_dir.as_deref(),
        true,
    )
}

pub(crate) fn maybe_cache_resolved_local_paths(
    context: AppContext<'_>,
    game_path: Option<&Path>,
    used_discovered_path: bool,
    assemblies_dir: Option<&Path>,
    used_assemblies_dir: bool,
) -> Option<String> {
    debug_assert!(
        context
            .config_origin
            .local_config_path
            .starts_with(&context.config_origin.base_dir)
    );
    if !context.config_origin.uses_default_stack {
        return None;
    }

    let cache_game_path = used_discovered_path.then_some(game_path).flatten();
    let cache_assemblies_dir = used_assemblies_dir.then_some(assemblies_dir).flatten();
    if cache_game_path.is_none() && cache_assemblies_dir.is_none() {
        return None;
    }

    match cache_game_path_to_local_config(
        cache_game_path,
        cache_assemblies_dir,
        &context.config_origin.local_config_path,
    ) {
        Ok(LocalGamePathCacheOutcome::Written {
            path,
            assemblies_dir,
        }) => {
            let fields = cached_local_game_path_fields(path, assemblies_dir);
            Some(format!(
                "Cached detected {fields} into '{}' for future runs.",
                context.config_origin.local_config_path.display()
            ))
        }
        Ok(LocalGamePathCacheOutcome::Unchanged) => None,
        Err(message) => Some(format!(
            "Failed to cache detected local paths into '{}': {}",
            context.config_origin.local_config_path.display(),
            message
        )),
    }
}

fn cached_local_game_path_fields(path: bool, assemblies_dir: bool) -> &'static str {
    match (path, assemblies_dir) {
        (true, true) => "game.path and game.assembliesDir",
        (true, false) => "game.path",
        (false, true) => "game.assembliesDir",
        (false, false) => "local game paths",
    }
}

fn cache_game_path_to_local_config(
    game_path: Option<&Path>,
    assemblies_dir: Option<&Path>,
    local_config_path: &Path,
) -> Result<LocalGamePathCacheOutcome, String> {
    let raw_config = if local_config_path.exists() {
        let raw = fs::read_to_string(local_config_path).map_err(|source| {
            format!(
                "failed to read local config '{}': {source}",
                local_config_path.display()
            )
        })?;
        Some(raw)
    } else {
        None
    };
    let mut document = if let Some(raw) = raw_config.as_deref() {
        serde_yaml::from_str::<YamlValue>(raw).map_err(|source| {
            format!(
                "failed to parse local config '{}': {source}",
                local_config_path.display()
            )
        })?
    } else {
        YamlValue::Mapping(serde_yaml::Mapping::new())
    };

    let Some(root_map) = document.as_mapping_mut() else {
        return Err("local config root must be a YAML mapping".to_string());
    };

    let game_key = YamlValue::String("game".to_string());
    let path_key = YamlValue::String("path".to_string());
    let assemblies_dir_key = YamlValue::String("assembliesDir".to_string());
    let desired_path = game_path.map(|path| YamlValue::String(path.display().to_string()));
    let desired_assemblies_dir =
        assemblies_dir.map(|path| YamlValue::String(path.display().to_string()));

    let game_value = root_map
        .entry(game_key)
        .or_insert_with(|| YamlValue::Mapping(serde_yaml::Mapping::new()));
    let Some(game_map) = game_value.as_mapping_mut() else {
        return Err("local config game section must be a YAML mapping".to_string());
    };

    let mut wrote_path = false;
    if let Some(desired_path) = desired_path
        && game_map.get(&path_key) != Some(&desired_path)
    {
        game_map.insert(path_key, desired_path);
        wrote_path = true;
    }

    let mut wrote_assemblies_dir = false;
    if let Some(desired_assemblies_dir) = desired_assemblies_dir
        && matches!(
            game_map.get(&assemblies_dir_key),
            None | Some(YamlValue::Null)
        )
    {
        game_map.insert(assemblies_dir_key, desired_assemblies_dir);
        wrote_assemblies_dir = true;
    }

    if !wrote_path && !wrote_assemblies_dir {
        return Ok(LocalGamePathCacheOutcome::Unchanged);
    }

    let rendered = if let Some(raw) = raw_config.as_deref() {
        render_local_config_preserving_comments(
            raw,
            wrote_path.then_some(game_path).flatten(),
            wrote_assemblies_dir.then_some(assemblies_dir).flatten(),
        )
        .unwrap_or_else(|| {
            serde_yaml::to_string(&document)
                .map_err(|source| format!("failed to serialize local config YAML: {source}"))
        })?
    } else {
        serde_yaml::to_string(&document)
            .map_err(|source| format!("failed to serialize local config YAML: {source}"))?
    };
    atomic_write_string(local_config_path, &rendered)?;
    Ok(LocalGamePathCacheOutcome::Written {
        path: wrote_path,
        assemblies_dir: wrote_assemblies_dir,
    })
}

fn render_local_config_preserving_comments(
    raw: &str,
    game_path: Option<&Path>,
    assemblies_dir: Option<&Path>,
) -> Option<Result<String, String>> {
    let needs_path = game_path.is_some();
    let needs_assemblies_dir = assemblies_dir.is_some();
    if !needs_path && !needs_assemblies_dir {
        return Some(Ok(raw.to_string()));
    }

    let mut lines = raw.lines().map(ToOwned::to_owned).collect::<Vec<_>>();
    let game_start = lines
        .iter()
        .position(|line| mapping_key(line) == Some((0, "game", "")))?;
    let game_end = lines
        .iter()
        .enumerate()
        .skip(game_start + 1)
        .find_map(|(index, line)| {
            let (indent, _, _) = mapping_key(line)?;
            (indent == 0).then_some(index)
        })
        .unwrap_or(lines.len());

    let child_indent = detect_mapping_child_indent(&lines[(game_start + 1)..game_end]).unwrap_or(2);
    if let Some(path) = game_path {
        upsert_mapping_child(
            &mut lines,
            game_start + 1,
            game_end,
            child_indent,
            "path",
            &path.display().to_string(),
        )?;
    }

    let game_end = lines
        .iter()
        .enumerate()
        .skip(game_start + 1)
        .find_map(|(index, line)| {
            let (indent, _, _) = mapping_key(line)?;
            (indent == 0).then_some(index)
        })
        .unwrap_or(lines.len());

    if let Some(assemblies_dir) = assemblies_dir {
        upsert_mapping_child(
            &mut lines,
            game_start + 1,
            game_end,
            child_indent,
            "assembliesDir",
            &assemblies_dir.display().to_string(),
        )?;
    }

    Some(Ok(format!("{}\n", lines.join("\n"))))
}

fn detect_mapping_child_indent(lines: &[String]) -> Option<usize> {
    lines.iter().find_map(|line| {
        let (indent, _, _) = mapping_key(line)?;
        (indent > 0).then_some(indent)
    })
}

fn upsert_mapping_child(
    lines: &mut Vec<String>,
    start: usize,
    end: usize,
    indent: usize,
    key: &str,
    value: &str,
) -> Option<()> {
    let rendered = format!("{}{}: {}", " ".repeat(indent), key, value);
    if let Some(index) = (start..end).find(|index| {
        mapping_key(&lines[*index])
            .map(|(line_indent, line_key, _)| line_indent == indent && line_key == key)
            .unwrap_or(false)
    }) {
        lines[index] = rendered;
    } else {
        lines.insert(start, rendered);
    }
    Some(())
}

fn mapping_key(line: &str) -> Option<(usize, &str, &str)> {
    let trimmed = line.trim_start();
    if trimmed.is_empty() || trimmed.starts_with('#') {
        return None;
    }
    let indent = line.len() - trimmed.len();
    let (key, value) = trimmed.split_once(':')?;
    if key.is_empty() || key.chars().any(char::is_whitespace) {
        return None;
    }
    Some((indent, key, value.trim_start()))
}

fn atomic_write_string(path: &Path, contents: &str) -> Result<(), String> {
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("sts2.local.yaml");
    let temp_path = path.with_file_name(format!("{file_name}.tmp-{}", std::process::id()));

    fs::write(&temp_path, contents).map_err(|source| {
        format!(
            "failed to write temp config '{}': {source}",
            temp_path.display()
        )
    })?;
    fs::rename(&temp_path, path).map_err(|source| {
        let _ = fs::remove_file(&temp_path);
        format!(
            "failed to replace local config '{}': {source}",
            path.display()
        )
    })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct GameConfig {
    pub path: String,
    pub assemblies_dir: Option<String>,
    pub resources_dir: Option<String>,
    pub mods_dir: Option<String>,
    /// Optional Godot user data directory override, passed to the game as
    /// `--user-dir`. Multi-instance launches set this per instance so saves,
    /// settings, mod load order, shader cache, and logs stay isolated.
    pub user_dir: Option<String>,
    pub mod_loadout: GameModLoadoutConfig,
    #[serde(deserialize_with = "deserialize_launch_wrapper_words")]
    pub launch_wrapper: Vec<String>,
    pub launch_executable: Option<String>,
    #[serde(deserialize_with = "deserialize_launch_args_words")]
    pub launch_args: Vec<String>,
    pub launch_working_dir: Option<String>,
    pub launch_env: BTreeMap<String, String>,
    /// Temporary opt-in: when true, the launcher sets
    /// `SPIRECTL_BRIDGE_ASSET_LOAD_GUARD=1` so the bridge mod installs its
    /// asset-load crash guards (texture + Ancient-event). Those guards replace
    /// real game-side textures/visuals with empty placeholders, so they are off
    /// by default (normal launches render correctly). Only the auto-player —
    /// whose software-rendered (xvfb) launch still segfaults on game-side
    /// texture loads — should enable it. Remove once that crash is root-caused.
    pub asset_load_guard: bool,
    /// Opt-in developer mode: when true, the launcher sets
    /// `SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE=1` so the bridge mod keeps
    /// the launched instance fully responsive while its window sits in the
    /// background. Without it, a backgrounded window drops the game to its
    /// background FPS limit and — once the compositor stops handing out frame
    /// callbacks for an invisible surface — can block the main loop in present,
    /// which wedges bridge RPCs and queues actions until the window is visible
    /// again. Off by default so ordinary launches keep the shipped power-saving
    /// behavior.
    pub disable_background_throttle: bool,
    pub stop_command: Vec<String>,
    pub deploy_build_command: Vec<String>,
    pub deploy_output_subdir: Option<String>,
}

impl Default for GameConfig {
    fn default() -> Self {
        Self {
            path: "auto".to_string(),
            assemblies_dir: None,
            resources_dir: None,
            mods_dir: None,
            user_dir: None,
            mod_loadout: GameModLoadoutConfig::default(),
            launch_wrapper: Vec::new(),
            launch_executable: None,
            launch_args: Vec::new(),
            launch_working_dir: None,
            launch_env: BTreeMap::new(),
            asset_load_guard: false,
            disable_background_throttle: false,
            stop_command: Vec::new(),
            deploy_build_command: Vec::new(),
            deploy_output_subdir: None,
        }
    }
}

fn deserialize_launch_wrapper_words<'de, D>(deserializer: D) -> Result<Vec<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    deserialize_shell_words_or_string_array(deserializer, "game.launchWrapper")
}

fn deserialize_launch_args_words<'de, D>(deserializer: D) -> Result<Vec<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    deserialize_shell_words_or_string_array(deserializer, "game.launchArgs")
}

fn deserialize_shell_words_or_string_array<'de, D>(
    deserializer: D,
    config_key: &str,
) -> Result<Vec<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = YamlValue::deserialize(deserializer)?;
    let type_message = || {
        format!("{config_key} must be a string or array of strings with valid shell-style quoting")
    };

    match value {
        YamlValue::String(value) => {
            if value.trim().is_empty() {
                return Ok(Vec::new());
            }
            shlex::split(&value).ok_or_else(|| serde::de::Error::custom(type_message()))
        }
        YamlValue::Sequence(values) => values
            .into_iter()
            .map(|value| match value {
                YamlValue::String(value) => Ok(value),
                _ => Err(serde::de::Error::custom(type_message())),
            })
            .collect(),
        _ => Err(serde::de::Error::custom(type_message())),
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct GameModLoadoutConfig {
    pub enabled: Option<Vec<String>>,
    pub settings_file: String,
    pub temporary: bool,
    pub restore_after_no_bridge_ms: u64,
}

impl Default for GameModLoadoutConfig {
    fn default() -> Self {
        Self {
            enabled: None,
            settings_file: "auto".to_string(),
            temporary: true,
            restore_after_no_bridge_ms: 5_000,
        }
    }
}

impl<'de> Deserialize<'de> for GameModLoadoutConfig {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(default, rename_all = "camelCase")]
        struct StructuredGameModLoadoutConfig {
            enabled: Option<Vec<String>>,
            settings_file: String,
            temporary: bool,
            restore_after_no_bridge_ms: u64,
        }

        impl Default for StructuredGameModLoadoutConfig {
            fn default() -> Self {
                let defaults = GameModLoadoutConfig::default();
                Self {
                    enabled: defaults.enabled,
                    settings_file: defaults.settings_file,
                    temporary: defaults.temporary,
                    restore_after_no_bridge_ms: defaults.restore_after_no_bridge_ms,
                }
            }
        }

        #[derive(Deserialize)]
        #[serde(untagged)]
        enum GameModLoadoutConfigInput {
            Enabled(Vec<String>),
            Structured(StructuredGameModLoadoutConfig),
        }

        match GameModLoadoutConfigInput::deserialize(deserializer)? {
            GameModLoadoutConfigInput::Enabled(enabled) => Ok(Self {
                enabled: Some(enabled),
                ..Self::default()
            }),
            GameModLoadoutConfigInput::Structured(structured) => Ok(Self {
                enabled: structured.enabled,
                settings_file: structured.settings_file,
                temporary: structured.temporary,
                restore_after_no_bridge_ms: structured.restore_after_no_bridge_ms,
            }),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct ModeConfig {
    pub default: Mode,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct TransportConfig {
    pub kind: TransportKind,
    pub mock_scenario: MockScenario,
    pub ipc_path: Option<String>,
    pub pipe_name: Option<String>,
    pub tcp_address: Option<String>,
    pub rpc_timeout_ms: Option<u64>,
}

impl Default for TransportConfig {
    fn default() -> Self {
        Self {
            kind: TransportKind::Ipc,
            mock_scenario: MockScenario::MainMenu,
            ipc_path: None,
            pipe_name: None,
            tcp_address: None,
            rpc_timeout_ms: None,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ToolsConfig {
    pub dotnet_tools_path: String,
    pub gdre_path: Option<String>,
}

pub(crate) const DEFAULT_DOTNET_TOOLS_PATH: &str =
    "dotnet-tools/src/Spirectl.DotnetTools/Spirectl.DotnetTools.csproj";

impl Default for ToolsConfig {
    fn default() -> Self {
        Self {
            dotnet_tools_path: DEFAULT_DOTNET_TOOLS_PATH.to_string(),
            gdre_path: None,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
#[derive(Default)]
pub struct ToolchainConfig {
    pub dir: Option<String>,
    pub shared_cache_dir: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
#[derive(Default)]
pub struct ProjectConfig {
    pub profiles_file: Option<String>,
    pub hooks_file: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct TestConfig {
    pub profiles: BTreeMap<String, TestProfileConfig>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct TestProfileConfig {
    pub description: Option<String>,
    pub config: Option<YamlValue>,
    pub paths: Vec<PathBuf>,
    pub include_tags: Vec<String>,
    pub exclude_tags: Vec<String>,
    pub preflight: TestProfilePreflightConfig,
    pub cleanup: TestProfileCleanupConfig,
    pub gate: TestProfileGateConfig,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct TestProfileCleanupConfig {
    pub launched_game: bool,
    pub timeout_ms: u64,
    pub interval_ms: u64,
}

impl Default for TestProfileCleanupConfig {
    fn default() -> Self {
        Self {
            launched_game: false,
            timeout_ms: 10_000,
            interval_ms: 250,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct TestProfilePreflightConfig {
    pub deploy_bridge: bool,
    pub deploy: Option<TestProfileDeployConfig>,
    pub launch: bool,
    pub attach: bool,
    pub timeout_ms: u64,
    pub interval_ms: u64,
    pub rpc_timeout_ms: u64,
    pub verify_stable_ms: u64,
}

impl Default for TestProfilePreflightConfig {
    fn default() -> Self {
        Self {
            deploy_bridge: false,
            deploy: None,
            launch: false,
            attach: false,
            timeout_ms: 30_000,
            interval_ms: 250,
            rpc_timeout_ms: 5_000,
            verify_stable_ms: 0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct TestProfileDeployConfig {
    pub path: Option<PathBuf>,
    pub build: bool,
    pub restart: bool,
    pub verify: bool,
    pub timeout_ms: u64,
    pub interval_ms: u64,
    pub rpc_timeout_ms: u64,
    pub verify_stable_ms: u64,
}

impl Default for TestProfileDeployConfig {
    fn default() -> Self {
        Self {
            path: None,
            build: false,
            restart: false,
            verify: false,
            timeout_ms: 30_000,
            interval_ms: 250,
            rpc_timeout_ms: 5_000,
            verify_stable_ms: 0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct TestProfileGateConfig {
    pub require_matching_scenario: bool,
    pub allow_not_applicable: bool,
    pub require_live_transport: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct ServiceConfig {
    pub job_store: ServiceJobStoreConfig,
    pub mcp: ServiceMcpConfig,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ServiceJobStoreConfig {
    pub mode: ServiceJobStoreMode,
    pub dir: Option<String>,
}

impl Default for ServiceJobStoreConfig {
    fn default() -> Self {
        Self {
            mode: ServiceJobStoreMode::Memory,
            dir: None,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ServiceMcpConfig {
    pub mode: ServiceMcpMode,
    pub listen: std::net::SocketAddr,
    pub auth_token: Option<String>,
    pub acknowledge_non_loopback_threat_model: bool,
}

impl Default for ServiceMcpConfig {
    fn default() -> Self {
        Self {
            mode: ServiceMcpMode::Disabled,
            listen: "127.0.0.1:4318"
                .parse()
                .expect("default MCP listen address"),
            auth_token: None,
            acknowledge_non_loopback_threat_model: false,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ArtifactsConfig {
    pub dir: String,
}

impl Default for ArtifactsConfig {
    fn default() -> Self {
        Self {
            dir: "./.sts2/artifacts".to_string(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct InstancesConfig {
    /// Root directory for per-instance state (Godot user-dir, isolated game
    /// mirror, registry entry). Relative paths resolve from the working dir.
    pub dir: String,
    /// Optional default instance name used when neither `--instance` nor
    /// `SPIRECTL_INSTANCE` is set.
    #[serde(rename = "default")]
    pub default_name: Option<String>,
    /// Default isolated-build mode for the active instance. `--isolated-build`
    /// can still force this on for one invocation.
    pub isolated_build: bool,
    /// Paths inside the shared STS2 user-data dir (`~/.local/share/SlayTheSpire2`)
    /// that instance seeding symlinks to the shared copy instead of duplicating.
    /// Relative to that root; nested paths (`couch-coop/astc-cache`) are allowed.
    ///
    /// Use this for large, mostly-regenerable mod caches that every instance
    /// would otherwise clone.
    pub symlink_user_data_dirs: Vec<String>,
}

impl Default for InstancesConfig {
    fn default() -> Self {
        Self {
            dir: "./.sts2/instances".to_string(),
            default_name: None,
            isolated_build: false,
            symlink_user_data_dirs: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct UsageTrackingConfig {
    pub enabled: bool,
    pub dir: String,
}

impl Default for UsageTrackingConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            dir: "./.sts2".to_string(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
#[derive(Default)]
pub struct VisualValidationConfig {
    pub preset_catalogs: Vec<PathBuf>,
}
