use crate::AppConfig;
use crate::steam_discovery;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum GamePathSource {
    Unresolved,
    Override,
    Configured,
    Discovered,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AssembliesDirSource {
    Override,
    Configured,
    DerivedFromGamePath,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct OptionalGamePathResolution {
    path: Option<PathBuf>,
    source: GamePathSource,
}

#[derive(Debug, Clone, Copy, Default)]
pub struct PathOverrides<'a> {
    pub game_path: Option<&'a Path>,
    pub assemblies_dir: Option<&'a Path>,
    pub resources_dir: Option<&'a Path>,
    pub mods_dir: Option<&'a Path>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedCodeSearchPaths {
    pub game_path: Option<PathBuf>,
    pub assemblies_dir: PathBuf,
    pub mods_dir: Option<PathBuf>,
    game_path_source: GamePathSource,
    assemblies_dir_source: AssembliesDirSource,
}

impl ResolvedCodeSearchPaths {
    pub fn used_discovered_game_path(&self) -> bool {
        self.game_path_source == GamePathSource::Discovered
    }

    pub fn derived_assemblies_dir_from_game_path(&self) -> bool {
        self.assemblies_dir_source == AssembliesDirSource::DerivedFromGamePath
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedSceneSearchPaths {
    pub game_path: Option<PathBuf>,
    pub resources_dir: PathBuf,
    pub assemblies_dir: Option<PathBuf>,
    pub mods_dir: Option<PathBuf>,
    game_path_source: GamePathSource,
    assemblies_dir_source: Option<AssembliesDirSource>,
}

impl ResolvedSceneSearchPaths {
    pub fn used_discovered_game_path(&self) -> bool {
        self.game_path_source == GamePathSource::Discovered
    }

    pub fn derived_assemblies_dir_from_game_path(&self) -> bool {
        self.assemblies_dir_source == Some(AssembliesDirSource::DerivedFromGamePath)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedLiveBridgePaths {
    pub game_path: PathBuf,
    pub assemblies_dir: PathBuf,
    pub mods_dir: PathBuf,
    game_path_source: GamePathSource,
    assemblies_dir_source: AssembliesDirSource,
}

impl ResolvedLiveBridgePaths {
    pub fn used_discovered_game_path(&self) -> bool {
        self.game_path_source == GamePathSource::Discovered
    }

    pub fn derived_assemblies_dir_from_game_path(&self) -> bool {
        self.assemblies_dir_source == AssembliesDirSource::DerivedFromGamePath
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GamePathDetection {
    pub configured_path: String,
    pub configured_valid_path: Option<PathBuf>,
    pub configured_error: Option<String>,
    pub discovered_path: Option<PathBuf>,
}

impl GamePathDetection {
    pub fn status(&self) -> &'static str {
        if self.configured_valid_path.is_some() {
            "configured"
        } else if self.discovered_path.is_some() {
            "detected"
        } else {
            "not-detected"
        }
    }

    pub fn effective_path(&self) -> Option<&Path> {
        self.configured_valid_path
            .as_deref()
            .or(self.discovered_path.as_deref())
    }

    pub fn used_discovered_path(&self) -> bool {
        self.configured_valid_path.is_none() && self.discovered_path.is_some()
    }
}

pub fn detect_game_path(config: &AppConfig) -> GamePathDetection {
    detect_game_path_with(config, &steam_discovery::discover_game_path)
}

pub fn resolve_live_bridge_paths(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
) -> Result<ResolvedLiveBridgePaths, String> {
    resolve_live_bridge_paths_with(config, overrides, &steam_discovery::discover_game_path)
}

fn resolve_live_bridge_paths_with<F>(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
    discover_game_path: &F,
) -> Result<ResolvedLiveBridgePaths, String>
where
    F: Fn() -> Option<PathBuf>,
{
    let game_path =
        resolve_required_game_path_with(config, overrides.game_path, discover_game_path)?;
    let assemblies_dir =
        resolve_live_bridge_assemblies_dir(config, overrides.assemblies_dir, &game_path.path)?;
    let mods_dir = resolve_mods_dir(config, overrides.mods_dir, Some(&game_path.path))?
        .expect("required game path should always derive a mods dir");

    Ok(ResolvedLiveBridgePaths {
        game_path: game_path.path,
        assemblies_dir: assemblies_dir.path,
        mods_dir,
        game_path_source: game_path.source,
        assemblies_dir_source: assemblies_dir.source,
    })
}

pub fn resolve_code_search_paths(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
    include_mods: bool,
) -> Result<ResolvedCodeSearchPaths, String> {
    resolve_code_search_paths_with(
        config,
        overrides,
        include_mods,
        &steam_discovery::discover_game_path,
    )
}

fn resolve_code_search_paths_with<F>(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
    include_mods: bool,
    discover_game_path: &F,
) -> Result<ResolvedCodeSearchPaths, String>
where
    F: Fn() -> Option<PathBuf>,
{
    let game_path =
        resolve_optional_game_path_with(config, overrides.game_path, discover_game_path)?;
    let assemblies_dir =
        resolve_code_assemblies_dir(config, overrides.assemblies_dir, game_path.path.as_deref())?;

    let mods_dir = if include_mods {
        resolve_mods_dir(config, overrides.mods_dir, game_path.path.as_deref())?
            .or_else(|| Some(PathBuf::from("")))
    } else {
        None
    };

    if include_mods
        && mods_dir
            .as_ref()
            .is_some_and(|path| path.as_os_str().is_empty())
    {
        return Err(
            "Static inspection with --include-mods requires --mods-dir, game.modsDir, or an explicit game.path."
                .to_string(),
        );
    }

    Ok(ResolvedCodeSearchPaths {
        game_path: game_path.path,
        assemblies_dir: assemblies_dir.path,
        mods_dir,
        game_path_source: game_path.source,
        assemblies_dir_source: assemblies_dir.source,
    })
}

pub fn resolve_scene_search_paths(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
    include_mods: bool,
) -> Result<ResolvedSceneSearchPaths, String> {
    resolve_scene_search_paths_with(
        config,
        overrides,
        include_mods,
        &steam_discovery::discover_game_path,
    )
}

fn resolve_scene_search_paths_with<F>(
    config: &AppConfig,
    overrides: PathOverrides<'_>,
    include_mods: bool,
    discover_game_path: &F,
) -> Result<ResolvedSceneSearchPaths, String>
where
    F: Fn() -> Option<PathBuf>,
{
    let game_path =
        resolve_optional_game_path_with(config, overrides.game_path, discover_game_path)?;
    let resources_dir =
        resolve_scene_resources_dir(config, overrides.resources_dir, game_path.path.as_deref())?;
    let assemblies_dir = resolve_optional_code_assemblies_dir(
        config,
        overrides.assemblies_dir,
        game_path.path.as_deref(),
    )?;

    let mods_dir = if include_mods {
        resolve_mods_dir(config, overrides.mods_dir, game_path.path.as_deref())?
            .or_else(|| Some(PathBuf::from("")))
    } else {
        None
    };

    if include_mods
        && mods_dir
            .as_ref()
            .is_some_and(|path| path.as_os_str().is_empty())
    {
        return Err(
            "Static inspection with --include-mods requires --mods-dir, game.modsDir, or an explicit game.path."
                .to_string(),
        );
    }

    Ok(ResolvedSceneSearchPaths {
        game_path: game_path.path,
        resources_dir,
        assemblies_dir: assemblies_dir
            .as_ref()
            .map(|resolution| resolution.path.clone()),
        mods_dir,
        game_path_source: game_path.source,
        assemblies_dir_source: assemblies_dir.map(|resolution| resolution.source),
    })
}

fn resolve_required_game_path_with<F>(
    config: &AppConfig,
    override_path: Option<&Path>,
    discover_game_path: &F,
) -> Result<RequiredGamePathResolution, String>
where
    F: Fn() -> Option<PathBuf>,
{
    let resolution = resolve_optional_game_path_with(config, override_path, discover_game_path)?;
    let Some(path) = resolution.path else {
        return Err(
            "game.path is set to 'auto', but no Steam install for Slay the Spire 2 was detected on this host OS; live bridge path resolution requires a detected or explicit game.path."
                .to_string(),
        );
    };

    Ok(RequiredGamePathResolution {
        path,
        source: resolution.source,
    })
}

fn resolve_optional_game_path_with<F>(
    config: &AppConfig,
    override_path: Option<&Path>,
    discover_game_path: &F,
) -> Result<OptionalGamePathResolution, String>
where
    F: Fn() -> Option<PathBuf>,
{
    if let Some(path) = override_path {
        return validate_game_path(path.to_path_buf()).map(|path| OptionalGamePathResolution {
            path: Some(path),
            source: GamePathSource::Override,
        });
    }

    let detection = detect_game_path_with(config, discover_game_path);
    if let Some(path) = detection.configured_valid_path {
        return Ok(OptionalGamePathResolution {
            path: Some(path),
            source: GamePathSource::Configured,
        });
    }

    if let Some(path) = detection.discovered_path {
        return Ok(OptionalGamePathResolution {
            path: Some(path),
            source: GamePathSource::Discovered,
        });
    }

    if let Some(error) = detection.configured_error {
        return Err(error);
    }

    Ok(OptionalGamePathResolution {
        path: None,
        source: GamePathSource::Unresolved,
    })
}

fn detect_game_path_with<F>(config: &AppConfig, discover_game_path: &F) -> GamePathDetection
where
    F: Fn() -> Option<PathBuf>,
{
    if config.game.path == "auto" {
        return GamePathDetection {
            configured_path: config.game.path.clone(),
            configured_valid_path: None,
            configured_error: None,
            discovered_path: discover_valid_game_path(discover_game_path),
        };
    }

    let configured_path = PathBuf::from(&config.game.path);
    match validate_game_path(configured_path.clone()) {
        Ok(configured_path) => GamePathDetection {
            configured_path: config.game.path.clone(),
            configured_valid_path: Some(configured_path),
            configured_error: None,
            discovered_path: None,
        },
        Err(error) => GamePathDetection {
            configured_path: config.game.path.clone(),
            configured_valid_path: None,
            configured_error: Some(error),
            discovered_path: discover_valid_game_path(discover_game_path),
        },
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct RequiredGamePathResolution {
    path: PathBuf,
    source: GamePathSource,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct AssembliesDirResolution {
    path: PathBuf,
    source: AssembliesDirSource,
}

fn discover_valid_game_path<F>(discover_game_path: &F) -> Option<PathBuf>
where
    F: Fn() -> Option<PathBuf>,
{
    discover_game_path().and_then(|path| validate_game_path(path).ok())
}

fn validate_game_path(path: PathBuf) -> Result<PathBuf, String> {
    ensure_existing_dir(&path, "game path")?;
    derive_assemblies_dir_from_game_path(&path)?;
    Ok(path)
}

fn resolve_live_bridge_assemblies_dir(
    config: &AppConfig,
    override_path: Option<&Path>,
    game_path: &Path,
) -> Result<AssembliesDirResolution, String> {
    if let Some(path) = override_path {
        return validate_live_bridge_assemblies_dir(path.to_path_buf()).map(|path| {
            AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Override,
            }
        });
    }

    if let Some(path) = config.game.assemblies_dir.as_deref() {
        return validate_live_bridge_assemblies_dir(PathBuf::from(path)).map(|path| {
            AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Configured,
            }
        });
    }

    derive_assemblies_dir_from_game_path(game_path).map(|path| AssembliesDirResolution {
        path,
        source: AssembliesDirSource::DerivedFromGamePath,
    })
}

fn resolve_code_assemblies_dir(
    config: &AppConfig,
    override_path: Option<&Path>,
    game_path: Option<&Path>,
) -> Result<AssembliesDirResolution, String> {
    if let Some(path) = override_path {
        return validate_code_assemblies_dir(path.to_path_buf()).map(|path| {
            AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Override,
            }
        });
    }

    if let Some(path) = config.game.assemblies_dir.as_deref() {
        return validate_code_assemblies_dir(PathBuf::from(path)).map(|path| {
            AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Configured,
            }
        });
    }

    if let Some(game_path) = game_path {
        return derive_assemblies_dir_from_game_path(game_path).map(|path| {
            AssembliesDirResolution {
                path,
                source: AssembliesDirSource::DerivedFromGamePath,
            }
        });
    }

    Err(
        "Static inspection requires --assemblies-dir, game.assembliesDir, or an explicit game.path."
            .to_string(),
    )
}

fn resolve_optional_code_assemblies_dir(
    config: &AppConfig,
    override_path: Option<&Path>,
    game_path: Option<&Path>,
) -> Result<Option<AssembliesDirResolution>, String> {
    if let Some(path) = override_path {
        return validate_code_assemblies_dir(path.to_path_buf()).map(|path| {
            Some(AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Override,
            })
        });
    }

    if let Some(path) = config.game.assemblies_dir.as_deref() {
        return validate_code_assemblies_dir(PathBuf::from(path)).map(|path| {
            Some(AssembliesDirResolution {
                path,
                source: AssembliesDirSource::Configured,
            })
        });
    }

    if let Some(game_path) = game_path {
        return match derive_assemblies_dir_from_game_path(game_path) {
            Ok(path) => Ok(Some(AssembliesDirResolution {
                path,
                source: AssembliesDirSource::DerivedFromGamePath,
            })),
            Err(_) => Ok(None),
        };
    }

    Ok(None)
}

fn resolve_scene_resources_dir(
    config: &AppConfig,
    override_path: Option<&Path>,
    game_path: Option<&Path>,
) -> Result<PathBuf, String> {
    if let Some(path) = override_path {
        return validate_scene_resources_dir(path.to_path_buf());
    }

    if let Some(path) = config.game.resources_dir.as_deref() {
        return validate_scene_resources_dir(PathBuf::from(path));
    }

    if let Some(game_path) = game_path {
        return validate_scene_resources_dir(game_path.to_path_buf());
    }

    Err(
        "Static scene inspection requires --resources-dir, game.resourcesDir, or an explicit game.path."
            .to_string(),
    )
}

fn resolve_mods_dir(
    config: &AppConfig,
    override_path: Option<&Path>,
    game_path: Option<&Path>,
) -> Result<Option<PathBuf>, String> {
    let path = override_path
        .map(Path::to_path_buf)
        .or_else(|| config.game.mods_dir.as_deref().map(PathBuf::from))
        .or_else(|| game_path.map(|path| path.join("mods")));

    let Some(path) = path else {
        return Ok(None);
    };

    if path.exists() && !path.is_dir() {
        return Err(format!(
            "Resolved mods directory '{}' exists but is not a directory.",
            path.display()
        ));
    }

    Ok(Some(path))
}

pub(crate) fn derive_assemblies_dir_from_game_path(game_path: &Path) -> Result<PathBuf, String> {
    let mut candidates = fs::read_dir(game_path)
        .map_err(|source| {
            format!(
                "Failed to inspect '{}' for STS2 assemblies: {source}",
                game_path.display()
            )
        })?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_dir()
                && path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .map(|name| name.starts_with("data_sts2_"))
                    .unwrap_or(false)
        })
        .collect::<Vec<_>>();
    candidates.sort();

    for candidate in candidates {
        if validate_required_sts2_assembly_files(&candidate).is_ok() {
            return Ok(candidate);
        }
    }

    Err(format!(
        "Could not find a data_sts2_* directory containing sts2.dll and GodotSharp.dll under '{}'.",
        game_path.display()
    ))
}

fn validate_live_bridge_assemblies_dir(path: PathBuf) -> Result<PathBuf, String> {
    ensure_existing_dir(&path, "assemblies directory")?;
    validate_required_sts2_assembly_files(&path)?;
    Ok(path)
}

fn validate_code_assemblies_dir(path: PathBuf) -> Result<PathBuf, String> {
    ensure_existing_dir(&path, "assemblies directory")?;

    let has_dll = fs::read_dir(&path)
        .map_err(|source| {
            format!(
                "Failed to inspect '{}' for assembly files: {source}",
                path.display()
            )
        })?
        .filter_map(Result::ok)
        .any(|entry| {
            entry
                .path()
                .extension()
                .and_then(|extension| extension.to_str())
                .map(|extension| extension.eq_ignore_ascii_case("dll"))
                .unwrap_or(false)
        });

    if !has_dll {
        return Err(format!(
            "Resolved assemblies directory '{}' does not contain any .dll files.",
            path.display()
        ));
    }

    Ok(path)
}

fn validate_scene_resources_dir(path: PathBuf) -> Result<PathBuf, String> {
    ensure_existing_dir(&path, "resources directory")?;
    Ok(path)
}

fn validate_required_sts2_assembly_files(path: &Path) -> Result<(), String> {
    for file_name in ["sts2.dll", "GodotSharp.dll"] {
        let file_path = path.join(file_name);
        if !file_path.is_file() {
            return Err(format!(
                "Expected '{}' inside '{}'.",
                file_name,
                path.display()
            ));
        }
    }

    Ok(())
}

fn ensure_existing_dir(path: &Path, label: &str) -> Result<(), String> {
    if !path.exists() {
        return Err(format!(
            "Resolved {label} '{}' does not exist.",
            path.display()
        ));
    }

    if !path.is_dir() {
        return Err(format!(
            "Resolved {label} '{}' is not a directory.",
            path.display()
        ));
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AppConfig, GameConfig};

    #[test]
    fn resolve_live_bridge_paths_derives_assemblies_and_mods_from_game_path() {
        let game_dir = tempfile::tempdir().expect("game dir");
        let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig {
            game: GameConfig {
                path: game_dir.path().to_string_lossy().into_owned(),
                ..GameConfig::default()
            },
            ..AppConfig::default()
        };

        let paths = resolve_live_bridge_paths(&config, PathOverrides::default()).expect("paths");

        assert_eq!(paths.game_path, game_dir.path());
        assert_eq!(paths.assemblies_dir, assemblies_dir);
        assert_eq!(paths.mods_dir, game_dir.path().join("mods"));
    }

    #[test]
    fn resolve_live_bridge_paths_uses_discovered_auto_game_path() {
        let game_dir = tempfile::tempdir().expect("game dir");
        let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig::default();
        let paths = resolve_live_bridge_paths_with(&config, PathOverrides::default(), &|| {
            Some(game_dir.path().to_path_buf())
        })
        .expect("paths");

        assert_eq!(paths.game_path, game_dir.path());
        assert_eq!(paths.assemblies_dir, assemblies_dir);
        assert_eq!(paths.mods_dir, game_dir.path().join("mods"));
    }

    #[test]
    fn resolve_live_bridge_paths_falls_back_when_configured_dir_is_not_valid_sts2_root() {
        let invalid_dir = tempfile::tempdir().expect("invalid game dir");
        let discovered_dir = tempfile::tempdir().expect("discovered game dir");
        let assemblies_dir = discovered_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig {
            game: GameConfig {
                path: invalid_dir.path().to_string_lossy().into_owned(),
                ..GameConfig::default()
            },
            ..AppConfig::default()
        };

        let paths = resolve_live_bridge_paths_with(&config, PathOverrides::default(), &|| {
            Some(discovered_dir.path().to_path_buf())
        })
        .expect("paths");

        assert_eq!(paths.game_path, discovered_dir.path());
        assert_eq!(paths.assemblies_dir, assemblies_dir);
        assert_eq!(paths.mods_dir, discovered_dir.path().join("mods"));
    }

    #[test]
    fn resolve_live_bridge_paths_requires_explicit_override_to_be_valid_sts2_root() {
        let invalid_dir = tempfile::tempdir().expect("invalid game dir");
        let discovered_dir = tempfile::tempdir().expect("discovered game dir");
        let assemblies_dir = discovered_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig::default();
        let error = resolve_live_bridge_paths_with(
            &config,
            PathOverrides {
                game_path: Some(invalid_dir.path()),
                ..PathOverrides::default()
            },
            &|| Some(discovered_dir.path().to_path_buf()),
        )
        .expect_err("invalid explicit override should fail");

        assert!(error.contains("Could not find a data_sts2_* directory"));
    }

    #[test]
    fn resolve_code_search_paths_accepts_plain_assembly_directories() {
        let assemblies_dir = tempfile::tempdir().expect("assemblies dir");
        fs::write(assemblies_dir.path().join("Spirectl.TestSymbols.dll"), []).expect("fixture dll");

        let config = AppConfig::default();
        let paths = resolve_code_search_paths_with(
            &config,
            PathOverrides {
                assemblies_dir: Some(assemblies_dir.path()),
                resources_dir: None,
                ..PathOverrides::default()
            },
            false,
            &|| None,
        )
        .expect("paths");

        assert_eq!(paths.game_path, None);
        assert_eq!(paths.assemblies_dir, assemblies_dir.path());
        assert_eq!(paths.mods_dir, None);
    }

    #[test]
    fn resolve_code_search_paths_uses_discovered_auto_game_path() {
        let game_dir = tempfile::tempdir().expect("game dir");
        let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig::default();
        let paths =
            resolve_code_search_paths_with(&config, PathOverrides::default(), true, &|| {
                Some(game_dir.path().to_path_buf())
            })
            .expect("paths");

        assert_eq!(paths.game_path, Some(game_dir.path().to_path_buf()));
        assert_eq!(paths.assemblies_dir, assemblies_dir);
        assert_eq!(paths.mods_dir, Some(game_dir.path().join("mods")));
    }

    #[test]
    fn resolve_code_search_paths_requires_a_way_to_find_assemblies() {
        let config = AppConfig::default();
        let error =
            resolve_code_search_paths_with(&config, PathOverrides::default(), false, &|| None)
                .expect_err("expected missing search roots to fail");

        assert!(error.contains("Static inspection requires"));
    }

    #[test]
    fn resolve_scene_search_paths_accepts_plain_resource_directories() {
        let resources_dir = tempfile::tempdir().expect("resources dir");
        fs::write(resources_dir.path().join("CombatScreen.tscn"), []).expect("fixture scene");

        let config = AppConfig::default();
        let paths = resolve_scene_search_paths_with(
            &config,
            PathOverrides {
                resources_dir: Some(resources_dir.path()),
                ..PathOverrides::default()
            },
            false,
            &|| None,
        )
        .expect("paths");

        assert_eq!(paths.game_path, None);
        assert_eq!(paths.resources_dir, resources_dir.path());
        assert_eq!(paths.assemblies_dir, None);
        assert_eq!(paths.mods_dir, None);
    }

    #[test]
    fn resolve_scene_search_paths_uses_discovered_auto_game_path() {
        let game_dir = tempfile::tempdir().expect("game dir");
        let assemblies_dir = game_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");
        fs::write(game_dir.path().join("CombatScreen.tscn"), []).expect("fixture scene");

        let config = AppConfig::default();
        let paths =
            resolve_scene_search_paths_with(&config, PathOverrides::default(), true, &|| {
                Some(game_dir.path().to_path_buf())
            })
            .expect("paths");

        assert_eq!(paths.game_path, Some(game_dir.path().to_path_buf()));
        assert_eq!(paths.resources_dir, game_dir.path());
        assert_eq!(paths.assemblies_dir, Some(assemblies_dir));
        assert_eq!(paths.mods_dir, Some(game_dir.path().join("mods")));
    }

    #[test]
    fn resolve_scene_search_paths_requires_a_way_to_find_resources() {
        let config = AppConfig::default();
        let error =
            resolve_scene_search_paths_with(&config, PathOverrides::default(), false, &|| None)
                .expect_err("expected missing resources roots to fail");

        assert!(error.contains("Static scene inspection requires"));
    }

    #[test]
    fn detect_game_path_falls_back_to_discovery_when_configured_path_is_invalid() {
        let discovered_dir = tempfile::tempdir().expect("discovered dir");
        let assemblies_dir = discovered_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");
        let config = AppConfig {
            game: GameConfig {
                path: "/missing/sts2".to_string(),
                ..GameConfig::default()
            },
            ..AppConfig::default()
        };

        let detection =
            detect_game_path_with(&config, &|| Some(discovered_dir.path().to_path_buf()));

        assert_eq!(detection.status(), "detected");
        assert_eq!(detection.configured_path, "/missing/sts2");
        assert!(detection.configured_error.is_some());
        assert_eq!(detection.effective_path(), Some(discovered_dir.path()));
    }

    #[test]
    fn detect_game_path_falls_back_when_configured_existing_dir_is_not_valid_sts2_root() {
        let invalid_dir = tempfile::tempdir().expect("invalid dir");
        let discovered_dir = tempfile::tempdir().expect("discovered dir");
        let assemblies_dir = discovered_dir.path().join("data_sts2_linux_x86_64");
        fs::create_dir_all(&assemblies_dir).expect("assemblies dir");
        fs::write(assemblies_dir.join("sts2.dll"), []).expect("sts2 dll");
        fs::write(assemblies_dir.join("GodotSharp.dll"), []).expect("godot dll");

        let config = AppConfig {
            game: GameConfig {
                path: invalid_dir.path().to_string_lossy().into_owned(),
                ..GameConfig::default()
            },
            ..AppConfig::default()
        };

        let detection =
            detect_game_path_with(&config, &|| Some(discovered_dir.path().to_path_buf()));

        assert_eq!(detection.status(), "detected");
        assert_eq!(
            detection.configured_path,
            invalid_dir.path().to_string_lossy()
        );
        assert!(
            detection
                .configured_error
                .as_ref()
                .expect("configured error")
                .contains("Could not find a data_sts2_* directory")
        );
        assert_eq!(detection.effective_path(), Some(discovered_dir.path()));
    }
}
