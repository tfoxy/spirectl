use super::{Remote, error, ps_literal};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Command, Stdio},
};

pub(super) const ASSEMBLIES_DIR: &str = "data_sts2_windows_x86_64";
const ASSEMBLIES: &[&str] = &[
    "sts2.dll",
    "GodotSharp.dll",
    "0Harmony.dll",
    "Steamworks.NET.dll",
];

pub(super) fn required_str<'a>(value: &'a Value, key: &str) -> Result<&'a str, crate::AppError> {
    value
        .get(key)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| {
            error(
                "remote_invalid_response",
                format!("missing string field {key}"),
            )
        })
}

pub(super) fn local_error(source: impl ToString) -> crate::AppError {
    error("remote_local_io_failed", source)
}

pub(super) fn transfer_id() -> String {
    format!(
        "{}-{}",
        std::process::id(),
        std::time::UNIX_EPOCH
            .elapsed()
            .unwrap_or_default()
            .as_nanos()
    )
}

fn safe_component(value: &str) -> bool {
    !value.is_empty()
        && value != "."
        && value != ".."
        && value
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || "._-@".contains(c))
}

pub(super) fn validate_stage(path: &str) -> Result<(), crate::AppError> {
    // Keep scp destinations literal across SFTP and legacy remote-shell implementations.
    let bytes = path.as_bytes();
    if bytes.len() < 4
        || !bytes[0].is_ascii_alphabetic()
        || bytes[1] != b':'
        || !b"\\/".contains(&bytes[2])
        || !path[2..]
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || "\\/._-".contains(c))
        || path.split(['\\', '/']).any(|p| p == "." || p == "..")
    {
        return Err(error(
            "remote_stage_invalid",
            "remote.stageDir must be an absolute Windows drive path without spaces or shell metacharacters",
        ));
    }
    Ok(())
}

impl Remote<'_> {
    pub(super) fn scp(&self, source: &str, destination: &str) -> Result<(), crate::AppError> {
        let output = Command::new(&self.config.scp_command)
            .args(["-o", "BatchMode=yes", "--", source, destination])
            .stdin(Stdio::null())
            .output()
            .map_err(|e| error("remote_transfer_failed", e))?;
        if !output.status.success() {
            return Err(self.command_error(
                if output.status.code() == Some(255) {
                    "remote_unreachable"
                } else {
                    "remote_transfer_failed"
                },
                &output,
            ));
        }
        Ok(())
    }

    pub(super) fn scp_path(&self, path: &str) -> String {
        format!("{}:{}", self.host, path.replace('\\', "/"))
    }

    pub(super) fn sync_assemblies(
        &self,
        status: &Value,
        refresh: bool,
    ) -> Result<Value, crate::AppError> {
        let release = &status["releaseInfo"];
        let version = required_str(release, "version")?;
        if !safe_component(version) {
            return Err(error(
                "remote_invalid_response",
                "invalid game version for cache key",
            ));
        }
        let identity = if let Some(hash) = release["main_assembly_hash"].as_i64() {
            hash.to_string()
        } else {
            let hash = self.run("assembly-hash", ASSEMBLY_HASH)?;
            let sha = required_str(&hash, "sha256")?;
            if sha.len() != 64 || !sha.bytes().all(|c| c.is_ascii_hexdigit()) {
                return Err(error("remote_invalid_response", "invalid assembly SHA-256"));
            }
            sha.to_ascii_lowercase()
        };
        let cache = std::env::current_dir()
            .map_err(local_error)?
            .join(".sts2/remote")
            .join(self.host)
            .join(format!("{version}-{identity}"));
        let assemblies = cache.join(ASSEMBLIES_DIR);
        let cache_hit = !refresh && complete_cache(&cache, release);
        if !cache_hit {
            let stage = required_str(status, "stageDir")?;
            validate_stage(stage)?;
            let token = transfer_id();
            let archive = format!(
                "{}\\assemblies-{token}.zip",
                stage.trim_end_matches(['\\', '/'])
            );
            let body = format!(
                "$transferName = {}\n{SYNC}",
                ps_literal(&format!("assemblies-{token}"))
            );
            self.run("sync-assemblies", &body)?;
            let pending = cache.with_file_name(format!(".fetch-{token}"));
            fs::create_dir_all(&pending).map_err(local_error)?;
            let download = pending.join("assemblies.zip");
            let result = (|| {
                self.scp(&self.scp_path(&archive), &download.to_string_lossy())?;
                unpack_assemblies(&download, &pending)?;
                fs::remove_file(&download).map_err(local_error)?;
                if !complete_cache(&pending, release) {
                    return Err(error(
                        "remote_game_changed",
                        "release metadata changed during assembly transfer; retry sync",
                    ));
                }
                if release["main_assembly_hash"].as_i64().is_none() {
                    let dll = fs::read(pending.join(ASSEMBLIES_DIR).join("sts2.dll"))
                        .map_err(local_error)?;
                    if format!("{:x}", Sha256::digest(dll)) != identity {
                        return Err(error(
                            "remote_game_changed",
                            "assembly hash changed during transfer; retry sync",
                        ));
                    }
                }
                if cache.exists() {
                    fs::remove_dir_all(&cache).map_err(local_error)?;
                }
                fs::rename(&pending, &cache).map_err(local_error)
            })();
            let _ = fs::remove_dir_all(&pending);
            let cleanup = format!(
                "$transferName = {}\n{CLEAN_SYNC}",
                ps_literal(&format!("assemblies-{token}"))
            );
            let cleanup_result = self.run("sync-cleanup", &cleanup);
            result?;
            cleanup_result?;
        }
        Ok(
            json!({"host":self.host,"gamePath":status["gamePath"],"releaseInfo":release,"apiLane":crate::game_build::api_lane_for_game_version(version),"mainAssemblySha256":if release["main_assembly_hash"].as_i64().is_none() {Some(identity)} else {None},"cacheDir":cache,"assembliesDir":assemblies,"cacheHit":cache_hit}),
        )
    }
}

fn complete_cache(cache: &Path, release: &Value) -> bool {
    let metadata = fs::read(cache.join("release_info.json"))
        .ok()
        .and_then(|b| serde_json::from_slice::<Value>(&b).ok());
    metadata.as_ref() == Some(release)
        && ASSEMBLIES
            .iter()
            .all(|name| cache.join(ASSEMBLIES_DIR).join(name).is_file())
}

fn unpack_assemblies(archive: &Path, destination: &Path) -> Result<(), crate::AppError> {
    let mut zip =
        zip::ZipArchive::new(fs::File::open(archive).map_err(local_error)?).map_err(local_error)?;
    let mut indices = std::collections::HashMap::new();
    for index in 0..zip.len() {
        let entry = zip.by_index(index).map_err(local_error)?;
        indices.insert(entry.name().replace('\\', "/"), index);
    }
    let entries = std::iter::once("release_info.json".to_string()).chain(
        ASSEMBLIES
            .iter()
            .map(|name| format!("{ASSEMBLIES_DIR}/{name}")),
    );
    for name in entries {
        let index = indices
            .get(&name)
            .ok_or_else(|| local_error(zip::result::ZipError::FileNotFound))?;
        let mut entry = zip.by_index(*index).map_err(local_error)?;
        if entry.size() > 512 * 1024 * 1024 {
            return Err(error(
                "remote_archive_invalid",
                "assembly archive entry exceeds size limit",
            ));
        }
        let path: PathBuf = destination.join(&name);
        fs::create_dir_all(path.parent().unwrap()).map_err(local_error)?;
        let mut bytes = Vec::new();
        entry.read_to_end(&mut bytes).map_err(local_error)?;
        fs::write(path, bytes).map_err(local_error)?;
    }
    Ok(())
}

const ASSEMBLY_HASH: &str = r#"
@{sha256=(Get-FileHash -LiteralPath (Join-Path $gamePath 'data_sts2_windows_x86_64\sts2.dll') -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json -Compress
"#;

const SYNC: &str = r#"
Assert-Stage
$transferDir = Join-Path $stageDir $transferName
New-Item -ItemType Directory -Path $transferDir -Force | Out-Null
$target = Join-Path $transferDir 'data_sts2_windows_x86_64'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $gamePath 'release_info.json') -Destination $transferDir
foreach ($name in @('sts2.dll','GodotSharp.dll','0Harmony.dll','Steamworks.NET.dll')) {
    Copy-Item -LiteralPath (Join-Path $gamePath ('data_sts2_windows_x86_64\' + $name)) -Destination $target
}
$archive = Join-Path $stageDir ($transferName + '.zip')
Compress-Archive -LiteralPath @((Join-Path $transferDir 'release_info.json'),$target) -DestinationPath $archive -Force
@{archive=$archive} | ConvertTo-Json -Compress
"#;

const CLEAN_SYNC: &str = r#"
Remove-Item -LiteralPath (Join-Path $stageDir $transferName) -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $stageDir ($transferName + '.zip')) -Force -ErrorAction SilentlyContinue
@{cleaned=$true} | ConvertTo-Json -Compress
"#;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn remote_stage_rejects_spaces_relative_paths_and_shell_syntax() {
        for path in [
            "C:\\Users\\Some User\\stage",
            "stage",
            "C:\\a;echo",
            "C:\\a\\..\\game",
            "C:\\a'",
            "C:\\a$",
            "C:/a/./b",
        ] {
            assert!(validate_stage(path).is_err(), "{path}");
        }
        for path in [
            "C:\\stage",
            "D:/spirectl/stage-1",
            "C:\\Users\\agent\\AppData\\Local\\spirectl\\stage",
        ] {
            assert!(validate_stage(path).is_ok(), "{path}");
        }
    }
}
