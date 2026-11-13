use super::sync::{required_str, transfer_id, validate_stage};
use super::{Remote, error, ps_literal};
use crate::{GameRemoteLaunchArgs, GameRemoteStopArgs};
use serde_json::{Value, json};

impl Remote<'_> {
    pub(super) fn launch(&self, args: GameRemoteLaunchArgs) -> Result<Value, crate::AppError> {
        let size = args.windowed.as_deref().map(window_size).transpose()?;
        let status = self.status()?;
        if status["processes"]
            .as_array()
            .is_some_and(|p| !p.is_empty())
        {
            return Err(error(
                "remote_game_running",
                "stop the remote game before launching",
            ));
        }
        validate_stage(required_str(&status, "stageDir")?)?;
        let token = transfer_id();
        let parameters = format!(
            "$taskName = {}\n$isolatedProfile = {}\n",
            ps_literal(&format!("spirectl-launch-{token}")),
            ps_literal(args.isolated_profile.as_deref().unwrap_or("")),
        );
        let prepared = self.run("prepare-launch", &format!("{parameters}{PREPARE}"))?;
        let mut patches = Vec::new();
        for file in prepared["settingsFiles"]
            .as_array()
            .ok_or_else(|| error("remote_invalid_response", "missing launch settingsFiles"))?
        {
            let text = required_str(file, "text")?;
            let (updated, _, _) = super::install::enabled_settings(text, &[])?;
            if updated.is_none() && file["sha256"].is_string() {
                continue;
            }
            patches.push(json!({"path":required_str(file,"path")?,"text":updated.as_deref().unwrap_or(text),"sha256":file["sha256"]}));
        }
        let mut game_args = args.game_args;
        game_args.push("--windowed".into());
        if let Some(size) = size {
            game_args.extend(["--resolution".into(), size]);
        }
        let arguments = game_args
            .iter()
            .map(|a| windows_argument(a))
            .collect::<Vec<_>>()
            .join(" ");
        let body = format!(
            "{parameters}$waitMs = {}\n$gameArguments = {}\n$patches = ConvertFrom-Json {}\n{LAUNCH}",
            args.wait_ms,
            ps_literal(&arguments),
            ps_literal(&serde_json::to_string(&patches).expect("settings serialize")),
        );
        let launched = self.run("launch", &body);
        // A second encoded command also covers a transport failure during task creation/waiting.
        let cleanup = self.run("launch-cleanup", &format!("{parameters}{CLEANUP}"));
        let mut result = match (launched, cleanup) {
            (Ok(result), Ok(_)) => result,
            (Err(mut failure), cleanup) => {
                if let Err(cleanup) = cleanup {
                    failure.payload["error"]["cleanupError"] = cleanup.payload["error"].clone();
                }
                return Err(failure);
            }
            (Ok(_), Err(failure)) => return Err(failure),
        };
        result["host"] = json!(self.host);
        result["task"] = json!({"name":format!("spirectl-launch-{token}"),"deleted":true});
        result["tunnelCommand"] = tunnel_command(self.host, &result["endpoint"]);
        Ok(result)
    }

    pub(super) fn stop(&self, args: GameRemoteStopArgs) -> Result<Value, crate::AppError> {
        let mut result = self.run(
            "stop",
            &format!(
                "$force = {}\n$timeoutMs = {}\n{STOP}",
                if args.force { "$true" } else { "$false" },
                args.timeout_ms,
            ),
        )?;
        result["host"] = json!(self.host);
        Ok(result)
    }
}

// ProcessStartInfo.Arguments uses Windows command-line quoting, independently of PowerShell syntax.
fn windows_argument(value: &str) -> String {
    let mut quoted = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' {
            slashes += 1;
            continue;
        }
        quoted.extend(std::iter::repeat_n(
            '\\',
            if c == '"' { slashes * 2 + 1 } else { slashes },
        ));
        quoted.push(c);
        slashes = 0;
    }
    quoted.extend(std::iter::repeat_n('\\', slashes * 2));
    quoted.push('"');
    quoted
}

fn window_size(size: &str) -> Result<String, crate::AppError> {
    let valid = size.split_once('x').is_some_and(|(w, h)| {
        [w, h].iter().all(|v| {
            !v.is_empty()
                && v.bytes().all(|b| b.is_ascii_digit())
                && v.parse::<u16>().is_ok_and(|n| n > 0)
        })
    });
    if !valid {
        return Err(error(
            "remote_window_invalid",
            "--windowed must be a positive WxH size, for example 1280x720",
        ));
    }
    Ok(size.into())
}

fn tunnel_command(host: &str, endpoint: &Value) -> Value {
    if endpoint["kind"] != "tcp" && endpoint["transportKind"] != "tcp" {
        return Value::Null;
    }
    let Some(address) = endpoint["address"].as_str() else {
        return Value::Null;
    };
    let Ok(address) = address.parse::<std::net::SocketAddr>() else {
        return Value::Null;
    };
    if !address.ip().is_loopback() {
        return Value::Null;
    }
    let destination = if address.is_ipv6() {
        format!("[{}]", address.ip())
    } else {
        address.ip().to_string()
    };
    json!(format!(
        "ssh -N -L 127.0.0.1:{}:{destination}:{} {host}",
        address.port(),
        address.port()
    ))
}

const PREPARE: &str = r#"
if (@(Game-Processes).Count) { Fail 'remote_game_running' 'Stop the remote game before launching.' }
$files = @()
if ($isolatedProfile) {
    $isolatedProfile = [Environment]::ExpandEnvironmentVariables($isolatedProfile)
    if ($isolatedProfile -notmatch '^[A-Za-z]:[\\/]' -and $isolatedProfile -notmatch '^\\\\[^\\]+\\[^\\]+') { Fail 'remote_profile_invalid' 'Use an absolute isolated profile path.' }
    $isolatedProfile = [IO.Path]::GetFullPath($isolatedProfile)
    $targetRoot = Join-Path $isolatedProfile 'Roaming\SlayTheSpire2'
    $sourceRoot = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'SlayTheSpire2'))
    if ([IO.Path]::GetFullPath($targetRoot).TrimEnd('\') -eq [IO.Path]::GetFullPath($sourceRoot).TrimEnd('\')) {
        Fail 'remote_profile_invalid' 'Isolated profile must differ from the real profile.'
    }
    $relative = @('default\1\settings.save')
    $steam = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue
    if ($steam.ActiveUser) { $relative += ('steam\' + ([uint64]76561197960265728 + [uint64]$steam.ActiveUser) + '\settings.save') }
    foreach ($root in @($sourceRoot,$targetRoot)) {
        if (Test-Path -LiteralPath $root) {
            $relative += @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'settings.save' -File | ForEach-Object { $_.FullName.Substring($root.TrimEnd('\').Length + 1) })
        }
    }
    foreach ($name in @($relative | Sort-Object -Unique)) {
        $target = Join-Path $targetRoot $name
        $source = Join-Path $sourceRoot $name
        $text = '{}'
        $hash = $null
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $text = [IO.File]::ReadAllText($target)
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        } elseif (Test-Path -LiteralPath $source -PathType Leaf) { $text = [IO.File]::ReadAllText($source) }
        $files += @{path=$target;text=$text;sha256=$hash}
    }
}
@{settingsFiles=@($files)} | ConvertTo-Json -Compress -Depth 20
"#;

const LAUNCH: &str = r#"
Assert-Stage
$launcher = Join-Path $stageDir ($taskName + '.ps1')
try {
    if (@(Game-Processes).Count) { Fail 'remote_game_running' 'Stop the remote game before launching.' }
    foreach ($patch in $patches) {
        if ($patch.sha256) {
            if (!(Test-Path -LiteralPath $patch.path) -or (Get-FileHash -LiteralPath $patch.path -Algorithm SHA256).Hash -ne $patch.sha256) {
                Fail 'remote_settings_changed' 'Isolated settings changed; retry launch.'
            }
        } elseif (Test-Path -LiteralPath $patch.path) { Fail 'remote_settings_changed' 'Isolated settings appeared; retry launch.' }
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($patch in $patches) {
        New-Item -ItemType Directory -Path (Split-Path $patch.path -Parent) -Force | Out-Null
        $temp = $patch.path + '.spirectl-new-' + $taskName
        try {
            [IO.File]::WriteAllText($temp, $patch.text, $utf8)
            if ($patch.sha256) {
                [IO.File]::Replace($temp, $patch.path, ($patch.path + '.spirectl-backup-' + $taskName))
            } else { [IO.File]::Move($temp, $patch.path) }
        } finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
        }
    }
    function Quote($text) { "'" + $text.Replace("'", "''") + "'" }
    $lines = @('$ErrorActionPreference = ''Stop''')
    if ($isolatedProfile) {
        $isolatedProfile = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($isolatedProfile))
        $roaming = Join-Path $isolatedProfile 'Roaming'
        $local = Join-Path $isolatedProfile 'Local'
        New-Item -ItemType Directory -Path $roaming,$local -Force | Out-Null
        $lines += ('$env:APPDATA = ' + (Quote $roaming))
        $lines += ('$env:LOCALAPPDATA = ' + (Quote $local))
    }
    $lines += ('$start = New-Object Diagnostics.ProcessStartInfo')
    $lines += ('$start.FileName = ' + (Quote (Join-Path $gamePath 'SlayTheSpire2.exe')))
    $lines += ('$start.WorkingDirectory = ' + (Quote $gamePath))
    $lines += ('$start.Arguments = ' + (Quote $gameArguments))
    $lines += ('$start.UseShellExecute = $false')
    $lines += ('[Diagnostics.Process]::Start($start) | Out-Null')
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    [IO.File]::WriteAllText($launcher, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $launcher + '"') -WorkingDirectory $gamePath
    $principal = New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME + '\' + $env:USERNAME) -LogonType Interactive
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = $null
    do {
        $process = @(Game-Processes | Where-Object { $_.executablePath -eq (Join-Path $gamePath 'SlayTheSpire2.exe') }) | Select-Object -First 1
        if ($process) { break }
        Start-Sleep -Milliseconds 100
    } while ($clock.ElapsedMilliseconds -lt $waitMs)
    if (!$process) { Fail 'remote_launch_timeout' 'No game process appeared before --wait-ms; ensure this Windows user is logged into the desktop.' }
    $result = @{pid=$process.pid;gamePath=$gamePath;isolatedProfile=$isolatedProfile;endpoint=(Read-Json (Join-Path $gamePath 'mods\spirectlbridge\spirectl-bridge.endpoint.json'))}
} finally {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    if (Test-Path -LiteralPath $launcher) { Remove-Item -LiteralPath $launcher -Force }
}
$result | ConvertTo-Json -Compress -Depth 20
"#;

const CLEANUP: &str = r#"
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
$launcher = Join-Path $stageDir ($taskName + '.ps1')
if (Test-Path -LiteralPath $launcher) { Remove-Item -LiteralPath $launcher -Force }
@{deleted=$true} | ConvertTo-Json -Compress
"#;

const STOP: &str = r#"
$before = @(Game-Processes)
$deletedTasks = @()
try {
    if (!$force) {
        foreach ($entry in $before) {
            $process = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
            if ($process) { $process.CloseMainWindow() | Out-Null }
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (@(Game-Processes).Count -and $clock.ElapsedMilliseconds -lt $timeoutMs) { Start-Sleep -Milliseconds 100 }
    }
    foreach ($entry in @(Game-Processes)) { Stop-Process -Id $entry.pid -Force -ErrorAction SilentlyContinue }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (@(Game-Processes).Count -and $clock.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 100 }
    if (@(Game-Processes).Count) { Fail 'remote_stop_timeout' 'Game processes remain after termination.' }
} finally {
    foreach ($task in @(Get-ScheduledTask -TaskName 'spirectl-launch-*' -ErrorAction SilentlyContinue)) {
        Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false
        $deletedTasks += $task.TaskName
    }
}
@{stoppedPids=@($before | ForEach-Object { $_.pid });deletedTasks=@($deletedTasks);forced=$force} | ConvertTo-Json -Compress -Depth 20
"#;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn remote_windows_arguments_preserve_empty_quotes_and_backslashes() {
        for (input, expected) in [
            ("", r#""""#),
            ("two words", r#""two words""#),
            ("a\"b", r#""a\"b""#),
            ("C:\\path\\", r#""C:\path\\""#),
            ("a\\\"b", r#""a\\\"b""#),
        ] {
            assert_eq!(windows_argument(input), expected);
        }
    }

    #[test]
    fn remote_window_size_rejects_invalid_values() {
        assert_eq!(window_size("1280x720").unwrap(), "1280x720");
        for value in [
            "0x720", "1280x0", "-1x2", "1x2;echo", "1x2x3", "65536x1", "1X2",
        ] {
            assert!(window_size(value).is_err(), "{value}");
        }
    }
    #[test]
    fn remote_tunnel_reports_only_forwardable_tcp_endpoints() {
        assert_eq!(
            tunnel_command("win", &json!({"kind":"tcp","address":"127.0.0.1:51173"})),
            "ssh -N -L 127.0.0.1:51173:127.0.0.1:51173 win"
        );
        assert_eq!(
            tunnel_command("win", &json!({"kind":"tcp","address":"[::1]:51234"})),
            "ssh -N -L 127.0.0.1:51234:[::1]:51234 win"
        );
        assert_eq!(
            tunnel_command("win", &json!({"kind":"tcp","address":"127.0.0.2:51234"})),
            "ssh -N -L 127.0.0.1:51234:127.0.0.2:51234 win"
        );
        for endpoint in [
            json!(null),
            json!({"kind":"named-pipe","name":"sts2"}),
            json!({"kind":"tcp","address":"0.0.0.0:5"}),
        ] {
            assert!(tunnel_command("win", &endpoint).is_null());
        }
    }
}
