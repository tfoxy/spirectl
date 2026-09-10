use crate::host_paths::{HostPlatform, current_host_platform};
use std::collections::{BTreeMap, HashSet};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

const STS2_APP_ID: &str = "2868840";

pub fn discover_game_path() -> Option<PathBuf> {
    discover_game_path_with_context(&DiscoveryContext::current())
}

fn discover_game_path_with_context(context: &DiscoveryContext) -> Option<PathBuf> {
    for steam_root in candidate_steam_roots(context) {
        if let Some(game_path) = discover_game_path_from_steam_root(&steam_root) {
            return Some(game_path);
        }
    }

    None
}

fn discover_game_path_from_steam_root(steam_root: &Path) -> Option<PathBuf> {
    for library_path in library_paths_for_steam_root(steam_root) {
        let manifest_path = library_path
            .join("steamapps")
            .join(format!("appmanifest_{STS2_APP_ID}.acf"));
        let Ok(manifest) = fs::read_to_string(&manifest_path) else {
            continue;
        };
        let Ok(install_dir) = parse_app_manifest_install_dir(&manifest) else {
            continue;
        };
        let game_path = library_path
            .join("steamapps")
            .join("common")
            .join(install_dir);

        if game_path.is_dir() {
            return Some(game_path);
        }
    }

    None
}

fn library_paths_for_steam_root(steam_root: &Path) -> Vec<PathBuf> {
    let mut library_paths = vec![steam_root.to_path_buf()];
    let library_file = steam_root.join("steamapps").join("libraryfolders.vdf");

    if let Ok(contents) = fs::read_to_string(library_file)
        && let Ok(paths) = parse_library_folders(&contents)
    {
        library_paths.extend(paths);
    }

    unique_paths(library_paths)
}

fn parse_library_folders(contents: &str) -> Result<Vec<PathBuf>, String> {
    let value = parse_vdf(contents)?;
    let libraryfolders = value
        .get("libraryfolders")
        .unwrap_or(&value)
        .as_object()
        .ok_or_else(|| "Expected 'libraryfolders' object.".to_string())?;

    let mut entries = libraryfolders
        .iter()
        .filter(|(key, _)| key.chars().all(|c| c.is_ascii_digit()))
        .collect::<Vec<_>>();
    entries.sort_by(|(left, _), (right, _)| {
        left.parse::<usize>()
            .unwrap_or(usize::MAX)
            .cmp(&right.parse::<usize>().unwrap_or(usize::MAX))
            .then_with(|| left.cmp(right))
    });

    let mut library_paths = Vec::new();
    for (_, entry) in entries {
        match entry {
            VdfValue::String(path) => library_paths.push(PathBuf::from(path)),
            VdfValue::Object(object) => {
                if let Some(path) = object.get("path").and_then(VdfValue::as_str) {
                    library_paths.push(PathBuf::from(path));
                }
            }
        }
    }

    Ok(unique_paths(library_paths))
}

fn parse_app_manifest_install_dir(contents: &str) -> Result<String, String> {
    let value = parse_vdf(contents)?;
    let app_state = value
        .get("AppState")
        .unwrap_or(&value)
        .as_object()
        .ok_or_else(|| "Expected 'AppState' object.".to_string())?;

    app_state
        .get("installdir")
        .and_then(VdfValue::as_str)
        .map(ToOwned::to_owned)
        .ok_or_else(|| "Expected 'installdir' string.".to_string())
}

fn candidate_steam_roots(context: &DiscoveryContext) -> Vec<PathBuf> {
    let mut roots = Vec::new();

    match context.host {
        HostPlatform::Windows => {
            if let Some(path) = context.windows_registry_steam_path.clone() {
                roots.push(path);
            }
            if let Some(path) = context.program_files_x86.as_ref() {
                roots.push(path.join("Steam"));
            }
            if let Some(path) = context.program_files.as_ref() {
                roots.push(path.join("Steam"));
            }
        }
        HostPlatform::MacOs => {
            if let Some(home_dir) = context.home_dir.as_ref() {
                roots.push(home_dir.join("Library/Application Support/Steam"));
            }
        }
        HostPlatform::Linux => {
            if let Some(home_dir) = context.home_dir.as_ref() {
                roots.push(home_dir.join(".local/share/Steam"));
                roots.push(home_dir.join(".steam/steam"));
                roots.push(home_dir.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"));
            }

            if context.is_wsl {
                roots.extend(wsl_windows_steam_roots(&context.wsl_mount_root));
            }
        }
    }

    unique_paths(roots)
}

fn wsl_windows_steam_roots(mount_root: &Path) -> Vec<PathBuf> {
    let mut drives = fs::read_dir(mount_root)
        .ok()
        .into_iter()
        .flat_map(|entries| entries.filter_map(Result::ok))
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .map(|name| {
                    name.len() == 1
                        && name
                            .chars()
                            .next()
                            .is_some_and(|character| character.is_ascii_alphabetic())
                })
                .unwrap_or(false)
        })
        .collect::<Vec<_>>();
    drives.sort_by(|left, right| {
        left.file_name()
            .and_then(|name| name.to_str())
            .unwrap_or_default()
            .cmp(
                right
                    .file_name()
                    .and_then(|name| name.to_str())
                    .unwrap_or_default(),
            )
    });

    let mut roots = Vec::new();
    for drive_root in drives {
        roots.push(drive_root.join("Program Files (x86)/Steam"));
        roots.push(drive_root.join("Program Files/Steam"));
        roots.push(drive_root.join("Steam"));
    }

    unique_paths(roots)
}

fn unique_paths(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut seen = HashSet::new();
    let mut unique = Vec::new();

    for path in paths {
        if seen.insert(path.clone()) {
            unique.push(path);
        }
    }

    unique
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct DiscoveryContext {
    host: HostPlatform,
    home_dir: Option<PathBuf>,
    program_files_x86: Option<PathBuf>,
    program_files: Option<PathBuf>,
    windows_registry_steam_path: Option<PathBuf>,
    is_wsl: bool,
    wsl_mount_root: PathBuf,
}

impl DiscoveryContext {
    fn current() -> Self {
        Self {
            host: current_host_platform(),
            home_dir: current_home_dir(),
            program_files_x86: env::var_os("ProgramFiles(x86)").map(PathBuf::from),
            program_files: env::var_os("ProgramFiles").map(PathBuf::from),
            windows_registry_steam_path: steam_root_from_registry(),
            is_wsl: is_wsl_environment(),
            wsl_mount_root: PathBuf::from("/mnt"),
        }
    }
}

fn current_home_dir() -> Option<PathBuf> {
    env::var_os("HOME")
        .map(PathBuf::from)
        .or_else(|| env::var_os("USERPROFILE").map(PathBuf::from))
        .or_else(|| {
            let drive = env::var_os("HOMEDRIVE")?;
            let path = env::var_os("HOMEPATH")?;
            Some(PathBuf::from(drive).join(path))
        })
}

#[cfg(target_os = "windows")]
fn steam_root_from_registry() -> Option<PathBuf> {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let steam = hkcu.open_subkey("Software\\Valve\\Steam").ok()?;
    let steam_path: String = steam.get_value("SteamPath").ok()?;
    Some(PathBuf::from(steam_path))
}

#[cfg(not(target_os = "windows"))]
fn steam_root_from_registry() -> Option<PathBuf> {
    None
}

fn is_wsl_environment() -> bool {
    #[cfg(target_os = "linux")]
    {
        if Path::new("/proc/sys/fs/binfmt_misc/WSLInterop").exists() {
            return true;
        }

        fs::read_to_string("/proc/version")
            .map(|contents| {
                let lower = contents.to_ascii_lowercase();
                lower.contains("microsoft") || lower.contains("wsl")
            })
            .unwrap_or(false)
    }

    #[cfg(not(target_os = "linux"))]
    {
        false
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum VdfValue {
    String(String),
    Object(BTreeMap<String, VdfValue>),
}

impl VdfValue {
    fn as_object(&self) -> Option<&BTreeMap<String, VdfValue>> {
        match self {
            Self::Object(object) => Some(object),
            Self::String(_) => None,
        }
    }

    fn as_str(&self) -> Option<&str> {
        match self {
            Self::String(value) => Some(value),
            Self::Object(_) => None,
        }
    }

    fn get(&self, key: &str) -> Option<&VdfValue> {
        self.as_object()?.get(key)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum VdfToken {
    String(String),
    OpenBrace,
    CloseBrace,
}

fn parse_vdf(contents: &str) -> Result<VdfValue, String> {
    let tokens = tokenize_vdf(contents)?;
    let mut index = 0;
    let object = parse_vdf_object(&tokens, &mut index, false)?;

    if index != tokens.len() {
        return Err("Unexpected trailing VDF tokens.".to_string());
    }

    Ok(VdfValue::Object(object))
}

fn parse_vdf_object(
    tokens: &[VdfToken],
    index: &mut usize,
    expect_braces: bool,
) -> Result<BTreeMap<String, VdfValue>, String> {
    if expect_braces {
        match tokens.get(*index) {
            Some(VdfToken::OpenBrace) => *index += 1,
            _ => return Err("Expected '{'.".to_string()),
        }
    }

    let mut object = BTreeMap::new();
    while *index < tokens.len() {
        match tokens.get(*index) {
            Some(VdfToken::CloseBrace) if expect_braces => {
                *index += 1;
                return Ok(object);
            }
            Some(VdfToken::CloseBrace) => return Err("Unexpected '}'.".to_string()),
            Some(VdfToken::OpenBrace) => return Err("Unexpected '{'.".to_string()),
            Some(VdfToken::String(_)) => {}
            None => break,
        }

        let key = match tokens.get(*index) {
            Some(VdfToken::String(value)) => {
                *index += 1;
                value.clone()
            }
            _ => return Err("Expected VDF key string.".to_string()),
        };

        let value = match tokens.get(*index) {
            Some(VdfToken::String(value)) => {
                *index += 1;
                VdfValue::String(value.clone())
            }
            Some(VdfToken::OpenBrace) => VdfValue::Object(parse_vdf_object(tokens, index, true)?),
            Some(VdfToken::CloseBrace) | None => {
                return Err(format!("Expected value for VDF key '{key}'."));
            }
        };

        object.insert(key, value);
    }

    if expect_braces {
        Err("Expected closing '}'.".to_string())
    } else {
        Ok(object)
    }
}

fn tokenize_vdf(contents: &str) -> Result<Vec<VdfToken>, String> {
    let mut tokens = Vec::new();
    let bytes = contents.as_bytes();
    let mut index = 0;

    while index < bytes.len() {
        match bytes[index] {
            b' ' | b'\t' | b'\r' | b'\n' => index += 1,
            b'/' if bytes.get(index + 1) == Some(&b'/') => {
                index += 2;
                while index < bytes.len() && bytes[index] != b'\n' {
                    index += 1;
                }
            }
            b'{' => {
                tokens.push(VdfToken::OpenBrace);
                index += 1;
            }
            b'}' => {
                tokens.push(VdfToken::CloseBrace);
                index += 1;
            }
            b'"' => {
                index += 1;
                let mut value = String::new();

                while index < bytes.len() {
                    match bytes[index] {
                        b'\\' => {
                            index += 1;
                            let escaped = bytes.get(index).copied().ok_or_else(|| {
                                "Unterminated escape sequence in VDF.".to_string()
                            })?;
                            value.push(escaped as char);
                            index += 1;
                        }
                        b'"' => {
                            index += 1;
                            break;
                        }
                        byte => {
                            value.push(byte as char);
                            index += 1;
                        }
                    }
                }

                if index > bytes.len() {
                    return Err("Unterminated quoted VDF string.".to_string());
                }

                tokens.push(VdfToken::String(value));
            }
            byte => {
                return Err(format!(
                    "Unexpected VDF token '{}' at byte {index}.",
                    byte as char
                ));
            }
        }
    }

    Ok(tokens)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_library_folders_supports_modern_steam_shape() {
        let library_paths = parse_library_folders(
            "\"libraryfolders\"\n{\n  \"0\"\n  {\n    \"path\" \"C:\\\\Program Files (x86)\\\\Steam\"\n  }\n  \"1\"\n  {\n    \"path\" \"/games/SteamLibrary\"\n  }\n}\n",
        )
        .expect("library paths");

        assert_eq!(
            library_paths,
            vec![
                PathBuf::from("C:\\Program Files (x86)\\Steam"),
                PathBuf::from("/games/SteamLibrary"),
            ]
        );
    }

    #[test]
    fn parse_app_manifest_extracts_install_dir() {
        let install_dir = parse_app_manifest_install_dir(
            "\"AppState\"\n{\n  \"appid\" \"2868840\"\n  \"installdir\" \"Slay the Spire 2\"\n}\n",
        )
        .expect("install dir");

        assert_eq!(install_dir, "Slay the Spire 2");
    }

    #[test]
    fn candidate_steam_roots_include_wsl_windows_mounts_in_drive_order() {
        let mount_root = tempfile::tempdir().expect("mount root");
        fs::create_dir_all(mount_root.path().join("d")).expect("d drive");
        fs::create_dir_all(mount_root.path().join("c")).expect("c drive");
        fs::create_dir_all(mount_root.path().join("zz")).expect("ignored");

        let context = DiscoveryContext {
            host: HostPlatform::Linux,
            home_dir: Some(PathBuf::from("/home/tester")),
            program_files_x86: None,
            program_files: None,
            windows_registry_steam_path: None,
            is_wsl: true,
            wsl_mount_root: mount_root.path().to_path_buf(),
        };

        let roots = candidate_steam_roots(&context);

        assert_eq!(
            roots[3..],
            [
                mount_root.path().join("c/Program Files (x86)/Steam"),
                mount_root.path().join("c/Program Files/Steam"),
                mount_root.path().join("c/Steam"),
                mount_root.path().join("d/Program Files (x86)/Steam"),
                mount_root.path().join("d/Program Files/Steam"),
                mount_root.path().join("d/Steam"),
            ]
        );
    }

    #[test]
    fn discover_game_path_returns_first_valid_library_candidate() {
        let temp = tempfile::tempdir().expect("temp dir");
        let steam_root = temp.path().join("Steam");
        let secondary_library = temp.path().join("SteamLibrary");
        let game_dir = secondary_library.join("steamapps/common/Slay the Spire 2");

        fs::create_dir_all(steam_root.join("steamapps")).expect("steamapps dir");
        fs::create_dir_all(&game_dir).expect("game dir");
        fs::write(
            steam_root.join("steamapps/libraryfolders.vdf"),
            format!(
                "\"libraryfolders\"\n{{\n  \"1\"\n  {{\n    \"path\" \"{}\"\n  }}\n}}\n",
                secondary_library.display()
            ),
        )
        .expect("libraryfolders");
        fs::create_dir_all(secondary_library.join("steamapps")).expect("secondary steamapps");
        fs::write(
            secondary_library.join("steamapps/appmanifest_2868840.acf"),
            "\"AppState\"\n{\n  \"installdir\" \"Slay the Spire 2\"\n}\n",
        )
        .expect("manifest");

        let discovered = discover_game_path_from_steam_root(&steam_root).expect("game path");

        assert_eq!(discovered, game_dir);
    }
}
