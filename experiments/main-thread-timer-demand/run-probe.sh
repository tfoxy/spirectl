#!/usr/bin/env bash
set -euo pipefail

fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "$fixture_dir/../.." && pwd)"
scratch_dir="${SPI_TIMER_PROBE_SCRATCH_DIR:-$(mktemp -d /tmp/spirectl-timer-demand-XXXXXX)}"
godot_bin="${SPI_GODOT_BIN:?set SPI_GODOT_BIN to a Godot 4.5.1 Mono executable}"
pump_source="${SPI_TIMER_PUMP_SOURCE:-$repo_dir/bridge-mod/src/Spirectl.Sts2/Live/Sts2GodotSynchronizationContext.cs}"
dispatcher_source="${SPI_TIMER_DISPATCHER_SOURCE:-$repo_dir/bridge-mod/src/Spirectl.Sts2/Live/Sts2MainThreadDispatcher.cs}"

"$godot_bin" --version | grep -q '^4\.5\.1\.stable\.mono\.'
test -f "$pump_source"
test -f "$dispatcher_source"
mkdir -p "$scratch_dir"
cp "$fixture_dir"/{Probe.cs,LogStub.cs,project.godot,main.tscn,TimerDemandProbe.csproj} "$scratch_dir/"
printf 'fixture_dir=%s\nscratch_dir=%s\ngodot=%s\n' "$fixture_dir" "$scratch_dir" "$godot_bin" | tee "$scratch_dir/inputs.txt"
sha256sum "$scratch_dir"/{Probe.cs,LogStub.cs,project.godot,main.tscn,TimerDemandProbe.csproj} \
  "$pump_source" "$dispatcher_source" | tee "$scratch_dir/source-sha256.txt"

flock -x /tmp/sts2-dotnet-build.lock dotnet build "$scratch_dir/TimerDemandProbe.csproj" -v:q \
  -p:SpiPumpSource="$pump_source" -p:SpiDispatcherSource="$dispatcher_source" \
  2>&1 | tee "$scratch_dir/build.log"
"$godot_bin" --headless --editor --path "$scratch_dir" --quit >"$scratch_dir/import.log" 2>&1
"$godot_bin" --headless --path "$scratch_dir" --quit-after 240 2>&1 | tee "$scratch_dir/run.log"
grep -q '^FIXTURE_RESULT PASS source-linked overdue, stop, reacquire, redundant demand$' "$scratch_dir/run.log"
