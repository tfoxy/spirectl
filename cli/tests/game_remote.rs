#![cfg(unix)]
use serde_json::{Value, json};
use std::{fs, os::unix::fs::PermissionsExt, path::Path, process::Command};

struct Harness {
    dir: tempfile::TempDir,
}
impl Harness {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        executable(
            &dir.path().join("ssh"),
            r#"#!/usr/bin/env python3
import base64,json,os,sys
from pathlib import Path
args=sys.argv[1:]
assert args[:2]==['-o','BatchMode=yes'],args
assert args[2]=='fake-windows',args
assert args[3:7]==['powershell','-NoProfile','-NonInteractive','-EncodedCommand'],args
bootstrap=base64.b64decode(args[7]).decode('utf-16-le')
assert '[Console]::In.ReadToEnd()' in bootstrap
assert len(args[7]) < 1024
script=base64.b64decode(sys.stdin.read()).decode('utf-16-le')
assert len(args)==8,args
with open('ssh.log','a') as f: f.write(json.dumps({'argv':args,'script':script})+'\n')
if os.getenv('FAKE_EXIT'):
    print('scripted failure',file=sys.stderr)
    sys.exit(int(os.environ['FAKE_EXIT']))
operation=script.splitlines()[0].removeprefix('# spirectl:')
if Path('operation-fail').exists() and operation==Path('operation-fail').read_text():
    print(json.dumps({'error':{'code':'remote_settings_changed','message':'scripted conflict'}}))
    sys.exit(1)
if operation=='status':
    print(Path('response.json').read_text())
elif operation=='assembly-hash':
    import hashlib
    print(json.dumps({'sha256':hashlib.sha256(b'fake sts2.dll').hexdigest()}))
elif operation in ('sync-assemblies','sync-cleanup'):
    if operation=='sync-assemblies':
        assert 'Compress-Archive -LiteralPath' in script
        for name in ('sts2.dll','GodotSharp.dll','0Harmony.dll','Steamworks.NET.dll'):
            assert name in script
    print('{}')
elif operation=='prepare-install':
    assert 'Get-FileHash -LiteralPath' in script
    print(Path('prepared.json').read_text() if Path('prepared.json').exists() else '{"profileRoot":"C:/profile","dependentModIds":[],"settingsFiles":[]}')
elif operation=='install-bridge':
    import zipfile
    assert 'Assert-Stage' in script
    assert "Get-Content -LiteralPath (Join-Path $unpack 'settings-patches.json') -Raw -Encoding UTF8" in script
    assert "Get-Content -LiteralPath (Join-Path $unpack 'settings-patches.json') -Raw |" not in script
    assert "@(Get-Content -LiteralPath (Join-Path $unpack 'settings-patches.json')" not in script
    assert "$ProgressPreference = 'SilentlyContinue'" in script
    assert 'Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json' in script
    if Path('force-expected').exists(): assert '$force = $true' in script
    assert script.index('Copy-Item -Destination $new') < script.index('Rename-Item -LiteralPath $modDir') < script.index('Rename-Item -LiteralPath $new')
    assert "'.spirectlbridge.new'" in script
    assert 'Rename-Item -LiteralPath $old' in script
    assert "'.spirectl-backup-'" in script and '[IO.File]::Replace($temp, $patch.path, $backup)' in script
    assert "Fail 'remote_game_running'" in script and "Fail 'remote_settings_changed'" in script
    with zipfile.ZipFile('uploaded.zip') as z:
        patches=json.loads(z.read('settings-patches.json'))
        manifest=json.loads(z.read('spirectlbridge/spirectlbridge.json'))
        endpoint=json.loads(z.read('spirectlbridge/spirectl-bridge.endpoint.json'))
        Path('installed.json').write_text(json.dumps({'patches':patches,'manifest':manifest,'endpoint':endpoint,'names':z.namelist()}))
    print(json.dumps({'modDir':'C:/Games/STS 2/mods/spirectlbridge','modsDir':'C:/Games/STS 2/mods','settingsFiles':[{'path':p['path'],'backupPath':p['path']+'.spirectl-backup-example'} for p in patches]}))
elif operation=='prepare-launch':
    assert "'Roaming\\SlayTheSpire2'" in script
    assert "'default\\1\\settings.save'" in script
    assert 'ActiveUser' in script and '76561197960265728' in script
    print(Path('launch-prepared.json').read_text() if Path('launch-prepared.json').exists() else '{"settingsFiles":[]}')
elif operation=='launch':
    import re
    assert '-LogonType Interactive' in script
    assert "$env:COMPUTERNAME + '\\' + $env:USERNAME" in script
    assert 'USERDOMAIN' not in script
    assert '$env:APPDATA = ' in script and '$env:LOCALAPPDATA = ' in script
    assert 'Register-ScheduledTask' in script and 'Start-ScheduledTask' in script
    assert script.index('Start-ScheduledTask') < script.rindex('finally {') < script.index('Unregister-ScheduledTask')
    assert 'Remove-Item -LiteralPath $launcher' in script
    assert '"--windowed"' in script
    assert '[Diagnostics.Process]::Start($start)' in script
    assert '$start.UseShellExecute = $false' in script
    assert '[IO.File]::WriteAllText($launcher' in script
    assert 'remote_launch_timeout' in script
    assert 'remote_settings_changed' in script
    patches=json.loads(re.search(r"^\$patches = ConvertFrom-Json '(.*)'$",script,re.M)[1].replace("''", "'"))
    Path('launch-patches.json').write_text(json.dumps(patches))
    if Path('launch-error').exists():
        if Path('launch-error').read_text()=='remote_unreachable':
            print('transport dropped',file=sys.stderr)
            sys.exit(255)
        print(json.dumps({'error':{'code':Path('launch-error').read_text(),'message':'scripted launch failure'}}))
        sys.exit(1)
    response=json.loads(Path('response.json').read_text())
    print(json.dumps({'pid':456,'gamePath':response['gamePath'],'endpoint':response['bridge']['endpoint']}))
elif operation=='launch-cleanup':
    assert 'Unregister-ScheduledTask' in script and 'Remove-Item -LiteralPath $launcher' in script
    if Path('cleanup-error').exists():
        print(json.dumps({'error':{'code':'remote_command_failed','message':'scripted cleanup failure'}}))
        sys.exit(1)
    print('{"deleted":true}')
elif operation=='stop':
    assert 'CloseMainWindow()' in script
    assert script.index('CloseMainWindow()') < script.index('Stop-Process') < script.index('finally {') < script.index('Unregister-ScheduledTask')
    assert '$clock.ElapsedMilliseconds -lt $timeoutMs' in script
    assert '$clock.ElapsedMilliseconds -lt 5000' in script
    if Path('stop-error').exists():
        print(json.dumps({'error':{'code':'remote_stop_timeout','message':'scripted remaining process'}}))
        sys.exit(1)
    response=json.loads(Path('response.json').read_text())
    print(json.dumps({'stoppedPids':[p['pid'] for p in response['processes']],'deletedTasks':response['launchTasks'],'forced':'$force = $true' in script}))
else:
    raise AssertionError(operation)
"#,
        );
        executable(
            &dir.path().join("scp"),
            r#"#!/usr/bin/env python3
import json,sys,zipfile,shutil
from pathlib import Path
args=sys.argv[1:]
assert args[:3]==['-o','BatchMode=yes','--'],args
with open('scp.log','a') as f: f.write(json.dumps(args)+'\n')
if Path('scp-fail').exists():
    print('scp failed',file=sys.stderr)
    sys.exit(9)
source,destination=args[-2:]
remote=source if source.startswith('fake-windows:') else destination
assert remote.startswith('fake-windows:C:/stage/'),remote
assert ' ' not in remote,remote
if source.startswith('fake-windows:'):
    release=json.loads(Path('response.json').read_text())['releaseInfo']
    with zipfile.ZipFile(destination,'w') as z:
        z.writestr('release_info.json',json.dumps(release))
        for name in ('sts2.dll','GodotSharp.dll','0Harmony.dll','Steamworks.NET.dll'):
            if Path('missing-dll').exists() and name=='Steamworks.NET.dll': continue
            separator='\\' if Path('backslash-entries').exists() else '/'
            z.writestr('data_sts2_windows_x86_64'+separator+name,('fake '+name).encode())
else:
    shutil.copyfile(source,'uploaded.zip')
"#,
        );
        executable(
            &dir.path().join("dotnet"),
            r#"#!/usr/bin/env python3
import json,sys
from pathlib import Path
args=sys.argv[1:]
with open('dotnet.log','a') as f: f.write(json.dumps(args)+'\n')
assemblies=next(a.split('=',1)[1] for a in args if a.startswith('-p:Sts2AssembliesDir='))
assert 'data_sts2_windows_x86_64' in assemblies
assert json.loads((Path(assemblies).parent/'release_info.json').read_text())['version']=='v0.111.0'
if args[0]=='build':
    out=Path(args[1]).parent/'bin'/'Debug'/'net9.0'
    names=['spirectlbridge.dll']
elif args[0]=='publish':
    out=Path(args[args.index('--output')+1])
    names=['Spirectl.BridgeMod.Sts2Host.dll','Spirectl.BridgeMod.dll','Spirectl.Sts2.dll']
else: raise AssertionError(args)
out.mkdir(parents=True,exist_ok=True)
for name in names: (out/name).write_bytes(b'fake managed IL')
"#,
        );
        let config = json!({"remote":{"host":"fake-windows","gamePath":"C:\\Games\\STS 2","sshCommand":dir.path().join("ssh"),"scpCommand":dir.path().join("scp")}});
        fs::write(
            dir.path().join("config.yaml"),
            serde_yaml::to_string(&config).unwrap(),
        )
        .unwrap();
        Self { dir }
    }
    fn response(&self, value: Value) {
        fs::write(self.dir.path().join("response.json"), value.to_string()).unwrap();
    }
    fn run(&self, args: &[&str], exit: Option<&str>) -> (i32, Value) {
        let mut cmd = Command::new(env!("CARGO_BIN_EXE_sts2"));
        cmd.current_dir(self.dir.path())
            .args(["--config", "config.yaml", "--json", "game", "remote"])
            .args(args)
            .env(
                "PATH",
                format!(
                    "{}:{}",
                    self.dir.path().display(),
                    std::env::var("PATH").unwrap()
                ),
            );
        if let Some(exit) = exit {
            cmd.env("FAKE_EXIT", exit);
        }
        let output = cmd.output().unwrap();
        let bytes = &output.stdout;
        let payload = serde_json::from_slice(bytes).unwrap_or_else(|_| {
            panic!(
                "stdout={} stderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            )
        });
        (output.status.code().unwrap(), payload)
    }
}
fn executable(path: &Path, body: &str) {
    fs::write(path, body).unwrap();
    fs::set_permissions(path, fs::Permissions::from_mode(0o755)).unwrap();
}
fn status(processes: Value) -> Value {
    json!({"gamePath":"C:\\Games\\STS 2","stageDir":"C:\\stage","releaseInfo":{"version":"v0.111.0","main_assembly_hash":42},"bridge":{"present":true,"endpoint":{"kind":"tcp","address":"127.0.0.1:51173"},"buildIdentity":{"contentHash":"example"}},"processes":processes,"launchTasks":[]})
}
#[test]
fn remote_status_reports_identity_and_encoded_transport() {
    let h = Harness::new();
    h.response(status(json!([])));
    let (code, value) = h.run(&["status", "--host", "fake-windows"], None);
    assert_eq!(code, 0);
    assert_eq!(value["host"], "fake-windows");
    assert_eq!(value["apiLane"], "v111");
    let log = fs::read_to_string(h.dir.path().join("ssh.log")).unwrap();
    let call: Value = serde_json::from_str(log.trim()).unwrap();
    assert!(
        call["script"]
            .as_str()
            .unwrap()
            .contains("$gamePath = 'C:\\Games\\STS 2'")
    );
    assert_eq!(value["bridge"]["present"], true);
}
#[test]
fn remote_status_allows_running_game() {
    let h = Harness::new();
    h.response(status(
        json!([{"pid":123,"executablePath":"C:\\Games\\STS 2\\SlayTheSpire2.exe"}]),
    ));
    let (code, value) = h.run(&["status"], None);
    assert_eq!(code, 0);
    assert_eq!(value["processes"][0]["pid"], 123);
}
#[test]
fn remote_status_maps_unreachable_and_command_failure() {
    let h = Harness::new();
    for (exit, expected) in [
        ("255", "remote_unreachable"),
        ("7", "remote_command_failed"),
    ] {
        let (code, value) = h.run(&["status"], Some(exit));
        assert_eq!(code, 4);
        assert_eq!(value["error"]["code"], expected);
        assert_eq!(value["error"]["exitCode"], exit.parse::<i32>().unwrap());
        assert!(
            value["error"]["stderrTail"]
                .as_str()
                .unwrap()
                .contains("scripted failure")
        );
    }
}
#[test]
fn remote_status_preserves_remote_errors_and_rejects_invalid_json() {
    let h = Harness::new();
    h.response(json!({"error":{"code":"remote_game_not_found","message":"missing install"}}));
    let (code, value) = h.run(&["status"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_game_not_found");
    h.response(json!({"error":"malformed"}));
    assert_eq!(
        h.run(&["status"], None).1["error"]["code"],
        "remote_invalid_response"
    );
    fs::write(h.dir.path().join("response.json"), "bad json").unwrap();
    assert_eq!(
        h.run(&["status"], None).1["error"]["code"],
        "remote_invalid_response"
    );
}
#[test]
fn remote_host_rejects_options_before_spawning_ssh() {
    let h = Harness::new();
    let (_, value) = h.run(&["status", "--host=--proxy"], None);
    assert_eq!(value["error"]["code"], "remote_host_invalid");
    assert!(!h.dir.path().join("ssh.log").exists());
}

#[test]
fn remote_sync_uses_cache_refreshes_and_repairs_partial_cache() {
    let h = Harness::new();
    h.response(status(json!([])));
    let (code, first) = h.run(&["sync-assemblies"], None);
    assert_eq!(code, 0, "{first}");
    assert_eq!(first["cacheHit"], false);
    let cache = Path::new(first["cacheDir"].as_str().unwrap());
    assert!(cache.ends_with("fake-windows/v0.111.0-42"));
    for name in [
        "sts2.dll",
        "GodotSharp.dll",
        "0Harmony.dll",
        "Steamworks.NET.dll",
    ] {
        assert!(cache.join("data_sts2_windows_x86_64").join(name).is_file());
    }
    assert_eq!(h.run(&["sync-assemblies"], None).1["cacheHit"], true);
    assert_eq!(
        h.run(&["sync-assemblies", "--refresh"], None).1["cacheHit"],
        false
    );
    fs::remove_file(cache.join("data_sts2_windows_x86_64/Steamworks.NET.dll")).unwrap();
    assert_eq!(h.run(&["sync-assemblies"], None).1["cacheHit"], false);
    assert_eq!(
        fs::read_to_string(h.dir.path().join("scp.log"))
            .unwrap()
            .lines()
            .count(),
        3
    );
}
#[test]
fn remote_sync_accepts_backslash_zip_entries_and_caches() {
    let h = Harness::new();
    h.response(status(json!([])));
    fs::write(h.dir.path().join("backslash-entries"), "").unwrap();
    let (code, first) = h.run(&["sync-assemblies"], None);
    assert_eq!(code, 0, "{first}");
    assert_eq!(first["cacheHit"], false);
    let cache = Path::new(first["cacheDir"].as_str().unwrap());
    for name in [
        "sts2.dll",
        "GodotSharp.dll",
        "0Harmony.dll",
        "Steamworks.NET.dll",
    ] {
        assert_eq!(
            fs::read_to_string(cache.join("data_sts2_windows_x86_64").join(name)).unwrap(),
            format!("fake {name}")
        );
    }
    let (code, second) = h.run(&["sync-assemblies"], None);
    assert_eq!(code, 0, "{second}");
    assert_eq!(second["cacheHit"], true);
    assert_eq!(
        fs::read_to_string(h.dir.path().join("scp.log"))
            .unwrap()
            .lines()
            .count(),
        1
    );
}

#[test]
fn remote_sync_falls_back_to_sha256_without_game_hash() {
    let h = Harness::new();
    let mut response = status(json!([]));
    response["releaseInfo"]
        .as_object_mut()
        .unwrap()
        .remove("main_assembly_hash");
    h.response(response);
    let (code, value) = h.run(&["sync-assemblies"], None);
    assert_eq!(code, 0, "{value}");
    assert_eq!(value["mainAssemblySha256"].as_str().unwrap().len(), 64);
    assert!(
        value["cacheDir"]
            .as_str()
            .unwrap()
            .ends_with(value["mainAssemblySha256"].as_str().unwrap())
    );
    assert_eq!(h.run(&["sync-assemblies"], None).1["cacheHit"], true);
}
#[test]
fn remote_sync_rejects_unsafe_stage_and_incomplete_transfers() {
    let h = Harness::new();
    let mut response = status(json!([]));
    response["stageDir"] = json!("C:\\Users\\Some User\\stage");
    h.response(response);
    assert_eq!(
        h.run(&["sync-assemblies"], None).1["error"]["code"],
        "remote_stage_invalid"
    );
    assert!(!h.dir.path().join("scp.log").exists());
    h.response(status(json!([])));
    fs::write(h.dir.path().join("missing-dll"), "").unwrap();
    assert_eq!(h.run(&["sync-assemblies"], None).0, 4);
    assert!(
        !h.dir
            .path()
            .join(".sts2/remote/fake-windows/v0.111.0-42")
            .exists()
    );
    fs::remove_file(h.dir.path().join("missing-dll")).unwrap();
    assert_eq!(h.run(&["sync-assemblies"], None).1["cacheHit"], false);
}
#[test]
fn remote_sync_reports_scp_failure_with_exit_and_stderr() {
    let h = Harness::new();
    h.response(status(json!([])));
    fs::write(h.dir.path().join("scp-fail"), "").unwrap();
    let (code, value) = h.run(&["sync-assemblies"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_transfer_failed");
    assert_eq!(value["error"]["exitCode"], 9);
    assert!(
        value["error"]["stderrTail"]
            .as_str()
            .unwrap()
            .contains("scp failed")
    );
}
#[test]
fn remote_install_refuses_running_game_before_build_or_transfer() {
    let h = Harness::new();
    h.response(status(json!([{"pid":123}])));
    let (code, value) = h.run(&["install-bridge"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_game_running");
    for log in ["dotnet.log", "scp.log"] {
        assert!(!h.dir.path().join(log).exists());
    }
}
#[test]
fn remote_install_builds_transfers_replaces_and_enables_remote_settings() {
    let h = Harness::new();
    h.response(status(json!([{"pid":123}])));
    fs::write(h.dir.path().join("force-expected"), "").unwrap();
    let prepared = json!({"profileRoot":"C:/Users/O'Brien/profile","dependentModIds":["consumer"],"settingsFiles":[
        {"path":"C:/profile/steam/1/settings.save","sha256":"original-one","text":json!({"volume":0.75,"mod_settings":{"mod_list":[{"id":"consumer","is_enabled":true},{"id":"spirectlbridge","is_enabled":false,"custom":8}]}}).to_string()},
        {"path":"C:/profile/steam/2/settings.save","sha256":"original-two","text":json!({"volume":0.25,"mod_settings":{"mod_list":[{"id":"consumer"}]}}).to_string()}
    ]});
    fs::write(h.dir.path().join("prepared.json"), prepared.to_string()).unwrap();
    let (code, value) = h.run(
        &[
            "install-bridge",
            "--force",
            "--endpoint",
            "tcp:127.0.0.1:51234",
            "--profile-root",
            "C:/Users/O'Brien/profile",
        ],
        None,
    );
    assert_eq!(code, 0, "{value}");
    assert_eq!(value["acquisition"]["sts2ApiLane"], "v111");
    assert_eq!(value["builtAgainstGame"]["version"], "v0.111.0");
    assert_eq!(value["modDir"], "C:/Games/STS 2/mods/spirectlbridge");
    assert_eq!(value["tcpAddress"], "127.0.0.1:51234");
    let installed: Value =
        serde_json::from_str(&fs::read_to_string(h.dir.path().join("installed.json")).unwrap())
            .unwrap();
    assert_eq!(installed["endpoint"]["address"], "127.0.0.1:51234");
    assert_eq!(installed["patches"].as_array().unwrap().len(), 2);
    for (index, patch) in installed["patches"].as_array().unwrap().iter().enumerate() {
        let settings: Value = serde_json::from_str(patch["text"].as_str().unwrap()).unwrap();
        assert_eq!(
            settings["mod_settings"]["mod_list"][0]["id"],
            "spirectlbridge"
        );
        assert_eq!(settings["mod_settings"]["mod_list"][0]["is_enabled"], true);
        assert_eq!(settings["volume"], if index == 0 { 0.75 } else { 0.25 });
        assert!(
            value["modLoadOrder"]["settingsFiles"][index]["backupPath"]
                .as_str()
                .unwrap()
                .contains(".spirectl-backup-")
        );
    }
    let calls = fs::read_to_string(h.dir.path().join("ssh.log")).unwrap();
    assert!(calls.contains("O''Brien"));
    let dotnet = fs::read_to_string(h.dir.path().join("dotnet.log")).unwrap();
    assert_eq!(dotnet.lines().count(), 2);
    assert!(dotnet.contains("v0.111.0-42/data_sts2_windows_x86_64"));
    assert!(
        !installed["names"]
            .as_array()
            .unwrap()
            .iter()
            .any(|n| n.as_str().unwrap().ends_with("/sts2.dll"))
    );
    h.response(status(json!([])));
    fs::remove_file(h.dir.path().join("force-expected")).unwrap();
    fs::write(
        h.dir.path().join("prepared.json"),
        json!({"profileRoot":"C:/profile","dependentModIds":[],"settingsFiles":[]}).to_string(),
    )
    .unwrap();
    let (code, again) = h.run(&["install-bridge"], None);
    assert_eq!(code, 0, "{again}");
    assert_eq!(again["cacheHit"], true);
    assert_eq!(again["tcpAddress"], "127.0.0.1:51173");
    assert_eq!(again["modLoadOrder"]["settingsFiles"], json!([]));
    let (code, pipe) = h.run(&["install-bridge", "--endpoint", "pipe:remote-test"], None);
    assert_eq!(code, 0, "{pipe}");
    assert_eq!(pipe["endpoint"]["kind"], "named-pipe");
    assert_eq!(pipe["pipeName"], "remote-test");
    assert!(pipe["tcpAddress"].is_null());
    assert!(pipe["socketPath"].is_null());
    fs::write(h.dir.path().join("operation-fail"), "prepare-install").unwrap();
    let (code, conflict) = h.run(&["install-bridge"], None);
    assert_eq!(code, 4);
    assert_eq!(conflict["error"]["code"], "remote_settings_changed");
    assert_eq!(conflict["error"]["exitCode"], 1);
}

#[test]
fn remote_install_preserves_utf8_settings_and_paths() {
    let h = Harness::new();
    h.response(status(json!([])));
    let path = "C:/Users/José/AppData/Roaming/SlayTheSpire2/steam/1/settings.save";
    let prepared = json!({"profileRoot":"C:/Users/José/AppData/Roaming/SlayTheSpire2","dependentModIds":[],"settingsFiles":[
        {"path":path,"sha256":"original","text":json!({"customLabel":"José","mod_settings":{"mod_list":[]}}).to_string()}
    ]});
    fs::write(h.dir.path().join("prepared.json"), prepared.to_string()).unwrap();
    let (code, value) = h.run(&["install-bridge"], None);
    assert_eq!(code, 0, "{value}");
    let mut archive =
        zip::ZipArchive::new(fs::File::open(h.dir.path().join("uploaded.zip")).unwrap()).unwrap();
    let mut patch_bytes = Vec::new();
    std::io::Read::read_to_end(
        &mut archive.by_name("settings-patches.json").unwrap(),
        &mut patch_bytes,
    )
    .unwrap();
    let patch_text = String::from_utf8(patch_bytes).unwrap();
    assert!(patch_text.contains("José"));
    let patches: Value = serde_json::from_str(&patch_text).unwrap();
    assert_eq!(patches.as_array().unwrap().len(), 1);
    assert_eq!(patches[0]["path"], path);
    let settings: Value = serde_json::from_str(patches[0]["text"].as_str().unwrap()).unwrap();
    assert_eq!(settings["customLabel"], "José");
    assert_eq!(
        settings["mod_settings"]["mod_list"][0]["id"],
        "spirectlbridge"
    );
    assert_eq!(settings["mod_settings"]["mod_list"][0]["is_enabled"], true);
    assert_eq!(value["modLoadOrder"]["settingsFiles"][0]["path"], path);
    assert!(
        value["modLoadOrder"]["settingsFiles"][0]["backupPath"]
            .as_str()
            .unwrap()
            .starts_with(&format!("{path}.spirectl-backup-"))
    );
}

#[test]
fn remote_launch_uses_interactive_task_and_reports_tunnel() {
    let h = Harness::new();
    h.response(status(json!([])));
    let (code, value) = h.run(
        &[
            "launch",
            "--windowed",
            "1280x720",
            "--wait-ms",
            "2000",
            "--",
            "--verbose",
            "O'Brien $x; echo",
            "two words",
        ],
        None,
    );
    assert_eq!(code, 0, "{value}");
    assert_eq!(value["pid"], 456);
    assert_eq!(value["task"]["deleted"], true);
    assert!(
        value["task"]["name"]
            .as_str()
            .unwrap()
            .starts_with("spirectl-launch-")
    );
    assert_eq!(
        value["tunnelCommand"],
        "ssh -N -L 127.0.0.1:51173:127.0.0.1:51173 fake-windows"
    );
    let calls = remote_calls(&h);
    assert_eq!(calls.len(), 4);
    let script = calls[2]["script"].as_str().unwrap();
    assert!(script.contains(r#"$gameArguments = '"--verbose" "O''Brien $x; echo" "two words" "--windowed" "--resolution" "1280x720"'"#));
    assert!(script.contains("$waitMs = 2000"));
    assert!(
        calls[3]["script"]
            .as_str()
            .unwrap()
            .starts_with("# spirectl:launch-cleanup")
    );
    assert!(!h.dir.path().join("scp.log").exists());
}

#[test]
fn remote_launch_seeds_isolated_settings_and_preserves_user_fields() {
    let h = Harness::new();
    h.response(status(json!([])));
    fs::write(h.dir.path().join("launch-prepared.json"), json!({"settingsFiles":[
        {"path":"C:/O'Brien/profile/Roaming/SlayTheSpire2/steam/1/settings.save","text":"{}","sha256":null},
        {"path":"C:/O'Brien/profile/Roaming/SlayTheSpire2/default/1/settings.save","text":json!({"volume":0.75,"largeField":"x".repeat(80000),"mod_settings":{"mods_enabled":false,"mod_list":[{"id":"spirectlbridge","is_enabled":false,"custom":8}]}}).to_string(),"sha256":"original"}
    ]}).to_string()).unwrap();
    let (code, value) = h.run(
        &["launch", "--isolated-profile", "C:/O'Brien/profile"],
        None,
    );
    assert_eq!(code, 0, "{value}");
    let patches: Value = serde_json::from_str(
        &fs::read_to_string(h.dir.path().join("launch-patches.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(patches.as_array().unwrap().len(), 2);
    for patch in patches.as_array().unwrap() {
        let settings: Value = serde_json::from_str(patch["text"].as_str().unwrap()).unwrap();
        assert_eq!(settings["mod_settings"]["mods_enabled"], true);
        assert_eq!(settings["mod_settings"]["mod_list"][0]["is_enabled"], true);
    }
    let existing: Value = serde_json::from_str(patches[1]["text"].as_str().unwrap()).unwrap();
    assert_eq!(existing["volume"], 0.75);
    assert_eq!(existing["largeField"].as_str().unwrap().len(), 80000);
    assert_eq!(existing["mod_settings"]["mod_list"][0]["custom"], 8);
    let calls = remote_calls(&h);
    assert!(
        calls[1]["script"]
            .as_str()
            .unwrap()
            .contains("$isolatedProfile = 'C:/O''Brien/profile'")
    );
    assert!(
        calls[2]["script"]
            .as_str()
            .unwrap()
            .contains("[IO.File]::Replace($temp, $patch.path, ($patch.path + '.spirectl-backup-'")
    );
}

#[test]
fn remote_launch_cleans_up_after_timeout_registration_and_transport_failures() {
    for failure in [
        "remote_launch_timeout",
        "remote_command_failed",
        "remote_game_running",
        "remote_unreachable",
    ] {
        let h = Harness::new();
        h.response(status(json!([])));
        fs::write(h.dir.path().join("launch-error"), failure).unwrap();
        let (code, value) = h.run(&["launch"], None);
        assert_eq!(code, 4);
        assert_eq!(value["error"]["code"], failure);
        assert!(
            remote_calls(&h).last().unwrap()["script"]
                .as_str()
                .unwrap()
                .starts_with("# spirectl:launch-cleanup")
        );
    }
}

#[test]
fn remote_launch_reports_cleanup_failure_and_preserves_primary_failure() {
    let h = Harness::new();
    h.response(status(json!([])));
    fs::write(h.dir.path().join("cleanup-error"), "").unwrap();
    let (code, value) = h.run(&["launch"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["message"], "scripted cleanup failure");
    fs::write(h.dir.path().join("launch-error"), "remote_launch_timeout").unwrap();
    let (code, value) = h.run(&["launch"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_launch_timeout");
    assert_eq!(
        value["error"]["cleanupError"]["message"],
        "scripted cleanup failure"
    );
}

#[test]
fn remote_launch_refuses_running_games_and_invalid_windows_before_changes() {
    let h = Harness::new();
    h.response(status(json!([{"pid":123}])));
    let (code, value) = h.run(&["launch"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_game_running");
    assert_eq!(remote_calls(&h).len(), 1);
    let (code, value) = h.run(&["launch", "--windowed", "0x720"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_window_invalid");
    assert_eq!(remote_calls(&h).len(), 1);
    let mut response = status(json!([]));
    response["stageDir"] = json!("C:/has spaces/stage");
    h.response(response);
    assert_eq!(
        h.run(&["launch"], None).1["error"]["code"],
        "remote_stage_invalid"
    );
    assert_eq!(remote_calls(&h).len(), 2);
}

#[test]
fn remote_launch_named_pipes_have_no_tcp_tunnel() {
    let h = Harness::new();
    let mut response = status(json!([]));
    response["bridge"]["endpoint"] = json!({"kind":"named-pipe","pipeName":"remote"});
    h.response(response);
    let (code, value) = h.run(&["launch"], None);
    assert_eq!(code, 0, "{value}");
    assert!(value["tunnelCommand"].is_null());
}

#[test]
fn remote_stop_handles_graceful_forced_empty_and_timeout_results() {
    let h = Harness::new();
    let mut response = status(json!([{"pid":123},{"pid":456}]));
    response["launchTasks"] = json!(["spirectl-launch-old"]);
    h.response(response);
    let (code, value) = h.run(&["stop", "--timeout-ms", "25"], None);
    assert_eq!(code, 0, "{value}");
    assert_eq!(value["stoppedPids"], json!([123, 456]));
    assert_eq!(value["deletedTasks"], json!(["spirectl-launch-old"]));
    assert_eq!(value["forced"], false);
    assert!(
        remote_calls(&h)[0]["script"]
            .as_str()
            .unwrap()
            .contains("$timeoutMs = 25")
    );
    assert_eq!(h.run(&["stop", "--force"], None).1["forced"], true);
    h.response(status(json!([])));
    assert_eq!(h.run(&["stop"], None).1["stoppedPids"], json!([]));
    fs::write(h.dir.path().join("stop-error"), "").unwrap();
    let (code, value) = h.run(&["stop"], None);
    assert_eq!(code, 4);
    assert_eq!(value["error"]["code"], "remote_stop_timeout");
}

fn remote_calls(h: &Harness) -> Vec<Value> {
    fs::read_to_string(h.dir.path().join("ssh.log"))
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}
