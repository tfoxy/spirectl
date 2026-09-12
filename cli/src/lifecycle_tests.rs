use super::*;
#[cfg(target_os = "linux")]
use std::io;
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
#[cfg(unix)]
use std::os::unix::net::UnixListener;
use std::sync::Mutex;

static ENV_LOCK: Mutex<()> = Mutex::new(());

fn without_agent_host_launch<T>(body: impl FnOnce() -> T) -> T {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let original = std::env::var_os("STS2_AGENT_HOST_LAUNCH");
    // SAFETY: This test helper serializes mutations to the process
    // environment and restores the original value before returning.
    unsafe {
        std::env::remove_var("STS2_AGENT_HOST_LAUNCH");
    }
    let result = body();
    // SAFETY: See the serialized environment mutation note above.
    unsafe {
        if let Some(value) = original {
            std::env::set_var("STS2_AGENT_HOST_LAUNCH", value);
        } else {
            std::env::remove_var("STS2_AGENT_HOST_LAUNCH");
        }
    }
    result
}

fn with_xdg_data_home<T>(data_home: &Path, body: impl FnOnce() -> T) -> T {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let original_xdg = std::env::var_os("XDG_DATA_HOME");
    let original_home = std::env::var_os("HOME");
    // SAFETY: This test helper serializes mutations to the process
    // environment and restores the original values before returning.
    unsafe {
        std::env::set_var("XDG_DATA_HOME", data_home);
        std::env::remove_var("HOME");
    }
    let result = body();
    // SAFETY: See the serialized environment mutation note above.
    unsafe {
        if let Some(value) = original_xdg {
            std::env::set_var("XDG_DATA_HOME", value);
        } else {
            std::env::remove_var("XDG_DATA_HOME");
        }
        if let Some(value) = original_home {
            std::env::set_var("HOME", value);
        } else {
            std::env::remove_var("HOME");
        }
    }
    result
}

fn settings_save_value(entries: &[(&str, bool, &str)]) -> Value {
    json!({
        "mod_settings": {
            "mods_enabled": true,
            "mod_list": entries.iter().map(|(id, enabled, source)| {
                json!({
                    "id": id,
                    "is_enabled": enabled,
                    "source": source
                })
            }).collect::<Vec<_>>()
        }
    })
}

fn write_settings_save(path: &Path, entries: &[(&str, bool, &str)]) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("settings parent");
    }
    fs::write(
        path,
        serde_json::to_string_pretty(&settings_save_value(entries)).expect("settings json"),
    )
    .expect("write settings.save");
}

#[test]
fn launch_process_json_reports_running_process() {
    let payload = launch_process_json(42, None);

    assert_eq!(payload["pid"], 42);
    assert_eq!(payload["alive"], true);
    assert_eq!(payload["exited"], false);
    assert!(payload["exitCode"].is_null());
    assert_eq!(payload["status"], "running");
}

#[test]
fn launch_mod_loadout_plan_distinguishes_absent_empty_and_bridge_allowlists() {
    let absent = resolve_launch_mod_loadout_plan(&AppConfig::default());
    assert!(!absent.configured);
    assert!(!absent.use_nomods_arg);
    assert!(absent.should_attach_bridge);

    let empty = resolve_launch_mod_loadout_plan(&AppConfig {
        game: crate::GameConfig {
            mod_loadout: crate::GameModLoadoutConfig {
                enabled: Some(Vec::new()),
                ..crate::GameModLoadoutConfig::default()
            },
            ..crate::GameConfig::default()
        },
        ..AppConfig::default()
    });
    assert!(empty.configured);
    assert!(empty.use_nomods_arg);
    assert!(!empty.should_attach_bridge);

    let allowlist_without_bridge = resolve_launch_mod_loadout_plan(&AppConfig {
        game: crate::GameConfig {
            mod_loadout: crate::GameModLoadoutConfig {
                enabled: Some(vec!["BaseLib".to_string(), " BaseLib ".to_string()]),
                ..crate::GameModLoadoutConfig::default()
            },
            ..crate::GameConfig::default()
        },
        ..AppConfig::default()
    });
    assert_eq!(allowlist_without_bridge.requested_enabled_ids, ["BaseLib"]);
    assert!(!allowlist_without_bridge.should_attach_bridge);

    let allowlist_with_bridge = resolve_launch_mod_loadout_plan(&AppConfig {
        game: crate::GameConfig {
            mod_loadout: crate::GameModLoadoutConfig {
                enabled: Some(vec![DEPLOYED_MOD_DIR_NAME.to_string()]),
                ..crate::GameModLoadoutConfig::default()
            },
            ..crate::GameConfig::default()
        },
        ..AppConfig::default()
    });
    assert!(allowlist_with_bridge.should_attach_bridge);
}

#[test]
fn rewrite_settings_mod_allowlist_enables_requested_and_disables_discovered_mods() {
    let dir = tempfile::tempdir().expect("temp dir");
    let settings_path = dir.path().join("settings.save");
    let mut settings = settings_save_value(&[
        ("BaseLib", true, "workshop"),
        ("godotexplorer", true, "mods_directory"),
    ]);
    let mut installed_mods = BTreeMap::new();
    installed_mods.insert(
        "spirectlbridge".to_string(),
        SavedModEntry {
            id: "spirectlbridge".to_string(),
            enabled: true,
            source: "mods_directory".to_string(),
        },
    );

    let rewrite = rewrite_settings_mod_allowlist(
        &mut settings,
        &installed_mods,
        &["BaseLib".to_string(), "spirectlbridge".to_string()],
        "game launch",
        &settings_path,
    )
    .expect("rewrite allowlist");

    assert!(rewrite.changed);
    assert_eq!(rewrite.effective_enabled_ids, ["BaseLib", "spirectlbridge"]);
    assert_eq!(rewrite.disabled_ids, ["godotexplorer"]);
    assert_eq!(settings["mod_settings"]["mods_enabled"], true);
    assert_eq!(
        settings["mod_settings"]["mod_list"],
        json!([
            {"id": "BaseLib", "is_enabled": true, "source": "workshop"},
            {"id": "godotexplorer", "is_enabled": false, "source": "mods_directory"},
            {"id": "spirectlbridge", "is_enabled": true, "source": "mods_directory"}
        ])
    );
}

#[test]
fn rewrite_settings_mod_allowlist_rejects_unknown_ids() {
    let dir = tempfile::tempdir().expect("temp dir");
    let settings_path = dir.path().join("settings.save");
    let mut settings = settings_save_value(&[("BaseLib", true, "workshop")]);

    let error = rewrite_settings_mod_allowlist(
        &mut settings,
        &BTreeMap::new(),
        &["missing_mod".to_string()],
        "game launch",
        &settings_path,
    )
    .expect_err("unknown mod id should fail");

    assert_eq!(error.payload["error"]["code"], "unknown_mod_loadout_id");
}

#[test]
fn resolve_mod_settings_file_auto_finds_single_mod_capable_profile_without_hardcoded_id() {
    let data_home = tempfile::tempdir().expect("data home");
    let settings_path = data_home
        .path()
        .join("SlayTheSpire2/steam/123456789/settings.save");
    write_settings_save(&settings_path, &[("BaseLib", true, "workshop")]);
    fs::create_dir_all(data_home.path().join("SlayTheSpire2/steam/987654321"))
        .expect("non-mod profile dir");
    fs::write(
        data_home
            .path()
            .join("SlayTheSpire2/steam/987654321/settings.save"),
        "{}",
    )
    .expect("write non-mod settings");

    let resolved = with_xdg_data_home(data_home.path(), || {
        resolve_mod_settings_file(&AppConfig::default(), None, "game mods settings")
    })
    .expect("resolve settings file");

    assert_eq!(resolved, settings_path);
}

#[test]
fn resolve_mod_settings_file_auto_reports_ambiguity_for_multiple_mod_capable_profiles() {
    let data_home = tempfile::tempdir().expect("data home");
    let first = data_home
        .path()
        .join("SlayTheSpire2/steam/111/settings.save");
    let second = data_home
        .path()
        .join("SlayTheSpire2/steam/222/settings.save");
    write_settings_save(&first, &[("BaseLib", true, "workshop")]);
    write_settings_save(&second, &[("godotexplorer", false, "mods_directory")]);

    let error = with_xdg_data_home(data_home.path(), || {
        resolve_mod_settings_file(&AppConfig::default(), None, "game mods settings")
    })
    .expect_err("multiple mod-capable settings files should fail");

    assert_eq!(error.payload["error"]["code"], "settings_save_ambiguous");
    assert_eq!(
        error.payload["error"]["candidates"],
        json!([first.display().to_string(), second.display().to_string()])
    );
}

#[cfg(unix)]
#[test]
fn launch_process_json_reports_exit_code() {
    let status = Command::new("sh")
        .arg("-c")
        .arg("exit 7")
        .status()
        .expect("exit status");

    let payload = launch_process_json(99, Some(&status));

    assert_eq!(payload["pid"], 99);
    assert_eq!(payload["alive"], false);
    assert_eq!(payload["exited"], true);
    assert_eq!(payload["exitCode"], 7);
}

#[cfg(unix)]
fn write_test_script(dir: &Path, name: &str, contents: &str) -> PathBuf {
    let path = dir.join(name);
    fs::write(&path, contents).expect("write script");
    let mut permissions = fs::metadata(&path).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&path, permissions).expect("chmod");
    path
}

#[cfg(target_os = "linux")]
fn spawn_test_launcher(path: &Path) -> std::process::Child {
    for _ in 0..20 {
        match Command::new(path).arg("30").spawn() {
            Ok(child) => return child,
            Err(error) if error.raw_os_error() == Some(26) => {
                thread::sleep(Duration::from_millis(10));
            }
            Err(error) => panic!("spawn launcher: {error}"),
        }
    }
    Command::new(path)
        .arg("30")
        .spawn()
        .expect("spawn launcher")
}

#[test]
fn resolve_launch_command_keeps_wrapper_separate_from_game_executable() {
    without_agent_host_launch(|| {
        let dir = tempfile::tempdir().expect("temp dir");
        let game_executable = dir.path().join("fake-sts2");
        fs::write(&game_executable, []).expect("write executable");
        let layout = ResolvedLiveBridgeLayout {
            game_path: dir.path().to_path_buf(),
            assemblies_dir: dir.path().join("assemblies"),
            mods_dir: dir.path().join("mods"),
            mod_dir: dir.path().join("mods/spirectlbridge"),
            endpoint: LiveBridgeEndpoint::UnixSocket(
                dir.path()
                    .join("bridge.sock")
                    .to_string_lossy()
                    .into_owned(),
            ),
            used_discovered_game_path: false,
            derived_assemblies_dir_from_game_path: false,
        };
        let config = AppConfig {
            game: crate::GameConfig {
                path: dir.path().to_string_lossy().into_owned(),
                launch_wrapper: vec!["xvfb-run".to_string(), "-a".to_string()],
                launch_executable: Some(game_executable.to_string_lossy().into_owned()),
                launch_args: vec!["--configured".to_string()],
                ..crate::GameConfig::default()
            },
            transport: crate::TransportConfig {
                kind: TransportKind::Ipc,
                ..crate::TransportConfig::default()
            },
            ..AppConfig::default()
        };

        let launch = resolve_launch_command(&config, &layout, &["--extra".to_string()], false)
            .expect("launch command");
        let target = resolve_lifecycle_process_target(&config, &layout, "game kill", None)
            .expect("process target");

        assert_eq!(launch.game_executable, game_executable);
        assert_eq!(
            launch.game_args,
            vec!["--configured".to_string(), "--extra".to_string()]
        );
        assert_eq!(launch.wrapper_command.as_deref(), Some("xvfb-run"));
        assert_eq!(launch.wrapper_args, vec!["-a".to_string()]);
        assert_eq!(launch.spawn_command, "xvfb-run");
        assert_eq!(
            launch.spawn_args,
            [
                "-a".to_string(),
                game_executable.display().to_string(),
                "--configured".to_string(),
                "--extra".to_string()
            ]
        );
        assert_eq!(
            target.launch_executable,
            fs::canonicalize(&game_executable).expect("canonical game executable")
        );
    });
}

#[test]
fn resolve_launch_command_sets_asset_load_guard_env_only_when_enabled() {
    fn launch_env_has_guard(asset_load_guard: bool) -> bool {
        without_agent_host_launch(|| {
            let dir = tempfile::tempdir().expect("temp dir");
            let game_executable = dir.path().join("fake-sts2");
            fs::write(&game_executable, []).expect("write executable");
            let layout = ResolvedLiveBridgeLayout {
                game_path: dir.path().to_path_buf(),
                assemblies_dir: dir.path().join("assemblies"),
                mods_dir: dir.path().join("mods"),
                mod_dir: dir.path().join("mods/spirectlbridge"),
                endpoint: LiveBridgeEndpoint::UnixSocket(
                    dir.path()
                        .join("bridge.sock")
                        .to_string_lossy()
                        .into_owned(),
                ),
                used_discovered_game_path: false,
                derived_assemblies_dir_from_game_path: false,
            };
            let config = AppConfig {
                game: crate::GameConfig {
                    path: dir.path().to_string_lossy().into_owned(),
                    launch_executable: Some(game_executable.to_string_lossy().into_owned()),
                    asset_load_guard,
                    ..crate::GameConfig::default()
                },
                transport: crate::TransportConfig {
                    kind: TransportKind::Ipc,
                    ..crate::TransportConfig::default()
                },
                ..AppConfig::default()
            };

            let launch =
                resolve_launch_command(&config, &layout, &[], false).expect("launch command");
            launch
                .env
                .iter()
                .any(|(key, value)| key == "SPIRECTL_BRIDGE_ASSET_LOAD_GUARD" && value == "1")
        })
    }

    assert!(
        !launch_env_has_guard(false),
        "asset-load guard env var must be absent by default"
    );
    assert!(
        launch_env_has_guard(true),
        "assetLoadGuard: true must set SPIRECTL_BRIDGE_ASSET_LOAD_GUARD=1"
    );
}

#[test]
fn resolve_launch_command_sets_background_throttle_env_from_config_or_flag() {
    fn launch_env_disables_background_throttle(config_key: bool, per_launch_flag: bool) -> bool {
        without_agent_host_launch(|| {
            let dir = tempfile::tempdir().expect("temp dir");
            let game_executable = dir.path().join("fake-sts2");
            fs::write(&game_executable, []).expect("write executable");
            let layout = ResolvedLiveBridgeLayout {
                game_path: dir.path().to_path_buf(),
                assemblies_dir: dir.path().join("assemblies"),
                mods_dir: dir.path().join("mods"),
                mod_dir: dir.path().join("mods/spirectlbridge"),
                endpoint: LiveBridgeEndpoint::UnixSocket(
                    dir.path()
                        .join("bridge.sock")
                        .to_string_lossy()
                        .into_owned(),
                ),
                used_discovered_game_path: false,
                derived_assemblies_dir_from_game_path: false,
            };
            let config = AppConfig {
                game: crate::GameConfig {
                    path: dir.path().to_string_lossy().into_owned(),
                    launch_executable: Some(game_executable.to_string_lossy().into_owned()),
                    disable_background_throttle: config_key,
                    ..crate::GameConfig::default()
                },
                transport: crate::TransportConfig {
                    kind: TransportKind::Ipc,
                    ..crate::TransportConfig::default()
                },
                ..AppConfig::default()
            };

            let launch = resolve_launch_command(&config, &layout, &[], per_launch_flag)
                .expect("launch command");
            launch.env.iter().any(|(key, value)| {
                key == "SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE" && value == "1"
            })
        })
    }

    assert!(
        !launch_env_disables_background_throttle(false, false),
        "background-throttle override must stay off by default"
    );
    assert!(
        launch_env_disables_background_throttle(true, false),
        "disableBackgroundThrottle: true must set SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE=1"
    );
    assert!(
        launch_env_disables_background_throttle(false, true),
        "--disable-background-throttle must set SPIRECTL_BRIDGE_DISABLE_BACKGROUND_THROTTLE=1"
    );
}

#[test]
fn steam_requirement_basis_detects_steamapps_and_steamworks_files() {
    let dir = tempfile::tempdir().expect("temp dir");
    let game_dir = dir
        .path()
        .join("SteamLibrary/steamapps/common/Slay the Spire 2");
    fs::create_dir_all(&game_dir).expect("game dir");
    fs::write(game_dir.join("libsteam_api.so"), []).expect("steam api marker");

    let basis = steam_requirement_basis(&game_dir);

    assert!(basis.contains(&"steamapps-path".to_string()));
    assert!(basis.contains(&"libsteam_api.so".to_string()));
}

#[test]
fn steam_launch_diagnostic_marks_non_steam_paths_not_applicable() {
    without_agent_host_launch(|| {
        let dir = tempfile::tempdir().expect("temp dir");

        let diagnostic = steam_launch_diagnostic(dir.path());

        assert_eq!(diagnostic["status"], "not_applicable");
        assert_eq!(diagnostic["likelyRequiresSteamClient"], false);
    });
}

#[cfg(target_os = "linux")]
#[test]
fn linux_steam_client_status_detects_steam_process() {
    let dir = tempfile::tempdir().expect("temp dir");
    let process_dir = dir.path().join("1234");
    fs::create_dir_all(&process_dir).expect("process dir");
    fs::write(process_dir.join("comm"), "steam\n").expect("comm");

    let status = detect_steam_client_status_with_proc_root(dir.path());

    assert_eq!(
        status,
        SteamClientStatus {
            running: Some(true),
            detection: "linux_proc_process_match".to_string()
        }
    );
}

#[cfg(target_os = "linux")]
#[test]
fn linux_steam_client_status_reports_missing_process() {
    let dir = tempfile::tempdir().expect("temp dir");
    let process_dir = dir.path().join("1234");
    fs::create_dir_all(&process_dir).expect("process dir");
    fs::write(process_dir.join("comm"), "bash\n").expect("comm");

    let status = detect_steam_client_status_with_proc_root(dir.path());

    assert_eq!(
        status,
        SteamClientStatus {
            running: Some(false),
            detection: "linux_proc_no_process_match".to_string()
        }
    );
}

#[cfg(target_os = "linux")]
#[test]
fn restart_stop_force_kills_process_that_ignores_term() {
    let dir = tempfile::tempdir().expect("temp dir");
    let launcher = dir.path().join("fake-sts2");
    let bash = [Path::new("/usr/bin/bash"), Path::new("/bin/bash")]
        .into_iter()
        .find(|path| path.is_file())
        .expect("bash is required for this test");
    fs::copy(bash, &launcher).expect("copy bash");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod");
    wait_until_executable_ready(&launcher).expect("launcher ready");
    let mut child = Command::new(&launcher)
        .arg("-c")
        .arg("trap '' TERM; sleep 30 & wait")
        .spawn()
        .expect("spawn launcher");
    let target = LifecycleProcessTarget {
        launch_executable: fs::canonicalize(&launcher).expect("canonical launcher"),
        instance_user_dir: None,
        ..LifecycleProcessTarget::default()
    };
    (0..20)
        .find(|_| {
            let matches =
                matching_linux_processes(&target, "dev checkpoint resume").expect("processes");
            if matches.iter().any(|process| process.pid == child.id()) {
                true
            } else {
                thread::sleep(Duration::from_millis(25));
                false
            }
        })
        .expect("process match");
    let endpoint = LiveBridgeEndpoint::UnixSocket(
        dir.path()
            .join("missing-bridge.sock")
            .to_string_lossy()
            .into_owned(),
    );

    let payload = execute_restart_stop_for_target(
        &target,
        &endpoint,
        LifecycleWaitArgs {
            timeout_ms: 2_000,
            interval_ms: 25,
            rpc_timeout_ms: 5_000,
        },
        "dev checkpoint resume",
    )
    .expect("restart stop should force-kill term-ignoring process");

    assert_eq!(payload["command"], "dev checkpoint resume");
    assert_eq!(payload["strategy"], "platform-restart");
    assert_eq!(payload["matchedPids"], json!([child.id()]));
    assert_eq!(payload["forcedPids"], json!([child.id()]));
    assert_eq!(payload["remainingPids"], json!([]));
    assert!(payload["success"].as_bool().unwrap());

    let _ = child.wait();
}

#[cfg(target_os = "linux")]
fn wait_until_executable_ready(path: &Path) -> io::Result<()> {
    for _ in 0..20 {
        match Command::new(path).arg("-c").arg(":").status() {
            Ok(_) => return Ok(()),
            Err(error) if error.raw_os_error() == Some(26) => {
                thread::sleep(Duration::from_millis(25));
            }
            Err(error) => return Err(error),
        }
    }

    Command::new(path).arg("-c").arg(":").status().map(|_| ())
}

#[cfg(target_os = "linux")]
#[test]
fn exact_linux_process_detection_matches_launched_executable() {
    let dir = tempfile::tempdir().expect("temp dir");
    let launcher = dir.path().join("fake-sts2");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod");
    let mut child = spawn_test_launcher(&launcher);
    let target = LifecycleProcessTarget {
        launch_executable: fs::canonicalize(&launcher).expect("canonical launcher"),
        instance_user_dir: None,
        ..LifecycleProcessTarget::default()
    };

    let matches = (0..20)
        .find_map(|_| {
            let matches = matching_linux_processes(&target, "game close").expect("processes");
            if matches.iter().any(|process| process.pid == child.id()) {
                Some(matches)
            } else {
                thread::sleep(Duration::from_millis(25));
                None
            }
        })
        .expect("script process match");

    assert!(
        matches
            .iter()
            .any(|process| process.pid == child.id() && process.match_kind == "exe")
    );
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg(target_os = "linux")]
#[test]
fn exact_linux_process_detection_ignores_unrelated_processes() {
    let dir = tempfile::tempdir().expect("temp dir");
    let launcher = write_test_script(
        dir.path(),
        "fake-sts2.sh",
        "#!/usr/bin/env bash\nsleep 30\n",
    );
    let mut child = Command::new("sleep")
        .arg("30")
        .spawn()
        .expect("spawn sleep");
    let target = LifecycleProcessTarget {
        launch_executable: fs::canonicalize(&launcher).expect("canonical launcher"),
        instance_user_dir: None,
        ..LifecycleProcessTarget::default()
    };

    let matches = matching_linux_processes(&target, "game close").expect("processes");

    assert!(!matches.iter().any(|process| process.pid == child.id()));
    let _ = child.kill();
    let _ = child.wait();
}

#[test]
fn bridge_close_not_implemented_maps_to_unsupported_notice() {
    let notice = bridge_close_fallback_notice(
        &bridge::proto::BridgeError {
            code: bridge::proto::BridgeErrorCode::NotImplemented as i32,
            message: "close is not implemented".to_string(),
            details: Vec::new(),
            action_failure: None,
        },
        250,
    );

    assert_eq!(notice["code"], "bridge-close-unsupported");
    assert_eq!(
        notice["message"],
        "The live bridge does not support graceful close; falling back."
    );
    assert_eq!(notice["bridgeErrorCode"], "not_implemented");
    assert_eq!(notice["rpcTimeoutMs"], 250);
}

#[test]
fn bridge_close_rpc_timeout_is_bounded_by_lifecycle_timeout() {
    let started_at = Instant::now();

    let bounded =
        bounded_bridge_close_rpc_timeout_ms(started_at, Duration::from_millis(100), 5_000);

    assert!(bounded <= 100, "bounded timeout was {bounded}");
    assert!(bounded > 0);
}

#[cfg(unix)]
#[test]
fn bridge_wait_without_process_target_observes_endpoint_reachability() {
    let dir = tempfile::tempdir().expect("temp dir");
    let socket_path = dir.path().join("bridge.sock");
    let listener = match UnixListener::bind(&socket_path) {
        Ok(listener) => listener,
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
            eprintln!(
                "skipping socket reachability assertion because this environment blocks Unix socket binding: {error}"
            );
            return;
        }
        Err(error) => panic!("bind socket: {error}"),
    };
    let endpoint = LiveBridgeEndpoint::UnixSocket(socket_path.to_string_lossy().into_owned());

    let error = wait_for_lifecycle_stop(
        None,
        None,
        "game close",
        "bridge",
        &[],
        &[],
        &endpoint,
        Duration::from_millis(20),
        Duration::from_millis(5),
        Instant::now(),
        Vec::new(),
    )
    .expect_err("reachable endpoint should keep bridge close waiting");

    assert_eq!(error.payload["error"]["code"], "close_timeout");
    assert_eq!(
        error.payload["error"]["nextCommand"],
        json!("sts2 game kill")
    );
    assert_eq!(
        error.payload["error"]["nextAction"]["command"],
        json!("game kill")
    );

    drop(listener);
    let wait = wait_for_lifecycle_stop(
        None,
        None,
        "game close",
        "bridge",
        &[],
        &[],
        &endpoint,
        Duration::from_millis(20),
        Duration::from_millis(5),
        Instant::now(),
        Vec::new(),
    )
    .expect("closed endpoint should satisfy bridge close wait");

    assert!(wait.endpoint_disappeared);
}

#[cfg(target_os = "linux")]
#[test]
fn lifecycle_wait_with_process_target_does_not_treat_missing_endpoint_as_stopped() {
    let dir = tempfile::tempdir().expect("temp dir");
    let launcher = dir.path().join("fake-sts2");
    fs::copy("/bin/sleep", &launcher).expect("copy sleep");
    let mut permissions = fs::metadata(&launcher).expect("metadata").permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(&launcher, permissions).expect("chmod");
    let mut child = spawn_test_launcher(&launcher);
    let target = LifecycleProcessTarget {
        launch_executable: fs::canonicalize(&launcher).expect("canonical launcher"),
        instance_user_dir: None,
        ..LifecycleProcessTarget::default()
    };
    let matched = (0..20)
        .find_map(|_| {
            let matches = matching_linux_processes(&target, "game kill").expect("processes");
            if matches.iter().any(|process| process.pid == child.id()) {
                Some(matches)
            } else {
                thread::sleep(Duration::from_millis(25));
                None
            }
        })
        .expect("process match");
    let endpoint = LiveBridgeEndpoint::UnixSocket(
        dir.path()
            .join("missing-bridge.sock")
            .to_string_lossy()
            .into_owned(),
    );

    let result = wait_for_lifecycle_stop(
        Some(&target),
        None,
        "game kill",
        "platform-force",
        &matched,
        &matched,
        &endpoint,
        Duration::from_millis(20),
        Duration::from_millis(5),
        Instant::now(),
        Vec::new(),
    );
    let _ = child.kill();
    let _ = child.wait();
    let error = result.expect_err("live process should keep lifecycle wait pending");

    assert_eq!(error.payload["error"]["code"], "close_timeout");
    assert_eq!(error.payload["error"]["remainingPids"], json!([child.id()]));
    assert!(error.payload["error"]["nextCommand"].is_null());
}

#[cfg(target_os = "linux")]
#[test]
fn platform_graceful_signal_failure_uses_documented_close_error_code() {
    let error =
        send_signal("game close", 999_999_999, "TERM").expect_err("impossible pid should fail");

    assert_eq!(
        error.payload["error"]["code"],
        "configured_stop_command_failed"
    );
}

#[test]
fn derive_linux_launch_executable_accepts_native_slay_the_spire_name() {
    let game_dir = tempfile::tempdir().expect("temp dir");
    let executable = game_dir.path().join("SlayTheSpire2");
    fs::write(&executable, []).expect("write executable");

    let resolved = derive_launch_executable(game_dir.path(), LaunchPlatform::Linux)
        .expect("expected native launcher");
    assert_eq!(resolved, executable);
}

#[test]
fn derive_linux_launch_executable_reports_ambiguous_linux_candidates() {
    let game_dir = tempfile::tempdir().expect("temp dir");
    fs::write(game_dir.path().join("alpha.x86_64"), []).expect("alpha");
    fs::write(game_dir.path().join("beta.x86_64"), []).expect("beta");

    let error = derive_launch_executable(game_dir.path(), LaunchPlatform::Linux)
        .expect_err("expected error");

    assert_eq!(
        error.payload["error"]["code"],
        "launch_executable_ambiguous"
    );
    assert_eq!(error.payload["error"]["configKey"], "game.launchExecutable");
    assert_eq!(
        error.payload["error"]["gamePath"],
        game_dir.path().display().to_string()
    );
    assert_eq!(
        error.payload["error"]["detectedExecutables"],
        serde_json::json!(["alpha.x86_64", "beta.x86_64"])
    );
    assert_eq!(
        error.payload["error"]["searchedCandidates"],
        serde_json::json!([
            "SlayTheSpire2",
            "SlayTheSpire2.x86_64",
            "Slay the Spire 2.x86_64",
            "slaythespire2.x86_64"
        ])
    );
}

#[test]
fn derive_linux_launch_executable_reports_not_found_when_no_linux_candidates_exist() {
    let game_dir = tempfile::tempdir().expect("temp dir");

    let error = derive_launch_executable(game_dir.path(), LaunchPlatform::Linux)
        .expect_err("expected error");

    assert_eq!(
        error.payload["error"]["code"],
        "launch_executable_not_found"
    );
    assert_eq!(error.payload["error"]["configKey"], "game.launchExecutable");
    assert_eq!(
        error.payload["error"]["gamePath"],
        game_dir.path().display().to_string()
    );
    assert_eq!(
        error.payload["error"]["detectedExecutables"],
        serde_json::json!([])
    );
    assert_eq!(
        error.payload["error"]["searchedCandidates"],
        serde_json::json!([
            "SlayTheSpire2",
            "SlayTheSpire2.x86_64",
            "Slay the Spire 2.x86_64",
            "slaythespire2.x86_64"
        ])
    );
}

#[test]
fn derive_windows_launch_executable_accepts_unique_exe_fallback() {
    let game_dir = tempfile::tempdir().expect("temp dir");
    let executable = game_dir.path().join("custom-sts2.exe");
    fs::write(&executable, []).expect("write executable");

    let resolved = derive_launch_executable(game_dir.path(), LaunchPlatform::Windows)
        .expect("expected windows executable");

    assert_eq!(resolved, executable);
}

#[test]
fn derive_macos_launch_executable_accepts_standard_app_bundle() {
    let game_dir = tempfile::tempdir().expect("temp dir");
    let executable = game_dir
        .path()
        .join("Slay the Spire 2.app/Contents/MacOS/Slay the Spire 2");
    fs::create_dir_all(executable.parent().expect("bundle dir")).expect("bundle dirs");
    fs::write(&executable, []).expect("write executable");

    let resolved = derive_launch_executable(game_dir.path(), LaunchPlatform::MacOs)
        .expect("expected macOS executable");

    assert_eq!(resolved, executable);
}

#[test]
fn resolve_mod_source_uses_configured_output_subdir() {
    let dir = tempfile::tempdir().expect("temp dir");
    let output = dir.path().join("dist/MyMod");
    fs::create_dir_all(&output).expect("output dir");

    let config = AppConfig {
        game: crate::GameConfig {
            deploy_output_subdir: Some("dist/MyMod".to_string()),
            ..crate::GameConfig::default()
        },
        ..AppConfig::default()
    };

    let source = resolve_mod_deploy_source(&config, dir.path()).expect("source");
    assert_eq!(source, output);
}

#[test]
fn deployable_mod_name_uses_leaf_directory_name() {
    let name = deployable_mod_name(Path::new("/tmp/build/MyMod")).expect("mod name");
    assert_eq!(name, "MyMod");
}

#[test]
fn extract_user_dir_arg_reads_both_forms() {
    let spaced = vec![
        "--display-driver".to_string(),
        "wayland".to_string(),
        "--user-dir".to_string(),
        "/tmp/inst/user".to_string(),
    ];
    assert_eq!(
        extract_user_dir_arg(&spaced),
        Some(PathBuf::from("/tmp/inst/user"))
    );

    let eq = vec!["--user-dir=/tmp/inst2/user".to_string()];
    assert_eq!(
        extract_user_dir_arg(&eq),
        Some(PathBuf::from("/tmp/inst2/user"))
    );

    assert_eq!(extract_user_dir_arg(&["--headless".to_string()]), None);
}

#[cfg(target_os = "linux")]
#[test]
fn instance_settings_seed_prefers_profile_with_mod_list() {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let original_xdg = std::env::var_os("XDG_DATA_HOME");

    let tmp = std::env::temp_dir().join(format!("spirectl-seed-{}", std::process::id()));
    let _ = fs::remove_dir_all(&tmp);
    let xdg = tmp.join("xdg");
    let shared = xdg.join("SlayTheSpire2");
    fs::create_dir_all(shared.join("default/1")).expect("mkdir default");
    fs::create_dir_all(shared.join("steam/123")).expect("mkdir steam");
    // First-sorted profile has no mod_settings.mod_list; the steam profile does.
    fs::write(shared.join("default/1/settings.save"), "{}").expect("write default");
    fs::write(
        shared.join("steam/123/settings.save"),
        r#"{"mod_settings":{"mod_list":[{"id":"spirectlbridge","is_enabled":true,"source":"mods_directory"}]}}"#,
    )
    .expect("write steam");

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        std::env::set_var("XDG_DATA_HOME", &xdg);
    }

    let instance_user = tmp.join("instance/user");
    let mut config = AppConfig::default();
    config.game.user_dir = Some(instance_user.display().to_string());

    let resolved =
        resolve_mod_settings_file(&config, None, "test").expect("resolves seeded settings");
    assert!(
        resolved.ends_with("steam/123/settings.save"),
        "expected the mod_list-bearing profile, got {resolved:?}"
    );
    // Both shared profiles are seeded into the instance's SlayTheSpire2 subdir
    // (the path the XDG-redirected game actually reads), not the xdg root itself.
    let game_data = instance_user.join("SlayTheSpire2");
    assert!(game_data.join("default/1/settings.save").is_file());
    assert!(game_data.join("steam/123/settings.save").is_file());

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        match original_xdg {
            Some(value) => std::env::set_var("XDG_DATA_HOME", value),
            None => std::env::remove_var("XDG_DATA_HOME"),
        }
    }
    let _ = fs::remove_dir_all(&tmp);
}

// `instances.symlinkUserDataDirs` exists so a big shared mod cache is linked,
// not cloned, into every instance's user-data dir.
#[cfg(target_os = "linux")]
#[test]
fn instance_seed_symlinks_configured_user_data_dirs() {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let original_xdg = std::env::var_os("XDG_DATA_HOME");

    let tmp = std::env::temp_dir().join(format!("spirectl-seedlink-{}", std::process::id()));
    let _ = fs::remove_dir_all(&tmp);
    let xdg = tmp.join("xdg");
    let shared = xdg.join("SlayTheSpire2");
    fs::create_dir_all(shared.join("steam/123")).expect("mkdir steam");
    fs::create_dir_all(shared.join("big-mod/assets")).expect("mkdir big-mod");
    fs::create_dir_all(shared.join("mod_configs")).expect("mkdir mod_configs");
    fs::write(shared.join("steam/123/settings.save"), "{}").expect("write settings");
    fs::write(shared.join("big-mod/assets/blob.bin"), b"cache").expect("write cache");
    fs::write(shared.join("mod_configs/keep.json"), "{}").expect("write mod config");

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        std::env::set_var("XDG_DATA_HOME", &xdg);
    }

    let instance_user = tmp.join("instance/user");
    let mut config = AppConfig::default();
    config.game.user_dir = Some(instance_user.display().to_string());
    config.instances.symlink_user_data_dirs = vec![
        "big-mod".to_string(),
        // Duplicate after trimming; must not be reported twice.
        "  big-mod  ".to_string(),
        // Not present in the shared root yet.
        "fresh-cache".to_string(),
        // Must never address anything outside the user-data root.
        "../escape".to_string(),
    ];

    let report = seed_instance_user_data_if_missing(&config, "test")
        .expect("seeds")
        .expect("seed report");

    let game_data = instance_game_data_dir(&instance_user);
    let linked_path = game_data.join("big-mod");
    assert!(
        fs::symlink_metadata(&linked_path)
            .expect("linked path exists")
            .file_type()
            .is_symlink(),
        "configured dir must be a symlink, not a copy"
    );
    assert_eq!(
        fs::read_link(&linked_path).expect("read link"),
        shared.join("big-mod")
    );
    // A configured dir missing from the shared root is created so the instance
    // links to it instead of growing its own copy.
    assert!(shared.join("fresh-cache").is_dir());
    assert!(
        fs::symlink_metadata(game_data.join("fresh-cache"))
            .expect("fresh link exists")
            .file_type()
            .is_symlink()
    );
    // Everything else is still copied as before.
    assert!(game_data.join("mod_configs/keep.json").is_file());
    assert!(game_data.join("steam/123/settings.save").is_file());

    let links = &report["symlinkedUserDataDirs"];
    assert_eq!(links["linked"], json!(["big-mod", "fresh-cache"]));
    assert_eq!(links["invalid"], json!(["../escape"]));
    assert_eq!(links["notLinkedExistingCopy"], json!([]));

    // The copy pass must not descend through the link and write into the user's
    // real shared dir.
    assert_eq!(
        fs::read_dir(shared.join("big-mod/assets"))
            .expect("read shared cache")
            .count(),
        1
    );

    // Idempotent: a second pass leaves the link in place.
    seed_instance_user_data_if_missing(&config, "test").expect("second pass");
    assert!(
        fs::symlink_metadata(&linked_path)
            .expect("linked path still exists")
            .file_type()
            .is_symlink()
    );

    // `game instances prune` removes the instance dir; that must not follow the
    // link into the shared cache.
    fs::remove_dir_all(tmp.join("instance")).expect("prune instance dir");
    assert!(shared.join("big-mod/assets/blob.bin").is_file());

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        match original_xdg {
            Some(value) => std::env::set_var("XDG_DATA_HOME", value),
            None => std::env::remove_var("XDG_DATA_HOME"),
        }
    }
    let _ = fs::remove_dir_all(&tmp);
}

// Adding a config entry later must still link on an already-seeded instance,
// but an existing real copy is instance data: report it, never delete it.
#[cfg(target_os = "linux")]
#[test]
fn instance_seed_leaves_existing_user_data_copy_and_reports_it() {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let original_xdg = std::env::var_os("XDG_DATA_HOME");

    let tmp = std::env::temp_dir().join(format!("spirectl-seedkeep-{}", std::process::id()));
    let _ = fs::remove_dir_all(&tmp);
    let xdg = tmp.join("xdg");
    let shared = xdg.join("SlayTheSpire2");
    fs::create_dir_all(shared.join("big-mod")).expect("mkdir shared big-mod");
    fs::write(shared.join("big-mod/shared.bin"), b"shared").expect("write shared");

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        std::env::set_var("XDG_DATA_HOME", &xdg);
    }

    let instance_user = tmp.join("instance/user");
    let game_data = instance_game_data_dir(&instance_user);
    // An instance that was already seeded (sentinel present) and already holds a
    // real copy of the configured dir.
    fs::create_dir_all(game_data.join("big-mod")).expect("mkdir instance copy");
    fs::write(game_data.join("big-mod/mine.bin"), b"mine").expect("write instance copy");
    fs::write(game_data.join(".spirectl-seeded"), b"").expect("write sentinel");

    let mut config = AppConfig::default();
    config.game.user_dir = Some(instance_user.display().to_string());
    config.instances.symlink_user_data_dirs = vec!["big-mod".to_string()];

    let report = seed_instance_user_data_if_missing(&config, "test")
        .expect("link pass runs past the sentinel")
        .expect("link report");

    let dest = game_data.join("big-mod");
    assert!(
        !fs::symlink_metadata(&dest)
            .expect("copy still exists")
            .file_type()
            .is_symlink(),
        "an existing real copy must be left alone"
    );
    assert!(
        dest.join("mine.bin").is_file(),
        "instance data is not deleted"
    );
    let links = &report["symlinkedUserDataDirs"];
    assert_eq!(
        links["notLinkedExistingCopy"],
        json!([dest.display().to_string()])
    );
    assert_eq!(links["linked"], json!([]));
    // Past the sentinel this is a link-only report; nothing was copied.
    assert!(report.get("fileCount").is_none());

    // SAFETY: env mutation is serialized by ENV_LOCK.
    unsafe {
        match original_xdg {
            Some(value) => std::env::set_var("XDG_DATA_HOME", value),
            None => std::env::remove_var("XDG_DATA_HOME"),
        }
    }
    let _ = fs::remove_dir_all(&tmp);
}

#[cfg(target_os = "linux")]
#[test]
fn command_line_mentions_path_matches_injected_user_dir() {
    let cmdline = vec![
        "/games/SlayTheSpire2".to_string(),
        "--user-dir".to_string(),
        "/tmp/inst-a/user".to_string(),
    ];
    assert!(command_line_mentions_path(
        &cmdline,
        Path::new("/tmp/inst-a/user")
    ));
    assert!(!command_line_mentions_path(
        &cmdline,
        Path::new("/tmp/inst-b/user")
    ));
}

// The isolation guard keys off `--user-dir` presence: a process carrying one
// (this instance or another) is isolated; one without it runs on the shared
// default user-dir.
#[test]
fn extract_user_dir_arg_detects_presence_and_form() {
    assert_eq!(
        extract_user_dir_arg(&[
            "/games/SlayTheSpire2".to_string(),
            "--user-dir".to_string(),
            "/tmp/inst-a/user".to_string(),
        ]),
        Some(PathBuf::from("/tmp/inst-a/user"))
    );
    assert_eq!(
        extract_user_dir_arg(&[
            "/games/SlayTheSpire2".to_string(),
            "--user-dir=/tmp/inst-b/user".to_string(),
        ]),
        Some(PathBuf::from("/tmp/inst-b/user"))
    );
    // A default-user-dir game carries no --user-dir at all.
    assert_eq!(
        extract_user_dir_arg(&[
            "/games/SlayTheSpire2".to_string(),
            "--display-driver".to_string(),
            "wayland".to_string(),
        ]),
        None
    );
}

#[test]
fn instance_game_data_dir_appends_custom_user_dir_name() {
    assert_eq!(
        instance_game_data_dir(Path::new("/tmp/inst-a/user")),
        PathBuf::from("/tmp/inst-a/user").join("SlayTheSpire2")
    );
}

// STS2 ignores --user-dir, so isolation rides on XDG_DATA_HOME. The mirror must
// expose the user's other ~/.local/share data via symlinks while keeping the
// game's own SlayTheSpire2 dir real/per-instance.
#[cfg(target_os = "linux")]
#[test]
fn ensure_xdg_data_mirror_symlinks_siblings_but_not_the_game_dir() {
    let _guard = ENV_LOCK.lock().expect("env lock");
    let tmp = std::env::temp_dir().join(format!("sts2-xdg-test-{}", std::process::id()));
    let fake_home = tmp.join("home");
    let real_share = fake_home.join(".local/share");
    fs::create_dir_all(real_share.join("vulkan")).unwrap();
    fs::create_dir_all(real_share.join("fonts")).unwrap();
    fs::create_dir_all(real_share.join("SlayTheSpire2/steam")).unwrap();
    let xdg_root = tmp.join("xdg");

    let original_home = std::env::var_os("HOME");
    // SAFETY: serialized by ENV_LOCK; restored before returning.
    unsafe { std::env::set_var("HOME", &fake_home) };
    ensure_xdg_data_mirror(&xdg_root);
    // SAFETY: see above.
    unsafe {
        match original_home {
            Some(value) => std::env::set_var("HOME", value),
            None => std::env::remove_var("HOME"),
        }
    }

    assert!(
        fs::symlink_metadata(xdg_root.join("vulkan"))
            .unwrap()
            .file_type()
            .is_symlink(),
        "sibling vulkan should be symlinked into the instance xdg root"
    );
    assert!(fs::symlink_metadata(xdg_root.join("fonts")).is_ok());
    assert!(
        fs::symlink_metadata(xdg_root.join("SlayTheSpire2")).is_err(),
        "the game's own data dir must stay real/per-instance, never symlinked"
    );
    let _ = fs::remove_dir_all(&tmp);
}

#[test]
fn launch_output_dir_is_per_instance_when_an_instance_is_active() {
    let tmp = std::env::temp_dir().join(format!("spirectl-stdio-dir-{}", std::process::id()));
    let mut config = AppConfig::default();
    config.artifacts.dir = tmp.join("artifacts").display().to_string();

    assert_eq!(
        launch_output_dir(&config, None),
        tmp.join("artifacts/game-launch")
    );

    let instance = crate::instance::InstanceContext {
        name: "alpha".to_string(),
        isolated: false,
        socket: "/tmp/alpha.sock".to_string(),
        user_dir: tmp.join("instances/alpha/user"),
        instance_dir: tmp.join("instances/alpha"),
        registry_path: tmp.join("instances/alpha/instance.json"),
        game_root: tmp.join("instances/alpha/game"),
        mods_dir: tmp.join("instances/alpha/game/mods"),
        launch_executable: tmp.join("instances/alpha/game/exe"),
    };
    assert_eq!(
        launch_output_dir(&config, Some(&instance)),
        tmp.join("instances/alpha/logs"),
        "parallel instances must not overwrite each other's game logs"
    );
}

#[test]
fn prune_launch_output_logs_keeps_the_newest_runs_per_stream() {
    let dir = std::env::temp_dir().join(format!("spirectl-stdio-prune-{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).expect("log dir");

    for index in 0..12 {
        for suffix in ["stdout", "stderr"] {
            fs::write(
                dir.join(format!(
                    "game-launch-{:013}-1.{suffix}.log",
                    1_700_000_000_000u64 + index
                )),
                "line\n",
            )
            .expect("write log");
        }
    }
    fs::write(dir.join("game.stdout.log"), "latest\n").expect("write latest");
    fs::write(dir.join("unrelated.log"), "keep\n").expect("write unrelated");

    prune_launch_output_logs(&dir, 10);

    let mut remaining = fs::read_dir(&dir)
        .expect("read log dir")
        .filter_map(Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .collect::<Vec<_>>();
    remaining.sort();

    let runs = remaining
        .iter()
        .filter(|name| name.starts_with("game-launch-") && name.ends_with(".stdout.log"))
        .count();
    assert_eq!(runs, 10, "only the newest ten runs per stream are kept");
    assert!(remaining.contains(&"game-launch-1700000000011-1.stdout.log".to_string()));
    assert!(!remaining.contains(&"game-launch-1700000000000-1.stdout.log".to_string()));
    assert!(remaining.contains(&"game.stdout.log".to_string()));
    assert!(remaining.contains(&"unrelated.log".to_string()));

    let _ = fs::remove_dir_all(&dir);
}

fn release_bridge_zip(version: &str, extra: Option<(&str, &[u8])>) -> Vec<u8> {
    release_bridge_zip_for_lane(version, "v107", extra)
}

fn release_bridge_zip_for_lane(version: &str, lane: &str, extra: Option<(&str, &[u8])>) -> Vec<u8> {
    let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default();
    let bridge_manifest = serde_json::to_vec(&json!({
        "id": DEPLOYED_MOD_DIR_NAME,
        "version": version,
        "has_pck": false,
        "has_dll": true,
        "buildIdentity": { "sts2ApiLane": lane }
    }))
    .expect("bridge manifest");
    for (name, bytes) in [
        (
            "spirectlbridge/spirectlbridge.json",
            bridge_manifest.as_slice(),
        ),
        ("spirectlbridge/spirectlbridge.dll", b"loader".as_slice()),
        (
            "spirectlbridge/Spirectl.BridgeMod.Sts2Host.dll",
            b"host".as_slice(),
        ),
        (
            "spirectlbridge/Spirectl.BridgeMod.dll",
            b"library".as_slice(),
        ),
        ("spirectlbridge/Spirectl.Sts2.dll", b"runtime".as_slice()),
    ] {
        writer.start_file(name, options).expect("zip entry");
        writer.write_all(bytes).expect("zip bytes");
    }
    if let Some((name, bytes)) = extra {
        writer.start_file(name, options).expect("extra zip entry");
        writer.write_all(bytes).expect("extra zip bytes");
    }
    writer.finish().expect("finish zip").into_inner()
}

#[test]
fn release_bridge_archive_requires_matching_digest_and_extracts_only_the_rooted_payload() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    let archive = release_bridge_zip(version, None);
    let archive_path = temp.path().join("spirectlbridge-v9.8.7.zip");
    fs::write(&archive_path, &archive).expect("archive");
    let manifest = BridgeReleaseManifest {
        schema_version: RELEASE_MANIFEST_SCHEMA.to_string(),
        version: version.to_string(),
        archive: BridgeReleaseArchive {
            name: "spirectlbridge-v9.8.7.zip".to_string(),
            sha256: sha256_hex(&archive),
        },
    };
    let verified =
        verify_release_archive("test", &archive_path, &manifest).expect("verified archive");
    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage");
    extract_release_bridge("test", &verified, &stage_dir, version).expect("extract release bridge");

    assert!(stage_dir.join("spirectlbridge.dll").is_file());
    assert!(stage_dir.join("Spirectl.BridgeMod.Sts2Host.dll").is_file());
    assert!(!stage_dir.join(DEPLOYED_MOD_DIR_NAME).exists());
}

/// Seed `<artifacts>/release-cache/<semver>/<lane>/` with a payload for one
/// lane, the way a download or a `package-bridge-release.sh --lane` run would.
fn seed_release_cache_lane(artifacts_root: &Path, version: &str, lane: &str) {
    let cache_dir = artifacts_root
        .join("release-cache")
        .join(version)
        .join(lane);
    fs::create_dir_all(&cache_dir).expect("cache dir");
    let archive_name = format!("spirectlbridge-v{version}-{lane}.zip");
    let archive = release_bridge_zip_for_lane(version, lane, None);
    fs::write(cache_dir.join(&archive_name), &archive).expect("cache archive");
    fs::write(
        cache_dir.join(format!("spirectlbridge-v{version}-{lane}.manifest.json")),
        serde_json::to_vec(&json!({
            "schemaVersion": RELEASE_MANIFEST_SCHEMA,
            "version": version,
            "archive": { "name": archive_name, "sha256": sha256_hex(&archive) }
        }))
        .expect("release manifest"),
    )
    .expect("cache manifest");
}

fn release_info_for(version: &str, hash: i64) -> crate::game_build::GameReleaseInfo {
    crate::game_build::GameReleaseInfo {
        version: version.to_string(),
        commit: "deadbeef".to_string(),
        main_assembly_hash: Some(hash),
    }
}

#[test]
fn release_bridge_stages_a_verified_cached_archive_without_downloading() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    seed_release_cache_lane(temp.path(), version, "v107");

    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage dir");
    let release_info = release_info_for("v0.107.1", 1_692_500_715);
    let acquisition = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        Some("v107"),
        Some(&release_info),
        &temp.path().join("release_info.json"),
        false,
    )
    .expect("cached release stages");

    assert_eq!(acquisition["kind"], "release-cache");
    assert_eq!(acquisition["sts2ApiLane"], "v107");
    let staged = serde_json::from_str::<Value>(
        &fs::read_to_string(stage_dir.join(BRIDGE_MANIFEST_NAME)).expect("staged manifest"),
    )
    .expect("staged manifest JSON");
    assert_eq!(staged["version"], version);
    assert_eq!(staged["buildIdentity"]["sts2ApiLane"], "v107");
    // A released payload compiles against the declaration-only reference SDK,
    // so it must claim a lane and NOTHING about the game build itself.
    assert_eq!(
        staged["buildIdentity"]["builtAgainstGame"]["identitySource"],
        "reference-sdk"
    );
    assert_eq!(
        staged["buildIdentity"]["builtAgainstGame"]["version"],
        Value::Null
    );
    assert_eq!(
        staged["buildIdentity"]["builtAgainstGame"]["mainAssemblyHash"],
        Value::Null
    );
    assert_eq!(
        staged["buildIdentity"]["builtAgainstGame"]["referencePackageVersion"],
        version
    );
}

/// The cache is keyed on `<semver>/<lane>`, so a payload cached for one game
/// build is invisible to an install that needs another. Installing it anyway
/// would produce a bridge that loads and then throws on the first lobby walk.
#[test]
fn release_bridge_refuses_a_payload_cached_for_another_game_build() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    let wanted_lane = crate::game_build::releasable_api_lanes()[0];
    // A payload for a lane this install does not need is cached; the lane this
    // install DOES need is not.
    seed_release_cache_lane(temp.path(), version, "v_cached_only");

    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage dir");
    let release_info = release_info_for("v0.107.1", 1_692_500_715);
    let error = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        Some(wanted_lane),
        Some(&release_info),
        &temp.path().join("release_info.json"),
        false,
    )
    .expect_err("another lane's cached payload must not satisfy this install");

    assert_eq!(error.exit_code, 2);
    assert_eq!(
        error.payload["error"]["code"],
        "bridge_release_cache_missing"
    );
    let message = error.payload["error"]["message"]
        .as_str()
        .expect("message")
        .to_string();
    assert!(message.contains(&format!("'{wanted_lane}'")), "{message}");
    assert!(
        message.contains(&format!("spirectlbridge-v{version}-{wanted_lane}.zip")),
        "{message}"
    );

    // Seed the wanted lane too and the same call stages, from the same cache.
    seed_release_cache_lane(temp.path(), version, wanted_lane);
    let control_stage = temp.path().join("control-stage");
    fs::create_dir_all(&control_stage).expect("control stage dir");
    let acquisition = stage_release_bridge(
        "test",
        temp.path(),
        &control_stage,
        version,
        Some(wanted_lane),
        Some(&release_info),
        &temp.path().join("release_info.json"),
        false,
    )
    .expect("the matching lane stages");
    assert_eq!(acquisition["sts2ApiLane"], wanted_lane);
}

/// A supported lane is not automatically a released one. Released payloads are
/// built against the locked, declaration-only reference SDK, which pins one game
/// build's declarations — so `--no-build` must say that rather than go looking for
/// an asset that was never published.
#[test]
fn release_bridge_refuses_a_lane_no_release_covers() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    let Some(unreleasable) = crate::game_build::api_lanes()
        .into_iter()
        .find(|lane| !crate::game_build::is_releasable_api_lane(lane))
    else {
        // Every supported lane is releasable; nothing to assert.
        return;
    };
    // Even WITH a payload cached under its name, the lane is refused: no released
    // artifact for it exists, so whatever is there is not one.
    seed_release_cache_lane(temp.path(), version, unreleasable);
    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage dir");
    let release_info = release_info_for("v0.111.0", 1_579_942_752);

    let error = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        Some(unreleasable),
        Some(&release_info),
        &temp.path().join("release_info.json"),
        true,
    )
    .expect_err("a lane no release covers must refuse");

    assert_eq!(
        error.payload["error"]["code"],
        "bridge_release_lane_unreleasable"
    );
    let message = error.payload["error"]["message"]
        .as_str()
        .expect("message")
        .to_string();
    assert!(message.contains(&format!("'{unreleasable}'")), "{message}");
    assert!(message.contains("install-bridge"), "{message}");
}

/// Without a resolvable lane there is nothing to match, and a best-effort
/// install is exactly what this work item exists to prevent.
#[test]
fn release_bridge_refuses_when_the_install_build_cannot_be_identified() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    seed_release_cache_lane(temp.path(), version, "v107");
    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage dir");
    let release_info_path = temp.path().join("release_info.json");

    let unreadable = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        None,
        None,
        &release_info_path,
        false,
    )
    .expect_err("an unidentifiable install must refuse");
    assert_eq!(
        unreadable.payload["error"]["code"],
        "bridge_release_lane_unresolved"
    );
    let message = unreadable.payload["error"]["message"]
        .as_str()
        .expect("message")
        .to_string();
    assert!(
        message.contains(&release_info_path.display().to_string()),
        "{message}"
    );
    assert!(message.contains("v107"), "{message}");

    let unmapped_info = release_info_for("v0.999.0", 1);
    let unmapped = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        None,
        Some(&unmapped_info),
        &release_info_path,
        false,
    )
    .expect_err("an unmapped game build must refuse");
    assert_eq!(
        unmapped.payload["error"]["code"],
        "bridge_release_lane_unresolved"
    );
    assert!(
        unmapped.payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("v0.999.0")
    );
}

/// The archive name is not a claim; the manifest inside it is. A payload whose
/// own manifest names another lane is rejected rather than installed.
#[test]
fn release_bridge_rejects_an_archive_whose_manifest_claims_another_lane() {
    let temp = tempfile::tempdir().expect("temp dir");
    let version = "9.8.7";
    let lane = crate::game_build::releasable_api_lanes()[0];
    let cache_dir = temp.path().join("release-cache").join(version).join(lane);
    fs::create_dir_all(&cache_dir).expect("cache dir");
    let archive_name = format!("spirectlbridge-v{version}-{lane}.zip");
    // Named for the lane this install needs, built for another one.
    let archive = release_bridge_zip_for_lane(version, "v_some_other_lane", None);
    fs::write(cache_dir.join(&archive_name), &archive).expect("cache archive");
    fs::write(
        cache_dir.join(format!("spirectlbridge-v{version}-{lane}.manifest.json")),
        serde_json::to_vec(&json!({
            "schemaVersion": RELEASE_MANIFEST_SCHEMA,
            "version": version,
            "archive": { "name": archive_name, "sha256": sha256_hex(&archive) }
        }))
        .expect("release manifest"),
    )
    .expect("cache manifest");

    let stage_dir = temp.path().join("stage");
    fs::create_dir_all(&stage_dir).expect("stage dir");
    let release_info = release_info_for("v0.107.1", 1_692_500_715);
    let error = stage_release_bridge(
        "test",
        temp.path(),
        &stage_dir,
        version,
        Some(lane),
        Some(&release_info),
        &temp.path().join("release_info.json"),
        false,
    )
    .expect_err("a mis-named archive must be rejected");

    assert_eq!(
        error.payload["error"]["code"],
        "bridge_release_lane_mismatch"
    );
    assert!(
        error.payload["error"]["message"]
            .as_str()
            .expect("message")
            .contains("'v_some_other_lane'")
    );
}

#[test]
fn release_bridge_archive_rejects_checksum_mismatch_and_unsafe_or_forbidden_entries() {
    let temp = tempfile::tempdir().expect("temp dir");
    let archive = release_bridge_zip("1.0.0", None);
    let archive_path = temp.path().join("bridge.zip");
    fs::write(&archive_path, &archive).expect("archive");
    let manifest = BridgeReleaseManifest {
        schema_version: RELEASE_MANIFEST_SCHEMA.to_string(),
        version: "1.0.0".to_string(),
        archive: BridgeReleaseArchive {
            name: "bridge.zip".to_string(),
            sha256: "0".repeat(64),
        },
    };
    let checksum_error =
        verify_release_archive("test", &archive_path, &manifest).expect_err("bad digest");
    assert_eq!(
        checksum_error.payload["error"]["code"],
        "bridge_release_checksum_invalid"
    );

    let unsafe_archive =
        release_bridge_zip("1.0.0", Some(("spirectlbridge/../../outside", b"nope")));
    let unsafe_error = extract_release_bridge(
        "test",
        &unsafe_archive,
        &temp.path().join("unsafe-stage"),
        "1.0.0",
    )
    .expect_err("unsafe archive rejected");
    assert_eq!(
        unsafe_error.payload["error"]["code"],
        "bridge_release_archive_invalid"
    );

    let forbidden_archive = release_bridge_zip(
        "1.0.0",
        Some(("spirectlbridge/GodotSharp.dll", b"reference")),
    );
    let forbidden_error = extract_release_bridge(
        "test",
        &forbidden_archive,
        &temp.path().join("forbidden-stage"),
        "1.0.0",
    )
    .expect_err("forbidden assembly rejected");
    assert_eq!(
        forbidden_error.payload["error"]["code"],
        "bridge_release_archive_invalid"
    );

    let stale_archive = release_bridge_zip("0.9.0", None);
    let version_error = extract_release_bridge(
        "test",
        &stale_archive,
        &temp.path().join("stale-stage"),
        "1.0.0",
    )
    .expect_err("stale archive rejected");
    assert_eq!(
        version_error.payload["error"]["code"],
        "bridge_release_version_mismatch"
    );
}
