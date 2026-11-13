use super::sync::{local_error, required_str, transfer_id, validate_stage};
use super::{Remote, error, ps_literal};
use crate::{AppContext, GameInstallBridgeArgs, GameRemoteInstallArgs};
use serde_json::{Value, json};
use std::{fs, io::Write, path::Path};

impl Remote<'_> {
    pub(super) fn install_bridge(
        &self,
        args: GameRemoteInstallArgs,
        context: AppContext<'_>,
    ) -> Result<Value, crate::AppError> {
        let spec = args.endpoint.as_deref().unwrap_or("tcp:127.0.0.1:51173");
        let endpoint = crate::endpoint::EndpointSpec::parse(spec)?;
        if matches!(endpoint, crate::endpoint::EndpointSpec::Unix(_)) {
            return Err(error(
                "remote_endpoint_invalid",
                "Windows installs support TCP or named-pipe endpoints",
            ));
        }
        let status = self.status()?;
        if !args.force
            && status["processes"]
                .as_array()
                .is_some_and(|p| !p.is_empty())
        {
            return Err(error(
                "remote_game_running",
                "stop the remote game or pass --force",
            ));
        }
        let stage = required_str(&status, "stageDir")?;
        validate_stage(stage)?;
        let synced = self.sync_assemblies(&status, false)?;
        let token = transfer_id();
        let job = Path::new(required_str(&synced, "cacheDir")?)
            .parent()
            .unwrap()
            .join(format!(".install-{token}"));
        fs::create_dir_all(&job).map_err(local_error)?;
        let result = (|| {
            let mut config = context.config.clone();
            config.game.path = required_str(&synced, "cacheDir")?.to_string();
            config.game.assemblies_dir = Some(required_str(&synced, "assembliesDir")?.to_string());
            config.game.mods_dir = Some(job.join("mods").to_string_lossy().into_owned());
            config.game.user_dir = None;
            config.artifacts.dir = job.join("artifacts").to_string_lossy().into_owned();
            endpoint.apply(&mut config);
            let mut result = crate::lifecycle::execute_game_install_bridge_json(
                GameInstallBridgeArgs {
                    endpoint: Some(spec.to_string()),
                    force: true,
                    skip_mod_load_order: cfg!(target_os = "windows"),
                    ..Default::default()
                },
                AppContext {
                    config: &config,
                    instance: None,
                    ..context
                },
            )?;
            let profile = args.profile_root.as_deref().unwrap_or("");
            let parameters = format!(
                "$force = {}\n$profileRoot = {}\n",
                if args.force { "$true" } else { "$false" },
                ps_literal(profile)
            );
            let prepared = self.run("prepare-install", &format!("{parameters}{PREPARE}"))?;
            let dependents: Vec<String> =
                serde_json::from_value(prepared["dependentModIds"].clone())
                    .map_err(|e| error("remote_invalid_response", e))?;
            let settings = prepared["settingsFiles"]
                .as_array()
                .ok_or_else(|| error("remote_invalid_response", "missing settingsFiles array"))?;
            let mut patches = Vec::new();
            let mut reports = Vec::new();
            for file in settings {
                let path = required_str(file, "path")?;
                let text = required_str(file, "text")?;
                let (updated, before, after) = enabled_settings(text, &dependents)?;
                if let Some(text) = updated.as_ref() {
                    patches.push(
                        json!({"path":path,"text":text,"sha256":required_str(file,"sha256")?}),
                    );
                }
                reports.push(json!({"path":path,"status":if updated.is_some() {"updated"} else {"unchanged"},"changed":updated.is_some(),"beforeOrder":before,"afterOrder":after}));
            }
            let archive = job.join("bridge.zip");
            create_archive(&archive, &job.join("mods/spirectlbridge"), &patches)?;
            let remote_archive = format!(
                "{}\\bridge-{token}.zip",
                stage.trim_end_matches(['\\', '/'])
            );
            self.scp(&archive.to_string_lossy(), &self.scp_path(&remote_archive))?;
            let parameters = format!(
                "{parameters}$transferName = {}\n$expectedVersion = {}\n$expectedHash = {}\n$expectedSha = {}\n",
                ps_literal(&format!("bridge-{token}")),
                ps_literal(required_str(&status["releaseInfo"], "version")?),
                ps_literal(
                    &status["releaseInfo"]["main_assembly_hash"]
                        .as_i64()
                        .map(|n| n.to_string())
                        .unwrap_or_default()
                ),
                ps_literal(synced["mainAssemblySha256"].as_str().unwrap_or(""))
            );
            let installed = self.run("install-bridge", &format!("{parameters}{INSTALL}"))?;
            let backups = installed["settingsFiles"].as_array().ok_or_else(|| {
                error(
                    "remote_invalid_response",
                    "missing settings install reports",
                )
            })?;
            for report in &mut reports {
                report["backupPath"] = backups
                    .iter()
                    .find(|b| b["path"] == report["path"])
                    .map(|b| b["backupPath"].clone())
                    .unwrap_or(Value::Null);
            }
            result["host"] = json!(self.host);
            result["modDir"] = installed["modDir"].clone();
            result["modsDir"] = installed["modsDir"].clone();
            result["socketPath"] = Value::Null;
            match &endpoint {
                crate::endpoint::EndpointSpec::Tcp(address) => {
                    result["endpoint"] =
                        json!({"transportKind":"tcp","kind":"tcp","address":address});
                    result["tcpAddress"] = json!(address);
                    result["pipeName"] = Value::Null;
                }
                crate::endpoint::EndpointSpec::Pipe(name) => {
                    result["endpoint"] = json!({"transportKind":"ipc","kind":"named-pipe","name":name,"pipeName":name});
                    result["tcpAddress"] = Value::Null;
                    result["pipeName"] = json!(name);
                }
                crate::endpoint::EndpointSpec::Unix(_) => {
                    unreachable!("validated Windows endpoint")
                }
            }
            result["endpoint"]["file"] = json!(format!(
                "{}\\spirectl-bridge.endpoint.json",
                required_str(&installed, "modDir")?
            ));
            result["endpoint"]["source"] = json!(if args.endpoint.is_some() {
                "flag"
            } else {
                "remote-default"
            });
            result["builtAgainstGame"] = result["acquisition"]["builtAgainstGame"].clone();
            result["forced"] = json!(args.force);
            result["cacheHit"] = synced["cacheHit"].clone();
            result["modLoadOrder"] = json!({"status":"completed","settingsRoot":prepared["profileRoot"],"dependentModIds":dependents,"settingsFiles":reports});
            Ok(result)
        })();
        let _ = fs::remove_dir_all(&job);
        result
    }
}

pub(super) fn enabled_settings(
    text: &str,
    dependents: &[String],
) -> Result<(Option<String>, Vec<String>, Vec<String>), crate::AppError> {
    let mut settings: Value =
        serde_json::from_str(text).map_err(|e| error("remote_settings_invalid", e))?;
    let root = settings
        .as_object_mut()
        .ok_or_else(|| error("remote_settings_invalid", "settings must be an object"))?;
    // The game writes `"mod_settings": null` until mods are first enabled; treat null as empty.
    let mods = root.entry("mod_settings").or_insert_with(|| json!({}));
    if mods.is_null() {
        *mods = json!({});
    }
    let mods = mods
        .as_object_mut()
        .ok_or_else(|| error("remote_settings_invalid", "mod_settings must be an object"))?;
    mods.insert("mods_enabled".into(), json!(true));
    let list = mods.entry("mod_list").or_insert_with(|| json!([]));
    if list.is_null() {
        *list = json!([]);
    }
    let list = list
        .as_array_mut()
        .ok_or_else(|| error("remote_settings_invalid", "mod_list must be an array"))?;
    let before = list
        .iter()
        .filter_map(|v| v["id"].as_str().map(str::to_string))
        .collect();
    if !list.iter().any(|v| v["id"] == "spirectlbridge") {
        list.push(json!({"id":"spirectlbridge","is_enabled":true,"source":"mods_directory"}));
    }
    for entry in list.iter_mut().filter(|v| v["id"] == "spirectlbridge") {
        entry["is_enabled"] = json!(true);
    }
    let enabled = serde_json::to_string(&settings).map_err(local_error)?;
    let reorder = crate::lifecycle::reorder_mod_settings(&enabled, dependents)
        .map_err(|e| error("remote_settings_invalid", format!("{e:?}")))?;
    let updated = reorder.updated_text.unwrap_or(enabled);
    let changed = serde_json::from_str::<Value>(&updated).map_err(local_error)?
        != serde_json::from_str::<Value>(text).map_err(local_error)?;
    Ok((changed.then_some(updated), before, reorder.after_order))
}

fn create_archive(path: &Path, mod_dir: &Path, patches: &[Value]) -> Result<(), crate::AppError> {
    let mut zip = zip::ZipWriter::new(fs::File::create(path).map_err(local_error)?);
    zip_directory(&mut zip, mod_dir, Path::new("spirectlbridge"))?;
    zip.start_file(
        "settings-patches.json",
        zip::write::SimpleFileOptions::default(),
    )
    .map_err(local_error)?;
    zip.write_all(&serde_json::to_vec(patches).map_err(local_error)?)
        .map_err(local_error)?;
    zip.finish().map_err(local_error)?;
    Ok(())
}

fn zip_directory(
    zip: &mut zip::ZipWriter<fs::File>,
    directory: &Path,
    relative: &Path,
) -> Result<(), crate::AppError> {
    let mut entries = fs::read_dir(directory)
        .map_err(local_error)?
        .collect::<Result<Vec<_>, _>>()
        .map_err(local_error)?;
    entries.sort_by_key(|e| e.file_name());
    for entry in entries {
        let name = relative.join(entry.file_name());
        let kind = entry.file_type().map_err(local_error)?;
        if kind.is_dir() {
            zip_directory(zip, &entry.path(), &name)?;
        } else if kind.is_file() {
            zip.start_file(
                name.to_string_lossy().replace('\\', "/"),
                zip::write::SimpleFileOptions::default()
                    .compression_method(zip::CompressionMethod::Deflated),
            )
            .map_err(local_error)?;
            zip.write_all(&fs::read(entry.path()).map_err(local_error)?)
                .map_err(local_error)?;
        }
    }
    Ok(())
}

const PREPARE: &str = r#"
Assert-Stage
if (!$force -and @(Game-Processes).Count) { Fail 'remote_game_running' 'Stop the remote game or pass --force.' }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
if (!$profileRoot) { $profileRoot = Join-Path $env:APPDATA 'SlayTheSpire2' }
$profileRoot = [Environment]::ExpandEnvironmentVariables($profileRoot)
$modsDir = Join-Path $gamePath 'mods'
$dependents = @()
if (Test-Path -LiteralPath $modsDir) {
    foreach ($dir in Get-ChildItem -LiteralPath $modsDir -Directory) {
        if ($dir.Name -eq 'spirectlbridge' -or $dir.Name.StartsWith('.spirectlbridge')) { continue }
        if (@(Get-ChildItem -LiteralPath $dir.FullName -Filter 'Spirectl.Sts2.dll' -Recurse -File).Count) {
            $id = $dir.Name
            foreach ($manifest in Get-ChildItem -LiteralPath $dir.FullName -Filter '*.json' -File) {
                try { $data = Read-Json $manifest.FullName } catch { continue }
                if ($data.id) { $id = $data.id; break }
            }
            $dependents += $id
        }
    }
}
$settings = @()
if (Test-Path -LiteralPath $profileRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $profileRoot -Filter 'settings.save' -Recurse -File) {
        $settings += @{path=$file.FullName;text=[IO.File]::ReadAllText($file.FullName);sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    }
}
@{profileRoot=$profileRoot;dependentModIds=@($dependents | Sort-Object -Unique);settingsFiles=@($settings)} | ConvertTo-Json -Compress -Depth 20
"#;

const INSTALL: &str = r#"
Assert-Stage
$archive = Join-Path $stageDir ($transferName + '.zip')
$unpack = Join-Path $stageDir $transferName
$modsDir = Join-Path $gamePath 'mods'
$modDir = Join-Path $modsDir 'spirectlbridge'
$new = Join-Path $modsDir '.spirectlbridge.new'
$old = Join-Path $modsDir ('.spirectlbridge.old-' + $transferName)
$ownsNew = $false
try {
    if (!$force -and @(Game-Processes).Count) { Fail 'remote_game_running' 'Stop the remote game or pass --force.' }
    $release = Read-Json (Join-Path $gamePath 'release_info.json')
    if ($release.version -ne $expectedVersion -or ($expectedHash -and [string]$release.main_assembly_hash -ne $expectedHash)) {
        Fail 'remote_game_changed' 'Game build changed during bridge build; retry installation.'
    }
    if ($expectedSha -and (Get-FileHash -LiteralPath (Join-Path $gamePath 'data_sts2_windows_x86_64\sts2.dll') -Algorithm SHA256).Hash -ne $expectedSha) {
        Fail 'remote_game_changed' 'Game assembly changed during bridge build; retry installation.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $unpack
    # Windows PowerShell 5.1 emits a JSON array as one object; wrapping it in @() would turn an empty
    # patch list into a one-element list holding an empty array.
    $patches = Get-Content -LiteralPath (Join-Path $unpack 'settings-patches.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($patch in $patches) {
        if ((Get-FileHash -LiteralPath $patch.path -Algorithm SHA256).Hash -ne $patch.sha256) {
            Fail 'remote_settings_changed' 'Settings changed during installation; retry.'
        }
    }
    New-Item -ItemType Directory -Path $modsDir -Force | Out-Null
    if (Test-Path -LiteralPath $new) { Fail 'remote_install_in_progress' 'mods\.spirectlbridge.new already exists.' }
    New-Item -ItemType Directory -Path $new | Out-Null
    $ownsNew = $true
    Get-ChildItem -LiteralPath (Join-Path $unpack 'spirectlbridge') | Copy-Item -Destination $new -Recurse -Force
    if (Test-Path -LiteralPath $modDir) { Rename-Item -LiteralPath $modDir -NewName (Split-Path $old -Leaf) }
    try {
        Rename-Item -LiteralPath $new -NewName 'spirectlbridge'
        $ownsNew = $false
    } catch {
        if (Test-Path -LiteralPath $old) { Rename-Item -LiteralPath $old -NewName 'spirectlbridge' }
        throw
    }
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    $reports = @()
    foreach ($patch in $patches) {
        $backup = $patch.path + '.spirectl-backup-' + $transferName
        $temp = $patch.path + '.spirectl-new-' + $transferName
        try {
            [IO.File]::WriteAllText($temp, $patch.text, (New-Object System.Text.UTF8Encoding($false)))
            [IO.File]::Replace($temp, $patch.path, $backup)
        } finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
        }
        $reports += @{path=$patch.path;backupPath=$backup}
    }
    @{modDir=$modDir;modsDir=$modsDir;settingsFiles=@($reports)} | ConvertTo-Json -Compress -Depth 20
} finally {
    if ($ownsNew -and (Test-Path -LiteralPath $new)) { Remove-Item -LiteralPath $new -Recurse -Force }
    if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
"#;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn enabled_settings_accepts_null_mod_settings_from_a_never_modded_profile() {
        for text in [
            r#"{"msaa":2,"mod_settings":null}"#,
            r#"{"msaa":2,"mod_settings":{"mods_enabled":false,"mod_list":null}}"#,
        ] {
            let (updated, _, _) = enabled_settings(text, &[]).expect("null treated as empty");
            let value: Value = serde_json::from_str(&updated.expect("changed")).unwrap();
            assert_eq!(value["msaa"], 2);
            assert_eq!(value["mod_settings"]["mods_enabled"], true);
            assert_eq!(value["mod_settings"]["mod_list"][0]["id"], "spirectlbridge");
            assert_eq!(value["mod_settings"]["mod_list"][0]["is_enabled"], true);
        }
    }
    #[test]
    fn remote_settings_enable_insert_reorder_and_preserve_fields() {
        for text in [
            r#"{"volume":0.5}"#,
            r#"{"volume":0.5,"mod_settings":{"other":7,"mod_list":[{"id":"consumer"},{"id":"spirectlbridge","is_enabled":false,"extra":9}]}}"#,
        ] {
            let (updated, _, _) = enabled_settings(text, &["consumer".into()]).unwrap();
            let value: Value = serde_json::from_str(&updated.unwrap()).unwrap();
            assert_eq!(value["volume"], 0.5);
            assert_eq!(value["mod_settings"]["mod_list"][0]["id"], "spirectlbridge");
            assert_eq!(value["mod_settings"]["mod_list"][0]["is_enabled"], true);
            assert_eq!(value["mod_settings"]["mods_enabled"], true);
            if text.contains("extra") {
                assert_eq!(value["mod_settings"]["mod_list"][0]["extra"], 9);
                assert_eq!(value["mod_settings"]["other"], 7);
            }
            assert!(
                enabled_settings(&value.to_string(), &["consumer".into()])
                    .unwrap()
                    .0
                    .is_none()
            );
        }
    }
}
