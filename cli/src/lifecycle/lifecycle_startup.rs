#[derive(Default)]
struct StartupLogEvidence {
    any_log: bool,
    engine_header: bool,
    wayland_warning: bool,
    renderer: bool,
    steam_failure: bool,
    mods: bool,
    bridge: bool,
}

impl StartupLogEvidence {
    fn observe(&mut self, line: &str) {
        self.any_log |= !line.trim().is_empty();
        self.engine_header |= line.starts_with("MegaDot v") || line.starts_with("Godot Engine v");
        self.wayland_warning |= line.contains("Can't obtain the XDG decoration manager")
            || line.contains("Can't obtain the XDG activation global");
        self.renderer |= line.starts_with("Vulkan ");
        self.steam_failure |=
            line.contains("NoSteamClient") || line.contains("Steamworks initialization failed!");
        self.mods |= line.contains("Loading assembly DLL ")
            || line.contains("Finished mod initialization for ");
        self.bridge |=
            line.contains("[spirectl] Live ") && line.contains(" bridge host started at ");
    }

    fn stage_and_hint(&self) -> (&'static str, &'static str) {
        if self.steam_failure {
            (
                "steam_init_failed",
                "Steamworks initialization failed; confirm Steam is running and signed in for the owning user session.",
            )
        } else if self.bridge {
            (
                "bridge_started",
                "The bridge reported startup; compare its logged endpoint with the requested endpoint and inspect bridge-health.",
            )
        } else if self.mods {
            (
                "mods_loading",
                "Mod loading began; inspect the captured logs for initialization failures and confirm the bridge mod is enabled.",
            )
        } else if self.renderer {
            (
                "renderer_started",
                "The renderer initialized; inspect the following game logs for Steam or mod initialization failures.",
            )
        } else if self.engine_header && self.wayland_warning {
            (
                "display_init",
                "Startup stopped before renderer initialization; under headless gamescope, set game.launchArgs: [\"--display-driver\",\"x11\"].",
            )
        } else if self.engine_header {
            (
                "engine_started",
                "The engine header was logged but renderer startup was not; inspect captured stderr and display configuration.",
            )
        } else if self.any_log {
            (
                "unknown",
                "Logs contain no recognized startup marker; inspect captured stdout/stderr for the earliest failure.",
            )
        } else {
            (
                "no_log",
                "No new startup log was found; check the executable, wrapper, and captured stdout/stderr paths.",
            )
        }
    }
}

fn add_startup_diagnostics(
    error: &mut Value,
    cursor: Option<&bridge::Sts2LogCursor>,
    output_capture: Option<&LaunchOutputCapture>,
    game_pids: &[u32],
    launch_pid: Option<u32>,
) {
    let mut evidence = StartupLogEvidence::default();
    if let Some(cursor) = cursor {
        bridge::visit_relevant_log_lines_since(cursor, |_, line| evidence.observe(line));
    }
    if let Some(capture) = output_capture {
        for path in [&capture.stdout_path, &capture.stderr_path] {
            if let Ok(contents) = fs::read(path) {
                for line in String::from_utf8_lossy(&contents).lines() {
                    evidence.observe(line);
                }
            }
        }
    }
    if let Some(line) = error["logLine"].as_str() {
        evidence.observe(line);
    }
    let (stage, hint) = evidence.stage_and_hint();
    error["startupStage"] = json!(stage);
    error["startupHint"] = json!(hint);
    error["launchDiagnostics"]["processState"] = startup_process_state(game_pids, launch_pid);
}

fn startup_process_state(game_pids: &[u32], launch_pid: Option<u32>) -> Value {
    #[cfg(target_os = "linux")]
    {
        let pids: BTreeSet<u32> = game_pids.iter().copied().chain(launch_pid).collect();
        json!({
            "supported": true,
            "processes": pids.into_iter().map(|pid| {
                let mut process = startup_proc_state(Path::new("/proc"), pid);
                process["role"] = json!(if game_pids.contains(&pid) { "game" } else { "launch_command" });
                process
            }).collect::<Vec<_>>()
        })
    }
    #[cfg(not(target_os = "linux"))]
    {
        let _ = (game_pids, launch_pid);
        json!({"supported": false, "processes": []})
    }
}

#[cfg(target_os = "linux")]
fn startup_proc_state(proc_root: &Path, pid: u32) -> Value {
    let dir = proc_root.join(pid.to_string());
    let status = fs::read_to_string(dir.join("status")).ok();
    let field = |name: &str| {
        status.as_deref()?.lines().find_map(|line| {
            line.strip_prefix(name)
                .map(|value| value.trim().to_string())
        })
    };
    let mut sampled = None;
    let mut unavailable = 0usize;
    let mut wchans = BTreeMap::<String, usize>::new();
    if let Ok(tasks) = fs::read_dir(dir.join("task")) {
        let mut count = 0usize;
        for task in tasks.flatten() {
            count += 1;
            match fs::read_to_string(task.path().join("wchan")) {
                Ok(wchan) if !wchan.trim().is_empty() && wchan.trim() != "0" => {
                    *wchans.entry(wchan.trim().to_string()).or_default() += 1;
                }
                _ => unavailable += 1,
            }
        }
        sampled = Some(count);
    }
    json!({
        "pid": pid,
        "available": status.is_some() || sampled.is_some(),
        "state": field("State:"),
        "threadCount": field("Threads:").and_then(|value| value.parse::<usize>().ok()),
        "sampledThreadCount": sampled,
        "wchanSummary": sampled.map(|_| wchans),
        "unavailableWchanThreads": sampled.map(|_| unavailable)
    })
}

fn retain_previous_launch_failure(result: &mut Value, failure: &Value) {
    result["launchDiagnostics"]["previousAttempts"] = json!([failure]);
}
