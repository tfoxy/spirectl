//! Windows lifecycle transport. Only encoded PowerShell reaches the remote command shell.
use crate::{AppContext, AppError, GameRemoteCommand, GameRemoteSubcommand};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::{
    io::Write,
    process::{Command, Stdio},
};

mod install;
mod lifecycle;
mod sync;

fn ps_literal(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn encoded_command(script: &str) -> String {
    STANDARD.encode(
        script
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>(),
    )
}

fn error(code: &str, message: impl ToString) -> AppError {
    AppError {
        exit_code: 4,
        payload: json!({"error": {"code": code, "message": message.to_string()}}),
    }
}

struct Remote<'a> {
    host: &'a str,
    config: &'a crate::config::RemoteConfig,
}

impl<'a> Remote<'a> {
    fn new(host: Option<&'a str>, context: AppContext<'a>) -> Result<Self, AppError> {
        let host = host
            .or(context.config.remote.host.as_deref())
            .ok_or_else(|| {
                error(
                    "remote_host_required",
                    "pass --host or configure remote.host",
                )
            })?;
        // Hosts are SSH aliases, never options or scp path expressions.
        if host.is_empty()
            || host == "."
            || host == ".."
            || host.starts_with('-')
            || !host
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || "._-@".contains(c))
        {
            return Err(error("remote_host_invalid", "host must be an SSH alias"));
        }
        Ok(Self {
            host,
            config: &context.config.remote,
        })
    }

    fn run(&self, operation: &str, body: &str) -> Result<Value, AppError> {
        let script = format!(
            "# spirectl:{operation}\n$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\ntry {{\n$gamePath = {}\n$stageDir = {}\n{COMMON}\n{body}\n}} catch {{\n@{{error=@{{code='remote_command_failed';message=$_.Exception.Message}}}} | ConvertTo-Json -Compress -Depth 20\nexit 1\n}}",
            ps_literal(self.config.game_path.as_deref().unwrap_or("")),
            ps_literal(self.config.stage_dir.as_deref().unwrap_or("")),
        );
        // Keep cmd.exe's command line short even for large profile settings payloads.
        let mut child = Command::new(&self.config.ssh_command)
            .args([
                "-o",
                "BatchMode=yes",
                self.host,
                "powershell",
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                &encoded_command(BOOTSTRAP),
            ])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|e| error("remote_unreachable", e))?;
        let sent = child
            .stdin
            .take()
            .expect("piped stdin")
            .write_all(encoded_command(&script).as_bytes());
        let output = child
            .wait_with_output()
            .map_err(|e| error("remote_unreachable", e))?;
        let parsed = serde_json::from_slice::<Value>(&output.stdout);
        if output.status.code() == Some(255) {
            return Err(self.command_error("remote_unreachable", &output));
        }
        if let Ok(value) = &parsed
            && value.get("error").is_some_and(Value::is_object)
        {
            let mut failure = error("remote_command_failed", "remote operation failed");
            failure.payload = value.clone();
            failure.payload["error"]["host"] = json!(self.host);
            failure.payload["error"]["exitCode"] = json!(output.status.code());
            failure.payload["error"]["stderrTail"] = json!(stderr_tail(&output.stderr));
            return Err(failure);
        }
        if !output.status.success() {
            return Err(self.command_error("remote_command_failed", &output));
        }
        sent.map_err(|e| error("remote_unreachable", e))?;
        let value = parsed.map_err(|_| self.command_error("remote_invalid_response", &output))?;
        if !value.is_object() {
            return Err(self.command_error("remote_invalid_response", &output));
        }
        if value.get("error").is_some() {
            return Err(self.command_error("remote_invalid_response", &output));
        }
        Ok(value)
    }

    fn command_error(&self, code: &str, output: &std::process::Output) -> AppError {
        let mut failure = error(code, "remote operation failed");
        failure.payload["error"]["host"] = json!(self.host);
        failure.payload["error"]["exitCode"] = json!(output.status.code());
        failure.payload["error"]["stderrTail"] = json!(stderr_tail(&output.stderr));
        failure
    }

    fn status(&self) -> Result<Value, AppError> {
        let mut status = self.run("status", STATUS)?;
        status["host"] = json!(self.host);
        let version = status
            .pointer("/releaseInfo/version")
            .and_then(Value::as_str)
            .unwrap_or("");
        status["apiLane"] = json!(crate::game_build::api_lane_for_game_version(version));
        Ok(status)
    }
}

const BOOTSTRAP: &str = "& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd()))))";

fn stderr_tail(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes)
        .chars()
        .rev()
        .take(4096)
        .collect::<String>()
        .chars()
        .rev()
        .collect()
}

pub(crate) fn execute(args: GameRemoteCommand, context: AppContext<'_>) -> Result<Value, AppError> {
    let remote = Remote::new(args.host.as_deref(), context)?;
    match args.command {
        GameRemoteSubcommand::Status => remote.status(),
        GameRemoteSubcommand::SyncAssemblies(args) => {
            let status = remote.status()?;
            remote.sync_assemblies(&status, args.refresh)
        }
        GameRemoteSubcommand::InstallBridge(args) => remote.install_bridge(args, context),
        GameRemoteSubcommand::Launch(args) => remote.launch(args),
        GameRemoteSubcommand::Stop(args) => remote.stop(args),
    }
}

const COMMON: &str = r#"
function Fail($code, $message) {
    @{error=@{code=$code;message=$message}} | ConvertTo-Json -Compress
    exit 1
}
function Read-Json($path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    return $null
}
if (!$gamePath) {
    $roots = @()
    $steam = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if ($steam.SteamPath) { $roots += $steam.SteamPath }
    $roots += (Join-Path ${env:ProgramFiles(x86)} 'Steam')
    foreach ($root in @($roots)) {
        $libraries = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraries) {
            foreach ($line in Get-Content -LiteralPath $libraries) {
                if ($line -match '"path"\s+"([^"]+)"') { $roots += $Matches[1].Replace('\\','\') }
            }
        }
    }
    foreach ($root in $roots) {
        $candidate = Join-Path $root 'steamapps\common\Slay the Spire 2'
        if (Test-Path -LiteralPath (Join-Path $candidate 'SlayTheSpire2.exe')) { $gamePath = $candidate; break }
    }
}
if (!$gamePath -or !(Test-Path -LiteralPath (Join-Path $gamePath 'SlayTheSpire2.exe') -PathType Leaf)) {
    Fail 'remote_game_not_found' 'Configure remote.gamePath with the Windows game directory.'
}
$gamePath = (Get-Item -LiteralPath $gamePath).FullName
if (!$stageDir) { $stageDir = Join-Path $env:LOCALAPPDATA 'spirectl\stage' }
$stageDir = [Environment]::ExpandEnvironmentVariables($stageDir)
function Assert-Stage {
    if ($stageDir -notmatch '^[A-Za-z]:[\\/][A-Za-z0-9_./\\-]+$' -or $stageDir -match '(^|[\\/])\.{1,2}([\\/]|$)') {
        Fail 'remote_stage_invalid' 'Use an absolute Windows drive path without spaces or shell metacharacters for remote.stageDir.'
    }
}
function Game-Processes {
    @(Get-CimInstance Win32_Process -Filter "Name = 'SlayTheSpire2.exe'" | ForEach-Object {
        @{pid=$_.ProcessId;executablePath=$_.ExecutablePath}
    })
}
"#;

const STATUS: &str = r#"
$modDir = Join-Path $gamePath 'mods\spirectlbridge'
$endpointPath = Join-Path $modDir 'spirectl-bridge.endpoint.json'
$manifest = Read-Json (Join-Path $modDir 'spirectlbridge.json')
@{
    gamePath=$gamePath
    stageDir=$stageDir
    releaseInfo=(Read-Json (Join-Path $gamePath 'release_info.json'))
    bridge=@{present=(Test-Path -LiteralPath $modDir -PathType Container);endpointFile=$endpointPath;endpoint=(Read-Json $endpointPath);buildIdentity=$manifest.buildIdentity}
    processes=@(Game-Processes)
    launchTasks=@(Get-ScheduledTask -TaskName 'spirectl-launch-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.TaskName })
} | ConvertTo-Json -Compress -Depth 20
"#;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn remote_ps_literals_are_single_quoted_and_escape_apostrophes() {
        assert_eq!(ps_literal("C:\\O'Brien\\$x; hi"), "'C:\\O''Brien\\$x; hi'");
        assert_eq!(ps_literal(""), "''");
    }

    #[test]
    fn remote_encoded_command_is_base64_utf16le_including_surrogates() {
        assert_eq!(encoded_command("A€😀"), "QQCsID3YAN4=");
        let script = "Write-Output 'hello'";
        let bytes = STANDARD.decode(encoded_command(script)).unwrap();
        let words = bytes
            .chunks_exact(2)
            .map(|b| u16::from_le_bytes([b[0], b[1]]))
            .collect::<Vec<_>>();
        assert_eq!(String::from_utf16(&words).unwrap(), script);
    }
}
