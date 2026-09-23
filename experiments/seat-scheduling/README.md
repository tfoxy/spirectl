# Seat scheduling experiments

These standalone probes are disabled by default and are not referenced by a shipped project. They change
only their own process, restore their requests before exit, and report native interop separately from game
performance. Running a probe beside a game does **not** change the game's scheduling.

```sh
dotnet run --project experiments/seat-scheduling -- --self-test
dotnet run --project experiments/seat-scheduling
# Windows, separately so each effect can be measured:
dotnet run --project experiments/seat-scheduling -- --enable-native-experiment --high-qos
dotnet run --project experiments/seat-scheduling -- --enable-native-experiment --honor-timer-resolution
# macOS:
dotnet run --project experiments/seat-scheduling -- --enable-native-experiment --activity
```

The project targets .NET 9. If only a newer runtime is installed, explicitly set `DOTNET_ROLL_FORWARD=Major`
when running the smoke checks. `--duration-ms` holds the request for 0–60000 ms (default 1000). The default
invocation makes no native scheduling calls. Unsupported platforms fail an explicitly enabled probe.

Windows HighQoS explicitly disables execution-speed throttling. The timer experiment independently disables
`IGNORE_TIMER_RESOLUTION`, honoring requests already made by the process; it makes no timer-resolution request
itself. Both preserve unrelated mask bits and restore the complete original configuration. The read-back masks
show configured policy, not effective QoS, core selection, wake-up delay, or timer delivery. Those require
Windows scheduler/ETW evidence in the actual seat process. Upstream Godot 4.5 already requests timer
resolution during Windows OS initialization; the probe does not duplicate that request.

The macOS experiment holds `NSActivityUserInitiatedAllowingIdleSystemSleep`, then ends its activity and releases
its token. It does not request latency-critical execution or prevent idle display/system sleep. A successful
call proves interop; Activity Monitor or Instruments and actual input workloads must establish App Nap behavior.

Linux has no corresponding policy change here. Record each game/seat's scheduler policy, nice value,
autogroup, cgroup, CPU time, and context switches before deciding whether it is being disadvantaged. A headless
process is not by itself evidence of reduced scheduling priority. Engine idle/no-draw behavior is a separate
cause and is not altered by these probes. Upstream Godot 4.5 applies its dynamic frame delay when a window
cannot draw even when low-processor mode is off; observe the actual engine settings separately.

Adoption requires native-platform, repeated interleaved baseline/candidate game runs with active, idle,
detached, reconnected, and first-after-idle input workloads. Missing native rigs or inconclusive latency evidence
keeps the corresponding experiment disabled. These probes make no high/realtime priority, affinity, global
registry/power, input-path, or game-sleep changes.

Sources:

- [Windows QoS](https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service)
- [Process power throttling and timer resolution](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
- [App Nap](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/AppNap.html)
- [Process activity options](https://developer.apple.com/documentation/foundation/processinfo/activityoptions/userinitiatedallowingidlesystemsleep)

- [Godot Windows timer initialization](https://github.com/godotengine/godot/blob/4.5-stable/platform/windows/os_windows.cpp#L237)
- [Godot dynamic frame delay](https://github.com/godotengine/godot/blob/4.5-stable/core/os/os.cpp#L649)
