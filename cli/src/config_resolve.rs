use super::*;
use std::path::Component;

/// The keys `config resolve` reports, in output order.
pub(crate) const CONFIG_RESOLVE_KEYS: &[&str] = &[
    "gamePath",
    "assembliesDir",
    "resourcesDir",
    "modsDir",
    "instancesDir",
    "artifactsDir",
];

/// `sts2 --json config resolve [key...]`
///
/// Prints the install paths the CLI itself would use, already absolute, so a downstream script
/// never has to re-parse `sts2.local.yaml` to learn where the game, its assemblies, its resources
/// or its mods directory live. Every value goes through the same `install_paths` resolvers the
/// real commands use, so the answer cannot drift from what the CLI actually does.
pub(crate) fn execute_config_resolve_json(
    args: ConfigResolveArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let requested = requested_keys(&args.keys)?;

    // `include_mods: true` is the only way to get a mods directory out of the scene resolver, but
    // it turns an unresolvable mods dir into a hard error. Fall back so one missing key never
    // hides the five that did resolve.
    let mut errors = Vec::new();
    let scene = match install_paths::resolve_scene_search_paths(
        context.config,
        install_paths::PathOverrides::default(),
        true,
    ) {
        Ok(scene) => Some(scene),
        Err(mods_error) => {
            match install_paths::resolve_scene_search_paths(
                context.config,
                install_paths::PathOverrides::default(),
                false,
            ) {
                Ok(scene) => {
                    errors.push(json!({ "key": "modsDir", "message": mods_error }));
                    Some(scene)
                }
                Err(scene_error) => {
                    errors.push(json!({ "key": "resourcesDir", "message": scene_error }));
                    None
                }
            }
        }
    };

    // The code resolver requires only an assemblies dir, so it still answers when the scene
    // resolver could not find a resources directory.
    let code = match install_paths::resolve_code_search_paths(
        context.config,
        install_paths::PathOverrides::default(),
        false,
    ) {
        Ok(code) => Some(code),
        Err(message) => {
            if scene
                .as_ref()
                .is_none_or(|scene| scene.assemblies_dir.is_none())
            {
                errors.push(json!({ "key": "assembliesDir", "message": message }));
            }
            None
        }
    };

    let game_path = scene
        .as_ref()
        .and_then(|scene| scene.game_path.clone())
        .or_else(|| code.as_ref().and_then(|code| code.game_path.clone()));
    if game_path.is_none() {
        errors.push(json!({
            "key": "gamePath",
            "message": "No STS2 install resolved from game.path, an override, or Steam autodiscovery.",
        }));
    }
    let assemblies_dir = scene
        .as_ref()
        .and_then(|scene| scene.assemblies_dir.clone())
        .or_else(|| code.as_ref().map(|code| code.assemblies_dir.clone()));
    let resources_dir = scene.as_ref().map(|scene| scene.resources_dir.clone());
    let mods_dir = scene
        .as_ref()
        .and_then(|scene| scene.mods_dir.clone())
        .filter(|path| !path.as_os_str().is_empty());

    let used_discovered_game_path = scene
        .as_ref()
        .map(install_paths::ResolvedSceneSearchPaths::used_discovered_game_path)
        .or_else(|| {
            code.as_ref()
                .map(install_paths::ResolvedCodeSearchPaths::used_discovered_game_path)
        })
        .unwrap_or(false);
    let derived_assemblies_dir = scene
        .as_ref()
        .filter(|scene| scene.assemblies_dir.is_some())
        .map(install_paths::ResolvedSceneSearchPaths::derived_assemblies_dir_from_game_path)
        .or_else(|| {
            code.as_ref()
                .map(install_paths::ResolvedCodeSearchPaths::derived_assemblies_dir_from_game_path)
        })
        .unwrap_or(false);

    let provenance = context.config_provenance;
    let sources = [
        (
            "gamePath",
            path_source(
                used_discovered_game_path,
                "discovered",
                provenance.game_path,
            ),
        ),
        (
            "assembliesDir",
            path_source(derived_assemblies_dir, "derived", provenance.assemblies_dir),
        ),
        (
            "resourcesDir",
            path_source(
                context.config.game.resources_dir.is_none(),
                "derived",
                provenance.resources_dir,
            ),
        ),
        (
            "modsDir",
            path_source(
                context.config.game.mods_dir.is_none(),
                "derived",
                provenance.mods_dir,
            ),
        ),
        ("instancesDir", provenance.instances_dir.name()),
        ("artifactsDir", provenance.artifacts_dir.name()),
    ];

    let values = [
        ("gamePath", game_path),
        ("assembliesDir", assemblies_dir),
        ("resourcesDir", resources_dir),
        ("modsDir", mods_dir),
        (
            "instancesDir",
            Some(crate::instance::absolute_instances_dir(context.config)),
        ),
        (
            "artifactsDir",
            Some(absolute_working_dir_path(&context.config.artifacts.dir)),
        ),
    ];

    let mut payload = serde_json::Map::new();
    let mut source_payload = serde_json::Map::new();
    for (key, value) in values {
        if !requested.contains(&key) {
            continue;
        }
        payload.insert(
            key.to_string(),
            value.map_or(Value::Null, |path| json!(tidy(path).display().to_string())),
        );
        let source = sources
            .iter()
            .find_map(|(candidate, source)| (*candidate == key).then_some(*source))
            .unwrap_or_else(|| ConfigValueSource::Default.name());
        source_payload.insert(key.to_string(), json!(source));
    }

    errors.retain(|error| {
        error["key"]
            .as_str()
            .is_some_and(|key| requested.contains(&key))
    });

    payload.insert("sources".to_string(), Value::Object(source_payload));
    payload.insert("configDir".to_string(), json!(config_dir_display(context)));
    payload.insert(
        "configDirSource".to_string(),
        json!(context.config_origin.config_dir_source.name()),
    );
    payload.insert("configFiles".to_string(), json!(config_files(context)));
    payload.insert("errors".to_string(), Value::Array(errors));
    Ok(Value::Object(payload))
}

fn path_source(
    resolver_derived: bool,
    derived_name: &'static str,
    configured: ConfigValueSource,
) -> &'static str {
    if resolver_derived {
        derived_name
    } else {
        configured.name()
    }
}

/// Mirrors `absolute_instances_dir`/`absolute_artifacts_dir`: a relative configured directory is
/// anchored on the working directory, not on the config directory.
fn absolute_working_dir_path(configured: &str) -> PathBuf {
    let configured = PathBuf::from(configured);
    if configured.is_absolute() {
        return configured;
    }
    std::env::current_dir()
        .map(|cwd| cwd.join(&configured))
        .unwrap_or(configured)
}

/// Drop the `.` components a joined `./.sts2/...` config value leaves behind. Purely cosmetic:
/// the output is meant to be pasted into other tools, and `/repo/./.sts2` reads like a bug.
/// `Path::components` already normalizes interior `.` away, so rebuilding from it is enough.
fn tidy(path: PathBuf) -> PathBuf {
    let rebuilt: PathBuf = path
        .components()
        .filter(|part| *part != Component::CurDir)
        .collect();
    if rebuilt.as_os_str().is_empty() {
        path
    } else {
        rebuilt
    }
}

fn config_dir_display(context: AppContext<'_>) -> String {
    context.config_origin.config_dir.display().to_string()
}

/// The config files that actually contributed, newest layer last.
fn config_files(context: AppContext<'_>) -> Vec<String> {
    let dir = &context.config_origin.config_dir;
    [
        dir.join(CHECKED_IN_CONFIG_FILE_NAME),
        context.config_origin.local_config_path.clone(),
    ]
    .into_iter()
    .filter(|path| path.is_file())
    .map(|path| path.display().to_string())
    .collect()
}

fn requested_keys(keys: &[String]) -> Result<Vec<&'static str>, AppError> {
    if keys.is_empty() {
        return Ok(CONFIG_RESOLVE_KEYS.to_vec());
    }

    let mut requested: Vec<&'static str> = Vec::new();
    for key in keys {
        let matched = CONFIG_RESOLVE_KEYS
            .iter()
            .find(|candidate| candidate.eq_ignore_ascii_case(key))
            .copied();
        match matched {
            Some(matched) if !requested.contains(&matched) => requested.push(matched),
            Some(_) => {}
            None => return Err(AppError::unknown_config_key(key, CONFIG_RESOLVE_KEYS)),
        }
    }
    Ok(requested)
}
