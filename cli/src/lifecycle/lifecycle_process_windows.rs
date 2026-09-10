// Windows process discovery and termination for the lifecycle stop paths.
//
// The Linux implementation reads `/proc` directly. Windows has no equivalent
// filesystem view, so the host process table is read through one of two
// external sources:
//
// 1. `Get-CimInstance Win32_Process` — the full picture (`ExecutablePath` and
//    `CommandLine`), which is what lets wrapper matching and `--user-dir`
//    instance scoping behave the same as on Linux.
// 2. `tasklist` — the fallback when PowerShell is unavailable or blocked by
//    policy. It only yields image names, so matches made from it are reported
//    with a `match_kind` of `image-name` and a degraded-match notice.
//
// Everything except the two `Command` invocations is compiled on every
// platform so the parsing and matching rules are covered by the normal test
// run rather than only on a Windows host.

pub(crate) mod windows_process {
    #![allow(
        dead_code,
        reason = "The parse/match layer is compiled on every platform so it stays unit-tested off Windows; only the Command callers are cfg-gated."
    )]

    use super::*;

    /// One row of the host process table.
    #[derive(Debug, Clone, PartialEq, Eq, Default)]
    pub(super) struct WindowsProcessEntry {
        pid: u32,
        /// Full image path. `None` from `tasklist`, and from CIM for processes this
        /// user may not query.
        executable: Option<PathBuf>,
        /// Image file name (`SlayTheSpire2.exe`), always available.
        image_name: Option<String>,
        /// Parsed command line. Empty when the source could not provide one.
        command_line: Vec<String>,
    }

    /// A process-table read, plus where it came from.
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub(super) struct WindowsProcessSnapshot {
        entries: Vec<WindowsProcessEntry>,
        /// `"cim"` or `"tasklist"`.
        source: &'static str,
    }

    impl WindowsProcessSnapshot {
        fn degraded(&self) -> bool {
            self.source != "cim"
        }
    }

    // ---------------------------------------------------------------------------
    // Parsing (platform-independent so it stays testable)
    // ---------------------------------------------------------------------------

    /// Parse `Get-CimInstance Win32_Process | ConvertTo-Json`.
    ///
    /// `ConvertTo-Json` emits a bare object rather than a one-element array when a
    /// single process matches, so both shapes are accepted.
    pub(super) fn parse_cim_process_json(raw: &str) -> Option<Vec<WindowsProcessEntry>> {
        let value: Value = serde_json::from_str(raw.trim()).ok()?;
        let rows = match value {
            Value::Array(rows) => rows,
            row @ Value::Object(_) => vec![row],
            _ => return None,
        };

        Some(
            rows.iter()
                .filter_map(|row| {
                    let pid = row.get("ProcessId").and_then(Value::as_u64)? as u32;
                    let executable = row
                        .get("ExecutablePath")
                        .and_then(Value::as_str)
                        .filter(|path| !path.trim().is_empty())
                        .map(PathBuf::from);
                    let command_line = row
                        .get("CommandLine")
                        .and_then(Value::as_str)
                        .map(split_windows_command_line)
                        .unwrap_or_default();
                    let image_name = row
                        .get("Name")
                        .and_then(Value::as_str)
                        .map(str::to_string)
                        .or_else(|| executable.as_deref().and_then(windows_file_name));
                    Some(WindowsProcessEntry {
                        pid,
                        executable,
                        image_name,
                        command_line,
                    })
                })
                .collect(),
        )
    }

    /// Parse `tasklist /FO CSV /NH` output: `"Image Name","PID",...`.
    pub(super) fn parse_tasklist_csv(raw: &str) -> Vec<WindowsProcessEntry> {
        raw.lines()
            .filter_map(|line| {
                let fields = parse_csv_line(line);
                let image_name = fields.first()?.trim().to_string();
                let pid = fields.get(1)?.trim().parse::<u32>().ok()?;
                (!image_name.is_empty()).then_some(WindowsProcessEntry {
                    pid,
                    executable: None,
                    image_name: Some(image_name),
                    command_line: Vec::new(),
                })
            })
            .collect()
    }

    /// Split a quoted CSV row. `tasklist` quotes every field and does not emit
    /// embedded quotes, so doubled-quote unescaping is all that is needed.
    fn parse_csv_line(line: &str) -> Vec<String> {
        let mut fields = Vec::new();
        let mut current = String::new();
        let mut in_quotes = false;
        let mut chars = line.chars().peekable();

        while let Some(character) = chars.next() {
            match character {
                '"' if in_quotes && chars.peek() == Some(&'"') => {
                    current.push('"');
                    chars.next();
                }
                '"' => in_quotes = !in_quotes,
                ',' if !in_quotes => fields.push(std::mem::take(&mut current)),
                _ => current.push(character),
            }
        }
        fields.push(current);
        fields
    }

    /// Split a Windows command-line string into arguments using the CRT rules
    /// (`2n` backslashes before a quote collapse to `n` and toggle quoting;
    /// `2n+1` collapse to `n` and emit a literal quote).
    pub(super) fn split_windows_command_line(raw: &str) -> Vec<String> {
        let mut args = Vec::new();
        let mut current = String::new();
        let mut started = false;
        let mut in_quotes = false;
        let mut backslashes = 0usize;

        for character in raw.chars() {
            match character {
                '\\' => {
                    backslashes += 1;
                    started = true;
                }
                '"' => {
                    current.extend(std::iter::repeat_n('\\', backslashes / 2));
                    if backslashes % 2 == 1 {
                        current.push('"');
                    } else {
                        in_quotes = !in_quotes;
                    }
                    backslashes = 0;
                    started = true;
                }
                character if character.is_whitespace() && !in_quotes => {
                    current.extend(std::iter::repeat_n('\\', backslashes));
                    backslashes = 0;
                    if started {
                        args.push(std::mem::take(&mut current));
                        started = false;
                    }
                }
                character => {
                    current.extend(std::iter::repeat_n('\\', backslashes));
                    backslashes = 0;
                    current.push(character);
                    started = true;
                }
            }
        }

        current.extend(std::iter::repeat_n('\\', backslashes));
        if started {
            args.push(current);
        }
        args
    }

    // ---------------------------------------------------------------------------
    // Path comparison
    // ---------------------------------------------------------------------------

    /// Compare two Windows paths: case-insensitive, separator-insensitive, and
    /// blind to the `\\?\` verbatim prefix `fs::canonicalize` adds (CIM never
    /// reports one, so a raw comparison would never match).
    pub(super) fn windows_paths_equal(left: &Path, right: &Path) -> bool {
        normalize_windows_path(left) == normalize_windows_path(right)
    }

    fn normalize_windows_path(path: &Path) -> String {
        let text = path.to_string_lossy().replace('/', "\\");
        let text = text
            .strip_prefix(r"\\?\UNC\")
            .map(|rest| format!(r"\\{rest}"))
            .unwrap_or_else(|| text.strip_prefix(r"\\?\").unwrap_or(&text).to_string());
        text.trim_end_matches('\\').to_lowercase()
    }

    /// Lowercased final component of a Windows path.
    ///
    /// Derived from the normalized string rather than `Path::file_name`, which
    /// only treats `\\` as a separator when the CLI itself runs on Windows —
    /// the matching rules have to read the same on every host.
    fn windows_file_name(path: &Path) -> Option<String> {
        normalize_windows_path(path)
            .rsplit('\\')
            .next()
            .filter(|name| !name.is_empty())
            .map(str::to_string)
    }

    fn command_line_mentions_windows_path(command_line: &[String], path: &Path) -> bool {
        command_line
            .iter()
            .any(|arg| windows_paths_equal(Path::new(arg), path))
    }

    // ---------------------------------------------------------------------------
    // Matching (platform-independent so it stays testable)
    // ---------------------------------------------------------------------------

    /// True when no instance scope is requested, or the command line carries the
    /// instance's `--user-dir`. A row with no command line can never prove it
    /// belongs to the instance, so it is excluded — same rule as Linux.
    fn windows_process_matches_instance_scope(
        entry: &WindowsProcessEntry,
        instance_user_dir: Option<&Path>,
    ) -> bool {
        let Some(user_dir) = instance_user_dir else {
            return true;
        };
        command_line_mentions_windows_path(&entry.command_line, user_dir)
    }

    /// Does a recorded pid still look like this instance's game? Mirrors the Linux
    /// `recorded_pid_looks_like_game` rule: an executable must match the resolved
    /// or recorded launch path (by full path or file name), and when no executable
    /// is readable the command line is consulted instead.
    fn recorded_windows_pid_looks_like_game(
        target: &LifecycleProcessTarget,
        entry: &WindowsProcessEntry,
    ) -> bool {
        if target.instance_user_dir.is_some() {
            return true;
        }
        let candidates = [
            Some(&target.launch_executable),
            target.recorded_launch_executable.as_ref(),
        ];

        if let Some(executable) = entry.executable.as_deref() {
            return candidates.into_iter().flatten().any(|candidate| {
                windows_paths_equal(candidate, executable)
                    || (windows_file_name(candidate).is_some()
                        && windows_file_name(candidate) == windows_file_name(executable))
            });
        }

        if let Some(image_name) = entry.image_name.as_deref()
            && candidates.into_iter().flatten().any(|candidate| {
                windows_file_name(candidate).as_deref() == Some(&image_name.to_lowercase())
            })
        {
            return true;
        }

        candidates
            .into_iter()
            .flatten()
            .any(|candidate| command_line_mentions_windows_path(&entry.command_line, candidate))
    }

    /// Game processes in `snapshot` that belong to `target`.
    pub(super) fn match_windows_game_processes(
        snapshot: &WindowsProcessSnapshot,
        target: &LifecycleProcessTarget,
        current_pid: u32,
    ) -> Vec<DetectedGameProcess> {
        let mut matches: Vec<DetectedGameProcess> = Vec::new();
        let launch_image_name = windows_file_name(&target.launch_executable);

        // Registry-recorded pids first, exactly as on Linux: an executable-path
        // match fails whenever the game was launched from another checkout.
        for pid in &target.recorded_pids {
            let pid = *pid;
            if pid == current_pid {
                continue;
            }
            let Some(entry) = snapshot.entries.iter().find(|entry| entry.pid == pid) else {
                continue;
            };
            if !windows_process_matches_instance_scope(entry, target.instance_user_dir.as_deref()) {
                continue;
            }
            if !recorded_windows_pid_looks_like_game(target, entry) {
                continue;
            }
            matches.push(detected_windows_game_process(entry, "recorded-pid"));
        }

        for entry in &snapshot.entries {
            if entry.pid == current_pid || matches.iter().any(|found| found.pid == entry.pid) {
                continue;
            }
            if !windows_process_matches_instance_scope(entry, target.instance_user_dir.as_deref()) {
                continue;
            }

            match entry.executable.as_deref() {
                Some(executable) => {
                    if windows_paths_equal(executable, &target.launch_executable) {
                        matches.push(detected_windows_game_process(entry, "exe"));
                    }
                }
                // Only rows whose full path is unknown fall back to the image name;
                // a known path that did not match belongs to a different install.
                None => {
                    if entry.image_name.as_ref().map(|name| name.to_lowercase()) == launch_image_name
                        && launch_image_name.is_some()
                    {
                        matches.push(detected_windows_game_process(entry, "image-name"));
                    }
                }
            }
        }

        matches
    }

    fn detected_windows_game_process(
        entry: &WindowsProcessEntry,
        match_kind: &'static str,
    ) -> DetectedGameProcess {
        DetectedGameProcess {
            pid: entry.pid,
            executable: entry.executable.clone(),
            argv0: entry.command_line.first().map(PathBuf::from),
            match_kind,
        }
    }

    /// Wrapper processes (`game.launchWrapper`) in `snapshot` that launched the
    /// target executable. Requires a command line, so the `tasklist` fallback never
    /// matches a wrapper.
    pub(super) fn match_windows_wrapper_processes(
        snapshot: &WindowsProcessSnapshot,
        target: &LifecycleWrapperTarget,
        current_pid: u32,
    ) -> Vec<DetectedWrapperProcess> {
        let mut matches = Vec::new();
        for entry in &snapshot.entries {
            if entry.pid == current_pid || entry.command_line.is_empty() {
                continue;
            }
            if !windows_wrapper_command_matches(&target.wrapper_command, entry) {
                continue;
            }
            if !command_line_mentions_windows_path(&entry.command_line, &target.launch_executable) {
                continue;
            }
            if let Some(user_dir) = target.instance_user_dir.as_deref()
                && !command_line_mentions_windows_path(&entry.command_line, user_dir)
            {
                continue;
            }

            matches.push(DetectedWrapperProcess {
                pid: entry.pid,
                executable: entry.executable.clone(),
                argv0: entry.command_line.first().map(PathBuf::from),
                command_line: entry.command_line.clone(),
                match_kind: "wrapper-cmdline",
            });
        }
        matches
    }

    fn windows_wrapper_command_matches(command: &str, entry: &WindowsProcessEntry) -> bool {
        let command_path = Path::new(command);
        if command_path.components().count() > 1 {
            if entry
                .executable
                .as_deref()
                .is_some_and(|executable| windows_paths_equal(executable, command_path))
            {
                return true;
            }
            return command_line_mentions_windows_path(&entry.command_line, command_path);
        }

        // A bare command name matches the image name, with or without `.exe`.
        let expected = command.to_lowercase();
        let matches_name = |name: &str| {
            let name = name.to_lowercase();
            name == expected || name.strip_suffix(".exe") == Some(expected.as_str())
        };
        entry.image_name.as_deref().is_some_and(matches_name)
            || entry
                .command_line
                .first()
                .and_then(|argv0| windows_file_name(Path::new(argv0)))
                .is_some_and(|name| matches_name(&name))
    }

    // ---------------------------------------------------------------------------
    // Host access (Windows only)
    // ---------------------------------------------------------------------------

    /// Read the host process table, preferring CIM and falling back to `tasklist`.
    #[cfg(windows)]
    pub(super) fn windows_process_snapshot(
        command_name: &str,
    ) -> Result<WindowsProcessSnapshot, AppError> {
        if let Some(entries) = cim_process_entries() {
            return Ok(WindowsProcessSnapshot {
                entries,
                source: "cim",
            });
        }

        let output = Command::new("tasklist")
            .args(["/FO", "CSV", "/NH"])
            .output()
            .map_err(|source| {
                lifecycle_error(
                    4,
                    "process_enumeration_failed",
                    command_name,
                    &format!(
                        "failed to enumerate processes: neither PowerShell CIM nor tasklist was usable ({source})"
                    ),
                )
            })?;
        if !output.status.success() {
            return Err(command_output_error(
                "process_enumeration_failed",
                command_name,
                &["tasklist".to_string(), "/FO".to_string(), "CSV".to_string()],
                &output,
            ));
        }

        Ok(WindowsProcessSnapshot {
            entries: parse_tasklist_csv(&String::from_utf8_lossy(&output.stdout)),
            source: "tasklist",
        })
    }

    #[cfg(windows)]
    fn cim_process_entries() -> Option<Vec<WindowsProcessEntry>> {
        const QUERY: &str = "Get-CimInstance Win32_Process | Select-Object ProcessId,Name,ExecutablePath,CommandLine | ConvertTo-Json -Compress";
        let output = Command::new("powershell")
            .args(["-NoProfile", "-NonInteractive", "-Command", QUERY])
            .output()
            .ok()?;
        if !output.status.success() {
            return None;
        }
        parse_cim_process_json(&String::from_utf8_lossy(&output.stdout))
            .filter(|entries| !entries.is_empty())
    }

    /// Each call reads the process table afresh, and the stop loops poll both
    /// this and the wrapper matcher per interval — so a wait costs roughly two
    /// PowerShell spawns per sample and will run slower than the requested
    /// interval. Timeouts are wall-clock, so waits still end on time; if that
    /// cost ever matters, a short-TTL snapshot cache is the fix.
    #[cfg(windows)]
    pub(super) fn matching_windows_processes(
        target: &LifecycleProcessTarget,
        command_name: &str,
    ) -> Result<Vec<DetectedGameProcess>, AppError> {
        let snapshot = windows_process_snapshot(command_name)?;
        Ok(match_windows_game_processes(
            &snapshot,
            target,
            std::process::id(),
        ))
    }

    #[cfg(windows)]
    pub(super) fn matching_windows_wrapper_processes(
        target: &LifecycleWrapperTarget,
        command_name: &str,
    ) -> Result<Vec<DetectedWrapperProcess>, AppError> {
        let snapshot = windows_process_snapshot(command_name)?;
        Ok(match_windows_wrapper_processes(
            &snapshot,
            target,
            std::process::id(),
        ))
    }

    /// Ask a process to close (`taskkill /T`), or force it (`/F`).
    ///
    /// Exit code 128 is "no such process", which for a stop flow means the work is
    /// already done rather than a failure.
    #[cfg(windows)]
    fn taskkill_pid(command_name: &str, pid: u32, force: bool) -> Result<(), AppError> {
        let error_code = if command_name == "game kill" {
            "force_kill_failed"
        } else {
            "configured_stop_command_failed"
        };
        let mut argv = vec!["taskkill".to_string()];
        if force {
            argv.push("/F".to_string());
        }
        argv.extend(["/PID".to_string(), pid.to_string(), "/T".to_string()]);

        let output = Command::new("taskkill")
            .args(&argv[1..])
            .output()
            .map_err(|source| {
                lifecycle_error(
                    4,
                    error_code,
                    command_name,
                    &format!("failed to invoke taskkill for pid {pid}: {source}"),
                )
            })?;

        if output.status.success() || output.status.code() == Some(128) {
            return Ok(());
        }
        // A process that exited between the scan and the kill is not an error.
        if String::from_utf8_lossy(&output.stderr).contains("not found") {
            return Ok(());
        }

        Err(command_output_error(
            error_code,
            command_name,
            &argv,
            &output,
        ))
    }

    #[cfg(windows)]
    pub(super) fn taskkill_processes(
        command_name: &str,
        processes: &[DetectedGameProcess],
        force: bool,
    ) -> Result<(), AppError> {
        for process in processes {
            taskkill_pid(command_name, process.pid, force)?;
        }
        Ok(())
    }

    #[cfg(windows)]
    pub(super) fn taskkill_wrapper_processes(
        command_name: &str,
        processes: &[DetectedWrapperProcess],
        force: bool,
    ) -> Result<(), AppError> {
        for pid in wrapper_process_pids(processes) {
            taskkill_pid(command_name, pid, force)?;
        }
        Ok(())
    }

    /// Best-effort liveness check for a recorded pid.
    #[cfg(windows)]
    pub(crate) fn windows_pid_is_live(pid: u32) -> bool {
        Command::new("tasklist")
            .args(["/FI", &format!("PID eq {pid}"), "/NH", "/FO", "CSV"])
            .output()
            .ok()
            .filter(|output| output.status.success())
            .is_some_and(|output| {
                parse_tasklist_csv(&String::from_utf8_lossy(&output.stdout))
                    .iter()
                    .any(|entry| entry.pid == pid)
            })
    }

    #[cfg(test)]
    mod windows_process_tests {
        use super::*;

        fn entry(pid: u32, executable: Option<&str>, image: &str, command_line: &[&str]) -> WindowsProcessEntry {
            WindowsProcessEntry {
                pid,
                executable: executable.map(PathBuf::from),
                image_name: Some(image.to_string()),
                command_line: command_line.iter().map(|arg| arg.to_string()).collect(),
            }
        }

        fn snapshot(entries: Vec<WindowsProcessEntry>, source: &'static str) -> WindowsProcessSnapshot {
            WindowsProcessSnapshot { entries, source }
        }

        fn target(launch_executable: &str) -> LifecycleProcessTarget {
            LifecycleProcessTarget {
                launch_executable: PathBuf::from(launch_executable),
                ..LifecycleProcessTarget::default()
            }
        }

        #[test]
        fn cim_json_parses_arrays_objects_and_missing_fields() {
            let raw = r#"[
                {"ProcessId":1234,"Name":"SlayTheSpire2.exe","ExecutablePath":"C:\\Games\\STS2\\SlayTheSpire2.exe","CommandLine":"\"C:\\Games\\STS2\\SlayTheSpire2.exe\" --user-dir C:\\inst\\a"},
                {"ProcessId":9,"Name":"System.exe","ExecutablePath":null,"CommandLine":null}
            ]"#;
            let entries = parse_cim_process_json(raw).expect("parsed");
            assert_eq!(entries.len(), 2);
            assert_eq!(
                entries[0].executable,
                Some(PathBuf::from(r"C:\Games\STS2\SlayTheSpire2.exe"))
            );
            assert_eq!(
                entries[0].command_line,
                vec![
                    r"C:\Games\STS2\SlayTheSpire2.exe".to_string(),
                    "--user-dir".to_string(),
                    r"C:\inst\a".to_string()
                ]
            );
            assert_eq!(entries[1].executable, None);
            assert_eq!(entries[1].image_name.as_deref(), Some("System.exe"));

            // A single row comes back as a bare object.
            let single = parse_cim_process_json(r#"{"ProcessId":7,"Name":"a.exe"}"#).expect("parsed");
            assert_eq!(single.len(), 1);
            assert_eq!(single[0].pid, 7);
        }

        #[test]
        fn tasklist_csv_parses_image_name_and_pid() {
            let raw = "\"SlayTheSpire2.exe\",\"4242\",\"Console\",\"1\",\"1,234,567 K\"\n\"svchost.exe\",\"9\",\"Services\",\"0\",\"12 K\"\n";
            let entries = parse_tasklist_csv(raw);
            assert_eq!(entries.len(), 2);
            assert_eq!(entries[0].pid, 4242);
            assert_eq!(entries[0].image_name.as_deref(), Some("SlayTheSpire2.exe"));
            assert_eq!(entries[0].executable, None);
        }

        #[test]
        fn command_line_split_follows_crt_quoting_rules() {
            assert_eq!(
                split_windows_command_line(r#""C:\Program Files\a b\game.exe" --flag "two words""#),
                vec![
                    r"C:\Program Files\a b\game.exe".to_string(),
                    "--flag".to_string(),
                    "two words".to_string()
                ]
            );
            // 2n backslashes collapse and toggle quoting; 2n+1 emit a literal quote.
            assert_eq!(
                split_windows_command_line(r#"a\\\"b c"#),
                vec![r#"a\"b"#.to_string(), "c".to_string()]
            );
            assert_eq!(
                split_windows_command_line(r"C:\dir\ next"),
                vec![r"C:\dir\".to_string(), "next".to_string()]
            );
            assert!(split_windows_command_line("   ").is_empty());
        }

        #[test]
        fn path_comparison_ignores_case_separators_and_verbatim_prefix() {
            assert!(windows_paths_equal(
                Path::new(r"\\?\C:\Games\STS2\SlayTheSpire2.exe"),
                Path::new(r"c:/games/sts2/SLAYTHESPIRE2.EXE")
            ));
            assert!(!windows_paths_equal(
                Path::new(r"C:\Games\A\game.exe"),
                Path::new(r"C:\Games\B\game.exe")
            ));
        }

        #[test]
        fn game_processes_match_on_canonicalized_executable_path() {
            // The target path carries the verbatim prefix `fs::canonicalize` adds;
            // CIM reports the plain path. They must still match.
            let target = target(r"\\?\C:\Games\STS2\SlayTheSpire2.exe");
            let snapshot = snapshot(
                vec![
                    entry(10, Some(r"C:\Games\STS2\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[]),
                    entry(11, Some(r"C:\Other\STS2\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[]),
                ],
                "cim",
            );

            let matched = match_windows_game_processes(&snapshot, &target, 1);
            assert_eq!(matched.len(), 1);
            assert_eq!(matched[0].pid, 10);
            assert_eq!(matched[0].match_kind, "exe");
        }

        #[test]
        fn image_name_matching_only_applies_to_rows_without_a_known_path() {
            let target = target(r"C:\Games\STS2\SlayTheSpire2.exe");
            let degraded = snapshot(
                vec![entry(20, None, "SlayTheSpire2.exe", &[])],
                "tasklist",
            );
            let matched = match_windows_game_processes(&degraded, &target, 1);
            assert_eq!(matched.len(), 1);
            assert_eq!(matched[0].match_kind, "image-name");
            assert!(degraded.degraded());

            // A row from another install has a known, different path: no match.
            let precise = snapshot(
                vec![entry(21, Some(r"C:\Other\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[])],
                "cim",
            );
            assert!(match_windows_game_processes(&precise, &target, 1).is_empty());
        }

        #[test]
        fn recorded_pids_match_across_a_different_install_path() {
            let mut target = target(r"C:\Games\STS2\SlayTheSpire2.exe");
            target.recorded_pids = vec![30];
            target.recorded_launch_executable = Some(PathBuf::from(r"D:\build\SlayTheSpire2.exe"));
            let snapshot = snapshot(
                vec![entry(30, Some(r"D:\build\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[])],
                "cim",
            );

            let matched = match_windows_game_processes(&snapshot, &target, 1);
            assert_eq!(matched.len(), 1);
            assert_eq!(matched[0].match_kind, "recorded-pid");
        }

        #[test]
        fn instance_scope_requires_the_user_dir_on_the_command_line() {
            let mut target = target(r"C:\Games\STS2\SlayTheSpire2.exe");
            target.instance_user_dir = Some(PathBuf::from(r"C:\inst\alpha"));
            let snapshot = snapshot(
                vec![
                    entry(
                        40,
                        Some(r"C:\Games\STS2\SlayTheSpire2.exe"),
                        "SlayTheSpire2.exe",
                        &[r"C:\Games\STS2\SlayTheSpire2.exe", "--user-dir", r"C:\inst\alpha"],
                    ),
                    entry(
                        41,
                        Some(r"C:\Games\STS2\SlayTheSpire2.exe"),
                        "SlayTheSpire2.exe",
                        &[r"C:\Games\STS2\SlayTheSpire2.exe", "--user-dir", r"C:\inst\beta"],
                    ),
                    // No command line: cannot prove instance membership.
                    entry(42, Some(r"C:\Games\STS2\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[]),
                ],
                "cim",
            );

            let matched = match_windows_game_processes(&snapshot, &target, 1);
            assert_eq!(
                matched.iter().map(|process| process.pid).collect::<Vec<_>>(),
                vec![40]
            );
        }

        #[test]
        fn wrapper_matching_needs_both_the_wrapper_and_the_launch_executable() {
            let wrapper_target = LifecycleWrapperTarget {
                wrapper_command: "steam".to_string(),
                launch_executable: PathBuf::from(r"C:\Games\STS2\SlayTheSpire2.exe"),
                instance_user_dir: None,
            };
            let snapshot = snapshot(
                vec![
                    entry(
                        50,
                        Some(r"C:\Steam\steam.exe"),
                        "steam.exe",
                        &[r"C:\Steam\steam.exe", r"C:\Games\STS2\SlayTheSpire2.exe"],
                    ),
                    // Right wrapper, different game.
                    entry(
                        51,
                        Some(r"C:\Steam\steam.exe"),
                        "steam.exe",
                        &[r"C:\Steam\steam.exe", r"C:\Games\Other\other.exe"],
                    ),
                    // The game itself is not its own wrapper.
                    entry(
                        52,
                        Some(r"C:\Games\STS2\SlayTheSpire2.exe"),
                        "SlayTheSpire2.exe",
                        &[r"C:\Games\STS2\SlayTheSpire2.exe"],
                    ),
                ],
                "cim",
            );

            let matched = match_windows_wrapper_processes(&snapshot, &wrapper_target, 1);
            assert_eq!(
                matched.iter().map(|process| process.pid).collect::<Vec<_>>(),
                vec![50]
            );
        }

        #[test]
        fn the_current_process_is_never_matched() {
            let target = target(r"C:\Games\STS2\SlayTheSpire2.exe");
            let snapshot = snapshot(
                vec![entry(60, Some(r"C:\Games\STS2\SlayTheSpire2.exe"), "SlayTheSpire2.exe", &[])],
                "cim",
            );
            assert!(match_windows_game_processes(&snapshot, &target, 60).is_empty());
        }
    }
}
